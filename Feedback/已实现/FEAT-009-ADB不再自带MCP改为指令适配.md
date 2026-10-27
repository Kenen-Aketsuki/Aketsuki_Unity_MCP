---
id: FEAT-009
type: feature
title: ADB 不再自带 MCP，改成指令系统里的 adb.* 适配指令
status: done
source: Aketsuki_Unity_MCP
created: 2026-10-27
updated: 2026-10-27
files: Assets/Editor/AdbDebugger/AdbExternalApi.cs, Assets/Editor/AdbDebugger/AdbJsonOut.cs, Assets/Editor/AdbDebugger/AdbLog.cs, Assets/Editor/AdbDebugger/AdbDevices.cs, Assets/Editor/AdbDebugger/AdbLogcatService.cs, Assets/Editor/AdbDebugger/AdbDebuggerSettings.cs, Assets/Editor/AdbDebugger/AdbDebugWindow.cs, Assets/Editor/ExternalCommandSystem/Commands/Adb/AdbBridgeAdapter.cs, Assets/Editor/ExternalCommandSystem/Commands/Adb/AdbCommands.cs, Tools/build_unitypackage.py, Tools/install_unitypackage.py, Tools/verify_unitypackage.py
proposal: false
---

## 需求

> ADB 工具就不用 MCP 了。最多在原本 Unity MCP 中添加这个 ADB 的适配插件。

拍板时补了一句口径：**HTTP 服务如果完全服务于 MCP，就直接删除**。

FEAT-007 把 ADB 拆成独立工具时，给它配了整套平行设施：自己的 HTTP 服务（57841）、
自己的令牌、自己的信息文件、自己的 python 桥（`adb_mcp_server.py` / `adb_cli.py`）、
自己的技能目录 `.workbuddy/skills/adb-cmd/`。核对的结果是——这套 HTTP 服务的唯一消费者就是那个
python 桥（窗口从不经过它），所以按口径**整套删除**。

## 决策

| 选项 | 结论 |
| --- | --- |
| ADB 自带 HTTP + MCP（旧） | 删。两套端口/令牌/信息文件/技能目录，只为了让 AI 调三个工具，配置负担全落在使用者身上 |
| ADB 保留 HTTP、只删 MCP 桥 | 不选。那就剩一套没人用的协议，语义还会跟指令系统分叉 |
| **ADB 只留窗口，AI 走指令系统的 `adb.*`** | **采用**。AI 侧只认 `unity-cmd` 一个 MCP 条目 |

## 实现

### 一、ADB 侧：删掉一整条对外链路

删除（含 `.meta`）：`AdbDebugHttpServer.cs`（693 行）、`AdbDebugProtocol.cs`（322 行）、
`AdbDebugAutoStart.cs`、`AdbDebugSetup.cs`（装/卸 `mcp.json` 的菜单）、整个 `AdbBridge/`
（`adb_mcp_server.py` / `adb_cli.py` / `install_bridge.py` / `SKILL.md`）。

模块从 31 个文件降到 20 个，剩：`AdbDebugWindow` / `AdbDebuggerSettings` / `AdbDevices` /
`AdbLocator` / `AdbLogcatService` / `AdbExternalApi` / `AdbJsonOut` / `AdbLog` + asmdef + README。

连带清理：

- `AdbDebuggerSettings` 去掉 `Port` / `Token` / `ServerEnabled` / `ProjectId` / `DefaultPort`
  （`ProjectHash` 留着，作用域还要用）；
- 窗口去掉整个「调试服务」区块（端口、令牌、复制/重新生成、信息文件路径），帮助文案改写；
- `AdbLogcatService` 去掉挂在 `Tools/指令系统/` 下的重复菜单项（窗口里那条保留）；
- `AdbDebugProtocol.cs` 里的 `JsonOut` 与 `AdbLog` 是两个被广泛引用的工具类，**不是删而是抽出来**
  成 `AdbJsonOut.cs` / `AdbLog.cs`，避免让它们跟着协议层一起消失。

### 二、ADB 侧：新增唯一对外入口 `AdbExternalApi`

```csharp
public static string Invoke(string command, IDictionary<string, string> args)  // → JSON 文本
```

子命令：`devices` / `logcat.start` / `logcat.stop` / `logcat.status` / `logcat.read`。

| 设计点 | 为什么 |
| --- | --- |
| 只暴露**一个**方法 | 指令侧用反射调用，反射点越少越不容易随重构失效 |
| 参数用 `Dictionary<string,string>` | 两边都有的基础类型，不传任何自定义对象，不用管程序集身份 |
| 回 **JSON 文本** | 回对象就得共享类型或反射每个字段；文本是最小契约，加字段不破坏对方 |
| 永远回合法 JSON | 失败也是 `{"ok":false,"error":"…"}`，指令侧不用处理"没返回" |
| 必须主线程调用 | 内部读 EditorPrefs、写 Unity 控制台、起子进程 |

`AdbLogcatService` 顺带加了一份**给指令读的环形缓冲**（2000 条、溢出丢最旧的）：
控制台是给人看的，AI 用 `console.read` 还得按 `[ADB]` 前缀筛、还会被其它日志稀释。
`TakeCaptured(max)` 取走即删——重复返回同一批会让模型以为又刷了一屏错误。

### 三、指令系统侧：`Commands/Adb/`（零 ADB 逻辑）

`AdbBridgeAdapter` 用 `Type.GetType("CivilRev.AdbDebugger.AdbExternalApi, CivilRev.AdbDebugger.Editor")`
拿类型，再按签名取 `Invoke`，结果 `JsonValue.Parse` 后作为 `Data` 返回
（`HttpProtocol.DataToJson` 对 `JsonValue` 是原样嵌入，报告里是结构化 JSON）。
这与 `AddressablesBridge` 处理可选包的做法一致。

**为什么必须反射**：指令系统跑在预定义程序集（没有 asmdef），ADB 是独立 asmdef。
直接引用会让"没装 ADB 就编译失败"，整个指令系统跟着不能用。反射把它降级成运行时依赖。

五条指令：`adb.devices`（默认现场扫）/ `adb.logcat.start` / `.stop` / `.status` / `.read`。
`unity_mcp_server.py` 会按已注册指令**自动**生成 `unity_adb_devices` 等工具，server 一行没改。

### 四、打包/安装/校验三件套

- ADB 包：`BRIDGE_NAMES` / `SKILL_FILES` / `SKILL_NAME` / `SERVER_SCRIPT` 全为空，
  清单里**显式写** `"skillName": ""`、`"skillFiles": []`（键存在但值为空 = 声明"本包没有桥"），
  `entrypoints` 只留 `console`；
- 安装器：`resolve_layout` 现在区分"键缺失"（老包，回落 `MODULE_HINTS`）与"键存在但为空"
  （显式无桥）——以前空列表会被回落规则覆盖，导致 ADB 包去找三个不存在的桥文件；
- 校验器：`markers` 只在清单声明了 `mcpBridge` 时才断言桥与 `SKILL.md`；
  ADB 包额外禁止出现 `AdbDebugger/AdbBridge/`（防回归）。

## 验证

| 项 | 方法 | 结果 |
| --- | --- | --- |
| 反射契约 | .NET 探针把程序集名**故意设成** `CivilRev.AdbDebugger.Editor`，用指令侧那条 `TypeName` 字符串解析 | `Type.GetType` 命中、`GetMethod("Invoke", string, IDictionary<string,string>)` 命中 |
| `devices` 真扫 | 探针链真实 `adb devices -l` | 合法 JSON，1 台 `V2241A（emulator-5554）`，`count`/`usableCount`/`selected` 正确 |
| `devices` 无 adb | 清空 `PROBE_ADB` 环境变量 | `ok:true` + `count:0` + 非空 `error`（"找不到 adb：…"） |
| 其余四条 | 探针调 `start` / `status` / `read`×3 / `stop` | 全部合法 JSON；`read` 取走即删（第二次只拿到剩的 1 条，第三次 0 条）；`text` 里的换行被正确转义 |
| 异常路径 | 未知子命令、强制 `start` 失败 | `ok:false` + 可操作 `error`；未知命令附带可用列表 |
| 编译 | `compile_adb.py`（ADB）+ 离线编译 `Assembly-CSharp-Editor`（指令） | 两边都过 |
| 部署 | `deploy.py --module adb --prune --offline-compile` | 三工程各 20 文件，孤儿清理干净（`AdbBridge/*`、`AdbDebugHttpServer.cs` 等 17 个），离线编译通过 |
| 部署 | `deploy.py --module mcp --offline-compile` | 三工程各新建 5（`Commands/Adb.meta` + 两个 .cs 及其 meta）/ 更新 1（`SKILL.md`），离线编译通过 |
| 包 | ADB 2.0 装到空工程 | 11 文件，**没有创建 `.workbuddy/`**；重装幂等（未变 11） |
| 包 | MCP 1.1 装到空工程 | 67 文件 + 技能目录 3 文件；`Commands/Adb/` 两个 .cs 到位 |
| 收尾 | 三目标工程的 `.workbuddy/skills/adb-cmd/` | 已删（那是 python 桥副本，桥没了留着只会误导） |

## 边界

1. **没装 ADB 包时**五条 `adb.*` 指令会失败并说明"没找到 ADB 调试台"——这是设计，不是 bug。
   装了包必然触发重编译 → 域重载 → 反射缓存重置，不用重启编辑器。
2. `adb.logcat.read` 缓冲 2000 条、取走即删、溢出丢最旧的并计数。
3. `adb.devices` 默认现场扫（同步，最长 8 秒），`refresh:false` 才读缓存。
4. 真机上「下拉是否按预期显示、tag 默认 `Unity` 后日志量是否合适」仍未经用户目视确认
   （FEAT-006 起就欠着这条，本轮依然没法验）。
5. 包版本我自己定了：ADB **2.0**（删了对外接口，破坏性）、MCP **1.1**（新增指令，向后兼容）。
   要换号说一声。

## 后续（2026-10-27）

> 上面「边界 1：没装 ADB 包时五条 `adb.*` 指令会失败并说明」**已被 FEAT-010 改掉**。
> 用户要求「不可用就不要在指令列表中出现」，现在没装 ADB 调试台时这五条**根本不注册**：
> `/v1/commands`、控制台窗口、MCP 工具列表里都查不到 `unity_adb_*`，控制台只留一条汇总日志。
> 硬调会得到"指令已从列表里剔除：<装什么>"。反射本身的契约没变（仍是 `AdbExternalApi.Invoke`），
> 只是样板搬进了通用的 `OptionalDependency`。
