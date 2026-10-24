---
id: FEAT-003
type: feature
title: runtime.play / runtime.stop：进出 Play 也纳入运行时指令组，同一个开关管住
status: done
source: Aketsuki_Unity_MCP
created: 2026-10-24
updated: 2026-10-24
files: Core/CommandAttribute.cs, Core/CommandRegistry.cs, Core/CommandRunner.cs, Core/RuntimeCommandGate.cs, Core/HttpProtocol.cs, CommandHttpServer.cs, CommandToolsWindow.cs, Commands/Runtime/RuntimePlayModeCommands.cs
proposal: false
---

## 需求

> 运行时添加进入/退出 Play Mode 的指令，并且和常规指令一样受到开关影响。

已有常规指令 `editor.play` / `editor.stop`，但它们属于"编辑期基础设施"，不受运行时开关约束。
这条需求要的是**同一组 `runtime.*` 里的进/出 Play**：开关关着就进不去，避免
"开关关着但 AI 照样能把游戏跑起来"的口子。

## 实现

### 一、每条运行时指令声明自己要求的 Play 状态

`CommandAttribute.RuntimePhase`：

| 值 | 含义 | 谁在用 |
| --- | --- | --- |
| `Playing`（默认） | 必须在 Play 中 | click / scroll / input / hierarchy |
| `NotPlaying` | 必须不在 Play 中 | `runtime.play` |
| `Any` | 不限 | `runtime.stop`（编辑模式下幂等返回成功） |

门禁顺序不变：**先开关，再 Play 状态**。三种失败文案互不混用：

- 开关关 → 「运行时指令未启用：…请到控制台勾选」
- 需要 Play 却在编辑模式 → 「只能在 Play 模式执行…先用 editor.play」
- 需要编辑模式却在 Play 中 → 「只能从编辑模式执行…要退出用 runtime.stop」

`/v1/commands` 每条运行时指令带 `playState`（`Play 模式` / `编辑模式` / `任意`），
失败结果带 `data.requiredPhase`；控制台的运行时清单也按 `[Play 模式]` / `[编辑模式]` 标注。

### 二、runtime.play 处理脏场景（默认不弹对话框）

`EditorApplication.isPlaying = true` 在有未保存改动时会弹「Save scene?」模态框——无人值守时
编辑器就卡在那儿，客户端只看到超时。所以：

- 默认 `onDirty=abort`：有脏场景就失败，`data.dirtyScenes` 列出清单；
- `onDirty=save`：逐个 `EditorSceneManager.SaveScene`，遇到未保存过的新场景（无资产路径）直接失败并说明；
- 正在编译/导入时拒绝（否则切 Play 拿到旧程序集）。

`runtime.stop` 无参数：不在 Play 时幂等成功；在 Play 时置 `isPlaying=false`。

### 三、门禁改读实时值（实测踩到的坑）

原实现 `RuntimeCommandGate.Check` 读的是每帧刷新的快照（那是给 `/v1/status` 的后台线程用的）。
结果：同一条请求里 `test.setEnabled(false)` + `runtime.stop` 两句在同一帧执行，
第二句仍拿旧快照**放行**——开关关了却还能退出 Play。

现在拆成两套取值：

- `EnabledLive` / `IsPlayingLive`：**主线程**用（执行器、`/v1/execute` 响应），实时读；
- `Enabled` / `IsPlaying`：**快照**，只给 `/v1/status` 这种后台线程拼响应的路径用。

## 实测（本仓库编辑器，开关默认关闭）

| 检查 | 结果 |
| --- | --- |
| 开关关 → `runtime.play` / `runtime.stop` / `runtime.hierarchy` | 三条全部失败，文案指向控制台开关项 |
| 开关关 → `editor.stop` | 正常退出 Play（常规指令不受开关影响，是逃生口） |
| 同一请求内「开开关 + runtime.play」 | 成功进入 Play（证明实时读生效，否则会被旧快照拒） |
| 同一请求内「关开关 + runtime.stop」 | `runtime.stop` 失败（修前会成功，就是这条暴露的快照滞后） |
| Play 中再调 `runtime.play` | 失败：「只能从编辑模式执行…要退出请用 runtime.stop」 |
| 编辑模式下 `runtime.stop` | 成功：「不在 Play 模式中，无需退出」 |
| 脏场景 + `runtime.play` | 失败，`data.dirtyScenes=[SceneA]` |
| 脏场景 + `onDirty=save` | 保存后进入 Play，`data.savedScenes=[SceneA]` |
| `runtime.stop` 退出 | 轮询 status → `playing=false` |

## 已知边界

- 进出 Play 都会**域重载**，响应可能在重载前发出、也可能被截断；调用方要按"轮询 status"处理，
  不能假设这条指令的返回值一定收得到。
- `runtime.play` 只切 Play 状态，不负责加载特定场景；要控制叠加场景请用常规 `scene.open` / 播前准备好。
- `onDirty=save` 只保存**已打开**的场景；未保存过的新场景（无路径）会直接失败，不会替你 Save As。
