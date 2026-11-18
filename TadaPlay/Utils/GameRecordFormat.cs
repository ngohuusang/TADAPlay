using System;
using System.IO;

namespace TadaPlay.Utils
{
    public enum GameRecordFormat
    {
        Unknown,
        UserPatch,
        SteamHd
    }

    public static class GameRecordFormats
    {
        public static GameRecordFormat FromPath(string path)
        {
            string extension = Path.GetExtension(path);
            if (string.Equals(extension, ".mgz", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".mgx", StringComparison.OrdinalIgnoreCase))
            {
                return GameRecordFormat.UserPatch;
            }

            if (string.Equals(extension, ".aoe2record", StringComparison.OrdinalIgnoreCase))
            {
                return GameRecordFormat.SteamHd;
            }

            return GameRecordFormat.Unknown;
        }

        public static string Extension(GameRecordFormat format) => format switch
        {
            GameRecordFormat.UserPatch => ".mgz",
            GameRecordFormat.SteamHd => ".aoe2record",
            _ => string.Empty
        };

        public static string WireName(GameRecordFormat format) => format switch
        {
            GameRecordFormat.UserPatch => "userpatch",
            GameRecordFormat.SteamHd => "steam-hd",
            _ => "unknown"
        };

        public static string DisplayName(GameRecordFormat format) => format switch
        {
            GameRecordFormat.UserPatch => "AOE2 WK",
            GameRecordFormat.SteamHd => "Steam HD",
            _ => "không xác định"
        };
    }
}
