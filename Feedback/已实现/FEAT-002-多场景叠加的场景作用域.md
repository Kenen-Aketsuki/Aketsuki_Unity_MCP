---
id: FEAT-002
type: feature
title: 多场景叠加下的场景作用域：target 支持 [scene:X]，同名多命中一律报错并列出可回喂候选
status: done
source: Aketsuki_Unity_MCP
created: 2026-10-24
updated: 2026-10-24
files: Core/SceneScope.cs, Core/SceneQuery.cs, Commands/Runtime/RuntimeSupport.cs, Commands/Runtime/RuntimeHierarchyCommand.cs, Commands/Runtime/RuntimeClickCommand.cs, Commands/Runtime/RuntimeScrollCommand.cs, Commands/Runtime/RuntimeInputCommand.cs
proposal: false
---

## 需求

> 项目有时候会同时打开多个场景进行叠加，运行时需要区分这种情况，否则会出现大量冲突。

叠加加载下「常驻引导场景 + 关卡场景 + UI 场景」各有一份 `UIRoot` / `Canvas` / `BtnStart`，
而 `SceneQuery` 是**跨所有已加载场景**遍历的，只给路径时按「激活优先、层级浅优先」挑一个——
看起来成功，实际点/改的是另一个场景那份。这种错从返回值里看不出来。

## 实现

### 一、作用域写在 target 里（两种等价写法）

| 形式 | 例子 |
| --- | --- |
| 字符串前缀 | `[scene:Level1]UIRoot/BtnStart` |
| 对象字段 | `{"scene":"Level1","path":"UIRoot/BtnStart"}` |

前缀写成 `[scene:...]` 而不是裸 `[Level1]`：对象池里 `[Pool]Item` 这种命名很常见，
裸括号会把它误判成场景名。

选择器：场景名 / `Level1#1`（同名场景第 N 份）/ `handle:-5270` / `path:Assets/...`（也认后缀与文件名）/
`active` / `DontDestroyOnLoad`（常驻对象所在的合成场景，不在 `SceneManager` 列表里，只能从根节点反查）。

### 二、运行时一律严格定位（不给"猜一个"的机会）

`RuntimeSupport.ResolveTarget`：0 个命中 → 报错并附已加载场景清单；**多个命中 → 报错并列出候选**，
每条带 `qualifiedPath` / `instanceId` / `activeInHierarchy`，同时把 `candidates` 写进 `data`：

```
'UIRoot/BtnStart' 命中 2 个节点，无法确定点哪个——多场景叠加 / 同名兄弟都会这样，必须显式消歧。
把下面任一条原样作为 target 重试：
  [scene:SceneA]UIRoot/BtnStart（activeInHierarchy=True, instanceId=-5346）
  [scene:SceneB]UIRoot/BtnStart（activeInHierarchy=True, instanceId=-5544）
```

编辑指令（`gameobject.setProperty` 等走 `TryResolveUnique` 的那批）也顺带换成了同一套候选清单文案。

### 三、runtime.hierarchy 按场景分组

- 顺序取 `SceneManager` 的加载序（不再用 `FindObjectsOfType` 的返回序，那个在运行时不保证稳定）；
  `DontDestroyOnLoad` 单独成组排最后。
- 每组带 `label` / `selector` / `handle` / `buildIndex` / `active` / `dontDestroyOnLoad` / `rootCount`；
  两个不同资产的场景重名时，**每个**都带序号（`C2#0`、`C2#1`）——只给后几个加序号的话，
  第一个的 label `C2` 作为选择器会命中 2 个场景、直接报歧义，等于不可用（实测后改的）。
- 每个节点带 `qualifiedPath`（`[scene:Level1]UIRoot/BtnStart`），可直接回喂。
- 新增 `scene` 参数只看某个场景。

### 四、qualifiedPath 怎么保证唯一

同名兄弟补 `#序号`；同一场景里同名根节点无法用序号表达时，直接退回 `id:<instanceId>`
（宁可少点可读性也要保证精确）。场景名里带 `]` 时选择器退回 `handle:N`。

### 五、顺带修掉的两个老问题

1. `SceneQuery.FindAll(JsonValue)` 的纯路径分支原来只返回 `Find` 的单个最优结果 →
   `gameobject.find all:true` 与歧义检测对路径完全失效（多场景下"明明两处都有只报一个"）。
2. `List.Sort` 是不稳定排序，同深度同名节点比较结果为 0 → 候选清单两次调用顺序会翻面
   （实测 SceneA/SceneB 互换）。现在补了「场景加载序 → 同级下标 → instanceId」的稳定兜底。

## 实测（本仓库编辑器 2021.3.16f1c1，临时搭 SceneA + SceneB 叠加、节点全同名）

| 检查 | 结果 |
| --- | --- |
| `runtime.hierarchy` | 3 组（SceneA / SceneB / DontDestroyOnLoad），label、selector、handle、buildIndex、active 全对 |
| `runtime.hierarchy scene=SceneB` / `handle:-5518` / `path:.../SceneB.unity` / `SceneA#0` | 均只返回目标场景 |
| `scene=Nope` | 失败并列出已加载场景（含各自的 selector / handle） |
| 无作用域点 `UIRoot/BtnStart` | 失败，候选按稳定序给出 `[scene:SceneA]…`、`[scene:SceneB]…` |
| `[scene:SceneB]UIRoot/BtnStart` | 命中 SceneB（`data.sceneScope.matched` 可核对），零 warning |
| `[scene:active]UIRoot/BtnStart` | 命中 SceneA，并给出 warning：坐标命中的 `Label` 不属于目标分支（B 的同屏 UI 盖在 A 上面）——`position` 模式在多场景重叠下确实会点错，实测踩到 |
| 对象形式 `{"scene":"SceneB","path":"UIRoot/BtnStart"}` + `direct:true` | 命中 SceneB |
| `runtime.input` 分别打 A / B | A 的输入框保持 `A-marker`，B 变 `hitB`；不给作用域则报 2 个候选 |
| `runtime.scroll` 分别打 A / B | 各自 `1.000→0.500` / `1.000→0.750`，互不影响 |
| 常驻对象 | `runtime.hierarchy` 归到 `DontDestroyOnLoad` 组（handle=-12, buildIndex=-1），`[scene:DontDestroyOnLoad]GlobalRoot/GlobalService` 可解析 |
| 同名场景叠加（C2 / Sub/C2） | 分组为 `C2#0` / `C2#1`；`[scene:C2#1]UIRoot` 精确命中；只写 `C2` 则失败并列出两个候选 |
| 编辑指令带前缀 | `gameobject.setProperty target="[scene:SceneA]UIRoot"` 成功，脏标记只落在 SceneA |

临时场景与临时指令已全部删除（`Assets/__RuntimeTest` 与 `__TempMultiSceneSmoke.cs`）。

## 已知边界

- `runtime.click` 的 `position` 模式没有场景概念：同屏重叠时命中由渲染顺序决定，返回的 `warning`
  会指出"命中的不在目标分支上"。要精确定位必须用 `target`。
- `handle` 来自 Unity 的 `Scene.handle`，同一次会话内稳定，重启编辑器会变；脚本里尽量用场景名。
- `SceneScope` 的作用域只是"过滤根节点"，不改变 `Find` 的激活优先语义（编辑指令的旧行为保留）。
