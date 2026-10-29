using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Aketsuki.UnityMcp.Editor
{
    /// <summary>
    /// 把 Unity 项目之外的图片导入到 Assets 中，并按 UI 需求配置导入参数（Sprite 类型 / 九宫格 / 不压缩）
    /// <para>
    /// 参数：
    /// <list type="bullet">
    /// <item><c>source</c>（必填）文件或目录，支持绝对路径、<c>@ws/</c>、<c>~/</c>、相对默认指令目录</item>
    /// <item><c>destination</c>（默认 <c>Assets/GameAssets/UI/RoundMenu</c>）Assets 内的目标目录</item>
    /// <item><c>pattern</c>（默认 <c>*.png</c>）source 是目录时的匹配模式</item>
    /// <item><c>pixelsPerUnit</c>（默认 100）与 Canvas 的 referencePixelsPerUnit 一致时，nativeSize 就是像素尺寸</item>
    /// <item><c>border</c> 九宫格 [左,下,右,上]，用于会被拉伸的圆角底板</item>
    /// <item><c>filterMode</c>（默认 Bilinear）、<c>maxSize</c>（默认 2048）、<c>overwrite</c>（默认 false）</item>
    /// </list>
    /// </para>
    /// <para>导入后的资源路径会写入变量 <c>asset.importSprite</c>（数组），供后续指令引用。</para>
    /// </summary>
    [Command("asset.importSprite", Description = "把项目外的图片导入 Assets 并配置为 Sprite（支持整个目录）；Play 模式下阻断并提示",
        Usage = "source: string, destination: string, pattern: string, pixelsPerUnit: int, border: [l,b,r,t], overwrite: bool",
        EditorOnly = true)]
    public sealed class ImportSpriteCommand : ICommand
    {
        /// <summary>默认导入目录，和项目现有 UI 资源约定保持一致（Assets/GameAssets/UI/...）</summary>
        public const string DefaultDestination = "Assets/GameAssets/UI/RoundMenu";

        public CommandResult Execute(CommandContext context)
        {
            EditorPlayState.GuardNotPlaying();

            var source = context.Args.RequireString("source");
            var destination = context.Args.GetString("destination", DefaultDestination).Replace('\\', '/').TrimEnd('/');
            var pattern = context.Args.GetString("pattern", "*.png");
            var pixelsPerUnit = context.Args.GetFloat("pixelsPerUnit", 100f);
            var filterMode = context.Args.GetEnum("filterMode", FilterMode.Bilinear);
            var maxSize = context.Args.GetInt("maxSize", 2048);
            var overwrite = context.Args.GetBool("overwrite", false);

            var sourcePath = CommandPathUtil.Resolve(source);
            var isDirectory = Directory.Exists(sourcePath);
            if (!isDirectory && !File.Exists(sourcePath))
            {
                throw new CommandArgumentException(context.CommandId, "source",
                    $"源路径不存在：{sourcePath}");
            }

            // 收集待导入文件
            var files = new List<string>();
            if (isDirectory)
            {
                files.AddRange(Directory.GetFiles(sourcePath, pattern, SearchOption.TopDirectoryOnly));
                files.Sort(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                files.Add(sourcePath);
            }

            if (files.Count == 0)
            {
                throw new CommandArgumentException(context.CommandId, "source",
                    $"在 {sourcePath} 下没有匹配 '{pattern}' 的文件");
            }

            // 目标目录必须在 Assets 内
            var destinationAbsolute = CommandPathUtil.Resolve(destination);
            var assetsRoot = CommandPathUtil.AssetsRoot.Replace('\\', '/');
            var destinationNormalized = destinationAbsolute.Replace('\\', '/');
            if (!destinationNormalized.StartsWith(assetsRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandArgumentException(context.CommandId, "destination",
                    $"目标目录必须位于 Assets 内，当前解析为：{destinationNormalized}");
            }

            var assetFolder = "Assets/" + destinationNormalized.Substring(assetsRoot.Length + 1);

            if (context.DryRun)
            {
                return CommandResult.Skip($"dry-run：将把 {files.Count} 个文件导入 {assetFolder}");
            }

            Directory.CreateDirectory(destinationAbsolute);
            if (!AssetDatabase.IsValidFolder(assetFolder))
            {
                AssetDatabase.Refresh();
            }

            var border = ParseBorder(context.Args.Get("border"));
            var imported = new List<string>();
            var skipped = new List<string>();

            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                var fileName = Path.GetFileName(file);
                var targetAbsolute = Path.Combine(destinationAbsolute, fileName);
                var targetAssetPath = assetFolder + "/" + fileName;

                if (File.Exists(targetAbsolute) && !overwrite)
                {
                    skipped.Add(fileName);
                    imported.Add(targetAssetPath);
                    continue;
                }

                File.Copy(file, targetAbsolute, true);
                AssetDatabase.ImportAsset(targetAssetPath, ImportAssetOptions.ForceUpdate);

                var importer = AssetImporter.GetAtPath(targetAssetPath) as TextureImporter;
                if (importer == null)
                {
                    throw new CommandException(
                        $"文件已复制到 {targetAssetPath}，但拿不到 TextureImporter，导入可能失败");
                }

                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = pixelsPerUnit;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = filterMode;
                importer.sRGBTexture = true;
                importer.maxTextureSize = maxSize;
                // UI 切图用不压缩，避免在暗色底上出现块状色带
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.textureShape = TextureImporterShape.Texture2D;

                if (border.HasValue)
                {
                    importer.spriteBorder = border.Value;
                }

                importer.SaveAndReimport();
                imported.Add(targetAssetPath);
            }

            AssetDatabase.Refresh();

            context.SetVariable("asset.importSprite", imported.ToArray());
            context.SetVariable("asset.importedCount", imported.Count);

            var message = $"已导入 {imported.Count} 张切图到 {assetFolder}";
            if (skipped.Count > 0)
            {
                message += $"（其中 {skipped.Count} 张已存在被跳过，需要覆盖时加 overwrite: true）";
            }

            CommandLog.Info(message);
            return CommandResult.Ok(message, imported.ToArray());
        }

        private static Vector4? ParseBorder(JsonValue value)
        {
            if (value == null || value.IsNull)
            {
                return null;
            }

            if (value.IsArray && value.Count >= 4)
            {
                return new Vector4(
                    value[0].AsFloat(0f),
                    value[1].AsFloat(0f),
                    value[2].AsFloat(0f),
                    value[3].AsFloat(0f));
            }

            // 也接受 { left, bottom, right, top } 或单值 { all: 20 }
            if (value.IsObject)
            {
                if (value.ContainsKey("all"))
                {
                    var all = value.GetFloat("all", 0f);
                    return new Vector4(all, all, all, all);
                }

                return new Vector4(
                    value.GetFloat("left", 0f),
                    value.GetFloat("bottom", 0f),
                    value.GetFloat("right", 0f),
                    value.GetFloat("top", 0f));
            }

            return null;
        }
    }
}
