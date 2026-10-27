using System;
using System.IO;
using UnityEditor;

namespace CivilRev.AdbDebugger
{
    /// <summary>
    /// 找 <c>adb</c> 可执行文件的位置。
    ///
    /// <para>
    /// <b>为什么要单独一个类：</b>adb 不在 PATH 里是常态。装了 Android SDK 的机器通常有三种摆法
    /// （环境变量指向的 SDK、Unity 里填的 AndroidSdkRoot、Hub 版编辑器自带的 AndroidPlayer/SDK），
    /// 再加一个"用户自己指路"。这些都写在同一个地方，改起来才不用翻服务代码。
    /// </para>
    ///
    /// <para>
    /// <b>顺序按"越具体越优先"：</b>手填路径 → 环境变量 → Unity 的 AndroidSdkRoot →
    /// 编辑器自带 SDK → PATH。手填排第一是因为它只可能由人显式设置，出现即代表意图。
    /// </para>
    ///
    /// <para>
    /// 找不到时**不退回裸名 <c>"adb"</c>**：那样错误会变成 <c>Win32Exception: 系统找不到指定的文件</c>，
    /// 看不出是"没装 adb"还是"装了但没在 PATH 里"。这里宁可返回 null 并带出试过哪些位置。
    /// </para>
    /// </summary>
    internal static class AdbLocator
    {
        /// <summary>平台对应的可执行文件名（Windows 必须有 .exe，否则 File.Exists 一律 false）</summary>
        internal static string ExecutableName
        {
            get { return Path.DirectorySeparatorChar == '\\' ? "adb.exe" : "adb"; }
        }

        /// <summary>
        /// 定位 adb。
        /// </summary>
        /// <param name="source">命中来源，或失败时"试过哪些位置"的说明；可直接嵌进错误文案</param>
        /// <returns>adb 的绝对路径；找不到返回 <c>null</c></returns>
        internal static string Resolve(out string source)
        {
            source = string.Empty;

            // 1) 控制台里手填的路径。允许填目录（省得记文件名），自动补 adb.exe
            var configured = (AdbDebuggerSettings.AdbPath ?? string.Empty).Trim();
            if (configured.Length > 0)
            {
                var candidate = Directory.Exists(configured)
                    ? Path.Combine(configured, ExecutableName)
                    : configured;

                if (File.Exists(candidate))
                {
                    source = "控制台手填路径";
                    return candidate;
                }

                // 手填错了也继续往下找：能跑起来比"严格尊重配置"更有用，
                // 但要把这件事说出去，否则用户会以为自己填的生效了
                source = "手填路径无效（" + configured + "）";
            }

            // 2) 环境变量（官方 SDK / 大多数 CI 环境）
            foreach (var name in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
            {
                var hit = PlatformToolsOf(Environment.GetEnvironmentVariable(name));
                if (hit != null)
                {
                    source = "环境变量 " + name;
                    return hit;
                }
            }

            // 3) Unity 里配置的 Android SDK（Preferences → External Tools）
            var fromUnityPrefs = PlatformToolsOf(EditorPrefs.GetString("AndroidSdkRoot", string.Empty));
            if (fromUnityPrefs != null)
            {
                source = "Unity 的 AndroidSdkRoot";
                return fromUnityPrefs;
            }

            // 4) 编辑器自带的 AndroidPlayer SDK —— Hub 装的 Unity 都带一份 platform-tools，
            //    这是"没单独装 SDK 但就是想抓个日志"的场景里最可能命中的位置
            var contents = EditorApplication.applicationContentsPath;
            if (!string.IsNullOrEmpty(contents))
            {
                var fromEditor = PlatformToolsOf(
                    Path.Combine(contents, "PlaybackEngines", "AndroidPlayer", "SDK"));
                if (fromEditor != null)
                {
                    source = "编辑器自带 AndroidPlayer SDK";
                    return fromEditor;
                }
            }

            // 5) PATH
            var onPath = FindOnPath();
            if (onPath != null)
            {
                source = "PATH";
                return onPath;
            }

            source = source.Length > 0
                ? source + "；PATH、ANDROID_HOME、AndroidSdkRoot、编辑器自带 SDK 里也都没有 adb"
                : "PATH、ANDROID_HOME、AndroidSdkRoot、编辑器自带 SDK 里都没找到 adb";
            return null;
        }

        /// <summary>把 SDK 根拼成 <c>&lt;root&gt;/platform-tools/adb</c>，存在才返回</summary>
        private static string PlatformToolsOf(string sdkRoot)
        {
            if (string.IsNullOrWhiteSpace(sdkRoot))
            {
                return null;
            }

            try
            {
                var candidate = Path.Combine(sdkRoot.Trim(), "platform-tools", ExecutableName);
                return File.Exists(candidate) ? candidate : null;
            }
            catch (Exception)
            {
                // 路径里带非法字符（用户手填的常见情况）：当作没配，别让定位过程抛异常
                return null;
            }
        }

        /// <summary>扫 PATH。Windows 上 PATH 里既有目录也可能有空项，逐个跳过</summary>
        private static string FindOnPath()
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            foreach (var raw in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                try
                {
                    var candidate = Path.Combine(raw.Trim().Trim('"'), ExecutableName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception)
                {
                    // 单个目录非法不影响整体扫描
                }
            }

            return null;
        }
    }
}
