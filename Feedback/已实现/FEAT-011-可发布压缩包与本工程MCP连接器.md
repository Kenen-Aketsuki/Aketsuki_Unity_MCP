---
id: FEAT-011
type: feature
title: 分发压缩包补上 AI 侧工具，两个工具各出一份可直接发出去的 zip
status: done
source: Aketsuki_Unity_MCP
created: 2026-10-27
updated: 2026-10-27
files: Tools/build_unitypackage.py, README.md, HANDOVER.md
proposal: false
---

## 需求

> 加一个指向本工程的（MCP 连接器）。然后给 ADB 和 MCP 各打一个可以发布的压缩包，
> 包内包含 Unity 侧工具，AI 侧工具（如果有），以及部署文档。

两件事都做在同一轮里：一件是本机环境（`~/.workbuddy/mcp.json`，不入库），
一件是打包器（入库）。

## 一、本工程的 MCP 连接器

`mcp.json` 里原来没有任何指向本仓库的条目，`unity-cmd` 那条写死指向
`CivelRev/Demo/CivilizationRevival`，而那台编辑器早退了 —— 于是第一次调 `unity_status` 报
"没有找到 projectPath 与指定工程一致的编辑器"。

新增 `unity-cmd-aketsuki`，并把桥脚本按其它工程的惯例放到
`<工程>/.workbuddy/skills/unity-cmd/`（`scripts/unity_mcp_server.py`、`scripts/send.py`、`SKILL.md`）。
`.workbuddy/` 已在 `.gitignore` 里，不会污染仓库。

验证：直接起该脚本说 MCP 协议（`initialize` → `notifications/initialized` → `tools/list`），
返回 **70 个工具**，其中 5 个 `unity_adb_*`。

**这一步机器做不完**：连接器要在管理页点一次「信任」才会被拉起，包和脚本都替不了。

## 二、可发布压缩包

`--bundle` 以前只产出一个目录（`.unitypackage` + `install.py` + `DEPLOY.md` + `manifest.json`），
AI 侧工具虽然在 `.unitypackage` 里躺着（`McpBridge/`），但那是"随工程走"的资产副本，
接收方看不出哪份是给 AI 用的、也不知道要落到 `.workbuddy/skills/` 去。

现在 `--bundle` 额外做两件事：

1. **拷一份 AI 侧到 `ai/`**：`scripts/unity_mcp_server.py`、`scripts/send.py`、`SKILL.md`，
   保持技能目录的相对形态。源码取 `BRIDGE_SOURCES`（可能来自 Assets，也可能只在技能目录里），
   与包内 `McpBridge/` **逐字节同源**（sha256 比对过）。
2. **压成同名 zip**：根目录名 = 目录名，解压即得整个包。

最终结构：

```
Aketsuki_Unity_MCP-1.2.zip
└── Aketsuki_Unity_MCP-1.2/
    ├── Aketsuki_Unity_MCP-1.2.unitypackage   ← Unity 侧工具（69 资产）
    ├── ai/                                   ← AI 侧工具（MCP 才有）
    │   ├── scripts/unity_mcp_server.py
    │   ├── scripts/send.py
    │   └── SKILL.md
    ├── install.py                            ← 部署工具
    ├── DEPLOY.md                             ← 部署文档
    └── manifest.json                         ← 清单（含 aiSide 字段）
```

ADB 包**没有** `ai/`：它不提供 MCP 服务、不开端口、不装技能目录，AI 侧入口是指令系统里的
`adb.*` 五条指令（反射调 `AdbExternalApi`）。`manifest.json` 里记 `"aiSide": null`，
`DEPLOY.md` 里明写「本包没有 AI 侧，不是漏打包」——免得接收方以为是打包漏了。

## 为什么必须发 zip，不能只发 `.unitypackage` 或目录

- 光一个 `.unitypackage`：接收方得自己拆 tar.gz、按 guid 拼 `pathname`，还要知道
  "目标工程已有的 `.meta` 不能覆盖（保住 GUID）"。手写这个的人十有八九会漏。
- 光一个目录：邮件/网盘里会被拆散；`.meta` 是点开头文件，Windows 资源管理器默认不显示，
  手工重打包时最容易被跳过 —— 少了 `.meta`，Unity 会给每个脚本重新生成 GUID，**场景引用全丢**。

## 验证

| 项 | 做法 | 结果 |
| --- | --- | --- |
| zip 完整性 | `ZipFile.testzip()` | 两个包都返回 `None`（无损坏） |
| ADB 包内容 | 解压 → 列目录 | 4 个文件（包 / DEPLOY / install / manifest），**无 `ai/`** |
| MCP 包内容 | 解压 → 列目录 | 7 个文件（包 / DEPLOY / install / manifest + `ai/` 3 个） |
| ai 与包内桥同源 | sha256 比对 3 个文件 | 全一致 |
| 安装（MCP） | 解压后的 `install.py` 装进空工程 | 资产新增 69；`.workbuddy/skills/unity-cmd/` 新增 3 |
| 安装（ADB） | 同上 | 资产新增 11；**不创建 `.workbuddy`**；重装幂等（未变 11 / 新增 0） |
| 结构校验 | `verify_unitypackage.py --module adb` | 通过，24 文件（含 `.meta`），关键文件 1/1 |
| 连接器 | stdio `tools/list` | 70 工具，含 5 个 `unity_adb_*` |

MCP 包的 `--with-ui` 有头校验这轮**没跑**：它要起真实 Unity 导入，上次就在这一步卡死过
（孤儿 `bee_backend` 占 DAG 锁）。改的只是分发目录布局，`.unitypackage` 内容与上一版一致
（69 文件），装空工程 + 逐字节比对已覆盖。

## 边界

1. `ai/` 是**副本**，不是软链。改了 `McpBridge/` 下的桥，必须重新 `--bundle`，否则 `ai/` 是旧的。
2. zip 里不含 `.git`、不含 `Project/`（工程客制化在打包阶段就被三次断言挡掉）。
3. `install.py` 只认自家目录里的 `.unitypackage`；解压后**别把包挪出去**，挪了要 `-p` 指定。
4. 包版本号与 `.unitypackage` 版本一致；这轮只改分发布局，版本没动（MCP 1.2 / ADB 2.0）。
