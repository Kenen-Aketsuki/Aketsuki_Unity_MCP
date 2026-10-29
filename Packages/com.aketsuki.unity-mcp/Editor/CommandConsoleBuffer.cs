using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 编辑器日志的环形缓冲，供 AI 读取运行时异常。
    ///
    /// <para>
    /// 编译错误不走这里（走 <see cref="CommandCompileMonitor"/>）；这里收的是运行时日志、
    /// 未捕获异常、Debug.LogError 等——也就是"改完代码跑起来之后出什么事"。
    /// </para>
    ///
    /// <para>
    /// 用 <see cref="Application.logMessageReceivedThreaded"/>：日志可能来自任意线程，
    /// 缓冲必须加锁；容量固定，超出丢弃最旧的，避免长会话吃内存。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class CommandConsoleBuffer
    {
        private const int Capacity = 400;
        private const int MaxStackChars = 1200;

        private sealed class Item
        {
            public string Time;
            public string Type;
            public string Message;
            public string Stack;
        }

        private static readonly Queue<Item> Items = new Queue<Item>(Capacity);
        private static readonly object Gate = new object();

        static CommandConsoleBuffer()
        {
            Application.logMessageReceivedThreaded += OnLog;
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            var item = new Item
            {
                Time = DateTime.Now.ToString("HH:mm:ss.fff"),
                Type = type.ToString(),
                Message = condition ?? string.Empty,
                Stack = Truncate(stackTrace)
            };

            lock (Gate)
            {
                while (Items.Count >= Capacity)
                {
                    Items.Dequeue();
                }

                Items.Enqueue(item);
            }
        }

        /// <summary>
        /// 取最近若干条日志。
        /// </summary>
        /// <param name="count">最多返回多少条（从新到旧截取，返回时仍按时间正序）</param>
        /// <param name="typeFilter">all / error（含 Exception、Assert）/ warning / log</param>
        /// <param name="contains">可选：只保留消息或堆栈包含该子串的条目（大小写不敏感）</param>
        /// <param name="clear">取完是否清空缓冲</param>
        public static JsonValue Snapshot(int count, string typeFilter, string contains, bool clear)
        {
            if (count <= 0)
            {
                count = 30;
            }

            count = Math.Min(count, 200);
            var filter = (typeFilter ?? "all").Trim().ToLowerInvariant();
            var needle = string.IsNullOrEmpty(contains) ? null : contains;

            var matched = new List<JsonValue>();
            lock (Gate)
            {
                var all = Items.ToArray();
                for (var i = all.Length - 1; i >= 0 && matched.Count < count; i--)
                {
                    var item = all[i];
                    if (!Matches(item, filter, needle))
                    {
                        continue;
                    }

                    var json = JsonValue.NewObject();
                    json.Set("time", JsonValue.Of(item.Time));
                    json.Set("type", JsonValue.Of(item.Type));
                    json.Set("message", JsonValue.Of(item.Message));
                    json.Set("stack", JsonValue.Of(item.Stack ?? string.Empty));
                    matched.Add(json);
                }

                if (clear)
                {
                    Items.Clear();
                }
            }

            // 上面是从新往旧取的，反转回时间正序，读起来更自然
            matched.Reverse();
            return JsonValue.FromArray(matched);
        }

        private static bool Matches(Item item, string filter, string needle)
        {
            if (!string.IsNullOrEmpty(needle))
            {
                var hit = (item.Message != null && item.Message.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                          || (item.Stack != null && item.Stack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!hit)
                {
                    return false;
                }
            }

            switch (filter)
            {
                case "":
                case "all":
                    return true;
                case "error":
                    return item.Type == "Error" || item.Type == "Exception" || item.Type == "Assert";
                case "warning":
                    return item.Type == "Warning";
                case "log":
                    return item.Type == "Log";
                default:
                    return true;
            }
        }

        private static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Length <= MaxStackChars ? text : text.Substring(0, MaxStackChars) + "…";
        }
    }
}
