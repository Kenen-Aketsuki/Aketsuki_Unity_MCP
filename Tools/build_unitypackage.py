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

包身份（名称 / 版本 / 作者 / 打包日期）写进包内 `package.json` 资产——Unity 的
.unitypackage 格式没有任何标准的元数据字段（gzip FNAME 还必须固定为 archtemp.tar，
见 write_package 的注释），所以只能在包里放一个普通资产当清单。装到工程后它同时
是"这个工程装的是哪个版本"的唯一凭据。

用法：
    python Tools/build_unitypackage.py
    python Tools/build_unitypackage.py --version 1.1 --built 2026-11-01
    python Tools/build_unitypackage.py -o D:/tmp/X.unitypackage
    python Tools/build_unitypackage.py --bundle      # 额外生成可分发目录（含安装器）
    python Tools/build_unitypackage.py --list-only
"""

import argparse
import datetime
import gzip
import io
import json
import os
import shutil
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

# 技能目录名（.workbuddy/skills/<这个名字>/）：老布局下桥脚本落在这里
SKILL_NAME = "unity-cmd"


def resolve_bridge_sources():
    asset_bridge = os.path.join(CS_SOURCE, BRIDGE_DIR_IN_PKG)
    skill_dir = os.path.join(UNITY_PROJECT, ".workbuddy", "skills", SKILL_NAME)
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


def apply_module(name):
    """切换要打包的模块：mcp（默认）或 adb。

    两个工具是并列关系，各有自己的源码根、桥脚本、技能目录与包名。
    """
    global PKG_ROOT, CS_SOURCE, BRIDGE_DIR_IN_PKG, BRIDGE_NAMES, SKILL_NAME
    global PROJECT_COUPLED_IN_PKG, PROJECT_COUPLED_DIR_NAME, BRIDGE_SOURCES
    global PKG_NAME, DEFAULT_OUTPUT, ACTIVE_MODULE, SERVER_SCRIPT
    global SKILL_FILES, ENTRY_CONSOLE, ENTRY_HTTP, REQUIRES

    if name == "adb":
        ACTIVE_MODULE = "adb"
        PKG_ROOT = "Assets/Editor/AdbDebugger"
        CS_SOURCE = os.path.join(UNITY_PROJECT, "Assets", "Editor", "AdbDebugger")
        # **本包没有桥**：ADB 调试台只做"把设备日志抓进 Unity 控制台"这一件事，
        # 自己不提供 MCP server，也不装命令行客户端。要让 AI 抓日志，装「Unity MCP 指令系统」，
        # 它里面的 adb.* 指令会反射调用本模块的 AdbExternalApi（见 Commands/Adb/）。
        BRIDGE_DIR_IN_PKG = ""
        BRIDGE_NAMES = []
        SKILL_FILES = []
        SKILL_NAME = ""
        SERVER_SCRIPT = ""
        ENTRY_CONSOLE = "Tools / ADB 调试 / ADB 调试台"
        ENTRY_HTTP = ""
        REQUIRES = [
            "Unity 2021.3 及以上",
            "adb 可执行文件（Android SDK platform-tools，可在设置里填完整路径）",
        ]
        # ADB 工具没有工程客制化目录；指向一个不存在的路径，终检自然恒过
        PROJECT_COUPLED_IN_PKG = PKG_ROOT + "/Project"
        PROJECT_COUPLED_DIR_NAME = "Project"
        PKG_NAME = "Aketsuki_AdbDebugger"
        DEFAULT_OUTPUT = os.path.join(HERE, "UnityPackage", PKG_NAME + "-" + PKG_VERSION + ".unitypackage")
    else:
        ACTIVE_MODULE = "mcp"
        PKG_ROOT = "Assets/Editor/ExternalCommandSystem"
        CS_SOURCE = os.path.join(UNITY_PROJECT, "Assets", "Editor", "ExternalCommandSystem")
        BRIDGE_DIR_IN_PKG = "McpBridge"
        BRIDGE_NAMES = [
            ("send.py", "scripts/send.py"),
            ("unity_mcp_server.py", "scripts/unity_mcp_server.py"),
            ("SKILL.md", "SKILL.md"),
        ]
        SKILL_NAME = "unity-cmd"
        SERVER_SCRIPT = "unity_mcp_server.py"
        SKILL_FILES = [
            ("send.py", "scripts/send.py"),
            ("unity_mcp_server.py", "scripts/unity_mcp_server.py"),
            ("SKILL.md", "SKILL.md"),
        ]
        ENTRY_CONSOLE = "Tools / 指令系统 / 指令控制台"
        ENTRY_HTTP = "Tools / 指令系统 / HTTP 指令服务"
        REQUIRES = ["Unity 2021.3 及以上", "Python 3.8 及以上（仅 MCP 桥需要）"]
        PROJECT_COUPLED_IN_PKG = PKG_ROOT + "/Project"
        PROJECT_COUPLED_DIR_NAME = "Project"
        PKG_NAME = "Aketsuki_Unity_MCP"
        DEFAULT_OUTPUT = os.path.join(HERE, "UnityPackage", "UnityCommandBridge.unitypackage")

    BRIDGE_SOURCES = resolve_bridge_sources()


ACTIVE_MODULE = "mcp"
SERVER_SCRIPT = "unity_mcp_server.py"

# 装到 `<工程>/.workbuddy/skills/<SKILL_NAME>/` 的文件，形如 (包内桥目录里的相对路径, 技能目录里的路径)——
# **两者常不一样**（桥在 Assets 里平铺，技能目录要求 python 落在 scripts/ 下）。
# 以及清单里记的菜单入口与依赖说明。这些都是**按模块变**的，由 apply_module 覆盖——
# install_unitypackage.py 读清单里的 skillName / skillFiles 决定往哪写，不再硬编码。
SKILL_FILES = [
    ("send.py", "scripts/send.py"),
    ("unity_mcp_server.py", "scripts/unity_mcp_server.py"),
    ("SKILL.md", "SKILL.md"),
]
ENTRY_CONSOLE = "Tools / 指令系统 / 指令控制台"
ENTRY_HTTP = "Tools / 指令系统 / HTTP 指令服务"
REQUIRES = ["Unity 2021.3 及以上", "Python 3.8 及以上（仅 MCP 桥需要）"]

# 包内只允许出现这两类内容：ECS（编辑器侧）+ McpBridge（桥脚本）。
# **绝不收工程运行期代码**——MCP 与工程完全解耦，工程可选地通过反射约定
# （MainUIRootBuilder.Ensure）反哺结构定义，见 ECS README「工程结构挂钩」。

DEFAULT_OUTPUT = os.path.join(HERE, "UnityPackage", "UnityCommandBridge.unitypackage")

# 包身份默认值（可用命令行覆盖）
PKG_NAME = "Aketsuki_Unity_MCP"
PKG_VERSION = "1.0"
PKG_AUTHOR = "Aketsuki"

# 包内清单资产（跟着 ECS 一起落到工程，充当版本凭据）
MANIFEST_REL = "package.json"

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
            # package.json 由 build_manifest 虚拟注入。源工程自己装过包的话磁盘上会有一份，
            # 再不跳过就会和虚拟资产撞成"包内路径重复"。
            if name == MANIFEST_REL and os.path.normpath(dirpath) == os.path.normpath(CS_SOURCE):
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
            # 虚拟目录（桥脚本）：磁盘上没有，生成新 guid
            if not folder.endswith("/" + BRIDGE_DIR_IN_PKG):
                warnings.append("目录缺 .meta，已生成新 GUID：%s" % folder)
            guid = uuid.uuid4().hex
            meta_text = folder_meta(guid)

        assets.append(Asset(folder, b"", meta_text, guid, "<folder>", is_folder=True))

    return assets


# ---------------------------------------------------------------- 包清单

def read_unity_version():
    """源工程的 Unity 版本，写进清单给接收方做兼容性判断。"""
    path = os.path.join(UNITY_PROJECT, "ProjectSettings", "ProjectVersion.txt")
    try:
        with open(path, "r", encoding="utf-8") as handle:
            for line in handle:
                if line.startswith("m_EditorVersion:"):
                    return line.split(":", 1)[1].strip()
    except OSError:
        pass
    return None


def stable_guid(path_in_pkg):
    """清单资产的 GUID 由路径派生：每次打包都一样，重复导入是覆盖不是新增。"""
    return uuid.uuid5(uuid.NAMESPACE_URL, "aketsuki-unity-mcp/" + path_in_pkg).hex


def build_manifest(name, version, author, built, file_count, output_name, warnings):
    """包内 `package.json`。Unity 的 .unitypackage 没有标准元数据槽位，只能自带清单。"""
    entrypoints = {"console": ENTRY_CONSOLE}
    if ENTRY_HTTP:
        entrypoints["httpServer"] = ENTRY_HTTP
    if BRIDGE_DIR_IN_PKG and SERVER_SCRIPT:
        entrypoints["mcpBridge"] = PKG_ROOT + "/" + BRIDGE_DIR_IN_PKG + "/" + SERVER_SCRIPT
        entrypoints["skillDoc"] = PKG_ROOT + "/" + BRIDGE_DIR_IN_PKG + "/SKILL.md"

    manifest = {
        "name": name,
        "version": version,
        "author": author,
        "built": built,
        # 哪个工具的包：安装器据此选技能目录 / 兜底布局，比按文件名猜稳
        "module": ACTIVE_MODULE,
        "unityVersion": read_unity_version(),
        "packageFile": output_name,
        "installRoot": PKG_ROOT,
        "files": file_count,
        "entrypoints": entrypoints,
        # 安装器据此决定把桥同步到 `.workbuddy/skills/<skillName>/` 的哪几份文件、各叫什么路径。
        # **键存在但值为空 = 本包不装技能目录**（ADB 包就是这样，它没有桥）。
        # 老包（v1.0）根本没有这几个键，安装器有回落规则，不是必需的。
        "skillName": SKILL_NAME,
        "skillFiles": [{"from": src, "to": dst} for src, dst in SKILL_FILES],
        "requires": list(REQUIRES),
    }
    text = json.dumps(manifest, ensure_ascii=False, indent=2) + "\n"
    path_in_pkg = PKG_ROOT + "/" + MANIFEST_REL
    guid = stable_guid(path_in_pkg)
    warnings.append("包清单：%s v%s（%s）" % (name, version, built))
    return Asset(path_in_pkg, text.encode("utf-8"), meta_for(".json")(guid), guid, "<manifest>")


# ---------------------------------------------------------------- 分发目录

BUNDLE_DOC = """# {name} v{version} — 部署说明

> 作者：{author}　打包日期：{built}　Unity：{unity}
> 本文件由 `Tools/build_unitypackage.py --bundle` 自动生成，勿手改（改模板请改脚本）。

## 这个目录是什么

Unity 编辑器指令系统（ExternalCommandSystem）+ MCP 桥的**可分发包**。
接收方只要有 `{name}-{version}.unitypackage` 和本目录里的 `install.py`，就能装进任意 Unity 工程。

**不要用 `deploy.py`**：它只在本仓库里有意义（从 `Assets/Editor/ExternalCommandSystem`
同步到本机多个工程）。它在单独拷出去的目录里会直接报错——上一级没有 `Assets/`。

## 装到指定工程

```bash
python install.py --project D:/路径/到/Unity工程根 --dry-run   # 先看会写入什么
python install.py --project D:/路径/到/Unity工程根             # 落地
```

`--project` 指向含 `Assets/` 和 `ProjectSettings/` 的那一层，**不是**含 `ProjectSettings` 的上一级。

它会做两件事：

1. 把包内 {files} 个资产解包到 `<工程>/{installRoot}/`
2. 把 MCP 桥同步到 `<工程>/.workbuddy/skills/unity-cmd/`（`scripts/send.py`、
   `scripts/unity_mcp_server.py`、`SKILL.md`）——MCP 客户端拉起的是这一份，不是 Assets 里那份。
   不需要就加 `--no-skill`。

规则：**只新增/覆盖，绝不删除**；目标工程已有的 `.meta` 一律保留（保住旧 GUID，否则引用会丢）。
包内文件按 LF 写入，不影响编译；若目标工程的工作区是 CRLF，git 会把这些文件显示成全量改动。

## 装完还要让它生效

1. **让 Unity 刷新**：聚焦 Unity 窗口自动导入，或走桥发 `asset.refresh`。
2. **确认菜单出现**：Unity 菜单栏 → `Tools / 指令系统 / 指令控制台`。
3. **开 HTTP 服务**：`Tools / 指令系统 / HTTP 指令服务`，端口默认从 57831 起避让。
4. **接 MCP**（可选，要用 AI 驱动才需要）：在 `~/.workbuddy/mcp.json` 的 `mcpServers` 里加

```json
"unity-cmd-你的工程名": {{
  "command": "python",
  "args": [
    "<工程根>/.workbuddy/skills/unity-cmd/scripts/unity_mcp_server.py",
    "--project-root", "<工程根>"
  ]
}}
```

`--project-root` 必须是绝对路径且指向工程根——桥用它做严格选址，目标对不上会直接失败，
不会"猜一个"连上去（猜错过一次，`file.write` 改到了别的工程）。

## 自检

```bash
python <工程根>/.workbuddy/skills/unity-cmd/scripts/send.py status
```

能看到 `port` / `projectPath` 就通了。之后 `unity_status`、`unity_list_commands` 也可以。

## 装之前确认版本

包内 `{installRoot}/package.json` 是版本凭据。`install.py` 会把它和工程里已有的对比。
**不要拿旧包覆盖新工程**：本安装器只增不删，旧包会把被改过的文件回退、却留下新增文件，
结果是编译不过的混合态。
"""


BUNDLE_DOC_ADB = """# {name} v{version} — 部署说明

> 作者：{author}　打包日期：{built}　Unity：{unity}
> 本文件由 `Tools/build_unitypackage.py --module adb --bundle` 自动生成，勿手改。

## 这个目录是什么

**ADB 调试台**的可分包：把 Android 真机 / 模拟器的 logcat 抓进 Unity 控制台。
它**不含任何 MCP 服务、不含端口、不含令牌**，装上就能用，不需要配置客户端。

AI 想抓日志时装「Unity MCP 指令系统」那一个包即可——它里面的 `adb.*` 指令会反射调用本工具的
`AdbExternalApi`。两个包可以都装，也可以只装这一个、纯手工看日志。

接收方只要有 `{name}-{version}.unitypackage` 和本目录里的 `install.py` 就能装。

## 装到指定工程

```bash
python install.py --project D:/路径/到/Unity工程根 --dry-run   # 先看会写入什么
python install.py --project D:/路径/到/Unity工程根             # 落地
```

`--project` 指向含 `Assets/` 和 `ProjectSettings/` 的那一层。
它会把这 {files} 个资产解包到 `<工程>/{installRoot}/`；**不写 .workbuddy、不装技能目录**。

规则：**只新增/覆盖，绝不删除**；目标工程已有的 `.meta` 一律保留（保住旧 GUID）。

## 用起来

1. **让 Unity 刷新**：聚焦 Unity 窗口自动导入。
2. 菜单 `Tools / ADB 调试 / ADB 调试台`：
   - 「设备」下拉是现场 `adb devices -l` 的结果，选中即写设置；不可用的设备会把原因写在脸上。
   - 勾「启用 logcat」开始抓，日志以 `[ADB] ` 前缀进控制台（Tag 过滤默认 `Unity`）。
3. 找不到 adb 时在窗口里手填 `adb.exe` 完整路径（通常在 `<Android SDK>/platform-tools/adb.exe`）。

## 和指令系统的关系

| | Unity MCP 指令系统 | 本工具 |
| --- | --- | --- |
| 对外接口 | HTTP 57831 + MCP 桥 | **没有**（只开窗口） |
| 技能目录 | `.workbuddy/skills/unity-cmd/` | 无 |
| AI 怎么用 | `unity_execute` 等工具 | 装指令系统后调 `adb.devices` / `adb.logcat.*` |

交集只有一处：本工具把日志打进 Unity 控制台；指令系统那侧的 `adb.*` 指令反射调用本工具的
`AdbExternalApi`，把它翻译成 MCP 工具 `unity_adb_devices` 等。

## 版本

包内 `{installRoot}/package.json` 是版本凭据。**不要拿旧包覆盖新工程**：
安装器只增不删，旧包会把改过的文件回退、却留下新增文件，结果可能是编译不过的混合态。
"""


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


def write_bundle(package_path, name, version, author, built, install_root, file_count, warnings):
    """生成可分发目录：包 + 安装器 + 部署说明 + 清单副本。

    光给一个 .unitypackage 不够——接收方得自己拆 tar（guid 目录 / pathname 文件）、
    还得知道"目标已有 .meta 不能覆盖"，手写这个的人十有八九会漏。所以安装器得一起给。
    """
    dist_dir = os.path.join(os.path.dirname(os.path.abspath(package_path)),
                            "%s-%s" % (name, version))
    if os.path.isdir(dist_dir):
        shutil.rmtree(dist_dir)
    os.makedirs(dist_dir)

    package_name = os.path.basename(package_path)
    target_pkg = os.path.join(dist_dir, package_name)
    shutil.copy2(package_path, target_pkg)

    # 安装器：源码唯一，原样拷贝。它从包内 package.json 读 installRoot / skillName / skillFiles，
    # 不写死路径，所以两个工具的包共用这一份；同目录有多个包时靠 --module / -p 指定。
    with open(os.path.join(HERE, "install_unitypackage.py"), "r", encoding="utf-8") as handle:
        source = handle.read()
    source = source.replace(
        '"""把 .unitypackage 直接解包落地到 Unity 工程（不经过 Unity 的导入器）。',
        '"""把 .unitypackage 直接解包落地到 Unity 工程（不经过 Unity 的导入器）。\n\n'
        '【分发副本】由 Tools/build_unitypackage.py --bundle 生成，勿在本目录改（改源仓库那份）。',
        1)
    with open(os.path.join(dist_dir, "install.py"), "w", encoding="utf-8") as handle:
        handle.write(source)

    template = BUNDLE_DOC_ADB if ACTIVE_MODULE == "adb" else BUNDLE_DOC
    doc = template.format(name=name, version=version, author=author, built=built,
                          unity=read_unity_version() or "未记录",
                          files=file_count, installRoot=install_root,
                          skillName=SKILL_NAME)
    with open(os.path.join(dist_dir, "DEPLOY.md"), "w", encoding="utf-8") as handle:
        handle.write(doc)

    manifest = {
        "name": name, "version": version, "author": author, "built": built,
        "unityVersion": read_unity_version(), "packageFile": package_name,
        "installRoot": install_root, "files": file_count,
    }
    with open(os.path.join(dist_dir, "manifest.json"), "w", encoding="utf-8") as handle:
        handle.write(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")

    warnings.append("分发目录：%s" % dist_dir)
    return dist_dir


# ---------------------------------------------------------------- 入口


def main(argv=None):
    parser = argparse.ArgumentParser(description="把 ExternalCommandSystem + MCP 桥打成 .unitypackage")
    parser.add_argument("-o", "--output", help="输出文件路径（默认 Tools/UnityPackage/<名称>-<版本>.unitypackage）")
    parser.add_argument("--module", choices=("mcp", "adb"), default="mcp",
                        help="打包哪个工具：mcp（默认，ExternalCommandSystem）或 adb（AdbDebugger）")
    parser.add_argument("--name", default=None, help="包名称（默认按模块自动取）")
    parser.add_argument("--version", default=PKG_VERSION, help="版本号（默认 %s）" % PKG_VERSION)
    parser.add_argument("--author", default=PKG_AUTHOR, help="作者（默认 %s）" % PKG_AUTHOR)
    parser.add_argument("--built", default=datetime.date.today().isoformat(),
                        help="打包日期 YYYY-MM-DD（默认今天）")
    parser.add_argument("--bundle", action="store_true",
                        help="额外生成可分发目录（包 + install.py + DEPLOY.md + manifest.json）")
    parser.add_argument("--list-only", action="store_true", help="只列出将要打包的文件，不生成")
    args = parser.parse_args(argv)

    apply_module(args.module)
    name = args.name or PKG_NAME
    print("模块：%s（%s）" % (args.module, CS_SOURCE))

    output = args.output or os.path.join(
        HERE, "UnityPackage", "%s-%s.unitypackage" % (name, args.version))

    warnings = []
    assets = collect_csharp(warnings)
    assets += collect_bridge(warnings)

    file_count = sum(1 for a in assets if not a.is_folder) + 1   # +1 = 清单自身
    assets.append(build_manifest(name, args.version, args.author, args.built,
                                 file_count, os.path.basename(output), warnings))
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

    output = write_package(assets, output, warnings)
    size_kb = os.path.getsize(output) / 1024.0

    print("已生成：%s" % os.path.abspath(output))
    print("  %s v%s（%s）作者 %s" % (name, args.version, args.built, args.author))
    print("  资产 %d 个（%d 文件 + %d 目录），%.1f KB" % (len(assets), files, folders, size_kb))

    if args.bundle:
        write_bundle(output, name, args.version, args.author, args.built,
                     PKG_ROOT, file_count, warnings)

    for message in warnings:
        print("  [警告] " + message)

    print("\n包内结构（部分）：")
    for asset in assets:
        if not asset.is_folder and not asset.path_in_pkg.endswith((".cs", ".py", ".md", ".json")):
            continue
        print("  " + asset.path_in_pkg)

    return 0


if __name__ == "__main__":
    sys.exit(main())
