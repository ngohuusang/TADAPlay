using System;
using System.Drawing;
using System.Windows.Forms;
using TadaPlay.Contexts.Interfaces;
using TadaPlay.Services.Interface;
using TadaPlay.Utils;

namespace TadaPlay.Controls
{
    public partial class Setting : UserControl
    {
        // Shared across every label/textbox/button/groupbox below so the whole dialog reads at
        // one consistent, larger size instead of the WinForms default ~9pt.
        private static readonly Font ControlFont = new Font("Segoe UI", 11F);

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
            this.Width = 560;
            // Deliberately NOT using AutoSize/GrowAndShrink here: a plain UserControl's default
            // layout engine doesn't compute preferred size from Dock=Top children (that's a
            // FlowLayoutPanel-only behavior), so AutoSize would just fight the explicit Height
            // set below and reset it back down once this control is re-parented into the modal
            // - which is exactly why the dialog was rendering as an empty box.
            this.AutoSize = false;

            int totalHeight = 0;

            if (_appContext != null)
            {
                var displayModeSection = BuildDisplayModeSection();
                var folderSection = BuildGameFolderSection();
                Controls.Add(displayModeSection);
                Controls.Add(folderSection);
                totalHeight += displayModeSection.Height + folderSection.Height;
            }

            if (_appContext != null && _accountService != null)
            {
                var profileSection = BuildProfileSection();
                Controls.Add(profileSection);
                totalHeight += profileSection.Height;
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
        private GroupBox BuildDisplayModeSection()
        {
            var group = new GroupBox
            {
                Text = "Chế độ hiển thị",
                Dock = DockStyle.Top,
                Height = 120,
                Padding = new Padding(10)
            };

            var label = new Label
            {
                Text = "Giao diện khi khởi chạy game:",
                Dock = DockStyle.Top,
                Height = 32
            };

            string currentMode = _appContext.GetGameLaunchMode();

            var wideRadio = new RadioButton
            {
                Text = "Wide (màn hình rộng)",
                Dock = DockStyle.Top,
                Height = 28,
                Checked = currentMode != GameExecutablePreparer.CenterMode
            };
            var centerRadio = new RadioButton
            {
                Text = "Center (căn giữa)",
                Dock = DockStyle.Top,
                Height = 28,
                Checked = currentMode == GameExecutablePreparer.CenterMode
            };

            centerRadio.CheckedChanged += (s, e) =>
            {
                if (centerRadio.Checked) _appContext.SetGameLaunchMode(GameExecutablePreparer.CenterMode);
            };
            wideRadio.CheckedChanged += (s, e) =>
            {
                if (wideRadio.Checked) _appContext.SetGameLaunchMode(GameExecutablePreparer.WideMode);
            };

            // Dock=Top stacks in reverse add order: add bottom-most control first.
            group.Controls.Add(centerRadio);
            group.Controls.Add(wideRadio);
            group.Controls.Add(label);
            return group;
        }

        // Game folder picker: where AoE2 saves recorded games (.mgz). Persisted immediately.
        private GroupBox BuildGameFolderSection()
        {
            var group = new GroupBox
            {
                Text = "Thư mục game",
                Dock = DockStyle.Top,
                Height = 244,
                Padding = new Padding(10)
            };

            var label = new Label
            {
                Text = "Thư mục cài đặt AoE2:",
                Dock = DockStyle.Top,
                Height = 32
            };

            var pathBox = new TextBox
            {
                Dock = DockStyle.Top,
                ReadOnly = true,
                Text = _appContext.GetGameFolder() ?? string.Empty
            };

            var browseButton = new Button { Text = "Chọn thư mục...", Dock = DockStyle.Top, Height = 38 };
            browseButton.Click += (s, e) =>
            {
                using var dialog = new FolderBrowserDialog
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
            var hotkeyButton = new Button { Text = "Chỉnh sửa phím tắt trong game...", Dock = DockStyle.Top, Height = 38 };
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
                using var editor = new HotkeyEditorForm(folder);
                editor.ShowDialog(form);
            };

            // Downloads + installs the game from the TADA server and points the game folder at it,
            // so a new user can get ready to play without hunting for an existing AoE2 install.
            var downloadButton = new Button { Text = "Tải game về máy...", Dock = DockStyle.Top, Height = 38 };
            downloadButton.Click += (s, e) =>
            {
                using var downloader = new GameDownloadForm(_appContext);
                if (downloader.ShowDialog(form) == DialogResult.OK &&
                    !string.IsNullOrWhiteSpace(downloader.InstalledGameFolder))
                {
                    pathBox.Text = downloader.InstalledGameFolder;
                }
            };

            // Dock=Top within the same parent stacks in reverse add order: the group's
            // GroupBox padding puts the frame around everything, so add bottom-most first.
            group.Controls.Add(hotkeyButton);
            group.Controls.Add(downloadButton);
            group.Controls.Add(browseButton);
            group.Controls.Add(pathBox);
            group.Controls.Add(label);
            return group;
        }

        // Profile editor: full name and optional password change, via the existing update-user
        // endpoint. The in-game display name is no longer a separate editable field - it's
        // always the account's username (see Home.startGameButton_Click / ProfileEnforceTimer_Tick).
        // The server always requires the current password to confirm any change, even if only
        // the name is being updated.
        private GroupBox BuildProfileSection()
        {
            var currentUser = _appContext.GetCurrentUser();

            var group = new GroupBox
            {
                Text = "Thông tin tài khoản",
                Dock = DockStyle.Top,
                Height = 366,
                Padding = new Padding(10)
            };

            var fullNameLabel = new Label { Text = "Họ tên:", Dock = DockStyle.Top, Height = 32 };
            var fullNameBox = new TextBox { Dock = DockStyle.Top, Text = currentUser?.FullName ?? string.Empty };

            var currentPasswordLabel = new Label { Text = "Mật khẩu hiện tại (bắt buộc):", Dock = DockStyle.Top, Height = 32 };
            var currentPasswordBox = new TextBox { Dock = DockStyle.Top, PasswordChar = '*' };

            var newPasswordLabel = new Label { Text = "Mật khẩu mới (để trống nếu không đổi):", Dock = DockStyle.Top, Height = 32 };
            var newPasswordBox = new TextBox { Dock = DockStyle.Top, PasswordChar = '*' };

            var confirmPasswordLabel = new Label { Text = "Xác nhận mật khẩu mới:", Dock = DockStyle.Top, Height = 32 };
            var confirmPasswordBox = new TextBox { Dock = DockStyle.Top, PasswordChar = '*' };

            var saveButton = new Button { Text = "Lưu thông tin", Dock = DockStyle.Top, Height = 40 };
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
            };

            // Dock=Top stacks in reverse add order: add bottom-most control first.
            group.Controls.Add(saveButton);
            group.Controls.Add(confirmPasswordBox);
            group.Controls.Add(confirmPasswordLabel);
            group.Controls.Add(newPasswordBox);
            group.Controls.Add(newPasswordLabel);
            group.Controls.Add(currentPasswordBox);
            group.Controls.Add(currentPasswordLabel);
            group.Controls.Add(fullNameBox);
            group.Controls.Add(fullNameLabel);
            return group;
        }
    }
}
