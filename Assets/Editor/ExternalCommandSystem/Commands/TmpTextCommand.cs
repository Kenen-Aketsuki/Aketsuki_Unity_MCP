using TMPro;
using UnityEngine;

namespace CivilRev.CommandSystem.Editor
{
    /// <summary>
    /// 创建一个 TextMeshPro 文本节点
    /// <para>
    /// 与 <c>ui.text</c>（旧的 UnityEngine.UI.Text）的区别：TMP 渲染更锐利，
    /// 且项目里已经有思源宋体的 TMP 字体资源，中文排版效果明显更好。
    /// 不指定 <c>font</c> 时按下面的默认值自动查找。
    /// </para>
    /// <para>
    /// <b>存在即复用</b>：父节点下已有同名子节点时，改成"改属性"而不是新建一个——
    /// 换字体、改文案、改颜色这类事后调整都走这条路，本指令因此是幂等的。
    /// 复用模式下<b>只改明确传了的属性</b>，且只有传了布局参数时才会重新摆放。
    /// </para>
    /// <para>
    /// 参数：name、parent/canvas、text、fontSize、color、align、bold、wrap、font（字体资源路径）、布局
    /// </para>
    /// </summary>
    [Command("ui.tmpText", Description = "创建/更新一个 TextMeshPro 文本节点（同名已存在则改属性）",
        Usage = "name: string, parent: string, text: string, fontSize: number, color: string|array, "
                + "align: string, font: string, bold: bool, wrap: bool, select: bool",
        EditorOnly = true)]
    public sealed class CreateTmpTextCommand : ICommand
    {
        /// <summary>默认字体资源所在目录</summary>
        public const string DefaultFontFolder = "Assets/TextMesh Pro/Fonts/FontAssets";

        /// <summary>默认字体资源文件名（项目已有的思源宋体）</summary>
        public const string DefaultFontFile = "SourceHanSerifCN-Regular SDF.asset";

        public CommandResult Execute(CommandContext context)
        {
            var args = context.Args;
            var parent = CreateImageCommand.RequireParent(context);
            var name = args.GetString("name", "Text");

            // 同名节点已存在 → 复用它并改属性，而不是再建一个。
            // 于是"换字体 / 改文案 / 改颜色"这类事后调整也走这一条指令，不必另造一条"修改"指令；
            // 副作用是本指令变成幂等的：同一份指令文件重跑不会堆出重名节点。
            var existing = FindExistingChild(parent, name);
            var reuse = existing != null;

            var rect = reuse
                ? existing.GetComponent<RectTransform>()
                : UiNodeFactory.CreateRect(name, parent);

            // 复用时只在确实传了布局参数才重新摆放，否则会把已有的摆放重置成全拉伸
            if (!reuse || UiNodeFactory.HasLayoutArgs(args))
            {
                UiNodeFactory.ApplyLayout(rect, args);
            }

            TextMeshProUGUI text;
            if (reuse)
            {
                text = rect.GetComponent<TextMeshProUGUI>();
                if (text == null)
                {
                    text = rect.gameObject.AddComponent<TextMeshProUGUI>();
                }

                // 只改这次明确传了的属性：不传 text 就不会把已有文案清成空串
                ConfigureTmp(text, args, context.CommandId);
            }
            else
            {
                text = AddTmp(rect.gameObject, args, args.GetString("text", string.Empty),
                    args.GetFloat("fontSize", 48f), context.CommandId);
            }

            if (reuse)
            {
                UiNodeFactory.FinalizeChange(rect.gameObject, text, args.GetBool("select", false));
                return CommandResult.Ok($"已更新 TMP 文本 '{name}'（{text.text}）", name);
            }

            UiNodeFactory.Finalize(rect.gameObject, args.GetBool("select", false));
            return CommandResult.Ok($"已创建 TMP 文本 '{name}'（{text.text}）", name);
        }

        /// <summary>
        /// 在父节点下按名字（支持 "A/B" 相对路径）找已存在的子节点
        /// <para><c>Transform.Find</c> 能命中 inactive 节点，正好符合"复用"的预期。</para>
        /// </summary>
        private static Transform FindExistingChild(Transform parent, string name)
        {
            if (parent == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            return parent.Find(name);
        }

        /// <summary>
        /// 按参数逐项配置一个 TMP 文本——**只改明确传了的属性**
        /// <para>
        /// 与 <see cref="AddTmp"/>（新建时用，每个属性都有默认值）的分工：
        /// 复用已有节点时若也套默认值，只传一个 font 就会把文案、字号、颜色一并重置。
        /// </para>
        /// </summary>
        internal static void ConfigureTmp(TextMeshProUGUI text, CommandArgs args, string commandId)
        {
            if (args.Has("text"))
            {
                text.text = args.GetString("text", string.Empty);
            }

            if (args.Has("fontSize"))
            {
                text.fontSize = args.GetFloat("fontSize", text.fontSize);
            }

            if (args.Has("color"))
            {
                text.color = UiNodeFactory.ParseColor(args.Get("color"), text.color);
            }

            if (args.Has("align"))
            {
                text.alignment = ParseAlignment(args.GetString("align", null));
            }

            if (args.Has("bold"))
            {
                text.fontStyle = args.GetBool("bold", false) ? FontStyles.Bold : FontStyles.Normal;
            }

            if (args.Has("raycastTarget"))
            {
                text.raycastTarget = args.GetBool("raycastTarget", false);
            }

            if (args.Has("wrap"))
            {
                text.enableWordWrapping = args.GetBool("wrap", false);
            }

            var fontPath = args.GetString("font", null);
            if (!string.IsNullOrEmpty(fontPath))
            {
                var font = ResolveFont(fontPath, commandId);
                if (font != null)
                {
                    text.font = font;
                }
            }
        }

        /// <summary>
        /// 挂一段 TMP 文本并按参数配置好——供本指令与其它需要文字的指令（如 <c>ui.button</c> 的标签）共用
        /// <para>不调用 <see cref="UiNodeFactory.Finalize"/>，由调用方决定何时收尾（建整棵树时只需收尾一次）。</para>
        /// </summary>
        /// <param name="alignOverride">强制对齐方式；为空时用 args 里的 align 参数</param>
        /// <param name="colorOverride">强制颜色；为空时用 args 里的 color 参数。
        /// 调用方的 color 若另有含义（如按钮底色），必须通过这个参数把文字色传进来</param>
        /// <param name="fontPathOverride">强制字体路径；为空时用 args 里的 font 参数</param>
        internal static TextMeshProUGUI AddTmp(
            GameObject go,
            CommandArgs args,
            string content,
            float fontSize,
            string commandId,
            TextAlignmentOptions? alignOverride = null,
            Color? colorOverride = null,
            string fontPathOverride = null)
        {
            var text = go.AddComponent<TextMeshProUGUI>();
            text.text = content ?? string.Empty;
            text.fontSize = fontSize;
            text.color = colorOverride ?? UiNodeFactory.ParseColor(args.Get("color"), new Color(0.24f, 0.19f, 0.29f, 1f));
            text.alignment = alignOverride ?? ParseAlignment(args.GetString("align", null));
            text.fontStyle = args.GetBool("bold", false) ? FontStyles.Bold : FontStyles.Normal;
            text.raycastTarget = args.GetBool("raycastTarget", false);
            text.enableWordWrapping = args.GetBool("wrap", false);
            text.overflowMode = TextOverflowModes.Overflow;

            var font = ResolveFont(fontPathOverride ?? args.GetString("font", null), commandId);
            if (font != null)
            {
                text.font = font;
            }

            return text;
        }

        /// <summary>
        /// 解析 TMP 字体：显式路径 → 项目默认（思源宋体 SDF）→ TMP 全局默认
        /// </summary>
        internal static TMP_FontAsset ResolveFont(string explicitPath, string commandId)
        {
            if (!string.IsNullOrEmpty(explicitPath))
            {
                return UiAssetUtil.LoadAsset<TMP_FontAsset>(explicitPath, commandId, "font");
            }

            var found = UiAssetUtil.FindAssetPathByName(DefaultFontFile, DefaultFontFolder);
            if (!string.IsNullOrEmpty(found))
            {
                var font = UiAssetUtil.LoadAsset<TMP_FontAsset>(found, commandId, "font");
                if (font != null)
                {
                    return font;
                }
            }

            // 退到 TMP 全局默认字体；再拿不到就让 TMP 自己报错，避免这里静默生成一个不可见的文本
            var fallback = TMP_Settings.defaultFontAsset;
            if (fallback == null)
            {
                CommandLog.Warning(
                    "找不到可用的 TMP 字体资源，文本可能不显示。请用 font 参数指定字体，或检查 TMP 的默认字体设置。");
            }
            return fallback;
        }

        private static TextAlignmentOptions ParseAlignment(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return TextAlignmentOptions.Center;
            }

            switch (text.Trim().ToLowerInvariant())
            {
                case "left":
                case "middleleft":
                    return TextAlignmentOptions.Left;
                case "right":
                case "middleright":
                    return TextAlignmentOptions.Right;
                case "top":
                case "uppercenter":
                    return TextAlignmentOptions.Top;
                case "topleft":
                    return TextAlignmentOptions.TopLeft;
                case "topright":
                    return TextAlignmentOptions.TopRight;
                case "bottom":
                case "lowercenter":
                    return TextAlignmentOptions.Bottom;
                case "bottomleft":
                    return TextAlignmentOptions.BottomLeft;
                case "bottomright":
                    return TextAlignmentOptions.BottomRight;
                default:
                    return TextAlignmentOptions.Center;
            }
        }
    }
}
