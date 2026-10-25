---
id: BUG-005
type: bug
title: runtime.scroll 横向滚动时 amount 正负方向写反（回落/position 模式往回滚）
status: done
source: Aketsuki_Unity_MCP
created: 2026-10-26
updated: 2026-10-26
files: Commands/Runtime/RuntimeScrollCommand.cs
proposal: false
---

## 症状

`runtime.scroll` 的 `direction: "horizontal"` 下，`amount` 的**正负方向是反的**：
文档与 `SKILL.md` 写的是「正数往后翻、负数往回翻」，横向实际却是正数往回翻。

纵向不受影响。受影响的是**所有用到 `desired` 的分支**：

| 分支 | 表现 |
| --- | --- |
| `mode: "position"`（默认 drag 之外那条） | 直接把位置设反：`amount: 0.5` 从 0.5 变成 0.0，滚回开头 |
| 拖拽**未生效**时的回落（`fallback: true`） | 回落把列表滚向反方向，且 `warning` 里的「期望 / 实际」两个数是异号，读起来像引擎算错了 |
| 没有 EventSystem 时的退化路径 | 同上，往回滚 |

拖拽**生效**的那条主路径侥幸是对的：`stepVector` 单独按方向算过符号
（横向 `-x` 拖 = 显示右侧内容 = normalized 变大），只有 `desired` 这一处漏了。
所以症状是「多数时候看着能用，一旦走了回落就往回滚」，很难在一次性冒烟里发现。

## 复现

运行时场景里有一个横向 `ScrollRect`（`horizontal = true`，content 宽 2000、viewport 宽 500）：

```json
{"commands":[{"id":"runtime.scroll","params":{
  "target":"[scene:Shop]UIRoot/List","direction":"horizontal","amount":0.5,"mode":"position"}}]}
```

期望：`horizontalNormalizedPosition` 0.0 → 0.5（往右侧后续内容翻）。
实际：0.5 → 0.0（`desired = before - amount`，回退到开头）。

## 根因

`Commands/Runtime/RuntimeScrollCommand.cs:106`：

```csharp
var desired = Mathf.Clamp01(before - amount);
```

Unity 的两个 normalizedPosition **方向定义相反**：

- 纵向 `1 = 顶部 / 0 = 底部`，「往后看更多内容」= 数值**变小** → `before - amount` ✅
- 横向 `0 = 左 / 1 = 右`，「往右看更多内容」= 数值**变大** → 应该是 `before + amount` ❌

同一个表达式套了两个方向。同文件 `:170` 的 `stepVector` 是按方向分别算的
（纵向 `+y`、横向 `-x`），可见作者当时清楚这个差别，只是 `desired` 漏了一次。

## 建议改法（待实现，未动代码）

```csharp
var desired = vertical ? Mathf.Clamp01(before - amount) : Mathf.Clamp01(before + amount);
```

一行，不改参数语义、不动 `stepVector`、不改返回结构。
顺带把 `expected`（回落判定用的期望位移）自动跟着变对——它是 `desired - before`，
原来横向恒为 `-amount`，与实际位移 `+amount` 异号，判定 `ignored` 全靠绝对值比较才没误触发。

## 影响面

只有 `runtime.scroll` 的横向路径；纵向、其它 runtime 指令、返回结构均不变。
`SKILL.md` / `README.md` 的文档措辞本来就对，不用改。

## 执行结果（2026-10-26）

按建议改法落地（`Commands/Runtime/RuntimeScrollCommand.cs`），一行三元表达式，
并在上方补了注释说明「两个方向定义相反」——这是第二次有人栽在同一个符号上，
`stepVector` 当初就是分开算的，`desired` 漏了一次。

```csharp
var desired = vertical ? Mathf.Clamp01(before - amount) : Mathf.Clamp01(before + amount);
```

顺带修正的是回落判定的 `expected`（它等于 `desired - before`）：原来横向恒为 `-amount`，
与实际位移 `+amount` 异号，全靠 `Mathf.Abs` 比较才没误判成"引擎忽略了我的拖拽"。

| 验证项 | 结果 |
| --- | --- |
| 本仓库 `Assembly-CSharp-Editor`（60 文件）离线编译 | ✅ 通过（7.34s） |
| 部署 project_island_back / majotavern / CivelRev + 闭环校验 | ✅ 三处 133 文件与仓库一致，MCP 桥 3 文件一致 |
| project_island_back / majotavern / CivelRev 离线编译 | ✅ 通过（island_back 另有 4 条 YooAsset CS0122 假错，与本次无关） |
| **横向实机滚动** | ❌ 未实测 |

**为什么没实测**：唯一在监听的编辑器是 `project_island_back`，且本轮部署时它正处在
**Play 模式**（`asset.refresh` 被拒：「编辑器正处于预览状态」）——说明那边有会话在用，
往别人的工程里塞临时冒烟场景不合适。要实测得满足其一：

1. 会话空闲时在那边临时建一个横向 ScrollRect 场景跑一次（此前 `test.buildRuntimeScene`
   的做法，验完连场景带指令一起删）；
2. 或者工程里本来就有横向列表，直接指 target 跑。

没实测的部分只有"运行时行为"，改的是纯算术符号，编译与三处部署校验已过。
