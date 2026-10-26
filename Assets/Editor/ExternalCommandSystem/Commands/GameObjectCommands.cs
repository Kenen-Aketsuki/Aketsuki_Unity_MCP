using UnityEngine;

#if UNITY_EDITOR
using CivilRev.CommandSystem.Editor;
using UnityEditor;
#endif

namespace CivilRev.CommandSystem
{
    /// <summary>
    /// 创建 GameObject
    /// <para>
    /// 参数：name（默认 "CommandObject"）、position（[x,y,z] 或 {x,y,z}）、
    /// primitive（cube/sphere/capsule/cylinder/plane/quad，可选）、parent（父物体，可选）、tag（可选）
    /// </para>
    /// <para>
    /// parent 支持三种写法：名字（"HUD"）、层级路径（"UIRoot/HUD"）、按类型查找（"type:Canvas"）；
    /// 也能找到 inactive 节点。规则见 <see cref="SceneQuery"/>。
    /// </para>
    /// </summary>
    [Command("gameobject.create", Description = "创建一个 GameObject",
        Usage = "name: string, position: [x,y,z], primitive: string, parent: string, tag: string")]
    public sealed class CreateGameObjectCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var name = context.Args.GetString("name", "CommandObject");
            var position = context.Args.GetVector3("position", Vector3.zero);
            var primitiveText = context.Args.GetString("primitive", null);

            GameObject go;
            if (!string.IsNullOrEmpty(primitiveText) &&
                System.Enum.TryParse(primitiveText, true, out PrimitiveType primitiveType))
            {
                go = GameObject.CreatePrimitive(primitiveType);
            }
            else
            {
                go = new GameObject(name);
            }

            go.name = name;
            go.transform.position = position;

            var parent = SceneQuery.ResolveTarget(context.Args.Get("parent"));
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }

            var tag = context.Args.GetString("tag", null);
            if (!string.IsNullOrEmpty(tag))
            {
                go.tag = tag;
            }

#if UNITY_EDITOR
            UnityEditor.Undo.RegisterCreatedObjectUndo(go, "CommandSystem.CreateGameObject");
#endif

            return CommandResult.Ok($"已创建 {name}", go.name);
        }
    }

    /// <summary>
    /// 删除 GameObject
    /// <para>参数：target（必填，物体名称或层级路径，如 "A/B"；也支持 "type:XXX" 按类型找）</para>
    /// </summary>
    [Command("gameobject.destroy", Description = "删除一个 GameObject", Usage = "target: string")]
    public sealed class DestroyGameObjectCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var targetPath = context.Args.RequireString("target");
            if (!SceneQuery.TryResolveUnique(targetPath, out var transform, out var resolveError))
            {
                throw new CommandArgumentException(context.CommandId, "target", resolveError);
            }

            var go = transform.gameObject;

#if UNITY_EDITOR
            UnityEditor.Undo.DestroyObjectImmediate(go);
#else
            Object.Destroy(go);
#endif

            return CommandResult.Ok($"已删除 {targetPath}");
        }
    }

    /// <summary>
    /// 设置节点激活状态（"老模块留着不删只隐藏"的常态需求）
    /// <para>参数：target（必填）、active（bool，默认 true）、includeChildren（是否连同子树，默认 false）</para>
    /// </summary>
    [Command("gameobject.setActive", Description = "设置节点激活状态（含可选子树）",
        Usage = "target: string, active: bool, includeChildren: bool", EditorOnly = true)]
    public sealed class SetActiveGameObjectCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var targetSpec = context.Args.RequireString("target");
            if (!SceneQuery.TryResolveUnique(targetSpec, out var target, out var resolveError))
            {
                throw new CommandArgumentException(context.CommandId, "target", resolveError);
            }

            var active = context.Args.GetBool("active", true);
            var includeChildren = context.Args.GetBool("includeChildren", false);

            var go = target.gameObject;
#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(go, "CommandSystem.SetActive");
#endif
            go.SetActive(active);
            if (includeChildren)
            {
                for (var i = 0; i < target.childCount; i++)
                {
                    var child = target.GetChild(i);
#if UNITY_EDITOR
                    UnityEditor.Undo.RecordObject(child.gameObject, "CommandSystem.SetActive");
#endif
                    child.gameObject.SetActive(active);
                }
            }

            var data = JsonValue.NewObject();
            data.Set("path", JsonValue.Of(SceneQuery.BuildPath(target)));
            data.Set("active", JsonValue.Of(active));
            data.Set("includeChildren", JsonValue.Of(includeChildren));
            SceneQueryCommandsUtil.AttachScene(data);
            return CommandResult.Ok(
                $"已将 {SceneQuery.BuildPath(target)} 的 activeSelf 设为 {active}" + (includeChildren ? "（含子树）" : string.Empty),
                data);
        }
    }

    /// <summary>
    /// 重命名节点（新建时名字写死、事后想改名的常态需求）
    /// <para>参数：target（必填）、name（新名字，必填）</para>
    /// </summary>
    [Command("gameobject.rename", Description = "重命名节点",
        Usage = "target: string, name: string", EditorOnly = true)]
    public sealed class RenameGameObjectCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var targetSpec = context.Args.RequireString("target");
            if (!SceneQuery.TryResolveUnique(targetSpec, out var target, out var resolveError))
            {
                throw new CommandArgumentException(context.CommandId, "target", resolveError);
            }

            var name = context.Args.RequireString("name");
            var oldName = target.name;

#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(target.gameObject, "CommandSystem.Rename");
#endif
            target.name = name;

            var data = JsonValue.NewObject();
            data.Set("path", JsonValue.Of(SceneQuery.BuildPath(target)));
            data.Set("oldName", JsonValue.Of(oldName));
            data.Set("newName", JsonValue.Of(name));
            SceneQueryCommandsUtil.AttachScene(data);
            return CommandResult.Ok($"已将 {oldName} 重命名为 {name}", data);
        }
    }

    /// <summary>
    /// 设置 GameObject **自身**属性（layer / tag / isStatic）——IMPR-002。
    /// <para>
    /// <c>component.setField</c> 只作用于组件字段，而 layer / tag / isStatic 挂在 GameObject 上
    /// （GameObject 不是 Component，<c>GetComponent(typeof(GameObject))</c> 取不到），
    /// 所以此前没有任何指令能改节点层级（layer）。这边补齐写侧，读侧见 <c>scene.hierarchy</c>
    /// 每个节点上的 layer / layerName / tag / isStatic。
    /// </para>
    /// <para>
    /// 参数：target（必填）、layer（int 或层名，如 "UI"）、tag（string）、isStatic（bool）、
    /// includeChildren（默认 false，只对 layer 生效，连同整棵子树一起刷）
    /// </para>
    /// </summary>
    [Command("gameobject.setProperty",
        Description = "设置 GameObject 自身属性（layer / tag / isStatic），layer 可连同子树",
        Usage = "target: string, layer: int|string, tag: string, isStatic: bool, includeChildren: bool",
        EditorOnly = true)]
    public sealed class SetGameObjectPropertyCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var targetSpec = context.Args.RequireString("target");
            if (!SceneQuery.TryResolveUnique(targetSpec, out var target, out var resolveError))
            {
                throw new CommandArgumentException(context.CommandId, "target", resolveError);
            }

            var hasLayer = context.Args.Has("layer");
            var hasTag = context.Args.Has("tag");
            var hasStatic = context.Args.Has("isStatic");
            if (!hasLayer && !hasTag && !hasStatic)
            {
                throw new CommandArgumentException(context.CommandId, "layer",
                    "至少要给 layer / tag / isStatic 之一（激活态请用 gameobject.setActive，改名请用 gameobject.rename）");
            }

            var includeChildren = context.Args.GetBool("includeChildren", false);
            var layer = hasLayer ? ResolveLayer(context, context.Args.Get("layer")) : -1;
            var tag = hasTag ? context.Args.GetString("tag", string.Empty) : null;
            var isStatic = hasStatic && context.Args.GetBool("isStatic", false);

            var go = target.gameObject;
#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(go, "CommandSystem.SetGameObjectProperty");
            UnityEditor.Undo.RecordObject(target, "CommandSystem.SetGameObjectProperty");
#endif
            if (hasTag)
            {
                try
                {
                    go.tag = tag;
                }
                catch (UnityException)
                {
                    throw new CommandArgumentException(context.CommandId, "tag",
                        $"'{tag}' 不是工程里已定义的 Tag（未定义的 Tag 需先在 Project Settings → Tags and Layers 里加）");
                }
            }

            if (hasStatic)
            {
                go.isStatic = isStatic;
            }

            var affected = 0;
            if (hasLayer)
            {
                go.layer = layer;
                affected = 1;
                if (includeChildren)
                {
                    foreach (var child in target.GetComponentsInChildren<Transform>(true))
                    {
                        if (child == target)
                        {
                            continue;
                        }

#if UNITY_EDITOR
                        UnityEditor.Undo.RecordObject(child.gameObject, "CommandSystem.SetGameObjectProperty");
#endif
                        child.gameObject.layer = layer;
                        affected++;
                    }
                }
            }

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(go);
            UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(go);
#endif

            var data = JsonValue.NewObject();
            data.Set("path", JsonValue.Of(SceneQuery.BuildPath(target)));
            data.Set("layer", hasLayer ? JsonValue.Of(layer) : JsonValue.Null);
            data.Set("layerName", hasLayer ? JsonValue.Of(LayerMask.LayerToName(layer)) : JsonValue.Null);
            data.Set("tag", hasTag ? JsonValue.Of(go.tag) : JsonValue.Null);
            data.Set("isStatic", hasStatic ? JsonValue.Of(go.isStatic) : JsonValue.Null);
            data.Set("affectedCount", JsonValue.Of(affected));
            SceneQueryCommandsUtil.AttachScene(data);

            var parts = new System.Collections.Generic.List<string>();
            if (hasLayer)
            {
                parts.Add($"layer={layer}({LayerMask.LayerToName(layer)})" + (includeChildren ? $"（含子树 {affected} 个节点）" : string.Empty));
            }

            if (hasTag)
            {
                parts.Add($"tag={go.tag}");
            }

            if (hasStatic)
            {
                parts.Add($"isStatic={go.isStatic}");
            }

            return CommandResult.Ok(
                $"已设置 {SceneQuery.BuildPath(target)} 的 {string.Join("、", parts.ToArray())}", data);
        }

        /// <summary>layer 参数：数字（0-31）或层名（"UI"/"Default"/自定义层），失败时列出可选层名。</summary>
        private static int ResolveLayer(CommandContext context, JsonValue spec)
        {
            if (spec == null || spec.IsNull)
            {
                throw new CommandArgumentException(context.CommandId, "layer", "缺少 layer 的值（数字 0-31 或层名）");
            }

            if (spec.IsNumber)
            {
                return CheckLayerRange(context, spec.AsInt(-1));
            }

            var text = spec.AsString(string.Empty).Trim();
            if (int.TryParse(text, out var parsed))
            {
                return CheckLayerRange(context, parsed);
            }

            var named = LayerMask.NameToLayer(text);
            if (named >= 0)
            {
                return named;
            }

            var known = new System.Collections.Generic.List<string>();
            for (var i = 0; i < 32; i++)
            {
                var name = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(name))
                {
                    known.Add($"{i}:{name}");
                }
            }

            throw new CommandArgumentException(context.CommandId, "layer",
                $"工程里没有名为 '{text}' 的层。已有层：{string.Join("、", known.ToArray())}"
                + "（自定义层先在 Project Settings → Tags and Layers 里加）");
        }

        private static int CheckLayerRange(CommandContext context, int layer)
        {
            if (layer < 0 || layer > 31)
            {
                throw new CommandArgumentException(context.CommandId, "layer",
                    $"layer 必须在 0-31 之间，收到 {layer}");
            }

            return layer;
        }
    }

    /// <summary>
    /// 改变节点父级（RectTransform 用 worldPositionStays 语义）
    /// <para>参数：target（必填）、parent（必填）、keepWorldPosition（默认 true）</para>
    /// </summary>
    [Command("gameobject.setParent", Description = "改变节点父级",
        Usage = "target: string, parent: string, keepWorldPosition: bool", EditorOnly = true)]
    public sealed class SetParentGameObjectCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var targetSpec = context.Args.RequireString("target");
            if (!SceneQuery.TryResolveUnique(targetSpec, out var target, out var resolveError))
            {
                throw new CommandArgumentException(context.CommandId, "target", resolveError);
            }

            var parentSpec = context.Args.Get("parent");
            if (parentSpec == null || parentSpec.IsNull)
            {
                throw new CommandArgumentException(context.CommandId, "parent", "缺少必填参数 parent");
            }

            var parent = SceneQuery.ResolveTarget(parentSpec);
            if (parent == null)
            {
                throw new CommandArgumentException(context.CommandId, "parent",
                    $"找不到父节点 '{context.Args.GetString("parent", string.Empty)}'");
            }

            var keepWorldPosition = context.Args.GetBool("keepWorldPosition", true);

#if UNITY_EDITOR
            UnityEditor.Undo.SetTransformParent(target, parent, "CommandSystem.SetParent");
#else
            target.SetParent(parent, keepWorldPosition);
#endif

            var data = JsonValue.NewObject();
            data.Set("path", JsonValue.Of(SceneQuery.BuildPath(target)));
            data.Set("parentPath", JsonValue.Of(SceneQuery.BuildPath(parent)));
            data.Set("keepWorldPosition", JsonValue.Of(keepWorldPosition));
            SceneQueryCommandsUtil.AttachScene(data);
            return CommandResult.Ok(
                $"已将 {SceneQuery.BuildPath(target)} 的父级改为 {SceneQuery.BuildPath(parent)}", data);
        }
    }

    /// <summary>
    /// 在场景内复制节点（与 <c>prefab.duplicateNode</c> 口径一致：引用默认指向原节点，不做重映射）。
    /// <para>
    /// 参数：target（必填）、name（默认 <c>原名_Copy</c>）、parent（缺省复制到同级）、
    /// includeChildren（默认 true，false 时只复制自身不要子树）、keepWorldPosition（默认 true）
    /// </para>
    /// </summary>
    [Command("gameobject.duplicate", Description = "在场景内复制节点（含子树，引用默认指向原节点）",
        Usage = "target: string, name: string, parent: string, includeChildren: bool, keepWorldPosition: bool",
        EditorOnly = true)]
    public sealed class DuplicateGameObjectCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var targetSpec = context.Args.RequireString("target");
            if (!SceneQuery.TryResolveUnique(targetSpec, out var target, out var resolveError))
            {
                throw new CommandArgumentException(context.CommandId, "target", resolveError);
            }

            var name = context.Args.GetString("name", target.name + "_Copy");
            var parentSpec = context.Args.Get("parent");
            var parent = (parentSpec != null && !parentSpec.IsNull)
                ? SceneQuery.ResolveTarget(parentSpec)
                : target.parent;
            var includeChildren = context.Args.GetBool("includeChildren", true);
            var keepWorldPosition = context.Args.GetBool("keepWorldPosition", true);

            var copy = Object.Instantiate(target.gameObject);
            copy.name = name;
            copy.transform.SetParent(parent, keepWorldPosition);

            // includeChildren=false：只复制自身，丢掉子树
            if (!includeChildren)
            {
                for (var i = copy.transform.childCount - 1; i >= 0; i--)
                {
                    Object.DestroyImmediate(copy.transform.GetChild(i).gameObject);
                }
            }

#if UNITY_EDITOR
            UnityEditor.Undo.RegisterCreatedObjectUndo(copy, "CommandSystem.Duplicate");
#endif

            var data = JsonValue.NewObject();
            data.Set("path", JsonValue.Of(SceneQuery.BuildPath(copy.transform)));
            data.Set("selectPath", JsonValue.Of(SceneQuery.SelectPath(copy.transform)));
            data.Set("instanceId", JsonValue.Of(copy.GetInstanceID()));
            data.Set("parentPath", parent != null ? JsonValue.Of(SceneQuery.BuildPath(parent)) : JsonValue.Null);
            SceneQueryCommandsUtil.AttachScene(data);
            return CommandResult.Ok(
                $"已复制 {SceneQuery.BuildPath(target)} → {SceneQuery.BuildPath(copy.transform)}", data);
        }
    }

    /// <summary>
    /// 实例化一个 Prefab 资源到场景（保持 Prefab 连接，不是复制展开）
    /// <para>
    /// 参数：asset（必填，Assets 下的 Prefab 路径）、parent（父物体，可选）、name（改名，可选）、
    /// UI 布局（size / anchoredPosition / anchorMin / anchorMax / scale / pivot，仅对 RectTransform 生效）、select
    /// </para>
    /// <para>
    /// 与 <c>gameobject.create</c> 的区别：这里是"摆一个已有积木"，组件与引用关系全部沿用 Prefab，
    /// 后续改 Prefab 能同步到所有实例。UI 节点不给 size 时不会动锚点/尺寸，只应用显式传入的项。
    /// </para>
    /// </summary>
    [Command("gameobject.instantiate", Description = "实例化 Prefab 到场景（保留 Prefab 连接）",
        Usage = "asset: string, parent: string, name: string, size:[w,h], anchoredPosition:[x,y], scale: number|array, select: bool",
        EditorOnly = true)]
    public sealed class InstantiatePrefabCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var assetPath = context.Args.RequireString("asset");

#if UNITY_EDITOR
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                throw new CommandArgumentException(context.CommandId, "asset",
                    $"找不到 Prefab '{assetPath}'（路径要基于 Assets/，含 .prefab 后缀）");
            }

            var parent = SceneQuery.ResolveTarget(context.Args.Get("parent"));
            var instance = (GameObject)(parent != null
                ? PrefabUtility.InstantiatePrefab(prefab, parent)
                : PrefabUtility.InstantiatePrefab(prefab));

            if (instance == null)
            {
                throw new CommandException($"指令 '{context.CommandId}' 实例化 '{assetPath}' 失败");
            }

            var name = context.Args.GetString("name", null);
            if (!string.IsNullOrEmpty(name))
            {
                instance.name = name;
            }

            var rect = instance.transform as RectTransform;
            if (rect != null)
            {
                if (context.Args.Has("size") || context.Args.Has("anchorMin") || context.Args.Has("anchorMax")
                    || context.Args.Has("offsetMin") || context.Args.Has("offsetMax")
                    || context.Args.Has("polar") || context.Args.Has("pivot"))
                {
                    // 完整布局：size 存在时锚点居中，按 size + anchoredPosition 摆放
                    UiNodeFactory.ApplyLayout(rect, context.Args);
                }
                else
                {
                    // 碎片式微调：只动显式传入的项，不碰 Prefab 原有的锚点与尺寸
                    if (context.Args.Has("anchoredPosition") || context.Args.Has("position"))
                    {
                        rect.anchoredPosition = context.Args.GetVector2("anchoredPosition",
                            context.Args.GetVector2("position", rect.anchoredPosition));
                    }

                    if (context.Args.Has("scale"))
                    {
                        var node = context.Args.Get("scale");
                        var scale = node.IsArray
                            ? context.Args.GetVector2("scale", Vector2.one)
                            : new Vector2(context.Args.GetFloat("scale", 1f), context.Args.GetFloat("scale", 1f));
                        rect.localScale = new Vector3(scale.x, scale.y, 1f);
                    }
                }
            }

            Undo.RegisterCreatedObjectUndo(instance, "CommandSystem.InstantiatePrefab");

            if (context.Args.GetBool("select", false))
            {
                Selection.activeGameObject = instance;
            }

            return CommandResult.Ok($"已实例化 {prefab.name} → {instance.name}", instance.name);
#else
            throw new CommandException($"指令 '{context.CommandId}' 只能在编辑器内执行");
#endif
        }
    }
}
