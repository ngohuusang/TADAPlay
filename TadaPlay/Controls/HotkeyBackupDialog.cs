using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AntdUI;
using TadaPlay.Common.Models;
using TadaPlay.Logger;
using TadaPlay.Services.Interface;

namespace TadaPlay.Controls
{
    /// <summary>
    /// Picks one of the player's hotkey backups to pull back down, or deletes one.
    ///
    /// A list of cards rather than a table with a selected row: every backup offers exactly two
    /// things to do with it, and putting those buttons on the row itself means there is no
    /// selection to get wrong - no "which one did I have highlighted?" between reading a name
    /// and pressing Restore.
    ///
    /// Restoring does not write anything here. The bytes are handed back to the editor, which
    /// loads them like an imported file and leaves the player to press Save - the same shape as
    /// every other way a layout arrives, and a pull they did not mean costs them a Close.
    /// </summary>
    public class HotkeyBackupDialog : Form
    {
        private readonly IAccountService _accountService;
        private readonly System.Windows.Forms.Panel _list;
        private readonly AntdUI.Label _status;

        /// <summary>The chosen backup's .hki bytes, set when DialogResult is OK.</summary>
        public byte[] RestoredBytes { get; private set; }

        /// <summary>The chosen backup's name, for the message the editor shows afterwards.</summary>
        public string RestoredName { get; private set; }

        public HotkeyBackupDialog(IAccountService accountService)
        {
            _accountService = accountService;

            Text = "Bản sao lưu phím tắt";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(560, 460);
            Font = new Font("Segoe UI", 9.75F);
            BackColor = UiTheme.PageBg;

            var heading = new AntdUI.Label
            {
                Dock = DockStyle.Top,
                Height = 46,
                Text = "Chọn một bản sao lưu để lấy về",
                Padding = new Padding(16, 10, 16, 0),
                ForeColor = UiTheme.Ink,
                Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold),
                BackColor = Color.Transparent,
            };

            _list = new System.Windows.Forms.Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(14, 4, 14, 8),
                BackColor = Color.Transparent,
            };

            var bottom = new System.Windows.Forms.Panel { Dock = DockStyle.Bottom, Height = 60, Padding = new Padding(14, 8, 14, 12), BackColor = Color.Transparent };
            _status = new AntdUI.Label
            {
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.Muted,
                Font = new Font("Segoe UI", 9.5F),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                BackColor = Color.Transparent,
            };
            var close = UiTheme.Quiet("Đóng");
            close.Dock = DockStyle.Right;
            close.Width = 110;
            close.DialogResult = DialogResult.Cancel;

            bottom.Controls.Add(_status);
            bottom.Controls.Add(close);

            Controls.Add(_list);
            Controls.Add(bottom);
            Controls.Add(heading);
            CancelButton = close;

            Load += async (s, e) => await LoadAsync();
        }

        private async System.Threading.Tasks.Task LoadAsync()
        {
            SetStatus("Đang tải danh sách sao lưu...");
            try
            {
                List<HotkeyBackup> backups = await _accountService.GetHotkeyBackupsAsync();
                Render(backups);
                SetStatus(backups.Count == 0
                    ? "Chưa có bản sao lưu nào."
                    : $"{backups.Count} bản sao lưu.");
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"HotkeyBackupDialog: load failed: {ex.Message}");
                SetStatus("Không tải được danh sách: " + ex.Message);
                AntdUI.Message.error(this, ex.Message);
            }
        }

        private void Render(List<HotkeyBackup> backups)
        {
            foreach (Control old in _list.Controls.Cast<Control>().ToList()) old.Dispose();
            _list.Controls.Clear();

            if (backups.Count == 0)
            {
                _list.Controls.Add(new AntdUI.Label
                {
                    Dock = DockStyle.Top,
                    Height = 60,
                    Text = "Chưa có bản sao lưu nào. Bấm \"Sao lưu lên tài khoản\" trong trình " +
                           "chỉnh sửa để tạo bản đầu tiên.",
                    ForeColor = UiTheme.Muted,
                    TextMultiLine = true,
                    BackColor = Color.Transparent,
                });
                return;
            }

            // Dock=Top stacks in reverse add order, so walk backwards to keep the server's
            // newest-first ordering on screen.
            for (int i = backups.Count - 1; i >= 0; i--)
            {
                _list.Controls.Add(BuildRow(backups[i]));
            }
        }

        private Control BuildRow(HotkeyBackup backup)
        {
            var card = new AntdUI.Panel
            {
                Dock = DockStyle.Top,
                Height = 76,
                Margin = new Padding(0, 0, 0, 10),
                Radius = 8,
                Back = Color.White,
                BorderWidth = 1,
                BorderColor = UiTheme.CardBorder,
                Padding = new Padding(12, 8, 12, 8),
            };

            var name = new AntdUI.Label
            {
                Location = new Point(14, 10),
                Size = new Size(300, 24),
                Text = backup.Name,
                ForeColor = UiTheme.Ink,
                Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold),
                AutoEllipsis = true,
                BackColor = Color.Transparent,
            };

            string when = backup.CreatedLocal.HasValue
                ? backup.CreatedLocal.Value.ToString("dd/MM/yyyy HH:mm")
                : backup.CreatedAt;
            var detail = new AntdUI.Label
            {
                Location = new Point(14, 36),
                Size = new Size(300, 20),
                Text = $"{when}  ·  {backup.ByteSize} byte",
                ForeColor = UiTheme.Muted,
                Font = new Font("Segoe UI", 9F),
                AutoEllipsis = true,
                BackColor = Color.Transparent,
            };

            var restore = UiTheme.Primary("Lấy về", UiTheme.AccentFolder, height: 36);
            restore.Dock = DockStyle.None;
            restore.Location = new Point(330, 20);
            restore.Size = new Size(110, 36);
            restore.Click += async (s, e) => await RestoreAsync(backup, restore);

            var remove = UiTheme.Toolbar("Xóa", new Point(450, 20), 70, 36);
            remove.Type = TTypeMini.Error;
            remove.Ghost = true;
            remove.Click += async (s, e) => await DeleteAsync(backup);

            card.Controls.Add(name);
            card.Controls.Add(detail);
            card.Controls.Add(restore);
            card.Controls.Add(remove);
            return card;
        }

        private async System.Threading.Tasks.Task RestoreAsync(HotkeyBackup backup, AntdUI.Button button)
        {
            button.Loading = true;
            SetStatus($"Đang tải \"{backup.Name}\"...");
            try
            {
                RestoredBytes = await _accountService.RestoreHotkeysAsync(backup.Id);
                RestoredName = backup.Name;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"HotkeyBackupDialog: restore {backup.Id} failed: {ex.Message}");
                SetStatus("Không lấy được bản sao lưu.");
                AntdUI.Message.error(this, ex.Message);
                if (!button.IsDisposed) button.Loading = false;
            }
        }

        private async System.Threading.Tasks.Task DeleteAsync(HotkeyBackup backup)
        {
            var confirm = MessageBox.Show(this,
                $"Xóa bản sao lưu \"{backup.Name}\"?\nHành động này không thể hoàn tác.",
                "Xóa bản sao lưu", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            try
            {
                await _accountService.DeleteHotkeyBackupAsync(backup.Id);
                AntdUI.Message.success(this, $"Đã xóa \"{backup.Name}\".");
                await LoadAsync();
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"HotkeyBackupDialog: delete {backup.Id} failed: {ex.Message}");
                AntdUI.Message.error(this, ex.Message);
            }
        }

        private void SetStatus(string text)
        {
            if (!IsDisposed) _status.Text = text;
        }
    }
}
