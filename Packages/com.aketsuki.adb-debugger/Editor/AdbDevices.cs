using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Aketsuki.AdbDebugger
{
    /// <summary>
    /// 一台 Android 设备或模拟器（来自 <c>adb devices -l</c> 的一行）。
    /// <para>
    /// <b>public 的原因：</b>「Unity MCP 指令系统」里的 <c>adb.devices</c> 指令要把这个列表
    /// 转成 JSON 交给 AI。指令系统是另一个程序集，反射门槛已经够低，不必再让它去拼
    /// NonPublic 的 BindingFlags。
    /// </para>
    /// </summary>
    public sealed class AdbDevice
    {
        public string Serial = string.Empty;
        public string State = "device";
        public string Model = string.Empty;
        public string Product = string.Empty;
        public string TransportId = string.Empty;

        /// <summary>能不能抓 logcat。只有 <c>device</c> 算——<c>unauthorized</c>/<c>offline</c> 时 adb 会直接拒。</summary>
        public bool Usable
        {
            get { return string.Equals(State, "device", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>下拉里显示的名字：<c>型号（序列号）</c>；没有型号时只显示序列号</summary>
        public string DisplayName
        {
            get { return string.IsNullOrEmpty(Model) ? Serial : Model + "（" + Serial + "）"; }
        }

        /// <summary>状态的中文解释（含怎么修）</summary>
        public string StateHint
        {
            get { return DescribeState(State); }
        }

        public static string DescribeState(string state)
        {
            var value = (state ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return "状态未知";
            }

            switch (value.ToLowerInvariant())
            {
                case "device":
                    return "已连接";

                case "unauthorized":
                    // 插上后不点"允许"是最常见的一种"我明明插了但没日志"
                    return "未授权：在设备屏幕上点「允许 USB 调试」，然后点「刷新」";

                case "offline":
                    return "离线：重新插拔数据线，或执行 adb reconnect";

                case "connecting":
                    return "连接中，等一两秒再刷新";

                case "bootloader":
                case "recovery":
                case "sideload":
                case "rescue":
                    return "当前是 " + value + " 模式，不能抓 logcat，先正常启动系统";

                default:
                    if (value.StartsWith("no permission", StringComparison.OrdinalIgnoreCase))
                    {
                        return "没有权限：Linux 要配 udev 规则，Windows 换个 USB 口或重装驱动";
                    }

                    return value;
            }
        }
    }

    /// <summary>
    /// 设备检索：跑 <c>adb devices -l</c> 并把结果缓存起来给窗口用。
    ///
    /// <para>
    /// <b>为什么要缓存而不是每次现扫：</b>扫一次要起一个进程（adb server 没起来时第一次还要 1 秒以上），
    /// 而 <c>OnGUI</c> 会在一秒内被调用好几次。现扫的结果是编辑器边拖窗口边卡。
    /// 所以「扫」是显式动作（窗口打开 / 拿回焦点 / 点刷新 / 列表为空时每 5 秒补一次），
    /// 读的是缓存。
    /// </para>
    ///
    /// <para>
    /// <b>线程约束：</b><see cref="RequestScan"/> 与 <see cref="ScanNow"/> 都必须在主线程调用——
    /// 它们要读 <see cref="AdbDebuggerSettings.AdbPath"/>，而 EditorPrefs 是主线程专用 API。
    /// 定位到 adb 之后，**跑进程**那一步才挪到后台线程（<see cref="RequestScan"/>），
    /// 这样窗口不会因为 adb server 冷启动而顿一下。
    /// </para>
    /// </summary>
    public static class AdbDevices
    {
        private const int ScanTimeoutMs = 8000;

        private static readonly object Gate = new object();
        private static readonly List<AdbDevice> Cached = new List<AdbDevice>();
        private static string _error = string.Empty;
        private static string _adb = string.Empty;
        private static string _adbSource = string.Empty;
        private static DateTime _scannedAt = DateTime.MinValue;

        private static int _scanning;
        private static int _version;

        /// <summary>正在后台扫</summary>
        public static bool IsScanning
        {
            get { return Volatile.Read(ref _scanning) != 0; }
        }

        /// <summary>每完成一次扫描自增；UI 靠它判断"该重绘了"（后台线程不能直接 Repaint）</summary>
        public static int Version
        {
            get { return Volatile.Read(ref _version); }
        }

        /// <summary>最近一次结果（快照，避免 UI 遍历时被后台线程改）</summary>
        public static IList<AdbDevice> Snapshot()
        {
            lock (Gate)
            {
                return Cached.ToArray();
            }
        }

        /// <summary>最近一次扫描的失败原因（找不到 adb、adb 卡住等）；正常为空</summary>
        public static string LastError
        {
            get
            {
                lock (Gate)
                {
                    return _error;
                }
            }
        }

        public static string AdbPath
        {
            get
            {
                lock (Gate)
                {
                    return _adb;
                }
            }
        }

        public static string AdbSource
        {
            get
            {
                lock (Gate)
                {
                    return _adbSource;
                }
            }
        }

        public static DateTime ScannedAt
        {
            get
            {
                lock (Gate)
                {
                    return _scannedAt;
                }
            }
        }

        /// <summary>后台扫一次。已在扫就忽略（主线程调用）。</summary>
        public static void RequestScan()
        {
            if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0)
            {
                return;
            }

            string source;
            var adb = AdbLocator.Resolve(out source); // EditorPrefs 只能主线程读，先在主线程定好路径

            var thread = new Thread(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(adb))
                    {
                        Store(null, string.Empty, source, MissingAdbMessage(source));
                    }
                    else
                    {
                        string error;
                        var devices = Run(adb, out error);
                        Store(devices, adb, source, error);
                    }
                }
                catch (Exception exception)
                {
                    Store(null, adb, source, "扫描设备失败：" + exception.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref _scanning, 0);
                    Interlocked.Increment(ref _version);
                }
            })
            {
                IsBackground = true,
                Name = "AdbDebugger.DeviceScan"
            };

            thread.Start();
        }

        /// <summary>同步扫一次（HTTP 路由用，主线程）。设备多时也不会超过 <see cref="ScanTimeoutMs"/>。</summary>
        public static List<AdbDevice> ScanNow(out string error)
        {
            string source;
            var adb = AdbLocator.Resolve(out source);
            if (string.IsNullOrEmpty(adb))
            {
                error = MissingAdbMessage(source);
                Store(null, string.Empty, source, error);
                Interlocked.Increment(ref _version);
                return new List<AdbDevice>();
            }

            var devices = Run(adb, out error);
            Store(devices, adb, source, error);
            Interlocked.Increment(ref _version);
            return devices;
        }

        // ---------------------------------------------------------------- 网络设备（模拟器）

        /// <summary>
        /// <c>adb connect</c> 的上限。连不通的端口通常 1–3 秒就拒，8 秒是给卡死的 adb server 留的。
        /// </summary>
        private const int ConnectTimeoutMs = 8000;

        private static int _connecting;
        private static bool _connectFinished;
        private static bool _connectSucceeded;
        private static string _connectTarget = string.Empty;
        private static string _connectMessage = string.Empty;

        /// <summary>正在后台执行 <c>adb connect</c></summary>
        public static bool IsConnecting
        {
            get { return Volatile.Read(ref _connecting) != 0; }
        }

        /// <summary>
        /// 后台连一台网络设备（模拟器）。主线程调用，立刻返回。
        /// <para>
        /// <b>为什么必须异步：</b><c>adb connect</c> 打到一个没开的端口时要等 TCP 超时才返，
        /// 最坏几秒；在 <c>OnGUI</c> 里同步跑会把窗口冻住，还会连带卡住整个编辑器的主线程泵。
        /// </para>
        /// <para>
        /// 结果不在这里回调——后台线程碰不了 EditorPrefs / Repaint。UI 每帧用
        /// <see cref="TryTakeConnectResult"/> 取。
        /// </para>
        /// </summary>
        public static void RequestConnect(string hostPort)
        {
            var target = (hostPort ?? string.Empty).Trim();
            if (target.Length == 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _connecting, 1, 0) != 0)
            {
                return;
            }

            lock (Gate)
            {
                _connectTarget = target;
                _connectMessage = string.Empty;
                _connectSucceeded = false;
                _connectFinished = false;
            }

            string source;
            var adb = AdbLocator.Resolve(out source); // EditorPrefs 只能主线程读，先在主线程定好路径

            var thread = new Thread(() =>
            {
                var ok = false;
                var message = string.Empty;
                try
                {
                    ok = ConnectCore(adb, source, target, out message);
                }
                catch (Exception exception)
                {
                    message = "连接 " + target + " 失败：" + exception.Message;
                }

                lock (Gate)
                {
                    _connectSucceeded = ok;
                    _connectMessage = message;
                    _connectFinished = true;
                }

                Interlocked.Exchange(ref _connecting, 0);
                Interlocked.Increment(ref _version);
            })
            {
                IsBackground = true,
                Name = "AdbDebugger.Connect"
            };

            thread.Start();
        }

        /// <summary>取走一次连接结果（取走即清）。没有新结果时返回 false。</summary>
        public static bool TryTakeConnectResult(out bool ok, out string target, out string message)
        {
            lock (Gate)
            {
                ok = _connectSucceeded;
                target = _connectTarget;
                message = _connectMessage;

                if (!_connectFinished)
                {
                    return false;
                }

                _connectFinished = false;
                return true;
            }
        }

        /// <summary>跑 <c>adb connect host:port</c>，按 adb 的自然语言输出判定成败</summary>
        private static bool ConnectCore(string adb, string source, string target, out string message)
        {
            if (string.IsNullOrEmpty(adb))
            {
                message = MissingAdbMessage(source);
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = adb,
                Arguments = "connect " + target,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                var stderr = new StringBuilder();
                process.ErrorDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                    {
                        stderr.AppendLine(args.Data);
                    }
                };

                try
                {
                    process.Start();
                    process.BeginErrorReadLine();
                }
                catch (Exception exception)
                {
                    message = "执行 adb connect 失败：" + exception.Message + "（adb=" + adb + "）";
                    return false;
                }

                var output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(ConnectTimeoutMs))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception)
                    {
                        // 进程可能刚退，杀失败无所谓
                    }

                    message = "adb connect " + (ConnectTimeoutMs / 1000) +
                              " 秒没返回（adb server 可能卡住，试试 adb kill-server）";
                    return false;
                }

                var text = string.Empty;
                try
                {
                    if (output.Wait(2000))
                    {
                        text = output.Result;
                    }
                }
                catch (Exception)
                {
                    // 读流失败时只靠 stderr 判定
                }

                var combined = (text + "\n" + stderr).Trim();
                var lower = combined.ToLowerInvariant();

                // adb 的成败只有自然语言输出，没有靠得住的退出码；
                // 中文系统的报错是「由于目标计算机积极拒绝」，所以两种都要认。
                if (lower.Contains("cannot connect") ||
                    lower.Contains("failed to connect") ||
                    lower.Contains("unable to connect") ||
                    combined.Contains("无法连接") ||
                    combined.Contains("积极拒绝"))
                {
                    message = "连不上 " + target + "：" +
                              (combined.Length > 0 ? combined : "adb 没有说明原因") +
                              "\n常见原因：模拟器没启动 / 端口填错 / adb 版本与模拟器自带的不一致（换成模拟器目录下的 adb.exe）";
                    return false;
                }

                if (lower.Contains("connected to"))
                {
                    message = lower.Contains("already connected") ? "已连过 " + target : "已连接 " + target;
                    return true;
                }

                message = combined.Length > 0
                    ? "adb 返回：" + combined
                    : "adb 没有返回任何结果（" + target + "）";
                return false;
            }
        }

        /// <summary>一行摘要，例：<c>2 台设备（1 台可用）</c></summary>
        public static string Summary()
        {
            var devices = Snapshot();
            if (devices.Count == 0)
            {
                return "未发现设备";
            }

            var usable = 0;
            for (var i = 0; i < devices.Count; i++)
            {
                if (devices[i].Usable)
                {
                    usable++;
                }
            }

            return devices.Count + " 台设备（" + usable + " 台可用）";
        }

        /// <summary>
        /// 把一次扫描结果拼成 <c>/v1/devices</c> 的 JSON。
        ///
        /// <para>
        /// 放在这里而不是 HTTP 服务里，是为了能离线跑（服务端那层依赖 <c>HttpListener</c>，没法单测）。
        /// 逻辑一样：真实的解析 + 拼接，不用 mock。
        /// </para>
        /// </summary>
        public static string Json(List<AdbDevice> devices, string serial, string error)
        {
            var selected = (serial ?? string.Empty).Trim();
            var items = new StringBuilder();
            var usable = 0;

            for (var i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                if (i > 0)
                {
                    items.Append(',');
                }

                if (device.Usable)
                {
                    usable++;
                }

                items.Append(new JsonOut()
                    .Str("serial", device.Serial)
                    .Str("state", device.State)
                    .Bool("usable", device.Usable)
                    .Str("model", device.Model)
                    .Str("product", device.Product)
                    .Bool("selected", selected.Length > 0
                                      && string.Equals(device.Serial, selected, StringComparison.OrdinalIgnoreCase))
                    .Str("hint", device.StateHint)
                    .ToString());
            }

            return new JsonOut()
                .Bool("ok", true)
                .Str("adb", AdbPath)
                .Str("adbSource", AdbSource)
                .Str("serial", selected)
                .Bool("auto", selected.Length == 0)
                .Num("count", devices.Count)
                .Num("usableCount", usable)
                .Raw("devices", "[" + items + "]")
                .Str("error", error ?? string.Empty)
                .ToString();
        }

        private static string MissingAdbMessage(string source)
        {
            return "找不到 adb：" + source +
                   "。在下面手填 adb.exe 的完整路径（通常在 <Android SDK>/platform-tools/adb.exe）后点「刷新」";
        }

        private static void Store(List<AdbDevice> devices, string adb, string source, string error)
        {
            lock (Gate)
            {
                Cached.Clear();
                if (devices != null)
                {
                    Cached.AddRange(devices);
                }

                if (adb != null)
                {
                    _adb = adb;
                }

                if (source != null)
                {
                    _adbSource = source;
                }

                _error = error ?? string.Empty;
                _scannedAt = DateTime.Now;
            }
        }

        /// <summary>跑 <c>adb devices -l</c> 并解析</summary>
        private static List<AdbDevice> Run(string adb, out string error)
        {
            error = string.Empty;

            var startInfo = new ProcessStartInfo
            {
                FileName = adb,
                Arguments = "devices -l",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                var stderr = new StringBuilder();
                process.ErrorDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                    {
                        stderr.AppendLine(args.Data);
                    }
                };

                try
                {
                    process.Start();
                    process.BeginErrorReadLine();
                }
                catch (Exception exception)
                {
                    error = "执行 adb devices 失败：" + exception.Message + "（adb=" + adb + "）";
                    return new List<AdbDevice>();
                }

                // 两个顺序都不能用：同步 ReadToEnd 放在 WaitForExit 之前会死锁（子进程写满管道就不动），
                // 放在之后又会在进程没退出时读不到。异步读 + 限时等退出才对。
                var output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(ScanTimeoutMs))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception)
                    {
                        // 进程可能刚退，杀失败无所谓
                    }

                    error = "adb devices " + (ScanTimeoutMs / 1000) +
                            " 秒没返回（adb server 可能卡住，试试 adb kill-server）";
                    return new List<AdbDevice>();
                }

                var text = string.Empty;
                try
                {
                    if (output.Wait(2000))
                    {
                        text = output.Result;
                    }
                }
                catch (Exception)
                {
                    // 读流失败时退回"没设备"，下面会用 stderr 说明原因
                }

                var devices = Parse(text);
                if (devices.Count == 0)
                {
                    var message = stderr.ToString().Trim();
                    if (message.Length > 0 && !OnlyNoise(message))
                    {
                        error = message;
                    }
                }

                return devices;
            }
        }

        /// <summary>adb 启动 server 时打在 stderr 的提示不是错误（如 <c>* daemon not running; starting now *</c>）</summary>
        private static bool OnlyNoise(string stderr)
        {
            var lines = stderr.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.IndexOf("daemon", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                return false;
            }

            return true;
        }

        /// <summary>
        /// 解析 <c>adb devices -l</c>。形如：
        /// <code>
        /// List of devices attached
        /// 2LQ0224A15000813       device product:HUAWEIALN-AL00 model:ALN-AL00 device:HWALN transport_id:1
        /// emulator-5554          offline
        /// </code>
        /// </summary>
        public static List<AdbDevice> Parse(string text)
        {
            var list = new List<AdbDevice>();
            if (string.IsNullOrEmpty(text))
            {
                return list;
            }

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0
                    || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    continue;
                }

                var device = new AdbDevice { Serial = parts[0] };
                var state = parts[1];
                var index = 2;

                // 老 adb 在 Linux 上会输出 "no permissions (user in plugdev group...)"，状态本身带空格
                if (string.Equals(state, "no", StringComparison.OrdinalIgnoreCase)
                    && parts.Length > 2
                    && parts[2].StartsWith("permission", StringComparison.OrdinalIgnoreCase))
                {
                    var builder = new StringBuilder(parts[1]);
                    while (index < parts.Length && parts[index].IndexOf(':') < 0)
                    {
                        builder.Append(' ').Append(parts[index]);
                        index++;
                    }

                    state = builder.ToString();
                }

                // 这条在 Linux 上会拖一句 "see [http://...]"，原样塞进下拉会长得没法看。
                // 只要前缀够判断就行，具体原因在 StateHint 里给。
                if (state.StartsWith("no permission", StringComparison.OrdinalIgnoreCase))
                {
                    state = "no permissions";
                }

                device.State = state;

                for (var i = index; i < parts.Length; i++)
                {
                    var token = parts[i];
                    var colon = token.IndexOf(':');
                    if (colon <= 0)
                    {
                        continue;
                    }

                    var key = token.Substring(0, colon).ToLowerInvariant();
                    var value = token.Substring(colon + 1);

                    switch (key)
                    {
                        case "model":
                            device.Model = value;
                            break;

                        case "product":
                            device.Product = value;
                            break;

                        case "transport_id":
                            device.TransportId = value;
                            break;
                    }
                }

                list.Add(device);
            }

            return list;
        }
    }
}
