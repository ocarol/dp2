﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Speech.Synthesis;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using DigitalPlatform.IO;
using DigitalPlatform.LibraryClient;
using DigitalPlatform.License;
using DigitalPlatform.Text;
using Ionic.Zip;
using static DigitalPlatform.CirculationClient.ClientInfo;

namespace DigitalPlatform.CirculationClient
{
    /// <summary>
    /// 扩展 ClientInfo 的一些功能，使之能够适应 Windows Form 前端要求
    /// </summary>
    public static class FormClientInfo
    {
        public static Form MainForm { get; set; }

        public static event EventHandler CommunityModeChanged;

        // parameters:
        //      product_name    例如 "fingerprintcenter"
        // return:
        //      true    初始化成功
        //      false   初始化失败，应立刻退出应用程序
        public static bool Initial(string product_name,
            Delegate_skipSerialNumberCheck skipCheck = null,
            string style = "")
        {
            var ret = ClientInfo.Initial(product_name, style);
            if (ret == false)
                return false;

            {
                // 检查序列号
                // if (DateTime.Now >= start_day || this.MainForm.IsExistsSerialNumberStatusFile() == true)
                if (SerialNumberMode == "must"
                    && (skipCheck == null || skipCheck() == false))
                {
                    // 在用户目录中写入一个隐藏文件，表示序列号功能已经启用
                    // this.WriteSerialNumberStatusFile();

                    ClientInfo.WriteInfoLog($"尝试打开序列号对话框");

                    // return:
                    //      -1  出错
                    //      0   放弃
                    //      1   成功
                    int nRet = VerifySerialCode($"{product_name}需要先设置序列号才能使用",
                        "",
                        "reinput",
                        out string strError);
                    if (nRet == -1 || nRet == 0)
                    {
                        ClientInfo.WriteErrorLog($"序列号不正确({strError})，{product_name} 退出");
                        MessageBox.Show(MainForm, $"{strError}。{product_name}需要先设置序列号才能使用");
                        API.PostMessage(MainForm.Handle, API.WM_CLOSE, 0, 0);
                        return false;
                    }

                    ClientInfo.WriteInfoLog($"序列号对话框被确认。nRet={nRet}, strError={strError}");
                }
            }

            return true;
        }

        #region 序列号

        static VerifyRequest BuildHashed(VerifyRequest request)
        {
            request.Hashed = GetMd5($"{request.ProductName}|{request.MacList}|{DateTime.UtcNow.ToString("yyyyMMdd")}");
            return request;
        }

        static string GetMd5(string strText)
        {
            MD5 hasher = MD5.Create();
            byte[] buffer = Encoding.UTF8.GetBytes(strText);
            byte[] target = hasher.ComputeHash(buffer);
            StringBuilder sb = new StringBuilder();
            foreach (byte b in target)
            {
                sb.Append(b.ToString("x2").ToLower());
            }

            return sb.ToString();
        }

        // 验证 MAC 地址是否被允许
        public static async Task<NormalResult> VerifyMac(
            string baseUrl,
            string product_name,
            string mac_list)
        {
            var request = new VerifyRequest
            {
                ProductName = product_name,
                MacList = mac_list,
            };
            BuildHashed(request);
            try
            {
                using (var httpClient = new HttpClient())
                {
                    var client = new LicenseClient(baseUrl, httpClient);
                    var result = await client.VerifyAsync(request);
                    if (result.State == null)
                        return new NormalResult();
                    return new NormalResult
                    {
                        Value = -1,
                        ErrorInfo = result.ErrorInfo,
                        ErrorCode = result.State
                    };
                }
            }
            catch (Exception ex)
            {
                ClientInfo.WriteErrorLog($"VerifyMac() 出现异常：{ExceptionUtil.GetDebugText(ex)}");
                if (ex is HttpRequestException)
                    return new NormalResult
                    {
                        Value = -1,
                        ErrorInfo = ex.Message + " " + ex.GetType().ToString(),
                        ErrorCode = ex.GetType().ToString(),
                    };
                return new NormalResult
                {
                    Value = -1,
                    ErrorInfo = $"VerifyMac() 出现异常：{ex.Message}",
                    ErrorCode = ex.GetType().ToString(),
                };
            }
        }

        // 最近一次网络验证 MAC 地址的时间
        static DateTime _lastNetworkVerifyMacTime = DateTime.MinValue;
        static int _lastNetworkVerifyMacResult = -1;
        static TimeSpan _networkVerifyExpireLength = TimeSpan.FromMinutes(10);  // TimeSpan.FromHours(12)

        // parameters:
        //      strRequireFuncList   要求必须具备的功能列表。逗号间隔的字符串
        //      strStyle    风格
        //                  reinput    如果序列号不满足要求，是否直接出现对话框让用户重新输入序列号
        //                  reset   执行重设序列号任务。意思就是无论当前序列号是否可用，都直接出现序列号对话框
        //                  skipVerify  不验证序列号合法性，只关注 function list 是否符合要求
        // return:
        //      -1  出错
        //      0   放弃
        //      1   成功
        public static int VerifySerialCode(
        string strTitle,
        string strRequireFuncList,
        string strStyle,
        out string strError)
        {
            strError = "";
            int nRet = 0;

            bool bReinput = StringUtil.IsInList("reinput", strStyle);
            bool bReset = StringUtil.IsInList("reset", strStyle);
            bool bSkipVerify = StringUtil.IsInList("skipVerify", strStyle);
            bool bLoose = StringUtil.IsInList("loose", strStyle);

            string strFirstMac = "";
            List<string> macs = SerialCodeForm.GetMacAddress();
            if (macs.Count != 0)
            {
                strFirstMac = macs[0];
            }

            string strSerialCode = ClientInfo.Config.Get("sn", "sn", "");
            if (string.IsNullOrEmpty(strSerialCode) == true
                && bLoose)
                return 1;

            // 首次运行
            if (string.IsNullOrEmpty(strSerialCode) == true)
            {
            }

        REDO_VERIFY:
            if (bReset == false)
            {
                if (SerialNumberMode != "must"
                    && strSerialCode == "community")
                {
                    if (string.IsNullOrEmpty(strRequireFuncList) == true)
                    {
                        CommunityMode = true;
                        ClientInfo.Config.Set("main_form", "last_mode", "community");
                        return 1;
                    }
                }

                // 2022/1/25
                if (string.IsNullOrEmpty(strSerialCode) == false
                    && strSerialCode != "community")
                    CommunityMode = false;
            }

            if (bReset == true
                || CheckFunction(GetEnvironmentString(""), strRequireFuncList) == false
                || (bSkipVerify == false && MatchLocalString(strSerialCode) == false)
                || String.IsNullOrEmpty(strSerialCode) == true)
            {
                _lastNetworkVerifyMacResult = -1;
                _lastNetworkVerifyMacTime = DateTime.MinValue;

                if (bReinput == false && bReset == false)
                {
                    strError = "序列号无效";
                    return -1;
                }

                if (bReset == false)
                {
                    if (String.IsNullOrEmpty(strSerialCode) == false
                        && MatchLocalString(strSerialCode) == false)
                        MessageBox.Show(MainForm, "序列号无效。请重新输入");
                    else if (CheckFunction(GetEnvironmentString(""), strRequireFuncList) == false)
                        MessageBox.Show(MainForm, $"序列号中尚未许可功能 {strRequireFuncList}。请重新输入");

                    /*
                    if (String.IsNullOrEmpty(strSerialCode) == false)
                        MessageBox.Show(MainForm, "序列号无效。请重新输入");
                    else if (CheckFunction(GetEnvironmentString(""), strRequireFuncList) == false)
                        MessageBox.Show(MainForm, "序列号中 function 参数无效。请重新输入");
                */
                }

                // 出现设置序列号对话框
                nRet = ResetSerialCode(
                    MainForm,
                    strTitle,
                    false,
                    strSerialCode,
                    GetEnvironmentString(strFirstMac));
                if (nRet == 0)
                {
                    strError = "放弃";
                    return 0;
                }
                strSerialCode = ClientInfo.Config.Get("sn", "sn", "");
                bReset = false;
                goto REDO_VERIFY;
            }

            // 网络验证
            // 2022/12/5
            if (bSkipVerify == false)
            {
                if (DateTime.Now - _lastNetworkVerifyMacTime < _networkVerifyExpireLength
                    && _lastNetworkVerifyMacResult == 1)
                    return 1;

                string mode = "online"; // 默认 online。online/offline/strict 之一

                bool offline = CheckFunction(GetEnvironmentString(""), "offline");
                bool strict = CheckFunction(GetEnvironmentString(""), "strict");
                if (offline)
                    mode = "offline";
                if (strict == true)
                    mode = "strict";

                // 如果没有 'offline' function
                if (mode == "online" || mode == "strict")
                {
                    _lastNetworkVerifyMacTime = DateTime.Now;
                    var mac_list = string.Join(",", macs);
                    var task = VerifyMac("https://dp2003.com/sncenter",
                        ProductName,
                        mac_list);
                    while (task.IsCompleted == false)
                    {
                        Application.DoEvents();
                    }
                    var result = task.Result;
                    if (mode == "strict")
                    {
                        // 如果模式为 strict，那么返回状态必须是空才行 
                        if (result.Value != 0 || string.IsNullOrEmpty(result.ErrorCode) == false)
                        {
                            strError = result.ErrorInfo;
                            _lastNetworkVerifyMacResult = -1;
                            return -1;
                        }
                    }
                    else
                    {
                        // 如果模式为一般 online，那么返回状态为 [notFound] 或空都是可以的 
                        if (result.Value == 0 || result.ErrorCode == "[notFound]")
                        {

                        }
                        else
                        {
                            strError = result.ErrorInfo;
                            _lastNetworkVerifyMacResult = -1;
                            return -1;
                        }
                    }

                    _lastNetworkVerifyMacResult = 1;
                }
            }

            return 1;
        }

        static bool _communityMode = false;

        public static bool CommunityMode
        {
            get
            {
                return _communityMode;
            }
            set
            {
                _communityMode = value;
                // SetTitle();

                // 2022/1/25
                CommunityModeChanged?.Invoke(value, new EventArgs());
            }
        }

        public static string SerialNumberMode = ""; // 序列号模式。空/must/loose
                                                    // 空表示不需要序列号。"must" 要求需要序列号；"loose" 不要序列号也可运行，但高级功能需要序列号。loose 和 must 都会出现“设置序列号”菜单命令
        public static string CopyrightKey = "";    // "dp2catalog_sn_key";

        static void SetTitle()
        {
#if NO
            if (this.CommunityMode == true)
                this.Text = "dp2Catalog V3 -- 编目 [社区版]";
            else
                this.Text = "dp2Catalog V3 -- 编目 [专业版]";
#endif
        }

        // return:
        //      0   Cancel
        //      1   OK
        static int ResetSerialCode(
            Form owner,
            string strTitle,
            bool bAllowSetBlank,
            string strOldSerialCode,
            string strOriginCode)
        {
            Hashtable ext_table = StringUtil.ParseParameters(strOriginCode);
            string strMAC = (string)ext_table["mac"];
            if (string.IsNullOrEmpty(strMAC) == true)
                strOriginCode = "!error";
            else
            {
                if (string.IsNullOrEmpty(CopyrightKey))
                    throw new Exception("请提前准备好 CopyrightKey 内容");
                Debug.Assert(string.IsNullOrEmpty(CopyrightKey) == false);
                strOriginCode = Cryptography.Encrypt(strOriginCode,
                CopyrightKey);
            }
            SerialCodeForm dlg = new SerialCodeForm();
            dlg.Text = strTitle;
            dlg.Font = owner.Font;
            if (SerialNumberMode == "loose")
                dlg.DefaultCodes = new List<string>(new string[] { "community|社区版" });
            dlg.SerialCode = strOldSerialCode;
            dlg.StartPosition = FormStartPosition.CenterScreen;
            dlg.OriginCode = strOriginCode;

            // 2020/12/24
            dlg.ShowInTaskbar = true;
        REDO:
            dlg.ShowDialog(owner);
            if (dlg.DialogResult != DialogResult.OK)
                return 0;

            if (string.IsNullOrEmpty(dlg.SerialCode) == true)
            {
                if (bAllowSetBlank == true)
                {
                    DialogResult result = MessageBox.Show(owner,
        $"确实要将序列号设置为空?\r\n\r\n(一旦将序列号设置为空，{ProductName} 将自动退出，下次启动需要重新设置序列号)",
        $"{ProductName}",
        MessageBoxButtons.YesNo,
        MessageBoxIcon.Question,
        MessageBoxDefaultButton.Button2);
                    if (result == System.Windows.Forms.DialogResult.No)
                    {
                        return 0;
                    }
                }
                else
                {
                    MessageBox.Show(owner, "序列号不允许为空。请重新设置");
                    goto REDO;
                }
            }

            ClientInfo.Config.Set("sn", "sn", dlg.SerialCode);
            ClientInfo.Config.Save();
            return 1;
        }

        // 将本地字符串匹配序列号
        static bool MatchLocalString(string strSerialNumber)
        {
            List<string> macs = SerialCodeForm.GetMacAddress();
            foreach (string mac in macs)
            {
                string strLocalString = GetEnvironmentString(mac);
                string strSha1 = Cryptography.GetSHA1(StringUtil.SortParams(strLocalString) + "_reply");
                if (strSha1 == SerialCodeForm.GetCheckCode(strSerialNumber))
                    return true;
            }

            if (DateTime.Now.Month == 12)
            {
                foreach (string mac in macs)
                {
                    string strLocalString = GetEnvironmentString(mac, true);
                    string strSha1 = Cryptography.GetSHA1(StringUtil.SortParams(strLocalString) + "_reply");
                    if (strSha1 == SerialCodeForm.GetCheckCode(strSerialNumber))
                        return true;
                }
            }

            return false;
        }

        // 根据前缀获取一个序列号功能。注意使用本函数以前应该用 VerifySerialCode() 函数先确认序列号有效
        public static string GetSerialCodeFunctionValueByPrefix(
string strPrefix)
        {
            var env = GetEnvironmentString("");
            return GetFunctionValueByPrefix(env, strPrefix);
        }

        // 在 function 参数中，根据前缀找到对应的值
        // parameters:
        //      strPrefix   前缀部分。注意，返回的内容是截取字符串中前缀匹配部分右侧内容，和前缀是密排的关系。
        //                  假设命中的字符串内容为 "prefix:xxx"，如果前缀是 "prefix"，那么返回的其实是 ":xxx"。
        //                  为了返回 "xxx"，可以用前缀 "prefix:" 来调用本函数
        static string GetFunctionValueByPrefix(string strEnvString,
    string strPrefix)
        {
            Hashtable table = StringUtil.ParseParameters(strEnvString);
            string strFuncValue = (string)table["function"];
            if (strFuncValue == null)
                return null;

            foreach (var value in strFuncValue.Split('|'))
            {
                if (value.StartsWith(strPrefix))
                    return value.Substring(strPrefix.Length);
            }

            return null;
        }


        // return:
        //      false   不满足
        //      true    满足
        static bool CheckFunction(string strEnvString,
            string strFuncList)
        {
            Hashtable table = StringUtil.ParseParameters(strEnvString);
            string strFuncValue = (string)table["function"];
            // 2020/12/24
            if (strFuncValue != null)
                strFuncValue = strFuncValue.Replace("|", ",");

            string[] parts = strFuncList.Split(new char[] { ',' });
            foreach (string part in parts)
            {
                if (string.IsNullOrEmpty(part) == true)
                    continue;
                if (StringUtil.IsInList(part, strFuncValue) == false)
                    return false;
            }

            return true;
        }

        // parameters:
        static string GetEnvironmentString(string strMAC,
            bool bNextYear = false)
        {
            Hashtable table = new Hashtable();
            table["mac"] = strMAC;  //  SerialCodeForm.GetMacAddress();
            if (bNextYear == false)
                table["time"] = SerialCodeForm.GetTimeRange();
            else
                table["time"] = SerialCodeForm.GetNextYearTimeRange();

            table["product"] = ProductName;

            string strSerialCode = ClientInfo.Config.Get("sn", "sn", "");
            // 将 strSerialCode 中的扩展参数设定到 table 中
            SerialCodeForm.SetExtParams(ref table, strSerialCode);
            return StringUtil.BuildParameterString(table);
        }

        // 获得 xxx|||xxxx 的左边部分
        static string GetCheckCode(string strSerialCode)
        {
            string strSN = "";
            string strExtParam = "";
            StringUtil.ParseTwoPart(strSerialCode,
                "|||",
                out strSN,
                out strExtParam);

            return strSN;
        }

        // 获得 xxx|||xxxx 的右边部分
        static string GetExtParams(string strSerialCode)
        {
            string strSN = "";
            string strExtParam = "";
            StringUtil.ParseTwoPart(strSerialCode,
                "|||",
                out strSN,
                out strExtParam);

            return strExtParam;
        }

        #endregion

        #region Speak

        static SpeechSynthesizer m_speech = new SpeechSynthesizer();
        static string m_strSpeakContent = "";

        public static void Speak(string strText,
            bool bError = false,
            bool cancel_before = true)
        {
#if NO
            string color = "gray";
            if (bError)
                color = "darkred";

            DisplayText(strText, "white", color);
#endif

            if (m_speech == null)
                return;

            if (SpeakOn == false)
                return;

            m_strSpeakContent = strText;
            MainForm.BeginInvoke((Action)(() =>
            {
                try
                {
                    if (cancel_before)
                        m_speech.SpeakAsyncCancelAll();
                    m_speech.SpeakAsync(strText);
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    WriteErrorLog($"FormClientInfo::Speak() 出现异常: {ExceptionUtil.GetDebugText(ex)}");
                }
            }));
        }

        public static async Task<NormalResult> Speaking(string strText,
            bool cancel_before,
            CancellationToken token)
        {
            if (m_speech == null)
                return new NormalResult
                {
                    Value = -1,
                    ErrorInfo = "m_speech == null",
                    ErrorCode = "internalError"
                };

            if (SpeakOn == false)
                return new NormalResult
                {
                    Value = 0,
                    ErrorCode = "speakOff"  // 当前用户关闭了语音
                };

            var prompt = new Prompt(strText);

            ManualResetEvent eventFinish = new ManualResetEvent(false);
            string error = null;
            MainForm.BeginInvoke((Action)(() =>
            {
                try
                {
                    if (cancel_before)
                        m_speech.SpeakAsyncCancelAll();
                    m_speech.SpeakCompleted += speech_SpeakCompleted;
                    m_speech.SpeakAsync(prompt);
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    error = $"FormClientInfo::Speaking() 出现异常: {ex.Message}";
                    WriteErrorLog($"FormClientInfo::Speaking() 出现异常: {ExceptionUtil.GetDebugText(ex)}");
                    m_speech.SpeakCompleted -= speech_SpeakCompleted;
                }
            }));

            int index = WaitHandle.WaitAny(new WaitHandle[] {
                            eventFinish,
                            token.WaitHandle,
                            });
            if (string.IsNullOrEmpty(error) == false)
                return new NormalResult
                {
                    Value = -1,
                    ErrorInfo = error
                };
            // cancelled
            if (index == 1)
                return new NormalResult
                {
                    Value = -1,
                    ErrorInfo = "中断",
                    ErrorCode = "cancelled"
                };
            // 正常返回
            return new NormalResult();

            void speech_SpeakCompleted(object sender, SpeakCompletedEventArgs e)
            {
                if (e.Prompt != prompt)
                    return;
                m_speech.SpeakCompleted -= speech_SpeakCompleted;
                eventFinish.Set();
            }
        }

        /*
        public static void CancelSpeaking()
        {
            m_speech.SpeakAsyncCancelAll();
        }
        */

        // 2021/4/21
        // 中断正在播报的语音
        public static void CancelSpeaking()
        {
            MainForm.BeginInvoke((Action)(() =>
            {
                try
                {
                    m_speech.SpeakAsyncCancelAll();
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    WriteErrorLog($"FormClientInfo::CancelSpeaking() 出现异常: {ExceptionUtil.GetDebugText(ex)}");
                }
            }));
        }

        public static bool SpeakOn
        {
            get
            {
                return true;    // for testing
            }
        }

        public static void SetErrorState(string state, string info)
        {
            if (state == "error")   // 出现错误，后面不再会重试
                SetWholeColor(Color.DarkRed, Color.White);
            else if (state == "retry")   // 出现错误，但后面会自动重试
                SetWholeColor(Color.DarkOrange, Color.Black);
            else if (state == "normal")  // 没有错误
                SetWholeColor(SystemColors.Window, SystemColors.WindowText);
            else
                throw new Exception($"无法识别的 state={state}");

            _errorState = state;
            _errorStateInfo = info;
        }

        static void SetWholeColor(Color backColor, Color foreColor)
        {
            MainForm?.Invoke((Action)(() =>
            {
                FormClientInfo.ProcessControl(MainForm,
                    (o) =>
                    {
                        dynamic d = o;
                        d.BackColor = backColor;
                        d.ForeColor = foreColor;
                    });

#if NO
                this.BackColor = backColor;
                this.ForeColor = foreColor;
                foreach (TabPage page in this.tabControl_main.TabPages)
                {
                    page.BackColor = backColor;
                    page.ForeColor = foreColor;
                }
                this.toolStrip1.BackColor = backColor;
                this.toolStrip1.ForeColor = foreColor;

                this.menuStrip1.BackColor = backColor;
                this.menuStrip1.ForeColor = foreColor;

                this.statusStrip1.BackColor = backColor;
                this.statusStrip1.ForeColor = foreColor;
#endif
            }));
        }

        #endregion

        #region 未捕获的异常处理 

        // 准备接管未捕获的异常
        public static void PrepareCatchException()
        {
            Application.ThreadException += Application_ThreadException;
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        static bool _bExiting = false;   // 是否处在 正在退出 的状态

        static void CurrentDomain_UnhandledException(object sender,
    UnhandledExceptionEventArgs e)
        {
            if (_bExiting == true)
                return;

            Exception ex = (Exception)e.ExceptionObject;
            string strError = GetExceptionText(ex, "");

            // TODO: 把信息提供给数字平台的开发人员，以便纠错
            // TODO: 显示为红色窗口，表示警告的意思
            bool bSendReport = true;
            DialogResult result = MessageDlg.Show(MainForm,
    $"{ProgramName} 发生未知的异常:\r\n\r\n" + strError + "\r\n---\r\n\r\n点“关闭”即关闭程序",
    $"{ProgramName} 发生未知的异常",
    MessageBoxButtons.OK,
    MessageBoxDefaultButton.Button1,
    ref bSendReport,
    new string[] { "关闭" },
    "将信息发送给开发者");
            // 发送异常报告
            if (bSendReport)
                CrashReport(strError);
        }

        static string GetExceptionText(Exception ex, string strType)
        {
            // Exception ex = (Exception)e.Exception;
            string strError = "发生未捕获的" + strType + "异常: \r\n" + ExceptionUtil.GetDebugText(ex);
            Assembly myAssembly = Assembly.GetAssembly(TypeOfProgram);
            strError += $"\r\n{ProgramName} 版本: " + myAssembly.FullName;
            strError += "\r\n操作系统：" + Environment.OSVersion.ToString();
            strError += "\r\n本机 MAC 地址: " + StringUtil.MakePathList(SerialCodeForm.GetMacAddress());

            // TODO: 给出操作系统的一般信息

            // MainForm.WriteErrorLog(strError);
            return strError;
        }

        static void Application_ThreadException(object sender,
    ThreadExceptionEventArgs e)
        {
            if (_bExiting == true)
                return;

            Exception ex = (Exception)e.Exception;
            string strError = GetExceptionText(ex, "界面线程");

            bool bSendReport = true;
            DialogResult result = MessageDlg.Show(MainForm,
    $"{ProgramName} 发生未知的异常:\r\n\r\n" + strError + "\r\n---\r\n\r\n是否关闭程序?",
    $"{ProgramName} 发生未知的异常",
    MessageBoxButtons.YesNo,
    MessageBoxDefaultButton.Button2,
    ref bSendReport,
    new string[] { "关闭", "继续" },
    "将信息发送给开发者");
            {
                if (bSendReport)
                    CrashReport(strError);
            }
            if (result == DialogResult.Yes)
            {
                _bExiting = true;
                Application.Exit();
            }
        }

        static void CrashReport(string strText)
        {
            MessageBar _messageBar = null;

            _messageBar = new MessageBar
            {
                TopMost = false,
                Text = $"{ProgramName} 出现异常",
                MessageText = "正在向 dp2003.com 发送异常报告 ...",
                StartPosition = FormStartPosition.CenterScreen
            };
            _messageBar.Show(MainForm);
            _messageBar.Update();

            int nRet = 0;
            string strError = "";
            try
            {
                string strSender = "";
                //if (MainForm != null)
                //    strSender = MainForm.GetCurrentUserName() + "@" + MainForm.ServerUID;
                // 崩溃报告

                nRet = LibraryChannel.CrashReport(
                    strSender,
                    $"{ProgramName}",
                    strText,
                    out strError);
            }
            catch (Exception ex)
            {
                strError = "CrashReport() 过程出现异常: " + ExceptionUtil.GetDebugText(ex);
                nRet = -1;
            }
            finally
            {
                _messageBar.Close();
                _messageBar = null;
            }

            if (nRet == -1)
            {
                strError = "向 dp2003.com 发送异常报告时出错，未能发送成功。详细情况: " + strError;
                MessageBox.Show(MainForm, strError);
                // 写入错误日志
                WriteErrorLog(strError);
            }
        }


        #endregion

        #region Form 实用函数

        public delegate void delegate_action(object o);

        public static void ProcessControl(Control control,
            delegate_action action)
        {
            action(control);
            ProcessChildren(control, action);
        }

        static void ProcessChildren(Control parent,
            delegate_action action)
        {
            // 修改所有下级控件的字体，如果字体名不一样的话
            foreach (Control sub in parent.Controls)
            {
                action(sub);

                if (sub is ToolStrip)
                {
                    ProcessToolStrip((ToolStrip)sub, action);
                }

                if (sub is SplitContainer)
                {
                    ProcessSplitContainer(sub as SplitContainer, action);
                }

                // 递归
                ProcessChildren(sub, action);
            }
        }

        static void ProcessToolStrip(ToolStrip tool,
delegate_action action)
        {
            List<ToolStripItem> items = new List<ToolStripItem>();
            foreach (ToolStripItem item in tool.Items)
            {
                items.Add(item);
            }

            foreach (ToolStripItem item in items)
            {
                action(item);

                if (item is ToolStripMenuItem)
                {
                    ProcessDropDownItemsFont(item as ToolStripMenuItem, action);
                }
            }
        }

        static void ProcessDropDownItemsFont(ToolStripMenuItem menu,
            delegate_action action)
        {
            // 修改所有事项的字体，如果字体名不一样的话
            foreach (ToolStripItem item in menu.DropDownItems)
            {

                action(item);

                if (item is ToolStripMenuItem)
                {
                    ProcessDropDownItemsFont(item as ToolStripMenuItem, action);
                }
            }
        }

        static void ProcessSplitContainer(SplitContainer container,
            delegate_action action)
        {
            action(container.Panel1);

            foreach (Control control in container.Panel1.Controls)
            {
                ProcessChildren(control, action);
            }

            action(container.Panel2);

            foreach (Control control in container.Panel2.Controls)
            {
                ProcessChildren(control, action);
            }
        }

        #endregion


        #region 绿色安装包

        // 创建绿色更新包
        public static async Task BuildGreenUpdatePack(
            string strProductName,
            string strDataDir,
            string strTempDir,
            IO.CompressUtil.delegate_displayText func_display)
        {
            string strError = "";

            var data_dir = strDataDir;
            var program_dir = Environment.CurrentDirectory;

            if (PathUtil.IsEqual(data_dir, program_dir) == true)
            {
                strError = $"此 {strProductName} 为非 ClickOnce 安装方式，无法创建绿色更新包";
                goto ERROR1;
            }

            var temp_dir = strTempDir;
            var data_filename = Path.Combine(strTempDir, "data.zip");
            var program_filename = Path.Combine(strTempDir, "program.zip");

            var final_filename = Path.Combine(strTempDir, $"{strProductName}_update.zip");

            NormalResult result = null;
            func_display?.Invoke("正在创建绿色更新包 ...");
            try
            {
                result = await Task.Run<NormalResult>(() =>
                {

                    int nRet = CompressUtil.CompressDirectory(
                data_dir,
                data_dir,
                data_filename,
                Encoding.UTF8,
                out strError);
                    if (nRet == -1)
                        return new NormalResult
                        {
                            Value = -1,
                            ErrorInfo = strError
                        };
                    nRet = CompressUtil.CompressDirectory(
    program_dir,
    program_dir,
    program_filename,
    Encoding.UTF8,
    out strError);
                    if (nRet == -1)
                        return new NormalResult
                        {
                            Value = -1,
                            ErrorInfo = strError
                        };
                    // 再压缩到一个文件
                    if (File.Exists(final_filename))
                        File.Delete(final_filename);

                    List<string> filenames = new List<string>();
                    filenames.Add(data_filename);
                    filenames.Add(program_filename);
                    using (ZipFile zip = new ZipFile(Encoding.UTF8))
                    {
                        zip.ParallelDeflateThreshold = -1;

                        foreach (string filename in filenames)
                        {
                            string strShortFileName = filename.Substring(temp_dir.Length + 1);
                            string directoryPathInArchive = Path.GetDirectoryName(strShortFileName);
                            zip.AddFile(filename, directoryPathInArchive);
                        }

                        zip.UseZip64WhenSaving = Zip64Option.AsNecessary;
                        zip.Save(final_filename);
                    }

                    File.Delete(data_filename);
                    File.Delete(program_filename);

                    return new NormalResult();
                });
            }
            finally
            {
                func_display?.Invoke("");
            }

            if (result.Value == -1)
            {
                strError = result.ErrorInfo;
                goto ERROR1;
            }

            // 打开文件夹
            try
            {
                // https://stackoverflow.com/questions/1073353/c-how-to-open-windows-explorer-windows-with-a-number-of-files-selected
                // Process.Start("explorer.exe",
                // "/select,Z:\Music\Thursday Blues\01. I wish it was friday.mp3")
                System.Diagnostics.Process.Start("explorer.exe",
                    $"/select,{final_filename}"); // temp_dir
            }
            catch (Exception ex)
            {
                MessageBox.Show(MainForm, ExceptionUtil.GetAutoText(ex));
            }

            return;
        ERROR1:
            MessageBox.Show(MainForm, strError);
        }

        // 安装绿色更新包
        // parameters:
        //      strTempDir  临时目录。
        //                  注: 可能在这个临时目录内留下一些名字为 GUID 形态的临时文件，直到 Windows 重启以后才自动删除。所以最好用一个专用的临时目录，避免干扰其它临时文件所在目录
        public static void UpdateByGreenUpdatePack(
            string strProductName,
            string strDataDir,
            string strTempDir,
            IO.CompressUtil.delegate_displayText func_display,
            CancellationToken token = default)
        {
            string strError = "";

            var data_dir = strDataDir;
            var program_dir = Environment.CurrentDirectory;

            if (PathUtil.IsEqual(data_dir, program_dir) == false)
            {
                strError = $"此 {strProductName} 为 ClickOnce 安装方式，无法使用绿色更新包进行更新";
                goto ERROR1;
            }

            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = $"请指定 {strProductName} 绿色更新包文件名";
            // dlg.FileName = this.textBox_filename.Text;

            dlg.Filter = $"{strProductName}_update.zip|{strProductName}_update.zip|All files (*.*)|*.*";
            dlg.RestoreDirectory = true;

            if (dlg.ShowDialog() != DialogResult.OK)
                return;

            // 检查文件名
            if (Path.GetFileName(dlg.FileName) != $"{strProductName}_update.zip")
            {
                strError = "文件名不正确";
                goto ERROR1;
            }

            // testing
            // data_dir = "c:\\temp\\data_dir";
            // program_dir = "c:\\temp\\program_dir";

            var data_filename = Path.Combine(strTempDir, "data.zip");
            var program_filename = Path.Combine(strTempDir, "program.zip");
            var temp_dir = strTempDir;  

            NormalResult result = null;
            func_display?.Invoke("正在安装绿色更新包 ...");

            NormalResult Extract()
            {
                try
                {
                    // 展开到临时目录
                    using (ZipFile zip = ZipFile.Read(dlg.FileName))
                    {
                        foreach (ZipEntry entry in zip)
                        {
                            entry.Extract(temp_dir, ExtractExistingFileAction.OverwriteSilently);
                        }
                    }

                    bool need_reboot = false;
                    // return:
                    //      -1  出错
                    //      0   成功。不需要 reboot
                    //      1   成功。需要 reboot
                    int nRet = CompressUtil.ExtractFile(data_filename,
                            data_dir,
                            true,
                            temp_dir,
                            func_display,
                            token,
                            out strError);
                    if (nRet == -1)
                        return new NormalResult
                        {
                            Value = -1,
                            ErrorInfo = strError
                        };
                    if (nRet == 1)
                        need_reboot = true;

                    nRet = CompressUtil.ExtractFile(program_filename,
            program_dir,
            true,
            temp_dir,
            func_display,
            token,
            out strError);
                    if (nRet == -1)
                        return new NormalResult
                        {
                            Value = -1,
                            ErrorInfo = strError
                        };
                    if (nRet == 1)
                        need_reboot = true;

                    return new NormalResult { Value = need_reboot ? 1 : 0 };
                }
                finally
                {
                    func_display?.Invoke("");
                }
            }

            result = Extract();
            if (result.Value == -1)
            {
                strError = result.ErrorInfo;
                goto ERROR1;
            }

            if (result.Value == 1)
                MessageBox.Show(MainForm, "请重新启动 Windows，以完成绿色更新包安装");
            else
                MessageBox.Show(MainForm, "绿色更新包安装完成");
            return;
        ERROR1:
            MessageBox.Show(MainForm, strError);
        }

        #endregion
    }
}
