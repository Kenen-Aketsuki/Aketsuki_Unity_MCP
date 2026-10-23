using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace CivilRev.CommandSystem.Editor
{
    /// <summary>一条编译消息（错误或警告）</summary>
    public sealed class CompileMessage
    {
        /// <summary>产生消息的程序集名（Assembly-CSharp / Assembly-CSharp-Editor ...）</summary>
        public string Assembly;

        /// <summary>Error / Warning / Info</summary>
        public string Type;

        /// <summary>编译器原文，含行列号</summary>
        public string Message;

        /// <summary>源文件（相对工程根，便于直接喂给 file.read / 编辑器）</summary>
        public string File;

        public int Line;
        public int Column;
    }

    /// <summary>
    /// 最近一次编译的结果快照
    /// </summary>
    public sealed class CompileReport
    {
        public bool Started;
        public bool Finished;

        /// <summary>没有 Error 即为 true</summary>
        public bool Ok = true;

        public string FinishedUtc;
        public double DurationMs;
        public int ErrorCount;
        public int WarningCount;
        public List<CompileMessage> Messages = new List<CompileMessage>();

        /// <summary>最近一次「请求编译」的时间（无论有没有真的编译）。Play 模式下被拒时它就是请求时刻。</summary>
        public string RequestedUtc;

        /// <summary>最近一次编译请求被拒的原因；空串表示没被拒。</summary>
        public string RejectedReason;

        /// <summary>
        /// 转成 JSON；<paramref name="includeMessages"/> 为 false 时只给计数（用于 status 摘要）。
        /// <para>
        /// DOC-001：编译失败/被拒时 HTTP 仍是 200，<c>requestedUtc</c> 与 <c>rejectedReason</c>
        /// 是调用方区分「上一次真的编译过」和「这次被挡回来了」的唯一信号。
        /// </para>
        /// </summary>
        public JsonValue ToJson(bool includeMessages)
        {
            var payload = JsonValue.NewObject();
            payload.Set("started", JsonValue.Of(Started));
            payload.Set("finished", JsonValue.Of(Finished));
            payload.Set("ok", JsonValue.Of(Ok));
            payload.Set("errorCount", JsonValue.Of(ErrorCount));
            payload.Set("warningCount", JsonValue.Of(WarningCount));
            payload.Set("durationMs", JsonValue.Of(Math.Round(DurationMs, 1)));
            payload.Set("finishedUtc", JsonValue.Of(FinishedUtc ?? string.Empty));
            payload.Set("requestedUtc", JsonValue.Of(RequestedUtc ?? string.Empty));
            payload.Set("rejectedReason", JsonValue.Of(RejectedReason ?? string.Empty));

            if (!includeMessages)
            {
                return payload;
            }

            var items = new JsonValue[Messages.Count];
            for (var i = 0; i < Messages.Count; i++)
            {
                var message = Messages[i];
                var item = JsonValue.NewObject();
                item.Set("assembly", JsonValue.Of(message.Assembly ?? string.Empty));
                item.Set("type", JsonValue.Of(message.Type ?? string.Empty));
                item.Set("message", JsonValue.Of(message.Message ?? string.Empty));
                item.Set("file", JsonValue.Of(message.File ?? string.Empty));
                item.Set("line", JsonValue.Of(message.Line));
                item.Set("column", JsonValue.Of(message.Column));
                items[i] = item;
            }

            payload.Set("messages", JsonValue.FromArray(items));
            return payload;
        }
    }

    /// <summary>
    /// 编译结果监控：把 Unity 编译管线的结构化消息留下来，供 AI 的"编辑-编译-纠错"闭环查询。
    ///
    /// <para>
    /// 为什么不能只靠日志文件：Editor.log 在长会话里能涨到几百 MB，从头扫很慢；不同平台路径也不同。
    /// <c>CompilationPipeline.assemblyCompilationErrors</c> 直接给出「程序集 + 文件 + 行列 + 消息」的结构化数据，
    /// 是这条链路最可靠的来源。
    /// </para>
    ///
    /// <para>
    /// 关键机制：**编译失败不会触发域重载**，所以本类的静态状态在失败后仍然有效，
    /// 旧程序集里的服务也还活着，能正常回答"编译错在哪"。
    /// 编译成功会重载域、静态状态清空——但那时的结果对 AI 没有价值。
    /// 为了「上次成功编译于何时」也能查到，摘要另存一份到 <see cref="SessionState"/>（会话级，重启清空）。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class CommandCompileMonitor
    {
        private const string SessionKey = "CivilRev.CommandSystem.LastCompile";

        private static readonly object Gate = new object();
        private static readonly List<CompileMessage> Current = new List<CompileMessage>();

        private static bool _compiling;
        private static DateTime _startedUtc;
        private static CompileReport _last = new CompileReport { Started = false, Finished = false };

        static CommandCompileMonitor()
        {
            _last = LoadSessionSummary();

            CompilationPipeline.compilationStarted += OnCompilationStarted;
            // 注意：API 名是 assemblyCompilationFinished（每编完一个程序集触发一次，messages 里带错误与警告），
            // 不是 assemblyCompilationErrors——后者在 Unity 的公开 API 里并不存在。
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;

            // 兜底：极少数情况下 compilationFinished 没触发，靠"isCompiling 由真变假"收尾
            EditorApplication.update += OnEditorUpdate;
        }

        /// <summary>最近一次编译的结果（含完整消息列表）</summary>
        public static CompileReport Last
        {
            get
            {
                lock (Gate)
                {
                    return _last;
                }
            }
        }

        /// <summary>
        /// 记一次「编译请求被拒」（DOC-001）。
        /// <para>
        /// 被拒时不会有任何编译发生，`finishedUtc` 仍是**上一次**的时间戳，调用方看不出这次没编译。
        /// 这里保留上一次的结果数据不动，只补 `requestedUtc` 与 `rejectedReason`——
        /// 「requestedUtc 比 finishedUtc 新」就是"这次被挡回来了"的判据。
        /// </para>
        /// </summary>
        public static void RecordRejected(string reason)
        {
            CompileReport snapshot;
            lock (Gate)
            {
                var previous = _last ?? new CompileReport();
                snapshot = new CompileReport
                {
                    Started = previous.Started,
                    Finished = previous.Finished,
                    Ok = previous.Ok,
                    ErrorCount = previous.ErrorCount,
                    WarningCount = previous.WarningCount,
                    DurationMs = previous.DurationMs,
                    FinishedUtc = previous.FinishedUtc,
                    RequestedUtc = DateTime.UtcNow.ToString("o"),
                    RejectedReason = reason ?? string.Empty,
                    Messages = previous.Messages ?? new List<CompileMessage>()
                };

                _last = snapshot;
            }

            SaveSessionSummary(snapshot);
            CommandLog.Warning($"[编译监控] 编译请求被拒：{snapshot.RejectedReason}");
        }

        /// <summary>编译是否正在进行（含 Unity 自身判定）</summary>
        public static bool IsCompiling
        {
            get
            {
                lock (Gate)
                {
                    return _compiling;
                }
            }
        }

        #region 管线回调（均在主线程）

        private static void OnCompilationStarted(object context)
        {
            lock (Gate)
            {
                Current.Clear();
                _compiling = true;
                _startedUtc = DateTime.UtcNow;
            }
            CommandLog.Info("[编译监控] 编译开始");
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null || messages.Length == 0)
            {
                return;
            }

            var assembly = Path.GetFileNameWithoutExtension(assemblyPath ?? string.Empty);

            lock (Gate)
            {
                for (var i = 0; i < messages.Length; i++)
                {
                    var message = messages[i];
                    Current.Add(new CompileMessage
                    {
                        Assembly = assembly,
                        Type = message.type.ToString(),
                        Message = StripPositionPrefix(message.message),
                        File = ToProjectRelative(message.file),
                        Line = message.line,
                        Column = message.column
                    });
                }
            }
        }

        private static void OnCompilationFinished(object context)
        {
            Finish("pipeline");
        }

        private static void OnEditorUpdate()
        {
            bool compiling;
            lock (Gate)
            {
                compiling = _compiling;
            }

            if (!compiling || EditorApplication.isCompiling)
            {
                return;
            }

            // 管线没发 compilationFinished 也要收尾，否则 status 永远显示"编译中"
            Finish("update-fallback");
        }

        private static void Finish(string reason)
        {
            CompileReport report;
            lock (Gate)
            {
                if (!_compiling)
                {
                    return;
                }

                _compiling = false;

                var errors = 0;
                var warnings = 0;
                var messages = new List<CompileMessage>(Current.Count);
                for (var i = 0; i < Current.Count; i++)
                {
                    var message = Current[i];
                    messages.Add(message);
                    if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                    {
                        errors++;
                    }
                    else if (string.Equals(message.Type, "Warning", StringComparison.OrdinalIgnoreCase))
                    {
                        warnings++;
                    }
                }

                // 错误排在前面，同类按文件/行排序，AI 读起来更有条理
                messages.Sort((a, b) =>
                {
                    var byType = string.Compare(b.Type, a.Type, StringComparison.Ordinal); // Error < Warning
                    if (byType != 0)
                    {
                        return byType;
                    }

                    var byFile = string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase);
                    return byFile != 0 ? byFile : a.Line.CompareTo(b.Line);
                });

                report = new CompileReport
                {
                    Started = true,
                    Finished = true,
                    Ok = errors == 0,
                    ErrorCount = errors,
                    WarningCount = warnings,
                    DurationMs = (DateTime.UtcNow - _startedUtc).TotalMilliseconds,
                    FinishedUtc = DateTime.UtcNow.ToString("o"),
                    RequestedUtc = _startedUtc.ToString("o"),
                    RejectedReason = string.Empty,
                    Messages = messages
                };

                _last = report;
                Current.Clear();
            }

            SaveSessionSummary(report);

            if (report.Ok)
            {
                CommandLog.Info($"[编译监控] 编译成功（{report.Messages.Count} 条消息，{report.DurationMs:F0}ms，{reason}）");
            }
            else
            {
                CommandLog.Error($"[编译监控] 编译失败：{report.ErrorCount} 个错误，{report.WarningCount} 个警告");
                for (var i = 0; i < report.Messages.Count && i < 5; i++)
                {
                    var message = report.Messages[i];
                    CommandLog.Error($"  {message.File}({message.Line},{message.Column}): {message.Message}");
                }
            }
        }

        #endregion

        #region 辅助

        /// <summary>
        /// 剥掉编译器原文里的路径前缀。
        /// <para>
        /// 编译器给的是 <c>D:\...\x.cs(5,25): error CS1002: ; expected</c>，
        /// 而我们已经把 file/line/column 结构化存下了，留着前缀会让展示重复一遍路径。
        /// </para>
        /// </summary>
        private static string StripPositionPrefix(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }

            var errorAt = message.IndexOf(": error ", StringComparison.OrdinalIgnoreCase);
            if (errorAt > 0)
            {
                return message.Substring(errorAt + 2);
            }

            var warningAt = message.IndexOf(": warning ", StringComparison.OrdinalIgnoreCase);
            if (warningAt > 0)
            {
                return message.Substring(warningAt + 2);
            }

            return message;
        }

        /// <summary>把绝对路径转成相对工程根的写法（Assets/...），没有工程前缀就原样返回</summary>
        private static string ToProjectRelative(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            try
            {
                var root = CommandPathUtil.ProjectRoot;
                if (string.IsNullOrEmpty(root))
                {
                    return path;
                }

                var full = Path.GetFullPath(path).Replace('\\', '/');
                var normalizedRoot = Path.GetFullPath(root).Replace('\\', '/').TrimEnd('/') + "/";
                if (full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return full.Substring(normalizedRoot.Length);
                }

                return full;
            }
            catch (Exception)
            {
                return path;
            }
        }

        /// <summary>摘要写入 SessionState：编译成功会重载域并清空静态字段，摘要靠它跨重载保留</summary>
        private static void SaveSessionSummary(CompileReport report)
        {
            try
            {
                var payload = report.ToJson(false);
                SessionState.SetString(SessionKey, payload.ToJson(false));
            }
            catch (Exception)
            {
                // 存不下不影响主流程
            }
        }

        private static CompileReport LoadSessionSummary()
        {
            try
            {
                var raw = SessionState.GetString(SessionKey, string.Empty);
                if (string.IsNullOrEmpty(raw))
                {
                    return new CompileReport { Started = false, Finished = false };
                }

                var json = JsonValue.Parse(raw);
                if (json == null || !json.IsObject)
                {
                    return new CompileReport { Started = false, Finished = false };
                }

                return new CompileReport
                {
                    Started = json.GetBool("started", false),
                    Finished = json.GetBool("finished", false),
                    Ok = json.GetBool("ok", true),
                    ErrorCount = json.GetInt("errorCount", 0),
                    WarningCount = json.GetInt("warningCount", 0),
                    DurationMs = json.GetDouble("durationMs", 0),
                    FinishedUtc = json.GetString("finishedUtc", string.Empty),
                    RequestedUtc = json.GetString("requestedUtc", string.Empty),
                    RejectedReason = json.GetString("rejectedReason", string.Empty),
                    Messages = new List<CompileMessage>()
                };
            }
            catch (Exception)
            {
                return new CompileReport { Started = false, Finished = false };
            }
        }

        #endregion
    }
}
