using UnityEngine;

namespace Game.Gameplay.Combat
{
    /// <summary>
    /// "有一个会变的血量、并且可以被打"的统一约定。
    ///
    /// 【为什么叫 HealthTarget 而不是 Damageable】已经有 <see cref="IDamageable"/> 管"挨打"，
    /// 这个接口管"血条怎么显示"。两者分开的理由：
    ///   - 有些东西能挨打但没有血条（以后的地形破坏）
    ///   - 有些东西有血条但不受伤害（以后的护盾发生器）
    /// 现在木桩和敌人两个都实现，但接口保持独立。
    ///
    /// 【为什么需要它 —— 血条组件的复用】
    /// 早先木桩的血条是写死在 TrainingDummyHud 里的（直接引用 TrainingDummy）。
    /// 加敌人时如果照抄一份，就会有第二份几乎一样的血条代码 ——
    /// 改一处忘一处（本工程在输入模式上踩过这个坑）。
    /// 抽成接口之后，一个血条组件同时服务木桩和敌人。
    /// </summary>
    public interface IHealthTarget
    {
        /// <summary>当前血量。</summary>
        float CurrentHealth { get; }

        /// <summary>血量上限。</summary>
        float MaxHealth { get; }

        /// <summary>0~1 的填充比例。</summary>
        float NormalizedHealth { get; }

        /// <summary>是否还活着（倒下的画灰条）。</summary>
        bool IsAlive { get; }

        /// <summary>血条挂在世界里的哪个位置（通常是头顶）。</summary>
        Vector3 HealthBarAnchor { get; }
    }
}
