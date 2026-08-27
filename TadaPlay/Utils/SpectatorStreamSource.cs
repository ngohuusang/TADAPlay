using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Feeds the live-share snapshot from the game's OWN spectator stream on
    /// <c>127.0.0.1:53754</c>, instead of the periodic volume-shadow-copy read.
    ///
    /// While a match is running (with Record Game and Allow Spectators on), the game serves
    /// its in-progress recorded game on that port as a continuous stream. The wire format,
    /// captured and verified on the VPN, is:
    ///
    ///   [256-byte "WololoKingdoms" wrapper]  magic + format ("mgz") + host name + sizes
    ///   [standard .mgz]                      real header length, then a body that GROWS live
    ///
    /// Two properties make this a better source than the shadow copy:
    ///
    /// - It is the game engine's own stream, delivered in real time, so viewers trail the host
    ///   by network latency rather than by a whole capture interval.
    /// - The .mgz already carries its real header length (the shadow-copy read sees a zero
    ///   placeholder mid-match and has to recover and repair it).
    ///
    /// One hard limitation, established by packet capture: the game serves this port to
    /// LOOPBACK ONLY - a remote peer connects but is never sent a byte. That is why remote
    /// spectating cannot read it directly; TadaPlay reads it here on the host's own machine and
    /// relays it to remote viewers through <see cref="Connections.LiveShareServer"/> exactly as
    /// before. This class only strips the wrapper and writes the mgz, append-only, to the same
    /// snapshot file that server already serves, so nothing downstream changes.
    /// </summary>
    public sealed class SpectatorStreamSource : IDisposable
    {
        /// <summary>The game's own spectator port. Served on loopback only.</summary>
        public const int GamePort = 53754;

        /// <summary>Fixed wrapper the game prepends before the mgz stream.</summary>
        private const int WrapperBytes = 256;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("WololoKingdoms");

        /// <summary>How often the match clock (from the mgz body) is recomputed and published.</summary>
        private static readonly TimeSpan DurationEvery = TimeSpan.FromSeconds(3);

        private readonly string _snapshotPath;
        private readonly Action<string, bool> _report;   // message, isProblem
        private readonly CancellationTokenSource _cancel = new();

        private Thread _thread;
        private long _lastDurationCheck;
        private DateTime _lastDurationUtc = DateTime.MinValue;

        /// <summary>True while the stream is connected and feeding the snapshot.</summary>
        public bool IsRunning { get; private set; }

        /// <summary>Bytes of mgz written so far (excludes the stripped wrapper).</summary>
        public long BytesWritten { get; private set; }

        /// <summary>Host name the wrapper reported, once known.</summary>
        public string HostName { get; private set; }

        public SpectatorStreamSource(string recordPath, Action<string, bool> report = null)
        {
            _snapshotPath = LiveRecordSnapshotStore.PathFor(recordPath);
            _report = report ?? ((_, __) => { });
        }

        /// <summary>
        /// Connects, verifies the wrapper, and starts feeding the snapshot on a background
        /// thread. Returns false without side effects when the port is not serving (game not in
        /// a match, Record Game/Allow Spectators off, or a build without the stream) so the
        /// caller can fall back to the shadow-copy capture.
        /// </summary>
        public bool TryStart()
        {
            if (string.IsNullOrWhiteSpace(_snapshotPath)) return false;

            Socket socket = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    ReceiveTimeout = 4000
                };
                var connect = socket.BeginConnect("127.0.0.1", GamePort, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(3)) || !socket.Connected)
                {
                    DebugLogger.Info("SpectatorStreamSource: 127.0.0.1:53754 not serving yet.");
                    socket.Close();
                    return false;
                }
                socket.EndConnect(connect);

                // The game speaks first with the wrapper. If nothing arrives, this build/state
                // is not streaming - hand back to the shadow-copy path rather than blocking.
                byte[] wrapper = ReadExact(socket, WrapperBytes, TimeSpan.FromSeconds(4));
                if (wrapper == null || !StartsWith(wrapper, Magic))
                {
                    DebugLogger.Info("SpectatorStreamSource: no WololoKingdoms wrapper - not a live stream.");
                    socket.Close();
                    return false;
                }

                HostName = ReadCString(wrapper, 0x40, 16);
                DebugLogger.Info($"SpectatorStreamSource: connected; host='{HostName}', " +
                                 $"format='{ReadCString(wrapper, 0x20, 12)}', writing to '{_snapshotPath}'.");

                IsRunning = true;
                _thread = new Thread(() => Pump(socket)) { IsBackground = true, Name = "SpectatorStream" };
                _thread.Start();
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Info($"SpectatorStreamSource: cannot start: {ex.Message}");
                try { socket?.Close(); } catch { /* ignore */ }
                return false;
            }
        }

        private void Pump(Socket socket)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                // Fresh file: the stream restarts the record from the beginning, so what is on
                // disk must be exactly what has arrived - never appended onto an older match.
                using var fs = new FileStream(_snapshotPath, FileMode.Create, FileAccess.Write,
                                              FileShare.ReadWrite | FileShare.Delete);
                socket.ReceiveTimeout = 0; // block for live data; the match may be quiet between ops
                while (!_cancel.IsCancellationRequested)
                {
                    int n;
                    try { n = socket.Receive(buffer); }
                    catch (SocketException) { break; } // host closed - match ended
                    if (n <= 0) break;

                    fs.Write(buffer, 0, n);
                    fs.Flush();
                    BytesWritten += n;
                    MaybePublishDuration();
                }
                DebugLogger.Info($"SpectatorStreamSource: stream ended, {BytesWritten} bytes written.");
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"SpectatorStreamSource: pump failed: {ex.Message}");
                _report($"[Xem] Luồng spectator của game dừng ({ex.Message}).", true);
            }
            finally
            {
                IsRunning = false;
                try { socket.Close(); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Recomputes the match clock from the mgz body now and then, so viewers are told how
        /// far into the match they would be joining. Kept off the write path's hot loop.
        /// </summary>
        private void MaybePublishDuration()
        {
            if (DateTime.UtcNow - _lastDurationUtc < DurationEvery) return;
            if (BytesWritten == _lastDurationCheck) return;
            _lastDurationUtc = DateTime.UtcNow;
            _lastDurationCheck = BytesWritten;
            try
            {
                LiveRecordReader.RecordAnalysis analysis = LiveRecordReader.AnalyzeFile(_snapshotPath);
                if (analysis != null && analysis.BodyBytes > 0)
                {
                    MatchShareState.DurationMs = analysis.DurationMs;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"SpectatorStreamSource: duration walk failed: {ex.Message}");
            }
        }

        private static byte[] ReadExact(Socket socket, int count, TimeSpan timeout)
        {
            var buf = new byte[count];
            int got = 0;
            var deadline = DateTime.UtcNow + timeout;
            while (got < count)
            {
                if (DateTime.UtcNow > deadline) return null;
                int n;
                try { n = socket.Receive(buf, got, count - got, SocketFlags.None); }
                catch (SocketException) { return null; }
                if (n <= 0) return null;
                got += n;
            }
            return buf;
        }

        private static bool StartsWith(byte[] data, byte[] prefix)
        {
            if (data.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
            {
                if (data[i] != prefix[i]) return false;
            }
            return true;
        }

        private static string ReadCString(byte[] data, int offset, int max)
        {
            if (offset < 0 || offset >= data.Length) return "";
            int end = offset;
            int limit = Math.Min(data.Length, offset + max);
            while (end < limit && data[end] != 0) end++;
            return Encoding.ASCII.GetString(data, offset, end - offset);
        }

        public void Dispose()
        {
            try
            {
                _cancel.Cancel();
                IsRunning = false;
                _cancel.Dispose();
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"SpectatorStreamSource: dispose failed: {ex.Message}");
            }
        }
    }
}