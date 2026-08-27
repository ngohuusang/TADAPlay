using log4net.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using TadaPlay.Common.Models;
using TadaPlay.Contexts.Interfaces;
using TadaPlay.Exceptions;
using TadaPlay.Logger;
using TadaPlay.Services.Interface;

namespace TadaPlay.Services;

public class AccountService : IAccountService
{
    private readonly static string BASE_URL = "https://openvpn.aoe2.io.vn/api.php";

    private readonly HttpClient _httpClient;
    private readonly IAppContext _appContext;

    /// <summary>
    /// This client's version, sent on EVERY request so the server can gate old builds.
    ///
    /// The server rejects a login whose version it cannot read with "Phiên bản ... không xác
    /// định" - and the client used to send its version only on upload_record, never on
    /// login/auth, so a perfectly current build was told to update. It is sent three ways so
    /// it matches whatever the server reads: an X-Client-Version header (covers header-based
    /// checks and the body-less GET auth call), the User-Agent, and a client_version field in
    /// the login body. The "+&lt;git-sha&gt;" suffix ProductVersion can carry is stripped so a
    /// plain "3.27.0" compares cleanly against the server's minimum.
    /// </summary>
    private static readonly string ClientVersion = GetClientVersion();

    private static string GetClientVersion()
    {
        string v;
        try
        {
            // Read the version straight off this assembly rather than trusting
            // Application.ProductVersion, so it can never come back empty at runtime and send
            // the server a blank version (which it reads as "không xác định" and rejects).
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            v = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(v)) v = asm.GetName().Version?.ToString();
            if (string.IsNullOrWhiteSpace(v)) v = System.Windows.Forms.Application.ProductVersion;
        }
        catch { v = System.Windows.Forms.Application.ProductVersion; }

        v = (v ?? "").Trim();
        int plus = v.IndexOf('+');   // drop the "+<git-sha>" suffix -> plain "3.27.0"
        if (plus >= 0) v = v.Substring(0, plus);
        return v;
    }

    public AccountService(IAppContext appContext)
    {
        System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
        _httpClient = new HttpClient();
        // HttpClient.Timeout is a hard ceiling for every request on this client - a longer
        // per-call CancellationToken (like upload_record's 5-minute one below) can't override
        // it, only make things worse. Set it to match the longest legitimate call (record
        // upload over a possibly slow VPN link) rather than the old 10s, which was aborting
        // uploads that hadn't even finished sending yet.
        _httpClient.Timeout = TimeSpan.FromMinutes(5);

        // Advertise the client version on every request so the server's version gate sees it
        // (including on login and the GET auto-login/auth call, which carry no body).
        try
        {
            if (!string.IsNullOrEmpty(ClientVersion))
            {
                _httpClient.DefaultRequestHeaders.Add("X-Client-Version", ClientVersion);
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"TadaPlay/{ClientVersion}");
            }
        }
        catch (Exception ex) { DebugLogger.Warn("AccountService: could not set version headers: " + ex.Message); }

        _appContext = appContext;
    }

    public async Task<List<User>> GetAllAsync()
    {
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _appContext.GetJwtTokenSetting());
        var response = await _httpClient.GetAsync(BASE_URL + "?action=users");

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            var apiResponse = JsonConvert.DeserializeObject<ApiResponse>(responseBody);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new LoginException("Lỗi đăng nhập: " + apiResponse.Message);
            }
            else
            {
                throw new Exception(apiResponse.Message);
            }
        }

        var jsonString = await response.Content.ReadAsStringAsync();
        var json = JObject.Parse(jsonString);

        if (json["success"]?.Value<bool>() == true)
        {
            var users = json["users"].ToObject<List<User>>();
            return users;
        }

        return new List<User>();
    }

    public async Task<bool> DoAuthAsync()
    {
        if (!_appContext.GetAutoLoginSetting())
        {
            return false;
        }

        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _appContext.GetJwtTokenSetting());
        var response = await _httpClient.GetAsync(BASE_URL + "?action=auth");
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonConvert.DeserializeObject<LoginResponse>(responseBody);

        if (apiResponse.Success)
        {
            var _currentUser = apiResponse.User;
            var vpnProfile = _currentUser?.VpnProfile;
            _appContext.SetCurrentUser(_currentUser);
            _appContext.SetVpnProfile(ResolveVpnProfile(_currentUser?.Username, vpnProfile));

            return true;
        }
        else
        {
            throw new Exception(apiResponse.Message);
        }
    }

    /// <summary>
    /// Caches the profile the server just returned (profiles are now pinned permanently
    /// per account, so this is stable across sessions), or falls back to a previously
    /// cached copy if the server didn't return one this time.
    /// </summary>
    private VpnProfile ResolveVpnProfile(string username, VpnProfile serverProfile)
    {
        if (serverProfile?.ConfigContent != null)
        {
            TadaPlay.Utils.VpnProfileCache.Save(username, serverProfile.ConfigContent);
            return serverProfile;
        }

        // The VPN server serves each account's profile by name, so when the login response
        // carries none this is still an authoritative copy - and a fresher one than the cache,
        // which may be from a session before the profile was last reissued.
        string downloaded = TadaPlay.Utils.VpnProfileDownloader.TryDownload(username);
        if (downloaded != null)
        {
            TadaPlay.Utils.VpnProfileCache.Save(username, downloaded);
            return new VpnProfile { ConfigContent = downloaded };
        }

        if (TadaPlay.Utils.VpnProfileCache.TryLoad(username, out string cachedConfig))
        {
            return new VpnProfile { ConfigContent = cachedConfig };
        }

        return null;
    }

    public async Task<bool> DoLoginAsync(string Username, string Password, bool AutoLogin)
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Username))
        {
            throw new ArgumentException("Vui lòng nhập thông tin đăng nhập!");

        }

        var loginData = new
        {
            username = Username,
            password = Password,
            // Also in the body: a server that reads the version from the JSON login payload
            // (rather than a header) still sees it, so a current client is never told to update.
            client_version = ClientVersion
        };

        var json = JsonConvert.SerializeObject(loginData);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(BASE_URL + "?action=login", content);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonConvert.DeserializeObject<LoginResponse>(responseBody);

        if (apiResponse.Success)
        {
            var jwtToken = apiResponse.Token;
            var _currentUser = apiResponse.User;
            var vpnProfile = _currentUser?.VpnProfile;
            _appContext.SetCurrentUser(_currentUser);
            _appContext.SetJwtTokenSetting(jwtToken);
            _appContext.SetAutoLoginSetting(AutoLogin);
            _appContext.SetVpnProfile(ResolveVpnProfile(_currentUser?.Username, vpnProfile));

            return true;
        }
        else
        {
            throw new Exception(apiResponse.Message);
        }
    }

    public async Task<bool> ReleaseVpnProfileAsync()
    {
        var currentUser = _appContext.GetCurrentUser();
        var content = new StringContent("", Encoding.UTF8, "application/json");
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _appContext.GetJwtTokenSetting());
        var response = await _httpClient.PostAsync(BASE_URL + "?action=release_vpn_profile", content);
        response.EnsureSuccessStatusCode();
        var responseBody = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonConvert.DeserializeObject<ApiResponse>(responseBody);
        if (apiResponse.Success)
        {
            _appContext.SetVpnProfile(null);

            return true;
        }

        return false;
    }

    public async Task<bool> UpdateUserInfo(string full_name, string nick_name, string current_password, string new_password)
    {
        var loginData = new
        {
            full_name,
            nick_name,
            current_password,
            new_password
        };

        var json = JsonConvert.SerializeObject(loginData);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _appContext.GetJwtTokenSetting());
        var response = await _httpClient.PostAsync(BASE_URL + "?action=update-user", content);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            var apiResponse = JsonConvert.DeserializeObject<ApiResponse>(responseBody);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new LoginException("Lỗi đăng nhập!" + apiResponse.Message);
            }
            else
            {
                throw new Exception("Lỗi:" + apiResponse.Message);
            }
        }

        return true;
    }

    public async Task<UpdateIpResponse> UpdateCurrentIPToServer(string currentIp)
    {
        if (currentIp == null || currentIp.Trim() == string.Empty) return null;

        var ipData = new { ip_address = currentIp };

        var content = new StringContent(JsonConvert.SerializeObject(ipData), Encoding.UTF8, "application/json");
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _appContext.GetJwtTokenSetting());
        var response = await _httpClient.PostAsync(BASE_URL + "?action=update-ip", content);

        var responseBody = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonConvert.DeserializeObject<UpdateIpResponse>(responseBody);

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new LoginException("Lỗi đăng nhập:" + apiResponse?.Message);
            }
            else
            {
                throw new Exception("Lỗi cập nhật IP:" + apiResponse?.Message);
            }
        }

        return apiResponse;
    }

    // Reports the player's actual in-game profile name (read from player.nfz) to the server,
    // which stores it as users.in_game_name - the key the replay matcher uses to attribute ELO.
    // A 409 (name already owned by another account) is returned as Conflict=true rather than
    // thrown, so the caller can prompt the user to rename their profile in-game.
    public async Task<SetInGameNameResponse> SetInGameNameAsync(string inGameName)
    {
        if (string.IsNullOrWhiteSpace(inGameName)) return null;

        var body = new { in_game_name = inGameName };
        var content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _appContext.GetJwtTokenSetting());
        var response = await _httpClient.PostAsync(BASE_URL + "?action=set_in_game_name", content);

        var responseBody = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonConvert.DeserializeObject<SetInGameNameResponse>(responseBody);

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new LoginException("Lỗi đăng nhập:" + apiResponse?.Message);
            }
            // 409 Conflict is an expected, actionable outcome (name taken) - hand it back to the
            // caller instead of throwing so it can be shown to the user.
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                return apiResponse ?? new SetInGameNameResponse { Success = false, Conflict = true };
            }
            throw new Exception("Lỗi cập nhật tên trong game:" + apiResponse?.Message);
        }

        return apiResponse;
    }

    public async Task<GameRecordUploadResponse> UploadGameRecordAsync(GameRecordMetadata metadata, string recordFilePath)
    {
        if (metadata == null) throw new ArgumentNullException(nameof(metadata));
        if (string.IsNullOrWhiteSpace(recordFilePath) || !File.Exists(recordFilePath))
        {
            throw new FileNotFoundException("Không tìm thấy file record của trận đấu.", recordFilePath);
        }

        using var form = new MultipartFormDataContent();

        // Metadata fields.
        form.Add(new StringContent(metadata.RoomId ?? string.Empty), "room_id");
        form.Add(new StringContent(metadata.RoomName ?? string.Empty), "room_name");
        form.Add(new StringContent(metadata.HostUsername ?? string.Empty), "host_username");
        form.Add(new StringContent(metadata.UploadedBy ?? string.Empty), "uploaded_by");
        form.Add(new StringContent(JsonConvert.SerializeObject(metadata.Players ?? Array.Empty<string>())), "players");
        form.Add(new StringContent(metadata.FinishedAt.ToString("o")), "finished_at");
        form.Add(new StringContent(metadata.ClientVersion ?? string.Empty), "client_version");

        // The recorded game binary. Stream it so large files aren't fully buffered in memory.
        var fileStream = File.OpenRead(recordFilePath);
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        string fileName = metadata.RecordFileName ?? Path.GetFileName(recordFilePath);
        form.Add(fileContent, "record", fileName);

        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _appContext.GetJwtTokenSetting());

        // Uploading a record can take longer than a regular API call; give it more time.
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
        HttpResponseMessage response = await _httpClient.PostAsync(BASE_URL + "?action=upload_record", form, cts.Token);

        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            var errorResponse = JsonConvert.DeserializeObject<ApiResponse>(responseBody);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new LoginException("Lỗi đăng nhập: " + errorResponse?.Message);
            }

            throw new Exception("Lỗi tải lên record: " + (errorResponse?.Message ?? response.ReasonPhrase));
        }

        var uploadResponse = JsonConvert.DeserializeObject<GameRecordUploadResponse>(responseBody);
        if (uploadResponse == null || !uploadResponse.Success)
        {
            throw new Exception("Tải lên record thất bại: " + (uploadResponse?.Message ?? "phản hồi không hợp lệ."));
        }

        return uploadResponse;
    }

    public async Task<ReportResultResponse> ReportGameResultAsync(long recordId, int winningTeam)
    {
        var payload = new { record_id = recordId, winning_team = winningTeam };
        var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _appContext.GetJwtTokenSetting());
        var response = await _httpClient.PostAsync(BASE_URL + "?action=report_result", content);

        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            var errorResponse = JsonConvert.DeserializeObject<ApiResponse>(responseBody);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new LoginException("Lỗi đăng nhập: " + errorResponse?.Message);
            }

            throw new Exception(errorResponse?.Message ?? "Báo kết quả thất bại.");
        }

        var result = JsonConvert.DeserializeObject<ReportResultResponse>(responseBody);
        if (result == null || !result.Success)
        {
            throw new Exception("Báo kết quả thất bại: " + (result?.Message ?? "phản hồi không hợp lệ."));
        }

        return result;
    }

    public async Task<List<RankingEntry>> GetLeaderboardAsync()
    {
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _appContext.GetJwtTokenSetting());
        var response = await _httpClient.GetAsync(BASE_URL + "?action=leaderboard");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<LeaderboardResponse>(body);
        return result?.Players ?? new List<RankingEntry>();
    }

    public async Task<List<MatchSummary>> GetMatchesAsync()
    {
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _appContext.GetJwtTokenSetting());
        var response = await _httpClient.GetAsync(BASE_URL + "?action=matches");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<MatchesResponse>(body);
        return result?.Matches ?? new List<MatchSummary>();
    }

    /// <summary>
    /// Renames a match. Allowed for the account that uploaded it and for admins; the server
    /// decides, so a refusal comes back as an error rather than being prevented here.
    /// </summary>
    public async Task RenameMatchAsync(long matchId, string roomName)
    {
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _appContext.GetJwtTokenSetting());

        var payload = new StringContent(
            JsonConvert.SerializeObject(new { id = matchId, room_name = roomName }),
            System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(BASE_URL + "?action=rename_match", payload);
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            // The server explains refusals in Vietnamese; surface that rather than a status code.
            string message = null;
            try { message = JObject.Parse(body)["message"]?.ToString(); } catch (Exception) { }
            throw new Exception(string.IsNullOrWhiteSpace(message)
                ? $"Không đổi được tên trận đấu ({(int)response.StatusCode})."
                : message);
        }
    }

    public async Task DownloadRecordAsync(long recordId, string destinationPath)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _appContext.GetJwtTokenSetting());
        using var response = await _httpClient.GetAsync(BASE_URL + $"?action=download_record&id={recordId}", HttpCompletionOption.ResponseHeadersRead);

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync();
            var apiResponse = JsonConvert.DeserializeObject<ApiResponse>(errBody);
            throw new Exception("Tải record thất bại: " + (apiResponse?.Message ?? response.ReasonPhrase));
        }

        await using var source = await response.Content.ReadAsStreamAsync();
        await using var fileStream = File.Create(destinationPath);
        await source.CopyToAsync(fileStream);
    }

    // --- Hotkey backups -----------------------------------------------------------------
    //
    // The layout travels base64 inside JSON rather than as a multipart upload. A .hki is a few
    // hundred bytes; the ceremony of a file upload would cost more than the payload, and every
    // other endpoint here already speaks JSON.

    public async Task<List<HotkeyBackup>> GetHotkeyBackupsAsync()
    {
        Authorize();
        var response = await _httpClient.GetAsync(BASE_URL + "?action=hotkey_backups");
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw Failure(body, "Không lấy được danh sách sao lưu", response);

        var result = JsonConvert.DeserializeObject<HotkeyBackupListResponse>(body);
        return result?.Backups ?? new List<HotkeyBackup>();
    }

    public async Task<HotkeyBackup> BackupHotkeysAsync(string name, byte[] fileBytes)
    {
        Authorize();
        var payload = new StringContent(
            JsonConvert.SerializeObject(new { name, data_base64 = Convert.ToBase64String(fileBytes) }),
            System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(BASE_URL + "?action=backup_hotkeys", payload);
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw Failure(body, "Sao lưu phím tắt thất bại", response);

        return JsonConvert.DeserializeObject<HotkeyBackup>(body);
    }

    public async Task<byte[]> RestoreHotkeysAsync(long backupId)
    {
        Authorize();
        var response = await _httpClient.GetAsync(BASE_URL + "?action=restore_hotkeys&id=" + backupId);
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw Failure(body, "Không tải được bản sao lưu", response);

        string base64 = JObject.Parse(body)["data_base64"]?.ToString();
        if (string.IsNullOrEmpty(base64))
        {
            throw new Exception("Bản sao lưu không có dữ liệu phím tắt.");
        }
        return Convert.FromBase64String(base64);
    }

    public async Task DeleteHotkeyBackupAsync(long backupId)
    {
        Authorize();
        var payload = new StringContent(
            JsonConvert.SerializeObject(new { id = backupId }),
            System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(BASE_URL + "?action=delete_hotkey_backup", payload);
        if (!response.IsSuccessStatusCode)
        {
            throw Failure(await response.Content.ReadAsStringAsync(), "Không xóa được bản sao lưu", response);
        }
    }

    private void Authorize()
    {
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _appContext.GetJwtTokenSetting());
    }

    /// <summary>
    /// The server explains refusals in Vietnamese; surface that rather than a status code, and
    /// fall back to the code only when there is no message to show.
    /// </summary>
    private static Exception Failure(string body, string fallback, HttpResponseMessage response)
    {
        string message = null;
        try { message = JObject.Parse(body)["message"]?.ToString(); } catch (Exception) { }
        return new Exception(string.IsNullOrWhiteSpace(message)
            ? $"{fallback} ({(int)response.StatusCode})."
            : message);
    }

    public async Task<bool> DoLogoutAsync()
    {
        await ReleaseVpnProfileAsync();
        _appContext.SetJwtTokenSetting(null);
        _appContext.SetCurrentUser(null);

        return true;
    }
}