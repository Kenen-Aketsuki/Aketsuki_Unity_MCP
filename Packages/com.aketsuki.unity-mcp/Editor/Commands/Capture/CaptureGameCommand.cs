using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 截取 GameView 画面。
    /// <para>
    /// 两种状态差别很大，返回里的 <c>source</c> 与 <c>warnings</c> 会把差别说清楚：
    /// </para>
    /// <list type="bullet">
    /// <item><b>Play 中</b>：GameView 真在渲染 → 窗口截屏，所见即所得（含 Screen Space-Overlay 的 UI）。</item>
    /// <item><b>编辑模式</b>：GameView <b>根本不渲染</b> → 只能退到 <c>Camera.Render()</c> 到 RenderTexture，
    /// <b>拿不到 Overlay Canvas 的 UI</b>（它不走相机）。这一点必须写进 warnings：
    /// 没说明"少了什么"的截图比没有截图更危险。</item>
    /// </list>
    /// <para>
    /// 参数：path（默认 &lt;指令目录&gt;/screenshots/game-latest.png）、
    /// crop（frame 剥掉 GameView 工具栏 / window 整个 GameView 窗口；full 仅对 Play 的窗口截屏有意义）、
    /// select（截图前临时选中，截完还原；编辑模式下对画面基本无影响）、scale（后置缩放）。
    /// </para>
    /// </summary>
    [Command("capture.game", Description = "截取 GameView 画面（Play 中为所见即所得；编辑模式降级为相机渲染且不含 Overlay UI）",
        Usage = "path: string, crop: frame|window|full, select: any, scale: number",
        EditorOnly = true)]
    public sealed class CaptureGameCommand : ICommand
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
                    $"dry-run：将截取 GameView（crop={crop}，Play={EditorApplication.isPlaying}）到 {CaptureSupport.DefaultOutputPath("game")}");
            }

            var warnings = new List<string>();
            var gameView = CaptureSupport.FindGameViewWindow();
            var gameViewCamera = CaptureSupport.GetGameViewCamera(gameView);

            string selectError;
            using (var scope = CaptureSupport.SelectionScope.Apply(context, out selectError))
            {
                if (scope == null)
                {
                    return CommandResult.Fail(selectError);
                }

                // ---- Play 中：GameView 真在渲染，窗口截屏就是所见即所得
                if (EditorApplication.isPlaying && gameView != null)
                {
                    return CaptureSupport.CaptureWindow(context, gameView, "game", crop, warnings, gameViewCamera);
                }

                if (EditorApplication.isPlaying && gameView == null)
                {
                    warnings.Add("Play 中但找不到 GameView 窗口（可能被关掉了），已降级为相机渲染");
                }

                // ---- 编辑模式：GameView 不渲染，退到相机渲染
                var camera = CaptureSupport.FindFallbackCamera(gameViewCamera);
                if (camera == null)
                {
                    return CommandResult.Fail(
                        "编辑模式下 GameView 不渲染，且场景里没有可用相机（Camera.main / 已启用相机都为空），无法出图");
                }

                var scale = CaptureSupport.DpiScale;
                int width, height;
                ResolveRenderSize(gameView, camera, scale, out width, out height);

                WindowCapture.Frame frame;
                string error;
                if (!WindowCapture.TryRenderCamera(camera, width, height, out frame, out error))
                {
                    return CommandResult.Fail(error);
                }

                frame = WindowCapture.Scale(frame, CaptureSupport.ParseScale(context));

                warnings.Add("编辑模式下 GameView 不渲染，画面来自相机渲染（不经过 GameView 的 Aspect / 分辨率设置）");
                warnings.Add("不含 Screen Space-Overlay 的 UI：Overlay Canvas 不经过相机，渲染图里一定看不到它");
                if (!ReferenceEquals(camera, gameViewCamera))
                {
                    warnings.Add($"用的不是 GameView 的相机，而是场景里的 {camera.name}（GameView 相机取不到）");
                }

                var path = CaptureSupport.ResolveOutputPath(context, "game");
                return CaptureSupport.Save(
                    path, frame, "game", "camera-render", new RectInt(0, 0, frame.Width, frame.Height), warnings);
            }
        }

        /// <summary>
        /// 渲染分辨率：优先用 GameView 相机的 <c>pixelRect</c>（那才是它自己认定的画幅），
        /// 拿不到就按窗口尺寸 × DPI 兜底。
        /// </summary>
        private static void ResolveRenderSize(EditorWindow gameView, Camera camera, float scale, out int width, out int height)
        {
            width = 0;
            height = 0;

            if (camera != null)
            {
                var pixelRect = camera.pixelRect;
                if (pixelRect.width >= 16f && pixelRect.height >= 16f)
                {
                    width = Mathf.RoundToInt(pixelRect.width);
                    height = Mathf.RoundToInt(pixelRect.height);
                    return;
                }
            }

            if (gameView != null)
            {
                width = Mathf.RoundToInt(gameView.position.width * scale);
                height = Mathf.RoundToInt(gameView.position.height * scale);
            }

            if (width < 16 || height < 16)
            {
                // 抬底：太小的画幅看不出问题，还会让相机 aspect 变成离谱值
                width = 1280;
                height = 720;
            }
        }
    }
}
