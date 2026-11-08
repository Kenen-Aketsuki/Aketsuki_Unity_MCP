#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""栈帧判定回归测试（IMPR-017）。

干什么
------
``Packages/com.aketsuki.adb-debugger/Editor/AdbLogcatService.cs`` 里那条 ``UnityStackFrame``
正则决定了"设备端一条 Debug.Log 在控制台里占一行，还是被拆成正文 + N 条栈帧"。
它有两个特点让它特别容易改坏：

* **放宽一点点就吞正文**（把普通日志并进上一条的栈里）；
* **收紧一点点就拆日志**（栈里有一帧认不出，合并就断在这一行）。

两种情况**编译都通过**，只会在真机抓日志时悄悄发作，而且发作了很难归因。
所以把判定的正例 / 反例固化在这里：动那条正则之前先跑一遍。

    python Tools/test_stack_frame.py

退出码 0 = 全过，1 = 有用例失败，2 = 脚本没能读到 C# 里的正则（比如文件被挪走）。

怎么同步
--------
下面的 ``PATTERN`` 是 C# 里那条正则的**副本**。脚本启动时会去读 C# 源文件、
把里面的字符串抠出来跟副本比对，不一致直接报错退出——不会让你在副本过时的情况下
拿到"全绿"的假象。改 C# 正则时两边一起改。

Python 的 ``re`` 与 .NET 的正则在这一条上语义一致（用到的都是最基础的字符类、
量词与 ``^``/``$`` 锚点，没有用到两边行为不同的构造）。

注意：**本文件刻意不进 Unity 包**（IMPR-017 用户批注）。它只是仓库级的回归网，
不需要出现在工程里。
"""

from __future__ import print_function

import io
import os
import re
import sys

# C# 侧 UnityStackFrame 的副本。改 C# 时同步改这里（脚本会校验，见 main）。
PATTERN = (
    r"^[A-Za-z_][A-Za-z0-9_.`<>+]*\s*:\s*"
    r"(?:\.c?ctor|[A-Za-z_<][A-Za-z0-9_<>`.|]*)\s*\([^()]*\)$"
)

# C# 源文件的候选路径（按顺序找第一个存在的）。
#
# ADB 调试台从"工程内资产"搬成了独立包（`com.aketsuki.adb-debugger`），老路径
# `Assets/Editor/AdbDebugger/…` 在 2026-11 之后就不存在了。**只留老路径的后果不是报错，
# 而是"文件找不到 → 打印一行警告 → 跳过副本一致性校验 → 依旧全绿"**：
# 正则改了、副本没改，测试照样通过（2026-11-07 实际发现，这是一条假绿）。
CS_CANDIDATE_PATHS = (
    os.path.join("Packages", "com.aketsuki.adb-debugger", "Editor", "AdbLogcatService.cs"),
    os.path.join("Assets", "Editor", "AdbDebugger", "AdbLogcatService.cs"),  # 老资产形态，仅兼容
)

# ---------------------------------------------------------------- 用例

# 正例：设备端 Unity player 打印的 managed 栈帧，必须被认出来（否则一条日志被拆成多条）。
POSITIVE = [
    "Foo:Bar()",
    "A:B()",
    "_Foo:_bar()",
    "UnityEngine.Debug:Log()",
    "UnityEngine.Debug:Log(Object)",
    "UnityEngine.DebugLogHandler:LogFormat(LogType, Object, String, Object[])",
    "Game.Main:Start()",
    "My.Namespace.Type:Method(System.String)",
    "Foo:.ctor()",                     # 构造函数
    "Foo:.cctor()",                    # 静态构造
    "Foo:<Start>b__0_0()",             # lambda
    "Foo:<Bar>g__Local|5_0()",         # 本地函数
    "Foo`1:get_Item()",                # 泛型类型
    "Coroutine:MoveNext()",            # 迭代器 / async 状态机
    "Type:Method(Int32, String)",
    "Foo:Bar ( )",                     # 括号前有空白
    "Foo :Bar()",                      # 冒号前后有空白
    "A.b.C:D()",
    "Foo:Bar(System.Collections.Generic.List`1[System.Int32])",
]

# 反例：长得像但**不是**栈帧的正文，绝不能被吞掉（否则正文被并进上一条日志）。
NEGATIVE = [
    "Error: .5 (units)",               # 放开"方法名允许点开头"就会被吞（BUG-008 的关键反例）
    "Note: see foo()",                 # 方法名里有空格
    "Time: 12:30 (x)",                 # 方法名以数字开头
    "at Foo:Bar()",                    # 带 at 前缀，不是设备端写法
    "Caused by: Foo:Bar()",
    "Foo:Bar() extra",                 # 括号后面还有内容
    "Foo Bar()",                       # 没有冒号
    "Foo:Bar(",                        # 括号没闭合
    "1Foo:Bar()",                      # 类型名以数字开头
    "Foo:9Bar()",                      # 方法名以数字开头
    "Hello world",
    "  Foo:Bar()",                     # 行首缩进（只 TrimEnd，不 TrimStart）
    "W/Unity   ( 1234): message",
    "System.Exception: something",     # 没有括号
    "Foo:.ctor",                       # 没有括号
]


def find_pattern_in_source(text):
    """从 C# 源码里抠出 UnityStackFrame 的正则字符串（逐字串拼接的形式）。"""
    marker = "UnityStackFrame = new Regex("
    at = text.find(marker)
    if at < 0:
        return None

    # 收集紧随其后的 @"..." 字面量，直到遇到 RegexOptions
    rest = text[at + len(marker):]
    end = rest.find("RegexOptions")
    body = rest[:end] if end >= 0 else rest

    parts = re.findall(r'@"((?:[^"]|"")*)"', body)
    if not parts:
        return None

    # C# 逐字串里 "" 表示一个引号
    return "".join(part.replace('""', '"') for part in parts)


def locate_source(repo_root):
    """按候选路径找 C# 源文件，返回 (绝对路径, 相对描述) 或 (None, None)。"""
    for relative in CS_CANDIDATE_PATHS:
        candidate = os.path.join(repo_root, relative)
        if os.path.isfile(candidate):
            return candidate, relative
    return None, None


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.dirname(here)
    cs_path, cs_relative = locate_source(repo_root)

    if cs_path is None:
        # 这里**必须**失败退出（docstring 承诺的退出码 2）。
        # 曾经的写法是"打印警告、跳过校验、继续跑用例"——那等于把这条回归网悄悄拆了：
        # 源文件挪走之后，正则改了副本没改，测试依旧全绿。
        print("[失败] 找不到 AdbLogcatService.cs，副本一致性校验没法做。找过这些位置：")
        for relative in CS_CANDIDATE_PATHS:
            print("  %s" % relative)
        print("  路径变了就更新本脚本的 CS_CANDIDATE_PATHS——不要让它静默跳过校验。")
        return 2

    with io.open(cs_path, "r", encoding="utf-8-sig") as handle:
        source = handle.read()
    actual = find_pattern_in_source(source)
    if actual is None:
        print("[失败] 在 %s 里没找到 UnityStackFrame 的正则，脚本需要更新" % cs_relative)
        return 2
    if actual != PATTERN:
        print("[失败] C# 里的正则与本脚本的副本不一致，两边要同步改：")
        print("  C#  : %s" % actual)
        print("  副本: %s" % PATTERN)
        return 2
    print("正则副本与 %s 一致" % cs_relative)

    regex = re.compile(PATTERN)
    failed = []

    for case in POSITIVE:
        if not regex.match(case.rstrip()):
            failed.append(("应为栈帧却没认出", case))

    for case in NEGATIVE:
        if regex.match(case.rstrip()):
            failed.append(("不应判为栈帧却认了", case))

    total = len(POSITIVE) + len(NEGATIVE)
    if failed:
        print("\n[失败] %d/%d 条用例不通过：" % (len(failed), total))
        for reason, case in failed:
            print("  %s：%s" % (reason, case))
        return 1

    print("[通过] %d 条用例全过（正例 %d / 反例 %d）"
          % (total, len(POSITIVE), len(NEGATIVE)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
