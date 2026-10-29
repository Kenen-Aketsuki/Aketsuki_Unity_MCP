using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 打开指定场景。
    /// <para>
    /// <b>需要人工确认</b>：确认由统一门禁 <see cref="ConsentGate"/> 在前置阶段弹窗（「允许 / 拒绝」，
    /// 默认 30 秒超时视为拒绝）；控制台勾选「全权委托」后直接放行、不再询问。
    /// 拒绝或超时时整批中止（后续指令标记为 Skipped），避免在错误的场景上继续操作——
    /// 即使 <c>stopOnError=false</c> 也会中止。
    /// </para>
    /// <para>参数：path（必填，完整 Assets 路径或场景名）、additive（默认 false）、saveCurrent（默认 true）。</para>
    /// </summary>
    [Command("scene.open", Description = "打开指定场景（需人工确认；拒绝/超时中止本批次）",
        Usage = "path: string, additive: bool, saveCurrent: bool", EditorOnly = true,
        RequiresConsent = true,
        ConsentMessage = "打开场景：当前场景会被关闭或叠加（是否保存由 saveCurrent 决定，有未保存改动且 saveCurrent=false 时直接拒绝）。")]
    public sealed class SceneOpenCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            if (context.DryRun)
            {
                return CommandResult.Skip("dry-run：将请求打开场景（需用户允许），不实际切换");
            }

            if (EditorApplication.isPlaying)
            {
                return CommandResult.Fail("编辑器处于 Play 模式，无法切换场景；请先退出 Play 模式");
            }

            var input = context.Args.RequireString("path");
            var resolved = ResolveScenePath(input);
            if (string.IsNullOrEmpty(resolved))
            {
                return CommandResult.Fail($"找不到场景：{input}（支持完整 Assets 路径或场景名）");
            }

            // 用户已在门禁里确认过（或被「全权委托」放行），这里只管保存与切换
            var saveCurrent = context.Args.GetBool("saveCurrent", true);
            if (AnySceneDirty())
            {
                if (saveCurrent)
                {
                    if (!EditorSceneManager.SaveOpenScenes())
                    {
                        return CommandResult.Fail("当前场景存在未保存改动且保存失败（可能存在未命名场景），已中止打开");
                    }
                }
                else
                {
                    return CommandResult.Fail("当前场景有未保存改动且 saveCurrent=false；为防丢失已拒绝打开");
                }
            }

            var additive = context.Args.GetBool("additive", false);
            var scene = EditorSceneManager.OpenScene(
                resolved,
                additive ? OpenSceneMode.Additive : OpenSceneMode.Single);

            var data = JsonValue.NewObject();
            data.Set("scenePath", JsonValue.Of(scene.path));
            data.Set("sceneName", JsonValue.Of(scene.name));
            return CommandResult.Ok($"已打开场景：{scene.name}", data);
        }

        private static string ResolveScenePath(string input)
        {
            return ScenePathResolver.Resolve(input);
        }

        private static bool AnySceneDirty()
        {
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                if (EditorSceneManager.GetSceneAt(i).isDirty)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// 从磁盘重载场景：丢掉内存里的改动，以 .unity 文件为准（FEAT-015）。
    ///
    /// <para>
    /// <b>为什么需要它：</b>有些字段只能直接改 <c>.unity</c> 文本。改完之后编辑器<b>不会</b>自动重读磁盘，
    /// <c>component.get</c> 读到的仍是内存里的旧值；此前唯一的办法是连调两次 <c>asset.refresh</c>
    /// ——"调两次"是从现象里试出来的经验，没有机制保证，换时序就可能不成立。
    /// 本指令把这件事变成一条语义确定的操作：<b>关掉内存里的场景、按路径重新打开</b>。
    /// </para>
    ///
    /// <para>
    /// <b>未保存改动默认拒绝：</b>重载等于丢弃，而"未保存改动"可能是人手动调了半小时的参数。
    /// 所以脏场景一律拒绝并列出清单，显式 <c>force:true</c> 才真的丢。指令本身同时走
    /// <see cref="ConsentGate"/>（与 <c>scene.open</c> 同级），未开「全权委托」时还要人点一次「允许」。
    /// </para>
    ///
    /// <para>
    /// 参数：path（可选，默认当前活跃场景）、force（默认 false；场景脏时是否仍丢弃内存改动）
    /// </para>
    /// </summary>
    [Command("scene.reload",
        Description = "从磁盘重载场景（丢弃内存中的未保存改动，以 .unity 为准；脏场景需 force）",
        Usage = "path: string, force: bool", EditorOnly = true,
        RequiresConsent = true,
        ConsentMessage = "从磁盘重载场景：内存中未保存的改动会被丢弃，场景内容回到磁盘文件的状态。")]
    public sealed class SceneReloadCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            if (context.DryRun)
            {
                return CommandResult.Skip("dry-run：将从磁盘重载场景（需用户允许），不实际重载");
            }

            if (EditorApplication.isPlaying)
            {
                return CommandResult.Fail("编辑器处于 Play 模式，无法重载场景；请先退出 Play 模式");
            }

            var input = context.Args.GetString("path", null);
            var path = string.IsNullOrEmpty(input)
                ? SceneManager.GetActiveScene().path
                : ScenePathResolver.Resolve(input);

            if (string.IsNullOrEmpty(path))
            {
                return string.IsNullOrEmpty(input)
                    ? CommandResult.Fail("当前没有可重载的场景（活跃场景未保存到磁盘，请先 Save As 或直接传 path）")
                    : CommandResult.Fail($"找不到场景：{input}（支持完整 Assets 路径或场景名）");
            }

            var fullPath = Path.GetFullPath(path);
            var modifiedUtc = ReadModifiedUtc(fullPath);

            var dirty = new List<string>();
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (scene.isDirty)
                {
                    dirty.Add(string.IsNullOrEmpty(scene.path) ? "(未保存的新场景)" : scene.path);
                }
            }

            var force = context.Args.GetBool("force", false);
            if (dirty.Count > 0 && !force)
            {
                return CommandResult.Fail(
                    $"有 {dirty.Count} 个场景存在未保存改动（{string.Join("、", dirty.ToArray())}），" +
                    "重载会丢弃它们。先 scene.save 保存，或显式传 force: true 表示确实要丢。");
            }

            // Single 模式会关掉当前所有已打开场景并按路径重新从磁盘加载——
            // 这正是"以磁盘为准"所需要的语义，不需要先 Close 再 Open。
            var reloaded = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);

            var data = JsonValue.NewObject();
            data.Set("scenePath", JsonValue.Of(reloaded.path));
            data.Set("sceneName", JsonValue.Of(reloaded.name));
            data.Set("force", JsonValue.Of(force));
            data.Set("discardedDirtyCount", JsonValue.Of(dirty.Count));
            data.Set("diskModifiedUtc", modifiedUtc != null
                ? JsonValue.Of(modifiedUtc.Value.ToString("o"))
                : JsonValue.Null);
            data.Set("rootCount", JsonValue.Of(reloaded.rootCount));
            data.Set("isDirtyAfter", JsonValue.Of(reloaded.isDirty));

            return CommandResult.Ok(
                "已从磁盘重载场景：" + reloaded.name +
                (dirty.Count > 0 ? $"（丢弃了 {dirty.Count} 个场景的未保存改动）" : "（重载前没有未保存改动）"),
                data);
        }

        /// <summary>磁盘文件的最后修改时间（读不到就返回 null，不因此失败）。</summary>
        private static DateTime? ReadModifiedUtc(string fullPath)
        {
            try
            {
                return File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath) : (DateTime?)null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 枚举场景：Build Settings 中启用的场景 + 磁盘 Assets 下全部 .unity（标注 inBuildSettings）。
    /// <para>参数：无。</para>
    /// </summary>
    [Command("scene.list", Description = "列出场景（Build Settings + 磁盘全量，标注 inBuildSettings）", EditorOnly = true)]
    public sealed class SceneListCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var inBuild = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var entry in EditorBuildSettings.scenes)
            {
                if (entry.enabled)
                {
                    inBuild.Add(entry.path);
                }
            }

            var arr = JsonValue.NewArray();
            foreach (var guid in AssetDatabase.FindAssets("t:Scene"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var obj = JsonValue.NewObject();
                obj.Set("path", JsonValue.Of(path));
                obj.Set("name", JsonValue.Of(Path.GetFileNameWithoutExtension(path)));
                obj.Set("inBuildSettings", JsonValue.Of(inBuild.Contains(path)));
                arr.Add(obj);
            }

            return CommandResult.Ok(
                $"共 {arr.Count} 个场景（其中 {inBuild.Count} 个在 Build Settings 启用）", arr);
        }
    }

    /// <summary>
    /// 场景路径解析共用工具（<see cref="SceneOpenCommand"/> / <see cref="SceneDeleteCommand"/> 共享）
    /// <para>支持：完整 Assets/.unity 路径（存在才认）、场景名、带 .unity 后缀的文件名；
    /// 先查 Build Settings，再查磁盘全量 Assets 下的 .unity。</para>
    /// </summary>
    internal static class ScenePathResolver
    {
        public static string Resolve(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return null;
            }

            if (input.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase)
                && input.EndsWith(".unity", System.StringComparison.OrdinalIgnoreCase))
            {
                return AssetDatabase.LoadAssetAtPath<SceneAsset>(input) != null ? input : null;
            }

            var fileName = input.EndsWith(".unity", System.StringComparison.OrdinalIgnoreCase)
                ? input
                : input + ".unity";

            // Build Settings 优先
            foreach (var entry in EditorBuildSettings.scenes)
            {
                if (string.Equals(Path.GetFileName(entry.path), fileName, System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.path, "Assets/" + fileName, System.StringComparison.OrdinalIgnoreCase))
                {
                    return entry.path;
                }
            }

            // 磁盘全量（Assets 下所有 .unity）
            foreach (var guid in AssetDatabase.FindAssets("t:Scene"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.Equals(Path.GetFileName(path), fileName, System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileNameWithoutExtension(path), input, System.StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// 新建一个场景资产——补全 CRUD 的"增场景"。
    /// <para>
    /// 用 <c>NewScene(Additive)</c> 在内存里新建空白场景后 <c>SaveScene</c> 到指定 Assets 路径，
    /// 全程不关闭当前已打开的场景，避免未保存改动丢失；保存后默认关闭新建场景（open=false）。
    /// 可选 <c>addToBuildSettings</c> 把它加进 Build Settings 并启用。
    /// </para>
    /// <para>参数：path（必填，如 "Assets/Scenes/New.unity" 或 "Scenes/New"，自动补 Assets/ 与 .unity）、
    /// open（默认 false）、addToBuildSettings（默认 false）</para>
    /// </summary>
    [Command("scene.create", Description = "新建场景资产（Additive 保存，可选加入 Build Settings）",
        Usage = "path: string, open: bool, addToBuildSettings: bool", EditorOnly = true)]
    public sealed class SceneCreateCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            if (EditorApplication.isPlaying)
            {
                return CommandResult.Fail("编辑器处于 Play 模式，无法新建场景；请先退出 Play 模式");
            }

            var input = context.Args.RequireString("path");
            var assetPath = NormalizeScenePath(context, input);
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(assetPath) != null)
            {
                return CommandResult.Fail($"场景已存在：{assetPath}（如需覆盖请先 scene.delete 或改用其它路径）");
            }

            EnsureFolders(assetPath);

            var openAfter = context.Args.GetBool("open", false);
            var addToBuild = context.Args.GetBool("addToBuildSettings", false);

            // Additive：不关闭当前场景，杜绝丢档；随后把新场景保存到目标路径
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var saved = EditorSceneManager.SaveScene(scene, assetPath, false);
            if (!saved)
            {
                EditorSceneManager.CloseScene(scene, true);
                return CommandResult.Fail($"保存场景失败：{assetPath}（确认父目录可写且不在黑名单内）");
            }

            if (!openAfter)
            {
                EditorSceneManager.CloseScene(scene, true);
            }

            if (addToBuild)
            {
                AddSceneToBuildSettings(assetPath);
            }

            AssetDatabase.SaveAssets();

            var data = JsonValue.NewObject();
            data.Set("scenePath", JsonValue.Of(assetPath));
            data.Set("open", JsonValue.Of(openAfter));
            data.Set("inBuildSettings", JsonValue.Of(addToBuild));
            return CommandResult.Ok(
                $"已创建场景：{assetPath}" + (addToBuild ? "（已加入 Build Settings）" : ""), data);
        }

        private static string NormalizeScenePath(CommandContext context, string input)
        {
            var text = (input ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
            if (text.Length == 0)
            {
                throw new CommandArgumentException(context.CommandId, "path", "场景路径不能为空");
            }

            if (!text.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase))
            {
                text = "Assets/" + text;
            }

            if (!text.EndsWith(".unity", System.StringComparison.OrdinalIgnoreCase))
            {
                text += ".unity";
            }

            try
            {
                CommandPathUtil.EnsureAccessible(Path.GetFullPath(text));
            }
            catch (Exception ex)
            {
                throw new CommandArgumentException(context.CommandId, "path", ex.Message);
            }

            return text;
        }

        /// <summary>逐级创建 Assets 下缺失的文件夹（AssetDatabase.CreateFolder 保证 .meta/GUID 正常）</summary>
        private static void EnsureFolders(string assetPath)
        {
            var segments = assetPath.Split('/');
            // segments[0] = "Assets"，最后一段是文件名，中间的都是文件夹
            for (var i = 1; i < segments.Length - 1; i++)
            {
                var folder = string.Join("/", segments, 0, i + 1);
                if (!AssetDatabase.IsValidFolder(folder))
                {
                    // AssetDatabase.CreateFolder 返回新文件夹的 GUID（失败才是空串），不是错误信息
                    var guid = AssetDatabase.CreateFolder(
                        string.Join("/", segments, 0, i), segments[i]);
                    if (string.IsNullOrEmpty(guid) || !AssetDatabase.IsValidFolder(folder))
                    {
                        throw new CommandException($"创建文件夹 '{folder}' 失败（AssetDatabase.CreateFolder 未返回 GUID）");
                    }
                }
            }
        }

        private static void AddSceneToBuildSettings(string assetPath)
        {
            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes)
            {
                new EditorBuildSettingsScene(assetPath, true)
            };
            EditorBuildSettings.scenes = list.ToArray();
        }
    }

    /// <summary>
    /// 删除场景资产文件——补全 CRUD 的"删场景资产"。
    /// <para>
    /// 与 <c>scene.open</c>（查/切）/ <c>scene.create</c>（增）配套。直接走
    /// <c>AssetDatabase.DeleteAsset</c>，路径同样过 <see cref="CommandPathUtil"/> 白名单。
    /// 若目标场景当前已加载到编辑器（哪怕未修改），会先拒绝并提示关闭，避免 Unity 删已打开资产报错。
    /// </para>
    /// <para>参数：path（必填，完整 Assets 路径或场景名，同 scene.open 解析）</para>
    /// </summary>
    [Command("scene.delete", Description = "删除场景资产文件（AssetDatabase.DeleteAsset）",
        Usage = "path: string", EditorOnly = true)]
    public sealed class SceneDeleteCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            if (EditorApplication.isPlaying)
            {
                return CommandResult.Fail("编辑器处于 Play 模式，无法删除场景；请先退出 Play 模式");
            }

            var input = context.Args.RequireString("path");
            var resolved = ScenePathResolver.Resolve(input);
            if (string.IsNullOrEmpty(resolved))
            {
                return CommandResult.Fail($"找不到场景：{input}（支持完整 Assets 路径或场景名）");
            }

            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var loaded = EditorSceneManager.GetSceneAt(i);
                if (string.Equals(loaded.path, resolved, System.StringComparison.OrdinalIgnoreCase))
                {
                    return CommandResult.Fail($"场景 '{resolved}' 当前已打开，请先在编辑器中关闭它再删除");
                }
            }

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(resolved) == null)
            {
                return CommandResult.Fail($"场景资产不存在：{resolved}");
            }

            if (!AssetDatabase.DeleteAsset(resolved))
            {
                return CommandResult.Fail($"删除场景失败：{resolved}");
            }

            AssetDatabase.SaveAssets();

            var data = JsonValue.NewObject();
            data.Set("scenePath", JsonValue.Of(resolved));
            return CommandResult.Ok($"已删除场景：{resolved}", data);
        }
    }
}
