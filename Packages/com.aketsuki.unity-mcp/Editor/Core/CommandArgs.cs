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

        /// <summary>构造参数包</summary>
        public CommandArgs(JsonValue root, string commandId = null)
        {
            _root = root ?? JsonValue.EmptyObject;
            _commandId = commandId;
        }

        /// <summary>原始 JSON 节点，用于自定义解析</summary>
        public JsonValue Raw => _root;

        /// <summary>参数个数</summary>
        public int Count => _root.Count;

        /// <summary>所有参数名</summary>
        public IEnumerable<string> Keys => _root.Keys;

        /// <summary>是否包含指定参数</summary>
        public bool Has(string key)
        {
            return _root.ContainsKey(key);
        }

        /// <summary>取原始节点，缺失时返回 JSON null</summary>
        public JsonValue Get(string key)
        {
            return _root[key];
        }

        /// <summary>尝试取原始节点</summary>
        public bool TryGet(string key, out JsonValue value)
        {
            return _root.TryGet(key, out value);
        }

        /// <summary>取必填原始节点，缺失时抛异常</summary>
        public JsonValue Require(string key)
        {
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
            return _root.GetString(key, fallback);
        }

        /// <summary>读取整数</summary>
        public int GetInt(string key, int fallback = 0)
        {
            return _root.GetInt(key, fallback);
        }

        /// <summary>读取浮点</summary>
        public float GetFloat(string key, float fallback = 0f)
        {
            return _root.GetFloat(key, fallback);
        }

        /// <summary>读取双精度</summary>
        public double GetDouble(string key, double fallback = 0d)
        {
            return _root.GetDouble(key, fallback);
        }

        /// <summary>读取布尔</summary>
        public bool GetBool(string key, bool fallback = false)
        {
            return _root.GetBool(key, fallback);
        }

        /// <summary>读取枚举</summary>
        public T GetEnum<T>(string key, T fallback = default) where T : struct
        {
            return _root.GetEnum(key, fallback);
        }

        /// <summary>读取三维向量，支持 [x,y,z] 数组或 {x,y,z} 对象</summary>
        public Vector3 GetVector3(string key, Vector3 fallback = default)
        {
            if (!_root.TryGet(key, out var node))
            {
                return fallback;
            }
            return node.As(fallback);
        }

        /// <summary>读取二维向量</summary>
        public Vector2 GetVector2(string key, Vector2 fallback = default)
        {
            if (!_root.TryGet(key, out var node))
            {
                return fallback;
            }
            return node.As(fallback);
        }

        /// <summary>读取字符串数组</summary>
        public string[] GetStringArray(string key, string[] fallback = null)
        {
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
            if (!_root.TryGet(key, out var node))
            {
                return fallback;
            }
            return node.As(fallback);
        }

        /// <summary>读取子节点列表</summary>
        public List<T> GetList<T>(string key)
        {
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
