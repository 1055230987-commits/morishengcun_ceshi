#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// 编辑器工具共享的文件/目录辅助方法。
    ///
    /// 存在的理由：AssetDatabase.CreateFolder 新建的目录**不会立刻**对
    /// AssetDatabase.CreateAsset / MoveAsset 可见，必须在中间插一次
    /// AssetDatabase.Refresh()，否则会报：
    ///   - "Creating asset at path ... failed"
    ///   - "Trying to move asset as a sub directory of a directory that does not exist"
    ///
    /// 这个坑在本工程里踩过三次（场景移动、物品创建、天空材质），
    /// 所以统一收敛到这一个方法，不允许各处再自己写目录创建。
    /// </summary>
    public static class EditorFileUtility
    {
        /// <summary>
        /// 确保目录存在并【已登记到 AssetDatabase】，之后才能安全地 CreateAsset / MoveAsset。
        /// 返回 false 表示目录最终仍不可用（调用方应当放弃并报错，而不是硬着头皮继续）。
        /// </summary>
        public static bool EnsureAssetFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return false;

            folderPath = folderPath.Replace('\\', '/').TrimEnd('/');

            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                CreateFolderRecursive(folderPath);
                AssetDatabase.Refresh();

                if (!AssetDatabase.IsValidFolder(folderPath))
                {
                    // 目录仍然不可用：可能是路径非法或磁盘问题，交给调用方处理。
                    return false;
                }
            }

            // 目录已存在（或者只有一层），补一次刷新是廉价的保险。
            // 注意：即使 IsValidFolder 返回 true，CreateAsset 仍可能因为"刚建好还没登记"而失败，
            // 所以这里不能提前 return。
            AssetDatabase.Refresh();
            return true;
        }

        /// <summary>确保某个【资产文件路径】所在目录存在并已登记。</summary>
        public static bool EnsureAssetFolderForFile(string assetFilePath)
        {
            string directory = System.IO.Path.GetDirectoryName(assetFilePath);
            if (string.IsNullOrEmpty(directory)) return false;

            return EnsureAssetFolder(directory);
        }

        private static void CreateFolderRecursive(string folderPath)
        {
            string[] parts = folderPath.Split('/');
            string current = parts[0];

            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }
    }
}
#endif
