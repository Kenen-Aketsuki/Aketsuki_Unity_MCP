using System;
using System.IO;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace CivilRev.CommandSystem.Editor
{
    /// <summary>
    /// 把场景节点（含子树）保存为 Prefab 资产，打通「MCP 搭 UI → 变成运行期可加载资源」的最后一环
    /// <para>
    /// 运行期 UI 经 <c>[BindUIPrefab]</c> 从 <c>Assets/GameAssets/UI</c> 加载，
    /// 本指令用 <c>SaveAsPrefabAssetAndConnect</c> 保存并保持 Prefab 连接——
    /// 之后改场景实例还能 <c>component.setField</c> + 保存同步回 Prefab。
    /// </para>
    /// <para>
    /// 参数：
    /// <list type="bullet">
    /// <item><c>target</c> 必填，场景节点（名字 / 路径 / type:XXX）</item>
    /// <item><c>path</c> 目标资产路径。<c>"Panel_PieceInfo"</c> 或 <c>"Dialog/Panel_PieceInfo"</c>
    /// 这类不带 Assets 前缀的会落到 <c>Assets/GameAssets/UI/</c> 下（缺 .prefab 后缀自动补）；
    /// 写全 <c>Assets/...</c> 则按写的来</item>
    /// <item><c>overwrite</c> 目标已存在时是否覆盖（覆盖 = 更新既有 Prefab，GUID 不变、
    /// 所有实例同步更新）。默认 false，防止误覆盖手工维护的 Prefab</item>
    /// <item><c>removeSceneInstance</c> 保存后是否删除场景实例，默认 false</item>
    /// <item><c>addressable</c> 保存后自动注册 Addressable 条目，默认 true（需求案 B7：
    /// 未注册的 Prefab 编辑器态一切正常，进 Play 才抛 InvalidKeyException，必须保存时闭环）。
    /// 注册走反射桥，未装 Addressables 的工程自动跳过并在消息里注明</item>
    /// <item><c>addressableGroup</c> 目标组名，缺省用工程 Default Group</item>
    /// <item><c>addressableLabels</c> 追加的标签（字符串或数组）</item>
    /// <item><c>select</c> 保存后是否在 Project 窗口选中资产，默认 false</item>
    /// </list>
    /// </para>
    /// </summary>
    [Command("ui.savePrefab", Description = "把场景节点保存为 Prefab（保留 Prefab 连接，默认自动注册 Addressable，可选删除场景实例）",
        Usage = "target: string, path: string, overwrite: bool, removeSceneInstance: bool, addressable: bool, addressableGroup: string, addressableLabels: string[], select: bool",
        EditorOnly = true)]
    public sealed class SavePrefabCommand : ICommand
    {
        /// <summary>省略路径时的默认落点（与运行期 [BindUIPrefab] 的加载根一致）</summary>
        public const string DefaultFolder = "Assets/GameAssets/UI";

        public CommandResult Execute(CommandContext context)
        {
#if UNITY_EDITOR
            var targetSpec = context.Args.RequireString("target");
            var node = SceneQuery.ResolveTarget(targetSpec);
            if (node == null)
            {
                throw new CommandArgumentException(context.CommandId, "target",
                    $"找不到节点 '{targetSpec}'");
            }

            if (!node.gameObject.scene.IsValid())
            {
                throw new CommandArgumentException(context.CommandId, "target",
                    $"'{targetSpec}' 不是场景中的物体（Prefab 资源本身不能再保存为 Prefab）");
            }

            var assetPath = NormalizePath(context, node.name, context.Args.GetString("path", null));
            var overwrite = context.Args.GetBool("overwrite", false);

            if (!overwrite && AssetDatabase.LoadAssetAtPath<GameObject>(assetPath) != null)
            {
                throw new CommandArgumentException(context.CommandId, "path",
                    $"Prefab 已存在：{assetPath}。确认要更新它时传 overwrite=true（GUID 不变，所有实例同步更新）");
            }

            EnsureFolders(assetPath);

            var connected = PrefabUtility.SaveAsPrefabAssetAndConnect(
                node.gameObject, assetPath, InteractionMode.AutomatedAction);
            if (connected == null)
            {
                throw new CommandException($"保存 Prefab 失败：{assetPath}");
            }

            AssetDatabase.SaveAssets();

            // 保存后闭环注册 Addressable（B7）：未装包的工程 EnsureEntry 会抛 CommandException，
            // 这里按"软失败"处理——prefab 已保存成功，注册失败只注明原因，不让整条指令回滚
            var addressableNote = "，Addressable：未启用";
            if (context.Args.GetBool("addressable", true))
            {
                try
                {
                    var address = AddressablesBridge.EnsureEntry(
                        assetPath, null, context.Args.GetString("addressableGroup", null),
                        AddressablesBridge.ParseStringArray(context.Args.Get("addressableLabels")),
                        out var created);
                    addressableNote = (created ? "，Addressable 已注册：" : "，Addressable 条目已存在：") + address;
                }
                catch (CommandException ex)
                {
                    addressableNote = "，Addressable 未注册（" + ex.Message + "）";
                }
            }

            var removeSceneInstance = context.Args.GetBool("removeSceneInstance", false);
            if (removeSceneInstance)
            {
                var scene = node.gameObject.scene;
                Undo.DestroyObjectImmediate(node.gameObject);
                if (scene.IsValid())
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                }
            }

            if (context.Args.GetBool("select", false))
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (asset != null)
                {
                    Selection.activeObject = asset;
                }
            }

            var data = JsonValue.NewObject();
            data.Set("path", JsonValue.Of(assetPath));
            data.Set("connected", JsonValue.Of(true));
            data.Set("removedSceneInstance", JsonValue.Of(removeSceneInstance));
            return CommandResult.Ok(
                $"已保存 Prefab：{assetPath}（Prefab 连接已保留"
                + (removeSceneInstance ? "，场景实例已删除" : "")
                + addressableNote + "）",
                data);
#else
            throw new CommandException($"指令 '{context.CommandId}' 只能在编辑器内执行");
#endif
        }

#if UNITY_EDITOR
        /// <summary>
        /// 归一化目标路径：不带 Assets/ 前缀的落到 <see cref="DefaultFolder"/>，缺后缀补 .prefab。
        /// 归一化结果仍要走 <see cref="CommandPathUtil"/> 白名单，避免借本指令写到工程外。
        /// </summary>
        private static string NormalizePath(CommandContext context, string nodeName, string raw)
        {
            var text = (raw ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
            if (text.Length == 0)
            {
                text = nodeName;
            }

            if (!text.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                text = DefaultFolder + "/" + text;
            }

            if (!text.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                text += ".prefab";
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

        /// <summary>逐级创建 Assets 下缺失的文件夹（AssetDatabase.CreateFolder 保证有 .meta、GUID 正常）</summary>
        private static void EnsureFolders(string assetPath)
        {
            var segments = assetPath.Split('/');
            // segments[0] = "Assets"，最后一段是文件名，中间的都是文件夹
            for (var i = 1; i < segments.Length - 1; i++)
            {
                var folder = string.Join("/", segments, 0, i + 1);
                if (!AssetDatabase.IsValidFolder(folder))
                {
                    var error = AssetDatabase.CreateFolder(
                        string.Join("/", segments, 0, i), segments[i]);
                    if (!string.IsNullOrEmpty(error))
                    {
                        throw new CommandException($"创建文件夹 '{folder}' 失败：{error}");
                    }
                }
            }
        }
#endif
    }

    /// <summary>
    /// 删除 Prefab 资产文件——补全 CRUD 的"删预制体资产"。
    /// <para>
    /// 与 <c>ui.savePrefab</c>（增）/ <c>prefab.tree</c>（查）配套。直接走
    /// <c>AssetDatabase.DeleteAsset</c>，路径同样过 <see cref="CommandPathUtil"/> 白名单，
    /// 不会越界删到工程外。删除后 <c>SaveAssets</c> 持久化。
    /// </para>
    /// <para>参数：prefab（必填，Assets/ 下的 .prefab 路径）</para>
    /// </summary>
    [Command("prefab.delete", Description = "删除 Prefab 资产文件（AssetDatabase.DeleteAsset）",
        Usage = "prefab: string", EditorOnly = true)]
    public sealed class DeletePrefabCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
#if UNITY_EDITOR
            var prefabPath = PrefabEditUtil.NormalizePrefabPath(context, context.Args.RequireString("prefab"));
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
            {
                throw new CommandArgumentException(context.CommandId, "prefab",
                    $"Prefab 不存在：{prefabPath}");
            }

            if (!AssetDatabase.DeleteAsset(prefabPath))
            {
                throw new CommandException($"删除 Prefab 失败：{prefabPath}");
            }

            AssetDatabase.SaveAssets();

            var data = JsonValue.NewObject();
            data.Set("path", JsonValue.Of(prefabPath));
            return CommandResult.Ok($"已删除 Prefab：{prefabPath}", data);
#else
            throw new CommandException($"指令 '{context.CommandId}' 只能在编辑器内执行");
#endif
        }
    }
}
