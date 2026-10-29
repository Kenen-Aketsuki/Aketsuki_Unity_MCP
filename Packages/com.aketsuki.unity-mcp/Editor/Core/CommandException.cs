using System;

namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// 指令系统基础异常
    /// </summary>
    public class CommandException : Exception
    {
        public CommandException(string message, Exception inner = null) : base(message, inner)
        {
        }
    }

    /// <summary>
    /// 指令参数缺失或类型错误时抛出
    /// </summary>
    public sealed class CommandArgumentException : CommandException
    {
        /// <summary>出错的指令 ID</summary>
        public string CommandId { get; }

        /// <summary>出错的参数名</summary>
        public string ParameterName { get; }

        public CommandArgumentException(string commandId, string parameterName, string message)
            : base($"指令 '{commandId}' 的参数 '{parameterName}' 无效：{message}")
        {
            CommandId = commandId;
            ParameterName = parameterName;
        }
    }

    /// <summary>
    /// 指令文件读取或解析失败时抛出
    /// </summary>
    public sealed class CommandFileException : CommandException
    {
        /// <summary>出错的文件路径</summary>
        public string FilePath { get; }

        public CommandFileException(string filePath, string message, Exception inner = null)
            : base($"指令文件 '{filePath}' 处理失败：{message}", inner)
        {
            FilePath = filePath;
        }
    }
}
