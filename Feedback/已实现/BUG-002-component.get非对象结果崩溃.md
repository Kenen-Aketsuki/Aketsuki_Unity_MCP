---
id: BUG-002
type: bug
title: component.get 读 Color/Vector/数组字段必崩——结果不是对象时仍往里塞场景信息
status: done
source: civilization_revival
created: 2026-10-23
updated: 2026-10-23
files: Commands/ComponentGetCommands.cs
---

## 症状

`component.get` 命中的字段如果序列化出来不是对象（Color / Vector2/3/4 / Rect 之外的一维数组等），
指令整体失败，报 JSON 类型错误。以前只读顶层 struct（返回对象）所以没暴露，
一旦支持点号嵌套路径（IMPR-001），随手读个 `m_Colors.normalColor` 就必崩。

## 复现

```json
{"command":"component.get","params":{"target":"McpSelfCheckRoot/Parent","component":"Button","field":"m_Colors.normalColor"}}
```

期望：返回 `[1,1,1,1]`。
实际：`异常：[Json] 期望类型 Object，实际为 Array`。

## 根因

`GetComponentFieldCommand.Execute` 里的：

```csharp
if (result.Data is JsonValue data) { SceneQueryCommandsUtil.AttachScene(data); }
```

`AttachScene` 内部 `data.Set("scenePath", ...)`，而 `JsonValue.Set` 只允许对象。
`SerializeProperty` 对 Color / Vector* 返回的是**数组**，于是 `Set` 抛异常。

## 怎么修的

```csharp
if (result.Data is JsonValue data && data.IsObject)
{
    SceneQueryCommandsUtil.AttachScene(data);
}
```

非对象结果（数组/标量）不附场景信息——本来也没地方放。
实测 `m_Colors.normalColor` → `[1,1,1,1]`，`colors.normalColor`（省 `m_` 前缀）同样命中。

## 影响面

只有 `component.get` 单字段分支会走到这里；`field` 省略的全字段快照与 `scene.inspect` 返回的都是对象，不受影响。
