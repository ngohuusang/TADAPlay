using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Fetches an account's WireGuard profile straight from the VPN server.
    ///
    /// Profiles are pinned per account and served by filename, so the only input is the
    /// username. This is the authoritative copy: the one cached on disk can be from an older
    /// session, and the login response does not always carry one.
    /// </summary>
    public static class VpnProfileDownloader
    {
        private const string BaseUrl = "https://openvpn.aoe2.io.vn/download.php?file=";

        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        /// <summary>
        /// Downloads <paramref name="username"/>'s profile, or returns null if it cannot be had.
        /// Never throws: no profile from here simply means falling back to the cached copy.
        /// </summary>
        public static string TryDownload(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;

            // The username lands in a URL and, downstream, in a filename - so anything that
            // could climb out of either is refused rather than escaped.
            string safe = username.Trim();
            if (safe.Any(c => c == '/' || c == '\\' || c == '?' || c == '&' || c == ':' ||
                              c == '"' || char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                DebugLogger.Warn($"VpnProfileDownloader: refusing to fetch a profile for an " +
                                 $"unexpected username '{username}'.");
                return null;
            }

            try
            {
                string url = BaseUrl + Uri.EscapeDataString(safe + ".conf");
                string content = Http.GetStringAsync(url).GetAwaiter().GetResult();

                if (!LooksLikeProfile(content))
                {
                    // A missing file typically comes back as an error page with status 200, so
                    // the shape of the body is what decides, not the status code.
                    DebugLogger.Warn($"VpnProfileDownloader: response for '{safe}' is not a " +
                                     $"WireGuard profile ({content?.Length ?? 0} bytes); ignoring it.");
                    return null;
                }

                DebugLogger.Info($"VpnProfileDownloader: downloaded profile for '{safe}' " +
                                 $"({content.Length} bytes).");
                return content;
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"VpnProfileDownloader: cannot download profile for '{safe}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Whether this really is a WireGuard config. Guards against handing an HTML error page
        /// to the tunnel, which would fail later and much less clearly.
        /// </summary>
        private static bool LooksLikeProfile(string content)
        {
            if (string.IsNullOrWhiteSpace(content) || content.Length > 64 * 1024) return false;
            return content.Contains("[Interface]", StringComparison.OrdinalIgnoreCase)
                && content.Contains("[Peer]", StringComparison.OrdinalIgnoreCase)
                && content.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase)
                && content.Contains("Endpoint", StringComparison.OrdinalIgnoreCase);
        }
    }
}
