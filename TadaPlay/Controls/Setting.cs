using System;
using System.Drawing;
using System.Windows.Forms;
using AntdUI;
using TadaPlay.Contexts.Interfaces;
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
        private const int ProfileCardHeight = 412;
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

            // Dock=Top stacks in REVERSE add order, so these go in bottom-up: the account card is
            // added first and ends up last. Game setup reads before account details.
            if (_appContext != null && _accountService != null)
            {
                Controls.Add(BuildProfileSection());
                totalHeight += ProfileCardHeight + CardGap;
            }

            if (_appContext != null)
            {
                Controls.Add(BuildDisplayModeSection());
                Controls.Add(BuildGameFolderSection());
                totalHeight += DisplayCardHeight + FolderCardHeight + CardGap * 2;
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

        // Profile editor: full name and optional password change, via the existing update-user
        // endpoint. The in-game display name is no longer a separate editable field - it's
        // always the account's username (see Home.startGameButton_Click / ProfileEnforceTimer_Tick).
        // The server always requires the current password to confirm any change, even if only
        // the name is being updated.
        private Control BuildProfileSection()
        {
            var currentUser = _appContext.GetCurrentUser();

            var card = UiTheme.Card(UiTheme.IconAccount, "Thông tin tài khoản",
                                         currentUser?.Username ?? string.Empty,
                                         UiTheme.AccentAccount, ProfileCardHeight,
                                         out System.Windows.Forms.Panel content);

            var fullNameBox = UiTheme.Field("Họ tên của bạn", UiTheme.IconAccount);
            fullNameBox.Text = currentUser?.FullName ?? string.Empty;

            var currentPasswordBox = UiTheme.Field("Bắt buộc để xác nhận thay đổi", password: true);
            var newPasswordBox = UiTheme.Field("Để trống nếu không đổi", password: true);
            var confirmPasswordBox = UiTheme.Field("Nhập lại mật khẩu mới", password: true);

            var saveButton = UiTheme.Primary("Lưu thông tin", UiTheme.AccentAccountStrong);
            saveButton.Click += async (s, e) =>
            {
                string fullName = fullNameBox.Text.Trim();
                string currentPassword = currentPasswordBox.Text;
                string newPassword = newPasswordBox.Text;
                string confirmPassword = confirmPasswordBox.Text;

                if (string.IsNullOrEmpty(fullName))
                {
                    AntdUI.Modal.open(new AntdUI.Modal.Config(form, "Lỗi", "Họ tên không được để trống.", AntdUI.TType.Warn)
                    { CancelText = null, OkText = "Đóng" });
                    return;
                }
                if (string.IsNullOrEmpty(currentPassword))
                {
                    AntdUI.Modal.open(new AntdUI.Modal.Config(form, "Lỗi", "Vui lòng nhập mật khẩu hiện tại để xác nhận thay đổi.", AntdUI.TType.Warn)
                    { CancelText = null, OkText = "Đóng" });
                    return;
                }
                if (newPassword != confirmPassword)
                {
                    AntdUI.Modal.open(new AntdUI.Modal.Config(form, "Lỗi", "Mật khẩu mới và xác nhận mật khẩu không khớp.", AntdUI.TType.Warn)
                    { CancelText = null, OkText = "Đóng" });
                    return;
                }
                if (newPassword.Length > 0 && newPassword.Length < 6)
                {
                    AntdUI.Modal.open(new AntdUI.Modal.Config(form, "Lỗi", "Mật khẩu mới phải có ít nhất 6 ký tự.", AntdUI.TType.Warn)
                    { CancelText = null, OkText = "Đóng" });
                    return;
                }

                try
                {
                    // The button carries the wait, so a slow request looks like it is working
                    // rather than like a click that did nothing.
                    saveButton.Loading = true;
                    // The in-game display name always mirrors the account's username now (no
                    // separate nickname field), so keep sending it through as nick_name for the
                    // server's existing update-user contract.
                    bool success = await _accountService.UpdateUserInfo(fullName, currentUser?.Username ?? string.Empty, currentPassword, newPassword);
                    if (success)
                    {
                        if (currentUser != null)
                        {
                            currentUser.FullName = fullName;
                            currentUser.NickName = currentUser.Username;
                            _appContext.SetCurrentUser(currentUser);
                        }
                        currentPasswordBox.Text = string.Empty;
                        newPasswordBox.Text = string.Empty;
                        confirmPasswordBox.Text = string.Empty;
                        AntdUI.Modal.open(new AntdUI.Modal.Config(form, "Thành công", "Thông tin tài khoản đã được cập nhật.", AntdUI.TType.Success)
                        { CancelText = null, OkText = "Đóng" });
                    }
                }
                catch (Exception ex)
                {
                    AntdUI.Modal.open(new AntdUI.Modal.Config(form, "Lỗi", ex.InnerException?.Message ?? ex.Message, AntdUI.TType.Error)
                    { CancelText = null, OkText = "Đóng" });
                }
                finally
                {
                    // The dialog can be closed while the request is still out, so this can run
                    // against a control that has already gone.
                    if (!saveButton.IsDisposed) saveButton.Loading = false;
                }
            };

            // Dock=Top stacks in reverse add order: add bottom-most control first.
            content.Controls.Add(saveButton);
            content.Controls.Add(UiTheme.Gap(10));
            content.Controls.Add(confirmPasswordBox);
            content.Controls.Add(UiTheme.Caption("Xác nhận mật khẩu mới"));
            content.Controls.Add(newPasswordBox);
            content.Controls.Add(UiTheme.Caption("Mật khẩu mới"));
            content.Controls.Add(currentPasswordBox);
            content.Controls.Add(UiTheme.Caption("Mật khẩu hiện tại (bắt buộc)"));
            content.Controls.Add(fullNameBox);
            content.Controls.Add(UiTheme.Caption("Họ tên"));
            return card;
        }
    }
}
