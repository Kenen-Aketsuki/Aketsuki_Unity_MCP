using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 确保场景中存在 Canvas（已存在则复用），可选同时创建 EventSystem
    /// <para>参数：name、reuseExisting（默认 true）、createEventSystem（默认 true）、referenceResolution（默认 1920x1080）、select</para>
    /// </summary>
    [Command("ui.canvas", Description = "确保场景中存在 Canvas（含 CanvasScaler 与 GraphicRaycaster）",
        Usage = "name: string, reuseExisting: bool, createEventSystem: bool, referenceResolution: [w,h]",
        EditorOnly = true)]
    public sealed class EnsureCanvasCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var name = context.Args.GetString("name", "Canvas");
            var reuse = context.Args.GetBool("reuseExisting", true);
            var resolution = context.Args.GetVector2("referenceResolution", new Vector2(1920f, 1080f));
            var createEventSystem = context.Args.GetBool("createEventSystem", true);

            var canvas = UiNodeFactory.EnsureCanvas(name, reuse, resolution);

            if (createEventSystem)
            {
                UiNodeFactory.EnsureEventSystem("EventSystem");
            }

            UiNodeFactory.Finalize(canvas.gameObject, context.Args.GetBool("select", true));

            // 记录画布的名称与完整层级路径，供后续指令用 parent 参数引用
            // 注意：复用的可能是场景里已有的、叫别的名字的 Canvas，因此后续指令
            // 更推荐写 "parent": "type:Canvas" 按类型查找，而不是写死名字
            var path = UiNodeFactory.BuildPath(canvas.transform);
            context.SetVariable("ui.canvas", canvas.name);
            context.SetVariable("ui.canvasPath", path);
            return CommandResult.Ok($"Canvas 就绪：{path}", canvas.name);
        }
    }

    /// <summary>
    /// 工程结构挂钩（可选，反射约定）：与工程运行期代码保持**编译期零耦合**。
    /// <para>
    /// 工程若想让自己的 MainUIRoot 结构定义成为唯一事实来源，提供一个静态类即可：
    /// <code>
    /// public static class MainUIRootBuilder  // 任意命名空间，按类型名发现
    /// {
    ///     public static Canvas Ensure(string name, Vector2 referenceResolution,
    ///                                 bool reuseExisting, bool createEventSystem) { ... }
    /// }
    /// </code>
    /// 本指令解析到该类型就用它；解析不到（比如导入到没有此类的工程）退回
    /// <see cref="CreateUIRootCommand.EnsureDefault"/> 的内置默认结构。不引用、不依赖、不随包分发。
    /// </para>
    /// </summary>
    internal static class MainUIRootHook
    {
        private const string HookTypeName = "MainUIRootBuilder";
        private const string HookMethodName = "Ensure";

        private static System.Reflection.MethodInfo _ensure;
        private static bool _resolved;

        /// <summary>解析工程挂钩方法；找不到或签名不符返回 null（结果缓存）。</summary>
        internal static System.Reflection.MethodInfo Resolve()
        {
            if (_resolved) return _ensure;
            _resolved = true;

            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                // 跳过 Unity / 系统程序集：挂钩只可能来自工程代码
                var name = asm.GetName().Name;
                if (name.StartsWith("Unity") || name.StartsWith("System") || name.StartsWith("mscorlib")
                    || name.StartsWith("netstandard") || name.StartsWith("Mono.") || name.StartsWith("mscorlib"))
                {
                    continue;
                }

                System.Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (System.Exception)
                {
                    continue;   // ReflectionTypeLoadException 等：这个程序集不参与发现
                }

                foreach (var type in types)
                {
                    if (type.Name != HookTypeName) continue;
                    // 静态类 = abstract + sealed
                    if (!type.IsAbstract || !type.IsSealed) continue;

                    var method = type.GetMethod(HookMethodName,
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                        null,
                        new[] { typeof(string), typeof(Vector2), typeof(bool), typeof(bool) },
                        null);
                    if (method != null && typeof(Canvas).IsAssignableFrom(method.ReturnType))
                    {
                        _ensure = method;
                        return _ensure;
                    }
                }
            }

            return _ensure;
        }
    }

    /// <summary>
    /// 生成 UI 根节点 MainUIRoot：一块 ScreenSpaceOverlay 画布，Activity / Dialog / Panel 都挂在它下面
    /// <para>
    /// 结构定义默认使用内置实现；工程若提供 <see cref="MainUIRootHook"/> 约定的
    /// <c>MainUIRootBuilder.Ensure</c>，则转调工程侧定义（结构唯一事实来源在工程，
    /// 与运行期 UIManager.EnsureUIRoot 共用同一份代码）。本指令对工程代码**零编译依赖**。
    /// </para>
    /// <para>
    /// 参数：name（默认 MainUIRoot）、referenceResolution（默认 1920x1080）、
    /// createEventSystem（默认 true）、reuseExisting（默认 true）、select
    /// </para>
    /// </summary>
    [Command("ui.uiRoot", Description = "生成 UI 根节点 MainUIRoot（自带 Canvas；工程提供 MainUIRootBuilder 约定时优先用工程定义）",
        Usage = "name: string, referenceResolution: [w,h], createEventSystem: bool, reuseExisting: bool",
        EditorOnly = true)]
    public sealed class CreateUIRootCommand : ICommand
    {
        /// <summary>内置默认根节点名：与常见运行期约定（uiRoot 指向 MainUIRoot）保持一致</summary>
        public const string DefaultName = "MainUIRoot";

        public CommandResult Execute(CommandContext context)
        {
            var name = context.Args.GetString("name", DefaultName);
            var resolution = context.Args.GetVector2("referenceResolution", new Vector2(1920f, 1080f));
            var reuse = context.Args.GetBool("reuseExisting", true);
            var createEventSystem = context.Args.GetBool("createEventSystem", true);

            // created 判定：复用到一个带 Canvas 的同名节点 = 没新建；其余都算新建。
            // 挂钩路径下同样以「调用前场景里是否已有可用根节点」为准，挂钩内部自行决定复用细节。
            Transform existing = reuse ? SceneQuery.Find(name) : null;
            var created = !(existing != null && existing.GetComponent<Canvas>() != null);

            Canvas canvas = null;
            var hook = MainUIRootHook.Resolve();
            if (hook != null)
            {
                canvas = (Canvas)hook.Invoke(null, new object[] { name, resolution, reuse, createEventSystem });
            }

            if (canvas == null)
            {
                // EnsureDefault 复用的条件与上面 created 的初始判定完全一致：
                // reuse 且同名节点带 Canvas → 返回既有画布（created 保持 false）；否则新建（true）
                canvas = EnsureDefault(name, resolution, reuse, existing, createEventSystem);
            }

            // 记下主画布，让后续指令的 "parent": "type:Canvas" 落到这一块上
            UiNodeFactory.RememberCanvas(canvas);

            if (created)
            {
                UiNodeFactory.ApplyLayout(canvas.gameObject.GetComponent<RectTransform>(), context.Args);
            }

            UiNodeFactory.Finalize(canvas.gameObject, context.Args.GetBool("select", true));

            var path = UiNodeFactory.BuildPath(canvas.transform);
            context.SetVariable("ui.uiRoot", canvas.name);
            context.SetVariable("ui.uiRootPath", path);

            return CommandResult.Ok(
                (created ? "已创建" : "已复用") + $" UI 根节点 {path}"
                + $"（ScreenSpaceOverlay，参考分辨率 {resolution.x}x{resolution.y}）",
                canvas.name);
        }

        /// <summary>
        /// 内置默认结构（自包含，无工程依赖）：场景根节点 + Canvas + CanvasScaler + GraphicRaycaster。
        /// 复用已有节点时只取其 Canvas，不改动已有设置。
        /// </summary>
        private static Canvas EnsureDefault(string name, Vector2 resolution, bool reuse,
            Transform existing, bool createEventSystem)
        {
            if (reuse && existing != null)
            {
                var reused = existing.GetComponent<Canvas>();
                if (reused != null) return reused;
            }

            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(null);

            var canvas = go.AddComponent<Canvas>();
            var scaler = go.AddComponent<CanvasScaler>();
            go.AddComponent<GraphicRaycaster>();

            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 0;

            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = resolution;
            scaler.matchWidthOrHeight = 0.5f;

            if (createEventSystem)
            {
                UiNodeFactory.EnsureEventSystem("EventSystem", canvas.transform);
            }

            return canvas;
        }
    }

    /// <summary>
    /// 创建一个 Image 节点：给 sprite 显示切图，不给则是一块纯色
    /// <para>
    /// 参数：name、parent/canvas、sprite（Assets 路径）、nativeSize、type（simple/sliced/tiled/filled）、
    /// preserveAspect、color（着色）、raycastTarget、布局（size+anchoredPosition / polar / 拉伸 / rotation / scale）
    /// </para>
    /// </summary>
    [Command("ui.image", Description = "创建一个 Image 节点（切图或纯色）",
        Usage = "name: string, parent: string, sprite: string, nativeSize: bool, type: string, "
                + "color: string|array, size:[w,h], anchoredPosition:[x,y], polar:[r,deg], "
                + "preserveAspect: bool, raycastTarget: bool, select: bool",
        EditorOnly = true)]
    public sealed class CreateImageCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var parent = RequireParent(context);
            var name = context.Args.GetString("name", "Image");

            var rect = UiNodeFactory.CreateRect(name, parent);
            UiNodeFactory.ApplyLayout(rect, context.Args);

            var spritePath = context.Args.GetString("sprite", null);
            var sprite = string.IsNullOrEmpty(spritePath)
                ? null
                : UiAssetUtil.LoadSprite(spritePath, context.CommandId, "sprite");

            var image = UiNodeFactory.AddImage(
                rect.gameObject,
                sprite,
                UiNodeFactory.ParseColor(context.Args.Get("color"), Color.white),
                UiNodeFactory.ParseImageType(context.Args.GetString("type", null), Image.Type.Simple),
                context.Args.GetBool("preserveAspect", false),
                context.Args.GetBool("raycastTarget", true),
                context.Args.GetBool("nativeSize", false));

            UiNodeFactory.Finalize(rect.gameObject, context.Args.GetBool("select", false));

            var description = sprite != null ? sprite.name : "纯色";
            return CommandResult.Ok($"已创建 Image '{name}'（{description}）", name);
        }

        internal static Transform RequireParent(CommandContext context)
        {
            var parent = UiNodeFactory.ResolveParent(context.Args);
            if (parent == null)
            {
                throw new CommandArgumentException(context.CommandId, "parent",
                    "场景中不存在 Canvas，请先执行 ui.canvas 或指定 parent / canvas 参数");
            }
            return parent;
        }
    }

    /// <summary>
    /// 创建一个空的 RectTransform 容器节点（不挂任何图形组件）
    /// <para>参数：name、parent/canvas、布局、select。用来给一组子节点做统一的缩放/位移锚点。</para>
    /// </summary>
    [Command("ui.node", Description = "创建一个空容器节点（RectTransform，无图形组件）",
        Usage = "name: string, parent: string, size:[w,h], anchoredPosition:[x,y], scale: number|array, select: bool",
        EditorOnly = true)]
    public sealed class CreateNodeCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var parent = CreateImageCommand.RequireParent(context);
            var name = context.Args.GetString("name", "Node");

            var rect = UiNodeFactory.CreateRect(name, parent);
            UiNodeFactory.ApplyLayout(rect, context.Args);

            UiNodeFactory.Finalize(rect.gameObject, context.Args.GetBool("select", false));
            return CommandResult.Ok($"已创建容器 '{name}'", name);
        }
    }

    /// <summary>
    /// 创建一个 Text 节点（使用 Unity 内置字体，零资源依赖）
    /// <para>参数：name、parent/canvas、text、fontSize、color、align（left/center/right/top/bottom）、bold、布局</para>
    /// </summary>
    [Command("ui.text", Description = "创建一个 Text 节点",
        Usage = "name: string, parent: string, text: string, fontSize: int, color: string|array, "
                + "align: string, bold: bool, font: string, raycastTarget: bool, select: bool",
        EditorOnly = true)]
    public sealed class CreateTextCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var parent = CreateImageCommand.RequireParent(context);
            var name = context.Args.GetString("name", "Text");

            var rect = UiNodeFactory.CreateRect(name, parent);
            UiNodeFactory.ApplyLayout(rect, context.Args);

            var text = UiNodeFactory.AddText(
                rect.gameObject,
                context.Args.GetString("text", string.Empty),
                context.Args.GetInt("fontSize", 24),
                UiNodeFactory.ParseColor(context.Args.Get("color"), new Color(0.2f, 0.2f, 0.2f)),
                UiNodeFactory.ParseAnchor(context.Args.GetString("align", null), TextAnchor.MiddleCenter),
                context.Args.GetBool("bold", false),
                context.Args.GetBool("raycastTarget", false));

            // 指定字体资源时替换掉内置字体（中文建议指定，例如思源宋体）
            var fontPath = context.Args.GetString("font", null);
            if (!string.IsNullOrEmpty(fontPath))
            {
                text.font = UiAssetUtil.LoadFont(fontPath, context.CommandId, "font");
            }

            UiNodeFactory.Finalize(rect.gameObject, context.Args.GetBool("select", false));
            return CommandResult.Ok($"已创建 Text '{name}'", name);
        }
    }

    /// <summary>
    /// 创建一个 Button 节点（Image + Button + 子标签），仅搭建结构，不绑定任何点击逻辑
    /// <para>
    /// 参数：name、parent/canvas、label、size、anchoredPosition、color（按钮底色）、
    /// textColor（标签色）、fontSize、bold、tmp / tmpFont（标签改用 TMP 渲染）、font（内置 Text 字体）
    /// </para>
    /// <para>
    /// 标签默认用内置 Text（零资源依赖），但内置字体在中文上会掉字——
    /// 中文标签请加 <c>"tmp": true</c>（用项目默认字体）或 <c>"tmpFont": "路径"</c>（指定字体）。
    /// 其余布局参数与 <c>ui.image</c> 完全一致，因此按钮可以像普通块一样锚到任意角落。
    /// </para>
    /// </summary>
    [Command("ui.button", Description = "创建一个 Button 节点（仅结构，不接逻辑）",
        Usage = "name: string, parent: string, label: string, size:[w,h], anchoredPosition:[x,y], "
                + "color: string|array, textColor: string|array, fontSize: int, bold: bool, "
                + "tmp: bool, tmpFont: string, font: string, select: bool",
        EditorOnly = true)]
    public sealed class CreateButtonCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var args = context.Args;
            var parent = CreateImageCommand.RequireParent(context);
            var name = args.GetString("name", "Button");
            var label = args.GetString("label", "按钮");

            var rect = UiNodeFactory.CreateRect(name, parent);
            UiNodeFactory.ApplyLayout(rect, args);

            var image = UiNodeFactory.AddImage(rect.gameObject,
                UiNodeFactory.ParseColor(args.Get("color"), new Color(0.18f, 0.5f, 0.97f)));

            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            // 不注册 onClick：本指令只负责建结构

            // 标签文字：填满按钮
            var labelRect = UiNodeFactory.CreateRect("Label", rect);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            var fontSize = args.GetInt("fontSize", 22);
            var textColor = UiNodeFactory.ParseColor(args.Get("textColor"), Color.white);

            var tmpFont = args.GetString("tmpFont", null);
            if (args.GetBool("tmp", false) || !string.IsNullOrEmpty(tmpFont))
            {
                // 中文等复杂字形走 TMP；按钮底色在 args 的 color 上，文字色必须显式传进来
                CreateTmpTextCommand.AddTmp(labelRect.gameObject, args, label, fontSize, context.CommandId,
                    TextAlignmentOptions.Center, textColor, tmpFont);
            }
            else
            {
                var text = UiNodeFactory.AddText(
                    labelRect.gameObject, label, fontSize, textColor, TextAnchor.MiddleCenter,
                    args.GetBool("bold", false));

                var fontPath = args.GetString("font", null);
                if (!string.IsNullOrEmpty(fontPath))
                {
                    text.font = UiAssetUtil.LoadFont(fontPath, context.CommandId, "font");
                }
            }

            UiNodeFactory.Finalize(rect.gameObject, args.GetBool("select", false));
            return CommandResult.Ok($"已创建 Button '{name}'（{label}）", name);
        }
    }
}
