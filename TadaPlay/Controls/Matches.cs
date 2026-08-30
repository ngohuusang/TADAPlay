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
        private readonly Button _renameButton;

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
                GridLines = false,
                MultiSelect = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Font = new Font("Segoe UI", 10F),
                BorderStyle = BorderStyle.FixedSingle
            };
            // Taller, more breathable rows - ListView row height tracks the SmallImageList's
            // item size, so a blank placeholder image is the usual way to bump it (no real icons).
            var rowSizer = new ImageList { ImageSize = new Size(1, 32) };
            rowSizer.Images.Add(new Bitmap(1, 32));
            _listView.SmallImageList = rowSizer;
            _listView.Columns.Add("Thời gian", 120, HorizontalAlignment.Left);
            _listView.Columns.Add("Tên trận", 150, HorizontalAlignment.Left);
            _listView.Columns.Add("Đội 1", 180, HorizontalAlignment.Left);
            _listView.Columns.Add("Đội 2", 180, HorizontalAlignment.Left);
            _listView.Columns.Add("Kết quả", 100, HorizontalAlignment.Left);
            _listView.Columns.Add("MVP", 120, HorizontalAlignment.Left);
            _listView.Columns.Add("Người tải lên", 130, HorizontalAlignment.Left);
            _listView.SelectedIndexChanged += (s, e) => UpdateReplayButton();
            _listView.DoubleClick += (s, e) => ShowScoreboard();
            // Stretch the MVP column to fill any leftover width so row backgrounds (zebra
            // stripes) span the full list instead of leaving a blank gap on the right.
            _listView.Resize += (s, e) => UiUtils.StretchLastListViewColumn(_listView, 130);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(12, 6, 12, 6) };
            _replayButton = new Button { Text = "Phát lại", Dock = DockStyle.Right, Width = 120, Enabled = false };
            _replayButton.Click += async (s, e) => await ReplaySelectedAsync();
            var detailsButton = new Button { Text = "Chi tiết / Điểm", Dock = DockStyle.Right, Width = 130 };
            detailsButton.Click += (s, e) => ShowScoreboard();
            _renameButton = new Button { Text = "Đổi tên", Dock = DockStyle.Right, Width = 100, Enabled = false };
            _renameButton.Click += async (s, e) => await RenameSelectedAsync();
            footer.Controls.Add(_replayButton);
            footer.Controls.Add(detailsButton);
            footer.Controls.Add(_renameButton);

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
            MatchSummary selected = _listView.SelectedItems.Count > 0
                ? _listView.SelectedItems[0].Tag as MatchSummary
                : null;

            _replayButton.Enabled = selected != null && selected.CanReplay;
            // Offered only where the server said it would be allowed - the uploader's own
            // matches, or anything at all for an admin. It still checks again when asked.
            _renameButton.Enabled = selected != null && selected.CanRename;
        }

        /// <summary>
        /// Renames the selected match. Only reachable when the server marked it renameable,
        /// but a refusal is still reported rather than assumed impossible: rights can change
        /// between loading the list and pressing the button.
        /// </summary>
        private async System.Threading.Tasks.Task RenameSelectedAsync()
        {
            if (_listView.SelectedItems.Count == 0 ||
                _listView.SelectedItems[0].Tag is not MatchSummary match)
            {
                return;
            }

            string current = string.IsNullOrWhiteSpace(match.RoomName) ? "" : match.RoomName;
            string name = UiUtils.PromptForText(FindForm(), "Đổi tên trận đấu",
                                                "Tên mới cho trận đấu:", current, 255);
            if (name == null) return;                       // cancelled

            name = name.Trim();
            if (name.Length == 0)
            {
                UiUtils.ShowAntdModal(FindForm(), "Tên không hợp lệ",
                    "Tên trận đấu không được để trống.", AntdUI.TType.Warn);
                return;
            }
            if (string.Equals(name, current, StringComparison.Ordinal)) return;

            _renameButton.Enabled = false;
            try
            {
                await _accountService.RenameMatchAsync(match.Id, name);
                match.RoomName = name;
                // Update the row in place rather than reloading: a full refresh would lose the
                // selection and scroll position for a one-cell change.
                _listView.SelectedItems[0].SubItems[1].Text = name;
                _listView.SelectedItems[0].SubItems[1].ForeColor = _listView.ForeColor;
                _statusLabel.ForeColor = Color.Gray;
                _statusLabel.Text = $"Đã đổi tên trận đấu thành \"{name}\".";
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"Matches: rename failed: {ex.Message}");
                UiUtils.ShowAntdModal(FindForm(), "Không đổi được tên", ex.Message, AntdUI.TType.Error);
            }
            finally
            {
                UpdateReplayButton();
            }
        }

        // Names for a team: prefer matched account usernames; fall back to the parsed replay's
        // in-game names grouped by team (so the team lists show up even before the record is
        // matched to accounts).
        private static string TeamText(MatchSummary m, int team)
        {
            if (m.Teams != null && m.Teams.TryGetValue(team.ToString(), out var members) && members != null && members.Length > 0)
            {
                return string.Join(", ", members);
            }
            if (m.Players != null)
            {
                var names = m.Players.Where(p => p.Team == team && !string.IsNullOrWhiteSpace(p.Name))
                                     .Select(p => p.Name);
                string joined = string.Join(", ", names);
                if (!string.IsNullOrEmpty(joined)) return joined;
            }
            return "—";
        }

        // The winning team (1 or 2): prefer the host-reported result, else the parser's guess
        // carried on each player's winner flag. Null when neither is known.
        private static int? EffectiveWinnerTeam(MatchSummary m)
        {
            if (m.WinnerTeam == 1 || m.WinnerTeam == 2) return m.WinnerTeam;
            var guessed = m.Players?.FirstOrDefault(p => p.Winner == true && p.Team.HasValue)?.Team;
            return (guessed == 1 || guessed == 2) ? guessed : (int?)null;
        }

        private static string ResultText(MatchSummary m)
        {
            int? w = EffectiveWinnerTeam(m);
            if (w == 1) return "Đội 1 thắng";
            if (w == 2) return "Đội 2 thắng";
            return "Chưa xác định";
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
                for (int i = 0; i < matches.Count; i++)
                {
                    var m = matches[i];
                    var item = new ListViewItem(FormatTime(m.FinishedAt))
                    {
                        UseItemStyleForSubItems = false,
                        // Zebra striping so long lists stay readable without gridlines.
                        BackColor = i % 2 == 0 ? Color.White : UiColors.ZebraStripe
                    };
                    var nameSubItem = item.SubItems.Add(
                        string.IsNullOrWhiteSpace(m.RoomName) ? "—" : m.RoomName);
                    if (string.IsNullOrWhiteSpace(m.RoomName)) nameSubItem.ForeColor = Color.Gray;
                    item.SubItems.Add(TeamText(m, 1));
                    item.SubItems.Add(TeamText(m, 2));

                    bool hasWinner = EffectiveWinnerTeam(m) != null;
                    var resultSubItem = item.SubItems.Add(ResultText(m));
                    resultSubItem.ForeColor = hasWinner ? UiColors.Winner : Color.Gray;
                    resultSubItem.Font = new Font(_listView.Font, hasWinner ? FontStyle.Bold : FontStyle.Regular);

                    // Prefer the winning team's MVP; fall back to the overall game MVP.
                    string headlineMvp = !string.IsNullOrEmpty(m.WinnerMvp) ? m.WinnerMvp : m.Mvp;
                    var mvpSubItem = item.SubItems.Add(string.IsNullOrEmpty(headlineMvp) ? "—" : "⭐ " + headlineMvp);
                    if (!string.IsNullOrEmpty(headlineMvp)) mvpSubItem.ForeColor = UiColors.Mvp;

                    var uploaderSubItem = item.SubItems.Add(
                        string.IsNullOrWhiteSpace(m.UploadedBy) ? "—" : m.UploadedBy);
                    if (string.IsNullOrWhiteSpace(m.UploadedBy)) uploaderSubItem.ForeColor = Color.Gray;

                    item.Tag = m;
                    _listView.Items.Add(item);
                }
                _listView.EndUpdate();
                UiUtils.StretchLastListViewColumn(_listView, 130);

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
                UiUtils.ShowAntdModal(FindForm(), "Chi tiết",
                    "Trận này chưa có dữ liệu điểm số (record chưa được phân tích).", AntdUI.TType.Info);
                return;
            }

            AntdUI.Modal.open(FindForm(), "Bảng điểm trận đấu", BuildScoreboardDialog(match));
        }

        private static Panel BuildScoreboardDialog(MatchSummary match)
        {
            const int DialogWidth = 620;
            var root = new Panel { Width = DialogWidth, AutoSize = false };

            // --- Header: room name + finished time (topmost - added first) ---
            var header = new Panel { Dock = DockStyle.Top, Height = 56, Padding = new Padding(0, 0, 0, 8) };
            var roomLabel = new Label
            {
                Text = string.IsNullOrWhiteSpace(match.RoomName) ? "Trận đấu" : match.RoomName,
                Dock = DockStyle.Top,
                Height = 30,
                Font = new Font("Segoe UI Semibold", 14F, FontStyle.Bold)
            };
            var timeLabel = new Label
            {
                Text = FormatTime(match.FinishedAt),
                Dock = DockStyle.Top,
                Height = 20,
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 9.5F)
            };
            header.Controls.Add(roomLabel);
            header.Controls.Add(timeLabel);

            // --- Result banner ---
            bool hasWinner = EffectiveWinnerTeam(match) != null;
            var resultBanner = new Label
            {
                Text = (hasWinner ? "🏆 " : "") + ResultText(match),
                Dock = DockStyle.Top,
                Height = 36,
                Font = new Font("Segoe UI Semibold", 11.5F, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = hasWinner ? UiColors.Winner : UiColors.Loser,
                TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(0, 0, 0, 10)
            };

            // --- MVP line(s) ---
            string mvpText = BuildMvpText(match);
            Label mvpLabel = null;
            if (mvpText != null)
            {
                mvpLabel = new Label
                {
                    Text = mvpText,
                    Dock = DockStyle.Top,
                    Height = 26,
                    Font = new Font("Segoe UI", 10F),
                    ForeColor = UiColors.Mvp,
                    TextAlign = ContentAlignment.MiddleCenter
                };
            }

            var team1Panel = BuildTeamPanel(match, 1);
            var team2Panel = BuildTeamPanel(match, 2);

            var body = new Panel { Dock = DockStyle.Top, AutoSize = false, Padding = new Padding(0, 8, 0, 0) };
            body.Controls.Add(team2Panel);
            body.Controls.Add(team1Panel);
            body.Height = team1Panel.Height + team2Panel.Height + body.Padding.Top;

            root.Controls.Add(body);
            if (mvpLabel != null) root.Controls.Add(mvpLabel);
            root.Controls.Add(resultBanner);
            root.Controls.Add(header);

            // AntdUI.Modal.open reads Height at call time (not via AutoSize/layout events), so
            // sum it explicitly - same reason Setting.cs avoids AutoSize for its own dialog.
            root.Height = header.Height + resultBanner.Height + resultBanner.Margin.Vertical
                + (mvpLabel?.Height ?? 0) + body.Height;

            return root;
        }

        private static string BuildMvpText(MatchSummary match)
        {
            if (!string.IsNullOrEmpty(match.WinnerMvp) || !string.IsNullOrEmpty(match.LoserMvp))
            {
                var parts = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrEmpty(match.WinnerMvp)) parts.Add($"⭐ MVP đội thắng: {match.WinnerMvp}");
                if (!string.IsNullOrEmpty(match.LoserMvp)) parts.Add($"⭐ MVP đội thua: {match.LoserMvp}");
                return string.Join("      ", parts);
            }
            if (!string.IsNullOrEmpty(match.Mvp))
            {
                return $"⭐ MVP: {match.Mvp}";
            }
            return null;
        }

        // One team's card: a tinted header row (name + win/loss tag) above a small table of
        // players (name / score / eapm / MVP star), sorted by score. Explicit heights throughout
        // so the whole dialog's total height can be summed reliably (see BuildScoreboardDialog).
        private static Panel BuildTeamPanel(MatchSummary match, int team)
        {
            int? winnerTeam = EffectiveWinnerTeam(match);
            bool teamWon = winnerTeam == team;
            bool resultKnown = winnerTeam == 1 || winnerTeam == 2;
            Color accent = !resultKnown ? Color.FromArgb(217, 217, 217) : (teamWon ? UiColors.Winner : UiColors.Loser);
            Color tint = !resultKnown ? Color.FromArgb(250, 250, 250) : (teamWon ? UiColors.WinnerTint : UiColors.LoserTint);

            var players = match.Players.Where(p => p.Team == team).OrderByDescending(p => p.Score ?? 0).ToList();
            const int RowHeight = 30;
            const int HeaderHeight = 34;
            int cardHeight = HeaderHeight + Math.Max(players.Count, 1) * RowHeight;

            var card = new Panel
            {
                Dock = DockStyle.Top,
                Height = cardHeight + 12,
                Padding = new Padding(0, 0, 0, 12)
            };

            var inner = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = tint,
                Padding = new Padding(1)
            };

            var teamHeader = new Panel { Dock = DockStyle.Top, Height = HeaderHeight, BackColor = accent };
            var teamNameLabel = new Label
            {
                Text = $"Đội {team}",
                Dock = DockStyle.Left,
                Width = 200,
                Padding = new Padding(10, 0, 0, 0),
                Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold),
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleLeft
            };
            var tagLabel = new Label
            {
                Text = resultKnown ? (teamWon ? "THẮNG" : "THUA") : "",
                Dock = DockStyle.Right,
                Width = 90,
                Padding = new Padding(0, 0, 10, 0),
                Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold),
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleRight
            };
            teamHeader.Controls.Add(tagLabel);
            teamHeader.Controls.Add(teamNameLabel);

            var rowsPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = Math.Max(players.Count, 1),
                BackColor = tint
            };
            rowsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
            rowsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
            rowsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
            rowsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 10));

            if (players.Count == 0)
            {
                rowsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, RowHeight));
                rowsPanel.Controls.Add(new Label
                {
                    Text = "(không có người chơi)",
                    Dock = DockStyle.Fill,
                    ForeColor = Color.Gray,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(10, 0, 0, 0)
                }, 0, 0);
                rowsPanel.SetColumnSpan(rowsPanel.Controls[0], 4);
            }
            else
            {
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    rowsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, RowHeight));

                    rowsPanel.Controls.Add(new Label
                    {
                        Text = p.Name,
                        Dock = DockStyle.Fill,
                        Padding = new Padding(10, 0, 0, 0),
                        Font = new Font("Segoe UI", 9.5F, p.Mvp ? FontStyle.Bold : FontStyle.Regular),
                        TextAlign = ContentAlignment.MiddleLeft,
                        AutoEllipsis = true
                    }, 0, i);
                    rowsPanel.Controls.Add(new Label
                    {
                        Text = $"Điểm: {p.Score ?? 0}",
                        Dock = DockStyle.Fill,
                        Font = new Font("Segoe UI", 9.5F),
                        ForeColor = Color.DimGray,
                        TextAlign = ContentAlignment.MiddleLeft
                    }, 1, i);
                    rowsPanel.Controls.Add(new Label
                    {
                        Text = $"EAPM: {p.Eapm ?? 0}",
                        Dock = DockStyle.Fill,
                        Font = new Font("Segoe UI", 9.5F),
                        ForeColor = Color.DimGray,
                        TextAlign = ContentAlignment.MiddleLeft
                    }, 2, i);
                    rowsPanel.Controls.Add(new Label
                    {
                        Text = p.Mvp ? "⭐" : "",
                        Dock = DockStyle.Fill,
                        Font = new Font("Segoe UI", 11F),
                        ForeColor = UiColors.Mvp,
                        TextAlign = ContentAlignment.MiddleCenter
                    }, 3, i);
                }
            }

            inner.Controls.Add(rowsPanel);
            inner.Controls.Add(teamHeader);
            card.Controls.Add(inner);
            return card;
        }

        private async System.Threading.Tasks.Task ReplaySelectedAsync()
        {
            if (_listView.SelectedItems.Count == 0 || _listView.SelectedItems[0].Tag is not MatchSummary match)
            {
                return;
            }

            // The game can only browse/play records from its own SaveGame folder - a file
            // dropped elsewhere won't show up even when launched directly. Prefix the file name
            // so the auto-upload watcher (Home.RecordWatchTimer_Tick) can recognize and skip
            // this download instead of treating it as a new match to upload.
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

            string baseFileName = string.IsNullOrWhiteSpace(match.FileName)
                ? $"match_{match.Id}.mgz"
                : Path.GetFileName(match.FileName);
            string fileName = RecordedGameFinder.ReplayPrefix + baseFileName;
            string destPath = Path.Combine(saveDir, fileName);

            _replayButton.Enabled = false;
            _statusLabel.ForeColor = Color.Gray;
            _statusLabel.Text = $"Đang tải record của trận #{match.Id}...";
            try
            {
                await _accountService.DownloadRecordAsync(match.Id, destPath);

                // Launch the game via the WK launcher matching the display mode configured in
                // Settings, passing the downloaded record as its argument (same as double-clicking
                // a .mgz/.mgx file) so it opens straight into the replay - no folder window, no
                // extra dialog. Use a dedicated LongRunning thread rather than Task.Run/the
                // shared ThreadPool - the WebSocket ping timer's callback is also
                // ThreadPool-scheduled, and this synchronous blocking I/O (copying the ~3MB
                // launcher exe, antivirus scanning it, ShellExecute reputation checks) was
                // starving it long enough to spike reported ping.
                var (status, launchMessage, _, _) = await System.Threading.Tasks.Task.Factory.StartNew(() =>
                {
                    string exePath = GameExecutablePreparer.PrepareAndGetExePath(_appContext.GetGameFolder(), _appContext.GetGameLaunchMode());
                    return GameLauncher.Launch(exePath, destPath);
                }, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning, System.Threading.Tasks.TaskScheduler.Default);
                _statusLabel.ForeColor = status == GameLauncher.LaunchStatus.Success ? Color.DarkGreen : Color.DarkOrange;
                _statusLabel.Text = launchMessage;

                if (status != GameLauncher.LaunchStatus.Success)
                {
                    MessageBox.Show(
                        $"Đã tải record vào thư mục lưu của AoE2 DE, nhưng không thể tự mở game ({launchMessage})\n\n" +
                        "Mở Age of Empires II: DE → mục \"Replays\" (Xem lại) để phát lại trận đấu.",
                        "Phát lại", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
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
