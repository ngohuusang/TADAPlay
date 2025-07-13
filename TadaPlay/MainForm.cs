using AntdUI;
using Microsoft.VisualBasic.Logging;
using TadaPlay.Contexts.Interfaces;
using TadaPlay.Controls;
using TadaPlay.Services;
using TadaPlay.Services.Interface;
using TadaPlay.Websockets.Interface;

namespace TadaPlay
{
    public partial class MainForm : AntdUI.Window
    {
        private readonly IAccountService accountService;
        private readonly IAppContext appContext;
        private readonly IWebSocketService webSocketService;
        private readonly IServiceProvider serviceProvider;
        private Login _loginControl;
        private Home _homeControl;

        public MainForm(IAccountService _accountService, IAppContext _appContext, IWebSocketService _webSocketService, IServiceProvider _serviceProvider)
        {
            InitializeComponent();
            accountService = _accountService;
            appContext = _appContext;
            webSocketService = _webSocketService;
            serviceProvider = _serviceProvider;
        }

        private void btn_setting_Click(object sender, EventArgs e)
        {
            accountService.GetAllAsync();
            var setting = new Setting(this);
            if (AntdUI.Modal.open(this, AntdUI.Localization.Get("Setting", "Cài đặt"), setting) == DialogResult.OK)
            {
                AntdUI.Config.Animation = setting.Animation;
                AntdUI.Config.ShadowEnabled = setting.ShadowEnabled;
                AntdUI.Config.ShowInWindow = setting.ShowInWindow;
                AntdUI.Config.ScrollBarHide = setting.ScrollBarHide;
                if (AntdUI.Config.TextRenderingHighQuality == setting.TextRenderingHighQuality) return;
                AntdUI.Config.TextRenderingHighQuality = setting.TextRenderingHighQuality;
                Refresh();
            }
        }

        // Helper method to switch UserControls in a dedicated content panel
        private void ShowControl(UserControl controlToShow, string subTitle, bool showBack, bool fillFull)
        {
           BeginInvoke(new Action(() =>
            {
                // Dispose the currently displayed control, if any
                if (windowBar.Tag is UserControl currentControl)
                {
                    virtualPanel.Visible = true;
                    currentControl.Dispose(); // Essential for freeing resources
                    Controls.Remove(currentControl);
                }

                // Add the new control
                if (fillFull)
                {
                    controlToShow.Dock = DockStyle.Fill;
                }

                Controls.Add(controlToShow);
                controlToShow.BringToFront();
                controlToShow.Focus();


                windowBar.SubText = subTitle;
                windowBar.ShowBack = showBack;

                virtualPanel.Visible = false;
                // You can add more conditions for other control types
                windowBar.Tag = controlToShow; // Keep track of the active control
            }));
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            _loginControl = new Login(this, accountService, appContext);
            _homeControl = new Home(this, webSocketService, appContext, serviceProvider);

            _loginControl.LoginSuccessful += (s, e) =>
            {
                ShowControl(_homeControl, "Trang chủ", false, true);
                settingButton.Visible = true;
                rankingButton.Visible = true;
            };
            _loginControl.Load += (s, e) =>
            {
                _loginControl.Left = (this.ClientSize.Width - _loginControl.Width) / 2;
                _loginControl.Top = (this.ClientSize.Height - _loginControl.Height) / 2;
            };

            ShowControl(_loginControl, "Đăng nhập", false, false);
        }

        private void MainForm_Resize(object sender, EventArgs e)
        {
            if (windowBar.Tag is Control control)
            {
                control.Left = (this.ClientSize.Width - control.Width) / 2;
                control.Top = (this.ClientSize.Height - control.Height) / 2;
            }
        }

        private void button7_SelectedValueChanged(object sender, ObjectNEventArgs e)
        {

        }

        private void virtualPanel_ItemClick(object sender, VirtualItemEventArgs e)
        {

        }
    }
}
