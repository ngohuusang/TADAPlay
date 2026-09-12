using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TadaPlay.Logger;
using TadaPlay.Utils;

namespace TadaPlay.Controls
{
    /// <summary>
    /// The report behind "Kiểm tra mạng" on the home screen.
    ///
    /// It starts working the moment it opens: one press checks and repairs, and this shows what
    /// is being done while it happens rather than going blank for four seconds and then
    /// announcing a verdict. Each row settles as its check finishes, so a player watching can
    /// see which part was slow and which part was wrong.
    ///
    /// The rows that end in amber are the interesting ones - they are the things this app cannot
    /// fix on the player's behalf (a router's own UPnP setting, a firewall locked by a company
    /// policy), so those carry a line of instruction instead of a result.
    /// </summary>
    public class NetworkCheckDialog : Form
    {
        private const int RowHeight = 78;
        private const int CardWidth = 552;

        private static readonly Color Good = Color.FromArgb(0x38, 0x9E, 0x0D);   // antd green-6
        private static readonly Color Changed = Color.FromArgb(0x15, 0x77, 0xD4); // antd blue-6
        private static readonly Color Attention = Color.FromArgb(0xD4, 0x88, 0x06); // antd gold-7
        private static readonly Color Bad = Color.FromArgb(0xCF, 0x13, 0x22);    // antd red-6

        private const string IconGood =
            "<svg viewBox='0 0 1024 1024'><path d='M512 64C264.6 64 64 264.6 64 512s200.6 448 448 448 448-200.6 448-448S759.4 64 512 64z m193.5 301.7l-210.6 292a31.8 31.8 0 0 1-51.7 0L318.5 484.9c-3.8-5.3 0-12.7 6.5-12.7h46.9c10.2 0 19.9 4.9 25.9 13.3l71.2 98.8 157.2-218c6-8.3 15.6-13.3 25.9-13.3H699c6.5 0 10.3 7.4 6.5 12.7z'/></svg>";

        private const string IconAttention =
            "<svg viewBox='0 0 1024 1024'><path d='M512 64C264.6 64 64 264.6 64 512s200.6 448 448 448 448-200.6 448-448S759.4 64 512 64z m-32 232c0-4.4 3.6-8 8-8h48c4.4 0 8 3.6 8 8v272c0 4.4-3.6 8-8 8h-48c-4.4 0-8-3.6-8-8V296z m32 440a48.01 48.01 0 0 1 0-96 48.01 48.01 0 0 1 0 96z'/></svg>";

        private const string IconBad =
            "<svg viewBox='0 0 1024 1024'><path d='M512 64C264.6 64 64 264.6 64 512s200.6 448 448 448 448-200.6 448-448S759.4 64 512 64z m165.4 618.2l-66-.3L512 563.4l-99.3 118.4-66.1.3c-4.4 0-8-3.5-8-8 0-1.9.7-3.7 1.9-5.2l130.1-155L340.5 359a8.32 8.32 0 0 1-1.9-5.2c0-4.4 3.6-8 8-8l66.1.3L512 464.6l99.3-118.4 66-.3c4.4 0 8 3.5 8 8 0 1.9-.7 3.7-1.9 5.2L553.5 514l130 155c1.2 1.5 1.9 3.3 1.9 5.2 0 4.4-3.6 8-8 8z'/></svg>";

        private const string IconRunning =
            "<svg viewBox='0 0 1024 1024'><path d='M512 64C264.6 64 64 264.6 64 512s200.6 448 448 448 448-200.6 448-448S759.4 64 512 64z m0 820c-205.4 0-372-166.6-372-372s166.6-372 372-372 372 166.6 372 372-166.6 372-372 372z'/><path d='M464 336a48 48 0 1 0 96 0 48 48 0 1 0-96 0z m72 112h-48c-4.4 0-8 3.6-8 8v272c0 4.4 3.6 8 8 8h48c4.4 0 8-3.6 8-8V456c0-4.4-3.6-8-8-8z'/></svg>";

        private readonly IReadOnlyList<ReadinessCheck> _checks;
        private readonly Dictionary<string, Row> _rows = new();
        private readonly CancellationTokenSource _cts = new();

        private AntdUI.Label _summary;
        private AntdUI.Button _closeButton;
        private bool _running;

        /// <summary>The labels one check owns, so an update repaints in place.</summary>
        private sealed record Row(AntdUI.Label Title, AntdUI.Label Detail);

        public NetworkCheckDialog(Form owner)
        {
            _checks = NetworkReadiness.CreateChecks();

            Text = "Kiểm tra mạng cho AoE2";
            StartPosition = owner == null ? FormStartPosition.CenterScreen
                                          : FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 10F);
            BackColor = UiTheme.PageBg;
            // Intro band, then the card, then the summary row - sized from the number of checks
            // rather than fixed, so adding a fifth one does not need a second number changed.
            ClientSize = new Size(580, 58 + (_checks.Count * RowHeight + 8) + 70);

            BuildLayout();
        }

        private void BuildLayout()
        {
            var intro = new AntdUI.Label
            {
                Text = "Đang kiểm tra và tự sửa những thứ hay chặn kết nối trong trận đấu. "
                     + "Việc này mất vài giây.",
                Location = new Point(16, 12),
                Size = new Size(CardWidth, 40),
                ForeColor = UiTheme.Muted,
                Font = new Font("Segoe UI", 9.5F),
                TextMultiLine = true,
                BackColor = Color.Transparent,
            };

            var card = new AntdUI.Panel
            {
                Location = new Point(14, 58),
                Size = new Size(CardWidth, _checks.Count * RowHeight + 8),
                Radius = 10,
                Shadow = 8,
                ShadowOpacity = 0.10F,
                ShadowOffsetY = 2,
                Back = Color.White,
                BorderWidth = 1,
                BorderColor = UiTheme.CardBorder,
            };

            int y = 8;
            foreach (ReadinessCheck check in _checks)
            {
                var title = new AntdUI.Label
                {
                    Text = check.Title,
                    PrefixSvg = IconRunning,
                    PrefixColor = UiTheme.Muted,
                    IconGap = 10,
                    Location = new Point(16, y),
                    Size = new Size(CardWidth - 32, 26),
                    ForeColor = UiTheme.Ink,
                    Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold),
                    BackColor = Color.Transparent,
                };

                var detail = new AntdUI.Label
                {
                    Text = check.Detail,
                    // Indented to the title's text rather than its icon, so the two read as one
                    // block instead of the detail hanging under the icon column.
                    Location = new Point(44, y + 26),
                    Size = new Size(CardWidth - 60, 40),
                    ForeColor = UiTheme.Muted,
                    Font = new Font("Segoe UI", 9F),
                    TextMultiLine = true,
                    BackColor = Color.Transparent,
                };

                card.Controls.Add(title);
                card.Controls.Add(detail);
                _rows[check.Id] = new Row(title, detail);
                y += RowHeight;
            }

            _summary = new AntdUI.Label
            {
                Text = "Đang chạy...",
                Location = new Point(16, card.Bottom + 12),
                Size = new Size(CardWidth - 130, 44),
                ForeColor = UiTheme.Muted,
                Font = new Font("Segoe UI", 9.5F),
                TextMultiLine = true,
                BackColor = Color.Transparent,
            };

            _closeButton = UiTheme.Toolbar("Đóng", new Point(CardWidth - 96, card.Bottom + 18), 110, 38);
            _closeButton.Enabled = false;
            _closeButton.Click += (s, e) => Close();

            Controls.Add(intro);
            Controls.Add(card);
            Controls.Add(_summary);
            Controls.Add(_closeButton);
            CancelButton = _closeButton;
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            await RunChecksAsync();
        }

        private async Task RunChecksAsync()
        {
            _running = true;
            try
            {
                await NetworkReadiness.RunAsync(
                    _checks,
                    check => UiUtils.InvokeOnUiThread(this, () => PaintRow(check), "NETWORK_CHECK_ROW"),
                    _cts.Token);
                ShowSummary();
            }
            catch (OperationCanceledException)
            {
                // The dialog was closed mid-run; nothing left to report to.
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"NetworkCheckDialog: the check run failed: {ex}");
                if (!IsDisposed)
                {
                    _summary.ForeColor = Bad;
                    _summary.Text = $"Kiểm tra không hoàn tất: {ex.Message}";
                }
            }
            finally
            {
                _running = false;
                if (!IsDisposed) _closeButton.Enabled = true;
            }
        }

        private void PaintRow(ReadinessCheck check)
        {
            if (IsDisposed || !_rows.TryGetValue(check.Id, out Row row)) return;

            row.Title.PrefixSvg = check.State switch
            {
                ReadinessState.Pass or ReadinessState.Repaired => IconGood,
                ReadinessState.Warn => IconAttention,
                ReadinessState.Fail => IconBad,
                _ => IconRunning,
            };
            row.Title.PrefixColor = ColorFor(check.State);

            // The advice is the whole point of an amber row, so it replaces the detail's second
            // line rather than being tucked away somewhere the player has to go looking.
            row.Detail.Text = string.IsNullOrWhiteSpace(check.Advice)
                ? check.Detail
                : $"{check.Detail} {check.Advice}";
            row.Detail.ForeColor = check.State == ReadinessState.Fail ? Bad : UiTheme.Muted;
        }

        private static Color ColorFor(ReadinessState state) => state switch
        {
            ReadinessState.Pass => Good,
            ReadinessState.Repaired => Changed,
            ReadinessState.Warn => Attention,
            ReadinessState.Fail => Bad,
            _ => UiTheme.Muted,
        };

        private void ShowSummary()
        {
            if (IsDisposed) return;

            int failed = _checks.Count(c => c.State == ReadinessState.Fail);
            int needsYou = _checks.Count(c => c.State == ReadinessState.Warn);
            int repaired = _checks.Count(c => c.State == ReadinessState.Repaired);

            if (failed > 0)
            {
                _summary.ForeColor = Bad;
                _summary.Text = $"Còn {failed} mục chưa sửa được. Hãy xem hướng dẫn ở trên.";
            }
            else if (needsYou > 0)
            {
                _summary.ForeColor = Attention;
                _summary.Text = $"Máy đã sẵn sàng, còn {needsYou} mục cần bạn tự làm theo hướng dẫn ở trên.";
            }
            else if (repaired > 0)
            {
                _summary.ForeColor = Changed;
                _summary.Text = $"Đã sửa {repaired} mục. Máy sẵn sàng để chơi.";
            }
            else
            {
                _summary.ForeColor = Good;
                _summary.Text = "Mọi thứ đã đúng từ trước. Máy sẵn sàng để chơi.";
            }
        }

        /// <summary>One line for the home screen's log, so the result is still there after the
        /// dialog is gone.</summary>
        public string ResultLine()
        {
            int failed = _checks.Count(c => c.State == ReadinessState.Fail);
            int needsYou = _checks.Count(c => c.State == ReadinessState.Warn);
            int repaired = _checks.Count(c => c.State == ReadinessState.Repaired);

            if (failed > 0) return $"[Mạng] Còn {failed} mục chưa sửa được - mở lại \"Kiểm tra mạng\" để xem chi tiết.";
            if (needsYou > 0) return $"[Mạng] Đã sửa xong phần tự động, còn {needsYou} mục cần bạn tự làm.";
            if (repaired > 0) return $"[Mạng] Đã sửa {repaired} mục, máy sẵn sàng để chơi.";
            return "[Mạng] Mạng đã sẵn sàng để chơi.";
        }

        /// <summary>The colour <see cref="ResultLine"/> should be printed in.</summary>
        public Color ResultColor()
        {
            if (_checks.Any(c => c.State == ReadinessState.Fail)) return Bad;
            if (_checks.Any(c => c.State == ReadinessState.Warn)) return Attention;
            return Good;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Closing mid-run cancels between checks rather than killing one in the middle: each
            // repair is a single netsh or service call, and abandoning the dialog should not
            // leave one of them half-applied.
            if (_running) _cts.Cancel();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _cts.Dispose();
            base.Dispose(disposing);
        }
    }
}
