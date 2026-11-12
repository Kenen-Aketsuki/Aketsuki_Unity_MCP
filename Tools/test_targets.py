# -*- coding: utf-8 -*-
"""目标选择逻辑的离线测试（不依赖 Unity 是否只开了一个）。

真机上很难同时开三个 Unity，但"多工程并存"恰恰是最需要写对的分支——
选错的代价是把指令发进别人的工程。所以这里用注入的假 Bridge 覆盖它。

    python Tools/test_targets.py
"""
import atexit
import os
import shutil
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "Packages", "com.aketsuki.unity-mcp",
                                "Editor", "McpBridge"))

import send  # noqa: E402
import unity_mcp_server as mcp  # noqa: E402

FAILED = []

# 测试用的"工程根"现场造，不依赖本机任何真实工程 —— clone 下来就能跑。
# parse_selector 判 root 的判据是 `<目录>/ProjectSettings/ProjectVersion.txt` 存在，
# 所以这里把这两个文件真建出来；整个临时目录在退出时删掉。
_TEST_ROOT = tempfile.mkdtemp(prefix="mcp_targets_")
atexit.register(shutil.rmtree, _TEST_ROOT, True)


def _make_fake_project(name):
    root = os.path.join(_TEST_ROOT, name)
    os.makedirs(os.path.join(root, "ProjectSettings"), exist_ok=True)
    with open(os.path.join(root, "ProjectSettings", "ProjectVersion.txt"),
              "w", encoding="utf-8") as handle:
        handle.write("m_EditorVersion: 0.0.0\n")
    return root


PROJECT_A = _make_fake_project("project_a")
PROJECT_B = _make_fake_project("project_b")
# 存在、但不是 Unity 工程根（缺 ProjectSettings/ProjectVersion.txt）
NOT_A_PROJECT = os.path.join(_TEST_ROOT, "not_a_project")
os.makedirs(NOT_A_PROJECT, exist_ok=True)
# 根本不存在
MISSING_PATH = os.path.join(_TEST_ROOT, "no_such_project")


def check(name, condition, detail=""):
    mark = "PASS" if condition else "FAIL"
    print("  [%s] %s%s" % (mark, name, ("  → " + detail) if detail and not condition else ""))
    if not condition:
        FAILED.append(name)


class FakeBridge(object):
    def __init__(self, label, alive=True):
        self.label = label
        self.alive = alive

    def target(self):
        if not self.alive:
            raise mcp.BridgeError("不可达")
        return object()

    def status(self):
        return {"ok": True, "port": 1}


def new_pool(live_paths, alive=True):
    """造一个池子，把"全局索引里的活目标"直接注入成假数据。"""
    pool = mcp.TargetPool()
    fakes = [(path, FakeBridge(os.path.basename(path), alive)) for path in live_paths]
    pool._live = fakes
    pool._live_at = time.time()
    pool._get_root = lambda root: dict(
        (os.path.normcase(os.path.normpath(p)), b) for p, b in fakes
    ).get(os.path.normcase(os.path.normpath(root))) or FakeBridge(root, alive)
    return pool


def test_parse_selector():
    print("parse_selector：")
    # 信息文件也现场造：判 info 的依据是"这个文件存在且以 .http-info 开头"，
    # 不能靠本机残留的那份（别人的 clone 里没有，测试会假失败）。
    info = os.path.join(_TEST_ROOT, send.INFO_PREFIX + ".deadbeef")
    with open(info, "w", encoding="utf-8") as handle:
        handle.write("{}\n")
    kind, value = mcp.parse_selector(os.path.abspath(info))
    check("绝对路径 .http-info → info", kind == "info", "%s %s" % (kind, value))

    kind, value = mcp.parse_selector(PROJECT_A)
    check("工程根目录 → root", kind == "root", "%s %s" % (kind, value))

    kind, value = mcp.parse_selector("project_a")
    check("裸名字 → name", kind == "name" and value == "project_a")

    kind, value = mcp.parse_selector("  \"project_b\"  ")
    check("去掉引号与空白", kind == "name" and value == "project_b", repr(value))

    check("空串 → (None, None)", mcp.parse_selector("") == (None, None))

    # 存在但不是 Unity 工程根（没有 ProjectSettings/ProjectVersion.txt）
    kind, value = mcp.parse_selector(NOT_A_PROJECT)
    check("非工程目录 → invalid", kind == "invalid", "%s %s" % (kind, value))

    kind, value = mcp.parse_selector(MISSING_PATH)
    check("不存在的路径 → invalid（不退化成名）", kind == "invalid", "%s %s" % (kind, value))


def test_single_target():
    print("唯一活目标：")
    pool = new_pool([PROJECT_A])
    bridge = pool.get(None)
    check("省略 target 自动选中唯一工程",
          bridge.label == os.path.basename(PROJECT_A), bridge.label)


def test_ambiguous():
    print("多工程并存：")
    pool = new_pool([PROJECT_A, PROJECT_B])
    try:
        pool.get(None)
        check("省略 target 应当报错", False, "竟然没报错")
    except mcp.SelectorError as error:
        text = str(error)
        check("提示同时开着几个", "2 个" in text, text)
        check("列出全部候选路径", PROJECT_A in text and PROJECT_B in text, text)

    bridge = pool.get(PROJECT_B)
    check("显式工程根命中 B", bridge.label == os.path.basename(PROJECT_B), bridge.label)


def test_sticky():
    print("粘性选择：")
    pool = mcp.TargetPool()
    calls = []

    def fake_dispatch(kind, value):
        calls.append((kind, value))
        return FakeBridge("G")

    pool._dispatch = fake_dispatch
    pool.get(PROJECT_A)         # 显式选一次
    pool.get(None)              # 之后省略
    want = ("root", os.path.normpath(PROJECT_A))
    got = calls[-1]
    check("显式选过之后省略会沿用",
          (got[0], os.path.normpath(got[1])) == want, str(calls))

    # 沿用目标已经死掉时要自动放弃粘性，而不是一直报错
    pool2 = mcp.TargetPool()
    pool2._dispatch = lambda kind, value: FakeBridge("D", alive=False)
    pool2._sticky = ("root", os.path.join(_TEST_ROOT, "D"))
    pool2._live = [(os.path.join(_TEST_ROOT, "E"), FakeBridge("E"))]
    pool2._live_at = time.time()
    try:
        bridge = pool2.get(None)
        check("粘性目标死掉后回落到活实例", bridge.label == "E", bridge.label)
    except mcp.SelectorError as error:
        check("粘性目标死掉后回落到活实例", False, str(error))


def test_name_resolution():
    print("按名字解析：")
    original = send.find_project_root_by_name
    try:
        send.find_project_root_by_name = lambda name: (PROJECT_A, [PROJECT_A], [])
        pool = new_pool([])
        pool.get("project_a")
        sticky = pool.sticky_value()
        check("唯一命中 → 解析成工程根并记下",
              sticky[0] == "root" and os.path.normpath(sticky[1]) == os.path.normpath(PROJECT_A),
              str(sticky))

        send.find_project_root_by_name = lambda name: (None, [PROJECT_A, PROJECT_B], [])
        pool = new_pool([])
        try:
            pool.get("project")
            check("重名应当报错", False, "竟然没报错")
        except mcp.SelectorError as error:
            check("重名报错并列出候选",
                  "多个" in str(error) and PROJECT_B in str(error), str(error))

        # 2026-11-11 拍板：只对上一部分的子串一律拒绝，并点名"是不是要选它"
        send.find_project_root_by_name = lambda name: (None, [], [PROJECT_A])
        pool = new_pool([])
        try:
            pool.get("proj")
            check("子串不自动选中", False, "竟然替人挑了一个")
        except mcp.SelectorError as error:
            check("子串报错并点名候选",
                  "全名" in str(error) and "project_a" in str(error), str(error))

        send.find_project_root_by_name = lambda name: (None, [], [])
        pool = new_pool([])
        try:
            pool.get("NotExist")
            check("查不到应当报错", False, "竟然没报错")
        except mcp.SelectorError as error:
            check("查不到给出 unity_targets 提示", "unity_targets" in str(error), str(error))
    finally:
        send.find_project_root_by_name = original


def test_cli_target_selector():
    """CLI 的 --target：三种写法都要能落到同一个工程根上。

    这条通路是把全局索引暴露给 `send.py` 用的——在 A 工程目录里跑脚本时，
    它默认只看 A 的工作区，B 工程的编辑器会被报成"没开着"（假阴性）。
    """
    print("CLI --target 选址：")
    original = send.find_project_root_by_name
    original_targets = send.list_global_targets
    try:
        send.find_project_root_by_name = lambda name: (PROJECT_A, [PROJECT_A], [])
        root, info, err, _ = send.resolve_target_selector("project_a")
        check("按工程名 → 工程根", err is None and os.path.normpath(root) == os.path.normpath(PROJECT_A), err or root)

        root, info, err, hits = send.resolve_target_selector(PROJECT_A)
        check("工程根路径 → 原样返回",
              err is None and os.path.normpath(root) == os.path.normpath(PROJECT_A), err or root)

        send.find_project_root_by_name = lambda name: (None, [PROJECT_A, PROJECT_B], [])
        root, info, err, hits = send.resolve_target_selector("clone")
        check("重名 → 报错并列候选",
              root is None and err and PROJECT_A in err and PROJECT_B in err, err or "")

        send.find_project_root_by_name = lambda name: (None, [], [PROJECT_A])
        root, info, err, hits = send.resolve_target_selector("proj")
        check("子串 → 拒绝并点名完整工程名",
              root is None and err and "全名" in err and "project_a" in err, err or "")

        send.find_project_root_by_name = lambda name: (None, [], [])
        send.list_global_targets = lambda: [
            {"_path": "x", "identity": {"projectName": PROJECT_A, "projectPath": PROJECT_A}}]
        root, info, err, hits = send.resolve_target_selector("NotExist")
        check("查不到 → 列出全局索引里的条目",
              root is None and "全局索引" in err and "project_a" in err, err or "")

        root, info, err, _ = send.resolve_target_selector(MISSING_PATH)
        check("像路径但不存在 → 明确报错", err is not None and "不存在" in err, err or "")

        root, info, err, _ = send.resolve_target_selector("")
        check("空值 → 报错", err is not None, err or "")
    finally:
        send.find_project_root_by_name = original
        send.list_global_targets = original_targets


def test_tool_modes():
    print("工具折叠：")
    import json

    commands = []
    for command_id, description in [
        ("gameobject.create", "创建"), ("gameobject.find", "查找"),
        ("prefab.tree", "预制体树"), ("prefab.delete", "删预制体"),
        ("log", "写日志"),
    ]:
        commands.append({"id": command_id, "description": description, "usage": "a: string"})

    domains = mcp.build_domains(commands)
    check(">=2 条的域单独成工具", set(domains) == {"gameobject", "prefab", "misc"}, str(list(domains)))
    check("单条指令并入 misc", [a for a, _, _ in domains["misc"]] == ["log"])

    tool = mcp.domain_to_tool("gameobject", domains["gameobject"])
    actions = tool["inputSchema"]["properties"]["action"]["enum"]
    check("action enum 列全", actions == ["create", "find"], str(actions))
    check("域工具带 target 参数", "target" in tool["inputSchema"]["properties"])

    size_meta = len(json.dumps({"tools": mcp.META_TOOLS}, ensure_ascii=False))
    check("meta 模式体积 < 4k 字符", size_meta < 4000, "%d" % size_meta)


def test_catalog():
    """本地指令清单：读得到、按工程精确挑、不串到别的工程。"""
    print("本地指令清单：")
    import json as _json
    import shutil
    import tempfile

    directory = tempfile.mkdtemp(prefix="catalog_")
    original_dir = send.COMMAND_DIR
    original_project = send.EXPECTED_PROJECT

    def write_catalog(project_path, commands, stamp, mtime):
        payload = {
            "ok": True,
            "count": len(commands),
            "generatedUtc": stamp,
            "protocolVersion": 2,
            "identity": {"projectPath": project_path,
                         "projectName": os.path.basename(project_path)},
            "commands": commands,
        }
        path = os.path.join(directory,
                            "commands.%d.json" % (abs(hash(project_path)) % 100000))
        with open(path, "w", encoding="utf-8") as handle:
            _json.dump(payload, handle)
        # mtime 决定"不指定工程时取哪份"，显式给出来，免得两次写盘落在同一秒
        os.utime(path, (mtime, mtime))
        return path

    try:
        send.COMMAND_DIR = directory
        send.EXPECTED_PROJECT = None

        write_catalog(PROJECT_A, [{"id": "ui.node"}, {"id": "prefab.renameNode"}],
                      "2026-11-04T00:00:00Z", 1001)
        write_catalog(PROJECT_B, [{"id": "adb.logcat.read"}],
                      "2026-11-04T01:00:00Z", 1002)

        data, path, error = send.pick_catalog(_Args())
        check("不指定工程 → 取最新那份",
              data is not None and data["count"] == 1 and not error,
              "%s / %s" % (error, path))

        send.EXPECTED_PROJECT = os.path.normcase(os.path.abspath(PROJECT_A))
        data, path, error = send.pick_catalog(_Args())
        check("指定工程根 → 精确挑到它的清单",
              data is not None and [c["id"] for c in data["commands"]] == ["ui.node", "prefab.renameNode"],
              "%s / %s" % (error, path))

        send.EXPECTED_PROJECT = os.path.normcase(os.path.abspath(NOT_A_PROJECT))
        data, path, error = send.pick_catalog(_Args())
        check("指定工程没有清单 → 报错而不串用别人的",
              data is None and error and "都不属于" in error, str(error))

        send.EXPECTED_PROJECT = None
        shutil.rmtree(directory, ignore_errors=True)
        data, path, error = send.pick_catalog(_Args())
        check("没有清单 → 提示先联网取一次",
              data is None and error and "没有清单" in error, str(error))
    finally:
        send.COMMAND_DIR = original_dir
        send.EXPECTED_PROJECT = original_project
        shutil.rmtree(directory, ignore_errors=True)


class _Args(object):
    """catalog 只用到这两个字段。"""

    json = False
    prefix = None


def test_name_match_real():
    """`find_project_root_by_name` 的真实匹配规则（不 stub 它，只换掉索引内容）。

    背景：`shop` 与 `shop_back` 这种前缀关系很常见（真实工程名多半长这样）。
    2026-11-11 拍板：**必须全名**——子串一律不自动选中，只在报错里点名候选。
    目录已经不存在的条目（关掉 / 挪走的工程，索引条目不会自动消失）必须被滤掉，
    否则会稳定命中一个幽灵目录。
    """
    print("按名字匹配（真实索引表）：")
    original_targets = send.list_global_targets
    original_notices = list(send.TARGET_NOTICES)
    project_a_back = _make_fake_project("project_a_back")
    ghost = os.path.join(_TEST_ROOT, "ghost_project")
    try:
        send.list_global_targets = lambda: [
            {"_path": "a", "identity": {"projectName": "project_a", "projectPath": PROJECT_A}},
            {"_path": "b", "identity": {"projectName": "project_a_back", "projectPath": project_a_back}},
            {"_path": "c", "identity": {"projectName": "project_a", "projectPath": ghost}},
        ]

        root, hits, near = send.find_project_root_by_name("project_a")
        check("全名精确命中不会被前缀更长的同名工程抢走",
              root is not None and os.path.normpath(root) == os.path.normpath(PROJECT_A), str(hits))
        check("精确命中时 near 也照实给出", len(near) == 1, str(near))

        root, hits, near = send.find_project_root_by_name("project_a_ba")
        check("子串唯一命中也不再自动选中（必须全名）",
              root is None and not hits and len(near) == 1, "root=%s near=%s" % (root, near))

        # 目录不存在的条目要被滤掉：否则 ghost 会与 PROJECT_A 一起命中 "project_a"
        root, hits, near = send.find_project_root_by_name("ghost_project")
        check("目录已不存在的条目被滤掉", root is None and not hits and not near,
              "hits=%s near=%s" % (hits, near))

        # 子串写法：拒绝，但要把"你要写的那个"直接点出来（含写法示例）
        del send.TARGET_NOTICES[:]
        root, info, err, _ = send.resolve_target_selector("project_a_ba")
        check("子串 → 拒绝并给出完整工程名与示例",
              root is None and err and "全名" in err and "project_a_back" in err, err or "")

        del send.TARGET_NOTICES[:]
        root, info, err, _ = send.resolve_target_selector("project_a")
        check("全名 → 正常选中且不产生提示",
              err is None and not send.TARGET_NOTICES, err or str(send.TARGET_NOTICES))
    finally:
        send.list_global_targets = original_targets
        del send.TARGET_NOTICES[:]
        send.TARGET_NOTICES.extend(original_notices)
        shutil.rmtree(project_a_back, ignore_errors=True)


def main():
    print("== 目标选择与工具折叠测试 ==")
    test_parse_selector()
    test_single_target()
    test_ambiguous()
    test_sticky()
    test_name_resolution()
    test_cli_target_selector()
    test_name_match_real()
    test_tool_modes()
    test_catalog()

    print("")
    if FAILED:
        print("失败 %d 项：%s" % (len(FAILED), "、".join(FAILED)))
        return 1
    print("全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
