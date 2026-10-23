---
id: IMPR-001
type: improve
title: component.get 的 field 支持点号嵌套路径（A.B.C）
status: done
source: project_island_back
created: 2026-10-22
updated: 2026-10-23
files: Assets/Editor/ExternalCommandSystem/Commands/ComponentGetCommands.cs
---

## 症状

`component.get` 的 `field` 只支持**顶层**字段名：精确匹配、`m_` 前缀兼容、以及嵌套 struct 的**尾段**名（如 `normalColor` 能命中 `m_Colors.normalColor`）。
但不支持完整的点号嵌套路径 `A.B.C`，读单个子字段必须先整体读父结构体再自己取子字段。

## 复现

```json
{"command": "component.get", "args": {"target": "Panel/Top/Btn", "component": "Button", "field": "m_Colors.normalColor"}}
```

期望：返回 `m_Colors.normalColor` 子字段值（如高亮色）。

实际：报错 `Button 上没有可序列化字段 'm_Colors.normalColor'`。

报错原文（对应 `ComponentGetCommands.ResolveProperty`）：

```
CommandArgumentException: Button 上没有可序列化字段 'm_Colors.normalColor'（SerializedObject 里没有对应属性）
```

根因：`ResolveProperty` 只在 `it.depth == 0` 的顶层属性里匹配，对 `m_Colors.normalColor` 这种带 `.` 的路径，既不会精确命中顶层、也不会向下钻取，直接走到 `loose` 兜底也失败。

## 猜测（修复方向）

Unity 的 `SerializedObject.FindProperty(string propertyPath)` **原生支持点号嵌套路径**，应优先用它解析 `field`：
1. `so.FindProperty(fieldName)` 命中即返回（天然覆盖 `m_Colors.normalColor`、`m_Colors.m_PressedColor` 等任意深度路径）；
2. 仅当 `FindProperty` 返回 null 时，再回退到现有的宽松匹配（`m_` 前缀 / 尾段名），保持向后兼容。

注意：`component.setField` / `setFields` 已在 C8 实现 `A.B.C` 嵌套**写入**（`ComponentCommands.cs`），唯独 `component.get` 漏了读取侧，属对称补全。

## 怎么修的

`ComponentGetCommands.ResolveProperty` 改成三级解析，前两级是新增：

1. `so.FindProperty(field)` —— Unity 原生支持任意深度路径（含 `m_List.Array.data[0].x`）。
2. `FindPropertyTolerant` —— 逐段生成「原名 / 去 `m_` / 加 `m_` / `m_`+首字母大写」变体，组合成整条路径再交给 `FindProperty` 裁决。
   这一步是为了吃下**按 Inspector / C# 属性名写**的习惯：`colors.normalColor` 或 `m_Colors.normalColor`
   都能命中真正的序列化路径 `m_Colors.m_NormalColor`（实测 `Button.m_Colors.normalColor` → `[1,1,1,1]`）。
   段数上限 6、组合上限 256，超了直接放弃，不做组合爆炸。
3. 原有宽松匹配（顶层精确 / 去 `m_` / 嵌套尾段名）原样保留，旧写法不受影响。

**顺带修掉一个隐性 bug（BUG-004）**：路径命中后 `SerializeProperty` 对 Color/Vector/数组返回的是**数组**，
而原来的 `if (result.Data is JsonValue data) AttachScene(data)` 无条件往上塞 `scenePath`，
`JsonValue.Set` 在数组上报 `[Json] 期望类型 Object，实际为 Array`。以前只能读顶层 struct（返回 Object）所以没暴露，
一放开嵌套路径立刻炸。改成只在 `data.IsObject` 时附场景信息。
