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

        /// <summary>
        /// 标记该指令属于「运行时（Play 模式）指令」，与常规编辑指令分开。
        /// <para>
        /// 带本标记的指令受两道门禁约束（见 <see cref="RuntimeCommandGate"/>）：
        /// ① 控制台开关「启用运行时指令」默认关闭，关闭时一律执行失败并说明原因；
        /// ② 必须处于 Play 模式，编辑模式下一律失败。
        /// </para>
        /// <para>
        /// 之所以要有独立开关：这类指令会**改变正在运行的游戏状态**（点按钮、拖列表、触发钩子），
        /// 误调一次可能写存档、发网络请求、甚至退出游戏，因此不能跟只读的编辑指令共用一套默认放行策略。
        /// </para>
        /// </summary>
        public bool Runtime { get; set; }

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
