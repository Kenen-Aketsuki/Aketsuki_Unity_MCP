# -*- coding: utf-8 -*-
"""装仓库卫生钩子。

做三件事：
  1. 生成 .githooks/private-names.txt：
     上半段从 Tools/deploy_targets.json 的 targets[].name 提取（那份配置本来就不入库，
     正好是"本机有哪些私有工程"的唯一事实来源）；
     下半段是"手工增补"区，install 时原样保留，用来放不在部署目标里的内部命名。
  2. 给 pre-commit 打可执行位（含 git index 里的 mode）
  3. 设 core.hooksPath = .githooks

新 clone 之后跑一次：python Tools/install_hooks.py

注意：本脚本刻意不写任何具体工程名/游戏名 —— 它在仓库里，写了就等于把敏感词
加进了公开历史（2026-11-04 被自己的钩子拦过一次，才改成现在这样）。
"""
import json
import os
import stat
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HOOK_DIR = os.path.join(REPO, ".githooks")
HOOK = os.path.join(HOOK_DIR, "pre-commit")
NAMES = os.path.join(HOOK_DIR, "private-names.txt")
TARGETS = os.path.join(REPO, "Tools", "deploy_targets.json")

MANUAL_MARK = "# --- 手工增补（install 时原样保留）---"


def read_auto_names():
    if not os.path.isfile(TARGETS):
        print("! 没找到 Tools/deploy_targets.json，自动段会是空的")
        return []
    with open(TARGETS, "r", encoding="utf-8") as handle:
        data = json.load(handle)
    names = []
    for item in data.get("targets") or []:
        name = (item.get("name") or "").strip()
        if name:
            names.append(name)
    return names


def read_manual_names():
    """读旧词表里"手工增补"段，重装时不能把它冲掉。"""
    if not os.path.isfile(NAMES):
        return []
    with open(NAMES, "r", encoding="utf-8") as handle:
        text = handle.read()
    if MANUAL_MARK not in text:
        return []
    tail = text.split(MANUAL_MARK, 1)[1]
    names = []
    for line in tail.splitlines():
        line = line.strip()
        if line and not line.startswith("#"):
            names.append(line)
    return names


def write_names(auto_names, manual_names):
    lines = ["# 本机私有词表（不入库）。由 Tools/install_hooks.py 生成。",
             "# 上半段自动生成，下半段手工维护。", ""]
    for name in auto_names:
        lines.append(name)
    lines.append("")
    lines.append(MANUAL_MARK)
    if not manual_names:
        lines.append("# 例：某个不在部署目标里的游戏名 / 内部模块名")
    for name in manual_names:
        lines.append(name)
    lines.append("")
    with open(NAMES, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(lines))
    return len(auto_names) + len(manual_names)


def main():
    if not os.path.isfile(HOOK):
        raise SystemExit("缺少钩子本体：%s" % HOOK)

    auto_names = read_auto_names()
    manual_names = read_manual_names()
    total = write_names(auto_names, manual_names)
    print("词表 %d 条（自动 %d / 手工 %d）-> %s"
          % (total, len(auto_names), len(manual_names), NAMES))

    mode = os.stat(HOOK).st_mode
    os.chmod(HOOK, mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)

    # git 只对已跟踪的文件记录 mode 位；首次入库前 update-index 会报错，跳过即可
    tracked = subprocess.run(
        ["git", "ls-files", "--error-unmatch", ".githooks/pre-commit"],
        cwd=REPO, capture_output=True)
    if tracked.returncode == 0:
        subprocess.run(["git", "update-index", "--chmod=+x", ".githooks/pre-commit"], cwd=REPO)
        print("已记录可执行位（index mode 100755）")
    else:
        print("钩子尚未入库：本地可执行位已设；git add 之后再跑一次本脚本记录 mode 位")

    result = subprocess.run(["git", "config", "core.hooksPath", ".githooks"], cwd=REPO)
    if result.returncode != 0:
        raise SystemExit("设置 core.hooksPath 失败")
    print("已启用：core.hooksPath = .githooks")
    print("")
    print("自检：挑一个含敏感内容的文件 git add 后 git commit，应当被拒绝。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
