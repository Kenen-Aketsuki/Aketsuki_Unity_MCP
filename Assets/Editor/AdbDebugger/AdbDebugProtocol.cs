using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace CivilRev.AdbDebugger
{
    /// <summary>
    /// ADB 调试工具的最小协议层。
    ///
    /// <para>
    /// <b>为什么不用通用 JSON 指令协议：</b>本工具只有 2 个动作（查状态、启停抓取），
    /// 参数全是扁平标量。为此引入一套 JSON 解析器与指令注册表是纯负担，而且会让人误以为
    /// 它和「Unity MCP 指令系统」共享代码。所以请求用 <b>query string</b>、响应用<b>手写 JSON</b>，
    /// 整个协议层不到 200 行、零解析依赖。
    /// </para>
    /// </summary>
    internal static class AdbDebugProtocol
    {
        /// <summary>请求路径前缀（都在本工具自己的端口上，不与指令系统共用）</summary>
        internal const string StatusPath = "/v1/status";
        internal const string LogcatPath = "/v1/logcat";

        /// <summary>把 <c>RawUrl</c> 拆成路径与查询串</summary>
        internal static void SplitUrl(string rawUrl, out string path, out string query)
        {
            path = rawUrl ?? "/";
            query = string.Empty;

            var index = path.IndexOf('?');
            if (index < 0)
            {
                return;
            }

            query = path.Substring(index + 1);
            path = path.Substring(0, index);
        }

        /// <summary>
        /// 解析 <c>a=1&amp;b=2</c>。键大小写不敏感（转小写存），值做 URL 解码。
        /// <para>不支持同名键多次出现——本协议里没有数组参数，重复键按后者覆盖。</para>
        /// </summary>
        internal static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(query))
            {
                return result;
            }

            foreach (var pair in query.Split('&'))
            {
                if (pair.Length == 0)
                {
                    continue;
                }

                var eq = pair.IndexOf('=');
                string key;
                string value;
                if (eq < 0)
                {
                    key = pair;
                    value = string.Empty;
                }
                else
                {
                    key = pair.Substring(0, eq);
                    value = pair.Substring(eq + 1);
                }

                key = Uri.UnescapeDataString(key.Replace('+', ' ')).Trim().ToLowerInvariant();
                value = Uri.UnescapeDataString(value.Replace('+', ' '));

                if (key.Length > 0)
                {
                    result[key] = value;
                }
            }

            return result;
        }

        /// <summary>取字符串参数，缺省返回 <paramref name="fallback"/></summary>
        internal static string Get(Dictionary<string, string> query, string key, string fallback)
        {
            string value;
            return query != null && query.TryGetValue(key.ToLowerInvariant(), out value) ? value : fallback;
        }

        /// <summary>取整数参数；解析失败时返回 <paramref name="fallback"/></summary>
        internal static int GetInt(Dictionary<string, string> query, string key, int fallback)
        {
            var text = Get(query, key, null);
            int value;
            if (text != null && int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }

            return fallback;
        }

        /// <summary>取布尔参数；<c>1/true/yes/on</c> 为真</summary>
        internal static bool GetBool(Dictionary<string, string> query, string key, bool fallback)
        {
            var text = Get(query, key, null);
            if (text == null)
            {
                return fallback;
            }

            switch (text.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on":
                    return true;
                case "0":
                case "false":
                case "no":
                case "off":
                    return false;
                default:
                    return fallback;
            }
        }

        /// <summary>参数是否存在（用来区分"没传"与"传了空值"）</summary>
        internal static bool Has(Dictionary<string, string> query, string key)
        {
            return query != null && query.ContainsKey(key.ToLowerInvariant());
        }
    }

    /// <summary>
    /// 手写 JSON 输出。只服务于本工具自己的响应——一个对象、几个键，够用即可。
    /// <para>
    /// 唯一必须小心的是字符串转义：日志文本里出现引号/反斜杠/换行是常态，
    /// 漏转义会让客户端拿到半个 JSON。
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

    /// <summary>
    /// ADB 工具自己的日志出口。
    /// <para>
    /// 不用指令系统的 <c>CommandLog</c>——本工具不引用它任何东西。日志仍然进 Unity 控制台、
    /// 仍带 <c>[ADB] </c> 前缀，这样人眼习惯和 <c>console.read contains:"[ADB]"</c> 的读法都不变。
    /// </para>
    /// </summary>
    internal static class AdbLog
    {
        internal const string Prefix = "[ADB] ";

        internal static void Info(string message)
        {
            Debug.Log(Prefix + message);
        }

        internal static void Warning(string message)
        {
            Debug.LogWarning(Prefix + message);
        }

        internal static void Error(string message)
        {
            Debug.LogError(Prefix + message);
        }
    }
}
