#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 ExternalCommandSystem + MCP 桥打成 .unitypackage。

.unitypackage 本质是个 tar.gz（USTAR），每个资产最多四个条目：
    <guid>             目录条目（tar typeflag '5'），**必须有**，Unity 靠它划分条目
    <guid>/asset       文件内容（目录型资产没有这个条目）
    <guid>/asset.meta  Unity 的资产元数据（含 guid）
    <guid>/pathname    导入后的路径（纯文本，如 Assets/Editor/XXX/a.cs）

踩过的坑：少了 `<guid>` 目录条目、或给目录资产写了空 `asset`，Unity 导入时会
**毫无反应**（不报错、不弹窗、什么都不导入）。正确形态见本文件 `write_package`。

本脚本不修改源工程：C# 直接取 `Assets/Editor/ExternalCommandSystem/`，
Python/文档优先从该目录下的 `McpBridge/` 取（本仓库的形态），取不到再回落到
`.workbuddy/skills/unity-cmd/`（老布局），然后在**内存里**映射到包内路径
`Assets/Editor/ExternalCommandSystem/McpBridge/`，再写进 tar。

工程客制化内容（ECS 下 `Project/` 子目录）**强制排除**：收集、目录条目、终检三道关，
漏排直接报错终止。见 ECS README「工程客制化内容」。

用法：
    python Tools/build_unitypackage.py
    python Tools/build_unitypackage.py -o D:/tmp/UnityCommandBridge.unitypackage
    python Tools/build_unitypackage.py --list-only
"""

import argparse
import gzip
import io
import os
import sys
import tarfile
import uuid

# Unity 自己导出的 .unitypackage，gzip 头里的文件名恒为这个值。
# 客户端（Unity 导入器）会读这个字段，填别的值会导致导入被静默忽略。
_GZIP_FNAME = "archtemp.tar"

# ---------------------------------------------------------------- 布局

HERE = os.path.dirname(os.path.abspath(__file__))
UNITY_PROJECT = os.path.dirname(HERE)                  # Tools 的上一级 = 工程根

if not os.path.isdir(os.path.join(UNITY_PROJECT, "Assets")):
    raise SystemExit("Tools 的上一级不是 Unity 工程（缺 Assets/）：%s" % UNITY_PROJECT)

# 包内路径前缀（导入到新工程后就是这个路径）
PKG_ROOT = "Assets/Editor/ExternalCommandSystem"

# 工程客制化目录（包内路径）：ECS 允许存在与工程代码耦合的客制化内容（挂钩实现、
# 工程专用指令等），约定统一放在 ECS 下的 `Project/` 子目录。**该目录绝不进包**——
# 打包器在收集、目录条目、最终校验三处强制排除，verify_unitypackage.py 也会断言。
# 见 ECS README「工程客制化内容」一节。
PROJECT_COUPLED_IN_PKG = PKG_ROOT + "/Project"
PROJECT_COUPLED_DIR_NAME = "Project"    # 磁盘上的目录名（CS_SOURCE 下）

# 源：C# 服务端目录（含 README.md）
CS_SOURCE = os.path.join(UNITY_PROJECT, "Assets", "Editor", "ExternalCommandSystem")

# 源：MCP 桥。两个可能位置，按顺序取第一个存在的：
#   1) `Assets/Editor/ExternalCommandSystem/McpBridge/` —— 本仓库的形态（桥脚本是真源，
#      跟着 git 走；工程里用菜单装完也在这里留一份）
#   2) `<工程根>/.workbuddy/skills/unity-cmd/` —— 老布局（开发源工程，桥只在 .workbuddy，
#      不进 git）
BRIDGE_DIR_IN_PKG = "McpBridge"
BRIDGE_NAMES = [
    ("send.py", "scripts/send.py"),
    ("unity_mcp_server.py", "scripts/unity_mcp_server.py"),
    ("SKILL.md", "SKILL.md"),
]


def resolve_bridge_sources():
    asset_bridge = os.path.join(CS_SOURCE, BRIDGE_DIR_IN_PKG)
    skill_dir = os.path.join(UNITY_PROJECT, ".workbuddy", "skills", "unity-cmd")
    resolved = []
    for name, skill_rel in BRIDGE_NAMES:
        candidates = [
            os.path.join(asset_bridge, name),
            os.path.join(skill_dir, skill_rel.replace("/", os.sep)),
        ]
        resolved.append((next((c for c in candidates if os.path.isfile(c)), candidates[0]),
                         BRIDGE_DIR_IN_PKG + "/" + name))
    return resolved


BRIDGE_SOURCES = resolve_bridge_sources()

# 包内只允许出现这两类内容：ECS（编辑器侧）+ McpBridge（桥脚本）。
# **绝不收工程运行期代码**——MCP 与工程完全解耦，工程可选地通过反射约定
# （MainUIRootBuilder.Ensure）反哺结构定义，见 ECS README「工程结构挂钩」。

DEFAULT_OUTPUT = os.path.join(HERE, "UnityPackage", "UnityCommandBridge.unitypackage")

# 不需要进包的（本机临时产物）
EXCLUDE_SUFFIXES = (".tmp", ".bak")
EXCLUDE_NAMES = ("__pycache__",)


# ---------------------------------------------------------------- meta 生成


def folder_meta(guid):
    return (
        "fileFormatVersion: 2\n"
        "guid: %s\n"
        "folderAsset: yes\n"
        "DefaultImporter:\n"
        "  externalObjects: {}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    ) % guid


def default_meta(guid):
    """未知扩展名（.py）用 DefaultImporter，Unity 会当成普通资产携带。"""
    return (
        "fileFormatVersion: 2\n"
        "guid: %s\n"
        "DefaultImporter:\n"
        "  externalObjects: {}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    ) % guid


def text_meta(guid):
    """.md 这类纯文本资产。"""
    return (
        "fileFormatVersion: 2\n"
        "guid: %s\n"
        "TextScriptImporter:\n"
        "  externalObjects: {}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    ) % guid


def meta_for(extension):
    if extension.lower() in (".md", ".txt", ".json", ".yml", ".yaml"):
        return text_meta
    return default_meta


# ---------------------------------------------------------------- 收集资产


class Asset(object):
    def __init__(self, path_in_pkg, content, meta_text, guid, origin, is_folder=False):
        self.path_in_pkg = path_in_pkg
        self.content = content
        self.meta_text = meta_text
        self.guid = guid
        self.origin = origin
        self.is_folder = is_folder


def read_existing_guid(meta_path):
    try:
        with open(meta_path, "r", encoding="utf-8") as handle:
            for line in handle:
                if line.startswith("guid:"):
                    return line.split(":", 1)[1].strip()
    except OSError:
        pass
    return None


def collect_csharp(warnings):
    """C# 目录下的所有文件（不含 .meta，meta 单独配对）。

    `CS_SOURCE/Project/`（工程客制化目录）只在顶层排除，整体跳过、连目录条目都不进包；
    顶层以下同名子目录不受影响（客制化约定只针对 ECS 根下的这一层）。
    """
    assets = []
    if not os.path.isdir(CS_SOURCE):
        raise SystemExit("找不到 C# 源目录：%s" % CS_SOURCE)

    for dirpath, dirnames, filenames in os.walk(CS_SOURCE):
        dirnames[:] = [d for d in dirnames if d not in EXCLUDE_NAMES]
        if os.path.normpath(dirpath) == os.path.normpath(CS_SOURCE) and PROJECT_COUPLED_DIR_NAME in dirnames:
            dirnames.remove(PROJECT_COUPLED_DIR_NAME)
            warnings.append("已排除工程客制化目录（不进包）：%s"
                            % os.path.join(CS_SOURCE, PROJECT_COUPLED_DIR_NAME))
        for name in sorted(filenames):
            if name.endswith(".meta"):
                continue
            if any(name.endswith(s) for s in EXCLUDE_SUFFIXES):
                continue

            full = os.path.join(dirpath, name)
            rel = os.path.relpath(full, CS_SOURCE).replace("\\", "/")
            path_in_pkg = PKG_ROOT + "/" + rel

            meta_path = full + ".meta"
            guid = read_existing_guid(meta_path)
            if guid:
                with open(meta_path, "r", encoding="utf-8") as handle:
                    meta_text = handle.read()
            else:
                # 缺 meta：新 GUID + 生成的 importer。
                # 注意：Unity 里已有资产缺 meta 说明它从未被导入过，这种情况很少；
                # 若确实缺，用新 GUID 不会破坏包外的引用（包外本来就引用不到它）。
                guid = uuid.uuid4().hex
                meta_text = meta_for(os.path.splitext(name)[1])(guid)
                warnings.append("缺 .meta，已生成新 GUID：%s" % path_in_pkg)

            with open(full, "rb") as handle:
                content = handle.read()

            assets.append(Asset(path_in_pkg, content, meta_text, guid, full))

    return assets


def collect_bridge(warnings):
    """补齐 MCP 桥到包内 McpBridge/ 下。

    已经是"桥就住在 Assets 里"的工程（本仓库的形态）：文件本来就在 CS_SOURCE 里，
    `collect_csharp` 已经把它们连同磁盘上的 `.meta`（GUID 稳定）一起收走了，
    这里**必须跳过**，否则同一个 pathname 会以两个不同 GUID 各进一次包，
    Unity 导入时是重复资产。
    只有老布局（桥在 `.workbuddy/skills/unity-cmd/`、Assets 里没有副本）才在这里补。
    """
    assets = []
    for source, rel in BRIDGE_SOURCES:
        if os.path.isfile(os.path.join(CS_SOURCE, rel.replace("/", os.sep))):
            warnings.append("桥已在 Assets 内，由 C# 收集阶段带上（不重复打包）：%s" % rel)
            continue
        if not os.path.isfile(source):
            warnings.append("缺少 MCP 文件，已跳过：%s" % source)
            continue

        path_in_pkg = PKG_ROOT + "/" + rel
        guid = uuid.uuid4().hex
        with open(source, "rb") as handle:
            content = handle.read()

        assets.append(Asset(path_in_pkg, content, meta_for(os.path.splitext(source)[1])(guid),
                            guid, source))

    return assets


def collect_folders(file_paths, warnings):
    """为包内所有需要出现的目录生成条目（含 PKG_ROOT 本身）。

    Unity 自己导出的包里，**每个资产都有一个 `<guid>` 目录条目**（tar typeflag '5'），
    目录型资产再额外省略 `asset` 文件。缺了这个目录条目，Unity 的导入器不会把
    `<guid>/asset` 当成一个资产，表现为"导入毫无反应"。

    目录的 guid 优先沿用磁盘上的 `<dir>.meta`（保持与源工程一致，
    否则每次打包目录 guid 都变，重复导入会生成新目录而不是覆盖）。
    """
    wanted = set()
    for path_in_pkg in file_paths:
        parts = path_in_pkg.split("/")
        for i in range(1, len(parts)):
            wanted.add("/".join(parts[:i]))

    assets = []
    for folder in sorted(wanted):
        if not folder.startswith(PKG_ROOT):
            continue
        if folder.startswith(PROJECT_COUPLED_IN_PKG):
            warnings.append("目录条目命中工程客制化路径，已排除：%s" % folder)
            continue

        disk_meta = os.path.join(UNITY_PROJECT, folder + ".meta")
        guid = read_existing_guid(disk_meta)
        if guid:
            with open(disk_meta, "r", encoding="utf-8") as handle:
                meta_text = handle.read()
        else:
            # 虚拟目录（McpBridge）：磁盘上没有，生成新 guid
            if not folder.endswith("/McpBridge"):
                warnings.append("目录缺 .meta，已生成新 GUID：%s" % folder)
            guid = uuid.uuid4().hex
            meta_text = folder_meta(guid)

        assets.append(Asset(folder, b"", meta_text, guid, "<folder>", is_folder=True))

    return assets


# ---------------------------------------------------------------- 写包


def write_package(assets, output, warnings):
    # 终检（放在写包层，任何调用路径都拦）：包内出现任何工程客制化路径都视为
    # 打包器 bug，直接失败——宁可打不出包，不可漏排。
    leaked = [a.path_in_pkg for a in assets if a.path_in_pkg.startswith(PROJECT_COUPLED_IN_PKG)]
    if leaked:
        raise SystemExit(
            "工程客制化内容漏进了包（%s 下共 %d 条）——MCP 与工程的耦合内容不允许分发：\n  %s"
            % (PROJECT_COUPLED_IN_PKG, len(leaked), "\n  ".join(leaked[:10])))

    guids = {}
    paths = {}
    for asset in assets:
        if asset.guid in guids:
            raise SystemExit(
                "GUID 冲突：%s\n  %s\n  %s"
                % (asset.guid, guids[asset.guid], asset.path_in_pkg))
        guids[asset.guid] = asset.path_in_pkg
        # 同一个路径进两条 = 导入后是重复资产（不同 GUID 会互相覆盖或并存），
        # 典型来源：桥脚本既在 Assets 里、又被 collect_bridge 从 .workbuddy 补了一遍。
        if asset.path_in_pkg in paths:
            raise SystemExit(
                "包内路径重复：%s\n  来源1：%s\n  来源2：%s"
                % (asset.path_in_pkg, paths[asset.path_in_pkg], asset.origin))
        paths[asset.path_in_pkg] = asset.origin

    out_dir = os.path.dirname(os.path.abspath(output))
    if out_dir and not os.path.isdir(out_dir):
        os.makedirs(out_dir)

    def add_bytes(tar, name, data):
        info = tarfile.TarInfo(name)
        info.size = len(data)
        info.mode = 0o644
        info.mtime = 1700000000          # 固定 mtime：同样输入产出同样的包，便于比对
        tar.addfile(info, io.BytesIO(data))

    def add_dir(tar, name):
        """每个资产都要有一个 `<guid>` 目录条目（typeflag '5'），Unity 靠它识别条目边界。"""
        info = tarfile.TarInfo(name)
        info.type = tarfile.DIRTYPE
        info.size = 0
        info.mode = 0o755
        info.mtime = 1700000000
        tar.addfile(info)

    # 先把 tar 写进内存，再按 Unity 自己的 gzip 约定封装。
    #
    # ★ 千万不要用 tarfile.open(path, "w:gz")：它会把**输出文件名**写进 gzip 头的
    #   FNAME 字段（这里是 "UnityCommandBridge.unitypackage"）。Unity 的导入器读到
    #   这个 FNAME 就静默放弃——不报错、不弹窗、导入 0 个资产，日志里只有一句
    #   "Import package from :<路径> !" 然后正常退出，极难排查。
    #   Unity 自己导出的包，FNAME 固定是 "archtemp.tar"。这里照抄。
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w", format=tarfile.USTAR_FORMAT) as tar:
        for asset in assets:
            prefix = asset.guid
            add_dir(tar, prefix)
            if not asset.is_folder:
                add_bytes(tar, prefix + "/asset", asset.content)
            add_bytes(tar, prefix + "/asset.meta", asset.meta_text.encode("utf-8"))
            add_bytes(tar, prefix + "/pathname", asset.path_in_pkg.encode("utf-8"))

    with open(output, "wb") as handle:
        with gzip.GzipFile(filename=_GZIP_FNAME, mode="wb", fileobj=handle, mtime=0) as gz:
            gz.write(buf.getvalue())

    return output


# ---------------------------------------------------------------- 入口


def main(argv=None):
    parser = argparse.ArgumentParser(description="把 ExternalCommandSystem + MCP 桥打成 .unitypackage")
    parser.add_argument("-o", "--output", default=DEFAULT_OUTPUT, help="输出文件路径")
    parser.add_argument("--list-only", action="store_true", help="只列出将要打包的文件，不生成")
    args = parser.parse_args(argv)

    warnings = []
    assets = collect_csharp(warnings)
    assets += collect_bridge(warnings)
    assets += collect_folders([a.path_in_pkg for a in assets], warnings)

    # 按路径排序，输出稳定
    assets.sort(key=lambda a: a.path_in_pkg)

    folders = sum(1 for a in assets if a.is_folder)
    files = len(assets) - folders

    if args.list_only:
        print("将打包 %d 个资产（%d 文件 + %d 目录）：" % (len(assets), files, folders))
        for asset in assets:
            print("  %-4s %-72s %s" % ("目录" if asset.is_folder else "文件",
                                       asset.path_in_pkg, asset.guid[:8]))
        for message in warnings:
            print("  [警告] " + message)
        return 0

    output = write_package(assets, args.output, warnings)
    size_kb = os.path.getsize(output) / 1024.0

    print("已生成：%s" % os.path.abspath(output))
    print("  资产 %d 个（%d 文件 + %d 目录），%.1f KB" % (len(assets), files, folders, size_kb))
    for message in warnings:
        print("  [警告] " + message)

    print("\n包内结构（部分）：")
    for asset in assets:
        if not asset.is_folder and not asset.path_in_pkg.endswith((".cs", ".py", ".md")):
            continue
        print("  " + asset.path_in_pkg)

    return 0


if __name__ == "__main__":
    sys.exit(main())
