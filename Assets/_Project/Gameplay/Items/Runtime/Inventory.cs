using System;
using System.Collections.Generic;

namespace Game.Items
{
    /// <summary>保留空格的容器。格子索引稳定；所有搬运先完整校验，再一次提交。</summary>
    [Serializable]
    public class Inventory
    {
        [UnityEngine.SerializeField] private List<ItemStack> _slots;
        [UnityEngine.SerializeField] private int _capacity;
        private List<ItemStack> Slots => _slots ?? (_slots = new List<ItemStack>());
        public Inventory(int capacity = 0) { _capacity = Math.Max(0, capacity); }
        public event Action Changed;
        public int Capacity { get => _capacity; set => _capacity = Math.Max(0, value); }
        public IReadOnlyList<ItemStack> SlotsView => Slots;
        /// <summary>占用的格子数，不包含保留的空位。遍历索引应使用 SlotsView.Count。</summary>
        public int SlotCount
        {
            get { int count = 0; foreach (ItemStack slot in Slots) if (!slot.IsEmpty) count++; return count; }
        }
        public bool IsFull => _capacity > 0 && SlotCount >= _capacity;
        public bool IsEmpty => SlotCount == 0;
        public ItemStack GetSlot(int index) => index >= 0 && index < Slots.Count ? Slots[index] : ItemStack.Empty;

        public virtual bool CanAccept(int index, ItemStack stack) =>
            index >= 0 && (_capacity == 0 || index < _capacity) &&
            (stack.IsEmpty || stack.Amount <= stack.Item.MaxStackSize);

        public bool SetSlot(int index, ItemStack stack)
        {
            if (!CanAccept(index, stack)) return false;
            Write(index, stack);
            Changed?.Invoke();
            return true;
        }

        private void Write(int index, ItemStack stack)
        {
            if (stack.IsEmpty && index >= Slots.Count) return;
            while (Slots.Count <= index) Slots.Add(ItemStack.Empty);
            Slots[index] = stack.IsEmpty ? ItemStack.Empty : stack;
            // 只裁剪尾部空位，绝不移动任何仍被占用的格子。
            while (Slots.Count > 0 && Slots[Slots.Count - 1].IsEmpty) Slots.RemoveAt(Slots.Count - 1);
        }

        public int Add(ItemDefinition item, int amount)
        {
            if (item == null || amount <= 0) return 0;
            int remaining = amount;
            for (int i = 0; i < Slots.Count && remaining > 0; i++)
            {
                ItemStack slot = Slots[i];
                if (slot.IsEmpty || slot.Item != item) continue;
                int moved = Math.Min(Math.Max(0, item.MaxStackSize - slot.Amount), remaining);
                ItemStack updated = new ItemStack(item, slot.Amount + moved);
                if (moved == 0 || !CanAccept(i, updated)) continue;
                Slots[i] = updated; remaining -= moved;
            }
            int limit = _capacity > 0 ? _capacity : Slots.Count + remaining;
            for (int i = 0; i < limit && remaining > 0; i++)
            {
                if (!GetSlot(i).IsEmpty) continue;
                int moved = Math.Min(item.MaxStackSize, remaining);
                var stack = new ItemStack(item, moved);
                if (!CanAccept(i, stack)) continue;
                Write(i, stack); remaining -= moved;
            }
            if (remaining != amount) Changed?.Invoke();
            return amount - remaining;
        }

        public bool RemoveSlotAt(int index)
        {
            if (GetSlot(index).IsEmpty) return false;
            Write(index, ItemStack.Empty); Changed?.Invoke(); return true;
        }

        public bool Remove(ItemDefinition item, int amount)
        {
            if (item == null || amount <= 0 || CountOf(item) < amount) return false;
            int remaining = amount;
            for (int i = Slots.Count - 1; i >= 0 && remaining > 0; i--)
            {
                ItemStack slot = GetSlot(i);
                if (slot.IsEmpty || slot.Item != item) continue;
                int taken = Math.Min(slot.Amount, remaining);
                slot.Amount -= taken; remaining -= taken; Write(i, slot);
            }
            Changed?.Invoke(); return true;
        }

        public int CountOf(ItemDefinition item)
        {
            if (item == null) return 0;
            int total = 0; foreach (ItemStack slot in Slots)
                if (!slot.IsEmpty && slot.Item == item) total += slot.Amount;
            return total;
        }

        public int CountOf(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return 0;
            int total = 0; foreach (ItemStack slot in Slots)
                if (!slot.IsEmpty && slot.Item.ItemId == itemId) total += slot.Amount;
            return total;
        }

        public void Clear()
        {
            if (Slots.Count == 0) return;
            Slots.Clear(); Changed?.Invoke();
        }

        public void RestoreSlot(ItemStack stack)
        {
            if (stack.IsEmpty) return;
            int index = 0;
            while (!GetSlot(index).IsEmpty) index++;
            if (!SetSlot(index, stack)) throw new ArgumentException("恢复物品超出容量或不符合格子规则。");
        }

        public bool ReplaceSlots(IReadOnlyList<ItemStack> slots)
        {
            if (slots == null) return false;
            for (int i = 0; i < slots.Count; i++)
                if (!slots[i].IsEmpty && !CanAccept(i, slots[i])) return false;
            var replacement = new List<ItemStack>(slots);
            while (replacement.Count > 0 && replacement[replacement.Count - 1].IsEmpty)
                replacement.RemoveAt(replacement.Count - 1);
            _slots = replacement; Changed?.Invoke(); return true;
        }

        public bool MoveWithin(int fromIndex, int toIndex) => MoveBetween(this, fromIndex, this, toIndex);

        public static bool CanMoveBetween(Inventory from, int fromIndex, Inventory to, int toIndex) =>
            PlanMove(from, fromIndex, to, toIndex, out _, out _);

        private static bool PlanMove(Inventory from, int fromIndex, Inventory to, int toIndex,
            out ItemStack sourceAfter, out ItemStack targetAfter)
        {
            sourceAfter = targetAfter = ItemStack.Empty;
            if (from == null || to == null || (from == to && fromIndex == toIndex)) return false;
            ItemStack source = from.GetSlot(fromIndex), target = to.GetSlot(toIndex);
            if (source.IsEmpty || !from.CanAccept(fromIndex, ItemStack.Empty) ||
                !to.CanAccept(toIndex, ItemStack.Empty)) return false;
            if (!target.IsEmpty && target.Item == source.Item)
            {
                int moved = Math.Min(Math.Max(0, source.Item.MaxStackSize - target.Amount), source.Amount);
                if (moved == 0) return false;
                sourceAfter = new ItemStack(source.Item, source.Amount - moved);
                targetAfter = new ItemStack(target.Item, target.Amount + moved);
            }
            else { sourceAfter = target; targetAfter = source; }
            return from.CanAccept(fromIndex, sourceAfter) && to.CanAccept(toIndex, targetAfter);
        }

        public static bool MoveBetween(Inventory from, int fromIndex, Inventory to, int toIndex)
        {
            if (!PlanMove(from, fromIndex, to, toIndex, out ItemStack source, out ItemStack target)) return false;
            from.Write(fromIndex, source); to.Write(toIndex, target);
            from.Changed?.Invoke();
            if (to != from) to.Changed?.Invoke();
            return true;
        }

        public string Describe()
        {
            var parts = new List<string>();
            foreach (ItemStack slot in Slots)
                if (!slot.IsEmpty) parts.Add(slot.Item.DisplayName + " ×" + slot.Amount);
            return parts.Count == 0 ? "（空）" : string.Join("，", parts);
        }
    }
}
