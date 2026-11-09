using System;
using System.Collections.Generic;
using UnityEngine;

namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// 布局驱动的 RectTransform 字段写入提示（IMPR-025）。
    ///
    /// <para>
    /// <b>现象：</b>给挂 <c>VerticalLayoutGroup</c> / <c>ContentSizeFitter</c> 的节点写
    /// <c>m_AnchoredPosition</c> / <c>m_SizeDelta</c>，指令返回成功，读回来还是旧值——Unity 的布局重建
    /// 按布局规则重算并覆盖了这两个序列化字段。调用方很容易误判成"根本没写进去"，然后反复重试。
    /// </para>
    ///
    /// <para>
    /// <b>为什么只提示不自动改：</b>自动把 <c>m_SizeDelta</c> 路由到 <c>sizeDelta</c> 属性上，
    /// 只是把"序列化值"换成"运行时值"，下一次布局重建照样会重算——真正的原因是这个节点本来就不该
    /// 由手写坐标决定位置/尺寸。所以这里给出可执行的下一步，而不是悄悄换一条路把问题盖住。
    /// </para>
    ///
    /// <para>
    /// 不引用 UGUI 类型：按类型名（含基类名）判定，包不需要为它多一个程序集引用。
    /// </para>
    /// </summary>
    internal static class LayoutDriveProbe
    {
        private enum FieldKind
        {
            None,
            Position,
            Size
        }

        /// <summary>
        /// 这次写入会不会被布局重建打回？会则返回一句提示，不会返回 <c>null</c>。
        /// <para>只看序列化字段（<c>m_AnchoredPosition</c> / <c>m_SizeDelta</c>）：
        /// 属性路径 <c>anchoredPosition</c> / <c>sizeDelta</c> 是运行时赋值，不在本提示范围内。</para>
        /// </summary>
        public static string DescribeSerializedOverwrite(Component component, string fieldName)
        {
            if (component == null || string.IsNullOrEmpty(fieldName))
            {
                return null;
            }

            var kind = Classify(fieldName);
            if (kind == FieldKind.None)
            {
                return null;
            }

            // 写入目标必须是 RectTransform（Transform 上根本没有这两个字段，这里只是防御）
            var rect = component as RectTransform;
            if (rect == null)
            {
                return null;
            }

            var drivers = new List<string>();

            // 自身 ContentSizeFitter：按内容反推自己的尺寸 → m_SizeDelta 会被重算
            if (kind == FieldKind.Size)
            {
                var fitter = FindByName(rect.gameObject, "ContentSizeFitter");
                if (fitter != null)
                {
                    drivers.Add("自身挂着 ContentSizeFitter");
                }
            }

            // 直接父节点上的布局组：它负责摆位置、也负责定尺寸（只作用于直接子节点，所以只看一级）
            var parent = rect.parent;
            if (parent != null)
            {
                var group = FindLayoutGroup(parent.gameObject);
                if (group != null)
                {
                    drivers.Add($"父节点 {parent.name} 上有 {group}");
                }
            }

            if (drivers.Count == 0)
            {
                return null;
            }

            var hint = kind == FieldKind.Size ? "sizeDelta" : "anchoredPosition";
            var field = kind == FieldKind.Size ? "m_SizeDelta" : "m_AnchoredPosition";
            return $"注意：{rect.name} 的 {field} 会被布局重建覆盖（{string.Join("；", drivers.ToArray())}），" +
                   $"读回可能仍是旧值。可改用属性路径 field: \"{hint}\"；若要彻底定死位置/尺寸，" +
                   "得改驱动它的布局参数（LayoutGroup 的 padding / spacing、LayoutElement 的 preferredSize 等）。";
        }

        /// <summary>一批字段里只要有一个会被打回就给一条提示（<c>component.setFields</c> 用）。</summary>
        public static string DescribeSerializedOverwrite(Component component, IEnumerable<string> fieldNames)
        {
            if (component == null || fieldNames == null)
            {
                return null;
            }

            foreach (var name in fieldNames)
            {
                var warning = DescribeSerializedOverwrite(component, name);
                if (!string.IsNullOrEmpty(warning))
                {
                    return warning;
                }
            }

            return null;
        }

        private static FieldKind Classify(string fieldName)
        {
            var name = fieldName.Trim();
            // 兼容 m_ 前缀与不带前缀两种写法，但只有 m_ 开头（序列化字段）才会被打回
            if (!name.StartsWith("m_", StringComparison.Ordinal))
            {
                return FieldKind.None;
            }

            if (string.Equals(name, "m_SizeDelta", StringComparison.Ordinal))
            {
                return FieldKind.Size;
            }

            if (string.Equals(name, "m_AnchoredPosition", StringComparison.Ordinal))
            {
                return FieldKind.Position;
            }

            return FieldKind.None;
        }

        /// <summary>按类型名（含其基类链）找组件，避免为 UGUI 类型加程序集引用</summary>
        private static Component FindByName(GameObject go, string typeName)
        {
            foreach (var component in go.GetComponents<Component>())
            {
                if (component != null && TypeNameMatches(component.GetType(), typeName))
                {
                    return component;
                }
            }

            return null;
        }

        /// <summary>找布局组（Horizontal / Vertical / Grid / 以及所有继承 LayoutGroup 的自定义布局），返回它的类型名</summary>
        private static string FindLayoutGroup(GameObject go)
        {
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null)
                {
                    continue;
                }

                var type = component.GetType();
                if (TypeNameMatches(type, "LayoutGroup"))
                {
                    return type.Name;
                }
            }

            return null;
        }

        /// <summary>类型自身或任一基类同名（LayoutGroup 是所有 UGUI 布局组的共同基类）</summary>
        private static bool TypeNameMatches(Type type, string name)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                if (string.Equals(current.Name, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
