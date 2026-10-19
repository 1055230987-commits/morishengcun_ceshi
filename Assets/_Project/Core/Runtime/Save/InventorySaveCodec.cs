#nullable enable
using System;
using System.Collections.Generic;
using Game.Items;

namespace Game.Save
{
    /// <summary>格子位置与装备 ID 的存档映射；预检不改动容器。</summary>
    public static class InventorySaveCodec
    {
        public static List<ItemSaveData> Capture(Inventory? inventory)
        {
            var result = new List<ItemSaveData>();
            if (inventory == null) return result;
            for (int i = 0; i < inventory.SlotsView.Count; i++)
            {
                ItemStack stack = inventory.GetSlot(i);
                if (stack.IsEmpty) continue;
                string slotId = inventory is EquipmentInventory equipment ? equipment.Definitions[i].SlotId : "";
                result.Add(new ItemSaveData(stack.Item.ItemId, stack.Amount, i, slotId));
            }
            return result;
        }

        public static bool TryDecode(Inventory inventory, List<ItemSaveData>? records, int version,
            Func<string, ItemDefinition?> resolve, out List<ItemStack> slots, out string error)
        {
            slots = new List<ItemStack>(); error = "";
            var occupied = new HashSet<int>();
            if (records == null) return true;
            int legacyIndex = 0;
            foreach (ItemSaveData record in records)
            {
                if (record == null) { error = "存在空物品记录"; return false; }
                ItemDefinition? item = resolve(record.ItemId);
                if (item == null) { error = "找不到物品：" + record.ItemId; return false; }
                int index = version >= 2 && record.SlotIndex >= 0 ? record.SlotIndex : legacyIndex++;
                if (inventory is EquipmentInventory equipment)
                    index = equipment.FindSlot(record.SlotId);
                var stack = new ItemStack(item, record.Amount);
                if (item == null || record.Amount <= 0 || !inventory.CanAccept(index, stack) || !occupied.Add(index))
                { error = "物品、格子、装备类型或数量无效：" + record.ItemId; return false; }
                while (slots.Count <= index) slots.Add(ItemStack.Empty);
                slots[index] = stack;
            }
            return true;
        }
    }
}
