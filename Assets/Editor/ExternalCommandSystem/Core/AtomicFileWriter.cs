using System;
using System.IO;
using System.Text;

namespace CivilRev.CommandSystem
{
    /// <summary>
    /// 原子写文本文件：先写同目录临时文件，再原子替换目标。
    /// <para>
    /// 目的：写大文件时不会长时间占着半个文件、写一半崩了也不会留下半截内容。
    /// 临时文件必须与目标同目录（同卷），跨卷的 Move 不是原子操作。
    /// </para>
    /// <para>纯 .NET 实现，不依赖 UnityEditor，可离线测试。</para>
    /// </summary>
    public static class AtomicFileWriter
    {
        /// <summary>把文本原子写入目标路径；目标不存在则直接创建</summary>
        public static void WriteAllText(string path, string content, Encoding encoding = null)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("path 不能为空", nameof(path));
            }

            encoding = encoding ?? new UTF8Encoding(false);

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 临时文件名带"目标名 + 随机串"，与目标同目录同扩展名，避免被误认成资源文件
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, content ?? string.Empty, encoding);

                if (File.Exists(path))
                {
                    try
                    {
                        // 首选 Replace：保留目标文件的 ACL，且是原子替换
                        File.Replace(temp, path, null);
                    }
                    catch (Exception)
                    {
                        // 某些文件系统（部分网络盘 / 只读挂载）不支持 Replace，退化为"删 + 移"
                        File.Delete(path);
                        File.Move(temp, path);
                    }
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            finally
            {
                // 成功时 temp 已被移走；失败时清掉，避免目录里留垃圾
                if (File.Exists(temp))
                {
                    try
                    {
                        File.Delete(temp);
                    }
                    catch (Exception)
                    {
                        // 删不掉不影响主流程
                    }
                }
            }
        }
    }
}
