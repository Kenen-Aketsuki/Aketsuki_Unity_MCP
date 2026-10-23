using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CivilRev.CommandSystem.Editor
{
    /// <summary>
    /// UI 节点构建工具：把"创建一个带组件的 RectTransform 节点"这类重复劳动收敛到一处
    /// <para>
    /// 只做结构搭建与配色，不挂任何业务回调——指令系统只负责把节点摆出来。
    /// </para>
    /// </summary>
    internal static class UiNodeFactory
    {
        #region 节点与布局

        /// <summary>布局相关参数名；用于判断"这次调用到底想不想重新摆放"</summary>
        private static readonly string[] LayoutKeys =
        {
            "size", "anchoredPosition", "position", "anchorMin", "anchorMax",
            "offsetMin", "offsetMax", "polar", "rotation", "scale", "pivot"
        };

        /// <summary>
        /// 参数里是否带了任何布局信息
        /// <para>
        /// 复用已有节点时用它判断要不要重新摆放：没传布局参数就直接跳过 <see cref="ApplyLayout"/>，
        /// 否则"只想改个字体"会顺手把节点重置成全拉伸（ApplyLayout 的默认值就是这个）。
        /// </para>
        /// </summary>
        public static bool HasLayoutArgs(CommandArgs args)
        {
            if (args == null)
            {
                return false;
            }

            for (var i = 0; i < LayoutKeys.Length; i++)
            {
                if (args.Has(LayoutKeys[i]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>创建一个空的 RectTransform 节点</summary>
        public static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.localScale = Vector3.one;
            return rect;
        }

        /// <summary>
        /// 按参数应用布局
        /// <list type="bullet">
        /// <item>给了 <c>size</c>：锚点默认居中，按 size + anchoredPosition 摆放</item>
        /// <item>没给 size：按 anchorMin/anchorMax + offsetMin/offsetMax 摆放（默认全拉伸）</item>
        /// <item>给了 <c>polar</c>：按 <c>[半径, 角度]</c> 极坐标摆放，覆盖 anchoredPosition。
        /// 角度以"正上方"为 0°，顺时针为正——正好对应轮盘菜单的槽位编号</item>
        /// <item><c>rotation</c> 绕 Z 轴旋转角度；<c>scale</c> 缩放（数字或 [x,y]）</item>
        /// </list>
        /// </summary>
        public static void ApplyLayout(RectTransform rect, CommandArgs args)
        {
            // nativeSize 与 polar 都要求"点锚点 + sizeDelta 就是实际尺寸"，
            // 否则锚点会被拉成 0..1，SetNativeSize 与 anchoredPosition 都会失真
            var pointAnchored = (args.Has("size") && args.Get("size").IsArray)
                                || args.GetBool("nativeSize", false)
                                || args.Has("polar");

            var anchorMin = args.GetVector2("anchorMin", pointAnchored ? new Vector2(0.5f, 0.5f) : Vector2.zero);
            var anchorMax = args.GetVector2("anchorMax", pointAnchored ? new Vector2(0.5f, 0.5f) : Vector2.one);
            var hasSize = pointAnchored;

            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;

            if (hasSize)
            {
                var size = args.GetVector2("size", new Vector2(100f, 100f));
                rect.sizeDelta = size;
                rect.anchoredPosition = args.GetVector2("anchoredPosition", args.GetVector2("position", Vector2.zero));
            }
            else
            {
                rect.offsetMin = args.GetVector2("offsetMin", Vector2.zero);
                rect.offsetMax = args.GetVector2("offsetMax", Vector2.zero);
            }

            // 极坐标定位：0° 在正上方，顺时针为正
            if (args.Has("polar"))
            {
                var polar = args.GetVector2("polar", Vector2.zero);
                var radians = polar.y * Mathf.Deg2Rad;
                rect.anchoredPosition = new Vector2(
                    Mathf.Sin(radians) * polar.x,
                    Mathf.Cos(radians) * polar.x);
            }

            if (args.Has("rotation"))
            {
                rect.localRotation = Quaternion.Euler(0f, 0f, args.GetFloat("rotation"));
            }

            if (args.Has("scale"))
            {
                var node = args.Get("scale");
                var scale = node.IsArray
                    ? args.GetVector2("scale", Vector2.one)
                    : new Vector2(args.GetFloat("scale", 1f), args.GetFloat("scale", 1f));
                rect.localScale = new Vector3(scale.x, scale.y, 1f);
            }
            else
            {
                rect.localScale = Vector3.one;
            }

            if (args.Has("pivot"))
            {
                rect.pivot = args.GetVector2("pivot", new Vector2(0.5f, 0.5f));
            }
        }

        #endregion

        #region 组件

        /// <summary>挂一个纯色 Image（不依赖任何美术资源）</summary>
        public static Image AddImage(GameObject go, Color color, bool raycastTarget = true)
        {
            return AddImage(go, null, color, Image.Type.Simple, false, raycastTarget, false);
        }

        /// <summary>
        /// 挂一个 Image（可带 Sprite）
        /// </summary>
        /// <param name="sprite">为空时退化为纯色块</param>
        /// <param name="tint">叠加在 Sprite 上的颜色，纯色块时即填充色</param>
        /// <param name="type">Simple / Sliced / Tiled / Filled，圆角底板被拉伸时应使用 Sliced</param>
        /// <param name="preserveAspect">保护原始宽高比</param>
        /// <param name="nativeSize">按 Sprite 原始像素尺寸设置 sizeDelta</param>
        public static Image AddImage(
            GameObject go,
            Sprite sprite,
            Color tint,
            Image.Type type = Image.Type.Simple,
            bool preserveAspect = false,
            bool raycastTarget = true,
            bool nativeSize = false)
        {
            var image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.color = tint;
            image.raycastTarget = raycastTarget;
            image.preserveAspect = preserveAspect;

            if (sprite != null && type != Image.Type.Simple)
            {
                image.type = type;
            }

            if (sprite != null && nativeSize)
            {
                // pixelsPerUnit 与 Canvas 的 referencePixelsPerUnit 都为 100 时，结果就是图片像素尺寸
                image.SetNativeSize();
            }

            return image;
        }

        /// <summary>把 JSON 里的图片类型字符串转成枚举</summary>
        public static Image.Type ParseImageType(string text, Image.Type fallback)
        {
            if (string.IsNullOrEmpty(text))
            {
                return fallback;
            }

            switch (text.Trim().ToLowerInvariant())
            {
                case "simple":
                    return Image.Type.Simple;
                case "sliced":
                case "slice":
                    return Image.Type.Sliced;
                case "tiled":
                    return Image.Type.Tiled;
                case "filled":
                    return Image.Type.Filled;
                default:
                    return fallback;
            }
        }

        /// <summary>挂一个 Text（使用 Unity 内置字体，零资源依赖）</summary>
        public static Text AddText(
            GameObject go,
            string content,
            int fontSize,
            Color color,
            TextAnchor alignment = TextAnchor.MiddleCenter,
            bool bold = false,
            bool raycastTarget = false)
        {
            var text = go.AddComponent<Text>();
            text.text = content ?? string.Empty;
            text.fontSize = fontSize;
            text.color = color;
            text.alignment = alignment;
            text.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            text.raycastTarget = raycastTarget;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.font = GetDefaultFont();
            return text;
        }

        private static Font _cachedFont;

        /// <summary>获取 Unity 内置字体（不同版本内置资源名不同，逐个尝试）</summary>
        public static Font GetDefaultFont()
        {
            if (_cachedFont != null)
            {
                return _cachedFont;
            }

            // 不同 Unity 版本的内置字体名不同，逐个尝试
            var names = new[] { "LegacyRuntime.ttf", "Arial.ttf" };
            foreach (var name in names)
            {
                try
                {
                    var font = Resources.GetBuiltinResource<Font>(name);
                    if (font != null)
                    {
                        _cachedFont = font;
                        return font;
                    }
                }
                catch (Exception)
                {
                    // 继续尝试下一个
                }
            }

            // 兜底：内置资源取不到时，直接用操作系统字体创建，保证 Text 仍可见
            var osNames = new[] { "Arial", "Microsoft YaHei", "SimHei", "SimSun" };
            foreach (var name in osNames)
            {
                try
                {
                    var font = Font.CreateDynamicFontFromOSFont(name, 16);
                    if (font != null)
                    {
                        _cachedFont = font;
                        return font;
                    }
                }
                catch (Exception)
                {
                    // 继续尝试下一个
                }
            }

            return null;
        }

        #endregion

        #region 颜色

        /// <summary>
        /// 解析颜色，支持三种写法：
        /// [r,g,b,a] 数组（0~1）、"#RRGGBB" / "#RRGGBBAA" 字符串、"255,128,0" 之类暂不支持
        /// </summary>
        public static Color ParseColor(JsonValue value, Color fallback)
        {
            if (value == null || value.IsNull)
            {
                return fallback;
            }

            if (value.IsArray)
            {
                var r = value.Count > 0 ? value[0].AsFloat(0f) : 0f;
                var g = value.Count > 1 ? value[1].AsFloat(0f) : 0f;
                var b = value.Count > 2 ? value[2].AsFloat(0f) : 0f;
                var a = value.Count > 3 ? value[3].AsFloat(1f) : 1f;

                // 兼容 0~255 写法
                if (r > 1f || g > 1f || b > 1f)
                {
                    r /= 255f;
                    g /= 255f;
                    b /= 255f;
                    if (value.Count > 3)
                    {
                        a = value[3].AsFloat(255f) / 255f;
                    }
                }

                return new Color(r, g, b, a);
            }

            var text = value.AsString();
            if (string.IsNullOrEmpty(text))
            {
                return fallback;
            }

            if (text[0] != '#')
            {
                text = "#" + text;
            }

            Color parsed;
            if (ColorUtility.TryParseHtmlString(text, out parsed))
            {
                return parsed;
            }

            return fallback;
        }

        #endregion

        #region 场景查找

        /// <summary>ui.canvas 解析到的画布：场景里有多个 Canvas 时，保证后续指令用的是同一个</summary>
        private static Canvas _lastCanvas;

        /// <summary>
        /// 记录"当前主画布"，供后续 <c>type:Canvas</c> 复用
        /// <para>供 <c>ui.uiRoot</c> 这类自己手动建画布的指令调用，行为与 <c>ui.canvas</c> 保持一致。</para>
        /// </summary>
        public static void RememberCanvas(Canvas canvas)
        {
            if (canvas != null)
            {
                _lastCanvas = canvas;
            }
        }

        /// <summary>
        /// 解析父节点：优先用 parent 参数，其次 canvas 参数，最后退回场景中的 Canvas
        /// <para>
        /// parent / canvas 支持四种写法：
        /// <list type="bullet">
        /// <item>节点名：<c>"RoundMenu"</c></item>
        /// <item>层级路径（后缀匹配，不必从根写起）：<c>"UIRoot/HUD/Canvas"</c></item>
        /// <item>按组件类型：<c>"type:Canvas"</c>（<c>"@canvas"</c> 为同义简写）；多个时用 <c>"type:Canvas#1"</c> 取第 2 个</item>
        /// <item>对象形式：<c>{ "type": "Canvas" }</c> / <c>{ "path": "UIRoot/Canvas" }</c> / <c>{ "name": "HUD" }</c></item>
        /// </list>
        /// 名称查找会遍历所有已加载场景并包含 inactive 节点，不受 GameObject.Find 的限制。
        /// </para>
        /// </summary>
        public static Transform ResolveParent(CommandArgs args)
        {
            var parentSpec = args.Get("parent");
            if (parentSpec != null && !parentSpec.IsNull)
            {
                var found = ResolveTarget(parentSpec);
                if (found == null)
                {
                    throw new CommandArgumentException("ui", "parent",
                        $"场景中找不到父节点 '{DescribeSpec(parentSpec)}'。{DescribeCanvasCandidates()}");
                }
                return found;
            }

            var canvasSpec = args.Get("canvas");
            if (canvasSpec != null && !canvasSpec.IsNull)
            {
                var found = ResolveTarget(canvasSpec);
                if (found == null)
                {
                    throw new CommandArgumentException("ui", "canvas",
                        $"场景中找不到 Canvas '{DescribeSpec(canvasSpec)}'。{DescribeCanvasCandidates()}");
                }
                return found;
            }

            var canvas = FindFirstCanvas();
            return canvas != null ? canvas.transform : null;
        }

        /// <summary>
        /// 解析查找目标：节点名 / 层级路径 / 组件类型（细节见 <see cref="SceneQuery.ResolveTarget"/>）
        /// <para>供 UI 指令与 <c>gameobject.*</c> 指令共用同一套查找规则。</para>
        /// </summary>
        public static Transform ResolveTarget(JsonValue spec)
        {
            return SceneQuery.ResolveTarget(spec);
        }

        /// <summary>
        /// 按节点名或层级路径查找（细节见 <see cref="SceneQuery.Find"/>）
        /// <para>相比 <c>GameObject.Find</c>：覆盖所有已加载场景、包含 inactive 节点、支持 "A/B" 后缀路径。</para>
        /// </summary>
        public static Transform FindInScene(string nameOrPath)
        {
            return SceneQuery.Find(nameOrPath);
        }

        /// <summary>
        /// 按组件类型查找场景节点，支持 "Canvas" / "Image" / "UnityEngine.UI.Image" 等写法
        /// <para>多个命中时按「层级由浅到深、同层按名字」排序，用 index 取第 N 个。</para>
        /// </summary>
        public static Transform FindByType(string typeName, int index = 0)
        {
            // 画布优先复用 ui.canvas 解析到的那个，避免场景里有多个 Canvas 时前后不一致
            if (string.Equals((typeName ?? string.Empty).Trim(), "Canvas", StringComparison.OrdinalIgnoreCase)
                && _lastCanvas != null)
            {
                return _lastCanvas.transform;
            }

            return SceneQuery.FindByType(typeName, index);
        }

        /// <summary>
        /// 取场景中的主画布：优先 <see cref="RenderMode.ScreenSpaceOverlay"/>，其次层级最浅的
        /// <para>
        /// 世界里那些 WorldSpace 画布（血条、飘字之类）不能当主 UI 根节点，
        /// 所以先按渲染模式过滤，再按 SceneQuery 排好的层级顺序取第一个。
        /// </para>
        /// </summary>
        private static Canvas FindFirstCanvas()
        {
            var canvases = SceneQuery.FindAllByType("Canvas");
            if (canvases.Count == 0)
            {
                return null;
            }

            // SceneQuery 已按层级由浅到深排序；先找 Overlay 画布
            for (var i = 0; i < canvases.Count; i++)
            {
                var canvas = canvases[i].GetComponent<Canvas>();
                if (canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    return canvas;
                }
            }

            // 没有 Overlay 画布时退回最浅的那个
            return canvases[0].GetComponent<Canvas>();
        }

        /// <summary>把 "Canvas" 之类的短名解析成组件类型（先试已知命名空间，再扫已加载程序集）</summary>
        private static Type ResolveComponentType(string name)
        {
            return SceneQuery.ResolveComponentType(name);
        }

        /// <summary>节点的完整层级路径，用于日志与报错提示</summary>
        public static string BuildPath(Transform node)
        {
            return SceneQuery.BuildPath(node);
        }

        private static float ParseNumber(string text, float fallback)
        {
            return SceneQuery.ParseNumber(text, fallback);
        }

        /// <summary>找不到父节点时，把场景里真实存在的 Canvas 列出来，方便直接照抄</summary>
        private static string DescribeCanvasCandidates()
        {
            var canvases = SceneQuery.FindAllByType("Canvas");
            if (canvases.Count == 0)
            {
                return "（场景中当前没有任何 Canvas）";
            }

            var names = new List<string>();
            foreach (var node in canvases)
            {
                names.Add("'" + SceneQuery.BuildPath(node) + "'");
            }

            return "场景中的 Canvas 有：" + string.Join("、", names.ToArray())
                   + "；也可以把 parent 写成 \"type:Canvas\" 直接按类型查找";
        }

        /// <summary>把 parent 参数还原成可读文本（对象形式时给出紧凑描述）</summary>
        private static string DescribeSpec(JsonValue spec)
        {
            if (spec == null || spec.IsNull)
            {
                return "(空)";
            }

            if (spec.IsObject)
            {
                return spec.ToJson(false);
            }

            return spec.AsString();
        }

        /// <summary>确保场景里有一个可用 Canvas：已存在则复用（取最外层那个），否则新建</summary>
        public static Canvas EnsureCanvas(string name, bool reuse, Vector2 referenceResolution)
        {
            if (reuse)
            {
                var existing = FindFirstCanvas();
                if (existing != null)
                {
                    _lastCanvas = existing;
                    return existing;
                }
            }

            // 显式带上 RectTransform：Canvas / CanvasScaler 都需要它，不写则依赖 Unity 自动补
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 0;

            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = referenceResolution;
            scaler.matchWidthOrHeight = 0.5f;

            _lastCanvas = canvas;
            return canvas;
        }

        /// <summary>确保场景里有一个 EventSystem（自动适配新旧输入系统）</summary>
        public static void EnsureEventSystem(string name, Transform parent = null)
        {
            if (UnityEngine.Object.FindObjectOfType<EventSystem>() != null)
            {
                return;
            }

            var go = new GameObject(name, typeof(EventSystem));

            // 项目若启用了 InputSystem，则使用对应的 UI 输入模块，避免新旧输入冲突
            var inputSystemModuleType = Type.GetType(
                "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");

            if (inputSystemModuleType != null)
            {
                var module = go.AddComponent(inputSystemModuleType);
                var assignMethod = inputSystemModuleType.GetMethod("AssignDefaultActions");
                assignMethod?.Invoke(module, null);
            }
            else
            {
                go.AddComponent<StandaloneInputModule>();
            }

            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }
        }

        #endregion

        #region 收尾

        /// <summary>注册撤销、标记场景为已修改并选中新节点</summary>
        public static void Finalize(GameObject root, bool select)
        {
            if (root == null)
            {
                return;
            }

#if UNITY_EDITOR
            UnityEditor.Undo.RegisterCreatedObjectUndo(root, "CommandSystem.CreateUI");

            // 连同子节点一起注册，保证一次撤销能撤掉整棵树，而不是只撤根节点
            var children = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < children.Length; i++)
            {
                if (children[i].gameObject != root)
                {
                    UnityEditor.Undo.RegisterCreatedObjectUndo(children[i].gameObject, "CommandSystem.CreateUI");
                }
            }

            var scene = root.scene;
            if (scene.IsValid())
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            }

            if (select)
            {
                UnityEditor.Selection.activeGameObject = root;
            }
#endif
        }

        /// <summary>
        /// 修改已有节点时的收尾：记录一次可撤销的改动、标记场景为已修改、（可选）选中
        /// <para>
        /// 与 <see cref="Finalize"/> 的区别：那边记录的是"创建"撤销，对已存在的对象不适用，
        /// 这里用 <c>Undo.RecordObject</c> 记录"改动"，撤销后节点回到修改前而不是被删掉。
        /// </para>
        /// </summary>
        /// <param name="node">被改的节点（用于标记场景脏与选中）</param>
        /// <param name="changed">真正被改动的组件（字体、颜色等挂在这里）</param>
        public static void FinalizeChange(GameObject node, UnityEngine.Object changed, bool select)
        {
            if (node == null)
            {
                return;
            }

#if UNITY_EDITOR
            if (changed != null)
            {
                UnityEditor.Undo.RecordObject(changed, "CommandSystem.ModifyUI");
            }

            var scene = node.scene;
            if (scene.IsValid())
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            }

            if (select)
            {
                UnityEditor.Selection.activeGameObject = node;
            }
#endif
        }

        /// <summary>把 TextAnchor 名称转成枚举</summary>
        public static TextAnchor ParseAnchor(string text, TextAnchor fallback)
        {
            if (string.IsNullOrEmpty(text))
            {
                return fallback;
            }

            switch (text.Trim().ToLowerInvariant())
            {
                case "left":
                case "middleleft":
                    return TextAnchor.MiddleLeft;
                case "right":
                case "middleright":
                    return TextAnchor.MiddleRight;
                case "top":
                case "uppercenter":
                    return TextAnchor.UpperCenter;
                case "bottom":
                case "lowercenter":
                    return TextAnchor.LowerCenter;
                case "center":
                case "middlecenter":
                    return TextAnchor.MiddleCenter;
                default:
                    return fallback;
            }
        }

        /// <summary>数值转字符串时使用不变文化，避免中文系统下出现逗号小数点</summary>
        public static string Num(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        #endregion
    }
}
