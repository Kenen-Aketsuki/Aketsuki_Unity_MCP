---
id: FEAT-005
type: feature
title: 接入 ADB logcat，日志逐条打印到 Unity 控制台（默认关闭的开关）
status: done
source: Aketsuki_Unity_MCP
created: 2026-10-27
updated: 2026-10-27
files: Assets/Editor/ExternalCommandSystem/Adb/AdbLogcatService.cs, Assets/Editor/ExternalCommandSystem/Adb/AdbLocator.cs, Assets/Editor/ExternalCommandSystem/Adb/AdbLogcatCommands.cs, Assets/Editor/ExternalCommandSystem/CommandSystemSettings.cs, Assets/Editor/ExternalCommandSystem/CommandToolsWindow.cs
proposal: false
---

## 需求

> 添加一个小功能：接入 ADB 的 logcat，打印日志。这个功能需要一个开关，日志按条打印到 Unity 中。

三条要求，逐条落到设计上：

1. **接入 adb logcat** —— 从主机拉起 `adb logcat`，不是"读设备上的日志文件"（那样看不到实时输出）。
2. **需要一个开关** —— 开关就是"要不要起进程"的唯一事实来源，默认关闭，按工程保存。
3. **按条打印到 Unity** —— 一行 logcat = 一条 Unity 日志，级别映射成 `Log`/`LogWarning`/`LogError`，
   而不是攒一批打成一段。

## 实现

> **后续修订（FEAT-006）**：本文写的"一行 logcat = 一条 Unity 日志"已被改成
> **"一条日志 = 首行 + 它的堆栈续行"**（`at …` / `Caused by:` / `... N more` 并进前一条），
> 并且 `[ADB]` 日志不再附带指向 `Emit` 的 C# 堆栈。读下面关于逐行打印的描述时以 FEAT-006 为准，
> 开关 / 进程 / 队列 / `-T N` 这些机制没有变。

### 一、开关与进程的关系（`AdbLogcatService`）

`[InitializeOnLoad]` 静态类，`EditorApplication.update` 每帧做两件事：出队打印、检查子进程是否退出。
开关打开时拉起 `adb [-s serial] logcat -v threadtime [-T n]`，关掉立刻 `Kill()`。

三个入口改的是同一个 `CommandSystemSettings.AdbLogcatEnabled`（按工程隔离）：
菜单 `Tools / 指令系统 / ADB Logcat 日志`、控制台「ADB logcat」分区、指令 `adb.logcat`。
没有第二份状态，避免"开关显示开着而进程早死了"。

**域重载 / 退出编辑器时 `StopInternal(clearSetting:false)`**：只杀进程、不清开关，
重载回来 `RestoreFromSettings()` 自动接上。不清开关是因为改完 `.cs` 重编译不该把用户的抓取意图清掉；
不杀进程则不行——`.NET` 侧 `Process` 对象随域消失，子进程会变成对空管道写日志的孤儿。

### 二、读线程 → 队列 → 主线程（为什么不能直接打）

`OutputDataReceived` 跑在线程池线程，而 `Debug.Log` 只能在主线程；且 logcat 是洪流
（开机后一次性几万行很常见），在回调里直连控制台会卡死编辑器。

所以：回调只 `Enqueue`，主线程每帧最多取 **20 条**打印；队列超过 **5000 条**丢弃并计 `dropped`。
单条超 2000 字符截断。

### 三、`-T N` 而不是 `-t N` / `-d`

- `-t N` 隐式 `-d`：打印完就退，要持续输出得反复拉进程、还会重复打印；
- `-T N`：先补 N 行历史，**然后保持连接继续跟随**。

回填默认 20；填 `<=0` 时不传该参数（那会退化成"整个缓冲区倒出来"）。
老 adb 不认 `-T` 时按 stderr 里的 `unrecognized option` 判一次降级重试，只降一次，避免反复重启刷屏。

### 四、级别映射与过滤

`-v threadtime` 的行形如 `10-27 15:35:28.620  2745  2895 W BestClock: message`，用正则取 `level`/`tag`。
`W→LogWarning`、`E/F/A→LogError`、其余 `Log`，前缀统一 `[ADB] `。

过滤（tag 子串 + 最低级别）**在客户端做**，因此改完即时生效、不用重启进程；
代价是过滤条件必须缓存（`RefreshFilters()`），不能在每行上读 `EditorPrefs`——那是注册表访问。

不匹配正则的行（续行、`--------- beginning of main` 之类）不吞：前者按原始文本打成 `Log`，
后者计入 `filtered` 丢弃。

### 五、adb 定位（`AdbLocator`）

手填路径 → `ANDROID_HOME`/`ANDROID_SDK_ROOT` → Unity 的 `AndroidSdkRoot` →
**编辑器自带** `PlaybackEngines/AndroidPlayer/SDK` → `PATH`。

本机实测就是靠第 4 条命中的（Hub 装的 Unity 都带一份 platform-tools），所以多数机器不用配。
找不到时**不退回裸名 `adb`**：那样错误会变成 `Win32Exception: 系统找不到指定的文件`，
看不出是"没装"还是"没在 PATH 里"；这里返回 null 并带出"试过哪些位置"。

### 六、`adb.logcat` 指令

`action` = `start` / `stop` / `restart` / `status`，可带 `tag` / `level` / `serial` / `backfill` 覆盖。
**不是运行时指令**（`runtime:false`）：它操作主机上的 adb，不碰运行中的游戏，所以不受
「启用运行时指令」开关约束，编辑模式与 Play 模式都能用；但自己默认关闭。

日志进了 `CommandConsoleBuffer`，所以 AI 读它**不需要新接口**：

```json
{"commands":[{"id":"console.read","params":{"count":30,"contains":"[ADB]","type":"error"}}]}
```

## 实测（真机/模拟器：MuMu `127.0.0.1:7555`）

| 场景 | 结果 |
| --- | --- |
| `action=start`（serial + level=I + backfill=5） | 启动成功，adb 由"编辑器自带 AndroidPlayer SDK"命中 |
| 连续抓 ~28s | 收到 93 / 打印 86 / 过滤 7 / 丢弃 0，控制台里 `W` 是 Warning、`E` 是 Error |
| `console.read contains:"[ADB]"` | 正常读到明细，前缀与时间戳完整 |
| `action=stop` | 进程被杀，`tasklist` 里只剩 adb server 常驻进程（`fork-server server`），无 logcat 残留 |
| 序列号写错（`deadbeef:5555`） | **adb 不会退出**，挂起等设备；状态里出现 `notice: "- waiting for device -"` 并播一条警告 |
| 外部强杀 logcat 进程 | `Exited` 被捕获 → 报 `exit=-1` + stderr → **自动关掉开关**（不留"开关开着却没在抓"） |
| 重连真机（restart） | `notice` 自动清空，计数继续增长，200 条打印无异常 |

## 踩到的坑

1. **设备不在时 adb 不退出**，只往 stderr 写 `- waiting for device -`。第一版以为会立刻退出、
   靠 `Exited` 报错，结果"进程活着 + 一条日志没有"，状态显示正常。现在这行 stderr 会浮到
   `notice` 字段/状态栏，并单独播一条警告。
2. **`_exited` 必须在 `Start()` 之前复位**。设备没连时 adb 几十毫秒内就退出，
   放在 `Start()` 之后复位会把退出信号抹掉。
3. **`Exited` 回调用 runId 失效化**。主动 Stop / 重启时，上一轮迟到的 `Exited` 会把"主动停止"
   误报成"意外退出"，还会顺手把开关关掉。
4. **过滤条件不能每行读 `EditorPrefs`**——每行一次注册表访问，稳稳拖垮编辑器。
5. 新文件的 `.meta`：仓库侧的 Unity 没开时不会自动生成，`deploy.py` 会把目标侧多出来的 `.meta`
   报成孤儿。手工补了 4 个（目录 + 3 个脚本），部署即对齐。

## 未做（明确不做，别当成漏了）

- **多缓冲区**（`-b crash` / `-b main` 切换）、`--pid` 只跟单进程：都要额外的 UI，收益不抵复杂度。
- **写入文件 / 落盘归档**：Unity 控制台 + `console.read` 已够用，落盘会引入自己的滚动清理问题。
- **自动重连**：设备掉线后不自动重试，开关会被关掉，由人重新勾选。自动重连在"连错设备"时更危险。
