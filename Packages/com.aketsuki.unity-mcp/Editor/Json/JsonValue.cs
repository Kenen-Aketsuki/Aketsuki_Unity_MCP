using System;
using System.Collections.Generic;
using System.Globalization;

namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// JSON 值类型枚举
    /// </summary>
    public enum JsonValueType
    {
        Null,
        Boolean,
        Number,
        String,
        Array,
        Object
    }

    /// <summary>
    /// 轻量级 JSON 值模型（树形结构，不依赖任何第三方库）
    /// <para>
    /// 不使用 UnityEngine.JsonUtility 的原因：JsonUtility 只能反序列化为"编译期已知的结构体"，
    /// 而指令系统需要在运行期接收"未知结构的参数包"，再交给对应的指令类自行解析。
    /// </para>
    /// <para>
    /// 该类型设计为只读（除显式提供的 Add/Set 方法外），所有节点均可安全共享。
    /// </para>
    /// </summary>
    public sealed class JsonValue
    {
        #region 静态常量与构造

        /// <summary>JSON null</summary>
        public static readonly JsonValue Null = new JsonValue(JsonValueType.Null, null);

        /// <summary>JSON true</summary>
        public static readonly JsonValue True = new JsonValue(JsonValueType.Boolean, true);

        /// <summary>JSON false</summary>
        public static readonly JsonValue False = new JsonValue(JsonValueType.Boolean, false);

        private readonly object _value;

        /// <summary>当前节点的 JSON 类型</summary>
        public JsonValueType Type { get; }

        private JsonValue(JsonValueType type, object value)
        {
            Type = type;
            _value = value;
        }

        #endregion

        #region 工厂方法

        /// <summary>构造布尔值</summary>
        public static JsonValue Of(bool value)
        {
            return value ? True : False;
        }

        /// <summary>构造字符串值；传入 null 时返回 JSON null</summary>
        public static JsonValue Of(string value)
        {
            return value == null ? Null : new JsonValue(JsonValueType.String, value);
        }

        /// <summary>构造数值</summary>
        public static JsonValue Of(double value)
        {
            return new JsonValue(JsonValueType.Number, value);
        }

        /// <summary>构造数值；内部统一按 double 存储，避免读取时出现装箱类型不一致</summary>
        public static JsonValue Of(long value)
        {
            return new JsonValue(JsonValueType.Number, (double)value);
        }

        /// <summary>构造数值</summary>
        public static JsonValue Of(int value)
        {
            return new JsonValue(JsonValueType.Number, (double)value);
        }

        /// <summary>构造数值</summary>
        public static JsonValue Of(float value)
        {
            return new JsonValue(JsonValueType.Number, (double)value);
        }

        /// <summary>空对象（每次返回新的可变实例，避免共享状态被意外修改）</summary>
        public static JsonValue EmptyObject => NewObject();

        /// <summary>空数组（每次返回新的可变实例）</summary>
        public static JsonValue EmptyArray => NewArray();

        /// <summary>构造一个新的空数组（可变）</summary>
        public static JsonValue NewArray()
        {
            return new JsonValue(JsonValueType.Array, new List<JsonValue>());
        }

        /// <summary>构造一个新的空对象（可变）</summary>
        public static JsonValue NewObject()
        {
            return new JsonValue(JsonValueType.Object, new Dictionary<string, JsonValue>(StringComparer.Ordinal));
        }

        /// <summary>由一组元素构造数组</summary>
        public static JsonValue FromArray(IEnumerable<JsonValue> items)
        {
            var list = new List<JsonValue>();
            if (items != null)
            {
                list.AddRange(items);
            }
            return new JsonValue(JsonValueType.Array, list);
        }

        /// <summary>由一组键值对构造对象</summary>
        public static JsonValue FromObject(IEnumerable<KeyValuePair<string, JsonValue>> items)
        {
            var map = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            if (items != null)
            {
                foreach (var kv in items)
                {
                    map[kv.Key] = kv.Value;
                }
            }
            return new JsonValue(JsonValueType.Object, map);
        }

        /// <summary>解析 JSON 文本（等价于 JsonParser.Parse）</summary>
        public static JsonValue Parse(string text)
        {
            return JsonParser.Parse(text);
        }

        #endregion

        #region 类型判定

        /// <summary>是否为 null</summary>
        public bool IsNull => Type == JsonValueType.Null;

        /// <summary>是否为布尔</summary>
        public bool IsBoolean => Type == JsonValueType.Boolean;

        /// <summary>是否为数值</summary>
        public bool IsNumber => Type == JsonValueType.Number;

        /// <summary>是否为字符串</summary>
        public bool IsString => Type == JsonValueType.String;

        /// <summary>是否为数组</summary>
        public bool IsArray => Type == JsonValueType.Array;

        /// <summary>是否为对象</summary>
        public bool IsObject => Type == JsonValueType.Object;

        /// <summary>是否为容器（数组或对象）</summary>
        public bool IsContainer => Type == JsonValueType.Array || Type == JsonValueType.Object;

        /// <summary>是否为"空值"（null / 空数组 / 空对象 / 空字符串）</summary>
        public bool IsEmpty
        {
            get
            {
                switch (Type)
                {
                    case JsonValueType.Null:
                        return true;
                    case JsonValueType.String:
                        return string.IsNullOrEmpty((string)_value);
                    case JsonValueType.Array:
                        return ((List<JsonValue>)_value).Count == 0;
                    case JsonValueType.Object:
                        return ((Dictionary<string, JsonValue>)_value).Count == 0;
                    default:
                        return false;
                }
            }
        }

        #endregion

        #region 严格取值（类型不符时抛出）

        /// <summary>以布尔取值，类型不符时抛异常</summary>
        public bool BooleanValue
        {
            get
            {
                EnsureType(JsonValueType.Boolean);
                return (bool)_value;
            }
        }

        /// <summary>以数值取值，类型不符时抛异常</summary>
        public double NumberValue
        {
            get
            {
                EnsureType(JsonValueType.Number);
                return (double)_value;
            }
        }

        /// <summary>以字符串取值，类型不符时抛异常</summary>
        public string StringValue
        {
            get
            {
                EnsureType(JsonValueType.String);
                return (string)_value;
            }
        }

        /// <summary>以数组取值，类型不符时抛异常</summary>
        public IReadOnlyList<JsonValue> ArrayValue
        {
            get
            {
                EnsureType(JsonValueType.Array);
                return (List<JsonValue>)_value;
            }
        }

        /// <summary>以对象取值，类型不符时抛异常</summary>
        public IReadOnlyDictionary<string, JsonValue> ObjectValue
        {
            get
            {
                EnsureType(JsonValueType.Object);
                return (Dictionary<string, JsonValue>)_value;
            }
        }

        private void EnsureType(JsonValueType expected)
        {
            if (Type != expected)
            {
                throw new InvalidCastException($"[Json] 期望类型 {expected}，实际为 {Type}");
            }
        }

        #endregion

        #region 宽容取值（类型不符时返回默认值）

        /// <summary>转为布尔；字符串 "true"/"1"/"yes"/"on" 均视为真</summary>
        public bool AsBool(bool fallback = false)
        {
            switch (Type)
            {
                case JsonValueType.Boolean:
                    return (bool)_value;
                case JsonValueType.Number:
                    return Math.Abs((double)_value) > 1e-9;
                case JsonValueType.String:
                {
                    var s = (string)_value;
                    if (bool.TryParse(s, out var b))
                    {
                        return b;
                    }
                    s = s.Trim().ToLowerInvariant();
                    if (s == "1" || s == "yes" || s == "on" || s == "true")
                    {
                        return true;
                    }
                    if (s == "0" || s == "no" || s == "off" || s == "false")
                    {
                        return false;
                    }
                    return fallback;
                }
                default:
                    return fallback;
            }
        }

        /// <summary>转为双精度数值</summary>
        public double AsDouble(double fallback = 0d)
        {
            switch (Type)
            {
                case JsonValueType.Number:
                    return (double)_value;
                case JsonValueType.Boolean:
                    return (bool)_value ? 1d : 0d;
                case JsonValueType.String:
                    return double.TryParse((string)_value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                        ? d
                        : fallback;
                default:
                    return fallback;
            }
        }

        /// <summary>转为长整型（数值为小数时截断）</summary>
        public long AsLong(long fallback = 0L)
        {
            switch (Type)
            {
                case JsonValueType.Number:
                    return (long)(double)_value;
                case JsonValueType.String:
                    return long.TryParse((string)_value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                        ? l
                        : fallback;
                default:
                    return fallback;
            }
        }

        /// <summary>转为 32 位整数</summary>
        public int AsInt(int fallback = 0)
        {
            var l = AsLong(fallback);
            if (l > int.MaxValue)
            {
                return int.MaxValue;
            }
            if (l < int.MinValue)
            {
                return int.MinValue;
            }
            return (int)l;
        }

        /// <summary>转为单精度浮点</summary>
        public float AsFloat(float fallback = 0f)
        {
            return (float)AsDouble(fallback);
        }

        /// <summary>转为字符串；数值与布尔会转换，其余类型返回默认值</summary>
        public string AsString(string fallback = null)
        {
            switch (Type)
            {
                case JsonValueType.String:
                    return (string)_value;
                case JsonValueType.Number:
                    return ((double)_value).ToString("R", CultureInfo.InvariantCulture);
                case JsonValueType.Boolean:
                    return (bool)_value ? "true" : "false";
                default:
                    return fallback;
            }
        }

        /// <summary>转为枚举（支持名称或数值）</summary>
        public T AsEnum<T>(T fallback = default) where T : struct
        {
            var type = typeof(T);
            if (!type.IsEnum)
            {
                return fallback;
            }

            if (Type == JsonValueType.String)
            {
                var text = (string)_value;
                if (Enum.IsDefined(type, text))
                {
                    return (T)Enum.Parse(type, text, true);
                }
                // 大小写不敏感匹配
                foreach (var name in Enum.GetNames(type))
                {
                    if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                    {
                        return (T)Enum.Parse(type, name);
                    }
                }
                return fallback;
            }

            if (Type == JsonValueType.Number)
            {
                try
                {
                    return (T)Enum.ToObject(type, Convert.ToInt32((double)_value));
                }
                catch (Exception)
                {
                    return fallback;
                }
            }

            return fallback;
        }

        /// <summary>转为指定类型（基本类型 / 枚举 / 可空类型 / 复杂对象）</summary>
        public T As<T>(T fallback = default)
        {
            var result = JsonMapper.ToObject(this, typeof(T), fallback);
            return result == null ? fallback : (T)result;
        }

        #endregion

        #region 容器访问

        /// <summary>元素数量；非容器返回 0</summary>
        public int Count
        {
            get
            {
                switch (Type)
                {
                    case JsonValueType.Array:
                        return ((List<JsonValue>)_value).Count;
                    case JsonValueType.Object:
                        return ((Dictionary<string, JsonValue>)_value).Count;
                    default:
                        return 0;
                }
            }
        }

        /// <summary>对象是否包含指定键</summary>
        public bool ContainsKey(string key)
        {
            return Type == JsonValueType.Object && ((Dictionary<string, JsonValue>)_value).ContainsKey(key);
        }

        /// <summary>尝试获取对象的字段值</summary>
        public bool TryGet(string key, out JsonValue value)
        {
            // 注意：Dictionary.TryGetValue 失败时会把 value 置为 null，
            // 这里必须统一回落到 JsonValue.Null，避免调用方拿到 null 引用
            if (Type == JsonValueType.Object && key != null &&
                ((Dictionary<string, JsonValue>)_value).TryGetValue(key, out value))
            {
                return true;
            }

            value = Null;
            return false;
        }

        /// <summary>获取对象字段，键不存在时返回 JSON null</summary>
        public JsonValue this[string key]
        {
            get
            {
                TryGet(key, out var v);
                return v;
            }
        }

        /// <summary>获取数组元素，越界时返回 JSON null</summary>
        public JsonValue this[int index]
        {
            get
            {
                if (Type == JsonValueType.Array)
                {
                    var list = (List<JsonValue>)_value;
                    return index >= 0 && index < list.Count ? list[index] : Null;
                }
                return Null;
            }
        }

        /// <summary>对象的所有键</summary>
        public IEnumerable<string> Keys
        {
            get
            {
                if (Type == JsonValueType.Object)
                {
                    return ((Dictionary<string, JsonValue>)_value).Keys;
                }
                return new string[0];
            }
        }

        /// <summary>转为元素列表（非数组时返回空列表）</summary>
        public List<JsonValue> Items
        {
            get
            {
                if (Type == JsonValueType.Array)
                {
                    return new List<JsonValue>((List<JsonValue>)_value);
                }
                return new List<JsonValue>();
            }
        }

        #endregion

        #region 对象字段的快捷读取

        /// <summary>读取字符串字段</summary>
        public string GetString(string key, string fallback = null)
        {
            return TryGet(key, out var v) ? v.AsString(fallback) : fallback;
        }

        /// <summary>读取整数字段</summary>
        public int GetInt(string key, int fallback = 0)
        {
            return TryGet(key, out var v) ? v.AsInt(fallback) : fallback;
        }

        /// <summary>读取浮点字段</summary>
        public float GetFloat(string key, float fallback = 0f)
        {
            return TryGet(key, out var v) ? v.AsFloat(fallback) : fallback;
        }

        /// <summary>读取双精度字段</summary>
        public double GetDouble(string key, double fallback = 0d)
        {
            return TryGet(key, out var v) ? v.AsDouble(fallback) : fallback;
        }

        /// <summary>读取布尔字段</summary>
        public bool GetBool(string key, bool fallback = false)
        {
            return TryGet(key, out var v) ? v.AsBool(fallback) : fallback;
        }

        /// <summary>读取枚举字段</summary>
        public T GetEnum<T>(string key, T fallback = default) where T : struct
        {
            return TryGet(key, out var v) ? v.AsEnum(fallback) : fallback;
        }

        /// <summary>读取子对象/任意类型字段</summary>
        public T Get<T>(string key, T fallback = default)
        {
            return TryGet(key, out var v) ? v.As(fallback) : fallback;
        }

        #endregion

        #region 修改（仅对新建的可变容器有效）

        /// <summary>向数组追加元素；当前节点不是数组时抛异常</summary>
        public JsonValue Add(JsonValue item)
        {
            EnsureType(JsonValueType.Array);
            ((List<JsonValue>)_value).Add(item ?? Null);
            return this;
        }

        /// <summary>设置对象字段；当前节点不是对象时抛异常</summary>
        public JsonValue Set(string key, JsonValue item)
        {
            EnsureType(JsonValueType.Object);
            ((Dictionary<string, JsonValue>)_value)[key] = item ?? Null;
            return this;
        }

        /// <summary>移除对象字段；当前节点不是对象时抛异常</summary>
        public bool Remove(string key)
        {
            EnsureType(JsonValueType.Object);
            return ((Dictionary<string, JsonValue>)_value).Remove(key);
        }

        #endregion

        #region 输出

        /// <summary>序列化为 JSON 文本</summary>
        public string ToJson(bool pretty = false)
        {
            return JsonWriter.Write(this, pretty);
        }

        /// <summary>紧凑 JSON 文本</summary>
        public override string ToString()
        {
            return JsonWriter.Write(this, false);
        }

        /// <summary>缩进格式 JSON 文本</summary>
        public string ToPrettyString()
        {
            return JsonWriter.Write(this, true);
        }

        #endregion
    }
}
