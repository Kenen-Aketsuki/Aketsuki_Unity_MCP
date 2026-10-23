using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

# if UNITY_EDITOR
using UnityEditor;
# endif

namespace CivilRev.CommandSystem.Editor
{
    /// <summary>
    /// Addressables 注册指令（需求案 B7）。
    /// <para>
    /// 背景：运行期 <c>PrefabMgr</c> 走 Addressables 加载（地址 = 资产路径），但编辑器态走
    /// AssetDatabase 一切正常，未注册的 Prefab 只有进 Play 才抛 <c>InvalidKeyException</c>
    /// ——自动化流程必须在保存时就闭环，不能靠 Play 时炸出来发现。
    /// </para>
    /// <para>
    /// 关键架构约束：部分目标工程（如 island_back）没装 com.unity.addressables 包，
    /// ECS 若直接 <c>using UnityEditor.AddressableAssets</c>，包一导入那边就编译失败
    /// ——所以这里全部走<b>反射</b>，缺包时报友好错误而不是编译炸。
    /// </para>
    /// </summary>
    internal static class AddressablesBridge
    {
        private const string SettingsTypeName =
            "UnityEditor.AddressableAssets.Settings.AddressableAssetSettings";
        private const string SettingsDefaultObjectTypeName =
            "UnityEditor.AddressableAssets.Settings.AddressableAssetSettingsDefaultObject";

        private static Type _settingsType;
        private static bool _probed;

        /// <summary>探测 Addressables 包是否已装入本工程；没装直接给可操作的错误。</summary>
        private static void Probe()
        {
            if (_probed)
            {
                if (_settingsType == null)
                {
                    throw new CommandException("本工程未安装 Addressables（com.unity.addressables），无法注册条目");
                }

                return;
            }

            _probed = true;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(SettingsTypeName);
                if (type != null)
                {
                    _settingsType = type;
                    break;
                }
            }

            if (_settingsType == null)
            {
                throw new CommandException("本工程未安装 Addressables（com.unity.addressables），无法注册条目");
            }
        }

        /// <summary>
        /// 确保 <paramref name="assetPath"/> 已注册为 Addressable 条目。
        /// 地址缺省用资产路径本身（运行期 PrefabMgr 的约定）；已存在的条目只补地址/标签，不重建。
        /// 返回条目最终地址。 groupName 传 null 用工程的 Default Group。
        /// </summary>
        internal static string EnsureEntry(string assetPath, string address, string groupName, string[] labels,
            out bool created)
        {
            Probe();

            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid))
            {
                throw new CommandException($"'{assetPath}' 不是工程内的资产（路径要基于 Assets/）");
            }

            var settings = GetSettings();
            var entry = FindEntry(settings, guid);
            created = entry == null;

            if (entry == null)
            {
                var group = ResolveGroup(settings, groupName);
                entry = CreateEntry(settings, address ?? assetPath, guid, group);
            }
            else if (!string.IsNullOrEmpty(address))
            {
                // 已有条目且显式给了地址：更新地址（避免同名资产两地址并存造成歧义）
                var addressProperty = entry.GetType().GetProperty("address");
                var current = addressProperty?.GetValue(entry, null) as string;
                if (!string.Equals(current, address, StringComparison.Ordinal))
                {
                    addressProperty?.SetValue(entry, address, null);
                }
            }

            if (labels != null)
            {
                // 1.19.x：entry 上只有 SetLabel(label, enable, force, postEvent)（AddLabel 是 settings 的标签词表方法）
                var setLabel = entry.GetType().GetMethod("SetLabel",
                    new[] { typeof(string), typeof(bool), typeof(bool), typeof(bool) })
                    ?? entry.GetType().GetMethod("SetLabel", new[] { typeof(string), typeof(bool) });
                if (setLabel == null)
                {
                    throw new CommandException("Addressables 版本不兼容：AddressableAssetEntry.SetLabel 不可用");
                }

                var parameters = setLabel.GetParameters();
                foreach (var label in labels)
                {
                    if (string.IsNullOrEmpty(label))
                    {
                        continue;
                    }

                    var arguments = parameters.Length == 4
                        ? new object[] { label, true, false, true }
                        : new object[] { label, true };
                    setLabel.Invoke(entry, arguments);
                }
            }

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();

            var finalAddress = entry.GetType().GetProperty("address")?.GetValue(entry, null) as string;
            return finalAddress ?? assetPath;
        }

        /// <summary>移除条目（冒烟测试清理用）。返回是否确实删了东西。</summary>
        internal static bool RemoveEntry(string assetPath)
        {
            Probe();

            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid))
            {
                return false;
            }

            var settings = GetSettings();
            if (FindEntry(settings, guid) == null)
            {
                return false;
            }

            // 1.19.x 签名：RemoveAssetEntry(string guid, bool postEvent = true)
            var remove = _settingsType.GetMethod("RemoveAssetEntry",
                new[] { typeof(string), typeof(bool) })
                ?? _settingsType.GetMethod("RemoveAssetEntry", new[] { typeof(string) });
            if (remove == null)
            {
                throw new CommandException("Addressables 版本不兼容：RemoveAssetEntry 不可用");
            }

            var parameters = remove.GetParameters();
            var arguments = parameters.Length == 2
                ? new object[] { guid, true }
                : new object[] { guid };
            var removed = (bool)remove.Invoke(settings, arguments);
            if (removed)
            {
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
            }

            return removed;
        }

        /// <summary>列出工程现有的 Addressable 组名（缺包返回空，给报错提示用）。</summary>
        internal static List<string> ListGroups()
        {
            try
            {
                Probe();
            }
            catch (CommandException)
            {
                return new List<string>();
            }

            var names = new List<string>();
            var groups = _settingsType.GetProperty("groups")?.GetValue(GetSettings(), null) as IEnumerable<object>;
            if (groups != null)
            {
                foreach (var group in groups)
                {
                    var name = group?.GetType().GetProperty("name")?.GetValue(group, null) as string;
                    if (!string.IsNullOrEmpty(name))
                    {
                        names.Add(name);
                    }
                }
            }

            return names;
        }

        /// <summary>labels 参数：字符串（单个）或字符串数组；空值返回 null。</summary>
        internal static string[] ParseStringArray(JsonValue value)
        {
            if (value == null || value.IsNull)
            {
                return null;
            }

            if (value.IsArray)
            {
                var items = value.Items;
                var result = new List<string>();
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        var text = item.AsString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            result.Add(text);
                        }
                    }
                }

                return result.ToArray();
            }

            var single = value.AsString();
            return string.IsNullOrEmpty(single) ? null : new[] { single };
        }

        // ------------------------------------------------------------ 反射细节

        private static UnityEngine.Object GetSettings()
        {
            var defaultObjectType = FindType(SettingsDefaultObjectTypeName);
            var settings = defaultObjectType?.GetProperty("Settings", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null, null) as UnityEngine.Object;

            // Settings getter 依赖 EditorBuildSettings 的会话内注册（打开 Addressables 窗口等才激活），
            // 没激活时直接按 Addressables 标准布局加载 settings 资产兜底
            if (settings == null)
            {
                settings = AssetDatabase.LoadAssetAtPath(
                    "Assets/AddressableAssetsData/AddressableAssetSettings.asset", _settingsType);
            }

            if (settings == null)
            {
                throw new CommandException(
                    "找不到 AddressableAssetSettings（AddressableAssetSettingsDefaultObject.Settings 为空，且 Assets/AddressableAssetsData/AddressableAssetSettings.asset 不存在）——工程可能还没初始化 Addressables");
            }

            return settings;
        }

        private static object FindEntry(UnityEngine.Object settings, string guid)
        {
            var method = _settingsType.GetMethod("FindAssetEntry",
                new[] { typeof(string), typeof(bool) })
                ?? _settingsType.GetMethod("FindAssetEntry", new[] { typeof(string) });
            if (method == null)
            {
                throw new CommandException("Addressables 版本不兼容：FindAssetEntry 不可用");
            }

            return method.GetParameters().Length == 2
                ? method.Invoke(settings, new object[] { guid, false })
                : method.Invoke(settings, new object[] { guid });
        }

        private static object ResolveGroup(UnityEngine.Object settings, string groupName)
        {
            if (!string.IsNullOrEmpty(groupName))
            {
                var findGroup = _settingsType.GetMethod("FindGroup", new[] { typeof(string) });
                var group = findGroup?.Invoke(settings, new object[] { groupName });
                if (group == null)
                {
                    var available = string.Join("、", ListGroups());
                    throw new CommandException(
                        $"找不到 Addressable 组 '{groupName}'。现有组：{available}");
                }

                return group;
            }

            var defaultGroup = _settingsType.GetProperty("DefaultGroup")?.GetValue(settings, null);
            if (defaultGroup == null)
            {
                throw new CommandException("Addressables 的 DefaultGroup 为空，请指定 group 参数");
            }

            return defaultGroup;
        }

        private static object CreateEntry(UnityEngine.Object settings, string address, string guid, object group)
        {
            // 1.19.x 签名：CreateOrMoveEntry(string guid, AddressableAssetGroup targetParent, bool readOnly, bool postEvent)
            // address 不在该重载里，创建后单独设 entry.address
            var method = _settingsType.GetMethod("CreateOrMoveEntry",
                new[] { typeof(string), group.GetType(), typeof(bool), typeof(bool) });
            if (method == null)
            {
                throw new CommandException("Addressables 版本不兼容：CreateOrMoveEntry 不可用");
            }

            var entry = method.Invoke(settings, new[] { guid, group, false, true });
            if (entry == null)
            {
                throw new CommandException($"创建 Addressable 条目失败（guid={guid}）");
            }

            var addressProperty = entry.GetType().GetProperty("address");
            if (addressProperty == null || addressProperty.CanWrite == false)
            {
                throw new CommandException("Addressables 版本不兼容：AddressableAssetEntry.address 不可写");
            }

            addressProperty.SetValue(entry, address, null);
            return entry;
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }
    }

    /// <summary>独立注册指令：把任意资产登记为 Addressable 条目。</summary>
    [Command("addressables.addEntry",
        Description = "把资产注册为 Addressable 条目（地址缺省 = 资产路径，组缺省 Default Group；未装 Addressables 的工程会报错不炸编译）",
        Usage = "asset: string, address: string, group: string, labels: string[]",
        EditorOnly = true)]
    public sealed class AddAddressableEntryCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var assetPath = context.Args.RequireString("asset");
            if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandArgumentException(context.CommandId, "asset",
                    $"asset 要写 Assets/ 全路径（收到 '{assetPath}'）");
            }

            var address = context.Args.GetString("address", null);
            var group = context.Args.GetString("group", null);
            var labels = AddressablesBridge.ParseStringArray(context.Args.Get("labels"));

            var finalAddress = AddressablesBridge.EnsureEntry(assetPath, address, group, labels, out var isNew);
            var data = JsonValue.NewObject();
            data.Set("path", JsonValue.Of(assetPath));
            data.Set("address", JsonValue.Of(finalAddress));
            data.Set("created", JsonValue.Of(isNew));

            return CommandResult.Ok(
                (isNew ? "已注册 Addressable：" : "Addressable 条目已存在，已补地址/标签：") + assetPath,
                data);
        }
    }

    /// <summary>反向操作：移除 Addressable 条目（不动资产本身，只摘注册）。冒烟清理 / 彻底下线用。</summary>
    [Command("addressables.removeEntry",
        Description = "把资产从 Addressable 条目中移除（不影响资产文件本身；条目不存在时报错）",
        Usage = "asset: string",
        EditorOnly = true)]
    public sealed class RemoveAddressableEntryCommand : ICommand
    {
        public CommandResult Execute(CommandContext context)
        {
            var assetPath = context.Args.RequireString("asset");
            if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandArgumentException(context.CommandId, "asset",
                    $"asset 要写 Assets/ 全路径（收到 '{assetPath}'）");
            }

            var removed = AddressablesBridge.RemoveEntry(assetPath);
            if (!removed)
            {
                throw new CommandException($"'{assetPath}' 没有 Addressable 条目可移除");
            }

            return CommandResult.Ok($"已移除 Addressable 条目：{assetPath}", assetPath);
        }
    }
}
