using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CivilRev.CommandSystem
{
    /// <summary>
    /// JSON 序列化器：把 <see cref="JsonValue"/> 写回文本
    /// </summary>
    public static class JsonWriter
    {
        /// <summary>序列化为 JSON 文本</summary>
        /// <param name="value">待序列化的值</param>
        /// <param name="pretty">是否缩进格式化</param>
        public static string Write(JsonValue value, bool pretty = false)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, value ?? JsonValue.Null, pretty ? 0 : -1);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, JsonValue value, int indent)
        {
            switch (value.Type)
            {
                case JsonValueType.Null:
                    sb.Append("null");
                    break;
                case JsonValueType.Boolean:
                    sb.Append(value.BooleanValue ? "true" : "false");
                    break;
                case JsonValueType.Number:
                    sb.Append(FormatNumber(value.NumberValue));
                    break;
                case JsonValueType.String:
                    WriteString(sb, value.StringValue);
                    break;
                case JsonValueType.Array:
                    WriteArray(sb, value, indent);
                    break;
                case JsonValueType.Object:
                    WriteObject(sb, value, indent);
                    break;
                default:
                    sb.Append("null");
                    break;
            }
        }

        private static void WriteArray(StringBuilder sb, JsonValue value, int indent)
        {
            var items = value.ArrayValue;
            if (items.Count == 0)
            {
                sb.Append("[]");
                return;
            }

            sb.Append('[');
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(indent >= 0 ? ",\n" : ",");
                }
                if (indent >= 0)
                {
                    sb.Append(' ', (indent + 1) * 2);
                }
                WriteValue(sb, items[i], indent >= 0 ? indent + 1 : -1);
            }
            if (indent >= 0)
            {
                sb.Append('\n').Append(' ', indent * 2);
            }
            sb.Append(']');
        }

        private static void WriteObject(StringBuilder sb, JsonValue value, int indent)
        {
            var map = value.ObjectValue;
            if (map.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            sb.Append('{');
            var first = true;
            foreach (var kv in map)
            {
                if (!first)
                {
                    sb.Append(indent >= 0 ? ",\n" : ",");
                }
                first = false;
                if (indent >= 0)
                {
                    sb.Append(' ', (indent + 1) * 2);
                }
                WriteString(sb, kv.Key);
                sb.Append(indent >= 0 ? ": " : ":");
                WriteValue(sb, kv.Value, indent >= 0 ? indent + 1 : -1);
            }
            if (indent >= 0)
            {
                sb.Append('\n').Append(' ', indent * 2);
            }
            sb.Append('}');
        }

        private static void WriteString(StringBuilder sb, string text)
        {
            sb.Append('"');
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < 0x20 || c == '\u007F')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        private static string FormatNumber(double number)
        {
            if (double.IsNaN(number) || double.IsInfinity(number))
            {
                // 标准 JSON 不支持 NaN/Infinity，退化为 null 以免生成非法文本
                return "null";
            }

            if (Math.Abs(number % 1) <= double.Epsilon && Math.Abs(number) < 1e15)
            {
                return ((long)number).ToString(CultureInfo.InvariantCulture);
            }
            return number.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
