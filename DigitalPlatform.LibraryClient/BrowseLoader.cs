﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

using DigitalPlatform.Text;

namespace DigitalPlatform.LibraryClient
{
    /// <summary>
    /// 快速获得浏览行信息
    /// </summary>
    public class BrowseLoader : IEnumerable
    {
        /// <summary>
        /// 提示框事件
        /// </summary>
        public event MessagePromptEventHandler Prompt = null;

        List<string> m_recpaths = new List<string>();

        public List<string> RecPaths
        {
            get
            {
                return this.m_recpaths;
            }
            set
            {
                this.m_recpaths = value;
            }
        }

        public string Format
        {
            get;
            set;
        }

        public LibraryChannel Channel
        {
            get;
            set;
        }

        public Stop Stop
        {
            get;
            set;
        }

        public IEnumerator GetEnumerator()
        {
            List<string> batch = new List<string>();
            for (int index = 0; index < m_recpaths.Count; index++)
            {
                string s = m_recpaths[index];
                batch.Add(s);

                // 每100个一批，或者最后一次
                if (batch.Count >= 100
                    || GetBatchChars(batch) >= 50 * 1024
                    || (index == m_recpaths.Count - 1 && batch.Count > 0))
                {
                REDO:
#if NO
                    string[] paths = new string[batch.Count];
                    batch.CopyTo(paths);
#endif
                    string[] paths = batch.ToArray();

                    DigitalPlatform.LibraryClient.localhost.Record[] searchresults = null;
                    string strError = "";

                    long lRet = Channel.GetBrowseRecords(
                        this.Stop,
                        paths,
                        this.Format,    // "id,cols",
                        out searchresults,
                        out strError);
                    if (lRet == -1)
                    {
                        // throw new Exception(strError);

                        if (this.Prompt != null)
                        {
                            MessagePromptEventArgs e = new MessagePromptEventArgs();
                            e.MessageText = "获得浏览记录时发生错误： " + strError + "\r\npaths='" + StringUtil.MakePathList(paths) + "' (" + this.Format + ")";
                            e.Actions = "yes,no,cancel";
                            this.Prompt(this, e);
                            if (e.ResultAction == "cancel")
                                throw new ChannelException(Channel.ErrorCode, strError);
                            else if (e.ResultAction == "yes")
                                goto REDO;
                            else
                            {
                                // no 也是抛出异常。因为继续下一批代价太大
                                throw new ChannelException(Channel.ErrorCode, strError);
                            }
                        }
                        else
                            throw new ChannelException(Channel.ErrorCode, strError);

                    }

                    if (searchresults == null)
                    {
                        strError = "searchresults == null";
                        throw new Exception(strError);
                    }

                    for (int i = 0; i < searchresults.Length; i++)
                    {
                        DigitalPlatform.LibraryClient.localhost.Record record = searchresults[i];

                        /*
                        // 2021/5/19
                        if (record.RecordBody != null && record.RecordBody.Result != null
                            && record.RecordBody.Result.ErrorCode == localhost.ErrorCodeValue.AccessDenied)
                        {
                            throw new Exception("(2)下标 " + i + " 的 batch 元素 '" + batch[i] + "' 访问被拒绝: " + record.RecordBody.Result.ErrorString);
                        }
                        */

                        var path = GetPath(batch[i]);
                        if (path != record.Path)
                        {
                            throw new Exception("(2)下标 " + i + " 的 batch 元素 '" + batch[i] + "' 和返回的该下标位置 GetBrowseRecords() 结果路径 '" + record.Path + "' 不匹配。有可能是账户权限不足");
                        }
                        Debug.Assert(path == record.Path, "");
                        yield return record;
                    }

                    // CONTINUE:
                    if (batch.Count > searchresults.Length)
                    {
                        // 有本次没有获取到的记录
                        batch.RemoveRange(0, searchresults.Length);
                        if (index == m_recpaths.Count - 1)
                            goto REDO;  // 当前已经是最后一轮了，需要继续做完

                        // 否则可以留给下一轮处理
                    }
                    else
                        batch.Clear();
                }
            }
        }

        public static string GetPath(string text)
        {
            if (text == null)
                return null;
            int index = text.IndexOf(":");
            if (index == -1)
                return text;
            return text.Substring(0, index);
        }

        static int GetBatchChars(List<string> batch)
        {
            int count = 0;
            foreach(var s in batch)
            {
                if (string.IsNullOrEmpty(s))
                    continue;
                count += s.Length;
            }

            return count;
        }
    }
}
