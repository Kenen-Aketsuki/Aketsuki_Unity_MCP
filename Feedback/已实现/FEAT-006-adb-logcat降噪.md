---
id: FEAT-006
type: feature
title: adb logcat 降噪——堆栈续行合并成一条 + 隐藏指向 Emit 的 C# 堆栈
status: done
source: Aketsuki_Unity_MCP
created: 2026-10-27
updated: 2026-10-27
files: Assets/Editor/ExternalCommandSystem/Adb/AdbLogcatService.cs, Assets/Editor/ExternalCommandSystem/Adb/AdbLogcatCommands.cs, Assets/Editor/ExternalCommandSystem/CommandSystemSettings.cs, Assets/Editor/ExternalCommandSystem/CommandToolsWindow.cs
proposal: false
---

## 需求

> 目前工具打的 Log 有较大噪声，Log 内容和调用栈都会打印出来。要么想办法把调用栈压缩到 Log 中打一起，
> 要么不打调用栈。最好实现前者。

两个方案，按用户指定的优先级做**前者**（合并），并把后者做成参数退路（`stack=0`）。

## 噪声的两个来源（这是两件事，不是一件）

FEAT-005 的"一行 logcat = 一条 Unity 日志"在真机上被放大成两处噪声，**都指向同一条原始日志**：

1. **logcat 自己把一次异常拆成几十行**。Java 堆栈每行都带完整前缀（`10-27 … E AndroidRuntime: \tat com.x.Y.z(...)`），
   每行都是独立记录 → 一次崩溃变成几十条控制台条目。
2. **每条 `Debug.Log` 又各带一段 C# 堆栈**，而且每条都指向 `AdbLogcatService.Emit` 同一个位置。
   零信息量，乘以几十条，噪声被平方放大。

所以只做一件不够：只合并多行，每条仍带 C# 堆栈；只压 C# 堆栈，一次崩溃仍是几十条。

## 实现

### 一、堆栈续行合并（`IsStackTraceContinuation` / `AppendContinuation` / `FlushPending`）

`Emit` 从"解析即打印"改成"解析 → 归类 → 攒够再打"：

- 普通行：先把上一条 flush 出去，自己成为新的 `_pending`（仍走原来的 tag / 级别过滤）。
- 续行：并进 `_pending`，用 `\n` 连接。**续行不做过滤**，跟着首行走——否则一次异常会被滤掉半截，剩下的堆栈反而看不懂。
- 收尾靠两件事：下一条普通行到来，或静默超过 `FlushIdleMs`（100ms）。
  纯靠前者不行：以堆栈结尾的日志后面可能再没有新行，会永远挂在 `_pending` 里不出现。

**判定必须看 message 部分，不能看整行。** 这是这次最容易踩空的一点：logcat 里 Java 与 Unity 的堆栈
都是"一行一条"、每行照样带时间戳和 tag，拿整行判 `StartsWith("\tat")` 永远不成立。
识别四类：`at …`（Java 与 Unity C# 都是这个形状）、`Caused by:`、`Suppressed:`、`... N more`。

上限两道：行数（`stack`，默认 40）与字符数（`MaxMessageChars` 2000，沿用）。撞到任一就停止并入，
末尾补 `…（堆栈已截断，本条最多合并 N 行）`。

`FATAL EXCEPTION: …` 与 `Process: …` 不并入——它们是崩溃元信息，各占一行更好认，
硬并进去属于特化 logcat 格式，收益一条、风险误判。

### 二、隐藏 C# 堆栈（`BeginStackTraceSuppression` / `ReleaseStackTraceSuppression`）

`Application.SetStackTraceLogType(LogType, None)` 是**全局**设置，没有 per-call 版本。做法：

- 打印期间切到 `None`，打完按窗口还原。
- **不是每条切成对切**：这个 API 会落到编辑器偏好（Windows 上是注册表），真机洪流下每帧几百次写盘不可接受。
  改成 500ms **抑制窗口**——窗口内直接放过，到期才恢复。稳态每秒最多两次切换。
- **停止 / 退出 / 域重载前必须立刻还原**（`StopInternal` 与 `HandleExit` 里都调 `RestoreStackTraceNow`）。
  静态字段随域消失，但编辑器偏好留着 `None`，不还原就等于永久改掉用户全局的堆栈显示。
  这是本次改动唯一有"污染用户环境"风险的地方，红线守住。

副作用（已写进 README）：抑制窗口内用户自己脚本的 `Debug.Log` 也没堆栈。所以留了开关
`AdbSuppressStack`（默认开），关掉就完全按 Unity 全局设置走。

### 三、顺手修掉一个吞吐问题

`Drain` 原来按"每帧 20 行输入"限量。合并之后 40 行堆栈只产出 1 条日志，20 行的预算等于只能产 0.5 条，
真机洪流下队列永远追不上、成片丢日志。改成两道闸：**输出 20 条 / 扫描 200 行**，各管一件事。

## 验证

真机（华为 ALN-AL00 / Android 12）已在改前实测 `logcat -v threadtime` 可读（3 秒 236 行，≈78 行/秒），
格式与 `ThreadTimeLine` 正则完全吻合。改动后的**分类与合并规则**用离线脚本逐条核对（临时脚本，验完删除）：

| 场景 | 输入 | 结果 |
| --- | --- | --- |
| 真实系统日志（抓回来的 8 行） | 8 行 | 输出 8 条、合并 0 行——**正常日志一条都没被误并**（这是最要紧的断言） |
| Java 崩溃块 | 9 行 | 3 条：`FATAL EXCEPTION` 1 条、`Process:` 1 条、异常体并入 6 行堆栈 1 条 |
| Unity 崩溃块 | 6 行 | 3 条：异常 + 2 行 at、`Stack trace:` + 1 行 at、后续普通日志 |
| `stack=0` | 9 行 | 6 行续行全丢、3 条普通行保留（= 用户要的"不打调用栈"退路） |
| 超长堆栈（61 行） | 61 行 | 1 条，截到 40 行 + `truncated` 标记 |
| 孤立续行（没有首行） | 2 行 | 0 条、丢弃 2 行——不凭空造日志 |

`Assembly-CSharp-Editor` 离线编译通过；三目标工程已部署（各 140 文件 + 桥 3 文件内容一致，离线编译均通过）。

## 边界

- **没有在 Unity 里真跑过**：验证设备抓到一半掉线（`adb devices` 变空），控制台实际观感待用户在编辑器里确认。
  代码路径与 FEAT-005 相同，差别只在出队之后的整形。
- 抑制窗口内用户脚本日志无堆栈（500ms，可关）。
- 合并按"连续续行"归并，不判断 pid/tag 是否与首行一致。跨进程交错的极端情况下理论上会串行归并，
  实际 logcat 输出是逐行原子写、交错的概率远低于噪声本身，暂不为它加复杂度。
