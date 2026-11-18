using System;
using System.IO;
using System.Linq;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Locates Age of Empires II recorded games (.mgz / .mgx for UserPatch and .aoe2record for
    /// Steam HD) under the user-configured game folder, and the folder to drop replays into.
    ///
    /// Records live in a "SaveGame" folder under the game install, e.g.
    ///   &lt;GameFolder&gt;\SaveGame\rec.YYYYMMDD-HHMMSS.mgz
    ///   &lt;GameFolder&gt;\Games\WololoKingdoms\SaveGame\rec.*.mgz
    /// so we search recursively from the configured game folder.
    /// </summary>
    public static class RecordedGameFinder
    {
        private static readonly string[] RecordPatterns = { "*.mgz", "*.mgx", "*.aoe2record" };

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
        public static string FindLatestRecord(string gameFolder, DateTime? modifiedAfterUtc = null,
                                               GameRecordFormat format = GameRecordFormat.Unknown)
        {
            gameFolder = ResolveGameFolder(gameFolder, format);
            if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
            {
                DebugLogger.Warn($"RecordedGameFinder: game folder not set or missing: '{gameFolder}'.");
                return null;
            }

            try
            {
                var newest = SearchRoots(gameFolder)
                    .SelectMany(root => RecordPatterns.Select(pattern => (root, pattern)))
                    .SelectMany(rp => SafeEnumerate(rp.root, rp.pattern))
                    .Select(path => new FileInfo(path))
                    .Where(fi => !fi.Name.StartsWith(ReplayPrefix, StringComparison.OrdinalIgnoreCase))
                    .Where(fi => !fi.Name.StartsWith(LivePrefix, StringComparison.OrdinalIgnoreCase))
                    .Where(fi => format == GameRecordFormat.Unknown
                                 || GameRecordFormats.FromPath(fi.FullName) == format)
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
        public static string FindSaveGameDirectory(string gameFolder,
                                                    GameRecordFormat format = GameRecordFormat.Unknown)
        {
            gameFolder = ResolveGameFolder(gameFolder, format);
            if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
            {
                return null;
            }

            // Prefer the folder that already holds a record of the requested format.
            string latest = FindLatestRecord(gameFolder, format: format);
            if (!string.IsNullOrEmpty(latest))
            {
                return Path.GetDirectoryName(latest);
            }

            string saveGame = Path.Combine(gameFolder, "SaveGame");
            if (Directory.Exists(saveGame)) return saveGame;

            return format == GameRecordFormat.SteamHd ? null : gameFolder;
        }

        /// <summary>Resolves the configured folder to the installation matching the record format.</summary>
        public static string ResolveGameFolder(string gameFolder,
                                               GameRecordFormat format = GameRecordFormat.Unknown)
        {
            if (string.IsNullOrWhiteSpace(gameFolder)) return gameFolder;

            var folder = new DirectoryInfo(gameFolder);
            if (format == GameRecordFormat.UserPatch)
            {
                if (Directory.Exists(Path.Combine(folder.FullName, "age2_x1"))) return folder.FullName;

                if (File.Exists(Path.Combine(folder.FullName, "AoK HD.exe")))
                {
                    try
                    {
                        foreach (string child in Directory.EnumerateDirectories(folder.FullName))
                        {
                            if (Directory.Exists(Path.Combine(child, "age2_x1"))) return child;
                        }
                    }
                    catch (IOException ex)
                    {
                        DebugLogger.Warn($"RecordedGameFinder: cannot locate the legacy game under " +
                                         $"'{folder.FullName}': {ex.Message}");
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        DebugLogger.Warn($"RecordedGameFinder: cannot locate the legacy game under " +
                                         $"'{folder.FullName}': {ex.Message}");
                    }
                }

                return gameFolder;
            }

            if (File.Exists(Path.Combine(folder.FullName, "AoK HD.exe"))) return folder.FullName;

            DirectoryInfo parent = folder.Parent;
            return parent != null && File.Exists(Path.Combine(parent.FullName, "AoK HD.exe"))
                ? parent.FullName
                : gameFolder;
        }

        public static GameRecordFormat DetectInstalledFormat(string gameFolder)
        {
            if (string.IsNullOrWhiteSpace(gameFolder)) return GameRecordFormat.Unknown;

            string configuredFolder = Path.GetFullPath(gameFolder);
            if (File.Exists(Path.Combine(configuredFolder, "AoK HD.exe")))
                return GameRecordFormat.SteamHd;

            DirectoryInfo parent = Directory.GetParent(configuredFolder);
            if (parent != null && File.Exists(Path.Combine(parent.FullName, "AoK HD.exe")))
                return GameRecordFormat.SteamHd;

            if (Directory.Exists(Path.Combine(configuredFolder, "age2_x1")))
                return GameRecordFormat.UserPatch;

            return GameRecordFormat.Unknown;
        }

        /// <summary>
        /// The game folder, plus its UAC VirtualStore copy when there is one.
        ///
        /// A game installed under Program Files and started without admin rights (a desktop
        /// shortcut rather than this app) cannot write there, so Windows silently redirects its
        /// records to %LOCALAPPDATA%\VirtualStore\Program Files\... . This app runs elevated
        /// and sees only the real folder, so those matches were never noticed.
        /// </summary>
        public static System.Collections.Generic.IEnumerable<string> SearchRoots(string gameFolder)
        {
            yield return gameFolder;

            string virtualStore = null;
            try
            {
                string full = Path.GetFullPath(gameFolder);
                string root = Path.GetPathRoot(full);
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(root) && !string.IsNullOrEmpty(local))
                {
                    virtualStore = Path.Combine(local, "VirtualStore", full.Substring(root.Length));
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"RecordedGameFinder: cannot work out the VirtualStore folder: {ex.Message}");
            }

            if (virtualStore != null && Directory.Exists(virtualStore)) yield return virtualStore;
        }

        // IgnoreInaccessible: the SearchOption overload stops the WHOLE enumeration at the first
        // subfolder it may not read, and that exception surfaced mid-scan - so one locked folder
        // anywhere in the game tree meant no record was ever found again, every 5 seconds, and
        // that player never showed as playing. AttributesToSkip 0 keeps hidden/system files in,
        // as the old overload did.
        private static readonly EnumerationOptions ScanOptions = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        private static System.Collections.Generic.IEnumerable<string> SafeEnumerate(string root, string pattern)
        {
            try
            {
                return Directory.EnumerateFiles(root, pattern, ScanOptions);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"RecordedGameFinder: cannot enumerate '{pattern}' under '{root}': {ex.Message}");
                return Array.Empty<string>();
            }
        }
    }
}
