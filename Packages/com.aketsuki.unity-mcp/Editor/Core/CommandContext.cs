using System;
using System.Collections.Generic;

namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// 指令执行上下文：承载参数、变量表与运行配置
    /// </summary>
    public sealed class CommandContext
    {
        /// <summary>当前指令 ID</summary>
        public string CommandId { get; }

        /// <summary>当前指令的参数包</summary>
        public CommandArgs Args { get; }

        /// <summary>所属的指令文件（手动构造上下文时可能为 null）</summary>
        public CommandScript Source { get; }

        /// <summary>当前指令在文件中的序号（从 0 开始）</summary>
        public int Index { get; }

        /// <summary>嵌套层级：文件为 0，batch / run_file 内层层递增</summary>
        public int Depth { get; }

        /// <summary>空跑模式：只记录不产生副作用（指令应自行检查该标记）</summary>
        public bool DryRun { get; }

        /// <summary>当前运行器，可用于嵌套执行其它指令</summary>
        public CommandRunner Runner { get; }

        /// <summary>同一批次内共享的变量表（跨指令传递数据）</summary>
        public IDictionary<string, object> Variables { get; }

        /// <summary>
        /// 是否已被请求取消（协作式）。
        /// <para>长指令应在关键点检查本属性并干净退出，返回 <see cref="CommandResult.Fail"/> + "已取消"。</para>
        /// </summary>
        public bool IsCancelled
        {
            get { return Runner != null && Runner.IsAbortRequested; }
        }

        public CommandContext(
            string commandId,
            CommandArgs args,
            CommandRunner runner = null,
            CommandScript source = null,
            int index = 0,
            int depth = 0,
            bool dryRun = false,
            IDictionary<string, object> variables = null)
        {
            CommandId = commandId ?? string.Empty;
            Args = args ?? CommandArgs.Empty;
            Runner = runner;
            Source = source;
            Index = index;
            Depth = depth;
            DryRun = dryRun;
            Variables = variables ?? new Dictionary<string, object>(StringComparer.Ordinal);
        }

        #region 变量表

        /// <summary>写入共享变量</summary>
        public void SetVariable(string name, object value)
        {
            Variables[name] = value;
        }

        /// <summary>读取共享变量，不存在时返回默认值</summary>
        public T GetVariable<T>(string name, T fallback = default)
        {
            if (Variables != null && Variables.TryGetValue(name, out var value))
            {
                if (value is T typed)
                {
                    return typed;
                }
                try
                {
                    return (T)Convert.ChangeType(value, typeof(T));
                }
                catch (Exception)
                {
                    return fallback;
                }
            }
            return fallback;
        }

        /// <summary>是否存在指定变量</summary>
        public bool HasVariable(string name)
        {
            return Variables != null && Variables.ContainsKey(name);
        }

        /// <summary>删除变量</summary>
        public bool RemoveVariable(string name)
        {
            return Variables != null && Variables.Remove(name);
        }

        /// <summary>
        /// 把文本中的 {变量名} 替换为共享变量的值（变量不存在时保持原样）
        /// </summary>
        public string Expand(string template)
        {
            return ExpandVariables(template, Variables);
        }

        #endregion

        private static string ExpandVariables(string template, IDictionary<string, object> variables)
        {
            if (string.IsNullOrEmpty(template) || variables == null || variables.Count == 0 ||
                template.IndexOf('{') < 0)
            {
                return template;
            }

            var result = template;
            foreach (var kv in variables)
            {
                result = result.Replace("{" + kv.Key + "}", Stringify(kv.Value));
            }
            return result;
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

            try
            {
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
