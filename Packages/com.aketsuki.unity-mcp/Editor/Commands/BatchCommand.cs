using System.Collections.Generic;

namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// 组合指令：顺序执行一组内联指令
    /// <para>参数：commands（必填，指令数组）、stopOnError（可选，默认沿用运行器设置）</para>
    /// </summary>
    [Command("batch", Description = "顺序执行一组内联指令", Usage = "commands: [...], stopOnError: bool")]
    public sealed class BatchCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var node = context.Args.Require("commands");
            if (!node.IsArray)
            {
                throw new CommandArgumentException(context.CommandId, "commands", "必须是 JSON 数组");
            }

            var entries = new List<CommandEntry>();
            var items = node.ArrayValue;
            for (var i = 0; i < items.Count; i++)
            {
                entries.Add(CommandReader.ParseEntry(items[i], i));
            }

            var runner = context.Runner ?? new CommandRunner();
            var stopOnError = context.Args.GetBool("stopOnError", runner.StopOnError);

            var previous = runner.StopOnError;
            runner.StopOnError = stopOnError;
            try
            {
                var report = runner.RunEntries(entries, context.Source, context.Variables, context.Depth + 1);

                if (report.HasError)
                {
                    return CommandResult.Fail($"batch 内有 {report.Failed} 条指令失败", report);
                }

                // 被取消时剩余指令是 Skipped 而非 Failed，若不单独判断会误报"完成"
                if (report.Skipped > 0 && context.IsCancelled)
                {
                    return CommandResult.Fail(
                        $"batch 已取消：执行到 {report.Succeeded}/{report.Total} 条，剩余 {report.Skipped} 条未执行",
                        report);
                }

                return CommandResult.Ok($"batch 完成：成功 {report.Succeeded}/{report.Total}", report);
            }
            finally
            {
                runner.StopOnError = previous;
            }
        }
    }
}
