using TadaPlay.Common.Models;

namespace TadaPlay.Contexts.Interfaces;

public interface IAppContext
{
    public string GetJwtTokenSetting();

    public void SetJwtTokenSetting(string token);

    public void SetAutoLoginSetting(bool autoLogin);

    public bool GetAutoLoginSetting();

    public void SetRunOnStartupSetting(bool runOnStartup);

    public bool GetRunOnStartupSetting();

    public void SetGameFolder(string folderPath);

    public string GetGameFolder();

    public void SetGameLaunchMode(string mode);

    public string GetGameLaunchMode();

    public void SetMinimapPosition(string position);

    public string GetMinimapPosition();

    public void SetCurrentUser(User user);

    public User GetCurrentUser();

    public void SetVpnProfile(VpnProfile vpnProfile);

    public VpnProfile GetVpnProfile();

    IReadOnlyList<User> AllOnlineUsers { get; }

    event System.EventHandler OnCurrentUserUpdated;
    event System.EventHandler OnOnlineUsersUpdated;
    event System.EventHandler<IReadOnlyList<User>> OnUserCameOnline;

    event System.EventHandler OnVpnProfileUpdated;

    void ProcessWebSocketMessage(string jsonMessage);
}