using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 一条待执行请求：由 HTTP 后台线程入队，由主线程 pump 取出执行
    /// </summary>
    public sealed class RunOutcome
    {
        /// <summary>执行报告；<see cref="Error"/> 非空时为 null</summary>
        public CommandRunReport Report;

        /// <summary>执行失败或服务中断的原因；成功时为 null</summary>
        public string Error;

        public static RunOutcome Ok(CommandRunReport report)
        {
            return new RunOutcome { Report = report };
        }

        public static RunOutcome Fail(string error)
        {
            return new RunOutcome { Error = error ?? "unknown" };
        }
    }

    /// <summary>
    /// HTTP 指令服务：把 Unity 编辑器变成一个只监听本机的指令执行端点
    /// <para>
    /// 设计要点（对应之前评估过的四个风险）：
    /// <list type="number">
    /// <item><b>主线程约束</b>：HttpListener 的回调跑在后台线程，那里只做"解析 + 鉴权 + 路由"；
    ///       真正执行指令的请求进入 <see cref="ConcurrentQueue{T}"/>，由 <see cref="Pump"/> 在
    ///       <c>EditorApplication.update</c>（主线程）里取出执行，后台线程用
    ///       <see cref="TaskCompletionSource{TResult}"/> 阻塞等待结果，从而对外表现为同步调用。</item>
    /// <item><b>域重载</b>：<c>beforeAssemblyReload</c> 时先把队列里所有等待方以"正在重载"终结，
    ///       避免后台线程永久挂起；静态域重建后按设置自动恢复监听。</item>
    /// <item><b>端口</b>：从设置里的首选端口起逐次 +1 尝试，最多 10 个，避开占用。</item>
    /// <item><b>安全</b>：只绑 127.0.0.1；校验 Host 头挡掉 DNS rebinding；强制要求
    ///       <c>X-Cmd-Token</c> 头（令牌为空时启动即自动生成）；默认关闭，需手动开启。</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class CommandHttpServer : IHttpEnvironment
    {
        /// <summary>默认端口；挑个不常见的，降低冲突概率</summary>
        public const int DefaultPort = 57831;

        /// <summary>等待主线程执行的最长时间（毫秒）</summary>
        public const int DefaultTimeoutMs = 120000;

        /// <summary>端口避让时最多尝试多少个</summary>
        private const int MaxPortAttempts = 10;

        /// <summary>报告里用的来源名，避免把伪路径当真路径解析</summary>
        private const string HttpSourceName = "<http>";

        private static readonly CommandHttpServer Live = new CommandHttpServer();

        private readonly ConcurrentQueue<PendingRequest> _queue = new ConcurrentQueue<PendingRequest>();

        private HttpListener _listener;
        private Thread _thread;
        private string _lastMessage = string.Empty;
        private int _port;
        private int _preferredPort = DefaultPort;
        private string _token = string.Empty;
        private int _timeoutMs = DefaultTimeoutMs;
        private volatile bool _stopping;

        /// <summary>本实例的身份自证；未启动时为 null</summary>
        private HttpIdentity _identity;

        /// <summary>编辑器编译/刷新状态的快照：只允许在主线程刷新，后台线程只读快照</summary>
        private bool _isCompiling;
        private bool _isUpdating;

        /// <summary>当前正在执行的运行器（主线程写、后台线程只读，用于 /v1/status 暴露进度）</summary>
        private CommandRunner _currentRunner;

        /// <summary>取消标记：/v1/cancel 置位，主线程 pump 读取后清除（协作式取消）</summary>
        private volatile bool _cancelRequested;

        /// <summary>requestId → 成功报告 的短期缓存（幂等重放；后台线程读写，需加锁）</summary>
        private readonly Dictionary<string, CachedReport> _reportCache =
            new Dictionary<string, CachedReport>(StringComparer.Ordinal);

        private readonly object _reportCacheLock = new object();

        /// <summary>幂等缓存容量上限</summary>
        private const int MaxCachedReports = 100;

        /// <summary>幂等缓存窗口期（秒）</summary>
        private const int ReportCacheTtlSeconds = 300;

        /// <summary>一条被缓存的执行报告</summary>
        private sealed class CachedReport
        {
            public JsonValue Payload;
            public DateTime CachedUtc;
        }

        /// <summary>进程内唯一实例</summary>
        public static CommandHttpServer Instance
        {
            get { return Live; }
        }

        /// <summary>是否正在监听</summary>
        public bool IsRunning
        {
            get { return _listener != null && _listener.IsListening; }
        }

        /// <summary>实际监听端口；未启动时为 0</summary>
        public int ActualPort
        {
            get { return _port; }
        }

        /// <summary>当前访问令牌</summary>
        public string Token
        {
            get { return _token; }
        }

        /// <summary>基础地址，如 <c>http://127.0.0.1:57831/</c></summary>
        public string BaseUrl
        {
            get { return _port > 0 ? "http://127.0.0.1:" + _port + "/" : string.Empty; }
        }

        /// <summary>队列中尚未执行的请求数</summary>
        public int Pending
        {
            get { return _queue.Count; }
        }

        /// <summary>最近一次执行的摘要</summary>
        public string LastMessage
        {
            get { return _lastMessage; }
        }

        /// <summary>本实例的身份自证（进程号 / 是否交互式 / 角色 / 工程）；未启动时为 null</summary>
        public HttpIdentity Identity
        {
            get { return _identity; }
        }

        /// <summary>本次启动时的首选端口；端口避让的区间起点</summary>
        public int PortRangeStart
        {
            get { return _preferredPort; }
        }

        /// <summary>端口避让区间长度（即最多向后尝试多少个端口）</summary>
        public int PortRangeCount
        {
            get { return MaxPortAttempts; }
        }

        #region IHttpEnvironment

        bool IHttpEnvironment.IsCompiling
        {
            get { return _isCompiling; }
        }

        bool IHttpEnvironment.IsUpdating
        {
            get { return _isUpdating; }
        }

        int IHttpEnvironment.PendingCount
        {
            get { return _queue.Count; }
        }

        string IHttpEnvironment.LastMessage
        {
            get { return _lastMessage; }
        }

        string IHttpEnvironment.ExpectedToken
        {
            get { return _token; }
        }

        int IHttpEnvironment.Port
        {
            get { return _port; }
        }

        HttpIdentity IHttpEnvironment.Identity
        {
            get { return _identity; }
        }

        int IHttpEnvironment.PortRangeStart
        {
            get { return _preferredPort; }
        }

        int IHttpEnvironment.PortRangeCount
        {
            get { return MaxPortAttempts; }
        }

        IEnumerable<HttpCommandInfo> IHttpEnvironment.Commands
        {
            get
            {
                var list = new List<HttpCommandInfo>();
                foreach (var descriptor in CommandRegistry.Default.All)
                {
                    list.Add(new HttpCommandInfo
                    {
                        Id = descriptor.Id,
                        Description = descriptor.Description,
                        Usage = descriptor.Usage,
                        EditorOnly = descriptor.IsEditorOnly,
                        Runtime = descriptor.IsRuntime,
                        PlayState = RuntimeCommandGate.DescribePhase(descriptor.RuntimePhase),
                        RequiresConsent = descriptor.RequiresConsent
                    });
                }

                return list;
            }
        }

        string IHttpEnvironment.CurrentCommandId
        {
            get
            {
                var runner = _currentRunner;
                return runner != null ? runner.CurrentCommandId : null;
            }
        }

        long IHttpEnvironment.CurrentElapsedMs
        {
            get
            {
                var runner = _currentRunner;
                return runner != null ? runner.CurrentElapsedMs : 0L;
            }
        }

        /// <summary>
        /// 最近一次编译的结果摘要。
        /// <para>编译失败时不会发生域重载，本服务的旧实例仍然存活，能正常回答——这正是闭环能工作的前提。</para>
        /// </summary>
        JsonValue IHttpEnvironment.CompileSummary
        {
            get { return CommandCompileMonitor.Last.ToJson(false); }
        }

        /// <summary>
        /// 运行时指令开关摘要：开关按工程存放在 EditorPrefs，主线程与后台线程都只读，无竞态。
        /// </summary>
        JsonValue IHttpEnvironment.RuntimeCommands
        {
            get
            {
                var count = 0;
                foreach (var descriptor in CommandRegistry.Default.All)
                {
                    if (descriptor.IsRuntime)
                    {
                        count++;
                    }
                }

                return RuntimeCommandGate.SummaryJson(count);
            }
        }

        /// <summary>
        /// 敏感操作（进/出 Play、开场景）的确认状态摘要。
        /// <para>与运行时摘要同理：只读主线程快照，因为本属性在 HTTP 监听线程被访问。</para>
        /// </summary>
        JsonValue IHttpEnvironment.Consent
        {
            get
            {
                var count = 0;
                foreach (var descriptor in CommandRegistry.Default.All)
                {
                    if (descriptor.RequiresConsent)
                    {
                        count++;
                    }
                }

                return ConsentGate.SummaryJson(count);
            }
        }

        JsonValue IHttpEnvironment.TryGetCachedReport(string requestId)
        {
            if (string.IsNullOrEmpty(requestId))
            {
                return null;
            }

            lock (_reportCacheLock)
            {
                CachedReport entry;
                if (!_reportCache.TryGetValue(requestId, out entry))
                {
                    return null;
                }

                if ((DateTime.UtcNow - entry.CachedUtc).TotalSeconds > ReportCacheTtlSeconds)
                {
                    _reportCache.Remove(requestId);
                    return null;
                }

                return entry.Payload;
            }
        }

        void IHttpEnvironment.CacheReport(string requestId, JsonValue payload)
        {
            if (string.IsNullOrEmpty(requestId) || payload == null)
            {
                return;
            }

            lock (_reportCacheLock)
            {
                if (!_reportCache.ContainsKey(requestId) && _reportCache.Count >= MaxCachedReports)
                {
                    PruneReportCacheLocked();
                }

                _reportCache[requestId] = new CachedReport
                {
                    Payload = payload,
                    CachedUtc = DateTime.UtcNow
                };
            }
        }

        void IHttpEnvironment.RequestCancel()
        {
            _cancelRequested = true;
            CommandLog.Warning("已收到取消请求；执行器将在下一个检查点退出");
        }

        /// <summary>
        /// 清缓存：先删过期的；仍然超量则淘汰最早写入的那条。
        /// <para>调用方必须已持有 <see cref="_reportCacheLock"/>。</para>
        /// </summary>
        private void PruneReportCacheLocked()
        {
            var now = DateTime.UtcNow;
            var expired = new List<string>();
            foreach (var pair in _reportCache)
            {
                if ((now - pair.Value.CachedUtc).TotalSeconds > ReportCacheTtlSeconds)
                {
                    expired.Add(pair.Key);
                }
            }

            for (var i = 0; i < expired.Count; i++)
            {
                _reportCache.Remove(expired[i]);
            }

            if (_reportCache.Count < MaxCachedReports)
            {
                return;
            }

            string oldestKey = null;
            var oldestUtc = DateTime.MaxValue;
            foreach (var pair in _reportCache)
            {
                if (pair.Value.CachedUtc < oldestUtc)
                {
                    oldestUtc = pair.Value.CachedUtc;
                    oldestKey = pair.Key;
                }
            }

            if (oldestKey != null)
            {
                _reportCache.Remove(oldestKey);
            }
        }

        /// <summary>
        /// 执行一段指令文本：本方法可能在 HTTP 后台线程被调用，
        /// 因此只负责入队并阻塞等待主线程 <see cref="Pump"/> 的结果
        /// </summary>
        CommandRunReport IHttpEnvironment.RunFromText(string text, bool dryRun)
        {
            var pending = new PendingRequest
            {
                Body = text,
                DryRun = dryRun,
                Completion = new TaskCompletionSource<RunOutcome>()
            };

            _queue.Enqueue(pending);

            RunOutcome outcome;
            if (!pending.Completion.Task.Wait(_timeoutMs))
            {
                return null;
            }

            outcome = pending.Completion.Task.Result;
            if (outcome != null && !string.IsNullOrEmpty(outcome.Error))
            {
                throw new InvalidOperationException(outcome.Error);
            }

            return outcome?.Report;
        }

        #endregion

        #region 生命周期

        /// <summary>
        /// 启动服务
        /// </summary>
        /// <param name="preferredPort">首选端口；被占用时自动向后避让</param>
        /// <param name="token">访问令牌；为空则自动生成一个并持久化</param>
        public bool Start(int preferredPort, string token)
        {
            Stop();

            _token = token ?? string.Empty;
            if (_token.Length == 0)
            {
                _token = Guid.NewGuid().ToString("N");
                CommandLog.Warning("未配置 HTTP 令牌，已自动生成；请从「指令系统 / HTTP 指令服务」窗口查看并交给客户端");
            }

            var port = preferredPort > 0 ? preferredPort : DefaultPort;
            _preferredPort = port;
            HttpListener listener = null;

            for (var attempt = 0; attempt < MaxPortAttempts && listener == null; attempt++)
            {
                var candidate = new HttpListener();
                try
                {
                    candidate.Prefixes.Add("http://127.0.0.1:" + port + "/");
                    candidate.Start();
                    listener = candidate;
                }
                catch (Exception e)
                {
                    CommandLog.Warning($"端口 {port} 不可用：{e.Message}");
                    // 失败的 HttpListener 必须显式关闭：Start() 抛异常前可能已向 http.sys 注册了前缀，
                    // 不关会留下一个"能建立 TCP 连接但永不回包"的僵尸监听，客户端探测只能等满超时。
                    try
                    {
                        candidate.Close();
                    }
                    catch (Exception)
                    {
                    }

                    listener = null;
                    port++;
                }
            }

            if (listener == null)
            {
                CommandLog.Error($"HTTP 指令服务启动失败：{DefaultPort} 起连续 {MaxPortAttempts} 个端口都被占用");
                _token = string.Empty;
                return false;
            }

            _listener = listener;
            _port = port;
            _identity = HttpIdentity.Build();
            _stopping = false;
            _thread = new Thread(ListenLoop);
            _thread.IsBackground = true;
            _thread.Name = "Aketsuki.UnityMcp.CommandHttpServer";
            _thread.Start();

            CommandLog.Info($"HTTP 指令服务已启动：{BaseUrl}（令牌{(string.IsNullOrEmpty(_token) ? "未设置" : "已设置")}）");
            return true;
        }

        /// <summary>停止服务，并把队列中所有等待方以原因终结</summary>
        public void Stop()
        {
            _stopping = true;

            if (_listener != null)
            {
                try
                {
                    _listener.Stop();
                }
                catch (Exception)
                {
                    // 关闭阶段抛异常无所谓，目标只是让它退出
                }

                try
                {
                    _listener.Close();
                }
                catch (Exception)
                {
                }

                _listener = null;
            }

            FlushPending("HTTP 指令服务已停止");
            _port = 0;
        }

        /// <summary>热替换内存中的令牌，不影响监听与队列中的请求</summary>
        public void ReplaceToken(string token)
        {
            _token = token ?? string.Empty;
        }

        /// <summary>把所有等待中的请求以指定原因终结（用于域重载、停止服务）</summary>
        public void FlushPending(string reason)
        {
            PendingRequest item;
            while (_queue.TryDequeue(out item))
            {
                item.Completion.TrySetResult(RunOutcome.Fail(reason));
            }
        }

        /// <summary>
        /// 在主线程上刷新编辑器状态快照（编译 / 刷新）
        /// <para>
        /// <c>EditorApplication.isCompiling</c> / <c>isUpdating</c> 只能在主线程读取，
        /// 而 HTTP 请求的鉴权与 busy 判断跑在后台线程。因此由 <c>EditorApplication.update</c>
        /// （主线程）每帧调用本方法刷新快照，后台线程只读这两个缓存值。
        /// </para>
        /// </summary>
        public void RefreshState()
        {
            try
            {
                _isCompiling = EditorApplication.isCompiling;
                _isUpdating = EditorApplication.isUpdating;
            }
            catch (Exception)
            {
                // 极端情况下保留上一帧快照，不影响请求处理
            }
        }

        #endregion

        #region 主线程泵

        /// <summary>
        /// 主线程执行泵：由 <c>EditorApplication.update</c> 驱动
        /// <para>无待办时一次判空即返回，不做分配。</para>
        /// </summary>
        public void Pump()
        {
            if (_queue.IsEmpty)
            {
                return;
            }

            PendingRequest item;
            while (_queue.TryDequeue(out item))
            {
                var outcome = new RunOutcome();
                CommandRunner runner = null;
                try
                {
                    runner = new CommandRunner
                    {
                        Verbose = CommandSystemSettings.Verbose,
                        StopOnError = CommandSystemSettings.StopOnError,
                        DryRun = item.DryRun,
                        WatchdogSeconds = CommandSystemSettings.WatchdogSeconds,
                        // 协作式取消：读取后台线程置位的标记
                        ShouldAbort = () => _cancelRequested
                    };

                    // 直接由文本构造，不触发任何文件副作用
                    var script = CommandReader.FromText(item.Body, HttpSourceName);
                    _currentRunner = runner;
                    outcome.Report = runner.Run(script);
                    _lastMessage = outcome.Report != null
                        ? outcome.Report.ToSummary()
                        : "执行器未返回报告";
                }
                catch (Exception e)
                {
                    outcome.Error = e.Message;
                    _lastMessage = "执行失败：" + e.Message;
                    CommandLog.Error("HTTP 指令执行失败：" + e.Message);
                }
                finally
                {
                    _currentRunner = null;

                    // 一次请求消费掉取消标记，避免影响后续请求
                    _cancelRequested = false;

                    // Refresh 收敛：整个请求（含 batch 内所有指令）只刷新一次
                    if (runner != null && runner.AssetsDirty)
                    {
                        try
                        {
                            AssetDatabase.Refresh();
                        }
                        catch (Exception e)
                        {
                            CommandLog.Warning("批次结束刷新 AssetDatabase 失败：" + e.Message);
                        }
                    }
                }

                item.Completion.TrySetResult(outcome);
            }
        }

        #endregion

        #region 后台监听

        private void ListenLoop()
        {
            while (!_stopping)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch (Exception)
                {
                    // Stop() 或监听器失效时 GetContext 会抛异常，借此退出循环
                    break;
                }

                // 每个请求独立处理：execute 会阻塞在队列上等主线程执行完，
                // 若串行处理，指令执行期间连 /v1/status 都进不来——看门狗就失去意义了。
                // 线程分工不变：这里仍只做解析/鉴权/路由，执行一律经队列交给主线程 Pump。
                var captured = context;
                Task.Run(() =>
                {
                    try
                    {
                        HandleContext(captured);
                    }
                    catch (Exception e)
                    {
                        // 单个请求失败不应拖垮整个服务
                        CommandLog.Warning("HTTP 请求处理异常：" + e.Message);
                        TryWrite(captured, HttpResponse.Fail(500, "internal_error", e.Message));
                    }
                });
            }
        }

        private void HandleContext(HttpListenerContext context)
        {
            var request = context.Request;

            // Host 头校验：挡掉通过恶意网页发起的 DNS rebinding 攻击
            var host = (request.Headers["Host"] ?? string.Empty).Trim().ToLowerInvariant();
            if (!IsHostAllowed(host))
            {
                TryWrite(context, HttpResponse.Fail(403, "forbidden_host", "仅接受来自本机的请求"));
                return;
            }

            string path;
            string query;
            HttpProtocol.SplitUrl(request.RawUrl, out path, out query);

            var body = string.Empty;
            if (string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                if (request.ContentLength64 > HttpProtocol.MaxBodyBytes)
                {
                    TryWrite(context, HttpResponse.Fail(413, "payload_too_large", "请求体超过上限"));
                    return;
                }

                using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                {
                    body = reader.ReadToEnd();
                }

                if (body.Length > HttpProtocol.MaxBodyBytes)
                {
                    TryWrite(context, HttpResponse.Fail(413, "payload_too_large", "请求体超过上限"));
                    return;
                }
            }

            var httpRequest = new HttpRequest
            {
                Method = (request.HttpMethod ?? "GET").ToUpperInvariant(),
                Path = path,
                Query = query,
                Body = body
            };

            var keys = request.Headers.AllKeys;
            for (var i = 0; i < keys.Length; i++)
            {
                httpRequest.Headers[keys[i].ToLowerInvariant()] = request.Headers[keys[i]];
            }

            var response = HttpProtocol.Handle(httpRequest, this);
            TryWrite(context, response);
        }

        /// <summary>只放行本机回环地址（可带端口）</summary>
        private static bool IsHostAllowed(string host)
        {
            if (host.Length == 0)
            {
                return true;
            }

            var colon = host.IndexOf(':');
            if (colon > 0)
            {
                host = host.Substring(0, colon);
            }

            return host == "localhost"
                   || host == "127.0.0.1"
                   || host == "::1"
                   || host == "[::1]";
        }

        private static void TryWrite(HttpListenerContext context, HttpResponse response)
        {
            if (context == null || response == null)
            {
                return;
            }

            try
            {
                var buffer = Encoding.UTF8.GetBytes(response.Body);
                context.Response.StatusCode = response.Status;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = buffer.Length;
                context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                context.Response.OutputStream.Close();
            }
            catch (Exception)
            {
                // 客户端提前断开等情况，静默即可
                try
                {
                    context.Response.Abort();
                }
                catch (Exception)
                {
                }
            }
        }

        #endregion

        /// <summary>队列中的一条待执行请求</summary>
        private sealed class PendingRequest
        {
            public string Body;
            public bool DryRun;
            public TaskCompletionSource<RunOutcome> Completion;
        }
    }

    /// <summary>
    /// HTTP 指令服务的引导、菜单与生命周期挂钩
    /// <para>
    /// 资源开销：未启动时不监听任何端口，update 钩子里的泵只做一次判空即返回，与文件监听同一量级。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class CommandHttpServerBoot
    {
        private const string MenuRoot = "Tools/指令系统/";
        private const string MenuToggle = MenuRoot + "HTTP 指令服务";

        static CommandHttpServerBoot()
        {
            // 关键：只在"真正的编辑器进程"里干活。
            // Unity 的资源导入工作进程（AssetImportWorker0/1）以 -batchMode 启动，但同样会加载
            // Editor 程序集并执行 [InitializeOnLoad]。不加区分的话它们也会各自起一个监听，
            // 并且往同一个 .http-info 里写自己的端口（谁最后启动谁覆盖）；
            // 而 batchmode 进程没有编辑器主循环，EditorApplication.update 不跑，
            // 指令会永久卡在它的队列里——客户端却以为连的是编辑器。见 IsInteractiveEditorProcess。
            if (!IsInteractiveEditorProcess())
            {
                return;
            }

            EditorApplication.update += Pump;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnQuitting;

            // 编辑器加载 / 每次域重载后，按"本工程的设置"恢复监听。
            //
            // 这里必须走 RestoreFromSettings（它会检查 HttpServerEnabled），不能直接 StartServer：
            // 以前是无条件启动的，导致开关形同虚设——关掉之后下一次域重载又自己拉起来，
            // 连"没开过的工程"也会一导入就起服务。
            // 现在每个工程各管各的：首次默认为关，用菜单 Tools / 指令系统 / HTTP 指令服务 开一次
            // 就会写进该工程自己的设置里，之后每次重载自动恢复。
            RestoreFromSettings();
        }

        /// <summary>
        /// 当前进程是否是交互式的 Unity 编辑器（而非 batchmode 的资源导入工作进程）
        /// <para>
        /// 判定依据是命令行里的 <c>-batchMode</c>：AssetImportWorker 由编辑器以
        /// <c>-adb2 -batchMode -name AssetImportWorkerN</c> 拉起，会加载同一套 Editor 程序集，
        /// 但既不显示窗口也不跑 <c>EditorApplication.update</c>。
        /// </para>
        /// <para>
        /// 判定统一交给 <see cref="HttpIdentity"/>：与写进 <c>.http-info</c>、随
        /// <c>/v1/status</c> 返回的身份是同一套逻辑，避免"启动时的判断"和"对外宣称的身份"漂移。
        /// 那里同样只用 <see cref="Environment.GetCommandLineArgs"/>，不碰 Unity API，
        /// 因此可以在静态构造里最早调用，离线测试工程也能直接跑。
        /// </para>
        /// </summary>
        private static bool IsInteractiveEditorProcess()
        {
            try
            {
                return HttpIdentity.Build().Interactive;
            }
            catch (Exception)
            {
                // 拿不到命令行时按编辑器处理，保持原有行为
                return true;
            }
        }

        /// <summary>当前是否在监听</summary>
        public static bool IsRunning
        {
            get { return CommandHttpServer.Instance.IsRunning; }
        }

        /// <summary>当前基础地址；未启动为空串</summary>
        public static string BaseUrl
        {
            get { return CommandHttpServer.Instance.BaseUrl; }
        }

        /// <summary>当前令牌；未启动为空串</summary>
        public static string Token
        {
            get { return CommandHttpServer.Instance.Token; }
        }

        /// <summary>最近一次执行摘要</summary>
        public static string LastMessage
        {
            get { return CommandHttpServer.Instance.LastMessage; }
        }

        /// <summary>按持久化设置恢复服务（域重载后会再次走到这里）</summary>
        public static void RestoreFromSettings()
        {
            if (!CommandSystemSettings.HttpServerEnabled)
            {
                return;
            }

            var token = CommandSystemSettings.HttpToken;
            if (string.IsNullOrEmpty(token))
            {
                token = Guid.NewGuid().ToString("N");
                CommandSystemSettings.HttpToken = token;
                CommandLog.Warning("已为 HTTP 指令服务生成新令牌，请在设置窗口查看");
            }

            StartServer(CommandSystemSettings.HttpPort, token);
        }

        /// <summary>启动服务；成功则把启用状态写回设置</summary>
        public static bool StartServer(int port, string token)
        {
            var ok = CommandHttpServer.Instance.Start(port, token);
            if (ok)
            {
                CommandSystemSettings.HttpServerEnabled = true;
                // 存实际端口：首选端口可能被占用而向后避让，存回 port 会让设置与真实监听漂移
                CommandSystemSettings.HttpPort = CommandHttpServer.Instance.ActualPort;
                if (!string.IsNullOrEmpty(CommandHttpServer.Instance.Token))
                {
                    CommandSystemSettings.HttpToken = CommandHttpServer.Instance.Token;
                }

                WriteInfoFile();
            }

            return ok;
        }

        /// <summary>停止服务并清掉启用标记</summary>
        public static void StopServer()
        {
            CommandHttpServer.Instance.Stop();
            CommandSystemSettings.HttpServerEnabled = false;
            DeleteInfoFile();
            CommandLog.Info("HTTP 指令服务已停止");
        }

        /// <summary>重新生成令牌（旧令牌立即失效，客户端需同步更新）</summary>
        public static string RegenerateToken()
        {
            var token = Guid.NewGuid().ToString("N");
            CommandSystemSettings.HttpToken = token;

            if (CommandHttpServer.Instance.IsRunning)
            {
                // 直接热替换内存里的令牌，无需重启监听
                CommandHttpServer.Instance.ReplaceToken(token);
                WriteInfoFile();
                CommandLog.Info("HTTP 令牌已重新生成，监听未中断");
            }

            return token;
        }

        /// <summary>信息文件的基础前缀（完整名为 <c>.http-info.&lt;projectHash&gt;</c>）</summary>
        private const string InfoFilePrefix = ".http-info";

        private static string _projectHash;

        /// <summary>
        /// 本工程的稳定短哈希（规范化工程路径的 SHA1 前 8 位 hex）。
        /// <para>
        /// 跨进程稳定、不依赖 EditorPrefs：多个 Unity 工程共用一个工作区时，
        /// 各自的信息文件互不覆盖（此前它们会争抢同一个 <c>.http-info</c>）。
        /// </para>
        /// </summary>
        public static string ProjectHash
        {
            get
            {
                if (_projectHash == null)
                {
                    _projectHash = ComputeProjectHash();
                }

                return _projectHash;
            }
        }

        private static string ComputeProjectHash()
        {
            // 哈希算法的唯一实现放在 CommandPathUtil（EditorPrefs 隔离键也要用），
            // 这里只做转发，避免两份实现漂移。
            return CommandPathUtil.ProjectHash;
        }

        /// <summary>信息文件的基础名（不带路径），已含工程 hash</summary>
        private static string InfoFileName
        {
            get { return InfoFilePrefix + "." + ProjectHash; }
        }

        /// <summary>
        /// 把服务地址、令牌与身份写进指令目录下的 <c>.http-info</c>
        /// <para>
        /// 这是客户端获取端口的首要途径：端口可能因占用而避让，客户端不该硬编码。
        /// 文件名不带 .json 后缀，免得被指令目录扫描当成指令文件。
        /// </para>
        /// <para>
        /// 写两份：<c>.http-info</c> 是"当前公认的目标"，<c>.http-info.&lt;pid&gt;</c> 是本实例的自述副本。
        /// 同一个工程可能有多个 Unity 进程在跑（编辑器 + 资源导入工作进程），都往主文件写就会互相覆盖，
        /// 客户端便可能连到一个不执行指令的进程上。副本让客户端能枚举出全部候选，再按身份挑对的那个。
        /// </para>
        /// </summary>
        public static void WriteInfoFile()
        {
            try
            {
                var server = CommandHttpServer.Instance;
                var identity = server.Identity;

                var payload = JsonValue.NewObject();
                payload.Set("port", JsonValue.Of(server.ActualPort));
                payload.Set("token", JsonValue.Of(server.Token));
                payload.Set("url", JsonValue.Of(server.BaseUrl));
                // 端口避让区间：客户端据此扫描候选，无需硬编码任何端口号
                payload.Set("portRangeStart", JsonValue.Of(server.PortRangeStart));
                payload.Set("portRangeCount", JsonValue.Of(server.PortRangeCount));
                payload.Set("updatedUtc", JsonValue.Of(DateTime.UtcNow.ToString("o")));
                payload.Set(HttpIdentityFields.Key, identity != null ? identity.ToJson() : JsonValue.Null);
                if (identity != null)
                {
                    // 顶层也平铺一份 pid：肉眼排查和老客户端都能直接读到
                    payload.Set("pid", JsonValue.Of(identity.Pid));
                    payload.Set("role", JsonValue.Of(identity.Role));
                    payload.Set("interactive", JsonValue.Of(identity.Interactive));
                }

                // 用无 BOM 的 UTF-8 写，避免 C# Encoding.UTF8 默认带 BOM 让客户端解析失败
                var text = payload.ToJson(true);
                var encoding = new UTF8Encoding(false);

                if (!ShouldYieldMainInfoFile(identity))
                {
                    // 原子写：客户端随时可能在读这个文件，绝不能读到写了一半的 JSON
                    AtomicFileWriter.WriteAllText(InfoFilePath, text, encoding);
                }

                PruneStaleInfoCopies(identity != null ? identity.Pid : 0);
                AtomicFileWriter.WriteAllText(PerPidInfoFilePath, text, encoding);
            }
            catch (Exception e)
            {
                CommandLog.Warning("写入 HTTP 服务信息文件失败：" + e.Message);
            }
        }

        /// <summary>
        /// 主信息文件是否应该让给别的进程
        /// <para>
        /// 只有一种情况让位：文件里的 pid 还活着，且那个进程是交互式编辑器——它同样是"能执行指令"的
        /// 正主，抢过来只会让端口归属在两个编辑器之间来回跳。反之（对方是 batchmode 的资源导入工作进程，
        /// 或进程已经死了）就照常覆盖，把客户端引回真正能干活的那个。
        /// </para>
        /// </summary>
        private static bool ShouldYieldMainInfoFile(HttpIdentity self)
        {
            var path = InfoFilePath;
            if (self == null || !File.Exists(path))
            {
                return false;
            }

            var holder = ReadInfoFileSnapshot(path);
            if (holder == null || holder.Pid <= 0 || holder.Pid == self.Pid)
            {
                return false;
            }

            if (!HttpIdentity.IsProcessAlive(holder.Pid))
            {
                return false;
            }

            return holder.Interactive;
        }

        /// <summary>主信息文件现在是不是本实例写的（被别人覆盖后需要抢回来）</summary>
        private static bool IsMainInfoFileOurs()
        {
            var path = InfoFilePath;
            if (!File.Exists(path))
            {
                return false;
            }

            var snapshot = ReadInfoFileSnapshot(path);
            if (snapshot == null)
            {
                return false;
            }

            var self = CommandHttpServer.Instance.Identity;
            var selfPid = self != null ? self.Pid : 0;
            return snapshot.Pid > 0 && snapshot.Pid == selfPid;
        }

        /// <summary>读信息文件里最关键的几个字段；读不出来返回 null</summary>
        private static InfoFileSnapshot ReadInfoFileSnapshot(string path)
        {
            try
            {
                var json = JsonValue.Parse(File.ReadAllText(path, Encoding.UTF8));
                if (json == null || !json.IsObject)
                {
                    return null;
                }

                var nested = HttpIdentity.FromJson(json.ContainsKey(HttpIdentityFields.Key)
                    ? json[HttpIdentityFields.Key]
                    : null);

                return new InfoFileSnapshot
                {
                    Pid = json.GetInt("pid", nested != null ? nested.Pid : 0),
                    Interactive = json.GetBool("interactive", nested != null && nested.Interactive),
                    Port = json.GetInt("port", 0)
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>清掉那些进程已经不存在的 per-pid 副本，免得目录里越积越多</summary>
        private static void PruneStaleInfoCopies(int selfPid)
        {
            var directory = Path.GetDirectoryName(InfoFilePath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }

            var prefix = InfoFileName + ".";
            string[] files;
            try
            {
                files = Directory.GetFiles(directory, InfoFileName + "*");
            }
            catch (Exception)
            {
                return;
            }

            for (var i = 0; i < files.Length; i++)
            {
                var name = Path.GetFileName(files[i]);
                // 主信息文件 .http-info 也匹配 ".http-info*" 通配，但它的长度比前缀 ".http-info." 还短，
                // 直接 Substring 会越界抛异常。它不是 per-pid 副本，跳过即可。
                if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length <= prefix.Length)
                {
                    continue;
                }

                var suffix = name.Substring(prefix.Length);
                int pid;
                if (!int.TryParse(suffix, out pid) || pid == selfPid)
                {
                    continue;
                }

                if (!HttpIdentity.IsProcessAlive(pid))
                {
                    try
                    {
                        File.Delete(files[i]);
                    }
                    catch (Exception)
                    {
                        // 删不掉无关紧要，下次启动还会再试
                    }
                }
            }
        }

        /// <summary>删除信息文件（服务停止后客户端能据此判断服务已下线）</summary>
        public static void DeleteInfoFile()
        {
            DeleteIfExists(InfoFilePath);
            DeleteIfExists(PerPidInfoFilePath);
        }

        private static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // 删不掉不影响主流程
            }
        }

        /// <summary>信息文件完整路径</summary>
        public static string InfoFilePath
        {
            get
            {
                return Path.Combine(CommandPathUtil.EnsureDefaultCommandDirectory(), InfoFileName);
            }
        }

        /// <summary>本实例的自述副本路径：同一工程多进程并存时供客户端枚举候选</summary>
        public static string PerPidInfoFilePath
        {
            get
            {
                var self = CommandHttpServer.Instance.Identity;
                var pid = self != null ? self.Pid : 0;
                return InfoFilePath + (pid > 0 ? "." + pid : ".unknown");
            }
        }

        /// <summary>信息文件的关键字段（内部用）</summary>
        private sealed class InfoFileSnapshot
        {
            public int Pid;
            public bool Interactive;
            public int Port;
        }

        private static void Pump()
        {
            // 先在主线程刷新编辑器状态快照，供 HTTP 后台线程只读缓存（避免跨线程读 isCompiling/isUpdating）
            CommandHttpServer.Instance.RefreshState();
            CommandHttpServer.Instance.Pump();
            EnsureInfoFile();
        }

        /// <summary>信息文件自检的节拍（Environment.TickCount，毫秒）</summary>
        private static int _nextInfoCheckTick;

        /// <summary>
        /// 保证主信息文件始终指向本实例（约每秒自检一次）
        /// <para>
        /// 同一个工程目录下的其它 Unity 进程（历史上包括资源导入工作进程）也会写/删这个文件：
        /// 它们可能在编辑器刚写完之后把它删掉、或写成自己的端口，于是客户端要么读到"服务未启动"的假象、
        /// 要么连到一个不执行指令的进程上。这里由编辑器自己补写/抢回，让真相不至于丢失。
        /// </para>
        /// </summary>
        private static void EnsureInfoFile()
        {
            if (!CommandHttpServer.Instance.IsRunning)
            {
                return;
            }

            var tick = Environment.TickCount;
            // 用无符号差值比较，TickCount 约 49 天回绕时也不会算错
            if (unchecked(tick - _nextInfoCheckTick) < 1000)
            {
                return;
            }

            _nextInfoCheckTick = tick;
            if (!IsMainInfoFileOurs())
            {
                WriteInfoFile();
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            // 先终结所有等待方，避免后台线程在域销毁后永久挂起
            CommandHttpServer.Instance.FlushPending("Unity 正在重新编译，请稍后重试");
            CommandHttpServer.Instance.Stop();
            // 信息文件一并删掉；新域重建后 RestoreFromSettings 会重新写
            DeleteInfoFile();
        }

        private static void OnQuitting()
        {
            CommandHttpServer.Instance.Stop();
        }

        [MenuItem(MenuToggle)]
        private static void ToggleMenuItem()
        {
            if (CommandHttpServer.Instance.IsRunning)
            {
                StopServer();
            }
            else
            {
                StartServer(CommandSystemSettings.HttpPort, CommandSystemSettings.HttpToken);
            }
        }

        [MenuItem(MenuToggle, true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(MenuToggle, CommandHttpServer.Instance.IsRunning);
            return true;
        }
    }
}
