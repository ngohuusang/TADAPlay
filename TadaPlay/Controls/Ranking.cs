using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using TadaPlay.Logger;
using TadaPlay.Services.Interface;

namespace TadaPlay.Controls
{
    /// <summary>
    /// Native ELO leaderboard view. Pulls ranked players from api.php?action=leaderboard.
    /// </summary>
    public class Ranking : UserControl
    {
        private const string RankingPageUrl = "https://openvpn.aoe2.io.vn/ranking.php";

        /// <summary>Raised when the user asks to return to the previous (home) screen.</summary>
        public event EventHandler BackRequested;

        private readonly IAccountService _accountService;
        private readonly ListView _listView;
        private readonly Label _statusLabel;
        private readonly Button _refreshButton;

        public Ranking(IAccountService accountService)
        {
            _accountService = accountService;
            Dock = DockStyle.Fill;
            Font = new Font("Segoe UI", 9.75F);

            var header = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(12, 8, 12, 8) };
            var backButton = new Button { Text = "← Trang chủ", Dock = DockStyle.Left, Width = 110 };
            backButton.Click += (s, e) => BackRequested?.Invoke(this, EventArgs.Empty);
            var title = new Label
            {
                Text = "   🏆 Bảng xếp hạng (ELO)",
                Dock = DockStyle.Left,
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _refreshButton = new Button { Text = "Làm mới", Dock = DockStyle.Right, Width = 90 };
            _refreshButton.Click += async (s, e) => await LoadAsync();
            var webButton = new Button { Text = "Mở trang web", Dock = DockStyle.Right, Width = 110 };
            webButton.Click += (s, e) => OpenWeb();

            // Add in right-to-left / left-to-right dock order: title after back so it sits to its right.
            header.Controls.Add(title);
            header.Controls.Add(backButton);
            header.Controls.Add(_refreshButton);
            header.Controls.Add(webButton);

            _listView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                MultiSelect = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            _listView.Columns.Add("#", 50, HorizontalAlignment.Center);
            _listView.Columns.Add("Người chơi", 200, HorizontalAlignment.Left);
            _listView.Columns.Add("ELO", 80, HorizontalAlignment.Right);
            _listView.Columns.Add("Trận", 70, HorizontalAlignment.Right);
            _listView.Columns.Add("Thắng", 70, HorizontalAlignment.Right);
            _listView.Columns.Add("Bại", 70, HorizontalAlignment.Right);
            _listView.Columns.Add("Tỉ lệ thắng", 100, HorizontalAlignment.Right);

            _statusLabel = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 26,
                Padding = new Padding(12, 4, 12, 4),
                ForeColor = Color.Gray
            };

            Controls.Add(_listView);
            Controls.Add(_statusLabel);
            Controls.Add(header);

            Load += async (s, e) => await LoadAsync();
        }

        private async System.Threading.Tasks.Task LoadAsync()
        {
            _refreshButton.Enabled = false;
            _statusLabel.ForeColor = Color.Gray;
            _statusLabel.Text = "Đang tải bảng xếp hạng...";
            try
            {
                var players = await _accountService.GetLeaderboardAsync();

                _listView.BeginUpdate();
                _listView.Items.Clear();
                foreach (var p in players)
                {
                    string rankText = p.Rank switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => p.Rank.ToString() };
                    var item = new ListViewItem(rankText);
                    item.SubItems.Add(p.DisplayName ?? p.Username);
                    item.SubItems.Add(p.Rating.ToString());
                    item.SubItems.Add(p.GamesPlayed.ToString());
                    item.SubItems.Add(p.Wins.ToString());
                    item.SubItems.Add(p.Losses.ToString());
                    item.SubItems.Add(p.WinRate + "%");
                    _listView.Items.Add(item);
                }
                _listView.EndUpdate();

                _statusLabel.Text = players.Count == 0
                    ? "Chưa có trận đấu nào được xếp hạng."
                    : $"{players.Count} người chơi.";
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"Ranking: load failed: {ex.Message}");
                _statusLabel.ForeColor = Color.Firebrick;
                _statusLabel.Text = "Lỗi tải bảng xếp hạng: " + ex.Message;
            }
            finally
            {
                _refreshButton.Enabled = true;
            }
        }

        private void OpenWeb()
        {
            try
            {
                Process.Start(new ProcessStartInfo(RankingPageUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"Ranking: open web failed: {ex.Message}");
            }
        }
    }
}
