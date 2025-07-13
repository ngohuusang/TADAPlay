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

        public static void InitLog4Net()
        {
            var logCfg = new FileInfo(AppDomain.CurrentDomain.BaseDirectory + "Resources/Log4net/log4net.config");
            XmlConfigurator.ConfigureAndWatch(logCfg);
        }
    }
}

