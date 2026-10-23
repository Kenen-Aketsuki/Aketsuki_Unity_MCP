---
id: IMPR-005
type: improve
title: ParticleSystem 的模块只能读不能写——main/shape/emission 是只读属性，被 FindMember 的 CanWrite 过滤掉
status: open
source: project_island_back
created: 2026-10-23
updated: 2026-10-23
files: Assets/Editor/ExternalCommandSystem/Commands/ComponentCommands.cs
---

## 症状

调粒子参数是 UI 打磨的常规需求（比如给新的弹窗补一层粒子），但：

- `component.get` **能读**：`field: "InitialModule.startSize.scalar"` 这类 SerializedObject 路径拿得到值
- `component.setFields` **写不了**：`field: "main.startSize"` 之类一律报「ParticleSystem 上没有字段或属性 'main'」

于是出现"看得见、改不了"的僵局，粒子参数目前只能让美术/程序手动在 Inspector 里改。

## 复现

```json
{"id":"component.setFields","params":{
  "prefab":"Assets/.../UI_Fx_Some.prefab",
  "target":"Fx_Root",
  "component":"ParticleSystem",
  "fields":{"main.startSize.scalar":24,"shape.radius":0.5}
}}
```

期望：写入 startSize / 半径，与 `component.get` 读取路径对称。

实际：

```
ParticleSystem 上没有字段或属性 'main'（解析 'main.startSize.scalar' 时）
```

`main` → `emission` → `shape` 全部一样；`gravityModifier` 这类能直接读写的普通属性不受影响。

## 根因

`ComponentCommands.cs` 的 `FindMember`（约 404–422 行）：

```csharp
var property = current.GetProperty(name, MemberFlags);
if (property != null && property.CanWrite && property.GetIndexParameters().Length == 0)
```

`ParticleSystem.main` / `.emission` / `.shape` / `.sizeOverLifetime` 都是**只读属性**
（返回各模块 struct 的拷贝，本来就不能整体赋值），被 `CanWrite` 直接过滤成"不存在"。
而 `component.get` 走的是 SerializedObject（`InitialModule.startSize.scalar` 这种属性路径），
所以读得到、写不到。

## 建议

写入路径在 `FindMember` 失败后加一层兜底：用 `SerializedObject.FindProperty(路径)` 写，
支持 `main.startSize.scalar` 这种"模块名 + 属性路径"的写法（`main` 映射到 `InitialModule`，
`shape` → `ShapeModule`，`emission` → `EmissionModule`…，或者干脆让调用方直接写序列化路径）。

如果短期不做，至少把报错改成能指路：

```
ParticleSystem 的 'main' 是只读模块属性，暂不支持写入；可用 component.get 读取（如 InitialModule.startSize.scalar）
```

## 怎么修的

（待处理）
