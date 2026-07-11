using System;
using System.IO;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Local disk cache for a user's WireGuard profile, keyed by username. The server now
    /// pins one profile permanently per account, so the same content is valid across
    /// sessions - caching it lets the client fall back to a working VPN config if a
    /// login/auth call succeeds without one (e.g. a transient server hiccup) while still
    /// always preferring whatever the server just returned.
    /// </summary>
    public static class VpnProfileCache
    {
        private static string CacheFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TadaPlay", "vpn");

        private static string GetPath(string username) => Path.Combine(CacheFolder, $"{username}.conf");

        public static void Save(string username, string configContent)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(configContent)) return;
            Directory.CreateDirectory(CacheFolder);
            File.WriteAllText(GetPath(username), configContent);
        }

        public static bool TryLoad(string username, out string configContent)
        {
            configContent = null;
            if (string.IsNullOrWhiteSpace(username)) return false;

            string path = GetPath(username);
            if (!File.Exists(path)) return false;

            configContent = File.ReadAllText(path);
            return true;
        }
    }
}
