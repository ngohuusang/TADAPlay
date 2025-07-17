namespace TadaPlay.Controls
{
    partial class Login
    {
        /// <summary> 
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

  
        #region Component Designer generated code

        /// <summary> 
        /// Required method for Designer support - do not modify 
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            passwordTextBox = new AntdUI.Input();
            usernameTextBox = new AntdUI.Input();
            tableLayoutPanel1 = new TableLayoutPanel();
            autoLoginCheckbox = new AntdUI.Checkbox();
            signInButton = new AntdUI.Button();
            tableLayoutPanel2 = new TableLayoutPanel();
            label1 = new AntdUI.Label();
            label2 = new AntdUI.Label();
            tableLayoutPanel1.SuspendLayout();
            tableLayoutPanel2.SuspendLayout();
            SuspendLayout();
            // 
            // passwordTextBox
            // 
            passwordTextBox.AllowClear = true;
            passwordTextBox.Dock = DockStyle.Fill;
            passwordTextBox.Location = new Point(3, 111);
            passwordTextBox.Name = "passwordTextBox";
            passwordTextBox.PasswordChar = '*';
            passwordTextBox.PlaceholderText = "Mật khẩu";
            passwordTextBox.PrefixSvg = "UnlockOutlined";
            passwordTextBox.Size = new Size(314, 42);
            passwordTextBox.TabIndex = 2;
            // 
            // usernameTextBox
            // 
            usernameTextBox.AllowClear = true;
            usernameTextBox.Dock = DockStyle.Fill;
            usernameTextBox.Location = new Point(3, 63);
            usernameTextBox.Name = "usernameTextBox";
            usernameTextBox.PlaceholderText = "Tài khoản";
            usernameTextBox.PrefixSvg = "UserOutlined";
            usernameTextBox.Size = new Size(314, 42);
            usernameTextBox.TabIndex = 1;
            // 
            // tableLayoutPanel1
            // 
            tableLayoutPanel1.ColumnCount = 1;
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tableLayoutPanel1.Controls.Add(usernameTextBox, 0, 1);
            tableLayoutPanel1.Controls.Add(passwordTextBox, 0, 2);
            tableLayoutPanel1.Controls.Add(autoLoginCheckbox, 0, 3);
            tableLayoutPanel1.Controls.Add(signInButton, 0, 4);
            tableLayoutPanel1.Controls.Add(tableLayoutPanel2, 0, 0);
            tableLayoutPanel1.Dock = DockStyle.Fill;
            tableLayoutPanel1.Font = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point, 0);
            tableLayoutPanel1.GrowStyle = TableLayoutPanelGrowStyle.FixedSize;
            tableLayoutPanel1.Location = new Point(0, 0);
            tableLayoutPanel1.Name = "tableLayoutPanel1";
            tableLayoutPanel1.RowCount = 5;
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 20F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 20F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 15F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 20F));
            tableLayoutPanel1.Size = new Size(320, 240);
            tableLayoutPanel1.TabIndex = 0;
            // 
            // autoLoginCheckbox
            // 
            autoLoginCheckbox.Dock = DockStyle.Fill;
            autoLoginCheckbox.Location = new Point(3, 159);
            autoLoginCheckbox.Name = "autoLoginCheckbox";
            autoLoginCheckbox.Size = new Size(314, 30);
            autoLoginCheckbox.TabIndex = 3;
            autoLoginCheckbox.Text = "Tự động đăng nhập";
            // 
            // signInButton
            // 
            signInButton.Dock = DockStyle.Fill;
            signInButton.LoadingRespondClick = true;
            signInButton.Location = new Point(5, 195);
            signInButton.Margin = new Padding(5, 3, 5, 3);
            signInButton.Name = "signInButton";
            signInButton.Size = new Size(310, 42);
            signInButton.TabIndex = 4;
            signInButton.Text = "Đăng nhập";
            signInButton.Type = AntdUI.TTypeMini.Error;
            signInButton.Click += signInButton_Click;
            // 
            // tableLayoutPanel2
            // 
            tableLayoutPanel2.ColumnCount = 1;
            tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tableLayoutPanel2.Controls.Add(label1, 0, 0);
            tableLayoutPanel2.Controls.Add(label2, 0, 1);
            tableLayoutPanel2.Dock = DockStyle.Fill;
            tableLayoutPanel2.Location = new Point(3, 3);
            tableLayoutPanel2.Name = "tableLayoutPanel2";
            tableLayoutPanel2.RowCount = 2;
            tableLayoutPanel2.RowStyles.Add(new RowStyle(SizeType.Percent, 60F));
            tableLayoutPanel2.RowStyles.Add(new RowStyle(SizeType.Percent, 40F));
            tableLayoutPanel2.Size = new Size(314, 54);
            tableLayoutPanel2.TabIndex = 5;
            // 
            // label1
            // 
            label1.Dock = DockStyle.Fill;
            label1.Font = new Font("Segoe UI", 15.75F, FontStyle.Bold, GraphicsUnit.Point, 0);
            label1.Location = new Point(3, 3);
            label1.Name = "label1";
            label1.Size = new Size(308, 26);
            label1.TabIndex = 0;
            label1.Text = "Đăng nhập";
            // 
            // label2
            // 
            label2.Dock = DockStyle.Fill;
            label2.Font = new Font("Segoe UI", 9.75F, FontStyle.Regular, GraphicsUnit.Point, 0);
            label2.Location = new Point(3, 35);
            label2.Name = "label2";
            label2.Size = new Size(308, 16);
            label2.TabIndex = 1;
            label2.Text = "Vui lòng nhập thông tin tài khoản";
            // 
            // Login
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            Controls.Add(tableLayoutPanel1);
            Name = "Login";
            Size = new Size(320, 240);
            Load += Login_Load;
            tableLayoutPanel1.ResumeLayout(false);
            tableLayoutPanel2.ResumeLayout(false);
            ResumeLayout(false);
        }

        #endregion

        private AntdUI.Input passwordTextBox;
        private AntdUI.Input usernameTextBox;
        private TableLayoutPanel tableLayoutPanel1;
        private AntdUI.Checkbox autoLoginCheckbox;
        private AntdUI.Button signInButton;
        private TableLayoutPanel tableLayoutPanel2;
        private AntdUI.Label label1;
        private AntdUI.Label label2;
    }
}
