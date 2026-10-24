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

定位正确的端点（本客户端最重要的一件事）：
    同一个工程目录里可能同时跑着多个 Unity 进程，它们都会加载同一套 Editor 程序集并
    各自起一个监听（典型的是资源导入工作进程 AssetImportWorkerN）。这些进程 ping 得通、
    status 也正常，但不跑 EditorApplication.update，指令进了队列就永远不会被消费。
    因此客户端在发指令之前会先问一句「你是谁」，只往自证为交互式编辑器的那个端口发，
    并优先选 projectPath 属于本工作区的（同机多工程时避免连错）。
    判定完全依赖服务端自报的身份（/v1/status 的 identity 块），不查进程、不猜端口。

退出码：
    0  HTTP 200 且 ok=true（全部指令成功）
    1  HTTP 200 但 ok=false（有指令失败，报告已打印）
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


# ---------------------------------------------------------------- 端点定位


class Target(object):
    """一个候选端点及其探测结果。"""

    def __init__(self, url, token, source, expected_project=None):
        self.url = url
        self.token = token or ""
        self.source = source
        self.payload = None      # /v1/status 的完整响应
        self.identity = None     # 身份 dict；服务端不提供时为 None
        self.error = None        # 探测失败原因
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

    def add(url, token, source, expected_project=None):
        normalized = normalize_url(url)
        if not normalized or normalized in seen:
            return
        seen.add(normalized)
        candidates.append(Target(normalized, token, source, expected_project))

    token = args.token or (info or {}).get("token") or ""
    # 信息文件自己记着"我是哪个工程写的"，拿它校验端口有没有被顶替
    info_owner = ((info or {}).get(IDENTITY_KEY) or {}).get("projectPath")

    # 1) 命令行显式指定：用户最清楚自己要连谁，优先探测但同样要过身份校验
    if args.url:
        add(args.url, token, "命令行 --url")

    # 2) 信息文件：正常情况下的目标
    if info:
        add(info.get("url") or info.get("port"), token, "信息文件", info_owner)

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
        return None, [], (
            "找不到服务信息文件（已尝试 %s 下的 %s.*）。\n"
            "  Unity 编辑器里的 HTTP 指令服务可能没启动。\n"
            "  请在 Unity 中执行：Tools / 指令系统 / HTTP 指令服务" % (COMMAND_DIR, INFO_PREFIX)
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
    for item in candidates:
        probe(item)

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
        return 0

    commands = payload.get("commands") or []
    print("已注册 %d 条指令：" % payload.get("count", len(commands)))
    width = max([len(item.get("id", "")) for item in commands] or [0])
    for item in commands:
        flag = "  [编辑器]" if item.get("editorOnly") else ""
        print("  %-*s  %s%s" % (width, item.get("id", "?"), item.get("description", ""), flag))
        # 参数说明由服务端单一来源提供（CommandAttribute.Usage），客户端不手写、不会漂移
        usage = item.get("usage") or ""
        if usage:
            print("      %s参数: %s" % (" " * width, usage))
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
    parser.add_argument("command", choices=["ping", "status", "commands", "send", "resolve", "cancel", "compile"],
                        help="要执行的操作；resolve 只做端点定位诊断，cancel 请求协作式取消，"
                             "compile 触发编译并回传错误")
    parser.add_argument("file", nargs="?",
                        help="send 时的指令 JSON 文件路径，'-' 表示从标准输入读")
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
    parser.add_argument("--url", help="直接指定服务地址，跳过信息文件（仍会校验身份）")
    parser.add_argument("--token", help="直接指定访问令牌，覆盖信息文件里的值")
    parser.add_argument("--port", type=int, help="信息文件缺端口时用它")
    parser.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT,
                        help="单次请求超时秒数（默认 %d）" % DEFAULT_TIMEOUT)
    parser.add_argument("--json", action="store_true",
                        help="输出原始 JSON，不做人话排版")
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

    if args.command == "send" and not args.file:
        parser.error("send 需要指定指令文件路径（'-' 表示标准输入）")

    if args.command == "resolve":
        return cmd_resolve(args)

    chosen, candidates, error = discover(args)
    if error:
        sys.stderr.write(error + "\n")
        return 2

    if chosen is None:
        sys.stderr.write("找不到任何可连通的端点。\n")
        for item in candidates:
            sys.stderr.write("  %-28s %-18s %s\n" % (item.url, item.source, item.summary()))
        sys.stderr.write("  Unity 编辑器可能没开着，或用菜单 Tools / 指令系统 / HTTP 指令服务 关掉了。\n")
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
    if args.command == "send":
        return cmd_send(client, args)
    if args.command == "cancel":
        return cmd_cancel(client, args)
    if args.command == "compile":
        return cmd_compile(client, args)
    return cmd_simple(client, args, "v1/" + args.command)


if __name__ == "__main__":
    sys.exit(main())
