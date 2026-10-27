#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 ADB 调试工具的 MCP 桥装到工程里，并注册进 WorkBuddy 的 mcp.json。

做的事只有三件：
1. 把 `adb_mcp_server.py` / `adb_cli.py` 复制到 `<工程根>/.workbuddy/skills/adb-cmd/scripts/`
2. 把 `SKILL.md` 复制到 `<工程根>/.workbuddy/skills/adb-cmd/`
3. 在 `~/.workbuddy/mcp.json` 里写入/更新 `adb-debug-<工程>` 条目（不动其它条目）

**为什么用 Python 做而不是在 C# 里做**：要改 mcp.json 就得能读写任意 JSON。
本工具刻意不依赖指令系统的 JsonValue，为了一个安装动作去实现一个 JSON 解析器不划算。
Python 自带 json，且这个脚本本来就是分发包的一部分。

用法：
    python install_bridge.py --project-root D:/proj/SomeGame
    python install_bridge.py --project-root . --python "C:/Python39/python.exe"
    python install_bridge.py --uninstall
"""

import argparse
import json
import os
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SKILL_NAME = "adb-cmd"
SERVER_PREFIX = "adb-debug"
SCRIPTS = ("adb_mcp_server.py", "adb_cli.py")


def mcp_config_path():
    return os.path.join(os.path.expanduser("~"), ".workbuddy", "mcp.json")


def detect_project_root(explicit):
    if explicit:
        return os.path.abspath(explicit)

    # 分发包里的位置是 <root>/Assets/Editor/AdbDebugger/AdbBridge/，向上找有 Assets 的那层
    cursor = HERE
    while True:
        if os.path.isdir(os.path.join(cursor, "Assets")):
            return cursor
        parent = os.path.dirname(cursor)
        if parent == cursor:
            break
        cursor = parent

    raise SystemExit("找不到工程根（没有含 Assets 的上级目录），请用 --project-root 指定")


def skill_dir_of(root):
    return os.path.join(root, ".workbuddy", "skills", SKILL_NAME)


def deploy(root):
    skill_dir = skill_dir_of(root)
    scripts_dir = os.path.join(skill_dir, "scripts")
    os.makedirs(scripts_dir, exist_ok=True)

    for name in SCRIPTS:
        source = os.path.join(HERE, name)
        if not os.path.isfile(source):
            raise SystemExit("分发包不完整，缺少 %s" % source)
        shutil.copy2(source, os.path.join(scripts_dir, name))

    skill_md = os.path.join(HERE, "SKILL.md")
    if os.path.isfile(skill_md):
        shutil.copy2(skill_md, os.path.join(skill_dir, "SKILL.md"))

    return skill_dir, scripts_dir


def load_config(path):
    if not os.path.isfile(path):
        return {}

    with open(path, "r", encoding="utf-8") as handle:
        text = handle.read().strip()
    if not text:
        return {}
    return json.loads(text)


def save_config(path, config):
    folder = os.path.dirname(path)
    if folder:
        os.makedirs(folder, exist_ok=True)

    # 先备份：这是用户的共享配置，写坏了所有 MCP 客户端都会起不来
    if os.path.isfile(path):
        backup = path + ".adb-debug.bak"
        try:
            shutil.copy2(path, backup)
        except Exception:
            pass

    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(config, handle, ensure_ascii=False, indent=2)
        handle.write("\n")


def find_entry_for_project(servers, root):
    target = os.path.normcase(os.path.abspath(root))
    for name, value in servers.items():
        if not name.startswith(SERVER_PREFIX):
            continue
        args = (value or {}).get("args") or []
        if "--project-root" not in args:
            continue
        index = args.index("--project-root")
        if index + 1 < len(args) and os.path.normcase(os.path.abspath(args[index + 1])) == target:
            return name
    return None


def pick_key(servers, root):
    existing = find_entry_for_project(servers, root)
    if existing:
        return existing

    base = "%s-%s" % (SERVER_PREFIX, os.path.basename(root.rstrip("\\/")) or "project")
    key = base
    index = 2
    while key in servers:
        key = "%s-%d" % (base, index)
        index += 1
    return key


def install(root, python, force_key=None):
    skill_dir, scripts_dir = deploy(root)

    path = mcp_config_path()
    config = load_config(path)
    servers = config.setdefault("mcpServers", {})

    key = force_key or pick_key(servers, root)
    servers[key] = {
        "command": python,
        "args": [
            os.path.join(scripts_dir, "adb_mcp_server.py"),
            "--project-root", root,
        ],
        "env": {"PYTHONIOENCODING": "utf-8", "PYTHONUTF8": "1"},
    }
    save_config(path, config)

    print("技能目录  : %s" % skill_dir)
    print("脚本目录  : %s" % scripts_dir)
    print("MCP 配置  : %s" % path)
    print("服务键名  : %s" % key)
    print("解释器    : %s" % python)
    print()
    print("下一步：到连接器管理页的自定义连接器里对「%s」点信任。" % key)


def uninstall():
    path = mcp_config_path()
    if not os.path.isfile(path):
        print("没有 MCP 配置文件，无需移除。")
        return

    config = load_config(path)
    servers = config.get("mcpServers") or {}

    removed = []
    for name in list(servers.keys()):
        if not name.startswith(SERVER_PREFIX):
            continue
        entry = servers[name] or {}
        args = entry.get("args") or []
        joined = " ".join(str(item) for item in args)
        if SERVER_PREFIX in joined or "adb_mcp_server.py" in joined:
            servers.pop(name)
            removed.append(name)

    config["mcpServers"] = servers
    save_config(path, config)

    if removed:
        print("已移除：%s" % "、".join(removed))
    else:
        print("配置里没有 ADB 调试的条目。")
    print("注意：只删了 mcp.json 条目，.workbuddy/skills/%s 目录仍在，可手动删除。" % SKILL_NAME)


def main():
    parser = argparse.ArgumentParser(description="安装/卸载 ADB 调试工具的 MCP 桥")
    parser.add_argument("--project-root", default=None, help="Unity 工程根；默认自动推断")
    parser.add_argument("--python", default=sys.executable, help="写进 mcp.json 的解释器路径")
    parser.add_argument("--key", default=None, help="强制指定 mcp.json 里的服务键名")
    parser.add_argument("--uninstall", action="store_true", help="从 mcp.json 移除本工具的条目")
    args = parser.parse_args()

    if args.uninstall:
        uninstall()
        return 0

    root = detect_project_root(args.project_root)
    install(root, args.python, args.key)
    return 0


if __name__ == "__main__":
    sys.exit(main())
