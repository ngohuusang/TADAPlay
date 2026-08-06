using System;
using System.IO;
using System.Reflection;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Copies one of the two app-bundled WK launcher exes into the game's age2_x1 folder,
    /// matching the display mode chosen in Settings, and returns the path to launch. Re-copying
    /// on every "Bắt đầu" click means the mode toggle takes effect immediately and the game is
    /// always started through this app rather than some other individually-kept shortcut.
    ///
    /// The exes are embedded as plain manifest resources (see TadaPlay.csproj's EmbeddedResource
    /// items with explicit LogicalName), not via a .resx System.Byte[] ResXFileRef - the
    /// .resx/ResourceManager byte[] deserializer only has a fast path for small resources; past
    /// a few hundred bytes it falls through to a type-activation path that can't construct an
    /// array, throwing MissingMethodException at runtime for files this size (~3MB each).
    /// </summary>
    public static class GameExecutablePreparer
    {
        public const string CenterMode = "center";
        public const string WideMode = "wide";

        private const string CenterExeResourceName = "TadaPlay.Resources.WK-center.exe";
        private const string WideExeResourceName = "TadaPlay.Resources.WK-wide.exe";

        public static string PrepareAndGetExePath(string gameFolder, string mode)
        {
            if (string.IsNullOrWhiteSpace(gameFolder)) return null;

            string age2X1Dir = Path.Combine(gameFolder, "age2_x1");
            if (!Directory.Exists(age2X1Dir))
            {
                DebugLogger.Error($"GameExecutablePreparer: 'age2_x1' folder not found under '{gameFolder}'.");
                return null;
            }

            bool center = string.Equals(mode, CenterMode, StringComparison.OrdinalIgnoreCase);

            // TEMPORARY: copying the app-bundled WK launcher into age2_x1 is disabled. Launch the
            // game's own existing WK exe directly instead (the downloaded game already ships it).
            // Re-enable the copy block below to restore the bundled-launcher behaviour.
            string existingExe = Path.Combine(age2X1Dir, center ? "age2-WK-center.exe" : "age2-WK.exe");
            if (!File.Exists(existingExe))
                DebugLogger.Error($"GameExecutablePreparer: expected game launcher not found: '{existingExe}'.");
            return existingExe; // returned even if missing - GameLauncher reports FileMissing cleanly.

            /* Copy the app-bundled WK launcher into age2_x1 (disabled temporarily - see above):
            string resourceName = center ? CenterExeResourceName : WideExeResourceName;
            string fileName = center ? "WK-center.exe" : "WK-wide.exe";
            string destPath = Path.Combine(age2X1Dir, fileName);

            try
            {
                using Stream resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
                using FileStream destStream = File.Create(destPath);
                resourceStream.CopyTo(destStream);
                return destPath;
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"GameExecutablePreparer: failed to copy '{fileName}' to '{destPath}': {ex.Message}");
                return null;
            }
            */
        }
    }
}
