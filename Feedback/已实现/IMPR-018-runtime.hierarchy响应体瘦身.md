---
id: IMPR-018
type: improve
title: runtime.hierarchy 的响应体要瘦身（BUG-010 的顺带项）
status: done
source: project_island_back
created: 2026-10-28
updated: 2026-10-29
files: Packages/com.aketsuki.unity-mcp/Editor/Commands/Runtime/RuntimeHierarchyCommand.cs; Packages/com.aketsuki.unity-mcp/Editor/Commands/Runtime/RuntimeSupport.cs; Packages/com.aketsuki.unity-mcp/Editor/Commands/SceneQueryCommands.cs
proposal: false
---

## 症状

`limit=400` 的默认值在 UI 工程上一次能回 **730 KB**（400 个命中节点，每个都带完整 `components` 数组），
工具结果会被外置到文件才没把会话顶掉。BUG-010 只改了 limit 的语义，没动体积——
`limit` 现在是命中上限之后，「命中 400 个」反而更容易了。

## 用户批注

> 默认不带 `components`，加一个小参数可以自带 `components`。但每次返回固定携带 Transform/RectTransform

## 执行结果（2026-10-29 第七跑）

按批注实现，改动落在三处：

| 位置 | 改动 |
| --- | --- |
| `SceneQueryCommandsUtil.NodeToJson` | 新增重载 `NodeToJson(node, includeComponents)`；裁剪时只留 `component is Transform`（`RectTransform` 是它的子类，一次判断覆盖 UI 与非 UI）；节点上固定给 `componentsComplete` 标明这份清单是不是全的 |
| `RuntimeSupport.NodeJson` | 同样加裁剪开关 |
| `RuntimeHierarchyCommand` | 新增参数 `includeComponents`（**默认 false**），`data.includeComponents` 回显，message 末尾自动补一句「components 只含 Transform/RectTransform，需要完整组件清单请传 includeComponents: true」 |

**范围只动 `runtime.hierarchy`**：编辑期的 `scene.hierarchy` / `gameobject.find` 走无参重载，
仍然带完整组件清单——它们的量级本来就小，改默认行为属于无谓的破坏兼容。

Transform 之所以一定留：位置 / 尺寸 / 锚点是判断"点哪里"的必要信息，砍掉它这条指令的定位能力就没了。

**未真机实测**（要进 Play 模式，`runtime.*` 双门禁 + 无人点确认窗），已记进 `HANDOVER.md` §8.1。
