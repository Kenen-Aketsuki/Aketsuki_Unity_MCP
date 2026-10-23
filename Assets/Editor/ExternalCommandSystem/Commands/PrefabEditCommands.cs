using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace CivilRev.CommandSystem.Editor
{
    /// <summary>
    /// Prefab 资产编辑类指令（prefab.addNode / prefab.duplicateNode / prefab.tree / component.add）
    /// 共用的路径归一化、生命周期与节点操作工具。
    /// <para>
    /// 铁律：一切 prefab 修改走 <c>PrefabUtility</c> API（LoadPrefabContents → 改 →
    /// SaveAsPrefabAsset → UnloadPrefabContents），不手写 YAML。
    /// </para>
    /// </summary>
#if UNITY_EDITOR
    internal static class PrefabEditUtil
    {
        /// <summary>归一化 prefab 参数：必须基于 Assets/、补 .prefab 后缀、过 CommandPathUtil 白名单</summary>
        public static string NormalizePrefabPath(CommandContext context, string raw, string paramName = "prefab")
        {
            var text = (raw ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
            if (text.Length == 0)
            {
                throw new CommandArgumentException(context.CommandId, paramName,
                    "prefab 路径不能为空（基于 Assets/ 的 .prefab 路径）");
            }

            if (!text.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandArgumentException(context.CommandId, paramName,
                    $"prefab 路径必须基于 Assets/：'{text}'");
            }

            if (!text.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                text += ".prefab";
            }

            try
            {
                CommandPathUtil.EnsureAccessible(System.IO.Path.GetFullPath(text));
            }
            catch (Exception ex)
            {
                throw new CommandArgumentException(context.CommandId, paramName, ex.Message);
            }

            return text;
        }

        /// <summary>
        /// 统一生命周期：LoadPrefabContents → edit 回调 → SaveAsPrefabAsset → UnloadPrefabContents。
        /// 回调抛异常时同样 Unload，不落盘。
        /// </summary>
        public static CommandResult EditPrefab(CommandContext context, string prefabPath,
            Func<GameObject, CommandResult> edit)
        {
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null)
            {
                throw new CommandArgumentException(context.CommandId, "prefab",
                    $"打不开 Prefab '{prefabPath}'（确认路径存在且有 .prefab 后缀）");
            }

            try
            {
                var result = edit(root);
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                return result;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// 新建节点：父节点带 RectTransform（UI 树）时自动用 RectTransform，否则用普通 Transform。
        /// </summary>
        public static GameObject CreateNode(string name, Transform parent)
        {
            var go = parent != null && parent.GetComponent<RectTransform>() != null
                ? new GameObject(name, typeof(RectTransform))
                : new GameObject(name);
            go.transform.SetParent(parent, false);
            return go;
        }

        /// <summary>
        /// 把 source 节点上的所有组件复制到 dest（Transform 除外，Hierarchy 已自带）。
        /// 用 <c>EditorUtility.CopySerialized</c> 保真复制；对象引用不重映射（仍指向源节点）。
        /// </summary>
        public static void CopyComponents(Transform source, Transform dest)
        {
            foreach (var component in source.GetComponents<Component>())
            {
                if (component == null)
                {
                    continue;
                }

                var type = component.GetType();
                if (typeof(Transform).IsAssignableFrom(type))
                {
                    continue;
                }

                var added = dest.gameObject.AddComponent(type);
                if (added == null)
                {
                    throw new CommandException($"给 '{dest.name}' 挂 {type.Name} 失败（该类型可能禁止 AddComponent）");
                }

                EditorUtility.CopySerialized(component, added);
            }
        }

        /// <summary>
        /// 解析并挂组件。已存在同类型时：require=true 报错，否则跳过（幂等）。
        /// 返回实际新挂上的类型名清单。
        /// </summary>
        /// <param name="useUndo">场景模式传 true（注册 Undo）；Prefab 内容里不需要</param>
        public static List<string> AddComponents(GameObject go, IEnumerable<string> typeNames,
            CommandContext context, bool require, bool useUndo, string paramName = "component")
        {
            var added = new List<string>();
            foreach (var name in typeNames)
            {
                var type = SceneQuery.ResolveComponentType(name);
                if (type == null)
                {
                    throw new CommandArgumentException(context.CommandId, paramName,
                        $"'{name}' 不是可识别的组件类型（写短名如 \"Image\"，或全名如 \"Spine.Unity.SkeletonAnimation\"）");
                }

                if (go.GetComponent(type) != null)
                {
                    if (require)
                    {
                        throw new CommandArgumentException(context.CommandId, paramName,
                            $"'{go.name}' 上已有 {type.Name} 组件（require=true，报错而不是跳过）");
                    }

                    continue;
                }

                Component component = useUndo
                    ? Undo.AddComponent(go, type)
                    : (Component)go.AddComponent(type);
                if (component == null)
                {
                    throw new CommandException($"给 '{go.name}' 挂 {type.Name} 失败");
                }

                added.Add(type.Name);
            }

            return added;
        }

        /// <summary>
        /// copyFrom 的源节点。跨 Prefab 时持有临时加载的 Prefab 内容，Dispose 时卸载（IMPR-003）。
        /// </summary>
        public sealed class CopySource : IDisposable
        {
            private GameObject _loadedContents;

            /// <summary>源节点（当前 Prefab 内，或临时加载的另一个 Prefab 内）</summary>
            public Transform Node { get; private set; }

            /// <summary>源所在 Prefab 的资产路径；null 表示源就在当前 Prefab 内</summary>
            public string PrefabPath { get; private set; }

            /// <summary>源节点的书写形式（原样回显，用于结果文案）</summary>
            public string Spec { get; private set; }

            public bool IsCrossPrefab
            {
                get { return _loadedContents != null; }
            }

            internal CopySource(Transform node, string prefabPath, string spec, GameObject loadedContents)
            {
                Node = node;
                PrefabPath = prefabPath;
                Spec = spec;
                _loadedContents = loadedContents;
            }

            public void Dispose()
            {
                if (_loadedContents != null)
                {
                    PrefabUtility.UnloadPrefabContents(_loadedContents);
                    _loadedContents = null;
                }
            }
        }

        /// <summary>
        /// 解析 copyFrom / 源节点：支持两种写法。
        /// <list type="bullet">
        /// <item><c>Parent/ChildA</c> —— 当前 Prefab 内的节点路径（旧口径）；</item>
        /// <item><c>Assets/GameRes/Other.prefab/SomeChild</c> —— **跨 Prefab 引用**（IMPR-003），
        /// 省略节点路径则取该 Prefab 的根。</item>
        /// </list>
        /// 跨 Prefab 时会临时 <c>LoadPrefabContents</c>，调用方**必须** <c>using</c> 包住，Dispose 时卸载。
        /// </summary>
        public static CopySource LoadCopySource(CommandContext context, GameObject currentRoot,
            string currentPrefabPath, string spec)
        {
            var text = (spec ?? string.Empty).Trim().Replace('\\', '/');
            if (text.Length == 0)
            {
                throw new CommandArgumentException(context.CommandId, "copyFrom", "copyFrom 不能为空串");
            }

            string assetPath;
            string nodeSpec;
            if (TrySplitPrefabSpec(text, out assetPath, out nodeSpec))
            {
                assetPath = NormalizeAssetPath(context, assetPath);
                if (!string.Equals(assetPath, currentPrefabPath, StringComparison.OrdinalIgnoreCase))
                {
                    var contents = PrefabUtility.LoadPrefabContents(assetPath);
                    if (contents == null)
                    {
                        throw new CommandArgumentException(context.CommandId, "copyFrom",
                            $"打不开源 Prefab '{assetPath}'（路径要基于 Assets/、含 .prefab 后缀）");
                    }

                    try
                    {
                        var node = SetComponentFieldCommand.FindInPrefab(contents, nodeSpec);
                        if (node == null)
                        {
                            throw new CommandArgumentException(context.CommandId, "copyFrom",
                                $"源 Prefab '{assetPath}' 里找不到节点 '{nodeSpec}'（空串表示该 Prefab 根节点）");
                        }

                        return new CopySource(node, assetPath, text, contents);
                    }
                    catch
                    {
                        PrefabUtility.UnloadPrefabContents(contents);
                        throw;
                    }
                }

                // 写的就是当前 Prefab 自己的完整路径：退回本地路径解析
                text = nodeSpec;
            }

            var local = SetComponentFieldCommand.FindInPrefab(currentRoot, text);
            if (local == null)
            {
                throw new CommandArgumentException(context.CommandId, "copyFrom",
                    $"Prefab '{currentPrefabPath}' 里找不到源节点 '{text}'"
                    + "（跨 Prefab 请写全路径：Assets/.../Other.prefab/节点/路径）");
            }

            return new CopySource(local, null, text, null);
        }

        /// <summary>把 "A/B/C.prefab/Node/Path" 拆成 ("A/B/C.prefab", "Node/Path")；不带 .prefab 返回 false。</summary>
        private static bool TrySplitPrefabSpec(string text, out string assetPath, out string nodeSpec)
        {
            assetPath = null;
            nodeSpec = null;

            var marker = text.IndexOf(".prefab", StringComparison.OrdinalIgnoreCase);
            while (marker >= 0)
            {
                var end = marker + ".prefab".Length;
                var boundary = end == text.Length || text[end] == '/';
                if (boundary)
                {
                    assetPath = text.Substring(0, end);
                    nodeSpec = end == text.Length ? string.Empty : text.Substring(end + 1);
                    return true;
                }

                marker = text.IndexOf(".prefab", end, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private static string NormalizeAssetPath(CommandContext context, string assetPath)
        {
            var text = assetPath.Trim().Trim('/');
            if (!text.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandArgumentException(context.CommandId, "copyFrom",
                    $"跨 Prefab 引用必须写 Assets/ 下的完整路径：'{text}'");
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(text) == null)
            {
                throw new CommandArgumentException(context.CommandId, "copyFrom",
                    $"找不到源 Prefab '{text}'（路径要基于 Assets/、含 .prefab 后缀）");
            }

            try
            {
                CommandPathUtil.EnsureAccessible(System.IO.Path.GetFullPath(text));
            }
            catch (Exception ex)
            {
                throw new CommandArgumentException(context.CommandId, "copyFrom", ex.Message);
            }

            return text;
        }

        /// <summary>
        /// 解析 components 类参数：接受单字符串或字符串数组（其余类型报错）。
        /// </summary>
        public static List<string> ParseTypeNames(JsonValue value, CommandContext context, string paramName)
        {
            var names = new List<string>();
            if (value == null || value.IsNull)
            {
                return names;
            }

            if (value.IsString)
            {
                names.Add(value.AsString());
                return names;
            }

            if (value.IsArray)
            {
                foreach (var item in value.Items)
                {
                    if (item.IsString && !string.IsNullOrWhiteSpace(item.AsString()))
                    {
                        names.Add(item.AsString().Trim());
                    }
                }

                return names;
            }

            throw new CommandArgumentException(context.CommandId, paramName,
                $"{paramName} 要写成字符串或字符串数组");
        }
    }
#endif

    /// <summary>
    /// 往 Prefab 资产里添加节点（可选复制源节点组件、追加组件）——B1。
    /// <para>
    /// 通用积木：不绑定任何业务。父节点带 RectTransform 时自动用 RectTransform；
    /// copyFrom 用 <c>EditorUtility.CopySerialized</c> 复制组件（对象引用不重映射，仍指向源节点）。
    /// </para>
    /// <para>
    /// 参数：prefab（必填，Assets/ 下的 .prefab 路径）、name（必填）、
    /// parent（默认空串 = Prefab 根）、copyFrom（可选，同 Prefab 内源节点路径）、
    /// components（可选，要追加的组件类型名，字符串或数组）、active（默认 true）
    /// </para>
    /// </summary>
    [Command("prefab.addNode", Description = "往 Prefab 资产里添加节点（可选复制源节点组件、追加组件）",
        Usage = "prefab: string, name: string, parent: string, copyFrom: string, components: string[], active: bool",
        EditorOnly = true)]    public sealed class PrefabAddNodeCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
#if UNITY_EDITOR
            var prefabPath = PrefabEditUtil.NormalizePrefabPath(context, context.Args.RequireString("prefab"));
            var name = context.Args.RequireString("name");
            var parentSpec = context.Args.GetString("parent", string.Empty);
            var copyFromSpec = context.Args.GetString("copyFrom", null);
            var active = context.Args.GetBool("active", true);
            var typeNames = PrefabEditUtil.ParseTypeNames(context.Args.Get("components"), context, "components");

            return PrefabEditUtil.EditPrefab(context, prefabPath, root =>
            {
                var parent = SetComponentFieldCommand.FindInPrefab(root, parentSpec);
                if (parent == null)
                {
                    throw new CommandArgumentException(context.CommandId, "parent",
                        $"Prefab '{prefabPath}' 里找不到父节点 '{parentSpec}'（空串表示根节点）");
                }

                var node = PrefabEditUtil.CreateNode(name, parent);
                if (!active)
                {
                    node.SetActive(false);
                }

                var copied = new List<string>();
                var copySourcePrefab = string.Empty;
                if (!string.IsNullOrEmpty(copyFromSpec))
                {
                    // 支持跨 Prefab：Assets/.../Other.prefab/Node/Path（IMPR-003）
                    using (var source = PrefabEditUtil.LoadCopySource(context, root, prefabPath, copyFromSpec))
                    {
                        PrefabEditUtil.CopyComponents(source.Node, node.transform);
                        foreach (var component in source.Node.GetComponents<Component>())
                        {
                            if (component != null && !typeof(Transform).IsAssignableFrom(component.GetType()))
                            {
                                copied.Add(component.GetType().Name);
                            }
                        }

                        if (source.IsCrossPrefab)
                        {
                            copySourcePrefab = source.PrefabPath;
                        }
                    }
                }

                var appended = PrefabEditUtil.AddComponents(node, typeNames, context, false, false, "components");

                var data = JsonValue.NewObject();
                data.Set("prefab", JsonValue.Of(prefabPath));
                data.Set("path", JsonValue.Of(PrefabEditCommandsUtil.BuildNodePath(node.transform)));
                data.Set("copiedComponents", PrefabEditCommandsUtil.ToJsonArray(copied));
                data.Set("addedComponents", PrefabEditCommandsUtil.ToJsonArray(appended));
                if (copySourcePrefab.Length > 0)
                {
                    data.Set("copySourcePrefab", JsonValue.Of(copySourcePrefab));
                    data.Set("warning", JsonValue.Of(
                        "跨 Prefab 复制只搬组件数据；其中的对象引用仍指向临时加载的源 Prefab，保存后会变成 null，需另行重指"));
                }

                return CommandResult.Ok(
                    $"已在 {prefabPath} 的 {parent.name} 下创建 {name}"
                    + PrefabEditCommandsUtil.DescribeComponents(copied, appended)
                    + (copySourcePrefab.Length > 0
                        ? $"（跨 Prefab 复制自 {copySourcePrefab}：对象引用需另行重指）"
                        : string.Empty),
                    data);
            });
#else
            throw new CommandException($"指令 '{context.CommandId}' 只能在编辑器内执行");
#endif
        }
    }

    /// <summary>
    /// 在 Prefab 资产内复制一个节点（含子树与组件）——B1。
    /// <para>克隆体里的对象引用仍指向原节点（引用重映射留二期）；克隆后按源节点的
    /// localPosition / localRotation / localScale 复位，保证摆位一致。</para>
    /// <para>
    /// 参数：prefab（必填）、target（必填，要复制的节点路径）、name（默认 target + "_Copy"）、
    /// parent（默认与源节点同父）、active（缺省沿用源节点）
    /// </para>
    /// </summary>
    [Command("prefab.duplicateNode", Description = "在 Prefab 资产内复制节点（默认含子树与组件）",
        Usage = "prefab: string, target: string, name: string, parent: string, active: bool, includeChildren: bool",
        EditorOnly = true)]
    public sealed class PrefabDuplicateNodeCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
#if UNITY_EDITOR
            var prefabPath = PrefabEditUtil.NormalizePrefabPath(context, context.Args.RequireString("prefab"));
            var targetSpec = context.Args.RequireString("target");
            var hasActive = context.Args.Has("active");

            return PrefabEditUtil.EditPrefab(context, prefabPath, root =>
            {
                var source = SetComponentFieldCommand.FindInPrefab(root, targetSpec);
                if (source == null)
                {
                    throw new CommandArgumentException(context.CommandId, "target",
                        $"Prefab '{prefabPath}' 里找不到节点 '{targetSpec}'");
                }

                if (source.parent == null)
                {
                    throw new CommandArgumentException(context.CommandId, "target",
                        "不能复制 Prefab 根节点（根节点必须唯一）");
                }

                var name = context.Args.GetString("name", source.name + "_Copy");
                var parentSpec = context.Args.GetString("parent", null);
                var parent = string.IsNullOrEmpty(parentSpec)
                    ? source.parent
                    : SetComponentFieldCommand.FindInPrefab(root, parentSpec);
                if (parent == null)
                {
                    throw new CommandArgumentException(context.CommandId, "parent",
                        $"Prefab '{prefabPath}' 里找不到目标父节点 '{parentSpec}'");
                }

                var clone = UnityEngine.Object.Instantiate(source.gameObject, parent);
                clone.name = name;
                clone.transform.localPosition = source.localPosition;
                clone.transform.localRotation = source.localRotation;
                clone.transform.localScale = source.localScale;

                // includeChildren=false：只要节点自身，丢掉子树（与 gameobject.duplicate 对齐）
                var includeChildren = context.Args.GetBool("includeChildren", true);
                if (!includeChildren)
                {
                    for (var i = clone.transform.childCount - 1; i >= 0; i--)
                    {
                        UnityEngine.Object.DestroyImmediate(clone.transform.GetChild(i).gameObject);
                    }
                }

                if (hasActive)
                {
                    clone.SetActive(context.Args.GetBool("active", clone.activeSelf));
                }

                var clonePath = PrefabEditCommandsUtil.BuildNodePath(clone.transform);
                var data = JsonValue.NewObject();
                data.Set("prefab", JsonValue.Of(prefabPath));
                data.Set("path", JsonValue.Of(clonePath));
                // 复核用：复制出来的子树规模（Instantiate 会连子树一起克隆，这里给出可核对的数字）
                data.Set("childCount", JsonValue.Of(clone.transform.childCount));
                data.Set("subtreeCount", JsonValue.Of(clone.GetComponentsInChildren<Transform>(true).Length - 1));
                var children = JsonValue.NewArray();
                foreach (var child in clone.GetComponentsInChildren<Transform>(true))
                {
                    if (child != clone.transform)
                    {
                        children.Add(JsonValue.Of(PrefabEditCommandsUtil.BuildNodePath(child)));
                    }
                }

                data.Set("subtreePaths", children);
                return CommandResult.Ok(
                    $"已复制 {source.name} → {clonePath}（{prefabPath}，含子树 {clone.transform.childCount} 个直接子节点，"
                    + $"共 {clone.GetComponentsInChildren<Transform>(true).Length - 1} 个后代；对象引用未重映射）",
                    data);
            });
#else
            throw new CommandException($"指令 '{context.CommandId}' 只能在编辑器内执行");
#endif
        }
    }

    /// <summary>
    /// 列出 Prefab 资产的节点树（路径 / 组件类型 / 激活状态）——B4。
    /// <para>
    /// 只读：LoadPrefabContents 后走与 <c>scene.hierarchy</c> 同一套节点序列化，不做保存。
    /// B1/B3 在 Prefab 上操作前，先用它确认父节点路径与目标组件。
    /// </para>
    /// <para>参数：prefab（必填）、maxDepth（默认 -1 不限深，0 = 只列根）</para>
    /// </summary>
    [Command("prefab.tree", Description = "列出 Prefab 资产的节点树（路径/组件/激活态，只读）",
        Usage = "prefab: string, maxDepth: int",
        EditorOnly = true)]
    public sealed class PrefabTreeCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
#if UNITY_EDITOR
            var prefabPath = PrefabEditUtil.NormalizePrefabPath(context, context.Args.RequireString("prefab"));
            var maxDepth = context.Args.GetInt("maxDepth", -1);

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null)
            {
                throw new CommandArgumentException(context.CommandId, "prefab",
                    $"打不开 Prefab '{prefabPath}'");
            }

            try
            {
                var total = 0;
                var nodes = Visit(root.transform, 0, maxDepth, ref total);

                var result = JsonValue.NewObject();
                result.Set("prefab", JsonValue.Of(prefabPath));
                result.Set("count", JsonValue.Of(total));
                result.Set("nodes", nodes);
                return CommandResult.Ok($"Prefab 共 {total} 个节点（maxDepth={maxDepth}）", result);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
#else
            throw new CommandException($"指令 '{context.CommandId}' 只能在编辑器内执行");
#endif
        }

        private static JsonValue Visit(Transform node, int depth, int maxDepth, ref int total)
        {
            total++;
            var item = SceneQueryCommandsUtil.NodeToJson(node);
            item.Set("childCount", JsonValue.Of(node.childCount));

            var children = JsonValue.NewArray();
            if (maxDepth < 0 || depth < maxDepth)
            {
                for (var i = 0; i < node.childCount; i++)
                {
                    children.Add(Visit(node.GetChild(i), depth + 1, maxDepth, ref total));
                }
            }

            item.Set("children", children);
            return item;
        }
    }

    /// <summary>
    /// 给节点挂组件（场景或 Prefab 资产）——B2。
    /// <para>
    /// 组件按**类型名字符串**反射解析（短名或全名，如 <c>ContentSizeFitter</c>、
    /// <c>Spine.Unity.SkeletonAnimation</c>），不引用任何工程类型。
    /// 已存在同类型组件时：require=true 报错，默认跳过并如实报告（幂等，重复跑不报错）。
    /// </para>
    /// <para>参数：target（必填）、component（必填）、prefab（可选，给了就操作 Prefab 资源）、
    /// require（默认 false）</para>
    /// </summary>
    [Command("component.add", Description = "给节点挂组件（场景或 Prefab 资产，类型名反射，幂等）",
        Usage = "target: string, component: string, prefab: string, require: bool",
        EditorOnly = true)]
    public sealed class AddComponentCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
#if UNITY_EDITOR
            var targetSpec = context.Args.RequireString("target");
            var typeName = context.Args.RequireString("component");
            var require = context.Args.GetBool("require", false);

            var prefabPath = context.Args.GetString("prefab", null);
            if (!string.IsNullOrEmpty(prefabPath))
            {
                var normalized = PrefabEditUtil.NormalizePrefabPath(context, prefabPath);
                return PrefabEditUtil.EditPrefab(context, normalized, root =>
                {
                    var node = SetComponentFieldCommand.FindInPrefab(root, targetSpec);
                    if (node == null)
                    {
                        throw new CommandArgumentException(context.CommandId, "target",
                            $"Prefab '{normalized}' 里找不到节点 '{targetSpec}'（空串表示根节点）");
                    }

                    var added = PrefabEditUtil.AddComponents(
                        node.gameObject, new[] { typeName }, context, require, false);
                    return BuildResult(node, typeName, added, normalized);
                });
            }

            var target = SceneQuery.ResolveTarget(targetSpec);
            if (target == null)
            {
                throw new CommandArgumentException(context.CommandId, "target",
                    $"找不到物体 '{targetSpec}'");
            }

            var sceneAdded = PrefabEditUtil.AddComponents(
                target.gameObject, new[] { typeName }, context, require, true);
            return BuildResult(target, typeName, sceneAdded, null);
#else
            throw new CommandException($"指令 '{context.CommandId}' 只能在编辑器内执行");
#endif
        }

        private static CommandResult BuildResult(Transform node, string typeName, List<string> added,
            string prefabPath)
        {
            var data = JsonValue.NewObject();
            data.Set("added", JsonValue.Of(added.Count > 0));
            data.Set("path", JsonValue.Of(SceneQuery.BuildPath(node)));
            if (prefabPath != null)
            {
                data.Set("prefab", JsonValue.Of(prefabPath));
            }

            var message = added.Count > 0
                ? $"已给 {node.name} 挂上 {string.Join("、", added.ToArray())}"
                      + (prefabPath != null ? $"（Prefab {prefabPath}）" : string.Empty)
                : $"{node.name} 上已有 {typeName}，跳过（幂等）";
            return CommandResult.Ok(message, data);
        }
    }

    /// <summary>Prefab 编辑类指令共用的结果小工具</summary>
    internal static class PrefabEditCommandsUtil
    {
        public static JsonValue ToJsonArray(List<string> items)
        {
            var array = JsonValue.NewArray();
            foreach (var item in items)
            {
                array.Add(JsonValue.Of(item));
            }

            return array;
        }

        public static string BuildNodePath(Transform node)
        {
            var segments = new List<string>();
            for (var current = node; current != null; current = current.parent)
            {
                segments.Insert(0, current.name);
            }

            return string.Join("/", segments.ToArray());
        }

        public static string DescribeComponents(List<string> copied, List<string> appended)
        {
            var parts = new List<string>();
            if (copied.Count > 0)
            {
                parts.Add("复制组件：" + string.Join("、", copied.ToArray()));
            }

            if (appended.Count > 0)
            {
                parts.Add("追加组件：" + string.Join("、", appended.ToArray()));
            }

            return parts.Count > 0 ? "（" + string.Join("；", parts.ToArray()) + "）" : string.Empty;
        }
    }

    /// <summary>
    /// 删除 Prefab 资产内的节点（含子树）——补全 CRUD 的"删预制体节点"。
    /// <para>
    /// 与 <c>prefab.addNode</c> / <c>prefab.duplicateNode</c> 对称，走 PrefabUtility 生命周期。
    /// <c>keepChildren=true</c> 时先把子节点上移到父节点下再删本节点（保留子树），否则连子树一起删。
    /// </para>
    /// <para>参数：prefab（必填）、target（必填，节点路径）、keepChildren（默认 false）</para>
    /// </summary>
    [Command("prefab.removeNode", Description = "删除 Prefab 资产内的节点（含子树，可选保留子节点）",
        Usage = "prefab: string, target: string, keepChildren: bool",
        EditorOnly = true)]
    public sealed class PrefabRemoveNodeCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
#if UNITY_EDITOR
            var prefabPath = PrefabEditUtil.NormalizePrefabPath(context, context.Args.RequireString("prefab"));
            var targetSpec = context.Args.RequireString("target");
            var keepChildren = context.Args.GetBool("keepChildren", false);

            return PrefabEditUtil.EditPrefab(context, prefabPath, root =>
            {
                var node = SetComponentFieldCommand.FindInPrefab(root, targetSpec);
                if (node == null)
                {
                    throw new CommandArgumentException(context.CommandId, "target",
                        $"Prefab '{prefabPath}' 里找不到节点 '{targetSpec}'");
                }

                if (node.parent == null)
                {
                    throw new CommandArgumentException(context.CommandId, "target",
                        "不能删除 Prefab 根节点（根节点必须唯一）");
                }

                var parent = node.parent;
                var childCount = node.childCount;
                if (keepChildren && childCount > 0)
                {
                    // 逆序遍历：SetParent 会把节点从 node 下移走，正序会漏掉后续子节点
                    for (var i = node.childCount - 1; i >= 0; i--)
                    {
                        node.GetChild(i).SetParent(parent, true);
                    }
                }

                var path = PrefabEditCommandsUtil.BuildNodePath(node);
                var name = node.name;
                UnityEngine.Object.DestroyImmediate(node.gameObject);

                var data = JsonValue.NewObject();
                data.Set("prefab", JsonValue.Of(prefabPath));
                data.Set("removed", JsonValue.Of(path));
                data.Set("keptChildren", JsonValue.Of(keepChildren && childCount > 0));
                return CommandResult.Ok(
                    $"已从 {prefabPath} 删除节点 {path}"
                    + (keepChildren && childCount > 0 ? "（子节点已上移到父节点）" : "（含子树）"),
                    data);
            });
#else
            throw new CommandException($"指令 '{context.CommandId}' 只能在编辑器内执行");
#endif
        }
    }
}
