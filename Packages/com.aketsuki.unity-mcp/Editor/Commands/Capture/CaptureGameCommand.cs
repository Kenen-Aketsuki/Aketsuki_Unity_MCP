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

                // ---- 编辑模式：GameView 不渲染，退到相机渲染。
                // 这里不能只挑一个相机就渲染：clearFlags = Depth only 的叠加相机单独渲染到空 RT 必然全黑，
                // 而且不报错。所以按"最可能出图"的顺序逐个试，见图像算数，见黑图换下一个。
                var candidates = CollectCameras(gameViewCamera);
                if (candidates.Count == 0)
                {
                    return CommandResult.Fail(
                        "编辑模式下 GameView 不渲染，且场景里没有可用相机（Camera.main / 已启用相机都为空），无法出图");
                }

                var scaleFactor = CaptureSupport.ParseScale(context);
                var dpi = CaptureSupport.DpiScale;
                var failures = new List<string>();

                foreach (var candidate in candidates)
                {
                    int width, height;
                    ResolveRenderSize(gameView, candidate, dpi, out width, out height);

                    WindowCapture.Frame frame;
                    string error;
                    if (!WindowCapture.TryRenderCamera(candidate, width, height, out frame, out error))
                    {
                        failures.Add($"{candidate.name}：渲染失败（{error}）");
                        continue;
                    }

                    if (WindowCapture.IsNearlyBlack(frame))
                    {
                        failures.Add(string.Format(
                            "{0}（clearFlags={1}, depth={2}）：画面全黑",
                            candidate.name, candidate.clearFlags, candidate.depth));
                        continue;
                    }

                    frame = WindowCapture.Scale(frame, scaleFactor);

                    warnings.Add("编辑模式下 GameView 不渲染，画面来自相机渲染（不经过 GameView 的 Aspect / 分辨率设置）");
                    warnings.Add("不含 Screen Space-Overlay 的 UI：Overlay Canvas 不经过相机，渲染图里一定看不到它");
                    if (!ReferenceEquals(candidate, gameViewCamera))
                    {
                        warnings.Add($"用的不是 GameView 的相机，而是场景里的 {candidate.name}");
                    }

                    var path = CaptureSupport.ResolveOutputPath(context, "game");
                    var result = CaptureSupport.Save(
                        path, frame, "game", "camera-render",
                        new RectInt(0, 0, frame.Width, frame.Height), warnings);

                    if (result.Success && result.Data is JsonValue data)
                    {
                        data.Set("camera", JsonValue.Of(candidate.name ?? string.Empty));
                    }

                    return result;
                }

                return CommandResult.Fail(
                    "编辑模式下没有相机能渲染出有效画面：" + string.Join("；", failures.ToArray()) +
                    "。Depth only 的叠加相机单独渲染一定黑屏，需要一个负责清屏的底层相机；" +
                    "要看完整游戏画面请进 Play 模式再截（窗口截屏不受此限制），只想看场景内容请用 capture.scene");
            }
        }

        /// <summary>
        /// 编辑模式下的候选相机，按"最可能渲染出有效画面"排序。
        /// <para>
        /// 排序规则：GameView 相机 → <c>Camera.main</c> → 其余（**会清屏的排前面**，同组内 depth 小的优先）。
        /// depth 大的往往是 UI / 叠加相机，单独渲染只会得到黑图（实测踩过），所以不能简单地取 depth 最大。
        /// 渲染到 RenderTexture 的相机（小地图之类）会被排除：它们画的不是屏幕画面。
        /// </para>
        /// </summary>
        private static List<Camera> CollectCameras(Camera gameViewCamera)
        {
            var ordered = new List<Camera>();
            AddCandidate(ordered, gameViewCamera);
            AddCandidate(ordered, Camera.main);

            var rest = new List<Camera>();
            foreach (var camera in Camera.allCameras)
            {
                if (camera == null || ordered.Contains(camera))
                {
                    continue;
                }

                if (!camera.isActiveAndEnabled || camera.targetTexture != null)
                {
                    continue;
                }

                rest.Add(camera);
            }

            rest.Sort((left, right) =>
            {
                var leftOverlay = left.clearFlags == CameraClearFlags.Depth ? 1 : 0;
                var rightOverlay = right.clearFlags == CameraClearFlags.Depth ? 1 : 0;
                return leftOverlay != rightOverlay ? leftOverlay - rightOverlay : left.depth.CompareTo(right.depth);
            });

            ordered.AddRange(rest);
            return ordered;
        }

        private static void AddCandidate(List<Camera> cameras, Camera camera)
        {
            if (camera == null || !camera.isActiveAndEnabled || camera.targetTexture != null)
            {
                return;
            }

            if (!cameras.Contains(camera))
            {
                cameras.Add(camera);
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
