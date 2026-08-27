using System;
using System.Runtime.InteropServices;
using System.Threading;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Keeps one copy of TadaPlay running, and makes a second launch surface the first.
    ///
    /// Two copies is not merely untidy here: each one starts its own VPN adapter, its own record
    /// watcher and its own lobby socket, so a second instance fights the first over the same
    /// tunnel IP and publishes a duplicate of the player to everyone else. Clicking the icon
    /// while the app sits in the tray - which looks like doing nothing, so people click again -
    /// was enough to cause it.
    /// </summary>
    public static class SingleInstance
    {
        // Session-local, not Global\: one instance per signed-in Windows user is right, and a
        // Global mutex would stop a second person on a shared machine running it at all.
        private const string MutexName = "TadaPlay.SingleInstance";

        // Registered by string, so both processes resolve the same id without sharing anything.
        private const string ShowMessageName = "TadaPlayShowExistingInstance";

        private const int HWND_BROADCAST = 0xFFFF;

        /// <summary>The window message a second launch sends to ask the first to show itself.</summary>
        public static readonly int ShowInstanceMessage = RegisterWindowMessage(ShowMessageName);

        // Held for the life of the process. A local would be eligible for collection the moment
        // the method returned, and a collected mutex releases - letting a second instance start.
        private static Mutex _mutex;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// True when this process is the first instance. False means another one is already
        /// running and this one should hand over and exit.
        /// </summary>
        public static bool TryAcquire()
        {
            try
            {
                _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);

                // Not created-new means someone else holds it. Release our claim so the handle
                // does not linger, but keep the object referenced until exit.
                if (!createdNew)
                {
                    _mutex.Dispose();
                    _mutex = null;
                }
                return createdNew;
            }
            catch (Exception ex)
            {
                // Never let this stop the app starting: a machine where the mutex cannot be
                // created is better off running a second copy than running none.
                DebugLogger.Warn($"SingleInstance: could not check for a running instance: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// Asks the already-running instance to restore its window.
        ///
        /// Broadcast rather than aimed at a specific handle: the running copy may be hidden in
        /// the tray, so there is no window to find by title. Hidden top-level windows still
        /// receive broadcasts, which is exactly the case that needs to work. Both processes run
        /// elevated (the app manifest requires admin), so they share an integrity level and UIPI
        /// does not filter the message.
        /// </summary>
        public static void SignalExistingInstance()
        {
            try
            {
                if (ShowInstanceMessage == 0)
                {
                    DebugLogger.Warn("SingleInstance: the show message was never registered; cannot signal.");
                    return;
                }
                PostMessage((IntPtr)HWND_BROADCAST, ShowInstanceMessage, IntPtr.Zero, IntPtr.Zero);
                DebugLogger.Info("SingleInstance: asked the running instance to show itself.");
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"SingleInstance: could not signal the running instance: {ex.Message}");
            }
        }

        /// <summary>Released at exit so the next launch can claim it.</summary>
        public static void Release()
        {
            try
            {
                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"SingleInstance: releasing the instance lock failed: {ex.Message}");
            }
            finally
            {
                _mutex = null;
            }
        }
    }
}
