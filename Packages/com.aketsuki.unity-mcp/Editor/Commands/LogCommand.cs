using System;
using System.Collections.Generic;

namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// 打印日志
    /// <para>参数：message（必填）、level（info/warn/error，默认 info）、variable（可选，打印变量值）</para>
    /// <para>message 中的 {变量名} 会被替换为共享变量的值</para>
    /// </summary>
    [Command("log", Description = "打印一条日志", Usage = "message: string, level: info|warn|error, variable: string")]
    public sealed class LogCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var level = context.Args.GetString("level", "info").ToLowerInvariant();
            var rawMessage = context.Args.RequireString("message");
            var message = context.Expand(rawMessage);

            var variableName = context.Args.GetString("variable", null);
            if (!string.IsNullOrEmpty(variableName))
            {
                var value = context.HasVariable(variableName)
                    ? Stringify(context.Variables[variableName])
                    : "<undefined>";
                message = message + " " + variableName + "=" + value;
            }

            switch (level)
            {
                case "warn":
                case "warning":
                    CommandLog.Warning(message);
                    break;
                case "error":
                    CommandLog.Error(message);
                    break;
                default:
                    CommandLog.Info(message);
                    break;
            }

            return CommandResult.Ok(message);
        }

        private static string Stringify(object value)
        {
            if (value == null)
            {
                return "null";
            }
            if (value is JsonValue json)
            {
                return json.ToJson(false);
            }
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
