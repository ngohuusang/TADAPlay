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
        private const ushort VkShift = 0x10;     // VK_SHIFT
        private const ushort ScanShift = 0x2A;   // left shift
        private const ushort VkDown = 0x28;      // VK_DOWN (extended)
        private const ushort ScanDown = 0x50;
        private const ushort ScanControl = 0x1D;
        private const ushort VkLeft = 0x25;      // VK_LEFT (extended)
        private const ushort VkRight = 0x27;     // VK_RIGHT (extended)
        private const ushort ScanLeft = 0x4B;
        private const ushort ScanRight = 0x4D;

        /// <summary>
        /// Sets a replay to exactly normal speed (50) from ANY current speed, with the
        /// Ctrl+Shift+Down chord.
        ///
        /// This is an ABSOLUTE control - it lands on 50 from anywhere, including from a floored
        /// 0 - unlike Ctrl+Left / Ctrl+Right, which only step one notch at a time. It was found
        /// by trying it against a running replay, and it does not appear in any player*.hki:
        /// those files bind single keys with modifier flags and carry no Ctrl+arrow entries at
        /// all, so a three-key chord like this one is invisible to that search. Ctrl+Up, tried
        /// first on the same reasoning, genuinely does nothing.
        ///
        /// <see cref="SetReplaySpeedNormalByStepping"/> remains as the fallback: it reaches the
        /// same state using only the relative controls, at the cost of ~2.4 seconds and a
        /// visible ramp down through 0.
        /// </summary>
        public static void SetReplaySpeedNormal() =>
            CtrlShiftChord(VkDown, ScanDown, "50 (bình thường)");

        /// <summary>
        /// Reaches normal speed using only the relative Ctrl+Left / Ctrl+Right controls: five
        /// Lefts to floor at 0 (a hard floor, so extra presses are no-ops), then exactly two
        /// Rights (0 -> 25 -> 50). Deterministic from any starting speed, but slow and visible -
        /// kept as the fallback for a build where the chord above does not work.
        /// </summary>
        public static void SetReplaySpeedNormalByStepping() =>
            FloorThenRaise(RaisePresses, "50 (bình thường, theo từng nấc)");

        /// <summary>
        /// Presses Ctrl+Shift+key as a chord: both modifiers down first, the key held long enough
        /// for the game's per-frame DirectInput poll to see all three together, then everything
        /// released. The modifiers come back up on every path - a stuck Ctrl or Shift would turn
        /// every later keystroke into a chord, in the game or in whatever the viewer switches to.
        /// </summary>
        private static void CtrlShiftChord(ushort vk, ushort scan, string target)
        {
            bool ctrlDown = false, shiftDown = false;
            try
            {
                // Activate the window first, as WScript.Shell AppActivate does before SendKeys.
                BringGameToForeground();
                Thread.Sleep(120);

                // SendInput is GLOBAL - it goes to whatever has focus rather than to a window we
                // name - so the game must still be foreground at the moment of the press.
                if (!IsGameForeground()) return;

                if (!Send(MakeKey(VkControl, ScanControl, keyUp: false, extended: false))) return;
                ctrlDown = true;
                Thread.Sleep(80);
                if (!Send(MakeKey(VkShift, ScanShift, keyUp: false, extended: false))) return;
                shiftDown = true;
                Thread.Sleep(80);

                Send(MakeKey(vk, scan, keyUp: false, extended: true));
                Thread.Sleep(KeyHoldMs);
                Send(MakeKey(vk, scan, keyUp: true, extended: true));
                Thread.Sleep(60);
                DebugLogger.Info($"GameInput: replay speed set to {target} (Ctrl+Shift+Down).");
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"GameInput: setting replay speed to {target} failed: {ex.Message}");
            }
            finally
            {
                if (shiftDown) Send(MakeKey(VkShift, ScanShift, keyUp: true, extended: false));
                if (ctrlDown) Send(MakeKey(VkControl, ScanControl, keyUp: true, extended: false));
            }
        }

        /// <summary>
        /// Stops a replay dead by flooring its speed to 0 - the FALLBACK for pausing, used only
        /// when the real pause key (<see cref="PressPauseKey"/>) is proved not to have worked.
        ///
        /// It is a fallback rather than the first choice because it visibly ramps the playback
        /// speed down through 75/50/25 on the way to 0, which a viewer sees and dislikes. Its
        /// merit is that it is the hardcoded Ctrl+Left viewer control, which is known to land,
        /// and that it is idempotent: extra presses at 0 are no-ops, so repeating it is safe in
        /// a way that re-pressing a toggle is not.
        /// </summary>
        public static void FloorReplaySpeed() => FloorThenRaise(0, "0 (dừng hẳn)");

        /// <summary>Virtual-key for the game's default "Pause Game" binding (F3).</summary>
        public const ushort VkPauseDefault = 0x72;

        /// <summary>
        /// Presses the game's "Pause Game" key - hotkey command id 19323, F3 by default.
        ///
        /// This is a real pause: the game shows its own "Game Paused" banner and the playhead
        /// stops, with none of the speed ramping <see cref="FloorReplaySpeed"/> causes.
        ///
        /// It is a TOGGLE, and nothing here can read whether the game is currently paused, so a
        /// press is never repeated blindly. Callers press once, then prove the outcome by
        /// watching the playhead: frozen means it worked, still moving means it did not. Pressing
        /// again "just in case" would resume a replay that had in fact paused.
        ///
        /// The scan code is resolved through MapVirtualKey rather than hardcoded, so a rebound
        /// pause key works as well as the default.
        /// </summary>
        public static void PressPauseKey(ushort vk = VkPauseDefault)
        {
            try
            {
                BringGameToForeground();
                Thread.Sleep(120);
                if (!IsGameForeground()) return;

                ushort scan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
                // Extended keys are the navigation cluster and the numpad enter/divide; a function
                // key never is, but a rebound pause could be, so this is derived rather than assumed.
                bool extended = IsExtendedKey(vk);

                Send(MakeKey(vk, scan, keyUp: false, extended: extended));
                Thread.Sleep(KeyHoldMs);
                Send(MakeKey(vk, scan, keyUp: true, extended: extended));
                DebugLogger.Info($"GameInput: pressed the pause key (VK 0x{vk:X2}, scan 0x{scan:X2}).");
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"GameInput: pause key press failed: {ex.Message}");
            }
        }

        private static bool IsExtendedKey(ushort vk)
        {
            switch (vk)
            {
                case 0x21: case 0x22: case 0x23: case 0x24:   // PgUp PgDn End Home
                case 0x25: case 0x26: case 0x27: case 0x28:   // arrows
                case 0x2D: case 0x2E:                         // Insert Delete
                case 0x6F:                                    // numpad /
                case 0x90:                                    // NumLock
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>How long a key is held. Matches TapArrow: long enough for the game's
        /// per-frame DirectInput poll to see it, which is what makes a press register.</summary>
        private const int KeyHoldMs = 200;

        private const uint MAPVK_VK_TO_VSC = 0;

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        /// <summary>
        /// Floors the playback speed to 0 with Ctrl+Left, then steps it back up
        /// <paramref name="raisePresses"/> times with Ctrl+Right. Two presses land on normal
        /// speed; zero presses leave the replay stopped.
        /// </summary>
        private static void FloorThenRaise(int raisePresses, string target)
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
                for (int i = 0; i < raisePresses; i++)
                {
                    if (!IsGameForeground()) return;
                    TapArrow(VkRight, ScanRight); // 0 -> 25 -> 50
                }
                Thread.Sleep(120);
                DebugLogger.Info($"GameInput: replay speed set to {target}.");
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"GameInput: setting replay speed to {target} failed: {ex.Message}");
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
