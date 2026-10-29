#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把指令系统同步部署到多个 Unity 工程。

    python3 Tools/deploy.py --list              查看目标清单与同步状态
    python3 Tools/deploy.py --check             只报告差异，不写文件
    python3 Tools/deploy.py                     部署到全部目标
    python3 Tools/deploy.py island majotavern   只部署名字能匹配上的目标
    python3 Tools/deploy.py --dry-run           显示将写入哪些文件
    python3 Tools/deploy.py --verify            部署后让目标工程编译并回传错误
    python3 Tools/deploy.py --prune             额外删除目标侧的孤儿文件（默认只报告）

设计要点（改脚本前先读，不要退回）：

1. **比对忽略行尾**。目标工程 core.autocrlf=true，工作区是 CRLF，本仓库文件是 LF。
   按字节比会把每个文件都判成"不同"，每次部署都在对方仓库里留下一片整文件 diff。
   写入时沿用目标工程既有行尾（探针见 detect_eol），新文件默认 CRLF（Windows 工程）。

2. **目标已有的 .meta 一律保留**。GUID 是场景/预制体/引用的锚点，覆盖等于断引用。
   本仓库的 .meta 与目标工程的 .meta GUID 本来就不同（各自 Unity 生成），
   所以"GUID 不一致"不算差异，只在 --check 里作为提示列出。
   仅当目标缺 .meta 时才从仓库补一份。

3. **Project/ 是工程专属目录**。仓库里只有一份占位说明，不覆盖目标工程里的客制化内容；
   目标缺该目录时才创建并写入说明。--no-project 可完全跳过。

4. **只写有差异的文件**，且用临时文件 + os.replace 原子替换。
   目标 Unity 多半开着，半截文件会被它的文件监视器读到并触发一次失败的重编译。

5. **孤儿文件默认只报告不删**。指令文件在仓库里被删除后，目标侧会留下编译不通过的旧类；
   确认后用 --prune 清理。

6. **`.workbuddy/skills/unity-cmd/` 也是部署对象**（IMPR-007，2026-10-25 拍板）。
   MCP 客户端真正拉起的是那份 `unity_mcp_server.py`，不是 `Assets/` 里那一份；
   以前只同步 Assets → 各工程的 SKILL.md 常年停在旧版（AI 读到的指令说明是过期的）。
   默认开，`--no-skill` 可关。**目标那份若比仓库新（mtime 更晚）且内容不同，不覆盖**：
   那通常意味着有人在工程里改过桥脚本，覆盖等于吃掉他的改动；报冲突，`--force-skill` 才硬写。
"""

from __future__ import print_function

import argparse
import os
import re
import shutil
import subprocess
import sys
import time

# ---------------------------------------------------------------- 常量

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)                                   # 仓库根 = Tools/ 的上一级
CONFIG_PATH = os.path.join(HERE, "deploy_targets.json")

# 两个并列的工具，各有自己的源码根、MCP 桥副本、技能目录与程序集。
# 它们共用同一批目标工程，但互不依赖：可以只部署其中一个（--module adb）。
MODULES = {
    "mcp": {
        "title": "Unity MCP 指令系统",
        # 2026-10-29 起是 UPM 包形态（embedded package）：源根 = 包根，
        # 目标落地到 <工程>/Packages/com.aketsuki.unity-mcp/，不再是 Assets 下的资产。
        "src_rel": "Packages/com.aketsuki.unity-mcp",
        # send.py 等桥脚本在包内的 Editor/McpBridge（包根下多一层 Editor）
        "bridge": "Editor/McpBridge",
        "skill_dir_rel": ".workbuddy/skills/unity-cmd",
        "skill_files": [
            ("SKILL.md", "SKILL.md"),
            ("send.py", "scripts/send.py"),
            ("unity_mcp_server.py", "scripts/unity_mcp_server.py"),
        ],
        # 包内代码带 asmdef，不并入预定义程序集；改名后是 Aketsuki.UnityMcp.Editor
        "assembly": "Aketsuki.UnityMcp.Editor",
        "has_project_dir": True,
        # 工程客制化目录（相对包根）。包形态下在 Editor/ 里
        "project_dir_rel": "Editor/Project",
        # 旧的资产形态目录。切包时必须整体退役，否则同名类型会在两个程序集里重复定义
        "legacy_rel": "Assets/Editor/ExternalCommandSystem",
        # 桥脚本目录（相对目标 dest_root），验证与刷新时用来找 send.py
        "bridge_rel": "Editor/McpBridge",
    },
    "adb": {
        "title": "ADB 调试",
        # 2026-10-29 起也是 UPM 包形态：源根 = 包根，目标落地到
        # <工程>/Packages/com.aketsuki.adb-debugger/，不再是 Assets 下的资产。
        "src_rel": "Packages/com.aketsuki.adb-debugger",
        # 本工具**没有桥、没有技能目录**：它不提供 MCP，AI 侧入口是指令系统的 adb.* 指令
        # （Commands/Adb/ 里反射调 AdbExternalApi）。留空是有意的，别照 MCP 那份补回来。
        "bridge": "",
        "skill_dir_rel": "",
        "skill_files": [],
        # 包内代码带 asmdef（2026-10-29 随切包改名，原 CivilRev.AdbDebugger.Editor）
        "assembly": "Aketsuki.AdbDebugger.Editor",
        "has_project_dir": False,
        "project_dir_rel": "Editor/Project",
        # 旧的资产形态目录。切包时必须整体退役，否则同名类型会在两个程序集里重复定义（CS0101）
        "legacy_rel": "Assets/Editor/AdbDebugger",
        "bridge_rel": "",
    },
}

ACTIVE_MODULE = "mcp"

# 下面这几个由 apply_module() 按当前模块填；保留模块级变量是为了让各函数不必层层传参
SRC_REL = ""
SRC_ROOT = ""
BRIDGE_ROOT = ""
SKILL_DIR_REL = ""
SKILL_FILES = []
ASSEMBLY = ""
HAS_PROJECT_DIR = True
PROJECT_DIR_REL = "Project"
LEGACY_REL = ""
BRIDGE_REL = ""


def apply_module(name):
    """切换当前部署的模块（mcp / adb）"""
    global ACTIVE_MODULE, SRC_REL, SRC_ROOT, BRIDGE_ROOT
    global SKILL_DIR_REL, SKILL_FILES, ASSEMBLY, HAS_PROJECT_DIR
    global PROJECT_DIR_REL, LEGACY_REL, BRIDGE_REL

    if name not in MODULES:
        raise SystemExit("未知模块：%s（可选：%s）" % (name, "、".join(MODULES)))

    module = MODULES[name]
    ACTIVE_MODULE = name
    SRC_REL = module["src_rel"]
    SRC_ROOT = os.path.join(REPO, *SRC_REL.split("/"))
    BRIDGE_ROOT = os.path.join(SRC_ROOT, module["bridge"]) if module["bridge"] else ""
    SKILL_DIR_REL = module["skill_dir_rel"]
    SKILL_FILES = module["skill_files"]
    ASSEMBLY = module["assembly"]
    HAS_PROJECT_DIR = module["has_project_dir"]
    PROJECT_DIR_REL = module.get("project_dir_rel") or "Project"
    LEGACY_REL = module.get("legacy_rel") or ""
    BRIDGE_REL = module.get("bridge_rel") or ""


apply_module("mcp")

PROJECT_DIR_NAME = "Project"        # 工程专属目录的名字（只用于判断"是不是客制化目录"）
SKIP_DIR_NAMES = {"__pycache__", ".git", ".vs", ".idea"}
SKIP_FILE_NAMES = {".DS_Store", "Thumbs.db"}

DEFAULT_EOL = b"\r\n"               # Windows Unity 工程

_EOL_CACHE = {}                     # 工程根 -> 行尾

# 离线编译校验器（跨工程通用的本机工具，找不到就跳过）
OFFLINE_COMPILER_CANDIDATES = [
    os.path.expanduser("~/.workbuddy/tools/unity-offline-compile/unity_offline_compile.py"),
    "C:/Users/poi77/.workbuddy/tools/unity-offline-compile/unity_offline_compile.py",
]


# ---------------------------------------------------------------- 小工具

def _utf8_stdout():
    """Git Bash / cmd 下中文输出不乱码。"""
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8")
        except Exception:
            pass


def rel_files(base, skip_top=None, skip_names=()):
    """列出 base 下所有文件的相对路径（用 / 分隔），可按顶层目录名排除。"""
    skip_top = set(skip_top or ())
    out = []
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = sorted(d for d in dirnames if d not in SKIP_DIR_NAMES)
        rel_dir = os.path.relpath(dirpath, base).replace("\\", "/")
        if rel_dir == ".":
            dirnames[:] = [d for d in dirnames if d not in skip_top]
            rel_dir = ""
        for name in sorted(filenames):
            if name in SKIP_FILE_NAMES or name in skip_names:
                continue
            out.append((rel_dir + "/" + name) if rel_dir else name)
    return out


def norm(data):
    """行尾归一，用于比较。"""
    return data.replace(b"\r\n", b"\n")


def read_bytes(path):
    with open(path, "rb") as handle:
        return handle.read()


def write_atomic(path, data):
    """临时文件 + 替换，避免 Unity 的文件监视器读到半截内容。"""
    directory = os.path.dirname(path)
    if directory and not os.path.isdir(directory):
        os.makedirs(directory)
    tmp = path + ".deploy-tmp"
    with open(tmp, "wb") as handle:
        handle.write(data)
    os.replace(tmp, path)


def to_eol(data, eol):
    """把内容统一成目标行尾。"""
    return norm(data).replace(b"\n", eol)


def detect_eol(root):
    """探测工程既有源码的行尾；拿不到就当 CRLF。

    只看 Assets 下的 .cs/.md，且有 64KB 上限——够判定，不读大文件。
    """
    if root in _EOL_CACHE:
        return _EOL_CACHE[root]
    eol = None
    assets = os.path.join(root, "Assets")
    for dirpath, dirnames, filenames in os.walk(assets):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIR_NAMES]
        for name in filenames:
            if not name.endswith((".cs", ".md")):
                continue
            try:
                with open(os.path.join(dirpath, name), "rb") as handle:
                    head = handle.read(65536)
            except OSError:
                continue
            if b"\r\n" in head:
                eol = b"\r\n"
                break
            if b"\n" in head:
                eol = b"\n"
                break
        if eol:
            break
    _EOL_CACHE[root] = eol or DEFAULT_EOL
    return _EOL_CACHE[root]


# ---------------------------------------------------------------- 目标配置

def load_targets():
    if not os.path.isfile(CONFIG_PATH):
        raise SystemExit("缺少配置文件：%s" % CONFIG_PATH)
    import json
    with open(CONFIG_PATH, "r", encoding="utf-8") as handle:
        data = json.load(handle)
    targets = data.get("targets") or []
    if not targets:
        raise SystemExit("配置文件里没有 targets：%s" % CONFIG_PATH)
    for item in targets:
        item["root"] = os.path.normpath(item["root"])
        item.setdefault("name", os.path.basename(item["root"]))
    return targets


def select_targets(targets, wanted):
    """位置参数按名字前缀/包含匹配，不区分大小写。"""
    if not wanted:
        return targets
    picked, missing = [], []
    for key in wanted:
        low = key.lower()
        hit = [t for t in targets if low in t["name"].lower()]
        if not hit:
            missing.append(key)
        for item in hit:
            if item not in picked:
                picked.append(item)
    if missing:
        raise SystemExit("没有匹配到目标：%s\n可用：%s"
                         % (", ".join(missing), ", ".join(t["name"] for t in targets)))
    return picked


def filter_by_module(targets, module):
    """剔掉对该模块声明了 excludeModules 的目标，返回 (保留, 跳过)。

    目标工程不总是"两个工具都要"：比如 project_lilith 只装指令系统不装 ADB
    （用户 2026-10-29 定）。写在 deploy_targets.json 的 excludeModules 里，
    免得每次全量部署又把它装回去。**静默跳过会让人误以为装好了**，所以调用方
    必须把跳过项打出来。
    """
    kept, skipped = [], []
    for item in targets:
        excluded = [str(m).strip().lower() for m in (item.get("excludeModules") or [])]
        (skipped if module.lower() in excluded else kept).append(item)
    return kept, skipped


def unity_running(root):
    """Temp/UnityLockfile 存在 = 编辑器占着工程。"""
    return os.path.isfile(os.path.join(root, "Temp", "UnityLockfile"))


def probe_target(target):
    """校验目标可用，返回 (dest_root, 问题列表)。"""
    root = target["root"]
    problems = []
    if not os.path.isdir(root):
        problems.append("工程目录不存在")
    elif not os.path.isdir(os.path.join(root, "Assets")):
        problems.append("不是 Unity 工程（缺 Assets/）")
    elif not os.path.isdir(os.path.join(root, "ProjectSettings")):
        problems.append("不是 Unity 工程（缺 ProjectSettings/）")
    # 目标路径与源码路径一一对应：两个工具现在都是包形态
    # （Packages/com.aketsuki.unity-mcp 与 Packages/com.aketsuki.adb-debugger）
    dest = os.path.join(root, *SRC_REL.split("/"))
    return dest, problems


# ---------------------------------------------------------------- 差异计算

class Plan(object):
    """一次部署要做的全部动作。"""

    def __init__(self):
        self.create = []        # (rel, src_abs)
        self.update = []        # (rel, src_abs)
        self.same = []          # rel
        self.meta_kept = []     # (rel, 目标 guid, 仓库 guid)
        self.orphan = []        # rel（目标有、仓库无）

    @property
    def dirty(self):
        return bool(self.create or self.update or self.orphan)


def meta_guid(data):
    for line in data.decode("utf-8", "replace").splitlines():
        if line.startswith("guid:"):
            return line.split(":", 1)[1].strip()
    return ""


def build_plan(dest_root):
    plan = Plan()
    # 工程专属目录及其 meta 由 ensure_project_dir 统一处理：
    # 目标是"有该目录就用它的、没有就建"，不能按文件内容直接推。
    # 注意 PROJECT_DIR_REL 可能是多级的（包形态是 Editor/Project），不能只比顶层目录名。
    project_prefix = PROJECT_DIR_REL.strip("/") + "/"
    project_meta = PROJECT_DIR_REL.strip("/") + ".meta"
    sources = [r for r in rel_files(SRC_ROOT)
               if not r.startswith(project_prefix) and r != project_meta]

    src_set = set(sources)
    for rel in sources:
        src_abs = os.path.join(SRC_ROOT, rel.replace("/", os.sep))
        dst_abs = os.path.join(dest_root, rel.replace("/", os.sep))
        if not os.path.isfile(dst_abs):
            plan.create.append((rel, src_abs))
            continue
        src_data = read_bytes(src_abs)
        dst_data = read_bytes(dst_abs)
        if rel.endswith(".meta"):
            # GUID 以目标为准，内容差异不算脏
            if norm(src_data) != norm(dst_data):
                plan.meta_kept.append((rel, meta_guid(dst_data), meta_guid(src_data)))
            else:
                plan.same.append(rel)
            continue
        if norm(src_data) == norm(dst_data):
            plan.same.append(rel)
        else:
            plan.update.append((rel, src_abs))

    if os.path.isdir(dest_root):
        for rel in rel_files(dest_root):
            # 目标侧的工程客制化目录同样排除，否则 --prune 会把它当孤儿删掉
            if rel.startswith(project_prefix) or rel == project_meta:
                continue
            if rel not in src_set:
                plan.orphan.append(rel)
    return plan


# ---------------------------------------------------------------- 执行

def apply_plan(plan, dest_root, root, dry_run=False):
    eol = detect_eol(root)
    eol_name = "CRLF" if eol == b"\r\n" else "LF"
    written = 0

    for rel, src_abs in plan.create + plan.update:
        dst_abs = os.path.join(dest_root, rel.replace("/", os.sep))
        if dry_run:
            continue
        write_atomic(dst_abs, to_eol(read_bytes(src_abs), eol))
        written += 1

    if plan.create or plan.update:
        print("    %s %d 个文件（行尾 %s）%s"
              % ("将写入" if dry_run else "已写入", len(plan.create) + len(plan.update),
                 eol_name, "：新建 %d / 更新 %d" % (len(plan.create), len(plan.update))
                 if plan.create else ""))
        for rel, _ in plan.create:
            print("      + %s" % rel)
        for rel, _ in plan.update:
            print("      M %s" % rel)
    return written


# ---------------------------------------------------------------- MCP 桥（skills 副本）

class SkillPlan(object):
    """`.workbuddy/skills/unity-cmd/` 的差异。"""

    def __init__(self):
        self.create = []        # (rel, src_abs)
        self.update = []        # (rel, src_abs)
        self.same = []          # rel
        self.conflict = []      # (rel, 目标 mtime, 仓库 mtime)

    @property
    def dirty(self):
        return bool(self.create or self.update or self.conflict)


def _ts(stamp):
    return time.strftime("%m-%d %H:%M", time.localtime(stamp))


def build_skill_plan(root, force=False, skill_root=None):
    """目标侧 mtime 比仓库新且内容不同 → 判冲突，不覆盖（可能是人在工程里改的）。

    skill_root 默认等于工程根；CivelRev 这种工作区根比工程根高一层的，
    配置里用 "skill_root" 单独指定（MCP 的 argv 指向工作区根那份）。
    """
    plan = SkillPlan()
    base = os.path.join(skill_root or root, *SKILL_DIR_REL.split("/"))
    for name, rel in SKILL_FILES:
        src_abs = os.path.join(BRIDGE_ROOT, name)
        if not os.path.isfile(src_abs):
            continue
        dst_abs = os.path.join(base, *rel.split("/"))
        if not os.path.isfile(dst_abs):
            plan.create.append((rel, src_abs))
            continue
        if norm(read_bytes(src_abs)) == norm(read_bytes(dst_abs)):
            plan.same.append(rel)
            continue
        if not force and os.path.getmtime(dst_abs) > os.path.getmtime(src_abs):
            plan.conflict.append((rel, os.path.getmtime(dst_abs), os.path.getmtime(src_abs)))
            continue
        plan.update.append((rel, src_abs))
    return plan


def report_skill(plan):
    if not SKILL_FILES:
        # ADB 调试这种"没有桥"的模块：不是 0 个文件同步成功，而是压根没有这件事
        print("    技能目录：无（本模块不带 MCP 桥）")
        return

    head = "    MCP 桥 %s" % SKILL_DIR_REL
    if not plan.dirty:
        print("%s：✓ 已同步（%d 个文件）" % (head, len(plan.same)))
        return
    bits = []
    if plan.create:
        bits.append("新建 %d" % len(plan.create))
    if plan.update:
        bits.append("更新 %d" % len(plan.update))
    if plan.conflict:
        bits.append("冲突 %d" % len(plan.conflict))
    print("%s：%s" % (head, " · ".join(bits)))
    for rel, _ in plan.create:
        print("      + %s" % rel)
    for rel, _ in plan.update:
        print("      M %s" % rel)
    for rel, tgt, src in plan.conflict:
        print("      ! %s 目标那边更晚（%s > %s），不覆盖；--force-skill 才硬写"
              % (rel, _ts(tgt), _ts(src)))


def apply_skill_plan(plan, root, dry_run=False, skill_root=None):
    entries = plan.create + plan.update
    if not entries:
        return 0
    eol = detect_eol(root)
    base = os.path.join(skill_root or root, *SKILL_DIR_REL.split("/"))
    if not dry_run:
        for rel, src_abs in entries:
            write_atomic(os.path.join(base, *rel.split("/")), to_eol(read_bytes(src_abs), eol))
    print("      %s %d 个文件（行尾 %s）%s"
          % ("将写入" if dry_run else "已写入", len(entries),
             "CRLF" if eol == b"\r\n" else "LF",
             "：新建 %d / 更新 %d" % (len(plan.create), len(plan.update)) if plan.create else ""))
    return len(entries)


def install_fresh(plan, dest_root, root, dry_run=False):
    """首次安装：整个目录一次落地。

    直接往 Assets 里逐个建文件，Unity 的文件监视器会在写到一半时就开始扫描并编译，
    拿到的是残缺清单 —— 实测报一屏 `error CS2001: Source file ... could not be found`，
    而且此后编辑器进后台就不再自动重编，得手工按 Ctrl+R 才恢复。
    先写到 Temp/ 下的暂存目录再 rename 进 Assets，对 Unity 就是"目录一次出现"，
    它扫到的一定是完整一套。
    """
    eol = detect_eol(root)
    eol_name = "CRLF" if eol == b"\r\n" else "LF"
    entries = plan.create + plan.update
    print("    %s %d 个文件（行尾 %s，首次安装：整目录落地）"
          % ("将写入" if dry_run else "已写入", len(entries), eol_name))
    if not entries:
        return 0
    if dry_run:
        return len(entries)

    staging = os.path.join(root, "Temp", "UnityMcpDeploy-staging")
    if os.path.isdir(staging):
        shutil.rmtree(staging, ignore_errors=True)
    for rel, src_abs in entries:
        write_atomic(os.path.join(staging, rel.replace("/", os.sep)),
                     to_eol(read_bytes(src_abs), eol))
    parent = os.path.dirname(dest_root)
    if parent and not os.path.isdir(parent):
        os.makedirs(parent)
    if os.path.isdir(dest_root):
        # 理论上不会发生（已判定为首次安装），发生了就退回逐个覆盖
        for rel, _ in entries:
            write_atomic(os.path.join(dest_root, rel.replace("/", os.sep)),
                         to_eol(read_bytes(os.path.join(staging, rel.replace("/", os.sep))), eol))
        shutil.rmtree(staging, ignore_errors=True)
    else:
        os.replace(staging, dest_root)
    return len(entries)



def touch_manifest(root, dest_root):
    """改 `Packages/manifest.json` 的 mtime，强制 Package Manager 重解析。

    embedded package **写进盘不算装好**：不重解析的话 Unity 既不会加载它，也不会为它编出
    dll；更坑的是 `asset.compile` 会回一句"没有检测到新的编译：Unity 认为无需重编"并退出 0，
    看着像成功。判据只有两个：`packages-lock.json` 出现该包且 `source` 为 `embedded`，
    以及 `Library/ScriptAssemblies/<程序集>.dll` 真的产出。
    """
    manifest = os.path.join(root, "Packages", "manifest.json")
    if not os.path.isfile(manifest):
        print("    ! 目标工程没有 Packages/manifest.json，无法强制重解析")
        return False
    os.utime(manifest, None)
    print("    已 touch Packages/manifest.json（强制重解析 %s）" % os.path.basename(dest_root))
    return True


def ensure_project_dir(dest_root, dry_run=False):
    """确保目标工程有自己的工程客制化目录（含占位说明）。"""
    if not HAS_PROJECT_DIR:
        # ADB 调试工具没有工程客制化目录，别给它凭空造一个
        return

    proj = os.path.join(dest_root, *PROJECT_DIR_REL.strip("/").split("/"))
    readme = os.path.join(proj, "README.md")
    created = []
    if not os.path.isdir(proj):
        if not dry_run:
            os.makedirs(proj)
        created.append(PROJECT_DIR_REL + "/")
    if not os.path.isfile(readme):
        src_readme = os.path.join(SRC_ROOT, *(PROJECT_DIR_REL.strip("/") + "/README.md").split("/"))
        if os.path.isfile(src_readme):
            if not dry_run:
                write_atomic(readme, read_bytes(src_readme))
            created.append(PROJECT_DIR_REL + "/README.md")
    # 目录的 .meta 只在缺的时候补，避免改 GUID
    for rel in (PROJECT_DIR_REL.strip("/") + ".meta", PROJECT_DIR_REL.strip("/") + "/README.md.meta"):
        src_abs = os.path.join(SRC_ROOT, rel.replace("/", os.sep))
        dst_abs = os.path.join(dest_root, rel.replace("/", os.sep))
        if os.path.isfile(src_abs) and not os.path.isfile(dst_abs):
            if not dry_run:
                write_atomic(dst_abs, read_bytes(src_abs))
            created.append(rel)
    if created:
        print("    %s工程专属目录：%s" % ("将建立" if dry_run else "已建立", ", ".join(created)))


def prune_orphans(plan, dest_root):
    removed = []
    for rel in plan.orphan:
        path = os.path.join(dest_root, rel.replace("/", os.sep))
        try:
            os.remove(path)
            removed.append(rel)
        except OSError as exc:
            print("    ! 删除失败 %s（%s）" % (rel, exc))
    # 清掉空目录
    for dirpath, dirnames, filenames in os.walk(dest_root, topdown=False):
        if os.path.abspath(dirpath) == os.path.abspath(dest_root):
            continue
        if os.path.basename(dirpath) == PROJECT_DIR_NAME:
            continue
        if not os.listdir(dirpath):
            try:
                os.rmdir(dirpath)
            except OSError:
                pass
    if removed:
        print("    已清理孤儿文件 %d 个：%s" % (len(removed), ", ".join(removed)))
    return removed


def retire_legacy(target, dry_run=False):
    """把旧的资产形态目录整体退役。

    UPM 包形态下，同一批类型已经搬进 Packages/ 下的独立程序集。如果 Assets/ 里还留着旧副本，
    两个程序集会各定义一遍 Aketsuki.UnityMcp.*，Unity 直接报一片 CS0101「命名空间已包含定义」。
    所以切包时旧目录**必须**消失，这不是"顺手清理"。

    用整体 rename 而不是逐个删文件：Unity 的文件监视器会在删除过程中扫盘，读到删了一半的
    清单就会报一屏 CS2001。一次性移走，它看到的是"目录整体消失"。
    移走的目标放在 Temp/ 下 —— Unity 不管那里，也方便出事时捞回来。
    """
    if not LEGACY_REL:
        return []

    root = target["root"]
    legacy = os.path.join(root, *LEGACY_REL.split("/"))
    meta = legacy + ".meta"
    removed = []

    if not os.path.isdir(legacy):
        # 目录没了但 .meta 还在：残留的 meta 会让 Unity 认为该目录仍然存在
        if os.path.isfile(meta):
            print("    %s清掉残留的 %s.meta" % ("将" if dry_run else "已", LEGACY_REL))
            if not dry_run:
                os.remove(meta)
            removed.append(LEGACY_REL + ".meta")
        return removed

    print("    %s退役旧资产形态：%s（整体移出 Assets，避免与新包同名类型重复定义）"
          % ("将" if dry_run else "已", LEGACY_REL))
    removed.append(LEGACY_REL + "/")
    if os.path.isfile(meta):
        removed.append(LEGACY_REL + ".meta")
    if dry_run:
        return removed

    trash = os.path.join(root, "Temp", "UnityMcpLegacyRetired")
    if os.path.isdir(trash):
        shutil.rmtree(trash, ignore_errors=True)
    parent = os.path.dirname(trash)
    if parent and not os.path.isdir(parent):
        os.makedirs(parent)
    os.replace(legacy, trash)
    if os.path.isfile(meta):
        os.remove(meta)
    return removed


def wait_endpoint(send, root, timeout):
    """等目标工程的 HTTP 服务就绪。

    首次安装时文件刚落地，Unity 正在编译，服务要等编译完 + 域重载后才起来；
    这里一直轮询，避免"刚写完就判失败"。
    """
    deadline = time.time() + timeout
    last = ""
    while time.time() < deadline:
        try:
            proc = subprocess.run([sys.executable, send, "status", "--project-root", root],
                                  capture_output=True, text=True, timeout=60)
        except subprocess.TimeoutExpired:
            last = "请求超时"
            time.sleep(5)
            continue
        if proc.returncode == 0:
            return True, ""
        last = ((proc.stderr or proc.stdout or "").strip().splitlines() or [""])[-1]
        time.sleep(5)
    return False, last


def find_offline_compiler():
    for path in OFFLINE_COMPILER_CANDIDATES:
        if os.path.isfile(path):
            return path
    return None


def run_offline_compile(target):
    """用离线编译器验证目标工程的编辑程序集能编过。

    比 HTTP compile 可靠：不依赖编辑器在跑、不吃域重载、2~4 秒出结论。
    代价是只验证"能编译"，不验证导入结果与资源引用。
    """
    if ACTIVE_MODULE == "adb":
        # asmdef 程序集没有 Unity 生成的 csproj 时，通用工具认不出来，走专用脚本
        print("    离线编译校验（%s，asmdef 程序集）……" % ASSEMBLY)
        script = os.path.join(HERE, "compile_adb.py")
        if not os.path.isfile(script):
            print("    ! 找不到 Tools/compile_adb.py，跳过")
            return None
        command = [sys.executable, script, "--project", target["root"]]
    else:
        compiler = find_offline_compiler()
        if not compiler:
            print("    ! 没找到离线编译工具（unity_offline_compile.py），跳过")
            return None
        print("    离线编译校验（%s）……" % ASSEMBLY)
        command = [sys.executable, compiler, "--project", target["root"], ASSEMBLY]

    try:
        proc = subprocess.run(command, capture_output=True, text=True, timeout=600)
    except subprocess.TimeoutExpired:
        print("    ! 离线编译超时")
        return False
    out = (proc.stdout or "") + (proc.stderr or "")
    # 工具的错误行是 "X Assets/.../Foo.cs(12,34): CS0122  ......
    cs_errors = [line.strip() for line in out.splitlines() if re.search(r"\bCS\d{4}\b", line)]
    ours = [line for line in cs_errors if SRC_REL in line.replace("\\", "/")]
    theirs = [line for line in cs_errors if line not in ours]

    if ours:
        for line in ours[:8]:
            print("      " + line[:170])
        print("    ! 部署的文件编译失败：%d 条" % len(ours))
        return False
    if theirs:
        # 目标工程自身的既有问题（如第三方库 InternalsVisibleTo 在离线环境下失效），与本仓库无关
        print("    · 目标工程自身有 %d 条编译错误（不在 ExternalCommandSystem 下，与本次部署无关）："
              % len(theirs))
        for line in theirs[:3]:
            print("      " + line[:150])
        print("    ✓ 部署的代码本身无编译错误")
        return True
    if "编译通过" in out or "全部通过" in out:
        print("    离线编译通过")
        return True
    if not cs_errors:
        tail = out.strip().splitlines()[-3:]
        print("    ! 结果无法判定：%s" % " / ".join(t.strip()[:100] for t in tail))
        return False
    return True


def _find_unity_window(project_name):
    """按工程名找该工程的 Unity 主窗口句柄（只认 UnityContainerWndClass）。"""
    if os.name != "nt":
        return None
    try:
        import ctypes
        from ctypes import wintypes
    except Exception:
        return None
    user32 = ctypes.windll.user32
    hits = []
    proc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def callback(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        cls = ctypes.create_unicode_buffer(64)
        user32.GetClassNameW(hwnd, cls, 64)
        if cls.value != "UnityContainerWndClass":
            return True
        title = ctypes.create_unicode_buffer(512)
        user32.GetWindowTextW(hwnd, title, 512)
        if project_name.lower() in title.value.lower():
            hits.append(hwnd)
        return True

    user32.EnumWindows(proc(callback), 0)
    return hits[0] if hits else None


def force_refresh(target):
    """Windows：把该工程的编辑器窗口提到前台并按 Ctrl+R。

    编辑器的 AssetDatabase 在窗口处于后台时会推迟自动刷新，外部写盘它不认。
    实测：聚焦 + Ctrl+R 之后 Unity 立刻导入并重编译，服务随之起来。
    """
    if os.name != "nt":
        return False
    try:
        import ctypes
        import time as _time
    except Exception:
        return False
    user32 = ctypes.windll.user32
    hwnd = _find_unity_window(os.path.basename(target["root"]))
    if not hwnd:
        return False
    user32.ShowWindow(hwnd, 9)          # SW_RESTORE
    user32.SetForegroundWindow(hwnd)
    _time.sleep(0.8)
    if user32.GetForegroundWindow() != hwnd:
        return False                    # 抢不到前台就别发按键，否则会落到别的窗口
    VK_CONTROL, VK_R, KEYUP = 0x11, 0x52, 0x0002
    user32.keybd_event(VK_CONTROL, 0, 0, 0)
    _time.sleep(0.05)
    user32.keybd_event(VK_R, 0, 0, 0)
    _time.sleep(0.05)
    user32.keybd_event(VK_R, 0, KEYUP, 0)
    user32.keybd_event(VK_CONTROL, 0, KEYUP, 0)
    return True


def trigger_refresh(target, dest_root, force=False):
    """部署完主动让 Unity 导入一次，不用等用户切窗口回来。

    编辑器在后台时 AssetDatabase 会推迟自动刷新（外部写盘它也不一定立刻扫），
    所以服务可达时补一发 asset.refresh；不可达且给了 --force-refresh 就聚焦 + Ctrl+R。
    """
    root = target["root"]
    send = os.path.join(dest_root, *BRIDGE_REL.split("/"), "send.py")
    if not unity_running(root):
        print("    Unity 没开：文件已就位，下次打开工程会自动导入编译")
        return None
    if not os.path.isfile(send):
        if ACTIVE_MODULE == "adb":
            # ADB 工具没有桥脚本，没法代发 asset.refresh；包解析靠上面的 touch manifest
            print("    包已就位；到目标工程里按 Ctrl+R 让它编译（本工具没有 send.py 可代劳）")
        return None
    ready, why = wait_endpoint(send, root, timeout=60)
    if not ready and force:
        if force_refresh(target):
            print("    编辑器在后台没响应，已把它提到前台并按 Ctrl+R，等它导入……")
            ready, why = wait_endpoint(send, root, timeout=240)
        else:
            print("    ! 自动刷新失败（窗口没找到或抢不到前台）")
    if not ready:
        print("    ! Unity 在跑但指令服务没响应，无法主动刷新：%s" % why)
        print("      请切到该工程窗口按 Ctrl+R 触发导入（后台的编辑器会推迟自动刷新）；"
              "加 --force-refresh 可让本脚本代按")
        return False
    body = '{"commands":[{"id":"asset.refresh"}]}'
    try:
        proc = subprocess.run([sys.executable, send, "send", "-", "--project-root", root],
                              input=body, capture_output=True, text=True, timeout=180)
    except subprocess.TimeoutExpired:
        print("    ! 刷新请求超时")
        return False
    if proc.returncode == 0:
        print("    已请求 Unity 刷新资源数据库（未触发重编译的改动即时生效）")
        return True
    tail = ((proc.stdout or "") + (proc.stderr or "")).strip().splitlines()
    print("    ! 刷新请求失败：%s" % (tail[-1] if tail else "未知原因"))
    return False


def verify_package_assembly(target, dest_root, since, timeout=300):
    """包形态、无桥模块（ADB）的编译校验：等 dll 真的产出。

    没有自带 send.py（那是指令系统的桥），所以只能看硬判据：
      ① `Packages/packages-lock.json` 里该包存在且 `"source": "embedded"`；
      ② `Library/ScriptAssemblies/<程序集>.dll` 的 mtime 晚于本次部署开始时间。
    两条都过才算通过。

    **只解析出不等于编出来了**（实测 island_back：lock 已 embedded，dll 还是没影）。
    目标工程如果同时装着指令系统，就借它的 `asset.refresh` 主动推一把 ——
    `asset.compile` 不会导入新文件，只会在旧状态上回一句"无需重编"。
    """
    root = target["root"]
    pkg = os.path.basename(dest_root)
    lock = os.path.join(root, "Packages", "packages-lock.json")
    dll = os.path.join(root, "Library", "ScriptAssemblies", ASSEMBLY + ".dll")
    send = os.path.join(root, "Packages", "com.aketsuki.unity-mcp", "Editor", "McpBridge", "send.py")

    if since is None:
        # 本次没写任何文件（--verify 单独重跑时常见）：只校验现状，不要求"比部署新"
        print("    本次未写文件，只校验现状")
        since = 0

    if not unity_running(root):
        print("    跳过编译校验：Unity 没开（包已就位，下次打开工程会解析并编译）")
        return None

    # 借指令系统的桥推一次导入；没装就等 Unity 自己扫（前台编辑器会扫）
    if os.path.isfile(send):
        try:
            proc = subprocess.run([sys.executable, send, "send", "-", "--project-root", root],
                                  input='{"commands":[{"id":"asset.refresh"}]}',
                                  capture_output=True, text=True, timeout=120)
            if proc.returncode == 0 and "asset.refresh" in (proc.stdout or ""):
                print("    已请求 Unity 刷新资源数据库（导入新包文件）")
            else:
                print("    · 刷新请求没成功，等 Unity 自己扫：%s"
                      % ((proc.stderr or proc.stdout or "").strip().splitlines() or ["?"])[-1][:120])
        except subprocess.TimeoutExpired:
            print("    · 刷新请求超时，等 Unity 自己扫")

    deadline = time.time() + timeout
    while time.time() < deadline:
        if os.path.isfile(dll) and os.path.getmtime(dll) >= since - 1:
            break
        time.sleep(2)

    if not os.path.isfile(lock):
        print("    ! 找不到 Packages/packages-lock.json：Package Manager 还没解析过这个工程")
        return False

    resolved = False
    data = ""
    try:
        data = read_bytes(lock).decode("utf-8", "replace")
        resolved = ('"%s"' % pkg) in data and '"source": "embedded"' in data
    except Exception as error:                      # noqa: BLE001 —— 读失败就判未解析
        print("    ! 读不了 packages-lock.json：%s" % error)

    if not resolved:
        print("    ✗ packages-lock.json 里没有 %s 的 embedded 记录：Package Manager 没解析它"
              % pkg)
        print("      去编辑器里 Window / Package Manager 刷新一次，或按 Ctrl+R")
        return False
    print("    ✓ packages-lock.json 已记为 embedded：%s" % pkg)

    if not os.path.isfile(dll):
        print("    ✗ 没有 %s：包已解析但程序集没编出来（源码有错，或编辑器还在后台）"
              % (ASSEMBLY + ".dll"))
        print("      去编辑器 Console 看编译错误；`asset.compile` 回'无需重编'不算通过")
        return False
    mtime = os.path.getmtime(dll)
    if mtime < since - 1:
        print("    ✗ %s 比本次部署还旧（%s）：Unity 没重编这个程序集"
              % (ASSEMBLY + ".dll", time.strftime("%H:%M:%S", time.localtime(mtime))))
        print("      去编辑器里按 Ctrl+R，或重开工程")
        return False
    print("    ✓ %s 已重新编译（%s）"
          % (ASSEMBLY + ".dll", time.strftime("%H:%M:%S", time.localtime(mtime))))
    return True


def run_verify(target, dest_root, force=False):
    """让目标工程的 Unity 编译一遍并回传错误。编辑器没开就只能跳过。"""
    root = target["root"]
    send = os.path.join(dest_root, *BRIDGE_REL.split("/"), "send.py")
    if not unity_running(root):
        print("    跳过编译校验：Unity 没开（文件已就位，下次打开工程会自动导入并编译）")
        return None
    if not os.path.isfile(send):
        print("    ! 跳过编译校验：目标工程里找不到 McpBridge/send.py")
        return None

    print("    等待目标工程的指令服务就绪（首次安装要等它把代码编译完）……")
    ready, why = wait_endpoint(send, root, timeout=180)
    if not ready and force:
        if force_refresh(target):
            print("    编辑器在后台没响应，已提到前台并按 Ctrl+R，等它导入编译……")
            ready, why = wait_endpoint(send, root, timeout=300)
    if not ready:
        print("    ! 服务没起来，无法验证编译：%s" % why)
        print("      通常是脚本编译有错导致 [InitializeOnLoad] 没执行，请到编辑器 Console 看错误")
        return None

    print("    触发目标工程编译（asset.compile）……")
    cmd = [sys.executable, send, "compile", "--project-root", root, "--wait", "300"]
    try:
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=900)
    except subprocess.TimeoutExpired:
        print("    ! 编译校验超时（>15 分钟）")
        return False
    out = (proc.stdout or "").strip()
    err = (proc.stderr or "").strip()
    if out:
        for line in out.splitlines()[-12:]:
            print("      " + line)
    if proc.returncode != 0:
        if err:
            for line in err.splitlines()[-8:]:
                print("      " + line)
        print("    ! 编译校验失败（退出码 %d）" % proc.returncode)
        return False
    print("    编译校验通过")
    return True


# ---------------------------------------------------------------- 入口

def report_target(target, plan, root_ok, problems):
    version = target.get("unity")
    head = "▶ %s  (%s%s)" % (target["name"], target["root"],
                             ", Unity " + version if version else "")
    print(head)
    if not root_ok:
        for item in problems:
            print("    ✗ %s" % item)
        return
    bits = ["源文件 %d" % (len(plan.same) + len(plan.create) + len(plan.update) + len(plan.meta_kept))]
    if plan.create:
        bits.append("新建 %d" % len(plan.create))
    if plan.update:
        bits.append("更新 %d" % len(plan.update))
    if plan.orphan:
        bits.append("孤儿 %d" % len(plan.orphan))
    bits.append("运行中" if unity_running(target["root"]) else "未运行")
    print("    " + " · ".join(bits))
    if not plan.dirty:
        print("    ✓ 已同步")
        return
    for rel, _ in plan.create:
        print("    + %s" % rel)
    for rel, _ in plan.update:
        print("    M %s" % rel)
    for rel in plan.orphan:
        print("    ? %s（目标多余，--prune 可清理）" % rel)
    if plan.meta_kept:
        print("    · %d 个 .meta 的 GUID 与仓库不同（保留目标侧，正常）" % len(plan.meta_kept))


def main(argv=None):
    _utf8_stdout()
    parser = argparse.ArgumentParser(
        prog="deploy.py",
        description="把指定模块（Unity MCP 指令系统 / ADB 调试）同步部署到配置里的 Unity 工程",
    )
    parser.add_argument("targets", nargs="*", help="目标名字（支持子串匹配）；省略=全部")
    parser.add_argument("--module", choices=sorted(MODULES), default="mcp",
                        help="部署哪个工具：mcp（默认，Packages/com.aketsuki.unity-mcp 这个 UPM 包）"
                             " 或 adb（Packages/com.aketsuki.adb-debugger 这个 UPM 包）")
    parser.add_argument("--list", action="store_true", help="只列出目标与状态")
    parser.add_argument("--check", action="store_true", help="只报告差异，不写文件")
    parser.add_argument("--dry-run", action="store_true", help="显示将写入哪些文件，不动磁盘")
    parser.add_argument("--prune", action="store_true", help="删除目标侧的孤儿文件")
    parser.add_argument("--verify", action="store_true", help="部署后触发目标工程编译校验")
    parser.add_argument("--offline-compile", action="store_true",
                        help="部署后用离线编译器验证编辑程序集（不依赖编辑器是否在跑）")
    parser.add_argument("--force-refresh", action="store_true",
                        help="编辑器不响应时，把它提到前台并按 Ctrl+R 触发导入（Windows）")
    parser.add_argument("--no-project", action="store_true", help="不处理 Project/ 客制化目录")
    parser.add_argument("--no-skill", action="store_true",
                        help="不同步 .workbuddy/skills/unity-cmd/（MCP 桥副本）")
    parser.add_argument("--force-skill", action="store_true",
                        help="MCP 桥副本即使目标侧更晚也强行覆盖")
    args = parser.parse_args(argv)

    apply_module(args.module)
    print("模块：%s（%s）" % (MODULES[ACTIVE_MODULE]["title"], SRC_REL))

    if not os.path.isdir(SRC_ROOT):
        raise SystemExit("源目录不存在：%s" % SRC_ROOT)

    targets = select_targets(load_targets(), args.targets)
    targets, skipped = filter_by_module(targets, args.module)
    for item in skipped:
        print("· 跳过 %s：它声明了不装 %s（%s）"
              % (item["name"], args.module,
                 item.get("excludeNote") or "deploy_targets.json 的 excludeModules"))
    if not targets:
        raise SystemExit("没有可部署的目标：全部被 excludeModules 排除了。"
                         "\n真要装，先把目标在 deploy_targets.json 里的 excludeModules 去掉 —— "
                         "这是刻意设的门，别用 --force 之类绕过（没有那个开关）。")
    check_only = args.check or args.list
    dry_run = args.dry_run or check_only
    failures = 0

    for target in targets:
        dest_root, problems = probe_target(target)
        if problems:
            report_target(target, Plan(), False, problems)
            failures += 1
            continue

        plan = build_plan(dest_root)
        report_target(target, plan, True, problems)

        # 旧资产形态必须先退役，再装新包：顺序反了就会出现"新旧两套同名类型同时在盘上"，
        # Unity 中途扫到就是一片 CS0101。check 模式也报告，方便看出还没切。
        retire_legacy(target, dry_run=dry_run)

        skill_root = target.get("skill_root") or target["root"]
        skill_plan = None if args.no_skill else build_skill_plan(
            target["root"], force=args.force_skill, skill_root=skill_root)
        if skill_plan is not None:
            report_skill(skill_plan)

        if check_only:
            continue

        # 没有工程客制化目录的模块（ADB）不存在"目标侧该有但没建"这回事，
        # 否则每次部署都会误判成需要写入。
        project_ok = (not HAS_PROJECT_DIR) or os.path.isdir(
            os.path.join(dest_root, *PROJECT_DIR_REL.strip("/").split("/")))
        fresh = not os.path.isdir(dest_root)
        deploy_started = time.time()
        wrote = fresh or plan.dirty
        if not plan.dirty and project_ok and not fresh:
            print("    无需写入")
        else:
            if fresh:
                install_fresh(plan, dest_root, target["root"], dry_run=dry_run)
                # 包文件落到 Packages/ 下 ≠ Unity 会加载它：Package Manager 不重解析，
                # 程序集根本不会出现（实测：dll 不产出、/v1/ping 还是旧 service 名）。
                # 改 manifest.json 的 mtime 是唯一可靠的重解析触发器。
                if not dry_run:
                    touch_manifest(target["root"], dest_root)
            elif plan.dirty:
                apply_plan(plan, dest_root, target["root"], dry_run=dry_run)
            if not args.no_project:
                ensure_project_dir(dest_root, dry_run=dry_run)
            if args.prune and plan.orphan and not dry_run:
                prune_orphans(plan, dest_root)

        if not dry_run:
            # 闭环校验：重新算一遍，必须干净
            after = build_plan(dest_root)
            leftover = after.create + after.update
            if leftover:
                print("    ✗ 部署后仍有 %d 个文件不一致：%s"
                      % (len(leftover), ", ".join(rel for rel, _ in leftover[:5])))
                failures += 1
            else:
                print("    ✓ 内容校验通过（%d 个文件与仓库一致）"
                      % (len(after.same) + len(after.meta_kept)))

        if skill_plan is not None:
            apply_skill_plan(skill_plan, target["root"], dry_run=dry_run,
                             skill_root=skill_root)
            if not dry_run:
                after_skill = build_skill_plan(target["root"], force=args.force_skill,
                                               skill_root=skill_root)
                if after_skill.create or after_skill.update:
                    print("    ✗ MCP 桥仍有 %d 个文件没对上：%s"
                          % (len(after_skill.create) + len(after_skill.update),
                             ", ".join(rel for rel, _ in
                                       (after_skill.create + after_skill.update)[:5])))
                    failures += 1
                elif after_skill.conflict:
                    print("    ! MCP 桥 %d 个文件因目标侧更晚未覆盖（见上）"
                          % len(after_skill.conflict))
                elif after_skill.same:
                    print("    ✓ MCP 桥校验通过（%d 个文件与仓库一致）" % len(after_skill.same))

        if args.verify:
            if ACTIVE_MODULE == "adb":
                # 没有 send.py，走不了 HTTP：只能看 packages-lock 与 dll 两条硬判据
                result = verify_package_assembly(target, dest_root,
                                                 deploy_started if wrote else None)
            else:
                result = run_verify(target, dest_root, force=args.force_refresh)
            if result is False:
                failures += 1
        elif not dry_run:
            trigger_refresh(target, dest_root, force=args.force_refresh)

        if args.offline_compile and not dry_run:
            if run_offline_compile(target) is False:
                failures += 1

        print("")

    if args.list or args.check:
        print("（%s 模式：未修改任何文件）" % ("list" if args.list else "check"))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
