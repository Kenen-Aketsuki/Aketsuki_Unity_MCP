using System;

namespace CivilRev.CommandSystem
{
    /// <summary>
    /// 指令标记：声明一个类是"可被 JSON 指令调用的指令"，并指定它的唯一字符串 ID。
    /// <para>
    /// 扩展方式：新建一个类，实现 <see cref="ICommand"/>，加上本特性即可，
    /// 无需修改任何已有代码（注册表会在运行期自动扫描）。
    /// </para>
    /// </summary>
    /// <example>
    /// <code>
    /// [Command("my.spawn", Description = "生成单位")]
    /// public sealed class SpawnCommand : ICommand
    /// {
    ///     public CommandResult Execute(CommandContext context)
    ///     {
    ///         var name = context.Args.RequireString("name");
    ///         return CommandResult.Ok("生成 " + name);
    ///     }
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class CommandAttribute : Attribute
    {
        /// <summary>指令唯一 ID，建议使用小写字母与点分命名（如 "asset.refresh"）</summary>
        public string Id { get; }

        /// <summary>指令的人类可读描述，用于 help / 编辑器窗口展示</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// 标记该指令依赖 UnityEditor API（AssetDatabase / Undo / 场景操作等）
        /// <para>
        /// 整个 CommandSystem 已整体置于 Editor 目录下，不会打进玩家包，
        /// 因此该标记不再承担"剔除"职责，只用于控制台窗口的分组展示与文档说明。
        /// </para>
        /// </summary>
        public bool EditorOnly { get; set; }

        /// <summary>参数说明，用于 help 输出</summary>
        public string Usage { get; set; } = string.Empty;

        /// <summary>
        /// 标记一个指令类
        /// </summary>
        /// <param name="id">全局唯一指令 ID，大小写不敏感</param>
        public CommandAttribute(string id)
        {
            Id = id;
        }
    }
}
