namespace TadaPlay
{
    partial class MainForm
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
            windowBar = new AntdUI.PageHeader();
            rankingButton = new AntdUI.Button();
            settingButton = new AntdUI.Button();
            virtualPanel = new AntdUI.VirtualPanel();
            rankingTooltip = new AntdUI.TooltipComponent();
            windowBar.SuspendLayout();
            SuspendLayout();
            // 
            // windowBar
            // 
            windowBar.Controls.Add(rankingButton);
            windowBar.Controls.Add(settingButton);
            windowBar.Dock = DockStyle.Top;
            windowBar.Font = new Font("Segoe UI", 14.25F, FontStyle.Regular, GraphicsUnit.Point, 0);
            windowBar.Icon = Properties.Resources.logo;
            windowBar.IconRatio = 2F;
            windowBar.Location = new Point(0, 0);
            windowBar.Name = "windowBar";
            windowBar.ShowButton = true;
            windowBar.ShowIcon = true;
            windowBar.Size = new Size(1024, 55);
            windowBar.SubFont = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point, 0);
            windowBar.SubGap = 10;
            windowBar.SubText = "aoe2.io.vn";
            windowBar.TabIndex = 0;
            windowBar.Text = "TADA Play";
            // 
            // rankingButton
            // 
            rankingButton.Dock = DockStyle.Right;
            rankingButton.Ghost = true;
            rankingButton.IconSvg = "OrderedListOutlined";
            rankingButton.Location = new Point(780, 0);
            rankingButton.Name = "rankingButton";
            rankingButton.Radius = 0;
            rankingButton.Size = new Size(50, 55);
            rankingButton.TabIndex = 13;
            rankingTooltip.SetTip(rankingButton, "Bảng xếp hạng");
            rankingButton.Type = AntdUI.TTypeMini.Warn;
            rankingButton.Visible = false;
            rankingButton.WaveSize = 0;
            // 
            // settingButton
            // 
            settingButton.Dock = DockStyle.Right;
            settingButton.Ghost = true;
            settingButton.IconSvg = "SettingOutlined";
            settingButton.Location = new Point(830, 0);
            settingButton.Name = "settingButton";
            settingButton.Radius = 0;
            settingButton.Size = new Size(50, 55);
            settingButton.TabIndex = 12;
            rankingTooltip.SetTip(settingButton, "Cài đặt");
            settingButton.Type = AntdUI.TTypeMini.Success;
            settingButton.Visible = false;
            settingButton.WaveSize = 0;
            // 
            // virtualPanel
            // 
            virtualPanel.Dock = DockStyle.Fill;
            virtualPanel.JustifyContent = AntdUI.TJustifyContent.SpaceEvenly;
            virtualPanel.Location = new Point(0, 55);
            virtualPanel.Name = "virtualPanel";
            virtualPanel.Shadow = 20;
            virtualPanel.ShadowOpacityAnimation = true;
            virtualPanel.Size = new Size(1024, 665);
            virtualPanel.TabIndex = 3;
            virtualPanel.Waterfall = true;
            virtualPanel.ItemClick += virtualPanel_ItemClick;
            // 
            // rankingTooltip
            // 
            rankingTooltip.ArrowAlign = AntdUI.TAlign.Bottom;
            // 
            // MainForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1024, 720);
            Controls.Add(virtualPanel);
            Controls.Add(windowBar);
            Icon = (Icon)resources.GetObject("$this.Icon");
            Name = "MainForm";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "TADA Play - aoe2.io.vn";
            Load += MainForm_Load;
            Resize += MainForm_Resize;
            windowBar.ResumeLayout(false);
            ResumeLayout(false);
        }

        #endregion

        private AntdUI.PageHeader windowBar;
        private AntdUI.VirtualPanel virtualPanel;
        private AntdUI.TooltipComponent rankingTooltip;
        private AntdUI.Button settingButton;
        private AntdUI.Button rankingButton;
    }
}
