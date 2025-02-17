﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

using DigitalPlatform.LibraryClient;

namespace DigitalPlatform.CirculationClient
{
    public partial class ChangePasswordDialog : Form
    {
        public string ServerUrl { get; set; }

        public ChangePasswordDialog()
        {
            InitializeComponent();
        }

        private void button_worker_changePassword_Click(object sender, EventArgs e)
        {
            bool succeed = false;
            this.button_worker_changePassword.Enabled = false;
            try
            {
                if (this.textBox_worker_newPassword.Text != this.textBox_worker_confirmNewPassword.Text)
                {
                    MessageBox.Show(this, "新密码和确认新密码不一致。请重新输入");
                    return;
                }

                using (LibraryChannel channel = new LibraryChannel())
                {
                    channel.Timeout = TimeSpan.FromSeconds(10);
                    channel.Url = ServerUrl;
                    bool isReader = this.checkBox_isReader.Checked;
                    
                    long ret = 0;
                    string strError = "";

                    if (isReader)
                    {
                        // Result.Value
                        //      -1  出错
                        //      0   旧密码不正确
                        //      1   旧密码正确,已修改为新密码
                        ret = channel.ChangeReaderPassword(null,
                            this.textBox_worker_userName.Text,
                            this.textBox_worker_oldPassword.Text,
                            this.textBox_worker_newPassword.Text,
                            out strError);
                        if (ret == -1 || ret == 0)
                            MessageBox.Show(this, strError);
                        else
                        {
                            MessageBox.Show(this, "密码修改成功");
                            succeed = true;
                        }
                    }
                    else
                    {
                        // return.Value:
                        //      -1  出错
                        //      0   成功
                        ret = channel.ChangeUserPassword(null,
                            this.textBox_worker_userName.Text,
                            this.textBox_worker_oldPassword.Text,
                            this.textBox_worker_newPassword.Text,
                            out strError);
                        if (ret == -1)
                            MessageBox.Show(this, strError);
                        else
                        {
                            MessageBox.Show(this, "密码修改成功");
                            succeed = true;
                        }
                    }

                    channel.Close();
                }
            }
            finally
            {
                this.button_worker_changePassword.Enabled = true;
            }

            if (succeed)
            {
                this.DialogResult = DialogResult.OK;
                this.Close();
            }
        }

        public string UserName
        {
            get
            {
                return this.textBox_worker_userName.Text;
            }
            set
            {
                this.textBox_worker_userName.Text = value;
            }
        }

        public string OldPassword
        {
            get
            {
                return this.textBox_worker_oldPassword.Text;
            }
            set
            {
                this.textBox_worker_oldPassword.Text = value;
            }
        }

        public string NewPassword
        {
            get
            {
                return this.textBox_worker_newPassword.Text;
            }
            set
            {
                this.textBox_worker_newPassword.Text = value;
            }
        }

        public string ConfirmNewPassword
        {
            get
            {
                return this.textBox_worker_confirmNewPassword.Text;
            }
            set
            {
                this.textBox_worker_confirmNewPassword.Text = value;
            }
        }

        public bool IsReader
        {
            get
            {
                return this.checkBox_isReader.Checked;
            }
            set
            {
                this.checkBox_isReader.Checked = value;
            }
        }

        private void checkBox_isReader_CheckedChanged(object sender, EventArgs e)
        {
            if (this.checkBox_isReader.Checked == false)
            {
                this.label_userName.Text = "用户名(&U):";
            }
            else
            {
                this.label_userName.Text = "读者证条码号(&B):";
            }
        }
    }
}
