using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 进入 Play 模式——**运行时指令组里的入口**，受控制台开关约束。
    ///
    /// <para>
    /// 与常规指令 <c>editor.play</c> 的分工：<c>editor.play</c> 是编辑期基础设施（不受开关影响，
    /// 任何时候都能用）；<c>runtime.play</c> 属于"运行时干预"这一组，开关关着就失败——
    /// 这样"AI 能操作正在运行的游戏"这件事只由**一个**开关控制，不会出现
    /// "开关关着但 AI 照样能把游戏跑起来"的口子。
    /// </para>
    ///
    /// <para>
    /// <b>为什么要处理脏场景：</b><c>EditorApplication.isPlaying = true</c> 在有未保存改动时会弹
    /// "Save scene?" 模态框——无人值守时编辑器就卡在那里，客户端只看到超时。所以这里默认
    /// <c>onDirty=abort</c>：有脏场景就失败并列出清单；显式传 <c>onDirty=save</c> 才替用户保存。
    /// </para>
    ///
    /// <para>参数：onDirty（abort / save，默认 abort）</para>
    /// </summary>
    [Command("runtime.play",
        Description = "进入 Play 模式（运行时指令组，受开关约束；会触发域重载）",
        Usage = "onDirty: abort|save",
        EditorOnly = true, Runtime = true, RuntimePhase = RuntimePhase.NotPlaying)]
    public sealed class RuntimePlayCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var data = JsonValue.NewObject();
            data.Set("isPlayingBefore", JsonValue.Of(EditorApplication.isPlaying));

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new CommandException(
                    "Unity 正在编译/导入，此时切 Play 会拿到旧程序集。等空闲再试（可用 editor.state 查询）");
            }

            if (EditorApplication.isPlaying)
            {
                // 正常路径由门禁挡下（phase=NotPlaying），这里只是兜底
                return CommandResult.Ok("已在 Play 模式中，无需进入", data);
            }

            var onDirty = (context.Args.GetString("onDirty", "abort") ?? "abort").Trim().ToLowerInvariant();
            if (onDirty != "abort" && onDirty != "save")
            {
                return CommandResult.Fail($"不支持的 onDirty：'{onDirty}'（可选 abort / save）", data);
            }

            var dirtyScenes = CollectDirtyScenes();
            data.Set("dirtySceneCount", JsonValue.Of(dirtyScenes.Count));
            if (dirtyScenes.Count > 0)
            {
                var list = JsonValue.NewArray();
                foreach (var scene in dirtyScenes)
                {
                    var item = JsonValue.NewObject();
                    item.Set("label", JsonValue.Of(SceneScope.Label(scene)));
                    item.Set("path", JsonValue.Of(scene.path ?? string.Empty));
                    list.Add(item);
                }

                data.Set("dirtyScenes", list);

                if (onDirty == "abort")
                {
                    return CommandResult.Fail(
                        $"有 {dirtyScenes.Count} 个场景存在未保存改动，直接进 Play 会弹保存对话框把编辑器卡住。" +
                        "先 scene.save 保存，或显式传 onDirty=save 让本指令保存后再进 Play。", data);
                }

                foreach (var scene in dirtyScenes)
                {
                    if (string.IsNullOrEmpty(scene.path))
                    {
                        return CommandResult.Fail(
                            $"场景 '{SceneScope.Label(scene)}' 是尚未保存过的新场景（无资产路径），无法自动保存。" +
                            "请先手动 Save As 指定路径，或关掉它再试。", data);
                    }
                }

                var saved = JsonValue.NewArray();
                foreach (var scene in dirtyScenes)
                {
                    if (!EditorSceneManager.SaveScene(scene))
                    {
                        return CommandResult.Fail($"保存场景 '{SceneScope.Label(scene)}' 失败，已中止进 Play", data);
                    }

                    saved.Add(JsonValue.Of(SceneScope.Label(scene)));
                }

                data.Set("savedScenes", saved);
            }

            EditorApplication.isPlaying = true;
            data.Set("isPlayingAfter", JsonValue.Of(EditorApplication.isPlaying));
            data.Set("willChangePlayMode", JsonValue.Of(EditorApplication.isPlayingOrWillChangePlaymode));

            return CommandResult.Ok(
                "已请求进入 Play 模式（会触发域重载，HTTP 服务随后自动恢复，请轮询 status 直到 playing=true）", data);
        }

        private static System.Collections.Generic.List<Scene> CollectDirtyScenes()
        {
            var list = new System.Collections.Generic.List<Scene>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isLoaded && scene.isDirty)
                {
                    list.Add(scene);
                }
            }

            return list;
        }
    }

    /// <summary>
    /// 退出 Play 模式——运行时指令组里的"恢复现场"入口。
    ///
    /// <para>
    /// <see cref="RuntimePhase.Any"/>：编辑模式下调用直接成功返回（幂等），
    /// 因为"确保不在 Play"这个诉求在编辑模式已经满足，报错反而逼调用方去判状态。
    /// 但**开关仍然生效**——关着就失败，和其它 runtime.* 一致。
    /// </para>
    /// </summary>
    [Command("runtime.stop",
        Description = "退出 Play 模式（运行时指令组，受开关约束；编辑模式下幂等）",
        Usage = "",
        EditorOnly = true, Runtime = true, RuntimePhase = RuntimePhase.Any)]
    public sealed class RuntimeStopCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var data = JsonValue.NewObject();
            data.Set("isPlayingBefore", JsonValue.Of(EditorApplication.isPlaying));

            if (!EditorApplication.isPlaying)
            {
                data.Set("isPlayingAfter", JsonValue.Of(false));
                return CommandResult.Ok("不在 Play 模式中，无需退出", data);
            }

            EditorApplication.isPlaying = false;
            data.Set("isPlayingAfter", JsonValue.Of(EditorApplication.isPlaying));
            return CommandResult.Ok(
                "已请求退出 Play 模式（同样会触发域重载，稍后轮询 status 确认 playing=false）", data);
        }
    }
}
