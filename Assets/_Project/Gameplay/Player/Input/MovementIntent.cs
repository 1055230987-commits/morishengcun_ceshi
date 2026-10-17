using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// 角色移动意图。
    /// 这是「输入层」和「移动层」之间唯一的数据契约：
    /// 键盘/手柄/摇杆/AI/网络指令都只需要构造出这个结构，角色本身不关心它从哪来。
    /// </summary>
    [System.Serializable]
    public struct MovementIntent
    {
        /// <summary>世界空间方向，长度 0~1（长度即移动强度，便于以后接摇杆轻推/受伤减速）。</summary>
        public Vector3 Direction;

        /// <summary>速度倍率（冲刺、负重、受伤等状态由上层决定，不写死在移动逻辑里）。</summary>
        public float SpeedMultiplier;

        /// <summary>是否强制朝向某个世界方向（远程攻击/交互时用）；HasFacingDirection 为 false 时按移动方向转身。</summary>
        public bool HasFacingDirection;
        public Vector3 FacingDirection;

        /// <summary>本帧是否请求交互（例如按下了 E）。</summary>
        public bool InteractPressed;

        /// <summary>本帧是否有有效的准心/指针位置（瞄准、鼠标交互用）。</summary>
        public bool HasAimScreenPosition;
        public Vector2 AimScreenPosition;

        public static MovementIntent None
        {
            get
            {
                MovementIntent intent = default;
                intent.SpeedMultiplier = 0f;
                return intent;
            }
        }

        public Vector2 DirectionXZ
        {
            get
            {
                Vector2 flat = new Vector2(Direction.x, Direction.z);
                return flat.sqrMagnitude > 1f ? flat.normalized : flat;
            }
        }
    }
}
