using System;
using System.Drawing;
using System.Windows.Forms;
using AntdUI;
using TadaPlay.Common.Models;
using TadaPlay.Contexts.Interfaces;
using TadaPlay.Services.Interface;
using TadaPlay.Utils;

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

        private readonly AntdUI.Label _formerNames = new AntdUI.Label();
        private AntdUI.Table _matches;
        private readonly AntdUI.Label _replayStatus = new AntdUI.Label();
        private readonly AntdUI.Button _replayButton = new AntdUI.Button();
        private bool _replaying;

        private IAccountService _accounts;
        private IAppContext _appContext;
        private System.Collections.Generic.List<PlayerMatch> _loaded;

        public RankingDetailDialog(RankingEntry player, IAccountService accounts, IAppContext appContext = null)
        {
            _accounts = accounts;
            _appContext = appContext;
            if (player == null) throw new ArgumentNullException(nameof(player));

            Text = "Thông tin người chơi";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(620, 660);
            Font = new Font("Segoe UI", 9.75F);
            BackColor = UiTheme.PageBg;

            var card = new AntdUI.Panel
            {
                Location = new Point(14, 14),
                Size = new Size(592, 292),
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
                Size = new Size(556, 30),
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
                Size = new Size(556, 20),
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
                Size = new Size(556, 22),
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

            // Filled in once the history arrives. Left blank when this player has only ever used
            // one name, which is most of them - an empty line reads as spacing, whereas
            // "Ten da dung: (khong co)" reads as a fault.
            _formerNames.Location = new Point(Pad, statsTop + 132);
            _formerNames.Size = new Size(556, 36);
            _formerNames.ForeColor = UiTheme.Muted;
            _formerNames.Font = new Font("Segoe UI", 9F);
            _formerNames.TextMultiLine = true;
            _formerNames.BackColor = Color.Transparent;
            _formerNames.Text = string.Empty;
            card.Controls.Add(_formerNames);

            _ = LoadFormerNamesAsync(accounts, player);

            var matchesCaption = new AntdUI.Label
            {
                Text = "Các trận đã đấu  ·  chọn một trận rồi bấm Xem lại",
                Location = new Point(16, 318),
                Size = new Size(300, 22),
                ForeColor = UiTheme.Ink,
                Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold),
                BackColor = Color.Transparent,
            };

            // A table rather than a list of labels: the columns are what make a row scannable,
            // and this is read by eye down the result column far more than it is read across.
            _matches = new AntdUI.Table
            {
                Location = new Point(14, 344),
                Size = new Size(592, 258),
                Bordered = true,
                BorderColor = UiTheme.CardBorder,
                Radius = 8,
                Font = new Font("Segoe UI", 9.5F),
                ColumnFont = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold),
                RowHeight = 34,
                EmptyText = "Chưa có trận nào.",
                // Columns share out the full width instead of sizing to their contents. The
                // default, Auto, sizes each column to what is in it and leaves the remainder as
                // dead space down the right-hand side of the table.
                AutoSizeColumnsMode = ColumnsMode.Fill,
                Columns = new ColumnCollection
                {
                    new Column(nameof(MatchRow.When), "Thời gian") { Width = "150" },
                    new Column(nameof(MatchRow.Result), "Kết quả", ColumnAlign.Center) { Width = "100" },
                    new Column(nameof(MatchRow.Score), "Điểm", ColumnAlign.Right) { Width = "80" },
                    new Column(nameof(MatchRow.Room), "Trận"),
                },
            };

            // Selecting a row arms the button; it does not start anything by itself.
            //
            // Clicking the row WAS the action, and that is the wrong shape for this: downloading
            // a match is megabytes and ends with the game taking over the screen, so it should
            // not be one stray click away while somebody is reading down the table. It also now
            // matches the Matches screen, where a replay has always been select-then-press.
            _matches.SelectIndexChanged += (s2, e2) => UpdateReplayButton();

            var close = new AntdUI.Button
            {
                Text = "Đóng",
                Location = new Point(496, 612),
                Size = new Size(110, 40),
                Type = TTypeMini.Default,
            };
            close.Click += (s, e) => Close();
            CancelButton = close;

            _replayButton.Text = "Xem lại trận";
            _replayButton.Location = new Point(14, 612);
            _replayButton.Size = new Size(150, 40);
            _replayButton.Type = TTypeMini.Primary;
            _replayButton.Enabled = false;
            _replayButton.Click += async (s2, e2) => await ReplaySelectedAsync();

            _replayStatus.Location = new Point(176, 618);
            _replayStatus.Size = new Size(310, 30);
            _replayStatus.ForeColor = UiTheme.Muted;
            _replayStatus.Font = new Font("Segoe UI", 9F);
            _replayStatus.TextMultiLine = true;
            _replayStatus.BackColor = Color.Transparent;
            _replayStatus.Text = string.Empty;

            Controls.Add(card);
            Controls.Add(_replayButton);
            Controls.Add(_replayStatus);
            Controls.Add(matchesCaption);
            Controls.Add(_matches);
            Controls.Add(close);

            _ = LoadMatchesAsync(accounts, player);
        }

        /// <summary>
        /// Fetches the name history in the background. The dialog is usable without it, so a
        /// failure here leaves the line blank rather than showing an error the reader cannot act
        /// on - GetPlayerNamesAsync already logs the reason.
        /// </summary>
        private async System.Threading.Tasks.Task LoadFormerNamesAsync(IAccountService accounts, RankingEntry player)
        {
            if (accounts == null) return;
            try
            {
                var names = await accounts.GetPlayerNamesAsync(player.Username);
                string line = FormerNamesLine(names, player.InGameName);
                if (line == null) return;
                if (IsDisposed || _formerNames.IsDisposed) return;
                if (InvokeRequired) { BeginInvoke(new Action(() => { if (!_formerNames.IsDisposed) _formerNames.Text = line; })); }
                else _formerNames.Text = line;
            }
            catch { /* the line stays blank */ }
        }


        /// <summary>
        /// "Previously known as ..." from the full name history, with the name already on
        /// display removed. Returns null when there is nothing to add, so callers can leave the
        /// line blank rather than print an empty label.
        ///
        /// Newest first, because the interesting question is "who were they last week", not
        /// "who were they in July".
        /// </summary>
        internal static string FormerNamesLine(System.Collections.Generic.IEnumerable<PlayerGameName> all,
                                               string current)
        {
            if (all == null) return null;
            var others = new System.Collections.Generic.List<string>();
            foreach (var entry in all)
            {
                string name = (entry?.Name ?? string.Empty).Trim();
                if (name.Length == 0) continue;
                if (!string.IsNullOrWhiteSpace(current)
                    && string.Equals(name, current.Trim(), System.StringComparison.OrdinalIgnoreCase)) continue;
                if (others.Contains(name)) continue;
                others.Add(name);
            }
            return others.Count == 0 ? null : "Tên đã dùng: " + string.Join("  ·  ", others);
        }

        /// <summary>
        /// Fills the match table in the background.
        ///
        /// Only matches whose teams were resolved can appear - an unresolved record does not know
        /// who played in it - so a player with games_played greater than the number of rows here
        /// is not a contradiction, it is the backlog still waiting on in-game names.
        /// </summary>
        private async System.Threading.Tasks.Task LoadMatchesAsync(IAccountService accounts, RankingEntry player)
        {
            if (accounts == null) return;
            try
            {
                var matches = await accounts.GetPlayerMatchesAsync(player.Username, 40);
                var rows = new System.Collections.Generic.List<MatchRow>(matches.Count);
                foreach (var m in matches)
                {
                    // "Chưa rõ" rather than a loss: nobody has reported this one yet, and
                    // showing it as a defeat would be inventing a result.
                    var (text, tone) = m.Won == null
                        ? ("Chưa rõ", TTypeMini.Default)
                        : (m.Won.Value ? "Thắng" : "Thua", m.Won.Value ? TTypeMini.Success : TTypeMini.Error);

                    rows.Add(new MatchRow
                    {
                        When = FormatWhen(m.FinishedAt),
                        Result = new CellTag(text, tone),
                        Score = m.Score?.ToString() ?? "—",
                        Room = string.IsNullOrWhiteSpace(m.RoomName) ? $"#{m.Id}" : m.RoomName,
                    });
                }

                _loaded = matches;
                if (IsDisposed || _matches == null || _matches.IsDisposed) return;
                if (InvokeRequired) BeginInvoke(new Action(() => { if (!_matches.IsDisposed) _matches.DataSource = rows; }));
                else _matches.DataSource = rows;
                UpdateReplayButton();
            }
            catch { /* the table stays empty, which its EmptyText already explains */ }
        }

        /// <summary>
        /// The match the table currently has selected, or null when nothing is.
        /// </summary>
        private PlayerMatch Selected()
        {
            if (_loaded == null || _matches == null || _matches.IsDisposed) return null;
            int i = _matches.SelectedIndex;
            return i >= 0 && i < _loaded.Count ? _loaded[i] : null;
        }

        /// <summary>
        /// Enables the button only for a row that can actually be replayed, and says why when it
        /// cannot - a disabled button with no explanation just looks broken.
        /// </summary>
        private void UpdateReplayButton()
        {
            if (_replayButton.IsDisposed) return;
            PlayerMatch match = Selected();

            _replayButton.Enabled = !_replaying && match != null && match.CanReplay;

            if (_replayStatus.IsDisposed) return;
            if (match != null && !match.CanReplay)
            {
                _replayStatus.ForeColor = UiTheme.Muted;
                _replayStatus.Text = "Trận này không còn file record trên máy chủ.";
            }
            else if (match != null && _replayStatus.Text == "Trận này không còn file record trên máy chủ.")
            {
                _replayStatus.Text = string.Empty;
            }
        }

        private async System.Threading.Tasks.Task ReplaySelectedAsync()
        {
            await ReplayAsync(Selected());
        }

        /// <summary>
        /// Downloads the chosen match and opens the game on it, through the same helper the
        /// Matches screen uses.
        ///
        /// Guarded by _replaying as well as by disabling the button: the download is tens of
        /// megabytes, and without the flag an impatient second press between the click and the
        /// button actually disabling would start a second download into the same file.
        /// </summary>
        private async System.Threading.Tasks.Task ReplayAsync(PlayerMatch match)
        {
            if (match == null || _replaying) return;

            if (!match.CanReplay) return;   // the button is disabled for these; UpdateReplayButton says why

            _replaying = true;
            UpdateReplayButton();
            try
            {
                _replayStatus.ForeColor = UiTheme.Muted;
                var result = await ReplayLauncher.ReplayAsync(
                    _accounts, _appContext, match.Id, match.FileName, this,
                    msg => { if (!_replayStatus.IsDisposed) _replayStatus.Text = msg; });

                if (IsDisposed || _replayStatus.IsDisposed) return;
                _replayStatus.ForeColor = result.Launched ? UiTheme.AccentFolder : UiTheme.Muted;
                _replayStatus.Text = result.Downloaded && !result.Launched
                    ? result.Message + "  Mở game → Replays để xem lại."
                    : result.Message;
            }
            finally
            {
                _replaying = false;
                UpdateReplayButton();
            }
        }

        /// <summary>Server timestamps are UTC; shown local, because that is when the player played.</summary>
        private static string FormatWhen(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "—";
            return DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                                     System.Globalization.DateTimeStyles.AssumeUniversal
                                     | System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime utc)
                ? utc.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
                : raw;
        }

        private sealed class MatchRow
        {
            public object When { get; set; }
            public object Result { get; set; }
            public object Score { get; set; }
            public object Room { get; set; }
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
