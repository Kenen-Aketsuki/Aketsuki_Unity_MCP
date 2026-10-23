---
id: IMPR-002
type: improve
title: 支持通过指令设置 GameObject.layer（及 GameObject 自身属性）
status: done
source: project_island_back
created: 2026-10-22
updated: 2026-10-23
files: Assets/Editor/ExternalCommandSystem/Commands/GameObjectCommands.cs, Commands/SceneQueryCommands.cs
---

## 症状

`component.setField` / `setFields` 只作用于**组件**的可序列化字段（`RequireComponent` 内部走 `target.GetComponent(type)`）。
而 `layer` / `tag` / `name` 是 **GameObject 自身**属性，不在任何组件上，因此无法用任何现有指令设置节点层级（layer）。
要改一个节点的 layer，只能回编辑器手动点，指令系统触达不到。

## 复现

概念性尝试（当前没有能成功的写法）：

```json
{"command": "component.setField", "args": {"target": "Panel/Top/Btn", "component": "GameObject", "field": "layer", "value": 8}}
```

实际：`component = "GameObject"` 时 `RequireComponent` 用 `target.GetComponent(typeof(GameObject))` 取不到（GameObject 不是 Component），报「找不到组件」或「没有可序列化字段」。
`field = "layer"` 即使组件取到了也无法映射到 GameObject.layer 属性。

报错原文（推测，按现有 `RequireComponent` 逻辑）：

```
CommandArgumentException: 节点 'Panel/Top/Btn' 上找不到组件 'GameObject'
```

## 猜测（修复方向 / 待定）

需要给 `setField` 增加「目标即 GameObject 自身」分支：当 `component` 省略或显式取 GameObject 时，直接操作 `go.layer` / `go.tag` / `go.name` 等属性。
具体放开哪些 GameObject 属性属于**设计决策**，需用户拍板（见下方待定），不要自行扩范围。

### 待定（需用户拍板）
- 只放开 `layer`，还是连 `tag` / `name` / `active` 一起放开？（`active` 已有 `gameobject.setActive` 专用指令，可能不必重复）
- 是否复用 `component.setField` 的字段名机制（如约定 `component` 缺省 = GameObject），还是新增独立指令 `gameobject.setLayer`？后者更直白但新增专用指令，与"积木搭房子"原则需权衡。

## 怎么修的

**没有**把 GameObject 塞进 `component.setField`（`GameObject` 不是 `Component`，`GetComponent(typeof(GameObject))`
永远返回 null，硬塞要在解析层开特例，语义会变浑浊）。改成新增一条独立指令，写侧读侧各补一半：

**写侧**：`gameobject.setProperty`
- `layer`：数字 0-31 或层名（`"UI"`）；层名不存在时报错并列出工程里所有已定义层。
- `tag` / `isStatic`：一并放开。`active` 不重复（已有 `gameobject.setActive`），改名不重复（已有 `gameobject.rename`）。
- `includeChildren`（默认 false）：连同整棵子树一起刷 layer——"整个面板丢到 UI 层"是常态需求，逐个节点下发太笨。
- 返回 `affectedCount`，可核对到底改了几个节点。

**读侧**：`scene.hierarchy` / `gameobject.find` 的每个节点新增 `layer` / `layerName` / `tag` / `isStatic`，
改完立刻能验证，不用再靠 `scene.inspect` 翻组件。

**范围说明**：`gameobject.*` 家族一直是**场景语义**（`setActive` / `rename` / `setParent` 都没有 prefab 模式），
这条保持一致，只作用于场景节点。Prefab 资产内节点要改 layer 目前得实例化到场景再改。

实测：`gameobject.setProperty target=McpSelfCheckRoot layer=UI includeChildren=true` → `layer=5(UI)（含子树 4 个节点）`，
`scene.hierarchy` 回来整棵树 4 个节点 layer 全是 5。

## 口径说明（原「待定」两项的取舍）

- **放开哪些**：`layer` / `tag` / `isStatic`。`active` 已有 `gameobject.setActive`，`name` 已有 `gameobject.rename`，不重复。
- **怎么放开**：新增独立指令 `gameobject.setProperty`，而不是复用 `component.setField`。
  若更希望写 `component: "GameObject"`，需要在 `RequireComponent` 里加特例，属于后续可选增强。
