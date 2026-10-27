---
id: FEAT-008
type: feature
title: ADB 调试自带设备检索与选择，Tag 过滤默认 Unity
status: done
source: Aketsuki_Unity_MCP
created: 2026-10-27
updated: 2026-10-27
files: Assets/Editor/AdbDebugger/AdbDevices.cs, Assets/Editor/AdbDebugger/AdbDebugWindow.cs, Assets/Editor/AdbDebugger/AdbDebuggerSettings.cs, Assets/Editor/AdbDebugger/AdbDebugProtocol.cs, Assets/Editor/AdbDebugger/AdbDebugHttpServer.cs, Assets/Editor/AdbDebugger/AdbLogcatService.cs, Assets/Editor/AdbDebugger/AdbBridge/adb_mcp_server.py, Assets/Editor/AdbDebugger/AdbBridge/adb_cli.py
proposal: false
---

## 需求

> 添加一个优化：ADB 调试工具自带设备检索和选择，Tag 过滤默认 Unity

两件事，都发生在**窗口层的可用性**上，抓取核心（合并、抑制、节流）没动：

1. **设备检索与选择**：原来是裸的「设备序列号」输入框，要人去 `adb devices` 里抄一串
   `2LQ0224A15000813`。抄错的症状是 `adb logcat` 一直挂着等设备、一条日志都不出——
   这正是排查起来最费时间的那类问题。
2. **Tag 过滤默认 `Unity`**：不设过滤时 logcat 把整机所有 App 的日志一起灌进控制台，
   自己的日志只占几行。

## 实现

### 一、`AdbDevices.cs`（新）

跑 `adb devices -l` 并解析，缓存结果给窗口与 HTTP 用。

| 关注点 | 做法 | 为什么 |
| --- | --- | --- |
| 线程 | `RequestScan()` 在**主线程**读 `AdbDebugPath`、解析 adb 路径，**进程在后台线程跑** | EditorPrefs 是主线程专用 API；而 `adb devices` 冷启动要 1 秒以上，放主线程会卡窗口 |
| 超时 | `WaitForExit(8000)`，超时就 `Kill` 并报「试试 adb kill-server」 | adb server 卡死时不能把编辑器一起挂住 |
| 读流顺序 | `ReadToEndAsync()` + 限时 `WaitForExit` | 同步 `ReadToEnd` 放在 `WaitForExit` 之前会死锁（管道写满互等），放在之后又读不到 |
| 解析 | 跳过 `List of devices attached`、空行、`* daemon not running *` 噪声 | adb 起 server 时这些提示会混进输出 |
| 状态带空格 | `no permissions (user in plugdev group…)` 会被截成 `no`，单独拼回去，再归一化成 `no permissions` | 不处理的话下拉里会出现一条长得没法看的假序列号 |
| 刷新 | 开窗 / 切回窗口 / 点「刷新」/ 换 adb 路径 / 抓取失败 / **列表为空时每 5 秒补扫** | 插线、启动模拟器后不用手动点；有设备时不自作主张，免得列表跟着闪 |

`AdbDevice` 带 `Usable`（只有 `device` 状态算）、`DisplayName`（`型号（序列号）`）、
`StateHint`（中文解释 + 怎么修：未授权 → 点「允许 USB 调试」；离线 → 重插或 `adb reconnect`）。

### 二、窗口：下拉 + 状态行 + 手动兜底

- 第一项固定是 `自动（adb 自己挑唯一设备）`，对应 serial 留空；
- 其余每台 `型号（序列号）`，不可用的带上状态；
- **选过但已拔掉的设备会保留在下拉里并标 `⚠ …（当前没连上）`**——否则下拉会默默跳回「自动」，
  让人以为设置没存住；
- 状态行说清「现在到底连哪台」：自动 + 只有一台可用时显示 `自动 → 型号（序列号）`；
  自动 + 多台可用时直接警告「adb 会因不唯一而失败，请显式选一台」；
- 切换即生效：正在抓取时自动重连（否则控制台里刷的还是旧设备，比不显示更容易误导）；
- 网络设备（`adb connect` 出来的）走折叠的「手动输入序列号」。

### 三、Tag 默认 `Unity`

`AdbDebuggerSettings.DefaultTag = "Unity"`，`Tag` 的 `EditorPrefs.GetString` 兜底值改它。
没改过这项的老工程（键不存在）自动变成 `Unity`；显式清空过的不受影响。
窗口里 tag 框下面加一行说明：空 = 不过滤（整机日志，量很大）。

### 四、对外接口

| 位置 | 变化 |
| --- | --- |
| `GET /v1/devices` | 新增：**现场扫一次**，返回 `devices[]`（`serial`/`state`/`model`/`usable`/`selected`/`hint`）+ `count`/`usableCount`/`auto`/`error` |
| `GET /v1/status` | 加 `serialAuto` / `deviceCount` / `usableDevices`（读缓存，不现扫——客户端轮询很密） |
| MCP 工具 | 新增 `adb_devices`；`adb_logcat` 的 `tag` 默认值改为 `Unity`，描述里写清「多设备先调 adb_devices」 |
| `adb_cli.py` | 新增 `devices` 子命令 |
| 抓取失败的报错 | 附「最近一次扫描到的设备：A、B（offline）」，不用再开窗口对序列号；同时触发一次重扫 |

拼 JSON 的逻辑放在 `AdbDevices.Json()` 而不是 HTTP 服务里：服务那层依赖 `HttpListener` 没法离线测，
`AdbDevices` 能。

## 顺带修掉的

- `AdbLogcatService.Start()` 的报错文案还写着「请在『指令控制台 → ADB Logcat』里手填」——
  那个界面在 FEAT-007 就没了，改成「Tools / ADB 调试 / ADB 调试台」。
- `AdbDebugger/README.md` 里令牌头的名字写成 `X-Command-Token`（那是指令系统的），
  本工具读的是 `X-Token`，已改。

## 验证

| 项 | 方法 | 结果 |
| --- | --- | --- |
| 解析：真实输出 | 把 `AdbDevices.cs` + `AdbDebugProtocol.cs` 编进一个 .NET 探针（`AdbLocator` 用最小替身），喂真实 `adb devices -l` | 1 台，`V2241A（emulator-5554）`，型号/序列号/transport_id 全对 |
| 解析：多设备三态 | 同探针，device + unauthorized + offline 混排 | 3 台，可用性判定与中文提示都正确 |
| 解析：`no permissions` | Linux 形态那行（带 `see [http://…]`） | 归一化成 `no permissions`，提示「配 udev / 换 USB 口」 |
| 解析：噪声 | `* daemon not running…` / 空输出 / `null` | 都是 0 台，不产生假设备 |
| 解析：无 model / recovery | `192.168.1.7:5555 device`、`abc123 recovery` | 前者退化成只显示序列号，后者判为不可用并说明「recovery 模式不能抓 logcat」 |
| 真实扫描 | 探针调 `AdbDevices.ScanNow()`（真的起 `adb` 进程） | 成功，耗时约 60ms；`RequestScan()` 异步约 60–110ms 完成，`IsScanning` 正确回落 |
| JSON | `AdbDevices.Json()` 输出交给 `System.Text.Json` 解析 | 合法；`devices` 是数组；`selected` 只标记一台；`auto`/`usableCount` 正确 |
| 桥 | 假服务端 + `adb_mcp_server.tools/list` / `tools/call` | 三个工具齐全；`adb_devices` 格式化输出正确；`adb_status` 多出「设备 自动｜可用 1/2 台」 |
| 编译 | `Tools/compile_adb.py` 本仓库 + 三目标工程 | 4 处全通过（`CivilRev.AdbDebugger.Editor`） |
| 部署 | `Tools/deploy.py --module adb` | 三工程各 31 文件 + 桥 3 文件，内容校验一致 |

## 边界

1. 设备列表是缓存：`/v1/status` 的 `deviceCount` 可能慢一拍，要准确值问 `/v1/devices`。
2. 扫描最长 8 秒（adb server 卡死时），窗口不会卡住但下拉会晚一点更新。
3. 不自动执行 `adb connect`：网络设备仍需用户先连好（或手动填序列号）。
4. Tag 默认 `Unity` 意味着系统层日志（`AndroidRuntime`、`libc`）默认看不到——这是有意的，
   要看得显式清空 tag。SKILL.md 里已写明这条升级路径。
