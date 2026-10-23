using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace CivilRev.CommandSystem.Editor
{
    /// <summary>
    /// 清理指令目录下"用过"的指令文件，防止越攒越多浪费存储空间。
    /// <para>
    /// 作用域严格限制在<b>指令目录</b>及其 <c>_done</c> 子目录内，绝不会删到目录之外。
    /// 与 <see cref="CommandSystemSettings.DeleteAfterRun"/>（执行后即时删当前文件）互补：
    /// 本指令用于"事后批量清理已攒下来的文件"。
    /// </para>
    /// <para>
    /// 参数：
    /// <list type="bullet">
    /// <item><c>target</c> 清理范围：<c>done</c>（默认）只删 _done 归档子目录；
    /// <c>files</c> 只删指令目录根下尚未归档的 .json；<c>all</c> 两者都删</item>
    /// <item><c>olderThanMinutes</c> 可选，只删最近修改早于该分钟数的文件（默认 0 = 不限制）</item>
    /// <item><c>dryRun</c> 默认 false；true 时只统计将要删除的数量，不真正删除</item>
    /// </list>
    /// </para>
    /// </summary>
    [Command("command.cleanup", Description = "清理指令目录下已执行/归档的指令文件（限定目录内）",
        Usage = "target: string, olderThanMinutes: int, dryRun: bool", EditorOnly = true)]
    public sealed class CleanupCommandFilesCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var directory = CommandSystemSettings.EffectiveWatchDirectory;
            if (!Directory.Exists(directory))
            {
                return CommandResult.Ok($"指令目录不存在，无需清理：{directory}");
            }

            var target = (context.Args.GetString("target", "done") ?? "done").ToLowerInvariant();
            var olderThanMinutes = context.Args.GetInt("olderThanMinutes", 0);
            var dryRun = context.Args.GetBool("dryRun", false);

            var cutoff = olderThanMinutes > 0
                ? (DateTime?) (DateTime.UtcNow - TimeSpan.FromMinutes(olderThanMinutes))
                : null;

            var doneDirectory = Path.Combine(directory, "_done");

            var candidates = new List<string>();
            if (target == "files" || target == "all")
            {
                Collect(directory, "*.json", cutoff, candidates);
            }

            if (target == "done" || target == "all")
            {
                if (Directory.Exists(doneDirectory))
                {
                    Collect(doneDirectory, "*", cutoff, candidates);
                }
            }

            var deleted = 0;
            var failed = 0;
            var errors = new List<string>();
            foreach (var file in candidates)
            {
                // 双保险：只在指令目录或其 _done 子目录内才允许删
                var dir = Path.GetDirectoryName(file);
                if (!string.Equals(dir, directory, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(dir, doneDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (dryRun)
                {
                    deleted++;
                    continue;
                }

                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception e)
                {
                    failed++;
                    errors.Add(Path.GetFileName(file) + "：" + e.Message);
                }
            }

            var data = JsonValue.NewObject();
            data.Set("target", JsonValue.Of(target));
            data.Set("scanned", JsonValue.Of(candidates.Count));
            data.Set("deleted", JsonValue.Of(deleted));
            data.Set("failed", JsonValue.Of(failed));
            data.Set("dryRun", JsonValue.Of(dryRun));

            if (dryRun)
            {
                return CommandResult.Ok(
                    $"[dry-run] 将清理 {deleted} 个文件（target={target}）", data);
            }

            var message = $"已清理 {deleted} 个指令文件（target={target}"
                + (failed > 0 ? $"，{failed} 个删除失败" : "") + "）";
            if (failed > 0)
            {
                data.Set("errors", ToJsonArray(errors));
            }

            return CommandResult.Ok(message, data);
        }

        private static void Collect(string directory, string searchPattern, DateTime? cutoff, List<string> outList)
        {
            foreach (var file in Directory.GetFiles(directory, searchPattern, SearchOption.TopDirectoryOnly))
            {
                if (cutoff != null && File.GetLastWriteTimeUtc(file) >= cutoff.Value)
                {
                    continue;
                }

                outList.Add(file);
            }
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
