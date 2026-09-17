using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TadaPlay.Utils.Mods
{
    /// <summary>
    /// Reads a mod's "id=text" language.ini - the file Voobly reads directly and the local game
    /// does not read at all, which is why its text has to be compiled into a language DLL for
    /// offline and GameRanger play.
    /// </summary>
    public static class LanguageIni
    {
        /// <summary>
        /// Every id and its text. Later lines win, matching how the game treats a repeated id.
        ///
        /// Encoding: a UTF-8 byte-order mark is honoured, otherwise Windows-1252 - what the game's
        /// own files use, and what makes "Empires®" read as a registered sign rather than mojibake.
        /// </summary>
        public static Dictionary<int, string> Read(string path)
        {
            var result = new Dictionary<int, string>();

            foreach (string raw in File.ReadLines(path, DetectEncoding(path)))
            {
                string line = raw.TrimStart();
                if (line.Length == 0 || line[0] == ';' || line[0] == '[' ||
                    (line.Length > 1 && line[0] == '/' && line[1] == '/'))
                {
                    continue;
                }

                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                if (!int.TryParse(line.Substring(0, equals).Trim(), out int id)) continue;

                result[id] = Unescape(line.Substring(equals + 1).TrimEnd('\r', '\n'));
            }
            return result;
        }

        /// <summary>
        /// Turns the ini's "\n" into a real line break, which is how the same text is stored in
        /// the game's own DLLs - checked against them: of the 5853 ids this mod shares with the
        /// game, converting raises exact matches from 4949 to 5329, and the rest are the mod's
        /// own rewording.
        ///
        /// Nothing else is translated. "&&" was the only other candidate and it appears in a
        /// single string, far too little to justify rewriting text a modder typed deliberately.
        /// </summary>
        private static string Unescape(string text)
        {
            return text.IndexOf('\\') < 0 ? text : text.Replace("\\n", "\n");
        }

        /// <summary>
        /// Windows-1252 unless the file is really UTF-8.
        ///
        /// The game's own files are Windows-1252, and so are most mods - but a Vietnamese mod
        /// cannot be: the language has no room in that code page, so those files are UTF-8, and
        /// usually written without a byte-order mark. Read as 1252 they come out as "Nhà nh?"
        /// instead of "Nhà nhỏ", and that text is then compiled into the DLL and seen in the game.
        ///
        /// So a mark is honoured, and without one the bytes decide: text that decodes as UTF-8
        /// and actually uses a multi-byte sequence is UTF-8. Plain ASCII reads the same either
        /// way, and 1252 text with accented characters is not valid UTF-8, so neither is misread.
        /// </summary>
        private static Encoding DetectEncoding(string path)
        {
            Encoding windows1252 = Encoding.GetEncoding(1252);
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                // Unreadable here means unreadable in a moment when the file is parsed, which
                // reports the failure properly. Fall through to the usual encoding.
                return windows1252;
            }

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Encoding.UTF8;
            }

            bool hasHighBytes = false;
            foreach (byte b in bytes)
            {
                if (b >= 0x80) { hasHighBytes = true; break; }
            }
            if (!hasHighBytes) return windows1252;

            try
            {
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(bytes);
                return new UTF8Encoding(false);
            }
            catch (DecoderFallbackException)
            {
                return windows1252;
            }
        }
    }
}
