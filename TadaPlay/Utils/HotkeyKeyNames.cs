using System.Collections.Generic;
using System.Windows.Forms;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Turns Win32 virtual-key codes (as stored in .hki) into readable labels, and turns a captured
    /// WinForms key press back into a code. WinForms <see cref="Keys"/> values are the VK codes for
    /// ordinary keys, so most of the mapping is the enum itself; this table just gives friendlier
    /// names and covers the punctuation/OEM keys AoE2 uses.
    /// </summary>
    public static class HotkeyKeyNames
    {
        public const int Unbound = 0;

        private static readonly Dictionary<int, string> Names = new Dictionary<int, string>
        {
            { 0, "—" },
            { 8, "Backspace" }, { 9, "Tab" }, { 13, "Enter" }, { 19, "Pause" },
            { 20, "Caps Lock" }, { 27, "Esc" }, { 32, "Space" },
            { 33, "Page Up" }, { 34, "Page Down" }, { 35, "End" }, { 36, "Home" },
            { 37, "Left" }, { 38, "Up" }, { 39, "Right" }, { 40, "Down" },
            { 44, "Print Screen" }, { 45, "Insert" }, { 46, "Delete" },
            { 96, "Num 0" }, { 97, "Num 1" }, { 98, "Num 2" }, { 99, "Num 3" }, { 100, "Num 4" },
            { 101, "Num 5" }, { 102, "Num 6" }, { 103, "Num 7" }, { 104, "Num 8" }, { 105, "Num 9" },
            { 106, "Num *" }, { 107, "Num +" }, { 109, "Num -" }, { 110, "Num ." }, { 111, "Num /" },
            { 186, ";" }, { 187, "=" }, { 188, "," }, { 189, "-" }, { 190, "." }, { 191, "/" },
            { 192, "`" }, { 219, "[" }, { 220, "\\" }, { 221, "]" }, { 222, "'" },
        };

        public static string Describe(int keyCode, bool ctrl, bool alt, bool shift)
        {
            string key = KeyName(keyCode);
            if (keyCode == Unbound) return key; // no modifiers on an unbound slot

            var parts = new List<string>(4);
            if (ctrl) parts.Add("Ctrl");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            parts.Add(key);
            return string.Join(" + ", parts);
        }

        private static string KeyName(int keyCode)
        {
            if (Names.TryGetValue(keyCode, out string name)) return name;
            if (keyCode >= '0' && keyCode <= '9') return ((char)keyCode).ToString();
            if (keyCode >= 'A' && keyCode <= 'Z') return ((char)keyCode).ToString();
            if (keyCode >= 112 && keyCode <= 123) return "F" + (keyCode - 111); // F1..F12
            return "VK" + keyCode;
        }

        /// <summary>
        /// True for keys that only make sense as modifiers - a capture of just Ctrl/Alt/Shift (or a
        /// lone Windows key) shouldn't be committed as the binding's main key.
        /// </summary>
        public static bool IsModifierOnly(Keys keyCode)
        {
            switch (keyCode)
            {
                case Keys.ControlKey:
                case Keys.ShiftKey:
                case Keys.Menu:      // Alt
                case Keys.LWin:
                case Keys.RWin:
                case Keys.None:
                    return true;
                default:
                    return false;
            }
        }
    }
}
