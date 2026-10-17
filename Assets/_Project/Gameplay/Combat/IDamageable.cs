using UnityEngine;

namespace Game.Gameplay.Combat
{
    /// <summary>
    /// "能被打"的统一约定。
    ///
    /// 【为什么要这个接口，而不是直接 GetComponent&lt;Health&gt;】
    /// Health 是纯逻辑类，挂在哪个组件上、谁持有它，是各对象自己的事：
    ///   木桩 → TrainingDummy 持有 Health
    ///   玩家 → PlayerHealth 持有 Health（带无敌时间）
    ///   以后的丧尸 → 大概又是另一个组件
    /// 武器只关心"给它造成伤害、并拿到实际掉血量"，不该知道这些细节。
    ///
    /// 【返回实际伤害】可能小于请求值（已经死了、无敌、伤害被吸收）。
    /// 命中提示、统计、飘字都需要这个真值，而不是请求值。
    /// </summary>
    public interface IDamageable
    {
        /// <summary>还能不能被打（已经死了的不要再吃伤害）。</summary>
        bool IsAlive { get; }

        /// <summary>造成伤害，返回【实际扣掉的血量】。0 表示这次伤害没生效。</summary>
        float ApplyDamage(float amount);

        /// <summary>命中判定用的位置（飘字、命中特效从这里生成）。</summary>
        Vector3 DamagePoint { get; }
    }
}
