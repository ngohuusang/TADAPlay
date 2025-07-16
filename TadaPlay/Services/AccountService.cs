using log4net.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Http;
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

    public AccountService(IAppContext appContext)
    {
        System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
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
            _appContext.SetVpnProfile(vpnProfile);

            return true;
        }
        else
        {
            throw new Exception(apiResponse.Message);
        }
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
            password = Password
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
            _appContext.SetVpnProfile(vpnProfile);

            return true;
        }
        else
        {
            throw new Exception(apiResponse.Message);
        }
    }

    public async Task<bool> ReleaseVpnProfileAsync()
    {
        var content = new StringContent("", Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(BASE_URL + "?action=release_vpn_profile", content);
        response.EnsureSuccessStatusCode();
        var responseBody = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonConvert.DeserializeObject<ApiResponse>(responseBody);
        if (apiResponse.Success)
        {
            _appContext.SetVpnProfile(null);

            return true;
        }
        else
        {
            throw new Exception(apiResponse.Message);
        }
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

    public async Task<bool> UpdateCurrentIPToServer(string currentIp)
    {
        if (currentIp == null || currentIp.Trim() == string.Empty) return false;

        var ipData = new { ip_address = currentIp };

        var content = new StringContent(JsonConvert.SerializeObject(ipData), Encoding.UTF8, "application/json");
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _appContext.GetJwtTokenSetting());
        var response = await _httpClient.PostAsync(BASE_URL + "?action=update-ip", content);
       

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            var apiResponse = JsonConvert.DeserializeObject<ApiResponse>(responseBody);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new LoginException("Lỗi đăng nhập:" + apiResponse.Message);
            }
            else
            {
                throw new Exception("Lỗi cập nhật IP:" + apiResponse.Message);
            }
        }

        return true;
    }
}