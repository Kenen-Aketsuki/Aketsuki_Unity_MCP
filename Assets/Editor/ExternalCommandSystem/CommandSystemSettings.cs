using UnityEditor;

namespace CivilRev.CommandSystem.Editor
{
    /// <summary>
    /// 指令系统的编辑器设置（持久化在 EditorPrefs，跨 Unity 会话保留）。
    ///
    /// <para>
    /// <b>两类设置，作用域不同：</b>
    /// <list type="bullet">
    /// <item><b>工程相关</b>（指令目录 / HTTP 开关 / 端口 / 令牌 / 额外访问根）——
    /// 键名带 <c>.</c> + 工程哈希后缀，按工程隔离。</item>
    /// <item><b>用户偏好</b>（Verbose / Archive / StopOnError / Watchdog）——保持全局。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// 为什么必须隔离：EditorPrefs 是"本机 + 本 Unity 版本"级别的全局存储，<b>不区分工程</b>。
    /// 早期所有键都是全局的，导致多工程时出现连锁问题：
    /// ① 新工程从"上一个工程用过的端口"起跳，工程间互相捡端口；
    /// ② 所有工程共用同一个 HTTP 令牌；
    /// ③ 在一个工程里开关服务，别的工程下次启动也跟着变。
    /// </para>
    /// </summary>
    public static class CommandSystemSettings
    {
        private const string Prefix = "CivilRev.CommandSystem.";

        private const string KeyWatchDirectory = Prefix + "WatchDirectory";
        private const string KeyVerbose = Prefix + "Verbose";
        private const string KeyArchive = Prefix + "ArchiveAfterRun";
        private const string KeyDeleteAfterRun = Prefix + "DeleteAfterRun";
        private const string KeyStopOnError = Prefix + "StopOnError";
        private const string KeyHttpEnabled = Prefix + "HttpServerEnabled";
        private const string KeyHttpPort = Prefix + "HttpPort";
        private const string KeyHttpToken = Prefix + "HttpToken";
        private const string KeyFileAccessRoots = Prefix + "FileAccessRoots";
        private const string KeyWatchdogSeconds = Prefix + "WatchdogSeconds";
        private const string KeyRuntimeCommandsEnabled = Prefix + "RuntimeCommandsEnabled";
        private const string KeyAdbLogcatEnabled = Prefix + "AdbLogcatEnabled";
        private const string KeyAdbLogcatTag = Prefix + "AdbLogcatTag";
        private const string KeyAdbLogcatLevel = Prefix + "AdbLogcatLevel";
        private const string KeyAdbLogcatSerial = Prefix + "AdbLogcatSerial";
        private const string KeyAdbLogcatBackfill = Prefix + "AdbLogcatBackfill";
        private const string KeyAdbLogcatStackLines = Prefix + "AdbLogcatStackLines";
        private const string KeyAdbSuppressStack = Prefix + "AdbSuppressStack";
        private const string KeyAdbPath = Prefix + "AdbPath";

        /// <summary>记录"全局旧值已被哪个工程继承"，保证迁移只发生一次</summary>
        private const string KeyLegacyOwner = Prefix + "LegacyPrefsOwner";

        private static bool _migrated;

        #region 按工程隔离的键

        /// <summary>
        /// 把全局键名扩展成"本工程专属"键名。
        /// <para>哈希算不出来时退回全局键，保证极端情况下仍能用（退化为旧行为，不会崩）。</para>
        /// </summary>
        private static string Scoped(string baseKey)
        {
            var hash = CommandPathUtil.ProjectHash;
            if (string.IsNullOrEmpty(hash) || hash == "unknown")
            {
                return baseKey;
            }

            return baseKey + "." + hash;
        }

        /// <summary>
        /// 一次性迁移：把老版本的全局值交给"升级后第一个跑起来的工程"继承。
        /// <para>
        /// 只迁移一次的原因：如果每个工程都继承同一份全局值，那等于没隔离——
        /// 尤其是端口和令牌，全部工程会拿到同一个值，问题原样复现。
        /// </para>
        /// </summary>
        private static void MigrateLegacyOnce()
        {
            if (_migrated)
            {
                return;
            }

            _migrated = true;
            if (EditorPrefs.HasKey(KeyLegacyOwner))
            {
                return;
            }

            if (EditorPrefs.HasKey(KeyHttpPort))
            {
                EditorPrefs.SetInt(Scoped(KeyHttpPort),
                    EditorPrefs.GetInt(KeyHttpPort, CommandHttpServer.DefaultPort));
            }

            if (EditorPrefs.HasKey(KeyHttpToken))
            {
                EditorPrefs.SetString(Scoped(KeyHttpToken), EditorPrefs.GetString(KeyHttpToken, string.Empty));
            }

            if (EditorPrefs.HasKey(KeyHttpEnabled))
            {
                EditorPrefs.SetBool(Scoped(KeyHttpEnabled), EditorPrefs.GetBool(KeyHttpEnabled, false));
            }

            if (EditorPrefs.HasKey(KeyWatchDirectory))
            {
                EditorPrefs.SetString(Scoped(KeyWatchDirectory),
                    EditorPrefs.GetString(KeyWatchDirectory, string.Empty));
            }

            if (EditorPrefs.HasKey(KeyFileAccessRoots))
            {
                EditorPrefs.SetString(Scoped(KeyFileAccessRoots),
                    EditorPrefs.GetString(KeyFileAccessRoots, string.Empty));
            }

            EditorPrefs.SetString(KeyLegacyOwner, CommandPathUtil.ProjectHash ?? string.Empty);
        }

        #endregion

        #region 工程相关

        /// <summary>指令目录；为空时使用 项目根/.workbuddy/UnityEditCommand</summary>
        public static string WatchDirectory
        {
            get
            {
                MigrateLegacyOnce();
                return EditorPrefs.GetString(Scoped(KeyWatchDirectory), string.Empty);
            }
            set
            {
                MigrateLegacyOnce();
                EditorPrefs.SetString(Scoped(KeyWatchDirectory), value ?? string.Empty);
            }
        }

        /// <summary>
        /// 是否启用 HTTP 指令服务（默认关闭，**每个工程各管各的**）
        /// <para>开启后会尝试恢复监听；关闭则停止。属于安全敏感项，不做默认开启。</para>
        /// </summary>
        public static bool HttpServerEnabled
        {
            get
            {
                MigrateLegacyOnce();
                return EditorPrefs.GetBool(Scoped(KeyHttpEnabled), false);
            }
            set
            {
                MigrateLegacyOnce();
                EditorPrefs.SetBool(Scoped(KeyHttpEnabled), value);
            }
        }

        /// <summary>HTTP 服务首选端口；被占用时运行期自动向后避让。按工程记忆，互不干扰</summary>
        public static int HttpPort
        {
            get
            {
                MigrateLegacyOnce();
                return EditorPrefs.GetInt(Scoped(KeyHttpPort), CommandHttpServer.DefaultPort);
            }
            set
            {
                MigrateLegacyOnce();
                EditorPrefs.SetInt(Scoped(KeyHttpPort),
                    value <= 0 || value > 65535 ? CommandHttpServer.DefaultPort : value);
            }
        }

        /// <summary>HTTP 访问令牌；为空时启动会自动生成一个并写回此处。**每个工程独立一份**</summary>
        public static string HttpToken
        {
            get
            {
                MigrateLegacyOnce();
                return EditorPrefs.GetString(Scoped(KeyHttpToken), string.Empty);
            }
            set
            {
                MigrateLegacyOnce();
                EditorPrefs.SetString(Scoped(KeyHttpToken), value ?? string.Empty);
            }
        }

        /// <summary>
        /// 额外允许 file.* 读写访问的根目录（绝对路径，分号分隔）。
        /// <para>默认仅放行项目根与工作空间根；需要读写工程外目录时在此显式追加。按工程隔离。</para>
        /// </summary>
        public static string FileAccessRoots
        {
            get
            {
                MigrateLegacyOnce();
                return EditorPrefs.GetString(Scoped(KeyFileAccessRoots), string.Empty);
            }
            set
            {
                MigrateLegacyOnce();
                EditorPrefs.SetString(Scoped(KeyFileAccessRoots), value ?? string.Empty);
            }
        }

        /// <summary>
        /// 是否允许执行运行时（Play 模式）指令，<b>默认关闭</b>，按工程隔离。
        /// <para>
        /// 这类指令会改变正在运行的游戏状态（点按钮、拖列表、写输入框），误调可能写存档或发请求，
        /// 所以默认必须由人在控制台里显式打开；关着时 <see cref="RuntimeCommandGate"/> 会让所有
        /// <c>Runtime = true</c> 的指令直接失败并说明去哪儿打开。
        /// </para>
        /// <para>
        /// 关掉开关<b>不需要</b>退出 Play 模式，下一次执行立即失效（每次执行都实时读这里，不做缓存）。
        /// </para>
        /// </summary>
        public static bool RuntimeCommandsEnabled
        {
            get
            {
                MigrateLegacyOnce();
                return EditorPrefs.GetBool(Scoped(KeyRuntimeCommandsEnabled), false);
            }
            set
            {
                MigrateLegacyOnce();
                EditorPrefs.SetBool(Scoped(KeyRuntimeCommandsEnabled), value);
            }
        }

        #region ADB logcat（按工程隔离；adb 路径是机器级的，放全局）

        /// <summary>回填行数的默认值（<c>-T N</c> 里的 N）</summary>
        public const int DefaultAdbBackfill = 20;

        /// <summary>堆栈最多合并多少行（0 = 丢弃续行，即完全不打印调用栈）</summary>
        public const int DefaultAdbStackLines = 40;

        /// <summary>堆栈合并上限的硬边界（防止手填过大把一条日志撑到几兆）</summary>
        public const int MaxAdbStackLines = 500;

        /// <summary>
        /// 是否启用 ADB logcat 抓取，<b>默认关闭</b>，按工程隔离。
        /// <para>
        /// 这个开关就是"要不要起 adb 进程"的唯一事实来源：控制台勾选框、菜单项、
        /// <c>adb.logcat</c> 指令改的都是它，进程启停由 <see cref="AdbLogcatService"/> 跟随它。
        /// 关掉时立刻杀进程，不留后台抓取。
        /// </para>
        /// <para>
        /// 按工程隔离的理由：不同工程通常连不同的设备/模拟器，序列号与 tag 过滤各不相同，
        /// 共用一个全局开关会互相打断（A 工程关掉，B 工程也跟着停）。
        /// </para>
        /// </summary>
        public static bool AdbLogcatEnabled
        {
            get
            {
                MigrateLegacyOnce();
                return EditorPrefs.GetBool(Scoped(KeyAdbLogcatEnabled), false);
            }
            set
            {
                MigrateLegacyOnce();
                EditorPrefs.SetBool(Scoped(KeyAdbLogcatEnabled), value);
            }
        }

        /// <summary>tag 过滤（子串、大小写不敏感）；空串表示不过滤。改完即时生效，无需重启抓取</summary>
        public static string AdbLogcatTag
        {
            get
            {
                MigrateLegacyOnce();
                return EditorPrefs.GetString(Scoped(KeyAdbLogcatTag), string.Empty);
            }
            set
            {
                MigrateLegacyOnce();
                EditorPrefs.SetString(Scoped(KeyAdbLogcatTag), value ?? string.Empty);
            }
        }

        /// <summary>最低级别（V/D/I/W/E/F，默认 I）。改完即时生效</summary>
        public static string AdbLogcatLevel
        {
            get
            {
                MigrateLegacyOnce();
                var value = EditorPrefs.GetString(Scoped(KeyAdbLogcatLevel), "I").Trim().ToUpperInvariant();
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
            set
            {
                MigrateLegacyOnce();
                EditorPrefs.SetString(Scoped(KeyAdbLogcatLevel), (value ?? "I").Trim().ToUpperInvariant());
            }
        }

        /// <summary>设备序列号（如 <c>127.0.0.1:7555</c>）；空串表示交给 adb 自己挑唯一设备。改完需重启抓取</summary>
        public static string AdbLogcatSerial
        {
            get
            {
                MigrateLegacyOnce();
                return EditorPrefs.GetString(Scoped(KeyAdbLogcatSerial), string.Empty);
            }
            set
            {
                MigrateLegacyOnce();
                EditorPrefs.SetString(Scoped(KeyAdbLogcatSerial), value ?? string.Empty);
            }
        }

        /// <summary>首次连接时回溯多少行历史（<c>-T N</c>，默认 20）。0 表示不回填——会从缓冲区开头倒，慎用</summary>
        public static int AdbLogcatBackfill
        {
            get
            {
                MigrateLegacyOnce();
                var value = EditorPrefs.GetInt(Scoped(KeyAdbLogcatBackfill), DefaultAdbBackfill);
                return value < 0 ? DefaultAdbBackfill : value;
            }
            set
            {
                MigrateLegacyOnce();
                EditorPrefs.SetInt(Scoped(KeyAdbLogcatBackfill), value < 0 ? DefaultAdbBackfill : value);
            }
        }

        /// <summary>
        /// 一条 logcat 日志最多把多少行**堆栈续行**合并进同一条 Unity 日志。
        ///
        /// <para>
        /// <b>0 = 丢弃续行</b>，也就是完全不打印调用栈，只要首行内容；
        /// <b>&gt;0 = 合并</b>（推荐，默认 40）：Java 的 <c>at com.x.Y.z(...)</c>、
        /// <c>Caused by:</c>、Unity 的 <c>  at Foo.Bar ()</c> 都是一行一条走进 logcat 的，
        /// 不合并就会把一次异常拆成几十条独立日志、每条还自带 C# 堆栈——控制台全是噪声。
        /// </para>
        /// </summary>
        public static int AdbLogcatStackLines
        {
            get
            {
                MigrateLegacyOnce();
                var value = EditorPrefs.GetInt(Scoped(KeyAdbLogcatStackLines), DefaultAdbStackLines);
                if (value < 0)
                {
                    return 0;
                }

                return value > MaxAdbStackLines ? MaxAdbStackLines : value;
            }
            set
            {
                MigrateLegacyOnce();
                var clamped = value < 0 ? 0 : value;
                if (clamped > MaxAdbStackLines)
                {
                    clamped = MaxAdbStackLines;
                }

                EditorPrefs.SetInt(Scoped(KeyAdbLogcatStackLines), clamped);
            }
        }

        /// <summary>
        /// 是否关掉 <c>[ADB]</c> 日志的 C# 堆栈（默认<b>开</b>）。
        ///
        /// <para>
        /// <c>Debug.Log</c> 会自动附一段调用栈，而每条 ADB 日志的堆栈都指向 <c>AdbLogcatService.Emit</c>
        /// 同一个位置——纯噪声。这里在打印期间把 <c>Application.SetStackTraceLogType</c> 临时设成
        /// <c>None</c>，打完按窗口还原，用户原本的全局设置不会丢。
        /// </para>
        /// </summary>
        public static bool AdbSuppressStack
        {
            get
            {
                MigrateLegacyOnce();
                return EditorPrefs.GetBool(Scoped(KeyAdbSuppressStack), true);
            }
            set
            {
                MigrateLegacyOnce();
                EditorPrefs.SetBool(Scoped(KeyAdbSuppressStack), value);
            }
        }

        /// <summary>
        /// adb 可执行文件路径（**全局**，不按工程隔离）。
        /// <para>理由：adb 装在哪是机器事实，跟工程无关；同一台机器上所有工程共用一份 adb。</para>
        /// <para>留空表示自动定位（环境变量 / Unity 的 AndroidSdkRoot / 编辑器自带 SDK / PATH）。</para>
        /// </summary>
        public static string AdbPath
        {
            get { return EditorPrefs.GetString(KeyAdbPath, string.Empty); }
            set { EditorPrefs.SetString(KeyAdbPath, value ?? string.Empty); }
        }

        #endregion

        /// <summary>取得实际生效的指令目录（未配置时返回默认值）</summary>
        public static string EffectiveWatchDirectory
        {            get
            {
                var dir = WatchDirectory;
                return string.IsNullOrWhiteSpace(dir) ? CommandPathUtil.DefaultCommandDirectory : dir.Trim();
            }
        }

        #endregion

        #region 用户偏好（全局）

        /// <summary>自动执行时是否输出逐条日志</summary>
        public static bool Verbose
        {
            get { return EditorPrefs.GetBool(KeyVerbose, true); }
            set { EditorPrefs.SetBool(KeyVerbose, value); }
        }

        /// <summary>执行后是否把文件移动到 _done 子目录（默认关闭）</summary>
        public static bool ArchiveAfterRun
        {
            get { return EditorPrefs.GetBool(KeyArchive, false); }
            set { EditorPrefs.SetBool(KeyArchive, value); }
        }

        /// <summary>
        /// 执行后是否直接删除已执行的指令文件（默认关闭）。
        /// <para>与 <see cref="ArchiveAfterRun"/> 互斥：本项优先——开启时执行成功即删除原文件，不再归档。
        /// 用于"指令文件越攒越多"的清理诉求（见 command.cleanup 也可手动批量清理）。</para>
        /// </summary>
        public static bool DeleteAfterRun
        {
            get { return EditorPrefs.GetBool(KeyDeleteAfterRun, false); }
            set { EditorPrefs.SetBool(KeyDeleteAfterRun, value); }
        }

        /// <summary>遇到失败是否中止后续指令</summary>
        public static bool StopOnError
        {
            get { return EditorPrefs.GetBool(KeyStopOnError, true); }
            set { EditorPrefs.SetBool(KeyStopOnError, value); }
        }

        /// <summary>
        /// 单条指令的软超时阈值（秒，默认 120）；执行结束后超过该值记 warning。
        /// <para>0 表示关闭检查。注意这是"事后可见"，无法中断已卡住的同步指令。</para>
        /// </summary>
        public static int WatchdogSeconds
        {
            get
            {
                var value = EditorPrefs.GetInt(KeyWatchdogSeconds, CommandRunner.DefaultWatchdogSeconds);
                return value < 0 ? CommandRunner.DefaultWatchdogSeconds : value;
            }
            set { EditorPrefs.SetInt(KeyWatchdogSeconds, value < 0 ? CommandRunner.DefaultWatchdogSeconds : value); }
        }

        #endregion

        /// <summary>恢复默认设置（只清本工程的那份 + 全局偏好）</summary>
        public static void Reset()
        {
            EditorPrefs.DeleteKey(Scoped(KeyWatchDirectory));
            EditorPrefs.DeleteKey(Scoped(KeyHttpEnabled));
            EditorPrefs.DeleteKey(Scoped(KeyHttpPort));
            EditorPrefs.DeleteKey(Scoped(KeyHttpToken));
            EditorPrefs.DeleteKey(Scoped(KeyFileAccessRoots));
            EditorPrefs.DeleteKey(Scoped(KeyRuntimeCommandsEnabled));
            EditorPrefs.DeleteKey(Scoped(KeyAdbLogcatEnabled));
            EditorPrefs.DeleteKey(Scoped(KeyAdbLogcatTag));
            EditorPrefs.DeleteKey(Scoped(KeyAdbLogcatLevel));
            EditorPrefs.DeleteKey(Scoped(KeyAdbLogcatSerial));
            EditorPrefs.DeleteKey(Scoped(KeyAdbLogcatBackfill));
            EditorPrefs.DeleteKey(Scoped(KeyAdbLogcatStackLines));
            EditorPrefs.DeleteKey(Scoped(KeyAdbSuppressStack));
            EditorPrefs.DeleteKey(KeyAdbPath);

            EditorPrefs.DeleteKey(KeyVerbose);
            EditorPrefs.DeleteKey(KeyArchive);
            EditorPrefs.DeleteKey(KeyDeleteAfterRun);
            EditorPrefs.DeleteKey(KeyStopOnError);
            EditorPrefs.DeleteKey(KeyWatchdogSeconds);
        }
    }
}
