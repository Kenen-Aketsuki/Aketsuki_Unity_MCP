using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CivilRev.AdbDebugger
{
    /// <summary>
    /// ADB 调试台的独立窗口。
    ///
    /// <para>
    /// <b>为什么不挂在「指令系统」的控制台里：</b>这是两个工具。指令系统管编辑器内的资产/场景/Play 操作，
    /// 本工具管 Android 设备上的日志，安装与开关全都不一样。合并的后果是任何一边出问题
    /// 都要在另一个工具的面板里找原因。
    /// </para>
    /// </summary>
    public class AdbDebugWindow : EditorWindow
    {
        private const string MenuPath = "Tools/ADB 调试/ADB 调试台";

        /// <summary>列表为空时的补扫间隔（秒）：插上线之后不用手动点刷新，但也不至于每秒起一个 adb 进程</summary>
        private const double AutoRescanSeconds = 5;

        private Vector2 _scroll;
        private bool _manualSerial;
        private int _devicesVersion = -1;

        [MenuItem(MenuPath)]
        public static void Open()
        {
            var window = GetWindow<AdbDebugWindow>("ADB 调试台");
            window.minSize = new Vector2(430, 480);
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
            _devicesVersion = AdbDevices.Version;
            AdbDevices.RequestScan();
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnFocus()
        {
            // 插拔设备后切回窗口就该看到新列表，不必手动点刷新
            AdbDevices.RequestScan();
        }

        /// <summary>扫描在后台线程完成，不能直接 <c>Repaint</c>；靠这里回到主线程再看版本号变化</summary>
        private void OnEditorUpdate()
        {
            if (AdbDevices.IsScanning)
            {
                return;
            }

            if (AdbDevices.Version != _devicesVersion)
            {
                _devicesVersion = AdbDevices.Version;
                Repaint();
                return;
            }

            // 一台都没有时补扫：插线 / 启动模拟器之后不用手动点「刷新」。
            // 有设备时不自作主张——adbd 偶尔抖一下不该让列表跟着闪。
            if (AdbDevices.Snapshot().Count == 0
                && (DateTime.Now - AdbDevices.ScannedAt).TotalSeconds >= AutoRescanSeconds)
            {
                AdbDevices.RequestScan();
            }
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

            DrawCaptureSection();
            EditorGUILayout.Space(6);
            DrawHelp();

            EditorGUILayout.EndScrollView();
        }

        #region 设备

        /// <summary>
        /// 设备检索 + 选择：下拉里的选项来自 <c>adb devices -l</c> 的实时结果，选中即写设置。
        ///
        /// <para>
        /// <b>为什么不是裸输入框：</b>序列号（<c>2LQ0224A15000813</c>）没人记得住，而填错的表现是
        /// adb 一直「等设备」、一条日志都不出——恰是最耗时的排查。下拉的选项直接来自 adb，
        /// 选不出错；不可用的设备（未授权 / 离线）也把原因写在脸上。
        /// </para>
        /// </summary>
        private void DrawDevicePicker()
        {
            var devices = AdbDevices.Snapshot();
            var serial = (AdbDebuggerSettings.Serial ?? string.Empty).Trim();

            var labels = new List<string> { "自动（adb 自己挑唯一设备）" };
            var values = new List<string> { string.Empty };
            for (var i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                labels.Add(device.Usable ? device.DisplayName : device.DisplayName + " · " + device.State);
                values.Add(device.Serial);
            }

            // 选过但已经拔掉的设备要保留：否则下拉会默默跳回「自动」，
            // 让人以为设置没保存，实际是那台设备不见了
            if (serial.Length > 0 && IndexOf(values, serial) < 0)
            {
                labels.Insert(1, "⚠ " + serial + "（当前没连上）");
                values.Insert(1, serial);
            }

            var current = IndexOf(values, serial);
            if (current < 0)
            {
                current = 0;
            }

            EditorGUILayout.BeginHorizontal();
            var picked = EditorGUILayout.Popup("设备", current, labels.ToArray());
            if (GUILayout.Button(AdbDevices.IsScanning ? "扫描中" : "刷新", GUILayout.Width(56)))
            {
                AdbDevices.RequestScan();
            }

            EditorGUILayout.EndHorizontal();

            if (picked != current)
            {
                ApplySerial(values[picked]);
                serial = values[picked];
            }

            EditorGUILayout.LabelField(DescribeSelection(devices, serial), EditorStyles.miniLabel);

            // 不可用的设备逐条说清怎么修；能用的不占地方
            for (var i = 0; i < devices.Count; i++)
            {
                if (devices[i].Usable)
                {
                    continue;
                }

                EditorGUILayout.HelpBox(devices[i].DisplayName + "：" + devices[i].StateHint, MessageType.Warning);
            }

            if (!string.IsNullOrEmpty(AdbDevices.LastError))
            {
                EditorGUILayout.HelpBox(AdbDevices.LastError, MessageType.Warning);
            }

            _manualSerial = EditorGUILayout.Foldout(_manualSerial, "手动输入序列号（adb connect 出来的网络设备）", true);
            if (_manualSerial)
            {
                EditorGUI.indentLevel++;
                var typed = EditorGUILayout.TextField("序列号", serial);
                if (typed != serial)
                {
                    ApplySerial(typed);
                }

                EditorGUI.indentLevel--;
            }

            if (AdbDevices.ScannedAt != DateTime.MinValue)
            {
                EditorGUILayout.LabelField(
                    "上次扫描 " + AdbDevices.ScannedAt.ToString("HH:mm:ss") + " · " + AdbDevices.Summary(),
                    EditorStyles.miniLabel);
            }
        }

        private void ApplySerial(string serial)
        {
            var value = (serial ?? string.Empty).Trim();
            if (string.Equals(value, (AdbDebuggerSettings.Serial ?? string.Empty).Trim(), StringComparison.Ordinal))
            {
                return;
            }

            AdbDebuggerSettings.Serial = value;
            var name = value.Length == 0 ? "自动" : value;

            if (AdbLogcatService.IsRunning)
            {
                // 换了设备却不重连的话，控制台里刷的还是旧设备——那比不显示更容易误导
                AdbLogcatService.Stop();
                string error;
                if (!AdbLogcatService.Start(out error) && !string.IsNullOrEmpty(error))
                {
                    AdbLog.Error(error);
                }

                AdbLog.Info("已切换到「" + name + "」并重连");
                return;
            }

            AdbLog.Info("已选择设备「" + name + "」，点「重新连接」开始抓取");
        }

        private static string DescribeSelection(IList<AdbDevice> devices, string serial)
        {
            if (serial.Length > 0)
            {
                var match = Find(devices, serial);
                if (match == null)
                {
                    return "已选设备当前不在列表里：开始抓取会失败，插好线后点「刷新」";
                }

                return match.Usable
                    ? "当前：" + match.DisplayName
                    : "已选设备不可用：" + match.StateHint;
            }

            var usable = 0;
            AdbDevice only = null;
            for (var i = 0; i < devices.Count; i++)
            {
                if (!devices[i].Usable)
                {
                    continue;
                }

                usable++;
                only = devices[i];
            }

            if (usable == 1)
            {
                return "当前：自动 → " + only.DisplayName;
            }

            if (usable > 1)
            {
                return "当前：自动，但有 " + usable + " 台可用设备——adb 会因不唯一而失败，请显式选一台";
            }

            return "当前：自动（还没发现可用设备）";
        }

        private static int IndexOf(List<string> values, string serial)
        {
            for (var i = 0; i < values.Count; i++)
            {
                if (string.Equals(values[i], serial, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private static AdbDevice Find(IList<AdbDevice> devices, string serial)
        {
            for (var i = 0; i < devices.Count; i++)
            {
                if (string.Equals(devices[i].Serial, serial, StringComparison.OrdinalIgnoreCase))
                {
                    return devices[i];
                }
            }

            return null;
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
                // 换了 adb 就等于换了一套设备来源，设备列表要跟着重扫
                AdbDevices.RequestScan();
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

            DrawDevicePicker();

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

            EditorGUILayout.LabelField(
                string.IsNullOrEmpty(tag)
                    ? "空 = 不过滤，整机所有 App 的日志都会进来（量很大）"
                    : "只显示 tag 含「" + tag + "」的日志（大小写不敏感）；清空 = 全部",
                EditorStyles.miniLabel);

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
                    // 起不来最常见的原因就是设备变了（拔了/没授权），顺手把列表刷新掉
                    AdbDevices.RequestScan();
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
                "本工具自己不提供任何对外接口。要让 AI 抓日志，装「Unity MCP 指令系统」，" +
                "用它的 adb.devices / adb.logcat.* 指令——那套指令会调到这里，" +
                "两边也可以只装其中一个。",
                MessageType.None);
        }
    }
}
