using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace CivilRev.CommandSystem.Editor
{
    /// <summary>
    /// 通往「ADB 调试台」的反射桥。
    ///
    /// <para>
    /// <b>为什么是反射而不是引用：</b>ADB 调试台是一个**可以单独安装**的独立包
    /// （asmdef <c>CivilRev.AdbDebugger.Editor</c>）。指令系统跑在预定义程序集里，
    /// 直接引用它会导致"没装 ADB 的程序集就编译失败"，整个指令系统跟着不能用。
    /// 反射把它降级成运行时依赖：没装就是这几条指令报错，其余照常。
    /// 这与 <see cref="AddressablesBridge"/> 处理可选包的做法一致。
    /// </para>
    ///
    /// <para>
    /// <b>契约面只有一处：</b><c>AdbExternalApi.Invoke(string, IDictionary&lt;string,string&gt;)</c>
    /// 返回 JSON 文本。ADB 侧加字段不会影响这里；改了类名/签名才会——那种改动要同步改本文件。
    /// </para>
    /// </summary>
    internal static class AdbBridgeAdapter
    {
        private const string TypeName = "CivilRev.AdbDebugger.AdbExternalApi, CivilRev.AdbDebugger.Editor";

        private const string NotInstalled =
            "没找到 ADB 调试台的代码（程序集 CivilRev.AdbDebugger.Editor 不存在）。" +
            "这几条指令只是适配层，本体要另装「ADB 调试」那个包；装好后菜单里会出现 Tools / ADB 调试 / ADB 调试台。";

        private static bool _resolved;
        private static MethodInfo _invoke;
        private static string _resolveError;

        /// <summary>ADB 调试台是否可用（不抛异常，供 help 与调试用）</summary>
        internal static bool Available
        {
            get
            {
                EnsureResolved();
                return _invoke != null;
            }
        }

        /// <summary>执行一个子命令并把结果解析成 <see cref="JsonValue"/>。</summary>
        /// <param name="command">devices / logcat.start / logcat.stop / logcat.status / logcat.read</param>
        /// <param name="args">字符串参数，可为 null</param>
        internal static CommandResult Run(string command, IDictionary<string, string> args)
        {
            string json;
            string error;
            if (!TryInvoke(command, args, out json, out error))
            {
                return CommandResult.Fail(error);
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

        private static bool TryInvoke(
            string command, IDictionary<string, string> args, out string json, out string error)
        {
            json = null;
            error = null;

            EnsureResolved();
            if (_invoke == null)
            {
                error = _resolveError;
                return false;
            }

            try
            {
                json = (string)_invoke.Invoke(null, new object[]
                {
                    command,
                    args ?? new Dictionary<string, string>(StringComparer.Ordinal)
                });

                if (json == null)
                {
                    error = "ADB 调试台没有返回内容（" + command + "）";
                    return false;
                }

                return true;
            }
            catch (TargetInvocationException exception)
            {
                var inner = exception.InnerException ?? exception;
                error = "ADB 调试台执行 " + command + " 抛异常：" + inner.Message;
                return false;
            }
            catch (Exception exception)
            {
                error = "调用 ADB 调试台失败：" + exception.Message;
                return false;
            }
        }

        private static void EnsureResolved()
        {
            if (_resolved)
            {
                return;
            }

            _resolved = true;

            var type = Type.GetType(TypeName, throwOnError: false);
            if (type == null)
            {
                _resolveError = NotInstalled;
                return;
            }

            _invoke = type.GetMethod(
                "Invoke",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(IDictionary<string, string>) },
                null);

            if (_invoke == null)
            {
                _resolveError = "ADB 调试台版本过旧：找到了 " + type.FullName +
                                "，但没有 Invoke(string, IDictionary<string,string>)。请升级 ADB 调试包。";
            }
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
