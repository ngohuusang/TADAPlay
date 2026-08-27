using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TadaPlay.Logger;
using TadaPlay.Utils;

namespace TadaPlay.Connections
{
    /// <summary>
    /// Serves this player's most recent finished match to anyone who wants to watch it.
    ///
    /// UserPatch's own spectator cannot be used here - it reports "Could not locate game
    /// expansion." against this install and only ever launches stock age2_x1.5.exe - so
    /// watching happens the long way round: once a match ends its record is published over
    /// the WireGuard subnet and a viewer plays it as an ordinary recorded game.
    ///
    /// A match in progress is served too, from the periodic volume-shadow-copy capture -
    /// never by reading the record the game still has open, which corrupts it (measured,
    /// not assumed). Viewers therefore trail the host by at least one capture interval,
    /// which is the property that keeps this from being a ghosting tool.
    ///
    /// Requests carry a "from" offset so a viewer can keep pulling just the new tail for
    /// the whole match instead of re-downloading it - see LiveStreamSession.
    ///
    /// Everyone is on the VPN subnet (10.10.0.0/16), so no port forwarding is involved.
    /// </summary>
    public sealed class LiveShareServer : IDisposable
    {
        /// <summary>Port TadaPlay shares matches on. Not the game's own spectator port 53754.</summary>
        public const int Port = 53755;

        private const string RecordPath = "/live/record";
        private const string StatusPath = "/live/status";

        /// <summary>
        /// Longest a long-poll is held open. Short enough to stay well inside any proxy or
        /// firewall idle timeout and to let a viewer notice the match ended, long enough that
        /// a quiet stretch does not turn into a burst of reconnects.
        /// </summary>
        private const int MaxWaitSeconds = 25;

        /// <summary>How often a held request re-checks for new data.</summary>
        private const int WaitPollMs = 200;

        private HttpListener _listener;
        private CancellationTokenSource _cancel;

        public bool IsRunning => _listener?.IsListening == true;

        /// <summary>
        /// Starts serving. Binding to "+" needs administrator, which TadaPlay's manifest
        /// already requests; without it Windows refuses the reservation.
        /// </summary>
        public bool Start()
        {
            if (IsRunning) return true;
            try
            {
                // Without this, Windows Firewall silently drops the peer's request and the
                // match simply never appears for them - which looks like "sharing is broken"
                // rather than "a rule is missing". TadaPlay already runs elevated, so it can
                // add the rule itself instead of relying on the installer or a UAC prompt.
                EnsureFirewallRule();

                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://+:{Port}/live/");
                _listener.Start();
                _cancel = new CancellationTokenSource();
                _ = Task.Run(() => AcceptLoopAsync(_cancel.Token));
                DebugLogger.Info($"LiveShareServer: sharing finished matches on port {Port}.");
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"LiveShareServer: cannot listen on {Port}: {ex.Message}");
                _listener = null;
                return false;
            }
        }

        /// <summary>Name of the inbound rule; reused so repeated starts don't stack duplicates.</summary>
        private const string FirewallRuleName = "TadaPlay match sharing";

        /// <summary>
        /// Opens the share port for inbound traffic, once. Failure is not fatal: sharing still
        /// works between machines whose firewall already allows it, and the download side is
        /// outbound so it is never blocked.
        /// </summary>
        private static void EnsureFirewallRule()
        {
            try
            {
                if (RunNetsh($"advfirewall firewall show rule name=\"{FirewallRuleName}\"") == 0)
                {
                    return; // already present
                }

                int code = RunNetsh($"advfirewall firewall add rule name=\"{FirewallRuleName}\" " +
                                    $"dir=in action=allow protocol=TCP localport={Port} " +
                                    "profile=any description=\"Cho phep nguoi choi khac xem lai tran dau\"");
                if (code == 0)
                {
                    DebugLogger.Info($"LiveShareServer: added firewall rule for TCP {Port}.");
                }
                else
                {
                    DebugLogger.Warn($"LiveShareServer: netsh returned {code} adding the firewall rule; " +
                                     "other players may not be able to reach this machine.");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"LiveShareServer: cannot configure the firewall: {ex.Message}");
            }
        }

        private static int RunNetsh(string arguments)
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("netsh", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo);
            if (process == null) return -1;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(15000);
            return process.HasExited ? process.ExitCode : -1;
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener?.IsListening == true)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception) when (token.IsCancellationRequested || _listener == null)
                {
                    return; // shutting down
                }
                catch (Exception ex)
                {
                    DebugLogger.Warn($"LiveShareServer: accept failed: {ex.Message}");
                    continue;
                }

                // Each request runs on its own, deliberately not awaited: a long-poll holds its
                // response open for up to 25 seconds, and handling requests one at a time
                // would mean a single waiting viewer froze status probes and every other
                // viewer for that whole time.
                _ = HandleSafelyAsync(context, token);
            }
        }

        private static async Task HandleSafelyAsync(HttpListenerContext context, CancellationToken token)
        {
            try
            {
                await HandleAsync(context, token);
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"LiveShareServer: request failed: {ex.Message}");
            }
            finally
            {
                try { context.Response.Close(); } catch (Exception) { /* client gone */ }
            }
        }

        private static async Task HandleAsync(HttpListenerContext context, CancellationToken token)
        {
            string path = context.Request.Url?.AbsolutePath ?? "";
            string match = CurrentShareable();

            if (path.Equals(StatusPath, StringComparison.OrdinalIgnoreCase))
            {
                WriteStatus(context, match);
                return;
            }

            if (!path.Equals(RecordPath, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 404;
                return;
            }

            if (match == null || !File.Exists(match))
            {
                context.Response.StatusCode = 404;
                Write(context, "text/plain", Encoding.UTF8.GetBytes("no finished match to share"));
                return;
            }

            // "from" makes this resumable, which is what turns a one-shot download into a
            // stream: a viewer asks for everything once and then only ever for the bytes
            // added since, every few seconds, for the rest of the match. Successive captures
            // of the same match are strict byte prefixes of each other - the record is
            // append-only and each capture is trimmed to a whole operation - so a byte offset
            // stays meaningful across them, including across the final publish at match end.
            long from = 0;
            string fromText = context.Request.QueryString["from"];
            if (fromText != null && long.TryParse(fromText, out long parsed) && parsed > 0)
            {
                from = parsed;
            }

            // "wait" turns polling into pushing. Rather than the viewer asking every few
            // seconds and mostly being told "nothing yet" - which makes the replay arrive in
            // lumps, one per capture - it asks once and the answer is held back until there
            // IS something, so new data leaves this machine within a fraction of a second of
            // being captured. A viewer that does not ask to wait still gets the old
            // answer-immediately behaviour.
            int wait = 0;
            string waitText = context.Request.QueryString["wait"];
            if (waitText != null && int.TryParse(waitText, out int requested))
            {
                wait = Math.Clamp(requested, 0, MaxWaitSeconds);
            }

            if (wait > 0)
            {
                match = await WaitForDataAsync(match, from, wait, token);
                if (match == null || !File.Exists(match))
                {
                    context.Response.StatusCode = 404;
                    return; // the match went away while we were holding the request
                }
            }

            byte[] data;
            long total;
            try
            {
                // Read with sharing so publishing the next match cannot fail an in-flight download.
                using var fs = new FileStream(match, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
                total = fs.Length;
                if (from >= total)
                {
                    data = Array.Empty<byte>(); // caught up - nothing new since last time
                }
                else
                {
                    fs.Seek(from, SeekOrigin.Begin);
                    data = new byte[total - from];
                    int read = 0;
                    while (read < data.Length)
                    {
                        int n = fs.Read(data, read, data.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read != data.Length) Array.Resize(ref data, read);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"LiveShareServer: cannot read '{match}': {ex.Message}");
                context.Response.StatusCode = 503;
                return;
            }

            // The name lets a viewer notice the host has moved on to a different match, and
            // the total lets it notice its offset no longer belongs to this file at all;
            // either way it must stop appending rather than splice two matches together.
            context.Response.AddHeader("X-Match-Name", Path.GetFileNameWithoutExtension(match));
            context.Response.AddHeader("X-Total-Bytes", total.ToString(CultureInfo.InvariantCulture));
            context.Response.AddHeader("X-In-Game", MatchShareState.InGame ? "true" : "false");
            Write(context, "application/octet-stream", data);

            NoteWatcher(context.Request.RemoteEndPoint?.Address?.ToString(), from, data.Length, total);
        }

        /// <summary>
        /// The match this machine may hand out right now.
        ///
        /// While a game is running that is strictly the running game's own snapshot, which does
        /// not exist until the first capture - so a player who has just started a second game
        /// correctly reports "nothing to watch yet" instead of offering the previous match.
        /// Between games it is simply the most recent snapshot, which is the finished match.
        /// </summary>
        private static string CurrentShareable()
        {
            string inProgress = MatchShareState.CurrentRecordPath;
            return inProgress != null
                ? LiveRecordSnapshotStore.FindFor(inProgress)
                : LiveRecordSnapshotStore.Current();
        }

        /// <summary>
        /// Holds a request until the shared match grows past <paramref name="from"/>, the
        /// deadline passes, or the match is replaced. Returns the match to serve.
        ///
        /// Re-reads which match is current on every pass rather than trusting the one the
        /// request arrived with: a capture replaces the file underneath, and the match can be
        /// swapped entirely if the player starts a new game while somebody is waiting.
        /// </summary>
        private static async Task<string> WaitForDataAsync(string match, long from, int waitSeconds,
                                                           CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
            while (!token.IsCancellationRequested)
            {
                string current = CurrentShareable();
                if (current == null) return null;

                // A different match means this viewer's offset is meaningless; hand it back
                // straight away so the client can notice the name changed and stop.
                if (!string.Equals(current, match, StringComparison.OrdinalIgnoreCase)) return current;

                try
                {
                    if (new FileInfo(current).Length > from) return current;
                }
                catch (Exception)
                {
                    return current; // let the normal read path report the real problem
                }

                if (DateTime.UtcNow >= deadline) return current;
                await Task.Delay(WaitPollMs, token);
            }
            return match;
        }

        #region Who is watching

        /// <summary>
        /// A viewer is considered gone once this long passes with no request from them.
        ///
        /// Must clear a long-poll plus a retry backoff, not just the poll: a viewer holding a
        /// 25-second request only refreshes this on completion, and one riding out a blip can
        /// go a good while longer. At 45s that produced "đã ngừng xem" immediately followed by
        /// "bắt đầu xem" for a viewer who never actually left.
        /// </summary>
        private static readonly TimeSpan WatcherIdleTimeout = TimeSpan.FromSeconds(90);

        private static readonly object WatcherGate = new();
        private static readonly System.Collections.Generic.Dictionary<string, Watcher> WatchersByAddress = new();

        /// <summary>What one viewer has pulled from this machine.</summary>
        public sealed class Watcher
        {
            public string Address { get; set; }
            public DateTime FirstSeenUtc { get; set; }
            public DateTime LastSeenUtc { get; set; }
            public long BytesServed { get; set; }
            public int Requests { get; set; }
            /// <summary>How much of the match they have pulled, as a share of what exists.</summary>
            public long Offset { get; set; }
        }

        /// <summary>
        /// Raised when someone starts or stops watching this player's match, so the activity
        /// log can say so. Static because the log lives in Home while the server is per-app.
        /// </summary>
        public static event Action<Watcher, bool> WatcherChanged;

        private static void NoteWatcher(string address, long from, int served, long total)
        {
            if (string.IsNullOrEmpty(address)) return;

            Watcher started = null;
            Watcher watcher;
            lock (WatcherGate)
            {
                if (!WatchersByAddress.TryGetValue(address, out watcher))
                {
                    watcher = new Watcher { Address = address, FirstSeenUtc = DateTime.UtcNow };
                    WatchersByAddress[address] = watcher;
                    started = watcher;
                }
                watcher.LastSeenUtc = DateTime.UtcNow;
                watcher.BytesServed += served;
                watcher.Requests++;
                watcher.Offset = from + served;
            }

            DebugLogger.Info($"LiveShareServer: {address} pulled {served} bytes from offset {from} " +
                             $"(now {watcher.Offset}/{total}, {watcher.Requests} requests, " +
                             $"{watcher.BytesServed / 1024} KB total, inGame={MatchShareState.InGame}).");

            if (started != null)
            {
                DebugLogger.Info($"LiveShareServer: {address} started watching.");
                WatcherChanged?.Invoke(started, true);
            }
        }

        /// <summary>
        /// Drops viewers who have gone quiet and announces them. Called on a timer rather than
        /// from the request path, because the moment worth reporting - the last viewer
        /// leaving - is by definition one where no more requests arrive to trigger a sweep.
        /// </summary>
        public static void SweepWatchers()
        {
            var gone = new System.Collections.Generic.List<Watcher>();
            lock (WatcherGate)
            {
                DateTime cutoff = DateTime.UtcNow - WatcherIdleTimeout;
                foreach (Watcher watcher in WatchersByAddress.Values)
                {
                    if (watcher.LastSeenUtc < cutoff) gone.Add(watcher);
                }
                foreach (Watcher watcher in gone) WatchersByAddress.Remove(watcher.Address);
            }

            foreach (Watcher watcher in gone)
            {
                DebugLogger.Info($"LiveShareServer: {watcher.Address} stopped watching after " +
                                 $"{(DateTime.UtcNow - watcher.FirstSeenUtc).TotalMinutes:F1} min, " +
                                 $"{watcher.BytesServed / 1024} KB over {watcher.Requests} requests.");
                WatcherChanged?.Invoke(watcher, false);
            }
        }

        /// <summary>Viewers currently pulling this player's match.</summary>
        public static System.Collections.Generic.IReadOnlyList<Watcher> CurrentWatchers()
        {
            lock (WatcherGate)
            {
                return System.Linq.Enumerable.ToList(WatchersByAddress.Values);
            }
        }

        #endregion

        private static void WriteStatus(HttpListenerContext context, string match)
        {
            bool available = match != null && File.Exists(match);
            string name = available ? Path.GetFileNameWithoutExtension(match) : "";
            long size = 0;
            string finishedUtc = "";
            if (available)
            {
                var fi = new FileInfo(match);
                size = fi.Length;
                finishedUtc = fi.LastWriteTimeUtc.ToString("o");
            }

            // Hand-built so the server has no serializer dependency; all values are ours.
            // inGame and waitSeconds let a viewer tell "no match" apart from "match started,
            // not captured yet" - the latter is a countdown, not a failure.
            string json = "{" +
                          $"\"hasMatch\":{(available ? "true" : "false")}," +
                          $"\"inGame\":{(MatchShareState.InGame ? "true" : "false")}," +
                          // Direct-probe fallback carries the pause flag too, so an overlay
                          // talking to a host whose lobby broadcast has not landed yet still
                          // shows it rather than silently reporting a frozen clock as normal.
                          $"\"paused\":{(MatchShareState.Paused ? "true" : "false")}," +
                          $"\"waitSeconds\":{MatchShareState.WaitSeconds}," +
                          $"\"gameMs\":{MatchShareState.DurationMs}," +
                          $"\"match\":\"{Escape(name)}\"," +
                          $"\"bytes\":{size}," +
                          $"\"finishedUtc\":\"{finishedUtc}\"" +
                          "}";
            Write(context, "application/json", Encoding.UTF8.GetBytes(json));
        }

        private static string Escape(string s) =>
            (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static void Write(HttpListenerContext context, string contentType, byte[] body)
        {
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = body.Length;
            context.Response.OutputStream.Write(body, 0, body.Length);
        }

        public void Dispose()
        {
            try
            {
                _cancel?.Cancel();
                if (_listener != null)
                {
                    HttpListener listener = _listener;
                    _listener = null;
                    listener.Stop();
                    listener.Close();
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"LiveShareServer: shutdown failed: {ex.Message}");
            }
            finally
            {
                _cancel?.Dispose();
                _cancel = null;
            }
        }
    }
}
