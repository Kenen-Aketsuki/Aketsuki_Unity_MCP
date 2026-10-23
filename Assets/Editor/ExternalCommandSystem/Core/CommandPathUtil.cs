using System.IO;
using UnityEngine;

namespace CivilRev.CommandSystem
{
    /// <summary>
    /// 指令文件路径解析工具
    /// <para>
    /// 支持的写法（按优先级匹配）：
    /// <list type="bullet">
    /// <item>绝对路径：<c>D:/Proj/Commands/a.json</c> 或 <c>/home/user/a.json</c></item>
    /// <item>工作空间根：<c>@ws/UnityEditCommand/a.json</c>（项目根的上一级，即 <c>.workbuddy</c> 所在层）</item>
    /// <item>项目根目录：<c>~/Commands/a.json</c></item>
    /// <item>Unity 资源目录：<c>Assets/Cfgs/a.json</c></item>
    /// <item>其余相对路径：依次尝试「调用方基准目录 → 默认指令目录 → Assets 目录」，取第一个命中的</item>
    /// </list>
    /// </para>
    /// <para>
    /// 默认指令目录为 <c>&lt;工作空间根&gt;/.workbuddy/UnityEditCommand</c>，
    /// 位于 Unity 项目之外，既不参与资源导入，也便于与编辑器工作空间一起管理。
    /// </para>
    /// </summary>
    public static class CommandPathUtil
    {
        /// <summary>工作空间标记目录名</summary>
        public const string WorkspaceMarker = ".workbuddy";

        /// <summary>工作空间内的指令目录名</summary>
        public const string CommandFolderName = "UnityEditCommand";

        /// <summary>工作空间前缀（写在路径开头）</summary>
        public const string WorkspacePrefix = "@ws/";

        /// <summary>
        /// 项目根覆盖值：仅供离线测试注入（脱离 Unity 宿主时 <c>Application.dataPath</c> 不可用）。
        /// 运行期保持 null 即可；一旦设置，路径解析整体以它为项目根。
        /// </summary>
        public static string RootOverride;

        /// <summary>
        /// 额外允许访问的根目录（绝对路径，可选）。由 Editor 层把 <c>FileAccessRoots</c> 设置解析后注入；
        /// 离线测试也可直接填入。为空时仅放行 <see cref="ProjectRoot"/> 与 <see cref="WorkspaceRoot"/>。
        /// </summary>
        public static readonly System.Collections.Generic.List<string> ExtraAllowedRoots =
            new System.Collections.Generic.List<string>();

        /// <summary>Assets 目录绝对路径（Unity 下 dataPath 恒等于 项目根/Assets）</summary>
        public static string AssetsRoot => Path.Combine(ProjectRoot, "Assets");

        private static string _projectHash;

        /// <summary>
        /// 本工程的稳定短哈希（规范化工程路径的 SHA1 前 8 位 hex）。
        /// <para>
        /// 两个用途，都要求"同一个工程每次都得到同一个值"：
        /// ① 服务端信息文件名 <c>.http-info.&lt;hash&gt;</c>；
        /// ② EditorPrefs 的按工程隔离键后缀（端口 / 令牌 / 开关）。
        /// </para>
        /// <para>
        /// **算法必须与客户端 <c>send.py:project_hash()</c> 逐字节一致**：
        /// <c>GetFullPath → 去尾分隔符 → '\'→'/' → 小写 → SHA1 前 4 字节 → 8 hex</c>。
        /// </para>
        /// </summary>
        public static string ProjectHash
        {
            get
            {
                if (_projectHash != null)
                {
                    return _projectHash;
                }

                _projectHash = ComputeProjectHash();
                return _projectHash;
            }
        }

        private static string ComputeProjectHash()
        {
            try
            {
                var root = ProjectRoot;
                if (string.IsNullOrEmpty(root))
                {
                    return "unknown";
                }

                var normalized = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/')
                    .ToLowerInvariant();

                using (var sha1 = System.Security.Cryptography.SHA1.Create())
                {
                    var bytes = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(normalized));
                    var builder = new System.Text.StringBuilder(8);
                    for (var i = 0; i < 4; i++)
                    {
                        builder.Append(bytes[i].ToString("x2"));
                    }

                    return builder.ToString();
                }
            }
            catch (System.Exception)
            {
                // 算不出来时退化为固定名，至少不影响单工程使用
                return "unknown";
            }
        }

        /// <summary>项目根目录（Assets 的上一级）</summary>
        public static string ProjectRoot
        {
            get
            {
                if (!string.IsNullOrEmpty(RootOverride))
                {
                    return RootOverride;
                }

                string dataPath;
                try
                {
                    dataPath = ReadUnityDataPath();
                }
                catch (System.Exception)
                {
                    return string.Empty;
                }

                var parent = Directory.GetParent(dataPath);
                return parent != null ? parent.FullName : dataPath;
            }
        }

        /// <summary>
        /// 读取 Unity 的 dataPath。
        /// 单独成方法：Unity 内部调用（ECall）在脱离宿主时会让"包含它的方法"无法被 JIT，
        /// 隔离后可保证其余逻辑在非 Unity 环境下依旧可测。
        /// </summary>
        private static string ReadUnityDataPath()
        {
            return Application.dataPath;
        }

        private static string _workspaceRoot;
        private static string _workspaceRootKey;

        /// <summary>
        /// 工作空间根目录：项目根的上一级（当且仅当该目录存在 <c>.workbuddy</c> 子目录时成立），
        /// 否则退化为项目根目录
        /// </summary>
        public static string WorkspaceRoot
        {
            get
            {
                // 缓存按覆盖值失效，避免测试注入后拿到旧结果
                var key = RootOverride ?? string.Empty;
                if (_workspaceRoot == null || !string.Equals(_workspaceRootKey, key, System.StringComparison.Ordinal))
                {
                    _workspaceRootKey = key;
                    _workspaceRoot = FindWorkspaceRoot();
                }
                return _workspaceRoot;
            }
        }

        /// <summary>
        /// 默认指令目录：<c>&lt;工作空间根&gt;/.workbuddy/UnityEditCommand</c>
        /// <para>该约定不存在时退回 <c>&lt;项目根&gt;/Commands</c>，保证老工程也能用。</para>
        /// </summary>
        public static string DefaultCommandDirectory
        {
            get
            {
                var candidate = Path.Combine(WorkspaceRoot, WorkspaceMarker, CommandFolderName);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                // 工作空间根就是项目根时，上述 candidate 与下面这条等价，避免重复判断
                if (!string.Equals(candidate, LegacyCommandDirectory, System.StringComparison.Ordinal) &&
                    Directory.Exists(LegacyCommandDirectory))
                {
                    return LegacyCommandDirectory;
                }

                return candidate;
            }
        }

        /// <summary>旧版默认目录：项目根/Commands</summary>
        public static string LegacyCommandDirectory => Path.Combine(ProjectRoot, "Commands");

        /// <summary>确保默认指令目录存在（不存在则创建）</summary>
        public static string EnsureDefaultCommandDirectory()
        {
            var dir = DefaultCommandDirectory;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return dir;
        }

        /// <summary>
        /// 把用户书写的路径解析为绝对路径
        /// </summary>
        /// <returns>解析成功返回 true</returns>
        public static bool TryResolve(string path, out string fullPath)
        {
            return TryResolve(path, null, out fullPath);
        }

        /// <summary>
        /// 把用户书写的路径解析为绝对路径
        /// </summary>
        /// <param name="path">用户书写的路径</param>
        /// <param name="baseDirectory">
        /// 基准目录（通常是引用它的指令文件所在目录）。
        /// 相对路径会优先相对它解析，便于指令文件之间互相引用。
        /// </param>
        /// <param name="fullPath">解析出的绝对路径</param>
        /// <returns>解析成功返回 true</returns>
        public static bool TryResolve(string path, string baseDirectory, out string fullPath)
        {
            fullPath = null;
            var candidate = ComputeCandidate(path, baseDirectory);
            return candidate != null && NormalizeAndCheck(candidate, out fullPath);
        }

        /// <summary>
        /// 只算出候选路径，不做规范化与白名单校验。
        /// <para>
        /// 单独抽出来是为了让 <see cref="Resolve"/> 在拒绝时能给出精确原因
        /// （越界 / 黑名单 / 真解析不了），而不是笼统的"无法解析路径"。
        /// </para>
        /// </summary>
        /// <returns>候选路径；无法处理（如空串）时返回 null</returns>
        private static string ComputeCandidate(string path, string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var trimmed = path.Trim().Trim('"').Replace('\\', '/');

            if (Path.IsPathRooted(trimmed))
            {
                return trimmed;
            }

            if (trimmed.StartsWith(WorkspacePrefix, System.StringComparison.OrdinalIgnoreCase))
            {
                // @ws/xxx → 工作空间根/xxx
                return Path.Combine(WorkspaceRoot, trimmed.Substring(WorkspacePrefix.Length));
            }

            if (string.Equals(trimmed, "@ws", System.StringComparison.OrdinalIgnoreCase))
            {
                return WorkspaceRoot;
            }

            if (trimmed.StartsWith("~/"))
            {
                return Path.Combine(ProjectRoot, trimmed.Substring(2));
            }

            if (trimmed == "~")
            {
                return ProjectRoot;
            }

            if (trimmed.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase))
            {
                // Unity 惯用写法：Assets/xxx
                return Path.Combine(ProjectRoot, trimmed);
            }

            // 相对路径：依次尝试 基准目录 → 默认指令目录 → Assets
            var candidates = new System.Collections.Generic.List<string>(3);
            if (!string.IsNullOrEmpty(baseDirectory))
            {
                candidates.Add(Path.Combine(baseDirectory, trimmed));
            }
            candidates.Add(Path.Combine(DefaultCommandDirectory, trimmed));
            candidates.Add(Path.Combine(AssetsRoot, trimmed));

            for (var i = 0; i < candidates.Count; i++)
            {
                if (File.Exists(candidates[i]) || Directory.Exists(candidates[i]))
                {
                    return candidates[i];
                }
            }

            // 都不存在（多半是写出场景）：落到第一个候选，保证报错信息指向预期位置
            return candidates[0];
        }

        /// <summary>解析路径，失败时抛出 <see cref="CommandException"/></summary>
        public static string Resolve(string path)
        {
            return Resolve(path, null);
        }

        /// <summary>带基准目录地解析路径，失败时抛出 <see cref="CommandException"/></summary>
        public static string Resolve(string path, string baseDirectory)
        {
            var candidate = ComputeCandidate(path, baseDirectory);
            if (candidate == null)
            {
                throw new CommandException($"无法解析路径：{path}");
            }

            string fullPath;
            if (!Normalize(candidate, out fullPath))
            {
                throw new CommandException($"无法解析路径：{path}");
            }

            // 拒绝时给出精确原因：黑名单 / 越界，消息里带可自查的信息
            EnsureAccessible(fullPath);
            return fullPath;
        }

        /// <summary>路径是否为 .json 指令文件</summary>
        public static bool IsCommandFile(string path)
        {
            return !string.IsNullOrEmpty(path) &&
                   string.Equals(Path.GetExtension(path), ".json", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>取得某个文件所在的目录（用于给嵌套引用当基准目录）</summary>
        public static string GetDirectoryOf(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return null;
            }

            try
            {
                return Path.GetDirectoryName(filePath);
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        private static string FindWorkspaceRoot()
        {
            try
            {
                var parent = Directory.GetParent(ProjectRoot);
                if (parent != null && Directory.Exists(Path.Combine(parent.FullName, WorkspaceMarker)))
                {
                    return parent.FullName;
                }
            }
            catch (System.Exception)
            {
                // 忽略，退化为项目根
            }

            return ProjectRoot;
        }

        private static bool Normalize(string candidate, out string fullPath)
        {
            try
            {
                fullPath = Path.GetFullPath(candidate);
                return true;
            }
            catch (System.Exception)
            {
                fullPath = null;
                return false;
            }
        }

        /// <summary>
        /// 规范化路径并做白名单范围校验：规范化成功但越界时返回 false（越界视为解析失败，交由 <see cref="Resolve"/> 报出带根列表的错误）。
        /// </summary>
        private static bool NormalizeAndCheck(string candidate, out string fullPath)
        {
            fullPath = null;
            if (!Normalize(candidate, out fullPath))
            {
                return false;
            }

            // 白名单校验：越界（含黑名单目录）直接判为不可解析
            return IsWithinAllowedRoots(fullPath) && !IsWithinBlacklist(fullPath);
        }

        /// <summary>
        /// 判定一个（已规范化）路径是否落在允许根目录内。
        /// <para>只做「根 + 目录分隔符」前缀比较，避免 <c>D:/Proj/AssetsEVIL/x</c> 骗过 <c>StartsWith("D:/Proj/Assets")</c>；
        /// Windows 路径用 <see cref="System.StringComparison.OrdinalIgnoreCase"/>（不区分大小写）。</para>
        /// </summary>
        public static bool IsWithinAllowedRoots(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return false;
            }

            var roots = new System.Collections.Generic.List<string>();
            AddRootIfNonEmpty(roots, ProjectRoot);
            AddRootIfNonEmpty(roots, WorkspaceRoot);
            for (var i = 0; i < ExtraAllowedRoots.Count; i++)
            {
                AddRootIfNonEmpty(roots, ExtraAllowedRoots[i]);
            }

            for (var i = 0; i < roots.Count; i++)
            {
                if (IsWithinRoot(fullPath, roots[i]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 校验路径可访问（在允许根内且不在黑名单）；不满足时抛出 <see cref="CommandException"/>。
        /// <para>错误信息区分"黑名单"与"越界"，便于调用方一眼判断是配置问题还是路径写错。</para>
        /// </summary>
        public static void EnsureAccessible(string fullPath)
        {
            if (IsWithinBlacklist(fullPath))
            {
                throw new CommandException(
                    $"路径受限：'{fullPath}' 位于禁止访问的目录" +
                    "（.git / ProjectSettings / Library / Temp / 指令系统自身源码）");
            }

            if (IsWithinAllowedRoots(fullPath))
            {
                return;
            }

            var roots = new System.Collections.Generic.List<string>();
            AddRootIfNonEmpty(roots, ProjectRoot);
            AddRootIfNonEmpty(roots, WorkspaceRoot);
            for (var i = 0; i < ExtraAllowedRoots.Count; i++)
            {
                AddRootIfNonEmpty(roots, ExtraAllowedRoots[i]);
            }

            var list = roots.Count == 0 ? "（无）" : string.Join("；", roots);
            throw new CommandException(
                $"路径越界：'{fullPath}' 不在允许访问的根目录内。允许的根：{list}");
        }

        /// <summary>
        /// 校验路径是否在允许根内（且不在黑名单）；不满足时抛出 <see cref="CommandException"/>。
        /// <para>等价于 <see cref="EnsureAccessible"/>，保留此名以兼容既有调用方。</para>
        /// </summary>
        public static void EnsureWithinAllowedRoots(string fullPath)
        {
            EnsureAccessible(fullPath);
        }

        /// <summary>
        /// 即使落在允许根内也禁止读写的高危目录（黑名单兜底）。
        /// <para>用于保护 <c>.git/</c>、<c>ProjectSettings/</c>、<c>Library/</c>、<c>Temp/</c> 以及工具自身源码目录。</para>
        /// </summary>
        public static bool IsWithinBlacklist(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return false;
            }

            var normalized = Path.GetFullPath(fullPath);

            // 逐个高危目录做「根 + 分隔符」前缀判断；目录名本身也在黑名单内
            var projectDir = ProjectRoot;
            string[] blacklist = null;
            if (!string.IsNullOrEmpty(projectDir))
            {
                blacklist = new[]
                {
                    Path.Combine(projectDir, ".git"),
                    Path.Combine(projectDir, "ProjectSettings"),
                    Path.Combine(projectDir, "Library"),
                    Path.Combine(projectDir, "Temp"),
                    Path.Combine(projectDir, "Assets", "Editor", "ExternalCommandSystem"),
                };
            }

            if (blacklist != null)
            {
                for (var i = 0; i < blacklist.Length; i++)
                {
                    if (IsWithinRoot(normalized, blacklist[i]))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>判定（已规范化）路径是否位于某允许根内</summary>
        public static bool IsWithinRoot(string fullPath, string root)
        {
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            // 规范化根，去掉末尾分隔符，保证后续拼接「根 + 目录分隔符」时不会出现双分隔符
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(fullPath, normalizedRoot, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 关键：必须加目录分隔符做前缀比较，否则 D:/Proj/AssetsEVIL 会误命中 D:/Proj/Assets
            var prefix = normalizedRoot + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase);
        }

        private static void AddRootIfNonEmpty(System.Collections.Generic.List<string> roots, string root)
        {
            if (!string.IsNullOrEmpty(root) && !roots.Contains(root))
            {
                roots.Add(root);
            }
        }
    }
}
