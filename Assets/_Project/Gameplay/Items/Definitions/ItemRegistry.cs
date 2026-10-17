using System.Collections.Generic;
using UnityEngine;

namespace Game.Items
{
    /// <summary>
    /// 物品注册表：把"物品 ID → 资产"的查找变成一次查表。
    ///
    /// 【为什么必须有它 —— 这不是优化，是打包后的功能缺口】
    /// 编辑器里可以靠 <c>AssetDatabase.FindAssets("t:ItemDefinition")</c> 按 ID 找物品，
    /// 但**打包后 AssetDatabase 不存在**。于是打包后的读档会出现：
    ///   背包被清空 → 每个物品都"找不到定义" → 全部跳过 → 玩家丢光背包。
    ///
    /// 在注册表出现之前，代码的做法是"检测到平台不支持查物品就直接拒绝加载"
    /// （不丢数据，但含物品的存档根本读不了）。
    /// 有了注册表之后，打包后也能正常恢复物品。
    ///
    /// 【为什么用 Resources 加载】本工程没有引入 Addressables（那是另一个第三方依赖）。
    /// Resources 是 Unity 内置的，一个注册表资产的开销完全可以接受。
    /// 资源目录：Assets/_Project/Resources/ItemRegistry.asset
    ///
    /// 【怎么维护】菜单 <c>游戏 → 物品 → 重建物品注册表</c> 会扫描所有 ItemDefinition
    /// 资产并写进去。**加了新物品之后要重新跑一次**，否则打包后那个物品读不出来。
    /// （编辑器里不依赖注册表 —— 它直接查 AssetDatabase，所以忘了重建也不会影响编辑器测试。）
    /// </summary>
    [CreateAssetMenu(fileName = "ItemRegistry", menuName = "游戏/物品/物品注册表", order = 20)]
    public class ItemRegistry : ScriptableObject
    {
        /// <summary>Resources 下的固定路径（不带扩展名）。</summary>
        public const string ResourcePath = "ItemRegistry";

        [Tooltip("所有物品定义。由「游戏 → 物品 → 重建物品注册表」自动填充。")]
        [SerializeField] private List<ItemDefinition> _items = new List<ItemDefinition>();

        // ---- 运行时缓存 ----
        //
        // 【为什么懒加载而不是在 Awake 建】ScriptableObject 的 OnEnable 时机不确定，
        // 而且这不是 MonoBehaviour。用属性访问 + 惰性构建最稳。
        private Dictionary<string, ItemDefinition> _byId;

        /// <summary>所有已注册的物品（只读）。</summary>
        public IReadOnlyList<ItemDefinition> Items => _items;

        /// <summary>按 ID 查物品定义。找不到返回 null。</summary>
        public ItemDefinition Find(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return null;

            EnsureIndex();

            return _byId.TryGetValue(itemId, out ItemDefinition found) ? found : null;
        }

        private void EnsureIndex()
        {
            if (_byId != null) return;

            _byId = new Dictionary<string, ItemDefinition>(_items.Count);

            foreach (ItemDefinition item in _items)
            {
                if (item == null) continue;

                string id = item.ItemId;
                if (string.IsNullOrEmpty(id)) continue;

                // 【重复 ID 只报不改】两个资产用同一个 ID 是配置错误，
                // 静默取一个会让"为什么读出来是另一个物品"变成难查的问题。
                if (_byId.ContainsKey(id))
                {
                    Debug.LogError(
                        $"[物品注册表] 有两个物品的 ID 都是「{id}」：" +
                        $"「{_byId[id].name}」和「{item.name}」。后一个被忽略 —— 请改掉其中一个的 ID。",
                        this);
                    continue;
                }

                _byId[id] = item;
            }
        }

        /// <summary>
        /// 【编辑器用】从一份物品列表重建索引（资产内容被工具改过之后调用）。
        /// 运行时不需要 —— 资产是只读的，索引建一次就够。
        /// </summary>
        public void RebuildIndexForEditor()
        {
            _byId = null;
            EnsureIndex();
        }

        /// <summary>
        /// 加载注册表。找不到返回 null（此时调用方应该拒绝"含物品的读档"）。
        ///
        /// 结果会缓存 —— Resources.Load 每次调用都有开销，而注册表只有一个。
        /// </summary>
        private static ItemRegistry _cached;
        private static bool _attempted;

        public static ItemRegistry LoadOrNull()
        {
            if (_attempted) return _cached;

            _attempted = true;
            _cached = Resources.Load<ItemRegistry>(ResourcePath);

            if (_cached == null)
            {
                Debug.LogWarning(
                    $"[物品注册表] 在 Resources/{ResourcePath} 找不到注册表。" +
                    "打包后需要它才能按 ID 恢复物品。请跑「游戏 → 物品 → 重建物品注册表」。");
            }

            return _cached;
        }

        /// <summary>清掉静态缓存（编辑器工具重建资产后要调用，否则会拿到旧实例）。</summary>
        public static void ClearCache()
        {
            _cached = null;
            _attempted = false;
        }
    }
}
