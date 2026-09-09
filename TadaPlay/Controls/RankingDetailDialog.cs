using System;
using System.Drawing;
using System.Windows.Forms;
using AntdUI;
using TadaPlay.Common.Models;

namespace TadaPlay.Controls
{
    /// <summary>
    /// One player from the ranking table, opened by clicking their row.
    ///
    /// The row already carries every number, so repeating them is not the point of this - the
    /// point is the in-game name, which the table has no column wide enough for and which is how
    /// teammates actually recognise each other. The stats come along because a detail view that
    /// showed only the one extra field would look broken.
    ///
    /// Deliberately not UserStatusDialog: that one is about a player who is online right now -
    /// it pings them and offers to spectate. A ranking row is just as often somebody who last
    /// played a week ago, and offering to watch them would be a dead button.
    /// </summary>
    public sealed class RankingDetailDialog : Form
    {
        private const int Pad = 18;

        public RankingDetailDialog(RankingEntry player)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));

            Text = "Thông tin người chơi";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(420, 340);
            Font = new Font("Segoe UI", 9.75F);
            BackColor = UiTheme.PageBg;

            var card = new AntdUI.Panel
            {
                Location = new Point(14, 14),
                Size = new Size(392, 262),
                Radius = 10,
                Shadow = 8,
                ShadowOpacity = 0.10F,
                Back = Color.White,
                BorderWidth = 1,
                BorderColor = UiTheme.CardBorder,
            };

            string display = string.IsNullOrWhiteSpace(player.DisplayName) ? player.Username : player.DisplayName;

            var name = new AntdUI.Label
            {
                Text = display ?? string.Empty,
                Location = new Point(Pad, 14),
                Size = new Size(356, 30),
                PrefixSvg = UiTheme.IconAccount,
                PrefixColor = UiTheme.AccentDisplay,
                IconGap = 10,
                ForeColor = UiTheme.Ink,
                Font = new Font("Segoe UI Semibold", 13F, FontStyle.Bold),
                BackColor = Color.Transparent,
                AutoEllipsis = true,
            };

            // Only when it differs - repeating the title back at the reader is noise.
            bool sameAsDisplay = string.Equals(display, player.Username, StringComparison.OrdinalIgnoreCase);
            var account = new AntdUI.Label
            {
                Text = sameAsDisplay ? string.Empty : $"Tài khoản: {player.Username}",
                Location = new Point(Pad, 46),
                Size = new Size(356, 20),
                ForeColor = UiTheme.Muted,
                Font = new Font("Segoe UI", 9.5F),
                BackColor = Color.Transparent,
                AutoEllipsis = true,
            };

            string inGame = (player.InGameName ?? string.Empty).Trim();
            var inGameLabel = new AntdUI.Label
            {
                Text = inGame.Length > 0 ? $"Tên trong game: “{inGame}”" : "Tên trong game: chưa rõ",
                Location = new Point(Pad, sameAsDisplay ? 46 : 68),
                Size = new Size(356, 22),
                ForeColor = inGame.Length > 0 ? UiTheme.Ink : UiTheme.Muted,
                Font = new Font("Segoe UI", 10F),
                BackColor = Color.Transparent,
                AutoEllipsis = true,
            };

            int statsTop = (sameAsDisplay ? 46 : 68) + 32;

            var rank = Stat("Hạng", "#" + player.Rank, new Point(Pad, statsTop), UiTheme.AccentDisplay);
            var elo = Stat("ELO", player.Rating.ToString(), new Point(Pad + 128, statsTop), UiTheme.AccentAccountStrong);
            var games = Stat("Trận", player.GamesPlayed.ToString(), new Point(Pad + 256, statsTop), UiTheme.Ink);

            var wl = Stat("Thắng / Bại", $"{player.Wins} / {player.Losses}", new Point(Pad, statsTop + 66), UiTheme.Ink);
            var rate = Stat("Tỉ lệ thắng", player.WinRate + "%", new Point(Pad + 128, statsTop + 66),
                            player.WinRate >= 50 ? UiTheme.AccentFolder : UiTheme.Muted);
            var vpn = Stat("VPN", string.IsNullOrWhiteSpace(player.VpnIp) ? "—" : player.VpnIp,
                           new Point(Pad + 256, statsTop + 66), UiTheme.Muted, small: true);

            foreach (Control c in new Control[] { name, account, inGameLabel })
            {
                if (!string.IsNullOrEmpty(c.Text)) card.Controls.Add(c);
            }
            foreach (Control c in new[] { rank, elo, games, wl, rate, vpn }) card.Controls.Add(c);

            var close = new AntdUI.Button
            {
                Text = "Đóng",
                Location = new Point(296, 288),
                Size = new Size(110, 40),
                Type = TTypeMini.Default,
            };
            close.Click += (s, e) => Close();
            CancelButton = close;

            Controls.Add(card);
            Controls.Add(close);
        }

        /// <summary>A caption above a value, which is the whole layout of this dialog.</summary>
        private static System.Windows.Forms.Panel Stat(string caption, string value, Point at, Color valueColour, bool small = false)
        {
            var host = new System.Windows.Forms.Panel { Location = at, Size = new Size(124, 56), BackColor = Color.Transparent };
            host.Controls.Add(new AntdUI.Label
            {
                Text = caption,
                Location = new Point(0, 0),
                Size = new Size(124, 18),
                ForeColor = UiTheme.Muted,
                Font = new Font("Segoe UI", 9F),
                BackColor = Color.Transparent,
            });
            host.Controls.Add(new AntdUI.Label
            {
                Text = value,
                Location = new Point(0, 18),
                Size = new Size(124, 30),
                ForeColor = valueColour,
                Font = new Font("Segoe UI Semibold", small ? 10F : 15F, FontStyle.Bold),
                BackColor = Color.Transparent,
                AutoEllipsis = true,
            });
            return host;
        }
    }
}
