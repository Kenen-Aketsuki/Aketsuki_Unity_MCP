using System;

namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// 运行时指令要求编辑器处于哪个 Play 状态。
    /// <para>
    /// 大多数 runtime.* 只能在 Play 中执行（<see cref="Playing"/>）；但"进/出 Play"这两条本身就是
    /// 切换动作，用 <see cref="NotPlaying"/> / <see cref="Any"/> 才能表达。
    /// </para>
    /// </summary>
    public enum RuntimePhase
    {
        /// <summary>必须在 Play 模式中（默认）</summary>
        Playing = 0,

        /// <summary>必须在编辑模式（用于 runtime.play）</summary>
        NotPlaying = 1,

        /// <summary>不限（用于 runtime.stop 这类幂等的"恢复现场"指令）</summary>
        Any = 2
    }

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

        /// <summary>
        /// 运行时指令要求的 Play 状态，默认 <see cref="RuntimePhase.Playing"/>。
        /// <para>
        /// 仅对 <see cref="Runtime"/>=<c>true</c> 的指令生效：<c>runtime.play</c> 要
        /// <see cref="RuntimePhase.NotPlaying"/>（编辑模式下才能进），<c>runtime.stop</c> 用
        /// <see cref="RuntimePhase.Any"/>（编辑模式下调用应当无害地成功）。
        /// 无论取哪个值，都仍然受控制台开关约束——开关关着一律失败。
        /// </para>
        /// </summary>
        public RuntimePhase RuntimePhase { get; set; } = RuntimePhase.Playing;

        /// <summary>参数说明，用于 help 输出</summary>
        public string Usage { get; set; } = string.Empty;

        /// <summary>
        /// 标记该指令属于「敏感操作」：未开启控制台的「全权委托」时，必须由人点「允许」才会执行。
        /// <para>
        /// 哪些算敏感：<b>会改变编辑器整体状态、且 AI 自作主张就很难挽回</b>——进/出 Play 模式、
        /// 切换当前场景。这类指令一旦被 AI 误调，用户手上的工作现场（运行中的游戏、未保存的场景）
        /// 会直接消失，事后没有任何撤销手段。
        /// </para>
        /// <para>
        /// 门禁实现在 <see cref="Editor.ConsentGate"/>，位置与 <see cref="Runtime"/> 一致（都在
        /// <c>CommandRunner.ExecuteEntry</c>），因此 HTTP / file.run / 控制台三条路径都拦得住。
        /// 不同的是：运行时指令是"开关没开就失败"，敏感操作是"开关没开就弹窗问人"。
        /// </para>
        /// </summary>
        public bool RequiresConsent { get; set; }

        /// <summary>
        /// 确认弹窗里展示的动作描述（如"退出 Play 模式，运行中的游戏会立即结束"）。
        /// <para>留空时退回 <see cref="Description"/>。写清楚"会发生什么"，人才判断得动。</para>
        /// </summary>
        public string ConsentMessage { get; set; }

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
