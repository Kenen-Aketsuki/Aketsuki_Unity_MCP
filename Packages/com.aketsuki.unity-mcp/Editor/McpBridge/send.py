#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Unity 指令系统 HTTP 客户端。

向 Unity 编辑器里运行的 CommandSystem HTTP 服务下发 JSON 指令，并把执行报告
读成人能看懂的样子。只依赖 Python 标准库。

用法概览：
    python send.py resolve                    # 诊断：列出所有候选端点，说明最终会连哪个
    python send.py status                     # 探活 + 看编辑器状态（含当前指令与耗时）
    python send.py commands                   # 列出全部可用指令（带参数说明）
    python send.py send <文件>.json            # 下发并同步等执行报告
    python send.py send <文件>.json --dry-run  # 只校验不真执行
    python send.py send -                     # 从标准输入读指令 JSON
    python send.py send <文件>.json --request-id <ID>   # 幂等重放：同一 ID 不二次执行
    python send.py cancel                     # 请求协作式取消当前批次
    python send.py compile                    # 编译并回传错误（改完 .cs 后的闭环）
    python send.py seq <步骤>.json             # 顺序执行多步，步间由客户端等待（跨帧节奏）

定位正确的端点（本客户端最重要的一件事）：
    同一个工程目录里可能同时跑着多个 Unity 进程，它们都会加载同一套 Editor 程序集并
    各自起一个监听（典型的是资源导入工作进程 AssetImportWorkerN）。这些进程 ping 得通、
    status 也正常，但不跑 EditorApplication.update，指令进了队列就永远不会被消费。
    因此客户端在发指令之前会先问一句「你是谁」，只往自证为交互式编辑器的那个端口发，
    并优先选 projectPath 属于本工作区的（同机多工程时避免连错）。
    判定完全依赖服务端自报的身份（/v1/status 的 identity 块），不查进程、不猜端口。

退出码：
    0  HTTP 200 且 ok=true（全部指令成功）
    1  HTTP 200 但 ok=false（有指令失败；seq 时表示某一步失败或后面有步没执行）
    2  连不上服务（未启动、信息文件缺失、或找不到任何可达端点）
    3  协议错误（400/401/404/405/413）
    4  编辑器忙碌（503）
    5  找到的唯一端点不是交互式编辑器（指令不会被执行）；确认要发就加 --force
"""

import argparse
import glob
import hashlib
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

TOKEN_HEADER = "X-Cmd-Token"
CONTENT_TYPE = "application/json; charset=utf-8"
DEFAULT_TIMEOUT = 180
MAX_BUSY_WAIT = 600

# 探测候选端点用的短超时：连不上要快速放弃，别让一次 status 拖住整条命令
PROBE_TIMEOUT = 3.0

# /v1/status 响应里的身份块字段名（与服务端 HttpIdentityFields.Key 一致）
IDENTITY_KEY = "identity"

HERE = os.path.dirname(os.path.abspath(__file__))


def find_workspace_root(start):
    """从脚本位置向上查找包含 .workbuddy 的目录，其上层即为工作区根。"""
    cur = os.path.abspath(start)
    while True:
        if os.path.isdir(os.path.join(cur, ".workbuddy")):
            return cur
        parent = os.path.dirname(cur)
        if parent == cur:
            return None
        cur = parent


def workspace_root_for(project_root):
    """由 Unity 工程根推出工作区根。

    必须与服务端 `CommandPathUtil.WorkspaceRoot` 完全一致：
    "工程根的父目录若含 .workbuddy 则取父目录，否则退化为工程根本身"。
    两边算错一个，就会去找另一个目录下的信息文件，表现为"怎么都连不上"。
    """
    root = os.path.abspath(project_root)
    parent = os.path.dirname(root)
    if parent and parent != root and os.path.isdir(os.path.join(parent, ".workbuddy")):
        return parent
    return root


# 这三个由 configure() 填充：既可自动发现（脚本在 .workbuddy 里），
# 也可由调用方显式指定 Unity 工程根（MCP 桥从别的目录拉起时走这条路）。
WS_ROOT = None
# 调用方显式指定的 Unity 工程根（--project-root / 桥的 --project-root）。
# 非空即进入"严格选址"：只认这个工程的编辑器，连不上就报错，绝不换目标。
EXPECTED_PROJECT = None
COMMAND_DIR = None

# 信息文件名：新版是 .http-info.<projectHash>（多工程共用一个工作区时互不覆盖），
# 旧版是不带 hash 的 .http-info。客户端两种都认。
INFO_PREFIX = ".http-info"
DEFAULT_INFO = None


def configure(project_root=None):
    """重新计算路径常量。

    :param project_root: Unity 工程根（含 Assets/ 与 ProjectSettings/ 的那一层）。
                         为空时从脚本位置自动发现，适用于脚本部署在 .workbuddy 下的常规情况。
    """
    global WS_ROOT, COMMAND_DIR, DEFAULT_INFO, EXPECTED_PROJECT

    if project_root:
        EXPECTED_PROJECT = os.path.normcase(os.path.abspath(project_root))
        WS_ROOT = workspace_root_for(project_root)
    else:
        EXPECTED_PROJECT = None
        WS_ROOT = find_workspace_root(HERE) or os.path.abspath(
            os.path.join(HERE, "..", "..", "..", ".."))

    COMMAND_DIR = os.path.join(WS_ROOT, ".workbuddy", "UnityEditCommand")
    DEFAULT_INFO = os.path.join(COMMAND_DIR, INFO_PREFIX)
    return WS_ROOT


configure()

KNOWN_PATHS = ("v1/ping", "v1/status", "v1/commands", "v1/execute", "v1/cancel")


# ---------------------------------------------------------------- 基础请求


# 明确禁用系统代理：本机回环流量若走代理，未监听端口会返回 502，
# 把"连不上"误报成"协议错误"（探测阶段尤其致命）。
_OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


class Client(object):
    def __init__(self, base_url, token, timeout):
        self.base_url = base_url.rstrip("/") + "/"
        self.token = token
        self.timeout = timeout

    def call(self, path, method="GET", body=None):
        """返回 (status, payload_dict_or_None, raw_text)。status 为 None 表示连接失败。"""
        url = self.base_url + path.lstrip("/")
        data = None
        if body is not None:
            data = body.encode("utf-8")

        request = urllib.request.Request(url, data=data, method=method)
        request.add_header("Content-Type", CONTENT_TYPE)
        if self.token:
            request.add_header(TOKEN_HEADER, self.token)

        try:
            with _OPENER.open(request, timeout=self.timeout) as response:
                raw = response.read().decode("utf-8")
                return response.status, parse_json(raw), raw
        except urllib.error.HTTPError as error:
            raw = error.read().decode("utf-8", "replace")
            return error.code, parse_json(raw), raw
        except urllib.error.URLError as error:
            reason = getattr(error, "reason", error)
            return None, None, "无法连接 %s：%s" % (url, reason)
        except Exception as error:  # 超时等
            return None, None, "请求 %s 失败：%s" % (url, error)


def parse_json(text):
    try:
        return json.loads(text)
    except Exception:
        return None


# ---------------------------------------------------------------- 信息文件


def load_info(path):
    """读取服务端写下的 .http-info；不存在则返回 None。"""
    if not os.path.isfile(path):
        return None
    try:
        # 服务端用 C# Encoding.UTF8 写文件会带 BOM，这里用 utf-8-sig 容错
        with open(path, "r", encoding="utf-8-sig") as handle:
            return json.load(handle)
    except Exception as error:
        sys.stderr.write("信息文件读取失败：%s\n" % error)
        return None


def normalize_url(value):
    """把各种写法（带不带 scheme、带不带尾斜杠）归一成 http://127.0.0.1:57831/ 。"""
    if not value:
        return None

    text = str(value).strip()
    if not text:
        return None
    if "://" not in text:
        text = "http://" + text
    if not text.endswith("/"):
        text += "/"
    return text


def list_info_files():
    """枚举命令目录下所有信息文件。

    覆盖三种命名：
      .http-info                    旧版主文件（过渡期兼容）
      .http-info.<hash>             新版主文件（每工程一个）
      .http-info.<hash>.<pid>       各实例的自述副本
    """
    try:
        return sorted(glob.glob(os.path.join(COMMAND_DIR, INFO_PREFIX + "*")))
    except OSError:
        return []


def find_unity_project_roots(root):
    """在工作区根下找 Unity 工程目录（含 ProjectSettings/ProjectVersion.txt）。"""
    candidates = [root]
    try:
        for name in sorted(os.listdir(root)):
            path = os.path.join(root, name)
            if os.path.isdir(path):
                candidates.append(path)
    except OSError:
        pass

    return [
        path for path in candidates
        if os.path.isfile(os.path.join(path, "ProjectSettings", "ProjectVersion.txt"))
    ]


def project_hash(project_root):
    """与服务端 ComputeProjectHash 保持一致：规范化 + 分隔符统一为 / + 小写 + SHA1 前 4 字节。

    任何一端改了算法，两端就互相找不到文件——改这里必须同步改
    CommandHttpServer.ComputeProjectHash。
    """
    normalized = os.path.normpath(os.path.abspath(project_root)).replace("\\", "/").rstrip("/").lower()
    digest = hashlib.sha1(normalized.encode("utf-8")).digest()
    return "".join("%02x" % byte for byte in digest[:4])


def load_primary_info(explicit_path=None):
    """定位主信息文件。

    优先级：
      1) --info 显式指定
      2) 按本工作区下的 Unity 工程算出 hash，精确命中 .http-info.<hash>
      3) 任意一个能读到端口的主信息文件（兼容旧版服务端 / 非标准目录布局）

    返回 (path_or_None, info_or_None)。
    """
    if explicit_path:
        return explicit_path, load_info(explicit_path)

    for root in find_unity_project_roots(WS_ROOT):
        path = os.path.join(COMMAND_DIR, INFO_PREFIX + "." + project_hash(root))
        data = load_info(path)
        if data:
            return path, data

    # 回退：旧版 .http-info 优先，其次任意带端口的文件
    legacy = load_info(DEFAULT_INFO)
    if legacy:
        return DEFAULT_INFO, legacy

    for path in list_info_files():
        data = load_info(path)
        if data and (data.get("url") or data.get("port")):
            return path, data

    return None, None


# ---------------------------------------------------------------- 跨工程全局索引
#
# `.http-info` 是按工作区分散的，MCP 桥只能看自己那个工作区。于是"每个工程配一条
# MCP server"成了必然，装 N 个工程就是 N 条 server × 每条几十个工具。
# 全局索引把"当前开着哪些 Unity"汇总到一个固定目录，一条 server 就能全域发现。
#
# 目录口径必须与 C# 的 GlobalTargetRegistry.DirectoryPath 完全一致：
#   Windows  %LOCALAPPDATA%\Aketsuki\UnityMcp\targets
#   macOS    ~/Library/Application Support/Aketsuki/UnityMcp/targets
#   Linux    $XDG_DATA_HOME/Aketsuki/UnityMcp/targets（默认 ~/.local/share）
# 任一端改算法，另一端就一个目标都找不到。

GLOBAL_TARGETS_SUBDIR = ("Aketsuki", "UnityMcp", "targets")


def global_targets_dir():
    """全局目标目录（与 .NET 的 SpecialFolder.LocalApplicationData 对齐）。"""
    if os.name == "nt":
        root = os.environ.get("LOCALAPPDATA")
        if not root:
            root = os.path.join(os.path.expanduser("~"), "AppData", "Local")
    elif sys.platform == "darwin":
        root = os.path.join(os.path.expanduser("~"), "Library", "Application Support")
    else:
        root = os.environ.get("XDG_DATA_HOME") or os.path.join(
            os.path.expanduser("~"), ".local", "share")
    return os.path.join(root, *GLOBAL_TARGETS_SUBDIR)


def list_global_targets():
    """枚举全局索引里的全部工程条目。

    返回 list[dict]：内容就是当初写下的 <c>.http-info</c>，额外补两个键
    <c>_path</c>（条目文件绝对路径）与 <c>_hash</c>（工程 hash）。
    这里<b>不做</b>存活过滤——判活必须 probe，光看文件时间会把"刚崩掉的编辑器"也当成活的。
    """
    items = []
    try:
        names = sorted(glob.glob(os.path.join(global_targets_dir(), "*.json")))
    except OSError:
        return items

    for path in names:
        data = load_info(path)
        if not isinstance(data, dict):
            continue
        if not (data.get("url") or data.get("port")):
            continue
        data["_path"] = path
        data["_hash"] = os.path.splitext(os.path.basename(path))[0]
        items.append(data)

    return items


def target_from_info_info(info, source, expected_project=None, timeout=PROBE_TIMEOUT):
    """由一个信息 dict 构造并探测 Target。纯函数，不读模块级全局状态。"""
    url = normalize_url(info.get("url") or info.get("port"))
    if not url:
        return None, "信息里没有可用地址"

    if expected_project is None:
        owner = (info.get(IDENTITY_KEY) or {}).get("projectPath")
        expected_project = owner or None

    target = Target(url, info.get("token") or "", source,
                    expected_project=expected_project, primary=True)
    probe(target, timeout=timeout)
    if not target.reachable:
        return None, target.error or "端点不可达"
    return target, None


def target_from_project_root(project_root, timeout=PROBE_TIMEOUT):
    """按工程根直接定位端点，返回 (target, info_path, error)。

    <b>刻意不调用 configure()</b>：那是进程级全局状态，桥要同时服务多个工程，
    各目标共享一份全局变量必然互相污染（A 工程 configure 完，B 工程的发现就串了）。
    这里只用纯函数把路径算出来，可以被任意多个目标复用、也可以并发调用。
    """
    root = os.path.normpath(os.path.abspath(project_root))
    ws_root = workspace_root_for(root)
    info_path = os.path.join(ws_root, ".workbuddy", "UnityEditCommand",
                             INFO_PREFIX + "." + project_hash(root))

    info = load_info(info_path)
    if not info:
        return None, info_path, (
            "找不到该工程的信息文件（%s）。\n"
            "  编辑器可能没开着，或 Tools / 指令系统 / HTTP 指令服务 处于关闭状态。" % info_path)

    target, error = target_from_info_info(info, "工程 " + os.path.basename(root),
                                          expected_project=root, timeout=timeout)
    if target is None:
        return None, info_path, "工程 %s 的端点不可用：%s" % (root, error)
    return target, info_path, None


def target_from_info_path(info_path, timeout=PROBE_TIMEOUT):
    """按 `.http-info*` 文件的绝对路径定位（用户最直观的指定方式）。"""
    info = load_info(info_path)
    if not info:
        return None, info_path, "信息文件不存在或无法解析：%s" % info_path
    target, error = target_from_info_info(info, "信息文件 " + os.path.basename(info_path),
                                          timeout=timeout)
    if target is None:
        return None, info_path, "%s 不可用：%s" % (info_path, error)
    return target, info_path, None


def find_project_root_by_name(name):
    """在全局索引里按工程名（或名字的一部分）找工程根。

    唯一命中才算数：多个工程同名的常见情形（两个 clone）必须由调用方显式区分，
    猜一个的代价是把指令发到别人工程里去。
    """
    key = (name or "").strip()
    if not key:
        return None, []

    lowered = key.lower()
    exact = []
    partial = []
    for item in list_global_targets():
        identity = item.get(IDENTITY_KEY) or {}
        project_name = (identity.get("projectName") or "").strip()
        project_path = (identity.get("projectPath") or "").strip()
        if not project_path:
            continue
        if project_name.lower() == lowered:
            exact.append(project_path)
        elif lowered in project_name.lower():
            partial.append(project_path)

    hits = exact or partial
    # 去重：同一工程可能有多个编辑器实例写了同一条
    unique = []
    seen = set()
    for path in hits:
        norm = os.path.normcase(os.path.normpath(path))
        if norm in seen:
            continue
        seen.add(norm)
        unique.append(path)

    if len(unique) == 1:
        return unique[0], unique
    return None, unique


def _is_unity_project(path):
    """目录是不是 Unity 工程根（有 Assets/ 或 ProjectSettings/ 就算）。"""
    return os.path.isdir(os.path.join(path, "ProjectSettings")) or \
        os.path.isdir(os.path.join(path, "Assets"))


def resolve_target_selector(value):
    """把 `--target` 的三种写法解析成 (project_root, info_path, error, candidates)。

    - **工程根路径**（目录）：`D:/Work/MyUnityProject`
    - **信息文件路径**：`…/.workbuddy/UnityEditCommand/.http-info.df0c4d80`
    - **工程名**（或名字的一部分）：`MyUnityProject`，走全局索引

    之所以要有这条通路：`.http-info` 是按**工作区**分散的，脚本只认自己所在的工作区。
    在 A 工程目录里跑 `send.py status` 永远看不到 B 工程的编辑器，报出来的是
    「Unity 编辑器可能没开着」——这是假阴性，会把人带偏（2026-11-03 实际踩到）。
    全局索引（Unity 侧 GlobalTargetRegistry 写的）本来就汇总了所有开着的工程，
    这里把它的能力暴露到 CLI，一行参数就能跨工程定位。

    返回的 candidates 仅在按名解析有歧义时非空，供调用方打印候选清单。
    """
    text = (value or "").strip().strip("'\"")
    if not text:
        return None, None, "--target 不能为空", []

    expanded = os.path.abspath(os.path.expanduser(text))
    looks_like_path = ("/" in text or "\\" in text or text.endswith(".json"))

    if os.path.isdir(expanded):
        if not _is_unity_project(expanded):
            return None, None, ("--target 指向的目录不是 Unity 工程根（缺 ProjectSettings/ 或 Assets/）：\n"
                                "  %s" % expanded), []
        return expanded, None, None, []

    if os.path.isfile(expanded) or (looks_like_path and os.path.basename(expanded).startswith(INFO_PREFIX)):
        info = load_info(expanded)
        if not info:
            return None, None, "信息文件不存在或无法解析：%s" % expanded, []
        # 工程根从信息文件自带的身份里取；拿不到就只按信息文件定位（不做严格工程比对）
        owner = (info.get(IDENTITY_KEY) or {}).get("projectPath")
        if owner and os.path.isdir(owner):
            return owner, expanded, None, []
        return None, expanded, None, []

    if looks_like_path:
        return None, None, ("--target 看起来是路径但不存在：%s\n"
                            "  要按工程名选，直接写名字（如 MyUnityProject）。" % expanded), []

    root, hits = find_project_root_by_name(text)
    if root:
        # 名字是**子串**匹配（`--target island` 也能命中 MyUnityProject）。
        # 唯一命中就直接用，但必须明说实际连的是谁：`ProjectA` 与 `MyUnityProject`
        # 互为前缀，静默近似会让人以为在操作 A，指令其实落进了 B（写错工程是事故级）。
        actual = os.path.basename(os.path.normpath(root))
        lowered = text.lower()
        if actual.lower() != lowered:
            sys.stderr.write(
                "提示：'%s' 按名字部分匹配到工程 %s（%s）。\n"
                "      要精确锁定请给完整工程名或工程根路径。\n"
                % (text, actual, root))
        return root, None, None, []
    if hits:
        return None, None, (
            "工程名 '%s' 命中 %d 个工程，无法确定连哪个：\n%s\n"
            "  把工程的绝对路径原样传给 --target 即可。"
            % (text, len(hits), "\n".join("    " + item for item in hits))), hits

    live = list_global_targets()
    lines = ["找不到名叫 '%s' 的工程。" % text]
    if live:
        lines.append("  本机全局索引里有这些条目（是否还活着要 probe 才知道）：")
        for item in live:
            identity = item.get(IDENTITY_KEY) or {}
            lines.append("    %-24s %s" % (
                identity.get("projectName") or "?",
                identity.get("projectPath") or item.get("_path") or "?"))
        lines.append("  也可以直接给工程根路径。")
    else:
        lines.append("  全局索引是空的：没有任何 Unity 编辑器上报过自己（%s）。" % global_targets_dir())
    return None, None, "\n".join(lines), []


def global_targets_hint():
    """没有本地信息文件时的一句话提示：本机还有谁开着。"""
    live = []
    for item in list_global_targets():
        identity = item.get(IDENTITY_KEY) or {}
        name = identity.get("projectName")
        if name:
            live.append(name)
    if not live:
        return ""
    return ("  本机还有这些工程在全局索引里：%s\n"
            "  它们不属于本工作区吗？如果其中有开着的编辑器，用 --target <工程名> 指定即可"
            "（例：--target %s）。"
            % ("、".join(sorted(set(live))), sorted(set(live))[0]))


# ---------------------------------------------------------------- 端点定位


class Target(object):
    """一个候选端点及其探测结果。"""

    def __init__(self, url, token, source, expected_project=None, primary=False):
        self.url = url
        self.token = token or ""
        self.source = source
        self.payload = None      # /v1/status 的完整响应
        self.identity = None     # 身份 dict；服务端不提供时为 None
        self.error = None        # 探测失败原因
        # 是否由"本工程主信息文件"产生（IMPR-014：只有它命中才允许提前收手）
        self.primary = primary
        # 是否已经探测过：提前收手后剩下的候选没被探测，诊断输出要如实标出来
        self.probed = False
        # 信息文件里声称的归属工程。端口会被别的工程捡走，
        # 探测回来的身份若与它对不上，说明这个信息文件已经过期。
        self.expected_project = expected_project

    @property
    def reachable(self):
        return isinstance(self.payload, dict)

    @property
    def interactive(self):
        """三态：True=能执行指令的编辑器 / False=不会执行 / None=服务端没说，无从判断。"""
        if not isinstance(self.identity, dict):
            return None
        return bool(self.identity.get("interactive"))

    @property
    def role(self):
        return self.identity.get("role", "?") if isinstance(self.identity, dict) else "?"

    def summary(self):
        """一行身份摘要，用于诊断输出。"""
        if not self.probed:
            return "未探测（已提前收手）"
        if not self.reachable:
            return "不可达（%s）" % (self.error or "未知原因")
        if not isinstance(self.identity, dict):
            return "可达，但服务端未提供身份（旧版本？）"
        return "pid=%s role=%s interactive=%s project=%s queue=%s" % (
            self.identity.get("pid", "?"),
            self.role,
            "yes" if self.interactive else "no",
            self.identity.get("projectName") or self.identity.get("projectPath") or "?",
            self.payload.get("queue", "?"),
        )


def port_probe_range(info):
    """端口扫描区间，来自服务端自报的避让区间；拿不到就退回信息文件里的端口。

    刻意不硬编码任何端口号：服务端换了默认端口，客户端依然能找到。
    """
    start = info.get("portRangeStart")
    count = info.get("portRangeCount") or 10
    try:
        start = int(start)
    except (TypeError, ValueError):
        start = 0
    try:
        count = max(1, min(int(count), 32))
    except (TypeError, ValueError):
        count = 10
    if start <= 0:
        try:
            start = int(info.get("port"))
        except (TypeError, ValueError):
            start = 0
    return start, count


def collect_candidates(args, info):
    """按优先级收集候选端点并去重。"""
    seen = set()
    candidates = []

    def add(url, token, source, expected_project=None, primary=False):
        normalized = normalize_url(url)
        if not normalized or normalized in seen:
            return
        seen.add(normalized)
        candidates.append(Target(normalized, token, source, expected_project, primary))

    token = args.token or (info or {}).get("token") or ""
    # 信息文件自己记着"我是哪个工程写的"，拿它校验端口有没有被顶替
    info_owner = ((info or {}).get(IDENTITY_KEY) or {}).get("projectPath")

    # 1) 命令行显式指定：用户最清楚自己要连谁，优先探测但同样要过身份校验
    if args.url:
        add(args.url, token, "命令行 --url")

    # 2) 信息文件：正常情况下的目标（primary=True，命中即可提前收手——见 strongly_identified）
    if info:
        add(info.get("url") or info.get("port"), token, "信息文件", info_owner, primary=True)

    # 3) 其它信息文件：主文件（每工程一个）+ 各实例自述副本，能列出全部候选
    try:
        for path in list_info_files():
            if os.path.abspath(path) == os.path.abspath(DEFAULT_INFO):
                continue
            data = load_info(path)
            if not data:
                continue
            add(data.get("url") or data.get("port"),
                data.get("token") or token,
                "信息文件 " + os.path.basename(path),
                ((data.get(IDENTITY_KEY) or {}).get("projectPath")))
    except OSError:
        pass

    # 4) 端口扫描兜底：信息文件可能被别的进程写成它自己的端口
    if info:
        start, count = port_probe_range(info)
        if start > 0:
            for port in range(start, start + count):
                add("http://127.0.0.1:%d/" % port, token, "端口扫描")

    return candidates


def probe(target, timeout=PROBE_TIMEOUT):
    """问一句「你是谁」：GET /v1/status 并取出身份块。"""
    target.probed = True
    client = Client(target.url, target.token, timeout)
    status, payload, raw = client.call("v1/status")

    if status is None:
        target.error = raw.strip()[:200] or "连接失败"
        return target

    if status == 401:
        target.error = "令牌被拒绝（401）"
        return target

    if status != 200 or not isinstance(payload, dict):
        target.error = "HTTP %s" % status
        return target

    target.payload = payload
    identity = payload.get(IDENTITY_KEY)
    if isinstance(identity, dict):
        target.identity = identity

    # 信息文件过期检测：文件是"某工程上次运行时"写的，端口可能早被别的工程捡走。
    # 只要回来的身份与文件声称的归属工程对不上，就判定这个候选不可用
    # （不删文件，只是不选它；诊断信息里能看出发生了什么）。
    expected = target.expected_project
    if expected and isinstance(identity, dict):
        actual = identity.get("projectPath")
        if actual and os.path.normcase(os.path.abspath(actual)) != \
                os.path.normcase(os.path.abspath(expected)):
            target.error = ("信息文件已过期：该端口现在是 %s（pid=%s），不是 %s"
                            % (identity.get("projectName") or actual,
                               identity.get("pid", "?"),
                               os.path.basename(expected.rstrip("\\/")) or expected))
            target.payload = None

    return target


def local_project_paths():
    """本工作区内的 Unity 工程绝对路径集合（规范化后用于比对身份块）。"""
    return {os.path.normcase(os.path.abspath(root)) for root in find_unity_project_roots(WS_ROOT)}


def strongly_identified(item):
    """这个候选是否「结构上不可能连错」——是就可以提前收手，不必再扫剩下的端口（IMPR-014）。

    每次调用固定花 ~18 秒，钱全花在端口避让区间里那 9 个空端口上：每个等 2 秒连接失败。
    而信息文件那一条是第一个被探测的，身份也常常完全对上。全量探测的收益只在
    「信息文件已过期、端口被别的进程顶替」时才兑现，所以把收手条件定得极窄，
    三条同时满足才收：

    1. 候选来自**本工程的主信息文件**（不是端口扫描、也不是别的工程的自述副本）；
    2. 身份明确 role=editor 且 interactive=true（服务端自证能执行指令）；
    3. projectPath 规范化后与 --project-root 完全相等；没指定工程根时，
       要求身份里确实带 projectPath（probe 已校验它与信息文件记录的归属一致）。

    任何一条不满足就退回全量探测——宁可慢，不可连错工程。
    """
    if not item.reachable or not item.primary:
        return False
    if item.interactive is not True or item.role != "editor":
        return False
    if not isinstance(item.identity, dict):
        return False

    path = item.identity.get("projectPath")
    if not path:
        return False

    path = os.path.normcase(os.path.abspath(path))
    if EXPECTED_PROJECT:
        return path == os.path.normcase(os.path.abspath(EXPECTED_PROJECT))
    # 没指定工程根时，probe() 已经确认过「回来的 projectPath == 信息文件里记录的归属」，
    # 端口被顶替的情况在那一步就被判死了，这里只需要它确实报了工程路径。
    return True


def choose_target(candidates):
    """挑选最终端点。

    优先级：本工程的交互式编辑器 > 任意交互式编辑器 > 身份未知 > 明确不能执行。
    第一档用 projectPath 比对：同一台机器上可能同时开着别的工程的编辑器，
    端口扫描兜底很容易扫到它们——连错了会改到别人的工程里去。

    **调用方显式指定了工程根时（EXPECTED_PROJECT 非空）改为严格模式**：
    只接受 projectPath 完全一致的目标，匹配不到就直接失败，绝不降级。
    降级曾经造成真实事故：某工程退出后信息文件残留、端口被另一个工程顶替，
    指令被静默发到了错误的工程（`file.write` 会直接改错项目）。
    """
    reachable = [item for item in candidates if item.reachable]
    mine = local_project_paths()

    def project_of(item):
        if not isinstance(item.identity, dict):
            return None
        path = item.identity.get("projectPath")
        if not path:
            return None
        return os.path.normcase(os.path.abspath(path))

    def is_local(item):
        path = project_of(item)
        return path is not None and path in mine

    if EXPECTED_PROJECT:
        want = os.path.normcase(os.path.abspath(EXPECTED_PROJECT))
        for item in reachable:
            if item.interactive is True and project_of(item) == want:
                return item
        return None

    # 没有指定工程根时（常规用法：用本工程自己部署的那份 send.py）也要守住一条红线：
    # **绝不因为"它是个 Unity 编辑器"就连上去**。身份里明确写着别的工程的候选一律排除，
    # 只保留"属于本工作区"和"身份缺失（老版本服务端）"两类。
    for predicate in (
        lambda item: item.interactive is True and is_local(item),
        lambda item: item.interactive is True and project_of(item) is None,
        lambda item: item.interactive is None,
        lambda item: item.interactive is False and project_of(item) is None,
    ):
        for item in reachable:
            if predicate(item):
                return item
    return None


def discover(args):
    """定位真正能执行指令的端点。

    返回 (target_or_None, candidates, error_message_or_None)。
    """
    info_path, info = load_primary_info(args.info)

    if info is None and not args.url:
        hint = global_targets_hint()
        return None, [], (
            "找不到服务信息文件（已尝试 %s 下的 %s.*）。\n"
            "  Unity 编辑器里的 HTTP 指令服务可能没启动。\n"
            "  请在 Unity 中执行：Tools / 指令系统 / HTTP 指令服务%s"
            % (COMMAND_DIR, INFO_PREFIX, ("\n" + hint) if hint else "")
        )

    if args.no_resolve:
        # 关闭定位：直接用信息文件 / --url，不做身份校验（应急或调试用）
        port = (info or {}).get("port") or args.port
        if args.url:
            url = args.url
        elif port:
            url = (info or {}).get("url") or ("http://127.0.0.1:%d/" % int(port))
        else:
            return None, [], "信息文件里没有端口，且未用 --url 或 --port 指定"
        target = Target(normalize_url(url), args.token or (info or {}).get("token") or "",
                        "信息文件（已关闭定位）")
        probe(target)
        return target, [target], None

    candidates = collect_candidates(args, info)
    # 逐个探测，命中即收手（IMPR-014）：
    # 主信息文件那条同时满足「身份自证 + 工程一致」时，端口避让区间里剩下的空端口
    # 不可能给出更好的答案，再逐个等 2 秒只是白烧时间。
    # 两种情况下不收手：--url 是用户的显式指定（优先级最高，必须走完整选址流程）；
    # resolve 是诊断命令，它的价值正是"把所有候选列全"。
    allow_early_stop = not args.url and args.command != "resolve"
    for item in candidates:
        probe(item)
        if allow_early_stop and strongly_identified(item):
            return item, candidates, None

    # `--url` 是用户的显式指定，拥有最高优先级：连不上就直接报错，绝不偷偷换成别的端点。
    # （曾经被"本工程编辑器"这一档盖掉：指了 57833，结果还是连回本机 57832。）
    if args.url:
        for item in candidates:
            if item.source == "命令行 --url":
                if item.reachable:
                    return item, candidates, None
                return None, candidates, (
                    "--url 指定的端点不可用：%s\n  %s" % (item.url, item.error or "未知原因"))

    chosen = choose_target(candidates)
    if chosen is None and EXPECTED_PROJECT:
        return None, candidates, strict_miss_message(candidates)
    return chosen, candidates, None


def strict_miss_message(candidates):
    """严格选址失败时的诊断：把"为什么连不上"和"当前可达的是谁"都说清楚。"""
    lines = [
        "没有找到 projectPath 与指定工程一致的编辑器：",
        "  " + EXPECTED_PROJECT,
        "  指定了工程根时不会改用其它工程的编辑器（那会把指令发错项目）。",
    ]
    unreachable = 0
    for item in candidates:
        if item.reachable:
            lines.append("  可达   %-30s %s" % (item.url, item.summary()))
        else:
            unreachable += 1
    if unreachable:
        lines.append("  另有 %d 个端口不可达" % unreachable)
    lines.append("  该工程的 Unity 可能已退出——信息文件是它上次运行时留下的，端口已被别的工程占走。")
    lines.append("  重新打开编辑器即可；确认要连别的工程请显式用 --url。")
    return "\n".join(lines)


def report_choice(chosen, candidates):
    """把定位结果写到 stderr：正常时一行，异常时解释为什么换了目标。"""
    if chosen is None:
        return

    if chosen.interactive is True and chosen.source.startswith("信息文件"):
        return
    if chosen.interactive is True and chosen.source == "命令行 --url":
        return

    sys.stderr.write("端点定位：" + chosen.url + "（来源：" + chosen.source + "）\n")
    for item in candidates:
        mark = "*" if item is chosen else " "
        sys.stderr.write("  %s %-28s %-22s %s\n"
                         % (mark, item.url, item.source, item.summary()))

    if chosen.interactive is False:
        sys.stderr.write(
            "警告：该端口背后的进程自证为「%s」（非交互式），它不跑 EditorApplication.update，\n"
            "      指令会永久卡在队列里不被执行。这通常说明信息文件被别的 Unity 进程写成了它的端口。\n"
            "      要么让客户端自动纠正（默认行为），要么用 --url 直连编辑器本体。\n" % chosen.role
        )


# ---------------------------------------------------------------- 输出


def print_report(payload, as_json=False):
    """把执行报告打印成人能看懂的样子。"""
    if as_json:
        print(json.dumps(payload, ensure_ascii=False, indent=2))
        return

    if not isinstance(payload, dict):
        print("响应不是 JSON 对象：%r" % (payload,))
        return

    if "records" not in payload:
        # status / ping / commands 之类的非执行响应
        print(json.dumps(payload, ensure_ascii=False, indent=2))
        return

    dry = payload.get("dryRun")
    head = "执行报告%s" % ("（dry-run，未真正执行）" if dry else "")
    print(head)
    print("  来源 %s" % payload.get("source", "?"))
    print("  共 %(total)s 条：成功 %(succeeded)s / 失败 %(failed)s / 跳过 %(skipped)s，耗时 %(elapsedMs)s ms"
          % {
              "total": payload.get("total", 0),
              "succeeded": payload.get("succeeded", 0),
              "failed": payload.get("failed", 0),
              "skipped": payload.get("skipped", 0),
              "elapsedMs": payload.get("elapsedMs", 0),
          })
    summary = payload.get("summary")
    if summary:
        print("  结论 %s" % summary)

    # DOC-001 后续：失败块顶到 records 之前。records 一长，末尾那句提示就淹没在输出里了
    if not payload.get("ok"):
        failures = payload.get("failures") or []
        print()
        print("⚠ 本次有 %s 条指令失败，先看这里：" % payload.get("failed", len(failures)))
        for item in failures:
            print("  ✗ #%s %-24s [%s] %s" % (
                item.get("index", "?"),
                item.get("id", "?"),
                item.get("status", "?"),
                item.get("message", ""),
            ))
        print("  修完再重试；自愈流程见 SKILL.md")
        print()

    records = payload.get("records") or []
    for record in records:
        status = record.get("status", "?")
        mark = "OK  " if status == "Success" else ("跳过" if status == "Skipped" else "失败")
        line = "  #%s %-24s [%s] %s" % (
            record.get("index", "?"),
            record.get("id", "?"),
            mark,
            record.get("message", ""),
        )
        print(line.rstrip())
        data = record.get("data")
        if data not in (None, ""):
            print("        data: %s" % json.dumps(data, ensure_ascii=False))


def print_error(status, payload, raw):
    message = ""
    if isinstance(payload, dict):
        message = payload.get("message") or payload.get("error") or ""
    if not message:
        message = raw.strip()[:400]
    code = payload.get("error") if isinstance(payload, dict) else None
    sys.stderr.write("请求被拒绝（HTTP %s%s）：%s\n" % (
        status, " / " + code if code else "", message))


# ---------------------------------------------------------------- 忙碌等待


def wait_until_ready(client, max_wait, poll=2.0):
    """轮询 /v1/status 直到编辑器空闲；超时返回 False。"""
    deadline = time.time() + max_wait
    announced = False
    while True:
        status, payload, _ = client.call("v1/status")
        if status == 200 and isinstance(payload, dict):
            if not payload.get("busy"):
                return True
            if not announced:
                sys.stderr.write("Unity 正在忙（编译中/刷新资源），等待中……\n")
                announced = True
        if time.time() >= deadline:
            return False
        time.sleep(poll)


# ---------------------------------------------------------------- 子命令


def read_body(path):
    if path == "-":
        return sys.stdin.read()
    if not os.path.isfile(path):
        sys.stderr.write("指令文件不存在：%s\n" % path)
        return None
    with open(path, "r", encoding="utf-8") as handle:
        return handle.read()


def cmd_send(client, args):
    body = read_body(args.file)
    if body is None:
        return 2
    if not body.strip():
        sys.stderr.write("指令内容为空：%s\n" % args.file)
        return 2

    # 先看一眼状态：编译/刷新中的话主线程泵不会推进，发了也是白等
    status, payload, _ = client.call("v1/status")
    if status == 200 and isinstance(payload, dict) and payload.get("busy"):
        if args.wait_busy <= 0:
            print_error(503, {"error": "busy", "message": "Unity 正在编译或刷新资源"},
                        "compiling/updating")
            sys.stderr.write("提示：加 --wait-busy 秒数 可让它自动等待，或先用 status 查看。\n")
            return 4
        if not wait_until_ready(client, args.wait_busy):
            sys.stderr.write("等待 Unity 空闲超时（%d 秒）\n" % args.wait_busy)
            return 4

    params = []
    if args.dry_run:
        params.append("dryRun=1")
    if args.request_id:
        # 幂等键走查询串，不侵入指令 JSON 结构；重试时保持同一个 ID 即不会二次执行
        params.append("requestId=" + urllib.parse.quote(args.request_id))
    query = ("?" + "&".join(params)) if params else ""

    status, payload, raw = client.call("v1/execute" + query, method="POST", body=body)

    if status is None:
        sys.stderr.write(raw + "\n")
        return 2
    if status != 200:
        print_error(status, payload, raw)
        if status == 503:
            sys.stderr.write("提示：加 --wait-busy 秒数 可让它自动等待，或先用 status 查看。\n")
            return 4
        return 3

    print_report(payload, as_json=args.json)
    return 0 if payload.get("ok") else 1


def load_steps(path):
    """读步骤文件 → (steps, error)。格式见 SKILL.md「顺序执行（客户端侧等待）」。"""
    body = read_body(path)
    if body is None:
        return None, "步骤文件读不到：%s" % path
    if not body.strip():
        return None, "步骤文件为空：%s" % path
    try:
        data = json.loads(body)
    except ValueError as exc:
        return None, "步骤文件不是合法 JSON：%s" % exc

    if isinstance(data, dict) and "steps" not in data and "commands" in data:
        # 允许直接把普通指令文件当单步脚本用，省得为一步再包一层
        data = [data]
    elif isinstance(data, dict):
        data = data.get("steps")

    if not isinstance(data, list) or not data:
        return None, "步骤文件里需要 steps 数组（或直接给 {\"commands\": [...]}）：%s" % path

    steps = []
    for offset, item in enumerate(data, 1):
        if not isinstance(item, dict):
            return None, "第 %d 步不是对象" % offset
        commands = item.get("commands")
        if not isinstance(commands, list) or not commands:
            return None, "第 %d 步缺 commands 数组（或为空）" % offset
        try:
            wait_ms = int(item.get("waitAfterMs") or 0)
            wait_busy = int(item.get("waitBusy") or 0)
        except (TypeError, ValueError):
            return None, "第 %d 步的 waitAfterMs / waitBusy 不是数字" % offset
        steps.append({
            "label": str(item.get("label") or ("第 %d 步" % offset)),
            "commands": commands,
            "waitAfterMs": max(0, wait_ms),
            "waitBusy": max(0, wait_busy),
            "stopOnError": bool(item.get("stopOnError", True)),
        })
    return steps, None


def cmd_seq(client, args):
    """客户端侧顺序执行：一步一个 HTTP 请求，步间按 waitAfterMs 自己等。

    服务端一条指令只占一帧、一个批次在一个 tick 内跑完，所以「点一下 → 等动画 → 再看结果」
    只能由客户端控节奏。这里就是那个控制器：**服务端零改动**，随时可回退。
    """
    steps, error = load_steps(args.file)
    if error:
        sys.stderr.write(error + "\n")
        return 2

    total = len(steps)
    results = []
    started_all = time.time()

    if not args.json:
        print("顺序执行 %d 步（步间等待在客户端做，服务端零改动）" % total)

    for position, step in enumerate(steps, 1):
        label = step["label"]
        wait_busy = step["waitBusy"] or args.wait_busy

        if wait_busy > 0:
            status, payload, _ = client.call("v1/status")
            if status == 200 and isinstance(payload, dict) and payload.get("busy"):
                if not wait_until_ready(client, wait_busy):
                    results.append({"index": position, "label": label, "ok": False,
                                    "error": "等待 Unity 空闲超时（%d 秒）" % wait_busy})
                    if not args.json:
                        sys.stderr.write("[%d/%d] %s → 等待空闲超时\n" % (position, total, label))
                    if step["stopOnError"]:
                        break
                    continue

        body = json.dumps({"commands": step["commands"]}, ensure_ascii=False)
        params = []
        if args.dry_run:
            params.append("dryRun=1")
        if args.request_id:
            # 每步一个派生 ID：重跑整份脚本时不会把已成功的步骤再执行一遍
            params.append("requestId=" + urllib.parse.quote("%s-%d" % (args.request_id, position)))
        query = ("?" + "&".join(params)) if params else ""

        tick = time.time()
        status, payload, raw = client.call("v1/execute" + query, method="POST", body=body)
        elapsed = int((time.time() - tick) * 1000)

        entry = {"index": position, "label": label, "elapsedMs": elapsed, "payload": payload}
        if status is None:
            entry.update({"ok": False, "error": raw.strip()[:400]})
        elif status != 200:
            message = payload.get("message") if isinstance(payload, dict) else None
            entry.update({"ok": False, "httpStatus": status,
                          "error": message or raw.strip()[:400]})
        else:
            entry["ok"] = bool(payload.get("ok"))
        results.append(entry)

        if not args.json:
            if entry.get("httpStatus"):
                sys.stderr.write("[%d/%d] %s → 请求被拒绝（HTTP %s）：%s\n"
                                 % (position, total, label, entry["httpStatus"], entry["error"]))
            elif entry.get("error"):
                sys.stderr.write("[%d/%d] %s → %s\n" % (position, total, label, entry["error"]))
            else:
                if payload.get("dryRun"):
                    print("[%d/%d] %s → 校验通过（dry-run，%s 条未执行），%d ms"
                          % (position, total, label, payload.get("total", 0), elapsed))
                else:
                    print("[%d/%d] %s → %s  %s/%s 条，%d ms"
                          % (position, total, label, "成功" if entry["ok"] else "有失败",
                             payload.get("succeeded", 0), payload.get("total", 0), elapsed))
                for item in (payload.get("failures") or []):
                    print("        ✗ #%s %-20s [%s] %s"
                          % (item.get("index", "?"), item.get("id", "?"),
                             item.get("status", "?"), item.get("message", "")))

        if not entry["ok"] and step["stopOnError"]:
            if not args.json:
                sys.stderr.write("第 %d 步失败，按 stopOnError 停下；后面 %d 步未执行\n"
                                 % (position, total - position))
            break

        if step["waitAfterMs"] > 0 and position < total:
            if not args.json:
                print("        等待 %d ms……" % step["waitAfterMs"])
            time.sleep(step["waitAfterMs"] / 1000.0)

    ok = len(results) == total and all(item["ok"] for item in results)
    if args.json:
        print(json.dumps({"ok": ok, "total": total, "executed": len(results),
                          "elapsedMs": int((time.time() - started_all) * 1000),
                          "steps": results}, ensure_ascii=False, indent=2))
    else:
        print("结论：%s（%d/%d 步成功，总耗时 %.1f s）"
              % ("全部成功" if ok else "有失败或未执行完",
                 sum(1 for item in results if item["ok"]), total,
                 time.time() - started_all))

    if not results:
        return 2
    if any(item.get("httpStatus") for item in results):
        return 3
    return 0 if ok else 1


def pump_note(payload):
    """从 /v1/status 取出「主线程泵停摆」提示；正常情况下返回空串。

    服务端给的事实是 pump.alive / pump.snapshotAgeMs / pump.note 与顶层 stale。
    这里的差别是把 note 从 JSON 里捞出来写成一行人话，让调用方（人或 AI）
    在等超时之前就知道这次大概率等不到结果。
    """
    if not isinstance(payload, dict):
        return ""
    pump = payload.get("pump")
    if isinstance(pump, dict) and pump.get("alive") is False:
        note = (pump.get("note") or "").strip()
        age = pump.get("snapshotAgeMs")
        if isinstance(age, (int, float)) and age >= 0:
            return note or ("主线程泵停摆（快照已 %.1f 秒没更新）" % (age / 1000.0))
        return note or "主线程泵从未跑过"
    # 没给 pump 块：旧版服务端，无从判断，不打扰
    return ""


def warn_if_pump_dead(target):
    """下发前先审视探活拿到的 status：泵停摆就先说清楚，别让人干等到超时。"""
    note = pump_note(getattr(target, "payload", None))
    if not note:
        return False
    queue = (target.payload or {}).get("queue")
    sys.stderr.write("⚠ 主线程泵停摆：%s\n" % note)
    if queue:
        sys.stderr.write("  队列里已有 %s 条指令在排队，本次下发同样不会被消费。\n" % queue)
    sys.stderr.write("  这既不是网络问题也不是本客户端能修的：重启该工程的编辑器通常就能恢复。\n")
    return True


def cmd_simple(client, args, path):
    status, payload, raw = client.call(path)
    if status is None:
        sys.stderr.write(raw + "\n")
        return 2
    if status != 200:
        print_error(status, payload, raw)
        return 3

    if args.json or path != "v1/commands":
        print(json.dumps(payload, ensure_ascii=False, indent=2))
        # status 的正常输出是 JSON（机器读），提示只能走 stderr，避免污染 stdout
        if path == "v1/status":
            note = pump_note(payload)
            if note:
                sys.stderr.write("⚠ %s\n" % note)
        return 0

    commands = payload.get("commands") or []
    print_command_list(commands, payload.get("count", len(commands)))
    return 0


def print_command_list(commands, count=None):
    """把指令数组打印成人读的样子（联网取到的和本地清单共用）。"""
    print("已注册 %d 条指令：" % (count if count is not None else len(commands)))
    width = max([len(item.get("id", "")) for item in commands] or [0])
    for item in commands:
        flag = "  [编辑器]" if item.get("editorOnly") else ""
        print("  %-*s  %s%s" % (width, item.get("id", "?"), item.get("description", ""), flag))
        # 参数说明由服务端单一来源提供（CommandAttribute.Usage），客户端不手写、不会漂移
        usage = item.get("usage") or ""
        if usage:
            print("      %s参数: %s" % (" " * width, usage))


# ---------------------------------------------------------------- 本地清单


def catalog_paths():
    """指令目录下所有清单文件，mtime 新的排前面。

    清单由服务端写（`commands.<projectHash>.json`），与 `.http-info` 同目录：
    那个目录由 `CommandSystemSettings` 决定，所以这里不硬编码任何客户端约定。
    """
    pattern = os.path.join(COMMAND_DIR, "commands.*.json")
    found = glob.glob(pattern)
    found.sort(key=lambda path: os.path.getmtime(path), reverse=True)
    return found


def read_catalog(path):
    """读一份清单；坏文件返回 (None, 原因)。"""
    try:
        with open(path, "r", encoding="utf-8-sig") as handle:
            data = json.load(handle)
    except Exception as error:
        return None, str(error)
    if not isinstance(data, dict) or not isinstance(data.get("commands"), list):
        return None, "不是清单格式（缺 commands 数组）"
    return data, None


def pick_catalog(args):
    """按 --target / 工作区选一份清单。

    返回 (data, path, error)。同目录有多份（一个工作区放多个工程）时：
    指定了工程根就按 identity.projectPath 精确挑，否则取最新那份并提示还有别的。
    """
    paths = catalog_paths()
    if not paths:
        return None, None, (
            "指令目录里没有清单（%s）。\n"
            "  Unity 编辑器里的 HTTP 指令服务没启动过，或跑的还是旧版包（未落盘清单）。\n"
            "  先 `send.py commands` 联网取一次，或升级目标工程的服务端包。"
            % COMMAND_DIR)

    if EXPECTED_PROJECT:
        want = os.path.normcase(os.path.abspath(EXPECTED_PROJECT))
        for path in paths:
            data, _ = read_catalog(path)
            identity = (data or {}).get(IDENTITY_KEY) or {}
            owner = identity.get("projectPath")
            if owner and os.path.normcase(os.path.abspath(owner)) == want:
                return data, path, None
        return None, None, (
            "指令目录里的清单都不属于 %s：\n%s\n"
            "  该工程的编辑器可能没启动过（清单由服务端写），先用 `send.py commands --target <工程>` 联网确认。"
            % (EXPECTED_PROJECT, "\n".join("    " + path for path in paths)))

    data, error = read_catalog(paths[0])
    if data is None:
        return None, paths[0], "清单读不了：%s" % error
    if len(paths) > 1:
        sys.stderr.write("（本工作区有 %d 份清单，取最新那份：%s）\n" % (len(paths), paths[0]))
    return data, paths[0], None


def cmd_catalog(args):
    """只读本地清单，不发任何 HTTP 请求。"""
    data, path, error = pick_catalog(args)
    if error:
        sys.stderr.write(error + "\n")
        return 2

    commands = data.get("commands") or []
    prefix = getattr(args, "prefix", None)
    if prefix:
        commands = [item for item in commands if (item.get("id") or "").startswith(prefix)]

    if args.json:
        payload = dict(data)
        if prefix:
            payload["commands"] = commands
            payload["count"] = len(commands)
        print(json.dumps(payload, ensure_ascii=False, indent=2))
        return 0

    identity = data.get(IDENTITY_KEY) or {}
    print("指令清单（本地，未联网）：%s" % path)
    print("  工程 %s · 生成于 %s · 协议 v%s"
          % (identity.get("projectName") or "?",
             data.get("generatedUtc") or "?",
             data.get("protocolVersion") or "?"))
    print_command_list(commands, len(commands) if prefix else data.get("count"))
    sys.stderr.write(
        "⚠ 这是编辑器上次运行时写下的快照，指令集会随包升级/模块增删变化；\n"
        "  真正下发前以服务端校验为准（`send.py commands` 联网版是权威来源）。\n")
    return 0


def cmd_cancel(client, args):
    """请求协作式取消：当前批次会在下一条指令前干净退出。"""
    status, payload, raw = client.call("v1/cancel", method="POST", body="")
    if status is None:
        sys.stderr.write(raw + "\n")
        return 2
    if status != 200:
        print_error(status, payload, raw)
        return 3

    message = payload.get("message") if isinstance(payload, dict) else None
    print(message or "已发送取消请求")
    return 0


def fetch_compile_report(client, limit=50):
    """读取服务端的编译报告；旧版服务端没有该指令时返回 None。"""
    body = json.dumps({"commands": [{
        "id": "asset.compileReport",
        "params": {"includeMessages": True, "limit": limit},
    }]}, ensure_ascii=False)

    status, payload, raw = client.call("v1/execute", method="POST", body=body)
    if status != 200 or not isinstance(payload, dict):
        return None

    records = payload.get("records") or []
    if not records:
        return None

    record = records[0]
    if record.get("status") != "Success":
        return None

    data = record.get("data")
    return data if isinstance(data, dict) else None


def latest_source_mtime(project_root):
    """工程 Assets 下 .cs / .asmdef 的最新 mtime；拿不到返回 0.0。仅用于预检提示。"""
    latest = 0.0
    assets = os.path.join(project_root, "Assets")
    if not os.path.isdir(assets):
        return 0.0
    for dirpath, dirnames, filenames in os.walk(assets):
        dirnames[:] = [d for d in dirnames if d not in ("Library", "obj", "bin")]
        for name in filenames:
            if name.endswith((".cs", ".asmdef")):
                try:
                    latest = max(latest, os.path.getmtime(os.path.join(dirpath, name)))
                except OSError:
                    pass
    return latest


def cmd_compile(client, args):
    """编译并回传结果——"改代码 → 编译 → 看错误"的 CLI 闭环。

    只等编辑器空闲是不够的：编译失败时同样会空闲，不读报告就会误判成功。
    """
    wait = max(1, args.wait)
    before = fetch_compile_report(client, limit=0)
    before_finished = (before or {}).get("finishedUtc")

    body = json.dumps({"commands": [{"id": "asset.compile", "params": {"clean": args.clean}}]},
                      ensure_ascii=False)
    status, payload, raw = client.call("v1/execute", method="POST", body=body)
    if status is None:
        sys.stderr.write(raw + "\n")
        return 2
    if status != 200:
        print_error(status, payload, raw)
        return 3

    sys.stderr.write("已请求编译，等待完成……\n")
    deadline = time.time() + wait
    seen_compiling = False
    idle_hits = 0
    while time.time() < deadline:
        st, snapshot, _ = client.call("v1/status")
        if st == 200 and isinstance(snapshot, dict):
            if snapshot.get("compiling") or snapshot.get("updating"):
                seen_compiling = True
                idle_hits = 0
            else:
                idle_hits += 1
                if idle_hits >= 2:
                    break
        time.sleep(0.4)
    else:
        sys.stderr.write("等待编译超时（%d 秒）。编译可能仍在进行，可用 status 复查。\n" % wait)
        return 4

    report = fetch_compile_report(client, limit=50)
    if report is None:
        print("编译已结束（编辑器空闲）。服务端未提供 asset.compileReport，无法确认成败。")
        return 0

    if not report.get("finished"):
        print("编译已结束，但没有拿到有效结果（可能未触发实际编译）。")
        return 0

    if before_finished and report.get("finishedUtc") == before_finished:
        # 已知边缘情形：源码 mtime 变了但 dll 内容哈希未变（或 ADB 缓存），
        # Unity 认为无需重编。此时明确提示 --force，并尽量给出 mtime 佐证。
        hint = ""
        # 工程根：显式 --project-root 优先，否则用 configure() 探明的 EXPECTED_PROJECT
        root = getattr(args, "project_root", None) or globals().get("EXPECTED_PROJECT")
        if root and os.path.isdir(root):
            latest = latest_source_mtime(root)
            if latest and time.time() - latest < 3600:
                hint = "（检测到最近 1 小时内源码 mtime 有变化）"
        print("没有检测到新的编译：Unity 认为无需重编%s。" % hint)
        print("确认刚改过代码时，用 --force 强制编译：send.py compile --force")
        return 0

    if report.get("ok"):
        print("编译成功（%d 个警告，%.0f ms）"
              % (report.get("warningCount", 0), report.get("durationMs") or 0))
        return 0

    print("编译失败：%d 个错误，%d 个警告"
          % (report.get("errorCount", 0), report.get("warningCount", 0)))
    for message in report.get("messages") or []:
        if (message.get("type") or "").lower() != "error":
            continue
        print("  %s(%s,%s): %s" % (
            message.get("file") or "?",
            message.get("line"),
            message.get("column"),
            (message.get("message") or "").strip()))
    return 1


def cmd_resolve(args):
    """只做定位诊断，不下发任何指令。"""
    chosen, candidates, error = discover(args)
    if error:
        sys.stderr.write(error + "\n")
        return 2

    print("候选端点 %d 个（* 为最终选择）：" % len(candidates))
    for item in candidates:
        mark = "*" if item is chosen else " "
        print("  %s %-28s %-18s %s" % (mark, item.url, item.source, item.summary()))

    if chosen is None:
        print()
        print("没有任何端点可连通。可能 Unity 没开着，或 HTTP 指令服务被手动关掉了。")
        return 2

    print()
    if chosen.interactive is True:
        print("结论：连的是交互式编辑器（%s），指令会被执行。" % chosen.url)
        return 0

    if chosen.interactive is None:
        print("结论：%s 可达，但服务端没有自报身份（旧版本客户端/服务端）。" % chosen.url)
        print("      无法确认它会不会执行指令；升级服务端后即可自动判定。")
        return 0

    print("结论：%s 不是交互式编辑器（role=%s），指令不会被执行！" % (chosen.url, chosen.role))
    print("      这通常意味着信息文件被别的 Unity 进程（如资源导入工作进程）写成了它的端口。")
    print("      正常情况下客户端会自动避开它——走到这一步说明没找到编辑器本体。")
    return 5


# ---------------------------------------------------------------- 入口


def build_parser():
    """扁平参数结构。

    刻意不用 argparse 子 parser：子 parser 会用自己的默认值覆盖主 parser 已解析的
    全局参数（经典陷阱），导致 `send x.json --json` 失效。扁平结构没这个问题。
    """
    parser = argparse.ArgumentParser(
        prog="send.py",
        description="向 Unity 编辑器的指令系统下发 JSON 指令",
    )
    parser.add_argument("command", choices=["ping", "status", "commands", "catalog", "send", "seq", "resolve", "cancel", "compile"],
                        help="要执行的操作；catalog 读本地清单（不联网），resolve 只做端点定位诊断，"
                             "cancel 请求协作式取消，compile 触发编译并回传错误，"
                             "seq 顺序执行多步（步间客户端等待）")
    parser.add_argument("file", nargs="?",
                        help="send 的指令 JSON / seq 的步骤 JSON 文件路径，'-' 表示从标准输入读")
    parser.add_argument("--dry-run", action="store_true", help="只校验不真正执行")
    parser.add_argument("--request-id", metavar="ID",
                        help="幂等键：同一 ID 重试不会二次执行（服务端缓存 5 分钟）")
    parser.add_argument("--wait", type=int, default=180, metavar="秒",
                        help="compile 时等待编译完成的秒数（默认 180）")
    parser.add_argument("--clean", action="store_true",
                        help="compile 时清空编译缓存做干净重建")
    parser.add_argument("--wait-busy", type=int, default=0, metavar="秒",
                        help="Unity 忙时自动等待的秒数（默认 0，直接报错）")
    parser.add_argument("--info", help="服务信息文件路径（默认 %s）" % DEFAULT_INFO)
    parser.add_argument("--project-root", metavar="路径",
                        help="Unity 工程根（含 Assets/ 的那一层）。脚本不在 .workbuddy 下时用它显式指定。")
    parser.add_argument("--target", metavar="工程名|路径",
                        help="跨工程选址：工程名（如 MyUnityProject）、工程根路径或 .http-info 路径。"
                             "不给时只看本工作区——脚本所在工作区之外的编辑器必须用它指定。")
    parser.add_argument("--url", help="直接指定服务地址，跳过信息文件（仍会校验身份）")
    parser.add_argument("--token", help="直接指定访问令牌，覆盖信息文件里的值")
    parser.add_argument("--port", type=int, help="信息文件缺端口时用它")
    parser.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT,
                        help="单次请求超时秒数（默认 %d）" % DEFAULT_TIMEOUT)
    parser.add_argument("--json", action="store_true",
                        help="输出原始 JSON，不做人话排版")
    parser.add_argument("--prefix", metavar="前缀",
                        help="catalog 时只列这个前缀的指令（如 ui / prefab / scene）")
    parser.add_argument("--no-resolve", action="store_true",
                        help="关闭端点定位：直接用信息文件/--url，不做身份校验")
    parser.add_argument("--force", action="store_true",
                        help="端点不是交互式编辑器时也要发（默认拒绝，指令不会被执行）")
    return parser


def main(argv=None):
    parser = build_parser()
    args = parser.parse_args(argv)

    if getattr(args, "project_root", None):
        configure(args.project_root)

    if getattr(args, "target", None):
        if getattr(args, "project_root", None):
            parser.error("--target 与 --project-root 是同一件事的两种写法，只留一个")
        root, info_path, err, _ = resolve_target_selector(args.target)
        if err:
            sys.stderr.write(err + "\n")
            return 2
        # configure() 同时改 WS_ROOT / COMMAND_DIR / EXPECTED_PROJECT：
        # 前两个让"信息文件在哪"指向目标工程所在工作区，后者让选址进入严格模式
        # （只认 projectPath 完全一致的编辑器，绝不降级到别的工程——见 choose_target）。
        if root:
            configure(root)
        if info_path:
            args.info = info_path

    if args.command in ("send", "seq") and not args.file:
        parser.error("%s 需要指定 JSON 文件路径（'-' 表示标准输入）" % args.command)

    if args.command == "resolve":
        return cmd_resolve(args)

    # catalog 只读本地清单，不发 HTTP：放在 discover 之前，
    # 于是"编辑器没开"也能列出上次运行时的指令集。
    if args.command == "catalog":
        return cmd_catalog(args)

    chosen, candidates, error = discover(args)
    if error:
        sys.stderr.write(error + "\n")
        return 2

    if chosen is None:
        # 不可达的候选**压成一行**：端口扫描会一次列十个"目标计算机积极拒绝"，
        # 每条都带一长串 WinError，真正的建议（编辑器没开 / 用 --target）会被淹掉。
        # 可达但身份不对的反而要逐条列出——那是"有东西在跑，但不是能干活的编辑器"。
        reachable = [item for item in candidates if item.reachable]
        unreachable = [item for item in candidates if not item.reachable]
        if reachable:
            sys.stderr.write("找到 %d 个可达端点，但没有一个能执行指令：\n" % len(reachable))
            for item in reachable:
                sys.stderr.write("  %-28s %-18s %s\n" % (item.url, item.source, item.summary()))
        else:
            sys.stderr.write("找不到任何可连通的端点（探测 %d 个候选，全部不可达）。\n"
                             % len(candidates))
            if unreachable:
                first = unreachable[0]
                sys.stderr.write("  首个候选 %s → %s\n"
                                 % (first.url, first.error or "未知原因"))
        sys.stderr.write("  Unity 编辑器可能没开着，或用菜单 Tools / 指令系统 / HTTP 指令服务 关掉了。\n")
        hint = global_targets_hint()
        if hint:
            sys.stderr.write(hint + "\n")
        return 2

    report_choice(chosen, candidates)

    if chosen.interactive is False and not args.force:
        sys.stderr.write(
            "拒绝下发：%s 不是交互式编辑器（role=%s），指令会卡在队列里永远不被执行。\n"
            "  先用 `send.py resolve` 看看有哪些端点；确认要发就加 --force。\n"
            % (chosen.url, chosen.role)
        )
        return 5

    client = Client(chosen.url, chosen.token, args.timeout)

    # 泵的死活在探活那一步已经顺手取回来了：趁还没下发先说清楚，
    # 否则调用方要干等到 --timeout 才拿到一个毫无信息的失败。
    warn_if_pump_dead(chosen)

    if args.command == "send":
        return cmd_send(client, args)
    if args.command == "seq":
        return cmd_seq(client, args)
    if args.command == "cancel":
        return cmd_cancel(client, args)
    if args.command == "compile":
        return cmd_compile(client, args)
    return cmd_simple(client, args, "v1/" + args.command)


if __name__ == "__main__":
    sys.exit(main())
