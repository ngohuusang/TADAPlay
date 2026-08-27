using System;

namespace TadaPlay.Exceptions
{
    /// <summary>
    /// Thrown when the server refuses this build as too old. Carries the download URL so the UI
    /// can offer the update directly instead of making the player hunt for it.
    /// </summary>
    internal class UpdateRequiredException : Exception
    {
        public string MinVersion { get; }
        public string DownloadUrl { get; }

        public UpdateRequiredException(string message, string minVersion, string downloadUrl)
            : base(message)
        {
            MinVersion = minVersion;
            DownloadUrl = downloadUrl;
        }
    }
}
