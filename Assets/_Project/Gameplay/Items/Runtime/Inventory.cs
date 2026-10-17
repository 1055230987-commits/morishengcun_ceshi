using System;
using System.Collections.Generic;

namespace Game.Items
{
    /// <summary>
    /// 背包数据（纯 C#，不是 MonoBehaviour）。
    ///
    /// 刻意做成普通类：
    ///   - 以后 AI、箱子、商店、联机同步都能各持有一个，不需要挂在场景物体上
    ///   - 存档时直接序列化 _slots 数组即可，不依赖任何场景对象
    ///
    /// 当前只做原型需要的：加、减、查、事件通知，以及界面的拖拽移动。
    /// 装备槽/排序等留到以后。
    /// </summary>
    [Serializable]
    public class Inventory
    {
        /// <summary>
        /// 格子列表。
        ///
        /// 【为什么不用字段初始化 + readonly】
        /// Inventory 是 [Serializable]，又被 [SerializeField] 引用 ——
        /// Unity 反序列化它时【不会调用构造函数】，而字段初始化器是编译进构造函数的。
        /// 所以 Unity 造出来的实例里这个列表会是 null，
        /// 读取时就会抛 NullReferenceException（本工程实际踩到过：SlotCount 报空引用）。
        ///
        /// 改成"私有字段 + 属性惰性初始化"后，无论构造、反序列化还是域重载，
        /// 任何一次访问都能拿到一个可用的列表。所以这个字段不能加 readonly。
        /// </summary>
        [UnityEngine.SerializeField] private List<ItemStack> _slots;

        private List<ItemStack> Slots
        {
            get
            {
                if (_slots == null) _slots = new List<ItemStack>();
                return _slots;
            }
        }

        /// <summary>
        /// 格子数量上限。
        /// 0 = 不限（原型早期行为：能无限开新格）。
        /// 背包 16、口袋 4 都靠这个字段实现，逻辑本身不需要区分"是背包还是口袋"。
        /// </summary>
        [UnityEngine.SerializeField] private int _capacity;

        /// <summary>用容量构造（背包 16 / 口袋 4）。</summary>
        public Inventory(int capacity = 0)
        {
            _capacity = capacity < 0 ? 0 : capacity;
        }

        /// <summary>背包内容变化时触发（UI 订阅它刷新，不需要每帧轮询）。</summary>
        public event Action Changed;

        /// <summary>格子数量上限。0 = 不限。</summary>
        public int Capacity
        {
            get => _capacity;
            set => _capacity = value < 0 ? 0 : value;
        }

        /// <summary>已占用的格子数。</summary>
        public int SlotCount => Slots.Count;

        /// <summary>是否已经装满。</summary>
        public bool IsFull => _capacity > 0 && Slots.Count >= _capacity;

        public IReadOnlyList<ItemStack> SlotsView => Slots;

        /// <summary>
        /// 放入物品。会自动填满已有同类格子、再开新格。
        /// 返回【实际放入的数量】—— 放不下时，这个返回值会小于请求数量（溢出部分被拒）。
        /// </summary>
        public int Add(ItemDefinition item, int amount)
        {
            if (item == null || amount <= 0) return 0;

            int remaining = amount;

            // 先填已有格子
            for (int i = 0; i < Slots.Count && remaining > 0; i++)
            {
                ItemStack slot = Slots[i];
                if (slot.Item != item) continue;

                int space = item.MaxStackSize - slot.Amount;
                if (space <= 0) continue;

                int moved = Math.Min(space, remaining);
                slot.Amount += moved;
                Slots[i] = slot;
                remaining -= moved;
            }

            // 再开新格（受容量限制）
            while (remaining > 0)
            {
                if (_capacity > 0 && Slots.Count >= _capacity) break;

                int moved = Math.Min(item.MaxStackSize, remaining);
                Slots.Add(new ItemStack(item, moved));
                remaining -= moved;
            }

            int added = amount - remaining;
            if (added > 0) Changed?.Invoke();
            return added;
        }

        /// <summary>取某个格子的内容（越界返回空）。UI 用。</summary>
        public ItemStack GetSlot(int index)
        {
            if (index < 0 || index >= Slots.Count) return ItemStack.Empty;
            return Slots[index];
        }

        /// <summary>整格移除（UI 拖拽/丢弃用）。</summary>
        public bool RemoveSlotAt(int index)
        {
            if (index < 0 || index >= Slots.Count) return false;

            Slots.RemoveAt(index);
            Changed?.Invoke();
            return true;
        }

        /// <summary>移除指定数量。数量不足时【不做任何改动】并返回 false（避免半途扣掉物品）。</summary>
        public bool Remove(ItemDefinition item, int amount)
        {
            if (item == null || amount <= 0) return false;
            if (CountOf(item) < amount) return false;

            int remaining = amount;

            for (int i = Slots.Count - 1; i >= 0 && remaining > 0; i--)
            {
                ItemStack slot = Slots[i];
                if (slot.Item != item) continue;

                int taken = Math.Min(slot.Amount, remaining);
                slot.Amount -= taken;
                remaining -= taken;

                if (slot.Amount <= 0) Slots.RemoveAt(i);
                else Slots[i] = slot;
            }

            Changed?.Invoke();
            return true;
        }

        /// <summary>统计某种物品的总数量（跨多个格子）。</summary>
        public int CountOf(ItemDefinition item)
        {
            if (item == null) return 0;

            int total = 0;
            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].Item == item) total += Slots[i].Amount;
            }

            return total;
        }

        /// <summary>按 itemId 统计（读档/调试用，避免依赖资产引用）。</summary>
        public int CountOf(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return 0;

            int total = 0;
            for (int i = 0; i < Slots.Count; i++)
            {
                ItemStack slot = Slots[i];
                if (slot.Item != null && slot.Item.ItemId == itemId) total += slot.Amount;
            }

            return total;
        }

        public bool IsEmpty => Slots.Count == 0;

        /// <summary>清空（读档前调用）。</summary>
        public void Clear()
        {
            if (Slots.Count == 0) return;

            Slots.Clear();
            Changed?.Invoke();
        }

        /// <summary>读档用：直接塞一个格子，不做合并。</summary>
        public void RestoreSlot(ItemStack stack)
        {
            if (stack.IsEmpty) return;

            Slots.Add(stack);
            Changed?.Invoke();
        }

        // ------------------------------------------------------------------ 拖拽

        /// <summary>
        /// 把某个格子的物品抽出来，插到另一个位置。
        ///
        /// 【为什么是"抽取 + 插入"而不是"交换"】
        /// 交换在"两个格子都有东西"时看起来对，但空格子会让结果和玩家的直觉不符：
        /// 把 A 拖到空格 B 上，交换的结果是"B 空了、A 还在原位"——
        /// 玩家看到的却是物品跳到了别的地方（因为空格子不占位置，后面的格子会前移）。
        ///
        /// 抽取 + 插入的语义是【把这一格的东西挪到那个位置】，其余物品相对顺序不变。
        /// 这符合"拖拽"这个动作的直觉。
        ///
        /// 【同类会合并】拖到同种物品上会叠加（受 MaxStackSize 限制），
        /// 剩余放不下的留在原格。这是背包类游戏的标准行为。
        /// </summary>
        public bool MoveWithin(int fromIndex, int toIndex)
        {
            if (fromIndex == toIndex) return false;
            if (fromIndex < 0 || fromIndex >= Slots.Count) return false;
            if (toIndex < 0) return false;

            ItemStack source = Slots[fromIndex];
            if (source.IsEmpty) return false;

            // 【特例：目标是"容量内的空格"】直接挪过去。
            //
            // 为什么单独处理：容量内的空格在紧凑列表里【不存在】，
            // 如果走下面的抽取 + 插入，目标下标会落在列表末尾之后的空洞里。
            // 这里显式区分"移到某个具体位置"和"移到后面某个空格"。
            if (toIndex >= Slots.Count)
            {
                if (_capacity > 0 && toIndex >= _capacity) return false;   // 超出容量

                Slots.RemoveAt(fromIndex);
                Slots.Add(source);

                Changed?.Invoke();
                return true;
            }

            ItemStack target = Slots[toIndex];

            // 同种物品 → 合并
            if (!target.IsEmpty && target.Item == source.Item)
            {
                int space = source.Item.MaxStackSize - target.Amount;
                int moved = Math.Min(space, source.Amount);

                if (moved <= 0) return false;   // 目标已满，什么都不做

                target.Amount += moved;
                Slots[toIndex] = target;

                if (moved >= source.Amount) Slots.RemoveAt(fromIndex);
                else
                {
                    source.Amount -= moved;
                    Slots[fromIndex] = source;
                }

                Changed?.Invoke();
                return true;
            }

            // 普通移动：抽出来，插到目标位置
            Slots.RemoveAt(fromIndex);

            // 移除后，若源在目标之前，目标下标要前移一位
            int insertAt = fromIndex < toIndex ? toIndex - 1 : toIndex;
            insertAt = Math.Max(0, Math.Min(insertAt, Slots.Count));

            Slots.Insert(insertAt, source);

            Changed?.Invoke();
            return true;
        }

        /// <summary>
        /// 跨容器移动（背包 ↔ 口袋）。返回是否真的动了。
        ///
        /// 【为什么不能只调 MoveWithin】两个容器是两个 Inventory 实例，
        /// 需要"从这边拿掉、放到那边"，中间任何一步失败都会丢东西。
        /// 所以这里先把物品取出来（此时源已变更），再尝试放入；
        /// 放不下就原样放回去 —— 不留半成品状态。
        /// </summary>
        public static bool MoveBetween(Inventory from, int fromIndex, Inventory to, int toIndex)
        {
            if (from == null || to == null) return false;
            if (from == to) return from.MoveWithin(fromIndex, toIndex);

            if (fromIndex < 0 || fromIndex >= from.Slots.Count) return false;
            if (toIndex < 0) return false;
            if (to.IsFull && toIndex >= to.Slots.Count) return false;   // 目标满了且没有可用的已有格

            ItemStack source = from.Slots[fromIndex];
            if (source.IsEmpty) return false;

            // 先取出（源容器变更）
            from.Slots.RemoveAt(fromIndex);

            bool placed = to.InsertAt(toIndex, source);

            if (!placed)
            {
                // 放不下 → 原样放回，不留半成品
                from.Slots.Insert(Math.Min(fromIndex, from.Slots.Count), source);
                return false;
            }

            from.Changed?.Invoke();
            to.Changed?.Invoke();
            return true;
        }

        /// <summary>
        /// 把一叠物品插到指定位置（内部用，调用方负责触发 Changed）。
        ///
        /// 三种情况：
        ///   - 目标是已有格子且同种 → 合并（放不下的部分返回 false 的"部分成功"这里不支持，
        ///     所以合并前先判断能不能全放下；放不下就整体失败，避免半格状态）
        ///   - 目标是已有格子但不同种 → 插到它前面（推挤，不覆盖）
        ///   - 目标在列表末尾之后 → 追加（受容量限制）
        /// </summary>
        private bool InsertAt(int index, ItemStack stack)
        {
            if (stack.IsEmpty) return false;

            if (index < Slots.Count)
            {
                ItemStack target = Slots[index];

                if (!target.IsEmpty && target.Item == stack.Item)
                {
                    int space = stack.Item.MaxStackSize - target.Amount;
                    if (space < stack.Amount) return false;   // 放不下全部 → 整体拒绝

                    target.Amount += stack.Amount;
                    Slots[index] = target;
                    return true;
                }

                // 不同种：插到它前面，不覆盖
                Slots.Insert(Math.Max(0, index), stack);
                return true;
            }

            // 追加
            if (_capacity > 0 && Slots.Count >= _capacity) return false;

            Slots.Add(stack);
            return true;
        }

        /// <summary>调试/存档用：把内容拼成一行文本。</summary>
        public string Describe()
        {
            if (Slots.Count == 0) return "（空）";

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            for (int i = 0; i < Slots.Count; i++)
            {
                if (i > 0) builder.Append("，");
                builder.Append(Slots[i].Item != null ? Slots[i].Item.DisplayName : "?");
                builder.Append(" ×");
                builder.Append(Slots[i].Amount);
            }

            return builder.ToString();
        }
    }
}
