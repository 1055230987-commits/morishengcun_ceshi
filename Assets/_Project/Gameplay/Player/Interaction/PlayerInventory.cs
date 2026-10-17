using Game.Items;
using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// 把背包挂到角色身上。
    ///
    /// 刻意做成很薄的一层：只负责"角色拥有哪些容器"这件事，
    /// 容器本身的数据与逻辑都在 Inventory（纯 C#）里，方便以后给箱子/NPC 复用、也方便存档。
    ///
    /// 两类容器：
    ///   背包（Backpack）4×4 = 16 格 —— 可扩展，以后换成"装备不同容量的背包"只需改容量
    ///   口袋（Pocket）  4 格        —— 角色自带，不需要装备任何东西
    /// 两者都是普通 Inventory，容量不同而已，所以采集/消耗逻辑不需要区分它们。
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerInventory : MonoBehaviour
    {
        /// <summary>背包容量：4×4。</summary>
        public const int BackpackCapacity = 16;

        /// <summary>口袋容量：4 格（角色自带）。</summary>
        public const int PocketCapacity = 4;

        // 刻意【不在这里 new】：Unity 会先跑字段初始化、再跑反序列化，
        // 那个临时实例会被丢掉，白分配一个。真正的兜底在下面的属性里。
        [SerializeField] private Inventory _backpack;
        [SerializeField] private Inventory _pocket;

        /// <summary>背包（4×4）。采集产出默认进这里。</summary>
        public Inventory Backpack
        {
            get
            {
                // 反序列化、域重载、旧场景缺字段，都可能让这里为 null。
                // 放在属性里兜底，而不是只在 Awake 里兜底 —— 这样任何调用时机都不会拿到 null。
                if (_backpack == null) _backpack = new Inventory(BackpackCapacity);
                return _backpack;
            }
        }

        /// <summary>口袋（4 格，角色自带）。</summary>
        public Inventory Pocket
        {
            get
            {
                if (_pocket == null) _pocket = new Inventory(PocketCapacity);
                return _pocket;
            }
        }

        /// <summary>
        /// 兼容旧调用：默认返回背包。
        /// 采集/掉落默认进背包，需要"优先装口袋"时用 AddToPocketFirst。
        /// </summary>
        public Inventory Inventory => Backpack;

        private void Awake()
        {
            // 通过属性访问一次，保证两个容器一定存在且容量正确。
            Backpack.Capacity = BackpackCapacity;
            Pocket.Capacity = PocketCapacity;
        }

        /// <summary>给其他系统（AI、联机）用的静态快捷方式：取某个角色身上的主背包。</summary>
        public static Inventory GetInventory(GameObject actor)
        {
            if (actor == null) return null;

            PlayerInventory holder = actor.GetComponent<PlayerInventory>();
            return holder != null ? holder.Backpack : null;
        }

        /// <summary>取某个角色身上的口袋。</summary>
        public static Inventory GetPocket(GameObject actor)
        {
            if (actor == null) return null;

            PlayerInventory holder = actor.GetComponent<PlayerInventory>();
            return holder != null ? holder.Pocket : null;
        }

        /// <summary>
        /// 先装口袋、装不下再装背包。返回【实际放入的总数】。
        /// 用于以后"捡拾优先放口袋"这类手感设计；目前的采集走 TryAdd（只进背包）。
        /// </summary>
        public int AddToPocketFirst(ItemDefinition item, int amount)
        {
            if (item == null || amount <= 0) return 0;

            int added = Pocket.Add(item, amount);
            int remaining = amount - added;
            if (remaining <= 0) return added;

            return added + Backpack.Add(item, remaining);
        }

        /// <summary>往背包里加（采集/掉落的主入口）。返回实际放入数量。</summary>
        public int TryAdd(ItemDefinition item, int amount)
        {
            return Backpack.Add(item, amount);
        }

        /// <summary>两类容器加起来一共持有多少（用于"总数"类显示）。</summary>
        public int CountTotal(ItemDefinition item)
        {
            if (item == null) return 0;
            return Backpack.CountOf(item) + Pocket.CountOf(item);
        }
    }
}
