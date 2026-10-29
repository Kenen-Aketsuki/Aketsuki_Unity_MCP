using System.Collections.Generic;
using UnityEngine;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 列出最近的敏感操作确认记录。
    /// <para>
    /// 用途：AI 发完 <c>editor.play</c> / <c>scene.open</c> 拿到的是「待确认 + consentId」，
    /// 之后要么用 <c>consent.fetch</c> 精确查一条，要么用本指令看最近发生了什么
    /// （比如"我上一条到底是被拒了还是超时了"）。
    /// </para>
    /// <para>参数：limit（默认 10，上限 50）。</para>
    /// </summary>
    [Command("consent.list", Description = "列出敏感操作确认记录（待确认 / 已允许 / 已拒绝 / 已超时）",
        Usage = "limit: int", EditorOnly = true)]
    public sealed class ConsentListCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var limit = Mathf.Clamp(context.Args.GetInt("limit", 10), 1, 50);
            var items = ConsentGate.Recent(limit);

            var array = new List<JsonValue>(items.Count);
            foreach (var record in items)
            {
                array.Add(record.ToJson());
            }

            var data = JsonValue.NewObject();
            data.Set("records", JsonValue.FromArray(array));
            data.Set("pendingCount", JsonValue.Of(ConsentGate.PendingCount));
            data.Set("fullDelegation", JsonValue.Of(ConsentGate.EnabledLive));
            data.Set("timeoutSeconds", JsonValue.Of(ConsentGate.TimeoutSecondsLive));

            var pending = ConsentGate.PendingCount;
            var message = items.Count == 0
                ? "还没有任何确认记录"
                : $"共 {items.Count} 条记录" + (pending > 0 ? $"，其中 {pending} 条仍在等待人工确认" : "，当前没有待确认项");
            return CommandResult.Ok(message, data);
        }
    }

    /// <summary>
    /// 查询某次敏感操作确认的结果。
    /// <para>
    /// <b>注意语义：</b>本指令返回成功只代表"查到了这条记录"，不代表那条敏感操作执行成功——
    /// 真正的结论看 <c>data.state</c>（pending / allowed / rejected / expired / failed）
    /// 与 <c>data.resultMessage</c>。这样 AI 不会把"用户拒绝"误判成"查询失败"。
    /// </para>
    /// <para>参数：id（必填，来自上一条指令返回的 consentId）。</para>
    /// </summary>
    [Command("consent.fetch", Description = "查询某次敏感操作确认的结果（state: pending/allowed/rejected/expired/failed）",
        Usage = "id: string", EditorOnly = true)]
    public sealed class ConsentFetchCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var id = context.Args.RequireString("id");
            var record = ConsentGate.Find(id);
            if (record == null)
            {
                return CommandResult.Fail(
                    $"找不到确认记录：{id}。用 consent.list 查看最近记录；" +
                    "记录只保留最近若干条，且 pending 项不会跨域重载保留。");
            }

            var state = ConsentRecord.Describe(record.State);
            string message;
            switch (record.State)
            {
                case ConsentState.Pending:
                    message = $"{record.CommandId} 仍在等待人工确认，剩余 {record.RemainingSeconds} 秒";
                    break;
                default:
                    message = $"{record.CommandId} → {state}" +
                              (string.IsNullOrEmpty(record.ResultMessage) ? string.Empty : "：" + record.ResultMessage);
                    break;
            }

            return CommandResult.Ok(message, record.ToJson());
        }
    }
}
