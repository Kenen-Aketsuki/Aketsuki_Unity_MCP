using System;
using UnityEditor;

namespace CivilRev.CommandSystem.Editor
{
    /// <summary>
    /// 读取最近一次编译的结果。
    /// <para>
    /// 这是"AI 编辑-编译-纠错"闭环的落点：改完 .cs → asset.compile →
    /// 轮询到空闲 → 调本指令拿到「成功 / 失败 + 文件:行:列 + 编译器原文」。
    /// </para>
    /// <para>
    /// 指令本身只要"能读到报告"就算成功；编译是否通过体现在返回的 <c>data.ok</c> 里。
    /// 这样调用方可以区分"查询失败"和"编译失败"。
    /// </para>
    /// </summary>
    [Command("asset.compileReport", Description = "读取最近一次编译结果（错误/警告明细）",
        Usage = "includeMessages: bool, limit: int", EditorOnly = true)]
    public sealed class CompileReportCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var includeMessages = context.Args.GetBool("includeMessages", true);
            var limit = context.Args.GetInt("limit", 0);

            var report = CommandCompileMonitor.Last;
            var payload = report.ToJson(includeMessages);

            if (limit > 0 && report.Messages != null && report.Messages.Count > limit)
            {
                // 只截断返回给调用方的部分，监控器里仍保留完整列表
                var trimmed = new JsonValue[limit];
                for (var i = 0; i < limit; i++)
                {
                    trimmed[i] = MessageToJson(report.Messages[i]);
                }

                payload.Set("messages", JsonValue.FromArray(trimmed));
                payload.Set("truncated", JsonValue.Of(true));
                payload.Set("totalMessages", JsonValue.Of(report.Messages.Count));
            }

            // BUG-004：被拒的判定必须排在最前面。
            // 被拒时 Finished / Ok 描述的都是**上一次**的编译，先判它们会把"这次压根没编译"
            // 说成"最近一次编译成功"——闭环的最后一步就是这个摘要，看错会以为改完的代码过了。
            string message;
            if (!string.IsNullOrEmpty(report.RejectedReason))
            {
                var lastReal = report.Finished
                    ? $"最近一次真正完成的编译在 {FormatUtc(report.FinishedUtc)}（ok={report.Ok}）"
                    : "本次会话还没有完成过任何编译";
                message = $"这一次编译请求被拒，代码没有被编译：{report.RejectedReason}；{lastReal}";
            }
            else if (!report.Finished)
            {
                message = CommandCompileMonitor.IsCompiling
                    ? "正在编译中，暂无结果"
                    : "还没有完成过一次编译（可先执行 asset.compile）";
            }
            else if (report.Ok)
            {
                message = $"最近一次编译成功（{report.WarningCount} 个警告，{report.DurationMs:F0}ms）";
            }
            else
            {
                message = $"最近一次编译失败：{report.ErrorCount} 个错误，{report.WarningCount} 个警告";
            }

            return CommandResult.Ok(message, payload);
        }

        /// <summary>
        /// 把 <c>DateTime.ToString("o")</c> 的 UTC 串显示成本地时间的短格式。
        /// <para>
        /// 摘要是给人/AI 直接读的，原始串带 7 位小数和时区后缀（<c>2026-10-24T02:50:03.5906584Z</c>），
        /// 放在"最近一次完成于…"里既吵又容易和本地时间对不上。解析失败就原样返回，不做猜测。
        /// </para>
        /// </summary>
        private static string FormatUtc(string utc)
        {
            if (string.IsNullOrEmpty(utc))
            {
                return "（未知时间）";
            }

            DateTime parsed;
            if (!DateTime.TryParse(utc, null, System.Globalization.DateTimeStyles.RoundtripKind, out parsed))
            {
                return utc;
            }

            return parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }

        private static JsonValue MessageToJson(CompileMessage message)
        {
            var item = JsonValue.NewObject();
            item.Set("assembly", JsonValue.Of(message.Assembly ?? string.Empty));
            item.Set("type", JsonValue.Of(message.Type ?? string.Empty));
            item.Set("message", JsonValue.Of(message.Message ?? string.Empty));
            item.Set("file", JsonValue.Of(message.File ?? string.Empty));
            item.Set("line", JsonValue.Of(message.Line));
            item.Set("column", JsonValue.Of(message.Column));
            return item;
        }
    }

    /// <summary>
    /// 读取最近的编辑器 / 运行时日志。
    /// <para>
    /// 编译错误请用 <c>asset.compileReport</c>；本指令用于「代码编译过了，但跑起来报错」的场景
    /// （NullReferenceException、缺失资源、断言失败等）。
    /// </para>
    /// </summary>
    [Command("console.read", Description = "读取最近的编辑器/运行时日志（异常、报错）",
        Usage = "count: int, type: string, contains: string, clear: bool", EditorOnly = true)]
    public sealed class ReadConsoleCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var count = context.Args.GetInt("count", 30);
            var type = context.Args.GetString("type", "all");
            var contains = context.Args.GetString("contains", null);
            var clear = context.Args.GetBool("clear", false);

            var items = CommandConsoleBuffer.Snapshot(count, type, contains, clear);
            var length = items.IsArray ? items.ArrayValue.Count : 0;

            return CommandResult.Ok(
                $"读取到 {length} 条日志（type={type}）",
                items);
        }
    }
}
