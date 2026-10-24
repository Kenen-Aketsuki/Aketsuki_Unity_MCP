---
id: FEAT-004
type: feature
title: 打包器带包身份（名称/版本/作者/日期）并输出可分发目录，安装器顺带同步 MCP 桥
status: done
source: Aketsuki_Unity_MCP
created: 2026-10-24
updated: 2026-10-24
files: Tools/build_unitypackage.py, Tools/install_unitypackage.py, Tools/verify_unitypackage.py
proposal: false
---

## 需求

> 重新打一个包，需要注明作者、版本号（1.0）、打包日期，以及包的名称：Aketsuki_Unity_MCP。
> 但我更想知道如果一个新的 AI 会话拿到这个包，可以在指定项目中进行部署吗。

触发点是两个现实问题：

1. 旧包 `UnityCommandBridge.unitypackage` 是 2026-10-23 打的，**缺 8 个 Play 模式文件**
   （`Commands/Runtime/` 6 个 + `Core/RuntimeCommandGate.cs` + `Core/SceneScope.cs`）。
   对 `project_island_back` 实测 dry-run 显示「更新 29 个文件」——装下去会把 Core 回退成旧版，
   而安装器只增不删，`Runtime/*.cs` 留着 → 编译不过的混合态。
2. 光给一个 `.unitypackage`，接收方得自己拆 tar（`<guid>/asset` + `<guid>/pathname`），
   还得知道「目标已有的 `.meta` 不能覆盖」，手写这一步必错。

## 实现

### 一、包身份写进包内 `package.json`

`.unitypackage` 格式**没有任何元数据槽位**：gzip 头的 FNAME 还必须恒为 `archtemp.tar`
（填别的值 Unity 会静默拒绝导入，见 `write_package` 注释）。所以只能在包里放一个普通资产当清单：

```json
{
  "name": "Aketsuki_Unity_MCP",
  "version": "1.0",
  "author": "Aketsuki",
  "built": "2026-10-24",
  "unityVersion": "2021.3.16f1c1",
  "packageFile": "Aketsuki_Unity_MCP-1.0.unitypackage",
  "installRoot": "Assets/Editor/ExternalCommandSystem",
  "files": 65,
  "entrypoints": { ... }
}
```

装到工程后它同时是「这个工程装的是哪个版本」的唯一凭据。GUID 用 `uuid5(namespace, path)` 派生，
每次打包稳定 → 重复导入是覆盖而不是新增。

新增命令行：`--name` / `--version` / `--author` / `--built` / `--bundle`。
默认输出改成 `Tools/UnityPackage/<名称>-<版本>.unitypackage`。

`collect_csharp` 增加一条跳过：源工程自己装过包时磁盘上会有一份 `package.json`，
不跳过就会和虚拟注入的那份撞成「包内路径重复」硬失败。

### 二、`--bundle` 输出可分发目录

```
Tools/UnityPackage/Aketsuki_Unity_MCP-1.0/
├── Aketsuki_Unity_MCP-1.0.unitypackage
├── install.py        # 源仓库 Tools/install_unitypackage.py 的原样拷贝
├── DEPLOY.md         # 给接收方（人或 AI）的部署说明
└── manifest.json
```

**为什么要给目录而不是裸包**：安装器和说明必须跟着走，否则"拿到包"等于拿到一堆 guid 目录。

### 三、安装器补三件事

| 能力 | 为什么 |
|---|---|
| `--skill`（默认开） | 同时把 `McpBridge/` 三个文件同步到 `<工程>/.workbuddy/skills/unity-cmd/`。MCP 客户端拉起的是那一份，不是 `Assets/` 里那份——以前靠手工拷，三个工程的 `SKILL.md` 因此各自停在 278 行旧版（不含 `runtime.*`） |
| 默认包定位 | 取「同级 / `Tools/UnityPackage` 下唯一的 `.unitypackage`」，多个就报错列出来。**不挑最新的**——多包共存时静默选一个是这类安装器最容易犯的错 |
| 版本守卫 | 目标已装版本 > 包版本时警告：只增不删的安装器拿旧包覆盖新工程 = 混合态 |

## 实测

| 检查 | 结果 |
|---|---|
| 新包内容 | 71 资产（65 文件 + 6 目录），246.9 KB |
| 临时假工程安装 | 64 个文件与仓库源**逐字节一致**，`package.json` + 6 个目录 meta 到位 |
| skills 同步 | `.workbuddy/skills/unity-cmd/` 出现 `scripts/send.py`、`scripts/unity_mcp_server.py`、`SKILL.md` |
| 重装幂等 | 新增 0 / 更新 0 / 未变 65 |
| 目标已有 `.meta` 的 GUID | 保留（手工改成 `deadbeef...` 后重装未被覆盖） |
| 版本守卫 | 把工程里清单改成 v2.0 后装 v1.0 → 报警并能说出两边版本 |
| 仅凭分发目录部署 | 把 4 个文件拷到别处，只跑那里的 `install.py` → 正常完成（不依赖仓库） |
| `verify_unitypackage.py --with-ui` | 无头 Unity 2022.3.62f2c1 导入 → Assets 下 137 文件、关键文件 5/5、**error CS 0 条** |

最后一条是跨版本的证据：源工程是 2021.3.16f1c1，包在 2022.3.62f2c1 上同样能导入并编译。

## 遗留

- `deploy.py`（本机多工程同步）**仍然不写** `<工程>/.workbuddy/skills/unity-cmd/`，
  所以漂移只被安装器这一路堵住了。要彻底根治得给 `deploy.py` 补同样一段。
- `~/.workbuddy/mcp.json` 里 5 条 unity-cmd 仍是各自独立的工程绑定，未合并成单 server。
  合并的前置条件：工具签名必须带强制 `project` 参数，多实例可达且未指定时直接失败——
  否则会把「结构上不可能连错」退化成「靠参数约束」。
- 包内文件按 LF 写入。目标工程工作区是 CRLF 时 git 会显示全量改动（不影响编译）。
