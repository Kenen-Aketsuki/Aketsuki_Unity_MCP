---
id: IMPR-003
type: improve
title: Prefab 内支持跨 Prefab 实例引用；复核 prefab.duplicateNode 层级展开
status: done
source: project_island_back
created: 2026-10-22
updated: 2026-10-23
files: Assets/Editor/ExternalCommandSystem/Commands/PrefabEditCommands.cs
---

## 症状

在 Prefab 资产内编辑节点时有两个能力缺口：

1. **`copyFrom`（prefab.addNode）只能引用同一 Prefab 内的源节点路径**，无法引用别的 Prefab。
   因此不能在 Prefab 资产内建立「跨 Prefab 的嵌套实例」（即把一个已有 Prefab 作为子节点实例化进来）。
2. **`prefab.duplicateNode` 被记为「会展开成扁平节点，别用」**——复制带子节点的节点时子树结构疑似丢失。
   但当前实现用的是 `UnityEngine.Object.Instantiate(source.gameObject, parent)`，理论上会连同子树一起克隆，
   此症状**待用当前代码实测复核**（可能是历史旧实现的遗留记录）。

## 复现

### 1. copyFrom 跨 Prefab 限制（已确认）

```json
{"command": "prefab.addNode", "args": {"prefab": "Assets/GameRes/X.prefab", "name": "NewNode", "copyFrom": "Assets/GameRes/Y.prefab/SomeChild"}}
```

期望：在 `X.prefab` 内建一个节点，其组件数据来自 `Y.prefab` 的 `SomeChild`。

实际：报错 `Prefab 'X.prefab' 里找不到源节点 'Assets/GameRes/Y.prefab/SomeChild'`。
`FindInPrefab(root, copyFromSpec)` 只在 `X.prefab` 的 root 子树内查找，不认别的 Prefab 路径。

报错原文：

```
CommandArgumentException: Prefab 'Assets/GameRes/X.prefab' 里找不到源节点 'Assets/GameRes/Y.prefab/SomeChild'
```

### 2. prefab.duplicateNode 扁平化（待复核）

```json
{"command": "prefab.duplicateNode", "args": {"prefab": "Assets/GameRes/X.prefab", "target": "Parent/ChildA"}}
```

报告症状：复制后 `Parent/ChildA_Copy` 的子节点没有一起带过来，层级被拍平。
**需先用当前代码实测确认是否仍存在**（当前 `Instantiate(source.gameObject, parent)` 应保留子树）。

## 猜测（修复方向）

1. 跨 Prefab 实例：若要在 Prefab 内嵌入另一个 Prefab 作为嵌套实例，应走 `PrefabUtility` 的嵌套 Prefab 实例化
   （而非 `CopySerialized` 平铺数据）。这是能力增强，需评估是否值得做——很多情况下用 `copyFrom` 平铺已够用。
2. duplicateNode 扁平化：若实测复现，检查 `Instantiate` 后是否有额外逻辑清掉子节点，或 `BuildNodePath` 输出误导；
   若不复现，本条仅保留为 copyFrom 跨 Prefab 一项即可。

## 怎么修的

### 1. copyFrom 跨 Prefab（`PrefabEditUtil.LoadCopySource`）

`copyFrom` 现在认两种写法：

| 写法 | 含义 |
| --- | --- |
| `Parent/ChildA` | 当前 Prefab 内的节点路径（旧口径，行为不变） |
| `Assets/GameRes/Other.prefab/SomeChild` | **跨 Prefab**：临时 `LoadPrefabContents` 那个 Prefab 取节点 |
| `Assets/GameRes/Other.prefab` | 跨 Prefab，取该 Prefab 的根 |

拆路径时按 `.prefab` 后是否紧跟 `/` 或结尾判定边界，`SomeNode.prefabHolder/x` 这种节点名不会被误拆。
跨 Prefab 的 Prefab 内容用 `CopySource : IDisposable` 持有，`using` 包住复制过程，Dispose 时 `UnloadPrefabContents`，
异常路径也保证卸载（旧实现只在本 Prefab 内查找，没有加载/卸载问题）。

**已知限制（结果里带 `warning` 字段明示）**：`EditorUtility.CopySerialized` 会把对象引用原样搬过来，
而源对象在临时加载的 Prefab 内容里，保存后这些引用会变成 null。跨 Prefab 复制适合只搬值类型/资源引用，
需要指回当前 Prefab 内节点的引用要另用 `component.setField` 重指。真要在 Prefab 内嵌**嵌套 Prefab 实例**
（保留 Prefab 连接），本指令做不到——那需要 `PrefabUtility.InstantiatePrefab` 语义，另开指令更合适。

### 2. duplicateNode 层级——**实测不复现，原记录是历史遗留**

用当前部署代码在 CivelRev 实跑：

```json
{"commands":[{"id":"prefab.duplicateNode","params":{"prefab":"Assets/__McpSelfCheck/Src.prefab","target":"Parent/ChildA"}}]}
```

结果：

```
已复制 ChildA → Src/Parent/ChildA_Copy（含子树 1 个直接子节点，共 1 个后代）
childCount: 1, subtreeCount: 1, subtreePaths: ["Src/Parent/ChildA_Copy/GrandA"]
```

`prefab.tree` 复核：`ChildA_Copy` 下确实有 `GrandA`，结构与源节点一致。
`Instantiate(source.gameObject, parent)` 本来就整体克隆子树，**扁平化症状不存在**。

为了让以后再有人怀疑时能一眼核对，复制结果新增了 `childCount` / `subtreeCount` / `subtreePaths` 三个字段，
并补了 `includeChildren` 参数（默认 true，`false` 时复制后丢掉子树，与 `gameobject.duplicate` 对齐）。
