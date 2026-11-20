using System;
using System.IO;
using System.Linq;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Holds the match this client is willing to share, so other players can watch it.
    ///
    /// A match is published once it has FINISHED and the game has closed the record. That
    /// timing is the whole point: capturing a match while it is still being played means
    /// reading through the game's own file handle, which shares a file pointer with the
    /// game and demonstrably corrupts the record being written (see
    /// Home.StartLiveSnapshotWatcher for the measurement). Watching is therefore delayed
    /// until the game ends rather than merely delayed by a snapshot interval.
    ///
    /// Copies live outside the game folder (under LocalApplicationData) so that clearing
    /// SaveGame does not take them with it, and they survive a TadaPlay restart.
    /// </summary>
    public static class LiveRecordSnapshotStore
    {
        private const string SnapshotSuffix = ".live.mgz";
        private static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

        /// <summary>Folder holding live snapshots; created on first use.</summary>
        public static string Directory
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TadaPlay", "LiveRecords");
                System.IO.Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>Snapshot path for a given source record, whether or not it exists yet.</summary>
        public static string PathFor(string recordPath)
        {
            if (string.IsNullOrWhiteSpace(recordPath)) return null;
            return Path.Combine(Directory, Path.GetFileNameWithoutExtension(recordPath) + SnapshotSuffix);
        }

        /// <summary>Existing snapshot for a record, or null.</summary>
        public static string FindFor(string recordPath)
        {
            string path = PathFor(recordPath);
            return path != null && File.Exists(path) ? path : null;
        }

        /// <summary>
        /// The most recently written snapshot - i.e. the match being played right now, which
        /// is what a spectator wants. Null when nothing has been captured.
        /// </summary>
        public static string Current()
        {
            try
            {
                return System.IO.Directory.EnumerateFiles(Directory, "*" + SnapshotSuffix)
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(fi => fi.LastWriteTimeUtc)
                    .Select(fi => fi.FullName)
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"LiveRecordSnapshotStore: cannot list snapshots: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Writes a snapshot for a match, growing the earlier one rather than replacing it.
        /// </summary>
        public static string Save(LiveRecordReader.Snapshot snapshot)
        {
            if (snapshot?.Data == null || snapshot.Data.Length == 0) return null;

            string target = PathFor(snapshot.SourcePath);
            if (target == null) return null;
            return WriteGrowing(target, snapshot.Data, snapshot.SourcePath);
        }

        /// <summary>Bytes at the end of the overlap compared before trusting it as a prefix.</summary>
        private const int CompareTailBytes = 64 * 1024;

        /// <summary>Bytes at the start compared too (after the header-length dword).</summary>
        private const int CompareHeadBytes = 4096;

        /// <summary>
        /// Brings the snapshot up to <paramref name="data"/> by appending only what is new.
        ///
        /// Viewers hold a byte offset into this file and ask for what lies beyond it. A snapshot
        /// that is rewritten, or worse ever gets SHORTER, puts every viewer's offset past the
        /// end, and their app stops following the match ("Dữ liệu trận đấu ... đã thay đổi").
        /// That used to happen whenever the source changed mid-match - the game's own stream
        /// reconnecting and starting again from byte 0, or a shadow copy (which lags the
        /// stream, because the game buffers its disk writes) replacing a longer streamed file.
        ///
        /// So, the same rule as a spectator relay that never rewinds its read cursor: the
        /// bytes already published are never touched. When the new data agrees with them it
        /// only ever adds to the end; when it is shorter there is nothing to do. Only data that
        /// genuinely disagrees - a different recording - replaces the file, and that is logged.
        ///
        /// The first four bytes are the header length the game patches in at the end of a
        /// match; they are excluded from the comparison and refreshed in place.
        /// </summary>
        private static string WriteGrowing(string target, byte[] data, string label)
        {
            try
            {
                if (File.Exists(target))
                {
                    using var fs = new FileStream(target, FileMode.Open, FileAccess.ReadWrite,
                                                  FileShare.ReadWrite | FileShare.Delete);
                    long have = fs.Length;
                    if (have > 0 && Agrees(fs, have, data))
                    {
                        if (data.Length > have)
                        {
                            fs.Seek(have, SeekOrigin.Begin);
                            fs.Write(data, (int)have, data.Length - (int)have);
                        }
                        PatchHeaderLength(fs, data);
                        return target;
                    }
                    if (have > 0)
                    {
                        DebugLogger.Warn($"LiveRecordSnapshotStore: new data for '{label}' does not continue " +
                                         $"the published {have} bytes - replacing it ({data.Length} bytes). " +
                                         "Viewers of the old copy will have to reopen it.");
                    }
                }

                // New file, or one that has to be replaced: written beside it and moved into
                // place, so nobody ever reads a half-written file.
                string temp = target + ".tmp";
                try
                {
                    File.WriteAllBytes(temp, data);
                    File.Move(temp, target, overwrite: true);
                }
                catch
                {
                    TryDelete(temp);
                    throw;
                }
                return target;
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"LiveRecordSnapshotStore: cannot save snapshot for '{label}': {ex.Message}");
                return null;
            }
        }

        /// <summary>Whether the first <paramref name="have"/> bytes on disk and <paramref name="data"/>
        /// agree wherever both exist (start and end of the overlap; the length dword excepted).</summary>
        private static bool Agrees(FileStream fs, long have, byte[] data)
        {
            long overlap = Math.Min(have, data.Length);
            if (overlap <= 4) return true;

            return Matches(fs, 4, Math.Min(overlap, CompareHeadBytes), data)
                && Matches(fs, Math.Max(4, overlap - CompareTailBytes), overlap, data);
        }

        private static bool Matches(FileStream fs, long from, long to, byte[] data)
        {
            int count = (int)(to - from);
            if (count <= 0) return true;
            var disk = new byte[count];
            fs.Seek(from, SeekOrigin.Begin);
            int read = 0;
            while (read < count)
            {
                int n = fs.Read(disk, read, count - read);
                if (n <= 0) return false;
                read += n;
            }
            return disk.AsSpan().SequenceEqual(data.AsSpan((int)from, count));
        }

        private static void PatchHeaderLength(FileStream fs, byte[] data)
        {
            if (data.Length < 4 || fs.Length < 4) return;
            var disk = new byte[4];
            fs.Seek(0, SeekOrigin.Begin);
            if (fs.Read(disk, 0, 4) != 4) return;
            if (disk.AsSpan().SequenceEqual(data.AsSpan(0, 4))) return;
            fs.Seek(0, SeekOrigin.Begin);
            fs.Write(data, 0, 4);
        }

        /// <summary>
        /// Publishes a finished match so other players can watch it.
        ///
        /// Safe to call the moment the watcher sees the game release the record: by then the
        /// game has closed the file, so this is an ordinary read of a file nobody is writing.
        /// The header length is repaired first when the game exited without patching it, so
        /// viewers never receive a record their parser rejects.
        /// </summary>
        /// <returns>The published path, or null with the reason in <paramref name="note"/>.</returns>
        public static string PublishFinished(string recordPath, out string note)
        {
            note = null;
            if (string.IsNullOrWhiteSpace(recordPath) || !File.Exists(recordPath))
            {
                note = "Không tìm thấy file record để chia sẻ.";
                return null;
            }

            LiveRecordReader.RecordAnalysis analysis = LiveRecordReader.AnalyzeFile(recordPath);
            if (analysis == null)
            {
                note = "Record không đọc được nên không chia sẻ.";
                return null;
            }

            try
            {
                byte[] data = analysis.NeedsRepair
                    ? analysis.RepairedData
                    : File.ReadAllBytes(recordPath);

                // Grown, not replaced: viewers are usually still following this same file
                // when the match ends, and a rewrite would cut them off before the ending.
                string target = WriteGrowing(PathFor(recordPath), data, recordPath);
                if (target == null)
                {
                    note = "Không ghi được file để chia sẻ.";
                    return null;
                }

                DebugLogger.Info($"LiveRecordSnapshotStore: published '{Path.GetFileName(recordPath)}' " +
                                 $"for spectating ({analysis.Operations} ops, {data.Length} bytes).");
                return target;
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"LiveRecordSnapshotStore: cannot publish '{recordPath}': {ex.Message}");
                note = ex.Message;
                return null;
            }
        }

        /// <summary>Drops the shared copy of a match.</summary>
        public static void Discard(string recordPath)
        {
            string path = FindFor(recordPath);
            if (path != null) TryDelete(path);
        }

        /// <summary>Deletes snapshots left behind by matches that were never uploaded.</summary>
        public static void Prune()
        {
            try
            {
                DateTime cutoff = DateTime.UtcNow - KeepFor;
                foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*" + SnapshotSuffix)
                                                           .Concat(System.IO.Directory.EnumerateFiles(Directory, "*.tmp")))
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) TryDelete(file);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"LiveRecordSnapshotStore: prune failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Picks the copy of a finished match that should actually be uploaded, and repairs
        /// it if the game never patched its header length.
        ///
        /// Prefers the game's own file - it is the complete, authoritative record - and
        /// falls back to the live snapshot when that file is gone, unreadable, or contains
        /// less of the match than the snapshot does (deleted, truncated, or edited after
        /// the fact).
        /// </summary>
        /// <param name="recordPath">The record the watcher saw the game finish.</param>
        /// <param name="note">Human-readable explanation of the choice, for the activity log.</param>
        /// <returns>Path to upload - the original file, a repaired temp copy, or a snapshot.</returns>
        public static string ResolveUploadSource(string recordPath, out string note)
        {
            note = null;

            LiveRecordReader.RecordAnalysis disk =
                !string.IsNullOrWhiteSpace(recordPath) && File.Exists(recordPath)
                    ? LiveRecordReader.AnalyzeFile(recordPath)
                    : null;

            string snapshotPath = FindFor(recordPath);
            LiveRecordReader.RecordAnalysis snapshot =
                snapshotPath != null ? LiveRecordReader.AnalyzeFile(snapshotPath) : null;

            if (disk == null && snapshot == null)
            {
                // Nothing usable either way - hand back the original so the caller reports
                // the real failure against the file the player expects to see named.
                return recordPath;
            }

            if (disk == null)
            {
                note = $"File record không đọc được - dùng bản lưu trực tiếp trong lúc chơi " +
                       $"({snapshot.Operations} thao tác).";
                DebugLogger.Warn($"LiveRecordSnapshotStore: falling back to snapshot for '{recordPath}'.");
                return snapshotPath;
            }

            if (snapshot != null && snapshot.BodyBytes > disk.BodyBytes)
            {
                note = $"Bản lưu trực tiếp dài hơn file record ({snapshot.Operations} so với " +
                       $"{disk.Operations} thao tác) - dùng bản lưu trực tiếp.";
                DebugLogger.Warn($"LiveRecordSnapshotStore: snapshot beats on-disk record for " +
                                 $"'{recordPath}' ({snapshot.BodyBytes} > {disk.BodyBytes} bytes).");
                return snapshotPath;
            }

            if (!disk.NeedsRepair) return recordPath;

            try
            {
                string repairedPath = Path.Combine(Path.GetTempPath(),
                                                   "tada_fixed_" + Path.GetFileName(recordPath));
                File.WriteAllBytes(repairedPath, disk.RepairedData);
                note = $"Record bị lỗi do thoát game đột ngột - đã sửa (header_len=0 -> " +
                       $"{disk.HeaderLength}, {disk.Operations} thao tác).";
                return repairedPath;
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"LiveRecordSnapshotStore: cannot write repaired copy: {ex.Message}");
                note = $"Không ghi được bản sửa lỗi: {ex.Message}";
                return recordPath;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"LiveRecordSnapshotStore: cannot delete '{path}': {ex.Message}");
            }
        }
    }
}
