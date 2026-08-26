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
        /// Writes a snapshot, replacing any earlier one for the same match.
        ///
        /// Written to a temporary file and then moved into place, so a crash (or the
        /// player killing TadaPlay) can never leave a half-written snapshot that would
        /// look like a valid but truncated record at upload time.
        /// </summary>
        public static string Save(LiveRecordReader.Snapshot snapshot)
        {
            if (snapshot?.Data == null || snapshot.Data.Length == 0) return null;

            string target = PathFor(snapshot.SourcePath);
            if (target == null) return null;

            string temp = target + ".tmp";
            try
            {
                File.WriteAllBytes(temp, snapshot.Data);
                File.Move(temp, target, overwrite: true);
                return target;
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"LiveRecordSnapshotStore: cannot save snapshot for " +
                                  $"'{snapshot.SourcePath}': {ex.Message}");
                TryDelete(temp);
                return null;
            }
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

                string target = PathFor(recordPath);
                string temp = target + ".tmp";
                File.WriteAllBytes(temp, data);
                File.Move(temp, target, overwrite: true);

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
