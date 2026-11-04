using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>窗口截屏的裁剪范围</summary>
    internal enum CaptureCrop
    {
        /// <summary>只裁场景画布（默认）：剥掉工具栏与左右 Overlay</summary>
        Frame = 0,

        /// <summary>整个 EditorWindow（含工具栏、Overlay）</summary>
        Window = 1,

        /// <summary>整个 Unity 窗口（含标题栏、菜单栏、其它面板）</summary>
        Full = 2
    }

    /// <summary>
    /// capture.* 三条指令的共用部分：取视图窗口、算裁剪矩形、存 PNG、拼返回结构。
    /// <para>
    /// 三条指令拆开而不是一条带 <c>view</c> 枚举：schema 里堆满各视图专属参数（<c>prefab</c> /
    /// <c>openPrefab</c> / <c>crop</c>）之后，AI 传错参数的概率比记三条指令高得多。
    /// </para>
    /// </summary>
    internal static class CaptureSupport
    {
        /// <summary>截图默认落在指令目录下的 screenshots/，覆盖写，不堆积历史图</summary>
        public const string ScreenshotFolder = "screenshots";

        /// <summary>
        /// 解析 <c>crop</c> 参数。缺省 <see cref="CaptureCrop.Frame"/>。
        /// <para>写法错误时抛 <see cref="CommandArgumentException"/>，不做"猜你要哪个"。</para>
        /// </summary>
        public static CaptureCrop ParseCrop(CommandContext context)
        {
            var raw = context.Args.GetString("crop", null);
            if (string.IsNullOrEmpty(raw))
            {
                return CaptureCrop.Frame;
            }

            switch (raw.Trim().ToLowerInvariant())
            {
                case "frame":
                    return CaptureCrop.Frame;
                case "window":
                    return CaptureCrop.Window;
                case "full":
                    return CaptureCrop.Full;
                default:
                    throw new CommandArgumentException(context.CommandId, "crop",
                        $"crop 只能是 frame / window / full，收到 '{raw}'");
            }
        }

        /// <summary>默认输出路径：<c>&lt;指令目录&gt;/screenshots/&lt;view&gt;-latest.png</c></summary>
        public static string DefaultOutputPath(string view)
        {
            return Path.Combine(
                Path.Combine(CommandPathUtil.DefaultCommandDirectory, ScreenshotFolder),
                view + "-latest.png");
        }

        /// <summary>解析并确保输出目录存在</summary>
        public static string ResolveOutputPath(CommandContext context, string view)
        {
            var raw = context.Args.GetString("path", null);
            var path = string.IsNullOrEmpty(raw) ? DefaultOutputPath(view) : raw;
            var fullPath = CommandPathUtil.Resolve(path, CommandPathUtil.GetDirectoryOf(context.Source?.FilePath));

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return fullPath;
        }

        /// <summary>解析 <c>scale</c>（默认 1，允许 0.1–4）</summary>
        public static float ParseScale(CommandContext context)
        {
            var scale = context.Args.GetFloat("scale", 1f);
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f)
            {
                throw new CommandArgumentException(context.CommandId, "scale", $"scale 必须是正数，收到 {scale}");
            }

            return Mathf.Clamp(scale, 0.1f, 4f);
        }

        /// <summary>窗口截屏的 DPI 换算系数：100% → 1，125% → 1.25</summary>
        public static float DpiScale => UnityEditor.EditorGUIUtility.pixelsPerPoint;

        /// <summary>
        /// 把 GUI 点矩形（屏幕坐标、左上原点）换算成"窗口内物理像素"矩形。
        /// <para>
        /// <b>不要写成 <c>(gui.x - windowRect.x) * scale</c></b>：<c>windowRect</c> 是物理像素，
        /// <c>gui</c> 是逻辑点，两者在 125% / 150% 下不同，先减再乘等于多减了 <c>windowRect * (scale-1)</c>，
        /// 裁剪会整体偏移。正确顺序是先把 GUI 点乘成物理像素，再减窗口原点。
        /// </para>
        /// </summary>
        public static RectInt ToWindowPixels(Rect guiRect, RectInt windowRect, float scale)
        {
            return new RectInt(
                Mathf.RoundToInt(guiRect.x * scale) - windowRect.x,
                Mathf.RoundToInt(guiRect.y * scale) - windowRect.y,
                Mathf.RoundToInt(guiRect.width * scale),
                Mathf.RoundToInt(guiRect.height * scale));
        }

        /// <summary>
        /// 算"相机画面"在窗口内的物理像素矩形（crop=frame 用）。
        /// <para>
        /// <c>camera.pixelRect</c> 的参照系在不同 Unity 版本里不一致，实测见过两种：
        /// </para>
        /// <list type="bullet">
        /// <item>2022.3：<b>相对视口</b>，铺满宽度、高度少掉顶部工具栏（实测 1070×509 vs 视口 1070×560）；</item>
        /// <item>2021.3：<b>屏幕坐标</b>（FEAT-019 的实测记录，501×219 vs 679×362）。</item>
        /// </list>
        /// <para>
        /// 所以这里按"最可能 → 最不可能"试几个候选，取第一个能完整落进视口矩形的：
        /// 相对视口（物理像素）→ 相对视口（GUI 点）→ 屏幕坐标（GUI 点）。
        /// <b>一个都不成立就退回整窗裁剪并给 warning</b>，而不是硬算一张偏移的图——
        /// 错位的截图比没有截图更容易误导判断。
        /// </para>
        /// </summary>
        public static bool TryGetFrameRect(
            EditorWindow window, Camera camera, RectInt windowPixels, float scale,
            out RectInt rect, out string warning)
        {
            rect = new RectInt(0, 0, 0, 0);
            warning = null;

            if (window == null || camera == null)
            {
                warning = "取不到视图相机，已退回整窗裁剪（含工具栏与 Overlay）";
                return false;
            }

            var pixelRect = camera.pixelRect;
            if (pixelRect.width <= 0f || pixelRect.height <= 0f)
            {
                warning = "视图相机 pixelRect 为空（窗口可能刚被创建或尺寸为 0），已退回整窗裁剪";
                return false;
            }

            var position = window.position;

            // 三个候选都算成"相对视口左上角"的偏移，校验通过后再统一加视口在窗口内的原点。
            // 别在校验前就把 windowPixels 加进去：容器本身就是视口矩形，加了必然越界。
            var candidates = new List<RectInt>(3)
            {
                // ① 相对视口、物理像素（Unity 2022.x 口径：pixelRect.x/y 是视口内偏移）
                new RectInt(
                    Mathf.RoundToInt(pixelRect.x),
                    windowPixels.height - Mathf.RoundToInt(pixelRect.y + pixelRect.height),
                    Mathf.RoundToInt(pixelRect.width),
                    Mathf.RoundToInt(pixelRect.height)),

                // ② 相对视口、GUI 点（同上但没做 DPI 缩放的情形）
                new RectInt(
                    Mathf.RoundToInt(pixelRect.x * scale),
                    windowPixels.height - Mathf.RoundToInt((pixelRect.y + pixelRect.height) * scale),
                    Mathf.RoundToInt(pixelRect.width * scale),
                    Mathf.RoundToInt(pixelRect.height * scale)),

                // ③ 屏幕坐标、GUI 点（Unity 2021.x 口径）
                new RectInt(
                    Mathf.RoundToInt((pixelRect.x - position.x) * scale),
                    Mathf.RoundToInt(
                        (position.y + position.height - (pixelRect.y + pixelRect.height)) * scale),
                    Mathf.RoundToInt(pixelRect.width * scale),
                    Mathf.RoundToInt(pixelRect.height * scale)),
            };

            var container = new RectInt(0, 0, windowPixels.width, windowPixels.height);
            for (var i = 0; i < candidates.Count; i++)
            {
                if (FitsInside(candidates[i], container))
                {
                    rect = new RectInt(
                        windowPixels.x + candidates[i].x,
                        windowPixels.y + candidates[i].y,
                        candidates[i].width,
                        candidates[i].height);
                    return true;
                }
            }

            warning = string.Format(
                "相机画面矩形（{0}×{1}）对不上视口矩形（{2}×{3}），三种坐标口径都不成立，已退回整窗裁剪（含工具栏与 Overlay）",
                Mathf.RoundToInt(pixelRect.width), Mathf.RoundToInt(pixelRect.height),
                windowPixels.width, windowPixels.height);
            return false;
        }

        /// <summary>矩形是否完整落在视口内（容 2 像素的取整/边框误差）</summary>
        private static bool FitsInside(RectInt candidate, RectInt container)
        {
            return candidate.width >= 16 && candidate.height >= 16 &&
                   candidate.x >= -2 && candidate.y >= -2 &&
                   candidate.x + candidate.width <= container.width + 2 &&
                   candidate.y + candidate.height <= container.height + 2;
        }

        #region SceneView / GameView 取用

        /// <summary>
        /// 取目标 SceneView。
        /// <para>
        /// <c>view</c> 缺省取 <c>lastActiveSceneView</c>；显式给索引时按 <c>SceneView.sceneViews</c> 顺序取。
        /// 一个都没有时**失败**，不替用户新开一个——开窗会改用户的工作区布局。
        /// </para>
        /// </summary>
        public static bool TryGetSceneView(CommandContext context, out SceneView view, out string error)
        {
            view = null;
            error = null;

            var all = new List<SceneView>();
            var list = SceneView.sceneViews;
            if (list != null)
            {
                foreach (var item in list)
                {
                    var sceneView = item as SceneView;
                    if (sceneView != null)
                    {
                        all.Add(sceneView);
                    }
                }
            }

            if (all.Count == 0)
            {
                error = "当前没有打开的 SceneView 窗口，无法截取场景画面（本指令不会自行开窗）";
                return false;
            }

            if (!context.Args.Has("view"))
            {
                view = SceneView.lastActiveSceneView;
                if (view == null)
                {
                    view = all[0];
                }
                return true;
            }

            var index = context.Args.GetInt("view", 0);
            if (index < 0 || index >= all.Count)
            {
                error = $"view 索引 {index} 越界：当前共有 {all.Count} 个 SceneView（索引 0–{all.Count - 1}）";
                return false;
            }

            view = all[index];
            return true;
        }

        /// <summary>
        /// 取 GameView 窗口实例。它是 <c>UnityEditor.GameView</c>（internal），只能按类型名反射。
        /// <para>拿不到时返回 null，由调用方降级到相机渲染并写 warning。</para>
        /// </summary>
        public static EditorWindow FindGameViewWindow()
        {
            try
            {
                var type = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (type == null)
                {
                    return null;
                }

                var candidates = Resources.FindObjectsOfTypeAll(type);
                EditorWindow fallback = null;
                foreach (var candidate in candidates)
                {
                    var window = candidate as EditorWindow;
                    if (window == null)
                    {
                        continue;
                    }

                    if (fallback == null)
                    {
                        fallback = window;
                    }

                    if (window.hasFocus)
                    {
                        return window;
                    }
                }

                return fallback;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 反射取 GameView 当前使用的相机。
        /// <para>取不到返回 null；这个属性在不同版本里可能改名，所以每次都走 try/catch。</para>
        /// </summary>
        public static Camera GetGameViewCamera(EditorWindow gameView)
        {
            if (gameView == null)
            {
                return null;
            }

            try
            {
                var property = gameView.GetType().GetProperty(
                    "camera", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return property == null ? null : property.GetValue(gameView) as Camera;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 编辑模式的候选相机选择已迁到 <see cref="CaptureGameCommand.CollectCameras"/>：
        /// 那边的排序是"会清屏的优先 + 逐个试、见黑图换下一个"，不能再退回"取 depth 最大"的旧逻辑
        /// （2026-11-04 实测：depth 最大的恰恰是 Depth only 的叠加相机，单独渲染必黑）。
        /// </summary>

        #endregion

        #region 选中态

        /// <summary>
        /// 截图前的临时选中，<see cref="Dispose"/> 时还原。
        /// <para>
        /// 存在的意义：<c>select</c> 参数要的是"看看这个对象的 selected gizmo"，而不是把用户的选中换掉。
        /// 用 <c>using</c> 包住整个截图过程，异常路径也能还原。
        /// </para>
        /// </summary>
        internal sealed class SelectionScope : IDisposable
        {
            private readonly UnityEngine.Object[] _previous;
            private readonly bool _changed;

            private SelectionScope(UnityEngine.Object[] previous, bool changed)
            {
                _previous = previous;
                _changed = changed;
            }

            public static SelectionScope Apply(CommandContext context, out string error)
            {
                error = null;
                var spec = context.Args.Get("select");
                if (spec == null || spec.IsNull)
                {
                    return new SelectionScope(null, false);
                }

                var target = SceneQuery.ResolveTarget(spec);
                if (target == null)
                {
                    error = $"select 找不到节点：{spec.ToJson(false)}";
                    return null;
                }

                var previous = Selection.objects;
                Selection.activeGameObject = target.gameObject;
                Selection.objects = new UnityEngine.Object[] { target.gameObject };
                return new SelectionScope(previous, true);
            }

            public void Dispose()
            {
                if (!_changed)
                {
                    return;
                }

                try
                {
                    Selection.objects = _previous;
                    if (_previous != null && _previous.Length > 0)
                    {
                        Selection.activeObject = _previous[0];
                    }
                }
                catch (Exception)
                {
                    // 还原失败不该把已经拍好的图变成失败结果
                }
            }
        }

        #endregion

        #region 保存与返回

        /// <summary>
        /// 保存 PNG 并拼返回结构。
        /// <para>
        /// <c>mimeType</c> 不是给人看的：MCP 桥按「data 里同时有 path + mimeType」这个通用约定
        /// 自动把图片内联成 image content，AI 不用再单独读一次文件。
        /// </para>
        /// </summary>
        public static CommandResult Save(
            string fullPath,
            WindowCapture.Frame frame,
            string view,
            string source,
            RectInt cropped,
            List<string> warnings)
        {
            var texture = WindowCapture.ToTexture(frame);
            byte[] png;
            try
            {
                png = texture.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }

            if (png == null || png.Length == 0)
            {
                return CommandResult.Fail("PNG 编码失败（EncodeToPNG 返回空）");
            }

            WriteAllBytes(fullPath, png);

            var data = JsonValue.NewObject();
            data.Set("path", JsonValue.Of(fullPath));
            data.Set("mimeType", JsonValue.Of("image/png"));
            data.Set("view", JsonValue.Of(view));
            data.Set("width", JsonValue.Of(frame.Width));
            data.Set("height", JsonValue.Of(frame.Height));
            data.Set("bytes", JsonValue.Of(png.Length));
            data.Set("source", JsonValue.Of(source));
            data.Set("dpiScale", JsonValue.Of((float)Math.Round(DpiScale, 3)));

            var cropNode = JsonValue.NewObject();
            cropNode.Set("x", JsonValue.Of(cropped.x));
            cropNode.Set("y", JsonValue.Of(cropped.y));
            cropNode.Set("w", JsonValue.Of(cropped.width));
            cropNode.Set("h", JsonValue.Of(cropped.height));
            data.Set("cropped", cropNode);

            var warningNode = JsonValue.NewArray();
            if (warnings != null)
            {
                foreach (var warning in warnings)
                {
                    warningNode.Add(JsonValue.Of(warning));
                }
            }

            data.Set("warnings", warningNode);

            var message = string.Format("{0} 截图 {1}×{2}（{3}）→ {4}",
                view, frame.Width, frame.Height, source, fullPath);
            if (warnings != null && warnings.Count > 0)
            {
                message += "；注意：" + string.Join("；", warnings.ToArray());
            }

            return CommandResult.Ok(message, data);
        }

        /// <summary>原子写文件（先写临时文件再替换，目标工程开着编辑器时不会被读到半截）</summary>
        private static void WriteAllBytes(string path, byte[] bytes)
        {
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(temporary, path);
            }
            catch (Exception)
            {
                // Move 失败（被占用等）时退化为直接覆盖，至少别把临时文件留在原地
                try
                {
                    File.WriteAllBytes(path, bytes);
                }
                finally
                {
                    TryDelete(temporary);
                }
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // 忽略
            }
        }

        #endregion

        #region 整窗截屏主流程

        /// <summary>
        /// 截一个 <see cref="EditorWindow"/>：窗口截屏 → 按 crop 裁剪 → 可选缩放 → 存 PNG。
        /// <para>
        /// SceneView 与 Play 中的 GameView 都走这里；只有"编辑模式下的 GameView"例外
        /// （它根本不渲染，见 <see cref="CaptureGameViewCommand"/>）。
        /// </para>
        /// </summary>
        public static CommandResult CaptureWindow(
            CommandContext context,
            EditorWindow window,
            string viewName,
            CaptureCrop crop,
            List<string> warnings,
            Camera frameCamera)
        {
            var hwnd = WindowCapture.MainWindowHandle;
            RectInt windowRect;
            string error;
            if (!WindowCapture.TryGetWindowRect(hwnd, out windowRect, out error))
            {
                return CommandResult.Fail(error);
            }

            var scale = DpiScale;
            var viewPixels = ToWindowPixels(window.position, windowRect, scale);

            var cropped = crop == CaptureCrop.Full
                ? new RectInt(0, 0, windowRect.width, windowRect.height)
                : WindowCapture.ClampToWindow(viewPixels, windowRect.width, windowRect.height);

            if (crop == CaptureCrop.Frame)
            {
                var camera = frameCamera != null ? frameCamera : (window as SceneView)?.camera;
                RectInt frameRect;
                string warning;
                if (TryGetFrameRect(window, camera, viewPixels, scale, out frameRect, out warning))
                {
                    cropped = WindowCapture.ClampToWindow(frameRect, windowRect.width, windowRect.height);
                    // 相机 pixelRect 只描述"渲染视口"，Unity 的 SceneView 工具栏 / 左侧 Overlay /
                    // GameView 顶部工具条都是画在视口之上的 GUI 叠加层，窗口截屏没法把它们剥掉。
                    // 实测（Unity 2022.3）上沿会带进约 19px 的工具栏——必须说清楚，别让人把按钮当场景内容。
                    warnings.Add("crop=frame 的矩形取自相机 pixelRect，其上沿/左沿可能压着 SceneView 工具栏或 Overlay 工具条" +
                                 "（它们是 GUI 叠加层，不属于相机视口，窗口截屏无法剥离）");
                }
                else if (!string.IsNullOrEmpty(warning))
                {
                    warnings.Add(warning);
                }
            }

            if (cropped.width <= 0 || cropped.height <= 0)
            {
                return CommandResult.Fail(
                    $"裁剪矩形为空（窗口 {windowRect.width}×{windowRect.height}，目标 {viewPixels.x},{viewPixels.y} {viewPixels.width}×{viewPixels.height}），窗口可能刚被缩放");
            }

            // 让窗口把脏区画掉再抓：PrintWindow 拿的是合成后的内容，窗口刚被缩放/切选中时不刷新会拿到旧帧
            WindowCapture.RequestRepaint(hwnd);

            WindowCapture.Frame full;
            if (!WindowCapture.TryGrabWindow(hwnd, out full, out error))
            {
                return CommandResult.Fail(error);
            }

            var frame = crop == CaptureCrop.Full ? full : WindowCapture.Crop(full, cropped);
            frame = WindowCapture.Scale(frame, ParseScale(context));

            var path = ResolveOutputPath(context, viewName);
            return Save(path, frame, viewName, "printwindow", cropped, warnings);
        }

        #endregion
    }
}
