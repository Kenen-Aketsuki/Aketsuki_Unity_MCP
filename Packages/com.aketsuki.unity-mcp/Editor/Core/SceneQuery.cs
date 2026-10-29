using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// 场景节点查找工具：按名字 / 层级路径 / 组件类型在场景里定位节点
    /// <para>
    /// 之所以不用 <c>GameObject.Find</c>，是因为它有三个坑：
    /// ① 找不到 <c>inactive</c> 节点；② 只按名字，不支持路径；③ 只覆盖部分已加载场景。
    /// 本类遍历所有已加载场景的整棵树（含 DontDestroyOnLoad），按名字 / 后缀路径 / 组件类型匹配，
    /// 命中多个时按「激活优先、层级由浅到深、同层按名字」排序，结果稳定可预期。
    /// </para>
    /// <para>只依赖 <c>UnityEngine.CoreModule</c>，不涉及 UI 程序集，任何指令都能用。</para>
    /// </summary>
    public static class SceneQuery
    {
        /// <summary>
        /// 解析查找目标：支持节点名、层级路径、按组件类型三种写法（字符串或 JSON 对象）
        /// <para>
        /// <list type="bullet">
        /// <item>节点名：<c>"RoundMenu"</c></item>
        /// <item>层级路径（后缀匹配）：<c>"UIRoot/HUD/Canvas"</c></item>
        /// <item>按组件类型：<c>"type:Canvas"</c>、<c>"t:Canvas"</c>（可带 <c>#序号</c>）、<c>"@canvas"</c></item>
        /// <item>对象形式：<c>{ "type": "Canvas", "index": 0 }</c> / <c>{ "path": "..." }</c> / <c>{ "name": "..." }</c></item>
        /// </list>
        /// 都找不到时返回 null，由调用方决定是否抛错（这样调用方能给出更贴切的报错文案）。
        /// </para>
        /// </summary>
        public static Transform ResolveTarget(JsonValue spec)
        {
            if (spec == null || spec.IsNull)
            {
                return null;
            }

            // 场景作用域：[scene:Level1]UIRoot/BtnStart 或 { "scene": "Level1", "path": "..." }
            // 没写作用域时 scope 为 null，行为与改造前完全一致（编辑指令不受影响）
            SceneScope scope;
            string textSpec;
            string scopeError;
            if (!SceneScope.TryExtract(spec, out scope, out textSpec, out scopeError))
            {
                return null; // 作用域写错的具体原因由 TryResolveScoped / SceneScope.Error 给出
            }

            if (spec.IsObject)
            {
                // 实例 ID 消歧（来自 gameobject.find 的 instanceId 字段，同一 Unity 会话内可回喂）
                if (spec.ContainsKey("instanceId"))
                {
                    return FindById((int)spec["instanceId"].AsFloat(0f), scope);
                }

                var typeName = spec["type"].AsString();
                if (!string.IsNullOrEmpty(typeName))
                {
                    ParseIndex(spec["index"], out var index, out var last, out _);
                    var matches = FindAllByType(typeName, scope);
                    return ResolveByIndex(matches, index, last);
                }

                var path = spec["path"].AsString();
                if (!string.IsNullOrEmpty(path))
                {
                    return Find(path, scope);
                }

                var name = spec["name"].AsString();
                return string.IsNullOrEmpty(name) ? null : Find(name, scope);
            }

            return ResolveText(textSpec, scope);
        }

        /// <summary>字符串版查找目标解析</summary>
        public static Transform ResolveTarget(string spec)
        {
            if (string.IsNullOrEmpty(spec))
            {
                return null;
            }

            SceneScope scope;
            string textSpec;
            string scopeError;
            if (!SceneScope.TryExtract(JsonValue.Of(spec), out scope, out textSpec, out scopeError))
            {
                return null;
            }

            return ResolveText(textSpec, scope);
        }

        /// <summary>字符串选择器的主体解析（作用域已剥离）</summary>
        private static Transform ResolveText(string spec, SceneScope scope)
        {
            var text = (spec ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return null;
            }

            // id:12345 —— 按实例 ID 精确消歧（find 输出的 instanceId 可直接回喂）
            if (text.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
            {
                return FindById((int)ParseNumber(text.Substring(3), 0f), scope);
            }

            // type:Canvas / t:Canvas，可带 #序号 或 #last
            var colon = text.IndexOf(':');
            if (colon > 0 && (text.StartsWith("type:", StringComparison.OrdinalIgnoreCase)
                              || text.StartsWith("t:", StringComparison.OrdinalIgnoreCase)))
            {
                ParseTypeSelector(text.Substring(colon + 1).Trim(), out var body, out var index, out var last, out _);
                var matches = FindAllByType(body, scope);
                return ResolveByIndex(matches, index, last);
            }

            // @canvas / @root：按类型找画布，等价于 type:Canvas
            if (text.Equals("@canvas", StringComparison.OrdinalIgnoreCase)
                || text.Equals("@root", StringComparison.OrdinalIgnoreCase))
            {
                return FindByType("Canvas", 0, scope);
            }

            return Find(text, scope);
        }

        /// <summary>
        /// 按实例 ID 在全场景里精确定位节点（同一 Unity 会话内有效）。用于把 <c>gameobject.find</c>
        /// 的结果原样回喂，彻底解决同名兄弟不可回喂的问题（BUG-3）。
        /// </summary>
        private static Transform FindById(int id, SceneScope scope)
        {
            if (id == 0)
            {
                return null;
            }

            foreach (var root in Roots(scope))
            {
                if (root.GetInstanceID() == id)
                {
                    return root;
                }

                Transform hit = null;
                Walk(root, 0, (node, depth) =>
                {
                    if (node.GetInstanceID() == id)
                    {
                        hit = node;
                    }
                });
                if (hit != null)
                {
                    return hit;
                }
            }

            return null;
        }

        /// <summary>
        /// 统一解析「按名字 / 路径 / 类型 / 实例ID」的全部命中，支持对象形式与字符串形式，
        /// 且字符串形式的 <c>type:X#N</c> 会正确剥离 <c>#N</c>（全量返回不看序号）。
        /// <see cref="FindAll(string)"/> 与 <see cref="FindAllByType"/> 的调度入口，也是
        /// <c>gameobject.find</c> 两条分支的统一实现（修 BUG-5 两条分支语法不一致）。
        /// </summary>
        public static List<Transform> FindAll(JsonValue spec)
        {
            if (spec == null || spec.IsNull)
            {
                return new List<Transform>();
            }

            // 场景作用域（对象形式的 scene 字段 / 字符串形式的 [scene:...] 前缀）
            SceneScope scope;
            string textSpec;
            string scopeError;
            if (!SceneScope.TryExtract(spec, out scope, out textSpec, out scopeError))
            {
                return new List<Transform>();
            }

            if (spec.IsObject)
            {
                if (spec.ContainsKey("instanceId"))
                {
                    var hit = FindById((int)spec["instanceId"].AsFloat(0f), scope);
                    return hit != null ? new List<Transform> { hit } : new List<Transform>();
                }

                var typeName = spec["type"].AsString();
                if (!string.IsNullOrEmpty(typeName))
                {
                    return FindAllByType(typeName, scope);
                }

                var path = spec["path"].AsString() ?? spec["name"].AsString();
                return FindAll(path ?? string.Empty, scope);
            }

            var text = (textSpec ?? string.Empty).Trim();
            var colon = text.IndexOf(':');
            if (text.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
            {
                var hit = FindById((int)ParseNumber(text.Substring(3), 0f), scope);
                return hit != null ? new List<Transform> { hit } : new List<Transform>();
            }

            if (colon > 0 && (text.StartsWith("type:", StringComparison.OrdinalIgnoreCase)
                              || text.StartsWith("t:", StringComparison.OrdinalIgnoreCase)))
            {
                ParseTypeSelector(text.Substring(colon + 1).Trim(), out var body, out _, out _, out _);
                return FindAllByType(body, scope);
            }

            if (text.Equals("@canvas", StringComparison.OrdinalIgnoreCase)
                || text.Equals("@root", StringComparison.OrdinalIgnoreCase))
            {
                return FindAllByType("Canvas", scope);
            }

            // 全量命中：这里以前只取 Find 的单个最优结果，导致 all:true / 歧义检测对纯路径失效
            // （多场景叠加时表现为"明明两处都有，却只报一个"）
            return FindAll(text, scope);
        }

        /// <summary>
        /// 在指定子树（如 Prefab 内容）里按名字 / 层级路径查找全部命中，规则与 <see cref="FindAll"/> 一致
        /// （含 <c>#序号</c> 消歧）。供 Prefab 编辑类指令做唯一性校验，避免改错节点。
        /// </summary>
        public static List<Transform> FindInSubtree(Transform root, string nameOrPath)
        {
            var list = new List<Transform>();
            var spec = (nameOrPath ?? string.Empty).Trim().Trim('/');
            if (spec.Length == 0)
            {
                if (root != null)
                {
                    list.Add(root);
                }

                return list;
            }

            var segments = spec.Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                segments[i] = segments[i].Trim();
            }

            if (root != null)
            {
                Walk(root, 0, (node, depth) =>
                {
                    if (MatchPath(node, segments))
                    {
                        list.Add(node);
                    }
                });
            }

            return list;
        }

        /// <summary>
        /// 严格解析单个目标：歧义（多个命中）或 <c>type:X#N</c> 越界时返回 false 并给出可读的错误
        /// （附带全部候选的路径/激活态/组件与当前已加载场景），由调用方抛出。
        /// <para>这是 BUG-1 / BUG-2 的核心修复：再也不静默写错节点。</para>
        /// </summary>
        /// <summary>字符串版（见 <see cref="TryResolveUnique(JsonValue, out Transform, out string)"/>）</summary>
        public static bool TryResolveUnique(string spec, out Transform result, out string error)
        {
            return TryResolveUnique(JsonValue.Of(spec ?? string.Empty), out result, out error);
        }

        public static bool TryResolveUnique(JsonValue spec, out Transform result, out string error)
        {
            result = null;
            error = null;

            if (spec == null || spec.IsNull)
            {
                error = "target 为空";
                return false;
            }

            // 场景作用域（多场景叠加时用来把范围收敛到某一个场景）
            SceneScope scope;
            string textSpec;
            string scopeError;
            if (!SceneScope.TryExtract(spec, out scope, out textSpec, out scopeError))
            {
                error = scopeError;
                return false;
            }

            if (scope != null && scope.Error != null)
            {
                error = scope.Error;
                return false;
            }

            if (spec.IsObject)
            {
                if (spec.ContainsKey("instanceId"))
                {
                    var hit = FindById((int)spec["instanceId"].AsFloat(0f), scope);
                    if (hit == null)
                    {
                        error = $"instanceId={spec["instanceId"].AsFloat(0f)} 没有对应的场景节点（可能已销毁或跨会话失效）";
                    }
                    else
                    {
                        result = hit;
                    }

                    return hit != null;
                }

                var typeName = spec["type"].AsString();
                if (!string.IsNullOrEmpty(typeName))
                {
                    ParseIndex(spec["index"], out var index, out var last, out var hasIndex);
                    var matches = FindAllByType(typeName, scope);
                    if (hasIndex)
                    {
                        return ResolveUniqueByIndex(typeName, matches, index, last, out result, out error);
                    }

                    return UniqueFromList(matches, "type:" + typeName, out result, out error);
                }

                var path = spec["path"].AsString() ?? spec["name"].AsString();
                return UniqueFromList(FindAll(path ?? string.Empty, scope), DescribeSpec(spec), out result, out error);
            }

            var text = (textSpec ?? string.Empty).Trim();
            if (text.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
            {
                var hit = FindById((int)ParseNumber(text.Substring(3), 0f), scope);
                if (hit == null)
                {
                    error = $"{text} 没有对应的场景节点（可能已销毁或跨会话失效）";
                }
                else
                {
                    result = hit;
                }

                return hit != null;
            }

            var colon = text.IndexOf(':');
            if (colon > 0 && (text.StartsWith("type:", StringComparison.OrdinalIgnoreCase)
                              || text.StartsWith("t:", StringComparison.OrdinalIgnoreCase)))
            {
                ParseTypeSelector(text.Substring(colon + 1).Trim(), out var body, out var index, out var last, out var hasIndex);
                var matches = FindAllByType(body, scope);
                if (hasIndex)
                {
                    return ResolveUniqueByIndex(body, matches, index, last, out result, out error);
                }

                return UniqueFromList(matches, text, out result, out error);
            }

            if (text.Equals("@canvas", StringComparison.OrdinalIgnoreCase)
                || text.Equals("@root", StringComparison.OrdinalIgnoreCase))
            {
                return UniqueFromList(FindAllByType("Canvas", scope), text, out result, out error);
            }

            return UniqueFromList(FindAll(text, scope), text, out result, out error);
        }

        /// <summary>从命中列表里取第 index 个（#last 取最后一个），越界报错。</summary>
        private static Transform ResolveByIndex(List<Transform> matches, int index, bool last)
        {
            if (matches.Count == 0)
            {
                return null;
            }

            var i = last ? matches.Count - 1 : (index < 0 ? 0 : index);
            return i < matches.Count ? matches[i] : null;
        }

        /// <summary>命中列表 + 显式序号的严格解析（用于 TryResolveUnique 的 type 分支）</summary>
        private static bool ResolveUniqueByIndex(string describe, List<Transform> matches, int index, bool last,
            out Transform result, out string error)
        {
            result = null;
            error = null;
            if (matches.Count == 0)
            {
                error = $"找不到类型 '{describe}' 的组件（当前已加载场景：{SceneList()}）";
                return false;
            }

            var i = last ? matches.Count - 1 : index;
            if (i < 0 || i >= matches.Count)
            {
                error = $"type:{describe}#{i} 越界：共 {matches.Count} 个命中（0-based，最大 #{matches.Count - 1}，或用 #last 取最后一个）"
                    + $"（当前已加载场景：{SceneList()}）";
                return false;
            }

            result = matches[i];
            return true;
        }

        /// <summary>命中唯一性校验：0 个报错、多个报歧义清单，1 个返回。</summary>
        private static bool UniqueFromList(List<Transform> matches, string describe,
            out Transform result, out string error)
        {
            result = null;
            error = null;
            if (matches.Count == 0)
            {
                error = $"找不到节点 '{describe}'（当前已加载场景：{SceneList()}）";
                return false;
            }

            if (matches.Count > 1)
            {
                var builder = new StringBuilder();
                builder.Append($"节点 '{describe}' 命中 {matches.Count} 个，存在歧义，必须显式消歧。" +
                               "把下面任一条原样作为 target 重试（多场景叠加时同一路径在每个场景各有一份，靠 [scene:...] 点名）：\n");
                for (var i = 0; i < matches.Count; i++)
                {
                    builder.Append($"  {SceneScope.QualifiedPath(matches[i])}" +
                                   $"（activeSelf={matches[i].gameObject.activeSelf}, " +
                                   $"instanceId={matches[i].GetInstanceID()}）\n");
                }

                builder.Append($"  也可用 type:{PrimaryComponentName(matches[0])}#N 按序号选（N 从 0 起，顺序见上）\n");
                builder.Append($"（当前已加载场景：{SceneList()}）");
                error = builder.ToString();
                return false;
            }

            result = matches[0];
            return true;
        }

        /// <summary>取节点上第一个组件类型名，用于歧义清单的 type: 建议（避免只给路径）</summary>
        private static string PrimaryComponentName(Transform node)
        {
            foreach (var component in node.GetComponents<Component>())
            {
                if (component != null)
                {
                    return component.GetType().Name;
                }
            }

            return node.name;
        }

        /// <summary>把对象形式选择器描述成可读串</summary>
        private static string DescribeSpec(JsonValue spec)
        {
            var typeName = spec["type"].AsString();
            if (!string.IsNullOrEmpty(typeName))
            {
                return "type:" + typeName;
            }

            return spec["path"].AsString() ?? spec["name"].AsString() ?? "<selector>";
        }

        /// <summary>解析对象形式的 index 字段（支持数字与字符串 "last"）；hasIndex 标记是否显式给了序号</summary>
        private static void ParseIndex(JsonValue indexSpec, out int index, out bool last, out bool hasIndex)
        {
            index = 0;
            last = false;
            hasIndex = false;
            if (indexSpec == null || indexSpec.IsNull)
            {
                return;
            }

            hasIndex = true;
            if (indexSpec.IsString
                && string.Equals(indexSpec.AsString(), "last", StringComparison.OrdinalIgnoreCase))
            {
                last = true;
                return;
            }

            index = (int)indexSpec.AsFloat(0f);
        }

        /// <summary>解析 "Canvas" / "Canvas#2" / "Canvas#last" 成 (类型名, 序号, 是否 last, 是否显式给了序号)</summary>
        private static void ParseTypeSelector(string text, out string typeName, out int index, out bool last, out bool hasIndex)
        {
            typeName = text;
            index = 0;
            last = false;
            hasIndex = false;
            var hash = text.LastIndexOf('#');
            if (hash > 0 && hash < text.Length - 1)
            {
                var suffix = text.Substring(hash + 1).Trim();
                if (string.Equals(suffix, "last", StringComparison.OrdinalIgnoreCase))
                {
                    last = true;
                    hasIndex = true;
                    typeName = text.Substring(0, hash).Trim();
                }
                else if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    index = n;
                    hasIndex = true;
                    typeName = text.Substring(0, hash).Trim();
                }
            }
        }

        /// <summary>当前已加载（有效）场景的路径清单，用于报错与诊断（C4）</summary>
        public static string SceneList()
        {
            var paths = LoadedScenePaths();
            if (paths.Count == 0)
            {
                return "（无已加载场景）";
            }

            var names = new List<string>();
            foreach (var path in paths)
            {
                names.Add(string.IsNullOrEmpty(path) ? "(未保存场景)" : path);
            }

            return string.Join("、", names.ToArray());
        }

        /// <summary>当前已加载场景的路径（含未保存场景的空串），用于结果里携带 scenePath/sceneName（C4）</summary>
        public static List<string> LoadedScenePaths()
        {
            var list = new List<string>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid())
                {
                    list.Add(scene.path);
                }
            }

            return list;
        }

        /// <summary>
        /// 按节点名或层级路径查找
        /// <para>路径为后缀匹配，不必从场景根写起：<c>"HUD/Canvas"</c> 能命中 <c>UIRoot/HUD/Canvas</c>。</para>
        /// </summary>
        public static Transform Find(string nameOrPath, SceneScope scope = null)
        {
            var spec = (nameOrPath ?? string.Empty).Trim().Trim('/');
            if (spec.Length == 0)
            {
                return null;
            }

            var segments = spec.Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                segments[i] = segments[i].Trim();
            }

            Transform best = null;
            var bestScore = int.MaxValue;

            foreach (var root in Roots(scope))
            {
                Walk(root, 0, (node, depth) =>
                {
                    if (!MatchPath(node, segments))
                    {
                        return;
                    }

                    // 激活优先，其次层级浅优先
                    var score = depth + (node.gameObject.activeInHierarchy ? 0 : 10000);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = node;
                    }
                });
            }

            return best;
        }

        /// <summary>
        /// 按节点名或层级路径查找全部命中（排序规则与 <see cref="Find"/> 一致：
        /// 激活优先、层级由浅到深、同层按名字）。
        /// </summary>
        public static List<Transform> FindAll(string nameOrPath, SceneScope scope = null)
        {
            var spec = (nameOrPath ?? string.Empty).Trim().Trim('/');
            var result = new List<Transform>();
            if (spec.Length == 0)
            {
                return result;
            }

            var segments = spec.Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                segments[i] = segments[i].Trim();
            }

            var matches = new List<KeyValuePair<int, Transform>>();
            foreach (var root in Roots(scope))
            {
                Walk(root, 0, (node, depth) =>
                {
                    if (MatchPath(node, segments))
                    {
                        matches.Add(new KeyValuePair<int, Transform>(depth, node));
                    }
                });
            }

            matches.Sort((a, b) =>
            {
                var activeA = a.Value.gameObject.activeInHierarchy ? 0 : 1;
                var activeB = b.Value.gameObject.activeInHierarchy ? 0 : 1;
                if (activeA != activeB)
                {
                    return activeA.CompareTo(activeB);
                }

                if (a.Key != b.Key)
                {
                    return a.Key.CompareTo(b.Key);
                }

                var byName = string.CompareOrdinal(a.Value.name, b.Value.name);
                return byName != 0 ? byName : CompareHierarchyOrder(a.Value, b.Value);
            });

            foreach (var pair in matches)
            {
                result.Add(pair.Value);
            }

            return result;
        }

        /// <summary>
        /// 按组件类型查找场景节点，支持 "Canvas" / "Image" / "UnityEngine.UI.Image" 等写法
        /// <para>多个命中时按「层级由浅到深、同层按名字」排序，用 index 取第 N 个。</para>
        /// </summary>
        public static Transform FindByType(string typeName, int index = 0, SceneScope scope = null)
        {
            var type = ResolveComponentType(typeName);
            if (type == null)
            {
                return null;
            }

            var matches = new List<KeyValuePair<int, Transform>>();
            foreach (var root in Roots(scope))
            {
                Walk(root, 0, (node, depth) =>
                {
                    if (node.GetComponent(type) != null)
                    {
                        matches.Add(new KeyValuePair<int, Transform>(depth, node));
                    }
                });
            }

            if (matches.Count == 0)
            {
                return null;
            }

            matches.Sort((a, b) => a.Key != b.Key
                ? a.Key.CompareTo(b.Key)
                : string.CompareOrdinal(a.Value.name, b.Value.name) != 0
                    ? string.CompareOrdinal(a.Value.name, b.Value.name)
                    : CompareHierarchyOrder(a.Value, b.Value));

            // BUG-2 修复：越界时返回 null 而非静默钳制到最后一个——否则选择器写错时指令照样
            // 成功却改到另一个节点，比直接报错危险得多。负数下标兜底为 0。
            if (matches.Count == 0)
            {
                return null;
            }

            var i = index < 0 ? 0 : index;
            return i < matches.Count ? matches[i].Value : null;
        }

        /// <summary>按组件类型查找，返回全部命中（按层级由浅到深排序）</summary>
        public static List<Transform> FindAllByType(string typeName, SceneScope scope = null)
        {
            var result = new List<Transform>();
            var type = ResolveComponentType(typeName);
            if (type == null)
            {
                return result;
            }

            var matches = new List<KeyValuePair<int, Transform>>();
            foreach (var root in Roots(scope))
            {
                Walk(root, 0, (node, depth) =>
                {
                    if (node.GetComponent(type) != null)
                    {
                        matches.Add(new KeyValuePair<int, Transform>(depth, node));
                    }
                });
            }

            matches.Sort((a, b) => a.Key != b.Key
                ? a.Key.CompareTo(b.Key)
                : string.CompareOrdinal(a.Value.name, b.Value.name) != 0
                    ? string.CompareOrdinal(a.Value.name, b.Value.name)
                    : CompareHierarchyOrder(a.Value, b.Value));

            foreach (var pair in matches)
            {
                result.Add(pair.Value);
            }

            return result;
        }

        /// <summary>把 "Canvas" 之类的短名解析成组件类型（先试已知命名空间，再扫已加载程序集）</summary>
        public static Type ResolveComponentType(string name)
        {
            var text = (name ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return null;
            }

            // 已写全名的情况
            Type direct = null;
            try
            {
                direct = Type.GetType(text) ?? Type.GetType(text + ", UnityEngine");
            }
            catch (Exception)
            {
                direct = null;
            }

            if (direct != null && typeof(Component).IsAssignableFrom(direct))
            {
                return direct;
            }

            var candidates = new[] { "UnityEngine." + text, "UnityEngine.UI." + text, "TMPro." + text };
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var candidate in candidates)
                {
                    Type found = null;
                    try
                    {
                        found = assembly.GetType(candidate);
                    }
                    catch (Exception)
                    {
                        found = null;
                    }

                    if (found != null && typeof(Component).IsAssignableFrom(found))
                    {
                        return found;
                    }
                }
            }

            // 兜底：扫全部已加载程序集，按「类型短名 / 全名」匹配。
            // 覆盖项目自己的组件（可能没有命名空间，或在我们不知道的命名空间里）。
            var fallback = ResolveByShortName(text);
            return fallback;
        }

        /// <summary>
        /// 在所有已加载程序集里按类型短名或全名找 Component 派生类型。
        /// 只在已知命名空间都没命中时才走这里，避免每次调用都全量扫描。
        /// </summary>
        private static Type ResolveByShortName(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (Exception)
                {
                    // 部分动态/引用程序集无法枚举类型，跳过即可
                    continue;
                }

                foreach (var type in types)
                {
                    if (type == null || !type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition)
                    {
                        continue;
                    }

                    if (!typeof(Component).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    if (type.Name == name || type.FullName == name)
                    {
                        return type;
                    }
                }
            }

            return null;
        }

        /// <summary>节点的完整层级路径，用于日志与报错提示</summary>
        public static string BuildPath(Transform node)
        {
            if (node == null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            var current = node;
            while (current != null)
            {
                parts.Add(current.name);
                current = current.parent;
            }

            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        /// <summary>
        /// 生成可直接回喂给 target 的选择器路径：在最后一段名字后追加 <c>#同名序号</c>
        /// （如 <c>Canvas/SafeArea/Main/T_R/Panel_Currency#2</c>）。配合 <see cref="MatchPath"/> 的
        /// <c>#序号</c> 消歧，让 <c>gameobject.find</c> 的输出能原样喂回其它指令（修 BUG-3）。
        /// </summary>
        public static string SelectPath(Transform node)
        {
            if (node == null)
            {
                return string.Empty;
            }

            var parentPath = node.parent != null ? BuildPath(node.parent) : string.Empty;
            var prefix = parentPath.Length > 0 ? parentPath + "/" : string.Empty;
            return prefix + node.name + "#" + SameNameOrdinal(node);
        }

        /// <summary>字符串转小数，失败时回落默认值</summary>
        public static float ParseNumber(string text, float fallback)
        {
            float value;
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value
                : fallback;
        }

        /// <summary>遍历所有已加载场景的根节点（含 DontDestroyOnLoad），排除资源里的对象</summary>
        public static IEnumerable<Transform> SceneRoots()
        {
            var all = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            for (var i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t == null || t.parent != null)
                {
                    continue;
                }

                // 资源（Prefab 资源等）的 scene 不是有效场景，跳过
                if (!t.gameObject.scene.IsValid())
                {
                    continue;
                }

                yield return t;
            }
        }

        /// <summary>
        /// 按场景作用域过滤后的根节点。传 <c>null</c> 时与 <see cref="SceneRoots"/> 完全一致（跨所有已加载场景）。
        /// <para>作用域解析失败（0 个/多个场景）时返回空集合——具体原因由调用方从
        /// <see cref="SceneScope.Error"/> 取，避免这里把"找不到节点"和"场景写错"混成一句话。</para>
        /// </summary>
        private static IEnumerable<Transform> Roots(SceneScope scope)
        {
            if (scope == null)
            {
                return SceneRoots();
            }

            var list = new List<Transform>();
            foreach (var root in SceneRoots())
            {
                if (root != null && scope.Accepts(root.gameObject.scene))
                {
                    list.Add(root);
                }
            }

            return list;
        }

        /// <summary>
        /// 稳定序：先生成场景的加载序，再按同级排序下标。
        /// <para>
        /// 为什么必须加：同深度同名节点的比较结果是 0，而 <c>List.Sort</c> 是不稳定排序——
        /// 多场景叠加时同一个查询两次调用可能给出不同顺序（实测：候选清单里 SceneA/SceneB 会翻面），
        /// 客户端拿 <c>candidates[0]</c> 当唯一解就点错场景。
        /// </para>
        /// </summary>
        private static int CompareHierarchyOrder(Transform a, Transform b)
        {
            var sceneA = a.gameObject.scene;
            var sceneB = b.gameObject.scene;
            if (sceneA.handle != sceneB.handle)
            {
                var byIndex = sceneA.buildIndex.CompareTo(sceneB.buildIndex);
                if (byIndex != 0)
                {
                    return byIndex;
                }

                return sceneA.handle.CompareTo(sceneB.handle);
            }

            if (a.GetSiblingIndex() != b.GetSiblingIndex())
            {
                return a.GetSiblingIndex().CompareTo(b.GetSiblingIndex());
            }

            return a.GetInstanceID().CompareTo(b.GetInstanceID());
        }

        private static void Walk(Transform node, int depth, Action<Transform, int> visit)
        {
            visit(node, depth);
            for (var i = 0; i < node.childCount; i++)
            {
                Walk(node.GetChild(i), depth + 1, visit);
            }
        }

        /// <summary>层级路径匹配：从末尾往前逐段比对祖先链，因此 "A/B" 只需是后缀即可命中。
        /// 每段可带 <c>#序号</c> 消歧（如 <c>Panel_Currency#2</c>），序号为「同名兄弟中的排序下标」（0-based），
        /// 不带序号时只比名字。该语法让 <c>gameobject.find</c> 输出的 <c>selectPath</c> 能原样回喂。</summary>
        private static bool MatchPath(Transform node, string[] segments)
        {
            var i = segments.Length - 1;
            var current = node;
            while (i >= 0)
            {
                if (current == null)
                {
                    return false;
                }

                SplitSegment(segments[i], out var name, out var ordinal);
                if (!string.Equals(current.name, name, StringComparison.Ordinal))
                {
                    return false;
                }

                if (ordinal.HasValue && SameNameOrdinal(current) != ordinal.Value)
                {
                    return false;
                }

                current = current.parent;
                i--;
            }

            return true;
        }

        /// <summary>把一个路径段拆成「名字 + 可选同名序号」。段形如 <c>Name#2</c> 表示同名兄弟中的第 3 个。</summary>
        private static void SplitSegment(string segment, out string name, out int? ordinal)
        {
            name = segment;
            ordinal = null;
            var hash = segment.LastIndexOf('#');
            if (hash > 0 && hash < segment.Length - 1)
            {
                var suffix = segment.Substring(hash + 1);
                if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    name = segment.Substring(0, hash);
                    ordinal = n;
                }
            }
        }

        /// <summary>节点在「同名兄弟」中的排序下标（0-based）；无同名兄弟时为 0。</summary>
        private static int SameNameOrdinal(Transform node)
        {
            var parent = node.parent;
            if (parent == null)
            {
                return 0;
            }

            var ordinal = 0;
            foreach (Transform sibling in parent)
            {
                if (sibling == node)
                {
                    return ordinal;
                }

                if (string.Equals(sibling.name, node.name, StringComparison.Ordinal))
                {
                    ordinal++;
                }
            }

            return 0;
        }
    }
}
