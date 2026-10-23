using System;
using UnityEditor;
using UnityEngine;

namespace CivilRev.CommandSystem.Editor
{
    /// <summary>
    /// 模态许可弹窗：仅「允许 / 拒绝」二选一，带倒计时；超时自动拒绝。
    /// <para>用于需要人工确认的切换性 / 破坏性操作（如打开指定场景）。</para>
    /// <para>阻塞调用线程（命令在 Unity 主线程执行，模态窗也跑在主线程，互不冲突）直到用户响应或超时。</para>
    /// <para>超时由两个重绘源共同驱动，保证在模态下一定生效：<see cref="OnGUI"/>（模态必重绘）与 <see cref="EditorApplication.update"/>（主循环 tick）。</para>
    /// </summary>
    internal sealed class PermissionWindow : EditorWindow
    {
        public enum Decision
        {
            None,
            Allow,
            Reject
        }

        private string _message;
        private DateTime _deadlineUtc;
        private Decision _decision = Decision.None;

        /// <summary>
        /// 弹出模态许可窗，阻塞直到用户点击允许/拒绝或超时（超时返回 <see cref="Decision.Reject"/>）。
        /// </summary>
        /// <param name="message">展示给用户的说明（应含将执行的动作与目标）。</param>
        /// <param name="timeoutSeconds">倒计时秒数，超时视为拒绝。</param>
        public static Decision Request(string message, int timeoutSeconds)
        {
            var window = CreateInstance<PermissionWindow>();
            window._message = message ?? string.Empty;
            window._deadlineUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds));
            window.titleContent = new GUIContent("操作许可");
            window.minSize = new Vector2(420, 220);
            window.maxSize = new Vector2(420, 220);
            window.position = new Rect(
                Screen.width / 2 - 210, Screen.height / 2 - 110, 420, 220);
            window.ShowModal();
            return window._decision;
        }

        private void OnEnable()
        {
            EditorApplication.update += OnUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnUpdate;
        }

        private void OnUpdate()
        {
            if (_decision == Decision.None && DateTime.UtcNow >= _deadlineUtc)
            {
                Resolve(Decision.Reject);
            }
        }

        private void Resolve(Decision decision)
        {
            if (_decision != Decision.None)
            {
                return;
            }

            _decision = decision;
            EditorApplication.update -= OnUpdate;
            Close();
        }

        private void OnGUI()
        {
            // 模态下必重绘：即使 update 不触发，这里也能驱动超时（双保险）
            if (_decision == Decision.None && DateTime.UtcNow >= _deadlineUtc)
            {
                Resolve(Decision.Reject);
                return;
            }

            GUILayout.Space(12);
            EditorGUILayout.HelpBox(_message, MessageType.Warning);
            GUILayout.Space(8);

            var remaining = (int)Math.Ceiling(Math.Max(0, (_deadlineUtc - DateTime.UtcNow).TotalSeconds));
            EditorGUILayout.LabelField(
                $"将在 {remaining} 秒后自动拒绝", EditorStyles.centeredGreyMiniLabel);
            GUILayout.Space(12);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("允许", GUILayout.Height(34)))
                {
                    Resolve(Decision.Allow);
                }

                if (GUILayout.Button("拒绝", GUILayout.Height(34)))
                {
                    Resolve(Decision.Reject);
                }
            }
        }
    }
}
