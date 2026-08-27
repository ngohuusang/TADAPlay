using System;
using System.Runtime.InteropServices;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Sends the AoE2 "Slow Down Game" hotkey (numpad minus) to the running game.
    ///
    /// Verified from the game's own player*.hki: numpad minus is bound to "Slow Down Game"
    /// (string id 19005) and numpad plus to "Speed Up Game" (19004). During replay playback the
    /// speed reads 50 at normal and up to 100 when fast-forwarded, so repeatedly pressing Slow
    /// Down floors it back to normal - which is how <see cref="PlayheadGovernor"/> keeps a
    /// spectator from running the replay off its end.
    ///
    /// The game reads the keyboard through DirectInput, which sees hardware SCAN CODES rather
    /// than window messages, so the key is injected with SendInput + KEYEVENTF_SCANCODE (a
    /// PostMessage would be silently ignored). Input goes to whatever window has the foreground,
    /// so the caller must only send this while the GAME is foreground - never while the player
    /// has alt-tabbed to TadaPlay, or the keypress lands in the wrong place.
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
        /// Holds Ctrl and taps Left several times - each Ctrl+Left is one step slower, so this
        /// floors a fast-forwarded replay back toward normal (50). Ctrl is held down for the
        /// whole run (like a person holding it) rather than pressed and released per tap.
        /// </summary>
        public static void SlowDownReplay(int taps = 8) => CtrlTaps(ScanLeft, taps);

        /// <summary>Ctrl+Right a few times - faster. Kept for completeness / tuning.</summary>
        public static void SpeedUpReplay(int taps = 1) => CtrlTaps(ScanRight, taps);

        /// <summary>
        /// Sets a replay to exactly normal speed (50), from ANY current speed.
        ///
        /// The playback steps are 0 · 25 · 50 · 75 · 99 · 100, so simply pressing Ctrl+Left
        /// repeatedly overshoots normal down to 25 and 0 (paused). Instead this floors to 0
        /// (six Ctrl+Left cover all the steps) and then presses Ctrl+Right exactly twice
        /// (0 -> 25 -> 50), which lands on 50 no matter where it started.
        /// </summary>
        public static void SetReplaySpeedNormal()
        {
            CtrlTaps(ScanLeft, 6);   // floor to 0
            Thread.Sleep(60);
            CtrlTaps(ScanRight, 2);  // 0 -> 25 -> 50
        }

        /// <summary>
        /// Same as <see cref="SetReplaySpeedNormal"/> but delivered with PostMessage straight to
        /// the game's window instead of SendInput - a fallback for a fullscreen-exclusive game
        /// whose DirectInput does not see synthesized OS input. Returns false if the game window
        /// could not be found. (Whether it takes effect depends on the game reading Ctrl+Arrow
        /// from the message loop rather than DirectInput.)
        /// </summary>
        public static bool SetReplaySpeedNormalViaPost()
        {
            IntPtr hwnd = GetGameWindow();
            if (hwnd == IntPtr.Zero)
            {
                DebugLogger.Warn("GameInput: PostMessage path - game window (age2-WK) NOT found.");
                return false;
            }
            DebugLogger.Info($"GameInput: PostMessage path - sending Ctrl+Arrow to game HWND 0x{hwnd.ToInt64():X}.");
            for (int i = 0; i < 6; i++) { PostCtrlArrow(hwnd, ScanLeft); Thread.Sleep(35); }
            Thread.Sleep(60);
            for (int i = 0; i < 2; i++) { PostCtrlArrow(hwnd, ScanRight); Thread.Sleep(35); }
            return true;
        }

        /// <summary>Posts a Ctrl+Arrow chord to a window: Ctrl down, arrow down, arrow up, Ctrl up.</summary>
        private static void PostCtrlArrow(IntPtr hwnd, ushort arrowScan)
        {
            // lParam: bit0-15 repeat=1, bit16-23 scancode, bit24 extended, bit30 prev-state,
            // bit31 transition. Arrows are extended keys.
            uint ctrlDown = 0x00000001u | (0x1Du << 16);
            uint ctrlUp = 0xC0000001u | (0x1Du << 16);
            uint arrDown = 0x01000001u | ((uint)arrowScan << 16);           // extended (bit24)
            uint arrUp = 0xC1000001u | ((uint)arrowScan << 16);
            PostMessage(hwnd, WM_KEYDOWN, (IntPtr)VkControl, (IntPtr)ctrlDown);
            ushort arrowVk = arrowScan == ScanLeft ? VkLeft : VkRight;
            PostMessage(hwnd, WM_KEYDOWN, (IntPtr)arrowVk, (IntPtr)arrDown);
            Thread.Sleep(30);
            PostMessage(hwnd, WM_KEYUP, (IntPtr)arrowVk, (IntPtr)arrUp);
            PostMessage(hwnd, WM_KEYUP, (IntPtr)VkControl, (IntPtr)ctrlUp);
        }

        /// <summary>The game's main visible top-level window, or IntPtr.Zero.</summary>
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
            catch (Exception ex)
            {
                DebugLogger.Warn($"GameInput: cannot find game window: {ex.Message}");
            }
            return found;
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
        /// Holds Ctrl down, taps the arrow <paramref name="taps"/> times (each with a ~50ms
        /// hold), then releases Ctrl.
        ///
        /// Injected as HARDWARE SCAN CODES (KEYEVENTF_SCANCODE), not virtual keys: AoE2 reads
        /// the keyboard through DirectInput, which looks at raw scan codes, and a virtual-key
        /// SendInput - which the game never sees - was why the earlier attempt did nothing even
        /// though the same Ctrl+Left works when typed by hand. Arrow keys carry the extended
        /// flag. Ctrl is held for the whole run so every tap is seen as Ctrl+Arrow.
        /// </summary>
        private static void CtrlTaps(ushort arrowScan, int taps)
        {
            try
            {
                Send(MakeScan(ScanControl, keyUp: false, extended: false));
                Thread.Sleep(20);
                for (int i = 0; i < taps; i++)
                {
                    Send(MakeScan(arrowScan, keyUp: false, extended: true));
                    Thread.Sleep(50);
                    Send(MakeScan(arrowScan, keyUp: true, extended: true));
                    Thread.Sleep(30);
                }
                Send(MakeScan(ScanControl, keyUp: true, extended: false));
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"GameInput: SendInput failed: {ex.Message}");
            }
        }

        private static void Send(INPUT input) =>
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());

        private static INPUT MakeScan(ushort scan, bool keyUp, bool extended)
        {
            uint flags = KEYEVENTF_SCANCODE;
            if (keyUp) flags |= KEYEVENTF_KEYUP;
            if (extended) flags |= KEYEVENTF_EXTENDEDKEY;
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = 0, wScan = scan, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero }
                }
            };
        }

        // ---- interop ----
        private const int INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_SCANCODE = 0x0008;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public int type; public InputUnion U; }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
