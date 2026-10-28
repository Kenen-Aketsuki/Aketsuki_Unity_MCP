using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace CivilRev.CommandSystem.Editor
{
    /// <summary>一次「敏感操作确认」的生命周期状态</summary>
    internal enum ConsentState
    {
        /// <summary>已弹窗，等人点</summary>
        Pending = 0,

        /// <summary>人点了允许，指令已执行完并成功</summary>
        Allowed = 1,

        /// <summary>人点了拒绝</summary>
        Rejected = 2,

        /// <summary>倒计时走完没响应，按拒绝处理</summary>
        Expired = 3,

        /// <summary>允许了但指令执行失败</summary>
        Failed = 4
    }

    /// <summary>一次确认请求的全部上下文（含重新执行所需的指令原文）</summary>
    internal sealed class ConsentRecord
    {
        internal string Id;
        internal string CommandId;
        internal string Message;
        internal DateTime CreatedUtc;
        internal DateTime DeadlineUtc;
        internal int TimeoutSeconds;
        internal ConsentState State = ConsentState.Pending;

        /// <summary>指令执行后的结果（仅 Allowed / Failed 有意义）</summary>
        internal bool ResultOk;
        internal string ResultMessage;

        // 重新执行用；不落盘（域重载后这些对象本来就失效了）
        internal CommandEntry Entry;
        internal CommandScript Source;

        internal JsonValue ToJson()
        {
            var data = JsonValue.NewObject();
            data.Set("id", JsonValue.Of(Id ?? string.Empty));
            data.Set("commandId", JsonValue.Of(CommandId ?? string.Empty));
            data.Set("state", JsonValue.Of(Describe(State)));
            data.Set("message", JsonValue.Of(Message ?? string.Empty));
            data.Set("createdUtc", JsonValue.Of(CreatedUtc.ToString("o")));
            data.Set("deadlineUtc", JsonValue.Of(DeadlineUtc.ToString("o")));
            data.Set("timeoutSeconds", JsonValue.Of(TimeoutSeconds));
            data.Set("remainingSeconds", JsonValue.Of(RemainingSeconds));
            if (State == ConsentState.Allowed || State == ConsentState.Failed)
            {
                data.Set("resultOk", JsonValue.Of(ResultOk));
                data.Set("resultMessage", JsonValue.Of(ResultMessage ?? string.Empty));
            }

            return data;
        }

        internal int RemainingSeconds
        {
            get
            {
                if (State != ConsentState.Pending)
                {
                    return 0;
                }

                var remaining = (DeadlineUtc - DateTime.UtcNow).TotalSeconds;
                return remaining <= 0 ? 0 : (int)Math.Ceiling(remaining);
            }
        }

        internal static string Describe(ConsentState state)
        {
            switch (state)
            {
                case ConsentState.Pending: return "pending";
                case ConsentState.Allowed: return "allowed";
                case ConsentState.Rejected: return "rejected";
                case ConsentState.Expired: return "expired";
                case ConsentState.Failed: return "failed";
                default: return "unknown";
            }
        }
    }

    /// <summary>
    /// 敏感操作的统一门禁：进/出 Play 模式、打开场景这些"一按就改现场"的指令，
    /// 在「全权委托」关闭时必须由人点一次「允许」。
    ///
    /// <para>
    /// <b>为什么必须有它：</b>AI 调一次 <c>editor.stop</c>，用户跑到一半的游戏就没了，
    /// 手上没有任何撤销手段；<c>scene.open</c> 更狠，连场景都可能换掉。
    /// 这类操作的风险不在"写错值"，而在"现场消失"，所以不能跟普通编辑指令共用默认放行策略。
    /// </para>
    ///
    /// <para>
    /// <b>为什么是异步的（别改回模态窗）：</b>最初用 <c>EditorWindow.ShowModal()</c> 同步等待，
    /// 实测<b>直接把编辑器卡死</b>——模态循环期间 <c>EditorApplication.update</c> 不再触发，
    /// 自定义窗的 <c>OnGUI</c> 也不会主动重绘，倒计时两个驱动源同时失效，
    /// 30 秒超时永远不走，指令和主线程一起僵在那里（实测卡了 180 秒仍未超时）。
    /// 现在的做法是：指令<b>立即返回"待确认 + consentId"</b>，确认窗用非模态
    /// <c>ShowUtility()</c> 打开，倒计时走正常 update；人点允许后指令在下一帧单独执行，
    /// 结果用 <c>consent.fetch</c> 取。编辑器全程不阻塞。
    /// </para>
    ///
    /// <para>
    /// <b>为什么结果要落盘：</b>允许 <c>editor.play</c> 之后立刻触发域重载，静态列表会被清空；
    /// 不落盘的话 AI 永远查不到"那条到底执行了没有"。所以每次状态变更写一份
    /// <c>consent-results.json</c>，域重载后重新加载最近若干条。
    /// </para>
    ///
    /// <para>
    /// <b>为什么状态要有快照：</b>与 <see cref="RuntimeCommandGate"/> 同因——<c>/v1/status</c>
    /// 在 HTTP 监听线程拼响应，<c>EditorPrefs.GetBool</c> 是主线程专用 API，
    /// 后台线程直接读会让 status 变 500，而客户端靠 status 做端点发现（整条通路会瘫）。
    /// 门禁判定本身走 <see cref="EnabledLive"/>（实时值），不走快照。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    internal static class ConsentGate
    {
        /// <summary>开关在控制台里的显示名（与窗口里保持一致）</summary>
        internal const string ToggleLabel = "全权委托（放行 Play 切换与开场景）";

        /// <summary>控制台菜单路径，写进提示文案，省得用户去找</summary>
        internal const string ConsoleMenuPath = "工具 / 指令系统 / 指令控制台";

        /// <summary>超时默认值（秒）；可由设置项覆盖，但不允许小于 5 秒</summary>
        internal const int DefaultTimeoutSeconds = 30;

        /// <summary>最多保留多少条历史记录（避免名单无限增长）</summary>
        private const int MaxRecords = 20;

        /// <summary>记录文件名；落在指令目录下，与指令文件同源管理</summary>
        private const string ResultFileName = "consent-results.json";

        private static readonly List<ConsentRecord> Records = new List<ConsentRecord>();
        private static readonly object RecordsLock = new object();

        private static bool _enabled;
        private static int _timeoutSeconds;
        private static int _pendingCount;
        private static int _sequence;

        static ConsentGate()
        {
            EditorApplication.update += Refresh;
            Refresh();
            LoadResults();
        }

        /// <summary>主线程刷新快照 + 推进超时（每帧两次 EditorPrefs 读，成本可忽略）</summary>
        private static void Refresh()
        {
            _enabled = CommandSystemSettings.FullDelegationEnabled;
            _timeoutSeconds = CommandSystemSettings.ConsentTimeoutSeconds;
            ExpireOverdue();
        }

        /// <summary>全权委托是否打开（主线程快照，任意线程可安全读）</summary>
        internal static bool Enabled
        {
            get { return _enabled; }
        }

        /// <summary>确认窗倒计时秒数（主线程快照）</summary>
        internal static int TimeoutSeconds
        {
            get { return _timeoutSeconds; }
        }

        /// <summary>待确认条数（主线程快照，<c>/v1/status</c> 用）</summary>
        internal static int PendingCount
        {
            get { return _pendingCount; }
        }

        /// <summary>实时读取开关（<b>仅主线程</b>）：门禁判定用它，避免同一批里刚开的开关不生效</summary>
        internal static bool EnabledLive
        {
            get { return CommandSystemSettings.FullDelegationEnabled; }
        }

        /// <summary>实时读取超时秒数（仅主线程）</summary>
        internal static int TimeoutSecondsLive
        {
            get { return CommandSystemSettings.ConsentTimeoutSeconds; }
        }

        /// <summary>
        /// 需要确认时登记一条请求并弹窗；返回 <c>null</c> 表示<b>放行</b>（非敏感指令或已开全权委托）。
        /// <para>返回值非空时，调用方<b>不能执行</b>该指令，应把「待确认」报给客户端。</para>
        /// </summary>
        internal static ConsentRecord RequestIfNeeded(
            CommandDescriptor descriptor, CommandEntry entry, CommandScript source, string commandId)
        {
            if (descriptor == null || !descriptor.RequiresConsent)
            {
                return null;
            }

            // 实时读：同一批指令里"刚打开全权委托 + 执行 editor.play"必须立刻放行
            if (EnabledLive)
            {
                return null;
            }

            var timeout = TimeoutSecondsLive;
            var action = string.IsNullOrEmpty(descriptor.ConsentMessage)
                ? descriptor.Description
                : descriptor.ConsentMessage;

            ConsentRecord record;
            lock (RecordsLock)
            {
                record = new ConsentRecord
                {
                    Id = NextId(),
                    CommandId = commandId ?? string.Empty,
                    Message = action,
                    CreatedUtc = DateTime.UtcNow,
                    DeadlineUtc = DateTime.UtcNow.AddSeconds(timeout),
                    TimeoutSeconds = timeout,
                    Entry = entry,
                    Source = source
                };

                Records.Add(record);
                TrimRecords();
                _pendingCount = CountPendingLocked();
            }

            ConsentWindow.ShowWindow();
            CommandLog.Warning(
                $"敏感操作需要人工确认：{commandId}（{record.Id}）。" +
                $"请在确认窗点「允许」，{timeout} 秒内不响应将自动拒绝。");
            return record;
        }

        /// <summary>待确认的记录快照（供窗口渲染）</summary>
        internal static List<ConsentRecord> GetPending()
        {
            var list = new List<ConsentRecord>();
            lock (RecordsLock)
            {
                foreach (var record in Records)
                {
                    if (record.State == ConsentState.Pending)
                    {
                        list.Add(record);
                    }
                }
            }

            return list;
        }

        /// <summary>按 Id 找记录（找不到返回 null）</summary>
        internal static ConsentRecord Find(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            lock (RecordsLock)
            {
                foreach (var record in Records)
                {
                    if (string.Equals(record.Id, id, StringComparison.Ordinal))
                    {
                        return record;
                    }
                }
            }

            return null;
        }

        /// <summary>最近若干条记录（新的在前），供 consent.list 使用</summary>
        internal static List<ConsentRecord> Recent(int limit)
        {
            var list = new List<ConsentRecord>();
            lock (RecordsLock)
            {
                for (var i = Records.Count - 1; i >= 0 && list.Count < limit; i--)
                {
                    list.Add(Records[i]);
                }
            }

            return list;
        }

        /// <summary>
        /// 人点了「允许」：立即执行那条指令。
        /// <para>只在主线程调用（窗口按钮 / delayCall）。执行可能触发域重载，所以先落盘「已允许」。</para>
        /// </summary>
        internal static ConsentRecord Approve(string id)
        {
            var record = Find(id);
            if (record == null || record.State != ConsentState.Pending)
            {
                return record;
            }

            if (record.Entry == null)
            {
                record.State = ConsentState.Failed;
                record.ResultMessage = "请求上下文已失效（可能发生过域重载），请重新下发指令";
                UpdatePendingCount();
                SaveResults();
                return record;
            }

            record.State = ConsentState.Allowed;
            record.ResultMessage = "已允许，正在执行…";
            UpdatePendingCount();
            // 先落盘：接下来这条很可能就是 editor.play，执行完立刻域重载
            SaveResults();

            var runner = new CommandRunner
            {
                Verbose = CommandSystemSettings.Verbose,
                StopOnError = true,
                WatchdogSeconds = CommandSystemSettings.WatchdogSeconds
            };

            try
            {
                var result = runner.ExecuteEntry(record.Entry, record.Source, runner.Variables, 0, true);
                record.ResultOk = result.Success;
                record.ResultMessage = result.Message;
                record.State = result.Success ? ConsentState.Allowed : ConsentState.Failed;

                if (runner.AssetsDirty)
                {
                    AssetDatabase.Refresh();
                }
            }
            catch (Exception error)
            {
                record.State = ConsentState.Failed;
                record.ResultOk = false;
                record.ResultMessage = error.Message;
            }

            SaveResults();
            return record;
        }

        /// <summary>人点了「拒绝」</summary>
        internal static void Reject(string id)
        {
            var record = Find(id);
            if (record == null || record.State != ConsentState.Pending)
            {
                return;
            }

            record.State = ConsentState.Rejected;
            record.ResultMessage = "用户拒绝";
            UpdatePendingCount();
            SaveResults();
            CommandLog.Info($"{record.CommandId} 被用户拒绝（{record.Id}）");
        }

        /// <summary>倒计时走完：按拒绝处理（安全默认值）</summary>
        private static void ExpireOverdue()
        {
            var expired = false;
            lock (RecordsLock)
            {
                var now = DateTime.UtcNow;
                foreach (var record in Records)
                {
                    if (record.State != ConsentState.Pending || now < record.DeadlineUtc)
                    {
                        continue;
                    }

                    record.State = ConsentState.Expired;
                    record.ResultMessage = "超时未响应，按拒绝处理";
                    expired = true;
                }

                if (expired)
                {
                    _pendingCount = CountPendingLocked();
                }
            }

            if (!expired)
            {
                return;
            }

            SaveResults();
            CommandLog.Warning("敏感操作确认超时，已按拒绝处理");
        }

        /// <summary>
        /// 待确认结果附带的结构化状态，供客户端机器判定（不必解析中文文案）。
        /// </summary>
        internal static JsonValue PendingJson(ConsentRecord record)
        {
            var data = JsonValue.NewObject();
            data.Set("requiresConsent", JsonValue.Of(true));
            data.Set("pending", JsonValue.Of(true));
            data.Set("consentId", JsonValue.Of(record.Id));
            data.Set("commandId", JsonValue.Of(record.CommandId));
            data.Set("timeoutSeconds", JsonValue.Of(record.TimeoutSeconds));
            data.Set("remainingSeconds", JsonValue.Of(record.RemainingSeconds));
            data.Set("fetchCommand", JsonValue.Of("consent.fetch"));
            data.Set("toggle", JsonValue.Of(ToggleLabel));
            data.Set("menu", JsonValue.Of(ConsoleMenuPath));
            return data;
        }

        /// <summary>
        /// <c>/v1/status</c> 里的敏感操作摘要：一眼看出"我的 editor.play 为什么没执行"。
        /// <para>只读主线程快照，因为本方法在被 HTTP 监听线程调用。</para>
        /// </summary>
        internal static JsonValue SummaryJson(int commandCount)
        {
            var data = JsonValue.NewObject();
            data.Set("fullDelegation", JsonValue.Of(Enabled));
            data.Set("timeoutSeconds", JsonValue.Of(TimeoutSeconds));
            data.Set("commandCount", JsonValue.Of(commandCount));
            data.Set("pendingCount", JsonValue.Of(PendingCount));
            data.Set("toggle", JsonValue.Of(ToggleLabel));
            data.Set("menu", JsonValue.Of(ConsoleMenuPath));
            return data;
        }

        // ---------------------------------------------------------------- 内部

        private static string NextId()
        {
            _sequence++;
            var suffix = Guid.NewGuid().ToString("N").Substring(0, 4);
            return $"c{DateTime.UtcNow:HHmmss}{_sequence % 100:00}{suffix}";
        }

        private static int CountPendingLocked()
        {
            var count = 0;
            foreach (var record in Records)
            {
                if (record.State == ConsentState.Pending)
                {
                    count++;
                }
            }

            return count;
        }

        private static void UpdatePendingCount()
        {
            lock (RecordsLock)
            {
                _pendingCount = CountPendingLocked();
            }
        }

        private static void TrimRecords()
        {
            while (Records.Count > MaxRecords)
            {
                // 优先丢最旧的已完成记录，绝不丢还在等待的那条
                var index = Records.FindIndex(r => r.State != ConsentState.Pending);
                Records.RemoveAt(index < 0 ? 0 : index);
            }
        }

        private static string ResultFilePath
        {
            get { return Path.Combine(CommandPathUtil.DefaultCommandDirectory, ResultFileName); }
        }

        /// <summary>
        /// 把记录写成 JSON（只写可序列化字段）。失败不影响主流程——落盘只是为了跨域重载可查。
        /// </summary>
        private static void SaveResults()
        {
            try
            {
                var items = new List<JsonValue>();
                lock (RecordsLock)
                {
                    // 只落盘"已有结论"的记录：Pending 的上下文本来就不能跨域重载恢复
                    foreach (var record in Records)
                    {
                        if (record.State == ConsentState.Pending)
                        {
                            continue;
                        }

                        items.Add(record.ToJson());
                    }
                }

                var root = JsonValue.NewObject();
                root.Set("updatedUtc", JsonValue.Of(DateTime.UtcNow.ToString("o")));
                root.Set("records", JsonValue.FromArray(items));

                var path = ResultFilePath;
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, root.ToJson(true));
            }
            catch (Exception error)
            {
                CommandLog.Warning("写入确认记录失败（不影响执行）：" + error.Message);
            }
        }

        /// <summary>域重载后把上次的记录读回来，保证 AI 还能查到"那条到底执行了没有"</summary>
        private static void LoadResults()
        {
            try
            {
                var path = ResultFilePath;
                if (!File.Exists(path))
                {
                    return;
                }

                var root = JsonValue.Parse(File.ReadAllText(path));
                var items = root == null ? null : root["records"];
                if (items == null || items.Type != JsonValueType.Array)
                {
                    return;
                }

                lock (RecordsLock)
                {
                    for (var i = 0; i < items.Count; i++)
                    {
                        var item = items[i];
                        if (item == null || item.Type != JsonValueType.Object)
                        {
                            continue;
                        }

                        Records.Add(new ConsentRecord
                        {
                            Id = item["id"].AsString(string.Empty),
                            CommandId = item["commandId"].AsString(string.Empty),
                            Message = item["message"].AsString(string.Empty),
                            State = ParseState(item["state"].AsString(string.Empty)),
                            ResultOk = item["resultOk"].AsBool(false),
                            ResultMessage = item["resultMessage"].AsString(string.Empty),
                            CreatedUtc = ParseTime(item["createdUtc"].AsString(null)),
                            DeadlineUtc = ParseTime(item["deadlineUtc"].AsString(null)),
                            TimeoutSeconds = item["timeoutSeconds"].AsInt(DefaultTimeoutSeconds)
                        });
                    }

                    _pendingCount = CountPendingLocked();
                }
            }
            catch (Exception error)
            {
                CommandLog.Warning("读取确认记录失败（忽略）：" + error.Message);
            }
        }

        private static ConsentState ParseState(string text)
        {
            switch ((text ?? string.Empty).ToLowerInvariant())
            {
                case "allowed": return ConsentState.Allowed;
                case "rejected": return ConsentState.Rejected;
                case "expired": return ConsentState.Expired;
                case "failed": return ConsentState.Failed;
                default: return ConsentState.Pending;
            }
        }

        private static DateTime ParseTime(string text)
        {
            DateTime parsed;
            return DateTime.TryParse(text, out parsed) ? parsed : DateTime.UtcNow;
        }
    }
}
