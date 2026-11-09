using System;
using System.Collections.Generic;
using UnityEditor;
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

            /// <summary>提交过至少一次：用来在收尾时如实报告"本次会话有没有真的写过盘"</summary>
            public bool Committed;
        }

        private static readonly Dictionary<string, Entry> Open =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

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
        public static void Commit(string prefabPath, GameObject root)
        {
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);

            Entry entry;
            if (Open.TryGetValue(prefabPath, out entry))
            {
                entry.Committed = true;
            }
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
            if (Open.Count == 0)
            {
                return;
            }

            var entries = new List<KeyValuePair<string, Entry>>(Open);
            Open.Clear();
            for (var i = 0; i < entries.Count; i++)
            {
                UnloadQuietly(entries[i].Value, entries[i].Key);
            }
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
