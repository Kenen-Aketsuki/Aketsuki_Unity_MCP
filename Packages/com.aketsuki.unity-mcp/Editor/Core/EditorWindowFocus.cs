using System;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 把 Unity 编辑器窗口拉到前台（IMPR-030）。
    ///
    /// <para>
    /// <b>为什么需要：</b>Unity 编辑器窗口失去焦点后，GUI 重绘会被节流——<c>RepaintAllViews()</c>
    /// 只是把视图标脏（<c>GUIView.Repaint</c> 置位），真正的 paint 要等窗口重新活跃才发生。
    /// 也就是说 IMPR-029 的"批次结束主动 repaint"在编辑器处于后台时**等于没做**：
    /// 用户切回来看到的仍是旧画面，必须点一下节点（<c>OnSelectionChanged</c> 强制重绘）才刷新。
    /// </para>
    ///
    /// <para>
    /// <b>与 SKILL.md「不要抢焦点」那条的区别：</b>那条说的是"用一个不跑主循环的进程去唤醒编辑器"——
    /// 那种情况下抢前台确实无效（泵死了，重绘再多也没内容）。这里的前提正相反：**泵活着、指令刚跑完、
    /// 画面已经有内容，只是编辑器在后台没重绘**。两者不是同一件事，别把那条套到这里。
    /// </para>
    ///
    /// <para>
    /// <b>触发面要收窄：</b>只有"PrefabStage 正开着本次写过的那个 prefab"时才抢前台。没开 Stage 的批次
    /// 只做 repaint——抢焦点是有代价的（会打断用户正在别的程序里做的事），不能每条指令都来一次。
    /// </para>
    /// </summary>
    internal static class EditorWindowFocus
    {
        private const int SwRestore = 9;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int cmd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        /// <summary>
        /// 先把键盘焦点给 <paramref name="preferred"/>（通常是 PrefabStage 的 SceneView），
        /// 再尝试把 Unity 主窗口提到系统前台。
        /// </summary>
        /// <param name="preferred">优先聚焦的编辑器窗口，可为 null（只抢主窗口前台）</param>
        /// <param name="note">没抢到前台时的原因，供调用方写进 warning；成功时为 null</param>
        /// <returns>是否成功把编辑器提到前台</returns>
        public static bool TryBringToFront(EditorWindow preferred, out string note)
        {
            note = null;

            // 1) Unity 内部焦点：把键盘焦点交给目标面板并请求重绘。
            //    只要 Unity 本身在前台，这一层就足够把画面刷出来。
            try
            {
                if (preferred != null)
                {
                    preferred.Focus();
                    preferred.Repaint();
                }
            }
            catch (Exception error)
            {
                note = "Unity 内部聚焦失败：" + error.Message;
            }

            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                note = note ?? "非 Windows 编辑器：只做了 Unity 内部聚焦，未抢系统前台";
                return preferred != null;
            }

            var hwnd = WindowCapture.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
            {
                note = note ?? "拿不到 Unity 主窗口句柄，无法提升到前台";
                return false;
            }

            try
            {
                if (IsIconic(hwnd))
                {
                    ShowWindow(hwnd, SwRestore);
                }

                if (SetForegroundWindow(hwnd))
                {
                    return true;
                }

                // Windows 的前台锁：只有"当前前台进程"或"刚收到用户输入"的进程能调 SetForegroundWindow。
                // 编辑器被指令唤醒时不满足条件，于是临时把自己的输入队列挂到前台线程上，再试一次。
                var foreground = GetForegroundWindow();
                if (foreground == IntPtr.Zero || foreground == hwnd)
                {
                    note = note ?? "当前没有可用的前台窗口，SetForegroundWindow 未生效";
                    return false;
                }

                uint processId;
                var foregroundThread = GetWindowThreadProcessId(foreground, out processId);
                var selfThread = GetCurrentThreadId();
                if (foregroundThread == 0 || foregroundThread == selfThread)
                {
                    note = note ?? "前台线程 id 不可用，SetForegroundWindow 未生效";
                    return false;
                }

                var attached = false;
                try
                {
                    attached = AttachThreadInput(selfThread, foregroundThread, true);
                    var raised = SetForegroundWindow(hwnd);
                    if (raised)
                    {
                        BringWindowToTop(hwnd);
                    }
                    else
                    {
                        note = note ?? $"SetForegroundWindow 被系统拒绝（Win32 错误 {Marshal.GetLastWin32Error()}），需手动切回编辑器";
                    }

                    return raised;
                }
                finally
                {
                    if (attached)
                    {
                        AttachThreadInput(selfThread, foregroundThread, false);
                    }
                }
            }
            catch (Exception error)
            {
                note = note ?? "提升前台异常：" + error.Message;
                return false;
            }
        }
    }
}
