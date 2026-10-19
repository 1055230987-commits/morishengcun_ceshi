using UnityEngine;

namespace Game.Items
{
    /// <summary>
    /// 物品定义（数据资产）。木头、石头、食物、枪械、钥匙……全部用这个描述，不写死在代码里。
    ///
    /// 为什么用 ScriptableObject：
    ///   - 加新物品 = 在 Project 窗口右键新建一个资产，不需要改代码、不需要重新编译
    ///   - 存档只存 itemId + 数量，不存资产本身（资产是只读的"配置"，不是"状态"）
    ///
    /// 现在只做原型需要的字段；以后要加重量/耐久/使用效果时，
    /// 建议新增字段或另开一个 ItemBehaviour，而不是把逻辑塞进这个数据类。
    /// </summary>
    [CreateAssetMenu(fileName = "Item_", menuName = "游戏/物品/物品定义", order = 0)]
    public class ItemDefinition : ScriptableObject
    {
        [Header("标识（存档依赖，不要随意改）")]
        [Tooltip("稳定 ID，例如 wood / stone / food。存档只认这个字符串。")]
        [SerializeField] private string _itemId = "item";

        [SerializeField] private string _displayName = "未命名物品";
        [TextArea(2, 4)]
        [SerializeField] private string _description = "";

        [Header("堆叠")]
        [Tooltip("单个格子最多堆多少个。1 = 不可堆叠（枪械/钥匙这类）。")]
        [SerializeField, Min(1)] private int _maxStackSize = 99;

        [Header("显示（暂时可以留空）")]
        [SerializeField] private Sprite _icon;

        [Header("装备（普通物品留空）")]
        [Tooltip("只有对应类型的装备槽才能接收。新增装备类型可创建新的类型资产。")]
        [SerializeField] private EquipmentType _equipmentType;

        public string ItemId => _itemId;
        public string DisplayName => _displayName;
        public string Description => _description;
        public int MaxStackSize => _equipmentType != null ? 1 : Mathf.Max(1, _maxStackSize);
        public EquipmentType EquipmentType => _equipmentType;
        public Sprite Icon => _icon;

        /// <summary>编辑器/调试用：直接造一个内存里的临时物品定义，方便测试，不要用于正式内容。</summary>
        public static ItemDefinition CreateRuntime(string itemId, string displayName, int maxStackSize = 99,
            EquipmentType equipmentType = null)
        {
            ItemDefinition definition = CreateInstance<ItemDefinition>();
            definition.name = $"Item_{itemId}";
            definition._itemId = itemId;
            definition._displayName = displayName;
            definition._maxStackSize = Mathf.Max(1, maxStackSize);
            definition._equipmentType = equipmentType;
            return definition;
        }

        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(_itemId))
            {
                _itemId = name.ToLowerInvariant().Replace("item_", string.Empty);
            }

            _maxStackSize = Mathf.Max(1, _maxStackSize);
        }
    }
}
