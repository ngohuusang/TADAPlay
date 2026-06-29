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
        /// <summary>1 or 2 when a winner was chosen; null if cancelled.</summary>
        public int? WinningTeam { get; private set; }

        public ReportWinnerDialog(string[] team1, string[] team2, int? suggestedWinner = null)
        {
            Text = "Báo kết quả trận đấu";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(420, 320);
            Font = new Font("Segoe UI", 9.75F);

            var prompt = new Label
            {
                Text = "Chọn đội chiến thắng để cập nhật ELO:",
                Dock = DockStyle.Top,
                Height = 32,
                Padding = new Padding(12, 8, 12, 0),
                Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold)
            };

            var team1Box = BuildTeamBox("Đội 1", team1);
            team1Box.Location = new Point(12, 44);
            team1Box.Size = new Size(190, 200);

            var team2Box = BuildTeamBox("Đội 2", team2);
            team2Box.Location = new Point(218, 44);
            team2Box.Size = new Size(190, 200);

            var team1WinButton = new Button
            {
                Text = suggestedWinner == 1 ? "Đội 1 thắng (gợi ý)" : "Đội 1 thắng",
                Location = new Point(12, 256),
                Size = new Size(190, 40),
                BackColor = Color.FromArgb(82, 196, 26),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            team1WinButton.Click += (s, e) => Choose(1);

            var team2WinButton = new Button
            {
                Text = suggestedWinner == 2 ? "Đội 2 thắng (gợi ý)" : "Đội 2 thắng",
                Location = new Point(218, 256),
                Size = new Size(190, 40),
                BackColor = Color.FromArgb(24, 144, 255),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            team2WinButton.Click += (s, e) => Choose(2);

            // Pre-focus the parser's suggested winner so the host can just press Enter to confirm.
            if (suggestedWinner == 1) AcceptButton = team1WinButton;
            else if (suggestedWinner == 2) AcceptButton = team2WinButton;

            var cancelButton = new Button
            {
                Text = "Hủy",
                Location = new Point(12, 300),
                Size = new Size(396, 14),
                Visible = false, // reserved; Esc cancels
                DialogResult = DialogResult.Cancel
            };

            Controls.Add(prompt);
            Controls.Add(team1Box);
            Controls.Add(team2Box);
            Controls.Add(team1WinButton);
            Controls.Add(team2WinButton);
            Controls.Add(cancelButton);

            CancelButton = cancelButton;
        }

        private static GroupBox BuildTeamBox(string title, string[] members)
        {
            var box = new GroupBox { Text = title };
            var list = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                IntegralHeight = false
            };
            if (members != null)
            {
                foreach (var m in members)
                {
                    list.Items.Add(m);
                }
            }
            box.Controls.Add(list);
            return box;
        }

        private void Choose(int team)
        {
            WinningTeam = team;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
