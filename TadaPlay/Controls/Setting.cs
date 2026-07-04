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

        public bool Animation, ShadowEnabled, ShowInWindow, ScrollBarHide, TextRenderingHighQuality;
        public Setting(Form _form, IAppContext appContext = null, IAccountService accountService = null)
        {
            InitializeComponent();
            form = _form;
            _appContext = appContext;
            _accountService = accountService;
            switch1.Checked = Animation = AntdUI.Config.Animation;
            switch2.Checked = ShadowEnabled = AntdUI.Config.ShadowEnabled;
            switch3.Checked = ShowInWindow = AntdUI.Config.ShowInWindow;
            switch4.Checked = ScrollBarHide = AntdUI.Config.ScrollBarHide;
            switch5.Checked = TextRenderingHighQuality = AntdUI.Config.TextRenderingHighQuality;

            switch1.CheckedChanged += (s, e) =>
            {
                Animation = e.Value;
            };

            switch2.CheckedChanged += (s, e) =>
            {
                ShadowEnabled = e.Value;
            };

            switch3.CheckedChanged += (s, e) =>
            {
                ShowInWindow = e.Value;
            };

            switch4.CheckedChanged += (s, e) =>
            {
                ScrollBarHide = e.Value;
            };

            switch5.CheckedChanged += (s, e) =>
            {
                TextRenderingHighQuality = e.Value;
            };

            if (_appContext != null)
            {
                BuildGameFolderRow();
            }

            if (_appContext != null && _accountService != null)
            {
                BuildProfileSection();
            }
        }

        // Profile editor: full name, in-game display name (nick_name), and optional password
        // change, via the existing update-user endpoint. The server always requires the
        // current password to confirm any change, even if only the name is being updated.
        private void BuildProfileSection()
        {
            this.Height = Math.Max(this.Height, 620);

            var currentUser = _appContext.GetCurrentUser();

            var panel = new Panel { Dock = DockStyle.Bottom, Height = 260, Padding = new Padding(3, 8, 3, 3) };

            var title = new Label
            {
                Text = "Thông tin tài khoản",
                Dock = DockStyle.Top,
                Height = 22,
                Font = new Font("Segoe UI", 9.75F, FontStyle.Bold)
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

            // Docked Top stacks in reverse add order: add bottom-most control first.
            panel.Controls.Add(saveButton);
            panel.Controls.Add(confirmPasswordBox);
            panel.Controls.Add(confirmPasswordLabel);
            panel.Controls.Add(newPasswordBox);
            panel.Controls.Add(newPasswordLabel);
            panel.Controls.Add(currentPasswordBox);
            panel.Controls.Add(currentPasswordLabel);
            panel.Controls.Add(nickNameBox);
            panel.Controls.Add(nickNameLabel);
            panel.Controls.Add(fullNameBox);
            panel.Controls.Add(fullNameLabel);
            panel.Controls.Add(title);
            Controls.Add(panel);
        }

        // Game folder picker: where AoE2 saves recorded games (.mgz). Persisted immediately.
        private void BuildGameFolderRow()
        {
            this.Height = Math.Max(this.Height, 360);

            var panel = new Panel { Dock = DockStyle.Bottom, Height = 90, Padding = new Padding(3, 8, 3, 3) };

            var label = new Label
            {
                Text = "Thư mục game (chứa SaveGame):",
                Dock = DockStyle.Top,
                Height = 22,
                Font = new Font("Segoe UI", 9.75F)
            };

            var pathBox = new TextBox
            {
                Dock = DockStyle.Top,
                ReadOnly = true,
                Text = _appContext.GetGameFolder() ?? string.Empty
            };

            var browseButton = new Button { Text = "Chọn thư mục...", Dock = DockStyle.Top, Height = 30 };
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

            // Docked Top stacks in reverse add order: add button, then path, then label.
            panel.Controls.Add(browseButton);
            panel.Controls.Add(pathBox);
            panel.Controls.Add(label);
            Controls.Add(panel);
        }
    }
}
