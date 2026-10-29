---
id: IMPR-019
type: improve
title: component.get 的 field 不支持点号嵌套（**重复单**：与 IMPR-001 是同一件事）
status: done
source: project_island_back
created: 2026-10-28
updated: 2026-10-29
files: Packages/com.aketsuki.unity-mcp/Editor/Commands/ComponentGetCommands.cs
proposal: false
---

## 症状

`setField` / `setFields` 支持 `A.B.C` 嵌套路径，`get` 不支持——要读单个子字段只能先整体读再从结果里取。
项目侧需求单「22:07 批次实测记录」里就留着这一条（`m_Colors.normalColor` 报「没有可序列化字段」）。

## 执行结果（2026-10-29 第七跑，用户批注「补齐」）

**读代码发现这条与 2026-10-23 的 `IMPR-001` 是同一件事，而且早就落地了** ——
`ComponentGetCommands.ResolveProperty()` 先是 `SerializedObject.FindProperty(field)`（原生支持任意深度路径），
未命中再走 `FindPropertyTolerant()`：逐段生成「原名 / 去 `m_` / 加 `m_` / `m_`+首字母大写」的变体，
组合成整条路径交给 `FindProperty` 裁决。批件里引用的那句"仍不支持"是 2026-10-22 的实测记录，
写于 IMPR-001 落地**之前**，一直没跟着更新。

所以本轮做的是**验证 + 清理陈旧记录**，没有改代码：

真机（island_back，`Canvas_Global/Panel_CommonResultShow/Btn_Close` 上的 `Button`）：

```
component.get field="m_Colors.normalColor"  → [1, 1, 1, 1]
component.get field="colors.normalColor"    → [1, 1, 1, 1]   # 无 m_ 前缀的写法同样命中
```

同时把项目侧需求单 `project_island_back/.workbuddy/UnityEditCommand/MCP-优化需求案-持续更新.md`
里那段过期的「22:07 批次实测记录」划掉了，免得下一个人再被它误导。

> 教训记一笔：跨仓库的需求单要标日期。这条就是因为一句没写日期的实测记录，
> 被当成新需求重新提了一次并占用了一整轮拍板。
