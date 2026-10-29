namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// 单条指令的执行状态
    /// </summary>
    public enum CommandStatus
    {
        /// <summary>执行成功</summary>
        Success,

        /// <summary>执行失败</summary>
        Failed,

        /// <summary>被跳过（enabled=false、dry-run、条件不满足等）</summary>
        Skipped,

        /// <summary>找不到对应的指令 ID</summary>
        UnknownCommand
    }

    /// <summary>
    /// 指令执行结果
    /// </summary>
    public sealed class CommandResult
    {
        /// <summary>执行状态</summary>
        public CommandStatus Status { get; }

        /// <summary>结果描述，会输出到日志</summary>
        public string Message { get; }

        /// <summary>可选的输出数据，会写入运行报告</summary>
        public object Data { get; }

        private CommandResult(CommandStatus status, string message, object data)
        {
            Status = status;
            Message = message ?? string.Empty;
            Data = data;
        }

        /// <summary>是否成功</summary>
        public bool Success => Status == CommandStatus.Success;

        /// <summary>构造成功结果</summary>
        public static CommandResult Ok(string message = null, object data = null)
        {
            return new CommandResult(CommandStatus.Success, message, data);
        }

        /// <summary>构造失败结果</summary>
        public static CommandResult Fail(string message, object data = null)
        {
            return new CommandResult(CommandStatus.Failed, message, data);
        }

        /// <summary>构造跳过结果</summary>
        public static CommandResult Skip(string message = null, object data = null)
        {
            return new CommandResult(CommandStatus.Skipped, message, data);
        }

        /// <summary>构造"未知指令"结果</summary>
        public static CommandResult Unknown(string commandId)
        {
            return Unknown(commandId, null);
        }

        /// <summary>
        /// 构造"未知指令"结果，并用 <paramref name="message"/> 覆盖默认文案。
        /// <para>
        /// 用于"指令存在但因依赖缺失被剔除"的情形：默认的"未注册的指令 ID"藏起了真正原因，
        /// 调用方会以为是自己拼错了 ID。
        /// </para>
        /// </summary>
        public static CommandResult Unknown(string commandId, string message)
        {
            return new CommandResult(
                CommandStatus.UnknownCommand,
                string.IsNullOrEmpty(message) ? $"未注册的指令 ID：{commandId}" : message,
                null);
        }

        public override string ToString()
        {
            return $"[{Status}] {Message}";
        }
    }
}
