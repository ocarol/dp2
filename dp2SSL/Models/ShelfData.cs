﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Xml;
using System.Windows.Markup;

using Newtonsoft.Json;

using Microsoft.VisualStudio.Threading;

using static dp2SSL.LibraryChannelUtil;
using dp2SSL.Models;

using DigitalPlatform;
using DigitalPlatform.WPF;
using DigitalPlatform.IO;
using DigitalPlatform.RFID;
using DigitalPlatform.Text;
using DigitalPlatform.LibraryClient;
using DigitalPlatform.LibraryClient.localhost;
using DigitalPlatform.LibraryServer;
using DigitalPlatform.Xml;
using static DigitalPlatform.RFID.LogicChip;

namespace dp2SSL
{
    /// <summary>
    /// 智能书架要用到的数据
    /// </summary>
    public static partial class ShelfData
    {
        public static NewTagList BookTagList = new NewTagList();
        public static NewTagList PatronTagList = new NewTagList();

#if DOOR_MONITOR
        public static DoorMonitor DoorMonitor = null;
#endif
        #region CancellationTokenSource

        static CancellationTokenSource _cancel = new CancellationTokenSource();

        public static CancellationToken CancelToken
        {
            get
            {
                return _cancel.Token;
            }
        }

        public static void CancelAll()
        {
            _cancel.Cancel();
        }

        #endregion

        public static event OpenCountChangedEventHandler OpenCountChanged;

        public static event DoorStateChangedEventHandler DoorStateChanged;

        /*
        public static event BookChangedEventHandler BookChanged;

        public static void TriggerBookChanged(BookChangedEventArgs e)
        {
            BookChanged?.Invoke(null, e);
        }
        */

        // 读者证读卡器名字。在 shelf.xml 中配置
        static string _patronReaderName = "";

        public static string PatronReaderName
        {
            get
            {
                return _patronReaderName;
            }
        }


        // 图书读卡器名字列表(也就是柜门里面的那些读卡器)
        static string _allDoorReaderName = "";

        public static string DoorReaderName
        {
            get
            {
                return _allDoorReaderName;
            }
        }

        // 当前处于打开状态的门的个数
        public static int OpeningDoorCount
        {
            get
            {
                return _openingDoorCount;
            }
        }

        // 是否全部柜门都在关闭状态？
        public static bool IsAllDoorClosed(out string message)
        {
            message = "";
            if (_openingDoorCount > 0)
            {
                message = $"有 {_openingDoorCount} 个门尚未关闭";
                return false;
            }

            {
                int count = 0;
                foreach (var door in Doors)
                {
                    if (door.Waiting != 0)
                        count++;
                }
                if (count != 0)
                {
                    message = $"有 {count} 个门正在处理事务";
                    return false;
                }
            }

            {
                int count = DoorStateTask.GetListCount();
                if (count > 0)
                {
                    message = $"有 {count} 个后台事务正在处理中";
                    return false;
                }
            }

            return true;
        }

        /*
        public static void ProcessOpenCommand(DoorItem door, string comment)
        {
            // 切换所有者
            var command = ShelfData.PopCommand(door, comment);
            if (command != null)
            {
                door.DecWaiting();
                //WpfClientInfo.WriteInfoLog($"--decWaiting() door '{door.Name}' pop command");
                door.Operator = command.Parameter as Operator;
            }
            else
            {
                WpfClientInfo.WriteErrorLog($"!!! 门 {door.Name} PopCommand() 时候没有找到命令对象");
            }
        }
        */

        static int _openingDoorCount = -1; // 当前处于打开状态的门的个数。-1 表示个数尚未初始化

        // 已经语音提醒过的读者
        // 读者 PII --> DateTime 最近提醒时间
        static Hashtable _notifiedPatronTable = new Hashtable();
        public static bool HasNotified(string pii)
        {
            if (string.IsNullOrEmpty(pii))
                return false;

            lock (_notifiedPatronTable.SyncRoot)
            {
                // 防止规模太大
                if (_notifiedPatronTable.Count > 100)
                    _notifiedPatronTable.Clear();

                if (_notifiedPatronTable.ContainsKey(pii))
                {
                    _notifiedPatronTable[pii] = DateTime.Now;
                    return true;
                }

                _notifiedPatronTable[pii] = DateTime.Now;
                return false;
            }
        }

        // 预先加入开门后要说的话
        static List<string> _openDoorSpeakList = new List<string>();
        public static void AddOpenDoorSpeak(string text)
        {
            _openDoorSpeakList.Add(text);
        }

        public static void ClearOpenDoorSpeak()
        {
            _openDoorSpeakList.Clear();
        }

        // 获得开门后要说的话
        public static string GetOpenDoorSpeakText()
        {
            return StringUtil.MakePathList(_openDoorSpeakList, ", ");
        }

        // 把状态变换为比较简单的形态。"open,close" 要拆成两个
        static List<LockState> ConvertLockStates(List<LockState> states)
        {
            List<LockState> results = new List<LockState>();
            foreach (var state in states)
            {
                if (state.State.Contains(","))
                {
                    var parts = StringUtil.SplitList(state.State);
                    foreach (var part in parts)
                    {
                        /*
        public string Path { get; set; }
        public string Lock { get; set; }
        public int Board { get; set; }
        public int Index { get; set; }
        public string State { get; set; }
                        * */
                        results.Add(new LockState
                        {
                            Path = state.Path,
                            Lock = state.Lock,
                            Board = state.Board,
                            Index = state.Index,
                            State = part
                        });
                    }
                }
                else
                    results.Add(state);
            }

            return results;
        }

        public static void RfidManager_ListLocks(object sender, ListLocksEventArgs e)
        {
            if (e.Result.Value == -1)
            {
                // TODO: 注意这里的信息量很大，需要防止错误日志空间被耗尽
                //WpfClientInfo.WriteErrorLog($"RfidManager ListLocks error: {e.Result.ErrorInfo}");
                return;
            }

            if (e.Result.ErrorCode == "retryWarning")
                App.SetError("shelfLock", "警告: " + e.Result.ErrorInfo);
            else
                App.SetError("shelfLock", null);

            // List<DoorItem> processed = new List<DoorItem>();
            // bool triggerAllClosed = false;
            {
                int count = 0;
                // 转为单纯形态
                var states = ConvertLockStates(e.Result.States);
                foreach (var state in states)
                {
                    // TODO: 这里有重复计算 count 的风险
                    if (state.State == "open")
                        count++;

                    // 刷新门锁对象的 State 状态
                    var results = DoorItem.SetLockState(ShelfData.Doors, state);

                    // 2020/11/26
                    if (ShelfData.FirstInitialized == false)
                        continue;

                    // 注：有可能一个锁和多个门关联
                    foreach (LockChanged result in results)
                    {
                        if (result.NewState != result.OldState
                            && string.IsNullOrEmpty(result.OldState) == false)
                        {
                            /*
                            // 触发单独一个门被关闭的事件
                            // 注意此时 door 对象的 State 状态已经变化为新状态了
                            DoorStateChanged?.Invoke(null, new DoorStateChangedEventArgs
                            {
                                Door = result.Door,
                                OldState = result.OldState,
                                NewState = result.NewState
                            });
                            */

                            {
                                string text = "";
                                if (result.NewState == "open")
                                    text = $"门 '{result.Door.Name}' 被 {result.Door.Operator?.GetDisplayStringMasked()} 打开";
                                else
                                    text = $"门 '{result.Door.Name}' 被 {result.Door.Operator?.GetDisplayStringMasked()} 关上";
                                PageShelf.TrySetMessage(null, text);
                            }

                            // 2021/9/26
                            // 有没有可能连续发来两次 open 信号?
                            if (StringUtil.IsInList("open", result.NewState))
                            {
                                result.Door.OpenTime = DateTime.Now;

                                // 2021/9/28
                                // 记忆最近开门者
                                if (result.Door.Operator != null)
                                    ShelfData.MemoryOpen(result.Door, result.Door.Operator);

                                // 2021/9/26
                                if (result.Door.Waiting <= 0)
                                {
                                    var current_state = result.Door.State;
                                    WpfClientInfo.WriteErrorLog($"收到门 '{result.Door.Name}' 打开信号时，Waiting 为异常值 {result.Door.Waiting}(正常值应为 >= 1)，这意味着稍早并没有配套的 IncWaiting() 动作(很可能是关门未关严放手又弹开造成)。此次 DecWaiting() 被忽略。(诊断信息：当前门状态为 '{current_state}')");

                                    // 2021/9/28
                                    // 记忆最近开门者
                                    if (result.Door.Operator == null)
                                    {
                                        var info = ShelfData.GetOpenInfo(result.Door);
                                        if (info == null)
                                            WpfClientInfo.WriteErrorLog($"此时门 '{result.Door.Name}' 没有开门者信息，这意味着后面关门时如果需要构建借书请求到时候将会出错");
                                        else
                                        {
                                            result.Door.Operator = info.Operator;
                                            WpfClientInfo.WriteErrorLog($"dp2ssl 权且利用最近一次开门 '{result.Door.Name}' 期间({info.OpenTime.ToString()},距当前时刻 {(DateTime.Now - info.OpenTime).ToString()})的操作者 {info.Operator.ToString()} 充当本次开门的操作者。但这样做不一定正确，请注意核实操作者");
                                        }
                                    }
                                }
                                else
                                {
                                    // 2020/11/21
                                    // 门收到打开信号后，停止等待动画
                                    result.Door.DecWaiting();
                                    WpfClientInfo.WriteInfoLog($"--decWaiting() door '{result.Door.Name}' in _listLocks() (收到门打开信号)");
                                }

                                /*
                                // 添加一个表示开门动作的(状态变化)事项
                                DoorStateTask.AppendList(new DoorStateTask.DoorStateChange
                                {
                                    Door = result.Door,
                                    OldState = result.OldState,
                                    NewState = "open",
                                });
                                DoorStateTask.ActivateTask();
                                */

                                // 开门后 播放预先准备好的语音提示
                                {
                                    App.CurrentApp.SpeakSequence($"{result.LockName} 打开。{GetOpenDoorSpeakText()}");
                                    ClearOpenDoorSpeak();
                                }
                            }

                            if (StringUtil.IsInList("close", result.NewState))
                            {
                                // 2021/9/28
                                // 记忆最近开门者
                                if (result.Door.Operator != null)
                                    ShelfData.MemoryOpen(result.Door, result.Door.Operator);
                                else
                                    WpfClientInfo.WriteErrorLog($"*** 警告 ***: 门 '{result.Door.Name}' 收到关门信号时发现没有开门者信息。预期后面构建动作对象时可能会出错");

                                // List<ActionInfo> actions = null;
                                // 2019/12/15
                                // 补做一次 inventory，确保不会漏掉 RFID 变动信息
                                WpfClientInfo.WriteInfoLog($"++incWaiting() door '{result.Door.Name}' in _listLocks() (收到门关闭信号，开始进行变化处理)");
                                result.Door.IncWaiting();  // inventory 期间显示等待动画

                                DoorStateTask.AppendList(new DoorStateTask.DoorStateChange
                                {
                                    Door = result.Door,
                                    OldState = result.OldState,
                                    NewState = "close",
                                    NeedDecCount = true,
                                });
                                DoorStateTask.ActivateTask();

                                // 语音提示
                                App.CurrentApp.SpeakSequence($"{result.LockName} 关闭");
                            }

                            // processed.Add(result.Door);
                        }
                    }
                }

                //if (_openingDoorCount > 0 && count == 0)
                //    triggerAllClosed = true;

                SetOpenCount(count);
            }

#if DOOR_MONITOR
            ShelfData.DoorMonitor?.ProcessTimeout();
#endif

#if REMOVED
            // TODO: 如果刚才已经获得了一个门锁的关门信号，则后面不要重复触发 DoorStateChanged 

            // 2019/12/16
            // 对可能遗漏 Pop 的 命令进行检查
            {
                // 检查命令队列中可能被 getLockState 轮询状态所遗漏的命令
                var missing_commands = CheckCommands(RfidManager.LockHeartbeat);
                if (missing_commands.Count > 0)
                {
                    foreach (var command in missing_commands)
                    {
                        // 如果此门状态不是关闭状态，则不需要进行修补处理
                        if (command.Door.State != "close")
                            continue;

                        // 2019/12/18
                        // 前面正常处理流程已经触发过这个门的状态变化事件了
                        if (processed.IndexOf(command.Door) != -1)
                            continue;

                        // ProcessOpenCommand(command.Door);
                        // 补一次门状态变化? --> open --> close
                        // 触发单独一个门被关闭的事件
                        DoorStateChanged?.Invoke(null, new DoorStateChangedEventArgs
                        {
                            Door = command.Door,
                            OldState = "close",
                            NewState = "open",
                            Comment = $"补做。Heartbeat={RfidManager.LockHeartbeat}",
                        });
                        DoorStateChanged?.Invoke(null, new DoorStateChangedEventArgs
                        {
                            Door = command.Door,
                            OldState = "open",
                            NewState = "close",
                            Comment = $"补做。Heartbeat={RfidManager.LockHeartbeat}",
                        });
                        App.CurrentApp.Speak("补做检查");   // 测试完成后可以取消这个语音
                        WpfClientInfo.WriteInfoLog($"提醒：检查过程为门 '{command.Door.Name}' 补做了一次 open 和 一次 close。Heartbeat:{RfidManager.LockHeartbeat}");
                    }
                }
            }
#endif
        }

        // 设置打开门数量
        static void SetOpenCount(int count)
        {
            int oldCount = _openingDoorCount;

            _openingDoorCount = count;

            // 打开门的数量发生变化
            if (oldCount != _openingDoorCount)
            {
                OpenCountChanged?.Invoke(null, new OpenCountChangedEventArgs
                {
                    OldCount = oldCount,
                    NewCount = count
                });

                // 
                RefreshReaderNameList();
            }
        }

        #region 记忆最近开门的操作者

        public class OpenInfo
        {
            public DoorItem Door { get; set; }
            public DateTime OpenTime { get; set; }
            public Operator Operator { get; set; }
        }
        // DoorItem --> OpenInfo
        static Hashtable _openTable = new Hashtable();

        public static void MemoryOpen(
            DoorItem door,
            Operator person)
        {
            lock (_openTable.SyncRoot)
            {
                var info = new OpenInfo
                {
                    Door = door,
                    Operator = person,
                    OpenTime = DateTime.Now
                };
                _openTable[door] = info;
            }
        }

        public static OpenInfo GetOpenInfo(DoorItem door)
        {
            lock (_openTable.SyncRoot)
            {
                return _openTable[door] as OpenInfo;
            }
        }

        #endregion


        // 保存一个已经打开的灯的门名字表。只要有一个以上事项，就表示要开灯；如果一个事项也没有，就表示要关灯
        // 门名字 --> bool
        static Hashtable _lampTable = new Hashtable();

        public static bool GetLampState(string doorName)
        {
            lock (_lampTable.SyncRoot)
            {
                if (_lampTable.ContainsKey(doorName) == false)
                    return false;
                return (bool)_lampTable[doorName];
            }
        }

        // parameters:
        //      style   on 或者 off
        //              delay   表示延迟关灯
        //              skip 表示不真正开关物理灯，只是改变 hashtable 里面计数
        public static void TurnLamp(string doorName, string style)
        {
            bool refresh = StringUtil.IsInList("refresh", style);

            int oldCount = 0;
            int newCount = 0;
            lock (_lampTable.SyncRoot)
            {
                oldCount = _lampTable.Count;

                if (refresh == false)
                {
                    bool on = StringUtil.IsInList("on", style);
                    if (on)
                        _lampTable[doorName] = true;
                    else
                        _lampTable.Remove(doorName);

                    newCount = _lampTable.Count;
                }
            }

            if (refresh)
            {
                string action = oldCount > 0 ? "turnOn" : "turnOff";
                WpfClientInfo.WriteInfoLog($"物理 {action} 灯 (refresh)");
                var result = RfidManager.TurnShelfLamp("*", action);
                if (result.Value == -1 && result.ErrorCode != "notFound")
                {
                    WpfClientInfo.WriteErrorLog($"RfidManager.TurnShelfLamp({action}) (refresh 时) 出错: {result.ErrorInfo}");
                }
                else
                {
                    if (result.Value == -1 && result.ErrorCode == "notFound")
                        WpfClientInfo.WriteInfoLog($"虽然返回出错，但模拟灯的控件依然亮起。RfidManager.TurnShelfLamp({action}) (refresh 时) 出错: {result.ErrorInfo}");

                    // 用控件模拟灯亮灭，便于调试
                    PageMenu.PageShelf?.SimulateLamp(action == "turnOn" ? true : false);
                }
                return;
            }

            if (oldCount == 0 && newCount > 0)
            {
                if (StringUtil.IsInList("skip", style) == false)
                {
                    WpfClientInfo.WriteInfoLog("物理开灯");
                    var result = RfidManager.TurnShelfLamp("*", "turnOn");
                    if (result.Value == -1 && result.ErrorCode != "notFound")
                    {
                        WpfClientInfo.WriteErrorLog($"RfidManager.TurnShelfLamp(turnOn) 出错: {result.ErrorInfo}");
                    }
                    else
                    {
                        // 用控件模拟灯亮灭，便于调试
                        PageMenu.PageShelf?.SimulateLamp(true);
                    }
                }
            }
            else if (oldCount > 0 && newCount == 0)
            {
                if (StringUtil.IsInList("delay", style))
                    BeginDelayTurnOffTask();
                else
                {
                    WpfClientInfo.WriteInfoLog("物理关灯");
                    var result = RfidManager.TurnShelfLamp("*", "turnOff");
                    if (result.Value == -1 && result.ErrorCode != "notFound")
                    {
                        WpfClientInfo.WriteErrorLog($"RfidManager.TurnShelfLamp(turnOff) 出错: {result.ErrorInfo}");
                    }
                    else
                    {
                        // 用控件模拟灯亮灭，便于调试
                        PageMenu.PageShelf.SimulateLamp(false);
                    }
                }
            }
        }

        #region 延迟关灯

        static DelayAction _delayTurnOffTask = null;

        public static void CancelDelayTurnOffTask()
        {
            if (_delayTurnOffTask != null)
            {
                _delayTurnOffTask.Stop();
                _delayTurnOffTask = null;
            }
        }

        public static void BeginDelayTurnOffTask()
        {
            CancelDelayTurnOffTask();

            // 让灯继续亮着
            ShelfData.TurnLamp("~", "on,skip");

            // TODO: 开始启动延时自动清除读者信息的过程。如果中途门被打开，则延时过程被取消(也就是说读者信息不再会被自动清除)
            _delayTurnOffTask = DelayAction.Start(
                20,
                () =>
                {
                    ShelfData.TurnLamp("~", "off");
                },
                (seconds) =>
                {
                    /*
                    Application.Current.Dispatcher.Invoke(new Action(() =>
                    {
                        if (seconds > 0)
                            this.clearPatron.Content = $"({seconds.ToString()} 秒后自动) 清除读者信息";
                        else
                            this.clearPatron.Content = $"清除读者信息";
                    }));
                    */
                });
        }

        #endregion


        public static void RefreshReaderNameList()
        {
            if (_openingDoorCount == 0)
            {
                /*
                // 关闭图书读卡器(只使用读者证读卡器)
                if (string.IsNullOrEmpty(_patronReaderName) == false
                    && RfidManager.ReaderNameList != _patronReaderName)
                {
                    // RfidManager.ReaderNameList = _patronReaderName;
                    RfidManager.ReaderNameList = "";    // 图书读卡器全部停止盘点。此处假定读者证读卡器在第二线程遍历
                    RfidManager.ClearCache();
                    //App.CurrentApp.SpeakSequence("静止");
                }
                */
                // 关闭图书读卡器(只使用读者证读卡器)
                if (RfidManager.ReaderNameList != "")
                {
                    // RfidManager.ReaderNameList = _patronReaderName;
                    RfidManager.ReaderNameList = "";    // 图书读卡器全部停止盘点。此处假定读者证读卡器在第二线程遍历
                    RfidManager.ClearCache();
                    //App.CurrentApp.SpeakSequence("静止");
                }

            }
            else
            {
                string list = "";
                /*
                if (App.DetectBookChange == true)
                    list = GetReaderNameList(Doors,
                        (d) =>
                        {
                            return (d.State == "open");
                        });
                */

                // 打开图书读卡器(同时也使用读者证读卡器)
                if (RfidManager.ReaderNameList != list)
                {
                    RfidManager.ReaderNameList = list;
                    RfidManager.ClearCache();
                    //App.CurrentApp.SpeakSequence("活动");
                }
            }
        }

        // 图书馆名字
        static string _libraryName = null;

        static internal List<string> _locationList = null;

        static string _rightTableXml = null;

        // 2020/7/15
        // 从 dp2library library.xml 中获取的 RFID 配置信息
        static XmlDocument _rfidCfgDom = null;

        // 2022/3/17
        // library.xml 中 rfid 元素定义
        public static string RfidXml
        {
            get
            {
                if (_rfidCfgDom == null || _rfidCfgDom.DocumentElement == null)
                    return "";
                return _rfidCfgDom.DocumentElement.OuterXml;
            }
        }

        // exception:
        //      可能会抛出异常
        public static NormalResult InitialShelf()
        {
            if (App.Protocol == "sip")
                return new NormalResult
                {
                    Value = -1,
                    ErrorInfo = "当前版本暂不支持智能书柜连接 SIP2 服务器"
                };

            // 初始化软时钟
            try
            {
                ShelfData.LoadSoftClock();
                if (ShelfData.LibraryNetworkCondition == "OK")
                {
                    var result = LibraryChannelUtil.VerifyClock();
                    if (result.Value == -1)
                    {
                        WpfClientInfo.WriteErrorLog($"首次校正本地软时钟时出错: {result.ErrorInfo}");
                    }
                    else
                        ShelfData.SetSoftClock(result.DeltaTicks);
                }
            }
            catch (Exception ex)
            {
                WpfClientInfo.WriteErrorLog($"初始化本地软时钟时出现异常: {ExceptionUtil.GetDebugText(ex)}");
            }

            try
            {
                ShelfData.InitialDoors();
            }
            catch (Exception ex)
            {
                WpfClientInfo.WriteErrorLog($"InitialShelf() 出现异常: {ExceptionUtil.GetDebugText(ex)}");
                return new NormalResult
                {
                    Value = -1,
                    ErrorInfo = $"InitialDoors() 出现异常: {ex.Message}"
                };
            }

            {
                // 获得馆藏地列表
                GetLocationListResult result = null;
                if (App.StartNetworkMode == "local")
                {
                    result = LibraryChannelUtil.GetLocationListFromLocal();
                    if (result.Value == 0)
                        return new NormalResult
                        {
                            Value = -1,
                            ErrorInfo = "本地没有馆藏地定义信息。需要联网以后重新启动"
                        };
                }
                else
                    result = LibraryChannelUtil.GetLocationList();

                if (result.Value == -1)
                    return new NormalResult
                    {
                        Value = -1,
                        ErrorInfo = $"获得馆藏地列表时出错: {result.ErrorInfo}"
                    };
                else
                    _locationList = result.List;
            }

            {
                _rfidCfgDom = new XmlDocument();

                // 获得 RFID 配置信息
                GetRfidCfgResult result = null;
                if (App.StartNetworkMode == "local")
                {
                    result = LibraryChannelUtil.GetRfidCfgFromLocal();
                    if (result.Value == 0)
                        return new NormalResult
                        {
                            Value = -1,
                            ErrorInfo = "本地没有 RFID 配置信息。需要联网以后重新启动"
                        };
                }
                else
                    result = LibraryChannelUtil.GetRfidCfg();

                if (result.Value == -1)
                    return new NormalResult
                    {
                        Value = -1,
                        ErrorInfo = $"从 dp2library 服务器获得 RFID 配置信息时出错: {result.ErrorInfo}"
                    };
                else
                {
                    if (string.IsNullOrEmpty(result.Xml))
                    {
                        return new NormalResult
                        {
                            Value = -1,
                            ErrorInfo = $"从 dp2library 服务器获得 RFID 配置信息时出错: library.xml 中没有定义 rfid 元素"
                        };
                    }
                    _rfidCfgDom = new XmlDocument();
                    _rfidCfgDom.LoadXml(result.Xml);

                    _libraryName = result.LibraryName;

                    if (result.XmlChanged)
                    {
                        WpfClientInfo.WriteInfoLog($"[2] 探测到 library.xml 中 rfid 发生变化。\r\n变化前的: {result.OldXml}\r\n变化后的: {result.Xml}");
                        // 触发重新全量下载册和读者记录
                        ShelfData.TriggerDownloadEntitiesAndPatrons();
                    }
                }
            }

            if (App.StartNetworkMode == "local")
            {
                _rightTableXml = WpfClientInfo.Config.Get("cache", "rightsTable", null);
                if (string.IsNullOrEmpty(_rightTableXml))
                    return new NormalResult
                    {
                        Value = -1,
                        ErrorInfo = "本地没有读者借阅权限定义信息。需要联网以后重新启动"
                    };
            }
            else
            {
                var get_result = GetRightsTableFromServer();
                if (get_result.Value == -1)
                    return get_result;
                /*
                // 获得读者借阅权限定义
                GetRightsTableResult get_result = LibraryChannelUtil.GetRightsTable();
                if (get_result.Value == -1)
                    return new NormalResult
                    {
                        Value = -1,
                        ErrorInfo = $"获得读者借阅权限定义 XML 时出错: {get_result.ErrorInfo}"
                    };
                _rightTableXml = get_result.Xml;
                // 顺便保存起来
                WpfClientInfo.Config.Set("cache", "rightsTable", _rightTableXml);
                */
            }

            // 要在初始化以前设定好
            _patronReaderName = GetAllReaderNameList("patron");
            WpfClientInfo.WriteInfoLog($"patron ReaderNameList '{_patronReaderName}'");

            RfidManager.Base2ReaderNameList = _patronReaderName;    // 2019/11/18
            // RfidManager.LockThread = "base2";   // 使用第二个线程来监控门锁

            _allDoorReaderName = GetAllReaderNameList("doors");
            WpfClientInfo.WriteInfoLog($"doors ReaderNameList '{_allDoorReaderName}'");

#if OLD_VERSION
            RfidManager.ReaderNameList = _allDoorReaderName;
#else
            RfidManager.ReaderNameList = "";    // 假定一开始门是关闭的
#endif

            RfidManager.LockCommands = StringUtil.MakePathList(ShelfData.GetLockCommands());

            WpfClientInfo.WriteInfoLog($"LockCommands '{RfidManager.LockCommands}'");

            // _patronReaderName = GetPatronReaderName();
            return new NormalResult();
        }

        public static void TriggerDownloadEntitiesAndPatrons()
        {
            WpfClientInfo.WriteInfoLog("因感知到 library.xml rfid 元素变化，触发重新全量下载册记录和读者记录");
            App.CurrentApp.SpeakSequence("重新全量下载册记录和读者记录");

            // 停止可能正在进行的长操作
            ShelfData.StopDownloadPatron();
            ShelfData.StopDownloadEntity();

            // 重做
            ShelfData.RedoReplicatePatron();
            ShelfData.RestartReplicateEntities();
        }

        public static NormalResult GetRightsTableFromServer()
        {
            // 获得读者借阅权限定义
            GetRightsTableResult get_result = LibraryChannelUtil.GetRightsTable();
            if (get_result.Value == -1)
                return new NormalResult
                {
                    Value = -1,
                    ErrorInfo = $"获得读者借阅权限定义 XML 时出错: {get_result.ErrorInfo}"
                };
            _rightTableXml = get_result.Xml;
            // 顺便保存起来
            WpfClientInfo.Config.Set("cache", "rightsTable", _rightTableXml);
            return new NormalResult();
        }

        // parameters:
        //      cfg_dom 根元素是 rfid
        //      strLocation 纯净的 location 元素内容。
        //      isil    [out] 返回 ISIL 形态的代码
        //      alternative [out] 返回其他形态的代码
        // return:
        //      true    找到。信息在 isil 和 alternative 参数里面返回
        //      false   没有找到
        public static bool GetOwnerInstitution(
            // XmlDocument cfg_dom,
            string strLocation,
            out string isil,
            out string alternative)
        {
            isil = "";
            alternative = "";

        REDO:
            var cfg_dom = _rfidCfgDom;

            if (cfg_dom == null)
            {
                var prepare_result = EnsureConfigDom();
                if (prepare_result.Value == -1)
                    throw new Exception(prepare_result.ErrorInfo);
                goto REDO;
                // return false;
            }

            if (cfg_dom.DocumentElement == null)
                return false;

            return LibraryServerUtil.GetOwnerInstitution(cfg_dom.DocumentElement,
                strLocation,
                "entity",
                out isil,
                out alternative);
        }

        // 专用于读者记录
        public static bool GetOwnerInstitution(
    string libraryCode,
    XmlDocument readerdom,
    out string isil,
    out string alternative)
        {
            isil = "";
            alternative = "";

        REDO:
            var cfg_dom = _rfidCfgDom;

            if (cfg_dom == null)
            {
                var prepare_result = EnsureConfigDom();
                if (prepare_result.Value == -1)
                    throw new Exception(prepare_result.ErrorInfo);
                goto REDO;
                // return false;
            }

            if (cfg_dom.DocumentElement == null)
                return false;

            return LibraryServerUtil.GetOwnerInstitution(cfg_dom.DocumentElement,
                libraryCode,
                readerdom,
                out isil,
                out alternative);
        }

#if REMOVED
        /*
<rfid>
<ownerInstitution>
<item map="海淀分馆/" isil="test" />
<item map="西城/" alternative="xc" />
</ownerInstitution>
</rfid>
map 为 "/" 或者 "/阅览室" 可以匹配 "图书总库" "阅览室" 这样的 strLocation
map 为 "海淀分馆/" 可以匹配 "海淀分馆/" "海淀分馆/阅览室" 这样的 strLocation
最好单元测试一下这个函数
 * */
        // parameters:
        //      cfg_dom 根元素是 rfid
        //      strLocation 纯净的 location 元素内容。
        //      isil    [out] 返回 ISIL 形态的代码
        //      alternative [out] 返回其他形态的代码
        // return:
        //      true    找到。信息在 isil 和 alternative 参数里面返回
        //      false   没有找到
        public static bool GetOwnerInstitution(
            // XmlDocument cfg_dom,
            string strLocation,
            out string isil,
            out string alternative)
        {
            isil = "";
            alternative = "";

        REDO:
            var cfg_dom = _rfidCfgDom;

            if (cfg_dom == null)
            {
                var prepare_result = PrepareConfigDom();
                if (prepare_result.Value == -1)
                    throw new Exception(prepare_result.ErrorInfo);
                goto REDO;
                // return false;
            }

            if (cfg_dom.DocumentElement == null)
                return false;

            // 分析 strLocation 是否属于总馆形态，比如“阅览室”
            // 如果是总馆形态，则要在前部增加一个 / 字符，以保证可以正确匹配 map 值
            // ‘/’字符可以理解为在馆代码和阅览室名字之间插入的一个必要的符号。这是为了弥补早期做法的兼容性问题
            dp2StringUtil.ParseCalendarName(strLocation,
        out string strLibraryCode,
        out string strRoom);
            if (string.IsNullOrEmpty(strLibraryCode))
                strLocation = "/" + strRoom;

            XmlNodeList items = cfg_dom.DocumentElement.SelectNodes(
                "ownerInstitution/item");
            List<HitItem> results = new List<HitItem>();
            foreach (XmlElement item in items)
            {
                string map = item.GetAttribute("map");
                if (strLocation.StartsWith(map))
                {
                    HitItem hit = new HitItem { Map = map, Element = item };
                    results.Add(hit);
                }
            }

            if (results.Count == 0)
                return false;

            // 如果命中多个，要选出 map 最长的那一个返回

            // 排序，大在前
            if (results.Count > 0)
                results.Sort((a, b) => { return b.Map.Length - a.Map.Length; });

            var element = results[0].Element;
            isil = element.GetAttribute("isil");
            alternative = element.GetAttribute("alternative");

            // 2021/2/1
            if (string.IsNullOrEmpty(isil) && string.IsNullOrEmpty(alternative))
            {
                throw new Exception($"map 元素不合法，isil 和 alternative 属性均为空");
            }

            return true;
        }

        class HitItem
        {
            public XmlElement Element { get; set; }
            public string Map { get; set; }
        }

#endif

        // 确保从 dp2library 获得 library.xml 中的 rfid 元素信息
        // return:
        //      result.Value 0: 一般返回 1: rfid 元素信息有变化，已经触发了重新下载册记录和读者记录
        public static NormalResult EnsureConfigDom()
        {
            _rfidCfgDom = new XmlDocument();

            // 获得 RFID 配置信息
            var result = LibraryChannelUtil.GetRfidCfg();

            if (result.Value == -1)
                return new NormalResult
                {
                    Value = -1,
                    ErrorInfo = $"从 dp2library 服务器获得 RFID 配置信息时出错: {result.ErrorInfo}"
                };
            else
            {
                if (string.IsNullOrEmpty(result.Xml))
                {
                    return new NormalResult
                    {
                        Value = -1,
                        ErrorInfo = $"从 dp2library 服务器获得 RFID 配置信息时出错: library.xml 中没有定义 rfid 元素"
                    };
                }
                _rfidCfgDom = new XmlDocument();
                _rfidCfgDom.LoadXml(result.Xml);

                _libraryName = result.LibraryName;

                if (result.XmlChanged)
                {
                    WpfClientInfo.WriteInfoLog($"[3] 探测到 library.xml 中 rfid 发生变化。\r\n变化前的: {result.OldXml}\r\n变化后的: {result.Xml}");
                    // 触发重新全量下载册和读者记录
                    ShelfData.TriggerDownloadEntitiesAndPatrons();

                    return new NormalResult { Value = 1 };
                }

                return new NormalResult();
            }
        }

        // 从 shelf.xml 配置文件中获得读者证读卡器名
        public static string GetPatronReaderName()
        {
            if (ShelfCfgDom == null)
                return "";

            XmlElement patron = ShelfCfgDom.DocumentElement.SelectSingleNode("patron") as XmlElement;
            if (patron == null)
                return "";

            return patron.GetAttribute("readerName");


            /*
            string cfg_filename = ShelfData.ShelfFilePath;
            XmlDocument cfg_dom = new XmlDocument();
            try
            {
                cfg_dom.Load(cfg_filename);

                XmlElement patron = cfg_dom.DocumentElement.SelectSingleNode("patron") as XmlElement;
                if (patron == null)
                    return "";

                return patron.GetAttribute("readerName");
            }
            catch (FileNotFoundException)
            {
                return "";
            }
            catch (Exception ex)
            {
                this.SetError("cfg", $"装载配置文件 shelf.xml 时出现异常: {ex.Message}");
                return "";
            }
            */
        }


        // 从 shelf.xml 中归纳出每个 door 读卡器的天线编号列表
        public static List<AntennaList> GetAntennaTable()
        {
            if (ShelfCfgDom == null)
                return new List<AntennaList>();

            // 读卡器名字 --> List<int> (天线列表)
            Hashtable name_table = new Hashtable();

            {
                XmlNodeList doors = ShelfCfgDom.DocumentElement.SelectNodes("//door");
                foreach (XmlElement door in doors)
                {
                    DoorItem.ParseReaderString(door.GetAttribute("antenna"),
                        out string readerName,
                        out int antenna);

                    // 禁止使用 * 作为读卡器名字
                    if (readerName == "*")
                        throw new Exception($"antenna属性值中读卡器名字部分不应使用 * ({door.OuterXml})");

                    // 跳过空读卡器名
                    if (string.IsNullOrEmpty(readerName))
                        continue;

                    AddToTable(name_table, readerName, antenna);
                }
            }

            List<AntennaList> results = new List<AntennaList>();
            foreach (string key in name_table.Keys)
            {
                List<int> list = name_table[key] as List<int>;
                list.Sort();

                results.Add(new AntennaList
                {
                    ReaderName = key,
                    Antennas = list
                });
            }

            return results;
        }

        // 断网模式下开门前检查读者是否超期
        public static string OfflineCheckOverdue()
        {
            if (ShelfCfgDom == null)
                return "true";
            var value = ShelfCfgDom.DocumentElement.SelectSingleNode("settings/key[@name='断网模式下开门前检查读者是否超期']/@value")?.Value;
            if (string.IsNullOrEmpty(value))
                value = "true";

            return value;
        }

        // 缓存工作人员账户到本地
        public static string CacheWorkerAccount()
        {
            if (ShelfCfgDom == null)
                return "false";
            var value = ShelfCfgDom.DocumentElement.SelectSingleNode("settings/key[@name='缓存工作人员账户到本地']/@value")?.Value;
            if (string.IsNullOrEmpty(value))
                value = "false";

            return value;
        }


        // 菜单页面显示图书馆名
        public static string GetLibraryNameVisibility()
        {
            if (ShelfCfgDom == null)
                return "true";
            var value = ShelfCfgDom.DocumentElement.SelectSingleNode("settings/key[@name='菜单页面显示图书馆名']/@value")?.Value;
            if (string.IsNullOrEmpty(value))
                value = "true";

            return value;
        }

        // 休眠关闭提交对话框秒数
        public static int GetIdleCloseSubmitDialog()
        {
            if (ShelfCfgDom == null)
                return 1;
            var value = ShelfCfgDom.DocumentElement.SelectSingleNode("settings/key[@name='休眠关闭提交对话框秒数']/@value")?.Value;
            if (string.IsNullOrEmpty(value))
                value = "0";
            if (Int32.TryParse(value, out int count) == false)
            {
                WpfClientInfo.WriteErrorLog($"shelf.xml 中 休眠关闭提交对话框秒数 配置参数值 '{value} 格式不正确。应为一个整数数字'");
                return 1;
            }

            return count;
        }

        static int _defaultWarningCloseDoorLength = 15;
        static int _defaultWarningCloseDoorRepeatLength = 10;

        // 语音提醒关门延迟秒数
        public static Tuple<int, int> GetWarningCloseDoorLength()
        {
            if (ShelfCfgDom == null)
                return new Tuple<int, int>(_defaultWarningCloseDoorLength, _defaultWarningCloseDoorRepeatLength);
            var value = ShelfCfgDom.DocumentElement.SelectSingleNode("settings/key[@name='语音提醒关门延迟秒数']/@value")?.Value;
            if (string.IsNullOrEmpty(value))
                return new Tuple<int, int>(_defaultWarningCloseDoorLength, _defaultWarningCloseDoorRepeatLength);

            var parts = StringUtil.ParseTwoPart(value, ",");
            string left = parts[0];
            if (string.IsNullOrEmpty(left))
                left = _defaultWarningCloseDoorLength.ToString();
            string right = parts[1];
            if (string.IsNullOrEmpty(right))
                right = _defaultWarningCloseDoorRepeatLength.ToString();
            if (Int32.TryParse(left, out int left_count) == false)
            {
                WpfClientInfo.WriteErrorLog($"shelf.xml 中 语音提醒关门延迟秒数 配置参数值 '{value} 格式不正确(逗号左侧数字 '{left}' 格式不正确)。应为一个整数数字，或者逗号间隔的两个整数数字");
                return new Tuple<int, int>(_defaultWarningCloseDoorLength, _defaultWarningCloseDoorRepeatLength);
            }
            if (Int32.TryParse(right, out int right_count) == false)
            {
                WpfClientInfo.WriteErrorLog($"shelf.xml 中 语音提醒关门延迟秒数 配置参数值 '{value} 格式不正确(逗号右侧数字 '{right}' 格式不正确)。应为一个整数数字，或者逗号间隔的两个整数数字");
                return new Tuple<int, int>(_defaultWarningCloseDoorLength, _defaultWarningCloseDoorRepeatLength);
            }
            return new Tuple<int, int>(left_count, right_count);
        }

        // 超额时语音播报次数
        public static int GetOverdueSpeakCount()
        {
            if (ShelfCfgDom == null)
                return 1;
            var value = ShelfCfgDom.DocumentElement.SelectSingleNode("settings/key[@name='超额时语音播报次数']/@value")?.Value;
            if (string.IsNullOrEmpty(value))
                value = "1";
            if (Int32.TryParse(value, out int count) == false)
            {
                WpfClientInfo.WriteErrorLog($"shelf.xml 中 超额时语音播报次数 配置参数值 '{value} 格式不正确。应为一个整数数字'");
                return 1;
            }

            return count;
        }

        // 读者信息屏蔽
        public static string GetPatronMask()
        {
            if (ShelfCfgDom == null)
                return null;
            var value = ShelfCfgDom.DocumentElement?.SelectSingleNode("settings/key[@name='读者信息屏蔽']/@value")?.Value;

            return value;
        }

        // 从 shelf.xml 配置文件中归纳出所有的读卡器名，包括天线编号部分
        // parameters:
        //      style   patron/doors
        public static string GetAllReaderNameList(string style)
        {
            if (ShelfCfgDom == null)
                return "*";

            // 读卡器名字 --> List<int> (天线列表)
            Hashtable name_table = new Hashtable();

            if (StringUtil.IsInList("doors", style))
            {
                XmlNodeList doors = ShelfCfgDom.DocumentElement.SelectNodes("//door");  // "shelf/door"
                foreach (XmlElement door in doors)
                {
                    DoorItem.ParseReaderString(door.GetAttribute("antenna"),
                        out string readerName,
                        out int antenna);

                    // 禁止使用 * 作为读卡器名字
                    if (readerName == "*")
                        throw new Exception($"antenna属性值中读卡器名字部分不应使用 * ({door.OuterXml})");

                    // 跳过空读卡器名
                    if (string.IsNullOrEmpty(readerName))
                        continue;

                    AddToTable(name_table, readerName, antenna);
                }
            }

            if (StringUtil.IsInList("patron", style))
            {
                XmlElement patron = ShelfCfgDom.DocumentElement.SelectSingleNode("patron") as XmlElement;
                if (patron != null)
                {
                    string readerName = patron.GetAttribute("readerName");
                    AddToTable(name_table, readerName, -1);
                }
            }

            StringBuilder result = new StringBuilder();
            int i = 0;
            foreach (string key in name_table.Keys)
            {
                List<int> list = name_table[key] as List<int>;
                list.Sort();

                if (i > 0)
                    result.Append(",");
                if (list.Count == 0)
                    result.Append(key);
                else
                    result.Append($"{key}:{Join(list, "|")}");
                i++;
            }

            return result.ToString();
        }

        // return:
        //      true 选中
        //      false 希望跳过
        public delegate bool Delegate_selectDoor(DoorItem door);

        // 获得处于打开状态的门的读卡器名字符串
        // parameters:
        public static string GetReaderNameList(List<DoorItem> _doors,
            Delegate_selectDoor func_select)
        {
            // 读卡器名字 --> List<int> (天线列表)
            Hashtable name_table = new Hashtable();
            foreach (var door in _doors)
            {
                var readerName = door.ReaderName;
                var antenna = door.Antenna;

                // 跳过空读卡器名
                if (string.IsNullOrEmpty(readerName))
                    continue;

                // 禁止使用 * 作为读卡器名字
                if (readerName == "*")
                    throw new Exception($"antenna属性值中读卡器名字部分不应使用 *");

                /*
                if (door.State != "open")
                    continue;
                    */
                if (func_select?.Invoke(door) == false)
                    continue;

                AddToTable(name_table, readerName, antenna);
            }

            StringBuilder result = new StringBuilder();
            int i = 0;
            foreach (string key in name_table.Keys)
            {
                List<int> list = name_table[key] as List<int>;
                list.Sort();

                if (i > 0)
                    result.Append(",");
                if (list.Count == 0)
                    result.Append(key);
                else
                    result.Append($"{key}:{Join(list, "|")}");
                i++;
            }

            return result.ToString();
        }

        static void AddToTable(Hashtable name_table, string readerName, int antenna)
        {
            List<int> list = new List<int>();
            if (name_table.ContainsKey(readerName) == false)
            {
                name_table[readerName] = list;
            }
            else
                list = name_table[readerName] as List<int>;

            if (antenna != -1)
            {
                if (list.IndexOf(antenna) == -1)
                    list.Add(antenna);
            }
        }

        static string Join(List<int> list, string sep)
        {
            StringBuilder text = new StringBuilder();
            int i = 0;
            foreach (var v in list)
            {
                if (i > 0)
                    text.Append(sep);
                text.Append(v.ToString());
                i++;
            }
            return text.ToString();
        }

        // 显示对书柜门的 Iventory 操作，同一时刻只能一个函数进入
        static AsyncSemaphore _inventoryLimit = new AsyncSemaphore(1);

        // 单独对一个门关联的 RFID 标签进行一次 inventory，确保此前的标签变化情况没有被遗漏
        public static async Task<NormalResult> RefreshInventoryAsync(DoorItem door)
        {
            // 获得和一个门相关的 readernamelist
            var list = GetReaderNameList(new List<DoorItem> { door }, null);
            string style = $"dont_delay";   // 确保 inventory 并立即返回

            using (var releaser = await _inventoryLimit.EnterAsync().ConfigureAwait(false))
            {
                // StringBuilder debugInfo = new StringBuilder();
                var result = RfidManager.CallListTags(list, style);
                // WpfClientInfo.WriteErrorLog($"RefreshInventory() list={list}, style={style}, result={result.ToString()}");

                try
                {
                    await RfidManager.TriggerListTagsEvent(list,
                        result,
                        "refresh",
                        true);
                    return new NormalResult();
                }
                catch (TagInfoException ex)
                {
                    WpfClientInfo.WriteErrorLog($"RefreshInventoryAsync() 出现异常: {ExceptionUtil.GetDebugText(ex)}");

                    return new NormalResult
                    {
                        Value = -1,
                        ErrorInfo = $"对门 {door.Name} 内的全部标签进行盘点时，发现无法解析的标签(UID:{ex.TagInfo.UID})",
                        ErrorCode = "tagParseError"
                    };
                }
                catch (Exception ex)
                {
                    // WpfClientInfo.WriteErrorLog($"RefreshInventory() TriggerListTagsEvent() 异常:{ExceptionUtil.GetDebugText(ex)}\r\ndebugInfo={debugInfo.ToString()}");
                    WpfClientInfo.WriteErrorLog($"RefreshInventory() TriggerListTagsEvent() list='{list}' 异常:{ExceptionUtil.GetDebugText(ex)}");
                    return new NormalResult
                    {
                        Value = -1,
                        ErrorInfo = $"RefreshInventory() 出现异常(门:{door.Name}): {ex.Message}",
                        ErrorCode = ex.GetType().ToString()
                    };
                }
            }
        }

        public class TestInventoryResult : NormalResult
        {
            public DoorItem Door { get; set; }
            public List<TagAndData> Datas { get; set; }
        }

        // 2020/12/31
        // 单独对一个门关联的 RFID 标签进行一次验证性 inventory
        public static async Task<TestInventoryResult> TestInventoryAsync(
            DoorItem door,
            string style)
        {
            // 获得和一个门相关的 readernamelist
            var readername_list = GetReaderNameList(new List<DoorItem> { door }, null);
            string list_style = $"dont_delay";   // 确保 inventory 并立即返回

            bool getTagInfo = StringUtil.IsInList("getTagInfo", style);
            using (var releaser = await _inventoryLimit.EnterAsync().ConfigureAwait(false))
            {
                // StringBuilder debugInfo = new StringBuilder();
                var result = RfidManager.CallListTags(readername_list, list_style);
                // WpfClientInfo.WriteErrorLog($"RefreshInventory() list={list}, style={style}, result={result.ToString()}");

                try
                {
                    /*
                    await RfidManager.TriggerListTagsEvent(list,
                        result,
                        "refresh",
                        true);
                    */
                    // 对每个标签 GetTagInfo()
                    var datas = new List<TagAndData>();
                    if (result.Results != null)
                    {
                        foreach (var tag in result.Results)
                        {
                            var data = new TagAndData
                            {
                                OneTag = tag
                            };
                            datas.Add(data);

                            if (getTagInfo)
                            {
                                var get_result = RfidManager.GetTagInfo(tag.ReaderName,
                                    tag.UID,
                                    tag.AntennaID);
                                if (get_result.Value == -1)
                                    data.Error = get_result.ErrorInfo;
                                else
                                    data.OneTag.TagInfo = get_result.TagInfo;
                            }
                        }
                    }

                    return new TestInventoryResult
                    {
                        Door = door,
                        Datas = datas
                    };
                }
                catch (TagInfoException ex)
                {
                    WpfClientInfo.WriteErrorLog($"TestInventoryAsync() 出现异常: {ExceptionUtil.GetDebugText(ex)}");

                    return new TestInventoryResult
                    {
                        Value = -1,
                        ErrorInfo = $"对门 {door.Name} 内的全部标签进行盘点时，发现无法解析的标签(UID:{ex.TagInfo.UID})",
                        ErrorCode = "tagParseError"
                    };
                }
                catch (Exception ex)
                {
                    // WpfClientInfo.WriteErrorLog($"TestInventoryAsync() TriggerListTagsEvent() 异常:{ExceptionUtil.GetDebugText(ex)}\r\ndebugInfo={debugInfo.ToString()}");
                    WpfClientInfo.WriteErrorLog($"TestInventoryAsync() TriggerListTagsEvent() list='{readername_list}' 异常:{ExceptionUtil.GetDebugText(ex)}");
                    return new TestInventoryResult
                    {
                        Value = -1,
                        ErrorInfo = $"TestInventoryAsync() 出现异常(门:{door.Name}): {ex.Message}",
                        ErrorCode = ex.GetType().ToString()
                    };
                }
            }
        }


        static XmlDocument _shelfCfgDom = null;

        public static XmlDocument ShelfCfgDom
        {
            get
            {
                return _shelfCfgDom;
            }
        }

        public static string ShelfFilePath
        {
            get
            {
                string cfg_filename = System.IO.Path.Combine(WpfClientInfo.UserDir, "shelf.xml");
                return cfg_filename;
            }
        }

        static List<DoorItem> _doors = new List<DoorItem>();
        public static List<DoorItem> Doors
        {
            get
            {
                return _doors;
            }
        }

        // 累积的全部 action
        static List<ActionInfo> _actions = new List<ActionInfo>();
        public static IReadOnlyCollection<ActionInfo> Actions
        {
            get
            {
                lock (_syncRoot_actions)
                {
                    return new List<ActionInfo>(_actions);
                    // return _actions;
                }
            }
        }

        // 获得 Actions，并同时从 _actions 中移走
        public static List<ActionInfo> PullActions()
        {
            lock (_syncRoot_actions)
            {
                var results = new List<ActionInfo>(_actions);
                _actions.Clear();
                return results;
            }
        }

        // 用于保护 Actions 数据结构的锁对象
        static object _syncRoot_actions = new object();

        public delegate Operator Delegate_getOperator(Entity entity);

        public class OperationInfo
        {
            // 操作名称
            public string Operation { get; set; }
            public Entity Entity { get; set; }
            public string Location { get; set; }    // 目标馆藏地(调拨)
            public string ShelfNo { get; set; }     // 目标架位(上下架)
            public Operator Operator { get; set; }
        }

        // 根据 ActionInfo 对象构建 OperationInfo 对象
        // TODO: 把 还书 和 上架，归并为一条 还书并上架
        public static List<OperationInfo> BuildOperationInfos(List<ActionInfo> actions)
        {
            List<OperationInfo> results = new List<OperationInfo>();
            foreach (var action in actions)
            {
                if (action.Action == "return")
                {
                    var operation = new OperationInfo
                    {
                        Operation = "还书",
                        Entity = action.Entity,
                        Operator = action.Operator,
                        ShelfNo = ShelfData.GetShelfNo(action.Entity),
                    };
                    /*
                    if (action.Operator.IsWorker == true)
                    {
                        operation.Operation = "转入";
                    }
                    */
                    results.Add(operation);
                }

                if (action.Action == "borrow")
                {
                    var operation = new OperationInfo
                    {
                        Operation = "借书",
                        Entity = action.Entity,
                        Operator = action.Operator,
                        ShelfNo = ShelfData.GetShelfNo(action.Entity),
                    };
                    /*
                    if (action.Operator.IsWorker == true)
                    {
                        operation.Operation = "转出";
                    }
                    */
                    results.Add(operation);
                }

                if (action.Action.StartsWith("transfer") && action.TransferDirection == "in")
                {
                    string name = "上架";
                    if (string.IsNullOrEmpty(action.Location) == false)
                        name = "上架+调入";
                    var operation = new OperationInfo
                    {
                        Operation = name,
                        Entity = action.Entity,
                        Operator = action.Operator,
                        Location = action.Location,
                        ShelfNo = action.CurrentShelfNo,
                    };

                    results.Add(operation);
                }

                if (action.Action.StartsWith("transfer") && action.TransferDirection == "out")
                {
                    string name = "下架";
                    if (string.IsNullOrEmpty(action.Location) == false)
                        name = "下架+调出";

                    var operation = new OperationInfo
                    {
                        Operation = name,
                        Entity = action.Entity,
                        Operator = action.Operator,
                        Location = action.Location,
                        ShelfNo = action.CurrentShelfNo,
                    };

                    results.Add(operation);
                }
            }
            return results;
        }

        // 2020/9/24
        // 限制 actions 操作，同一时刻只能进行一轮次操作
        // internal static AsyncSemaphore _actionsLimit = new AsyncSemaphore(1);

        public class SaveActionResult : NormalResult
        {
            // public List<OperationInfo> Operations { get; set; }
            public List<ActionInfo> Actions { get; set; }
        }

        // 将暂存的信息构造为 Action。但并不立即提交
        // parameters:
        //      patronBarcode   读者证条码号。如果为 "*"，表示希望针对全部读者的都提交
        public static async Task<SaveActionResult> BuildActionsAsync(
            // string patronBarcode,
            Delegate_getOperator func_getOperator)
        {
            // List<OperationInfo> infos = new List<OperationInfo>();
            try
            {
                // using (var releaser = await _actionsLimit.EnterAsync())
                {
                    // oi_pii --> bookType string
                    Hashtable bookTypeCache = new Hashtable();

                    // PII -> patron xml
                    Hashtable patron_table = new Hashtable();

                    List<string> returned_piis = new List<string>();
                    List<string> special_piis = new List<string>();

                    List<ActionInfo> actions = new List<ActionInfo>();
                    List<Entity> processed = new List<Entity>();
                    foreach (var entity in ShelfData.l_Adds)
                    {
                        // Debug.Assert(string.IsNullOrEmpty(entity.PII) == false, "");

                        if (ShelfData.BelongToNormal(entity) == false)
                            continue;
                        var person = func_getOperator?.Invoke(entity);
                        if (person == null)
                            continue;

                        actions.Add(new ActionInfo
                        {
                            Entity = entity.Clone(),
                            Action = "return",
                            Operator = person,
                        });

                        // 2020/9/7
                        returned_piis.Add(entity.GetOiPii(true));

                        // 没有更新的，才进行一次 transfer。更新的留在后面专门做
                        // “更新”的意思是从这个门移动到了另外一个门
                        if (ShelfData.Find(ShelfData.l_Changes, (o) => o.UID == entity.UID).Count == 0)
                        {
                            string location = "";
                            // 工作人员身份，还可能要进行馆藏位置向内转移
                            if (person.IsWorker == true)
                            {
                                location = GetLocationPart(ShelfData.GetShelfNo(entity));
                            }
                            actions.Add(new ActionInfo
                            {
                                Entity = entity.Clone(),
                                Action = "transfer",
                                TransferDirection = "in",
                                Location = location,
                                CurrentShelfNo = ShelfData.GetShelfNo(entity),
                                Operator = person
                            });
                        }

                        /*
                        // 用于显示的操作信息
                        {
                            var operation = new OperationInfo
                            {
                                Operation = "还书",
                                Entity = entity,
                                Operator = person,
                                ShelfNo = ShelfData.GetShelfNo(entity),
                            };
                            if (person.IsWorker == true)
                            {
                                operation.Operation = "转入";
                            }
                            infos.Add(operation);
                        }
                        */

                        processed.Add(entity);

                        // 2020/4/2
                        ShelfData.Add("all", entity);

                        // 2020/4/2
                        // 还书操作前先尝试修改 EAS
                        if (entity.Error == null && StringUtil.IsInList("patronCard,oiError", entity.ErrorCode) == false)
                        {
                            var result = SetEAS(entity.UID, entity.Antenna, false);
                            if (result.Value == -1)
                            {
                                string text = $"修改 EAS 动作失败: {result.ErrorInfo}";
                                // entity.SetError(text, "yellow");
                                entity.AppendError(text, "red", "setEasError");

                                // 写入错误日志
                                WpfClientInfo.WriteInfoLog($"修改册 '{entity.GetPiiOrUid()}' 的 EAS 失败: {result.ErrorInfo}");
                            }
                        }
                    }

                    foreach (var entity in ShelfData.l_Changes)
                    {
                        // Debug.Assert(string.IsNullOrEmpty(entity.PII) == false, "");

                        if (ShelfData.BelongToNormal(entity) == false)
                            continue;
                        var person = func_getOperator?.Invoke(entity);
                        if (person == null)
                            continue;

                        string location = "";
                        // 工作人员身份，还可能要进行馆藏位置转移
                        if (person.IsWorker == true)
                        {
                            location = GetLocationPart(ShelfData.GetShelfNo(entity));
                        }
                        // 更新
                        actions.Add(new ActionInfo
                        {
                            Entity = entity.Clone(),
                            Action = "transfer",
                            TransferDirection = "in",
                            Location = location,
                            CurrentShelfNo = ShelfData.GetShelfNo(entity),
                            Operator = person
                        });

                        /*
                        // 用于显示的操作信息
                        {
                            var operation = new OperationInfo
                            {
                                Operation = "调整位置",
                                Entity = entity,
                                Operator = person,
                                ShelfNo = ShelfData.GetShelfNo(entity),
                            };

                            infos.Add(operation);
                        }
                        */

                        processed.Add(entity);
                    }

                    // int borrowed_count = 0;
                    List<string> borrowed_piis = new List<string>();
                    foreach (var entity in ShelfData.l_Removes)
                    {
                        // Debug.Assert(string.IsNullOrEmpty(entity.PII) == false, "");

                        if (ShelfData.BelongToNormal(entity) == false)
                            continue;
                        var person = func_getOperator?.Invoke(entity);
                        if (person == null) // 注：如果得到一个内容为空的 Operator 对象则不会进入 if
                        {
                            // 2021/9/28
                            WpfClientInfo.WriteErrorLog($"*** 严重错误 ***: 在构造借书动作时发现没有和门关联的操作者信息。已忽略此 entity。请注意检查追踪此册去向。\r\nentity={ActionInfo.ToString(entity)}");
                            continue;
                        }

                        // 2020/4/19
                        // 检查一下 actions 里面是否已经有了针对同一个 PII 的 return 动作。
                        // 如果已经有了，则删除 return 动作，并且也忽略新的 borrow 动作
                        var returns = actions.FindAll(o => o.Action == "return" && o.Entity.PII == entity.PII);
                        if (returns.Count > 0)
                        {
                            foreach (var r in returns)
                            {
                                actions.Remove(r);
                            }
                            continue;
                        }

                        if (person.IsWorker == false)
                        {
                            string patron_xml = null;
                            // 2020/8/13
                            // 如果是联网情况下，还是要尽量获得最新的读者记录作为演算借册超期的基础
                            if (ShelfData.LibraryNetworkCondition == "OK")
                            {
                                patron_xml = (string)patron_table[GetString(person.PatronBarcode)];
                                if (string.IsNullOrEmpty(patron_xml) == true)
                                {
                                    // 尝试获得最新的读者记录
                                    // return.Value:
                                    //      -1  出错
                                    //      0   读者记录没有找到
                                    //      1   成功
                                    var get_result = LibraryChannelUtil.GetReaderInfo(person.PatronBarcode);
                                    patron_xml = get_result.ReaderXml;
                                    // 记忆
                                    if (string.IsNullOrEmpty(patron_xml) == false)
                                        patron_table[GetString(person.PatronBarcode)] = patron_xml;
                                }
                            }

                            // 只有读者身份才进行借阅操作
                            actions.Add(new ActionInfo
                            {
                                Entity = entity.Clone(),
                                Action = "borrow",
                                Operator = person,
                                // TODO: 让 patron_xml 可以累积变化，这样可以大幅度提高速度
                                ActionString = await BuildBorrowInfo(
                                    bookTypeCache,
                                    person.PatronBarcode,
                                    person.PatronInstitution,
                                    patron_xml,
                                    entity,
                                    borrowed_piis,
                                    returned_piis,
                                    special_piis), // borrowed_count++
                            });

                            borrowed_piis.Add(entity.GetOiPii(true));
                        }

                        //
                        if (person.IsWorker == true)
                        {
                            // 工作人员身份，还可能要进行馆藏位置向外转移
                            string location = "%checkout_location%";
                            actions.Add(new ActionInfo
                            {
                                Entity = entity.Clone(),
                                Action = "transfer",
                                TransferDirection = "out",
                                Location = location,
                                // 注: ShelfNo 成员不使用。意在保持册记录中 currentLocation 元素不变
                                Operator = person
                            });
                        }

                        /*
                        // 用于显示的操作信息
                        {
                            var operation = new OperationInfo
                            {
                                Operation = "借书",
                                Entity = entity,
                                Operator = person,
                                ShelfNo = ShelfData.GetShelfNo(entity),
                            };
                            if (person.IsWorker == true)
                            {
                                operation.Operation = "转出";
                            }
                            infos.Add(operation);
                        }
                        */

                        processed.Add(entity);

                        // 2020/4/2
                        ShelfData.Remove("all", entity);
                    }

                    /*
                    foreach (var entity in processed)
                    {
                        ShelfData.Remove("all", entity);
                        ShelfData.Remove("adds", entity);
                        ShelfData.Remove("removes", entity);
                        ShelfData.Remove("changes", entity);
                    }
                    */
                    {
                        // ShelfData.Remove("all", processed);
                        ShelfData.l_Remove("adds", processed);
                        ShelfData.l_Remove("removes", processed);
                        ShelfData.l_Remove("changes", processed);
                    }

                    // 2020/4/2
                    ShelfData.l_RefreshCount();

                    if (actions.Count == 0)
                        return new SaveActionResult
                        {
                            Actions = actions,
                            //Operations = infos
                        };  // 没有必要处理
                    ShelfData.PushActions(actions);
                    return new SaveActionResult
                    {
                        Actions = actions,
                        //Operations = infos
                    };
                }
            }
            catch (Exception ex)
            {
                // 2020/6/10
                WpfClientInfo.WriteErrorLog($"SaveActions() 出现异常: {ExceptionUtil.GetDebugText(ex)}");
                return new SaveActionResult
                {
                    Value = -1,
                    ErrorInfo = $"SaveActions() 出现异常: {ex.Message}"
                };
            }

            string GetString(string text)
            {
                if (text == null)
                    return "";
                return text;
            }
        }

        static int max_items = 5;  // 一个读者最多能同时借阅的册数
        static int max_period = 31; // 读者借阅期限天数

        // 构造 BorrowInfo 字符串
        // 用于在同步之前，为本地数据库记录临时模拟出 BorrowInfo。这样当长期断网的情况下，dp2ssl 能用它进行本地借书权限的判断(判断是否超期、超额)
        // parameters:
        //      patron_xml  读者记录 XML。如果为 null，表示需要本函数自己去尝试获得读者记录
        //      delta_piis   尚未来得及保存到数据库的已借册的 PII 列表。注意里面的 PII 有可能是空字符串。PII 字符串是有“点”的格式
        //      returned_piis   尚未来得及保存到数据库的已还册的 PII 列表。PII 字符串里面有“点”
        static async Task<string> BuildBorrowInfo(
            Hashtable bookTypeCache,
            string patron_pii,
            string patron_oi,
            string patron_xml,
            Entity entity,
            List<string> delta_piis,
            List<string> returned_piis,
            List<string> special_piis)
        {
            StringBuilder debugInfo = null; // new StringBuilder();

            DateTime start = DateTime.Now;
            try
            {
                // 输入参数
                debugInfo?.AppendLine($"=== 进入 BuildBorrowInfo() ===");
                debugInfo?.AppendLine($"patron_pii='{patron_pii}'");
                debugInfo?.AppendLine($"patron_oi='{patron_oi}'");
                debugInfo?.AppendLine($"entity='PII={entity.PII},Title='{entity.Title}''");

                BorrowInfo borrow_info = new BorrowInfo();

                XmlDocument readerdom = null;
                if (string.IsNullOrEmpty(patron_xml) == false)
                {
                    readerdom = new XmlDocument();
                    try
                    {
                        readerdom.LoadXml(patron_xml);
                    }
                    catch (Exception ex)
                    {
                        WpfClientInfo.WriteErrorLog($"读者记录装载进入 XMLDOM 时出现异常: {ExceptionUtil.GetDebugText(ex)}");
                        readerdom = null;
                    }
                }

                if (debugInfo != null)
                {
                    if (readerdom != null && readerdom.DocumentElement != null)
                    {
                        DomUtil.DeleteElement(readerdom.DocumentElement, "borrowHistory");
                        DomUtil.DeleteElement(readerdom.DocumentElement, "fingerprint");
                        DomUtil.DeleteElement(readerdom.DocumentElement, "face");
                        DomUtil.RemoveEmptyElements(readerdom.DocumentElement);
                    }
                    if (readerdom != null)
                        debugInfo?.AppendLine($"patron_xml='{DomUtil.GetIndentXml(readerdom)}'");
                    else
                        debugInfo?.AppendLine($"patron_xml=null");
                }

                string patron_type = GetPatronType(patron_pii,
                    patron_oi,
                    ref readerdom,
                    out string patronLibraryCode);
                if (patron_type == null)
                {
                    debugInfo?.AppendLine($"因为没有找到证条码号为 '{patron_pii}' OI 为 '{patron_oi}' 的读者的读者类型，只好采用默认的借阅总册数 {max_items}");
                    WpfClientInfo.WriteInfoLog($"因为没有找到证条码号为 '{patron_pii}' OI 为 '{patron_oi}' 的读者的读者类型，只好采用默认的借阅总册数 {max_items}");
                    goto DEFAULT;
                }

                debugInfo?.AppendLine($"读者类型为 '{patron_type}'");

                /*
                // 从读者记录中去掉已经还书的 borrows/borrow 元素
                if (readerdom != null && returned_piis.Count > 0)
                {
                    RemoveReturnedBorrows(readerdom,
                        returned_piis);
                    debugInfo?.AppendLine($"从读者记录中去掉已经还书若干 PII '{StringUtil.MakePathList(returned_piis)}' 后, 读者记录变为:\r\n{DomUtil.GetIndentXml(readerdom)}");
                }
                */

                // TODO: 如何判断本册借阅时候是否已经超额？
                var piis = GetBorrowItems(patron_pii, readerdom);

                debugInfo?.AppendLine($"readerdom 中的 在借图书列表为 '{StringUtil.MakePathList(piis)}'");

                piis.AddRange(delta_piis);

                debugInfo?.AppendLine($"和 delta_piis 合并后的在借列表为 '{StringUtil.MakePathList(piis)}'");

                // 2020/9/7
                if (returned_piis.Count > 0)
                {
                    foreach (var pii in returned_piis)
                    {
                        piis.Remove(pii);
                    }
                    debugInfo?.AppendLine($"去掉 returned_piis 中已还(还来不及同步的)在借列表为 '{StringUtil.MakePathList(piis)}'");
                }

                // 2020/9/8
                if (special_piis.Count > 0)
                {
                    foreach (var pii in special_piis)
                    {
                        piis.Remove(pii);
                    }
                    debugInfo?.AppendLine($"去掉 special_piis 中特殊的标签以后在借列表为 '{StringUtil.MakePathList(piis)}'");
                }

                // 当前册的图书类型
                var info_result = await GetBookInfoAsync(entity.GetOiPii(true));
                if (info_result.Value == -1)
                {
                    // 加入特殊列表，避免影响后面其他册计算超额
                    if (info_result.ErrorCode == "notFoundWhileNetwork")
                        special_piis.Add(entity.GetOiPii(true));
                    // 如果得不到图书类型，建议按照默认的权限参数处理
                    debugInfo?.AppendLine($"因为没有找到 PII 为 '{entity.PII}' 的图书的图书类型({info_result.ToString()})，只好采用默认的借阅总册数 {max_items}");
                    WpfClientInfo.WriteInfoLog($"因为没有找到 PII 为'{entity.PII}' 的图书的图书类型({info_result.ToString()})，只好采用默认的借阅总册数 {max_items}");
                    goto DEFAULT;
                }

                debugInfo?.AppendLine($"当前册 (PII 为 '{entity.PII}') 的册类型为 '{info_result.ToString()}'");

                GetTypeMaxResult max_result = null;
                int thisTypeCount = 0;
                bool bLibraryCodeMismatch = false;
                if (info_result.LibraryCode != patronLibraryCode)
                {
                    debugInfo?.AppendLine($"*** 当前册 (PII 为 '{entity.PII}') 的馆代码为 '{info_result.LibraryCode}'，和当前读者的馆代码 '{patronLibraryCode}' 不吻合，");
                    debugInfo?.AppendLine($"所以图书类型 '{info_result.BookType}' 的最大借阅许可数，被当作 0 处理");
                    max_result = new GetTypeMaxResult { Max = 0 };
                    bLibraryCodeMismatch = true;
                }
                else
                {
                    // 计算已经借阅的册中和当前册类型相同的册数
                    foreach (string pii in piis)
                    {
                        // 此函数比较费时间
                        string book_type = await GetBookType(bookTypeCache, pii);
                        debugInfo?.AppendLine($"计算在借册数过程: 获得 '{pii}' 的图书类型，返回 book_type='{book_type}'");
                        if (book_type == info_result.BookType)
                        {
                            debugInfo?.AppendLine($"匹配 图书类型 '{book_type}' 和 info_result.BookType '{info_result.BookType}' 匹配上了，加一");
                            thisTypeCount++;
                        }
                        else
                        {
                            debugInfo?.AppendLine($"不匹配 图书类型 '{book_type}' 和 info_result.BookType '{info_result.BookType}'");
                        }
                    }

                    debugInfo?.AppendLine($"和 '{info_result.BookType}' 相同的在借册数为 {thisTypeCount}");

                    max_result = GetTypeMax(info_result.LibraryCode,
            patron_type,
            info_result.BookType);

                    debugInfo?.AppendLine($"获得图书类型 '{info_result.BookType}' 的最大借阅许可数，返回 {max_result.ToString()}");
                }


                bool overflow = false;
                // 图书类型限额超过了
                if (thisTypeCount + 1 > max_result.Max)
                {
                    debugInfo?.AppendLine($"thisTypeCount={thisTypeCount} 加 1 大于 {max_result.Max}，具体图书类型超额了");

                    if (bLibraryCodeMismatch)   // 2020/9/14
                        borrow_info.Overflows = new string[] { $"读者 '{patron_pii}' 的馆代码 '{patronLibraryCode}' 和册的馆代码 '{info_result.LibraryCode}' 不匹配" };
                    else
                        borrow_info.Overflows = new string[] { $"读者 '{patron_pii}' 所借 '{info_result.BookType}' 类图书数量将超过 馆代码 '{info_result.LibraryCode}' 中 该读者类型 '{patron_type}' 对该图书类型 '{info_result.BookType}' 的最多 可借册数 值 '{max_result.Max}'" };
                    // 一天以后还书
                    SetReturning(1, "day");
                    overflow = true;
                }
                else
                {
                    var total_max_result = GetTotalMax(info_result.LibraryCode,
        patron_type);

                    debugInfo?.AppendLine($"获得读者类型 '{patron_type}' 的总借阅许可数，返回 {total_max_result.ToString()}");

                    // 读者类型限额超过了
                    if (piis.Count + 1 > total_max_result.Max)
                    {
                        debugInfo?.AppendLine($"piis.Count={piis.Count} 加 1 大于 {total_max_result.Max}，读者类型总限额超额了");

                        borrow_info.Overflows = new string[] { $"读者 '{patron_pii}' 所借图书数量将超过 馆代码 '{info_result.LibraryCode}' 中 该读者类型 '{patron_type}' 对所有图书类型的最多 可借册数 值 '{total_max_result.Max}'" };
                        // 一天以后还书
                        SetReturning(1, "day");
                        overflow = true;
                    }
                }

                if (overflow == false)
                {
                    // 获得借期
                    var period_result = GetPeriod(info_result.LibraryCode,
        patron_type,
        info_result.BookType);

                    debugInfo?.AppendLine($"获得读者类型 '{patron_type}' 针对图书类型 '{info_result.BookType}' 的借期(馆代码 '{info_result.LibraryCode}')，返回 {period_result.ToString()}");

                    if (period_result.Value == -1)
                    {
                        debugInfo?.AppendLine($"(1)只好按照 {max_period} 天的默认天数");

                        // 一个月以后还书
                        SetReturning(max_period, "day");
                        // TODO: 写入错误日志
                    }
                    else
                    {
                        int nRet = DateTimeUtil.ParsePeriodUnit(
        period_result.ErrorCode,
        "day",
        out long lPeriodValue,
        out string strPeriodUnit,
        out string strError);
                        if (nRet == -1)
                        {
                            debugInfo?.AppendLine($"(2)只好按照 {max_period} 天的默认天数");

                            // 只好按照一个月以后还书来处理
                            SetReturning(max_period, "day");
                            // 写入错误日志
                            WpfClientInfo.WriteErrorLog($"解析时间段字符串 '{period_result.ErrorCode}' 时发生错误: {strError}");
                        }
                        else
                        {
                            debugInfo?.AppendLine($"解析时间段字符串 '{period_result.ErrorCode}' 成功");

                            string error = SetReturning((int)lPeriodValue, strPeriodUnit);
                            // 2020/6/10
                            if (error != null)
                            {
                                debugInfo?.AppendLine($"SetReturning() 返回 '{error}', (3)只好按照 {max_period} 天的默认天数");

                                // 只好按照一个月以后还书来处理
                                SetReturning(max_period, "day");

                                WpfClientInfo.WriteErrorLog($"时间段字符串 '{period_result.ErrorCode}' 格式错误: {error}");
                            }
                        }
                    }
                }

                goto END;

            DEFAULT:
                int item_count = GetBorrowItems(patron_pii, readerdom).Count;
                if (item_count + delta_piis.Count >= max_items)
                {
                    debugInfo?.AppendLine($"默认处理，达到或超过 max_items ({max_items}) 情形(item_count={item_count},delta_piis.Count={delta_piis.Count})");

                    borrow_info.Overflows = new string[] { $"超过额度(额度是 {max_items} 册)" };
                    // 一天以后还书
                    SetReturning(1, "day");
                }
                else
                {
                    debugInfo?.AppendLine($"默认处理，未超过 max_items ({max_items}) 情形");

                    // 一个月以后还书
                    SetReturning(max_period, "day");
                    //borrow_info.Period = $"{max_period}day";
                    //borrow_info.LatestReturnTime = DateTimeUtil.Rfc1123DateTimeStringEx(DateTime.Now.AddDays(max_period));
                }

            END:
                if (entity != null)
                    borrow_info.ItemBarcode = entity.PII;
                string json = JsonConvert.SerializeObject(borrow_info);

                if (debugInfo != null)
                    WpfClientInfo.WriteInfoLog($"{debugInfo.ToString()}\r\nborrow_info={json}");
                return json;


                // 设置 BorrowInfo 里面和还书时间有关的两个成员 Period 和 LatestReturnTime
                string SetReturning(int days, string unit)
                {
                    // 检查 unit
                    if (unit != "day" && unit != "hour")
                    {
                        string error = $"出现了无法理解的时间单位字符串 '{unit}'";
                        WpfClientInfo.WriteErrorLog(error);
                        return error;
                    }

                    borrow_info.Period = $"{days}{unit}";
                    DateTime returning = /*DateTime*/ShelfData.Now.AddDays(days);
                    if (unit == "hour")
                        returning = /*DateTime*/ShelfData.Now.AddHours(days);
                    // 正规化时间
                    returning = LibraryServerUtil.RoundTime(unit, returning);
                    borrow_info.LatestReturnTime = DateTimeUtil.Rfc1123DateTimeStringEx(returning);
                    return null;
                }

            }
            finally
            {
                WpfClientInfo.WriteInfoLog($"BuildBorrowInfo() 耗时 {(DateTime.Now - start).TotalSeconds.ToString()} (读者 {patron_pii} 针对册 {entity.PII})");
            }
        }

        // 2020/9/7
        // 把读者记录中已经还书的那些 borrows/borrow 元素删除
        static void RemoveReturnedBorrows(XmlDocument readerdom,
            List<string> returned_piis)
        {
            if (readerdom.DocumentElement == null)
                return;

            foreach (var pii in returned_piis)
            {
                XmlElement borrow = readerdom.DocumentElement.SelectSingleNode($"borrows/borrow[@barcode='{pii}']") as XmlElement;
                if (borrow == null)
                    continue;
                borrow.ParentNode.RemoveChild(borrow);
            }
        }

        // parameters:
        //      oi_pii  形态为 OI.PII
        static async Task<string> GetBookType(Hashtable bookTypeCache,
            string oi_pii)
        {
            // 先尝试从 cache 中找
            if (bookTypeCache != null)
            {
                string bookType = bookTypeCache[oi_pii] as string;
                if (bookType != null)
                    return bookType;
            }

            var result = await GetBookInfoAsync(oi_pii);
            if (result.Value == -1)
            {
                WpfClientInfo.WriteErrorLog($"GetBookType() 用 '{oi_pii}' 获得图书类型返回出错 {result.ToString()}");
                return null;
            }

            // 加入 cache
            if (bookTypeCache != null)
            {
                bookTypeCache[oi_pii] = result.BookType;
            }

            return result.BookType;
        }

        // 
        public class GetBookInfoResult : NormalResult
        {
            public string BookType { get; set; }
            public string LibraryCode { get; set; }

            public override string ToString()
            {
                return $"BookType='{BookType}',LibraryCode='{LibraryCode}'," + base.ToString();
            }
        }

        // TODO: 要增加从 dp2library 服务器直接获取的分支
        // 获得册信息
        // parameters:
        //      oi_pii  形态为 OI.PII
        // return.Value:
        //      -1  出错(包括册记录没有找到的情况)
        //      1   成功
        static async Task<GetBookInfoResult> GetBookInfoAsync(string oi_pii)
        {
            // 2020/8/27
            // Debug.Assert(pii.IndexOf(".") != -1, "GetBookInfoAsync() 所使用的 PII 中必须有点");

            var result = LibraryChannelUtil.LocalGetEntityData(oi_pii);
            if (result.Value == -1
                || result.Value == 0
                || string.IsNullOrEmpty(result.ItemXml)/* 2020/9/3 增加*/)
            {
                if (ShelfData.LibraryNetworkCondition == "OK")
                {
                    result = await LibraryChannelUtil.GetEntityDataAsync(oi_pii, "network");
                    // 联网状态下确定没有找到
                    if (result.ErrorCode == "NotFound")
                        return new GetBookInfoResult
                        {
                            Value = -1,
                            ErrorInfo = $"PII 为 '{oi_pii}' 的册记录没有找到",
                            ErrorCode = "notFoundWhileNetwork"
                        };
                }

                if (result.Value == -1 || result.Value == 0)
                    return new GetBookInfoResult
                    {
                        Value = -1,
                        ErrorInfo = result.ErrorInfo,
                        ErrorCode = result.ErrorCode,
                    };
                if (string.IsNullOrEmpty(result.ItemXml))
                    return new GetBookInfoResult
                    {
                        Value = -1,
                        ErrorInfo = $"PII 为 '{oi_pii}' 的册记录没有找到",
                        ErrorCode = "notFound"
                    };
            }

            XmlDocument dom = new XmlDocument();
            try
            {
                dom.LoadXml(result.ItemXml);
            }
            catch (Exception ex)
            {
                return new GetBookInfoResult
                {
                    Value = -1,
                    ErrorInfo = $"册记录 XML 格式不正确: {ex.Message}",
                    ErrorCode = ex.GetType().ToString()
                };
            }

            string bookType = DomUtil.GetElementText(dom.DocumentElement, "bookType");
            string location = DomUtil.GetElementText(dom.DocumentElement, "location");
            location = StringUtil.GetPureLocationString(location);

            // 获得 location 中馆代码部分
            dp2StringUtil.ParseCalendarName(location,
    out string strLibraryCode,
    out string strPureName);
            return new GetBookInfoResult
            {
                Value = 1,
                BookType = bookType,
                LibraryCode = strLibraryCode
            };
        }

        // TODO: 要增加从 dp2library 服务器直接获取的分支
        // 获得读者的类型，从本地缓存的读者记录中
        static string GetPatronType(string patron_pii,
            string patron_oi,
            ref XmlDocument readerdom,
            out string libraryCode)
        {
            libraryCode = "";

            if (readerdom == null)
            {
                string query = patron_pii;
                if (string.IsNullOrEmpty(patron_oi) == false)
                    query = patron_oi + "." + patron_pii;

                GetReaderInfoResult result = null;
                /*
                // 网络条件 OK 的时候尽量直接从 dp2library 服务器获得读者记录
                if (ShelfData.LibraryNetworkCondition == "OK")
                    result = GetReaderInfo(query);
                else
                */
                result = LibraryChannelUtil.GetReaderInfoFromLocal(query, false);
                if (result.Value == -1 || result.Value == 0)
                    return null;
                readerdom = new XmlDocument();
                try
                {
                    readerdom.LoadXml(result.ReaderXml);
                }
                catch
                {
                    readerdom = null;
                    return null;
                }
            }

            // 2020/9/10
            libraryCode = DomUtil.GetElementText(readerdom.DocumentElement, "libraryCode");

            return DomUtil.GetElementText(readerdom.DocumentElement, "readerType");
        }

        // 包装后的版本
        // 获得流通参数
        // parameters:
        //      strLibraryCode  图书馆代码, 如果为空,表示使用<library>元素以外的片段
        // return:
        //      reader和book类型均匹配 算4分
        //      只有reader类型匹配，算3分
        //      只有book类型匹配，算2分
        //      reader和book类型都不匹配，算1分
        static int GetLoanParam(
            string strLibraryCode,
            string strReaderType,
            string strBookType,
            string strParamName,
            out string strParamValue,
            out MatchResult matchresult,
#if DEBUG_LOAN_PARAM
            out string strDebug,
#endif
            out string strError)
        {
            strParamValue = "";
            strError = "";
            matchresult = MatchResult.None;

            XmlDocument dom = new XmlDocument();
            try
            {
                dom.LoadXml(_rightTableXml);
            }
            catch (Exception ex)
            {
                strError = $"读者借阅权限 XML 装入 DOM 时出错: {ex.Message}";
                return -1;
            }


            XmlNode root = dom.DocumentElement;

            return LoanParam.GetLoanParam(
                root,    // this.LibraryCfgDom,
                strLibraryCode,
                strReaderType,
                strBookType,
                strParamName,
                out strParamValue,
                out matchresult,
#if DEBUG_LOAN_PARAM
                out strDebug,
#endif
                out strError);
        }

        public class GetTypeMaxResult : NormalResult
        {
            public string PatronType { get; set; }
            public string BookType { get; set; }
            // 指定图书类型(对于指定读者类型)的允许借阅最大册数
            public int Max { get; set; }

            public override string ToString()
            {
                return $"PatronType={PatronType},BookType={BookType},Max={Max}," + base.ToString();
            }
        }

        // 获得特定图书类型的最大可借册数
        static GetTypeMaxResult GetTypeMax(string strLibraryCode,
            string strReaderType,
            string strBookType)
        {
            // 得到该类图书的册数限制配置
            // return:
            //      reader和book类型均匹配 算4分
            //      只有reader类型匹配，算3分
            //      只有book类型匹配，算2分
            //      reader和book类型都不匹配，算1分
            int nRet = GetLoanParam(
                //null,
                strLibraryCode,
                strReaderType,
                strBookType,
                "可借册数",
                out string strParamValue,
                out MatchResult matchresult,
                out string strError);
            if (nRet == -1 || nRet < 4)
            {
                strError = "馆代码 '" + strLibraryCode + "' 中 读者类型 '" + strReaderType + "' 图书类型 '" + strBookType + "' 尚未定义 可借册数 参数";
                return new GetTypeMaxResult
                {
                    Value = -1,
                    ErrorInfo = strError
                };
            }

            // 看看是此类否超过册数限制
            int nThisTypeMax = 0;
            try
            {
                nThisTypeMax = Convert.ToInt32(strParamValue);
            }
            catch
            {
                strError = "馆代码 '" + strLibraryCode + "' 中 读者类型 '" + strReaderType + "' 图书类型 '" + strBookType + "' 的 可借册数 参数值 '" + strParamValue + "' 格式有问题";
                return new GetTypeMaxResult
                {
                    Value = -1,
                    ErrorInfo = strError
                };
            }

            return new GetTypeMaxResult
            {
                Value = 0,
                Max = nThisTypeMax,
                PatronType = strReaderType,
                BookType = strBookType
            };
        }

        // 获得特定读者类型的最大可借册数
        public static GetTypeMaxResult GetTotalMax(string strLibraryCode,
            string strReaderType)
        {

            // 得到该读者类型针对所有类型图书的总册数限制配置
            // return:
            //      reader和book类型均匹配 算4分
            //      只有reader类型匹配，算3分
            //      只有book类型匹配，算2分
            //      reader和book类型都不匹配，算1分
            int nRet = GetLoanParam(
                //null,
                strLibraryCode,
                strReaderType,
                "",
                "可借总册数",
                out string strParamValue,
                out MatchResult matchresult,
                out string strError);
            if (nRet == -1)
            {
                strError = "在获取馆代码 '" + strLibraryCode + "' 中 读者类型 '" + strReaderType + "' 的 可借总册数 参数过程中出错: " + strError + "。";
                return new GetTypeMaxResult
                {
                    Value = -1,
                    ErrorInfo = strError
                };
            }
            if (nRet < 3)
            {
                strError = "馆代码 '" + strLibraryCode + "' 中 读者类型 '" + strReaderType + "' 尚未定义 可借总册数 参数";
                return new GetTypeMaxResult
                {
                    Value = -1,
                    ErrorInfo = strError
                };
            }

            // 然后看看总册数是否已经超过限制
            int nMax = 0;
            try
            {
                nMax = Convert.ToInt32(strParamValue);
            }
            catch
            {
                strError = "馆代码 '" + strLibraryCode + "' 中 读者类型 '" + strReaderType + "' 的 可借总册数 参数值 '" + strParamValue + "' 格式有问题";
                return new GetTypeMaxResult
                {
                    Value = -1,
                    ErrorInfo = strError
                };
            }

            return new GetTypeMaxResult
            {
                Value = 0,
                Max = nMax,
                PatronType = strReaderType,
            };
        }

        // 获得借期
        static NormalResult GetPeriod(string strLibraryCode,
            string strReaderType,
            string strBookType)
        {
            // return:
            //      reader和book类型均匹配 算4分
            //      只有reader类型匹配，算3分
            //      只有book类型匹配，算2分
            //      reader和book类型都不匹配，算1分
            int nRet = GetLoanParam(
            //null,
            strLibraryCode,
            strReaderType,
            strBookType,
            "借期",
            out string strBorrowPeriodList,
            out MatchResult matchresult,
            out string strError);
            if (nRet == -1)
            {
                strError = "借阅失败。获得 馆代码 '" + strLibraryCode + "' 中 读者类型 '" + strReaderType + "' 针对图书类型 '" + strBookType + "' 的 借期 参数时发生错误: " + strError;
                return new NormalResult
                {
                    Value = -1,
                    ErrorInfo = strError
                };
            }
            if (nRet < 4)  // nRet == 0
            {
                strError = "借阅失败。馆代码 '" + strLibraryCode + "' 中 读者类型 '" + strReaderType + "' 针对图书类型 '" + strBookType + "' 的 借期 参数无法获得: " + strError;
                return new NormalResult
                {
                    Value = -1,
                    ErrorInfo = strError
                };
            }

            // 按照逗号分列值，需要根据序号取出某个参数
            string[] aPeriod = strBorrowPeriodList.Split(new char[] { ',' });

            if (aPeriod.Length == 0)
            {
                strError = "借阅失败。馆代码 '" + strLibraryCode + "' 中 读者类型 '" + strReaderType + "' 针对图书类型 '" + strBookType + "' 的 借期 参数 '" + strBorrowPeriodList + "'格式错误";
                return new NormalResult
                {
                    Value = -1,
                    ErrorInfo = strError
                };
            }

            return new NormalResult
            {
                Value = 0,
                ErrorCode = aPeriod[0]
            };
        }



#if NO
        // 获得一个读者当前的在借册册数
        static int GetBorrowItemCount(string pii)
        {
            using (var context = new RequestContext())
            {
                // 该读者的在借册册数
                return context.Requests
                    .Where(o => o.OperatorID == pii && o.Action == "borrow" && o.LinkID == null)
                    .OrderBy(o => o.ID).Count();
            }
        }
#endif

        // 在读者记录中调整增减本地操作的册
        public static void AddLocalBorrowItems(XmlDocument readerdom)
        {
            string patron_pii = DomUtil.GetElementText(readerdom.DocumentElement, "barcode");

            using (var context = new RequestContext())
            {
                List<string> results = new List<string>();
                // 遍历现有读者记录中的在借册
                if (readerdom != null && readerdom.DocumentElement != null)
                {
                    // 打算删除的 borrow 元素
                    List<XmlElement> remove_borrows = new List<XmlElement>();

                    XmlNodeList borrows = readerdom.DocumentElement.SelectNodes("borrows/borrow");
                    foreach (XmlElement borrow in borrows)
                    {
                        string borrowDate = borrow.GetAttribute("borrowDate");
                        DateTime borrowTime;
                        try
                        {
                            borrowTime = DateTimeUtil.FromRfc1123DateTimeString(borrowDate).ToLocalTime();
                        }
                        catch (Exception ex)
                        {
                            WpfClientInfo.WriteErrorLog($"AddLocalBorrowItems() FromRfc1123DateTimeString({borrowDate}) 出现异常: {ExceptionUtil.GetDebugText(ex)}");
                            continue;
                        }

                        var item_pii = borrow.GetAttribute("barcode");
                        var item_oi = borrow.GetAttribute("oi");
                        string oi_pii = item_oi + "." + item_pii;

                        // 如果借阅时间以后发生过还书，则排除
                        var items = context.Requests
    .Where(o => o.OperTime > borrowTime && o.OperatorID == patron_pii && o.Action == "return" && o.PII == oi_pii)
    .ToList();
                        if (items.Count > 0)
                        {
                            remove_borrows.Add(borrow);
                            continue;
                        }
                        results.Add(oi_pii);
                    }

                    foreach (var borrow in remove_borrows)
                    {
                        borrow.ParentNode.RemoveChild(borrow);
                    }
                }

                // TODO: 在联网情况下，不计入本地的在借册？

                XmlElement container = readerdom.DocumentElement.SelectSingleNode("borrows") as XmlElement;
                if (container == null)
                {
                    container = readerdom.CreateElement("borrows");
                    readerdom.DocumentElement.AppendChild(container);
                }

                // 该读者本地的在借册。注：字符串中含有点
                var local_items = context.Requests
                    .Where(o => o.OperatorID == patron_pii && o.Action == "borrow" && o.LinkID == null
                    && o.State != "dontsync"   // 2020/6/17 注：dontsync 表示同步时候实际上另外已经有前端对本册进行了操作(若能操作成功可以推测是还书操作)，所以这一册实际上已经还了，不要计入在借册列表中
                    && o.SyncErrorCode != "AlreadyBorrowed") // 2020/11/17  过滤掉 书柜借书时服务器返回已经是在借状态，借书被拒绝的情况。
                    .ToList();  // .Select(o => o.PII).ToList();
                foreach (var item in local_items)
                {
                    string current_pii = item.PII;
                    if (results.IndexOf(current_pii) == -1)
                    {
                        // 添加 borrow 元素
                        var borrow = container.AppendChild(readerdom.CreateElement("borrow")) as XmlElement;
                        borrow.SetAttribute("barcode", GetPiiPart(current_pii));
                        borrow.SetAttribute("oi", GetOiPart(current_pii, false));
                        borrow.SetAttribute("borrowDate", DateTimeUtil.Rfc1123DateTimeStringEx(item.OperTime));

                        /*
                        if (string.IsNullOrEmpty(item.OperatorString) == false)
                        {
                            try
                            {
                                var person = JsonConvert.DeserializeObject<Operator>(item.OperatorString);
                                borrow.SetAttribute("borrower", person.PatronBarcode);
                            }
                            catch
                            {

                            }
                        }
                        */

                        if (string.IsNullOrEmpty(item.ActionString) == false)
                        {
                            try
                            {
                                var borrow_info = JsonConvert.DeserializeObject<BorrowInfo>(item.ActionString);
                                borrow.SetAttribute("borrowPeriod", borrow_info.Period);
                                borrow.SetAttribute("returningDate", borrow_info.LatestReturnTime);
                                borrow.SetAttribute("operator", borrow_info.BorrowOperator);
                                borrow.SetAttribute("borrowID", borrow_info.BorrowID);
                                if (borrow_info.Overflows != null && borrow_info.Overflows.Length > 0)
                                    borrow.SetAttribute("overflow", string.Join("; ", borrow_info.Overflows));
                            }
                            catch (Exception ex)
                            {
                                WpfClientInfo.WriteErrorLog($"AddLocalBorrowItems() 出现异常: {ExceptionUtil.GetDebugText(ex)}");
                            }
                        }

                        results.Add(current_pii);   // 含有点
                    }
                }
            }
        }

        // TODO: 用 AddLocalBorrowItems() 实现这个函数，以防止两边出现不一致
        // 获得一个读者当前的在借册的 PII 列表
        // 用本地读者记录和本地操作记录一起合成
        // parameters:
        //      readerdom   用于参考的读者记录 XmlDocument 对象。可以为 null
        static List<string> GetBorrowItems(string patron_pii,
            XmlDocument readerdom)
        {
            using (var context = new RequestContext())
            {
                List<string> results = new List<string>();
                // 遍历现有读者记录中的在借册
                if (readerdom != null && readerdom.DocumentElement != null)
                {
                    XmlNodeList borrows = readerdom.DocumentElement.SelectNodes("borrows/borrow");
                    foreach (XmlElement borrow in borrows)
                    {
                        string borrowDate = borrow.GetAttribute("borrowDate");
                        DateTime borrowTime;
                        try
                        {
                            borrowTime = DateTimeUtil.FromRfc1123DateTimeString(borrowDate).ToLocalTime();
                        }
                        catch (Exception ex)
                        {
                            WpfClientInfo.WriteErrorLog($"GetBorrowItems() FromRfc1123DateTimeString({borrowDate}) 出现异常: {ExceptionUtil.GetDebugText(ex)}");
                            continue;
                        }

                        var item_pii = borrow.GetAttribute("barcode");
                        var item_oi = borrow.GetAttribute("oi");
                        string oi_pii = item_oi + "." + item_pii;
                        // 2020/9/24
                        // 注意早期的读者记录中 borrow 元素没有 oi 属性。这时宽容一点处理
                        if (string.IsNullOrEmpty(item_oi))
                            oi_pii = item_pii;

                        // 如果借阅时间以后发生过还书，则排除
                        var items = context.Requests
    .Where(o => o.OperTime > borrowTime && o.OperatorID == patron_pii && o.Action == "return" && o.PII == oi_pii)
    .ToList();
                        if (items.Count > 0)
                            continue;
                        results.Add(oi_pii);
                    }
                }

                // TODO: 在联网情况下，不计入本地的在借册？

                // 该读者本地的在借册。注：字符串中含有点
                var local_items = context.Requests
                    .Where(o => o.OperatorID == patron_pii && o.Action == "borrow" && o.LinkID == null
                    && o.State != "dontsync")   // 2020/6/17 注：dontsync 表示同步时候实际上另外已经有前端对本册进行了操作(若能操作成功可以推测是还书操作)，所以这一册实际上已经还了，不要计入在借册列表中
                    .Select(o => o.PII).ToList();
                foreach (var current_pii in local_items)
                {
                    // TODO: 可以在这里验证一下 dp2library 一端的册记录，该册是否已经还了(指通过内务而不是书柜还的)
                    if (results.IndexOf(current_pii) == -1)
                        results.Add(current_pii);
                }

                return results;
            }
        }

        // 从 "阅览室:1-1" 中析出 "阅览室" 部分
        static string GetLocationPart(string shelfNo)
        {
            return StringUtil.ParseTwoPart(shelfNo, ":")[0];
        }

        public static string GetLibraryCode(string shelfNo)
        {
            string location = GetLocationPart(shelfNo);
            ParseLocation(location, out string libraryCode, out string room);
            return libraryCode;
        }

        static void ParseLocation(string strName,
        out string strLibraryCode,
        out string strPureName)
        {
            strLibraryCode = "";
            strPureName = "";
            int nRet = strName.IndexOf("/");
            if (nRet == -1)
            {
                strPureName = strName;
                return;
            }
            strLibraryCode = strName.Substring(0, nRet).Trim();
            strPureName = strName.Substring(nRet + 1).Trim();
        }

        // 将 actions 保存起来
        public static void PushActions(List<ActionInfo> actions)
        {
            lock (_syncRoot_actions)
            {
                _actions.AddRange(actions);
            }
        }


        // 限制询问对话框，同一时刻只能打开一个对话框
        static AsyncSemaphore _askLimit = new AsyncSemaphore(1);


        public delegate void Delegate_removeAction(ActionInfo action);

        // 询问典藏移交的一些条件参数
        // parameters:
        //      actions     在本函数处理过程中此集合内的对象可能被修改，集合元素可能被移除
        // return:
        //      false   没有发生询问
        //      true    发生了询问
        public static async Task<bool> AskLocationTransferAsync(List<ActionInfo> actions,
            Delegate_removeAction func_removeAction)
        {
            bool bAsked = false;
            // 1) 搜集信息。观察是否有需要询问和兑现的参数
            {
                List<ActionInfo> transferins = new List<ActionInfo>();
                foreach (var action in actions)
                {
                    if (action.Action.StartsWith("transfer")
                        && action.TransferDirection == "in"
                        && string.IsNullOrEmpty(action.Location) == false)
                    {
                        transferins.Add(action);
                    }
                }

                // 询问放入的图书是否需要移交到当前书柜馆藏地
                if (transferins.Count > 0)
                {
                    using (var releaser = await _askLimit.EnterAsync())
                    {
                        bAsked = true;
                        App.CurrentApp.Speak("上架");
                        string batchNo = transferins[0].Operator.GetWorkerAccountName() + "_" + DateTime.Now.ToShortDateString();
                        /*
                        EntityCollection collection = new EntityCollection();
                        foreach (var action in transferins)
                        {
                            Entity dup = action.Entity.Clone();
                            dup.Container = collection;
                            dup.Waiting = false;
                            collection.Add(dup);
                        }
                        */
                        EntityCollection collection = BuildEntityCollection(transferins);
                        string selection = "";
                        App.Invoke(new Action(() =>
                        {
                            App.PauseBarcodeScan();
                            try
                            {
                                var door_names = StringUtil.MakePathList(GetDoorName(transferins), ",");
                                AskTransferWindow dialog = new AskTransferWindow();
                                dialog.TitleText = $"上架({door_names})";
                                dialog.TransferButtonText = "上架+调入";
                                dialog.NotButtonText = "普通上架";
                                dialog.SetBooks(collection);
                                dialog.Text = $"如何处理以上放入 {door_names} 的 {collection.Count} 册图书？";
                                dialog.Owner = App.CurrentApp.MainWindow;
                                dialog.BatchNo = batchNo;
                                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                                App.SetSize(dialog, "tall");

                                //dialog.Width = Math.Min(700, App.CurrentApp.MainWindow.ActualWidth);
                                //dialog.Height = Math.Min(900, App.CurrentApp.MainWindow.ActualHeight);
                                dialog.ShowDialog();
                                selection = dialog.Selection;
                                batchNo = dialog.BatchNo;
                            }
                            finally
                            {
                                App.ContinueBarcodeScan();
                            }
                        }));

                        // 把 transfer 动作里的 Location 成员清除
                        if (selection == "not")
                        {
                            foreach (var action in transferins)
                            {
                                action.Location = "";

                                // 把不需要操作的 ActionInfo 删除
                                if (string.IsNullOrEmpty(action.Location)
                                    && string.IsNullOrEmpty(action.CurrentShelfNo))
                                {
                                    actions.Remove(action);
                                    func_removeAction?.Invoke(action);
                                }
                            }
                        }
                        else
                        {
                            // 2022/3/18
                            // 检查 transfer 动作里的 Location 成员，如果跨越机构代码，则改为普通上架
                            await CheckOiChangingAsync(transferins, "in");
#if REMOVED
                            List<string> errors = new List<string>();
                            foreach (var action in transferins)
                            {
                                string old_location = action.Entity.Location;

                                // 获取册记录馆藏地
                                if (old_location == null)
                                {
                                    var get_result = await GetEntityDataAsync(action.Entity.GetOiPii(),
    ShelfData.LibraryNetworkCondition == "OK" ? "" : "offline");
                                    if (get_result.Value == -1 || get_result.Value == 0)
                                    {

                                    }
                                    else
                                        old_location = GetLocation(get_result.ItemXml);
                                }

                                string new_location = action.Location;

                                var error = IsOiChanging(old_location, new_location);
                                if (error != null)
                                {
                                    action.Location = "";   // 不改变
                                    errors.Add(action.Entity.GetOiPii() + ":" + error);
                                }
                            }
                            if (errors.Count > 0)
                                App.ErrorBox("调入",
                                    $"下列 {errors.Count} 册因机构代码可能发生变化，而从调拨改为普通上架: \r\n{StringUtil.MakePathList(errors, "\r\n")}",
                                    "green");
#endif


                            foreach (var action in transferins)
                            {
                                action.BatchNo = batchNo;
                            }
                        }
                    }
                }
            }

            // 2) 搜集信息。观察是否有移交出
            {
                List<ActionInfo> transferouts = new List<ActionInfo>();
                foreach (var action in actions)
                {
                    if (action.Action.StartsWith("transfer")
                        && action.TransferDirection == "out"
                        && string.IsNullOrEmpty(action.Location) == false)
                    {
                        transferouts.Add(action);
                    }
                }

                // 询问放入的图书是否需要移交到当前书柜馆藏地
                if (transferouts.Count > 0)
                {
                    using (var releaser = await _askLimit.EnterAsync())
                    {
                        bAsked = true;
                        App.CurrentApp.Speak("下架");

                        string batchNo = transferouts[0].Operator.GetWorkerAccountName() + "_" + DateTime.Now.ToShortDateString();

                        // TODO: 这个列表是否在程序初始化的时候得到?
                        // var result = LibraryChannelUtil.GetLocationList();
                        /*
                        EntityCollection collection = new EntityCollection();
                        foreach (var action in transferouts)
                        {
                            Entity dup = action.Entity.Clone();
                            dup.Container = collection;
                            dup.Waiting = false;
                            collection.Add(dup);
                        }
                        */
                        EntityCollection collection = BuildEntityCollection(transferouts);
                        string selection = "";
                        string target = "";
                        App.Invoke(new Action(() =>
                        {
                            App.PauseBarcodeScan();
                            try
                            {
                                // 下架时，要从列表中排除当前书柜所在的 location
                                List<string> locations = new List<string>(_locationList);
                                foreach (var location in GetLocation(transferouts)) // 所涉及的图书的馆藏地汇总
                                {
                                    locations.Remove(location);
                                }

                                var door_names = StringUtil.MakePathList(GetDoorName(transferouts), ",");
                                AskTransferWindow dialog = new AskTransferWindow();
                                dialog.TitleText = $"下架({door_names})";
                                dialog.TransferButtonText = "下架+调出";
                                dialog.NotButtonText = "普通下架";
                                dialog.Mode = "out";
                                dialog.SetBooks(collection);
                                dialog.Text = $"如何处理以上从 {door_names} 取走的 {collection.Count} 册图书？";
                                dialog.target.ItemsSource = locations;  // _locationList;
                                dialog.BatchNo = batchNo;
                                dialog.Owner = App.CurrentApp.MainWindow;
                                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                                App.SetSize(dialog, "tall");

                                //dialog.Width = Math.Min(700, App.CurrentApp.MainWindow.ActualWidth);
                                //.Height = Math.Min(900, App.CurrentApp.MainWindow.ActualHeight);
                                dialog.ShowDialog();
                                selection = dialog.Selection;
                                target = dialog.Target;
                                batchNo = dialog.BatchNo;
                            }
                            finally
                            {
                                App.ContinueBarcodeScan();
                            }
                        }));

                        // 把 transfer 动作里的 Location 成员清除
                        if (selection == "not")
                        {
                            foreach (var action in transferouts)
                            {
                                // 修改 Action
                                action.Location = "";
                                // 注: action.CurrentShelfNo 也为空
                                // 注: action.TransferDirection 为 "out"

                                /*
                                // 把不需要操作的 ActionInfo 删除
                                if (string.IsNullOrEmpty(action.Location)
                                    && string.IsNullOrEmpty(action.CurrentShelfNo))
                                {
                                    actions.Remove(action);
                                    func_removeAction?.Invoke(action);
                                }
                                */

                            }
                        }
                        else
                        {
                            foreach (var action in transferouts)
                            {
                                action.Location = target;
                                action.BatchNo = batchNo;
                            }

                            // 2022/3/18
                            // 检查 transfer 动作里的 Location 成员，如果跨越机构代码，则改为普通下架
                            await CheckOiChangingAsync(transferouts, "out");
                        }
                    }
                }
            }

            return bAsked;
        }

        async static Task CheckOiChangingAsync(List<ActionInfo> actions,
            string direction)
        {
            // 检查 transfer 动作里的 Location 成员，如果跨越机构代码，则拒绝移交
            List<string> errors = new List<string>();
            foreach (var action in actions)
            {
                string new_location = action.Location;
                if (string.IsNullOrEmpty(new_location))
                    continue;

                string old_location = action.Entity.Location;

                // 获取册记录馆藏地
                if (old_location == null)
                {
                    var get_result = await GetEntityDataAsync(action.Entity.GetOiPii(),
ShelfData.LibraryNetworkCondition == "OK" ? "" : "offline");
                    if (get_result.Value == -1 || get_result.Value == 0)
                    {

                    }
                    else
                        old_location = GetLocation(get_result.ItemXml);
                }

                var error = IsOiChanging(old_location, new_location);
                if (error != null)
                {
                    action.Location = "";   // 不改变
                    errors.Add(action.Entity.GetOiPii() + ":" + error);
                }
            }

            StringBuilder text = new StringBuilder();
            int i = 0;
            foreach (string error in errors)
            {
                text.AppendLine($"{(i + 1)}) {error}");
                i++;
            }

            if (errors.Count > 0)
                App.ErrorBox(direction == "in" ? "调入" : "调出",
                    $"下列 {errors.Count} 册因机构代码可能发生变化，而从调拨改为普通{(direction == "in" ? "上架" : "下架")}: \r\n{text.ToString()}",
                    "yellow");
        }

        // 从册记录中获得馆藏地
        static string GetLocation(string item_xml)
        {
            if (string.IsNullOrEmpty(item_xml))
                return null;
            XmlDocument dom = new XmlDocument();
            try
            {
                dom.LoadXml(item_xml);
            }
            catch
            {
                return null;
            }
            var location = DomUtil.GetElementText(dom.DocumentElement, "location");
            return StringUtil.GetPureLocation(location);
        }

        // 概括门名字
        public static List<string> GetLocation(List<ActionInfo> actions_param)
        {
            List<DoorItem> results = new List<DoorItem>();
            foreach (var action in actions_param)
            {
                var doors = DoorItem.FindDoors(ShelfData.Doors, action.Entity.ReaderName, action.Entity.Antenna);
                Add(results, doors);
            }

            List<string> names = new List<string>();
            foreach (var door in results)
            {
                names.Add(GetLocationPart(door.ShelfNo));
            }

            StringUtil.RemoveDupNoSort(ref names);
            return names;

            void Add(List<DoorItem> target, List<DoorItem> doors)
            {
                foreach (var door in doors)
                {
                    if (target.IndexOf(door) == -1)
                        target.Add(door);
                }
            }
        }

        // 概括门名字
        public static List<string> GetDoorName(List<ActionInfo> actions_param)
        {
            List<DoorItem> results = new List<DoorItem>();
            foreach (var action in actions_param)
            {
                var doors = DoorItem.FindDoors(ShelfData.Doors, action.Entity.ReaderName, action.Entity.Antenna);
                Add(results, doors);
            }

            List<string> names = new List<string>();
            foreach (var door in results)
            {
                names.Add(door.Name);
            }

            return names;

            void Add(List<DoorItem> target, List<DoorItem> doors)
            {
                foreach (var door in doors)
                {
                    if (target.IndexOf(door) == -1)
                        target.Add(door);
                }
            }
        }

        static EntityCollection BuildEntityCollection(List<ActionInfo> actions)
        {
            EntityCollection collection = new EntityCollection();
            foreach (var action in actions)
            {
                Entity dup = action.Entity.Clone();
                dup.Container = collection;
                dup.Waiting = false;
                // testing
                // dup.Title = null;
                dup.FillFinished = false;
                collection.Add(dup);
            }

            return collection;
        }

        // 用于保护 _all _adds _removes _changes 的锁对象
        static object _syncRoot_all = new object();

        static List<Entity> _all = new List<Entity>();  // 累积的全部图书
        static List<Entity> _adds = new List<Entity>(); // 临时区 放入的图书
        static List<Entity> _removes = new List<Entity>();  // 临时区 取走的图书
        static List<Entity> _changes = new List<Entity>();  // 临时区 天线编号、门位置发生过变化的图书

        public static IReadOnlyCollection<Entity> l_All
        {
            get
            {
                lock (_syncRoot_all)
                {
                    List<Entity> results = new List<Entity>(_all);
                    return results;
                    // return _all.AsReadOnly();
                }
            }
        }

        public static IReadOnlyCollection<Entity> l_Adds
        {
            get
            {
                lock (_syncRoot_all)
                {
                    return new List<Entity>(_adds);
                }
            }
        }

        public static IReadOnlyCollection<Entity> l_Removes
        {
            get
            {
                lock (_syncRoot_all)
                {
                    return new List<Entity>(_removes);
                }
            }
        }

        public static IReadOnlyCollection<Entity> l_Changes
        {
            get
            {
                lock (_syncRoot_all)
                {
                    return new List<Entity>(_changes);
                }
            }
        }

        /*
        static Operator _operator = null;   // 当前控制临时区的读者身份

        public static Operator Operator
        {
            get
            {
                return _operator;
            }
        }
        */

        // 初始化门控件定义。包括初始化 ShelfCfgDom
        // 异常：
        //      可能会抛出 Exception 异常
        public static void InitialDoors()
        {
            {
                string cfg_filename = ShelfFilePath;
                XmlDocument cfg_dom = new XmlDocument();
                cfg_dom.Load(cfg_filename);

                _shelfCfgDom = cfg_dom;
            }

            // 2019/12/22
            if (_doors != null)
                _doors.Clear();
            _doors = DoorItem.BuildItems(_shelfCfgDom, out List<string> errors);

            if (errors.Count > 0)
                throw new Exception(StringUtil.MakePathList(errors, "; "));
        }

        static bool _firstInitial = false;

        public static bool FirstInitialized
        {
            get
            {
                return _firstInitial;
            }
            set
            {
                _firstInitial = value;
            }
        }

        public static async Task<NormalResult> WaitLockReadyAsync(
            Delegate_displayText func_display,
            Delegate_cancelled func_cancelled)
        {
            WpfClientInfo.WriteInfoLog("等待锁控就绪");
            func_display("等待锁控就绪 ...");
            bool ret = await Task.Run(() =>
            {
                while (true)
                {
                    if (OpeningDoorCount != -1)
                        return true;
                    if (func_cancelled() == true)
                        return false;
                    Thread.Sleep(100);
                }
            });

            if (ret == false)
                return new NormalResult
                {
                    Value = -1,
                    ErrorInfo = "用户中断",
                    ErrorCode = "cancelled"
                };

            return new NormalResult();
        }

        public class WriteInitialLogResult : NormalResult
        {
            public string FileName { get; set; }
            public string Time { get; set; }
        }

        // 写入详细的初始化信息到一个专门的 .log 文件，避免基本的 .log 文件尺寸太大
        public static WriteInitialLogResult WriteInitialLog(string text)
        {
            DateTime now = DateTime.Now;
            string path = Path.Combine(WpfClientInfo.UserLogDir, "initial_" + DateTimeUtil.DateTimeToString8(now) + ".txt");
            string time = now.ToString("yyyy-MM-dd HH:mm:ss.ffff");
            File.AppendAllText(path, "=== " + time + " ===\r\n" + text + "\r\n");
            return new WriteInitialLogResult
            {
                FileName = path,
                Time = time
            };
        }

        public delegate void Delegate_displayText(string text);
        public delegate bool Delegate_cancelled();

        public class InitialShelfResult : NormalResult
        {
            public List<string> Warnings { get; set; }
            public List<Entity> All { get; set; }
        }

        // 首次初始化智能书柜所需的标签相关数据结构
        // 初始化开始前，要先把 RfidManager.ReaderNameList 设置为 "*"
        // 初始化完成前，先不要允许(开关门变化导致)修改 RfidManager.ReaderNameList
        public static async Task<InitialShelfResult> newVersion_InitialShelfEntitiesAsync(
            List<DoorItem> doors_param,
            bool silently,
            Delegate_setProgress func_setProgress,
            // Delegate_displayText func_display,
            Delegate_cancelled func_cancelled)
        {
            // TODO: 出现“正在初始化”的对话框。另外需要注意如果 DataReady 信号永远来不了怎么办
            WpfClientInfo.WriteInfoLog("开始初始化图书信息");
            func_display("开始初始化图书信息 ...");

            void func_display(string text)
            {
                func_setProgress?.Invoke(-1, -1, -1, text);
            }

            // 一个一个门地填充图书信息
            int i = 0;
            foreach (var door in doors_param)
            {
                if (func_cancelled() == true)
                    return new InitialShelfResult();

                // 获得和一个门相关的 readernamelist
                var list = GetReaderNameList(new List<DoorItem> { door }, null);
                string style = $"dont_delay";   // 确保 inventory 并立即返回

                // func_display($"{i + 1}/{Doors.Count} 门 {door.Name} ({list}) ...");
                func_display($"列举标签 {door.Name} ({list}) ...");

                using (var releaser = await _inventoryLimit.EnterAsync().ConfigureAwait(false))
                {
                    App.GetTagInfoProgressChanged += App_GetTagInfoProgressChanged;
                    var result = RfidManager.CallListTags(list, style);
                    try
                    {
                        await RfidManager.TriggerListTagsEvent(list,
                            result,
                            "initial",
                            true);

#if AUTO_TEST
                        ShelfData.BookTagList.AssertTagInfo();
#endif
                    }
                    catch (TagInfoException ex)
                    {
                        // 2020/4/9
                        string error = $"出现无法解析的标签 UID:{ex.TagInfo.UID}";
                        WpfClientInfo.WriteErrorLog($"InitialShelfEntities() 异常: {error} 门:{door.Name}");
                        return new InitialShelfResult
                        {
                            Value = -1,
                            ErrorInfo = error
                        };
                    }
                    finally
                    {
                        App.GetTagInfoProgressChanged -= App_GetTagInfoProgressChanged;
                    }
                }

                void App_GetTagInfoProgressChanged(object sender, ProgressChangedEventArgs e)
                {
                    /*
                    if (e.Message != null)
                        func_display($"门 {door.Name} ({list}) {e.Message}...");
                    */
                    func_setProgress(e.Start, e.End, e.Value, $"读卡器\t{list}\r\n标签\t{e.Message}...");
                }

                i++;
            }

            if (func_cancelled() == true)
                return new InitialShelfResult();

            WpfClientInfo.WriteInfoLog("开始填充图书队列");
            func_display("正在填充图书队列 ...");

            List<string> warnings = new List<string>();

            List<Entity> all = new List<Entity>();
            lock (_syncRoot_all)
            {
                // _all.Clear();

#if OLD_TAGCHANGED
                var books = TagList.Books;
#else
                var books = ShelfData.BookTagList.Tags;
                // TODO: 注意里面也包含了读者卡，需要过滤一下
#endif

                StringBuilder debug_info = new StringBuilder();
                WpfClientInfo.WriteInfoLog($"books count={books.Count}, ReaderNameList={RfidManager.ReaderNameList}(注：此时门应该都是关闭的，图书读卡器应该是停止盘点状态)");
                int line = 0;
                foreach (var tag in books)
                {
                    if (func_cancelled() == true)
                        return new InitialShelfResult();

                    // 跳过读者读卡器上的标签
                    if (tag.OneTag.ReaderName == _patronReaderName)
                        continue;

                    // 2019/12/17
                    // 判断一下 tag 是否属于已经定义的门范围
                    var doors = DoorItem.FindDoors(ShelfData.Doors, tag.OneTag.ReaderName, tag.OneTag.AntennaID.ToString());
                    if (doors.Count == 0)
                    {
                        // 注：这里可能会重复多次报错
                        WpfClientInfo.WriteInfoLog($"tag (UID={tag.OneTag?.UID},Antenna={tag.OneTag.AntennaID}) 不属于任何已经定义的门，没有被加入 _all 集合。\r\ntag 详情：{tag.ToString()}");
                        debug_info.AppendLine($"tag (UID={tag.OneTag?.UID},Antenna={tag.OneTag.AntennaID}) 不属于任何已经定义的门，没有被加入 _all 集合。\r\ntag 详情：{tag.ToString()}");
                        continue;
                    }

                    // 不属于本函数当前关注的门范围
                    if (Cross(doors_param, doors) == false)
                        continue;

                    // WpfClientInfo.WriteInfoLog($" tag={tag.ToString()}");
                    debug_info.AppendLine($"{++line}) {tag.ToString()}");

                    try
                    {
#if AUTO_TEST
                        Debug.Assert(tag.OneTag.TagInfo != null);
                        tag.Type = null;
#endif

                        // 注：所创建的 Entity 对象其 Error 成员可能有值，表示有出错信息
                        // Exception:
                        //      可能会抛出异常 ArgumentException
                        var entity = NewEntity(tag, false);

#if AUTO_TEST
                        Debug.Assert(string.IsNullOrEmpty(entity.PII) == false);
#endif

                        func_display($"正在填充图书队列 ({GetPiiString(entity)})...");

                        all.Add(entity);

                        /*
                        if (silently == false
    && string.IsNullOrEmpty(entity.OI) == true && string.IsNullOrEmpty(entity.AOI) == true)
                        {
                            warnings.Add($"UID 为 '{tag.OneTag?.UID}' 的标签解析出错: 没有 OI 或 AOI 字段");
                            WpfClientInfo.WriteErrorLog($"InitialShelfEntities() 遇到 tag (UID={tag.OneTag?.UID}) 解析出错: 没有 OI 或 AOI 字段");
                        }
                        */

                        if (silently == false
                            && string.IsNullOrEmpty(entity.Error) == false)
                        {
                            warnings.Add($"UID 为 '{tag.OneTag?.UID}' (PII 为 '{entity.PII}') 的标签解析出错: {entity.Error}");
                            WpfClientInfo.WriteErrorLog($"InitialShelfEntities() 遇到 tag (UID={tag.OneTag?.UID}) 解析出错: {entity.Error}\r\ntag 详情：{tag.ToString()}");
                        }
                    }
                    catch (TagDataException ex)
                    {
                        warnings.Add($"UID 为 '{tag.OneTag?.UID}' 的标签出现数据格式错误: {ex.Message}");
                        WpfClientInfo.WriteErrorLog($"InitialShelfEntities() 遇到 tag (UID={tag.OneTag?.UID}) 数据格式出错：{ex.Message}\r\ntag 详情：{tag.ToString()}");
                    }

                    /*
                    // 对读者卡进行判断(注：这些都是在书柜门以内的读卡器上的读者卡)
                    // 属于本函数当前关注的门范围
                    if (tag.Type == "patron")
                    {
                        warnings.Add($"出现读者证标签。UID={tag.OneTag?.UID} Protocol={tag.OneTag?.Protocol}");
                        WpfClientInfo.WriteErrorLog($"InitialShelfEntities() 出现读者证标签。门={doors[0].Name},UID={tag.OneTag?.UID} Protocol={tag.OneTag?.Protocol}\r\ntag 详情：{tag.ToString()}");
                    }
                    */
                }

                if (debug_info.Length > 0)
                {
                    var log_result = WriteInitialLog(debug_info.ToString());
                    WpfClientInfo.WriteInfoLog($"对门 {ToString(doors_param)} 初始化过程中获得的 tag 详细信息已写入另一日志文件 {log_result.FileName}，时刻为 {log_result.Time}");
                }
                else
                {
                    WpfClientInfo.WriteInfoLog($"对门 {ToString(doors_param)} 初始化过程中没有发现 tag");
                }

                string ToString(List<DoorItem> door_list)
                {
                    List<string> names = new List<string>();
                    foreach (var door in door_list)
                    {
                        names.Add(door.Name);
                    }

                    return StringUtil.MakePathList(names);
                }

#if OLD_TAGCHANGED

                // 2020/4/9
                // 检查放在柜门内的 ISO15693 读者卡
                var patrons = TagList.Patrons;
                foreach (var tag in patrons)
                {
                    if (func_cancelled() == true)
                        return new InitialShelfResult();

                    WpfClientInfo.WriteErrorLog($" (读者卡)tag={tag.ToString()}");

                    // 判断一下 tag 是否属于已经定义的门范围
                    var doors = DoorItem.FindDoors(ShelfData.Doors, tag.OneTag.ReaderName, tag.OneTag.AntennaID.ToString());
                    if (doors.Count == 0)
                    {
                        // 这是正常情况：读者卡所放的读卡器不是柜门读卡器
                        continue;
                    }

                    // 属于本函数当前关注的门范围
                    if (Cross(doors_param, doors) == true)
                    {
                        warnings.Add($"出现读者证标签。UID={tag.OneTag?.UID} Protocol={tag.OneTag?.Protocol}");
                        WpfClientInfo.WriteErrorLog($"InitialShelfEntities() 出现读者证标签。门={doors[0].Name},UID={tag.OneTag?.UID} Protocol={tag.OneTag?.Protocol}\r\ntag 详情：{tag.ToString()}");
                    }
                }
#endif
            }

            /*
            // DoorItem.DisplayCount(_all, _adds, _removes, App.CurrentApp.Doors);
            // TODO: 只刷新指定门的数字即可
            l_RefreshCount();
            */

            // TryReturn(progress, _all);
            // _firstInitial = true;   // 第一次初始化已经完成

            /* 这一段可以在函数返回后做
            func_display("获取图书册记录信息 ...");

            var task = Task.Run(async () =>
            {
                CancellationToken token = CancelToken;
                await FillBookFields(All, token);
                await FillBookFields(Adds, token);
                await FillBookFields(Removes, token);
            });
            */

            return new InitialShelfResult
            {
                Warnings = warnings,
                All = all
            };

            // 观察两个集合是否有交集
            bool Cross(List<DoorItem> doors1, List<DoorItem> doors2)
            {
                foreach (var door1 in doors1)
                {
                    if (doors2.IndexOf(door1) != -1)
                        return true;
                }

                return false;
            }
        }

        // Exception:
        //      可能会抛出异常 ArgumentException TagDataException
        static void SetTagType(TagAndData data,
            out string pii,
            out LogicChip chip)
        {
            pii = null;
            chip = null;

            if (data.OneTag.Protocol == InventoryInfo.ISO14443A)
            {
                data.Type = "patron";
                return;
            }

            if (data.OneTag.TagInfo == null)
            {
                data.Type = ""; // 表示类型不确定
                return;
            }

            if (string.IsNullOrEmpty(data.Type))
            {
#if OLD
                // Exception:
                //      可能会抛出异常 ArgumentException TagDataException
                chip = LogicChip.From(data.OneTag.TagInfo.Bytes,
            (int)data.OneTag.TagInfo.BlockSize,
            "" // tag.TagInfo.LockStatus
            );
#endif

                // 2023/11/3
                // 注1: taginfo.EAS 在调用后可能被修改
                // 注2: 本函数不再抛出异常。会在 ErrorInfo 中报错
                var chip_info = RfidTagList.GetChipInfo(data.OneTag.TagInfo);
                
                if (string.IsNullOrEmpty(chip_info.ErrorInfo) == false)
                {
                    data.Type = ""; // 表示类型不确定
                    return;
                }

                chip = chip_info.Chip;

                pii = chip.FindElement(ElementOID.PII)?.Text;

#if AUTO_TEST
                Debug.Assert(string.IsNullOrEmpty(pii) == false);
#endif

                var typeOfUsage = chip.FindElement(ElementOID.TypeOfUsage)?.Text;
                if (typeOfUsage != null && typeOfUsage.StartsWith("8"))
                    data.Type = "patron";
                else
                    data.Type = "book";
            }
        }

#if NO

        // 首次初始化智能书柜所需的标签相关数据结构
        // 初始化开始前，要先把 RfidManager.ReaderNameList 设置为 "*"
        // 初始化完成前，先不要允许(开关门变化导致)修改 RfidManager.ReaderNameList
        public static async Task<InitialShelfResult> InitialShelfEntities(
            Delegate_displayText func_display,
            Delegate_cancelled func_cancelled)
        {
            // TODO: 出现“正在初始化”的对话框。另外需要注意如果 DataReady 信号永远来不了怎么办
            WpfClientInfo.WriteInfoLog("开始初始化图书信息");

            func_display("等待读卡器就绪 ...");
            bool ret = await Task.Run(() =>
            {
                while (true)
                {
                    if (RfidManager.TagsReady == true)
                        return true;
                    if (func_cancelled() == true)
                        return false;
                    Thread.Sleep(100);
                }
            });

            if (ret == false)
                return new InitialShelfResult();

            // 使用全部读卡器、全部天线进行初始化。即便门是全部关闭的(注：一般情况下，当门关闭的时候图书读卡器是暂停盘点的)
            WpfClientInfo.WriteInfoLog("开始启用全部读卡器和天线");

            func_display("启用全部读卡器和天线 ...");
            ret = await Task.Run(() =>
            {
                // 使用全部读卡器，全部天线
                RfidManager.Pause = true;

                // TODO: 这里并不是马上能停下来呀？是否要等待停下来
                // 否则探测到 TagsReady == true 可能是上一轮延迟到来的结果
                // 可以考虑给 TagsReady 变成一个字符串值，内容是每一轮请求的 session_id，这样就可以确认是哪一次的返回了

                //RfidManager.Pause2 = true;  // 暂停 Base2 线程
                RfidManager.ReaderNameList = _allDoorReaderName;
                RfidManager.TagsReady = false;
                RfidManager.Pause = false;
                // 注意此时 Base 线程依然是暂停状态
                RfidManager.ClearCache();   // 迫使立即重新请求 Inventory
                while (true)
                {
                    if (RfidManager.TagsReady == true)
                        return true;
                    if (func_cancelled() == true)
                        return false;
                    Thread.Sleep(100);
                }
            });

            if (ret == false)
            {
                WpfClientInfo.WriteErrorLog($"waiting DataReady cancelled");
                return new InitialShelfResult();
            }

            WpfClientInfo.WriteInfoLog("开始填充图书队列");
            func_display("正在填充图书队列 ...");

            List<string> warnings = new List<string>();

            lock (_syncRoot_all)
            {
                _all.Clear();
                var books = TagList.Books;
                WpfClientInfo.WriteErrorLog($"books count={books.Count}, ReaderNameList={RfidManager.ReaderNameList}");
                foreach (var tag in books)
                {
                    WpfClientInfo.WriteErrorLog($" tag={tag.ToString()}");

                    try
                    {
                        // Exception:
                        //      可能会抛出异常 ArgumentException TagDataException
                        _all.Add(NewEntity(tag));
                    }
                    catch (TagDataException ex)
                    {
                        warnings.Add($"UID 为 '{tag.OneTag?.UID}' 的标签出现数据格式错误: {ex.Message}");
                        WpfClientInfo.WriteErrorLog($"InitialShelfEntities() 遇到 tag (UID={tag.OneTag?.UID}) 数据格式出错：{ex.Message}\r\ntag 详情：{tag.ToString()}");
                    }
                }
            }


            {
                WpfClientInfo.WriteInfoLog("等待锁控就绪");
                func_display("等待锁控就绪 ...");
                // 恢复 Base2 线程运行
                // RfidManager.Pause2 = false;
                ret = await Task.Run(() =>
                {
                    while (true)
                    {
                        if (OpeningDoorCount != -1)
                            return true;
                        if (func_cancelled() == true)
                            return false;
                        Thread.Sleep(100);
                    }
                });

                if (ret == false)
                    return new InitialShelfResult();
            }


            // DoorItem.DisplayCount(_all, _adds, _removes, App.CurrentApp.Doors);
            RefreshCount();

            // TryReturn(progress, _all);
            _firstInitial = true;   // 第一次初始化已经完成

            var task = Task.Run(async () =>
            {
                CancellationToken token = CancelToken;
                await FillBookFields(All, token);
                await FillBookFields(Adds, token);
                await FillBookFields(Removes, token);
            });

            return new InitialShelfResult { Warnings = warnings };
        }


#endif

        // 注：所创建的 Entity 对象其 Error 成员可能有值，表示有出错信息
        // Exception:
        //      可能会抛出异常 ArgumentException
        static Entity NewEntity(TagAndData tag, bool throw_exception = true)
        {
            var result = new Entity
            {
                UID = tag.OneTag.UID,
                ReaderName = tag.OneTag.ReaderName,
                Antenna = tag.OneTag.AntennaID.ToString(),
                TagInfo = tag.OneTag.TagInfo,
            };

            LogicChip chip = null;
            // Exception:
            //      可能会抛出异常 ArgumentException TagDataException
            try
            {
                SetTagType(tag, out string pii, out chip);
#if AUTO_TEST
                Debug.Assert(string.IsNullOrEmpty(pii) == false);
                Debug.Assert(chip != null);
#endif
                result.PII = pii;
            }
            catch (Exception ex)
            {
                App.CurrentApp.SpeakSequence("警告: 标签解析出错");
                if (throw_exception == false)
                {
                    result.AppendError($"RFID 标签格式错误: {ex.Message}",
                        "red",
                        "parseTagError");
                }
                else
                    throw ex;
            }

#if NO
            // Exception:
            //      可能会抛出异常 ArgumentException 
            EntityCollection.SetPII(result, pii);
#endif

            // 2020/4/9
            if (tag.Type == "patron")
            {
                // 避免被当作图书同步到 dp2library
                result.PII = "(读者卡)" + result.PII;
                result.AppendError("读者卡误放入书柜", "red", "patronCard");
            }

            // 2020/7/15
            // 获得图书 RFID 标签的 OI 和 AOI 字段
            if (tag.Type == "book")
            {
                if (chip == null)
                {
                    if (tag.OneTag.Protocol == InventoryInfo.ISO15693)
                    {
                        // Exception:
                        //      可能会抛出异常 ArgumentException TagDataException
                        chip = LogicChip.From(tag.OneTag.TagInfo.Bytes,
                (int)tag.OneTag.TagInfo.BlockSize,
                "" // tag.TagInfo.LockStatus
                );
                    }
                    else if (tag.OneTag.Protocol == InventoryInfo.ISO18000P6C)
                    {
                        // 2023/11/3
                        // 注1: taginfo.EAS 在调用后可能被修改
                        // 注2: 本函数不再抛出异常。会在 ErrorInfo 中报错
                        var chip_info = RfidTagList.GetUhfChipInfo(tag.OneTag.TagInfo);
                        chip = chip_info.Chip;
                    }
                    else
                    {
                        // 无法识别的 RFID 标签协议
                        // TODO: 抛出异常?
                    }
                }

                string oi = chip?.FindElement(ElementOID.OI)?.Text;
                string aoi = chip?.FindElement(ElementOID.AOI)?.Text;

                result.OI = oi;
                result.AOI = aoi;

                // 2020/8/27
                // 严格要求必须有 OI(AOI) 字段
                if (string.IsNullOrEmpty(oi) && string.IsNullOrEmpty(aoi))
                    result.AppendError("没有 OI 或 AOI 字段", "red", "missingOI");
            }
            return result;
        }

        // 检查一本图书是否处在普通(非 free) 类型的门内
        public static bool BelongToNormal(Entity entity)
        {
            var doors = DoorItem.FindDoors(_doors, entity.ReaderName, entity.Antenna);
            int count = 0;
            foreach (DoorItem door in doors)
            {
                if (door.Type == "free")
                    return false;
                count++;
            }
            return count > 0;
        }

        public static string GetShelfNo(Entity entity)
        {
            var doors = DoorItem.FindDoors(_doors, entity.ReaderName, entity.Antenna);
            if (doors.Count == 0)
                return "";
            return doors[0].ShelfNo;
        }

        // 刷新门内图书数字显示
        public static void l_RefreshCount()
        {
            List<Entity> errors = null;
            List<Entity> all = null;
            List<Entity> adds = null;
            List<Entity> removes = null;

            lock (_syncRoot_all)
            {
                all = new List<Entity>(_all);
                adds = new List<Entity>(_adds);
                removes = new List<Entity>(_removes);
                errors = GetErrors(_all, _adds, _removes);
            }
            DoorItem.DisplayCount(all, adds, removes, errors, Doors);
        }

        // 注意，没有加锁
        public static List<Entity> GetErrors(List<Entity> all,
            List<Entity> adds,
            List<Entity> removes)
        {
            List<Entity> errors = new List<Entity>();
            List<Entity> list = new List<Entity>(all);
            list.AddRange(adds);
            list.AddRange(removes);
            foreach (var entity in list)
            {
                if (entity.Error != null && entity.ErrorColor == "red")
                {
                    if (errors.IndexOf(entity) == -1)
                        internalAdd(errors, entity);
                }
            }

            return errors;
        }

        public static List<string> GetLockCommands()
        {
            /*
            string cfg_filename = App.ShelfFilePath;
            XmlDocument cfg_dom = new XmlDocument();
            cfg_dom.Load(cfg_filename);
            */
            return GetLockCommands(ShelfCfgDom);
        }

        // 构造锁命令字符串数组
        public static List<string> GetLockCommands(XmlDocument cfg_dom)
        {
            // lockName --> bool
            Hashtable table = new Hashtable();
            XmlNodeList doors = cfg_dom.DocumentElement.SelectNodes("//door");
            foreach (XmlElement door in doors)
            {
                string lockDef = door.GetAttribute("lock");
                if (string.IsNullOrEmpty(lockDef))
                    continue;

                string lockName = DoorItem.NormalizeLockName(lockDef);
                // DoorItem.ParseReaderString(lockDef, out string lockName, out int lockIndex);
                if (string.IsNullOrEmpty(lockName))
                    continue;

                if (table.ContainsKey(lockName) == false)
                {
                    table[lockName] = true;
                }
                else
                    continue;
            }

            /*
            List<LockCommand> results = new List<LockCommand>();
            foreach (string key in table.Keys)
            {
                StringBuilder text = new StringBuilder();
                int i = 0;
                foreach (var v in table[key] as List<int>)
                {
                    if (i > 0)
                        text.Append(",");
                    text.Append(v);
                    i++;
                }
                results.Add(new LockCommand
                {
                    LockName = key,
                    Indices = text.ToString()
                });
            }
            */
            List<string> lock_names = new List<string>();
            foreach (string s in table.Keys)
            {
                lock_names.Add(s);
            }
            lock_names.Sort();

            return lock_names;
        }

        public delegate bool Delegate_match(Entity entity);

        public static List<Entity> Find(IReadOnlyCollection<Entity> entities,
            Delegate_match func_match)
        {
            List<Entity> results = new List<Entity>();
            /*
            entities.ForEach((o) =>
            {
                if (o.UID == uid)
                    results.Add(o);
            });
            */
            foreach (var o in entities)
            {
                if (func_match(o) == true)
                    results.Add(o);
            }
            return results;
        }

#if NO
        public static List<Entity> Find(IReadOnlyCollection<Entity> entities,
            string uid)
        {
            List<Entity> results = new List<Entity>();
            /*
            entities.ForEach((o) =>
            {
                if (o.UID == uid)
                    results.Add(o);
            });
            */
            foreach (var o in entities)
            {
                if (o.UID == uid)
                    results.Add(o);
            }
            return results;
        }
#endif

        static List<Entity> l_Find(string name, TagAndData tag)
        {
            lock (_syncRoot_all)
            {
                List<Entity> entities = LinkByName(name);

                List<Entity> results = new List<Entity>();
                entities.ForEach((o) =>
                {
                    if (o.UID == tag.OneTag.UID)
                        results.Add(o);
                });
                return results;
            }
        }

        // 注意：这是不加锁的版本
        static List<Entity> Find(List<Entity> entities, TagAndData tag)
        {
            List<Entity> results = new List<Entity>();
            entities.ForEach((o) =>
            {
                if (o.UID == tag.OneTag.UID)
                    results.Add(o);
            });
            return results;
        }

        // return:
        //      false   实际上没有添加(对象以前已经在集合中存在)
        //      true    发生了添加
        internal static bool Add(string name, Entity entity)
        {
            var list = new List<Entity>();
            list.Add(entity);
            if (l_Add(name, list) > 0)
                return true;
            return false;
        }

        static void l_ReplaceOrAdd(List<Entity> entities, TagAndData tag)
        {
            lock (_syncRoot_all)
            {
                var found = entities.FindAll((o) => o.UID == tag.OneTag.UID);
                if (found.Count > 0)
                {
                    foreach (var o in found)
                    {
                        entities.Remove(o);
                    }
                }
                entities.Add(NewEntity(tag, false));
            }
        }

        // 2020/4/19
        // 替换集合中 UID 相同的 Entity 对象。如果没有找到则添加 entity 进入集合
        static void l_ReplaceOrAdd(List<Entity> entities, Entity entity)
        {
            lock (_syncRoot_all)
            {
                var found = entities.FindAll((o) => o.UID == entity.UID);
                if (found.Count > 0)
                {
                    foreach (var o in found)
                    {
                        entities.Remove(o);
                    }
                }
                entities.Add(entity);
            }
        }

        // return:
        //      返回实际添加的个数
        internal static int l_Add(string name,
            IReadOnlyCollection<Entity> adds)
        {
            lock (_syncRoot_all)
            {
                List<Entity> entities = LinkByName(name);

                int count = 0;
                foreach (var entity in adds)
                {
                    Debug.Assert(entity != null, "");
                    Debug.Assert(string.IsNullOrEmpty(entity.UID) == false, "");

                    List<Entity> results = new List<Entity>();
                    entities.ForEach((o) =>
                    {
                        if (o.UID == entity.UID)
                            results.Add(o);
                    });
                    if (results.Count > 0)
                        continue;
                    entities.Add(entity);
                    count++;
                }

                return count;
            }
        }


        /*
        internal static bool Add(string name, Entity entity)
        {
            lock (_syncRoot_all)
            {
                List<Entity> entities = LinkByName(name);

                Debug.Assert(entity != null, "");
                Debug.Assert(string.IsNullOrEmpty(entity.UID) == false, "");

                List<Entity> results = new List<Entity>();
                entities.ForEach((o) =>
                {
                    if (o.UID == entity.UID)
                        results.Add(o);
                });
                if (results.Count > 0)
                    return false;
                entities.Add(entity);
                return true;
            }
        }
        */

        // 注意，没有加锁
        internal static bool internalAdd(List<Entity> entities, Entity entity)
        {
            Debug.Assert(entity != null, "");
            Debug.Assert(string.IsNullOrEmpty(entity.UID) == false, "");

            List<Entity> results = new List<Entity>();
            entities.ForEach((o) =>
            {
                if (o.UID == entity.UID)
                    results.Add(o);
            });
            if (results.Count > 0)
                return false;
            entities.Add(entity);
            return true;
        }

        internal static void Remove(string name, Entity entity)
        {
            var list = new List<Entity>();
            list.Add(entity);
            l_Remove(name, list);
        }

        internal static void l_Remove(string name,
            IReadOnlyCollection<Entity> removes)
        {
            lock (_syncRoot_all)
            {
                List<Entity> entities = LinkByName(name);

                int count = 0;
                foreach (var entity in removes)
                {
                    Debug.Assert(entity != null, "");
                    Debug.Assert(string.IsNullOrEmpty(entity.UID) == false, "");

                    List<Entity> results = new List<Entity>();
                    entities.ForEach((o) =>
                    {
                        if (o.UID == entity.UID)
                            results.Add(o);
                    });
                    if (results.Count > 0)
                    {
                        foreach (var o in results)
                        {
                            entities.Remove(o);
                            count++;
                        }
                    }
                }
            }
        }

        /*
        internal static bool Remove(string name, Entity entity)
        {
            lock (_syncRoot_all)
            {
                List<Entity> entities = LinkByName(name);

                Debug.Assert(entity != null, "");
                Debug.Assert(string.IsNullOrEmpty(entity.UID) == false, "");

                List<Entity> results = new List<Entity>();
                entities.ForEach((o) =>
                {
                    if (o.UID == entity.UID)
                        results.Add(o);
                });
                if (results.Count > 0)
                {
                    foreach (var o in results)
                    {
                        entities.Remove(o);
                    }
                    return true;
                }
                return false;
            }
        }

        */

        /*
        internal static bool Remove(List<Entity> entities, Entity entity)
        {
            lock (_syncRoot_all)
            {
                Debug.Assert(entity != null, "");
                Debug.Assert(string.IsNullOrEmpty(entity.UID) == false, "");

                List<Entity> results = new List<Entity>();
                entities.ForEach((o) =>
                {
                    if (o.UID == entity.UID)
                        results.Add(o);
                });
                if (results.Count > 0)
                {
                    foreach (var o in results)
                    {
                        entities.Remove(o);
                    }
                    return true;
                }
                return false;
            }
        }
        */

        // Exception:
        //      可能会抛出异常 ArgumentException TagDataException
        static bool Add(List<Entity> entities, Entity entity)
        {
            List<Entity> results = new List<Entity>();
            entities.ForEach((o) =>
            {
                if (o.UID == entity.UID)
                    results.Add(o);
            });
            if (results.Count == 0)
            {
                // 注：所创建的 Entity 对象其 Error 成员可能有值，表示有出错信息
                // Exception:
                //      可能会抛出异常 ArgumentException
                entities.Add(entity);
                return true;
            }
            return false;
        }

        static void CheckPII(List<Entity> entities)
        {
            foreach (var entity in entities)
            {
                if (entity.PII == null)
                {
                    Debug.Assert(false, "PII 不应为 null");
                }
            }
        }

        // Exception:
        //      可能会抛出异常 ArgumentException TagDataException
        static bool Add(List<Entity> entities, TagAndData tag)
        {
            List<Entity> results = new List<Entity>();
            entities.ForEach((o) =>
            {
                if (o.UID == tag.OneTag.UID)
                    results.Add(o);
            });
            if (results.Count == 0)
            {
                // 注：所创建的 Entity 对象其 Error 成员可能有值，表示有出错信息
                // Exception:
                //      可能会抛出异常 ArgumentException
                entities.Add(NewEntity(tag, false));
                return true;
            }
            return false;
        }

        static bool Remove(List<Entity> entities, TagAndData tag)
        {
            List<Entity> results = new List<Entity>();
            entities.ForEach((o) =>
            {
                if (o.UID == tag.OneTag.UID)
                    results.Add(o);
            });
            if (results.Count > 0)
            {
                foreach (var o in results)
                {
                    entities.Remove(o);
                }
                return true;
            }
            return false;
        }

        static List<Entity> LinkByName(string name)
        {
            List<Entity> entities = null;
            switch (name)
            {
                case "all":
                    entities = _all;
                    break;
                case "adds":
                    entities = _adds;
                    break;
                case "removes":
                    entities = _removes;
                    break;
                case "changes":
                    entities = _changes;
                    break;
                default:
                    throw new ArgumentException($"无法识别的 name 参数值 '{name}'");
            }

            return entities;
        }

        // 2020/4/13
        // 更新 entity 里面的读者记录相关数据
        static bool l_UpdateEntityXml(string name,
            string uid,
            string entity_xml)
        {
            lock (_syncRoot_all)
            {
                List<Entity> entities = LinkByName(name);

                bool changed = false;
                foreach (var entity in entities)
                {
                    if (entity.UID == uid)
                    {
                        entity.SetData(entity.ItemRecPath,
                            entity_xml,
                            ShelfData.Now);
                    }
                }
                return changed;
            }
        }

        // 更新 Entity 信息
        static bool l_Update(string name, TagAndData tag)
        {
            lock (_syncRoot_all)
            {
                List<Entity> entities = LinkByName(name);

                bool changed = false;
                foreach (var entity in entities)
                {
                    if (entity.UID == tag.OneTag.UID)
                    {
                        if (entity.ReaderName != tag.OneTag.ReaderName)
                        {
                            entity.ReaderName = tag.OneTag.ReaderName;
                            changed = true;
                        }
                        if (entity.Antenna != tag.OneTag.AntennaID.ToString())
                        {
                            entity.Antenna = tag.OneTag.AntennaID.ToString();
                            changed = true;
                        }
                        // 2019/11/26
                        if (entity.TagInfo != null && tag.OneTag.TagInfo != null
                            && entity.TagInfo.EAS != tag.OneTag.TagInfo.EAS)
                        {
                            entity.TagInfo.EAS = tag.OneTag.TagInfo.EAS;
                            // changed = true;
                        }
                    }
                }
                return changed;
            }
        }


        // 注意：这是不加锁的版本
        static bool Update(List<Entity> entities, TagAndData tag)
        {
            bool changed = false;
            foreach (var entity in entities)
            {
                if (entity.UID == tag.OneTag.UID)
                {
                    if (entity.ReaderName != tag.OneTag.ReaderName)
                    {
                        entity.ReaderName = tag.OneTag.ReaderName;
                        changed = true;
                    }
                    if (entity.Antenna != tag.OneTag.AntennaID.ToString())
                    {
                        entity.Antenna = tag.OneTag.AntennaID.ToString();
                        changed = true;
                    }
                    // 2019/11/26
                    if (entity.TagInfo != null && tag.OneTag.TagInfo != null
                        && entity.TagInfo.EAS != tag.OneTag.TagInfo.EAS)
                    {
                        entity.TagInfo.EAS = tag.OneTag.TagInfo.EAS;
                        // changed = true;
                    }
                }
            }
            return changed;
        }

#if NEW_VERSION

        public class UpdateResult : NormalResult
        {
            // 变化前的 Entity 内容
            public List<Entity> OldEntities { get; set; }
            // 变化后的 Entity 内容
            public List<Entity> NewEntities { get; set; }
        }

        // 注意：这是不加锁的版本
        static UpdateResult new_Update(List<Entity> entities, TagAndData tag)
        {
            List<Entity> old_entities = new List<Entity>();
            List<Entity> new_entities = new List<Entity>();

            // bool changed = false;
            foreach (var entity in entities)
            {
                if (entity.UID == tag.OneTag.UID)
                {
                    if (entity.ReaderName != tag.OneTag.ReaderName
                        || entity.Antenna != tag.OneTag.AntennaID.ToString()
                        || (entity.TagInfo != null && tag.OneTag.TagInfo != null
                        && entity.TagInfo.EAS != tag.OneTag.TagInfo.EAS))
                    {
                        old_entities.Add(entity.Clone());

                        entity.ReaderName = tag.OneTag.ReaderName;
                        entity.Antenna = tag.OneTag.AntennaID.ToString();
                        if (entity.TagInfo != null && tag.OneTag.TagInfo != null)
                            entity.TagInfo.EAS = tag.OneTag.TagInfo.EAS;

                        new_entities.Add(entity);
                        // changed = true;
                    }
                }
            }

            // return changed;
            return new UpdateResult
            {
                OldEntities = old_entities,
                NewEntities = new_entities
            };
        }

#endif

        // 故意选择用到的天线编号加一的天线(用 ListTags() 实现)
        public static async Task<NormalResult> SelectAntennaAsync()
        {
            StringBuilder text = new StringBuilder();
            List<string> errors = new List<string>();
            List<AntennaList> table = ShelfData.GetAntennaTable();
            foreach (var list in table)
            {
                if (list.Antennas == null || list.Antennas.Count == 0)
                    continue;
                uint antenna = (uint)(list.Antennas[list.Antennas.Count - 1] + 1);
                // int first_antenna = list.Antennas[0];
                text.Append($"readerName[{list.ReaderName}], antenna[{antenna}]\r\n");
                using (var releaser = await _inventoryLimit.EnterAsync().ConfigureAwait(false))
                {
                    try
                    {
                        var result = RfidManager.CallListTags($"{list.ReaderName}:{antenna}", "");
                        if (result.Value == -1)
                            errors.Add($"CallListTags() 出错: {result.ErrorInfo}");
                    }
                    catch (Exception ex)
                    {
                        // 2020/4/17
                        errors.Add($"CallListTags() 出现异常: {ex.Message}");
                        WpfClientInfo.WriteErrorLog($"SelectAntennaAsync() 中 CallListTags() 出现异常: {ExceptionUtil.GetDebugText(ex)}");
                    }
                }
            }
            if (errors.Count > 0)
            {
                // this.SetGlobalError("InitialShelfEntities", $"SelectAntenna() 出错: {StringUtil.MakePathList(errors, ";")}");
                return new NormalResult
                {
                    Value = -1,
                    ErrorInfo = $"SelectAntenna() 出错: {StringUtil.MakePathList(errors, ";")}"
                };
            }
            return new NormalResult
            {
                Value = 0,
                ErrorInfo = text.ToString()
            };
        }

        static bool _tagAdded = false;

        static SpeakList _speakList = new SpeakList();

        public delegate void Delagate_booksChanged();

        // 新版本事件
        // 跟随事件动态更新列表
        // Add: 检查列表中是否存在这个 PII，如果存在，则修改状态为 在架，并设置 UID 成员
        //      如果不存在，则为列表添加一个新元素，修改状态为在架，并设置 UID 和 PII 成员
        // Remove: 检查列表中是否存在这个 PII，如果存在，则修改状态为 不在架
        //      如果不存在这个 PII，则不做任何动作
        // Update: 检查列表中是否存在这个 PII，如果存在，则修改状态为 在架，并设置 UID 成员
        //      如果不存在，则为列表添加一个新元素，修改状态为在架，并设置 UID 和 PII 成员
        public static async Task ChangeEntitiesAsync(BaseChannel<IRfid> channel,
            SeperateResult e,
            Delagate_booksChanged func_booksChanged)
        {
            if (ShelfData.FirstInitialized == false)
                return;

            // 开门状态下，动态信息暂时不要合并
            bool changed = false;

            List<TagAndData> tags = new List<TagAndData>();
            if (e.add_books != null)
            {
                tags.AddRange(e.add_books);
            }

            if (e.updated_books != null)
            {
                tags.AddRange(e.updated_books);
            }

            // 2020/4/17
            // 忽略其他读卡器上的标签
            {
                var filtered = tags.FindAll(tag =>
                {
                    if (tag.OneTag.Protocol == InventoryInfo.ISO15693
                        && tag.OneTag.TagInfo == null)
                        return false;   // 忽略还没有 TagInfo 的那些超前的通知

                    // 判断一下 tag 是否属于已经定义的门范围
                    var doors = DoorItem.FindDoors(ShelfData.Doors, tag.OneTag.ReaderName, tag.OneTag.AntennaID.ToString());
                    if (doors.Count > 0)
                        return true;
                    return false;
                });

                tags = filtered;
            }

            // 延时触发 SelectAntenna()
            if (tags.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // 延时设置
                        await Task.Delay(TimeSpan.FromSeconds(10), App.CancelToken);
                        _tagAdded = true;
                    }
                    catch
                    {

                    }
                });
            }

            List<string> add_uids = new List<string>();
            int removeBooksCount = 0;
            lock (_syncRoot_all)
            {
                // 新添加标签(或者更新标签信息)
                foreach (var tag in tags)
                {
                    // 没有 TagInfo 信息的先跳过
                    if (tag.OneTag.TagInfo == null)
                        continue;

                    add_uids.Add(tag.OneTag.UID);

                    // 看看 _all 里面有没有
                    var results = Find(_all, tag);
                    if (results.Count == 0)
                    {
                        // Exception:
                        //      可能会抛出异常 ArgumentException TagDataException
                        if (Add(_adds, tag) == true)
                        {
                            changed = true;

                            // 刚刚增加的 patron 的 UID，记忆下来
                            //if (tag.Type == "patron")
                            //    new_patron_uids.Add(tag.OneTag.UID);
                        }
                        if (Remove(_removes, tag) == true)
                            changed = true;
                    }
                    else
                    {
                        bool processed = false;
                        /*
                        // var old_entities = Find(_all, o => o.UID == tag.OneTag.UID);
                        // 找到以前的对象
                        if (results.Count > 0)
                        {
                            var old_entity = results[0];
                            var old_doors = DoorItem.FindDoors(ShelfData.Doors, old_entity.ReaderName, old_entity.Antenna);
                            var new_doors = DoorItem.FindDoors(ShelfData.Doors, tag.OneTag.ReaderName, tag.OneTag.AntennaID.ToString());

                            // 如果新旧对象所在的门发生了转移
                            if (old_doors.Count > 0 && new_doors.Count > 0
        && old_doors[0] != new_doors[0])
                            {
                                // 新门
                                ReplaceOrAdd(_adds, tag);

                                // 旧门
                                ReplaceOrAdd(_removes, old_entity);
                                changed = true;

                                processed = true;
                            }

                            // 更新 _all 里面的信息
                            if (Update(_all, tag) == true)
                            {
                                tag.Type = null;    // 令 NewEntity 重新解析标签
                                // Exception:
                                //      可能会抛出异常 ArgumentException TagDataException
                                Add(_changes, tag);
                            }
                        }
                        */

                        if (processed == false)
                        {
                            // 更新 _all 里面的信息
                            if (Update(_all, tag) == true)
                            {
                                tag.Type = null;    // 令 NewEntity 重新解析标签

                                // Exception:
                                //      可能会抛出异常 ArgumentException TagDataException
                                Add(_changes, tag);
                            }

                            // 要把 _adds 和 _removes 里面都去掉
                            if (Remove(_adds, tag) == true)
                                changed = true;
                            if (Remove(_removes, tag) == true)
                                changed = true;
                        }
                    }
                }

                List<TagAndData> removes = null;
                {
                    // 2020/4/9
                    // 把书柜读卡器上的(ISO15693)读者卡也计算在内
                    removes = e.removed_books?.FindAll(tag =>
                    {
                        /*
                        // 判断一下 tag 是否属于已经定义的门范围
                        var doors = DoorItem.FindDoors(ShelfData.Doors, tag.OneTag.ReaderName, tag.OneTag.AntennaID.ToString());
                        if (doors.Count > 0)
                            return true;
                        return false;
                        */
                        // 注：对 removed_books 里面的 tag 不再进行过滤。相信它们都是符合条件的。
                        // 特别注意，readerName 和 antenna 可能已经发生变化，不再符合柜门的范围定义。这种变化正是促使这些 tag 需要脱离柜门范围的一个原因
                        return true;
                    });
                }

                // 拿走标签
                foreach (var tag in removes)
                {
                    /*
                    if (tag.OneTag.TagInfo == null)
                        continue;
                    */

                    /*
                    // testing
                    tag.OneTag.TagInfo = null;
                    */

                    // 刚添加过的标签，这里就不要去移走了。即，添加比移除要优先
                    if (add_uids.IndexOf(tag.OneTag.UID) != -1)
                        continue;

                    // 2020/12/3
                    // 对于拿出书柜的标签，清掉其 RFID 标签缓存
                    BookTagList.ClearTagTable(tag.OneTag.UID);

                    // TODO: 特别注意，对于书柜门内的标签，要所属门完全一致才允许 remove

                    // 看看 _all 里面有没有
                    var results = l_Find("all", tag);
                    if (results.Count > 0)
                    {
                        if (Remove(_adds, tag) == true)
                            changed = true;
                        if (Remove(_changes, tag) == true)
                            changed = true;
                        /*
                        if (Add(_removes, tag) == true)
                        {
                            changed = true;
                        }
                        */
                        // 2020/4/5
                        // 这样可以利用 All 里面的 Entity 对象，通常其 Title 属性已经有值
                        if (Add("removes", results[0]) == true)
                            changed = true;
                    }
                    else
                    {
                        // _all 里面没有，很奇怪(是否写入错误日志？)。但，
                        // 要把 _adds 和 _removes 里面都去掉
                        if (Remove(_adds, tag) == true)
                            changed = true;
                        if (Remove(_removes, tag) == true)
                            changed = true;
                        if (Remove(_changes, tag) == true)
                            changed = true;
                    }

                    removeBooksCount++;
                }
            }

            // TODO: 把 add remove error 动作分散到每个门，然后再触发 ShelfData.BookChanged 事件

            if (changed == true)
            {
                // DoorItem.DisplayCount(_all, _adds, _removes, ShelfData.Doors);
                ShelfData.l_RefreshCount();
                func_booksChanged?.Invoke();
            }

            /*
            CheckPII(_all);
            CheckPII(_adds);
            CheckPII(_removes);
            CheckPII(_changes);
            */

            // TODO: 平时可以建立一个 cache，以后先从 cache 里面取书目摘要字符串
            _ = Task.Run(async () =>
            {
                try
                {
                    string style = "";  // "refreshCount";
                    CancellationToken token = CancelToken;
                    await FillBookFieldsAsync(l_All, token, style);
                    var result = await FillBookFieldsAsync(l_Adds, token, style);
                    /*
                    // 2020/7/22
                    if (result.Errors != null && result.Errors.Count > 0)
                    {
                        App.CurrentApp.SpeakSequence("警告:");
                        foreach (var error in result.Errors)
                        {
                            App.CurrentApp.SpeakSequence(error);
                        }
                    }
                    */
                    await FillBookFieldsAsync(l_Removes, token, style);
                    await FillBookFieldsAsync(l_Changes, token, style);
                }
                catch (Exception ex)
                {
                    WpfClientInfo.WriteErrorLog($"ChangeEntitiesAsync() 出现异常: {ExceptionUtil.GetDebugText(ex)}");
                }
            });
        }

        #region 分离图书和读者标签的算法

        /*
        static object _syncRoot_patronTags = new object();
        static List<TagAndData> _patronTags = null;

        public static List<TagAndData> PatronTags
        {
            get
            {
                lock (_syncRoot_patronTags)
                {
                    return new List<TagAndData>(_patronTags);
                }
            }
        }
        */

        static object _syncRoot_bookTags = new object();
        static List<TagAndData> _bookTags = null;

        public static List<TagAndData> BookTags
        {
            get
            {
                lock (_syncRoot_bookTags)
                {
                    return new List<TagAndData>(_bookTags);
                }
            }
        }

        // 用 UID 找到，并移走
        // parameters:
        //      uid 要匹配的 UID
        //      reader_name 要匹配的读卡器名。若为 "*"，表示任何读卡器名都匹配
        //      antenna 要匹配的天线编号。若为 uint.MaxValue，表示任何天线编号都匹配
        static List<TagAndData> Remove(List<TagAndData> list,
            string uid,
            string reader_name,
            uint antenna)
        {
            List<TagAndData> found = list.FindAll((tag) =>
            {
                // 2020/4/29
                // TODO: 在添加到集合的地方进行检查，确保 .OneTag 不为 null
                if (tag.OneTag == null)
                    return false;
                return (tag.OneTag.UID == uid
                && (reader_name == "*" || tag.OneTag.ReaderName == reader_name)
                && (antenna == uint.MaxValue || tag.OneTag.AntennaID == antenna));
            });
            foreach (var tag in found)
            {
                list.Remove(tag);
            }
            return found;
        }

        // 更新同 UID 的事项。如果没有找到，则在末尾添加
        // return:
        //      返回被替换掉的，以前的对象
        static List<TagAndData> Update(List<TagAndData> list, TagAndData tag)
        {
            List<TagAndData> found = list.FindAll((t) =>
            {
                return (t.OneTag.UID == tag.OneTag.UID);
            });
            foreach (var t in found)
            {
                list.Remove(t);
            }
            list.Add(tag);

            return found;
        }

        static bool Add(List<TagAndData> list, TagAndData tag)
        {
            List<TagAndData> found = list.FindAll((t) =>
            {
                return (t.OneTag.UID == tag.OneTag.UID);
            });
            if (found.Count > 0)
                return false;
            list.Add(tag);
            return true;
        }

        public class SeperateResult : NormalResult
        {
            public List<TagAndData> add_books { get; set; }
            public List<TagAndData> add_patrons { get; set; }
            public List<TagAndData> updated_books { get; set; }
            public List<TagAndData> updated_patrons { get; set; }
            public List<TagAndData> removed_books { get; set; }
            public List<TagAndData> removed_patrons { get; set; }
        }

        // 探测标签的类型。返回 "book" 或者 "patron" 或者 "other"。
        // 特殊地，.TagInfo 为 null 的 ISO15693 会暂时被当作 "book"
        public delegate string Delegate_detectType(OneTag tag);

        /*
        // 初始化 _patronTags 集合
        public static void InitialPatronTags(
            bool fill)
        {
            lock (_syncRoot_patronTags)
            {
                _patronTags = new List<TagAndData>();
                if (fill)
                    _patronTags.AddRange(PatronTagList.Tags);
            }

        }
        */

        // 初始化 _bookTags 集合
        public static void InitialBookTags(
            bool fill)
        {
            lock (_syncRoot_bookTags)
            {
                _bookTags = new List<TagAndData>();
                if (fill)
                    _bookTags.AddRange(BookTagList.Tags);
                /*
                if (func_detectType != null)
                {
                    BookTagList.Tags.ForEach((tag) =>
                    {
                        var type = func_detectType(tag.OneTag);
                        if (type == "patron")
                            _patronTags.Add(tag);
                        else if (type == "book")
                            _bookTags.Add(tag);
                    });
                }
                */
            }
        }
        // 更新 _bookTags 集合
        // 要返回新增加的两类标签的数目
        // TODO: 要能处理 ISO15693 图书标签放到读者读卡器上的动作。可以弹出一个窗口显示这一本图书的信息
        public static async Task<SeperateResult> SeperateBookTagsAsync(BaseChannel<IRfid> channel,
            NewTagChangedEventArgs e)
        {
            // 临时初始化一下
            if (_bookTags == null)
                InitialBookTags(false);

            lock (_syncRoot_bookTags)
            {
                List<TagAndData> add_books = new List<TagAndData>();
                //List<TagAndData> add_patrons = new List<TagAndData>();
                List<TagAndData> updated_books = new List<TagAndData>();
                //List<TagAndData> updated_patrons = new List<TagAndData>();
                List<TagAndData> removed_books = new List<TagAndData>();
                //List<TagAndData> removed_patrons = new List<TagAndData>();

                // ****
                // 处理需要添加的对象
                List<TagAndData> tags = new List<TagAndData>();
                if (e.AddTags != null && e.AddTags.Count > 0)
                {
                    // 分离新添加的标签
                    e.AddTags.ForEach((tag) =>
                    {
                        // 对于 .TagInfo == null 的 ISO15693 标签不敏感
                        if (tag.OneTag.TagInfo == null
                && tag.OneTag.Protocol == InventoryInfo.ISO15693)
                            return;

                        var ret = Add(_bookTags, tag);
                        if (ret == true)
                            add_books.Add(tag);
                    });
                }

                // *** 
                // 处理更新了的对象
                if (e.UpdateTags != null && e.UpdateTags.Count > 0)
                {
                    // 分离更新了的标签
                    e.UpdateTags.ForEach((tag) =>
                    {
                        // 对于 .TagInfo == null 的 ISO15693 标签不敏感
                        if (tag.OneTag.TagInfo == null
                && tag.OneTag.Protocol == InventoryInfo.ISO15693)
                            return;

                        var one_tag = tag.OneTag;
                        // TODO: 尝试从 _patronTags 里面移走
                        // removed_patrons.AddRange(Remove(_patronTags, one_tag.UID, one_tag.ReaderName, one_tag.AntennaID));

                        // 注：只匹配 UID 即可。readerName 和 antenna 可能已经变化，无法和已有的信息匹配
                        // removed_patrons.AddRange(Remove(_patronTags, one_tag.UID, "*", uint.MaxValue));
                        Update(_bookTags, tag);
                        updated_books.Add(tag);
                    });
                }

                // ***
                // 处理移走了的对象
                if (e.RemoveTags != null && e.RemoveTags.Count > 0)
                {
                    // 分离移走了的标签
                    e.RemoveTags.ForEach((tag) =>
                    {
                        var one_tag = tag.OneTag;
                        {
                            // 注意，只有当 UID 和 读卡器名字 和 天线编号都相同才予以删除
                            removed_books.AddRange(Remove(_bookTags, one_tag.UID, one_tag.ReaderName, one_tag.AntennaID));
                            // removed_patrons.AddRange(Remove(_patronTags, one_tag.UID, one_tag.ReaderName, one_tag.AntennaID));
                        }
                    });
                }

                // 2020/4/19
                foreach (var tag in updated_books)
                {
                    tag.Type = null;    // 迫使 NewEntity 重新解析标签
                }

                return new SeperateResult
                {
                    add_books = add_books,
                    //add_patrons = add_patrons,
                    updated_books = updated_books,
                    //updated_patrons = updated_patrons,
                    removed_books = removed_books,
                    //removed_patrons = removed_patrons,
                };
            }
        }

#if REMOVED
        // 更新 _patronTags 集合
        // 要返回新增加的两类标签的数目
        // TODO: 要能处理 ISO15693 图书标签放到读者读卡器上的动作。可以弹出一个窗口显示这一本图书的信息
        public static async Task<SeperateResult> SeperatePatronTagsAsync(BaseChannel<IRfid> channel,
            NewTagChangedEventArgs e)
        {
            // 临时初始化一下
            if (_patronTags == null)
                InitialPatronTags(true);

            lock (_syncRoot_patronTags)
            {
                //List<TagAndData> add_books = new List<TagAndData>();
                List<TagAndData> add_patrons = new List<TagAndData>();
                //List<TagAndData> updated_books = new List<TagAndData>();
                List<TagAndData> updated_patrons = new List<TagAndData>();
                //List<TagAndData> removed_books = new List<TagAndData>();
                List<TagAndData> removed_patrons = new List<TagAndData>();

                // ****
                // 处理需要添加的对象
                List<TagAndData> tags = new List<TagAndData>();
                if (e.AddTags != null && e.AddTags.Count > 0)
                {
                    // 分离新添加的标签
                    e.AddTags.ForEach((tag) =>
                    {
                        // 对于 .TagInfo == null 的 ISO15693 标签不敏感
                        if (tag.OneTag.TagInfo == null
                && tag.OneTag.Protocol == InventoryInfo.ISO15693)
                            return;

                        {
                            var ret = Add(_patronTags, tag);
                            if (ret == true)
                                add_patrons.Add(tag);
                        }
                    });
                }

                // *** 
                // 处理更新了的对象
                if (e.UpdateTags != null && e.UpdateTags.Count > 0)
                {
                    // 分离更新了的标签
                    e.UpdateTags.ForEach((tag) =>
                    {
                        // 对于 .TagInfo == null 的 ISO15693 标签不敏感
                        if (tag.OneTag.TagInfo == null
                && tag.OneTag.Protocol == InventoryInfo.ISO15693)
                            return;

                        {
                            var one_tag = tag.OneTag;
                            // TODO: 尝试从 _bookTags 里面移走
                            // removed_books.AddRange(Remove(_bookTags, one_tag.UID, one_tag.ReaderName, one_tag.AntennaID));

                            // 注：只匹配 UID 即可。readerName 和 antenna 可能已经变化，无法和已有的信息匹配
                            // removed_books.AddRange(Remove(_bookTags, one_tag.UID, "*", uint.MaxValue));
                            Update(_patronTags, tag);
                            updated_patrons.Add(tag);
                        }
                    });
                }

                // ***
                // 处理移走了的对象
                if (e.RemoveTags != null && e.RemoveTags.Count > 0)
                {
                    // 分离移走了的标签
                    e.RemoveTags.ForEach((tag) =>
                    {
                        var one_tag = tag.OneTag;
                        //var type = func_detectType(one_tag);
                        //if (type == "patron" || type == "book")
                        {
                            // 注意，只有当 UID 和 读卡器名字 和 天线编号都相同才予以删除
                            // removed_books.AddRange(Remove(_bookTags, one_tag.UID, one_tag.ReaderName, one_tag.AntennaID));
                            removed_patrons.AddRange(Remove(_patronTags, one_tag.UID, one_tag.ReaderName, one_tag.AntennaID));
                        }
                    });
                }

                foreach (var tag in updated_patrons)
                {
                    tag.Type = null;    // 迫使 NewEntity 重新解析标签
                }

                /*
                // testing
                _patronTags.Clear();
                _patronTags.AddRange(PatronTagList.Tags);
                */

                return new SeperateResult
                {
                    //add_books = add_books,
                    add_patrons = add_patrons,
                    //updated_books = updated_books,
                    updated_patrons = updated_patrons,
                    //removed_books = removed_books,
                    removed_patrons = removed_patrons,
                };
            }
        }
#endif

        #endregion

#if OLD_TAGCHANGED
        // 跟随事件动态更新列表
        // Add: 检查列表中是否存在这个 PII，如果存在，则修改状态为 在架，并设置 UID 成员
        //      如果不存在，则为列表添加一个新元素，修改状态为在架，并设置 UID 和 PII 成员
        // Remove: 检查列表中是否存在这个 PII，如果存在，则修改状态为 不在架
        //      如果不存在这个 PII，则不做任何动作
        // Update: 检查列表中是否存在这个 PII，如果存在，则修改状态为 在架，并设置 UID 成员
        //      如果不存在，则为列表添加一个新元素，修改状态为在架，并设置 UID 和 PII 成员
        public static async Task ChangeEntitiesAsync(BaseChannel<IRfid> channel,
            TagChangedEventArgs e,
            Delagate_booksChanged func_booksChanged)
        {
            if (ShelfData.FirstInitialized == false)
                return;

            // 开门状态下，动态信息暂时不要合并
            bool changed = false;

            List<TagAndData> tags = new List<TagAndData>();
            if (e.AddBooks != null)
            {
                tags.AddRange(e.AddBooks);
                // 延时触发 SelectAntenna()
                if (e.AddBooks.Count > 0)
                    _tagAdded = true;
            }

            if (e.UpdateBooks != null)
                tags.AddRange(e.UpdateBooks);

            // 2020/4/9
            // 把书柜读卡器上的(ISO15693)读者卡也计算在内
            {
                List<TagAndData> temp = new List<TagAndData>();
                if (e.AddPatrons != null)
                    temp.AddRange(e.AddPatrons);
                if (e.UpdatePatrons != null)    // 因为有两阶段通知的问题，所以 update 的也应该考虑在内
                    temp.AddRange(e.UpdatePatrons);
                var patrons = temp.FindAll(tag =>
                {
                    if (tag.OneTag.Protocol != InventoryInfo.ISO15693)
                        return false;
                    if (tag.OneTag.TagInfo == null)
                        return false;   // 忽略还没有 TagInfo 的那些超前的通知

                    // 判断一下 tag 是否属于已经定义的门范围
                    var doors = DoorItem.FindDoors(ShelfData.Doors, tag.OneTag.ReaderName, tag.OneTag.AntennaID.ToString());
                    if (doors.Count > 0)
                        return true;
                    return false;
                });
                /*
                foreach (var patron in patrons)
                {
                    var type = patron.Type;
                }
                */
                tags.AddRange(patrons);
            }

            // List<string> new_patron_uids = new List<string>();

            List<string> add_uids = new List<string>();
            int removeBooksCount = 0;
            lock (_syncRoot_all)
            {
                // 新添加标签(或者更新标签信息)
                foreach (var tag in tags)
                {
                    // 没有 TagInfo 信息的先跳过
                    if (tag.OneTag.TagInfo == null)
                        continue;

                    add_uids.Add(tag.OneTag.UID);

                    // 看看 _all 里面有没有
                    var results = Find(_all, tag);
                    if (results.Count == 0)
                    {
                        // Exception:
                        //      可能会抛出异常 ArgumentException TagDataException
                        if (Add(_adds, tag) == true)
                        {
                            changed = true;

                            // 刚刚增加的 patron 的 UID，记忆下来
                            //if (tag.Type == "patron")
                            //    new_patron_uids.Add(tag.OneTag.UID);
                        }
                        if (Remove(_removes, tag) == true)
                            changed = true;
                    }
                    else
                    {
                        // 更新 _all 里面的信息
                        if (Update(_all, tag) == true)
                        {
                            // Exception:
                            //      可能会抛出异常 ArgumentException TagDataException
                            Add(_changes, tag);
                        }

                        // 要把 _adds 和 _removes 里面都去掉
                        if (Remove(_adds, tag) == true)
                            changed = true;
                        if (Remove(_removes, tag) == true)
                            changed = true;
                    }
                }

                var removes = e.RemoveBooks;
                {
                    // 2020/4/9
                    // 把书柜读卡器上的(ISO15693)读者卡也计算在内
                    var remove_patrons = e.RemovePatrons?.FindAll(tag =>
                    {
                        if (tag.OneTag.Protocol != InventoryInfo.ISO15693)
                            return false;
                        // 判断一下 tag 是否属于已经定义的门范围
                        var doors = DoorItem.FindDoors(ShelfData.Doors, tag.OneTag.ReaderName, tag.OneTag.AntennaID.ToString());
                        if (doors.Count > 0)
                            return true;
                        return false;
                    });
                    if (remove_patrons != null)
                        removes.AddRange(remove_patrons);
                }

                // 拿走标签
                foreach (var tag in removes)
                {
                    if (tag.OneTag.TagInfo == null)
                        continue;

                    //if (tag.Type == "patron")
                    //    continue;

                    /*
                    // 2020/4/10
                    // 刚增加的 patron，这里就不要去移走了
                    if (new_patron_uids.IndexOf(tag.OneTag.UID) != -1)
                        continue;
                        */

                    // 2020/4/10
                    // 刚添加过的标签，这里就不要去移走了。即，添加比移除要优先
                    if (add_uids.IndexOf(tag.OneTag.UID) != -1)
                        continue;

                    // 看看 _all 里面有没有
                    var results = Find("all", tag);
                    if (results.Count > 0)
                    {
                        if (Remove(_adds, tag) == true)
                            changed = true;
                        if (Remove(_changes, tag) == true)
                            changed = true;
                        /*
                        if (Add(_removes, tag) == true)
                        {
                            changed = true;
                        }
                        */
                        // 2020/4/5
                        // 这样可以利用 All 里面的 Entity 对象，通常其 Title 属性已经有值
                        if (Add("removes", results[0]) == true)
                            changed = true;
                    }
                    else
                    {
                        // _all 里面没有，很奇怪(是否写入错误日志？)。但，
                        // 要把 _adds 和 _removes 里面都去掉
                        if (Remove(_adds, tag) == true)
                            changed = true;
                        if (Remove(_removes, tag) == true)
                            changed = true;
                        if (Remove(_changes, tag) == true)
                            changed = true;
                    }

                    removeBooksCount++;
                }

            }

            StringUtil.RemoveDup(ref add_uids, false);
            int add_count = add_uids.Count;
            int remove_count = 0;
            if (e.RemoveBooks != null)
                remove_count = removeBooksCount; // 注： e.RemoveBooks.Count 是不准确的，有时候会把 ISO15693 的读者卡判断时作为 remove 信号

#if REMOVED
            if (remove_count > 0)
            {
                // App.CurrentApp.SpeakSequence($"取出 {remove_count} 本");
                Sound(1, remove_count, "取出");
                /*
                _speakList.Speak("取出 {0} 本",
                    remove_count,
                    (s) =>
                    {
                        App.CurrentApp.SpeakSequence(s);
                    });
                    */
            }
            if (add_count > 0)
            {
                Sound(2, add_count, "放入");
                /*
                // App.CurrentApp.SpeakSequence($"放入 {add_count} 本");
                _speakList.Speak("放入 {0} 本",
    add_count,
    (s) =>
    {
        App.CurrentApp.SpeakSequence(s);
    });
    */
            }
#endif

            // TODO: 把 add remove error 动作分散到每个门，然后再触发 ShelfData.BookChanged 事件

            if (changed == true)
            {
                // DoorItem.DisplayCount(_all, _adds, _removes, ShelfData.Doors);
                ShelfData.RefreshCount();
                func_booksChanged?.Invoke();
            }

            // TODO: 平时可以建立一个 cache，以后先从 cache 里面取书目摘要字符串
            var task = Task.Run(async () =>
            {
                CancellationToken token = CancelToken;
                await FillBookFieldsAsync(All, token);
                await FillBookFieldsAsync(Adds, token);
                await FillBookFieldsAsync(Removes, token);
            });
        }

#endif

        static int[] tones = new int[] { 523, 659, 783 };
        /*
         *  C4: 261 330 392
            C5: 523 659 783
         * */
        public static void Sound(int tone, int count, string text)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < count; i++)
                        System.Console.Beep(tones[tone], 500);
                    if (string.IsNullOrEmpty(text) == false)
                        App.CurrentApp.SpeakSequence(text); // 不打断前面的说话
                }
                catch (Exception ex)
                {
                    WpfClientInfo.WriteErrorLog($"Sound() 出现异常: {ExceptionUtil.GetDebugText(ex)}");
                }
            });
        }

        public class FillBookFieldsResult : NormalResult
        {
            public List<string> Errors { get; set; }
        }

        /*
         * FillBookFieldsAsync() 遇到的报错类型有两种：1) RFID 标签解析出错；2) 在获取册记录信息的过程中，通讯出错，或者册记录没有找到
         * */
        // TODO: 刷新 data 以前，是否先把有关字段都设置为 ?，避免观看者误会
        // TODO: 获取册记录，优先从缓存中获取。注意借书、还书、转移等同步操作后，要及时更新或者废止缓存内容
        public static async Task<FillBookFieldsResult> FillBookFieldsAsync(// BaseChannel<IRfid> channel,
        IReadOnlyCollection<Entity> entities,
        CancellationToken token,
        string style/*,
    bool refreshCount = true*/)
        {
            // 是否重新获得册记录?
            bool refresh_data = StringUtil.IsInList("refreshData", style);
            // 是否刷新门上的数字
            bool refreshCount = StringUtil.IsInList("refreshCount", style);

            bool localGetEntityInfo = StringUtil.IsInList("localGetEntityInfo", style);

            // int error_count = 0;
            int request_error_count = 0;    // 请求因为通讯失败的次数
            List<string> errors = new List<string>();
            foreach (Entity entity in entities)
            {
#if AUTO_TEST
                Debug.Assert(string.IsNullOrEmpty(entity.PII) == false);
#endif
                if (token.IsCancellationRequested)
                    return new FillBookFieldsResult
                    {
                        Value = -1,
                        ErrorInfo = "中断",
                        ErrorCode = "cancelled"
                    };
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
                    LogicChip chip = null;
                    try
                    {
                        if (entity.TagInfo.Protocol == InventoryInfo.ISO15693)
                        {
                            // Exception:
                            //      可能会抛出异常 ArgumentException TagDataException
                            chip = LogicChip.From(entity.TagInfo.Bytes,
            (int)entity.TagInfo.BlockSize,
            "" // tag.TagInfo.LockStatus
            );
                        }
                        else if (entity.TagInfo.Protocol == InventoryInfo.ISO18000P6C)
                        {
                            // 2023/11/3
                            // 注1: taginfo.EAS 在调用后可能被修改
                            // 注2: 本函数不再抛出异常。会在 ErrorInfo 中报错
                            var chip_info = RfidTagList.GetUhfChipInfo(entity.TagInfo);
                            chip = chip_info.Chip;
                        }
                        else
                        {
                            // 无法识别的 RFID 标签协议
                            // TODO: 抛出异常?
                        }
                    }
                    catch (Exception ex)
                    {
                        WpfClientInfo.WriteErrorLog($"FillBookFieldsAsync() 出现异常: {ExceptionUtil.GetDebugText(ex)}");
                        errors.Add($"解析 RFID 标签(UID:{entity.TagInfo.UID})时出现异常 {ex.Message}");
                        continue;
                    }

                    string pii = chip?.FindElement(ElementOID.PII)?.Text;
                    if (string.IsNullOrEmpty(pii))
                    {
                        // 报错
                        App.CurrentApp.SpeakSequence($"警告：发现 PII 字段为空的标签");
                        entity.SetError($"PII 字段为空");
                        entity.FillFinished = true;
                        // error_count++;
                        errors.Add($"标签 PII 字段为空(UID={entity.TagInfo.UID})");
                        continue;
                    }

                    entity.PII = PageBorrow.GetCaption(pii);

                    // 2020/9/5
                    entity.OI = chip?.FindElement(ElementOID.OI)?.Text;
                    entity.AOI = chip?.FindElement(ElementOID.AOI)?.Text;
                }

                // 获得 Title
                // 注：如果 Title 为空，文字中要填入 "(空)"
                if ((string.IsNullOrEmpty(entity.Title) || refresh_data)
                    && string.IsNullOrEmpty(entity.PII) == false && entity.PII != "(空)")
                {
                    GetEntityDataResult result = null;
                    if (localGetEntityInfo)
                    {
                        // 只从本地数据库中获取
                        result = LocalGetEntityData(entity.GetOiPii(true));
                        if (string.IsNullOrEmpty(result.Title) == false)
                            entity.Title = PageBorrow.GetCaption(result.Title);
                        if (string.IsNullOrEmpty(result.ItemXml) == false)
                            entity.SetData(result.ItemRecPath,
                                result.ItemXml,
                                ShelfData.Now);
                    }
                    else
                    {
                        string uii = entity.GetOiPii(true);
                        result = await GetEntityDataAsync(uii,
                            ShelfData.LibraryNetworkCondition == "OK" ? "" : "offline");
                        if (result.Value == -1 || result.Value == 0)
                        {
                            // TODO: 条码号没有找到的错误码要单独记下来
                            // 报错
                            string error = $"警告：UII 为 {uii} 的标签出错: {result.ErrorInfo}";
                            if (result.ErrorCode == "NotFound")
                                error = $"警告：UII 为 '{uii}' 的图书没有找到记录";

                            // 2020/3/5
                            WpfClientInfo.WriteErrorLog($"GetEntityData() error: {error}");

                            // TODO: 如果发现当前一直是通讯中断的情况，要避免语音念太多报错
                            // App.CurrentApp.SpeakSequence(error);
                            entity.SetError(result.ErrorInfo);
                            // 2020/4/8
                            if (result.ErrorCode == "RequestError" || result.ErrorCode == "RequestTimeOut")
                            {
                                // 如果是通讯失败导致的出错，应该有办法进行重试获取
                                entity.FillFinished = false;
                                // 统计通讯失败次数
                                request_error_count++;
                            }
                            else
                                entity.FillFinished = true;
                            // error_count++;
                            errors.Add(error);
                            continue;
                        }
                        entity.Title = PageBorrow.GetCaption(result.Title);
                        entity.SetData(result.ItemRecPath,
                            result.ItemXml,
                            ShelfData.Now);
                    }

#if NO
                    // 验证 OI 和 AOI
                    // return:
                    //      true    找到。信息在 isil 和 alternative 参数里面返回
                    //      false   没有找到
                    var ret = ShelfData.GetOwnerInstitution(
                        entity.Location,
                        out string isil,
                        out string alternative);
                    if (ret == false)
                    {
                        string error = $"册 '{entity.PII}' 馆藏地 '{entity.Location}' 没有找到相关的 OI 定义";
                        errors.Add(error);
                        entity.AppendError(error, "red", "oiError");
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(isil) == false)
                        {
                            if (isil != entity.OI)
                            {
                                string error = $"册 '{entity.PII}' 的理论 OI '{isil}' 和 RFID 标签中的 OI '{entity.OI}' 不符";
                                errors.Add(error);
                                entity.AppendError(error, "red", "oiError");
                            }
                        }
                        else if (string.IsNullOrEmpty(isil) == false)
                        {
                            if (alternative != entity.AOI)
                            {
                                string error = $"册 '{entity.PII}' 的理论 AOI '{alternative}' 和 RFID 标签中的 OI '{entity.AOI}' 不符";
                                errors.Add(error);
                                entity.AppendError(error, "red", "oiError");
                            }
                        }
                    }
#endif
                }

                // entity.SetError(null);
                entity.FillFinished = true;

                if (request_error_count >= 2)
                {
                    /*
                    if (App.TrySwitchToLocalMode() == true)
                        localGetEntityInfo = true;
                    */
                    if (localGetEntityInfo == false)
                        return new FillBookFieldsResult
                        {
                            Value = -1,
                            ErrorInfo = "请求 dp2library 时通讯失败",
                            ErrorCode = "requestError"
                        };
                }
            }

            if (token.IsCancellationRequested)
                return new FillBookFieldsResult
                {
                    Value = -1,
                    ErrorInfo = "中断",
                    ErrorCode = "cancelled"
                };

            if (refreshCount)
                ShelfData.l_RefreshCount();

            return new FillBookFieldsResult { Errors = errors };
            /*
            }
            catch (Exception ex)
            {
                //LibraryChannelManager.Log?.Error($"FillBookFields() 发生异常: {ExceptionUtil.GetExceptionText(ex)}");   // 2019/9/19
                //SetGlobalError("current", $"FillBookFields() 发生异常(已写入错误日志): {ex.Message}"); // 2019/9/11 增加 FillBookFields() exception:
            }
            */
        }

        static string GetRandomString()
        {
            Random rnd = new Random();
            return rnd.Next(1, 999999).ToString();
        }

        // 限制获取摘要时候可以并发使用的 LibraryChannel 通道数
        // static Semaphore _limit = new Semaphore(1, 1);

        // public delegate void Delegate_showDialog();

        // -1 -1 n only change progress value
        // -1 -1 -1 hide progress bar
        public delegate void Delegate_setProgress(double min, double max, double value, string text);

        // TODO: 结果似乎可以考虑直接设置 ActionInfo 的 State 成员？这样返回后直接写入数据库即可
        // TODO: 无法进行重试的错误，应该尝试在本地 SQLite 数据库中建立借还信息，以便日后追查
        // 提交请求到 dp2library 服务器
        // parameters:
        //      actions 要处理的 Action 集合。每个 Action 对象处理完以后，会自动从 _actions 中移除
        //      style   "auto_stop" 遇到报错就停止处理后面部分
        // result.Value
        //      -1  出错(要用对话框显示结果)
        //      0   没有必要处理
        //      1   已经完成处理(要用对话框显示结果)
        public static async Task<SubmitResult> SubmitCheckInOutAsync(
            Delegate_setProgress func_setProgress,
            IReadOnlyCollection<ActionInfo> actions,
            string style)
        {
            // TODO: 如果当前没有读者身份，则当作初始化处理，将书柜内的全部图书做还书尝试；被拿走的图书记入本地日志(所谓无主操作)
            // TODO: 注意还书，也就是往书柜里面放入图书，是不需要具体读者身份就可以提交的

            // TODO: 属于 free 类型的门里面的图书不要参与处理

            // ProgressWindow progress = null;
            //string patron_name = "";
            //patron_name = _patron.PatronName;

            // 先尽量执行还书请求，再报错说无法进行借书操作(记入错误日志)
            MessageDocument doc = new MessageDocument();

            /*
            // 限制同时能进入临界区的线程个数
            // TODO: 如果另一个并发的 submit 过程时间较长，导致这里超时了，应该需要自动重试
            // true if the current instance receives a signal; otherwise, false.
            if (_limit.WaitOne(TimeSpan.FromSeconds(10)) == false)
                return new SubmitResult
                {
                    Value = -1,
                    ErrorInfo = "获得资源过程中超时",
                    ErrorCode = "limitTimeout",
                    RetryActions = new List<ActionInfo>(actions),
                };
                */

            try
            {
                // ClearEntitiesError();

                /*
                if (progress != null)
                {
                    Application.Current.Dispatcher.Invoke(new Action(() =>
                    {
                        progress.ProgressBar.Value = 0;
                        progress.ProgressBar.Minimum = 0;
                        progress.ProgressBar.Maximum = actions.Count;
                    }));
                }
                */
                int index = 0;
                func_setProgress?.Invoke(0, actions.Count, index, "正在处理，请稍候 ...");

                // TODO: 准备工作：把涉及到的 Entity 对象的字段填充完整
                // 检查 PII 是否都具备了

                // xml 发生改变了的那些实体记录
                List<Entity> updates = new List<Entity>();

                List<ActionInfo> processed = new List<ActionInfo>();

                // 出错了的，需要重做请求 dp2library 的那些 Action
                // List<ActionInfo> retry_actions = new List<ActionInfo>();

                // 出错了的，但无法进行重试的那些 Action
                List<ActionInfo> error_actions = new List<ActionInfo>();

                foreach (ActionInfo info in actions)
                {
                    // testing 
                    // Thread.Sleep(1000);

                    string action = info.Action;
                    Entity entity = info.Entity;

                    // 2020/4/27
                    info.SyncOperTime = /*DateTime*/ShelfData.Now;

                    // 2020/4/8
                    // 如果 PII 为空
                    if (string.IsNullOrEmpty(entity.PII))
                    {
                        info.State = "dontsync";
                        info.SyncErrorCode = "PiiEmpty";
                        info.SyncErrorInfo = $"UID 为 {entity.UID} 的标签 PII 为空，不再进行同步(标签原出错信息 '{entity.Error}')";
                        processed.Add(info);
                        error_actions.Add(info);
                        continue;
                    }

                    // 2020/7/14
                    // 如果误放入了读者卡
                    if (StringUtil.IsInList("patronCard", entity.ErrorCode) == true)
                    {
                        info.State = "dontsync";
                        info.SyncErrorCode = "PatronCard";
                        info.SyncErrorInfo = $"UID 为 {entity.UID} 的标签是误放入书柜的读者卡，不再进行同步(标签原出错信息 '{entity.Error}')";
                        processed.Add(info);
                        error_actions.Add(info);
                        continue;
                    }

                    // 2020/7/15
                    // 如果 OI 不匹配
                    if (StringUtil.IsInList("oiError", entity.ErrorCode) == true)
                    {
                        info.State = "dontsync";
                        info.SyncErrorCode = "OiError";
                        info.SyncErrorInfo = $"UID 为 {entity.UID} 的标签 OI 不匹配，不再进行同步(标签原出错信息 '{entity.Error}')";
                        processed.Add(info);
                        error_actions.Add(info);
                        continue;
                    }

                    if (info.Action.StartsWith("transfer")
                        && info.TransferDirection == "out"
                        && string.IsNullOrEmpty(info.Location) == true
                        && string.IsNullOrEmpty(info.CurrentShelfNo) == true)
                    {
                        info.State = "dontsync";
                        info.SyncErrorCode = "NotSupport";  // 目前暂不支持同步此请求
                        info.SyncErrorInfo = $"(无目标式)下架请求暂不支持同步到 dp2library";
                        processed.Add(info);
                        error_actions.Add(info);
                        continue;
                    }

                    // 借书操作必须要有读者身份的请求者
                    if (action == "borrow")
                    {
                        if (string.IsNullOrEmpty(info.Operator.PatronBarcode)
                            || info.Operator.IsWorker == true)
                        {
                            MessageItem error = new MessageItem
                            {
                                SyncCount = info.SyncCount,
                                Operator = info.Operator,
                                OperTime = info.OperTime,
                                Operation = action,
                                ResultType = "error",
                                ErrorCode = "InvalidOperator",
                                ErrorInfo = "缺乏请求者",
                                Entity = entity,
                            };
                            doc.Add(error);
                            // 写入错误日志
                            WpfClientInfo.WriteInfoLog($"册 '{GetPiiString(entity)}' 因缺乏请求者无法进行借书请求");
                            continue;
                        }
                    }

#if REMOVED
                    // 2019/11/25
                    // 还书操作前先尝试修改 EAS
                    if (action == "return")
                    {
                        var result = SetEAS(entity.UID, entity.Antenna, false);
                        if (result.Value == -1)
                        {
                            string text = $"修改 EAS 动作失败: {result.ErrorInfo}";
#if REMOVED
                            entity.SetError(text, "yellow");
#endif

                            MessageItem error = new MessageItem
                            {
                                SyncCount = info.SyncCount,
                                Operator = info.Operator,
                                Operation = "changeEAS",
                                ResultType = "error",
                                ErrorCode = "ChangeEasFail",
                                ErrorInfo = text,
                                Entity = entity,
                            };
                            doc.Add(error);

                            // 写入错误日志
                            WpfClientInfo.WriteInfoLog($"修改册 '{entity.PII}' 的 EAS 失败: {result.ErrorInfo}");
                        }
                    }
#endif

                    // 实际操作时间
                    string operTimeStyle = "";
                    if (info.OperTime > DateTime.MinValue)
                        operTimeStyle = $",operTime:{StringUtil.EscapeString(DateTimeUtil.Rfc1123DateTimeStringEx(info.OperTime), ",:")}";

                    long lRet = 0;
                    ErrorCode error_code = ErrorCode.NoError;

                    string strError = "";
                    string[] item_records = null;
                    string[] biblio_records = null;
                    BorrowInfo borrow_info = null;
                    ReturnInfo return_info = null;

                    string strUserName = info.Operator?.GetWorkerAccountName();

                    /*
                    // testing
                    if (info.Action == "transfer")
                        strUserName = "supervisor1";
                    */

                    // 包含 OI 的 PII
                    string pii = entity.GetOiPii(true);
                    /*
                    string pii = "." + entity.PII;
                    if (string.IsNullOrEmpty(entity.OI) == false)
                        pii = entity.OI + "." + entity.PII;
                    else if (string.IsNullOrEmpty(entity.AOI) == false)
                        pii = entity.AOI + "." + entity.PII;
                    */

                    int nRedoCount = 0;
                REDO:
                    entity.Waiting = true;
                    //WpfClientInfo.WriteInfoLog($"SubmitCheckInOutAysnc() 中 strUserName='{strUserName}'");
                    LibraryChannel channel = App.CurrentApp.GetChannel(strUserName);
                    TimeSpan old_timeout = channel.Timeout;
                    channel.Timeout = TimeSpan.FromSeconds(10);
                    try
                    {
                        //WpfClientInfo.WriteInfoLog($"SubmitCheckInOutAysnc() 中 GetChannel(strUserName) 得到的 channel.UserName='{channel.UserName}'");

                        string strStyle = "item";   //  "item,reader";
                        if (entity.Title == null)
                            strStyle += ",biblio";

                        if (action == "borrow" || action == "renew")
                        {
                            if (string.IsNullOrEmpty(info.ActionString) == false)
                            {
                                var old_borrow_info = JsonConvert.DeserializeObject<BorrowInfo>(info.ActionString);
                                if (old_borrow_info.Overflows != null && old_borrow_info.Overflows.Length > 0)
                                {
                                    string value = StringUtil.EscapeString(string.Join("; ", old_borrow_info.Overflows), ":,");
                                    strStyle += $",overflow:{value}";
                                }
                                else if (string.IsNullOrEmpty(old_borrow_info.Period) == false)
                                {
                                    string value = StringUtil.EscapeString(old_borrow_info.Period, ":,");
                                    strStyle += $",requestPeriod:{value}";
                                }
                            }
                            int nRedoBorrowCount = 0;
                        REDO_BORROW:
                            WpfClientInfo.WriteInfoLog($"submit API Borrow() patron={info.Operator.PatronBarcode} book={pii}");
                            lRet = channel.Borrow(null,
                                action == "renew",
                                info.Operator.PatronBarcode,
                                pii,    // entity.PII,
                                entity.ItemRecPath,
                                false,
                                null,
                                strStyle + ",overflowable" + operTimeStyle, // style,
                                "xml", // item_format_list
                                out item_records,
                                "xml",
                                out string[] reader_records,
                                "summary",
                                out biblio_records,
                                out string[] dup_path,
                                out string output_reader_barcode,
                                out borrow_info,
                                out strError);
                            // 2021/7/1
                            if (lRet == -1
                                && (channel.ErrorCode == ErrorCode.AlreadyBorrowed || channel.ErrorCode == ErrorCode.AlreadyBorrowedByOther))
                            {
                                WpfClientInfo.WriteInfoLog($"submit API (after AlreadyBorrow) Return() book={pii}");
                                // 智能书柜要求强制借书。如果册操作前处在被其他读者借阅状态，要自动先还书再进行借书
                                long temp = channel.Return(null,
    "return",
    "",
    pii,
    entity.ItemRecPath,
    false,
    strStyle + operTimeStyle,
    "xml", // item_format_list
    out item_records,
    "xml",
    out reader_records,
    "summary",
    out biblio_records,
    out dup_path,
    out output_reader_barcode,
    out return_info,
    out string return_error);
                                if (temp == -1)
                                {
                                    lRet = -1;
                                    strError = $"提交借书动作时遇到出错: {strError}，然后补做还书时又遇到出错: {return_error}";
                                }
                                else if (nRedoBorrowCount < 10)
                                {
                                    WpfClientInfo.WriteInfoLog($"为读者 {info.Operator.PatronBarcode} 同步提交借书 (册 {pii}) 动作时遇到出错: {strError}，然后补做还书成功。后面自动将自动重试提交借书动作");
                                    nRedoBorrowCount++;
                                    goto REDO_BORROW;
                                }
                            }
                        }
                        else if (action == "return")
                        {
                            /*
                            // TODO: 增加检查 EAS 现有状态功能，如果已经是 true 则不用修改，后面 API 遇到出错后也不要回滚 EAS
                            // return 操作，提前修改 EAS
                            // 注: 提前修改 EAS 的好处是比较安全。相比 API 执行完以后再修改 EAS，提前修改 EAS 成功后，无论后面发生什么，读者都无法拿着这本书走出门禁
                            {
                                var result = SetEAS(entity.UID, entity.Antenna, action == "return");
                                if (result.Value == -1)
                                {
                                    entity.SetError($"{action_name}时修改 EAS 动作失败: {result.ErrorInfo}", "red");
                                    errors.Add($"册 '{entity.PII}' {action_name}时修改 EAS 动作失败: {result.ErrorInfo}");
                                    continue;
                                }
                            }
                            */
                            // 智能书柜不使用 EAS 状态。可以考虑统一修改为 EAS Off 状态？

                            WpfClientInfo.WriteInfoLog($"submit API Return() book={pii}");
                            lRet = channel.Return(null,
                                "return",
                                "", // _patron.Barcode,
                                pii,    // entity.PII,
                                entity.ItemRecPath,
                                false,
                                strStyle + operTimeStyle, // style,
                                "xml", // item_format_list
                                out item_records,
                                "xml",
                                out string[] reader_records,
                                "summary",
                                out biblio_records,
                                out string[] dup_path,
                                out string output_reader_barcode,
                                out return_info,
                                out strError);
                        }
                        else if (action.StartsWith("transfer"))
                        {
                            // currentLocation 元素内容。格式为 馆藏地:架号
                            // 注意馆藏地和架号字符串里面不应包含逗号和冒号
                            List<string> commands = new List<string>();
                            if (string.IsNullOrEmpty(info.CurrentShelfNo) == false)
                                commands.Add($"currentLocation:{StringUtil.EscapeString(info.CurrentShelfNo, ":,")}");
                            if (string.IsNullOrEmpty(info.Location) == false)
                                commands.Add($"location:{StringUtil.EscapeString(info.Location, ":,")}");
                            if (string.IsNullOrEmpty(info.BatchNo) == false)
                            {
                                commands.Add($"batchNo:{StringUtil.EscapeString(info.BatchNo, ":,")}");
                                // 2020/10/14
                                // 即便册记录没有发生修改，也要产生 transfer 操作日志记录。这样便于进行典藏移交清单统计打印
                                commands.Add("forceLog");
                            }

                            WpfClientInfo.WriteInfoLog($"submit API (transfer) Return() book={pii}");
                            // string currentLocation = GetRandomString(); // testing
                            // TODO: 如果先前 entity.Title 已经有了内容，就不要在本次 Return() API 中要求返 biblio summary
                            lRet = channel.Return(null,
                                "transfer",
                                "", // _patron.Barcode,
                                pii,    // entity.PII,
                                entity.ItemRecPath,
                                false,
                                $"{strStyle},{StringUtil.MakePathList(commands, ",")}" + operTimeStyle, // style,
                                "xml", // item_format_list
                                out item_records,
                                "xml",
                                out string[] reader_records,
                                "summary",
                                out biblio_records,
                                out string[] dup_path,
                                out string output_reader_barcode,
                                out return_info,
                                out strError);
                        }

                        error_code = channel.ErrorCode; // 保存下来，避免被 ReturnChannel 以后破坏
                    }
                    finally
                    {
                        //WpfClientInfo.WriteInfoLog($"SubmitCheckInOutAysnc() 中 ReturnChannel 前一刻的 channel.UserName='{channel.UserName}'");

                        // 2021/5/24
                        // 如果经过使用以后，UserName 和 GetChannel() 时不一样了，则立即清理闲置通道，避免发生通道溢出
                        bool need_clean = false;
                        if (channel.UserName != strUserName)
                            need_clean = true;

                        channel.Timeout = old_timeout;
                        App.CurrentApp.ReturnChannel(channel);
                        entity.Waiting = false;

                        //WpfClientInfo.WriteInfoLog($"SubmitCheckInOutAysnc() 中 ReturnChannel 后一刻的 channel.UserName='{channel.UserName}'，App._channelPool.Count={App._channelPool.Count}");

                        if (need_clean)
                            App._channelPool.CleanChannel();
                    }

                    // 2020/3/7
                    if ((error_code == ErrorCode.RequestError
        || error_code == ErrorCode.RequestTimeOut))
                    {
                        nRedoCount++;

                        if (nRedoCount < 2)
                            goto REDO;
                        else
                        {
                            if (StringUtil.IsInList("network_sensitive", style))
                                return new SubmitResult
                                {
                                    Value = -1,
                                    ErrorInfo = "因网络出现问题，请求 dp2library 服务器失败",
                                    ErrorCode = "requestError"
                                };
                        }
                    }

                    processed.Add(info);

                    if (lRet != -1)
                    {
                        if (info.Action == "borrow")
                        {
                            if (borrow_info == null)
                                info.ActionString = null;
                            else
                                info.ActionString = JsonConvert.SerializeObject(borrow_info);
                        }
                        else
                        {
                            if (return_info == null)
                                info.ActionString = null;
                            else
                                info.ActionString = JsonConvert.SerializeObject(return_info);
                        }
                    }

                    /*
                    // testing
                    lRet = -1;
                    strError = "testing";
                    channel.ErrorCode = ErrorCode.AccessDenied;
                    */

                    func_setProgress?.Invoke(-1, -1, ++index, null);

                    if (entity.Title == null
                        && biblio_records != null
                        && biblio_records.Length > 0
                        && string.IsNullOrEmpty(biblio_records[0]) == false)
                        entity.Title = biblio_records[0];

                    string title = GetPiiString(entity);
                    if (string.IsNullOrEmpty(entity.Title) == false)
                        title += " (" + entity.Title + ")";

#if REMOVED
                    // TODO: 其实 SaveActions 里面已经处理了 all adds removes changed 数组，这里似乎不需要再处理了
                    if (action == "borrow" || action == "return")
                    {
                        // 把 _adds 和 _removes 归入 _all
                        // 一边处理一边动态修改 _all?
                        if (action == "return")
                            ShelfData.Add("all", entity);
                        else
                            ShelfData.Remove("all", entity);

                        ShelfData.Remove("adds", entity);
                        ShelfData.Remove("removes", entity);
                    }

                    if (action == "transfer")
                        ShelfData.Remove("changes", entity);
#endif

                    string resultType = "succeed";
                    if (lRet == -1)
                        resultType = "error";
                    else if (lRet == 1)
                        resultType = "information";
                    string direction = "";
                    if (string.IsNullOrEmpty(info.Location) == false)
                        direction = $"家({info.Location})";
                    if (string.IsNullOrEmpty(info.CurrentShelfNo) == false)
                        direction += $" 当前位置({info.CurrentShelfNo})";
                    MessageItem messageItem = new MessageItem
                    {
                        SyncCount = info.SyncCount,
                        Operator = info.Operator,
                        OperTime = info.OperTime,
                        Operation = action,
                        ResultType = resultType,
                        ErrorCode = error_code.ToString(),
                        ErrorInfo = strError,
                        Entity = entity,
                        Direction = $"-->{direction}",
                    };
                    doc.Add(messageItem);

                    {
                        info.SyncErrorInfo = strError;
                        if (error_code != ErrorCode.NoError)
                            info.SyncErrorInfo += $"[{error_code}]";
                        info.SyncErrorCode = error_code.ToString();
                        info.SyncCount++;
                    }

                    // 微调
                    if (lRet == 0 && action == "return")
                        messageItem.ErrorInfo = "";

                    // sync/commerror/normalerror/空
                    // 同步成功/通讯出错/一般出错/从未同步过
                    info.State = "sync";

                    if (lRet == -1)
                    {
                        /*
                        // return 操作如果 API 失败，则要改回原来的 EAS 状态
                        if (action == "return")
                        {
                            var result = SetEAS(entity.UID, entity.Antenna, false);
                            if (result.Value == -1)
                                strError += $"\r\n并且复原 EAS 状态的动作也失败了: {result.ErrorInfo}";
                        }
                        */

                        if (action == "borrow")
                        {
                            // TODO: ErrorCode.AlreadyBorrowedByOther 应该补一个还书动作然后重试借书?
                            if (error_code == ErrorCode.AlreadyBorrowed)
                            {
                                messageItem.ResultType = "information";
                                WpfClientInfo.WriteInfoLog($"读者 {info.Operator.PatronName} {info.Operator.PatronBarcode} 尝试借阅册 '{title}' 时: {strError}");
#if REMOVED
                                entity.SetError(null);
#endif
                                continue;
                            }
                        }

                        if (action == "return")
                        {
                            if (error_code == ErrorCode.NotBorrowed)
                            {
                                messageItem.ResultType = "information";
                                WpfClientInfo.WriteInfoLog($"读者 {info.Operator.PatronName} {info.Operator.PatronBarcode} 尝试还回册 '{title}' 时: {strError}");
                                // TODO: 这里也要修改 EAS
#if REMOVED
                                entity.SetError(null);
#endif
                                continue;
                            }

                            // 2020/4/29
                            if (error_code == ErrorCode.SyncDenied)
                            {
                                messageItem.ResultType = "information";
                            }
                        }

                        if (action.StartsWith("transfer"))
                        {
                            if (error_code == ErrorCode.NotChanged)
                            {
                                // 不出现在结果中
                                // doc.Remove(messageItem);

                                // 改为警告
                                messageItem.ResultType = "information";
                                // messageItem.ErrorCode = channel.ErrorCode.ToString();
                                // 界面警告
                                //warnings.Add($"册 '{title}' (尝试转移时发现没有发生修改): {strError}");
                                // 写入错误日志
                                WpfClientInfo.WriteInfoLog($"转移册 '{title}' 时: {strError}");
#if REMOVED
                                entity.SetError(null);
#endif
                                continue;
                            }
                        }

                        error_actions.Add(info);

                        // 如果是通讯出错，要加入 retry_actions
                        if (error_code == ErrorCode.RequestError
                            || error_code == ErrorCode.RequestTimeOut
                            || error_code == ErrorCode.RequestCanceled
                            )
                        {
                            // retry_actions.Add(info);
                            info.State = "commerror";
                        }
                        else
                        {
                            if (error_code == ErrorCode.ItemBarcodeNotFound
                                || error_code == ErrorCode.SyncDenied)  // 2020/4/24
                                info.State = "dontsync";    // 注: borrow 类型的此种 dontsync 可以理解为读者在其他地方已经还书了。在断网情况下此种动作不要计入未还书列表
                            else
                                info.State = "normalerror";

                            // 2020/7/16
                            // 清除本地册记录缓存
                            if (error_code == ErrorCode.ItemBarcodeNotFound)
                            {
                                // result.Value
                                //      0   没有找到记录。没有发生更新
                                //      1   成功更新
                                var result = await LibraryChannelUtil.UpdateEntityXmlAsync(pii,
                                    null,
                                    null);
                            }

                        }

                        if (StringUtil.IsInList("auto_stop", style))
                            break;

                        WpfClientInfo.WriteErrorLog($"请求失败。action:{action},PII:{entity.PII}, 错误信息:{strError}, 错误码:{error_code.ToString()}");

#if REMOVED
                        entity.SetError($"{action_name}操作失败: {strError}", "red");
#endif
                        continue;
                    }

                    if (action == "borrow")
                    {
                        if (borrow_info.Overflows != null && borrow_info.Overflows.Length > 0)
                        {
                            // 界面警告
                            // TODO: 可以考虑归入 overflows 单独语音警告处理。语音要简洁。详细原因可出现在文字警告中
                            // warnings.Add($"册 '{title}' (借书操作发生溢出，请于当日内还书): {string.Join("; ", borrow_info.Overflows)}");

                            // TODO: 详细原因文字可否用稍弱的字体效果来显示？
                            messageItem.ErrorInfo = $"借书操作超越许可，请将本册放回书柜。详细原因： {string.Join("; ", borrow_info.Overflows)}";
                            messageItem.ResultType = "warning";
                            messageItem.ErrorCode = "overflow";
                            // 写入错误日志
                            WpfClientInfo.WriteInfoLog($"读者 {info.Operator.PatronName} {info.Operator.PatronBarcode} 借阅 '{title}' 时发生超越许可: {strError}");
#if REMOVED
                            entity.SetError(null);
#endif
                            info.SyncErrorCode = "overflow";
                            {
                                if (string.IsNullOrEmpty(info.SyncErrorInfo) == false)
                                    info.SyncErrorInfo += "; ";
                                info.SyncErrorInfo += $"借书超额，请将本册放回书柜。详细原因： {string.Join("; ", borrow_info.Overflows)}";
                            }
                        }
                    }

                    //if (action == "borrow")
                    //    borrows.Add(title);
                    //if (action == "return")
                    //    returns.Add(title);

                    /*
                    // borrow 操作，API 之后才修改 EAS
                    // 注: 如果 API 成功但修改 EAS 动作失败(可能由于读者从读卡器上过早拿走图书导致)，读者会无法把本册图书拿出门禁。遇到此种情况，读者回来补充修改 EAS 一次即可
                    if (action == "borrow")
                    {
                        var result = SetEAS(entity.UID, entity.Antenna, action == "return");
                        if (result.Value == -1)
                        {
                            entity.SetError($"虽然{action_name}操作成功，但修改 EAS 动作失败: {result.ErrorInfo}", "yellow");
                            errors.Add($"册 '{entity.PII}' {action_name}操作成功，但修改 EAS 动作失败: {result.ErrorInfo}");
                        }
                    }
                    */

                    // 刷新显示
                    {
                        if (item_records?.Length > 0)
                        {
                            // TODO: 这里更新 entity 后，那些克隆的 entity 何时更新呢？可否现在存入缓存备用?
                            string entity_xml = item_records[0];
                            entity.SetData(entity.ItemRecPath,
                                entity_xml,
                                ShelfData.Now);
                            // 2020/4/13
                            l_UpdateEntityXml("all", entity.UID, entity_xml);

                            // 2020/4/26
                            // result.Value
                            //      0   没有找到记录。没有发生更新
                            //      1   成功更新
                            var result = await LibraryChannelUtil.UpdateEntityXmlAsync(pii,
                                entity_xml,
                                null);

                            updates.Add(entity);
                        }

                        //if (entity.Error != null)
                        //    continue;

#if REMOVED
                        string message = $"{action_name}成功";
                        if (lRet == 1 && string.IsNullOrEmpty(strError) == false)
                            message = strError;
                        entity.SetError(message,
                            lRet == 1 ? "yellow" : "green");
#endif

                        // TODO: 刷新读者信息显示。特别是一些关于借阅日期，借期，应还日期的内容
                    }
                }

                func_setProgress?.Invoke(-1, -1, -1, "处理完成");   // hide progress bar

                {
                    /*
                     * 
        ERROR dp2SSL 2020-03-05 16:49:47,472 - 重试专用线程出现异常: Type: System.Threading.Tasks.TaskCanceledException
        Message: 已取消一个任务。
        Stack:
        在 System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)
        在 System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification(Task task)
        在 System.Windows.Threading.DispatcherOperation.Wait(TimeSpan timeout)
        在 System.Windows.Threading.Dispatcher.InvokeImpl(DispatcherOperation operation, CancellationToken cancellationToken, TimeSpan timeout)
        在 System.Windows.Threading.Dispatcher.Invoke(Action callback, DispatcherPriority priority, CancellationToken cancellationToken, TimeSpan timeout)
        在 System.Windows.Threading.Dispatcher.Invoke(Action callback)
        在 dp2SSL.DoorItem.DisplayCount(List`1 entities, List`1 adds, List`1 removes, List`1 errors, List`1 _doors)
        在 dp2SSL.ShelfData.RefreshCount()
        在 dp2SSL.ShelfData.SubmitCheckInOut(Delegate_setProgress func_setProgress, IReadOnlyCollection`1 actions)
        在 dp2SSL.ShelfData.<>c__DisplayClass119_0.<StartRetryTask>b__1()                     * 
                     * */
                    // 重新装载读者信息和显示
                    try
                    {
                        // ShelfData.RefreshCount();
                        DoorItem.RefreshEntity(updates, ShelfData.Doors);
                        // App.CurrentApp.Speak(speak);
                    }
                    catch (Exception ex)
                    {
                        WpfClientInfo.WriteErrorLog($"SubmitCheckInOutAsync() 中的 RefreshEntity() 出现异常: {ExceptionUtil.GetDebugText(ex)}。为了避免破坏流程，这里截获了异常，让后续处理正常进行");
                    }
                }

                // TODO: 遇到通讯出错的请求，是否放入一个永久保存的数据结构里面，自动在稍后进行重试请求？
                // 把处理过的移走
                lock (_syncRoot_actions)
                {
                    foreach (var info in actions)
                    {
                        _actions.Remove(info);
                    }
                }

                return new SubmitResult
                {
                    Value = 1,
                    MessageDocument = doc,
                    // RetryActions = retry_actions,
                    ProcessedActions = processed,
                    ErrorActions = error_actions,
                };
            }
            finally
            {
                // _limit.Release();
            }
        }

        public static NormalResult SetEAS(string uid,
            string antenna,
            bool enable)
        {
            try
            {
                // testing
                // return new NormalResult { Value = -1, ErrorInfo = "修改 EAS 失败，测试" };

                // 2020/12/3 (减少真正需要发送指令给读写器执行修改 EAS 的次数)
                // 先尝试观察内存中的标签信息，看 EAS 是否已经到位
                var tag = BookTagList.FindTag(uid);
                if (tag != null && tag.OneTag.TagInfo != null)
                {
                    if (NewTagList.VerifyTagInfoEas(tag.OneTag.TagInfo, enable) == true)
                        return new NormalResult();
                }

                if (uint.TryParse(antenna, out uint antenna_id) == false)
                    antenna_id = 0;
                var result = RfidManager.SetEAS($"{uid}", antenna_id, enable);
                if (result.Value != -1)
                {
#if OLD_TAGCHANGED

                    TagList.SetEasData(uid, enable);
#else
                    BookTagList.SetEasData(uid, enable);
#endif
                }
                return result;
            }
            catch (Exception ex)
            {
                WpfClientInfo.WriteErrorLog($"SetEAS() 出现异常: {ExceptionUtil.GetDebugText(ex)}");
                return new NormalResult { Value = -1, ErrorInfo = ex.Message };
            }
        }


        #region 模拟标签测试

        // 将 RfidCenter 切换到真实标签模式
        public static NormalResult RestoreRealTags()
        {
            var result = RfidManager.SimuTagInfo("switchToRealMode", null, "");
            if (result.Value == -1)
            {
                // 这里只写入错误日志，不调用 App.SetError("simuReader")。
                // 原因是：一般是 dp2ssl 带动 RfidCenter 启动时调用一次切换状态，如果用 SetError() 报错，后面没有提供机会重试和清除报错，那么错误信息会一直没法消除
                WpfClientInfo.WriteErrorLog($"RestoreRealTags() error: {result.ErrorInfo}");
                // App.SetError("simuReader", result.ErrorInfo);
                return result;
            }

            // App.SetError("simuReader", null);
            return new NormalResult();
        }

#if AUTO_TEST


        const int TAG_COUNT_PER_DOOR = 30;   // 30

        // 将 RfidCenter 切换为模拟标签模式，并添加好标签
        public static NormalResult InitialSimuTags()
        {
            List<string> names = new List<string>();
            foreach (var door in Doors)
            {
                names.Add(door.ReaderName);
            }
            if (string.IsNullOrEmpty(ShelfData._patronReaderName) == false)
                names.Add(ShelfData._patronReaderName);
            StringUtil.RemoveDupNoSort(ref names);

            List<SimuTagInfo> tagInfos = null;
            {
                var result = LibraryChannelUtil.DownloadTagsInfo(null,
                    Doors.Count * TAG_COUNT_PER_DOOR,
                    null,
                    App.CancelToken);
                if (result.Value == -1)
                {
                    App.SetError("simuReader", result.ErrorInfo);
                    return result;
                }
                tagInfos = result.TagInfos;
            }

            {
                var result = RfidManager.SimuTagInfo("switchToSimuMode", null, $"readerNameList:{StringUtil.MakePathList(names, "|")}");
                if (result.Value == -1)
                {
                    App.SetError("simuReader", result.ErrorInfo);
                    return result;
                }
            }
            List<TagInfo> tags = new List<TagInfo>();
            // 对当前每个柜门，都给填充一定数量的标签
            int index = 0;
            foreach (var door in Doors)
            {
                for (int i = 0; i < TAG_COUNT_PER_DOOR; i++)
                {
                    LogicChip chip = new LogicChip();
                    SimuTagInfo info = null;
                    if (index < tagInfos.Count)
                        info = tagInfos[index];
                    else
                        info = new SimuTagInfo
                        {
                            PII = $"B{(index + 1).ToString().PadLeft(8, '0')}",
                            AccessNo = "?",
                            OI = "testoi"
                        };
                    chip.NewElement(ElementOID.PII, $"{info.PII}");
                    chip.NewElement(ElementOID.ShelfLocation, info.AccessNo);
                    chip.NewElement(ElementOID.OwnerInstitution, info.OI).WillLock = true;

                    var bytes = chip.GetBytes(4 * 20,
    4,
    GetBytesStyle.None,
    out string block_map);

                    var tag = new TagInfo
                    {
                        ReaderName = door.ReaderName,
                        AntennaID = (uint)door.Antenna,
                        BlockSize = 4,
                        MaxBlockCount = 28,
                        Bytes = bytes
                    };

                    tags.Add(tag);
                    index++;
                }
            }

            {
                var result = RfidManager.SimuTagInfo("setTag", tags, "");
                if (result.Value == -1)
                {
                    App.SetError("simuReader", result.ErrorInfo);
                    return result;
                }
            }

            App.SetError("simuReader", null);
            return new NormalResult();
        }

        // 为读者卡读卡器添加证卡
        public static NormalResult SimuAddPatronTag()
        {
            List<TagInfo> tags = new List<TagInfo>();
            TagInfo tag = new TagInfo
            {
                UID = "6DB28CAF",
                ReaderName = ShelfData._patronReaderName
            };
            tags.Add(tag);

            var result = RfidManager.SimuTagInfo("setTag",
                tags,
                $"protocol:{InventoryInfo.ISO14443A}");
            if (result.Value == -1)
            {
                App.SetError("simuReader", result.ErrorInfo);
                return result;
            }
            App.SetError("simuReader", null);
            return new NormalResult();
        }

        // 为读者卡读卡器添加工作人员身份卡
        public static NormalResult SimuAddWorkerTag()
        {
            List<TagInfo> tags = new List<TagInfo>();

            // 构造工作人员卡
            {
                LogicChip chip = new LogicChip();

                chip.NewElement(ElementOID.PII, $"~supervisor");
                chip.NewElement(ElementOID.TypeOfUsage, "80");

                var bytes = chip.GetBytes(4 * 20,
4,
GetBytesStyle.None,
out string block_map);

                var tag = new TagInfo
                {
                    UID = "12345678",
                    ReaderName = ShelfData._patronReaderName,
                    // AntennaID = (uint)door.Antenna,
                    BlockSize = 4,
                    MaxBlockCount = 28,
                    Bytes = bytes
                };
                tags.Add(tag);
            }

            var result = RfidManager.SimuTagInfo("setTag",
                tags,
                $"protocol:{InventoryInfo.ISO15693}");
            if (result.Value == -1)
            {
                App.SetError("simuReader", result.ErrorInfo);
                return result;
            }
            App.SetError("simuReader", null);
            return new NormalResult();
        }

        // 模拟移走读者卡
        // result.Value:
        //      -1  出错
        //      0   没有找到需要移走的读者卡
        //      其他  移走的读者卡数量
        public static NormalResult SimuRemovePatronTag()
        {
            List<TagInfo> tags = new List<TagInfo>();
            // 先寻找读者卡
            foreach (var tag in PatronTagList.Tags)
            {
                if (tag.OneTag.Protocol == InventoryInfo.ISO14443A
                    || tag.Type == "patron"
                    || tag.OneTag.ReaderName == _patronReaderName)
                {
                    // tags.Add(tag.OneTag.TagInfo);
                    tags.Add(new TagInfo { UID = tag.OneTag.UID });
                }
            }
            if (tags.Count == 0)
                return new NormalResult();

            {
                var result = RfidManager.SimuTagInfo("removeTag", tags, "");
                if (result.Value == -1)
                {
                    App.SetError("simuReader", result.ErrorInfo);
                    return result;
                }

                App.SetError("simuReader", null);
                return new NormalResult { Value = tags.Count };
            }
        }

        public static List<TagInfo> GetAllTagInfo(List<DoorItem> doors)
        {
            List<TagInfo> results = new List<TagInfo>();
            foreach (var entity in ShelfData.l_All)
            {
                if (Match(doors, entity))
                    results.Add(entity.TagInfo);
            }
            return results;
        }

        /*
        public static List<string> GetAllTagsUid(List<DoorItem> doors)
        {
            List<string> results = new List<string>();
            foreach (var entity in ShelfData.l_All)
            {
                if (Match(doors, entity))
                    results.Add(entity.UID);
            }
            return results;
        }
        */

        static bool Match(List<DoorItem> doors, Entity entity)
        {
            foreach (var door in doors)
            {
                if (Match(door, entity))
                    return true;
            }

            return false;
        }

        static bool Match(DoorItem door, Entity entity)
        {
            if (entity.ReaderName == door.ReaderName && entity.Antenna == door.Antenna.ToString())
                return true;
            return false;
        }

        // 从读卡器上移走指定的标签
        public static NormalResult SimuRemoveTags(List<TagInfo> tags)
        {
            var result = RfidManager.SimuTagInfo("removeTag", tags, "");
            if (result.Value == -1)
            {
                App.SetError("simuReader", result.ErrorInfo);
                return result;
            }

            return new NormalResult();
        }

        /*
        // 从读卡器上移走指定的标签
        public static NormalResult SimuRemoveTags(List<string> uids)
        {
            List<TagInfo> tags = new List<TagInfo>();
            foreach (var uid in uids)
            {
                tags.Add(new TagInfo { UID = uid });
            }

            var result = RfidManager.SimuTagInfo("removeTag", tags, "");
            if (result.Value == -1)
            {
                App.SetError("simuReader", result.ErrorInfo);
                return result;
            }

            return new NormalResult();
        }
        */
#endif
        #endregion

        class OfflineItem
        {
            public string UII { get; set; }
            public string RecPath { get; set; }
            public string Xml { get; set; }
            public byte[] Timestamp { get; set; }

            public string Title { get; set; }
        }

        public static async Task<NormalResult> ImportOfflineEntityAsync(
    string filename,
    delegate_showText func_showProgress,
    CancellationToken token)
        {
            try
            {
                int count = 0;
                using (var s = new StreamReader(filename, Encoding.UTF8))
                using (var reader = new JsonTextReader(s))
                using (BiblioCacheContext context = new BiblioCacheContext())
                {
                    while (token.IsCancellationRequested == false)
                    {
                        // https://www.newtonsoft.com/json/help/html/ReadMultipleContentWithJsonReader.htm
                        if (!reader.Read())
                            break;

                        if (reader.TokenType == JsonToken.StartArray
                            || reader.TokenType == JsonToken.EndArray
                            || reader.TokenType == JsonToken.Comment)
                            continue;

                        JsonSerializer serializer = new JsonSerializer();
                        OfflineItem o = serializer.Deserialize<OfflineItem>(reader);

                        func_showProgress?.Invoke($"正在导入 {o.UII} {o.Title} ...");

                        // 保存册记录到本地数据库
                        await EntityReplication.AddOrUpdateAsync(context,
                            new EntityItem
                            {
                                RecPath = o.RecPath,
                                PII = o.UII,
                                Xml = o.Xml,
                                Timestamp = o.Timestamp,
                            });

                        // 保存书目摘要
                        await LibraryChannelUtil.AddOrUpdateAsync(context,
                            new BiblioSummaryItem
                            {
                                PII = o.UII,
                                BiblioSummary = o.Title
                            });

                        count++;
                    }
                }

                return new NormalResult { Value = count };
            }
            catch (Exception ex)
            {
                WpfClientInfo.WriteErrorLog($"ImportOfflineEntityAsync() 出现异常: {ExceptionUtil.GetDebugText(ex)}");
                return new NormalResult
                {
                    Value = -1,
                    ErrorInfo = $"ImportOfflineEntityAsync() 出现异常: {ex.Message}"
                };
            }
        }

        #region 本地软时钟

        static long _deltaTicks = 0;

        public static DateTime Now
        {
            get
            {
                return DateTime.Now + TimeSpan.FromTicks(_deltaTicks);
            }
        }

        // 从文件中装载
        public static void LoadSoftClock()
        {
            try
            {
                var fileName = Path.Combine(WpfClientInfo.UserDir, "softclock.bin");
                if (File.Exists(fileName))
                {
                    using (BinaryReader reader = new BinaryReader(File.Open(fileName, FileMode.Open)))
                    {
                        _deltaTicks = reader.ReadInt64();
                    }
                }
            }
            catch (Exception ex)
            {
                _deltaTicks = 0;
                WpfClientInfo.WriteErrorLog($"LoadSoftClock() 出现异常: {ExceptionUtil.GetDebugText(ex)}");
            }
        }

        public static long SetSoftClock(long ticks)
        {
            var old_value = _deltaTicks;
            _deltaTicks = ticks;
            return old_value;
        }

        // 保存到文件
        public static void SaveSoftClock()
        {
            try
            {
                var fileName = Path.Combine(WpfClientInfo.UserDir, "softclock.bin");
                using (BinaryWriter writer = new BinaryWriter(File.Open(fileName, FileMode.Create)))
                {
                    writer.Write(_deltaTicks);
                }
            }
            catch (Exception ex)
            {
                WpfClientInfo.WriteErrorLog($"SaveSoftClock() 出现异常: {ExceptionUtil.GetDebugText(ex)}");
            }
        }

        #endregion

        // 构建一个表达当前所有标签的字符串集合。用于比对
        public static List<string> BuildCurrentTagLines()
        {
            List<string> results = new List<string>();
            foreach (var entity in _all)
            {
                results.Add($"{entity.GetOiPii(true)}|{entity.ReaderName}:{entity.Antenna}");
            }

            return results;
        }

        #region 标签集合比对相关

        public static string BuildTagLineJsonString()
        {
            var lines = BuildCurrentTagLines();
            return JsonConvert.SerializeObject(lines);
        }

        public static List<string> ParseTagLineString(string value)
        {
            if (string.IsNullOrEmpty(value))
                return new List<string>();
            return JsonConvert.DeserializeObject<List<string>>(value);
        }

        // 记忆本次启动阶段写入动作库的时间
        public static void SetWriteTime(DateTime time)
        {
            string value = JsonConvert.SerializeObject(time);
            WpfClientInfo.Config?.Set("actions", "initialWriteTime", value);
        }

        // 获得上次启动阶段写入动作库的时间
        public static DateTime GetWriteTime()
        {
            string value = WpfClientInfo.Config?.Get("actions", "initialWriteTime");
            if (string.IsNullOrEmpty(value))
                return DateTime.MinValue;
            return JsonConvert.DeserializeObject<DateTime>(value);
        }

        // 间隔多少时间必须在初始化阶段写入动作库至少一次
        public static TimeSpan ForceWriteLength = TimeSpan.FromDays(30);

        #endregion


        public static string IsOiChanging(string old_location, string new_location)
        {
            string old_oi = "";
            {
                ShelfData.GetOwnerInstitution(old_location, out string isil, out string alternative);
                if (string.IsNullOrEmpty(isil) == false)
                    old_oi = isil;
                else if (string.IsNullOrEmpty(alternative) == false)
                    old_oi = alternative;
            }

            string new_oi = "";
            {
                ShelfData.GetOwnerInstitution(new_location, out string isil, out string alternative);
                if (string.IsNullOrEmpty(isil) == false)
                    new_oi = isil;
                else if (string.IsNullOrEmpty(alternative) == false)
                    new_oi = alternative;
            }

            if (string.IsNullOrEmpty(old_oi) && string.IsNullOrEmpty(new_oi))
                return null;

            if (old_oi == new_oi)
                return null;

            return $"馆藏地从 '{old_location}' 变为 '{new_location}' 将导致机构代码从 '{old_oi}' 变为 '{new_oi}'";
        }

        /*
        static Operator OperatorFromRequest(RequestItem request)
        {
            if (request.PatronName == null
                && request.PatronBarcode == null)
                return null;
            return new Operator
            {
                PatronName = request.PatronName,
                PatronBarcode = request.PatronBarcode,
            };
        }

        static Entity EntityFromRequest(RequestItem request)
        {
            return new Entity
            {
                UID = request.UID,
                ReaderName = request.ReaderName,
                Antenna = request.Antenna,
                PII = request.PII,
                ItemRecPath = request.ItemRecPath,
                Title = request.Title,
                Location = request.ItemLocation,
                CurrentLocation = request.ItemCurrentLocation,
                ShelfNo = request.ShelfNo,
                State = request.State
            };
        }
        */

#if NO
        // 从外部存储中装载以前遗留的 Actions
        public static void LoadRetryActions()
        {
            using (var context = new MyContext())
            {
                context.Database.EnsureCreated();
                var items = context.Requests.ToList();
                AddRetryActions(FromRequests(items));

                WpfClientInfo.WriteInfoLog($"从本地数据库装载 RetryActions 成功。内容如下：\r\n{ActionInfo.ToString(_retryActions)}");
            }
        }

        public static void SaveRetryActions()
        {
            try
            {
                using (var context = new MyContext())
                {
                    // context.Database.EnsureDeleted();
                    // context.Database.EnsureCreated();

                    context.Database.EnsureCreated();
                    {
                        var allRec = context.Requests;
                        context.Requests.RemoveRange(allRec);
                        context.SaveChanges();
                    }

                    lock (_syncRoot_retryActions)
                    {
                        context.Requests.AddRange(FromActions(_retryActions));
                    }
                    context.SaveChanges();

                    WpfClientInfo.WriteInfoLog($"RetryActions 保存到本地数据库成功。内容如下：\r\n{ActionInfo.ToString(_retryActions)}");
                }
            }
            catch (Exception ex)
            {
                WpfClientInfo.WriteErrorLog($"SaveRetryActions() 出现异常: {ExceptionUtil.GetDebugText(ex)}");
            }
        }

#endif






#if NO
        // 启动重试任务。此任务长期在后台运行
        public static void StartRetryTask()
        {
            if (_retryTask != null)
                return;

            CancellationToken token = _cancel.Token;

            token.Register(() =>
            {
                _eventRetry.Set();
            });

            // 启动重试专用线程
            _retryTask = Task.Factory.StartNew(() =>
                {
                    WpfClientInfo.WriteInfoLog("重试专用线程开始");
                    try
                    {
                        while (token.IsCancellationRequested == false)
                        {
                            // TODO: 无论是整体退出，还是需要激活，都需要能中断 Delay
                            // Task.Delay(TimeSpan.FromSeconds(10)).Wait(token);
                            _eventRetry.WaitOne(TimeSpan.FromSeconds(10));
                            token.ThrowIfCancellationRequested();

                            List<ActionInfo> actions = null;
                            lock (_syncRoot_retryActions)
                            {
                                actions = new List<ActionInfo>(_retryActions);
                            }

                            if (actions.Count == 0)
                                continue;

                            // 准备对话框
                            SubmitWindow progress = PageMenu.PageShelf?.OpenProgressWindow();

                            var result = SubmitCheckInOut(
                            (min, max, value, text) =>
                            {
                                if (progress != null)
                                {
                                    Application.Current.Dispatcher.Invoke(new Action(() =>
                                    {
                                        if (min == -1 && max == -1 && value == -1)
                                            progress.ProgressBar.Visibility = Visibility.Collapsed;
                                        else
                                            progress.ProgressBar.Visibility = Visibility.Visible;

                                        if (text != null)
                                            progress.TitleText = text;

                                        if (min != -1)
                                            progress.ProgressBar.Minimum = min;
                                        if (max != -1)
                                            progress.ProgressBar.Maximum = max;
                                        if (value != -1)
                                            progress.ProgressBar.Value = value;
                                    }));
                                }
                            },
                            actions);

                            // 将 submit 情况写入日志备查
                            WpfClientInfo.WriteInfoLog($"重试提交请求:\r\n{ActionInfo.ToString(actions)}\r\n返回结果:{result.ToString()}");

                            List<ActionInfo> processed = new List<ActionInfo>();
                            if (result.RetryActions != null)
                            {
                                foreach (var action in actions)
                                {
                                    if (result.RetryActions.IndexOf(action) == -1)
                                        processed.Add(action);
                                }
                            }

                            // TODO: 保存到数据库。这样不怕中途断电或者异常退出

                            // 把处理掉的 ActionInfo 对象移走
                            lock (_syncRoot_retryActions)
                            {
                                foreach (var action in processed)
                                {
                                    _retryActions.Remove(action);
                                }

                                RefreshRetryInfo();
                            }

                            // 把执行结果显示到对话框内
                            // 全部事项都重试失败的时候不需要显示
                            if (processed.Count > 0 && progress != null)
                            {
                                if (result.Value == -1)
                                    progress?.PushContent(result.ErrorInfo, "red");
                                else if (result.Value == 1 && result.MessageDocument != null)
                                {
                                    Application.Current.Dispatcher.Invoke(new Action(() =>
                                    {
                                        progress?.PushContent(result.MessageDocument);
                                    }));
                                }

                                // 显示出来
                                Application.Current.Dispatcher.Invoke(new Action(() =>
                                {
                                    progress?.ShowContent();
                                }));
                            }
                        }
                        _retryTask = null;

                    }
                    catch (Exception ex)
                    {
                        WpfClientInfo.WriteErrorLog($"重试专用线程出现异常: {ExceptionUtil.GetDebugText(ex)}");
                    }
                    finally
                    {
                        WpfClientInfo.WriteInfoLog("重试专用线程结束");
                    }
                },
token,
TaskCreationOptions.LongRunning,
TaskScheduler.Default);
        }

#endif

        /*
        public static void AddRetryActions(List<ActionInfo> actions)
        {
            lock (_syncRoot_retryActions)
            {
                _retryActions.AddRange(actions);
                RefreshRetryInfo();
            }
        }
        */

#if NO
        public static void ClearRetryActions()
        {
            lock (_syncRoot_retryActions)
            {
                _retryActions.Clear();
                RefreshRetryInfo();
            }
        }
#endif

        /*
    public static int RetryActionsCount
    {
        get
        {
            lock (_syncRoot_retryActions)
            {
                return _retryActions.Count;
            }
        }
    }
    */


#if NO
        // 把动作写入本地操作日志
        // parameters:
        //      initial 是否为书柜启动时候的初始化操作
        public static async Task SaveOperations(List<ActionInfo> actions,
            bool initial)
        {
            try
            {
                using (var context = new MyContext())
                {
                    foreach (var action in actions)
                    {
                        var operation = FromAction(action, initial);
                        context.Operations.Add(operation);
                    }
                    int count = await context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                // TODO: 出现此错误，要把应用挂起，显示警告请管理员介入处理
                WpfClientInfo.WriteErrorLog($"SaveOperations() 出现异常：{ExceptionUtil.GetDebugText(ex)}");
                throw ex;
            }
        }

        // TODO: 省略记载 transfer 操作？但记载工作人员典藏移交的操作
        static Operation FromAction(ActionInfo action, bool initial)
        {
            Operation result = new Operation();
            if (action.Action == "borrow")
                result.Action = "checkout";
            else if (action.Action == "return")
            {
                if (initial)
                    result.Action = "inventory";
                else
                    result.Action = "checkin";
            }
            else if (action.Action == "transfer")
                result.Action = "transfer";
            else
                result.Action = "~" + action.Action;

            if (initial)
                result.Condition = "initial";   // 表示这是书柜启动时候的初始化操作

            result.UID = action.Entity?.UID;
            result.PII = action.Entity?.PII;
            result.Antenna = action.Entity?.Antenna;
            result.Title = action.Entity?.Title;
            result.Operator = GetOperatorString(action.Operator);
            result.OperTime = DateTime.Now;

            if (action.Action == "transfer")
            {
                result.Parameter = JsonConvert.SerializeObject(new
                {
                    batchNo = action.BatchNo,
                    location = action.Location,
                    currentShelfNo = action.CurrentShelfNo,
                    direction = action.TransferDirection,
                });
            }
            return result;

            string GetOperatorString(Operator person)
            {
                if (person == null)
                    return null;
                return JsonConvert.SerializeObject(new
                {
                    name = person.PatronName,
                    barcode = person.PatronBarcode,
                });
            }
        }

#endif

#if REMOVED
        #region 门命令延迟执行

        // 门命令(延迟执行)队列。开门时放一个命令进入队列。等得到门开信号的时候再取出这个命令
        static List<CommandItem> _commandQueue = new List<CommandItem>();
        static object _syncRoot_commandQueue = new object();

        public static void PushCommand(DoorItem door,
            Operator person,
            long heartbeat)
        {
            CommandItem command = new CommandItem
            {
                Command = "setOwner",
                Door = door,
                Parameter = person,
                Heartbeat = heartbeat,
            };

            lock (_syncRoot_commandQueue)
            {
                if (_commandQueue.Count > 1000)
                {
                    _commandQueue.Clear();
                    WpfClientInfo.WriteErrorLog("_commandQueue 元素个数超过 1000。为保证安全自动清除了全部元素");
                }
                _commandQueue.Add(command);
                WpfClientInfo.WriteInfoLog($"PushCommand {command.ToString()}");
            }
        }

        public static CommandItem PopCommand(DoorItem door, string comment = "")
        {
            lock (_syncRoot_commandQueue)
            {
                CommandItem result = null;
                foreach (var command in _commandQueue)
                {
                    if (command.Door == door)
                    {
                        result = command;
                        break;
                    }
                }

                if (result == null)
                {
                    WpfClientInfo.WriteInfoLog($"PopCommand (door={door.Name} 时间={RfidManager.LockHeartbeat}) ({comment}) not found command");
                    return null;
                }
                _commandQueue.Remove(result);
                WpfClientInfo.WriteInfoLog($"PopCommand (door={door.Name} 时间={RfidManager.LockHeartbeat}) ({comment}) {result.ToString()}");
                return result;
            }
        }

        // 检查命令队列。观察是否有超过合理时间的命令滞留，如果有就返回它们
        public static List<CommandItem> CheckCommands(long currentHeartbeat)
        {
            List<CommandItem> results = new List<CommandItem>();
            lock (_syncRoot_commandQueue)
            {
                foreach (var command in _commandQueue)
                {
                    // 和当初 push 时候间隔了多个心跳
                    if (currentHeartbeat >= command.Heartbeat + 1)  // +1
                    {
                        results.Add(command);
                    }
                }
            }

            return results;
        }

        public static string CommandToString()
        {
            lock (_syncRoot_commandQueue)
            {
                StringBuilder text = new StringBuilder();
                int i = 0;
                foreach (var command in _commandQueue)
                {
                    text.AppendLine($"{i + 1}) {command.ToString()}");
                    i++;
                }
                return text.ToString();
            }
        }


        #endregion
#endif
    }

    // 操作者
    public class Operator
    {
        public string PatronName { get; set; }

        public string PatronBarcode { get; set; }
        // 2020/7/26
        // 读者的 OI 或者 AOI
        public string PatronInstitution { get; set; }

        [JsonIgnore]
        public string PatronNameMasked
        {
            get
            {
                var def = ShelfData.GetPatronMask();
                return dp2StringUtil.Mask(def, PatronName, "name");
            }
        }

        [JsonIgnore]
        public string PatronBarcodeMasked
        {
            get
            {
                var def = ShelfData.GetPatronMask();
                return dp2StringUtil.Mask(def, PatronBarcode, "barcode");
            }
        }

        public Operator Clone()
        {
            Operator dup = new Operator();
            dup.PatronName = this.PatronName;
            dup.PatronBarcode = this.PatronBarcode;
            dup.PatronInstitution = this.PatronInstitution;
            return dup;
        }

        public override string ToString()
        {
            return $"PatronName:{PatronName}, PatronBarcode:{PatronBarcode}, PatronInstitution:{PatronInstitution}";
        }

        public string GetDisplayString()
        {
            if (string.IsNullOrEmpty(PatronName) == false)
                return PatronName;
            return PatronBarcode;
        }

        public string GetDisplayStringMasked()
        {
            if (string.IsNullOrEmpty(PatronNameMasked) == false)
                return PatronNameMasked;
            return PatronBarcodeMasked;
        }

        public static bool IsPatronBarcodeWorker(string patronBarcode)
        {
            if (string.IsNullOrEmpty(patronBarcode))
                return false;
            return patronBarcode.StartsWith("~");
        }

        public static string BuildWorkerAccountName(string text)
        {
            return text.Substring(1);
        }

        public bool IsWorker
        {
            get
            {
                return IsPatronBarcodeWorker(this.PatronBarcode);
            }
        }

        public string GetWorkerAccountName()
        {
            if (this.IsWorker == true)
                return BuildWorkerAccountName(this.PatronBarcode);
            return "";
        }
    }

    public class ActionInfo
    {
        public Operator Operator { get; set; }  // 提起请求的读者
        public DateTime OperTime { get; set; }  // 首次操作的时间
        public Entity Entity { get; set; }
        public string Action { get; set; }  // borrow/return/transfer
        public string TransferDirection { get; set; } // in/out 典藏移交的方向
        public string Location { get; set; }    // 所有者馆藏地。transfer 动作会用到
        public string CurrentShelfNo { get; set; }  // 当前架号。transfer 动作会用到
        public string BatchNo { get; set; } // 批次号。transfer 动作会用到。建议可以用当前用户名加上日期构成

        // 状态 
        // sync/dontsync/commerror/normalerror/空
        // 对应于: 同步成功/不再同步/通讯出错/一般出错/从未同步过
        public string State { get; set; }
        public string SyncErrorInfo { get; set; }   // 最近一次同步操作的报错信息
        public string SyncErrorCode { get; set; }   // 最近一次同步操作的错误码
        public int SyncCount { get; set; } // 已经进行过的同步重试次数
        public int ID { get; set; } // 日志数据库中对应的记录 ID

        public DateTime SyncOperTime { get; set; }  // 最后一次同步操作的时间
        public string ActionString { get; set; }    // 存储 BorrowInfo 或者 ReturnInfo 的 JSON 化字符串

        public override string ToString()
        {
            return $"Action={Action},TransferDirection={TransferDirection},Location={Location},CurrentShelfNo={CurrentShelfNo},Operator=[{Operator}],Entity=[{ToString(this.Entity)}],BatchNo={BatchNo},State=[{State}],SyncErrorInfo=[{SyncErrorInfo}],SyncErrorCode=[{SyncErrorCode}],SyncCount=[{SyncCount}],SyncOperTime=[{SyncOperTime}],ID={ID},ActionString=[{ActionString}]";
        }

        public static string ToString(Entity entity)
        {
            return $"PII:{entity.PII},OI:{entity.OI},UID:{entity.UID},Title:{entity.Title},ItemRecPath:{entity.ItemRecPath},ReaderName:{entity.ReaderName},Antenna:{entity.Antenna},AOI:{entity.AOI}";
        }

        public static string ToString(List<ActionInfo> actions)
        {
            if (actions == null)
                return "(null)";
            StringBuilder text = new StringBuilder();
            text.AppendLine($"ActionInfo 对象共 {actions.Count} 个:");
            int i = 0;
            foreach (var action in actions)
            {
                text.AppendLine($"{(i + 1)}) {action.ToString()}");
                i++;
            }

            return text.ToString();
        }
    }



    public class SubmitResult : NormalResult
    {
        // [out]
        public MessageDocument MessageDocument { get; set; }

        // [out]
        // 发生了错误，但不需要后面重试提交的 ActionInfo 对象集合
        public List<ActionInfo> ErrorActions { get; set; }

        // [out]
        // 处理过的 ActionInfo 对象集合。这里面包含成功的，和失败的
        public List<ActionInfo> ProcessedActions { get; set; }


        // [out]
        // 发生了错误，需要后面重试提交的 ActionInfo 对象集合
        // public List<ActionInfo> RetryActions { get; set; }

        public override string ToString()
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine(base.ToString());
            if (ErrorActions != null && ErrorActions.Count > 0)
            {
                text.AppendLine($"发生了错误(但不需要重试)的 ActionInfo:({ErrorActions.Count})");
                text.AppendLine(ActionInfo.ToString(ErrorActions));
            }
            /*
            if (RetryActions != null && RetryActions.Count > 0)
            {
                text.AppendLine($"需要重试的 ActionInfo:({RetryActions.Count})");
                text.AppendLine(ActionInfo.ToString(RetryActions));
            }
            */
            if (ProcessedActions != null && ProcessedActions.Count > 0)
            {
                text.AppendLine($"处理过的 ActionInfo:({ProcessedActions.Count})");
                text.AppendLine(ActionInfo.ToString(ProcessedActions));
            }
            return text.ToString();
        }
    }

    public class AntennaList
    {
        public string ReaderName { get; set; }
        public List<int> Antennas { get; set; }
    }

    public class CommandItem
    {
        public DoorItem Door { get; set; }
        public string Command { get; set; }
        public object Parameter { get; set; }
        public long Heartbeat { get; set; }

        // TODO: 是否增加一个时间成员，用以测算 item 在 queue 中的留存时间？时间太长了说明不正常，需要排除故障

        public override string ToString()
        {
            return $"DoorName:{Door.Name}, Command:{Command}, Parameter:{Parameter}, Heartbeat:{Heartbeat}";
        }
    }
}
