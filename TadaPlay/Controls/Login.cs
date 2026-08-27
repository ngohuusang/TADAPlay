using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using TadaPlay.Exceptions;
using TadaPlay.Logger;
using TadaPlay.Contexts;
using TadaPlay.Contexts.Interfaces;
using TadaPlay.Services;
using TadaPlay.Utils;
using TadaPlay.Services.Interface;

namespace TadaPlay.Controls
{
    public partial class Login : UserControl
    {
        private readonly Form form;
        private readonly IAccountService accountService;
        private readonly IAppContext appContext;

        public event EventHandler LoginSuccessful;

        public Login(Form _form, IAccountService _accountService, IAppContext _appContext)
        {
            InitializeComponent();

            form = _form;
            accountService = _accountService;
            appContext = _appContext;

            this.usernameTextBox.KeyDown += TextBox_KeyDown;
            this.passwordTextBox.KeyDown += TextBox_KeyDown;
        }

        private async void signInButton_Click(object sender, EventArgs e)
        {
            // AntdUI.Spin.open takes a plain Action<Config>, not an awaitable - wrapping the
            // async login call in .ContinueWith() without awaiting it meant the "action" delegate
            // (and with it, whatever Spin.open uses to decide the overlay is done) returned as
            // soon as the login call hit its first await, so the spinner never actually tracked
            // the real request duration. Button.Loading is a property we set/clear ourselves
            // around a real await, so it can't desync from the actual work.
            signInButton.Loading = true;
            signInButton.Enabled = false;
            try
            {
                bool success = await accountService.DoLoginAsync(usernameTextBox.Text, passwordTextBox.Text, autoLoginCheckbox.Checked);
                if (success)
                {
                    LoginSuccessful?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (UpdateRequiredException ex)
            {
                ShowUpdateRequired(ex);
            }
            catch (Exception ex)
            {
                AntdUI.Modal.open(new AntdUI.Modal.Config(form, AntdUI.Localization.Get("LoginError", "Lỗi đăng nhập"), AntdUI.Localization.Get("LoginErrorContent", ex.InnerException?.Message ?? ex.Message), AntdUI.TType.Error)
                {
                    CancelText = null,
                    OkText = AntdUI.Localization.Get("CloseButton", "Đóng")
                });
            }
            finally
            {
                signInButton.Loading = false;
                signInButton.Enabled = true;
            }
        }

        /// <summary>
        /// The server refused this build as too old. There is no auto-updater, so the most useful
        /// thing we can do is open the download page and get out of the way - the player cannot
        /// proceed regardless, and leaving the login form up would just invite retries that
        /// cannot succeed.
        /// </summary>
        private void ShowUpdateRequired(UpdateRequiredException ex)
        {
            string message = string.IsNullOrWhiteSpace(ex.Message)
                ? "Phiên bản TADA Play của bạn đã cũ. Vui lòng cập nhật để tiếp tục."
                : ex.Message;

            var config = new AntdUI.Modal.Config(form, "Cần cập nhật TADA Play", message, AntdUI.TType.Warn)
            {
                OkText = "Tải bản mới",
                CancelText = "Thoát"
            };

            config.OkText = "Cập nhật ngay";
            if (AntdUI.Modal.open(config) != DialogResult.OK)
            {
                Application.Exit();
                return;
            }

            _ = RunForcedUpdateAsync(ex.DownloadUrl);
        }

        /// <summary>
        /// Downloads and installs the newer client in place, rather than sending the player to a
        /// web page to do it by hand.
        ///
        /// This is the whole point of the updater: a build old enough to be refused at login is
        /// the one least able to help itself, and "go and re-download it" is a step a lot of
        /// people simply do not take. If anything goes wrong we fall back to opening the page,
        /// which is exactly what used to happen every time.
        /// </summary>
        private async System.Threading.Tasks.Task RunForcedUpdateAsync(string fallbackUrl)
        {
            signInButton.Loading = true;
            signInButton.Enabled = false;
            try
            {
                UpdateService.UpdateInfo info = await UpdateService.CheckAsync();
                if (info == null)
                {
                    // The server told us to update but cannot tell us what to - nothing sensible
                    // left to do in-app.
                    OpenDownloadPage(fallbackUrl);
                    return;
                }

                string installer = await UpdateService.DownloadAsync(info, message =>
                    UiUtils.InvokeOnUiThread(this, () =>
                        AntdUI.Notification.info(form, "Cập nhật", message,
                                                 AntdUI.TAlignFrom.Bottom, Font), "LOGIN_UPDATE_PROGRESS"));

                if (installer == null || !UpdateService.StartInstaller(installer))
                {
                    OpenDownloadPage(fallbackUrl);
                    return;
                }

                // The installer replaces files this process is running from, so it cannot
                // finish while we are alive. It restarts the app itself (/RELAUNCH).
                Application.Exit();
            }
            catch (Exception ex)
            {
                DebugLogger.Warn("Login: in-app update failed: " + ex.Message);
                OpenDownloadPage(fallbackUrl);
            }
            finally
            {
                signInButton.Loading = false;
                signInButton.Enabled = true;
            }
        }

        private void OpenDownloadPage(string url)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                try
                {
                    // UseShellExecute so the URL opens in the default browser; without it .NET
                    // tries to exec the string as a program and throws.
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception openEx)
                {
                    DebugLogger.Warn("Login: could not open the download URL: " + openEx.Message);
                }
            }
            Application.Exit();
        }

        private async void Login_Load(object sender, EventArgs e)
        {
            autoLoginCheckbox.Checked = appContext.GetAutoLoginSetting();

            form.AcceptButton = signInButton;

            if (appContext.GetAutoLoginSetting())
            {
                // Same fix as signInButton_Click: drive the loading state from a real await
                // instead of an un-awaited .ContinueWith() under Spin.open's Action<Config>, and
                // show it on the button itself (instead of hiding the whole form blank) so a
                // silent auto-login attempt still gives visible feedback instead of a blank screen.
                signInButton.Loading = true;
                signInButton.Enabled = false;
                try
                {
                    bool success = await accountService.DoAuthAsync();
                    if (success)
                    {
                        LoginSuccessful?.Invoke(this, EventArgs.Empty);
                    }
                }
                catch (UpdateRequiredException ex)
                {
                    // Deliberately the modal, not the toast used for other auto-login failures:
                    // this one is blocking and actionable, and a notification that fades after a
                    // few seconds is exactly the wrong affordance for it.
                    ShowUpdateRequired(ex);
                }
                catch (Exception ex)
                {
                    AntdUI.Notification.error(form, AntdUI.Localization.Get("LoginErrorTitle", "Lỗi đăng nhập"), AntdUI.Localization.Get("LoginErrorContent", ex.InnerException?.Message ?? ex.Message), AntdUI.TAlignFrom.Bottom, Font);
                }
                finally
                {
                    signInButton.Loading = false;
                    signInButton.Enabled = true;
                }
            }
        }

        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                // Prevent the TextBox from processing the Enter key (e.g., adding a new line)
                e.SuppressKeyPress = true;
                e.Handled = true;

                // Trigger the click event of the login button
                this.signInButton.PerformClick();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                // --- NEW: Unsubscribe from KeyDown events ---
                this.usernameTextBox.KeyDown -= TextBox_KeyDown;
                this.passwordTextBox.KeyDown -= TextBox_KeyDown;
                // ... (existing unsubscribes) ...
            }
            base.Dispose(disposing);
        }
    }
}
