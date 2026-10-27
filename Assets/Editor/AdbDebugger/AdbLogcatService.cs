using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CivilRev.AdbDebugger
{
    /// <summary>
    /// 一条已经合并好、打进过控制台的 logcat 日志。
    /// <para>
    /// <b>为什么要额外留一份：</b>控制台是给人看的，AI 读它要走 <c>console.read</c> 再按
    /// <c>[ADB]</c> 前缀筛，而且控制台有滚动上限、一刷就找不到。指令系统的
    /// <c>adb.logcat.read</c> 直接取这份缓冲，拿到的是"合并后的整条日志 + 级别 + tag"。
    /// </para>
    /// </summary>
    public sealed class AdbCapturedLine
    {
        public DateTime At;
        public string Level = "I";
        public string Tag = string.Empty;

        /// <summary>合并后的正文（含堆栈，不含 <c>[ADB] </c> 前缀）</summary>
        public string Text = string.Empty;
    }

    /// <summary>
    /// ADB logcat 抓取服务：把 Android 真机 / 模拟器的日志**逐条**打进 Unity 控制台。
    ///
    /// <para>
    /// <b>开关默认关闭，按工程保存</b>（<see cref="AdbDebuggerSettings.LogcatEnabled"/>）。
    /// 关着的时候一个进程都不起；打开才去拉起 <c>adb logcat</c>。
    /// 开关状态就是"要不要起进程"这一个事实的唯一来源，窗口、菜单、指令系统的
    /// <c>adb.logcat.start</c> / <c>adb.logcat.stop</c> 改的都是它，没有第二份状态。
    /// </para>
    ///
    /// <para>
    /// <b>为什么不用 <c>adb logcat -d</c> 轮询：</b><c>-d</c> 是"打印完就退出"，要持续输出就得反复拉进程、
    /// 还会重复打印同一批行。<c>-T N</c> 才是正解：先补 N 行历史，然后**保持连接继续跟随**，
    /// 与终端的 <c>adb logcat</c> 行为一致（<c>-t N</c> 则会隐式 <c>-d</c> 立刻退出）。
    /// </para>
    ///
    /// <para>
    /// <b>打印走"读线程 → 队列 → 主线程每帧取一批"</b>，理由有两条：
    /// ① stdout 回调跑在线程池线程上，而 <c>Debug.Log</c> 只能在主线程用；
    /// ② logcat 是洪流（开机后一次性几万行很常见），在回调里直连 Unity 控制台会直接把编辑器卡死。
    /// 每帧上限 <see cref="MaxLinesPerFrame"/> 条，超出排队；队列超过 <see cref="MaxQueuedLines"/>
    /// 直接丢弃并计数，保护内存而不是无限堆积。
    /// </para>
    ///
    /// <para>
    /// <b>进程收尾必须显式做：</b>域重载 / 退出编辑器时 .NET 侧的 <see cref="Process"/> 对象会消失，
    /// 但子进程不一定跟着死——不杀就会留下一个对空管道写日志的孤儿 adb 进程，
    /// 用户下次开开关时可能吸到旧进程的输出。所以 <c>beforeAssemblyReload</c> 与 <c>quitting</c>
    /// 都走一次 <see cref="StopInternal"/>（不清开关，重载后自动恢复）。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class AdbLogcatService
    {
        /// <summary>每帧最多打印多少条：翻一屏 20 条够看，又不至于在洪流里卡住编辑器</summary>
        private const int MaxLinesPerFrame = 20;

        /// <summary>
        /// 每帧最多**扫描**多少行原始输入。
        /// <para>
        /// 要和 <see cref="MaxLinesPerFrame"/> 分开：合并之后 40 行堆栈只产出 1 条日志，
        /// 若仍按"输入即配额"算，一帧就只能吃 20 行，真机洪流下队列永远追不上、成片丢日志。
        /// </para>
        /// </summary>
        private const int MaxScannedPerFrame = 200;

        /// <summary>队列水位：超过就丢新行并计数，避免断点/后台时把内存吃光</summary>
        private const int MaxQueuedLines = 5000;

        /// <summary>
        /// 给 <c>adb.logcat.read</c> 留的日志环形缓冲容量。
        /// <para>
        /// 2000 条够覆盖"AI 一小段时间没来取"的场景，又不会因为没人取而无限吃内存。
        /// 真机洪流下一分钟就能刷满，所以溢出时丢最旧的并计数，不丢新的——
        /// 排查问题要的是"最近发生了什么"。
        /// </para>
        /// </summary>
        private const int MaxCapturedLines = 2000;

        /// <summary>单条消息上限，超长截断（logcat 里偶有整段 JSON/堆栈）</summary>
        private const int MaxMessageChars = 2000;

        /// <summary>
        /// 等这么久没有续行进来，就认为"这一条日志已经打完"。
        /// <para>
        /// 合并必须靠"下一条非续行"或超时来收尾：一条以堆栈结尾的日志后面可能再没有新行，
        /// 不设超时它会永远挂在 <see cref="_pending"/> 里不出现。
        /// </para>
        /// </summary>
        private const int FlushIdleMs = 100;

        /// <summary>
        /// 抑制 C# 堆栈的冷却窗口：窗口内不再重复写 EditorPrefs。
        /// <para>
        /// <c>Application.SetStackTraceLogType</c> 会落到编辑器偏好（Windows 上是注册表），
        /// 每条日志都切一次在真机洪流下就是每秒几百次写盘。改为"进入抑制 → 500ms 后才考虑恢复"，
        /// 稳态下每秒最多两次切换。
        /// </para>
        /// </summary>
        private const int StackSuppressWindowMs = 500;

        /// <summary>Unity 控制台里的统一前缀，配合 <c>console.read</c> 的 <c>contains</c> 精确筛</summary>
        public const string Prefix = "[ADB] ";

        private const string StateStopped = "stopped";
        private const string StateRunning = "running";
        private const string StateFailed = "failed";

        /// <summary>
        /// 解析 <c>adb logcat -v threadtime</c> 的一行：
        /// <c>10-27 15:35:28.620  2745  2895 W BestClock: message</c>
        /// <para>
        /// 用正则而不是 Split：tag 里出现过空格，连续多行消息（续行）也压根不匹配。
        /// 不匹配的行不当错误，按原始文本打成 Info——宁可多打一行，也不吞掉真日志。
        /// </para>
        /// </summary>
        private static readonly Regex ThreadTimeLine = new Regex(
            @"^(?<date>\d{2}-\d{2})\s+(?<time>\d{2}:\d{2}:\d{2}\.\d{3})\s+" +
            @"(?<pid>\d+)\s+(?<tid>\d+)\s+(?<level>[VDIWEFA])\s+(?<tag>.*?)\s*:\s?(?<message>.*)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Unity 在 Android 上打印的 C# 栈帧：<c>Type:Method()</c> / <c>Namespace.Type:Method(System.String)</c>。
        /// <para>
        /// <b>为什么要单独认这一种：</b>设备端 player 的 managed 栈既没有 <c>at </c> 前缀、也不带文件行号，
        /// 只用冒号把类型和方法连起来。逐行抓 logcat 时它和普通正文长得一模一样，
        /// 只按 <c>at </c> / <c>Caused by:</c> 判定必然漏——一条 <c>Debug.Log</c> 会被拆成「正文 + N 条栈帧」。
        /// </para>
        /// <para>
        /// 保守取舍：类型名不许空白、方法名不许空白，于是 <c>Note: see foo()</c>、<c>Time: 12:30 (x)</c>
        /// 这类正文不会被误判；代价是「用户自己打了一条恰好形如 <c>Foo:Bar()</c> 的正文、且紧跟上一条」
        /// 会被并进去（内容不丢，展开仍可见）。
        /// </para>
        /// </summary>
        private static readonly Regex UnityStackFrame = new Regex(
            @"^[A-Za-z_][A-Za-z0-9_.`<>+]*\s*:\s*[A-Za-z_][A-Za-z0-9_<>]*\s*\([^()]*\)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly ConcurrentQueue<string> Lines = new ConcurrentQueue<string>();
        private static readonly StringBuilder Stderr = new StringBuilder();
        private static readonly object StderrGate = new object();

        /// <summary>已输出的日志（给指令读）。只在主线程读写，加锁是为了"取的时候"能安全快照</summary>
        private static readonly Queue<AdbCapturedLine> Captured = new Queue<AdbCapturedLine>();
        private static readonly object CapturedGate = new object();
        private static long _capturedDropped;

        private static Process _process;

        /// <summary>每次启停自增，用来让"上一轮"迟到的 Exited 回调失效</summary>
        private static volatile int _runId;

        /// <summary>子进程已退出（线程池置位，主线程消费）</summary>
        private static volatile bool _exited;

        /// <summary>子进程退出码（线程池写，主线程读，配合 <see cref="_exited"/> 的 happens-before）</summary>
        private static int _exitCode;

        private static string _state = StateStopped;
        private static string _adbPath = string.Empty;
        private static string _adbSource = string.Empty;
        private static string _lastError = string.Empty;
        private static string _lastCommand = string.Empty;
        private static DateTime _startedAt;

        /// <summary>
        /// adb 自己吐在 stderr 上的"当前卡在哪"（典型：<c>- waiting for device -</c>）。
        /// <para>
        /// 这是实测才知道的一件事：设备不在时 <c>adb logcat</c> **不会退出**，而是挂着等设备，
        /// 只在 stderr 打一行 <c>- waiting for device -</c>。只看"进程还在跑"会以为一切正常，
        /// 实际上一条日志都不会来。所以这行必须浮到状态里。
        /// </para>
        /// </summary>
        private static string _notice = string.Empty;

        /// <summary>有新 notice 待主线程播报（stderr 回调在别的线程，不能直接写控制台窗口的回调）</summary>
        private static volatile bool _noticePending;

        private static string _tagFilter = string.Empty;
        private static int _minLevelRank = 2; // I

        /// <summary>
        /// <see cref="AdbDebuggerSettings.StackLines"/> 的缓存。
        /// <para>
        /// 热路径（每行都要判定）绝不能直接读设置：getter 背后是 <c>EditorPrefs.GetInt</c>，
        /// 真机洪流下就是每秒几百次读注册表。改设置后由 <see cref="RefreshFilters"/> 刷新。
        /// </para>
        /// </summary>
        private static int _stackLines = AdbDebuggerSettings.DefaultStackLines;

        /// <summary><see cref="AdbDebuggerSettings.SuppressStack"/> 的缓存，同 <see cref="_stackLines"/></summary>
        private static bool _suppressStack = true;

        /// <summary>老 adb 不认 <c>-T</c> 时的一次性降级标记</summary>
        private static bool _legacyRetry;

        private static long _received;
        private static long _printed;
        private static long _filtered;
        private static long _dropped;
        private static long _coalesced;

        // ---------------------------------------------------------------- 多行合并

        /// <summary>
        /// 正在攒的那一条（首行 + 已并入的续行，用 <c>\n</c> 连接）。
        /// <para>
        /// <b>为什么需要这个：</b>logcat 里一次 Java 异常是十几到上百行，每行都带完整前缀、
        /// 都算一条独立记录；逐行 <c>Debug.Log</c> 会把一次崩溃刷成几十条控制台条目，
        /// 每条还各自附带一段 C# 堆栈。合并后它只是**一条**日志，在控制台里占一行（展开才看全）。
        /// </para>
        /// </summary>
        private static string _pending;

        /// <summary>这一条用哪个级别输出（由首行决定，续行不改变它）</summary>
        private static string _pendingLevel;

        /// <summary>这一条的 logcat tag（由首行决定），只用于留给指令读的那份缓冲</summary>
        private static string _pendingTag = string.Empty;

        private static DateTime _pendingAt;

        /// <summary>已并入的续行数</summary>
        private static int _pendingLines;

        /// <summary>已因超限截断，后续续行直接丢</summary>
        private static bool _pendingTruncated;

        // ---------------------------------------------------------------- C# 堆栈抑制

        /// <summary>当前是否处于"已把堆栈类型切到 None"的状态</summary>
        private static bool _stackSuppressed;

        /// <summary>抑制窗口的到期时刻；在此之前不重复写 EditorPrefs</summary>
        private static DateTime _suppressUntil;

        /// <summary>切换前用户的堆栈设置，恢复时原样写回</summary>
        private static bool _stackSettingsSaved;
        private static StackTraceLogType _savedLogStack;
        private static StackTraceLogType _savedWarningStack;
        private static StackTraceLogType _savedErrorStack;


        static AdbLogcatService()
        {
            // 资源导入工作进程 / batchmode 也会执行静态构造，但它们没有主循环：
            // 在那里起 adb 会得到一个没人消费输出的孤儿进程。
            // 这里不询问指令系统的 HttpIdentity，只看 Unity 自己的 batchmode 标志。
            if (Application.isBatchMode)
            {
                return;
            }

            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnQuitting;

            // 域重载后按设置恢复：开关开着就重新连上（否则每次改完 .cs 都得手点一次）
            RestoreFromSettings();
        }

        #region 对外状态

        /// <summary>当前是否在抓日志（进程存活且没收到退出信号）</summary>
        public static bool IsRunning
        {
            get { return _process != null && !_exited; }
        }

        /// <summary>stopped / running / failed</summary>
        public static string State
        {
            get { return _state; }
        }

        /// <summary>实际使用的 adb 路径（未启动时为空）</summary>
        public static string AdbPath
        {
            get { return _adbPath; }
        }

        /// <summary>adb 是怎么找到的（PATH / 环境变量 / 编辑器自带 SDK …）</summary>
        public static string AdbSource
        {
            get { return _adbSource; }
        }

        /// <summary>最近一次错误；成功后清空</summary>
        public static string LastError
        {
            get { return _lastError; }
        }

        /// <summary>从 adb 收到的行数（含被过滤、被丢弃的）</summary>
        public static long ReceivedCount
        {
            get { return Interlocked.Read(ref _received); }
        }

        /// <summary>真正打进 Unity 控制台的行数</summary>
        public static long PrintedCount
        {
            get { return Interlocked.Read(ref _printed); }
        }

        /// <summary>被 tag / 级别过滤掉的行数</summary>
        public static long FilteredCount
        {
            get { return Interlocked.Read(ref _filtered); }
        }

        /// <summary>队列溢出被丢弃的行数（超过水位说明编辑器和 logcat 速率差太多）</summary>
        public static long DroppedCount
        {
            get { return Interlocked.Read(ref _dropped); }
        }

        /// <summary>本次启动时实际用的命令行（排查参数问题用）</summary>
        public static string LastCommand
        {
            get { return _lastCommand; }
        }

        /// <summary>给控制台用的一行摘要</summary>
        public static string SummaryText
        {
            get
            {
                if (!IsRunning)
                {
                    return _state == StateFailed
                        ? "未运行（上次失败：" + _lastError + "）"
                        : "未运行";
                }

                var seconds = Math.Max(0, (int)(DateTime.Now - _startedAt).TotalSeconds);
                var text = string.Format("已连接 {0}（{1}s）· 收到 {2} / 打印 {3} 条 / 合并 {4} / 过滤 {5} / 丢弃 {6}",
                    string.IsNullOrEmpty(_adbSource) ? "adb" : _adbSource,
                    seconds, ReceivedCount, PrintedCount, Interlocked.Read(ref _coalesced),
                    FilteredCount, DroppedCount);
                if (!string.IsNullOrEmpty(_notice))
                {
                    text += " · adb：" + _notice;
                }

                return text;
            }
        }

        /// <summary>统计归零（只看计数，不影响抓取本身）</summary>
        public static void ResetCounters()
        {
            Interlocked.Exchange(ref _received, 0);
            Interlocked.Exchange(ref _printed, 0);
            Interlocked.Exchange(ref _filtered, 0);
            Interlocked.Exchange(ref _dropped, 0);
            Interlocked.Exchange(ref _coalesced, 0);
            Interlocked.Exchange(ref _capturedDropped, 0);
        }

        /// <summary>缓冲里还有多少条没被取走</summary>
        public static int CapturedCount
        {
            get
            {
                lock (CapturedGate)
                {
                    return Captured.Count;
                }
            }
        }

        /// <summary>因缓冲满而被丢掉的旧日志条数（说明有人很久没取，或日志量远超消费速度）</summary>
        public static long CapturedDroppedCount
        {
            get { return Interlocked.Read(ref _capturedDropped); }
        }

        /// <summary>
        /// 取走最多 <paramref name="max"/> 条已输出日志（取走即从缓冲删除）。
        /// <para>
        /// "取走"而不是"再读一遍"：指令会被反复调用，重复返回同一批日志会让模型以为
        /// 又刷了一屏错误。要重新看历史就调大 <paramref name="max"/> 或先别取。
        /// </para>
        /// </summary>
        public static List<AdbCapturedLine> TakeCaptured(int max)
        {
            if (max <= 0)
            {
                max = 200;
            }

            var result = new List<AdbCapturedLine>();
            lock (CapturedGate)
            {
                while (result.Count < max && Captured.Count > 0)
                {
                    result.Add(Captured.Dequeue());
                }
            }

            return result;
        }

        /// <summary>被并进前一条日志的堆栈续行数</summary>
        public static long CoalescedCount
        {
            get { return Interlocked.Read(ref _coalesced); }
        }

        /// <summary>adb 自己吐在 stderr 上"当前卡在哪"（典型：<c>- waiting for device -</c>）</summary>
        public static string Notice
        {
            get { return _notice; }
        }

        #endregion

        #region 启停

        /// <summary>按设置恢复：开关开着才拉进程（域重载 / 编辑器启动时走这里）</summary>
        public static void RestoreFromSettings()
        {
            if (!AdbDebuggerSettings.LogcatEnabled)
            {
                return;
            }

            if (!Start(out var error) && !string.IsNullOrEmpty(error))
            {
                AdbLog.Error(error);
            }
        }

        /// <summary>重新读取 tag / 级别 / 堆栈合并设置（改设置后必须调，否则要重启才生效）</summary>
        public static void RefreshFilters()
        {
            _tagFilter = (AdbDebuggerSettings.Tag ?? string.Empty).Trim();
            _minLevelRank = RankOf(AdbDebuggerSettings.Level);
            _stackLines = AdbDebuggerSettings.StackLines;

            var suppress = AdbDebuggerSettings.SuppressStack;
            if (_suppressStack && !suppress)
            {
                // 刚被关掉：立刻还原，不要让用户等到下一个抑制窗口到期
                RestoreStackTraceNow();
            }

            _suppressStack = suppress;
        }

        /// <summary>
        /// 启动抓取。成功会把 <see cref="AdbDebuggerSettings.LogcatEnabled"/> 置为 true，
        /// 让开关与"真的有进程在跑"保持一致。
        /// </summary>
        /// <param name="error">失败原因（已含怎么修）；成功时为 null</param>
        public static bool Start(out string error)
        {
            error = null;

            if (IsRunning)
            {
                return true;
            }

            // 清掉上一轮可能残留的进程与半截状态
            StopInternal(false, null, keepFilters: true);

            var adb = AdbLocator.Resolve(out var source);
            if (string.IsNullOrEmpty(adb))
            {
                error = "找不到 adb：" + source +
                        "。请在「Tools / ADB 调试 / ADB 调试台」里手填 adb.exe 的完整路径" +
                        "（通常在 <Android SDK>/platform-tools/adb.exe）。";
                _state = StateFailed;
                _lastError = error;
                return false;
            }

            RefreshFilters();

            var arguments = BuildArguments();
            var startInfo = new ProcessStartInfo
            {
                FileName = adb,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var runId = ++_runId;
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (sender, args) => Enqueue(args.Data);
            process.ErrorDataReceived += (sender, args) => CaptureStderr(args.Data);
            process.Exited += (sender, args) => OnExited(runId, process);

            // 必须在 Start 之前复位：设备没连时 adb 会在几十毫秒内退出，
            // 放在 Start 之后复位会把已经收到的退出信号抹掉（开关看着开着、其实没在抓）
            _exited = false;
            _exitCode = 0;
            ClearStderr();
            DropQueued();
            ClearCaptured();
            _notice = string.Empty;
            _noticePending = false;

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception exception)
            {
                error = "启动 adb logcat 失败：" + exception.Message + "（adb=" + adb + "）";
                _state = StateFailed;
                _lastError = error;
                try
                {
                    process.Dispose();
                }
                catch (Exception)
                {
                    // 释放失败无所谓，进程压根没起来
                }

                return false;
            }

            _process = process;
            _adbPath = adb;
            _adbSource = source;
            _lastCommand = "adb " + arguments;
            _state = StateRunning;
            _startedAt = DateTime.Now;
            _lastError = string.Empty;

            AdbDebuggerSettings.LogcatEnabled = true;
            AdbLog.Info("已启动：" + _lastCommand + "（adb 来源：" + source + "）");
            return true;
        }

        /// <summary>停止抓取。</summary>
        /// <param name="clearSetting">是否把开关也关掉。域重载 / 退出编辑器时传 false，重载后自动恢复</param>
        public static void Stop(bool clearSetting = true)
        {
            StopInternal(clearSetting, "adb logcat 已停止", keepFilters: true);
        }

        private static void StopInternal(bool clearSetting, string reason, bool keepFilters)
        {
            // 自增让在途的 Exited 回调失效，否则"主动停止"会被当成"意外退出"报一遍错误
            _runId++;
            _exited = false;
            _exitCode = 0;

            var process = _process;
            _process = null;
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch (Exception)
                {
                    // 进程可能刚好自己退了，Kill 抛异常可以忽略
                }

                try
                {
                    process.Dispose();
                }
                catch (Exception)
                {
                    // 同上
                }
            }

            _state = StateStopped;
            ClearStderr();
            DropQueued();

            // 没打完的半条直接丢（停止后不该再冒出来一条陈年堆栈）
            _pending = null;
            _pendingTruncated = false;

            // 停止就必须把用户的堆栈设置还原，否则会一直停留在"无堆栈"状态
            RestoreStackTraceNow();

            _notice = string.Empty;
            _noticePending = false;

            if (!keepFilters)
            {
                _tagFilter = string.Empty;
                _minLevelRank = 2;
            }

            if (clearSetting)
            {
                AdbDebuggerSettings.LogcatEnabled = false;
            }

            if (!string.IsNullOrEmpty(reason))
            {
                AdbLog.Info(reason);
            }
        }

        /// <summary><c>adb [-s serial] logcat -v threadtime [-T n]</c></summary>
        private static string BuildArguments()
        {
            var builder = new StringBuilder();

            var serial = (AdbDebuggerSettings.Serial ?? string.Empty).Trim();
            if (serial.Length > 0)
            {
                // 序列号形如 127.0.0.1:7555，含冒号，加引号避免被当成分隔符
                builder.Append("-s \"").Append(serial).Append("\" ");
            }

            builder.Append("logcat -v threadtime");

            // -T N：先补 N 行历史再跟随。0 会退化成"整个缓冲区倒出来"（可能上万行），
            // 所以 <=0 时不传这个参数交给用户自己决定，并在控制台里写明后果
            var backfill = AdbDebuggerSettings.Backfill;
            if (backfill > 0 && !_legacyRetry)
            {
                builder.Append(" -T ").Append(backfill);
            }

            return builder.ToString();
        }

        #endregion

        #region 主线程泵

        private static void Tick()
        {
            if (_noticePending)
            {
                _noticePending = false;
                AdbLog.Warning("adb 卡住了：" + _notice +
                                   "。设备没连上或序列号填错时 adb 不会退出，会一直等——所以这里一条日志也不会有。");
            }

            // 有活才碰堆栈设置：完全静止时不写 EditorPrefs
            if (!Lines.IsEmpty || _pending != null)
            {
                BeginStackTraceSuppression();
                try
                {
                    Drain();
                    FlushIfIdle();
                }
                finally
                {
                    ReleaseStackTraceSuppression();
                }
            }

            if (_exited)
            {
                HandleExit();
            }
        }

        /// <summary>每帧定量取：输出条数与扫描行数各有一道闸，超出留到下一帧</summary>
        private static void Drain()
        {
            var printedAtStart = PrintedCount;
            var scanned = 0;

            while (scanned < MaxScannedPerFrame && PrintedCount - printedAtStart < MaxLinesPerFrame)
            {
                if (!Lines.TryDequeue(out var raw))
                {
                    return;
                }

                scanned++;
                Emit(raw);
            }
        }

        /// <summary>
        /// 把一行 logcat 归并成 Unity 控制台的一条日志。
        /// <para>
        /// 分两段：普通行**启一条新的**，堆栈续行**并进当前这条**（<see cref="ClassifyContinuation"/>）。
        /// 过滤只作用在普通行上——续行跟着首行走，否则一次异常会被滤掉半截、剩下的堆栈反而看不懂。
        /// </para>
        /// </summary>
        private static void Emit(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            Interlocked.Increment(ref _received);

            // 真的收到行了就说明设备通了，"等设备"提示该撤了（哪怕这行随后被过滤掉）
            if (_notice.Length > 0)
            {
                _notice = string.Empty;
            }

            var line = raw.TrimEnd();

            // 缓冲区起点标记：--------- beginning of main / system / crash
            if (line.StartsWith("---------", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _filtered);
                return;
            }

            var level = "I";
            var tag = string.Empty;
            var message = line;
            var match = ThreadTimeLine.Match(line);
            if (match.Success)
            {
                level = match.Groups["level"].Value;
                tag = match.Groups["tag"].Value.Trim();
                message = match.Groups["message"].Value;
            }

            var continuation = ClassifyContinuation(message);

            // 栈尾的空行：既不能收尾上一条（会把正文和栈劈成两条），也不能自己成条
            // （否则控制台里多出一条什么都不带的 [ADB]）。直接丢，计进 filtered。
            if (continuation == ContinuationKind.Blank)
            {
                Interlocked.Increment(ref _filtered);
                return;
            }

            // StackLine 没归属（首行被 tag/级别滤掉）就丢弃：孤立栈行没有信息量。
            // UnityFrame 与普通正文同形，没归属时宁可当日志打出来，也不吞掉可能是用户日志的那行。
            if (continuation == ContinuationKind.StackLine ||
                (continuation == ContinuationKind.UnityFrame && _pending != null))
            {
                AppendContinuation(message);
                return;
            }

            // 新的一条开始：先把上一条收尾
            FlushPending();

            if (RankOf(level) < _minLevelRank)
            {
                Interlocked.Increment(ref _filtered);
                return;
            }

            if (_tagFilter.Length > 0 && tag.IndexOf(_tagFilter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                Interlocked.Increment(ref _filtered);
                return;
            }

            _pending = message;
            _pendingLevel = level;
            _pendingTag = tag;
            _pendingAt = DateTime.Now;
            _pendingLines = 0;
            _pendingTruncated = false;
        }

        /// <summary>
        /// 把一条堆栈续行并进当前日志。
        /// <para>
        /// 三种情况直接丢：① 当前没有可归属的首行（首行被过滤掉或还没到）；
        /// ② <see cref="AdbDebuggerSettings.StackLines"/> 为 0（用户选择完全不打印调用栈）；
        /// ③ 已达合并上限。丢弃计进 <c>filtered</c>，不冒充"打印"。
        /// </para>
        /// </summary>
        private static void AppendContinuation(string message)
        {
            if (_pending == null || _stackLines <= 0 || _pendingTruncated)
            {
                Interlocked.Increment(ref _filtered);
                return;
            }

            _pendingAt = DateTime.Now;

            if (_pendingLines >= _stackLines ||
                _pending.Length + 1 + message.Length > MaxMessageChars)
            {
                _pendingTruncated = true;
                Interlocked.Increment(ref _filtered);
                return;
            }

            _pending += "\n" + message;
            _pendingLines++;
            Interlocked.Increment(ref _coalesced);
        }

        /// <summary>把攒好的那条打出去（一条 = 首行 + 若干堆栈行）</summary>
        private static void FlushPending()
        {
            var text = _pending;
            if (text == null)
            {
                return;
            }

            _pending = null;

            if (_pendingTruncated)
            {
                text += "\n…（堆栈已截断，本条最多合并 " + _stackLines + " 行）";
            }

            if (text.Length > MaxMessageChars)
            {
                text = text.Substring(0, MaxMessageChars) + "…（已截断）";
            }

            Capture(text, _pendingLevel, _pendingTag);

            text = Prefix + text;

            switch (_pendingLevel)
            {
                case "W":
                    Debug.LogWarning(text);
                    break;
                case "E":
                case "F":
                case "A":
                    Debug.LogError(text);
                    break;
                default:
                    Debug.Log(text);
                    break;
            }

            Interlocked.Increment(ref _printed);
        }

        /// <summary>静默超过 <see cref="FlushIdleMs"/> 就把待定的那条收尾（堆栈结尾后面常常再没有新行）</summary>
        private static void FlushIfIdle()
        {
            if (_pending == null)
            {
                return;
            }

            if ((DateTime.Now - _pendingAt).TotalMilliseconds < FlushIdleMs)
            {
                return;
            }

            FlushPending();
        }

        /// <summary>一行原始输入相对「上一条日志」的身份</summary>
        private enum ContinuationKind
        {
            /// <summary>普通日志正文，启一条新的</summary>
            None,

            /// <summary><c>at …</c> / <c>Caused by:</c> / <c>... N more</c>：Java 与老格式 Unity 栈行</summary>
            StackLine,

            /// <summary>Unity Android 的 C# 栈帧 <c>Type:Method(参数)</c>，无 <c>at </c> 前缀</summary>
            UnityFrame,

            /// <summary>纯空行（Unity 的栈尾常见）</summary>
            Blank,
        }

        /// <summary>
        /// 一行是不是上一条日志的堆栈续行，以及是哪一种。
        /// <para>
        /// <b>必须看 message 部分而不是整行：</b>logcat 里 Java 与 Unity 的堆栈都是"一行一条"，
        /// 每行照样带时间戳和 tag（<c>10-27 … E Unity   :   at Foo.Bar ()</c>），
        /// 拿整行判 <c>StartsWith("\tat")</c> 永远不成立。
        /// </para>
        /// <para>
        /// <b>为什么返回分类而不是 bool：</b>三种续行的"没归属时怎么办"不一样——
        /// <see cref="ContinuationKind.StackLine"/> 直接丢（孤立栈行没意义），
        /// <see cref="ContinuationKind.UnityFrame"/> 要放行成普通日志（它和正文同形，丢了就是吞日志），
        /// <see cref="ContinuationKind.Blank"/> 一律丢（不能让它把上一条收尾）。
        /// </para>
        /// </summary>
        private static ContinuationKind ClassifyContinuation(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return ContinuationKind.Blank;
            }

            var text = message.TrimStart(' ', '\t');
            if (text.Length == 0)
            {
                return ContinuationKind.Blank;
            }

            // Java：\tat com.x.Y.z(Foo.java:42)     Unity C#：  at Foo.Bar () [0x…] in <…>:0
            if (text.StartsWith("at ", StringComparison.Ordinal))
            {
                return ContinuationKind.StackLine;
            }

            if (text.StartsWith("Caused by:", StringComparison.Ordinal) ||
                text.StartsWith("Suppressed:", StringComparison.Ordinal))
            {
                return ContinuationKind.StackLine;
            }

            // Java 的 "... 23 more"
            if (text.StartsWith("...", StringComparison.Ordinal) &&
                text.IndexOf("more", StringComparison.OrdinalIgnoreCase) > 0)
            {
                return ContinuationKind.StackLine;
            }

            // Android 上 player 的 managed 栈：Proxy_TaskGuide:CheckGuide()
            if (UnityStackFrame.IsMatch(text.TrimEnd()))
            {
                return ContinuationKind.UnityFrame;
            }

            return ContinuationKind.None;
        }

        #region C# 堆栈抑制

        /// <summary>
        /// 把 <c>Debug.Log</c> 自动附带的 C# 堆栈关掉（成对使用，见 <see cref="ReleaseStackTraceSuppression"/>）。
        /// <para>
        /// <b>为什么需要：</b>每条 <c>[ADB]</c> 日志的堆栈都指向 <c>AdbLogcatService.Emit</c> 同一个位置，
        /// 零信息量却每条占 4–8 行，是真机上最刺眼的噪声。
        /// </para>
        /// <para>
        /// <b>为什么不是每条切成对切：</b><c>Application.SetStackTraceLogType</c> 会落到编辑器偏好
        /// （Windows 上是注册表），每帧几百次写盘不可接受。这里进"抑制窗口"，窗口内直接放过，
        /// 到期（<see cref="StackSuppressWindowMs"/>）才恢复，稳态每秒最多两次切换。
        /// </para>
        /// </summary>
        private static void BeginStackTraceSuppression()
        {
            if (!_suppressStack)
            {
                return;
            }

            if (!_stackSuppressed)
            {
                _savedLogStack = Application.GetStackTraceLogType(LogType.Log);
                _savedWarningStack = Application.GetStackTraceLogType(LogType.Warning);
                _savedErrorStack = Application.GetStackTraceLogType(LogType.Error);
                _stackSettingsSaved = true;

                Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
                Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
                Application.SetStackTraceLogType(LogType.Error, StackTraceLogType.None);
                _stackSuppressed = true;
            }

            _suppressUntil = DateTime.Now.AddMilliseconds(StackSuppressWindowMs);
        }

        /// <summary>窗口到期才恢复：把用户原本的堆栈设置写回去</summary>
        private static void ReleaseStackTraceSuppression()
        {
            if (!_stackSuppressed || DateTime.Now < _suppressUntil)
            {
                return;
            }

            RestoreStackTraceNow();
        }

        /// <summary>
        /// 立刻恢复用户原来的堆栈设置。
        /// <para>
        /// <b>停止 / 退出 / 域重载前必须调</b>：这几个时机静态字段会没，但编辑器偏好留着 <c>None</c>，
        /// 不还原就等于永久改掉了用户全局的堆栈显示。
        /// </para>
        /// </summary>
        private static void RestoreStackTraceNow()
        {
            if (!_stackSuppressed)
            {
                return;
            }

            _stackSuppressed = false;
            if (!_stackSettingsSaved)
            {
                return;
            }

            _stackSettingsSaved = false;
            Application.SetStackTraceLogType(LogType.Log, _savedLogStack);
            Application.SetStackTraceLogType(LogType.Warning, _savedWarningStack);
            Application.SetStackTraceLogType(LogType.Error, _savedErrorStack);
        }

        #endregion

        private static void Enqueue(string line)
        {
            if (line == null)
            {
                return;
            }

            if (Lines.Count >= MaxQueuedLines)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }

            Lines.Enqueue(line);
        }

        private static void DropQueued()
        {
            while (Lines.TryDequeue(out _))
            {
                // 清空即可
            }
        }

        /// <summary>留一份给指令读（溢出丢最旧的，保住"最近发生了什么"）</summary>
        private static void Capture(string text, string level, string tag)
        {
            lock (CapturedGate)
            {
                if (Captured.Count >= MaxCapturedLines)
                {
                    Captured.Dequeue();
                    Interlocked.Increment(ref _capturedDropped);
                }

                Captured.Enqueue(new AdbCapturedLine
                {
                    At = DateTime.Now,
                    Level = level ?? "I",
                    Tag = tag ?? string.Empty,
                    Text = text ?? string.Empty
                });
            }
        }

        private static void ClearCaptured()
        {
            lock (CapturedGate)
            {
                Captured.Clear();
            }
        }

        private static void OnExited(int runId, Process process)
        {
            if (runId != _runId)
            {
                return;
            }

            try
            {
                _exitCode = process.ExitCode;
            }
            catch (Exception)
            {
                _exitCode = -1;
            }

            _exited = true;
        }

        private static void HandleExit()
        {
            _exited = false;
            var code = _exitCode;
            var stderr = TakeStderr();

            var process = _process;
            _process = null;
            if (process != null)
            {
                try
                {
                    process.Dispose();
                }
                catch (Exception)
                {
                    // 已经退出，释放失败无所谓
                }
            }

            _state = StateStopped;

            // 进程没了就不会再有日志，堆栈设置先还回去（下面的报错日志要保留自己的堆栈）
            _pending = null;
            RestoreStackTraceNow();

            // 老版本 adb 不认 -T：降级成"不回溯"再试一次。
            // 只在第一次失败时降级，避免设备没连时反复重启刷屏
            if (!_legacyRetry && LooksLikeUnknownOption(stderr))
            {
                _legacyRetry = true;
                AdbLog.Warning("当前 adb 不支持 -T 参数，已改为不回溯历史缓冲重试");
                if (Start(out var retryError))
                {
                    return;
                }

                _lastError = retryError;
                _state = StateFailed;
                AdbLog.Error(_lastError);
                return;
            }

            _lastError = "adb logcat 已退出（exit=" + code + "）";
            if (!string.IsNullOrEmpty(stderr))
            {
                _lastError += "：" + stderr;
            }

            var advice = "常见原因：没连设备、开了多个设备但没填序列号、adb server 版本不匹配。";
            var serial = (AdbDebuggerSettings.Serial ?? string.Empty).Trim();
            if (serial.Length > 0)
            {
                advice += "当前指定设备：" + serial + "。";
            }

            // 把"上次扫到哪几台"直接写进报错：设备换了是这个报错最常见的成因，
            // 让用户不用再开窗口对一遍序列号。措辞保留"最近一次"，因为这里读的是缓存，
            // 现扫要起进程、会卡住主线程的 Tick
            var devices = AdbDevices.Snapshot();
            if (devices.Count > 0)
            {
                var names = new StringBuilder();
                for (var i = 0; i < devices.Count; i++)
                {
                    if (i > 0)
                    {
                        names.Append("、");
                    }

                    names.Append(devices[i].Serial);
                    if (!devices[i].Usable)
                    {
                        names.Append("（").Append(devices[i].State).Append("）");
                    }
                }

                advice += "最近一次扫描到的设备：" + names + "。";
            }

            AdbLog.Error(_lastError + advice);

            // 进程没了，设备列表很可能也变了（拔线 / 关模拟器）——顺手重扫，窗口下一帧就更新
            AdbDevices.RequestScan();

            // 开关必须跟着现实走：进程没了还显示"已启用"是最容易误导人的状态
            if (AdbDebuggerSettings.LogcatEnabled)
            {
                AdbDebuggerSettings.LogcatEnabled = false;
                AdbLog.Warning("已自动关闭开关，修好后重新勾选即可");
            }
        }

        private static void CaptureStderr(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            var trimmed = line.Trim();

            lock (StderrGate)
            {
                if (Stderr.Length < 4000)
                {
                    if (Stderr.Length > 0)
                    {
                        Stderr.Append(' ');
                    }

                    Stderr.Append(trimmed);
                }
            }

            // 等设备是最常见的"没日志但也没报错"原因，单独浮出来；其它 stderr 只在退出时用
            if (trimmed.IndexOf("waiting for device", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (!string.Equals(_notice, trimmed, StringComparison.Ordinal))
                {
                    _notice = trimmed;
                    _noticePending = true;
                }
            }
        }

        private static string TakeStderr()
        {
            lock (StderrGate)
            {
                var text = Stderr.ToString();
                Stderr.Length = 0;
                return text;
            }
        }

        private static void ClearStderr()
        {
            lock (StderrGate)
            {
                Stderr.Length = 0;
            }
        }

        private static bool LooksLikeUnknownOption(string stderr)
        {
            if (string.IsNullOrEmpty(stderr))
            {
                return false;
            }

            return stderr.IndexOf("nrecognized option", StringComparison.OrdinalIgnoreCase) >= 0
                   || stderr.IndexOf("nknown option", StringComparison.OrdinalIgnoreCase) >= 0
                   || stderr.IndexOf("invalid option", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>级别名 → 数值（V=0 … A=6）。未知按 I 处理，宁可多打不可少打</summary>
        private static int RankOf(string level)
        {
            switch ((level ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "V":
                    return 0;
                case "D":
                    return 1;
                case "W":
                    return 3;
                case "E":
                    return 4;
                case "F":
                    return 5;
                case "A":
                    return 6;
                case "I":
                default:
                    return 2;
            }
        }

        #endregion

        #region 生命周期

        private static void OnBeforeAssemblyReload()
        {
            // 不清开关：编译完重载回来要按设置自动重连
            StopInternal(false, null, keepFilters: true);
        }

        private static void OnQuitting()
        {
            StopInternal(false, null, keepFilters: true);
        }

        #endregion
    }
}
