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
    python Tools/install_unitypackage.py -p X.unitypackage --project <工程根> --dry-run
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

# 桥文件在包内的位置（相对 ECS 根），装完要同步到工程的 .workbuddy/skills/unity-cmd/
BRIDGE_IN_PKG = "Assets/Editor/ExternalCommandSystem/McpBridge"
SKILL_LAYOUT = [
    ("send.py", "scripts/send.py"),
    ("unity_mcp_server.py", "scripts/unity_mcp_server.py"),
    ("SKILL.md", "SKILL.md"),
]
MANIFEST_IN_PKG = "Assets/Editor/ExternalCommandSystem/package.json"


def default_package():
    """定位默认包：`Tools/UnityPackage/` 或脚本同级目录里**唯一**的那个 .unitypackage。

    刻意不做"挑最新的"——多包共存时静默选一个正是这类安装器最容易出的错。
    分发目录里就是同级那一个，能自动命中。
    """
    for directory in (HERE, PKG_DIR):
        found = sorted(glob.glob(os.path.join(directory, "*.unitypackage")))
        if len(found) == 1:
            return found[0]
        if len(found) > 1:
            raise SystemExit("目录里有多个 .unitypackage，请用 -p 指定：\n  %s"
                             % "\n  ".join(found))
    return os.path.join(PKG_DIR, "Aketsuki_Unity_MCP-1.0.unitypackage")



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


def install_skill(project, entries, dry_run, out):
    """把 MCP 桥同步到 `<工程>/.workbuddy/skills/unity-cmd/`。

    MCP 客户端拉起的是这一份，不是 `Assets/Editor/ExternalCommandSystem/McpBridge/` 那份。
    以前靠手工拷，三个工程的 SKILL.md 因此各自停在旧版（不含 runtime.*）——这里一并兜住。
    """
    skill_root = os.path.join(project, ".workbuddy", "skills", "unity-cmd")
    blobs = {}
    for pathname, is_folder, asset, _meta in entries:
        if is_folder or asset is None or not pathname.startswith(BRIDGE_IN_PKG + "/"):
            continue
        blobs[pathname[len(BRIDGE_IN_PKG) + 1:]] = asset

    written = up = 0
    for in_pkg, rel in SKILL_LAYOUT:
        content = blobs.get(in_pkg)
        if content is None:
            out.append("桥缺文件，跳过：%s" % in_pkg)
            continue
        target = os.path.join(skill_root, rel.replace("/", os.sep))
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
    parser.add_argument("-p", "--package", help="包路径（默认取同级 / Tools/UnityPackage 下唯一的那个）")
    parser.add_argument("--project", required=True, help="Unity 工程根（含 Assets/ 的那一层）")
    parser.add_argument("--skill", dest="skill", action="store_true", default=True,
                        help="同步 MCP 桥到 <工程>/.workbuddy/skills/unity-cmd/（默认开）")
    parser.add_argument("--no-skill", dest="skill", action="store_false", help="不同步 MCP 桥")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)

    package = os.path.abspath(args.package or default_package())
    project = os.path.abspath(args.project)
    if not os.path.isfile(package):
        raise SystemExit("包不存在：%s" % package)
    if not os.path.isdir(os.path.join(project, "Assets")):
        raise SystemExit("不是 Unity 工程（缺 Assets/）：%s" % project)

    problems = []
    entries = read_entries(package)
    installed = load_manifest(os.path.join(project, MANIFEST_IN_PKG))
    raw_manifest = next((a for p, _d, a, _m in entries if p == MANIFEST_IN_PKG), None)
    incoming = json.loads(raw_manifest.decode("utf-8")) if raw_manifest else None

    if incoming is None:
        problems.append("包里没有 package.json——来源不明，注意可能覆盖掉更新的文件")
    elif installed and version_tuple(installed.get("version")) > version_tuple(incoming.get("version")):
        problems.append("目标工程已装 %s v%s（%s），比本包 v%s 更新："
                        "覆盖会把新版文件回退，而本安装器只增不删，结果是编译不过的混合态"
                        % (installed.get("name"), installed.get("version"),
                           installed.get("built"), incoming.get("version")))

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
        skill_new, skill_up = install_skill(project, entries, args.dry_run, problems)

    print("包：%s" % package)
    if incoming:
        print("  %s v%s（%s）作者 %s，Unity %s"
              % (incoming.get("name"), incoming.get("version"), incoming.get("built"),
                 incoming.get("author"), incoming.get("unityVersion")))
    print("目标：%s" % project)
    if installed and incoming:
        print("  目标已装：%s v%s（%s）" % (installed.get("name"), installed.get("version"),
                                          installed.get("built")))
    print("资产 %d 个（目录 %d）→ 新增 %d / 更新 %d / 未变 %d%s"
          % (len(entries), folders, added, updated, same, "（dry-run，未写盘）" if args.dry_run else ""))
    if args.skill:
        print("MCP 桥 → .workbuddy/skills/unity-cmd/：新增 %d / 更新 %d%s"
              % (skill_new, skill_up, "（dry-run，未写盘）" if args.dry_run else ""))

    if problems:
        print()
        for message in problems:
            print("  [警告] " + message)
    if added or updated or skill_new or skill_up:
        print("\n下一步：让编辑器刷新一次（聚焦 Unity 窗口，或走桥发 asset.refresh）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
