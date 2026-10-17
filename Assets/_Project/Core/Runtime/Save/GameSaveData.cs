// 本文件开启可空引用类型分析（和 Health.cs / BuildConnectivity.cs 一致）。
#nullable enable

using System;
using System.Collections.Generic;

namespace Game.Save
{
    /// <summary>
    /// 存档的顶层数据。
    ///
    /// 【为什么用这么"扁"的结构，而不是直接序列化场景对象】
    /// 序列化 GameObject 会把场景结构写进存档 —— 换 Unity 版本、改一次物体层级，
    /// 旧存档就废了。所以这里只存【逻辑状态】：
    ///   - 玩家在哪、朝哪
    ///   - 背包里有什么
    ///   - 建造物占哪个格/哪条边、是什么部件、哪个建造区、哪一层
    /// 加载时按这些数据【重新生成】物体。这样存档与场景结构解耦，
    /// 以后改物体怎么摆都不会让旧存档失效。
    ///
    /// 【这个类型不依赖 UnityEngine】所以能被独立断言覆盖（往返一致性等）。
    /// 真正读写文件、换算世界坐标的部分在 SaveSystem 里。
    /// </summary>
    [Serializable]
    public class GameSaveData
    {
        /// <summary>存档格式版本。以后改结构时用它做兼容判断。</summary>
        public int Version = CurrentVersion;

        public const int CurrentVersion = 1;

        /// <summary>存档对应的地图 ID。加载时如果和当前地图不一致，要警告。</summary>
        public string MapId = string.Empty;

        /// <summary>保存时的游戏内天数与时刻（可选，用于以后还原昼夜）。</summary>
        public int DayNumber;

        /// <summary>保存时的一天内归一化时刻（0~1）。</summary>
        public float NormalizedTime;

        /// <summary>玩家位置与朝向。</summary>
        public PlayerSaveData Player = new PlayerSaveData();

        /// <summary>背包与口袋的物品。</summary>
        public List<ItemSaveData> Backpack = new List<ItemSaveData>();
        public List<ItemSaveData> Pocket = new List<ItemSaveData>();

        /// <summary>所有已放置的建造物。</summary>
        public List<PlacedPartSaveData> PlacedParts = new List<PlacedPartSaveData>();
    }

    /// <summary>玩家状态。</summary>
    [Serializable]
    public class PlayerSaveData
    {
        public float X;
        public float Y;
        public float Z;

        /// <summary>朝向（绕 Y 的欧拉角，度）。</summary>
        public float Yaw;

        /// <summary>当前生命值。0 表示没存（加载时用满血）。</summary>
        public float Health;
    }

    /// <summary>一件物品。</summary>
    [Serializable]
    public class ItemSaveData
    {
        /// <summary>物品的稳定 ID（ItemDefinition.ItemId），不是资产路径。</summary>
        public string ItemId = string.Empty;

        public int Amount;

        public ItemSaveData() { }

        public ItemSaveData(string itemId, int amount)
        {
            ItemId = itemId;
            Amount = amount;
        }
    }

    /// <summary>
    /// 一个已放置的建造物。
    ///
    /// 【为什么地板和墙共用一个结构】它们的区别只是"占格还是占边"，
    /// 用 IsWall 区分可以让存档格式统一、加载时一个循环处理完。
    /// 字段对齐 <see cref="Game.World.PlacedPartRecord"/>，转换是直接的。
    /// </summary>
    [Serializable]
    public class PlacedPartSaveData
    {
        /// <summary>所属建造区的标识（对应 BuildArea 的 AreaId）。</summary>
        public string AreaId = string.Empty;

        /// <summary>部件标识（对应 BuildController 里 BuildablePart.PartId）。</summary>
        public string PartId = string.Empty;

        /// <summary>true = 墙（占边），false = 地板（占格）。</summary>
        public bool IsWall;

        /// <summary>所属楼层。</summary>
        public int Floor;

        // ---- 地板用 ----
        public int CellX;
        public int CellZ;

        // ---- 墙用 ----
        /// <summary>边中点的 X ×2。</summary>
        public int EdgeMidX2;

        /// <summary>边中点的 Z ×2。</summary>
        public int EdgeMidZ2;

        /// <summary>
        /// 朝向（绕 Y 的欧拉角，度）。
        ///
        /// 【为什么现在就要存】当前墙是按边自动决定朝向的（沿 X 或沿 Z），
        /// 所以朝向可以从边的编码推出来。但以后会有"可旋转的家具"，
        /// 那时朝向就是独立信息了 —— 按要求先把字段留出来，
        /// 加载时如果发现它和边编码矛盾就以边为准（并给出警告）。
        /// </summary>
        public float Yaw;

        public override string ToString()
            => IsWall
                ? $"墙[{AreaId}] 层{Floor} 边({EdgeMidX2},{EdgeMidZ2}) ← {PartId}"
                : $"地板[{AreaId}] 层{Floor} 格({CellX},{CellZ}) ← {PartId}";
    }
}
