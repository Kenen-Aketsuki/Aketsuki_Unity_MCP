using UnityEngine;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 受控进入 Play 模式。
    /// <para>
    /// 注意：进 Play 会触发域重载，HTTP 指令服务会重启（新域按设置自动恢复）——
    /// 调用方要像等编译一样轮询 <c>status</c>。
    /// </para>
    /// <para>
    /// <b>内存水位前置检查（IMPR-008）：</b>长跑的编辑器进 Play 时会弹原生 OOM 框，
    /// 结果是 HTTP 服务消失、MCP 全断，只能人工重启。所以这里先采样，
    /// 超阈值直接拒绝并说明原因，<c>force: true</c> 可强行放行。
    /// 检查<b>只作用于本指令</b>，编辑器自己的 Play 按钮不受影响。
    /// </para>
    /// <para>参数：force（默认 false；内存水位超阈值时仍要进 Play）</para>
    /// </summary>
    [Command("editor.play", Description = "进入 Play 模式（会触发域重载，服务随后自动恢复；内存水位过高时拒绝）",
        Usage = "force: bool", EditorOnly = true,
        RequiresConsent = true,
        ConsentMessage = "进入 Play 模式：编辑器会编译并域重载，游戏开始运行，当前编辑现场会被打断。")]
    public sealed class EditorPlayCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
#if UNITY_EDITOR
            if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorApplication.isUpdating)
            {
                throw new CommandException("Unity 正在编译/导入，先等空闲再进出 Play 模式（可用 editor.state 查询）");
            }

            if (UnityEditor.EditorApplication.isPlaying)
            {
                return CommandResult.Ok("已在 Play 模式中，无需切换", true);
            }

            var force = context.Args.GetBool("force", false);
            var data = JsonValue.NewObject();
            string memoryMessage;
            if (PlayModeMemoryGuard.ShouldBlock(data, force, out memoryMessage))
            {
                return CommandResult.Fail(memoryMessage, data);
            }

            UnityEditor.EditorApplication.isPlaying = true;
            if (memoryMessage != null)
            {
                // force 放行的提示也要回传，别让"这条到底检没检查"变成疑问
                data.Set("memoryNotice", JsonValue.Of(memoryMessage));
            }

            return CommandResult.Ok(
                "已请求进入 Play 模式（域重载后 HTTP 服务会自动恢复，请轮询 status）", data);
#else
            throw new CommandException($"指令 '{context.CommandId}' 只能在编辑器内执行");
#endif
        }
    }

    /// <summary>退出 Play 模式（未在 Play 中时为幂等 no-op）。</summary>
    [Command("editor.stop", Description = "退出 Play 模式（幂等）",
        Usage = "", EditorOnly = true,
        RequiresConsent = true,
        ConsentMessage = "退出 Play 模式：运行中的游戏会立即结束，未保存的运行时状态全部丢失，无法撤销。")]
    public sealed class EditorStopCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
#if UNITY_EDITOR
            if (!UnityEditor.EditorApplication.isPlaying)
            {
                return CommandResult.Ok("不在 Play 模式中，无需退出", false);
            }

            UnityEditor.EditorApplication.isPlaying = false;
            return CommandResult.Ok("已请求退出 Play 模式", false);
#else
            throw new CommandException($"指令 '{context.CommandId}' 只能在编辑器内执行");
#endif
        }
    }

    /// <summary>
    /// 查询编辑器状态（Play / 暂停 / 编译 / 导入 / 即将切换 Play 模式）。
    /// 供 <c>editor.play</c> 之后的轮询与回归验证前的预检使用。
    /// </summary>
    [Command("editor.state", Description = "查询编辑器状态（isPlaying/isPaused/isCompiling/isUpdating 等）",
        Usage = "", EditorOnly = true)]
    public sealed class EditorStateCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
#if UNITY_EDITOR
            var data = JsonValue.NewObject();
            data.Set("isPlaying", JsonValue.Of(UnityEditor.EditorApplication.isPlaying));
            data.Set("isPaused", JsonValue.Of(UnityEditor.EditorApplication.isPaused));
            data.Set("isCompiling", JsonValue.Of(UnityEditor.EditorApplication.isCompiling));
            data.Set("isUpdating", JsonValue.Of(UnityEditor.EditorApplication.isUpdating));
            data.Set("willChangePlayMode", JsonValue.Of(UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode));
            // IMPR-008：进 Play 前可以先用本指令看一眼水位，不必等 editor.play 拒绝才知道
            data.Set("memory", PlayModeMemoryGuard.Take().ToJson());
            return CommandResult.Ok(
                UnityEditor.EditorApplication.isPlaying ? "Play 模式中" : "编辑模式", data);
#else
            throw new CommandException($"指令 '{context.CommandId}' 只能在编辑器内执行");
#endif
        }
    }

    /// <summary>
    /// 编辑器状态守卫：预览态（Play 模式，含暂停）下禁止触发编译/刷新，
    /// 避免打断用户正在进行的游戏预览，并防止域重载/重导入导致运行状态错乱。
    /// </summary>
    internal static class EditorPlayState
    {
        /// <summary>当前是否处于 Play 模式（运行时指令与场景指令共用这一判断口径）</summary>
        internal static bool IsPlayingNow
        {
            get { return UnityEditor.EditorApplication.isPlaying; }
        }

        /// <summary>
        /// 若编辑器正处于 Play 模式（含暂停），抛出 <see cref="CommandException"/>。
        /// </summary>
        internal static void GuardNotPlaying()
        {
            if (UnityEditor.EditorApplication.isPlaying)
            {
                throw new CommandException("编辑器正处于预览状态，不允许编译与刷新");
            }
        }
    }
}
