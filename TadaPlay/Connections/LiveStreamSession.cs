using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TadaPlay.Logger;
using TadaPlay.Utils;

namespace TadaPlay.Connections
{
    /// <summary>
    /// Keeps a downloaded match growing while the host is still playing it.
    ///
    /// The opening download (<see cref="LiveShareClient.TryFetchAsync"/>) only gets the match
    /// as far as the host's last capture. On its own that gives a replay that ends a minute
    /// in and has to be re-downloaded and re-launched to see any more. This session instead
    /// polls the host for the bytes added since, and appends them to the very same file the
    /// game already has open, so the replay simply keeps going.
    ///
    /// Two rules make appending safe:
    ///
    /// - Only whole operations are ever written. A chunk off the wire almost never ends on an
    ///   operation boundary, so the ragged tail is held back until the next chunk completes
    ///   it; the game never reads half an operation.
    /// - Bytes taken from the host and bytes written to disk are counted separately. They
    ///   differ by whatever is currently held back, and confusing the two would resume from
    ///   the wrong offset and splice garbage into the middle of the match.
    ///
    /// Whether the game picks up the new bytes at all depends on it re-reading the file as it
    /// plays rather than slurping it at load, and on it not holding the file against writers.
    /// If it does hold it, the append fails with a sharing violation - which is reported
    /// rather than swallowed, and the bytes are kept for the next attempt.
    /// </summary>
    public sealed class LiveStreamSession : IDisposable
    {
        /// <summary>
        /// How long the host may hold a request open waiting for new data. This is what makes
        /// the stream continuous rather than lumpy: instead of asking every few seconds and
        /// usually being told "nothing yet", the viewer asks once and is answered the moment
        /// the host captures, so data arrives within a fraction of a second of existing.
        /// </summary>
        private const int LongPollSeconds = 25;

        /// <summary>
        /// Floor on how fast the loop may spin when a request comes back empty immediately.
        /// A host that ignores "wait" - an older build - would otherwise be hammered flat out.
        /// </summary>
        private const int MinCycleSeconds = 3;

        /// <summary>Backoff between retries, in seconds, holding at the last value.</summary>
        private static readonly int[] RetryDelaySeconds = { 2, 3, 5, 5, 10, 10, 15, 20, 30 };

        /// <summary>
        /// How long the host may stay unreachable before the stream is abandoned.
        ///
        /// Generous on purpose. A match runs for half an hour or more, and the earlier limit -
        /// eight tries, five seconds apart - gave up inside a minute, so a single blip ended
        /// spectating for the whole game with no way back except relaunching. Whatever is
        /// behind an outage, outlasting it is nearly always better than quitting: the host is
        /// still playing and the record is still growing, so there is something to come back
        /// to right up until the match ends.
        /// </summary>
        private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Consecutive failures before the player is told. A single missed request is normal
        /// on a VPN - and the host stalls briefly during each shadow copy - so reporting the
        /// first one produced a stream of "lost them"/"got them back" pairs that said nothing.
        /// </summary>
        private const int FailuresBeforeReporting = 3;

        /// <summary>
        /// Cap on bytes held back waiting to become a whole operation. Anything near this
        /// means the stream has desynchronised rather than merely arrived mid-operation.
        /// </summary>
        private const int MaxPendingBytes = 4 * 1024 * 1024;

        private readonly string _hostIp;
        private readonly string _hostLabel;
        private readonly string _path;
        private readonly string _matchName;
        private readonly Action<string, bool> _report;   // message, isProblem
        private readonly CancellationTokenSource _cancel = new();

        /// <summary>How often the viewer's activity log gets a progress line.</summary>
        private static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(60);

        private long _fetched;          // bytes taken from the host
        private byte[] _pending;        // fetched, not yet written
        private long _written;          // bytes appended since the opening download
        private int _operations;
        private bool _warnedLocked;
        private int _polls;
        private int _appends;
        private bool _sawHostQuit;
        private DateTime _lastProgressUtc = DateTime.UtcNow;

        public bool IsRunning { get; private set; }

        public LiveStreamSession(string hostIp, string hostLabel,
                                 LiveShareClient.FetchResult start,
                                 Action<string, bool> report)
        {
            _hostIp = hostIp;
            _hostLabel = hostLabel ?? hostIp;
            _path = start.Path;
            _matchName = start.MatchName;
            _fetched = start.RawBytes;
            _pending = start.Pending ?? Array.Empty<byte>();
            _report = report ?? ((_, __) => { });
        }

        public void Start()
        {
            if (IsRunning) return;
            IsRunning = true;
            DebugLogger.Info($"LiveStreamSession: following {_hostLabel} ({_hostIp}) from offset " +
                             $"{_fetched} ({_pending.Length} bytes held back), match '{_matchName}', " +
                             $"writing to '{_path}'; long-polling with wait={LongPollSeconds}s.");
            _ = Task.Run(() => RunAsync(_cancel.Token));
        }

        private async Task RunAsync(CancellationToken token)
        {
            int failures = 0;
            try
            {
                bool reportedLoss = false;
                DateTime failingSince = DateTime.UtcNow;
                while (!token.IsCancellationRequested)
                {
                    DateTime cycleStart = DateTime.UtcNow;

                    // The host holds this open until it has something, so there is no delay
                    // before asking - the waiting happens inside the request.
                    LiveShareClient.TailResult tail =
                        await LiveShareClient.TryFetchTailAsync(_hostIp, _fetched, LongPollSeconds, token);

                    _polls++;
                    if (tail.Failed)
                    {
                        if (failures == 0) failingSince = DateTime.UtcNow;
                        failures++;
                        TimeSpan down = DateTime.UtcNow - failingSince;
                        DebugLogger.Warn($"LiveStreamSession: poll {_polls} to {_hostLabel} failed " +
                                         $"({failures} in a row, {down.TotalSeconds:F0}s down): {tail.Error}");

                        if (down >= GiveUpAfter)
                        {
                            Finish($"[Xem] Mất kết nối tới {_hostLabel} quá {GiveUpAfter.TotalMinutes:F0} " +
                                   $"phút - dừng cập nhật trận đấu ({tail.Error}).", true);
                            return;
                        }
                        if (failures == FailuresBeforeReporting)
                        {
                            reportedLoss = true;
                            _report($"[Xem] Đang mất liên lạc với {_hostLabel} ({tail.Error}) - " +
                                    $"sẽ tự thử lại cho tới khi kết nối lại được.", true);
                        }

                        int step = Math.Min(failures - 1, RetryDelaySeconds.Length - 1);
                        await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds[step]), token);
                        continue;
                    }
                    if (failures > 0)
                    {
                        // Only say "reconnected" if the player was told there was a problem;
                        // otherwise a blip nobody saw produces a reassurance about nothing.
                        if (reportedLoss)
                        {
                            _report($"[Xem] Đã kết nối lại với {_hostLabel} sau " +
                                    $"{(DateTime.UtcNow - failingSince).TotalSeconds:F0} giây.", false);
                            reportedLoss = false;
                        }
                        DebugLogger.Info($"LiveStreamSession: {_hostLabel} reachable again after " +
                                         $"{failures} failed polls.");
                        failures = 0;
                    }

                    if (tail.TotalBytes < 0)
                    {
                        Finish($"[Xem] {_hostLabel} không còn chia sẻ trận này nữa.", false);
                        return;
                    }

                    // A different match, or an offset past the end of the file being served,
                    // both mean the host has moved on. Appending either would splice two
                    // different games into one file.
                    if (!string.IsNullOrEmpty(_matchName) && !string.IsNullOrEmpty(tail.MatchName)
                        && !string.Equals(_matchName, tail.MatchName, StringComparison.OrdinalIgnoreCase))
                    {
                        Finish($"[Xem] {_hostLabel} đã bắt đầu trận mới - bấm 'Xem trận đấu' để xem trận đó.",
                               false);
                        return;
                    }
                    if (tail.TotalBytes > 0 && _fetched > tail.TotalBytes)
                    {
                        Finish($"[Xem] Dữ liệu trận đấu của {_hostLabel} đã thay đổi - dừng cập nhật.", true);
                        return;
                    }

                    // Worth calling out once: from here the host is no longer playing, so what
                    // is left is the tail of a finished match rather than a live feed.
                    if (!tail.InGame && !_sawHostQuit)
                    {
                        _sawHostQuit = true;
                        _report($"[Xem] {_hostLabel} đã thoát game - đang tải nốt phần cuối của trận.",
                                false);
                    }

                    if (tail.Data.Length > 0)
                    {
                        DebugLogger.Info($"LiveStreamSession: poll {_polls} got {tail.Data.Length} bytes " +
                                         $"from offset {_fetched} (host has {tail.TotalBytes}, " +
                                         $"inGame={tail.InGame}).");
                        _fetched += tail.Data.Length;
                        Append(tail.Data);
                        ReportProgress(force: false);
                    }
                    else if (!tail.InGame && _pending.Length == 0)
                    {
                        Finish($"[Xem] Trận của {_hostLabel} đã kết thúc - đã nhận đủ dữ liệu " +
                               $"({_written / 1024} KB, {_operations} thao tác qua {_polls} lần hỏi).",
                               false);
                        return;
                    }
                    else
                    {
                        // Normal between the host's captures: nothing new yet. File log only -
                        // one of these every poll would drown the activity log.
                        DebugLogger.Info($"LiveStreamSession: poll {_polls} to {_hostLabel} - nothing new " +
                                         $"(at {_fetched}/{tail.TotalBytes}, inGame={tail.InGame}).");
                        ReportProgress(force: false);

                        // A host that honours "wait" has already burned the time inside the
                        // request; one that does not would send us straight round again.
                        TimeSpan spent = DateTime.UtcNow - cycleStart;
                        TimeSpan floor = TimeSpan.FromSeconds(MinCycleSeconds);
                        if (spent < floor) await Task.Delay(floor - spent, token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // stopped on purpose
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"LiveStreamSession: {_hostIp} stream failed: {ex.Message}");
                Finish($"[Xem] Lỗi khi cập nhật trận đấu: {ex.Message}", true);
            }
            finally
            {
                IsRunning = false;
            }
        }

        /// <summary>Appends the whole operations at the front of what has arrived; keeps the rest.</summary>
        private void Append(byte[] chunk)
        {
            byte[] buffer = Combine(_pending, chunk);
            int consumed = LiveRecordReader.WalkAppended(buffer, 0, out int operations);

            if (consumed <= 0)
            {
                if (buffer.Length > MaxPendingBytes)
                {
                    Finish("[Xem] Dữ liệu nhận về không đọc được - dừng cập nhật trận đấu.", true);
                    _cancel.Cancel();
                    return;
                }
                _pending = buffer;   // not a whole operation yet - wait for the next chunk
                return;
            }

            try
            {
                // FileShare.ReadWrite so the game replaying this file is never blocked by the
                // write; it says nothing about whether the game lets US in, which is what the
                // sharing violation below would mean.
                using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write,
                                              FileShare.ReadWrite | FileShare.Delete);
                fs.Write(buffer, 0, consumed);
            }
            catch (IOException ex)
            {
                // Keep everything: the bytes are still owed to the file, and the next poll
                // retries the whole buffer once the game releases it.
                _pending = buffer;
                if (!_warnedLocked)
                {
                    _warnedLocked = true;
                    _report($"[Xem] Chưa ghi thêm được vào file trận đấu (game đang mở file): {ex.Message}", true);
                }
                DebugLogger.Warn($"LiveStreamSession: cannot append to '{_path}': {ex.Message}");
                return;
            }

            _warnedLocked = false;
            _pending = consumed >= buffer.Length ? Array.Empty<byte>() : buffer[consumed..];
            _written += consumed;
            _operations += operations;
            _appends++;
            DebugLogger.Info($"LiveStreamSession: append {_appends} wrote {consumed} bytes " +
                             $"({operations} ops) from {_hostLabel}; {_pending.Length} held back; " +
                             $"total {_written} bytes / {_operations} ops.");

            // The single most useful line in the log: it proves the game is being fed data
            // it did not have when it launched, which is the whole point of streaming.
            if (_appends == 1)
            {
                _report($"[Xem] Đã nhận phần mới đầu tiên từ {_hostLabel} " +
                        $"({consumed / 1024} KB, {operations} thao tác) - trận đang được cập nhật.",
                        false);
                _lastProgressUtc = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Puts a progress line in the activity log at most once a minute. The poll loop runs
        /// every few seconds, so reporting every pass would bury everything else.
        /// </summary>
        private void ReportProgress(bool force)
        {
            if (!force && DateTime.UtcNow - _lastProgressUtc < ProgressEvery) return;
            _lastProgressUtc = DateTime.UtcNow;
            if (_appends == 0) return;   // nothing has arrived yet; the launch line still stands

            string held = _pending.Length > 0 ? $", đang chờ {_pending.Length} byte" : "";
            _report($"[Xem] Đang xem {_hostLabel}: đã nhận thêm {_written / 1024} KB " +
                    $"({_operations} thao tác{held}).", false);
        }

        private void Finish(string message, bool isProblem)
        {
            IsRunning = false;
            DebugLogger.Info($"LiveStreamSession: finished following {_hostLabel} - {_polls} polls, " +
                             $"{_appends} appends, {_written} bytes, {_operations} ops, " +
                             $"{_pending.Length} never written. Reason: {message}");
            _report(message, isProblem);
        }

        private static byte[] Combine(byte[] first, byte[] second)
        {
            if (first == null || first.Length == 0) return second;
            var combined = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, combined, 0, first.Length);
            Buffer.BlockCopy(second, 0, combined, first.Length, second.Length);
            return combined;
        }

        public void Dispose()
        {
            try
            {
                if (IsRunning)
                {
                    DebugLogger.Info($"LiveStreamSession: stopping the {_hostLabel} stream early - " +
                                     $"{_polls} polls, {_appends} appends, {_written} bytes written.");
                }
                IsRunning = false;
                _cancel.Cancel();
                _cancel.Dispose();
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"LiveStreamSession: stop failed: {ex.Message}");
            }
        }
    }
}
