using System;
using System.IO;
using UnityEditor;

namespace Aketsuki.UnityMcp.Editor
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

        /// <summary>
        /// 临时统计：已执行指令文件的收集目录（不做任何设置 UI）。
        /// <para>
        /// 隔离设计：目录不存在即功能关闭（<see cref="CollectForAnalysis"/> 静默跳过），
        /// 删除该目录可一键关停；部署到其它工程时该路径本就不存在，天然零副作用。
        /// 这是有时效的临时能力，移除统计 = 删掉 <see cref="CollectForAnalysis"/> 与这条字段即可。
        /// </para>
        /// <para>
        /// 位置由工程根推导（<c>Application.dataPath</c> 的上一级 + <c>.workbuddy/CommandAnal</c>），
        /// 与原先写死的那条路径指向同一个目录，但不带盘符和用户名：包装到别的工程时，
        /// 指的是那个工程自己的 <c>.workbuddy/CommandAnal</c>，而不是开发机的绝对路径。
        /// </para>
        /// </summary>
        private static readonly string CommandAnalyticsDir = Path.Combine(
            Path.GetDirectoryName(UnityEngine.Application.dataPath) ?? ".",
            ".workbuddy", "CommandAnal");

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
                    // 临时统计：先于删除/归档复制一份到分析目录（复制不破坏原文件，与下面两者不互斥）
                    CollectForAnalysis(path);

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
                // 与 HTTP 路径一致：Prefab 编辑会话在文件跑完后统一卸载（BUG-018）
                try
                {
                    PrefabEditSession.Close();
                }
                catch (Exception e)
                {
                    CommandLog.Warning("批次结束关闭 Prefab 编辑会话失败：" + e.Message);
                }

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

        /// <summary>
        /// 临时统计：把已成功执行的指令文件复制一份到 <see cref="CommandAnalyticsDir"/>，用于后续指令执行统计。
        /// <para>
        /// 行为约束（见 FEAT-017 批注）：
        /// <list type="bullet">
        /// <item>复制而非移动，不破坏原文件，也不与 <c>DeleteAfterRun</c> / <c>ArchiveAfterRun</c> 冲突；</item>
        /// <item>目标目录不存在则**静默跳过**（不拷贝、不报错）—— 删除该目录即关停；</item>
        /// <item>同名碰撞追加时间戳后缀，保留每次执行样本，不覆盖历史；</item>
        /// <item>任何异常都只告警，绝不影响正常执行流程。</item>
        /// </list>
        /// 本方法是统计能力的唯一入口，移除统计 = 删本方法 + 删 <see cref="ExecuteFile"/> 里的调用 + 删 <see cref="CommandAnalyticsDir"/> 字段。
        /// </summary>
        private static void CollectForAnalysis(string path)
        {
            if (!Directory.Exists(CommandAnalyticsDir))
            {
                return; // 目录不存在 = 功能关闭，静默跳过
            }

            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                var fileName = Path.GetFileName(path);
                var target = Path.Combine(CommandAnalyticsDir, fileName);

                // 同名碰撞：追加时间戳后缀，保留每次执行样本（不覆盖历史）
                if (File.Exists(target))
                {
                    var baseName = Path.GetFileNameWithoutExtension(fileName);
                    var extension = Path.GetExtension(fileName);
                    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    target = Path.Combine(CommandAnalyticsDir, $"{baseName}__{stamp}{extension}");
                    var counter = 1;
                    while (File.Exists(target))
                    {
                        target = Path.Combine(CommandAnalyticsDir, $"{baseName}__{stamp}_{counter}{extension}");
                        counter++;
                    }
                }

                File.Copy(path, target, false);
            }
            catch (Exception e)
            {
                CommandLog.Warning($"收集指令文件到分析目录失败（已忽略）：{e.Message}");
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
