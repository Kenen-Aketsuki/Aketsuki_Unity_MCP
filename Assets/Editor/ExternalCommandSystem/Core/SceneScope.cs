using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CivilRev.CommandSystem
{
    /// <summary>
    /// 场景作用域：把 target 收敛到**某一个已加载场景**内，解决多场景叠加（Additive）时的同名冲突。
    ///
    /// <para>
    /// <b>为什么必须有它：</b>叠加加载下「常驻引导场景 + 关卡场景 + UI 场景」通常都各有一份
    /// <c>UIRoot</c>／<c>Canvas</c>／<c>BtnStart</c>。而 <see cref="SceneQuery"/> 是**跨所有已加载场景**遍历的，
    /// 只给路径时按「激活优先、层级浅优先」挑一个——看起来有结果，实际改的／点的是另一个场景那份，
    /// 这种错很难从返回值里看出来。所以运行时指令一律要求「同名多命中就报错并列出候选」，
    /// 并在候选里直接给出可回喂的限定路径。
    /// </para>
    ///
    /// <para>
    /// <b>两种写法（等价）：</b>
    /// <list type="bullet">
    /// <item>字符串前缀：<c>"[scene:Level1]UIRoot/BtnStart"</c></item>
    /// <item>对象字段：<c>{ "scene": "Level1", "path": "UIRoot/BtnStart" }</c></item>
    /// </list>
    /// 前缀写成 <c>[scene:...]</c> 而不是裸 <c>[Level1]</c>，是为了不和节点名撞车——
    /// 对象池里 <c>[Pool]Item</c> 这种命名很常见。
    /// </para>
    ///
    /// <para>
    /// <b>选择器语法：</b>
    /// <list type="bullet">
    /// <item>场景名：<c>Level1</c>（大小写不敏感，须完整匹配）</item>
    /// <item>同名场景的第 N 个：<c>Level1#0</c>／<c>Level1#last</c>（同一个场景被叠加加载两次时用）</item>
    /// <item>场景 handle：<c>handle:3</c></item>
    /// <item>资产路径：<c>path:Assets/Scenes/Level1.unity</c>（也接受后缀与文件名）</item>
    /// <item>当前激活场景：<c>active</c></item>
    /// <item>常驻对象所在的合成场景：<c>DontDestroyOnLoad</c></item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class SceneScope
    {
        /// <summary>字符串形式的作用域前缀，后接场景选择器，以 <c>]</c> 收尾</summary>
        public const string Prefix = "[scene:";

        private const string DontDestroySceneName = "DontDestroyOnLoad";

        private readonly string _name;
        private readonly int _ordinal;
        private readonly bool _hasOrdinal;
        private readonly bool _lastOrdinal;
        private readonly int _handle;
        private readonly bool _useHandle;
        private readonly string _path;
        private readonly bool _active;
        private readonly bool _dontDestroy;

        private List<Scene> _matches;
        private string _resolveError;

        private SceneScope(string text, string name, int ordinal, bool hasOrdinal, bool lastOrdinal,
            int handle, bool useHandle, string path, bool active, bool dontDestroy)
        {
            Text = text;
            _name = name;
            _ordinal = ordinal;
            _hasOrdinal = hasOrdinal;
            _lastOrdinal = lastOrdinal;
            _handle = handle;
            _useHandle = useHandle;
            _path = path;
            _active = active;
            _dontDestroy = dontDestroy;
        }

        /// <summary>原始选择器文本（不含 <c>[scene:]</c> 包装），用于报错</summary>
        public string Text { get; }

        #region 解析

        /// <summary>解析场景选择器（不含前缀）。失败时 <paramref name="error"/> 给出可选语法。</summary>
        public static bool TryParse(string selector, out SceneScope scope, out string error)
        {
            scope = null;
            error = null;

            var text = (selector ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                error = "场景选择器为空：应写 [scene:<场景名>]，例如 [scene:Level1]UIRoot/BtnStart";
                return false;
            }

            if (text.Equals("active", StringComparison.OrdinalIgnoreCase))
            {
                scope = new SceneScope(text, null, 0, false, false, 0, false, null, true, false);
                return true;
            }

            if (text.Equals(DontDestroySceneName, StringComparison.OrdinalIgnoreCase))
            {
                scope = new SceneScope(text, DontDestroySceneName, 0, false, false, 0, false, null, false, true);
                return true;
            }

            if (text.StartsWith("handle:", StringComparison.OrdinalIgnoreCase))
            {
                int handle;
                if (!int.TryParse(text.Substring(7).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out handle))
                {
                    error = $"场景选择器 '{text}' 的 handle 不是整数；正确写法：handle:3";
                    return false;
                }

                scope = new SceneScope(text, null, 0, false, false, handle, true, null, false, false);
                return true;
            }

            if (text.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
            {
                var path = text.Substring(5).Trim();
                if (path.Length == 0)
                {
                    error = $"场景选择器 '{text}' 缺少路径；正确写法：path:Assets/Scenes/Level1.unity";
                    return false;
                }

                scope = new SceneScope(text, null, 0, false, false, 0, false, path, false, false);
                return true;
            }

            // 场景名，可带 #序号 / #last（同一个场景被叠加加载多次时用来点名第几份）
            var name = text;
            var ordinal = 0;
            var hasOrdinal = false;
            var lastOrdinal = false;
            var hash = text.LastIndexOf('#');
            if (hash > 0 && hash < text.Length - 1)
            {
                var suffix = text.Substring(hash + 1).Trim();
                if (suffix.Equals("last", StringComparison.OrdinalIgnoreCase))
                {
                    name = text.Substring(0, hash).Trim();
                    hasOrdinal = true;
                    lastOrdinal = true;
                }
                else
                {
                    int parsed;
                    if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    {
                        name = text.Substring(0, hash).Trim();
                        ordinal = parsed;
                        hasOrdinal = true;
                    }
                }
            }

            if (name.Length == 0)
            {
                error = $"场景选择器 '{text}' 缺少场景名";
                return false;
            }

            // 写成资产路径（含 / 或 .unity）时同时按路径匹配，省得调用方为了一个后缀换写法
            var looksLikePath = name.IndexOf('/') >= 0 || name.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
            scope = new SceneScope(text, name, ordinal, hasOrdinal, lastOrdinal, 0, false,
                looksLikePath ? name : null, false, false);
            return true;
        }

        /// <summary>
        /// 从 target 规格里抽出场景作用域。
        /// <para>
        /// 对象形式取 <c>scene</c> 字段（其余字段保持原样由调用方解析）；字符串形式识别并剥离
        /// <c>[scene:...]</c> 前缀，返回剥离后的文本 <paramref name="textSpec"/>。
        /// 两种形式都没有作用域时 <paramref name="scope"/> 为 <c>null</c>，行为与改造前完全一致。
        /// </para>
        /// </summary>
        public static bool TryExtract(JsonValue spec, out SceneScope scope, out string textSpec, out string error)
        {
            scope = null;
            textSpec = null;
            error = null;

            if (spec == null || spec.IsNull)
            {
                return true;
            }

            if (spec.IsObject)
            {
                var selector = spec["scene"].AsString();
                if (string.IsNullOrEmpty(selector))
                {
                    return true;
                }

                return TryParse(selector, out scope, out error);
            }

            var text = (spec.AsString() ?? string.Empty).Trim();
            textSpec = text;
            if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var close = text.IndexOf(']');
            if (close < 0)
            {
                error = $"target 的场景前缀没有闭合：'{text}'（正确写法：[scene:Level1]UIRoot/BtnStart）";
                return false;
            }

            var inner = text.Substring(Prefix.Length, close - Prefix.Length).Trim();
            if (!TryParse(inner, out scope, out error))
            {
                return false;
            }

            var rest = text.Substring(close + 1).Trim();
            if (rest.Length == 0)
            {
                error = $"场景前缀 '[scene:{inner}]' 后面没有节点路径；想列场景本身请用 runtime.hierarchy 的 scene 参数";
                return false;
            }

            textSpec = rest;
            return true;
        }

        #endregion

        #region 解析出场景

        /// <summary>
        /// 解析出唯一的目标场景。0 个命中 / 多个同名未消歧都会失败并给出候选清单。
        /// </summary>
        public bool TryResolve(out List<Scene> scenes, out string error)
        {
            if (_matches == null)
            {
                Resolve();
            }

            scenes = _matches;
            error = _resolveError;
            return _resolveError == null && _matches.Count == 1;
        }

        private void Resolve()
        {
            _matches = new List<Scene>();
            _resolveError = null;

            var candidates = AllLoadedScenes();
            var picked = new List<Scene>();
            foreach (var scene in candidates)
            {
                if (Matches(scene))
                {
                    picked.Add(scene);
                }
            }

            if (_hasOrdinal)
            {
                var index = _lastOrdinal ? picked.Count - 1 : _ordinal;
                if (picked.Count == 0)
                {
                    _resolveError = $"场景选择器 '{Text}' 没有匹配到任何已加载场景（当前：{DescribeSceneList(candidates)}）";
                    return;
                }

                if (index < 0 || index >= picked.Count)
                {
                    _resolveError =
                        $"场景选择器 '{Text}' 序号越界：同名场景共 {picked.Count} 个（0-based，最大 #{picked.Count - 1}，" +
                        $"或用 #last）（当前：{DescribeSceneList(candidates)}）";
                    return;
                }

                _matches.Add(picked[index]);
                return;
            }

            if (picked.Count == 0)
            {
                _resolveError = $"场景选择器 '{Text}' 没有匹配到任何已加载场景（当前：{DescribeSceneList(candidates)}）";
                return;
            }

            if (picked.Count > 1)
            {
                _resolveError = $"场景选择器 '{Text}' 命中 {picked.Count} 个场景，必须消歧（同名场景被叠加加载时会这样）。" +
                                "改用下列任一写法：" + DescribeSceneList(picked);
                return;
            }

            _matches.Add(picked[0]);
        }

        /// <summary>某场景的根节点是否落在本作用域内（过滤用；解析失败时一律不匹配）</summary>
        public bool Accepts(Scene scene)
        {
            if (_matches == null)
            {
                Resolve();
            }

            if (_resolveError != null)
            {
                return false;
            }

            foreach (var match in _matches)
            {
                if (match.handle == scene.handle)
                {
                    return true;
                }
            }

            return false;
        }

        private bool Matches(Scene scene)
        {
            if (_active)
            {
                return scene.handle == SceneManager.GetActiveScene().handle;
            }

            if (_useHandle)
            {
                return scene.handle == _handle;
            }

            if (_dontDestroy)
            {
                return IsDontDestroyOnLoad(scene);
            }

            if (_path != null)
            {
                var path = scene.path ?? string.Empty;
                if (path.Length == 0)
                {
                    return false;
                }

                if (path.Equals(_path, StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith("/" + _path, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // 只写文件名（Level1.unity）也认
                var fileName = SafeFileName(path);
                if (!string.IsNullOrEmpty(fileName)
                    && fileName.Equals(_path, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return _name != null && string.Equals(scene.name, _name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>失败时给调用方看的说明（成功则为 null）</summary>
        public string Error
        {
            get
            {
                if (_matches == null)
                {
                    Resolve();
                }

                return _resolveError;
            }
        }

        /// <summary>结果里回显作用域，便于客户端机器判定</summary>
        public JsonValue ToJson()
        {
            var data = JsonValue.NewObject();
            data.Set("selector", JsonValue.Of(Text));
            if (_matches == null)
            {
                Resolve();
            }

            var scenes = JsonValue.NewArray();
            if (_resolveError == null)
            {
                foreach (var scene in _matches)
                {
                    scenes.Add(DescribeScene(scene));
                }
            }

            data.Set("matched", scenes);
            if (_resolveError != null)
            {
                data.Set("error", JsonValue.Of(_resolveError));
            }

            return data;
        }

        #endregion

        #region 场景清单

        /// <summary>全部已加载场景，含 DontDestroyOnLoad 这个不在 SceneManager 列表里的合成场景</summary>
        public static List<Scene> AllLoadedScenes()
        {
            var list = new List<Scene>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isLoaded)
                {
                    list.Add(scene);
                }
            }

            var dontDestroy = DontDestroyOnLoadScene();
            if (dontDestroy.IsValid())
            {
                list.Add(dontDestroy);
            }

            return list;
        }

        /// <summary>
        /// 找 DontDestroyOnLoad 的合成场景。
        /// <para>它不在 <see cref="SceneManager"/> 的列表里（sceneCount 数不到），只能从常驻对象的
        /// <c>gameObject.scene</c> 反查；场上没有常驻对象时返回 default(Scene)。</para>
        /// </summary>
        public static Scene DontDestroyOnLoadScene()
        {
            foreach (var root in SceneQuery.SceneRoots())
            {
                if (root == null)
                {
                    continue;
                }

                var scene = root.gameObject.scene;
                if (scene.IsValid() && IsDontDestroyOnLoad(scene))
                {
                    return scene;
                }
            }

            return default(Scene);
        }

        /// <summary>场景的展示名：普通场景用名字，同名场景追加 <c>#序号</c></summary>
        public static string Label(Scene scene)
        {
            if (!scene.IsValid())
            {
                return "(无效场景)";
            }

            var name = string.IsNullOrEmpty(scene.name) ? "(未命名场景)" : scene.name;
            if (IsDontDestroyOnLoad(scene))
            {
                return DontDestroySceneName;
            }

            var all = AllLoadedScenes();
            var ordinal = 0;
            var duplicate = false;
            foreach (var other in all)
            {
                if (!string.Equals(other.name, scene.name, StringComparison.Ordinal))
                {
                    continue;
                }

                if (other.handle == scene.handle)
                {
                    duplicate = duplicate || ordinal > 0;
                    break;
                }

                ordinal++;
                duplicate = true;
            }

            return duplicate ? name + "#" + ordinal : name;
        }

        /// <summary>可直接写进 <c>[scene:...]</c> 的选择器（名字里有 <c>]</c> 时退回 handle）</summary>
        public static string SelectorOf(Scene scene)
        {
            if (!scene.IsValid())
            {
                return "?";
            }

            var label = Label(scene);
            if (label.IndexOf(']') >= 0 || label.IndexOf('[') >= 0)
            {
                return "handle:" + scene.handle;
            }

            return label;
        }

        /// <summary>场景的结构化描述（handle / buildIndex / 是否激活 / 资产路径）</summary>
        public static JsonValue DescribeScene(Scene scene)
        {
            var data = JsonValue.NewObject();
            data.Set("label", JsonValue.Of(Label(scene)));
            data.Set("selector", JsonValue.Of(SelectorOf(scene)));
            data.Set("name", JsonValue.Of(scene.name ?? string.Empty));
            data.Set("path", JsonValue.Of(scene.path ?? string.Empty));
            data.Set("handle", JsonValue.Of(scene.handle));
            data.Set("buildIndex", JsonValue.Of(scene.buildIndex));
            data.Set("active", JsonValue.Of(scene.handle == SceneManager.GetActiveScene().handle));
            data.Set("dontDestroyOnLoad", JsonValue.Of(IsDontDestroyOnLoad(scene)));
            return data;
        }

        /// <summary>候选场景清单，用于报错（每项形如 <c>Level1（handle=3, active）</c>）</summary>
        public static string DescribeSceneList(List<Scene> scenes)
        {
            if (scenes == null || scenes.Count == 0)
            {
                return "（无已加载场景）";
            }

            var builder = new System.Text.StringBuilder();
            for (var i = 0; i < scenes.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append("、");
                }

                var scene = scenes[i];
                builder.Append(scene.IsValid() ? Label(scene) : "(无效场景)");
                builder.Append("（selector=").Append(SelectorOf(scene));
                builder.Append(", handle=").Append(scene.handle);
                if (scene.IsValid() && scene.handle == SceneManager.GetActiveScene().handle)
                {
                    builder.Append(", active");
                }

                builder.Append("）");
            }

            return builder.ToString();
        }

        /// <summary>是否为 DontDestroyOnLoad 的合成场景</summary>
        public static bool IsDontDestroyOnLoad(Scene scene)
        {
            return scene.IsValid() && string.Equals(scene.name, DontDestroySceneName, StringComparison.Ordinal);
        }

        private static string SafeFileName(string path)
        {
            try
            {
                return Path.GetFileName(path);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        #endregion

        #region 限定路径

        /// <summary>
        /// 可直接回喂给 target 的限定路径：<c>[scene:Level1]UIRoot/BtnStart</c>。
        /// <para>
        /// 同名兄弟自动补 <c>#序号</c>；根节点同名（同一场景里两个同名根）无法用序号表达时，
        /// 直接退回 <c>[scene:X]id:12345</c>——宁可少点可读性也要保证精确。
        /// </para>
        /// </summary>
        public static string QualifiedPath(Transform node)
        {
            if (node == null)
            {
                return string.Empty;
            }

            var scene = node.gameObject.scene;
            bool rootAmbiguous;
            var path = PathWithOrdinals(node, out rootAmbiguous);
            if (!scene.IsValid() || rootAmbiguous)
            {
                return "id:" + node.GetInstanceID();
            }

            return Prefix + SelectorOf(scene) + "]" + path;
        }

        /// <summary>层级路径，同名兄弟补 <c>#序号</c>（不唯一表达根节点时 <paramref name="rootAmbiguous"/> 为 true）</summary>
        public static string PathWithOrdinals(Transform node, out bool rootAmbiguous)
        {
            rootAmbiguous = false;
            if (node == null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            var current = node;
            while (current != null)
            {
                if (current.parent == null)
                {
                    if (HasDuplicateRootName(current))
                    {
                        rootAmbiguous = true;
                    }

                    parts.Add(current.name);
                    break;
                }

                int ordinal;
                var duplicate = TrySiblingOrdinal(current, out ordinal);
                parts.Add(duplicate ? current.name + "#" + ordinal : current.name);
                current = current.parent;
            }

            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        /// <summary>节点在「同名兄弟」中的下标；第二个返回值表示是否真有同名兄弟（否则不必写 #N）</summary>
        private static bool TrySiblingOrdinal(Transform node, out int ordinal)
        {
            ordinal = 0;
            var parent = node.parent;
            if (parent == null)
            {
                return false;
            }

            var duplicate = false;
            foreach (Transform sibling in parent)
            {
                if (sibling == node)
                {
                    return duplicate;
                }

                if (string.Equals(sibling.name, node.name, StringComparison.Ordinal))
                {
                    ordinal++;
                    duplicate = true;
                }
            }

            return false;
        }

        /// <summary>同一场景里是否有别的根节点与它同名（同名根无法用 #序号 定位）</summary>
        private static bool HasDuplicateRootName(Transform node)
        {
            var roots = RootsOf(node.gameObject.scene);
            foreach (var other in roots)
            {
                if (other != node && string.Equals(other.name, node.name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>某场景的根节点，按真实层级顺序（普通场景走 GetRootGameObjects，DDOL 走全场景根节点反查）</summary>
        public static List<Transform> RootsOf(Scene scene)
        {
            var list = new List<Transform>();
            if (!scene.IsValid())
            {
                return list;
            }

            if (!IsDontDestroyOnLoad(scene) && scene.isLoaded)
            {
                foreach (var go in scene.GetRootGameObjects())
                {
                    if (go != null)
                    {
                        list.Add(go.transform);
                    }
                }

                return list;
            }

            foreach (var root in SceneQuery.SceneRoots())
            {
                if (root != null && root.gameObject.scene.handle == scene.handle)
                {
                    list.Add(root);
                }
            }

            return list;
        }

        #endregion
    }
}
