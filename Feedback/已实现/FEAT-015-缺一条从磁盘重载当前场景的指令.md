---
id: FEAT-015
type: feature
title: 缺一条「从磁盘重载当前打开的场景」的指令，外部改完 .unity 文本后只能靠 asset.refresh 反复赌
status: done
source: project_island_back
created: 2026-10-28
updated: 2026-10-29
files: Packages/com.aketsuki.unity-mcp/Editor/Commands/SceneCommands.cs
proposal: true
---

## 症状

只要有字段必须**直接改 `.unity` 文本**（典型原因见 IMPR-007：数组字段没法用 `component.setField` 配），改完之后让编辑器看到新内容没有一条可靠指令：

- `component.get` / `scene.inspect` 读的还是内存里的旧值（编辑器打开的场景不会因为磁盘文件变了就自动重读）
- `asset.refresh` 只调 `AssetDatabase.Refresh()`（`EditorCommands.cs:17`），实测**调一次不生效**，同一条指令里连调两次之后才读到新值

我这次的实际流程是「`component.add` → `scene.save` → 手改 `.unity` → `asset.refresh` ×2 → `component.get` 验证 → `scene.save`」，能跑通，但**哪一次 refresh 真正让场景重读磁盘，我没有隔离验证**——也可能是第二次 refresh 之前的 `component.get` 恰好触发了重导入，或者纯粹是时序凑巧。所以这条的「复现」只到「一次不够、两次成了」，不到「为什么」。

## 期望

一条 `scene.reload`：

- 对当前打开的场景执行 `EditorSceneManager.OpenScene(path, OpenSceneMode.Single)`，或 `EditorSceneManager.SaveOpenScenes` 之外的 `CloseScene + OpenScene`
- 语义明确：**丢弃内存中的未保存改动，以磁盘为准**
- 返回重载前后是否一致（或者干脆返回重载后的场景 `mtime`），好让调用方判断值不值得重载

## 待拍板

| | 做好处 | 代价 |
|---|---|---|
| 加 `scene.reload` | 外部改场景文本这条工作流有确定性终点，不用再赌 refresh 次数；验证类操作（`asset.refresh` ×N）从流程里消失 | 新指令；且「丢内存改动」是破坏性操作，得配 consent / `force` |
| 不加，改做「refresh 后自动重载打开的场景」 | 现有工作流无感升级 | 把重载挂在 refresh 上是隐性行为：有人手上有未保存改动时刷个资源就把改动刷没了，比显式指令危险得多 |
| 不加，只在文档里写清 `asset.refresh` 要调两次 | 零改动 | 「调两次」是基于现象的经验，没有机制保证；换 Unity 版本或换个时序就可能不成立 |

**我的倾向**：加显式 `scene.reload`，**不要**把重载挂到 `asset.refresh` 上。理由：破坏性语义必须显式表达；`asset.refresh` 现在被到处当「安全刷新」用，给它加副作用迟早出事。

另外建议 `scene.reload` 落在 `ConsentGate` 的敏感操作里（和 `scene.delete`、`editor.stop` 同级），因为「未保存改动」在编辑器里可能就是人手动调的半小时。

**用户批注**：（等待）

---

## 执行结果（2026-10-29 第七跑，用户批注「可以」）

新增指令 `scene.reload`（`SceneReloadCommand`），落在 `SceneCommands.cs`：

| 项 | 实现 |
| --- | --- |
| 语义 | `EditorSceneManager.OpenScene(path, OpenSceneMode.Single)` —— 关掉内存里的场景、按路径从磁盘重开，**以磁盘为准** |
| 参数 | `path`（可选，默认当前活跃场景）、`force`（默认 false） |
| 未保存改动 | 脏场景**默认拒绝**并列出脏场景清单；`force:true` 才真的丢。`data.discardedDirtyCount` 回报丢了几份 |
| 返回值 | `scenePath` / `sceneName` / `diskModifiedUtc`（磁盘文件 mtime，让调用方判断值不值得重载）/ `rootCount` / `isDirtyAfter` |
| 门禁 | 纳入 `ConsentGate`（`RequiresConsent`，与 `scene.open` 同级），未开「全权委托」时要人点「允许」 |
| Play 模式 | 直接失败并说明，不进 Play 里动场景 |

**没有**把重载挂到 `asset.refresh` 上（批件里否掉的那条）：`asset.refresh` 现在被到处当安全刷新用，给它加副作用迟早出事。

真机（island_back，2026-10-29）：指令已注册、参数已解析、门禁按预期返回 `consentId`。
**真正执行一次被门禁拦下**（要人点「允许」），所以"重载完 `component.get` 读到新值"这一步未实测 —— 已记进 `HANDOVER.md` §8.1。