namespace TadaPlay
{
    partial class RoomDetailForm
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

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            windowBar = new AntdUI.PageHeader();
            tableLayoutPanel2 = new TableLayoutPanel();
            kickUserButton = new AntdUI.Button();
            startGameButton = new AntdUI.Button();
            label1 = new AntdUI.Label();
            tableLayoutPanel1 = new TableLayoutPanel();
            label2 = new AntdUI.Label();
            userListView = new ListView();
            logChatList = new AntdUI.Chat.ChatList();
            tableLayoutPanel2.SuspendLayout();
            tableLayoutPanel1.SuspendLayout();
            SuspendLayout();
            // 
            // windowBar
            // 
            windowBar.Dock = DockStyle.Top;
            windowBar.Font = new Font("Segoe UI", 14.25F, FontStyle.Regular, GraphicsUnit.Point, 0);
            windowBar.Icon = Properties.Resources.Barracks_aoe2DE;
            windowBar.IconRatio = 2F;
            windowBar.Location = new Point(0, 0);
            windowBar.MaximizeBox = false;
            windowBar.Name = "windowBar";
            windowBar.ShowButton = true;
            windowBar.ShowIcon = true;
            windowBar.Size = new Size(802, 62);
            windowBar.SubFont = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point, 0);
            windowBar.SubGap = 10;
            windowBar.SubText = "aoe2.io.vn";
            windowBar.TabIndex = 1;
            windowBar.Text = "Phòng";
            // 
            // tableLayoutPanel2
            // 
            tableLayoutPanel2.ColumnCount = 2;
            tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            tableLayoutPanel2.Controls.Add(kickUserButton, 1, 0);
            tableLayoutPanel2.Controls.Add(startGameButton, 0, 0);
            tableLayoutPanel2.Dock = DockStyle.Fill;
            tableLayoutPanel2.Location = new Point(3, 396);
            tableLayoutPanel2.Name = "tableLayoutPanel2";
            tableLayoutPanel2.RowCount = 1;
            tableLayoutPanel2.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            tableLayoutPanel2.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            tableLayoutPanel2.Size = new Size(261, 44);
            tableLayoutPanel2.TabIndex = 4;
            // 
            // kickUserButton
            // 
            kickUserButton.Dock = DockStyle.Fill;
            kickUserButton.Location = new Point(133, 3);
            kickUserButton.Name = "kickUserButton";
            kickUserButton.Size = new Size(125, 38);
            kickUserButton.TabIndex = 4;
            kickUserButton.Text = "Rời phòng";
            kickUserButton.Type = AntdUI.TTypeMini.Error;
            // 
            // startGameButton
            // 
            startGameButton.Dock = DockStyle.Fill;
            startGameButton.Location = new Point(3, 3);
            startGameButton.Name = "startGameButton";
            startGameButton.Size = new Size(124, 38);
            startGameButton.TabIndex = 3;
            startGameButton.Text = "Khởi động game";
            startGameButton.Type = AntdUI.TTypeMini.Success;
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
            label1.Size = new Size(261, 44);
            label1.TabIndex = 3;
            label1.Text = "Thành viên";
            // 
            // tableLayoutPanel1
            // 
            tableLayoutPanel1.ColumnCount = 2;
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3333321F));
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66.6666641F));
            tableLayoutPanel1.Controls.Add(label2, 1, 0);
            tableLayoutPanel1.Controls.Add(label1, 0, 0);
            tableLayoutPanel1.Controls.Add(tableLayoutPanel2, 0, 2);
            tableLayoutPanel1.Controls.Add(userListView, 0, 1);
            tableLayoutPanel1.Controls.Add(logChatList, 1, 1);
            tableLayoutPanel1.Dock = DockStyle.Fill;
            tableLayoutPanel1.Location = new Point(0, 62);
            tableLayoutPanel1.Name = "tableLayoutPanel1";
            tableLayoutPanel1.RowCount = 3;
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
            tableLayoutPanel1.Size = new Size(802, 443);
            tableLayoutPanel1.TabIndex = 2;
            // 
            // label2
            // 
            label2.BackColor = Color.Honeydew;
            label2.Dock = DockStyle.Fill;
            label2.Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold, GraphicsUnit.Point, 0);
            label2.IconGap = 5;
            label2.IconRatio = 1.2F;
            label2.Location = new Point(270, 3);
            label2.Name = "label2";
            label2.Padding = new Padding(10, 0, 0, 0);
            label2.PrefixColor = Color.Green;
            label2.PrefixSvg = "ScheduleOutlined";
            label2.Size = new Size(529, 44);
            label2.TabIndex = 5;
            label2.Text = "Nhật ký";
            // 
            // userListView
            // 
            userListView.Dock = DockStyle.Fill;
            userListView.Location = new Point(3, 53);
            userListView.Name = "userListView";
            userListView.Size = new Size(261, 337);
            userListView.TabIndex = 6;
            userListView.UseCompatibleStateImageBehavior = false;
            // 
            // logChatList
            // 
            logChatList.Dock = DockStyle.Fill;
            logChatList.Location = new Point(270, 53);
            logChatList.Name = "logChatList";
            logChatList.Size = new Size(529, 337);
            logChatList.TabIndex = 7;
            logChatList.Text = "chatList1";
            // 
            // RoomDetailForm
            // 
            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(802, 505);
            Controls.Add(tableLayoutPanel1);
            Controls.Add(windowBar);
            Font = new Font("Segoe UI", 9.75F, FontStyle.Regular, GraphicsUnit.Point, 0);
            MaximizeBox = false;
            Name = "RoomDetailForm";
            Resizable = false;
            Text = "Phòng thi đấu - TADA Play";
            TopMost = true;
            FormClosing += RoomDetailForm_FormClosing;
            FormClosed += RoomDetailForm_FormClosed;
            tableLayoutPanel2.ResumeLayout(false);
            tableLayoutPanel1.ResumeLayout(false);
            ResumeLayout(false);
        }

        #endregion

        private AntdUI.PageHeader windowBar;
        private TableLayoutPanel tableLayoutPanel2;
        private AntdUI.Button kickUserButton;
        private AntdUI.Button startGameButton;
        private AntdUI.Label label1;
        private TableLayoutPanel tableLayoutPanel1;
        private AntdUI.Label label2;
        private ListView userListView;
        private AntdUI.Chat.ChatList logChatList;
    }
}