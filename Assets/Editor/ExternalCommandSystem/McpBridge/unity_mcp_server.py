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
4. **typed 工具由服务端单一来源生成**：从 `/v1/commands` 的 `usage` 解析出 JSON Schema，
   不在这里手写 24 份描述，避免与 `CommandAttribute` 漂移。

用法（由 MCP 客户端拉起，一般不手动执行）：
    python unity_mcp_server.py

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
SERVER_VERSION = "1.0.0"

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
    "这是 Unity 编辑器（CivilizationRevival 工程）的指令通道。"
    "调用任何工具前不需要手动启动服务，编辑器启动时会自动拉起 HTTP 桥；"
    "若返回『连不上 Unity』，说明编辑器没开、正在重编译，或服务被手动关了。"
    "unity_status 可查看编辑器状态与当前执行中的指令；"
    "unity_execute 可一次下发多条指令（会按顺序串行执行，比逐条调用更可靠）；"
    "unity_list_commands 可查看全部指令参数说明。"
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
    """send.discover() 需要的参数占位；桥不暴露 --url/--token 等覆盖项。"""

    info = None
    url = None
    token = None
    port = None
    no_resolve = False


class Bridge(object):
    """封装"找到编辑器 + 发请求 + 缓存"三件事。"""

    def __init__(self):
        self._lock = threading.Lock()
        self._target = None
        self._target_at = 0.0
        self._target_failed_at = 0.0
        self._target_error = None
        self._commands = None
        self._commands_at = 0.0

    # ---- 端点

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

    def _discover(self):
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


BRIDGE = Bridge()


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
    payload = BRIDGE.status()
    lines = [
        "端口 %s｜编译中 %s｜刷新中 %s｜队列 %s｜忙碌 %s"
        % (payload.get("port"), payload.get("compiling"), payload.get("updating"),
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


def tool_list_commands(arguments):
    try:
        commands = BRIDGE.commands(refresh=True)
    except BridgeError as error:
        return _text_result(str(error), is_error=True)

    lines = ["已注册 %d 条指令：" % len(commands)]
    width = max([len(item.get("id", "")) for item in commands] or [0])
    for item in commands:
        flag = "  [编辑器]" if item.get("editorOnly") else ""
        lines.append("  %-*s  %s%s" % (width, item.get("id", "?"), item.get("description", ""), flag))
        usage = item.get("usage") or ""
        if usage:
            lines.append("      %s参数: %s" % (" " * width, usage))
    lines.append("")
    lines.append(_json_text(commands))
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
        payload = BRIDGE.execute(
            body,
            dry_run=bool(arguments.get("dryRun")),
            request_id=arguments.get("requestId") or None,
        )
    except BridgeError as error:
        return _text_result(str(error), is_error=True)

    return _text_result(_summarize_execute(payload), is_error=not payload.get("ok"))


def tool_cancel(arguments):
    try:
        payload = BRIDGE.cancel()
    except BridgeError as error:
        return _text_result(str(error), is_error=True)
    return _text_result(payload.get("message") or "已发送取消请求")


def _fetch_compile_report(limit=50):
    """读取服务端的编译报告；旧版服务端或正在编译时返回 None。"""
    body = json.dumps({
        "commands": [{
            "id": "asset.compileReport",
            "params": {"includeMessages": True, "limit": limit},
        }]
    }, ensure_ascii=False)

    try:
        payload = BRIDGE.execute(body)
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


def _execute_with_busy_retry(body, max_wait=90.0):
    """发 execute；若撞上「正在编译/刷新」(503)，等一会儿再试。

    这个竞态很常见：AI 写完 .cs，Unity 的 Auto Refresh 已经开始编译，
    此时 asset.compile 必然被 503 拒绝——但那恰恰说明编译正在进行，等它就好。
    """
    deadline = time.time() + max_wait
    while True:
        try:
            return BRIDGE.execute(body)
        except BridgeError as error:
            if "503" not in str(error) or time.time() > deadline:
                raise
            time.sleep(0.6)


def _is_busy():
    try:
        status = BRIDGE.status()
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

    before_finished = None
    already_compiling = _is_busy()

    if already_compiling:
        # 刚写完 .cs 时 Unity 的 Auto Refresh 可能已经在编了：
        # 这正是我们要等的那次编译，直接等它，别再发 asset.compile（会被 503 拒）。
        log("检测到 Unity 已在编译，直接等待该次编译完成")
    else:
        # 编译前先记下"上一次编译完成的时刻"，用于判断这次是否真的触发了新编译
        try:
            before = _fetch_compile_report(limit=0)
        except BridgeError as error:
            return _text_result(str(error), is_error=True)
        before_finished = (before or {}).get("finishedUtc")

        body = json.dumps({"commands": [{"id": "asset.compile", "params": {"clean": clean}}]},
                          ensure_ascii=False)
        try:
            payload = _execute_with_busy_retry(body)
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
            status = BRIDGE.status()
        except BridgeError:
            # 域重载期间服务会短暂断开，这是正常现象，重建端点继续等
            BRIDGE.invalidate()
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
        report = _fetch_compile_report(limit=50)
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
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
    },
    {
        "name": "unity_list_commands",
        "description": "列出 Unity 侧全部已注册指令及其参数说明。"
                       "不确定某条指令叫什么、要什么参数时先调它。",
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
    },
    {
        "name": "unity_execute",
        "description": "向 Unity 编辑器下发一批指令并同步等待执行报告。"
                       "多条有依赖关系的指令应放进同一次调用（服务端按顺序串行执行），比逐条调用更可靠。"
                       "返回包含摘要与完整 JSON 报告。",
        "inputSchema": {
            "type": "object",
            "properties": {
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
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
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
                "clean": {"type": "boolean", "description": "是否清空编译缓存做干净重建（默认 false）。"},
                "timeout": {"type": "integer", "description": "最长等待秒数（默认 180，上限 600）。"},
            },
            "additionalProperties": False,
        },
    },
]

META_HANDLERS = {
    "unity_status": tool_status,
    "unity_list_commands": tool_list_commands,
    "unity_execute": tool_execute,
    "unity_cancel": tool_cancel,
    "unity_compile_and_wait": tool_compile_and_wait,
}


# ---------------------------------------------------------------- 工具注册表（含动态 typed）


class ToolRegistry(object):
    """元工具恒在；typed 工具从服务端 usage 动态生成（单一来源，不手写漂移）。"""

    def __init__(self):
        self._lock = threading.Lock()
        self._typed = []                 # [(tool_def, command_id)]
        self._typed_map = {}             # tool_name -> command_id
        self._built_at = 0.0
        self._build_error = None

    def _rebuild(self):
        try:
            commands = BRIDGE.commands()
        except BridgeError as error:
            self._build_error = str(error)
            self._typed = []
            self._typed_map = {}
            self._built_at = time.time()
            return

        typed = []
        mapping = {}
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

        self._typed = typed
        self._typed_map = mapping
        self._build_error = None
        self._built_at = time.time()

    def tools(self, force=False):
        with self._lock:
            stale = force or (time.time() - self._built_at > COMMANDS_TTL)
            if stale:
                self._rebuild()
            # 元工具优先，typed 其次（让模型先看到通用入口）
            return list(META_TOOLS) + [tool for tool, _ in self._typed]

    def command_id_for(self, tool_name):
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

        # 动态生成的 typed 工具
        command_id = REGISTRY.command_id_for(name)
        if not command_id:
            return _error(
                msg_id,
                -32602,
                "未知工具：%s。可调用 unity_list_commands 查看可用指令。" % name,
            )

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
            BRIDGE.cancel()
        except BridgeError as error:
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


def _parse_bridge_args(argv):
    """极简参数解析：只认 --project-root / --info。

    刻意不用 argparse：本文件由 MCP 客户端拉起，参数极少，且 argparse 的
    报错会写 stderr 后 exit(2)，在 stdio 场景下表现为"服务启动即退出"，排查困难。
    未知参数一律忽略，保证向前兼容（以后加参数不会让旧配置失效）。
    """
    project_root = None
    info_path = None
    i = 0
    while i < len(argv):
        arg = argv[i]
        if arg == "--project-root" and i + 1 < len(argv):
            project_root = argv[i + 1]
            i += 2
            continue
        if arg.startswith("--project-root="):
            project_root = arg.split("=", 1)[1]
            i += 1
            continue
        if arg == "--info" and i + 1 < len(argv):
            info_path = argv[i + 1]
            i += 2
            continue
        if arg.startswith("--info="):
            info_path = arg.split("=", 1)[1]
            i += 1
            continue
        i += 1
    return project_root, info_path


def main():
    _setup_io()

    # 包化安装后脚本可能不在 .workbuddy 下，此时必须靠 --project-root 显式指明工程根
    project_root, info_path = _parse_bridge_args(sys.argv[1:])
    if project_root:
        send.configure(project_root)
        log("使用显式工程根：%s" % os.path.abspath(project_root))
    if info_path:
        send.DEFAULT_INFO = info_path
        log("使用显式信息文件：%s" % info_path)

    log("启动 %s v%s（Python %s）" % (SERVER_NAME, SERVER_VERSION,
                                     sys.version.split()[0]))
    log("命令目录：%s" % send.COMMAND_DIR)

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
