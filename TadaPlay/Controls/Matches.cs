using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using TadaPlay.Common.Models;
using TadaPlay.Contexts.Interfaces;
using TadaPlay.Logger;
using TadaPlay.Services.Interface;
using TadaPlay.Utils;

namespace TadaPlay.Controls
{
    /// <summary>
    /// Lists uploaded matches (api.php?action=matches) and lets the user download a match's
    /// recorded game into the AoE2 DE savegame folder so it can be replayed in-game.
    /// </summary>
    public class Matches : UserControl
    {
        /// <summary>Raised when the user asks to return to the previous (home) screen.</summary>
        public event EventHandler BackRequested;

        private readonly IAccountService _accountService;
        private readonly IAppContext _appContext;
        private readonly ListView _listView;
        private readonly Label _statusLabel;
        private readonly Button _refreshButton;
        private readonly Button _replayButton;

        public Matches(IAccountService accountService, IAppContext appContext)
        {
            _accountService = accountService;
            _appContext = appContext;
            Dock = DockStyle.Fill;
            Font = new Font("Segoe UI", 9.75F);

            var header = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(12, 8, 12, 8) };
            var backButton = new Button { Text = "← Trang chủ", Dock = DockStyle.Left, Width = 110 };
            backButton.Click += (s, e) => BackRequested?.Invoke(this, EventArgs.Empty);
            var title = new Label
            {
                Text = "   📜 Danh sách trận đấu",
                Dock = DockStyle.Left,
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _refreshButton = new Button { Text = "Làm mới", Dock = DockStyle.Right, Width = 90 };
            _refreshButton.Click += async (s, e) => await LoadAsync();
            header.Controls.Add(title);
            header.Controls.Add(backButton);
            header.Controls.Add(_refreshButton);

            _listView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                MultiSelect = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            _listView.Columns.Add("Thời gian", 130, HorizontalAlignment.Left);
            _listView.Columns.Add("Phòng", 120, HorizontalAlignment.Left);
            _listView.Columns.Add("Đội 1", 170, HorizontalAlignment.Left);
            _listView.Columns.Add("Đội 2", 170, HorizontalAlignment.Left);
            _listView.Columns.Add("Kết quả", 90, HorizontalAlignment.Left);
            _listView.Columns.Add("MVP", 110, HorizontalAlignment.Left);
            _listView.SelectedIndexChanged += (s, e) => UpdateReplayButton();
            _listView.DoubleClick += (s, e) => ShowScoreboard();

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(12, 6, 12, 6) };
            _replayButton = new Button { Text = "Phát lại", Dock = DockStyle.Right, Width = 120, Enabled = false };
            _replayButton.Click += async (s, e) => await ReplaySelectedAsync();
            var detailsButton = new Button { Text = "Chi tiết / Điểm", Dock = DockStyle.Right, Width = 130 };
            detailsButton.Click += (s, e) => ShowScoreboard();
            footer.Controls.Add(_replayButton);
            footer.Controls.Add(detailsButton);

            _statusLabel = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 26,
                Padding = new Padding(12, 4, 12, 4),
                ForeColor = Color.Gray
            };

            Controls.Add(_listView);
            Controls.Add(_statusLabel);
            Controls.Add(footer);
            Controls.Add(header);

            Load += async (s, e) => await LoadAsync();
        }

        private void UpdateReplayButton()
        {
            _replayButton.Enabled = _listView.SelectedItems.Count > 0
                && _listView.SelectedItems[0].Tag is MatchSummary m && m.CanReplay;
        }

        private static string TeamText(MatchSummary m, string teamKey)
        {
            if (m.Teams != null && m.Teams.TryGetValue(teamKey, out var members) && members != null && members.Length > 0)
            {
                return string.Join(", ", members);
            }
            return "—";
        }

        private static string ResultText(MatchSummary m)
        {
            if (m.Status == "needs_review") return "Chưa xác định";
            if (m.WinnerTeam == 1) return "Đội 1 thắng";
            if (m.WinnerTeam == 2) return "Đội 2 thắng";
            return "Chưa báo KQ";
        }

        private async System.Threading.Tasks.Task LoadAsync()
        {
            _refreshButton.Enabled = false;
            _statusLabel.ForeColor = Color.Gray;
            _statusLabel.Text = "Đang tải danh sách trận đấu...";
            try
            {
                var matches = await _accountService.GetMatchesAsync();

                _listView.BeginUpdate();
                _listView.Items.Clear();
                foreach (var m in matches)
                {
                    var item = new ListViewItem(FormatTime(m.FinishedAt));
                    item.SubItems.Add(m.RoomName ?? "");
                    item.SubItems.Add(TeamText(m, "1"));
                    item.SubItems.Add(TeamText(m, "2"));
                    item.SubItems.Add(ResultText(m));
                    item.SubItems.Add(string.IsNullOrEmpty(m.Mvp) ? "—" : "⭐ " + m.Mvp);
                    item.Tag = m;
                    _listView.Items.Add(item);
                }
                _listView.EndUpdate();

                _statusLabel.Text = matches.Count == 0
                    ? "Chưa có trận đấu nào."
                    : $"{matches.Count} trận đấu. Chọn một trận và bấm \"Phát lại\".";
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"Matches: load failed: {ex.Message}");
                _statusLabel.ForeColor = Color.Firebrick;
                _statusLabel.Text = "Lỗi tải danh sách trận đấu: " + ex.Message;
            }
            finally
            {
                _refreshButton.Enabled = true;
                UpdateReplayButton();
            }
        }

        private static string FormatTime(string raw)
        {
            if (DateTime.TryParse(raw, out var dt))
            {
                return dt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
            }
            return raw ?? "";
        }

        private void ShowScoreboard()
        {
            if (_listView.SelectedItems.Count == 0 || _listView.SelectedItems[0].Tag is not MatchSummary match)
            {
                return;
            }
            if (match.Players == null || match.Players.Count == 0)
            {
                MessageBox.Show("Trận này chưa có dữ liệu điểm số (record chưa được phân tích).",
                    "Chi tiết", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Phòng: {match.RoomName}    Kết quả: {ResultText(match)}");
            if (!string.IsNullOrEmpty(match.Mvp)) sb.AppendLine($"MVP: ⭐ {match.Mvp}");
            sb.AppendLine();
            foreach (var team in new[] { 1, 2 })
            {
                bool teamWon = match.WinnerTeam == team;
                sb.AppendLine($"— Đội {team}{(teamWon ? " (Thắng)" : "")} —");
                foreach (var p in match.Players.Where(p => p.Team == team).OrderByDescending(p => p.Score ?? 0))
                {
                    string star = p.Mvp ? " ⭐MVP" : "";
                    sb.AppendLine($"   {p.Name,-16} điểm: {p.Score,-6} eapm: {p.Eapm}{star}");
                }
                sb.AppendLine();
            }

            MessageBox.Show(sb.ToString(), "Bảng điểm trận đấu", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private async System.Threading.Tasks.Task ReplaySelectedAsync()
        {
            if (_listView.SelectedItems.Count == 0 || _listView.SelectedItems[0].Tag is not MatchSummary match)
            {
                return;
            }

            // Find where the game looks for replays; let the user choose if we can't.
            string saveDir = RecordedGameFinder.FindSaveGameDirectory(_appContext.GetGameFolder());
            if (string.IsNullOrEmpty(saveDir))
            {
                using var picker = new FolderBrowserDialog
                {
                    Description = "Không tìm thấy thư mục SaveGame của game. Chọn nơi lưu file record để phát lại."
                };
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                saveDir = picker.SelectedPath;
            }

            string fileName = string.IsNullOrWhiteSpace(match.FileName)
                ? $"tadaplay_match_{match.Id}.mgz"
                : Path.GetFileName(match.FileName);
            string destPath = Path.Combine(saveDir, fileName);

            _replayButton.Enabled = false;
            _statusLabel.ForeColor = Color.Gray;
            _statusLabel.Text = $"Đang tải record của trận #{match.Id}...";
            try
            {
                await _accountService.DownloadRecordAsync(match.Id, destPath);

                _statusLabel.ForeColor = Color.DarkGreen;
                _statusLabel.Text = $"Đã tải về: {destPath}";

                // Reveal the file in Explorer; it will also show in the game's Replays list.
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{destPath}\"")
                    {
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    DebugLogger.Warn($"Matches: could not open Explorer: {ex.Message}");
                }

                MessageBox.Show(
                    "Đã tải record vào thư mục lưu của AoE2 DE.\n\nMở Age of Empires II: DE → mục \"Replays\" (Xem lại) để phát lại trận đấu.",
                    "Phát lại", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"Matches: replay download failed: {ex.Message}");
                _statusLabel.ForeColor = Color.Firebrick;
                _statusLabel.Text = "Lỗi tải record: " + ex.Message;
                MessageBox.Show(ex.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                UpdateReplayButton();
            }
        }
    }
}
