using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 进 Play 模式之前的内存水位检查（IMPR-008）。
    ///
    /// <para>
    /// <b>为什么要有它：</b>连续跑了十几个小时的编辑器，进程工作集能涨到几十 GB。
    /// 此时 <c>editor.play</c> 会弹原生 OOM 框，后果不是「指令失败」那么简单——
    /// 编辑器卡在弹框上、<c>.http-info</c> 消失、HTTP 服务没了、MCP 全部工具失联，
    /// 只能人工重启。所以要在<b>指令层</b>把这件事拦下来，换一条能读的报错。
    /// </para>
    ///
    /// <para>
    /// <b>只管指令，不管人：</b>检查写在 <c>editor.play</c> / <c>runtime.play</c> 的指令处理逻辑里，
    /// 编辑器自己的 Play 按钮、菜单、快捷键一条都不碰——用户手动进 Play 不受任何影响。
    /// </para>
    ///
    /// <para>
    /// 阈值刻意定得粗（正常编辑器 3~6 GB，长跑到 30 GB 才危险），误拦的代价是"多带一个 force 参数"，
    /// 漏拦的代价是人工重启 + 会话重建，两者极不对称。
    /// </para>
    /// </summary>
    internal static class PlayModeMemoryGuard
    {
        /// <summary>提示阈值：进程工作集（字节）。超过只警告，照常执行。</summary>
        internal const long WarnWorkingSetBytes = 16L * 1024 * 1024 * 1024;

        /// <summary>拒绝阈值：进程工作集（字节）。超过要 <c>force</c> 才放行。</summary>
        internal const long BlockWorkingSetBytes = 24L * 1024 * 1024 * 1024;

        /// <summary>提示阈值：系统可用物理内存（字节）。低于只警告。</summary>
        internal const long WarnAvailableBytes = 8L * 1024 * 1024 * 1024;

        /// <summary>拒绝阈值：系统可用物理内存（字节）。低于要 <c>force</c> 才放行。</summary>
        internal const long BlockAvailableBytes = 4L * 1024 * 1024 * 1024;

        /// <summary>提示阈值：编辑器已连续运行小时数。超过只警告。</summary>
        internal const double WarnUptimeHours = 8.0;

        /// <summary>一份内存水位采样</summary>
        internal sealed class Sample
        {
            /// <summary>进程工作集（字节）；0 = 取不到</summary>
            public long WorkingSetBytes;

            /// <summary>系统可用物理内存（字节）；0 = 取不到（非 Windows 或 P/Invoke 失败）</summary>
            public long AvailablePhysicalBytes;

            /// <summary>系统总物理内存（字节）；0 = 取不到</summary>
            public long TotalPhysicalBytes;

            /// <summary>本进程已运行小时数；0 = 取不到</summary>
            public double UptimeHours;

            /// <summary>结论：ok / warn / block</summary>
            public string Level = "ok";

            /// <summary>触发的具体原因（中文，给人看）</summary>
            public readonly System.Collections.Generic.List<string> Reasons =
                new System.Collections.Generic.List<string>();

            /// <summary>机器可读的阈值快照，让调用方不必解析中文也能复算</summary>
            public JsonValue ToJson()
            {
                var json = JsonValue.NewObject();
                json.Set("level", JsonValue.Of(Level));
                json.Set("workingSetBytes", JsonValue.Of(WorkingSetBytes));
                json.Set("workingSetGB", JsonValue.Of(Math.Round(ToGB(WorkingSetBytes), 2)));
                json.Set("availablePhysicalGB", JsonValue.Of(Math.Round(ToGB(AvailablePhysicalBytes), 2)));
                json.Set("totalPhysicalGB", JsonValue.Of(Math.Round(ToGB(TotalPhysicalBytes), 2)));
                json.Set("uptimeHours", JsonValue.Of(Math.Round(UptimeHours, 2)));
                json.Set("thresholds", ThresholdsJson());
                var reasons = JsonValue.NewArray();
                foreach (var reason in Reasons)
                {
                    reasons.Add(JsonValue.Of(reason));
                }

                json.Set("reasons", reasons);
                return json;
            }

            internal static JsonValue ThresholdsJson()
            {
                var json = JsonValue.NewObject();
                json.Set("warnWorkingSetGB", JsonValue.Of(Math.Round(ToGB(WarnWorkingSetBytes), 2)));
                json.Set("blockWorkingSetGB", JsonValue.Of(Math.Round(ToGB(BlockWorkingSetBytes), 2)));
                json.Set("warnAvailableGB", JsonValue.Of(Math.Round(ToGB(WarnAvailableBytes), 2)));
                json.Set("blockAvailableGB", JsonValue.Of(Math.Round(ToGB(BlockAvailableBytes), 2)));
                json.Set("warnUptimeHours", JsonValue.Of(WarnUptimeHours));
                return json;
            }

            private static double ToGB(long bytes)
            {
                return bytes <= 0 ? 0.0 : bytes / (1024.0 * 1024.0 * 1024.0);
            }
        }

        /// <summary>取一份当前水位（含结论）。任意时刻可调，<c>editor.state</c> 也用它。</summary>
        internal static Sample Take()
        {
            var sample = new Sample();
            sample.WorkingSetBytes = ReadWorkingSet();
            sample.UptimeHours = ReadUptimeHours();

            long available;
            long total;
            if (TryReadPhysicalMemory(out available, out total))
            {
                sample.AvailablePhysicalBytes = available;
                sample.TotalPhysicalBytes = total;
            }

            var gb = sample.WorkingSetBytes / (1024.0 * 1024.0 * 1024.0);
            var availableGb = sample.AvailablePhysicalBytes / (1024.0 * 1024.0 * 1024.0);

            if (sample.WorkingSetBytes > BlockWorkingSetBytes)
            {
                sample.Level = "block";
                sample.Reasons.Add($"进程工作集 {gb:F1} GB，超过 {BlockWorkingSetBytes / (1024.0 * 1024.0 * 1024.0):F0} GB");
            }
            else if (sample.WorkingSetBytes > WarnWorkingSetBytes)
            {
                sample.Level = "warn";
                sample.Reasons.Add($"进程工作集 {gb:F1} GB 偏高");
            }

            if (sample.AvailablePhysicalBytes > 0 && sample.AvailablePhysicalBytes < BlockAvailableBytes)
            {
                sample.Level = "block";
                sample.Reasons.Add($"系统可用内存 {availableGb:F1} GB，低于 {BlockAvailableBytes / (1024.0 * 1024.0 * 1024.0):F0} GB");
            }
            else if (sample.AvailablePhysicalBytes > 0 && sample.AvailablePhysicalBytes < WarnAvailableBytes)
            {
                if (sample.Level != "block")
                {
                    sample.Level = "warn";
                }

                sample.Reasons.Add($"系统可用内存 {availableGb:F1} GB 偏低");
            }

            if (sample.UptimeHours > WarnUptimeHours)
            {
                if (sample.Level != "block")
                {
                    sample.Level = "warn";
                }

                sample.Reasons.Add($"编辑器已连续运行 {sample.UptimeHours:F1} 小时");
            }

            return sample;
        }

        /// <summary>
        /// 把检查结果写进 <paramref name="data"/>（<c>memory</c> 块），并返回<b>是否应当阻断</b>。
        /// <para>阻断时 <paramref name="message"/> 给出可执行的建议——「先重启编辑器」这件事 AI 自己做不到，
        /// 得让人知道该干嘛。</para>
        /// </summary>
        internal static bool ShouldBlock(JsonValue data, bool force, out string message)
        {
            var sample = Take();
            if (data != null)
            {
                data.Set("memory", sample.ToJson());
            }

            message = null;
            if (!string.Equals(sample.Level, "block", StringComparison.Ordinal))
            {
                return false;
            }

            if (force)
            {
                message = "已按 force:true 跳过内存水位检查并进入 Play 模式" +
                          "（当前：" + string.Join("；", sample.Reasons.ToArray()) + "）";
                return false;
            }

            message = "拒绝进入 Play 模式：当前内存水位很可能触发 Unity 原生 OOM" +
                      "（" + string.Join("；", sample.Reasons.ToArray()) + "）。" +
                      "OOM 的代价不是'指令失败'，而是编辑器卡死、HTTP 指令服务消失、MCP 全部工具失联，必须人工重启。" +
                      "建议先重启 Unity 编辑器；确认要带险进 Play 请显式传 force: true。";
            return true;
        }

        /// <summary>
        /// 进程工作集（字节）。
        /// <para>
        /// <b>不要只信 <c>Process.WorkingSet64</c>：</b>实测在 Unity 2022 编辑器里它返回 0
        /// （同一个 <c>Process</c> 对象的 <c>StartTime</c> 却正常），于是"工作集 0 GB"会让
        /// 这道检查形同虚设——恰恰是最需要它的长跑编辑器上失效。
        /// 所以先走 PSAPI 的 <c>GetProcessMemoryInfo</c>，取不到再退回 <c>WorkingSet64</c>。
        /// </para>
        /// </summary>
        private static long ReadWorkingSet()
        {
            long native;
            if (TryReadWorkingSetNative(out native) && native > 0)
            {
                return native;
            }

            try
            {
                var value = Process.GetCurrentProcess().WorkingSet64;
                if (value > 0)
                {
                    return value;
                }
            }
            catch (Exception)
            {
                // 落到最后一道兜底
            }

            // 托管堆只是进程内存的一部分，但在两个来源都拿不到时，
            // "几百 MB 的托管堆"至少能让"内存确实在涨"这件事有个数。
            try
            {
                return GC.GetTotalMemory(false);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static double ReadUptimeHours()
        {
            try
            {
                var elapsed = DateTime.Now - Process.GetCurrentProcess().StartTime;
                return elapsed.TotalHours;
            }
            catch (Exception)
            {
                return 0;
            }
        }

#if UNITY_EDITOR_WIN
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCountersEx
        {
            public uint Cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMemoryInfo(IntPtr handle,
            out ProcessMemoryCountersEx counters, uint size);

        private static bool TryReadWorkingSetNative(out long bytes)
        {
            bytes = 0;
            try
            {
                ProcessMemoryCountersEx counters;
                if (GetProcessMemoryInfo(Process.GetCurrentProcess().Handle,
                        out counters, (uint)Marshal.SizeOf(typeof(ProcessMemoryCountersEx))))
                {
                    bytes = (long)(ulong)counters.WorkingSetSize;
                    return bytes > 0;
                }
            }
            catch (Exception)
            {
                return false;
            }

            return false;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
#endif

        /// <summary>
        /// 系统物理内存（可用 / 总量）。只在 Windows 编辑器上取得到；
        /// 取不到就返回 false，判定退回「只看工作集与运行时长」，不会因此误拦。
        /// </summary>
        private static bool TryReadPhysicalMemory(out long available, out long total)
        {
            available = 0;
            total = 0;
#if UNITY_EDITOR_WIN
            try
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
                if (GlobalMemoryStatusEx(ref status))
                {
                    available = (long)status.AvailPhys;
                    total = (long)status.TotalPhys;
                    return available > 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
#endif
            return false;
        }
    }
}
