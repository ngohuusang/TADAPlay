using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Reads the player's current in-game profile name out of AoE2's player*.nfz/nfx profile
    /// (the ASCII name buffer at offset 20 - see <see cref="GameProfileNameWriter"/> for the format).
    ///
    /// This is the counterpart to (and replacement for) forcing the name: rather than overwriting
    /// the game's profile to match the account - which wiped the game's profile&lt;-&gt;hotkey link and
    /// reset hotkeys - TadaPlay now READS whatever name the player actually uses in-game and reports
    /// it to the server as their locked identity for replay matching.
    ///
    /// Only the Voobly data mod's "Game Data" folder is read, and only its top level. That is the
    /// copy the game actually loads, exactly as with player*.hki (see
    /// HotkeyEditorForm.ResolveHotkeyDirectory). An earlier version searched the whole game folder
    /// with SearchOption.AllDirectories and returned whichever profile the filesystem happened to
    /// enumerate first, which is how a stale copy left in the game root - or in some other mod's
    /// folder - could be reported as the player's live name.
    ///
    /// When there is no Voobly layout this reports NOTHING rather than falling back to a wider
    /// search. The name is what the server matches replays against to attribute ELO, so a wrong
    /// name is materially worse than a missing one: it credits somebody else's games.
    /// </summary>
    public static class GameProfileNameReader
    {
        private static readonly string[] ProfilePatterns = { "player*.nfz", "player*.nfx" };

        /// <summary>
        /// How the fixed 256-byte name buffer is decoded.
        ///
        /// It was Encoding.ASCII, which turns every byte above 127 into a literal '?'. That is
        /// lossy and silent: "Em là Ta." reached the server as "Em l? Ta.", which then matches no
        /// replay and so earns its owner no ELO. A profile found on this machine decodes as
        /// 50-E1-6E-63-72-6F-6C: "Páncrol" in any single-byte codepage, "P?ncrol" in ASCII, and a
        /// hard DecoderFallbackException under strict UTF-8 - which is what rules UTF-8 out.
        ///
        /// The right codepage is the MACHINE'S ANSI one, because that is what the game encoded
        /// with, and the same machine writes and reads the file. That is 1252 on a Western
        /// install and 1258 on a Vietnamese one, and the two disagree about exactly the bytes
        /// Vietnamese names use - so guessing a fixed codepage would be wrong for one group or
        /// the other.
        ///
        /// .NET Core ships none of these single-byte codepages until CodePagesEncodingProvider is
        /// registered, hence the package reference. Latin1 is the last resort: it is built in and
        /// agrees with 1252 over most of the range, so a name is at worst imperfect rather than
        /// destroyed.
        /// </summary>
        private static readonly Encoding ProfileEncoding = ResolveProfileEncoding();

        private static Encoding ResolveProfileEncoding()
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                int codepage = (int)GetACP();
                Encoding encoding = Encoding.GetEncoding(codepage);
                DebugLogger.Info($"GameProfileNameReader: decoding profile names as ANSI codepage " +
                                 $"{codepage} ({encoding.WebName}).");
                return encoding;
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"GameProfileNameReader: cannot use the system ANSI codepage " +
                                 $"({ex.Message}); falling back to 1252.");
            }

            try { return Encoding.GetEncoding(1252); }
            catch (Exception ex)
            {
                DebugLogger.Warn($"GameProfileNameReader: codepage 1252 unavailable ({ex.Message}); " +
                                 "falling back to Latin1.");
                return Encoding.Latin1;
            }
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetACP();

        /// <summary>
        /// Returns the in-game profile name from the Voobly "Game Data" folder under
        /// <paramref name="gameFolder"/>, or null when there is no readable profile there.
        /// Never throws.
        /// </summary>
        public static string ReadActiveName(string gameFolder)
        {
            string profileDir = ResolveProfileDirectory(gameFolder);
            if (profileDir == null) return null;

            // Most recently written first. The game saves the profile it is actually using, so
            // the newest file is the active one - and unlike enumeration order this is stable
            // and explainable when a report turns out wrong. Ordering across BOTH extensions
            // together also removes the old trap where every .nfz outranked every .nfx purely
            // because of the order the patterns were listed in.
            List<FileInfo> candidates;
            try
            {
                candidates = ProfilePatterns
                    .SelectMany(p => Directory.EnumerateFiles(profileDir, p, SearchOption.TopDirectoryOnly))
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"GameProfileNameReader: cannot list profiles in '{profileDir}': {ex.Message}");
                return null;
            }

            if (candidates.Count == 0)
            {
                DebugLogger.Warn($"GameProfileNameReader: no player*.nfz/nfx in '{profileDir}'.");
                return null;
            }

            string chosen = null;
            // Every candidate is logged, not just the winner. When a player reports the wrong name
            // this is the difference between guessing and reading the answer off the log.
            foreach (FileInfo file in candidates)
            {
                string name = TryReadName(file.FullName);
                bool taken = chosen == null && !string.IsNullOrWhiteSpace(name);
                if (taken) chosen = name;
                DebugLogger.Info($"GameProfileNameReader: {(taken ? "USING " : "      ")}" +
                                 $"'{file.Name}' modified {file.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss}Z " +
                                 $"-> {(string.IsNullOrWhiteSpace(name) ? "<empty/unreadable>" : $"\"{name}\"")}");
            }

            if (chosen == null)
                DebugLogger.Warn($"GameProfileNameReader: {candidates.Count} profile(s) in '{profileDir}' but none held a name.");
            return chosen;
        }

        /// <summary>
        /// The Voobly data mod folder the game loads profiles from, or null when this install has
        /// no Voobly layout. Mirrors HotkeyEditorForm.ResolveHotkeyDirectory, which resolves the
        /// same folder for player*.hki - deliberately, since both files live side by side.
        /// </summary>
        private static string ResolveProfileDirectory(string gameFolder)
        {
            if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
            {
                DebugLogger.Warn($"GameProfileNameReader: game folder not set or missing: '{gameFolder}'.");
                return null;
            }

            string dataMods = Path.Combine(gameFolder, "Voobly Mods", "AOC", "Data Mods");
            if (!Directory.Exists(dataMods))
            {
                DebugLogger.Warn($"GameProfileNameReader: no Voobly data mods folder at '{dataMods}' - " +
                                 "not reporting an in-game name rather than risk reporting the wrong one.");
                return null;
            }

            try
            {
                var gameDataDirs = Directory.EnumerateDirectories(dataMods, "*Game Data").ToList();

                // Prefer a "* Game Data" folder that actually holds a profile. An install can carry
                // several (v1.4, v1.5, WololoKingdoms...) and the empty ones are not the live copy.
                string withProfile = gameDataDirs.FirstOrDefault(
                    d => ProfilePatterns.Any(p => Directory.EnumerateFiles(d, p, SearchOption.TopDirectoryOnly).Any()));
                if (withProfile != null) return withProfile;

                if (gameDataDirs.Count > 0)
                {
                    DebugLogger.Warn($"GameProfileNameReader: no profile in any of {gameDataDirs.Count} " +
                                     $"'*Game Data' folder(s) under '{dataMods}'.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"GameProfileNameReader: resolving the Game Data folder failed: {ex.Message}");
                return null;
            }

            // The conventional path, for an install whose Game Data folder is named unusually.
            string conventional = Path.Combine(dataMods, "v1.5 Game Data");
            if (Directory.Exists(conventional)) return conventional;

            DebugLogger.Warn($"GameProfileNameReader: no '*Game Data' folder under '{dataMods}'.");
            return null;
        }

        private static string TryReadName(string path)
        {
            try
            {
                byte[] decompressed = GameProfileNameWriter.Inflate(ReadAllBytesShared(path));
                int start = GameProfileNameWriter.NameOffset;
                if (decompressed.Length < start + GameProfileNameWriter.NameBufferSize) return null;

                int end = start;
                int limit = start + GameProfileNameWriter.NameBufferSize;
                while (end < limit && decompressed[end] != 0) end++;

                return ProfileEncoding.GetString(decompressed, start, end - start).Trim();
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"GameProfileNameReader: failed reading '{path}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Reads a file the GAME may have open. File.ReadAllBytes asks for FileShare.Read, which is
        /// refused outright if the holder opened it for writing - the same incompatibility that
        /// froze the spectator match clock at 00:00 (see LiveRecordReader.ReadAllBytesShared). This
        /// poll runs while the game is up, so it has to tolerate exactly that.
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
    }
}
