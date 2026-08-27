using System;
using Newtonsoft.Json;

namespace TadaPlay.Common.Models
{
    /// <summary>
    /// One hotkey layout kept against the player's TADA account.
    ///
    /// The editor otherwise only ever touches player*.hki inside the game folder, which is the
    /// one place a reinstall wipes - so a backup exists to survive that, and to be pulled down
    /// on another machine.
    ///
    /// This is the LISTING shape: the layout itself is not carried here. Drawing a picker does
    /// not need the bytes, and sending every layout to draw a list of names would mean shipping
    /// the whole backup set every time the picker opens (see api.php?action=hotkey_backups).
    /// </summary>
    public class HotkeyBackup
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>Size of the stored .hki, in bytes.</summary>
        [JsonProperty("byte_size")]
        public int ByteSize { get; set; }

        /// <summary>Server time the backup was made, as sent ("yyyy-MM-dd HH:mm:ss").</summary>
        [JsonProperty("created_at")]
        public string CreatedAt { get; set; }

        /// <summary>
        /// <see cref="CreatedAt"/> as a date, or null when the server sent something unparseable.
        /// Local time, because it is shown next to a name the player chose themselves.
        /// </summary>
        public DateTime? CreatedLocal =>
            DateTime.TryParse(CreatedAt, out DateTime parsed) ? parsed : null;
    }

    /// <summary>Response shape for api.php?action=hotkey_backups.</summary>
    public class HotkeyBackupListResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("backups")]
        public System.Collections.Generic.List<HotkeyBackup> Backups { get; set; }
    }
}
