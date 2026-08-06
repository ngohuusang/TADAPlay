using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using TadaPlay.Logger;
using TadaPlay.Utils;

namespace TadaPlay.Controls
{
    /// <summary>
    /// In-app editor for AoE2 (AoC) player*.hki hotkey files, so a player can rebind keys and
    /// import a hotkey layout from another .hki file without opening the game's own options menu.
    /// Reads/writes via <see cref="HotkeyFile"/> and names commands via <see cref="GameLanguageStrings"/>.
    /// </summary>
    public class HotkeyEditorForm : Form
    {
        private readonly string _gameFolder;
        private readonly List<string> _slotFiles;      // player*.hki found under the game folder
        private Dictionary<int, string> _strings;

        private ComboBox _slotCombo;
        private Panel _listPanel;
        private Label _statusLabel;
        private HotkeyFile _file;                       // working copy currently being edited
        private bool _dirty;

        // Which key-button (if any) is waiting to capture the next key press.
        private Button _capturing;
        private HotkeyBinding _capturingBinding;
        private string _capturingRestoreText;

        // Friendly headers for the standard AoC groups; anything past the well-known ones falls
        // back to a generic "Nhóm N". The command names themselves come from the language DLLs.
        private static readonly Dictionary<int, string> GroupTitles = new Dictionary<int, string>
        {
            { 0, "Lệnh đơn vị" },
            { 1, "Lệnh chung & điều hướng" },
            { 2, "Cuộn màn hình" },
            { 3, "Xây dựng" },
        };

        public HotkeyEditorForm(string gameFolder)
        {
            _gameFolder = gameFolder;
            _slotFiles = FindSlotFiles(gameFolder);

            Text = "Chỉnh sửa phím tắt trong game";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(720, 640);
            Size = new Size(760, 820);
            Font = new Font("Segoe UI", 10F);

            BuildLayout();

            _strings = GameLanguageStrings.Load(gameFolder);

            if (_slotFiles.Count == 0)
            {
                _statusLabel.Text = "Không tìm thấy tệp phím tắt (player*.hki). Hãy dùng \"Nhập từ tệp...\" để tạo mới.";
            }
            else
            {
                _slotCombo.SelectedIndex = 0; // triggers load
            }
        }

        private void BuildLayout()
        {
            var top = new Panel { Dock = DockStyle.Top, Height = 96, Padding = new Padding(12, 10, 12, 6) };

            var slotLabel = new Label { Text = "Hồ sơ phím tắt:", AutoSize = true, Location = new Point(12, 14) };
            _slotCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(140, 10),
                Width = 300,
            };
            foreach (string f in _slotFiles) _slotCombo.Items.Add(Path.GetFileName(f));
            _slotCombo.SelectedIndexChanged += OnSlotChanged;

            var importButton = new Button { Text = "Nhập từ tệp...", Location = new Point(460, 9), Width = 130, Height = 30 };
            importButton.Click += ImportFromFile;

            var hint = new Label
            {
                Text = "Nhấn vào ô phím để đặt lại. Đang thu phím: nhấn phím mới, hoặc Esc để hủy, Delete để bỏ gán.",
                AutoSize = false,
                Location = new Point(12, 50),
                Size = new Size(710, 38),
                ForeColor = Color.DimGray,
            };

            top.Controls.Add(slotLabel);
            top.Controls.Add(_slotCombo);
            top.Controls.Add(importButton);
            top.Controls.Add(hint);

            _listPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12, 6, 12, 6) };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(12, 10, 12, 10) };
            _statusLabel = new Label { Dock = DockStyle.Left, AutoSize = false, Width = 300, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DimGray };

            var closeButton = new Button { Text = "Đóng", Dock = DockStyle.Right, Width = 110, Height = 34 };
            closeButton.Click += (s, e) => Close();
            var saveAllButton = new Button { Text = "Lưu cho mọi hồ sơ", Dock = DockStyle.Right, Width = 160, Height = 34 };
            saveAllButton.Click += (s, e) => Save(allSlots: true);
            var saveButton = new Button { Text = "Lưu", Dock = DockStyle.Right, Width = 110, Height = 34 };
            saveButton.Click += (s, e) => Save(allSlots: false);

            // Dock=Right stacks right-to-left in add order.
            bottom.Controls.Add(_statusLabel);
            bottom.Controls.Add(closeButton);
            bottom.Controls.Add(saveAllButton);
            bottom.Controls.Add(saveButton);

            Controls.Add(_listPanel);
            Controls.Add(bottom);
            Controls.Add(top);
        }

        private static List<string> FindSlotFiles(string gameFolder)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder)) return list;
            try
            {
                list = Directory.EnumerateFiles(gameFolder, "player*.hki", SearchOption.AllDirectories)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"HotkeyEditor: enumerate player*.hki failed: {ex.Message}");
            }
            return list;
        }

        private void LoadSelectedSlot()
        {
            CancelCapture();
            int idx = _slotCombo.SelectedIndex;
            if (idx < 0 || idx >= _slotFiles.Count) return;
            try
            {
                _file = HotkeyFile.Load(_slotFiles[idx]);
                _dirty = false;
                RenderList();
                SetStatus($"Đã tải {Path.GetFileName(_slotFiles[idx])}.");
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"HotkeyEditor: load '{_slotFiles[idx]}' failed: {ex.Message}");
                MessageBox.Show(this, $"Không đọc được tệp phím tắt:\n{ex.Message}", "Lỗi",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RenderList()
        {
            _listPanel.SuspendLayout();
            _listPanel.Controls.Clear();

            int width = 660;
            int y = 6;
            const int rowH = 30;

            if (_file != null)
            {
                for (int g = 0; g < _file.Groups.Count; g++)
                {
                    var commands = _file.Groups[g].Bindings.Where(b => b.IsCommand).ToList();
                    if (commands.Count == 0) continue;

                    string title = GroupTitles.TryGetValue(g, out string t) ? t : $"Nhóm {g + 1}";
                    var header = new Label
                    {
                        Text = title,
                        Font = new Font(Font, FontStyle.Bold),
                        Location = new Point(4, y),
                        Size = new Size(width, 26),
                        ForeColor = Color.FromArgb(0x1E, 0x5A, 0x8A),
                    };
                    _listPanel.Controls.Add(header);
                    y += 30;

                    foreach (var b in commands)
                    {
                        var name = new Label
                        {
                            Text = ResolveName(b.StringId),
                            Location = new Point(16, y + 4),
                            Size = new Size(400, rowH - 6),
                            TextAlign = ContentAlignment.MiddleLeft,
                        };
                        var keyBtn = new Button
                        {
                            Location = new Point(430, y),
                            Size = new Size(210, rowH - 2),
                            TextAlign = ContentAlignment.MiddleLeft,
                            Tag = b,
                        };
                        SetKeyButtonText(keyBtn, b);
                        keyBtn.Click += KeyButton_Click;
                        _listPanel.Controls.Add(name);
                        _listPanel.Controls.Add(keyBtn);
                        y += rowH;
                    }
                    y += 10;
                }
            }

            _listPanel.AutoScrollMinSize = new Size(0, y + 10);
            _listPanel.ResumeLayout();
        }

        private string ResolveName(int stringId)
        {
            if (_strings != null && _strings.TryGetValue(stringId, out string s))
            {
                // Some entries carry a trailing "\n<hint>" second line - keep just the command name.
                int nl = s.IndexOfAny(new[] { '\r', '\n' });
                return nl >= 0 ? s.Substring(0, nl) : s;
            }
            return $"[{stringId}]";
        }

        private void SetKeyButtonText(Button btn, HotkeyBinding b)
        {
            btn.Text = "  " + HotkeyKeyNames.Describe(b.KeyCode, b.Ctrl, b.Alt, b.Shift);
            btn.ForeColor = b.KeyCode == HotkeyKeyNames.Unbound ? Color.Gray : SystemColors.ControlText;
        }

        private void KeyButton_Click(object sender, EventArgs e)
        {
            var btn = (Button)sender;
            if (_capturing == btn) { CancelCapture(); return; }
            CancelCapture();

            _capturing = btn;
            _capturingBinding = (HotkeyBinding)btn.Tag;
            _capturingRestoreText = btn.Text;
            btn.Text = "  Nhấn phím...";
            btn.BackColor = Color.FromArgb(0xFF, 0xF3, 0xC4);
            SetStatus("Đang thu phím... nhấn phím mới (kèm Ctrl/Alt/Shift nếu muốn), Delete để bỏ gán, Esc để hủy.");
        }

        // ProcessCmdKey sees every key before dialog navigation (Tab/arrows/Space/Enter), which is
        // exactly what we need to capture an arbitrary binding.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_capturing == null) return base.ProcessCmdKey(ref msg, keyData);

            Keys code = keyData & Keys.KeyCode;

            if (code == Keys.Escape) { CancelCapture(); return true; }

            if (code == Keys.Delete || code == Keys.Back)
            {
                ApplyCapture(HotkeyKeyNames.Unbound, false, false, false);
                return true;
            }

            if (HotkeyKeyNames.IsModifierOnly(code)) return true; // wait for the real key

            bool ctrl = (keyData & Keys.Control) != 0;
            bool alt = (keyData & Keys.Alt) != 0;
            bool shift = (keyData & Keys.Shift) != 0;
            ApplyCapture((int)code, ctrl, alt, shift);
            return true;
        }

        private void ApplyCapture(int keyCode, bool ctrl, bool alt, bool shift)
        {
            if (_capturing == null || _capturingBinding == null) return;

            string conflict = FindConflict(keyCode, ctrl, alt, shift, _capturingBinding);

            _capturingBinding.KeyCode = keyCode;
            _capturingBinding.Ctrl = ctrl;
            _capturingBinding.Alt = alt;
            _capturingBinding.Shift = shift;

            SetKeyButtonText(_capturing, _capturingBinding);
            _capturing.BackColor = SystemColors.Control;
            _capturing = null;
            _capturingBinding = null;
            _dirty = true;

            if (keyCode == HotkeyKeyNames.Unbound)
                SetStatus("Đã bỏ gán.");
            else if (conflict != null)
                SetStatus($"Đã gán. Lưu ý: phím này trùng với \"{conflict}\" trong cùng nhóm.");
            else
                SetStatus("Đã gán phím. Nhớ bấm Lưu.");
        }

        // A key is "in conflict" only within the same group (different groups are different in-game
        // contexts and legitimately reuse keys). Returns the other command's name, or null.
        private string FindConflict(int keyCode, bool ctrl, bool alt, bool shift, HotkeyBinding self)
        {
            if (keyCode == HotkeyKeyNames.Unbound || _file == null) return null;
            var group = _file.Groups.FirstOrDefault(gr => gr.Bindings.Contains(self));
            if (group == null) return null;
            foreach (var b in group.Bindings)
            {
                if (ReferenceEquals(b, self) || !b.IsCommand) continue;
                if (b.KeyCode == keyCode && b.Ctrl == ctrl && b.Alt == alt && b.Shift == shift)
                    return ResolveName(b.StringId);
            }
            return null;
        }

        private void CancelCapture()
        {
            if (_capturing == null) return;
            _capturing.Text = _capturingRestoreText;
            _capturing.BackColor = SystemColors.Control;
            _capturing = null;
            _capturingBinding = null;
        }

        private void ImportFromFile(object sender, EventArgs e)
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Chọn tệp phím tắt (.hki) để nhập",
                Filter = "Tệp phím tắt AoE2 (*.hki)|*.hki|Tất cả tệp (*.*)|*.*",
                InitialDirectory = _slotFiles.Count > 0 ? Path.GetDirectoryName(_slotFiles[0]) : _gameFolder,
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                var imported = HotkeyFile.Load(dlg.FileName);
                _file = imported;
                _dirty = true;
                RenderList();
                SetStatus($"Đã nhập từ {Path.GetFileName(dlg.FileName)}. Bấm Lưu để áp dụng cho hồ sơ đã chọn.");
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"HotkeyEditor: import '{dlg.FileName}' failed: {ex.Message}");
                MessageBox.Show(this, $"Không đọc được tệp phím tắt đã chọn:\n{ex.Message}", "Lỗi",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Save(bool allSlots)
        {
            CancelCapture();
            if (_file == null) { SetStatus("Chưa có gì để lưu."); return; }

            try
            {
                List<string> targets;
                if (allSlots)
                {
                    // Write the layout to player1..player5 so it applies whichever profile the game
                    // has selected. Base the folder on an existing slot, else the game folder root.
                    string dir = (_slotFiles.Count > 0
                        ? Path.GetDirectoryName(_slotFiles[0])
                        : _gameFolder) ?? _gameFolder;
                    targets = Enumerable.Range(1, 5).Select(i => Path.Combine(dir, $"player{i}.hki")).ToList();
                }
                else if (_slotCombo.SelectedIndex >= 0 && _slotCombo.SelectedIndex < _slotFiles.Count)
                {
                    targets = new List<string> { _slotFiles[_slotCombo.SelectedIndex] };
                }
                else
                {
                    // No existing slot selected (e.g. after importing into an empty folder) - create player1.
                    targets = new List<string> { Path.Combine(_gameFolder, "player1.hki") };
                }

                foreach (string target in targets) _file.Save(target);
                _dirty = false;

                RefreshSlotList(targets);
                SetStatus(allSlots
                    ? $"Đã lưu cho {targets.Count} hồ sơ (player1–5)."
                    : $"Đã lưu {Path.GetFileName(targets[0])}.");
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"HotkeyEditor: save failed: {ex.Message}");
                MessageBox.Show(this, $"Lưu thất bại:\n{ex.Message}", "Lỗi",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Pick up any newly-created slot files (e.g. after "save for all profiles") while keeping the
        // current selection pointing at a real file - without reloading from disk, which would throw
        // away the just-saved in-memory model.
        private void RefreshSlotList(List<string> justSaved)
        {
            string current = _slotCombo.SelectedIndex >= 0 && _slotCombo.SelectedIndex < _slotFiles.Count
                ? _slotFiles[_slotCombo.SelectedIndex]
                : justSaved.FirstOrDefault();

            var refreshed = FindSlotFiles(_gameFolder);
            if (refreshed.Count == _slotFiles.Count) return; // nothing new to show

            _slotFiles.Clear();
            _slotFiles.AddRange(refreshed);

            _slotCombo.SelectedIndexChanged -= OnSlotChanged;
            _slotCombo.Items.Clear();
            foreach (string f in _slotFiles) _slotCombo.Items.Add(Path.GetFileName(f));

            int idx = _slotFiles.FindIndex(f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _slotCombo.SelectedIndex = idx; // display only; handler is detached
            _slotCombo.SelectedIndexChanged += OnSlotChanged;
        }

        private void OnSlotChanged(object sender, EventArgs e) => LoadSelectedSlot();

        private void SetStatus(string text) => _statusLabel.Text = text;

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_dirty)
            {
                var r = MessageBox.Show(this, "Bạn có thay đổi chưa lưu. Đóng mà không lưu?",
                    "Chưa lưu", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r == DialogResult.No) { e.Cancel = true; return; }
            }
            base.OnFormClosing(e);
        }
    }
}
