using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using TadaPlay.Contexts;
using TadaPlay.Contexts.Interfaces;
using TadaPlay.Services;
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
