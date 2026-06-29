using System.Collections.Generic;
using Newtonsoft.Json;

namespace TadaPlay.Common.Models
{
    /// <summary>Response from api.php?action=upload_record.</summary>
    public class GameRecordUploadResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("record")]
        public GameRecordInfo Record { get; set; }
    }

    public class GameRecordInfo
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("room_id")]
        public string RoomId { get; set; }

        [JsonProperty("file_name")]
        public string FileName { get; set; }

        /// <summary>"pending" (host can report winner) or "needs_review" (cannot rate).</summary>
        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("can_report")]
        public bool CanReport { get; set; }

        /// <summary>Team number ("1"/"2") to the usernames on that team. Null when teams couldn't be resolved.</summary>
        [JsonProperty("teams")]
        public Dictionary<string, string[]> Teams { get; set; }

        /// <summary>MVP username (or in-game name if unmatched).</summary>
        [JsonProperty("mvp")]
        public string Mvp { get; set; }

        /// <summary>Parser's best-guess winning team (1/2), shown pre-selected for host confirmation.</summary>
        [JsonProperty("suggested_winner_team")]
        public int? SuggestedWinnerTeam { get; set; }

        [JsonProperty("review_reason")]
        public string ReviewReason { get; set; }
    }

    /// <summary>Response from api.php?action=report_result.</summary>
    public class ReportResultResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("winning_team")]
        public int WinningTeam { get; set; }

        [JsonProperty("ratings")]
        public List<RatingChange> Ratings { get; set; }
    }

    public class RatingChange
    {
        [JsonProperty("user_id")]
        public string UserId { get; set; }

        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("team")]
        public int Team { get; set; }

        [JsonProperty("score")]
        public int? Score { get; set; }

        [JsonProperty("is_mvp")]
        public bool IsMvp { get; set; }

        [JsonProperty("base_delta")]
        public int BaseDelta { get; set; }

        [JsonProperty("perf_delta")]
        public int PerfDelta { get; set; }

        [JsonProperty("mvp_delta")]
        public int MvpDelta { get; set; }

        [JsonProperty("old")]
        public int Old { get; set; }

        [JsonProperty("new")]
        public int NewRating { get; set; }

        [JsonProperty("delta")]
        public int Delta { get; set; }
    }
}
