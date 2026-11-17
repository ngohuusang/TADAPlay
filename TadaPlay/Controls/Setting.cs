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
        // Every card is 80 of chrome (a 56 title band plus 12/12 content padding) and then its
        // own content, so the numbers below are that 80 plus what each one actually holds.
        // Kept tight on purpose: four roomy cards came to 914 pixels in a window that can show
        // about 550, so the dialog scrolled to reach settings that fit on one screen if the
        // space is not spent on explanations of switches that explain themselves.
        private const int FolderCardHeight = 176;      // path field 40, gap 8, a row of buttons 48
        private const int DisplayCardHeight = 132;     // one 52 segmented control
        private const int OptionsCardHeight = 172;     // two 42 switch rows, plus 8 so the lower
                                                       // one is not flush with the card's edge
        private const int CardGap = 14;

        /// <summary>
        /// What the modal puts around the content: its title bar and its Cancel/OK row. Measured
        /// off the rendered dialog (about 111 pixels) with a little in hand, rather than guessed -
        /// the first guess of 170 was pessimistic enough to force a scrollbar onto a dialog that
        /// fits.
        /// </summary>
        private const int ModalChrome = 150;

        /// <summary>Never shrink below this, even in a very small window - it would show nothing useful.</summary>
        private const int MinimumDialogHeight = 420;

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
            // The bottom is deliberately deeper than the top: below it sits the modal's own
            // Cancel/OK row, and at 16 the last card ran straight into it.
            this.Padding = new Padding(14, 14, 14, 28);
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
                Controls.Add(BuildOptionsSection());
                Controls.Add(BuildDisplayModeSection());
                Controls.Add(BuildGameFolderSection());
                totalHeight += FolderCardHeight + DisplayCardHeight + OptionsCardHeight
                               + CardGap * 3;
            }

            // AntdUI.Modal.open's (Form, string, object) overload does read control.Height
            // correctly to size the dialog - it just needs a real value here, which AutoSize
            // was never actually providing (see note above).
            //
            // But only up to what the window can actually show. The dialog is sized from this
            // number with no upper bound of its own, so a tall one is simply cut off at the
            // bottom: the last card loses its padding and the card above it loses whatever sat
            // at its bottom edge - which is exactly what adding a fourth card did. Past that
            // point the cards scroll instead, so a section can never become unreachable, on this
            // window or on a smaller screen.
            int visible = (form?.ClientSize.Height ?? 720) - ModalChrome;
            if (totalHeight > visible)
            {
                this.AutoScroll = true;
                this.Height = Math.Max(MinimumDialogHeight, visible);
            }
            else
            {
                this.Height = totalHeight;
            }
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

            // No explanatory line under it: the two options already say "màn hình rộng" and
            // "căn giữa" on their own faces, and a sentence repeating that cost 36 pixels of a
            // dialog that had run out of them.
            content.Controls.Add(modes);
            return card;
        }

        /// <summary>
        /// The two switches that used to be a card each: launching with Windows, and the debug log.
        ///
        /// Together now because they are the same shape of thing - one line, one switch - and two
        /// cards spent 160 pixels of chrome to say that twice. Their explanatory paragraphs went
        /// with them: a switch reading "Ghi nhật ký ra file" does not need a sentence below it
        /// saying that it writes a log to a file.
        /// </summary>
        private Control BuildOptionsSection()
        {
            var card = UiTheme.Card(UiTheme.IconLog, "Tùy chọn",
                                    "Khởi động máy và nhật ký",
                                    UiTheme.AccentDiagnostic, OptionsCardHeight,
                                    out System.Windows.Forms.Panel content);

            var openLogButton = UiTheme.Quiet("Mở thư mục nhật ký", UiTheme.IconLog, height: 32);
            openLogButton.Enabled = Directory.Exists(DebugLogger.LogFolder);
            openLogButton.Click += (s, e) =>
            {
                try
                {
                    if (!Directory.Exists(DebugLogger.LogFolder)) return;
                    // UseShellExecute so this hands the path to Explorer rather than trying to
                    // run it - a directory is not an executable.
                    Process.Start(new ProcessStartInfo(DebugLogger.LogFolder) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    AntdUI.Modal.open(new AntdUI.Modal.Config(form, "Lỗi",
                        $"Không mở được thư mục nhật ký: {ex.Message}", AntdUI.TType.Error)
                    { CancelText = null, OkText = "Đóng" });
                }
            };

            // The log's folder button sits on the log's own row, where a paragraph used to be.
            Control logRow = SwitchRow("Ghi nhật ký ra file", 250,
                _appContext.GetDebugLogSetting,
                on =>
                {
                    _appContext.SetDebugLogSetting(on);
                    // Applied now rather than at the next start: someone reproducing a problem
                    // needs the log for the run they are about to do, not the one after it.
                    DebugLogger.SetFileLogging(on);
                    if (on) DebugLogger.Info("Debug log switched on from Cài đặt.");
                    openLogButton.Enabled = Directory.Exists(DebugLogger.LogFolder);
                });
            logRow.Controls.Add(UiTheme.ButtonColumn(openLogButton, DockStyle.Right, 180, top: 5, gap: 0));

            // Off takes effect at once - the scheduled task is removed there and then, not at the
            // next launch - so a player who does not want this app starting with their computer
            // can see it gone rather than take it on trust.
            Control startupRow = SwitchRow("Mở cùng Windows (chạy ẩn trong khay hệ thống)", 430,
                _appContext.GetStartWithWindowsSetting,
                on =>
                {
                    _appContext.SetStartWithWindowsSetting(on);
                    DebugLogger.Info($"Start with Windows switched {(on ? "on" : "off")} from Cài đặt.");
                });

            // Dock=Top stacks in reverse add order: add bottom-most first.
            content.Controls.Add(logRow);
            content.Controls.Add(startupRow);
            return card;
        }

        /// <summary>
        /// One line carrying a switch. The current value is read through <paramref name="read"/>
        /// each time rather than captured once, so a row can never act on a stale copy of it.
        /// </summary>
        private static Control SwitchRow(string caption, int captionWidth,
                                         Func<bool> read, Action<bool> write)
        {
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
                Checked = read(),
            };

            var label = new AntdUI.Label
            {
                Text = caption,
                Font = new Font("Segoe UI", 11F),
                ForeColor = UiTheme.Ink,
                Location = new Point(56, 10),
                Size = new Size(captionWidth, 24),
                BackColor = Color.Transparent,
            };

            // AntdUI's Switch animates, and CheckedChanged fires off the back of that animation,
            // after construction has returned - so a "still building" flag does not catch it.
            // Only a real click counts as intent (same guard as the spectate switch in Home).
            bool userToggled = false;
            toggle.MouseDown += (s, e) => userToggled = true;
            toggle.CheckedChanged += (s, e) =>
            {
                if (!userToggled) return;
                if (toggle.Checked == read()) return;
                write(toggle.Checked);
            };

            row.Controls.Add(label);
            row.Controls.Add(toggle);
            return row;
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
                    Description = "Chọn thư mục cài đặt AoE2 (nơi chứa thư mục age2_x1).",
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
            var hotkeyButton = UiTheme.Quiet("Phím tắt trong game...");
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

            // The three actions side by side rather than stacked: they are alternatives to each
            // other - point at an install, fetch one, or edit its keys - and stacked they cost
            // three rows plus their gaps, over a hundred pixels, to offer three short buttons.
            var actions = new System.Windows.Forms.Panel
            {
                Dock = DockStyle.Top,
                Height = 48,
                BackColor = Color.Transparent,
            };
            // Added right to left: the layout docks the most recently added control first, so the
            // last one added ends up leftmost.
            actions.Controls.Add(UiTheme.ButtonColumn(hotkeyButton, DockStyle.Left, 178, top: 4, gap: 0));
            actions.Controls.Add(UiTheme.ButtonColumn(downloadButton, DockStyle.Left, 176, top: 4));
            actions.Controls.Add(UiTheme.ButtonColumn(browseButton, DockStyle.Left, 186, top: 4));

            // Dock=Top within the same parent stacks in reverse add order: add bottom-most first.
            content.Controls.Add(actions);
            content.Controls.Add(UiTheme.Gap(8));
            content.Controls.Add(pathBox);
            return card;
        }
    }
}
