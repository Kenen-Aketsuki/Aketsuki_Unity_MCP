using System;
using System.Collections.Generic;

namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// 指令文件中的一条指令
    /// </summary>
    public sealed class CommandEntry
    {
        /// <summary>指令 ID</summary>
        public string Id { get; set; }

        /// <summary>参数节点（JSON 对象）</summary>
        public JsonValue Params { get; set; } = JsonValue.EmptyObject;

        /// <summary>是否启用；false 时执行阶段直接跳过</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>备注，不影响执行</summary>
        public string Comment { get; set; }

        /// <summary>在文件中的序号（从 0 开始）</summary>
        public int Index { get; set; }

        /// <summary>构造参数包</summary>
        public CommandArgs CreateArgs()
        {
            return new CommandArgs(Params ?? JsonValue.EmptyObject, Id);
        }

        public override string ToString()
        {
            var state = Enabled ? string.Empty : " (disabled)";
            return $"#{Index} {Id}{state} {Params?.ToJson(false)}";
        }
    }

    /// <summary>
    /// 一个指令文件解析后的结果
    /// </summary>
    public sealed class CommandScript
    {
        /// <summary>文件绝对路径；由文本直接解析时为 null</summary>
        public string FilePath { get; set; }

        /// <summary>文件名称（用于日志）</summary>
        public string Name { get; set; }

        /// <summary>格式版本</summary>
        public int Version { get; set; } = 1;

        /// <summary>是否只执行一次（配合自动运行器去重使用）</summary>
        public bool Once { get; set; }

        /// <summary>指令列表</summary>
        public List<CommandEntry> Commands { get; } = new List<CommandEntry>();

        /// <summary>展示名</summary>
        public string DisplayName => !string.IsNullOrEmpty(Name) ? Name : FilePath ?? "<memory>";

        /// <summary>手动构建一条指令的脚本（便于代码内直接调用）</summary>
        public static CommandScript Single(string commandId, JsonValue parameters = null)
        {
            var script = new CommandScript { Name = commandId };
            script.Commands.Add(new CommandEntry
            {
                Id = commandId,
                Params = parameters ?? JsonValue.EmptyObject
            });
            return script;
        }
    }

    /// <summary>
    /// 单条指令的执行记录
    /// </summary>
    public sealed class CommandRecord
    {
        /// <summary>指令 ID</summary>
        public string CommandId { get; set; }

        /// <summary>序号</summary>
        public int Index { get; set; }

        /// <summary>嵌套层级</summary>
        public int Depth { get; set; }

        /// <summary>执行状态</summary>
        public CommandStatus Status { get; set; }

        /// <summary>结果描述</summary>
        public string Message { get; set; }

        /// <summary>耗时（毫秒）</summary>
        public double ElapsedMs { get; set; }

        /// <summary>输出数据</summary>
        public object Data { get; set; }

        public override string ToString()
        {
            var indent = Depth > 0 ? new string(' ', Depth * 2) : string.Empty;
            return $"{indent}#{Index} {CommandId} -> {Status} {Message}";
        }
    }

    /// <summary>
    /// 一次运行（文件级）的汇总报告
    /// </summary>
    public sealed class CommandRunReport
    {
        /// <summary>来源文件路径</summary>
        public string SourcePath { get; set; }

        /// <summary>总指令数</summary>
        public int Total { get; set; }

        /// <summary>成功数</summary>
        public int Succeeded { get; set; }

        /// <summary>失败数</summary>
        public int Failed { get; set; }

        /// <summary>跳过数</summary>
        public int Skipped { get; set; }

        /// <summary>总耗时（毫秒）</summary>
        public double ElapsedMs { get; set; }

        /// <summary>逐条记录</summary>
        public List<CommandRecord> Records { get; } = new List<CommandRecord>();

        /// <summary>是否存在失败（含未知指令）</summary>
        public bool HasError => Failed > 0;

        /// <summary>追加一条记录并同步统计</summary>
        public void Add(CommandRecord record)
        {
            Records.Add(record);
            switch (record.Status)
            {
                case CommandStatus.Success:
                    Succeeded++;
                    break;
                case CommandStatus.Skipped:
                    Skipped++;
                    break;
                default:
                    Failed++;
                    break;
            }
        }

        /// <summary>生成可读摘要</summary>
        public string ToSummary()
        {
            return $"指令执行完毕：{SourcePath ?? "<memory>"}，" +
                   $"共 {Total} 条（成功 {Succeeded} / 失败 {Failed} / 跳过 {Skipped}），耗时 {ElapsedMs:F1} ms";
        }
    }
}
