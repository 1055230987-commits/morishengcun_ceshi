using UnityEngine;

namespace Game.Items
{
    [CreateAssetMenu(fileName = "EquipmentType", menuName = "游戏/物品/装备类型", order = 1)]
    public sealed class EquipmentType : ScriptableObject
    {
        [SerializeField] private string _typeId;
        [SerializeField] private string _displayName;
        public string TypeId => _typeId;
        public string DisplayName => _displayName;

        public static EquipmentType CreateRuntime(string id, string label)
        {
            var type = CreateInstance<EquipmentType>();
            type._typeId = id; type._displayName = label;
            return type;
        }
    }
}
