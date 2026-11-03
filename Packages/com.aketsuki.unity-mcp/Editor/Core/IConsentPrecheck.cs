namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// 敏感指令的「确认预检」：让指令自己在**弹窗之前**声明"这一次调用其实没有副作用，不用问人"。
    ///
    /// <para>
    /// <b>为什么需要这个接口（IMPR-011）：</b>人工确认门禁在
    /// <see cref="CommandRunner.ExecuteEntry"/> 里、<b>指令实例化之前</b>就触发了
    /// （<see cref="Aketsuki.UnityMcp.Editor.ConsentGate.RequestIfNeeded"/>），
    /// 这时 <c>Execute</c> 还没跑，指令自己没有任何机会说"我这次是空操作"。
    /// 典型受害者是 <c>scene.open</c>：目标场景本来就是当前打开的那个，
    /// 结果还是要弹一次窗、等人点「允许」、再白重载一遍场景。
    /// </para>
    ///
    /// <para>
    /// <b>语义红线：预检只允许"少问一次"，绝不允许"改判危险为安全"。</b>
    /// 实现必须保守——条件不完整就让门禁照常弹窗。返回 <c>true</c> 之后
    /// <c>Execute</c> 仍会被正常调用，所以真正的空操作判定要在两处一致：
    /// 预检说"无副作用"，<c>Execute</c> 就必须走同一条短路分支。
    /// </para>
    ///
    /// <para>
    /// <b>异常处理：</b>预检里抛异常等于让门禁失去判断依据，调用方会按"需要确认"处理
    /// （保守方向），不会因为你写了 try 就放行。所以实现里宁可返回 <c>false</c>，
    /// 也不要把"解析不到路径"这类情况咽掉——<c>Execute</c> 稍后会给出真正的报错。
    /// </para>
    /// </summary>
    public interface IConsentPrecheck
    {
        /// <summary>
        /// 询问这次调用是否可以跳过人工确认。
        /// </summary>
        /// <param name="context">与 <c>Execute</c> 拿到的同一份上下文（参数已解析完毕）</param>
        /// <param name="reason">
        /// 跳过确认的理由，写进日志与控制台。返回 <c>true</c> 时应给出人能看懂的一句话
        /// （例如"目标场景已是当前场景且无未保存改动"）。
        /// </param>
        /// <returns><c>true</c> 表示这次调用无副作用、可跳过确认；<c>false</c> 走正常门禁。</returns>
        bool SkipConsent(CommandContext context, out string reason);
    }
}
