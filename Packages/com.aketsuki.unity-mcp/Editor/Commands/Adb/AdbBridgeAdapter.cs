using System;
using System.Collections.Generic;
using System.Text;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 通往「ADB 调试台」的反射桥。
    ///
    /// <para>
    /// <b>为什么是反射而不是引用：</b>ADB 调试台是一个**可以单独安装**的独立包
    /// （asmdef <c>CivilRev.AdbDebugger.Editor</c>）。指令系统跑在预定义程序集里，
    /// 直接引用它会导致"没装 ADB 的程序集就编译失败"，整个指令系统跟着不能用。
    /// 反射把它降级成运行时依赖：没装就不注册这几条指令，其余照常。
    /// </para>
    ///
    /// <para>
    /// <b>反射样板在 <see cref="OptionalDependency"/> 里</b>，本文件只剩"ADB 的契约定在哪"。
    /// 以后加同类插件（录屏、截图、采样器……）照抄这个文件的结构即可：
    /// 一个 <see cref="OptionalDependency.Static"/> 句柄 + 一层薄薄的子命令分发。
    /// </para>
    ///
    /// <para>
    /// <b>契约面只有一处：</b><c>AdbExternalApi.Invoke(string, IDictionary&lt;string,string&gt;)</c>
    /// 返回 JSON 文本。ADB 侧加字段不会影响这里；改了类名/签名才会——那种改动要同步改下面的常量。
    /// </para>
    /// </summary>
    internal static class AdbBridgeAdapter
    {
        /// <summary>插件程序集名（= asmdef 名，不含 .dll）</summary>
        internal const string AssemblyName = "CivilRev.AdbDebugger.Editor";

        /// <summary>对外契约的类型全名（不含程序集）</summary>
        internal const string TypeName = "CivilRev.AdbDebugger.AdbExternalApi";

        /// <summary>对外契约的方法名</summary>
        internal const string MemberName = "Invoke";

        /// <summary>没装时给用户看的话：装什么、装完在哪</summary>
        private const string InstallHint =
            "这几条指令只是适配层，本体要另装「ADB 调试」那个包；装好后菜单里会出现 Tools / ADB 调试 / ADB 调试台。";

        /// <summary>
        /// 插件句柄。解析一次就缓存；装包后 Unity 会重新编译并域重载，静态状态自然重建。
        /// </summary>
        internal static readonly OptionalDependency Plugin = OptionalDependency.Static(
            TypeName,
            AssemblyName,
            MemberName,
            new[] { typeof(string), typeof(IDictionary<string, string>) },
            InstallHint);

        /// <summary>ADB 调试台是否可用（不抛异常，供诊断用）</summary>
        internal static bool Available => Plugin.Available;

        /// <summary>不可用的原因；可用时为 null。注册阶段由 <c>AdbCommandBase</c> 取用。</summary>
        internal static string UnavailableReason => Plugin.UnavailableReason;

        /// <summary>执行一个子命令并把结果解析成 <see cref="JsonValue"/>。</summary>
        /// <param name="command">devices / logcat.start / logcat.stop / logcat.status / logcat.read</param>
        /// <param name="args">字符串参数，可为 null</param>
        internal static CommandResult Run(string command, IDictionary<string, string> args)
        {
            object raw;
            string error;
            var payload = args ?? new Dictionary<string, string>(StringComparer.Ordinal);

            if (!Plugin.TryInvoke(new object[] { command, payload }, out raw, out error))
            {
                return CommandResult.Fail(error);
            }

            var json = raw as string;
            if (json == null)
            {
                return CommandResult.Fail("ADB 调试台没有返回内容（" + command + "）");
            }

            JsonValue value;
            try
            {
                value = JsonValue.Parse(json);
            }
            catch (Exception exception)
            {
                return CommandResult.Fail("ADB 调试台返回的不是合法 JSON：" + exception.Message, json);
            }

            if (!value["ok"].AsBool(true))
            {
                return CommandResult.Fail(value["error"].AsString("执行失败（未给出原因）"), value);
            }

            return CommandResult.Ok(string.Empty, value);
        }

        /// <summary>把设备数组拼成一行人类可读摘要（Message 用；完整字段在 Data 里）</summary>
        internal static string DescribeDevices(JsonValue root)
        {
            var devices = root["devices"];
            if (!devices.IsArray || devices.ArrayValue.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            var list = devices.ArrayValue;
            for (var i = 0; i < list.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append("、");
                }

                var device = list[i];
                var serial = device["serial"].AsString(string.Empty);
                var model = device["model"].AsString(string.Empty);
                builder.Append(model.Length == 0 ? serial : model + "（" + serial + "）");

                if (!device["usable"].AsBool(false))
                {
                    builder.Append("[").Append(device["state"].AsString("未知")).Append("]");
                }
            }

            return builder.ToString();
        }
    }
}
