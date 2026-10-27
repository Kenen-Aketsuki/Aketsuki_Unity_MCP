# ADB 调试（独立工具，无 MCP）

把 Android 真机 / 模拟器的 `adb logcat` 逐条打进 Unity 控制台。**这是一个独立工具**，
与本工程的「Unity MCP 指令系统」（`Assets/Editor/ExternalCommandSystem/`）是并列关系：

- **本工具不提供任何对外接口**：没有端口、没有令牌、没有信息文件、没有 MCP server、没有 python 桥；
- **本工具不引用指令系统的任何类型**（有自己的 asmdef `CivilRev.AdbDebugger.Editor`）；
- 可以只装这一个（纯手工看日志），也可以两个都装（那就多了 AI 侧的五条 `adb.*` 指令）。

## 本体构成

| 文件 | 职责 |
| --- | --- |
| `AdbDebuggerSettings.cs` | 全部设置（按工程隔离，`adb.exe` 路径全局）；EditorPrefs 前缀 `CivilRev.AdbDebug.` |
| `AdbLocator.cs` | 五级定位 `adb.exe`：手填 → `ANDROID_HOME`/`ANDROID_SDK_ROOT` → Unity 的 `AndroidSdkRoot` → 编辑器自带 SDK → `PATH` |
| `AdbDevices.cs` | 设备检索：跑 `adb devices -l`、解析、缓存（后台线程），给窗口下拉与 `adb.devices` 指令用 |
| `AdbLogcatService.cs` | 抓取主体：进程生命周期、读线程→队列→主线程、堆栈续行合并、C# 堆栈抑制、留一份给指令读的缓冲 |
| `AdbDebugWindow.cs` | 唯一界面：菜单 `Tools / ADB 调试 /`，设备下拉 + 抓取参数 + 状态 |
| `AdbExternalApi.cs` | **唯一对外入口**：`Invoke(子命令, 参数字典) → JSON`，由指令系统的 `adb.*` 指令反射调用 |
| `AdbJsonOut.cs` / `AdbLog.cs` | 手写 JSON 输出器 / 日志出口（都零外部依赖） |

## 怎么用

1. **人看日志**：`Tools / ADB 调试 / ADB 调试台` —— 设备下拉里选一台（通常只有一台，留「自动」即可），
   勾「启用 logcat」，日志以 `[ADB] ` 前缀进 Console。
2. **让 AI 抓日志**：装「Unity MCP 指令系统」，用它的 `adb.devices` / `adb.logcat.*` 指令
   （MCP 工具名 `unity_adb_devices`、`unity_adb_logcat_start` …）。**不需要给本工具配任何 MCP 条目。**

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

扫描结果缓存在内存里，窗口只读缓存；`adb.devices` 指令默认**现场扫**（`refresh:false` 才读缓存）。

## Tag 过滤默认 `Unity`

`Debug.Log` 在 Android 上走 Unity 自己的日志通道，tag 恒为 `Unity`；不设过滤时 logcat 会把整机
所有 App 的日志一起灌进来。所以**默认 tag = `Unity`**，只看自己的日志。

要看全部（比如排查系统层的崩溃、`AndroidRuntime`）就把 tag 框清空；指令那边的 `tag` 传空字符串同理。
机器级的系统日志（`FATAL EXCEPTION`、`lowmemorykiller` 之类）通常在别的 tag 下，被默认值滤掉是预期行为——
真需要时清空再抓一次。

## AI 侧：五条指令

| 指令 | 用途 |
| --- | --- |
| `adb.devices` | 现场扫设备（`refresh:false` 读缓存） |
| `adb.logcat.start` | 开始抓，可顺手改 `serial` / `tag` / `level` / `backfill` |
| `adb.logcat.stop` | 停止（缓冲里的日志不丢） |
| `adb.logcat.status` | 运行状态、设备、各项计数、`notice` / `lastError` |
| `adb.logcat.read` | 取走缓冲里的日志（**取走即删**，`max` 默认 200 / 上限 2000） |

实现上：指令系统的 `Commands/Adb/` 里**没有一行 ADB 逻辑**，它只用反射调
`AdbExternalApi.Invoke(string, IDictionary<string,string>)` 并把返回的 JSON 转成结构化结果。
所以本工具可以单独卸载，指令那边只会报"没找到 ADB 调试台"，不会编译失败、也不会崩。

## 和其它工具的关系

| | Unity MCP 指令系统 | ADB 调试 |
| --- | --- | --- |
| 对外接口 | HTTP 57831 + MCP 桥 | **没有** |
| 信息文件 | `.workbuddy/UnityEditCommand/.http-info.*` | 无 |
| 技能目录 | `.workbuddy/skills/unity-cmd/` | 无 |
| 读日志 | `console.read` / `adb.logcat.read`（装了本工具才有后者） | **不读日志**，只启停并把内容推进 Unity 控制台 |

日志的出口是 Unity 控制台，本工具只负责把设备日志**弄进控制台**。
没装指令系统时，人在 Console 窗口里照样看得到，只是 AI 读不到——需要 AI 读就两个都装。

### 控制台里的样子

主内容加粗、调用栈原样。一条日志展开后栈有十几行，全加粗等于没加粗：

```
[ADB] <b>Task Guide True-3-Controller (MainIsLandController)-Normal</b>
Proxy_TaskGuide:CheckGuide()
Proxy_TaskGuide:Update()
```

标签只加在**给控制台的那一份**上，两条读取途径拿到的内容不一样：

| 途径 | 内容 |
| --- | --- |
| `adb.logcat.read` | **纯文本**，无任何标签（缓冲里存的就是合并后的原文） |
| `console.read` | 带 `<b>` 的原文——按正文关键词 `contains` 过滤照样命中，标签不影响匹配 |

## 安装到别的工程

两条路，都**不需要**指令系统在场：

```bash
# ① 从本仓库部署（开发用）
python Tools/deploy.py --module adb <目标工程> --verify

# ② 分发包（给别人）
python Tools/build_unitypackage.py --module adb --bundle --version <版本>
#   → Tools/UnityPackage/Aketsuki_AdbDebugger-<版本>/（包 + install.py + DEPLOY.md + manifest.json）
python Tools/install_unitypackage.py --project <目标工程>
```

包内 `package.json` 记着 `installRoot`；`skillFiles` 显式为空列表 = **本包不写 `.workbuddy`**。
两个工具的包共用同一个安装器，同目录存在两个包时用 `--module` 或 `-p` 指定。

## 可单独删除

删掉 `Assets/Editor/AdbDebugger/` 整个目录（连同 `.meta`）即可，指令系统完全不受影响
（那五条 `adb.*` 指令会开始报"没找到 ADB 调试台"，但其余照常）；
反过来删指令系统，本工具也照常工作。

## 已知边界

1. 一次只跟一个设备（`-s` 单序列号）。多设备并存时 `adb` 会因"more than one device"直接失败，
   所以要么在下拉里显式选一台，要么留「自动」但只插一台。窗口的状态行与 `adb.devices` 的结果都会提示。
2. 设备不在时 `adb logcat` **不退出**、只挂着等，打 `- waiting for device -`；该状态会出现在
   `adb.logcat.status` 的 `notice` 字段里。抓取进程退出时，报错里会附上"最近一次扫描到的设备"，方便对序列号。
3. `suppressStack`（默认开）会在打印期间临时改 Unity 的堆栈设置（500ms 抑制窗口），
   停止 / 退出 / 域重载立刻还原，**空闲超过窗口也会自动还原**（BUG-009：之前"没日志"就没人看窗口，
   于是设置会一直停在 None，你自己脚本的日志也连带没栈）。
   副作用是窗口内你自己脚本的 `Debug.Log` 也没堆栈。
4. 堆栈合并的上限是「行数 + 2000 字符」，撞到就截断并注明。`stackLines=0` 等于完全不打印调用栈。
5. `adb.logcat.read` 的缓冲只有 **2000 条**，满了丢最旧的并计数（`dropped`）。长期不读会丢历史。
6. 扫描有 8 秒上限：`adb server` 卡死时不会把编辑器一起挂住，窗口里会给一句"试试 adb kill-server"。
