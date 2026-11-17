using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TadaPlay.Utils.Mods
{
    /// <summary>
    /// Reads and writes the game's .drs archives - the containers its graphics live in.
    ///
    /// A mod ships loose .slp files (a folder of "2223.slp", "5041.slp", ...), which is what
    /// Voobly loads directly. The game itself cannot: offline and over GameRanger it only ever
    /// reads .drs, so a mod's graphics have to be put inside the archive that already holds the
    /// graphic each one replaces.
    ///
    /// The format, confirmed against the game's own files:
    ///   header  40 bytes copyright, 4 bytes "1.00", 12 bytes "tribe", int32 table count,
    ///           int32 offset of the first file's data
    ///   table   4 bytes file type REVERSED ("slp " is stored as " pls"), int32 offset of this
    ///           table's entry list, int32 number of files
    ///   entry   int32 id, int32 offset, int32 size   (12 bytes, one per file)
    /// File data follows packed end to end, no padding and no alignment.
    ///
    /// Archives run to hundreds of megabytes (the v1.5 data mod's is 263 MB), so everything here
    /// works from the table of contents and streams file data, rather than holding an archive in
    /// memory.
    /// </summary>
    public static class DrsArchive
    {
        // "Copyright (c) 1997 Ensemble Studios." + 0x1A (DOS end-of-file) padded to 40 bytes.
        private const string CopyrightText = "Copyright (c) 1997 Ensemble Studios.";
        private const string Version = "1.00";
        private const string FileType = "tribe";

        private const int HeaderSize = 64;
        private const int TableSize = 12;
        private const int EntrySize = 12;

        /// <summary>One file inside an archive, or one about to be put into it.</summary>
        public sealed class Entry
        {
            /// <summary>The 4-character table type, unreversed and space-padded: "slp ", "bina".</summary>
            public string Type;
            public int Id;
            public byte[] Data;
        }

        /// <summary>Where a file sits in an archive, without its contents.</summary>
        public readonly struct Location
        {
            public readonly int Offset;
            public readonly int Size;
            public Location(int offset, int size) { Offset = offset; Size = size; }
        }

        /// <summary>
        /// An archive's table of contents: every file's type, id, position and length. Cheap on
        /// any archive - a few hundred kilobytes of tables even for the 263 MB one.
        /// </summary>
        public sealed class Toc
        {
            public string Path;

            /// <summary>Table types in the order the archive stores them.</summary>
            public readonly List<string> Types = new List<string>();

            /// <summary>Ids per type, in stored order.</summary>
            public readonly Dictionary<string, List<int>> IdsByType = new Dictionary<string, List<int>>();

            public readonly Dictionary<(string Type, int Id), Location> Files =
                new Dictionary<(string, int), Location>();

            public bool Contains(string type, int id) => Files.ContainsKey((type, id));
            public int Count => Files.Count;
        }

        /// <summary>
        /// The 4-character table type a loose file belongs in, from its extension. The game's
        /// types are four characters, which is why .bin files live in "bina" rather than "bin ".
        /// </summary>
        public static string TypeForExtension(string extension)
        {
            switch ((extension ?? string.Empty).TrimStart('.').ToLowerInvariant())
            {
                case "slp": return "slp ";
                case "wav": return "wav ";
                case "shp": return "shp ";
                case "bin":
                case "bina": return "bina";
                default: return null;
            }
        }

        /// <summary>Reads an archive's table of contents. Does not read any file data.</summary>
        public static Toc ReadToc(string path)
        {
            var toc = new Toc { Path = path };
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(stream);

            byte[] header = reader.ReadBytes(HeaderSize);
            if (header.Length < HeaderSize || Encoding.ASCII.GetString(header, 44, 5) != FileType)
            {
                throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a .drs archive.");
            }

            int tableCount = BitConverter.ToInt32(header, 56);
            var tables = new List<(string Type, int Offset, int Count)>();
            for (int i = 0; i < tableCount; i++)
            {
                stream.Position = HeaderSize + i * TableSize;
                byte[] t = reader.ReadBytes(TableSize);
                // Stored reversed, so read the four bytes back to front.
                string type = new string(new[] { (char)t[3], (char)t[2], (char)t[1], (char)t[0] });
                tables.Add((type, BitConverter.ToInt32(t, 4), BitConverter.ToInt32(t, 8)));
            }

            foreach (var table in tables)
            {
                toc.Types.Add(table.Type);
                var ids = new List<int>(table.Count);
                toc.IdsByType[table.Type] = ids;

                stream.Position = table.Offset;
                byte[] block = reader.ReadBytes(table.Count * EntrySize);
                for (int j = 0; j < table.Count; j++)
                {
                    int id = BitConverter.ToInt32(block, j * EntrySize);
                    int offset = BitConverter.ToInt32(block, j * EntrySize + 4);
                    int size = BitConverter.ToInt32(block, j * EntrySize + 8);

                    if (offset < 0 || size < 0 || offset + (long)size > stream.Length)
                    {
                        throw new InvalidDataException(
                            $"'{Path.GetFileName(path)}' entry {id} points outside the file - the archive is damaged.");
                    }

                    ids.Add(id);
                    toc.Files[(table.Type, id)] = new Location(offset, size);
                }
            }
            return toc;
        }

        /// <summary>One file's bytes, read straight from where the table of contents says it is.</summary>
        public static byte[] ReadFile(string path, string type, int id)
        {
            Toc toc = ReadToc(path);
            return ReadFile(path, toc, type, id);
        }

        public static byte[] ReadFile(string path, Toc toc, string type, int id)
        {
            if (!toc.Files.TryGetValue((type, id), out Location where)) return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            stream.Position = where.Offset;
            var data = new byte[where.Size];
            int read = 0;
            while (read < data.Length)
            {
                int n = stream.Read(data, read, data.Length - read);
                if (n <= 0) throw new EndOfStreamException($"'{Path.GetFileName(path)}' ended early reading {id}.");
                read += n;
            }
            return data;
        }

        /// <summary>Everything in an archive, contents included. Only for small archives.</summary>
        public static List<Entry> Read(string path)
        {
            Toc toc = ReadToc(path);
            var result = new List<Entry>(toc.Count);
            foreach (string type in toc.Types)
            {
                foreach (int id in toc.IdsByType[type])
                {
                    result.Add(new Entry { Type = type, Id = id, Data = ReadFile(path, toc, type, id) });
                }
            }
            return result;
        }

        /// <summary>
        /// Rewrites an archive with some files replaced and others added, keeping everything else
        /// exactly where it was. Replaced files keep their position in the archive; added ones go
        /// to the end of their type's table in id order.
        ///
        /// Written to a temporary file next to the original and moved into place only once it is
        /// complete, so an interrupted write - or a full disk part-way through 263 MB - leaves the
        /// game with the archive it already had rather than a truncated one it cannot start on.
        /// </summary>
        public static void Update(string path, IReadOnlyList<Entry> replacements,
                                  IReadOnlyList<Entry> additions = null,
                                  IReadOnlyList<(string Type, int Id)> removals = null)
        {
            Toc toc = ReadToc(path);

            var changed = new Dictionary<(string, int), byte[]>();
            foreach (Entry entry in replacements ?? Array.Empty<Entry>())
            {
                changed[(entry.Type, entry.Id)] = entry.Data;
            }

            var dropped = new HashSet<(string, int)>(removals ?? Array.Empty<(string, int)>());

            // The layout to write: existing files in their existing order, minus anything being
            // taken back out, then anything new.
            var types = new List<string>(toc.Types);
            var plan = new Dictionary<string, List<int>>();
            foreach (string type in types)
            {
                plan[type] = toc.IdsByType[type].Where(id => !dropped.Contains((type, id))).ToList();
            }

            var added = new Dictionary<(string, int), byte[]>();
            foreach (Entry entry in (additions ?? Array.Empty<Entry>()).OrderBy(e => e.Id))
            {
                if (toc.Contains(entry.Type, entry.Id) || added.ContainsKey((entry.Type, entry.Id)))
                {
                    changed[(entry.Type, entry.Id)] = entry.Data;   // already there: a replacement
                    continue;
                }
                if (!plan.TryGetValue(entry.Type, out List<int> ids))
                {
                    types.Add(entry.Type);
                    ids = new List<int>();
                    plan[entry.Type] = ids;
                }
                ids.Add(entry.Id);
                added[(entry.Type, entry.Id)] = entry.Data;
            }

            int entryCount = plan.Sum(p => p.Value.Count);
            int firstFileOffset = HeaderSize + types.Count * TableSize + entryCount * EntrySize;

            string tempPath = path + ".tadaplay-new";
            try
            {
                using (var source = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                using (var w = new BinaryWriter(target))
                {
                    w.Write(Fixed(CopyrightText, 40));
                    w.Write(Fixed(Version, 4));
                    w.Write(Fixed(FileType, 12));
                    w.Write(types.Count);
                    w.Write(firstFileOffset);

                    int entryOffset = HeaderSize + types.Count * TableSize;
                    foreach (string type in types)
                    {
                        string padded = (type + "    ").Substring(0, 4);
                        w.Write(new[] { (byte)padded[3], (byte)padded[2], (byte)padded[1], (byte)padded[0] });
                        w.Write(entryOffset);
                        w.Write(plan[type].Count);
                        entryOffset += plan[type].Count * EntrySize;
                    }

                    int dataOffset = firstFileOffset;
                    foreach (string type in types)
                    {
                        foreach (int id in plan[type])
                        {
                            int size = SizeOf(toc, changed, added, type, id);
                            w.Write(id);
                            w.Write(dataOffset);
                            w.Write(size);
                            dataOffset += size;
                        }
                    }

                    var buffer = new byte[1 << 20];
                    foreach (string type in types)
                    {
                        foreach (int id in plan[type])
                        {
                            if (changed.TryGetValue((type, id), out byte[] replacement) ||
                                added.TryGetValue((type, id), out replacement))
                            {
                                target.Write(replacement, 0, replacement.Length);
                                continue;
                            }

                            Location where = toc.Files[(type, id)];
                            source.Position = where.Offset;
                            int left = where.Size;
                            while (left > 0)
                            {
                                int n = source.Read(buffer, 0, Math.Min(buffer.Length, left));
                                if (n <= 0)
                                {
                                    throw new EndOfStreamException(
                                        $"'{Path.GetFileName(path)}' ended early copying {id}.");
                                }
                                target.Write(buffer, 0, n);
                                left -= n;
                            }
                        }
                    }
                }

                File.Delete(path);
                File.Move(tempPath, path);
            }
            catch
            {
                try { File.Delete(tempPath); } catch (IOException) { }
                throw;
            }
        }

        private static int SizeOf(Toc toc, Dictionary<(string, int), byte[]> changed,
                                  Dictionary<(string, int), byte[]> added, string type, int id)
        {
            if (changed.TryGetValue((type, id), out byte[] data)) return data.Length;
            if (added.TryGetValue((type, id), out data)) return data.Length;
            return toc.Files[(type, id)].Size;
        }

        /// <summary>
        /// The loose files a mod folder offers. The file name is the id the game looks the graphic
        /// up by, so "2223.slp" replaces graphic 2223; anything not named that way is skipped
        /// rather than guessed at.
        /// </summary>
        public static List<Entry> ReadLooseFiles(string folder)
        {
            var result = new List<Entry>();
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                string type = TypeForExtension(Path.GetExtension(file));
                if (type == null) continue;
                if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out int id)) continue;

                result.Add(new Entry { Type = type, Id = id, Data = File.ReadAllBytes(file) });
            }
            return result;
        }

        private static byte[] Fixed(string text, int length)
        {
            var buffer = new byte[length];
            Encoding.ASCII.GetBytes(text, 0, Math.Min(text.Length, length), buffer, 0);
            return buffer;
        }
    }
}
