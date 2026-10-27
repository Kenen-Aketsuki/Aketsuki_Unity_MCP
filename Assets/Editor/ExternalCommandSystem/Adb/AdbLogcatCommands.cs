using System;

namespace CivilRev.CommandSystem.Editor
{
    /// <summary>
    /// <c>adb.logcat</c>：启停 / 查询 ADB logcat 抓取。
    ///
    /// <para>
    /// 日志本身是**逐条打进 Unity 控制台**的（前缀 <c>[ADB] </c>），所以想读内容不必再开一个接口：
    /// <c>console.read</c> 带 <c>contains: "[ADB]"</c> 就能捞到，级别也已经映射成
    /// Unity 的 Log / Warning / Error，<c>type: "error"</c> 同样能筛。
    /// </para>
    ///
    /// <para>
    /// <b>为什么不是运行时指令：</b>它操作的是主机上的 adb 进程，不碰正在跑的游戏状态，
    /// 所以不受「启用运行时指令」开关约束，编辑模式和 Play 模式下都能用。
    /// 但它同样默认关闭——要抓日志得先显式打开，这是本模块唯一的开关。
    /// </para>
    /// </summary>
    [Command("adb.logcat",
        Description = "启停 ADB logcat，把 Android 日志逐条打印到 Unity 控制台（默认关闭）",
        Usage = "action: string（start/stop/restart/status）, tag: string, level: string（V/D/I/W/E/F）, serial: string, backfill: int, stack: int（0=不打调用栈，>0=最多合并行数）, suppressStack: bool",
        EditorOnly = true)]
    public sealed class AdbLogcatCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var action = (context.Args.GetString("action", "status") ?? "status").Trim().ToLowerInvariant();

            // 覆盖参数先落盘再启停：否则"带 tag 启动"会先用旧过滤条件跑起来，再重启一次才生效
            var overridden = ApplyOverrides(context);

            if (context.DryRun)
            {
                return CommandResult.Skip("dry-run：未启停 adb logcat" +
                                          (overridden.Length > 0 ? "（将应用：" + overridden + "）" : string.Empty),
                    AdbLogcatService.StatusJson());
            }

            switch (action)
            {
                case "start":
                    return Start();

                case "stop":
                    AdbLogcatService.Stop();
                    return CommandResult.Ok("已停止 adb logcat", AdbLogcatService.StatusJson());

                case "restart":
                    AdbLogcatService.Stop();
                    return Start();

                case "status":
                    return Status();

                default:
                    return CommandResult.Fail(
                        "未知的 action：'" + action + "'，只支持 start / stop / restart / status");
            }
        }

        private static CommandResult Start()
        {
            if (!AdbLogcatService.Start(out var error))
            {
                return CommandResult.Fail(error ?? "启动 adb logcat 失败", AdbLogcatService.StatusJson());
            }

            return CommandResult.Ok(
                "已启动 adb logcat，日志将以 [ADB] 前缀逐条打印到 Unity 控制台（" +
                AdbLogcatService.LastCommand + "）",
                AdbLogcatService.StatusJson());
        }

        private static CommandResult Status()
        {
            var data = AdbLogcatService.StatusJson();

            string message;
            if (AdbLogcatService.IsRunning)
            {
                message = "adb logcat 正在运行：" + AdbLogcatService.SummaryText;
            }
            else if (!string.IsNullOrEmpty(AdbLogcatService.LastError))
            {
                message = "adb logcat 未运行，上次失败原因：" + AdbLogcatService.LastError;
            }
            else
            {
                message = "adb logcat 未运行（开关未开启，用 action=start 打开）";
            }

            return CommandResult.Ok(message, data);
        }

        /// <summary>把参数写回设置，并刷新过滤器缓存；返回一句人类可读的变更摘要</summary>
        private static string ApplyOverrides(CommandContext context)
        {
            var changes = string.Empty;

            if (context.Args.Has("tag"))
            {
                var tag = context.Args.GetString("tag", string.Empty) ?? string.Empty;
                CommandSystemSettings.AdbLogcatTag = tag;
                changes += "tag=" + (tag.Length == 0 ? "(清空)" : tag) + " ";
            }

            if (context.Args.Has("level"))
            {
                var level = NormalizeLevel(context.Args.GetString("level", "I"));
                CommandSystemSettings.AdbLogcatLevel = level;
                changes += "level=" + level + " ";
            }

            if (context.Args.Has("serial"))
            {
                var serial = context.Args.GetString("serial", string.Empty) ?? string.Empty;
                CommandSystemSettings.AdbLogcatSerial = serial;
                changes += "serial=" + (serial.Length == 0 ? "(自动)" : serial) + " ";
            }

            if (context.Args.Has("backfill"))
            {
                var backfill = context.Args.GetInt("backfill", 20);
                CommandSystemSettings.AdbLogcatBackfill = backfill;
                changes += "backfill=" + backfill + " ";
            }

            if (context.Args.Has("stack"))
            {
                var stack = context.Args.GetInt("stack", CommandSystemSettings.DefaultAdbStackLines);
                CommandSystemSettings.AdbLogcatStackLines = stack;
                changes += "stack=" + (stack <= 0 ? "0(不打调用栈)" : stack.ToString()) + " ";
            }

            if (context.Args.Has("suppressStack"))
            {
                var suppress = context.Args.GetBool("suppressStack", true);
                CommandSystemSettings.AdbSuppressStack = suppress;
                changes += "suppressStack=" + suppress + " ";
            }

            if (changes.Length > 0)
            {
                // tag / level / stack / suppressStack 都改完立刻生效；serial / backfill 要重启进程才作数
                AdbLogcatService.RefreshFilters();
            }

            return changes.Trim();
        }

        private static string NormalizeLevel(string level)
        {
            var text = (level ?? string.Empty).Trim().ToUpperInvariant();
            switch (text)
            {
                case "V":
                case "D":
                case "I":
                case "W":
                case "E":
                case "F":
                case "A":
                    return text;
                default:
                    throw new CommandException(
                        "level 只支持 V / D / I / W / E / F（A 表示 Assert），收到：" + (level ?? "null"));
            }
        }
    }
}
