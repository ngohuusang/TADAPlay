using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TadaPlay.Logger;
using TadaPlay.Utils;

namespace TadaPlay.Connections
{
    /// <summary>
    /// Fetches another player's most recent finished match from their
    /// <see cref="LiveShareServer"/> and puts it somewhere the game can replay it.
    /// </summary>
    public static class LiveShareClient
    {
        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        public sealed class HostStatus
        {
            public bool HasMatch { get; set; }
            /// <summary>A match is being played right now on that machine.</summary>
            public bool InGame { get; set; }
            /// <summary>Seconds until the next capture makes it watchable; 0 when ready.</summary>
            public int WaitSeconds { get; set; }
            public string Match { get; set; }
            public long Bytes { get; set; }
            /// <summary>Game time in the shared match, in ms - the match clock, not wall time.</summary>
            public long GameMs { get; set; }
            /// <summary>How far into the match the shared record reaches.</summary>
            public TimeSpan GameTime => TimeSpan.FromMilliseconds(GameMs);
            public DateTime? FinishedUtc { get; set; }
            /// <summary>How long ago the match finished.</summary>
            public TimeSpan? Age => FinishedUtc.HasValue ? DateTime.UtcNow - FinishedUtc.Value : null;
        }

        /// <summary>
        /// Asks a peer whether they have a finished match to share. Returns null if TadaPlay isn't
        /// reachable there (not running, or not on the VPN).
        /// </summary>
        public static async Task<HostStatus> TryGetStatusAsync(string hostIp,
                                                               CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(hostIp)) return null;
            try
            {
                using var request = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(request.Token, token);
                string json = await Http.GetStringAsync(
                    $"http://{hostIp}:{LiveShareServer.Port}/live/status", linked.Token);

                return new HostStatus
                {
                    HasMatch = Read(json, "hasMatch") == "true",
                    InGame = Read(json, "inGame") == "true",
                    WaitSeconds = int.TryParse(Read(json, "waitSeconds"), out int w) ? w : 0,
                    Match = Read(json, "match")?.Trim('"'),
                    Bytes = long.TryParse(Read(json, "bytes"), out long b) ? b : 0,
                    GameMs = long.TryParse(Read(json, "gameMs"), out long g) ? g : 0,
                    FinishedUtc = DateTime.TryParse(Read(json, "finishedUtc")?.Trim('"'),
                                                    null, System.Globalization.DateTimeStyles.RoundtripKind,
                                                    out DateTime t) ? t : null
                };
            }
            catch (Exception ex)
            {
                DebugLogger.Info($"LiveShareClient: no TadaPlay at {hostIp}: {ex.Message}");
                return null;
            }
        }

        // Minimal reader for the small, self-produced status document - avoids pulling a
        // JSON dependency into a path that only ever parses four known fields.
        private static string Read(string json, string field)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int at = json.IndexOf($"\"{field}\":", StringComparison.Ordinal);
            if (at < 0) return null;
            at += field.Length + 3;
            int end = json.IndexOfAny(new[] { ',', '}' }, at);
            return end < 0 ? null : json.Substring(at, end - at).Trim();
        }

        /// <summary>The opening download of a match, and everything needed to keep following it.</summary>
        public sealed class FetchResult
        {
            /// <summary>Where the record was written; null when <see cref="Error"/> is set.</summary>
            public string Path { get; set; }
            public string Error { get; set; }
            /// <summary>The host's name for this match; used to notice when they start a new one.</summary>
            public string MatchName { get; set; }
            /// <summary>Bytes taken from the host - the offset the next request resumes from.</summary>
            public long RawBytes { get; set; }
            /// <summary>Fetched but not written yet: a trailing operation that isn't complete.</summary>
            public byte[] Pending { get; set; } = Array.Empty<byte>();
            public int Operations { get; set; }
            /// <summary>The host was still playing when this was served.</summary>
            public bool InGame { get; set; }
        }

        /// <summary>
        /// The status a player last broadcast over the lobby socket, as a <see cref="HostStatus"/>,
        /// or null when they report nothing - either they are idle or their build predates the
        /// broadcast, and asking them directly tells those apart.
        ///
        /// Lives here so the picker, the status dialog and the spectator overlay all read the
        /// broadcast the same way. They are meant to agree with each other, and three private
        /// copies of this mapping is precisely how they would stop.
        /// </summary>
        public static HostStatus FromBroadcast(TadaPlay.Common.Models.User user)
        {
            if (user == null || (!user.InGame && !user.HasMatch)) return null;
            return new HostStatus
            {
                InGame = user.InGame,
                HasMatch = user.HasMatch,
                GameMs = user.GameMs,
                WaitSeconds = user.WaitSeconds
            };
        }

        /// <summary>A tail pulled from the host: the bytes added since a given offset.</summary>
        public sealed class TailResult
        {
            public byte[] Data { get; set; } = Array.Empty<byte>();
            public string MatchName { get; set; }
            public long TotalBytes { get; set; }
            public bool InGame { get; set; }
            /// <summary>Set when the host could not be reached; Data is meaningless then.</summary>
            public string Error { get; set; }
            public bool Failed => Error != null;
        }

        /// <summary>
        /// Downloads a peer's match and writes it where the game can open it. Works on a match
        /// still being played: the host serves its most recent capture, and
        /// <see cref="LiveStreamSession"/> then keeps appending to the file this produced.
        /// </summary>
        public static async Task<FetchResult> TryFetchAsync(
            string hostIp, string gameFolder, string hostLabel, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(hostIp))
            {
                return new FetchResult { Error = "Chưa chọn người chơi." };
            }

            byte[] data;
            string matchName;
            bool inGame;
            try
            {
                using HttpResponseMessage response = await Http.GetAsync(
                    $"http://{hostIp}:{LiveShareServer.Port}/live/record",
                    HttpCompletionOption.ResponseContentRead, token);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return new FetchResult { Error = $"{hostLabel} chưa có trận đấu nào để xem." };
                }
                response.EnsureSuccessStatusCode();
                data = await response.Content.ReadAsByteArrayAsync(token);
                matchName = Header(response, "X-Match-Name");
                inGame = Header(response, "X-In-Game") == "true";
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"LiveShareClient: fetch from {hostIp} failed: {ex.Message}");
                return new FetchResult { Error = $"Không tải được trận đấu từ {hostLabel}: {ex.Message}" };
            }

            // Never hand the game something it will reject: a match the host abandoned still
            // has an unpatched header length, so verify (and repair) before launching.
            LiveRecordReader.RecordAnalysis analysis = LiveRecordReader.Analyze(data, null);
            if (analysis == null)
            {
                return new FetchResult { Error = "Dữ liệu trận đấu tải về không hợp lệ." };
            }

            string dir = RecordedGameFinder.FindSaveGameDirectory(gameFolder);
            if (string.IsNullOrWhiteSpace(dir))
            {
                return new FetchResult { Error = "Không tìm thấy thư mục SaveGame để lưu trận đấu." };
            }

            // Write only whole operations. The rest of the download is kept as the stream's
            // starting leftover so the next tail continues from the right place - the file on
            // disk is shorter than what was fetched, which is why the two are counted apart.
            int keep = analysis.HeaderLength + analysis.BodyBytes;
            byte[] toWrite = analysis.NeedsRepair ? analysis.RepairedData : Trim(data, keep);
            byte[] pending = data.Length > keep ? data[keep..] : Array.Empty<byte>();

            string safeHost = string.Join("_", (hostLabel ?? hostIp).Split(Path.GetInvalidFileNameChars()));
            string path = Path.Combine(dir, $"{RecordedGameFinder.LivePrefix}{safeHost}.mgz");
            try
            {
                File.WriteAllBytes(path, toWrite);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"LiveShareClient: cannot write '{path}': {ex.Message}");
                return new FetchResult { Error = $"Không lưu được file trận đấu: {ex.Message}" };
            }

            DebugLogger.Info($"LiveShareClient: fetched {data.Length} bytes from {hostIp} " +
                             $"({analysis.Operations} ops, {toWrite.Length} written) -> {path}");
            return new FetchResult
            {
                Path = path,
                MatchName = matchName,
                RawBytes = data.Length,
                Pending = pending,
                Operations = analysis.Operations,
                InGame = inGame
            };
        }

        /// <summary>
        /// Asks the host for whatever it has added past <paramref name="from"/>.
        ///
        /// With <paramref name="waitSeconds"/> above zero the host holds the request open
        /// until it has something, so new data arrives as soon as it is captured instead of
        /// on the next poll - the difference between a replay that trickles and one that
        /// lands in lumps. An empty result still means "nothing new", which after a wait just
        /// means the host was quiet for that long.
        /// </summary>
        public static async Task<TailResult> TryFetchTailAsync(
            string hostIp, long from, int waitSeconds = 0, CancellationToken token = default)
        {
            string url = $"http://{hostIp}:{LiveShareServer.Port}/live/record?from={from}";
            if (waitSeconds > 0) url += $"&wait={waitSeconds}";

            // Bound each attempt itself rather than relying on HttpClient.Timeout, which is
            // shared with the short status probes and would have to be raised for everyone.
            using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(waitSeconds + 10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(attempt.Token, token);
            try
            {
                using HttpResponseMessage response = await Http.GetAsync(
                    url, HttpCompletionOption.ResponseContentRead, linked.Token);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // The host dropped the match entirely - treat as end of stream, not an error.
                    return new TailResult { TotalBytes = -1 };
                }
                response.EnsureSuccessStatusCode();
                return new TailResult
                {
                    Data = await response.Content.ReadAsByteArrayAsync(linked.Token),
                    MatchName = Header(response, "X-Match-Name"),
                    TotalBytes = long.TryParse(Header(response, "X-Total-Bytes"), out long total) ? total : 0,
                    InGame = Header(response, "X-In-Game") == "true"
                };
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                // Carry the reason back rather than only logging it: "could not reach them" with
                // no cause is the least useful thing an activity log can say.
                string reason = attempt.IsCancellationRequested
                    ? $"quá {waitSeconds + 10} giây không trả lời"
                    : ex.Message;
                DebugLogger.Info($"LiveShareClient: tail from {hostIp} at {from} failed: {ex.GetType().Name}: {ex.Message}");
                return new TailResult { Error = reason };
            }
        }

        private static string Header(HttpResponseMessage response, string name) =>
            response.Headers.TryGetValues(name, out System.Collections.Generic.IEnumerable<string> values)
                ? System.Linq.Enumerable.FirstOrDefault(values)
                : null;

        private static byte[] Trim(byte[] data, int length)
        {
            if (length >= data.Length) return data;
            var trimmed = new byte[length];
            Buffer.BlockCopy(data, 0, trimmed, 0, length);
            return trimmed;
        }
    }
}
