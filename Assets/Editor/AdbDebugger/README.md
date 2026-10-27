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
| `AdbLogcatService.cs` | 抓取主体：进程生命周期、读线程→队列→主线程、堆栈续行合并、C# 堆栈抑制 |
| `AdbDebugWindow.cs` | 独立窗口与菜单（`Tools / ADB 调试 /`） |
| `AdbDebugSetup.cs` | 调 `install_bridge.py` 完成 MCP 注册（C# 不碰 `mcp.json`） |
| `AdbDebugAutoStart.cs` | 编辑器启动时按设置拉起 HTTP 服务 |

## 怎么用

1. **看日志（人）**：`Tools / ADB 调试 / ADB 调试台` —— 勾「启用 logcat」，日志以 `[ADB] ` 前缀进 Console。
2. **让 AI 用**：`Tools / ADB 调试 / 安装 MCP 桥（写入 mcp.json）`，然后在连接器管理页对
   `adb-debug-<工程>` 点信任。AI 侧只有两个工具：`adb_status`、`adb_logcat`。

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
python Tools/build_unitypackage.py --module adb --bundle --version 1.0
#   → Tools/UnityPackage/Aketsuki_AdbDebugger-1.0/（包 + install.py + DEPLOY.md + manifest.json）
python Tools/install_unitypackage.py --module adb --project <目标工程>
```

包内 `package.json` 记着 `installRoot` / `skillName` / `skillFiles`，安装器据此决定落点——
两个工具的包共用同一个安装器，同目录存在两个包时用 `--module` 或 `-p` 指定。

## HTTP 接口

只监听 `127.0.0.1`，带令牌（`?token=` 或 `X-Command-Token` 头），端口与令牌在
`<工程>/.workbuddy/AdbDebug/.adb-info.<工程hash>` 里（编辑器启动时自动重写）。

| 路径 | 说明 |
| --- | --- |
| `GET /v1/status` | 服务与抓取状态：`running` / `logcatRunning` / `logcatState` / `adb` / `adbSource` / `serial` / `received` / `printed` / `coalesced` / `filtered` / `dropped` / `lastError` / `notice` |
| `POST /v1/logcat` | `action=start\|stop\|restart\|status`，可带 `tag` / `level` / `serial` / `backfill` / `stack` / `suppressStack` |

`adb_cli.py` 是它的命令行壳；`adb_mcp_server.py` 把它包成 MCP 的两个工具。

## 可单独删除

删掉 `Assets/Editor/AdbDebugger/` 整个目录（连同 `.meta`）即可，指令系统完全不受影响；
反过来删指令系统，本工具也照常工作。两者之间唯一的"耦合"是：

- 都往同一个 Unity 控制台写日志（`[ADB] ` vs 指令系统自己的前缀）；
- 本工具的日志内容要靠指令系统的 `console.read` 才能被 AI 读到（不装则人看）。

## 已知边界

1. 一次只跟一个设备（`-s` 单序列号），多设备必须填序列号。
2. 设备不在时 `adb logcat` **不退出**、只挂着等，打 `- waiting for device -`；该状态会出现在
   `adb_status` 的 `notice` 字段里。
3. `suppressStack`（默认开）会在打印期间临时改 Unity 的堆栈设置（500ms 抑制窗口），
   停止 / 退出 / 域重载立刻还原。副作用是窗口内你自己脚本的 `Debug.Log` 也没堆栈。
4. 堆栈合并的上限是「行数 + 2000 字符」，撞到就截断并注明。`stack=0` 等于完全不打印调用栈。
