---
id: IMPR-005
type: improve
title: ParticleSystem 的模块只能读不能写——main/shape/emission 是只读属性，被 FindMember 的 CanWrite 过滤掉
status: done
source: project_island_back
created: 2026-10-23
updated: 2026-10-24
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

采纳「在 `FindMember` 失败后加一层序列化兜底」这条建议，按新的执行模型落到 `ComponentCommands.cs`：

1. `FieldOp` 增加一个 `Func<object> applyOverride`——**非空就走序列化通道**，反射一句不动。
   原有的成功路径（反射找得到成员）完全没变，只是多了一条"原来会报错"的退路。
2. `RequireFieldPath` 拆出可失败版 `TryResolveFieldPath`（返回 false + 原因，不直接抛），
   `ResolveField` 据此决定是走反射还是走兜底；兜底也失败才抛出合并后的错误。
3. 新增 `TryResolveSerializedWrite`：
   - 解析期只做「路径找得到吗 + 值转得了吗」，真正的写入包成委托推迟到 `ApplyField`，
     `setFields` 的"先全部校验、再整体写入"原子语义因此不受影响；
   - **写入时新建一个 `SerializedObject`**——多条 op 共用解析期的旧快照会互相覆盖。
4. `ParticleModuleAliases` 表把 `main` / `shape` / `emission` / `noise` 等 23 个脚本短名
   映射到 Unity 序列化里的 `InitialModule` / `ShapeModule` / `EmissionModule` …，
   所以既写得了 `main.startSize.scalar`，也写得了 `component.get` 回显的原生路径
   `InitialModule.startSize.scalar`。
5. 报错也要能指路：路径没命中且首段是已知模块名时，附上
   「`'main'` 是只读模块属性，请写到具体字段…也可直接写 Unity 序列化路径 InitialModule.…」。

**实测（project_island_back，`Area1.prefab` 的 `Area1/Rain/Particle_Rain`）**：

| 步骤 | 指令 | 结果 |
| --- | --- | --- |
| 基线 | `component.get` InitialModule.startSize.scalar | `0.05` |
| 写入 | `setFields {"main.startSize.scalar": 1.25}` | Success（此前报"没有字段或属性 'main'"） |
| 回读 | `component.get` | `1.25` |
| 还原 | `setFields {"main.startSize.scalar": 0.05}` | Success |
| 回读 | `component.get` | `0.05` |

**已知边界**：序列化通道目前支持数值 / 布尔 / 字符串 / 枚举 / 颜色 / Vector2~4。
`MinMaxCurve`、`Gradient`、`AnimationCurve`、`ObjectReference` 这类复合类型不给整体写
（会明确报错并提示"请写到具体分量"），对象引用仍需走反射通道的 `asset:` / 节点路径写法。
