using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 一条请求（一个批次）内对同一个 Prefab 资产的编辑会话（BUG-018）。
    ///
    /// <para>
    /// <b>解决什么：</b>此前每条 prefab 写指令都独立走
    /// <c>LoadPrefabContents → 改 → SaveAsPrefabAsset → Unload</c>。同一个批次里对同一个 Prefab
    /// 连发 60 条时，就是 60 次"重新从磁盘读一次"。只要这中间有任何东西拿的是旧副本
    /// （PrefabStage 的自动保存、异步导入、别处的 SaveAssets），后面那次整文件写回就会把前面
    /// 已经落盘的新节点静默抹掉——返回全是 <c>ok:true</c>，只有 <c>git diff</c> 才看得见。
    /// 本会话把"读"收敛成一次：整个批次共用同一份内存副本，每条指令成功后写回一次（提交点），
    /// 批次结束统一卸载。跨帧的窗口因此从 N 次缩到 1 次，且批次内的读后写永远看到自己刚做的改动。
    /// </para>
    ///
    /// <para>
    /// <b>失败语义：</b>某条指令抛异常或返回 Failed 时，整个会话被丢弃（不落盘），下一条指令
    /// 重新从磁盘加载——磁盘上留下的是"最后一条成功指令"的状态，失败那条的半成品不会跟着落盘。
    /// 这跟逐条 save 的旧语义一致（失败不写），但不会再把半成品带进下一次提交。
    /// </para>
    ///
    /// <para>
    /// <b>谁负责关：</b>批次边界在 <c>CommandHttpServer.Pump</c> / <c>CommandAutoRunner</c> /
    /// <c>ConsentGate</c> 收尾处调用 <see cref="Close"/>。漏调用只会多占一份内存副本，
    /// 不会丢数据（改动早已逐条提交）。
    /// </para>
    /// </summary>
    internal static class PrefabEditSession
    {
        private sealed class Entry
        {
            public GameObject Root;
        }

        private static readonly Dictionary<string, Entry> Open =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 本批次写过盘的 Prefab 路径（IMPR-029 / IMPR-030）。
        /// <para>
        /// 不按条目记：某条指令失败会 <see cref="Discard"/> 掉整条会话，条目跟着消失，
        /// 但"这之前已经提交成功过"是既成事实——那一份改动躺在磁盘上，照样需要重绘。
        /// </para>
        /// <para>
        /// 记路径而不只是 bool：批次结束时要比对"当前 PrefabStage 开的是不是其中一个"，
        /// 只有命中才值得抢系统前台（抢焦点有代价，不能无差别触发）。
        /// </para>
        /// </summary>
        private static HashSet<string> _committedInBatch =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前是否还有打开的会话（调试 / 状态排查用）</summary>
        public static int OpenCount
        {
            get { return Open.Count; }
        }

        /// <summary>
        /// 取这个 Prefab 在本批次内共用的内存副本。没有就加载一份并记进会话。
        /// <para>调用方<b>不要</b>自己 Unload——生命周期由本会话负责。</para>
        /// </summary>
        public static GameObject Acquire(CommandContext context, string prefabPath)
        {
            Entry entry;
            if (Open.TryGetValue(prefabPath, out entry))
            {
                if (entry.Root != null)
                {
                    return entry.Root;
                }

                // 根对象被外部销毁（域重载、用户关掉 Prefab 模式等）：丢掉这条记录重新加载
                Open.Remove(prefabPath);
            }

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null)
            {
                throw new CommandArgumentException(context.CommandId, "prefab",
                    $"打不开 Prefab '{prefabPath}'（确认路径存在且有 .prefab 后缀）");
            }

            Open[prefabPath] = new Entry { Root = root };
            return root;
        }

        /// <summary>只读路径用：批次里已经打开了这个 Prefab 就直接借用内存态，避免读到磁盘旧值。</summary>
        public static bool TryGetOpen(string prefabPath, out GameObject root)
        {
            root = null;
            Entry entry;
            if (!Open.TryGetValue(prefabPath, out entry) || entry.Root == null)
            {
                return false;
            }

            root = entry.Root;
            return true;
        }

        /// <summary>提交点：把内存副本整体写回资产（每条指令成功后调用一次）。</summary>
        /// <exception cref="CommandException">写盘失败（<c>SaveAsPrefabAsset</c> 返回 null）时抛出</exception>
        public static void Commit(string prefabPath, GameObject root)
        {
            // SaveAsPrefabAsset 失败时**返回 null 而不抛异常**（Unity 文档原话："or null if the save failed"）。
            // 不校验的话"改完了但没落盘"会一路报 ok:true —— 那正是 BUG-018 要消灭的静默丢改动，
            // 只不过这次丢在最后一步。所以把它变成明确的失败，并把这份写不进去的内存副本一起丢弃，
            // 免得下一条指令接着在这份脏副本上改。
            var saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            if (saved == null)
            {
                Discard(prefabPath);
                throw new CommandException(
                    $"写回 Prefab 失败：{prefabPath}（SaveAsPrefabAsset 返回 null，本次改动未落盘）");
            }

            _committedInBatch.Add(prefabPath);
        }

        /// <summary>
        /// 丢弃会话（不落盘）。指令失败时调用：连带这条指令已经改到的半成品一起丢掉，
        /// 下一条指令重新从磁盘加载，看到的仍是上一条成功指令提交后的样子。
        /// </summary>
        public static void Discard(string prefabPath)
        {
            Entry entry;
            if (!Open.TryGetValue(prefabPath, out entry))
            {
                return;
            }

            Open.Remove(prefabPath);
            UnloadQuietly(entry, prefabPath);
        }

        /// <summary>批次结束：卸载全部会话（改动早已逐条提交过，这里只回收内存）。</summary>
        public static void Close()
        {
            // 先消费"本批次写过哪些盘"：中途 Discard 会移除条目，
            // 但前面已经提交过的改动仍然躺在磁盘上，那一份照样该触发重绘
            var committed = _committedInBatch;
            _committedInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (Open.Count > 0)
            {
                var entries = new List<KeyValuePair<string, Entry>>(Open);
                Open.Clear();
                for (var i = 0; i < entries.Count; i++)
                {
                    UnloadQuietly(entries[i].Value, entries[i].Key);
                }
            }

            // 只有真的写过盘的批次才值得重绘；纯读（prefab.tree 借用会话）不打扰视图
            if (committed.Count > 0)
            {
                RequestViewRepaint(committed);
            }
        }

        /// <summary>
        /// 写完 Prefab 后请求一次全视图重绘；若 Stage 正开着本批次写过的 prefab，再把编辑器提到前台
        /// （IMPR-029 / IMPR-030）。
        ///
        /// <para>
        /// <b>为什么需要：</b><c>SaveAsPrefabAsset</c> 是同步 import，正开在 Prefab 编辑模式里的
        /// Stage 会因此整份重建 —— Canvas / Graphic 全是新的，<c>CanvasUpdateRegistry</c> 的
        /// layout rebuild 队列也在重建时被清空。这个队列只由一次 <c>Canvas.willRenderCanvases</c>
        /// （即一次视图 repaint）驱动；而 MCP 的泵跑在 <c>EditorApplication.update</c> 里，跑完就收工，
        /// 不会触发任何重绘 —— Stage 于是停在"布局还没算完"的一帧：ContentSizeFitter / LayoutGroup
        /// 还没把 sizeDelta 写回，Graphic 也还没 batch，整块 UI 一片空白。
        /// 必须人手点一下节点（触发 <c>OnSelectionChanged</c> → SceneView repaint）才显示出来。
        /// </para>
        ///
        /// <para>
        /// <b>为什么只 repaint、不做 <c>LayoutRebuilder.ForceRebuildLayoutImmediate</c>：</b>
        /// 后者会主动改写 Stage 里 RectTransform 的值，把 Stage 弄成 dirty，用户关窗口时
        /// Auto Save 一覆盖就是 BUG-018 的重演。repaint 是只读的，与用户手动点一下完全等价。
        /// </para>
        ///
        /// <para>
        /// <b>为什么扔到 <c>delayCall</c>：</b>Stage 的重建可能落在本次提交之后（import 收尾阶段），
        /// 立刻 repaint 会赶在重建之前，等于白请求。放到下一帧，重建已经落定。
        /// </para>
        /// </summary>
        /// <param name="committed">本批次写过盘的 prefab 路径集合，用来判断"要不要抢前台"</param>
        private static void RequestViewRepaint(HashSet<string> committed)
        {
            EditorApplication.delayCall += () =>
            {
                try
                {
                    // SceneView repaint 是真正驱动 Canvas.willRenderCanvases 的那一下；
                    // RepaintAllViews 顺带刷 Hierarchy / Inspector / Project，避免还显示旧节点树。
                    // （InspectorWindow 在 2021.3/2022.3 是 internal，拿不到；
                    //   InternalEditorUtility.RepaintAllViews 是跨版本稳定的等价入口。）
                    SceneView.RepaintAll();
                    UnityEditorInternal.InternalEditorUtility.RepaintAllViews();

                    // 编辑器在后台时 GUI 重绘会被节流：RepaintAllViews 只把视图标脏，
                    // 真正的 paint 要等窗口重新活跃。所以命中 Stage 时顺手把编辑器提到前台，
                    // 否则用户切回来看到的仍是旧画面，还得再点一下节点（IMPR-030）。
                    // 只在"Stage 正开着刚写过的那个 prefab"时抢，避免无差别打断用户。
                    var stage = PrefabStageUtility.GetCurrentPrefabStage();
                    if (stage == null || string.IsNullOrEmpty(stage.assetPath))
                    {
                        return;
                    }

                    if (!committed.Contains(stage.assetPath))
                    {
                        return;
                    }

                    string note;
                    if (!EditorWindowFocus.TryBringToFront(FindStageView(stage), out note)
                        && !string.IsNullOrEmpty(note))
                    {
                        CommandLog.Warning("Prefab 写入后未能把编辑器提到前台：" + note);
                    }
                }
                catch (Exception error)
                {
                    // 重绘失败绝不能把已经完成的批次变成失败
                    CommandLog.Warning("Prefab 写入后重绘视图失败：" + error.Message);
                }
            };
        }

        /// <summary>找 PrefabStage 自己那个 SceneView（不是主场景的那个）。</summary>
        private static SceneView FindStageView(PrefabStage stage)
        {
            var views = SceneView.sceneViews;
            if (views != null)
            {
                for (var i = 0; i < views.Count; i++)
                {
                    var view = views[i] as SceneView;

                    // SceneView 本身没有 scene 属性，要经由它的相机反查所属场景
                    if (view != null && view.camera != null && view.camera.scene == stage.scene)
                    {
                        return view;
                    }
                }
            }

            return SceneView.lastActiveSceneView;
        }

        private static void UnloadQuietly(Entry entry, string prefabPath)
        {
            if (entry == null || entry.Root == null)
            {
                return;
            }

            try
            {
                PrefabUtility.UnloadPrefabContents(entry.Root);
            }
            catch (Exception error)
            {
                // 卸载失败不该把已经完成的批次变成失败；留一条日志方便排查内存异常
                CommandLog.Warning($"卸载 Prefab 内容失败（{prefabPath}）：{error.Message}");
            }
            finally
            {
                entry.Root = null;
            }
        }
    }
}
