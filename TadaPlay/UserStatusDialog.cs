using System;
using System.Drawing;
using System.Windows.Forms;
using TadaPlay.Common.Models;
using TadaPlay.Connections;

namespace TadaPlay
{
    /// <summary>
    /// Shows what one player is doing right now: whether they are in a game, and whether that
    /// game can be watched yet.
    ///
    /// Opened by clicking a player in the online list, so it answers the question that list
    /// cannot - "is this person playing?" - without making anyone open the spectate picker and
    /// read a row. The answer comes from that player's own TadaPlay share port, never from the
    /// game's spectator port: the game announces spectators to the host in chat, so probing
    /// that would fire a false "x.x.x.x is spectating" notice into their match every time
    /// somebody clicked their name.
    ///
    /// It re-checks while it is open, because the interesting states change on their own - a
    /// match that has just started becomes watchable a capture interval later, and a player
    /// who quits stops being watchable at all.
    /// </summary>
    public class UserStatusDialog : Form
    {
        private static readonly Color PageBg = Color.FromArgb(245, 245, 245);   // antd gray-3
        private static readonly Color LiveColor = Color.FromArgb(82, 196, 26);  // antd green-6
        private static readonly Color WaitColor = Color.FromArgb(250, 173, 20); // antd gold-6
        private static readonly Color IdleColor = Color.FromArgb(140, 140, 140);// antd gray-7

        /// <summary>
        /// How often the peer is re-asked while this dialog is open.
        ///
        /// Deliberately brisk: the states worth watching change on their own - a countdown
        /// running down, a match becoming watchable, a player quitting - and the whole point
        /// of this dialog is to sit and watch for that. One probe is a few hundred bytes and
        /// the previous one is never left running (see _probing), so a peer is asked at most
        /// once at a time regardless of how slow they are to answer.
        /// </summary>
        private const int RefreshMs = 3000;

        private readonly User _user;
        private readonly bool _isSelf;

        private readonly Label _state = new();
        private readonly Label _detail = new();
        private readonly Button _watchButton = new();
        private readonly System.Windows.Forms.Timer _refresh = new() { Interval = RefreshMs };
        private bool _probing;

        public UserStatusDialog(User user, bool isSelf)
        {
            _user = user;
            _isSelf = isSelf;

            Text = "Trạng thái người chơi";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(420, 250);
            Font = new Font("Segoe UI", 9.75F);
            BackColor = PageBg;

            var name = new Label
            {
                Text = user.Username ?? "",
                Location = new Point(20, 18),
                Size = new Size(380, 30),
                Font = new Font("Segoe UI Semibold", 13F, FontStyle.Bold)
            };

            var address = new Label
            {
                Text = string.IsNullOrWhiteSpace(user.IpAddress) ? "Chưa có địa chỉ VPN" : user.IpAddress,
                Location = new Point(20, 48),
                Size = new Size(380, 22),
                ForeColor = IdleColor
            };

            _state.Location = new Point(20, 88);
            _state.Size = new Size(380, 30);
            _state.Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold);
            _state.ForeColor = IdleColor;
            _state.Text = "Đang kiểm tra...";

            _detail.Location = new Point(20, 118);
            _detail.Size = new Size(380, 60);
            _detail.ForeColor = IdleColor;

            _watchButton.Text = "Xem trận";
            _watchButton.Location = new Point(196, 190);
            _watchButton.Size = new Size(100, 40);
            _watchButton.BackColor = LiveColor;
            _watchButton.ForeColor = Color.White;
            _watchButton.FlatStyle = FlatStyle.Flat;
            _watchButton.FlatAppearance.BorderSize = 0;
            _watchButton.Enabled = false;
            _watchButton.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };

            var close = new Button
            {
                Text = "Đóng",
                Location = new Point(304, 190),
                Size = new Size(100, 40),
                FlatStyle = FlatStyle.Flat,
                DialogResult = DialogResult.Cancel
            };

            Controls.Add(name);
            Controls.Add(address);
            Controls.Add(_state);
            Controls.Add(_detail);
            Controls.Add(_watchButton);
            Controls.Add(close);
            CancelButton = close;

            _refresh.Tick += (s, e) => Probe();
            _refresh.Start();
            Probe();
        }

        private async void Probe()
        {
            if (_probing || IsDisposed) return;
            if (string.IsNullOrWhiteSpace(_user.IpAddress))
            {
                Show("KHÔNG KIỂM TRA ĐƯỢC", IdleColor,
                     "Người chơi này chưa có địa chỉ VPN nên không thể hỏi trạng thái.", false);
                _refresh.Stop();
                return;
            }

            _probing = true;
            try
            {
                LiveShareClient.HostStatus status = await LiveShareClient.TryGetStatusAsync(_user.IpAddress);
                if (IsDisposed) return;
                Render(status);
            }
            finally
            {
                _probing = false;
            }
        }

        private void Render(LiveShareClient.HostStatus status)
        {
            if (status == null)
            {
                Show("KHÔNG CHƠI", IdleColor,
                     "Không hỏi được TadaPlay của người này - họ chưa mở TadaPlay, hoặc chưa vào VPN.",
                     false);
                return;
            }

            if (status.InGame && !status.HasMatch)
            {
                // In a game, but nothing has been captured yet. That is a countdown, not a
                // failure, so it says how long rather than "không xem được".
                Show("ĐANG CHƠI", WaitColor,
                     $"Đang trong trận nhưng chưa xem được. Chờ khoảng {Math.Max(1, status.WaitSeconds)} " +
                     "giây nữa rồi mở lại.", false);
                return;
            }

            if (status.InGame)
            {
                // The match clock comes from the record's own sync increments, so it is the
                // real in-game time, not how long ago the match started.
                Show("● ĐANG CHƠI", LiveColor,
                     $"Trận đang ở phút {Clock(status.GameTime)} - xem được ngay " +
                     $"({status.Bytes / 1024} KB). Người xem chậm hơn người chơi khoảng 10-20 giây.",
                     !_isSelf);
                return;
            }

            if (status.HasMatch)
            {
                // Quit the game, so the match is over. Deliberately not watchable: this is for
                // watching along, not for replaying old games.
                TimeSpan age = status.Age ?? TimeSpan.Zero;
                string when = age.TotalMinutes < 90
                    ? $"khoảng {Math.Max(1, (int)age.TotalMinutes)} phút trước"
                    : $"lúc {status.FinishedUtc?.ToLocalTime():dd/MM HH:mm}";
                Show("KHÔNG CHƠI", IdleColor,
                     $"Đã thoát game - trận kết thúc {when}. Trận đã kết thúc thì không xem được.",
                     false);
                return;
            }

            Show("KHÔNG CHƠI", IdleColor, "Người này đang online nhưng không ở trong trận nào.", false);
        }

        /// <summary>Game time as mm:ss, or h:mm:ss once a match runs past an hour.</summary>
        private static string Clock(TimeSpan time) =>
            time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
                                 : $"{time.Minutes:00}:{time.Seconds:00}";

        private void Show(string state, Color color, string detail, bool watchable)
        {
            _state.Text = state;
            _state.ForeColor = color;
            _detail.Text = _isSelf ? detail + "\r\n(Đây là bạn.)" : detail;
            _watchButton.Enabled = watchable;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _refresh.Stop();
                _refresh.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
