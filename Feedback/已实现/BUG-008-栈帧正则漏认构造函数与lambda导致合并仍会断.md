---
id: BUG-008
type: bug
title: 栈帧正则漏认构造函数 / lambda / 本地函数，合并遇到这些帧照样断
status: done
source: Aketsuki_Unity_MCP
created: 2026-10-27
updated: 2026-10-27
files: AdbDebugger/AdbLogcatService.cs
proposal: false
---

## 症状

BUG-007 让设备端 `Type:Method()` 形式的栈帧能被认出来，但**方法名只允许字母开头**，
于是 Unity 真机上另外三种常见写法依旧认不出：

```
11-27 21:39:38.820 2745 2895 I Unity   : ItemPanel:Refresh()
11-27 21:39:38.820 2745 2895 I Unity   : ItemView:.ctor()
11-27 21:39:38.820 2745 2895 I Unity   : BagView:<Init>b__2_0()
11-27 21:39:38.820 2745 2895 I Unity   : BagView:<Refresh>g__Fill|7_1()
11-27 21:39:38.820 2745 2895 I Unity   : System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1:MoveNext()
```

期望：一条日志，展开可见调用栈。
实际：`.ctor()` / `<Init>b__2_0()` / `<Refresh>g__Fill|7_1()` 三行各启一条新日志，
一条 `Debug.Log` 在控制台里又变成四条——和 BUG-007 修之前一模一样。

**根因**：栈里只要有一帧认不出来，合并就断在这一行；不是"少认了几个好看的名字"，
是**修复目标没达成**。构造函数在带 UI 的项目里几乎必然出现在栈上。

## 复现

```bash
python <临时>/re_test.py    # 32 条用例：19 正例 / 13 反例
```

把上面的栈喂给旧正则（`...\s*:\s*[A-Za-z_][A-Za-z0-9_<>]*\s*\(`），三种写法全部不匹配。

## 怎么修的

放宽了 `UnityStackFrame` 的**方法名**段，类型段不动：

```
^[A-Za-z_][A-Za-z0-9_.`<>+]*\s*:\s*(?:\.c?ctor|[A-Za-z_<][A-Za-z0-9_<>`.|]*)\s*\([^()]*\)$
```

- 方法名允许 `<` 开头 → `<Init>b__2_0()`；允许 `|` → `<Refresh>g__Fill|7_1()`
- `.ctor` / `.cctor` 用**精确列举**的分支放行，而不是"方法名允许点开头"。
  一旦允许任意点开头，`Error: .5 (units)` 这种正文就会被吞进上一条——
  放开 unsafe 换的是精准度，不划算
- 类型段本来就收 `` ` `` 与 `+`，async 状态机那一长串 `…AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1:MoveNext()`
  本来就认得，这次补进用例固定住

刻意不收的一种：`Foo:<Bar>d__5:MoveNext()` 这种**双冒号**写法。要认它就得允许类型段出现冒号，
等于宣告「任何 `X: y: z()` 都是栈帧」——代价远大于收益。而且它不是 Unity 的打印形式：
IL2CPP 里状态机是嵌套类型，写作 `Outer+<Method>d__N:MoveNext()`（这一条在正例里）。

## 执行结果（2026-10-27）

| 验证项 | 结果 |
| --- | --- |
| 正则用例 32 条（19 正例 / 13 反例） | ✅ 全过，旧正则下漏的 5 种写法现在都认 |
| `Tools/compile_adb.py --project .` | ✅ 编译通过（CivilRev.AdbDebugger.Editor） |
| 部署 project_island_back / majotavern / CivelRev | ✅ 三处都装了这个模块，各更新 2 文件、20 文件校验一致、离线编译通过 |
| 真机复验 | ⏳ 等用户下次抓到带构造函数帧的日志确认（控制台应只剩 1 条） |

反例里保留了 `Note: see foo()`、`Time: 12:30 (x)`、`Error: .5 (units)`、
`Task Guide True-3-Controller (MainIsLandController)-Normal` 这些真实正文形状，
确认放宽之后没有被误吞。
