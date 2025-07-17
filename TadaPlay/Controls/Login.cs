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

        private void signInButton_Click(object sender, EventArgs e)
        {
            AntdUI.Spin.open(form, AntdUI.Localization.Get("Loading2", "Đang đăng nhập..."), config =>
            {
                accountService.DoLoginAsync(usernameTextBox.Text, passwordTextBox.Text, autoLoginCheckbox.Checked).ContinueWith(async task =>
                {
                    if (task.IsCompletedSuccessfully && task.Result)
                    {
                        LoginSuccessful?.Invoke(this, EventArgs.Empty);
                    }
                    else
                    {
                        AntdUI.Modal.open(new AntdUI.Modal.Config(form, AntdUI.Localization.Get("LoginError", "Lỗi đăng nhập"), AntdUI.Localization.Get("LoginErrorContent", task.Exception!.InnerException!.Message), AntdUI.TType.Error)
                        {
                            CancelText = null,
                            OkText = AntdUI.Localization.Get("CloseButton", "Đóng")
                        });
                       
                    }
                });
            });
        }

        private void Login_Load(object sender, EventArgs e)
        {
            autoLoginCheckbox.Checked = appContext.GetAutoLoginSetting();

            form.AcceptButton = signInButton;

            if (appContext.GetAutoLoginSetting())
            {
                this.Visible = false;
                AntdUI.Spin.open(form, AntdUI.Localization.Get("Loading2", "Đang đăng nhập..."), config =>
                {
                    accountService.DoAuthAsync().ContinueWith(task =>
                    {
                        if (task.IsCompletedSuccessfully && task.Result)
                        {
                            LoginSuccessful?.Invoke(this, EventArgs.Empty);
                        }
                        else
                        {
                            this.Visible = true;
                            AntdUI.Notification.error(form, AntdUI.Localization.Get("LoginErrorTitle", "Lỗi đăng nhập"), AntdUI.Localization.Get("LoginErrorContent", task.Exception!.InnerException!.Message), AntdUI.TAlignFrom.Bottom, Font);
                        }
                    });
                });
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
