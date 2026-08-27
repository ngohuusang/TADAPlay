using System;
using System.Drawing;
using System.Windows.Forms;
using TadaPlay.Common.Models;
using AntdUI;
using TadaPlay.Connections;
using TadaPlay.Logger;
using TadaPlay.Controls;

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

        /// <summary>
        /// Finds the CURRENT record for this player by name. Every user_list broadcast
        /// replaces the whole list with new instances rather than mutating the existing ones,
        /// so the User captured when this dialog opened is a snapshot that never moves.
        /// </summary>
        private readonly Func<string, User> _lookup;

        private readonly AntdUI.Tag _state = new();
        private readonly AntdUI.Label _detail = new();
        private readonly AntdUI.Button _watchButton = new();
        private readonly AntdUI.Button _pingButton = new();
        private readonly AntdUI.Label _pingResult = new();

        /// <summary>
        /// Set while a measurement is running so the button can stop it. Thirty seconds is long
        /// enough that "wait for it to finish or close the dialog" is not an acceptable answer.
        /// </summary>
        private System.Threading.CancellationTokenSource _pingCancel;
        private readonly System.Windows.Forms.Timer _refresh = new() { Interval = RefreshMs };
        private bool _probing;

        public UserStatusDialog(User user, bool isSelf, Func<string, User> lookup = null)
        {
            _user = user;
            _isSelf = isSelf;
            _lookup = lookup;

            Text = "Trạng thái người chơi";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(440, 306);
            Font = new Font("Segoe UI", 9.75F);
            BackColor = UiTheme.PageBg;

            // A card on the page background, like the settings screen - the dialog is one
            // block of information about one player, and the card is what says so.
            var card = new AntdUI.Panel
            {
                Location = new Point(14, 14),
                Size = new Size(412, 172),
                Radius = 10,
                Shadow = 8,
                ShadowOpacity = 0.10F,
                Back = Color.White,
                BorderWidth = 1,
                BorderColor = UiTheme.CardBorder,
            };

            var name = new AntdUI.Label
            {
                Text = user.Username ?? "",
                Location = new Point(18, 14),
                Size = new Size(376, 32),
                PrefixSvg = UiTheme.IconAccount,
                PrefixColor = UiTheme.AccentDisplay,
                IconGap = 10,
                ForeColor = UiTheme.Ink,
                Font = new Font("Segoe UI Semibold", 13F, FontStyle.Bold),
                BackColor = Color.Transparent,
            };

            var address = new AntdUI.Label
            {
                Text = string.IsNullOrWhiteSpace(user.IpAddress) ? "Chưa có địa chỉ VPN" : user.IpAddress,
                Location = new Point(18, 46),
                Size = new Size(376, 22),
                ForeColor = UiTheme.Muted,
                Font = new Font("Segoe UI", 9.5F),
                BackColor = Color.Transparent,
            };

            // A tag, not a coloured line of shouty text: the state is a label on the player,
            // and antd already draws exactly that with the right fill for the colour.
            _state.Location = new Point(18, 78);
            _state.AutoSizeMode = TAutoSize.Auto;
            _state.Size = new Size(200, 30);
            _state.Radius = 6;
            _state.Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold);
            _state.Text = "Đang kiểm tra...";

            _detail.Location = new Point(18, 112);
            _detail.Size = new Size(376, 50);
            _detail.ForeColor = UiTheme.Muted;
            _detail.Font = new Font("Segoe UI", 9.5F);
            _detail.TextMultiLine = true;
            _detail.BackColor = Color.Transparent;

            card.Controls.Add(name);
            card.Controls.Add(address);
            card.Controls.Add(_state);
            card.Controls.Add(_detail);

            _pingResult.Location = new Point(18, 192);
            _pingResult.Size = new Size(408, 40);
            _pingResult.ForeColor = UiTheme.Muted;
            _pingResult.Font = new Font("Segoe UI", 9.5F);
            _pingResult.TextMultiLine = true;
            _pingResult.BackColor = Color.Transparent;
            _pingResult.Text = "";

            _pingButton.Text = "Đo ping";
            _pingButton.Location = new Point(14, 240);
            _pingButton.Size = new Size(110, 44);
            _pingButton.Type = TTypeMini.Default;
            _pingButton.Shape = TShape.Round;
            _pingButton.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold);
            _pingButton.Cursor = Cursors.Hand;
            _pingButton.Click += (s, e) =>
            {
                if (_pingCancel != null) { _pingCancel.Cancel(); return; }
                MeasurePing();
            };

            _watchButton.Text = "Xem trận";
            _watchButton.Location = new Point(196, 240);
            _watchButton.Size = new Size(110, 44);
            _watchButton.Type = TTypeMini.Primary;
            _watchButton.Shape = TShape.Round;
            _watchButton.BackColor = LiveColor;
            _watchButton.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold);
            _watchButton.Cursor = Cursors.Hand;
            _watchButton.Enabled = false;
            _watchButton.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };

            var close = UiTheme.Quiet("Đóng", height: 44);
            close.Dock = DockStyle.None;
            close.Location = new Point(316, 240);
            close.Size = new Size(110, 44);
            close.DialogResult = DialogResult.Cancel;

            Controls.Add(card);
            Controls.Add(_pingResult);
            Controls.Add(_pingButton);
            Controls.Add(_watchButton);
            Controls.Add(close);
            CancelButton = close;

            _refresh.Tick += (s, e) => Probe();
            _refresh.Start();
            Probe();
        }

        /// <summary>How long a measurement runs, and how often it samples.</summary>
        private const int PingSeconds = 30;
        private const int PingIntervalMs = 1000;

        /// <summary>
        /// Measures round-trip latency to this player over the VPN for half a minute.
        ///
        /// This is the number that matters for a game: the VPN is hub-and-spoke while AoE2 is
        /// peer-to-peer, so a packet between two players crosses the tunnel server and comes
        /// back. What comes out is that whole path, which is why it can be much larger than
        /// either player's ping to the server.
        ///
        /// Thirty samples over thirty seconds rather than a quick burst, because the question
        /// is whether the link is STEADY. A handful of pings a fifth of a second apart all land
        /// inside the same moment of network weather and will look fine even on a connection
        /// that stutters every few seconds - and a lockstep RTS runs at the pace of the worst
        /// moment, not the average one. So the verdict is driven by jitter and loss, with the
        /// average reported alongside rather than on its own.
        /// </summary>
        private async void MeasurePing()
        {
            string ip = (_lookup?.Invoke(_user.Username)?.IpAddress ?? _user.IpAddress ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(ip))
            {
                _pingResult.ForeColor = IdleColor;
                _pingResult.Text = "Người này chưa có địa chỉ VPN để đo.";
                return;
            }

            var cancel = new System.Threading.CancellationTokenSource();
            _pingCancel = cancel;
            _pingButton.Text = "Dừng";

            var times = new System.Collections.Generic.List<long>();
            int lost = 0, sent = 0;

            try
            {
                using var ping = new System.Net.NetworkInformation.Ping();
                for (int i = 0; i < PingSeconds && !cancel.IsCancellationRequested; i++)
                {
                    var started = DateTime.UtcNow;
                    sent++;
                    try
                    {
                        var reply = await ping.SendPingAsync(ip, PingIntervalMs);
                        if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                        {
                            times.Add(reply.RoundtripTime);
                        }
                        else
                        {
                            lost++;
                        }
                    }
                    catch (Exception ex)
                    {
                        lost++;
                        DebugLogger.Warn($"UserStatusDialog: ping to {ip} failed: {ex.Message}");
                    }

                    if (IsDisposed) return;
                    ShowPingProgress(sent, times, lost);

                    // Pace to roughly one sample a second, counting the time the reply already
                    // took - otherwise a slow link stretches a 30s measurement well past a
                    // minute and the reading stops matching what the label promised.
                    var elapsed = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                    int wait = PingIntervalMs - elapsed;
                    if (wait > 0)
                    {
                        try { await System.Threading.Tasks.Task.Delay(wait, cancel.Token); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }
            finally
            {
                _pingCancel = null;
                cancel.Dispose();
                if (!IsDisposed)
                {
                    _pingButton.Text = "Đo ping";
                    _pingButton.Enabled = true;
                }
            }

            if (IsDisposed) return;
            ShowPingVerdict(times, lost, sent);
        }

        private void ShowPingProgress(int sent, System.Collections.Generic.List<long> times, int lost)
        {
            string latest = times.Count > 0 ? $"{times[times.Count - 1]} ms" : "không phản hồi";
            _pingResult.ForeColor = UiTheme.Muted;
            _pingResult.Text = $"Đang đo... {sent}/{PingSeconds}  ·  gần nhất: {latest}"
                             + (lost > 0 ? $"  ·  mất {lost}" : string.Empty);
        }

        /// <summary>
        /// Turns the samples into an answer to "is it stable?".
        ///
        /// Jitter is mean absolute deviation, the same figure ping reports as mdev. It is the
        /// one that decides how a match feels: a steady 60ms is playable, while 30ms that
        /// regularly spikes to 120ms is not, and an average alone cannot tell those apart.
        /// Loss outranks it - a lockstep game stalls every player when one packet goes missing.
        /// </summary>
        private void ShowPingVerdict(System.Collections.Generic.List<long> times, int lost, int sent)
        {
            if (times.Count == 0)
            {
                // Deliberately not "offline". Windows blocks inbound ICMP by default, and
                // measured against live peers about half were silent while perfectly
                // connected. Reporting that as a dead player would be wrong far too often.
                _pingResult.ForeColor = WaitColor;
                _pingResult.Text = "Không nhận được phản hồi. Máy của họ có thể đang chặn ping "
                                 + "(bản TADA Play cũ chưa mở), không hẳn là mất kết nối.";
                return;
            }

            long min = times[0], max = times[0], total = 0;
            foreach (long t in times) { if (t < min) min = t; if (t > max) max = t; total += t; }
            long avg = total / times.Count;

            double deviation = 0;
            foreach (long t in times) { deviation += Math.Abs(t - avg); }
            double jitter = deviation / times.Count;

            int lossPercent = sent > 0 ? (int)Math.Round(lost * 100.0 / sent) : 0;

            string verdict;
            Color colour;
            if (lossPercent >= 5)
            {
                verdict = $"KHÔNG ỔN ĐỊNH - mất {lossPercent}% gói";
                colour = WaitColor;
            }
            else if (jitter >= 15)
            {
                verdict = "DAO ĐỘNG NHIỀU - dễ giật trong trận";
                colour = WaitColor;
            }
            else if (jitter >= 5)
            {
                verdict = "ỔN ĐỊNH";
                colour = LiveColor;
            }
            else
            {
                verdict = "RẤT ỔN ĐỊNH";
                colour = LiveColor;
            }

            _pingResult.ForeColor = colour;
            _pingResult.Text = $"{verdict}  ·  trung bình {avg} ms (thấp nhất {min}, cao nhất {max})\n"
                             + $"dao động {jitter:F1} ms, mất {lost}/{sent} gói trong {sent} giây";
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
                // Prefer what the lobby server has broadcast - it costs nothing and keeps
                // working while the VPN does not. Only ask the peer directly when they are
                // running a build too old to report it.
                User latest = (_user.Username != null ? _lookup?.Invoke(_user.Username) : null) ?? _user;
                LiveShareClient.HostStatus status = LiveShareClient.FromBroadcast(latest)
                                                    ?? await LiveShareClient.TryGetStatusAsync(_user.IpAddress);
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

            if (status.InGame && !status.AllowSpectate)
            {
                // Playing, but they have turned off being watched. The countdown below would
                // be a promise nothing is going to keep, and "không xem được" alone reads like
                // a fault - so say which it is.
                string clock = status.GameMs > 0 ? $" - đã chơi {Clock(status.GameTime)}" : string.Empty;
                Show("ĐANG CHƠI - KHÔNG SPEC", IdleColor,
                     $"Người này đang trong trận{clock}, nhưng đã tắt cho phép người khác xem. "
                     + "Không thể xem trận của họ.", false);
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
            // The colour still decides the meaning, so it keeps driving the tag - mapped to
            // an antd type so the fill and the border come out consistent with every other
            // tag in the app rather than being mixed here.
            _state.Type = color == LiveColor ? TTypeMini.Success
                        : color == WaitColor ? TTypeMini.Warn
                        : TTypeMini.Default;
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
