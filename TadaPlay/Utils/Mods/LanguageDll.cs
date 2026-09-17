using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TadaPlay.Utils.Mods
{
    /// <summary>
    /// Reads the text out of the game's language*.dll files.
    ///
    /// They are plain Win32 resource DLLs. String tables hold sixteen strings per block, each one
    /// a uint16 character count followed by UTF-16 text; string N lives in block (N/16)+1 at index
    /// N%16. The PE and .rsrc directory are parsed here rather than loaded through LoadLibrary, so
    /// a 32-bit game DLL can be read from this 64-bit app and the result never depends on the
    /// machine's locale.
    /// </summary>
    public static class LanguageDll
    {
        private const int RT_STRING = 6;

        public sealed class Strings
        {
            public readonly Dictionary<int, string> ById = new Dictionary<int, string>();

            /// <summary>
            /// The resource language the DLL's own string tables are stored under, which anything
            /// written back has to match - the game asks for a language when it loads a string,
            /// and tables filed under a different one are invisible to it.
            /// </summary>
            public ushort Language;
        }

        public static Strings Read(string dllPath)
        {
            var result = new Strings();
            byte[] d = File.ReadAllBytes(dllPath);

            uint peOffset = BitConverter.ToUInt32(d, 0x3C);
            if (d[peOffset] != 'P' || d[peOffset + 1] != 'E')
            {
                throw new InvalidDataException($"'{Path.GetFileName(dllPath)}' is not a Windows DLL.");
            }

            int coff = (int)peOffset + 4;
            ushort sectionCount = BitConverter.ToUInt16(d, coff + 2);
            ushort optionalSize = BitConverter.ToUInt16(d, coff + 16);
            int sectionTable = coff + 20 + optionalSize;

            uint rsrcVirtual = 0, rsrcRaw = 0;
            bool found = false;
            for (int i = 0; i < sectionCount; i++)
            {
                int o = sectionTable + i * 40;
                if (Encoding.ASCII.GetString(d, o, 8).TrimEnd('\0') == ".rsrc")
                {
                    rsrcVirtual = BitConverter.ToUInt32(d, o + 12);
                    rsrcRaw = BitConverter.ToUInt32(d, o + 20);
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                throw new InvalidDataException($"'{Path.GetFileName(dllPath)}' carries no resources.");
            }

            int ToOffset(uint rva) => (int)(rva - rsrcVirtual + rsrcRaw);

            // The resource tree is three levels: type, then name (the block number for strings),
            // then language.
            void Walk(int dirOffset, int level, int block)
            {
                ushort named = BitConverter.ToUInt16(d, dirOffset + 12);
                ushort ids = BitConverter.ToUInt16(d, dirOffset + 14);
                int entries = dirOffset + 16;

                for (int i = 0; i < named + ids; i++)
                {
                    uint id = BitConverter.ToUInt32(d, entries + i * 8);
                    uint offset = BitConverter.ToUInt32(d, entries + i * 8 + 4);

                    if (level == 0 && id != RT_STRING) continue;

                    if ((offset & 0x80000000) != 0)
                    {
                        Walk(ToOffset(rsrcVirtual + (offset & 0x7FFFFFFF)), level + 1,
                             level == 1 ? (int)id : block);
                    }
                    else
                    {
                        if (level == 2) result.Language = (ushort)id;
                        int dataEntry = ToOffset(rsrcVirtual + offset);
                        ReadBlock(d, ToOffset(BitConverter.ToUInt32(d, dataEntry)),
                                  (int)BitConverter.ToUInt32(d, dataEntry + 4), block, result.ById);
                    }
                }
            }

            Walk((int)rsrcRaw, 0, 0);
            return result;
        }

        private static void ReadBlock(byte[] d, int offset, int size, int block, Dictionary<int, string> into)
        {
            int p = offset, end = offset + size;
            for (int index = 0; index < 16; index++)
            {
                if (p + 2 > end) break;
                int length = BitConverter.ToUInt16(d, p);
                p += 2;
                if (length == 0) continue;          // an empty slot, not a string
                if (p + length * 2 > end) break;

                into[(block - 1) * 16 + index] = Encoding.Unicode.GetString(d, p, length * 2);
                p += length * 2;
            }
        }

        /// <summary>
        /// Writes a language DLL: a copy of <paramref name="baseDllPath"/> with these strings in
        /// it. Returns how many blocks were rewritten.
        ///
        /// Built by updating a copy of the game's own DLL through the Windows resource API rather
        /// than by emitting a PE file here. The game's DLL is a real Windows binary - headers,
        /// sections, a version resource - and writing one from scratch means getting all of that
        /// right forever, to gain nothing: the only part that needs to change is the strings.
        ///
        /// Whole blocks are replaced, sixteen at a time, so every block that gains a string is
        /// written complete - the base DLL's own text for that block included, or changing one
        /// name would blank the fifteen ids sharing its block.
        /// </summary>
        public static int Build(string baseDllPath, string outputPath,
                                IReadOnlyDictionary<int, string> strings, ushort language)
        {
            Strings baseStrings = Read(baseDllPath);

            var merged = new Dictionary<int, string>(baseStrings.ById);
            var blocks = new SortedSet<int>();
            foreach (var kv in strings)
            {
                merged[kv.Key] = kv.Value;
                blocks.Add(BlockOf(kv.Key));
            }

            if (!string.Equals(Path.GetFullPath(baseDllPath), Path.GetFullPath(outputPath),
                               StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(baseDllPath, outputPath, overwrite: true);
            }

            try
            {
                foreach (var pass in Chunk(blocks, BlocksPerPass))
                {
                    WriteBlocks(outputPath, merged, pass, language);
                }
            }
            catch
            {
                // Earlier passes have already been committed to the file, so what is on disk now
                // is a DLL with only some of the mod's text. Remove it rather than leave that
                // where the game would load it.
                try { File.Delete(outputPath); } catch (IOException) { }
                throw;
            }

            return blocks.Count;
        }

        /// <summary>
        /// How many blocks are written per pass over the file.
        ///
        /// Windows' resource updater refuses a single pass that grows the file too far: writing a
        /// full mod's text (10,792 strings, 1,114 blocks) into the game's own 45 KB
        /// language_x1_p1.dll fails with ERROR_INVALID_DATA once the result passes ~200 KB, even
        /// though the very same text writes fine into a DLL that is already large. Split into
        /// passes it succeeds, each pass reopening the file the previous one produced. 200 blocks
        /// is comfortably below where it broke (about 330), and the whole file still takes well
        /// under a second.
        /// </summary>
        private const int BlocksPerPass = 200;

        private static IEnumerable<List<int>> Chunk(IEnumerable<int> blocks, int size)
        {
            var batch = new List<int>(size);
            foreach (int block in blocks)
            {
                batch.Add(block);
                if (batch.Count == size)
                {
                    yield return batch;
                    batch = new List<int>(size);
                }
            }
            if (batch.Count > 0) yield return batch;
        }

        private static void WriteBlocks(string path, IReadOnlyDictionary<int, string> strings,
                                        List<int> blocks, ushort language)
        {
            IntPtr handle = BeginUpdateResourceW(path, bDeleteExistingResources: false);
            if (handle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"Không mở được '{Path.GetFileName(path)}' để ghi văn bản.");
            }

            bool committed = false;
            try
            {
                foreach (int block in blocks)
                {
                    byte[] packed = PackBlock(strings, block);
                    if (!UpdateResourceW(handle, (IntPtr)RT_STRING, (IntPtr)block, language,
                                         packed, (uint)packed.Length))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(),
                            $"Không ghi được nhóm văn bản {block}.");
                    }
                }

                if (!EndUpdateResourceW(handle, fDiscard: false))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        $"Không lưu được '{Path.GetFileName(path)}'.");
                }
                committed = true;
            }
            finally
            {
                if (!committed) EndUpdateResourceW(handle, fDiscard: true);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr BeginUpdateResourceW(string pFileName, bool bDeleteExistingResources);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateResourceW(IntPtr hUpdate, IntPtr lpType, IntPtr lpName,
                                                   ushort wLanguage, byte[] lpData, uint cb);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EndUpdateResourceW(IntPtr hUpdate, bool fDiscard);

        /// <summary>
        /// The sixteen-string block a string id belongs to. Ids are grouped, so changing one
        /// string means rewriting the whole block it sits in - which is why the text already in
        /// that block has to be carried over rather than dropped.
        /// </summary>
        public static int BlockOf(int stringId) => (stringId / 16) + 1;

        /// <summary>Packs sixteen strings into the block layout the resource holds.</summary>
        public static byte[] PackBlock(IReadOnlyDictionary<int, string> strings, int block)
        {
            using var stream = new MemoryStream();
            using var w = new BinaryWriter(stream);

            for (int index = 0; index < 16; index++)
            {
                int id = (block - 1) * 16 + index;
                if (!strings.TryGetValue(id, out string text) || text == null)
                {
                    w.Write((ushort)0);             // empty slot
                    continue;
                }

                // The count is UTF-16 code units, not characters, so text outside the basic
                // plane counts as the two units it actually occupies.
                byte[] encoded = Encoding.Unicode.GetBytes(text);
                w.Write((ushort)(encoded.Length / 2));
                w.Write(encoded);
            }
            return stream.ToArray();
        }
    }
}
