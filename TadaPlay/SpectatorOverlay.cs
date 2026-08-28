using System;
using System.Drawing;
using System.Windows.Forms;
using TadaPlay.Common.Models;
using TadaPlay.Connections;
using TadaPlay.Logger;

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
        private const int PollMs = 3000;

        // Two layouts. Compact is the normal readout; paused deliberately takes far more room,
        // because the viewer has to notice it without looking for it - they are watching the
        // game, not the overlay, and the whole point is to prompt an action.
        private static readonly Size CompactSize = new(226, 58);
        private static readonly Size PausedSize = new(330, 116);

        private static readonly Font WhoFont = new("Segoe UI", 8.5F);
        private static readonly Font WhoFontPaused = new("Segoe UI Semibold", 10F, FontStyle.Bold);
        private static readonly Font ClockFont = new("Segoe UI Semibold", 17F, FontStyle.Bold);
        private static readonly Font ClockFontPaused = new("Segoe UI Semibold", 23F, FontStyle.Bold);
        private static readonly Font HintFont = new("Segoe UI", 9.5F);

        private static readonly Color Ink = Color.FromArgb(245, 245, 245);
        private static readonly Color LiveColor = Color.FromArgb(120, 220, 90);
        private static readonly Color DimColor = Color.FromArgb(165, 165, 165);
        /// <summary>Paused reads as amber: not running, but not over either.</summary>
        private static readonly Color PausedColor = Color.FromArgb(250, 190, 60);

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
        /// <summary>What the viewer should DO about the pause. Only shown while paused.</summary>
        private readonly Label _hint = new();
        private readonly System.Windows.Forms.Timer _poll = new() { Interval = PollMs };
        private bool _probing;
        /// <summary>Which layout is currently applied, so the form is only resized on a change.</summary>
        private bool _pausedLayout;
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
            ClientSize = CompactSize;

            _who.Text = $"▶ {_hostLabel}";
            _who.ForeColor = DimColor;
            _who.Font = WhoFont;
            _who.Location = new Point(12, 7);
            _who.Size = new Size(202, 16);
            _who.BackColor = Color.Transparent;

            _clock.Text = "--:--";
            _clock.ForeColor = Ink;
            _clock.Font = ClockFont;
            _clock.Location = new Point(12, 22);
            _clock.Size = new Size(202, 30);
            _clock.BackColor = Color.Transparent;

            _hint.Text = "Đang tự động dừng replay của bạn...";
            _hint.ForeColor = PausedColor;
            _hint.Font = HintFont;
            _hint.Location = new Point(14, 74);
            _hint.Size = new Size(302, 34);
            _hint.BackColor = Color.Transparent;
            _hint.Visible = false;

            Controls.Add(_who);
            Controls.Add(_clock);
            Controls.Add(_hint);

            // Drag from anywhere on the overlay, including the labels - a 226px window with
            // only its background draggable is fiddly to move.
            foreach (Control control in new Control[] { this, _who, _clock, _hint })
            {
                control.MouseDown += OnDragStart;
                control.MouseMove += OnDragMove;
                control.MouseUp += (s, e) => _dragging = false;
                control.MouseClick += (s, e) => { if (e.Button == MouseButtons.Right) Close(); };
            }

            PlaceTopCentre();

            _poll.Tick += (s, e) => Probe();
            _poll.Start();
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

        /// <summary>
        /// Switches between the compact readout and the larger paused panel.
        ///
        /// Guarded on a change rather than run every poll: this resizes a top-level window, and
        /// doing that every three seconds would make the overlay visibly twitch and fight the
        /// user while they drag it.
        ///
        /// The window grows right and down from its existing Location, so an overlay the player
        /// has parked in a corner does not jump somewhere else at the moment it needs reading.
        /// </summary>
        private void ApplyPausedLayout(bool paused)
        {
            if (paused == _pausedLayout) return;
            _pausedLayout = paused;

            SuspendLayout();
            if (paused)
            {
                ClientSize = PausedSize;
                Opacity = 0.94;              // less see-through: this one is meant to interrupt
                _who.Font = WhoFontPaused;
                _who.ForeColor = PausedColor;
                _who.Location = new Point(14, 10);
                _who.Size = new Size(302, 20);
                _clock.Font = ClockFontPaused;
                _clock.Location = new Point(14, 32);
                _clock.Size = new Size(302, 40);
                _hint.Visible = true;
            }
            else
            {
                ClientSize = CompactSize;
                Opacity = 0.82;
                _hint.Visible = false;
                _who.Font = WhoFont;
                _who.ForeColor = DimColor;
                _who.Location = new Point(12, 7);
                _who.Size = new Size(202, 16);
                _clock.Font = ClockFont;
                _clock.Location = new Point(12, 22);
                _clock.Size = new Size(202, 30);
            }
            ResumeLayout();
            Invalidate();                    // repaint the border for the new state/size
        }

        /// <summary>
        /// An amber border while paused. The size change alone is easy to miss in peripheral
        /// vision over a busy game; an outline reads as "something changed" at a glance.
        /// </summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!_pausedLayout) return;

            using var pen = new Pen(PausedColor, 2f);
            e.Graphics.DrawRectangle(pen, 1, 1, ClientSize.Width - 2, ClientSize.Height - 2);
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

                if (status == null)
                {
                    // Unreachable is not paused - drop back to the compact readout so a stale
                    // "pause your game" prompt cannot sit on screen after the host disappears.
                    ApplyPausedLayout(false);
                    _clock.ForeColor = DimColor;
                    _who.Text = $"▶ {_hostLabel} · mất kết nối";
                    return;
                }

                // Paused is reported only while still in game - a finished match has a
                // stopped clock too, and calling that "paused" would be wrong.
                bool paused = status.InGame && status.Paused;

                ApplyPausedLayout(paused);

                if (paused)
                {
                    _who.Text = $"⏸ {_hostLabel} · ĐÃ TẠM DỪNG";
                }
                else
                {
                    _who.Text = status.InGame ? $"▶ {_hostLabel} · đang chơi"
                                              : $"▶ {_hostLabel} · đã kết thúc";
                }

                if (status.GameMs > 0)
                {
                    TimeSpan t = status.GameTime;
                    _clock.Text = t.TotalHours >= 1
                        ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                        : $"{t.Minutes:00}:{t.Seconds:00}";
                    // The frozen number is the point: it tells a viewer their replay is about
                    // to catch up to a match that is not moving. Amber says that is expected
                    // rather than a dropped connection, which is what a dimmed clock means here.
                    _clock.ForeColor = paused ? PausedColor
                                              : (status.InGame ? LiveColor : DimColor);
                }
                else if (status.InGame)
                {
                    // Match running but nothing captured yet - show the countdown rather than a
                    // dash, so it reads as "not yet" instead of "broken".
                    _clock.Text = $"chờ {Math.Max(1, status.WaitSeconds)}s";
                    _clock.ForeColor = DimColor;
                }
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _poll.Stop();
                _poll.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
