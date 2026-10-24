using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CivilRev.CommandSystem.Editor
{
    /// <summary>
    /// 模拟一次点击：通过 EventSystem 注入 pointerDown / pointerUp / pointerClick 事件。
    ///
    /// <para>
    /// <b>为什么不用 OS 级鼠标模拟：</b><c>SendInput</c> 之类要抢焦点、受窗口位置影响、
    /// 还会真的动用户的鼠标指针，编辑器在后台时直接失效。走 EventSystem 注入是等价且可控的：
    /// uGUI 的按钮、开关、列表项最终都是通过这些接口响应，遮罩、<c>interactable</c>、
    /// 事件冒泡全部按真实链路走一遍。
    /// </para>
    ///
    /// <para>
    /// <b>两种定位方式：</b>
    /// <list type="bullet">
    /// <item><c>target</c>：给节点（名字/路径/type 选择器，与其它指令同一套），自动换算成屏幕坐标；</item>
    /// <item><c>position</c>：直接给屏幕像素坐标，用于点空白处、点某个位置、验证"这里是不是能被点到"。</item>
    /// </list>
    /// 两者都给时以 <c>position</c> 为准，但仍会带 <c>target</c> 做遮挡比对。
    /// </para>
    ///
    /// <para>
    /// <b>失败了怎么查：</b>返回里的 <c>raycast.hits</c> 是按深度排序的命中清单——如果第一项不是你要的对象，
    /// 就是被挡住了；<c>button.listenerCount</c> 为 0 说明按钮根本没人监听。
    /// <c>direct</c> 模式跳过射线直接对目标触发 <c>IPointerClickHandler</c>，用于"元素被透明层挡住但确实想点它"。
    /// </para>
    ///
    /// <para>参数：target、position、button（left/right/middle）、clicks（默认 1）、direct（默认 false）</para>
    /// </summary>
    [Command("runtime.click",
        Description = "模拟点击：EventSystem 注入 pointerDown/Up/Click，可按节点或屏幕坐标",
        Usage = "target: target, position: [x,y], button: left|right|middle, clicks: int, direct: bool",
        EditorOnly = true, Runtime = true)]
    public sealed class RuntimeClickCommand : ICommand
    {
        private const int MaxReportHits = 8;

        public CommandResult Execute(CommandContext context)
        {
            var data = JsonValue.NewObject();
            data.Set("eventSystem", RuntimeSupport.EventSystemJson());

            string error;
            var system = RuntimeSupport.RequireEventSystem(out error);
            if (system == null)
            {
                return CommandResult.Fail(error, data);
            }

            var targetSpec = context.Args.Get("target");
            Transform target = null;
            if (targetSpec != null && !targetSpec.IsNull)
            {
                target = SceneQuery.ResolveTarget(targetSpec);
                if (target == null)
                {
                    return CommandResult.Fail(
                        $"运行时场景里找不到 target：{SceneQueryCommandsUtil.Describe(targetSpec)}" +
                        "（可先用 runtime.hierarchy 看运行时实际有哪些节点）", data);
                }

                data.Set("target", SceneQueryCommandsUtil.NodeToJson(target));
                var buttonInfo = RuntimeSupport.ButtonJson(target);
                if (buttonInfo != null && buttonInfo.IsObject)
                {
                    data.Set("button", buttonInfo);
                }
            }

            Vector2 position;
            string positionSource;
            if (context.Args.Has("position"))
            {
                position = context.Args.GetVector2("position", Vector2.zero);
                positionSource = "explicit";
            }
            else if (target != null)
            {
                position = RuntimeSupport.ScreenPointOf(target, out positionSource);
                if (positionSource == "no-camera")
                {
                    return CommandResult.Fail(
                        $"target '{SceneQuery.BuildPath(target)}' 不是 UI 节点，且场景里没有 MainCamera，" +
                        "无法换算屏幕坐标；请直接用 position 指定屏幕像素坐标。", data);
                }
            }
            else
            {
                return CommandResult.Fail("需要 target 或 position 之一：target 用来定位节点，position 用来指定屏幕像素坐标", data);
            }

            var buttonName = (context.Args.GetString("button", "left") ?? "left").Trim().ToLowerInvariant();
            PointerEventData.InputButton button;
            if (!TryParseButton(buttonName, out button))
            {
                return CommandResult.Fail($"不支持的 button：'{buttonName}'（可选 left / right / middle）", data);
            }

            var clicks = Mathf.Clamp(context.Args.GetInt("clicks", 1), 1, 3);
            var direct = context.Args.GetBool("direct", false);

            var pointer = new PointerEventData(system)
            {
                position = position,
                pressPosition = position,
                button = button,
                clickCount = clicks,
                pointerId = -1,
                eligibleForClick = true
            };

            data.Set("position", JsonValue.FromArray(new[]
            {
                JsonValue.Of(Mathf.Round(position.x * 100f) / 100f),
                JsonValue.Of(Mathf.Round(position.y * 100f) / 100f)
            }));
            data.Set("positionSource", JsonValue.Of(positionSource));
            data.Set("buttonName", JsonValue.Of(buttonName));
            data.Set("clicks", JsonValue.Of(clicks));
            data.Set("direct", JsonValue.Of(direct));

            if (direct)
            {
                return ClickDirect(data, system, pointer, target, clicks);
            }

            var hits = new List<RaycastResult>();
            system.RaycastAll(pointer, hits);
            var raycast = JsonValue.NewObject();
            raycast.Set("hitCount", JsonValue.Of(hits.Count));
            raycast.Set("hits", RuntimeSupport.RaycastHitsJson(hits, MaxReportHits));
            data.Set("raycast", raycast);

            if (hits.Count == 0)
            {
                return CommandResult.Fail(
                    $"屏幕坐标 ({position.x:F0},{position.y:F0}) 没有命中任何可交互元素：" +
                    "可能该处没有 UI、Canvas 缺 GraphicRaycaster、或元素的 raycastTarget 被关掉了。", data);
            }

            var hitObject = hits[0].gameObject;
            pointer.pointerPress = hitObject;
            pointer.rawPointerPress = hitObject;
            pointer.pointerEnter = hitObject;

            var downPath = string.Empty;
            var upPath = string.Empty;
            var clickPath = string.Empty;
            for (var i = 0; i < clicks; i++)
            {
                downPath = DispatchPath(hitObject, pointer, ExecuteEvents.pointerDownHandler);
                upPath = DispatchPath(hitObject, pointer, ExecuteEvents.pointerUpHandler);
                clickPath = DispatchPath(hitObject, pointer, ExecuteEvents.pointerClickHandler);
            }

            var dispatch = JsonValue.NewObject();
            dispatch.Set("pointerDown", JsonValue.Of(downPath));
            dispatch.Set("pointerUp", JsonValue.Of(upPath));
            dispatch.Set("pointerClick", JsonValue.Of(clickPath));
            data.Set("dispatch", dispatch);

            var warning = OcclusionWarning(target, hitObject);
            if (!string.IsNullOrEmpty(warning))
            {
                data.Set("warning", JsonValue.Of(warning));
            }

            // 点击后的选中态：Selectable 的 OnPointerDown 会把 currentSelectedGameObject 设成自己，
            // 这是"事件真的被游戏侧处理了"的可观测证据（没有绑定回调的按钮也能看出来）
            data.Set("eventSystemAfter", RuntimeSupport.EventSystemJson());

            var hitPath = SceneQuery.BuildPath(hitObject.transform);
            if (string.IsNullOrEmpty(clickPath))
            {
                // 按到了东西但没人处理 click：多数是 Graphic 没开 raycastTarget 的父级问题，或纯装饰节点
                return CommandResult.Ok(
                    $"已按下 ({position.x:F0},{position.y:F0}) 命中 '{hitPath}'，但没有任何对象实现 IPointerClickHandler（点了等于没点）" +
                    (string.IsNullOrEmpty(warning) ? string.Empty : "；" + warning), data);
            }

            return CommandResult.Ok(
                $"已点击 '{clickPath}'（屏幕 {position.x:F0},{position.y:F0}，{clicks} 次）" +
                (string.IsNullOrEmpty(warning) ? string.Empty : "；" + warning), data);
        }

        /// <summary>
        /// 跳过射线，直接对 target 上的 <c>IPointerClickHandler</c> 触发点击。
        /// <para>用于"目标被透明遮罩挡住 / 在视口外但确实要触发"的场景。</para>
        /// </summary>
        private static CommandResult ClickDirect(
            JsonValue data, EventSystem system, PointerEventData pointer, Transform target, int clicks)
        {
            if (target == null)
            {
                return CommandResult.Fail("direct 模式需要 target：直接触发要对具体节点找 IPointerClickHandler", data);
            }

            var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target.gameObject);
            if (handler == null)
            {
                return CommandResult.Fail(
                    $"'{SceneQuery.BuildPath(target)}' 及其祖先都没有实现 IPointerClickHandler，" +
                    "direct 模式点不了（普通 Button 会实现，纯 Image 不会）。", data);
            }

            for (var i = 0; i < clicks; i++)
            {
                ExecuteEvents.Execute(handler, pointer, ExecuteEvents.pointerClickHandler);
            }

            var path = SceneQuery.BuildPath(handler.transform);
            data.Set("dispatch", BuildDispatch(string.Empty, string.Empty, path));
            data.Set("eventSystemAfter", RuntimeSupport.EventSystemJson());
            return CommandResult.Ok($"已直接触发 '{path}' 的点击（未经过射线，绕过遮挡）", data);
        }

        private static JsonValue BuildDispatch(string down, string up, string click)
        {
            var dispatch = JsonValue.NewObject();
            dispatch.Set("pointerDown", JsonValue.Of(down ?? string.Empty));
            dispatch.Set("pointerUp", JsonValue.Of(up ?? string.Empty));
            dispatch.Set("pointerClick", JsonValue.Of(click ?? string.Empty));
            return dispatch;
        }

        /// <summary>沿层级分发事件，返回实际处理者的路径（空串表示没人处理）</summary>
        private static string DispatchPath<T>(GameObject target, PointerEventData pointer,
            ExecuteEvents.EventFunction<T> handler) where T : IEventSystemHandler
        {
            var handledBy = ExecuteEvents.ExecuteHierarchy(target, pointer, handler);
            return handledBy != null ? SceneQuery.BuildPath(handledBy.transform) : string.Empty;
        }

        /// <summary>
        /// 命中对象与目标不在同一条分支时给警告——这是"点了没反应"最常见的原因（被弹窗/遮罩/别的元素挡住）。
        /// </summary>
        private static string OcclusionWarning(Transform target, GameObject hit)
        {
            if (target == null || hit == null)
            {
                return string.Empty;
            }

            var hitTransform = hit.transform;
            if (hitTransform == target || hitTransform.IsChildOf(target) || target.IsChildOf(hitTransform))
            {
                return string.Empty;
            }

            return $"点击位置命中的是 '{SceneQuery.BuildPath(hitTransform)}'，不在目标 " +
                   $"'{SceneQuery.BuildPath(target)}' 的分支上——目标可能被它遮挡，或坐标算偏了";
        }

        private static bool TryParseButton(string text, out PointerEventData.InputButton button)
        {
            switch (text)
            {
                case "left":
                    button = PointerEventData.InputButton.Left;
                    return true;
                case "right":
                    button = PointerEventData.InputButton.Right;
                    return true;
                case "middle":
                    button = PointerEventData.InputButton.Middle;
                    return true;
                default:
                    button = PointerEventData.InputButton.Left;
                    return false;
            }
        }
    }
}
