using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Resolves AoE2 hotkey command string-ids (e.g. 19214 -> "Economic Buildings") to display
    /// text by reading the STRINGTABLE resources out of the game's own language*.dll files.
    ///
    /// The DLLs are plain Win32 PE resource DLLs: STRINGTABLE (resource type 6) stores strings in
    /// blocks of 16, each string prefixed by a uint16 character count and encoded UTF-16LE. String
    /// number N lives in block (N/16)+1 at index N%16. We parse the PE/.rsrc directory ourselves so
    /// there's no LoadLibrary of a foreign-architecture DLL and no dependency on the current locale.
    /// Results are cached per source folder.
    /// </summary>
    public static class GameLanguageStrings
    {
        private static readonly string[] DllNames = { "language.dll", "language_x1.dll", "language_x1_p1.dll" };

        private static readonly object _lock = new object();
        private static string _cachedFolder;
        private static Dictionary<int, string> _cache;

        /// <summary>
        /// Loads (and caches) the string table for <paramref name="gameFolder"/>. Never throws -
        /// an unreadable DLL just yields fewer names, and the editor falls back to showing the raw id.
        /// </summary>
        public static Dictionary<int, string> Load(string gameFolder)
        {
            lock (_lock)
            {
                if (_cache != null && string.Equals(_cachedFolder, gameFolder, StringComparison.OrdinalIgnoreCase))
                    return _cache;

                var strings = new Dictionary<int, string>();
                foreach (string dll in FindLanguageDlls(gameFolder))
                {
                    try
                    {
                        ReadStringTables(File.ReadAllBytes(dll), strings);
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Warn($"GameLanguageStrings: failed reading '{dll}': {ex.Message}");
                    }
                }

                DebugLogger.Info($"GameLanguageStrings: resolved {strings.Count} strings from '{gameFolder}'.");
                _cachedFolder = gameFolder;
                _cache = strings;
                return strings;
            }
        }

        public static void Invalidate()
        {
            lock (_lock) { _cache = null; _cachedFolder = null; }
        }

        // Prefer a DLL sitting directly in the game root; fall back to the first match found anywhere
        // beneath it (Voobly mod folders sometimes carry their own copies).
        private static IEnumerable<string> FindLanguageDlls(string gameFolder)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
                return result;

            foreach (string name in DllNames)
            {
                string atRoot = Path.Combine(gameFolder, name);
                if (File.Exists(atRoot)) { result.Add(atRoot); continue; }

                try
                {
                    foreach (string found in Directory.EnumerateFiles(gameFolder, name, SearchOption.AllDirectories))
                    {
                        result.Add(found);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Warn($"GameLanguageStrings: enumerate '{name}' failed: {ex.Message}");
                }
            }
            return result;
        }

        private static void ReadStringTables(byte[] d, Dictionary<int, string> into)
        {
            uint peOff = BitConverter.ToUInt32(d, 0x3C);
            if (d[peOff] != 'P' || d[peOff + 1] != 'E') return;

            int coff = (int)peOff + 4;
            ushort numSections = BitConverter.ToUInt16(d, coff + 2);
            ushort optSize = BitConverter.ToUInt16(d, coff + 16);
            int secTbl = coff + 20 + optSize;

            uint rsrcVaddr = 0, rsrcRaw = 0;
            bool found = false;
            for (int i = 0; i < numSections; i++)
            {
                int o = secTbl + i * 40;
                string name = Encoding.ASCII.GetString(d, o, 8).TrimEnd('\0');
                if (name == ".rsrc")
                {
                    rsrcVaddr = BitConverter.ToUInt32(d, o + 12);
                    rsrcRaw = BitConverter.ToUInt32(d, o + 20);
                    found = true;
                    break;
                }
            }
            if (!found) return;

            int Rva2Off(uint rva) => (int)(rva - rsrcVaddr + rsrcRaw);

            // Walk the 3-level resource tree. Only type id 6 (RT_STRING) matters; for those the
            // second-level id is the string block number.
            void Walk(int dirOff, int level, int blockId)
            {
                ushort named = BitConverter.ToUInt16(d, dirOff + 12);
                ushort ids = BitConverter.ToUInt16(d, dirOff + 14);
                int entries = dirOff + 16;
                for (int i = 0; i < named + ids; i++)
                {
                    uint eid = BitConverter.ToUInt32(d, entries + i * 8);
                    uint offset = BitConverter.ToUInt32(d, entries + i * 8 + 4);

                    if (level == 0 && eid != 6) continue; // only RT_STRING

                    if ((offset & 0x80000000) != 0)
                    {
                        int sub = Rva2Off(rsrcVaddr + (offset & 0x7FFFFFFF));
                        Walk(sub, level + 1, level == 1 ? (int)eid : blockId);
                    }
                    else
                    {
                        int dataEntry = Rva2Off(rsrcVaddr + offset);
                        uint dataRva = BitConverter.ToUInt32(d, dataEntry);
                        uint size = BitConverter.ToUInt32(d, dataEntry + 4);
                        ParseBlock(Rva2Off(dataRva), (int)size, blockId, into, d);
                    }
                }
            }

            Walk((int)rsrcRaw, 0, 0);
        }

        private static void ParseBlock(int off, int size, int blockId, Dictionary<int, string> into, byte[] d)
        {
            int p = off, end = off + size;
            for (int idx = 0; idx < 16; idx++)
            {
                if (p + 2 > end) break;
                int len = BitConverter.ToUInt16(d, p); p += 2;
                if (len == 0) continue;
                if (p + len * 2 > end) break;
                string s = Encoding.Unicode.GetString(d, p, len * 2);
                p += len * 2;
                into[(blockId - 1) * 16 + idx] = s;
            }
        }
    }
}
