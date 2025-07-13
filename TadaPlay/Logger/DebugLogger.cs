using log4net.Config;

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
                    XmlConfigurator.ConfigureAndWatch(logCfg);
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

