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
            AntdUI.Chat.MsgItem msgItem1 = new AntdUI.Chat.MsgItem();
            AntdUI.Chat.MsgItem msgItem2 = new AntdUI.Chat.MsgItem();
            AntdUI.Chat.MsgItem msgItem3 = new AntdUI.Chat.MsgItem();
            AntdUI.CarouselItem carouselItem1 = new AntdUI.CarouselItem();
            AntdUI.CarouselItem carouselItem2 = new AntdUI.CarouselItem();
            AntdUI.TimelineItem timelineItem1 = new AntdUI.TimelineItem();
            AntdUI.TimelineItem timelineItem2 = new AntdUI.TimelineItem();
            AntdUI.TimelineItem timelineItem3 = new AntdUI.TimelineItem();
            homeGridPanel = new AntdUI.GridPanel();
            roomTableLayoutPanel = new TableLayoutPanel();
            joinRoomButton = new AntdUI.Button();
            createRoomButton = new AntdUI.Button();
            label2 = new AntdUI.Label();
            roomTable = new AntdUI.Table();
            tableLayoutPanel1 = new TableLayoutPanel();
            userList = new AntdUI.Chat.MsgList();
            label1 = new AntdUI.Label();
            tableLayoutPanel2 = new TableLayoutPanel();
            gridPanel1 = new AntdUI.GridPanel();
            newsCarousel = new AntdUI.Carousel();
            newsTimeline = new AntdUI.Timeline();
            homeGridPanel.SuspendLayout();
            roomTableLayoutPanel.SuspendLayout();
            tableLayoutPanel1.SuspendLayout();
            tableLayoutPanel2.SuspendLayout();
            gridPanel1.SuspendLayout();
            SuspendLayout();
            // 
            // homeGridPanel
            // 
            homeGridPanel.Controls.Add(roomTableLayoutPanel);
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
            // roomTableLayoutPanel
            // 
            roomTableLayoutPanel.BackColor = Color.Snow;
            roomTableLayoutPanel.ColumnCount = 3;
            roomTableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            roomTableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50F));
            roomTableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50F));
            roomTableLayoutPanel.Controls.Add(joinRoomButton, 2, 0);
            roomTableLayoutPanel.Controls.Add(createRoomButton, 1, 0);
            roomTableLayoutPanel.Controls.Add(label2, 0, 0);
            roomTableLayoutPanel.Controls.Add(roomTable, 0, 1);
            roomTableLayoutPanel.Dock = DockStyle.Fill;
            roomTableLayoutPanel.Font = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point, 0);
            roomTableLayoutPanel.Location = new Point(284, 8);
            roomTableLayoutPanel.Name = "roomTableLayoutPanel";
            roomTableLayoutPanel.RowCount = 2;
            roomTableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
            roomTableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            roomTableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
            roomTableLayoutPanel.Size = new Size(537, 324);
            roomTableLayoutPanel.TabIndex = 1;
            // 
            // joinRoomButton
            // 
            joinRoomButton.AutoSizeMode = AntdUI.TAutoSize.Auto;
            joinRoomButton.Dock = DockStyle.Fill;
            joinRoomButton.Enabled = false;
            joinRoomButton.IconSvg = "UsergroupAddOutlined";
            joinRoomButton.LoadingWaveVertical = true;
            joinRoomButton.Location = new Point(490, 3);
            joinRoomButton.Name = "joinRoomButton";
            joinRoomButton.Padding = new Padding(5);
            joinRoomButton.Shape = AntdUI.TShape.Circle;
            joinRoomButton.Size = new Size(46, 46);
            joinRoomButton.TabIndex = 6;
            joinRoomButton.Type = AntdUI.TTypeMini.Warn;
            // 
            // createRoomButton
            // 
            createRoomButton.AutoSizeMode = AntdUI.TAutoSize.Auto;
            createRoomButton.Dock = DockStyle.Fill;
            createRoomButton.IconSvg = "PlusOutlined";
            createRoomButton.LoadingWaveVertical = true;
            createRoomButton.Location = new Point(440, 3);
            createRoomButton.Name = "createRoomButton";
            createRoomButton.Padding = new Padding(5);
            createRoomButton.Shape = AntdUI.TShape.Circle;
            createRoomButton.Size = new Size(46, 46);
            createRoomButton.TabIndex = 3;
            createRoomButton.Type = AntdUI.TTypeMini.Primary;
            createRoomButton.Click += createRoomButton_Click;
            // 
            // label2
            // 
            label2.Dock = DockStyle.Fill;
            label2.Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold, GraphicsUnit.Point, 0);
            label2.IconGap = 5;
            label2.IconRatio = 1.2F;
            label2.Location = new Point(3, 3);
            label2.Name = "label2";
            label2.Padding = new Padding(10, 0, 0, 0);
            label2.PrefixColor = Color.DarkOrange;
            label2.PrefixSvg = "TeamOutlined";
            label2.Size = new Size(431, 44);
            label2.SuffixSvg = "";
            label2.TabIndex = 2;
            label2.Text = "Phòng game";
            // 
            // roomTable
            // 
            roomTableLayoutPanel.SetColumnSpan(roomTable, 3);
            roomTable.Dock = DockStyle.Fill;
            roomTable.EmptyText = "Chưa có phòng nào!";
            roomTable.Location = new Point(3, 53);
            roomTable.Name = "roomTable";
            roomTable.Size = new Size(531, 268);
            roomTable.TabIndex = 5;
            roomTable.Text = "Phong";
            roomTable.CellClick += roomTable_CellClick;
            // 
            // tableLayoutPanel1
            // 
            tableLayoutPanel1.BackColor = Color.Snow;
            tableLayoutPanel1.ColumnCount = 1;
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50F));
            tableLayoutPanel1.Controls.Add(userList, 0, 1);
            tableLayoutPanel1.Controls.Add(label1, 0, 0);
            tableLayoutPanel1.Dock = DockStyle.Fill;
            tableLayoutPanel1.Font = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point, 0);
            tableLayoutPanel1.Location = new Point(8, 8);
            tableLayoutPanel1.Name = "tableLayoutPanel1";
            tableLayoutPanel1.RowCount = 2;
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            tableLayoutPanel1.Size = new Size(260, 324);
            tableLayoutPanel1.TabIndex = 0;
            // 
            // userList
            // 
            userList.BackColor = Color.MintCream;
            userList.BadgeSvg = "";
            userList.Dock = DockStyle.Fill;
            msgItem1.Icon = Properties.Resources.user_icon;
            msgItem1.Name = "Sang Ngo";
            msgItem1.Text = "sangbro";
            msgItem1.Time = "10:24";
            msgItem2.Icon = Properties.Resources.user_icon;
            msgItem2.Name = "Huy Canh";
            msgItem2.Text = "huycanh11";
            msgItem2.Time = "11:24";
            msgItem3.Icon = Properties.Resources.user_icon;
            msgItem3.Name = "Duong Tank";
            msgItem3.Text = "duongtank";
            msgItem3.Time = "12:12";
            userList.Items.Add(msgItem1);
            userList.Items.Add(msgItem2);
            userList.Items.Add(msgItem3);
            userList.Location = new Point(3, 53);
            userList.Name = "userList";
            userList.Size = new Size(254, 268);
            userList.TabIndex = 0;
            userList.Text = "msgList1";
            // 
            // label1
            // 
            label1.BackColor = Color.Honeydew;
            label1.Dock = DockStyle.Fill;
            label1.Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold, GraphicsUnit.Point, 0);
            label1.IconGap = 5;
            label1.IconRatio = 1.2F;
            label1.Location = new Point(3, 3);
            label1.Name = "label1";
            label1.Padding = new Padding(10, 0, 0, 0);
            label1.PrefixColor = Color.Green;
            label1.PrefixSvg = "UserOutlined";
            label1.Size = new Size(254, 44);
            label1.TabIndex = 1;
            label1.Text = "Thành viên";
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
            newsCarousel.SelectIndex = 1;
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
            roomTableLayoutPanel.ResumeLayout(false);
            roomTableLayoutPanel.PerformLayout();
            tableLayoutPanel1.ResumeLayout(false);
            tableLayoutPanel2.ResumeLayout(false);
            gridPanel1.ResumeLayout(false);
            ResumeLayout(false);
        }

        #endregion

        private AntdUI.GridPanel homeGridPanel;
        private TableLayoutPanel roomTableLayoutPanel;
        private AntdUI.Label label2;
        private AntdUI.Button createRoomButton;
        private AntdUI.Table roomTable;
        private TableLayoutPanel tableLayoutPanel1;
        private AntdUI.Chat.MsgList userList;
        private TableLayoutPanel tableLayoutPanel2;
        private AntdUI.Label label1;
        private AntdUI.GridPanel gridPanel1;
        private AntdUI.Timeline newsTimeline;
        private AntdUI.Carousel newsCarousel;
        private AntdUI.Button joinRoomButton;
    }
}
