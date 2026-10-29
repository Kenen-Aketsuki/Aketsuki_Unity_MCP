using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// 给场景中某个物体上「任意组件的任意字段/属性」赋值。
    /// <para>
    /// 这是把「场景里写死的节点」接到「控制脚本字段」上的通用积木：
    /// 不绑定任何具体业务，任何组件类型、任何字段都能用。
    /// </para>
    /// <para>
    /// 参数：
    /// <list type="bullet">
    /// <item><c>target</c> 目标物体：名字 / 层级路径（"A/B"）/ "type:XXX"，支持 inactive 节点</item>
    /// <item><c>component</c> 组件类型名：如 "Image"、"Transform"、"TempUIController"</item>
    /// <item><c>field</c> 字段名或属性名（大小写敏感；含 [SerializeField] 的私有字段也能设）</item>
    /// <item><c>value</c> 值。字段类型是 UnityEngine.Object 派生时，按「节点引用」解析成序列化引用：
    /// 节点名 / 层级路径（"A/B"）/ 类型选择器（"type:Canvas#1"）/ 对象形式（{"path":"..."}），
    /// 或按「资源引用」解析："asset:Assets/..."（全路径）/"asset:裸文件名"（精确匹配）/
    /// {"asset":"..."}。数字 0 或 null 表示置空。其余按目标类型转换（数字 / 布尔 / 字符串 / 枚举 / 向量 / 颜色）；
    /// 数组与 <c>List&lt;T&gt;</c> 字段传 JSON 数组，逐元素按同一套规则转换（IMPR-007）</item>
    /// <item><c>prefab</c> 可选。给了就改这个 **Prefab 资源**而不是场景：<c>target</c> 按 Prefab 内的
    /// 节点路径解析（空串 = Prefab 根），改完自动 <c>SaveAsPrefabAsset</c> 落盘</item>
    /// </list>
    /// </para>
    /// </summary>
    [Command("component.setField",
        Description = "给任意组件的字段/属性赋值（支持对象引用、数字、字符串、向量、颜色、枚举）",
        Usage = "target: string, component: string, field: string, value: any, prefab: string",
        EditorOnly = true)]
    public sealed class SetComponentFieldCommand : ICommand
    {
        private const BindingFlags MemberFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public CommandResult Execute(CommandContext context)
        {
            var targetSpec = RequireTargetSpec(context);

            var typeName = context.Args.RequireString("component");
            var fieldName = context.Args.RequireString("field");
            var raw = context.Args.Get("value");

            // IMPR-006：target 的读法由 RequireTargetSpec / EnsureSceneTarget / ResolvePrefabTarget 统一，
            // 保证 setField / setFields / add / remove 四条指令的约定一致。
            var prefabPath = context.Args.GetString("prefab", null);
            if (!string.IsNullOrEmpty(prefabPath))
            {
                return ExecuteOnPrefab(context, prefabPath, targetSpec, typeName, fieldName, raw);
            }

            EnsureSceneTarget(targetSpec, context);

            if (!SceneQuery.TryResolveUnique(targetSpec, out var target, out var resolveError))
            {
                throw new CommandArgumentException(context.CommandId, "target", resolveError);
            }

            var component = RequireComponent(target, typeName, context);
            var op = ResolveField(component, fieldName, raw, context, null);

#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(component, "CommandSystem.SetComponentField");
#endif
            ApplyField(op, true);

            return CommandResult.Ok(
                $"已设置 {SceneQuery.BuildPath(target)} 上 {component.GetType().Name}.{fieldName} = {Describe(op.converted)}",
                fieldName);
        }

        /// <summary>
        /// 改 Prefab 资源：<c>LoadPrefabContents → 改字段 → SaveAsPrefabAsset → Unload</c>。
        /// <para>走 Unity 自己的序列化，比手改 YAML 安全：字段名/引用/默认值都由 Unity 校验。</para>
        /// </summary>
        private CommandResult ExecuteOnPrefab(CommandContext context, string prefabPath, string targetSpec,
            string typeName, string fieldName, JsonValue raw)
        {
#if UNITY_EDITOR
            var root = UnityEditor.PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null)
            {
                throw new CommandArgumentException(context.CommandId, "prefab",
                    $"打不开 Prefab '{prefabPath}'（路径要基于 Assets/、含 .prefab 后缀）");
            }

            try
            {
                var target = ResolvePrefabTarget(root, targetSpec, context);
                var component = RequireComponent(target, typeName, context);
                // 引用值也要在 Prefab 内容里找，不能走场景查找
                var op = ResolveField(component, fieldName, raw, context,
                    path => FindInPrefab(root, path));

                // 与场景分支对称：ResolveField 只做「解析 + 值转换」，真正写入在 ApplyField。
                // 早期版本漏了这一句，导致 Prefab 上回显 Success 却什么都没改（BUG-003）。
                ApplyField(op, false);

                UnityEditor.PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                return CommandResult.Ok(
                    $"已设置 Prefab '{prefabPath}' 的 {target.name}.{component.GetType().Name}.{fieldName} = {Describe(op.converted)}",
                    fieldName);
            }
            finally
            {
                UnityEditor.PrefabUtility.UnloadPrefabContents(root);
            }
#else
            throw new CommandException($"指令 '{context.CommandId}' 只能在编辑器内执行");
#endif
        }

        // ------------------------------------------------------------ 目标解析

        /// <summary>
        /// 读 <c>target</c> 参数的唯一入口（IMPR-006）。
        /// <para>
        /// 约定与 <c>component.setField</c> 一致：Prefab 模式下 <c>target</c> 可以省略或写空串
        /// （都表示 Prefab 根），因此这里只拦「完全没给」这一种情况——早期版本在
        /// <c>component.add</c> / <c>component.remove</c> 上用的是 <c>RequireString</c>，
        /// 空串会被当成"缺少必填参数"拦下，逼调用方改传根节点名（重名时还会歧义）。
        /// </para>
        /// </summary>
        internal static string RequireTargetSpec(CommandContext context)
        {
            var spec = context.Args.GetString("target", null);
            if (spec == null)
            {
                throw new CommandArgumentException(context.CommandId, "target",
                    "缺少必填参数 target（Prefab 模式下可用空串表示 Prefab 根）");
            }

            return spec;
        }

        /// <summary>场景模式下的 target 非空校验：场景没有唯一根，空串在这里没有意义。</summary>
        internal static void EnsureSceneTarget(string spec, CommandContext context)
        {
            if (spec.Trim().Length == 0)
            {
                throw new CommandArgumentException(context.CommandId, "target",
                    "场景模式的 target 不能为空（空串只在 Prefab 模式下表示根节点）");
            }
        }

        /// <summary>
        /// Prefab 模式解析 target 的共用入口：空串 / 省略 = Prefab 根。
        /// add / remove / setField / setFields 四条指令都走这里，避免"某条指令不支持"再次发生。
        /// </summary>
        internal static Transform ResolvePrefabTarget(GameObject root, string spec, CommandContext context)
        {
            return FindInPrefabUnique(root, spec ?? string.Empty, context);
        }

        internal static Component RequireComponent(Transform target, string typeName, CommandContext context)
        {
            var componentType = SceneQuery.ResolveComponentType(typeName);
            if (componentType == null)
            {
                throw new CommandArgumentException(context.CommandId, "component",
                    $"'{typeName}' 不是可识别的组件类型");
            }

            var component = target.GetComponent(componentType);
            if (component == null)
            {
                throw new CommandArgumentException(context.CommandId, "component",
                    $"'{target.name}' 上没有 {componentType.Name} 组件。它身上有：{ListComponents(target)}");
            }

            return component;
        }

        /// <summary>在 Prefab 内容里按名字 / 路径找节点（空串 = 根节点）。Prefab 编辑类指令共用。</summary>
        internal static Transform FindInPrefab(GameObject root, string spec)
        {
            var text = (spec ?? string.Empty).Trim().Trim('/');
            if (text.Length == 0)
            {
                return root.transform;
            }

            var segments = text.Split('/');
            var start = 0;
            // 允许写 "Prefab_Wander" 或 "Prefab_Wander/SkillBubble" 这种带根节点名（第一段就是根）的写法
            if (segments.Length > 0 && string.Equals(segments[0].Trim(), root.name, StringComparison.Ordinal))
            {
                start = 1;
            }

            var current = root.transform;
            for (var i = start; i < segments.Length; i++)
            {
                var segment = segments[i].Trim();
                if (segment.Length == 0)
                {
                    continue;
                }

                current = FindChildByName(current, segment);
                if (current == null)
                {
                    return null;
                }
            }

            return current;
        }

        /// <summary>先找直接子节点（同级有多个同名时取第一个），找不到再全树按名字找。</summary>
        private static Transform FindChildByName(Transform parent, string name, bool deep = true)
        {
            foreach (Transform child in parent)
            {
                if (string.Equals(child.name, name, StringComparison.Ordinal))
                {
                    return child;
                }
            }

            if (!deep)
            {
                return null;
            }

            foreach (Transform child in parent)
            {
                var found = FindChildByName(child, name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        // ------------------------------------------------------------ 成员查找

        internal struct Member
        {
            public FieldInfo field;
            public PropertyInfo property;
        }

        private static Member RequireMember(Type type, string name, CommandContext context)
        {
            var member = FindMember(type, name);
            if (member != null)
            {
                return member.Value;
            }

            // C5：兼容 Unity 序列化里的 m_ 前缀（如 m_Enabled / m_PressedColor）
            var alt = name.StartsWith("m_", StringComparison.Ordinal) ? name.Substring(2) : "m_" + name;
            member = FindMember(type, alt);
            if (member != null)
            {
                return member.Value;
            }

            var suggestions = ClosestMemberNames(type, name, 3);
            var hint = suggestions.Count > 0
                ? $"。您是不是要写：{string.Join("、", suggestions.ToArray())}"
                : string.Empty;
            throw new CommandArgumentException(context.CommandId, "field",
                $"{type.Name} 上没有字段或属性 '{name}'{hint}");
        }

        private static Type MemberType(Member member)
        {
            return member.field != null ? member.field.FieldType : member.property.PropertyType;
        }

        // ------------------------------------------------------------ 字段解析与写入

        /// <summary>一次字段写入的预解析结果（解析 + 值转换已完成，但尚未落盘）。</summary>
        internal struct FieldOp
        {
            public Component componentHolder;
            public string fieldName;
            public Member leaf;
            public object leafContainer;
            public List<(object parent, Member member)> backChain;
            public object converted;
            public object oldValue;

            /// <summary>
            /// 非空时走「序列化写入」通道，由它完成真正的写入（IMPR-005）。
            /// <para>反射认不出来但 Unity 自己能序列化的字段（ParticleSystem 的 <c>main</c>/<c>shape</c>/<c>emission</c>
            /// 这类只读模块属性）走这条通道。</para>
            /// </summary>
            public Func<object> applyOverride;
        }

        /// <summary>
        /// 解析并校验一个字段写入（支持 <c>A.B.C</c> 嵌套路径与结构体整体赋值），完成值转换但不落盘。
        /// 解析或转换任一失败会直接抛错——配合 <see cref="ApplyField"/> 即可实现「先全部校验、再整体写入」的原子语义。
        /// <para>
        /// 两条写入通道：反射优先（普通字段 / 可写属性）；反射找不到时退到
        /// <see cref="TryResolveSerializedWrite"/>（SerializedObject），这样 ParticleSystem 之类的
        /// 只读模块属性也能写（此前是"读得到、写不进"，见 IMPR-005）。
        /// </para>
        /// </summary>
        internal static FieldOp ResolveField(Component component, string fieldName, JsonValue raw,
            CommandContext context, Func<string, Transform> nodeResolver)
        {
            if (TryResolveFieldPath(component, fieldName, context, out var leaf, out var leafContainer,
                    out var backChain, out var resolveError))
            {
                var oldValue = GetMemberValue(leaf, leafContainer);
                var converted = ConvertValue(raw, MemberType(leaf), context, fieldName, nodeResolver);
                return new FieldOp
                {
                    componentHolder = component,
                    fieldName = fieldName,
                    leaf = leaf,
                    leafContainer = leafContainer,
                    backChain = backChain,
                    converted = converted,
                    oldValue = oldValue,
                    applyOverride = null
                };
            }

            Func<object> applyOverride;
            object fallbackValue;
            object fallbackOld;
            string fallbackError;
            if (TryResolveSerializedWrite(component, fieldName, raw, context,
                    out applyOverride, out fallbackValue, out fallbackOld, out fallbackError))
            {
                return new FieldOp
                {
                    componentHolder = component,
                    fieldName = fieldName,
                    converted = fallbackValue,
                    oldValue = fallbackOld,
                    applyOverride = applyOverride
                };
            }

            throw new CommandArgumentException(context.CommandId, "field",
                string.IsNullOrEmpty(fallbackError) ? resolveError : resolveError + "；" + fallbackError);
        }

        /// <summary>把 <see cref="ResolveField"/> 解析出的结果真正写入组件（含 Undo/脏标记），并返回新值。</summary>
        internal static object ApplyField(FieldOp op, bool recordUndo)
        {
            // 序列化通道：自带 SerializedObject 的写入与脏标记，不走下面的反射路径
            if (op.applyOverride != null)
            {
                return op.applyOverride();
            }

#if UNITY_EDITOR
            if (recordUndo)
            {
                UnityEditor.Undo.RecordObject(op.componentHolder, "CommandSystem.SetComponentField");
            }
#endif
            SetMemberValue(op.leaf, op.leafContainer, op.converted);

            // 回写值类型的中间容器：嵌套路径里父级若是 struct，改了副本需写回上一层
            var childValue = op.leafContainer;
            for (var i = op.backChain.Count - 1; i >= 0; i--)
            {
                var (parent, member) = op.backChain[i];
                SetMemberValue(member, parent, childValue);
                childValue = parent;
            }

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(op.componentHolder);
            // 目标若是预制体实例的一部分，还要记下这条改动，否则保存场景时不会落盘
            UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(op.componentHolder);
#endif
            return op.converted;
        }

        private static object GetMemberValue(Member member, object container)
        {
            return member.field != null
                ? member.field.GetValue(container)
                : member.property.GetValue(container, null);
        }

        private static void SetMemberValue(Member member, object container, object value)
        {
            if (member.field != null)
            {
                member.field.SetValue(container, value);
            }
            else
            {
                member.property.SetValue(container, value, null);
            }
        }

        // ------------------------------------------------------------ 序列化写入通道（IMPR-005）

        /// <summary>
        /// 脚本 API 里的模块短名 → Unity 序列化里的模块名。
        /// <para>
        /// <c>ParticleSystem.main</c> / <c>.shape</c> / <c>.emission</c> 都是**只读属性**
        /// （返回模块 struct 的拷贝，本就不能整体赋值），而 YAML 里它们叫 InitialModule / ShapeModule /
        /// EmissionModule。有了这张表，调用方就能用写脚本时的习惯写 <c>main.startSize.scalar</c>。
        /// </para>
        /// </summary>
        private static readonly Dictionary<string, string> ParticleModuleAliases =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "main", "InitialModule" },
                { "emission", "EmissionModule" },
                { "shape", "ShapeModule" },
                { "velocityOverLifetime", "VelocityModule" },
                { "limitVelocityOverLifetime", "LimitVelocityModule" },
                { "inheritVelocity", "InheritVelocityModule" },
                { "forceOverLifetime", "ForceModule" },
                { "colorOverLifetime", "ColorModule" },
                { "colorBySpeed", "ColorBySpeedModule" },
                { "sizeOverLifetime", "SizeModule" },
                { "sizeBySpeed", "SizeBySpeedModule" },
                { "rotationOverLifetime", "RotationModule" },
                { "rotationBySpeed", "RotationBySpeedModule" },
                { "externalForces", "ExternalForcesModule" },
                { "noise", "NoiseModule" },
                { "collision", "CollisionModule" },
                { "trigger", "TriggerModule" },
                { "subEmitters", "SubModule" },
                { "textureSheetAnimation", "TextureSheetAnimationModule" },
                { "lights", "LightsModule" },
                { "trails", "TrailsModule" },
                { "customData", "CustomDataModule" },
                { "uv", "UVModule" }
            };

        /// <summary>
        /// 反射写不进去的字段的最后一条路：用 <c>SerializedObject</c> 按 Unity 自己的序列化路径写。
        /// <para>
        /// 解析期只做「路径能不能找到 + 值能不能转」，真正的写入包成委托推迟到 <see cref="ApplyField"/>，
        /// 这样 <c>component.setFields</c> 的原子语义（先全部校验、再整体写入）依然成立。
        /// </para>
        /// <para>
        /// 写入时**新建一个** SerializedObject：多条 op 若共用解析期的旧快照，后写的会把先写的覆盖回去。
        /// </para>
        /// </summary>
        private static bool TryResolveSerializedWrite(Component component, string fieldPath, JsonValue raw,
            CommandContext context, out Func<object> apply, out object converted, out object oldValue,
            out string error)
        {
            apply = null;
            converted = null;
            oldValue = null;
            error = null;

            if (component == null)
            {
                return false;
            }

#if UNITY_EDITOR
            var probe = new UnityEditor.SerializedObject(component);
            var prop = FindSerializedProperty(probe, fieldPath);
            if (prop == null)
            {
                error = SerializedPathHint(component, fieldPath);
                return false;
            }

            object pending;
            string convertError;
            if (!TryConvertSerializedValue(prop, raw, context, fieldPath, out pending, out convertError))
            {
                error = convertError;
                return false;
            }

            oldValue = DescribeSerializedValue(prop);
            converted = pending;

            var resolvedPath = prop.propertyPath;
            var holder = component;
            apply = () =>
            {
                var fresh = new UnityEditor.SerializedObject(holder);
                var target = fresh.FindProperty(resolvedPath);
                if (target == null)
                {
                    throw new CommandException(
                        $"序列化写入失败：'{holder.GetType().Name}' 上找不到路径 '{resolvedPath}'（对象可能已被销毁）");
                }

                WriteSerializedValue(target, pending);
                fresh.ApplyModifiedProperties();
                UnityEditor.EditorUtility.SetDirty(holder);
                UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(holder);
                return pending;
            };

            return true;
#else
            return false;
#endif
        }

#if UNITY_EDITOR
        /// <summary>按原路径找序列化属性；找不到时把首段按模块别名表翻译一次再找（<c>main.x</c> → <c>InitialModule.x</c>）。</summary>
        private static UnityEditor.SerializedProperty FindSerializedProperty(UnityEditor.SerializedObject so,
            string fieldPath)
        {
            var prop = so.FindProperty(fieldPath);
            if (prop != null)
            {
                return prop;
            }

            var dot = fieldPath.IndexOf('.');
            if (dot <= 0)
            {
                return null;
            }

            var head = fieldPath.Substring(0, dot).Trim();
            var tail = fieldPath.Substring(dot);

            string mapped;
            return ParticleModuleAliases.TryGetValue(head, out mapped)
                ? so.FindProperty(mapped + tail)
                : null;
        }

        private static string FirstSegment(string fieldPath)
        {
            var dot = fieldPath.IndexOf('.');
            return dot < 0 ? fieldPath : fieldPath.Substring(0, dot);
        }

        /// <summary>序列化路径也没命中时给出的提示：只对"确实是只读模块属性"的情况说话，普通拼错不添噪音。</summary>
        private static string SerializedPathHint(Component component, string fieldPath)
        {
            var head = FirstSegment(fieldPath).Trim();
            string mapped;
            if (!ParticleModuleAliases.TryGetValue(head, out mapped))
            {
                return null;
            }

            return $"'{head}' 是只读模块属性，请写到具体字段（如 {head}.<字段名>），" +
                   $"或直接写 Unity 序列化路径 {mapped}.…（component.get 读回来的路径可直接用）";
        }

        /// <summary>按 <c>SerializedProperty</c> 的类型做值转换；不支持的类型给出明确原因。</summary>
        private static bool TryConvertSerializedValue(UnityEditor.SerializedProperty prop, JsonValue raw,
            CommandContext context, string fieldName, out object value, out string error)
        {
            value = null;
            error = null;

            if (raw == null || raw.IsNull)
            {
                error = $"字段 {fieldName}：缺少 value";
                return false;
            }

            switch (prop.propertyType)
            {
                case UnityEditor.SerializedPropertyType.Float:
                    value = raw.AsFloat();
                    return true;
                case UnityEditor.SerializedPropertyType.Integer:
                    value = raw.AsInt();
                    return true;
                case UnityEditor.SerializedPropertyType.Boolean:
                    value = raw.AsBool();
                    return true;
                case UnityEditor.SerializedPropertyType.String:
                    value = raw.AsString();
                    return true;
                case UnityEditor.SerializedPropertyType.Color:
                    value = ToColor(raw, context, fieldName);
                    return true;
                case UnityEditor.SerializedPropertyType.Vector2:
                    value = ToVector2(raw, context, fieldName);
                    return true;
                case UnityEditor.SerializedPropertyType.Vector3:
                    value = ToVector3(raw, context, fieldName);
                    return true;
                case UnityEditor.SerializedPropertyType.Vector4:
                    value = ToVector4(raw, context, fieldName);
                    return true;
                case UnityEditor.SerializedPropertyType.Enum:
                    value = ResolveEnumIndex(prop, raw, context, fieldName);
                    return true;
                case UnityEditor.SerializedPropertyType.Generic:
                    error = $"字段 {fieldName}：'{prop.propertyPath}' 是复合结构（{prop.propertyType}），" +
                            "序列化通道不支持整体写入，请写到具体分量（如 main.startSize.scalar）";
                    return false;
                default:
                    error = $"字段 {fieldName}：序列化类型 {prop.propertyType} 暂不支持写入";
                    return false;
            }
        }

        private static void WriteSerializedValue(UnityEditor.SerializedProperty prop, object value)
        {
            switch (prop.propertyType)
            {
                case UnityEditor.SerializedPropertyType.Float:
                    prop.floatValue = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                    break;
                case UnityEditor.SerializedPropertyType.Integer:
                    prop.intValue = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    break;
                case UnityEditor.SerializedPropertyType.Boolean:
                    prop.boolValue = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                    break;
                case UnityEditor.SerializedPropertyType.String:
                    prop.stringValue = Convert.ToString(value, CultureInfo.InvariantCulture);
                    break;
                case UnityEditor.SerializedPropertyType.Color:
                    prop.colorValue = (Color)value;
                    break;
                case UnityEditor.SerializedPropertyType.Vector2:
                    prop.vector2Value = (Vector2)value;
                    break;
                case UnityEditor.SerializedPropertyType.Vector3:
                    prop.vector3Value = (Vector3)value;
                    break;
                case UnityEditor.SerializedPropertyType.Vector4:
                    prop.vector4Value = (Vector4)value;
                    break;
                case UnityEditor.SerializedPropertyType.Enum:
                    prop.enumValueIndex = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new CommandException($"序列化写入不支持类型 {prop.propertyType}（'{prop.propertyPath}'）");
            }
        }

        private static int ResolveEnumIndex(UnityEditor.SerializedProperty prop, JsonValue raw,
            CommandContext context, string fieldName)
        {
            var names = prop.enumNames ?? new string[0];
            if (raw.IsString)
            {
                var text = raw.AsString();
                for (var i = 0; i < names.Length; i++)
                {
                    if (string.Equals(names[i], text, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }

                throw new CommandArgumentException(context.CommandId, "value",
                    $"字段 {fieldName}：'{text}' 不是合法枚举值，可选：{string.Join("、", names)}");
            }

            var index = raw.AsInt();
            if (index < 0 || (names.Length > 0 && index >= names.Length))
            {
                throw new CommandArgumentException(context.CommandId, "value",
                    $"字段 {fieldName}：枚举下标 {index} 越界，可选：{string.Join("、", names)}");
            }

            return index;
        }

        /// <summary>把序列化属性的当前值描述成字符串，用于回显旧值。</summary>
        private static string DescribeSerializedValue(UnityEditor.SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case UnityEditor.SerializedPropertyType.Float:
                    return prop.floatValue.ToString(CultureInfo.InvariantCulture);
                case UnityEditor.SerializedPropertyType.Integer:
                    return prop.intValue.ToString(CultureInfo.InvariantCulture);
                case UnityEditor.SerializedPropertyType.Boolean:
                    return prop.boolValue ? "true" : "false";
                case UnityEditor.SerializedPropertyType.String:
                    return prop.stringValue;
                case UnityEditor.SerializedPropertyType.Enum:
                    return prop.enumNames != null && prop.enumValueIndex >= 0 &&
                           prop.enumValueIndex < prop.enumNames.Length
                        ? prop.enumNames[prop.enumValueIndex]
                        : prop.enumValueIndex.ToString(CultureInfo.InvariantCulture);
                case UnityEditor.SerializedPropertyType.Color:
                    return prop.colorValue.ToString();
                case UnityEditor.SerializedPropertyType.Vector2:
                    return prop.vector2Value.ToString();
                case UnityEditor.SerializedPropertyType.Vector3:
                    return prop.vector3Value.ToString();
                case UnityEditor.SerializedPropertyType.Vector4:
                    return prop.vector4Value.ToString();
                default:
                    return $"<{prop.propertyType}>";
            }
        }
#endif // UNITY_EDITOR

        /// <summary>
        /// 解析嵌套字段路径（<c>A.B.C</c>），返回叶子成员与「直接持有叶子的容器」以及需要回写的中间容器链。
        /// 每段都兼容 <c>m_</c> 前缀（C5）并在解析失败时给出最接近的候选（C5）。
        /// </summary>
        private static Member RequireFieldPath(Component component, string fieldName, CommandContext context,
            out object leafContainer, out List<(object parent, Member member)> backChain)
        {
            Member leaf;
            string error;
            if (!TryResolveFieldPath(component, fieldName, context, out leaf, out leafContainer, out backChain,
                    out error))
            {
                throw new CommandArgumentException(context.CommandId, "field", error);
            }

            return leaf;
        }

        /// <summary>
        /// <see cref="RequireFieldPath"/> 的可失败版本：解析不出来时返回 false 并把原因放进 <paramref name="error"/>，
        /// 好让调用方先退到序列化写入通道（IMPR-005）再决定报什么错。
        /// </summary>
        private static bool TryResolveFieldPath(Component component, string fieldName, CommandContext context,
            out Member leaf, out object leafContainer, out List<(object parent, Member member)> backChain,
            out string error)
        {
            leaf = default;
            leafContainer = null;
            backChain = null;
            error = null;

            var segments = fieldName.Split('.');
            object current = component;
            var currentType = component.GetType();
            var chain = new List<(object, Member)>();

            for (var i = 0; i < segments.Length; i++)
            {
                var seg = segments[i].Trim();
                var member = FindMember(currentType, seg);
                if (member == null)
                {
                    // C5：兼容 Unity 序列化里的 m_ 前缀
                    var alt = seg.StartsWith("m_", StringComparison.Ordinal) ? seg.Substring(2) : "m_" + seg;
                    member = FindMember(currentType, alt);
                }

                if (member == null)
                {
                    var suggestions = ClosestMemberNames(currentType, seg, 3);
                    var hint = suggestions.Count > 0
                        ? $"。您是不是要写：{string.Join("、", suggestions.ToArray())}"
                        : string.Empty;
                    error = $"{currentType.Name} 上没有字段或属性 '{seg}'（解析 '{fieldName}' 时）{hint}";
                    return false;
                }

                if (i == segments.Length - 1)
                {
                    leaf = member.Value;
                    leafContainer = current;
                    backChain = chain;
                    return true;
                }

                var value = GetMemberValue(member.Value, current);
                if (value == null)
                {
                    error = $"'{fieldName}' 的中间字段 '{seg}' 为 null，无法继续访问下一级";
                    return false;
                }

                chain.Add((current, member.Value));
                current = value;
                currentType = member.Value.field != null
                    ? member.Value.field.FieldType
                    : member.Value.property.PropertyType;
            }

            error = $"'{fieldName}' 不是合法的字段路径";
            return false;
        }

        /// <summary>在 Prefab 内容里按名字 / 路径找节点并做唯一性校验（空串 = 根节点）。复用 SceneQuery 的子树匹配。</summary>
        internal static Transform FindInPrefabUnique(GameObject root, string spec, CommandContext context)
        {
            var all = SceneQuery.FindInSubtree(root.transform, spec);
            if (all.Count == 0)
            {
                throw new CommandArgumentException(context.CommandId, "target",
                    $"Prefab '{root.name}' 里找不到节点 '{spec}'（空串表示 Prefab 根）");
            }

            if (all.Count > 1)
            {
                throw new CommandArgumentException(context.CommandId, "target",
                    $"Prefab 节点 '{spec}' 命中 {all.Count} 个，存在歧义，请用更完整的层级路径消歧");
            }

            return all[0];
        }

        /// <summary>先找字段再找属性；private 字段需要自己沿基类往上找（GetField 默认不搜基类私有成员）。</summary>
        private static Member? FindMember(Type type, string name)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var field = current.GetField(name, MemberFlags);
                if (field != null)
                {
                    return new Member { field = field };
                }

                var property = current.GetProperty(name, MemberFlags);
                if (property != null && property.CanWrite && property.GetIndexParameters().Length == 0)
                {
                    return new Member { property = property };
                }
            }

            return null;
        }

        private static string ListComponents(Transform target)
        {
            var builder = new StringBuilder();
            foreach (var item in target.GetComponents<Component>())
            {
                if (item == null)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append("、");
                }

                builder.Append(item.GetType().Name);
            }

            return builder.Length > 0 ? builder.ToString() : "（无）";
        }

        // ------------------------------------------------------------ 值转换

        private static object ConvertValue(JsonValue value, Type targetType, CommandContext context,
            string fieldName, Func<string, Transform> nodeResolver = null)
        {
            // CommandArgs.Get 在键缺失时返回 JSON null，所以这里只判 IsNull 就够了
            var isMissing = value == null || value.IsNull;

            // UnityEngine.Object 派生：GameObject / Transform / 任意组件 —— 按节点路径解析成引用
            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType))
            {
                if (isMissing)
                {
                    return null;
                }

                return ResolveReference(value, targetType, context, fieldName, nodeResolver);
            }

            if (isMissing)
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }

            if (targetType == typeof(string))
            {
                return value.AsString();
            }

            if (targetType == typeof(bool))
            {
                return value.AsBool();
            }

            if (targetType.IsEnum)
            {
                return ParseEnum(targetType, value.AsString(), context, fieldName);
            }

            if (targetType == typeof(int))
            {
                return value.AsInt();
            }

            if (targetType == typeof(long))
            {
                return value.AsLong();
            }

            if (targetType == typeof(float))
            {
                return value.AsFloat();
            }

            if (targetType == typeof(double))
            {
                return value.AsDouble();
            }

            if (targetType == typeof(Vector2))
            {
                return ToVector2(value, context, fieldName);
            }

            if (targetType == typeof(Vector3))
            {
                return ToVector3(value, context, fieldName);
            }

            if (targetType == typeof(Vector4))
            {
                return ToVector4(value, context, fieldName);
            }

            if (targetType == typeof(Color))
            {
                return ToColor(value, context, fieldName);
            }

            // IMPR-007：数组 / List<T> 字段整体赋值。
            // 元素递归回 ConvertValue，所以枚举数组、Vector3[]、组件引用数组（走 ResolveReference）、
            // 结构体数组（走下面的 ConvertStruct）全都同一条路径覆盖。
            var elementType = ArrayOrListElementType(targetType);
            if (elementType != null)
            {
                if (!value.IsArray)
                {
                    throw new CommandArgumentException(context.CommandId, "value",
                        $"字段 {fieldName} 的类型是 {targetType.Name}，value 请写成数组（如 [\"a\", \"b\"]）；" +
                        $"当前给的是 {DescribeJsonKind(value)}");
                }

                return ConvertSequence(value, targetType, elementType, context, fieldName, nodeResolver);
            }

            // C8：结构体整体赋值（ColorBlock / SpriteState / Navigation / Vector*/Rect 等）。
            // value 为对象时按字段名逐个灌入；数组形式由上面的 Vector/Color 分支处理。
            if (value.IsObject && targetType.IsValueType && targetType != typeof(string))
            {
                return ConvertStruct(value, targetType, context, fieldName);
            }

            throw new CommandArgumentException(context.CommandId, "value",
                $"暂不支持给类型 {targetType.Name} 赋值（字段 {fieldName}）。{UnsupportedTypeHint(targetType)}");
        }

        /// <summary>
        /// 数组或 <c>List&lt;T&gt;</c> 的元素类型；两者都不是时返回 null（IMPR-007）。
        /// <para>只认一维数组：多维数组没法用一层 JSON 数组表达，宁可直接报错也不要静默填一半。</para>
        /// </summary>
        private static Type ArrayOrListElementType(Type targetType)
        {
            if (targetType.IsArray)
            {
                return targetType.GetArrayRank() == 1 ? targetType.GetElementType() : null;
            }

            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(List<>))
            {
                return targetType.GetGenericArguments()[0];
            }

            return null;
        }

        /// <summary>
        /// 把 JSON 数组逐元素转成目标序列（IMPR-007）。
        /// <para>
        /// 每次都<b>新建一个</b>数组 / List 再整个赋回字段：写进原实例会踩到「长度对不上」的静默截断，
        /// 也拿不到"期望 n 个、实际 m 个"这种能自查的信息——新建就没这个歧义，长度天然等于入参。
        /// </para>
        /// </summary>
        private static object ConvertSequence(JsonValue value, Type targetType, Type elementType,
            CommandContext context, string fieldName, Func<string, Transform> nodeResolver)
        {
            var count = value.Count;
            var items = new object[count];
            for (var i = 0; i < count; i++)
            {
                items[i] = ConvertValue(value[i], elementType, context, fieldName + "[" + i + "]", nodeResolver);
            }

            if (targetType.IsArray)
            {
                var array = Array.CreateInstance(elementType, count);
                for (var i = 0; i < count; i++)
                {
                    array.SetValue(items[i], i);
                }

                return array;
            }

            var list = (IList)Activator.CreateInstance(targetType);
            for (var i = 0; i < count; i++)
            {
                list.Add(items[i]);
            }

            return list;
        }

        private static string DescribeJsonKind(JsonValue value)
        {
            if (value == null || value.IsNull)
            {
                return "null";
            }

            if (value.IsObject)
            {
                return "对象";
            }

            if (value.IsArray)
            {
                return "数组";
            }

            if (value.IsString)
            {
                return "字符串 '" + value.AsString() + "'";
            }

            if (value.IsBoolean)
            {
                return "布尔 " + (value.AsBool() ? "true" : "false");
            }

            return value.IsNumber ? "数字 " + value.AsDouble().ToString(CultureInfo.InvariantCulture) : value.Type.ToString();
        }

        /// <summary>
        /// 「这个类型写不进去」时给人看的提示（IMPR-007）。
        /// <para>
        /// 数组 / List 的可写成员列表是空的（反射看不到元素），以前的报错会吐一个空冒号，
        /// 连"该写什么"都猜不出来——现在直接点明要传数组。
        /// </para>
        /// </summary>
        private static string UnsupportedTypeHint(Type targetType)
        {
            var elementType = ArrayOrListElementType(targetType);
            if (elementType != null)
            {
                var sample = elementType.Name;
                if (elementType.IsEnum)
                {
                    var names = Enum.GetNames(elementType);
                    sample = names.Length > 0 ? names[0] : elementType.Name;
                }

                return $"该字段是 {targetType.Name}，value 请写成数组（如 [\"{sample}\"]）";
            }

            return $"该类型可写的子字段：{string.Join("、", WritableMemberNames(targetType))}";
        }

        /// <summary>
        /// 把对象形式的 value 按字段名逐个灌入结构体（C8）。每个子字段递归走 <see cref="ConvertValue"/>，
        /// 因此数组 / 颜色 / 嵌套子结构体都能正确转换；子字段名同样兼容 m_ 前缀与候选提示。
        /// </summary>
        private static object ConvertStruct(JsonValue value, Type structType, CommandContext context, string fieldName)
        {
            var instance = Activator.CreateInstance(structType);
            foreach (var key in value.Keys)
            {
                var seg = key;
                var member = FindMember(structType, seg);
                if (member == null)
                {
                    var alt = seg.StartsWith("m_", StringComparison.Ordinal) ? seg.Substring(2) : "m_" + seg;
                    member = FindMember(structType, alt);
                }

                if (member == null)
                {
                    var suggestions = ClosestMemberNames(structType, seg, 3);
                    var hint = suggestions.Count > 0
                        ? $"（您是不是要写：{string.Join("、", suggestions.ToArray())}）"
                        : string.Empty;
                    throw new CommandArgumentException(context.CommandId, "value",
                        $"类型 {structType.Name} 没有可写子字段 '{seg}'{hint}");
                }

                var subType = member.Value.field != null
                    ? member.Value.field.FieldType
                    : member.Value.property.PropertyType;
                var converted = ConvertValue(value[seg], subType, context, fieldName + "." + seg, null);
                SetMemberValue(member.Value, instance, converted);
            }

            return instance;
        }

        // ------------------------------------------------------------ 字段名候选提示（C5）

        /// <summary>收集类型的全部可写成员名（字段 + 属性，含基类私有成员）。</summary>
        private static List<string> CollectMemberNames(Type type)
        {
            var names = new List<string>();
            for (var current = type; current != null; current = current.BaseType)
            {
                foreach (var field in current.GetFields(MemberFlags))
                {
                    if (!names.Contains(field.Name))
                    {
                        names.Add(field.Name);
                    }
                }

                foreach (var property in current.GetProperties(MemberFlags))
                {
                    if (property.CanWrite && property.GetIndexParameters().Length == 0 && !names.Contains(property.Name))
                    {
                        names.Add(property.Name);
                    }
                }
            }

            return names;
        }

        /// <summary>列出类型可写子字段（用于报错提示，C8）。</summary>
        private static List<string> WritableMemberNames(Type type)
        {
            return CollectMemberNames(type);
        }

        /// <summary>按编辑距离挑出最像 <paramref name="name"/> 的若干成员名（最多 <paramref name="max"/> 个）。</summary>
        private static List<string> ClosestMemberNames(Type type, string name, int max)
        {
            var all = CollectMemberNames(type);
            var scored = new List<KeyValuePair<int, string>>();
            foreach (var candidate in all)
            {
                var distance = Levenshtein(name, candidate);
                // 阈值随名字长度放宽，避免短名误杀或长名全中
                if (distance <= Math.Max(2, name.Length / 4))
                {
                    scored.Add(new KeyValuePair<int, string>(distance, candidate));
                }
            }

            scored.Sort((a, b) => a.Key.CompareTo(b.Key));
            var result = new List<string>();
            for (var i = 0; i < Math.Min(max, scored.Count); i++)
            {
                result.Add(scored[i].Value);
            }

            return result;
        }

        /// <summary>经典 Levenshtein 编辑距离（用于字段名"您是不是要写 X"提示）。</summary>
        private static int Levenshtein(string a, string b)
        {
            a = a ?? string.Empty;
            b = b ?? string.Empty;
            var n = a.Length;
            var m = b.Length;
            var d = new int[n + 1, m + 1];
            for (var i = 0; i <= n; i++)
            {
                d[i, 0] = i;
            }

            for (var j = 0; j <= m; j++)
            {
                d[0, j] = j;
            }

            for (var i = 1; i <= n; i++)
            {
                for (var j = 1; j <= m; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }

            return d[n, m];
        }

        /// <summary>
        /// 把引用值解析成场景节点：字符串（节点名 / 层级路径 / "type:Canvas#1"）
        /// 或对象形式（<c>{"type":"Image","index":2}</c> / <c>{"path":"..."}</c> / <c>{"name":"..."}</c>），
        /// 再按目标类型取 GameObject / Transform / 组件。
        /// <para>Prefab 模式下走 nodeResolver 在 Prefab 内容里找（对象形式取其 path/name 字段）。</para>
        /// </summary>
        private static object ResolveReference(JsonValue value, Type targetType, CommandContext context,
            string fieldName, Func<string, Transform> nodeResolver)
        {
            // fileID 0 这种写成数字的，视为置空
            if (value.IsNumber && value.AsLong() == 0L)
            {
                return null;
            }

            if (!value.IsString && !value.IsObject)
            {
                throw new CommandArgumentException(context.CommandId, "value",
                    $"字段 {fieldName} 是 {targetType.Name} 引用，value 请写成节点路径字符串"
                    + "（如 \"Button_MyHome/Image_RedDot\"、\"type:Canvas\"）或对象形式（如 {\"path\":\"...\"}）");
            }

            // 资源引用："asset:路径"（Assets/ 全路径，或裸文件名按精确匹配查找）或 {"asset":"..."}
            string assetSpec = null;
            if (value.IsString && value.AsString().TrimStart().StartsWith("asset:", StringComparison.OrdinalIgnoreCase))
            {
                assetSpec = value.AsString().TrimStart().Substring(6).Trim();
            }
            else if (value.IsObject && value.ContainsKey("asset"))
            {
                assetSpec = value["asset"].AsString();
            }

            if (assetSpec != null)
            {
                return ResolveAsset(assetSpec, targetType, context, fieldName);
            }

            // Prefab 内容里没有 SceneQuery 的场景遍历，只能按 path/name 找；
            // 场景模式直接用 ResolveTarget，完整支持类型选择器
            var node = nodeResolver != null
                ? nodeResolver(value.IsObject
                    ? (value["path"].AsString() ?? value["name"].AsString())
                    : value.AsString())
                : SceneQuery.ResolveTarget(value);
            if (node == null)
            {
                var described = value.IsObject
                    ? value.ToJson(false)
                    : "'" + value.AsString() + "'";
                throw new CommandArgumentException(context.CommandId, "value",
                    $"字段 {fieldName}：找不到节点 {described}");
            }

            if (targetType == typeof(GameObject))
            {
                return node.gameObject;
            }

            if (targetType == typeof(Transform) || targetType == typeof(RectTransform))
            {
                // RectTransform 是 Transform 的派生，GetComponent 取到的是实际类型，可安全赋值
                var rect = node.GetComponent(targetType);
                if (rect == null)
                {
                    throw new CommandArgumentException(context.CommandId, "value",
                        $"字段 {fieldName}：节点 '{node.name}' 上没有 {targetType.Name}");
                }

                return rect;
            }

            var component = node.GetComponent(targetType);
            if (component == null)
            {
                throw new CommandArgumentException(context.CommandId, "value",
                    $"字段 {fieldName}：节点 '{node.name}' 上没有 {targetType.Name} 组件");
            }

            return component;
        }

        /// <summary>
        /// 把 <c>asset:</c> 引用解析成项目资源（骨架资产 / Sprite / Material 等任意 UnityEngine.Object）。
        /// <para>
        /// <c>Assets/</c> 全路径直接加载；裸文件名（如 <c>gift_box_SkeletonData</c>，不带扩展名）
        /// 用 AssetDatabase 按文件名精确匹配，命中多个时报错列出候选让调用方写全路径消歧。
        /// </para>
        /// </summary>
        internal static UnityEngine.Object ResolveAsset(string spec, Type targetType, CommandContext context,
            string fieldName)
        {
            if (string.IsNullOrEmpty(spec))
            {
                return null;
            }

            string path;
            if (spec.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                path = spec;
            }
            else
            {
                // 裸名（ResourceManager.Load 风格）：支持 "SpineModel/BuildingUpgrade/gift_box" 这种
                // 资源名——最后一段按文件名（不含扩展名）精确匹配，前面的段做路径包含过滤
                var segments = spec.Split('/');
                var fileName = segments[segments.Length - 1].Trim();
                var dot = fileName.LastIndexOf('.');
                var fileNameNoExt = dot > 0 ? fileName.Substring(0, dot) : fileName;
                var pathFilter = segments.Length > 1
                    ? string.Join("/", segments, 0, segments.Length - 1)
                    : null;

                var matches = new List<string>();
                foreach (var guid in UnityEditor.AssetDatabase.FindAssets(fileNameNoExt))
                {
                    var candidate = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.Equals(System.IO.Path.GetFileNameWithoutExtension(candidate), fileNameNoExt,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (pathFilter != null && !candidate.Replace('\\', '/').Contains(pathFilter))
                    {
                        continue;
                    }

                    matches.Add(candidate);
                }

                if (matches.Count == 0)
                {
                        throw new CommandArgumentException(context.CommandId, "value",
                            $"字段 {fieldName}：找不到资源 '{spec}'（asset: 引用按文件名精确匹配 + 子路径过滤，也可写全 Assets/ 路径）");
                }

                if (matches.Count > 1)
                {
                    var shown = matches.GetRange(0, Math.Min(5, matches.Count));
                    throw new CommandArgumentException(context.CommandId, "value",
                        $"字段 {fieldName}：'{spec}' 命中 {matches.Count} 个同名资源，请写全 Assets/ 路径消歧：\n  "
                        + string.Join("\n  ", shown.ToArray()));
                }

                path = matches[0];
            }

            // 先按目标类型加载：Texture 的 Sprite 是子资产（主资产是 Texture2D），
            // 只有按 Sprite 类型加载才能取到；其它类型同样受益于精确加载
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath(path, targetType);
            if (asset == null)
            {
                asset = UnityEditor.AssetDatabase.LoadAssetAtPath(path, typeof(UnityEngine.Object));
            }
            if (asset == null)
            {
                throw new CommandArgumentException(context.CommandId, "value",
                    $"字段 {fieldName}：'{path}' 不是可加载的资源");
            }

            if (!targetType.IsInstanceOfType(asset))
            {
                throw new CommandArgumentException(context.CommandId, "value",
                    $"字段 {fieldName}：'{path}' 是 {asset.GetType().Name}，目标字段需要 {targetType.Name}");
            }

            return asset;
        }

        private static object ParseEnum(Type enumType, string text, CommandContext context, string fieldName)
        {
            try
            {
                return Enum.Parse(enumType, text, true);
            }
            catch (Exception)
            {
                throw new CommandArgumentException(context.CommandId, "value",
                    $"字段 {fieldName}：'{text}' 不是 {enumType.Name} 的合法取值（可选：{string.Join("、", Enum.GetNames(enumType))}）");
            }
        }

        /// <summary>取数组里的第 index 个分量；越界或不是数组时返回 0。</summary>
        /// <summary>
        /// 取数组第 index 个分量的浮点值。<b>严格模式</b>：数组太短 / 元素不是数字都直接抛错。
        /// <para>历史上这里是"越界返回 0"，导致 {"x":1,...} 这类对象格式被静默解析成全 0 写进目标
        /// （2026-10-21 实测把 MainUIRoot 运行时 localScale 误写成 (0,0,0)），绝不允许再静默置零。</para>
        /// </summary>
        private static float ElementAt(JsonValue value, int index, CommandContext context, string fieldName)
        {
            var items = value.Items;
            if (items == null || index >= items.Count)
            {
                throw new CommandArgumentException(context.CommandId, "value",
                    $"字段 {fieldName}：数组分量不足（至少需要 {index + 1} 个数字，实际 {items?.Count ?? 0} 个）");
            }

            if (!items[index].IsNumber)
            {
                throw new CommandArgumentException(context.CommandId, "value",
                    $"字段 {fieldName}：数组第 {index + 1} 个分量不是数字");
            }

            return items[index].AsFloat();
        }

        /// <summary>向量支持数组 [x,y] / [x,y,z] / [x,y,z,w] 与对象 {"x":1,"y":1,"z":1} 两种写法；解析不出即抛错。</summary>
        private static Vector2 ToVector2(JsonValue value, CommandContext context, string fieldName)
        {
            return new Vector2(
                VectorPart(value, "x", 0, context, fieldName),
                VectorPart(value, "y", 1, context, fieldName));
        }

        private static Vector3 ToVector3(JsonValue value, CommandContext context, string fieldName)
        {
            return new Vector3(
                VectorPart(value, "x", 0, context, fieldName),
                VectorPart(value, "y", 1, context, fieldName),
                VectorPart(value, "z", 2, context, fieldName));
        }

        private static Vector4 ToVector4(JsonValue value, CommandContext context, string fieldName)
        {
            return new Vector4(
                VectorPart(value, "x", 0, context, fieldName),
                VectorPart(value, "y", 1, context, fieldName),
                VectorPart(value, "z", 2, context, fieldName),
                VectorPart(value, "w", 3, context, fieldName));
        }

        /// <summary>对象格式取键（如 "x"），数组格式取下标；两种格式都不是时抛错，绝不返回默认值。</summary>
        private static float VectorPart(JsonValue value, string key, int index,
            CommandContext context, string fieldName)
        {
            if (value.IsObject)
            {
                if (!value.TryGet(key, out var part) || !part.IsNumber)
                {
                    throw new CommandArgumentException(context.CommandId, "value",
                        $"字段 {fieldName}：对象格式向量缺少数字分量 '{key}'（写法如 {{\"{key}\":1}}）");
                }

                return part.AsFloat();
            }

            if (value.IsArray)
            {
                return ElementAt(value, index, context, fieldName);
            }

            throw new CommandArgumentException(context.CommandId, "value",
                $"字段 {fieldName}：向量要写成数组 [x,y,...] 或对象 {{\"x\":...,\"y\":...}}，收到的是 {value.ToJson(false)}");
        }

        /// <summary>颜色支持 [r,g,b] / [r,g,b,a] / "#RRGGBB" / "#RRGGBBAA"，取值范围 0~1。</summary>
        private static Color ToColor(JsonValue value, CommandContext context, string fieldName)
        {
            if (value.IsString)
            {
                var text = value.AsString();
                if (ColorUtility.TryParseHtmlString(text, out var parsed))
                {
                    return parsed;
                }

                throw new CommandArgumentException(context.CommandId, "value",
                    $"字段 {fieldName}：'{text}' 不是合法颜色（用 \"#RRGGBB\" 或 [r,g,b,a]）");
            }

            if (value.IsArray)
            {
                var items = value.Items;
                return new Color(ElementAt(value, 0, context, fieldName), ElementAt(value, 1, context, fieldName),
                    ElementAt(value, 2, context, fieldName),
                    items != null && items.Count > 3 ? ElementAt(value, 3, context, fieldName) : 1f);
            }

            throw new CommandArgumentException(context.CommandId, "value",
                $"字段 {fieldName}：颜色需要写成 \"#RRGGBB\" 或 [r,g,b,a]");
        }

        internal static string Describe(object value)
        {
            if (value == null)
            {
                return "null";
            }

            var unityObject = value as UnityEngine.Object;
            return unityObject != null ? unityObject.name : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 把一个字段读成 JSON（与 <c>component.get</c> 同一口径），供 <c>component.setFields</c>
        /// 回显 old/new（IMPR-004 方案 2）。
        /// <para>
        /// 优先走 <see cref="SerializedObject"/>：这样 Color / Vector / 数组 / 枚举都拿到结构化值，
        /// 调用方可以直接比对，不必再发一条 <c>component.get</c>。读不到（非序列化的可写属性）
        /// 才退到反射值的字符串形态，与旧版 data 的表达保持一致。
        /// </para>
        /// </summary>
        internal static JsonValue ReadFieldJson(Component component, string fieldName, object reflectionValue)
        {
#if UNITY_EDITOR
            try
            {
                var so = new UnityEditor.SerializedObject(component);
                var property = Aketsuki.UnityMcp.Editor.ComponentGetCommands.ResolveProperty(so, fieldName);
                if (property != null)
                {
                    return Aketsuki.UnityMcp.Editor.ComponentGetCommands.SerializeProperty(property, 0);
                }
            }
            catch (Exception)
            {
                // SerializedObject 读不了（脚本未刷新、字段不属于序列化图）时继续走反射值
            }
#endif
            return JsonValue.Of(Describe(reflectionValue));
        }
    }

    /// <summary>
    /// 从节点上移除组件（场景或 Prefab 资产）——补全 CRUD 的"删组件"。
    /// <para>
    /// 与 <c>component.add</c> 对称：组件按类型名字符串反射解析（短名或全名）。
    /// 场景模式走 <c>Undo.DestroyObjectImmediate</c>（可撤销）；Prefab 模式走
    /// <c>LoadPrefabContents → 删组件 → SaveAsPrefabAsset → Unload</c> 全程 Unity 序列化。
    /// </para>
    /// <para>参数：target（必填，节点路径 / 名字 / type:XXX）、component（必填，组件类型名）、
    /// prefab（可选，给了就操作 Prefab 资源内的目标节点，空串表示根节点）</para>
    /// </summary>
    [Command("component.remove", Description = "从节点上移除组件（场景或 Prefab 资产，类型名反射）",
        Usage = "target: string（Prefab 模式下空串=根）, component: string, prefab: string",
        EditorOnly = true)]
    public sealed class RemoveComponentCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            // IMPR-006：与 component.add / setField / setFields 一致——Prefab 模式下 target 可为空串（= 根节点）
            var targetSpec = SetComponentFieldCommand.RequireTargetSpec(context);
            var typeName = context.Args.RequireString("component");

            var prefabPath = context.Args.GetString("prefab", null);
            if (!string.IsNullOrEmpty(prefabPath))
            {
#if UNITY_EDITOR
                var normalized = Aketsuki.UnityMcp.Editor.PrefabEditUtil.NormalizePrefabPath(context, prefabPath);
                return Aketsuki.UnityMcp.Editor.PrefabEditUtil.EditPrefab(context, normalized, root =>
                {
                var node = SetComponentFieldCommand.ResolvePrefabTarget(root, targetSpec, context);
                var component = SetComponentFieldCommand.RequireComponent(node, typeName, context);
                    var componentType = component.GetType().Name;
                    UnityEngine.Object.DestroyImmediate(component);

                    var data = JsonValue.NewObject();
                    data.Set("prefab", JsonValue.Of(normalized));
                    data.Set("path", JsonValue.Of(node.name));
                    data.Set("removed", JsonValue.Of(componentType));
                    return CommandResult.Ok(
                        $"已从 Prefab '{normalized}' 的 {node.name} 上移除组件 {componentType}", data);
                });
#else
                throw new CommandException($"指令 '{context.CommandId}' 只能在编辑器内执行");
#endif
            }

            SetComponentFieldCommand.EnsureSceneTarget(targetSpec, context);

            if (!SceneQuery.TryResolveUnique(targetSpec, out var target, out var resolveError))
            {
                throw new CommandArgumentException(context.CommandId, "target", resolveError);
            }

#if UNITY_EDITOR
            var sceneComponent = SetComponentFieldCommand.RequireComponent(target, typeName, context);
            var sceneType = sceneComponent.GetType().Name;
            UnityEditor.Undo.DestroyObjectImmediate(sceneComponent);

            var sdata = JsonValue.NewObject();
            sdata.Set("path", JsonValue.Of(SceneQuery.BuildPath(target)));
            sdata.Set("removed", JsonValue.Of(sceneType));
            return CommandResult.Ok(
                $"已从 {SceneQuery.BuildPath(target)} 上移除组件 {sceneType}", sdata);
#else
            throw new CommandException($"指令 '{context.CommandId}' 只能在编辑器内执行");
#endif
        }
    }

    /// <summary>
    /// 一次写入一个组件上的多个字段（原子操作：先全部解析/校验，任一字段解析或赋值失败则整体不生效）。
    /// <para>
    /// 与连续多条 <c>component.setField</c> 相比，它把"隐藏一个 4 槽位面板要 11 条"压成一条，
    /// 且保证部分失败时不留半成品（验收：混入一个不存在的字段名时，其余字段一个都不改）。
    /// </para>
    /// <para>
    /// 参数：target（必填）、component（必填）、fields（必填，对象：字段名 → 值，支持 <c>A.B.C</c> 嵌套与结构体整体赋值）、
    /// prefab（可选，操作 Prefab 资源）。成功时返回各字段修改前的旧值，方便复核。
    /// </para>
    /// </summary>
    [Command("component.setFields",
        Description = "一次写入一个组件上的多个字段（原子：任一失败整体不生效）；data 同时回显写入前后的值（old / new）",
        Usage = "target: string（Prefab 模式下空串=根）, component: string, fields: object, prefab: string",
        EditorOnly = true)]
    public sealed class SetComponentFieldsCommand : ICommand
    {
        /// <summary>
        /// IMPR-004 方案 2：data 直接给 <c>old</c> / <c>new</c> 两份。
        /// <para>
        /// 调用方是 LLM，一次给全比"回显旧值 + 再发一条 component.get"更省往返，
        /// 也不会因为它把旧值误当新值而误判成写入失败。
        /// </para>
        /// </summary>
        private const string ValuesNote = "（data.old / data.new 为各字段写入前后的值）";

        public CommandResult Execute(CommandContext context)
        {
            var targetSpec = SetComponentFieldCommand.RequireTargetSpec(context);

            var typeName = context.Args.RequireString("component");
            var fieldsSpec = context.Args.Get("fields");
            if (fieldsSpec == null || !fieldsSpec.IsObject)
            {
                throw new CommandArgumentException(context.CommandId, "fields",
                    "fields 必须是对象（字段名 → 值），如 {\"enabled\":false,\"m_CellSize\":[212,59]}");
            }

            var prefabPath = context.Args.GetString("prefab", null);
            if (!string.IsNullOrEmpty(prefabPath))
            {
                return ExecuteOnPrefab(context, prefabPath, targetSpec, typeName, fieldsSpec);
            }

            SetComponentFieldCommand.EnsureSceneTarget(targetSpec, context);

            if (!SceneQuery.TryResolveUnique(targetSpec, out var target, out var resolveError))
            {
                throw new CommandArgumentException(context.CommandId, "target", resolveError);
            }

            var component = SetComponentFieldCommand.RequireComponent(target, typeName, context);
            // 先全部解析/校验（任一失败直接抛错，此时尚未落盘 —— 原子性保证）
            var ops = ResolveAll(component, fieldsSpec, context, null);
            // 旧值必须在 Apply 之前读
            var oldValues = BuildValues(ops, true);

#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(component, "CommandSystem.SetComponentFields");
#endif
            var applied = ApplyAll(ops, true);
            var newValues = BuildValues(ops, false, applied);

            // IMPR-004 方案 2：data = {"old": {...}, "new": {...}}
            return CommandResult.Ok(
                $"已批量设置 {SceneQuery.BuildPath(target)} 上 {component.GetType().Name} 的 {ops.Count} 个字段" +
                ValuesNote,
                BuildData(oldValues, newValues));
        }

        private static CommandResult ExecuteOnPrefab(CommandContext context, string prefabPath, string targetSpec,
            string typeName, JsonValue fieldsSpec)
        {
#if UNITY_EDITOR
            var normalized = Aketsuki.UnityMcp.Editor.PrefabEditUtil.NormalizePrefabPath(context, prefabPath);
            return Aketsuki.UnityMcp.Editor.PrefabEditUtil.EditPrefab(context, normalized, root =>
            {
                var node = SetComponentFieldCommand.ResolvePrefabTarget(root, targetSpec, context);
                var component = SetComponentFieldCommand.RequireComponent(node, typeName, context);
                var ops = ResolveAll(component, fieldsSpec, context, path => SetComponentFieldCommand.FindInPrefab(root, path));
                var oldValues = BuildValues(ops, true);
                var applied = ApplyAll(ops, false);
                var newValues = BuildValues(ops, false, applied);
                UnityEditor.PrefabUtility.SaveAsPrefabAsset(root, normalized);
                return CommandResult.Ok(
                    $"已批量设置 Prefab '{normalized}' 的 {node.name}.{component.GetType().Name} 的 {ops.Count} 个字段" +
                    ValuesNote,
                    BuildData(oldValues, newValues));
            });
#else
            throw new CommandException($"指令 '{context.CommandId}' 只能在编辑器内执行");
#endif
        }

        /// <summary>解析所有字段（校验失败会冒泡，此时还未 Apply，故整体不生效）</summary>
        private static List<SetComponentFieldCommand.FieldOp> ResolveAll(Component component, JsonValue fieldsSpec,
            CommandContext context, Func<string, Transform> nodeResolver)
        {
            var ops = new List<SetComponentFieldCommand.FieldOp>();
            foreach (var key in fieldsSpec.Keys)
            {
                ops.Add(SetComponentFieldCommand.ResolveField(component, key, fieldsSpec[key], context, nodeResolver));
            }

            return ops;
        }

        /// <summary>逐条写入，并收集每条 ApplyField 回传的新值（序列化通道读不回来时用得上）</summary>
        private static List<object> ApplyAll(List<SetComponentFieldCommand.FieldOp> ops, bool recordUndo)
        {
            var applied = new List<object>(ops.Count);
            foreach (var op in ops)
            {
                applied.Add(SetComponentFieldCommand.ApplyField(op, recordUndo));
            }

            return applied;
        }

        /// <summary>
        /// 把每个字段读成 JSON：<paramref name="useOld"/> 为真取写入前的值，否则取写入后的值。
        /// <para>读取优先走 SerializedObject（结构化、与 component.get 同口径），读不到才用传入的反射值兜底。</para>
        /// </summary>
        private static JsonValue BuildValues(List<SetComponentFieldCommand.FieldOp> ops, bool useOld,
            List<object> applied = null)
        {
            var obj = JsonValue.NewObject();
            for (var i = 0; i < ops.Count; i++)
            {
                var op = ops[i];
                var fallback = useOld || applied == null ? op.oldValue : applied[i];
                obj.Set(op.fieldName,
                    SetComponentFieldCommand.ReadFieldJson(op.componentHolder, op.fieldName, fallback));
            }

            return obj;
        }

        /// <summary>data = {"old": {...}, "new": {...}}（IMPR-004 方案 2）</summary>
        private static JsonValue BuildData(JsonValue oldValues, JsonValue newValues)
        {
            var data = JsonValue.NewObject();
            data.Set("old", oldValues);
            data.Set("new", newValues);
            return data;
        }
    }
}
