﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;

using DigitalPlatform;
using DigitalPlatform.CommonControl;
using DigitalPlatform.IO;
using DigitalPlatform.Text;
using DigitalPlatform.LibraryClient;
using DigitalPlatform.LibraryClient.localhost;
using DigitalPlatform.RFID;
using DigitalPlatform.Core;

namespace dp2Circulation
{
    /// <summary>
    /// 一个出纳任务
    /// </summary>
    internal class ChargingTask
    {
        public string ID = "";  // 任务 ID。这是在任务之间唯一的一个字符串，用于查询和定位任务
        public string ReaderBarcode = "";
        public string ItemBarcode = "";
        public string Action = "";  // load_reader_info borrow return lost renew
        public string Parameters = "";  // 附加的参数。simulate_reservation_arrive 等
        public string State = "";   // 空 / begin / finish / error
        public string Color = "";   // 颜色
        public string ErrorInfo = "";   // 出错信息。指 API 出错信息

        string _easErrorInfo = "";
        // EAS 出错信息 (2023/11/15)
        public string EasErrorInfo
        {
            get
            {
                return _easErrorInfo;
            }
            set
            {
                string old_value = _easErrorInfo;
                _easErrorInfo = value;
                if (string.IsNullOrEmpty(old_value) == false
                    && string.IsNullOrEmpty(value) == true)
                {
                    TriggerEasErrorCleared();
                }
            }
        }

        public void TriggerEasErrorCleared()
        {
            this.EasErrorCleared?.Invoke(this, new EasErrorClearedEventArgs());
        }

        // 2023/11/16
        // EasErrorInfo 被清除时触发的事件
        public event EasErrorClearedEventHandler EasErrorCleared;

        public delegate void EasErrorClearedEventHandler(object sender,
    EasErrorClearedEventArgs e);

        public class EasErrorClearedEventArgs : EventArgs
        {
            /*
            public string OldValue { get; set; }
            public string NewValue { get; set; }
            */
        }

        public string ReaderName = "";  // 读者姓名
        public string ItemSummary = ""; // 册的书目摘要

        public string ReaderXml = "";   // 读者记录 XML

        public string ReaderBarcodeRfidType = "";
        // 册条码号是否为 rfid 类型。值为 "pii" "uid" 和 "" 之一
        // 如果是 "pii" 或 "uid"，则完成借书和还书操作最后一步要修改 RFID 标签的 EAS
        public string ItemBarcodeEasType = "";

        const int IMAGEINDEX_WAITING = 0;
        const int IMAGEINDEX_FINISH = 1;
        const int IMAGEINDEX_ERROR = 2;
        const int IMAGEINDEX_INFORMATION = 3;

        public void RefreshPatronCardDisplay(DpRow row)
        {
            if (row.Count < 3)
                return;

            DpCell cell = row[2];
            if (this.Action == "load_reader_info")
                cell.OwnerDraw = true;

            cell.Relayout();
        }

        // parameters:
        //      strSpeakStyle   状态/状态+内容
        public string GetSpeakContent(DpRow row, string strSpeakStyle)
        {
            if (this.State == "finish" || this.State == "error")
            {
                string strText = "";
                if (this.Color == "red")
                    strText = "错误: ";
                else if (this.Color == "yellow")
                    strText = "提示: ";
                else
                    return "";
                if (strSpeakStyle == "状态")
                    return strText;
                // 状态+内容
                return strText + this.WholeErrorInfo;
            }

            return "";
        }

        // 2023/11/15
        public string WholeErrorInfo
        {
            get
            {
                List<string> errors = new List<string>();
                if (string.IsNullOrEmpty(this.ErrorInfo) == false)
                    errors.Add(this.ErrorInfo);
                if (string.IsNullOrEmpty(this.EasErrorInfo) == false)
                    errors.Add(this.EasErrorInfo);
                if (errors.Count == 0)
                    return "";
                return StringUtil.MakePathList(errors, "; ");
            }
        }

        // 2023/11/15
        // 在 ErrorInfo 现有内容后面追加
        public void AppendErrorInfo(string text)
        {
            if (string.IsNullOrEmpty(this.ErrorInfo) == false)
                this.ErrorInfo += "; ";
            this.ErrorInfo += text;
        }

        // parameters:
        //      color   如果不为 null，表示使用这个颜色，否则用 this.Color
        public void RefreshDisplay(DpRow row,
            string color = null)
        {
            // 初始化列
            if (row.Count == 0)
            {
                // 色条
                DpCell cell = new DpCell();
                row.Add(cell);

                // 状态
                cell = new DpCell();
                cell.ImageIndex = -1;
                row.Add(cell);

                // 内容
                cell = new DpCell();
                row.Add(cell);
            }

            bool bStateText = false;

            // 状态
            // row[0].Text = this.State;
            DpCell state_cell = row[1];
            if (this.State == "begin")
            {
                if (bStateText == true)
                    state_cell.Text = "请求中";
                state_cell.ImageIndex = IMAGEINDEX_WAITING;
            }
            else if (this.State == "error")
            {
                if (bStateText == true)
                    state_cell.Text = "出错";
                state_cell.ImageIndex = IMAGEINDEX_ERROR;
            }
            else if (this.State == "finish")
            {
                if (bStateText == true)
                    state_cell.Text = "完成";
                state_cell.ImageIndex = IMAGEINDEX_FINISH;
            }
            else
            {
                if (bStateText == true)
                    state_cell.Text = "未处理";
                state_cell.ImageIndex = -1;
            }

            string strText = "";
            // 内容
            if (this.Action == "load_reader_info")
                strText = "装载读者信息 " + this.ReaderBarcode;
            else if (this.Action == "borrow")
            {
                strText = GetOperText("借");
            }
            else if (this.Action == "return")
            {
                strText = GetOperText("还");
            }
            else if (this.Action == "verify_return")
            {
                strText = GetOperText("(验证)还");
            }
            else if (this.Action == "lost")
            {
                strText = GetOperText("丢失");
            }
            else if (this.Action == "verify_lost")
            {
                strText = GetOperText("(验证)丢失");
            }
            else if (this.Action == "renew")
            {
                strText = GetOperText("续借");
            }
            else if (this.Action == "verify_renew")
            {
                strText = GetOperText("(验证)续借");
            }
            else if (this.Action == "inventory")
            {
                strText = GetOperText("盘点");
            }
            else if (this.Action == "transfer")
            {
                strText = GetOperText("调拨");
            }
            else if (this.Action == "read")
            {
                strText = GetOperText("读过");
            }
            else if (this.Action == "boxing")
            {
                strText = GetOperText("配书");
            }

            var wholeErrorInfo = this.WholeErrorInfo;
            if (string.IsNullOrEmpty(wholeErrorInfo) == false)
                strText += "\r\n===\r\n" + wholeErrorInfo;

            row[2].Text = strText;

            // 缺省为透明色，即使用 row 的前景背景色 2015/10/18
            ////row.BackColor = SystemColors.Window;
            ////row.ForeColor = SystemColors.WindowText;

            DpCell color_cell = row[0];

            // 2023/11/21
            if (color == null)
                color = this.Color;

            SetColor(row, color_cell, color);
        }

        static void SetColor(
            DpRow row,
            DpCell color_cell,
            string color)
        {
            // row.BackColor = System.Drawing.Color.Transparent;
            if (color == "red")
            {
                color_cell.BackColor = System.Drawing.Color.Red;
                // row.ForeColor = System.Drawing.Color.White;
            }
            else if (color == "green")
            {
                color_cell.BackColor = System.Drawing.Color.Green;
                // row.ForeColor = System.Drawing.Color.White;
            }
            else if (color == "yellow")
            {
                row.BackColor = System.Drawing.Color.Yellow;
                color_cell.BackColor = System.Drawing.Color.Transparent;
                // row.ForeColor = System.Drawing.Color.Black;
            }
            else if (color == "light")
            {
                color_cell.BackColor = System.Drawing.Color.LightGray;
            }
            else if (color == "purple")
            {
                color_cell.BackColor = System.Drawing.Color.Purple;
            }
            else if (color == "black")
            {
                color_cell.BackColor = System.Drawing.Color.Purple;
#if NO
                row.BackColor = System.Drawing.Color.Black;
                row.ForeColor = System.Drawing.Color.LightGray;
#endif
            }
            else
            {
                // color_cell.BackColor = System.Drawing.Color.Transparent;
                color_cell.BackColor = System.Drawing.Color.White;
            }
        }

        string GetOperText(string strOperName)
        {
            string strSummary = "";
            if (string.IsNullOrEmpty(this.ItemSummary) == false)
                strSummary = "\r\n---\r\n" + this.ItemSummary;

            if (strOperName == "调拨")
            {
                string direction = StringUtil.GetParameterByPrefix(this.Parameters, "direction");
                if (string.IsNullOrEmpty(direction))
                    direction = this.Parameters.Replace("location:", "-->");

                return $"{strOperName} {this.ItemBarcode} {direction} {strSummary}";
            }
            else if (string.IsNullOrEmpty(this.ReaderBarcode) == false
                && strOperName != "盘点")
                return this.ReaderBarcode + " " + this.ReaderName + " " + strOperName + " " + this.ItemBarcode + strSummary;
            else
                return strOperName + " " + this.ItemBarcode + strSummary;
        }

        // 任务是否完成
        // error finish 都是完成状态
        public bool Compeleted
        {
            get
            {
                if (this.State == "error" || this.State == "finish")
                    return true;
                return false;
            }
        }
    }

    /// <summary>
    /// 出纳任务列表
    /// </summary>
    internal class TaskList : ThreadBase
    {
        public QuickChargingForm Container = null;

        List<ChargingTask> _tasks = new List<ChargingTask>();

        string _strCurrentReaderBarcode = "";
        public string CurrentReaderBarcode
        {
            get
            {
                return this._strCurrentReaderBarcode;
            }
            set
            {
                this._strCurrentReaderBarcode = value;

                if (this.Container != null)
                    this.Container.CurrentReaderBarcodeChanged(this.CurrentReaderBarcode);  // 通知 读者记录已经成功装载
            }
        }

#if OLD_CHARGING_CHANNEL
        /// <summary>
        /// 通讯通道
        /// </summary>
        public LibraryChannel Channel = null;
        public DigitalPlatform.Stop stop = null;
#endif

        ReaderWriterLockSlim m_lock = new ReaderWriterLockSlim();
        static int m_nLockTimeout = 5000;	// 5000=5秒

        public override void StopThread(bool bForce)
        {
            // this.Clear();

            base.StopThread(bForce);
        }

        // 工作线程每一轮循环的实质性工作
        public override void Worker()
        {
            int nOldCount = 0;
            List<ChargingTask> tasks = new List<ChargingTask>();
            List<ChargingTask> remove_tasks = new List<ChargingTask>();
            if (this.m_lock.TryEnterReadLock(m_nLockTimeout) == false)
                throw new LockException("锁定尝试中超时");
            try
            {
                nOldCount = this._tasks.Count;
                foreach (ChargingTask task in this._tasks)
                {
                    if (task.State == "")
                    {
                        tasks.Add(task);
                    }

#if NO
                    if (task.State == "finish"
                        && task.Action == "load_reader_info"
                        && task.ReaderBarcode != this.CurrentReaderBarcode)
                        remove_tasks.Add(task);
#endif
                }
            }
            finally
            {
                this.m_lock.ExitReadLock();
            }

            if (tasks.Count > 0)
            {
#if OLD_CHARGING_CHANNEL
                stop.OnStop += new StopEventHandler(this.DoStop);
                stop.Initial("进行一轮任务处理...");
                stop.BeginLoop();
#endif
                try
                {

                    foreach (ChargingTask task in tasks)
                    {
                        if (this.Stopped == true)
                        {
                            this.Container.SetColorList();  // 促使“任务已经暂停”显示出来
                            return;
                        }

#if OLD_CHARGING_CHANNEL
                        if (stop != null && stop.State != 0)
                        {
                            this.Stopped = true;
                            this.Container.SetColorList();  // 促使“任务已经暂停”显示出来
                            return;
                        }
#endif

                        // bool bStop = false;
                        // 执行任务
                        if (task.Action == "load_reader_info")
                        {
                            LoadReaderInfo(task);
                        }
                        else if (task.Action == "borrow"
                            || task.Action == "renew"
                            || task.Action == "verify_renew")
                        {
                            Borrow(task);
                        }
                        else if (task.Action == "return"
                            || task.Action == "verify_return"
                            || task.Action == "lost"
                            || task.Action == "verify_lost"
                            || task.Action == "inventory"
                            || task.Action == "read"
                            || task.Action == "boxing"
                            || task.Action == "transfer")
                        {
                            Return(task);
                        }

#if OLD_CHARGING_CHANNEL
                        stop.SetMessage("");
#endif
                    }
                }
                finally
                {
#if OLD_CHARGING_CHANNEL
                    stop.EndLoop();
                    stop.OnStop -= new StopEventHandler(this.DoStop);
                    stop.Initial("");
#endif
                }
            }

            //bool bChanged = false;
            if (remove_tasks.Count > 0)
            {
                if (this.m_lock.TryEnterWriteLock(m_nLockTimeout) == false)
                    throw new LockException("锁定尝试中超时");
                try
                {
                    //if (this._tasks.Count != nOldCount)
                    //    bChanged = true;

                    foreach (ChargingTask task in remove_tasks)
                    {
                        RemoveTask(task, false);
                    }
                }
                finally
                {
                    this.m_lock.ExitWriteLock();
                }
            }
            /*
            if (bChanged == true)
                this.Activate();
             * */
        }

        // 获得可以发送给服务器的证条码号字符串
        // 去掉前面的 ~
        static string GetRequestPatronBarcode(string strText)
        {
            if (string.IsNullOrEmpty(strText) == true)
                return "";
            if (strText[0] == '~')
                return strText.Substring(1);

            return strText;
        }

        // 将字符串中的宏 %datadir% 替换为实际的值
        string ReplaceMacro(string strText)
        {
            strText = strText.Replace("%mappeddir%", PathUtil.MergePath(Program.MainForm.DataDir, "servermapped"));
            return strText.Replace("%datadir%", Program.MainForm.DataDir);
        }

#if OLD_CHARGING_CHANNEL
        internal void DoStop(object sender, StopEventArgs e)
        {
            if (this.Channel != null)
                this.Channel.Abort();
        }
#else
        internal void DoStop(object sender, StopEventArgs e)
        {
            if (this.Container != null)
                this.Container.DoStop(sender, e);
        }
#endif

        LibraryChannel GetChannel()
        {
            return this.Container.GetChannel();
        }

        void ReturnChannel(LibraryChannel channel)
        {
            this.Container.ReturnChannel(channel);
        }

        // 装载读者信息
        // return:
        //      false   正常
        //      true    需要停止后继的需要通道的操作
        void LoadReaderInfo(ChargingTask task)
        {
            task.State = "begin";
            task.Color = "purple";    // "light";
            this.Container.DisplayTask("refresh", task);
            this.Container.SetColorList();

#if OLD_CHARGING_CHANNEL
            stop.SetMessage("装入读者信息 " + task.ReaderBarcode + "...");
#endif
            string strError = "";

            if (this.Container.IsCardMode == true)
                this.Container.SetReaderCardString("");
            else
                this.Container.SetReaderHtmlString("(空)");

            string strStyle = this.Container.PatronRenderFormat;
            if (this.Container.SpeakPatronName == true)
                strStyle += ",summary";
            {
                strStyle += ",xml";
                if (StringUtil.CompareVersion(Program.MainForm.ServerVersion, "2.25") >= 0)
                    strStyle += ":noborrowhistory";
            }
#if NO
            if (this.VoiceName == true)
                strStyle += ",summary";
#endif

#if OLD_CHARGING_CHANNEL
            stop.SetMessage("正在装入读者记录 " + task.ReaderBarcode + " ...");
#endif

            string[] results = null;
            byte[] baTimestamp = null;
            string strRecPath = "";

            long lRet = 0;
            {
                LibraryChannel channel = this.GetChannel();
                try
                {
#if OLD_CHARGING_CHANNEL
                lRet = this.Channel.GetReaderInfo(
                    stop,
                    GetRequestPatronBarcode(task.ReaderBarcode),
                    strStyle,   // this.RenderFormat, // "html",
                    out results,
                    out strRecPath,
                    out baTimestamp,
                    out strError);
#else
                    lRet = channel.GetReaderInfo(
        null,
        GetRequestPatronBarcode(task.ReaderBarcode),
        strStyle,   // this.RenderFormat, // "html",
        out results,
        out strRecPath,
        out baTimestamp,
        out strError);
#endif
                }
                finally
                {
                    this.ReturnChannel(channel);
                }
            }

            task.ErrorInfo = strError;

            if (lRet == 0)
            {
                if (StringUtil.IsIdcardNumber(task.ReaderBarcode) == true)
                    task.ErrorInfo = ("证条码号(或身份证号)为 '" + task.ReaderBarcode + "' 的读者记录没有找到 ...");
                else
                    task.ErrorInfo = ("证条码号为 '" + task.ReaderBarcode + "' 的读者记录没有找到 ...");

                goto ERROR1;   // not found
            }
            if (lRet == -1)
                goto ERROR1;

            if (results == null || results.Length == 0)
            {
                strError = "返回的results不正常。";
                goto ERROR1;
            }
            string strResult = "";
            strResult = results[0];
            string strReaderXml = results[results.Length - 1];

            if (lRet > 1)
            {
                // return:
                //      -1  error
                //      0   放弃
                //      1   成功
                int nRet = this.Container.SelectOnePatron(lRet,
                    strRecPath,
                    out string strBarcode,
                    out strResult,
                    out strError);
                if (nRet == -1)
                {
                    task.ErrorInfo = strError;
                    goto ERROR1;
                }
                if (nRet == 0)
                {
                    strError = "放弃装入读者记录";
                    task.ErrorInfo = strError;
                    goto ERROR1;
                }

                if (task.ReaderBarcode != strBarcode)
                {
                    task.ReaderBarcode = strBarcode;

                    // TODO: 此时 task.ReaderXml 需要另行获得
                    strReaderXml = "";

                    strStyle = "xml";
                    if (StringUtil.CompareVersion(Program.MainForm.ServerVersion, "2.25") >= 0)
                        strStyle += ":noborrowhistory";
                    results = null;

                    {
                        LibraryChannel channel = this.GetChannel();
                        try
                        {
#if OLD_CHARGING_CHANNEL
                        lRet = this.Channel.GetReaderInfo(
    stop,
    strBarcode,
    strStyle,   // this.RenderFormat, // "html",
    out results,
    out strRecPath,
    out baTimestamp,
    out strError);
#else
                            lRet = channel.GetReaderInfo(
        null,
        strBarcode,
        strStyle,   // this.RenderFormat, // "html",
        out results,
        out strRecPath,
        out baTimestamp,
        out strError);
#endif
                        }
                        finally
                        {
                            this.ReturnChannel(channel);
                        }
                    }
                    if (lRet == 1 && results != null && results.Length >= 1)
                        strReaderXml = results[0];
                }
            }
            else
            {
                // 检查用户输入的 barcode 是否和读者记录里面的 barcode 吻合
            }

            task.ReaderXml = strReaderXml;

            if (string.IsNullOrEmpty(strReaderXml) == false)
            {
                task.ReaderName = Global.GetReaderSummary(strReaderXml);
            }

            if (this.Container.IsCardMode == true)
                this.Container.SetReaderCardString(strReaderXml);
            else
                this.Container.SetReaderHtmlString(ReplaceMacro(strResult));

            // 如果切换了读者
            // if (this.CurrentReaderBarcode != task.ReaderBarcode)
            {
                // 删除除了本任务以外的其他任务
                // TODO: 需要检查这些任务中是否有尚未完成的
                List<ChargingTask> tasks = new List<ChargingTask>();
                tasks.AddRange(this._tasks);
                tasks.Remove(task);

                int nCount = CountNotFinishTasks(tasks);
                if (nCount > 0)
                {
#if NO
                        if (this.Container.AskContinue("当前有 " + nCount.ToString()+ " 个任务尚未完成。\r\n\r\n是否清除这些任务并继续?") == DialogResult.Cancel)
                        {
                            strError = "装入读者记录的操作被中断";
                            task.ErrorInfo = strError;
                            goto ERROR1;
                        }
#endif
                    this.Container.DisplayReaderSummary(task, "前面读者有 " + nCount.ToString() + " 个任务尚未完成，或有提示需要进一步处理。\r\n点击此处查看摘要信息");
                }
                else
                    this.Container.ClearTaskList(tasks);

                // 真正从 _tasks 里面删除
                ClearTasks(tasks);  // 2019/9/3
            }

            this.CurrentReaderBarcode = task.ReaderBarcode; // 会自动显示出来

            if (this.Container.SpeakPatronName == true && results.Length >= 2)
            {
                string strName = results[1];
                Program.MainForm.Speak(strName);
            }

            // this.m_strCurrentBarcode = strBarcode;
            task.State = "finish";
            task.Color = "";
            // 兑现显示
            this.Container.DisplayTask("refresh", task);
            this.Container.SetColorList();
            // this.Container.CurrentReaderBarcodeChanged(this.CurrentReaderBarcode);  // 通知 读者记录已经成功装载
#if NO
                if (stop != null && stop.State != 0)
                    return true;
                return false;
#endif
            return;
        ERROR1:
            task.State = "error";
            task.Color = "red";
            this.CurrentReaderBarcode = ""; // 及时清除上下文,避免后面错误借到先前的读者名下
            if (this.Container.IsCardMode == true)
                this.Container.SetReaderCardString(task.WholeErrorInfo);
            else
                this.Container.SetReaderTextString(task.WholeErrorInfo);
            // 兑现显示
            this.Container.DisplayTask("refresh", task);
            this.Container.SetColorList();
            this.Stopped = true;  // 全部任务停止处理。这是因为装载读者的操作比较重要，是后继操作的前提
#if NO
                if (stop != null && stop.State != 0)
                    return true;
                return false;
#endif
            return;
#if NO
            }
            finally
            {

                stop.EndLoop();
                stop.OnStop -= new StopEventHandler(this.DoStop);
                stop.Initial("");
            }
#endif
        }

        // 统计若干任务中，有多少个处于未完成的状态。和黄色 红色状态
        static int CountNotFinishTasks(List<ChargingTask> tasks)
        {
            int nCount = 0;
            foreach (ChargingTask task in tasks)
            {
                if (task.State == "begin" || task.Color == "black"
                    || task.Color == "yellow" || task.Color == "red")
                    nCount++;
            }

            return nCount;
        }

        string GetPostFix()
        {
            if (StringUtil.CompareVersion(Program.MainForm.ServerVersion, "2.24") >= 0)
                return ":noborrowhistory";
            return "";
        }

        // 借书
        void Borrow(ChargingTask task)
        {
            DateTime start_time = DateTime.Now;

            List<DateTime> times = new List<DateTime>();
            times.Add(start_time);

            task.State = "begin";
            task.Color = "purple";  //  "light";
            this.Container.DisplayTask("refresh", task);
            this.Container.SetColorList();

            string strOperText = task.ReaderBarcode + " 借 " + task.ItemBarcode;

#if OLD_CHARGING_CHANNEL
            stop.SetMessage(strOperText + " ...");
#endif
            string strError = "";

            string strReaderRecord = "";
            string strConfirmItemRecPath = null;

            bool bRenew = false;
            if (task.Action == "renew" || task.Action == "verify_renew")
                bRenew = true;

            if (task.Action == "renew")
                task.ReaderBarcode = "";
            else
            {
                if (string.IsNullOrEmpty(task.ReaderBarcode) == true)
                {
                    strError = "证条码号为空，无法进行借书操作";
                    task.ErrorInfo = strError;
                    goto ERROR1;
                }
            }

            // REDO:
            string[] aDupPath = null;
            string[] item_records = null;
            string[] reader_records = null;
            string[] biblio_records = null;
            string strOutputReaderBarcode = "";

            BorrowInfo borrow_info = null;

            // item返回的格式
            string strItemReturnFormats = "";

            if (Program.MainForm.ChargingNeedReturnItemXml == true)
            {
                if (String.IsNullOrEmpty(strItemReturnFormats) == false)
                    strItemReturnFormats += ",";
                strItemReturnFormats += "xml" + GetPostFix();
            }

            // biblio返回的格式
            string strBiblioReturnFormats = "";

            // 读者返回格式
            string strReaderFormatList = "";
            bool bName = false; // 是否直接取得读者姓名，而不要获得读者 XML
            if (StringUtil.CompareVersion(Program.MainForm.ServerVersion, "2.24") >= 0)
            {
                strReaderFormatList = this.Container.PatronRenderFormat + ",summary";
                bName = true;
            }
            else
                strReaderFormatList = this.Container.PatronRenderFormat + ",xml" + GetPostFix();

            string strStyle = "reader";

            if (Program.MainForm.ChargingNeedReturnItemXml)
                strStyle += ",item";

            // 2021/8/25
            if (string.IsNullOrEmpty(task.Parameters) == false)
                strStyle += "," + task.Parameters;

            //if (this.Container.MainForm.TestMode == true)
            //    strStyle += ",testmode";
            times.Add(DateTime.Now);



            if (string.IsNullOrEmpty(task.ItemBarcodeEasType) == false
                && string.IsNullOrEmpty(RfidManager.Url))    // this.Container._rfidChannel == null
            {
                task.ErrorInfo = "尚未连接 RFID 设备，无法进行 RFID 标签物品的流通操作";
                goto ERROR1;
            }

            string additional = this.Container.GetOperTimeParamString();
            if (string.IsNullOrEmpty(additional) == false)
                strStyle += ",operTime:" + StringUtil.EscapeString(additional, ",:");

            long lRet = 0;
            LibraryChannel channel = this.GetChannel();
            try
            {
#if OLD_CHARGING_CHANNEL
                                lRet = Channel.Borrow(
     stop,
     bRenew,
     task.ReaderBarcode,
     task.ItemBarcode,
     strConfirmItemRecPath,
     false,
     null,   // this.OneReaderItemBarcodes,
     strStyle,
     strItemReturnFormats,
     out item_records,
     strReaderFormatList,    // this.Container.PatronRenderFormat + ",xml" + GetPostFix(),
     out reader_records,
     strBiblioReturnFormats,
     out biblio_records,
     out aDupPath,
     out strOutputReaderBarcode,
     out borrow_info,
     out strError);
#else
                lRet = channel.Borrow(
     null,
     bRenew,
     task.ReaderBarcode,
     task.ItemBarcode,
     strConfirmItemRecPath,
     false,
     null,   // this.OneReaderItemBarcodes,
     strStyle,
     strItemReturnFormats,
     out item_records,
     strReaderFormatList,    // this.Container.PatronRenderFormat + ",xml" + GetPostFix(),
     out reader_records,
     strBiblioReturnFormats,
     out biblio_records,
     out aDupPath,
     out strOutputReaderBarcode,
     out borrow_info,
     out strError);
#endif
            }
            finally
            {
                this.ReturnChannel(channel);
            }
            task.ErrorInfo = strError;

            if (lRet != -1)
            {
                // 修改 EAS
                if (string.IsNullOrEmpty(task.ItemBarcodeEasType) == false)
                {
                    if (SetEAS(task,
                        false,
                        Program.MainForm.RfidTestBorrowEAS == false ? null : "测试触发修改 EAS 报错(借书操作末段)",   // 测试触发报错
                        out string changed_uid,
                        out strError) == false)
                    {
                        // TODO: 要 undo 刚才进行的操作
                        // lRet = -1;
                        lRet = 1;   // 相当于黄色状态 // 红色状态，但填充 ItemSummary
                        task.EasErrorInfo += strError;
                        // 添加一个事件函数，以便后面 EasError 被清除后，任务能变成绿色状态
                        task.EasErrorCleared -= Task_EasErrorCleared;
                        task.EasErrorCleared += Task_EasErrorCleared;
                    }

                    // TODO: 需要压制一次 UID 引起的借还操作(changed_uid)
                }
            }

            times.Add(DateTime.Now);

            if (reader_records != null && reader_records.Length > 0)
                strReaderRecord = reader_records[0];

            // 刷新读者信息
            if (this.Container.IsCardMode == true)
            {
                if (String.IsNullOrEmpty(strReaderRecord) == false)
                    this.Container.SetReaderCardString(strReaderRecord);
            }
            else
            {
                if (String.IsNullOrEmpty(strReaderRecord) == false)
                    this.Container.SetReaderHtmlString(ReplaceMacro(strReaderRecord));
            }

            string strItemXml = "";
            if (Program.MainForm.ChargingNeedReturnItemXml == true
                && item_records != null)
            {
                Debug.Assert(item_records != null, "");

                if (item_records.Length > 0)
                {
                    // xml总是在最后一个
                    strItemXml = item_records[item_records.Length - 1];
                }
            }

            if (lRet == -1)
                goto ERROR1;

            DateTime end_time = DateTime.Now;

            string strReaderSummary = "";
            if (reader_records != null && reader_records.Length > 1)
            {
                if (bName == false)
                    strReaderSummary = Global.GetReaderSummary(reader_records[1]);
                else
                    strReaderSummary = reader_records[1];
            }

#if NO
                string strBiblioSummary = "";
                if (biblio_records != null && biblio_records.Length > 1)
                    strBiblioSummary = biblio_records[1];
#endif

            task.ReaderName = strReaderSummary;
            // task.ItemSummary = strBiblioSummary;
#if NO
            this.Container.AsynFillItemSummary(task.ItemBarcode,
                strConfirmItemRecPath,
                task);
#endif
            this.Container.AddItemSummaryTask(// task.ItemBarcode,
                string.IsNullOrEmpty(borrow_info?.ItemBarcode) ? task.ItemBarcode : borrow_info?.ItemBarcode,
                strConfirmItemRecPath,
                task);

            Program.MainForm.OperHistory.BorrowAsync(
this.Container,
bRenew,
strOutputReaderBarcode,
task.ItemBarcode,
strConfirmItemRecPath,
strReaderSummary,
strItemXml,
borrow_info,
start_time,
end_time);

            /*
            lRet = 1;
            task.ErrorInfo = "asdf a asdf asdf as df asdf as f a df asdf a sdf a sdf asd f asdf a sdf as df";
            */

            if (lRet == 2)
            {
                // 2019/8/28
                // 红色状态
                task.Color = "red";
            }
            else if (lRet == 1)
            {
                // 黄色状态
                task.Color = "yellow";
            }
            else
            {
                // 绿色状态
                task.Color = "green";
            }

            // this.m_strCurrentBarcode = strBarcode;
            task.State = "finish";
            // 兑现显示
            this.Container.DisplayTask("refresh_and_visible", task);
            this.Container.SetColorList();
#if NO
                if (stop != null && stop.State != 0)
                    return true;
                return false;
#endif

            {
                BorrowCompleteEventArgs e1 = new BorrowCompleteEventArgs();
                e1.Action = task.Action;
                e1.ItemBarcode = task.ItemBarcode;
                e1.ReaderBarcode = strOutputReaderBarcode;
                this.Container.TriggerBorrowComplete(e1);
            }

            times.Add(DateTime.Now);
            LogOperTime("borrow", times, strOperText);
            return;
        ERROR1:
            task.State = "error";
            task.Color = "red";
            // this.Container.SetReaderRenderString(strError);
            // 兑现显示
            this.Container.DisplayTask("refresh_and_visible", task);
            this.Container.SetColorList();
#if NO
                if (stop != null && stop.State != 0)
                    return true;
                return false;
#endif
            return;

#if NO
            }
            finally
            {
                stop.EndLoop();
                stop.OnStop -= new StopEventHandler(this.DoStop);
                stop.Initial("");
            }
#endif

        }

        // 2023/11/16
        private void Task_EasErrorCleared(object sender, ChargingTask.EasErrorClearedEventArgs e)
        {
            var task = sender as ChargingTask;
            // 当 EasErrorInfo 被清理时。将黄色状态修改为绿色状态
            if (task.Color == "yellow")
            {
                task.Color = "green";
                this.Container.DisplayTask("refresh_and_visible", task);
                this.Container.SetColorList();
            }
        }

        static void Sound(CancellationToken token)
        {
            while (token.IsCancellationRequested == false)
            {
                System.Console.Beep(440, 10);
            }
        }

        static int[] tones = new int[] { 523, 659, 783 };
        /*
         *  C4: 261 330 392
            C5: 523 659 783
         * */
        public static void Sound(int tone)
        {
            Task.Run(() =>
            {
                if (tone == -1)
                {
                    for (int i = 0; i < 2; i++)
                        System.Console.Beep(1147, 1000);
                }
                else
                    System.Console.Beep(tones[tone], tone == 1 ? 200 : 500);
            });
        }

        static string ToLower(string text)
        {
            if (text == null)
                return "";
            return text.ToLower();
        }

        // TODO: 增加测试机制，允许制造修改 EAS 失败的场景
        bool PreSetEAS(ChargingTask task,
    bool enable,
    out bool old_state, // 注: old_state 暂未兑现
    out string changed_uid,
    out string strError)
        {
            strError = "";
            old_state = false;
            changed_uid = null;
            try
            {
                string tag_name = ToLower(task.ItemBarcodeEasType) + ":" + task.ItemBarcode;
                // 前置情况下，要小心检查原来标签的 EAS，如果没有必要修改就不要修改
                uint antenna_id = 0;
                string reader_name = "";

#if REMOVED
                {
                    var get_result = this.Container.GetEAS("*", tag_name);

                    if (get_result == null)
                        this.Container.WriteErrorLog("get_result == null");
                    else
                        this.Container.WriteErrorLog($"get_result == {get_result.ToString()}");

                    if (get_result.Value == -1)
                    {
                        Sound(-1);

                        strError = $"获得 RFID 标签 EAS 标志位时出错: {get_result.ErrorInfo}";
                        return false;
                    }

                    old_state = (get_result.Value == 1);

                    antenna_id = get_result.AntennaID;
                    reader_name = get_result.ReaderName;
                }

                // 如果修改前已经是这个值就不修改了
                if (old_state == enable)
                    return true;
#endif

                // 2023/11/1
                if (string.IsNullOrEmpty(task.ItemBarcode) == false)
                {
                    // Debug.WriteLine("PreSetEAS() call SetLastTime()");
                    this.Container.SetLastTime(
                        task.ItemBarcodeEasType,
                        task.ItemBarcode,
                        DateTime.Now);
                }

                {
                    SetEasResult result = null;
                    // 修改 EAS。带有重试功能 // 2019/9/2
                    for (int i = 0; i < 2; i++)
                    {
                        // return result.Value:
                        //      -1  出错
                        //      0   没有找到指定的标签
                        //      1   找到，并成功修改 EAS
                        result = RfidManager.SetEAS(string.IsNullOrEmpty(reader_name) ? "*" : reader_name,
                            tag_name,
                            antenna_id,
                            enable);
                        if (result.Value == 1)
                            break;
                    }

                    changed_uid = result.ChangedUID;
                    var old_uid = result.OldUID;

                    // 2023/11/21
                    // 修改 _piiTable 中的有关条目
                    if (string.IsNullOrEmpty(old_uid) == false
                        && string.IsNullOrEmpty(result.ChangedUID) == false)
                        this.Container.UpdateUID(old_uid, result.ChangedUID);

#if REMOVED
                    if (string.IsNullOrEmpty(old_uid) == false)
                        RfidTagList.ClearTagTable(old_uid);
                    else
                    {
                        // 2023/11/21
                        // 保险
                        RfidTagList.ClearTagTable("");
                        FillTagList();
                    }
#endif

                    // 2023/11/21
                    // 精确修改 TagInfo
                    if (string.IsNullOrEmpty(old_uid) == false
                        && result.Value == 1)
                    {
                        if (string.IsNullOrEmpty(changed_uid) == false)
                            ChangeUID(old_uid, changed_uid);
                        else
                            ChangeUID(old_uid, enable ? "on" : "off");
                    }

                    // 注: 从此以后 UHF 标签也立即刷新了，修改以后的 EPC 发生了变化

                    // testing
                    // NormalResult result = new NormalResult { Value = -1, ErrorInfo = "testing" };

                    if (result.Value != 1)
                    {
                        Sound(-1);

                        strError = $"前置修改 RFID 标签 EAS 标志位时出错: {result.ErrorInfo}";
                        return false;
                    }
                }

                Sound(2);
                return true;
            }
            catch (Exception ex)
            {
                var text = $"前置修改 RFID 标签 EAS 标志位时出现异常: {ExceptionUtil.GetDebugText(ex)}";
                this.Container.WriteErrorLog(text);
                strError = $"前置修改 RFID 标签 EAS 标志位时出现异常: {ex.Message} (已写入错误日志)";
                return false;
            }
        }

        // parameters:
        //      old_uid 原先的 UID
        //      changed_uid 修改后的 UID (对于 UHF 标签)
        //                  "on" "off" 之一(对于 HF 标签)
        public static void ChangeUID(string old_uid, string changed_uid)
        {
            BaseChannel<IRfid> channel = RfidManager.GetChannel();
            try
            {
                RfidTagList.ChangeUID(channel, old_uid, changed_uid);
            }
            finally
            {
                RfidManager.ReturnChannel(channel);
            }
        }


        public static void FillTagList()
        {
            BaseChannel<IRfid> channel = RfidManager.GetChannel();
            try
            {
                RfidTagList.FillTagInfo(channel);
            }
            finally
            {
                RfidManager.ReturnChannel(channel);
            }
        }

        bool SetEAS(ChargingTask task,
            bool enable,
            // bool preprocess,
            string simulate_error,
            out string changed_uid,
            out string strError)
        {
            string prefix = "";
            strError = "";
            changed_uid = null;
            try
            {
                SetEasResult result = null;
                /*
                if (preprocess == true)
                {
                    prefix = "前置";

                    // 前置情况下，要小心检查原来标签的 EAS，如果没有必要修改就不要修改

                    result = RfidManager.SetEAS("*",
                    task.ItemBarcodeEasType.ToLower() + ":" + task.ItemBarcode,
                    enable);
                    TagList.ClearTagTable("");
                }
                else
                */
                {
                    // 2023/11/1
                    if (string.IsNullOrEmpty(task.ItemBarcode) == false)
                    {
                        // Debug.WriteLine("SetEAS() call SetLastTime()");
                        this.Container.SetLastTime(
                            task.ItemBarcodeEasType,
                            task.ItemBarcode,
                            DateTime.Now);
                    }

                    result = this.Container.SetEAS(
                        task,
                        "*",
                        ToLower(task.ItemBarcodeEasType) + ":" + task.ItemBarcode,
                        enable,
                        simulate_error);
                    changed_uid = result.ChangedUID;
                }

                // testing
                // NormalResult result = new NormalResult { Value = -1, ErrorInfo = "testing" };

                if (result.Value != 1)
                {
                    Sound(-1);

                    strError = $"{prefix}修改 RFID 标签 EAS 标志位时出错: {result.ErrorInfo}";
                    return false;
                }

                Sound(2);
                return true;
            }
            catch (Exception ex)
            {
                strError = $"{prefix}修改 RFID 标签 EAS 标志位时出现异常: {ex.Message}";
                return false;
            }
        }

#if NO
        bool SetEAS(ChargingTask task, bool enable, out string strError)
        {
            strError = "";
            try
            {
                /*
                NormalResult result = this.Container._rfidChannel.Object.SetEAS("*",
        task.ItemBarcodeEasType.ToLower() + ":" + task.ItemBarcode,
        enable);
        */

                NormalResult result = RfidManager.SetEAS("*",
    task.ItemBarcodeEasType.ToLower() + ":" + task.ItemBarcode,
    enable);
                TagList.ClearTagTable("");

                // testing
                // NormalResult result = new NormalResult { Value = -1, ErrorInfo = "testing" };

                if (result.Value != 1)
                {
                    Sound(-1);

                    strError = "修改 RFID 标签 EAS 标志位时出错: " + result.ErrorInfo;

                    bool eas_fixed = false;
                    string text = strError;
                    this.Container.Invoke((Action)(() =>
                    {
                        var oldPause = this.Container.PauseRfid;
                        this.Container.PauseRfid = true;
                        try
                        {
                            // TODO: 对话框打开之后，QuickCharingForm 要暂停接收标签信息
                            using (RfidToolForm dlg = new RfidToolForm())
                            {
                                RfidManager.GetState("clearCache");
                                dlg.MessageText = text + "\r\n请利用本窗口修正 EAS";
                                dlg.Mode = "auto_fix_eas_and_close";
                                dlg.SelectedID = task.ItemBarcodeEasType.ToLower() + ":" + task.ItemBarcode;
                                dlg.ProtocolFilter = InventoryInfo.ISO15693;
                                dlg.ShowDialog(this.Container);
                                eas_fixed = dlg.EasFixed;
                                // 2019/1/23
                                // TODO: 似乎也可以让 RfidToolForm 来负责恢复它打开前的 sendkey 状态
                                // this.Container.OpenRfidCapture(true);
                            }
                        }
                        finally
                        {
                            this.Container.PauseRfid = oldPause;
                        }
                    }));

                    if (eas_fixed == true)
                        return true;

                    return false;
                }

                Sound(2);
                return true;
            }
            catch (Exception ex)
            {
                strError = "修改 RFID 标签 EAS 标志位时出错: " + ex.Message;
                return false;
            }
        }

#endif

        // parameters:
        //      times   时间值数组。依次是 总开始时间, API 开始时间, API 结束时间, 总结束时间
        void LogOperTime(string strAPI, List<DateTime> times, string strDesc)
        {
            Debug.Assert(times.Count == 4, "");

            StringBuilder text = new StringBuilder();
            TimeSpan total_delta = times[3] - times[0];
            TimeSpan api_delta = times[2] - times[1];
            if (api_delta.TotalSeconds > 2)
                text.Append("*** ");
            text.Append("API " + strAPI + " 耗时 " + api_delta.TotalSeconds.ToString() + " 秒  " + strDesc + " (" + times[1].ToLongTimeString() + "-" + times[2].ToLongTimeString() + "); "
                + " 总耗时 " + total_delta.TotalSeconds.ToString() + " 秒(" + times[0].ToLongTimeString() + "-" + times[3].ToLongTimeString() + ") ");
            this.Container.WriteErrorLog(text.ToString());
        }

        // 还书
        void Return(ChargingTask task)
        {
            DateTime start_time = DateTime.Now;

            List<DateTime> times = new List<DateTime>();
            times.Add(start_time);

            task.State = "begin";
            task.Color = "purple";  //  "light";
            this.Container.DisplayTask("refresh", task);
            this.Container.SetColorList();

            string strOperText = "";

            if (task.Action == "inventory")
                strOperText = task.ReaderBarcode + " 盘点 " + task.ItemBarcode;
            else if (task.Action == "transfer")
                strOperText = task.ReaderBarcode + " 移交 " + task.ItemBarcode;
            else if (task.Action == "read")
            {
                if (StringUtil.CompareVersion(Program.MainForm.ServerVersion, "2.68") < 0)
                {
                    task.ErrorInfo = "操作未能进行。“读过”功能要求 dp2library 版本在 2.68 或以上";
                    goto ERROR1;
                }

                strOperText = task.ReaderBarcode + " 读过 " + task.ItemBarcode;
            }
            else if (task.Action == "boxing")
            {
                if (StringUtil.CompareVersion(Program.MainForm.ServerVersion, "2.92") < 0)
                {
                    task.ErrorInfo = "操作未能进行。“配书”功能要求 dp2library 版本在 2.92 或以上";
                    goto ERROR1;
                }

                strOperText = task.ReaderBarcode + " 配书 " + task.ItemBarcode;
            }
            else
                strOperText = task.ReaderBarcode + " 还 " + task.ItemBarcode;

#if OLD_CHARGING_CHANNEL
            stop.SetMessage(strOperText + " ...");
#endif

            string strError = "";

            string strReaderRecord = "";
            string strConfirmItemRecPath = null;

            string strAction = task.Action;
            string strReaderBarcode = task.ReaderBarcode;
            if (task.Action == "verify_return")
            {
                if (string.IsNullOrEmpty(strReaderBarcode) == true)
                {
                    strError = "尚未输入读者证条码号";
                    task.ErrorInfo = strError;
                    goto ERROR1;
                }
                strAction = "return";
            }
            else if (task.Action == "verify_lost")
            {
                if (string.IsNullOrEmpty(strReaderBarcode) == true)
                {
                    strError = "尚未输入读者证条码号";
                    task.ErrorInfo = strError;
                    goto ERROR1;
                }
                strAction = "lost";
            }
            else if (task.Action == "inventory")
            {
                if (string.IsNullOrEmpty(strReaderBarcode) == true)
                {
                    strError = "尚未设定批次号";
                    task.ErrorInfo = strError;
                    goto ERROR1;
                }
                strAction = "inventory";
            }
            else if (task.Action == "transfer")
            {
                // testing
                // strReaderBarcode = "test_bachno";
                /*
                if (string.IsNullOrEmpty(strReaderBarcode) == true)
                {
                    strError = "尚未设定批次号";
                    task.ErrorInfo = strError;
                    goto ERROR1;
                }
                */
                strAction = "transfer";
            }
            else if (task.Action == "read")
            {
                if (string.IsNullOrEmpty(strReaderBarcode) == true)
                {
                    strError = "尚未输入读者证条码号";
                    task.ErrorInfo = strError;
                    goto ERROR1;
                }
                strAction = "read";
            }
            else
            {
                strReaderBarcode = "";
            }

            //REDO:
            string[] aDupPath = null;
            string[] item_records = null;
            string[] reader_records = null;
            string[] biblio_records = null;
            string strOutputReaderBarcode = "";

            ReturnInfo return_info = null;

            // item返回的格式
            string strItemReturnFormats = "";

            if (Program.MainForm.ChargingNeedReturnItemXml == true)
            {
                if (String.IsNullOrEmpty(strItemReturnFormats) == false)
                    strItemReturnFormats += ",";
                strItemReturnFormats += "xml" + GetPostFix();
            }

            // biblio返回的格式
            string strBiblioReturnFormats = "";

            // 读者返回格式
            string strReaderFormatList = "";
            bool bName = false; // 是否直接取得读者姓名，而不要获得读者 XML
            if (StringUtil.CompareVersion(Program.MainForm.ServerVersion, "2.24") >= 0)
            {
                strReaderFormatList = this.Container.PatronRenderFormat + ",summary";
                bName = true;
            }
            else
                strReaderFormatList = this.Container.PatronRenderFormat + ",xml" + GetPostFix();

            string strStyle = "reader";

            if (Program.MainForm.ChargingNeedReturnItemXml)
                strStyle += ",item";

            if (string.IsNullOrEmpty(task.Parameters) == false)
                strStyle += "," + task.Parameters;

            // testing
            // if (strAction == "transfer")
            //    strStyle += ",location:新馆藏地";
#if NO
            if (strAction == "inventory")
            {
                strItemReturnFormats = "xml";
                strStyle += ",item";
            }
#endif

            //if (this.Container.MainForm.TestMode == true)
            //    strStyle += ",testmode";

            times.Add(DateTime.Now);

            if (string.IsNullOrEmpty(task.ItemBarcodeEasType) == false
    && string.IsNullOrEmpty(RfidManager.Url)) // this.Container._rfidChannel == null
            {
                task.ErrorInfo = "尚未连接 RFID 设备，无法进行 RFID 标签物品的流通操作";
                goto ERROR1;
            }

            // 还书操作在调用 Return() API 之前预先修改一次 EAS 为 On
            bool eas_changed = false;
            {
                // 修改 EAS
                if (string.IsNullOrEmpty(task.ItemBarcodeEasType) == false)
                {
                    // 测试触发报错
                    if (Program.MainForm.RfidTestReturnPreEAS)
                    {
                        task.EasErrorInfo = "测试触发修改 EAS 报错(还书操作前段)";
                        task.AppendErrorInfo("还书操作失败");   // EAS 不需要额外修正
                        goto ERROR1;
                    }
                    else
                    {
                        if (PreSetEAS(task,
                            true,
                            out bool old_eas,
                            out string changed_uid,
                            out strError) == false)
                        {
                            // task.ErrorInfo = $"{strError}\r\n还书操作没有执行，EAS 不需要修正";
                            task.ErrorInfo = $"{strError}\r\n还书操作失败";   // EAS 不需要额外修正
                            goto ERROR1;
                        }

                        // 2023/11/15
                        if (string.IsNullOrEmpty(changed_uid) == false)
                        {
                            if (task.ItemBarcodeEasType == "pii")
                                this.Container.UpdateUidByPII(task.ItemBarcode, changed_uid);
                            if (task.ItemBarcodeEasType == "uid")
                                this.Container.UpdateUID(task.ItemBarcode, changed_uid);
                        }

                        // TODO: 需要压制一次 UID 引起的借还操作(changed_uid)

                        if (old_eas == false)
                            eas_changed = true;
                    }
                }
            }

            string additional = "";
            if (this.Container.TestSync == true)
                additional = this.Container.GetOperTimeParamString();
            else
            {
                // 不是测试状态也带有 operTime 子参数
                // additional = DateTimeUtil.Rfc1123DateTimeStringEx(DateTime.Now);

                // 让服务器自己填写时间 2021/8/26
            }

            if (string.IsNullOrEmpty(additional) == false)
                strStyle += ",operTime:" + StringUtil.EscapeString(additional, ",:");

            long lRet = 0;

            LibraryChannel channel = this.GetChannel();
            try
            {
                // 测试触发报错
                if (Program.MainForm.RfidTestReturnAPI)
                {
                    lRet = -1;
                    strError = "测试触发还书 API 报错(还书操作中段)，注意此时还书并没有成功";
                }

#if OLD_CHARGING_CHANNEL
                                lRet = Channel.Return(
                     stop,
                     strAction,
                     strReaderBarcode,
                     task.ItemBarcode,
                     strConfirmItemRecPath,
                     false,
                     strStyle,   // this.NoBiblioAndItemInfo == false ? "reader,item,biblio" : "reader",
                     strItemReturnFormats,
                     out item_records,
                     strReaderFormatList,    // this.Container.PatronRenderFormat + ",xml" + GetPostFix(), // "html",
                     out reader_records,
                     strBiblioReturnFormats,
                     out biblio_records,
                     out aDupPath,
                     out strOutputReaderBarcode,
                     out return_info,
                     out strError);
#else
                else
                {
                    lRet = channel.Return(
                         null,
                         strAction,
                         strReaderBarcode,
                         task.ItemBarcode,
                         strConfirmItemRecPath,
                         false,
                         strStyle,   // "reader,item,biblio",    //// this.NoBiblioAndItemInfo == false ? "reader,item,biblio" : "reader",
                         strItemReturnFormats,
                         out item_records,
                         strReaderFormatList,    // this.Container.PatronRenderFormat + ",xml" + GetPostFix(), // "html",
                         out reader_records,
                         strBiblioReturnFormats,
                         out biblio_records,
                         out aDupPath,
                         out strOutputReaderBarcode,
                         out return_info,
                         out strError);
                }
#endif
            }
            finally
            {
                this.ReturnChannel(channel);
            }
            if (lRet != 0)
                task.ErrorInfo = strError;

            // 2021/9/1
            if (return_info != null
                && string.IsNullOrEmpty(return_info.Borrower) == false)
                this.CurrentReaderBarcode = return_info?.Borrower;

            if (eas_changed == true && lRet == -1)
            {
                if (channel.ErrorCode == ErrorCode.NotBorrowed)
                {
                    Debug.Assert(eas_changed == true, "");
                    /*
                    // 此时正好顺便修正了以前此册的 EAS 问题，所以也不需要回滚了
                    // TODO: 是否需要提示一下操作者？
                    task.AppendErrorInfo($"(前置 EAS 修改顺便把以前遗留的 Off 状态修正为 On)");
                    */
                }
                else
                {
                    // Undo 早先的 EAS 修改
                    if (SetEAS(task,
                        false,
                        Program.MainForm.RfidTestReturnPostUndoEAS == false ? null : "测试触发修改 EAS 报错(还书操作末段，回滚 EAS 时)",
                        out string changed_uid,
                        out strError) == false)
                    {
                        // lRet = -1;
                        // lRet = 1;
                        task.EasErrorInfo = $"回滚 EAS 阶段: {strError}";
                    }

                    // TODO: 需要压制一次 UID 引起的借还操作(changed_uid)
                }
            }

#if NO
                if (return_info != null)
                {
                    strLocation = StringUtil.GetPureLocation(return_info.Location);
                }
#endif
            times.Add(DateTime.Now);

            if (reader_records != null && reader_records.Length > 0)
                strReaderRecord = reader_records[0];

            // 刷新读者信息
            if (this.Container.IsCardMode == true)
            {
                if (String.IsNullOrEmpty(strReaderRecord) == false)
                    this.Container.SetReaderCardString(strReaderRecord);
            }
            else
            {
                if (String.IsNullOrEmpty(strReaderRecord) == false)
                    this.Container.SetReaderHtmlString(ReplaceMacro(strReaderRecord));
            }

            string strItemXml = "";
            if ((Program.MainForm.ChargingNeedReturnItemXml == true
                || strAction == "inventory" || strAction == "transfer")
                && item_records != null)
            {
                Debug.Assert(item_records != null, "");

                if (item_records.Length > 0)
                {
                    // xml总是在最后一个
                    strItemXml = item_records[item_records.Length - 1];
                }
            }

            // 对 return_info.Location 进行观察，看看是否超过要求的范围
            if (lRet != -1
                && strAction == "inventory"
                && Container.FilterLocations != null
                && Container.FilterLocations.Count > 0)
            {
#if NO
                XmlDocument item_dom = new XmlDocument();
                try
                {
                    item_dom.LoadXml(strItemXml);
                }
                catch(Exception ex)
                {
                    strError = "strItemXml 装入 XMLDOM 时出错: " + ex.Message;
                    goto ERROR1;
                }
                string strLocation = DomUtil.GetElementText(item_dom.DocumentElement, "location");
#endif
                string strLocation = return_info?.Location;
                strLocation = StringUtil.GetPureLocation(strLocation);

                if (Container.FilterLocations.IndexOf(strLocation) == -1)
                {
                    lRet = 1;
                    task.AppendErrorInfo("册记录中的馆藏地 '" + strLocation + "' 不在当前盘点要求的范围 '" + StringUtil.MakePathList(Container.FilterLocations) + "'。请及时处理");
                }
            }

            // 修改 Parameters，增加用于显示的调拨方向
            if (lRet != -1
    && strAction == "transfer")
                task.Parameters += ",direction:" + return_info?.Location;

            if (lRet == -1)
                goto ERROR1;

            string strReaderSummary = "";
            if (reader_records != null && reader_records.Length > 1)
            {
                if (bName == false)
                    strReaderSummary = Global.GetReaderSummary(reader_records[1]);
                else
                    strReaderSummary = reader_records[1];
            }

#if NO
                string strBiblioSummary = "";
                if (biblio_records != null && biblio_records.Length > 1)
                    strBiblioSummary = biblio_records[1];
#endif

            task.ReaderName = strReaderSummary;
            // task.ItemSummary = strBiblioSummary;
#if NO
            this.Container.AsynFillItemSummary(task.ItemBarcode,
                strConfirmItemRecPath,
                task);
#endif
            this.Container.AddItemSummaryTask( // task.ItemBarcode,
                string.IsNullOrEmpty(return_info?.ItemBarcode) ? task.ItemBarcode : return_info?.ItemBarcode,
                strConfirmItemRecPath,
                task);

            if (string.IsNullOrEmpty(task.ReaderBarcode) == true)
                task.ReaderBarcode = strOutputReaderBarcode;

            DateTime end_time = DateTime.Now;

            Program.MainForm.OperHistory.ReturnAsync(
                this.Container,
                strAction,  // task.Action == "lost" || task.Action == "verify_lost",
                strOutputReaderBarcode, // this.textBox_readerBarcode.Text,
                task.ItemBarcode,
                strConfirmItemRecPath,
                strReaderSummary,
                strItemXml,
                return_info,
                start_time,
                end_time);

            if (lRet == 2)
            {
                // 2019/8/28
                // 红色状态
                task.Color = "red";
            }
            else if (lRet == 1)
            {
                // 黄色状态
                task.Color = "yellow";
            }
            else
            {
                // 绿色状态
                task.Color = "green";
            }

            // this.m_strCurrentBarcode = strBarcode;
            task.State = "finish";
            // 兑现显示
            this.Container.DisplayTask("refresh_and_visible", task);
            this.Container.SetColorList();
#if NO
                if (stop != null && stop.State != 0)
                    return true;
                return false;
#endif
            {
                BorrowCompleteEventArgs e1 = new BorrowCompleteEventArgs();
                e1.Action = task.Action;
                e1.ItemBarcode = task.ItemBarcode;
                e1.ReaderBarcode = strOutputReaderBarcode;
                this.Container.TriggerBorrowComplete(e1);
            }

            times.Add(DateTime.Now);
            LogOperTime("return", times, strOperText);
            return;

        ERROR1:
            task.State = "error";
            task.Color = "red";
            // this.Container.SetReaderRenderString(strError);
            // 兑现显示
            this.Container.DisplayTask("refresh_and_visible", task);
            this.Container.SetColorList();
#if NO
                if (stop != null && stop.State != 0)
                    return true;
                return false;
#endif
            return;
#if NO
            }
            finally
            {
                stop.EndLoop();
                stop.OnStop -= new StopEventHandler(this.DoStop);
                stop.Initial("");
            }
#endif
        }


        public void Close(bool bForce = true)
        {
#if OLD_CHARGING_CHANNEL
            if (stop != null)
                stop.DoStop();
#endif
            this.StopThread(bForce);
        }

        public void Clear()
        {
            if (this.m_lock.TryEnterWriteLock(m_nLockTimeout) == false)   // m_nLockTimeout
                throw new LockException("锁定尝试中超时");
            try
            {
                this._tasks.Clear();
            }
            finally
            {
                this.m_lock.ExitWriteLock();
            }
        }

        // 清除指定的那些任务
        public void ClearTasks(List<ChargingTask> tasks)
        {
            if (this.m_lock.TryEnterWriteLock(m_nLockTimeout) == false)   // m_nLockTimeout
                throw new LockException("锁定尝试中超时");
            try
            {
                foreach (ChargingTask task in tasks)
                {
                    this._tasks.Remove(task);
                }
            }
            finally
            {
                this.m_lock.ExitWriteLock();
            }
        }

        public int Count
        {
            get
            {
                if (this.m_lock.TryEnterReadLock(m_nLockTimeout) == false)
                    throw new LockException("锁定尝试中超时");
                try
                {
                    return _tasks.Count;
                }
                finally
                {
                    this.m_lock.ExitReadLock();
                }
            }
        }

        public ChargingTask FindTaskByItemBarcode(string itemBarcode)
        {
            if (this.m_lock.TryEnterReadLock(m_nLockTimeout) == false)   // m_nLockTimeout
                throw new LockException("锁定尝试中超时");
            try
            {
                for (int i = _tasks.Count - 1; i >= 0; i--)
                {
                    var task = _tasks[i];

                    if (task.Action == "load_reader_info")
                        continue;

                    if (itemBarcode == task.ItemBarcode)
                        return task;
                }

                return null;
            }
            finally
            {
                this.m_lock.ExitReadLock();
            }
        }

#if NO
        public bool Stopped
        {
            get
            {
                return m_bStopThread;
            }
        }

        void StopThread(bool bForce)
        {
            // 如果以前在做，立即停止
            if (stop != null)
                stop.DoStop();

            m_bStopThread = true;
            this.eventClose.Set();

            if (bForce == true)
            {
                if (this._thread != null)
                {
                    if (!this._thread.Join(2000))
                        this._thread.Abort();
                    this._thread = null;
                }
            }
        }

        public void BeginThread()
        {
            // 如果以前在做，立即停止
            StopThread(true);

            this.eventActive.Set();
            this.eventClose.Reset(); 

            this._thread = new Thread(new ThreadStart(this.ThreadMain));
            this._thread.Start();
        }

        void ThreadMain()
        {
            m_bStopThread = false;

            try
            {

                WaitHandle[] events = new WaitHandle[2];

                events[0] = eventClose;
                events[1] = eventActive;

                while (m_bStopThread == false)
                {
                    int index = 0;
                    try
                    {
                        index = WaitHandle.WaitAny(events, PerTime, false);
                    }
                    catch (System.Threading.ThreadAbortException /*ex*/)
                    {
                        break;
                    }

                    if (index == WaitHandle.WaitTimeout)
                    {
                        // 超时
                        eventActive.Reset();
                        Worker();
                        eventActive.Reset();

                    }
                    else if (index == 0)
                    {
                        break;
                    }
                    else
                    {
                        // 得到激活信号
                        eventActive.Reset();
                        Worker();
                        eventActive.Reset();    // 2013/11/23 只让堵住的时候发挥作用
                    }
                }

                return;
            }
            finally
            {
                m_bStopThread = true;
                _thread = null;
            }
        }


        public void Activate()
        {
            eventActive.Set();
        }

#endif

        // 加入一个任务到列表中
        public void AddTask(ChargingTask task)
        {
            task.Color = "black";   // 表示等待处理
            if (this.m_lock.TryEnterWriteLock(500) == false)   // m_nLockTimeout
                throw new LockException("锁定尝试中超时");
            try
            {
                this._tasks.Add(task);
            }
            finally
            {
                this.m_lock.ExitWriteLock();
            }

            // 触发任务开始执行
            this.Activate();

            // 兑现显示
            this.Container.DisplayTask("add", task);
            this.Container.SetColorList();
        }

        public void RemoveTask(ChargingTask task, bool bLock = true)
        {
            if (bLock == true)
            {
                if (this.m_lock.TryEnterWriteLock(m_nLockTimeout) == false)
                    throw new LockException("锁定尝试中超时");
            }
            try
            {
                this._tasks.Remove(task);
            }
            finally
            {
                if (bLock == true)
                    this.m_lock.ExitWriteLock();
            }

            // 兑现显示
            this.Container.DisplayTask("remove", task);
            this.Container.SetColorList();
        }
    }

}
