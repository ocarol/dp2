﻿
namespace dp2LibraryApiTester
{
    partial class SettingDialog
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
            this.button_Cancel = new System.Windows.Forms.Button();
            this.button_OK = new System.Windows.Forms.Button();
            this.tabControl1 = new System.Windows.Forms.TabControl();
            this.tabPage_dp2library = new System.Windows.Forms.TabPage();
            this.textBox_dp2library_location = new System.Windows.Forms.TextBox();
            this.label4 = new System.Windows.Forms.Label();
            this.textBox_dp2library_password = new System.Windows.Forms.TextBox();
            this.textBox_dp2library_userName = new System.Windows.Forms.TextBox();
            this.label3 = new System.Windows.Forms.Label();
            this.label2 = new System.Windows.Forms.Label();
            this.textBox_dp2library_serverUrl = new System.Windows.Forms.TextBox();
            this.label1 = new System.Windows.Forms.Label();
            this.toolStrip_server = new System.Windows.Forms.ToolStrip();
            this.toolStripButton_cfg_setXeServer = new System.Windows.Forms.ToolStripButton();
            this.toolStripSeparator1 = new System.Windows.Forms.ToolStripSeparator();
            this.toolStripButton_cfg_setHongnibaServer = new System.Windows.Forms.ToolStripButton();
            this.tabControl1.SuspendLayout();
            this.tabPage_dp2library.SuspendLayout();
            this.toolStrip_server.SuspendLayout();
            this.SuspendLayout();
            // 
            // button_Cancel
            // 
            this.button_Cancel.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.button_Cancel.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.button_Cancel.Location = new System.Drawing.Point(698, 646);
            this.button_Cancel.Margin = new System.Windows.Forms.Padding(3, 2, 3, 2);
            this.button_Cancel.Name = "button_Cancel";
            this.button_Cancel.Size = new System.Drawing.Size(111, 40);
            this.button_Cancel.TabIndex = 5;
            this.button_Cancel.Text = "取消";
            this.button_Cancel.UseVisualStyleBackColor = true;
            this.button_Cancel.Click += new System.EventHandler(this.button_Cancel_Click);
            // 
            // button_OK
            // 
            this.button_OK.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.button_OK.Location = new System.Drawing.Point(581, 646);
            this.button_OK.Margin = new System.Windows.Forms.Padding(3, 2, 3, 2);
            this.button_OK.Name = "button_OK";
            this.button_OK.Size = new System.Drawing.Size(111, 40);
            this.button_OK.TabIndex = 4;
            this.button_OK.Text = "确定";
            this.button_OK.UseVisualStyleBackColor = true;
            this.button_OK.Click += new System.EventHandler(this.button_OK_Click);
            // 
            // tabControl1
            // 
            this.tabControl1.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.tabControl1.Controls.Add(this.tabPage_dp2library);
            this.tabControl1.Location = new System.Drawing.Point(12, 11);
            this.tabControl1.Margin = new System.Windows.Forms.Padding(3, 2, 3, 2);
            this.tabControl1.Name = "tabControl1";
            this.tabControl1.SelectedIndex = 0;
            this.tabControl1.Size = new System.Drawing.Size(797, 631);
            this.tabControl1.TabIndex = 3;
            // 
            // tabPage_dp2library
            // 
            this.tabPage_dp2library.AutoScroll = true;
            this.tabPage_dp2library.Controls.Add(this.textBox_dp2library_location);
            this.tabPage_dp2library.Controls.Add(this.label4);
            this.tabPage_dp2library.Controls.Add(this.textBox_dp2library_password);
            this.tabPage_dp2library.Controls.Add(this.textBox_dp2library_userName);
            this.tabPage_dp2library.Controls.Add(this.label3);
            this.tabPage_dp2library.Controls.Add(this.label2);
            this.tabPage_dp2library.Controls.Add(this.textBox_dp2library_serverUrl);
            this.tabPage_dp2library.Controls.Add(this.label1);
            this.tabPage_dp2library.Controls.Add(this.toolStrip_server);
            this.tabPage_dp2library.Location = new System.Drawing.Point(4, 31);
            this.tabPage_dp2library.Name = "tabPage_dp2library";
            this.tabPage_dp2library.Size = new System.Drawing.Size(789, 596);
            this.tabPage_dp2library.TabIndex = 1;
            this.tabPage_dp2library.Text = "dp2library";
            this.tabPage_dp2library.UseVisualStyleBackColor = true;
            // 
            // textBox_dp2library_location
            // 
            this.textBox_dp2library_location.ImeMode = System.Windows.Forms.ImeMode.Off;
            this.textBox_dp2library_location.Location = new System.Drawing.Point(191, 268);
            this.textBox_dp2library_location.Margin = new System.Windows.Forms.Padding(5);
            this.textBox_dp2library_location.Name = "textBox_dp2library_location";
            this.textBox_dp2library_location.Size = new System.Drawing.Size(266, 31);
            this.textBox_dp2library_location.TabIndex = 18;
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Location = new System.Drawing.Point(12, 271);
            this.label4.Margin = new System.Windows.Forms.Padding(5, 0, 5, 0);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(148, 21);
            this.label4.TabIndex = 17;
            this.label4.Text = "工作台号(&W)：";
            // 
            // textBox_dp2library_password
            // 
            this.textBox_dp2library_password.ImeMode = System.Windows.Forms.ImeMode.Off;
            this.textBox_dp2library_password.Location = new System.Drawing.Point(191, 220);
            this.textBox_dp2library_password.Margin = new System.Windows.Forms.Padding(5);
            this.textBox_dp2library_password.Name = "textBox_dp2library_password";
            this.textBox_dp2library_password.PasswordChar = '*';
            this.textBox_dp2library_password.Size = new System.Drawing.Size(266, 31);
            this.textBox_dp2library_password.TabIndex = 16;
            // 
            // textBox_dp2library_userName
            // 
            this.textBox_dp2library_userName.ImeMode = System.Windows.Forms.ImeMode.Off;
            this.textBox_dp2library_userName.Location = new System.Drawing.Point(191, 172);
            this.textBox_dp2library_userName.Margin = new System.Windows.Forms.Padding(5);
            this.textBox_dp2library_userName.Name = "textBox_dp2library_userName";
            this.textBox_dp2library_userName.Size = new System.Drawing.Size(266, 31);
            this.textBox_dp2library_userName.TabIndex = 14;
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Location = new System.Drawing.Point(12, 223);
            this.label3.Margin = new System.Windows.Forms.Padding(5, 0, 5, 0);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(106, 21);
            this.label3.TabIndex = 15;
            this.label3.Text = "密码(&P)：";
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(12, 175);
            this.label2.Margin = new System.Windows.Forms.Padding(5, 0, 5, 0);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(127, 21);
            this.label2.TabIndex = 13;
            this.label2.Text = "用户名(&U)：";
            // 
            // textBox_dp2library_serverUrl
            // 
            this.textBox_dp2library_serverUrl.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.textBox_dp2library_serverUrl.Location = new System.Drawing.Point(16, 52);
            this.textBox_dp2library_serverUrl.Margin = new System.Windows.Forms.Padding(5);
            this.textBox_dp2library_serverUrl.Name = "textBox_dp2library_serverUrl";
            this.textBox_dp2library_serverUrl.Size = new System.Drawing.Size(760, 31);
            this.textBox_dp2library_serverUrl.TabIndex = 11;
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(11, 20);
            this.label1.Margin = new System.Windows.Forms.Padding(5, 0, 5, 0);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(249, 21);
            this.label1.TabIndex = 10;
            this.label1.Text = "dp2Library 服务器 URL:";
            // 
            // toolStrip_server
            // 
            this.toolStrip_server.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.toolStrip_server.AutoSize = false;
            this.toolStrip_server.Dock = System.Windows.Forms.DockStyle.None;
            this.toolStrip_server.GripStyle = System.Windows.Forms.ToolStripGripStyle.Hidden;
            this.toolStrip_server.ImageScalingSize = new System.Drawing.Size(24, 24);
            this.toolStrip_server.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.toolStripButton_cfg_setXeServer,
            this.toolStripSeparator1,
            this.toolStripButton_cfg_setHongnibaServer});
            this.toolStrip_server.Location = new System.Drawing.Point(16, 92);
            this.toolStrip_server.Name = "toolStrip_server";
            this.toolStrip_server.Size = new System.Drawing.Size(760, 51);
            this.toolStrip_server.TabIndex = 12;
            this.toolStrip_server.Text = "toolStrip1";
            // 
            // toolStripButton_cfg_setXeServer
            // 
            this.toolStripButton_cfg_setXeServer.Alignment = System.Windows.Forms.ToolStripItemAlignment.Right;
            this.toolStripButton_cfg_setXeServer.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            this.toolStripButton_cfg_setXeServer.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.toolStripButton_cfg_setXeServer.Name = "toolStripButton_cfg_setXeServer";
            this.toolStripButton_cfg_setXeServer.Size = new System.Drawing.Size(142, 45);
            this.toolStripButton_cfg_setXeServer.Text = "单机版服务器";
            this.toolStripButton_cfg_setXeServer.ToolTipText = "设为单机版服务器";
            this.toolStripButton_cfg_setXeServer.Click += new System.EventHandler(this.toolStripButton_cfg_setXeServer_Click);
            // 
            // toolStripSeparator1
            // 
            this.toolStripSeparator1.Alignment = System.Windows.Forms.ToolStripItemAlignment.Right;
            this.toolStripSeparator1.Name = "toolStripSeparator1";
            this.toolStripSeparator1.Size = new System.Drawing.Size(6, 51);
            // 
            // toolStripButton_cfg_setHongnibaServer
            // 
            this.toolStripButton_cfg_setHongnibaServer.Alignment = System.Windows.Forms.ToolStripItemAlignment.Right;
            this.toolStripButton_cfg_setHongnibaServer.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            this.toolStripButton_cfg_setHongnibaServer.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.toolStripButton_cfg_setHongnibaServer.Name = "toolStripButton_cfg_setHongnibaServer";
            this.toolStripButton_cfg_setHongnibaServer.Size = new System.Drawing.Size(231, 45);
            this.toolStripButton_cfg_setHongnibaServer.Text = "红泥巴.数字平台服务器";
            this.toolStripButton_cfg_setHongnibaServer.ToolTipText = "设为红泥巴.数字平台服务器";
            this.toolStripButton_cfg_setHongnibaServer.Click += new System.EventHandler(this.toolStripButton_cfg_setHongnibaServer_Click);
            // 
            // SettingDialog
            // 
            this.AcceptButton = this.button_OK;
            this.AutoScaleDimensions = new System.Drawing.SizeF(11F, 21F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.CancelButton = this.button_Cancel;
            this.ClientSize = new System.Drawing.Size(821, 697);
            this.Controls.Add(this.button_Cancel);
            this.Controls.Add(this.button_OK);
            this.Controls.Add(this.tabControl1);
            this.Font = new System.Drawing.Font("宋体", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.Name = "SettingDialog";
            this.ShowIcon = false;
            this.ShowInTaskbar = false;
            this.Text = "设置";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.SettingDialog_FormClosing);
            this.FormClosed += new System.Windows.Forms.FormClosedEventHandler(this.SettingDialog_FormClosed);
            this.Load += new System.EventHandler(this.SettingDialog_Load);
            this.tabControl1.ResumeLayout(false);
            this.tabPage_dp2library.ResumeLayout(false);
            this.tabPage_dp2library.PerformLayout();
            this.toolStrip_server.ResumeLayout(false);
            this.toolStrip_server.PerformLayout();
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.Button button_Cancel;
        private System.Windows.Forms.Button button_OK;
        private System.Windows.Forms.TabControl tabControl1;
        private System.Windows.Forms.TabPage tabPage_dp2library;
        public System.Windows.Forms.TextBox textBox_dp2library_location;
        private System.Windows.Forms.Label label4;
        public System.Windows.Forms.TextBox textBox_dp2library_password;
        public System.Windows.Forms.TextBox textBox_dp2library_userName;
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.TextBox textBox_dp2library_serverUrl;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.ToolStrip toolStrip_server;
        private System.Windows.Forms.ToolStripButton toolStripButton_cfg_setXeServer;
        private System.Windows.Forms.ToolStripSeparator toolStripSeparator1;
        private System.Windows.Forms.ToolStripButton toolStripButton_cfg_setHongnibaServer;
    }
}