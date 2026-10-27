#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 .unitypackage 直接解包落地到 Unity 工程（不经过 Unity 的导入器）。

为什么需要它：
- `AssetDatabase.ImportPackage` 在编辑器里静默无效（路径写法不对就什么都不做，
  传 file:/// 还会卡死主线程）；
- 无头 `Unity.exe -importPackage` 好用，但**工程正在编辑器里打开时会被 UnityLockfile 挡住**。

`.unitypackage` 本身就是个 tar.gz，每个资产是一个 `<guid>/` 目录，
里面是 `asset`（文件内容）、`asset.meta`、`pathname`（目标路径）。
把它们直接写到工程对应位置，效果与导入等价；之后让编辑器刷新一下即可。

**只新增/覆盖，不删除**：包里没有的文件一律不动，避免误删。

用法：
    python Tools/install_unitypackage.py --project D:/Wrok_proj/project_lilith
    python Tools/install_unitypackage.py --module adb --project <工程根>
    python Tools/install_unitypackage.py -p X.unitypackage --project <工程根> --dry-run

两个工具（MCP 指令系统 / ADB 调试）各有独立的包与安装根，本脚本不写死任何一处：
包内 `package.json` 的 `installRoot` / `entrypoints.mcpBridge` / `skillName` / `skillFiles`
决定装到哪、桥同步到哪个技能目录。老包缺字段时按模块兜底（见 MODULE_HINTS）。
"""

import argparse
import glob
import gzip
import io
import json
import os
import sys
import tarfile

HERE = os.path.dirname(os.path.abspath(__file__))
PKG_DIR = os.path.join(HERE, "UnityPackage")

# 按模块区分的兜底值。**正常情况下这些都不该被用到**——真值来自包内 package.json
# 的 `installRoot` / `entrypoints.mcpBridge` / `skillName` / `skillFiles`。
# 这里只是给老包（v1.0 的 MCP 包没有后两个字段）和残缺包留的回落，不要当成配置源改。
MODULE_HINTS = {
    "mcp": {
        "prefix": "Aketsuki_Unity_MCP",
        "install_root": "Assets/Editor/ExternalCommandSystem",
        "bridge_dir": "McpBridge",
        "skill_name": "unity-cmd",
        # (包内桥目录里的相对路径, 技能目录里的路径)——两边不一定同形：
        # 桥在 Assets 里是平铺的，技能目录要求 python 落在 scripts/ 下
        "skill_files": [
            ("send.py", "scripts/send.py"),
            ("unity_mcp_server.py", "scripts/unity_mcp_server.py"),
            ("SKILL.md", "SKILL.md"),
        ],
        "server": "unity_mcp_server.py",
    },
    "adb": {
        "prefix": "Aketsuki_AdbDebugger",
        "install_root": "Assets/Editor/AdbDebugger",
        "bridge_dir": "AdbBridge",
        "skill_name": "adb-cmd",
        "skill_files": [
            ("adb_mcp_server.py", "scripts/adb_mcp_server.py"),
            ("adb_cli.py", "scripts/adb_cli.py"),
            ("SKILL.md", "SKILL.md"),
        ],
        "server": "adb_mcp_server.py",
    },
}

MANIFEST_NAME = "package.json"


def package_module(package):
    """按文件名猜这个包属于哪个模块（只用于打印与兜底，不作为判定依据）。"""
    base = os.path.basename(package)
    for name, hint in MODULE_HINTS.items():
        if base.startswith(hint["prefix"]):
            return name
    return None


def default_package(module=None):
    """定位默认包：`Tools/UnityPackage/` 或脚本同级目录里的 .unitypackage。

    刻意不做"挑最新的"——多包共存时静默选一个正是这类安装器最容易出的错。
    两个工具的包会同时躺在分发目录里，所以**必须**要么只有一个、要么用 --module / -p 指定。
    """
    found = []
    for directory in (HERE, PKG_DIR):
        found += sorted(glob.glob(os.path.join(directory, "*.unitypackage")))
    if module:
        prefix = MODULE_HINTS[module]["prefix"]
        found = [p for p in found if os.path.basename(p).startswith(prefix)]
        if not found:
            raise SystemExit("没有找到 %s 的 .unitypackage，请用 -p 指定" % module)
    if len(found) == 1:
        return found[0]
    if len(found) > 1:
        raise SystemExit("目录里有多个 .unitypackage，请用 -p（或 --module）指定：\n  %s"
                         % "\n  ".join(found))
    raise SystemExit("没找到 .unitypackage，请用 -p 指定")



def read_entries(package):
    """返回 [(pathname, is_folder, asset_bytes|None, meta_bytes)]"""
    raw = gzip.decompress(open(package, "rb").read())
    with tarfile.open(fileobj=io.BytesIO(raw)) as tar:
        members = tar.getmembers()
        names = set(m.name for m in members)
        guids = []
        for member in members:
            head = member.name.split("/")[0]
            if head and head not in guids:
                guids.append(head)

        entries = []
        for guid in guids:
            if guid + "/pathname" not in names:
                continue
            pathname = tar.extractfile(guid + "/pathname").read().decode("utf-8").strip()
            meta = tar.extractfile(guid + "/asset.meta").read() if guid + "/asset.meta" in names else None
            asset = tar.extractfile(guid + "/asset").read() if guid + "/asset" in names else None
            entries.append((pathname, asset is None, asset, meta))
    return entries


def load_manifest(path):
    """包内 `package.json`（名称/版本/作者/日期）。老包没有，返回 None。"""
    try:
        with open(path, "rb") as handle:
            return json.loads(handle.read().decode("utf-8"))
    except (OSError, ValueError):
        return None


def version_tuple(text):
    parts = []
    for piece in str(text or "").replace("-", ".").split("."):
        if not piece.isdigit():
            break
        parts.append(int(piece))
    return tuple(parts)


def find_manifest(entries):
    """在包里找 `package.json`，返回 (pathname, dict) 或 (None, None)。

    不硬编码路径：两个工具的 installRoot 不同（ECS / AdbDebugger），写死一个另一个就找不到。
    优先取装在 `Assets/` 下、层级最浅的那份，避免桥目录里偶然夹带的 json 被当成清单。
    """
    candidates = []
    for pathname, is_folder, asset, _meta in entries:
        if is_folder or asset is None or os.path.basename(pathname) != MANIFEST_NAME:
            continue
        try:
            data = json.loads(asset.decode("utf-8"))
        except (UnicodeDecodeError, ValueError):
            continue
        if not isinstance(data, dict):
            continue
        depth = pathname.count("/")
        candidates.append(((0 if pathname.startswith("Assets/") else 1, depth), pathname, data))
    if not candidates:
        return None, None
    candidates.sort(key=lambda item: item[0])
    _key, pathname, data = candidates[0]
    return pathname, data


def resolve_layout(manifest, module):
    """从包清单推出：安装根、桥目录、技能目录名、要同步进技能目录的文件。

    清单字段是权威来源；缺字段的老包按模块兜底。
    """
    hint = MODULE_HINTS.get(module) or {}
    manifest = manifest or {}

    install_root = manifest.get("installRoot") or hint.get("install_root") \
        or "Assets/Editor/ExternalCommandSystem"

    bridge = (manifest.get("entrypoints") or {}).get("mcpBridge") or ""
    bridge_dir = os.path.dirname(bridge) if bridge else ""
    if not bridge_dir:
        bridge_dir = install_root + "/" + (hint.get("bridge_dir") or "McpBridge")

    server = os.path.basename(bridge) if bridge else (hint.get("server") or "")
    skill_name = manifest.get("skillName") or hint.get("skill_name")
    if not skill_name:
        # 老包没有 skillName：按服务脚本名反推，再不行退回 MCP 的
        skill_name = "adb-cmd" if server == "adb_mcp_server.py" else "unity-cmd"

    skill_files = normalize_skill_files(manifest.get("skillFiles"))
    if not skill_files:
        skill_files = MODULE_HINTS["adb" if skill_name == "adb-cmd" else "mcp"]["skill_files"]

    return {
        "install_root": install_root,
        "bridge_dir": bridge_dir,
        "server": server,
        "skill_name": skill_name,
        "skill_files": skill_files,
    }


def normalize_skill_files(raw):
    """把清单里的 `skillFiles` 统一成 [(包内相对路径, 技能目录路径)]。

    接受三种写法，兼容手写与后续演化：
      {"from": "adb_cli.py", "to": "scripts/adb_cli.py"}   —— 现行格式（两边路径可不同形）
      ["scripts/adb_cli.py"]                               —— 简写，视为两边同路径
    """
    result = []
    if not isinstance(raw, list):
        return result
    for item in raw:
        if isinstance(item, dict):
            src = item.get("from") or item.get("path")
            dst = item.get("to") or src
        else:
            src = dst = item
        if not src:
            continue
        src = str(src).replace("\\", "/").lstrip("/")
        dst = str(dst).replace("\\", "/").lstrip("/")
        result.append((src, dst))
    return result


def install_skill(project, entries, layout, dry_run, out):
    """把 MCP 桥同步到 `<工程>/.workbuddy/skills/<skill_name>/`。

    MCP 客户端拉起的是这一份，不是包内 `McpBridge/`（ADB 是 `AdbBridge/`）那份。
    以前靠手工拷，三个工程的 SKILL.md 因此各自停在旧版（不含 runtime.*）——这里一并兜住。
    """
    skill_root = os.path.join(project, ".workbuddy", "skills", layout["skill_name"])
    bridge_dir = layout["bridge_dir"]
    blobs = {}
    for pathname, is_folder, asset, _meta in entries:
        if is_folder or asset is None or not pathname.startswith(bridge_dir + "/"):
            continue
        blobs[pathname[len(bridge_dir) + 1:]] = asset

    written = up = 0
    for rel_in_pkg, rel_in_skill in layout["skill_files"]:
        content = blobs.get(rel_in_pkg)
        if content is None:
            out.append("桥缺文件，跳过：%s（桥目录 %s）" % (rel_in_pkg, bridge_dir))
            continue
        target = os.path.join(skill_root, rel_in_skill.replace("/", os.sep))
        old = open(target, "rb").read() if os.path.isfile(target) else None
        if old == content:
            continue
        if old is not None:
            up += 1
        else:
            written += 1
        if not dry_run:
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with open(target, "wb") as handle:
                handle.write(content)
    return written, up


def main(argv=None):
    parser = argparse.ArgumentParser(description="把 .unitypackage 直接解包到 Unity 工程")
    parser.add_argument("-p", "--package", help="包路径（默认取同级 / Tools/UnityPackage 下的那个）")
    parser.add_argument("--project", required=True, help="Unity 工程根（含 Assets/ 的那一层）")
    parser.add_argument("--module", choices=("mcp", "adb"), default=None,
                        help="要装哪个工具（只在自动定位包、清单缺字段时用；正常从包里读）")
    parser.add_argument("--skill", dest="skill", action="store_true", default=True,
                        help="同步 MCP 桥到 <工程>/.workbuddy/skills/<skillName>/（默认开）")
    parser.add_argument("--no-skill", dest="skill", action="store_false", help="不同步 MCP 桥")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)

    package = os.path.abspath(args.package or default_package(args.module))
    project = os.path.abspath(args.project)
    if not os.path.isfile(package):
        raise SystemExit("包不存在：%s" % package)
    if not os.path.isdir(os.path.join(project, "Assets")):
        raise SystemExit("不是 Unity 工程（缺 Assets/）：%s" % project)

    problems = []
    entries = read_entries(package)
    # 先看包里自称是谁，再用命令行 / 文件名补；两处都没有才当 MCP 处理
    manifest_in_pkg, incoming = find_manifest(entries)
    module = args.module or (incoming or {}).get("module") or package_module(package) or "mcp"
    layout = resolve_layout(incoming, module)
    if manifest_in_pkg is None:
        # 没清单就无从得知安装根，只能按模块兜底
        manifest_in_pkg = layout["install_root"] + "/" + MANIFEST_NAME

    installed = load_manifest(os.path.join(project, manifest_in_pkg.replace("/", os.sep)))

    if incoming is None:
        problems.append("包里没有 package.json——来源不明，注意可能覆盖掉更新的文件")
    elif installed and version_tuple(installed.get("version")) > version_tuple(incoming.get("version")):
        problems.append("目标工程已装 %s v%s（%s），比本包 v%s 更新："
                        "覆盖会把新版文件回退，而本安装器只增不删，结果是编译不过的混合态"
                        % (installed.get("name"), installed.get("version"),
                           installed.get("built"), incoming.get("version")))
    elif installed and installed.get("name") and incoming.get("name") \
            and installed.get("name") != incoming.get("name"):
        problems.append("目标 %s 里装的是另一个工具（%s），本包是 %s："
                        "两个工具装进同一个目录会互相覆盖"
                        % (layout["install_root"], installed.get("name"), incoming.get("name")))

    added = updated = same = folders = 0
    for pathname, is_folder, asset, meta in entries:
        target = os.path.join(project, pathname.replace("/", os.sep))
        if is_folder:
            if not args.dry_run and not os.path.isdir(target):
                os.makedirs(target)
            folders += 1
            if meta:
                meta_path = target + ".meta"
                if not os.path.isfile(meta_path):
                    if not args.dry_run:
                        open(meta_path, "wb").write(meta)
        else:
            existed = os.path.isfile(target)
            old = open(target, "rb").read() if existed else None
            if old == asset:
                same += 1
            elif existed:
                if not args.dry_run:
                    open(target, "wb").write(asset)
                updated += 1
            else:
                if not args.dry_run:
                    os.makedirs(os.path.dirname(target), exist_ok=True)
                    open(target, "wb").write(asset)
                added += 1

            if meta:
                meta_path = target + ".meta"
                # 已有 meta 不覆盖：保住工程里的旧 GUID，避免引用丢失
                if not os.path.isfile(meta_path) and not args.dry_run:
                    open(meta_path, "wb").write(meta)

    skill_new = skill_up = 0
    if args.skill:
        skill_new, skill_up = install_skill(project, entries, layout, args.dry_run, problems)

    print("包：%s" % package)
    if incoming:
        print("  %s v%s（%s）作者 %s，Unity %s"
              % (incoming.get("name"), incoming.get("version"), incoming.get("built"),
                 incoming.get("author"), incoming.get("unityVersion")))
    print("目标：%s" % project)
    print("  安装根：%s（清单 %s）" % (layout["install_root"], manifest_in_pkg))
    if installed and incoming:
        print("  目标已装：%s v%s（%s）" % (installed.get("name"), installed.get("version"),
                                          installed.get("built")))
    print("资产 %d 个（目录 %d）→ 新增 %d / 更新 %d / 未变 %d%s"
          % (len(entries), folders, added, updated, same, "（dry-run，未写盘）" if args.dry_run else ""))
    if args.skill:
        print("MCP 桥 → .workbuddy/skills/%s/：新增 %d / 更新 %d%s"
              % (layout["skill_name"], skill_new, skill_up,
                 "（dry-run，未写盘）" if args.dry_run else ""))

    if problems:
        print()
        for message in problems:
            print("  [警告] " + message)
    if added or updated or skill_new or skill_up:
        print("\n下一步：让编辑器刷新一次（聚焦 Unity 窗口，或走桥发 asset.refresh）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
