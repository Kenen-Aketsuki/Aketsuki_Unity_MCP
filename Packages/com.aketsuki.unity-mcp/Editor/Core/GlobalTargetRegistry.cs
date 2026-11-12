using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 跨工程的全局目标索引：把"当前有哪些 Unity 实例开着"汇总到一个固定目录。
    /// <para>
    /// <b>为什么需要它</b>：<c>.http-info</c> 是按工作区分散存放的（<c>&lt;工作区&gt;/.workbuddy/UnityEditCommand</c>），
    /// MCP 桥只能看自己那个工作区。于是每个工程都得在 MCP 客户端里单独配一条 server，
    /// 装 N 个工程就是 N 条 server × 每条几十个工具，客户端上下文被撑爆。
    /// 有了这张全局表，客户端只配一条 server 就能发现全部工程，按需切换。
    /// </para>
    /// <para>
    /// <b>与 Python 侧的口径必须完全一致</b>（<c>send.py:global_targets_dir()</c>），三平台分别是：
    /// <list type="bullet">
    /// <item>Windows：<c>%LOCALAPPDATA%\Aketsuki\UnityMcp\targets</c></item>
    /// <item>macOS：<c>~/Library/Application Support/Aketsuki/UnityMcp/targets</c></item>
    /// <item>Linux：<c>$XDG_DATA_HOME/Aketsuki/UnityMcp/targets</c>，默认 <c>~/.local/share</c></item>
    /// </list>
    /// 以前这里写的是 <c>~/.aketsuki/unity-mcp/targets</c>，而代码取
    /// <c>Environment.SpecialFolder.LocalApplicationData</c> —— 在 Mono/.NET 的 macOS 实现里
    /// 那会落到 <c>~/.local/share</c>，与 Python 侧的 <c>~/Library/Application Support</c> 不一致，
    /// 于是 mac 上表现为"工程明明开着，客户端一个都找不到"。现在按平台显式取值，不再依赖运行时的默认映射。
    /// </para>
    /// <para>
    /// 目录里每个工程一个 <c>&lt;projectHash&gt;.json</c>，内容与 <c>.http-info</c> 同构。
    /// 这里刻意<b>不做</b>进程存活校验：那需要每端各自实现一遍，容易漂移。
    /// 判活交给客户端——它连不上就当这个目标不存在，下次编辑器启动会覆盖写。
    /// </para>
    /// </summary>
    public static class GlobalTargetRegistry
    {
        private const string FolderRelative = "Aketsuki/UnityMcp/targets";

        /// <summary>全局目标目录（绝对路径）</summary>
        public static string DirectoryPath
        {
            get
            {
                return Path.Combine(PlatformRoot(), FolderRelative.Replace('/', Path.DirectorySeparatorChar));
            }
        }

        /// <summary>
        /// 三平台的"本地应用数据"根，逐平台显式写死，**与 send.py:global_targets_dir() 一一对应**。
        /// <para>
        /// 不直接用 <c>SpecialFolder.LocalApplicationData</c>：它在 macOS 上落到 <c>~/.local/share</c>，
        /// 而 Python 侧用的是 <c>~/Library/Application Support</c>，两边一漂就是"找不到任何工程"。
        /// </para>
        /// </summary>
        private static string PlatformRoot()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            switch (UnityEngine.Application.platform)
            {
                case UnityEngine.RuntimePlatform.OSXEditor:
                case UnityEngine.RuntimePlatform.OSXPlayer:
                    if (!string.IsNullOrEmpty(profile))
                    {
                        return Path.Combine(profile, "Library", "Application Support");
                    }

                    break;

                case UnityEngine.RuntimePlatform.LinuxEditor:
                case UnityEngine.RuntimePlatform.LinuxPlayer:
                    var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                    if (!string.IsNullOrEmpty(xdg))
                    {
                        return xdg;
                    }

                    if (!string.IsNullOrEmpty(profile))
                    {
                        return Path.Combine(profile, ".local", "share");
                    }

                    break;
            }

            // Windows 与其它平台：LocalApplicationData 就是 Python 侧的 %LOCALAPPDATA%
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(root))
            {
                return root;
            }

            // 极端环境（某些容器/服务账号）拿不到 LocalApplicationData，退到用户目录
            if (!string.IsNullOrEmpty(profile))
            {
                return profile;
            }

            return Path.GetTempPath();
        }

        /// <summary>某个工程的目标文件路径</summary>
        public static string FilePathFor(string projectHash)
        {
            return Path.Combine(DirectoryPath, (projectHash ?? "unknown") + ".json");
        }

        /// <summary>
        /// 发布/更新本工程的目标条目（内容通常是刚写好的 <c>.http-info</c> 原文，避免两处字段漂移）。
        /// </summary>
        public static void Publish(string infoJson, string projectHash)
        {
            if (string.IsNullOrEmpty(infoJson) || string.IsNullOrEmpty(projectHash))
            {
                return;
            }

            try
            {
                AtomicFileWriter.WriteAllText(FilePathFor(projectHash), infoJson, new UTF8Encoding(false));
                PruneStaleEntries(FilePathFor(projectHash));
            }
            catch (Exception e)
            {
                // 全局索引只是"跨工程便利"，写不进去不该影响本工程正常服务
                CommandLog.Warning("写入全局目标索引失败：" + e.Message);
            }
        }

        /// <summary>
        /// 清掉"工程根目录已不存在"的陈旧条目（2026-11-11 拍板：加 prune）。
        /// <para>
        /// 挂在 Publish 上而不是定时任务：编辑器每次启动/域重载都会写一次索引，
        /// 顺手扫一遍代价极小，也就不需要额外的清理流程。只删目录确实没了的，
        /// **不按时间删**——工程被搬到新路径时旧条目会自己更新，按时间删会误伤。
        /// </para>
        /// </summary>
        private static void PruneStaleEntries(string keepPath)
        {
            try
            {
                var directory = DirectoryPath;
                if (!Directory.Exists(directory))
                {
                    return;
                }

                foreach (var file in Directory.GetFiles(directory, "*.json"))
                {
                    if (string.Equals(file, keepPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!IsStaleEntry(file))
                    {
                        continue;
                    }

                    File.Delete(file);
                    CommandLog.Info("[全局索引] 清理陈旧条目：" + Path.GetFileName(file));
                }
            }
            catch (Exception)
            {
                // 清理失败不影响主流程：客户端本来就会跳过不可达目标
            }
        }

        /// <summary>条目里的 projectPath 已经不存在 → 陈旧（读不出来也算陈旧，留着只会误导选址）</summary>
        private static bool IsStaleEntry(string file)
        {
            try
            {
                var json = File.ReadAllText(file, Encoding.UTF8);
                var marker = "\"projectPath\"";
                var at = json.IndexOf(marker, StringComparison.Ordinal);
                if (at < 0)
                {
                    return false; // 结构看不懂就留着，宁可少删
                }

                var colon = json.IndexOf(':', at + marker.Length);
                var quote = colon < 0 ? -1 : json.IndexOf('"', colon + 1);
                if (quote < 0)
                {
                    return false;
                }

                var end = json.IndexOf('"', quote + 1);
                if (end < 0)
                {
                    return false;
                }

                var path = json.Substring(quote + 1, end - quote - 1).Replace("\\\\", "\\");
                return !string.IsNullOrEmpty(path) && !Directory.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>服务停止时撤掉本工程的条目</summary>
        public static void Unpublish(string projectHash)
        {
            if (string.IsNullOrEmpty(projectHash))
            {
                return;
            }

            try
            {
                var path = FilePathFor(projectHash);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // 删不掉无所谓：客户端连不上会自动跳过，下次启动也会覆盖
            }
        }
    }
}
