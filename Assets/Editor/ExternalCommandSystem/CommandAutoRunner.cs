using System;
using System.IO;
using UnityEditor;

namespace CivilRev.CommandSystem.Editor
{
    /// <summary>
    /// 指令系统的编辑器入口：自动注册 + 手动执行
    /// <para>
    /// 资源开销说明：
    /// <list type="number">
    /// <item><b>注册</b>：仅在程序集重载时执行一次，使用 Unity 的 TypeCache（内部有缓存，几乎无开销）。</item>
    /// <item><b>执行</b>：全部由「指令系统」控制台窗口或菜单手动触发，没有常驻监听、没有轮询、没有后台线程。</item>
    /// </list>
    /// 旧的「文件状态监听」（FileSystemWatcher + 去抖 + 去重）已被移除，下发指令改走 HTTP 指令服务，
    /// 不再依赖目录轮询。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class CommandAutoRunner
    {
        private const string MenuRoot = "Tools/指令系统/";
        private const string MenuOpenWindow = MenuRoot + "控制台";
        private const string MenuExecuteFile = MenuRoot + "执行指令文件...";
        private const string MenuRescan = MenuRoot + "重新扫描指令类";
        private const string MenuOpenFolder = MenuRoot + "打开指令目录";

        static CommandAutoRunner()
        {
            ApplyFileAccessRoots();
            RegisterCommands();
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
        }

        #region 指令注册

        /// <summary>
        /// 从设置重新加载额外允许根并立即生效（控制台窗口改完设置后调用）。
        /// </summary>
        public static void ApplyFileAccessRootsFromSettings()
        {
            ApplyFileAccessRoots();
        }

        /// <summary>
        /// 把 <see cref="CommandSystemSettings.FileAccessRoots"/>（分号分隔的绝对路径）解析后注入
        /// <see cref="CommandPathUtil.ExtraAllowedRoots"/>，作为 file.* 白名单的扩展根。
        /// </summary>
        private static void ApplyFileAccessRoots()
        {
            CommandPathUtil.ExtraAllowedRoots.Clear();
            var raw = CommandSystemSettings.FileAccessRoots;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            var parts = raw.Split(';', ',');
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i].Trim();
                if (part.Length == 0)
                {
                    continue;
                }

                try
                {
                    var full = Path.GetFullPath(part);
                    if (!CommandPathUtil.ExtraAllowedRoots.Contains(full))
                    {
                        CommandPathUtil.ExtraAllowedRoots.Add(full);
                    }
                }
                catch (Exception e)
                {
                    CommandLog.Warning($"忽略无效的 FileAccessRoots 条目 '{part}'：{e.Message}");
                }
            }
        }

        /// <summary>用 TypeCache 注册所有指令类，并做一次反射兜底</summary>
        public static int RegisterCommands()
        {
            var types = TypeCache.GetTypesWithAttribute<CommandAttribute>();
            var count = CommandRegistry.Default.RegisterTypes(types);

            // 兜底：个别 Unity 版本下 TypeCache 可能漏掉非编辑器程序集
            count += CommandRegistry.Default.ScanAssemblies();
            return count;
        }

        #endregion

        #region 执行

        /// <summary>
        /// 执行一个指令文件（控制台窗口、菜单、扫描目录都走这里）
        /// </summary>
        public static CommandRunReport ExecuteFile(string path, bool dryRun = false)
        {
            var runner = new CommandRunner
            {
                Verbose = CommandSystemSettings.Verbose,
                StopOnError = CommandSystemSettings.StopOnError,
                DryRun = dryRun,
                WatchdogSeconds = CommandSystemSettings.WatchdogSeconds
            };

            try
            {
                var report = runner.RunFile(path);

                if (!dryRun && report != null && !report.HasError)
                {
                    // 删除优先于归档：开后者的同时关前者，避免既删又留 _done
                    if (CommandSystemSettings.DeleteAfterRun)
                    {
                        TryDelete(path);
                    }
                    else if (CommandSystemSettings.ArchiveAfterRun)
                    {
                        Archive(path);
                    }
                }

                return report;
            }
            catch (Exception e)
            {
                CommandLog.Error($"执行指令文件失败：{e.Message}");
                return null;
            }
            finally
            {
                // 与 HTTP 路径一致：整个文件（含 batch）只刷新一次
                if (runner.AssetsDirty)
                {
                    try
                    {
                        AssetDatabase.Refresh();
                    }
                    catch (Exception e)
                    {
                        CommandLog.Warning("批次结束刷新 AssetDatabase 失败：" + e.Message);
                    }
                }
            }
        }

        /// <summary>扫描指令目录，逐个执行其中（未归档的）.json 文件</summary>
        public static int ScanAndExecuteDirectory()
        {
            var directory = CommandSystemSettings.EffectiveWatchDirectory;
            if (!Directory.Exists(directory))
            {
                return 0;
            }

            var count = 0;
            var files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                if (fileName.IndexOf(".done", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                ExecuteFile(file);
                count++;
            }
            return count;
        }

        private static void Archive(string path)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                var doneDirectory = Path.Combine(directory, "_done");
                if (!Directory.Exists(doneDirectory))
                {
                    Directory.CreateDirectory(doneDirectory);
                }

                var target = Path.Combine(doneDirectory, Path.GetFileName(path));
                if (File.Exists(target))
                {
                    File.Delete(target);
                }
                File.Move(path, target);
            }
            catch (Exception e)
            {
                CommandLog.Warning($"归档指令文件失败：{e.Message}");
            }
        }

        /// <summary>执行成功后删除已执行的指令文件（DeleteAfterRun 开启时调用）。失败仅告警，不阻塞流程。</summary>
        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception e)
            {
                CommandLog.Warning($"删除已执行指令文件失败：{e.Message}");
            }
        }

        #endregion

        #region 生命周期

        private static void OnAfterAssemblyReload()
        {
            RegisterCommands();
        }

        #endregion

        #region 菜单

        [MenuItem(MenuOpenWindow, priority = 1)]
        private static void OpenWindowMenuItem()
        {
            CommandToolsWindow.Open();
        }

        [MenuItem(MenuExecuteFile, priority = 2)]
        private static void ExecuteFileMenuItem()
        {
            var directory = CommandSystemSettings.EffectiveWatchDirectory;
            var path = EditorUtility.OpenFilePanel("选择指令文件", directory, "json");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var report = ExecuteFile(path);
            if (report != null)
            {
                CommandLog.Info(report.ToSummary());
            }
        }

        [MenuItem(MenuRescan, priority = 20)]
        private static void RescanMenuItem()
        {
            var count = RegisterCommands();
            CommandLog.Info($"已注册 {CommandRegistry.Default.Count} 条指令（本次新增 {count} 条）");
        }

        [MenuItem(MenuOpenFolder, priority = 21)]
        private static void OpenFolderMenuItem()
        {
            var directory = CommandSystemSettings.EffectiveWatchDirectory;
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            EditorUtility.RevealInFinder(directory);
        }

        #endregion
    }
}
