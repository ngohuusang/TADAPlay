using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Newtonsoft.Json;
using TadaPlay.Logger;
using TadaPlay.Utils;

namespace TadaPlay.Controls
{
    /// <summary>
    /// "Xếp hạng phe": how strong each of the 31 WololoKingdoms (UserPatch 1.5) civilizations is,
    /// building by building and unit by unit.
    ///
    /// The grades come from Survivalist's "Simplified Tech Tree for 1v1 Arabia"
    /// (aoe2-de-tools.herokuapp.com/civ-ranking), cut down to the civs this game actually has and
    /// with Definitive-Edition-only units removed (see Resources/civ-ranking-wk.json). They are
    /// shipped inside the app rather than fetched: the page is a hobby site that may move, and a
    /// grade table does not change between one game and the next.
    ///
    /// The source grades the DE balance, not 1.5's, so the screen says so - it is a guide to the
    /// tech trees, not a statement about this exact patch.
    /// </summary>
    public class CivRanking : UserControl
    {
        public event EventHandler BackRequested;

        private const string ResourceName = "TadaPlay.Resources.civ-ranking-wk.json";

        // Columns of the ranking table, in display order: data.json building key -> Vietnamese.
        private static readonly (string Key, string Vi)[] Buildings =
        {
            ("town center", "Kinh tế"),
            ("barracks", "Nhà lính"),
            ("archery range", "Trường bắn"),
            ("stable", "Kỵ binh"),
            ("siege workshop", "Công thành"),
            ("monastery", "Tu viện"),
            ("castle", "Lâu đài"),
            ("university", "Phòng thủ"),
        };

        private static readonly Dictionary<string, string> BuildingVi = new()
        {
            ["town center"] = "Nhà chính",
            ["barracks"] = "Nhà lính",
            ["archery range"] = "Trường bắn",
            ["stable"] = "Chuồng ngựa",
            ["siege workshop"] = "Xưởng công thành",
            ["monastery"] = "Tu viện",
            ["castle"] = "Lâu đài",
            ["university"] = "Đại học (phòng thủ)",
        };

        // Each graded entry stands for a whole unit LINE (e.g. "champion" is militia through
        // champion), named after its last upgrade. Unique units keep their in-game English name.
        private static readonly Dictionary<string, string> UnitVi = new()
        {
            ["villager"] = "Dân làng",
            ["long swordsman"] = "Lính kiếm",
            ["two-handed swordsman"] = "Lính kiếm",
            ["champion"] = "Lính kiếm",
            ["spearman"] = "Lính giáo",
            ["pikeman"] = "Lính giáo",
            ["halberdier"] = "Lính giáo",
            ["elite eagle warrior"] = "Chiến binh đại bàng",
            ["condottiero"] = "Condottiero",
            ["archer"] = "Cung thủ",
            ["crossbowman"] = "Cung thủ",
            ["arbalester"] = "Cung thủ",
            ["skirmisher"] = "Lính phóng lao",
            ["elite skirmisher"] = "Lính phóng lao",
            ["imperial skirmisher"] = "Lính phóng lao",
            ["cavalry archer"] = "Cung kỵ",
            ["heavy cavalry archer"] = "Cung kỵ",
            ["hand cannoneer"] = "Lính súng tay",
            ["slinger"] = "Lính ném đá",
            ["scout cavalry"] = "Kỵ binh nhẹ",
            ["light cavalry"] = "Kỵ binh nhẹ",
            ["hussar"] = "Kỵ binh nhẹ",
            ["knight"] = "Hiệp sĩ",
            ["cavalier"] = "Hiệp sĩ",
            ["paladin"] = "Hiệp sĩ",
            ["heavy camel rider"] = "Kỵ lạc đà",
            ["imperial camel rider"] = "Kỵ lạc đà",
            ["magyar huszar"] = "Magyar Huszar",
            ["capped ram"] = "Xe húc",
            ["siege ram"] = "Xe húc",
            ["mangonel"] = "Máy bắn đá",
            ["onager"] = "Máy bắn đá",
            ["siege onager"] = "Máy bắn đá",
            ["scorpion"] = "Nỏ lớn",
            ["heavy scorpion"] = "Nỏ lớn",
            ["bombard cannon"] = "Đại bác",
            ["trebuchet"] = "Máy bắn đá lớn",
            ["monk"] = "Tu sĩ",
            ["missionary"] = "Nhà truyền giáo",
            ["watch tower"] = "Tháp canh",
            ["guard tower"] = "Tháp canh",
            ["keep"] = "Tháp canh",
            ["defenses"] = "Tường, tháp và lâu đài",
        };

        private static readonly string[] AgesVi = { "Bóng tối", "Phong kiến", "Lâu đài", "Đế quốc" };

        private readonly ListView _civList;
        private readonly ListView _unitList;
        private readonly RichTextBox _descBox;
        private readonly TabControl _tabs;
        private readonly Label _civTitle;
        private readonly Label _civNote;
        private readonly Label _statusLabel;
        private readonly ToolTip _tip = new();

        private CivData _data;
        private List<CivRow> _rows = new();
        private int _sortColumn = 2; // Tổng
        private bool _sortAscending;

        public CivRanking()
        {
            Dock = DockStyle.Fill;
            Font = new Font("Segoe UI", 9.75F);

            var header = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(12, 8, 12, 8) };
            var backButton = new Button { Text = "← Trang chủ", Dock = DockStyle.Left, Width = 110 };
            backButton.Click += (s, e) => BackRequested?.Invoke(this, EventArgs.Empty);
            var title = new Label
            {
                Text = "   Xếp hạng phe (WololoKingdoms 1.5)",
                Dock = DockStyle.Left,
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            header.Controls.Add(title);
            header.Controls.Add(backButton);

            var intro = new Label
            {
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(14, 2, 14, 2),
                ForeColor = Color.DimGray,
                Text = "Đánh giá sức mạnh từng phe theo từng công trình cho trận 1v1 Arabia. A là mức chuẩn khi nâng cấp đầy đủ; "
                     + "S, A+ là mạnh hơn chuẩn, B và C là thiếu nâng cấp quan trọng. Bấm vào tên cột để sắp xếp, bấm vào một phe để xem chi tiết.",
            };

            _civList = NewList();
            _civList.Columns.Add("#", 40, HorizontalAlignment.Right);
            _civList.Columns.Add("Phe", 170);
            _civList.Columns.Add("Tổng", 80, HorizontalAlignment.Center);
            foreach (var (_, vi) in Buildings)
            {
                _civList.Columns.Add(vi, 82, HorizontalAlignment.Center);
            }
            _civList.HeaderStyle = ColumnHeaderStyle.Clickable;
            _civList.ColumnClick += (s, e) => SortBy(e.Column);
            _civList.SelectedIndexChanged += (s, e) => ShowSelectedCiv();

            var detail = new Panel { Dock = DockStyle.Fill };
            _civTitle = new Label
            {
                Dock = DockStyle.Top,
                Height = 30,
                Padding = new Padding(12, 6, 12, 0),
                Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold),
                Text = "Chọn một phe ở bảng trên để xem chi tiết.",
            };
            _civNote = new Label
            {
                Dock = DockStyle.Top,
                Height = 0,
                Padding = new Padding(12, 0, 12, 4),
                ForeColor = Color.DarkOrange,
            };
            _unitList = NewList();
            _unitList.Columns.Add("Công trình", 150);
            _unitList.Columns.Add("Dòng lính", 170);
            _unitList.Columns.Add("Tên trong game", 190);
            foreach (string age in AgesVi)
            {
                _unitList.Columns.Add(age, 90, HorizontalAlignment.Center);
            }

            // The civ's own description, from the game's text (language.ini) rather than the
            // grading site, so it describes 1.5 exactly - bonuses, unique units and techs, and
            // the team bonus.
            _descBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                Font = new Font("Segoe UI", 10.5F),
                DetectUrls = false,
                ScrollBars = RichTextBoxScrollBars.Vertical,
            };
            var descHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 6), BackColor = Color.White };
            descHost.Controls.Add(_descBox);

            _tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
            var descTab = new TabPage("Đặc điểm phe") { BackColor = Color.White };
            descTab.Controls.Add(descHost);
            var unitsTab = new TabPage("Đánh giá lính");
            unitsTab.Controls.Add(_unitList);
            _tabs.TabPages.Add(descTab);
            _tabs.TabPages.Add(unitsTab);

            detail.Controls.Add(_tabs);
            detail.Controls.Add(_civNote);
            detail.Controls.Add(_civTitle);

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6,
            };
            split.Panel1.Controls.Add(_civList);
            split.Panel2.Controls.Add(detail);
            // Set on the first real layout, not at Load: at Load the control has not been given
            // its size yet, and a distance taken from that left the ranking table one row tall.
            bool splitPlaced = false;
            split.SizeChanged += (s, e) =>
            {
                if (splitPlaced || split.Height < 300) return;
                splitPlaced = true;
                split.SplitterDistance = (int)(split.Height * 0.42);
            };
            _civList.Resize += (s, e) => FitCivColumns();

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(12, 4, 12, 4) };
            _statusLabel = new Label { Dock = DockStyle.Fill, ForeColor = Color.Gray };
            var sourceLink = new LinkLabel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                Text = "Nguồn: Simplified Tech Tree (Survivalist)",
            };
            sourceLink.LinkClicked += (s, e) => OpenSource();
            footer.Controls.Add(_statusLabel);
            footer.Controls.Add(sourceLink);

            Controls.Add(split);
            Controls.Add(footer);
            Controls.Add(intro);
            Controls.Add(header);

            Load += (s, e) => LoadData();
            Disposed += (s, e) => _tip.Dispose();
        }

        /// <summary>Shares the width left after #, Phe and Tổng evenly between the eight
        /// building columns, so none of their headers is cut off and nothing scrolls sideways.</summary>
        private void FitCivColumns()
        {
            if (_civList.Columns.Count < 3 + Buildings.Length) return;
            int fixedWidth = 36 + 175 + 95;
            int free = _civList.ClientSize.Width - fixedWidth - 4;
            int each = Math.Max(80, free / Buildings.Length);
            _civList.Columns[0].Width = 36;
            _civList.Columns[1].Width = 175;
            _civList.Columns[2].Width = 95;
            for (int i = 0; i < Buildings.Length; i++) _civList.Columns[3 + i].Width = each;
        }

        private static ListView NewList()
        {
            var list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Font = new Font("Segoe UI", 10F),
                BorderStyle = BorderStyle.FixedSingle,
            };
            // Taller rows, the same way Matches does it: row height follows the image list.
            var rowSizer = new ImageList { ImageSize = new Size(1, 28) };
            rowSizer.Images.Add(new Bitmap(1, 28));
            list.SmallImageList = rowSizer;
            return list;
        }

        // ------------------------------------------------------------------ data

        private sealed class CivData
        {
            [JsonProperty("source")] public string Source { get; set; }
            [JsonProperty("sourceUrl")] public string SourceUrl { get; set; }
            [JsonProperty("sourceVersion")] public string SourceVersion { get; set; }
            [JsonProperty("rankConversion")] public Dictionary<string, string> RankConversion { get; set; }
            [JsonProperty("civilizations")] public List<Civ> Civilizations { get; set; }
        }

        private sealed class Civ
        {
            [JsonProperty("key")] public string Key { get; set; }
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("vi")] public string Vi { get; set; }
            [JsonProperty("note")] public string Note { get; set; }

            /// <summary>building -> unit line -> grade per age, ending at Imperial.</summary>
            [JsonProperty("ranks")] public Dictionary<string, Dictionary<string, double[]>> Ranks { get; set; }

            [JsonProperty("description")] public CivDescription Description { get; set; }

            /// <summary>The game's original English text, shown as the description's tooltip.</summary>
            [JsonProperty("descriptionEn")] public string DescriptionEn { get; set; }
        }

        private sealed class CivDescription
        {
            [JsonProperty("bonuses")] public List<string> Bonuses { get; set; }
            [JsonProperty("unique")] public string Unique { get; set; }
            [JsonProperty("castleTech")] public string CastleTech { get; set; }
            [JsonProperty("imperialTech")] public string ImperialTech { get; set; }
            [JsonProperty("team")] public string Team { get; set; }
        }

        /// <summary>One civ's computed scores. Null where the civ has nothing graded there.</summary>
        private sealed class CivRow
        {
            public Civ Civ;
            public Dictionary<string, double?> Building = new();
            public double Overall;
        }

        private void LoadData()
        {
            try
            {
                using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                    ?? throw new InvalidOperationException($"Thiếu dữ liệu '{ResourceName}'.");
                using var reader = new StreamReader(stream);
                _data = JsonConvert.DeserializeObject<CivData>(reader.ReadToEnd());

                _rows = _data.Civilizations.Select(Score).ToList();
                SortBy(_sortColumn, keepDirection: true);
                _statusLabel.Text = $"{_rows.Count} phe · điểm theo bản DE, với 1.5 chỉ để tham khảo";
                _tip.SetToolTip(_statusLabel, $"{_data.Source} - {_data.SourceVersion}. Điểm chấm theo cân bằng của "
                                            + "bản Definitive Edition, nên với WololoKingdoms 1.5 chỉ mang tính tham khảo.");
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"CivRanking: could not load data: {ex}");
                _statusLabel.ForeColor = Color.Firebrick;
                _statusLabel.Text = "Không đọc được dữ liệu xếp hạng phe: " + ex.Message;
            }
        }

        /// <summary>
        /// The source's own rule for a building: the average of every grade of every unit in it,
        /// across all ages. The overall score is the average of the buildings, each counted once,
        /// so a civ is not ranked higher just for having more units in one building.
        /// </summary>
        private static CivRow Score(Civ civ)
        {
            var row = new CivRow { Civ = civ };
            foreach (var (key, _) in Buildings)
            {
                if (civ.Ranks.TryGetValue(key, out var units) && units.Count > 0)
                {
                    var all = units.Values.SelectMany(v => v).ToList();
                    row.Building[key] = all.Count > 0 ? all.Average() : (double?)null;
                }
                else
                {
                    row.Building[key] = null;
                }
            }
            var present = row.Building.Values.Where(v => v.HasValue).Select(v => v.Value).ToList();
            row.Overall = present.Count > 0 ? present.Average() : 0;
            return row;
        }

        // ------------------------------------------------------------------ ranking table

        private void SortBy(int column, bool keepDirection = false)
        {
            if (!keepDirection)
            {
                // A new column starts best-first; clicking the same one again flips it.
                _sortAscending = column == _sortColumn ? !_sortAscending : column == 1;
                _sortColumn = column;
            }

            Func<CivRow, IComparable> key = column switch
            {
                1 => r => r.Civ.Vi,
                0 or 2 => r => r.Overall,
                _ => r => r.Building[Buildings[column - 3].Key] ?? -1,
            };
            // The rank number always follows the overall score, whatever the table is sorted by.
            var byOverall = _rows.OrderByDescending(r => r.Overall).ThenBy(r => r.Civ.Vi).ToList();
            var ordered = (_sortAscending ? _rows.OrderBy(key) : _rows.OrderByDescending(key))
                          .ThenByDescending(r => r.Overall).ThenBy(r => r.Civ.Vi).ToList();

            string selectedKey = (_civList.SelectedItems.Count > 0 ? _civList.SelectedItems[0].Tag as CivRow : null)?.Civ.Key;

            _civList.BeginUpdate();
            _civList.Items.Clear();
            for (int i = 0; i < ordered.Count; i++)
            {
                CivRow r = ordered[i];
                var item = new ListViewItem((byOverall.IndexOf(r) + 1).ToString())
                {
                    UseItemStyleForSubItems = false,
                    BackColor = i % 2 == 0 ? Color.White : UiColors.ZebraStripe,
                    Tag = r,
                };
                item.SubItems.Add(r.Civ.Vi == r.Civ.Name ? r.Civ.Name : $"{r.Civ.Vi} ({r.Civ.Name})");
                AddGrade(item, r.Overall, showNumber: true, bold: true);
                foreach (var (b, _) in Buildings)
                {
                    AddGrade(item, r.Building[b], showNumber: false, bold: false);
                }
                _civList.Items.Add(item);
                if (r.Civ.Key == selectedKey) item.Selected = true;
            }
            _civList.EndUpdate();

            for (int c = 0; c < _civList.Columns.Count; c++)
            {
                string text = _civList.Columns[c].Text.TrimEnd(' ', '▲', '▼');
                _civList.Columns[c].Text = c == _sortColumn
                    ? text + (_sortAscending ? " ▲" : " ▼")
                    : text;
            }

            if (_civList.SelectedItems.Count == 0 && _civList.Items.Count > 0)
            {
                _civList.Items[0].Selected = true;
            }
        }

        private void AddGrade(ListViewItem item, double? value, bool showNumber, bool bold)
        {
            if (!value.HasValue)
            {
                var none = item.SubItems.Add("—");
                none.ForeColor = Color.Silver;
                return;
            }
            string letter = Letter(value.Value);
            var sub = item.SubItems.Add(showNumber
                ? $"{letter}  ({value.Value.ToString("0.00", CultureInfo.InvariantCulture)})"
                : letter);
            sub.ForeColor = GradeColor(value.Value);
            sub.Font = new Font(_civList.Font, bold ? FontStyle.Bold : FontStyle.Regular);
        }

        // ------------------------------------------------------------------ civ detail

        private void ShowSelectedCiv()
        {
            if (_civList.SelectedItems.Count == 0 || _civList.SelectedItems[0].Tag is not CivRow row) return;
            Civ civ = row.Civ;

            _civTitle.Text = $"{civ.Vi} ({civ.Name}) - tổng {Letter(row.Overall)}, "
                           + $"hạng {_rows.Count(r => r.Overall > row.Overall) + 1}/{_rows.Count}";
            _civNote.Text = civ.Note ?? "";
            _civNote.Height = string.IsNullOrEmpty(civ.Note) ? 0 : 24;

            ShowDescription(civ);

            _unitList.BeginUpdate();
            _unitList.Items.Clear();
            int n = 0;
            foreach (var (b, _) in Buildings)
            {
                if (!civ.Ranks.TryGetValue(b, out var units)) continue;
                bool firstInBuilding = true;
                foreach (var (unit, grades) in units)
                {
                    var item = new ListViewItem(firstInBuilding ? BuildingVi[b] : "")
                    {
                        UseItemStyleForSubItems = false,
                        BackColor = n++ % 2 == 0 ? Color.White : UiColors.ZebraStripe,
                    };
                    item.SubItems.Add(UnitVi.TryGetValue(unit, out string vi) ? vi : "Lính đặc biệt");
                    item.SubItems.Add(TitleCase(unit));

                    // Grades run up to Imperial, so a 3-long list starts in Feudal: right-align it.
                    int offset = AgesVi.Length - grades.Length;
                    for (int age = 0; age < AgesVi.Length; age++)
                    {
                        if (age < offset)
                        {
                            item.SubItems.Add("");
                            continue;
                        }
                        double g = grades[age - offset];
                        var sub = item.SubItems.Add(Letter(g));
                        sub.ForeColor = GradeColor(g);
                        sub.Font = new Font(_unitList.Font, FontStyle.Bold);
                        // The source's highlight: gold for better than a generic fully upgraded
                        // unit, red for one that is particularly bad.
                        if (g > 3) sub.BackColor = Color.FromArgb(0xFF, 0xF4, 0xD6);
                        else if (g <= 1) sub.BackColor = Color.FromArgb(0xFD, 0xE2, 0xE2);
                    }
                    _unitList.Items.Add(item);
                    firstInBuilding = false;
                }
            }
            _unitList.EndUpdate();
        }

        /// <summary>Writes the civ's description as headed sections: bonuses as a bullet list,
        /// then unique units, the two unique techs and the team bonus.</summary>
        private void ShowDescription(Civ civ)
        {
            CivDescription d = civ.Description;
            _descBox.Clear();
            if (d == null)
            {
                _descBox.Text = "Chưa có mô tả cho phe này.";
                return;
            }

            var heading = new Font(_descBox.Font, FontStyle.Bold);
            Color headingColor = Color.FromArgb(0x1F, 0x2A, 0x37);
            Color bodyColor = Color.FromArgb(0x37, 0x41, 0x51);

            void Heading(string text)
            {
                if (_descBox.TextLength > 0) Append("\n", _descBox.Font, bodyColor);
                Append(text + "\n", heading, headingColor);
            }

            void Append(string text, Font font, Color color)
            {
                _descBox.SelectionStart = _descBox.TextLength;
                _descBox.SelectionLength = 0;
                _descBox.SelectionFont = font;
                _descBox.SelectionColor = color;
                _descBox.AppendText(text);
            }

            Heading("Điểm mạnh của phe");
            foreach (string bonus in d.Bonuses ?? new List<string>())
            {
                Append("  •  " + bonus + "\n", _descBox.Font, bodyColor);
            }

            Heading("Lính đặc biệt");
            Append("  " + d.Unique + "\n", _descBox.Font, bodyColor);

            Heading("Công nghệ đặc biệt");
            Append("  Thời Lâu đài - " + d.CastleTech + "\n", _descBox.Font, bodyColor);
            Append("  Thời Đế quốc - " + d.ImperialTech + "\n", _descBox.Font, bodyColor);

            Heading("Thưởng đồng đội");
            Append("  " + d.Team + "\n", _descBox.Font, bodyColor);

            // AppendText leaves the view at the end and ScrollToCaret does not reliably undo
            // that, so scroll to the top directly - the bonuses are what a reader wants first.
            _descBox.SelectionStart = 0;
            _descBox.SelectionLength = 0;
            SendMessage(_descBox.Handle, WM_VSCROLL, (IntPtr)SB_TOP, IntPtr.Zero);
            _tip.SetToolTip(_descBox, string.IsNullOrEmpty(civ.DescriptionEn)
                ? null
                : "Bản gốc trong game:\n\n" + civ.DescriptionEn);
        }

        private const int WM_VSCROLL = 0x0115;
        private const int SB_TOP = 6;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // ------------------------------------------------------------------ helpers

        /// <summary>Letter for a grade, using the source's scale, rounded to its half steps.</summary>
        private string Letter(double value)
        {
            double step = Math.Round(Math.Clamp(value, 0, 4) * 2, MidpointRounding.AwayFromZero) / 2;
            string key = step.ToString("0.#", CultureInfo.InvariantCulture);
            return _data?.RankConversion != null && _data.RankConversion.TryGetValue(key, out string letter)
                ? letter
                : key;
        }

        private static Color GradeColor(double value) => value switch
        {
            >= 3.75 => Color.FromArgb(0xB8, 0x86, 0x0B), // S - dark gold
            >= 3.25 => Color.FromArgb(0x23, 0x8B, 0x23), // A+
            >= 2.75 => Color.FromArgb(0x38, 0x9E, 0x0D), // A
            >= 1.75 => Color.FromArgb(0x15, 0x77, 0xD4), // B, B+
            >= 0.75 => Color.FromArgb(0xD4, 0x88, 0x06), // C range
            _ => Color.FromArgb(0xCF, 0x13, 0x22),       // F
        };

        private static string TitleCase(string s) =>
            CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s);

        private void OpenSource()
        {
            try
            {
                Process.Start(new ProcessStartInfo(_data?.SourceUrl ?? "https://aoe2-de-tools.herokuapp.com/civ-ranking/")
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"CivRanking: could not open source page: {ex.Message}");
            }
        }
    }
}
