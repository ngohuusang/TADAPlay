using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using TadaPlay.Common.Models;
using TadaPlay.Connections;

namespace TadaPlay
{
    /// <summary>
    /// Lets the player pick whose match to watch.
    ///
    /// Each online player is asked what their match is doing. That question goes to
    /// TadaPlay's own share port, never to the game's spectator port - the game announces
    /// spectators to the host in chat ("!!! Notice - x.x.x.x is spectating."), so probing
    /// that port would spray false notices across everyone's match every time this dialog
    /// opened.
    ///
    /// ONLY a match being played right now can be picked. The viewer gets the host's most
    /// recent capture and TadaPlay then keeps appending to it (see LiveStreamSession), so
    /// the replay follows the match instead of stopping where the download did, always a
    /// capture interval behind - which is what keeps this from being a ghosting tool.
    ///
    /// A host who has quit the game is finished, and finished matches are not on offer here:
    /// this button is for watching along, not for browsing replays. The host keeps serving a
    /// finished match anyway, because a viewer already watching needs those last bytes to see
    /// the match out - it just cannot be the starting point for a new viewer.
    /// </summary>
    public class SpectatePickerDialog : Form
    {
        private static readonly Color PageBg = Color.FromArgb(245, 245, 245);   // antd gray-3
        private static readonly Color LiveColor = Color.FromArgb(82, 196, 26);  // antd green-6
        private static readonly Color HintColor = Color.FromArgb(140, 140, 140);// antd gray-7

        /// <summary>VPN address of the chosen host; null when cancelled.</summary>
        public string SelectedHostIp { get; private set; }

        /// <summary>Display name of the chosen host, for logs and the downloaded file name.</summary>
        public string SelectedHostLabel { get; private set; }

        /// <summary>
        /// How often every listed player is re-asked. Matches the single-player status dialog,
        /// but a round here is one request PER player rather than one, so it is deliberately
        /// self-limiting: <see cref="_refreshing"/> means a tick that finds the previous round
        /// still running is skipped rather than stacking a second one. With the 4s probe
        /// timeout that puts the floor at roughly one round every 4 seconds however many
        /// players are online.
        /// </summary>
        private const int RefreshMs = 3000;

        private readonly ListBox _list = new();
        private readonly Button _watchButton = new();
        private readonly List<Row> _rows = new();
        private readonly System.Windows.Forms.Timer _refresh = new() { Interval = RefreshMs };
        private readonly List<User> _candidates = new();
        private Label _hint;
        private bool _refreshing;

        /// <summary>
        /// One player in the list. <see cref="Watchable"/> is false for everyone who is not
        /// in a game right now, and those rows cannot be picked - they are still listed so
        /// the reason nobody can be watched is visible rather than an empty dialog.
        /// </summary>
        private sealed class Row
        {
            public string Label;
            public string Ip;
            public string Name;
            public bool Watchable;
        }

        public SpectatePickerDialog(IReadOnlyList<User> onlineUsers, string currentUsername)
        {
            Text = "Xem trận đấu";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(460, 380);
            Font = new Font("Segoe UI", 9.75F);
            BackColor = PageBg;

            var prompt = new Label
            {
                Text = "Chọn người chơi để xem trận đấu của họ",
                Dock = DockStyle.Top,
                Height = 38,
                Padding = new Padding(16, 12, 16, 0),
                Font = new Font("Segoe UI Semibold", 11.5F, FontStyle.Bold)
            };

            _list.Location = new Point(16, 48);
            _list.Size = new Size(428, 240);
            _list.Font = new Font("Segoe UI", 10.5F);
            _list.IntegralHeight = false;
            _list.DoubleClick += (s, e) => Confirm();
            _list.SelectedIndexChanged += (s, e) => _watchButton.Enabled = IsWatchable(_list.SelectedIndex);

            var hint = new Label
            {
                Location = new Point(16, 292),
                Size = new Size(428, 24),
                ForeColor = HintColor,
                Text = "Chỉ xem được người đang chơi, chậm hơn khoảng 90 giây."
            };

            _watchButton.Text = "Xem trận";
            _watchButton.Location = new Point(236, 320);
            _watchButton.Size = new Size(100, 40);
            _watchButton.BackColor = LiveColor;
            _watchButton.ForeColor = Color.White;
            _watchButton.FlatStyle = FlatStyle.Flat;
            _watchButton.FlatAppearance.BorderSize = 0;
            _watchButton.Enabled = false;
            _watchButton.Click += (s, e) => Confirm();

            var cancel = new Button
            {
                Text = "Đóng",
                Location = new Point(344, 320),
                Size = new Size(100, 40),
                FlatStyle = FlatStyle.Flat,
                DialogResult = DialogResult.Cancel
            };

            Controls.Add(_list);
            Controls.Add(hint);
            Controls.Add(_watchButton);
            Controls.Add(cancel);
            Controls.Add(prompt);
            AcceptButton = _watchButton;
            CancelButton = cancel;

            _hint = hint;
            Populate(onlineUsers, currentUsername);
        }

        private void Populate(IReadOnlyList<User> onlineUsers, string currentUsername)
        {
            _candidates.AddRange((onlineUsers ?? Array.Empty<User>())
                .Where(u => !string.IsNullOrWhiteSpace(u.IpAddress))
                .Where(u => !string.Equals(u.Username, currentUsername, StringComparison.OrdinalIgnoreCase)));

            if (_candidates.Count == 0)
            {
                _hint.Text = "Không có người chơi nào đang online có địa chỉ VPN.";
                return;
            }

            foreach (User user in _candidates)
            {
                _rows.Add(new Row
                {
                    Label = $"{user.Username} · {user.IpAddress}   (đang kiểm tra...)",
                    Ip = user.IpAddress,
                    Name = user.Username
                });
            }
            Redraw();

            // Players start and finish games while this dialog is open, so keep asking rather
            // than showing whatever was true the moment it was opened.
            _refresh.Tick += (s, e) => RefreshStatuses();
            _refresh.Start();
            RefreshStatuses();
        }

        private async void RefreshStatuses()
        {
            // A round is one request per player; letting rounds overlap would multiply that
            // by however many are in flight the moment a slow peer holds one up.
            if (_refreshing || IsDisposed || _candidates.Count == 0) return;
            _refreshing = true;
            try
            {
                await RefreshRoundAsync();
            }
            finally
            {
                _refreshing = false;
            }
        }

        private async System.Threading.Tasks.Task RefreshRoundAsync()
        {
            // Ask everyone at once; a peer with TadaPlay closed just times out. Rows keep their
            // previous text until their own probe answers, so a refresh never flashes the whole
            // list back to "đang kiểm tra...".
            var probes = _candidates
                .Select(u => (User: u, Task: LiveShareClient.TryGetStatusAsync(u.IpAddress)))
                .ToList();

            int live = 0;
            for (int i = 0; i < probes.Count; i++)
            {
                LiveShareClient.HostStatus status = await probes[i].Task;
                if (IsDisposed) return;

                User user = probes[i].User;
                string note;
                bool watchable = false;
                if (status == null)
                {
                    note = "TadaPlay không chạy";
                }
                else if (status.InGame && !status.HasMatch)
                {
                    // A match has started but has not been captured yet - a countdown, not a
                    // failure, so say how long rather than "nothing to watch".
                    note = $"đã bắt đầu game · chờ {Math.Max(1, status.WaitSeconds)}s để xem";
                }
                else if (status.InGame)
                {
                    live++;
                    watchable = true;
                    // The match clock, read out of the record itself - it tells a viewer how
                    // far in they would be joining, which "xem được" alone does not.
                    note = $"● ĐANG CHƠI · phút {Clock(status.GameTime)}";
                }
                else if (status.HasMatch)
                {
                    // The host has quit, so the match is over. Say when, but do not offer it:
                    // this is for watching along, not for replaying old games.
                    TimeSpan age = status.Age ?? TimeSpan.Zero;
                    note = age.TotalMinutes < 90
                        ? $"đã thoát game · trận kết thúc {Math.Max(1, (int)age.TotalMinutes)} phút trước"
                        : $"đã thoát game · trận lúc {status.FinishedUtc?.ToLocalTime():dd/MM HH:mm}";
                }
                else
                {
                    note = "chưa vào trận";
                }

                _rows[i] = new Row
                {
                    Label = $"{user.Username} · {user.IpAddress}   {note}",
                    Ip = user.IpAddress,
                    Name = user.Username,
                    Watchable = watchable
                };
                Redraw();
            }

            _hint.Text = live > 0
                ? $"{live} người đang chơi. Trận phát lại từ đầu và tự cập nhật theo họ."
                : "Hiện chưa có ai đang chơi để xem.";
            _hint.ForeColor = live > 0 ? LiveColor : HintColor;

            // Re-evaluated every round, not just the first: a row the player selected while it
            // was still "đang kiểm tra..." - or one that was watchable until the host quit a
            // moment ago - must not leave the button armed on a match that cannot be watched.
            _watchButton.Enabled = IsWatchable(_list.SelectedIndex);
        }

        /// <summary>Game time as mm:ss, or h:mm:ss once a match runs past an hour.</summary>
        private static string Clock(TimeSpan time) =>
            time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
                                 : $"{time.Minutes:00}:{time.Seconds:00}";

        private bool IsWatchable(int index) =>
            index >= 0 && index < _rows.Count && _rows[index].Watchable;

        private void Redraw()
        {
            int selected = _list.SelectedIndex;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (Row row in _rows) _list.Items.Add(row.Label);
            if (selected >= 0 && selected < _list.Items.Count) _list.SelectedIndex = selected;
            _list.EndUpdate();
        }

        private void Confirm()
        {
            // Guards the double-click and Enter paths too, not just the button.
            if (!IsWatchable(_list.SelectedIndex)) return;
            SelectedHostIp = _rows[_list.SelectedIndex].Ip;
            SelectedHostLabel = _rows[_list.SelectedIndex].Name;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Otherwise the round keeps probing every player after the dialog is gone -
                // and this dialog is opened and closed repeatedly, so they would accumulate.
                _refresh.Stop();
                _refresh.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
