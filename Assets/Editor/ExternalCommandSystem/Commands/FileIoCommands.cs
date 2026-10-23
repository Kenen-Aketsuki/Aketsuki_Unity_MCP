using System.IO;
using System.Text;

namespace CivilRev.CommandSystem
{
    /// <summary>
    /// 写入文本文件
    /// <para>参数：path（必填，支持相对/绝对路径）、content（必填）、append（默认 false）、encoding（默认 utf8）</para>
    /// </summary>
    [Command("file.write", Description = "写入文本文件",
        Usage = "path: string, content: string, append: bool")]
    public sealed class WriteTextFileCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var path = context.Args.RequireString("path");
            var content = context.Expand(context.Args.GetString("content", string.Empty));
            var append = context.Args.GetBool("append", false);
            var fullPath = CommandPathUtil.Resolve(path, CommandPathUtil.GetDirectoryOf(context.Source?.FilePath));

            if (context.DryRun)
            {
                return CommandResult.Skip($"dry-run：将写入 {fullPath}");
            }

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var encoding = new UTF8Encoding(false);
            if (append && File.Exists(fullPath))
            {
                // 追加语义保留，但仍走原子路径：读出原内容拼接后整体替换，
                // 避免"追加到一半失败"留下半行内容。
                var original = File.ReadAllText(fullPath, encoding);
                AtomicFileWriter.WriteAllText(fullPath, original + content, encoding);
            }
            else
            {
                AtomicFileWriter.WriteAllText(fullPath, content, encoding);
            }

#if UNITY_EDITOR
            // 写入 Assets 目录内时请求刷新资源数据库（用「根 + 分隔符」前缀判断，避免 AssetsEVIL 误命中）。
            // 不再逐条 Refresh：标记脏位，由调用方在批次结束时统一刷新一次。
            if (CommandPathUtil.IsWithinRoot(fullPath, CommandPathUtil.AssetsRoot))
            {
                if (context.Runner != null)
                {
                    context.Runner.MarkAssetsDirty();
                }
                else
                {
                    // 没有运行器（手动构造上下文）时退回立即刷新，保证行为不退步
                    UnityEditor.AssetDatabase.Refresh();
                }
            }
#endif

            return CommandResult.Ok($"已写入 {fullPath}（{content.Length} 字符）", fullPath);
        }
    }

    /// <summary>
    /// 读取文本文件
    /// <para>参数：path（必填）、variable（可选，把内容存入共享变量）、log（默认 true，是否打印内容）</para>
    /// </summary>
    [Command("file.read", Description = "读取文本文件", Usage = "path: string, variable: string, log: bool")]
    public sealed class ReadTextFileCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var path = context.Args.RequireString("path");
            var fullPath = CommandPathUtil.Resolve(path, CommandPathUtil.GetDirectoryOf(context.Source?.FilePath));

            if (!File.Exists(fullPath))
            {
                throw new CommandArgumentException(context.CommandId, "path", $"文件不存在：{fullPath}");
            }

            var content = File.ReadAllText(fullPath, Encoding.UTF8);

            var variable = context.Args.GetString("variable", null);
            if (!string.IsNullOrEmpty(variable))
            {
                context.SetVariable(variable, content);
            }

            if (context.Args.GetBool("log", true))
            {
                CommandLog.Info($"读取 {fullPath}：\n{content}");
            }

            return CommandResult.Ok($"已读取 {fullPath}（{content.Length} 字符）", content);
        }
    }
}
