---
id: IMPR-006
type: improve
title: component.add / component.remove 的 target 必填，不能用空串表示 Prefab 根节点（与 setField 约定不一致）
status: open
source: project_island_back
created: 2026-10-23
updated: 2026-10-23
files: Assets/Editor/ExternalCommandSystem/Commands/ComponentCommands.cs
---

## 症状

给 Prefab **根节点**加/删组件时，按 `component.setField` 的约定传 `"target": ""`，
会被参数校验拦下，报"缺少必填参数"。只能改成传根节点的名字。

传名字能work，但有副作用：根节点若与某个子节点**同名**（或 Prefab 内部有重名节点），
`FindInPrefabUnique` 会报歧义，而"空串"本来是没有歧义的那个表达。

## 复现

```json
{"id":"component.remove","params":{
  "prefab":"Assets/.../NewsPaperRewardView.prefab",
  "target":"",
  "component":"BackpackExpansion"
}}
```

期望：从根节点移除该组件（`target` 空串 = 根节点，与 `component.setField` 一致）。

实际：

```
缺少必填参数 'target'
```

改成 `"target":"NewsPaperRewardView"` 后成功。

## 代码位置

- `component.remove`：约 974 行 `context.Args.RequireString("target")`
- `component.add`：同样写法
- 对照 `component.setField`：约 60–63 行有专门判断，并在错误信息里明确写着
  「空串只在 Prefab 模式下表示根节点」

也就是说这套约定**只落实在一半指令上**，同族指令的行为不一致，学到的经验不能迁移。

## 建议

`prefab` 存在时允许 `target` 省略或为空串，统一解释为根节点；
`component.add/remove/setField/setFields` 共用同一个 `ResolvePrefabTarget(root, spec)` 入口，
避免以后再出现"某条指令不支持"。

## 怎么修的

（待处理）
