// -----------------------------------------------------------------------------
// 本目录（ExternalCommandSystem/Editor/**）是 Unity 的 Editor 特殊目录，
// Unity 会把它整体编译进 Assembly-CSharp-Editor，永远不会参与打包。
//
// 下面这段是"保险丝"：UNITY_EDITOR 只有在真正打玩家包时才会未定义。
// 因此一旦有人把这些源文件挪出 Editor 目录（或改了目录名），打玩家包时
// 会立刻以编译错误的形式暴露出来，而不是被静默打进游戏包。
//
// COMMANDSYSTEM_VERIFY 是给离线验证工程用的逃生开关（.verify 下的 csproj 会定义它，
// 那里没有 UnityEditor 可供引用，无法定义 UNITY_EDITOR）。正式工程里不应出现该符号。
// -----------------------------------------------------------------------------

#if !UNITY_EDITOR && !COMMANDSYSTEM_VERIFY
#error CommandSystem 是编辑器专用工具，禁止编译进玩家包。请确保所有源文件都位于名为 "Editor" 的目录下；若确实需要运行期可用，请先与维护者确认再移除本保护。
#endif

namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// 编辑器专用标记
    /// <para>
    /// 本类型不含任何逻辑，存在的唯一作用是携带上面的编译期保护（#error）。
    /// 整个 CommandSystem 依赖 UnityEditor（Undo / AssetDatabase / TypeCache / 场景操作），
    /// 只在编辑器环境下使用，不应出现在任何玩家构建中。
    /// </para>
    /// </summary>
    internal static class EditorOnlyGuard
    {
    }
}
