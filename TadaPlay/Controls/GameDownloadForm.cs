using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using AntdUI;
using TadaPlay.Contexts.Interfaces;
using TadaPlay.Logger;
using TadaPlay.Utils;

namespace TadaPlay.Controls
{
    /// <summary>
    /// Downloads the AoE2 game archive, shows download/extract progress, then configures the app's
    /// game folder to the installed location so the game is ready to launch - no manual folder pick.
    /// </summary>
    public class GameDownloadForm : Form
    {
        private readonly IAppContext _appContext;
        private AntdUI.Input _folderBox;
        private AntdUI.Button _browseButton;
        private AntdUI.Button _startButton;
        private AntdUI.Button _closeButton;
        private AntdUI.Progress _progressBar;
        private AntdUI.Label _statusLabel;
        private CancellationTokenSource _cts;
        private bool _busy;

        /// <summary>Game root that was installed and configured, or null if the user closed without installing.</summary>
        public string InstalledGameFolder { get; private set; }

        public GameDownloadForm(IAppContext appContext)
        {
            _appContext = appContext;

            Text = "Tải game về máy";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(580, 316);
            Font = new Font("Segoe UI", 10F);
            BackColor = UiTheme.PageBg;

            BuildLayout();
        }

        private void BuildLayout()
        {
            var card = new AntdUI.Panel
            {
                Location = new Point(14, 14),
                Size = new Size(552, 222),
                Radius = 10,
                Shadow = 8,
                ShadowOpacity = 0.10F,
                Back = Color.White,
                BorderWidth = 1,
                BorderColor = UiTheme.CardBorder,
            };

            var info = new AntdUI.Label
            {
                Text = "Tải bản cài đặt Age of Empires II từ máy chủ TADA và cài đặt sẵn sàng để chơi. " +
                       "Quá trình có thể mất vài phút tùy tốc độ mạng.",
                Location = new Point(16, 14),
                Size = new Size(520, 44),
                ForeColor = UiTheme.Muted,
                Font = new Font("Segoe UI", 9.5F),
                TextMultiLine = true,
                BackColor = Color.Transparent,
            };

            var folderLabel = new AntdUI.Label
            {
                Text = "Thư mục cài đặt",
                Location = new Point(16, 62),
                Size = new Size(200, 22),
                ForeColor = UiTheme.Muted,
                Font = new Font("Segoe UI", 9.5F),
                BackColor = Color.Transparent,
            };
            _folderBox = new AntdUI.Input
            {
                Location = new Point(16, 86),
                Size = new Size(410, 38),
                Radius = 8,
                PrefixSvg = UiTheme.IconFolder,
                Text = GameDownloader.DefaultTargetFolder(),
                Font = new Font("Segoe UI", 10F),
            };
            _browseButton = UiTheme.Toolbar("Chọn...", new Point(436, 86), 100, 38);
            _browseButton.Click += Browse_Click;

            _progressBar = new AntdUI.Progress
            {
                Location = new Point(16, 140),
                Size = new Size(520, 24),
                Radius = 6,
                Value = 0F,
            };

            _statusLabel = new AntdUI.Label
            {
                Location = new Point(16, 170),
                Size = new Size(520, 40),
                ForeColor = UiTheme.Muted,
                Font = new Font("Segoe UI", 9.5F),
                Text = "Sẵn sàng tải.",
                TextMultiLine = true,
                BackColor = Color.Transparent,
            };

            card.Controls.Add(info);
            card.Controls.Add(folderLabel);
            card.Controls.Add(_folderBox);
            card.Controls.Add(_browseButton);
            card.Controls.Add(_progressBar);
            card.Controls.Add(_statusLabel);

            _startButton = UiTheme.Primary("Tải và cài đặt", UiTheme.AccentFolder, height: 44);
            _startButton.Dock = DockStyle.None;
            _startButton.Location = new Point(14, 252);
            _startButton.Size = new Size(180, 44);
            _startButton.Click += Start_Click;

            _closeButton = UiTheme.Quiet("Đóng", height: 44);
            _closeButton.Dock = DockStyle.None;
            _closeButton.Location = new Point(456, 252);
            _closeButton.Size = new Size(110, 44);
            _closeButton.Click += (s, e) => Close();

            Controls.Add(card);
            Controls.Add(_startButton);
            Controls.Add(_closeButton);
        }

        private void Browse_Click(object sender, EventArgs e)
        {
            // Qualified: AntdUI ships one of its own, and this is the system picker this has
            // always used.
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Chọn thư mục để tải và cài đặt game.",
                SelectedPath = Directory.Exists(_folderBox.Text) ? _folderBox.Text : GameDownloader.DefaultTargetFolder(),
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _folderBox.Text = dialog.SelectedPath;
            }
        }

        private async void Start_Click(object sender, EventArgs e)
        {
            string targetFolder = _folderBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(targetFolder))
            {
                MessageBox.Show(this, "Hãy chọn thư mục cài đặt.", "Thiếu thông tin",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetBusy(true);
            _cts = new CancellationTokenSource();
            var progress = new Progress<DownloadProgress>(OnProgress);

            try
            {
                string gameRoot = await GameDownloader.DownloadAndInstallAsync(targetFolder, progress, _cts.Token);

                _appContext.SetGameFolder(gameRoot);
                InstalledGameFolder = gameRoot;

                // Install finished - clear busy BEFORE anything can trigger a close, so OnFormClosing
                // doesn't mistake this for an in-progress download and prompt to cancel.
                _busy = false;
                _progressBar.Value = 100;
                _statusLabel.ForeColor = Color.ForestGreen;
                _statusLabel.Text = $"Đã cài đặt và cấu hình game tại:\n{gameRoot}";
                _startButton.Text = "Hoàn tất";
                _closeButton.Text = "Xong";

                MessageBox.Show(this, "Đã tải và cài đặt game thành công. Bạn có thể bắt đầu chơi.",
                    "Hoàn tất", MessageBoxButtons.OK, MessageBoxIcon.Information);

                // Return OK to the caller (Settings refreshes its game-folder display); this also closes
                // the dialog - and since _busy is now false, closing won't prompt.
                DialogResult = DialogResult.OK;
            }
            catch (OperationCanceledException)
            {
                _statusLabel.ForeColor = UiTheme.Muted;
                _statusLabel.Text = "Đã hủy tải.";
                _progressBar.Loading = false;
                SetBusy(false);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"GameDownloadForm: install failed: {ex.Message}");
                _statusLabel.ForeColor = Color.Firebrick;
                _statusLabel.Text = "Tải thất bại.";
                // Colours the bar red where it stopped, so the failure is visible even after
                // the message has been read and forgotten.
                _progressBar.Loading = false;
                _progressBar.State = TType.Error;
                MessageBox.Show(this, $"Tải game thất bại:\n{ex.Message}", "Lỗi",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetBusy(false);
            }
        }

        private void OnProgress(DownloadProgress p)
        {
            if (p.Percent < 0)
            {
                // Antd has no marquee; Loading is its indeterminate state.
                _progressBar.Loading = true;
            }
            else
            {
                _progressBar.Loading = false;
                // Antd takes a 0..1 ratio where the WinForms bar took 0..100.
                _progressBar.Value = Math.Min(100, Math.Max(0, p.Percent)) / 100F;
            }
            _statusLabel.ForeColor = UiTheme.Muted;
            _statusLabel.Text = p.Detail;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _folderBox.Enabled = !busy;
            _browseButton.Enabled = !busy;
            _startButton.Enabled = !busy;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy)
            {
                // A download is in progress - confirm, then cancel it before closing.
                var r = MessageBox.Show(this, "Đang tải game. Hủy và đóng?", "Đang tải",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r == DialogResult.No) { e.Cancel = true; return; }
                _cts?.Cancel();
            }
            base.OnFormClosing(e);
        }
    }
}
