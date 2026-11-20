using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// The two speed builds of the game: UserPatch's own multiplayer speed, and the slower HD
    /// Edition speed. See AOE2-SPEED-VARIANTS-SPEC.md in the game folder.
    ///
    /// UserPatch multiplies the multiplayer speed by a float constant, 1.1875, read by three
    /// instructions in the exe; the HD build is the same files with that constant set to 1.0.
    /// One byte differs (0x98 vs 0x80). Nothing is downloaded for it: the HD exes are made by
    /// patching copies of the downloaded game's own exes, and back again.
    ///
    /// The two UserPatch DLLs hold copies of the same constant and can write it back into the
    /// game at runtime, so they are shared by both builds and set to whichever one is about to
    /// run. That is why choosing a build is a switch done at launch, refused while a game is
    /// open, and not just a choice of exe.
    ///
    /// Everything is checked before it is written: an exe must be exactly the expected size
    /// and carry the expected bytes around the constant, or it is a different build and is
    /// left alone. Files are written beside the target and renamed into place, and a file
    /// changed in place is backed up as "&lt;name&gt;.orig" the first time.
    ///
    /// Players in the same match MUST run the same build: each PC applies its own multiplier,
    /// so a mixed game goes out of sync within seconds.
    /// </summary>
    public static class GameSpeedVariant
    {
        public const string Original = "original";
        public const string Hd = "hd";

        private const long ExeSize = 3_145_728;
        private const int ExeCheckOffset = 0x2D89E8;    // 16 bytes ending just past the constant
        private const int ExeMultiplierByte = 0x2D89F2; // third byte of the float at 0x2D89F0
        private const int ExeLabelOffset = 0x280FD4;    // "2.0" / "2.2" on WK exes' main menu

        private static readonly byte[] ExeCheck =
            { 0xFB, 0xE9, 0x26, 0x1C, 0xDE, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3F, 0x00, 0x00, 0x00, 0x00 };
        private const int ExeCheckMultiplierIndex = 10;

        private const long DllSize = 770_048;
        private static readonly int[] DllConstantOffsets = { 0x1BB88, 0x6DA68 };
        private static readonly string[] DllPaths =
        {
            @"Voobly Mods\AOC\Patches\v1.6 RC\userpatch\userpatch.dll",
            @"Voobly Mods\AOC\Patches\v1.5 RC\userpatch.dll",
        };

        private const byte OriginalByte = 0x98; // 1.1875 = 00 00 98 3F
        private const byte HdByte = 0x80;       // 1.0    = 00 00 80 3F

        /// <summary>Raised when the player picks the other build, so the lobby can be told.</summary>
        public static event Action<string> Changed;

        internal static void RaiseChanged(string variant)
        {
            try { Changed?.Invoke(Normalize(variant)); }
            catch (Exception ex) { DebugLogger.Warn($"GameSpeedVariant: change handler failed: {ex.Message}"); }
        }

        /// <summary>What the game's main menu shows for each build: "2.0" or "2.2".</summary>
        public static string MenuLabel(string variant) => Normalize(variant) == Hd ? "2.2" : "2.0";

        public static string Normalize(string variant) =>
            string.Equals(variant, Hd, StringComparison.OrdinalIgnoreCase) ? Hd : Original;

        /// <summary>Short name for the UI and the log.</summary>
        public static string Label(string variant) =>
            Normalize(variant) == Hd ? "HD (2.2) - chậm hơn" : "UserPatch (2.0)";

        /// <summary>The exe each build runs under, per display mode (spec §2.3).</summary>
        public static string ExeName(string variant, bool center) =>
            Normalize(variant) == Hd
                ? (center ? "age2-WK-2.2-center.exe" : "age2-WK-2.2.exe")
                : (center ? "age2-WK-center.exe" : "age2-WK.exe");

        /// <summary>
        /// Makes the chosen build ready to run and returns its exe, or a reason it cannot be.
        /// Creates the exe from the other build's file when it is missing, puts one that has
        /// been switched in place back, and sets both DLLs to match.
        /// </summary>
        public static (string ExePath, string Problem) Prepare(string gameFolder, string variant, bool center)
        {
            variant = Normalize(variant);
            string age2X1 = Path.Combine(gameFolder, "age2_x1");
            string target = Path.Combine(age2X1, ExeName(variant, center));
            string other = Path.Combine(age2X1, ExeName(variant == Hd ? Original : Hd, center));

            try
            {
                // ---- work out what has to change, before changing anything
                var writes = new List<(string Path, byte[] Data, bool InPlace)>();

                string targetState = ExeVariant(target, out byte[] targetBytes, out string targetNote);
                if (targetState == variant)
                {
                    // already right
                }
                else if (targetState != null)
                {
                    writes.Add((target, ToVariant(targetBytes, variant), true));
                }
                else if (File.Exists(target))
                {
                    return (null, $"Không nhận ra file {Path.GetFileName(target)} ({targetNote}) - " +
                                  "không sửa để tránh làm hỏng game.");
                }
                else
                {
                    string otherState = ExeVariant(other, out byte[] otherBytes, out string otherNote);
                    if (otherState == null)
                    {
                        return (null, File.Exists(other)
                            ? $"Không nhận ra file {Path.GetFileName(other)} ({otherNote}) - không tạo được " +
                              $"{Path.GetFileName(target)}."
                            : $"Không tìm thấy {Path.GetFileName(target)} hay {Path.GetFileName(other)} trong " +
                              "thư mục age2_x1 - hãy tải lại game.");
                    }
                    writes.Add((target, ToVariant(otherBytes, variant), false));
                }

                foreach (string relative in DllPaths)
                {
                    string dll = Path.Combine(gameFolder, relative);
                    if (!File.Exists(dll)) continue;
                    string state = DllVariant(dll, out byte[] dllBytes, out string note);
                    if (state == null)
                    {
                        DebugLogger.Warn($"GameSpeedVariant: leaving '{relative}' alone - {note}.");
                        continue;
                    }
                    if (state != variant) writes.Add((dll, DllToVariant(dllBytes, variant), true));
                }

                if (writes.Count == 0) return (target, null);

                // ---- the DLLs are shared with any game already open, so never under one
                if (GameIsRunning())
                {
                    return (null, $"Đang có game AoE2 mở - hãy tắt game trước khi đổi sang tốc độ " +
                                  $"{Label(variant)}.");
                }

                foreach (var (path, data, inPlace) in writes)
                {
                    if (inPlace) BackUpOnce(path);
                    WriteReplacing(path, data);
                    DebugLogger.Info($"GameSpeedVariant: wrote '{path}' as {variant}" +
                                     (inPlace ? " (switched in place)." : " (made from the other build)."));
                }
                return (target, null);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"GameSpeedVariant: preparing {variant} failed: {ex.Message}");
                return (null, $"Không chuẩn bị được bản tốc độ {Label(variant)}: {ex.Message}");
            }
        }

        /// <summary>Which build an exe is, or null when it is not one of the two (with why).</summary>
        public static string ExeVariant(string path, out byte[] bytes, out string note)
        {
            bytes = null;
            note = null;
            if (!File.Exists(path))
            {
                note = "không có file";
                return null;
            }
            if (new FileInfo(path).Length != ExeSize)
            {
                note = "kích thước khác";
                return null;
            }
            bytes = File.ReadAllBytes(path);
            for (int i = 0; i < ExeCheck.Length; i++)
            {
                if (i == ExeCheckMultiplierIndex) continue;
                if (bytes[ExeCheckOffset + i] != ExeCheck[i])
                {
                    note = "bản build khác";
                    return null;
                }
            }
            byte m = bytes[ExeMultiplierByte];
            if (m == OriginalByte) return Original;
            if (m == HdByte) return Hd;
            note = $"hệ số tốc độ lạ 0x{m:X2}";
            return null;
        }

        private static string DllVariant(string path, out byte[] bytes, out string note)
        {
            bytes = null;
            note = null;
            if (new FileInfo(path).Length != DllSize)
            {
                note = "unexpected size";
                return null;
            }
            bytes = File.ReadAllBytes(path);
            string found = null;
            foreach (int offset in DllConstantOffsets)
            {
                byte m = bytes[offset + 2];
                bool shape = bytes[offset] == 0 && bytes[offset + 1] == 0 && bytes[offset + 3] == 0x3F;
                string here = !shape ? null : m == OriginalByte ? Original : m == HdByte ? Hd : null;
                if (here == null)
                {
                    note = $"no speed constant at 0x{offset:X}";
                    return null;
                }
                // Two copies that disagree: report the one that needs fixing as "not this build"
                // so a write puts both right.
                found = found == null || found == here ? here : "mixed";
            }
            return found;
        }

        private static byte[] ToVariant(byte[] exe, string variant)
        {
            var data = (byte[])exe.Clone();
            data[ExeMultiplierByte] = variant == Hd ? HdByte : OriginalByte;

            // The menu label, only where it is one of the two WK labels; other exes say "1.X".
            if (data[ExeLabelOffset] == (byte)'2' && data[ExeLabelOffset + 1] == (byte)'.'
                && (data[ExeLabelOffset + 2] == (byte)'0' || data[ExeLabelOffset + 2] == (byte)'2'))
            {
                data[ExeLabelOffset + 2] = variant == Hd ? (byte)'2' : (byte)'0';
            }
            return data;
        }

        private static byte[] DllToVariant(byte[] dll, string variant)
        {
            var data = (byte[])dll.Clone();
            foreach (int offset in DllConstantOffsets)
            {
                data[offset + 2] = variant == Hd ? HdByte : OriginalByte;
            }
            return data;
        }

        private static bool GameIsRunning()
        {
            try
            {
                return Process.GetProcesses().Any(p =>
                {
                    try { return p.ProcessName.StartsWith("age2", StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                    finally { p.Dispose(); }
                });
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"GameSpeedVariant: cannot list processes: {ex.Message}");
                return false;
            }
        }

        private static void BackUpOnce(string path)
        {
            string backup = path + ".orig";
            if (File.Exists(backup)) return;
            File.Copy(path, backup);
            DebugLogger.Info($"GameSpeedVariant: backed up '{path}' to '{backup}'.");
        }

        private static void WriteReplacing(string path, byte[] data)
        {
            string temp = path + ".tmp";
            try
            {
                File.WriteAllBytes(temp, data);
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
                throw;
            }
        }
    }
}
