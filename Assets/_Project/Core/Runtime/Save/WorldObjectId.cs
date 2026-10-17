using UnityEngine;

namespace Game.Save
{
    /// <summary>
    /// 世界里可被存档/联机引用的对象标识。
    ///
    /// 原则：**不要用 GetInstanceID()，也不要用对象在 Hierarchy 里的路径** 作为存档键，
    /// 它们在不同运行、不同客户端、不同加载顺序下都会变。
    /// 这里由作者或生成器写入一个稳定字符串（推荐格式：地图ID/类型/序号，例如 "home/tree/007"）。
    ///
    /// 当前阶段这个组件只是为了「结构上先立住」，
    /// 灰盒对象已经挂上它，但完整的存档系统、ID 分配工具、对象生成/回收都留到后续阶段。
    /// </summary>
    [DisallowMultipleComponent]
    public class WorldObjectId : MonoBehaviour
    {
        [Tooltip("稳定唯一 ID。同一次存档内不可重复，跨版本不可改名。")]
        [SerializeField] private string _objectId;

        [Tooltip("对象类型标签（tree / crate / wall / npc ...），方便按类型批量保存与生成。")]
        [SerializeField] private string _objectType = "generic";

        public string ObjectId => _objectId;
        public string ObjectType => _objectType;

        public bool HasId => !string.IsNullOrEmpty(_objectId);

        public void SetId(string objectId, string objectType = null)
        {
            _objectId = objectId;
            if (!string.IsNullOrEmpty(objectType))
            {
                _objectType = objectType;
            }
        }
    }
}
