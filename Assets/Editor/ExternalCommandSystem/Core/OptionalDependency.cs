using System;
using System.Collections.Generic;
using System.Reflection;

namespace CivilRev.CommandSystem
{
    /// <summary>
    /// 一个「可选插件」的反射句柄：类型在就可用，不在就给出装什么的提示。
    ///
    /// <para>
    /// <b>要解决的问题：</b>指令系统的一部分指令是**适配层**，本体在可以单独安装、也可以完全不装的包里
    /// （目前是「ADB 调试台」；以后可能有录屏、Profiler 采样、真机截图之类的同类工具）。
    /// 指令系统跑在预定义程序集里，直接引用这些包会导致"没装就编译失败"，所以只能反射。
    /// 而反射的样板代码（typeName 拼接、GetMethod、异常包装、缓存）每加一个插件就要抄一遍，
    /// 抄第三遍必然各写各的。本类把这段固定下来。
    /// </para>
    ///
    /// <para>
    /// <b>与 <see cref="ICommandAvailability"/> 的分工：</b>本类只回答"这个外部程序集的方法能不能调到"，
    /// 是一个**查询工具**；<see cref="ICommandAvailability"/> 是**注册表的协议**，决定"这条指令要不要注册"。
    /// 两者可以组合（见 <see cref="OptionalDependencyCommand"/>），也可以各用各的 ——
    /// 例如某个指令的可用性取决于"工程里有没有某个场景"，那就不需要本类。
    /// </para>
    ///
    /// <para>
    /// <b>解析只做一次</b>：首次访问 <see cref="Available"/> / <see cref="UnavailableReason"/> /
    /// <see cref="TryInvoke"/> 时解析并缓存。装包之后 Unity 会重新编译并域重载，静态状态自然重建，
    /// 不需要手动清理缓存。
    /// </para>
    /// </summary>
    public sealed class OptionalDependency
    {
        private readonly string _typeName;
        private readonly string _assemblyName;
        private readonly string _memberName;
        private readonly Type[] _signature;
        private readonly string _installHint;

        private bool _resolved;
        private MethodInfo _method;
        private string _error;

        private OptionalDependency(
            string typeName, string assemblyName, string memberName, Type[] signature, string installHint)
        {
            _typeName = typeName;
            _assemblyName = assemblyName;
            _memberName = memberName;
            _signature = signature ?? Type.EmptyTypes;
            _installHint = installHint ?? string.Empty;
        }

        /// <summary>
        /// 声明一个可选插件：要调它的**静态方法**。
        /// </summary>
        /// <param name="typeName">类型全名（不含程序集），例如 <c>CivilRev.AdbDebugger.AdbExternalApi</c></param>
        /// <param name="assemblyName">程序集名（不含 .dll），例如 <c>CivilRev.AdbDebugger.Editor</c></param>
        /// <param name="memberName">静态方法名</param>
        /// <param name="signature">方法参数类型；传 null 表示无参</param>
        /// <param name="installHint">
        /// 不可用时给用户看的话：**装什么、装完在哪**。这句话会原样出现在
        /// 注册期汇总日志与硬调这条指令的报错里，所以要能照着做，不要写"依赖缺失"。
        /// </param>
        public static OptionalDependency Static(
            string typeName,
            string assemblyName,
            string memberName,
            Type[] signature,
            string installHint)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                throw new ArgumentException("typeName 不能为空", nameof(typeName));
            }

            if (string.IsNullOrEmpty(assemblyName))
            {
                throw new ArgumentException("assemblyName 不能为空", nameof(assemblyName));
            }

            if (string.IsNullOrEmpty(memberName))
            {
                throw new ArgumentException("memberName 不能为空", nameof(memberName));
            }

            return new OptionalDependency(typeName, assemblyName, memberName, signature, installHint);
        }

        /// <summary>插件是否可用（类型解析到了，并且目标方法签名对得上）</summary>
        public bool Available
        {
            get
            {
                EnsureResolved();
                return _method != null;
            }
        }

        /// <summary>
        /// 不可用的原因；可用时为 <c>null</c>。
        /// <para>可以直接喂给 <see cref="ICommandAvailability.GetUnavailableReason"/>。</para>
        /// </summary>
        public string UnavailableReason
        {
            get
            {
                EnsureResolved();
                return _method != null ? null : _error;
            }
        }

        /// <summary>
        /// 调用目标方法；失败抛 <see cref="CommandException"/>（消息已含插件名与原因）。
        /// </summary>
        public object Invoke(params object[] args)
        {
            string error;
            object result;
            if (!TryInvoke(args, out result, out error))
            {
                throw new CommandException(error);
            }

            return result;
        }

        /// <summary>
        /// 调用目标方法，失败不抛异常。
        /// </summary>
        /// <param name="args">实参，按 <c>signature</c> 的顺序；可为 null</param>
        /// <param name="result">返回值；失败时为 null</param>
        /// <param name="error">失败原因；成功时为 null</param>
        /// <returns>是否调用成功</returns>
        public bool TryInvoke(object[] args, out object result, out string error)
        {
            result = null;
            error = null;

            EnsureResolved();
            if (_method == null)
            {
                error = _error;
                return false;
            }

            try
            {
                result = _method.Invoke(null, args ?? Array.Empty<object>());
                return true;
            }
            catch (TargetInvocationException exception)
            {
                // 反射会把内层异常包一层，直接显示外层只会得到"调用的目标发生了异常"，没有信息量
                var inner = exception.InnerException ?? exception;
                error = _typeName + " 执行 " + _memberName + " 抛异常：" + inner.Message;
                return false;
            }
            catch (Exception exception)
            {
                error = "调用 " + _typeName + " 失败：" + exception.Message;
                return false;
            }
        }

        private void EnsureResolved()
        {
            if (_resolved)
            {
                return;
            }

            _resolved = true;

            var qualified = _typeName + ", " + _assemblyName;
            var type = Type.GetType(qualified, throwOnError: false);
            if (type == null)
            {
                _error = "没找到 " + _assemblyName + " 的程序集（或其中没有 " + _typeName + "）。" + _installHint;
                return;
            }

            _method = type.GetMethod(
                _memberName,
                BindingFlags.Public | BindingFlags.Static,
                null,
                _signature,
                null);

            if (_method == null)
            {
                _error = "找到了 " + _typeName + "，但没有签名为 " + _memberName + "(" + DescribeSignature() +
                         ") 的公开静态方法 —— 多半是插件版本过旧，请升级。" + _installHint;
            }
        }

        private string DescribeSignature()
        {
            if (_signature.Length == 0)
            {
                return string.Empty;
            }

            var parts = new string[_signature.Length];
            for (var i = 0; i < _signature.Length; i++)
            {
                parts[i] = _signature[i] == null ? "?" : _signature[i].Name;
            }

            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// 给「可选插件」的适配指令用的基类：把插件的可用性直接接到注册表上。
    ///
    /// <para>
    /// 用法（新增一个同类插件时，这就是全部样板）：
    /// <code>
    /// public abstract class FooCommandBase : OptionalDependencyCommand
    /// {
    ///     protected override OptionalDependency Dependency => FooBridge.Plugin;
    /// }
    ///
    /// [Command("foo.run", EditorOnly = true)]
    /// public sealed class FooRunCommand : FooCommandBase
    /// {
    ///     public override CommandResult Execute(CommandContext context) { ... }
    /// }
    /// </code>
    /// </para>
    ///
    /// <para>
    /// <see cref="Dependency"/> 返回 <c>null</c>（或它 <see cref="OptionalDependency.Available"/> 为 false）时，
    /// 指令**不会被注册** —— 不出现在 <c>/v1/commands</c>、编辑器窗口与 MCP 工具列表里。
    /// </para>
    /// </summary>
    public abstract class OptionalDependencyCommand : ICommand, ICommandAvailability
    {
        /// <summary>这条指令依赖的插件；返回 null 视为不可用</summary>
        protected abstract OptionalDependency Dependency { get; }

        string ICommandAvailability.GetUnavailableReason()
        {
            var dependency = Dependency;
            if (dependency == null)
            {
                return "没有声明可用的可选插件（Dependency 为 null）";
            }

            return dependency.UnavailableReason;
        }

        /// <summary>执行指令</summary>
        public abstract CommandResult Execute(CommandContext context);
    }
}
