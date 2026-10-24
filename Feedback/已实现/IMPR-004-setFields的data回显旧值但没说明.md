---
id: IMPR-004
type: improve
title: component.setFields 的 data 回显的是修改前的旧值，消息里没说，容易被当成"没写进去"
status: done
source: project_island_back
created: 2026-10-23
updated: 2026-10-24
files: Assets/Editor/ExternalCommandSystem/Commands/ComponentCommands.cs
---

## 症状

`component.setFields` 成功执行后，返回体的 `data` 里是**每个字段修改前的旧值**，
但外层消息只说"已批量设置 N 个字段"，没有任何地方说明 `data` 是旧值。

调用方（尤其 AI / 脚本）很自然会拿 `data` 当"写后的当前值"来核对，
看到还是旧值就判定"写入失败"，然后重复写、或者去查字段名。

## 复现

```json
{"id":"component.setFields","params":{
  "prefab":"Assets/.../Panel_LoveImprove.prefab",
  "target":"Panel_Win",
  "component":"RectTransform",
  "fields":{"sizeDelta":{"x":1140,"y":538},"anchoredPosition":{"x":0,"y":45}}
}}
```

返回：

```json
{"ok":true,"records":[{"id":"component.setFields","status":"Success",
  "message":"已批量设置 Prefab '...' 的 Panel_Win.RectTransform 的 2 个字段",
  "data":{"sizeDelta":"(1000.0, 500.0)","anchoredPosition":"(0.0, 0.0)"}}]}
```

期望：`data` 能反映"写后的值"，或者消息里明确说明这是旧值。

实际：`data` 就是旧值（写前 sizeDelta 1000×500、位置 (0,0)），但消息完全没提；
而单数版 `component.setField` 返回的 `data` 是 `fieldName` 字符串——
同族指令的 `data` 语义不一致，进一步加重误解。

## 代码位置

`ComponentCommands.cs` 约 1104–1106 行：

```csharp
return CommandResult.Ok(
    $"已批量设置 Prefab '{normalized}' 的 {node.name}.{component.GetType().Name} 的 {ops.Count} 个字段",
    BuildOldValues(ops));
```

`BuildOldValues` 的注释是「把每个字段修改前的旧值汇总成对象，供复核」——
设计意图是"回显旧值方便对比"，但这条意图没有传达到调用方。

## 建议

三选一，成本从低到高：

1. 消息后缀加一句「（data 为修改前的旧值）」
2. 返回结构改成 `{"old": {...}, "new": {...}}` —— 破坏兼容，慎用
3. 单数、复数统一成"回显旧值"，并在指令 Description 里写明

我倾向 1：不改结构、不破坏任何现有调用。

## 怎么修的

采纳建议里的**方案 1**（成本最低、不改结构、不破坏任何现有调用）：消息后缀加一句说明，
`setFields` 的场景分支与 Prefab 分支都加了。

```csharp
private const string OldValuesNote =
    "（data 为各字段修改前的旧值，用于复核；写后的值请再看消息或 component.get）";
```

同时在 `[Command]` 的 Description 里写明"`data` 回显各字段修改前的旧值"，
这样 `list_commands` / MCP 工具描述里也能看到，不必执行才知道。

**实测（project_island_back，Particle_Rain 的 ParticleSystem）**：

```
已批量设置 Prefab '...Area1.prefab' 的 Particle_Rain.ParticleSystem 的 1 个字段
（data 为各字段修改前的旧值，用于复核；写后的值请再看消息或 component.get）
data: {"main.startSize.scalar": "0.05"}     ← 写入前的值，写后是 1.25
```

**没有做的**：方案 2（`{"old":...,"new":...}`）与方案 3（单数/复数统一成旧值对象）都会改 `data` 的结构，
属破坏性变更——已列入待你拍板清单，没替你决定。

## 后续（2026-10-24 用户拍板：按方案 2 执行）

**用户决策**：调用方是 LLM，"单次给到全部数据"比"靠规则约束它记得再 get 一次"更可靠 —— 采纳方案 2。

现在 `component.setFields` 的 `data` 是：

```json
{"old": {"enabled": true, "m_CellSize": [100, 50]},
 "new": {"enabled": false, "m_CellSize": [212, 59]}}
```

- 两侧都走 `component.get` 同一口径（`SerializedObject` → 结构化 JSON），Color / Vector / 数组 / 枚举
  都是可以直接比对的值，不再是 `"(1000.0, 500.0)"` 这种字符串
- 旧值在 `Apply` 之前读、新值在 `Apply` 之后读；序列化写入通道（粒子模块等）读不回时退到反射值
- 消息里那句「data 为各字段修改前的旧值」改成「`data.old` / `data.new` 为各字段写入前后的值」

**没有做**：方案 3（单数 `component.setField` 的 `data` 也改成同一形状）。用户只批了方案 2，
单数版仍返回字段名字符串 —— 需要统一的话另开一条。
