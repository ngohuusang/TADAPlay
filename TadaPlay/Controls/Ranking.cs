using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using AntdUI;
using TadaPlay.Logger;
using TadaPlay.Services.Interface;
using TadaPlay.Utils;

namespace TadaPlay.Controls
{
    /// <summary>
    /// Native ELO leaderboard view. Pulls ranked players from api.php?action=leaderboard.
    ///
    /// Drawn with <see cref="AntdUI.Table"/> rather than a ListView: the striping, the header
    /// styling and the per-cell colours were all being hand-set on ListViewItems before, down to
    /// a 1x32 blank bitmap in an ImageList purely to make the rows taller. The table does all of
    /// that, and matches the rest of the app.
    /// </summary>
    public class Ranking : UserControl
    {
        private const string RankingPageUrl = "https://openvpn.aoe2.io.vn/ranking.php";

        /// <summary>Raised when the user asks to return to the previous (home) screen.</summary>
        public event EventHandler BackRequested;

        private readonly IAccountService _accountService;
        private readonly AntdUI.Table _table;
        private readonly AntdUI.Label _statusLabel;
        private readonly AntdUI.Button _refreshButton;

        public Ranking(IAccountService accountService)
        {
            _accountService = accountService;
            Dock = DockStyle.Fill;
            Font = new Font("Segoe UI", 9.75F);
            BackColor = UiTheme.PageBg;

            var header = new System.Windows.Forms.Panel { Dock = DockStyle.Top, Height = 56, Padding = new Padding(14, 10, 14, 8), BackColor = Color.Transparent };

            var backButton = UiTheme.Quiet("← Trang chủ");
            backButton.Dock = DockStyle.Left;
            backButton.Width = 130;
            backButton.Click += (s, e) => BackRequested?.Invoke(this, EventArgs.Empty);

            var title = new AntdUI.Label
            {
                Text = "Bảng xếp hạng (ELO)",
                Dock = DockStyle.Left,
                Width = 260,
                PrefixSvg = UiTheme.IconTrophy,
                PrefixColor = UiTheme.AccentAccount,
                IconGap = 10,
                Padding = new Padding(14, 0, 0, 0),
                Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold),
                ForeColor = UiTheme.Ink,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent,
            };

            _refreshButton = UiTheme.Quiet("Làm mới");
            _refreshButton.Dock = DockStyle.Right;
            _refreshButton.Width = 110;
            _refreshButton.Click += async (s, e) => await LoadAsync();

            var webButton = UiTheme.Quiet("Mở trang web");
            webButton.Dock = DockStyle.Right;
            webButton.Width = 130;
            webButton.Click += (s, e) => OpenWeb();

            // Dock=Left/Right stack in add order from their own edge inwards.
            header.Controls.Add(title);
            header.Controls.Add(backButton);
            header.Controls.Add(_refreshButton);
            header.Controls.Add(UiTheme.Spacer(DockStyle.Right, 8));
            header.Controls.Add(webButton);

            _table = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                Bordered = true,
                BorderColor = UiTheme.CardBorder,
                Radius = 8,
                Font = new Font("Segoe UI", 10F),
                ColumnFont = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
                RowHeight = 38,
                Columns = new ColumnCollection
                {
                    new Column(nameof(Row.Rank), "#", ColumnAlign.Center) { Width = "60" },
                    new Column(nameof(Row.Player), "Người chơi"),
                    new Column(nameof(Row.Elo), "ELO", ColumnAlign.Right) { Width = "90" },
                    new Column(nameof(Row.Games), "Trận", ColumnAlign.Right) { Width = "80" },
                    new Column(nameof(Row.Wins), "Thắng", ColumnAlign.Right) { Width = "80" },
                    new Column(nameof(Row.Losses), "Bại", ColumnAlign.Right) { Width = "80" },
                    new Column(nameof(Row.WinRate), "Tỉ lệ thắng", ColumnAlign.Right) { Width = "120" },
                },
            };

            var tableHost = new System.Windows.Forms.Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 0, 14, 8), BackColor = Color.Transparent };
            tableHost.Controls.Add(_table);

            _statusLabel = new AntdUI.Label
            {
                Dock = DockStyle.Bottom,
                Height = 30,
                Padding = new Padding(16, 0, 16, 6),
                ForeColor = UiTheme.Muted,
                Font = new Font("Segoe UI", 9.5F),
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent,
            };

            Controls.Add(tableHost);
            Controls.Add(_statusLabel);
            Controls.Add(header);

            Load += async (s, e) => await LoadAsync();
        }

        /// <summary>
        /// One table row. The property NAMES are the column keys, so they have to keep matching
        /// the <see cref="Column"/>s above - which is why those use nameof rather than literals.
        ///
        /// The typed cells (<see cref="CellText"/>, <see cref="CellTag"/>) are how a single cell
        /// gets its own colour or weight without styling the whole row.
        /// </summary>
        private sealed class Row
        {
            public object Rank { get; set; }
            public object Player { get; set; }
            public object Elo { get; set; }
            public int Games { get; set; }
            public int Wins { get; set; }
            public int Losses { get; set; }
            public object WinRate { get; set; }
        }

        private async System.Threading.Tasks.Task LoadAsync()
        {
            _refreshButton.Enabled = false;
            _refreshButton.Loading = true;
            _statusLabel.ForeColor = UiTheme.Muted;
            _statusLabel.Text = "Đang tải bảng xếp hạng...";
            try
            {
                var players = await _accountService.GetLeaderboardAsync();

                var bold = new Font("Segoe UI Semibold", 10F, FontStyle.Bold);
                // The medals need a font that actually has them. AntdUI.Table draws its own text
                // and does not fall back the way a WinForms Label does, so a podium row rendered
                // as an empty box until the cell was given Segoe UI Emoji explicitly.
                var medal = new Font("Segoe UI Emoji", 12F, FontStyle.Regular);
                var rows = new List<Row>(players.Count);
                foreach (var p in players)
                {
                    // A medal for the top three, the number for everyone else.
                    string rankText = p.Rank switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => p.Rank.ToString() };
                    bool podium = p.Rank <= 3;
                    bool winning = p.WinRate >= 50;

                    rows.Add(new Row
                    {
                        Rank = new CellText(rankText) { Font = podium ? medal : null },
                        Player = new CellText(p.DisplayName ?? p.Username) { Font = podium ? bold : null },
                        Elo = new CellText(p.Rating.ToString()) { Font = bold },
                        Games = p.GamesPlayed,
                        Wins = p.Wins,
                        Losses = p.Losses,
                        // A tag rather than coloured text: it is a verdict on the player, and it
                        // is the one cell anybody scans the column for.
                        WinRate = new CellTag(p.WinRate + "%", winning ? TTypeMini.Success : TTypeMini.Default),
                    });
                }

                _table.DataSource = rows;

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
                _refreshButton.Loading = false;
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
