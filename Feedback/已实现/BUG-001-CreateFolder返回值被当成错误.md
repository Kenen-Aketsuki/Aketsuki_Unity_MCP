---
id: BUG-001
type: bug
title: ui.savePrefab / scene.create 建目录必失败——把 CreateFolder 返回的 GUID 当成了错误信息
status: done
source: civilization_revival
created: 2026-10-23
updated: 2026-10-23
files: Commands/PrefabCommands.cs, Commands/SceneCommands.cs
---

## 症状

目标目录不存在、需要自动逐级建文件夹时，**任何一次 `ui.savePrefab` / `scene.create` 都会失败**，
即使目录完全合法。已存在的目录不受影响，所以只有"第一次往新文件夹里存东西"才踩到。

## 复现

```json
{"commands":[{"id":"ui.savePrefab","params":{"target":"SomeRoot","path":"Assets/__McpSelfCheck/Src.prefab"}}]}
```

期望：建出 `Assets/__McpSelfCheck/` 并保存 Prefab。
实际：直接失败，报错里带一串 32 位十六进制。

报错原文：

```
创建文件夹 'Assets/__McpSelfCheck' 失败：a426e18bbfd8e48408b282283121e584
```

## 根因

`AssetDatabase.CreateFolder(parent, name)` 的返回值是**新文件夹的 GUID**（成功时非空），
失败才返回空串。原代码把返回值命名成 `error` 并判 `!string.IsNullOrEmpty(error)` → 抛异常，
等于"成功即失败"。报错里那串十六进制就是 GUID。

`Commands/PrefabCommands.cs` 与 `Commands/SceneCommands.cs` 各有一份同样的 `EnsureFolders`，两处都错。

## 怎么修的

判断条件反过来，并且补一道 `IsValidFolder` 兜底（有些环境 CreateFolder 返回 GUID 但目录没真正建出来）：

```csharp
var guid = AssetDatabase.CreateFolder(parent, segments[i]);
if (string.IsNullOrEmpty(guid) || !AssetDatabase.IsValidFolder(folder))
{
    throw new CommandException($"创建文件夹 '{folder}' 失败（AssetDatabase.CreateFolder 未返回 GUID）");
}
```

两处 `EnsureFolders` 同步修。实测 `Assets/__McpSelfCheck/Src.prefab` 一次建成，目录与 `.meta` 正常。
