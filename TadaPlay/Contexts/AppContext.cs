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
    private List<ClientRoom> _allActiveRooms = new List<ClientRoom>();
    private ClientRoom _currentRoomDetails;

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
            key.SetValue(name, value);
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

    public IReadOnlyList<User> AllOnlineUsers => _allOnlineUsers.AsReadOnly();
    public IReadOnlyList<ClientRoom> AllActiveRooms => _allActiveRooms.AsReadOnly();
    public ClientRoom CurrentRoomDetails => _currentRoomDetails;

    // --- Events ---
    public event EventHandler OnCurrentUserUpdated;
    public event EventHandler OnOnlineUsersUpdated;
    public event EventHandler OnActiveRoomsUpdated;
    public event EventHandler OnCurrentRoomDetailsUpdated;

    public void ProcessWebSocketMessage(string jsonMessage)
    {
        try
        {
            var data = JObject.Parse(jsonMessage);
            string messageType = data["type"]?.ToString();

            if (messageType == "user_list")
            {
                // Update AllOnlineUsers
                var newOnlineUsers = (data["users"] as JArray)?
                                     .Select(u => JsonConvert.DeserializeObject<User>(u.ToString()))
                                     .ToList() ?? new List<User>();
                // Update our internal list
                _allOnlineUsers = newOnlineUsers;
                OnOnlineUsersUpdated?.Invoke(this, EventArgs.Empty); // Notify subscribers

                // Update AllActiveRooms
                var newActiveRooms = (data["rooms"] as JArray)?
                                     .Select(r => JsonConvert.DeserializeObject<ClientRoom>(r.ToString()))
                                     .ToList() ?? new List<ClientRoom>();
                _allActiveRooms = newActiveRooms;
                OnActiveRoomsUpdated?.Invoke(this, EventArgs.Empty); // Notify subscribers

                // Also update CurrentUser's latest status and CurrentRoomDetails
                var updatedCurrentUser = _allOnlineUsers.FirstOrDefault(u => u.Username == _currentUser?.Username);
                if (updatedCurrentUser != null)
                {
                    // Only update properties that might change, to avoid replacing the whole object if it has events/bindings
                    if (_currentUser != null)
                    {
                        _currentUser.Status = updatedCurrentUser.Status;
                        _currentUser.CurrentRoomId = updatedCurrentUser.CurrentRoomId;
                    }
                    else
                    {
                        _currentUser = updatedCurrentUser; // Set it if it was null
                    }
                    OnCurrentUserUpdated?.Invoke(this, EventArgs.Empty);
                }

                // Update CurrentRoomDetails if the user is in a room
                var currentUsersRoom = _allActiveRooms.FirstOrDefault(r => r.Id == _currentUser?.CurrentRoomId);
                if (currentUsersRoom != null)
                {
                    // Deep copy or update properties if you want to avoid direct reference.
                    // For simplicity, directly assign the new object.
                    _currentRoomDetails = currentUsersRoom;
                }
                else
                {
                    _currentRoomDetails = null; // User is no longer in a room
                }
                OnCurrentRoomDetailsUpdated?.Invoke(this, EventArgs.Empty); // Notify


                DebugLogger.Info("AppContext: Lobby state updated from WebSocket message.");
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