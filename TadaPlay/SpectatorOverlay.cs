using System;
using System.Drawing;
using System.Windows.Forms;
using TadaPlay.Common.Models;
using TadaPlay.Connections;
using TadaPlay.Logger;
using TadaPlay.Utils;

namespace TadaPlay
{
    /// <summary>
    /// A small always-on-top readout of the host's game time, shown over the game while
    /// spectating.
    ///
    /// The number exists in TadaPlay's dialogs already, but those are unreachable once the
    /// game is running and covering the screen - which is exactly when a viewer wants it, to
    /// see how far behind the live match they are and whether to speed the replay up. So it
    /// gets its own window that floats above the game.
    ///
    /// Deliberately unobtrusive: it never takes focus (see <see cref="ShowWithoutActivation"/>,
    /// which matters because stealing focus from the game would drop the player out of it),
    /// it can be dragged anywhere with the mouse, and right-clicking closes it.
    ///
    /// One limitation worth knowing: a game running in EXCLUSIVE fullscreen paints over every
    /// other window, topmost included, and no overlay can be shown above it. Windowed or
    /// borderless-fullscreen works.
    /// </summary>
    public sealed class SpectatorOverlay : Form
    {
        /// <summary>How often the host's latest status is looked up.</summary>
        private const int PollMs = 3000;

        /// <summary>
        /// How often the clock is redrawn. Faster than the poll on purpose: what is drawn is
        /// the host's LIVE match time, extrapolated from their last capture (see
        /// <see cref="LiveMatchClock"/>), so it can tick every second off a status that only
        /// changes every ten. A clock that jumped ten seconds at a time was the thing that
        /// made a replay look as though it had overtaken the match it was following.
        /// </summary>
        private const int RenderMs = 1000;

        private static readonly Color Ink = Color.FromArgb(245, 245, 245);
        private static readonly Color LiveColor = Color.FromArgb(120, 220, 90);
        private static readonly Color DimColor = Color.FromArgb(165, 165, 165);

        private readonly string _hostIp;
        private readonly string _hostLabel;

        /// <summary>
        /// Finds the host's current record by name, so the overlay reads the same lobby
        /// broadcast the picker and the status dialog do.
        ///
        /// It used to ask the host directly over the VPN every three seconds. That made it the
        /// one part of the UI that could disagree with the rest - and the one that went blank
        /// during a tunnel dropout, which is when a viewer most wants to know whether the host
        /// is still playing. Asking the host is now only the fallback for a build too old to
        /// broadcast.
        /// </summary>
        private readonly Func<string, User> _lookup;
        private readonly Label _clock = new();
        private readonly Label _who = new();
        private readonly System.Windows.Forms.Timer _poll = new() { Interval = PollMs };
        private readonly System.Windows.Forms.Timer _render = new() { Interval = RenderMs };
        private LiveShareClient.HostStatus _status;
        private DateTime _statusAtUtc;
        private bool _probing;
        private Point _dragFrom;
        private bool _dragging;

        /// <summary>Never steal focus - the player is in the game, and taking it drops them out.</summary>
        protected override bool ShowWithoutActivation => true;

        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        /// <summary>
        /// <see cref="ShowWithoutActivation"/> on its own is not enough - measured: the overlay
        /// still took the foreground away from the window underneath it. WS_EX_NOACTIVATE makes
        /// that impossible at the window level, on the initial show AND on every later click,
        /// so dragging the overlay cannot pull the player out of the game either. Mouse
        /// messages still arrive, so dragging and right-click-to-close keep working.
        ///
        /// WS_EX_TOOLWINDOW additionally keeps it out of Alt-Tab, where a 226px clock would
        /// only be in the way.
        /// </summary>
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return parameters;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y,
                                                int cx, int cy, uint flags);

        private const int SW_SHOWNOACTIVATE = 4;
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        /// <summary>
        /// Shows the overlay without taking the foreground. Use this instead of Show().
        ///
        /// Neither ShowWithoutActivation nor WS_EX_NOACTIVATE is sufficient on its own -
        /// measured, both times: Form.Show() still handed the overlay the foreground and the
        /// window underneath lost it. Since the window underneath is the game the player is
        /// watching, that is not cosmetic; it drops them out of it. Showing the window through
        /// the API with SW_SHOWNOACTIVATE, and placing it topmost with SWP_NOACTIVATE, is what
        /// actually leaves the foreground alone.
        /// </summary>
        public void ShowNoActivate()
        {
            IntPtr handle = Handle;   // forces handle creation without showing
            ShowWindow(handle, SW_SHOWNOACTIVATE);
            SetWindowPos(handle, HWND_TOPMOST, Left, Top, Width, Height,
                         SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        public SpectatorOverlay(string hostIp, string hostLabel, Func<string, User> lookup = null)
        {
            _hostIp = hostIp;
            _hostLabel = hostLabel ?? hostIp;
            _lookup = lookup;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(18, 18, 18);
            Opacity = 0.82;
            ClientSize = new Size(226, 58);

            _who.Text = $"▶ {_hostLabel}";
            _who.ForeColor = DimColor;
            _who.Font = new Font("Segoe UI", 8.5F);
            _who.Location = new Point(12, 7);
            _who.Size = new Size(202, 16);
            _who.BackColor = Color.Transparent;

            _clock.Text = "--:--";
            _clock.ForeColor = Ink;
            _clock.Font = new Font("Segoe UI Semibold", 17F, FontStyle.Bold);
            _clock.Location = new Point(12, 22);
            _clock.Size = new Size(202, 30);
            _clock.BackColor = Color.Transparent;

            Controls.Add(_who);
            Controls.Add(_clock);

            // Drag from anywhere on the overlay, including the labels - a 226px window with
            // only its background draggable is fiddly to move.
            foreach (Control control in new Control[] { this, _who, _clock })
            {
                control.MouseDown += OnDragStart;
                control.MouseMove += OnDragMove;
                control.MouseUp += (s, e) => _dragging = false;
                control.MouseClick += (s, e) => { if (e.Button == MouseButtons.Right) Close(); };
            }

            PlaceTopCentre();

            _poll.Tick += (s, e) => Probe();
            _poll.Start();
            _render.Tick += (s, e) => Render();
            _render.Start();
            Probe();
        }

        /// <summary>
        /// Top-centre of the primary screen, just below the game's own resource bar rather than
        /// over it. Only a starting point - it can be dragged.
        /// </summary>
        private void PlaceTopCentre()
        {
            Rectangle screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
            Location = new Point(screen.Left + (screen.Width - Width) / 2, screen.Top + 46);
        }

        private void OnDragStart(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _dragFrom = e.Location;
            if (sender is Control control && control != this)
            {
                _dragFrom = new Point(e.X + control.Left, e.Y + control.Top);
            }
        }

        private void OnDragMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            Point at = e.Location;
            if (sender is Control control && control != this)
            {
                at = new Point(e.X + control.Left, e.Y + control.Top);
            }
            Location = new Point(Location.X + at.X - _dragFrom.X, Location.Y + at.Y - _dragFrom.Y);
        }

        private async void Probe()
        {
            if (_probing || IsDisposed) return;
            _probing = true;
            try
            {
                User latest = _lookup?.Invoke(_hostLabel);
                LiveShareClient.HostStatus status = LiveShareClient.FromBroadcast(latest)
                                                    ?? await LiveShareClient.TryGetStatusAsync(_hostIp);
                if (IsDisposed) return;

                _status = status;
                _statusAtUtc = DateTime.UtcNow;
                Render();
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"SpectatorOverlay: probe failed: {ex.Message}");
            }
            finally
            {
                _probing = false;
            }
        }

        /// <summary>
        /// Draws the last status fetched. Runs every second rather than once per poll, because
        /// both numbers on it move continuously between polls: the match clock is extrapolated
        /// forward from the host's last capture, and the countdown runs down.
        /// </summary>
        private void Render()
        {
            if (IsDisposed) return;

            LiveShareClient.HostStatus status = _status;
            if (status == null)
            {
                _clock.ForeColor = DimColor;
                _who.Text = $"▶ {_hostLabel} · mất kết nối";
                return;
            }

            _who.Text = status.InGame ? $"▶ {_hostLabel} · đang chơi"
                                      : $"▶ {_hostLabel} · đã kết thúc";

            if (status.GameMs > 0)
            {
                // LiveGameTime, not GameTime: the captured value is a still frame up to 90
                // seconds old, and a replay running at normal speed appears to overtake it.
                TimeSpan t = status.LiveGameTime;
                _clock.Text = t.TotalHours >= 1
                    ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                    : $"{t.Minutes:00}:{t.Seconds:00}";
                _clock.ForeColor = status.InGame ? LiveColor : DimColor;
            }
            else if (status.InGame)
            {
                // Match running but nothing captured yet - show the countdown rather than a
                // dash, so it reads as "not yet" instead of "broken". Counted down from when
                // the status was fetched, so it moves between polls instead of sticking.
                int left = status.WaitSeconds - (int)(DateTime.UtcNow - _statusAtUtc).TotalSeconds;
                _clock.Text = $"chờ {Math.Max(1, left)}s";
                _clock.ForeColor = DimColor;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _poll.Stop();
                _poll.Dispose();
                _render.Stop();
                _render.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
