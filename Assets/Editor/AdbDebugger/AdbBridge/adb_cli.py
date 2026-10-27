#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ADB 调试工具的命令行入口（排查用，不参与 MCP）。

MCP 客户端走 adb_mcp_server.py；这个脚本是给人用的：
    python adb_cli.py status
    python adb_cli.py logcat start --serial 2LQ0224A15000813 --tag Unity
    python adb_cli.py logcat stop
"""

import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import adb_mcp_server as bridge  # noqa: E402


def main():
    parser = argparse.ArgumentParser(description="ADB 调试工具命令行")
    parser.add_argument("--project-root", default=None, help="Unity 工程根（默认自动向上找）")
    sub = parser.add_subparsers(dest="command")

    sub.add_parser("status", help="查服务与抓取状态")

    logcat = sub.add_parser("logcat", help="启停 logcat")
    logcat.add_argument("action", choices=["start", "stop", "restart", "status"])
    logcat.add_argument("--tag", default=None)
    logcat.add_argument("--level", default=None)
    logcat.add_argument("--serial", default=None)
    logcat.add_argument("--backfill", type=int, default=None)
    logcat.add_argument("--stack", type=int, default=None)
    logcat.add_argument("--keep-csharp-stack", action="store_true",
                        help="不隐藏 [ADB] 日志的 C# 堆栈")

    args = parser.parse_args()

    if args.project_root:
        sys.argv = [sys.argv[0], "--project-root", args.project_root]

    try:
        if args.command == "status" or (args.command == "logcat" and args.action == "status"):
            print(bridge.tool_adb_status({})["content"][0]["text"])
            return 0

        if args.command == "logcat":
            params = {
                "action": args.action,
                "tag": args.tag,
                "level": args.level,
                "serial": args.serial,
                "backfill": args.backfill,
                "stack": args.stack,
            }
            if args.keep_csharp_stack:
                params["suppressStack"] = "false"

            payload = bridge.BRIDGE.logcat(params)
            print(json.dumps(payload, ensure_ascii=False, indent=2))
            return 0 if payload.get("ok", True) else 1

        parser.print_help()
        return 2
    except bridge.BridgeError as error:
        sys.stderr.write("错误：%s\n" % error)
        return 1


if __name__ == "__main__":
    sys.exit(main())
