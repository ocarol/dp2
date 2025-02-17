﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using static dp2SSL.LibraryChannelUtil;

using DigitalPlatform;
using DigitalPlatform.RFID;
using DigitalPlatform.WPF;
using DigitalPlatform.Text;
using DigitalPlatform.Xml;

namespace dp2SSL
{
    /// <summary>
    /// WriteTagWindow.xaml 的交互逻辑
    /// </summary>
    public partial class WriteTagWindow : Window
    {
        // 要执行的任务信息
        public WriteTagTask TaskInfo { get; set; }

        public bool Finished { get; set; }

        // 是否要循环写入？
        // 循环写入的意思是，当前一次写入结束后，不自动关闭对话框，而是继续等待后面扫入册条码号再次进行写入
        public bool LoopWriting { get; set; }

        public WriteTagWindow()
        {
            InitializeComponent();

            this.booksControl.SetSource(_entities);

            this.Loaded += WriteTagWindow_Loaded;
            this.Unloaded += WriteTagWindow_Unloaded;

            this.booksControl.SelectionChanged += BooksControl_SelectionChanged;

            ShelfData.PatronTagList.EnableTagCache = false;
        }

        private void BooksControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (this.TaskInfo == null)
            {
                this.writeButton.IsEnabled = false;
                return;
            }

            if (this.booksControl.SelectedItems.Count > 0)
                this.writeButton.IsEnabled = true;
            else
                this.writeButton.IsEnabled = false;
        }

        public string Comment
        {
            get
            {
                return this.comment.Text;
            }
            set
            {
                this.comment.Text = value;
                if (value != null)
                {
                    this.comment.Visibility = Visibility.Visible;
                    this.richText.Visibility = Visibility.Collapsed;
                }
            }
        }

        public FlowDocument CommentDocument
        {
            get
            {
                return richText.Document;
            }
            set
            {
                richText.Document = value;
                if (value != null)
                {
                    if (this.comment.Visibility != Visibility.Collapsed)
                        this.comment.Visibility = Visibility.Collapsed;
                    if (richText.Visibility != Visibility.Visible)
                        richText.Visibility = Visibility.Visible;
                }
            }
        }


        public string TitleText
        {
            get
            {
                return this.title.Text;
            }
            set
            {
                this.title.Text = value;
            }
        }

        private void WriteTagWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            App.PatronTagChanged -= App_PatronTagChanged;
            App.LineFeed -= App_LineFeed;

            PageShelf.TrySetMessage(null, $"写入 RFID 标签对话框关闭");
        }

        private async void WriteTagWindow_Loaded(object sender, RoutedEventArgs e)
        {
            PageShelf.TrySetMessage(null, $"写入 RFID 标签对话框打开");

            _tagChanged = false;
            App.LineFeed += App_LineFeed;
            App.PatronTagChanged += App_PatronTagChanged;
            while (true)
            {
                await InitialEntitiesAsync();
                if (_tagChanged == false)
                    break;
            }
            BeginComment();
        }

        void BeginComment()
        {
            if (this.TaskInfo != null)
            {
                App.CurrentApp.Speak("请放 RFID 标签");
                // this.Comment = $"准备写入 RFID 标签。PII={this.TaskInfo.PII}";
                this.CommentDocument = BuildDocument("准备写入 RFID 标签", this.TaskInfo, 18);
                this.booksControl.EmptyComment = "请在读写器上放 RFID 标签 ...";
                if (this.booksControl.ItemCount == 0)
                    this.booksControl.ShowEmptyComment(true);
            }
            else
            {
                App.CurrentApp.Speak("请扫图书册条码");
                this.Comment = "请扫入图书册条码 ...";
                this.booksControl.ShowEmptyComment(false);
                this.border.Background = new SolidColorBrush(Colors.Transparent);
            }
        }

        private async void App_LineFeed(object sender, LineFeedEventArgs e)
        {
            // 扫入一个条码
            string barcode = e.Text;

            // 检查防范空字符串
            if (string.IsNullOrEmpty(barcode))
            {
                App.CurrentApp.Speak("条码不合法");
                App.ErrorBox("扫入册条码",
    "条码不合法",
    "red",
    "auto_close");
                return;
            }

            // 册条码号应该都是大写的
            barcode = barcode.ToUpper();

            PageShelf.TrySetMessage(null, $"扫入册条码号: {barcode}");

            // 根据 PII 准备好 TaskInfo
            var result = await this.PrepareTaskAsync(barcode);
            if (result.Value == -1)
            {
                App.CurrentApp.Speak(result.ErrorInfo);
                App.ErrorBox("准备 Task 时出错",
                    result.ErrorInfo,
                    "red",
                    "auto_close");
                return;
            }
            else
            {
                App.Invoke(new Action(() =>
                {
                    BeginComment();
                }));
            }

            if (this.TaskInfo != null
                && _entities.Count > 0)
            {
                var write_result = await TryWriteTagAsync(_entities,
                    this.TaskInfo);
                if (write_result.Value == -1)
                {
                    App.CurrentApp.Speak(result.ErrorInfo);
                    App.ErrorBox("写标签时出错",
                        write_result.ErrorInfo,
                        "red",
                        "auto_close");
                }
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

#pragma warning disable VSTHRD100 // 避免使用 Async Void 方法
        private async void App_PatronTagChanged(object sender, NewTagChangedEventArgs e)
#pragma warning restore VSTHRD100 // 避免使用 Async Void 方法
        {
            _tagChanged = true;
            if (_inInitializing > 0)
                return;

            // 重置活跃时钟
            PageMenu.MenuPage.ResetActivityTimer();

            /*
            // 在读者证读卡器上扫 ISO15693 的标签可以查看图书内容
            {
                if (e.AddTags?.Count > 0
                    || e.UpdateTags?.Count > 0
                    || e.RemoveTags?.Count > 0)
                    DetectPatron();
            }
            */
            await ChangeEntitiesAsync((BaseChannel<IRfid>)sender, e);
        }

        EntityCollection _entities = new EntityCollection();

        // 在初始化过程中途 TagChanged 是否到来过
        bool _tagChanged = false;
        int _inInitializing = 0;

        async Task InitialEntitiesAsync()
        {
            _inInitializing++;
            try
            {
                _entities.Clear();

                List<Entity> update_entities = new List<Entity>();
                foreach (var tag in ShelfData.PatronTagList.Tags)
                {
                    var entity = _entities.Add(tag);
                    update_entities.Add(entity);
                }

                if (update_entities.Count > 0)
                {
                    BaseChannel<IRfid> channel = RfidManager.GetChannel();
                    try
                    {
                        await FillBookFieldsAsync(channel, update_entities);
                    }
                    finally
                    {
                        RfidManager.ReturnChannel(channel);
                    }
                }

                if (this.TaskInfo != null)
                {
                    var write_result = await TryWriteTagAsync(update_entities,
                        this.TaskInfo);
                    if (write_result.Value == -1)
                    {
                        App.ErrorBox("写标签时出错",
                            write_result.ErrorInfo,
                            "red",
                            "auto_close");
                    }
                }
            }
            finally
            {
                _inInitializing--;
            }
        }

        // 跟随事件动态更新列表
        async Task ChangeEntitiesAsync(BaseChannel<IRfid> channel,
            NewTagChangedEventArgs e)
        {
            /*
            if (booksControl.Visibility != Visibility.Visible)
                return;
            */

            bool changed = false;
            List<Entity> update_entities = new List<Entity>();
            App.Invoke(new Action(() =>
            {
                if (e.AddTags != null)
                    foreach (var tag in e.AddTags)
                    {
                        var entity = _entities.Add(tag);
                        update_entities.Add(entity);
                    }
                if (e.RemoveTags != null)
                    foreach (var tag in e.RemoveTags)
                    {
                        _entities.Remove(tag.OneTag.UID);
                        changed = true;
                    }
                if (e.UpdateTags != null)
                    foreach (var tag in e.UpdateTags)
                    {
                        var entity = _entities.Update(tag);
                        if (entity != null)
                            update_entities.Add(entity);
                    }
            }));

            if (update_entities.Count > 0)
            {
                App.Invoke(new Action(() =>
                {
                    this.booksControl.ShowEmptyComment(false);
                }));

                await FillBookFieldsAsync(channel, update_entities);
            }
            else if (changed)
            {
                // 修改 borrowable
                // booksControl.SetBorrowable();
            }

            if (update_entities.Count > 0)
                changed = true;

            if (this.TaskInfo != null)
            {
                var write_result = await TryWriteTagAsync(update_entities,
        this.TaskInfo);
                if (write_result.Value == -1)
                {
                    App.ErrorBox("写标签时出错",
                        write_result.ErrorInfo,
                        "red",
                        "auto_close");
                }
            }
        }

        // 第二阶段：填充图书信息的 PII 和 Title 字段
        async Task FillBookFieldsAsync(BaseChannel<IRfid> channel,
            List<Entity> entities)
        {
#if NO
            RfidChannel channel = RFID.StartRfidChannel(App.RfidUrl,
out string strError);
            if (channel == null)
                throw new Exception(strError);
#endif
            try
            {
                foreach (Entity entity in entities)
                {
                    /*
                    if (_cancel == null
                        || _cancel.IsCancellationRequested)
                        return;
                        */
                    if (entity.FillFinished == true)
                        continue;

                    //if (string.IsNullOrEmpty(entity.Error) == false)
                    //    continue;

                    // 获得 PII
                    // 注：如果 PII 为空，文字中要填入 "(空)"
                    if (string.IsNullOrEmpty(entity.PII))
                    {
                        if (entity.TagInfo == null)
                            continue;

                        Debug.Assert(entity.TagInfo != null);

                        // Exception:
                        //      可能会抛出异常 ArgumentException TagDataException
                        LogicChip chip = LogicChip.From(entity.TagInfo.Bytes,
(int)entity.TagInfo.BlockSize,
"" // tag.TagInfo.LockStatus
);
                        string pii = chip.FindElement(ElementOID.PII)?.Text;
                        entity.PII = GetCaption(pii);

                        // 2021/4/2
                        entity.OI = chip.FindElement(ElementOID.OI)?.Text;
                        entity.AOI = chip.FindElement(ElementOID.AOI)?.Text;
                    }

                    bool clearError = true;

                    // 获得 Title
                    // 注：如果 Title 为空，文字中要填入 "(空)"
                    if (string.IsNullOrEmpty(entity.Title)
                        && string.IsNullOrEmpty(entity.PII) == false && entity.PII != "(空)")
                    {
                        var waiting = entity.Waiting;
                        entity.Waiting = true;
                        try
                        {
                            GetEntityDataResult result = null;
                            if (App.Protocol == "sip")
                                result = await SipChannelUtil.GetEntityDataAsync(entity.PII,
                                    entity.GetOiOrAoi(),
                                    "network");
                            else
                            {
                                // 2021/4/15
                                var strict = ChargingData.GetBookInstitutionStrict();
                                if (strict)
                                {
                                    string oi = entity.GetOiOrAoi();
                                    if (string.IsNullOrEmpty(oi))
                                    {
                                        entity.SetError("标签中没有机构代码，被拒绝使用");
                                        clearError = false;
                                        goto CONTINUE;
                                    }
                                }
                                result = await LibraryChannelUtil.GetEntityDataAsync(entity.GetOiPii(strict), "network"); // 2021/4/2 改为严格模式 OI_PII
                            }

                            if (result.Value == -1)
                            {
                                entity.SetError(result.ErrorInfo);
                                clearError = false;
                                goto CONTINUE;
                            }

                            entity.Title = GetCaption(result.Title);
                            entity.SetData(result.ItemRecPath,
                                result.ItemXml,
                                DateTime.Now);

                            // 2020/7/3
                            // 获得册记录阶段出错，但获得书目摘要成功
                            if (string.IsNullOrEmpty(result.ErrorCode) == false)
                            {
                                entity.SetError(result.ErrorInfo);
                                clearError = false;
                            }
                        }
                        finally
                        {
                            entity.Waiting = waiting;
                        }
                    }

                CONTINUE:
                    if (clearError == true)
                        entity.SetError(null);
                    entity.FillFinished = true;
                    // 2020/9/10
                    entity.Waiting = false;
                }

                booksControl.SetBorrowable();
            }
            catch (Exception ex)
            {
                WpfClientInfo.WriteErrorLog($"FillBookFields() 发生异常: {ExceptionUtil.GetExceptionText(ex)}");   // 2019/9/19
                SetGlobalError("current", $"FillBookFields() 发生异常(已写入错误日志): {ex.Message}"); // 2019/9/11 增加 FillBookFields() exception:
            }
        }

        public static string GetCaption(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "(空)";

            return text;
        }

        // 设置全局区域错误字符串
        void SetGlobalError(string type, string error)
        {
            App.SetError(type, error);
        }

        class FindBlankTagResult : NormalResult
        {
            public Entity ResultEntity { get; set; }
        }

        // 寻找唯一的空标签。如果出现了，而且是唯一一个，则自动向里写入内容
        async Task<FindBlankTagResult> FindBlankTagAsync(
            IList<Entity> entities,
            WriteTagTask task_info)
        {
            List<Entity> blank_entities = new List<Entity>();
            List<Entity> pii_entities = new List<Entity>();
            foreach (var entity in entities)
            {
                if (IsBlank(entity))
                    blank_entities.Add(entity);
                if (entity.PII == task_info.PII && entity.GetOiOrAoi() == task_info.OI)
                    pii_entities.Add(entity);
            }

            // 如果空白标签正好是一个
            if (pii_entities.Count == 0 && blank_entities.Count == 1)
                return new FindBlankTagResult { ResultEntity = blank_entities[0] };

            // 如果 PII 对得上的正好是一个
            if (blank_entities.Count == 0 && pii_entities.Count == 1)
                return new FindBlankTagResult { ResultEntity = pii_entities[0] };

            return new FindBlankTagResult();
        }

        // 尝试寻找一个空白标签写入
        async Task<NormalResult> TryWriteTagAsync(IList<Entity> entities,
            WriteTagTask task_info)
        {
            if (this.Finished)
                return new NormalResult { Value = 0 };

            var result = await FindBlankTagAsync(entities, task_info);
            if (result.Value == -1)
                return new NormalResult { Value = 0 };

            if (result.ResultEntity != null)
            {
                var write_result = WriteEntity(result.ResultEntity,
                    task_info);
                if (write_result.Value == -1)
                    return write_result;

                PageShelf.TrySetMessage(null, $"写入 RFID 成功。\r\n{task_info.GetOiPii()}\r\n{task_info?.Title}");
#if REMOVED
                // 写入
                var chip = BuildChip(task_info);
                int nRet = SaveNewChip(result.ResultEntity.ReaderName,
                    result.ResultEntity.TagInfo,
                    chip,
                    out TagInfo new_tag_info,
                    out string strError);
                if (nRet == -1)
                    return new NormalResult
                    {
                        Value = -1,
                        ErrorInfo = strError
                    };
                // 语音播报成功，自动关闭窗口
                this.Finished = true;
                App.CurrentApp.SpeakSequence($"写入完成");
                App.Invoke(new Action(() =>
                {
                    this.Comment = $"写入完成。PII={result.ResultEntity.PII}, UID={new_tag_info.UID}";
                    this.Background = new SolidColorBrush(Colors.DarkGreen);
                }));

                // 3 秒以后自动关闭对话框
                _ = Task.Run(async ()=> {
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    App.Invoke(new Action(() =>
                    {
                        this.Close();
                    }));
                });
#endif

                return new NormalResult { Value = 1 };
            }

            return new NormalResult { Value = 0 };
        }

        // 写入指定的 Entity 所代表的标签
        NormalResult WriteEntity(Entity entity,
            WriteTagTask task_info)
        {
            // 写入
            var chip = BuildChip(task_info);
            int nRet = SaveNewChip(entity.ReaderName,
                entity.TagInfo,
                chip,
                out TagInfo new_tag_info,
                out string strError);
            if (nRet == -1)
                return new NormalResult
                {
                    Value = -1,
                    ErrorInfo = strError
                };

            {
                entity.TagInfo = null;
                entity.PII = null;
                entity.FillFinished = false;

                // 刷新这一个 entity
                _ = Task.Run(async () =>
                {
                    BaseChannel<IRfid> channel = RfidManager.GetChannel();
                    try
                    {
                        await FillBookFieldsAsync(channel, new List<Entity>() { entity });
                    }
                    finally
                    {
                        RfidManager.ReturnChannel(channel);
                    }
                });
            }

            // 语音播报成功，自动关闭窗口
            this.Finished = true;
            App.CurrentApp.SpeakSequence($"写入完成");
            App.Invoke(new Action(() =>
            {
                // this.Comment = $"写入完成。PII={entity.PII}, UID={new_tag_info.UID}";
                this.CommentDocument = BuildDocument("写入完成", this.TaskInfo, 18);
                this.border.Background = new SolidColorBrush(Colors.DarkGreen);
            }));

            // 3 秒以后自动关闭对话框
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                if (this.LoopWriting == false)
                {
                    App.Invoke(new Action(() =>
                    {
                        this.Close();
                    }));
                }
                else
                {
                    // 继续下一轮循环
                    this.Finished = false;
                    this.TaskInfo = null;
                    App.Invoke(new Action(() =>
                    {
                        BeginComment();
                    }));
                }
            });

            return new NormalResult { Value = 1 };
        }

        public static LogicChip BuildChip(WriteTagTask task)
        {
            LogicChip result = new LogicChip();

            /*
            result.AFI = LogicChipItem.DefaultBookAFI;
            result.DSFID = LogicChipItem.DefaultDSFID;
            result.EAS = LogicChipItem.DefaultBookEAS;
            */

            // barcode --> PII
            result.NewElement(ElementOID.PII, task.PII);

            if (IsIsil(task.OI))
                result.NewElement(ElementOID.OwnerInstitution, task.OI);
            else
                result.NewElement(ElementOID.AlternativeOwnerInstitution, task.OI);

            // TypeOfUsage?
            // (十六进制两位数字)
            // 10 一般流通馆藏
            // 20 非流通馆藏。保存本库? 加工中?
            // 70 被剔旧的馆藏。和 state 元素应该有某种对应关系，比如“注销”
            {
                string typeOfUsage = "10";

                result.NewElement(ElementOID.TypeOfUsage, typeOfUsage);
            }

            // AccessNo --> ShelfLocation
            // 注意去掉 {ns} 部分
            result.NewElement(ElementOID.ShelfLocation,
                task.AccessNo);

            return result;
        }

        static bool IsIsil(string text)
        {
            // 所属机构ISIL由拉丁字母、阿拉伯数字（0-9），分隔符（-/:)组成，总长度不超过16个字符。
            if (DigitalPlatform.RFID.Compact.CheckIsil(text, false) == false)
                return false;

            string strError = VerifyOI(text);
            if (strError != null)
                return false;
            return true;
        }

        int SaveNewChip(
            string readerName,
            TagInfo tagInfo,
            LogicChip _right,
    out TagInfo new_tag_info,
    out string strError)
        {
            new_tag_info = null;
            strError = "";

            try
            {
                new_tag_info = ToTagInfo(
                    tagInfo,
                    _right);
                NormalResult result = RfidManager.WriteTagInfo(
    readerName,
    tagInfo,
    new_tag_info);
                ShelfData.PatronTagList.ClearTagTable(new_tag_info.UID);
                // 2023/10/31
                if (tagInfo.Protocol == InventoryInfo.ISO18000P6C)
                    ShelfData.PatronTagList.ClearTagTable(new_tag_info.UID);

                // 迫使所有标签都重新获取和显示一次
                // ShelfData.PatronTagList.Clear();

                if (result.Value == -1)
                {
                    strError = result.ErrorInfo + $" errorCode={result.ErrorCode}";
                    return -1;
                }

                return 0;
            }
            catch (Exception ex)
            {
                WpfClientInfo.WriteErrorLog($"SaveNewChip() 出现异常: {ExceptionUtil.GetDebugText(ex)}");
                strError = "SaveNewChip() 出现异常: " + ex.Message;
                return -1;
            }
        }

        public static TagInfo ToTagInfo(TagInfo existing,
    LogicChip chip)
        {
            TagInfo new_tag_info = existing.Clone();
            new_tag_info.Bytes = chip.GetBytes(
                (int)(new_tag_info.MaxBlockCount * new_tag_info.BlockSize),
                (int)new_tag_info.BlockSize,
                LogicChip.GetBytesStyle.None,
                out string block_map);
            new_tag_info.LockStatus = block_map;

            new_tag_info.AFI = LogicChip.DefaultBookAFI;
            new_tag_info.DSFID = LogicChip.DefaultDSFID;
            new_tag_info.EAS = LogicChip.DefaultBookEAS;
            return new_tag_info;
        }

        static bool IsBlank(Entity entity)
        {
            if (entity.TagInfo == null)
                return false;
            // Exception:
            //      可能会抛出异常 ArgumentException TagDataException
            LogicChip chip = LogicChip.From(entity.TagInfo.Bytes,
(int)entity.TagInfo.BlockSize,
"" // tag.TagInfo.LockStatus
);
            return chip.IsBlank();
        }

        #region

        /*
OI的校验，总长度不超过16位。
2位国家代码前缀-6位中国行政区划代码-1位图书馆类型代码-图书馆自定义码（最长4位）
 * */
        public static string VerifyOI(string oi)
        {
            if (string.IsNullOrEmpty(oi))
                return "机构代码不应为空";

            if (oi.Length > 16)
                return $"机构代码 '{oi}' 不合法: 总长度不应超过 16 字符";

            var parts = oi.Split(new char[] { '-' });
            if (parts.Length != 4)
                return $"机构代码 '{oi}' 不合法: 应为 - 间隔的四个部分形态";
            string country = parts[0];
            if (country != "CN")
                return $"机构代码 '{oi}' 不合法: 第一部分国家代码 '{country}' 不正确，应为 'CN'";
            string region = parts[1];
            if (region.Length != 6
                || StringUtil.IsPureNumber(region) == false)
                return $"机构代码 '{oi}' 不合法: 第二部分行政区代码 '{region}' 不正确，应为 6 位数字";
            string type = parts[2];
            if (type.Length != 1
    || VerifyType(type[0]) == false)
                return $"机构代码 '{oi}' 不合法: 第三部分图书馆类型代码 '{type}' 不正确，应为 1 位字符(取值范围为 1-9,A-F)";
            string custom = parts[3];
            if (custom.Length < 1 || custom.Length > 4
                || IsLetterOrDigit(custom) == false)
                return $"机构代码 '{oi}' 不合法: 第四部分图书馆自定义码 '{custom}' 不正确，应为 1-4 位数字或者大写字母";

            return null;
        }

        static bool VerifyType(char ch)
        {
            if (ch >= '1' && ch <= '9')
                return true;
            if (ch >= 'A' && ch <= 'F')
                return true;
            return false;
        }

        static bool IsLetterOrDigit(string text)
        {
            foreach (char ch in text)
            {
                if (char.IsLetter(ch) && char.IsUpper(ch) == false)
                    return false;
                if (char.IsLetterOrDigit(ch) == false)
                    return false;
            }

            return true;
        }


        #endregion

        private void writeButton_Click(object sender, RoutedEventArgs e)
        {
            this.writeButton.IsEnabled = false;

            if (this.TaskInfo == null)
            {
                App.ErrorBox("写标签时出错", "请先扫图书册条码，再选择标签写入");
                return;
            }

            Entity entity = this.booksControl.SelectedItem as Entity;

            var write_result = WriteEntity(entity,
                this.TaskInfo);
            if (write_result.Value == -1)
            {
                App.ErrorBox("写标签时出错", write_result.ErrorInfo);
                return;
            }

            PageShelf.TrySetMessage(null, $"写入 RFID 成功。\r\n{TaskInfo?.GetOiPii()}\r\n{TaskInfo?.Title}");
        }

        // 根据 PII 准备好 TaskInfo
        public async Task<NormalResult> PrepareTaskAsync(string pii)
        {
            // 检索出指定 PII 的图书信息
            // .Value
            //      -1  出错
            //      0   没有找到
            //      1   找到
            var get_result = await LibraryChannelUtil.GetEntityDataAsync(pii, "network");
            if (get_result.Value == -1)
                return new NormalResult
                {
                    Value = -1,
                    ErrorInfo = get_result.ErrorInfo
                };
            if (get_result.Value == 0)
                return new NormalResult
                {
                    Value = -1,
                    ErrorInfo = $"册记录 '{pii}' 没有找到",
                    ErrorCode = "itemNotFound"
                };
            this.TaskInfo = BuildWriteTagTask(get_result);
            return new NormalResult();
        }

        static WriteTagTask BuildWriteTagTask(LibraryChannelUtil.GetEntityDataResult result)
        {
            System.Xml.XmlDocument dom = new System.Xml.XmlDocument();
            dom.LoadXml(result.ItemXml);

            string pii = DomUtil.GetElementText(dom.DocumentElement, "barcode");
            string oi = DomUtil.GetElementText(dom.DocumentElement, "oi");
            string accessNo = DomUtil.GetElementText(dom.DocumentElement, "accessNo");
            WriteTagTask task = new WriteTagTask
            {
                PII = pii,
                OI = oi,
                AccessNo = accessNo,
                Title = result.Title,
            };
            return task;
        }

        static FlowDocument BuildDocument(
            string comment,
            WriteTagTask taskInfo,
            double baseFontSize)
        {
            FlowDocument doc = new FlowDocument();
            {
                var p = new Paragraph();
                p.FontFamily = new FontFamily("微软雅黑");
                p.FontSize = baseFontSize * 1.6F;
                p.TextAlignment = TextAlignment.Center;
                p.Margin = new Thickness(baseFontSize * 0.5F);
                doc.Blocks.Add(p);

                p.Inlines.Add(new Run
                {
                    Text = comment,
                    Foreground = Brushes.LightGray,
                });
            }

            {
                var p = new Paragraph();
                // p.FontFamily = new FontFamily("Courier New");
                p.FontSize = baseFontSize;
                p.TextAlignment = TextAlignment.Left;
                p.Padding = new Thickness(baseFontSize);
                p.Background = new SolidColorBrush(Color.FromRgb(60, 60, 60));
                doc.Blocks.Add(p);

                p.Inlines.Add(new Run
                {
                    Text = taskInfo.OI + "." + taskInfo.PII,
                    FontFamily = new FontFamily("Arial"),
                    FontWeight = FontWeights.Bold,
                    FontSize = baseFontSize * 1.2F,
                    Foreground = Brushes.Yellow,
                });

                p.Inlines.Add(new Run
                {
                    Text = "\r\n" + taskInfo.Title,
                    FontFamily = new FontFamily("微软雅黑"),
                    FontSize = baseFontSize * 0.8F,
                    Foreground = Brushes.White,
                });
            }

            return doc;
        }
    }

    // 任务信息
    public class WriteTagTask
    {
        // PII
        public string PII { get; set; }
        // 机构代码
        public string OI { get; set; }
        // 索取号
        public string AccessNo { get; set; }
        // 书名
        public string Title { get; set; }

        // 任务完成时是否自动关闭对话框
        public bool AutoCloseDialog { get; set; }

        public string GetOiPii()
        {
            if (string.IsNullOrEmpty(OI))
                return PII;
            return OI + "." + PII;
        }
    }
}
