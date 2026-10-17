// 本文件开启可空引用类型分析（和 Health.cs / GridConnectivity.cs 一致）。
#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Game.Save
{
    /// <summary>
    /// 存档文件的读写。
    ///
    /// 【这一层只负责"数据 ↔ 文件"】不碰场景对象、不碰建造系统。
    /// 采集与还原场景的工作在 <see cref="Game.Gameplay.Building.GameSaveController"/> 里 ——
    /// 分层的理由：读写文件可以独立测试（路径、编码、损坏处理），
    /// 而"把数据摆回场景"必须在 Unity 里验证。
    ///
    /// 【为什么用自己的极简 JSON 而不是 JsonUtility】
    /// JsonUtility 有几条限制会咬人：
    ///   - 不支持 Dictionary
    ///   - 不支持顶层是数组
    ///   - 不支持 null 的引用字段（会变成空对象）
    /// 但它是 Unity 内置的、不需要第三方库，而且我们的数据结构很扁（全是
    /// 基本类型 + List），刚好在它的能力范围内。
    /// 所以：用 JsonUtility，但把数据结构刻意设计成"它支持的样子"。
    ///
    /// 【为什么不用 Newtonsoft】不想为了存一个存档引入第三方依赖
    /// （本项目至今没有第三方库，保持这一点有价值）。
    /// </summary>
    public static class SaveSerializer
    {
        /// <summary>存档文件名。</summary>
        public const string DefaultFileName = "save_slot0.json";

        /// <summary>存档目录名（相对 Application.persistentDataPath）。</summary>
        public const string SaveFolderName = "Saves";

        /// <summary>存档文件的完整路径。</summary>
        public static string GetSavePath(string fileName = DefaultFileName)
        {
            return Path.Combine(GetSaveDirectory(), fileName);
        }

        /// <summary>存档目录。打包后是 persistentDataPath（可写）。</summary>
        public static string GetSaveDirectory()
        {
            return Path.Combine(Application.persistentDataPath, SaveFolderName);
        }

        /// <summary>
        /// 把存档写进文件。返回是否成功。
        ///
        /// 【为什么要"先写临时文件再替换"】直接覆盖原文件的话，
        /// 写到一半崩溃/断电会留下一个【半个 JSON】—— 那份存档就彻底废了，
        /// 而玩家原来那份好的也没了。先写 .tmp 再原子替换，最坏情况只是这次没存上。
        /// </summary>
        public static bool TryWrite(GameSaveData data, out string message, string fileName = DefaultFileName)
        {
            message = string.Empty;

            if (data == null)
            {
                message = "存档数据为空";
                return false;
            }

            try
            {
                string directory = GetSaveDirectory();
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

                string json = JsonUtility.ToJson(data, prettyPrint: true);
                string finalPath = GetSavePath(fileName);
                string tempPath = finalPath + ".tmp";

                // 用 UTF-8 无 BOM：带 BOM 的话某些文本编辑器/工具读起来会带一个不可见字符
                File.WriteAllText(tempPath, json, new UTF8Encoding(false));

                // File.Replace 要求目标存在；不存在时直接用 Move
                if (File.Exists(finalPath))
                {
                    File.Replace(tempPath, finalPath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tempPath, finalPath);
                }

                message = $"已保存到 {finalPath}";
                return true;
            }
            catch (Exception exception)
            {
                message = $"保存失败：{exception.GetType().Name} {exception.Message}";
                return false;
            }
        }

        /// <summary>
        /// 从文件读存档。返回 null 表示没有存档或读取失败（原因在 message 里）。
        /// </summary>
        public static GameSaveData? TryRead(out string message, string fileName = DefaultFileName)
        {
            message = string.Empty;

            try
            {
                string path = GetSavePath(fileName);

                if (!File.Exists(path))
                {
                    message = "没有找到存档文件";
                    return null;
                }

                string json = File.ReadAllText(path, Encoding.UTF8);

                if (string.IsNullOrWhiteSpace(json))
                {
                    message = "存档文件是空的";
                    return null;
                }

                GameSaveData? data = JsonUtility.FromJson<GameSaveData>(json);

                if (data == null)
                {
                    message = "存档内容无法解析（可能已损坏）";
                    return null;
                }

                if (data.Version != GameSaveData.CurrentVersion)
                {
                    // 【不直接拒绝】版本不同时先警告再尝试加载 ——
                    // 以后加了新字段，旧存档通常还能读（缺的字段是默认值）。
                    message = $"存档版本是 {data.Version}，当前是 {GameSaveData.CurrentVersion}，会尽力加载";
                }
                else
                {
                    message = "读取成功";
                }

                // JsonUtility 对 null 的 List 字段会给 null，而不是空列表。
                // 不补这一手的话，后面遍历会 NullReferenceException。
                data.Backpack ??= new List<ItemSaveData>();
                data.Pocket ??= new List<ItemSaveData>();
                data.PlacedParts ??= new List<PlacedPartSaveData>();
                data.Player ??= new PlayerSaveData();

                return data;
            }
            catch (Exception exception)
            {
                message = $"读取失败：{exception.GetType().Name} {exception.Message}";
                return null;
            }
        }

        /// <summary>存档是否存在。</summary>
        public static bool Exists(string fileName = DefaultFileName)
        {
            return File.Exists(GetSavePath(fileName));
        }

        /// <summary>删除存档。返回是否真的删掉了。</summary>
        public static bool TryDelete(out string message, string fileName = DefaultFileName)
        {
            message = string.Empty;

            try
            {
                string path = GetSavePath(fileName);
                if (!File.Exists(path))
                {
                    message = "没有存档可删";
                    return false;
                }

                File.Delete(path);
                message = "存档已删除";
                return true;
            }
            catch (Exception exception)
            {
                message = $"删除失败：{exception.GetType().Name} {exception.Message}";
                return false;
            }
        }
    }
}
