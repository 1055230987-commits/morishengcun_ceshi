using System.Collections.Generic;
using Game.Gameplay.Combat;
using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// 在世界空间里给所有"有血量的东西"画血条（投影到屏幕上用 OnGUI 画）。
    ///
    /// 【为什么用 OnGUI 而不是 World Space Canvas】
    /// 和 InteractionHud / InventoryScreen 保持一致：本工程目前没有引入 uGUI，
    /// 为一个血条拉进整套 Canvas / RectTransform 体系不划算。
    /// WorldToScreenPoint + 画矩形已经够用，以后做正式 UI 时整段删掉即可。
    ///
    /// 【它服务谁】任何实现了 <see cref="IHealthTarget"/> 的对象 ——
    /// 现在是训练木桩和敌人。加新的可受伤对象时不用改这个组件。
    ///
    /// 【性能】每帧要遍历所有目标并投影。数量在几十个以内没问题；
    /// 以后规模大了应该改成"只在受伤后若干秒内显示"或走对象池的精灵。
    /// 这里刻意保持简单 —— 现阶段要的是能看到掉血。
    /// </summary>
    [DisallowMultipleComponent]
    public class HealthBarHud : MonoBehaviour
    {
        [Header("开关")]
        [SerializeField] private bool _showHealthBars = true;

        [Tooltip("已经倒下的目标是否还显示血条。")]
        [SerializeField] private bool _showDead = true;

        [Header("布局")]
        [SerializeField] private float _barWidth = 64f;
        [SerializeField] private float _barHeight = 7f;

        [Tooltip("血条相对锚点的向上偏移（世界单位）。")]
        [SerializeField] private float _worldOffset;

        [Tooltip("超过这个距离（米）不画血条，避免远处一堆小条。")]
        [SerializeField, Min(1f)] private float _maxDistance = 30f;

        [Tooltip("同屏最多画多少条。防止极端情况下刷爆。")]
        [SerializeField, Min(1)] private int _maxBars = 48;

        [Tooltip("列表刷新间隔（秒）。不必每帧 FindObjects。")]
        [SerializeField, Min(0.1f)] private float _refreshInterval = 1f;

        [Header("外观")]
        [SerializeField] private Color _backColor = new Color(0.05f, 0.05f, 0.06f, 0.8f);
        [SerializeField] private Color _borderColor = new Color(0f, 0f, 0f, 0.9f);

        [Tooltip("木桩等「训练用目标」的高血量颜色。")]
        [SerializeField] private Color _dummyHighColor = new Color(0.85f, 0.45f, 0.3f);

        [Tooltip("敌人的高血量颜色。用不同颜色区分「靶子」和「会还手的东西」。")]
        [SerializeField] private Color _enemyHighColor = new Color(0.85f, 0.22f, 0.20f);

        [SerializeField] private Color _lowColor = new Color(0.75f, 0.15f, 0.12f);
        [SerializeField] private Color _deadColor = new Color(0.4f, 0.4f, 0.42f);

        private readonly List<IHealthTarget> _targets = new List<IHealthTarget>();
        private readonly List<bool> _isEnemy = new List<bool>();
        private Camera _camera;
        private float _nextRefresh;

        private void Awake() => RefreshList();

        private void Update()
        {
            if (Time.time < _nextRefresh) return;

            _nextRefresh = Time.time + Mathf.Max(0.1f, _refreshInterval);
            RefreshList();
            if (_camera == null) _camera = Camera.main;
        }

        /// <summary>
        /// 收集场景里所有血条目标。
        ///
        /// 【为什么要同时找两类组件】木桩（TrainingDummy）和敌人（EnemyController）
        /// 是两个不同的 MonoBehaviour，但它们都实现了同一个接口。
        /// Unity 没有"按接口查找"的 API，所以这里分别找再合并。
        /// 加第三种可受伤对象时，在这里加一行即可。
        /// </summary>
        private void RefreshList()
        {
            _targets.Clear();
            _isEnemy.Clear();

#if UNITY_2023_1_OR_NEWER
            var dummies = Object.FindObjectsByType<Game.Gameplay.Combat.TrainingDummy>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            var enemies = Object.FindObjectsByType<Game.Gameplay.AI.EnemyController>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
            var dummies = Object.FindObjectsOfType<Game.Gameplay.Combat.TrainingDummy>();
            var enemies = Object.FindObjectsOfType<Game.Gameplay.AI.EnemyController>();
#endif

            foreach (var dummy in dummies)
            {
                _targets.Add(dummy);
                _isEnemy.Add(false);
            }

            foreach (var enemy in enemies)
            {
                _targets.Add(enemy);
                _isEnemy.Add(true);
            }
        }

        private void OnGUI()
        {
            if (!_showHealthBars) return;
            if (_targets.Count == 0) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;

            if (_camera == null)
            {
                _camera = Camera.main;
                if (_camera == null) return;
            }

            Vector3 cameraPosition = _camera.transform.position;
            int drawn = 0;

            for (int i = 0; i < _targets.Count && drawn < _maxBars; i++)
            {
                IHealthTarget target = _targets[i];
                if (target == null) continue;
                if (!target.IsAlive && !_showDead) continue;

                Vector3 anchor = target.HealthBarAnchor;

                // 距离裁剪：用锚点算，比屏幕坐标稳定
                float distance = Vector3.Distance(cameraPosition, anchor);
                if (distance > _maxDistance) continue;

                Vector3 screen = _camera.WorldToScreenPoint(anchor + Vector3.up * _worldOffset);

                // z <= 0 表示点在相机背后 —— 必须剔除，否则会画到屏幕另一侧（镜像鬼影）。
                if (screen.z <= 0f) continue;

                // IMGUI 的 y 轴向下，屏幕坐标 y 轴向上，所以翻转。
                float x = screen.x - _barWidth * 0.5f;
                float y = Screen.height - screen.y - _barHeight * 0.5f;

                DrawBar(target, _isEnemy[i], new Rect(x, y, _barWidth, _barHeight));
                drawn++;
            }
        }

        private void DrawBar(IHealthTarget target, bool isEnemy, Rect rect)
        {
            Fill(rect, _backColor);

            float ratio = Mathf.Clamp01(target.NormalizedHealth);

            if (!target.IsAlive)
            {
                // 倒下：画一条灰色的满槽，表示"已经没了"。
                Fill(rect, _deadColor);
            }
            else if (ratio > 0f)
            {
                Color high = isEnemy ? _enemyHighColor : _dummyHighColor;
                Color fill = Color.Lerp(_lowColor, high, ratio);
                Fill(new Rect(rect.x, rect.y, rect.width * ratio, rect.height), fill);
            }

            Border(rect, _borderColor);
        }

        /// <summary>用 Unity 内置白贴图 + GUI.color 画纯色矩形。</summary>
        private static void Fill(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            // 用内置 whiteTexture，而不是自己 new Texture2D：
            // 自建贴图若创建失败，GUI.DrawTexture 会静默什么都不画（本工程踩过）。
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private static void Border(Rect rect, Color color)
        {
            Fill(new Rect(rect.x, rect.y, rect.width, 1f), color);
            Fill(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), color);
            Fill(new Rect(rect.x, rect.y, 1f, rect.height), color);
            Fill(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), color);
        }
    }
}
