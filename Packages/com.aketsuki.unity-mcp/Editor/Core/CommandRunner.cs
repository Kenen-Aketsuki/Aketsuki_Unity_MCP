using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Aketsuki.UnityMcp.Editor;

namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// 指令执行模块（模块二）：解析并执行 <see cref="CommandScript"/> 中的每条指令
    /// <para>
    /// 职责边界：
    /// <list type="bullet">
    /// <item>按 ID 找到对应指令类（<see cref="CommandRegistry"/>）</item>
    /// <item>把参数包传给指令类并执行</item>
    /// <item>捕获异常、统计结果、输出日志</item>
    /// </list>
    /// 具体业务由各个 ICommand 实现类自行完成，运行器不关心任何具体指令。
    /// </para>
    /// </summary>
    public sealed class CommandRunner
    {
        /// <summary>嵌套执行深度上限，防止 batch / run_file 无限递归</summary>
        public const int MaxDepth = 8;

        /// <summary>单条指令软超时的默认阈值（秒）</summary>
        public const int DefaultWatchdogSeconds = 120;

        /// <summary>使用的指令注册表</summary>
        public CommandRegistry Registry { get; }

        /// <summary>遇到失败是否中止后续指令（默认 true）</summary>
        public bool StopOnError { get; set; } = true;

        /// <summary>空跑模式：只记录将要执行的指令，不真正执行</summary>
        public bool DryRun { get; set; }

        /// <summary>是否输出每条指令的日志（关闭后只输出失败与最终摘要）</summary>
        public bool Verbose { get; set; } = true;

        /// <summary>运行期共享变量表，跨指令、跨文件生效</summary>
        public IDictionary<string, object> Variables { get; } =
            new Dictionary<string, object>(StringComparer.Ordinal);

        /// <summary>
        /// 协作式取消：返回 true 时，执行器在下一个检查点（批次里的下一条指令前）干净退出。
        /// <para>
        /// .NET 没有可安全使用的中断机制，因此这里只能"协作"——正在同步执行的单条指令
        /// （如 <c>scene.save</c>）无法被打断，只能等它自己返回。
        /// </para>
        /// </summary>
        public Func<bool> ShouldAbort { get; set; }

        /// <summary>
        /// 指令内部主动请求中止当前批次（例如用户拒绝许可弹窗）。
        /// <para>与 <see cref="ShouldAbort"/>（外部 cancel 请求）并行生效：设置后，运行器在下一条指令前会跳过剩余指令。</para>
        /// <para>本标记挂在运行器实例上，而 HTTP 层每条请求创建独立运行器并在请求结束后丢弃，故天然按「本批次」作用域隔离，不影响后续请求。</para>
        /// </summary>
        private volatile bool _commandAbortRequested;

        /// <summary>
        /// 请求中止当前批次的剩余指令（当前正在执行的这条不受影响，会在它返回后于下一条前生效）。
        /// </summary>
        public void RequestAbort()
        {
            _commandAbortRequested = true;
        }

        /// <summary>
        /// 单条指令的软超时阈值（秒）；执行结束后超过该值记一条 warning。0 表示不检查。
        /// <para>由调用方从设置注入，Core 层不依赖 UnityEditor。</para>
        /// </summary>
        public int WatchdogSeconds { get; set; } = DefaultWatchdogSeconds;

        /// <summary>
        /// 本批次是否写入过 Assets 目录（由 <c>file.write</c> 等指令标记）。
        /// <para>调用方据此在批次结束时统一 <c>AssetDatabase.Refresh()</c> 一次，避免逐条刷新。</para>
        /// </summary>
        public bool AssetsDirty { get; private set; }

        /// <summary>标记"写过 Assets 目录"，请求调用方在批尾刷新一次</summary>
        public void MarkAssetsDirty()
        {
            AssetsDirty = true;
        }

        // 当前指令的起始时间戳（Stopwatch.GetTimestamp）；0 表示空闲。
        // 用 Interlocked 读写：主线程写、后台线程（/v1/status）读，64 位与 32 位运行时都安全。
        private long _currentCommandStartedAt;
        private string _currentCommandId;

        /// <summary>当前正在执行的指令 ID；空闲时为 null</summary>
        public string CurrentCommandId
        {
            get { return _currentCommandId; }
        }

        /// <summary>当前指令已执行时长（毫秒）；空闲时为 0。跨线程读取安全。</summary>
        public long CurrentElapsedMs
        {
            get
            {
                var started = Interlocked.Read(ref _currentCommandStartedAt);
                if (started == 0)
                {
                    return 0;
                }

                var ticks = Stopwatch.GetTimestamp() - started;
                if (ticks <= 0)
                {
                    return 0;
                }

                return (long)(ticks * 1000.0 / Stopwatch.Frequency);
            }
        }

        /// <summary>是否已被请求取消</summary>
        public bool IsAbortRequested
        {
            get
            {
                if (_commandAbortRequested)
                {
                    return true;
                }

                var hook = ShouldAbort;
                if (hook == null)
                {
                    return false;
                }

                try
                {
                    return hook();
                }
                catch (Exception)
                {
                    // 取消委托自身异常不应影响执行
                    return false;
                }
            }
        }

        // 正在执行的文件栈，用于检测循环引用
        private readonly List<string> _fileStack = new List<string>();

        public CommandRunner(CommandRegistry registry = null)
        {
            Registry = registry ?? CommandRegistry.Default;
        }

        #region 对外入口

        /// <summary>读取并执行一个指令文件</summary>
        public CommandRunReport RunFile(string path)
        {
            return RunFile(path, null);
        }

        /// <summary>
        /// 读取并执行一个指令文件
        /// </summary>
        /// <param name="path">文件路径</param>
        /// <param name="baseDirectory">基准目录；相对路径优先相对它解析</param>
        public CommandRunReport RunFile(string path, string baseDirectory)
        {
            var fullPath = CommandPathUtil.Resolve(path, baseDirectory);
            return RunFileInternal(fullPath);
        }

        /// <summary>执行已解析的指令脚本</summary>
        public CommandRunReport Run(CommandScript script)
        {
            if (script == null)
            {
                throw new ArgumentNullException(nameof(script));
            }

            var report = new CommandRunReport { SourcePath = script.FilePath, Total = script.Commands.Count };
            RunEntries(script.Commands, script, Variables, 0, report);
            return report;
        }

        /// <summary>直接执行单条指令（代码内调用）</summary>
        public CommandResult RunSingle(string commandId, JsonValue parameters = null)
        {
            var entry = new CommandEntry
            {
                Id = commandId,
                Params = parameters ?? JsonValue.EmptyObject
            };
            return ExecuteEntry(entry, null, Variables, 0);
        }

        /// <summary>
        /// 执行一组指令（供 batch 等复合指令嵌套调用）
        /// </summary>
        public CommandRunReport RunEntries(
            IList<CommandEntry> entries,
            CommandScript source = null,
            IDictionary<string, object> variables = null,
            int depth = 0,
            CommandRunReport report = null)
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            if (depth > MaxDepth)
            {
                throw new CommandException($"指令嵌套层级超过上限 {MaxDepth}，疑似循环调用");
            }

            if (report == null)
            {
                report = new CommandRunReport { SourcePath = source?.FilePath, Total = entries.Count };
            }

            var bag = variables ?? Variables;
            var watch = Stopwatch.StartNew();

            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry == null)
                {
                    continue;
                }

                // 协作式取消检查点：放在每条指令之前，保证退出点是干净的（不会中断正在执行的那条）
                if (IsAbortRequested)
                {
                    for (var j = i; j < entries.Count; j++)
                    {
                        report.Add(new CommandRecord
                        {
                            CommandId = entries[j]?.Id,
                            Index = j,
                            Depth = depth,
                            Status = CommandStatus.Skipped,
                            Message = "已取消（收到取消请求）"
                        });
                    }
                    break;
                }

                if (entry.Index == 0)
                {
                    entry.Index = i;
                }

                if (!entry.Enabled)
                {
                    report.Add(new CommandRecord
                    {
                        CommandId = entry.Id,
                        Index = i,
                        Depth = depth,
                        Status = CommandStatus.Skipped,
                        Message = "已禁用（enabled=false）"
                    });
                    continue;
                }

                var result = ExecuteEntry(entry, source, bag, depth);

                report.Add(new CommandRecord
                {
                    CommandId = entry.Id,
                    Index = i,
                    Depth = depth,
                    Status = result.Status,
                    Message = result.Message,
                    Data = result.Data
                });

                if (!result.Success && StopOnError)
                {
                    // 标记剩余指令为跳过，便于一眼看出中断位置
                    for (var j = i + 1; j < entries.Count; j++)
                    {
                        report.Add(new CommandRecord
                        {
                            CommandId = entries[j]?.Id,
                            Index = j,
                            Depth = depth,
                            Status = CommandStatus.Skipped,
                            Message = "因前序指令失败而中止"
                        });
                    }
                    break;
                }
            }

            watch.Stop();
            report.ElapsedMs = watch.Elapsed.TotalMilliseconds;
            return report;
        }

        #endregion

        #region 单条执行

        /// <summary>执行一条指令：查表 → 建实例 → 传参 → 执行 → 捕获异常</summary>
        /// <param name="consentGranted">
        /// 人工确认已拿到（由 <see cref="ConsentGate.Approve"/> 重新执行时传入 <c>true</c>）。
        /// <para>为 <c>false</c> 时，敏感指令会在门禁处弹出确认请求并立即返回「待确认」。</para>
        /// </param>
        public CommandResult ExecuteEntry(
            CommandEntry entry,
            CommandScript source,
            IDictionary<string, object> variables,
            int depth,
            bool consentGranted = false)
        {
            var descriptor = Registry.Find(entry.Id);
            if (descriptor == null)
            {
                // "被剔除"和"ID 写错"要分开说：前者有明确原因（缺可选包），照着原因做就能用；
                // 后者是拼写/版本问题。混成一句"未注册的指令 ID"会把人带到错的方向。
                var skipped = Registry.SkipReason(entry.Id);
                var message = skipped == null
                    ? $"未注册的指令 ID：{entry.Id}"
                    : $"指令 '{entry.Id}' 当前不可用，已从指令列表里剔除：{skipped}";

                CommandLog.Error(message);
                return CommandResult.Unknown(entry.Id, message);
            }

            // 运行时（Play 模式）指令的统一门禁：开关关着 / 不在 Play 一律失败。
            // 放在 dry-run 之前，是因为这是"这条指令根本不允许执行"，不是"执行了但跳过"——
            // 空跑同样应该报出来，否则脚本作者体会不到开关没开。
            if (descriptor.IsRuntime)
            {
                var rejection = RuntimeCommandGate.Check(entry.Id, descriptor.RuntimePhase);
                if (!string.IsNullOrEmpty(rejection))
                {
                    CommandLog.Warning(rejection);
                    return CommandResult.Fail(rejection,
                        RuntimeCommandGate.StatusJson(entry.Id, descriptor.RuntimePhase));
                }
            }

            if (DryRun)
            {
                if (Verbose)
                {
                    CommandLog.Info($"[dry-run] {entry.Id} {entry.Params.ToJson(false)}");
                }
                return CommandResult.Skip("dry-run", null);
            }

            // 敏感操作（进/出 Play、打开场景）的人工确认门禁。
            // 放在 dry-run 之后：空跑不产生副作用，没必要为一个"不会发生"的动作打断人。
            // 与运行时门禁的区别：那个是"开关没开就失败"，这个是"开关没开就弹窗问一次"。
            //
            // 注意这里是**异步**的：不等人点，直接返回「待确认」并把指令挂起。
            // 早期用 ShowModal 同步等，实测会让编辑器彻底卡死（模态循环里 update 不触发，
            // 倒计时走不动），所以改由用户点「允许」时再单独执行。
            if (!consentGranted)
            {
                var pendingConsent = ConsentGate.RequestIfNeeded(descriptor, entry, source, entry.Id);
                if (pendingConsent != null)
                {
                    RequestAbort();
                    return CommandResult.Skip(
                        $"'{entry.Id}' 是敏感操作，已弹出确认窗等待人工点「允许」" +
                        $"（{pendingConsent.TimeoutSeconds} 秒后自动拒绝）。" +
                        $"用 consent.fetch id=\"{pendingConsent.Id}\" 取结果；" +
                        $"控制台勾选「{ConsentGate.ToggleLabel}」后可跳过确认。",
                        ConsentGate.PendingJson(pendingConsent));
                }
            }

            ICommand instance;
            try
            {
                instance = descriptor.CreateInstance();
            }
            catch (Exception e)
            {
                CommandLog.Error($"{entry.Id} 实例化失败：{e.Message}");
                return CommandResult.Fail($"实例化失败：{e.Message}");
            }

            var context = new CommandContext(
                entry.Id,
                entry.CreateArgs(),
                this,
                source,
                entry.Index,
                depth,
                DryRun,
                variables);

            var watch = Stopwatch.StartNew();

            // 发布"当前指令"供 /v1/status 观察；主线程写、后台线程只读（Interlocked 保证位数安全）
            _currentCommandId = entry.Id;
            Interlocked.Exchange(ref _currentCommandStartedAt, Stopwatch.GetTimestamp());

            try
            {
                var result = instance.Execute(context) ?? CommandResult.Ok();

                if (result.Status == CommandStatus.Failed)
                {
                    CommandLog.Error($"{entry.Id} 执行失败：{result.Message}");
                }
                else if (Verbose)
                {
                    CommandLog.Info($"{entry.Id} -> {result.Status} {result.Message}");
                }

                return result;
            }
            catch (CommandException e)
            {
                CommandLog.Error($"{entry.Id} 执行失败：{e.Message}");
                return CommandResult.Fail(e.Message);
            }
            catch (Exception e)
            {
                CommandLog.Error($"{entry.Id} 抛出未处理异常：{e}");
                return CommandResult.Fail($"异常：{e.Message}");
            }
            finally
            {
                watch.Stop();
                Interlocked.Exchange(ref _currentCommandStartedAt, 0);
                _currentCommandId = null;
                WarnIfOverWatchdog(entry.Id, watch.Elapsed.TotalSeconds);
            }
        }

        /// <summary>
        /// 单条指令耗时超过阈值时记一条 warning。
        /// <para>这只是"事后可见"：主线程被占住时无法在执行中途告警，也中断不了它，属 Unity 主线程模型的硬约束。</para>
        /// </summary>
        private void WarnIfOverWatchdog(string commandId, double elapsedSeconds)
        {
            if (WatchdogSeconds <= 0 || elapsedSeconds < WatchdogSeconds)
            {
                return;
            }

            CommandLog.Warning(
                $"{commandId} 耗时 {elapsedSeconds:F1}s，超过看门狗阈值 {WatchdogSeconds}s；" +
                "若编辑器已无响应，只能重启编辑器（单条指令无法被中断）");
        }

        #endregion

        #region 文件级执行（含循环引用保护）

        private CommandRunReport RunFileInternal(string fullPath)
        {
            for (var i = 0; i < _fileStack.Count; i++)
            {
                if (string.Equals(_fileStack[i], fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CommandException(
                        $"检测到指令文件循环引用：{string.Join(" -> ", _fileStack)} -> {fullPath}");
                }
            }

            var script = CommandReader.FromFile(fullPath);

            _fileStack.Add(fullPath);
            try
            {
                if (Verbose)
                {
                    CommandLog.Info($"开始执行指令文件：{fullPath}（{script.Commands.Count} 条）");
                }

                var report = Run(script);

                if (Verbose || report.HasError)
                {
                    CommandLog.Info(report.ToSummary());
                }

                return report;
            }
            finally
            {
                _fileStack.RemoveAt(_fileStack.Count - 1);
            }
        }

        /// <summary>当前正在执行的文件栈（调试用）</summary>
        public IReadOnlyList<string> FileStack => _fileStack;

        /// <summary>清空共享变量</summary>
        public void ClearVariables()
        {
            Variables.Clear();
        }

        #endregion
    }
}
