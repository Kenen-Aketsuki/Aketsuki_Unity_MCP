---
id: FEAT-012
type: feature
title: 控制台日志主内容加粗，调用栈保持原样
status: done
source: Aketsuki_Unity_MCP
created: 2026-10-27
updated: 2026-10-27
files: AdbDebugger/AdbLogcatService.cs, AdbDebugger/README.md, McpBridge/SKILL.md
proposal: false
---

## 需求

用户 2026-10-27：*「用富文本标签加粗每个 log 的主内容，调用栈内容维持原状」*。

合并之后每条日志是「正文 + N 行栈」，在控制台里一整块同样粗细，扫一眼分不出哪行是结论。

## 怎么做的

`FlushPending` 里加一步 `BoldFirstLine`：只把**第一行**包进 `<b>…</b>`，后面所有行原样。

```csharp
var log = Prefix + BoldFirstLine(text);   // 给控制台
// Capture(text, …) 在之前调用，存的是纯文本
```

关键在于**标签只加在控制台那一份上**：`Capture()` 早就把合并后的原文存进环形缓冲，
所以 `adb.logcat.read` 拿到的仍然是干净文本，AI 不必剥标签。

不转义的取舍：Unity 富文本不认 HTML 实体（`&lt;` 会原样显示成 4 个字符），所以正文里自带的
`<color=…>` 之类本来就是会被渲染的既存行为，加不加 `<b>` 都一样。唯一新增风险是正文里出现
`</b>` 会让加粗提前闭合——视觉问题，不丢内容。

## 执行结果（2026-10-27）

| 验证项 | 结果 |
| --- | --- |
| `Tools/compile_adb.py --project .` | ✅ 通过 |
| 部署 project_island_back | ✅ AdbLogcatService.cs + README.md，20 文件校验一致 |
| island_back 真实重编译（`asset.refresh` 触发） | ✅ ok，0 错误 0 警告，1493 ms |
| SKILL.md 同步到 island_back 技能目录 | ✅ 3 文件校验一致 |

`McpBridge/SKILL.md` 补了一条纪律：`console.read` 读到的主内容带 `<b>`、`adb.logcat.read` 是纯文本，
要程序化解析用后者；`contains` 按正文过滤两边都命中。

## 备注

`<b>` 在 Unity 控制台的渲染由 `GUIStyle.richText` 决定，本工程实测生效（用户下次抓日志即可确认观感）。
若某天发现显示成字面 `<b>`，说明该 Unity 版本控制了富文本，把 `BoldFirstLine` 改成直通即可，
不影响合并逻辑。
