using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Animations;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 动画资产（<c>.controller</c>）的公共工具：路径归一化、加载、层/状态解析、参数与条件解析。
    /// <para>
    /// 为什么要专门做一套指令（FEAT-019）：<c>.controller</c> 内部靠一堆 <c>fileID</c> 互相引用
    /// （StateMachine ↔ State ↔ Transition ↔ Condition），还要和 <c>m_DefaultState</c> /
    /// <c>m_AnyStateTransitions</c> / 每个 State 的 <c>m_Transitions</c> 四方对齐。
    /// 手写 YAML 错一个 fileID 不会报错，表现只是"controller 打开是空的"或"过渡永远不触发"。
    /// 走 <see cref="AnimatorController"/> 的编辑器 API，交叉引用由 Unity 自己维护。
    /// </para>
    /// <para>
    /// 范围：只做<b>状态机装配</b>（FEAT-019 缺口 A）。<see cref="AnimationClip"/> 的曲线写入（缺口 B）
    /// 刻意没做——<c>AnimationUtility.SetEditorCurve</c> 的 binding 三元组写错同样是静默失效，
    /// 需要单独的验证面，等真正有需求再上。
    /// </para>
    /// </summary>
    internal static class AnimatorAssetUtil
    {
        /// <summary>状态机里表示"任何状态 / 入口 / 出口"的保留字（比较时忽略大小写、空格与下划线）</summary>
        internal const string AnyState = "anystate";
        internal const string EntryState = "entry";
        internal const string ExitState = "exit";

        /// <summary>
        /// 归一化 <c>.controller</c> 资产路径：相对写法落到 <c>Assets/</c>，缺后缀补 <c>.controller</c>，
        /// 结果仍要过 <see cref="CommandPathUtil"/> 白名单（防止借本指令写到工程外）。
        /// </summary>
        internal static string NormalizeControllerPath(CommandContext context, string raw, string paramName)
        {
            var text = (raw ?? string.Empty).Trim().Replace('\\', '/').Trim('"').Trim();
            if (text.Length == 0)
            {
                throw new CommandArgumentException(context.CommandId, paramName, "路径不能为空");
            }

            if (!text.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                text = "Assets/" + text.TrimStart('/');
            }

            if (!text.EndsWith(".controller", StringComparison.OrdinalIgnoreCase))
            {
                text += ".controller";
            }

            try
            {
                CommandPathUtil.EnsureAccessible(Path.GetFullPath(text));
            }
            catch (Exception ex)
            {
                throw new CommandArgumentException(context.CommandId, paramName, ex.Message);
            }

            return text;
        }

        /// <summary>按路径加载 controller；不存在时报"找不到"而不是拿到空引用。</summary>
        internal static AnimatorController LoadController(CommandContext context, out string assetPath,
            string paramName = "controller")
        {
            assetPath = NormalizeControllerPath(context, context.Args.RequireString(paramName), paramName);
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath);
            if (controller == null)
            {
                throw new CommandArgumentException(context.CommandId, paramName,
                    $"找不到 AnimatorController：{assetPath}");
            }

            return controller;
        }

        /// <summary>
        /// 解析 <c>layer</c> 参数（整数下标或层名，省略取第 0 层）。
        /// 层不存在时列出全部现有层——动画类报错最怕"给了个名字然后什么都没发生"。
        /// </summary>
        internal static int ResolveLayerIndex(CommandContext context, AnimatorController controller, string key = "layer")
        {
            var layers = controller.layers;
            if (layers == null || layers.Length == 0)
            {
                throw new CommandException($"controller '{controller.name}' 没有任何层（层数为 0，无法定位状态机）");
            }

            if (!context.Args.Has(key))
            {
                return 0;
            }

            var raw = context.Args.Get(key);
            if (raw != null && raw.IsNumber)
            {
                var index = raw.AsInt(0);
                if (index < 0 || index >= layers.Length)
                {
                    throw new CommandArgumentException(context.CommandId, key,
                        $"层下标 {index} 越界（共 {layers.Length} 层，0-based）");
                }

                return index;
            }

            var name = raw != null ? raw.AsString(string.Empty) : string.Empty;
            if (string.IsNullOrEmpty(name))
            {
                return 0;
            }

            for (var i = 0; i < layers.Length; i++)
            {
                if (string.Equals(layers[i].name, name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            var available = new List<string>();
            for (var i = 0; i < layers.Length; i++)
            {
                available.Add(i + ":" + layers[i].name);
            }

            throw new CommandArgumentException(context.CommandId, key,
                $"找不到层 '{name}'。现有层：{string.Join("、", available)}");
        }

        /// <summary>按名字找一层里的顶层状态；找不到返回 null。</summary>
        internal static AnimatorState FindState(AnimatorControllerLayer layer, string name)
        {
            var stateMachine = layer.stateMachine;
            if (stateMachine == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            var states = stateMachine.states;
            for (var i = 0; i < states.Length; i++)
            {
                var state = states[i].state;
                if (state != null && string.Equals(state.name, name, StringComparison.Ordinal))
                {
                    return state;
                }
            }

            return null;
        }

        /// <summary>列出顶层状态名（报错时给人看）。</summary>
        internal static string DescribeStateNames(AnimatorControllerLayer layer)
        {
            var stateMachine = layer.stateMachine;
            if (stateMachine == null)
            {
                return "（该层没有状态机）";
            }

            var names = new List<string>();
            var states = stateMachine.states;
            for (var i = 0; i < states.Length; i++)
            {
                var state = states[i].state;
                if (state != null)
                {
                    names.Add(state.name);
                }
            }

            return names.Count == 0 ? "（该层还没有状态）" : string.Join("、", names);
        }

        /// <summary>取状态；找不到时报错并列出该层现有状态名。</summary>
        internal static AnimatorState RequireState(CommandContext context, AnimatorControllerLayer layer,
            string name, string paramName)
        {
            var state = FindState(layer, name);
            if (state == null)
            {
                throw new CommandArgumentException(context.CommandId, paramName,
                    $"层 '{layer.name}' 里没有状态 '{name}'。现有状态：{DescribeStateNames(layer)}");
            }

            return state;
        }

        /// <summary>保留字归一：<c>"Any State"</c> / <c>"any_state"</c> 都能认。</summary>
        internal static string NormalizeKeyword(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Replace(" ", string.Empty).Replace("_", string.Empty).Trim().ToLowerInvariant();
        }

        /// <summary>解析参数类型（<c>Trigger</c> / <c>Bool</c> / <c>Float</c> / <c>Int</c>）。</summary>
        internal static AnimatorControllerParameterType ParseParameterType(CommandContext context, string key,
            AnimatorControllerParameterType fallback)
        {
            if (!context.Args.Has(key))
            {
                return fallback;
            }

            var text = (context.Args.GetString(key, string.Empty) ?? string.Empty).Trim().ToLowerInvariant();
            switch (text)
            {
                case "":
                    return fallback;
                case "trigger":
                case "trig":
                    return AnimatorControllerParameterType.Trigger;
                case "bool":
                case "boolean":
                    return AnimatorControllerParameterType.Bool;
                case "float":
                case "number":
                    return AnimatorControllerParameterType.Float;
                case "int":
                case "integer":
                    return AnimatorControllerParameterType.Int;
                default:
                    throw new CommandArgumentException(context.CommandId, key,
                        $"不支持的类型 '{text}'（可选 Trigger / Bool / Float / Int）");
            }
        }

        /// <summary>解析条件模式（<c>if</c> / <c>ifNot</c> / <c>greater</c> / <c>less</c> / <c>equals</c> / <c>notEqual</c>）。</summary>
        internal static AnimatorConditionMode ParseConditionMode(string text)
        {
            switch ((text ?? "if").Trim().ToLowerInvariant())
            {
                case "":
                case "if":
                case "true":
                case "is":
                    return AnimatorConditionMode.If;
                case "ifnot":
                case "false":
                case "not":
                    return AnimatorConditionMode.IfNot;
                case "greater":
                case ">":
                    return AnimatorConditionMode.Greater;
                case "less":
                case "<":
                    return AnimatorConditionMode.Less;
                case "equals":
                case "equal":
                case "==":
                case "=":
                    return AnimatorConditionMode.Equals;
                case "notequal":
                case "!=":
                    return AnimatorConditionMode.NotEqual;
                default:
                    throw new CommandException(
                        $"不支持的条件模式 '{text}'（可选 if / ifNot / greater / less / equals / notEqual）");
            }
        }

        /// <summary>按名字找参数；找不到返回 null。</summary>
        internal static AnimatorControllerParameter FindParameter(AnimatorController controller, string name)
        {
            foreach (var parameter in controller.parameters)
            {
                if (string.Equals(parameter.name, name, StringComparison.Ordinal))
                {
                    return parameter;
                }
            }

            return null;
        }

        /// <summary>"name(Type)、name(Type)" 形式，用于报错时列出可用参数。</summary>
        internal static string DescribeParameters(AnimatorController controller)
        {
            var names = new List<string>();
            foreach (var parameter in controller.parameters)
            {
                names.Add(parameter.name + "(" + parameter.type + ")");
            }

            return names.Count == 0 ? "（还没有参数）" : string.Join("、", names);
        }

        /// <summary>
        /// 条件与参数类型的兼容性校验。
        /// <para>
        /// 冲突时 Unity 自己的表现是"过渡永远不触发"，不报错——所以这里提前拦：
        /// Trigger 只能配 <c>If</c>（这就是"触发器到点"，编辑器 UI 里只显示参数名、不显示模式）；
        /// Bool 只能 <c>If</c> / <c>IfNot</c>；Float / Int 只能比较类（Greater / Less / Equals / NotEqual）。
        /// </para>
        /// </summary>
        internal static AnimatorControllerParameter ValidateCondition(CommandContext context,
            AnimatorController controller, string parameterName, AnimatorConditionMode mode, string key)
        {
            var parameter = FindParameter(controller, parameterName);
            if (parameter == null)
            {
                throw new CommandArgumentException(context.CommandId, key,
                    $"条件引用的参数 '{parameterName}' 不存在。现有参数：{DescribeParameters(controller)}");
            }

            var numericMode = mode == AnimatorConditionMode.Greater || mode == AnimatorConditionMode.Less
                              || mode == AnimatorConditionMode.Equals || mode == AnimatorConditionMode.NotEqual;
            var booleanMode = mode == AnimatorConditionMode.If || mode == AnimatorConditionMode.IfNot;

            if (parameter.type == AnimatorControllerParameterType.Trigger)
            {
                // Trigger 的过渡条件在 Unity 里恒为 If（"该触发器被置位"），不能取反也不能比大小
                if (mode != AnimatorConditionMode.If)
                {
                    throw new CommandArgumentException(context.CommandId, key,
                        $"参数 '{parameterName}' 是 Trigger，条件模式只能用 if（收到 {mode}）");
                }

                return parameter;
            }

            if (parameter.type == AnimatorControllerParameterType.Bool && !booleanMode)
            {
                throw new CommandArgumentException(context.CommandId, key,
                    $"参数 '{parameterName}' 是 Bool，条件模式只能用 if / ifNot（收到 {mode}）");
            }

            if (parameter.type != AnimatorControllerParameterType.Bool && !numericMode)
            {
                throw new CommandArgumentException(context.CommandId, key,
                    $"参数 '{parameterName}' 是 {parameter.type}，条件模式要用 greater / less / equals / notEqual"
                    + $"（收到 {mode}）");
            }

            return parameter;
        }

        /// <summary>把一条过渡的条件列表转成 JSON。</summary>
        internal static JsonValue ConditionsToJson(AnimatorTransitionBase transition)
        {
            var array = JsonValue.NewArray();
            if (transition == null)
            {
                return array;
            }

            var conditions = transition.conditions;
            if (conditions == null)
            {
                return array;
            }

            for (var i = 0; i < conditions.Length; i++)
            {
                var item = JsonValue.NewObject();
                item.Set("parameter", JsonValue.Of(conditions[i].parameter));
                item.Set("mode", JsonValue.Of(conditions[i].mode.ToString()));
                item.Set("threshold", JsonValue.Of(conditions[i].threshold));
                array.Add(item);
            }

            return array;
        }

        /// <summary>删掉过渡里引用了某参数的所有条件，返回删了几条（<c>animator.parameter.remove</c> 用）。</summary>
        internal static int StripConditions(AnimatorTransitionBase transition, string parameterName)
        {
            if (transition == null)
            {
                return 0;
            }

            var conditions = transition.conditions;
            if (conditions == null || conditions.Length == 0)
            {
                return 0;
            }

            var removed = 0;
            // conditions 是数组快照，边遍历边 RemoveCondition 是安全的
            for (var i = 0; i < conditions.Length; i++)
            {
                if (!string.Equals(conditions[i].parameter, parameterName, StringComparison.Ordinal))
                {
                    continue;
                }

                transition.RemoveCondition(conditions[i]);
                removed++;
            }

            return removed;
        }

        /// <summary>把一条过渡序列化成 JSON（条件 + 时长 + 目标）。</summary>
        internal static JsonValue TransitionToJson(AnimatorTransitionBase transition)
        {
            var item = JsonValue.NewObject();
            if (transition == null)
            {
                return item;
            }

            if (transition.destinationState != null)
            {
                item.Set("to", JsonValue.Of(transition.destinationState.name));
            }
            else if (transition.destinationStateMachine != null)
            {
                item.Set("to", JsonValue.Of(transition.destinationStateMachine.name));
                item.Set("toStateMachine", JsonValue.Of(true));
            }
            else
            {
                item.Set("to", JsonValue.Of(transition.isExit ? "(Exit)" : "(unknown)"));
            }

            item.Set("isExit", JsonValue.Of(transition.isExit));
            item.Set("conditions", ConditionsToJson(transition));

            var stateTransition = transition as AnimatorStateTransition;
            if (stateTransition != null)
            {
                item.Set("hasExitTime", JsonValue.Of(stateTransition.hasExitTime));
                item.Set("exitTime", JsonValue.Of(stateTransition.exitTime));
                item.Set("duration", JsonValue.Of(stateTransition.duration));
                item.Set("hasFixedDuration", JsonValue.Of(stateTransition.hasFixedDuration));
                item.Set("canTransitionToSelf", JsonValue.Of(stateTransition.canTransitionToSelf));
            }

            return item;
        }

        /// <summary>逐级创建 Assets 下缺失的文件夹（<c>AssetDatabase.CreateFolder</c> 保证 .meta/GUID 正常）。</summary>
        internal static void EnsureFolders(string assetPath)
        {
            var segments = assetPath.Split('/');
            // segments[0] = "Assets"，最后一段是文件名，中间的都是文件夹
            for (var i = 1; i < segments.Length - 1; i++)
            {
                var folder = string.Join("/", segments, 0, i + 1);
                if (AssetDatabase.IsValidFolder(folder))
                {
                    continue;
                }

                // CreateFolder 返回的是新文件夹的 GUID（失败才是空串），别把 GUID 当错误信息（BUG-003）
                var guid = AssetDatabase.CreateFolder(string.Join("/", segments, 0, i), segments[i]);
                if (string.IsNullOrEmpty(guid) || !AssetDatabase.IsValidFolder(folder))
                {
                    throw new CommandException($"创建文件夹 '{folder}' 失败（AssetDatabase.CreateFolder 未返回 GUID）");
                }
            }
        }

        /// <summary>把改动落盘：controller 与其状态机一起打脏，再 SaveAssets。</summary>
        internal static void Persist(AnimatorController controller, AnimatorStateMachine stateMachine = null)
        {
            EditorUtility.SetDirty(controller);
            if (stateMachine != null)
            {
                EditorUtility.SetDirty(stateMachine);
            }

            AssetDatabase.SaveAssets();
        }

        /// <summary>Play 模式下改动画资产容易被运行中的 Animator 覆盖，给一条 warning（不阻断）。</summary>
        internal static CommandResult WarnIfPlaying(CommandResult result)
        {
            if (EditorApplication.isPlaying)
            {
                result.AddWarning("编辑器处于 Play 模式：改动的 controller 可能被运行中的 Animator 覆盖，建议退出 Play 后再改");
            }

            return result;
        }

        /// <summary>取某个层当前状态数（<c>state.add</c> 自动排布位置用）。</summary>
        internal static int StateCount(AnimatorControllerLayer layer)
        {
            var states = layer.stateMachine != null ? layer.stateMachine.states : null;
            return states == null ? 0 : states.Length;
        }
    }

    /// <summary>
    /// 新建 <c>.controller</c> 资产。
    /// <para>
    /// <c>AnimatorController.CreateAnimatorControllerAtPath</c> 会顺手建一个"Base Layer"层和一个
    /// 默认状态（没有 motion 的空壳）。<c>cleanup</c> 默认 <b>true</b>，把这种"自动生成、没有 motion、
    /// 且是当前默认状态"的空壳清掉，让调用方从空状态机开始装配——不然每次装配都要先猜它叫什么名字再删。
    /// 只删满足这三条的，真正的默认状态（已经绑了 clip 的）不会被动。
    /// </para>
    /// </summary>
    [Command("animator.controller.create",
        Description = "新建 AnimatorController 资产（缺省清掉自动生成的空默认状态，使状态机从空开始）",
        Usage = "path: string, layer: string, weight: number, overwrite: bool, cleanup: bool, addressable: bool",
        EditorOnly = true)]
    public sealed class AnimatorControllerCreateCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var assetPath = AnimatorAssetUtil.NormalizeControllerPath(context,
                context.Args.RequireString("path"), "path");
            var overwrite = context.Args.GetBool("overwrite", false);
            var cleanup = context.Args.GetBool("cleanup", true);
            var addressable = context.Args.GetBool("addressable", false);
            var layerName = context.Args.GetString("layer", null);
            var weight = context.Args.GetFloat("weight", 1f);

            if (context.DryRun)
            {
                return CommandResult.Skip($"dry-run：将新建 controller {assetPath}");
            }

            var existed = AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath) != null;
            if (existed && !overwrite)
            {
                throw new CommandException(
                    $"'{assetPath}' 已存在。要重建请加 overwrite:true（会先删掉原资产，已有引用会断）");
            }

            if (existed && !AssetDatabase.DeleteAsset(assetPath))
            {
                throw new CommandException($"覆盖失败：无法删除旧资产 {assetPath}");
            }

            AnimatorAssetUtil.EnsureFolders(assetPath);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(assetPath);
            if (controller == null)
            {
                throw new CommandException($"创建 AnimatorController 失败：{assetPath}");
            }

            var removedStates = new List<string>();
            if (cleanup)
            {
                foreach (var layer in controller.layers)
                {
                    var stateMachine = layer.stateMachine;
                    if (stateMachine == null)
                    {
                        continue;
                    }

                    var states = stateMachine.states;
                    // 倒序删，避免下标失效；只删"无 motion 且是默认状态"的空壳
                    for (var i = states.Length - 1; i >= 0; i--)
                    {
                        var state = states[i].state;
                        if (state == null || state.motion != null || stateMachine.defaultState != state)
                        {
                            continue;
                        }

                        stateMachine.defaultState = null;
                        stateMachine.RemoveState(state);
                        removedStates.Add(state.name);
                    }
                }
            }

            var layers = controller.layers;
            if (layers.Length == 0)
            {
                throw new CommandException("新建的 controller 没有层，无法命名");
            }

            if (!string.IsNullOrEmpty(layerName))
            {
                layers[0].name = layerName;
            }

            // 无条件写权重：CreateAnimatorControllerAtPath 建出来的层 m_DefaultWeight 是 0（实测），
            // 权重 0 的层等于不参与混合——动画"完全没生效"却不报任何错。别改成"不同才写"。
            layers[0].defaultWeight = weight;
            controller.layers = layers;

            var warnings = new List<string>();
            if (addressable)
            {
                try
                {
                    AddressablesBridge.EnsureEntry(assetPath, null, null, null, out var created);
                    if (!created)
                    {
                        warnings.Add("Addressable 条目已存在，未改动");
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add("注册 Addressable 失败（不影响 controller 本体）：" + ex.Message);
                }
            }

            AnimatorAssetUtil.Persist(controller);

            var data = JsonValue.NewObject();
            data.Set("path", JsonValue.Of(assetPath));
            data.Set("overwritten", JsonValue.Of(existed));
            data.Set("layerCount", JsonValue.Of(controller.layers.Length));
            var layerNames = JsonValue.NewArray();
            foreach (var layer in controller.layers)
            {
                layerNames.Add(JsonValue.Of(layer.name));
            }

            data.Set("layers", layerNames);
            data.Set("weight", JsonValue.Of(controller.layers.Length > 0 ? controller.layers[0].defaultWeight : 0f));
            data.Set("parameterCount", JsonValue.Of(controller.parameters.Length));
            var removed = JsonValue.NewArray();
            foreach (var name in removedStates)
            {
                removed.Add(JsonValue.Of(name));
            }

            data.Set("removedStates", removed);

            var result = CommandResult.Ok(
                $"已创建 controller：{assetPath}（{controller.layers.Length} 层，"
                + (cleanup ? $"清空默认空壳 {removedStates.Count} 个" : "保留自动生成的默认状态") + "）",
                data);
            foreach (var warning in warnings)
            {
                result.AddWarning(warning);
            }

            return AnimatorAssetUtil.WarnIfPlaying(result);
        }
    }

    /// <summary>
    /// 给 controller 加参数。
    /// <para>
    /// <c>default</c> 按类型解释：Bool 收 true/false、Float 收数字、Int 收整数；Trigger 没有默认值
    /// （传了会 warning 说明已忽略，而不是静默吞掉）。
    /// </para>
    /// </summary>
    [Command("animator.parameter.add",
        Description = "给 AnimatorController 加参数（Trigger / Bool / Float / Int），可同时指定默认值",
        Usage = "controller: string, name: string, type: string, default: string|number|bool",
        EditorOnly = true)]
    public sealed class AnimatorParameterAddCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var controller = AnimatorAssetUtil.LoadController(context, out var assetPath);
            var name = context.Args.RequireString("name");
            var type = AnimatorAssetUtil.ParseParameterType(context, "type", AnimatorControllerParameterType.Trigger);
            var hasDefault = context.Args.Has("default");

            if (context.DryRun)
            {
                return CommandResult.Skip($"dry-run：将给 {assetPath} 加参数 {name}（{type}）");
            }

            if (AnimatorAssetUtil.FindParameter(controller, name) != null)
            {
                throw new CommandArgumentException(context.CommandId, "name",
                    $"参数 '{name}' 已存在。现有参数：{AnimatorAssetUtil.DescribeParameters(controller)}");
            }

            var parameter = new AnimatorControllerParameter { name = name, type = type };
            var ignoredDefault = false;
            switch (type)
            {
                case AnimatorControllerParameterType.Bool:
                    parameter.defaultBool = context.Args.GetBool("default", false);
                    break;
                case AnimatorControllerParameterType.Float:
                    parameter.defaultFloat = context.Args.GetFloat("default", 0f);
                    break;
                case AnimatorControllerParameterType.Int:
                    parameter.defaultInt = context.Args.GetInt("default", 0);
                    break;
                default:
                    // Trigger 没有默认值：明确说一声，别让人以为 default 生效了
                    ignoredDefault = hasDefault;
                    break;
            }

            var stateMachine = controller.layers.Length > 0 ? controller.layers[0].stateMachine : null;
            controller.AddParameter(parameter);
            AnimatorAssetUtil.Persist(controller, stateMachine);

            var data = JsonValue.NewObject();
            data.Set("controller", JsonValue.Of(assetPath));
            data.Set("name", JsonValue.Of(name));
            data.Set("type", JsonValue.Of(type.ToString()));
            data.Set("parameterCount", JsonValue.Of(controller.parameters.Length));

            var result = CommandResult.Ok($"已加参数 {name}（{type}）→ {assetPath}", data);
            if (ignoredDefault)
            {
                result.AddWarning("Trigger 参数没有默认值，已忽略 default");
            }

            return AnimatorAssetUtil.WarnIfPlaying(result);
        }
    }

    /// <summary>
    /// 删参数，并连带清掉引用它的过渡条件。
    /// <para>
    /// 直接 <c>RemoveParameter</c> 会留下一堆指向已删参数的条件（编辑器里表现为永远不触发的过渡），
    /// 所以先扫全部层/状态/过渡把条件摘掉，再删参数，并把摘掉的条数回报出来。
    /// </para>
    /// </summary>
    [Command("animator.parameter.remove",
        Description = "删除 AnimatorController 参数，并清理引用它的过渡条件（回报清掉的条数）",
        Usage = "controller: string, name: string",
        EditorOnly = true)]
    public sealed class AnimatorParameterRemoveCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var controller = AnimatorAssetUtil.LoadController(context, out var assetPath);
            var name = context.Args.RequireString("name");

            if (context.DryRun)
            {
                return CommandResult.Skip($"dry-run：将删除参数 {name}");
            }

            var parameters = controller.parameters;
            var index = -1;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (string.Equals(parameters[i].name, name, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                throw new CommandArgumentException(context.CommandId, "name",
                    $"参数 '{name}' 不存在。现有参数：{AnimatorAssetUtil.DescribeParameters(controller)}");
            }

            var removedConditions = 0;
            var touchedTransitions = 0;
            AnimatorStateMachine firstStateMachine = null;
            foreach (var layer in controller.layers)
            {
                var stateMachine = layer.stateMachine;
                if (stateMachine == null)
                {
                    continue;
                }

                if (firstStateMachine == null)
                {
                    firstStateMachine = stateMachine;
                }

                foreach (var transition in stateMachine.anyStateTransitions)
                {
                    var removed = AnimatorAssetUtil.StripConditions(transition, name);
                    removedConditions += removed;
                    if (removed > 0)
                    {
                        touchedTransitions++;
                    }
                }

                foreach (var child in stateMachine.states)
                {
                    if (child.state == null)
                    {
                        continue;
                    }

                    foreach (var transition in child.state.transitions)
                    {
                        var removed = AnimatorAssetUtil.StripConditions(transition, name);
                        removedConditions += removed;
                        if (removed > 0)
                        {
                            touchedTransitions++;
                        }
                    }
                }
            }

            controller.RemoveParameter(index);
            AnimatorAssetUtil.Persist(controller, firstStateMachine);

            var data = JsonValue.NewObject();
            data.Set("controller", JsonValue.Of(assetPath));
            data.Set("name", JsonValue.Of(name));
            data.Set("removedConditions", JsonValue.Of(removedConditions));
            data.Set("touchedTransitions", JsonValue.Of(touchedTransitions));
            data.Set("parameterCount", JsonValue.Of(controller.parameters.Length));

            return AnimatorAssetUtil.WarnIfPlaying(CommandResult.Ok(
                $"已删除参数 {name}"
                + (removedConditions > 0 ? $"（同时清掉 {removedConditions} 条引用它的过渡条件）" : string.Empty),
                data));
        }
    }

    /// <summary>
    /// 往某一层加状态，可同时绑 motion、设默认状态、给图上的位置。
    /// <para>
    /// 没给 <c>position</c> 时按"状态数"自动往右排（x 每级 +250），避免所有新状态叠在 (0,0)。
    /// <c>motion</c> 只接受工程内已存在的 <c>.anim</c>（本指令不造 clip，见类注释里的范围说明）。
    /// </para>
    /// </summary>
    [Command("animator.state.add",
        Description = "给 AnimatorController 的某一层加状态（可绑现有 AnimationClip、设默认状态、给图位置）",
        Usage = "controller: string, name: string, layer: int|string, motion: string, default: bool, "
                + "position: [x,y], tag: string, speed: number, writeDefaultValues: bool",
        EditorOnly = true)]
    public sealed class AnimatorStateAddCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var controller = AnimatorAssetUtil.LoadController(context, out var assetPath);
            var name = context.Args.RequireString("name");
            var layerIndex = AnimatorAssetUtil.ResolveLayerIndex(context, controller);
            var layer = controller.layers[layerIndex];
            var stateMachine = layer.stateMachine;
            if (stateMachine == null)
            {
                throw new CommandException($"层 '{layer.name}' 没有状态机（controller 结构异常）");
            }

            if (context.DryRun)
            {
                return CommandResult.Skip($"dry-run：将在层 '{layer.name}' 加状态 {name}");
            }

            if (AnimatorAssetUtil.FindState(layer, name) != null)
            {
                throw new CommandArgumentException(context.CommandId, "name",
                    $"层 '{layer.name}' 里已有同名状态 '{name}'。现有状态：{AnimatorAssetUtil.DescribeStateNames(layer)}");
            }

            // 先校验 motion，再建状态——避免"状态建出来了但 motion 没绑上"的半成品
            AnimationClip clip = null;
            var motionPath = context.Args.GetString("motion", null);
            if (!string.IsNullOrEmpty(motionPath))
            {
                clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(motionPath);
                if (clip == null)
                {
                    throw new CommandArgumentException(context.CommandId, "motion",
                        $"找不到 AnimationClip：{motionPath}（本指令只绑工程内已有的 .anim，不新建 clip）");
                }
            }

            var position = context.Args.Has("position")
                ? context.Args.GetVector2("position", Vector2.zero)
                : new Vector2(-300f + AnimatorAssetUtil.StateCount(layer) * 250f, 50f);

            var state = stateMachine.AddState(name, new Vector3(position.x, position.y, 0f));
            if (state == null)
            {
                throw new CommandException($"添加状态 '{name}' 失败（AnimatorStateMachine.AddState 返回空）");
            }

            if (clip != null)
            {
                state.motion = clip;
            }

            var tag = context.Args.GetString("tag", null);
            if (!string.IsNullOrEmpty(tag))
            {
                state.tag = tag;
            }

            var speed = context.Args.GetFloat("speed", float.NaN);
            if (!float.IsNaN(speed))
            {
                state.speed = speed;
            }

            var writeDefaults = context.Args.Has("writeDefaultValues");
            if (writeDefaults)
            {
                state.writeDefaultValues = context.Args.GetBool("writeDefaultValues", true);
            }

            var isDefault = context.Args.GetBool("default", false);
            if (isDefault)
            {
                stateMachine.defaultState = state;
            }

            AnimatorAssetUtil.Persist(controller, stateMachine);

            var data = JsonValue.NewObject();
            data.Set("controller", JsonValue.Of(assetPath));
            data.Set("name", JsonValue.Of(name));
            data.Set("layer", JsonValue.Of(layer.name));
            data.Set("layerIndex", JsonValue.Of(layerIndex));
            data.Set("motion", JsonValue.Of(clip != null ? AssetDatabase.GetAssetPath(clip) : string.Empty));
            data.Set("isDefault", JsonValue.Of(isDefault));
            data.Set("stateCount", JsonValue.Of(AnimatorAssetUtil.StateCount(layer)));

            return AnimatorAssetUtil.WarnIfPlaying(CommandResult.Ok(
                $"已在层 '{layer.name}' 加状态 {name}"
                + (clip != null ? $"（motion={clip.name}）" : "（未绑 motion）")
                + (isDefault ? "，并设为默认状态" : string.Empty),
                data));
        }
    }

    /// <summary>
    /// 删状态。指向它的过渡会被 Unity 一并清掉；如果它是默认状态，先把默认状态清空再删
    /// （否则 controller 会留一个指向已删对象的默认状态）。
    /// </summary>
    [Command("animator.state.remove",
        Description = "删除 AnimatorController 某一层里的状态（是默认状态时先清空默认再删）",
        Usage = "controller: string, name: string, layer: int|string",
        EditorOnly = true)]
    public sealed class AnimatorStateRemoveCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var controller = AnimatorAssetUtil.LoadController(context, out var assetPath);
            var name = context.Args.RequireString("name");
            var layerIndex = AnimatorAssetUtil.ResolveLayerIndex(context, controller);
            var layer = controller.layers[layerIndex];
            var state = AnimatorAssetUtil.RequireState(context, layer, name, "name");

            if (context.DryRun)
            {
                return CommandResult.Skip($"dry-run：将删除状态 {name}");
            }

            var stateMachine = layer.stateMachine;
            var wasDefault = stateMachine.defaultState == state;
            if (wasDefault)
            {
                stateMachine.defaultState = null;
            }

            stateMachine.RemoveState(state);
            AnimatorAssetUtil.Persist(controller, stateMachine);

            var data = JsonValue.NewObject();
            data.Set("controller", JsonValue.Of(assetPath));
            data.Set("name", JsonValue.Of(name));
            data.Set("wasDefault", JsonValue.Of(wasDefault));
            data.Set("stateCount", JsonValue.Of(AnimatorAssetUtil.StateCount(layer)));

            var result = CommandResult.Ok($"已删除状态 {name}（层 '{layer.name}'）", data);
            if (wasDefault)
            {
                result.AddWarning($"'{name}' 原本是默认状态，已清空 defaultState——记得给层重新指定一个默认状态");
            }

            return AnimatorAssetUtil.WarnIfPlaying(result);
        }
    }

    /// <summary>
    /// 加过渡。<c>from</c> / <c>to</c> 支持保留字 <c>AnyState</c> / <c>Entry</c> / <c>Exit</c>，
    /// 也支持状态名。四种组合都走同一条指令：
    /// <list type="bullet">
    /// <item><c>AnyState → 状态</c>：<c>AddAnyStateTransition</c>（打断式，常用来做弹窗互斥）</item>
    /// <item><c>Entry → 状态</c>：<c>AddEntryTransition</c>（**不支持条件**，传了会 warning 而不是失败）</item>
    /// <item><c>状态 → Exit</c>：<c>AddExitTransition</c>（末帧收尾，配 <c>hasExitTime</c> 用）</item>
    /// <item><c>状态 → 状态</c>：<c>AddTransition</c></item>
    /// </list>
    /// <para>
    /// <c>hasExitTime</c> 默认 <b>false</b>（本指令口径：条件满足即切换）。默认 true 会让"加完 trigger 却没反应"
    /// 变成常态——那正是这套指令要消灭的坑。要等动画播完再切，显式传 <c>hasExitTime:true</c>。
    /// </para>
    /// <para>
    /// 条件在创建过渡<b>之前</b>全部校验（参数存在 + 模式与参数类型兼容），不合法直接失败、不留半条过渡。
    /// </para>
    /// </summary>
    [Command("animator.transition.add",
        Description = "加过渡（AnyState / Entry / Exit 作保留字；hasExitTime 默认 false，条件先校验后创建）",
        Usage = "controller: string, from: string, to: string, layer: int|string, "
                + "conditions: [{parameter, mode, threshold}], hasExitTime: bool, exitTime: number, "
                + "duration: number, hasFixedDuration: bool, canTransitionToSelf: bool",
        EditorOnly = true)]
    public sealed class AnimatorTransitionAddCommand : ICommand
    {
        private struct PendingCondition
        {
            public AnimatorConditionMode Mode;
            public float Threshold;
            public string Parameter;
        }

        public CommandResult Execute(CommandContext context)
        {
            var controller = AnimatorAssetUtil.LoadController(context, out var assetPath);
            var layerIndex = AnimatorAssetUtil.ResolveLayerIndex(context, controller);
            var layer = controller.layers[layerIndex];
            var stateMachine = layer.stateMachine;
            if (stateMachine == null)
            {
                throw new CommandException($"层 '{layer.name}' 没有状态机（controller 结构异常）");
            }

            var fromRaw = context.Args.RequireString("from");
            var toRaw = context.Args.RequireString("to");
            var fromKey = AnimatorAssetUtil.NormalizeKeyword(fromRaw);
            var toKey = AnimatorAssetUtil.NormalizeKeyword(toRaw);

            // 条件先解析 + 校验：不合法就整条失败，不留下"过渡建好了但条件没加上"的半成品
            var pending = ParseConditions(context, controller);

            if (context.DryRun)
            {
                return CommandResult.Skip($"dry-run：将加过渡 {fromRaw} → {toRaw}");
            }

            AnimatorStateTransition stateTransition = null;
            AnimatorTransition entryTransition = null;
            var kind = "state";

            if (fromKey == AnimatorAssetUtil.AnyState)
            {
                if (toKey == AnimatorAssetUtil.ExitState)
                {
                    throw new CommandArgumentException(context.CommandId, "to",
                        "AnyState → Exit 暂不支持：AnimatorStateMachine.AddAnyStateTransition 只接受具体状态，"
                        + "没有通向 Exit 的重载。要「任何状态都能收尾」，请给需要收尾的状态各加一条 状态 → Exit，"
                        + "或用 Bool 条件进一个统一的 Exit 状态");
                }

                var target = AnimatorAssetUtil.RequireState(context, layer, toRaw, "to");
                stateTransition = stateMachine.AddAnyStateTransition(target);
                kind = "anyState";
            }
            else if (fromKey == AnimatorAssetUtil.EntryState)
            {
                var target = AnimatorAssetUtil.RequireState(context, layer, toRaw, "to");
                entryTransition = stateMachine.AddEntryTransition(target);
                kind = "entry";
            }
            else if (fromKey == AnimatorAssetUtil.ExitState)
            {
                throw new CommandArgumentException(context.CommandId, "from",
                    "Exit 只能作为 to（Exit 是终点，不能作为过渡起点）");
            }
            else
            {
                var fromState = AnimatorAssetUtil.RequireState(context, layer, fromRaw, "from");
                if (toKey == AnimatorAssetUtil.ExitState)
                {
                    stateTransition = fromState.AddExitTransition();
                    kind = "exit";
                }
                else
                {
                    var target = AnimatorAssetUtil.RequireState(context, layer, toRaw, "to");
                    stateTransition = fromState.AddTransition(target);
                }
            }

            var warnings = new List<string>();

            if (stateTransition != null)
            {
                // 显式给默认值：条件满足就切，不等动画（见类注释）
                stateTransition.hasExitTime = context.Args.GetBool("hasExitTime", false);

                if (context.Args.Has("exitTime"))
                {
                    stateTransition.exitTime = context.Args.GetFloat("exitTime", 1f);
                }

                if (context.Args.Has("duration"))
                {
                    stateTransition.duration = context.Args.GetFloat("duration", 0.1f);
                }

                if (context.Args.Has("hasFixedDuration"))
                {
                    stateTransition.hasFixedDuration = context.Args.GetBool("hasFixedDuration", true);
                }

                if (context.Args.Has("canTransitionToSelf"))
                {
                    stateTransition.canTransitionToSelf = context.Args.GetBool("canTransitionToSelf", true);
                }

                foreach (var condition in pending)
                {
                    stateTransition.AddCondition(condition.Mode, condition.Threshold, condition.Parameter);
                }
            }
            else if (pending.Count > 0)
            {
                warnings.Add($"Entry 过渡不支持条件，已忽略 {pending.Count} 条（Entry → 状态 永远无条件是 Unity 的模型）");
            }

            AnimatorAssetUtil.Persist(controller, stateMachine);

            var transition = (AnimatorTransitionBase)stateTransition ?? entryTransition;
            var data = JsonValue.NewObject();
            data.Set("controller", JsonValue.Of(assetPath));
            data.Set("layer", JsonValue.Of(layer.name));
            data.Set("layerIndex", JsonValue.Of(layerIndex));
            data.Set("kind", JsonValue.Of(kind));
            data.Set("from", JsonValue.Of(fromRaw));
            data.Set("to", JsonValue.Of(toRaw));
            data.Set("conditionCount", JsonValue.Of(stateTransition != null ? pending.Count : 0));
            data.Set("transition", AnimatorAssetUtil.TransitionToJson(transition));

            var conditionNote = stateTransition != null && pending.Count > 0
                ? $"（条件 {pending.Count} 条）"
                : string.Empty;
            var result = CommandResult.Ok($"已加过渡 {fromRaw} → {toRaw}{conditionNote}（层 '{layer.name}'）", data);
            foreach (var warning in warnings)
            {
                result.AddWarning(warning);
            }

            return AnimatorAssetUtil.WarnIfPlaying(result);
        }

        /// <summary>
        /// 解析 <c>conditions</c>：<c>[{parameter, mode, threshold}]</c>，也接受纯字符串数组（等价 mode=if）。
        /// 每条都过 <see cref="AnimatorAssetUtil.ValidateCondition"/>，不合法立刻抛。
        /// </summary>
        private static List<PendingCondition> ParseConditions(CommandContext context, AnimatorController controller)
        {
            var result = new List<PendingCondition>();
            var raw = context.Args.Get("conditions");
            if (raw == null || raw.IsNull)
            {
                return result;
            }

            if (!raw.IsArray)
            {
                throw new CommandArgumentException(context.CommandId, "conditions",
                    "conditions 要是数组，元素形如 {\"parameter\":\"OnEnter\",\"mode\":\"if\",\"threshold\":0}");
            }

            var items = raw.Items;
            if (items == null)
            {
                return result;
            }

            foreach (var item in items)
            {
                string parameterName;
                var modeText = "if";
                var threshold = 0f;

                if (item.IsString)
                {
                    parameterName = item.AsString();
                }
                else if (item.IsObject)
                {
                    parameterName = item.GetString("parameter", null) ?? item.GetString("name", null);
                    modeText = item.GetString("mode", "if");
                    threshold = item.GetFloat("threshold", 0f);
                }
                else
                {
                    throw new CommandArgumentException(context.CommandId, "conditions",
                        "conditions 的每个元素要么是参数名字符串，要么是 {parameter, mode, threshold} 对象");
                }

                if (string.IsNullOrEmpty(parameterName))
                {
                    throw new CommandArgumentException(context.CommandId, "conditions",
                        "条件的 parameter 不能为空");
                }

                var mode = AnimatorAssetUtil.ParseConditionMode(modeText);
                AnimatorAssetUtil.ValidateCondition(context, controller, parameterName, mode, "conditions");
                result.Add(new PendingCondition { Mode = mode, Threshold = threshold, Parameter = parameterName });
            }

            return result;
        }
    }

    /// <summary>
    /// 删过渡。<c>from</c> / <c>to</c> 与 <c>transition.add</c> 同口径；
    /// 默认删掉<b>全部</b>匹配项（同名状态间常有多条过渡），可用 <c>index</c> 只删第 N 条（0-based）。
    /// </summary>
    [Command("animator.transition.remove",
        Description = "删除过渡（默认删全部 from→to 匹配项；index 可只删第 N 条）",
        Usage = "controller: string, from: string, to: string, layer: int|string, index: int",
        EditorOnly = true)]
    public sealed class AnimatorTransitionRemoveCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var controller = AnimatorAssetUtil.LoadController(context, out var assetPath);
            var layerIndex = AnimatorAssetUtil.ResolveLayerIndex(context, controller);
            var layer = controller.layers[layerIndex];
            var stateMachine = layer.stateMachine;
            if (stateMachine == null)
            {
                throw new CommandException($"层 '{layer.name}' 没有状态机（controller 结构异常）");
            }

            var fromRaw = context.Args.RequireString("from");
            var toRaw = context.Args.RequireString("to");
            var fromKey = AnimatorAssetUtil.NormalizeKeyword(fromRaw);
            var toKey = AnimatorAssetUtil.NormalizeKeyword(toRaw);
            var only = context.Args.Has("index") ? context.Args.GetInt("index", 0) : -1;

            if (context.DryRun)
            {
                return CommandResult.Skip($"dry-run：将删除过渡 {fromRaw} → {toRaw}");
            }

            var removed = 0;
            var matched = 0;

            if (fromKey == AnimatorAssetUtil.AnyState)
            {
                foreach (var transition in stateMachine.anyStateTransitions)
                {
                    if (!MatchesTarget(transition, toRaw, toKey))
                    {
                        continue;
                    }

                    if (only >= 0 && matched++ != only)
                    {
                        continue;
                    }

                    stateMachine.RemoveAnyStateTransition(transition);
                    removed++;
                }
            }
            else if (fromKey == AnimatorAssetUtil.EntryState)
            {
                foreach (var transition in stateMachine.entryTransitions)
                {
                    if (!MatchesTarget(transition, toRaw, toKey))
                    {
                        continue;
                    }

                    if (only >= 0 && matched++ != only)
                    {
                        continue;
                    }

                    stateMachine.RemoveEntryTransition(transition);
                    removed++;
                }
            }
            else
            {
                var fromState = AnimatorAssetUtil.RequireState(context, layer, fromRaw, "from");
                foreach (var transition in fromState.transitions)
                {
                    if (toKey == AnimatorAssetUtil.ExitState)
                    {
                        if (!transition.isExit)
                        {
                            continue;
                        }
                    }
                    else if (!MatchesTarget(transition, toRaw, toKey))
                    {
                        continue;
                    }

                    if (only >= 0 && matched++ != only)
                    {
                        continue;
                    }

                    fromState.RemoveTransition(transition);
                    removed++;
                }
            }

            if (removed == 0)
            {
                throw new CommandException(
                    $"没有匹配的过渡（{fromRaw} → {toRaw}，层 '{layer.name}'）。"
                    + "用 animator.controller.get 看一下实际有哪些过渡");
            }

            AnimatorAssetUtil.Persist(controller, stateMachine);

            var data = JsonValue.NewObject();
            data.Set("controller", JsonValue.Of(assetPath));
            data.Set("layer", JsonValue.Of(layer.name));
            data.Set("from", JsonValue.Of(fromRaw));
            data.Set("to", JsonValue.Of(toRaw));
            data.Set("removedCount", JsonValue.Of(removed));

            return AnimatorAssetUtil.WarnIfPlaying(CommandResult.Ok(
                $"已删除 {removed} 条过渡（{fromRaw} → {toRaw}，层 '{layer.name}'）", data));
        }

        private static bool MatchesTarget(AnimatorTransitionBase transition, string toRaw, string toKey)
        {
            if (transition == null)
            {
                return false;
            }

            if (toKey == AnimatorAssetUtil.ExitState)
            {
                return transition.isExit;
            }

            var target = transition.destinationState;
            if (target == null)
            {
                return false;
            }

            return string.Equals(target.name, toRaw, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 只读：把一个 controller 的结构（参数 / 层 / 状态 / 过渡 / 条件）读出来。
    /// <para>
    /// 这条的性价比比写入还高——没有它，AI 只能把 <c>.controller</c> 当 YAML 硬啃
    /// （FEAT-019 里就是这么干的，还先被 CRLF 把 <c>path:</c> 正则弄断过一次）。
    /// </para>
    /// </summary>
    [Command("animator.controller.get",
        Description = "只读：读 AnimatorController 的参数 / 层 / 状态 / 过渡 / 条件（替代硬啃 .controller YAML）",
        Usage = "controller: string, layer: int|string, includeTransitions: bool, maxTransitions: int",
        EditorOnly = true)]
    public sealed class AnimatorControllerGetCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var controller = AnimatorAssetUtil.LoadController(context, out var assetPath);
            var includeTransitions = context.Args.GetBool("includeTransitions", true);
            var maxTransitions = context.Args.GetInt("maxTransitions", 200);
            if (maxTransitions <= 0)
            {
                maxTransitions = 200;
            }

            // layer 省略 = 读全部层；给了就只读那一层
            var onlyLayer = context.Args.Has("layer") ? AnimatorAssetUtil.ResolveLayerIndex(context, controller) : -1;

            var data = JsonValue.NewObject();
            data.Set("path", JsonValue.Of(assetPath));
            data.Set("name", JsonValue.Of(controller.name));

            var parameters = JsonValue.NewArray();
            foreach (var parameter in controller.parameters)
            {
                var item = JsonValue.NewObject();
                item.Set("name", JsonValue.Of(parameter.name));
                item.Set("type", JsonValue.Of(parameter.type.ToString()));
                switch (parameter.type)
                {
                    case AnimatorControllerParameterType.Bool:
                        item.Set("default", JsonValue.Of(parameter.defaultBool));
                        break;
                    case AnimatorControllerParameterType.Float:
                        item.Set("default", JsonValue.Of(parameter.defaultFloat));
                        break;
                    case AnimatorControllerParameterType.Int:
                        item.Set("default", JsonValue.Of(parameter.defaultInt));
                        break;
                }

                parameters.Add(item);
            }

            data.Set("parameterCount", JsonValue.Of(controller.parameters.Length));
            data.Set("parameters", parameters);

            var layersJson = JsonValue.NewArray();
            var transitionBudget = maxTransitions;
            var totalTransitions = 0;
            var truncated = false;

            var layers = controller.layers;
            for (var i = 0; i < layers.Length; i++)
            {
                if (onlyLayer >= 0 && i != onlyLayer)
                {
                    continue;
                }

                var layer = layers[i];
                var layerJson = JsonValue.NewObject();
                layerJson.Set("index", JsonValue.Of(i));
                layerJson.Set("name", JsonValue.Of(layer.name));
                layerJson.Set("weight", JsonValue.Of(layer.defaultWeight));

                var stateMachine = layer.stateMachine;
                if (stateMachine == null)
                {
                    layerJson.Set("stateCount", JsonValue.Of(0));
                    layerJson.Set("note", JsonValue.Of("该层没有状态机"));
                    layersJson.Add(layerJson);
                    continue;
                }

                var defaultName = stateMachine.defaultState != null ? stateMachine.defaultState.name : string.Empty;
                layerJson.Set("defaultState", JsonValue.Of(defaultName));

                var statesJson = JsonValue.NewArray();
                var states = stateMachine.states;
                foreach (var child in states)
                {
                    var state = child.state;
                    if (state == null)
                    {
                        continue;
                    }

                    var stateJson = JsonValue.NewObject();
                    stateJson.Set("name", JsonValue.Of(state.name));
                    stateJson.Set("motion", JsonValue.Of(
                        state.motion != null ? AssetDatabase.GetAssetPath(state.motion) : string.Empty));
                    stateJson.Set("speed", JsonValue.Of(state.speed));
                    stateJson.Set("tag", JsonValue.Of(state.tag ?? string.Empty));
                    stateJson.Set("writeDefaultValues", JsonValue.Of(state.writeDefaultValues));
                    stateJson.Set("isDefault", JsonValue.Of(stateMachine.defaultState == state));

                    var transitions = state.transitions;
                    stateJson.Set("transitionCount", JsonValue.Of(transitions != null ? transitions.Length : 0));

                    if (includeTransitions && transitions != null && transitions.Length > 0)
                    {
                        var listJson = JsonValue.NewArray();
                        foreach (var transition in transitions)
                        {
                            totalTransitions++;
                            if (transitionBudget-- <= 0)
                            {
                                truncated = true;
                                break;
                            }

                            listJson.Add(AnimatorAssetUtil.TransitionToJson(transition));
                        }

                        stateJson.Set("transitions", listJson);
                    }

                    statesJson.Add(stateJson);
                }

                layerJson.Set("stateCount", JsonValue.Of(states.Length));
                layerJson.Set("states", statesJson);
                layerJson.Set("subStateMachineCount", JsonValue.Of(
                    stateMachine.stateMachines != null ? stateMachine.stateMachines.Length : 0));

                if (includeTransitions)
                {
                    var anyState = JsonValue.NewArray();
                    foreach (var transition in stateMachine.anyStateTransitions)
                    {
                        totalTransitions++;
                        if (transitionBudget-- <= 0)
                        {
                            truncated = true;
                            break;
                        }

                        anyState.Add(AnimatorAssetUtil.TransitionToJson(transition));
                    }

                    layerJson.Set("anyStateTransitions", anyState);

                    var entry = JsonValue.NewArray();
                    foreach (var transition in stateMachine.entryTransitions)
                    {
                        entry.Add(AnimatorAssetUtil.TransitionToJson(transition));
                    }

                    layerJson.Set("entryTransitions", entry);
                }

                layersJson.Add(layerJson);
            }

            data.Set("layerCount", JsonValue.Of(layers.Length));
            data.Set("layers", layersJson);
            data.Set("transitionCount", JsonValue.Of(totalTransitions));
            data.Set("truncated", JsonValue.Of(truncated));

            var summary = $"controller {assetPath}：{layers.Length} 层、{controller.parameters.Length} 个参数";
            if (truncated)
            {
                summary += $"（过渡超过 maxTransitions={maxTransitions} 已截断，调大再读）";
            }

            return CommandResult.Ok(summary, data);
        }
    }
}
#endif
