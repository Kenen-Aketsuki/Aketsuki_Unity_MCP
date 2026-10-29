using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 给运行时输入框写值，并按需触发"编辑完成 / 提交"回调。
    ///
    /// <para>
    /// 覆盖两套实现：<see cref="TMP_InputField"/>（TextMeshPro）与 <see cref="InputField"/>（UGUI 原生）。
    /// 优先 TMP——同一节点上两套都挂时，游戏实际在用的几乎总是 TMP。
    /// </para>
    ///
    /// <para>
    /// <b>为什么改完还要手动 fire 回调：</b>直接赋 <c>text</c> 只会触发 <c>onValueChanged</c>
    /// （两套实现的 setter 内部都会 fire），而 <c>onEndEdit</c>/<c>onSubmit</c> 在真实交互里是
    /// "失焦"和"按回车"才触发的。很多游戏的提交逻辑（搜索、登录、改名）只挂在 <c>onEndEdit</c> 上，
    /// 所以这里显式 fire，等价于"填完按了回车"。
    /// </para>
    ///
    /// <para>
    /// 注意：这是**直接赋值**，不是逐字符模拟键盘输入。挂在 <c>onValueChanged</c> 上做逐字校验、
    /// 输入法联想、自动补全的逻辑只会收到一次最终值。需要逐字行为的话属于后续版本的事。
    /// </para>
    ///
    /// <para>参数：target、text（必填）、append（默认 false，追加而非覆盖）、submit（默认 true）</para>
    /// </summary>
    [Command("runtime.input",
        Description = "给运行时输入框写值（TMP_InputField / InputField），可触发提交回调",
        Usage = "target: string|object, text: string, append: bool, submit: bool",
        EditorOnly = true, Runtime = true)]
    public sealed class RuntimeInputCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var data = JsonValue.NewObject();

            var targetSpec = context.Args.Get("target");
            if (targetSpec == null || targetSpec.IsNull)
            {
                return CommandResult.Fail("需要 target：输入框节点（可用 runtime.hierarchy 加 name 过滤找到）", data);
            }

            var target = RuntimeSupport.ResolveTarget(targetSpec, data, out var resolveError);
            if (target == null)
            {
                return CommandResult.Fail(resolveError, data);
            }

            var text = context.Args.GetString("text", string.Empty) ?? string.Empty;
            var append = context.Args.GetBool("append", false);
            var submit = context.Args.GetBool("submit", true);

            var tmpField = RuntimeSupport.FindInChain<TMP_InputField>(target);
            if (tmpField != null)
            {
                return ApplyTmp(data, tmpField, text, append, submit);
            }

            var legacyField = RuntimeSupport.FindInChain<InputField>(target);
            if (legacyField != null)
            {
                return ApplyLegacy(data, legacyField, text, append, submit);
            }

            return CommandResult.Fail(
                $"'{RuntimeSupport.Qualified(target)}' 及其祖先/子节点上没有 TMP_InputField 或 InputField；" +
                "如果这是自定义输入组件，请用 component.setField 直接写它的字段。", data);
        }

        private static CommandResult ApplyTmp(
            JsonValue data, TMP_InputField field, string text, bool append, bool submit)
        {
            if (!field.interactable || field.readOnly)
            {
                return CommandResult.Fail(
                    $"输入框 '{SceneQuery.BuildPath(field.transform)}' 处于不可交互状态" +
                    $"（interactable={field.interactable}, readOnly={field.readOnly}），真实用户也输不进去。", data);
            }

            var before = field.text ?? string.Empty;
            var value = append ? before + text : text;
            // setter 内部会 fire onValueChanged，这里不重复触发
            field.text = value;
            field.onEndEdit.Invoke(value);
            if (submit)
            {
                field.onSubmit.Invoke(value);
            }

            data.Set("fieldType", JsonValue.Of("TMP_InputField"));
            data.Set("path", JsonValue.Of(SceneQuery.BuildPath(field.transform)));
            data.Set("qualifiedPath", JsonValue.Of(SceneScope.QualifiedPath(field.transform)));
            data.Set("textBefore", JsonValue.Of(before));
            data.Set("textAfter", JsonValue.Of(field.text ?? string.Empty));
            data.Set("appended", JsonValue.Of(append));
            data.Set("endedEdit", JsonValue.Of(true));
            data.Set("submitted", JsonValue.Of(submit));

            return CommandResult.Ok(
                $"已写入输入框 '{SceneQuery.BuildPath(field.transform)}'：'{before}' → '{field.text}'" +
                (submit ? "，并触发了提交回调" : "，仅触发编辑完成回调"), data);
        }

        private static CommandResult ApplyLegacy(
            JsonValue data, InputField field, string text, bool append, bool submit)
        {
            if (!field.interactable || field.readOnly)
            {
                return CommandResult.Fail(
                    $"输入框 '{SceneQuery.BuildPath(field.transform)}' 处于不可交互状态" +
                    $"（interactable={field.interactable}, readOnly={field.readOnly}），真实用户也输不进去。", data);
            }

            var before = field.text ?? string.Empty;
            var value = append ? before + text : text;
            field.text = value;
            field.onEndEdit.Invoke(value);

            data.Set("fieldType", JsonValue.Of("InputField"));
            data.Set("path", JsonValue.Of(SceneQuery.BuildPath(field.transform)));
            data.Set("qualifiedPath", JsonValue.Of(SceneScope.QualifiedPath(field.transform)));
            data.Set("textBefore", JsonValue.Of(before));
            data.Set("textAfter", JsonValue.Of(field.text ?? string.Empty));
            data.Set("appended", JsonValue.Of(append));
            data.Set("endedEdit", JsonValue.Of(true));
            data.Set("submitted", JsonValue.Of(false));

            // UGUI 原生 InputField 没有 onSubmit 事件，提交通常挂 onEndEdit，语义已在上面覆盖
            var hint = submit ? "；原生 InputField 没有 onSubmit，提交逻辑若挂在 onEndEdit 上已触发" : string.Empty;
            return CommandResult.Ok(
                $"已写入输入框 '{SceneQuery.BuildPath(field.transform)}'：'{before}' → '{field.text}'{hint}", data);
        }
    }
}
