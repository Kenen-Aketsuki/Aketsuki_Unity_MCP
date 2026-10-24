using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CivilRev.CommandSystem.Editor
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
