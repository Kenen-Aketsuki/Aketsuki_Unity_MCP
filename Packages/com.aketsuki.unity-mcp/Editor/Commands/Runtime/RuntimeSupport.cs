using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 运行时（Play 模式）指令的共用支撑：把"编辑期随便点、运行期要算"的差别收在一处。
    ///
    /// <para>
    /// 与编辑期指令最大的不同：运行时的坐标全是**屏幕像素**，而节点的定位信息散落在
    /// Canvas 渲染模式（Overlay 直接就是屏幕坐标，Camera/ScreenSpaceCamera 要走
    /// <see cref="RectTransformUtility.WorldToScreenPoint"/>）与世界坐标（非 UI 对象走
    /// <see cref="Camera.WorldToScreenPoint"/>）三种情况里。写错了表现是"点了但没点到"，
    /// 很难从日志看出原因，所以这里统一处理并在结果里回报判定来源。
    /// </para>
    /// </summary>
    internal static class RuntimeSupport
    {
        /// <summary>歧义候选最多列这么多条（场景里同名节点上千时不能全塞进响应体）</summary>
        internal const int MaxCandidates = 10;

        /// <summary>
        /// 取节点在屏幕上的中心点（像素），并回报用的是哪种换算，便于排查"点歪了"。
        /// <paramref name="source"/> 取值：<c>ui-overlay</c> / <c>ui-camera</c> / <c>ui-null-camera</c> /
        /// <c>world</c> / <c>world-ortho</c> / <c>no-camera</c>。
        /// </summary>
        internal static Vector2 ScreenPointOf(Transform node, out string source)
        {
            var rect = node as RectTransform;
            if (rect != null)
            {
                // Overlay 模式下 Canvas 的世界坐标本身就是屏幕像素，套 WorldToScreenPoint 反而会算错
                var canvas = rect.GetComponentInParent<Canvas>();
                if (canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    source = "ui-overlay";
                    var overlayCenter = rect.TransformPoint(rect.rect.center);
                    return new Vector2(overlayCenter.x, overlayCenter.y);
                }

                var camera = canvas != null ? canvas.worldCamera : null;
                source = camera != null ? "ui-camera" : "ui-null-camera";
                return RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
            }

            var main = Camera.main;
            if (main == null)
            {
                source = "no-camera";
                return Vector2.zero;
            }

            source = main.orthographic ? "world-ortho" : "world";
            return main.WorldToScreenPoint(node.position);
        }

        /// <summary>
        /// 沿「自身 → 祖先 → 子树」找组件。
        /// <para>
        /// 运行时指令的 target 常常是按钮里的 Text/Image，而真正处理交互的组件在父节点上；
        /// 反过来列表项也可能把 ScrollRect 挂在子节点。所以先往上找，再退化为子树搜索。
        /// </para>
        /// </summary>
        internal static T FindInChain<T>(Transform from) where T : Component
        {
            if (from == null)
            {
                return null;
            }

            for (var current = from; current != null; current = current.parent)
            {
                var found = current.GetComponent<T>();
                if (found != null)
                {
                    return found;
                }
            }

            return from.GetComponentInChildren<T>(true);
        }

        /// <summary>节点的人类可读路径（用于日志与报错）</summary>
        internal static string Describe(Transform node)
        {
            return node == null ? "<null>" : SceneQuery.BuildPath(node);
        }

        /// <summary>
        /// 节点信息（含 <c>qualifiedPath</c>）：运行时指令回显 target 时统一用它，
        /// 免得客户端为了消歧还得自己从 path 拼场景前缀。
        /// </summary>
        internal static JsonValue NodeJson(Transform node)
        {
            return NodeJson(node, true);
        }

        /// <summary>带裁剪开关的节点信息（IMPR-018）：<paramref name="includeComponents"/> 为 false 时组件只留 Transform。</summary>
        internal static JsonValue NodeJson(Transform node, bool includeComponents)
        {
            var item = SceneQueryCommandsUtil.NodeToJson(node, includeComponents);
            item.Set("qualifiedPath", JsonValue.Of(SceneScope.QualifiedPath(node)));
            return item;
        }

        /// <summary>节点的**带场景限定**路径：<c>[scene:Level1]UIRoot/BtnStart</c>，多场景下才是唯一可回喂的写法</summary>
        internal static string Qualified(Transform node)
        {
            return node == null ? "<null>" : SceneScope.QualifiedPath(node);
        }

        /// <summary>
        /// 严格解析运行时 target：<b>0 个命中就报错，多个命中就要求消歧</b>。
        ///
        /// <para>
        /// <b>为什么不用 <see cref="SceneQuery.ResolveTarget"/>：</b>那个是"尽力而为"的语义
        /// （激活优先、层级浅优先），单场景编辑期没问题；但多场景叠加时同一个 <c>UIRoot/BtnStart</c>
        /// 在引导场景和关卡场景里各有一份，它照样会挑一个返回——看起来成功，实际点的是另一份。
        /// 运行时指令有真实副作用，宁可失败也不能猜。
        /// </para>
        ///
        /// <para>
        /// 歧义时会往 <paramref name="data"/> 里写 <c>candidates</c>（每条带 <c>qualifiedPath</c> /
        /// <c>instanceId</c>），调用方把它原样回喂给 target 即可。
        /// </para>
        /// </summary>
        internal static Transform ResolveTarget(JsonValue spec, JsonValue data, out string error)
        {
            error = null;

            SceneScope scope;
            string textSpec;
            string scopeError;
            if (!SceneScope.TryExtract(spec, out scope, out textSpec, out scopeError))
            {
                error = scopeError;
                return null;
            }

            if (scope != null)
            {
                // 作用域自身的问题（0 个/多个场景）单独报，否则会被"找不到节点"掩盖
                if (scope.Error != null)
                {
                    error = scope.Error;
                    if (data != null)
                    {
                        data.Set("sceneScope", scope.ToJson());
                    }

                    return null;
                }

                if (data != null)
                {
                    data.Set("sceneScope", scope.ToJson());
                }
            }

            var matches = SceneQuery.FindAll(spec);
            if (matches.Count == 0)
            {
                error = $"{DescribeSpecForError(spec)} 在运行时场景里找不到" +
                        $"（当前已加载场景：{SceneScope.DescribeSceneList(SceneScope.AllLoadedScenes())}）" +
                        "；可先调 runtime.hierarchy 看实际有哪些节点（多场景叠加时建议带 [scene:场景名] 前缀）";
                return null;
            }

            if (matches.Count == 1)
            {
                return matches[0];
            }

            var candidates = JsonValue.NewArray();
            var lines = new List<string>();
            for (var i = 0; i < matches.Count; i++)
            {
                if (i >= MaxCandidates)
                {
                    lines.Add($"  ……另有 {matches.Count - MaxCandidates} 个未列出");
                    break;
                }

                var node = matches[i];
                var item = JsonValue.NewObject();
                item.Set("qualifiedPath", JsonValue.Of(Qualified(node)));
                item.Set("path", JsonValue.Of(SceneQuery.BuildPath(node)));
                item.Set("scene", JsonValue.Of(SceneScope.Label(node.gameObject.scene)));
                item.Set("instanceId", JsonValue.Of(node.GetInstanceID()));
                item.Set("activeInHierarchy", JsonValue.Of(node.gameObject.activeInHierarchy));
                candidates.Add(item);

                lines.Add($"  {Qualified(node)}（activeInHierarchy={node.gameObject.activeInHierarchy}, " +
                          $"instanceId={node.GetInstanceID()}）");
            }

            if (data != null)
            {
                data.Set("candidates", candidates);
                data.Set("candidateCount", JsonValue.Of(matches.Count));
            }

            error = $"{DescribeSpecForError(spec)} 命中 {matches.Count} 个节点，无法确定点哪个——" +
                    "多场景叠加 / 同名兄弟都会这样，必须显式消歧。把下面任一条原样作为 target 重试：\n" +
                    string.Join("\n", lines.ToArray());
            return null;
        }

        private static string DescribeSpecForError(JsonValue spec)
        {
            if (spec == null || spec.IsNull)
            {
                return "<空 target>";
            }

            if (spec.IsString)
            {
                return $"'{spec.AsString()}'";
            }

            var text = spec.ToJson(false);
            return text.Length > 160 ? text.Substring(0, 160) + "…" : text;
        }

        /// <summary>
        /// 取可用的 EventSystem。运行时点击/拖拽都靠它做射线与事件分发，没有就什么都点不了。
        /// </summary>
        internal static EventSystem RequireEventSystem(out string error)
        {
            var system = EventSystem.current;
            if (system == null)
            {
                error = "场景里没有处于激活状态的 EventSystem（EventSystem.current 为空）：" +
                        "UGUI 的点击/拖拽全靠它分发，请先确认运行时存在 EventSystem（可用 runtime.hierarchy 查，" +
                        "编辑期可用 ui.canvas 的 createEventSystem 创建）。";
                return null;
            }

            error = null;
            return system;
        }

        /// <summary>EventSystem 现状快照，失败时一并返回，省一次查询</summary>
        internal static JsonValue EventSystemJson()
        {
            var system = EventSystem.current;
            var data = JsonValue.NewObject();
            data.Set("present", JsonValue.Of(system != null));
            if (system == null)
            {
                return data;
            }

            data.Set("name", JsonValue.Of(system.name));
            data.Set("path", JsonValue.Of(SceneQuery.BuildPath(system.transform)));
            data.Set("inputModule", JsonValue.Of(
                system.currentInputModule != null ? system.currentInputModule.GetType().Name : string.Empty));
            data.Set("selected", JsonValue.Of(
                system.currentSelectedGameObject != null ? system.currentSelectedGameObject.name : string.Empty));
            return data;
        }

        /// <summary>把 Button 之类的 onClick 监听数量报出来，判断"点了但没反应"是没监听还是被拦</summary>
        internal static JsonValue ButtonJson(Transform node)
        {
            var button = FindInChain<Button>(node);
            if (button == null)
            {
                return JsonValue.Null;
            }

            var data = JsonValue.NewObject();
            data.Set("path", JsonValue.Of(SceneQuery.BuildPath(button.transform)));
            data.Set("interactable", JsonValue.Of(button.interactable));
            data.Set("listenerCount", JsonValue.Of(button.onClick != null ? button.onClick.GetPersistentEventCount() : 0));
            return data;
        }

        /// <summary>射线命中的节点清单（截断到 <paramref name="max"/> 条），用于诊断"被谁挡住了"</summary>
        internal static JsonValue RaycastHitsJson(List<RaycastResult> hits, int max)
        {
            var array = JsonValue.NewArray();
            for (var i = 0; i < hits.Count && i < max; i++)
            {
                var hit = hits[i];
                var item = JsonValue.NewObject();
                item.Set("path", JsonValue.Of(hit.gameObject != null ? SceneQuery.BuildPath(hit.gameObject.transform) : string.Empty));
                item.Set("depth", JsonValue.Of(hit.depth));
                item.Set("distance", JsonValue.Of(Mathf.Round(hit.distance * 1000f) / 1000f));
                array.Add(item);
            }

            return array;
        }
    }
}
