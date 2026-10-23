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
import gzip
import io
import os
import sys
import tarfile

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_PKG = os.path.join(HERE, "UnityPackage", "UnityCommandBridge.unitypackage")


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


def main(argv=None):
    parser = argparse.ArgumentParser(description="把 .unitypackage 直接解包到 Unity 工程")
    parser.add_argument("-p", "--package", default=DEFAULT_PKG)
    parser.add_argument("--project", required=True, help="Unity 工程根（含 Assets/ 的那一层）")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)

    package = os.path.abspath(args.package)
    project = os.path.abspath(args.project)
    if not os.path.isfile(package):
        raise SystemExit("包不存在：%s" % package)
    if not os.path.isdir(os.path.join(project, "Assets")):
        raise SystemExit("不是 Unity 工程（缺 Assets/）：%s" % project)

    entries = read_entries(package)
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

    print("包：%s" % package)
    print("目标：%s" % project)
    print("资产 %d 个（目录 %d）→ 新增 %d / 更新 %d / 未变 %d%s"
          % (len(entries), folders, added, updated, same, "（dry-run，未写盘）" if args.dry_run else ""))
    if added or updated:
        print("\n下一步：让编辑器刷新一次（聚焦 Unity 窗口，或走桥发 asset.refresh）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
