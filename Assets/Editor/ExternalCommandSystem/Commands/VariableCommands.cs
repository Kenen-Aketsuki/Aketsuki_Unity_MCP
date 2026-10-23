using System;
using System.Collections.Generic;

namespace CivilRev.CommandSystem
{
    /// <summary>
    /// 设置共享变量：在同一批指令（及嵌套文件）之间传递数据
    /// <para>参数：name（必填）、value（必填，可以是任意 JSON 值）</para>
    /// </summary>
    [Command("var.set", Description = "设置共享变量", Usage = "name: string, value: any")]
    public sealed class SetVariableCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var name = context.Args.RequireString("name");
            var value = context.Args.Require("value");

            context.SetVariable(name, JsonMapper.ToPlainObject(value));
            return CommandResult.Ok($"{name} = {value.ToJson(false)}");
        }
    }

    /// <summary>
    /// 打印当前所有共享变量（调试用）
    /// <para>参数：level（可选）</para>
    /// </summary>
    [Command("var.dump", Description = "打印所有共享变量", Usage = "level: info|warn|error")]
    public sealed class DumpVariablesCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            if (context.Variables == null || context.Variables.Count == 0)
            {
                CommandLog.Info("变量表为空");
                return CommandResult.Ok("变量表为空");
            }

            var node = JsonValue.NewObject();
            foreach (var kv in context.Variables)
            {
                node.Set(kv.Key, JsonMapper.FromObject(kv.Value));
            }

            var text = node.ToJson(true);
            var level = context.Args.GetString("level", "info").ToLowerInvariant();
            if (level == "warn" || level == "warning")
            {
                CommandLog.Warning("变量表：\n" + text);
            }
            else if (level == "error")
            {
                CommandLog.Error("变量表：\n" + text);
            }
            else
            {
                CommandLog.Info("变量表：\n" + text);
            }

            return CommandResult.Ok($"共 {context.Variables.Count} 个变量", text);
        }
    }
}
