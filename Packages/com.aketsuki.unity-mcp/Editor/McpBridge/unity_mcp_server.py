#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Unity 指令系统的 MCP stdio 桥。

把一个"只监听本机的 HTTP 指令服务"包装成标准 MCP 服务器，让任意 MCP 客户端
（WorkBuddy / Claude Desktop / Cursor 等）用 tools/list + tools/call 驱动 Unity 编辑器。

设计要点
--------
1. **Unity 服务端零改动**：桥进程内部 `import send` 复用既有的端点发现
   （`.http-info.<projectHash>` / per-pid 副本 / 端口扫描 / 身份自证），
   对外只暴露一条干净的 stdio 通道。
2. **stdout 只允许 MCP 消息**：MCP stdio 传输要求"每行一条紧凑 JSON、消息内不得含换行"。
   所有日志一律走 stderr，否则客户端会解析失败。
3. **为什么不做 Unity 原生 MCP HTTP 服务**：MCP HTTP 传输要求配置里写死一个 URL，
   而本系统的核心价值是"端口避让 + 多工程共存"，写死端口就退化成抢端口。
   stdio 桥天然绕开这个矛盾——发现逻辑在桥里跑，宿主只看到 stdio。
4. **一条 server 服务所有工程**：桥读全局目标索引（`%LOCALAPPDATA%/Aketsuki/UnityMcp/targets`）
   发现本机全部 Unity，按 target 参数切换；不再需要"每个工程配一条 server"。
5. **工具默认折叠**：`--tool-mode meta` 只注册 6 个元工具（~1.7k token），
   指令靠 `unity_list_commands` 按需查；`group` 按域折叠，`full` 是旧的全量展开。
   全量 75 条指令 = 29k 字符 ≈ 1 万 token，且每个工程一条 server 时还要乘 N。

用法（由 MCP 客户端拉起，一般不手动执行）：
    python unity_mcp_server.py                       # 自动发现唯一活工程
    python unity_mcp_server.py --tool-mode group     # 按域折叠
    python unity_mcp_server.py --default-target 工程名或路径

手动自检：
    echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"probe","version":"1"}}}' | python unity_mcp_server.py
"""

import concurrent.futures
import json
import os
import sys
import threading
import time
import urllib.parse

# 让 `import send` 无论 cwd 在哪都能工作（MCP 客户端拉起时 cwd 不可控）
_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import send  # noqa: E402  （必须在 sys.path 调整之后）

SERVER_NAME = "unity-cmd"
SERVER_VERSION = "2.0.0"

# 支持的协议版本（新 → 旧）；协商时优先回客户端给的那个
SUPPORTED_PROTOCOLS = ("2025-06-18", "2025-03-26", "2024-11-05")
DEFAULT_PROTOCOL = SUPPORTED_PROTOCOLS[0]

# 单个工具调用里对 Unity 的 HTTP 超时（秒）。批处理可能很久，给足。
HTTP_TIMEOUT = 180

# 端点缓存有效期：避免每次 tools/call 都做一次全量端口探测
TARGET_TTL = 30.0
# 失败缓存有效期：探测失败后短时间内不重复全量探测（否则一连串调用会连续卡住）
TARGET_FAIL_TTL = 3.0

# 指令列表缓存有效期（tools/list 会频繁调用）
COMMANDS_TTL = 60.0

INSTRUCTIONS = (
    "这是 Unity 编辑器的指令通道：一条连接即可操作本机所有工程。"
    "调用前不需要手动启动服务，编辑器启动时会自动拉起 HTTP 桥；"
    "若返回『连不上 Unity』，说明编辑器没开、正在重编译，或服务被手动关了。"
    "unity_targets 列出当前开着的工程；"
    "多数工具都接受 target 参数（工程名 / 工程根 / .http-info 绝对路径）指定操作哪个工程，"
    "只开着一个工程时可以省略，显式指定过一次后也会沿用。"
    "unity_status 查看编辑器状态与执行中的指令；"
    "unity_execute 一次下发多条指令（串行执行，比逐条调用可靠）；"
    "unity_list_commands 查指令 ID（可加 prefix 收窄、加 verbose 看参数）。"
)


# ---------------------------------------------------------------- 日志（只走 stderr）


def log(message):
    """写 stderr。绝不能用 print——stdout 被 MCP 协议独占。"""
    sys.stderr.write("[unity-mcp] %s\n" % message)
    sys.stderr.flush()


# ---------------------------------------------------------------- 与 Unity 通信


class BridgeError(Exception):
    """工具执行层面的错误（对应 MCP 的 isError=true）。"""


class _DiscoverArgs(object):
    """send.discover() 需要的参数占位；桥不暴露 --url/--token 等覆盖项。

    字段必须覆盖 send.discover() 真正会读的**全部**属性：info / url / token / port /
    no_resolve / command。少一个就是一个 AttributeError，而它发生在慢路径里，
    会把 tools/list 整体打成 -32603、客户端里一条工具都看不到（BUG-011 的现场）。
    新增字段时照着 send.normalize_args 的默认值抄，别凭印象补。
    """

    info = None
    url = None
    token = None
    port = None
    no_resolve = False
    command = None          # send.discover() 用它区分 resolve 诊断模式；桥没有子命令，恒 None


class Bridge(object):
    """封装"找到编辑器 + 发请求 + 缓存"三件事。

    两种选址方式，必须共存：

    * **显式**（<paramref>project_root</paramref> / <paramref>info_path</paramref> 非空）：
      每次失效都重读该工程自己的信息文件，全程不碰 <c>send</c> 的模块级全局状态。
      正因为不碰全局，同一个进程里可以并存多个 Bridge，各自指向不同工程——
      这是"一条 MCP server 服务所有工程"的地基。
    * **传统**（两者都空）：走 <c>send</c> 的全局发现（主信息文件 + 端口扫描），
      兼容没有全局索引的旧服务端，也兼容只配一个 <c>--project-root</c> 的老配置文件。
    """

    def __init__(self, project_root=None, info_path=None, label=None):
        self._lock = threading.Lock()
        self._target = None
        self._target_at = 0.0
        self._target_failed_at = 0.0
        self._target_error = None
        self._commands = None
        self._commands_at = 0.0

        self.project_root = os.path.normpath(os.path.abspath(project_root)) if project_root else None
        self.info_path = os.path.abspath(info_path) if info_path else None
        if label:
            self.label = label
        elif self.project_root:
            self.label = os.path.basename(self.project_root.rstrip("\\/")) or self.project_root
        elif self.info_path:
            self.label = os.path.basename(self.info_path)
        else:
            self.label = "默认"

    # ---- 端点

    def _unreachable(self, detail):
        """把"连不上"翻译成可执行的修复建议——错误信息是模型唯一的线索来源。"""
        lines = ["连不上 Unity（目标：%s）" % self.label]
        if detail:
            lines.append(str(detail))
        lines.append(
            "请确认：编辑器已打开、Tools / 指令系统 / HTTP 指令服务 处于开启状态、"
            "且当前不在重编译过程中。若开着多个工程，用 target 参数指明具体工程。")
        return "\n".join(lines)

    def _fast_discover(self):
        """快路径：直接读本工程的信息文件并做一次身份校验。

        信息文件名已带工程 hash，不再会被别的工程覆盖，所以绝大多数情况这一步就能命中；
        命中即返回，避免走 send.discover 的端口扫描（最坏要串行探测 10 个端口 × 3 秒超时，
        那会让首次 tools/list 卡十几秒，MCP 客户端体验很差）。
        """
        path, info = send.load_primary_info(None)
        if not info:
            return None

        url = info.get("url") or info.get("port")
        if not url:
            return None

        normalized = send.normalize_url(url)
        if not normalized:
            return None

        target = send.Target(normalized, info.get("token") or "",
                             "信息文件 " + os.path.basename(path or ""))
        send.probe(target, timeout=2.0)
        if not target.reachable:
            log("信息文件候选 %s 不可用：%s" % (normalized, target.error))
            return None
        return target

    def _discover_legacy(self):
        fast = self._fast_discover()
        if fast is not None:
            return fast

        # 慢路径：完整发现（含端口扫描），仅在信息文件缺失/失效时走
        log("信息文件不可用，回退到完整端点探测（可能较慢）")
        target, candidates, error = send.discover(_DiscoverArgs())
        if error:
            raise BridgeError(error)
        if target is None:
            lines = ["找不到可连通的 Unity 指令服务。"]
            for item in candidates:
                lines.append("  %-30s %-20s %s" % (item.url, item.source, item.summary()))
            lines.append(
                "请确认：Unity 编辑器已打开、Tools / 指令系统 / HTTP 指令服务 处于开启状态、"
                "且当前不在重编译过程中。"
            )
            raise BridgeError("\n".join(lines))
        return target

    def _discover(self):
        if self.info_path:
            target, _, error = send.target_from_info_path(self.info_path, timeout=2.0)
            if target is None:
                raise BridgeError(self._unreachable(error))
            return target

        if self.project_root:
            target, _, error = send.target_from_project_root(self.project_root, timeout=2.0)
            if target is None:
                raise BridgeError(self._unreachable(error))
            return target

        return self._discover_legacy()

    def target(self, refresh=False):
        """取一个可用的端点；带缓存，避免每次调用都全量探测。"""
        now = time.time()
        with self._lock:
            if not refresh:
                if self._target is not None and now - self._target_at < TARGET_TTL:
                    return self._target
                if self._target is None and now - self._target_failed_at < TARGET_FAIL_TTL:
                    raise BridgeError(self._target_error or "连不上 Unity 指令服务")

            try:
                found = self._discover()
            except BridgeError as error:
                self._target = None
                self._target_error = str(error)
                self._target_failed_at = time.time()
                raise

            self._target = found
            self._target_at = time.time()
            self._target_error = None
            return found

    def invalidate(self):
        with self._lock:
            self._target = None
            self._target_at = 0.0

    # ---- 请求

    def call(self, path, method="GET", body=None, refresh_on_fail=True):
        """发一个请求；连接类失败会重建端点并重试一次。"""
        target = self.target()
        client = send.Client(target.url, target.token, HTTP_TIMEOUT)
        status, payload, raw = client.call(path, method=method, body=body)

        if status is None and refresh_on_fail:
            # 端口可能因域重载变了：重建端点再试一次
            log("请求失败，重建端点后重试：%s" % raw.strip()[:160])
            self.invalidate()
            target = self.target(refresh=True)
            client = send.Client(target.url, target.token, HTTP_TIMEOUT)
            status, payload, raw = client.call(path, method=method, body=body)

        if status is None:
            self.invalidate()
            raise BridgeError("连不上 Unity 指令服务：%s" % raw.strip()[:400])

        return status, payload, raw

    # ---- 具体接口

    def status(self):
        status, payload, raw = self.call("v1/status")
        if status != 200 or not isinstance(payload, dict):
            raise BridgeError(_describe_http_error(status, payload, raw))
        return payload

    def commands(self, refresh=False):
        """指令列表（带缓存）。失败时抛 BridgeError。"""
        now = time.time()
        with self._lock:
            if not refresh and self._commands is not None and now - self._commands_at < COMMANDS_TTL:
                return self._commands

        status, payload, raw = self.call("v1/commands")
        if status != 200 or not isinstance(payload, dict):
            raise BridgeError(_describe_http_error(status, payload, raw))

        items = payload.get("commands") or []
        with self._lock:
            self._commands = items
            self._commands_at = time.time()
        return items

    def execute(self, body, dry_run=False, request_id=None):
        query = []
        if dry_run:
            query.append("dryRun=1")
        if request_id:
            query.append("requestId=" + _quote(request_id))
        path = "v1/execute" + (("?" + "&".join(query)) if query else "")
        status, payload, raw = self.call(path, method="POST", body=body)
        if status != 200 or not isinstance(payload, dict):
            raise BridgeError(_describe_http_error(status, payload, raw))
        return payload

    def cancel(self):
        status, payload, raw = self.call("v1/cancel", method="POST", body="")
        if status != 200 or not isinstance(payload, dict):
            raise BridgeError(_describe_http_error(status, payload, raw))
        return payload


def _quote(text):
    return urllib.parse.quote(str(text))


def _describe_http_error(status, payload, raw):
    message = ""
    if isinstance(payload, dict):
        message = payload.get("message") or payload.get("error") or ""
    if not message:
        message = (raw or "").strip()[:400]

    hints = {
        400: "请求体不是合法的指令 JSON，或参数有误。",
        401: "令牌被拒绝：令牌来自信息文件，不要手改；必要时重启 HTTP 服务让它重新生成。",
        404: "路径不存在（协议版本不匹配？）。",
        405: "HTTP 方法用错。",
        413: "请求体超过 4 MB，请拆分成多条。",
        500: "编辑器内部错误。",
        503: "Unity 正在编译或刷新资源，稍后重试（可用 unity_status 观察）。",
    }
    hint = hints.get(status, "")
    return "HTTP %s%s%s" % (status, ("：" + message) if message else "", ("\n" + hint) if hint else "")


# ---------------------------------------------------------------- 目标选择（一条 server 服务所有工程）


class SelectorError(Exception):
    """选不中目标。这类错误不需要先连上 Unity 就能判定，直接回给模型看。"""


def parse_selector(text):
    """把用户给的 target 归一成 (kind, value)，kind ∈ {root, info, name, invalid}。

    接受三种写法，都来自真实使用习惯：
      * `<工程>/.workbuddy/UnityEditCommand/.http-info.<hash>` —— 令牌文件绝对路径（最直观）
      * `<工程根>`（含 ProjectSettings/ProjectVersion.txt 的那层）
      * 工程名（如 `MyUnityProject`），从全局索引里解析

    **带路径分隔符但解析失败的，一律判 invalid，不再退化成"工程名"**：
    否则传 `D:/Work`（工作区根，不是工程根）会得到"找不到叫这个名字的工程"，
    与真实原因（那是目录不是工程）完全不搭，排查要多绕一圈。
    """
    raw = (text or "").strip().strip('"').strip("'")
    if not raw:
        return None, None

    candidate = os.path.expanduser(raw)
    is_path_like = os.path.isabs(candidate) or ("/" in raw) or ("\\" in raw)

    if os.path.isfile(candidate):
        base = os.path.basename(candidate)
        if base.startswith(send.INFO_PREFIX):
            return "info", os.path.abspath(candidate)
        return "invalid", os.path.abspath(candidate)

    if os.path.isdir(candidate):
        if os.path.isfile(os.path.join(candidate, "ProjectSettings", "ProjectVersion.txt")):
            return "root", os.path.abspath(candidate)
        return "invalid", os.path.abspath(candidate)

    if is_path_like:
        return "invalid", candidate

    return "name", raw


class TargetPool(object):
    """target 选择符 → Bridge 实例池。

    同一个工程复用同一个 Bridge，端点缓存与指令缓存也就跟着复用；
    不同工程各持一份，互不影响（Bridge 内部刻意不碰 send 的模块级全局状态）。
    """

    def __init__(self):
        self._lock = threading.Lock()
        self._bridges = {}          # key -> Bridge
        self._default = None        # 启动参数的默认目标（kind, value）
        self._sticky = None         # 最近一次显式选中的目标（省略 target 时优先用它）
        self._live = None           # 全局索引探活结果缓存
        self._live_at = 0.0

    # ---- 内部

    def _get_root(self, root):
        key = ("root", os.path.normcase(os.path.normpath(os.path.abspath(root))))
        with self._lock:
            bridge = self._bridges.get(key)
            if bridge is None:
                bridge = Bridge(project_root=root)
                self._bridges[key] = bridge
            return bridge

    def _get_info(self, path):
        key = ("info", os.path.normcase(os.path.abspath(path)))
        with self._lock:
            bridge = self._bridges.get(key)
            if bridge is None:
                bridge = Bridge(info_path=path)
                self._bridges[key] = bridge
            return bridge

    def _get_legacy(self):
        """没有任何显式目标时，退到传统单工程发现（兼容不写全局索引的旧服务端）。"""
        key = ("legacy", "")
        with self._lock:
            bridge = self._bridges.get(key)
            if bridge is None:
                bridge = Bridge()
                self._bridges[key] = bridge
            return bridge

    def _live_targets(self):
        """全局索引里当前真的连得上的工程（带缓存，避免每次都等死条目超时）。"""
        now = time.time()
        with self._lock:
            if self._live is not None and now - self._live_at < TARGET_TTL:
                return self._live

        items = send.list_global_targets()
        # 最近更新的排前面：正常退出/启动的条目基本都是活的，第一个就命中
        items.sort(key=lambda item: item.get("updatedUtc") or "", reverse=True)

        live = []
        for item in items:
            identity = item.get(send.IDENTITY_KEY) or {}
            path = (identity.get("projectPath") or "").strip()
            if not path:
                continue
            try:
                bridge = self._get_root(path)
                bridge.target()
            except (BridgeError, SelectorError) as error:
                log("跳过不可用目标 %s：%s" % (path, str(error).splitlines()[0]))
                continue
            live.append((path, bridge))

        with self._lock:
            self._live = live
            self._live_at = time.time()
        return live

    def _resolve_default(self):
        # 1) 启动参数显式指定的默认目标
        if self._default is not None:
            kind, value = self._default
            return self._dispatch(kind, value)

        # 2) 粘性：上次显式选过的工程还活着就继续用它，免得每轮调用都要重述 target
        with self._lock:
            sticky = self._sticky
        if sticky is not None:
            try:
                bridge = self._dispatch(sticky[0], sticky[1])
                bridge.target()
                return bridge
            except (BridgeError, SelectorError):
                with self._lock:
                    if self._sticky == sticky:
                        self._sticky = None

        # 3) 全局索引：唯一活实例就自动用它
        live = self._live_targets()
        if len(live) == 1:
            return live[0][1]
        if len(live) > 1:
            raise SelectorError(self._ambiguous_message(live))

        # 4) 一个都没有：退回传统单工程发现
        return self._get_legacy()

    def _ambiguous_message(self, live):
        lines = ["同时开着 %d 个 Unity 工程，无法确定要操作哪个。请用 target 参数指定其一："
                 % len(live)]
        for path, _ in live:
            lines.append("  " + path)
        lines.append("（也可以调用 unity_targets 查看完整列表；选中一次后后续会沿用。）")
        return "\n".join(lines)

    def _resolve_name(self, value):
        """工程名 → 工程根。重名/查不到都直接报错，绝不猜一个。"""
        root, hits = send.find_project_root_by_name(value)
        if root is not None:
            return root
        if hits:
            raise SelectorError(
                "工程名「%s」匹配到多个工程，请改用完整路径：\n%s"
                % (value, "\n".join("  " + h for h in hits)))
        raise SelectorError(
            "找不到工程名匹配「%s」的 Unity。可调用 unity_targets 查看当前开着的工程。"
            % value)

    def _dispatch(self, kind, value):
        if kind == "root":
            return self._get_root(value)
        if kind == "info":
            return self._get_info(value)
        if kind == "legacy":
            return self._get_legacy()
        if kind == "invalid":
            raise SelectorError(
                "target 指向的路径不是可用的 Unity 工程：%s\n"
                "  需要「含 ProjectSettings/ProjectVersion.txt 的工程根」或「.http-info 文件」，"
                "不是工作区根、也不是 Assets 之类的子目录。" % value)

        return self._get_root(self._resolve_name(value))

    # ---- 对外

    def set_default(self, kind, value):
        self._default = (kind, value)

    def sticky_value(self):
        with self._lock:
            return self._sticky

    def get(self, selector=None):
        """取目标 Bridge。显式 selector 会被记为粘性默认。

        名字在这里就先解析成工程根再记下来：否则粘性里存的是"名字"，
        下次省略 target 时要重新扫一遍全局索引，遇到重名还会突然失败——
        用户明明刚选过，不该有任何歧义。
        """
        kind, value = parse_selector(selector)
        if kind is None:
            return self._resolve_default()

        if kind == "name":
            kind, value = "root", self._resolve_name(value)

        bridge = self._dispatch(kind, value)
        with self._lock:
            self._sticky = (kind, value)
        return bridge

    def invalidate_all(self):
        with self._lock:
            bridges = list(self._bridges.values())
            self._live = None
            self._live_at = 0.0
        for bridge in bridges:
            bridge.invalidate()


POOL = TargetPool()


def bridge_for(arguments):
    """从工具参数里取 target 并解析成 Bridge。

    所有元工具都走这里：`target` 是桥自己的寻址参数，**绝不能**混进指令 params
    一起发给 Unity（那边会当成未知参数报错）。
    """
    selector = arguments.get("target") if isinstance(arguments, dict) else None
    return POOL.get(selector)


# ---------------------------------------------------------------- usage → JSON Schema


def _split_top_level(text, sep=","):
    """按顶层分隔符切分，忽略 [] 内部的分隔符（usage 里会出现 [w,h]）。"""
    parts = []
    depth = 0
    current = []
    for ch in text:
        if ch in "[{(":
            depth += 1
        elif ch in "]})":
            depth -= 1
        if ch == sep and depth == 0:
            parts.append("".join(current))
            current = []
            continue
        current.append(ch)
    if current:
        parts.append("".join(current))
    return parts


_TYPE_ALIASES = {
    "bool": "boolean",
    "boolean": "boolean",
    "int": "integer",
    "integer": "integer",
    "long": "integer",
    "float": "number",
    "double": "number",
    "number": "number",
    "string": "string",
    "str": "string",
    "object": "object",
    "dict": "object",
}


def _split_type_token(text):
    """把类型片段拆成「类型名」+「其余说明」。

    usage 里的类型名后面经常挂着中文括注（`string（Prefab 模式下空串=根）`），
    也可能写成多候选（`int|string`）。拿整段去比对类型名的话这两种都会失配，
    后果是**最常见的 `target: string` 退化成"任意 JSON 值"**——typed 工具因此
    丢掉了本该有的参数类型。这里只取开头的 ASCII 标识符当类型名，后面原样保留。
    """
    i = 0
    while i < len(text) and text[i].isascii() and (text[i].isalnum() or text[i] == "_"):
        i += 1
    return text[:i], text[i:]


def _type_to_schema(type_text):
    text = (type_text or "").strip()

    if "[" in text:
        # [x,y,z] / [...] / [w,h] —— 一律当数组；元素类型无法从 usage 精确得知
        return {"type": "array", "description": text}

    name, rest = _split_type_token(text)
    schema_type = _TYPE_ALIASES.get(name.lower())

    if schema_type is None:
        # 未知类型（target / vertical|horizontal / any / 空）：不限制，把原文写进描述
        if not text:
            return {"description": "任意 JSON 值"}
        return {"description": text if name.lower() != "any" else text + "（任意 JSON 值）"}

    if not rest:
        return {"type": schema_type}

    # 多候选（int|string）与中文括注都保留进描述，别把调用方要的信息丢掉
    return {"type": schema_type, "description": text}


def usage_to_schema(usage):
    """把 `path: string, content: string, append: bool` 解析成 JSON Schema 的 properties。"""
    properties = {}
    if not usage:
        return properties

    for part in _split_top_level(usage):
        part = part.strip()
        if not part:
            continue
        if ":" in part:
            name, type_text = part.split(":", 1)
        else:
            name, type_text = part, ""
        name = name.strip()
        if not name or " " in name:
            # 形如 "size:[w,h]" 已在上面的 split 里正确切开；剩余含空格的多半是说明文字，跳过
            continue
        properties[name] = _type_to_schema(type_text)
    return properties


def command_to_tool(command):
    """把一条服务端指令描述转成一个 MCP 工具定义。"""
    command_id = command.get("id") or ""
    usage = command.get("usage") or ""
    description = command.get("description") or command_id

    tool_name = "unity_" + command_id.replace(".", "_").replace("-", "_")
    parts = [description]
    if usage:
        parts.append("参数：%s" % usage)
    parts.append("对应指令 ID：`%s`。" % command_id)
    if command.get("editorOnly"):
        parts.append("（仅编辑器可用）")
    if command.get("requiresConsent"):
        parts.append("⚠ 敏感操作：执行前 Unity 会弹真模态对话框，必须人点「允许」才执行 —— "
                     "对话框不点会一直等（编辑器主线程阻塞，没有自动超时）；"
                     "拒绝则本条不执行并中止当批后续指令；控制台勾选「全权委托」后可跳过确认。")

    schema = {
        "type": "object",
        "properties": usage_to_schema(usage),
        # usage 不区分必填/可选，这里一律宽松；漏参数会由服务端返回明确的参数错误
        "additionalProperties": True,
    }
    return {
        "name": tool_name,
        "description": " ".join(parts),
        "inputSchema": schema,
    }, command_id


# ---------------------------------------------------------------- 元工具


def _text_result(text, is_error=False):
    return {"content": [{"type": "text", "text": text}], "isError": bool(is_error)}


def _json_text(value):
    return json.dumps(value, ensure_ascii=False, indent=2)


# 所有元工具共用的寻址参数。描述要写清"三种写法 + 省略时的行为"，
# 因为模型只能靠这段话决定该不该带 target。
TARGET_PARAM = {
    "type": "string",
    "description": "操作哪个 Unity 工程，三种写法任选：工程名（如 MyUnityProject）、"
                   "工程根绝对路径（含 ProjectSettings/ 的那层）、"
                   "或 .workbuddy/UnityEditCommand/.http-info.<hash> 的绝对路径。"
                   "省略时：只开着一个工程就自动用它；多个并存会报错并列出候选；"
                   "显式指定过一次后，后续省略会沿用上一次的选择。",
}


def _summarize_execute(payload):
    """把执行报告压成"摘要 + 完整 JSON"两段，模型先读摘要就够判断成败。"""
    ok = payload.get("ok")
    dry_run = payload.get("dryRun")
    total = payload.get("total", 0)
    succeeded = payload.get("succeeded", 0)
    failed = payload.get("failed", 0)
    skipped = payload.get("skipped", 0)
    elapsed = payload.get("elapsedMs", 0)

    # dry-run 必须显眼：否则模型会误以为副作用已经发生
    head = "执行成功" if ok else "⚠ 执行失败"
    if dry_run:
        head = "dry-run（未真正执行，仅校验）"

    lines = [
        "**%s** 共 %s 条：成功 %s / 失败 %s / 跳过 %s，耗时 %s ms"
        % (head, total, succeeded, failed, skipped, elapsed)
    ]

    summary = payload.get("summary")
    if summary and not dry_run:
        lines.append("结论：%s" % summary)

    # DOC-001 后续：失败清单放在逐条记录之前。records 一长，ok:false 很容易被上下文淹没
    if not ok:
        failures = payload.get("failures") or []
        if failures:
            lines.append("")
            lines.append("**失败的指令（修完再重试）：**")
            for item in failures:
                lines.append("  ✗ #%s %s [%s] %s" % (
                    item.get("index"), item.get("id"), item.get("status"), item.get("message", "")))
        lines.append("")

    for record in payload.get("records") or []:
        status = record.get("status")
        mark = "OK" if status == "Success" else ("SKIP" if status == "Skipped" else "FAIL")
        lines.append("  #%s %s [%s] %s"
                     % (record.get("index"), record.get("id"), mark, record.get("message", "")))

    lines.append("")
    lines.append(_json_text(payload))
    return "\n".join(lines)


def tool_status(arguments):
    try:
        bridge = bridge_for(arguments)
        payload = bridge.status()
    except (BridgeError, SelectorError) as error:
        return _text_result(str(error), is_error=True)

    lines = [
        "工程 %s｜端口 %s｜编译中 %s｜刷新中 %s｜队列 %s｜忙碌 %s"
        % (bridge.label, payload.get("port"), payload.get("compiling"), payload.get("updating"),
           payload.get("queue"), payload.get("busy")),
    ]
    current = payload.get("currentCommandId") or ""
    if current:
        lines.append("正在执行：%s（已 %s ms）" % (current, payload.get("currentElapsedMs")))
    else:
        lines.append("当前空闲")

    compile_state = payload.get("lastCompile") or {}
    if compile_state.get("finished"):
        if compile_state.get("ok"):
            lines.append("上次编译：成功（%s 个警告）" % compile_state.get("warningCount", 0))
        else:
            lines.append(
                "上次编译：**失败**（%s 个错误，%s 个警告）——明细用 unity_asset_compileReport 读取"
                % (compile_state.get("errorCount", 0), compile_state.get("warningCount", 0)))
    elif compile_state:
        lines.append("上次编译：尚未完成过一次")
    identity = payload.get("identity") or {}
    if isinstance(identity, dict) and identity:
        lines.append("工程 %s｜pid %s｜role %s"
                     % (identity.get("projectName") or identity.get("projectPath"),
                        identity.get("pid"), identity.get("role")))
    lines.append("")
    lines.append(_json_text(payload))
    return _text_result("\n".join(lines))


def tool_targets(arguments):
    """列出可操作的 Unity 工程。

    索引为空是一个**有诊断价值**的状态：说明服务端还是旧版本（没有 GlobalTargetRegistry），
    或者所有编辑器都关着。这两种情况的处理方式完全不同，所以输出要分开说。
    """
    want_probe = bool(arguments.get("probe"))
    directory = send.global_targets_dir()
    items = send.list_global_targets()
    items.sort(key=lambda item: item.get("updatedUtc") or "", reverse=True)

    lines = []
    if not items:
        lines.append("全局索引为空：%s" % directory)
        lines.append("")
        lines.append("两种可能：")
        lines.append("  1) 当前工程装的是旧版服务端（没有 GlobalTargetRegistry），只能单工程模式运作；")
        lines.append("  2) 所有 Unity 编辑器都没开。")
        lines.append("")
        lines.append("无论哪种，都可以直接调用 unity_status 试试默认目标通不通。")
        return _text_result("\n".join(lines))

    sticky = POOL.sticky_value()
    sticky_path = None
    if sticky and sticky[0] in ("root", "info"):
        sticky_path = os.path.normcase(os.path.normpath(sticky[1]))

    lines.append("发现 %d 个工程（索引目录 %s）：" % (len(items), directory))
    for item in items:
        identity = item.get(send.IDENTITY_KEY) or {}
        path = (identity.get("projectPath") or "").strip()
        name = identity.get("projectName") or (os.path.basename(path) if path else "?")
        stamp = (item.get("updatedUtc") or "")[:19].replace("T", " ")

        marks = []
        if want_probe:
            if not path:
                marks.append("索引里没有工程路径")
            else:
                try:
                    POOL._get_root(path).target()
                    marks.append("可达")
                except (BridgeError, SelectorError):
                    marks.append("**不可达**")
        if sticky_path and path and os.path.normcase(os.path.normpath(path)) == sticky_path:
            marks.append("当前目标")

        lines.append("  %-24s %s" % (name, path or "?"))
        tail = "      端口 %s ｜ 更新 %s" % (item.get("url") or item.get("port") or "?", stamp)
        if marks:
            tail += " ｜ " + "，".join(marks)
        lines.append(tail)

    lines.append("")
    lines.append("用法：给任意工具加 target 参数（工程名 / 工程根 / .http-info 绝对路径均可）。")
    lines.append("省略 target 时：只有一个活工程就自动用它；多个并存会要求指明。")
    return _text_result("\n".join(lines))


def tool_list_commands(arguments):
    """列指令。

    默认只输出「id + 一句描述」，参数说明要显式要（verbose）。原因很实在：
    75 条指令带上参数说明是 ~2 万字符，而模型多数时候只需要知道"有这么条指令"，
    真正关心参数时它已经能按 prefix 把范围缩到十来条了。
    """
    try:
        bridge = bridge_for(arguments)
        commands = bridge.commands(refresh=True)
    except (BridgeError, SelectorError) as error:
        return _text_result(str(error), is_error=True)

    prefix = (arguments.get("prefix") or "").strip().lower()
    keyword = (arguments.get("keyword") or "").strip().lower()
    verbose = bool(arguments.get("verbose"))

    total = len(commands)
    if prefix:
        commands = [c for c in commands if (c.get("id") or "").lower().startswith(prefix)]
    if keyword:
        commands = [c for c in commands
                    if keyword in (c.get("id") or "").lower()
                    or keyword in (c.get("description") or "").lower()]

    if not commands:
        return _text_result(
            "工程 %s 里没有匹配的指令（prefix=%r keyword=%r）。去掉过滤条件再试。"
            % (bridge.label, prefix, keyword))

    filters = []
    if prefix:
        filters.append("prefix=" + prefix)
    if keyword:
        filters.append("keyword=" + keyword)
    head = "工程 %s：%d 条指令" % (bridge.label, total)
    if filters:
        head += "（过滤 %s → %d 条）" % (" ".join(filters), len(commands))

    lines = [head + "："]
    width = max([len(item.get("id", "")) for item in commands] or [0])
    for item in commands:
        flag = "  [编辑器]" if item.get("editorOnly") else ""
        lines.append("  %-*s  %s%s"
                     % (width, item.get("id", "?"), item.get("description", ""), flag))
        if verbose:
            usage = item.get("usage") or ""
            if usage:
                lines.append("      %s参数: %s" % (" " * width, usage))

    if not verbose:
        lines.append("")
        lines.append("需要参数说明时加 verbose=true（并建议先用 prefix 收窄范围）。")

    return _text_result("\n".join(lines))


def tool_execute(arguments):
    body = None

    raw_json = arguments.get("json")
    if isinstance(raw_json, str) and raw_json.strip():
        body = raw_json
    else:
        commands = arguments.get("commands")
        if isinstance(commands, dict):
            # 也接受整包 {"commands":[...]} 写法
            commands = commands.get("commands", commands)
        if isinstance(commands, list):
            body = json.dumps({"commands": commands}, ensure_ascii=False)
        elif isinstance(commands, dict):
            body = json.dumps(commands, ensure_ascii=False)

    if not body:
        return _text_result(
            "缺少指令：需要 `commands`（数组，每项 {id, params}）或 `json`（完整指令 JSON 文本）。",
            is_error=True)

    try:
        bridge = bridge_for(arguments)
        payload = bridge.execute(
            body,
            dry_run=bool(arguments.get("dryRun")),
            request_id=arguments.get("requestId") or None,
        )
    except (BridgeError, SelectorError) as error:
        return _text_result(str(error), is_error=True)

    return _text_result(_summarize_execute(payload), is_error=not payload.get("ok"))


def tool_cancel(arguments):
    try:
        bridge = bridge_for(arguments)
        payload = bridge.cancel()
    except (BridgeError, SelectorError) as error:
        return _text_result(str(error), is_error=True)
    return _text_result(payload.get("message") or "已发送取消请求")


def _fetch_compile_report(bridge, limit=50):
    """读取服务端的编译报告；旧版服务端或正在编译时返回 None。"""
    body = json.dumps({
        "commands": [{
            "id": "asset.compileReport",
            "params": {"includeMessages": True, "limit": limit},
        }]
    }, ensure_ascii=False)

    try:
        payload = bridge.execute(body)
    except BridgeError as error:
        # 编译期间服务端会回 503：这是辅助查询，不该因此中断主流程
        if "503" in str(error):
            return None
        raise

    records = payload.get("records") or []
    if not records:
        return None

    record = records[0]
    if record.get("status") != "Success":
        return None

    data = record.get("data")
    # 新版服务端把 JsonValue 原样嵌入（对象）；旧版会退化成字符串
    return data if isinstance(data, dict) else None


def _execute_with_busy_retry(body, bridge, max_wait=90.0):
    """发 execute；若撞上「正在编译/刷新」(503)，等一会儿再试。

    这个竞态很常见：AI 写完 .cs，Unity 的 Auto Refresh 已经开始编译，
    此时 asset.compile 必然被 503 拒绝——但那恰恰说明编译正在进行，等它就好。
    """
    deadline = time.time() + max_wait
    while True:
        try:
            return bridge.execute(body)
        except BridgeError as error:
            if "503" not in str(error) or time.time() > deadline:
                raise
            time.sleep(0.6)


def _is_busy(bridge):
    try:
        status = bridge.status()
    except BridgeError:
        return False
    return bool(status.get("compiling") or status.get("updating"))


def _format_compile_errors(report, limit=15):
    lines = []
    for message in report.get("messages") or []:
        if (message.get("type") or "").lower() != "error":
            continue
        lines.append("  %s(%s,%s): %s" % (
            message.get("file") or "?",
            message.get("line"),
            message.get("column"),
            (message.get("message") or "").strip(),
        ))
        if len(lines) >= limit:
            lines.append("  …（更多错误见 unity_asset_compileReport）")
            break
    return "\n".join(lines)


def tool_compile_and_wait(arguments):
    """请求重编译，等到编辑器可交互，并**回传编译结果**。

    这是"AI 编辑-编译-纠错"闭环的关键：只等空闲是不够的——编译失败时编辑器同样会空闲，
    若不读编译报告，调用方会误以为成功，后续指令全部跑在旧程序集上。
    """
    timeout = arguments.get("timeout")
    try:
        timeout = float(timeout) if timeout is not None else 180.0
    except (TypeError, ValueError):
        timeout = 180.0
    timeout = max(1.0, min(timeout, 600.0))

    clean = bool(arguments.get("clean"))

    try:
        bridge = bridge_for(arguments)
    except SelectorError as error:
        return _text_result(str(error), is_error=True)

    before_finished = None
    already_compiling = _is_busy(bridge)

    if already_compiling:
        # 刚写完 .cs 时 Unity 的 Auto Refresh 可能已经在编了：
        # 这正是我们要等的那次编译，直接等它，别再发 asset.compile（会被 503 拒）。
        log("检测到 Unity 已在编译，直接等待该次编译完成")
    else:
        # 编译前先记下"上一次编译完成的时刻"，用于判断这次是否真的触发了新编译
        try:
            before = _fetch_compile_report(bridge, limit=0)
        except BridgeError as error:
            return _text_result(str(error), is_error=True)
        before_finished = (before or {}).get("finishedUtc")

        body = json.dumps({"commands": [{"id": "asset.compile", "params": {"clean": clean}}]},
                          ensure_ascii=False)
        try:
            payload = _execute_with_busy_retry(body, bridge)
            if not payload.get("ok"):
                return _text_result(_summarize_execute(payload), is_error=True)
        except BridgeError as error:
            return _text_result(str(error), is_error=True)

    deadline = time.time() + timeout
    # 编译请求是异步的：先给它一点时间进入 compiling，再判定"已结束"
    time.sleep(0.4)
    idle_hits = 0
    seen_compiling = False
    while time.time() < deadline:
        try:
            status = bridge.status()
        except BridgeError:
            # 域重载期间服务会短暂断开，这是正常现象，重建端点继续等
            bridge.invalidate()
            seen_compiling = True
            time.sleep(0.5)
            continue

        if status.get("compiling") or status.get("updating"):
            seen_compiling = True
            idle_hits = 0
        else:
            idle_hits += 1
            if idle_hits >= 2:
                break
        time.sleep(0.4)
    else:
        return _text_result("等待编译超时（%.0f 秒）。编译可能仍在进行，可用 unity_status 复查。" % timeout,
                            is_error=True)

    # 读回结果
    try:
        report = _fetch_compile_report(bridge, limit=50)
    except BridgeError as error:
        return _text_result("编译结束，但读取编译报告失败：%s" % error, is_error=True)

    if report is None:
        # 旧版服务端：只能确认"已空闲"，无法判断成败
        return _text_result(
            "编译已结束（编辑器空闲）。\n"
            "注意：当前 Unity 端版本较旧，未提供 asset.compileReport，"
            "无法确认编译是否成功——请升级服务端，或自行检查 Editor.log 里的 error CS。",
            is_error=not seen_compiling)

    if not report.get("finished"):
        return _text_result("编译已结束，但没有拿到有效结果（可能没有触发实际编译）。", is_error=False)

    if before_finished and report.get("finishedUtc") == before_finished:
        return _text_result(
            "没有检测到新的编译：文件可能未变更，或 Unity 跳过了本次编译。",
            is_error=False)

    if report.get("ok"):
        return _text_result(
            "编译成功（%s 个警告，耗时 %s ms）。可以继续下发指令。"
            % (report.get("warningCount", 0), round(report.get("durationMs") or 0)))

    detail = _format_compile_errors(report)
    return _text_result(
        "编译失败：%s 个错误，%s 个警告。\n%s\n\n"
        "请修改上述文件后重新调用本工具；不要在下发指令前跳过这一步。"
        % (report.get("errorCount", 0), report.get("warningCount", 0), detail or "（未取到明细）"),
        is_error=True)


META_TOOLS = [
    {
        "name": "unity_status",
        "description": "查看 Unity 编辑器状态：是否在编译/刷新、队列长度、当前正在执行的指令与已耗时、"
                       "上次编译成功与否、以及连的是哪个工程。"
                       "排查『连不上』『指令没反应』『改完代码没生效』时先调它。",
        "inputSchema": {
            "type": "object",
            "properties": {"target": TARGET_PARAM},
            "additionalProperties": False,
        },
    },
    {
        "name": "unity_targets",
        "description": "列出当前开着（以及最近开过）的 Unity 工程，含工程名、路径、端口与更新时间。"
                       "同时开着多个工程、或想确认默认会操作哪个时先调它。"
                       "加 probe=true 会逐个探活（慢一些，但能筛掉已经关掉的残留条目）。",
        "inputSchema": {
            "type": "object",
            "properties": {
                "probe": {"type": "boolean", "description": "是否逐个探测可达性（默认 false，只读索引）。"},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "unity_list_commands",
        "description": "列出该 Unity 工程已注册的指令 ID 与一句话说明。"
                       "不确定指令叫什么时先调它；加 prefix 可只列某个域（如 gameobject / prefab / scene）。"
                       "需要参数说明时加 verbose=true。",
        "inputSchema": {
            "type": "object",
            "properties": {
                "target": TARGET_PARAM,
                "prefix": {"type": "string",
                           "description": "只列指令 ID 以此开头的（如 \"prefab.\" 或 \"gameobject\"）。"},
                "keyword": {"type": "string",
                            "description": "在指令 ID 与描述里按子串过滤（中文也可）。"},
                "verbose": {"type": "boolean",
                            "description": "是否附上每条指令的参数说明（默认 false，输出会长很多）。"},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "unity_execute",
        "description": "向 Unity 编辑器下发一批指令并同步等待执行报告。"
                       "多条有依赖关系的指令应放进同一次调用（服务端按顺序串行执行），比逐条调用更可靠。"
                       "返回包含摘要与完整 JSON 报告。",
        "inputSchema": {
            "type": "object",
            "properties": {
                "target": TARGET_PARAM,
                "commands": {
                    "type": "array",
                    "description": "指令数组，每项形如 {\"id\": \"gameobject.create\", \"params\": {...}}。"
                                   "也接受 {\"commands\": [...]} 整包。",
                    "items": {"type": "object"},
                },
                "json": {
                    "type": "string",
                    "description": "逃生舱：直接给完整指令 JSON 文本（优先级高于 commands）。",
                },
                "dryRun": {
                    "type": "boolean",
                    "description": "只解析与查表，不真正执行（零副作用），用于验证指令 ID 与参数。",
                },
                "requestId": {
                    "type": "string",
                    "description": "幂等键：同一 ID 重发返回缓存结果、不产生二次副作用（服务端缓存 5 分钟）。",
                },
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "unity_cancel",
        "description": "请求取消当前批次。协作式：正在执行的单条指令无法被打断，"
                       "会在下一条指令前退出。真卡死只能重启编辑器。",
        "inputSchema": {
            "type": "object",
            "properties": {"target": TARGET_PARAM},
            "additionalProperties": False,
        },
    },
    {
        "name": "unity_compile_and_wait",
        "description": "触发 Unity 重新编译脚本，等到编辑器重新空闲，**并回传编译结果**。"
                       "编译失败时返回 isError=true 与「文件(行,列): 编译器原文」，据此改代码再重试。"
                       "改完 .cs 后必须走这一步：只等空闲不读结果会漏掉编译失败，"
                       "后续指令将全部运行在旧程序集上。",
        "inputSchema": {
            "type": "object",
            "properties": {
                "target": TARGET_PARAM,
                "clean": {"type": "boolean", "description": "是否清空编译缓存做干净重建（默认 false）。"},
                "timeout": {"type": "integer", "description": "最长等待秒数（默认 180，上限 600）。"},
            },
            "additionalProperties": False,
        },
    },
]

META_HANDLERS = {
    "unity_status": tool_status,
    "unity_targets": tool_targets,
    "unity_list_commands": tool_list_commands,
    "unity_execute": tool_execute,
    "unity_cancel": tool_cancel,
    "unity_compile_and_wait": tool_compile_and_wait,
}


def _call_domain(tool_name, actions, arguments):
    """group 模式的域工具调用：把 action 映射回具体指令，再走同一条 execute 通道。"""
    action = (arguments.get("action") or "").strip()
    command_id = actions.get(action)
    if command_id is None:
        return _text_result(
            "未知 action：%r。%s 可用的 action：%s"
            % (action, tool_name, "、".join(sorted(actions))), is_error=True)

    params = arguments.get("params")
    if params is None:
        params = {}
    if not isinstance(params, dict):
        return _text_result("params 必须是 JSON 对象（该指令的参数），当前是 %s。"
                            % type(params).__name__, is_error=True)

    body = json.dumps({"commands": [{"id": command_id, "params": params}]}, ensure_ascii=False)
    try:
        bridge = bridge_for(arguments)
        payload = bridge.execute(body, dry_run=bool(arguments.get("dryRun")))
    except (BridgeError, SelectorError) as error:
        return _text_result(str(error), is_error=True)

    return _text_result(_summarize_execute(payload), is_error=not payload.get("ok"))


# ---------------------------------------------------------------- 工具注册表（三种折叠模式）
#
# 为什么要折叠：75 条指令全量铺成 typed 工具是 29k 字符 ≈ 1 万 token，
# 而且每个工程各一条 server 时这份成本要乘 N——客户端上下文直接被吃光，
# 工具选择的准确率也随之下降。三档模式对应三种取舍：
#
#   meta  （默认）6 个元工具，~1.9k 字符。模型靠 unity_list_commands 按需查指令名。
#   group         元工具 + 按域合并的 15 个工具，action 用 enum 列全。不用查也能选。
#   full          老行为，一条指令一个工具。留给"就是要细粒度 schema"的场景。
#
# 默认 meta 是算过账的选择：省下 90%+ 的常驻成本，代价只是首次用某个域时多查一次。

TOOL_MODES = ("meta", "group", "full")
DEFAULT_TOOL_MODE = "meta"

# 域工具合并阈值：指令数 >= 2 的域单独成工具，不足的并入 misc
MIN_DOMAIN_SIZE = 2


def build_domains(commands):
    """把指令按 ID 前缀分域；只有一条指令的域合并进 misc。

    返回 OrderedDict：domain -> [(action, command_id, command_dict)]，
    misc 的 action 直接用完整指令 ID（因为它下面没有共同前缀）。
    """
    groups = {}
    order = []
    for command in commands:
        command_id = (command.get("id") or "").strip()
        if not command_id:
            continue
        head = command_id.split(".")[0]
        if head not in groups:
            groups[head] = []
            order.append(head)
        groups[head].append((command_id, command))

    domains = {}
    misc = []
    for head in order:
        items = groups[head]
        if len(items) >= MIN_DOMAIN_SIZE:
            actions = []
            for command_id, command in items:
                if command_id.startswith(head + "."):
                    action = command_id[len(head) + 1:]
                else:
                    action = command_id
                actions.append((action, command_id, command))
            domains[head] = actions
        else:
            misc.extend(items)

    if misc:
        domains["misc"] = [(command_id, command_id, command) for command_id, command in misc]

    return domains


# 域工具用的短版寻址参数：15 个工具各带一份，长描述会白白吃掉几千字符。
TARGET_PARAM_SHORT = {
    "type": "string",
    "description": "目标工程（工程名 / 工程根 / .http-info 路径），省略时用默认工程。",
}


def domain_to_tool(domain, actions):
    """把一个域折叠成单个 MCP 工具：{action, params, target}。

    描述里**不再重复列举** action：inputSchema 的 enum 已经把它们全列出来了，
    再写一遍等于每个域工具白白多出一两百字符。
    """
    names = [action for action, _, _ in actions]
    if domain == "misc":
        description = ("零散指令（各自域里只有一条的那些）。action 直接写完整指令 ID，"
                       "params 为该指令的参数对象。")
    else:
        description = ("%s 域指令。params 是对应指令的参数对象；"
                       "要参数说明用 unity_list_commands(prefix=\"%s.\", verbose=true)。"
                       % (domain, domain))

    return {
        "name": "unity_" + domain,
        "description": description,
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {
                    "type": "string",
                    "enum": names,
                    "description": "要执行的指令。",
                },
                "params": {
                    "type": "object",
                    "description": "该 action 对应指令的参数；不需要参数时省略。",
                },
                "target": TARGET_PARAM_SHORT,
            },
            "required": ["action"],
            "additionalProperties": False,
        },
    }


class ToolRegistry(object):
    """元工具恒在；typed / 域工具从服务端 usage 动态生成（单一来源，不手写漂移）。"""

    def __init__(self, mode=DEFAULT_TOOL_MODE):
        self.mode = mode if mode in TOOL_MODES else DEFAULT_TOOL_MODE
        self._lock = threading.Lock()
        self._typed = []                 # [(tool_def, command_id)]          full 模式
        self._typed_map = {}             # tool_name -> command_id           full 模式
        self._domains = []               # [tool_def]                        group 模式
        self._domain_map = {}            # tool_name -> {action: command_id} group 模式
        self._built_at = 0.0
        self._build_error = None

    def _degrade(self, reason):
        """把折叠工具清空、只留元工具，并记下原因（下次刷新会自动重试）。

        **为什么要有这条退路（BUG-011）**：端点发现一失败，`commands()` 就抛异常；
        早先只捕获 `BridgeError`，于是任何**非预期**异常（实测是 `_DiscoverArgs` 少一个字段的
        `AttributeError`）都会一路冒到 JSON-RPC 层，变成 `-32603 内部错误`，
        客户端侧表现是「这个工程的 MCP 整个没了」——连 `unity_status` 都调不到，
        排查时极易误判成包丢了。退化成"只有元工具"再差也还能自证状态。
        """
        self._build_error = reason
        self._typed = []
        self._typed_map = {}
        self._domains = []
        self._domain_map = {}
        self._built_at = time.time()

    def _fetch_commands(self):
        bridge = POOL.get(None)
        return bridge.commands()

    def _rebuild(self):
        try:
            commands = self._fetch_commands()
        except (BridgeError, SelectorError) as error:
            self._degrade(str(error))
            return
        except Exception as error:  # noqa: BLE001 —— 任何异常都不许让 tools/list 整体失败
            self._degrade("生成工具时出现非预期异常：%r（本次只提供元工具）" % (error,))
            log("tools/list 退化为仅元工具：%r" % (error,))
            return

        typed = []
        mapping = {}
        domains = []
        domain_map = {}

        if self.mode == "full":
            for command in commands:
                command_id = (command.get("id") or "").strip()
                if not command_id:
                    continue
                tool, cid = command_to_tool(command)
                name = tool["name"]
                if name in META_HANDLERS or name in mapping:
                    # 命名撞车（极不可能）：跳过，通用 execute 仍可调用该指令
                    log("跳过重复的工具名：%s（指令 %s）" % (name, cid))
                    continue
                typed.append((tool, cid))
                mapping[name] = cid
        elif self.mode == "group":
            for domain, actions in build_domains(commands).items():
                tool = domain_to_tool(domain, actions)
                name = tool["name"]
                if name in META_HANDLERS or name in domain_map:
                    log("跳过重复的域工具名：%s" % name)
                    continue
                domains.append(tool)
                domain_map[name] = {action: cid for action, cid, _ in actions}

        self._typed = typed
        self._typed_map = mapping
        self._domains = domains
        self._domain_map = domain_map
        self._build_error = None
        self._built_at = time.time()

    def tools(self, force=False):
        # meta 模式不需要连 Unity、也不需要指令表：tools/list 必须永远秒回，
        # 客户端刚启动时编辑器可能正关着，这时让列表失败是最糟的体验。
        if self.mode == "meta":
            return list(META_TOOLS)

        with self._lock:
            stale = force or (time.time() - self._built_at > COMMANDS_TTL)
            if stale:
                self._rebuild()
            # 元工具优先，折叠工具其次（让模型先看到通用入口）
            return list(META_TOOLS) + [tool for tool, _ in self._typed] + list(self._domains)

    def domain_actions(self, tool_name):
        """group 模式：取某个域工具的 action → command_id 映射。"""
        if self.mode != "group":
            return None
        with self._lock:
            if tool_name in self._domain_map:
                return self._domain_map[tool_name]
        with self._lock:
            self._rebuild()
            return self._domain_map.get(tool_name)

    def command_id_for(self, tool_name):
        if self.mode != "full":
            return None
        with self._lock:
            if tool_name in self._typed_map:
                return self._typed_map[tool_name]
        # 缓存里没有：可能是新注册的指令，强制刷新一次再找
        with self._lock:
            self._rebuild()
            return self._typed_map.get(tool_name)


REGISTRY = ToolRegistry()


# ---------------------------------------------------------------- JSON-RPC 处理


def _error(msg_id, code, message, data=None):
    payload = {"jsonrpc": "2.0", "id": msg_id, "error": {"code": code, "message": message}}
    if data is not None:
        payload["error"]["data"] = data
    return payload


def _result(msg_id, result):
    return {"jsonrpc": "2.0", "id": msg_id, "result": result}


def _negotiate_version(requested):
    if requested in SUPPORTED_PROTOCOLS:
        return requested
    return DEFAULT_PROTOCOL


def handle_request(message):
    """处理一条带 id 的请求，返回响应 dict。"""
    msg_id = message.get("id")
    method = message.get("method")
    params = message.get("params") or {}

    if method == "initialize":
        return _result(msg_id, {
            "protocolVersion": _negotiate_version(params.get("protocolVersion")),
            "capabilities": {"tools": {"listChanged": False}},
            "serverInfo": {"name": SERVER_NAME, "version": SERVER_VERSION},
            "instructions": INSTRUCTIONS,
        })

    if method == "ping":
        return _result(msg_id, {})

    if method == "tools/list":
        return _result(msg_id, {"tools": REGISTRY.tools()})

    if method == "tools/call":
        name = params.get("name")
        arguments = params.get("arguments") or {}

        if not isinstance(name, str) or not name:
            return _error(msg_id, -32602, "tools/call 缺少 name")

        if not isinstance(arguments, dict):
            return _error(msg_id, -32602, "tools/call 的 arguments 必须是对象")

        handler = META_HANDLERS.get(name)
        if handler is not None:
            try:
                return _result(msg_id, handler(arguments))
            except Exception as error:  # noqa: BLE001 —— 任何异常都要变成工具错误，不能让桥崩掉
                log("工具 %s 异常：%r" % (name, error))
                return _result(msg_id, _text_result("工具执行异常：%s" % error, is_error=True))

        # group 模式的域工具：{action, params} → 一条具体指令
        actions = REGISTRY.domain_actions(name)
        if actions is not None:
            return _result(msg_id, _call_domain(name, actions, arguments))

        # 动态生成的 typed 工具（仅 full 模式）
        command_id = REGISTRY.command_id_for(name)
        if not command_id:
            hint = "可调用 unity_list_commands 查看可用指令。"
            if REGISTRY.mode != "full":
                hint += ("当前工具模式是 %s，单条指令请用 unity_execute 下发，"
                         "或用对应的 unity_<域> 工具。" % REGISTRY.mode)
            return _error(msg_id, -32602, "未知工具：%s。%s" % (name, hint))

        body = json.dumps({"commands": [{"id": command_id, "params": arguments}]},
                          ensure_ascii=False)
        try:
            payload = BRIDGE.execute(body, dry_run=bool(arguments.get("__dryRun")))
        except BridgeError as error:
            return _result(msg_id, _text_result(str(error), is_error=True))

        return _result(msg_id, _text_result(_summarize_execute(payload),
                                            is_error=not payload.get("ok")))

    return _error(msg_id, -32601, "未知方法：%s" % method)


def handle_notification(message):
    """通知不需要响应；这里只做最低限度的日志。"""
    method = message.get("method")
    if method == "notifications/initialized":
        log("客户端初始化完成")
    elif method == "notifications/cancelled":
        # 客户端取消某次请求：本桥的请求是同步执行的，直接作用于 Unity 侧最接近的语义
        log("收到取消通知：%s" % json.dumps(message.get("params") or {}, ensure_ascii=False))
        try:
            POOL.get(None).cancel()
        except (BridgeError, SelectorError) as error:
            log("转发取消失败：%s" % error)
    elif method:
        log("收到未处理的通知：%s" % method)


# ---------------------------------------------------------------- stdio 主循环


def _write_message(payload, lock):
    # 关键：紧凑 JSON（无内嵌换行）+ 显式 \n 分隔，符合 MCP stdio 传输要求
    text = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
    if "\n" in text or "\r" in text:
        # json.dumps 不会产生裸换行（会被转义成 \\n），这里只是防御
        text = text.replace("\r", "\\r").replace("\n", "\\n")
    with lock:
        sys.stdout.write(text + "\n")
        sys.stdout.flush()


def _process_line(line, lock):
    try:
        message = json.loads(line)
    except ValueError as error:
        _write_message(_error(None, -32700, "JSON 解析失败：%s" % error), lock)
        return

    if not isinstance(message, dict):
        _write_message(_error(None, -32600, "请求必须是 JSON 对象"), lock)
        return

    if "id" not in message:
        handle_notification(message)
        return

    try:
        response = handle_request(message)
    except Exception as error:  # noqa: BLE001
        log("处理请求异常：%r" % error)
        response = _error(message.get("id"), -32603, "内部错误：%s" % error)

    _write_message(response, lock)


def _setup_io():
    """强制 UTF-8 且用 \\n 分帧。

    Windows 上 sys.stdout 默认可能是 cp936，且文本模式会把 \\n 写成 \\r\\n，
    两者都会让 MCP 客户端解析失败。
    """
    try:
        sys.stdin.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    try:
        sys.stdout.reconfigure(encoding="utf-8", newline="\n")
    except Exception:
        pass
    try:
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass


_ARG_NAMES = {
    "--project-root": "project_root",
    "--info": "info_path",
    "--tool-mode": "tool_mode",
    "--default-target": "default_target",
}


def _parse_bridge_args(argv):
    """极简参数解析：--project-root / --info / --tool-mode / --default-target。

    刻意不用 argparse：本文件由 MCP 客户端拉起，参数极少，且 argparse 的
    报错会写 stderr 后 exit(2)，在 stdio 场景下表现为"服务启动即退出"，排查困难。
    未知参数一律忽略，保证向前兼容（以后加参数不会让旧配置失效）。
    """
    options = {name: None for name in _ARG_NAMES.values()}
    i = 0
    while i < len(argv):
        arg = argv[i]
        key = None
        value = None
        if "=" in arg:
            head, tail = arg.split("=", 1)
            if head in _ARG_NAMES:
                key = _ARG_NAMES[head]
                value = tail
        elif arg in _ARG_NAMES and i + 1 < len(argv):
            key = _ARG_NAMES[arg]
            value = argv[i + 1]

        if key is None:
            i += 1
            continue

        options[key] = value
        i += 1 if "=" in arg else 2

    return options


def main():
    global REGISTRY

    _setup_io()

    options = _parse_bridge_args(sys.argv[1:])
    project_root = options["project_root"]
    info_path = options["info_path"]
    default_target = options["default_target"]

    tool_mode = (options["tool_mode"] or os.environ.get("UNITY_MCP_TOOL_MODE")
                 or DEFAULT_TOOL_MODE).strip().lower()
    if tool_mode not in TOOL_MODES:
        log("未知 tool-mode：%r，回退到 %s（可选：%s）"
            % (tool_mode, DEFAULT_TOOL_MODE, " / ".join(TOOL_MODES)))
        tool_mode = DEFAULT_TOOL_MODE

    # send 的全局状态总要初始化一次：传统发现路径（没装全局索引的旧服务端）依赖它，
    # 不初始化会让 COMMAND_DIR 停在 None，一调发现就 TypeError。
    # 包化安装后脚本可能不在 .workbuddy 下，此时 --project-root 就是唯一线索。
    send.configure(project_root)
    if info_path:
        send.DEFAULT_INFO = info_path

    # 默认目标优先级：--default-target > --project-root > --info
    # （前两个都是显式声明，default-target 语义更专一，故排最前）
    if default_target:
        kind, value = parse_selector(default_target)
        if kind:
            POOL.set_default(kind, value)
            log("默认目标：%s %s" % (kind, value))
        else:
            log("--default-target 为空，忽略")
    elif project_root:
        POOL.set_default("root", os.path.abspath(project_root))
        log("默认目标工程根：%s" % os.path.abspath(project_root))
    elif info_path:
        POOL.set_default("info", os.path.abspath(info_path))
        log("默认目标信息文件：%s" % os.path.abspath(info_path))

    if tool_mode != REGISTRY.mode:
        REGISTRY = ToolRegistry(tool_mode)

    log("启动 %s v%s（Python %s，tool-mode=%s）"
        % (SERVER_NAME, SERVER_VERSION, sys.version.split()[0], tool_mode))
    log("命令目录：%s" % send.COMMAND_DIR)
    log("全局目标目录：%s" % send.global_targets_dir())

    write_lock = threading.Lock()
    # 每个请求独立线程：慢的 execute 不能挡住 cancel / status 这类需要"穿透"的调用
    pool = concurrent.futures.ThreadPoolExecutor(max_workers=8)

    try:
        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            pool.submit(_process_line, line, write_lock)
    except KeyboardInterrupt:
        log("收到中断，退出")
    finally:
        pool.shutdown(wait=True)
        log("退出")


if __name__ == "__main__":
    main()
