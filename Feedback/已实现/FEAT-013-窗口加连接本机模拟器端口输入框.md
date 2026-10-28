---
id: FEAT-013
type: feature
title: 窗口加「连接本机模拟器」端口输入框
status: done
source: Aketsuki_Unity_MCP
created: 2026-10-28
updated: 2026-10-28
files: AdbDebugger/AdbDebugWindow.cs, AdbDebugger/AdbDevices.cs, AdbDebugger/AdbDebuggerSettings.cs, AdbDebugger/README.md, McpBridge/SKILL.md
proposal: false
---

## 背景

用户 2026-10-27：*「我用的 Mumu 模拟器得用 `adb connect 127.0.0.1:7555`（上一次用的，下一次不一定是这个）。
模拟器启动时似乎不会第一时间被发现。」*

根因不是扫描慢：**模拟器不是 USB 设备，没人 `adb connect` 它就不会出现在 `adb devices` 里**。
原来的窗口只有「手动输入序列号」，那只写设置、不发 connect，所以模拟器用户没有入口。

## 做法

窗口设备段新增区块「连接本机模拟器（adb connect）」，默认展开：

| 元素 | 行为 |
| --- | --- |
| 端口输入框 | 只填 `7555` 就补成 `127.0.0.1:7555`；直接粘 `192.168.1.20:5555` 也照用 |
| 「连接」按钮 | 后台跑 `adb connect`，期间禁用并显示「连接中」 |
| 结果提示 | 成功 Info、失败 Warning；失败文案带上「模拟器没启动 / 端口错 / adb 版本冲突」三种常见原因 |
| 记忆 | 端口存 EditorPrefs **全局键**（机器事实，不按工程），下次开窗自动带出 |

连接成功后自动做两件事：把「设备」切到该地址（`ApplySerial`，正在抓取会重连）、触发重扫列表。

线程模型：`AdbDevices.RequestConnect()` 起后台线程跑进程（`adb connect` 打空端口要等 TCP 超时，
同步跑会冻住窗口），结果放静态字段，UI 每帧用 `TryTakeConnectResult()` 取走。
后台线程不碰 EditorPrefs / Repaint。

成败判定按 adb 的自然语言输出（它没有可靠退出码）：命中 `cannot connect` / `failed to connect` /
`unable to connect` / 中文「无法连接」「积极拒绝」→ 失败；命中 `connected to` → 成功
（`already connected to` 也算）。

## 没做的两件事

1. **自动探测常见端口**（7555 / 16384 / 5555 / 62001 …）——用户这一轮只要输入框。
   真要做得挂在「列表为空每 5 秒补扫」上并加节流，属于下一轮。
2. **指令侧的 connect 能力**——`adb.*` 五条没有这个动作。SKILL.md 里写明了：
   `adb.devices` 返回空时让用户去窗口点，不要反复重试。

## 执行结果（2026-10-28）

| 验证项 | 结果 |
| --- | --- |
| `Tools/compile_adb.py --project .` | ✅ 通过 |
| 部署 project_island_back（3 个 .cs + README） | ✅ 20 文件与仓库一致 |
| island_back 真实重编译 | ✅ ok / 0 错 0 警 / 3719 ms |
| SKILL.md 同步到 island_back 技能目录 | ✅ 3 文件一致 |
| 窗口交互（填 7555 点连接） | ⏳ 需要用户点一次，机器没法代劳 |

版本仍是 **2.1**（2.1 的包刚发出去，这一条改动尚未进包；要发布得重打 2.2）。
