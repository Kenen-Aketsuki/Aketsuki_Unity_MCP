---
id: IMPR-008
type: improve
title: editor.play 不做内存/运行时长前置检查，长跑编辑器一进 Play 就 OOM，HTTP 服务消失、MCP 全断
status: done
source: project_island_back
created: 2026-10-28
updated: 2026-10-29
files: Packages/com.aketsuki.unity-mcp/Editor/Core/PlayModeMemoryGuard.cs; Packages/com.aketsuki.unity-mcp/Editor/Commands/EditorStateCommands.cs; Packages/com.aketsuki.unity-mcp/Editor/Commands/Runtime/RuntimePlayModeCommands.cs
proposal: true
---

## 症状

在 `project_island_back` 里调 `editor.play`，编辑器弹原生 OOM 框：

```
Could not allocate memory: System out of memory!
ALLOC_DEFAULT used: 4.88 GB
```

随后：

- 进程还活着但卡在弹框，`WorkingSet` 实测 **37.9 GB**（连续运行 14.4 小时）
- `.workbuddy/UnityEditCommand/.http-info.*` 消失 → **HTTP 指令服务没了**
- MCP 所有工具全部失联，只能人工重启编辑器

已保存的改动没丢（前一步 `scene.save` 已落盘），但整个验证流程就此中断，人必须放下手上事去重启。

## 复现

不是稳定复现，取决于水位。已知的触发条件：

| 项 | 当时的实测值 |
|---|---|
| 编辑器连续运行 | 14.4 小时 |
| 进程 WorkingSet | 37.92 GB |
| 进入 Play 前的动作 | 正常编译（0 error），无异常 |
| 结果 | 进 Play 瞬间 OOM，服务文件消失 |

重启编辑器后（WorkingSet 3.65 GB / 系统可用 52 GB）同样的 `editor.play` 一次就成功，说明不是场景或代码问题，纯粹是进程水位。

期望：进 Play 之前先取样，超阈值时**在指令层就说清楚**，不要把人送进 OOM——因为 OOM 的代价不是「指令失败」，而是「HTTP 服务消失 + MCP 全断 + 人工重启」。

## 待拍板

| | 做好处 | 代价 |
|---|---|---|
| 只警告仍执行 | 改动最小，不挡正常使用 | 人看到警告照样点，等于没防护 |
| 超阈值拒绝 + `force: true` 放行 | OOM 前就被拦住；正常编辑器（3~6 GB）无感知 | 阈值定错会误拦一次，得多带一个参数重试 |
| 不做 | 零风险 | 每隔十来小时就可能复现一次，代价是整会话断开 |

**我的倾向**：拒绝 + `force` 放行。理由是代价极不对称——误拦一次只是多带个参数，OOM 一次是人工重启 + 会话重建。

阈值建议粗一点就够，不用精确（取样源：`Process.GetCurrentProcess().WorkingSet64`、`SystemInfo.systemMemorySize` 或 `GlobalMemoryStatusEx` 的可用量、进程 `StartTime`）：

- **提示**（仍执行）：WorkingSet > 16 GB，或系统可用 < 8 GB，或运行时长 > 8 h
- **拒绝**（需 `force`）：WorkingSet > 24 GB，或系统可用 < 4 GB

另外希望 message 里带上「距上次进 Play 多久 / 建议先重启编辑器」，因为「先重启一下」这个操作 AI 自己做不到，得让人知道该干嘛。

**用户批注**：（等待）

---

## 执行结果（2026-10-29 第七跑，用户批注「可以，但仅限于 MCP 指令，不要干预用户人工操作」）

新增 `Core/PlayModeMemoryGuard.cs`，按批件给的粗阈值实现（误拦只是一次重试，OOM 是人工重启）：

| 档位 | 条件 | 行为 |
| --- | --- | --- |
| 提示 | 工作集 > 16 GB，或系统可用 < 8 GB，或运行 > 8 h | 照常执行，`data.memory.level=warn` |
| 拒绝 | 工作集 > 24 GB，或系统可用 < 4 GB | **失败**并说明代价；`force:true` 放行并回 `memoryNotice` |

- **作用范围严格按批注**：检查写在 `editor.play` / `runtime.play` 的指令处理里，
  编辑器自己的 Play 按钮、菜单、快捷键一条都不碰。
- `editor.state` 新增 `memory` 块（采样 + 阈值），AI 可以先查再决定要不要进 Play。
- 系统可用内存走 `GlobalMemoryStatusEx`（Windows），取不到就退回"只看工作集与运行时长"，不会因此误拦。

**踩到的坑（写在代码注释里，别退回去）**：`Process.GetCurrentProcess().WorkingSet64`
在 island_back 的 Unity 2022 编辑器上**实测返回 0**（同一个对象的 `StartTime` 却正常），
只看它这道检查会形同虚设。现在先走 PSAPI 的 `GetProcessMemoryInfo`，取不到才退回 `WorkingSet64`，
最后兜 `GC.GetTotalMemory`。

真机（island_back）：`editor.state` 实测 `workingSetGB=23.49`、`availablePhysicalGB=34.03`、
`uptimeHours=14.04`、`level=warn`（23.49 刚好压在 24 GB 拒绝线下方，说明阈值确实接在真实值上）。
**"拒绝"分支未实测** —— 要把水位推过 24 GB 或临时下调阈值，已记进 `HANDOVER.md` §8.1。