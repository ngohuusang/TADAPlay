using System.Diagnostics;
using System.Linq;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TadaPlay.Connections;
using TadaPlay.Connections.Interface;
using TadaPlay.Contexts.Interfaces;
using TadaPlay.Logger;
using TadaPlay.Services;
using TadaPlay.Services.Interface;
using TadaPlay.Websockets;
using TadaPlay.Websockets.Interface;

namespace TadaPlay
{
    internal static class Program
    {

        private static IServiceProvider ServiceProvider { get; set; }

        // Set when launched with "--minimized" (used by the Windows "Run at startup" entry),
        // so MainForm can start hidden in the tray instead of popping up the window.
        public static bool StartMinimized { get; private set; }

        [STAThread]
        static void Main(string[] args)
        {
            StartMinimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            ApplicationConfiguration.Initialize();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            DebugLogger.InitLog4Net();
            // Read straight from the registry: this runs before ConfigureServices, so there is
            // no IAppContext to ask yet. Off unless the player turned it on in Cài đặt.
            DebugLogger.SetFileLogging(Contexts.AppContext.IsDebugLogEnabled());
            DebugLogger.CleanLog();
            InstallCrashGuard();
            DebugLogger.Info("Project start: " + Application.ProductVersion);

            // One copy only. A second instance would start its own VPN adapter, record watcher
            // and lobby socket, fighting the first over the same tunnel IP and showing every
            // other player a duplicate of this account.
            if (!Utils.SingleInstance.TryAcquire())
            {
                // Except when this launch IS the logon task: the app is already up, and yanking
                // its window open at sign-in because a scheduled task fired would be worse than
                // doing nothing.
                if (!StartMinimized)
                {
                    Utils.SingleInstance.SignalExistingInstance();
                }
                DebugLogger.Info("Project start: another instance is already running - handing over to it.");
                return;
            }
            Application.ApplicationExit += (s, e) => Utils.SingleInstance.Release();

            // If this start follows an update, the installer it ran is still sitting in %TEMP%.
            // Done here rather than after login so it also happens for a player who never gets
            // that far - an abandoned 55 MB file should not depend on signing in.
            Services.UpdateService.CleanupDownloadedInstallers();

            ConfigureServices();

            // Ensure the WebSocketService is disposed when the application exits
            Application.ApplicationExit += (sender, e) =>
            {
                (ServiceProvider as IDisposable)?.Dispose(); // Dispose the entire service provider
            };

            Application.Run(ServiceProvider.GetRequiredService<MainForm>());
        }

        /// <summary>
        /// Keeps a UI-thread exception from killing the app.
        ///
        /// Without this, WinForms shows the raw .NET crash dialog - a wall of stack trace and
        /// loaded assemblies, in English, with a "JIT Debugging" footer - and the app is gone.
        /// That is a terrible outcome for a player mid-evening, and it does not even leave
        /// anything behind to diagnose with unless they photograph the dialog.
        ///
        /// What prompted it: AntdUI 2.0.9 races its own scrollbar hover animation. Moving the
        /// mouse off the online-user list can land in
        ///
        ///   AntdUI.Chat.MsgList.OnMouseLeave -> ScrollBar.Leave -> set_HoverY
        ///     -> ITask.Dispose -> CancellationTokenSource.Cancel  (already disposed)
        ///
        /// which throws ObjectDisposedException on the UI thread. It is inside the library, in
        /// a code path nothing here calls directly, and it needs a list long enough to have a
        /// scrollbar - so it shows up on a busy lobby night and not in testing. There is no fix
        /// available from this side beyond not letting it be fatal.
        ///
        /// CatchException means WinForms raises ThreadException instead of tearing down, and
        /// returning from the handler lets the message loop carry on. That is the right trade
        /// for a painting or animation fault, which is what reaches here in practice: the app
        /// keeps running and the fault is logged rather than shown.
        ///
        /// The AppDomain handler cannot stop the process - a background-thread exception is
        /// still fatal - but it makes sure the reason is written down before it goes.
        /// </summary>
        private static void InstallCrashGuard()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.ThreadException += (sender, e) =>
            {
                DebugLogger.Error($"Unhandled UI exception (app kept running): {e.Exception}");
            };

            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                DebugLogger.Fatal($"Unhandled exception, terminating={e.IsTerminating}: {e.ExceptionObject}");
            };
        }

        private static void ConfigureServices()
        {
            var services = new ServiceCollection();

            services.AddScoped<IAccountService, AccountService>();
            services.AddSingleton<IAppContext, Contexts.AppContext>();
            services.AddSingleton<IWebSocketService, WebSocketService>();
            services.AddSingleton<IWireGuardVpnService, WireguardVpnService>();

            services.AddTransient<MainForm>();

            ServiceProvider = services.BuildServiceProvider();
        }
    }
}