using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Writes the account's in-game display name into AoE2's own player profile files
    /// (player*.nfx / player*.nfz — the "quick game options" / campaign-history profile,
    /// found under the game folder, e.g. a Voobly mod's own "Data Mods" copy) so the name
    /// shown in-game matches the TADAPlay account, without the player having to re-type it
    /// in the game's own options menu before every match.
    ///
    /// File format (reverse-engineered, not documented by Microsoft/Voobly): each file is a
    /// raw DEFLATE stream (RFC 1951, no zlib/gzip header) whose decompressed body starts with
    /// a 4-byte version tag, a float and three int32 fields (20 bytes), followed by a fixed
    /// 256-byte null-terminated ASCII name buffer at offset 20. Everything after offset 276
    /// (recent scenarios, quick-game favorites, etc.) is left untouched.
    ///
    /// player*.hki files are deliberately NOT touched here: they store only hotkey bindings
    /// (int32 tables, no header, no name field) - there is nothing to rename in them.
    /// </summary>
    public static class GameProfileNameWriter
    {
        private const int NameOffset = 20;
        private const int NameBufferSize = 256;
        private const int MinDecompressedSize = NameOffset + NameBufferSize;

        private static readonly string[] ProfilePatterns = { "player*.nfx", "player*.nfz" };

        /// <summary>
        /// Best-effort: rewrites the name field in every player*.nfx/nfz profile found under
        /// <paramref name="gameFolder"/> to <paramref name="playerName"/>. Never throws - failures
        /// are logged and skipped so a broken/unexpected profile file can't block starting a game.
        /// </summary>
        public static void SyncPlayerName(string gameFolder, string playerName)
        {
            if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
            {
                DebugLogger.Warn($"GameProfileNameWriter: game folder not set or missing: '{gameFolder}'.");
                return;
            }

            if (string.IsNullOrWhiteSpace(playerName))
            {
                DebugLogger.Warn("GameProfileNameWriter: no player name to sync.");
                return;
            }

            foreach (string path in ProfilePatterns.SelectMany(pattern => SafeEnumerate(gameFolder, pattern)))
            {
                TryUpdateProfileName(path, playerName);
            }
        }

        private static void TryUpdateProfileName(string path, string playerName)
        {
            try
            {
                byte[] compressed = File.ReadAllBytes(path);
                byte[] decompressed = Inflate(compressed);

                if (decompressed.Length < MinDecompressedSize)
                {
                    DebugLogger.Warn($"GameProfileNameWriter: '{path}' is smaller than expected, skipping.");
                    return;
                }

                byte[] nameBytes = Encoding.ASCII.GetBytes(playerName);
                int copyLen = Math.Min(nameBytes.Length, NameBufferSize - 1);

                Array.Clear(decompressed, NameOffset, NameBufferSize);
                Array.Copy(nameBytes, 0, decompressed, NameOffset, copyLen);

                File.WriteAllBytes(path, Deflate(decompressed));
                DebugLogger.Info($"GameProfileNameWriter: synced in-game name in '{path}'.");
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"GameProfileNameWriter: failed to update '{path}': {ex.Message}");
            }
        }

        private static byte[] Inflate(byte[] compressed)
        {
            using var input = new MemoryStream(compressed);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);
            return output.ToArray();
        }

        private static byte[] Deflate(byte[] raw)
        {
            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(raw, 0, raw.Length);
            }
            return output.ToArray();
        }

        private static System.Collections.Generic.IEnumerable<string> SafeEnumerate(string root, string pattern)
        {
            try
            {
                return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"GameProfileNameWriter: cannot enumerate '{pattern}' under '{root}': {ex.Message}");
                return Array.Empty<string>();
            }
        }
    }
}
