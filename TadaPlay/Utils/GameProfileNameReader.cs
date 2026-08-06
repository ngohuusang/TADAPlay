using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Reads the player's current in-game profile name out of AoE2's player*.nfz/nfx profile
    /// (the ASCII name buffer at offset 20 - see <see cref="GameProfileNameWriter"/> for the format).
    ///
    /// This is the counterpart to (and replacement for) forcing the name: rather than overwriting
    /// the game's profile to match the account - which wiped the game's profile<->hotkey link and
    /// reset hotkeys - TadaPlay now READS whatever name the player actually uses in-game and reports
    /// it to the server as their locked identity for replay matching.
    /// </summary>
    public static class GameProfileNameReader
    {
        private static readonly string[] ProfilePatterns = { "player*.nfz", "player*.nfx" };

        /// <summary>
        /// Returns the in-game profile name found under <paramref name="gameFolder"/>, or null when
        /// no readable profile exists / the name is empty. Never throws.
        /// </summary>
        public static string ReadActiveName(string gameFolder)
        {
            if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
                return null;

            foreach (string path in ProfilePatterns.SelectMany(p => SafeEnumerate(gameFolder, p)))
            {
                string name = TryReadName(path);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            return null;
        }

        private static string TryReadName(string path)
        {
            try
            {
                byte[] decompressed = GameProfileNameWriter.Inflate(File.ReadAllBytes(path));
                int start = GameProfileNameWriter.NameOffset;
                if (decompressed.Length < start + GameProfileNameWriter.NameBufferSize) return null;

                int end = start;
                int limit = start + GameProfileNameWriter.NameBufferSize;
                while (end < limit && decompressed[end] != 0) end++;

                return Encoding.ASCII.GetString(decompressed, start, end - start).Trim();
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"GameProfileNameReader: failed reading '{path}': {ex.Message}");
                return null;
            }
        }

        private static IEnumerable<string> SafeEnumerate(string root, string pattern)
        {
            try
            {
                return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"GameProfileNameReader: cannot enumerate '{pattern}' under '{root}': {ex.Message}");
                return Array.Empty<string>();
            }
        }
    }
}
