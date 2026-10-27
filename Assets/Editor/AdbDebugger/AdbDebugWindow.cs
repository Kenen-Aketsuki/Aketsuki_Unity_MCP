using System;
using UnityEditor;
using UnityEngine;

namespace CivilRev.AdbDebugger
{
    /// <summary>
    /// ADB 调试台的独立窗口。
    ///
    /// <para>
    /// <b>为什么不挂在「指令系统」的控制台里：</b>这是两个工具。指令系统管编辑器内的资产/场景/Play 操作，
    /// 本工具管 Android 设备上的日志，生命周期、端口、开关全都不一样。合并的后果是任何一边出问题
    /// 都要在另一个工具的面板里找原因。
    /// </para>
    /// </summary>
    public class AdbDebugWindow : EditorWindow
    {
        private const string MenuPath = "Tools/ADB 调试/ADB 调试台";

        private Vector2 _scroll;

        [MenuItem(MenuPath)]
        public static void Open()
        {
            var window = GetWindow<AdbDebugWindow>("ADB 调试台");
            window.minSize = new Vector2(430, 480);
            window.Show();
        }

        /// <summary>不开窗口也能快速启停（和窗口里的开关是同一个设置）</summary>
        [MenuItem("Tools/ADB 调试/启停 logcat")]
        private static void ToggleLogcat()
        {
            if (AdbLogcatService.IsRunning)
            {
                AdbLogcatService.Stop();
                return;
            }

            string error;
            if (!AdbLogcatService.Start(out error) && !string.IsNullOrEmpty(error))
            {
                AdbLog.Error(error);
            }
        }

        [MenuItem("Tools/ADB 调试/启停 logcat", true)]
        private static bool ToggleLogcatValidate()
        {
            Menu.SetChecked("Tools/ADB 调试/启停 logcat", AdbLogcatService.IsRunning);
            return true;
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawServerSection();
            EditorGUILayout.Space(6);
            DrawCaptureSection();
            EditorGUILayout.Space(6);
            DrawHelp();

            EditorGUILayout.EndScrollView();
        }

        #region 服务

        private void DrawServerSection()
        {
            EditorGUILayout.LabelField("调试服务（MCP 客户端进这个端口）", EditorStyles.boldLabel);

            var server = AdbDebugHttpServer.Instance;
            EditorGUILayout.LabelField("状态",
                server.IsRunning
                    ? "运行中 · " + server.BaseUrl
                    : "未运行（编辑器启动时会按下面的开关自动拉起）");

            if (server.IsRunning)
            {
                EditorGUILayout.LabelField("令牌", server.Token, EditorStyles.miniLabel);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("复制令牌"))
                {
                    GUIUtility.systemCopyBuffer = server.Token ?? string.Empty;
                    AdbLog.Info("令牌已复制到剪贴板");
                }

                if (GUILayout.Button("重新生成令牌"))
                {
                    if (EditorUtility.DisplayDialog("重新生成令牌",
                            "旧令牌会立刻失效，已配置的客户端需要重新读取信息文件。继续？", "生成", "取消"))
                    {
                        server.RegenerateToken();
                        AdbLog.Info("令牌已重新生成");
                    }
                }

                EditorGUILayout.EndHorizontal();
            }

            var serverEnabled = EditorGUILayout.Toggle("启用调试服务", AdbDebuggerSettings.ServerEnabled);
            if (serverEnabled != AdbDebuggerSettings.ServerEnabled)
            {
                AdbDebuggerSettings.ServerEnabled = serverEnabled;
                if (serverEnabled)
                {
                    string error;
                    if (!server.Start(AdbDebuggerSettings.Port, out error) && !string.IsNullOrEmpty(error))
                    {
                        AdbLog.Error(error);
                    }
                }
                else
                {
                    server.Stop();
                    AdbLog.Info("调试服务已停止（信息文件已删除）");
                }
            }

            var port = EditorGUILayout.IntField("端口", AdbDebuggerSettings.Port);
            if (port != AdbDebuggerSettings.Port)
            {
                AdbDebuggerSettings.Port = port;
                AdbLog.Info("端口已修改，点「重启服务」后生效");
            }

            if (GUILayout.Button("重启服务"))
            {
                server.Stop();
                string error;
                if (!server.Start(AdbDebuggerSettings.Port, out error) && !string.IsNullOrEmpty(error))
                {
                    AdbLog.Error(error);
                }
            }

            EditorGUILayout.LabelField("信息文件", AdbDebugHttpServer.InfoDirectory, EditorStyles.miniLabel);
        }

        #endregion

        #region 抓取

        private void DrawCaptureSection()
        {
            EditorGUILayout.LabelField("logcat 抓取", EditorStyles.boldLabel);

            var enabled = EditorGUILayout.Toggle("启用 logcat", AdbDebuggerSettings.LogcatEnabled);
            if (enabled != AdbDebuggerSettings.LogcatEnabled)
            {
                AdbDebuggerSettings.LogcatEnabled = true; // Start() 失败时会被自己关回去
                if (enabled)
                {
                    string error;
                    if (!AdbLogcatService.Start(out error) && !string.IsNullOrEmpty(error))
                    {
                        AdbLog.Error(error);
                    }
                }
                else
                {
                    AdbLogcatService.Stop();
                }
            }

            EditorGUILayout.LabelField("状态", AdbLogcatService.SummaryText);
            if (!string.IsNullOrEmpty(AdbLogcatService.AdbPath))
            {
                EditorGUILayout.LabelField("adb", AdbLogcatService.AdbPath, EditorStyles.miniLabel);
            }

            if (!string.IsNullOrEmpty(AdbLogcatService.LastError))
            {
                EditorGUILayout.HelpBox(AdbLogcatService.LastError, MessageType.Error);
            }

            EditorGUI.indentLevel++;

            EditorGUILayout.BeginHorizontal();
            var adbPath = EditorGUILayout.TextField("adb 路径", AdbDebuggerSettings.AdbPath);
            if (GUILayout.Button("浏览", GUILayout.Width(56)))
            {
                var picked = EditorUtility.OpenFilePanel("选择 adb 可执行文件", string.Empty,
                    Application.platform == RuntimePlatform.WindowsEditor ? "exe" : string.Empty);
                if (!string.IsNullOrEmpty(picked))
                {
                    adbPath = picked;
                }
            }

            EditorGUILayout.EndHorizontal();
            if (adbPath != AdbDebuggerSettings.AdbPath)
            {
                AdbDebuggerSettings.AdbPath = adbPath;
                if (AdbLogcatService.IsRunning)
                {
                    AdbLog.Info("adb 路径已修改，点「重新连接」后生效");
                }
            }

            if (string.IsNullOrEmpty(AdbDebuggerSettings.AdbPath))
            {
                EditorGUILayout.LabelField("留空＝自动定位：环境变量 → Unity 的 AndroidSdkRoot → 编辑器自带 SDK → PATH",
                    EditorStyles.miniLabel);
            }

            var serial = EditorGUILayout.TextField("设备序列号", AdbDebuggerSettings.Serial);
            if (serial != AdbDebuggerSettings.Serial)
            {
                AdbDebuggerSettings.Serial = serial;
                AdbLog.Info("序列号已修改，点「重新连接」后生效");
            }

            EditorGUILayout.BeginHorizontal();
            var tag = EditorGUILayout.TextField("Tag 过滤", AdbDebuggerSettings.Tag);

            var levelOptions = new[] { "V", "D", "I", "W", "E", "F", "A" };
            var currentLevel = Array.IndexOf(levelOptions, AdbDebuggerSettings.Level);
            if (currentLevel < 0)
            {
                currentLevel = 2;
            }

            var pickedLevel = EditorGUILayout.Popup(currentLevel, levelOptions, GUILayout.Width(60));
            EditorGUILayout.EndHorizontal();

            if (tag != AdbDebuggerSettings.Tag || levelOptions[pickedLevel] != AdbDebuggerSettings.Level)
            {
                AdbDebuggerSettings.Tag = tag;
                AdbDebuggerSettings.Level = levelOptions[pickedLevel];
                AdbLogcatService.RefreshFilters();
            }

            var backfill = EditorGUILayout.IntField("回填行数", AdbDebuggerSettings.Backfill);
            if (backfill != AdbDebuggerSettings.Backfill)
            {
                AdbDebuggerSettings.Backfill = backfill;
            }

            var stackRaw = EditorGUILayout.IntField("堆栈合并行数", AdbDebuggerSettings.StackLines);
            var stackLines = Math.Max(0, Math.Min(AdbDebuggerSettings.MaxStackLines, stackRaw));
            if (stackLines != AdbDebuggerSettings.StackLines)
            {
                AdbDebuggerSettings.StackLines = stackLines;
                AdbLogcatService.RefreshFilters();
            }

            EditorGUILayout.LabelField(
                stackLines <= 0
                    ? "0 = 不打印调用栈，只留首行内容"
                    : "一条日志最多并入这么多行堆栈，超出截断并注明",
                EditorStyles.miniLabel);

            var suppressStack = EditorGUILayout.Toggle("隐藏 C# 堆栈", AdbDebuggerSettings.SuppressStack);
            if (suppressStack != AdbDebuggerSettings.SuppressStack)
            {
                AdbDebuggerSettings.SuppressStack = suppressStack;
                AdbLogcatService.RefreshFilters();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("重新连接"))
            {
                AdbLogcatService.Stop();
                string error;
                if (!AdbLogcatService.Start(out error) && !string.IsNullOrEmpty(error))
                {
                    AdbLog.Error(error);
                }
            }

            if (GUILayout.Button("清空计数"))
            {
                AdbLogcatService.ResetCounters();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel--;
        }

        #endregion

        private static void DrawHelp()
        {
            EditorGUILayout.HelpBox(
                "日志以 [ADB] 前缀打进 Unity 控制台，级别已映射（W→警告，E/F→错误）。\n" +
                "一次异常的堆栈会合并成一条日志，不再刷几十条。\n\n" +
                "AI 侧走本工具自己的 MCP 服务（端口 " + AdbDebuggerSettings.DefaultPort + " 起），" +
                "与「Unity MCP 指令系统」互不依赖，可以只装其中一个。",
                MessageType.None);
        }
    }
}
