using System;
using System.Collections.Generic;
using Game.World;
using UnityEngine;

namespace Game.Gameplay.Building
{
    /// <summary>
    /// 一个已放置部件的运行时记录。
    ///
    /// 【为什么需要这个组件 —— 这是一个真实的缺陷】
    /// 早先"已放置了什么"只存在于 <see cref="BuildAreaData"/> 里，而它是普通 C# 类，
    /// Unity【不会序列化】它。后果是：
    ///   编辑器生成场景时登记的"起步地板"，进 Play 之后就没了 ——
    ///   物体会显示出来，但数据层认为那格是空的（能重叠建、也拆不掉）。
    ///
    /// 所以让场景里的"起步部件"也挂一个本组件：它的值会被 Unity 序列化，
    /// 进 Play 后由 <see cref="BuildController"/> 读出来补登进 BuildAreaData。
    /// 这样编辑器期和运行期的占用状态就一致了。
    /// </summary>
    [DisallowMultipleComponent]
    public class PreplacedPart : MonoBehaviour
    {
        [Tooltip("部件标识，要和 BuildController 里某个 BuildablePart.PartId 对得上。")]
        [SerializeField] private string _partId = "floor";

        [Tooltip("是否是墙（占边）而不是地板（占格）。")]
        [SerializeField] private bool _isWall;

        [Tooltip("地板用：格子 X。墙忽略。")]
        [SerializeField] private int _cellX;

        [Tooltip("地板用：格子 Z。墙忽略。")]
        [SerializeField] private int _cellZ;

        [Tooltip("墙用：边的中点 X ×2。地板忽略。")]
        [SerializeField] private int _edgeMidX2;

        [Tooltip("墙用：边的中点 Z ×2。地板忽略。")]
        [SerializeField] private int _edgeMidZ2;

        [Tooltip("所属楼层。")]
        [SerializeField] private int _floor;

        public string PartId => _partId;
        public bool IsWall => _isWall;
        public int CellX => _cellX;
        public int CellZ => _cellZ;
        public int Floor => _floor;
        public BuildEdge Edge => new BuildEdge(_edgeMidX2, _edgeMidZ2, _floor);

        public void ConfigureFloor(int cellX, int cellZ, int floor, string partId)
        {
            _isWall = false;
            _cellX = cellX;
            _cellZ = cellZ;
            _floor = floor;
            _partId = partId;
        }

        public void ConfigureWall(BuildEdge edge, string partId)
        {
            _isWall = true;
            _edgeMidX2 = edge.MidX2;
            _edgeMidZ2 = edge.MidZ2;
            _floor = edge.Floor;
            _partId = partId;
        }

        public override string ToString()
            => _isWall
                ? $"预置墙 {Edge} ← {_partId}"
                : $"预置地板 ({_cellX}, {_cellZ}, 层{_floor}) ← {_partId}";
    }
}
