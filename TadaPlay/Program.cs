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
            DebugLogger.CleanLog();
            DebugLogger.Info("Project start: " + Application.ProductVersion);

            ConfigureServices();

            // Ensure the WebSocketService is disposed when the application exits
            Application.ApplicationExit += (sender, e) =>
            {
                (ServiceProvider as IDisposable)?.Dispose(); // Dispose the entire service provider
            };

            Application.Run(ServiceProvider.GetRequiredService<MainForm>());
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