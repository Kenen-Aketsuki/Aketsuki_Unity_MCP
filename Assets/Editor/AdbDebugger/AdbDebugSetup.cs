using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CivilRev.AdbDebugger
{
    /// <summary>
    /// 把本工具的 MCP 桥装进工程，并注册进 WorkBuddy 的 <c>mcp.json</c>。
    ///
    /// <para>
    /// <b>为什么 C# 只负责"调脚本"：</b>注册要在 <c>~/.workbuddy/mcp.json</c> 里改一个嵌套结构，
    /// 而本工具刻意不引用指令系统的 <c>JsonValue</c>。为一个安装动作实现 JSON 解析器不划算，
    /// 而分发包里本来就有 Python（MCP 桥就是 Python），交给它做既安全又不用重复造轮子。
    /// </para>
    ///
    /// <para>
    /// 脚本位置用 <c>AssetDatabase.FindAssets</c> 找而不是写死路径：unitypackage 的安装位置
    /// 可能被用户改过，写死会在改名后失效。
    /// </para>
    /// </summary>
    internal static class AdbDebugSetup
    {
        private const string MenuRoot = "Tools/ADB 调试/";

        [MenuItem(MenuRoot + "安装 MCP 桥（写入 mcp.json）", priority = 20)]
        private static void InstallMenuItem()
        {
            var script = FindInstallScript();
            if (script == null)
            {
                EditorUtility.DisplayDialog("找不到安装脚本",
                    "分发包里应该有 AdbBridge/install_bridge.py，但工程里没找到。\n" +
                    "请确认 ADB 调试工具是完整安装的。", "好");
                return;
            }

            var python = ResolvePython();
            if (string.IsNullOrEmpty(python))
            {
                EditorUtility.DisplayDialog("找不到 Python",
                    "没找到可用的 Python 解释器。\n\n" +
                    "本机装了 Python 但不在 PATH 里时，可以在系统环境变量里加一下，或直接手工执行：\n" +
                    "python \"" + script + "\" --project-root \"" + ProjectRoot + "\"", "好");
                return;
            }

            var arguments = "\"" + script + "\" --project-root \"" + ProjectRoot + "\" --python \"" + python + "\"";
            string output;
            string error;
            var code = Run(python, arguments, out output, out error);

            if (code == 0)
            {
                AdbLog.Info("MCP 桥已安装：\n" + output);
                EditorUtility.DisplayDialog("安装完成",
                    output + "\n\n请到连接器管理页的自定义连接器里点「信任」。", "好");
            }
            else
            {
                AdbLog.Error("MCP 桥安装失败（exit=" + code + "）：" + error + output);
                EditorUtility.DisplayDialog("安装失败",
                    (string.IsNullOrEmpty(error) ? output : error) + "\n\n可手工执行：\n" + arguments, "好");
            }
        }

        [MenuItem(MenuRoot + "移除 MCP 桥（从 mcp.json 删除）", priority = 21)]
        private static void UninstallMenuItem()
        {
            if (!EditorUtility.DisplayDialog("移除 MCP 桥",
                    "会从 ~/.workbuddy/mcp.json 删掉本工具的条目（其它条目不动）。继续？", "移除", "取消"))
            {
                return;
            }

            var script = FindInstallScript();
            var python = ResolvePython();
            if (script == null || string.IsNullOrEmpty(python))
            {
                EditorUtility.DisplayDialog("无法移除", "找不到脚本或 Python 解释器。", "好");
                return;
            }

            string output;
            string error;
            var code = Run(python, "\"" + script + "\" --uninstall", out output, out error);
            if (code == 0)
            {
                AdbLog.Info("已移除 MCP 桥：" + output);
            }
            else
            {
                AdbLog.Error("移除失败：" + error + output);
            }
        }

        /// <summary>定位分发包里的安装脚本</summary>
        private static string FindInstallScript()
        {
            var guids = AssetDatabase.FindAssets("install_bridge t:DefaultAsset");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("install_bridge.py", StringComparison.OrdinalIgnoreCase))
                {
                    // AssetDatabase 给的是相对路径，Python 与 Unity 都要绝对路径
                    return Path.GetFullPath(Path.Combine(ProjectRoot, path));
                }
            }

            return null;
        }

        private static string ProjectRoot
        {
            get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..")); }
        }

        /// <summary>
        /// 找 Python：WorkBuddy 托管版本优先（本机确定有），其次 PATH 上的 python / python3 / py。
        /// </summary>
        private static string ResolvePython()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var versionsDir = Path.Combine(home, ".workbuddy", "binaries", "python", "versions");
            if (Directory.Exists(versionsDir))
            {
                foreach (var dir in Directory.GetDirectories(versionsDir))
                {
                    var exe = Path.Combine(dir,
                        Application.platform == RuntimePlatform.WindowsEditor ? "python.exe" : "bin/python3");
                    if (File.Exists(exe))
                    {
                        return exe;
                    }
                }
            }

            foreach (var candidate in new[] { "python", "python3", "py" })
            {
                string output;
                string error;
                if (Run(candidate, "--version", out output, out error) == 0)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static int Run(string fileName, string arguments, out string output, out string error)
        {
            output = string.Empty;
            error = string.Empty;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = ProjectRoot
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        error = "进程未能启动";
                        return -1;
                    }

                    output = process.StandardOutput.ReadToEnd();
                    error = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(30000))
                    {
                        error = "脚本 30 秒未结束";
                        try
                        {
                            process.Kill();
                        }
                        catch (Exception)
                        {
                        }

                        return -1;
                    }

                    return process.ExitCode;
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                return -1;
            }
        }
    }
}
