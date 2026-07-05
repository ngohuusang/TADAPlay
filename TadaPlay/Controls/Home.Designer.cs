namespace TadaPlay.Controls
{
    partial class Home
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
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

        #region Component Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            AntdUI.CarouselItem carouselItem1 = new AntdUI.CarouselItem();
            AntdUI.CarouselItem carouselItem2 = new AntdUI.CarouselItem();
            AntdUI.TimelineItem timelineItem1 = new AntdUI.TimelineItem();
            AntdUI.TimelineItem timelineItem2 = new AntdUI.TimelineItem();
            AntdUI.TimelineItem timelineItem3 = new AntdUI.TimelineItem();
            homeGridPanel = new AntdUI.GridPanel();
            playPanel = new Panel();
            logRichTextBox = new RichTextBox();
            uploadRecordButton = new AntdUI.Button();
            startGameButton = new AntdUI.Button();
            pingLabel = new Label();
            ipAddressLabel = new Label();
            vpnStatusLabel = new Label();
            playHeaderLabel = new AntdUI.Label();
            tableLayoutPanel1 = new TableLayoutPanel();
            userAvatar = new AntdUI.Avatar();
            logoutButton = new AntdUI.Button();
            userList = new AntdUI.Chat.MsgList();
            tableLayoutPanel3 = new TableLayoutPanel();
            rankLabel = new Label();
            usernameLabel = new Label();
            tableLayoutPanel2 = new TableLayoutPanel();
            gridPanel1 = new AntdUI.GridPanel();
            newsCarousel = new AntdUI.Carousel();
            newsTimeline = new AntdUI.Timeline();
            homeGridPanel.SuspendLayout();
            playPanel.SuspendLayout();
            tableLayoutPanel1.SuspendLayout();
            tableLayoutPanel3.SuspendLayout();
            tableLayoutPanel2.SuspendLayout();
            gridPanel1.SuspendLayout();
            SuspendLayout();
            //
            // homeGridPanel
            //
            homeGridPanel.Controls.Add(playPanel);
            homeGridPanel.Controls.Add(tableLayoutPanel1);
            homeGridPanel.Dock = DockStyle.Fill;
            homeGridPanel.Gap = 5;
            homeGridPanel.Location = new Point(3, 89);
            homeGridPanel.Name = "homeGridPanel";
            homeGridPanel.Size = new Size(829, 340);
            homeGridPanel.Span = "33.33% 66.66%";
            homeGridPanel.TabIndex = 0;
            homeGridPanel.Text = "gridPanel1";
            //
            // playPanel
            //
            playPanel.BackColor = Color.Snow;
            playPanel.Controls.Add(logRichTextBox);
            playPanel.Controls.Add(uploadRecordButton);
            playPanel.Controls.Add(startGameButton);
            playPanel.Controls.Add(pingLabel);
            playPanel.Controls.Add(ipAddressLabel);
            playPanel.Controls.Add(vpnStatusLabel);
            playPanel.Controls.Add(playHeaderLabel);
            playPanel.Dock = DockStyle.Fill;
            playPanel.Location = new Point(284, 8);
            playPanel.Name = "playPanel";
            playPanel.Padding = new Padding(3);
            playPanel.Size = new Size(537, 324);
            playPanel.TabIndex = 1;
            //
            // logRichTextBox
            //
            logRichTextBox.Dock = DockStyle.Fill;
            logRichTextBox.Location = new Point(3, 168);
            logRichTextBox.Name = "logRichTextBox";
            logRichTextBox.ReadOnly = true;
            logRichTextBox.Size = new Size(531, 153);
            logRichTextBox.TabIndex = 6;
            logRichTextBox.Text = "";
            //
            // uploadRecordButton
            //
            uploadRecordButton.AutoSizeMode = AntdUI.TAutoSize.Auto;
            uploadRecordButton.Dock = DockStyle.Top;
            uploadRecordButton.IconSvg = "CloudUploadOutlined";
            uploadRecordButton.Location = new Point(3, 128);
            uploadRecordButton.Name = "uploadRecordButton";
            uploadRecordButton.Padding = new Padding(5);
            uploadRecordButton.Size = new Size(531, 40);
            uploadRecordButton.TabIndex = 5;
            uploadRecordButton.Text = "Tải lên record thủ công";
            uploadRecordButton.Type = AntdUI.TTypeMini.Default;
            //
            // startGameButton
            //
            startGameButton.AutoSizeMode = AntdUI.TAutoSize.Auto;
            startGameButton.Dock = DockStyle.Top;
            startGameButton.Enabled = false;
            startGameButton.IconSvg = "PlayCircleOutlined";
            startGameButton.Location = new Point(3, 88);
            startGameButton.Name = "startGameButton";
            startGameButton.Padding = new Padding(5);
            startGameButton.Size = new Size(531, 40);
            startGameButton.TabIndex = 4;
            startGameButton.Text = "Bắt đầu";
            startGameButton.Type = AntdUI.TTypeMini.Primary;
            startGameButton.Click += startGameButton_Click;
            //
            // pingLabel
            //
            pingLabel.Dock = DockStyle.Top;
            pingLabel.Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point, 0);
            pingLabel.Location = new Point(3, 66);
            pingLabel.Name = "pingLabel";
            pingLabel.Size = new Size(531, 22);
            pingLabel.TabIndex = 3;
            pingLabel.Text = "Ping: -";
            //
            // ipAddressLabel
            //
            ipAddressLabel.Dock = DockStyle.Top;
            ipAddressLabel.Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point, 0);
            ipAddressLabel.Location = new Point(3, 44);
            ipAddressLabel.Name = "ipAddressLabel";
            ipAddressLabel.Size = new Size(531, 22);
            ipAddressLabel.TabIndex = 2;
            ipAddressLabel.Text = "IP: -";
            //
            // vpnStatusLabel
            //
            vpnStatusLabel.Dock = DockStyle.Top;
            vpnStatusLabel.Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point, 0);
            vpnStatusLabel.Location = new Point(3, 22);
            vpnStatusLabel.Name = "vpnStatusLabel";
            vpnStatusLabel.Size = new Size(531, 22);
            vpnStatusLabel.TabIndex = 1;
            vpnStatusLabel.Text = "VPN: Chưa kết nối";
            //
            // playHeaderLabel
            //
            playHeaderLabel.Dock = DockStyle.Top;
            playHeaderLabel.Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold, GraphicsUnit.Point, 0);
            playHeaderLabel.IconGap = 5;
            playHeaderLabel.IconRatio = 1.2F;
            playHeaderLabel.Location = new Point(3, 3);
            playHeaderLabel.Name = "playHeaderLabel";
            playHeaderLabel.Padding = new Padding(10, 0, 0, 0);
            playHeaderLabel.PrefixColor = Color.DarkOrange;
            playHeaderLabel.PrefixSvg = "TeamOutlined";
            playHeaderLabel.Size = new Size(531, 22);
            playHeaderLabel.SuffixSvg = "";
            playHeaderLabel.TabIndex = 0;
            playHeaderLabel.Text = "Kết nối & Chơi game";
            //
            // tableLayoutPanel1
            //
            tableLayoutPanel1.BackColor = Color.Honeydew;
            tableLayoutPanel1.ColumnCount = 3;
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50F));
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50F));
            tableLayoutPanel1.Controls.Add(userAvatar, 0, 0);
            tableLayoutPanel1.Controls.Add(logoutButton, 2, 0);
            tableLayoutPanel1.Controls.Add(userList, 0, 1);
            tableLayoutPanel1.Controls.Add(tableLayoutPanel3, 1, 0);
            tableLayoutPanel1.Dock = DockStyle.Fill;
            tableLayoutPanel1.Font = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point, 0);
            tableLayoutPanel1.Location = new Point(8, 8);
            tableLayoutPanel1.Name = "tableLayoutPanel1";
            tableLayoutPanel1.RowCount = 2;
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
            tableLayoutPanel1.Size = new Size(260, 324);
            tableLayoutPanel1.TabIndex = 0;
            //
            // userAvatar
            //
            userAvatar.BackColor = Color.FromArgb(253, 227, 207);
            userAvatar.Badge = "S";
            userAvatar.ForeColor = Color.FromArgb(245, 106, 0);
            userAvatar.Location = new Point(3, 3);
            userAvatar.Name = "userAvatar";
            userAvatar.Padding = new Padding(3);
            userAvatar.Round = true;
            userAvatar.ShadowOffsetY = 4;
            userAvatar.Size = new Size(44, 44);
            userAvatar.TabIndex = 7;
            userAvatar.Text = "U";
            //
            // logoutButton
            //
            logoutButton.AutoSizeMode = AntdUI.TAutoSize.Auto;
            logoutButton.Dock = DockStyle.Fill;
            logoutButton.IconSvg = "LogoutOutlined";
            logoutButton.LoadingWaveVertical = true;
            logoutButton.Location = new Point(213, 3);
            logoutButton.Name = "logoutButton";
            logoutButton.Padding = new Padding(5);
            logoutButton.Shape = AntdUI.TShape.Circle;
            logoutButton.Size = new Size(46, 46);
            logoutButton.TabIndex = 4;
            logoutButton.Type = AntdUI.TTypeMini.Error;
            logoutButton.Click += logoutButton_Click;
            //
            // userList
            //
            userList.BackColor = Color.MintCream;
            userList.BadgeSvg = "";
            tableLayoutPanel1.SetColumnSpan(userList, 3);
            userList.Dock = DockStyle.Fill;
            userList.Location = new Point(0, 50);
            userList.Margin = new Padding(0);
            userList.Name = "userList";
            userList.Size = new Size(260, 274);
            userList.TabIndex = 0;
            userList.Text = "msgList1";
            //
            // tableLayoutPanel3
            //
            tableLayoutPanel3.ColumnCount = 1;
            tableLayoutPanel3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tableLayoutPanel3.Controls.Add(rankLabel, 0, 1);
            tableLayoutPanel3.Controls.Add(usernameLabel, 0, 0);
            tableLayoutPanel3.Dock = DockStyle.Fill;
            tableLayoutPanel3.Location = new Point(50, 5);
            tableLayoutPanel3.Margin = new Padding(0, 5, 0, 2);
            tableLayoutPanel3.Name = "tableLayoutPanel3";
            tableLayoutPanel3.RowCount = 2;
            tableLayoutPanel3.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            tableLayoutPanel3.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
            tableLayoutPanel3.Size = new Size(160, 43);
            tableLayoutPanel3.TabIndex = 8;
            //
            // rankLabel
            //
            rankLabel.AutoSize = true;
            rankLabel.Dock = DockStyle.Fill;
            rankLabel.Font = new Font("Segoe UI", 6.75F, FontStyle.Regular, GraphicsUnit.Point, 0);
            rankLabel.ForeColor = SystemColors.GrayText;
            rankLabel.Location = new Point(3, 23);
            rankLabel.Name = "rankLabel";
            rankLabel.Padding = new Padding(3, 0, 0, 0);
            rankLabel.Size = new Size(154, 20);
            rankLabel.TabIndex = 2;
            rankLabel.Text = "Ranking";
            //
            // usernameLabel
            //
            usernameLabel.AutoSize = true;
            usernameLabel.Dock = DockStyle.Fill;
            usernameLabel.Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold, GraphicsUnit.Point, 0);
            usernameLabel.Location = new Point(3, 0);
            usernameLabel.Name = "usernameLabel";
            usernameLabel.Size = new Size(154, 23);
            usernameLabel.TabIndex = 1;
            usernameLabel.Text = "Sang Ngo";
            //
            // tableLayoutPanel2
            //
            tableLayoutPanel2.ColumnCount = 1;
            tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tableLayoutPanel2.Controls.Add(homeGridPanel, 0, 1);
            tableLayoutPanel2.Controls.Add(gridPanel1, 0, 0);
            tableLayoutPanel2.Dock = DockStyle.Fill;
            tableLayoutPanel2.Location = new Point(10, 10);
            tableLayoutPanel2.Name = "tableLayoutPanel2";
            tableLayoutPanel2.RowCount = 2;
            tableLayoutPanel2.RowStyles.Add(new RowStyle(SizeType.Percent, 20F));
            tableLayoutPanel2.RowStyles.Add(new RowStyle(SizeType.Percent, 80F));
            tableLayoutPanel2.Size = new Size(835, 432);
            tableLayoutPanel2.TabIndex = 1;
            //
            // gridPanel1
            //
            gridPanel1.Controls.Add(newsCarousel);
            gridPanel1.Controls.Add(newsTimeline);
            gridPanel1.Dock = DockStyle.Fill;
            gridPanel1.Gap = 5;
            gridPanel1.Location = new Point(3, 3);
            gridPanel1.Name = "gridPanel1";
            gridPanel1.Size = new Size(829, 80);
            gridPanel1.Span = "33.33% 66.66%";
            gridPanel1.TabIndex = 1;
            gridPanel1.Text = "gridPanel1";
            //
            // newsCarousel
            //
            newsCarousel.Autodelay = 10;
            newsCarousel.Autoplay = true;
            newsCarousel.BackColor = Color.Snow;
            newsCarousel.Dock = DockStyle.Fill;
            newsCarousel.DotPosition = AntdUI.TAlignMini.Bottom;
            newsCarousel.DotSize = new Size(10, 4);
            carouselItem1.Img = Properties.Resources._1;
            carouselItem2.Img = Properties.Resources._2;
            newsCarousel.Image.Add(carouselItem1);
            newsCarousel.Image.Add(carouselItem2);
            newsCarousel.Location = new Point(284, 8);
            newsCarousel.Name = "newsCarousel";
            newsCarousel.Size = new Size(537, 64);
            newsCarousel.TabIndex = 7;
            //
            // newsTimeline
            //
            timelineItem1.Text = "19:30 14/07/2025 Team 3 vs Team 4";
            timelineItem2.Text = "19:30 15/07/2025 Team 5 vs Team 6";
            timelineItem2.Type = AntdUI.TTypeMini.Success;
            timelineItem3.Text = "9:00 30/07/2025 Offline Sai Gon";
            timelineItem3.Type = AntdUI.TTypeMini.Error;
            newsTimeline.Items.Add(timelineItem1);
            newsTimeline.Items.Add(timelineItem2);
            newsTimeline.Items.Add(timelineItem3);
            newsTimeline.Location = new Point(8, 8);
            newsTimeline.Name = "newsTimeline";
            newsTimeline.Size = new Size(260, 64);
            newsTimeline.TabIndex = 6;
            newsTimeline.Text = "Lịch thi đấu";
            //
            // Home
            //
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            Controls.Add(tableLayoutPanel2);
            Name = "Home";
            Padding = new Padding(10);
            Size = new Size(855, 452);
            Load += Home_Load;
            homeGridPanel.ResumeLayout(false);
            playPanel.ResumeLayout(false);
            tableLayoutPanel1.ResumeLayout(false);
            tableLayoutPanel1.PerformLayout();
            tableLayoutPanel3.ResumeLayout(false);
            tableLayoutPanel3.PerformLayout();
            tableLayoutPanel2.ResumeLayout(false);
            gridPanel1.ResumeLayout(false);
            ResumeLayout(false);
        }

        #endregion

        private AntdUI.GridPanel homeGridPanel;
        private Panel playPanel;
        private AntdUI.Label playHeaderLabel;
        private Label vpnStatusLabel;
        private Label ipAddressLabel;
        private Label pingLabel;
        private AntdUI.Button startGameButton;
        private AntdUI.Button uploadRecordButton;
        private RichTextBox logRichTextBox;
        private TableLayoutPanel tableLayoutPanel1;
        private AntdUI.Chat.MsgList userList;
        private TableLayoutPanel tableLayoutPanel2;
        private AntdUI.GridPanel gridPanel1;
        private AntdUI.Timeline newsTimeline;
        private AntdUI.Carousel newsCarousel;
        private AntdUI.Button logoutButton;
        private AntdUI.Avatar userAvatar;
        private TableLayoutPanel tableLayoutPanel3;
        private Label usernameLabel;
        private Label rankLabel;
    }
}
