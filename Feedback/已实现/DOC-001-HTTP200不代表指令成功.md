---
id: DOC-001
type: doc
title: HTTP 200 不代表指令成功——asset.compile 在 Play 模式下返回 200 + ok:false，容易误判
status: done
source: project_island_back
created: 2026-10-23
updated: 2026-10-24
files: README.md, Assets/Editor/ExternalCommandSystem/CommandHttpServer.cs
---

## 症状

调用方（脚本 / AI）习惯用"HTTP 状态码 / 请求抛没抛异常"判断成败。
但本服务**命令逻辑失败时仍返回 HTTP 200**，失败信息只在 body 的 `ok` / `records[].status` 里。

实测踩到的场景：编辑器处于 Play 模式时 `asset.compile` 被拒绝，HTTP 200、`ok:false`。
调用方只看"请求成功"就以为编译过了，接着去核 `HotUpdate.dll`，发现时间戳没变，白绕一圈。

## 复现

编辑器进入 Play 模式，然后：

```json
{"id":"asset.compile","params":{}}
```

期望：失败能被调用方一眼识别（非 2xx，或文档里显著声明）。

实际：HTTP **200**，body：

```json
{"ok": false, "total": 1, "succeeded": 0, "failed": 1, "skipped": 0,
 "records": [{"index": 0, "id": "asset.compile", "status": "Failed",
   "message": "编辑器正处于预览状态，不允许编译与刷新", "elapsedMs": 0}]}
```

同一时刻 `/v1/status` 的 `lastCompile.finishedUtc` 仍是**上一次**的时间戳 —— 没有任何"这次没编译"的信号。

## 根因 / 设计取舍

`CommandHttpServer.cs` 只在抛异常时给 `500`（约 635 行 `HttpResponse.Fail(500, ...)`），
`CommandResult.Fail` / `records[].status = Failed` 走的是正常 200 通道。
这样设计对 `batch` 是合理的（一条失败不中断后续），代价是**单条执行时状态码失去意义**。

## 建议（文档为主）

1. README 顶部加一句醒目的：**"HTTP 200 ≠ 成功，必须判 `body.ok`；批量看 `succeeded/failed`"**
2. `asset.compile` 这类"整条命令就一个结果"的指令，失败时同时返回非 2xx（比如 409），
   既保留 batch 语义（batch 内部仍汇总成 200），又让单独调用能被状态码识别
3. `/v1/status` 补一个 `lastCompile.requestedUtc` / `rejectedReason`，让调用方能区分"上次编译"和"这次被拒"

## 怎么修的

建议 1 与建议 3 落地，**建议 2（失败返回非 2xx）没自己做**——那是协议变更，见下文与待拍板清单。

**建议 1：醒目声明（三处）**

根目录 `README.md`、`Assets/Editor/ExternalCommandSystem/README.md`、以及随包分发的
`McpBridge/SKILL.md` 顶部各加了一块告警：HTTP 200 ≠ 成功，成败只看 `body.ok`；
批量再看 `succeeded`/`failed`；状态码只用来识别网络/协议层错误。
（每个包一份 SKILL，所以 MCP 桥那份不能漏。）

**建议 3：让 status 能区分"上次编译"和"这次被拒"**

`CompileReport` 加两个字段，`/v1/status` 的 `lastCompile` 随之带上：

| 字段 | 含义 |
| --- | --- |
| `requestedUtc` | 最近一次"请求编译"的时刻（无论有没有真的编译） |
| `rejectedReason` | 被拒原因；空串表示没被拒 |

判据：**`requestedUtc` 比 `finishedUtc` 新 = 这次没编译成**。
实现要点是 `RecordRejected` **只补这两个字段、保留上一次的真实结果**（错误数/耗时/消息都不动），
否则"上次编译失败了吗"这个信息会被一次被拒的请求冲掉；它同样写进 `SessionState`，
跨域重载不丢。`asset.compile` 在 Play 模式下会先 `RecordRejected` 再按原样抛错。
成功编译时 `requestedUtc` = 编译开始时间、`rejectedReason` = 空串。

**实测**：部署到 project_island_back 后 `/v1/status` 已返回这两个字段：

```json
"lastCompile": {"ok": true, "errorCount": 0, "finishedUtc": "...", "requestedUtc": "", "rejectedReason": ""}
```

（值为空串是因为那是一次真实编译；Play 模式拒绝路径没有实测——进 Play 会打断正在运行的编辑器，
留给手上有空窗口的时候补。）

**建议 2（未做，待拍板）**：单条指令失败时同时返回非 2xx。改的是 HTTP 状态码语义，
所有已经"只看状态码"的调用方都会一夜之间从成功变成抛异常，属于破坏性兼容变更。
选项与影响已整理进待拍板清单。
