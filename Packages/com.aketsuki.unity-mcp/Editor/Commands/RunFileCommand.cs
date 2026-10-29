namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// 执行另一个指令文件（支持嵌套组合，自带循环引用保护）
    /// <para>参数：path（必填，支持相对/绝对路径）</para>
    /// </summary>
    [Command("file.run", Description = "执行另一个指令文件", Usage = "path: string")]
    public sealed class RunFileCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var path = context.Args.RequireString("path");
            var runner = context.Runner ?? new CommandRunner();

            // 相对路径以引用它的指令文件所在目录为基准，方便同一目录下的文件互相引用
            var baseDirectory = CommandPathUtil.GetDirectoryOf(context.Source?.FilePath);

            var report = runner.RunFile(path, baseDirectory);
            return report.HasError
                ? CommandResult.Fail($"指令文件 {path} 执行失败（{report.Failed} 条）", report)
                : CommandResult.Ok($"指令文件 {path} 执行完成（{report.Succeeded}/{report.Total}）", report);
        }
    }
}
