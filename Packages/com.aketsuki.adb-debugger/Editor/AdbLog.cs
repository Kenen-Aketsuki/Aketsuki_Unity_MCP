using UnityEngine;

namespace Aketsuki.AdbDebugger
{
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
