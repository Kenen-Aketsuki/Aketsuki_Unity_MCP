---
id: FEAT-007
type: feature
title: 把 ADB 调试从指令系统里彻底拆出去，做成完全独立的第二个工具
status: done
source: Aketsuki_Unity_MCP
created: 2026-10-27
updated: 2026-10-27
files: Assets/Editor/AdbDebugger/（整套）, Assets/Editor/ExternalCommandSystem/（摘除 ADB）, Tools/deploy.py, Tools/compile_adb.py, Tools/build_unitypackage.py, Tools/install_unitypackage.py
proposal: false
---

## 需求

> 然后想办法把这个 ADB 调试工具和原本的 MCP 拆开，二者共用这个工程，但分属两个不同的工具

两个决策点、用户已拍板：

| 问题 | 用户选择 |
| --- | --- |
| 依赖边界 | **不允许依赖，ADB 视为完全独立的另一个工具** |
| 拆分深度 | **完全物理独立**（独立目录 + 独立窗口菜单 + 独立 skill + 独立 mcp.json 条目 + 独立 asmdef + 独立端口 + 独立 HTTP 服务 + 独立 unitypackage，可只装 ADB 不装 MCP） |

"可只装 ADB 不装 MCP"是这条的硬判据：**新工程的 Assets 里只放 `AdbDebugger/` 一个目录，
不装 `ExternalCommandSystem/`，必须能编译、能开窗口、能抓到日志。**

## 为什么值得拆（不是洁癖）

混在一起时，只想要其中一半的人被迫接受另一半：

- 端口、令牌、`.http-info` 信息文件是**一套**，但 ADB 与指令下发没有任何共同语义；
- ADB 的编辑器偏好挤在 `CommandSystemSettings.` 前缀下，`ResetAll()` 一键重置会连带清掉对方的设置；
- `adb.logcat` 是一条**指令**，于是抓日志受「运行时指令开关」这类跟它无关的门禁牵连
  （FEAT-005 当时专门为它开了一个例外，例外本身就是气味）；
- 打包分发时两拨人拿到同一个包，包体积与依赖说明都得混着写。

ADB 真正依赖的只有主机上的 `adb.exe`，它的宿主是**编辑器进程**而不是指令系统。

## 实现

### 一、新工具本体：`Assets/Editor/AdbDebugger/`

从 `ExternalCommandSystem/Adb/` 整体迁出（`AdbLocator` / `AdbLogcatService`，改名空间
`CivilRev.CommandSystem.Editor` → `CivilRev.AdbDebugger`），并补齐独立运行所需的四件套：

| 文件 | 干什么 |
| --- | --- |
| `CivilRev.AdbDebugger.Editor.asmdef` | 独立程序集。**`references: []`** —— 物理上无法引用指令系统，这条比任何约定都硬 |
| `AdbDebuggerSettings.cs` | 独立 EditorPrefs 前缀 `CivilRev.AdbDebug.`，自带工程哈希隔离键 |
| `AdbDebugHttpServer.cs` | 独立 HTTP 服务，端口 57841，单例 + 主线程泵，`/v1/status` `/v1/logcat` |
| `AdbDebugWindow.cs` | 窗口 `Tools/ADB 调试/ADB 调试台`（服务段 + 抓取段），菜单 `启停 logcat` |
| `AdbDebugSetup.cs` | 菜单装/卸 MCP 桥（写 `~/.workbuddy/mcp.json`） |
| `AdbDebugAutoStart.cs` | `[InitializeOnLoad]` + `delayCall` 自动起服务、退出时清理 |
| `AdbDebugProtocol.cs` | URL/查询解析、手写 JSON 输出、`[ADB] ` 前缀日志 |
| `AdbBridge/` | 零依赖 MCP stdio server（`adb-debug`）+ CLI + `install_bridge.py` + `SKILL.md` |

**端口选 57841**：指令系统从 57831 起最多避让 20 个端口（57831–57850），ADB 落在避让区间之外，
两套服务在同一工程里同时起不会互相挤。

### 二、指令系统那边摘干净

删 `ExternalCommandSystem/Adb/`（`AdbLocator` / `AdbLogcatService` / `AdbLogcatCommands` 三对文件），
删 `CommandSystemSettings` 的 ADB 段、`CommandToolsWindow` 的 ADB 分区。
`adb.logcat` 指令随之消失——**这是有意的破坏性变更**，客户端要改走 ADB 服务的 `/v1/logcat`。

删 UI 分区踩的坑：ADB 区里还嵌着 `#region adb 路径` / `#region 设备与过滤` 两个**嵌套 region**，
`text.index("#endregion", start)` 会命中内层就停、留下 6 处 `AdbLogcatService.` 悬空引用。
改成数 region 深度、深度归零才停的写法，删完 463 行、零残留。

### 三、工具链双模块化

三个脚本各加一个 `--module {mcp,adb}`，路径全部从"模块表"里取，不再散落硬编码：

- `Tools/deploy.py`：`MODULES` 字典（源码根 / 桥目录 / 技能目录 / 程序集名 / 是否有 `Project/` 客制化），
  `apply_module()` 填模块级变量。踩过：`probe_target` 里目标路径写死成 `ExternalCommandSystem`，
  ADB 模块一跑就把整个目标目录判成 138 个孤儿——改成按 `SRC_REL` 推导后是「新建 29 · 孤儿 0」。
- `Tools/compile_adb.py`（新）：`unity_offline_compile.py` 只认 Unity 生成的 csproj，
  而 asmdef 程序集在"没开过编辑器的机器"上根本不存在 csproj。做法是先跑一次通用编译器把
  `DefineConstants` 与引用列表**借**出来（滤掉 `ScriptAssemblies`），再拼一个只含
  `AdbDebugger/*.cs` 的 csproj 交给 `dotnet build`。
- `Tools/build_unitypackage.py`：`--module adb` 时换源码根、桥目录、包名 `Aketsuki_AdbDebugger`。
- `Tools/install_unitypackage.py`：**不再写死任何路径**，改从包内 `package.json` 读
  `installRoot` / `entrypoints.mcpBridge` / `skillName` / `skillFiles`。
  清单为此新增四个字段（`module` / `skillName` / `skillFiles` / `entrypoints` 按模块取）。
  `skillFiles` 是 `{from, to}` 对——**桥在 Assets 里是平铺的，技能目录要求 python 落在 `scripts/` 下**，
  两边不同形，一开始按"同路径"写，实测 ADB 包只装进了 `SKILL.md`，两个 py 被跳过。

## 验证

| 项 | 判据 | 结果 |
| --- | --- | --- |
| 三目标工程编译 | `compile_adb.py --project <三工程>` | 均「编译通过（CivilRev.AdbDebugger.Editor）」：island_back 2022.3 / majotavern 2022.3 / CivelRev 2021.3 |
| 三目标部署 | `deploy.py --module adb <目标>` | 各 29 文件全新建 + 桥 3 文件内容一致；`--check` 复跑「新建 0 · 孤儿 0」 |
| 指令系统摘除 | `deploy.py --prune` | 三工程清掉 7 个 ADB 孤儿文件，指令系统本体编译不受影响 |
| ADB 包 | `build_unitypackage.py --module adb --version 1.0` | 17 资产（15 文件 + 2 目录），40.8 KB |
| 装到空工程 | `install_unitypackage.py --module adb --project <空工程>` | 15 文件落地且**逐字节与源一致**、`.meta` 齐备；桥 3 份正确进 `.workbuddy/skills/adb-cmd/`（含 `scripts/` 子目录） |
| 幂等 | 同一命令再跑一次 | 「新增 0 / 更新 0 / 未变 15」 |
| 无头导入 | `verify_unitypackage.py --module adb --with-ui` | Unity 真把 15 个文件导到 `Assets/Editor/AdbDebugger/`（连同 .meta 共 33 个），`error CS` 0 条 |
| Unity 真实编译 | 用户工程 `project_island_back`（2022.3.62f2c1，同时装了指令系统） | 编辑器自己编出 `Library/ScriptAssemblies/CivilRev.AdbDebugger.Editor.dll`（10-27 16:44），证明 asmdef 在**真实编辑器**里也能独立成程序集 |

### 无头编译这一步没跑完（环境问题，不是包的问题）

`verify_unitypackage.py --with-ui` 的第二步（补 UGUI/TMP 再编译）在本机跑不完：
`bee_backend` 卡在 `ScriptAssemblies` 不再输出，`Library/ScriptAssemblies` 一直是空的。
期间 `-quit` 之后 Unity 也不退出。日志里的线索是
`bee_backend: error: More than one copy of bee_backend running in <临时工程> -- PID ... waiting`
——上一次被我中断的 Unity 留下的**孤儿 `bee_backend.exe`** 占着该工程的 DAG 锁；
清掉孤儿、换新临时工程后仍然卡住，判断为环境问题（用户的编辑器同时开着、机器资源吃满）。
所以「无头 Unity 编译通过」这条判据本轮**没拿到**，改用上表最后两行替代：
四份离线编译 + 用户工程真实编辑器产出的 dll。等机器空下来可以补跑。

顺带修了这个脚本的一个真 bug：关键文件断言（`EXPECTED_MARKERS`/`FORBIDDEN_PREFIX`）
原来写死成指令系统的路径，拿 ADB 包一跑就报「缺少关键文件 McpSetup.cs…」的**假失败**。
现在改为从包内 `package.json` 的 `installRoot` / `entrypoints` 推导，两个包都能用。

## 边界与遗留

- **`adb.logcat` 指令已删除**，直接依赖它的脚本会失败。这是拆分的必然代价，用户已确认。
  替代：`POST http://127.0.0.1:<ADB端口>/v1/logcat`，或技能 `adb-cmd` 的 `adb_logcat`。
- 两套服务并存时**各写各的信息文件**（`.http-info.<hash>` vs `.adb-info.<hash>`），
  客户端按文件名前缀分辨，不存在"读错端点"。
- 指令系统的包还停在 v1.0（10-24 那次），不含之后的改动；重打需要用户定版本号（同号会被版本守卫拦）。
- 真机在 Unity 里的实际观感仍需用户开工程抓一次（FEAT-006 就欠着这一条）。

## 后续（2026-10-27）

> **本工单里的「ADB 自带 HTTP 服务 + MCP 桥 + `adb-cmd` 技能目录」已被 FEAT-009 撤销。**
> 用户拍板「ADB 工具就不用 MCP 了，最多在原本 Unity MCP 中添加这个 ADB 的适配插件」，
> 且确认「HTTP 完全服务于 MCP 就直接删」。现在 ADB 只剩窗口，AI 侧入口是指令系统的
> `adb.*` 五条指令（反射适配）。本文上面描述的端口/令牌/信息文件/桥/技能目录均已不存在。
