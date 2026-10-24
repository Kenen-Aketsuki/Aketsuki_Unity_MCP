---
name: unity-cmd
description: 向本项目的 Unity 编辑器下发 JSON 指令、查询编辑器状态、读取指令执行报告时使用。适用于"把某个界面/节点生成到 Unity 场景里"、"执行 ui_*.json"、"看 Unity 现在能不能收指令"、"刚才那条指令为什么失败"等场景。触发词：Unity 指令、下发指令、执行指令文件、ui_round_menu、Unity 状态、执行报告。
---

# unity-cmd：向 Unity 编辑器下发指令

本项目在 Unity 编辑器里跑着一个只监听本机的 HTTP 指令服务（`CivilRev.CommandSystem`）。
本 Skill 是它的**客户端半边**：负责探活、发送、把执行报告读成人能看懂的样子。

> **⚠️ HTTP 200 ≠ 指令成功（DOC-001）。** 业务失败时服务端照样返回 200，
> 成败只在 body 的 `ok` / `records[].status` 里。**先看 `body.ok`，失败就立刻读 `summary` 与 `failures`**
> ——这两个字段是专门为避免"`ok:false` 被长上下文淹没"加的，都排在 `records` 之前。
> 想确认"这次是不是真的编译了"，查 `/v1/status` 的 `lastCompile.requestedUtc` 与 `rejectedReason`
> （被拒时 `rejectedReason` 非空，且 `requestedUtc` 会比 `finishedUtc` 新）。

服务端由 `CommandHttpServerBoot` 在编辑器启动和每次域重载后**自动拉起**，正常情况下不需要用户点任何按钮。
连不上通常是这三种情况：Unity 没开着、正在重编译（旧进程已断开、新域还没起来）、或用菜单
`Tools / 指令系统 / HTTP 指令服务` 手动关掉了。先 `status` 确认，再对症处理，不要反复重试。

## 快速开始

```bash
python scripts/send.py status                 # 探活 + 看编辑器状态（含当前指令与耗时）
python scripts/send.py commands               # 列出全部可用指令（带参数说明）
python scripts/send.py resolve                # 端点定位诊断：列出所有候选并说明最终连哪个
python scripts/send.py send <文件>.json        # 下发并同步等执行报告
python scripts/send.py send <文件>.json --dry-run   # 只校验不真执行
python scripts/send.py send <文件>.json --request-id <ID>  # 幂等下发（重试不会二次执行）
python scripts/send.py cancel                 # 请求协作式取消当前批次
python scripts/send.py compile                # 编译 + 等待 + 回传错误（改 .cs 后的闭环）
```

客户端从 `<工作空间>/.workbuddy/UnityEditCommand/.http-info.<projectHash>` 读取端口与令牌，
**不要硬编码端口**——端口被占用时服务端会自动避让，写死必然连错。
文件名带工程 hash（工程路径 SHA1 前 8 位）：同一工作区下同时开着多个 Unity 工程时，
各自的信息文件互不覆盖。旧版无 hash 的 `.http-info` 仍被兼容读取（过渡期）。

### 端点定位（最重要的一件事）

同一个工程目录里可能同时跑着多个 Unity 进程，它们都会加载同一套 Editor 程序集并各自起监听。
典型的是资源导入工作进程 `AssetImportWorker0/1`（命令行带 `-batchMode`）——它们 `ping` 得通、
`status` 也正常，但**不跑 `EditorApplication.update`**，指令进了它们的队列就永远不被消费。
因此客户端在发指令前会先问一句「你是谁」（读 `/v1/status` 的 `identity` 块），**只往自证为
交互式编辑器（role=editor、interactive=true）的那个端口发**，其余一律避开。

判定完全依赖服务端自报的身份，**不查进程、不猜端口**：

1. 候选来源（按优先级，去重）：`--url` 显指 → 本工程信息文件 `.http-info.<hash>` →
   其它信息文件与各实例自述副本 `.http-info.<hash>.<pid>` → 信息文件给出的端口避让区间
   （默认 57831 起、向后 10 个）扫描。
2. 对每个候选 `GET /v1/status`，取出 `identity` 块。
3. 选择顺序：**`projectPath` 属于本工作区的交互式编辑器** > 其它交互式编辑器 > 身份不明的 > 明确不能执行的。
   第一档用 `projectPath` 比对，避免端口扫描扫到隔壁工程的编辑器（同机多工程时很常见）。
4. 万一最终选中的端点不是交互式编辑器，`send` 会拒绝下发（退出码 5），除非加 `--force`。

`resolve` 子命令只做定位、不下发，用来在"卡住"时看清到底连到了谁。怀疑连错进程时第一时间跑它，
而不是去翻 `netstat` / 进程命令行（那只是兜底手段）。

## MCP 通道（推荐给 AI Agent 用）

除了 CLI（`send.py`），同一套能力还以标准 **MCP 服务器**形式提供：
`scripts/unity_mcp_server.py`（stdio 传输，**Unity 服务端零改动**）。

**为什么是 stdio 桥，而不是在 Unity 里原生实现 MCP HTTP 服务**：MCP 的 HTTP 传输要求在配置里
写死一个 URL，而本系统的核心价值是「端口避让 + 多工程共存」，写死端口就退化成抢端口；
stdio 桥把发现逻辑关在桥进程内部（复用 `send.py` 的 `.http-info.<hash>` / 身份自证），
宿主只看到一条干净的 stdio 通道。

### 启用

配置写在 `~/.workbuddy/mcp.json`：

```json
{
  "mcpServers": {
    "unity-cmd": {
      "command": "<python 绝对路径>",
      "args": ["<工作空间>/.workbuddy/skills/unity-cmd/scripts/unity_mcp_server.py"]
    }
  }
}
```

写完后**必须到连接器管理页点「信任」**，MCP 才会真正启动。
桥是**一工程一份**：它从脚本位置向上找 `.workbuddy` 推出工作区，接另一个 Unity 工程就复制一份
脚本并单独注册。

> 如果本项目是通过 **UnityPackage** 引入指令系统的，以上步骤可以跳过：
> 菜单 `Tools / 指令系统 / MCP / ① 安装桥到本工程` 会自动复制脚本并合并 mcp.json
> （保留文件里其它 MCP 服务、写前自动备份）。装完仍需要手动点一次「信任」。

### 工具集

固定 5 个元工具：

| 工具 | 用途 |
| --- | --- |
| `unity_status` | 编辑器状态 + 当前执行中的指令与耗时 |
| `unity_execute` | 一次下发多条指令（服务端串行执行），支持 `dryRun` / `requestId` |
| `unity_list_commands` | 指令清单与参数说明 |
| `unity_cancel` | 协作式取消当前批次 |
| `unity_compile_and_wait` | 触发重编译并等到编辑器重新空闲（跨域重载自动重连） |

外加**动态生成**的 typed 工具：`unity_asset_compile`、`unity_file_write`、`unity_ui_button`……
命名规则是「指令 ID 的点换成下划线」，参数 schema 由服务端 `/v1/commands` 的 `usage` 实时解析
（单一来源，不在桥里手写，避免与 `CommandAttribute` 漂移）。
**新增一条 `[Command]` 指令就自动多一个 MCP 工具**，桥不需要改。

Unity 没启动时 `tools/list` 仍会在半秒内返回那 5 个元工具，调用会得到清晰的中文提示，
不会卡住、也不会崩。

### 协议实现要点（改桥时别踩）

- **stdout 只允许 MCP 消息**：每行一条**紧凑** JSON（`separators=(",", ":")`），
  消息内不得含换行；所有日志走 stderr。Windows 上还必须 `reconfigure(encoding="utf-8", newline="\n")`，
  否则默认 cp936 / `\r\n` 会让客户端解析失败。
- **每请求独立线程**：慢的 `execute` 不能挡住 `cancel` / `status` 这类需要穿透的调用。
- **端点缓存**：优先读本工程信息文件（快路径 ~0.5s）；失败才退回 `send.discover` 的端口扫描
  （最坏要串行探测 10 个端口 × 3s，会让首次 `tools/list` 卡十几秒）。

## 编辑-编译-纠错闭环（改 .cs 必须走这条）

改完脚本后的固定动作，不要跳过：

1. 写文件（CLI 用 `file.write` 指令；MCP 用 `unity_file_write`）
2. `python scripts/send.py compile`（MCP 用 `unity_compile_and_wait`）
3. 读返回结果：
   - 成功 → 继续下一步
   - 失败 → 按 `文件(行,列): error CSxxxx: ...` 改代码，回到第 1 步
4. 直到编译通过再下发任何依赖新代码的指令

**为什么必须读结果**：编译失败时 Unity 一样会回到空闲状态。只等"编辑器空闲"
就当成成功，会让 AI 误判，之后所有指令都跑在**旧程序集**上，行为与代码不符，然后开始胡乱归因。

三条读取途径：

| 途径 | 内容 | 用在哪 |
| --- | --- | --- |
| `send.py compile` / `unity_compile_and_wait` | 编译 + 等待 + 失败时回传错误（退出码 1 / isError=true） | 主流程 |
| `asset.compileReport` 指令 | 最近一次编译的完整列表（assembly / file / line / column / message） | 需要明细或单独复查 |
| `/v1/status` 的 `lastCompile` | 摘要：`ok` / `errorCount` / `warningCount` / `durationMs` | 一眼看"上次编译过没过" |

**编译错误 vs 运行时错误**：上面管的是"编不编得过"。若编译通过但跑起来异常
（NullReferenceException、资源缺失等），用 `console.read`：

```json
{"commands":[{"id":"console.read","params":{"count":30,"type":"error"}}]}
```

`type` 可选 `all` / `error` / `warning` / `log`；`contains` 可做文本过滤；`clear:true` 读完清空。

> 已知限制：编译**失败**时不会发生域重载，服务仍然存活、能正常回答"错在哪"——这是闭环成立的前提。
> 编译**成功**会重载域，内存里的完整消息列表会清空，但摘要存在 `SessionState` 里，`lastCompile` 仍可查。

## 运行时指令（Play 模式干预，默认关闭）

`runtime.*` 这一组指令操作的是**正在运行的游戏**：进/出 Play、找运行时节点、点按钮、拖列表、写输入框。
它和 `asset.*` / `scene.*` / `gameobject.*` 这类改资产的编辑指令**分开管**，原因很直接——
点错一个按钮可能写存档、发网络请求、甚至退出游戏，不能跟只读指令共用一个默认放行策略。

### 两道门禁，顺序固定

1. **控制台开关**：Unity 菜单 `工具 / 指令系统 / 指令控制台` →「运行时指令（Play 模式干预）」→
   勾选「启用运行时指令」。**默认关闭**，按工程保存（换工程要各自打开）。
   关着时**这一组全部失败**，包括 `runtime.play` / `runtime.stop`。
2. **Play 状态**（每条指令各自声明，见 `playState` 字段）：
   `runtime.play` 要求**编辑模式**，`runtime.stop` **任意状态**（幂等），其余 `Play 模式`。

先判开关再判 Play：开关没开是配置问题，这时报"不在 Play"会把人带偏。
开关是**实时读**的，同一条请求里"先关开关、再调 runtime.*"也会立刻失败，不会拖到下一帧。

失败时**HTTP 仍是 200**（与 DOC-001 同一类坑），看这几处：

| 信号 | 含义 |
| --- | --- |
| `records[].message` / `failures[]` | 「运行时指令未启用：…」/「只能在 Play 模式执行：…」/「只能从编辑模式执行：…」 |
| `data.runtime` / `data.enabled` / `data.playing` / `data.requiredPhase` | 机器可读的同一事实，不必解析中文 |
| `/v1/status` 的 `runtimeCommands` | `enabled` / `playing` / `commandCount`，一眼看出为什么调不动 |
| `commands`（`/v1/commands`）的 `runtime: true` + `playState` | 该指令属于这一组，且需要哪种 Play 状态，可提前跳过 |

> 常规指令 `editor.play` / `editor.stop` **不受开关影响**，任何时候都能用——
> 它是"关掉开关后怎么把游戏停下来"的逃生口。

### 六条指令

| 指令 | 用途 | 关键参数 |
| --- | --- | --- |
| `runtime.play` | 进入 Play（受开关约束；会触发域重载） | `onDirty`: `abort`（默认）/ `save` |
| `runtime.stop` | 退出 Play（受开关约束；编辑模式下幂等返回成功） | — |
| `runtime.hierarchy` | 运行时层级快照（**含运行时实例、inactive、DontDestroyOnLoad**） | `root` / `scene` / `name` / `depth` / `includeInactive` / `limit` |
| `runtime.click` | EventSystem 注入点击 | `target` 或 `position` / `button` / `clicks` / `direct` |
| `runtime.scroll` | 向 ScrollRect 注入拖拽序列 | `target` / `direction` / `amount` / `mode` / `steps` |
| `runtime.input` | 写 TMP/UGUI 输入框并触发提交回调 | `target` / `text` / `append` / `submit` |

```json
{"commands":[
  {"id":"runtime.play","params":{}},
  {"id":"runtime.hierarchy","params":{"name":"BtnStart","limit":50}},
  {"id":"runtime.click","params":{"target":"[scene:Level1]UIRoot/HUD/Canvas/BtnStart"}},
  {"id":"runtime.scroll","params":{"target":"[scene:Level1]UIRoot/ShopPanel/List","direction":"vertical","amount":0.5}},
  {"id":"runtime.input","params":{"target":"[scene:UIRoot]LoginPanel/InputAccount","text":"tester01","submit":true}},
  {"id":"runtime.stop","params":{}}
]}
```

`runtime.hierarchy` 给的 `qualifiedPath` 可以原样回喂给另外三条的 `target`（跨场景也唯一）。

### 多场景叠加：必须点名场景

叠加加载下「常驻引导 + 关卡 + UI」各会有一份 `UIRoot` / `Canvas` / `BtnStart`。
`runtime.hierarchy` 会按场景分组（每组带 `label` / `selector` / `handle` / `buildIndex` / `active`），
节点带 `qualifiedPath`：

```
[scene:Level1]UIRoot/BtnStart
```

两种等价写法：

| 形式 | 例子 |
| --- | --- |
| 字符串前缀 | `[scene:Level1]UIRoot/BtnStart` |
| 对象字段 | `{"scene":"Level1","path":"UIRoot/BtnStart"}` |

选择器语法（都写在 `[scene:...]` 的括号里）：

| 写法 | 含义 |
| --- | --- |
| `Level1` | 场景名（大小写不敏感，须完整匹配） |
| `Level1#0` / `Level1#1` / `Level1#last` | 同名场景的第 N 个（两个不同资产的场景重名时会这样；同名时**每个**都带序号，含 `#0`） |
| `handle:-5270` | 场景 handle（直接从 `runtime.hierarchy` 抄） |
| `path:Assets/Scenes/Level1.unity` | 资产路径，也接受后缀 / 文件名 |
| `active` | 当前激活场景 |
| `DontDestroyOnLoad` | 常驻对象所在的合成场景（它不在 `SceneManager` 列表里，但可以点名） |

**不给场景时，多命中一律失败**，不给"猜一个"的机会：

```json
{"failures":[{"message":"'UIRoot/BtnStart' 命中 2 个节点，无法确定点哪个……把下面任一条原样作为 target 重试：
  [scene:SceneA]UIRoot/BtnStart（…instanceId=-5346）
  [scene:SceneB]UIRoot/BtnStart（…instanceId=-5544）"}],
 "data":{"candidates":[...],"candidateCount":2}}
```

改 `target` 时就抄 `candidates[].qualifiedPath`。候选顺序是**稳定**的（场景加载序 → 同名兄弟序），
不要拿 `candidates[0]` 当唯一解——那正是要避免的"猜"。

> `runtime.click` 用 `position` 时**没有场景概念**：叠加场景的同屏 UI 会按渲染顺序叠在一起，
> 命中谁取决于谁在上面（返回里的 `warning` 会指出"命中的不在目标分支上"）。
> 要精确定位就用 `target`，别用坐标。

两条容易搞错的语义：

- `runtime.scroll` 的 `amount` 是 **normalizedPosition 的位移量**（`1` = 从头滚到尾，`0.5` = 滚一半），
  正数往后翻、负数往回翻；它不是"像素"也不是"几屏"。
- `runtime.click` 的 `position` 是**屏幕像素**（左下角原点），与 `target` 二选一；
  两个都给时以 `position` 为准，`target` 只用来做遮挡比对。

### 三条硬边界（别硬扛）

1. **没有跨帧等待**。一条指令只占一帧，所以"点一下 → 等动画 → 再点"要**客户端自己控节奏**
   （两次 HTTP 之间 sleep，或轮询 `runtime.hierarchy` 观察状态变化）。`runtime.scroll` 也因此**没有松手惯性**。
2. **拿不到键盘/摇杆的硬输入**。游戏代码里 `Input.GetKeyDown`、新 Input System 直接读设备的部分，
   EventSystem 注入碰不到——这属于要改项目输入抽象才能解决的事，不是指令能绕过的。
3. **点击有真实副作用**。开开关前的确认、用完关掉，都是纪律问题不是技术问题。

### 排查"点了没反应"

按顺序看 `runtime.click` 的返回：

- `raycast.hitCount = 0` → 坐标上根本没有可交互元素（Canvas 缺 GraphicRaycaster / `raycastTarget=false`）
- `raycast.hits[0]` 不是你的目标 → 被挡住了，返回里 `warning` 会直说命中者和目标不在同一分支
- `dispatch.pointerClick` 为空 → 按到了但没人实现 `IPointerClickHandler`（纯装饰节点）
- `button.listenerCount = 0` → 按钮存在但没绑回调
- 被透明层挡住却确实要点 → `direct: true`，跳过射线直接对目标触发点击

## 端点与语义

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| GET | `/v1/ping` | 存活检测，返回协议版本与端口 |
| GET | `/v1/status` | 编辑器状态：`compiling` / `updating` / `queue` / `busy` / `last` / `currentCommandId` / `currentElapsedMs` / `lastCompile` |
| GET | `/v1/commands` | 已注册指令列表（含 `usage` 参数说明与 `editorOnly` 标记） |
| POST | `/v1/execute` | 下发指令；body 就是指令文件的 JSON 文本。查询串可带 `requestId` 做幂等重放 |
| POST | `/v1/cancel` | 请求协作式取消：当前批次在**下一条指令前**干净退出 |

请求头必须带 `X-Cmd-Token`（值取自信息文件）。服务端只绑 `127.0.0.1` 并校验 Host 头，
所以本 Skill 不能被用于访问远端机器。

### 幂等重放（`requestId`）

`POST /v1/execute?requestId=xxx` 会缓存**成功**的报告 5 分钟（LRU 上限 100 条）。
同一 ID 再次提交直接返回缓存结果、**不产生二次副作用**——批处理重试时用它避免"已成功的步骤被重复执行"。
失败/中止的结果不入缓存（重试语义由调用方决定）；不带 `requestId` 时行为与以前完全一致。

### 取消语义（重要限制）

取消是**协作式**的：`/v1/cancel` 只是置一个标记，执行器在**下一条指令之前**看到它才退出。
正在同步执行的**单条**指令（如 `scene.save`、大场景导入）**无法被打断**——.NET 没有安全的线程中止，
这是 Unity 主线程模型的硬约束，不是本系统能解决的。若某条指令真卡死，**只能重启编辑器**。

### 看门狗

单条指令耗时超过阈值（`WatchdogSeconds`，默认 120 秒，0 为关闭）会在 Editor.log 记一条 warning。
这同样是"事后可见"，不能中断已卡住的指令。执行进度可随时 `status` 查看：`currentCommandId` 为当前指令 ID、
`currentElapsedMs` 为实时耗时（空闲时为空串 / 0）。

## 响应该怎么读

HTTP 状态码只表示"请求是否被处理"，**业务结果看 body 里的 `ok`**：

| 状态码 | 含义 | 该怎么办 |
| --- | --- | --- |
| 200 且 `ok:true` | 全部指令成功 | 完事 |
| 200 且 `ok:false` | 有指令失败 | 先读 `summary`（一句话结论）与 `failures`（只含失败项），再按下面的自愈流程走 |
| 400 `bad_json` | body 不是合法指令 JSON | 修 JSON；报错带行列号 |
| 401 `unauthorized` | 令牌缺失或错误 | 重新读 `.http-info`，别手改令牌 |
| 404 `not_found` | 路径写错 | 只应打上表五个路径 |
| 405 | 方法用错 | GET 端点别用 POST |
| 413 | body 超过 4 MB | 拆分指令文件 |
| 503 `compiling` / `updating` | Unity 正在编译或刷新资源 | **等**，见下方纪律 |
| 连接失败 | 服务没启动 | 让用户去菜单开启，不要重试轰炸 |

`records` 里每条含 `index` / `id` / `status` / `message` / `data`。
`status` 取值：`Success` / `Failed` / `Skipped` / `UnknownCommand`。
`UnknownCommand` 说明指令 ID 拼错或未注册，用 `commands` 子命令核对。

> **失败时先读 `summary` 和 `failures`。** `summary` 是一句话结论
> （如 `⚠ 1/2 条指令失败：#1 asset.compile（明细见 failures，逐条见 records）`），
> `failures` 是只含失败项的精简数组（`index` / `id` / `status` / `message`，不含 `data`）。
> 两者都排在 `records` 之前，就是为了让"有东西失败了"这件事不可能被忽略——
> 别跳过它们直接翻 `records`，records 可能有几十条。

## 自愈流程（本 Skill 的核心价值）

拿到失败报告后，按这个顺序走，**不要直接把报错丢回给用户**：

1. 读 `summary` 拿到失败条数，再读 `failures` 逐条看 `message`（没有这两个字段的旧服务端，退回翻 `records`）
2. 常见成因与对策：
   - `找不到父节点 'X'` → 把 `parent` 改成 `type:Canvas` 或 `type:节点所在类型`；
     也可能该父节点还没创建，把创建它的指令移到前面
   - `未注册的指令 ID` → `send.py commands` 查正确 ID
   - `场景中找不到 Canvas` → 先下发 `ui_main_root.json` 建出 `MainUIRoot`
   - 切图 `sprite` 加载不到 → 先跑文件里的 `asset.importSprite` 指令，或确认目标路径
3. 改完对应 JSON 文件，重新 `send`
4. 最多重试 2 轮；仍在同一处失败就停下，把报告原文和你的判断交给用户

## 纪律（这些是踩过的坑，别绕）

- **先 `status` 再 `send`**。`compiling` 或 `updating` 为真时主线程泵不推进，
  发过去也是等到超时。要么等，要么先告诉用户。
- **`queue` 只增不减、或卡在"执行中"时，先怀疑"连错了进程"，别怪焦点**（2026-10-16 实测结论，
  此前写的"失焦导致停摆"是误判，已更正）。
  Unity 的**资源导入工作进程**（`AssetImportWorker0/1`，命令行带 `-batchMode`）也会加载 Editor 程序集、
  执行 `[InitializeOnLoad]`，于是同样会起监听、并写自己的信息文件。
  这类进程没有编辑器主循环，`EditorApplication.update` 不跑，指令就永久卡在它的队列里。
  表现与"失焦"一模一样：`ping` 正常、`compiling/updating` 都是 false、`queue` 却不降。
  **现在的客户端已自动规避**：发指令前会读每个候选端点的 `identity` 块，只连 `interactive=true` 的编辑器、
  且优先 `projectPath` 属于本工作区的，所以正常情况不会连错。若仍卡住，按下面排查：
  1. 先跑 `python scripts/send.py resolve`——它会列出全部候选端点并标出最终选择，
     一眼能看出有没有连到 `role=assetImportWorker` / `batchMode` 的冒牌货。
  2. 信息文件里的 **`pid`** 是不是那个窗口标题带工程名的 Unity 进程；
     `netstat -ano -p TCP | findstr 5783` 看这些端口分别归谁（Unity 本体应占首选端口 57831）。
  3. `Get-CimInstance Win32_Process -Filter "ProcessId=<pid>"` 看命令行：
     带 `-batchMode -name AssetImportWorkerN` 的就是冒牌货。
  4. 代码侧护栏：`CommandHttpServerBoot` 检测到 `-batchMode` 直接返回，不启动服务；
     每个交互式编辑器写两份信息文件（`.http-info.<hash>` 主文件 + `.http-info.<hash>.<pid>` 自述副本），
     客户端据此枚举全部候选；编辑器每秒自检主文件，被别的进程删掉会自动补写。
  5. 应急：用 `--url http://127.0.0.1:<真实端口>/` 直连编辑器本体（令牌通用，因为存在 EditorPrefs 里）；
     确认要发给非编辑器端点就加 `--force`。
- **不要用抢焦点 / 置顶的方式去"唤醒" Unity。** 实测 `SetForegroundWindow`、`SetWindowPos(HWND_TOPMOST)`
  都唤不醒一个不跑主循环的进程，反而有副作用（TOPMOST 会挡住 Win+Tab，且被超时中断时恢复代码可能跑不到）。
  要诊断就看进程命令行与端口归属，不要动用户的窗口。
- **编译期间发指令会导致连接被断**。Unity 重编译会重建 AppDomain，
  服务端会把队列里的请求以「正在重新编译」终结。遇到连接中断就重发一次，不要连续重发。
- **`asset.importSprite` 之后立刻引用该 sprite 可能拿到 null**。
  资源导入是异步的，中间夹一条无关指令或稍等再发。
- **一次只发一个指令文件**。文件内部指令按顺序执行，文件之间没有保证；
  有依赖就合并进同一个文件，或用 `run_file` 指令串起来。
- **`execute` 之间按 FIFO 串行执行**，但查询类请求（`status` / `commands` / `cancel`）可以在指令执行期间并发访问
  ——服务端每个请求独立线程，执行统一经队列交给主线程。所以执行中也能查进度、发取消。
  不要靠并发 `execute` 来"加速"，它们只会排队。
- **`file.*` 只能读写工程目录内**。默认允许根是 `ProjectRoot` 与 `WorkspaceRoot`；
  `.git/`、`ProjectSettings/`、`Library/`、`Temp/`、`Assets/Editor/ExternalCommandSystem/` 是黑名单，
  即使在根内也禁止。越界会返回"路径越界 / 路径受限"并列出允许的根。
  确实需要访问工程外目录时，在 `EditorPrefs` 的 `CivilRev.CommandSystem.FileAccessRoots`
  （分号分隔的绝对路径）里显式追加。
- **拿不准就先 `--dry-run`**。它走完整条解析与注册表查找，但不真正执行，
  能在零副作用的前提下验证 JSON 和指令 ID 是否正确。
- 新写的指令文件先用 `--dry-run` 验一遍，再正式发。

## 只有 HTTP 这一条通路

下发指令**只有 HTTP 一条路**。曾经存在过的「把 JSON 存进指令目录、`FileSystemWatcher` 自动执行」
那条通路已经彻底移除（`AutoWatchEnabled` / `HttpDisableFileWatch` 等设置项也一并删了），
**不存在"保存文件即自动执行"**。

指令目录 `.workbuddy/UnityEditCommand/` 现在只是**存放 JSON 的地方**：在这里写好文件后，
必须显式 `send` 才会执行。想批量执行用控制台窗口的「扫描目录」，或逐条 `send`。
写在这里的好处是可人工审查、可版本管理，执行走 HTTP 拿回结构化报告。

## 文件位置

| 内容 | 路径 |
| --- | --- |
| 服务信息（端口 + 令牌） | `<工作空间>/.workbuddy/UnityEditCommand/.http-info.<projectHash>` |
| 指令文件 | `<工作空间>/.workbuddy/UnityEditCommand/*.json` |
| 本 Skill | `<工作空间>/.workbuddy/skills/unity-cmd/` |
| 服务端源码 | `CivilizationRevival/Assets/Editor/ExternalCommandSystem/CommandHttpServer.cs` |
| 协议层源码 | `CivilizationRevival/Assets/Editor/ExternalCommandSystem/Core/HttpProtocol.cs` |
| 路径策略源码 | `CivilizationRevival/Assets/Editor/ExternalCommandSystem/Core/CommandPathUtil.cs` |
