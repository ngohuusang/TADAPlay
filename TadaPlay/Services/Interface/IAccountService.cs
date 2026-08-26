
using TadaPlay.Common.Models;

namespace TadaPlay.Services.Interface;

public interface IAccountService
{
    Task<List<User>> GetAllAsync();

    Task<bool> DoAuthAsync();

    Task<bool> DoLoginAsync(string Username, string Password, bool AutoLogin);

    Task<bool> ReleaseVpnProfileAsync();

    Task<bool> DoLogoutAsync();

    Task<bool> UpdateUserInfo(string full_name, string nick_name, string current_password, string new_password);

    Task<UpdateIpResponse> UpdateCurrentIPToServer(string currentIp);

    Task<SetInGameNameResponse> SetInGameNameAsync(string inGameName);

    Task<GameRecordUploadResponse> UploadGameRecordAsync(GameRecordMetadata metadata, string recordFilePath);

    Task<ReportResultResponse> ReportGameResultAsync(long recordId, int winningTeam);

    Task<List<RankingEntry>> GetLeaderboardAsync();

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