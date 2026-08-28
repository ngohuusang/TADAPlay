using System;
using System.Runtime.InteropServices;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Slows a recorded-game replay down one step at a time, by injecting the viewer's own
    /// Ctrl+Left "slower playback" control into the running game.
    ///
    /// Ctrl+Left / Ctrl+Right are hardcoded controls of the recorded-game viewer - NOT the .hki
    /// hotkeys. The .hki "Slow Down Game" binding (numpad minus, string id 19005) only changes a
    /// LIVE game and does nothing to a replay, which is why an earlier attempt at it had no
    /// effect. The playback speeds step 0 - 25 - 50 - 75 - 99 - 100, with 50 being normal.
    ///
    /// Getting a press to actually land needs three things, each established by measurement:
    /// the process must be ELEVATED (UIPI otherwise drops the input - the same Ctrl+Left works
    /// from an elevated PowerShell and not from a medium-integrity one), the game window must be
    /// activated first (as WScript.Shell AppActivate does before SendKeys), and each key must be
    /// HELD long enough for the game's per-frame DirectInput poll to see it.
    ///
    /// Input goes to whichever window has the foreground, so callers must only send while the
    /// game is foreground - never while the player has alt-tabbed to TadaPlay.
    /// </summary>
    public static class GameInput
    {
        // REPLAY playback speed is Ctrl+Left (slower) / Ctrl+Right (faster) - a hardcoded
        // recorded-game viewer control, NOT the .hki "Slow Down Game" hotkey (numpad minus,
        // which only changes a LIVE game and does nothing to a replay). Arrow keys are extended.
        private const ushort VkControl = 0x11;   // VK_CONTROL
        private const ushort ScanControl = 0x1D;
        private const ushort VkLeft = 0x25;      // VK_LEFT (extended)
        private const ushort VkRight = 0x27;     // VK_RIGHT (extended)
        private const ushort ScanLeft = 0x4B;
        private const ushort ScanRight = 0x4D;

        /// <summary>
        /// Sets a replay to exactly normal speed (50) from ANY current speed.
        ///
        /// The playback speeds step 0 - 25 - 50 - 75 - 99 - 100, and nothing here can read which
        /// one is active. What makes this deterministic is that 0 is a hard floor: five Ctrl+Left
        /// presses reach it from the fastest setting, and further presses at 0 do nothing. From
        /// that known state exactly two Ctrl+Right presses land on 50 (0 -> 25 -> 50), whatever
        /// the speed was to begin with.
        ///
        /// Stepping down one notch at a time and re-measuring instead is tempting but cannot stop
        /// cleanly: the measurement lags the change, so the loop kept pressing after the replay
        /// had already slowed and walked 75 -> 50 -> 25 -> 0, leaving it paused.
        ///
        /// Ctrl is held down across the whole sequence, and every press is held long enough for
        /// the game's per-frame DirectInput poll to see it.
        ///
        /// Abandoning the sequence part-way (the viewer alt-tabbed) leaves the replay at an
        /// unknown speed - possibly paused, if the Lefts landed but the Rights did not. That is
        /// acceptable precisely because this call is deterministic from ANY starting speed,
        /// including 0: the governor's next attempt puts it back on 50. It also errs in the safe
        /// direction, since a paused replay can be resumed while one that has run off the end
        /// cannot.
        /// </summary>
        public static void SetReplaySpeedNormal()
        {
            bool ctrlDown = false;
            try
            {
                // Activate the window first, as WScript.Shell AppActivate does before SendKeys.
                BringGameToForeground();
                Thread.Sleep(120);

                // Re-checked before every press, not once at the start. SendInput is GLOBAL - it
                // goes to whatever has focus, not to a window we name - and this sequence takes
                // about 2.4 seconds end to end (seven taps at 260ms plus the settling sleeps).
                // Alt-tabbing inside that window would otherwise deliver the remaining Ctrl+arrow
                // presses into whatever the viewer switched to.
                if (!IsGameForeground()) return;

                if (!Send(MakeKey(VkControl, ScanControl, keyUp: false, extended: false))) return;
                ctrlDown = true;
                Thread.Sleep(200);

                for (int i = 0; i < FloorPresses; i++)
                {
                    if (!IsGameForeground()) return;
                    TapArrow(VkLeft, ScanLeft);   // -> 0
                }
                Thread.Sleep(150);
                for (int i = 0; i < RaisePresses; i++)
                {
                    if (!IsGameForeground()) return;
                    TapArrow(VkRight, ScanRight); // 0 -> 25 -> 50
                }
                Thread.Sleep(120);
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"GameInput: SetReplaySpeedNormal failed: {ex.Message}");
            }
            finally
            {
                // Ctrl comes back up on every path, including an abort. A stuck modifier would
                // turn every later keystroke - in the game or in whatever the viewer alt-tabbed
                // to - into a Ctrl chord, which is far worse than the speed being wrong.
                if (ctrlDown)
                {
                    Send(MakeKey(VkControl, ScanControl, keyUp: true, extended: false));
                }
            }
        }

        /// <summary>Ctrl+Left presses to reach 0 from the fastest setting (100/99/75/50/25 -> 0).</summary>
        private const int FloorPresses = 5;

        /// <summary>Ctrl+Right presses from 0 up to normal speed (0 -> 25 -> 50).</summary>
        private const int RaisePresses = 2;

        /// <summary>
        /// Activates the game window the way WScript.Shell.AppActivate does before SendKeys -
        /// present in the configuration that worked. AttachThreadInput is what lets a background
        /// process set the foreground without the focus-stealing guard dropping the call.
        /// </summary>
        private static void BringGameToForeground()
        {
            try
            {
                IntPtr hwnd = GetGameWindow();
                if (hwnd == IntPtr.Zero) { DebugLogger.Warn("GameInput: game window not found."); return; }
                uint myThread = GetCurrentThreadId();
                uint gameThread = GetWindowThreadProcessId(hwnd, out _);
                if (gameThread == 0) return;
                AttachThreadInput(myThread, gameThread, true);
                try { SetForegroundWindow(hwnd); }
                finally { AttachThreadInput(myThread, gameThread, false); }
            }
            catch (Exception ex) { DebugLogger.Warn($"GameInput: activate failed: {ex.Message}"); }
        }

        /// <summary>The game main visible top-level window, or IntPtr.Zero.</summary>
        private static IntPtr GetGameWindow()
        {
            IntPtr found = IntPtr.Zero;
            try
            {
                var game = LiveRecordReader.FindGameProcess();
                if (game == null) return IntPtr.Zero;
                int pid = game.Id; game.Dispose();
                EnumWindows((h, l) =>
                {
                    if (!IsWindowVisible(h)) return true;
                    GetWindowThreadProcessId(h, out uint wpid);
                    if (wpid == (uint)pid) { found = h; return false; }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex) { DebugLogger.Warn($"GameInput: cannot find game window: {ex.Message}"); }
            return found;
        }

        /// <summary>
        /// Whether this process runs elevated (High integrity). Injecting keys into the game
        /// requires it - UIPI blocks a Medium-integrity process from sending input to the game,
        /// which is why the same Ctrl+Left works from an elevated PowerShell but not from a
        /// TadaPlay that was started without administrator rights.
        /// </summary>
        public static bool IsElevated()
        {
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>The window title/process that currently has the foreground, or null.</summary>
        public static bool IsGameForeground()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                GetWindowThreadProcessId(fg, out uint pid);
                if (pid == 0) return false;
                using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                string name = proc.ProcessName.ToLowerInvariant();
                return name.Contains("age2") || name.Contains("empires") || name.Contains("wk");
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"GameInput: cannot check foreground window: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Injects one key event and reports whether the OS ACCEPTED it. SendInput returning 0 is
        /// the signature of the event being blocked outright (UIPI against a higher-integrity
        /// window, or a low-level hook eating it) as opposed to the game receiving it and doing
        /// nothing with it - two failure modes that look identical from the outside.
        /// </summary>
        private static bool Send(INPUT input)
        {
            int size = Marshal.SizeOf<INPUT>();
            uint sent = SendInput(1, new[] { input }, size);
            if (sent == 0)
            {
                int err = Marshal.GetLastWin32Error();
                DebugLogger.Warn($"GameInput: SendInput REJECTED (0 events, Win32 error {err}, " +
                                 $"cbSize {size}) - error 87 means the INPUT struct size is wrong, " +
                                 "anything else means the OS refused the injection.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Taps an arrow key once (down, hold, up). Ctrl is expected to already be held.
        ///
        /// The hold is deliberately long (~150ms): AoE2 polls the keyboard through DirectInput
        /// roughly once a frame, so a brief down+up falls between polls and is missed - which is
        /// why even an elevated SendKeys Ctrl+Left only registered a couple of times in twenty.
        /// Holding the key across several polls is what makes a press land.
        /// </summary>
        private static void TapArrow(ushort vk, ushort scan)
        {
            Send(MakeKey(vk, scan, keyUp: false, extended: true));
            Thread.Sleep(200);
            Send(MakeKey(vk, scan, keyUp: true, extended: true));
            Thread.Sleep(60);
        }

        /// <summary>
        /// A key event carrying BOTH the virtual key and the scan code, VK-based (no
        /// KEYEVENTF_SCANCODE) to mirror WScript.Shell SendKeys - the form that reached this
        /// game where a pure scan-code injection did not. Arrows set the extended flag.
        /// </summary>
        private static INPUT MakeKey(ushort vk, ushort scan, bool keyUp, bool extended)
        {
            uint flags = 0u;
            if (keyUp) flags |= KEYEVENTF_KEYUP;
            if (extended) flags |= KEYEVENTF_EXTENDEDKEY;
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero }
                }
            };
        }

        // ---- interop ----
        private const int INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        // INPUT must be laid out exactly as Win32 expects, and its size is set by the LARGEST
        // union member - MOUSEINPUT - not by the member being used. Declaring the union with only
        // KEYBDINPUT in it made Marshal.SizeOf<INPUT>() 32 bytes on x64 where Windows requires 40,
        // so every single call failed with ERROR_INVALID_PARAMETER (87) and injected nothing. That
        // was the real reason none of the injected keys ever reached the game: the OS rejected the
        // call outright, which is indistinguishable from "the game ignored it" without checking
        // SendInput's return value.
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public int type; public InputUnion U; }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

    }
}
