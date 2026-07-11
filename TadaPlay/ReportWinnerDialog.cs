using System;
using System.Drawing;
using System.Windows.Forms;

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
            BackColor = PageBg;

            var prompt = new Label
            {
                Text = "Chọn đội chiến thắng để cập nhật ELO",
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(16, 12, 16, 0),
                Font = new Font("Segoe UI Semibold", 11.5F, FontStyle.Bold)
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

            var hint = new Label
            {
                Text = "Nhấn Esc để huỷ và báo kết quả sau.",
                Location = new Point(16, 364),
                Size = new Size(448, 20),
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 8.5F),
                TextAlign = ContentAlignment.MiddleCenter
            };

            var cancelButton = new Button
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

        private static Button BuildWinButton(string text, Color color)
        {
            var button = new Button
            {
                Text = text,
                BackColor = color,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(color, 0.15f);
            button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(color, 0.05f);
            return button;
        }

        private static Panel BuildTeamCard(string title, string[] members, Color accent)
        {
            var card = new Panel
            {
                BackColor = Color.White,
                Padding = new Padding(1)
            };

            var header = new Label
            {
                Text = title,
                Dock = DockStyle.Top,
                Height = 34,
                BackColor = accent,
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter
            };

            var list = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                IntegralHeight = false,
                Font = new Font("Segoe UI", 9.75F),
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 26,
                SelectionMode = SelectionMode.None
            };
            if (members != null)
            {
                foreach (var m in members)
                {
                    list.Items.Add(m);
                }
            }
            list.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                Color rowBg = e.Index % 2 == 0 ? Color.White : Color.FromArgb(250, 250, 250);
                using (var brush = new SolidBrush(rowBg)) e.Graphics.FillRectangle(brush, e.Bounds);
                TextRenderer.DrawText(
                    e.Graphics, list.Items[e.Index].ToString(), list.Font,
                    new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height),
                    Color.FromArgb(38, 38, 38),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            };

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
