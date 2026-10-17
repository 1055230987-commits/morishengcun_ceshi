#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// 一次性工程目录重构工具。
    ///
    /// 把所有自有内容从 Assets/Game 与 Assets/Scenes 迁移到 Assets/_Project 下，
    /// 并统一资产命名前缀（M_ / DA_ / SC_ / PF_ / VFX_）。
    ///
    /// 为什么用 AssetDatabase.MoveAsset 而不是文件系统移动：
    ///   - 它会把 .meta 一起搬走，**GUID 不变**，所以场景/材质/脚本引用全部保持有效
    ///   - 用文件系统直接 move 而不带 .meta，会导致引用全部丢失
    ///
    /// 这是纯目录重构，不增加任何新玩法。执行一次即可，之后可以删掉本文件。
    /// </summary>
    public static class ProjectLayoutMigration
    {
        private const string OldItemFolder = "Assets/Game/Items/Definitions";

        [MenuItem("游戏/工程/迁移到 _Project 结构（一次性）", priority = 100)]
        public static void Migrate()
        {
            List<string[]> moves = BuildMoveTable();

            // 第一步：先把所有目标目录建出来，并强制 Refresh 一次。
            // 为什么必须 Refresh：AssetDatabase.CreateFolder 新建的目录不会立刻对 MoveAsset 可见，
            // 直接移动会报 "Trying to move asset as a sub directory of a directory that does not exist"。
            // 本次踩过这个坑（场景迁移失败），所以改成"先建目录 → Refresh → 再统一移动"。
            HashSet<string> directories = new HashSet<string>();
            foreach (string[] pair in moves)
            {
                if (!AssetDatabase.LoadAssetAtPath<Object>(pair[0])) continue;

                string directory = System.IO.Path.GetDirectoryName(pair[1]);
                if (!string.IsNullOrEmpty(directory)) directories.Add(directory.Replace('\\', '/'));
            }

            foreach (string directory in directories)
            {
                EnsureDirectory(directory);
            }

            AssetDatabase.Refresh();

            int moved = 0;
            int skipped = 0;
            List<string> failures = new List<string>();

            foreach (string[] pair in moves)
            {
                string source = pair[0];
                string destination = pair[1];

                if (!AssetDatabase.LoadAssetAtPath<Object>(source))
                {
                    // 不存在就跳过（例如已经迁移过，或某个可选资产还没生成）
                    skipped++;
                    continue;
                }

                string error = AssetDatabase.MoveAsset(source, destination);
                if (string.IsNullOrEmpty(error))
                {
                    moved++;
                    Debug.Log($"[迁移] {source} → {destination}");
                }
                else
                {
                    failures.Add($"{source} → {destination}：{error}");
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[迁移] 完成：成功 {moved} 项，跳过 {skipped} 项（不存在或已迁移），失败 {failures.Count} 项。");

            foreach (string failure in failures)
            {
                Debug.LogError($"[迁移] 失败：{failure}");
            }

            if (failures.Count == 0)
            {
                Debug.Log("[迁移] 全部成功。下一步：确认 Console 无红字，删除残留的 Assets/Game 空目录，然后 Play 验证行为不变。");
            }
        }

        [MenuItem("游戏/工程/列出待迁移项（不执行）", priority = 101)]
        public static void DryRun()
        {
            int pending = 0;
            foreach (string[] pair in BuildMoveTable())
            {
                if (!AssetDatabase.LoadAssetAtPath<Object>(pair[0])) continue;

                pending++;
                Debug.Log($"[迁移-预演] {pair[0]}\n         → {pair[1]}");
            }

            Debug.Log($"[迁移-预演] 共 {pending} 项待迁移。");
        }

        private static List<string[]> BuildMoveTable()
        {
            List<string[]> moves = new List<string[]>();

            // ---------------- 核心框架 ----------------
            Add(moves, "Assets/Game/Save/WorldObjectId.cs", "Assets/_Project/Core/Runtime/Save/WorldObjectId.cs");

            // ---------------- 玩法：玩家 ----------------
            Add(moves, "Assets/Game/Player/Input/MovementIntent.cs", "Assets/_Project/Gameplay/Player/Input/MovementIntent.cs");
            Add(moves, "Assets/Game/Player/Input/ICharacterInputSource.cs", "Assets/_Project/Gameplay/Player/Input/ICharacterInputSource.cs");
            Add(moves, "Assets/Game/Player/Input/PlayerInputSource.cs", "Assets/_Project/Gameplay/Player/Input/PlayerInputSource.cs");
            Add(moves, "Assets/Game/Player/Movement/PlayerController.cs", "Assets/_Project/Gameplay/Player/Movement/PlayerController.cs");
            Add(moves, "Assets/Game/Player/Interaction/IInteractable.cs", "Assets/_Project/Gameplay/Player/Interaction/IInteractable.cs");
            Add(moves, "Assets/Game/Player/Interaction/InteractionProbe.cs", "Assets/_Project/Gameplay/Player/Interaction/InteractionProbe.cs");
            Add(moves, "Assets/Game/Player/Interaction/InteractionHud.cs", "Assets/_Project/Gameplay/Player/Interaction/InteractionHud.cs");
            Add(moves, "Assets/Game/Player/Interaction/PlayerInventory.cs", "Assets/_Project/Gameplay/Player/Interaction/PlayerInventory.cs");

            // ---------------- 玩法：物品 ----------------
            Add(moves, "Assets/Game/Items/ItemDefinition.cs", "Assets/_Project/Gameplay/Items/Definitions/ItemDefinition.cs");
            Add(moves, "Assets/Game/Items/ItemStack.cs", "Assets/_Project/Gameplay/Items/Runtime/ItemStack.cs");
            Add(moves, "Assets/Game/Items/Inventory.cs", "Assets/_Project/Gameplay/Items/Runtime/Inventory.cs");
            Add(moves, "Assets/Game/Items/Definitions/Item_Wood.asset", "Assets/_Project/Data/Items/DA_Item_Wood.asset");
            Add(moves, "Assets/Game/Items/Definitions/Item_Stone.asset", "Assets/_Project/Data/Items/DA_Item_Stone.asset");

            // ---------------- 玩法：采集（原来的可交互物里的资源类） ----------------
            Add(moves, "Assets/Game/World/Interactables/InteractableBase.cs", "Assets/_Project/Gameplay/Player/Interaction/InteractableBase.cs");
            Add(moves, "Assets/Game/World/Interactables/InteractableTree.cs", "Assets/_Project/Gameplay/Gathering/InteractableTree.cs");
            Add(moves, "Assets/Game/World/Interactables/InteractableCrate.cs", "Assets/_Project/Gameplay/Gathering/InteractableCrate.cs");

            // ---------------- 世界 ----------------
            Add(moves, "Assets/Game/World/Area/MapAreaDefinition.cs", "Assets/_Project/World/Areas/Definitions/MapAreaDefinition.cs");
            Add(moves, "Assets/Game/World/Area/AreaBounds.cs", "Assets/_Project/World/Areas/Runtime/AreaBounds.cs");
            Add(moves, "Assets/Game/World/Area/Area_Prototype.asset", "Assets/_Project/Data/Areas/DA_Area_Prototype.asset");
            Add(moves, "Assets/Game/World/GreyboxPrimitives.cs", "Assets/_Project/World/Greybox/GreyboxPrimitives.cs");
            Add(moves, "Assets/Game/Time/TimeOfDaySystem.cs", "Assets/_Project/World/Time/TimeOfDaySystem.cs");
            Add(moves, "Assets/Game/Time/DayPhase.cs", "Assets/_Project/World/Time/DayPhase.cs");
            Add(moves, "Assets/Game/Time/TimeControlMode.cs", "Assets/_Project/World/Time/TimeControlMode.cs");
            Add(moves, "Assets/Game/Environment/DayNightLightingController.cs", "Assets/_Project/World/Environment/DayNightLightingController.cs");
            Add(moves, "Assets/Game/Environment/DayNightPostProcessing.cs", "Assets/_Project/World/Environment/DayNightPostProcessing.cs");
            Add(moves, "Assets/Game/Environment/DayNightProfile.asset", "Assets/_Project/Rendering/VolumeProfiles/DA_DayNight_Profile.asset");
            Add(moves, "Assets/Game/Environment/ProceduralSkyCelestial.shader", "Assets/_Project/Rendering/Sky/ProceduralSkyCelestial.shader");
            Add(moves, "Assets/Game/Environment/M_NightSkyCelestial.mat", "Assets/_Project/Rendering/Sky/M_Sky_Celestial.mat");

            // ---------------- 相机 ----------------
            Add(moves, "Assets/Game/Camera/TopDownCameraRig.cs", "Assets/_Project/Camera/Runtime/TopDownCameraRig.cs");
            Add(moves, "Assets/Game/Camera/CameraScreenAligner.cs", "Assets/_Project/Camera/Runtime/CameraScreenAligner.cs");

            // ---------------- 灰盒美术 ----------------
            Add(moves, "Assets/Game/World/Materials/M_GreyGround.mat", "Assets/_Project/Art/Environment/Terrain/Materials/M_Grey_Ground.mat");
            Add(moves, "Assets/Game/World/Materials/M_GreyStructure.mat", "Assets/_Project/Art/Environment/Buildings/Materials/M_Grey_Structure.mat");
            Add(moves, "Assets/Game/World/Materials/M_GreyProp.mat", "Assets/_Project/Art/Environment/Props/Materials/M_Grey_Prop.mat");

            // ---------------- 文档 ----------------
            Add(moves, "Assets/Game/README_原型说明.md", "Assets/_Project/Docs/README_原型说明.md");
            Add(moves, "Assets/Game/工程框架报告.md", "Assets/_Project/Docs/工程框架报告.md");
            Add(moves, "Assets/Game/编译异常证据.md", "Assets/_Project/Docs/编译异常证据.md");
            Add(moves, "Assets/Game/.keep", "Assets/_Project/Docs/.keep");

            // ---------------- 编辑器工具 ----------------
            Add(moves, "Assets/Game/Editor/GreyboxLevelBuilder.cs", "Assets/_Project/Editor/Greybox/GreyboxLevelBuilder.cs");
            Add(moves, "Assets/Game/Editor/GameItemTools.cs", "Assets/_Project/Editor/ItemTools/GameItemTools.cs");
            Add(moves, "Assets/Game/Editor/DayNightSceneTools.cs", "Assets/_Project/Editor/EnvironmentTools/DayNightSceneTools.cs");
            Add(moves, "Assets/Game/Editor/Game.Editor.asmdef", "Assets/_Project/Editor/Game.Editor.asmdef");

            // ---------------- asmdef（必须最后搬，避免中途出现"两个同名程序集"） ----------------
            Add(moves, "Assets/Game/Game.Runtime.asmdef", "Assets/_Project/Game.Runtime.asmdef");

            // ---------------- 场景 ----------------
            Add(moves, "Assets/Scenes/Prototype_Greybox.unity", "Assets/_Project/Scenes/Prototype/SC_Prototype_Greybox.unity");

            return moves;
        }

        private static void Add(List<string[]> moves, string source, string destination)
        {
            moves.Add(new[] { source, destination });
        }

        /// <summary>递归创建目录（AssetDatabase.CreateFolder 一次只能建一层）。</summary>
        private static void EnsureDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory) || AssetDatabase.IsValidFolder(directory)) return;

            string[] parts = directory.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }
    }
}
#endif
