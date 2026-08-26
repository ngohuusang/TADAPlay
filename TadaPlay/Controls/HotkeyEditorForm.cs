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
        private readonly string _hotkeyDir;             // the Voobly "Game Data" folder the game reads player*.hki from
        private readonly List<string> _slotFiles;       // player*.hki in _hotkeyDir
        private Dictionary<int, string> _strings;

        private AntdUI.Select _slotCombo;
        private AntdUI.Input _filterBox;   // narrows both panes to commands matching this
        private AntdUI.Menu _groupList;    // master: one row per group that has commands
        private AntdUI.Label _detailHeader; // which group the detail pane is showing
        private Panel _listPanel;          // detail: the selected group's bindings
        private AntdUI.Label _statusLabel;

        /// <summary>
        /// Which group the detail pane is showing, as an index into <see cref="HotkeyFile.Groups"/>.
        ///
        /// Kept as the file's own index rather than the row number in the master list, because
        /// the two do not line up: groups with no named commands are left out of the list. It
        /// also has to survive a reload - loading another profile, importing, resetting - so the
        /// player is put back where they were looking instead of at the top.
        /// </summary>
        private int _selectedGroup = -1;
        private HotkeyFile _file;                       // working copy currently being edited
        private bool _dirty;

        // Which key-button (if any) is waiting to capture the next key press.
        private AntdUI.Button _capturing;
        private HotkeyBinding _capturingBinding;
        private string _capturingRestoreText;

        // Friendly headers for the standard AoC groups; anything past the well-known ones falls
        // back to a generic "Nhóm N". The command names themselves come from the language DLLs.
        /// <summary>Alternate-row band in the detail pane. Faint on purpose - it is a guide
        /// between a command and its key, not a thing to look at.</summary>
        private static readonly Color RowBandColor = Color.FromArgb(0xF4, 0xF7, 0xFA);

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
            _hotkeyDir = ResolveHotkeyDirectory(gameFolder);
            _slotFiles = FindSlotFiles(_hotkeyDir);

            Text = "Chỉnh sửa phím tắt trong game";
            StartPosition = FormStartPosition.CenterParent;
            // Wider than the old single list: the master pane costs horizontal room, and the
            // detail rows still need to fit a long command name beside a key button.
            MinimumSize = new Size(820, 640);
            Size = new Size(920, 820);
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
            // AntdUI throughout, like the settings screen: the app already ships it, and it is
            // what gives the rounded fields, the hover states and the focus rings that would
            // otherwise have to be painted here by hand.
            BackColor = UiTheme.PageBg;

            // --- top bar: which profile, and what to do to it ---
            var top = new Panel { Dock = DockStyle.Top, Height = 104, Padding = new Padding(14, 12, 14, 6), BackColor = Color.Transparent };

            var slotLabel = new AntdUI.Label
            {
                Text = "Hồ sơ phím tắt",
                Location = new Point(14, 14),
                Size = new Size(140, 32),
                ForeColor = UiTheme.Muted,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            _slotCombo = new AntdUI.Select
            {
                Location = new Point(150, 10),
                Width = 260,
                Height = 36,
                Radius = 8,
                PlaceholderText = "Chưa có hồ sơ",
            };
            foreach (string f in _slotFiles) _slotCombo.Items.Add(Path.GetFileName(f));
            _slotCombo.SelectedIndexChanged += OnSlotChanged;

            var importButton = UiTheme.Toolbar("Nhập từ tệp...", new Point(424, 10), 132);
            importButton.Click += ImportFromFile;

            var resetButton = UiTheme.Toolbar("Đặt lại mặc định", new Point(566, 10), 146);
            resetButton.Click += ResetToDefault;

            // Danger-typed and last in the row: it is the only button here that destroys
            // something, and it should not sit next to the two that do not.
            var deleteButton = UiTheme.Toolbar("Xóa hồ sơ", new Point(722, 10), 110);
            deleteButton.Type = AntdUI.TTypeMini.Error;
            deleteButton.Ghost = true;
            deleteButton.Click += DeleteSelectedSlot;

            var hint = new AntdUI.Label
            {
                Text = "Nhấn vào ô phím để đặt lại. Đang thu phím: nhấn phím mới, hoặc Esc để hủy, Delete để bỏ gán.",
                Location = new Point(14, 54),
                Size = new Size(818, 38),
                ForeColor = UiTheme.Muted,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                TextMultiLine = true,
            };

            top.Controls.Add(slotLabel);
            top.Controls.Add(_slotCombo);
            top.Controls.Add(importButton);
            top.Controls.Add(resetButton);
            top.Controls.Add(deleteButton);
            top.Controls.Add(hint);

            // --- master/detail. One long scroll of every group meant hunting for a heading
            // among two hundred rows; the groups are separate in-game contexts, so picking one
            // and seeing only its keys is how the file is actually organised. ---
            _groupList = new AntdUI.Menu
            {
                Dock = DockStyle.Left,
                Width = 250,
                Radius = 8,
                BackColor = Color.White,
                Padding = new Padding(6),
            };
            _groupList.SelectChanged += OnGroupChanged;

            _detailHeader = new AntdUI.Label
            {
                Dock = DockStyle.Top,
                Height = 40,
                Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold),
                ForeColor = UiTheme.AccentDisplay,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(14, 0, 0, 0),
                BackColor = Color.Transparent,
            };

            _listPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(8, 4, 8, 8),
                BackColor = Color.Transparent,
            };

            var detailCard = new AntdUI.Panel
            {
                Dock = DockStyle.Fill,
                Radius = 8,
                Back = Color.White,
                BorderWidth = 1,
                BorderColor = UiTheme.CardBorder,
                Padding = new Padding(0),
            };
            detailCard.Controls.Add(_listPanel);   // Fill first, then the edges
            detailCard.Controls.Add(_detailHeader);

            var detailHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 0, 0), BackColor = Color.Transparent };
            detailHost.Controls.Add(detailCard);

            // Spans both panes because it narrows both: a search that only looked inside the
            // group already open would be no help to someone who does not know which group a
            // command lives in, which is the usual reason for searching at all.
            var filterBar = new Panel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(0, 6, 0, 8), BackColor = Color.Transparent };
            _filterBox = new AntdUI.Input
            {
                Dock = DockStyle.Fill,
                Radius = 8,
                PlaceholderText = "Tìm lệnh... (ví dụ: pali, farm, wall)",
                PrefixSvg = UiTheme.IconSearch,
                AllowClear = true,      // antd's own clear button - no separate "Xóa lọc" needed
                Font = new Font("Segoe UI", 10.5F),
            };
            _filterBox.TextChanged += (s, e) => OnFilterChanged();
            filterBar.Controls.Add(_filterBox);

            var centre = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 0, 14, 8), BackColor = Color.Transparent };
            centre.Controls.Add(detailHost);
            centre.Controls.Add(_groupList);
            // Added last so it docks first and spans the full width, above both panes.
            centre.Controls.Add(filterBar);

            // --- bottom bar ---
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(14, 10, 14, 12), BackColor = Color.Transparent };
            _statusLabel = new AntdUI.Label
            {
                Dock = DockStyle.Fill,
                Text = string.Empty,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiTheme.Muted,
                Font = new Font("Segoe UI", 9.5F),
                AutoEllipsis = true,
                BackColor = Color.Transparent,
            };

            var closeButton = UiTheme.Quiet("Đóng");
            closeButton.Dock = DockStyle.Right;
            closeButton.Width = 110;
            closeButton.Click += (s, e) => Close();

            var saveAllButton = UiTheme.Quiet("Lưu cho mọi hồ sơ");
            saveAllButton.Dock = DockStyle.Right;
            saveAllButton.Width = 180;
            saveAllButton.Margin = new Padding(8, 0, 8, 0);
            saveAllButton.Click += (s, e) => Save(allSlots: true);

            var saveButton = UiTheme.Primary("Lưu", UiTheme.AccentFolder, height: 40);
            saveButton.Dock = DockStyle.Right;
            saveButton.Width = 130;
            saveButton.Click += (s, e) => Save(allSlots: false);

            // Dock=Right stacks right-to-left in add order, and Fill goes in first.
            bottom.Controls.Add(_statusLabel);
            bottom.Controls.Add(closeButton);
            bottom.Controls.Add(UiTheme.Spacer(DockStyle.Right, 8));
            bottom.Controls.Add(saveAllButton);
            bottom.Controls.Add(UiTheme.Spacer(DockStyle.Right, 8));
            bottom.Controls.Add(saveButton);

            // Docking is applied in reverse z-order, so the Fill goes in first and the edges
            // after it - the same order the rest of this form already uses.
            Controls.Add(centre);
            Controls.Add(bottom);
            Controls.Add(top);
        }

        // The game loads player*.hki from the Voobly data mod's "Game Data" folder
        // (e.g. "<gameFolder>\Voobly Mods\AOC\Data Mods\v1.5 Game Data"), NOT the game root - so
        // reads and writes must target that folder for edits to reach the copy the game actually uses.
        private static string ResolveHotkeyDirectory(string gameFolder)
        {
            if (string.IsNullOrWhiteSpace(gameFolder)) return gameFolder;

            string dataMods = Path.Combine(gameFolder, "Voobly Mods", "AOC", "Data Mods");
            if (Directory.Exists(dataMods))
            {
                try
                {
                    // Prefer a "* Game Data" subfolder that already holds player*.hki; else the first
                    // one found; else fall back to the conventional v1.5 path.
                    var gameDataDirs = Directory.EnumerateDirectories(dataMods, "*Game Data").ToList();
                    string withHki = gameDataDirs.FirstOrDefault(
                        d => Directory.EnumerateFiles(d, "player*.hki").Any());
                    if (withHki != null) return withHki;
                    if (gameDataDirs.Count > 0) return gameDataDirs[0];
                }
                catch (Exception ex)
                {
                    DebugLogger.Warn($"HotkeyEditor: resolving Game Data folder failed: {ex.Message}");
                }
                return Path.Combine(dataMods, "v1.5 Game Data");
            }

            // No Voobly layout: fall back to wherever a player*.hki already lives, else the game folder.
            try
            {
                string any = Directory.EnumerateFiles(gameFolder, "player*.hki", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (any != null) return Path.GetDirectoryName(any);
            }
            catch { /* fall through to gameFolder */ }
            return gameFolder;
        }

        private static List<string> FindSlotFiles(string hotkeyDir)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(hotkeyDir) || !Directory.Exists(hotkeyDir)) return list;
            try
            {
                // Only this folder - not subfolders - so the game-root or a nested "Data" copy can't
                // shadow the real slot files.
                list = Directory.EnumerateFiles(hotkeyDir, "player*.hki", SearchOption.TopDirectoryOnly)
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
                int adopted = _file.AdoptModCommands(HasName);
                _dirty = false;
                RenderList();
                SetStatus(adopted > 0
                    ? $"Đã tải {Path.GetFileName(_slotFiles[idx])}. Hiện thêm {adopted} lệnh của bản " +
                      "mod chưa có trong tệp - đặt phím rồi bấm Lưu để áp dụng."
                    : $"Đã tải {Path.GetFileName(_slotFiles[idx])}.");
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"HotkeyEditor: load '{_slotFiles[idx]}' failed: {ex.Message}");
                MessageBox.Show(this, $"Không đọc được tệp phím tắt:\n{ex.Message}", "Lỗi",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DeleteSelectedSlot(object sender, EventArgs e)
        {
            CancelCapture();
            int idx = _slotCombo.SelectedIndex;
            if (idx < 0 || idx >= _slotFiles.Count) { SetStatus("Chưa chọn hồ sơ để xóa."); return; }

            string path = _slotFiles[idx];
            var r = MessageBox.Show(this,
                $"Xóa tệp phím tắt '{Path.GetFileName(path)}'?\nHành động này không thể hoàn tác.",
                "Xóa hồ sơ phím tắt", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;

            try
            {
                File.Delete(path);
                DebugLogger.Info($"HotkeyEditor: deleted '{path}'.");
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"HotkeyEditor: delete '{path}' failed: {ex.Message}");
                MessageBox.Show(this, $"Xóa thất bại:\n{ex.Message}", "Lỗi",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Rebuild the slot list from disk, keeping the selection near where it was.
            _slotCombo.SelectedIndexChanged -= OnSlotChanged;
            _slotFiles.Clear();
            _slotFiles.AddRange(FindSlotFiles(_hotkeyDir));
            _slotCombo.Items.Clear();
            foreach (string f in _slotFiles) _slotCombo.Items.Add(Path.GetFileName(f));

            if (_slotFiles.Count > 0)
            {
                _slotCombo.SelectedIndex = Math.Min(idx, _slotFiles.Count - 1);
                _slotCombo.SelectedIndexChanged += OnSlotChanged;
                LoadSelectedSlot();
                SetStatus($"Đã xóa {Path.GetFileName(path)}.");
            }
            else
            {
                _slotCombo.SelectedIndexChanged += OnSlotChanged;
                _file = null;
                _dirty = false;
                RenderList();
                SetStatus($"Đã xóa {Path.GetFileName(path)}. Không còn hồ sơ nào.");
            }
        }

        /// <summary>
        /// Whether a binding should be shown at all: it has to name a command, and match the
        /// filter if one is typed. Case-insensitive substring - the names are short and the
        /// player is usually typing the start of one ("pali", "far").
        /// </summary>
        private bool Matches(HotkeyBinding b)
        {
            if (!b.IsCommand) return false;
            string filter = _filterBox?.Text?.Trim();
            if (string.IsNullOrEmpty(filter)) return true;
            return ResolveName(b.StringId).IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private bool Filtering => !string.IsNullOrEmpty(_filterBox?.Text?.Trim());

        /// <summary>
        /// Re-narrows both panes. The group the player was in is kept when it still has a
        /// match, so typing does not throw them out of the group they were working in; when it
        /// stops matching they land on the first group that does, which is where the thing
        /// they searched for actually is.
        /// </summary>
        private void OnFilterChanged()
        {
            RenderList();
            if (_file == null) return;
            if (!Filtering) { SetStatus("Đã bỏ lọc."); return; }

            int hits = _file.Groups.Sum(g => g.Bindings.Count(Matches));
            SetStatus(hits == 0
                ? "Không có lệnh nào khớp."
                : $"Tìm thấy {hits} lệnh khớp.");
        }

        /// <summary>Rebuilds both panes. Call after the file behind them changes.</summary>
        private void RenderList()
        {
            RenderGroups();
            RenderDetail();
        }

        /// <summary>
        /// Fills the master pane with the groups that have something to show.
        ///
        /// Groups with no named commands are left out entirely - they are structural padding, and
        /// a row that opens an empty pane is worse than no row. The selection is carried across a
        /// reload where it can be, so switching profiles leaves the player looking at the same
        /// group rather than back at the top.
        /// </summary>
        private void RenderGroups()
        {
            int wanted = _selectedGroup;

            // Detached while rebuilding: setting Select on an item raises SelectChanged, and
            // re-entering the render from inside itself leaves the panes disagreeing.
            _groupList.SelectChanged -= OnGroupChanged;
            _groupList.PauseLayout = true;
            _groupList.Items.Clear();

            if (_file != null)
            {
                for (int g = 0; g < _file.Groups.Count; g++)
                {
                    int count = _file.Groups[g].Bindings.Count(Matches);
                    if (count == 0) continue;   // nothing here, under the current filter
                    _groupList.Items.Add(new AntdUI.MenuItem(GroupTitle(g))
                    {
                        // The count as an antd badge rather than "(13)" in the text: it is a
                        // number about the row, not part of the group name. Recoloured off
                        // antd's default red, which made every count read as an error.
                        Badge = count.ToString(),
                        BadgeBack = UiTheme.BadgeBack,
                        Tag = g,
                    });
                }
            }

            // Put the selection back on the same GROUP, which is not the same as the same row.
            int row = IndexOfGroup(wanted);
            if (row < 0 && _groupList.Items.Count > 0) row = 0;
            if (row >= 0)
            {
                _groupList.Items[row].Select = true;
                _selectedGroup = (int)_groupList.Items[row].Tag;
            }
            else _selectedGroup = -1;

            _groupList.PauseLayout = false;
            _groupList.SelectChanged += OnGroupChanged;
        }

        private int IndexOfGroup(int group)
        {
            for (int i = 0; i < _groupList.Items.Count; i++)
            {
                if (_groupList.Items[i].Tag is int g && g == group) return i;
            }
            return -1;
        }

        private string GroupTitle(int group) =>
            GroupTitles.TryGetValue(group, out string t) ? t : $"Nhóm {group + 1}";

        private void OnGroupChanged(object sender, AntdUI.MenuSelectEventArgs e)
        {
            if (e.Value?.Tag is not int group) return;
            _selectedGroup = group;
            RenderDetail();
        }

        /// <summary>
        /// Fills the detail pane with the selected group's bindings.
        ///
        /// Rows are anchored rather than laid out at a fixed width, so widening the window gives
        /// the command name the extra room and leaves the key button on the right edge.
        /// </summary>
        private void RenderDetail()
        {
            // Before the controls go: cancelling restores the text of the button being captured,
            // and that button is about to be disposed.
            CancelCapture();

            _listPanel.SuspendLayout();
            foreach (Control old in _listPanel.Controls.Cast<Control>().ToList()) old.Dispose();
            _listPanel.Controls.Clear();

            if (_file == null || _selectedGroup < 0 || _selectedGroup >= _file.Groups.Count)
            {
                _detailHeader.Text = Filtering ? "Không có lệnh nào khớp bộ lọc." : string.Empty;
                _listPanel.AutoScrollMinSize = Size.Empty;
                _listPanel.ResumeLayout();
                return;
            }

            var commands = _file.Groups[_selectedGroup].Bindings.Where(Matches).ToList();
            _detailHeader.Text = Filtering
                ? $"{GroupTitle(_selectedGroup)}  -  {commands.Count} lệnh khớp"
                : $"{GroupTitle(_selectedGroup)}  -  {commands.Count} lệnh";

            // Leave room for the scrollbar so the rows do not sit under it.
            int width = Math.Max(360, _listPanel.ClientSize.Width - 36);
            const int keyWidth = 190;
            const int rowH = 30;
            int y = 6;

            for (int i = 0; i < commands.Count; i++)
            {
                HotkeyBinding b = commands[i];

                // Each binding gets its own row panel, banded on alternate rows. The name and
                // its key sit at opposite ends of a wide window, and with nothing joining them
                // the eye has to work out which key belongs to which command; the band does
                // that work. It also makes the pair one control to place and one to dispose.
                var row = new Panel
                {
                    Location = new Point(8, y),
                    Size = new Size(width, rowH),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                    BackColor = i % 2 == 0 ? RowBandColor : Color.Transparent,
                };

                var name = new AntdUI.Label
                {
                    Text = ResolveName(b.StringId),
                    Location = new Point(10, 4),
                    Size = new Size(width - keyWidth - 30, rowH - 8),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                    AutoEllipsis = true,          // a long name shortens rather than overlapping
                    ForeColor = UiTheme.Ink,
                    BackColor = Color.Transparent,
                };
                // Round, like every other button in the app now, and centred - a keycap reads as
                // a keycap when the key is in the middle of it.
                var keyBtn = new AntdUI.Button
                {
                    Location = new Point(width - keyWidth - 8, 2),
                    Size = new Size(keyWidth, rowH - 6),
                    Type = AntdUI.TTypeMini.Default,
                    Shape = AntdUI.TShape.Round,
                    BorderWidth = 1,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Anchor = AnchorStyles.Top | AnchorStyles.Right,
                    Cursor = Cursors.Hand,
                    Tag = b,
                };
                SetKeyButtonText(keyBtn, b);
                keyBtn.Click += KeyButton_Click;

                row.Controls.Add(name);
                row.Controls.Add(keyBtn);
                _listPanel.Controls.Add(row);
                y += rowH;
            }

            _listPanel.AutoScrollMinSize = new Size(0, y + 10);
            _listPanel.ResumeLayout();
        }

        /// <summary>
        /// Whether this installation names the given command at all - the test for whether a
        /// nameless slot in the file is a real command here or just structural padding. See
        /// <see cref="HotkeyFile.AdoptModCommands"/>.
        /// </summary>
        private bool HasName(int stringId) => _strings != null && _strings.ContainsKey(stringId);

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

        private void SetKeyButtonText(AntdUI.Button btn, HotkeyBinding b)
        {
            bool unbound = b.KeyCode == HotkeyKeyNames.Unbound;
            btn.Text = HotkeyKeyNames.Describe(b.KeyCode, b.Ctrl, b.Alt, b.Shift);
            btn.ForeColor = unbound ? UiTheme.Muted : UiTheme.Ink;
            btn.DefaultBack = Color.White;
            btn.DefaultBorderColor = unbound ? UiTheme.CardBorder : UiTheme.KeyBorder;
            btn.Font = new Font("Segoe UI" + (unbound ? "" : " Semibold"), 10F,
                                unbound ? FontStyle.Regular : FontStyle.Bold);
        }

        private void KeyButton_Click(object sender, EventArgs e)
        {
            var btn = (AntdUI.Button)sender;
            if (_capturing == btn) { CancelCapture(); return; }
            CancelCapture();

            _capturing = btn;
            _capturingBinding = (HotkeyBinding)btn.Tag;
            _capturingRestoreText = btn.Text;
            btn.Text = "Nhấn phím...";
            btn.DefaultBack = UiTheme.CapturingBack;
            btn.DefaultBorderColor = UiTheme.CapturingBorder;
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

            // Taken from whoever had it, not merely reported. Two commands in one group
            // sharing a key is not a preference, it is a broken layout - in game only one of
            // them answers, and which one is not something the player chose. Warning and
            // leaving both bound meant the file could be saved in that state.
            HotkeyBinding displaced = TakeKeyFrom(keyCode, ctrl, alt, shift, _capturingBinding);
            string displacedName = displaced == null ? null : ResolveName(displaced.StringId);

            _capturingBinding.KeyCode = keyCode;
            _capturingBinding.Ctrl = ctrl;
            _capturingBinding.Alt = alt;
            _capturingBinding.Shift = shift;

            string assignedTo = ResolveName(_capturingBinding.StringId);
            string key = HotkeyKeyNames.Describe(keyCode, ctrl, alt, shift);

            SetKeyButtonText(_capturing, _capturingBinding);
            _capturing = null;
            _capturingBinding = null;
            _dirty = true;

            if (keyCode == HotkeyKeyNames.Unbound)
            {
                SetStatus("Đã bỏ gán.");
            }
            else if (displacedName != null)
            {
                // Redrawn because a second row changed - the displaced command is now blank,
                // and it is very often not the row the player was looking at.
                RenderDetail();
                string message = $"Phím {key} đã chuyển sang \"{assignedTo}\". " +
                                 $"\"{displacedName}\" trong cùng nhóm đã bị bỏ gán.";
                SetStatus(message);
                AntdUI.Message.warn(this, message);
            }
            else
            {
                SetStatus("Đã gán phím. Nhớ bấm Lưu.");
            }
        }

        /// <summary>
        /// Unbinds any other command in the same group that already held this key, and returns
        /// it so the caller can say whose key was taken.
        ///
        /// Same group only. Different groups are different in-game contexts and reuse keys
        /// perfectly legitimately - clearing across groups would silently wreck a layout.
        ///
        /// A modifier is part of the key: Ctrl+B does not collide with B.
        /// </summary>
        private HotkeyBinding TakeKeyFrom(int keyCode, bool ctrl, bool alt, bool shift, HotkeyBinding self)
        {
            if (keyCode == HotkeyKeyNames.Unbound || _file == null) return null;
            var group = _file.Groups.FirstOrDefault(gr => gr.Bindings.Contains(self));
            if (group == null) return null;

            HotkeyBinding displaced = null;
            foreach (var b in group.Bindings)
            {
                if (ReferenceEquals(b, self) || !b.IsCommand) continue;
                if (b.KeyCode != keyCode || b.Ctrl != ctrl || b.Alt != alt || b.Shift != shift) continue;

                displaced ??= b;   // report the first; clear every one, in case of an existing clash
                b.KeyCode = HotkeyKeyNames.Unbound;
                b.Ctrl = b.Alt = b.Shift = false;
            }
            return displaced;
        }

        private void CancelCapture()
        {
            if (_capturing == null) return;
            _capturing.Text = _capturingRestoreText;
            if (_capturingBinding != null) SetKeyButtonText(_capturing, _capturingBinding);
            _capturing = null;
            _capturingBinding = null;
        }

        private void ImportFromFile(object sender, EventArgs e)
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Chọn tệp phím tắt (.hki) để nhập",
                Filter = "Tệp phím tắt AoE2 (*.hki)|*.hki|Tất cả tệp (*.*)|*.*",
                InitialDirectory = Directory.Exists(_hotkeyDir) ? _hotkeyDir : _gameFolder,
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                var imported = HotkeyFile.Load(dlg.FileName);
                imported.AdoptModCommands(HasName);
                _file = imported;
                _dirty = true;
                RenderList();
                string message = $"Đã nhập từ {Path.GetFileName(dlg.FileName)}. Bấm Lưu để áp dụng cho hồ sơ đã chọn.";
                SetStatus(message);
                AntdUI.Message.info(this, message);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"HotkeyEditor: import '{dlg.FileName}' failed: {ex.Message}");
                AntdUI.Message.error(this, $"Không đọc được tệp phím tắt đã chọn: {ex.Message}");
            }
        }

        /// <summary>
        /// Replaces everything on screen with the layout TadaPlay ships.
        ///
        /// Deliberately the same shape as importing a file, because that is what it is - the
        /// file just comes from inside the app. Nothing is written until the player saves, so a
        /// reset they did not mean costs them a Close rather than their whole layout.
        /// </summary>
        private void ResetToDefault(object sender, EventArgs e)
        {
            CancelCapture();

            // Worth a prompt: it discards every binding in the list at once, which is not
            // something to do on a mis-click next to "Xóa hồ sơ".
            var confirm = MessageBox.Show(this,
                "Đặt lại toàn bộ phím tắt về mặc định của TadaPlay?\n" +
                "Các thay đổi đang hiển thị sẽ bị bỏ. Chưa ghi vào tệp cho tới khi bạn bấm Lưu.",
                "Đặt lại phím tắt", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            try
            {
                _file = HotkeyFile.LoadDefault();
                _file.AdoptModCommands(HasName);
                _dirty = true;
                RenderList();
                const string message = "Đã nạp phím tắt mặc định. Bấm Lưu (hoặc Lưu cho mọi hồ sơ) để áp dụng.";
                SetStatus(message);
                AntdUI.Message.info(this, message);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"HotkeyEditor: loading the default layout failed: {ex.Message}");
                AntdUI.Message.error(this, $"Không nạp được phím tắt mặc định: {ex.Message}");
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
                    // Write the layout to player1..player5 in the game's hotkey folder so it applies
                    // whichever profile the game has selected.
                    targets = Enumerable.Range(1, 5).Select(i => Path.Combine(_hotkeyDir, $"player{i}.hki")).ToList();
                }
                else if (_slotCombo.SelectedIndex >= 0 && _slotCombo.SelectedIndex < _slotFiles.Count)
                {
                    targets = new List<string> { _slotFiles[_slotCombo.SelectedIndex] };
                }
                else
                {
                    // No existing slot selected (e.g. after importing into an empty folder) - create
                    // player1 in the game's hotkey folder.
                    targets = new List<string> { Path.Combine(_hotkeyDir, "player1.hki") };
                }

                foreach (string target in targets) _file.Save(target);
                _dirty = false;

                RefreshSlotList(targets);
                string saved = allSlots
                    ? $"Đã lưu phím tắt cho {targets.Count} hồ sơ (player1–5)."
                    : $"Đã lưu phím tắt vào {Path.GetFileName(targets[0])}.";
                SetStatus(saved);
                // The status line alone was not enough: it is small, grey, at the bottom
                // corner, and it often says roughly what it said before - so a save looked
                // exactly like a click that did nothing. A toast is the confirmation.
                AntdUI.Message.success(this, saved);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"HotkeyEditor: save failed: {ex.Message}");
                SetStatus("Lưu thất bại.");
                AntdUI.Message.error(this, $"Lưu phím tắt thất bại: {ex.Message}");
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

            var refreshed = FindSlotFiles(_hotkeyDir);
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
