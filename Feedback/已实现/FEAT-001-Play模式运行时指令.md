---
id: FEAT-001
type: feature
title: Play 模式运行时指令（runtime.*）：找节点 / 点按钮 / 拖列表 / 写输入框，默认关闭需显式开启
status: done
source: Aketsuki_Unity_MCP
created: 2026-10-24
updated: 2026-10-24
files: Core/RuntimeCommandGate.cs, Commands/Runtime/*.cs, CommandSystemSettings.cs, CommandToolsWindow.cs, Core/CommandRunner.cs, Core/HttpProtocol.cs
proposal: false
---

## 需求

让 MCP 能在 Unity **Play 模式期间**干预运行中的游戏：从运行时场景找节点、模拟按钮点击、
模拟滚动列表滑动。要求：

1. 控制台里加一个**默认关闭**的开关，关闭时这些指令**全部执行失败**并给出说明；
2. 这套 Play 模式指令与常规编辑指令**分开**。

设计前的可行性评估见对话记录：运行时对象访问几乎白捡（`SceneQuery` 本来就用
`FindObjectsOfType`），事件注入有现成的 UI/EventSystems 依赖，真正的架构约束是
**`ICommand.Execute` 是同步的、一个 tick 跑完整个队列**。

## 实现

### 一、与编辑指令分开的四道分隔

| 维度 | 做法 |
| --- | --- |
| 命名 | 全部 `runtime.` 前缀，与 `asset.*` / `scene.*` / `gameobject.*` 不重叠 |
| 代码 | `Commands/Runtime/` 独立目录 + `Core/RuntimeCommandGate.cs` 独立门禁 |
| 声明 | `[Command(..., Runtime = true)]`，`CommandDescriptor.IsRuntime` 透出 |
| 展示 | 控制台单独一区「运行时指令（Play 模式干预）」；指令总表标 `[运行时]`；`/v1/commands` 带 `runtime: true` |

### 二、双门禁（`RuntimeCommandGate`）

顺序固定，先开关后 Play：

1. `CommandSystemSettings.RuntimeCommandsEnabled`（**默认 false**，按工程隔离，存 EditorPrefs）；
2. `EditorApplication.isPlaying` 必须为真。

先判开关：开关没开是配置问题，这时报"不在 Play 模式"会把人带偏。

拦截点放在 `CommandRunner.ExecuteEntry`——HTTP / `file.run` / 控制台扫描三条路径的唯一共同入口，
且放在 dry-run 之前（这是"根本不允许执行"，不是"执行了但跳过"）。

失败时返回结构化字段，客户端不必解析中文：

```json
{"runtime": true, "commandId": "runtime.click", "enabled": false, "playing": false,
 "toggle": "启用运行时指令", "menu": "工具 / 指令系统 / 指令控制台"}
```

`/v1/status` 另给 `runtimeCommands: {enabled, playing, commandCount}`，一眼看出为什么调不动。

### 三、四条指令

| 指令 | 关键点 |
| --- | --- |
| `runtime.hierarchy` | 走 `SceneQuery.SceneRoots`（`FindObjectsOfType`），含运行时实例、inactive、DontDestroyOnLoad 分组；`name` 过滤走扁平命中，默认 `limit=400` 封顶并回报 `truncated` |
| `runtime.click` | `EventSystem.RaycastAll` 做命中诊断 + `ExecuteHierarchy` 派发 down/up/click；`direct:true` 跳过射线直打 `IPointerClickHandler`；回报 `raycast.hits`、遮挡 `warning`、`button.listenerCount`、`eventSystemAfter.selected` |
| `runtime.scroll` | 向 ScrollRect 注入 beginDrag/drag×N/endDrag；`mode=position` 可一步到位；发完**实测**位置变化，没动就退回直接设位置并标 `fallback:true` |
| `runtime.input` | TMP_InputField 优先、退 UGUI InputField；写值后 fire `onEndEdit`（TMP 另 fire `onSubmit`） |

## 实测（本仓库 D:/Wrok_proj/Aketsuki_Unity_MCP，2021.3.16f1c1）

临时用一条 `test.buildRuntimeScene` 指令搭了 Canvas + 按钮 + ScrollRect(10 项) + InputField 的
冒烟场景（验证完连场景带指令一起删除，不进提交）。

| 项 | 结果 |
| --- | --- |
| 开关关闭时 | `runtime.hierarchy` 失败并给出"去控制台勾选"的完整指引 + 结构化 data |
| 开关开、编辑模式 | 失败并提示"先用 editor.play 进入 Play 模式" |
| `runtime.hierarchy` | `playMode=true, frameCount=2`，树模式 1 场景 5 节点；`name=Item` 命中 10 个 Item，组件清单正确 |
| `runtime.click` | 屏幕 (551,475)、`positionSource=ui-overlay`、raycast 命中 `UIRoot/BtnStart`、三个 handler 全部命中、`eventSystemAfter.selected=BtnStart`（证明 Selectable 真的处理了事件） |
| `runtime.click` direct | 只走 `pointerClick`，绕过射线 |
| `runtime.scroll` | 拖拽路径 `1.000 → 0.500`（amount=0.5，8 步，53 像素），`fallback=false`；反向 `-0.25` → `0.500→0.750`；`mode=position` → `0.750→0.550` |
| `runtime.input` | `'' → 'tester01'`；`append` 模式 `'' → '-suffix'` |
| 失败分支 | 目标不存在 / 目标不是 ScrollRect 都给出可执行的中文原因 |

## 实现过程中踩的四个坑（都改了，别退回去）

1. **`EditorPrefs.GetBool` 不能从后台线程读**。`/v1/status` 在 HTTP 监听线程拼响应，
   我最初的 `RuntimeCommands` 直接读设置 → `GetBool can only be called from the main thread`
   → **整个 `/v1/status` 变 HTTP 500**，而 `send.py` 的端点发现依赖它，
   结果连 `--url` 直连都发不出去（客户端全瘫）。改为 `RuntimeCommandGate` 每帧在主线程刷快照。
2. **`ScrollRect.OnDrag` 不读 `eventData.delta`**，它用「当前光标位置 − OnBeginDrag 时光标位置」。
   只改 delta 会原地不动，必须真的把 `PointerEventData.position` 一步一步挪过去。
3. **纵向拖拽方向与屏幕坐标反着来**：Unity 屏幕 y 轴朝上，往下翻内容要**往上**拖；
   方向写反会被 `MovementType.Clamped` 直接吃掉，表现为"拖了没动"。
4. **`amount` 的换算基准是可滚动范围，不是视口高度**。用视口高度算，"滚半屏"实际会滚出 2 倍。

## 已知边界

- **没有跨帧等待**：一条指令占一帧，"点一下→等动画→再点"由客户端控节奏；滚动因此**没有松手惯性**。
- **拿不到键盘/摇杆硬输入**：游戏代码直接读 `Input` 的部分 EventSystem 注入碰不到。
- **未做**：`runtime.screenshot`（截图是帧末行为，需要跨帧能力）、`wait.frames`/`wait.until`、
  断言原语、`runtime.invoke`（反射调任意方法）。

后续若要做跨帧，方向是给 `CommandRunner` 加可逐帧推进的执行体（协程/状态机），
让 `wait.*` 与截图在同一请求里串起来——这是 Phase 2。
