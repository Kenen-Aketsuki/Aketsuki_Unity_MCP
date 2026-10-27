# 交接清单（HANDOVER）

> 这份文件的唯一职责：**让没接触过这套东西的人（或下一个 AI 会话）在 10 分钟内知道该读哪、
> 该改哪、改完怎么证明没坏、哪些坑不要踩。**
> 细节一律不复述，只给入口和判据。最后更新：2026-10-27。

## 1. 这是什么

Unity **编辑器扩展** + 一层 **Python 桥**，让你（或 AI）用 HTTP / MCP 驱动 Unity 编辑器：
建节点、改组件、存 Prefab、编译、进 Play 模式点按钮。三个消费者，同一套服务端：

| 消费者 | 入口 | 谁在用 |
| --- | --- | --- |
| HTTP 直连 | `Assets/Editor/ExternalCommandSystem/McpBridge/send.py` | 脚本、人、这个会话里的我 |
| MCP（stdio 桥） | `.../McpBridge/unity_mcp_server.py` | WorkBuddy 会话，65 个 typed 工具 |
| Unity 菜单 | `工具 / 指令系统 / 指令控制台` | 你，手工开关与调试 |

服务端是 C#，装在 **4 个 Unity 工程**里（现役 3 个，见 §4）。它只读写本地文件 + 监听
`127.0.0.1`，没有远端通路。

> **本仓库现在有两个并列的工具，不是一个。**
>
> | 工具 | 目录 | 干什么 | 包名 | 对外接口 |
> | --- | --- | --- | --- | --- |
> | 指令系统（本文档主角） | `Assets/Editor/ExternalCommandSystem/` | 下发指令控制编辑器 / 运行时 | `Aketsuki_Unity_MCP` | HTTP 57831 + MCP 桥 |
> | ADB 调试 | `Assets/Editor/AdbDebugger/` | 抓 Android 真机 logcat 进控制台 | `Aketsuki_AdbDebugger` | **没有**（只开窗口） |
>
> 两者**零依赖**（ADB 的 asmdef 是 `references: []`，物理上引用不到对方），可单独安装。
> ADB 那份**不自带 MCP**：要让 AI 抓日志，装指令系统，用它的 `adb.*` 五条指令——
> 那是指令系统里的一层反射适配（`Commands/Adb/`），调 ADB 模块的 `AdbExternalApi`。
> ADB 自己的文档在 `Assets/Editor/AdbDebugger/README.md`，本文档不重复。
> 工具侧一切按 `--module {mcp,adb}` 区分（`deploy.py` / `build_unitypackage.py` / `install_unitypackage.py`）。

## 2. 10 分钟上手

```bash
cd D:/Wrok_proj/Aketsuki_Unity_MCP

# ① 服务活着吗（Unity 编辑器必须开着，且菜单里开过 HTTP 指令服务）
python Assets/Editor/ExternalCommandSystem/McpBridge/send.py \
       --project-root D:/Wrok_proj/project_island_back status

# ② 有哪些指令（条数不写死，随代码变；参数说明由服务端 CommandAttribute 提供，不会漂）
... send.py --project-root <工程> commands

# ③ 发一条
... send.py --project-root <工程> send my-command.json

# ④ 改完 C# 的闭环
python Tools/deploy.py <目标> --verify      # 部署 + 让目标编辑器编译并回传错误
```

**不要硬编码端口。** 端口在 `<工作区>/.workbuddy/UnityEditCommand/.http-info.<工程hash>` 里，
客户端自己读、自己比对身份。删掉那个文件 = 客户端找不到端点，只能等 Unity 重启重写。

## 3. 文件地图：只想读一份就读对的那份

| 想知道 | 读这个 | 别读 |
| --- | --- | --- |
| 指令系统的架构、路径规则、UI 指令参数 | `Assets/Editor/ExternalCommandSystem/README.md` | 根目录 `README.md`（那只是仓库门面） |
| AI 该怎么用（自愈流程、运行时门禁、多场景、客户端语义） | `Assets/Editor/ExternalCommandSystem/McpBridge/SKILL.md` | — |
| 反馈 / bug / 改进项的登记与流转 | `Feedback/README.md` + `Feedback/索引.md` | 各工程 `.workbuddy/` 下的散装清单（已废弃，见 DOC-002） |
| 部署器为什么这么写（行尾、.meta、原子写） | `Tools/deploy.py` 顶部 docstring | — |
| 历史决策（每条为什么这么做） | `Feedback/已实现/` 下的工单与 `已拍板-*.md` | git log（只看标题看不出代价） |

## 4. 现役部署目标与理由

`Tools/deploy_targets.json`（改这里不用改脚本）：

| 目标 | 工程根 | Unity | 备注 |
| --- | --- | --- | --- |
| `project_island_back` | `D:/Wrok_proj/project_island_back` | 2022.3.62f2c1 | 主力验证工程，常开 |
| `majotavern` | `D:/Wrok_proj/majotavern` | 2022.3.62f2c1 | 常不开，部署即落盘 |
| `CivelRev` | `D:/Wrok_proj/CivelRev/Demo/CivilizationRevival` | 2021.3.16f1c1 | **工程根比工作区根低一层**；`skill_root` 单列成 `CivelRev/Demo` |

`project_island` **已摘除**（用户 2026-10-25 自己卸的，IMPR-009）。它的
`~/.workbuddy/mcp.json` 条目 `unity-cmd-project-island` 还在，但现在指着一个不存在的服务端 ——
用不上就删掉那条，别看到报错又去装回来。

**部署一次写两处**，两个都要管：
1. `<工程>/Assets/Editor/ExternalCommandSystem/`（服务端 C#，145 个文件，含 `.meta`）
2. `<工程>/.workbuddy/skills/unity-cmd/`（MCP 桥脚本 + `SKILL.md`，3 个文件）

ADB 工具只写一处：`Assets/Editor/AdbDebugger/`（20 个文件，含 `.meta`）。
**它没有技能目录、没有桥**——`deploy.py --module adb <目标>` 只同步这些 C# 与文档；
**不带 `--module` 就是 mcp**。`--prune` 只清本模块目录里的孤儿，不会碰另一个工具的目录。

第 2 处是 MCP **真正拉起**的那份，不写它，AI 读到的指令说明就永远停在旧版（IMPR-007 的坑）。
`--check` / `--list` / `--dry-run` 都会报这两处状态。目标那份比仓库新（mtime 更晚）时**不覆盖**，
只报冲突 —— 那通常意味着有人在工程里改过，`--force-skill` 才硬写。

## 5. 验证手段：三层，按改动范围选

| 改动 | 验证 | 判据 |
| --- | --- | --- |
| 只改 C# | `deploy.py <目标> --verify`（编辑器开着）；或离线编译 ↓ | 0 个 `error CS` 落在 `ExternalCommandSystem/` 下 |
| 改 C# + 打包 | `python Tools/verify_unitypackage.py -p <包> --with-ui` | 无头 Unity 导入 + 编译，关键文件齐全 |
| 改 Python 桥 | `py_compile` + 对在线编辑器实跑那条子命令 | 退出码 0 且输出对 |

离线编译工具**不在这个仓库**：`C:\Users\poi77\.workbuddy\tools\unity-offline-compile\unity_offline_compile.py`
（跨程序集同时改动时用同目录的 `merged_offline_compile.py`）。它不写工程的 `Temp/`、`Library/`，
编辑器开着也能跑，比 `Unity -batchmode` 安全。**换机器就没了，先备份。**

## 6. 分发给别人

```bash
python Tools/build_unitypackage.py --bundle                 # → Tools/UnityPackage/Aketsuki_Unity_MCP-<版本>/
python Tools/build_unitypackage.py --module adb --bundle    # → Tools/UnityPackage/Aketsuki_AdbDebugger-<版本>/
```

产出目录：`.unitypackage` + `install.py` + `DEPLOY.md` + `manifest.json`。
**给目录或 zip，不要只给那个 `.unitypackage`** —— 接收方得自己拆 tar.gz、按 guid 拼路径、
还要知道"目标已有 `.meta` 不能覆盖"，光一个包做不对。`install.py`（= 仓库里的
`Tools/install_unitypackage.py`）会顺带写 `.workbuddy/skills/<skillName>/`，
**装哪个工具、装到哪、桥同步哪几份，全从包内 `package.json` 读**（`installRoot` /
`entrypoints.mcpBridge` / `skillName` / `skillFiles`），脚本里没有写死路径 —— 两个包共用同一个安装器。
同目录里有两个包时用 `--module` 或 `-p` 指定，别指望它猜。

包装完 ≠ 能用：还要在 `~/.workbuddy/mcp.json` 加一条带 `--project-root` 的条目，
并在连接器管理页点"信任"。这件事包里做不了，必须人来做。

## 7. 已知边界（不是 bug，是设计取舍）

1. **一条指令只占一帧**。执行器在 `EditorApplication.update` 里同步跑完整个队列，无法跨帧等待。
   要"点一下 → 等动画 → 再看"，用 `send.py seq`（客户端侧 sleep，服务端零改动），
   或自己在两次请求之间轮询 `runtime.hierarchy` / `editor.state`。服务端的 `batch` 解决不了这个。
2. **拿不到键盘/摇杆硬输入**。EventSystem 注入碰不到 `Input.GetKeyDown` 与新 Input System 的设备读取。
3. **`runtime.click` 的 `position` 模式没有场景概念**，同屏重叠时点到上层（返回里带 `warning`）。
4. **`runtime.scroll` 没有松手惯性**；`amount` 基准是"可滚动范围"，不是视口高度。
5. **多场景叠加必须点名场景**：`[scene:Level1]UIRoot/BtnStart` 或 `{"scene":…,"path":…}`。
   同名多命中一律失败并给候选，不猜。
6. **端点发现很慢**：一次 `status` 实测 ~18 秒。原因见 `IMPR-014`（9 个空端口各等 2 秒）。
   不影响正确性，但你等的时候别以为卡死了。
7. **运行时指令默认关闭**（`CommandSystemSettings.RuntimeCommandsEnabled=false`，按工程存）。
   关着时连 `runtime.play/stop` 都失败，逃生口是常规指令 `editor.play` / `editor.stop`。
8. **ADB 相关的三件事要分清**：
   - 抓取本身（设备扫描、进程、堆栈合并、`suppressStack` 临时改全局堆栈设置）在
     `Assets/Editor/AdbDebugger/`，边界见它的 README。
   - AI 侧入口是本文档这套指令里的 `adb.devices` / `adb.logcat.start|stop|status|read`
     （`Commands/Adb/`），**全部走反射**调 `AdbExternalApi`：没装 ADB 包时这五条**根本不注册**——
     `/v1/commands`、控制台窗口、MCP 工具列表里都没有 `unity_adb_*`，其余指令不受影响，也不会编译失败。
     硬调会得到"指令已从列表里剔除：<装什么>"而不是"未注册的指令 ID"。
     机制见 `Assets/Editor/ExternalCommandSystem/README.md` §5.5（`ICommandAvailability` + `OptionalDependency`），
     以后加同类可选插件照那三步做。
   - `adb.logcat.read` 的缓冲 **2000 条、取走即删**；日志同时进 Unity 控制台（`[ADB] ` 前缀），
     所以 `console.read contains:"[ADB]"` 也能读到。

## 8. 未决事项（接手前先看这段）

| 编号 | 状态 | 说明 |
| --- | --- | --- |
| BUG-005 | **已改未实测** | `runtime.scroll` 横向 amount 正负号修正（commit `68f564f`），只过了编译与部署。真机要进 Play，用户说自己找时间做（IMPR-012） |
| IMPR-008 | 决定**不做** | 把 5 条 unity-cmd 合并成一个 MCP server。代价是破坏现有工具签名 |
| IMPR-009 | 已执行 | `project_island` 不装回，已从 `deploy_targets.json` 摘除 |
| IMPR-010 | 已执行（最小版） | 只做客户端侧等待（`send.py seq`），服务端跨帧执行器**没做** |
| IMPR-011 | 已执行 | 改进清单归档成 `DOC-002`，原文件删除 |
| IMPR-013 | 已执行 | 就是本文件 |
| IMPR-014 | **待拍板** | 端点发现要不要在"信息文件命中的编辑器已自证身份"时提前收手，省掉那 18 秒 |
| Phase 2 | 未开工 | 跨帧执行器、`runtime.screenshot`、断言原语、`runtime.invoke`。见 `FEAT-001` 末尾 |

## 9. 不能做的事（踩过的）

1. **不要手改 Unity 自动生成的 `.asset` / `.meta`**。GUID 是引用锚点，覆盖等于断引用。
   部署器只补缺失的 `.meta`，从不覆盖目标已有的那份 —— 这是有意的。
2. **不要 `git push`**。这个仓库只允许本地 commit（用户 2026-10-23 授权），推送由用户自己做。
3. **不要在 HTTP 监听线程里读 `EditorPrefs` / `EditorApplication.isPlaying`**。
   那是主线程 API，一调就 500，而客户端靠着 `/v1/status` 做端点发现 ——
   后果是**整个客户端发不出任何指令**（连 `--url` 都被前置校验拦掉）。门禁状态走主线程每帧快照。
4. **门禁判定读实时值，不读快照**。快照每帧刷一次，同一请求里"关开关 + `runtime.stop`"会拿旧值放行。
5. **不要用 `curl` 判断端口存活**。沙盒里会走本地代理，未监听时报 502 + `upstream connect failed`，
   看着像服务端 bug。用 TcpClient / `tasklist`。
6. **删 `.http-info.*` 前想清楚**。删了客户端就找不到端点，只能等 Unity 重启重写。

## 10. 走完这一屏还缺什么

- 想改指令行为 → `Feedback/README.md` 的流程，一条一个文件、带 `source`、编号由脚本校验。
- 想加新指令 → `Assets/Editor/ExternalCommandSystem/README.md` §4「扩展一个新指令」。
- 想知道某个设计为什么这么怪 → 先搜 `Feedback/已实现/` 的标题，再搜代码注释；
  大部分坑都在注释里写明了"别退回去"。
