---
id: IMPR-014
type: improve
title: 端点发现已命中自证身份的编辑器仍把 9 个空端口各等 2 秒，一条指令要多花 18 秒
status: pending
source: project_island_back
created: 2026-10-27
updated: 2026-10-27
files: McpBridge/send.py
proposal: true
---

## 症状

每一次 `send.py <任意子命令>` 都要先做端点发现，实测**固定 18 秒左右**，与指令本身无关：

```
status（只探测一次）      退出码=0 墙钟=18.35 s
seq 单步（无等待）        退出码=0 墙钟=18.49 s
```

`resolve` 的输出说明钱花在哪：

```
候选端点 10 个（* 为最终选择）：
  * http://127.0.0.1:57833/      信息文件   pid=5084 role=editor interactive=yes project=project_island_back
    http://127.0.0.1:57834/      端口扫描   不可达（10061 拒绝）
    …57835 … 57842 共 9 个        端口扫描   全部不可达
```

信息文件那一条**第一个就被探测了、而且身份完全对上**（role=editor、interactive、projectPath 匹配），
但客户端仍然把端口避让区间里剩下的 9 个端口挨个连了一遍，每个等约 2 秒。

## 复现

```bash
python Assets/Editor/ExternalCommandSystem/McpBridge/send.py \
       --project-root D:/Wrok_proj/project_island_back status
```

期望：信息文件命中且自证为交互式编辑器、projectPath 与本工程一致 → 直接用，秒回。
实际：18 秒后才返回，中间 9 次连接失败。

`--url` / `--no-resolve` 能绕开，但 MCP 桥和多工程环境下不能总靠人记得加参数。

## 待拍板

| | 做好处 | 代价 |
| --- | --- | --- |
| 做 | 一条指令从 18 秒降到 1 秒内；44 条 typed 工具的每次调用都受益 | 改的是**安全底线**：现在的"结构上不可能连错工程"部分靠的是把候选全探一遍。提前收手 = 主动放弃这份保险 |
| 不做 | 零风险 | 每次调用白等 18 秒，MCP 会话里更明显（每次工具调用都重跑发现） |

**我的倾向**：做，但把条件收得很窄——**只有三条同时满足才提前收手**：

1. 候选来自**本工程的信息文件**（`.http-info.<本工程hash>`，不是端口扫描、不是别的工程的副本）；
2. `/v1/status` 的 `identity` 明确 `role=editor` 且 `interactive=true`；
3. `identity.projectPath` 与 `--project-root` **规范化后完全相等**（不做"看起来像"的模糊匹配）。

任何一条不满足就退回现有全量探测。这样"连错工程"仍然在结构上不可能：
信息文件带工程 hash，端口被别的工程顶替时 `projectPath` 对比会失败、照样走全量探测。

**用户批注**：（等待）
