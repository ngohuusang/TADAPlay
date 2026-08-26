using System;
using System.Drawing;
using System.Windows.Forms;
using AntdUI;
using TadaPlay.Controls;

namespace TadaPlay
{
    /// <summary>
    /// Simple host-facing dialog to report which team won a finished 4v4 game.
    /// Teams are parsed from the replay server-side and passed in for display.
    /// Result is read from <see cref="WinningTeam"/> when DialogResult is OK.
    /// </summary>
    public class ReportWinnerDialog : Form
    {
        private static readonly Color Team1Color = Color.FromArgb(82, 196, 26);   // antd green-6
        private static readonly Color Team2Color = Color.FromArgb(24, 144, 255);  // antd blue-6
        private static readonly Color HeaderBg = Color.FromArgb(38, 38, 38);      // antd gray-11
        private static readonly Color PageBg = Color.FromArgb(245, 245, 245);     // antd gray-3

        /// <summary>1 or 2 when a winner was chosen; null if cancelled.</summary>
        public int? WinningTeam { get; private set; }

        public ReportWinnerDialog(string[] team1, string[] team2, int? suggestedWinner = null)
        {
            Text = "Báo kết quả trận đấu";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(480, 400);
            Font = new Font("Segoe UI", 9.75F);
            BackColor = UiTheme.PageBg;

            var prompt = new AntdUI.Label
            {
                Text = "Chọn đội chiến thắng để cập nhật ELO",
                Dock = DockStyle.Top,
                Height = 44,
                Padding = new Padding(16, 12, 16, 0),
                ForeColor = UiTheme.Ink,
                Font = new Font("Segoe UI Semibold", 11.5F, FontStyle.Bold),
                BackColor = Color.Transparent,
            };

            var team1Box = BuildTeamCard("Đội 1", team1, Team1Color);
            team1Box.Location = new Point(16, 48);
            team1Box.Size = new Size(212, 240);

            var team2Box = BuildTeamCard("Đội 2", team2, Team2Color);
            team2Box.Location = new Point(252, 48);
            team2Box.Size = new Size(212, 240);

            var team1WinButton = BuildWinButton(
                suggestedWinner == 1 ? "★ Đội 1 thắng (gợi ý)" : "Đội 1 thắng", Team1Color);
            team1WinButton.Location = new Point(16, 304);
            team1WinButton.Size = new Size(212, 48);
            team1WinButton.Click += (s, e) => Choose(1);

            var team2WinButton = BuildWinButton(
                suggestedWinner == 2 ? "★ Đội 2 thắng (gợi ý)" : "Đội 2 thắng", Team2Color);
            team2WinButton.Location = new Point(252, 304);
            team2WinButton.Size = new Size(212, 48);
            team2WinButton.Click += (s, e) => Choose(2);

            // Pre-focus the parser's suggested winner so the host can just press Enter to confirm.
            if (suggestedWinner == 1) AcceptButton = team1WinButton;
            else if (suggestedWinner == 2) AcceptButton = team2WinButton;

            var hint = new AntdUI.Label
            {
                Text = "Nhấn Esc để huỷ và báo kết quả sau.",
                Location = new Point(16, 364),
                Size = new Size(448, 20),
                ForeColor = UiTheme.Muted,
                Font = new Font("Segoe UI", 8.5F),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent,
            };

            // Invisible and never clicked - it exists only so Esc has something to map to.
            var cancelButton = new System.Windows.Forms.Button
            {
                Location = new Point(16, 364),
                Size = new Size(448, 20),
                Visible = false, // reserved; Esc cancels
                DialogResult = DialogResult.Cancel
            };

            Controls.Add(prompt);
            Controls.Add(team1Box);
            Controls.Add(team2Box);
            Controls.Add(team1WinButton);
            Controls.Add(team2WinButton);
            Controls.Add(hint);
            Controls.Add(cancelButton);

            CancelButton = cancelButton;
        }

        private static AntdUI.Button BuildWinButton(string text, Color color)
        {
            return new AntdUI.Button
            {
                Text = text,
                Type = TTypeMini.Primary,
                Shape = TShape.Round,
                BackColor = color,
                BackHover = ControlPaint.Light(color, 0.15f),
                BackActive = ControlPaint.Dark(color, 0.05f),
                Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                WaveSize = 4,   // the click ripple, so a mis-hit is visibly a hit
            };
        }

        /// <summary>
        /// One team: an antd card with a coloured name band and the players under it.
        ///
        /// The players were an owner-drawn ListBox purely to get striped, unselectable rows -
        /// twenty lines of DrawItem to render eight names nobody can click. They are labels in
        /// a stack now, which is what they always were.
        /// </summary>
        private static AntdUI.Panel BuildTeamCard(string title, string[] members, Color accent)
        {
            var card = new AntdUI.Panel
            {
                Radius = 10,
                Shadow = 6,
                ShadowOpacity = 0.10F,
                Back = Color.White,
                BorderWidth = 1,
                BorderColor = UiTheme.CardBorder,
                Padding = new Padding(0),
            };

            var header = new AntdUI.Panel
            {
                Dock = DockStyle.Top,
                Height = 38,
                Back = accent,
                Radius = 10,
                RadiusAlign = TAlignRound.Top,
            };
            header.Controls.Add(new AntdUI.Label
            {
                Dock = DockStyle.Fill,
                Text = title,
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent,
            });

            var list = new System.Windows.Forms.Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.White,
                Padding = new Padding(0, 6, 0, 6),
            };
            if (members != null)
            {
                // Dock=Top stacks in reverse add order, so walk the team backwards to keep
                // them in the order the replay listed them.
                for (int i = members.Length - 1; i >= 0; i--)
                {
                    list.Controls.Add(new AntdUI.Label
                    {
                        Dock = DockStyle.Top,
                        Height = 28,
                        Text = members[i],
                        Padding = new Padding(12, 0, 8, 0),
                        ForeColor = UiTheme.Ink,
                        Font = new Font("Segoe UI", 9.75F),
                        TextAlign = ContentAlignment.MiddleLeft,
                        AutoEllipsis = true,
                        BackColor = i % 2 == 0 ? Color.White : UiTheme.RowBand,
                    });
                }
            }

            card.Controls.Add(list);
            card.Controls.Add(header);
            return card;
        }

        private void Choose(int team)
        {
            WinningTeam = team;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
