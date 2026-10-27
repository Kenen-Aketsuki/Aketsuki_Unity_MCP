#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ADB 调试工具的 MCP stdio 桥。

这是一个**独立工具**：不 import、不依赖 `unity-cmd` 的任何脚本与配置。
它只做两件事——找到本工程的 ADB 调试服务（读 `.workbuddy/AdbDebug/.adb-info.*`），
然后把它包装成 MCP 工具供客户端调用。

设计要点
--------
1. **零共享**：本文件可以单独拷到任何装了 ADB 调试工具（UnityPackage：Aketsuki_AdbDebugger）
   的工程里使用。找不到 `unity-cmd`、没有 `send.py`、没有 `.http-info` 都完全不影响。
2. **stdout 只放 MCP 消息**：stdio 传输要求每行一条紧凑 JSON，日志一律走 stderr。
3. **不检查 pid 存活**：Windows 上 `os.kill(pid, 0)` 是终止进程而不是探测。
   直接用「连得上 + 自报 tool=adb-debug」判定端点有效，连不上就试下一个候选。
4. **禁用系统代理**：本机回环请求绕不过某些代理软件，会返回 502。固定用空 ProxyHandler。

用法（由 MCP 客户端拉起）：
    python adb_mcp_server.py --project-root <Unity 工程根>

手动自检：
    echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"probe","version":"1"}}}' | python adb_mcp_server.py
"""

import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

SERVER_NAME = "adb-debug"
SERVER_VERSION = "1.0.0"

SUPPORTED_PROTOCOLS = ("2025-06-18", "2025-03-26", "2024-11-05")
DEFAULT_PROTOCOL = SUPPORTED_PROTOCOLS[0]

HTTP_TIMEOUT = 20.0
TARGET_TTL = 30.0
TARGET_FAIL_TTL = 3.0

INFO_DIR_PARTS = (".workbuddy", "AdbDebug")
INFO_PREFIX = ".adb-info."

# 固定空代理：本机回环不该经过系统代理（否则可能出现 502 upstream connect failed）
_OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def log(message):
    sys.stderr.write("[adb-debug] %s\n" % message)
    sys.stderr.flush()


class BridgeError(Exception):
    pass


# --------------------------------------------------------------------------- 端点发现


def _project_roots():
    """按优先级给出候选工程根"""
    roots = []

    argv = sys.argv[1:]
    for index, item in enumerate(argv):
        if item == "--project-root" and index + 1 < len(argv):
            roots.append(argv[index + 1])

    env = os.environ.get("ADB_DEBUG_PROJECT")
    if env:
        roots.append(env)

    # 脚本位于 <root>/.workbuddy/skills/adb-cmd/scripts/，向上四层即工程根
    here = os.path.dirname(os.path.abspath(__file__))
    cursor = here
    for _ in range(6):
        cursor = os.path.dirname(cursor)
        if not cursor:
            break
        roots.append(cursor)

    cursor = os.getcwd()
    while True:
        roots.append(cursor)
        parent = os.path.dirname(cursor)
        if parent == cursor:
            break
        cursor = parent

    seen = set()
    ordered = []
    for root in roots:
        try:
            key = os.path.normcase(os.path.abspath(root))
        except Exception:
            continue
        if key in seen:
            continue
        seen.add(key)
        ordered.append(root)
    return ordered


def _info_candidates():
    """列出所有信息文件，主文件优先于 per-pid 副本"""
    found = []
    for root in _project_roots():
        folder = os.path.join(root, *INFO_DIR_PARTS)
        if not os.path.isdir(folder):
            continue
        try:
            names = os.listdir(folder)
        except Exception:
            continue

        for name in names:
            if not name.startswith(INFO_PREFIX):
                continue
            # .adb-info.<hash> 是主文件；.adb-info.<hash>.<pid> 是副本
            is_main = name.count(".") == 2
            found.append((0 if is_main else 1, os.path.join(folder, name)))

    found.sort()
    return [path for _, path in found]


class Bridge(object):
    """持有一个已确认可用的服务端点，带 TTL 缓存"""

    def __init__(self):
        self._base = None
        self._token = None
        self._expires = 0.0
        self._fail_until = 0.0
        self._fail_message = ""

    # ---------------------------------------------------------------- 端点

    def _invalidate(self):
        self._base = None
        self._token = None
        self._expires = 0.0

    def _probe(self, base, token):
        """连一次 /v1/status 自证身份；成功返回 payload"""
        payload = self._http(base, token, "/v1/status")
        if payload.get("tool") != SERVER_NAME:
            raise BridgeError("这个端点不是 ADB 调试服务（tool=%r）" % payload.get("tool"))
        return payload

    def endpoint(self, force=False):
        now = time.time()

        if not force and self._base and now < self._expires:
            return self._base, self._token

        if not force and now < self._fail_until:
            raise BridgeError(self._fail_message)

        candidates = _info_candidates()
        if not candidates:
            message = (
                "没找到 ADB 调试服务的信息文件。请确认：① 本工程装了 ADB 调试工具（Aketsuki_AdbDebugger）；"
                "② Unity 处于打开状态且「Tools / ADB 调试 / ADB 调试台」里勾了「启用调试服务」；"
                "③ 信息文件应在 <工程根>/.workbuddy/AdbDebug/.adb-info.*")
            self._fail_until = now + TARGET_FAIL_TTL
            self._fail_message = message
            raise BridgeError(message)

        problems = []
        for path in candidates:
            try:
                with open(path, "r", encoding="utf-8") as handle:
                    info = json.load(handle)
            except Exception as error:
                problems.append("%s 读不出来：%s" % (os.path.basename(path), error))
                continue

            base = (info.get("url") or "").strip()
            token = (info.get("token") or "").strip()
            if not base:
                problems.append("%s 没有 url" % os.path.basename(path))
                continue

            try:
                self._probe(base, token)
            except BridgeError as error:
                problems.append("%s：%s" % (os.path.basename(path), error))
                continue
            except Exception as error:
                problems.append("%s 连不上：%s" % (os.path.basename(path), error))
                continue

            self._base = base
            self._token = token
            self._expires = now + TARGET_TTL
            log("已连接 %s（来自 %s）" % (base, os.path.basename(path)))
            return self._base, self._token

        message = ("ADB 调试服务发现失败：\n  " + "\n  ".join(problems) +
                   "\n常见原因：Unity 已关闭、服务被停、端口被改后没重启服务。")
        self._fail_until = now + TARGET_FAIL_TTL
        self._fail_message = message
        raise BridgeError(message)

    # ---------------------------------------------------------------- HTTP

    def _http(self, base, token, path, params=None):
        url = base.rstrip("/") + path
        if params:
            clean = {k: v for k, v in params.items() if v is not None}
            if clean:
                url += ("&" if "?" in url else "?") + urllib.parse.urlencode(clean)

        request = urllib.request.Request(url, method="GET")
        request.add_header("X-Token", token or "")
        request.add_header("Accept", "application/json")

        try:
            with _OPENER.open(request, timeout=HTTP_TIMEOUT) as response:
                body = response.read().decode("utf-8", "replace")
        except urllib.error.HTTPError as error:
            detail = ""
            try:
                detail = error.read().decode("utf-8", "replace")
            except Exception:
                pass
            raise BridgeError("HTTP %s：%s" % (error.code, detail or error.reason))
        except urllib.error.URLError as error:
            raise BridgeError("连接失败：%s" % error.reason)

        if not body.strip():
            return {}
        try:
            return json.loads(body)
        except Exception:
            raise BridgeError("响应不是 JSON：%s" % body[:200])

    def call(self, path, params=None, retry_on_auth=True):
        """调一次；令牌失效时重发现一次"""
        base, token = self.endpoint()
        try:
            return self._http(base, token, path, params)
        except BridgeError as error:
            text = str(error)
            if retry_on_auth and ("HTTP 401" in text or "连接失败" in text):
                self._invalidate()
                base, token = self.endpoint(force=True)
                return self._http(base, token, path, params)
            raise

    def status(self):
        return self.call("/v1/status")

    def logcat(self, params):
        return self.call("/v1/logcat", params)


BRIDGE = Bridge()


# --------------------------------------------------------------------------- 工具


def _text_result(text, is_error=False):
    return {"content": [{"type": "text", "text": text}], "isError": bool(is_error)}


def _json_text(value):
    return json.dumps(value, ensure_ascii=False, indent=2)


def _fmt_int(value):
    try:
        return "{:,}".format(int(value))
    except Exception:
        return str(value)


def tool_adb_status(arguments):
    try:
        payload = BRIDGE.status()
    except BridgeError as error:
        return _text_result(str(error), is_error=True)

    lines = []
    lines.append("服务 %s｜端口 %s｜抓取 %s"
                 % ("运行中" if payload.get("running") else "未运行",
                    payload.get("port"),
                    "运行中" if payload.get("logcatRunning") else "未运行"))
    if payload.get("logcatRunning"):
        lines.append("收到 %s 行 / 打印 %s 条 / 合并 %s 行 / 过滤 %s / 丢弃 %s"
                     % (_fmt_int(payload.get("received", 0)),
                        _fmt_int(payload.get("printed", 0)),
                        _fmt_int(payload.get("coalesced", 0)),
                        _fmt_int(payload.get("filtered", 0)),
                        _fmt_int(payload.get("dropped", 0))))
    if payload.get("command"):
        lines.append("命令：%s" % payload.get("command"))
    if payload.get("adb"):
        lines.append("adb：%s（%s）" % (payload.get("adb"), payload.get("adbSource")))
    if payload.get("notice"):
        lines.append("注意：%s" % payload.get("notice"))
    if payload.get("lastError"):
        lines.append("上次失败：%s" % payload.get("lastError"))

    lines.append("")
    lines.append(_json_text(payload))
    return _text_result("\n".join(lines))


def tool_adb_logcat(arguments):
    params = {
        "action": arguments.get("action") or "start",
        "tag": arguments.get("tag"),
        "level": arguments.get("level"),
        "serial": arguments.get("serial"),
        "backfill": arguments.get("backfill"),
        "stack": arguments.get("stack"),
    }
    if arguments.get("suppressStack") is not None:
        params["suppressStack"] = "true" if arguments.get("suppressStack") else "false"

    try:
        payload = BRIDGE.logcat(params)
    except BridgeError as error:
        return _text_result(str(error), is_error=True)

    # /v1/logcat 在 start/stop 时返回 {ok, message, command}；status 时返回完整状态
    if "message" in payload:
        lines = [payload.get("message", "")]
        if payload.get("command"):
            lines.append("命令：%s" % payload.get("command"))
        lines.append("")
        lines.append(_json_text(payload))
        return _text_result("\n".join(lines), is_error=not payload.get("ok", True))

    return tool_adb_status({})


TOOLS = [
    {
        "name": "adb_status",
        "description": (
            "查询 ADB 调试服务与 logcat 抓取的状态：端口、抓取是否在跑、收到/打印/合并/过滤的行数、"
            "实际使用的 adb 路径、以及 adb 自己的「等设备」提示。"
            "不知道当前什么情况时先调它。"),
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
    },
    {
        "name": "adb_logcat",
        "description": (
            "启停 Android 设备的 logcat 抓取。日志会以 [ADB] 前缀逐条打进 Unity 控制台"
            "（一次异常的堆栈会合并成一条日志），用 Unity 侧的 console.read 读内容。"
            "action=start 需要设备已连接（USB 调试已授权或模拟器已启动）。"
            "抓完请 stop —— logcat 是持续洪流。"),
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {
                    "type": "string",
                    "enum": ["start", "stop", "restart", "status"],
                    "description": "start 启动、stop 停止、restart 重连、status 查状态",
                    "default": "start",
                },
                "tag": {"type": "string", "description": "tag 子串过滤（大小写不敏感）；Unity 的日志 tag 是 Unity"},
                "level": {
                    "type": "string",
                    "enum": ["V", "D", "I", "W", "E", "F"],
                    "description": "最低级别，默认 I",
                },
                "serial": {"type": "string", "description": "设备序列号；多设备时必须填，单设备可留空"},
                "backfill": {"type": "integer", "description": "连接时先回填多少行历史（-T N），默认 20"},
                "stack": {
                    "type": "integer",
                    "description": "一条日志最多合并多少行堆栈续行；0 = 完全不打印调用栈。默认 40",
                },
                "suppressStack": {
                    "type": "boolean",
                    "description": "是否隐藏 [ADB] 日志附带的 C# 堆栈（默认 true）",
                },
            },
            "additionalProperties": False,
        },
    },
]

TOOL_HANDLERS = {
    "adb_status": tool_adb_status,
    "adb_logcat": tool_adb_logcat,
}


# --------------------------------------------------------------------------- 协议


def _negotiate_version(requested):
    if requested in SUPPORTED_PROTOCOLS:
        return requested
    return DEFAULT_PROTOCOL


def _result(msg_id, result):
    return {"jsonrpc": "2.0", "id": msg_id, "result": result}


def _error(msg_id, code, message):
    return {"jsonrpc": "2.0", "id": msg_id, "error": {"code": code, "message": message}}


def handle(message):
    method = message.get("method") or ""
    msg_id = message.get("id")
    params = message.get("params") or {}

    if method == "initialize":
        return _result(msg_id, {
            "protocolVersion": _negotiate_version(params.get("protocolVersion")),
            "capabilities": {"tools": {"listChanged": False}},
            "serverInfo": {"name": SERVER_NAME, "version": SERVER_VERSION},
        })

    if method == "notifications/initialized":
        return None

    if method == "ping":
        return _result(msg_id, {})

    if method == "tools/list":
        return _result(msg_id, {"tools": TOOLS})

    if method == "tools/call":
        name = params.get("name") or ""
        arguments = params.get("arguments") or {}
        handler = TOOL_HANDLERS.get(name)
        if handler is None:
            return _error(msg_id, -32602, "未知工具：%s（可用：%s）"
                          % (name, "、".join(sorted(TOOL_HANDLERS))))
        try:
            return _result(msg_id, handler(arguments))
        except BridgeError as error:
            return _result(msg_id, _text_result(str(error), is_error=True))
        except Exception as error:  # 兜底：任何异常都要变成工具错误，不能让进程死掉
            return _result(msg_id, _text_result("执行 %s 时出错：%s" % (name, error), is_error=True))

    return _error(msg_id, -32601, "不支持的方法：%s" % method)


def _write(payload):
    sys.stdout.write(json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def main():
    log("启动 %s %s（cwd=%s）" % (SERVER_NAME, SERVER_VERSION, os.getcwd()))

    for raw in sys.stdin:
        line = raw.strip()
        if not line:
            continue

        try:
            message = json.loads(line)
        except Exception as error:
            log("收到非 JSON 行，已忽略：%s" % error)
            continue

        try:
            response = handle(message)
        except Exception as error:
            response = _error(message.get("id"), -32603, "内部错误：%s" % error)

        if response is not None:
            _write(response)


if __name__ == "__main__":
    main()
