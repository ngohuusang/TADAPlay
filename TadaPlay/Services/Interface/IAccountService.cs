
using TadaPlay.Common.Models;

namespace TadaPlay.Services.Interface;

public interface IAccountService
{
    Task<List<User>> GetAllAsync();

    Task<bool> DoAuthAsync();

    Task<bool> DoLoginAsync(string Username, string Password, bool AutoLogin);

    Task<bool> ReleaseVpnProfileAsync();

    Task<bool> DoLogoutAsync();

    /// <summary>
    /// Forgets the stored token, current user and VPN profile without calling the server - for
    /// when the server has already ended this session (the account logged in on another PC).
    /// </summary>
    void ClearLocalSession();

    Task<bool> UpdateUserInfo(string full_name, string nick_name, string current_password, string new_password);

    Task<UpdateIpResponse> UpdateCurrentIPToServer(string currentIp);

    Task<SetInGameNameResponse> SetInGameNameAsync(string inGameName);

    Task<GameRecordUploadResponse> UploadGameRecordAsync(GameRecordMetadata metadata, string recordFilePath);

    Task<ReportResultResponse> ReportGameResultAsync(long recordId, int winningTeam);

    Task<List<RankingEntry>> GetLeaderboardAsync();

    /// <summary>
    /// Every in-game name an account has played under, newest first. Empty when the player has
    /// never been seen under one - which is itself the answer, not a failure.
    /// </summary>
    Task<List<PlayerGameName>> GetPlayerNamesAsync(string username);

    /// <summary>
    /// The matches a player appeared in, newest first. Only matches whose teams were resolved
    /// can be listed - an unresolved one does not know who played in it.
    /// </summary>
    Task<List<PlayerMatch>> GetPlayerMatchesAsync(string username, int limit = 30);

    Task<List<MatchSummary>> GetMatchesAsync();
    Task RenameMatchAsync(long matchId, string roomName);

    Task DownloadRecordAsync(long recordId, string destinationPath);

    // --- Hotkey backups. The editor otherwise only writes player*.hki inside the game folder,
    // which a reinstall wipes; these keep a layout against the account instead.

    /// <summary>The player's backups, newest first. Names and dates only - not the layouts.</summary>
    Task<List<HotkeyBackup>> GetHotkeyBackupsAsync();

    /// <summary>Stores a layout under a name. <paramref name="fileBytes"/> is a .hki as written to disk.</summary>
    Task<HotkeyBackup> BackupHotkeysAsync(string name, byte[] fileBytes);

    /// <summary>Fetches one backup's layout, as the bytes of a .hki.</summary>
    Task<byte[]> RestoreHotkeysAsync(long backupId);

    Task DeleteHotkeyBackupAsync(long backupId);
}