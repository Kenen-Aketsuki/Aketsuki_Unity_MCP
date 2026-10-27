using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace CivilRev.AdbDebugger
{
    /// <summary>一次请求的响应</summary>
    internal struct AdbReply
    {
        internal int StatusCode;
        internal string Body;

        internal static AdbReply Ok(string body)
        {
            return new AdbReply { StatusCode = 200, Body = body };
        }

        internal static AdbReply Fail(int status, string error)
        {
            return new AdbReply
            {
                StatusCode = status,
                Body = new JsonOut().Bool("ok", false).Str("error", error).ToString()
            };
        }
    }

    /// <summary>等待主线程处理的请求</summary>
    internal sealed class AdbWorkItem
    {
        internal string Method;
        internal string Path;
        internal Dictionary<string, string> Query;
        internal readonly TaskCompletionSource<AdbReply> Completion =
            new TaskCompletionSource<AdbReply>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// ADB 调试工具自己的 HTTP 服务。
    ///
    /// <para>
    /// <b>为什么必须有自己的服务：</b>抓取要往 Unity 控制台打日志、要读写 EditorPrefs，
    /// 这些都是<b>主线程专用</b> API。MCP 客户端跑在另一个进程里，唯一能把"请启动抓取"送进主线程的
    /// 通道就是本机 HTTP。它不借用「Unity MCP 指令系统」的服务——那套服务可能根本不存在
    /// （本工具可以单独安装）。
    /// </para>
    ///
    /// <para>
    /// <b>端口默认 57841</b>，与指令系统的 57831 拉开距离，避开它的端口避让区间；
    /// 另外有自己的令牌、自己的信息文件（<c>.workbuddy/AdbDebug/.adb-info.&lt;hash&gt;</c>），
    /// 两边互不感知。
    /// </para>
    ///
    /// <para>
    /// 线程模型与指令系统同构（踩过的坑不想再踩一遍）：
    /// 后台线程只做 <c>GetContext</c> + 鉴权 + 路由，<b>执行一律排队交给主线程</b>；
    /// 每个请求独立 <c>Task.Run</c>，否则一条慢请求会堵死 <c>/v1/status</c>。
    /// </para>
    /// </summary>
    internal sealed class AdbDebugHttpServer
    {
        private const int MaxPortAttempts = 20;
        private const int MaxQueuedItems = 64;

        private static readonly AdbDebugHttpServer Live = new AdbDebugHttpServer();

        private readonly ConcurrentQueue<AdbWorkItem> _queue = new ConcurrentQueue<AdbWorkItem>();

        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _stopping;
        private int _port;
        private string _token = string.Empty;
        private bool _pumping;

        internal static AdbDebugHttpServer Instance
        {
            get { return Live; }
        }

        internal bool IsRunning
        {
            get { return _listener != null && !_stopping; }
        }

        internal int ActualPort
        {
            get { return _port; }
        }

        internal string Token
        {
            get { return _token; }
        }

        internal string BaseUrl
        {
            get { return _port > 0 ? "http://127.0.0.1:" + _port + "/" : string.Empty; }
        }

        /// <summary>
        /// 启动服务。
        /// <para>首选端口被占用时向后避让；全部失败返回 false 并说明。</para>
        /// </summary>
        internal bool Start(int preferredPort, out string error)
        {
            error = null;
            if (IsRunning)
            {
                return true;
            }

            // 资源导入工作进程 / batchmode 没有主循环，泵不动队列：在那里起服务只会得到"连得上但永不回包"
            if (Application.isBatchMode)
            {
                error = "当前是 batchmode / 工作进程，ADB 调试服务不启动（没有主循环可泵）";
                return false;
            }

            Stop();

            var port = preferredPort > 0 ? preferredPort : AdbDebuggerSettings.DefaultPort;
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
                    // 失败的 HttpListener 必须显式关闭：Start() 抛异常前可能已向 http.sys 注册了前缀，
                    // 不关会留下"能建立 TCP 连接但永不回包"的僵尸监听，客户端只能等满超时。
                    try
                    {
                        candidate.Close();
                    }
                    catch (Exception)
                    {
                    }

                    listener = null;
                    if (attempt == 0)
                    {
                        AdbLog.Warning("端口 " + port + " 不可用：" + e.Message + "，尝试向后避让");
                    }

                    port++;
                }
            }

            if (listener == null)
            {
                error = "端口 " + preferredPort + " 起连续 " + MaxPortAttempts + " 个都被占用，ADB 调试服务未能启动";
                return false;
            }

            _token = (AdbDebuggerSettings.Token ?? string.Empty).Trim();
            if (_token.Length == 0)
            {
                _token = Guid.NewGuid().ToString("N");
                AdbDebuggerSettings.Token = _token;
                AdbLog.Warning("未配置令牌，已自动生成并保存（在「ADB 调试台」窗口可查看）");
            }

            _listener = listener;
            _port = port;
            _stopping = false;

            _thread = new Thread(ListenLoop) { IsBackground = true, Name = "AdbDebugger.HttpServer" };
            _thread.Start();

            if (!_pumping)
            {
                EditorApplication.update += Pump;
                _pumping = true;
            }

            WriteInfoFile();
            AdbLog.Info("调试服务已启动：" + BaseUrl + "（端口 " + port + "，令牌已设置）");
            return true;
        }

        internal void Stop()
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

            if (_pumping)
            {
                EditorApplication.update -= Pump;
                _pumping = false;
            }

            // 队列里等着的请求必须给出明确答复，否则客户端会一直挂着直到超时
            while (_queue.TryDequeue(out var item))
            {
                item.Completion.TrySetResult(AdbReply.Fail(503, "服务已停止"));
            }

            DeleteInfoFile();
            _port = 0;
        }

        /// <summary>换一个令牌（旧令牌立刻失效）</summary>
        internal string RegenerateToken()
        {
            var token = Guid.NewGuid().ToString("N");
            AdbDebuggerSettings.Token = token;
            _token = token;
            WriteInfoFile();
            return token;
        }

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
                    // Stop() 或监听器失效时 GetContext 抛异常，借此退出
                    break;
                }

                var captured = context;
                Task.Run(() =>
                {
                    try
                    {
                        HandleContext(captured);
                    }
                    catch (Exception e)
                    {
                        AdbLog.Warning("请求处理异常：" + e.Message);
                        TryWrite(captured, AdbReply.Fail(500, e.Message));
                    }
                });
            }
        }

        private void HandleContext(HttpListenerContext context)
        {
            var request = context.Request;

            // Host 头校验：挡掉通过恶意网页发起的 DNS rebinding
            var host = (request.Headers["Host"] ?? string.Empty).Trim().ToLowerInvariant();
            if (!IsHostAllowed(host))
            {
                TryWrite(context, AdbReply.Fail(403, "仅接受来自本机的请求"));
                return;
            }

            string path;
            string queryText;
            AdbDebugProtocol.SplitUrl(request.RawUrl, out path, out queryText);
            var query = AdbDebugProtocol.ParseQuery(queryText);

            // 令牌：优先 Header，其次 query（方便 curl 手测）
            var token = (request.Headers["X-Token"] ?? string.Empty).Trim();
            if (token.Length == 0)
            {
                token = AdbDebugProtocol.Get(query, "token", string.Empty).Trim();
            }

            if (!string.Equals(token, _token, StringComparison.Ordinal))
            {
                TryWrite(context, AdbReply.Fail(401, "令牌无效（从 .workbuddy/AdbDebug/.adb-info.* 里读 token）"));
                return;
            }

            var item = new AdbWorkItem
            {
                Method = (request.HttpMethod ?? "GET").ToUpperInvariant(),
                Path = (path ?? "/").TrimEnd('/'),
                Query = query
            };

            if (item.Path.Length == 0)
            {
                item.Path = "/";
            }

            if (_queue.Count >= MaxQueuedItems)
            {
                TryWrite(context, AdbReply.Fail(429, "请求队列已满，稍后再试"));
                return;
            }

            _queue.Enqueue(item);

            if (!item.Completion.Task.Wait(TimeSpan.FromSeconds(20)))
            {
                TryWrite(context, AdbReply.Fail(504, "主线程 20 秒内没有处理该请求（编辑器可能正卡在编译/导入）"));
                return;
            }

            TryWrite(context, item.Completion.Task.Result);
        }

        private static bool IsHostAllowed(string host)
        {
            if (host.Length == 0)
            {
                // 本地 HttpListener 一般都会带 Host；不带时按本机处理，避免误伤
                return true;
            }

            var name = host;
            var colon = host.LastIndexOf(':');
            if (colon > 0)
            {
                name = host.Substring(0, colon);
            }

            return string.Equals(name, "127.0.0.1", StringComparison.Ordinal)
                   || string.Equals(name, "localhost", StringComparison.Ordinal)
                   || string.Equals(name, "[::1]", StringComparison.Ordinal);
        }

        private static void TryWrite(HttpListenerContext context, AdbReply reply)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(reply.Body ?? string.Empty);
                context.Response.StatusCode = reply.StatusCode;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                context.Response.OutputStream.Close();
            }
            catch (Exception)
            {
                // 客户端提前断开时写响应会失败，没有补救手段
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch (Exception)
                {
                }
            }
        }

        #endregion

        #region 主线程泵

        private void Pump()
        {
            while (_queue.TryDequeue(out var item))
            {
                if (item == null)
                {
                    continue;
                }

                AdbReply reply;
                try
                {
                    reply = Route(item);
                }
                catch (Exception e)
                {
                    reply = AdbReply.Fail(500, "执行失败：" + e.Message);
                }

                item.Completion.TrySetResult(reply);
            }
        }

        /// <summary>路由（主线程执行，可以直接碰 EditorPrefs / Debug.Log）</summary>
        private static AdbReply Route(AdbWorkItem item)
        {
            var path = item.Path ?? "/";

            // 兼容 /v1/status 与 /status 两种写法，省得客户端拼错就 404
            if (EndsWith(path, AdbDebugProtocol.StatusPath) || EndsWith(path, "/status"))
            {
                return AdbReply.Ok(StatusJson());
            }

            if (EndsWith(path, AdbDebugProtocol.LogcatPath) || EndsWith(path, "/logcat"))
            {
                return HandleLogcat(item.Query);
            }

            if (EndsWith(path, AdbDebugProtocol.DevicesPath) || EndsWith(path, "/devices"))
            {
                return HandleDevices();
            }

            return AdbReply.Fail(404, "未知路径：" + path + "（支持 /v1/status、/v1/devices、/v1/logcat）");
        }

        private static bool EndsWith(string path, string suffix)
        {
            return path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        private static AdbReply HandleLogcat(Dictionary<string, string> query)
        {
            // 覆盖参数先落盘再启停：否则「带 tag 启动」会先用旧过滤条件跑起来
            ApplyOverrides(query);

            var action = (AdbDebugProtocol.Get(query, "action", "status") ?? "status").Trim().ToLowerInvariant();

            switch (action)
            {
                case "start":
                {
                    string error;
                    if (!AdbLogcatService.Start(out error))
                    {
                        return AdbReply.Fail(500, error ?? "启动失败");
                    }

                    return AdbReply.Ok(new JsonOut()
                        .Bool("ok", true)
                        .Str("message", "已启动 adb logcat，日志以 [ADB] 前缀打进 Unity 控制台")
                        .Str("command", AdbLogcatService.LastCommand)
                        .ToString());
                }

                case "stop":
                    AdbLogcatService.Stop();
                    return AdbReply.Ok(new JsonOut()
                        .Bool("ok", true)
                        .Str("message", "已停止 adb logcat")
                        .ToString());

                case "restart":
                    AdbLogcatService.Stop();
                    {
                        string error;
                        if (!AdbLogcatService.Start(out error))
                        {
                            return AdbReply.Fail(500, error ?? "重启失败");
                        }

                        return AdbReply.Ok(new JsonOut()
                            .Bool("ok", true)
                            .Str("message", "已重启 adb logcat")
                            .Str("command", AdbLogcatService.LastCommand)
                            .ToString());
                    }

                case "status":
                    return AdbReply.Ok(StatusJson());

                default:
                    return AdbReply.Fail(400, "未知的 action：" + action + "（start / stop / restart / status）");
            }
        }

        /// <summary>
        /// 设备列表。**同步扫一次**（主线程）：客户端问"现在连了哪台"时给它缓存里的旧答案没有意义。
        /// adb server 正常时这一步几十毫秒；冷启动最长 8 秒，仍在客户端 20 秒超时之内。
        /// </summary>
        private static AdbReply HandleDevices()
        {
            string error;
            var devices = AdbDevices.ScanNow(out error);
            var serial = (AdbDebuggerSettings.Serial ?? string.Empty).Trim();

            // 拼 JSON 的部分在 AdbDevices.Json 里：那层能离线单测，这层依赖 HttpListener 不能
            return AdbReply.Ok(AdbDevices.Json(devices, serial, error));
        }

        private static void ApplyOverrides(Dictionary<string, string> query)
        {
            if (AdbDebugProtocol.Has(query, "tag"))
            {
                AdbDebuggerSettings.Tag = AdbDebugProtocol.Get(query, "tag", string.Empty);
            }

            if (AdbDebugProtocol.Has(query, "level"))
            {
                AdbDebuggerSettings.Level = AdbDebugProtocol.Get(query, "level", "I");
            }

            if (AdbDebugProtocol.Has(query, "serial"))
            {
                AdbDebuggerSettings.Serial = AdbDebugProtocol.Get(query, "serial", string.Empty);
            }

            if (AdbDebugProtocol.Has(query, "backfill"))
            {
                AdbDebuggerSettings.Backfill = AdbDebugProtocol.GetInt(query, "backfill", AdbDebuggerSettings.DefaultBackfill);
            }

            if (AdbDebugProtocol.Has(query, "stack"))
            {
                AdbDebuggerSettings.StackLines = AdbDebugProtocol.GetInt(query, "stack", AdbDebuggerSettings.DefaultStackLines);
            }

            if (AdbDebugProtocol.Has(query, "suppressStack"))
            {
                AdbDebuggerSettings.SuppressStack = AdbDebugProtocol.GetBool(query, "suppressStack", true);
            }

            // tag / level / stack / suppressStack 是客户端侧行为，改完立刻生效
            AdbLogcatService.RefreshFilters();
        }

        /// <summary>状态 JSON（服务 + 抓取两段，客户端一次就能拿全）</summary>
        internal static string StatusJson()
        {
            var service = Instance;
            var serial = (AdbDebuggerSettings.Serial ?? string.Empty).Trim();
            // 设备数只读缓存，不现扫：客户端会高频轮询 status，每次都跑 adb 会把主线程拖住
            var devices = AdbDevices.Snapshot();
            var usable = 0;
            for (var i = 0; i < devices.Count; i++)
            {
                if (devices[i].Usable)
                {
                    usable++;
                }
            }

            var json = new JsonOut()
                .Bool("ok", true)
                .Str("tool", "adb-debug")
                .Str("url", service.BaseUrl)
                .Num("port", service.ActualPort)
                .Bool("running", service.IsRunning)
                .Bool("logcatEnabled", AdbDebuggerSettings.LogcatEnabled)
                .Bool("logcatRunning", AdbLogcatService.IsRunning)
                .Str("logcatState", AdbLogcatService.State)
                .Str("adb", AdbLogcatService.AdbPath ?? string.Empty)
                .Str("adbSource", AdbLogcatService.AdbSource ?? string.Empty)
                .Str("command", AdbLogcatService.LastCommand ?? string.Empty)
                .Str("serial", serial)
                .Bool("serialAuto", serial.Length == 0)
                .Num("deviceCount", devices.Count)
                .Num("usableDevices", usable)
                .Str("tag", AdbDebuggerSettings.Tag ?? string.Empty)
                .Str("level", AdbDebuggerSettings.Level ?? "I")
                .Num("backfill", AdbDebuggerSettings.Backfill)
                .Num("stackLines", AdbDebuggerSettings.StackLines)
                .Bool("suppressStack", AdbDebuggerSettings.SuppressStack)
                .Num("received", AdbLogcatService.ReceivedCount)
                .Num("printed", AdbLogcatService.PrintedCount)
                .Num("coalesced", AdbLogcatService.CoalescedCount)
                .Num("filtered", AdbLogcatService.FilteredCount)
                .Num("dropped", AdbLogcatService.DroppedCount)
                .Str("lastError", AdbLogcatService.LastError ?? string.Empty)
                .Str("notice", AdbLogcatService.Notice ?? string.Empty);

            return json.ToString();
        }

        #endregion

        #region 信息文件

        /// <summary>信息文件目录：<c>&lt;工程根&gt;/.workbuddy/AdbDebug/</c></summary>
        internal static string InfoDirectory
        {
            get
            {
                try
                {
                    var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                    return Path.Combine(root, ".workbuddy", "AdbDebug");
                }
                catch (Exception)
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>
        /// 把端口/令牌写到 <c>.adb-info.&lt;hash&gt;</c>。
        /// <para>
        /// 写两份：主文件是"当前公认目标"，<c>.adb-info.&lt;hash&gt;.&lt;pid&gt;</c> 是本实例自述。
        /// 同一工程多开编辑器时主文件会互相覆盖，客户端靠副本 + pid 存活判断挑对的那个。
        /// </para>
        /// </summary>
        internal static void WriteInfoFile()
        {
            if (!Instance.IsRunning)
            {
                return;
            }

            try
            {
                var directory = InfoDirectory;
                if (string.IsNullOrEmpty(directory))
                {
                    return;
                }

                Directory.CreateDirectory(directory);

                var body = new JsonOut()
                    .Str("tool", "adb-debug")
                    .Num("port", Instance.ActualPort)
                    .Str("token", Instance.Token)
                    .Str("url", Instance.BaseUrl)
                    .Num("pid", System.Diagnostics.Process.GetCurrentProcess().Id)
                    .Str("updatedUtc", DateTime.UtcNow.ToString("o"))
                    .ToString();

                var name = ".adb-info." + AdbDebuggerSettings.ProjectId;
                File.WriteAllText(Path.Combine(directory, name), body, new UTF8Encoding(false));
                File.WriteAllText(
                    Path.Combine(directory, name + "." + System.Diagnostics.Process.GetCurrentProcess().Id),
                    body,
                    new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                AdbLog.Warning("写信息文件失败：" + e.Message);
            }
        }

        internal static void DeleteInfoFile()
        {
            try
            {
                var directory = InfoDirectory;
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    return;
                }

                var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                var self = Path.Combine(directory, ".adb-info." + AdbDebuggerSettings.ProjectId + "." + pid);
                if (File.Exists(self))
                {
                    File.Delete(self);
                }

                // 主文件只在自己还持有服务时删除，避免把另一个编辑器实例的条目删掉
                var main = Path.Combine(directory, ".adb-info." + AdbDebuggerSettings.ProjectId);
                if (File.Exists(main) && !Instance.IsRunning)
                {
                    File.Delete(main);
                }
            }
            catch (Exception)
            {
                // 清理失败不影响使用
            }
        }

        #endregion
    }
}
