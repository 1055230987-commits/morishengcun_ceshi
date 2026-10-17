using System;

namespace Game.Items
{
    /// <summary>
    /// 背包里的一格：物品定义 + 数量。
    /// 是可序列化结构体，所以以后存档可以直接 JsonUtility 序列化数组，不需要额外转换。
    /// </summary>
    [Serializable]
    public struct ItemStack
    {
        public ItemDefinition Item;
        public int Amount;

        public ItemStack(ItemDefinition item, int amount)
        {
            Item = item;
            Amount = amount;
        }

        public bool IsEmpty => Item == null || Amount <= 0;

        public static readonly ItemStack Empty = new ItemStack(null, 0);
    }
}
