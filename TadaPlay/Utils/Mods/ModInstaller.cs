using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json;
using TadaPlay.Logger;

namespace TadaPlay.Utils.Mods
{
    /// <summary>
    /// Installs a graphics/text mod into the local game, so it works offline and over GameRanger
    /// rather than only on Voobly.
    ///
    /// A mod is distributed the way Voobly wants it: loose .slp graphics named after the graphic
    /// they replace ("2223.slp" is graphic 2223), and sometimes a language.ini of replacement
    /// text. Voobly applies both as it launches. Run locally the game does neither - it reads
    /// graphics only from .drs archives and never reads a language.ini at all - which is why a mod
    /// that looks right on Voobly does nothing offline.
    ///
    /// Installing therefore means putting each graphic INTO the archive that already holds that
    /// id. Which archive that is varies per graphic and cannot be assumed: of the mods on one real
    /// install, "No Snow" replaces six ids that live only in terrain.drs, "DE Terrains with grid"
    /// is split across terrain.drs and graphics.drs, and "Short Walls" hits ids held by both
    /// graphics.drs and the data mod's 263 MB gamedata_x1_p1.drs. So every archive in the game's
    /// Data folder and in the Voobly data mod's Data folder is searched, and every archive holding
    /// the id is updated - whichever one the game reads first, it sees the mod.
    ///
    /// Nothing is rebuilt wholesale. Only the ids a mod touches are replaced, and the bytes that
    /// were there first are kept, so removing a mod restores exactly what it overwrote - including
    /// edits made by hand before this app existed.
    /// </summary>
    public static class ModInstaller
    {
        /// <summary>Where the game's own archives live, and where a Voobly data mod keeps its own.</summary>
        private const string GameData = "Data";
        private const string DataMods = @"Voobly Mods\AOC\Data Mods";

        private const string LocalMods = @"Voobly Mods\AOC\Local Mods";

        /// <summary>Graphics with no home anywhere go here: the archive the game reads first.</summary>
        private const string FallbackArchive = @"Data\gamedata_x1_p1.drs";

        private const string TargetDll = "language_x1_p1.dll";
        private const string BaseDll = "language_x1_p1_Original.dll";

        /// <summary>Our own files: the record of what is installed, and what each mod overwrote.</summary>
        private const string Store = @"Data\TadaPlayMods";
        private const string Manifest = @"Data\TadaPlayMods\installed.json";
        private const string Sources = @"Data\TadaPlayMods\sources";

        private static readonly string[] GraphicsFolders = { "drs", "graphics" };

        public sealed class Mod
        {
            public string Name;
            public string Folder;

            [JsonIgnore] public string GraphicsFolder;
            [JsonIgnore] public int GraphicsCount;
            [JsonIgnore] public string LanguageIni;
            [JsonIgnore] public bool HasAnything => GraphicsCount > 0 || LanguageIni != null;
        }

        /// <summary>What one archive gained from an install, for the player to see.</summary>
        public sealed class ArchiveChange
        {
            public string Path;
            public string Label;
            public int Replaced;
            public int Added;
        }

        /// <summary>What an install actually did.</summary>
        public sealed class Report
        {
            public string ModName;
            public readonly List<ArchiveChange> Archives = new List<ArchiveChange>();
            public int GraphicsTotal;
            public int TextStrings;

            /// <summary>Graphics whose id no archive held, so they were added to the fallback.</summary>
            public readonly List<int> AddedNew = new List<int>();

            public int Replaced => Archives.Sum(a => a.Replaced);
            public bool DidSomething => Replaced > 0 || AddedNew.Count > 0 || TextStrings > 0;
        }

        /// <summary>What was recorded for an installed mod, so it can be taken back out.</summary>
        private sealed class Record
        {
            public string Name;
            public string Folder;
            public List<ArchiveRecord> Archives = new List<ArchiveRecord>();
            public bool HasText;
        }

        private sealed class ArchiveRecord
        {
            public string Path;
            public List<int> Replaced = new List<int>();
            public List<int> Added = new List<int>();
        }

        // ---- finding mods -------------------------------------------------------------------

        /// <summary>
        /// The mods already sitting in the player's Voobly folder - the ones they have downloaded
        /// and can currently only use online, which is exactly what this is for.
        /// </summary>
        public static List<Mod> Discover(string gameFolder)
        {
            var result = new List<Mod>();
            string root = Path.Combine(gameFolder ?? string.Empty, LocalMods);
            if (!Directory.Exists(root)) return result;

            foreach (string folder in Directory.EnumerateDirectories(root))
            {
                Mod mod = Inspect(folder);
                if (mod.HasAnything) result.Add(mod);
            }
            return result.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>
        /// A mod the player picked themselves: a folder, or a .zip which is unpacked first. The
        /// unpacked copy is kept (under the game's Data folder) so the mod can be re-installed
        /// later without the player having to find the zip again.
        /// </summary>
        public static Mod Open(string path, string gameFolder)
        {
            if (Directory.Exists(path)) return Inspect(path);

            if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".zip",
                                                     StringComparison.OrdinalIgnoreCase))
            {
                throw new FileNotFoundException($"Không mở được '{path}'.", path);
            }

            string name = Path.GetFileNameWithoutExtension(path);
            string target = Path.Combine(gameFolder, Sources, Safe(name));
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.CreateDirectory(target);
            ZipFile.ExtractToDirectory(path, target);

            // A zip usually holds one folder rather than the files themselves; if so, that folder
            // is the mod - otherwise every mod from a zip would be called after the zip and hold
            // nothing.
            Mod mod = Inspect(target);
            if (!mod.HasAnything)
            {
                string[] inner = Directory.GetDirectories(target);
                if (inner.Length == 1)
                {
                    mod = Inspect(inner[0]);
                    mod.Name = name;
                }
            }
            return mod;
        }

        /// <summary>What a folder offers.</summary>
        public static Mod Inspect(string folder)
        {
            var mod = new Mod { Name = new DirectoryInfo(folder).Name, Folder = folder };

            foreach (string candidate in GraphicsFolders.Select(s => Path.Combine(folder, s)).Append(folder))
            {
                if (!Directory.Exists(candidate)) continue;
                int count = CountGraphics(candidate);
                if (count > 0)
                {
                    mod.GraphicsFolder = candidate;
                    mod.GraphicsCount = count;
                    break;
                }
            }

            string ini = Path.Combine(folder, "language.ini");
            if (File.Exists(ini)) mod.LanguageIni = ini;
            return mod;
        }

        private static int CountGraphics(string folder)
        {
            try
            {
                return Directory.EnumerateFiles(folder).Count(IsGraphic);
            }
            catch (IOException ex)
            {
                DebugLogger.Warn($"ModInstaller: cannot read '{folder}': {ex.Message}");
                return 0;
            }
        }

        private static bool IsGraphic(string file) =>
            DrsArchive.TypeForExtension(Path.GetExtension(file)) != null &&
            int.TryParse(Path.GetFileNameWithoutExtension(file), out _);

        // ---- the archives a graphic could live in --------------------------------------------

        /// <summary>
        /// Every archive worth searching: the game's own Data folder first, then each Voobly data
        /// mod's. Our own backups and any *_Original.drs kept by hand are skipped - they are
        /// copies nothing reads, and writing to them would destroy the only pristine copy there is.
        /// </summary>
        public static List<string> FindArchives(string gameFolder)
        {
            var result = new List<string>();

            void AddFrom(string folder)
            {
                if (!Directory.Exists(folder)) return;
                foreach (string file in Directory.EnumerateFiles(folder, "*.drs").OrderBy(f => f))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (name.EndsWith("_Original", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.IndexOf("tadaplay", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    result.Add(file);
                }
            }

            AddFrom(Path.Combine(gameFolder, GameData));

            string dataMods = Path.Combine(gameFolder, DataMods);
            if (Directory.Exists(dataMods))
            {
                foreach (string mod in Directory.EnumerateDirectories(dataMods).OrderBy(f => f))
                {
                    AddFrom(Path.Combine(mod, GameData));
                }
            }
            return result;
        }

        /// <summary>
        /// A short name for an archive: "Data\graphics.drs" for the game's own, and
        /// "v1.5 Game Data\gamedata_x1_p1.drs" for a data mod's.
        ///
        /// Named after the data mod rather than the folder it sits in, because every one of those
        /// folders is also called "Data" - shortening by folder alone labelled the game's
        /// gamedata_x1_p1.drs and the data mod's identically, so a report listing both looked like
        /// the same file twice.
        /// </summary>
        private static string Label(string gameFolder, string archive)
        {
            string relative = Path.GetRelativePath(gameFolder, archive);
            string[] parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Length <= 2) return relative;

            // ...\Data Mods\<the mod>\Data\<file>
            int at = Array.FindIndex(parts, p => string.Equals(p, "Data Mods", StringComparison.OrdinalIgnoreCase));
            if (at >= 0 && at + 1 < parts.Length - 1)
            {
                return $"{parts[at + 1]}\\{parts[parts.Length - 1]}";
            }
            return $"{parts[parts.Length - 2]}\\{parts[parts.Length - 1]}";
        }

        // ---- what is installed ---------------------------------------------------------------

        private static List<Record> ReadManifest(string gameFolder)
        {
            string path = Path.Combine(gameFolder ?? string.Empty, Manifest);
            if (!File.Exists(path)) return new List<Record>();
            try
            {
                return JsonConvert.DeserializeObject<List<Record>>(File.ReadAllText(path)) ?? new List<Record>();
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"ModInstaller: cannot read the installed-mod list: {ex.Message}");
                return new List<Record>();
            }
        }

        private static void WriteManifest(string gameFolder, List<Record> records)
        {
            string path = Path.Combine(gameFolder, Manifest);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonConvert.SerializeObject(records, Formatting.Indented));
        }

        public static List<Mod> Installed(string gameFolder) =>
            ReadManifest(gameFolder).Select(r => new Mod { Name = r.Name, Folder = r.Folder }).ToList();

        public static bool IsInstalled(string gameFolder, string name) =>
            ReadManifest(gameFolder).Any(r => Same(r.Name, name));

        // ---- installing ------------------------------------------------------------------------

        /// <summary>
        /// Puts a mod into the game. Re-installing one already installed takes the old one back
        /// out first, so the mod's own files can change in between and the result still matches
        /// the folder rather than being layered on itself.
        /// </summary>
        public static Report Install(string gameFolder, Mod mod, Action<string> progress = null)
        {
            if (!mod.HasAnything)
            {
                throw new InvalidOperationException(
                    $"'{mod.Name}' không có tệp .slp hay language.ini nào để cài.");
            }

            if (IsInstalled(gameFolder, mod.Name)) Uninstall(gameFolder, mod.Name, progress);

            var report = new Report { ModName = mod.Name };
            var record = new Record { Name = mod.Name, Folder = mod.Folder };

            if (mod.GraphicsCount > 0)
            {
                progress?.Invoke("Đang đọc danh sách tệp trong game...");
                List<string> archives = FindArchives(gameFolder);
                var tocs = archives.ToDictionary(a => a, a =>
                {
                    try { return DrsArchive.ReadToc(a); }
                    catch (Exception ex)
                    {
                        DebugLogger.Warn($"ModInstaller: skipping '{a}': {ex.Message}");
                        return null;
                    }
                });

                List<DrsArchive.Entry> graphics = DrsArchive.ReadLooseFiles(mod.GraphicsFolder);
                report.GraphicsTotal = graphics.Count;

                // Each graphic goes into every archive that already holds its id - which archive
                // the game reads first differs per install, and updating them all removes the
                // question.
                var perArchive = new Dictionary<string, List<DrsArchive.Entry>>();
                var orphans = new List<DrsArchive.Entry>();

                foreach (DrsArchive.Entry graphic in graphics)
                {
                    bool placed = false;
                    foreach (string archive in archives)
                    {
                        if (tocs[archive]?.Contains(graphic.Type, graphic.Id) != true) continue;
                        if (!perArchive.TryGetValue(archive, out List<DrsArchive.Entry> list))
                        {
                            perArchive[archive] = list = new List<DrsArchive.Entry>();
                        }
                        list.Add(graphic);
                        placed = true;
                    }
                    if (!placed) orphans.Add(graphic);
                }

                foreach (var pair in perArchive)
                {
                    string archive = pair.Key;
                    string label = Label(gameFolder, archive);
                    progress?.Invoke($"Đang cập nhật {label} ({pair.Value.Count} hình ảnh)...");

                    SaveOverwritten(gameFolder, mod.Name, archive, tocs[archive], pair.Value);
                    DrsArchive.Update(archive, pair.Value);

                    record.Archives.Add(new ArchiveRecord
                    {
                        Path = archive,
                        Replaced = pair.Value.Select(e => e.Id).ToList(),
                    });
                    report.Archives.Add(new ArchiveChange
                    {
                        Path = archive,
                        Label = label,
                        Replaced = pair.Value.Count,
                    });
                }

                if (orphans.Count > 0)
                {
                    // Graphics the game has never had. There is no archive that "owns" them, so
                    // they go into the one the game reads first.
                    string fallback = Path.Combine(gameFolder, FallbackArchive);
                    if (File.Exists(fallback))
                    {
                        string label = Label(gameFolder, fallback);
                        progress?.Invoke($"Đang thêm {orphans.Count} hình ảnh mới vào {label}...");
                        DrsArchive.Update(fallback, Array.Empty<DrsArchive.Entry>(), orphans);

                        ArchiveRecord existing = record.Archives.FirstOrDefault(a => Same(a.Path, fallback));
                        if (existing == null)
                        {
                            existing = new ArchiveRecord { Path = fallback };
                            record.Archives.Add(existing);
                        }
                        existing.Added.AddRange(orphans.Select(e => e.Id));

                        ArchiveChange change = report.Archives.FirstOrDefault(a => Same(a.Path, fallback));
                        if (change == null)
                        {
                            change = new ArchiveChange { Path = fallback, Label = label };
                            report.Archives.Add(change);
                        }
                        change.Added += orphans.Count;
                    }
                    report.AddedNew.AddRange(orphans.Select(e => e.Id));
                }
            }

            record.HasText = mod.LanguageIni != null;

            List<Record> records = ReadManifest(gameFolder);
            records.RemoveAll(r => Same(r.Name, mod.Name));
            records.Add(record);
            WriteManifest(gameFolder, records);

            report.TextStrings = RebuildText(gameFolder, records, progress);

            DebugLogger.Info($"ModInstaller: installed '{mod.Name}': " +
                             string.Join(", ", report.Archives.Select(a => $"{a.Label} +{a.Replaced}/{a.Added}")) +
                             $", {report.TextStrings} strings");
            return report;
        }

        /// <summary>
        /// Keeps the bytes a mod is about to overwrite, one file per graphic, so removing it can
        /// put back exactly what was there - not what the game shipped, which may not be the same
        /// thing on an install that has been modded by hand before.
        /// </summary>
        private static void SaveOverwritten(string gameFolder, string modName, string archive,
                                            DrsArchive.Toc toc, List<DrsArchive.Entry> entries)
        {
            string folder = BackupFolder(gameFolder, modName, archive);
            Directory.CreateDirectory(folder);

            foreach (DrsArchive.Entry entry in entries)
            {
                byte[] previous = DrsArchive.ReadFile(archive, toc, entry.Type, entry.Id);
                if (previous == null) continue;
                File.WriteAllBytes(Path.Combine(folder, $"{entry.Id}.bin"), previous);
            }
        }

        private static string BackupFolder(string gameFolder, string modName, string archive) =>
            Path.Combine(gameFolder, Store, Safe(modName), Safe(Label(gameFolder, archive)));

        // ---- removing ---------------------------------------------------------------------------

        /// <summary>
        /// Takes a mod back out: every graphic it overwrote is restored from the bytes kept at
        /// install time, and anything it added is removed again.
        /// </summary>
        public static void Uninstall(string gameFolder, string name, Action<string> progress = null)
        {
            List<Record> records = ReadManifest(gameFolder);
            Record record = records.FirstOrDefault(r => Same(r.Name, name));
            if (record == null) return;

            foreach (ArchiveRecord archive in record.Archives)
            {
                if (!File.Exists(archive.Path))
                {
                    DebugLogger.Warn($"ModInstaller: '{archive.Path}' is gone; nothing to restore there.");
                    continue;
                }

                progress?.Invoke($"Đang khôi phục {Label(gameFolder, archive.Path)}...");
                string folder = BackupFolder(gameFolder, record.Name, archive.Path);

                var restore = new List<DrsArchive.Entry>();
                foreach (int id in archive.Replaced)
                {
                    string file = Path.Combine(folder, $"{id}.bin");
                    if (!File.Exists(file))
                    {
                        DebugLogger.Warn($"ModInstaller: no saved copy of {id} for '{record.Name}'.");
                        continue;
                    }
                    restore.Add(new DrsArchive.Entry
                    {
                        Type = "slp ",
                        Id = id,
                        Data = File.ReadAllBytes(file),
                    });
                }

                var remove = archive.Added.Select(id => ("slp ", id)).ToList();
                if (restore.Count > 0 || remove.Count > 0)
                {
                    DrsArchive.Update(archive.Path, restore, null, remove);
                }
            }

            try
            {
                string folder = Path.Combine(gameFolder, Store, Safe(record.Name));
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (IOException ex)
            {
                DebugLogger.Warn($"ModInstaller: could not clean up '{record.Name}' backups: {ex.Message}");
            }

            records.RemoveAll(r => Same(r.Name, name));
            WriteManifest(gameFolder, records);
            RebuildText(gameFolder, records, progress);

            DebugLogger.Info($"ModInstaller: removed '{record.Name}'.");
        }

        public static void UninstallAll(string gameFolder, Action<string> progress = null)
        {
            foreach (Mod mod in Installed(gameFolder))
            {
                Uninstall(gameFolder, mod.Name, progress);
            }
        }

        // ---- text ---------------------------------------------------------------------------------

        /// <summary>
        /// Rebuilds language_x1_p1.dll from the untouched original plus every installed mod's
        /// language.ini, in install order. Rebuilt from scratch each time rather than edited, so
        /// removing a mod removes its text too.
        /// </summary>
        private static int RebuildText(string gameFolder, List<Record> records, Action<string> progress)
        {
            string target = Path.Combine(gameFolder, TargetDll);
            if (!File.Exists(target)) return 0;

            var withText = records.Select(r => Inspect(r.Folder))
                                  .Where(m => m.LanguageIni != null)
                                  .ToList();

            string original = Path.Combine(gameFolder, BaseDll);
            if (!File.Exists(original))
            {
                if (withText.Count == 0) return 0;      // nothing to do, so nothing to preserve yet
                File.Copy(target, original);
                DebugLogger.Info($"ModInstaller: kept '{BaseDll}' as the untouched copy of '{TargetDll}'.");
            }

            var text = new Dictionary<int, string>();
            foreach (Mod mod in withText)
            {
                foreach (var kv in LanguageIni.Read(mod.LanguageIni)) text[kv.Key] = kv.Value;
            }

            if (text.Count == 0)
            {
                File.Copy(original, target, overwrite: true);
                GameLanguageStrings.Invalidate();
                return 0;
            }

            progress?.Invoke($"Đang biên dịch {text.Count} dòng văn bản...");
            string temp = target + ".tadaplay-new";
            LanguageDll.Build(original, temp, text, LanguageDll.Read(original).Language);
            File.Copy(temp, target, overwrite: true);
            File.Delete(temp);

            GameLanguageStrings.Invalidate();
            return text.Count;
        }

        // ---- helpers -------------------------------------------------------------------------------

        private static bool Same(string a, string b) =>
            string.Equals(a, b, StringComparison.CurrentCultureIgnoreCase);

        private static string Safe(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars().Concat(new[] { Path.DirectorySeparatorChar }))
            {
                name = name.Replace(c, '_');
            }
            return name;
        }
    }
}
