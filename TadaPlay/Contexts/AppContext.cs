using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using TadaPlay.Common.Models;
using TadaPlay.Contexts.Interfaces;
using TadaPlay.Logger;

namespace TadaPlay.Contexts;

public class AppContext : IAppContext
{

    private const string REGISTRY_SUB_KEY = @"SOFTWARE\TadaPlay";

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

    public void SetGameFolder(string folderPath)
    {
        SetRegistryValue("GameFolder", folderPath);
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
                _allOnlineUsers = newOnlineUsers;
                OnOnlineUsersUpdated?.Invoke(this, EventArgs.Empty); // Notify subscribers

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