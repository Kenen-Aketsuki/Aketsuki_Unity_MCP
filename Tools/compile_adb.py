#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""编译校验 ADB 调试工具的 asmdef 程序集（Aketsuki.AdbDebugger.Editor）。

    python Tools/compile_adb.py --project D:/proj/SomeGame

为什么需要单独的脚本
--------------------
`unity_offline_compile.py` 是从**Unity 自己生成的 csproj** 里取源文件与引用的。
ADB 工具带 asmdef，Unity 只在编辑器里点过刷新之后才会为它生成 csproj；CI / 没开编辑器的
机器上直接调那个工具会得到"没有这个程序集"。

本脚本换一条路：先把目标工程的 `Assembly-CSharp-Editor` 离线编一次（借这一步拿到该工程
**正确的** DefineConstants 与引用列表，Unity 版本各工程不同，不能硬编码），再用同一套
引用重新拼一个只含 AdbDebugger 源文件的工程交给 dotnet。

输出格式与那个工具保持一致（含 `error CSxxxx` 或 `编译通过`），deploy.py 直接就能解析。
"""

import argparse
import glob
import io
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)

ASSEMBLY = "Aketsuki.AdbDebugger.Editor"
# 2026-10-29 起是 UPM 包形态（embedded package），不再是 Assets 下的资产目录
SRC_REL = "Packages/com.aketsuki.adb-debugger/Editor"

OFFLINE_COMPILER_CANDIDATES = [
    os.path.expanduser("~/.workbuddy/tools/unity-offline-compile/unity_offline_compile.py"),
    "C:/Users/poi77/.workbuddy/tools/unity-offline-compile/unity_offline_compile.py",
]


def find_offline_compiler():
    for path in OFFLINE_COMPILER_CANDIDATES:
        if os.path.isfile(path):
            return path
    return None


def run(cmd, timeout=600):
    try:
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
        return proc.returncode, (proc.stdout or "") + (proc.stderr or "")
    except subprocess.TimeoutExpired:
        return -1, "超时（%d 秒）" % timeout


def reference_project(project_root):
    """跑一次主程序集的离线编译，返回它生成的 csproj 路径"""
    compiler = find_offline_compiler()
    if not compiler:
        return None, "没找到 unity_offline_compile.py"

    code, out = run([sys.executable, compiler, "--project", project_root])
    name = os.path.basename(project_root.rstrip("\\/"))
    pattern = os.path.expanduser(
        "~/.workbuddy/tools/unity-offline-compile/work/%s/proj/offline_Assembly-CSharp-Editor.csproj" % name)
    hits = glob.glob(pattern)
    if not hits:
        return None, "没能生成参考 csproj（主程序集编译输出：%s）" % out.strip().splitlines()[-1:] 
    return hits[0], None


def build_adb_project(project_root, reference_csproj):
    text = io.open(reference_csproj, encoding="utf-8").read()

    prop = text[text.index("<PropertyGroup>"):text.index("</PropertyGroup>") + len("</PropertyGroup>")]
    prop = re.sub(r"<AssemblyName>[^<]*</AssemblyName>",
                  "<AssemblyName>Offline_Aketsuki_AdbDebugger_Editor</AssemblyName>", prop)

    # 只保留 Unity / 第三方引用：指向工程内 ScriptAssemblies 的都不要，本程序集不依赖它们
    refs = [item for item in re.findall(r"    <Reference Include=.*?</Reference>", text, re.S)
            if "ScriptAssemblies" not in item]

    sources = sorted(glob.glob(os.path.join(project_root, *SRC_REL.split("/"), "*.cs")))
    if not sources:
        return None, "工程里没有 %s 的源码" % SRC_REL

    compile_items = "\n".join(
        '    <Compile Include="%s" />' % os.path.abspath(path).replace("\\", "/") for path in sources)

    content = ('<Project Sdk="Microsoft.NET.Sdk">\n  %s\n  <ItemGroup>\n%s\n  </ItemGroup>\n'
               '  <ItemGroup>\n%s\n  </ItemGroup>\n</Project>\n'
               % (prop, compile_items, "\n".join(refs)))

    name = os.path.basename(project_root.rstrip("\\/"))
    target_dir = os.path.expanduser("~/.workbuddy/tools/unity-offline-compile/work/%s/proj" % name)
    os.makedirs(target_dir, exist_ok=True)
    csproj = os.path.join(target_dir, "offline_Aketsuki.AdbDebugger.Editor.csproj")
    io.open(csproj, "w", encoding="utf-8", newline="\n").write(content)
    return csproj, None


def main():
    parser = argparse.ArgumentParser(description="编译校验 ADB 调试工具的 asmdef 程序集")
    parser.add_argument("--project", required=True, help="Unity 工程根")
    parser.add_argument("--quiet", action="store_true", help="只输出最终结论")
    args = parser.parse_args()

    project_root = os.path.abspath(args.project)

    reference, error = reference_project(project_root)
    if error:
        print("! " + error)
        return 2

    csproj, error = build_adb_project(project_root, reference)
    if error:
        print("! " + error)
        return 2

    code, out = run(["dotnet", "build", csproj, "-v", "q", "--nologo"])

    errors = [line for line in out.splitlines() if re.search(r"\berror CS\d{4}\b", line)]
    if errors:
        for line in errors[:20]:
            print(line.strip())
        print("! 编译失败：%d 条" % len(errors))
        return 1

    if code != 0:
        tail = [line for line in out.strip().splitlines() if line.strip()][-6:]
        for line in tail:
            print(line[:200])
        print("! dotnet build 失败（exit=%d），但不是 CS 错误——检查引用或源码路径" % code)
        return 2

    if not args.quiet:
        print("编译通过（%s）" % ASSEMBLY)
    return 0


if __name__ == "__main__":
    sys.exit(main())
