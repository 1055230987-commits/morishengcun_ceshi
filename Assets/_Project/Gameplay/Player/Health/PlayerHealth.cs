using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// 玩家生命值：把纯逻辑的 <see cref="Health"/> 接到场景里。
    ///
    /// 【分工】Health 负责"还剩多少血、死没死"（不碰引擎、可单测）；
    /// 本组件负责"挂在哪、谁受伤、死了怎么办、怎么给其他系统看"。
    ///
    /// 【以后的伤害来源】现在只提供 API + 一个调试快捷键。
    /// 真实来源（丧尸攻击、陷阱、坠落、饥饿）以后接进来时，
    /// 一律调 <see cref="TakeDamage"/>，不要绕过去直接改 Health ——
    /// 否则死亡判定和事件会被漏掉。
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerHealth : MonoBehaviour, Game.Gameplay.Combat.IDamageable
    {
        [Header("生命值")]
        [Tooltip("最大生命值。")]
        [SerializeField, Min(1f)] private float _maxHealth = 100f;

        [Tooltip("打开后按 H 造成一次伤害、按 J 治疗一次（原型阶段用来试血条）。\n" +
                 "真实伤害来源接进来之后可以关掉。")]
        [SerializeField] private bool _debugKeys = true;

        [Tooltip("调试伤害量。")]
        [SerializeField, Min(1f)] private float _debugDamage = 12f;

        [Header("受伤后无敌时间")]
        [Tooltip("受伤后这段时间内免疫伤害（秒）。0 = 不免疫。\n" +
                 "没有它的话，持续接触型伤害会在几帧内把人秒掉。")]
        [SerializeField, Min(0f)] private float _invulnerableTime = 0.5f;

        private Health _health;
        private float _invulnerableUntil;

        /// <summary>生命值逻辑对象。UI 和伤害来源都通过它读取状态。</summary>
        public Health Health => _health;

        public float Current => _health != null ? _health.Current : 0f;
        public float Max => _health != null ? _health.Max : _maxHealth;
        public float Normalized => _health != null ? _health.Normalized : 0f;
        public bool IsDead => _health != null && _health.IsDead;
        public bool IsInvulnerable => Time.time < _invulnerableUntil;

        /// <summary>玩家死亡时触发（供以后接重生、结算画面）。</summary>
        public event System.Action Died;

        // ---------------------------------------------------------------- IDamageable
        //
        // 【为什么让玩家也实现 IDamageable】敌人攻击时不该知道"我在打玩家"，
        // 它只需要知道"这是个能受伤的东西"。这样：
        //   - 手枪打木桩、敌人打玩家、以后玩家打敌人，走的是同一个接口
        //   - 玩家的无敌时间、以后的护甲/减伤，全部自动对敌人生效
        //     （因为都经过 TakeDamage 这一个入口）
        //
        // IsAlive 用 !IsDead（接口的语义是"还能不能被打"，和"死亡"互补）。

        /// <summary>还能不能被打。</summary>
        public bool IsAlive => !IsDead;

        /// <summary>受到伤害，返回实际扣掉的血量（无敌时间内返回 0）。</summary>
        float Game.Gameplay.Combat.IDamageable.ApplyDamage(float amount) => TakeDamage(amount);

        /// <summary>命中判定用的位置（胸口高度）。</summary>
        public Vector3 DamagePoint => transform.position + Vector3.up * 1.0f;

        private void Awake()
        {
            // 【注意】Health 是 [Serializable] 的普通类，Unity 反序列化不会跑它的构造函数，
            // 所以这里必须显式构造，不能指望字段初始化。
            _health = new Health(_maxHealth);
            _health.Died += HandleDied;
        }

        private void OnDestroy()
        {
            if (_health != null) _health.Died -= HandleDied;
        }

        private void Update()
        {
            if (!_debugKeys) return;

            // 用键盘直接测，不需要先做出一套伤害来源。
            // 读键走 Input System（本工程禁用了旧 UnityEngine.Input，读它会抛异常）。
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.hKey.wasPressedThisFrame) TakeDamage(_debugDamage);
            if (keyboard.jKey.wasPressedThisFrame) Heal(_debugDamage);
        }

        /// <summary>
        /// 【存档专用】把血量直接设定成某个值，不走伤害/治疗流程。
        ///
        /// 【为什么必须单独开一个接口，而不是拿 TakeDamage/Heal 去凑】
        /// 那两个都有副作用：
        ///   - TakeDamage 会触发无敌时间，还会在血量归零时触发 Died 事件
        ///   - Heal 有"死亡后不能治疗"的限制，救不回已经倒下的玩家
        /// 存档恢复是"把状态摆回保存时的样子"，不是"经历一次伤害或治疗"。
        /// 用副作用凑出来的状态一定和保存时不同（例如多了一段无敌时间）。
        ///
        /// 这个方法是整个工程里【唯一】能绕过这些规则改血量的入口，
        /// 名字里带 Restore 是为了在调用处一眼看出"这是存档在恢复"。
        /// </summary>
        public void RestoreHealth(float value)
        {
            if (_health == null) return;

            _health.SetCurrent(value);

            // 恢复时不该带着上一局的受伤冷却
            _invulnerableUntil = 0f;

            if (_logDamage) Debug.Log($"[生命] 存档恢复血量到 {_health.Current:F1}/{_health.Max:F1}", this);
        }

        /// <summary>
        /// 造成伤害。返回实际扣掉的血量。
        /// 无敌时间内、或已经死亡时返回 0。
        /// </summary>
        public float TakeDamage(float amount)
        {
            if (_health == null) return 0f;

            if (IsInvulnerable)
            {
                if (_logDamage) Debug.Log($"[生命] 无敌时间内，忽略 {amount:F1} 点伤害（剩余 {_invulnerableUntil - Time.time:F2}s）", this);
                return 0f;
            }

            float applied = _health.TakeDamage(amount);

            if (applied > 0f)
            {
                if (_invulnerableTime > 0f) _invulnerableUntil = Time.time + _invulnerableTime;
                if (_logDamage)
                {
                    Debug.Log($"[生命] 受伤 {applied:F1}（{_health.Current:F1}/{_health.Max:F1}）", this);
                }
            }

            return applied;
        }

        public float Heal(float amount)
        {
            if (_health == null) return 0f;

            float applied = _health.Heal(amount);
            if (applied > 0f && _logDamage)
            {
                Debug.Log($"[生命] 回复 {applied:F1}（{_health.Current:F1}/{_health.Max:F1}）", this);
            }
            return applied;
        }

        /// <summary>复活并回满血。</summary>
        public void Revive()
        {
            if (_health == null) return;
            _health.Revive();
            _invulnerableUntil = Time.time + Mathf.Max(_invulnerableTime, 1f);   // 复活给一点无敌
        }

        [Tooltip("打开后打印每次受伤/回复。")]
        [SerializeField] private bool _logDamage;

        private void HandleDied()
        {
            Debug.Log("[生命] 玩家死亡", this);
            Died?.Invoke();
        }

#if UNITY_EDITOR
        /// <summary>编辑器专用：场景生成器配置。</summary>
        public void ConfigureHealth(float maxHealth)
        {
            _maxHealth = Mathf.Max(1f, maxHealth);
            _health = new Health(_maxHealth);
            _health.Died += HandleDied;
        }
#endif

        private void OnValidate()
        {
            // 【只在运行时同步】
            //
            // 早先这里写的是"编辑期也同步"（`if (!Application.isPlaying && _health != null) ...`），
            // 结果在编辑器里抛 NullReferenceException —— 因为 _health 是普通 C# 类字段、
            // 不是序列化字段，编辑期它根本没有被创建（只有 Awake 里才 new）。
            //
            // 所以编辑期不做任何事：_maxHealth 是序列化字段，改它会正常存进场景；
            // 运行时的 Health 会在 Awake 里按这个值构造。
            // 运行时想改上限请走 ReconfigureKeepingRatio。
            if (!Application.isPlaying) return;
            if (_health == null) return;

            if (!Mathf.Approximately(_health.Max, _maxHealth))
            {
                _health.ReconfigureKeepingRatio(_maxHealth);
            }
        }
    }
}
