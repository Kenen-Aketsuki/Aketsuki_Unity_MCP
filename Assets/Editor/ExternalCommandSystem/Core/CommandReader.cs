using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CivilRev.CommandSystem
{
    /// <summary>
    /// 指令读取模块（模块一）：负责把磁盘上的 .json 文件读成 <see cref="CommandScript"/>
    /// <para>
    /// 只做"读取 + 结构化"，不做任何执行；执行由 <see cref="CommandRunner"/> 负责。
    /// </para>
    /// </summary>
    public static class CommandReader
    {
        /// <summary>指令节点的保留键，解析隐式参数时会被排除</summary>
        private static readonly HashSet<string> ReservedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 注意：这里不放 "name"——脚本名只在根节点使用，
            // 单条指令的 name 应当作为参数传给指令（例如 var.set 就需要 name）
            "id", "command", "type", "enabled", "disabled",
            "comment", "note", "description",
            "params", "args", "parameters"
        };

        /// <summary>指令列表字段的候选名</summary>
        private static readonly string[] CommandsKeys = { "commands", "instructions", "steps" };

        /// <summary>参数字段的候选名</summary>
        private static readonly string[] ParamsKeys = { "params", "args", "parameters" };

        #region 文件读取

        /// <summary>
        /// 从指定路径读取指令文件
        /// </summary>
        /// <param name="path">绝对路径，或相对路径（见 <see cref="CommandPathUtil"/>）</param>
        /// <exception cref="CommandFileException">文件不存在、读取失败或格式错误</exception>
        public static CommandScript FromFile(string path)
        {
            var fullPath = CommandPathUtil.Resolve(path);

            if (!File.Exists(fullPath))
            {
                throw new CommandFileException(fullPath, "文件不存在");
            }

            string text;
            try
            {
                // 自动识别 UTF-8 / UTF-8 BOM；其它编码在 Unity 环境下极少见
                text = File.ReadAllText(fullPath, Encoding.UTF8);
            }
            catch (Exception e)
            {
                throw new CommandFileException(fullPath, e.Message, e);
            }

            var script = FromText(text, fullPath);
            script.FilePath = fullPath;
            return script;
        }

        /// <summary>
        /// 尝试读取；失败时返回 false 并通过 error 输出原因，不抛异常
        /// </summary>
        public static bool TryReadFile(string path, out CommandScript script, out string error)
        {
            script = null;
            string fullPath;
            if (!CommandPathUtil.TryResolve(path, out fullPath))
            {
                error = $"无法解析路径：{path}";
                return false;
            }

            try
            {
                script = FromFile(fullPath);
                error = null;
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        #endregion

        #region 文本解析

        /// <summary>解析指令文本</summary>
        public static CommandScript FromText(string text, string origin = null)
        {
            if (text == null)
            {
                throw new CommandFileException(origin ?? "<memory>", "内容为空");
            }

            JsonValue root;
            try
            {
                root = JsonValue.Parse(text);
            }
            catch (JsonParseException e)
            {
                throw new CommandFileException(origin ?? "<memory>", e.Message, e);
            }

            var script = FromJson(root, origin);
            script.FilePath = origin;
            return script;
        }

        /// <summary>由已解析的 JSON 构建脚本对象</summary>
        public static CommandScript FromJson(JsonValue root, string origin = null)
        {
            var script = new CommandScript();

            // 形式一：顶层就是指令数组
            if (root.IsArray)
            {
                AppendEntries(script, root);
                return script;
            }

            if (!root.IsObject)
            {
                throw new CommandFileException(origin ?? "<memory>",
                    $"指令文件根节点必须是对象或数组，实际为 {root.Type}");
            }

            script.Version = root.GetInt("version", 1);
            script.Name = root.GetString("name", null);
            script.Once = root.GetBool("once", false);

            JsonValue commands = JsonValue.Null;
            foreach (var key in CommandsKeys)
            {
                if (root.TryGet(key, out commands) && commands.IsArray)
                {
                    break;
                }
                commands = JsonValue.Null;
            }

            if (commands.IsArray)
            {
                AppendEntries(script, commands);
                return script;
            }

            // 形式三：单条指令（根节点自带 id）
            var singleId = root.GetString("id", null) ?? root.GetString("command", null);
            if (!string.IsNullOrEmpty(singleId))
            {
                script.Commands.Add(ParseEntry(root, 0));
                return script;
            }

            throw new CommandFileException(origin ?? "<memory>",
                "指令文件缺少 commands 数组，且根节点也不是一条指令（缺少 id 字段）");
        }

        private static void AppendEntries(CommandScript script, JsonValue array)
        {
            var items = array.ArrayValue;
            for (var i = 0; i < items.Count; i++)
            {
                script.Commands.Add(ParseEntry(items[i], script.Commands.Count));
            }
        }

        /// <summary>解析单条指令节点</summary>
        public static CommandEntry ParseEntry(JsonValue node, int index)
        {
            if (node == null || !node.IsObject)
            {
                throw new CommandFileException(null, $"第 {index} 条指令不是 JSON 对象");
            }

            var id = node.GetString("id", null)
                     ?? node.GetString("command", null)
                     ?? node.GetString("type", null);

            if (string.IsNullOrWhiteSpace(id))
            {
                throw new CommandFileException(null, $"第 {index} 条指令缺少 id 字段");
            }

            var entry = new CommandEntry
            {
                Id = id.Trim(),
                Index = index,
                Enabled = node.GetBool("enabled", true) && !node.GetBool("disabled", false),
                Comment = node.GetString("comment", null) ?? node.GetString("note", null)
            };

            // 显式 params 优先
            JsonValue parameters = JsonValue.Null;
            foreach (var key in ParamsKeys)
            {
                if (node.TryGet(key, out parameters))
                {
                    break;
                }
                parameters = JsonValue.Null;
            }

            if (parameters.IsObject || parameters.IsArray)
            {
                entry.Params = parameters;
            }
            else
            {
                // 隐式参数：除保留键外的所有字段都作为参数
                var implicitParams = JsonValue.NewObject();
                foreach (var key in node.Keys)
                {
                    if (ReservedKeys.Contains(key))
                    {
                        continue;
                    }
                    implicitParams.Set(key, node[key]);
                }
                entry.Params = implicitParams;
            }

            return entry;
        }

        #endregion
    }
}
