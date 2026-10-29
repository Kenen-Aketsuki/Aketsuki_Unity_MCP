namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// 指令执行体接口
    /// <para>
    /// 每个指令 = 一个独立类 + 一个 <see cref="CommandAttribute"/> 声明的唯一 ID。
    /// 指令类应保持无状态：参数全部从 <see cref="CommandContext.Args"/> 读取，
    /// 跨指令的数据通过 <see cref="CommandContext.Variables"/> 传递。
    /// </para>
    /// </summary>
    public interface ICommand
    {
        /// <summary>
        /// 执行指令
        /// </summary>
        /// <param name="context">包含参数、变量表、运行配置的上下文</param>
        /// <returns>执行结果；抛出任意异常也会被运行器捕获并记为失败</returns>
        CommandResult Execute(CommandContext context);
    }
}
