using System;
using System.Collections.Generic;
using System.Reflection;

namespace CivilRev.CommandSystem
{
    /// <summary>
    /// 指令描述信息：ID 与实现类的绑定关系
    /// </summary>
    public sealed class CommandDescriptor
    {
        /// <summary>指令唯一 ID</summary>
        public string Id { get; }

        /// <summary>实现类型</summary>
        public Type ImplementationType { get; }

        /// <summary>标记特性</summary>
        public CommandAttribute Attribute { get; }

        /// <summary>是否为仅编辑器指令</summary>
        public bool IsEditorOnly => Attribute != null && Attribute.EditorOnly;

        /// <summary>是否为运行时（Play 模式）指令；受 <see cref="RuntimeCommandGate"/> 门禁约束</summary>
        public bool IsRuntime => Attribute != null && Attribute.Runtime;

        /// <summary>描述文本</summary>
        public string Description => Attribute != null ? Attribute.Description : string.Empty;

        /// <summary>参数用法说明</summary>
        public string Usage => Attribute != null ? Attribute.Usage : string.Empty;

        /// <summary>展示名（类型名）</summary>
        public string DisplayName => ImplementationType != null ? ImplementationType.Name : "<instance>";

        private readonly ICommand _fixedInstance;

        public CommandDescriptor(string id, Type type, CommandAttribute attribute)
        {
            Id = id;
            ImplementationType = type;
            Attribute = attribute;
        }

        public CommandDescriptor(string id, ICommand instance)
        {
            Id = id;
            _fixedInstance = instance;
        }

        /// <summary>
        /// 创建指令实例
        /// <para>每次执行都会新建实例，指令类因此可以持有执行期临时状态</para>
        /// </summary>
        public ICommand CreateInstance()
        {
            if (_fixedInstance != null)
            {
                return _fixedInstance;
            }

            try
            {
                return (ICommand)Activator.CreateInstance(ImplementationType);
            }
            catch (Exception e)
            {
                throw new CommandException($"无法创建指令 '{Id}' 的实例（{ImplementationType?.Name}）：{e.Message}", e);
            }
        }
    }

    /// <summary>
    /// 指令注册表：维护 "字符串 ID → 指令类" 的映射
    /// <para>
    /// 查找大小写不敏感；同一个 ID 重复注册时会保留先注册者并输出警告，避免行为不确定。
    /// </para>
    /// </summary>
    public sealed class CommandRegistry
    {
        private static CommandRegistry _default;

        /// <summary>全局默认注册表（懒加载）</summary>
        public static CommandRegistry Default => _default ?? (_default = new CommandRegistry());

        private readonly Dictionary<string, CommandDescriptor> _map =
            new Dictionary<string, CommandDescriptor>(StringComparer.OrdinalIgnoreCase);

        private readonly List<CommandDescriptor> _order = new List<CommandDescriptor>();

        private bool _scanned;

        /// <summary>已注册指令数量</summary>
        public int Count => _map.Count;

        /// <summary>所有已注册指令（按注册顺序）</summary>
        public IEnumerable<CommandDescriptor> All => _order;

        #region 注册

        /// <summary>注册一个指令类型（类型需带 <see cref="CommandAttribute"/> 且实现 <see cref="ICommand"/>）</summary>
        public bool Register(Type type)
        {
            if (type == null)
            {
                return false;
            }

            if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
            {
                return false;
            }

            if (!typeof(ICommand).IsAssignableFrom(type))
            {
                CommandLog.Warning($"类型 {type.Name} 标记了 Command 特性但未实现 ICommand，已忽略");
                return false;
            }

            var attribute = type.GetCustomAttribute<CommandAttribute>(false);
            if (attribute == null || string.IsNullOrEmpty(attribute.Id))
            {
                CommandLog.Warning($"指令类 {type.Name} 缺少有效的 CommandAttribute.Id，已忽略");
                return false;
            }

#if !UNITY_EDITOR
            // 第二层保险：整套源码位于 Editor 目录下，正常不会走到这里。
            // 万一有人把文件挪出 Editor 目录，这里能保证依赖 UnityEditor 的指令至少不会被注册。
            // 真正的硬阻断在 Core/EditorOnlyGuard.cs（打玩家包时直接编译报错）。
            if (attribute.EditorOnly)
            {
                return false;
            }
#endif

            return Add(new CommandDescriptor(attribute.Id, type, attribute));
        }

        /// <summary>注册一个指令实例（适用于需要依赖注入的场景）</summary>
        public bool RegisterInstance(string id, ICommand instance)
        {
            if (string.IsNullOrEmpty(id) || instance == null)
            {
                return false;
            }
            return Add(new CommandDescriptor(id, instance));
        }

        /// <summary>批量注册类型（编辑器下可配合 TypeCache 使用，避免全量反射）</summary>
        public int RegisterTypes(IEnumerable<Type> types)
        {
            var count = 0;
            if (types == null)
            {
                return 0;
            }

            foreach (var type in types)
            {
                if (Register(type))
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>注销指令</summary>
        public bool Unregister(string id)
        {
            if (string.IsNullOrEmpty(id) || !_map.Remove(id))
            {
                return false;
            }
            _order.RemoveAll(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
            return true;
        }

        /// <summary>清空注册表</summary>
        public void Clear()
        {
            _map.Clear();
            _order.Clear();
            _scanned = false;
        }

        private bool Add(CommandDescriptor descriptor)
        {
            CommandDescriptor existing;
            if (_map.TryGetValue(descriptor.Id, out existing))
            {
                // 同一类型被多次扫描到时静默跳过（例如 TypeCache 与反射扫描同时命中）
                if (existing.ImplementationType == descriptor.ImplementationType && descriptor.ImplementationType != null)
                {
                    return false;
                }

                CommandLog.Warning(
                    $"指令 ID '{descriptor.Id}' 重复注册：已存在 {existing.DisplayName}，忽略 {descriptor.DisplayName}");
                return false;
            }

            _map[descriptor.Id] = descriptor;
            _order.Add(descriptor);
            return true;
        }

        #endregion

        #region 扫描

        /// <summary>
        /// 扫描当前应用域内所有程序集，自动发现带 <see cref="CommandAttribute"/> 的指令类
        /// <para>只会完整扫描一次，结果缓存；传入 force=true 可强制重新扫描</para>
        /// </summary>
        public int ScanAssemblies(bool force = false)
        {
            if (_scanned && !force)
            {
                return 0;
            }

            var before = _order.Count;
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                if (ShouldSkip(assembly))
                {
                    continue;
                }

                var types = SafeGetTypes(assembly);
                for (var i = 0; i < types.Count; i++)
                {
                    var type = types[i];
                    if (type == null || !type.IsDefined(typeof(CommandAttribute), false))
                    {
                        continue;
                    }
                    Register(type);
                }
            }

            _scanned = true;
            return _order.Count - before;
        }

        private static bool ShouldSkip(Assembly assembly)
        {
            var name = assembly.GetName().Name;
            if (string.IsNullOrEmpty(name))
            {
                return true;
            }

            // 系统程序集不可能包含指令，跳过可显著减少反射开销
            return name.StartsWith("System", StringComparison.Ordinal)
                   || name.StartsWith("mscorlib", StringComparison.Ordinal)
                   || name.StartsWith("netstandard", StringComparison.Ordinal)
                   || name.StartsWith("Microsoft", StringComparison.Ordinal)
                   || name.StartsWith("Mono.", StringComparison.Ordinal)
                   || name.StartsWith("nunit", StringComparison.Ordinal)
                   || name.StartsWith("UnityEngine", StringComparison.Ordinal)
                   || name.StartsWith("Unity.", StringComparison.Ordinal)
                   || name == "UnityEditor";
        }

        private static List<Type> SafeGetTypes(Assembly assembly)
        {
            try
            {
                return new List<Type>(assembly.GetTypes());
            }
            catch (ReflectionTypeLoadException e)
            {
                var result = new List<Type>();
                foreach (var type in e.Types)
                {
                    if (type != null)
                    {
                        result.Add(type);
                    }
                }
                return result;
            }
            catch (Exception)
            {
                return new List<Type>();
            }
        }

        #endregion

        #region 查找

        /// <summary>查找指令；未找到且尚未扫描过时会自动扫描一次</summary>
        public CommandDescriptor Find(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            CommandDescriptor descriptor;
            if (_map.TryGetValue(id, out descriptor))
            {
                return descriptor;
            }

            if (!_scanned)
            {
                ScanAssemblies();
                _map.TryGetValue(id, out descriptor);
            }

            return descriptor;
        }

        /// <summary>是否已注册该 ID（不触发扫描）</summary>
        public bool Contains(string id)
        {
            return !string.IsNullOrEmpty(id) && _map.ContainsKey(id);
        }

        /// <summary>创建指令实例，未注册时返回 null</summary>
        public ICommand Create(string id)
        {
            var descriptor = Find(id);
            return descriptor?.CreateInstance();
        }

        #endregion
    }
}
