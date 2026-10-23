# Aketsuki_Unity_MCP

自制练手用 MCP，纯兴趣产品。

把 Unity 编辑器变成一个可被 AI 客户端直接驱动的 MCP 服务：**Python stdio 桥**（MCP 协议 ⇄ HTTP）
夹在中间，Unity 一侧是一个**只监听 127.0.0.1 的 HTTP 指令服务**，把每条 `[Command]` 自动映射成
一个 MCP 工具。

**本仓库只有这套工具**：没有任何游戏逻辑、场景、美术资源、第三方游戏依赖。

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
│   ├── 未实现/  已实现/              移动文件 = 改状态
│   └── 索引.md                       由 Tools/feedback_index.py 生成
└── Tools/
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
真正的接口细节、指令清单、新增指令的写法、`.unitypackage` 打包与校验，全部在
[`Assets/Editor/ExternalCommandSystem/README.md`](Assets/Editor/ExternalCommandSystem/README.md)。

## 打包分发

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

```bash
python Tools/feedback_index.py           # 重建 Feedback/索引.md，并校验状态/编号/必填字段
python Tools/feedback_index.py --check   # 只校验（可挂提交前）
```

规则细节见 [`Feedback/README.md`](Feedback/README.md)，新建一条从 [`Feedback/_模板.md`](Feedback/_模板.md) 复制。
