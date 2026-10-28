---
id: IMPR-007
type: improve
title: component.setField / setFields 不支持数组与 List 字段赋值，枚举数组只能绕过指令手改 .unity 文件
status: done
source: project_island_back
created: 2026-10-28
updated: 2026-10-29
files: ExternalCommandSystem/Commands/ComponentCommands.cs
proposal: false
---

## 症状

给一个枚举数组字段赋值，指令直接拒绝：

```json
{
  "command": "component.setFields",
  "args": {
    "target": "Canvas/SafeArea/Panel_Main/C_R/Button_Menu/RedDot",
    "component": "RedDotMultiKeyController",
    "fields": {
      "redDotKeys": ["WorkShop", "Mail", "Announcement", "Achievement"],
      "root": "Canvas/SafeArea/Panel_Main/C_R/Button_Menu/RedDot/Root",
      "type": "Dot",
      "IsAnimated": true
    }
  }
}
```

实际：

```
暂不支持给类型 RedDotKeyEnum[] 赋值（字段 redDotKeys）。该类型可写的子字段：
```

冒号后面是空的——`WritableMemberNames(RedDotKeyEnum[])` 对数组类型没有可写成员，所以连「该写什么」的提示都给不出。

同批里的 `root` / `type` / `IsAnimated` 都是能正常写的，只有数组这一个字段把整批挡下来了。

## 复现

见上（`project_island_back`，`RedDotMultiKeyController.redDotKeys` 是 `[SerializeField] RedDotKeyEnum[]`）。

期望：按 `["WorkShop","Mail",...]` 逐元素转成枚举填进数组。
实际：整条指令参数校验失败，一个字段都没写。

## 根因（已核到源码行）

`Assets/Editor/ExternalCommandSystem/Commands/ComponentCommands.cs:874-937`，`ConvertValue` 的分支只覆盖了：

`string` / `bool` / `enum` / `int` / `long` / `float` / `double` / `Vector2-4` / `Color` / 值类型结构体（`value.IsObject` 时走 `ConvertStruct`）。

数组没有任何分支，落到 `:936` 的 `throw`。注意 `:942` 的注释写着「数组 / 颜色 / 嵌套子结构体都能正确转换」，那说的是**结构体子字段**递归，不是顶层数组字段——容易被这句话误导成本来就该支持。

## 现在的绕过（脏，且会卡住整个「外部改场景」工作流）

实测在 `project_island_back` 上跑通的完整流程：

1. `component.add` 挂上新脚本 → `scene.save`
2. 用编辑器/脚本**直接改 `.unity` 文本**，写 `redDotKeys:\n  - 7\n  - 28\n ...`（`RedDotKeyEnum` 序列化成 int）
3. `asset.refresh` —— **一次不生效**，`component.get` 仍读到空数组，要连调两次才读到新值
4. `component.get` 复核 → `scene.save`

一个字段要绕 4 步、还得动磁盘文件，只要场景里有数组字段要配就必然重复这套。

## 修法建议

在 `ConvertValue` 里补 `ConvertValue` 的数组分支（位置放在枚举分支之后、结构体之前）：

- `targetType.IsArray && value.IsArray`：`elementType = targetType.GetElementType()`，逐元素递归 `ConvertValue`，`Array.CreateInstance(elementType, n)` 填回去；
- `List<T>` 同理（反射拿 `Add`，或 `IList`）；
- 元素是 `UnityEngine.Object` 引用时走已有的 `ResolveReference`；
- 长度不一致时显式报错（不要静默截断/补 null），文案里给 `期望 n 个，实际 m 个`。

这条一补，所有「数组/列表字段」都一起能配（不止枚举数组），`ConvertStruct` 里注释说的那句话也才算名副其实。

顺带：`WritableMemberNames` 对数组返回空列表时，报错文案应该改成提示「该字段是数组，请传 [a,b,c]」之类，别再吐一个空冒号。

---

## 执行结果（2026-10-29）

`ComponentCommands.cs` 的 `ConvertValue` 补了数组 / `List<T>` 分支（位置在枚举分支之后、结构体之前）：

1. `ArrayOrListElementType()`：识别一维数组与 `List<>`，返回元素类型；**多维数组返回 null**
   （一层 JSON 数组表达不了，宁可报错也不要静默填一半）。
2. `ConvertSequence()`：**新建一个**数组 / List 再整个赋回字段，逐元素递归 `ConvertValue`
   （字段名在报错里带下标，如 `redDotKeys[2]`）。不写进原实例，于是"期望 n 个、实际 m 个"
   这种静默截断在结构上不可能发生，长度天然等于入参。
3. 元素递归回 `ConvertValue`，所以**枚举数组、Vector/Color 数组、结构体数组、组件引用数组**
   （元素走 `ResolveReference`）同一条路径全覆盖——`ConvertStruct` 注释里那句
   「数组 / 颜色 / 嵌套子结构体都能正确转换」这才算名副其实。
4. **报错文案**：给数组字段传了非数组 → 「字段 X 的类型是 `Color[]`，value 请写成数组（如 [...]）；
   当前给的是 字符串 'not-an-array'」；落到兜底报错时，`UnsupportedTypeHint()` 对数组/列表
   直接点明要传数组，不再吐一个空冒号。

**真机实测**（`project_island_back`，临时节点 `__Impr007Probe`，验完已删除）：

| 用例 | 指令 | 结果 |
| --- | --- | --- |
| `List<Transform>` + 节点路径 | `setFields` 写 `hideInMaxStar: ["Island Camera","Island UI Camera"]` | 成功；`component.get` 回读就是这两个 Transform |
| 结构体数组 | `setField` 写 `color_Tag: ["#FF0000","#00FF00"]`（`Color[]`） | 成功；回读 `[[1,0,0,1],[0,1,0,1]]` |
| 传错类型 | `color_Tag` 给字符串 | 按预期报错，且是新文案 |

**验证**：真机编译通过（同上），三处部署一致。枚举数组没有单独实测（没找到现成的枚举数组字段），
但它走的是与标量枚举同一个 `ParseEnum` 递归，没有新增代码路径。

原文里那个「改 .unity 文本 + 刷新两次」的绕过流程可以作废了。
