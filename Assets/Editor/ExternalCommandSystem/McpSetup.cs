using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CivilRev.CommandSystem.Editor
{
    /// <summary>
    /// MCP 桥的安装器：把 UnityPackage 内自带的 Python 脚本部署到本工程，
    /// 并把启动项注册进 WorkBuddy 的 MCP 配置。
    ///
    /// <para>
    /// 为什么需要安装这一步：MCP 桥是 Python 进程，Unity 既不能执行它、也不能把它注册给 MCP 宿主。
    /// 包只能"带着"脚本，真正落地要靠本类把脚本复制到运行位置、把绝对路径写进 mcp.json。
    /// 每个工程的路径都不同，所以这一步无法预置在包里，只能装完再生成。
    /// </para>
    ///
    /// <para>
    /// 脚本部署到 <c>&lt;工程根&gt;/.workbuddy/skills/unity-cmd/scripts/</c> 而不是 Assets 下：
    /// 一是 Unity 不会去导入/编译它们，二是 <c>send.py</c> 的自动发现正好能从该位置向上找到
    /// <c>.workbuddy</c>，与服务端的信息文件目录规则天然对齐。
    /// </para>
    /// </summary>
    public static class McpSetup
    {
        private const string MenuRoot = "Tools/指令系统/MCP/";

        /// <summary>包内脚本目录名（位于 ExternalCommandSystem 下）</summary>
        private const string BridgeFolderName = "McpBridge";

        /// <summary>EditorPrefs 里记住的 Python 解释器路径（换机器/换环境时可改）</summary>
        private const string PythonPrefKey = "CivilRev.CommandSystem.McpPython";

        /// <summary>
        /// 单工程时代的通用键名。
        /// <para>
        /// 以前所有工程都往 <c>mcpServers["unity-cmd"]</c> 里写，装第二个工程时会把上一个工程的
        /// 条目**原地覆盖**掉——表现为"换了个工程，原来那个工程的 MCP 就没了"。
        /// 现在按工程派生键名；只有这个键空闲、或本来就指向本工程时才继续沿用它，
        /// 保持单工程场景的配置不变。
        /// </para>
        /// </summary>
        private const string LegacyServerKey = "unity-cmd";

        /// <summary>最近一次写入时实际使用的服务键（用于界面提示）</summary>
        private static string _lastServerKey = LegacyServerKey;

        /// <summary>工程目录名，用于派生服务键</summary>
        private static string ProjectName
        {
            get
            {
                var trimmed = ProjectRoot.TrimEnd('\\', '/');
                var index = trimmed.LastIndexOfAny(new[] { '\\', '/' });
                return index >= 0 && index + 1 < trimmed.Length ? trimmed.Substring(index + 1) : trimmed;
            }
        }

        /// <summary>
        /// 工作区根：父目录有 <c>.workbuddy</c> 就取父目录，否则取工程根。
        /// 与服务端 <c>CommandPathUtil.FindWorkspaceRoot</c>、<c>send.py:workspace_root_for</c> 保持一致。
        /// </summary>
        private static string WorkspaceRoot
        {
            get
            {
                var parent = Directory.GetParent(ProjectRoot);
                if (parent != null && Directory.Exists(Path.Combine(parent.FullName, ".workbuddy")))
                {
                    return parent.FullName;
                }

                return ProjectRoot;
            }
        }

        private const string SkillFolderName = "unity-cmd";

        #region 路径

        /// <summary>包内 MCP 脚本目录（相对 Unity 项目根）</summary>
        private static string BridgeAssetDir
        {
            get { return "Assets/Editor/ExternalCommandSystem/" + BridgeFolderName; }
        }

        /// <summary>Unity 工程根（Assets 的上一级）</summary>
        private static string ProjectRoot
        {
            get
            {
                var parent = Directory.GetParent(Application.dataPath);
                return parent != null ? parent.FullName : Application.dataPath;
            }
        }

        /// <summary>脚本部署目录：&lt;工程根&gt;/.workbuddy/skills/unity-cmd/scripts</summary>
        private static string InstallScriptDir
        {
            get
            {
                return Path.Combine(ProjectRoot, ".workbuddy", "skills", SkillFolderName, "scripts");
            }
        }

        /// <summary>Skill 部署目录：&lt;工程根&gt;/.workbuddy/skills/unity-cmd</summary>
        private static string InstallSkillDir
        {
            get
            {
                var parent = Directory.GetParent(InstallScriptDir);
                return parent != null ? parent.FullName : InstallScriptDir;
            }
        }

        private static string McpConfigPath
        {
            get
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, ".workbuddy", "mcp.json");
            }
        }

        /// <summary>
        /// 找到包内脚本目录。优先用固定路径，找不到再按资产名反查
        /// （包被用户改了目录结构时仍能工作）。
        /// </summary>
        private static string ResolveBridgeAssetDir()
        {
            var fixedPath = Path.Combine(ProjectRoot, BridgeAssetDir);
            if (Directory.Exists(fixedPath))
            {
                return BridgeAssetDir;
            }

            foreach (var guid in AssetDatabase.FindAssets("unity_mcp_server"))
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (assetPath.EndsWith("unity_mcp_server.py", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetDirectoryName(assetPath).Replace('\\', '/');
                }
            }

            return null;
        }

        #endregion

        #region 菜单

        [MenuItem(MenuRoot + "① 安装桥到本工程", priority = 1)]
        private static void InstallMenuItem()
        {
            Install();
        }

        [MenuItem(MenuRoot + "② 查看将要写入的 MCP 配置", priority = 2)]
        private static void ShowConfigMenuItem()
        {
            Debug.Log("[McpSetup] MCP 配置路径：" + McpConfigPath + "\n"
                      + BuildServerEntry().ToJson(true));
        }

        [MenuItem(MenuRoot + "③ 只写入 MCP 配置（不复制脚本）", priority = 3)]
        private static void WriteConfigOnlyMenuItem()
        {
            try
            {
                var path = WriteMcpConfig();
                CommandLog.Info("已写入 MCP 配置：" + path);
                EditorUtility.DisplayDialog("已写入",
                    "MCP 配置已更新：\n" + path +
                    "\n\n请到连接器管理页的自定义连接器中点「信任」" + _lastServerKey + "。", "好");
            }
            catch (Exception e)
            {
                CommandLog.Error("写入 MCP 配置失败：" + e.Message);
                EditorUtility.DisplayDialog("写入失败", e.Message, "知道了");
            }
        }

        [MenuItem(MenuRoot + "④ 从 MCP 配置中移除", priority = 4)]
        private static void RemoveConfigMenuItem()
        {
            try
            {
                var path = McpConfigPath;
                if (!File.Exists(path))
                {
                    EditorUtility.DisplayDialog("无需移除", "本机还没有 MCP 配置文件。", "好");
                    return;
                }

                var root = LoadJson(path);
                var servers = root["mcpServers"];
                if (servers == null || !servers.IsObject)
                {
                    EditorUtility.DisplayDialog("无需移除", "配置里没有 mcpServers 段。", "好");
                    return;
                }

                // 只要条目的脚本路径或 --project-root 落在本工程/本工作区，就算本工程的
                // （这样老键 unity-cmd 和按工程派生的键都能一并清掉，不会漏）
                var owned = new List<string>();
                var rebuilt = JsonValue.NewObject();
                foreach (var key in servers.Keys)
                {
                    if (EntryBelongsToThisProject(servers[key]))
                    {
                        owned.Add(key);
                    }
                    else
                    {
                        rebuilt.Set(key, servers[key]);
                    }
                }

                if (owned.Count == 0)
                {
                    CommandLog.Info("MCP 配置里没有属于本工程的条目（" + path + "）");
                    EditorUtility.DisplayDialog("无需移除", "配置里没有属于本工程的条目。", "好");
                    return;
                }

                if (!EditorUtility.DisplayDialog("移除 MCP 配置",
                        "将从下面这个文件里移除 " + owned.Count + " 个条目：" +
                        "\n" + path + "\n\n  " + string.Join("\n  ", owned.ToArray()) +
                        "\n\n已部署的脚本不会被删除（可用菜单 ⑤ 卸载桥 单独清理）。",
                        "移除", "取消"))
                {
                    return;
                }

                root.Set("mcpServers", rebuilt);
                Backup(path);
                File.WriteAllText(path, root.ToJson(true), new UTF8Encoding(false));
                CommandLog.Info("已从 MCP 配置移除：" + string.Join(", ", owned.ToArray()));
            }
            catch (Exception e)
            {
                CommandLog.Error("移除 MCP 配置失败：" + e.Message);
            }
        }

        [MenuItem(MenuRoot + "⑤ 卸载本工程的桥文件", priority = 5)]
        private static void UninstallMenuItem()
        {
            if (!Directory.Exists(InstallSkillDir))
            {
                EditorUtility.DisplayDialog("无需卸载", "本工程没有已部署的桥文件。", "好");
                return;
            }

            if (!EditorUtility.DisplayDialog("卸载桥文件",
                    "将删除目录：\n" + InstallSkillDir +
                    "\n\n（MCP 配置需要另外用菜单 ④ 移除）", "删除", "取消"))
            {
                return;
            }

            try
            {
                Directory.Delete(InstallSkillDir, true);
                CommandLog.Info("已删除 " + InstallSkillDir);
            }
            catch (Exception e)
            {
                CommandLog.Error("删除失败：" + e.Message);
            }
        }

        #endregion

        #region 安装实现

        /// <summary>安装结果（供菜单展示，也供自动化调用方读取）</summary>
        public sealed class InstallResult
        {
            public bool Ok;
            public string Message;
            public string ScriptDir;
            public string ConfigPath;
            public int Added;
            public int Updated;
            public int Unchanged;
            public int BackedUp;
        }

        private static void Install()
        {
            try
            {
                var bridgeDir = ResolveBridgeAssetDir();
                if (string.IsNullOrEmpty(bridgeDir))
                {
                    // 开发源工程：脚本本来就在 .workbuddy 下，Assets 里没有 McpBridge 副本
                    if (File.Exists(Path.Combine(InstallScriptDir, "unity_mcp_server.py")))
                    {
                        var writeConfig = EditorUtility.DisplayDialog(
                            "本工程已部署过桥",
                            "本工程的 .workbuddy 下已有桥文件，但 Assets 里没有 McpBridge 目录" +
                            "（这是开发源工程的正常状态）。\n\n是否只把 MCP 配置写入 mcp.json？",
                            "只写配置", "取消");
                        if (writeConfig)
                        {
                            WriteConfigOnlyMenuItem();
                        }
                        return;
                    }

                    EditorUtility.DisplayDialog("安装失败",
                        "包内找不到 " + BridgeFolderName + " 目录。\n" +
                        "请确认本包是完整导入的（不是只导入了部分文件）。", "知道了");
                    return;
                }

                var result = InstallFrom(bridgeDir);
                var reveal = EditorUtility.DisplayDialog(
                    "MCP 桥已安装",
                    result.Message +
                    "\n\n最后一步（必须手动做）：打开连接器管理页，在自定义连接器中点「信任」" +
                    _lastServerKey + "，MCP 才会启动。",
                    "打开配置所在目录", "关闭");

                if (reveal)
                {
                    EditorUtility.RevealInFinder(result.ConfigPath);
                }
            }
            catch (Exception e)
            {
                CommandLog.Error("安装 MCP 桥失败：" + e);
                EditorUtility.DisplayDialog("安装失败", e.Message, "知道了");
            }
        }

        /// <summary>
        /// 无交互安装：复制脚本 + 写 MCP 配置，全程不弹任何对话框。
        /// <para>
        /// 菜单「① 安装」与自动化调用（编辑器探针、CI、AI 自举）都走这里，
        /// 保证"人点的"和"程序点的"是同一条代码路径，不会出现只有菜单才生效的分支。
        /// </para>
        /// </summary>
        /// <returns>安装结果；包内缺少 McpBridge 且本工程也没有已部署脚本时返回 null</returns>
        public static InstallResult InstallSilent()
        {
            try
            {
                var bridgeDir = ResolveBridgeAssetDir();
                var deployed = File.Exists(Path.Combine(InstallScriptDir, "unity_mcp_server.py"));

                if (string.IsNullOrEmpty(bridgeDir))
                {
                    if (!deployed)
                    {
                        CommandLog.Error("安装失败：包内找不到 " + BridgeFolderName + " 目录，本工程也没有已部署的桥文件");
                        return null;
                    }

                    // 开发源工程：Assets 里没有 McpBridge 副本，只刷配置
                    var onlyCfg = WriteMcpConfig();
                    CommandLog.Info("本工程无 McpBridge 副本（开发源工程），只写入 MCP 配置：" + onlyCfg);
                    return new InstallResult
                    {
                        Ok = true,
                        Message = "本工程无 McpBridge 副本（开发源工程），只更新了 MCP 配置：\n" + onlyCfg,
                        ScriptDir = InstallScriptDir,
                        ConfigPath = onlyCfg
                    };
                }

                return InstallFrom(bridgeDir);
            }
            catch (Exception e)
            {
                CommandLog.Error("安装 MCP 桥失败：" + e);
                return new InstallResult { Ok = false, Message = "安装失败：" + e.Message };
            }
        }

        /// <summary>安装的实际动作：部署脚本 + 写配置（无对话框）</summary>
        private static InstallResult InstallFrom(string bridgeAssetDir)
        {
            var stats = DeployScripts(bridgeAssetDir);
            var configPath = WriteMcpConfig();

            var message = string.Format(
                "已部署到：{0}\n新增 {1} 个 / 更新 {2} 个 / 未变 {3} 个",
                InstallScriptDir, stats.Added, stats.Updated, stats.Unchanged);

            if (stats.BackedUp > 0)
            {
                message += string.Format(
                    "\n其中 {0} 个文件与包内版本不同，原文件已备份为 *.bak.<时间戳>",
                    stats.BackedUp);
            }

            message += "\n\n已注册到：\n" + configPath;

            CommandLog.Info("MCP 桥已部署：" + InstallScriptDir
                            + "（新增 " + stats.Added + " / 更新 " + stats.Updated
                            + " / 未变 " + stats.Unchanged + " / 备份 " + stats.BackedUp + "）");
            CommandLog.Info("MCP 配置已写入 " + configPath);

            return new InstallResult
            {
                Ok = true,
                Message = message,
                ScriptDir = InstallScriptDir,
                ConfigPath = configPath,
                Added = stats.Added,
                Updated = stats.Updated,
                Unchanged = stats.Unchanged,
                BackedUp = stats.BackedUp
            };
        }

        /// <summary>部署统计</summary>
        private struct DeployStats
        {
            public int Added;
            public int Updated;
            public int Unchanged;
            public int BackedUp;
        }

        /// <summary>
        /// 把包内脚本/文档复制到运行位置。
        /// <para>
        /// 目标已存在且内容不同时，<b>先备份再覆盖</b>：目标位置可能放着别的工程的旧版脚本
        /// （例如一行 <c>File.Copy(..., true)</c> 就会把对方的定制逻辑冲掉，且无从恢复）。
        /// 内容相同则直接跳过，避免重复安装堆出一堆 .bak。
        /// </para>
        /// </summary>
        private static DeployStats DeployScripts(string bridgeAssetDir)
        {
            var stats = new DeployStats();
            var sourceDir = Path.Combine(ProjectRoot, bridgeAssetDir);
            if (!Directory.Exists(InstallScriptDir))
            {
                Directory.CreateDirectory(InstallScriptDir);
            }

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                // .meta 是 Unity 的资产元数据，不该跟着走
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = Path.GetFileName(file);
                string target;

                if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    // 文档放到 skill 根目录，脚本放 scripts/
                    if (!Directory.Exists(InstallSkillDir))
                    {
                        Directory.CreateDirectory(InstallSkillDir);
                    }
                    target = Path.Combine(InstallSkillDir, name);
                }
                else
                {
                    target = Path.Combine(InstallScriptDir, name);
                }

                if (!File.Exists(target))
                {
                    File.Copy(file, target, false);
                    stats.Added++;
                    continue;
                }

                if (SameContent(file, target))
                {
                    stats.Unchanged++;
                    continue;
                }

                Backup(target);
                File.Copy(file, target, true);
                stats.Updated++;
                stats.BackedUp++;
            }

            return stats;
        }

        /// <summary>逐字节比较，用于判断"要不要覆盖/要不要备份"</summary>
        private static bool SameContent(string left, string right)
        {
            try
            {
                var a = new FileInfo(left);
                var b = new FileInfo(right);
                if (a.Length != b.Length)
                {
                    return false;
                }

                using (var sa = a.OpenRead())
                using (var sb = b.OpenRead())
                {
                    var ba = new byte[81920];
                    var bb = new byte[81920];
                    int ra;
                    while ((ra = sa.Read(ba, 0, ba.Length)) > 0)
                    {
                        var rb = sb.Read(bb, 0, bb.Length);
                        if (rb != ra)
                        {
                            return false;
                        }

                        for (var i = 0; i < ra; i++)
                        {
                            if (ba[i] != bb[i])
                            {
                                return false;
                            }
                        }
                    }
                }

                return true;
            }
            catch (Exception)
            {
                // 读不了就当不同，宁可多备份一次
                return false;
            }
        }

        /// <summary>把本服务条目合并进 mcp.json（保留其它服务），返回配置文件路径</summary>
        private static string WriteMcpConfig()
        {
            var path = McpConfigPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            JsonValue root;
            if (File.Exists(path))
            {
                root = LoadJson(path);
                Backup(path);
            }
            else
            {
                root = JsonValue.NewObject();
            }

            var servers = root["mcpServers"];
            if (servers == null || !servers.IsObject)
            {
                servers = JsonValue.NewObject();
            }

            // 键名要按"当前已有哪些工程"决定，所以必须在拿到 root 之后算
            _lastServerKey = ResolveServerKey(root);
            servers.Set(_lastServerKey, BuildServerEntry());
            // 取出来的可能是副本实现，显式回设一次，保证一定写进 root
            root.Set("mcpServers", servers);

            File.WriteAllText(path, root.ToJson(true), new UTF8Encoding(false));
            return path;
        }

        /// <summary>
        /// 决定本工程写进 mcp.json 的服务键名。
        /// <para>
        /// <c>unity-cmd</c> 空闲、或本来就指向本工程 → 继续用 <c>unity-cmd</c>（单工程场景配置不变）。
        /// 已被别的工程占用 → 用 <c>unity-cmd-&lt;工程名&gt;</c>，重名再加 8 位路径哈希。
        /// </para>
        /// </summary>
        private static string ResolveServerKey(JsonValue root)
        {
            var servers = root == null ? null : root["mcpServers"];
            if (servers == null || !servers.IsObject || !servers.ContainsKey(LegacyServerKey))
            {
                return LegacyServerKey;
            }

            if (EntryBelongsToThisProject(servers[LegacyServerKey]))
            {
                return LegacyServerKey;
            }

            var key = LegacyServerKey + "-" + SanitizeKey(ProjectName);
            if (servers.ContainsKey(key) && !EntryBelongsToThisProject(servers[key]))
            {
                key = key + "-" + ShortPathHash(ProjectRoot);
            }

            return key;
        }

        /// <summary>
        /// 判断一条 mcpServers 条目是否属于本工程。
        /// <para>
        /// 依据有两个：args 里的 <c>--project-root</c>，以及脚本自身的绝对路径
        /// （脚本部署在 <c>&lt;工作区&gt;/.workbuddy/skills/unity-cmd/scripts/</c> 下）。
        /// 直接看序列化后的文本即可，不必逐字段解析。
        /// </para>
        /// </summary>
        private static bool EntryBelongsToThisProject(JsonValue entry)
        {
            if (entry == null)
            {
                return false;
            }

            var text = NormalizeForMatch(entry.ToJson(false));
            return text.IndexOf(NormalizeForMatch(ProjectRoot), StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf(NormalizeForMatch(WorkspaceRoot), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string NormalizeForMatch(string path)
        {
            return path == null ? string.Empty : path.Replace('\\', '/');
        }

        /// <summary>工程名 → 可用作 JSON 键的标识（只留 ascii 字母数字与小横线）</summary>
        private static string SanitizeKey(string raw)
        {
            var builder = new StringBuilder();
            foreach (var c in raw ?? string.Empty)
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    builder.Append(c);
                }
                else if (c >= 'A' && c <= 'Z')
                {
                    builder.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    builder.Append('-');
                }
            }

            var text = builder.ToString().Trim('-');
            return text.Length == 0 ? "project" : text;
        }

        /// <summary>路径短哈希（8 hex），算法与服务端信息文件名的 hash 一致</summary>
        private static string ShortPathHash(string path)
        {
            try
            {
                using (var sha1 = System.Security.Cryptography.SHA1.Create())
                {
                    var bytes = sha1.ComputeHash(
                        Encoding.UTF8.GetBytes(path.Replace('\\', '/').ToLowerInvariant()));
                    var builder = new StringBuilder(8);
                    for (var i = 0; i < 4; i++)
                    {
                        builder.Append(bytes[i].ToString("x2"));
                    }

                    return builder.ToString();
                }
            }
            catch (Exception)
            {
                return "00000000";
            }
        }

        /// <summary>构造 mcpServers["unity-cmd"] 的内容</summary>
        private static JsonValue BuildServerEntry()
        {
            var script = Path.Combine(InstallScriptDir, "unity_mcp_server.py");

            var args = JsonValue.FromArray(new[]
            {
                JsonValue.Of(script),
                // 显式给工程根：脚本虽部署在 .workbuddy 下，但显式指明更抗路径变化
                JsonValue.Of("--project-root"),
                JsonValue.Of(ProjectRoot)
            });

            var env = JsonValue.NewObject();
            // Windows 下不加这两个，Python 可能用 cp936 写 stdout，MCP 客户端会解析失败
            env.Set("PYTHONIOENCODING", JsonValue.Of("utf-8"));
            env.Set("PYTHONUTF8", JsonValue.Of("1"));

            var entry = JsonValue.NewObject();
            entry.Set("command", JsonValue.Of(ResolvePython()));
            entry.Set("args", args);
            entry.Set("env", env);
            return entry;
        }

        /// <summary>
        /// 找一个可用的 Python 解释器。
        /// 优先 WorkBuddy 托管版本（本机已装且版本可控），其次 PATH 里的 python。
        /// </summary>
        private static string ResolvePython()
        {
            var configured = EditorPrefs.GetString(PythonPrefKey, string.Empty);
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
            {
                return configured;
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var versionsDir = Path.Combine(home, ".workbuddy", "binaries", "python", "versions");
            if (Directory.Exists(versionsDir))
            {
                string best = null;
                try
                {
                    foreach (var dir in Directory.GetDirectories(versionsDir))
                    {
                        var exe = Path.Combine(dir, Application.platform == RuntimePlatform.WindowsEditor
                            ? "python.exe"
                            : "python3");
                        if (!File.Exists(exe))
                        {
                            continue;
                        }

                        // 目录名带版本号，字典序最大的通常是最新版
                        if (best == null || string.CompareOrdinal(exe, best) > 0)
                        {
                            best = exe;
                        }
                    }
                }
                catch (Exception)
                {
                    // 扫不动就用 PATH 兜底
                }

                if (!string.IsNullOrEmpty(best))
                {
                    return best;
                }
            }

            return Application.platform == RuntimePlatform.WindowsEditor ? "python" : "python3";
        }

        private static JsonValue LoadJson(string path)
        {
            try
            {
                var parsed = JsonValue.Parse(File.ReadAllText(path, Encoding.UTF8));
                if (parsed != null && parsed.IsObject)
                {
                    return parsed;
                }
            }
            catch (Exception e)
            {
                CommandLog.Warning("现有 MCP 配置无法解析，将重建：" + e.Message);
            }

            return JsonValue.NewObject();
        }

        private static void Backup(string path)
        {
            try
            {
                var backup = path + ".bak." + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(path, backup, true);
                CommandLog.Info("已备份原文件到 " + backup);
            }
            catch (Exception e)
            {
                CommandLog.Warning("备份失败（" + path + "）：" + e.Message);
            }
        }

        #endregion
    }
}
