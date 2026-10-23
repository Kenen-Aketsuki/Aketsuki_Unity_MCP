# 指令系统（CommandSystem）

一个"用 JSON 文件驱动 Unity 执行操作"的**编辑器专用**扩展。
核心目标：**新增一种操作 = 新增一个类**，不需要改动任何已有代码。

- Unity 版本：2021.3（仅依赖 `System.IO` / `UnityEngine` / `UnityEditor`，无第三方库）
- 命名空间：`CivilRev.CommandSystem`（编辑器扩展为 `CivilRev.CommandSystem.Editor`）
- **整套代码位于 `Editor/` 目录下，不会打进游戏包**（见 [1.1 打包隔离](#11-打包隔离编辑器专用)）

> **本仓库形态（2026-10-23 从游戏工程剥离）**：仓库里只有这套指令系统 + MCP 桥，
> **不含任何工程运行期代码**，`Project/` 为空占位。下文举例出现的 `CivelRev` / `Demo` /
> `UIManager` / `Assets/GameAssets/...` 都是剥离前的历史背景，在本仓库中不存在；
> 指令目录、信息文件位置一律按本文 §2.1 / §3 的路径规则在运行时推导，与本仓库无关。

---

## 1. 整体结构

```
Assets/Script/ExternalCommandSystem/
├── README.md
└── Editor/                     # ← 关键：Unity 的 Editor 特殊目录，整体不参与打包
    ├── Json/                   # 自研轻量 JSON（JsonUtility 无法表达任意结构参数包）
    │   ├── JsonValue.cs        值模型（对象/数组/字符串/数值/布尔/null）
    │   ├── JsonParser.cs       递归下降解析（支持注释、尾随逗号）
    │   ├── JsonWriter.cs       序列化
    │   └── JsonMapper.cs       JSON <-> C# 对象反射映射（含 Vector2/3/4、Color）
    ├── Core/
    │   ├── EditorOnlyGuard.cs  【打包保险】万一被挪出 Editor 目录，打玩家包时直接编译报错
    │   ├── CommandAttribute.cs 指令 ID 标记
    │   ├── ICommand.cs         指令接口
    │   ├── CommandArgs.cs      参数包（强类型读取 / 必填校验）
    │   ├── CommandContext.cs   上下文（参数、变量表、层级、DryRun）
    │   ├── CommandResult.cs    执行结果
    │   ├── CommandRegistry.cs  【注册表】ID -> 指令类
    │   ├── CommandReader.cs    【模块一】读取 .json 指令文件
    │   ├── CommandRunner.cs    【模块二】解析并执行指令
    │   ├── CommandPathUtil.cs  路径解析（绝对 / 工作空间 / 项目根 / Assets 相对）
    │   ├── SceneQuery.cs       场景节点查找（名字 / 层级路径 / 组件类型，含 inactive 与多场景）
    │   ├── HttpProtocol.cs     【HTTP 协议层】路由 / 令牌校验 / 响应构造（纯托管，可离线测试）
    │   └── CommandLog.cs       日志出口（可重定向）
    ├── Commands/               # 内置指令（全部）
    │   ├── LogCommand.cs        log
    │   ├── BatchCommand.cs      batch（嵌套执行）
    │   ├── RunFileCommand.cs    file.run（含循环引用检测）
    │   ├── FileIoCommands.cs    file.write / file.read
    │   ├── VariableCommands.cs  var.set / var.get / var.dump
    │   ├── GameObjectCommands.cs  gameobject.create / gameobject.destroy / gameobject.instantiate
    │   ├── SceneQueryCommands.cs  gameobject.find / scene.hierarchy（场景查询）
    │   ├── ComponentCommands.cs   component.setField（含节点引用解析）
    │   ├── ListCommandsCommand.cs commands.list
    │   ├── EditorCommands.cs    asset.refresh / asset.save / scene.save / asset.compile
    │   ├── SceneCommands.cs     scene.open（带 30s 许可弹窗）/ scene.list
    │   ├── PermissionWindow.cs  模态许可弹窗（允许/拒绝 + 倒计时，超时自动拒绝）
    │   ├── AssetImportCommand.cs  asset.importSprite（项目外图片导入为 Sprite，支持九宫格）
    │   ├── DiagnosticsCommands.cs  asset.compileReport / console.read（编译反馈闭环）
    │   ├── PrefabCommands.cs     ui.savePrefab（场景节点保存为 Prefab）
    │   ├── PrefabEditCommands.cs  prefab.addNode / prefab.duplicateNode / prefab.tree / component.add（Prefab 资产编辑）
    │   ├── EditorStateCommands.cs  editor.play / editor.stop / editor.state（Play 模式受控进出与状态查询）
    │   ├── ComponentGetCommands.cs  component.get / scene.inspect（字段读取，SerializedObject 全量快照）
│   ├── CleanupCommand.cs        command.cleanup（清理指令目录下已执行/归档的文件）
    │   ├── AddressablesCommands.cs  addressables.addEntry / removeEntry + AddressablesBridge 反射桥（未装包的工程报错不炸编译）
    │   ├── UiCommands.cs        ui.canvas / ui.uiRoot / ui.node / ui.image / ui.text / ui.button
    │   ├── TmpTextCommand.cs    ui.tmpText（TextMeshPro 文本）
    │   ├── UiAssetUtil.cs       按 Assets 路径加载 Sprite / Font / TMP 字体
    │   └── UiNodeFactory.cs     UI 节点构建工具（布局、配色、字体、撤销收尾）
    ├── CommandAutoRunner.cs     编辑器入口：指令注册（TypeCache）+ 手动执行（控制台 / 菜单）
    ├── CommandHttpServer.cs     HTTP 指令服务：监听 / 跨线程队列 / 主线程泵（含生命周期挂钩）
    ├── CommandSystemSettings.cs 设置（EditorPrefs 持久化）
    └── CommandToolsWindow.cs    控制台窗口
```

### 1.1 打包隔离（编辑器专用）

本工具依赖 `UnityEditor`（`Undo` / `AssetDatabase` / `EditorApplication` / 场景操作），
且设计目标就是"给编辑器用"，因此做了三层隔离：

| 层 | 机制 | 作用 |
| --- | --- | --- |
| 一 | 所有源码都在 `ExternalCommandSystem/Editor/**` 下 | Unity 把 Editor 目录编译进 `Assembly-CSharp-Editor`，**永不参与打包**（这是主要保证） |
| 二 | `Core/EditorOnlyGuard.cs` 里的 `#if !UNITY_EDITOR #error` | 万一有人把源码挪出 Editor 目录，打玩家包时会**直接编译报错**，而不是静默进包 |
| 三 | `CommandRegistry` 里的 `#if !UNITY_EDITOR` 分支 | 极端情况下仍会跳过依赖 UnityEditor 的指令注册 |

> 原 `Core/CommandRuntimeBootstrap.cs`（`[RuntimeInitializeOnLoadMethod]` + `StreamingAssets/commands/startup.json`
> 的运行期启动钩子）**已删除**——它是唯一有意在包内运行的部分，与"编辑器专用"冲突。
> 需要编辑器启动时自动执行，请用控制台窗口的 **扫描目录** 或设置里的"脚本重载后执行"。

新增文件时请务必放进 `Editor/` 目录下；如果为了复用把某段代码搬到 Assets 其它位置，
它就会被打进包里，届时需要自行剥离 UnityEditor 依赖。

两个模块的分工是严格的：

| 模块 | 职责 | 不做什么 |
| --- | --- | --- |
| 指令读取 `CommandReader` | 路径解析、`File.ReadAllText`、JSON -> `CommandScript` | 不知道任何指令怎么执行 |
| 指令执行 `CommandRunner` | 查注册表、建实例、传参、捕获异常、统计报告 | 不知道任何具体业务 |

---

## 2. 快速开始

1. 打开菜单 **Tools / 指令系统 / 控制台**。
2. 在"文件路径"里填入 `ui_modal.json`（相对路径会自动定位到默认指令目录），点 **执行**（可先点"空跑"预览）——会在当前场景里生成一个经典模态框。也可以点窗口里的 **打开指令目录** 按钮直接跳到目录。
3. 或者：由外部客户端（例如 AI 助手用的 Skill）通过 **HTTP 指令服务** 直接 POST 指令；文件监听已被移除，不再有"保存即执行"。

命令行 / 代码内调用：

```csharp
using CivilRev.CommandSystem;

// 读取并执行一个文件（相对默认指令目录、相对项目根、绝对路径都可以）
var report = new CommandRunner().RunFile("ui_modal.json");
Debug.Log(report.ToSummary());

// 直接执行单条指令
new CommandRunner().RunSingle("log", JsonValue.Parse("{\"message\":\"hello\"}"));
```

---

## 2.1 HTTP 指令服务（文件监听的替代通路）

除了"改文件触发"，还可以把编辑器变成一个**只监听本机的 HTTP 端点**，由外部程序直接 POST 指令、
同步拿回结构化执行报告。这是给 AI / 脚本客户端用的高速通路。

### 怎么开

控制台窗口（**Tools / 指令系统 / 控制台**）的「HTTP 指令服务」区有 **启用 HTTP 监听** 开关（持久化，按工程记忆）：
打开即尝试拉起监听并写回本工程设置，关闭即停止并清掉启用标记。开关下方另有一个 **启动/停止** 按钮，
用于在不改变长期开关的情况下对当前实时监听做即时启停。也可以用菜单 **Tools / 指令系统 / HTTP 指令服务** 切换。

编辑器启动与每次域重载后，会**按本工程的「启用」设置自动恢复**（`CommandHttpServerBoot` 的静态构造调用
`RestoreFromSettings`，仅当 `HttpServerEnabled` 为 true 时才拉起），正常情况下无需人工点击；
想彻底关掉就用上面的开关（关掉后即使重载也不会自启）。
启动时会自动生成访问令牌，并写进
`<工作空间>/.workbuddy/UnityEditCommand/.http-info`——**客户端从那里读端口和令牌，不要硬编码**，
端口被占用时服务端会自动向后避让。

> 旧的「文件状态监听」（FileSystemWatcher + 去抖 + 去重）已被移除，下发指令改走 HTTP 通路，
> 不再有"保存即自动执行"；需要批量执行目录里的文件时，用控制台窗口的 **扫描目录**。

### 端点

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| GET | `/v1/ping` | 存活检测，返回协议版本与端口 |
| GET | `/v1/status` | `compiling` / `updating` / `queue` / `busy` / `last` |
| GET | `/v1/commands` | 已注册指令列表（含 `editorOnly`） |
| POST | `/v1/execute` | 下发指令；body 就是指令文件的 JSON 文本 |

请求头必须带 `X-Cmd-Token`。`/v1/execute` 支持 `?dryRun=1` 只校验不执行。

HTTP 状态码只表示"请求是否被处理"，**业务结果看 body 里的 `ok`**：

| 状态码 | 含义 |
| --- | --- |
| 200 + `ok:true` | 全部指令成功 |
| 200 + `ok:false` | 有指令失败，看 `records` 里非 `Success` 的条目 |
| 400 `bad_json` | body 不是合法指令 JSON（报错带行列号） |
| 401 `unauthorized` | 令牌缺失或错误 |
| 404 / 405 / 413 | 路径错 / 方法错 / body 超 4 MB |
| 503 `compiling` / `updating` | Unity 正在编译或刷新，**等**，别重试轰炸 |

### 为什么这样设计

| 关注点 | 做法 |
| --- | --- |
| 主线程约束 | HTTP 回调只做解析与鉴权；执行请求入 `ConcurrentQueue`，由 `EditorApplication.update` 泵在主线程取出，后台线程用 `TaskCompletionSource` 阻塞等待结果 |
| 域重载 | `beforeAssemblyReload` 先把队列里所有等待方以"正在重新编译"终结，避免后台线程永久挂起；新域按设置自动恢复监听 |
| 端口占用 | 从首选端口起逐次 +1，最多试 10 个；信息文件同时写入 `portRangeStart` / `portRangeCount`，客户端据此扫描候选，无需硬编码 |
| 多进程 / 连错端点 | 同一工程可能同时跑编辑器与 `AssetImportWorker` 等 batchmode 进程，后者也加载 Editor 程序集、会起监听但**不跑主循环**，指令会石沉大海。服务端在 `/v1/status` 自报 `identity` 块（pid / role / interactive / projectName），并写 `.http-info.<pid>` 自述副本；客户端据此只连 `interactive=true` 的编辑器，其余避开。batchmode 进程自身检测到 `-batchMode` 直接不启动服务 |
| 安全 | 只绑 `127.0.0.1` + Host 头校验（挡 DNS rebinding）+ 强制令牌 + 默认关闭 |

协议层（`Core/HttpProtocol.cs`）是**纯托管**的，不依赖 `System.Net` 与 Unity API，
因此路由、令牌校验、报告序列化全部能在离线测试工程里验证；真正起监听和管线程的
`CommandHttpServer.cs` 则只做"转换 + 生命周期"。

### 客户端

配套 Skill 在 `<工作空间>/.workbuddy/skills/unity-cmd/`：

```bash
python scripts/send.py status                  # 探活 + 编辑器状态
python scripts/send.py commands                # 列出可用指令
python scripts/send.py send ui_round_menu.json # 下发并等执行报告
python scripts/send.py send ui_round_menu.json --dry-run   # 只校验
```

---

## 3. 指令文件格式

三种写法都支持：

```jsonc
// A. 顶层数组（最简洁）
[
  { "id": "log", "message": "第一条" },
  { "id": "log", "message": "第二条", "level": "warn" }
]

// B. 完整对象（推荐：可带 name / once / version）
{
  "version": 1,
  "name": "示例",
  "once": false,
  "commands": [
    { "id": "log", "message": "hello" }
  ]
}

// C. 单条指令
{ "id": "log", "message": "hello" }
```

### 一条指令的字段

| 字段 | 说明 |
| --- | --- |
| `id` | **必填**，指令唯一字符串 ID（大小写不敏感）；别名 `command` / `type` |
| `params` | 参数对象；别名 `args` / `parameters`。**也可以省略**：此时除保留字段外的所有同级字段都作为参数 |
| `enabled` | 默认 true，false 时跳过 |
| `comment` / `note` | 备注，不参与执行 |

保留字段：`id` `command` `type` `enabled` `disabled` `comment` `note` `description` `params` `args` `parameters`。
（注意 `name` **不是**保留字段，会作为参数传给指令，例如 `var.set` 就要用 `name`。）

也就是说下面两种写法等价：

```json
{ "id": "log", "params": { "message": "hi" } }
{ "id": "log", "message": "hi" }
```

JSON 里可以写 `//` 与 `/* */` 注释，也允许结尾多一个逗号。

### 路径规则（`CommandPathUtil`）

| 写法 | 解析结果 |
| --- | --- |
| `D:/proj/a.json` | 原样使用 |
| `@ws/a.json` | 工作空间根（项目根的上一级，即 `.workbuddy` 所在层） |
| `~/Commands/a.json` | 项目根目录（Assets 的上一级） |
| `Assets/Cfgs/a.json` | 项目根/Assets/Cfgs/a.json |
| `Cfgs/a.json` | 依次尝试 **引用它的指令文件所在目录 → 默认指令目录 → Assets 目录**，取第一个命中的 |

**默认指令目录 = `<工作空间根>/.workbuddy/UnityEditCommand`**（本工程即 `Demo/.workbuddy/UnityEditCommand`，
位于 Unity 项目之外，既不参与资源导入、也不会生成 .meta，且与编辑器工作空间放在一起管理）。

判定规则：项目根的上一级若存在 `.workbuddy` 子目录，则它为工作空间根；否则退化为项目根，
此时默认目录回落到 `<项目根>/Commands`，老工程不受影响。

由 `file.run` / `file.write` / `file.read` 触发的相对路径，会优先相对**引用它的那个 .json 所在目录**解析，
因此同一目录下的指令文件可以直接用文件名互相引用。

---

## 4. 扩展一个新指令

只需两步：写一个类，加一个特性。注册表会在程序集加载时自动发现它。

```csharp
using CivilRev.CommandSystem;

[Command("piece.spawn", Description = "在指定格子生成棋子", Usage = "id: string, x:int, y:int")]
public sealed class SpawnPieceCommand : ICommand
{
    public CommandResult Execute(CommandContext context)
    {
        // 必填参数：缺失会自动抛出 CommandArgumentException，运行器捕获为失败
        var id = context.Args.RequireString("id");

        // 选填参数：缺失返回默认值
        var x = context.Args.GetInt("x", 0);
        var y = context.Args.GetInt("y", 0);
        var position = context.Args.GetVector3("position", UnityEngine.Vector3.zero);

        // 复杂参数：直接映射成对象（嵌套、List、Dictionary、Vector3 都支持）
        var options = context.Args.GetObject("options", new SpawnOptions());

        // 跨指令传递数据
        context.SetVariable("lastSpawn", id);

        // ... 真实业务逻辑 ...

        return CommandResult.Ok($"生成 {id} @ ({x},{y})");
    }
}
```

对应 JSON：

```json
{ "id": "piece.spawn", "params": { "id": "warrior", "x": 3, "y": 5, "options": { "level": 2 } } }
```

约定：

- 指令类必须实现 `ICommand`，且是**非抽象、非泛型**的类；
- ID 全局唯一，重复注册时保留先注册者并输出警告；
- 指令类应无状态：参数从 `context.Args` 读，跨指令数据放 `context.Variables`；
- 依赖 `UnityEditor` 的指令建议加 `EditorOnly = true`（用于控制台分组展示；整套系统已是编辑器专用，不再承担剔除职责）：

```csharp
[Command("asset.refresh", EditorOnly = true)]
```

不想被自动扫描时，也可以手动注册实例（便于依赖注入）：

```csharp
CommandRegistry.Default.RegisterInstance("my.cmd", new MyCommand(dependency));
```

---

## 5. 内置指令

| ID | 说明 | 主要参数 |
| --- | --- | --- |
| `log` | 打印日志，支持 `{变量名}` 替换 | `message`(必填), `level`, `variable` |
| `commands.list` | 列出已注册指令 | `filter` |
| `batch` | 顺序执行内联子指令 | `commands`(必填), `stopOnError` |
| `file.run` | 执行另一个指令文件 | `path`(必填) |
| `file.write` | 写文本文件 | `path`(必填), `content`, `append` |
| `file.read` | 读文本文件 | `path`(必填), `variable`, `log` |
| `gameobject.create` | 创建 GameObject | `name`, `position`, `primitive`, `parent`, `tag` |
| `gameobject.destroy` | 删除 GameObject | `target`(必填) |
| `gameobject.setActive` | 设置节点激活状态（"老模块留着不删只隐藏"的常态需求）；`includeChildren` 可连同子树 | `target`(必填), `active`(默认 true), `includeChildren`(默认 false) |
| `gameobject.rename` | 重命名节点 | `target`(必填), `name`(必填) |
| `gameobject.setProperty` | 设置 **GameObject 自身**属性（`layer` / `tag` / `isStatic`）——这些不在任何组件上，`component.setField` 够不着。`layer` 可传数字 0-31 或层名（`"UI"`），`includeChildren` 可连同整棵子树一起刷；返回受影响节点数 | `target`(必填), `layer`, `tag`, `isStatic`, `includeChildren`(默认 false) |
| `gameobject.setParent` | 改变节点父级（RectTransform 用 `worldPositionStays` 语义） | `target`(必填), `parent`(必填), `keepWorldPosition`(默认 true) |
| `gameobject.duplicate` | 在场景内复制节点（含子树，引用默认指向原节点，不做重映射，与 `prefab.duplicateNode` 口径一致） | `target`(必填), `name`(默认 `原名_Copy`), `parent`(默认同级), `includeChildren`(默认 true), `keepWorldPosition`(默认 true) |
| `gameobject.instantiate` | 实例化 Prefab 到场景（保留 Prefab 连接） | `asset`(必填), `parent`, `name`, 布局参数, `select` |
| `gameobject.find` | 按名称/类型/路径查找节点，返回完整路径与组件清单（绑字段前先用它确认目标）；每个节点带 `layer`/`layerName`/`tag`/`isStatic` | `query`(必填), `all`, `maxCount` |
| `scene.hierarchy` | 列出某节点子树（含组件类型、激活状态、`layer`/`layerName`/`tag`/`isStatic`）；不给 `target` 列出所有场景根 | `target`, `maxDepth` |
| `component.setField` | 给任意组件的字段/属性赋值（含 `[SerializeField]` 私有字段）；解析失败**绝不静默置零**。`field` 支持 **`m_` 前缀自动兼容**（写 `m_Enabled` 或 `enabled` 等价）与 **`A.B.C` 嵌套路径**（如 `m_Colors.m_PressedColor`）。`value` 支持：节点/类型选择器/ `asset:` 引用、数字/布尔/字符串/枚举/向量（`[x,y,z]` 或 `{"x":1,"y":1}`）、颜色（`#RRGGBB` / `[r,g,b,a]`）；**结构体整体赋值**（`Color`/`Vector*`/`Rect`/ `ColorBlock`/`SpriteState`/`Navigation`/`AnimationTriggers` 等，传对象按子字段名逐个灌，失败信息会列出可写子字段）。 | `target`(必填), `component`(必填), `field`(必填), `value`, `prefab` |
| `component.get` | 读取组件字段当前值（与 setField 对称）；`field` 省略返回全部可序列化字段；`field` 支持**点号嵌套路径**（`m_Colors.normalColor`，也自动补 `m_` 前缀，写 `colors.normalColor` 同样命中）与旧口径的尾段名；引用返回 `{path,name,type}`；**Play 模式运行时值也读得到** | `target`(必填), `component`(必填), `field`, `prefab` |
| `component.setFields` | 一次写入同组件多个字段（原子：先全部解析/校验，**任一失败整体不生效**，不会留半成品）；成功返回各字段旧值便于复核。字段语法与 `component.setField` 完全一致（支持 `m_` 前缀、`A.B.C` 嵌套、结构体整体赋值） | `target`(必填), `component`(必填), `fields`(必填, 对象: 字段名→值), `prefab` |
| `scene.inspect` | 目标节点全组件快照（每个组件的全部可序列化字段），UI/运行时盲调的解药 | `target`(必填) |
| `component.add` | 给节点挂组件（场景或 Prefab 资产，类型名反射解析，已存在时幂等跳过） | `target`(必填), `component`(必填), `prefab`, `require`(默认 false) |
| `component.remove` | 从节点移除组件（场景走 `Undo.DestroyObjectImmediate`；Prefab 走 `LoadPrefabContents` 生命周期）。类型名按反射解析，缺失报错 | `target`(必填), `component`(必填), `prefab` |
| `prefab.addNode` | 往 Prefab 资产里添加节点（可选复制源节点组件、按类型名追加组件）。`copyFrom` 支持**跨 Prefab**：写 `Assets/.../Other.prefab/节点/路径`（省略节点路径取该 Prefab 根）；跨 Prefab 只搬组件数据，对象引用需另行重指（结果里带 `warning`） | `prefab`(必填), `name`(必填), `parent`(默认根), `copyFrom`, `components`, `active` |
| `prefab.duplicateNode` | 在 Prefab 资产内复制节点（**默认含子树与组件**，引用不重映射）。返回 `childCount` / `subtreeCount` / `subtreePaths` 便于核对层级；`includeChildren:false` 只要节点自身 | `prefab`(必填), `target`(必填), `name`, `parent`, `active`, `includeChildren`(默认 true) |
| `prefab.tree` | 列出 Prefab 资产的节点树（路径/组件/激活态，只读） | `prefab`(必填), `maxDepth` |
| `prefab.removeNode` | 删除 Prefab 资产内节点（含子树）；`keepChildren:true` 把子节点上移到父再删自身；禁止删根节点 | `prefab`(必填), `target`(必填), `keepChildren`(默认 false) |
| `prefab.delete` | 删除 Prefab 资产文件（`AssetDatabase.DeleteAsset`，路径过白名单） | `prefab`(必填) |
| `addressables.addEntry` | 把资产注册为 Addressable 条目（地址缺省=资产路径，组缺省 Default Group；未装 Addressables 的工程报友好错误不炸编译，全部走反射） | `asset`(必填), `address`, `group`, `labels` |
| `addressables.removeEntry` | 移除 Addressable 条目（不动资产文件；条目不存在时报错） | `asset`(必填) |
| `editor.play` | 进入 Play 模式（会触发域重载，服务随后自动恢复） | - |
| `editor.stop` | 退出 Play 模式（幂等） | - |
| `editor.state` | 查询编辑器状态（isPlaying/isPaused/isCompiling/isUpdating/willChangePlayMode） | - |
| `var.set` | 设置共享变量 | `name`(必填), `value`(必填) |
| `var.dump` | 打印所有共享变量 | `level` |
| `asset.refresh` | 刷新 AssetDatabase（仅编辑器）；**Play 模式下阻断**并报错「编辑器正处于预览状态，不允许编译与刷新」 | - |
| `asset.save` | 保存资源（仅编辑器） | - |
| `asset.importSprite` | 把项目外的 PNG 拷进工程并设为 Sprite（可配九宫格）；**Play 模式下阻断**（会触发 AssetDatabase 刷新） | `source`(必填), `destination`, `pattern`, `pixelsPerUnit`, `filterMode`, `maxSize`, `overwrite`, `border` |
| `scene.save` | 保存当前场景（仅编辑器） | - |
| `scene.open` | 打开指定场景；**触发即弹模态许可窗，请用户手动「允许/拒绝」（30s 超时视为拒绝）**，被拒绝/超时返回失败并中止当前批次（后续指令标记 Skipped，即使 `stopOnError=false`）。Play 模式下拒绝 | `path`(必填，完整 Assets 路径或场景名), `additive`(默认 false), `saveCurrent`(默认 true) |
| `scene.list` | 枚举场景：Build Settings 中启用的场景 + 磁盘 Assets 下全部 `.unity`（标注 `inBuildSettings`） | - |
| `scene.create` | 新建场景资产：用 `NewScene(Additive)` 加载空场景再 `SaveScene` 到目标路径（**不关当前场景，杜绝丢档**），父目录逐层 `CreateFolder`；`path` 可只写名字/相对路径，自动补 `Assets/` 与 `.unity` 并过白名单；已存在则报错。`open:true` 留在新场景，`addToBuildSettings:true` 加入 Build Settings | `path`(必填), `open`(默认 false), `addToBuildSettings`(默认 false) |
| `scene.delete` | 删除场景资产文件（`AssetDatabase.DeleteAsset`，路径过白名单）；目标场景当前已打开则拒绝 | `path`(必填，完整 Assets 路径或场景名) |
| `asset.compile` | 请求 Unity 重新编译脚本程序集（默认先 `AssetDatabase.Refresh()` 同步资源数据库，否则外部直写的 .cs 不被 ADB 感知，用户随后 Ctrl+R 会再补编一次；`clean:true` 做干净重建）；**Play 模式下阻断** | `clean`, `refresh`(默认 true) |
| `asset.compileReport` | 读取最近一次编译结果（错误/警告明细，结构化） | `clear` |
| `console.read` | 读取最近的编辑器/运行时日志（异常、报错） | `type`, `contains`, `clear` |
| `ui.canvas` | 确保场景有 Canvas（含 Scaler 与 EventSystem）；复用时取最外层那个，并写入变量 `ui.canvas`（名字）与 `ui.canvasPath`（完整路径） | `name`, `reuseExisting`, `createEventSystem`, `referenceResolution` |
| `ui.uiRoot` | 生成 UI 根节点（自带 Canvas，结构与运行期 `uiRoot` 约定一致） | `name`, `referenceResolution`, `createEventSystem`, `reuseExisting` |
| `ui.node` | 空 RectTransform 容器（只做父节点/坐标系） | `name`, `parent` + 布局参数 |
| `ui.image` | Image 节点（纯色或指定 Sprite） | `name`, `parent`, `color`, `sprite`, `type`, `preserveAspect`, `nativeSize` + 布局参数 |
| `ui.text` | Text 节点（内置字体） | `name`, `parent`, `text`, `fontSize`, `color`, `align`, `bold`, `font` + 布局 |
| `ui.tmpText` | TextMeshPro 文本节点（中文等复杂字形首选）；**父节点下已有同名子节点时改为"改属性"** | `name`, `parent`, `text`, `fontSize`, `color`, `align`, `bold`, `wrap`, `font` + 布局 |
| `ui.button` | Button 节点（Image+Button+子标签，不绑事件）；布局参数与 `ui.image` 通用，因此能锚到任意角落 | `name`, `parent`, `label`, `size`, `color`, `textColor`, `fontSize`, `tmp`, `tmpFont` |
| `ui.savePrefab` | 把场景节点（含子树）保存为 Prefab（保留 Prefab 连接，可选删除场景实例）；缺省落到 `Assets/GameAssets/UI/` | `target`(必填), `path`, `overwrite`(默认 false), `removeSceneInstance`, `select` |
| `command.cleanup` | 清理指令目录下"用过"的文件，防膨胀。作用域严格限制在指令目录与其 `_done` 子目录内（删前二次校验，绝不越界）。`target`：`done`(默认，只清 `_done`)/`files`(只清根下未归档 `.json`)/`all`；`olderThanMinutes` 按修改时间过滤；`dryRun` 只统计不删 | `target`, `olderThanMinutes`, `dryRun` |

> **指令文件生命周期（防膨胀）**：`command.cleanup` 用于事后批量清理已攒下的指令文件；控制台窗口另有「执行后删除文件」(`DeleteAfterRun`) 开关——开启后 `CommandAutoRunner` 在指令文件执行成功后**直接删除该文件**，且**优先于**「归档到 `_done`」。`DeleteAfterRun` 是全局偏好（默认关），与 `ArchiveAfterRun` 同处控制台；HTTP 下发路径本就不落盘文件，无需清理。

> **歧义与消歧规则（重要，2026-10-22 修复）**：场景里**同名 / 同类型节点很常见**（旧模块留着不删 + 新模块先建后切），所以查找一律遵循：
> - 名字 / 层级路径匹配**始终含未激活节点**（全树遍历，不会"有时找得到有时找不到"）。
> - 凡是**会改节点/组件**的指令（`component.setField`/`get`/`remove`、`gameobject.destroy`/`setActive`/`rename`/`setParent`/`duplicate`、`scene.inspect`），遇到**多个命中会直接报错**，并在错误信息里列出每个候选的 `type:X#N` / 路径 / `activeSelf`，要求你显式消歧——**绝不静默选一个**，否则会改错节点。
> - 消歧有三种可靠写法（任选其一）：
>   1. **类型 + 序号**：`type:CurrencyPanelController#0`（0-based；`#last` 取最后一个）。越界（如 `#9` 但只有 2 个命中）会明确报错「只有 N 个命中」，绝不兜底到最后一个（修 BUG-2）。
>   2. **`gameobject.find` 输出的 `selectPath`**：形如 `Canvas/.../Panel_Currency#2`，`#2` 是同名兄弟中的排序下标，能原样回喂给任何 `target`（修 BUG-3 不可回喂）。
>   3. **`gameobject.find` 输出的 `instanceId`**：同一 Unity 会话内 `id:12345` 可精确回喂（跨会话失效，改用前两种）。
> - 纯查询 `gameobject.find`（`all:false`）仍返回"最佳一个"（激活优先），但会在结果里带 `ambiguous:true` / `matchCount` / `alternatives`，提示你存在多个同名节点——此时务必换上面的消歧写法再发写指令。
> - 附：`gameobject.find` 的 `all:true` 与 `all:false` 现在**语法一致**（都支持 `type:X#N` 的 `#N` 剥离，返回该类型的全部命中），不再出现"加 all 反而 0 命中"（修 BUG-5）。

> **场景诊断（C4）**：所有场景类指令返回都带 `scenePath` / `sceneName`（当前已加载场景）；`gameobject.find` 0 命中或写指令找不到目标时，消息里会附「当前已加载场景：…」，便于一眼看出是否"场景开错了"而非"节点名写错"。



---

## 5.1 UI 指令与模态框

目标：在当前打开的场景中直接搭出 UI，且**不需要任何美术资源**（纯色 Image + Unity 内置字体），**只建节点不接逻辑**（Button 只挂组件，不注册 onClick）。

### 布局参数（`ui.node` / `ui.image` / `ui.text` / `ui.tmpText` / `ui.button` 通用）

| 参数 | 说明 |
| --- | --- |
| `parent` | 父节点：支持**名字 / 层级路径 / 组件类型**三种写法，详见下方「父节点写法」；不填则退回 `canvas` 参数，再退回场景中最外层的 Canvas |
| `canvas` | 指定挂到哪个 Canvas（名称或路径） |
| `size` `[w,h]` | 给了 size 就按 **size + anchoredPosition** 摆放，锚点默认居中 |
| `anchoredPosition` `[x,y]` | 配合 size 使用 |
| `polar` `[radius, angleDeg]` | **极坐标布局**：`angleDeg` 0° 指向正上方，**顺时针**为正；自动换算成 `anchoredPosition` |
| `rotation` `deg` | 局部欧拉角 Z（不写默认 0；若切图本身已经预旋转过就不要再加） |
| `scale` `number` 或 `[x,y]` | 局部缩放；挂到根节点上可以当作整体尺寸的单一旋钮 |
| `anchorMin` / `anchorMax` `[x,y]` | 显式锚点；**不给 size / nativeSize / polar 时**按锚点拉伸，默认 (0,0)-(1,1) 全拉伸 |
| `offsetMin` `[left,bottom]` / `offsetMax` `[right,top]` | 拉伸模式下四边内缩量，right/top 取负值表示向内收缩 |
| `pivot` `[x,y]` | 可选，默认 (0.5, 0.5) |

> `polar` 与 `size` / `anchoredPosition` 可以共存：`polar` 只负责算出 `anchoredPosition`，
> 尺寸仍由 `size` 或 `nativeSize` 决定。`rotation` / `scale` 与任何摆放方式都兼容。

### 父节点写法（`parent` / `canvas` 通用）

| 写法 | 示例 | 说明 |
| --- | --- | --- |
| 节点名 | `"parent": "RoundMenu"` | 遍历**所有已加载场景**查找，且**包含 inactive 节点**；同名时取"激活的、层级更浅的"那个 |
| 层级路径 | `"parent": "UIRoot/HUD/Canvas"` | 后缀匹配，不必从根写起 |
| 组件类型 | `"parent": "type:Canvas"` | 按类型查找；多个命中时按"层级由浅到深、同层按名字"排序 |
| 类型 + 序号 | `"parent": "type:Canvas#1"` | 取第 2 个（从 0 开始） |
| 画布简写 | `"parent": "@canvas"` | 等价于 `type:Canvas` |
| 对象形式 | `"parent": { "type": "Canvas" }` | 也支持 `{ "path": "..." }` / `{ "name": "..." }` / `{ "type": "...", "index": 0 }` |

> **强烈建议把根节点的 `parent` 写成 `type:Canvas` 而不是写死名字。**
> `ui.canvas` 在场景里已有画布时会**直接复用**那个画布，它的名字由场景决定（不一定叫 `Canvas`），
> 后续按名字写 `parent` 就会报"找不到父节点"。
> 实测踩过：场景画布叫别的名字 → `ui.canvas` 成功、`ui.node` 失败。
>
> `type:Canvas` 还会优先复用 `ui.canvas` 刚解析到的那个画布，场景里有多个 Canvas 时前后一致。
> 查找失败时日志会把场景里真实存在的 Canvas 路径列出来，可直接照抄。
>
> 同一套查找规则也被 `gameobject.create` 的 `parent` 和 `gameobject.destroy` 的 `target` 复用，
> 实现见 `Core/SceneQuery.cs`（只依赖 CoreModule，不涉及 UI 程序集）。

### Image 专属参数

| 参数 | 说明 |
| --- | --- |
| `sprite` | 资源路径或资源名（如 `"Assets/GameAssets/UI/RoundMenu/RM_01_off.png"` 或 `"RM_01_off"`）；找不到会报错并说明"文件不存在 / 类型不对" |
| `type` | `simple`(默认) / `sliced` / `tiled` / `filled`；`sliced` 需配合导入时设置的九宫格 border |
| `preserveAspect` | `true` 时保持 Sprite 原始宽高比 |
| `nativeSize` | `true` 时按 Sprite 原始像素尺寸（再除以 `pixelsPerUnit`）设置 `sizeDelta`，**不用手写 size** |

### Button 专属参数

| 参数 | 说明 |
| --- | --- |
| `label` | 标签文字，默认「按钮」；渲染成一个填满按钮的子节点 `Label` |
| `textColor` | 标签颜色，默认白 |
| `fontSize` | 标签字号，默认 22 |
| `tmp` | `true` 时标签改用 **TextMeshPro** 渲染（字体按默认解析，见 `ui.tmpText`） |
| `tmpFont` | 指定 TMP 字体资源路径；给了它等价于 `tmp: true` |
| `font` | 仅在**不用** TMP 时生效：给内置 `Text` 指定字体资源 |

> **中文标签请务必写 `"tmp": true`。**
> 默认走的是内置 `Text`（Unity 内置 Arial 字体），中文会掉字或显示成方块；
> 项目里有思源宋体的 TMP 字体资源，`tmp: true` 会自动用它。
> 这也是"让现有指令更泛用"而非新增指令的例子：`ui.button` 仍然只是一块积木，
> 只是现在它既能配内置字体、也能配 TMP 字体，于是中文按钮不必再单独造一条指令。

### "存在即复用"：用建节点的指令改已有节点

`ui.tmpText` 在父节点下**已经有同名子节点**时，不再新建，而是复用那个节点并改属性。

三个要点：
- **只改明确传了的属性**。传 `font` 就只换字体，不会把文案清成空串、不会把字号颜色重置。
  （新建时仍按默认值初始化：字号 48、深色、居中。）
- **只有传了布局参数才会重新摆放**。没传 `size` / `anchorMin` 等参数时跳过布局，
  否则"只想改个字体"会顺手把节点重置成全拉伸。
- 因此本指令是**幂等**的：同一份指令文件重跑不会堆出重名节点。

换字体的典型写法（5 个文本节点一次改完，结构与文案都不动）：

```json
{ "id": "ui.tmpText", "params": { "name": "TitleText",
  "parent": "MainUIRoot/Window_Confirm/Dialog/TitleBar",
  "font": "Assets/TextMesh Pro/Fonts/FontAssets/SourceHanSerifCN-Regular SDF.asset" } }
```

> `ui.button` 的标签是它自己创建的子节点 `Label`，
> 所以改按钮标签字体时把 `parent` 指到那个按钮、`name` 写 `Label` 即可。

### 颜色写法

三种都可以：`"#RRGGBB"`、`"#RRGGBBAA"`、`[r,g,b,a]`（0~1，写 0~255 也会自动换算）。

### 示例文件

| 文件 | 内容 |
| --- | --- |
| `.workbuddy/UnityEditCommand/ui_modal.json` | 一步式：`ui.canvas` + `ui.modal`，3 条指令出结果 |
| `.workbuddy/UnityEditCommand/ui_modal_detailed.json` | 细粒度：用 `ui.image` / `ui.text` / `ui.button` 逐条拼同样的结构，示范指令的可组合性 |
| `.workbuddy/UnityEditCommand/ui_round_menu.json` | 主界面轮盘菜单：先用 `asset.importSprite` 导入外部切图（含九宫格），再用 `ui.node` + `polar` 极坐标排布 8 个扇形按钮，配 `ui.tmpText` 文案 |
| `.workbuddy/UnityEditCommand/ui_main_root.json` | 把运行期 `UIManager` 代码生成的 `MainUIRoot` 画布直接生成到当前场景 |
| `.workbuddy/UnityEditCommand/ui_confirm_window.json` | 纯色块确认窗口：深色遮罩 + 白底 + 棕色顶栏 + 右上角关闭 + 中间一行提示 + 底部确定/取消。只用 `ui.node` / `ui.image` / `ui.tmpText` / `ui.button` 四种积木拼出来，没为此新增任何指令 |

`ui.modal` 生成的层级：

```
Modal_Confirm              (铺满全屏)
├── Overlay                (半透明遮罩)
└── Dialog                 (居中 520x280)
    ├── TitleBar           (贴顶，高 56)
    │   └── TitleText
    ├── Content            (标题栏与底栏之间)
    │   └── MessageText
    └── Footer             (贴底，高 76)
        ├── Button_Cancel  (120x44，右对齐)
        └── Button_Confirm (120x44，右对齐)
```

常用可调参数：`width`/`height`、`titleHeight`、`footerHeight`、`buttonSize`、`buttonPadding`、
`buttonGap`、`contentPadding`、`fontSize`，以及配色 `overlayColor` / `panelColor` / `titleBgColor` /
`contentBgColor` / `footerColor` / `confirmColor` / `cancelColor` 等。

需要更自由的排布时，直接改 `ui_modal_detailed.json` 里的某一条指令即可，不必改代码。

### 5.2 用外部美术资源搭界面（轮盘菜单）

`ui_round_menu.json` 示范了「先导入资源、再摆界面」的完整流程，分三段：

1. **导入切图**（`asset.importSprite`）——把项目外的 PNG 拷进工程并按 Sprite 设好导入参数：

```json
{ "id": "asset.importSprite", "params": {
    "source": "D:/Wrok_proj/majotavern-art/UI/Z-主界面/WT主界面-切图资源/轮盘菜单",
    "destination": "Assets/GameAssets/UI/RoundMenu",
    "pattern": "*.png", "pixelsPerUnit": 100, "filterMode": "Point", "overwrite": true } }

{ "id": "asset.importSprite", "params": {
    "source": "D:/Wrok_proj/majotavern-art/UI/Z-主界面/WT主界面-切图资源/轮盘菜单/RM_info_keyBG.png",
    "destination": "Assets/GameAssets/UI/RoundMenu", "border": [44, 44, 44, 44] } }
```

   成功后会写入变量 `asset.importSprite`（导入路径）和 `asset.importedCount`（数量）。
   `border` 接受 `[l,b,r,t]` 数组或 `{ "all": 20 }` 对象，用于九宫格（`type: sliced`）。

2. **建根节点 + 用极坐标排布**（`ui.node` + `polar`）——8 个扇形按钮一步到位：

```json
{ "id": "ui.node",  "params": { "name": "RoundMenu", "size": [1400, 1700], "anchoredPosition": [0, 0], "scale": 0.42 } }
{ "id": "ui.image", "params": { "name": "RM_BG", "parent": "RoundMenu", "sprite": "RM_mainBG", "nativeSize": true } }
{ "id": "ui.image", "params": { "name": "Slot_1", "parent": "RoundMenu", "sprite": "RM_05_off", "nativeSize": true, "polar": [465, 22.5] } }
```

   `polar` 的 0° 是正上方、顺时针为正，所以 8 等分的槽位就是 `22.5 + 45 × k`。

3. **写文案**（`ui.tmpText`）——内置 `Text` 在中文上依赖系统字体，UI 文案建议一律走 TMP：

```json
{ "id": "ui.tmpText", "params": { "name": "CenterLabel", "parent": "RoundMenu", "text": "地图",
    "fontSize": 96, "color": "#3D3049", "align": "center" } }
```

**关于整体尺寸**：根节点 `RoundMenu` 的 `scale` 是唯一的尺寸旋钮，子节点全部写**美术原始像素坐标**，
所以对照切图改起来不用做任何换算；想整体放大/缩小只改这一个数。

**关于切图是否预旋转**：本套 `RM_01~08` 的图标在美术侧已经按各自槽位方向画好了（主惯性轴实测
`±24° / ±70°`），因此指令里**不要**再加 `rotation`，否则会歪两次。若之后换成"图标统一朝上"的切图，
再给每个槽位补 `"rotation": <该槽位的角度>` 即可。

---

## 5.3 UI 根节点 MainUIRoot（与运行期代码对应）

游戏运行时的 UI 根节点由 `Demo.UI.UIManager.EnsureUIRoot()` 确保（序列化引用缺失时自动查找/创建
MainUIRoot 并赋值，见工程内 `Demo.UI.MainUIRootBuilder`）：

```
MainUIRoot                      DontDestroyOnLoad
├── Canvas        ScreenSpaceOverlay，sortingOrder 0
├── CanvasScaler  ScaleWithScreenSize，参考分辨率 1920x1080，match 0.5
└── GraphicRaycaster
```

`BaseActivity` / `BaseDialog` 都带 `[RequireComponent(typeof(Canvas))]`，且全部实例化在这个根节点下，
**没有这一层画布 UI 一个都不会渲染**。

`ui.uiRoot` 指令在场景里生成**完全相同**的结构（同一个名字、同一套组件、同样的参数），
并且会记住这块画布，让后续指令的 `"parent": "type:Canvas"` 落到它上面：

```json
{ "id": "ui.uiRoot", "name": "MainUIRoot", "referenceResolution": [1920, 1080],
  "createEventSystem": true, "reuseExisting": true }
```

| 参数 | 说明 |
| --- | --- |
| `name` | 根节点名字，默认 `MainUIRoot` |
| `referenceResolution` | CanvasScaler 参考分辨率，默认 1920x1080 |
| `createEventSystem` | 场景里没有 EventSystem 时创建一个（挂在根节点下）；已有则不重复创建 |
| `reuseExisting` | 场景里已有同名节点则复用并补齐缺失组件，默认 true，重复执行不会造出第二个根 |

> 运行时侧 `UIManager` 的 EventSystem 是挂在 `AfterSceneLoad` 上补的，不是 `OnGameStart`：
> 早于场景加载时场景自带的 EventSystem 还没出现，先建就会同一个场景里出现两个。

### 5.3.1 工程结构挂钩（可选，MCP 与工程保持编译期零耦合）

本指令系统**不引用任何工程运行期类型**。工程若想让 MainUIRoot 的结构定义只有一份
（运行期 `EnsureUIRoot` 与编辑器指令共用），提供一个静态类即可，指令按**类型名**反射发现：

```csharp
// 任意命名空间；名字必须是 MainUIRootBuilder，方法签名必须完全一致
public static class MainUIRootBuilder
{
    public static Canvas Ensure(string name, Vector2 referenceResolution,
                                bool reuseExisting, bool createEventSystem)
    { /* 工程自己的结构定义 */ }
}
```

- 找到且签名匹配 → `ui.uiRoot` 转调工程定义（结构唯一事实来源在工程）；
- 找不到（如导入到没有此类的工程）→ 使用本节的内置默认结构，行为与过去完全一致；
- 发现范围只扫工程程序集（跳过 Unity/System 等前缀），结果按进程缓存。

> 沿用这一约定的是 CivelRev 的 `Demo.UI.MainUIRootBuilder`（`UIManager.EnsureUIRoot`
> 运行期也调它）。island_back 等其它工程没有该类，走内置默认，互不影响。

### 5.3.2 工程客制化内容（`Project/` 目录）：允许存在，但绝不进包

指令系统**允许**出现与工程代码耦合的客制化内容（挂钩实现、工程专用指令、工程配置等），
约定统一放在本目录下的 `Project/` 子目录：

```
Assets/Editor/ExternalCommandSystem/
├── Core/  Commands/  ...        ← 通用部分，随包分发
├── Project/                     ← 工程客制化专区，绝不进包
└── McpBridge/                   ← 桥脚本，随包分发
```

**铁律：`Project/` 下的任何文件都不允许出现在 `.unitypackage` 里**——客制化内容
依赖工程运行期代码，随包分发到别的工程必然编译失败。打包与校验两端强制把关：

- `Tools/build_unitypackage.py`：收集阶段整体跳过 `Project/`、目录条目阶段排除、
  打包前终检（发现漏排直接报错终止，宁可打不出包）；
- `Tools/verify_unitypackage.py`：对成品包独立断言，发现 `Project/` 路径即判失败。

**通用部分与客制化部分之间的调用方向**：通用代码（`Core/` / `Commands/`）**不得引用**
`Project/` 内的类型（否则去掉 Project/ 的工程编译不过）；反向无限制——客制化代码可以
随便引用通用类型，也可以经 §5.3.1 的反射约定被通用代码发现。打包进包里的内容必须
保持对工程**零编译依赖**，这一点不因客制化目录的存在而放宽。

---

## 6. 自动激活机制（重点：低开销）

`Editor/CommandAutoRunner.cs` 用 `[InitializeOnLoad]` 自动启动，只做一件事——指令注册：

1. **注册**：只在程序集重载时执行一次，使用 Unity 的 `TypeCache`（内部有缓存，不需要全量反射遍历程序集），并做一次 `ScanAssemblies` 兜底。
2. **执行**：全部由控制台窗口或菜单**手动触发**，没有常驻监听、没有轮询、没有后台线程，空闲时零开销。

> 旧的「文件状态监听」（`FileSystemWatcher` + 去抖 + 去重）**已被移除**；现在下发指令改走
> **HTTP 指令服务**，不再有"保存文件即自动执行"。需要批量执行目录里的 `.json` 时，用控制台窗口的 **扫描目录**。

其它入口：

- 菜单 **Tools / 指令系统 / 执行指令文件...**：手动选文件执行；
- 菜单 **Tools / 指令系统 / 打开指令目录**：打开默认指令目录；
- 菜单 **Tools / 指令系统 / 重新扫描指令类**：新增指令类后强制重新扫描；
- 控制台窗口的 **扫描目录**：执行目录下所有未归档的 `.json`。

> 已移除运行期（打包后）启动钩子：原先会在 `AfterSceneLoad` 检查 `StreamingAssets/commands/startup.json`，
> 与"编辑器专用、不进包"的约束冲突，现已删除。

---

## 7. 运行器行为

| 行为 | 说明 |
| --- | --- |
| 未知 ID | 记为 `UnknownCommand` 失败，不会中断其它指令的注册 |
| 指令抛异常 | 被捕获，记为失败，日志输出完整堆栈摘要 |
| `StopOnError`（默认 true） | 失败后中止后续指令，剩余指令标记为"因前序指令失败而中止" |
| `DryRun` | 只记录将要执行的指令，不产生副作用 |
| 嵌套 | `batch` / `file.run` 最多 8 层，且 `file.run` 有循环引用检测 |
| 报告 | `CommandRunReport` 提供 成功/失败/跳过 计数、逐条记录与耗时 |

---

## 8. 验证状态

> ⚠️ **离线编译检查已退役（2026-10-19 删除）**：原 `.verify/` 离线编译工程已移除。现改用**运行中的 Unity + HTTP 指令 `asset.compile`** 做真实全量编译验证——经 `unity-cmd` 技能 `python scripts/send.py send '{"id":"asset.compile"}' --json` 触发，再用 `/v1/status` 的 `compiling` 字段轮询结果。该方式覆盖整个工程（而非仅 `ExternalCommandSystem/Editor`），本次即借此抓出 `DemoScript/Managers/FacilityManager/RoadLogic.cs` 的缺分号编译错误。

重构后按**依赖关系**拆分（不再按目录名，因为现在全在 `Editor/` 下）：

- **core**（26 个文件：Json + Core + 通用指令 + HTTP 协议层，只依赖 `UnityEngine.CoreModule`）：0 错误 0 警告；
- **editor**（core + CommandAutoRunner / CommandHttpServer / 窗口 / 设置 / `asset.*` 指令，引用完整 `UnityEngine.dll` + `UnityEditor.dll`，定义 `UNITY_EDITOR`）：0 错误 0 警告；
- **ui**（core + `ui.*` 指令，单独建工程：因为 `UnityEditor.dll` 要的是程序集名 `UnityEngine`，而 `UnityEngine.UI.dll` 依赖的是 `CoreModule` / `UIModule` / `TextRenderingModule`，两者不能混在同一工程里）：0 错误 0 警告；
- **guardcheck**（只编译 `EditorOnlyGuard.cs`，且**故意**不定义 `UNITY_EDITOR` / `COMMANDSYSTEM_VERIFY`）：预期失败，实测报 `CS1029`，证明打包保险真的会拦住；
- **tests**：163 项行为测试全部通过，覆盖 JSON 解析（注释/转义/科学计数/异常定位）、序列化往返、对象映射（含 Vector3）、注册表扫描与去重、三种文件格式读取、执行流程（未知指令/异常/禁用/失败中止/空跑/嵌套/循环引用）、文件读写指令、路径解析（默认指令目录 / `@ws/` 前缀 / 绝对路径 / 基准目录优先），并校验默认指令目录下每个示例 json 都能被正确解析；
  另外新增三组回归测试：
  - **轮盘菜单几何**：直接解析 `ui_round_menu.json`，校验 8 个槽位的极坐标角度为 `22.5+45k`、半径 465、槽位→切图映射正确、图标外缘与轮盘外径吻合（容差 20px）、引用的切图在源目录或工程内存在、TMP 字体资源存在、源目录 PNG 数量与记录的切图尺寸未漂移；
  - **父节点引用可解析**：扫描默认指令目录下所有示例，要求每条 `parent` / `canvas` 要么是 `type:` 之类的类型查找、带路径，要么能在同一文件里找到对应的创建指令（`ui.canvas` 的 `name` 不算，因为复用时它不生效）——专门盯"按名字找画布"这类必现故障；
  - **HTTP 协议层**：用假 `IHttpEnvironment` 直接驱动 `HttpProtocol.Handle`，校验四个端点的响应结构、令牌门禁（缺/错/空三种情况）、方法校验（405）、未知路径 404 并列出手动端点、execute 的空 body / 坏 JSON / dryRun query / 编译中 503 / 超大 body 413，以及报告序列化的字段完整性与 data 兜底。

> 注：`Application.dataPath` 是 Unity 内部调用（ECall），脱离 Unity 宿主时"包含它的方法"连 JIT 都过不去。
> 因此 `CommandPathUtil` 把该调用单独隔离进 `ReadUnityDataPath()`，并留出 `RootOverride` 注入点供离线测试使用。

注意：UI 指令中 `#if UNITY_EDITOR` 分支内的几行（Undo 注册、MarkSceneDirty、Selection）未包含在离线编译中，属于标准 UnityEditor API 用法。

### 8.1 Skill 客户端验证

`skills/unity-cmd/scripts/send.py` 是纯标准库客户端（与离线编译检查无关，仍保留可用），其端到端行为用本地 mock 服务端验证，覆盖：

- 信息文件读取、令牌头、四个子命令的退出码与输出排版
- 成功下发 / dry-run / `--json` 原始输出 / 业务失败（退出码 1 且失败条目被标出）
- 坏 JSON（400 + 行列号）、错令牌（401）、503 与 `--wait-busy` 自动轮询等待
- 服务未启动时的提示（引导去菜单开启，而不是报一堆连接错误）

`CommandHttpServer.cs` 本身依赖 `UnityEditor` 与 `System.Net`，无法离线编译验证，
需要在 Unity 里编译确认；它的可测部分（路由、鉴权、响应构造）已全部前移到 `HttpProtocol.cs`。

---

## 9. MCP 集成与打包分发

本包除了 HTTP 指令桥，还自带一个 **MCP（Model Context Protocol）服务器**，
让 AI Agent 以标准工具调用的方式驱动 Unity，不必再拼 HTTP 请求。

### 9.1 为什么是"Python stdio 桥"

MCP 的 HTTP 传输要求在客户端配置里**写死一个 URL**，而本系统的核心价值是
「端口避让 + 多工程共存」，写死端口就退化成抢端口。因此 MCP 一侧采用
**stdio 桥**（`McpBridge/unity_mcp_server.py`）：

```
MCP 客户端 ──stdio(JSON-RPC)──> unity_mcp_server.py ──HTTP──> Unity 指令服务
                                        ↑
                              复用 send.py 的端点发现
                              （.http-info.<hash> / 身份自证 / 端口扫描兜底）
```

桥进程内部 `import send`，发现逻辑与 CLI 完全同一份代码，不重复实现。
**Unity 服务端零改动**——它仍然只提供原来那 5 个 HTTP 端点。

### 9.2 包内容

```
Assets/Editor/ExternalCommandSystem/
├── README.md                  本文
├── McpBridge/                 ← MCP 桥（Unity 不会编译/执行它们，只作为资产携带）
│   ├── send.py                CLI 客户端（也是桥的依赖）
│   ├── unity_mcp_server.py    stdio MCP 服务器
│   └── SKILL.md               Agent 使用说明
└── ...                        C# 服务端（其余文件）
```

> `.py` 在 Unity 里会显示为灰底的未知资产（`DefaultAsset`），这是正常的：
> 它们只需要被复制到磁盘上的运行位置，不需要 Unity 导入。

### 9.3 安装到新工程

导入本包后，菜单栏会出现：

| 菜单 | 作用 |
| --- | --- |
| `Tools / 指令系统 / MCP / ① 安装桥到本工程` | 复制脚本 + 注册 MCP 配置（一步到位） |
| `② 查看将要写入的 MCP 配置` | 只打印，不写盘 |
| `③ 只写入 MCP 配置（不复制脚本）` | 脚本已手动放好时用 |
| `④ 从 MCP 配置中移除` | 摘掉 `unity-cmd` 条目（其它服务保留） |
| `⑤ 卸载本工程的桥文件` | 删除本工程的桥目录 |

安装器做的事：

1. 把 `McpBridge/*.py` 复制到 `<工程根>/.workbuddy/skills/unity-cmd/scripts/`；
   `SKILL.md` 复制到 `.workbuddy/skills/unity-cmd/`。
2. 把启动项合并进 `~/.workbuddy/mcp.json`（**保留文件里其它 MCP 服务**，写前自动备份为 `.bak.<时间戳>`）。
3. 弹窗提示你去连接器管理页点「信任」——**这一步必须手动做**，安装器无法代劳。

生成的条目形如：

```json
{
  "mcpServers": {
    "unity-cmd": {
      "command": "<Python 解释器绝对路径>",
      "args": [
        "<工程根>/.workbuddy/skills/unity-cmd/scripts/unity_mcp_server.py",
        "--project-root",
        "<工程根>"
      ],
      "env": { "PYTHONIOENCODING": "utf-8", "PYTHONUTF8": "1" }
    }
  }
}
```

Python 解释器按以下顺序选取：`EditorPrefs` 里的 `CivilRev.CommandSystem.McpPython` →
WorkBuddy 托管的 `~/.workbuddy/binaries/python/versions/*/python.exe` → PATH 上的 `python`。
想换解释器就设那个 `EditorPrefs` 键。

### 9.4 工具集

固定 5 个元工具：`unity_status` / `unity_execute` / `unity_list_commands` /
`unity_cancel` / `unity_compile_and_wait`。

此外，桥会在 `tools/list` 时拉取 `/v1/commands`，把**每条指令动态映射成一个工具**
（`asset.compile` → `unity_asset_compile`，参数 schema 由指令的 `usage` 解析生成）。
所以**新增一条 `[Command]` 指令，MCP 就自动多一个工具，桥不用改**。

### 9.5 打包（维护者用）

```bash
python Tools/build_unitypackage.py                 # 输出到 Tools/UnityPackage/
python Tools/build_unitypackage.py -o D:/tmp/x.unitypackage
```

打包器把 C# 目录 + `McpBridge` 脚本一并收进 `.unitypackage`（tar.gz 格式），
GUID 取自现有 `.meta`；新增的 `.py`/`.md` 会生成新 GUID 与 `DefaultImporter` 的 meta。

### 9.6 移植注意

- **命名空间**仍是 `CivilRev.CommandSystem`。跨项目使用不受影响，若想改成中性名，
  全局替换该字符串即可（`EditorOnlyGuard.cs` 不依赖它）。
- **信息文件目录**规则两端必须一致：`<工程根>/.workbuddy/UnityEditCommand/.http-info.<hash>`，
  其中 hash 是「工程路径规范化 → 分隔符统一为 `/` → 小写 → SHA1 前 4 字节」。
  C# 在 `CommandHttpServer.ComputeProjectHash`，Python 在 `send.py:project_hash`。
  **改一端必须同时改另一端**，否则"怎么都连不上"。
- 若工程根的上层目录也含 `.workbuddy`，工作区根取上层（多工程共用一个工作区时的行为）；
  否则工作区根就是工程根。`send.py:workspace_root_for` 复刻了这条规则。
- Python 侧只用标准库（`urllib` / `json` / `hashlib` / `threading`），无需 `pip install`。

---

## 10. AI 编辑反馈闭环

这一节解决的问题：**AI 改完 `.cs` 之后，怎么知道编译过没过。**

### 10.1 为什么必须专门做

Unity 编译是异步的，而且**编译失败时编辑器同样会回到"空闲"状态**。
如果调用方只看 `compiling` 变回 `false` 就认为成功，会漏掉编译失败——
后续所有指令都跑在旧程序集上，行为与代码不符，排查方向会完全跑偏。

真实缺陷记录：2026-10-20 之前的 `unity_compile_and_wait` 就只等空闲、不读结果，
AI 拿到的是"编译已完成"，实际编译早已失败。

### 10.2 实现

| 组件 | 作用 |
| --- | --- |
| `CommandCompileMonitor` | 订阅 `CompilationPipeline.compilationStarted` / `assemblyCompilationFinished` / `compilationFinished`，把结构化消息留下来 |
| `CommandConsoleBuffer` | 环形缓冲（400 条），订阅 `Application.logMessageReceivedThreaded`，供读运行时异常 |
| `asset.compileReport` 指令 | 返回最近一次编译的完整结果（assembly / file / line / column / message） |
| `console.read` 指令 | 返回最近的编辑器/运行时日志，支持 `type` / `contains` / `clear` |
| `/v1/status` 的 `lastCompile` | 编译结果摘要（ok / errorCount / warningCount / durationMs） |

### 10.3 关键机制：编译失败不触发域重载

这是整套闭环能成立的前提：

- **编译失败** → Unity 不会重载域 → 已加载的旧 `Assembly-CSharp-Editor` 继续运行 →
  服务活着 → 能正常回答"错在哪"，且静态字段里的错误列表还在。
- **编译成功** → 域重载 → 静态字段清空（那时的结果对 AI 没有价值），
  但摘要会写进 `SessionState`（会话级，编辑器重启才清），所以 `lastCompile` 依然可查。

另有一层兜底：`EditorApplication.update` 里检测"编译中由真变假"，
防止极少数情况下 `compilationFinished` 未触发导致状态卡在"编译中"。

### 10.4 使用

```bash
# CLI：编译 + 等待 + 失败时打印错误，退出码 0/1/2/3/4
python scripts/send.py compile
python scripts/send.py compile --clean --wait 300
```

```json
// 需要明细时
{"commands":[{"id":"asset.compileReport","params":{"includeMessages":true,"limit":50}}]}
{"commands":[{"id":"console.read","params":{"count":30,"type":"error"}}]}
```

MCP 侧对应 `unity_compile_and_wait`（失败时 `isError=true` 并附带错误清单）
与自动生成的 `unity_asset_compileReport` / `unity_console_read`。

> 注意：写完 `.cs` 后 Unity 的 Auto Refresh 可能已经开始编译，此时再调 `asset.compile`
> 会被 503 拒绝。桥里的处理是"检测到已在编译就直接等这次编译"，并对 503 做退避重试。
