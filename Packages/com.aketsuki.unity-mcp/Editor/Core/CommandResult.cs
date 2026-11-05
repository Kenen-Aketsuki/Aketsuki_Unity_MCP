using System.Collections.Generic;

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

        private readonly List<string> _warnings = new List<string>();

        /// <summary>
        /// 与"成功/失败"无关、但调用方必须知道的提醒（如"参数名写错了，按默认值执行的"）。
        /// <para>
        /// 独立于 <see cref="Message"/> 的原因：message 是"这条指令做了什么"，
        /// 警告是"你这次调用有地方不对，但还不到失败"——混进 message 会被当成正常输出扫过去。
        /// </para>
        /// </summary>
        public IReadOnlyList<string> Warnings => _warnings;

        /// <summary>追加一条警告</summary>
        public CommandResult AddWarning(string warning)
        {
            if (!string.IsNullOrEmpty(warning))
            {
                _warnings.Add(warning);
            }

            return this;
        }

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
