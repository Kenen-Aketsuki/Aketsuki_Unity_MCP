---
id: FEAT-010
type: feature
title: 可选插件的可用性判定：没装就不注册，不出现在指令列表里
status: done
source: Aketsuki_Unity_MCP
created: 2026-10-27
updated: 2026-10-27
files: Assets/Editor/ExternalCommandSystem/Core/ICommandAvailability.cs, Assets/Editor/ExternalCommandSystem/Core/OptionalDependency.cs, Assets/Editor/ExternalCommandSystem/Core/CommandRegistry.cs, Assets/Editor/ExternalCommandSystem/Core/CommandRunner.cs, Assets/Editor/ExternalCommandSystem/Core/CommandResult.cs, Assets/Editor/ExternalCommandSystem/Commands/Adb/AdbBridgeAdapter.cs, Assets/Editor/ExternalCommandSystem/Commands/Adb/AdbCommands.cs, Assets/Editor/ExternalCommandSystem/README.md, Assets/Editor/ExternalCommandSystem/McpBridge/SKILL.md
proposal: false
---

## 需求

> 对于 MCP 侧的 ADB 指令插件，需要有个办法判定其是否可用。如果不可用，那么不要在指令列表中出现。

> 留一些扩展性，以后可能会有别的同类插件。

FEAT-009 把 `adb.*` 做成了反射适配层：没装 ADB 调试台时**编译不受影响**，但五条指令照样注册，
调了才报"没找到 ADB 调试台"。这留下一个更糟的后果——`unity_list_commands` 里能看到
`unity_adb_devices`，AI 以为能抓日志，调了才发现要装包，然后花几轮去验证是不是自己参数写错了。
**列表里挂着一个必然失败的工具，比它不出现更误导。**

## 决策

| 选项 | 结论 |
| --- | --- |
| 执行期报错（FEAT-009 现状） | 不选。列表骗人，且要 AI 靠试错发现 |
| 在四个出口（`/v1/commands`、控制台窗口、MCP 工具列表、`unity_list_commands`）各过滤一遍 | 不选。四份状态必然漂移 |
| **注册期判定，不可用就不进注册表** | **采用**。出口全从 `CommandRegistry.All` 派生，天然一致 |
| 在 `[Command]` 上加 `RequiresType = "类型全名"` 字符串 | 不选。写死的全名跟代码不同步（重命名不会有编译错误），且只覆盖"类型在不在"，覆盖不了"签名对不对" |
| 加 `ICommandAvailability` 接口 + `OptionalDependency` 反射句柄 | **采用**。判定逻辑归指令自己写，注册表只负责问一次 |

## 实现

### 一、协议：`Core/ICommandAvailability.cs`

```csharp
public interface ICommandAvailability
{
    string GetUnavailableReason();   // null / 空 = 可用；否则是"照着能做"的原因
}
```

注册表在 `Register()` 里问一次，实现了接口且返回非空 → **不注册**，原因记进 `_skipped`。

### 二、工具：`Core/OptionalDependency.cs`

把反射样板固定下来，ADB 侧的 `AdbBridgeAdapter` 从"自己拼 typeName + GetMethod + 包异常"缩成
一段声明 + 一层子命令分发。

```csharp
internal static readonly OptionalDependency Plugin = OptionalDependency.Static(
    "CivilRev.AdbDebugger.AdbExternalApi",     // 类型全名
    "CivilRev.AdbDebugger.Editor",             // 程序集名
    "Invoke",                                  // 静态方法名
    new[] { typeof(string), typeof(IDictionary<string, string>) },
    "本体要另装「ADB 调试」那个包；装好后菜单在 Tools / ADB 调试 / ADB 调试台。");   // 装包提示
```

- `Available` / `UnavailableReason` / `Invoke` / `TryInvoke`，解析一次缓存；
- 判定是「类型解析到了 **并且**目标方法签名对得上」——比只看类型严，插件版本过旧同样算不可用；
- 报错会把 `TargetInvocationException` 剥一层，否则只会看到"调用的目标发生了异常"。

同文件里还有个 `OptionalDependencyCommand` 基类：

```csharp
public abstract class AdbCommandBase : OptionalDependencyCommand
{
    protected override OptionalDependency Dependency => AdbBridgeAdapter.Plugin;
}
```

**加同类插件就是这三步**（另一个文件里也只剩"声明句柄 + 子命令分发"），详见
`Assets/Editor/ExternalCommandSystem/README.md` §5.5。

### 三、注册表：`Core/CommandRegistry.cs`

| 改动 | 为什么 |
| --- | --- |
| `_skipped` + `Skipped` + `SkipReason(id)` | 剔除不是"悄悄消失"——要知道少了什么、为什么少 |
| `Add()` 成功时清 `_skipped[id]` | 装了包重新扫描后，旧原因不能继续骗人说"不可用" |
| `Clear()` 一并清 `_skipped` | 同 `_map` / `_order` |
| `LogSkipped()` 在扫描结束时汇总一条 Info | 用 Info 不用 Warning：可选包没装是正常状态，不是错误 |
| 汇总**按原因分组** | ADB 是 5 条同一句原因，不分组就是刷 5 行一样的字 |

日志长这样：

```
[Cmd] 已跳过 5 条依赖未就绪的指令（不会出现在指令列表里）：
  adb.devices、adb.logcat.start、adb.logcat.stop、adb.logcat.status、adb.logcat.read — 没找到 CivilRev.AdbDebugger.Editor 的程序集（或其中没有 CivilRev.AdbDebugger.AdbExternalApi）。这几条指令只是适配层……
```

### 四、报错文案：`CommandRunner` + `CommandResult.Unknown`

硬调一条被剔除的指令，原来只会得到 `未注册的指令 ID：adb.devices`——和"ID 拼错"长得一模一样。
现在：

```
指令 'adb.devices' 当前不可用，已从指令列表里剔除：没找到 CivilRev.AdbDebugger.Editor 的程序集（…）。这几条指令只是适配层……
```

（`CommandResult.Unknown(id, message)` 新增重载，原 `Unknown(id)` 行为不变。）

## 验证

用 .NET 探针编**真实源文件**跑两个场景，同一份代码靠 `AssemblyName` 切换「已装 / 没装」：

| 场景 | 做法 | 结果 |
| --- | --- | --- |
| 没装 | 程序集叫 `AdbAvailProbe`，不含 `AdbExternalApi` | 5 条 `adb.*` 都不在 `All`；`SkipReason` 点名 ADB 调试台；汇总日志按原因分组；`All` 里没有任何 `adb.` 前缀 |
| 已装 | 程序集名冒充 `CivilRev.AdbDebugger.Editor` 并编入桩 `AdbExternalApi` | 5 条都在 `All`；`SkipReason` 为 null；汇总日志里没有 `adb.` |
| 机制 | 自造四类指令 | 可用→注册；不可用→剔除且原因可查；**探测抛异常→剔除且原因含"可用性判定失败"**；不实现接口→不受影响 |
| 回归 | 先不可用、后改可用再 `Register` | 第二次注册成功，且旧原因被清空 |
| 回归 | `Clear()` | `Skipped` 归零 |

两场景各 19 项断言全部通过。`Assembly-CSharp-Editor` 离线编译通过（64 个文件）。

**探针踩到的坑（值得记）**：探针第一版没定义 `UNITY_EDITOR`，五条指令永远不注册——
`Register()` 里有一段 `#if !UNITY_EDITOR` 的第二层保险会静默丢掉 `EditorOnly` 的指令。
纯 .NET 探针验证编辑器代码时必须补 `DefineConstants=UNITY_EDITOR`，否则验的是另一个程序的行为。

## 边界

1. **一次域重载判一次**。装包 → Unity 重新编译 → 域重载 → 重新扫描，指令自动出现，不用重启编辑器。
   反过来（卸载包）同理。
2. 判定本身抛异常时**按不可用处理**：连"能不能用"都问不出来的指令，放进去只会在执行期炸同一个异常。
3. `Skipped` / `SkipReason` **只用于日志与报错**，不要拿它拼指令列表——这些指令就是不该出现。
4. 判定必须便宜且无副作用（每次扫描都会调），不要在 `GetUnavailableReason()` 里跑进程或读大文件。
5. 目前只有 `adb.*` 五条走这套机制；`AddressablesBridge` 等其它可选依赖**没改**——
   它们的失败是"指令存在但调用报错"，要不要一起收敛是另一件事，不在本轮范围。

## 真机端到端复验（2026-10-27 18:17，Aketsuki_Unity_MCP 编辑器）

上面那轮是离线探针。本工程的编辑器起来之后，用真实 HTTP / MCP 链路又跑了一遍（这台工程**装了**
ADB 调试台，所以验的是"可用就出现"那一侧；"没装就剔除"仍以探针为准，见顶部表格）：

| 环节 | 命令 / 请求 | 结果 |
| --- | --- | --- |
| 指令列表 | `GET /v1/commands` | 65 条，其中 5 条 `adb.devices` / `adb.logcat.start|stop|status|read` |
| 设备扫描 | `adb.devices` | 36 ms，1 台可用：`V2241A（emulator-5554）`，adb 来自编辑器自带 AndroidPlayer SDK |
| 抓取启动 | `adb.logcat.start {tag:Unity, level:I, backfill:50}` | 实际命令 `adb logcat -v threadtime -T 50` |
| 计数 | `adb.logcat.status` | 8 秒收到 191 / 打印 111 / tag 过滤 80 / 丢弃 0 |
| 取走即删 | `adb.logcat.read {max:5}` → 再读 3 条 | 第一次 5 条、缓冲 110；第二次是新行，前 5 条不再出现 |
| 停止 | `adb.logcat.stop` | 报"缓冲里还有 111 条没被取走" |
| MCP 工具列表 | `unity_mcp_server.py` 走 stdio 的 `tools/list` | 70 个工具，含 `unity_adb_devices` 等 5 个 `unity_adb_*` |
| MCP 调用 | `tools/call unity_adb_devices` | 24.9 ms，返回同一台设备 |

**未覆盖**：没有一台"活着且没装 ADB"的编辑器，"没装 → MCP 工具列表里查不到"这一条仍是推演
（工具表由 `/v1/commands` 生成，而后者只读 `Registry.All`，所以剔除必然传导），不是实测。
要做实测就临时把 `Assets/Editor/AdbDebugger` 移出 Assets 重编一次，代价是两次域重载。
