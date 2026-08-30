using System;
using System.Drawing;
using System.Windows.Forms;
using AntdUI;
using TadaPlay.Contexts.Interfaces;
using TadaPlay.Services.Interface;
using TadaPlay.Utils;

namespace TadaPlay.Controls
{
    /// <summary>
    /// The account editor: full name and an optional password change.
    ///
    /// This used to be the third card inside the Cài đặt dialog, below display mode and the
    /// game folder. Those two are about the machine's game setup; this one is about the person,
    /// and burying it under them meant a player looking for "my account" had to open Settings
    /// and scroll past things that had nothing to do with them. It now opens from the identity
    /// block above the user list - the place they already look to see who they are signed in as.
    ///
    /// The in-game display name is deliberately not editable here: it is always the account's
    /// username (see Home.startGameButton_Click), and a second name a player could set would
    /// just be a way to claim someone else's in-game identity. The server also requires the
    /// current password to confirm any change, even one that only touches the name.
    /// </summary>
    public class AccountInfo : UserControl
    {
        // Matches the Cài đặt dialog so the two read as the same family of screens.
        private static readonly Font ControlFont = new Font("Segoe UI", 11F);
        private const int ProfileCardHeight = 412;

        private readonly Form _form;
        private readonly IAppContext _appContext;
        private readonly IAccountService _accountService;

        public AccountInfo(Form form, IAppContext appContext, IAccountService accountService)
        {
            _form = form;
            _appContext = appContext;
            _accountService = accountService;

            Font = ControlFont;
            Width = 600;
            BackColor = UiTheme.PageBg;
            Padding = new Padding(14, 14, 14, 16);
            // Not AutoSize: AntdUI.Modal.open sizes the dialog from this control's Height, and a
            // plain UserControl does not compute a preferred size from Dock=Top children, so
            // AutoSize would collapse the modal to an empty box. See the same note in Setting.
            AutoSize = false;

            Controls.Add(BuildProfileCard());
            Height = Padding.Vertical + ProfileCardHeight;
        }

        private Control BuildProfileCard()
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
                    Warn("Họ tên không được để trống.");
                    return;
                }
                if (string.IsNullOrEmpty(currentPassword))
                {
                    Warn("Vui lòng nhập mật khẩu hiện tại để xác nhận thay đổi.");
                    return;
                }
                if (newPassword != confirmPassword)
                {
                    Warn("Mật khẩu mới và xác nhận mật khẩu không khớp.");
                    return;
                }
                if (newPassword.Length > 0 && newPassword.Length < 6)
                {
                    Warn("Mật khẩu mới phải có ít nhất 6 ký tự.");
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
                    bool success = await _accountService.UpdateUserInfo(
                        fullName, currentUser?.Username ?? string.Empty, currentPassword, newPassword);
                    if (success)
                    {
                        if (currentUser != null)
                        {
                            currentUser.FullName = fullName;
                            currentUser.NickName = currentUser.Username;
                            // Raises OnCurrentUserUpdated, which is what repaints the name above
                            // the user list without this screen having to reach into Home.
                            _appContext.SetCurrentUser(currentUser);
                        }
                        currentPasswordBox.Text = string.Empty;
                        newPasswordBox.Text = string.Empty;
                        confirmPasswordBox.Text = string.Empty;
                        Say("Thành công", "Thông tin tài khoản đã được cập nhật.", AntdUI.TType.Success);
                    }
                }
                catch (Exception ex)
                {
                    Say("Lỗi", ex.InnerException?.Message ?? ex.Message, AntdUI.TType.Error);
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

        private void Warn(string message) => Say("Lỗi", message, AntdUI.TType.Warn);

        private void Say(string title, string message, AntdUI.TType type) =>
            AntdUI.Modal.open(new AntdUI.Modal.Config(_form, title, message, type)
            { CancelText = null, OkText = "Đóng" });
    }
}
