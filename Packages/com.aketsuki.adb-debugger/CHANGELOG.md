# Changelog

本包遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [2.2.0] - 2026-10-29

### Changed

- **打包形态改为 UPM 包**：从 `Assets/Editor/AdbDebugger/`（资产形态）改为
  `Packages/com.aketsuki.adb-debugger/`（embedded package）。安装方式从"拷资产"变成
  Package Manager / manifest 写 `"com.aketsuki.adb-debugger": "file:..."`。
- **命名空间与程序集改名**：`CivilRev.AdbDebugger` → `Aketsuki.AdbDebugger`，
  asmdef `CivilRev.AdbDebugger.Editor` → `Aketsuki.AdbDebugger.Editor`。
  `AdbExternalApi.ApiTypeName` 随之变为 `Aketsuki.AdbDebugger.AdbExternalApi`。
  配套的「Unity MCP 指令系统」`AdbBridgeAdapter` 已同步改两条常量，**两边必须同版本安装**。
- **`EditorPrefs` 前缀保持 `CivilRev.AdbDebug.` 不变**：那是存储契约，改了等于把所有工程的
  设备 / tag / 端口 / 开关一次性重置。

### Added

- 包根补 `package.json`、`CHANGELOG.md`、`LICENSE.md`（Apache-2.0）。

### 历史

2.2.0 之前的版本是 `.unitypackage` 资产形态发布（1.0 / 2.0 / 2.1），行为与 2.2.0 相同，
仅安装形态与命名空间不同。
