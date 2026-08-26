using System;
using System.Collections.Generic;
using System.IO;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// A single hotkey binding inside a player*.hki file.
    ///
    /// Layout on disk (12 bytes, little-endian): int32 KeyCode, int32 StringId,
    /// then four bytes Ctrl, Alt, Shift, Status.
    ///
    /// <see cref="StringId"/> is the language-DLL string number naming the command
    /// (resolved for display via <see cref="GameLanguageStrings"/>). A value of -1 (or 0)
    /// marks a structural placeholder slot the game keeps but never shows - those are
    /// preserved verbatim on save and simply not surfaced in the editor.
    /// <see cref="KeyCode"/> is a Win32 virtual-key code; 0 means "unbound".
    /// </summary>
    public class HotkeyBinding
    {
        public int KeyCode;
        public int StringId;
        public bool Ctrl;
        public bool Alt;
        public bool Shift;
        public byte Status; // 4th flag byte - meaning unknown, preserved verbatim.

        /// <summary>True when this slot names a real, user-visible command.</summary>
        public bool IsCommand => StringId > 0;
    }

    /// <summary>One category of bindings (matches an in-game hotkey tab).</summary>
    public class HotkeyGroup
    {
        public readonly List<HotkeyBinding> Bindings = new List<HotkeyBinding>();
    }

    /// <summary>
    /// In-memory model of an Age of Empires II (AoC / The Conquerors) player*.hki hotkey file,
    /// with lossless load/save. The file is a raw DEFLATE stream (RFC 1951, no zlib/gzip header -
    /// same container as the player*.nfx profiles handled by <see cref="GameProfileNameWriter"/>).
    /// Decompressed body: float Version, int32 GroupCount, then per group { int32 Count,
    /// Count x 12-byte binding }.
    /// </summary>
    public class HotkeyFile
    {
        public float Version = 1.0f;
        public readonly List<HotkeyGroup> Groups = new List<HotkeyGroup>();

        public static HotkeyFile Load(string path)
        {
            byte[] raw = GameProfileNameWriter.Inflate(File.ReadAllBytes(path));
            using var ms = new MemoryStream(raw, writable: false);
            using var reader = new BinaryReader(ms);

            var file = new HotkeyFile { Version = reader.ReadSingle() };
            int groupCount = reader.ReadInt32();
            for (int g = 0; g < groupCount; g++)
            {
                int count = reader.ReadInt32();
                var group = new HotkeyGroup();
                for (int i = 0; i < count; i++)
                {
                    group.Bindings.Add(new HotkeyBinding
                    {
                        KeyCode = reader.ReadInt32(),
                        StringId = reader.ReadInt32(),
                        Ctrl = reader.ReadByte() != 0,
                        Alt = reader.ReadByte() != 0,
                        Shift = reader.ReadByte() != 0,
                        Status = reader.ReadByte(),
                    });
                }
                file.Groups.Add(group);
            }
            return file;
        }

        /// <summary>
        /// Slots the HD-era data mods fill in for commands The Conquerors never had.
        ///
        /// The build group has always been 30 slots wide, but AoC only ever named 25 of them;
        /// the rest sit in the file as placeholders (StringId -1, everything else zeroed). A mod
        /// that adds a building does not grow the group - it names one of those spare slots. So a
        /// player whose .hki predates the mod has a Palisade Gate slot sitting right there,
        /// nameless, and no way to reach it: the editor only shows slots that name a command,
        /// which is what keeps the structural padding out of the list.
        ///
        /// Read off the mod's own files rather than guessed - a player*.hki written by the game
        /// with WololoKingdoms active has 19212 at slot 28 and 19075 at slot 29 of group 3.
        /// </summary>
        private static readonly (int Group, int Slot, int StringId)[] ModCommandSlots =
        {
            (3, 28, 19212),   // Palisade Gate
            (3, 29, 19075),   // Feitoria
        };

        /// <summary>
        /// Names the placeholder slots this game's data actually uses, so they can be bound.
        /// Returns how many were adopted.
        ///
        /// <paramref name="hasName"/> decides whether a command exists in THIS installation -
        /// pass it a lookup over the game's own language data. That is the whole guard against
        /// inventing commands: on plain AoC nothing names 19212, so the slot stays a placeholder
        /// and stays hidden, which is correct because that game has no Palisade Gate to build.
        ///
        /// Never touches a slot that already names something. Writing over a real command would
        /// silently rebind whatever was there, and the layout differing from what is assumed
        /// here is exactly the case where that would happen.
        /// </summary>
        public int AdoptModCommands(Func<int, bool> hasName)
        {
            if (hasName == null) return 0;

            int adopted = 0;
            foreach ((int group, int slot, int stringId) in ModCommandSlots)
            {
                if (group < 0 || group >= Groups.Count) continue;
                var bindings = Groups[group].Bindings;
                if (slot < 0 || slot >= bindings.Count) continue;

                HotkeyBinding binding = bindings[slot];
                if (binding.IsCommand) continue;          // a real command already lives here
                if (!hasName(stringId)) continue;         // this installation does not have it

                // Only the name is taken on. The key stays as the placeholder left it - zero,
                // meaning unbound - so the row turns up asking to be assigned rather than
                // claiming a binding the player never chose.
                binding.StringId = stringId;
                adopted++;
                DebugLogger.Info($"HotkeyFile: named placeholder slot {group}/{slot} as string {stringId}.");
            }
            return adopted;
        }

        public void Save(string path)
        {
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(Version);
                writer.Write(Groups.Count);
                foreach (var group in Groups)
                {
                    writer.Write(group.Bindings.Count);
                    foreach (var b in group.Bindings)
                    {
                        writer.Write(b.KeyCode);
                        writer.Write(b.StringId);
                        writer.Write((byte)(b.Ctrl ? 1 : 0));
                        writer.Write((byte)(b.Alt ? 1 : 0));
                        writer.Write((byte)(b.Shift ? 1 : 0));
                        writer.Write(b.Status);
                    }
                }
            }

            byte[] compressed = GameProfileNameWriter.Deflate(ms.ToArray());

            // Write to a temp file next to the target, then swap in - avoids leaving a half-written
            // .hki behind if something fails mid-write (the game would refuse to load a truncated one).
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, compressed);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            DebugLogger.Info($"HotkeyFile: saved '{path}' ({compressed.Length} bytes).");
        }
    }
}
