using Game.Player;
using Game.World;
using UnityEngine;

namespace Game.Gameplay.Combat
{
    /// <summary>
    /// 训练木桩：不会动、不会还手，专门用来验证武器系统。
    ///
    /// 【它复用 Health】不是另写一套血量逻辑 —— 这样"武器 → 伤害 → 血条"这条链
    /// 和玩家的生命值走的是同一份代码。木桩挨打掉的血，就是玩家以后会掉的血。
    ///
    /// 【它同时继承 InteractableBase】这样按 E 能重置木桩，
    /// 测完一轮不用重建场景就能接着测。
    ///
    /// 【表现反馈】没有动画，所以用两件事表达状态：
    ///   1. 血量低时整体变暗/偏红（改材质颜色）
    ///   2. 倒下时侧倒 + 变灰
    /// 血条由 HealthBarHud 统一在世界空间画出（它服务所有 IHealthTarget）。
    /// </summary>
    [DisallowMultipleComponent]
    public class TrainingDummy : InteractableBase, IDamageable, IHealthTarget
    {
        [Header("生命值")]
        [Tooltip("木桩血量。故意给得多一点，方便反复测试。")]
        [SerializeField, Min(1f)] private float _maxHealth = 200f;

        [Tooltip("血条挂在头顶多高。")]
        [SerializeField] private float _healthBarHeight = 2.35f;

        [Header("表现")]
        [Tooltip("受击闪白的时长（秒）。")]
        [SerializeField, Range(0f, 0.5f)] private float _hitFlashDuration = 0.08f;

        [Tooltip("受击闪烁颜色。")]
        [SerializeField] private Color _hitFlashColor = new Color(1f, 0.85f, 0.6f);

        [Tooltip("倒下时侧倒的角度。")]
        [SerializeField, Range(0f, 90f)] private float _fallAngle = 78f;

        private Health _health;
        private Renderer[] _renderers;
        private MaterialPropertyBlock _block;
        private Color[] _baseColors;
        private Quaternion _uprightRotation;
        private float _flashUntil;
        private bool _fallen;

        /// <summary>生命值逻辑对象（纯数据层，可被单测）。</summary>
        public Health Health => _health;

        public float Current => _health != null ? _health.Current : 0f;
        public float Max => _health != null ? _health.Max : _maxHealth;
        public float Normalized => _health != null ? _health.Normalized : 0f;

        public bool IsAlive => _health != null && !_health.IsDead;

        public Vector3 DamagePoint => transform.position + Vector3.up * 1.1f;

        // ---- IHealthTarget（血条用）----
        // 【为什么不直接用 Current/Max/Normalized】那三个是本类原有的名字，
        // 接口要求的是 CurrentHealth/MaxHealth/NormalizedHealth。
        // 留两套名字很别扭，但改掉原有的会破坏 Inspector 里已序列化的引用
        // （BuildController 的提示、诊断工具都在用）。所以这里做转发。
        public float CurrentHealth => Current;
        public float MaxHealth => Max;
        public float NormalizedHealth => Normalized;
        public Vector3 HealthBarAnchor => transform.position + Vector3.up * _healthBarHeight;

        public override string InteractionPrompt =>
            IsAlive ? "重置木桩（回满血）" : "扶起木桩（回满血）";

        private void Awake()
        {
            // 【注意】Health 是普通 C# 类，Unity 不会跑它的构造函数 —— 必须显式构造。
            // 本工程在 Inventory 和 PlayerHealth 上都踩过这个坑。
            _health = new Health(_maxHealth);
            _health.Changed += OnHealthChanged;
            _health.Died += OnDied;

            _renderers = GetComponentsInChildren<Renderer>(true);
            _block = new MaterialPropertyBlock();
            _uprightRotation = transform.rotation;

            // 记录初始颜色，用于恢复和做受伤染色。
            _baseColors = new Color[_renderers.Length];
            for (int i = 0; i < _renderers.Length; i++)
            {
                Material shared = _renderers[i].sharedMaterial;
                _baseColors[i] = shared != null && shared.HasProperty("_BaseColor")
                    ? shared.GetColor("_BaseColor")
                    : Color.white;
            }

            ApplyTint(1f);
        }

        private void OnDestroy()
        {
            if (_health == null) return;
            _health.Changed -= OnHealthChanged;
            _health.Died -= OnDied;
        }

        private void Update()
        {
            if (_flashUntil <= 0f || Time.time < _flashUntil) return;

            _flashUntil = 0f;
            ApplyTint(1f);
        }

        // ---------------------------------------------------------------- 受伤

        public float ApplyDamage(float amount)
        {
            if (_health == null) return 0f;

            float applied = _health.TakeDamage(amount);
            if (applied <= 0f) return 0f;

            _flashUntil = Time.time + _hitFlashDuration;
            ApplyTint(1f, _hitFlashColor);

            return applied;
        }

        private void OnHealthChanged(float current, float max)
        {
            // 血越少越暗、越偏红。这是没有动画时的替代反馈。
            float ratio = max > 0f ? Mathf.Clamp01(current / max) : 0f;
            ApplyTint(Mathf.Lerp(0.45f, 1f, ratio), Color.Lerp(new Color(1f, 0.55f, 0.45f), Color.white, ratio));
        }

        private void OnDied()
        {
            if (_fallen) return;
            _fallen = true;
            ApplyTint(0.5f, new Color(0.6f, 0.6f, 0.62f));

            // 侧倒：绕本地 X 轴倒下。木桩不是物理体，所以直接设旋转。
            transform.rotation = _uprightRotation * Quaternion.Euler(_fallAngle, 0f, 0f);
        }

        /// <summary>
        /// 给所有渲染器染色。
        /// 用 MaterialPropertyBlock 而不是改 sharedMaterial —— 后者会改到材质【资产】，
        /// 影响场景里所有用同一材质的物体（本工程的灰盒材质是共享的）。
        /// </summary>
        private void ApplyTint(float brightness, Color? tint = null)
        {
            if (_renderers == null) return;

            Color target = tint ?? Color.white;
            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer renderer = _renderers[i];
                if (renderer == null) continue;

                Color baseColor = _baseColors != null && i < _baseColors.Length ? _baseColors[i] : Color.white;
                Color result = new Color(
                    baseColor.r * target.r * brightness,
                    baseColor.g * target.g * brightness,
                    baseColor.b * target.b * brightness,
                    baseColor.a);

                renderer.GetPropertyBlock(_block);
                _block.SetColor("_BaseColor", result);
                _block.SetColor("_Color", result);      // 兼容非 URP/Lit 的 shader
                renderer.SetPropertyBlock(_block);
            }
        }

        // ---------------------------------------------------------------- 交互

        protected override InteractionResult OnInteract(GameObject actor)
        {
            ResetDummy();
            return InteractionResult.Success(IsAlive ? "木桩已回满血" : "木桩已扶起并回满血");
        }

        /// <summary>回满血、扶正。</summary>
        public void ResetDummy()
        {
            if (_health == null) return;

            _health.Revive(1f);
            _fallen = false;
            transform.rotation = _uprightRotation;
            _flashUntil = 0f;
            ApplyTint(1f);
        }

#if UNITY_EDITOR
        /// <summary>编辑器专用：场景生成器配置。</summary>
        public void ConfigureDummy(float maxHealth)
        {
            _maxHealth = Mathf.Max(1f, maxHealth);
            _health = new Health(_maxHealth);
            _health.Changed += OnHealthChanged;
            _health.Died += OnDied;
        }
#endif

        private void OnValidate()
        {
            _maxHealth = Mathf.Max(1f, _maxHealth);
        }
    }
}
