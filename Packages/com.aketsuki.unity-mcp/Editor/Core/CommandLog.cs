using System;
using UnityEngine;

namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// 指令系统的日志出口
    /// <para>
    /// 默认写入 UnityEngine.Debug；可替换 <see cref="Sink"/> 重定向到文件、编辑器窗口或测试框架。
    /// 替换后不会触碰 UnityEngine，便于在纯 C# 环境下做单元测试。
    /// </para>
    /// </summary>
    public static class CommandLog
    {
        /// <summary>日志级别</summary>
        public enum Level
        {
            Info,
            Warning,
            Error
        }

        /// <summary>
        /// 日志输出目标；为 null 时使用默认的 Debug 输出（<see cref="DefaultSink"/>）
        /// </summary>
        public static Action<Level, string> Sink;

        /// <summary>默认输出目标：写入 UnityEngine.Debug</summary>
        public static readonly Action<Level, string> DefaultSink = (level, text) =>
        {
            switch (level)
            {
                case Level.Warning:
                    Debug.LogWarning(text);
                    break;
                case Level.Error:
                    Debug.LogError(text);
                    break;
                default:
                    Debug.Log(text);
                    break;
            }
        };

        /// <summary>是否默认加前缀 [Cmd]</summary>
        public static bool UsePrefix { get; set; } = true;

        /// <summary>输出普通日志</summary>
        public static void Info(string message)
        {
            Emit(Level.Info, message);
        }

        /// <summary>输出警告</summary>
        public static void Warning(string message)
        {
            Emit(Level.Warning, message);
        }

        /// <summary>输出错误</summary>
        public static void Error(string message)
        {
            Emit(Level.Error, message);
        }

        private static void Emit(Level level, string message)
        {
            var text = UsePrefix ? "[Cmd] " + message : message;

            var sink = Sink ?? DefaultSink;
            sink(level, text);
        }
    }
}
