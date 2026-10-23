---
id: IMPR-004
type: improve
title: component.setFields 的 data 回显的是修改前的旧值，消息里没说，容易被当成"没写进去"
status: open
source: project_island_back
created: 2026-10-23
updated: 2026-10-23
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

（待处理）
