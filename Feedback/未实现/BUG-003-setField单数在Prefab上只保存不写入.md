---
id: BUG-003
type: bug
title: component.setField（单数）在 Prefab 资产上只保存不写入——ExecuteOnPrefab 漏了 ApplyField
status: open
source: project_island_back
created: 2026-10-23
updated: 2026-10-23
files: Assets/Editor/ExternalCommandSystem/Commands/ComponentCommands.cs
---

## 症状

对 `.prefab` 资源执行 `component.setField`（**单数**），指令回显 Success、消息里还带着"新值"，
但打开 Prefab 发现字段**一点没变**，YAML 也没动。同样参数改用 `component.setFields`（复数）就正常写入。

场景模式（不带 `prefab` 参数）的单数版是好的，所以踩到时很容易误判成"字段名写错了 / 属性名不对"，
会白花时间在字段名上试错。

## 复现

```json
{"id":"component.setField","params":{
  "prefab":"Assets/GameRes/PreResources/Prefabs/UIModules/NewsPaperRewardView.prefab",
  "target":"NewsPaperRewardView",
  "component":"RectTransform",
  "field":"localScale",
  "value":{"x":1,"y":1,"z":1}
}}
```

期望：Prefab 根节点的 `m_LocalScale` 变成 `(1, 1, 1)`。

实际：回显

```
已设置 Prefab 'Assets/.../NewsPaperRewardView.prefab' 的 NewsPaperRewardView.RectTransform.localScale = (1.0, 1.0, 1.0)
```

但文件里 `m_LocalScale` 仍是 `{x: 1.1111112, ...}`；随后 `component.get` 回读也是旧值。
改写成 `component.setFields` + `fields: {"localScale": {...}}` 后一次成功。

## 根因

`ComponentCommands.cs` 的 `ExecuteOnPrefab`（约 87–118 行）在 `ResolveField` 之后**直接保存了**：

```csharp
var op = ResolveField(component, fieldName, raw, context,
    path => FindInPrefab(root, path));

UnityEditor.PrefabUtility.SaveAsPrefabAsset(root, prefabPath);   // ← 中间没有 ApplyField
```

而 `ResolveField` 自己的注释写得很清楚：「完成值转换但**不落盘**」；真正写入在 `ApplyField`。
场景分支（第 76 行）有 `ApplyField(op, true)`，Prefab 分支漏了这一句。

对照：`component.setFields` 的 Prefab 分支是对的 —— `ResolveAll` 之后调了 `ApplyAll(ops, false)`
（约 1101–1103 行），所以只有单数版坏。

## 复现强度

代码层已确认，未做运行时二次验证（当时编辑器处于 Play 模式，`asset.*` 被阻断）。
非 Play 时按上面的 JSON 打一次即可复现，`component.get` 回读就能看出来。

## 怎么修的

（待处理）
