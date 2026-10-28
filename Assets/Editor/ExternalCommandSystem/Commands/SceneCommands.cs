using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CivilRev.CommandSystem.Editor
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
