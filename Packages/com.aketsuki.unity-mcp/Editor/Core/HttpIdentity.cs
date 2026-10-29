using System;
using System.Diagnostics;
using System.IO;

namespace Aketsuki.UnityMcp
{
    /// <summary>
    /// HTTP 指令服务实例的"身份自证"信息
    /// <para>
    /// 存在的理由：同一个工程目录里可能同时跑着多个 Unity 进程，它们都会加载同一套 Editor 程序集。
    /// 典型的是资源导入工作进程 <c>AssetImportWorker0/1</c>——由编辑器以
    /// <c>-adb2 -batchMode -name AssetImportWorkerN -projectPath &lt;同一工程&gt;</c> 拉起，
    /// 同样会执行 <c>[InitializeOnLoad]</c>，于是也会起一个监听、也会往同一个 <c>.http-info</c> 写端口。
    /// 而 batchmode 进程不跑 <c>EditorApplication.update</c>，指令进了它的队列就永远不会被消费：
    /// 客户端 <c>ping</c> 得通、<c>status</c> 也正常，唯独指令石沉大海——与"编辑器卡住"的表象完全一致。
    /// </para>
    /// <para>
    /// 因此服务端必须能回答"我是谁"，客户端才能判断"我连的是不是能执行指令的那个"。
    /// 判定只依据命令行，不依赖任何 Unity API：既能在静态构造的最早期调用，也能在离线测试里直接构造。
    /// </para>
    /// </summary>
    public sealed class HttpIdentity
    {
        /// <summary>交互式编辑器：能跑主循环、能真正执行指令的那个</summary>
        public const string RoleEditor = "editor";

        /// <summary>资源导入工作进程：会监听但不会执行指令</summary>
        public const string RoleAssetImportWorker = "assetImportWorker";

        /// <summary>其它 batchmode 进程（命令行构建等）：同样不会执行指令</summary>
        public const string RoleBatchMode = "batchMode";

        /// <summary>命令行里读不出足够信息时的兜底</summary>
        public const string RoleUnknown = "unknown";

        private const string ArgBatchMode = "-batchmode";
        private const string ArgName = "-name";
        private const string ArgProjectPath = "-projectpath";
        private const string WorkerNamePrefix = "assetimportworker";

        /// <summary>进程号；客户端据此判断"信息文件里那个进程还是不是它"</summary>
        public int Pid { get; set; }

        /// <summary>进程名，通常就是 Unity</summary>
        public string ProcessName { get; set; } = string.Empty;

        /// <summary>
        /// 是否是交互式进程（命令行不含 <c>-batchMode</c>）
        /// <para>只有为 true 的进程才会驱动 <c>EditorApplication.update</c>，才可能真的执行指令。</para>
        /// </summary>
        public bool Interactive { get; set; }

        /// <summary>角色：<see cref="RoleEditor"/> / <see cref="RoleAssetImportWorker"/> / <see cref="RoleBatchMode"/> / <see cref="RoleUnknown"/></summary>
        public string Role { get; set; } = RoleUnknown;

        /// <summary>工程根路径（取自命令行 <c>-projectPath</c>）；用于确认连的是不是目标工程</summary>
        public string ProjectPath { get; set; } = string.Empty;

        /// <summary>工程目录名，打印诊断信息时比全路径更省地方</summary>
        public string ProjectName { get; set; } = string.Empty;

        /// <summary>服务启动时刻（UTC，ISO8601）</summary>
        public string StartedUtc { get; set; } = string.Empty;

        /// <summary>本进程是否是"能执行指令的编辑器本体"</summary>
        public bool IsExecutableEditor
        {
            get { return Interactive && string.Equals(Role, RoleEditor, StringComparison.Ordinal); }
        }

        /// <summary>按当前进程的命令行构建身份</summary>
        public static HttpIdentity Build()
        {
            return Build(Environment.GetCommandLineArgs());
        }

        /// <summary>
        /// 按给定命令行构建身份
        /// <para>单独暴露出来是为了可测：离线工程里直接喂一段命令行就能验证判定结果，不必真起 Unity。</para>
        /// </summary>
        public static HttpIdentity Build(string[] argv)
        {
            var identity = new HttpIdentity
            {
                Pid = CurrentPid(),
                ProcessName = CurrentProcessName(),
                StartedUtc = DateTime.UtcNow.ToString("o")
            };

            var batchMode = false;
            var isWorker = false;
            var projectPath = string.Empty;

            if (argv != null)
            {
                for (var i = 0; i < argv.Length; i++)
                {
                    var arg = (argv[i] ?? string.Empty).Trim();
                    if (arg.Length == 0)
                    {
                        continue;
                    }

                    if (string.Equals(arg, ArgBatchMode, StringComparison.OrdinalIgnoreCase))
                    {
                        batchMode = true;
                        continue;
                    }

                    // -name AssetImportWorker0（也可能是 -name=AssetImportWorker0）
                    var name = ValueOf(argv, ref i, ArgName);
                    if (name != null
                        && name.Trim().ToLowerInvariant().StartsWith(WorkerNamePrefix, StringComparison.Ordinal))
                    {
                        isWorker = true;
                        continue;
                    }

                    // -projectPath <路径>（也可能是 -projectPath=<路径>）
                    var project = ValueOf(argv, ref i, ArgProjectPath);
                    if (project != null)
                    {
                        projectPath = project.Trim().Trim('"');
                    }
                }
            }

            identity.Interactive = !batchMode;
            if (isWorker)
            {
                identity.Role = RoleAssetImportWorker;
            }
            else if (batchMode)
            {
                identity.Role = RoleBatchMode;
            }
            else
            {
                identity.Role = RoleEditor;
            }

            identity.ProjectPath = projectPath ?? string.Empty;
            identity.ProjectName = DeriveProjectName(identity.ProjectPath);
            return identity;
        }

        /// <summary>
        /// 取 <c>-xxx value</c> 或 <c>-xxx=value</c> 形式的值；不是该参数时返回 null
        /// <para>命中"空格分隔"写法时会把 <paramref name="i"/> 推进到值的下标，主循环因此不会把值当参数再判一遍。</para>
        /// </summary>
        private static string ValueOf(string[] argv, ref int i, string lowerName)
        {
            var arg = (argv[i] ?? string.Empty).Trim();

            var eq = arg.IndexOf('=');
            if (eq > 0)
            {
                var head = arg.Substring(0, eq);
                if (string.Equals(head, lowerName, StringComparison.OrdinalIgnoreCase))
                {
                    return arg.Substring(eq + 1);
                }

                return null;
            }

            if (!string.Equals(arg, lowerName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (i + 1 >= argv.Length)
            {
                return null;
            }

            i++;
            return argv[i];
        }

        private static string DeriveProjectName(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
            {
                return string.Empty;
            }

            var trimmed = projectPath.Trim().TrimEnd('\\', '/');
            var index = trimmed.LastIndexOfAny(new[] { '\\', '/' });
            return index >= 0 ? trimmed.Substring(index + 1) : trimmed;
        }

        private static int CurrentPid()
        {
            try
            {
                return Process.GetCurrentProcess().Id;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static string CurrentProcessName()
        {
            try
            {
                return Process.GetCurrentProcess().ProcessName;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>某个进程号是否还活着（用于判断信息文件里的 pid 是否仍然有效）</summary>
        public static bool IsProcessAlive(int pid)
        {
            if (pid <= 0)
            {
                return false;
            }

            try
            {
                var process = Process.GetProcessById(pid);
                if (process == null)
                {
                    return false;
                }

                var exited = process.HasExited;
                process.Dispose();
                return !exited;
            }
            catch (Exception)
            {
                // 取不到就是查不了，按"不在了"处理：宁可重写信息文件，也不要守着一份失效的
                return false;
            }
        }

        /// <summary>序列化成 JSON（写进 <c>.http-info</c>，也随 <c>/v1/status</c> 返回）</summary>
        public JsonValue ToJson()
        {
            var json = JsonValue.NewObject();
            json.Set("pid", JsonValue.Of(Pid));
            json.Set("processName", JsonValue.Of(ProcessName ?? string.Empty));
            json.Set("interactive", JsonValue.Of(Interactive));
            json.Set("role", JsonValue.Of(Role ?? RoleUnknown));
            json.Set("executable", JsonValue.Of(IsExecutableEditor));
            json.Set("projectPath", JsonValue.Of(ProjectPath ?? string.Empty));
            json.Set("projectName", JsonValue.Of(ProjectName ?? string.Empty));
            json.Set("startedUtc", JsonValue.Of(StartedUtc ?? string.Empty));
            return json;
        }

        /// <summary>从 JSON 读回；结构不对时返回 null</summary>
        public static HttpIdentity FromJson(JsonValue json)
        {
            if (json == null || !json.IsObject)
            {
                return null;
            }

            return new HttpIdentity
            {
                Pid = json.GetInt("pid", 0),
                ProcessName = json.GetString("processName", string.Empty),
                Interactive = json.GetBool("interactive", false),
                Role = json.GetString("role", RoleUnknown),
                ProjectPath = json.GetString("projectPath", string.Empty),
                ProjectName = json.GetString("projectName", string.Empty),
                StartedUtc = json.GetString("startedUtc", string.Empty)
            };
        }

        /// <summary>一行摘要，用于日志与诊断输出</summary>
        public override string ToString()
        {
            return string.Format("pid={0} role={1} interactive={2} project={3}",
                Pid, Role, Interactive ? "yes" : "no",
                string.IsNullOrEmpty(ProjectName) ? "?" : ProjectName);
        }
    }
}
