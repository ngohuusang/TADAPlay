using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AntdUI;
using Microsoft.VisualBasic.Logging;
using TadaPlay.Common.Models;
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
        private bool _trayBalloonShown;
        private bool _isExiting;

        public MainForm(IAccountService _accountService, IAppContext _appContext, IWebSocketService _webSocketService, IWireGuardVpnService _wireGuardVpnService, IServiceProvider _serviceProvider)
        {
            InitializeComponent();
            accountService = _accountService;
            appContext = _appContext;
            webSocketService = _webSocketService;
            serviceProvider = _serviceProvider;
            wireGuardVpnService = _wireGuardVpnService;

            appContext.OnVpnProfileUpdated += AppContext_OnVpnProfileUpdated;
            appContext.OnUserCameOnline += AppContext_OnUserCameOnline;

            // Always run with Windows, starting hidden in the tray - no user-facing toggle for this.
            appContext.SetRunOnStartupSetting(true);

            rankingButton.Click += rankingButton_Click;
            matchesButton.Click += matchesButton_Click;
            settingButton.Click += btn_setting_Click;

            ShowVersionInTitle();
        }

        /// <summary>
        /// Puts the running version in the title bar and the taskbar entry.
        ///
        /// Read from the assembly at runtime rather than written into the designer file, so it
        /// cannot drift from the build the player is actually running - a hardcoded string here
        /// would be wrong the first time someone forgot to update it, and a wrong version on
        /// screen is worse than none when the question is "did the update actually take?".
        ///
        /// It goes on Text, not SubText: SubText is rewritten with the current page's name on
        /// every navigation, so a version there would vanish the moment anyone clicked anything.
        /// </summary>
        private void ShowVersionInTitle()
        {
            try
            {
                string version = UpdateService.CurrentVersion;
                if (string.IsNullOrWhiteSpace(version)) return;

                windowBar.Text = $"TADA Play v{version}";
                Text = $"TADA Play v{version} - aoe2.io.vn";
            }
            catch (Exception ex)
            {
                // Cosmetic - never let it stop the window opening.
                DebugLogger.Warn($"MainForm: could not show the version in the title: {ex.Message}");
            }
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
            UiUtils.InvokeOnUiThread(this, () =>
            {
                string configContent = appContext.GetVpnProfile()?.ConfigContent;

                // The profile is cleared (set to null) as part of a normal logout - there's no
                // config to apply in that case, so re-initializing the adapter would only
                // produce a spurious "empty config" error rather than a real failure.
                if (string.IsNullOrWhiteSpace(configContent))
                {
                    DebugLogger.Info("Home: VPN profile cleared by AppContext (e.g. logout) - skipping adapter re-init.");
                    return;
                }

                DebugLogger.Info($"Home: Current vpn profile updated by AppContext.");
                wireGuardVpnService.InitAdapter(configContent).ContinueWith(task =>
                {
                    if (!task.IsCompletedSuccessfully)
                    {
                        UiUtils.InvokeOnUiThread(this, () =>
                            AntdUI.Notification.error(this, AntdUI.Localization.Get("VPNProfileUpdateError", "Lỗi cập nhật cấu hình VPN"), AntdUI.Localization.Get("VPNProfileUpdateErrorContent", task.Exception!.InnerException!.Message), AntdUI.TAlignFrom.Bottom));
                    }
                });
            }, "MAINFORM_VPN_PROFILE");
        }

        private void AppContext_OnUserCameOnline(object sender, IReadOnlyList<User> newlyOnlineUsers)
        {
            UiUtils.InvokeOnUiThread(this, () =>
            {
                if (!trayIcon.Visible || newlyOnlineUsers.Count == 0) return;

                string names = string.Join(", ", newlyOnlineUsers.Select(u => u.FullName ?? u.Username));
                string message = newlyOnlineUsers.Count == 1
                    ? $"{names} vừa online."
                    : $"{newlyOnlineUsers.Count} người vừa online: {names}";

                // ToolTipIcon.None, not .Info: Windows plays its own system sound for an Info
                // balloon, and with our own alert underneath it that lands as two overlapping
                // noises for one event. Losing the small info glyph is the cheaper trade.
                trayIcon.ShowBalloonTip(3000, "TADA Play", message, ToolTipIcon.None);
                NotificationSound.PlayUserOnline();
            }, "MAINFORM_USER_ONLINE");
        }

        private void btn_setting_Click(object sender, EventArgs e)
        {
            accountService.GetAllAsync();
            var setting = new Setting(this, appContext, accountService);
            AntdUI.Modal.open(this, AntdUI.Localization.Get("Setting", "Cài đặt"), setting);
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
            _homeControl = new Home(this, webSocketService, appContext, wireGuardVpnService, accountService);
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

            // Launched via the Windows "Run at startup" entry (see AppContext.SetRunOnStartupSetting) -
            // go straight to the tray instead of flashing the main window on screen.
            if (Program.StartMinimized)
            {
                Hide();
            }
        }

        private void MainForm_Resize(object sender, EventArgs e)
        {
            if (windowBar.Tag is Control control)
            {
                control.Left = (this.ClientSize.Width - control.Width) / 2;
                control.Top = (this.ClientSize.Height - control.Height) / 2;
            }

            if (WindowState == FormWindowState.Minimized)
            {
                MinimizeToTray();
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // Clicking the window's X should hide to tray, not quit - only "Thoát" from the
            // tray's right-click menu (which sets _isExiting first) actually closes the app.
            if (!_isExiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                MinimizeToTray();
            }
        }

        private void MinimizeToTray()
        {
            // Hide() alone removes the taskbar entry - deliberately not touching ShowInTaskbar
            // here: toggling it while the handle already exists forces WinForms to recreate the
            // window handle (and cascades into child controls recreating theirs too), which was
            // re-firing Home's Load logic - re-connecting the VPN and starting a duplicate record
            // watcher - every single time the window was hidden/restored.
            Hide();

            if (!_trayBalloonShown)
            {
                trayIcon.ShowBalloonTip(2000, "TADA Play", "Ứng dụng vẫn đang chạy trong khay hệ thống.", ToolTipIcon.Info);
                _trayBalloonShown = true;
            }
        }

        /// <summary>
        /// Listens for the "show yourself" message a second launch broadcasts.
        ///
        /// This is what makes clicking the icon while the app sits in the tray do the obvious
        /// thing - bring the running copy forward - instead of appearing to do nothing and
        /// tempting the player into clicking again.
        /// </summary>
        // Fully qualified: AntdUI also has a Message type, so the bare name is ambiguous here.
        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg != 0 && m.Msg == SingleInstance.ShowInstanceMessage)
            {
                RestoreFromTray();
            }
            base.WndProc(ref m);
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        // Left-click alone does nothing - NotifyIcon.Click fires for both mouse buttons, which
        // used to also pop the window open behind the right-click context menu. Only a genuine
        // left double-click (or the "Mở TADA Play" menu item) restores the window now.
        private void trayIcon_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) RestoreFromTray();
        }

        private void trayShowMenuItem_Click(object sender, EventArgs e) => RestoreFromTray();

        private void trayExitMenuItem_Click(object sender, EventArgs e)
        {
            _isExiting = true;
            trayIcon.Visible = false;
            Application.Exit();
        }

        private async void Home_LogoutRequested(object sender, EventArgs e)
        {
            DebugLogger.Info("Logout requested from Home control.");

            // logoutButton.Loading (set in Home.logoutButton_Click) already gives visible loading
            // feedback, driven straight off this same await chain - no need for a separate
            // Spin.open overlay whose Action<Config>-based timing doesn't reliably track an
            // async operation's real completion (see the same fix in Login.signInButton_Click).
            try
            {
                // Call the AccountService to handle actual logout (clears AppContext, releases VPN profile via API)
                bool loggedOut = await accountService.DoLogoutAsync();

                if (loggedOut)
                {
                    // Disconnect WebSocket and tear down the VPN tunnel
                    webSocketService.Disconnect();
                    if (wireGuardVpnService.IsConnected)
                    {
                        await wireGuardVpnService.DisconnectAsync();
                    }

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
                    _homeControl.ResetLogoutButtonState();
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"Exception during logout: {ex.Message}");
                AntdUI.Notification.error(this, AntdUI.Localization.Get("LoginOutTitle", "Lỗi đăng xuất"), AntdUI.Localization.Get("LogoutErrorContent", "Đăng xuất không thành công, hãy mở lại ứng dụng và thử lại."), AntdUI.TAlignFrom.Bottom, Font);
                _homeControl.ResetLogoutButtonState();
            }
        }
    }
}
