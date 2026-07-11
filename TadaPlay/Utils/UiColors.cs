using System.Drawing;

namespace TadaPlay.Utils
{
    /// <summary>Shared accent colors so the matches/ranking/report screens read as one consistent palette.</summary>
    public static class UiColors
    {
        public static readonly Color Winner = Color.FromArgb(56, 158, 13);       // antd green-6
        public static readonly Color WinnerTint = Color.FromArgb(246, 255, 237); // antd green-1
        public static readonly Color Loser = Color.FromArgb(140, 140, 140);      // antd gray-7
        public static readonly Color LoserTint = Color.FromArgb(250, 250, 250);  // antd gray-2
        public static readonly Color Mvp = Color.FromArgb(212, 136, 6);          // antd gold-7
        public static readonly Color ZebraStripe = Color.FromArgb(250, 250, 250);
    }
}
