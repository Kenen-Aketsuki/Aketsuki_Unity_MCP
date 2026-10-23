using UnityEngine;

namespace CivilRev.CommandSystem.Editor
{
    /// <summary>
    /// UI 资源加载工具：把"路径字符串 → Sprite / Font / TMP 字体"这段逻辑收在一处。
    /// <para>
    /// 关键设计：AssetDatabase 调用用 <c>#if UNITY_EDITOR</c> 包在方法体内部，而不是包住整个方法。
    /// 这样两件事同时成立：
    /// <list type="bullet">
    /// <item>带 UNITY_EDITOR 的离线编译工程能真正编译并检查 AssetDatabase 那几行；</item>
    /// <item>不带 UNITY_EDITOR 的工程（引用 UnityEngine.UI 的那个）也能编译到调用方，
    /// 于是"给 Image.sprite / TMP_Text.font 赋值"这类代码同样参与编译检查。</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static class UiAssetUtil
    {
        /// <summary>把各种写法归一成 <c>Assets/xxx</c></summary>
        public static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            var resolved = CommandPathUtil.Resolve(path).Replace('\\', '/');
            var assets = CommandPathUtil.AssetsRoot.Replace('\\', '/');

            if (resolved.StartsWith(assets + "/", System.StringComparison.OrdinalIgnoreCase))
            {
                return "Assets/" + resolved.Substring(assets.Length + 1);
            }

            return resolved;
        }

        /// <summary>
        /// 按 Assets 路径加载任意资源，失败时报出"是哪种失败"
        /// </summary>
        public static T LoadAsset<T>(string path, string commandId, string paramName) where T : Object
        {
#if UNITY_EDITOR
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var assetPath = NormalizeAssetPath(path);

            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<T>(assetPath);
            if (asset != null)
            {
                return asset;
            }

            // 区分"文件不存在"和"存在但类型不对"，后者是最常见的坑
            var actualType = UnityEditor.AssetDatabase.GetMainAssetTypeAtPath(assetPath);
            if (actualType == null)
            {
                throw new CommandArgumentException(commandId, paramName,
                    $"找不到资源：{assetPath}（项目外的图片可先用 asset.importSprite 导入 Assets）");
            }

            if (typeof(T) == typeof(Sprite) && actualType == typeof(Texture2D))
            {
                throw new CommandArgumentException(commandId, paramName,
                    $"资源 {assetPath} 当前的导入类型是 Texture，不是 Sprite。" +
                    "请用 asset.importSprite 重新导入，或在 Inspector 中把 Texture Type 改为 Sprite (2D and UI)。");
            }

            throw new CommandArgumentException(commandId, paramName,
                $"资源 {assetPath} 的类型是 {actualType.Name}，不是 {typeof(T).Name}");
#else
            return null;
#endif
        }

        /// <summary>按 Assets 路径加载 Sprite</summary>
        public static Sprite LoadSprite(string path, string commandId, string paramName)
        {
            return LoadAsset<Sprite>(path, commandId, paramName);
        }

        /// <summary>按 Assets 路径加载字体（.ttf/.otf）</summary>
        public static Font LoadFont(string path, string commandId, string paramName)
        {
            return LoadAsset<Font>(path, commandId, paramName);
        }

        /// <summary>在指定目录下按文件名查找资源路径（用于"只记得名字"的默认值）</summary>
        public static string FindAssetPathByName(string fileName, string searchFolder)
        {
#if UNITY_EDITOR
            if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(searchFolder))
            {
                return null;
            }

            if (!UnityEditor.AssetDatabase.IsValidFolder(searchFolder))
            {
                return null;
            }

            // 先按"目录 + 文件名"直接拼路径试一次。
            // AssetDatabase.FindAssets 的过滤串对带空格、带扩展名的名字（如
            // "SourceHanSerifCN-Regular SDF.asset"）匹配并不稳定 —— 这正是之前默认字体
            // 解析失败、悄悄退回 TMP 全局默认（LiberationSans）的原因。
            var direct = searchFolder.TrimEnd('/') + "/" + fileName;
            if (UnityEditor.AssetDatabase.LoadAssetAtPath<Object>(direct) != null)
            {
                return direct;
            }

            // 退而用不带扩展名的名字做搜索，再按文件名比对
            var bare = System.IO.Path.GetFileNameWithoutExtension(fileName);
            var guids = UnityEditor.AssetDatabase.FindAssets(bare, new[] { searchFolder });
            for (var i = 0; i < guids.Length; i++)
            {
                var candidate = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]);
                if (System.IO.Path.GetFileName(candidate) == fileName
                    || System.IO.Path.GetFileNameWithoutExtension(candidate) == bare)
                {
                    return candidate;
                }
            }

            return null;
#else
            return null;
#endif
        }
    }
}
