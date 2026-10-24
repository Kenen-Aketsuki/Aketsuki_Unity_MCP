---
id: BUG-004
type: bug
title: asset.compile 被拒后，asset.compileReport 的摘要仍宣称「最近一次编译成功」
status: open
source: Aketsuki_Unity_MCP
created: 2026-10-24
updated: 2026-10-24
files: Commands/DiagnosticsCommands.cs
proposal: false
---

## 症状

DOC-001 把「被拒」这件事记进了 `/v1/status` 的 `lastCompile`（`requestedUtc` + `rejectedReason`），
但 `asset.compileReport` 的**摘要行**没跟着改，仍然按 `report.Finished` / `report.Ok` 二元判断：

- 上一次真编译成功过、这次在 Play 模式被拒 → 摘要打印「最近一次编译成功（0 个警告，643ms）」
- 从没编译过、这次被拒 → 摘要打印「还没有完成过一次编译（可先执行 asset.compile）」

两种情况都把「这次没编」说成了别的意思。AI 的「编辑-编译-纠错」闭环最后落点就是这个摘要
（`SKILL.md` 自己写的用法：改完 → `asset.compile` → 轮询空闲 → `asset.compileReport`），
只看摘要会以为改完的代码编译通过了。

`data` 里 `requestedUtc` 比 `finishedUtc` 新、`rejectedReason` 非空，原始数据是对的——**只有摘要措辞错**。

## 复现

本仓库编辑器（`D:/Wrok_proj/Aketsuki_Unity_MCP`），2026-10-24 实测：

```bash
# 1) 编辑模式下先真编译一次，拿到 finishedUtc
#    → lastCompile.finishedUtc = 2026-10-24T02:50:03.5906584Z, ok = true
# 2) editor.play 进 Play
# 3) 再发 asset.compile
echo '{"commands":[{"id":"asset.compile","params":{}}]}' | python Assets/Editor/ExternalCommandSystem/McpBridge/send.py send - --project-root D:/Wrok_proj/Aketsuki_Unity_MCP
#    指令本身正确失败：编辑器正处于预览状态，不允许编译与刷新
#    lastCompile.requestedUtc = 2026-10-24T02:53:22.2816304Z（比 finishedUtc 新 3 分 18 秒）
# 4) 查报告
echo '{"commands":[{"id":"asset.compileReport","params":{"includeMessages":false}}]}' | python ... send - --project-root ...
```

期望：摘要点明「本次请求被拒，最近一次完成于 02:50:03」。
实际：`[OK] 最近一次编译成功（0 个警告，643ms）`。

`data.rejectedReason` 里躺着「编辑器正处于预览状态，不允许编译与刷新」，摘要完全没提。

## 根因

`CompileReportCommand.Execute` 选摘要文案时只看 `Finished` / `Ok`：

```csharp
if (!report.Finished) { message = "还没有完成过一次编译（可先执行 asset.compile）"; }
else if (report.Ok)   { message = $"最近一次编译成功（...）"; }
else                  { message = $"最近一次编译失败：..."; }
```

`RejectedReason` 在 `CompileReport` 里存在、也序列化进 data 了，但没参与文案判定。

## 建议改法（待实现，未动代码）

`RejectedReason` 非空的判断要放在最前面（两处错都在它先被忽略）：

```csharp
if (!string.IsNullOrEmpty(report.RejectedReason))
{
    var lastReal = report.Finished
        ? $"最近一次真正完成的编译在 {report.FinishedUtc}（ok={report.Ok}）"
        : "本次会话还没有完成过任何编译";
    message = $"这一次编译请求被拒：{report.RejectedReason}；{lastReal}";
}
else if (!report.Finished) { ... 原逻辑 ... }
else if (report.Ok)        { ... 原逻辑 ... }
else                       { ... 原逻辑 ... }
```

只动摘要文案，`data` 结构不变，向后兼容。

## 影响面

只有「摘要行」这一处；`/v1/status`、`data` 字段、`asset.compile` 自身的失败返回都正确。
`send.py` 打印的是 `结论/✗ 失败行` 来源于指令结果，被拒时本来就显示失败，不受影响——
真正会踩坑的是直接读 `compileReport` 摘要文本的调用方（人和 AI 都会）。
