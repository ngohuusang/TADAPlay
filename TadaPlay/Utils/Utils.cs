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
                    // CancelText = null hides the Cancel button (AntdUI's documented way to do
                    // so) - these are single-action alert dialogs, so unless a caller explicitly
                    // asks for a two-button confirm/cancel dialog via cancelText, only show the
                    // one dismiss button. Previously this defaulted both buttons to "Đóng",
                    // rendering two identical "Đóng" buttons on every alert (e.g. upload errors).
                    CancelText = cancelText,
                    OkText = okText ?? AntdUI.Localization.Get("CloseButton", "Đóng")
                });
            }, "SHOW_ANTD_MODAL");
        }

        /// <summary>
        /// Stretches a Details-view ListView's last column to fill any remaining width, so rows
        /// (and zebra-stripe backgrounds) span the full control instead of leaving a blank gap
        /// on the right when the window is wider than the sum of the defined column widths.
        /// Call once after adding columns, and wire to the ListView's Resize event.
        /// </summary>
        /// <summary>
        /// Asks for a single line of text. Returns null if the user cancelled, so an empty
        /// string stays distinguishable from "did not answer".
        /// </summary>
        /// <param name="maxLength">Enforced in the box so a too-long value cannot be submitted.</param>
        public static string PromptForText(Form owner, string title, string prompt,
                                           string initial = "", int maxLength = 255)
        {
            using var dialog = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = owner == null ? FormStartPosition.CenterScreen
                                              : FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(420, 150),
                Font = new Font("Segoe UI", 9.75F),
                BackColor = Color.FromArgb(245, 245, 245)
            };

            var label = new System.Windows.Forms.Label
            {
                Text = prompt,
                Location = new Point(16, 16),
                Size = new Size(388, 22)
            };

            var box = new TextBox
            {
                Text = initial ?? "",
                Location = new Point(16, 44),
                Size = new Size(388, 28),
                MaxLength = maxLength,
                Font = new Font("Segoe UI", 10.5F)
            };
            box.SelectAll();

            var ok = new System.Windows.Forms.Button
            {
                Text = "Lưu",
                DialogResult = DialogResult.OK,
                Location = new Point(212, 92),
                Size = new Size(90, 34)
            };
            var cancel = new System.Windows.Forms.Button
            {
                Text = "Huỷ",
                DialogResult = DialogResult.Cancel,
                Location = new Point(312, 92),
                Size = new Size(90, 34)
            };

            dialog.Controls.Add(label);
            dialog.Controls.Add(box);
            dialog.Controls.Add(ok);
            dialog.Controls.Add(cancel);
            dialog.AcceptButton = ok;
            dialog.CancelButton = cancel;

            return dialog.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
        }

        public static void StretchLastListViewColumn(ListView listView, int minWidth)
        {
            if (listView.Columns.Count == 0) return;

            int othersWidth = 0;
            for (int i = 0; i < listView.Columns.Count - 1; i++)
            {
                othersWidth += listView.Columns[i].Width;
            }

            // Leave a little room for the vertical scrollbar so the stretched column doesn't
            // get clipped under it once the list has enough rows to scroll.
            int available = listView.ClientSize.Width - othersWidth - SystemInformation.VerticalScrollBarWidth;
            listView.Columns[listView.Columns.Count - 1].Width = Math.Max(minWidth, available);
        }
    }
}