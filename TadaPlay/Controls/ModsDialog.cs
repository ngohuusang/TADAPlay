using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using TadaPlay.Contexts.Interfaces;
using TadaPlay.Logger;
using TadaPlay.Utils;
using TadaPlay.Utils.Mods;

namespace TadaPlay.Controls
{
    /// <summary>
    /// "Cài mod" - turns a Voobly mod into one the local game can actually use.
    ///
    /// The player sees the mods already in their Voobly folder, installs one with a button, and is
    /// told which of the game's files were changed and how many graphics went into each. That last
    /// part is the point: a mod that silently did nothing is the failure this whole feature exists
    /// to end, so "installed" has to be something they can see rather than assume.
    /// </summary>
    public partial class ModsDialog : UserControl
    {
        private const int DialogWidth = 660;
        private const int RowHeight = 66;
        private const int ListHeight = 300;
        private const int HintHeight = 26;
        private const int ToolbarHeight = 52;
        private const int StatusHeight = 34;

        /// <summary>Roughly what the modal puts around the content: its title bar, buttons and margins.</summary>
        private const int ModalChrome = 170;
        private const int MinimumDialogHeight = 360;

        /// <summary>Width of the column the per-mod button sits in, docked to the right of its row.</summary>
        private const int ButtonColumnWidth = 130;

        private static readonly Color InstalledGreen = Color.FromArgb(0x38, 0x9E, 0x0D);

        private readonly Form _owner;
        private readonly IAppContext _appContext;
        private readonly System.Windows.Forms.Panel _list;
        private readonly AntdUI.Label _status;

        public ModsDialog(Form owner, IAppContext appContext)
        {
            _owner = owner;
            _appContext = appContext;

            Font = new Font("Segoe UI", 11F);
            Width = DialogWidth;
            BackColor = UiTheme.PageBg;
            Padding = new Padding(14, 14, 14, 16);
            AutoSize = false;

            _status = new AntdUI.Label
            {
                Dock = DockStyle.Bottom,
                Height = 34,
                ForeColor = UiTheme.Muted,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                TextMultiLine = true,
            };

            _list = new System.Windows.Forms.Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.Transparent,
            };

            Controls.Add(_list);
            Controls.Add(_status);
            Controls.Add(BuildToolbar());
            Controls.Add(BuildHint());

            // Sized to fit the window rather than to the list: the modal takes this height as
            // given, so anything taller is cut off at the bottom - and what sits at the bottom
            // here is the row of buttons the dialog exists for. The list keeps the remainder and
            // scrolls, which it does anyway once there are more than a few mods.
            int wanted = Padding.Vertical + HintHeight + ListHeight + ToolbarHeight + StatusHeight;
            int visible = (owner?.ClientSize.Height ?? 720) - ModalChrome;
            Height = Math.Max(MinimumDialogHeight, Math.Min(wanted, visible));

            Reload();
        }

        /// <summary>
        /// One line of explanation, where a titled card used to be. The card repeated the name the
        /// dialog already carries in its own title bar and cost 118 pixels of the list to do it.
        /// </summary>
        private Control BuildHint() => new AntdUI.Label
        {
            Dock = DockStyle.Top,
            Height = HintHeight,
            Text = "Mod tải từ Voobly chỉ chạy khi vào Voobly. Cài ở đây để game đọc được trực tiếp.",
            ForeColor = UiTheme.Muted,
            Font = new Font("Segoe UI", 9F),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        private Control BuildToolbar()
        {
            var bar = new System.Windows.Forms.Panel
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                BackColor = Color.Transparent,
            };

            var pickFolder = UiTheme.Quiet("Chọn thư mục mod...", UiTheme.IconFolder);
            pickFolder.Click += (s, e) => PickFolder();

            var pickZip = UiTheme.Quiet("Chọn tệp .zip...", UiTheme.IconFolder);
            pickZip.Click += (s, e) => PickZip();

            var removeAll = UiTheme.Quiet("Gỡ tất cả mod");
            removeAll.Click += (s, e) => Run("Đang gỡ tất cả mod...",
                                            folder => { ModInstaller.UninstallAll(folder, Say); return null; });

            // Each button in its own docked column, filling it. UiTheme's buttons come docked to
            // the top of whatever holds them, which quietly overrides any Location given to them -
            // side by side in one panel they stacked instead, and this bar is only tall enough to
            // show the first, so "Chọn thư mục mod..." was invisible below the edge.
            //
            // Added right-to-left: the layout docks the most recently added control first, so the
            // last one added is the one that ends up leftmost.
            bar.Controls.Add(UiTheme.ButtonColumn(removeAll, DockStyle.Right, 166));
            bar.Controls.Add(UiTheme.ButtonColumn(pickZip, DockStyle.Left, 186));
            bar.Controls.Add(UiTheme.ButtonColumn(pickFolder, DockStyle.Left, 206));
            return bar;
        }

        private void PickFolder()
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Chọn thư mục mod (thư mục chứa 'drs' hoặc các tệp .slp)",
                UseDescriptionForTitle = true,
            };
            if (dialog.ShowDialog(_owner) == DialogResult.OK) InstallFrom(dialog.SelectedPath);
        }

        private void PickZip()
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Chọn tệp mod (.zip)",
                Filter = "Tệp mod (*.zip)|*.zip|Tất cả tệp (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dialog.ShowDialog(_owner) == DialogResult.OK) InstallFrom(dialog.FileName);
        }

        /// <summary>Installs from a folder or a .zip the player picked.</summary>
        private void InstallFrom(string path)
        {
            string gameFolder = _appContext.GetGameFolder();
            if (!HasGameFolder(gameFolder)) return;

            ModInstaller.Mod mod;
            try
            {
                Say("Đang mở mod...");
                mod = ModInstaller.Open(path, gameFolder);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"ModsDialog: cannot open '{path}': {ex}");
                Problem("Không mở được mod", ex.Message);
                return;
            }

            if (!mod.HasAnything)
            {
                Problem("Không phải mod dùng được",
                        "Không tìm thấy tệp .slp nào (dạng '2223.slp') và cũng không có language.ini.");
                return;
            }

            Run($"Đang cài '{mod.Name}'...", folder => ModInstaller.Install(folder, mod, Say));
        }

        /// <summary>Redraws the list from the Voobly folder and what is currently installed.</summary>
        private void Reload()
        {
            _list.Controls.Clear();

            string gameFolder = _appContext.GetGameFolder();
            if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
            {
                _list.Controls.Add(Message("Chưa chọn thư mục game. Vào Cài đặt để chọn trước."));
                return;
            }

            List<ModInstaller.Mod> mods = ModInstaller.Discover(gameFolder);
            List<ModInstaller.Mod> installed = ModInstaller.Installed(gameFolder);

            // A mod installed from somewhere else still belongs in the list - otherwise it could
            // never be removed from here.
            foreach (ModInstaller.Mod extra in installed)
            {
                if (!mods.Any(m => Same(m.Name, extra.Name)) && Directory.Exists(extra.Folder))
                {
                    mods.Add(ModInstaller.Inspect(extra.Folder));
                }
            }

            if (mods.Count == 0)
            {
                _list.Controls.Add(Message(
                    "Chưa có mod nào trong thư mục Voobly. Dùng \"Chọn thư mục mod...\" hoặc \"Chọn tệp .zip...\"."));
            }
            else
            {
                // Added in reverse: Dock=Top stacks the last one added at the top.
                for (int i = mods.Count - 1; i >= 0; i--)
                {
                    _list.Controls.Add(BuildRow(mods[i], installed.Any(m => Same(m.Name, mods[i].Name)), i % 2 == 1));
                }
            }

            _status.Text = installed.Count == 0
                ? "Chưa cài mod nào."
                : $"Đang cài: {string.Join(", ", installed.Select(m => m.Name))}.";
        }

        private Control BuildRow(ModInstaller.Mod mod, bool installed, bool banded)
        {
            var row = new System.Windows.Forms.Panel
            {
                Dock = DockStyle.Top,
                Height = RowHeight,
                BackColor = banded ? UiTheme.RowBand : Color.White,
            };

            row.Controls.Add(new AntdUI.Label
            {
                Text = mod.Name,
                Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold),
                ForeColor = UiTheme.Ink,
                Location = new Point(12, 9),
                Size = new Size(380, 22),
                BackColor = Color.Transparent,
            });

            var parts = new List<string>();
            if (installed) parts.Add("✓ Đã cài");
            if (mod.GraphicsCount > 0) parts.Add($"{mod.GraphicsCount} hình ảnh");
            if (mod.LanguageIni != null) parts.Add("có văn bản riêng");
            if (parts.Count == 0) parts.Add("không có tệp dùng được");

            row.Controls.Add(new AntdUI.Label
            {
                Text = string.Join("  ·  ", parts),
                Font = new Font("Segoe UI", 9F),
                ForeColor = installed ? InstalledGreen : UiTheme.Muted,
                Location = new Point(12, 33),
                Size = new Size(400, 20),
                BackColor = Color.Transparent,
            });

            // In a column docked to the right rather than placed at a measured offset: the list
            // scrolls, and the moment its scrollbar appears every row gets narrower - which slid
            // a button positioned by arithmetic off the edge of its own row.
            var button = installed
                ? UiTheme.Quiet("Gỡ")
                : UiTheme.Primary("Cài", UiTheme.AccentFolder, height: 36);
            button.Enabled = mod.HasAnything || installed;
            button.Click += (s, e) =>
            {
                if (installed)
                {
                    Run($"Đang gỡ '{mod.Name}'...",
                        folder => { ModInstaller.Uninstall(folder, mod.Name, Say); return null; });
                }
                else
                {
                    Run($"Đang cài '{mod.Name}'...", folder => ModInstaller.Install(folder, mod, Say));
                }
            };
            // 15 above and below centres it in the row rather than leaving it hugging the top.
            row.Controls.Add(UiTheme.ButtonColumn(button, DockStyle.Right, ButtonColumnWidth, top: 15, gap: 12));

            return row;
        }

        private Control Message(string text) => new AntdUI.Label
        {
            Dock = DockStyle.Top,
            Height = 60,
            Text = text,
            ForeColor = UiTheme.Muted,
            TextMultiLine = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9.5F),
        };

        /// <summary>
        /// Runs one install or removal off the UI thread - rewriting a 263 MB archive takes a
        /// while, and doing it inline freezes the window - then reports what changed.
        /// </summary>
        private async void Run(string what, Func<string, ModInstaller.Report> work)
        {
            string gameFolder = _appContext.GetGameFolder();
            if (!HasGameFolder(gameFolder)) return;

            Enabled = false;
            Say(what);
            try
            {
                ModInstaller.Report report = await Task.Run(() => work(gameFolder));
                if (report == null)
                {
                    Say("Xong. Khởi động lại game để thấy thay đổi.");
                }
                else
                {
                    ShowReport(report);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"ModsDialog: {what} failed: {ex}");
                Say("Không thành công.");
                Problem("Cài mod không thành công",
                        ex is IOException
                            ? "Không ghi được tệp game. Hãy đóng game rồi thử lại.\n\n" + ex.Message
                            : ex.Message);
            }
            finally
            {
                Enabled = true;
                Reload();
            }
        }

        /// <summary>
        /// What the install actually changed, file by file. A mod whose graphics went nowhere is
        /// the case worth being loud about: it looks installed and changes nothing in the game.
        /// </summary>
        private void ShowReport(ModInstaller.Report report)
        {
            var lines = new List<string>();
            foreach (ModInstaller.ArchiveChange archive in report.Archives)
            {
                var bits = new List<string>();
                if (archive.Replaced > 0) bits.Add($"{archive.Replaced} hình ảnh đã thay");
                if (archive.Added > 0) bits.Add($"{archive.Added} hình ảnh mới");
                lines.Add($"• {archive.Label}: {string.Join(", ", bits)}");
            }

            if (report.TextStrings > 0)
            {
                lines.Add($"• language_x1_p1.dll: {report.TextStrings} dòng văn bản");
            }

            if (report.AddedNew.Count > 0)
            {
                lines.Add($"\n{report.AddedNew.Count} hình ảnh chưa từng có trong game nên được thêm mới " +
                          $"(id: {string.Join(", ", report.AddedNew.Take(8))}" +
                          (report.AddedNew.Count > 8 ? ", ..." : "") + ").");
            }

            if (!report.DidSomething)
            {
                Say($"'{report.ModName}': không có gì thay đổi.");
                Problem("Mod không thay đổi gì",
                        "Không tìm thấy tệp nào trong game khớp với mod này, nên game sẽ trông như cũ.\n\n" +
                        "Hãy kiểm tra lại thư mục mod: các tệp .slp phải đặt tên theo id (ví dụ '2223.slp').");
                return;
            }

            Say($"Đã cài '{report.ModName}'. Khởi động lại game để thấy thay đổi.");
            AntdUI.Modal.open(new AntdUI.Modal.Config(_owner, $"Đã cài '{report.ModName}'",
                string.Join("\n", lines) + "\n\nKhởi động lại game để thấy thay đổi.",
                AntdUI.TType.Success)
            { CancelText = null, OkText = "Xong" });
        }

        private bool HasGameFolder(string gameFolder)
        {
            if (!string.IsNullOrWhiteSpace(gameFolder) && Directory.Exists(gameFolder)) return true;

            Problem("Chưa có thư mục game",
                    "Vào Cài đặt và chọn thư mục cài đặt AoE2 trước khi cài mod.");
            return false;
        }

        private void Problem(string title, string message) =>
            AntdUI.Modal.open(new AntdUI.Modal.Config(_owner, title, message, AntdUI.TType.Warn)
            { CancelText = null, OkText = "Đóng" });

        private static bool Same(string a, string b) =>
            string.Equals(a, b, StringComparison.CurrentCultureIgnoreCase);

        private void Say(string text) =>
            UiUtils.InvokeOnUiThread(this, () => _status.Text = text, "MODS_STATUS");
    }
}
