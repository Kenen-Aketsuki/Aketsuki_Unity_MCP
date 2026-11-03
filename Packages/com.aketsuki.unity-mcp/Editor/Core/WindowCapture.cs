using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 窗口截屏工具：Win32 <c>PrintWindow</c> + 按窗口矩形裁剪 + DPI 换算，产出 BGRA 位图。
    /// <para>
    /// 为什么不用 <c>System.Drawing</c>：Unity 2021/2022 的 .NET Standard profile 里
    /// <c>System.Drawing.dll</c> 只是 14 KB 的类型转发壳（<c>Data/NetStandard/compat/.../shims/netfx</c>），
    /// 一旦真的 new Bitmap 就是运行期崩，而不是编译期报错。这里改用
    /// <c>CreateDIBSection</c> 拿一块 GDI 写入的像素内存，再 <c>Marshal.Copy</c> 读出来，
    /// 全程只用 <c>user32</c> / <c>gdi32</c> 与 Unity 自己的 <see cref="Texture2D"/>。
    /// </para>
    /// <para>
    /// 为什么不用 <c>ScreenCapture.CaptureScreenshotAsTexture()</c>：那是"帧末"行为，
    /// 与"一条指令一帧"的执行模型冲突；而且它只认 GameView 的 backbuffer，截不到 SceneView。
    /// </para>
    /// </summary>
    internal static class WindowCapture
    {
        /// <summary>PW_RENDERFULLCONTENT：Win8.1+ 起支持，能抓到 DWM 合成后的完整内容（含被遮挡的部分）</summary>
        private const uint PwRenderFullContent = 2;

        private const uint DibRgbColors = 0;

        /// <summary>RedrawWindow 标志：立即重画（含子窗口）</summary>
        private const uint RdwInvalidate = 0x0001;
        private const uint RdwAllChildren = 0x0080;
        private const uint RdwUpdateNow = 0x0100;

        #region Win32

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public int Size;
            public int Width;
            public int Height;
            public short Planes;
            public short BitCount;
            public int Compression;
            public int SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public int ClrUsed;
            public int ClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapInfoHeader Header;
            public int Colors;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hwnd, IntPtr updateRect, IntPtr updateRgn, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(
            IntPtr hdc, ref BitmapInfo pbmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        #endregion

        /// <summary>非 Windows 编辑器（macOS / Linux）不支持：本仓库部署目标全在 Windows</summary>
        public static bool IsSupported => Application.platform == RuntimePlatform.WindowsEditor;

        /// <summary>Unity 主窗口句柄（进程级，缓存一次）</summary>
        private static IntPtr _mainWindow;
        private static bool _mainWindowResolved;

        /// <summary>
        /// 取 Unity 编辑器主窗口句柄。
        /// <para>拿不到时返回 <see cref="IntPtr.Zero"/>，由调用方报明确错误，不要偷偷退化成"截全屏"。</para>
        /// </summary>
        public static IntPtr MainWindowHandle
        {
            get
            {
                if (_mainWindowResolved)
                {
                    return _mainWindow;
                }

                _mainWindowResolved = true;
                try
                {
                    _mainWindow = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                }
                catch (Exception)
                {
                    _mainWindow = IntPtr.Zero;
                }

                return _mainWindow;
            }
        }

        /// <summary>
        /// 取窗口矩形（物理像素、屏幕坐标、左上原点）。
        /// <para>
        /// 与 <see cref="UnityEditor.EditorWindow.position"/>（逻辑像素）单位不同，
        /// 125% / 150% 缩放下必须乘 <c>EditorGUIUtility.pixelsPerPoint</c> 才能对齐。
        /// </para>
        /// </summary>
        public static bool TryGetWindowRect(IntPtr hwnd, out RectInt rect, out string error)
        {
            rect = new RectInt(0, 0, 0, 0);
            error = null;

            if (!IsSupported)
            {
                error = $"窗口截屏只支持 Windows 编辑器（当前平台：{Application.platform}）";
                return false;
            }

            if (hwnd == IntPtr.Zero)
            {
                error = "拿不到 Unity 主窗口句柄（Process.MainWindowHandle 为空）";
                return false;
            }

            if (IsIconic(hwnd))
            {
                error = "Unity 窗口处于最小化状态：最小化时窗口没有可截取的内容，请先还原窗口";
                return false;
            }

            NativeRect native;
            if (!GetWindowRect(hwnd, out native))
            {
                error = $"GetWindowRect 失败（Win32 错误 {Marshal.GetLastWin32Error()}）";
                return false;
            }

            var width = native.Right - native.Left;
            var height = native.Bottom - native.Top;
            if (width <= 0 || height <= 0)
            {
                error = $"窗口尺寸无效（{width}×{height}），窗口可能被最小化或正处于创建过程中";
                return false;
            }

            rect = new RectInt(native.Left, native.Top, width, height);
            return true;
        }

        #region 抓图

        /// <summary>
        /// 请窗口把脏区立即画掉。
        /// <para>
        /// 只是"尽力而为"：Unity 编辑器客户区由 GPU 渲染，<c>RedrawWindow</c> 通常不会让引擎重画一帧，
        /// 真正生效的仍是 <c>PrintWindow</c> 本身取到的合成内容。它对"窗口刚被缩放、边框还在半截"
        /// 这一类边缘情况有帮助，成本接近 0，且不会崩（纯 Win32，不碰任何 internal 绘制 API）。
        /// </para>
        /// </summary>
        public static void RequestRepaint(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            try
            {
                RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwUpdateNow | RdwAllChildren);
            }
            catch (Exception)
            {
                // 重绘失败不该让截图本身失败
            }
        }

        /// <summary>一帧 BGRA 像素（每像素 4 字节，行优先、自上而下）</summary>
        internal sealed class Frame
        {
            public byte[] Bgra;
            public int Width;
            public int Height;
        }

        /// <summary>
        /// 用 <c>PrintWindow</c> 抓整个窗口。
        /// <para>
        /// 返回的帧与 <see cref="TryGetWindowRect"/> 的矩形严格对应（左上原点），
        /// 因此裁剪只需做窗口内相对坐标的减法。
        /// </para>
        /// </summary>
        public static bool TryGrabWindow(IntPtr hwnd, out Frame frame, out string error)
        {
            frame = null;
            error = null;

            RectInt rect;
            if (!TryGetWindowRect(hwnd, out rect, out error))
            {
                return false;
            }

            var width = rect.width;
            var height = rect.height;

            var screenDc = IntPtr.Zero;
            var memoryDc = IntPtr.Zero;
            var bitmap = IntPtr.Zero;
            var previous = IntPtr.Zero;
            var bits = IntPtr.Zero;

            try
            {
                screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero)
                {
                    error = "GetDC(屏幕) 失败";
                    return false;
                }

                memoryDc = CreateCompatibleDC(screenDc);
                if (memoryDc == IntPtr.Zero)
                {
                    error = "CreateCompatibleDC 失败";
                    return false;
                }

                var info = new BitmapInfo();
                info.Header.Size = Marshal.SizeOf(typeof(BitmapInfoHeader));
                info.Header.Width = width;
                // 正高度 = 标准的"自下而上" DIB。
                // 别改成负高度换 top-down：PrintWindow(BitBlt 路径) 会忽略负高度符号照样按 bottom-up 写，
                // 结果整幅图上下颠倒（2026-11-03 实测：文字全倒过来）。正高度 + 手工翻行是唯一稳的写法。
                info.Header.Height = height;
                info.Header.Planes = 1;
                info.Header.BitCount = 32;
                info.Header.Compression = 0; // BI_RGB

                bitmap = CreateDIBSection(memoryDc, ref info, DibRgbColors, out bits, IntPtr.Zero, 0);
                if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
                {
                    error = "CreateDIBSection 失败";
                    return false;
                }

                previous = SelectObject(memoryDc, bitmap);

                var ok = PrintWindow(hwnd, memoryDc, PwRenderFullContent);
                if (!ok)
                {
                    // 老系统 / 某些显卡驱动上 PW_RENDERFULLCONTENT 不被认，退回 flags=0 再试一次
                    ok = PrintWindow(hwnd, memoryDc, 0);
                }

                if (!ok)
                {
                    error = $"PrintWindow 失败（{width}×{height}，Win32 错误 {Marshal.GetLastWin32Error()}）";
                    return false;
                }

                var bytes = new byte[width * height * 4];
                Marshal.Copy(bits, bytes, 0, bytes.Length);

                // DIB 是自下而上的：翻成自上而下，后续裁剪才符合"y 向下增长"的直觉
                var stride = width * 4;
                var flipped = new byte[bytes.Length];
                for (var row = 0; row < height; row++)
                {
                    Buffer.BlockCopy(bytes, (height - 1 - row) * stride, flipped, row * stride, stride);
                }

                // GDI 不维护 alpha：32bpp DIB 的 A 通道通常是 0，直接当 RGBA 用会得到全透明 PNG。
                // 这里一律置 255，颜色判读只看 RGB。
                for (var i = 3; i < flipped.Length; i += 4)
                {
                    flipped[i] = 255;
                }

                frame = new Frame { Bgra = flipped, Width = width, Height = height };
                return true;
            }
            catch (Exception exception)
            {
                error = $"窗口截屏异常：{exception.Message}";
                return false;
            }
            finally
            {
                if (memoryDc != IntPtr.Zero && previous != IntPtr.Zero)
                {
                    SelectObject(memoryDc, previous);
                }

                if (bitmap != IntPtr.Zero)
                {
                    DeleteObject(bitmap);
                }

                if (memoryDc != IntPtr.Zero)
                {
                    DeleteDC(memoryDc);
                }

                if (screenDc != IntPtr.Zero)
                {
                    ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
        }

        /// <summary>
        /// 从整窗口帧里裁出 <paramref name="rect"/>（窗口内相对坐标，左上原点）。
        /// <para>矩形已被 <see cref="ClampToWindow"/> 处理过，这里不再做边界判断。</para>
        /// </summary>
        public static Frame Crop(Frame source, RectInt rect)
        {
            if (source == null)
            {
                return null;
            }

            var bytes = new byte[rect.width * rect.height * 4];
            var rowBytes = rect.width * 4;
            for (var row = 0; row < rect.height; row++)
            {
                var sourceOffset = ((rect.y + row) * source.Width + rect.x) * 4;
                Buffer.BlockCopy(source.Bgra, sourceOffset, bytes, row * rowBytes, rowBytes);
            }

            return new Frame { Bgra = bytes, Width = rect.width, Height = rect.height };
        }

        /// <summary>
        /// 把矩形夹进窗口范围。
        /// <para>
        /// 窗口被缩到最小尺寸、或 <c>pixelRect</c> 与 <c>position</c> 口径不一致时，
        /// 算出来的矩形可能越界或为空——这里不抛，交给调用方判断 <c>width/height &lt;= 0</c>。
        /// </para>
        /// </summary>
        public static RectInt ClampToWindow(RectInt rect, int windowWidth, int windowHeight)
        {
            if (windowWidth <= 0 || windowHeight <= 0)
            {
                return new RectInt(0, 0, 0, 0);
            }

            var x = Mathf.Clamp(rect.x, 0, windowWidth - 1);
            var y = Mathf.Clamp(rect.y, 0, windowHeight - 1);
            var width = Mathf.Clamp(rect.width, 0, windowWidth - x);
            var height = Mathf.Clamp(rect.height, 0, windowHeight - y);
            return new RectInt(x, y, width, height);
        }

        /// <summary>
        /// 后置缩放（双线性，纯 CPU）。
        /// <para>
        /// 不用 <c>Graphics.Blit</c>：线性色彩空间下 Blit 会走 sRGB 转换，截出来的颜色和屏幕上不一样。
        /// CPU 双线性只做像素插值，所见即所得。
        /// </para>
        /// </summary>
        public static Frame Scale(Frame source, float scale)
        {
            if (source == null || Mathf.Approximately(scale, 1f) || scale <= 0f)
            {
                return source;
            }

            var width = Mathf.Max(1, Mathf.RoundToInt(source.Width * scale));
            var height = Mathf.Max(1, Mathf.RoundToInt(source.Height * scale));
            if (width == source.Width && height == source.Height)
            {
                return source;
            }

            var bytes = new byte[width * height * 4];
            var xRatio = (float)source.Width / width;
            var yRatio = (float)source.Height / height;

            for (var y = 0; y < height; y++)
            {
                var sy = Mathf.Clamp((y + 0.5f) * yRatio - 0.5f, 0f, source.Height - 1);
                var y0 = Mathf.FloorToInt(sy);
                var y1 = Mathf.Min(y0 + 1, source.Height - 1);
                var fy = sy - y0;

                for (var x = 0; x < width; x++)
                {
                    var sx = Mathf.Clamp((x + 0.5f) * xRatio - 0.5f, 0f, source.Width - 1);
                    var x0 = Mathf.FloorToInt(sx);
                    var x1 = Mathf.Min(x0 + 1, source.Width - 1);
                    var fx = sx - x0;

                    var i00 = (y0 * source.Width + x0) * 4;
                    var i10 = (y0 * source.Width + x1) * 4;
                    var i01 = (y1 * source.Width + x0) * 4;
                    var i11 = (y1 * source.Width + x1) * 4;
                    var target = (y * width + x) * 4;

                    for (var channel = 0; channel < 4; channel++)
                    {
                        var top = source.Bgra[i00 + channel] * (1f - fx) + source.Bgra[i10 + channel] * fx;
                        var bottom = source.Bgra[i01 + channel] * (1f - fx) + source.Bgra[i11 + channel] * fx;
                        bytes[target + channel] = (byte)Mathf.Clamp(Mathf.RoundToInt(top * (1f - fy) + bottom * fy), 0, 255);
                    }
                }
            }

            return new Frame { Bgra = bytes, Width = width, Height = height };
        }

        /// <summary>
        /// 把 BGRA 帧包成 sRGB <see cref="Texture2D"/>。
        /// <para>
        /// <b>这里必须翻行</b>：<see cref="Frame"/> 约定的行序是"自上而下"（第 0 行 = 图像顶部，
        /// 与裁剪矩形 y 增长方向一致），而 Unity 的 <c>LoadRawTextureData</c> / <c>SetPixels</c>
        /// 按 GPU 约定"自下而上"（第 0 行 = 图像底部）。
        /// 不翻的话 PNG 会整幅上下颠倒 —— 2026-11-03 实测踩过，且和"PrintWindow 拿到的是哪种行序"
        /// 是两个独立的问题，别混在一起调。
        /// </para>
        /// </summary>
        public static Texture2D ToTexture(Frame frame)
        {
            var stride = frame.Width * 4;
            var bottomUp = new byte[frame.Bgra.Length];
            for (var row = 0; row < frame.Height; row++)
            {
                Buffer.BlockCopy(
                    frame.Bgra, (frame.Height - 1 - row) * stride,
                    bottomUp, row * stride, stride);
            }

            var texture = new Texture2D(frame.Width, frame.Height, TextureFormat.BGRA32, false, false);
            texture.LoadRawTextureData(bottomUp);
            texture.Apply(false, false);
            return texture;
        }

        #endregion

        #region 相机渲染（GameView 编辑模式降级）

        /// <summary>
        /// 把一个相机渲染到 RenderTexture 再读成像素，返回 BGRA 帧。
        /// <para>
        /// <b>只能在 update 上下文里调用</b>：在 <c>Camera.onPostRender</c> 里调 <see cref="Camera.Render"/>
        /// 会递归进同一个回调，编辑器直接崩（2026-10-28 实测崩过两次）。
        /// </para>
        /// <para>
        /// 三个相机状态（<c>targetTexture</c> / <c>aspect</c> / <c>rect</c>）必须原样还原，
        /// 否则用户之后的 SceneView 会一直变形。
        /// </para>
        /// </summary>
        public static bool TryRenderCamera(Camera camera, int width, int height, out Frame frame, out string error)
        {
            frame = null;
            error = null;

            if (camera == null)
            {
                error = "没有可渲染的相机";
                return false;
            }

            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);

            var previousTarget = camera.targetTexture;
            var previousAspect = camera.aspect;
            var previousRect = camera.rect;
            var previousActive = RenderTexture.active;

            var renderTexture = RenderTexture.GetTemporary(
                width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

            try
            {
                camera.targetTexture = renderTexture;
                camera.aspect = (float)width / height;
                camera.rect = new Rect(0f, 0f, 1f, 1f);
                camera.Render();

                RenderTexture.active = renderTexture;
                var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
                try
                {
                    texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    texture.Apply(false, false);

                    // RGBA32 → BGRA，并同步把 GPU 的"自下而上"翻成 <see cref="Frame"/> 约定的"自上而下"。
                    // 统一到窗口截屏的内部表示，后续裁剪 / 缩放 / 保存走同一条路。
                    var rgba = texture.GetRawTextureData();
                    var bgra = new byte[width * height * 4];
                    var rowBytes = width * 4;
                    for (var row = 0; row < height; row++)
                    {
                        var sourceRow = (height - 1 - row) * rowBytes;
                        var targetRow = row * rowBytes;
                        for (var x = 0; x < rowBytes; x += 4)
                        {
                            bgra[targetRow + x] = rgba[sourceRow + x + 2];
                            bgra[targetRow + x + 1] = rgba[sourceRow + x + 1];
                            bgra[targetRow + x + 2] = rgba[sourceRow + x];
                            bgra[targetRow + x + 3] = 255;
                        }
                    }

                    frame = new Frame { Bgra = bgra, Width = width, Height = height };
                    return true;
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }
            catch (Exception exception)
            {
                error = $"相机渲染失败：{exception.Message}";
                return false;
            }
            finally
            {
                RenderTexture.active = previousActive;
                camera.targetTexture = previousTarget;
                camera.aspect = previousAspect;
                camera.rect = previousRect;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        #endregion
    }
}
