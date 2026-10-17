using UnityEngine;
using UnityEngine.Events;

namespace Game.World
{
    /// <summary>
    /// 树的倒下特效挂接器。
    ///
    /// 这个脚本【不做任何特效】，它只负责"在正确的时机、正确的位置，生成你自己做的预制体"。
    /// 特效本身（粒子系统 / VFX Graph / 任何东西）完全由你在 Unity 里自己搭。
    ///
    /// 用法：
    ///   1. 把本组件挂在任意一棵树上（也可以挂在多棵树上，或者挂在一个空物体上并用 _tree 指定树）
    ///   2. 把你自己做的特效预制体拖到 _effectPrefab
    ///   3. 需要的话调 _spawnDelay 控制出现时机
    ///
    /// 位置说明：生成点默认为【树干根部】，也就是树倒下时接触地面、最该起烟的位置。
    /// 想让它出现在树干中段或顶部，用 _localOffset 调（Y 为正 = 往上）。
    /// </summary>
    [DisallowMultipleComponent]
    public class TreeFallEffectHook : MonoBehaviour
    {
        [Header("挂哪棵树")]
        [Tooltip("留空 = 用本物体上的 InteractableTree。也可以把别的树拖进来。")]
        [SerializeField] private InteractableTree _tree;

        [Header("要生成的特效")]
        [Tooltip("你自己做的特效预制体。留空则什么都不做。")]
        [SerializeField] private GameObject _effectPrefab;

        [Tooltip("延迟多少秒生成（从树开始倒的那一刻算起）。\n" +
                 "0 = 立刻；0.2~0.4 比较像「砸地起尘」；想等倒完再起尘就填更大的值。")]
        [SerializeField, Min(0f)] private float _spawnDelay = 0.25f;

        [Tooltip("生成点相对树干根部的偏移。Y 为正 = 往树干上方偏。\n" +
                 "想让烟覆盖整条树干，建议这样做：把粒子系统的 Shape 设成 Box、并自己调好尺寸，\n" +
                 "而不是靠这个偏移 —— 位置只有一个点，形状得由粒子系统自己决定。")]
        [SerializeField] private Vector3 _localOffset = Vector3.zero;

        [Header("生成方式")]
        [Tooltip("勾上 = 生成的特效作为树的子物体（会跟着树的旋转一起倒）。\n" +
                 "取消 = 留在世界坐标（推荐：树随后会被隐藏/销毁，挂在树下面会把特效一起带走）。")]
        [SerializeField] private bool _attachToTree = false;

        [Tooltip("生成后多少秒销毁特效。0 = 不自动销毁（适合让粒子系统自己控制生命周期）。")]
        [SerializeField, Min(0f)] private float _destroyAfter = 0f;

        [Tooltip("勾上 = 只在第一次倒下时生成一次。")]
        [SerializeField] private bool _onlyOnce = true;

        [Header("额外事件（可选，不用写代码）")]
        [Tooltip("在生成特效的同一时刻一并触发。可以在这里挂音效、屏幕震动、其他特效等。")]
        [SerializeField] private UnityEvent _onTriggered;

        private bool _hasTriggered;

        public InteractableTree Tree => _tree;

        private void Reset()
        {
            // 在 Inspector 里刚添加本组件时，自动关联同一物体上的树。
            _tree = GetComponent<InteractableTree>();
        }

        private void OnEnable()
        {
            if (_tree == null) _tree = GetComponent<InteractableTree>();
            if (_tree == null)
            {
                Debug.LogWarning(
                    $"[{nameof(TreeFallEffectHook)}]「{name}」没有指定 InteractableTree，特效不会触发。", this);
                return;
            }

            _tree.OnFellStarted += HandleFellStarted;
        }

        private void OnDisable()
        {
            if (_tree != null) _tree.OnFellStarted -= HandleFellStarted;
        }

        private void HandleFellStarted(Vector3 treeBasePosition)
        {
            if (_effectPrefab == null) return;
            if (_onlyOnce && _hasTriggered) return;

            _hasTriggered = true;

            if (_spawnDelay <= 0f)
            {
                Spawn(treeBasePosition);
            }
            else
            {
                StartCoroutine(SpawnDelayed(treeBasePosition, _spawnDelay));
            }
        }

        private System.Collections.IEnumerator SpawnDelayed(Vector3 position, float delay)
        {
            yield return new WaitForSeconds(delay);
            Spawn(position);
        }

        private void Spawn(Vector3 treeBasePosition)
        {
            if (_effectPrefab == null) return;

            Vector3 position = treeBasePosition + _localOffset;
            Transform parent = _attachToTree && _tree != null ? _tree.transform : null;

            GameObject instance = parent != null
                ? Instantiate(_effectPrefab, position, Quaternion.identity, parent)
                : Instantiate(_effectPrefab, position, Quaternion.identity);

            _onTriggered?.Invoke();

            if (_destroyAfter > 0f) Destroy(instance, _destroyAfter);
        }

        /// <summary>手动触发一次（供调试/其他脚本调用）。</summary>
        public void Trigger()
        {
            _hasTriggered = false;
            HandleFellStarted(_tree != null ? _tree.transform.position : transform.position);
        }
    }
}
