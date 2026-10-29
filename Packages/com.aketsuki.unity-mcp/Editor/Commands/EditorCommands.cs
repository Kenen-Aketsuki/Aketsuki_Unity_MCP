using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 刷新资源数据库
    /// <para>参数：无</para>
    /// </summary>
    [Command("asset.refresh", Description = "刷新 AssetDatabase（导入新增/修改的资源）；Play 模式下阻断并提示", EditorOnly = true)]
    public sealed class AssetRefreshCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            EditorPlayState.GuardNotPlaying();
            AssetDatabase.Refresh();
            return CommandResult.Ok("已刷新资源数据库");
        }
    }

    /// <summary>
    /// 保存所有资源修改
    /// </summary>
    [Command("asset.save", Description = "调用 AssetDatabase.SaveAssets", EditorOnly = true)]
    public sealed class AssetSaveCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            AssetDatabase.SaveAssets();
            return CommandResult.Ok("已保存资源");
        }
    }

    /// <summary>
    /// 保存当前打开的场景
    /// <para>
    /// 协作式取消：进入前检查一次取消标记，已取消则直接返回失败、不写盘。
    /// 注意 <c>EditorSceneManager.SaveOpenScenes</c> 是同步阻塞调用，一旦开始就无法中断——
    /// 这是 Unity 主线程模型的硬约束，不是本指令能解决的。
    /// </para>
    /// </summary>
    [Command("scene.save", Description = "保存当前打开的场景", EditorOnly = true)]
    public sealed class SceneSaveCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            if (context.IsCancelled)
            {
                return CommandResult.Fail("已取消");
            }

            var saved = EditorSceneManager.SaveOpenScenes();
            return saved
                ? CommandResult.Ok("场景已保存")
                : CommandResult.Fail("场景保存失败（可能存在未命名场景）");
        }
    }

    /// <summary>
    /// 请求 Unity 重新编译脚本程序集
    /// <para>
    /// 与 <c>asset.refresh</c>（AssetDatabase.Refresh，会重扫描并重导入资源）不同，
    /// 本指令聚焦于"让脚本程序集变成最新"。但**默认会先做一次 AssetDatabase.Refresh**（refresh:true），
    /// 原因：外部工具直接写磁盘的 .cs，ADB 无从感知；若只调 RequestScriptCompilation，
    /// dll 会更新而 ADB 记录仍是旧的，用户随后按 Ctrl+R 仍会被判定为脏并再编一次。
    /// </para>
    /// <para>参数：clean（bool，默认 false）——true 时清空编译缓存做干净重建；refresh（bool，默认 true）——false 时退回"只编译不扫资源"的旧行为。</para>
    /// <para>
    /// 闭环验证（2026-10-19）：改完 .cs 后执行本指令，再单独执行一次 asset.refresh，
    /// 应耗时 &lt;20ms 且不触发域重载，即表示 AssetDatabase 已认同编译结果，用户手动 Ctrl+R 不会再补编。
    /// </para>
    /// <para>
    /// 注意：本调用是异步的，Unity 会在后续帧开始真正编译，因此指令在编译开始前就已返回；
    /// 之后编辑器进入"编译中"状态，HTTP 层会在此期间对新的执行请求返回 503，
    /// 客户端可用 <c>/v1/status</c> 的 compiling 字段轮询进度。
    /// </para>
    /// </summary>
        [Command("asset.compile", Description = "请求 Unity 重新编译脚本程序集；Play 模式下阻断并提示",
        Usage = "clean: bool, refresh: bool", EditorOnly = true)]
    public sealed class AssetCompileCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            // DOC-001：被拒时 HTTP 仍是 200，只看状态码会误判成"编译过了"。
            // 先把这次请求记进 lastCompile（requestedUtc + rejectedReason），再按原样抛错。
            if (EditorApplication.isPlaying)
            {
                CommandCompileMonitor.RecordRejected("编辑器正处于预览状态，不允许编译与刷新");
            }

            EditorPlayState.GuardNotPlaying();

            if (context.DryRun)
            {
                return CommandResult.Skip("dry-run：将请求脚本重新编译");
            }

            var clean = context.Args.GetBool("clean", false);
            // 外部工具是直接往磁盘写 .cs 的，AssetDatabase 并不知道文件变了。
            // 只调 RequestScriptCompilation 会让 Bee 按磁盘内容重编出新的 dll，
            // 但 ADB 的导入记录仍是旧的 —— 用户回到编辑器按 Ctrl+R 时，
            // ADB 扫到 mtime/hash 与记录不符，会再导入并再编一次。
            // 因此默认先用 Refresh 把 ADB 同步到磁盘现状，再请求编译。
            var refresh = context.Args.GetBool("refresh", true);
            var wasCompiling = EditorApplication.isCompiling;
            var options = clean
                ? RequestScriptCompilationOptions.CleanBuildCache
                : RequestScriptCompilationOptions.None;

            if (refresh)
            {
                AssetDatabase.Refresh();
            }

            // 真正请求重编译；该调用是 void、异步的，Unity 会在后续帧开始编译，因此指令在编译开始前就已返回。
            UnityEngine.Debug.Log("[ExternalCommandSystem] 接收到 asset.compile 请求，正在触发脚本重新编译" +
                                  (clean ? "（CleanBuildCache）" : ""));
            if (clean || (!wasCompiling && !EditorApplication.isCompiling))
            {
                // Refresh 若已检测到脚本变更并开始编译，就不再重复请求（clean 模式除外，语义要求重建缓存）。
                CompilationPipeline.RequestScriptCompilation(options);
            }

            var message = wasCompiling
                ? "已请求脚本重新编译（编译已在后台进行中）"
                : "已请求脚本重新编译；可用 /v1/status 的 compiling 字段轮询进度";
            return CommandResult.Ok(message);
        }
    }
}
