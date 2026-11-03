using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 截取 SceneView 当前画面（含 Gizmos / 网格 / Overlay 工具栏），存成 PNG。
    /// <para>
    /// <b>这是"看见"而不是"读到"</b>：<c>scene.hierarchy</c> 给的是 YAML 级真相，
    /// 摆位歪了、UI 被裁、材质丢引用、光照过曝这类问题在文本里一个字段都不报错。
    /// 画面走窗口截屏（PrintWindow），不用离屏渲染——离屏拿不到 Gizmos，见 FEAT-019 的实测记录。
    /// </para>
    /// <para>
    /// 默认<b>不动相机</b>：截的就是用户当前视角。要"对准某个对象"得改视角，本指令不做这件事。
    /// </para>
    /// <para>
    /// 参数：path（默认 &lt;指令目录&gt;/screenshots/scene-latest.png，覆盖写）、
    /// crop（frame 只裁场景画布 / window 整个 SceneView 窗口 / full 整个 Unity 窗口）、
    /// view（SceneView 索引，缺省取当前激活的那个）、
    /// select（截图前临时选中某个节点，截完还原）、
    /// scale（后置缩放，默认 1）。
    /// </para>
    /// </summary>
    [Command("capture.scene", Description = "截取 SceneView 画面（含 Gizmos/网格/Overlay），返回 PNG 路径与实际像素",
        Usage = "path: string, crop: frame|window|full, view: int, select: any, scale: number",
        EditorOnly = true)]
    public sealed class CaptureSceneCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            if (!WindowCapture.IsSupported)
            {
                return CommandResult.Fail($"窗口截屏只支持 Windows 编辑器（当前平台：{Application.platform}）");
            }

            var crop = CaptureSupport.ParseCrop(context);

            if (context.DryRun)
            {
                return CommandResult.Skip(
                    $"dry-run：将截取 SceneView（crop={crop}）到 {CaptureSupport.DefaultOutputPath("scene")}");
            }

            SceneView view;
            string error;
            if (!CaptureSupport.TryGetSceneView(context, out view, out error))
            {
                return CommandResult.Fail(error);
            }

            var warnings = new List<string>();
            var hasCamera = view.camera != null;

            if (crop == CaptureCrop.Full)
            {
                warnings.Add("crop=full：图里包含标题栏、菜单栏和其它面板，场景画面只占其中一块");
            }

            if (!hasCamera && crop == CaptureCrop.Frame)
            {
                warnings.Add("SceneView 相机尚未初始化（窗口可能刚打开），已退回整窗裁剪");
            }

            string selectError;
            using (var scope = CaptureSupport.SelectionScope.Apply(context, out selectError))
            {
                if (scope == null)
                {
                    return CommandResult.Fail(selectError);
                }

                if (context.Args.Has("select"))
                {
                    // 选中高亮是渲染结果，而一条指令只跑一帧：这里改的 Selection 还来不及画进窗口表面，
                    // 截出来的通常仍是无高亮的那一帧。不在同步指令里硬等帧（主线程等待会死锁），如实说明。
                    warnings.Add("select 已临时选中目标并在截完后还原，但选中高亮属于渲染结果：本次截图通常仍是改选中之前的帧，" +
                                 "别指望用 select 拍出 selected gizmo 的高亮圈");
                }

                return CaptureSupport.CaptureWindow(context, view, "scene", crop, warnings, null);
            }
        }
    }
}
