using System.Collections.Generic;
using Newtonsoft.Json;

namespace TadaPlay.Common.Models
{
    /// <summary>Response from api.php?action=leaderboard.</summary>
    public class LeaderboardResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("players")]
        public List<RankingEntry> Players { get; set; }
    }

    public class RankingEntry
    {
        [JsonProperty("rank")]
        public int Rank { get; set; }

        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("display_name")]
        public string DisplayName { get; set; }

        [JsonProperty("rating")]
        public int Rating { get; set; }

        [JsonProperty("games_played")]
        public int GamesPlayed { get; set; }

        [JsonProperty("wins")]
        public int Wins { get; set; }

        [JsonProperty("losses")]
        public int Losses { get; set; }

        [JsonProperty("win_rate")]
        public int WinRate { get; set; }

        /// <summary>
        /// The name this player appears as inside the game. Null when nothing has been
        /// recorded for them - which is itself worth showing, because it means their matches
        /// cannot be tied back to this account.
        /// </summary>
        [JsonProperty("in_game_name")]
        public string InGameName { get; set; }

        /// <summary>The VPN address pinned to the account, or null.</summary>
        [JsonProperty("vpn_ip")]
        public string VpnIp { get; set; }
    }

    /// <summary>Response from api.php?action=player_names.</summary>
    public class PlayerNamesResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("names")]
        public List<PlayerGameName> Names { get; set; }
    }

    /// <summary>One in-game name an account has been seen playing under.</summary>
    public class PlayerGameName
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("first_seen")]
        public string FirstSeen { get; set; }

        [JsonProperty("last_seen")]
        public string LastSeen { get; set; }
    }

    /// <summary>Response from api.php?action=player_matches.</summary>
    public class PlayerMatchesResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("matches")]
        public List<PlayerMatch> Matches { get; set; }
    }

    /// <summary>One match a player appeared in, from their side of it.</summary>
    public class PlayerMatch
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("room_name")]
        public string RoomName { get; set; }

        [JsonProperty("finished_at")]
        public string FinishedAt { get; set; }

        /// <summary>"pending", "resolved" or "needs_review".</summary>
        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("team")]
        public int? Team { get; set; }

        [JsonProperty("score")]
        public int? Score { get; set; }

        /// <summary>
        /// Null when nobody has reported a winner yet, which is a different thing from losing.
        /// </summary>
        [JsonProperty("won")]
        public bool? Won { get; set; }

        [JsonProperty("is_mvp")]
        public bool IsMvp { get; set; }

        [JsonProperty("team_mates")]
        public List<string> TeamMates { get; set; }

        [JsonProperty("opponents")]
        public List<string> Opponents { get; set; }

        /// <summary>
        /// Whether the replay file is actually on the server. Checked there rather than assumed,
        /// so a row never offers a button that cannot work.
        /// </summary>
        [JsonProperty("can_replay")]
        public bool CanReplay { get; set; }

        [JsonProperty("file_name")]
        public string FileName { get; set; }
    }

    /// <summary>Response from api.php?action=matches.</summary>
    public class MatchesResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("matches")]
        public List<MatchSummary> Matches { get; set; }
    }

    public class MatchSummary
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("room_name")]
        public string RoomName { get; set; }

        [JsonProperty("finished_at")]
        public string FinishedAt { get; set; }

        /// <summary>"pending", "resolved" or "needs_review".</summary>
        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("winner_team")]
        public int? WinnerTeam { get; set; }

        /// <summary>Team number ("1"/"2") to usernames. May be empty if teams weren't resolved.</summary>
        [JsonProperty("teams")]
        public Dictionary<string, string[]> Teams { get; set; }

        /// <summary>Overall game-flagged MVP username (or in-game name if unmatched).</summary>
        [JsonProperty("mvp")]
        public string Mvp { get; set; }

        /// <summary>Top scorer on the winning team (set once the result is reported).</summary>
        [JsonProperty("winner_mvp")]
        public string WinnerMvp { get; set; }

        /// <summary>Top scorer on the losing team.</summary>
        [JsonProperty("loser_mvp")]
        public string LoserMvp { get; set; }

        /// <summary>Per-player scoreboard parsed from the replay.</summary>
        [JsonProperty("players")]
        public List<MatchPlayer> Players { get; set; }

        [JsonProperty("file_name")]
        public string FileName { get; set; }

        [JsonProperty("can_replay")]
        public bool CanReplay { get; set; }

        /// <summary>Account that uploaded this match.</summary>
        [JsonProperty("uploaded_by")]
        public string UploadedBy { get; set; }

        /// <summary>
        /// Whether the signed-in account may rename this match - it uploaded it, or it is an
        /// admin. Decided by the server; this only says whether to OFFER the rename, and the
        /// server checks again before applying one.
        /// </summary>
        [JsonProperty("can_rename")]
        public bool CanRename { get; set; }
    }

    public class MatchPlayer
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("team")]
        public int? Team { get; set; }

        [JsonProperty("winner")]
        public bool? Winner { get; set; }

        [JsonProperty("score")]
        public int? Score { get; set; }

        [JsonProperty("mvp")]
        public bool Mvp { get; set; }

        [JsonProperty("eapm")]
        public int? Eapm { get; set; }
    }
}
