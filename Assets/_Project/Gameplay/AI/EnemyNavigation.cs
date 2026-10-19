#nullable enable
using UnityEngine;
using UnityEngine.AI;

namespace Game.Gameplay.AI
{
    public enum EnemyNavigationResult { NoNavMesh, NoPath, Moving, Arrived, PartialEnd }

    /// <summary>NavMesh 路径查询与跟随；CharacterController 仍负责实际移动。</summary>
    public sealed class EnemyNavigation
    {
        public const string AgentTypeName = "SurvivalEnemy";
        public const float DefaultSnapRadius = 2f;
        public const float MaxSampleHeightDifference = 0.6f;
        public float RepathInterval = 0.35f;
        public float TargetMoveTolerance = 0.8f;
        public float CornerReach = 0.15f;
        public float SnapRadius = DefaultSnapRadius;
        public int AgentTypeId = FindAgentTypeId();

        private readonly NavMeshPath _path = new NavMeshPath();
        private Vector3[] _corners = new Vector3[64];
        private float _nextRepathTime;
        private Vector3 _lastTarget;
        private bool _hasLastTarget;
        private bool _forceRepath;
        private int _cornerIndex;
        public bool HasPath { get; private set; }
        public bool NavMeshAvailable { get; private set; }
        public int CornerCount { get; private set; }
        public int RecalculationCount { get; private set; }
        public NavMeshPathStatus LastStatus { get; private set; } = NavMeshPathStatus.PathInvalid;
        public EnemyNavigationResult Result { get; private set; }
        public float DistanceToCorner { get; private set; }
        public NavMeshQueryFilter Filter => new NavMeshQueryFilter
            { agentTypeID = AgentTypeId, areaMask = NavMesh.AllAreas };

        public static int FindAgentTypeId()
        {
            for (int i = 0; i < NavMesh.GetSettingsCount(); i++)
            {
                int id = NavMesh.GetSettingsByIndex(i).agentTypeID;
                if (NavMesh.GetSettingsNameFromID(id) == AgentTypeName) return id;
            }
            return 0;
        }

        public void Reset()
        {
            HasPath = NavMeshAvailable = _hasLastTarget = _forceRepath = false;
            CornerCount = _cornerIndex = RecalculationCount = 0;
            _nextRepathTime = DistanceToCorner = 0f;
            LastStatus = NavMeshPathStatus.PathInvalid;
            Result = EnemyNavigationResult.NoNavMesh;
        }

        public void RequestRepath() => _forceRepath = true;

        // 高低楼层不能仅凭一个很大的球形采样半径互相吸附。
        public bool TrySamplePosition(Vector3 position, out NavMeshHit hit) =>
            NavMesh.SamplePosition(position, out hit, SnapRadius, Filter)
            && Mathf.Abs(hit.position.y - position.y) <= MaxSampleHeightDifference;

        public EnemyNavigationResult GetDirection(Vector3 self, Vector3 target, float now,
            out Vector3 direction)
        {
            direction = Vector3.zero;
            DistanceToCorner = 0f;
            // 无路时也遵守间隔；目标移动只允许提前刷新已有路径。
            if (!_hasLastTarget || _forceRepath || now >= _nextRepathTime ||
                (HasPath && Vector3.Distance(target, _lastTarget) > TargetMoveTolerance))
                Recalculate(self, target, now);

            if (!NavMeshAvailable) return Result = EnemyNavigationResult.NoNavMesh;
            if (!HasPath || CornerCount == 0) return Result = EnemyNavigationResult.NoPath;

            while (_cornerIndex < CornerCount - 1 &&
                CanAdvanceCorner(self))
                _cornerIndex++;

            Vector3 delta = _corners[_cornerIndex] - self;
            bool sameHeight = Mathf.Abs(delta.y) <= MaxSampleHeightDifference;
            delta.y = 0f;
            DistanceToCorner = delta.magnitude;
            if (_cornerIndex == CornerCount - 1 && DistanceToCorner <= CornerReach && sameHeight)
                return Result = LastStatus == NavMeshPathStatus.PathPartial
                    ? EnemyNavigationResult.PartialEnd : EnemyNavigationResult.Arrived;

            if (DistanceToCorner < 1e-4f) return Result = EnemyNavigationResult.NoPath;
            direction = delta / DistanceToCorner;
            return Result = EnemyNavigationResult.Moving;
        }

        public bool TryGetDirection(Vector3 self, Vector3 target, float now, out Vector3 direction) =>
            GetDirection(self, target, now, out direction) == EnemyNavigationResult.Moving;

        private bool CanAdvanceCorner(Vector3 self)
        {
            Vector3 delta = _corners[_cornerIndex] - self;
            float height = Mathf.Abs(delta.y);
            delta.y = 0f;
            // 精确到达时允许沿边界转弯；贴边 Raycast 会受浮点误差影响。
            if (delta.magnitude <= 0.02f && height <= MaxSampleHeightDifference) return true;
            return Vector3.Distance(self, _corners[_cornerIndex]) <= CornerReach &&
                TrySamplePosition(self, out NavMeshHit current) &&
                !NavMesh.Raycast(current.position, _corners[_cornerIndex + 1], out _, Filter);
        }

        /// <summary>分离力只能在可达的导航区域内调整方向，不能把角色推下平台或切穿墙角。</summary>
        public bool CanMove(Vector3 self, Vector3 direction, float distance)
        {
            if (!TrySamplePosition(self, out NavMeshHit start)) return false;
            Vector3 end = start.position + direction * distance;
            return !NavMesh.Raycast(start.position, end, out _, Filter);
        }

        private void Recalculate(Vector3 self, Vector3 target, float now)
        {
            RecalculationCount++;
            _forceRepath = false;
            _nextRepathTime = now + Mathf.Max(0.05f, RepathInterval);
            _lastTarget = target;
            _hasLastTarget = true;
            _cornerIndex = CornerCount = 0;
            HasPath = false;
            LastStatus = NavMeshPathStatus.PathInvalid;
            NavMeshAvailable = TrySamplePosition(self, out NavMeshHit start);
            if (!NavMeshAvailable || !TrySamplePosition(target, out NavMeshHit end)) return;

            if (!NavMesh.CalculatePath(start.position, end.position, Filter, _path)) return;
            LastStatus = _path.status;
            CornerCount = _path.GetCornersNonAlloc(_corners);
            // 不截断长路线。只有容量不足时才扩容，正常重算不产生 corners 数组。
            if (CornerCount == _corners.Length)
            {
                _corners = _path.corners;
                CornerCount = _corners.Length;
            }
            HasPath = CornerCount > 0 && LastStatus != NavMeshPathStatus.PathInvalid;
        }
    }
}
