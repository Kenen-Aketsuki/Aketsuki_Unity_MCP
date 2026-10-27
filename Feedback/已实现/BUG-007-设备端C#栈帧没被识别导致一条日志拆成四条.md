---
id: BUG-007
type: bug
title: 设备端 Unity 的 C# 栈帧不被识别，一条 Debug.Log 被打成四条
status: done
source: Aketsuki_Unity_MCP
created: 2026-10-27
updated: 2026-10-27
files: AdbDebugger/AdbLogcatService.cs
proposal: false
---

## 症状

真机上一条 `Debug.Log` 在 Unity 控制台里变成 **4 条**独立条目（用户 2026-10-27 截图）：

```
[21:39:38] [ADB] Task Guide True-3-Controller (MainIsLandController)-Normal
[21:39:38] [ADB] Proxy_TaskGuide:CheckGuide()
[21:39:38] [ADB] Proxy_TaskGuide:Update()
[21:39:38] [ADB]                      ← 空
```

期望：一条日志，展开可见调用栈。

## 根因

`IsStackTraceContinuation` 只认三种续行写法：`at …`、`Caused by:` / `Suppressed:`、`... N more`。
那是 **Java 异常栈**和**编辑器里 Unity 的栈**格式。

**Android player 的 managed 栈是另一种写法**：没有 `at ` 前缀、不带文件行号，只用冒号连接类型与方法：

```
Proxy_TaskGuide:CheckGuide()
UnityEngine.EventSystems.EventSystem:Update()
```

于是每一行都被当成"新日志"，前一条被 `FlushPending` 收尾，四行各打一条。
第 4 条那个空的 `[ADB]` 是栈尾空行：`IsStackTraceContinuation("")` 返回 false →
建了一个 `_pending = ""` 的新条目 → 超时后被 flush 成一条什么都没有的日志。

## 怎么修的

`IsStackTraceContinuation`（bool）改成 `ClassifyContinuation`（四分类），因为三种续行
"没归属时怎么办"不一样，bool 表达不了：

| 分类 | 识别 | 没有上一条时 |
| --- | --- | --- |
| `StackLine` | `at …` / `Caused by:` / `... N more` | 丢弃（孤立栈行没信息量，同旧行为） |
| `UnityFrame` | 新增正则 `Type:Method(参数)` | **当普通日志打出**（它和正文同形，丢了就是吞日志） |
| `Blank` | message 为空 / 全空白 | 一律丢弃，**不收尾上一条** |
| `None` | 其余 | 启一条新的（不变） |

新正则（`UnityStackFrame`）：

```
^[A-Za-z_][A-Za-z0-9_.`<>+]*\s*:\s*[A-Za-z_][A-Za-z0-9_<>]*\s*\([^()]*\)$
```

类型名与方法名都不许空白，所以 `Note: see foo()`、`Time: 12:30 (x)`、`Foo:Bar() failed`
这类正文不会被误吞。

**已知取舍**：用户自己打了一条恰好形如 `Foo:Bar()` 的正文、且紧跟上一条（100 ms 内，
即 `FlushIdleMs` 窗口）时，会被并进上一条。内容不丢、展开仍可见，只是少一条；
与"把每帧的栈刷成 N 条"相比这个代价可以接受。

## 执行结果（2026-10-27）

| 验证项 | 结果 |
| --- | --- |
| `Tools/compile_adb.py --project .` | ✅ 编译通过（CivilRev.AdbDebugger.Editor） |
| 正则用例（15 条，含 8 条反例） | ✅ 全过 |
| 部署 project_island_back（本轮只部署这一个） | ✅ 更新 1 文件，20 文件内容校验一致 |
| island_back 离线编译 | ✅ 通过 |
| 真机复验 | ⏳ 等用户下次抓到同类日志确认（控制台应只剩 1 条） |

部署方式：`python Tools/deploy.py --module adb project_island_back --offline-compile`。
另外两个目标（majotavern / CivelRev）本轮按用户要求**未部署**，需要时补跑同一条命令。
