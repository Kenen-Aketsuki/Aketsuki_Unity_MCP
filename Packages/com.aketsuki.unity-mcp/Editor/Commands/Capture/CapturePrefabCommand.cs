using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 截取 Prefab 的画面。
    /// <para>
    /// Prefab 编辑模式本质上就是一个特殊的 SceneView，所以<b>已在 Prefab 模式时直接复用
    /// <c>capture.scene</c> 的窗口截屏逻辑</b>，同样能拿到 Gizmos 与网格。
    /// </para>
    /// <para>
    /// 未打开时有两个选择，本指令只做前者：
    /// </para>
    /// <list type="bullet">
    /// <item><c>openPrefab: true</c> → <c>PrefabStageUtility.OpenPrefab</c> 进入编辑模式再截，<b>有副作用</b>
    /// （编辑器当前编辑对象被切走）。不标 RequiresConsent：默认值是 false，只有显式传 true 才会发生，
    /// 而且返回里一定带 warning 说明。</item>
    /// <item>离屏预览（<c>NewPreviewScene</c> + 自动 framing）：零副作用，但没有 Gizmos、光照要自己搭，
    /// 适合"给资产拍证件照"，不适合"看它现在长什么样"。本指令不做。</item>
    /// </list>
    /// <para>
    /// 参数：prefab（Prefab 资产路径或名字；不给则要求当前已在 Prefab 编辑模式）、
    /// openPrefab（默认 false，true 时自动进入编辑模式）、
    /// path / crop / view / select / scale 同 <c>capture.scene</c>。
    /// </para>
    /// </summary>
    [Command("capture.prefab", Description = "截取 Prefab 编辑模式的画面（含 Gizmos）；未打开时可 openPrefab 进入后再截",
        Usage = "path: string, crop: frame|window|full, view: int, select: any, scale: number, prefab: string, openPrefab: bool",
        EditorOnly = true)]
    public sealed class CapturePrefabCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            if (!WindowCapture.IsSupported)
            {
                return CommandResult.Fail($"窗口截屏只支持 Windows 编辑器（当前平台：{Application.platform}）");
            }

            var crop = CaptureSupport.ParseCrop(context);
            var requested = context.Args.GetString("prefab", null);
            var openPrefab = context.Args.GetBool("openPrefab", false);

            if (context.DryRun)
            {
                return CommandResult.Skip(
                    $"dry-run：将截取 Prefab（crop={crop}，openPrefab={openPrefab}）到 {CaptureSupport.DefaultOutputPath("prefab")}");
            }

            var warnings = new List<string>();
            var stage = PrefabStageUtility.GetCurrentPrefabStage();

            string assetPath = null;
            if (!string.IsNullOrEmpty(requested))
            {
                string error;
                if (!TryResolvePrefabPath(requested, out assetPath, out error))
                {
                    return CommandResult.Fail(error);
                }
            }

            if (stage == null)
            {
                if (assetPath == null)
                {
                    return CommandResult.Fail(
                        "当前不在 Prefab 编辑模式。请传 prefab（资产路径或名字）+ openPrefab:true 让本指令进入编辑模式，" +
                        "或先在 Project 窗口里双击打开目标 Prefab");
                }

                if (!openPrefab)
                {
                    return CommandResult.Fail(
                        $"当前不在 Prefab 编辑模式，且 openPrefab=false。要截 {assetPath}，请传 openPrefab:true" +
                        "（会切换编辑器当前编辑对象）；或先手动打开该 Prefab");
                }

                var openError = TryOpenPrefab(assetPath, warnings);
                if (openError != null)
                {
                    return CommandResult.Fail(openError);
                }

                stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage == null)
                {
                    return CommandResult.Fail($"进入 Prefab 编辑模式失败：{assetPath}");
                }
            }
            else if (assetPath != null && !SameAssetPath(GetStageAssetPath(stage), assetPath))
            {
                if (!openPrefab)
                {
                    return CommandResult.Fail(
                        $"当前正在编辑另一个 Prefab（{GetStageAssetPath(stage)}），要切到 {assetPath} 请传 openPrefab:true");
                }

                var openError = TryOpenPrefab(assetPath, warnings);
                if (openError != null)
                {
                    return CommandResult.Fail(openError);
                }

                stage = PrefabStageUtility.GetCurrentPrefabStage();
            }

            if (stage == null)
            {
                return CommandResult.Fail("当前不在 Prefab 编辑模式，无法截取 Prefab 画面");
            }

            SceneView view;
            string viewError;
            if (!CaptureSupport.TryGetSceneView(context, out view, out viewError))
            {
                return CommandResult.Fail(viewError);
            }

            string selectError;
            using (var scope = CaptureSupport.SelectionScope.Apply(context, out selectError))
            {
                if (scope == null)
                {
                    return CommandResult.Fail(selectError);
                }

                if (context.Args.Has("select"))
                {
                    warnings.Add("select 已临时选中目标并在截完后还原，但选中高亮属于渲染结果：本次截图通常仍是改选中之前的帧，" +
                                 "别指望用 select 拍出 selected gizmo 的高亮圈");
                }

                var result = CaptureSupport.CaptureWindow(context, view, "prefab", crop, warnings, null);
                if (result.Success && result.Data is JsonValue data)
                {
                    data.Set("prefabPath", JsonValue.Of(GetStageAssetPath(stage) ?? string.Empty));
                }

                return result;
            }
        }

        /// <summary>
        /// Prefab 资产的路径。
        /// <para>
        /// 2022.2 起 <c>PrefabStage.prefabAssetPath</c> 被标为 obsolete（改名 <c>assetPath</c>），
        /// 而 2021.3 只有旧名。本包要同时装在 2021.3 与 2022.3 的工程里，只能按版本宏取值，
        /// 否则每次部署都在目标工程里刷三条 CS0618。
        /// </para>
        /// </summary>
        private static string GetStageAssetPath(PrefabStage stage)
        {
            if (stage == null)
            {
                return null;
            }

#if UNITY_2022_2_OR_NEWER
            return stage.assetPath;
#else
            return stage.prefabAssetPath;
#endif
        }

        /// <summary>
        /// 进入 Prefab 编辑模式。
        /// <para>
        /// <b>先检查未保存改动</b>：<c>OpenPrefab</c> 在场景 dirty 时可能弹出保存对话框，
        /// 那是一个真模态循环——指令会连同主线程一起卡住（FEAT-014 已经踩过一次）。
        /// 与其冒险，不如直接拒绝并让人先保存。
        /// </para>
        /// </summary>
        private static string TryOpenPrefab(string assetPath, List<string> warnings)
        {
            if (AnySceneDirty())
            {
                return "当前场景有未保存改动，进入 Prefab 编辑模式可能弹出保存对话框并阻塞指令。" +
                       "请先保存场景（或手动打开该 Prefab）后重试";
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                return $"不是有效的 Prefab 资产：{assetPath}";
            }

            var stage = PrefabStageUtility.OpenPrefab(assetPath);
            if (stage == null)
            {
                return $"打开 Prefab 失败：{assetPath}";
            }

            warnings.Add(
                $"已通过 openPrefab 进入 Prefab 编辑模式：{assetPath}；编辑器当前编辑对象已切走，返回原场景请手动关闭 Prefab 模式");
            return null;
        }

        private static bool AnySceneDirty()
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isDirty)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SameAssetPath(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                return false;
            }

            return string.Equals(
                left.Replace('\\', '/'), right.Replace('\\', '/'), System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 把 <c>prefab</c> 参数解析成 <c>Assets/...</c> 资产路径。
        /// <para>
        /// 支持完整路径与"只给名字"（Project 窗口里看到的那个）。重名时**报候选**，不静默取第一个——
        /// 拿错 Prefab 截出来的图会让人往完全错误的方向排查。
        /// </para>
        /// </summary>
        private static bool TryResolvePrefabPath(string raw, out string assetPath, out string error)
        {
            assetPath = null;
            error = null;

            var normalized = raw.Trim().Trim('"').Replace('\\', '/');
            if (normalized.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase))
            {
                var full = Path.Combine(CommandPathUtil.ProjectRoot, normalized);
                if (!File.Exists(full))
                {
                    error = $"Prefab 资产不存在：{normalized}";
                    return false;
                }

                assetPath = normalized;
                return true;
            }

            var name = Path.GetFileNameWithoutExtension(normalized);
            var guids = AssetDatabase.FindAssets(name + " t:Prefab");
            var matches = new List<string>();
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                if (string.Equals(Path.GetFileNameWithoutExtension(path), name,
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(path);
                }
            }

            if (matches.Count == 0)
            {
                error = $"找不到名为 '{name}' 的 Prefab（可以改传完整 Assets 路径）";
                return false;
            }

            if (matches.Count > 1)
            {
                error = $"'{name}' 命中 {matches.Count} 个 Prefab，请传完整路径：{string.Join("；", matches.ToArray())}";
                return false;
            }

            assetPath = matches[0];
            return true;
        }
    }
}
