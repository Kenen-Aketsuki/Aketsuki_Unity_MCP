using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// 一条 HTTP 请求的抽象表示
    /// <para>
    /// 故意不依赖 <c>System.Net</c>：真实的 <c>HttpListenerRequest</c> 由
    /// <c>CommandHttpServer</c> 转换成本类型，本类型则可以在离线测试工程里直接 new 出来，
    /// 于是整套路由 / 鉴权 / 响应构造都能脱离 Unity 验证。
    /// </para>
    /// </summary>
    public sealed class HttpRequest
    {
        /// <summary>请求方法，已统一大写</summary>
        public string Method { get; set; } = "GET";

        /// <summary>路径，已小写并去掉首尾斜杠，如 <c>"v1/execute"</c></summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>查询字符串，不含 '?'，如 <c>"dryRun=1"</c></summary>
        public string Query { get; set; } = string.Empty;

        /// <summary>请求体文本；GET 请求为空</summary>
        public string Body { get; set; } = string.Empty;

        /// <summary>请求头；键已转为小写，便于大小写无关地查找</summary>
        public readonly Dictionary<string, string> Headers =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>取一个请求头；不存在返回 null</summary>
        public string Header(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            string value;
            return Headers.TryGetValue(name.Trim().ToLowerInvariant(), out value) ? value : null;
        }

        /// <summary>取查询参数；不存在返回 null</summary>
        public string QueryValue(string name)
        {
            if (string.IsNullOrEmpty(Query) || string.IsNullOrEmpty(name))
            {
                return null;
            }

            var pairs = Query.Split('&');
            for (var i = 0; i < pairs.Length; i++)
            {
                var pair = pairs[i];
                if (pair.Length == 0)
                {
                    continue;
                }

                var eq = pair.IndexOf('=');
                var key = eq < 0 ? pair : pair.Substring(0, eq);
                var value = eq < 0 ? string.Empty : pair.Substring(eq + 1);
                if (string.Equals(Unescape(key).Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return Unescape(value);
                }
            }

            return null;
        }

        /// <summary>查询参数是否被置为真值（1 / true / yes，大小写无关）</summary>
        public bool QueryFlag(string name)
        {
            var value = QueryValue(name);
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            return string.Equals(value.Trim(), "1", StringComparison.Ordinal)
                   || string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(value.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>最小化的 percent-decoding，够用且不引入外部依赖</summary>
        internal static string Unescape(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            if (text.IndexOf('%') < 0 && text.IndexOf('+') < 0)
            {
                return text;
            }

            var builder = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '+' )
                {
                    builder.Append(' ');
                }
                else if (c == '%' && i + 2 < text.Length)
                {
                    int code;
                    if (int.TryParse(text.Substring(i + 1, 2), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out code))
                    {
                        builder.Append((char)code);
                        i += 2;
                        continue;
                    }

                    builder.Append(c);
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// 一条 HTTP 响应的抽象表示
    /// </summary>
    public sealed class HttpResponse
    {
        /// <summary>HTTP 状态码</summary>
        public int Status { get; set; } = 200;

        /// <summary>响应体（JSON 载荷）</summary>
        public JsonValue Payload { get; set; }

        /// <summary>序列化后的响应体文本</summary>
        public string Body
        {
            get { return Payload == null ? string.Empty : Payload.ToJson(false); }
        }

        /// <summary>构造 200 响应</summary>
        public static HttpResponse Ok(JsonValue payload)
        {
            return new HttpResponse { Status = 200, Payload = payload };
        }

        /// <summary>
        /// 构造错误响应
        /// </summary>
        /// <param name="status">HTTP 状态码</param>
        /// <param name="error">机器可读的短错误码，如 "unauthorized"</param>
        /// <param name="message">人类可读的说明</param>
        public static HttpResponse Fail(int status, string error, string message)
        {
            var payload = JsonValue.NewObject();
            payload.Set("ok", JsonValue.Of(false));
            payload.Set("error", JsonValue.Of(error ?? "error"));
            payload.Set("message", JsonValue.Of(message ?? string.Empty));
            return new HttpResponse { Status = status, Payload = payload };
        }
    }

    /// <summary>指令摘要信息（用于 /v1/commands 列举）</summary>
    public sealed class HttpCommandInfo
    {
        public string Id;
        public string Description;

        /// <summary>参数说明；供 MCP 工具 schema 与 help 输出使用</summary>
        public string Usage;
        public bool EditorOnly;

        /// <summary>
        /// 是否属于运行时（Play 模式）指令：受控制台开关与 Play 模式双重门禁。
        /// <para>客户端可据此在开关关闭时直接跳过，不必先吃一次失败。</para>
        /// </summary>
        public bool Runtime;

        /// <summary>
        /// 运行时指令要求的 Play 状态："Play 模式" / "编辑模式" / "任意"。
        /// <para>客户端据此提前判断——例如 <c>runtime.play</c> 只能在编辑模式调用。</para>
        /// </summary>
        public string PlayState;

        /// <summary>
        /// 是否属于敏感操作：未开「全权委托」时会弹窗等人点「允许」，超时按拒绝。
        /// <para>
        /// 客户端据此提前告知模型"这条会卡在人工确认上"，避免它把超时当成执行失败反复重试。
        /// </para>
        /// </summary>
        public bool RequiresConsent;
    }

    /// <summary><c>/v1/ping</c> 与 <c>/v1/status</c> 响应里身份块的字段名</summary>
    public static class HttpIdentityFields
    {
        /// <summary>
        /// 响应里的字段名
        /// <para>
        /// 客户端据此判断"连上的到底是不是能执行指令的那个进程"：
        /// 只认 <see cref="HttpIdentity.Interactive"/>（以及派生的 <c>executable</c>）为真者，
        /// 其余一律视为不能执行指令，避免指令石沉大海。
        /// </para>
        /// </summary>
        public const string Key = "identity";
    }

    /// <summary>
    /// HTTP 服务运行所需的宿主能力
    /// <para>
    /// 由 <c>CommandHttpServer</c>（Unity 编辑器侧）实现；协议层只依赖本接口，
    /// 因此离线测试可以注入假实现，不必启动真正的监听器。
    /// </para>
    /// </summary>
    public interface IHttpEnvironment
    {
        /// <summary>编辑器是否正在编译脚本</summary>
        bool IsCompiling { get; }

        /// <summary>AssetDatabase 是否正在刷新/导入</summary>
        bool IsUpdating { get; }

        /// <summary>队列中尚未执行的请求数</summary>
        int PendingCount { get; }

        /// <summary>最近一次运行的摘要（用于 /v1/status）</summary>
        string LastMessage { get; }

        /// <summary>期望的访问令牌；为空表示不校验</summary>
        string ExpectedToken { get; }

        /// <summary>实际监听端口</summary>
        int Port { get; }

        /// <summary>
        /// 本服务实例的身份自证（进程号 / 是否交互式 / 角色 / 工程路径）
        /// <para>
        /// 随 <c>ping</c> 与 <c>status</c> 一起返回，让客户端在发指令之前就能判断
        /// "这个端口背后的进程能不能执行指令"。拿不到身份时返回 null，客户端按"未知"降级处理。
        /// </para>
        /// </summary>
        HttpIdentity Identity { get; }

        /// <summary>
        /// 端口避让区间的起点与长度（<c>portRangeStart</c> / <c>portRangeCount</c>）
        /// <para>客户端据此扫描候选端口，不必硬编码任何端口号。</para>
        /// </summary>
        int PortRangeStart { get; }

        /// <summary>端口避让区间长度，见 <see cref="PortRangeStart"/></summary>
        int PortRangeCount { get; }

        /// <summary>已注册指令列表</summary>
        IEnumerable<HttpCommandInfo> Commands { get; }

        /// <summary>
        /// 当前正在执行的指令 ID；空闲时为 null / 空串。
        /// <para>由主线程写入、后台线程只读，用于 /v1/status 暴露执行进度。</para>
        /// </summary>
        string CurrentCommandId { get; }

        /// <summary>当前指令已执行时长（毫秒）；空闲时为 0</summary>
        long CurrentElapsedMs { get; }

        /// <summary>
        /// 取一次成功执行的报告缓存（按 requestId）；未命中返回 null。
        /// <para>用于 <c>POST /v1/execute?requestId=xxx</c> 的重放幂等：命中缓存则不二次执行。</para>
        /// </summary>
        JsonValue TryGetCachedReport(string requestId);

        /// <summary>缓存一次成功执行的报告（按 requestId）；实现方负责窗口期与容量上限</summary>
        void CacheReport(string requestId, JsonValue payload);

        /// <summary>
        /// 请求取消：置位后，执行器在下一个检查点干净退出（协作式，无法中断正在同步执行的单条指令）。
        /// </summary>
        void RequestCancel();

        /// <summary>
        /// 最近一次编译的结果摘要（ok / errorCount / finished / durationMs）。
        /// <para>供 /v1/status 一眼看出"上次编译有没有过"，完整明细走 asset.compileReport 指令。</para>
        /// </summary>
        JsonValue CompileSummary { get; }

        /// <summary>
        /// 运行时（Play 模式）指令的启用状态摘要（enabled / playing / commandCount）。
        /// <para>
        /// 供 <c>/v1/status</c> 一眼看出"我的 runtime.* 为什么调不动"——
        /// 是开关没开，还是编辑器没进 Play。省得客户端只能从失败文案里猜。
        /// </para>
        /// </summary>
        JsonValue RuntimeCommands { get; }

        /// <summary>
        /// 敏感操作确认的启用状态摘要（fullDelegation / timeoutSeconds / commandCount）。
        /// <para>
        /// 供 <c>/v1/status</c> 一眼看出"editor.play 为什么卡在弹窗上"：
        /// 全权委托没开 = 有一条模态窗正在等人点「允许」，而不是指令卡死。
        /// </para>
        /// </summary>
        JsonValue Consent { get; }

        /// <summary>把一段指令文本交给执行器（由主线程调用）</summary>
        CommandRunReport RunFromText(string text, bool dryRun);
    }

    /// <summary>
    /// HTTP 协议层：路由、鉴权、响应构造
    /// <para>
    /// 本类不监听任何端口、不起任何线程，也不触碰 Unity API —— 它只做"请求进、响应出"的纯函数式处理。
    /// 线程与生命周期由 <c>CommandHttpServer</c> 负责。
    /// </para>
    /// </summary>
    public static class HttpProtocol
    {
        /// <summary>协议版本，客户端可据此判断兼容性</summary>
        public const int ProtocolVersion = 1;

        /// <summary>令牌请求头名称</summary>
        public const string TokenHeader = "X-Cmd-Token";

        /// <summary>请求体大小上限（4 MB），超出直接拒绝，避免异常大包占满内存</summary>
        public const int MaxBodyBytes = 4 * 1024 * 1024;

        /// <summary>把路径规整成 "v1/execute" 形式：小写、去首尾斜杠、压缩重复斜杠</summary>
        public static string NormalizePath(string rawPath)
        {
            var text = (rawPath ?? string.Empty).Trim();
            var slash = text.IndexOf('?');
            if (slash >= 0)
            {
                text = text.Substring(0, slash);
            }

            var hash = text.IndexOf('#');
            if (hash >= 0)
            {
                text = text.Substring(0, hash);
            }

            var parts = text.Split('/');
            var kept = new List<string>(parts.Length);
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i].Trim();
                if (part.Length > 0)
                {
                    kept.Add(part.ToLowerInvariant());
                }
            }

            return string.Join("/", kept.ToArray());
        }

        /// <summary>从完整 URL 中分离出路径与查询串</summary>
        public static void SplitUrl(string rawUrl, out string path, out string query)
        {
            path = string.Empty;
            query = string.Empty;
            if (string.IsNullOrEmpty(rawUrl))
            {
                return;
            }

            var text = rawUrl.Trim();
            var hash = text.IndexOf('#');
            if (hash >= 0)
            {
                text = text.Substring(0, hash);
            }

            var question = text.IndexOf('?');
            if (question < 0)
            {
                path = NormalizePath(text);
                return;
            }

            path = NormalizePath(text.Substring(0, question));
            query = text.Substring(question + 1);
        }

        /// <summary>
        /// 令牌的恒定时间比较：逐字符 XOR 累加、不早退，避免通过响应耗时侧信道泄露令牌前缀。
        /// <para>长度不同先直接返回 false（GUID 定长，长度本身不敏感）；长度相同才逐字符比较。</para>
        /// </summary>
        public static bool TokenEquals(string provided, string expected)
        {
            if (provided == null || expected == null)
            {
                return false;
            }

            if (provided.Length != expected.Length)
            {
                return false;
            }

            var diff = 0;
            for (var i = 0; i < provided.Length; i++)
            {
                diff |= provided[i] ^ expected[i];
            }

            return diff == 0;
        }

        /// <summary>
        /// 处理一个请求，返回响应
        /// <para>本方法是纯逻辑，可在任意线程调用；真正执行指令的动作通过 <paramref name="env"/> 转交主线程。</para>
        /// </summary>
        public static HttpResponse Handle(HttpRequest request, IHttpEnvironment env)
        {
            if (request == null)
            {
                return HttpResponse.Fail(400, "bad_request", "请求为空");
            }

            if (env == null)
            {
                return HttpResponse.Fail(500, "no_environment", "服务尚未初始化");
            }

            // 鉴权先于一切：未配置令牌时不校验，但 status 里会暴露这个风险
            var expected = env.ExpectedToken;
            if (!string.IsNullOrEmpty(expected))
            {
                var provided = request.Header(TokenHeader);
                if (string.IsNullOrEmpty(provided)
                    || !TokenEquals(provided.Trim(), expected.Trim()))
                {
                    return HttpResponse.Fail(401, "unauthorized",
                        "缺少或错误的 " + TokenHeader + " 头");
                }
            }

            var path = request.Path ?? string.Empty;

            if (path == "v1/ping")
            {
                return RequireGet(request) ?? Ping(env);
            }

            if (path == "v1/status")
            {
                return RequireGet(request) ?? Status(env);
            }

            if (path == "v1/commands")
            {
                return RequireGet(request) ?? Commands(env);
            }

            if (path == "v1/execute")
            {
                return RequirePost(request) ?? Execute(request, env);
            }

            if (path == "v1/cancel")
            {
                return RequirePost(request) ?? Cancel(env);
            }

            var notFound = JsonValue.NewObject();
            notFound.Set("ok", JsonValue.Of(false));
            notFound.Set("error", JsonValue.Of("not_found"));
            notFound.Set("message", JsonValue.Of("未知路径：" + path));
            notFound.Set("known", JsonValue.FromArray(new[]
            {
                JsonValue.Of("v1/ping"),
                JsonValue.Of("v1/status"),
                JsonValue.Of("v1/commands"),
                JsonValue.Of("v1/execute"),
                JsonValue.Of("v1/cancel")
            }));
            return new HttpResponse { Status = 404, Payload = notFound };
        }

        #region 各端点

        private static HttpResponse Ping(IHttpEnvironment env)
        {
            var payload = JsonValue.NewObject();
            payload.Set("ok", JsonValue.Of(true));
            payload.Set("service", JsonValue.Of("Aketsuki.UnityMcp"));
            payload.Set("version", JsonValue.Of(ProtocolVersion));
            payload.Set("port", JsonValue.Of(env.Port));
            payload.Set("portRangeStart", JsonValue.Of(env.PortRangeStart));
            payload.Set("portRangeCount", JsonValue.Of(env.PortRangeCount));
            WriteIdentity(payload, env);
            return HttpResponse.Ok(payload);
        }

        private static HttpResponse Status(IHttpEnvironment env)
        {
            var payload = JsonValue.NewObject();
            payload.Set("ok", JsonValue.Of(true));
            payload.Set("port", JsonValue.Of(env.Port));
            payload.Set("portRangeStart", JsonValue.Of(env.PortRangeStart));
            payload.Set("portRangeCount", JsonValue.Of(env.PortRangeCount));
            payload.Set("compiling", JsonValue.Of(env.IsCompiling));
            payload.Set("updating", JsonValue.Of(env.IsUpdating));
            payload.Set("queue", JsonValue.Of(env.PendingCount));
            payload.Set("busy", JsonValue.Of(env.IsCompiling || env.IsUpdating));
            payload.Set("tokenRequired", JsonValue.Of(!string.IsNullOrEmpty(env.ExpectedToken)));
            payload.Set("last", JsonValue.Of(env.LastMessage ?? string.Empty));
            // 执行进度：卡住的指令一眼可见（主线程被占住时 elapsed 仍在增长，说明确实在跑）
            payload.Set("currentCommandId", JsonValue.Of(env.CurrentCommandId ?? string.Empty));
            payload.Set("currentElapsedMs", JsonValue.Of(env.CurrentElapsedMs));
            // 上次编译结果：AI 改完代码后可直接从 status 判断"编译过没过"
            var compile = env.CompileSummary;
            if (compile != null)
            {
                payload.Set("lastCompile", compile);
            }

            // 运行时指令开关：默认关闭，关着时 runtime.* 全部失败，客户端据此提前判定
            var runtime = env.RuntimeCommands;
            if (runtime != null)
            {
                payload.Set("runtimeCommands", runtime);
            }

            // 敏感操作（进/出 Play、开场景）：全权委托没开时会弹窗等人确认，
            // 客户端看到这个块就能解释"指令为什么停了十几秒还没回"。
            var consent = env.Consent;
            if (consent != null)
            {
                payload.Set("consent", consent);
            }

            WriteIdentity(payload, env);
            return HttpResponse.Ok(payload);
        }

        /// <summary>
        /// 把身份块写进响应；拿不到身份时写 null，客户端据此降级为"未知身份"
        /// </summary>
        private static void WriteIdentity(JsonValue payload, IHttpEnvironment env)
        {
            var identity = env == null ? null : env.Identity;
            payload.Set(HttpIdentityFields.Key,
                identity != null ? identity.ToJson() : JsonValue.Null);
        }

        private static HttpResponse Commands(IHttpEnvironment env)
        {
            var items = new List<JsonValue>();
            if (env.Commands != null)
            {
                foreach (var info in env.Commands)
                {
                    if (info == null || string.IsNullOrEmpty(info.Id))
                    {
                        continue;
                    }

                    var item = JsonValue.NewObject();
                    item.Set("id", JsonValue.Of(info.Id));
                    item.Set("description", JsonValue.Of(info.Description ?? string.Empty));
                    item.Set("usage", JsonValue.Of(info.Usage ?? string.Empty));
                    item.Set("editorOnly", JsonValue.Of(info.EditorOnly));
                    item.Set("runtime", JsonValue.Of(info.Runtime));
                    if (info.Runtime)
                    {
                        item.Set("playState", JsonValue.Of(info.PlayState ?? string.Empty));
                    }
                    if (info.RequiresConsent)
                    {
                        item.Set("requiresConsent", JsonValue.True);
                    }
                    items.Add(item);
                }
            }

            var payload = JsonValue.NewObject();
            payload.Set("ok", JsonValue.Of(true));
            payload.Set("count", JsonValue.Of(items.Count));
            payload.Set("commands", JsonValue.FromArray(items));
            return HttpResponse.Ok(payload);
        }

        private static HttpResponse Execute(HttpRequest request, IHttpEnvironment env)
        {
            var body = request.Body ?? string.Empty;
            if (body.Trim().Length == 0)
            {
                return HttpResponse.Fail(400, "empty_body", "请求体为空，应为指令文件的 JSON 文本");
            }

            if (body.Length > MaxBodyBytes)
            {
                return HttpResponse.Fail(413, "payload_too_large",
                    $"请求体超过上限 {MaxBodyBytes} 字节");
            }

            // 幂等重放：带 requestId 的请求若已成功执行过，直接返回缓存结果，不产生二次副作用。
            // 查询串优先（不侵入指令 JSON 结构），请求体顶层的 requestId 作为兼容写法。
            var requestId = request.QueryValue("requestId");
            if (string.IsNullOrEmpty(requestId) || requestId.Length > MaxRequestIdLength)
            {
                requestId = null;
            }

            if (requestId != null)
            {
                var cached = env.TryGetCachedReport(requestId);
                if (cached != null)
                {
                    return HttpResponse.Ok(cached);
                }
            }

            // 编辑器正在编译 / 刷新时主线程 pump 不会推进，入队只会等到超时，因此直接拒绝
            if (env.IsCompiling)
            {
                return HttpResponse.Fail(503, "compiling",
                    "Unity 正在编译脚本，请稍后重试（可用 /v1/status 查询）");
            }

            if (env.IsUpdating)
            {
                return HttpResponse.Fail(503, "updating",
                    "AssetDatabase 正在刷新，请稍后重试（可用 /v1/status 查询）");
            }

            var dryRun = request.QueryFlag("dryRun") || request.QueryFlag("dry");

            // 先做一次纯语法校验：格式错误属于客户端问题，应回 400 并带上行列号，
            // 而不是等主线程执行时才变成 500。校验通过后执行器还会再解析一次，开销可忽略。
            try
            {
                CommandReader.FromText(body, "<validate>");
            }
            catch (CommandFileException e)
            {
                return HttpResponse.Fail(400, "bad_json", e.Message);
            }

            CommandRunReport report;
            try
            {
                report = env.RunFromText(body, dryRun);
            }
            catch (Exception e)
            {
                return HttpResponse.Fail(500, "execute_failed", e.Message);
            }

            if (report == null)
            {
                return HttpResponse.Fail(500, "no_report", "执行器没有返回报告");
            }

            var payload = ToJson(report, dryRun);

            // 只缓存"成功"的完整报告：失败/中止的结果重放会掩盖真实状态，且重试语义应由调用方决定
            if (requestId != null && !dryRun && !report.HasError)
            {
                env.CacheReport(requestId, payload);
            }

            return HttpResponse.Ok(payload);
        }

        /// <summary>requestId 的长度上限，避免被当成任意大 key 撑爆缓存</summary>
        public const int MaxRequestIdLength = 128;

        /// <summary>
        /// 请求取消：只置位，不阻塞等待
        /// <para>
        /// 协作式：正在同步执行的单条指令无法被打断（.NET 没有安全的 Thread.Abort），
        /// 执行器会在下一个检查点（批次里的下一条指令前）看到标记并干净退出。
        /// </para>
        /// </summary>
        private static HttpResponse Cancel(IHttpEnvironment env)
        {
            env.RequestCancel();
            var payload = JsonValue.NewObject();
            payload.Set("ok", JsonValue.Of(true));
            payload.Set("message", JsonValue.Of(
                "已请求取消；当前同步执行的指令会在下一个检查点退出（若已卡死在单条指令内，需重启编辑器）"));
            return HttpResponse.Ok(payload);
        }

        #endregion

        #region 方法校验

        private static HttpResponse RequireGet(HttpRequest request)
        {
            if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                return HttpResponse.Fail(405, "method_not_allowed",
                    request.Path + " 只支持 GET，实际为 " + request.Method);
            }

            return null;
        }

        private static HttpResponse RequirePost(HttpRequest request)
        {
            if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                return HttpResponse.Fail(405, "method_not_allowed",
                    request.Path + " 只支持 POST，实际为 " + request.Method);
            }

            return null;
        }

        #endregion

        #region 报告序列化

        /// <summary>
        /// 把执行报告转成客户端可消费的 JSON
        /// <para>
        /// <c>ok</c> 表示业务结果（无失败项），与 HTTP 状态码分工：状态码只反映"请求是否被处理"。
        /// 客户端应先判断 HTTP 200，再读 <c>ok</c> 与逐条 <c>records</c>。
        /// </para>
        /// <para>
        /// DOC-001 后续：<c>ok</c> 是布尔值，混在几十行 JSON 里很容易被调用方（尤其是 LLM）
        /// 忽略，于是额外给出 <c>summary</c>（一句话结论，失败时带 ⚠ 前缀）和 <c>failures</c>
        /// （只含失败项的精简数组），两者都排在 <c>records</c> 之前——先读到结论，再决定要不要翻明细。
        /// 状态码仍统一 200：业务失败不改变 HTTP 语义，<c>batch</c> 与单条保持一致。
        /// </para>
        /// </summary>
        public static JsonValue ToJson(CommandRunReport report, bool dryRun = false)
        {
            var payload = JsonValue.NewObject();
            if (report == null)
            {
                payload.Set("ok", JsonValue.Of(false));
                payload.Set("error", JsonValue.Of("no_report"));
                payload.Set("summary", JsonValue.Of("⚠ 没有拿到执行报告（no_report）"));
                return payload;
            }

            payload.Set("ok", JsonValue.Of(!report.HasError));
            payload.Set("summary", JsonValue.Of(BuildSummary(report, dryRun)));
            payload.Set("dryRun", JsonValue.Of(dryRun));
            payload.Set("source", JsonValue.Of(report.SourcePath ?? "<memory>"));
            payload.Set("total", JsonValue.Of(report.Total));
            payload.Set("succeeded", JsonValue.Of(report.Succeeded));
            payload.Set("failed", JsonValue.Of(report.Failed));
            payload.Set("skipped", JsonValue.Of(report.Skipped));
            payload.Set("elapsedMs", JsonValue.Of(Math.Round(report.ElapsedMs, 2)));

            var records = new List<JsonValue>();
            var failures = new List<JsonValue>();
            for (var i = 0; i < report.Records.Count; i++)
            {
                var record = report.Records[i];
                records.Add(RecordToJson(record));
                if (record != null && !IsNonFailure(record.Status))
                {
                    failures.Add(FailureToJson(record));
                }
            }

            // 失败清单排在 records 前面：调用方按顺序读就能先看到失败原因
            if (failures.Count > 0)
            {
                payload.Set("failures", JsonValue.FromArray(failures));
            }

            payload.Set("records", JsonValue.FromArray(records));
            return payload;
        }

        /// <summary>成功与跳过都不算失败；其余（Failed / UnknownCommand / 任何新状态）一律算</summary>
        private static bool IsNonFailure(CommandStatus status)
        {
            return status == CommandStatus.Success || status == CommandStatus.Skipped;
        }

        /// <summary>
        /// 一句话结论，放在响应最前面。失败时以 ⚠ 开头并点名失败的指令，
        /// 不让调用方靠"扫 records 找 status"来发现失败（DOC-001 后续）。
        /// </summary>
        private static string BuildSummary(CommandRunReport report, bool dryRun)
        {
            if (dryRun)
            {
                return $"dry-run：共 {report.Total} 条（通过 {report.Succeeded} / 失败 {report.Failed} / 跳过 {report.Skipped}），未真正执行";
            }

            if (!report.HasError)
            {
                // 有跳过但一条都没真跑（典型：敏感操作等人工确认、或全批次被 dry-run 拦下）时，
                // 说"全部成功"会把人带偏——它其实什么都没做。
                if (report.Succeeded == 0 && report.Skipped > 0)
                {
                    return $"{report.Total} 条指令都没有真正执行（{report.Skipped} 条被跳过）";
                }

                return report.Skipped > 0
                    ? $"{report.Total} 条指令全部成功（其中 {report.Skipped} 条跳过）"
                    : $"{report.Total} 条指令全部成功";
            }

            var names = new List<string>();
            foreach (var record in report.Records)
            {
                if (record == null || IsNonFailure(record.Status))
                {
                    continue;
                }

                names.Add($"#{record.Index} {record.CommandId}");
                if (names.Count >= 3)
                {
                    break;
                }
            }

            var suffix = report.Failed > names.Count ? " 等" : string.Empty;
            return $"⚠ {report.Failed}/{report.Total} 条指令失败：{string.Join("、", names)}{suffix}（明细见 failures，逐条见 records）";
        }

        /// <summary>失败项精简视图：只留定位与原因，不含可能很大的 data</summary>
        private static JsonValue FailureToJson(CommandRecord record)
        {
            var item = JsonValue.NewObject();
            item.Set("index", JsonValue.Of(record.Index));
            item.Set("id", JsonValue.Of(record.CommandId ?? string.Empty));
            item.Set("status", JsonValue.Of(record.Status.ToString()));
            item.Set("message", JsonValue.Of(record.Message ?? string.Empty));
            return item;
        }

        /// <summary>单条执行记录转 JSON</summary>
        public static JsonValue RecordToJson(CommandRecord record)
        {
            var item = JsonValue.NewObject();
            if (record == null)
            {
                item.Set("index", JsonValue.Of(-1));
                item.Set("status", JsonValue.Of("Unknown"));
                return item;
            }

            item.Set("index", JsonValue.Of(record.Index));
            item.Set("id", JsonValue.Of(record.CommandId ?? string.Empty));
            item.Set("status", JsonValue.Of(record.Status.ToString()));
            item.Set("message", JsonValue.Of(record.Message ?? string.Empty));
            item.Set("elapsedMs", JsonValue.Of(Math.Round(record.ElapsedMs, 2)));
            item.Set("data", DataToJson(record.Data));
            return item;
        }

        /// <summary>
        /// 把指令返回的 <c>Data</c> 转成 JSON
        /// <para>Data 是 object，可能是字符串、数字，也可能是任意业务对象；无法安全结构化时退化为 ToString。</para>
        /// </summary>
        private static JsonValue DataToJson(object data)
        {
            if (data == null)
            {
                return JsonValue.Null;
            }

            // 指令已经构造好的结构化 JSON：原样嵌入，不再退化成 ToString
            if (data is JsonValue jsonValue)
            {
                return jsonValue;
            }

            // batch 这类复合指令的 Data 就是嵌套执行报告：结构化嵌入。
            // 否则 ToString 只给类型名，嵌套批次里哪条失败了完全看不到。
            if (data is CommandRunReport nestedReport)
            {
                return ToJson(nestedReport);
            }

            if (data is string s)
            {
                return JsonValue.Of(s);
            }

            if (data is bool b)
            {
                return JsonValue.Of(b);
            }

            if (data is int i)
            {
                return JsonValue.Of(i);
            }

            if (data is long l)
            {
                return JsonValue.Of(l);
            }

            if (data is float f)
            {
                return JsonValue.Of((double)f);
            }

            if (data is double d)
            {
                return JsonValue.Of(d);
            }

            // 其它类型不强行反射，直接给可读文本，保证响应永不因序列化失败而崩掉
            return JsonValue.Of(data.ToString());
        }

        #endregion
    }
}
