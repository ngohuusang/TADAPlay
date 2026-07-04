using System.Threading.Tasks;
using System.Windows.Forms;
using AntdUI;
using Microsoft.VisualBasic.Logging;
using TadaPlay.Connections.Interface;
using TadaPlay.Contexts.Interfaces;
using TadaPlay.Controls;
using TadaPlay.Logger;
using TadaPlay.Services;
using TadaPlay.Services.Interface;
using TadaPlay.Utils;
using TadaPlay.Websockets;
using TadaPlay.Websockets.Interface;

namespace TadaPlay
{
    public partial class MainForm : AntdUI.Window
    {
        private readonly IAccountService accountService;
        private readonly IAppContext appContext;
        private readonly IWebSocketService webSocketService;
        private readonly IWireGuardVpnService wireGuardVpnService;
        private readonly IServiceProvider serviceProvider;
        private Login _loginControl;
        private Home _homeControl;
        private Ranking _rankingControl;
        private Matches _matchesControl;

        public MainForm(IAccountService _accountService, IAppContext _appContext, IWebSocketService _webSocketService, IWireGuardVpnService _wireGuardVpnService, IServiceProvider _serviceProvider)
        {
            InitializeComponent();
            accountService = _accountService;
            appContext = _appContext;
            webSocketService = _webSocketService;
            serviceProvider = _serviceProvider;
            wireGuardVpnService = _wireGuardVpnService;

            appContext.OnVpnProfileUpdated += AppContext_OnVpnProfileUpdated;

            rankingButton.Click += rankingButton_Click;
            matchesButton.Click += matchesButton_Click;
            settingButton.Click += btn_setting_Click;
        }

        private void rankingButton_Click(object sender, EventArgs e)
        {
            _rankingControl ??= CreateRankingControl();
            ShowControl(_rankingControl, "Bảng xếp hạng", false, true);
        }

        private void matchesButton_Click(object sender, EventArgs e)
        {
            _matchesControl ??= CreateMatchesControl();
            ShowControl(_matchesControl, "Danh sách trận đấu", false, true);
        }

        private Ranking CreateRankingControl()
        {
            var control = new Ranking(accountService);
            control.BackRequested += (s, e) => ShowControl(_homeControl, "Trang chủ", false, true);
            return control;
        }

        private Matches CreateMatchesControl()
        {
            var control = new Matches(accountService, appContext);
            control.BackRequested += (s, e) => ShowControl(_homeControl, "Trang chủ", false, true);
            return control;
        }

        private void AppContext_OnVpnProfileUpdated(object sender, EventArgs e)
        {
            this.BeginInvoke(() => {
                DebugLogger.Info($"Home: Current vpn profile updated by AppContext.");
                wireGuardVpnService.InitAdapter(appContext.GetVpnProfile()?.ConfigContent).ContinueWith(task =>
                {
                    if (!task.IsCompletedSuccessfully)
                    {
                        AntdUI.Notification.error(this, AntdUI.Localization.Get("VPNProfileUpdateError", "Lỗi cập nhật cấu hình VPN"), AntdUI.Localization.Get("VPNProfileUpdateErrorContent", task.Exception!.InnerException!.Message), AntdUI.TAlignFrom.Bottom);
                    }
                });
            });
        }

        private void btn_setting_Click(object sender, EventArgs e)
        {
            accountService.GetAllAsync();
            var setting = new Setting(this, appContext, accountService);
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
            UiUtils.InvokeOnUiThread(this, () =>
            {
                // Dispose the currently displayed control, if any
                if (windowBar.Tag is UserControl currentControl)
                {
                    virtualPanel.Visible = true;
                    //currentControl.Dispose(); // Essential for freeing resources
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
                controlToShow.Visible = true;
            });
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            AntdUI.Localization.Provider = new Localizer();
            AntdUI.Localization.SetLanguage("vi-VN"); // Set default language to Vietnamese
            //AntdUI.Localization.SetLanguage("en-US");

            _loginControl = new Login(this, accountService, appContext);
            _homeControl = new Home(this, webSocketService, appContext, serviceProvider);
            _homeControl.LogoutRequested += Home_LogoutRequested;
            _loginControl.LoginSuccessful += (s, e) =>
            {
                ShowControl(_homeControl, "Trang chủ", false, true);
                settingButton.Visible = true;
                rankingButton.Visible = true;
                matchesButton.Visible = true;
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

        private async void Home_LogoutRequested(object sender, EventArgs e)
        {
            DebugLogger.Info("Logout requested from Home control.");

            await AntdUI.Spin.open(this, AntdUI.Localization.Get("Loading2", "Đang đăng xuất..."), async config => 
            {
                try
                {
                    // Call the AccountService to handle actual logout (clears AppContext, releases VPN profile via API)
                    bool loggedOut = await accountService.DoLogoutAsync();

                    if (loggedOut)
                    {
                        // Disconnect WebSocket connection
                        webSocketService.Disconnect();


                        DebugLogger.Info("User logged out successfully. Navigating back to login.");
                        // Navigate back to login screen
                        ShowControl(_loginControl, "Đăng nhập", false, false);

                        // Hide global buttons
                        settingButton.Visible = false;
                        rankingButton.Visible = false;
                        matchesButton.Visible = false;
                    }
                    else
                    {
                        DebugLogger.Error("Failed to complete logout process.");
                        AntdUI.Notification.error(this, AntdUI.Localization.Get("LoginOutTitle", "Lỗi đăng xuất"), AntdUI.Localization.Get("LogoutErrorContent", "Đăng xuất không thành công, hãy mở lại ứng dụng và thử lại."), AntdUI.TAlignFrom.Bottom, Font);
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Error($"Exception during logout: {ex.Message}");
                    AntdUI.Notification.error(this, AntdUI.Localization.Get("LoginOutTitle", "Lỗi đăng xuất"), AntdUI.Localization.Get("LogoutErrorContent", "Đăng xuất không thành công, hãy mở lại ứng dụng và thử lại."), AntdUI.TAlignFrom.Bottom, Font);
                }

            });
        }
    }
}
