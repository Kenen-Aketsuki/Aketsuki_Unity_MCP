using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// JSON 解析异常，携带出错位置，便于定位配置文件错误
    /// </summary>
    public sealed class JsonParseException : Exception
    {
        /// <summary>出错字符下标</summary>
        public int Position { get; }

        /// <summary>出错行号（从 1 开始）</summary>
        public int Line { get; }

        /// <summary>出错列号（从 1 开始）</summary>
        public int Column { get; }

        public JsonParseException(string message, int position, int line, int column)
            : base($"{message}（第 {line} 行，第 {column} 列，字符下标 {position}）")
        {
            Position = position;
            Line = line;
            Column = column;
        }
    }

    /// <summary>
    /// 手写递归下降 JSON 解析器
    /// <para>
    /// 相比标准 JSON 额外宽容支持：单行注释 //、多行注释 /* */、结尾多余逗号、+ 开头的数字。
    /// 这样指令文件可以直接手写而不用担心格式细节。
    /// </para>
    /// </summary>
    public static class JsonParser
    {
        /// <summary>解析 JSON 文本</summary>
        /// <exception cref="JsonParseException">文本不是合法 JSON 时抛出</exception>
        public static JsonValue Parse(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            var reader = new Reader(text);
            reader.SkipInsignificant();
            var value = reader.ReadValue(0);
            reader.SkipInsignificant();
            if (!reader.IsEnd)
            {
                throw reader.Error($"JSON 结束后仍有多余内容 '{reader.Current}'");
            }
            return value;
        }

        /// <summary>尝试解析，失败时返回 false 而不抛异常</summary>
        public static bool TryParse(string text, out JsonValue value)
        {
            try
            {
                value = Parse(text);
                return true;
            }
            catch (Exception)
            {
                value = JsonValue.Null;
                return false;
            }
        }

        /// <summary>
        /// 解析时的递归状态机
        /// </summary>
        private sealed class Reader
        {
            private const int MaxDepth = 64;

            private readonly string _src;
            private int _pos;

            public Reader(string src)
            {
                _src = src;
                _pos = 0;
                // 跳过 UTF-8 BOM
                if (_src.Length > 0 && _src[0] == '\uFEFF')
                {
                    _pos = 1;
                }
            }

            public bool IsEnd => _pos >= _src.Length;

            public char Current => _pos < _src.Length ? _src[_pos] : '\0';

            /// <summary>跳过空白与注释</summary>
            public void SkipInsignificant()
            {
                while (_pos < _src.Length)
                {
                    var c = _src[_pos];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' || c == '\v')
                    {
                        _pos++;
                        continue;
                    }

                    // 单行注释
                    if (c == '/' && _pos + 1 < _src.Length && _src[_pos + 1] == '/')
                    {
                        _pos += 2;
                        while (_pos < _src.Length && _src[_pos] != '\n')
                        {
                            _pos++;
                        }
                        continue;
                    }

                    // 多行注释
                    if (c == '/' && _pos + 1 < _src.Length && _src[_pos + 1] == '*')
                    {
                        _pos += 2;
                        var closed = false;
                        while (_pos + 1 < _src.Length)
                        {
                            if (_src[_pos] == '*' && _src[_pos + 1] == '/')
                            {
                                _pos += 2;
                                closed = true;
                                break;
                            }
                            _pos++;
                        }
                        if (!closed)
                        {
                            throw Error("多行注释未闭合");
                        }
                        continue;
                    }

                    break;
                }
            }

            public JsonValue ReadValue(int depth)
            {
                if (depth > MaxDepth)
                {
                    throw Error("JSON 嵌套层数超过上限 " + MaxDepth);
                }

                SkipInsignificant();
                if (IsEnd)
                {
                    throw Error("意外结束，期望一个 JSON 值");
                }

                switch (Current)
                {
                    case '{':
                        return ReadObject(depth);
                    case '[':
                        return ReadArray(depth);
                    case '"':
                        return JsonValue.Of(ReadString());
                    case 't':
                        ExpectWord("true");
                        return JsonValue.True;
                    case 'f':
                        ExpectWord("false");
                        return JsonValue.False;
                    case 'n':
                        ExpectWord("null");
                        return JsonValue.Null;
                    default:
                        return ReadNumber();
                }
            }

            private JsonValue ReadObject(int depth)
            {
                Expect('{');
                var map = new Dictionary<string, JsonValue>(StringComparer.Ordinal);

                SkipInsignificant();
                if (Current == '}')
                {
                    _pos++;
                    return JsonValue.FromObject(map);
                }

                while (true)
                {
                    SkipInsignificant();
                    if (Current != '"')
                    {
                        throw Error($"期望对象键（双引号字符串），实际为 '{Current}'");
                    }

                    var key = ReadString();

                    SkipInsignificant();
                    Expect(':');

                    var value = ReadValue(depth + 1);
                    map[key] = value;

                    SkipInsignificant();
                    if (Current == ',')
                    {
                        _pos++;
                        // 宽容：允许结尾多余逗号
                        SkipInsignificant();
                        if (Current == '}')
                        {
                            _pos++;
                            break;
                        }
                        continue;
                    }

                    if (Current == '}')
                    {
                        _pos++;
                        break;
                    }

                    throw Error($"期望 ',' 或 '}}'，实际为 '{Current}'");
                }

                return JsonValue.FromObject(map);
            }

            private JsonValue ReadArray(int depth)
            {
                Expect('[');
                var list = new List<JsonValue>();

                SkipInsignificant();
                if (Current == ']')
                {
                    _pos++;
                    return JsonValue.FromArray(list);
                }

                while (true)
                {
                    list.Add(ReadValue(depth + 1));

                    SkipInsignificant();
                    if (Current == ',')
                    {
                        _pos++;
                        SkipInsignificant();
                        if (Current == ']')
                        {
                            _pos++;
                            break;
                        }
                        continue;
                    }

                    if (Current == ']')
                    {
                        _pos++;
                        break;
                    }

                    throw Error($"期望 ',' 或 ']'，实际为 '{Current}'");
                }

                return JsonValue.FromArray(list);
            }

            private string ReadString()
            {
                Expect('"');
                var sb = new StringBuilder();

                while (true)
                {
                    if (IsEnd)
                    {
                        throw Error("字符串未闭合");
                    }

                    var c = _src[_pos++];

                    if (c == '"')
                    {
                        break;
                    }

                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }

                    if (IsEnd)
                    {
                        throw Error("转义符后意外结束");
                    }

                    var esc = _src[_pos++];
                    switch (esc)
                    {
                        case '"':
                            sb.Append('"');
                            break;
                        case '\\':
                            sb.Append('\\');
                            break;
                        case '/':
                            sb.Append('/');
                            break;
                        case 'b':
                            sb.Append('\b');
                            break;
                        case 'f':
                            sb.Append('\f');
                            break;
                        case 'n':
                            sb.Append('\n');
                            break;
                        case 'r':
                            sb.Append('\r');
                            break;
                        case 't':
                            sb.Append('\t');
                            break;
                        case 'u':
                            sb.Append(ReadUnicodeEscape());
                            break;
                        default:
                            throw Error($"非法转义字符 '\\{esc}'");
                    }
                }

                return sb.ToString();
            }

            private char ReadUnicodeEscape()
            {
                if (_pos + 4 > _src.Length)
                {
                    throw Error("\\u 转义需要 4 位十六进制数");
                }

                var hex = _src.Substring(_pos, 4);
                _pos += 4;
                if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                {
                    throw Error($"\\u{hex} 不是合法十六进制数");
                }
                return (char)code;
            }

            private JsonValue ReadNumber()
            {
                var start = _pos;

                if (Current == '-' || Current == '+')
                {
                    _pos++;
                }

                var digits = 0;
                while (!IsEnd && char.IsDigit(_src[_pos]))
                {
                    _pos++;
                    digits++;
                }

                if (!IsEnd && _src[_pos] == '.')
                {
                    _pos++;
                    while (!IsEnd && char.IsDigit(_src[_pos]))
                    {
                        _pos++;
                        digits++;
                    }
                }

                if (digits == 0)
                {
                    throw Error($"非法数值，起始字符 '{Current}'");
                }

                if (!IsEnd && (_src[_pos] == 'e' || _src[_pos] == 'E'))
                {
                    _pos++;
                    if (!IsEnd && (_src[_pos] == '+' || _src[_pos] == '-'))
                    {
                        _pos++;
                    }
                    var expDigits = 0;
                    while (!IsEnd && char.IsDigit(_src[_pos]))
                    {
                        _pos++;
                        expDigits++;
                    }
                    if (expDigits == 0)
                    {
                        throw Error("指数部分缺少数字");
                    }
                }

                var raw = _src.Substring(start, _pos - start);
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    throw Error($"无法解析数值 '{raw}'");
                }
                return JsonValue.Of(value);
            }

            private void Expect(char expected)
            {
                if (IsEnd || _src[_pos] != expected)
                {
                    throw Error($"期望 '{expected}'，实际为 '{Current}'");
                }
                _pos++;
            }

            private void ExpectWord(string word)
            {
                if (_pos + word.Length > _src.Length ||
                    string.CompareOrdinal(_src, _pos, word, 0, word.Length) != 0)
                {
                    throw Error($"期望字面量 '{word}'");
                }
                _pos += word.Length;
            }

            /// <summary>构造带行列信息的解析异常</summary>
            public JsonParseException Error(string message)
            {
                var line = 1;
                var column = 1;
                for (var i = 0; i < _pos && i < _src.Length; i++)
                {
                    if (_src[i] == '\n')
                    {
                        line++;
                        column = 1;
                    }
                    else
                    {
                        column++;
                    }
                }
                return new JsonParseException(message, _pos, line, column);
            }
        }
    }
}
