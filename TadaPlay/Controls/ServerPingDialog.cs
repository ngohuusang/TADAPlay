using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TadaPlay.Logger;
using TadaPlay.Utils;

namespace TadaPlay.Controls
{
    /// <summary>
    /// "Kiểm tra ping" on the home screen: measures two legs for half a minute and answers
    /// "is this connection good enough to play on?".
    ///
    ///  - Internet: this machine to the server's public address. This is the path the tunnel
    ///    itself rides on, and the part a player can do something about (wifi vs cable, a
    ///    download running in the background).
    ///  - VPN: this machine to the VPN hub (10.10.0.1) through the tunnel. The VPN is
    ///    hub-and-spoke, so every packet of a match crosses this - it is the number closest to
    ///    what the game actually experiences.
    ///
    /// Both are sampled together, once a second, so they see the same moments of network
    /// weather and can be compared directly. Thirty samples rather than a quick burst, because
    /// the question is whether the link is STEADY: a lockstep RTS runs at the pace of its worst
    /// moment, so loss and jitter decide the verdict and the average is reported alongside.
    /// </summary>
    public class ServerPingDialog : Form
    {
        public const string ServerHost = "openvpn.aoe2.io.vn";
        public const string VpnHubHost = "10.10.0.1";
        private const int TcpFallbackPort = 8888; // the lobby WebSocket port, known to be open

        private const int Samples = 30;
        private const int IntervalMs = 1000;
        private const int TimeoutMs = 1000;

        private const int CardWidth = 532;
        private const int SectionHeight = 168;

        private static readonly Color Good = Color.FromArgb(0x38, 0x9E, 0x0D);      // green-6
        private static readonly Color Okay = Color.FromArgb(0x15, 0x77, 0xD4);      // blue-6
        private static readonly Color Attention = Color.FromArgb(0xD4, 0x88, 0x06); // gold-7
        private static readonly Color Bad = Color.FromArgb(0xCF, 0x13, 0x22);       // red-6

        /// <summary>One measured leg and the controls that report it.</summary>
        private sealed class Target
        {
            public string Name = "";
            public string Host = "";
            public bool AllowTcpFallback;
            public bool UseTcp;
            public readonly List<long?> Results = new(); // null = lost
            public AntdUI.Label Title = null!;
            public PingChart Chart = null!;
            public AntdUI.Label Stats = null!;

            // Filled in by Evaluate().
            public long Avg;
            public double Jitter;
            public int LossPercent;
            public int Rank; // 0 = very stable .. 3 = unstable, -1 = no replies
            public string Verdict = "";
            public Color Colour;
        }

        private readonly Target _internet = new()
        {
            Name = "Internet → máy chủ",
            Host = ServerHost,
            AllowTcpFallback = true,
        };

        private readonly Target _vpn = new()
        {
            Name = "Qua VPN → máy chủ VPN",
            Host = VpnHubHost,
            AllowTcpFallback = false,
        };

        private readonly CancellationTokenSource _cts = new();

        private AntdUI.Label _status = null!;
        private AntdUI.Progress _progress = null!;
        private AntdUI.Label _verdict = null!;
        private AntdUI.Button _againButton = null!;
        private AntdUI.Button _closeButton = null!;
        private bool _running;

        public ServerPingDialog(Form owner)
        {
            Text = "Kiểm tra ping tới máy chủ";
            StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 10F);
            BackColor = UiTheme.PageBg;
            ClientSize = new Size(560, 58 + 2 * SectionHeight + 120 + 60);

            BuildLayout();
        }

        private void BuildLayout()
        {
            var intro = new AntdUI.Label
            {
                Text = $"Đo độ trễ, độ dao động và tỉ lệ mất gói trong {Samples} giây - qua Internet "
                     + "và qua VPN - để biết mạng có đủ ổn định để chơi hay không.",
                Location = new Point(16, 10),
                Size = new Size(528, 44),
                ForeColor = UiTheme.Muted,
                Font = new Font("Segoe UI", 9.5F),
                TextMultiLine = true,
                BackColor = Color.Transparent,
            };

            var card = new AntdUI.Panel
            {
                Location = new Point(14, 58),
                Size = new Size(CardWidth, 2 * SectionHeight + 120),
                Radius = 10,
                Shadow = 8,
                ShadowOpacity = 0.10F,
                ShadowOffsetY = 2,
                Back = Color.White,
                BorderWidth = 1,
                BorderColor = UiTheme.CardBorder,
            };

            _status = new AntdUI.Label
            {
                Text = "Đang chuẩn bị...",
                Location = new Point(16, 10),
                Size = new Size(CardWidth - 32, 24),
                ForeColor = UiTheme.Ink,
                Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
                BackColor = Color.Transparent,
            };

            _progress = new AntdUI.Progress
            {
                Location = new Point(16, 38),
                Size = new Size(CardWidth - 32, 12),
                Value = 0F,
                Fill = Okay,
                BackColor = Color.Transparent,
            };

            card.Controls.Add(_status);
            card.Controls.Add(_progress);

            int y = 58;
            foreach (Target t in new[] { _vpn, _internet })
            {
                BuildSection(card, t, y);
                y += SectionHeight;
            }

            _verdict = new AntdUI.Label
            {
                Text = "",
                Location = new Point(16, y + 4),
                Size = new Size(CardWidth - 32, 54),
                ForeColor = UiTheme.Ink,
                Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold),
                TextMultiLine = true,
                BackColor = Color.Transparent,
            };
            card.Controls.Add(_verdict);

            int buttonsY = card.Bottom + 12;
            _againButton = UiTheme.Toolbar("Đo lại", new Point(318, buttonsY), 110, 38);
            _againButton.Enabled = false;
            _againButton.Click += async (s, e) => await RunAsync();

            _closeButton = UiTheme.Toolbar("Đóng", new Point(436, buttonsY), 110, 38);
            _closeButton.Click += (s, e) => Close();

            Controls.Add(intro);
            Controls.Add(card);
            Controls.Add(_againButton);
            Controls.Add(_closeButton);
            CancelButton = _closeButton;
        }

        private static void BuildSection(Control card, Target t, int y)
        {
            t.Title = new AntdUI.Label
            {
                Text = $"{t.Name} ({t.Host})",
                Location = new Point(16, y),
                Size = new Size(CardWidth - 32, 24),
                ForeColor = UiTheme.Ink,
                Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
                BackColor = Color.Transparent,
            };

            t.Chart = new PingChart
            {
                Location = new Point(16, y + 26),
                Size = new Size(CardWidth - 32, 92),
            };

            t.Stats = new AntdUI.Label
            {
                Text = "",
                Location = new Point(16, y + 122),
                Size = new Size(CardWidth - 32, 42),
                ForeColor = UiTheme.Muted,
                Font = new Font("Segoe UI", 9F),
                TextMultiLine = true,
                BackColor = Color.Transparent,
            };

            card.Controls.Add(t.Title);
            card.Controls.Add(t.Chart);
            card.Controls.Add(t.Stats);
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            await RunAsync();
        }

        private async Task RunAsync()
        {
            if (_running) return;
            _running = true;
            _againButton.Enabled = false;
            _verdict.Text = "";
            _progress.Value = 0F;
            _progress.Fill = Okay;
            foreach (Target t in new[] { _internet, _vpn })
            {
                t.Results.Clear();
                t.Chart.SetSamples(t.Results);
                t.Stats.ForeColor = UiTheme.Muted;
                t.Stats.Text = "";
                t.UseTcp = false;
            }

            try
            {
                _status.Text = "Đang kết nối tới máy chủ...";
                // Only the public server may need the TCP fallback: the hub is inside our own
                // tunnel, so if it does not answer ping that is itself the finding.
                _internet.UseTcp = !await IcmpWorksAsync(_internet.Host, _cts.Token);
                DebugLogger.Info($"ServerPingDialog: measuring {ServerHost} via {(_internet.UseTcp ? "TCP" : "ICMP")} and {VpnHubHost} via ICMP.");

                for (int i = 0; i < Samples; i++)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    var started = Stopwatch.StartNew();

                    // Sampled together so both legs see the same moment.
                    Task<long?> a = SampleAsync(_internet, _cts.Token);
                    Task<long?> b = SampleAsync(_vpn, _cts.Token);
                    await Task.WhenAll(a, b);
                    if (IsDisposed) return;

                    _internet.Results.Add(a.Result);
                    _vpn.Results.Add(b.Result);
                    ShowProgress();

                    int wait = IntervalMs - (int)started.ElapsedMilliseconds;
                    if (wait > 0) await Task.Delay(wait, _cts.Token);
                }

                ShowVerdict();
            }
            catch (OperationCanceledException)
            {
                // Closed mid-run.
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"ServerPingDialog: measurement failed: {ex}");
                if (!IsDisposed)
                {
                    _verdict.ForeColor = Bad;
                    _verdict.Text = $"Không đo được: {ex.Message}";
                }
            }
            finally
            {
                _running = false;
                if (!IsDisposed) _againButton.Enabled = true;
            }
        }

        private static Task<long?> SampleAsync(Target t, CancellationToken token) =>
            t.UseTcp ? TcpRoundTripAsync(t.Host, token) : IcmpRoundTripAsync(t.Host);

        private static async Task<bool> IcmpWorksAsync(string host, CancellationToken token)
        {
            for (int i = 0; i < 3; i++)
            {
                token.ThrowIfCancellationRequested();
                if (await IcmpRoundTripAsync(host) != null) return true;
            }
            return false;
        }

        private static async Task<long?> IcmpRoundTripAsync(string host)
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, TimeoutMs);
                return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"ServerPingDialog: ping to {host} failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>A TCP handshake is one round trip; timing it measures the same thing ping
        /// would. DNS comes from the OS cache after the first call, so it does not skew later
        /// samples.</summary>
        private static async Task<long?> TcpRoundTripAsync(string host, CancellationToken token)
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeoutMs);
            var sw = Stopwatch.StartNew();
            try
            {
                await client.ConnectAsync(host, TcpFallbackPort, timeout.Token);
                return sw.ElapsedMilliseconds;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return null; // timed out
            }
            catch (SocketException ex)
            {
                DebugLogger.Warn($"ServerPingDialog: TCP connect to {host} failed: {ex.Message}");
                return null;
            }
        }

        private void ShowProgress()
        {
            int sent = _internet.Results.Count;
            _progress.Value = sent / (float)Samples;
            _status.Text = $"Đang đo... {sent}/{Samples}";

            foreach (Target t in new[] { _internet, _vpn })
            {
                t.Chart.SetSamples(t.Results);
                long? latest = t.Results[^1];
                int lost = t.Results.Count(r => r == null);
                t.Stats.Text = "Gần nhất: " + (latest.HasValue ? $"{latest} ms" : "không phản hồi")
                             + (lost > 0 ? $"  ·  mất {lost}" : "")
                             + (t.UseTcp ? "  ·  đo bằng TCP (máy chủ chặn ping)" : "");
            }
        }

        /// <summary>
        /// Loss outranks everything (a lockstep game stalls every player when a packet is
        /// missing), then jitter (spikes are felt as stutter), then raw latency.
        /// </summary>
        private static void Evaluate(Target t)
        {
            var times = t.Results.Where(r => r.HasValue).Select(r => r!.Value).ToList();
            int sent = t.Results.Count;
            int lost = sent - times.Count;

            if (times.Count == 0)
            {
                t.Rank = -1;
                t.Colour = Bad;
                t.Verdict = "KHÔNG PHẢN HỒI";
                t.Stats.ForeColor = Bad;
                t.Stats.Text = $"Không nhận được phản hồi nào trong {sent} lần đo.";
                return;
            }

            long min = times.Min(), max = times.Max();
            t.Avg = (long)Math.Round(times.Average());
            t.Jitter = times.Average(x => Math.Abs(x - t.Avg));
            t.LossPercent = (int)Math.Round(lost * 100.0 / sent);

            if (t.LossPercent >= 5)
            {
                t.Rank = 3; t.Colour = Bad; t.Verdict = $"KHÔNG ỔN ĐỊNH (mất {t.LossPercent}% gói)";
            }
            else if (t.Jitter >= 15)
            {
                t.Rank = 2; t.Colour = Attention; t.Verdict = "DAO ĐỘNG NHIỀU";
            }
            else if (t.Avg >= 200)
            {
                t.Rank = 2; t.Colour = Attention; t.Verdict = "PING CAO";
            }
            else if (lost > 0 || t.Jitter >= 5 || t.Avg >= 100)
            {
                t.Rank = 1; t.Colour = Okay; t.Verdict = "TẠM ỔN";
            }
            else
            {
                t.Rank = 0; t.Colour = Good; t.Verdict = "RẤT ỔN ĐỊNH";
            }

            t.Stats.ForeColor = t.Colour;
            t.Stats.Text = $"{t.Verdict}  ·  trung bình {t.Avg} ms (thấp nhất {min}, cao nhất {max})\n"
                         + $"Dao động {t.Jitter:F1} ms  ·  mất {lost}/{sent} gói"
                         + (t.UseTcp ? "  ·  đo bằng TCP" : "");
        }

        private void ShowVerdict()
        {
            Evaluate(_internet);
            Evaluate(_vpn);
            _status.Text = $"Đã đo xong {_internet.Results.Count} lần.";

            string verdict;
            Color colour;

            if (_internet.Rank < 0 && _vpn.Rank < 0)
            {
                verdict = "KHÔNG KẾT NỐI ĐƯỢC tới máy chủ. Hãy kiểm tra Internet của bạn.";
                colour = Bad;
            }
            else if (_vpn.Rank < 0)
            {
                // Internet works but the tunnel does not: the game will not connect at all.
                verdict = "KHÔNG CÓ KẾT NỐI QUA VPN - Internet vẫn chạy nhưng không tới được máy "
                        + "chủ VPN. Hãy bấm \"Kết nối lại VPN\" rồi đo lại.";
                colour = Bad;
            }
            else
            {
                // The VPN leg is what the game actually uses; the internet leg explains why
                // it is bad when it is. Judge on whichever is worse.
                Target worst = _vpn.Rank >= _internet.Rank ? _vpn : _internet;
                colour = worst.Colour;
                verdict = worst.Rank switch
                {
                    3 => "KHÔNG ỔN ĐỊNH - mất gói, trận đấu sẽ bị giật/khựng. Nên dùng dây mạng "
                         + "thay vì wifi và tắt các ứng dụng tải nặng.",
                    2 => worst.Jitter >= 15
                         ? "DAO ĐỘNG NHIỀU - dễ giật trong trận. Kiểm tra wifi hoặc ứng dụng đang dùng mạng."
                         : "PING CAO - chơi được nhưng sẽ thấy trễ.",
                    1 => "TẠM ỔN - đủ để chơi, có thể thỉnh thoảng hơi trễ.",
                    _ => "RẤT ỔN ĐỊNH - mạng sẵn sàng để chơi.",
                };
                if (_internet.Rank < 0)
                {
                    verdict += " (Máy chủ không trả lời qua Internet, chỉ đánh giá theo VPN.)";
                }
            }

            _progress.Fill = colour;
            _verdict.ForeColor = colour;
            _verdict.Text = verdict;

            LastResultLine = $"[Ping] {verdict.Split(" - ")[0]} · VPN: {Summary(_vpn)} · Internet: {Summary(_internet)}";
            LastResultColor = colour;
        }

        private static string Summary(Target t) => t.Rank < 0
            ? "không phản hồi"
            : $"{t.Avg} ms, dao động {t.Jitter:F1} ms, mất {t.LossPercent}%";

        /// <summary>One line for the home screen's log, or null if no run finished.</summary>
        public string? LastResultLine { get; private set; }
        public Color LastResultColor { get; private set; } = UiTheme.Muted;

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_running) _cts.Cancel();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _cts.Dispose();
            base.Dispose(disposing);
        }

        /// <summary>One bar per sample, coloured by latency; a red stub marks a lost packet.
        /// Seeing the spikes is what makes "dao động" mean something to a player.</summary>
        private sealed class PingChart : Control
        {
            private List<long?> _samples = new();

            public PingChart()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = Color.FromArgb(0xF7, 0xF9, 0xFC);
            }

            public void SetSamples(List<long?> samples)
            {
                _samples = samples.ToList();
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(BackColor);

                const int axis = 34;
                var plot = new Rectangle(axis, 6, Width - axis - 6, Height - 12);

                long top = Math.Max(100, _samples.Where(s => s.HasValue).Select(s => s!.Value).DefaultIfEmpty(0).Max());
                top = (long)(Math.Ceiling(top / 50.0) * 50);

                using var gridPen = new Pen(Color.FromArgb(0xE3, 0xE8, 0xEF));
                using var labelBrush = new SolidBrush(UiTheme.Muted);
                using var labelFont = new Font("Segoe UI", 7.5F);
                foreach (long mark in new[] { 0L, top / 2, top })
                {
                    int y = plot.Bottom - (int)(plot.Height * mark / (double)top);
                    g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                    g.DrawString($"{mark}", labelFont, labelBrush, 2, y - 7);
                }

                float slot = plot.Width / (float)Samples;
                float barWidth = Math.Max(2f, slot - 3f);
                for (int i = 0; i < _samples.Count; i++)
                {
                    float x = plot.Left + i * slot + (slot - barWidth) / 2f;
                    long? v = _samples[i];
                    if (v == null)
                    {
                        using var lostBrush = new SolidBrush(Bad);
                        g.FillRectangle(lostBrush, x, plot.Top, barWidth, 6);
                        continue;
                    }
                    float h = Math.Max(2f, plot.Height * v.Value / (float)top);
                    Color c = v.Value < 100 ? Good : v.Value < 200 ? Attention : Bad;
                    using var brush = new SolidBrush(c);
                    g.FillRectangle(brush, x, plot.Bottom - h, barWidth, h);
                }
            }
        }
    }
}
