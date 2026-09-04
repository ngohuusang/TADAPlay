using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using TadaPlay.Common.Models;
using TadaPlay.Contexts.Interfaces;
using TadaPlay.Logger;
using TadaPlay.Utils;

namespace TadaPlay.Contexts;

public class AppContext : IAppContext
{

    private const string REGISTRY_SUB_KEY = @"SOFTWARE\TadaPlay";
    private const string WINDOWS_RUN_REGISTRY_KEY = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string WINDOWS_RUN_VALUE_NAME = "TadaPlay";

    private User? _currentUser;
    private List<User> _allOnlineUsers = new List<User>();
    private VpnProfile _currentVpnProfile;

    private static object? GetRegistryValue(string name)
    {
        try
        {
            RegistryKey key = Registry.CurrentUser.OpenSubKey(REGISTRY_SUB_KEY);
            if (key != null)
            {
                object value = key.GetValue(name);
                if (value != null)
                {
                    return value;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error reading token from registry: " + ex.Message);
        }

        return null;
    }

    private static void SetRegistryValue(string name, object value)
    {
        try
        {
            RegistryKey key = Registry.CurrentUser.CreateSubKey(REGISTRY_SUB_KEY);
            if (value == null)
            {
                key.DeleteValue(name);
            }
            else
            {
                key.SetValue(name, value);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error writing to registry: " + ex.Message);
        }
    }

    public string GetJwtTokenSetting()
    {
        object jwtToken = GetRegistryValue("JwtToken");
        return jwtToken != null ? jwtToken.ToString() : string.Empty;
    }

    public void SetJwtTokenSetting(string token)
    {
        SetRegistryValue("JwtToken", token);
    }

    public void SetAutoLoginSetting(bool autoLogin)
    {
        SetRegistryValue("AutoLogin", autoLogin);
    }

    public bool GetAutoLoginSetting()
    {
        object AutoLogin = GetRegistryValue("AutoLogin");
        return AutoLogin != null && Convert.ToBoolean(AutoLogin);
    }

    // The app's manifest requests requireAdministrator, and Windows will NOT auto-elevate an
    // entry in the HKCU "Run" key at logon - it just silently skips launching it (no UAC prompt
    // is ever shown for Run-key startup, so a would-be-elevated entry is refused rather than
    // asking). A Scheduled Task with "Run with highest privileges" is the supported way to
    // launch an elevated app at logon without a prompt, since the elevation was already granted
    // when the (already-elevated) app registered the task. See SetRunOnStartupSetting.
    private const string STARTUP_TASK_NAME = "TadaPlay";

    public void SetRunOnStartupSetting(bool runOnStartup)
    {
        // Clean up the old HKCU Run-key entry from earlier builds, if present - it never
        // actually launched the app (see above) and would otherwise linger alongside the task.
        try
        {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(WINDOWS_RUN_REGISTRY_KEY, writable: true);
            key?.DeleteValue(WINDOWS_RUN_VALUE_NAME, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error cleaning up legacy startup registry entry: " + ex.Message);
        }

        if (runOnStartup)
        {
            string exePath = System.Windows.Forms.Application.ExecutablePath;
            RunSchtasks($"/Create /TN \"{STARTUP_TASK_NAME}\" /TR \"\\\"{exePath}\\\" --minimized\" /SC ONLOGON /RL HIGHEST /F");
        }
        else
        {
            RunSchtasks($"/Delete /TN \"{STARTUP_TASK_NAME}\" /F");
        }
    }

    public bool GetRunOnStartupSetting()
    {
        return RunSchtasks($"/Query /TN \"{STARTUP_TASK_NAME}\"") == 0;
    }

    private static int RunSchtasks(string arguments)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };
            process.Start();
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error running schtasks: " + ex.Message);
            return -1;
        }
    }

    public void SetGameFolder(string folderPath)
    {
        SetRegistryValue("GameFolder", folderPath);
    }

    // Which embedded WK launcher (see GameExecutablePreparer) gets copied into the game's
    // age2_x1 folder and started on "Bắt đầu" - replaces the old free-form exe file picker now
    // that the launcher itself is always one of these two known, app-provided files.
    /// <summary>
    /// Whether this player's matches may be watched by others.
    ///
    /// Default OFF, and deliberately opt-in rather than opt-out. Sharing is not free for the
    /// person doing it: it captures the running record on a timer, listens on a port, and
    /// streams tens of megabytes of match data to each viewer across the VPN - traffic that all
    /// hairpins through the single tunnel server everyone else is also playing through. Nobody
    /// should be paying that, for themselves or for the lobby, without having chosen to.
    /// </summary>
    public void SetAllowSpectateSetting(bool allow)
    {
        SetRegistryValue("AllowSpectate", allow ? 1 : 0);
    }

    public bool GetAllowSpectateSetting()
    {
        object value = GetRegistryValue("AllowSpectate");
        // Absent means never chosen, which must read as off - the whole point of the default.
        if (value == null) return false;
        return value.ToString() == "1"
               || string.Equals(value.ToString(), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether to write a debug log file. Off unless the player turned it on in Cài đặt.
    ///
    /// Opt-in because the log is only ever useful when something has gone wrong and someone is
    /// going to read it. Writing it by default cost every player a file that grew all session,
    /// on every machine, to be read by nobody.
    /// </summary>
    public void SetDebugLogSetting(bool enabled)
    {
        SetRegistryValue("DebugLog", enabled ? 1 : 0);
    }

    public bool GetDebugLogSetting() => IsDebugLogEnabled();

    /// <summary>
    /// The same value, readable without an instance: Program has to configure logging before
    /// the DI container that would hand it an IAppContext exists.
    /// </summary>
    public static bool IsDebugLogEnabled()
    {
        object value = GetRegistryValue("DebugLog");
        // Absent means never chosen, which must read as off - the whole point of the default.
        if (value == null) return false;
        return value.ToString() == "1"
               || string.Equals(value.ToString(), "true", StringComparison.OrdinalIgnoreCase);
    }

    public void SetGameLaunchMode(string mode)
    {
        SetRegistryValue("GameLaunchMode", mode);
    }

    public string GetGameLaunchMode()
    {
        object mode = GetRegistryValue("GameLaunchMode");
        return string.Equals(mode?.ToString(), GameExecutablePreparer.CenterMode, StringComparison.OrdinalIgnoreCase)
            ? GameExecutablePreparer.CenterMode
            : GameExecutablePreparer.WideMode;
    }

    public void SetMinimapPosition(string position)
    {
        SetRegistryValue("GameMinimapPosition", position);
    }

    public string GetGameFolder()
    {
        object GameFolder = GetRegistryValue("GameFolder");
        return GameFolder != null ? GameFolder.ToString() : string.Empty;
    }

    public string GetMinimapPosition()
    {
        object GameMinimapPosition = GetRegistryValue("GameMinimapPosition");
        return GameMinimapPosition != null ? GameMinimapPosition.ToString() : string.Empty;
    }

    public User GetCurrentUser()
    {
        return _currentUser ?? new User();
    }

    public void SetCurrentUser(User user)
    {
        _currentUser = user;
        OnCurrentUserUpdated?.Invoke(this, EventArgs.Empty);
        // Also update the global list if current user changes
        UpdateOnlineUserInList(user);
    }

    public void SetVpnProfile(VpnProfile vpnProfile)
    {
        _currentVpnProfile = vpnProfile;
        OnVpnProfileUpdated?.Invoke(this, EventArgs.Empty);
    }

    public VpnProfile GetVpnProfile()
    {
        return _currentVpnProfile;
    }

    public IReadOnlyList<User> AllOnlineUsers => _allOnlineUsers.AsReadOnly();

    // --- Events ---
    public event EventHandler OnCurrentUserUpdated;
    public event EventHandler OnOnlineUsersUpdated;
    public event EventHandler<IReadOnlyList<User>> OnUserCameOnline;

    public event EventHandler OnVpnProfileUpdated;

    public void ProcessWebSocketMessage(string jsonMessage)
    {
        try
        {
            var data = JObject.Parse(jsonMessage);
            string messageType = data["type"]?.ToString();

            if (messageType == "user_list")
            {
                var newOnlineUsers = (data["users"] as JArray)?
                                     .Select(u => JsonConvert.DeserializeObject<User>(u.ToString()))
                                     .ToList() ?? new List<User>();

                var previousUsernames = _allOnlineUsers.Select(u => u.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var newlyOnline = newOnlineUsers
                    .Where(u => !string.IsNullOrEmpty(u.Username)
                                && !string.Equals(u.Username, _currentUser?.Username, StringComparison.OrdinalIgnoreCase)
                                && !previousUsernames.Contains(u.Username))
                    .ToList();

                _allOnlineUsers = newOnlineUsers;
                OnOnlineUsersUpdated?.Invoke(this, EventArgs.Empty); // Notify subscribers

                // Skip the very first snapshot after (re)connecting - everyone already online
                // would otherwise be reported as "just came online".
                if (previousUsernames.Count > 0 && newlyOnline.Count > 0)
                {
                    OnUserCameOnline?.Invoke(this, newlyOnline);
                }

                var updatedCurrentUser = _allOnlineUsers.FirstOrDefault(u => u.Username == _currentUser?.Username);
                if (updatedCurrentUser != null && _currentUser != null)
                {
                    _currentUser.Status = updatedCurrentUser.Status;
                    OnCurrentUserUpdated?.Invoke(this, EventArgs.Empty);
                }

                DebugLogger.Info("AppContext: Online users updated from WebSocket message.");
            }
            // Handle other message types if AppContext needs to process them
            // e.g., notifications directly (though UI might also subscribe directly)
        }
        catch (Exception ex)
        {
            DebugLogger.Error($"AppContext: Error processing WebSocket message: {ex.Message}");
        }

    }

    private void UpdateOnlineUserInList(User user)
    {
        if (user == null) return;
        var existingUser = _allOnlineUsers.FirstOrDefault(u => u.Id == user.Id);
        if (existingUser != null)
        {
            // Update properties of the existing object
            existingUser.Status = user.Status;
            existingUser.CurrentRoomId = user.CurrentRoomId;
            existingUser.NickName = user.NickName;
            existingUser.FullName = user.FullName;
            existingUser.IpAddress = user.IpAddress;
        }
        else
        {
            // Add if not found
            _allOnlineUsers.Add(user);
        }
        OnOnlineUsersUpdated?.Invoke(this, EventArgs.Empty);
    }

}