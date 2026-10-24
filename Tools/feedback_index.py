#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""扫描 Feedback/ 生成索引，并校验反馈表本身的格式。

为什么要有它：反馈是靠"移动文件"改状态的，人一多（或者自己隔两周回来）必然出现
status 和目录不一致、编号重复、必填字段漏写。这些都让脚本报出来，不靠自觉。

用法：
    python Tools/feedback_index.py            # 重写 Feedback/索引.md 并校验
    python Tools/feedback_index.py --check    # 只校验，不写文件（可挂 CI/提交前）
"""

import argparse
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
FEEDBACK = os.path.join(os.path.dirname(HERE), "Feedback")

# 目录 -> 该目录允许的 status。状态与目录不一致 = 有人改了 front-matter 没移文件（或反过来）
DIR_STATUS = {
    "待拍板": ("pending",),
    "未实现": ("open", "wontfix"),
    "已实现": ("done",),
}

# 决策批件：一次列出多条待拍的文档，不带 front-matter、不参与编号校验（用户 2026-10-24 定）
BATCH_PREFIXES = ("待拍板-", "已拍板-")

REQUIRED = ("id", "type", "title", "status", "source", "created")

TYPES = {
    "bug": "BUG",
    "feature": "FEAT",
    "improve": "IMPR",
    "doc": "DOC",
}

STATUS_LABEL = {
    "pending": "待拍板",
    "open": "待处理",
    "wontfix": "不做",
    "done": "已实现",
}

# 忽略：说明文件、模板、生成物
SKIP_NAMES = ("README.md", "索引.md")
SKIP_PREFIX = "_"


def parse_front_matter(path):
    """极简 front-matter 解析：只认 `key: value`，不值当为此引 yaml。"""
    with open(path, "r", encoding="utf-8") as handle:
        text = handle.read()

    meta = {}
    body = text
    if text.startswith("---"):
        end = text.find("\n---", 3)
        if end < 0:
            return None, text, "front-matter 没有闭合的 ---"
        block = text[3:end]
        body = text[end + 4:]
        for line in block.splitlines():
            line = line.strip()
            if not line or ":" not in line:
                continue
            key, value = line.split(":", 1)
            value = value.split("    #")[0].strip()  # 去掉行尾注释
            if "  #" in value:
                value = value.split("  #")[0].strip()
            meta[key.strip()] = value.strip().strip('"').strip("'")

    return meta, body, None


def collect():
    entries = []
    batches = []
    problems = []

    for folder, allowed in DIR_STATUS.items():
        full = os.path.join(FEEDBACK, folder)
        if not os.path.isdir(full):
            problems.append("缺少目录：Feedback/%s" % folder)
            continue

        for name in sorted(os.listdir(full)):
            if not name.lower().endswith(".md"):
                continue
            if name in SKIP_NAMES or name.startswith(SKIP_PREFIX):
                continue

            rel = "Feedback/%s/%s" % (folder, name)
            if name.startswith(BATCH_PREFIXES):
                # 决策批件：一段时间的拍板清单，正文由用户直接批注，故不校验 front-matter
                batches.append({"folder": folder, "name": name, "rel": rel})
                continue

            path = os.path.join(full, name)
            meta, _body, error = parse_front_matter(path)
            if error:
                problems.append("%s：%s" % (rel, error))
                continue

            missing = [key for key in REQUIRED if not meta.get(key)]
            if missing:
                problems.append("%s：缺必填字段 %s" % (rel, ", ".join(missing)))

            status = meta.get("status", "")
            if status and status not in allowed:
                problems.append(
                    "%s：status=%s 不该放在「%s」（该目录只收 %s）"
                    % (rel, status, folder, "/".join(allowed)))

            entry_type = meta.get("type", "")
            if entry_type and entry_type not in TYPES:
                problems.append("%s：type=%s 不认识（可选：%s）"
                                % (rel, entry_type, "/".join(sorted(TYPES))))

            entry_id = meta.get("id", "")
            prefix = TYPES.get(entry_type)
            if entry_id and prefix and not entry_id.upper().startswith(prefix + "-"):
                problems.append("%s：编号 %s 与 type=%s 不匹配（应为 %s-xxx）"
                                % (rel, entry_id, entry_type, prefix))
            if entry_id and not re.match(r"^[A-Z]+-\d{3}$", entry_id.upper()):
                problems.append("%s：编号格式应为 XXX-001，实际 %s" % (rel, entry_id))
            if entry_id and entry_id not in rel:
                problems.append("%s：文件名里没带编号 %s（约定：%s-简短标题.md）"
                                % (rel, entry_id, entry_id))

            entries.append({
                "id": entry_id,
                "type": entry_type,
                "title": meta.get("title", ""),
                "status": status,
                "source": meta.get("source", ""),
                "created": meta.get("created", ""),
                "updated": meta.get("updated", ""),
                "proposal": meta.get("proposal", "").lower() in ("true", "yes", "1"),
                "folder": folder,
                "rel": rel,
            })

    # 编号唯一（跨目录）
    seen = {}
    for entry in entries:
        key = entry["id"]
        if key in seen:
            problems.append("编号重复：%s（%s 与 %s）" % (key, seen[key], entry["rel"]))
        else:
            seen[key] = entry["rel"]

    return entries, batches, problems


def next_ids(entries):
    """每种前缀下一个可用编号"""
    used = {prefix: set() for prefix in TYPES.values()}
    for entry in entries:
        match = re.match(r"^([A-Z]+)-(\d{3})$", entry["id"].upper())
        if match and match.group(1) in used:
            used[match.group(1)].add(int(match.group(2)))

    result = {}
    for prefix, numbers in used.items():
        n = 1
        while n in numbers:
            n += 1
        result[prefix] = "%s-%03d" % (prefix, n)
    return result


def render(entries, batches, problems, ids):
    def count(statuses):
        return sum(1 for e in entries if e["status"] in statuses)

    pending_count = count(("pending",))
    open_count = count(("open", "wontfix"))
    done_count = count(("done",))
    proposal_count = sum(1 for e in entries if e["proposal"])

    lines = [
        "# 反馈索引",
        "",
        "> 本文件由 `python Tools/feedback_index.py` 生成，**手改会被覆盖**。",
        "> 规则见 [README.md](README.md)，新建一条从 [_模板.md](_模板.md) 复制。",
        "",
        "待拍板 **%d** 条 · 未实现 **%d** 条 · 已实现 **%d** 条 · 合计 %d 条"
        % (pending_count, open_count, done_count, len(entries)),
        "",
        "下一个可用编号：%s" % " · ".join("%s" % v for v in sorted(ids.values())),
        "",
    ]

    if proposal_count:
        lines += ["其中标 `proposal: true` 的「建议扩展」共 **%d** 条" % proposal_count, ""]
        if proposal_count >= 3:
            lines += ["> ⚠ 建议扩展积到 %d 条了。按 IMPR-005 的拍板口径，这类提议偏多时"
                      "应该停下来找替代方案，而不是继续往下堆。" % proposal_count, ""]

    if problems:
        lines += ["## ⚠ 校验未通过", ""]
        lines += ["- " + item for item in problems]
        lines += [""]

    titles = {
        "待拍板": "## 待拍板（%d）\n\n> 等你拍板。**直接在文件里写决策**，AI 执行完把文件移到 `已实现/`。",
        "未实现": "## 未实现（%d）",
        "已实现": "## 已实现（%d）",
    }

    for folder in ("待拍板", "未实现", "已实现"):
        group = [e for e in entries if e["folder"] == folder]
        lines += [(titles[folder] % len(group)), ""]
        group_batches = [b for b in batches if b["folder"] == folder]

        if not group:
            lines += ["_（无单条反馈）_", ""]
        else:
            lines += ["| 编号 | 类型 | 标题 | 来源工程 | 状态 | 更新 |",
                      "|---|---|---|---|---|---|"]
            for entry in sorted(group, key=lambda e: (e["type"], e["id"])):
                title = entry["title"].replace("|", "\\|")
                if entry["proposal"]:
                    title += " *(建议扩展)*"
                lines.append("| [%s](%s) | %s | %s | %s | %s | %s |" % (
                    entry["id"],
                    entry["rel"].replace("Feedback/", ""),
                    entry["type"],
                    title,
                    entry["source"],
                    STATUS_LABEL.get(entry["status"], entry["status"]),
                    entry["updated"] or entry["created"],
                ))
            lines += [""]

        if group_batches:
            lines += ["决策批件（批量清单，不参与编号）：", ""]
            for batch in group_batches:
                lines.append("- [%s](%s)" % (batch["name"], batch["rel"].replace("Feedback/", "")))
            lines += [""]

    return "\n".join(lines)


def main(argv=None):
    parser = argparse.ArgumentParser(description="生成反馈索引并校验格式")
    parser.add_argument("--check", action="store_true", help="只校验，不写文件")
    args = parser.parse_args(argv)

    if not os.path.isdir(FEEDBACK):
        raise SystemExit("找不到 Feedback 目录：%s" % FEEDBACK)

    entries, batches, problems = collect()
    ids = next_ids(entries)

    if not args.check:
        os.makedirs(FEEDBACK, exist_ok=True)
        with open(os.path.join(FEEDBACK, "索引.md"), "w", encoding="utf-8", newline="\n") as handle:
            handle.write(render(entries, batches, problems, ids))

    def count(statuses):
        return sum(1 for e in entries if e["status"] in statuses)

    print("反馈 %d 条（待拍板 %d / 未实现 %d / 已实现 %d），决策批件 %d 份"
          % (len(entries), count(("pending",)), count(("open", "wontfix")),
             count(("done",)), len(batches)))
    print("下一个编号：%s" % "  ".join(sorted(ids.values())))
    if not args.check:
        print("已更新：%s" % os.path.join(FEEDBACK, "索引.md"))

    if problems:
        print("\n校验未通过（%d 项）：" % len(problems))
        for item in problems:
            print("  - " + item)
        return 1

    print("校验通过。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
