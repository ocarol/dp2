﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

using DigitalPlatform;
using DigitalPlatform.CirculationClient;
using DigitalPlatform.CommonControl;
using DigitalPlatform.dp2.Statis;
using DigitalPlatform.GUI;
using DigitalPlatform.RFID;

namespace RfidTool
{
    public partial class MainForm : Form
    {
        ScanDialog _scanDialog = null;

        #region floating message
        internal FloatingMessageForm _floatingMessage = null;

        public FloatingMessageForm FloatingMessageForm
        {
            get
            {
                return this._floatingMessage;
            }
            set
            {
                this._floatingMessage = value;
            }
        }

        public void ShowMessageAutoClear(string strMessage,
string strColor = "",
int delay = 2000,
bool bClickClose = false)
        {
            _ = Task.Run(() =>
            {
                ShowMessage(strMessage,
    strColor,
    bClickClose);
                System.Threading.Thread.Sleep(delay);
                // 中间一直没有变化才去消除它
                if (_floatingMessage.Text == strMessage)
                    ClearMessage();
            });
        }

        public void ShowMessage(string strMessage,
    string strColor = "",
    bool bClickClose = false)
        {
            if (this._floatingMessage == null)
                return;

            Color color = Color.FromArgb(80, 80, 80);

            if (strColor == "red")          // 出错
                color = Color.DarkRed;
            else if (strColor == "yellow")  // 成功，提醒
                color = Color.DarkGoldenrod;
            else if (strColor == "green")   // 成功
                color = Color.Green;
            else if (strColor == "progress")    // 处理过程
                color = Color.FromArgb(80, 80, 80);

            this._floatingMessage.SetMessage(strMessage, color, bClickClose);
        }

        // 线程安全
        public void ClearMessage()
        {
            if (this._floatingMessage == null)
                return;

            this._floatingMessage.Text = "";
        }

        #endregion

        public MainForm()
        {
            InitializeComponent();

            ClientInfo.MainForm = this;

            {
                _floatingMessage = new FloatingMessageForm(this, true);
                // _floatingMessage.AutoHide = false;
                _floatingMessage.Font = new System.Drawing.Font(this.Font.FontFamily, this.Font.Size * 2, FontStyle.Bold);
                _floatingMessage.Opacity = 0.7;
                _floatingMessage.RectColor = Color.Green;
                _floatingMessage.Show(this);

                this.Move += (s1, o1) =>
                {
                    if (this._floatingMessage != null)
                        this._floatingMessage.OnResizeOrMove();
                };
            }

        }

        void CreateScanDialog()
        {
            if (_scanDialog == null)
            {
                _scanDialog = new ScanDialog();

                _scanDialog.FormClosing += _scanDialog_FormClosing;
                _scanDialog.WriteComplete += _scanDialog_WriteComplete;

                GuiUtil.SetControlFont(_scanDialog, this.Font);
                ClientInfo.MemoryState(_scanDialog, "scanDialog", "state");
                _scanDialog.UiState = ClientInfo.Config.Get("scanDialog", "uiState", null);
            }
        }

        private void _scanDialog_WriteComplete(object sender, WriteCompleteventArgs e)
        {
            this.Invoke((Action)(() =>
            {
                AppendItem(e.Chip, e.TagInfo);
            }));
        }

        private void _scanDialog_FormClosing(object sender, FormClosingEventArgs e)
        {
            var dialog = sender as Form;

            // 将关闭改为隐藏
            dialog.Visible = false;
            if (e.CloseReason == CloseReason.UserClosing)
                e.Cancel = true;
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            var ret = ClientInfo.Initial("TestShelfLock");
            if (ret == false)
            {
                Application.Exit();
                return;
            }

            LoadSettings();

            this.ShowMessage("正在连接 RFID 读卡器");
            _ = Task.Run(() =>
            {
                DataModel.InitialDriver();
                this.ClearMessage();
            });
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            {
                if (_scanDialog != null)
                    ClientInfo.Config.Set("scanDialog", "uiState", _scanDialog.UiState);
                _scanDialog?.Close();
                _scanDialog?.Dispose();
                _scanDialog = null;
            }

            this.ShowMessage("正在退出 ...");

            DataModel.ReleaseDriver();
        }

        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            SaveSettings();
        }

        void LoadSettings()
        {
            this.UiState = ClientInfo.Config.Get("global", "ui_state", "");

            // 恢复 MainForm 的显示状态
            {
                var state = ClientInfo.Config.Get("mainForm", "state", "");
                if (string.IsNullOrEmpty(state) == false)
                {
                    FormProperty.SetProperty(state, this, ClientInfo.IsMinimizeMode());
                }
            }

        }

        void SaveSettings()
        {
            // 保存 MainForm 的显示状态
            {
                var state = FormProperty.GetProperty(this);
                ClientInfo.Config.Set("mainForm", "state", state);
            }

            ClientInfo.Config?.Set("global", "ui_state", this.UiState);
            ClientInfo.Finish();
        }

        public string UiState
        {
            get
            {
                List<object> controls = new List<object>
                {
                    this.tabControl1,
                    this.listView_writeHistory,
                };
                return GuiState.GetUiState(controls);
            }
            set
            {
                List<object> controls = new List<object>
                {
                    this.tabControl1,
                    this.listView_writeHistory,
                };
                //_inSetUiState++;
                try
                {
                    GuiState.SetUiState(controls, value);
                }
                finally
                {
                    //_inSetUiState--;
                }
            }
        }

        const int COLUMN_UID = 0;
        const int COLUMN_PII = 1;
        const int COLUMN_TOU = 2;
        const int COLUMN_OI = 3;
        const int COLUMN_AOI = 4;
        const int COLUMN_WRITETIME = 5;

        public void AppendItem(LogicChip chip,
            TagInfo tagInfo)
        {
            ListViewItem item = new ListViewItem();
            this.listView_writeHistory.Items.Add(item);
            item.EnsureVisible();
            ListViewUtil.ChangeItemText(item, COLUMN_UID, tagInfo.UID);
            ListViewUtil.ChangeItemText(item, COLUMN_PII, chip.FindElement(ElementOID.PII)?.Text);
            ListViewUtil.ChangeItemText(item, COLUMN_TOU, chip.FindElement(ElementOID.TypeOfUsage)?.Text);
            ListViewUtil.ChangeItemText(item, COLUMN_OI, chip.FindElement(ElementOID.OI)?.Text);
            ListViewUtil.ChangeItemText(item, COLUMN_AOI, chip.FindElement(ElementOID.AOI)?.Text);
            ListViewUtil.ChangeItemText(item, COLUMN_WRITETIME, DateTime.Now.ToString());
        }

        // 导出选择的行到 Excel 文件
        private void MenuItem_saveToExcelFile_Click(object sender, EventArgs e)
        {
            string strError = "";

            List<ListViewItem> items = new List<ListViewItem>();
            foreach (ListViewItem item in this.listView_writeHistory.Items)
            {
                items.Add(item);
            }

            this.ShowMessage("正在导出选定的事项到 Excel 文件 ...");

            this.EnableControls(false);
            try
            {
                int nRet = ClosedXmlUtil.ExportToExcel(
                    null,
                    items,
                    out strError);
                if (nRet == -1)
                    goto ERROR1;
            }
            finally
            {
                this.EnableControls(true);
                this.ClearMessage();
            }

            return;
        ERROR1:
            MessageBox.Show(this, strError);
        }

        void EnableControls(bool enable)
        {
            this.listView_writeHistory.Enabled = enable;
        }

        // 写入层架标
        private void MenuItem_writeShelfTags_Click(object sender, EventArgs e)
        {
            // 把扫描对话框打开
            CreateScanDialog();

            _scanDialog.TypeOfUsage = "30"; // 层架标
            if (_scanDialog.Visible == false)
                _scanDialog.Show(this);
        }

        // 开始(扫描并)写入图书标签
        private void MenuItem_writeBookTags_Click(object sender, EventArgs e)
        {
            // 把扫描对话框打开
            CreateScanDialog();

            _scanDialog.TypeOfUsage = "10"; // 图书
            if (_scanDialog.Visible == false)
                _scanDialog.Show(this);
        }

        // 设置
        private void MenuItem_settings_Click(object sender, EventArgs e)
        {
            using (SettingDialog dlg = new SettingDialog())
            {
                GuiUtil.SetControlFont(dlg, this.Font);
                ClientInfo.MemoryState(dlg, "settingDialog", "state");

                dlg.ShowDialog(this);
            }
        }

        // 退出
        private void MenuItem_exit_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        // 写入读者证件
        private void MenuItem_writePatronTags_Click(object sender, EventArgs e)
        {
            // 把扫描对话框打开
            CreateScanDialog();

            _scanDialog.TypeOfUsage = "80"; // 读者
            if (_scanDialog.Visible == false)
                _scanDialog.Show(this);
        }
    }

    public delegate void WriteCompleteEventHandler(object sender,
WriteCompleteventArgs e);

    /// <summary>
    /// 写入成功事件的参数
    /// </summary>
    public class WriteCompleteventArgs : EventArgs
    {
        public LogicChip Chip { get; set; }
        public TagInfo TagInfo { get; set; }
    }
}
