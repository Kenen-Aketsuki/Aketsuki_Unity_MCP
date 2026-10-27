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
    /// 这套工具与本工程的「Unity MCP 指令系统」是两个平行工具：共用一个 Unity 工程，
    /// 但各有自己的端口、令牌、开关与信息文件。所以设置前缀、隔离键、默认端口都不与那边共用。
    /// </para>
    ///
    /// <para>
    /// 作用域：日志相关（端口/令牌/开关/tag/级别/序列号/回填/堆栈）**按工程隔离**——
    /// 不同工程通常连不同设备；<c>adb.exe</c> 路径是机器事实，**全局共享**。
    /// </para>
    /// </summary>
    internal static class AdbDebuggerSettings
    {
        private const string Prefix = "CivilRev.AdbDebug.";

        /// <summary>ADB 调试工具的默认端口。刻意与指令系统的 57831 拉开距离，避开它的避让区间。</summary>
        public const int DefaultPort = 57841;

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

        private const string KeyPort = Prefix + "Port";
        private const string KeyToken = Prefix + "Token";
        private const string KeyServerEnabled = Prefix + "ServerEnabled";
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

        /// <summary>本工程的短哈希（信息文件名要用，客户端按它区分工程）</summary>
        internal static string ProjectId
        {
            get { return ProjectHash; }
        }

        #region 服务

        /// <summary>HTTP 服务端口；改完要重启服务</summary>
        public static int Port
        {
            get
            {
                var value = EditorPrefs.GetInt(Scoped(KeyPort), DefaultPort);
                return value <= 0 || value > 65535 ? DefaultPort : value;
            }
            set { EditorPrefs.SetInt(Scoped(KeyPort), value <= 0 || value > 65535 ? DefaultPort : value); }
        }

        /// <summary>访问令牌；空表示下次启动时自动生成并持久化</summary>
        public static string Token
        {
            get { return EditorPrefs.GetString(Scoped(KeyToken), string.Empty); }
            set { EditorPrefs.SetString(Scoped(KeyToken), value ?? string.Empty); }
        }

        /// <summary>编辑器启动时是否自动拉起 HTTP 服务（与「是否抓日志」是两件事）</summary>
        public static bool ServerEnabled
        {
            get { return EditorPrefs.GetBool(Scoped(KeyServerEnabled), true); }
            set { EditorPrefs.SetBool(Scoped(KeyServerEnabled), value); }
        }

        #endregion

        #region 抓取

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

        #endregion

        /// <summary>清空本工具的全部设置（调试用）</summary>
        public static void ResetAll()
        {
            EditorPrefs.DeleteKey(Scoped(KeyPort));
            EditorPrefs.DeleteKey(Scoped(KeyToken));
            EditorPrefs.DeleteKey(Scoped(KeyServerEnabled));
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
