using System;
using System.Collections.Generic;
using UnityEngine;

namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// 指令参数包：对 JSON 参数对象做一层强类型读取封装
    /// <para>
    /// 设计取舍：读取缺失参数一律返回默认值而非抛异常，
    /// 只有 <c>Require*</c> 系列才会因缺失而抛出 <see cref="CommandArgumentException"/>，
    /// 这样"必填/选填"由指令类自己声明，运行器无需感知。
    /// </para>
    /// </summary>
    public sealed class CommandArgs
    {
        /// <summary>空参数包</summary>
        public static readonly CommandArgs Empty = new CommandArgs(JsonValue.EmptyObject, null);

        private readonly JsonValue _root;
        private readonly string _commandId;

        // 被指令真正读过（或明确点过名）的参数。用途见 UnconsumedKeys：发现"参数名写错导致静默走默认值"。
        private readonly HashSet<string> _consumed = new HashSet<string>(StringComparer.Ordinal);

        // 指令绕开强类型读取、直接遍历原始参数（Raw / Keys）时置位：此时"没读过"这件事无从判断。
        private bool _consumeAll;

        /// <summary>构造参数包</summary>
        public CommandArgs(JsonValue root, string commandId = null)
        {
            _root = root ?? JsonValue.EmptyObject;
            _commandId = commandId;
        }

        /// <summary>
        /// 原始 JSON 节点，用于自定义解析
        /// <para>读它意味着"我自己遍历参数"，后续不再做未知参数检查（见 <see cref="UnconsumedKeys"/>）。</para>
        /// </summary>
        public JsonValue Raw
        {
            get
            {
                _consumeAll = true;
                return _root;
            }
        }

        /// <summary>参数个数</summary>
        public int Count => _root.Count;

        /// <summary>所有参数名</summary>
        public IEnumerable<string> Keys
        {
            get
            {
                _consumeAll = true;
                return _root.Keys;
            }
        }

        /// <summary>
        /// 传进来但没有任何强类型读取碰过的参数名（按原始顺序）。
        /// <para>
        /// 典型场景：<c>scene.hierarchy</c> 的真实参数是 <c>target</c> / <c>maxDepth</c>，
        /// 调用方写成了 <c>root</c> / <c>depth</c>（那是 <c>runtime.hierarchy</c> 的叫法）——两个名字都被
        /// 静默丢弃，指令按"无 target + 全深度"执行，回了整个场景 2.9 MB。有了这份清单，
        /// 运行器就能回一条"未知参数：root, depth"的警告，而不是让人去翻文档才发现。
        /// </para>
        /// <para>指令自己遍历过 <see cref="Raw"/> / <see cref="Keys"/> 时返回空——那种情况下它本来就能看到全部参数。</para>
        /// </summary>
        public List<string> UnconsumedKeys()
        {
            var result = new List<string>();
            if (_consumeAll)
            {
                return result;
            }

            foreach (var key in _root.Keys)
            {
                if (!_consumed.Contains(key))
                {
                    result.Add(key);
                }
            }

            return result;
        }

        private void Mark(string key)
        {
            if (!string.IsNullOrEmpty(key))
            {
                _consumed.Add(key);
            }
        }

        /// <summary>是否包含指定参数</summary>
        public bool Has(string key)
        {
            Mark(key);
            return _root.ContainsKey(key);
        }

        /// <summary>取原始节点，缺失时返回 JSON null</summary>
        public JsonValue Get(string key)
        {
            Mark(key);
            return _root[key];
        }

        /// <summary>尝试取原始节点</summary>
        public bool TryGet(string key, out JsonValue value)
        {
            Mark(key);
            return _root.TryGet(key, out value);
        }

        /// <summary>取必填原始节点，缺失时抛异常</summary>
        public JsonValue Require(string key)
        {
            Mark(key);
            if (!_root.TryGet(key, out var value) || value.IsNull)
            {
                throw Missing(key);
            }
            return value;
        }

        #region 选填读取

        /// <summary>读取字符串</summary>
        public string GetString(string key, string fallback = null)
        {
            Mark(key);
            return _root.GetString(key, fallback);
        }

        /// <summary>读取整数</summary>
        public int GetInt(string key, int fallback = 0)
        {
            Mark(key);
            return _root.GetInt(key, fallback);
        }

        /// <summary>读取浮点</summary>
        public float GetFloat(string key, float fallback = 0f)
        {
            Mark(key);
            return _root.GetFloat(key, fallback);
        }

        /// <summary>读取双精度</summary>
        public double GetDouble(string key, double fallback = 0d)
        {
            Mark(key);
            return _root.GetDouble(key, fallback);
        }

        /// <summary>读取布尔</summary>
        public bool GetBool(string key, bool fallback = false)
        {
            Mark(key);
            return _root.GetBool(key, fallback);
        }

        /// <summary>读取枚举</summary>
        public T GetEnum<T>(string key, T fallback = default) where T : struct
        {
            Mark(key);
            return _root.GetEnum(key, fallback);
        }

        /// <summary>读取三维向量，支持 [x,y,z] 数组或 {x,y,z} 对象</summary>
        public Vector3 GetVector3(string key, Vector3 fallback = default)
        {
            Mark(key);
            if (!_root.TryGet(key, out var node))
            {
                return fallback;
            }
            return node.As(fallback);
        }

        /// <summary>读取二维向量</summary>
        public Vector2 GetVector2(string key, Vector2 fallback = default)
        {
            Mark(key);
            if (!_root.TryGet(key, out var node))
            {
                return fallback;
            }
            return node.As(fallback);
        }

        /// <summary>读取字符串数组</summary>
        public string[] GetStringArray(string key, string[] fallback = null)
        {
            Mark(key);
            if (!_root.TryGet(key, out var node) || !node.IsArray)
            {
                return fallback ?? new string[0];
            }

            var items = node.ArrayValue;
            var result = new string[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                result[i] = items[i].AsString();
            }
            return result;
        }

        /// <summary>读取子节点并映射为指定类型（复杂参数推荐用法）</summary>
        public T GetObject<T>(string key, T fallback = default)
        {
            Mark(key);
            if (!_root.TryGet(key, out var node))
            {
                return fallback;
            }
            return node.As(fallback);
        }

        /// <summary>读取子节点列表</summary>
        public List<T> GetList<T>(string key)
        {
            Mark(key);
            var result = new List<T>();
            if (!_root.TryGet(key, out var node) || !node.IsArray)
            {
                return result;
            }

            foreach (var item in node.ArrayValue)
            {
                result.Add(item.As<T>());
            }
            return result;
        }

        #endregion

        #region 必填读取

        /// <summary>读取必填字符串</summary>
        public string RequireString(string key)
        {
            var value = GetString(key);
            if (string.IsNullOrEmpty(value))
            {
                throw Missing(key);
            }
            return value;
        }

        /// <summary>读取必填整数</summary>
        public int RequireInt(string key)
        {
            if (!Has(key))
            {
                throw Missing(key);
            }
            return GetInt(key);
        }

        /// <summary>读取必填浮点</summary>
        public float RequireFloat(string key)
        {
            if (!Has(key))
            {
                throw Missing(key);
            }
            return GetFloat(key);
        }

        /// <summary>读取必填布尔</summary>
        public bool RequireBool(string key)
        {
            if (!Has(key))
            {
                throw Missing(key);
            }
            return GetBool(key);
        }

        /// <summary>读取必填复杂对象</summary>
        public T RequireObject<T>(string key)
        {
            if (!Has(key))
            {
                throw Missing(key);
            }
            return GetObject<T>(key);
        }

        #endregion

        #region 参数名声明（usage 解析）

        /// <summary>
        /// 从 <see cref="CommandAttribute.Usage"/> 里取出参数名列表。
        /// <para>
        /// usage 的唯一合法形态是 <c>"name: type, name2: type2"</c>，但类型里会出现
        /// <c>[l,b,r,t]</c> / <c>string(rect|transform|none)</c> 这类带分隔符的写法，
        /// 所以要按"顶层逗号"切（括号内的逗号不算）。切出来的非 ASCII 片段一律丢弃——
        /// 那是中文说明而不是参数名，留着只会在提示里添乱。
        /// </para>
        /// <para>与 Python 桥的 usage→JSON Schema（<c>_split_top_level</c>）语义一致，只取名字、不管类型。</para>
        /// </summary>
        public static string[] DeclaredParameterNames(string usage)
        {
            var names = new List<string>();
            if (string.IsNullOrEmpty(usage))
            {
                return names.ToArray();
            }

            var depth = 0;
            var start = 0;
            for (var i = 0; i <= usage.Length; i++)
            {
                var atEnd = i == usage.Length;
                if (!atEnd)
                {
                    switch (usage[i])
                    {
                        case '[':
                        case '{':
                        case '(':
                        case '（':
                            depth++;
                            break;
                        case ']':
                        case '}':
                        case ')':
                        case '）':
                            if (depth > 0) depth--;
                            break;
                    }
                }

                if (!atEnd && (usage[i] != ',' || depth > 0))
                {
                    continue;
                }

                var part = usage.Substring(start, i - start).Trim();
                start = i + 1;
                if (part.Length == 0)
                {
                    continue;
                }

                var colon = part.IndexOf(':');
                if (colon < 0)
                {
                    colon = part.IndexOf('：');
                }

                var name = (colon > 0 ? part.Substring(0, colon) : part).Trim();
                if (IsParameterName(name))
                {
                    names.Add(name);
                }
            }

            return names.ToArray();
        }

        /// <summary>参数名只允许 ASCII 标识符（usage 里的中文片段不是参数名）</summary>
        private static bool IsParameterName(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c == '-';
                if (!ok && (i == 0 || c < '0' || c > '9'))
                {
                    return false;
                }
            }

            return true;
        }

        #endregion

        private CommandArgumentException Missing(string key)
        {
            return new CommandArgumentException(_commandId ?? "<unknown>", key, "缺少必填参数");
        }

        /// <summary>紧凑 JSON 文本</summary>
        public override string ToString()
        {
            return _root.ToJson(false);
        }

        /// <summary>缩进 JSON 文本</summary>
        public string ToPrettyString()
        {
            return _root.ToJson(true);
        }
    }
}
