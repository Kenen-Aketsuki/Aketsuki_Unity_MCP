using System;
using System.IO;
using System.Text;

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
    /// <b>与 Python 侧的口径必须完全一致</b>（<c>send.py:global_targets_dir()</c>）：
    /// Windows 是 <c>%LOCALAPPDATA%\Aketsuki\UnityMcp\targets</c>，
    /// 其它平台是 <c>~/.aketsuki/unity-mcp/targets</c>。改任何一边都要同步改另一边，
    /// 否则表现为"工程明明开着，客户端却说一个都找不到"。
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
                var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(root))
                {
                    // 极端环境（某些容器/服务账号）拿不到 LocalApplicationData，退到用户目录
                    root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                }

                if (string.IsNullOrEmpty(root))
                {
                    root = Path.GetTempPath();
                }

                return Path.Combine(root, FolderRelative.Replace('/', Path.DirectorySeparatorChar));
            }
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
            }
            catch (Exception e)
            {
                // 全局索引只是"跨工程便利"，写不进去不该影响本工程正常服务
                CommandLog.Warning("写入全局目标索引失败：" + e.Message);
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
