# -*- coding: utf-8 -*-
"""send.py 编译等待逻辑的离线回归测试（BUG-022 / IMPR-027）。

不起 Unity，也不起 HTTP：直接用假 Client 驱动 cmd_compile 的轮询时间线，
断言"编译开始得晚"时不会被误判成"Unity 认为无需重编"。

    python Tools/test_send_compile.py            # 跑测试
    python Tools/test_send_compile.py --old      # 顺带证明修复前的版本会误判

假 Client 的时间线（秒，自 asset.compile 下发起算）：

    compile_start=None   永不编译（模拟"Unity 认为无需重编"）
    compile_start=1.2    编译在请求返回 1.2 秒后才开始（真实情况：异步返回）
    compile_start=0.1    编译几乎立刻开始（对照）
"""

import importlib.util
import io
import json
import os
import sys
import tempfile
import time
import types

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
SEND_PY = os.path.join(REPO, "Packages", "com.aketsuki.unity-mcp", "Editor", "McpBridge", "send.py")

COMPILE_DURATION = 0.6


def load_module(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


class FakeClient(object):
    """模拟命令服务的两条接口，按时间线决定"现在是不是在编译"。"""

    def __init__(self, compile_start, duration=COMPILE_DURATION, prior_finished=True):
        self.compile_start = compile_start
        self.duration = duration
        self.prior_finished = prior_finished
        self.t0 = None
        self.status_calls = 0
        self.report_calls = 0
        self.report_include_messages = []

    # -- 时间线 --------------------------------------------------------------
    def _compiling(self):
        if self.t0 is None or self.compile_start is None:
            return False
        elapsed = time.time() - self.t0
        return self.compile_start <= elapsed < self.compile_start + self.duration

    def _finished(self):
        if self.t0 is None or self.compile_start is None:
            return False
        return time.time() - self.t0 >= self.compile_start + self.duration

    def _finished_utc(self):
        if not self.prior_finished and not self._finished():
            return ""
        return "2026-11-11T15:00:00.0000000Z" if self._finished() else "2026-11-11T14:00:00.0000000Z"

    # -- 接口 ----------------------------------------------------------------
    def call(self, path, method="GET", body=None):
        if path == "v1/status":
            self.status_calls += 1
            return 200, {"compiling": self._compiling(), "updating": False, "busy": self._compiling()}, ""

        if path == "v1/execute":
            request = json.loads(body)
            command = request["commands"][0]
            if command["id"] == "asset.compile":
                self.t0 = time.time()
                return 200, {"ok": True, "records": [{"status": "Success", "data": {}}]}, ""
            if command["id"] == "asset.compileReport":
                params = command.get("params") or {}
                self.report_calls += 1
                self.report_include_messages.append(params.get("includeMessages"))
                return 200, {"ok": True, "records": [{
                    "status": "Success",
                    "data": {
                        "finished": bool(self.prior_finished) or self._finished(),
                        "ok": True,
                        "errorCount": 0,
                        "warningCount": 3,
                        "durationMs": self.duration * 1000.0,
                        "finishedUtc": self._finished_utc(),
                        "requestedUtc": "",
                        "rejectedReason": "",
                    },
                }]}, ""

        return 404, {}, "unexpected path " + path


def run_compile(module, client, wait=20):
    args = types.SimpleNamespace(wait=wait, clean=False, json=True, project_root=None)
    out, err = io.StringIO(), io.StringIO()
    real_out, real_err = sys.stdout, sys.stderr
    sys.stdout, sys.stderr = out, err
    try:
        code = module.cmd_compile(client, args)
    finally:
        sys.stdout, sys.stderr = real_out, real_err
    return code, out.getvalue(), err.getvalue()


def scenario(module, label, compile_start, expect):
    client = FakeClient(compile_start)
    started = time.time()
    code, out, _ = run_compile(module, client)
    elapsed = time.time() - started

    print("  %-22s exit=%d  用时 %4.1fs  报告读取 %d 次" % (label, code, elapsed, client.report_calls))
    print("      输出：%s" % out.strip().replace("\n", "\n            "))

    problems = []
    if expect["exit"] is not None and code != expect["exit"]:
        problems.append("退出码应为 %d，实际 %d" % (expect["exit"], code))
    if expect.get("contains") and expect["contains"] not in out:
        problems.append("输出里应该出现 %r" % expect["contains"])
    if expect.get("not_contains") and expect["not_contains"] in out:
        problems.append("输出里不该出现 %r" % expect["not_contains"])
    if expect.get("max_seconds") and elapsed > expect["max_seconds"]:
        problems.append("耗时 %.1fs 超过上限 %.1fs" % (elapsed, expect["max_seconds"]))
    if expect.get("min_seconds") and elapsed < expect["min_seconds"]:
        problems.append("耗时 %.1fs 低于下限 %.1fs（没等宽限期就收工了）" % (elapsed, expect["min_seconds"]))
    return problems


def check_warnings(module):
    """IMPR-027：人类可读输出要打印顶层 warnings。"""
    payload = {
        "source": "http",
        "total": 1, "succeeded": 1, "failed": 0, "skipped": 0, "elapsedMs": 3,
        "ok": True,
        "summary": "1 条指令全部成功；另有 1 条警告（见 warnings）",
        "warnings": [{"index": 0, "id": "scene.hierarchy", "warning": "未知参数：root, depth（该指令支持 target, maxDepth）"}],
        "records": [{"index": 0, "id": "scene.hierarchy", "status": "Success", "message": "ok"}],
    }
    out = io.StringIO()
    real_out = sys.stdout
    sys.stdout = out
    try:
        module.print_report(payload)
    finally:
        sys.stdout = real_out
    text = out.getvalue()
    print(text.strip())
    problems = []
    if "未知参数" not in text:
        problems.append("print_report 没有输出 warnings 内容")
    if "scene.hierarchy" not in text:
        problems.append("print_report 没打印警告所属的指令 id")
    return problems


def main(argv):
    module = load_module(SEND_PY, "send_under_test")
    failures = []

    print("[1] 编译晚于两次空闲轮询才开始（BUG-022 的核心场景）")
    failures += scenario(module, "start=1.2s 成功编译", 1.2, {
        "exit": 0, "contains": "编译成功", "not_contains": "没有检测到新的编译"})

    print("[2] 编译快到两次轮询之间就跑完（靠 finishedUtc 判据）")
    failures += scenario(module, "start=0.1s 秒级完成", 0.1, {
        "exit": 0, "contains": "编译成功", "not_contains": "没有检测到新的编译",
        "max_seconds": 3.0})

    print("[3] 真的没触发编译：等够宽限期才收工，并提示 --clean 而不是 --force")
    failures += scenario(module, "start=None 从未编译", None, {
        "exit": 0, "contains": "--clean", "not_contains": "--force",
        "min_seconds": 5.0, "max_seconds": 15.0})

    print("[4] 探时间戳的调用不再索取消息列表（includeMessages=False）")
    client = FakeClient(1.2)
    run_compile(module, client)
    if client.report_include_messages and client.report_include_messages[0] is not False:
        failures.append("before 探测没有传 includeMessages=False（实际 %r）"
                        % (client.report_include_messages[0],))
    else:
        print("  ok：首次探测 includeMessages=%r" % (client.report_include_messages[:1],))

    print("[5] 人类可读输出打印顶层 warnings（IMPR-027）")
    failures += check_warnings(module)

    if "--old" in argv:
        print("\n[对照] 修复前的版本（HEAD 里的 send.py）")
        blob = os.popen('git -C "%s" show HEAD:Packages/com.aketsuki.unity-mcp/Editor/McpBridge/send.py'
                        % REPO).read()
        if not blob.strip():
            print("  拿不到 HEAD 版本，跳过")
        else:
            handle = tempfile.NamedTemporaryFile("w", suffix=".py", delete=False, encoding="utf-8")
            handle.write(blob)
            handle.close()
            old = load_module(handle.name, "send_before_fix")
            os.unlink(handle.name)
            # 老版本还没有这几个常量，常量名差异由 scenario 自行忽略
            scenario(old, "start=1.2s 成功编译", 1.2, {"exit": 0})

    print()
    if failures:
        print("失败 %d 项：" % len(failures))
        for item in failures:
            print("  - " + item)
        return 1
    print("全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
