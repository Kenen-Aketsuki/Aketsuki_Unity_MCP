using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 清理指令目录下"用过"的指令文件与缓存截图，防止越攒越多浪费存储空间。
    /// <para>
    /// 作用域严格限制在<b>指令目录</b>及其子目录内（删前二次校验，绝不删到目录之外）。
    /// 与 <see cref="CommandSystemSettings.DeleteAfterRun"/>（执行后即时删当前文件）互补：
    /// 本指令用于"事后批量清理已攒下来的文件"。
    /// </para>
    /// <para>
    /// 参数：
    /// <list type="bullet">
    /// <item><c>target</c> 清理范围：<c>done</c>（默认）只删 _done 归档子目录；
    /// <c>files</c> 只删指令目录根下尚未归档的 .json；<c>screenshots</c> 只删缓存截图；
    /// <c>all</c> 三者都删</item>
    /// <item><c>olderThanMinutes</c> 可选，只删最近修改早于该分钟数的文件。
    /// 指令文件默认 0 = 不限制；<b>截图默认 60</b>（见 <see cref="DefaultScreenshotExemptMinutes"/>），
    /// 显式传值以传入为准（传 0 表示连刚截的图也删）</item>
    /// <item><c>dryRun</c> 默认 false；true 时只统计将要删除的数量，不真正删除</item>
    /// </list>
    /// </para>
    /// </summary>
    [Command("command.cleanup", Description = "清理指令目录下已执行/归档的指令文件与缓存截图（限定目录内）",
        Usage = "target: string, olderThanMinutes: int, dryRun: bool", EditorOnly = true)]
    public sealed class CleanupCommandFilesCommand : ICommand
    {
        /// <summary>
        /// 截图的默认豁免时长（分钟）。
        /// <para>
        /// 判据必须是<b>时间差</b>，不能用自然日边界：清理任务通常挂在凌晨跑，按"不是今天就算旧"
        /// 会把 23:59 刚截出来、可能还在被读的图判成"昨天"直接删掉（IMPR-015）。
        /// </para>
        /// </summary>
        public const int DefaultScreenshotExemptMinutes = 60;

        /// <summary>算作"截图"的扩展名</summary>
        private static readonly string[] ScreenshotExtensions = { ".png", ".jpg", ".jpeg" };

        /// <summary>
        /// 服务端自己写的文件，删除会打断运行期功能，一律跳过：
        /// <c>consent-results.json</c> 是敏感操作确认结果的落盘副本（<see cref="ConsentGate"/> 启动时回读），
        /// <c>commands.&lt;hash&gt;.json</c> 是指令清单快照（FEAT-020，删了 <c>send.py catalog</c> 就瞎了）。
        /// </summary>
        private static readonly string[] ServiceOwnedFiles = { "consent-results.json" };

        public CommandResult Execute(CommandContext context)
        {
            var directory = CommandSystemSettings.EffectiveWatchDirectory;
            if (!Directory.Exists(directory))
            {
                return CommandResult.Ok($"指令目录不存在，无需清理：{directory}");
            }

            var target = (context.Args.GetString("target", "done") ?? "done").Trim().ToLowerInvariant();
            // 用 Has 而不是 GetInt 的返回值判断"有没有显式传"：0 在这里是一个有意义的取值（不豁免）
            var olderThanGiven = context.Args.Has("olderThanMinutes");
            var olderThanMinutes = context.Args.GetInt("olderThanMinutes", 0);
            var dryRun = context.Args.GetBool("dryRun", false);

            if (target != "done" && target != "files" && target != "screenshots" && target != "all")
            {
                return CommandResult.Fail(
                    $"未知的 target：{target}（可用：done / files / screenshots / all）",
                    JsonValue.Of(target));
            }

            var jsonCutoff = olderThanMinutes > 0
                ? (DateTime?) (DateTime.UtcNow - TimeSpan.FromMinutes(olderThanMinutes))
                : null;

            DateTime? screenshotCutoff;
            if (!olderThanGiven)
            {
                screenshotCutoff = DateTime.UtcNow - TimeSpan.FromMinutes(DefaultScreenshotExemptMinutes);
            }
            else
            {
                screenshotCutoff = olderThanMinutes > 0 ? jsonCutoff : null;
            }

            var doneDirectory = Path.Combine(directory, "_done");
            var candidates = new List<Candidate>();
            var skippedRecentShots = 0;

            if (target == "files" || target == "all")
            {
                CollectInstructionFiles(directory, jsonCutoff, candidates);
            }

            if (target == "done" || target == "all")
            {
                if (Directory.Exists(doneDirectory))
                {
                    CollectArchived(doneDirectory, jsonCutoff, candidates);
                }
            }

            if (target == "screenshots" || target == "all")
            {
                skippedRecentShots = CollectScreenshots(directory, screenshotCutoff, candidates);
            }

            var deletedByKind = new Dictionary<string, int>(StringComparer.Ordinal);
            var failed = 0;
            var errors = new List<string>();
            var rootFull = NormalizeDirectory(directory);

            foreach (var candidate in candidates)
            {
                // 双保险：只在指令目录（含任意层子目录）内才允许删。
                // 从"等于指令目录或 _done"升级为"前缀包含"，否则子目录里的截图会被全部拦下，等于白扫
                var full = Path.GetFullPath(candidate.Path);
                if (!IsInsideRoot(full, rootFull))
                {
                    continue;
                }

                if (dryRun)
                {
                    Bump(deletedByKind, candidate.Kind);
                    continue;
                }

                try
                {
                    File.Delete(full);
                    Bump(deletedByKind, candidate.Kind);
                }
                catch (Exception e)
                {
                    failed++;
                    errors.Add(Path.GetFileName(full) + "：" + e.Message);
                }
            }

            var deleted = 0;
            foreach (var pair in deletedByKind)
            {
                deleted += pair.Value;
            }

            var data = JsonValue.NewObject();
            data.Set("target", JsonValue.Of(target));
            data.Set("scanned", JsonValue.Of(candidates.Count));
            data.Set("deleted", JsonValue.Of(deleted));
            data.Set("skippedRecent", JsonValue.Of(skippedRecentShots));
            data.Set("failed", JsonValue.Of(failed));
            data.Set("dryRun", JsonValue.Of(dryRun));
            data.Set("deletedByKind", KindToJson(deletedByKind));

            var detail = DescribeKinds(deletedByKind)
                         + (skippedRecentShots > 0
                             ? $"，跳过近 {DefaultScreenshotExemptMinutes} 分钟内的截图 {skippedRecentShots} 张"
                             : string.Empty);

            if (dryRun)
            {
                return CommandResult.Ok(
                    $"[dry-run] 将清理 {deleted} 个文件（{detail}；target={target}）", data);
            }

            var message = $"已清理 {deleted} 个文件（{detail}；target={target}"
                + (failed > 0 ? $"，{failed} 个删除失败" : "") + "）";
            if (failed > 0)
            {
                data.Set("errors", ToJsonArray(errors));
            }

            return CommandResult.Ok(message, data);
        }

        /// <summary>待删文件：路径 + 类别（用于返回里分类报数）</summary>
        private sealed class Candidate
        {
            public string Path;

            /// <summary>instruction（根下 .json）/ archived（_done）/ screenshot</summary>
            public string Kind;
        }

        /// <summary>指令目录根下尚未归档的 .json（跳过服务端自有文件）</summary>
        private static void CollectInstructionFiles(string directory, DateTime? cutoff, List<Candidate> outList)
        {
            foreach (var file in Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                if (IsServiceOwned(file))
                {
                    continue;
                }

                if (cutoff != null && File.GetLastWriteTimeUtc(file) >= cutoff.Value)
                {
                    continue;
                }

                outList.Add(new Candidate { Path = file, Kind = "instruction" });
            }
        }

        /// <summary>_done 归档子目录（原有行为，保持"这里面的东西都可以清"）</summary>
        private static void CollectArchived(string doneDirectory, DateTime? cutoff, List<Candidate> outList)
        {
            foreach (var file in Directory.GetFiles(doneDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                if (cutoff != null && File.GetLastWriteTimeUtc(file) >= cutoff.Value)
                {
                    continue;
                }

                outList.Add(new Candidate { Path = file, Kind = "archived" });
            }
        }

        /// <summary>
        /// 指令目录下<b>任意子目录</b>里的截图（<c>screenshots/</c>、<c>Temp/</c> 以及 path 指定的其它子目录）。
        /// <para>只认图片、也只认子目录：根目录里的 png 可能是人特意放的，不碰。</para>
        /// </summary>
        /// <returns>因为"还在豁免期内"而被跳过的张数</returns>
        private static int CollectScreenshots(string directory, DateTime? cutoff, List<Candidate> outList)
        {
            var skipped = 0;
            var rootFull = NormalizeDirectory(directory);

            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (!IsScreenshot(file))
                {
                    continue;
                }

                var dir = Path.GetDirectoryName(Path.GetFullPath(file));
                if (dir == null || string.Equals(NormalizeDirectory(dir), rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (cutoff != null && File.GetLastWriteTimeUtc(file) >= cutoff.Value)
                {
                    skipped++;
                    continue;
                }

                outList.Add(new Candidate { Path = file, Kind = "screenshot" });
            }

            return skipped;
        }

        private static bool IsScreenshot(string file)
        {
            var extension = Path.GetExtension(file);
            if (string.IsNullOrEmpty(extension))
            {
                return false;
            }

            for (var i = 0; i < ScreenshotExtensions.Length; i++)
            {
                if (string.Equals(extension, ScreenshotExtensions[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsServiceOwned(string file)
        {
            var name = Path.GetFileName(file) ?? string.Empty;

            for (var i = 0; i < ServiceOwnedFiles.Length; i++)
            {
                if (string.Equals(name, ServiceOwnedFiles[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // 指令清单快照 commands.<projectHash>.json（FEAT-020）：同名格式在服务端生成，删了就等于丢了离线清单
            return name.StartsWith("commands.", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeDirectory(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>文件是否在指令目录之内（含任意层子目录，不含目录之外的同名前缀兄弟目录）</summary>
        private static bool IsInsideRoot(string fileFullPath, string rootFullPath)
        {
            var dir = Path.GetDirectoryName(fileFullPath);
            if (dir == null)
            {
                return false;
            }

            var normalized = NormalizeDirectory(dir);
            if (string.Equals(normalized, rootFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return normalized.StartsWith(
                rootFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void Bump(Dictionary<string, int> counts, string kind)
        {
            int current;
            counts.TryGetValue(kind, out current);
            counts[kind] = current + 1;
        }

        /// <summary>把分类计数写成人话：<c>指令 2 / 归档 1 / 截图 3</c></summary>
        private static string DescribeKinds(Dictionary<string, int> counts)
        {
            var parts = new List<string>();
            AppendKind(parts, counts, "instruction", "指令");
            AppendKind(parts, counts, "archived", "归档");
            AppendKind(parts, counts, "screenshot", "截图");
            return parts.Count == 0 ? "没有可清理的文件" : string.Join(" / ", parts.ToArray());
        }

        private static void AppendKind(List<string> parts, Dictionary<string, int> counts, string kind, string label)
        {
            int count;
            if (counts.TryGetValue(kind, out count) && count > 0)
            {
                parts.Add(label + " " + count);
            }
        }

        private static JsonValue KindToJson(Dictionary<string, int> counts)
        {
            var node = JsonValue.NewObject();
            node.Set("instruction", JsonValue.Of(Get(counts, "instruction")));
            node.Set("archived", JsonValue.Of(Get(counts, "archived")));
            node.Set("screenshot", JsonValue.Of(Get(counts, "screenshot")));
            return node;
        }

        private static int Get(Dictionary<string, int> counts, string kind)
        {
            int value;
            return counts.TryGetValue(kind, out value) ? value : 0;
        }

        private static JsonValue ToJsonArray(List<string> items)
        {
            var array = JsonValue.NewArray();
            foreach (var item in items)
            {
                array.Add(JsonValue.Of(item));
            }

            return array;
        }
    }
}
