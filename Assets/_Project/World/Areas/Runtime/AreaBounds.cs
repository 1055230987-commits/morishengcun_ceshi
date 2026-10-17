using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// 当前场景对应的地图区域。场景里只放一个，作为「这个场景是哪张地图」的入口。
    ///
    /// 场景本身只是这张地图的一份运行时呈现；
    /// 地图的尺寸/边界/出生点数据放在 MapAreaDefinition 资产里，未来切图只需换 Scene + 换这份资产。
    /// </summary>
    [DisallowMultipleComponent]
    public class AreaBounds : MonoBehaviour
    {
        [Tooltip("本场景使用的地图定义。留空则使用回退尺寸（仅用于临时测试）。")]
        [SerializeField] private MapAreaDefinition _definition;

        [Header("回退尺寸（未指定 Definition 时生效）")]
        [SerializeField] private Vector2 _fallbackSizeXZ = new Vector2(60f, 60f);
        [SerializeField] private float _fallbackInset = 0.6f;

        private MapAreaDefinition _runtimeDefinition;

        public MapAreaDefinition Definition
        {
            get
            {
                if (_definition != null) return _definition;
                if (_runtimeDefinition == null)
                {
                    _runtimeDefinition = ScriptableObject.CreateInstance<MapAreaDefinition>();
                    _runtimeDefinition.hideFlags = HideFlags.HideAndDontSave;
                }
                return _runtimeDefinition;
            }
            set => _definition = value;
        }

        public string MapId => Definition.MapId;
        public float WalkableHalfExtent => Definition.WalkableHalfExtent;

        /// <summary>把一次水平位移夹回地图内，返回允许的位移量（Y 分量原样返回）。</summary>
        public Vector3 ClampMove(Vector3 currentPosition, Vector3 delta, float margin = 0f)
        {
            return Definition.ClampMove(currentPosition, delta, margin);
        }

        public Vector3 ClampPosition(Vector3 position, float margin = 0f)
        {
            return Definition.ClampPosition(position, margin);
        }

        public bool Contains(Vector3 position)
        {
            return Definition.Contains(position);
        }

        private void Awake()
        {
            // 保证运行时一定有一份有效定义，避免忘记拖资产导致边界失效。
            if (_definition == null)
            {
                Debug.LogWarning(
                    $"[{nameof(AreaBounds)}] 未指定 MapAreaDefinition，正在使用回退尺寸 " +
                    $"{_fallbackSizeXZ}。请在 Inspector 里指定地图资产。", this);
            }
        }

        private void OnDrawGizmosSelected()
        {
            Vector2 size = _definition != null ? _definition.SizeXZ : _fallbackSizeXZ;
            float inset = _definition != null
                ? Mathf.Min(size.x, size.y) * 0.5f - _definition.WalkableHalfExtent
                : _fallbackInset;

            // 地图外框
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 1f);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(size.x, 0.1f, size.y));

            // 实际可行走范围
            float half = Mathf.Min(size.x, size.y) * 0.5f - inset;
            Gizmos.color = new Color(1f, 0.85f, 0.2f, 1f);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(half * 2f, 0.1f, half * 2f));
        }
    }
}
