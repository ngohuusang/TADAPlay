using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
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
        private TextBox _folderBox;
        private Button _browseButton;
        private Button _startButton;
        private Button _closeButton;
        private ProgressBar _progressBar;
        private Label _statusLabel;
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
            ClientSize = new Size(560, 300);
            Font = new Font("Segoe UI", 10F);

            BuildLayout();
        }

        private void BuildLayout()
        {
            var info = new Label
            {
                Text = "Tải bản cài đặt Age of Empires II từ máy chủ TADA và cài đặt sẵn sàng để chơi. " +
                       "Quá trình có thể mất vài phút tùy tốc độ mạng.",
                Location = new Point(16, 14),
                Size = new Size(528, 46),
            };

            var folderLabel = new Label { Text = "Thư mục cài đặt:", Location = new Point(16, 70), AutoSize = true };
            _folderBox = new TextBox
            {
                Location = new Point(16, 94),
                Width = 420,
                Text = GameDownloader.DefaultTargetFolder(),
            };
            _browseButton = new Button { Text = "Chọn...", Location = new Point(444, 92), Width = 100, Height = 28 };
            _browseButton.Click += Browse_Click;

            _progressBar = new ProgressBar
            {
                Location = new Point(16, 140),
                Size = new Size(528, 24),
                Style = ProgressBarStyle.Continuous,
                Minimum = 0,
                Maximum = 100,
            };

            _statusLabel = new Label
            {
                Location = new Point(16, 174),
                Size = new Size(528, 44),
                ForeColor = Color.DimGray,
                Text = "Sẵn sàng tải.",
            };

            _startButton = new Button { Text = "Tải và cài đặt", Location = new Point(16, 250), Width = 160, Height = 34 };
            _startButton.Click += Start_Click;

            _closeButton = new Button { Text = "Đóng", Location = new Point(444, 250), Width = 100, Height = 34 };
            _closeButton.Click += (s, e) => Close();

            Controls.Add(info);
            Controls.Add(folderLabel);
            Controls.Add(_folderBox);
            Controls.Add(_browseButton);
            Controls.Add(_progressBar);
            Controls.Add(_statusLabel);
            Controls.Add(_startButton);
            Controls.Add(_closeButton);
        }

        private void Browse_Click(object sender, EventArgs e)
        {
            using var dialog = new FolderBrowserDialog
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
                _statusLabel.ForeColor = Color.DimGray;
                _statusLabel.Text = "Đã hủy tải.";
                SetBusy(false);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"GameDownloadForm: install failed: {ex.Message}");
                _statusLabel.ForeColor = Color.Firebrick;
                _statusLabel.Text = "Tải thất bại.";
                MessageBox.Show(this, $"Tải game thất bại:\n{ex.Message}", "Lỗi",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetBusy(false);
            }
        }

        private void OnProgress(DownloadProgress p)
        {
            if (p.Percent < 0)
            {
                if (_progressBar.Style != ProgressBarStyle.Marquee) _progressBar.Style = ProgressBarStyle.Marquee;
            }
            else
            {
                if (_progressBar.Style != ProgressBarStyle.Continuous) _progressBar.Style = ProgressBarStyle.Continuous;
                _progressBar.Value = Math.Min(100, Math.Max(0, p.Percent));
            }
            _statusLabel.ForeColor = Color.DimGray;
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
