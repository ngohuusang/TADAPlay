
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
}