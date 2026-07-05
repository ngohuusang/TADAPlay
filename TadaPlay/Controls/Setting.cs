using System;
using System.Drawing;
using System.Windows.Forms;
using TadaPlay.Contexts.Interfaces;
using TadaPlay.Services.Interface;

namespace TadaPlay.Controls
{
    public partial class Setting : UserControl
    {
        Form form;
        private readonly IAppContext _appContext;
        private readonly IAccountService _accountService;

        public Setting(Form _form, IAppContext appContext = null, IAccountService accountService = null)
        {
            InitializeComponent();
            form = _form;
            _appContext = appContext;
            _accountService = accountService;

            this.Width = 420;
            this.AutoSize = true;
            this.AutoSizeMode = AutoSizeMode.GrowAndShrink;

            if (_appContext != null)
            {
                Controls.Add(BuildGameExecutableSection());
                Controls.Add(BuildGameFolderSection());
            }

            if (_appContext != null && _accountService != null)
            {
                Controls.Add(BuildProfileSection());
            }
        }

        // Game launcher picker: the exe Start Game should open (e.g. Voobly's own launcher,
        // not necessarily the raw age2_x1.exe). Persisted immediately.
        private GroupBox BuildGameExecutableSection()
        {
            var group = new GroupBox
            {
                Text = "Khởi chạy game",
                Dock = DockStyle.Top,
                Height = 95,
                Padding = new Padding(8)
            };

            var label = new Label
            {
                Text = "File chạy game khi bấm \"Bắt đầu\" (vd: Voobly.exe):",
                Dock = DockStyle.Top,
                Height = 20
            };

            var pathBox = new TextBox
            {
                Dock = DockStyle.Top,
                ReadOnly = true,
                Text = _appContext.GetGameExecutablePath() ?? string.Empty
            };

            var browseButton = new Button { Text = "Chọn file...", Dock = DockStyle.Top, Height = 28 };
            browseButton.Click += (s, e) =>
            {
                using var dialog = new OpenFileDialog
                {
                    Title = "Chọn file khởi chạy game",
                    Filter = "Chương trình (*.exe)|*.exe",
                    FileName = _appContext.GetGameExecutablePath() ?? string.Empty
                };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _appContext.SetGameExecutablePath(dialog.FileName);
                    pathBox.Text = dialog.FileName;
                }
            };

            group.Controls.Add(browseButton);
            group.Controls.Add(pathBox);
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
                Height = 95,
                Padding = new Padding(8)
            };

            var label = new Label
            {
                Text = "Thư mục cài đặt AoE2 (chứa SaveGame):",
                Dock = DockStyle.Top,
                Height = 20
            };

            var pathBox = new TextBox
            {
                Dock = DockStyle.Top,
                ReadOnly = true,
                Text = _appContext.GetGameFolder() ?? string.Empty
            };

            var browseButton = new Button { Text = "Chọn thư mục...", Dock = DockStyle.Top, Height = 28 };
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

            // Dock=Top within the same parent stacks in reverse add order: the group's
            // GroupBox padding puts the frame around everything, so add bottom-most first.
            group.Controls.Add(browseButton);
            group.Controls.Add(pathBox);
            group.Controls.Add(label);
            return group;
        }

        // Profile editor: full name, in-game display name (nick_name), and optional password
        // change, via the existing update-user endpoint. The server always requires the
        // current password to confirm any change, even if only the name is being updated.
        private GroupBox BuildProfileSection()
        {
            var currentUser = _appContext.GetCurrentUser();

            var group = new GroupBox
            {
                Text = "Thông tin tài khoản",
                Dock = DockStyle.Top,
                Height = 300,
                Padding = new Padding(8)
            };

            var fullNameLabel = new Label { Text = "Họ tên:", Dock = DockStyle.Top, Height = 20 };
            var fullNameBox = new TextBox { Dock = DockStyle.Top, Text = currentUser?.FullName ?? string.Empty };

            var nickNameLabel = new Label { Text = "Tên hiển thị trong game:", Dock = DockStyle.Top, Height = 20 };
            var nickNameBox = new TextBox { Dock = DockStyle.Top, Text = currentUser?.NickName ?? string.Empty };

            var currentPasswordLabel = new Label { Text = "Mật khẩu hiện tại (bắt buộc):", Dock = DockStyle.Top, Height = 20 };
            var currentPasswordBox = new TextBox { Dock = DockStyle.Top, PasswordChar = '*' };

            var newPasswordLabel = new Label { Text = "Mật khẩu mới (để trống nếu không đổi):", Dock = DockStyle.Top, Height = 20 };
            var newPasswordBox = new TextBox { Dock = DockStyle.Top, PasswordChar = '*' };

            var confirmPasswordLabel = new Label { Text = "Xác nhận mật khẩu mới:", Dock = DockStyle.Top, Height = 20 };
            var confirmPasswordBox = new TextBox { Dock = DockStyle.Top, PasswordChar = '*' };

            var saveButton = new Button { Text = "Lưu thông tin", Dock = DockStyle.Top, Height = 30 };
            saveButton.Click += async (s, e) =>
            {
                string fullName = fullNameBox.Text.Trim();
                string nickName = nickNameBox.Text.Trim();
                string currentPassword = currentPasswordBox.Text;
                string newPassword = newPasswordBox.Text;
                string confirmPassword = confirmPasswordBox.Text;

                if (string.IsNullOrEmpty(fullName) || string.IsNullOrEmpty(nickName))
                {
                    AntdUI.Modal.open(new AntdUI.Modal.Config(form, "Lỗi", "Họ tên và tên hiển thị không được để trống.", AntdUI.TType.Warn)
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
                    bool success = await _accountService.UpdateUserInfo(fullName, nickName, currentPassword, newPassword);
                    if (success)
                    {
                        if (currentUser != null)
                        {
                            currentUser.FullName = fullName;
                            currentUser.NickName = nickName;
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
            group.Controls.Add(nickNameBox);
            group.Controls.Add(nickNameLabel);
            group.Controls.Add(fullNameBox);
            group.Controls.Add(fullNameLabel);
            return group;
        }
    }
}
