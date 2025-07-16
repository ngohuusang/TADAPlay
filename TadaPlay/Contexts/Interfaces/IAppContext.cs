using TadaPlay.Common.Models;

namespace TadaPlay.Contexts.Interfaces;

public interface IAppContext
{
    public string GetJwtTokenSetting();

    public void SetJwtTokenSetting(string token);

    public void SetAutoLoginSetting(bool autoLogin);

    public bool GetAutoLoginSetting();

    public void SetGameFolder(string folderPath);

    public string GetGameFolder();

    public void SetMinimapPosition(string position);

    public string GetMinimapPosition();

    public void SetCurrentUser(User user);

    public User GetCurrentUser();

    public void SetVpnProfile(VpnProfile vpnProfile);

    public VpnProfile GetVpnProfile();

    IReadOnlyList<User> AllOnlineUsers { get; }
    IReadOnlyList<ClientRoom> AllActiveRooms { get; }
    ClientRoom CurrentRoomDetails { get; }

    event System.EventHandler OnCurrentUserUpdated;
    event System.EventHandler OnOnlineUsersUpdated;
    event System.EventHandler OnActiveRoomsUpdated;
    event System.EventHandler OnCurrentRoomDetailsUpdated;

    event System.EventHandler OnVpnProfileUpdated;

    void ProcessWebSocketMessage(string jsonMessage);
}