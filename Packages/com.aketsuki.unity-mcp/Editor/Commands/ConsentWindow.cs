using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 敏感操作的人工确认窗（<b>现在只在批处理模式下使用</b>，见 <see cref="ConsentModalPrompt"/>）。
    ///
    /// <para>
    /// <b>必须是 <c>ShowUtility()</c>，不能是 <c>ShowModal()</c>：</b>模态窗口会进入嵌套循环，
    /// 期间 <c>EditorApplication.update</c> 不触发、窗口也不主动重绘，倒计时直接失效
    /// （实测：30 秒超时永远不走，指令和主线程一起僵住）。非模态窗口下 update 正常跑，
    /// 倒计时和自动拒绝才有意义。
    /// </para>
    ///
    /// <para>
    /// 一条指令一个卡片：允许 / 拒绝 + 剩余秒数。点「允许」不在 OnGUI 里直接执行——
    /// <c>editor.play</c> 会触发域重载，在 GUI 回调里做会打断绘制，改用 <c>delayCall</c> 推到下一帧。
    /// </para>
    ///
    /// <para>
    /// <b>2026-11-03（IMPR-010）：</b>非模态浮窗的"被遮挡 / 不抢焦点 / 没声音"是实测踩过的坑
    /// （人根本没看见窗口，30 秒后静默按拒绝），所以交互场景改为
    /// <see cref="ConsentModalPrompt"/> 的真模态框；本窗口退居为 <c>-batchmode</c> 下的兜底
    /// （批处理弹不出可交互对话框）。
    /// </para>
    /// </summary>
    internal sealed class ConsentWindow : EditorWindow
    {
        private const string WindowTitle = "等待确认（敏感操作）";

        /// <summary>打开（或聚焦）确认窗；没有待确认项时窗口会自行关闭</summary>
        internal static void ShowWindow()
        {
            var window = GetWindow<ConsentWindow>(false, WindowTitle, true);
            window.titleContent = new GUIContent(WindowTitle);
            window.minSize = new Vector2(460f, 220f);
            window.ShowUtility();
            window.Repaint();
        }

        private void OnEnable()
        {
            EditorApplication.update += Tick;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Tick;
        }

        /// <summary>每帧刷新倒计时；没有待确认项就关窗，不留空壳</summary>
        private void Tick()
        {
            if (ConsentGate.PendingCount == 0)
            {
                Close();
                return;
            }

            Repaint();
        }

        private void OnGUI()
        {
            var pending = ConsentGate.GetPending();
            if (pending.Count == 0)
            {
                EditorGUILayout.LabelField("没有待确认的操作。", EditorStyles.miniLabel);
                return;
            }

            EditorGUILayout.HelpBox(
                "AI 请求执行敏感操作，需要你确认。倒计时结束前不响应，一律按拒绝处理。",
                MessageType.Warning);

            foreach (var record in pending)
            {
                DrawRecord(record);
            }

            GUILayout.Space(4);
            EditorGUILayout.LabelField(
                $"不想每次都被问：控制台「{ConsentGate.ConsoleMenuPath}」→ 勾选「{ConsentGate.ToggleLabel}」",
                EditorStyles.miniLabel);
        }

        private void DrawRecord(ConsentRecord record)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField($"{record.CommandId}    [{record.Id}]", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(record.Message ?? string.Empty, EditorStyles.wordWrappedLabel);
                GUILayout.Space(2);
                EditorGUILayout.LabelField(
                    $"剩余 {record.RemainingSeconds} 秒后自动拒绝", EditorStyles.centeredGreyMiniLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("允许", GUILayout.Height(28)))
                    {
                        ApproveLater(record.Id);
                    }

                    if (GUILayout.Button("拒绝", GUILayout.Height(28)))
                    {
                        ConsentGate.Reject(record.Id);
                    }
                }
            }

            GUILayout.Space(4);
        }

        /// <summary>
        /// 推迟到下一帧执行：<c>editor.play</c> 会触发域重载，
        /// 直接在 OnGUI 回调里执行等于在绘制过程中拆掉窗口。
        /// </summary>
        private static void ApproveLater(string id)
        {
            var target = id;
            EditorApplication.delayCall += () =>
            {
                var record = ConsentGate.Approve(target);
                if (record != null && !string.IsNullOrEmpty(record.ResultMessage))
                {
                    Debug.Log($"[指令系统] 已确认执行 {target}：{record.ResultMessage}");
                }
            };
        }
    }

    /// <summary>
    /// 敏感操作的<b>真模态</b>确认（IMPR-010，用户 2026-11-03 批注「真模态」）。
    ///
    /// <para>
    /// <b>为什么必须换成真模态：</b>原来的非模态浮窗（<see cref="ConsentWindow"/>）反馈太弱 ——
    /// 会被其它 Unity 窗口压住、不抢焦点、没声音、Console 那条 warning 也可能被折叠，
    /// 结果是人根本没看见窗口，30 秒后静默按拒绝（2026-11-02 在 island_back 连踩两次：
    /// AI 等到 <c>expired</c>，用户说"可能是弹窗弹了，但我没看到"）。
    /// </para>
    ///
    /// <para>
    /// <b>用 <c>EditorUtility.DisplayDialog</c>：</b>编辑器级模态框，必然抢焦点、必须点一下才走，
    /// 不可能被忽略。代价是<b>阻塞主线程</b>直到人点 —— 这是<b>接受的</b>：
    /// 此时 HTTP 早已返回 <c>SKIP + pending</c>，客户端不依赖编辑器同步响应；
    /// <c>editor.play</c> 这类指令本来就要等到人点头才会有动作。
    /// </para>
    ///
    /// <para>
    /// <b>三件必须配套的事（别删）：</b>
    /// <list type="number">
    /// <item><b>停表</b>：模态循环里 <c>EditorApplication.update</c> 不推进，<c>ExpireOverdue</c> 也就不会跑；
    /// 窗口关掉后把这段时长补回 deadline（<see cref="ConsentGate.PauseCountdown"/>），
    /// 否则人刚看完就被判超时，排队中的记录也会被人家的思考时间吃掉。</item>
    /// <item><b>状态外露</b>：<see cref="ConsentGate.SetModalOpen"/> 让 <c>/v1/status</c> 能说清
    /// "泵停在这里"（否则会被误判成编辑器死了）。</item>
    /// <item><b>逐条排队</b>：一次只弹一条（队列头），点完下一条下一帧再弹。
    /// 一批里出现多条敏感指令的情况极少（门禁在第一条处就中止整批），不值得为它做列表视图。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>不在 <c>update</c> 回调里直接开模态</b>：先跳到 <c>delayCall</c>，
    /// 避免在 Unity 遍历更新列表的过程中进入嵌套模态循环。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    internal static class ConsentModalPrompt
    {
        /// <summary>模态框正开着（挡住 update 重入，正常情况模态循环里 update 本身就不会进来）</summary>
        private static bool _showing;

        /// <summary>已经排进 delayCall、还没真正弹（避免一帧里排多次）</summary>
        private static bool _scheduled;

        static ConsentModalPrompt()
        {
            EditorApplication.update += Pump;
        }

        private static void Pump()
        {
            if (_showing || _scheduled)
            {
                return;
            }

            // 批处理没有可交互界面；全权委托开着就根本不需要问
            if (UnityEngine.Application.isBatchMode || ConsentGate.EnabledLive)
            {
                return;
            }

            var pending = ConsentGate.GetPending();
            if (pending.Count == 0)
            {
                return;
            }

            var head = pending[0];
            _scheduled = true;
            EditorApplication.delayCall += () =>
            {
                _scheduled = false;
                Show(head);
            };
        }

        private static void Show(ConsentRecord record)
        {
            // 排队期间状态可能已经变了（超时 / 被别处拒绝）
            if (record == null || record.State != ConsentState.Pending)
            {
                return;
            }

            _showing = true;
            ConsentGate.SetModalOpen(true);
            var startedUtc = DateTime.UtcNow;

            var approved = false;
            try
            {
                // 声音提示：静音的人听不到也无妨，重点是模态框本身一定抢焦点
                EditorApplication.Beep();

                var message =
                    record.CommandId + "\n\n" +
                    (record.Message ?? string.Empty) + "\n\n" +
                    "确认 id：" + record.Id + "\n\n" +
                    "「允许」= 立即执行；「拒绝」= 本条不执行，本批次后续指令一并跳过。\n" +
                    "窗口打开期间倒计时暂停。";

                approved = EditorUtility.DisplayDialog(
                    "指令系统 · 敏感操作需要你确认", message, "允许", "拒绝");
            }
            catch (Exception error)
            {
                CommandLog.Warning("弹出确认框失败，已按拒绝处理：" + error.Message);
            }
            finally
            {
                ConsentGate.PauseCountdown(DateTime.UtcNow - startedUtc);
                ConsentGate.SetModalOpen(false);
            }

            // 结论阶段仍保持 _showing：Approve 里可能执行 editor.play 触发域重载，
            // 期间若放开标志，下一帧 update 会把同一条再弹一次
            try
            {
                if (record.State != ConsentState.Pending)
                {
                    return;
                }

                if (approved)
                {
                    var executed = ConsentGate.Approve(record.Id);
                    if (executed != null && !string.IsNullOrEmpty(executed.ResultMessage))
                    {
                        CommandLog.Info($"已确认执行 {record.Id}：{executed.ResultMessage}");
                    }
                }
                else
                {
                    ConsentGate.Reject(record.Id);
                }
            }
            finally
            {
                _showing = false;
            }
        }
    }
}
