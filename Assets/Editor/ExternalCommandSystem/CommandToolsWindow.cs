using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CivilRev.CommandSystem.Editor
{
    /// <summary>
    /// 指令系统控制台窗口：手动执行指令文件、查看已注册指令、调整自动监听设置
    /// </summary>
    public class CommandToolsWindow : EditorWindow
    {
        private const int MaxLogLines = 300;

        private string _path = string.Empty;
        private Vector2 _logScroll;
        private Vector2 _commandScroll;
        private bool _showSettings = true;
        private bool _showCommands;
        private bool _showRuntimeCommands;
        private bool _captureLogs = true;

        private readonly List<string> _logs = new List<string>();
        private Action<CommandLog.Level, string> _previousSink;

        /// <summary>打开窗口</summary>
        public static void Open()
        {
            var window = GetWindow<CommandToolsWindow>(false, "指令系统", true);
            window.minSize = new Vector2(420, 360);
            window.Show();
        }

        private void OnEnable()
        {
            _previousSink = CommandLog.Sink;
            CommandLog.Sink = OnLog;
        }

        private void OnDisable()
        {
            if (CommandLog.Sink == OnLog)
            {
                CommandLog.Sink = _previousSink;
            }
            _previousSink = null;
        }

        private void OnLog(CommandLog.Level level, string message)
        {
            if (_captureLogs)
            {
                _logs.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
                if (_logs.Count > MaxLogLines)
                {
                    _logs.RemoveAt(0);
                }
            }

            (_previousSink ?? CommandLog.DefaultSink)?.Invoke(level, message);
        }

        private void OnGUI()
        {
            DrawExecuteSection();
            EditorGUILayout.Space();
            DrawSettingsSection();
            EditorGUILayout.Space();
            DrawHttpSection();
            EditorGUILayout.Space();
            DrawRuntimeSection();
            EditorGUILayout.Space();
            DrawCommandListSection();
            EditorGUILayout.Space();
            DrawLogSection();
        }

        #region 执行区

        private void DrawExecuteSection()
        {
            EditorGUILayout.LabelField("执行指令文件", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            _path = EditorGUILayout.TextField("文件路径", _path);
            if (GUILayout.Button("浏览", GUILayout.Width(56)))
            {
                var picked = EditorUtility.OpenFilePanel("选择指令文件",
                    CommandSystemSettings.EffectiveWatchDirectory, "json");
                if (!string.IsNullOrEmpty(picked))
                {
                    _path = picked;
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("执行"))
            {
                Run(false);
            }
            if (GUILayout.Button("空跑（Dry Run）"))
            {
                Run(true);
            }
            if (GUILayout.Button("扫描目录"))
            {
                var count = CommandAutoRunner.ScanAndExecuteDirectory();
                CommandLog.Info($"扫描完成，处理 {count} 个文件");
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("打开指令目录", GUILayout.Width(96)))
            {
                // 默认目录是 .workbuddy 下的隐藏约定目录，直接打开方便查看/新建
                EditorUtility.RevealInFinder(CommandPathUtil.EnsureDefaultCommandDirectory());
            }
            EditorGUILayout.EndHorizontal();
        }

        private void Run(bool dryRun)
        {
            if (string.IsNullOrWhiteSpace(_path))
            {
                CommandLog.Warning("请先填写指令文件路径");
                return;
            }

            var report = CommandAutoRunner.ExecuteFile(_path.Trim(), dryRun);
            if (report != null)
            {
                CommandLog.Info(report.ToSummary());
            }
        }

        #endregion

        #region 设置区

        private void DrawSettingsSection()
        {
            _showSettings = EditorGUILayout.Foldout(_showSettings, "执行设置", true);
            if (!_showSettings)
            {
                return;
            }

            EditorGUI.indentLevel++;

            EditorGUILayout.BeginHorizontal();
            var directory = EditorGUILayout.TextField("指令目录", CommandSystemSettings.EffectiveWatchDirectory);
            if (GUILayout.Button("选择", GUILayout.Width(56)))
            {
                var picked = EditorUtility.OpenFolderPanel("选择指令目录",
                    CommandSystemSettings.EffectiveWatchDirectory, string.Empty);
                if (!string.IsNullOrEmpty(picked))
                {
                    directory = picked;
                }
            }
            EditorGUILayout.EndHorizontal();

            if (directory != CommandSystemSettings.EffectiveWatchDirectory)
            {
                CommandSystemSettings.WatchDirectory = directory;
            }

            var verbose = EditorGUILayout.Toggle("逐条日志", CommandSystemSettings.Verbose);
            if (verbose != CommandSystemSettings.Verbose)
            {
                CommandSystemSettings.Verbose = verbose;
            }

            var stopOnError = EditorGUILayout.Toggle("失败即中止", CommandSystemSettings.StopOnError);
            if (stopOnError != CommandSystemSettings.StopOnError)
            {
                CommandSystemSettings.StopOnError = stopOnError;
            }

            var archive = EditorGUILayout.Toggle("执行后归档到 _done", CommandSystemSettings.ArchiveAfterRun);
            if (archive != CommandSystemSettings.ArchiveAfterRun)
            {
                CommandSystemSettings.ArchiveAfterRun = archive;
            }

            var deleteAfter = EditorGUILayout.Toggle("执行后删除指令文件", CommandSystemSettings.DeleteAfterRun);
            if (deleteAfter != CommandSystemSettings.DeleteAfterRun)
            {
                CommandSystemSettings.DeleteAfterRun = deleteAfter;
                // 删除优先于归档：开删除时自动关归档，避免既删又留 _done
                if (deleteAfter && CommandSystemSettings.ArchiveAfterRun)
                {
                    CommandSystemSettings.ArchiveAfterRun = false;
                }
            }

            EditorGUILayout.HelpBox(
                "执行后处理三选一：删除（优先）> 归档到 _done > 都不勾则保留原文件。想批量清理已攒文件可用 command.cleanup 指令。",
                MessageType.None);

            var watchdog = EditorGUILayout.IntField("单指令超时告警（秒，0=关）",
                CommandSystemSettings.WatchdogSeconds);
            if (watchdog != CommandSystemSettings.WatchdogSeconds)
            {
                CommandSystemSettings.WatchdogSeconds = watchdog;
            }

            var roots = EditorGUILayout.TextField("额外文件访问根（分号分隔）",
                CommandSystemSettings.FileAccessRoots);
            if (roots != CommandSystemSettings.FileAccessRoots)
            {
                CommandSystemSettings.FileAccessRoots = roots;
                // 立即生效，无需重启编辑器
                CommandAutoRunner.ApplyFileAccessRootsFromSettings();
            }
            EditorGUILayout.HelpBox(
                "file.* 默认只能读写项目根与工作空间根；.git/ProjectSettings/Library/Temp 与指令系统源码目录始终禁止。",
                MessageType.None);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("重新扫描指令类"))
            {
                var count = CommandAutoRunner.RegisterCommands();
                CommandLog.Info($"已注册 {CommandRegistry.Default.Count} 条指令（本次新增 {count} 条）");
            }
            if (GUILayout.Button("恢复默认设置"))
            {
                CommandSystemSettings.Reset();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel--;
        }

        #endregion

        #region HTTP 服务区

        private void DrawHttpSection()
        {
            EditorGUILayout.LabelField("HTTP 指令服务", EditorStyles.boldLabel);

            // 持久化开关：是否启用 HTTP 监听。开启即尝试拉起监听并写回本工程设置，
            // 关闭即停止监听并清掉启用标记；跨域重载 / 编辑器重启后按此恢复。
            // 这是用户要的"Http 监听开关"——独立于下方即时启停按钮，反映长期意图。
            var enabled = CommandSystemSettings.HttpServerEnabled;
            var newEnabled = EditorGUILayout.Toggle("启用 HTTP 监听", enabled);
            if (newEnabled != enabled)
            {
                if (newEnabled)
                {
                    CommandHttpServerBoot.StartServer(CommandSystemSettings.HttpPort,
                        CommandSystemSettings.HttpToken);
                }
                else
                {
                    CommandHttpServerBoot.StopServer();
                }
            }

            var running = CommandHttpServerBoot.IsRunning;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("状态",
                running ? "已监听 " + CommandHttpServerBoot.BaseUrl : "未启动",
                EditorStyles.miniLabel);
            if (GUILayout.Button(running ? "停止" : "启动", GUILayout.Width(56)))
            {
                if (running)
                {
                    CommandHttpServerBoot.StopServer();
                }
                else
                {
                    CommandHttpServerBoot.StartServer(CommandSystemSettings.HttpPort,
                        CommandSystemSettings.HttpToken);
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel++;

            var port = EditorGUILayout.IntField("端口", CommandSystemSettings.HttpPort);
            if (port != CommandSystemSettings.HttpPort)
            {
                CommandSystemSettings.HttpPort = port;
                if (running)
                {
                    CommandLog.Info("端口已修改，重启 HTTP 服务后生效");
                }
            }

            EditorGUILayout.BeginHorizontal();
            var token = EditorGUILayout.TextField("访问令牌", CommandSystemSettings.HttpToken);
            if (token != CommandSystemSettings.HttpToken)
            {
                CommandSystemSettings.HttpToken = token;
                if (running && !string.IsNullOrEmpty(token))
                {
                    CommandHttpServerBoot.StartServer(CommandSystemSettings.HttpPort, token);
                }
            }
            if (GUILayout.Button("复制", GUILayout.Width(48)))
            {
                if (string.IsNullOrEmpty(CommandSystemSettings.HttpToken))
                {
                    CommandLog.Warning("令牌为空，请先生成");
                }
                else
                {
                    EditorGUIUtility.systemCopyBuffer = CommandSystemSettings.HttpToken;
                    CommandLog.Info("令牌已复制到剪贴板");
                }
            }
            if (GUILayout.Button("重新生成", GUILayout.Width(72)))
            {
                CommandHttpServerBoot.RegenerateToken();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField("最近执行", CommandHttpServerBoot.LastMessage, EditorStyles.miniLabel);

            EditorGUI.indentLevel--;
        }

        #endregion

        #region 运行时指令区

        /// <summary>
        /// 运行时（Play 模式）指令开关与清单。
        /// <para>
        /// 与上方"编辑指令"分区的理由：这两类指令的风险等级不同——编辑指令改的是资产，
        /// 运行时指令改的是**正在跑的游戏状态**（点按钮可能存档、联网、退出）。
        /// 所以这里默认关闭，并且单独成区、单独说明，不和执行设置混在一起。
        /// </para>
        /// </summary>
        private void DrawRuntimeSection()
        {
            EditorGUILayout.LabelField("运行时指令（Play 模式干预）", EditorStyles.boldLabel);

            var enabled = CommandSystemSettings.RuntimeCommandsEnabled;
            var newEnabled = EditorGUILayout.Toggle(RuntimeCommandGate.ToggleLabel, enabled);
            if (newEnabled != enabled)
            {
                CommandSystemSettings.RuntimeCommandsEnabled = newEnabled;
                CommandLog.Info(newEnabled
                    ? "已启用运行时指令：Play 模式下 runtime.* 可以操作正在运行的游戏"
                    : "已关闭运行时指令：runtime.* 现在一律执行失败");
            }

            if (newEnabled)
            {
                EditorGUILayout.HelpBox(
                    "已启用。AI 现在可以在 Play 模式下操作正在运行的游戏：进/出 Play、点按钮、拖列表、写输入框" +
                    "都可能触发存档写入、网络请求等真实副作用。请确认当前运行的游戏可被随意操作，用完记得关掉。",
                    MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "默认关闭。关闭时所有 runtime.* 指令（含 runtime.play / runtime.stop 进出 Play 模式、" +
                    "运行时找节点 / 点按钮 / 拖列表 / 写输入框）一律执行失败，并在错误里提示来这里打开；" +
                    "常规编辑指令（editor.play 等）不受影响。",
                    MessageType.None);
            }

            EditorGUILayout.LabelField("当前状态",
                EditorApplication.isPlaying
                    ? "Play 模式中（开关打开时 Play 类指令可执行；runtime.play 此时反而会失败）"
                    : "编辑模式（只有 runtime.play 可用，其余 runtime.* 会失败）",
                EditorStyles.miniLabel);

            _showRuntimeCommands = EditorGUILayout.Foldout(_showRuntimeCommands, "运行时指令清单", true);
            if (!_showRuntimeCommands)
            {
                return;
            }

            EditorGUI.indentLevel++;
            var any = false;
            foreach (var descriptor in CommandRegistry.Default.All)
            {
                if (!descriptor.IsRuntime)
                {
                    continue;
                }

                any = true;
                EditorGUILayout.LabelField(descriptor.Id,
                    $"[{RuntimeCommandGate.DescribePhase(descriptor.RuntimePhase)}] {descriptor.Description}",
                    EditorStyles.miniLabel);
            }

            if (!any)
            {
                EditorGUILayout.LabelField("（未注册任何运行时指令）", EditorStyles.miniLabel);
            }
            EditorGUI.indentLevel--;
        }

        #endregion

        #region 指令列表

        private void DrawCommandListSection()
        {
            _showCommands = EditorGUILayout.Foldout(_showCommands,
                $"已注册指令（{CommandRegistry.Default.Count}）", true);
            if (!_showCommands)
            {
                return;
            }

            _commandScroll = EditorGUILayout.BeginScrollView(_commandScroll, GUILayout.Height(150));
            foreach (var descriptor in CommandRegistry.Default.All)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(descriptor.Id, GUILayout.Width(180));
                EditorGUILayout.LabelField(descriptor.Description, EditorStyles.miniLabel);
                if (descriptor.IsRuntime)
                {
                    EditorGUILayout.LabelField("[运行时]", EditorStyles.miniLabel, GUILayout.Width(60));
                }
                else if (descriptor.IsEditorOnly)
                {
                    EditorGUILayout.LabelField("[编辑器]", EditorStyles.miniLabel, GUILayout.Width(60));
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }

        #endregion

        #region 日志区

        private void DrawLogSection()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("运行日志", EditorStyles.boldLabel);
            _captureLogs = EditorGUILayout.Toggle("捕获", _captureLogs, GUILayout.Width(70));
            if (GUILayout.Button("清空", GUILayout.Width(56)))
            {
                _logs.Clear();
            }
            EditorGUILayout.EndHorizontal();

            var rect = EditorGUILayout.BeginVertical(GUI.skin.box);
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(160));
            for (var i = _logs.Count - 1; i >= 0; i--)
            {
                EditorGUILayout.LabelField(_logs[i], EditorStyles.wordWrappedMiniLabel);
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
            GUILayoutUtility.GetRect(rect.width, 0);
        }

        #endregion
    }
}
