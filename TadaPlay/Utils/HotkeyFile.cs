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
