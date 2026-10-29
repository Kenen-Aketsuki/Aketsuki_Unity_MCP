using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Aketsuki.AdbDebugger
{
    /// <summary>
    /// 手写 JSON 输出器（只写不读）。
    ///
    /// <para>
    /// <b>为什么不用指令系统的 <c>JsonValue</c>：</b>本工具刻意不引用指令系统的任何类型
    /// （可以只装它、不装指令系统）。对外只有一台设备列表与一份抓取状态，结构固定，
    /// 手写比引入依赖划算。指令系统那边读这段 JSON 时用自己的解析器，两边只以文本为契约。
    /// </para>
    /// </summary>
    internal sealed class JsonOut
    {
        private readonly StringBuilder _builder = new StringBuilder(256);
        private bool _first = true;

        internal JsonOut()
        {
            _builder.Append('{');
        }

        private void Key(string name)
        {
            if (!_first)
            {
                _builder.Append(',');
            }

            _first = false;
            AppendQuoted(_builder, name);
            _builder.Append(':');
        }

        internal JsonOut Str(string name, string value)
        {
            Key(name);
            if (value == null)
            {
                _builder.Append("null");
            }
            else
            {
                AppendQuoted(_builder, value);
            }

            return this;
        }

        internal JsonOut Num(string name, long value)
        {
            Key(name);
            _builder.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        internal JsonOut Num(string name, double value)
        {
            Key(name);
            _builder.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
            return this;
        }

        internal JsonOut Bool(string name, bool value)
        {
            Key(name);
            _builder.Append(value ? "true" : "false");
            return this;
        }

        /// <summary>嵌套一个对象；<paramref name="body"/> 收到的是一个新 builder</summary>
        internal JsonOut Obj(string name, Action<JsonOut> body)
        {
            Key(name);
            var inner = new JsonOut();
            body(inner);
            _builder.Append(inner.ToString());
            return this;
        }

        /// <summary>
        /// 原样塞入一段**已经是合法 JSON** 的文本（数组 / 对象）。
        /// 用来拼"对象数组"这类 <see cref="Arr(string,IEnumerable{string})"/> 表达不了的结构；
        /// 正确性由调用方保证——这里不做校验（本工具的输出全是自造的，不接外部输入）。
        /// </summary>
        internal JsonOut Raw(string name, string json)
        {
            Key(name);
            _builder.Append(string.IsNullOrEmpty(json) ? "null" : json);
            return this;
        }

        /// <summary>字符串数组</summary>
        internal JsonOut Arr(string name, IEnumerable<string> values)
        {
            Key(name);
            _builder.Append('[');
            var first = true;
            if (values != null)
            {
                foreach (var value in values)
                {
                    if (!first)
                    {
                        _builder.Append(',');
                    }

                    first = false;
                    AppendQuoted(_builder, value ?? string.Empty);
                }
            }

            _builder.Append(']');
            return this;
        }

        public override string ToString()
        {
            return _builder.ToString() + "}";
        }

        private static void AppendQuoted(StringBuilder builder, string text)
        {
            builder.Append('"');
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            builder.Append('"');
        }
    }
}
