#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""用无头 Unity 验证 .unitypackage 到底能不能被导入。

背景：`AssetDatabase.ImportPackage` 在编辑器里**不可靠**——路径写法不对时它会静默
什么都不做，传 `file:///` 甚至能把主线程卡死。命令行 `-importPackage` 走的是
Unity 自己的原生导入器，才是可信的判据。

流程：
  1. `-createProject` 建一个临时空工程
  2. `-importPackage` 导入目标包
  3. 统计 Assets 下的文件数，与包内资产数比对
  4. （可选 `--with-ui`）补上 com.unity.ugui + com.unity.textmeshpro 再编译一次，
     确认没有 error CS——指令系统里的 ui.* / tmpText 指令硬依赖这两个包

用法：
    python Tools/verify_unitypackage.py
    python Tools/verify_unitypackage.py -p D:/tmp/X.unitypackage --with-ui
    python Tools/verify_unitypackage.py --keep      # 保留临时工程以便排查
"""

import argparse
import glob
import gzip
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_PKG = os.path.join(HERE, "UnityPackage", "UnityCommandBridge.unitypackage")

# Unity 没有统一的"最新版"入口，按目录名倒序挑最高版本
UNITY_HUB_DIRS = [
    r"C:\Program Files\Unity\Hub\Editor",
    r"C:\Program Files\Unity\Editor",
]

EXPECTED_MARKERS = [
    "Assets/Editor/ExternalCommandSystem/McpSetup.cs",
    "Assets/Editor/ExternalCommandSystem/CommandHttpServer.cs",
    "Assets/Editor/ExternalCommandSystem/McpBridge/send.py",
    "Assets/Editor/ExternalCommandSystem/McpBridge/unity_mcp_server.py",
    "Assets/Editor/ExternalCommandSystem/McpBridge/SKILL.md",
]

# 工程客制化目录（ECS 下 Project/）：MCP 与工程代码耦合的内容只允许存在本工程，
# 绝不允许随包分发。打包器已强制排除，这里对成品包再做一道独立断言。
FORBIDDEN_PREFIX = "Assets/Editor/ExternalCommandSystem/Project/"


def find_unity():
    candidates = []
    for root in UNITY_HUB_DIRS:
        if not os.path.isdir(root):
            continue
        for name in os.listdir(root):
            exe = os.path.join(root, name, "Editor", "Unity.exe")
            if os.path.isfile(exe):
                candidates.append((name, exe))
    if not candidates:
        raise SystemExit("找不到 Unity 编辑器（已找：%s）" % ", ".join(UNITY_HUB_DIRS))
    candidates.sort(key=lambda item: item[0], reverse=True)
    return candidates[0]


def read_package(path):
    """返回 (资产列表, gzip 头信息)。资产列表元素为 (pathname, is_folder)。"""
    raw = open(path, "rb").read()
    head = raw[:10]
    flags = head[3]
    fname = None
    if flags & 0x08:
        fname = raw[10:raw.index(b"\x00", 10)].decode("utf-8", "replace")

    tar_bytes = gzip.decompress(raw)
    per = {}
    order = []
    with tarfile.open(fileobj=io.BytesIO(tar_bytes)) as tar:
        for member in tar.getmembers():
            parts = member.name.split("/")
            if len(parts) == 1:
                continue
            guid, name = parts[0], parts[1]
            if guid not in per:
                per[guid] = {}
                order.append(guid)
            per[guid][name] = True

    assets = []
    for guid in order:
        files = per[guid]
        pathname = tarfile.open(fileobj=io.BytesIO(tar_bytes)).extractfile(
            guid + "/pathname").read().decode("utf-8")
        assets.append((pathname, "asset" not in files))
    return assets, {"fname": fname, "flags": flags, "tar_size": len(tar_bytes)}


def run(unity, args, log_path):
    with open(log_path, "wb") as log:
        proc = subprocess.run([unity] + args, stdout=log, stderr=subprocess.STDOUT)
    return proc.returncode


def count_files(root):
    total = 0
    for _, _, files in os.walk(root):
        total += len(files)
    return total


def main(argv=None):
    parser = argparse.ArgumentParser(description="用无头 Unity 验证 .unitypackage 可导入性")
    parser.add_argument("-p", "--package", default=DEFAULT_PKG)
    parser.add_argument("--unity", help="Unity.exe 路径，默认自动挑版本最高的")
    parser.add_argument("--with-ui", action="store_true", help="补 UGUI/TMP 后再编译，检查 error CS")
    parser.add_argument("--keep", action="store_true", help="保留临时工程")
    args = parser.parse_args(argv)

    package = os.path.abspath(args.package)
    if not os.path.isfile(package):
        raise SystemExit("包不存在：%s" % package)

    if args.unity:
        unity = args.unity
        name = os.path.basename(os.path.dirname(os.path.dirname(unity)))
    else:
        name, unity = find_unity()
    print("Unity：%s (%s)" % (name, unity))
    print("待验证：%s" % package)

    assets, gz = read_package(package)
    folders = sum(1 for _, is_dir in assets if is_dir)
    print("包内：%d 资产（%d 文件 + %d 目录），tar %d 字节，gzip FNAME=%r"
          % (len(assets), len(assets) - folders, folders, gz["tar_size"], gz["fname"]))

    problems = []
    # Unity 自己导出的包 FNAME 恒为 archtemp.tar；填别的值会被静默拒绝
    if gz["fname"] not in (None, "archtemp.tar"):
        problems.append("gzip FNAME 为 %r，Unity 只接受 'archtemp.tar' 或缺省" % gz["fname"])

    # 工程客制化内容不得随包分发
    leaked = sorted({p for p, _ in assets if p.startswith(FORBIDDEN_PREFIX)})
    if leaked:
        problems.append("包内出现工程客制化内容（%s 下 %d 条）：%s..."
                        % (FORBIDDEN_PREFIX, len(leaked), leaked[0]))

    workdir = tempfile.mkdtemp(prefix="pkgverify_")
    project = os.path.join(workdir, "proj")
    logs = os.path.join(workdir, "logs")
    os.makedirs(logs)
    print("临时工程：%s" % project)

    try:
        code = run(unity, ["-batchmode", "-nographics", "-quit",
                           "-createProject", project,
                           "-logFile", os.path.join(logs, "create.log")], os.path.join(logs, "create.out"))
        if code != 0 or not os.path.isdir(os.path.join(project, "Assets")):
            print("失败：建工程返回 %d" % code)
            return 1

        code = run(unity, ["-batchmode", "-nographics", "-quit",
                           "-projectPath", project,
                           "-importPackage", package,
                           "-logFile", os.path.join(logs, "import.log")], os.path.join(logs, "import.out"))

        assets_dir = os.path.join(project, "Assets")
        found = count_files(assets_dir)
        markers = [m for m in EXPECTED_MARKERS
                   if os.path.isfile(os.path.join(project, m.replace("/", os.sep)))]

        print("\n导入结果：Assets 下 %d 个文件（含 .meta），关键文件命中 %d/%d"
              % (found, len(markers), len(EXPECTED_MARKERS)))

        if found == 0:
            problems.append("导入后 Assets 为空——Unity 读到了包但没导出任何资产")

        missing = [m for m in EXPECTED_MARKERS if m not in markers]
        if missing:
            problems.append("缺少关键文件：%s" % ", ".join(missing))

        ui_errors = None
        if args.with_ui and found:
            manifest_path = os.path.join(project, "Packages", "manifest.json")
            manifest = json.load(open(manifest_path, encoding="utf-8"))
            manifest["dependencies"]["com.unity.ugui"] = "1.0.0"
            manifest["dependencies"]["com.unity.textmeshpro"] = "3.0.6"
            json.dump(manifest, open(manifest_path, "w", encoding="utf-8"), indent=2)

            log_path = os.path.join(logs, "recompile.log")
            run(unity, ["-batchmode", "-nographics", "-quit",
                        "-projectPath", project, "-logFile", log_path],
                os.path.join(logs, "recompile.out"))
            text = open(log_path, "r", encoding="utf-8", errors="replace").read()
            ui_errors = sorted(set(re.findall(r"Assets[\\/][^\s()]+\(\d+,\d+\): error CS\d+: [^\r\n]+", text)))
            print("编译：error CS %d 条" % len(ui_errors))
            for item in ui_errors[:10]:
                print("   " + item)
            if ui_errors:
                problems.append("补齐 UGUI/TMP 后仍有 %d 条编译错误" % len(ui_errors))

        print()
        if problems:
            print("结论：失败")
            for item in problems:
                print("  - " + item)
            if not args.keep:
                print("  临时工程保留在 %s（便于排查）" % workdir)
            return 1

        print("结论：通过——包能被 Unity 正常导入%s"
              % ("，且编译无错误" if args.with_ui else ""))
        return 0
    finally:
        if not args.keep and not problems:
            shutil.rmtree(workdir, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
