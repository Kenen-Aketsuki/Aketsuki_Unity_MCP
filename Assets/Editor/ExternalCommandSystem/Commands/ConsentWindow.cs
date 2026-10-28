using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CivilRev.CommandSystem.Editor
{
    /// <summary>
    /// 敏感操作的人工确认窗。
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
}
