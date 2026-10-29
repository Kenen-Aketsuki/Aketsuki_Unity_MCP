using System.Collections.Generic;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// <c>adb.*</c> 五条指令的公共基座：**唯一职责**是说明"我依赖哪个可选插件"。
    ///
    /// <para>
    /// 判定与剔除全在 <see cref="OptionalDependencyCommand"/> / <see cref="CommandRegistry"/> 里，
    /// 这里不写一行判断代码 —— 以后加同类插件（录屏、真机截图、Profiler 采样……）也只需照抄这三行。
    /// </para>
    ///
    /// <para>
    /// 没装 ADB 调试台就不注册：这五条不会出现在 <c>/v1/commands</c>、编辑器控制台窗口，
    /// 也不会被 <c>unity_mcp_server.py</c> 变成 MCP 工具。锁死这一点很重要：列表里挂着一个
    /// **必然失败**的 <c>unity_adb_devices</c>，比它压根不出现更误导人（AI 会以为能抓日志，
    /// 调了才发现要装包，然后花几轮去验证是不是自己参数写错了）。
    /// </para>
    ///
    /// <para>
    /// 装包之后 Unity 会重新编译 → 域重载 → 重新扫描，五条指令自动出现，不需要重启编辑器。
    /// </para>
    /// </summary>
    public abstract class AdbCommandBase : OptionalDependencyCommand
    {
        protected override OptionalDependency Dependency => AdbBridgeAdapter.Plugin;
    }

    /// <summary>
    /// ADB 调试适配指令。
    ///
    /// <para>
    /// <b>为什么指令系统里只有"适配层"：</b>真正的设备扫描与 logcat 抓取在「ADB 调试台」那个独立包里，
    /// 它本来也能单独安装、单独使用（手工开窗口看日志）。这里只做两件事：把它的能力翻译成
    /// 指令 ID 让 <c>unity_mcp_server.py</c> 自动变成 MCP 工具；以及在没装它时给出明确原因。
    /// 所以本文件**一行 ADB 逻辑都不该有**。
    /// </para>
    ///
    /// <para>
    /// <b>MCP 工具名：</b><c>adb.devices</c> → <c>unity_adb_devices</c>，
    /// 其余同理（adapter 会按已注册指令自动生成工具，不用改 server）。
    /// </para>
    /// </summary>
    [Command("adb.devices",
        Description = "列出 Android 设备（真机 / 模拟器）：序列号、状态、型号，以及 adb 路径与失败原因",
        Usage = "refresh: bool（默认 true 现场扫；false 用上次缓存）",
        EditorOnly = true)]
    public sealed class AdbDevicesCommand : AdbCommandBase
    {
        public override CommandResult Execute(CommandContext context)
        {
            var args = new Dictionary<string, string>();
            if (context.Args.Has("refresh"))
            {
                args["refresh"] = context.Args.GetBool("refresh", true) ? "true" : "false";
            }

            var result = AdbBridgeAdapter.Run("devices", args);
            if (!result.Success)
            {
                return result;
            }

            var data = (JsonValue)result.Data;
            var count = data["count"].AsInt(0);
            var usable = data["usableCount"].AsInt(0);
            var error = data["error"].AsString(string.Empty);

            string message;
            if (count == 0)
            {
                message = string.IsNullOrEmpty(error)
                    ? "没发现设备：插好数据线或启动模拟器后重试；USB 调试没开时列表也是空的"
                    : "没发现设备：" + error;
            }
            else
            {
                message = count + " 台设备（" + usable + " 台可用）：" + AdbBridgeAdapter.DescribeDevices(data);
                if (usable > 1)
                {
                    message += "。多台可用时必须显式指定 serial（不指定 adb 会因不唯一直接失败）";
                }
            }

            return CommandResult.Ok(message, data);
        }
    }

    /// <summary>
    /// 开始把 logcat 抓进 Unity 控制台。可顺手切换设备 / tag / 级别，
    /// 这样"换个设备再看"只调一次就够，不必先改设置再单独启动。
    /// </summary>
    [Command("adb.logcat.start",
        Description = "开始抓 Android logcat（日志以 [ADB] 前缀进 Unity 控制台，并留一份供 adb.logcat.read 读）",
        Usage = "serial: string, tag: string（默认 Unity；空=不过滤）, level: V|D|I|W|E|F|A, backfill: int",
        EditorOnly = true)]
    public sealed class AdbLogcatStartCommand : AdbCommandBase
    {
        public override CommandResult Execute(CommandContext context)
        {
            var args = new Dictionary<string, string>();
            Copy(context, args, "serial");
            Copy(context, args, "tag");
            Copy(context, args, "level");
            Copy(context, args, "backfill");

            var result = AdbBridgeAdapter.Run("logcat.start", args);
            if (!result.Success)
            {
                return result;
            }

            var data = (JsonValue)result.Data;
            var message = data["message"].AsString("已开始抓取");
            var command = data["command"].AsString(string.Empty);
            if (!string.IsNullOrEmpty(command))
            {
                message += "。实际命令：" + command;
            }

            return CommandResult.Ok(message, data);
        }

        private static void Copy(CommandContext context, IDictionary<string, string> args, string key)
        {
            if (!context.Args.Has(key))
            {
                return;
            }

            // 统一转字符串：tag 允许空串（= 不过滤），所以不能因为"看起来没值"就跳过
            args[key] = context.Args.GetString(key, string.Empty) ?? string.Empty;
        }
    }

    [Command("adb.logcat.stop",
        Description = "停止 logcat 抓取（已抓到的日志不会丢，仍可用 adb.logcat.read 读走）",
        EditorOnly = true)]
    public sealed class AdbLogcatStopCommand : AdbCommandBase
    {
        public override CommandResult Execute(CommandContext context)
        {
            var result = AdbBridgeAdapter.Run("logcat.stop", null);
            if (!result.Success)
            {
                return result;
            }

            var data = (JsonValue)result.Data;
            return CommandResult.Ok(data["message"].AsString("已停止抓取"), data);
        }
    }

    [Command("adb.logcat.status",
        Description = "查看 logcat 抓取状态：是否在跑、连的哪台设备、tag/级别、各项计数与最近的错误",
        EditorOnly = true)]
    public sealed class AdbLogcatStatusCommand : AdbCommandBase
    {
        public override CommandResult Execute(CommandContext context)
        {
            var result = AdbBridgeAdapter.Run("logcat.status", null);
            if (!result.Success)
            {
                return result;
            }

            var data = (JsonValue)result.Data;
            var message = data["summary"].AsString("（无摘要）");

            var notice = data["notice"].AsString(string.Empty);
            if (!string.IsNullOrEmpty(notice))
            {
                message += "。adb 提示：" + notice;
            }

            return CommandResult.Ok(message, data);
        }
    }

    /// <summary>
    /// 取走缓冲里的日志。**取走即删除**：同一批不会被读第二次，
    /// 否则模型会把上次的错误当成新刷的一屏。
    /// </summary>
    [Command("adb.logcat.read",
        Description = "取走已抓到的 logcat（取走即删除），返回级别 / tag / 合并后的正文",
        Usage = "max: int（默认 200，上限 2000）",
        EditorOnly = true)]
    public sealed class AdbLogcatReadCommand : AdbCommandBase
    {
        public override CommandResult Execute(CommandContext context)
        {
            var args = new Dictionary<string, string>();
            if (context.Args.Has("max"))
            {
                args["max"] = context.Args.GetInt("max", 200).ToString();
            }

            var result = AdbBridgeAdapter.Run("logcat.read", args);
            if (!result.Success)
            {
                return result;
            }

            var data = (JsonValue)result.Data;
            var count = data["count"].AsInt(0);
            var remaining = data["remaining"].AsInt(0);
            var dropped = data["dropped"].AsInt(0);

            if (count == 0)
            {
                var running = data["running"].AsBool(false);
                return CommandResult.Ok(
                    running
                        ? "这批没有新日志（抓取在跑，可能当前没有输出；也可调大 max 或稍后再取）"
                        : "没有新日志，而且当前没在抓——先调 adb.logcat.start",
                    data);
            }

            var message = "取走 " + count + " 条（缓冲剩余 " + remaining + "）";

            var errors = 0;
            var warnings = 0;
            var lines = data["lines"];
            if (lines.IsArray)
            {
                foreach (var line in lines.ArrayValue)
                {
                    var level = line["level"].AsString(string.Empty);
                    if (level == "E" || level == "F" || level == "A")
                    {
                        errors++;
                    }
                    else if (level == "W")
                    {
                        warnings++;
                    }
                }
            }

            if (errors > 0 || warnings > 0)
            {
                message += "，其中错误 " + errors + " 条 / 警告 " + warnings + " 条";
            }

            if (dropped > 0)
            {
                message += "。注意：缓冲曾满，已丢弃 " + dropped + " 条最旧日志";
            }

            return CommandResult.Ok(message, data);
        }
    }
}
