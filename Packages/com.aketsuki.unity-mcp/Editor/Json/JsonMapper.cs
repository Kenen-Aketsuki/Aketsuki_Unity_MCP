using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// 指定该字段/属性在 JSON 中使用的键名
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
    public sealed class JsonMemberAttribute : Attribute
    {
        /// <summary>JSON 中的键名</summary>
        public string Name { get; }

        public JsonMemberAttribute(string name)
        {
            Name = name;
        }
    }

    /// <summary>
    /// JsonValue 与 C# 对象之间的反射映射器
    /// <para>
    /// 支持：基本类型 / 枚举 / 可空类型 / 字符串 / Unity 向量与颜色 / 数组 / List / Dictionary / 普通类与结构体。
    /// 映射规则：公共实例字段、公共可写属性，以及标记了 [SerializeField] 的非公共字段（兼容 Unity 序列化习惯）。
    /// </para>
    /// </summary>
    public static class JsonMapper
    {
        private static readonly Dictionary<Type, List<MemberAccessor>> Cache =
            new Dictionary<Type, List<MemberAccessor>>();

        private static readonly object CacheLock = new object();

        #region 反序列化

        /// <summary>把 JSON 转为指定类型的对象</summary>
        public static T ToObject<T>(JsonValue value, T fallback = default)
        {
            var result = ToObject(value, typeof(T), fallback);
            return result == null ? fallback : (T)result;
        }

        /// <summary>把 JSON 转为指定类型的对象</summary>
        public static object ToObject(JsonValue value, Type type, object fallback = null)
        {
            if (type == null)
            {
                return fallback;
            }

            if (value == null || value.IsNull)
            {
                // 值类型不能返回 null
                return type.IsValueType && Nullable.GetUnderlyingType(type) == null
                    ? Activator.CreateInstance(type)
                    : null;
            }

            if (type == typeof(JsonValue))
            {
                return value;
            }

            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null)
            {
                return ToObject(value, underlying, fallback);
            }

            // Unity 常用值类型优先处理
            var unityValue = TryReadUnityValue(value, type);
            if (unityValue != null)
            {
                return unityValue;
            }

            if (type == typeof(string))
            {
                return value.AsString(fallback as string);
            }

            if (type == typeof(bool))
            {
                return value.AsBool(fallback is bool b && b);
            }

            if (type == typeof(char))
            {
                var s = value.AsString();
                return string.IsNullOrEmpty(s) ? '\0' : s[0];
            }

            if (type.IsEnum)
            {
                return ToEnum(value, type, fallback);
            }

            if (IsNumericType(type))
            {
                return Convert.ChangeType(value.AsDouble(fallback == null ? 0d : Convert.ToDouble(fallback)), type,
                    CultureInfo.InvariantCulture);
            }

            if (type == typeof(decimal))
            {
                return decimal.TryParse(value.AsString("0"), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var dec)
                    ? dec
                    : 0m;
            }

            if (typeof(IDictionary).IsAssignableFrom(type))
            {
                return ToDictionary(value, type, fallback);
            }

            if (type.IsArray)
            {
                return ToArray(value, type, fallback);
            }

            if (type.IsGenericType && typeof(IList).IsAssignableFrom(type))
            {
                return ToList(value, type, fallback);
            }

            if (type.IsClass || (type.IsValueType && !type.IsPrimitive))
            {
                return ToComplexObject(value, type, fallback);
            }

            return fallback;
        }

        private static object ToEnum(JsonValue value, Type type, object fallback)
        {
            if (value.IsString)
            {
                var text = value.StringValue;
                foreach (var name in Enum.GetNames(type))
                {
                    if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                    {
                        return Enum.Parse(type, name);
                    }
                }
                return fallback ?? Enum.GetValues(type).GetValue(0);
            }

            if (value.IsNumber)
            {
                try
                {
                    return Enum.ToObject(type, Convert.ToInt64(value.NumberValue));
                }
                catch (Exception)
                {
                    return fallback ?? Enum.GetValues(type).GetValue(0);
                }
            }

            return fallback ?? Enum.GetValues(type).GetValue(0);
        }

        private static object ToArray(JsonValue value, Type type, object fallback)
        {
            var elementType = type.GetElementType();
            if (!value.IsArray)
            {
                return fallback ?? Array.CreateInstance(elementType, 0);
            }

            var items = value.ArrayValue;
            var array = Array.CreateInstance(elementType, items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                array.SetValue(ToObject(items[i], elementType, null), i);
            }
            return array;
        }

        private static object ToList(JsonValue value, Type type, object fallback)
        {
            var elementType = type.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(type);
            if (!value.IsArray)
            {
                return fallback ?? list;
            }

            foreach (var item in value.ArrayValue)
            {
                list.Add(ToObject(item, elementType, null));
            }
            return list;
        }

        private static object ToDictionary(JsonValue value, Type type, object fallback)
        {
            Type keyType = typeof(string);
            Type valueType = typeof(object);
            if (type.IsGenericType)
            {
                var args = type.GetGenericArguments();
                keyType = args[0];
                valueType = args[1];
            }

            var dict = (IDictionary)Activator.CreateInstance(type);
            if (!value.IsObject)
            {
                return fallback ?? dict;
            }

            foreach (var key in value.Keys)
            {
                var convertedKey = keyType == typeof(string)
                    ? key
                    : Convert.ChangeType(key, keyType, CultureInfo.InvariantCulture);
                dict[convertedKey] = ToObject(value[key], valueType, null);
            }
            return dict;
        }

        private static object ToComplexObject(JsonValue value, Type type, object fallback)
        {
            if (!value.IsObject)
            {
                return fallback;
            }

            object instance;
            try
            {
                instance = Activator.CreateInstance(type);
            }
            catch (Exception)
            {
                return fallback;
            }

            var members = GetMembers(type);
            foreach (var member in members)
            {
                JsonValue fieldValue;
                if (!TryFindField(value, member.Name, out fieldValue))
                {
                    continue;
                }
                try
                {
                    member.Set(instance, ToObject(fieldValue, member.ValueType, null));
                }
                catch (Exception e)
                {
                    throw new JsonMappingException($"字段 '{type.Name}.{member.Name}' 赋值失败：{e.Message}", e);
                }
            }

            return instance;
        }

        private static bool TryFindField(JsonValue obj, string name, out JsonValue value)
        {
            if (obj.TryGet(name, out value))
            {
                return true;
            }

            foreach (var key in obj.Keys)
            {
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = obj[key];
                    return true;
                }
            }

            value = JsonValue.Null;
            return false;
        }

        private static object TryReadUnityValue(JsonValue value, Type type)
        {
            if (type == typeof(Vector2))
            {
                var v = ReadVector(value);
                return new Vector2(v[0], v[1]);
            }
            if (type == typeof(Vector3))
            {
                var v = ReadVector(value);
                return new Vector3(v[0], v[1], v[2]);
            }
            if (type == typeof(Vector4))
            {
                var v = ReadVector(value);
                return new Vector4(v[0], v[1], v[2], v[3]);
            }
            if (type == typeof(Color))
            {
                var v = ReadVector(value);
                if (value.IsObject && (value.ContainsKey("r") || value.ContainsKey("g") || value.ContainsKey("b")))
                {
                    // 颜色对象默认 alpha 为 1
                    var a = value.ContainsKey("a") ? value.GetFloat("a", 1f) : 1f;
                    return new Color(v[0], v[1], v[2], a);
                }
                if (value.IsArray && value.Count < 4)
                {
                    return new Color(v[0], v[1], v[2], 1f);
                }
                return new Color(v[0], v[1], v[2], v[3]);
            }
            return null;
        }

        /// <summary>读取最多 4 个数值分量，返回顺序为 x/y/z/w（或 r/g/b/a）</summary>
        private static float[] ReadVector(JsonValue value)
        {
            var x = 0f;
            var y = 0f;
            var z = 0f;
            var w = 0f;

            if (value.IsArray)
            {
                var items = value.ArrayValue;
                if (items.Count > 0)
                {
                    x = items[0].AsFloat();
                }
                if (items.Count > 1)
                {
                    y = items[1].AsFloat();
                }
                if (items.Count > 2)
                {
                    z = items[2].AsFloat();
                }
                if (items.Count > 3)
                {
                    w = items[3].AsFloat();
                }
            }
            else if (value.IsObject)
            {
                x = value.GetFloat("x", x);
                y = value.GetFloat("y", y);
                z = value.GetFloat("z", z);
                w = value.GetFloat("w", w);
                // 颜色常用 r/g/b/a
                if (value.ContainsKey("r") || value.ContainsKey("g") || value.ContainsKey("b"))
                {
                    x = value.GetFloat("r", x);
                    y = value.GetFloat("g", y);
                    z = value.GetFloat("b", z);
                    w = value.GetFloat("a", 1f);
                }
            }
            else if (value.IsNumber)
            {
                x = y = z = w = value.AsFloat();
            }

            return new[] { x, y, z, w };
        }

        #endregion

        /// <summary>
        /// 把 JSON 转为"纯 CLR 对象"：对象→Dictionary&lt;string,object&gt;，数组→List&lt;object&gt;，
        /// 字符串→string，数值→double，布尔→bool，null→null。
        /// 用于把未知结构的参数原样存入变量表。
        /// </summary>
        public static object ToPlainObject(JsonValue value)
        {
            if (value == null || value.IsNull)
            {
                return null;
            }

            switch (value.Type)
            {
                case JsonValueType.String:
                    return value.StringValue;
                case JsonValueType.Boolean:
                    return value.BooleanValue;
                case JsonValueType.Number:
                    return value.NumberValue;
                case JsonValueType.Array:
                {
                    var list = new List<object>();
                    foreach (var item in value.ArrayValue)
                    {
                        list.Add(ToPlainObject(item));
                    }
                    return list;
                }
                case JsonValueType.Object:
                {
                    var map = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var key in value.Keys)
                    {
                        map[key] = ToPlainObject(value[key]);
                    }
                    return map;
                }
                default:
                    return null;
            }
        }

        #region 序列化

        /// <summary>把任意对象转为 JsonValue</summary>
        public static JsonValue FromObject(object obj)
        {
            return WriteValue(obj, 0);
        }

        private static JsonValue WriteValue(object obj, int depth)
        {
            if (obj == null)
            {
                return JsonValue.Null;
            }

            if (depth > 32)
            {
                return JsonValue.Null;
            }

            if (obj is JsonValue json)
            {
                return json;
            }

            var type = obj.GetType();

            if (type.IsEnum)
            {
                return JsonValue.Of(obj.ToString());
            }

            if (obj is string s)
            {
                return JsonValue.Of(s);
            }
            if (obj is bool b)
            {
                return JsonValue.Of(b);
            }
            if (obj is char c)
            {
                return JsonValue.Of(c.ToString());
            }
            if (IsNumericType(type))
            {
                return JsonValue.Of(Convert.ToDouble(obj, CultureInfo.InvariantCulture));
            }
            if (obj is decimal dec)
            {
                return JsonValue.Of(dec.ToString(CultureInfo.InvariantCulture));
            }

            if (obj is Vector2 v2)
            {
                return JsonValue.FromArray(new[] { JsonValue.Of(v2.x), JsonValue.Of(v2.y) });
            }
            if (obj is Vector3 v3)
            {
                return JsonValue.FromArray(new[] { JsonValue.Of(v3.x), JsonValue.Of(v3.y), JsonValue.Of(v3.z) });
            }
            if (obj is Vector4 v4)
            {
                return JsonValue.FromArray(new[]
                    { JsonValue.Of(v4.x), JsonValue.Of(v4.y), JsonValue.Of(v4.z), JsonValue.Of(v4.w) });
            }
            if (obj is Color col)
            {
                return JsonValue.FromArray(new[]
                    { JsonValue.Of(col.r), JsonValue.Of(col.g), JsonValue.Of(col.b), JsonValue.Of(col.a) });
            }

            if (obj is IDictionary dict)
            {
                var result = JsonValue.NewObject();
                foreach (DictionaryEntry entry in dict)
                {
                    result.Set(entry.Key == null ? string.Empty : entry.Key.ToString(), WriteValue(entry.Value, depth + 1));
                }
                return result;
            }

            if (obj is IEnumerable enumerable)
            {
                var array = JsonValue.NewArray();
                foreach (var item in enumerable)
                {
                    array.Add(WriteValue(item, depth + 1));
                }
                return array;
            }

            var members = GetMembers(type);
            if (members.Count == 0)
            {
                return JsonValue.Of(obj.ToString());
            }

            var objNode = JsonValue.NewObject();
            foreach (var member in members)
            {
                try
                {
                    objNode.Set(member.Name, WriteValue(member.Get(obj), depth + 1));
                }
                catch (Exception)
                {
                    // 单个成员序列化失败时忽略，保证整体可用
                }
            }
            return objNode;
        }

        #endregion

        #region 成员缓存

        private static List<MemberAccessor> GetMembers(Type type)
        {
            lock (CacheLock)
            {
                List<MemberAccessor> members;
                if (Cache.TryGetValue(type, out members))
                {
                    return members;
                }

                members = new List<MemberAccessor>();

                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                foreach (var field in type.GetFields(flags))
                {
                    if (field.IsStatic || field.IsLiteral)
                    {
                        continue;
                    }
                    var isPublic = field.IsPublic;
                    var hasSerializeField = field.GetCustomAttribute<SerializeField>() != null;
                    if (!isPublic && !hasSerializeField)
                    {
                        continue;
                    }
                    if (field.FieldType.IsPointer)
                    {
                        continue;
                    }
                    members.Add(new MemberAccessor(ResolveName(field), field.FieldType, field));
                }

                foreach (var property in type.GetProperties(flags))
                {
                    if (!property.CanRead || !property.CanWrite)
                    {
                        continue;
                    }
                    if (property.GetIndexParameters().Length > 0)
                    {
                        continue;
                    }
                    var getter = property.GetGetMethod(true);
                    if (getter == null || !getter.IsPublic)
                    {
                        continue;
                    }
                    members.Add(new MemberAccessor(ResolveName(property), property.PropertyType, property));
                }

                Cache[type] = members;
                return members;
            }
        }

        private static string ResolveName(MemberInfo member)
        {
            var attr = member.GetCustomAttribute<JsonMemberAttribute>();
            return attr != null && !string.IsNullOrEmpty(attr.Name) ? attr.Name : member.Name;
        }

        /// <summary>清空成员缓存（仅在增删类型结构后需要）</summary>
        public static void ClearCache()
        {
            lock (CacheLock)
            {
                Cache.Clear();
            }
        }

        private static bool IsNumericType(Type type)
        {
            return type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
                   type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
                   type == typeof(float) || type == typeof(double);
        }

        /// <summary>
        /// 统一封装字段与属性的读写
        /// </summary>
        private sealed class MemberAccessor
        {
            private readonly FieldInfo _field;
            private readonly PropertyInfo _property;

            public string Name { get; }

            public Type ValueType { get; }

            public MemberAccessor(string name, Type valueType, MemberInfo member)
            {
                Name = name;
                ValueType = valueType;
                _field = member as FieldInfo;
                _property = member as PropertyInfo;
            }

            public void Set(object target, object value)
            {
                if (_field != null)
                {
                    _field.SetValue(target, value);
                }
                else if (_property != null)
                {
                    _property.SetValue(target, value, null);
                }
            }

            public object Get(object target)
            {
                if (_field != null)
                {
                    return _field.GetValue(target);
                }
                return _property != null ? _property.GetValue(target, null) : null;
            }
        }

        #endregion
    }

    /// <summary>
    /// 映射过程中的异常（例如字段类型不兼容）
    /// </summary>
    public sealed class JsonMappingException : Exception
    {
        public JsonMappingException(string message, Exception inner = null) : base(message, inner)
        {
        }
    }
}
