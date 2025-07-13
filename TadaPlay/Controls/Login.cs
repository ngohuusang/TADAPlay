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
            form = _form;
            accountService = _accountService;
            appContext = _appContext;
            InitializeComponent();
        }

        private void signInButton_Click(object sender, EventArgs e)
        {
            AntdUI.Spin.open(form, AntdUI.Localization.Get("Loading2", "Đang đăng nhập..."), config =>
            {
                accountService.DoLoginAsync(usernameTextBox.Text, passwordTextBox.Text, autoLoginCheckbox.Checked).ContinueWith(async task =>
                {
                    if (task.IsCompletedSuccessfully && task.Result)
                    {
                        AntdUI.Notification.success(form, AntdUI.Localization.Get("LoginSuccessTitle", "Đăng nhập"), AntdUI.Localization.Get("LoginSuccessContent", "Đăng nhập thành công!"), AntdUI.TAlignFrom.Bottom);
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
            }, () =>
            {
                System.Diagnostics.Debug.WriteLine("Hoàn tất");
            });
        }

        private void Login_Load(object sender, EventArgs e)
        {
            if (appContext.GetAutoLoginSetting())
            {
                this.Visible = false;
                AntdUI.Spin.open(form, AntdUI.Localization.Get("Loading2", "Đang đăng nhập..."), config =>
                {
                    accountService.DoAuthAsync().ContinueWith(async task =>
                    {
                        if (task.IsCompletedSuccessfully && task.Result)
                        {
                            AntdUI.Message.info(form, AntdUI.Localization.Get("LoginSuccess", "Đăng nhập thành công!"));
                            LoginSuccessful?.Invoke(this, EventArgs.Empty);
                        }
                        else
                        {
                            this.Visible = true;
                            AntdUI.Message.error(form, AntdUI.Localization.Get("LoginError", task.Exception!.InnerException!.Message));
                        }
                    });
                }, () =>
                {
                    System.Diagnostics.Debug.WriteLine("Hoàn tất");
                });
            }
        }
    }
}
