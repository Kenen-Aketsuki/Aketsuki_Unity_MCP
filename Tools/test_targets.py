# -*- coding: utf-8 -*-
"""目标选择逻辑的离线测试（不依赖 Unity 是否只开了一个）。

真机上很难同时开三个 Unity，但"多工程并存"恰恰是最需要写对的分支——
选错的代价是把指令发进别人的工程。所以这里用注入的假 Bridge 覆盖它。

    python Tools/test_targets.py
"""
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "Packages", "com.aketsuki.unity-mcp",
                                "Editor", "McpBridge"))

import send  # noqa: E402
import unity_mcp_server as mcp  # noqa: E402

FAILED = []

# 用本机真实存在的两个 Unity 工程根做测试路径：parse_selector 会去查
# ProjectSettings/ProjectVersion.txt，凭空编的假路径会被判成 invalid。
PROJECT_A = "D:/Work/MyUnityProject"
PROJECT_B = "D:/Work/ProjectB"


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
    info = os.path.join(HERE, "..", ".workbuddy", "UnityEditCommand", ".http-info.ad39ac9d")
    kind, value = mcp.parse_selector(os.path.abspath(info))
    check("绝对路径 .http-info → info", kind == "info", "%s %s" % (kind, value))

    kind, value = mcp.parse_selector(PROJECT_A)
    check("工程根目录 → root", kind == "root", "%s %s" % (kind, value))

    kind, value = mcp.parse_selector("MyUnityProject")
    check("裸名字 → name", kind == "name" and value == "MyUnityProject")

    kind, value = mcp.parse_selector("  \"ProjectB\"  ")
    check("去掉引号与空白", kind == "name" and value == "ProjectB", repr(value))

    check("空串 → (None, None)", mcp.parse_selector("") == (None, None))

    # 存在但不是 Unity 工程根（没有 ProjectSettings/ProjectVersion.txt）
    kind, value = mcp.parse_selector("D:/Work")
    check("非工程目录 → invalid", kind == "invalid", "%s %s" % (kind, value))

    kind, value = mcp.parse_selector("D:/no/such/project")
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
    pool2._sticky = ("root", "D:/p/D")
    pool2._live = [("D:/p/E", FakeBridge("E"))]
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
        send.find_project_root_by_name = lambda name: (PROJECT_A, [])
        pool = new_pool([])
        pool.get("MyUnityProject")
        sticky = pool.sticky_value()
        check("唯一命中 → 解析成工程根并记下",
              sticky[0] == "root" and os.path.normpath(sticky[1]) == os.path.normpath(PROJECT_A),
              str(sticky))

        send.find_project_root_by_name = lambda name: (None, [PROJECT_A, PROJECT_B])
        pool = new_pool([])
        try:
            pool.get("project")
            check("重名应当报错", False, "竟然没报错")
        except mcp.SelectorError as error:
            check("重名报错并列出候选",
                  "多个" in str(error) and PROJECT_B in str(error), str(error))

        send.find_project_root_by_name = lambda name: (None, [])
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
        send.find_project_root_by_name = lambda name: (PROJECT_A, [])
        root, info, err, _ = send.resolve_target_selector("MyUnityProject")
        check("按工程名 → 工程根", err is None and os.path.normpath(root) == os.path.normpath(PROJECT_A), err or root)

        root, info, err, hits = send.resolve_target_selector("D:/Work/MyUnityProject")
        check("工程根路径 → 原样返回",
              err is None and os.path.normpath(root) == os.path.normpath(PROJECT_A), err or root)

        send.find_project_root_by_name = lambda name: (None, [PROJECT_A, PROJECT_B])
        root, info, err, hits = send.resolve_target_selector("clone")
        check("重名 → 报错并列候选",
              root is None and err and PROJECT_A in err and PROJECT_B in err, err or "")

        send.find_project_root_by_name = lambda name: (None, [])
        send.list_global_targets = lambda: [
            {"_path": "x", "identity": {"projectName": PROJECT_A, "projectPath": PROJECT_A}}]
        root, info, err, hits = send.resolve_target_selector("NotExist")
        check("查不到 → 列出全局索引里的条目",
              root is None and "全局索引" in err and "MyUnityProject" in err, err or "")

        root, info, err, _ = send.resolve_target_selector("D:/no/such/project")
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


def main():
    print("== 目标选择与工具折叠测试 ==")
    test_parse_selector()
    test_single_target()
    test_ambiguous()
    test_sticky()
    test_name_resolution()
    test_cli_target_selector()
    test_tool_modes()

    print("")
    if FAILED:
        print("失败 %d 项：%s" % (len(FAILED), "、".join(FAILED)))
        return 1
    print("全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
