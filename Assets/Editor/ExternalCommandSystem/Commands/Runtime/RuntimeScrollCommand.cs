using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CivilRev.CommandSystem.Editor
{
    /// <summary>
    /// 模拟滚动列表：对 <see cref="ScrollRect"/> 注入 beginDrag / drag / endDrag 序列。
    ///
    /// <para>
    /// <b>为什么默认走"事件"而不是直接改 <c>verticalNormalizedPosition</c>：</b>
    /// 直接改位置只是把内容挪了，游戏里监听 <c>onValueChanged</c> 的逻辑（懒加载、页码显示、
    /// 吸附对齐）不会按"用户滚动"的语义被触发；而且惯性、边界回弹完全绕过。
    /// 事件注入让 <see cref="ScrollRect.OnDrag"/> 自己算位移，行为与真拖一致。
    /// </para>
    ///
    /// <para>
    /// <b>但没有惯性：</b>真实拖动的惯性由 ScrollRect 在多帧里按速度积分完成，而一条指令只能占一帧。
    /// 所以这里一次把 N 个 drag 事件发完，位移是准的、<c>onValueChanged</c> 也会响，但松手后的滑行没有。
    /// 需要"拖完继续滑"要等后续版本的跨帧执行能力，或改用 <c>mode=position</c> 直接给最终位置。
    /// </para>
    ///
    /// <para>
    /// <b>自动回落：</b>事件注入可能被引擎忽略（比如 Canvas 缩放换算不一致、布局还没重建），
    /// 因此发完事件后会**实测** normalizedPosition 有没有按预期变；没变且本可以变，就退回直接设位置，
    /// 并在结果里标 <c>fallback: true</c>——不会假装成功。
    /// </para>
    ///
    /// <para>
    /// 参数：target（ScrollRect 或其子节点）、direction（vertical/horizontal）、amount（normalized 位移量，1=从头滚到尾，正数=往后续内容滚）、
    /// mode（drag/position，默认 drag）、steps（默认 8）
    /// </para>
    /// </summary>
    [Command("runtime.scroll",
        Description = "模拟滚动/拖动列表：向 ScrollRect 注入拖拽事件（可回落为直接设位置）",
        Usage = "target: target, direction: vertical|horizontal, amount: float, mode: drag|position, steps: int",
        EditorOnly = true, Runtime = true)]
    public sealed class RuntimeScrollCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var data = JsonValue.NewObject();

            var targetSpec = context.Args.Get("target");
            if (targetSpec == null || targetSpec.IsNull)
            {
                return CommandResult.Fail("需要 target：ScrollRect 本身或其子节点（可用 runtime.hierarchy 找到列表路径）", data);
            }

            var target = RuntimeSupport.ResolveTarget(targetSpec, data, out var resolveError);
            if (target == null)
            {
                return CommandResult.Fail(resolveError, data);
            }

            var scroll = RuntimeSupport.FindInChain<ScrollRect>(target);
            if (scroll == null)
            {
                return CommandResult.Fail(
                    $"'{RuntimeSupport.Qualified(target)}' 及其祖先/子节点上没有 ScrollRect", data);
            }

            var content = scroll.content;
            if (content == null)
            {
                return CommandResult.Fail("该 ScrollRect 的 content 为空，无法滚动（先在编辑期把 content 绑好）", data);
            }

            var direction = (context.Args.GetString("direction", "vertical") ?? "vertical").Trim().ToLowerInvariant();
            var vertical = direction != "horizontal";
            if (direction != "vertical" && direction != "horizontal")
            {
                return CommandResult.Fail($"不支持的 direction：'{direction}'（可选 vertical / horizontal）", data);
            }

            if (vertical && !scroll.vertical)
            {
                return CommandResult.Fail("该 ScrollRect 没有开启 vertical，无法纵向滚动", data);
            }

            if (!vertical && !scroll.horizontal)
            {
                return CommandResult.Fail("该 ScrollRect 没有开启 horizontal，无法横向滚动", data);
            }

            var amount = context.Args.GetFloat("amount", 0.5f);
            if (Mathf.Approximately(amount, 0f))
            {
                return CommandResult.Fail("amount 不能为 0（正数=往后滚，负数=往前滚，1=滚一屏）", data);
            }

            var mode = (context.Args.GetString("mode", "drag") ?? "drag").Trim().ToLowerInvariant();
            if (mode != "drag" && mode != "position")
            {
                return CommandResult.Fail($"不支持的 mode：'{mode}'（可选 drag / position）", data);
            }

            var viewport = scroll.viewport != null ? scroll.viewport : (RectTransform)scroll.transform;
            if (content.rect.height <= 0f || content.rect.width <= 0f)
            {
                // 布局还没算过时 rect 是 0，量出来的位移会全部失效，先强制算一次
                LayoutRebuilder.ForceRebuildLayoutImmediate(content);
            }

            var before = Normalized(scroll, vertical);
            // 两个方向的 normalizedPosition 定义相反，别写成同一个表达式：
            // 纵向 1=顶 / 0=底，「往后看」是数值变小；横向 0=左 / 1=右，「往后看」是数值变大。
            // 写反时 mode=position 与两条回落路径都会往回滚，而拖拽主路径（stepVector 按方向分开算）
            // 反而是对的——症状只在回落时出现，一次性冒烟很难发现。
            var desired = vertical ? Mathf.Clamp01(before - amount) : Mathf.Clamp01(before + amount);
            data.Set("scrollRect", JsonValue.Of(SceneQuery.BuildPath(scroll.transform)));
            data.Set("scrollRectQualified", JsonValue.Of(SceneScope.QualifiedPath(scroll.transform)));
            data.Set("content", JsonValue.Of(SceneQuery.BuildPath(content)));
            data.Set("direction", JsonValue.Of(direction));
            data.Set("amount", JsonValue.Of(Mathf.Round(amount * 1000f) / 1000f));
            data.Set("mode", JsonValue.Of(mode));
            data.Set("normalizedBefore", JsonValue.Of(Round4(before)));

            if (mode == "position")
            {
                SetNormalized(scroll, vertical, desired);
                var afterDirect = Normalized(scroll, vertical);
                data.Set("normalizedAfter", JsonValue.Of(Round4(afterDirect)));
                data.Set("moved", JsonValue.Of(Round4(afterDirect - before)));
                return CommandResult.Ok(
                    $"已直接设置 {direction} normalizedPosition：{before:F3} → {afterDirect:F3}（未经拖拽事件，onValueChanged 的拖动语义不成立）",
                    data);
            }

            var steps = Mathf.Clamp(context.Args.GetInt("steps", 8), 1, 64);
            var canvas = scroll.GetComponentInParent<Canvas>();
            var scale = canvas != null ? canvas.scaleFactor : 1f;
            var contentSize = vertical ? content.rect.height : content.rect.width;
            var viewportSize = vertical ? viewport.rect.height : viewport.rect.width;
            // amount 是 normalizedPosition 的位移量（1 = 从头滚到尾），因此换算基准是**可滚动范围**，
            // 不是视口高度——用视口高度会让"滚半屏"实际滚出 2 倍，实测踩过。
            var scrollable = Mathf.Max(0f, contentSize - viewportSize);
            var screenPixels = scrollable * amount * scale;

            string error;
            var system = RuntimeSupport.RequireEventSystem(out error);
            if (system == null)
            {
                // 拖拽事件不依赖 EventSystem 的射线，但 PointerEventData 需要它做上下文；没有就退回直接设位置
                SetNormalized(scroll, vertical, desired);
                var afterNoEs = Normalized(scroll, vertical);
                data.Set("normalizedAfter", JsonValue.Of(Round4(afterNoEs)));
                data.Set("moved", JsonValue.Of(Round4(afterNoEs - before)));
                data.Set("fallback", JsonValue.Of(true));
                data.Set("warning", JsonValue.Of(error + " 已退化为直接设置位置。"));
                return CommandResult.Ok(
                    $"没有 EventSystem，已退化为直接设置 {direction} normalizedPosition：{before:F3} → {afterNoEs:F3}", data);
            }

            var center = RectTransformUtility.WorldToScreenPoint(
                canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null,
                viewport.TransformPoint(viewport.rect.center));

            var pointer = new PointerEventData(system)
            {
                position = center,
                pressPosition = center,
                button = PointerEventData.InputButton.Left,
                pointerId = -1,
                dragging = true
            };

            ExecuteEvents.Execute(scroll.gameObject, pointer, ExecuteEvents.beginDragHandler);

            // 关键：ScrollRect.OnDrag 用「光标当前位置 − OnBeginDrag 时的光标位置」算位移，
            // 不读 eventData.delta。所以必须真的把 position 一步步挪过去，只改 delta 是原地不动（实测踩过）。
            // 符号不对称：Unity 屏幕坐标 y 轴朝上，往下翻内容（normalized 变小）要**往上**拖；
            // 横向则是往左拖才显示右侧内容。方向写反会被 Clamped 直接吃掉，表现成"拖了没动"。
            var stepVector = vertical
                ? new Vector2(0f, screenPixels / steps)
                : new Vector2(-screenPixels / steps, 0f);
            for (var i = 0; i < steps; i++)
            {
                pointer.position = pointer.position + stepVector;
                pointer.delta = stepVector;
                ExecuteEvents.Execute(scroll.gameObject, pointer, ExecuteEvents.dragHandler);
            }

            ExecuteEvents.Execute(scroll.gameObject, pointer, ExecuteEvents.endDragHandler);

            var after = Normalized(scroll, vertical);
            var moved = after - before;
            var expected = desired - before;
            var ignored = Mathf.Abs(expected) > 1e-3f && Mathf.Abs(moved) < Mathf.Abs(expected) * 0.2f;

            if (ignored)
            {
                // 引擎没按事件位移（缩放换算、布局未重建、content 小于 viewport 等），如实回落到直接设位置
                SetNormalized(scroll, vertical, desired);
                var afterFallback = Normalized(scroll, vertical);
                data.Set("normalizedAfter", JsonValue.Of(Round4(afterFallback)));
                data.Set("moved", JsonValue.Of(Round4(afterFallback - before)));
                data.Set("fallback", JsonValue.Of(true));
                data.Set("screenPixels", JsonValue.Of(Mathf.Round(screenPixels)));
                data.Set("canvasScale", JsonValue.Of(Round4(scale)));
                data.Set("warning", JsonValue.Of(
                    $"拖拽事件没有产生位移（期望 {expected:F3}，实际 {moved:F3}），已退化为直接设置位置。" +
                    "常见原因：content 尺寸未超出 viewport、canvas 缩放换算不一致、布局未重建。"));
                return CommandResult.Ok(
                    $"拖拽未生效，已退化为直接设置 {direction}：{before:F3} → {afterFallback:F3}", data);
            }

            data.Set("normalizedAfter", JsonValue.Of(Round4(after)));
            data.Set("moved", JsonValue.Of(Round4(moved)));
            data.Set("fallback", JsonValue.Of(false));
            data.Set("steps", JsonValue.Of(steps));
            data.Set("screenPixels", JsonValue.Of(Mathf.Round(screenPixels)));
            data.Set("canvasScale", JsonValue.Of(Round4(scale)));

            return CommandResult.Ok(
                $"已拖动 '{SceneQuery.BuildPath(scroll.transform)}' {direction} {amount:F2} 屏：" +
                $"{before:F3} → {after:F3}（{steps} 步，无惯性）", data);
        }

        private static float Normalized(ScrollRect scroll, bool vertical)
        {
            return vertical ? scroll.verticalNormalizedPosition : scroll.horizontalNormalizedPosition;
        }

        private static void SetNormalized(ScrollRect scroll, bool vertical, float value)
        {
            if (vertical)
            {
                scroll.verticalNormalizedPosition = value;
            }
            else
            {
                scroll.horizontalNormalizedPosition = value;
            }

            Canvas.ForceUpdateCanvases();
        }

        private static float Round4(float value)
        {
            return Mathf.Round(value * 10000f) / 10000f;
        }
    }
}
