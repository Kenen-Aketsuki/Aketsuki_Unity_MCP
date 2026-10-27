---
name: adb-cmd
description: 抓取 Android 真机 / 模拟器的 logcat 并打进 Unity 控制台。当用户要求看设备日志、排查真机上的崩溃或异常、或需要 unity-cmd 之外独立调试 Android 运行时，使用本技能。
---

# ADB 调试（独立工具）

这是与 `unity-cmd`（Unity MCP 指令系统）**并列的另一个工具**：同一个 Unity 工程里可以两个都装，
也可以只装这一个。它们各有自己的端口、令牌、开关与信息文件，互不依赖、互不感知。

- **管什么**：Android 设备上的日志（`adb logcat`）。
- **不管什么**：编辑器里的资产/场景/Play 操作——那是 `unity-cmd` 的事。
- **读日志的出口仍是 Unity 控制台**（前缀 `[ADB] `），所以看内容要用 `unity-cmd` 的
  `console.read`；如果本工程没装 `unity-cmd`，人在「Tools / ADB 调试 / ADB 调试台」窗口和
  Console 里同样能看到。

## 两个工具

| 工具 | 默认端口 | MCP 名字 | 信息文件 |
| --- | --- | --- | --- |
| Unity MCP 指令系统 | 57831 | `unity-cmd-<工程>` | `<工程>/.workbuddy/UnityEditCommand/.http-info.*` |
| ADB 调试 | 57841 | `adb-debug-<工程>` | `<工程>/.workbuddy/AdbDebug/.adb-info.*` |

## 工具一：`adb_status`

查服务与抓取状态，不需要参数。返回端口、是否在抓、收到/打印/合并/过滤的行数、adb 路径，
以及 `notice`（典型值 `- waiting for device -`，表示设备没连上、一条日志都不会来）。

**不知道当前什么情况时先调它。**

## 工具二：`adb_logcat`

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| `action` | `start` | `start` / `stop` / `restart` / `status` |
| `tag` | 空=不过滤 | tag 子串，大小写不敏感。**抓 Unity 日志就填 `Unity`** |
| `level` | `I` | `V`/`D`/`I`/`W`/`E`/`F` |
| `serial` | 空=自动 | 多设备必须填；`adb devices` 里的那一串 |
| `backfill` | 20 | 连接时先回填多少行历史（`-T N`） |
| `stack` | 40 | 一条日志最多并入多少行堆栈；**`0` = 完全不打印调用栈** |
| `suppressStack` | true | 隐藏 `[ADB]` 日志附带的 C# 堆栈（打印期间临时改 Unity 设置，打完还原） |

典型用法：

```json
{"name": "adb_logcat", "arguments": {"action": "start", "tag": "Unity", "level": "V"}}
```

然后**用另一个工具读日志**（本工具不返回日志内容，只返回启停结果）：

```json
{"id": "console.read", "params": {"count": 50, "contains": "[ADB]"}}
```

抓完必须 `{"action": "stop"}`——logcat 是持续洪流，开着就一直在往控制台灌。

## 读日志时的两个注意点

1. **一条控制台日志可能含多行**。一次 Java 异常在 logcat 里是几十条独立记录，
   本工具会把 `at …` / `Caused by:` / `... N more` 这些堆栈续行并进前一条。
   所以「打印条数」（`printed`）远小于「收到行数」（`received`），这不是丢日志。
2. **级别已映射**：`W→warning`、`E/F→error`，所以 `console.read` 带 `type: "error"` 能直接筛错误。

## 设备不在时的表现

`adb logcat` **不会退出**，只会挂着等，并在 stderr 打 `- waiting for device -`。
这个状态会出现在 `adb_status` 的 `notice` 字段里。看到它就说明：数据线/授权/序列号有问题，
而不是"命令没生效"。

## 故障排查顺序

1. `adb_status` —— 服务在不在、端口多少、`lastError` 说了什么。
2. 服务没起来 → 让用户打开 Unity 的「Tools / ADB 调试 / ADB 调试台」，勾「启用调试服务」。
3. 服务在、抓取起不来 → 看 `lastError` 与 `notice`：多半是设备没连或序列号填错。
4. 一切正常但没日志 → 检查 `tag` / `level` 过滤，以及 `adb logcat` 是否真有输出
   （用 `adb_cli.py status` 或直接 `adb logcat -d -t 20` 对照）。
