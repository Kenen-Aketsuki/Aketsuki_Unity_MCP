using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CivilRev.CommandSystem.Editor
{
    /// <summary>
    /// 运行时层级快照：遍历所有已加载场景的整棵树，包含**运行时 Instantiate 出来的对象**。
    ///
    /// <para>
    /// 与编辑期 <c>scene.hierarchy</c> 的区别：那个读的是场景资产（EditorSceneManager），
    /// 看不到运行时生成的东西；本指令走 <see cref="SceneQuery.SceneRoots"/>（底层是
    /// <c>Object.FindObjectsOfType</c>），因此动态实例、对象池里的隐藏节点、
    /// <c>DontDestroyOnLoad</c> 上的全局对象都在视野里。
    /// </para>
    ///
    /// <para>
    /// <b>为什么要有 limit：</b>运行时的对象池 + 特效 + 飘字可能瞬间上千个节点，
    /// 全量序列化出来能撑到几 MB，直接把响应体顶掉（HTTP 上限 4 MB）。
    /// 默认 400 个节点封顶，并在结果里明确 <c>truncated</c>，需要更多就显式调大。
    /// </para>
    ///
    /// <para>
    /// 参数：root（从某个节点开始，默认全部）、name（按名字子串过滤，给了就返回**扁平命中列表**）、
    /// depth（0=不限）、includeInactive（默认 true）、limit（默认 400）
    /// </para>
    /// </summary>
    [Command("runtime.hierarchy",
        Description = "运行时层级快照：遍历已加载场景（含运行时实例与 DontDestroyOnLoad）",
        Usage = "root: target, name: string, depth: int, includeInactive: bool, limit: int",
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

            if (rootSpec != null && !rootSpec.IsNull)
            {
                var root = SceneQuery.ResolveTarget(rootSpec);
                if (root == null)
                {
                    return CommandResult.Fail(
                        $"找不到 root：{SceneQueryCommandsUtil.Describe(rootSpec)}" +
                        "（运行时对象要用运行时生成的路径/名字定位，可先不带参数调一次 runtime.hierarchy 看全貌）");
                }

                var node = string.IsNullOrEmpty(nameFilter) ? BuildNode(root, 0) : null;
                var single = JsonValue.NewObject();
                single.Set("name", JsonValue.Of(root.gameObject.scene.name));
                single.Set("path", JsonValue.Of(root.gameObject.scene.path));
                single.Set("dontDestroyOnLoad", JsonValue.Of(IsDontDestroyOnLoad(root.gameObject.scene)));
                single.Set("root", node != null ? node : JsonValue.Null);

                data.Set("sceneCount", JsonValue.Of(1));
                data.Set("nodeCount", JsonValue.Of(_visited));
                data.Set("truncated", JsonValue.Of(_truncated));
                data.Set("scenes", JsonValue.FromArray(new[] { single }));
                return CommandResult.Ok(
                    $"运行时层级：从 {SceneQuery.BuildPath(root)} 起 {_visited} 个节点{TruncatedHint()}", data);
            }

            var scenes = CollectScenes();
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

                var item = JsonValue.NewObject();
                item.Set("name", JsonValue.Of(group.Name));
                item.Set("path", JsonValue.Of(group.Path));
                item.Set("dontDestroyOnLoad", JsonValue.Of(group.DontDestroyOnLoad));
                item.Set("rootCount", JsonValue.Of(group.Roots.Count));
                item.Set("roots", roots);
                groups.Add(item);
            }

            data.Set("sceneCount", JsonValue.Of(scenes.Count));
            data.Set("nodeCount", JsonValue.Of(_visited));
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
                    WalkMatches(group.Roots[j], nameFilter, group, matches);
                }
            }

            data.Set("mode", JsonValue.Of("match"));
            data.Set("name", JsonValue.Of(nameFilter));
            // _visited 是遍历节点数，命中数必须单独计——混用会报出"命中 19 个"却只有 1 条记录（实测踩过）
            data.Set("matchCount", JsonValue.Of(_matched));
            data.Set("truncated", JsonValue.Of(_truncated));
            data.Set("matches", matches);

            if (_matched == 0)
            {
                return CommandResult.Ok($"运行时层级里没有名字含 '{nameFilter}' 的节点{TruncatedHint()}", data);
            }

            return CommandResult.Ok($"运行时层级里命中 {_matched} 个名字含 '{nameFilter}' 的节点{TruncatedHint()}", data);
        }

        private void WalkMatches(Transform node, string nameFilter, SceneGroup group, JsonValue matches)
        {
            if (node == null || _visited >= _limit)
            {
                _truncated = _truncated || node != null;
                return;
            }

            _visited++;

            if (node.name.IndexOf(nameFilter, System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var item = SceneQueryCommandsUtil.NodeToJson(node);
                item.Set("scene", JsonValue.Of(group.Name));
                matches.Add(item);
                _matched++;
            }

            for (var i = 0; i < node.childCount; i++)
            {
                WalkMatches(node.GetChild(i), nameFilter, group, matches);
            }
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
            var item = SceneQueryCommandsUtil.NodeToJson(node);

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

        #endregion

        #region 场景归组

        private sealed class SceneGroup
        {
            public string Name;
            public string Path;
            public bool DontDestroyOnLoad;
            public readonly List<Transform> Roots = new List<Transform>();
        }

        /// <summary>
        /// 按场景归组所有根节点；DontDestroyOnLoad 单独成组（其 scene.name 就是 "DontDestroyOnLoad"）。
        /// </summary>
        private List<SceneGroup> CollectScenes()
        {
            var groups = new List<SceneGroup>();
            var byHandle = new Dictionary<int, SceneGroup>();

            foreach (var root in SceneQuery.SceneRoots())
            {
                if (root == null)
                {
                    continue;
                }

                if (!_includeInactive && !root.gameObject.activeInHierarchy)
                {
                    continue;
                }

                var scene = root.gameObject.scene;
                var handle = scene.handle;
                SceneGroup group;
                if (!byHandle.TryGetValue(handle, out group))
                {
                    group = new SceneGroup
                    {
                        Name = IsDontDestroyOnLoad(scene) ? "DontDestroyOnLoad" : scene.name,
                        Path = scene.path,
                        DontDestroyOnLoad = IsDontDestroyOnLoad(scene)
                    };
                    byHandle[handle] = group;
                    groups.Add(group);
                }

                group.Roots.Add(root);
            }

            return groups;
        }

        private static bool IsDontDestroyOnLoad(Scene scene)
        {
            return scene.IsValid() && scene.name == "DontDestroyOnLoad";
        }

        #endregion

        private string TruncatedHint()
        {
            return _truncated ? $"（已按 limit={_limit} 截断，需要更多请调大 limit 或加 name 过滤）" : string.Empty;
        }
    }
}
