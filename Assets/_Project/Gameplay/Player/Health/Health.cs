// 本文件开启可空引用类型分析：事件声明成可空（Action?），并用 ?.Invoke 调用。
//
// 【为什么需要】Health 是普通 C# 类，不是 MonoBehaviour —— Unity 的一些构造路径
// （反序列化、编辑器校验）会绕过字段初始化器，导致事件是 null。
// 让 null 成为合法状态是最稳的做法；而 Game.Runtime 程序集默认没开可空上下文，
// 所以这里用 #nullable enable 局部开启，否则会报 CS8632。
#nullable enable

using System;

namespace Game.Player
{
    /// <summary>
    /// 生命值数据层：当前值 / 上限 / 受伤 / 治疗 / 死亡。
    ///
    /// 【为什么刻意不 using UnityEngine】
    /// 和 StairSnapLogic 同一个理由：这是纯逻辑，不碰引擎就能验证。
    /// 只要这个文件不引用 UnityEngine，就能被 OptimizationReview/StairSnapChecks
    /// 那个独立 .NET 程序直接编译进去做断言 —— 不开 Unity 也能测。
    ///
    /// 【为什么不用现成的开源包】搜到过 ashblue/unity-gauges（MIT，5 个文件），
    /// 它做的正是这件事：int 血量的 get/set + 变更事件。但它 package.json 里
    /// 版本还是 0.0.0，而且这点逻辑和本工程的 Inventory / ItemDefinition 是同一层 ——
    /// 引入第三方只会让以后每次改需求都要迁就它的接口。所以自己写。
    ///
    /// 【它不管什么】显示、动画、无敌帧、护甲、抗性 —— 那些属于上层。
    /// 这里只回答"还剩多少血、死没死、什么时候变的"。
    /// </summary>
    [Serializable]
    public class Health
    {
        /// <summary>
        /// 生命值变化时触发。参数：(当前值, 上限)。
        ///
        /// 【为什么声明成可空、并用 ?.Invoke 调用】
        /// Health 是普通 C# 类，不是 MonoBehaviour。字段初始化器（= delegate { }）
        /// 在"正常 new"时会跑，但 Unity 的一些构造路径（反序列化、编辑器校验）会绕过它。
        /// 本工程实际踩过：事件是 null → RaiseChanged 抛 NullReferenceException。
        /// 所以让 null 成为合法状态，调用一律用 ?.Invoke —— 永远不用关心有没有订阅者。
        /// </summary>
        public event Action<float, float>? Changed;

        /// <summary>死亡时触发一次（从"活着"变为"死了"的那一次）。</summary>
        public event Action? Died;

        /// <summary>满血时受到治疗、或对尸体造成伤害等情况会触发，用于排查。</summary>
        public event Action<string>? Rejected;

        private float _max;
        private float _current;
        private bool _dead;

        /// <summary>
        /// 构造。
        /// 【注意】Unity 反序列化不会调用构造函数，所以字段初始化不能只依赖这里 ——
        /// 使用方要么显式构造，要么调用 Configure。
        /// </summary>
        public Health(float max)
        {
            Configure(max);
        }

        /// <summary>把上限设为 max 并回满血。切换最大生命值时用。</summary>
        public void Configure(float max)
        {
            _max = Clamp(max, 1f, float.MaxValue);
            _current = _max;
            _dead = false;
            RaiseChanged();
        }

        /// <summary>把上限设为 max，并按比例保留当前血量（用于"升级后血量上限提高"）。</summary>
        public void ReconfigureKeepingRatio(float max)
        {
            float ratio = _max > 0f ? _current / _max : 1f;
            _max = Clamp(max, 1f, float.MaxValue);
            _current = Clamp(_max * ratio, 0f, _max);
            _dead = _current <= 0f;
            RaiseChanged();
        }

        public float Max => _max;
        public float Current => _current;
        public bool IsDead => _dead;
        /// <summary>0~1 的填充比例。UI 直接用这个画条。</summary>
        public float Normalized => _max > 0f ? Clamp(_current / _max, 0f, 1f) : 0f;

        /// <summary>
        /// 造成伤害。返回【实际扣掉的血量】—— 可能小于请求值
        /// （已经死了、或者伤害超过剩余血量）。
        /// </summary>
        public float TakeDamage(float amount)
        {
            if (!(amount > 0f)) return 0f;      // NaN 和负数都走这里

            if (_dead)
            {
                Rejected?.Invoke("已经死亡，伤害无效");
                return 0f;
            }

            float before = _current;
            _current = Clamp(_current - amount, 0f, _max);
            float applied = before - _current;

            RaiseChanged();

            if (_current <= 0f && !_dead)
            {
                _dead = true;
                Died?.Invoke();
            }

            return applied;
        }

        /// <summary>
        /// 治疗。返回【实际回复的血量】—— 可能小于请求值（满血、或已死亡）。
        /// 【设计取舍】默认死亡后不能被治疗（需要先复活），
        /// 否则"血条回满但角色还是尸体状态"这种不一致会出现。
        /// </summary>
        public float Heal(float amount)
        {
            if (!(amount > 0f)) return 0f;

            if (_dead)
            {
                Rejected?.Invoke("已经死亡，需要先复活");
                return 0f;
            }

            float before = _current;
            _current = Clamp(_current + amount, 0f, _max);
            float applied = _current - before;

            if (applied > 0f) RaiseChanged();
            else Rejected?.Invoke("已经满血");

            return applied;
        }

        /// <summary>复活并回满血。死亡后重新开始用。</summary>
        public void Revive(float ratio = 1f)
        {
            _dead = false;
            _current = Clamp(_max * Clamp(ratio, 0f, 1f), 0f, _max);
            RaiseChanged();
        }

        /// <summary>
        /// 【存档专用】把当前血量直接设定成某个值。
        ///
        /// 【为什么不能用 Heal/TakeDamage 凑】那两个都有副作用：
        ///   - TakeDamage 会在血量归零时触发 Died 事件
        ///   - Heal 在死亡状态下直接拒绝
        /// 存档恢复是"把状态摆回保存时的样子"，不是"经历一次伤害或治疗"。
        ///
        /// 【为什么需要它而不是用 Revive(比例)】Revive 只能按比例设，
        /// 而血量上限以后可能变（升级、装备）。按【绝对值】设定才是正确的语义 ——
        /// 存档里存的就是绝对值。
        ///
        /// 【对死亡状态的处理】设为 0 会进入死亡状态并触发 Died；
        /// 设为正数会复活（_dead 置回 false）。
        /// </summary>
        public void SetCurrent(float value)
        {
            bool wasDead = _dead;
            float clamped = Clamp(value, 0f, _max);

            _current = clamped;
            _dead = clamped <= 0f;

            RaiseChanged();

            // 只在【从活变死】时触发，避免重复读档时反复触发死亡结算
            if (_dead && !wasDead) Died?.Invoke();
        }

        private void RaiseChanged() => Changed?.Invoke(_current, _max);

        private static float Clamp(float v, float min, float max)
        {
            if (float.IsNaN(v)) return min;
            if (v < min) return min;
            return v > max ? max : v;
        }
    }
}
