# -*- coding: utf-8 -*-
"""MCP 桥的 stdio 自检：发一串 JSON-RPC，把响应按行读回来。

用法：
    python Tools/probe_bridge.py --default-target D:/Work/MyUnityProject
    python Tools/probe_bridge.py --tool-mode group --default-target <工程>

只依赖标准库，与 MCP 客户端拉起的姿势一致（每行一条紧凑 JSON）。
"""
import argparse
import json
import os
import subprocess
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
BRIDGE = os.path.join(HERE, "..", "Packages", "com.aketsuki.unity-mcp", "Editor", "McpBridge",
                      "unity_mcp_server.py")


def run(extra_args, calls, timeout=90):
    cmd = [sys.executable, "-u", BRIDGE] + extra_args
    proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, encoding="utf-8", bufsize=1,
                            errors="replace")

    out = {}
    done = threading.Event()

    def reader():
        for line in proc.stdout:
            line = line.strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
            except ValueError:
                continue
            out[msg.get("id")] = msg
            if len(out) >= len(calls):
                done.set()
        done.set()

    thread = threading.Thread(target=reader, daemon=True)
    thread.start()

    for index, call in enumerate(calls):
        call = dict(call)
        call.setdefault("jsonrpc", "2.0")
        call.setdefault("id", index + 1)
        proc.stdin.write(json.dumps(call, ensure_ascii=False) + "\n")
        proc.stdin.flush()

    done.wait(timeout)
    try:
        proc.stdin.close()
    except Exception:
        pass
    try:
        proc.wait(5)
    except subprocess.TimeoutExpired:
        proc.kill()
    stderr = proc.stderr.read()
    return out, stderr


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--tool-mode", default=None, choices=["meta", "group", "full"])
    parser.add_argument("--default-target", default=None)
    parser.add_argument("--project-root", default=None)
    parser.add_argument("--call", default=None,
                        help="要调用的工具名，形如 unity_status 或 unity_execute")
    parser.add_argument("--args", default="{}", help="工具参数 JSON")
    parser.add_argument("--show", default="tools", choices=["tools", "call", "both"])
    args = parser.parse_args()

    extra = []
    if args.tool_mode:
        extra += ["--tool-mode", args.tool_mode]
    if args.default_target:
        extra += ["--default-target", args.default_target]
    if args.project_root:
        extra += ["--project-root", args.project_root]

    calls = [
        {"method": "initialize",
         "params": {"protocolVersion": "2025-06-18", "capabilities": {},
                    "clientInfo": {"name": "probe", "version": "1"}}},
        {"method": "tools/list", "params": {}},
    ]
    if args.call:
        calls.append({"method": "tools/call",
                      "params": {"name": args.call, "arguments": json.loads(args.args)}})

    responses, stderr = run(extra, calls)

    if args.show in ("tools", "both"):
        listed = responses.get(2, {}).get("result", {}).get("tools")
        if listed is None:
            print("!! tools/list 失败：", json.dumps(responses.get(2), ensure_ascii=False)[:500])
        else:
            payload = json.dumps({"tools": listed}, ensure_ascii=False, separators=(",", ":"))
            print("tools/list：%d 个工具，%d 字符" % (len(listed), len(payload)))
            for tool in listed:
                print("  %-28s %s" % (tool["name"], tool.get("description", "")[:70]))

    if args.show in ("call", "both") and args.call:
        response = responses.get(3, {})
        result = response.get("result") or {}
        print("\ntools/call %s（isError=%s）：" % (args.call, result.get("isError")))
        for part in result.get("content") or []:
            print(part.get("text", ""))
        if "error" in response:
            print("!! 错误：", json.dumps(response["error"], ensure_ascii=False)[:500])

    if stderr.strip():
        print("\n--- stderr ---")
        print(stderr.strip())


if __name__ == "__main__":
    main()
