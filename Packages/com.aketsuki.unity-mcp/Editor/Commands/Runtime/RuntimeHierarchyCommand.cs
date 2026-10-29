using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 运行时层级快照：遍历所有已加载场景的整棵树，包含**运行时 Instantiate 出来的对象**。
    ///
    /// <para>
    /// 与编辑期 <c>scene.hierarchy</c> 的区别：那个读的是场景资产（EditorSceneManager），
    /// 看不到运行时生成的东西；本指令走 <c>SceneManager</c> 已加载场景列表 + 全场景根节点，
    /// 因此动态实例、对象池里的隐藏节点、<c>DontDestroyOnLoad</c> 上的全局对象都在视野里。
    /// </para>
    ///
    /// <para>
    /// <b>多场景叠加是常态，不是例外：</b>「常驻引导场景 + 关卡 + UI」各会有一份
    /// <c>UIRoot</c>／<c>Canvas</c>／<c>BtnStart</c>。因此结果**按场景分组**，每组带
    /// <c>label</c>（同名场景会是 <c>Level1#1</c>）、<c>selector</c>、<c>handle</c>、<c>buildIndex</c>、
    /// <c>active</c>；每个节点另给 <c>qualifiedPath</c>（<c>[scene:Level1]UIRoot/BtnStart</c>），
    /// 可直接回喂给 <c>runtime.click</c> 等指令，不需要自己拼前缀。
    /// </para>
    ///
    /// <para>
    /// <b>为什么要有 limit：</b>运行时的对象池 + 特效 + 飘字可能瞬间上千个节点，
    /// 全量序列化出来能撑到几 MB，直接把响应体顶掉（HTTP 上限 4 MB）。
    /// 默认 400 封顶，并在结果里明确 <c>truncated</c>，需要更多就显式调大。
    /// </para>
    ///
    /// <para>
    /// <b>limit 在两种模式下量的是不同的东西（BUG-010）：</b>
    /// <list type="bullet">
    /// <item>树模式（不给 <c>name</c>）：limit 是<b>序列化节点数</b>预算，走满即截断，因为要防的是响应体爆炸。</item>
    /// <item>命中模式（给了 <c>name</c>）：limit 是<b>命中条数</b>上限，遍历不限量。
    /// 此前两种模式共用「遍历节点数」预算，<c>limit=20</c> 等于「只看前 20 个节点」，
    /// 命中区在后面就必然空手而归，还会让调用方误判成「场景里没有这个节点」。</item>
    /// </list>
    /// 两种模式都在 data 里给 <c>visited</c>（实际遍历节点数）与 <c>limitAppliesTo</c>，便于分辨。
    /// </para>
    ///
    /// <para>
    /// 参数：root（从某个节点开始，支持 <c>[scene:X]</c> 前缀；默认全部）、
    /// scene（只看某个场景，写法同 <c>[scene:...]</c> 里的选择器）、name（按名字子串过滤，给了就返回**扁平命中列表**）、
    /// depth（0=不限）、includeInactive（默认 true）、limit（默认 400；树模式=节点数，name 模式=命中数）
    /// </para>
    /// </summary>
    [Command("runtime.hierarchy",
        Description = "运行时层级快照：按场景分组遍历已加载场景（含运行时实例与 DontDestroyOnLoad）",
        Usage = "root: string|object, scene: string, name: string, depth: int, includeInactive: bool, limit: int",
        EditorOnly = true, Runtime = true)]
    public sealed class RuntimeHierarchyCommand : ICommand
    {
        private const int DefaultLimit = 400;

        private int _limit = DefaultLimit;
        private int _maxDepth;
        private bool _includeInactive = true;
        private int _visited;
        private int _matched;
        private bool _truncated;

        public CommandResult Execute(CommandContext context)
        {
            _limit = Mathf.Max(1, context.Args.GetInt("limit", DefaultLimit));
            _maxDepth = Mathf.Max(0, context.Args.GetInt("depth", 0));
            _includeInactive = context.Args.GetBool("includeInactive", true);
            _visited = 0;
            _matched = 0;
            _truncated = false;

            var nameFilter = context.Args.GetString("name", null);
            var rootSpec = context.Args.Get("root");

            var data = JsonValue.NewObject();
            data.Set("playMode", JsonValue.Of(EditorPlayState.IsPlayingNow));
            data.Set("frameCount", JsonValue.Of(Time.frameCount));
            data.Set("time", JsonValue.Of(Mathf.Round(Time.time * 1000f) / 1000f));
            data.Set("activeScene", JsonValue.Of(SceneScope.Label(SceneManager.GetActiveScene())));

            if (rootSpec != null && !rootSpec.IsNull)
            {
                var root = RuntimeSupport.ResolveTarget(rootSpec, data, out var rootError);
                if (root == null)
                {
                    return CommandResult.Fail(rootError, data);
                }

                var node = string.IsNullOrEmpty(nameFilter) ? BuildNode(root, 0) : null;
                var single = JsonValue.NewObject();
                single.Set("label", JsonValue.Of(SceneScope.Label(root.gameObject.scene)));
                single.Set("selector", JsonValue.Of(SceneScope.SelectorOf(root.gameObject.scene)));
                single.Set("name", JsonValue.Of(root.gameObject.scene.name));
                single.Set("path", JsonValue.Of(root.gameObject.scene.path));
                single.Set("handle", JsonValue.Of(root.gameObject.scene.handle));
                single.Set("active", JsonValue.Of(
                    root.gameObject.scene.handle == SceneManager.GetActiveScene().handle));
                single.Set("dontDestroyOnLoad", JsonValue.Of(SceneScope.IsDontDestroyOnLoad(root.gameObject.scene)));
                single.Set("root", node != null ? node : JsonValue.Null);

                data.Set("sceneCount", JsonValue.Of(1));
                data.Set("nodeCount", JsonValue.Of(_visited));
                data.Set("visited", JsonValue.Of(_visited));
                data.Set("limitAppliesTo", JsonValue.Of("nodes"));
                data.Set("truncated", JsonValue.Of(_truncated));
                data.Set("scenes", JsonValue.FromArray(new[] { single }));
                return CommandResult.Ok(
                    $"运行时层级：从 {RuntimeSupport.Qualified(root)} 起 {_visited} 个节点{TruncatedHint()}", data);
            }

            var scenes = CollectScenes(context.Args.GetString("scene", null), out var sceneError);
            if (sceneError != null)
            {
                return CommandResult.Fail(sceneError, data);
            }

            if (!string.IsNullOrEmpty(nameFilter))
            {
                return MatchMode(scenes, nameFilter, data);
            }

            var groups = JsonValue.NewArray();
            var total = 0;
            for (var i = 0; i < scenes.Count; i++)
            {
                var group = scenes[i];
                var roots = JsonValue.NewArray();
                for (var j = 0; j < group.Roots.Count; j++)
                {
                    var node = BuildNode(group.Roots[j], 0);
                    if (node != null)
                    {
                        roots.Add(node);
                        total++;
                    }
                }

                var item = group.ToJson();
                item.Set("roots", roots);
                groups.Add(item);
            }

            data.Set("sceneCount", JsonValue.Of(scenes.Count));
            data.Set("nodeCount", JsonValue.Of(_visited));
            data.Set("visited", JsonValue.Of(_visited));
            data.Set("limitAppliesTo", JsonValue.Of("nodes"));
            data.Set("truncated", JsonValue.Of(_truncated));
            data.Set("scenes", groups);

            return CommandResult.Ok(
                $"运行时层级：{scenes.Count} 个场景 / {_visited} 个节点（{total} 个根）{TruncatedHint()}", data);
        }

        #region 扁平命中模式

        private CommandResult MatchMode(List<SceneGroup> scenes, string nameFilter, JsonValue data)
        {
            var matches = JsonValue.NewArray();
            for (var i = 0; i < scenes.Count; i++)
            {
                var group = scenes[i];
                for (var j = 0; j < group.Roots.Count; j++)
                {
                    if (_matched >= _limit)
                    {
                        // 还有根没走：后面可能压着命中，明确记为截断（BUG-010）
                        _truncated = true;
                        break;
                    }

                    WalkMatches(group.Roots[j], nameFilter, group, matches);
                }
            }

            data.Set("mode", JsonValue.Of("match"));
            data.Set("name", JsonValue.Of(nameFilter));
            // _visited 是遍历节点数，命中数必须单独计——混用会报出"命中 19 个"却只有 1 条记录（实测踩过）
            data.Set("matchCount", JsonValue.Of(_matched));
            data.Set("truncated", JsonValue.Of(_truncated));
            data.Set("visited", JsonValue.Of(_visited));
            data.Set("limitAppliesTo", JsonValue.Of("matches"));
            data.Set("matches", matches);

            if (_matched == 0)
            {
                // 遍历是不限量的，所以这里真的是"没有"，不是"没看到"——
                // 但要把遍历规模报出来，调用方才知道这个结论覆盖了多少节点（BUG-010）
                return CommandResult.Ok(
                    $"运行时层级里没有名字含 '{nameFilter}' 的节点（已遍历 {_visited} 个节点，未截断）", data);
            }

            var sceneHint = scenes.Count > 1 ? $"（分布在 {scenes.Count} 个场景，同名时请用 matches 里的 qualifiedPath 精确指定）" : string.Empty;
            return CommandResult.Ok(
                $"运行时层级里命中 {_matched} 个名字含 '{nameFilter}' 的节点{sceneHint}{MatchTruncatedHint()}", data);
        }

        /// <summary>
        /// 命中模式的遍历：<c>limit</c> 只砍<b>命中条数</b>，遍历本身不限量（BUG-010）。
        /// <para>
        /// 命中满了之后要立刻整体收手——否则每个兄弟节点都会被叫进来一次再空转返回，
        /// 一颗几千节点的树白白多走几遍。
        /// </para>
        /// </summary>
        private void WalkMatches(Transform node, string nameFilter, SceneGroup group, JsonValue matches)
        {
            if (node == null || _matched >= _limit)
            {
                return;
            }

            _visited++;

            if (node.name.IndexOf(nameFilter, System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (_matched >= _limit)
                {
                    // 命中已经够了：记一次截断，不再往下走
                    _truncated = true;
                    return;
                }

                var item = NodeJson(node);
                item.Set("scene", JsonValue.Of(group.Label));
                item.Set("sceneHandle", JsonValue.Of(group.Scene.handle));
                matches.Add(item);
                _matched++;
            }

            for (var i = 0; i < node.childCount; i++)
            {
                if (_matched >= _limit)
                {
                    // 子树里可能还有命中，但已经不收了 → 明确记为截断，别让调用方以为"只有这些"
                    _truncated = _truncated || node.childCount > 0;
                    return;
                }

                WalkMatches(node.GetChild(i), nameFilter, group, matches);
            }
        }

        private string MatchTruncatedHint()
        {
            return _truncated
                ? $"（命中已达 limit={_limit}，可能还有未列出的节点；调大 limit，或用 root / scene 收窄范围）"
                : string.Empty;
        }

        #endregion

        #region 树模式

        private JsonValue BuildNode(Transform node, int depth)
        {
            if (node == null)
            {
                return null;
            }

            if (_visited >= _limit)
            {
                _truncated = true;
                return null;
            }

            _visited++;
            var item = NodeJson(node);

            var stopDepth = _maxDepth > 0 && depth >= _maxDepth;
            if (stopDepth || node.childCount == 0)
            {
                return item;
            }

            var children = JsonValue.NewArray();
            for (var i = 0; i < node.childCount; i++)
            {
                var child = node.GetChild(i);
                if (!_includeInactive && !child.gameObject.activeInHierarchy)
                {
                    continue;
                }

                var childJson = BuildNode(child, depth + 1);
                if (childJson != null)
                {
                    children.Add(childJson);
                }
            }

            if (children.Count > 0)
            {
                item.Set("children", children);
            }

            return item;
        }

        /// <summary>
        /// 节点信息：多场景叠加时 <c>selectPath</c> 只在单个场景内唯一，
        /// 跨场景必须靠 <c>qualifiedPath</c>（带 <c>[scene:...]</c> 前缀）。
        /// </summary>
        private static JsonValue NodeJson(Transform node)
        {
            return RuntimeSupport.NodeJson(node);
        }

        #endregion

        #region 场景归组

        private sealed class SceneGroup
        {
            public Scene Scene;
            public string Label;
            public string Selector;
            public readonly List<Transform> Roots = new List<Transform>();

            public JsonValue ToJson()
            {
                var item = JsonValue.NewObject();
                item.Set("label", JsonValue.Of(Label));
                item.Set("selector", JsonValue.Of(Selector));
                item.Set("name", JsonValue.Of(Scene.name ?? string.Empty));
                item.Set("path", JsonValue.Of(Scene.path ?? string.Empty));
                item.Set("handle", JsonValue.Of(Scene.handle));
                item.Set("buildIndex", JsonValue.Of(Scene.buildIndex));
                item.Set("active", JsonValue.Of(Scene.handle == SceneManager.GetActiveScene().handle));
                item.Set("dontDestroyOnLoad", JsonValue.Of(SceneScope.IsDontDestroyOnLoad(Scene)));
                item.Set("rootCount", JsonValue.Of(Roots.Count));
                return item;
            }
        }

        /// <summary>
        /// 按**加载顺序**归组所有根节点；DontDestroyOnLoad 单独成组排在最后。
        /// <para>
        /// 顺序取 <see cref="SceneManager"/> 而不是 <c>FindObjectsOfType</c> 的返回序——
        /// 后者在运行时不保证稳定，同一个场景两帧之间可能换个位置，结果不可比对。
        /// </para>
        /// </summary>
        private List<SceneGroup> CollectScenes(string sceneSelector, out string error)
        {
            error = null;

            SceneScope scope = null;
            if (!string.IsNullOrEmpty(sceneSelector))
            {
                if (!SceneScope.TryParse(sceneSelector, out scope, out error))
                {
                    return new List<SceneGroup>();
                }

                if (scope.Error != null)
                {
                    error = scope.Error;
                    return new List<SceneGroup>();
                }
            }

            var groups = new List<SceneGroup>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }

                if (scope != null && !scope.Accepts(scene))
                {
                    continue;
                }

                var group = NewGroup(scene);
                foreach (var root in SceneScope.RootsOf(scene))
                {
                    if (root == null)
                    {
                        continue;
                    }

                    if (!_includeInactive && !root.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    group.Roots.Add(root);
                }

                groups.Add(group);
            }

            var dontDestroy = SceneScope.DontDestroyOnLoadScene();
            if (dontDestroy.IsValid() && (scope == null || scope.Accepts(dontDestroy)))
            {
                var group = NewGroup(dontDestroy);
                foreach (var root in SceneScope.RootsOf(dontDestroy))
                {
                    if (root == null)
                    {
                        continue;
                    }

                    if (!_includeInactive && !root.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    group.Roots.Add(root);
                }

                if (group.Roots.Count > 0)
                {
                    groups.Add(group);
                }
            }

            if (groups.Count == 0)
            {
                error = $"没有可枚举的运行时场景（当前：{SceneScope.DescribeSceneList(SceneScope.AllLoadedScenes())}）" +
                        "；Play 模式启动后才有运行时场景";
            }

            return groups;
        }

        private static SceneGroup NewGroup(Scene scene)
        {
            return new SceneGroup
            {
                Scene = scene,
                Label = SceneScope.Label(scene),
                Selector = SceneScope.SelectorOf(scene)
            };
        }

        #endregion

        private string TruncatedHint()
        {
            return _truncated ? $"（已按 limit={_limit} 截断，需要更多请调大 limit 或加 name 过滤）" : string.Empty;
        }
    }
}
