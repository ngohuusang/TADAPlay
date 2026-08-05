using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// AoE2's own profile files (player*.nfx/nfz) accumulate a growing history of past
    /// quick-game names/settings, and the game itself keeps rewriting them while the player
    /// browses the in-game profile picker - simply patching the name once (see
    /// <see cref="GameProfileNameWriter"/>) isn't enough to keep the account name "selected"
    /// there. Instead, this repeatedly stamps every player*.nfx/nfz found under the game
    /// folder with a known-good, single-profile template (bundled as an app resource) with
    /// only the current name field changed, so no matter what the game does to those files
    /// in between ticks, the next tick forces them back to a clean single-entry profile.
    /// </summary>
    public static class ProfileTemplateEnforcer
    {
        private static readonly string[] ProfilePatterns = { "player*.nfx", "player*.nfz" };
        private const string TemplateResourceName = "TadaPlay.Resources.player.nfz.template";

        /// <summary>
        /// Overwrites every player*.nfx/nfz found under <paramref name="gameFolder"/> with the
        /// bundled template, name field set to <paramref name="playerName"/>. Never throws.
        /// </summary>
        public static void EnforceOnce(string gameFolder, string playerName)
        {
            if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(playerName))
            {
                return;
            }

            byte[] templateBytes;
            try
            {
                templateBytes = BuildProfileBytes(playerName);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"ProfileTemplateEnforcer: failed to build template: {ex.Message}");
                return;
            }

            foreach (string path in ProfilePatterns.SelectMany(pattern => SafeEnumerate(gameFolder, pattern)))
            {
                try
                {
                    File.WriteAllBytes(path, templateBytes);
                }
                catch (Exception ex)
                {
                    DebugLogger.Error($"ProfileTemplateEnforcer: failed to overwrite '{path}': {ex.Message}");
                }
            }
        }

        /// <summary>Rebuilds the compressed profile bytes from the bundled template with a new name.</summary>
        private static byte[] BuildProfileBytes(string playerName)
        {
            byte[] template = LoadTemplateBytes();
            byte[] decompressed = GameProfileNameWriter.Inflate(template);

            byte[] nameBytes = Encoding.ASCII.GetBytes(playerName);
            int copyLen = Math.Min(nameBytes.Length, GameProfileNameWriter.NameBufferSize - 1);

            Array.Clear(decompressed, GameProfileNameWriter.NameOffset, GameProfileNameWriter.NameBufferSize);
            Array.Copy(nameBytes, 0, decompressed, GameProfileNameWriter.NameOffset, copyLen);

            return GameProfileNameWriter.Deflate(decompressed);
        }

        private static byte[] LoadTemplateBytes()
        {
            using Stream resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(TemplateResourceName)
                ?? throw new InvalidOperationException($"Embedded resource '{TemplateResourceName}' not found.");
            using var ms = new MemoryStream();
            resourceStream.CopyTo(ms);
            return ms.ToArray();
        }

        private static System.Collections.Generic.IEnumerable<string> SafeEnumerate(string root, string pattern)
        {
            try
            {
                return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"ProfileTemplateEnforcer: cannot enumerate '{pattern}' under '{root}': {ex.Message}");
                return Array.Empty<string>();
            }
        }
    }
}
