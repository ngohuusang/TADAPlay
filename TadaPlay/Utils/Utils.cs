using AntdUI; 
using System;
using System.Windows.Forms;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Provides utility methods for common UI operations, especially cross-thread marshalling.
    /// </summary>
    public static class UiUtils
    {
        /// <summary>
        /// Safely executes an action on the UI thread of the given control.
        /// Prevents "Cross-thread operation not valid" exceptions.
        /// </summary>
        /// <param name="control">The UI control whose thread should execute the action (e.g., MainForm or Home).</param>
        /// <param name="action">The action to execute on the UI thread.</param>
        /// <param name="logTag">An optional tag for logging if UI handle is not ready.</param>
        public static void InvokeOnUiThread(Control control, Action action, string logTag = "UI_UPDATE")
        {
            if (control == null)
            {
                DebugLogger.Error($"UiUtils: Attempted UI update on null control. Tag: {logTag}");
                return;
            }

            if (control.InvokeRequired && control.IsHandleCreated)
            {
                control.BeginInvoke(action);
            }
            else if (!control.InvokeRequired) // Already on UI thread
            {
                action.Invoke();
            }
            else // Handle early calls before handle creation (log only)
            {
                Console.WriteLine($"[WARNING - {logTag} - No UI Handle] Attempted UI update before handle creation.");
                DebugLogger.Warn($"[WARNING - {logTag} - No UI Handle] Attempted UI update before handle creation.");
            }
        }

        /// <summary>
        /// Opens an AntdUI modal dialog safely on the UI thread.
        /// </summary>
        /// <param name="owner">The owner Form of the modal.</param>
        /// <param name="title">The title of the modal.</param>
        /// <param name="message">The message content of the modal.</param>
        /// <param name="type">The type of modal (e.g., Error, Warning, Info).</param>
        /// <param name="okText">Text for the OK button (default: "Đóng").</param>
        /// <param name="cancelText">Text for the Cancel button (default: null).</param>
        public static void ShowAntdModal(
            Form owner,
            string title,
            string message,
            TType type,
            string okText = null,
            string cancelText = null)
        {
            // Ensure owner is not null and has a handle before attempting to show modal
            if (owner == null || !owner.IsHandleCreated || owner.IsDisposed)
            {
                DebugLogger.Warn($"UiUtils: Cannot show AntdUI Modal. Owner form is null, not created, or disposed. Title: {title}, Message: {message}");
                // Fallback to MessageBox if owner is not ready
                MessageBox.Show(message, title, MessageBoxButtons.OK, type == TType.Error ? MessageBoxIcon.Error : MessageBoxIcon.Information);
                return;
            }

            InvokeOnUiThread(owner, () =>
            {
                AntdUI.Modal.open(new AntdUI.Modal.Config(owner, title, message, type)
                {
                    CancelText = cancelText ?? AntdUI.Localization.Get("CloseButton", "Đóng"),
                    OkText = okText ?? AntdUI.Localization.Get("CloseButton", "Đóng")
                });
            }, "SHOW_ANTD_MODAL");
        }
    }
}