# ADB 调试（独立工具）

把 Android 真机 / 模拟器的 `adb logcat` 逐条打进 Unity 控制台。**这是一个独立工具**，
与本工程的「Unity MCP 指令系统」（`Assets/Editor/ExternalCommandSystem/`）是并列关系：

- 各有自己的端口、令牌、信息文件、设置与开关；
- **本工具不引用指令系统的任何类型**（有自己的 asmdef `CivilRev.AdbDebugger.Editor`）；
- 可以只装这一个，也可以两个都装。

## 本体构成

| 文件 | 职责 |
| --- | --- |
| `AdbDebuggerSettings.cs` | 全部设置（按工程隔离，`adb.exe` 路径全局）；独立的 EditorPrefs 前缀 `CivilRev.AdbDebug.` |
| `AdbDebugHttpServer.cs` | 本工具的 HTTP 服务：端口 57841 起、端口避让、令牌、Host 校验、主线程泵、信息文件 |
| `AdbDebugProtocol.cs` | 协议层：query string 解析 + 手写 JSON 输出 + 日志出口（不到 200 行，零解析依赖） |
| `AdbLocator.cs` | 五级定位 `adb.exe`：手填 → `ANDROID_HOME`/`ANDROID_SDK_ROOT` → Unity 的 `AndroidSdkRoot` → 编辑器自带 SDK → `PATH` |
| `AdbDevices.cs` | 设备检索：跑 `adb devices -l`、解析、缓存（后台线程），给窗口下拉和 `/v1/devices` 用 |
| `AdbLogcatService.cs` | 抓取主体：进程生命周期、读线程→队列→主线程、堆栈续行合并、C# 堆栈抑制 |
| `AdbDebugWindow.cs` | 独立窗口与菜单（`Tools / ADB 调试 /`），含设备下拉与实时刷新 |
| `AdbDebugSetup.cs` | 调 `install_bridge.py` 完成 MCP 注册（C# 不碰 `mcp.json`） |
| `AdbDebugAutoStart.cs` | 编辑器启动时按设置拉起 HTTP 服务，并预热一次设备列表 |

## 怎么用

1. **看日志（人）**：`Tools / ADB 调试 / ADB 调试台` —— 设备下拉里选一台（通常只有一台，留「自动」即可），
   勾「启用 logcat」，日志以 `[ADB] ` 前缀进 Console。
2. **让 AI 用**：`Tools / ADB 调试 / 安装 MCP 桥（写入 mcp.json）`，然后在连接器管理页对
   `adb-debug-<工程>` 点信任。AI 侧有三个工具：`adb_status`、`adb_devices`、`adb_logcat`。

## 设备检索与选择

窗口里的「设备」是一个**下拉**，选项来自 `adb devices -l` 的实时结果，而不是让人手抄序列号：

- 第一项永远是 `自动（adb 自己挑唯一设备）`——只插一台时选它就行，序列号留空；
- 其余每台显示 `型号（序列号）`；状态不是 `device` 的会带上状态（`unauthorized` / `offline` / `recovery`…），
  并在下面用黄框写清怎么修（例如「在设备屏幕上点『允许 USB 调试』」）；
- 已经选过但拔掉的设备会**保留在下拉里**并标 `⚠ …（当前没连上）`——否则下拉会默默跳回「自动」，
  让人以为设置没存住；
- 选完立刻生效：正在抓取时自动重连，没在抓取就等下次点「重新连接」；
- 刷新时机：开窗 / 切回窗口 / 点「刷新」/ 换 adb 路径 / 抓取失败 / **列表为空时每 5 秒补扫一次**
  （插线、启动模拟器后不用手动点）。扫描在后台线程跑，不卡编辑器；
- 网络设备（`adb connect 192.168.x.x:5555`）如果还没出现在列表里，展开「手动输入序列号」直接填。

扫描结果缓存在内存里，`/v1/status` 只读缓存（客户端轮询很密，不能每次跑 adb），
`/v1/devices` 才现场扫一次。

## Tag 过滤默认 `Unity`

`Debug.Log` 在 Android 上走 Unity 自己的日志通道，tag 恒为 `Unity`；不设过滤时 logcat 会把整机
所有 App 的日志一起灌进来。所以**默认 tag = `Unity`**，只看自己的日志。

要看全部（比如排查系统层的崩溃、`AndroidRuntime`）就把 tag 框清空；命令行的 `tag` 参数传空字符串同理。
机器级的系统日志（`FATAL EXCEPTION`、`lowmemorykiller` 之类）通常在别的 tag 下，被默认值滤掉是预期行为——
真需要时清空再抓一次。

## 和其它工具的关系

| | Unity MCP 指令系统 | ADB 调试 |
| --- | --- | --- |
| 默认端口 | 57831 | 57841 |
| MCP 名字 | `unity-cmd-<工程>` | `adb-debug-<工程>` |
| 信息文件 | `.workbuddy/UnityEditCommand/.http-info.*` | `.workbuddy/AdbDebug/.adb-info.*` |
| 技能目录 | `.workbuddy/skills/unity-cmd/` | `.workbuddy/skills/adb-cmd/` |
| 读日志 | `console.read` | **不读日志**，只启停；内容在 Unity 控制台里 |

最后一行是有意为之：日志的出口是 Unity 控制台，本工具只负责把设备日志**弄进控制台**。
没装指令系统时，人在 Console 窗口里照样看得到，只是 AI 读不到——需要 AI 读就两个都装。

## 安装到别的工程

两条路，都**不需要**指令系统在场：

```bash
# ① 从本仓库部署（开发用，会把桥也同步过去）
python Tools/deploy.py --module adb <目标工程> --verify

# ② 分发包（给别人）
python Tools/build_unitypackage.py --module adb --bundle --version <版本>
#   → Tools/UnityPackage/Aketsuki_AdbDebugger-<版本>/（包 + install.py + DEPLOY.md + manifest.json）
python Tools/install_unitypackage.py --module adb --project <目标工程>
```

包内 `package.json` 记着 `installRoot` / `skillName` / `skillFiles`，安装器据此决定落点——
两个工具的包共用同一个安装器，同目录存在两个包时用 `--module` 或 `-p` 指定。

## HTTP 接口

只监听 `127.0.0.1`，带令牌（`?token=` 或 `X-Token` 头），端口与令牌在
`<工程>/.workbuddy/AdbDebug/.adb-info.<工程hash>` 里（编辑器启动时自动重写）。

| 路径 | 说明 |
| --- | --- |
| `GET /v1/status` | 服务与抓取状态：`running` / `logcatRunning` / `logcatState` / `adb` / `adbSource` / `serial` / `serialAuto` / `deviceCount` / `usableDevices` / `received` / `printed` / `coalesced` / `filtered` / `dropped` / `lastError` / `notice` |
| `GET /v1/devices` | **现场扫一次**设备：`devices[]`（`serial` / `state` / `model` / `usable` / `selected` / `hint`）、`count` / `usableCount` / `auto` / `error` |
| `POST /v1/logcat` | `action=start\|stop\|restart\|status`，可带 `tag` / `level` / `serial` / `backfill` / `stack` / `suppressStack` |

`adb_cli.py` 是它的命令行壳（`status` / `devices` / `logcat`）；`adb_mcp_server.py` 把它包成 MCP 的三个工具。

## 可单独删除

删掉 `Assets/Editor/AdbDebugger/` 整个目录（连同 `.meta`）即可，指令系统完全不受影响；
反过来删指令系统，本工具也照常工作。两者之间唯一的"耦合"是：

- 都往同一个 Unity 控制台写日志（`[ADB] ` vs 指令系统自己的前缀）；
- 本工具的日志内容要靠指令系统的 `console.read` 才能被 AI 读到（不装则人看）。

## 已知边界

1. 一次只跟一个设备（`-s` 单序列号）。多设备并存时 `adb` 会因"more than one device"直接失败，
   所以要么在下拉里显式选一台，要么留「自动」但只插一台。窗口的状态行会直接提示这种情况。
2. 设备不在时 `adb logcat` **不退出**、只挂着等，打 `- waiting for device -`；该状态会出现在
   `adb_status` 的 `notice` 字段里。抓取进程退出时，报错里会附上"最近一次扫描到的设备"，方便对序列号。
3. `suppressStack`（默认开）会在打印期间临时改 Unity 的堆栈设置（500ms 抑制窗口），
   停止 / 退出 / 域重载立刻还原。副作用是窗口内你自己脚本的 `Debug.Log` 也没堆栈。
4. 堆栈合并的上限是「行数 + 2000 字符」，撞到就截断并注明。`stack=0` 等于完全不打印调用栈。
5. 设备列表是**缓存**：`/v1/status` 里的 `deviceCount` 来自上一次扫描。刚插上线还没扫过时可能是 0，
   要准确值就问 `/v1/devices`（它会现场扫）。
6. 扫描有 8 秒上限：`adb server` 卡死时不会把编辑器一起挂住，窗口里会给一句"试试 adb kill-server"。
