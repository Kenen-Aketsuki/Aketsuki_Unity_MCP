using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CivilRev.AdbDebugger
{
    /// <summary>
    /// 本工具唯一对外的入口，给「Unity MCP 指令系统」的 <c>adb.*</c> 指令调用。
    ///
    /// <para>
    /// <b>为什么要这一层：</b>指令系统是另一个程序集（预定义程序集，没有 asmdef），
    /// 它引用不到本模块，只能用反射。反射调用点越少越不容易随重构失效，
    /// 所以把"设备 / 抓取"的全部入口收成一个方法：<see cref="Invoke"/>。
    /// 指令侧只认这一个签名，参数用 <c>Dictionary&lt;string,string&gt;</c>（两边都有的基础类型，
    /// 不传任何自定义对象），回一段 JSON 文本由指令侧解析。
    /// </para>
    ///
    /// <para>
    /// <b>线程：</b>必须在主线程调用。内部要读 EditorPrefs、操作 Unity 控制台、起停子进程，
    /// 这些都是主线程专用。指令系统本来就在主线程执行指令，天然满足。
    /// </para>
    ///
    /// <para>
    /// <b>为什么回字符串而不是对象：</b>回对象就得让两边共享类型或让指令侧反射每个字段；
    /// 回 JSON 是最小契约，本模块将来加字段也不会破坏指令侧。
    /// </para>
    /// </summary>
    public static class AdbExternalApi
    {
        /// <summary>指令侧按这个名字反射查找本类型（改类名等于破坏契约，别改）</summary>
        public const string ApiTypeName = "CivilRev.AdbDebugger.AdbExternalApi";

        /// <summary>指令侧按这个签名反射查找（改签名等于破坏契约，别改）</summary>
        public const string InvokeMethodName = "Invoke";

        /// <summary>支持的子命令</summary>
        public static readonly string[] Commands =
        {
            "devices", "logcat.start", "logcat.stop", "logcat.status", "logcat.read"
        };

        /// <summary>
        /// 执行一个子命令，返回 JSON 文本（永远是合法 JSON，失败时 <c>ok=false</c> 且带 <c>error</c>）。
        /// </summary>
        /// <param name="command">见 <see cref="Commands"/></param>
        /// <param name="args">字符串参数；可为 null</param>
        public static string Invoke(string command, IDictionary<string, string> args)
        {
            if (args == null)
            {
                args = new Dictionary<string, string>();
            }

            try
            {
                switch ((command ?? string.Empty).Trim().ToLowerInvariant())
                {
                    case "devices":
                        return Devices(args);

                    case "logcat.start":
                        return LogcatStart(args);

                    case "logcat.stop":
                        return LogcatStop();

                    case "logcat.status":
                        return LogcatStatus();

                    case "logcat.read":
                        return LogcatRead(args);

                    default:
                        return new JsonOut()
                            .Bool("ok", false)
                            .Str("error", "未知子命令：" + command + "（可用：" + string.Join(" / ", Commands) + "）")
                            .ToString();
                }
            }
            catch (Exception exception)
            {
                return new JsonOut()
                    .Bool("ok", false)
                    .Str("error", "ADB 调试工具执行 " + command + " 失败：" + exception.Message)
                    .ToString();
            }
        }

        #region 子命令

        /// <summary>
        /// 设备列表。默认**现场扫**——缓存可能是空的（窗口没开过），
        /// 拿空列表回给 AI 会直接把人带向"没插设备"这个错误结论。
        /// </summary>
        private static string Devices(IDictionary<string, string> args)
        {
            string raw;
            var refresh = !args.TryGetValue("refresh", out raw)
                          || !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);

            string error;
            List<AdbDevice> devices;
            if (refresh)
            {
                devices = AdbDevices.ScanNow(out error);
            }
            else
            {
                devices = new List<AdbDevice>(AdbDevices.Snapshot());
                error = AdbDevices.LastError;
            }

            return AdbDevices.Json(devices, AdbDebuggerSettings.Serial, error);
        }

        /// <summary>
        /// 起抓取。可顺手改 serial / tag / level / backfill（写进设置，与窗口是同一份），
        /// 这样"换个设备再看"只调一次指令就够。
        /// </summary>
        private static string LogcatStart(IDictionary<string, string> args)
        {
            string raw;

            if (args.TryGetValue("serial", out raw))
            {
                AdbDebuggerSettings.Serial = (raw ?? string.Empty).Trim();
            }

            if (args.TryGetValue("tag", out raw))
            {
                // 空字符串是合法值，表示不过滤——所以不能因为"空"就跳过
                AdbDebuggerSettings.Tag = raw ?? string.Empty;
            }

            if (args.TryGetValue("level", out raw))
            {
                AdbDebuggerSettings.Level = raw;
            }

            if (args.TryGetValue("backfill", out raw))
            {
                int value;
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                {
                    AdbDebuggerSettings.Backfill = value;
                }
            }

            // 改完设置必须刷缓存，否则要重启才生效（服务里缓存是为了热路径不读注册表）
            AdbLogcatService.RefreshFilters();

            string error;
            if (!AdbLogcatService.Start(out error))
            {
                return new JsonOut()
                    .Bool("ok", false)
                    .Str("error", string.IsNullOrEmpty(error) ? "启动失败（没有给出原因）" : error)
                    .Str("adb", AdbLogcatService.AdbPath)
                    .Str("lastError", AdbLogcatService.LastError)
                    .ToString();
            }

            return new JsonOut()
                .Bool("ok", true)
                .Bool("running", true)
                .Str("serial", AdbDebuggerSettings.Serial)
                .Bool("auto", (AdbDebuggerSettings.Serial ?? string.Empty).Trim().Length == 0)
                .Str("tag", AdbDebuggerSettings.Tag)
                .Str("level", AdbDebuggerSettings.Level)
                .Num("backfill", AdbDebuggerSettings.Backfill)
                .Str("adb", AdbLogcatService.AdbPath)
                .Str("adbSource", AdbLogcatService.AdbSource)
                .Str("command", AdbLogcatService.LastCommand)
                .Str("message", "已开始抓取，日志会以 [ADB] 前缀打进 Unity 控制台")
                .ToString();
        }

        private static string LogcatStop()
        {
            var wasRunning = AdbLogcatService.IsRunning;
            AdbLogcatService.Stop();

            return new JsonOut()
                .Bool("ok", true)
                .Bool("running", false)
                .Bool("wasRunning", wasRunning)
                .Num("captured", AdbLogcatService.CapturedCount)
                .Str("message", wasRunning
                    ? "已停止抓取（缓冲里还有 " + AdbLogcatService.CapturedCount + " 条没被取走，可用 adb.logcat.read 读走）"
                    : "本来就没在抓")
                .ToString();
        }

        private static string LogcatStatus()
        {
            return new JsonOut()
                .Bool("ok", true)
                .Bool("running", AdbLogcatService.IsRunning)
                .Str("state", AdbLogcatService.State)
                .Str("adb", AdbLogcatService.AdbPath)
                .Str("adbSource", AdbLogcatService.AdbSource)
                .Str("serial", AdbDebuggerSettings.Serial)
                .Bool("auto", (AdbDebuggerSettings.Serial ?? string.Empty).Trim().Length == 0)
                .Str("tag", AdbDebuggerSettings.Tag)
                .Str("level", AdbDebuggerSettings.Level)
                .Num("backfill", AdbDebuggerSettings.Backfill)
                .Num("received", AdbLogcatService.ReceivedCount)
                .Num("printed", AdbLogcatService.PrintedCount)
                .Num("coalesced", AdbLogcatService.CoalescedCount)
                .Num("filtered", AdbLogcatService.FilteredCount)
                .Num("dropped", AdbLogcatService.DroppedCount)
                .Num("captured", AdbLogcatService.CapturedCount)
                .Num("capturedDropped", AdbLogcatService.CapturedDroppedCount)
                .Str("notice", AdbLogcatService.Notice)
                .Str("lastError", AdbLogcatService.LastError)
                .Str("command", AdbLogcatService.LastCommand)
                .Str("summary", AdbLogcatService.SummaryText)
                .ToString();
        }

        /// <summary>
        /// 取走缓冲里的日志（取走即删）。默认 200 条；<c>max</c> 上限 2000（= 缓冲总容量）。
        /// </summary>
        private static string LogcatRead(IDictionary<string, string> args)
        {
            string raw;
            var max = 200;
            if (args.TryGetValue("max", out raw)
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out max))
            {
                if (max <= 0)
                {
                    max = 200;
                }
            }

            var lines = AdbLogcatService.TakeCaptured(max);

            var items = new StringBuilder();
            for (var i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                {
                    items.Append(',');
                }

                items.Append(new JsonOut()
                    .Str("at", lines[i].At.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
                    .Str("level", lines[i].Level)
                    .Str("tag", lines[i].Tag)
                    .Str("text", lines[i].Text)
                    .ToString());
            }

            return new JsonOut()
                .Bool("ok", true)
                .Bool("running", AdbLogcatService.IsRunning)
                .Num("count", lines.Count)
                .Num("remaining", AdbLogcatService.CapturedCount)
                .Num("dropped", AdbLogcatService.CapturedDroppedCount)
                .Raw("lines", "[" + items + "]")
                .ToString();
        }

        #endregion
    }
}
