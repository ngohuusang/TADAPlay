using System;
using System.Drawing;
using System.Windows.Forms;
using TadaPlay.Contexts.Interfaces;

namespace TadaPlay.Controls
{
    public partial class Setting : UserControl
    {
        Form form;
        private readonly IAppContext _appContext;

        public bool Animation, ShadowEnabled, ShowInWindow, ScrollBarHide, TextRenderingHighQuality;
        public Setting(Form _form, IAppContext appContext = null)
        {
            InitializeComponent();
            form = _form;
            _appContext = appContext;
            switch1.Checked = Animation = AntdUI.Config.Animation;
            switch2.Checked = ShadowEnabled = AntdUI.Config.ShadowEnabled;
            switch3.Checked = ShowInWindow = AntdUI.Config.ShowInWindow;
            switch4.Checked = ScrollBarHide = AntdUI.Config.ScrollBarHide;
            switch5.Checked = TextRenderingHighQuality = AntdUI.Config.TextRenderingHighQuality;

            switch1.CheckedChanged += (s, e) =>
            {
                Animation = e.Value;
            };

            switch2.CheckedChanged += (s, e) =>
            {
                ShadowEnabled = e.Value;
            };

            switch3.CheckedChanged += (s, e) =>
            {
                ShowInWindow = e.Value;
            };

            switch4.CheckedChanged += (s, e) =>
            {
                ScrollBarHide = e.Value;
            };

            switch5.CheckedChanged += (s, e) =>
            {
                TextRenderingHighQuality = e.Value;
            };

            if (_appContext != null)
            {
                BuildGameFolderRow();
            }
        }

        // Game folder picker: where AoE2 saves recorded games (.mgz). Persisted immediately.
        private void BuildGameFolderRow()
        {
            this.Height = Math.Max(this.Height, 360);

            var panel = new Panel { Dock = DockStyle.Bottom, Height = 90, Padding = new Padding(3, 8, 3, 3) };

            var label = new Label
            {
                Text = "Thư mục game (chứa SaveGame):",
                Dock = DockStyle.Top,
                Height = 22,
                Font = new Font("Segoe UI", 9.75F)
            };

            var pathBox = new TextBox
            {
                Dock = DockStyle.Top,
                ReadOnly = true,
                Text = _appContext.GetGameFolder() ?? string.Empty
            };

            var browseButton = new Button { Text = "Chọn thư mục...", Dock = DockStyle.Top, Height = 30 };
            browseButton.Click += (s, e) =>
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = "Chọn thư mục cài đặt AoE2 (nơi chứa thư mục SaveGame).",
                    SelectedPath = _appContext.GetGameFolder() ?? string.Empty
                };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _appContext.SetGameFolder(dialog.SelectedPath);
                    pathBox.Text = dialog.SelectedPath;
                }
            };

            // Docked Top stacks in reverse add order: add button, then path, then label.
            panel.Controls.Add(browseButton);
            panel.Controls.Add(pathBox);
            panel.Controls.Add(label);
            Controls.Add(panel);
        }
    }
}
