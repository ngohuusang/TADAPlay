using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using AntdUI;
using TadaPlay.Contexts.Interfaces;
using TadaPlay.Logger;
using TadaPlay.Services.Interface;
using TadaPlay.Utils;

namespace TadaPlay.Controls
{
    public partial class Setting : UserControl
    {
        // Shared across every label/textbox/button below so the whole dialog reads at one
        // consistent, larger size instead of the WinForms default ~9pt.
        private static readonly Font ControlFont = new Font("Segoe UI", 11F);

        // Card heights. Explicit rather than measured because the modal is sized from this
        // control's Height (see the note in the constructor), so the numbers have to be known
        // before anything is laid out.
        private const int DisplayCardHeight = 180;
        private const int FolderCardHeight = 272;
        private const int DiagnosticCardHeight = 220;
        private const int CardGap = 14;

        Form form;
        private readonly IAppContext _appContext;
        private readonly IAccountService _accountService;

        public Setting(Form _form, IAppContext appContext = null, IAccountService accountService = null)
        {
            InitializeComponent();
            form = _form;
            _appContext = appContext;
            _accountService = accountService;

            this.Font = ControlFont;
            this.Width = 600;
            this.BackColor = UiTheme.PageBg;
            // Even bottom padding: at 4 the last card's Save button sat almost on the edge of
            // the dialog, which reads as the screen having been cut off.
            this.Padding = new Padding(14, 14, 14, 16);
            // Deliberately NOT using AutoSize/GrowAndShrink here: a plain UserControl's default
            // layout engine doesn't compute preferred size from Dock=Top children (that's a
            // FlowLayoutPanel-only behavior), so AutoSize would just fight the explicit Height
            // set below and reset it back down once this control is re-parented into the modal
            // - which is exactly why the dialog was rendering as an empty box.
            this.AutoSize = false;

            int totalHeight = Padding.Vertical;

            // Dock=Top stacks in REVERSE add order, so the last card added is the one on top.
            // Account details are no longer here - they were about the person rather than this
            // machine's game setup, and sat below two cards nobody had to read to reach them.
            // See AccountInfo, opened from the name above the user list.
            if (_appContext != null)
            {
                // Added first, so it ends up last: the log switch is the card a player needs
                // least often, and only when someone has asked them for a log.
                Controls.Add(BuildDiagnosticSection());
                Controls.Add(BuildDisplayModeSection());
                Controls.Add(BuildGameFolderSection());
                totalHeight += DisplayCardHeight + FolderCardHeight + DiagnosticCardHeight
                               + CardGap * 3;
            }

            // AntdUI.Modal.open's (Form, string, object) overload does read control.Height
            // correctly to size the dialog - it just needs a real value here, which AutoSize
            // was never actually providing (see note above).
            this.Height = totalHeight;
        }

        // Display mode picker: which of the two app-bundled WK launcher exes (see
        // GameExecutablePreparer) gets copied into the game's age2_x1 folder and started on
        // "Bắt đầu" - there's no longer a free-form exe path to browse for, since the launcher
        // is always one of these two known, app-provided files. Persisted immediately.
        private Control BuildDisplayModeSection()
        {
            var card = UiTheme.Card(UiTheme.IconDisplay, "Chế độ hiển thị",
                                         "Giao diện khi khởi chạy game",
                                         UiTheme.AccentDisplay, DisplayCardHeight,
                                         out System.Windows.Forms.Panel content);

            string currentMode = _appContext.GetGameLaunchMode();
            bool isCenter = currentMode == GameExecutablePreparer.CenterMode;

            // A Segmented rather than two radio buttons: there are exactly two mutually exclusive
            // choices, and this shows both at once with the current one lit - which is what a
            // launcher setting should look like. Radios read as a form to be filled in.
            var modes = new AntdUI.Segmented
            {
                Dock = DockStyle.Top,
                Height = 52,
                Full = true,
                Round = true,
                Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold),
                Items =
                {
                    new SegmentedItem { Text = "Wide  ·  màn hình rộng" },
                    new SegmentedItem { Text = "Center  ·  căn giữa" },
                },
                SelectIndex = isCenter ? 1 : 0,
            };
            modes.SelectIndexChanged += (s, e) =>
            {
                _appContext.SetGameLaunchMode(modes.SelectIndex == 1
                    ? GameExecutablePreparer.CenterMode
                    : GameExecutablePreparer.WideMode);
            };

            var hint = new AntdUI.Label
            {
                Dock = DockStyle.Top,
                Height = 36,
                Text = "Wide dùng toàn bộ chiều ngang màn hình. Center giữ khung game ở giữa.",
                ForeColor = UiTheme.Muted,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                TextMultiLine = true,
            };

            // Dock=Top stacks in reverse add order: add bottom-most first.
            content.Controls.Add(hint);
            content.Controls.Add(modes);
            return card;
        }

        /// <summary>
        /// The debug-log switch.
        ///
        /// The app used to write Log\tadaplay.log unconditionally on every machine, every
        /// session. Almost nobody ever reads one, so it was pure cost: a file that grew all
        /// evening for no reader. It is now off unless it is switched on here, which is the
        /// only time it is worth anything - when something has gone wrong and someone has asked
        /// for it.
        ///
        /// The button beside it opens the folder, because a player who has just been asked for
        /// their log should not also have to be told where the app was installed.
        /// </summary>
        private Control BuildDiagnosticSection()
        {
            var card = UiTheme.Card(UiTheme.IconLog, "Nhật ký gỡ lỗi",
                                    "Chỉ bật khi cần hỗ trợ",
                                    UiTheme.AccentDiagnostic, DiagnosticCardHeight,
                                    out System.Windows.Forms.Panel content);

            // A compact row rather than a docked switch: Dock=Top stretches an AntdUI.Switch
            // across the full width, which reads as a grey bar rather than a toggle.
            var row = new System.Windows.Forms.Panel
            {
                Dock = DockStyle.Top,
                Height = 42,
                BackColor = Color.Transparent,
            };

            var toggle = new AntdUI.Switch
            {
                Size = new Size(44, 24),
                Location = new Point(0, 9),
                Checked = _appContext.GetDebugLogSetting(),
            };

            var caption = new AntdUI.Label
            {
                Text = "Ghi nhật ký ra file",
                Font = new Font("Segoe UI", 11F),
                ForeColor = UiTheme.Ink,
                Location = new Point(56, 10),
                Size = new Size(420, 24),
                BackColor = Color.Transparent,
            };

            var openButton = UiTheme.Quiet("Mở thư mục nhật ký", UiTheme.IconLog);
            openButton.Enabled = Directory.Exists(DebugLogger.LogFolder);
            openButton.Click += (s, e) =>
            {
                try
                {
                    if (!Directory.Exists(DebugLogger.LogFolder)) return;
                    // UseShellExecute so this hands the path to Explorer rather than trying to
                    // run it - a directory is not an executable.
                    Process.Start(new ProcessStartInfo(DebugLogger.LogFolder)
                    { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    AntdUI.Modal.open(new AntdUI.Modal.Config(form, "Lỗi",
                        $"Không mở được thư mục nhật ký: {ex.Message}", AntdUI.TType.Error)
                    { CancelText = null, OkText = "Đóng" });
                }
            };

            // AntdUI's Switch animates, and CheckedChanged fires off the back of that animation,
            // after construction has returned - so a "still building" flag does not catch it.
            // Only a real click counts as intent (same guard as the spectate switch in Home).
            bool userToggled = false;
            toggle.MouseDown += (s, e) => userToggled = true;
            toggle.CheckedChanged += (s, e) =>
            {
                if (!userToggled) return;
                if (toggle.Checked == _appContext.GetDebugLogSetting()) return;

                _appContext.SetDebugLogSetting(toggle.Checked);
                // Applied now rather than at the next start: someone reproducing a problem
                // needs the log for the run they are about to do, not the one after it.
                DebugLogger.SetFileLogging(toggle.Checked);
                if (toggle.Checked) DebugLogger.Info("Debug log switched on from Cài đặt.");
                openButton.Enabled = Directory.Exists(DebugLogger.LogFolder);
            };

            row.Controls.Add(caption);
            row.Controls.Add(toggle);

            var hint = new AntdUI.Label
            {
                Dock = DockStyle.Top,
                Height = 52,
                Text = "Mặc định tắt. Bật để ghi lại hoạt động của ứng dụng vào file, "
                     + "rồi gửi file cho quản trị viên khi cần hỗ trợ.",
                ForeColor = UiTheme.Muted,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                TextMultiLine = true,
            };

            // Dock=Top stacks in reverse add order: add bottom-most first.
            content.Controls.Add(openButton);
            content.Controls.Add(UiTheme.Gap(10));
            content.Controls.Add(hint);
            content.Controls.Add(row);
            return card;
        }

        // Game folder picker: where AoE2 saves recorded games (.mgz). Persisted immediately.
        private Control BuildGameFolderSection()
        {
            var card = UiTheme.Card(UiTheme.IconFolder, "Thư mục game",
                                         "Nơi cài đặt AoE2",
                                         UiTheme.AccentFolder, FolderCardHeight,
                                         out System.Windows.Forms.Panel content);

            var pathBox = UiTheme.Field("Chưa chọn thư mục", UiTheme.IconFolder);
            pathBox.ReadOnly = true;
            pathBox.Text = _appContext.GetGameFolder() ?? string.Empty;

            var browseButton = UiTheme.Primary("Chọn thư mục...", UiTheme.AccentFolder);
            browseButton.Click += (s, e) =>
            {
                // Qualified: AntdUI ships a FolderBrowserDialog of its own, and this is the
                // system one the picker has always used.
                using var dialog = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "Chọn thư mục cài đặt AoE2 (nơi chứa thư mục SaveGame).",
                    SelectedPath = _appContext.GetGameFolder() ?? string.Empty
                };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _appContext.SetGameFolder(dialog.SelectedPath);
                    pathBox.Text = dialog.SelectedPath;
                }
            };

            // Opens the in-app hotkey editor for the currently-configured game folder, so players
            // can rebind keys / import a .hki layout without going into the game's own options menu.
            var hotkeyButton = UiTheme.Quiet("Chỉnh sửa phím tắt trong game...");
            hotkeyButton.Click += (s, e) =>
            {
                string folder = _appContext.GetGameFolder();
                if (string.IsNullOrWhiteSpace(folder) || !System.IO.Directory.Exists(folder))
                {
                    AntdUI.Modal.open(new AntdUI.Modal.Config(form, "Chưa có thư mục game",
                        "Hãy chọn thư mục cài đặt AoE2 trước khi chỉnh sửa phím tắt.", AntdUI.TType.Warn)
                    { CancelText = null, OkText = "Đóng" });
                    return;
                }
                // The account service goes through so the editor can offer the backups; without
                // it the two backup buttons are disabled.
                using var editor = new HotkeyEditorForm(folder, _accountService);
                editor.ShowDialog(form);
            };

            // Downloads + installs the game from the TADA server and points the game folder at it,
            // so a new user can get ready to play without hunting for an existing AoE2 install.
            var downloadButton = UiTheme.Quiet("Tải game về máy...");
            downloadButton.Click += (s, e) =>
            {
                using var downloader = new GameDownloadForm(_appContext);
                if (downloader.ShowDialog(form) == DialogResult.OK &&
                    !string.IsNullOrWhiteSpace(downloader.InstalledGameFolder))
                {
                    pathBox.Text = downloader.InstalledGameFolder;
                }
            };

            // Dock=Top within the same parent stacks in reverse add order: add bottom-most first.
            content.Controls.Add(hotkeyButton);
            content.Controls.Add(UiTheme.Gap(8));
            content.Controls.Add(downloadButton);
            content.Controls.Add(UiTheme.Gap(8));
            content.Controls.Add(browseButton);
            content.Controls.Add(UiTheme.Gap(10));
            content.Controls.Add(pathBox);
            return card;
        }
    }
}
