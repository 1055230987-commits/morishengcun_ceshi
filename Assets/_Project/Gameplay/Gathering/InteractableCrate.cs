using Game.Items;
using Game.Player;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// 可搜刮的箱子。
    ///
    /// 和树用同一套交互接口，但行为完全不同：树是"重复采集"，箱子是"一次性开启"。
    /// 这正好验证 IInteractable 的抽象是站得住的 —— 新增一种交互物不需要改探测器和输入层。
    /// </summary>
    public class InteractableCrate : InteractableBase
    {
        [Header("内容物")]
        [Tooltip("打开后获得的物品。")]
        [SerializeField] private ItemDefinition _lootItem;

        [SerializeField, Min(1)] private int _lootAmount = 2;

        [Header("一次性")]
        [Tooltip("勾上 = 只能开一次（搜刮类箱子）；取消 = 可以反复开（补给箱原型）。")]
        [SerializeField] private bool _singleUse = true;

        private bool _opened;
        private int _remainingLoot = -1;

        public override string InteractionPrompt
        {
            get
            {
                if (_lootItem == null) return $"{name}（未配置内容物）";
                if (_singleUse && _opened) return $"{name}（已搜刮）";

                return $"搜刮 {name}";
            }
        }

        public override bool CanInteract(GameObject actor)
        {
            if (!base.CanInteract(actor)) return false;
            if (_lootItem == null) return false;
            if (_singleUse && _opened) return false;

            return true;
        }

#if UNITY_EDITOR
        /// <summary>编辑器专用：给场景生成器配置内容物。</summary>
        public void ConfigureLoot(ItemDefinition item, int amount, bool singleUse)
        {
            _lootItem = item;
            _lootAmount = Mathf.Max(1, amount);
            _singleUse = singleUse;
        }
#endif

        protected override InteractionResult OnInteract(GameObject actor)
        {
            Inventory inventory = PlayerInventory.GetInventory(actor);
            if (inventory == null)
            {
                return InteractionResult.Failed("这个角色没有背包");
            }

            if (_remainingLoot < 0) _remainingLoot = _lootAmount;
            int added = inventory.Add(_lootItem, _remainingLoot);
            if (added <= 0)
            {
                return InteractionResult.Failed("背包满了");
            }

            _remainingLoot -= added;
            if (_remainingLoot == 0)
            {
                // 补给箱反复使用也只在首次取空时改变外观。
                if (!_opened)
                {
                    transform.localScale = new Vector3(
                        transform.localScale.x, transform.localScale.y * 0.85f, transform.localScale.z);
                }
                _opened = true;
                if (!_singleUse) _remainingLoot = -1;
            }

            return InteractionResult.Success($"+{_lootItem.DisplayName} ×{added}");
        }
    }
}
