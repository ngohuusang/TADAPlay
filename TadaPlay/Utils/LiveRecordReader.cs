using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Reads the recorded game of a match that is still being played.
    ///
    /// The obvious approach - just open the .mgz - does not work, and the reason is
    /// worth writing down because it looks exactly like "the record isn't written
    /// until the game ends":
    ///
    /// 1. The game DOES write the record continuously (measured: ~3 KB/s, flushed
    ///    every ~3s). Nothing is missing from disk.
    /// 2. But it holds the file open WITHOUT FILE_SHARE_READ, so every CreateFile by
    ///    path fails with ERROR_SHARING_VIOLATION. Only FILE_READ_ATTRIBUTES opens
    ///    succeed - which is why <see cref="FileInfo.Length"/> works during a match
    ///    while <see cref="File.OpenRead"/> throws.
    /// 3. And while the match runs, the header-length dword at offset 0 is a zero
    ///    placeholder that the game patches only when it closes the file. That single
    ///    field makes every parser reject an in-progress record.
    ///
    /// So: duplicate the game's own handle (it grants FILE_READ_DATA), read through
    /// it, recover the real header length ourselves, and trim the body to the last
    /// complete operation. The result is a normal .mgz the server parses as usual.
    /// </summary>
    public static class LiveRecordReader
    {
        /// <summary>The 28-byte meta block that opens a UserPatch 1.5 body: log version 4, marker 500.</summary>
        private static readonly byte[] BodyMagic = { 0x04, 0, 0, 0, 0xF4, 0x01, 0, 0 };

        private static readonly string[] GameExeNames =
        {
            "age2_x1", "age2_x2", "empires2", "age2_x1.5", "age2-WK",
            "age2-WK-center", "WK", "age2_x1.0c", "age2_x1.4"
        };

        /// <summary>Header length is constant for a given match, so only discover it once per file.</summary>
        private static readonly Dictionary<string, int> HeaderLengthCache =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public sealed class Snapshot
        {
            /// <summary>Full path of the record the game currently has open.</summary>
            public string SourcePath { get; set; }
            /// <summary>A complete, parseable .mgz: repaired header + body trimmed to a whole operation.</summary>
            public byte[] Data { get; set; }
            public int HeaderLength { get; set; }
            public int BodyOperations { get; set; }
            public int BodyBytes { get; set; }
        }

        /// <summary>
        /// DO NOT USE on a match in progress - this corrupts the record being written.
        ///
        /// It reads through a duplicate of the game's file handle. That handle is synchronous,
        /// so the read shares one file pointer with the game and leaves it at the EOF observed
        /// when the read began; a write the game issues during the read is serialised behind it
        /// and then lands at that stale offset, duplicating ~20 bytes of the stream. Measured
        /// on a real 30-minute match: three breakages, and the resulting file makes mgz swallow
        /// the remainder silently rather than erroring.
        ///
        /// Kept only because it is the one way to read a file the game holds without
        /// FILE_SHARE_READ, which may still be useful for read-only diagnostics on a match
        /// nobody cares about. Matches are shared after they finish instead - see
        /// LiveRecordSnapshotStore.PublishFinished.
        /// </summary>
        /// <param name="pid">
        /// Process to read from; 0 (the default) finds the running game automatically.
        /// </param>
        public static bool TryCapture(out Snapshot snapshot, out string error, int pid = 0)
        {
            snapshot = null;
            error = null;

            Process game = null;
            if (pid == 0)
            {
                game = FindGameProcess();
                if (game == null)
                {
                    error = "Game không chạy.";
                    return false;
                }
                pid = game.Id;
            }

            try
            {
                byte[] raw = ReadLockedRecord(pid, out string path, out string readError);
                if (raw == null)
                {
                    error = readError;
                    return false;
                }

                int headerLength = DiscoverHeaderLength(path, raw);
                if (headerLength <= 0)
                {
                    error = "Header của record chưa ghi xong.";
                    return false;
                }

                int bodyBytes = WalkBody(raw, headerLength, out int operations, out long durationMs);
                if (bodyBytes <= 0)
                {
                    error = "Chưa có dữ liệu trận đấu trong record.";
                    return false;
                }

                var data = new byte[headerLength + bodyBytes];
                Buffer.BlockCopy(raw, 0, data, 0, headerLength + bodyBytes);
                // Stamp the real header length over the game's zero placeholder.
                BitConverter.GetBytes(headerLength).CopyTo(data, 0);

                snapshot = new Snapshot
                {
                    SourcePath = path,
                    Data = data,
                    HeaderLength = headerLength,
                    BodyOperations = operations,
                    BodyBytes = bodyBytes
                };
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"LiveRecordReader: capture failed: {ex.Message}");
                error = ex.Message;
                return false;
            }
            finally
            {
                game?.Dispose();
            }
        }

        /// <summary>What a record actually contains, and whether it needed fixing up.</summary>
        public sealed class RecordAnalysis
        {
            public int HeaderLength { get; set; }
            /// <summary>Bytes of the body that form whole operations - the comparable "how much match is in here".</summary>
            public int BodyBytes { get; set; }
            public int Operations { get; set; }
            /// <summary>Game time the walked operations cover - the match clock, in ms.</summary>
            public long DurationMs { get; set; }
            /// <summary>True when the header length had to be recovered rather than read.</summary>
            public bool NeedsRepair { get; set; }
            /// <summary>Set only when <see cref="NeedsRepair"/>; the corrected file contents.</summary>
            public byte[] RepairedData { get; set; }
        }

        /// <summary>
        /// Inspects record bytes: where the body starts, how much of it is intact, and
        /// whether the header length needs recovering. Returns null if this isn't a record.
        /// </summary>
        public static RecordAnalysis Analyze(byte[] raw, string pathForCache = null)
        {
            if (raw == null || raw.Length < 16) return null;

            int declared = BitConverter.ToInt32(raw, 0);
            bool healthy = declared > 8 && declared <= raw.Length && LooksLikeBodyStart(raw, declared);

            int headerLength = healthy ? declared : DiscoverHeaderLength(pathForCache, raw);
            if (headerLength <= 0) return null;

            int bodyBytes = WalkBody(raw, headerLength, out int operations, out long durationMs);
            if (bodyBytes <= 0) return null;

            var analysis = new RecordAnalysis
            {
                HeaderLength = headerLength,
                BodyBytes = bodyBytes,
                Operations = operations,
                DurationMs = durationMs,
                NeedsRepair = !healthy
            };

            if (!healthy)
            {
                var repaired = new byte[headerLength + bodyBytes];
                Buffer.BlockCopy(raw, 0, repaired, 0, repaired.Length);
                BitConverter.GetBytes(headerLength).CopyTo(repaired, 0);
                analysis.RepairedData = repaired;
            }
            return analysis;
        }

        /// <summary>Same as <see cref="Analyze(byte[], string)"/>, reading from disk. Null if unreadable.</summary>
        public static RecordAnalysis AnalyzeFile(string path)
        {
            try
            {
                return Analyze(ReadAllBytesShared(path), path);
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"LiveRecordReader: cannot analyze '{path}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Repairs a record the game left behind without patching its header length.
        ///
        /// The game writes that dword as zero for the whole match and only fills it in
        /// when it closes the file cleanly. If the player alt-F4s - or the game crashes -
        /// the record on disk is complete and correct except for that one field, and the
        /// server's parser rejects the whole file because of it. Verified on a real
        /// abandoned match: rejected outright before, and afterwards parsed as a 33-minute
        /// 4v4 with every player and both teams.
        ///
        /// Returns false when the file is already fine (or beyond help), in which case it
        /// should be uploaded unchanged.
        /// </summary>
        public static bool TryRepairFile(string path, out byte[] repaired, out string note)
        {
            repaired = null;
            note = null;

            RecordAnalysis analysis = AnalyzeFile(path);
            if (analysis == null)
            {
                note = "Không đọc/phân tích được file record.";
                return false;
            }
            if (!analysis.NeedsRepair) return false; // already good - leave it alone

            repaired = analysis.RepairedData;
            note = $"header_len=0 -> {analysis.HeaderLength}, {analysis.Operations} thao tác, " +
                   $"{analysis.BodyBytes} byte dữ liệu trận đấu";
            DebugLogger.Info($"LiveRecordReader: repaired '{Path.GetFileName(path)}': {note}");
            return true;
        }

        /// <summary>Returns the running AoE2 process, or null.</summary>
        public static Process FindGameProcess()
        {
            foreach (string name in GameExeNames)
            {
                Process[] found;
                try
                {
                    found = Process.GetProcessesByName(name);
                }
                catch (Exception ex)
                {
                    DebugLogger.Error($"LiveRecordReader: GetProcessesByName('{name}') failed: {ex.Message}");
                    continue;
                }
                if (found.Length == 0) continue;

                for (int i = 1; i < found.Length; i++) found[i].Dispose();
                return found[0];
            }
            return null;
        }

        /// <summary>
        /// Reads a file that something else may be writing right now.
        ///
        /// File.ReadAllBytes opens with FileShare.Read, meaning "other handles may read this,
        /// but not write it". The live snapshot is held open for WRITING by
        /// SpectatorStreamSource, so that request is refused - "the process cannot access the
        /// file because it is being used by another process" - even though the writer had
        /// explicitly allowed readers. Share flags have to be compatible in BOTH directions.
        ///
        /// That is why the match clock sat at 00:00 for a whole game on a live host: every
        /// three seconds the duration walk threw this, and both the stream's own clock and the
        /// backstop added to work around it failed for the same reason. The record parsing was
        /// never at fault; the file simply could not be opened.
        ///
        /// Length is re-read rather than trusted: the file is growing, so the amount actually
        /// read is what matters.
        /// </summary>
        private static byte[] ReadAllBytesShared(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            var data = new byte[stream.Length];
            int total = 0;
            while (total < data.Length)
            {
                int read = stream.Read(data, total, data.Length - total);
                if (read <= 0) break;
                total += read;
            }
            if (total != data.Length) Array.Resize(ref data, total);
            return data;
        }

        /// <summary>True when the file cannot be opened for reading yet - i.e. the game still owns it.</summary>
        public static bool IsLockedByGame(string path)
        {
            try
            {
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    return false;
                }
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        // ------------------------------------------------------------------
        // reading a file the game holds without FILE_SHARE_READ
        // ------------------------------------------------------------------

        /// <summary>
        /// Reads the whole record through a duplicate of the game's own handle.
        ///
        /// That handle is synchronous, so the duplicate shares ONE file pointer with the
        /// game - and the game only ever appends at it. Leaving the pointer short of EOF
        /// would make the game's next write land on top of data it already wrote. So this
        /// issues a single read covering [0, EOF), which leaves the pointer exactly at
        /// EOF where the game wants it, then snaps it back to FILE_END regardless. The
        /// kernel serialises I/O on a synchronous handle, so the read itself can never
        /// interleave with a write.
        /// </summary>
        private static byte[] ReadLockedRecord(int pid, out string path, out string error)
        {
            path = null;
            error = null;

            IntPtr dup = IntPtr.Zero;
            IntPtr hProc = OpenProcess(PROCESS_DUP_HANDLE | PROCESS_QUERY_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero)
            {
                error = $"Không mở được tiến trình game (lỗi {Marshal.GetLastWin32Error()}). " +
                        "TadaPlay cần chạy với quyền Administrator.";
                return null;
            }

            try
            {
                if (!TryFindRecordHandle(hProc, pid, out IntPtr handleValue, out path))
                {
                    error = "Game chưa mở file record nào.";
                    return null;
                }

                if (!DuplicateHandle(hProc, handleValue, GetCurrentProcess(), out dup,
                                     0, false, DUPLICATE_SAME_ACCESS))
                {
                    error = $"DuplicateHandle thất bại (lỗi {Marshal.GetLastWin32Error()}).";
                    return null;
                }

                if (!GetFileSizeEx(dup, out long size) || size <= 0 || size > int.MaxValue)
                {
                    error = $"Kích thước record không hợp lệ ({size}).";
                    return null;
                }

                var buffer = new byte[size];
                var overlapped = new NativeOverlapped
                {
                    OffsetLow = 0,
                    OffsetHigh = 0
                };
                bool ok = ReadFile(dup, buffer, (uint)size, out uint read, ref overlapped);
                int lastError = Marshal.GetLastWin32Error();

                // Put the shared pointer back at EOF no matter what happened above.
                SetFilePointerEx(dup, 0, out long _, FILE_END);

                if (!ok)
                {
                    error = $"ReadFile thất bại (lỗi {lastError}).";
                    return null;
                }
                if (read < size)
                {
                    Array.Resize(ref buffer, (int)read);
                }
                return buffer;
            }
            finally
            {
                if (dup != IntPtr.Zero) CloseHandle(dup);
                CloseHandle(hProc);
            }
        }

        /// <summary>Finds the game's open .mgz/.mgx handle by walking the system handle table.</summary>
        private static bool TryFindRecordHandle(IntPtr hProc, int pid, out IntPtr handleValue, out string path)
        {
            handleValue = IntPtr.Zero;
            path = null;

            int size = 1 << 20;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                while (true)
                {
                    buffer = Marshal.AllocHGlobal(size);
                    uint status = NtQuerySystemInformation(SystemExtendedHandleInformation,
                                                           buffer, size, out int needed);
                    if (status == 0) break;
                    Marshal.FreeHGlobal(buffer);
                    buffer = IntPtr.Zero;
                    if (status != STATUS_INFO_LENGTH_MISMATCH && status != STATUS_BUFFER_TOO_SMALL)
                    {
                        DebugLogger.Error($"LiveRecordReader: NtQuerySystemInformation failed 0x{status:X8}");
                        return false;
                    }
                    size = Math.Max(size * 2, needed + (1 << 20));
                }

                long count = Marshal.ReadIntPtr(buffer).ToInt64();
                int stride = Marshal.SizeOf<SystemHandleEntryEx>();
                int start = IntPtr.Size * 2;
                IntPtr me = GetCurrentProcess();

                for (long i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<SystemHandleEntryEx>(buffer + start + (int)(i * stride));
                    if (entry.UniqueProcessId.ToInt64() != pid) continue;

                    if (!DuplicateHandle(hProc, entry.HandleValue, me, out IntPtr probe,
                                         0, false, DUPLICATE_SAME_ACCESS))
                    {
                        continue;
                    }
                    try
                    {
                        // Check the type before asking for a name: querying the name of a
                        // synchronous pipe can block forever, disk files cannot.
                        if (GetFileType(probe) != FILE_TYPE_DISK) continue;

                        var name = new System.Text.StringBuilder(32768);
                        if (GetFinalPathNameByHandle(probe, name, name.Capacity, 0) == 0) continue;

                        string candidate = name.ToString();
                        if (!candidate.EndsWith(".mgz", StringComparison.OrdinalIgnoreCase)
                            && !candidate.EndsWith(".mgx", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        handleValue = entry.HandleValue;
                        path = candidate.StartsWith(@"\\?\") ? candidate.Substring(4) : candidate;
                        return true;
                    }
                    finally
                    {
                        CloseHandle(probe);
                    }
                }
                return false;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }

        // ------------------------------------------------------------------
        // record structure
        // ------------------------------------------------------------------

        /// <summary>
        /// Where the body starts. A finished record stores this in the first dword; an
        /// in-progress one stores zero, so it has to be recovered.
        ///
        /// Recovery works by candidate-and-verify rather than by inflating: every
        /// occurrence of the body magic is tried, and the one whose body actually walks
        /// as a long run of valid operations wins. A stray magic inside the compressed
        /// header parses for a handful of bytes at most, while the real one walks
        /// megabytes, so the winner is never in doubt.
        /// </summary>
        public static int DiscoverHeaderLength(string path, byte[] data)
        {
            if (data == null || data.Length < 16) return -1;

            int declared = BitConverter.ToInt32(data, 0);
            if (declared > 8 && declared <= data.Length && LooksLikeBodyStart(data, declared))
            {
                return declared;
            }

            if (path != null)
            {
                lock (HeaderLengthCache)
                {
                    if (HeaderLengthCache.TryGetValue(path, out int cached)
                        && cached < data.Length && LooksLikeBodyStart(data, cached))
                    {
                        return cached;
                    }
                }
            }

            int best = -1, bestBytes = 0;
            for (int at = IndexOf(data, BodyMagic, 8); at >= 0; at = IndexOf(data, BodyMagic, at + 1))
            {
                int bytes = WalkBody(data, at, out int _);
                if (bytes > bestBytes)
                {
                    bestBytes = bytes;
                    best = at;
                }
                // The real body runs to the end of the file; nothing else comes close.
                if (bestBytes > 0 && at + bestBytes >= data.Length - 64) break;
            }

            if (best > 0 && path != null)
            {
                lock (HeaderLengthCache)
                {
                    HeaderLengthCache[path] = best;
                }
            }
            return best;
        }

        private static bool LooksLikeBodyStart(byte[] data, int offset)
        {
            if (offset < 0 || offset + BodyMagic.Length > data.Length) return false;
            for (int i = 0; i < BodyMagic.Length; i++)
            {
                if (data[offset + i] != BodyMagic[i]) return false;
            }
            return true;
        }

        /// <summary>Verifies the header really is a deflate stream. Diagnostics only.</summary>
        public static bool HeaderInflates(byte[] data, int headerLength)
        {
            try
            {
                using var input = new MemoryStream(data, 8, headerLength - 8, writable: false);
                using var inflate = new DeflateStream(input, CompressionMode.Decompress);
                var scratch = new byte[64 * 1024];
                while (inflate.Read(scratch, 0, scratch.Length) > 0) { }
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"LiveRecordReader: header did not inflate: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Walks the operation stream from <paramref name="offset"/> and returns how many
        /// bytes form whole operations - i.e. where a live snapshot must be trimmed.
        ///
        /// Deliberately strict: an unrecognised operation id ends the walk. The reference
        /// Python parser (mgz) instead treats unknown ids as a "saved chapter" and then
        /// reads a length straight out of the data, which makes it run off the end of a
        /// truncated record and claim far more valid bytes than exist.
        /// </summary>
        /// <summary>
        /// How many times a walk may skip a stray 4 bytes to pick the stream back up.
        /// Kept small: this recovers known damage, it is not a licence to scan garbage.
        /// </summary>
        private const int MaxResyncs = 8;

        /// <summary>Operations a resynced run must parse before the skip is believed.</summary>
        private const int ResyncConfidenceOps = 64;

        public static int WalkBody(byte[] data, int offset, out int operations)
            => WalkBody(data, offset, out operations, out _);

        /// <summary>
        /// As <see cref="WalkBody(byte[], int, out int)"/>, also reporting how much GAME time
        /// the walked operations cover.
        ///
        /// Every sync operation carries the milliseconds since the previous one, so summing
        /// them gives the match clock exactly - no guessing from file size, and no dependence
        /// on when the capture happened. Checked against five real records: a 1.6 MB file
        /// reports 22:17, an 823 KB one 12:21, and each walk ended precisely at EOF.
        /// </summary>
        public static int WalkBody(byte[] data, int offset, out int operations, out long durationMs)
        {
            int consumed = WalkRun(data, offset, out operations, out durationMs);

            // A record written while something else moved the game's file pointer can carry a
            // stray duplicated dword mid-stream (see Home.StartLiveSnapshotWatcher). The rest
            // of the match is intact behind it, so step over the bad word and continue - but
            // only when what follows really does parse as a long run of operations, otherwise
            // this would happily walk off into garbage.
            for (int i = 0; i < MaxResyncs; i++)
            {
                int at = offset + consumed;
                if (at + 8 >= data.Length) break;

                int aheadOps;
                int ahead = WalkRun(data, at + 4, out aheadOps, out long aheadMs, skipMeta: false);
                if (aheadOps < ResyncConfidenceOps) break;

                DebugLogger.Warn($"LiveRecordReader: skipped a stray 4 bytes at {at} and " +
                                 $"recovered {aheadOps} more operations - this record was " +
                                 "damaged while it was being written.");
                consumed += 4 + ahead;
                operations += aheadOps;
                durationMs += aheadMs;
            }
            return consumed;
        }

        /// <summary>
        /// Walks a chunk that begins exactly on an operation boundary rather than at the
        /// start of the body, so there is no meta block to step over.
        ///
        /// This is what the live stream appends with: a chunk pulled from the host almost
        /// never ends on a boundary, so only the whole operations at its front are written
        /// to the file the game is replaying and the ragged tail is held back until the
        /// next chunk completes it. Without that the game would periodically see half an
        /// operation at the end of the file.
        ///
        /// No resync here - unlike <see cref="WalkBody"/> this walks bytes that arrived
        /// seconds ago over a socket, where "cannot parse" means "not all here yet", not
        /// "damaged", and skipping ahead would drop real data.
        /// </summary>
        public static int WalkAppended(byte[] data, int offset, out int operations)
            => WalkRun(data, offset, out operations, out _, skipMeta: false);

        /// <summary>As <see cref="WalkAppended(byte[], int, out int)"/>, also reporting game time.</summary>
        public static int WalkAppended(byte[] data, int offset, out int operations, out long durationMs)
            => WalkRun(data, offset, out operations, out durationMs, skipMeta: false);

        /// <summary>One uninterrupted walk; stops at the first thing it cannot read.</summary>
        private static int WalkRun(byte[] data, int offset, out int operations, out long durationMs,
                                   bool skipMeta = true)
        {
            operations = 0;
            durationMs = 0;
            if (offset < 0 || offset >= data.Length) return 0;

            int start = offset;
            if (skipMeta)
            {
                int metaLength = MetaLength(data, offset);
                if (metaLength < 0) return 0;
                offset += metaLength;
            }

            // A marker-style sync occupies 8 bytes but takes 12 to recognise, because the
            // 4 bytes that identify it belong to the next operation. One sitting at the very
            // end of the returned range therefore cannot be confirmed by anything re-reading
            // the trimmed result, so the walk would shrink by one every time. Tracking the
            // trailing run and rolling it back makes trimming idempotent: whatever is written
            // out re-parses to exactly the operation count reported here.
            int trailingSyncStart = -1;
            int trailingSyncCount = 0;
            long trailingSyncMs = 0;   // rolled back with the run, so the clock stays consistent too

            int end = data.Length;
            while (offset + 4 <= end)
            {
                uint op = BitConverter.ToUInt32(data, offset);
                if (op == 1) // action: length, action id + payload, sequence
                {
                    if (offset + 8 > end) break;
                    uint length = BitConverter.ToUInt32(data, offset + 4);
                    if (length == 0 || length >= 0x10000 || offset + 8 + length + 4 > end) break;
                    offset += (int)(8 + length + 4);
                }
                else if (op == 2) // sync: increment, then optionally a checksum block
                {
                    if (offset + 12 > end) break;
                    // Milliseconds of game time since the previous sync - summing these across
                    // the body is what gives the match clock.
                    uint increment = BitConverter.ToUInt32(data, offset + 4);
                    uint marker = BitConverter.ToUInt32(data, offset + 8);
                    if (marker != 0)
                    {
                        if (trailingSyncStart < 0)
                        {
                            trailingSyncStart = offset;
                            trailingSyncCount = 0;
                            trailingSyncMs = 0;
                        }
                        trailingSyncCount++;
                        trailingSyncMs += increment;
                        durationMs += increment;
                        offset += 8;
                        operations++;
                        continue; // keep the trailing run intact
                    }

                    // op, increment, marker=0, pad, checksum, pad, is_de, 8 more
                    if (offset + 36 > end) break;
                    if (BitConverter.ToUInt32(data, offset + 24) != 0) break; // DE-only layout
                    durationMs += increment;
                    offset += 36;
                }
                else if (op == 3) // viewlock: x, y, player
                {
                    if (offset + 16 > end) break;
                    offset += 16;
                }
                else if (op == 4) // chat: marker, length, text
                {
                    if (offset + 12 > end) break;
                    uint length = BitConverter.ToUInt32(data, offset + 8);
                    if (length > 0x1000 || offset + 12 + length > end) break;
                    offset += (int)(12 + length);
                }
                else
                {
                    break;
                }
                operations++;
                trailingSyncStart = -1;
                trailingSyncCount = 0;
                trailingSyncMs = 0;
            }

            if (trailingSyncStart >= 0)
            {
                offset = trailingSyncStart;
                operations -= trailingSyncCount;
                durationMs -= trailingSyncMs;
            }
            return offset - start;
        }

        /// <summary>Size of the meta block that opens the body, or -1 if this isn't one.</summary>
        private static int MetaLength(byte[] data, int offset)
        {
            if (offset + 8 > data.Length) return -1;
            uint first = BitConverter.ToUInt32(data, offset);
            int length = first == 500 ? 4 : 8; // AOK omits the log version
            length += 20;
            if (offset + length + 12 > data.Length) return -1;
            uint a = BitConverter.ToUInt32(data, offset + length);
            uint b = BitConverter.ToUInt32(data, offset + length + 4);
            if (a != 0) return length;               // AOC 1.0x: those 12 bytes are the first op
            return length + 12 - (b == 2 ? 8 : 0);   // DE rewinds 8
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int from)
        {
            int last = haystack.Length - needle.Length;
            for (int i = Math.Max(from, 0); i <= last; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        // ------------------------------------------------------------------
        // interop
        // ------------------------------------------------------------------
        private const int PROCESS_QUERY_INFORMATION = 0x0400;
        private const int PROCESS_DUP_HANDLE = 0x0040;
        private const uint DUPLICATE_SAME_ACCESS = 0x02;
        private const uint FILE_TYPE_DISK = 1;
        private const uint FILE_END = 2;
        private const int SystemExtendedHandleInformation = 0x40;
        private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
        private const uint STATUS_BUFFER_TOO_SMALL = 0xC0000023;

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemHandleEntryEx
        {
            public IntPtr Object;
            public IntPtr UniqueProcessId;
            public IntPtr HandleValue;
            public uint GrantedAccess;
            public ushort CreatorBackTraceIndex;
            public ushort ObjectTypeIndex;
            public uint HandleAttributes;
            public uint Reserved;
        }

        [DllImport("ntdll.dll")]
        private static extern uint NtQuerySystemInformation(int infoClass, IntPtr buffer,
                                                            int length, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle,
                                                   IntPtr targetProcess, out IntPtr targetHandle,
                                                   uint access, bool inherit, uint options);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetFileType(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFinalPathNameByHandle(IntPtr handle,
                                                            System.Text.StringBuilder path,
                                                            int capacity, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileSizeEx(IntPtr handle, out long size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFilePointerEx(IntPtr handle, long distance,
                                                    out long newPointer, uint method);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadFile(IntPtr handle, byte[] buffer, uint toRead,
                                            out uint read, ref NativeOverlapped overlapped);
    }
}
