using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Reads a file the game holds open, by taking a volume shadow copy and reading the file
    /// out of the snapshot.
    ///
    /// This exists because the obvious approach is unsafe. The game keeps its record open
    /// without FILE_SHARE_READ, so the only direct way in is a duplicate of the game's own
    /// handle - and that handle is synchronous, so reading through it moves a file pointer the
    /// game is still using. A write issued during the read then lands at a stale offset and
    /// duplicates ~20 bytes of the stream. That is not theoretical: it damaged a real 30-minute
    /// match in three places, and the damage was silent because mgz swallows the resulting
    /// invalid operation without erroring.
    ///
    /// A shadow copy touches nothing the game owns. The volume is snapshotted at block level
    /// and the file is read from the snapshot device, so the game's handle, file pointer and
    /// writes are all untouched. Verified against a stand-in holding a file exactly the way the
    /// game does: the read produced a valid record and the original was left byte-identical.
    ///
    /// The cost is that a snapshot takes a couple of seconds and briefly quiesces volume
    /// writes, so this is something to do every few minutes, not every few seconds.
    /// </summary>
    public static class VolumeSnapshotReader
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_ALL = 0x07;      // read | write | delete
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;

        /// <summary>How long to allow for creating or deleting a snapshot.</summary>
        private static readonly TimeSpan PowerShellTimeout = TimeSpan.FromSeconds(120);

        /// <summary>
        /// Reads <paramref name="path"/> as of now, even if it is locked. Returns null with a
        /// reason on failure. The snapshot is always deleted again, including on failure.
        /// </summary>
        public static byte[] TryReadLockedFile(string path, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "Chưa có đường dẫn file.";
                return null;
            }

            string root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || !root.Contains(':'))
            {
                error = $"Không xác định được ổ đĩa của '{path}'.";
                return null;
            }

            if (!TryCreateSnapshot(root, out string shadowId, out string device, out error))
            {
                return null;
            }

            try
            {
                // "C:\games\rec.mgz" -> "<device>\games\rec.mgz"
                string relative = Path.GetFullPath(path).Substring(root.Length - 1);
                return ReadFromDevice(device + relative, out error);
            }
            finally
            {
                DeleteSnapshot(shadowId);
            }
        }

        private static bool TryCreateSnapshot(string root, out string shadowId, out string device,
                                              out string error)
        {
            shadowId = null;
            device = null;
            error = null;

            // Shelled out rather than taking a System.Management dependency for two calls.
            string script =
                "$c=[WMICLASS]\"root\\cimv2:Win32_ShadowCopy\"; " +
                $"$r=$c.Create(\"{root.Replace("\\", "\\\\")}\",\"ClientAccessible\"); " +
                "if ($r.ReturnValue -ne 0) { \"ERR $($r.ReturnValue)\" } else { " +
                "$s=Get-CimInstance Win32_ShadowCopy | Where-Object { $_.ID -eq $r.ShadowID }; " +
                "\"$($r.ShadowID)`n$($s.DeviceObject)\" }";

            string output = RunPowerShell(script);
            string[] lines = (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2 || lines[0].StartsWith("ERR", StringComparison.Ordinal))
            {
                // Common causes: VSS service disabled, or no space for the diff area.
                error = $"Không tạo được bản chụp ổ đĩa (VSS){(lines.Length > 0 ? ": " + lines[0].Trim() : "")}.";
                DebugLogger.Warn($"VolumeSnapshotReader: snapshot of {root} failed: {output}");
                return false;
            }

            shadowId = lines[0].Trim();
            device = lines[1].Trim();
            return true;
        }

        private static void DeleteSnapshot(string shadowId)
        {
            if (string.IsNullOrWhiteSpace(shadowId)) return;
            try
            {
                RunPowerShell("Get-CimInstance Win32_ShadowCopy | " +
                              $"Where-Object {{ $_.ID -eq \"{shadowId}\" }} | Remove-CimInstance");
            }
            catch (Exception ex)
            {
                // Leaving one behind wastes disk until Windows recycles it, but is not fatal.
                DebugLogger.Warn($"VolumeSnapshotReader: cannot delete snapshot {shadowId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads a \\?\GLOBALROOT\... path. The .NET file APIs cannot open those, so this goes
        /// through CreateFile directly.
        /// </summary>
        private static byte[] ReadFromDevice(string devicePath, out string error)
        {
            error = null;
            IntPtr handle = CreateFileW(devicePath, GENERIC_READ, FILE_SHARE_ALL, IntPtr.Zero,
                                        OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero);
            if (handle == new IntPtr(-1))
            {
                error = $"Không mở được file trong bản chụp (lỗi {Marshal.GetLastWin32Error()}).";
                return null;
            }

            try
            {
                var chunks = new List<byte[]>();
                var buffer = new byte[1 << 20];
                long total = 0;
                while (true)
                {
                    if (!ReadFile(handle, buffer, (uint)buffer.Length, out uint read, IntPtr.Zero))
                    {
                        error = $"Lỗi đọc file trong bản chụp ({Marshal.GetLastWin32Error()}).";
                        return null;
                    }
                    if (read == 0) break;

                    var chunk = new byte[read];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, (int)read);
                    chunks.Add(chunk);
                    total += read;
                    if (total > int.MaxValue)
                    {
                        error = "File record quá lớn.";
                        return null;
                    }
                }

                var result = new byte[total];
                int offset = 0;
                foreach (byte[] chunk in chunks)
                {
                    Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
                    offset += chunk.Length;
                }
                return result;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static string RunPowerShell(string script)
        {
            var startInfo = new ProcessStartInfo("powershell")
            {
                Arguments = "-NoProfile -NonInteractive -Command -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            using Process process = Process.Start(startInfo);
            if (process == null) return null;

            // Passed on stdin so quoting in the script cannot be mangled by the command line.
            process.StandardInput.Write(script);
            process.StandardInput.Close();

            string output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit((int)PowerShellTimeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                return null;
            }
            return output;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string fileName, uint access, uint shareMode,
                                                 IntPtr security, uint creationDisposition,
                                                 uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadFile(IntPtr handle, byte[] buffer, uint toRead,
                                            out uint read, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
