using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor.PackageManager;

namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// 「本工具的源码住在哪」的唯一答案。
    ///
    /// <para>
    /// <b>要解决的问题：</b>同一份代码有两种落地形态 ——
    /// UPM 包（<c>Packages/&lt;包名&gt;/</c> 或 <c>Library/PackageCache/&lt;包名&gt;@版本/</c>）
    /// 和早期的资产形态（<c>Assets/Editor/ExternalCommandSystem/</c>）。
    /// 任何需要"找到自己"的地方（桥脚本目录、源码只读保护）都不能再写死
    /// <c>Assets/Editor/ExternalCommandSystem</c>：包形态下那条路径根本不存在，
    /// 而且 PackageCache 目录名带 <c>@版本</c> 后缀，无法静态拼出来。
    /// </para>
    ///
    /// <para>
    /// <b>为什么以程序集为锚点：</b><see cref="PackageInfo.FindForAssembly"/>
    /// 由 Package Manager 自己维护，包升级、被换到 PackageCache、被 embed 都跟着程序集走，
    /// 不依赖任何目录命名约定。资产形态下它返回 null，正好用来区分两种形态。
    /// </para>
    ///
    /// <para>
    /// <b>缓存策略：</b>解析只做一次。包装卸会触发重编译 + 域重载，静态状态自然重建，
    /// 不需要手动清理。
    /// </para>
    /// </summary>
    public static class PackagePaths
    {
        /// <summary>包名（与 <c>package.json</c> 的 <c>name</c> 必须一致）</summary>
        public const string PackageName = "com.aketsuki.unity-mcp";

        /// <summary>MCP 桥脚本目录名（位于包内 <c>Editor/</c> 下）</summary>
        public const string BridgeFolderName = "McpBridge";

        /// <summary>资产形态下的源码目录（相对工程根）。仅为兼容还没迁到包的目标工程保留。</summary>
        public const string LegacyAssetRelative = "Assets/Editor/ExternalCommandSystem";

        /// <summary>包形态下的源码目录（相对工程根，embedded / 本地包）。</summary>
        public const string PackagedEditorRelative = "Packages/" + PackageName + "/Editor";

        private static bool _packageResolved;
        private static PackageInfo _package;

        /// <summary>本程序集所属的包信息；资产形态（代码在 Assets 下）返回 null</summary>
        public static PackageInfo Package
        {
            get
            {
                if (!_packageResolved)
                {
                    _packageResolved = true;
                    try
                    {
                        _package = PackageInfo.FindForAssembly(typeof(PackagePaths).Assembly);
                    }
                    catch (Exception)
                    {
                        // 脱离 Unity 宿主的纯离线场景会走到这里；按资产形态处理
                        _package = null;
                    }
                }

                return _package;
            }
        }

        /// <summary>是否以 UPM 包形态存在</summary>
        public static bool IsPackaged
        {
            get { return Package != null && !string.IsNullOrEmpty(Package.resolvedPath); }
        }

        /// <summary>包根绝对路径；资产形态返回 null</summary>
        public static string PackageRoot
        {
            get { return IsPackaged ? Package.resolvedPath : null; }
        }

        /// <summary>
        /// 本工具源码内容的根目录（<b>绝对路径</b>）：包形态是包根，资产形态是
        /// <c>&lt;工程&gt;/Assets/Editor/ExternalCommandSystem</c>。
        /// <para>用于"禁止指令改写工具自身源码"的黑名单；拿不到时返回 null（调用方按"无此项"处理）。</para>
        /// </summary>
        public static string SelfRoot
        {
            get
            {
                if (IsPackaged)
                {
                    return Package.resolvedPath;
                }

                var projectRoot = CommandPathUtil.ProjectRoot;
                if (string.IsNullOrEmpty(projectRoot))
                {
                    return null;
                }

                return Path.Combine(projectRoot, "Assets", "Editor", "ExternalCommandSystem");
            }
        }

        /// <summary>
        /// 解析桥脚本目录，返回<b>相对工程根</b>的路径（分隔符统一为 <c>/</c>）。
        /// <para>
        /// 返回相对路径而不是绝对路径：调用方（<c>McpSetup</c>）习惯用
        /// <c>Path.Combine(ProjectRoot, 相对路径)</c> 拿实际目录，且日志里相对路径更好读。
        /// </para>
        /// </summary>
        public static bool TryGetBridgeAssetDir(out string projectRelative)
        {
            projectRelative = null;

            var candidates = new List<string>();

            // ① PackageInfo 给出的真实位置（最准：Git 依赖、PackageCache、embedded 都成立）
            if (IsPackaged)
            {
                var relative = MakeProjectRelative(Path.Combine(Package.resolvedPath, "Editor", BridgeFolderName));
                if (!string.IsNullOrEmpty(relative))
                {
                    candidates.Add(relative);
                }
            }

            // ② 固定约定路径（embedded / 本地路径包）
            candidates.Add(PackagedEditorRelative + "/" + BridgeFolderName);

            // ③ PackageCache 下带版本后缀的目录（registry / Git 依赖）
            AddPackageCacheCandidates(candidates);

            // ④ 旧资产形态（还没迁移的目标工程）
            candidates.Add(LegacyAssetRelative + "/" + BridgeFolderName);

            var projectRoot = CommandPathUtil.ProjectRoot;
            if (string.IsNullOrEmpty(projectRoot))
            {
                return false;
            }

            for (var i = 0; i < candidates.Count; i++)
            {
                var absolute = Path.Combine(projectRoot, candidates[i].Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(absolute))
                {
                    projectRelative = candidates[i];
                    return true;
                }
            }

            return false;
        }

        /// <summary>把绝对路径转成相对工程根的 <c>/</c> 分隔路径；不在工程内返回 null</summary>
        public static string MakeProjectRelative(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath))
            {
                return null;
            }

            var projectRoot = CommandPathUtil.ProjectRoot;
            if (string.IsNullOrEmpty(projectRoot))
            {
                return null;
            }

            var root = Path.GetFullPath(projectRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(absolutePath);

            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return full.Substring(root.Length + 1).Replace('\\', '/');
        }

        /// <summary>枚举 <c>Library/PackageCache/com.aketsuki.unity-mcp@*</c></summary>
        private static void AddPackageCacheCandidates(List<string> candidates)
        {
            var projectRoot = CommandPathUtil.ProjectRoot;
            if (string.IsNullOrEmpty(projectRoot))
            {
                return;
            }

            var cacheRoot = Path.Combine(projectRoot, "Library", "PackageCache");
            if (!Directory.Exists(cacheRoot))
            {
                return;
            }

            try
            {
                var dirs = Directory.GetDirectories(cacheRoot, PackageName + "@*");
                for (var i = 0; i < dirs.Length; i++)
                {
                    var relative = MakeProjectRelative(Path.Combine(dirs[i], "Editor", BridgeFolderName));
                    if (!string.IsNullOrEmpty(relative) && !candidates.Contains(relative))
                    {
                        candidates.Add(relative);
                    }
                }
            }
            catch (Exception)
            {
                // 目录被占用/权限不足时静默跳过：候选 ②④ 仍可用
            }
        }
    }
}
