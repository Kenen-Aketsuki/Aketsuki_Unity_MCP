using System.Collections.Generic;
using System.Text;

namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// 列出所有已注册指令（排查"指令 ID 写错"时很有用）
    /// <para>参数：filter（可选，按前缀过滤）</para>
    /// </summary>
    [Command("commands.list", Description = "列出所有已注册指令", Usage = "filter: string")]
    public sealed class ListCommandsCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var registry = context.Runner != null ? context.Runner.Registry : CommandRegistry.Default;
            var filter = context.Args.GetString("filter", null);

            var builder = new StringBuilder();
            builder.AppendLine("已注册指令：");

            var count = 0;
            foreach (var descriptor in registry.All)
            {
                if (!string.IsNullOrEmpty(filter) &&
                    !descriptor.Id.StartsWith(filter, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                count++;
                builder.Append("  - ").Append(descriptor.Id);
                if (!string.IsNullOrEmpty(descriptor.Description))
                {
                    builder.Append("：").Append(descriptor.Description);
                }
                if (descriptor.IsEditorOnly)
                {
                    builder.Append(" [仅编辑器]");
                }
                builder.AppendLine();
            }

            builder.Append("共 ").Append(count).Append(" 条");
            CommandLog.Info(builder.ToString());
            return CommandResult.Ok($"共 {count} 条指令", builder.ToString());
        }
    }
}
