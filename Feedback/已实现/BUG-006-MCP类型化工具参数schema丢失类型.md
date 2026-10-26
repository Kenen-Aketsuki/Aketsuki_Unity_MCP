---
id: BUG-006
type: bug
title: MCP 桥生成的 typed 工具普遍丢参数类型（类型名后的中文括注让整体失配）
status: done
source: Aketsuki_Unity_MCP
created: 2026-10-26
updated: 2026-10-26
files: McpBridge/unity_mcp_server.py, Commands/UiCommands.cs, Commands/TmpTextCommand.cs, Commands/GameObjectCommands.cs, Commands/Runtime/Runtime*.cs
proposal: false
---

## 症状

MCP 客户端（WorkBuddy / Claude Desktop / Cursor）拿到的 `unity_*` typed 工具里，
**大量参数的 inputSchema 没有 `type`**，只剩一句 description。模型因此只能靠猜传参：

```json
"target":  {"description": "string（Prefab 模式下空串=根）"}   ← 本该是 {"type":"string"}
"name":    {"description": "任意 JSON 值"}                     ← ui.image 全部参数都这样
"layer":   {"description": "int|string"}
```

实测（project_island_back，65 个工具）：修之前 5 条 `ui.*` 指令的 **全部** 参数、以及
`component.setField(s)` / `component.add` / `prefab.tree` 等的 `target` 都没有类型。

## 复现

```bash
printf '%s\n%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{}}}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' | python unity_mcp_server.py
```

期望：`unity_component_setFields` 的 `target` 是 `{"type":"string"}`。
实际：只有 `{"description":"string（Prefab 模式下空串=根）"}`。

## 根因

两处独立成因：

1. **桥的类型匹配太整段**（`_type_to_schema`）：拿整段 `type_text` 去比对 `"string"`，
   而 usage 里类型名后面常挂中文括注（`string（Prefab 模式下空串=根）`）或多候选
   （`int|string`），一挂就整体失配 → 退化成"未知类型"。
2. **usage 本身没写类型**（`CommandAttribute.Usage`）：`ui.image` / `ui.node` / `ui.text` /
   `ui.button` / `ui.tmpText` 五条写成 `Usage = "name, parent, sprite, ..."`，
   一个冒号都没有；`gameobject.instantiate` 的 `scale` 同样裸着。
   usage 是 typed 工具 schema 的**唯一来源**（单一来源原则），源头没有就无处可补。

## 怎么修的

**桥侧**（`McpBridge/unity_mcp_server.py`）：新增 `_split_type_token()`，只取开头的
ASCII 标识符当类型名，其余（中文括注 / `|候选`）原样保留成 description：

```python
name, rest = _split_type_token(text)      # "string（…）" → ("string", "（…）")
schema_type = _TYPE_ALIASES.get(name.lower())
```

已知类型照旧给 `type`，`[w,h]` 这类仍优先判成 array，未知类型（枚举候选、
`target`、`any`）保持"不限类型 + 原文描述"。

**C# 侧**（单一来源）：给 5 条 `ui.*` 补上类型，并把 runtime 四条里语义不明的
`target: target` 改成 `target: string|object`（它确实既可传路径串也可传
`{"scene":…,"path":…}` 对象）。顺带把此前没写进 usage 但代码支持的
`preserveAspect` / `raycastTarget` / `select` 也列了出来——它们本来就在 XML 注释里，
只是没进 usage，MCP 侧因此看不见。

## 执行结果（2026-10-26）

| 验证项 | 结果 |
| --- | --- |
| 本仓库 `Assembly-CSharp-Editor`（60 文件）离线编译 | ✅ 通过 |
| 部署 project_island_back / majotavern / CivelRev + 闭环校验 | ✅ 三处 133 文件与仓库一致，MCP 桥 3 文件一致 |
| 三处目标工程离线编译 | ✅ 通过 |
| **真机验证**（project_island_back 57833 实编译后拉 tools/list） | ✅ 65 个工具，无类型参数从 "一片" 降到 **8 个** |

剩下 8 个是**本来就不该有单一类型**的：`info|warn|error`、`abort|save`、
`vertical|horizontal`、`left|right|middle`、`any`（任意 JSON 值）。

真机验证做了什么：`asset.compile` 触发 island_back 真实重编译（0 警告），
再用 stdio 桥跑 `initialize` + `tools/list`，逐项统计缺 `type` 的参数。
期间只跑了只读指令（`editor.state` / `scene.hierarchy` / `component.get` /
`gameobject.find` / `console.read`），临时探查 JSON 用完已删，**没改那个工程的任何资产**。

## 备注

`runtime.*` 的横向滚动（BUG-005）本轮**仍未实机实测**：要测得在那边临时建
ScrollRect 场景并进 Play 模式，而进 Play 会真的跑起那个工程的游戏逻辑（联网、
写存档都有可能），无人值守时不该做。等有会话在场时再说。
