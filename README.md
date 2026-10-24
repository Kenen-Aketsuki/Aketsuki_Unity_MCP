# Aketsuki_Unity_MCP

自制练手用 MCP，纯兴趣产品。

把 Unity 编辑器变成一个可被 AI 客户端直接驱动的 MCP 服务：**Python stdio 桥**（MCP 协议 ⇄ HTTP）
夹在中间，Unity 一侧是一个**只监听 127.0.0.1 的 HTTP 指令服务**，把每条 `[Command]` 自动映射成
一个 MCP 工具。

**本仓库只有这套工具**：没有任何游戏逻辑、场景、美术资源、第三方游戏依赖。

> **⚠️ HTTP 200 ≠ 指令成功（DOC-001）。** 指令业务失败时服务端仍返回 200，
> 成败在 body 的 `ok` / `records[].status`；批量看 `succeeded`/`failed`。
> 判据顺序永远是 **body 优先，状态码只用来识别网络/协议层错误**。

---

## 结构

```
Aketsuki_Unity_MCP/                     ← Unity 工程根（用 Unity 直接打开这里）
├── Assets/Editor/ExternalCommandSystem/
│   ├── Core/          JSON/         指令框架、HTTP 协议层、路径与场景查询
│   ├── Commands/                    内置指令（gameobject / component / prefab / ui / scene / asset ...）
│   ├── CommandHttpServer.cs         监听 + 跨线程队列 + 主线程泵
│   ├── CommandToolsWindow.cs        控制台窗口
│   ├── McpSetup.cs                  MCP 桥安装器（菜单 ①~⑤）
│   ├── McpBridge/                   Python 桥：send.py / unity_mcp_server.py / SKILL.md
│   ├── Project/                     工程客制化专区（不进包，本仓库为空占位）
│   └── README.md                    ★ 完整文档（协议、指令清单、扩展方式、打包维护）
├── Packages/manifest.json           只留 textmeshpro + ugui
├── ProjectSettings/                 Unity 2021.3.16f1c1
├── Feedback/                        反馈表：其它工程踩到的 bug / 缺的指令
│   ├── 待拍板/  未实现/  已实现/      移动文件 = 改状态（待拍板里由你直接写决策）
│   └── 索引.md                       由 Tools/feedback_index.py 生成
└── Tools/
    ├── deploy.py                    把工具同步部署到各工程（Tools/deploy_targets.json 驱动）
    ├── deploy_targets.json          部署目标清单：工程根、Unity 版本
    ├── build_unitypackage.py        打成 .unitypackage（排除 Project/，三次校验防泄漏）
    ├── install_unitypackage.py      解包直接落地到别的工程（不经过 Unity 导入器）
    ├── verify_unitypackage.py       无头 Unity 建空工程 → 导入 → 补 UGUI/TMP 再编译，断言可用
    └── feedback_index.py            重建 Feedback/索引.md + 校验状态/编号/必填字段
```

## 跑起来

1. 用 **Unity 2021.3.16f1c1**（或任意 2021.3.x）打开仓库根目录。
2. 首次打开会提示 **Import TMP Essentials** —— 要导入，`ui.tmpText` / `ui.button` 的中文依赖它。
3. 菜单 **Tools / 指令系统 / 控制台**：打开窗口，勾上「启用 HTTP 监听」。
   令牌与端口写在 `.workbuddy/UnityEditCommand/.http-info`，端口占用会自动避让。
4. 菜单 **Tools / 指令系统 / MCP / ① 安装桥到本工程**：把 `McpBridge/*.py` 部署到
   `.workbuddy/skills/unity-cmd/scripts/`，并把启动项合并进 `~/.workbuddy/mcp.json`（自动备份，不动其它服务）。
5. 到连接器管理页 → 自定义连接器，点 **信任**。这一步只能手动做。

之后 CLI 也能单独用：

```bash
python .workbuddy/skills/unity-cmd/scripts/send.py status
python .workbuddy/skills/unity-cmd/scripts/send.py commands
python .workbuddy/skills/unity-cmd/scripts/send.py compile --wait 300
```

## 搬到别的工程

整套 `Assets/Editor/ExternalCommandSystem/` 直接拷走即可：它**零工程编译依赖**
（唯一可选的挂钩 `MainUIRootBuilder` 是按类型名反射发现，找不到就走内置默认）。
搬过一次之后，把工程登记进 [`Tools/deploy_targets.json`](Tools/deploy_targets.json)，
以后都用 `Tools/deploy.py` 增量同步（见下一节），别再手工拷。
真正的接口细节、指令清单、新增指令的写法、`.unitypackage` 打包与校验，全部在
[`Assets/Editor/ExternalCommandSystem/README.md`](Assets/Editor/ExternalCommandSystem/README.md)。

## 部署到多个工程

`Tools/deploy_targets.json` 登记了正在用这套工具的工程。本仓库改完后**不用手工拷贝**，一条命令同步：

```bash
python Tools/deploy.py --list      # 各工程同步状态 + Unity 是否在跑
python Tools/deploy.py --check     # 只报告差异，不改文件
python Tools/deploy.py             # 部署到全部目标
python Tools/deploy.py island      # 只部署名字能匹配上的（子串匹配）
python Tools/deploy.py --verify            # 部署后让目标工程编译一遍，回传错误
python Tools/deploy.py --offline-compile   # 不依赖编辑器，离线编译验证编辑程序集（2 秒级）
python Tools/deploy.py --force-refresh     # 编辑器在后台不响应时，提到前台代按 Ctrl+R
python Tools/deploy.py --prune             # 顺带删掉目标侧的孤儿文件（默认只报告）
```

当前登记：`project_island_back`、`majotavern`、`CivelRev`（工程根在 `CivelRev/Demo/CivilizationRevival`，
比工作区根低一层——配置里写的是**工程根**，不是 `.workbuddy` 所在的那层）。

规则（脚本头部注释里有完整理由）：

- **比对忽略行尾**。目标工程 `core.autocrlf=true`（工作区 CRLF）、本仓库是 LF，按字节比会把 110 个文件全判成"不同"，
  每次部署都往对方仓库丢一片整文件 diff。写入时沿用目标工程既有行尾。
- **目标已有的 `.meta` 一律保留**（`guid` 是场景/预制体引用的锚点，覆盖等于断引用）。
  仓库与目标的 GUID 本来就不同（各自 Unity 生成），所以"GUID 不一致"不算脏，只在 `--check` 里提示。
  仅当目标缺 `.meta` 时才从仓库补。
- **`Project/`（工程客制化）不同步**：只保证目标工程有该目录，不覆盖里面的内容。
- **只写有差异的文件**，临时文件 + 原子替换。目标 Unity 多半开着，半截文件会被文件监视器读到并触发一次失败的重编译。
- **首次安装整目录落地**：先写到工程 `Temp/` 下的暂存目录，再一次性 rename 进 `Assets/`。
  逐个建文件时 Unity 会在写到一半就扫描并编译，实测报一屏 `error CS2001: Source file ... could not be found`，
  而且之后编辑器进后台就不再自动重编。整目录 rename 对它只是"目录一次出现"，扫到的一定是完整一套。
- **编辑器在后台会推迟自动刷新**，外部写盘它不一定立刻扫。所以部署完会补一发 `asset.refresh`；
  服务连不上且加了 `--force-refresh` 时，脚本会把这个工程的窗口提到前台代按一次 Ctrl+R（Windows）。

## 打包分发

> **日常同步不用这里**——本机的三个目标工程走上面的 `deploy.py`（直接读本仓库源码，永远是实时的）。
> 打包三件套只用于「目标机器上没有本仓库」的场景：给别人的工程装一份、或存档一个可交付的安装包。
> 包内是**打包那一刻的快照**，改完代码要重新打包，否则分发出去的是旧的。

```bash
python Tools/build_unitypackage.py                  # → Tools/UnityPackage/UnityCommandBridge.unitypackage
python Tools/build_unitypackage.py --list-only      # 只看会打进哪些资产
python Tools/verify_unitypackage.py --with-ui \
    --unity "C:/Program Files/Unity/Hub/Editor/2021.3.16f1c1/Editor/Unity.exe"
python Tools/install_unitypackage.py --project <目标工程根>   # 目标工程正开着编辑器时用这个
```

包内只有 `Assets/Editor/ExternalCommandSystem/`（含 `McpBridge/` 三个桥脚本），
`Project/`（工程客制化）**强制排除**：收集阶段跳过、目录条目跳过、写包终检断言，
`verify_unitypackage.py` 再对成品做一次独立断言。桥脚本以磁盘上的 `.meta` 为准，
GUID 稳定，重复导入不会产生重复资产（打包器对重复 pathname 直接报错）。

## 反馈表

在**别的工程**里用出问题，写进 `Feedback/未实现/`，回这里修，修完 `git mv` 到 `Feedback/已实现/`。
一条一个文件、文件名带编号（`BUG-001-xxx.md`），front-matter 里记 `source`（来源工程）。

**需要你拍板的别塞进 `未实现/`**：那类一律写进 `Feedback/待拍板/`（`status: pending`）——
你在文件里直接写「用户批注」，我读完执行、验证、部署，再把文件移到 `已实现/`。
一轮攒了好几条时用决策批件 `待拍板-YYYY-MM-DD.md`，一次列完、你逐条批注，同样归档到 `已实现/`。

```bash
python Tools/feedback_index.py           # 重建 Feedback/索引.md，并校验状态/编号/必填字段
python Tools/feedback_index.py --check   # 只校验（可挂提交前）
```

规则细节见 [`Feedback/README.md`](Feedback/README.md)，新建一条从 [`Feedback/_模板.md`](Feedback/_模板.md) 复制。
