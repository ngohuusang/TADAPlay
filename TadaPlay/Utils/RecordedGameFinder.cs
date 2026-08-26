using System;
using System.IO;
using System.Linq;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Locates Age of Empires II recorded games (.mgz / .mgx — UserPatch / WololoKingdoms)
    /// under the user-configured game folder, and the folder to drop replays into.
    ///
    /// Records live in a "SaveGame" folder under the game install, e.g.
    ///   &lt;GameFolder&gt;\SaveGame\rec.YYYYMMDD-HHMMSS.mgz
    ///   &lt;GameFolder&gt;\Games\WololoKingdoms\SaveGame\rec.*.mgz
    /// so we search recursively from the configured game folder.
    /// </summary>
    public static class RecordedGameFinder
    {
        private static readonly string[] RecordPatterns = { "*.mgz", "*.mgx" };

        /// <summary>
        /// File-name prefix stamped on records downloaded for local replay (see
        /// Matches.ReplaySelectedAsync) - these are copies of matches already on the server, so
        /// the auto-upload watcher must skip them instead of re-uploading as a "new" match.
        /// </summary>
        public const string ReplayPrefix = "tada_record_";

        /// <summary>
        /// File-name prefix for another player's in-progress match downloaded for delayed
        /// spectating (see LiveShareClient). Like <see cref="ReplayPrefix"/> these are not
        /// this player's own games, so the auto-upload watcher must ignore them - otherwise
        /// watching someone else's match would upload it under your name.
        /// </summary>
        public const string LivePrefix = "tada_live_";

        /// <summary>
        /// Returns the most recently written recorded game under <paramref name="gameFolder"/>,
        /// or null if none found. Records previously downloaded for replay (see
        /// <see cref="ReplayPrefix"/>) are excluded. <paramref name="modifiedAfterUtc"/>, when
        /// set, restricts to files written at/after that time (pass the game's start time to
        /// avoid stale records).
        /// </summary>
        public static string FindLatestRecord(string gameFolder, DateTime? modifiedAfterUtc = null)
        {
            if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
            {
                DebugLogger.Warn($"RecordedGameFinder: game folder not set or missing: '{gameFolder}'.");
                return null;
            }

            try
            {
                var newest = RecordPatterns
                    .SelectMany(pattern => SafeEnumerate(gameFolder, pattern))
                    .Select(path => new FileInfo(path))
                    .Where(fi => !fi.Name.StartsWith(ReplayPrefix, StringComparison.OrdinalIgnoreCase))
                    .Where(fi => !fi.Name.StartsWith(LivePrefix, StringComparison.OrdinalIgnoreCase))
                    .Where(fi => !modifiedAfterUtc.HasValue || fi.LastWriteTimeUtc >= modifiedAfterUtc.Value)
                    .OrderByDescending(fi => fi.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (newest != null)
                {
                    DebugLogger.Info($"RecordedGameFinder: found '{newest.FullName}' (written {newest.LastWriteTimeUtc:o} UTC).");
                    return newest.FullName;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"RecordedGameFinder: scan failed under '{gameFolder}': {ex.Message}");
            }

            DebugLogger.Warn($"RecordedGameFinder: no .mgz/.mgx record found under '{gameFolder}'.");
            return null;
        }

        /// <summary>
        /// Returns the directory to download replays into so the game can list them: the folder
        /// of the most recent record under the game folder, else &lt;GameFolder&gt;\SaveGame, else
        /// the game folder itself. Returns null if the game folder is not configured.
        /// </summary>
        public static string FindSaveGameDirectory(string gameFolder)
        {
            if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
            {
                return null;
            }

            // Prefer the folder that already holds the newest record.
            string latest = FindLatestRecord(gameFolder);
            if (!string.IsNullOrEmpty(latest))
            {
                return Path.GetDirectoryName(latest);
            }

            string saveGame = Path.Combine(gameFolder, "SaveGame");
            return Directory.Exists(saveGame) ? saveGame : gameFolder;
        }

        private static System.Collections.Generic.IEnumerable<string> SafeEnumerate(string root, string pattern)
        {
            try
            {
                return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"RecordedGameFinder: cannot enumerate '{pattern}' under '{root}': {ex.Message}");
                return Array.Empty<string>();
            }
        }
    }
}
