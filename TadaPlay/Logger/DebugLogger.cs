using log4net.Appender;
using log4net.Config;
using log4net.Core;
using log4net.Layout;
using log4net.Repository.Hierarchy;

namespace TadaPlay.Logger
{
    public class DebugLogger
    {
        private static readonly log4net.ILog logDebug = log4net.LogManager.GetLogger("logdebug");
        private static readonly log4net.ILog logInfo = log4net.LogManager.GetLogger("loginfo");
        private static readonly log4net.ILog logError = log4net.LogManager.GetLogger("logerror");
        private static readonly log4net.ILog logWarn = log4net.LogManager.GetLogger("loginfo");
        private static readonly log4net.ILog logFatal = log4net.LogManager.GetLogger("logerror");

        public static bool IsStreamOnConsole = true;

        // Named so SetFileLogging can find and detach exactly its own appender, without
        // disturbing the console one the config file installs.
        private const string FileAppenderName = "debugfile";

        /// <summary>Where the debug log goes when it is switched on.</summary>
        public static string LogFolder =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Log");

        public static string LogFilePath => Path.Combine(LogFolder, "tadaplay.log");

        /// <summary>True while the app is writing a log file. Off unless the player asked.</summary>
        public static bool FileLoggingEnabled { get; private set; }

        /// <summary>
        /// Attaches or detaches the log file at runtime.
        ///
        /// The file used to be wired up in log4net.config and so was always written - every
        /// player, every session, forever, on a machine most of whom will never send a log in.
        /// It is now opt-in, and this is what the setting toggles. Attaching in code rather than
        /// leaving the appender declared-but-unreferenced matters: log4net constructs and OPENS
        /// every appender it finds in the config, so a declared RollingFileAppender creates the
        /// file whether anything logs to it or not.
        ///
        /// Detaching closes the file, which is what lets the player delete the folder afterwards.
        /// Never throws - failing to write a log is not worth taking the app down for, and the
        /// install directory is not always writable.
        /// </summary>
        public static void SetFileLogging(bool enabled)
        {
            try
            {
                if (log4net.LogManager.GetRepository() is not Hierarchy hierarchy) return;
                var root = hierarchy.Root;

                if (root.GetAppender(FileAppenderName) is IAppender existing)
                {
                    root.RemoveAppender(existing);
                    existing.Close();
                }

                if (enabled)
                {
                    var layout = new PatternLayout("%date [%thread] %level %logger - %message%newline");
                    layout.ActivateOptions();

                    var appender = new RollingFileAppender
                    {
                        Name = FileAppenderName,
                        File = LogFilePath,
                        AppendToFile = true,
                        RollingStyle = RollingFileAppender.RollingMode.Size,
                        MaxSizeRollBackups = 5,
                        MaximumFileSize = "10MB",
                        StaticLogFileName = true,
                        Layout = layout,
                        // Explicit UTF-8, because the appender otherwise writes the machine's
                        // ANSI codepage and every Vietnamese message loses characters that
                        // codepage has no room for - "Cài đặt" was landing as "Cài d?t". These
                        // logs exist to be sent to someone and read, so they have to survive
                        // the trip. No BOM: the file is appended to across sessions.
                        Encoding = new System.Text.UTF8Encoding(false),
                    };
                    appender.ActivateOptions();
                    root.AddAppender(appender);
                }

                hierarchy.RaiseConfigurationChanged(EventArgs.Empty);
                FileLoggingEnabled = enabled;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not switch the log file {(enabled ? "on" : "off")}: {ex.Message}");
            }
        }

        public static void Debug(string message)
        {
            if (DebugLogger.IsStreamOnConsole)
            {
                Console.WriteLine(message);
            }

            if (logDebug.IsDebugEnabled)
            {
                logDebug.Info(message);
            }
        }

        public static void Info(string message)
        {
            if (DebugLogger.IsStreamOnConsole)
            {
                Console.WriteLine(message);
            }

            if (logInfo.IsInfoEnabled)
            {
                logInfo.Info(message);
            }
        }

        public static void Warn(string message)
        {
            if (DebugLogger.IsStreamOnConsole)
            {
                Console.WriteLine(message);
            }

            if (logWarn.IsWarnEnabled)
            {
                logWarn.Fatal(message);
            }
        }

        public static void Error(string message)
        {
            if (DebugLogger.IsStreamOnConsole)
            {
                Console.WriteLine(message);
            }

            if (logError.IsErrorEnabled)
            {
                logError.Error(message);
            }
        }

        public static void Fatal(string message)
        {
            if (DebugLogger.IsStreamOnConsole)
            {
                Console.WriteLine(message);
            }

            if (logFatal.IsFatalEnabled)
            {
                logFatal.Fatal(message);
            }
        }

        public static void CleanLog()
        {
            try
            {
                string logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs"); // Assuming 'Logs' folder
                if (Directory.Exists(logDirectory))
                {
                    foreach (string file in Directory.GetFiles(logDirectory, "*.log"))
                    {
                        if (File.GetCreationTime(file) < DateTime.Now.AddDays(-2)) // Delete logs older than 2 days
                        {
                            File.Delete(file);
                            Console.WriteLine($"Deleted old log file: {file}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error cleaning logs: {ex.Message}");
            }
        }

        public static void InitLog4Net()
        {
            try
            {
                var logCfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Log4net", "log4net.config");
                var logCfg = new FileInfo(logCfgPath);

                if (!logCfg.Exists)
                {
                    Console.Error.WriteLine($"Error: log4net configuration file not found at {logCfgPath}");
                    log4net.Config.BasicConfigurator.Configure(); // Basic fallback to console
                }
                else
                {
                    // Configure, not ConfigureAndWatch: nothing edits this file while the app
                    // runs, and a reload would silently drop the file appender SetFileLogging
                    // attached in code - turning the player's log off mid-session.
                    XmlConfigurator.Configure(logCfg);
                    Console.WriteLine("log4net configured successfully.");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to initialize log4net: {ex.Message}");
            }
        }        
    }
}

