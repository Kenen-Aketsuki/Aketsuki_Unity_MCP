using System;
using UnityEditor;
using UnityEngine;
using Aketsuki.UnityMcp;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 组件字段「读取」指令（需求案 B8，2026-10-21 两次排障卡死的直接产物）：
    /// <list type="bullet">
    /// <item><c>component.get</c>：读组件字段的当前值（与 <c>component.setField</c> 对称）；
    /// field 省略时返回该组件全部可序列化字段。</item>
    /// <item><c>scene.inspect</c>：目标节点的全组件快照（路径、每个组件的全部字段）。</item>
    /// </list>
    /// 读取统一走 <see cref="SerializedObject"/>（与 Inspector 完全一致，天然含 [SerializeField] 私有字段，
    /// 运行时值也读得到——Play 模式下 scene.save 被拒，此前运行时值彻底无路可读）。
    /// </summary>
    internal static class ComponentGetCommands
    {
        [Command("component.get",
            Description = "读取组件字段的当前值（field 支持点号嵌套路径 A.B.C；省略则返回全部可序列化字段；支持场景与 Prefab 双模式）",
            Usage = "target: string, component: string, field: string, prefab: string",
            EditorOnly = true)]
        public sealed class GetComponentFieldCommand : ICommand
        {
            public CommandResult Execute(CommandContext context)
            {
                var targetSpec = context.Args.GetString("target", null);
                if (targetSpec == null)
                {
                    throw new CommandArgumentException(context.CommandId, "target",
                        "缺少必填参数 target（Prefab 模式下可用空串表示 Prefab 根）");
                }

                var typeName = context.Args.RequireString("component");
                var fieldName = context.Args.GetString("field", null);

                var prefabPath = context.Args.GetString("prefab", null);
                if (!string.IsNullOrEmpty(prefabPath))
                {
                    return ExecuteOnPrefab(context, prefabPath, targetSpec, typeName, fieldName);
                }

                if (targetSpec.Trim().Length == 0)
                {
                    throw new CommandArgumentException(context.CommandId, "target",
                        "场景模式的 target 不能为空（空串只在 Prefab 模式下表示根节点）");
                }

                if (!SceneQuery.TryResolveUnique(targetSpec, out var target, out var resolveError))
                {
                    throw new CommandArgumentException(context.CommandId, "target", resolveError);
                }

                var component = SetComponentFieldCommand.RequireComponent(target, typeName, context);
                var result = Read(context, component, fieldName, describeTarget: SceneQuery.BuildPath(target));
                // 只有对象结果才附场景信息：读 Color/Vector/数组字段时 data 是数组，Set 会抛
                // 「[Json] 期望类型 Object，实际为 Array」（BUG-004）
                if (result.Data is JsonValue data && data.IsObject)
                {
                    SceneQueryCommandsUtil.AttachScene(data);
                }

                return result;
            }

            private CommandResult ExecuteOnPrefab(CommandContext context, string prefabPath,
                string targetSpec, string typeName, string fieldName)
            {
                var root = PrefabUtility.LoadPrefabContents(prefabPath);
                if (root == null)
                {
                    throw new CommandArgumentException(context.CommandId, "prefab",
                        $"打不开 Prefab '{prefabPath}'（路径要基于 Assets/、含 .prefab 后缀）");
                }

                try
                {
                    var target = SetComponentFieldCommand.FindInPrefab(root, targetSpec);
                    if (target == null)
                    {
                        throw new CommandArgumentException(context.CommandId, "target",
                            $"Prefab '{prefabPath}' 里找不到节点 '{targetSpec}'（空串表示 Prefab 根）");
                    }

                    var component = SetComponentFieldCommand.RequireComponent(target, typeName, context);
                    // 只读：不走 SaveAsPrefabAsset，读完即卸载
                    return Read(context, component, fieldName, describeTarget: prefabPath);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }

            private CommandResult Read(CommandContext context, Component component, string fieldName,
                string describeTarget)
            {
                var so = new SerializedObject(component);

                if (string.IsNullOrEmpty(fieldName))
                {
                    // 全字段快照
                    var fields = JsonValue.NewObject();
                    var it = so.GetIterator();
                    var enterChildren = true;
                    while (it.Next(enterChildren))
                    {
                        enterChildren = false;
                        if (it.depth == 0)
                        {
                            fields.Set(it.propertyPath, SerializeProperty(it, 0));
                        }
                    }

                    return CommandResult.Ok(
                        $"{describeTarget} 的 {component.GetType().Name} 共 {CountTopLevel(fields)} 个字段",
                        fields);
                }

                var property = ResolveProperty(so, fieldName);
                if (property == null)
                {
                    throw new CommandArgumentException(context.CommandId, "field",
                        $"{component.GetType().Name} 上没有可序列化字段 '{fieldName}'（SerializedObject 里没有对应属性）");
                }

                var value = SerializeProperty(property, 0);
                return CommandResult.Ok(
                    $"{describeTarget} 的 {component.GetType().Name}.{fieldName} = {value.ToJson(false)}",
                    value);
            }
        }

        [Command("scene.inspect",
            Description = "目标节点的全组件快照（路径 + 每个组件的全部可序列化字段）；Play 模式也能读运行时值",
            Usage = "target: string",
            EditorOnly = true)]
        public sealed class InspectNodeCommand : ICommand
        {
            public CommandResult Execute(CommandContext context)
            {
                var targetSpec = context.Args.RequireString("target");
                if (!SceneQuery.TryResolveUnique(targetSpec, out var target, out var resolveError))
                {
                    throw new CommandArgumentException(context.CommandId, "target", resolveError);
                }

                var path = SceneQuery.BuildPath(target);
                var components = JsonValue.FromArray(Array.Empty<JsonValue>());
                var list = target.GetComponents<Component>();
                var items = new System.Collections.Generic.List<JsonValue>();
                foreach (var component in list)
                {
                    if (component == null)
                    {
                        continue; // missing script
                    }

                    var fields = JsonValue.NewObject();
                    var so = new SerializedObject(component);
                    var it = so.GetIterator();
                    var enterChildren = true;
                    while (it.Next(enterChildren))
                    {
                        enterChildren = false;
                        if (it.depth == 0)
                        {
                            fields.Set(it.propertyPath, SerializeProperty(it, 0));
                        }
                    }

                    var entry = JsonValue.NewObject();
                    entry.Set("type", JsonValue.Of(component.GetType().Name));
                    entry.Set("fields", fields);
                    items.Add(entry);
                }

                components = JsonValue.FromArray(items);

                var data = JsonValue.NewObject();
                data.Set("path", JsonValue.Of(path));
                data.Set("components", components);
                SceneQueryCommandsUtil.AttachScene(data);

                return CommandResult.Ok(
                    $"{path} 共 {items.Count} 个组件（含全部可序列化字段）", data);
            }
        }

        // ------------------------------------------------------------ SerializedObject → JsonValue

        /// <summary>
        /// 定位字段：点号嵌套路径 → 补 m_ 前缀的嵌套路径 → 顶层精确 / 去 m_ 前缀 / 嵌套尾段（旧口径）。
        /// <para>
        /// 第一步优先用 <see cref="SerializedObject.FindProperty(string)"/>：它天生支持任意深度路径
        /// （<c>m_Colors.normalColor</c>，甚至带数组下标的 <c>m_Fonts.Array.data[0].m_Font</c>），
        /// 与 <c>component.setField</c> 的 <c>A.B.C</c> 写入侧对齐（IMPR-001）。
        /// </para>
        /// </summary>
        internal static SerializedProperty ResolveProperty(SerializedObject so, string field)
        {
            var direct = so.FindProperty(field);
            if (direct != null)
            {
                return direct;
            }

            // 用户常按 Inspector / C# 属性名写（colors.normalColor），而序列化字段叫 m_Colors.m_NormalColor。
            // 逐段生成「原名 / 去 m_ / 加 m_ / m_+首字母大写」的变体，整条路径交给 FindProperty 裁决。
            var tolerant = FindPropertyTolerant(so, field);
            if (tolerant != null)
            {
                return tolerant;
            }

            var it = so.GetIterator();
            var enterChildren = true;
            SerializedProperty loose = null;
            while (it.Next(enterChildren))
            {
                enterChildren = false;
                if (it.depth != 0)
                {
                    continue;
                }

                if (string.Equals(it.propertyPath, field, StringComparison.Ordinal))
                {
                    return it.Copy();
                }

                var bare = it.propertyPath.StartsWith("m_", StringComparison.Ordinal)
                    ? it.propertyPath.Substring(2)
                    : it.propertyPath;
                if (string.Equals(bare, field, StringComparison.OrdinalIgnoreCase))
                {
                    loose = it.Copy();
                }
                else if (loose == null && it.propertyPath.EndsWith("." + field, StringComparison.OrdinalIgnoreCase))
                {
                    loose = it.Copy(); // 嵌套 struct 字段的尾段
                }
            }

            return loose;
        }

        /// <summary>
        /// 容错解析点号路径：每段生成「原名 / 去 m_ / 加 m_ / m_+首字母大写」的变体，
        /// 组合成整条路径逐个试 <see cref="SerializedObject.FindProperty(string)"/>，命中即返回。
        /// 例：<c>colors.normalColor</c> → 命中 <c>m_Colors.m_NormalColor</c>。
        /// </summary>
        private static SerializedProperty FindPropertyTolerant(SerializedObject so, string field)
        {
            var parts = field.Split('.');
            if (parts.Length == 0 || parts.Length > 6)
            {
                return null; // 段数过多多半是写错了，不做组合爆炸
            }

            var paths = new System.Collections.Generic.List<string> { string.Empty };
            foreach (var part in parts)
            {
                var variants = SegmentVariants(part);
                var next = new System.Collections.Generic.List<string>(paths.Count * variants.Count);
                foreach (var prefix in paths)
                {
                    foreach (var variant in variants)
                    {
                        next.Add(prefix.Length == 0 ? variant : prefix + "." + variant);
                    }
                }

                paths = next;
                if (paths.Count > 256)
                {
                    return null;
                }
            }

            foreach (var path in paths)
            {
                if (path.Length == 0)
                {
                    continue;
                }

                var property = so.FindProperty(path);
                if (property != null)
                {
                    return property;
                }
            }

            return null;
        }

        /// <summary>单段名的候选写法：原名、去 m_、加 m_、m_ + 首字母大写（normalColor → m_NormalColor）。</summary>
        private static System.Collections.Generic.List<string> SegmentVariants(string segment)
        {
            var list = new System.Collections.Generic.List<string>();
            AddVariant(list, segment);

            // 数组下标写法（m_Fonts.Array.data[0] 里的 data[0]）原样保留，不做变形
            if (segment.IndexOf('[') >= 0 || string.Equals(segment, "Array", StringComparison.Ordinal))
            {
                return list;
            }

            if (segment.StartsWith("m_", StringComparison.Ordinal))
            {
                AddVariant(list, segment.Substring(2));
            }
            else
            {
                AddVariant(list, "m_" + segment);
                if (segment.Length > 0)
                {
                    AddVariant(list, "m_" + char.ToUpperInvariant(segment[0]) + segment.Substring(1));
                }
            }

            return list;
        }

        private static void AddVariant(System.Collections.Generic.List<string> list, string value)
        {
            if (string.IsNullOrEmpty(value) || list.Contains(value))
            {
                return;
            }

            list.Add(value);
        }

        /// <summary>SerializedProperty → JsonValue。引用返回 {path, name, type}（null 返回 null），向量/颜色/矩形返回数组或对象。</summary>
        internal static JsonValue SerializeProperty(SerializedProperty property, int depth)
        {
            if (depth > 4)
            {
                return JsonValue.Of("...(层级过深，省略)");
            }

            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.ArraySize:
                case SerializedPropertyType.LayerMask:
                    return JsonValue.Of(property.intValue);

                case SerializedPropertyType.Boolean:
                    return JsonValue.Of(property.boolValue);

                case SerializedPropertyType.Float:
                    return JsonValue.Of(Math.Round(property.doubleValue, 6));

                case SerializedPropertyType.String:
                    return JsonValue.Of(property.stringValue);

                case SerializedPropertyType.Color:
                    var c = property.colorValue;
                    return JsonValue.FromArray(new[]
                    {
                        JsonValue.Of(Math.Round(c.r, 4)), JsonValue.Of(Math.Round(c.g, 4)),
                        JsonValue.Of(Math.Round(c.b, 4)), JsonValue.Of(Math.Round(c.a, 4)),
                    });

                case SerializedPropertyType.Vector2:
                    var v2 = property.vector2Value;
                    return JsonValue.FromArray(new[]
                    {
                        JsonValue.Of(Math.Round(v2.x, 4)), JsonValue.Of(Math.Round(v2.y, 4)),
                    });

                case SerializedPropertyType.Vector3:
                    var v3 = property.vector3Value;
                    return JsonValue.FromArray(new[]
                    {
                        JsonValue.Of(Math.Round(v3.x, 4)), JsonValue.Of(Math.Round(v3.y, 4)),
                        JsonValue.Of(Math.Round(v3.z, 4)),
                    });

                case SerializedPropertyType.Vector4:
                    var v = property.vector4Value;
                    return JsonValue.FromArray(new[]
                    {
                        JsonValue.Of(Math.Round(v.x, 4)), JsonValue.Of(Math.Round(v.y, 4)),
                        JsonValue.Of(Math.Round(v.z, 4)), JsonValue.Of(Math.Round(v.w, 4)),
                    });

                case SerializedPropertyType.Rect:
                    var r = property.rectValue;
                    var rect = JsonValue.NewObject();
                    rect.Set("x", JsonValue.Of(Math.Round(r.x, 4)));
                    rect.Set("y", JsonValue.Of(Math.Round(r.y, 4)));
                    rect.Set("width", JsonValue.Of(Math.Round(r.width, 4)));
                    rect.Set("height", JsonValue.Of(Math.Round(r.height, 4)));
                    return rect;

                case SerializedPropertyType.Bounds:
                    var b = property.boundsValue;
                    var bounds = JsonValue.NewObject();
                    bounds.Set("center", JsonValue.FromArray(new[]
                    {
                        JsonValue.Of(Math.Round(b.center.x, 4)), JsonValue.Of(Math.Round(b.center.y, 4)),
                        JsonValue.Of(Math.Round(b.center.z, 4)),
                    }));
                    bounds.Set("size", JsonValue.FromArray(new[]
                    {
                        JsonValue.Of(Math.Round(b.size.x, 4)), JsonValue.Of(Math.Round(b.size.y, 4)),
                        JsonValue.Of(Math.Round(b.size.z, 4)),
                    }));
                    return bounds;

                case SerializedPropertyType.Enum:
                    var index = property.enumValueIndex;
                    var names = property.enumNames;
                    return index >= 0 && index < names.Length
                        ? JsonValue.Of(names[index])
                        : JsonValue.Of(property.intValue);

                case SerializedPropertyType.ObjectReference:
                    return ReferenceToJson(property.objectReferenceValue);

                case SerializedPropertyType.Character:
                    return JsonValue.Of(((char)property.intValue).ToString());

                default:
                    // 嵌套 struct / 数组 / 其余类型：递归子属性
                    if (property.isArray && property.arraySize > 0)
                    {
                        var items = new System.Collections.Generic.List<JsonValue>();
                        for (var i = 0; i < property.arraySize; i++)
                        {
                            items.Add(SerializeProperty(property.GetArrayElementAtIndex(i), depth + 1));
                        }

                        return JsonValue.FromArray(items);
                    }

                    var obj = JsonValue.NewObject();
                    var inner = property.Copy();
                    var enterChildren = true;
                    while (inner.Next(enterChildren))
                    {
                        enterChildren = false;
                        if (inner.depth <= depth)
                        {
                            break; // 走出了当前属性的子树
                        }

                        if (inner.depth == depth + 1)
                        {
                            obj.Set(inner.propertyPath, SerializeProperty(inner, depth + 1));
                        }
                    }

                    return obj;
            }
        }

        /// <summary>UnityEngine.Object 引用 → {path, name, type}。场景对象 path 用层级路径，资产用 Assets/ 路径。</summary>
        internal static JsonValue ReferenceToJson(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return JsonValue.Null;
            }

            string path;
            var transform = (obj as Component) != null ? (obj as Component).transform
                : (obj as GameObject) != null ? (obj as GameObject).transform
                : null;
            if (transform != null)
            {
                path = SceneQuery.BuildPath(transform);
            }
            else
            {
                path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path))
                {
                    path = "(内存对象)";
                }
            }

            var json = JsonValue.NewObject();
            json.Set("path", JsonValue.Of(path));
            json.Set("name", JsonValue.Of(obj.name));
            json.Set("type", JsonValue.Of(obj.GetType().Name));
            return json;
        }

        private static int CountTopLevel(JsonValue obj)
        {
            var count = 0;
            var it = obj;
            // JsonValue 对象没有公开计数 API，用 ToJson 数引号的代价可接受（快照本身已是大对象）
            var text = it.ToJson(false);
            foreach (var ch in text)
            {
                if (ch == ':')
                {
                    count++;
                }
            }

            return count;
        }
    }
}
