using System.Collections.Generic;
using UnityEngine;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 按名称 / 类型 / 层级路径查找场景节点，返回完整路径与组件清单
    /// <para>
    /// 是 <c>component.setField</c>、<c>gameobject.*</c> 等指令的"眼睛"：
    /// 绑字段前先用它确认目标节点路径，避免盲操作。
    /// 查找规则与 <c>parent</c> 参数完全一致（见 <see cref="SceneQuery"/>），
    /// 支持 <c>"HUD"</c>、<c>"UIRoot/HUD/Canvas"</c>、<c>"type:Canvas#1"</c>、
    /// <c>{"type":"Image","index":2}</c> 等写法，含 inactive 节点。
    /// </para>
    /// <para>
    /// 参数：query（必填）、all（默认 false，返回全部命中而非最佳一个）、maxCount（all 时限制返回条数）
    /// </para>
    /// </summary>
    [Command("gameobject.find", Description = "按名称/类型/路径查找场景节点，返回路径与组件清单",
        Usage = "query: string|object, all: bool, maxCount: int",
        EditorOnly = true)]
    public sealed class FindGameObjectCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var query = context.Args.Require("query");
            var maxCount = context.Args.GetInt("maxCount", 100);

            var all = context.Args.GetBool("all", false);
            if (!all)
            {
                var node = SceneQuery.ResolveTarget(query);
                if (node == null)
                {
                    var notFound = SceneQueryCommandsUtil.NewResult(false);
                    SceneQueryCommandsUtil.AttachScene(notFound);
                    notFound.Set("message",
                        JsonValue.Of($"场景中没有命中 {SceneQueryCommandsUtil.Describe(query)}（当前已加载场景：{SceneQuery.SceneList()}）"));
                    return CommandResult.Ok($"场景中没有命中 {SceneQueryCommandsUtil.Describe(query)}", notFound);
                }

                // 单条返回也要提示歧义，避免用户误以为唯一命中（BUG-1/BUG-3）
                var allMatches = SceneQuery.FindAll(query);
                var ambiguous = allMatches.Count > 1;

                var data = SceneQueryCommandsUtil.NewResult(true);
                SceneQueryCommandsUtil.AttachScene(data);
                data.Set("match", SceneQueryCommandsUtil.NodeToJson(node));
                if (ambiguous)
                {
                    data.Set("ambiguous", JsonValue.Of(true));
                    data.Set("matchCount", JsonValue.Of(allMatches.Count));
                    var alternatives = JsonValue.NewArray();
                    foreach (var alternative in allMatches)
                    {
                        alternatives.Add(SceneQueryCommandsUtil.NodeToJson(alternative));
                    }

                    data.Set("alternatives", alternatives);
                }

                return CommandResult.Ok(
                    $"命中 {SceneQuery.BuildPath(node)}（{SceneQueryCommandsUtil.DescribeComponents(node)}）"
                    + (ambiguous ? $"，但存在 {allMatches.Count} 个同名/同类型节点，请用 match 里的 selectPath 或 instanceId 精确消歧" : string.Empty),
                    data);
            }

            // all:true —— 统一走 FindAll，正确处理 type:X#N 的 #N 剥离（修 BUG-5 两条分支语法不一致）
            var matches = SceneQuery.FindAll(query);

            var truncated = matches.Count > maxCount;
            if (truncated)
            {
                matches = matches.GetRange(0, maxCount);
            }

            var items = JsonValue.NewArray();
            foreach (var match in matches)
            {
                items.Add(SceneQueryCommandsUtil.NodeToJson(match));
            }

            var result = SceneQueryCommandsUtil.NewResult(matches.Count > 0);
            SceneQueryCommandsUtil.AttachScene(result);
            result.Set("count", JsonValue.Of(matches.Count));
            result.Set("truncated", JsonValue.Of(truncated));
            result.Set("matches", items);
            return CommandResult.Ok(
                $"共 {matches.Count} 个命中" + (truncated ? $"（只返回前 {maxCount} 条）" : string.Empty)
                + $"（当前已加载场景：{SceneQuery.SceneList()}）",
                result);
        }
    }

    /// <summary>
    /// 列出某个节点的子树（含每个节点的组件类型与激活状态）
    /// <para>
    /// 不给 target 时列出所有已加载场景的根（含 DontDestroyOnLoad）。
    /// 结果按层级嵌套，每个节点带完整路径，可直接抄给 <c>component.setField</c> 的
    /// target / value 参数使用。
    /// </para>
    /// <para>
    /// 参数：target（可选，默认全部场景根）、maxDepth（默认 -1 不限深，0 表示只列目标自身）
    /// </para>
    /// </summary>
    [Command("scene.hierarchy", Description = "列出场景节点子树（含组件类型与激活状态）",
        Usage = "target: string|object, maxDepth: int",
        EditorOnly = true)]
    public sealed class SceneHierarchyCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var maxDepth = context.Args.GetInt("maxDepth", -1);
            var total = 0;

            var roots = new List<Transform>();
            var targetSpec = context.Args.Get("target");
            if (targetSpec != null && !targetSpec.IsNull)
            {
                var target = SceneQuery.ResolveTarget(targetSpec);
                if (target == null)
                {
                    throw new CommandArgumentException(context.CommandId, "target",
                        $"找不到节点 {SceneQueryCommandsUtil.Describe(targetSpec)}");
                }

                roots.Add(target);
            }
            else
            {
                roots.AddRange(SceneQuery.SceneRoots());
            }

            var nodes = JsonValue.NewArray();
            foreach (var root in roots)
            {
                nodes.Add(Visit(root, 0, maxDepth, ref total));
            }

            var result = SceneQueryCommandsUtil.NewResult(total > 0);
            result.Set("count", JsonValue.Of(total));
            result.Set("nodes", nodes);
            SceneQueryCommandsUtil.AttachScene(result);
            return CommandResult.Ok($"共 {total} 个节点（maxDepth={maxDepth}）", result);
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

    /// <summary>查询类指令共用的节点序列化与描述工具</summary>
    internal static class SceneQueryCommandsUtil
    {
        /// <summary>把查找参数原样描述出来，用于报错与结果文案</summary>
        public static string Describe(JsonValue spec)
        {
            if (spec == null || spec.IsNull)
            {
                return "<null>";
            }

            return spec.IsString ? $"'{spec.AsString()}'" : spec.ToJson(false);
        }

        /// <summary>节点信息（不含 children，由调用方按需补）。含可回喂字段：instanceId / selectPath / siblingIndex / parentPath。</summary>
        public static JsonValue NodeToJson(Transform node)
        {
            return NodeToJson(node, true);
        }

        /// <summary>
        /// 节点信息，可裁剪 <c>components</c>（IMPR-018）。
        /// <para>
        /// <c>includeComponents=false</c> 时 <c>components</c> 里只留 <c>Transform</c> / <c>RectTransform</c>——
        /// 位置/尺寸/锚点是判断"点哪里"的必要信息，而 <c>Button</c> / <c>Image</c> / 业务脚本这些
        /// 在遍历上千个节点的快照里只是噪音：<c>limit=400</c> 的树在 UI 工程上一次能回 730 KB，
        /// 全靠每个节点那份完整组件名数组。
        /// </para>
        /// </summary>
        public static JsonValue NodeToJson(Transform node, bool includeComponents)
        {
            var item = JsonValue.NewObject();
            item.Set("name", JsonValue.Of(node.name));
            item.Set("path", JsonValue.Of(SceneQuery.BuildPath(node)));
            // 可回喂：把本节点唯一选择器直接给出，避免同名兄弟不可回喂（BUG-3）
            item.Set("selectPath", JsonValue.Of(SceneQuery.SelectPath(node)));
            item.Set("instanceId", JsonValue.Of(node.GetInstanceID()));
            item.Set("siblingIndex", JsonValue.Of(node.GetSiblingIndex()));
            item.Set("parentPath", node.parent != null
                ? JsonValue.Of(SceneQuery.BuildPath(node.parent))
                : JsonValue.Null);
            item.Set("activeSelf", JsonValue.Of(node.gameObject.activeSelf));
            item.Set("activeInHierarchy", JsonValue.Of(node.gameObject.activeInHierarchy));
            // GameObject 自身属性（不在任何组件上，读侧与 gameobject.setProperty 的写侧对称）
            item.Set("layer", JsonValue.Of(node.gameObject.layer));
            item.Set("layerName", JsonValue.Of(LayerMask.LayerToName(node.gameObject.layer)));
            item.Set("tag", JsonValue.Of(node.gameObject.tag));
            item.Set("isStatic", JsonValue.Of(node.gameObject.isStatic));

            var components = JsonValue.NewArray();
            foreach (var component in node.GetComponents<Component>())
            {
                if (component == null)
                {
                    continue;
                }

                // 裁剪模式下只保留 Transform / RectTransform（RectTransform 是 Transform 的子类，
                // 一次 is 判断同时覆盖 UI 与非 UI 节点）
                if (!includeComponents && !(component is Transform))
                {
                    continue;
                }

                components.Add(JsonValue.Of(component.GetType().Name));
            }

            item.Set("components", components);
            // 固定携带：调用方据此知道"这个数组是完整的还是只剩 Transform"，不必靠猜
            item.Set("componentsComplete", JsonValue.Of(includeComponents));
            return item;
        }

        public static string DescribeComponents(Transform node)
        {
            var names = new List<string>();
            foreach (var component in node.GetComponents<Component>())
            {
                if (component != null)
                {
                    names.Add(component.GetType().Name);
                }
            }

            return names.Count > 0 ? string.Join("、", names.ToArray()) : "无组件";
        }

        public static JsonValue NewResult(bool found)
        {
            var result = JsonValue.NewObject();
            result.Set("found", JsonValue.Of(found));
            return result;
        }

        /// <summary>在结果里附上当前已加载场景的路径与名称（C4 场景诊断；多场景时取第一个）</summary>
        public static void AttachScene(JsonValue data)
        {
            var paths = SceneQuery.LoadedScenePaths();
            if (paths.Count > 0)
            {
                var path = paths[0];
                data.Set("scenePath", JsonValue.Of(path));
                data.Set("sceneName",
                    JsonValue.Of(string.IsNullOrEmpty(path)
                        ? "(未保存场景)"
                        : System.IO.Path.GetFileNameWithoutExtension(path)));
            }
            else
            {
                data.Set("scenePath", JsonValue.Null);
                data.Set("sceneName", JsonValue.Null);
            }
        }
    }
}
