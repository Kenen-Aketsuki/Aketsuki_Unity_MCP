---
id: BUG-010
type: bug
title: runtime.hierarchy 的 name 过滤模式下 limit 限制的是「遍历节点数」而非「命中数」，小 limit 必然假阴性且断言「没有该节点」
status: done
source: project_island_back
created: 2026-10-28
updated: 2026-10-29
files: ExternalCommandSystem/Commands/Runtime/RuntimeHierarchyCommand.cs
proposal: false
---

## 症状

在 `project_island_back` 的 Play 模式下，查一个**确实存在**的节点，`runtime.hierarchy` 回「没有名字含 'X' 的节点」。

```json
{"command": "runtime.hierarchy", "args": {"name": "Button_Workshop", "limit": 20}}
```

返回：

```
运行时层级里没有名字含 'Button_Workshop' 的节点（已按 limit=20 截断，需要更多请调大 limit 或加 name 过滤）
matchCount = 0, truncated = true
```

该节点当时确实在场景里：`Canvas/SafeArea/Panel_Main/MenuPanel/Panel/Button_Workshop`，随后用 `scene.hierarchy` 一次就查到了，并且 `MenuPanel` 的 runtime 点击打开后 `component.get` 也能取到它。

**更坏的是我据此下了错误结论**：当时判断成「`MenuPanel` 未激活所以整棵子树被跳过」，并写进了项目日志。实际 `includeInactive` 默认就是 `true`（见 `RuntimeHierarchyCommand.cs:46,55`），未激活子树本来就会遍历。真实原因是 limit 把遍历提前掐掉了。这属于「工具给出了错误的因果线索」，比单纯查不到更容易误导。

## 复现

```json
{"command": "runtime.hierarchy", "args": {"name": "Button_Workshop", "limit": 20}}
```

期望：返回该节点（含 `qualifiedPath`，可回喂 `runtime.click`）。
实际：`matchCount=0`。

同一次会话里把 `limit` 调大后再查别的名字是能命中的，说明不是节点不存在，是预算用完了。另一次 `{"name": "RedDot", "limit": 400}` 返回了 **73 万字符**（400 个命中节点把结果撑爆，工具结果被外置到文件才没把会话顶掉）。

## 根因（已核到源码行）

`Assets/Editor/ExternalCommandSystem/Commands/Runtime/RuntimeHierarchyCommand.cs:169-192`：

```csharp
private void WalkMatches(Transform node, string nameFilter, SceneGroup group, JsonValue matches)
{
    if (node == null || _visited >= _limit)
    {
        _truncated = _truncated || node != null;
        return;
    }
    _visited++;
    ...
}
```

`_visited` 是**走过的节点数**，`_limit` 砍的是它，不是 `_matched`。UI 工程的运行时层级动辄几千节点，`limit=20` 等于「只看前 20 个节点」，命中区在后面就必然 0。

配套的提示语（`:161`）也把结论写死了：

```csharp
return CommandResult.Ok($"运行时层级里没有名字含 '{nameFilter}' 的节点{TruncatedHint()}", data);
```

断言「没有」，括号里才补「已截断」。人读到第一句就不往下看了。

顺带一个性能细节：`return` 后上层 `for` 循环仍会对每个兄弟节点再调一次 `WalkMatches`，每次立刻返回，纯浪费。

## 修法建议（倾向 A）

**A. name 模式把 limit 重新解释为「命中上限」**：遍历不限，`_matched >= _limit` 时停下。
理由：`name` 过滤的使用场景就是「找东西」，人关心的是找到几个，不是走了几个。遍历中止用 `_truncated`/`_matched` 表达，message 里区分「命中达到上限」与「遍历未走完」，不要再用「没有…的节点」这种断言。

**B. 保持遍历预算语义，只改文案**：`在前 N 个节点里没有找到…，可能被截断（truncated=true），建议调大 limit 或加 root 收窄范围`。
改动最小，但「小 limit 查不到」这个坑还在，只是不再误导。

**顺带**：`limit=400` 默认值对 UI 工程偏大（一次 730 KB），如果 A 方案实施，可以顺带给节点 JSON 加个 `compact` 选项或默认砍掉 `components` 数组，否则「命中 400 个」照样撑爆响应体。

---

## 执行结果（2026-10-29）

**按 A 方案实现**，`RuntimeHierarchyCommand.cs`：

1. **命中模式的 `limit` 改为「命中上限」**：`WalkMatches` 的收手条件从 `_visited >= _limit` 换成
   `_matched >= _limit`，遍历本身不再限量（树模式保持原样——那里要防的是响应体爆炸）。
2. **顺手修掉空转**：命中满了之后立刻整体收手，不再对每个兄弟节点各调一次再空转返回
   （原文里提到的性能细节）。根节点循环同样加了提前 break。
3. **不再断言「没有」**：`matchCount=0` 时的 message 改成
   「没有名字含 'X' 的节点（**已遍历 N 个节点，未截断**）」——遍历不限量，所以这回是真的没有，
   并把覆盖规模报出来；命中满了才说「命中已达 limit=N，可能还有未列出的节点；调大 limit，
   或用 root / scene 收窄范围」。
4. **data 补两个字段**：`visited`（实际遍历节点数）与 `limitAppliesTo`（`nodes` / `matches`），
   调用方不用猜 limit 量的是哪个。

**验证**：`project_island_back` 真机编译通过（`send.py compile --force`，0 error / 0 warning，
`Assembly-CSharp-Editor.dll` 于 00:07 重建，字节里能查到新字符串 `limitAppliesTo` / `命中已达 limit=`），
已部署 island_back / majotavern / CivelRev 三处并校验一致。

**未做真机复验**：`runtime.hierarchy` 要进 Play 模式，而 `runtime.*` 默认双门禁关闭、
`editor.play` 又带人工确认（FEAT-014），无人值守的自动化跑不了。已记进 `HANDOVER.md` §8
（「已改未实测」）与本轮 `待拍板-2026-10-28.md`。

**顺带项没做**（要扩能力，先记账）：`compact` 选项 / 默认砍 `components`，见本轮待拍板 IMPR-018。
