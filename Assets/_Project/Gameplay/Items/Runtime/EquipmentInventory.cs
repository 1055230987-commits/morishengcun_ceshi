using System;
using System.Collections.Generic;

namespace Game.Items
{
    [Serializable]
    public class EquipmentSlot
    {
        public string SlotId;
        public string DisplayName;
        public EquipmentType AcceptedType;
        public EquipmentSlot(string id, string label, EquipmentType type)
        { SlotId = id; DisplayName = label; AcceptedType = type; }
    }

    /// <summary>装备槽规则由配置决定；新增类型或同类型的多个槽位无需改搬运逻辑。</summary>
    [Serializable]
    public sealed class EquipmentInventory : Inventory
    {
        [UnityEngine.SerializeField] private List<EquipmentSlot> _definitions;
        public IReadOnlyList<EquipmentSlot> Definitions => _definitions;

        public EquipmentInventory(IEnumerable<EquipmentSlot> definitions) : base(1)
        {
            _definitions = new List<EquipmentSlot>(definitions);
            var ids = new HashSet<string>();
            foreach (EquipmentSlot slot in _definitions)
                if (slot == null || string.IsNullOrWhiteSpace(slot.SlotId) || !ids.Add(slot.SlotId))
                    throw new ArgumentException("装备槽 ID 必须非空且唯一。");
            if (_definitions.Count == 0) throw new ArgumentException("至少配置一个装备槽。");
            Capacity = _definitions.Count;
        }

        public int FindSlot(string id) => _definitions.FindIndex(slot => slot.SlotId == id);

        public override bool CanAccept(int index, ItemStack stack)
        {
            if (!base.CanAccept(index, stack) || index >= _definitions.Count) return false;
            if (stack.IsEmpty) return true;
            EquipmentType required = _definitions[index].AcceptedType;
            return stack.Amount == 1 && required != null && stack.Item.EquipmentType != null &&
                !string.IsNullOrEmpty(required.TypeId) && required.TypeId == stack.Item.EquipmentType.TypeId;
        }
    }
}
