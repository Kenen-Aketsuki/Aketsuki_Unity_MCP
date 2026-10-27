namespace CivilRev.CommandSystem
{
    /// <summary>
    /// 指令的「可用性判定」：可选依赖缺失时，让这条指令**根本不注册**，而不是注册了再报错。
    ///
    /// <para>
    /// <b>为什么要单独一个接口：</b>指令系统里有一部分指令只是**适配层**，本体在另一个可以单独安装的包里
    /// （目前是 <c>adb.*</c> 五条 —— 本体是「ADB 调试台」，独立 asmdef、独立 .unitypackage）。
    /// 用反射访问可选包能保证"没装也不影响编译"，但如果不做剔除，就会出现更糟的情况：
    /// AI 在 <c>unity_list_commands</c> 里看到 <c>unity_adb_devices</c>，以为能抓日志，
    /// 调了才发现没装 —— 列表里出现一个**必然失败**的工具，比它不出现更误导。
    /// </para>
    ///
    /// <para>
    /// <b>判定时机是注册阶段，不是执行阶段</b>：注册表在 <see cref="CommandRegistry.Register"/> 里问一次，
    /// 不可用就不进 <see cref="CommandRegistry.All"/>。于是所有派生出口自动一致 ——
    /// <c>/v1/commands</c>、编辑器控制台窗口、MCP 工具列表都不会有它，不需要在四个地方各写一遍过滤。
    /// </para>
    ///
    /// <para>
    /// <b>判定必须便宜且无副作用</b>：每次程序集扫描（域重载后各一次）都会调用，实现里只该做
    /// "类型在不在""文件存在不存在"这类检查，不要跑进程、不要读大文件。
    /// </para>
    /// </summary>
    public interface ICommandAvailability
    {
        /// <summary>
        /// 询问当前能否使用这条指令。
        /// </summary>
        /// <returns>
        /// 可用返回 <c>null</c> 或空串；不可用返回**人能照着做**的原因（例如"没找到 ADB 调试台，请先装那个包"）。
        /// 该原因会写进日志（扫描时汇总一条），并作为硬调这条指令时的报错正文。
        /// </returns>
        string GetUnavailableReason();
    }
}
