using UnityEditor;
using CivilRev.CommandSystem.Editor;

namespace CivilRev.CommandSystem
{
    /// <summary>
    /// 运行时（Play 模式）指令的统一门禁。
    ///
    /// <para>
    /// <b>为什么需要它：</b>运行时指令会改变正在跑的游戏状态——点一个按钮可能触发存档、发网络请求、
    /// 甚至退出游戏。这与 <c>asset.refresh</c> 那类"只读/只写资产"的编辑指令风险等级完全不同，
    /// 所以不能共用"默认可调"的策略。
    /// </para>
    ///
    /// <para>
    /// <b>两道门禁，顺序固定：</b>
    /// <list type="number">
    /// <item>控制台开关（<see cref="CommandSystemSettings.RuntimeCommandsEnabled"/>，默认 <c>false</c>）——
    /// 关着就一律失败，报错里告诉调用方去哪儿打开；</item>
    /// <item>Play 模式——开关打开了但编辑器不在 Play，也一律失败，避免"点了半天没反应"。</item>
    /// </list>
    /// 先判开关再判 Play：开关没开是**配置问题**，调用方应该先解决它；此时报"不在 Play 模式"会误导。
    /// </para>
    ///
    /// <para>
    /// <b>为什么集中在执行器里拦：</b>指令可能来自 HTTP、<c>file.run</c>、控制台自动扫描三条路径，
    /// 只有 <see cref="CommandRunner.ExecuteEntry"/> 是共同入口。放在这里就不会漏掉某条路径，
    /// 也不需要每条指令自己写一遍守卫。
    /// </para>
    ///
    /// <para>
    /// <b>为什么状态要缓存（别改回去）：</b><c>/v1/status</c> 是在 HTTP 监听线程（非主线程）上拼装响应的，
    /// 而 <c>EditorApplication.isPlaying</c> 与 <c>EditorPrefs.GetBool</c> 都是**主线程专用** API——
    /// 在后台线程调用会抛 <c>GetBool can only be called from the main thread</c>，
    /// 表现是整个 <c>/v1/status</c> 变成 HTTP 500（实测踩过一次）。
    /// 因此这里每帧在主线程刷新一份快照，后台线程只读快照，最多滞后一帧。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    internal static class RuntimeCommandGate
    {
        /// <summary>主线程刷新出来的开关快照</summary>
        private static bool _enabled;

        /// <summary>主线程刷新出来的 Play 状态快照</summary>
        private static bool _isPlaying;

        static RuntimeCommandGate()
        {
            EditorApplication.update += Refresh;
            Refresh();
        }

        /// <summary>在主线程刷新快照（每帧一次，成本是一次 EditorPrefs 读 + 一个 bool）</summary>
        private static void Refresh()
        {
            _isPlaying = EditorApplication.isPlaying;
            _enabled = CommandSystemSettings.RuntimeCommandsEnabled;
        }

        /// <summary>控制台窗口里的菜单路径，报错文案里给出，省得用户去找</summary>
        internal const string ConsoleMenuPath = "工具 / 指令系统 / 指令控制台";

        /// <summary>开关在控制台里的显示名（与窗口里保持一致）</summary>
        internal const string ToggleLabel = "启用运行时指令";

        /// <summary>
        /// 开关当前是否打开（按工程保存，见 <see cref="CommandSystemSettings.RuntimeCommandsEnabled"/>）。
        /// <para>读的是主线程快照，可在任意线程安全调用。</para>
        /// </summary>
        internal static bool Enabled
        {
            get { return _enabled; }
        }

        /// <summary>编辑器是否处于 Play 模式（暂停也算在 Play 里）；同样是主线程快照</summary>
        internal static bool IsPlaying
        {
            get { return _isPlaying; }
        }

        /// <summary>
        /// 检查能否执行；返回 <c>null</c> 表示放行，否则返回给调用方看的中文原因。
        /// </summary>
        internal static string Check(string commandId)
        {
            if (!Enabled)
            {
                return DisabledMessage(commandId);
            }

            if (!IsPlaying)
            {
                return NotPlayingMessage(commandId);
            }

            return null;
        }

        /// <summary>开关关闭时的失败原因</summary>
        internal static string DisabledMessage(string commandId)
        {
            return $"运行时指令未启用：'{commandId}' 属于 Play 模式干预指令，默认关闭。" +
                   $"请在 Unity 菜单 {ConsoleMenuPath} →「运行时指令（Play 模式）」勾选「{ToggleLabel}」后重试。";
        }

        /// <summary>开关已开但不在 Play 模式时的失败原因</summary>
        internal static string NotPlayingMessage(string commandId)
        {
            return $"运行时指令只能在 Play 模式执行：'{commandId}' 需要正在运行的游戏，当前编辑器处于编辑模式。" +
                   "先用 editor.play 进入 Play 模式（或手动点 Play）再重试。";
        }

        /// <summary>
        /// 失败结果附带的结构化状态，供客户端机器判定（不必解析中文文案）。
        /// </summary>
        internal static JsonValue StatusJson(string commandId)
        {
            var data = JsonValue.NewObject();
            data.Set("runtime", JsonValue.Of(true));
            data.Set("commandId", JsonValue.Of(commandId ?? string.Empty));
            data.Set("enabled", JsonValue.Of(Enabled));
            data.Set("playing", JsonValue.Of(IsPlaying));
            data.Set("toggle", JsonValue.Of(ToggleLabel));
            data.Set("menu", JsonValue.Of(ConsoleMenuPath));
            return data;
        }

        /// <summary>
        /// <c>/v1/status</c> 里的运行时指令摘要：一眼看出"为什么我的 runtime.* 调不动"。
        /// </summary>
        internal static JsonValue SummaryJson(int commandCount)
        {
            var data = JsonValue.NewObject();
            data.Set("enabled", JsonValue.Of(Enabled));
            data.Set("playing", JsonValue.Of(IsPlaying));
            data.Set("commandCount", JsonValue.Of(commandCount));
            data.Set("toggle", JsonValue.Of(ToggleLabel));
            data.Set("menu", JsonValue.Of(ConsoleMenuPath));
            return data;
        }
    }
}
