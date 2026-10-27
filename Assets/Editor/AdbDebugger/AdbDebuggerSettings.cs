using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CivilRev.AdbDebugger
{
    /// <summary>
    /// ADB 调试工具自己的设置，**独立于指令系统**（不引用它的任何类型）。
    ///
    /// <para>
    /// 本工具只负责"把设备上的 logcat 抓进 Unity 控制台"这一件事，没有端口、没有令牌、
    /// 没有对外服务。想让它被 AI 调用由「Unity MCP 指令系统」的 <c>adb.*</c> 指令负责，
    /// 那是另一份安装，两边可以只装其中一个。
    /// </para>
    ///
    /// <para>
    /// 作用域：日志相关（开关/tag/级别/序列号/回填/堆栈）**按工程隔离**——
    /// 不同工程通常连不同设备；<c>adb.exe</c> 路径是机器事实，**全局共享**。
    /// </para>
    /// </summary>
    public static class AdbDebuggerSettings
    {
        private const string Prefix = "CivilRev.AdbDebug.";

        public const int DefaultBackfill = 20;
        public const int DefaultStackLines = 40;
        public const int MaxStackLines = 500;

        /// <summary>
        /// tag 过滤的默认值。
        /// <para>
        /// <b>为什么是 <c>Unity</c>：</b>Android 上 <c>Debug.Log</c> 走的是 Unity 的日志通道，tag 恒为
        /// <c>Unity</c>（Player 的 stdout 也归到它）。不设过滤时 logcat 会把整机所有 App 的日志一起灌进来，
        /// 一屏里自己的日志只占几行。默认按 Unity 收窄，要全量就把这个框清空。
        /// </para>
        /// </summary>
        public const string DefaultTag = "Unity";

        private const string KeyLogcatEnabled = Prefix + "LogcatEnabled";
        private const string KeyTag = Prefix + "Tag";
        private const string KeyLevel = Prefix + "Level";
        private const string KeySerial = Prefix + "Serial";
        private const string KeyBackfill = Prefix + "Backfill";
        private const string KeyStackLines = Prefix + "StackLines";
        private const string KeySuppressStack = Prefix + "SuppressStack";

        /// <summary>adb 可执行文件路径（**全局**，机器事实）</summary>
        private const string KeyAdbPath = Prefix + "AdbPath";

        private static string _hash;

        /// <summary>本工程专属后缀：EditorPrefs 不区分工程，只能自己加</summary>
        private static string ProjectHash
        {
            get
            {
                if (_hash != null)
                {
                    return _hash;
                }

                try
                {
                    var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                    using (var md5 = MD5.Create())
                    {
                        var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(root.ToLowerInvariant()));
                        var builder = new StringBuilder(8);
                        for (var i = 0; i < 4; i++)
                        {
                            builder.Append(bytes[i].ToString("x2"));
                        }

                        _hash = builder.ToString();
                    }
                }
                catch (Exception)
                {
                    // 拿不到路径时退化为全局键，功能仍可用，只是多工程会互相影响
                    _hash = "unknown";
                }

                return _hash;
            }
        }

        private static string Scoped(string key)
        {
            return key + "." + ProjectHash;
        }

        /// <summary>是否启用 logcat 抓取，默认关闭</summary>
        public static bool LogcatEnabled
        {
            get { return EditorPrefs.GetBool(Scoped(KeyLogcatEnabled), false); }
            set { EditorPrefs.SetBool(Scoped(KeyLogcatEnabled), value); }
        }

        /// <summary>tag 子串过滤（客户端侧，即时生效）；默认 <c>Unity</c>，清空表示不过滤</summary>
        public static string Tag
        {
            get { return EditorPrefs.GetString(Scoped(KeyTag), DefaultTag); }
            set { EditorPrefs.SetString(Scoped(KeyTag), value ?? string.Empty); }
        }

        /// <summary>最低级别 V/D/I/W/E/F/A</summary>
        public static string Level
        {
            get
            {
                var value = EditorPrefs.GetString(Scoped(KeyLevel), "I").Trim().ToUpperInvariant();
                switch (value)
                {
                    case "V":
                    case "D":
                    case "I":
                    case "W":
                    case "E":
                    case "F":
                    case "A":
                        return value;
                    default:
                        return "I";
                }
            }
            set { EditorPrefs.SetString(Scoped(KeyLevel), (value ?? "I").Trim().ToUpperInvariant()); }
        }

        /// <summary>设备序列号；空表示交给 adb 自己挑唯一设备</summary>
        public static string Serial
        {
            get { return EditorPrefs.GetString(Scoped(KeySerial), string.Empty); }
            set { EditorPrefs.SetString(Scoped(KeySerial), value ?? string.Empty); }
        }

        /// <summary>连接时先补多少行历史（<c>-T N</c>）</summary>
        public static int Backfill
        {
            get
            {
                var value = EditorPrefs.GetInt(Scoped(KeyBackfill), DefaultBackfill);
                return value < 0 ? DefaultBackfill : value;
            }
            set { EditorPrefs.SetInt(Scoped(KeyBackfill), value < 0 ? DefaultBackfill : value); }
        }

        /// <summary>一条日志最多并入多少行堆栈续行；0 = 完全不打印调用栈</summary>
        public static int StackLines
        {
            get
            {
                var value = EditorPrefs.GetInt(Scoped(KeyStackLines), DefaultStackLines);
                if (value < 0)
                {
                    return 0;
                }

                return value > MaxStackLines ? MaxStackLines : value;
            }
            set
            {
                var clamped = value < 0 ? 0 : value;
                if (clamped > MaxStackLines)
                {
                    clamped = MaxStackLines;
                }

                EditorPrefs.SetInt(Scoped(KeyStackLines), clamped);
            }
        }

        /// <summary>是否隐藏 [ADB] 日志的 C# 堆栈</summary>
        public static bool SuppressStack
        {
            get { return EditorPrefs.GetBool(Scoped(KeySuppressStack), true); }
            set { EditorPrefs.SetBool(Scoped(KeySuppressStack), value); }
        }

        /// <summary>adb.exe 完整路径；空表示自动定位</summary>
        public static string AdbPath
        {
            get { return EditorPrefs.GetString(KeyAdbPath, string.Empty); }
            set { EditorPrefs.SetString(KeyAdbPath, value ?? string.Empty); }
        }

        /// <summary>清空本工具的全部设置（调试用）</summary>
        public static void ResetAll()
        {
            EditorPrefs.DeleteKey(Scoped(KeyLogcatEnabled));
            EditorPrefs.DeleteKey(Scoped(KeyTag));
            EditorPrefs.DeleteKey(Scoped(KeyLevel));
            EditorPrefs.DeleteKey(Scoped(KeySerial));
            EditorPrefs.DeleteKey(Scoped(KeyBackfill));
            EditorPrefs.DeleteKey(Scoped(KeyStackLines));
            EditorPrefs.DeleteKey(Scoped(KeySuppressStack));
            EditorPrefs.DeleteKey(KeyAdbPath);
        }
    }
}
