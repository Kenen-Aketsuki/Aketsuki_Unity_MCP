---
id: FEAT-014
type: feature
title: 敏感操作人工确认与「全权委托」开关
status: done
source: Aketsuki_Unity_MCP
created: 2026-10-28
updated: 2026-10-28
files: Core/CommandAttribute.cs, Core/CommandRegistry.cs, Core/ConsentGate.cs, Core/CommandRunner.cs, Core/HttpProtocol.cs, CommandSystemSettings.cs, CommandHttpServer.cs, CommandToolsWindow.cs, Commands/ConsentWindow.cs, Commands/ConsentCommands.cs, Commands/EditorStateCommands.cs, Commands/SceneCommands.cs, McpBridge/unity_mcp_server.py, McpBridge/SKILL.md, README.md, HANDOVER.md
proposal: false
---

## 需求

用户 2026-10-28：*「AI 直接把我 Play 模式退了。添加一个『全权委托』模式开关，如果这个开关打开那么直接放行以下内容：
进入/退出 Play 模式、开启场景，否则这些命令都需要弹出 Unity 默认模态框并计时，超时自动拒绝。」*

前因不是"指令写错了"，是**现场被清掉且不可撤销**：`editor.stop` 一执行，用户跑到一半的游戏就没了。

## 怎么做的

三道摆放，都在既有架构的位置上，不新增旁路：

| 位置 | 作用 |
| --- | --- |
| `CommandAttribute.RequiresConsent` + `ConsentMessage` | 声明敏感操作，并给出"会发生什么"的一句话 |
| `ConsentGate`，在 `CommandRunner.ExecuteEntry` 里拦 | 未开全权委托就登记请求 + 弹窗，**本条不执行**，批次中止 |
| 控制台「敏感操作确认」区 | 全权委托开关 + 确认超时（默认 30s，<5s 回退 30s），按工程隔离 |

`editor.play` / `editor.stop` / `scene.open` 三条打上标记；`scene.open` 原来自己弹窗的那段删掉，改由统一门禁处理。

## 关键决策：异步，不是模态

最初按需求写了"模态框 + 倒计时"，**实测把编辑器卡死**：

1. `ShowModal()` 之后主线程进入模态循环，`EditorApplication.update` 不再触发；
2. 自定义窗的 `OnGUI` 也不会主动重绘（没有 repaint 请求）；
3. 倒计时同时依赖这两个源 → 两个都不跑 → **超时永远不走**；
4. 指令与主线程一起僵住，HTTP 队列堆积：`/v1/status` 还能回（后台线程），但任何指令都不执行。

实测数据：发一条 `editor.stop`，等了 180 秒仍未超时，只能人工点掉窗口。
说明旧版 `scene.open` 的"30 秒超时"**从未真正生效过**——用户每次都点得比超时快。

改成两段式：

- 敏感指令 → 立即返回 `Skipped` + `data.consentId`，批次中止，编辑器不阻塞；
- 确认窗用 `ShowUtility()`（非模态），倒计时走正常 `update`，到点自动 `expired`；
- 人点「允许」→ `EditorApplication.delayCall` 到下一帧单独执行那一条（避开 GUI 回调里的域重载）；
- AI 用 `consent.fetch {id}` 取结论。

## 踩过的坑

- **结果必须落盘**：允许 `editor.play` 后立刻域重载，静态列表清空，不落盘 AI 永远查不到"到底执行了没有"。
  现在写 `<指令目录>/consent-results.json`，启动时回读；Pending 项不落盘（上下文无法跨域重载恢复）。
- **摘要文案会骗人**：原 `BuildSummary` 在"0 成功 + N 跳过"时说"全部成功"，
  敏感操作被挂起时会让人以为已经执行了。已改成"N 条指令都没有真正执行（M 条被跳过）"。
- **`PermissionWindow.cs` 已删除**：它就是这个卡死的来源。删除时全仓库已无引用。

## 验证（本工程实测，2026-10-28）

| 场景 | 结果 |
| --- | --- |
| 下发 `editor.stop`（未开委托） | 18.7ms 返回 `Skipped` + `consentId`，后续指令 `Skipped`，编辑器不阻塞 |
| 倒计时 | `consent.fetch` 依次读到"剩余 4 秒"→ 到点变 `expired`，`pendingCount` 归零，窗口自动关闭 |
| 人工允许 | 批准后指令真的执行（`editor.stop` 幂等返回"不在 Play 模式中"），`state=allowed` |
| 全权委托 | 同一批次内"刚开开关 + `editor.stop`"立刻放行，不再弹窗（实时读，不读快照） |
| 关闭委托 | 恢复拦截 |

`/v1/status` 新增 `consent` 块（`fullDelegation` / `timeoutSeconds` / `pendingCount` / `toggle` / `menu`）；
`/v1/commands` 新增 `requiresConsent`，MCP 工具描述里会带"⚠ 敏感操作"提示。

## 未做 / 边界

- **未部署到其他工程**（用户 2026-10-28 明确："本次更新后不要同步到其他项目"）。
  `project_island_back` / `majotavern` / `CivelRev` 上仍是旧版。
- `runtime.play` / `runtime.stop` **没有**打敏感标记：它们已经由「启用运行时指令」开关管控，
  再加一层确认等于问两遍。要改的话在 `RuntimePlayModeCommands.cs` 加 `RequiresConsent = true` 即可。
- 确认窗同时只能看到当前所有 pending（列表式），不支持"记住这个选择"——需要免打扰就开全权委托。
