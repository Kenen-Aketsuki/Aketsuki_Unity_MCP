using UnityEditor;
using UnityEngine;

namespace CivilRev.AdbDebugger
{
    /// <summary>
    /// 编辑器启动时按设置把调试服务拉起来。
    ///
    /// <para>
    /// 用 <c>delayCall</c> 而不是在静态构造里直接起：静态构造发生在域重载的很早期，
    /// 那时 <c>EditorPrefs</c> 与 <c>Application.dataPath</c> 都还可能不可用（尤其是首次导入）。
    /// 延迟一帧的代价可以忽略，换来的是"启动即连上"的稳定行为。
    /// </para>
    ///
    /// <para>
    /// 与 <see cref="AdbLogcatService"/> 的恢复是两件事：这个管"HTTP 门开不开"，
    /// 那个管"要不要抓日志"。服务没起来时抓取开关勾了也没用（客户端进不来），
    /// 所以两者各自独立恢复、互不牵连，界面上也分开显示。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    internal static class AdbDebugAutoStart
    {
        static AdbDebugAutoStart()
        {
            if (Application.isBatchMode)
            {
                return;
            }

            EditorApplication.delayCall += Restore;
            EditorApplication.quitting += OnQuitting;
        }

        private static void Restore()
        {
            // 先把设备列表扫一遍：/v1/status 与 /v1/devices 里会报"几台设备可用"，
            // 不预热的话第一次问的人会看到 0 台（真扫在后台线程，不占启动时间）
            AdbDevices.RequestScan();

            if (!AdbDebuggerSettings.ServerEnabled)
            {
                return;
            }

            if (AdbDebugHttpServer.Instance.IsRunning)
            {
                return;
            }

            string error;
            if (!AdbDebugHttpServer.Instance.Start(AdbDebuggerSettings.Port, out error) &&
                !string.IsNullOrEmpty(error))
            {
                AdbLog.Error(error);
            }
        }

        private static void OnQuitting()
        {
            // 退出前把信息文件清掉：留着会指向一个已经不存在的端口，客户端要白等一轮超时
            AdbDebugHttpServer.Instance.Stop();
        }
    }
}
