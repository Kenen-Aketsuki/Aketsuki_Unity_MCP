using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Aketsuki.UnityMcp.Editor
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
        /// 统一生命周期：<b>批次内共用一份</b> Prefab 内容 → edit 回调 → 提交（SaveAsPrefabAsset）。
        /// <para>
        /// 与旧版的差别（BUG-018）：不再每条指令 Load/Unload 一次，而是走
        /// <see cref="PrefabEditSession"/> —— 同一批次里对同一个 Prefab 连发多条写指令时，
        /// 大家改的是同一份内存副本，每条成功后提交一次，批次结束才卸载。
        /// 这样"后一条的 Load 读到前一条还没保存的状态"这类覆盖窗口就不存在了，
        /// 60 条指令也从 60 次加载降到 1 次。
        /// </para>
        /// <para>
        /// 回调抛异常 / 返回 Failed 时会话被丢弃（<b>不落盘</b>），磁盘上留下的是最后一条成功指令的状态。
        /// </para>
        /// </summary>
        public static CommandResult EditPrefab(CommandContext context, string prefabPath,
            Func<GameObject, CommandResult> edit)
        {
            var root = PrefabEditSession.Acquire(context, prefabPath);

            CommandResult result;
            try
            {
                result = edit(root);
            }
            catch
            {
                // 失败即回滚：连带这条指令改到一半的节点一起丢掉，别让它跟着下一次提交落盘
                PrefabEditSession.Discard(prefabPath);
                throw;
            }

            if (result != null && result.Status == CommandStatus.Failed)
            {
                PrefabEditSession.Discard(prefabPath);
                return result;
            }

            PrefabEditSession.Commit(prefabPath, root);

            // 场景 2（BUG-018 的另一条线）：这个 Prefab 正开在 Prefab 编辑模式里。
            // Stage 里是它自己的一份副本，关掉 / 切走 Stage 时 Unity 的 Auto Save 会用那份旧副本
            // 整体覆盖资产——刚写进去的新节点会静默消失。这里只能提醒，不能替用户关窗。
            var stageWarning = DescribeOpenPrefabStage(prefabPath);
            if (stageWarning != null && result != null)
            {
                result.AddWarning(stageWarning);
            }

            return result;
        }

        /// <summary>
        /// 这个 Prefab 是否正被 PrefabStage 打开着（是则返回一句警告，否则 null）。
        /// <para>
        /// <c>PrefabStage.prefabAssetPath</c> 在 2022.2 之后改名 <c>assetPath</c>，本包要同时装在
        /// 2021.3 与 2022.3，只能按版本宏取值（与 <c>CapturePrefabCommand</c> 同一套写法）。
        /// </para>
        /// </summary>
        public static string DescribeOpenPrefabStage(string prefabPath)
        {
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
            {
                return null;
            }

#if UNITY_2022_2_OR_NEWER
            var openPath = stage.assetPath;
#else
            var openPath = stage.prefabAssetPath;
#endif
            if (string.IsNullOrEmpty(openPath)
                || !string.Equals(openPath.Replace('\\', '/'), prefabPath.Replace('\\', '/'),
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return "该 Prefab 正开在 Prefab 编辑模式里：Stage 内是独立的一份副本，关闭 / 切走 Stage 时" +
                   "Unity 的 Auto Save 可能用它覆盖刚写入的资产（本次的新节点会消失）。" +
                   "落地前请先关掉 Prefab 模式，或重新打开该 Prefab 让 Stage 重载。";
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
        /// 开关 Prefab 资产内某个节点的激活态（IMPR-017）。
        /// <para>
        /// <c>prefab.setActive</c> 直接调用；<c>gameobject.setActive</c> 带 <c>prefab</c> 参数时也走这里
        /// ——两条入口共用一套语义，避免"同一个动作在两个域里行为不一样"。
        /// </para>
        /// <para>
        /// <c>includeChildren</c> 与 <c>gameobject.setActive</c> 一致：只切一级子节点（不是整棵子树）。
        /// 这个口径容易误会，所以结果里回显 <c>changedCount</c>，让人一眼看出到底改了几个。
        /// </para>
        /// </summary>
        public static CommandResult SetActiveOnAsset(CommandContext context, string prefabPath,
            string targetSpec, bool active, bool includeChildren)
        {
            return EditPrefab(context, prefabPath, root =>
            {
                var node = SetComponentFieldCommand.ResolvePrefabTarget(root, targetSpec, context);

                var changed = 0;
                if (node.gameObject.activeSelf != active)
                {
                    node.gameObject.SetActive(active);
                    changed++;
                }

                var children = includeChildren ? node.childCount : 0;
                if (includeChildren)
                {
                    for (var i = 0; i < node.childCount; i++)
                    {
                        var child = node.GetChild(i);
                        if (child.gameObject.activeSelf != active)
                        {
                            child.gameObject.SetActive(active);
                            changed++;
                        }
                    }
                }

                var path = PrefabEditCommandsUtil.BuildNodePath(node);
                var data = JsonValue.NewObject();
                data.Set("prefab", JsonValue.Of(prefabPath));
                data.Set("path", JsonValue.Of(path));
                data.Set("active", JsonValue.Of(active));
                data.Set("includeChildren", JsonValue.Of(includeChildren));
                data.Set("childCount", JsonValue.Of(children));
                data.Set("changedCount", JsonValue.Of(changed));
                data.Set("activeSelf", JsonValue.Of(node.gameObject.activeSelf));

                return CommandResult.Ok(
                    $"已把 {prefabPath} 里的 {path} 设为 {(active ? "active" : "inactive")}"
                    + (includeChildren ? $"（含 {children} 个直接子节点，实际改动 {changed} 个）" : string.Empty)
                    + (changed == 0 ? "（本来就已是该状态，未改动）" : string.Empty),
                    data);
            });
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
        /// 归一化「要实例化的 Prefab 资产」参数（<c>prefab.instantiatePrefab</c> 的 asset）。
        /// <para>
        /// 与 <see cref="NormalizePrefabPath"/> 的差别：<b>不强加 .prefab 后缀</b>。
        /// 源可以是 .fbx 之类的模型资产（Unity 一样能 <c>InstantiatePrefab</c>），
        /// 所以只在「原样加载不到 + 本来也没有 .prefab 后缀」时才补一次后缀。
        /// </para>
        /// </summary>
        public static string NormalizePrefabAssetPath(CommandContext context, string raw, string paramName)
        {
            var text = (raw ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
            if (text.Length == 0)
            {
                throw new CommandArgumentException(context.CommandId, paramName,
                    $"{paramName} 不能为空（基于 Assets/ 的 Prefab 资产路径）");
            }

            if (!text.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandArgumentException(context.CommandId, paramName,
                    $"{paramName} 必须基于 Assets/：'{text}'");
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(text) == null
                && !text.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                var withSuffix = text + ".prefab";
                if (AssetDatabase.LoadAssetAtPath<GameObject>(withSuffix) != null)
                {
                    text = withSuffix;
                }
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
        /// <para>
        /// 源 Prefab 在本批次里已有会话（<see cref="PrefabEditSession"/>）时**借用**那份内存态，
        /// 此时不持有、Dispose 也不卸载它——卸载会把别的指令正在改的东西一起毁掉。
        /// </para>
        /// </summary>
        public sealed class CopySource : IDisposable
        {
            private GameObject _loadedContents;
            private readonly bool _crossPrefab;

            /// <summary>源节点（当前 Prefab 内，或临时加载的另一个 Prefab 内）</summary>
            public Transform Node { get; private set; }

            /// <summary>源所在 Prefab 的资产路径；null 表示源就在当前 Prefab 内</summary>
            public string PrefabPath { get; private set; }

            /// <summary>源节点的书写形式（原样回显，用于结果文案）</summary>
            public string Spec { get; private set; }

            public bool IsCrossPrefab
            {
                get { return _crossPrefab; }
            }

            internal CopySource(Transform node, string prefabPath, string spec, GameObject loadedContents,
                bool crossPrefab = false)
            {
                Node = node;
                PrefabPath = prefabPath;
                Spec = spec;
                _loadedContents = loadedContents;
                _crossPrefab = crossPrefab || loadedContents != null;
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
                    // 本批次里已经打开过这个源 Prefab（前面几条指令写过它）时直接用内存态：
                    // 磁盘上可能还没有最新改动，也不能为了读一次就把别人的会话卸掉
                    GameObject openRoot;
                    if (PrefabEditSession.TryGetOpen(assetPath, out openRoot))
                    {
                        var opened = SetComponentFieldCommand.FindInPrefab(openRoot, nodeSpec);
                        if (opened == null)
                        {
                            throw new CommandArgumentException(context.CommandId, "copyFrom",
                                $"源 Prefab '{assetPath}' 里找不到节点 '{nodeSpec}'（空串表示该 Prefab 根节点）");
                        }

                        return new CopySource(opened, assetPath, text, null, true);
                    }

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
    /// 在 Prefab 资产内嵌一个<b>真正的嵌套 Prefab 实例</b>（FEAT-016）。
    /// <para>
    /// 为什么要有它：<c>prefab.duplicateNode</c> 复制嵌套实例时拿到的是<b>展开的普通节点树</b>
    /// （<c>m_CorrespondingSourceObject</c> 全为 0，<c>PrefabInstanceStatus.NotAPrefab</c>），
    /// 即"复制得很像但断了源头"——母 Prefab 里同一个子 Prefab 有几份实例时，
    /// 源 Prefab 后续改图 / 改尺寸 / 加动画，只有本来就是实例的那几份跟着变，复制出来的各自走样。
    /// <c>prefab.addNode</c> 的 copyFrom 同理是 <c>CopySerialized</c> 平铺数据，保不住连接。
    /// </para>
    /// <para>
    /// 本指令用 <c>PrefabUtility.InstantiatePrefab</c>：新节点是源 Prefab 的实例
    /// （<c>PrefabInstanceStatus.Connected</c>），源资产一改它跟着变。
    /// </para>
    /// <para>
    /// 参数：prefab（必填，宿主 Prefab 资产路径）、asset（必填，要实例化的 Prefab 资产路径）、
    /// parent（可选，宿主内父节点路径，空串 = 根）、name（可选，默认沿用源名）、index（可选，sibling 顺序）
    /// </para>
    /// </summary>
    [Command("prefab.instantiatePrefab", Description = "在 Prefab 资产内嵌一个真正的嵌套 Prefab 实例（保留 Prefab 连接）",
        Usage = "prefab: string, asset: string, parent: string, name: string, index: int",
        EditorOnly = true)]
    public sealed class PrefabInstantiatePrefabCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
#if UNITY_EDITOR
            var prefabPath = PrefabEditUtil.NormalizePrefabPath(context, context.Args.RequireString("prefab"));
            var assetPath = PrefabEditUtil.NormalizePrefabAssetPath(context, context.Args.RequireString("asset"), "asset");
            var parentSpec = context.Args.GetString("parent", string.Empty);
            var name = context.Args.GetString("name", null);
            var index = context.Args.GetInt("index", -1);

            // 源资产在进 Prefab 内容之前先取好：进入 LoadPrefabContents 后再做资产加载，
            // 等于在一份临时场景里多挂一次，没必要，也让"源不存在"的报错早点发生。
            var assetObject = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (assetObject == null)
            {
                throw new CommandArgumentException(context.CommandId, "asset",
                    $"找不到源 Prefab '{assetPath}'（路径要基于 Assets/、指向一个 Prefab / 模型资产）");
            }

            var assetType = PrefabUtility.GetPrefabAssetType(assetObject);
            if (assetType == PrefabAssetType.MissingAsset || assetType == PrefabAssetType.NotAPrefab)
            {
                throw new CommandArgumentException(context.CommandId, "asset",
                    $"'{assetPath}' 不是可实例化的 Prefab 资产（PrefabAssetType={assetType}）");
            }

            return PrefabEditUtil.EditPrefab(context, prefabPath, root =>
            {
                var parent = SetComponentFieldCommand.FindInPrefab(root, parentSpec);
                if (parent == null)
                {
                    throw new CommandArgumentException(context.CommandId, "parent",
                        $"Prefab '{prefabPath}' 里找不到父节点 '{parentSpec}'（空串表示根节点）");
                }

                var instance = PrefabUtility.InstantiatePrefab(assetObject, parent) as GameObject;
                if (instance == null)
                {
                    throw new CommandException(
                        $"实例化 '{assetPath}' 失败（PrefabUtility.InstantiatePrefab 返回空；" +
                        "该资产可能不是 Prefab 资产根）");
                }

                if (!string.IsNullOrEmpty(name))
                {
                    instance.name = name;
                }

                var siblingIndex = instance.transform.GetSiblingIndex();
                if (index >= 0)
                {
                    // InstantiatePrefab 挂上去时是最后一个子节点；SetSiblingIndex 的合法区间是
                    // [0, childCount-1]，越界 Unity 会自己夹到末尾，这里显式夹一下好回显真实落点。
                    var clamped = index;
                    var last = parent.childCount - 1;
                    if (clamped > last)
                    {
                        clamped = last;
                    }

                    instance.transform.SetSiblingIndex(clamped);
                    siblingIndex = instance.transform.GetSiblingIndex();
                }

                var status = PrefabUtility.GetPrefabInstanceStatus(instance);
                var path = PrefabEditCommandsUtil.BuildNodePath(instance.transform);

                var data = JsonValue.NewObject();
                data.Set("prefab", JsonValue.Of(prefabPath));
                data.Set("asset", JsonValue.Of(assetPath));
                data.Set("path", JsonValue.Of(path));
                data.Set("instanceStatus", JsonValue.Of(status.ToString()));
                data.Set("connected", JsonValue.Of(status == PrefabInstanceStatus.Connected));
                data.Set("siblingIndex", JsonValue.Of(siblingIndex));
                data.Set("childCount", JsonValue.Of(instance.transform.childCount));

                var message = $"已在 {prefabPath} 的 {parent.name} 下实例化 {assetPath}"
                              + $" → {path}（{status}）";
                if (status != PrefabInstanceStatus.Connected)
                {
                    // 不静默：这正是本指令要解决的问题，没连上等于白做
                    data.Set("warning", JsonValue.Of(
                        $"实例状态是 {status} 而不是 Connected：源 Prefab 以后的改动不会同步到这个节点"));
                    message += $"（注意：状态是 {status}，源 Prefab 改动不会同步到这里）";
                }

                return CommandResult.Ok(message, data);
            });
#else
            throw new CommandException($"指令 '{context.CommandId}' 只能在编辑器内执行");
#endif
        }
    }

    /// <summary>
    /// 把 Prefab 里<b>已有的展开节点</b>替换成 Prefab 实例（FEAT-016 进阶项，用户 2026-11-03 拍板「可以」）。
    ///
    /// <para>
    /// <b>解决什么：</b>历史遗留的断链节点（当年 <c>duplicateNode</c> / <c>copyFrom</c> 复制出来的平铺树，
    /// <c>PrefabInstanceStatus.NotAPrefab</c>）想修好，之前只能「建新的实例 + 手工搬布局 + 删旧的」三步，
    /// 中间任何一步漏了就是"看着像、位置不对"或"引用了已经被删的节点"。本指令把三步合成一步。
    /// </para>
    ///
    /// <para>
    /// <b>布局复用口径（<c>layout</c>，默认 <c>rect</c>）：</b>
    /// <list type="bullet">
    /// <item><c>rect</c>（默认）—— 两者都是 RectTransform 时复制 <c>anchorMin/anchorMax/pivot/
    /// sizeDelta/anchoredPosition</c>，另外 <c>localPosition/localRotation/localScale</c> 一律复制；
    /// 只有一边是 RectTransform 时退回 <c>transform</c> 的行为并给 <c>warning</c>。</item>
    /// <item><c>transform</c> —— 只复制 <c>localPosition/localRotation/localScale</c>（用 Prefab 自己的尺寸）。</item>
    /// <item><c>none</c> —— 完全不碰，保留源 Prefab 的原始布局。</item>
    /// </list>
    /// 为什么不复制 RectTransform 的全部字段：<c>offsetMin/offsetMax</c> 与 <c>sizeDelta/anchoredPosition</c>
    /// 是同一组数据的两种写法，全抄会互相覆盖；旋转/缩放已经单独搬了。
    /// </para>
    ///
    /// <para>
    /// <b>安全闸：</b>
    /// ① 不支持替换根节点（整根换源请改 <c>prefab.instantiatePrefab</c> 重建）；
    /// ② 旧节点已经是 <c>Connected</c> 实例时默认拒绝 —— 它身上可能有 Inspector 里调过的 override，
    /// 替换等于把这些改动一起丢掉；确认要换源请传 <c>force: true</c>。
    /// </para>
    ///
    /// <para>
    /// 参数：prefab（必填，宿主 Prefab）、target（必填，要替换掉的旧节点路径）、asset（必填，新实例化的 Prefab 资产）、
    /// name（可选，默认沿用旧节点名）、layout（默认 rect）、keepChildren（默认 false；true 则把旧节点的子节点搬到新实例下）、
    /// force（默认 false）
    /// </para>
    /// </summary>
    [Command("prefab.replaceWithInstanceOf",
        Description = "把 Prefab 里已有的展开节点替换成 Prefab 实例（复用布局值，可选搬走子节点）",
        Usage = "prefab: string, target: string, asset: string, name: string, layout: string(rect|transform|none), keepChildren: bool, force: bool",
        EditorOnly = true)]
    public sealed class PrefabReplaceWithInstanceCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
#if UNITY_EDITOR
            var prefabPath = PrefabEditUtil.NormalizePrefabPath(context, context.Args.RequireString("prefab"));
            var targetSpec = context.Args.GetString("target", string.Empty);
            var assetPath = PrefabEditUtil.NormalizePrefabAssetPath(context,
                context.Args.RequireString("asset"), "asset");
            var name = context.Args.GetString("name", null);
            var layout = (context.Args.GetString("layout", "rect") ?? "rect").Trim().ToLowerInvariant();
            var keepChildren = context.Args.GetBool("keepChildren", false);
            var force = context.Args.GetBool("force", false);

            if (layout != "rect" && layout != "transform" && layout != "none")
            {
                throw new CommandArgumentException(context.CommandId, "layout",
                    $"layout 只能是 rect / transform / none，实际为 '{layout}'");
            }

            // 根节点不能替换：SaveAsPrefabAsset 存的就是这个对象，删掉它就没东西可存了
            if (string.IsNullOrEmpty(targetSpec.Trim()))
            {
                throw new CommandArgumentException(context.CommandId, "target",
                    "不支持替换 Prefab 根节点（空串 / 根路径）：整根换源请直接用 prefab.instantiatePrefab 重建一个同级资产");
            }

            var assetObject = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (assetObject == null)
            {
                throw new CommandArgumentException(context.CommandId, "asset",
                    $"找不到源 Prefab '{assetPath}'（路径要基于 Assets/、指向一个 Prefab / 模型资产）");
            }

            var assetType = PrefabUtility.GetPrefabAssetType(assetObject);
            if (assetType == PrefabAssetType.MissingAsset || assetType == PrefabAssetType.NotAPrefab)
            {
                throw new CommandArgumentException(context.CommandId, "asset",
                    $"'{assetPath}' 不是可实例化的 Prefab 资产（PrefabAssetType={assetType}）");
            }

            return PrefabEditUtil.EditPrefab(context, prefabPath, root =>
            {
                var old = SetComponentFieldCommand.FindInPrefab(root, targetSpec.Trim());
                if (old == null)
                {
                    throw new CommandArgumentException(context.CommandId, "target",
                        $"Prefab '{prefabPath}' 里找不到节点 '{targetSpec}'");
                }

                if (old.transform == root.transform)
                {
                    throw new CommandArgumentException(context.CommandId, "target",
                        $"'{targetSpec}' 解析到的是 Prefab 根节点，不支持替换（同上：请重建资产）");
                }

                var oldStatus = PrefabUtility.GetPrefabInstanceStatus(old);
                if (oldStatus == PrefabInstanceStatus.Connected && !force)
                {
                    return CommandResult.Fail(
                        $"'{targetSpec}' 已经是 {assetPath} 这类 Prefab 的 Connected 实例，替换会把它在 Inspector 里调过的 override 一起丢掉。" +
                        "确认要换源请传 force: true。");
                }

                // 旧节点的一切都必须在 DestroyImmediate 之前取完
                var parent = old.transform.parent;
                var siblingIndex = old.transform.GetSiblingIndex();
                var oldName = old.name;
                var oldPath = PrefabEditCommandsUtil.BuildNodePath(old.transform);
                var oldChildCount = old.transform.childCount;

                var instance = PrefabUtility.InstantiatePrefab(assetObject, parent) as GameObject;
                if (instance == null)
                {
                    throw new CommandException(
                        $"实例化 '{assetPath}' 失败（PrefabUtility.InstantiatePrefab 返回空；该资产可能不是 Prefab 资产根）");
                }

                instance.name = string.IsNullOrEmpty(name) ? oldName : name;

                var copied = ApplyLayout(old.transform, instance.transform, layout, out var layoutWarning);
                instance.transform.SetSiblingIndex(siblingIndex);

                // 子节点搬运：先取快照再搬，边遍历边改父子关系会漏
                var movedChildren = 0;
                if (keepChildren && oldChildCount > 0)
                {
                    var children = new List<Transform>(oldChildCount);
                    for (var i = 0; i < oldChildCount; i++)
                    {
                        children.Add(old.transform.GetChild(i));
                    }

                    foreach (var child in children)
                    {
                        child.SetParent(instance.transform, false);
                        movedChildren++;
                    }
                }

                UnityEngine.Object.DestroyImmediate(old);

                var status = PrefabUtility.GetPrefabInstanceStatus(instance);
                var newPath = PrefabEditCommandsUtil.BuildNodePath(instance.transform);

                var data = JsonValue.NewObject();
                data.Set("prefab", JsonValue.Of(prefabPath));
                data.Set("asset", JsonValue.Of(assetPath));
                data.Set("oldPath", JsonValue.Of(oldPath));
                data.Set("newPath", JsonValue.Of(newPath));
                data.Set("oldName", JsonValue.Of(oldName));
                data.Set("newName", JsonValue.Of(instance.name));
                data.Set("oldInstanceStatus", JsonValue.Of(oldStatus.ToString()));
                data.Set("instanceStatus", JsonValue.Of(status.ToString()));
                data.Set("connected", JsonValue.Of(status == PrefabInstanceStatus.Connected));
                data.Set("layout", JsonValue.Of(layout));
                data.Set("copiedLayoutFields", JsonValue.FromArray(ToJsonItems(copied)));
                data.Set("movedChildCount", JsonValue.Of(movedChildren));
                data.Set("discardedChildCount", JsonValue.Of(keepChildren ? 0 : oldChildCount));
                data.Set("siblingIndex", JsonValue.Of(instance.transform.GetSiblingIndex()));

                var message = $"已把 {oldPath} 替换为 {assetPath} 的实例 → {newPath}（{status}）"
                              + $"，复用 {copied.Count} 个布局字段"
                              + (movedChildren > 0 ? $"，搬走 {movedChildren} 个子节点" : string.Empty)
                              + (!keepChildren && oldChildCount > 0 ? $"，丢弃 {oldChildCount} 个原子节点" : string.Empty);

                var warnings = new List<string>();
                if (!string.IsNullOrEmpty(layoutWarning))
                {
                    warnings.Add(layoutWarning);
                }

                if (status != PrefabInstanceStatus.Connected)
                {
                    warnings.Add($"实例状态是 {status} 而不是 Connected：源 Prefab 以后的改动不会同步到这个节点");
                }

                if (oldStatus == PrefabInstanceStatus.Connected)
                {
                    warnings.Add("旧节点原本就是实例，它身上的 override 已随替换丢弃（本次由 force: true 放行）");
                }

                if (!keepChildren && oldChildCount > 0)
                {
                    warnings.Add("旧节点的子节点已随旧节点一起丢弃（如需保留请传 keepChildren: true）");
                }

                if (warnings.Count > 0)
                {
                    data.Set("warning", JsonValue.Of(string.Join("；", warnings.ToArray())));
                    message += "。注意：" + string.Join("；", warnings.ToArray());
                }

                return CommandResult.Ok(message, data);
            });
#else
            throw new CommandException($"指令 '{context.CommandId}' 只能在编辑器内执行");
#endif
        }

        /// <summary>
        /// 按 <paramref name="layout"/> 把旧节点的布局值搬到新实例上，返回实际复制的字段名。
        /// <para>
        /// RectTransform 的复制顺序固定为 anchors → pivot → sizeDelta → anchoredPosition：
        /// 改 anchor 会带动 sizeDelta / anchoredPosition 的重算，反着写会把刚设好的值冲掉。
        /// </para>
        /// </summary>
        private static List<string> ApplyLayout(Transform from, Transform to, string layout, out string warning)
        {
            warning = null;
            var copied = new List<string>();
            if (layout == "none")
            {
                return copied;
            }

            to.localPosition = from.localPosition;
            to.localRotation = from.localRotation;
            to.localScale = from.localScale;
            copied.Add("localPosition");
            copied.Add("localRotation");
            copied.Add("localScale");

            if (layout != "rect")
            {
                return copied;
            }

            var source = from as RectTransform;
            var target = to as RectTransform;
            if (source == null || target == null)
            {
                if (source != null || target != null)
                {
                    warning = source == null
                        ? "旧节点不是 RectTransform，新实例是：只搬了 localPosition/Rotation/Scale，锚点与尺寸沿用源 Prefab"
                        : "新实例不是 RectTransform，旧节点是：只搬了 localPosition/Rotation/Scale，UI 布局信息无法复用";
                }

                return copied;
            }

            target.anchorMin = source.anchorMin;
            target.anchorMax = source.anchorMax;
            target.pivot = source.pivot;
            target.sizeDelta = source.sizeDelta;
            target.anchoredPosition = source.anchoredPosition;
            copied.Add("anchorMin");
            copied.Add("anchorMax");
            copied.Add("pivot");
            copied.Add("sizeDelta");
            copied.Add("anchoredPosition");
            return copied;
        }

        private static List<JsonValue> ToJsonItems(List<string> names)
        {
            var items = new List<JsonValue>(names.Count);
            foreach (var name in names)
            {
                items.Add(JsonValue.Of(name));
            }

            return items;
        }
    }

    /// <summary>
    /// 给 Prefab 资产内的节点改名（FEAT-016 顺带的缺口之一）。
    /// <para>
    /// 为什么单独一条：<c>gameobject.rename</c> 只在<b>当前已加载场景</b>里找节点，
    /// 传 <c>prefab</c> 会落到场景里去找、报「找不到节点（当前已加载场景：Global.unity）」。
    /// 于是 prefab 域想改个名只能走「duplicateNode 新节点 + 重绑字段 + removeNode 旧节点」，
    /// 一条改名变成三条，还有丢引用的风险。
    /// </para>
    /// <para>参数：prefab（必填）、target（必填，节点路径，空串 = 根）、name（必填，新名字）</para>
    /// </summary>
    [Command("prefab.renameNode", Description = "给 Prefab 资产内的节点改名（prefab 域的 gameobject.rename）",
        Usage = "prefab: string, target: string, name: string",
        EditorOnly = true)]
    public sealed class PrefabRenameNodeCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
#if UNITY_EDITOR
            var prefabPath = PrefabEditUtil.NormalizePrefabPath(context, context.Args.RequireString("prefab"));
            var targetSpec = context.Args.RequireString("target");
            var name = context.Args.RequireString("name");

            return PrefabEditUtil.EditPrefab(context, prefabPath, root =>
            {
                var node = SetComponentFieldCommand.FindInPrefab(root, targetSpec);
                if (node == null)
                {
                    throw new CommandArgumentException(context.CommandId, "target",
                        $"Prefab '{prefabPath}' 里找不到节点 '{targetSpec}'（空串表示根节点）");
                }

                var oldName = node.name;
                var oldPath = PrefabEditCommandsUtil.BuildNodePath(node);
                if (string.Equals(oldName, name, StringComparison.Ordinal))
                {
                    var same = JsonValue.NewObject();
                    same.Set("prefab", JsonValue.Of(prefabPath));
                    same.Set("path", JsonValue.Of(oldPath));
                    same.Set("renamed", JsonValue.Of(false));
                    return CommandResult.Ok($"{oldPath} 名字已经是 '{name}'，未改动（幂等）", same);
                }

                node.name = name;
                var path = PrefabEditCommandsUtil.BuildNodePath(node);

                var data = JsonValue.NewObject();
                data.Set("prefab", JsonValue.Of(prefabPath));
                data.Set("path", JsonValue.Of(path));
                data.Set("oldName", JsonValue.Of(oldName));
                data.Set("oldPath", JsonValue.Of(oldPath));
                data.Set("renamed", JsonValue.Of(true));
                data.Set("warning", JsonValue.Of(
                    "Unity 内部按 fileID 引用节点，改名不会断资产引用；" +
                    "但代码里按名字 Find(\"" + oldName + "\") 的地方会失效，需自行检查"));

                return CommandResult.Ok($"已把 {oldPath} 改名为 {name}（{prefabPath}）", data);
            });
#else
            throw new CommandException($"指令 '{context.CommandId}' 只能在编辑器内执行");
#endif
        }
    }

    /// <summary>
    /// 开关 Prefab 资产内某个节点的激活态（IMPR-017）。
    /// <para>
    /// 为什么必须有这条：<c>gameobject.setActive</c> 只在<b>当前已加载场景</b>里找节点，
    /// 传 <c>prefab</c> 参数会被静默忽略、然后按节点路径命中场景里的同名实例 ——
    /// 实测踩过：想关 prefab 里的模板节点，结果改的是打开着的场景（<c>Global.unity</c>）里的实例，
    /// 返回里还带着 <c>sceneName: Global</c>，不看就发现不了。
    /// </para>
    /// <para>参数：prefab（必填）、target（必填，节点路径，空串 = 根）、active（默认 true）、
    /// includeChildren（默认 false，只切一级子节点，与 gameobject.setActive 同口径）</para>
    /// </summary>
    [Command("prefab.setActive", Description = "开关 Prefab 资产内节点的激活态（prefab 域的 gameobject.setActive）",
        Usage = "prefab: string, target: string, active: bool, includeChildren: bool",
        EditorOnly = true)]
    public sealed class PrefabSetActiveCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
#if UNITY_EDITOR
            var prefabPath = PrefabEditUtil.NormalizePrefabPath(context, context.Args.RequireString("prefab"));
            var targetSpec = context.Args.RequireString("target");
            var active = context.Args.GetBool("active", true);
            var includeChildren = context.Args.GetBool("includeChildren", false);

            return PrefabEditUtil.SetActiveOnAsset(context, prefabPath, targetSpec, active, includeChildren);
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

            // 批次内前面几条指令可能刚改过这个 Prefab：优先读会话里的内存态，
            // 否则读到的是磁盘上"最后一次提交"的样子，会把已经改好的节点看成不存在
            GameObject sessionRoot;
            var borrowed = PrefabEditSession.TryGetOpen(prefabPath, out sessionRoot);
            var root = borrowed ? sessionRoot : PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null)
            {
                throw new CommandArgumentException(context.CommandId, "prefab",
                    $"打不开 Prefab '{prefabPath}'");
            }

            try
            {
                var total = 0;
                var instances = 0;
                var nodes = Visit(root.transform, 0, maxDepth, ref total, ref instances);

                var result = JsonValue.NewObject();
                result.Set("prefab", JsonValue.Of(prefabPath));
                result.Set("count", JsonValue.Of(total));
                result.Set("instanceCount", JsonValue.Of(instances));
                result.Set("nodes", nodes);
                return CommandResult.Ok(
                    $"Prefab 共 {total} 个节点（maxDepth={maxDepth}"
                    + (instances > 0 ? $"，其中 {instances} 个是嵌套 Prefab 实例" : string.Empty) + "）",
                    result);
            }
            finally
            {
                // 借来的会话副本由 PrefabEditSession 负责卸载
                if (!borrowed)
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
#else
            throw new CommandException($"指令 '{context.CommandId}' 只能在编辑器内执行");
#endif
        }

        private static JsonValue Visit(Transform node, int depth, int maxDepth, ref int total, ref int instances)
        {
            total++;
            var item = SceneQueryCommandsUtil.NodeToJson(node);
            item.Set("childCount", JsonValue.Of(node.childCount));
            AttachInstanceInfo(item, node, ref instances);

            var children = JsonValue.NewArray();
            if (maxDepth < 0 || depth < maxDepth)
            {
                for (var i = 0; i < node.childCount; i++)
                {
                    children.Add(Visit(node.GetChild(i), depth + 1, maxDepth, ref total, ref instances));
                }
            }

            item.Set("children", children);
            return item;
        }

        /// <summary>
        /// 给节点补上"我是不是嵌套 Prefab 实例、来自哪个资产"（FEAT-016 顺带的缺口之二）。
        /// <para>
        /// 原来树里看不出实例身份：嵌套实例在 YAML 里是 <c>!u!1001</c> 的 PrefabInstance 块，
        /// 排查"这个节点为什么没跟着源 Prefab 变"只能回头翻 YAML。
        /// 现在每个实例节点都带 <c>isPrefabInstance</c> / <c>instanceStatus</c> / <c>sourceAsset</c>，
        /// <c>Disconnected</c> / <c>MissingAsset</c> 一眼可见——那正是"断了源头"的信号。
        /// </para>
        /// <para>非实例节点不加字段（而不是加 <c>false</c>）：老调用方解析不受影响，树也不会平白变大。</para>
        /// </summary>
        private static void AttachInstanceInfo(JsonValue item, Transform node, ref int instances)
        {
            var status = PrefabUtility.GetPrefabInstanceStatus(node.gameObject);
            if (status == PrefabInstanceStatus.NotAPrefab)
            {
                return;
            }

            // 注意：实例**内部**的节点（比如 RedDot 下的 sign）GetPrefabInstanceStatus 也是 Connected，
            // 所以"是不是实例"和"是不是实例根"是两件事。计数只数根，
            // 否则一个带 3 个子节点的实例会被记成 4 个实例，跟 YAML 里的 PrefabInstance 块对不上。
            var nearestRoot = PrefabUtility.GetNearestPrefabInstanceRoot(node.gameObject);
            var isRoot = nearestRoot == node.gameObject;
            if (isRoot)
            {
                instances++;
                item.Set("isPrefabInstanceRoot", JsonValue.Of(true));
            }

            item.Set("isPrefabInstance", JsonValue.Of(true));
            item.Set("instanceStatus", JsonValue.Of(status.ToString()));

            var source = PrefabUtility.GetCorrespondingObjectFromSource(node.gameObject);
            if (source != null)
            {
                var assetPath = AssetDatabase.GetAssetPath(source);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    item.Set("sourceAsset", JsonValue.Of(assetPath));
                }
            }
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
        Usage = "target: string（Prefab 模式下空串=根）, component: string, prefab: string, require: bool",
        EditorOnly = true)]
    public sealed class AddComponentCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
#if UNITY_EDITOR
            // IMPR-006：与 component.remove / setField / setFields 一致——Prefab 模式下 target 可为空串（= 根节点）
            var targetSpec = SetComponentFieldCommand.RequireTargetSpec(context);
            var typeName = context.Args.RequireString("component");
            var require = context.Args.GetBool("require", false);

            var prefabPath = context.Args.GetString("prefab", null);
            if (!string.IsNullOrEmpty(prefabPath))
            {
                var normalized = PrefabEditUtil.NormalizePrefabPath(context, prefabPath);
                return PrefabEditUtil.EditPrefab(context, normalized, root =>
                {
                    var node = SetComponentFieldCommand.ResolvePrefabTarget(root, targetSpec, context);
                    var added = PrefabEditUtil.AddComponents(
                        node.gameObject, new[] { typeName }, context, require, false);
                    return BuildResult(node, typeName, added, normalized);
                });
            }

            SetComponentFieldCommand.EnsureSceneTarget(targetSpec, context);

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
