#if UNITY_EDITOR
using Game.Items;
using Game.Player;
using UnityEditor;
using UnityEngine;

namespace Game.Editor
{
    [CustomEditor(typeof(PlayerInventory))]
    public sealed class PlayerInventoryEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.HelpBox("装备槽留空时自动使用四个默认槽。需要扩展时先写入默认槽，再添加唯一 SlotId、名称和对应装备类型。请在编辑模式调整配置。", MessageType.Info);
            using (new EditorGUI.DisabledScope(Application.isPlaying))
                if (GUILayout.Button("写入默认四个装备槽"))
                {
                    var holder = (PlayerInventory)target;
                    Undo.RecordObject(holder, "配置默认装备槽");
                    holder.UseDefaultEquipmentSlots();
                    EditorUtility.SetDirty(holder);
                }
        }
    }

    public static class InventoryAuthoringTools
    {
        [MenuItem("游戏/物品/给主角添加四件测试装备（Play）", priority = 43)]
        public static void AddTestEquipment()
        {
            if (!Application.isPlaying)
            { Debug.LogWarning("请进入 Play 后再添加测试装备，不会改动场景初始物品。"); return; }
            PlayerInventory holder = Object.FindFirstObjectByType<PlayerInventory>();
            if (holder == null) return;
            int added = 0;
            foreach (string name in new[] { "Head", "Torso", "Legs", "Feet" })
            {
                var item = AssetDatabase.LoadAssetAtPath<ItemDefinition>(
                    "Assets/_Project/Data/Items/DA_Item_Basic_" + name + ".asset");
                if (item != null) added += holder.Backpack.Add(item, 1);
            }
            Debug.Log($"已放入 {added}/4 件测试装备。未放入的没有被生成或扣除；背包满时请先腾位置。");
        }
    }
}
#endif
