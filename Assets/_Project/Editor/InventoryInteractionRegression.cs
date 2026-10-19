#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using System.Text;
using Game.Items;
using Game.Player;
using Game.Save;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.Editor
{
    public static class InventoryInteractionRegression
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Set(object target, string field, object value) =>
            target.GetType().GetField(field, Private).SetValue(target, value);
        private static void Call(object target, string method) => target.GetType().GetMethod(method, Private).Invoke(target, null);

        [MenuItem("游戏/工程/验证背包拖放、装备与预览（独立测试场景）", false, 114)]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            Scene previous = SceneManager.GetActiveScene();
            string folder = "Assets/__InventoryRegression_" + Guid.NewGuid().ToString("N");
            if (string.IsNullOrEmpty(previous.path))
            {
                if (!Application.isBatchMode) { Debug.LogWarning("请先命名并保存当前场景。"); return; }
                AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
                EditorSceneManager.SaveScene(previous, folder + "/BatchBaseline.unity");
            }
            Scene test = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var report = new StringBuilder("Inventory native regression " + DateTime.Now.ToString("O") + "\n");
            int passed = 0, failed = 0;
            InputMode mode = InputModeState.Current;
            bool cursor = Cursor.visible;
            CursorLockMode cursorLock = Cursor.lockState;
            Mouse previousMouse = Mouse.current;
            Mouse mouse = null;
            InputSettings previousSettings = InputSystem.settings;
            InputSettings testSettings = null;
            string jsonPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "OptimizationReview", "inventory-test.json");
            void Check(bool ok, string label)
            { report.AppendLine((ok ? "PASS " : "FAIL ") + label); if (ok) passed++; else failed++; }
            try
            {
                SceneManager.SetActiveScene(test);
                InputModeState.SetPlayerDead(false);
                var player = new GameObject("Inventory fixture");
                var holder = player.AddComponent<PlayerInventory>();
                var ui = player.AddComponent<InventoryScreen>();
                Set(ui, "_playerInventory", holder);
                // 在编辑模式执行游戏的 Update 输入链路，设置只作用于临时副本。
                testSettings = Object.Instantiate(previousSettings);
                testSettings.SetInternalFeatureFlag("RUN_PLAYER_UPDATES_IN_EDIT_MODE", true);
                InputSystem.settings = testSettings;
                mouse = InputSystem.AddDevice<Mouse>();
                var material = AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/Data/Items/DA_Item_Wood.asset");
                var hat = AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/Data/Items/DA_Item_Basic_Head.asset");
                var shirt = AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/Data/Items/DA_Item_Basic_Torso.asset");
                Check(material != null && hat != null && shirt != null && holder.Equipment.Capacity == 4,
                    "默认四个装备槽与物品资产加载正确");
                Check(hat.EquipmentType == holder.Equipment.Definitions[0].AcceptedType,
                    "帽子物品引用正确的装备类型");
                var originalSlots = new System.Collections.Generic.List<EquipmentSlot>(holder.Equipment.Definitions);
                var extendedSlots = new System.Collections.Generic.List<EquipmentSlot>(originalSlots);
                extendedSlots.Add(new EquipmentSlot("extra_head", "备用帽槽", originalSlots[0].AcceptedType));
                Set(holder,"_equipmentSlots",extendedSlots); Call(holder,"OnValidate");
                Check(holder.Equipment.Capacity == 5 && holder.Equipment.FindSlot("extra_head") == 4,
                    "编辑配置可新增装备槽，空缓存不会固化成四槽");
                Set(holder,"_equipmentSlots",originalSlots); Call(holder,"OnValidate");
                ui.SetOpen(true);
                void Frame(Vector2 point, bool held)
                {
                    Vector2 raw = new Vector2(point.x, Screen.height - point.y);
                    InputSystem.QueueStateEvent(mouse, new MouseState { position = raw, buttons = held ? (ushort)1 : (ushort)0 });
                    InputSystem.Update();
                    Call(ui, "Update");
                }
                void Drag(InventoryScreen.ContainerKind source, int sourceIndex, InventoryScreen.ContainerKind target, int targetIndex)
                {
                    Vector2 from = ui.GetSlotRect(source, sourceIndex).center;
                    Vector2 to = ui.GetSlotRect(target, targetIndex).center;
                    Frame(from, true); Frame(to, true); Frame(to, false);
                }
                holder.Backpack.Add(material, 12);
                Drag(InventoryScreen.ContainerKind.Backpack,0,InventoryScreen.ContainerKind.Backpack,15);
                Check(holder.Backpack.GetSlot(0).IsEmpty && holder.Backpack.GetSlot(15).Amount == 12,
                    "真实鼠标 Update 链路拖到第 16 格，保留源空位");
                Drag(InventoryScreen.ContainerKind.Backpack,15,InventoryScreen.ContainerKind.Pocket,3);
                Check(holder.Pocket.GetSlot(3).Amount == 12 && holder.Backpack.IsEmpty,
                    "跨容器落入指定口袋格");
                holder.Backpack.Add(hat,1); holder.Backpack.Add(shirt,1);
                Drag(InventoryScreen.ContainerKind.Backpack,0,InventoryScreen.ContainerKind.Equipment,0);
                Check(holder.Equipment.GetSlot(0).Item == hat && holder.Backpack.GetSlot(1).Item == shirt,
                    "拖动装备成功且其他格子不移动");
                Drag(InventoryScreen.ContainerKind.Backpack,1,InventoryScreen.ContainerKind.Equipment,2);
                Check(holder.Backpack.GetSlot(1).Item == shirt && holder.Equipment.GetSlot(2).IsEmpty,
                    "错误类型实际鼠标拖放被拒绝");
                Drag(InventoryScreen.ContainerKind.Backpack,1,InventoryScreen.ContainerKind.Equipment,1);
                Check(holder.Equipment.GetSlot(1).Item == shirt, "衣服只能装备到衣服槽");
                Drag(InventoryScreen.ContainerKind.Equipment,0,InventoryScreen.ContainerKind.Backpack,10);
                Check(holder.Backpack.GetSlot(10).Item == hat && holder.Equipment.GetSlot(0).IsEmpty,
                    "鼠标拖卸装备到指定空格");

                // 不依赖 GPU 的预览控制夹具：与正式组件相同的指针、偏航和插值方法。
                var previewObject = new GameObject("Preview fixture");
                previewObject.SetActive(false);
                var preview = previewObject.AddComponent<CharacterPreviewRig>();
                Set(preview,"_built",true);
                Set(preview,"_renderTexture",new RenderTexture(16,16,0));
                var pivot = new GameObject("Pivot"); pivot.transform.SetParent(previewObject.transform,false);
                Set(preview,"_pivot",pivot.transform);
                Set(ui,"_preview",preview);
                Vector2 center = ui.PreviewRect.center;
                Frame(center,true);
                Frame(center + Vector2.right * 25f,true);
                float first = preview.Yaw;
                Frame(center + Vector2.right * 50f,true);
                Check(preview.Yaw < first && first < 0f, "连续多个 Update 不会重置预览拖动");
                float beforeOutside = preview.Yaw;
                Frame(new Vector2(ui.PreviewRect.xMax + 120f,center.y),true);
                Check(preview.Yaw < beforeOutside, "指针拖出预览边界后仍持续旋转");
                float beforeReverse = preview.Yaw;
                Frame(center - Vector2.right * 80f,true);
                Check(preview.Yaw > beforeReverse, "不松手也能反向旋转");
                Frame(center,false);
                float released = preview.Yaw;
                Frame(center + Vector2.right * 90f,false);
                Check(Mathf.Approximately(released,preview.Yaw), "松手后指针移动不再旋转模型");
                Set(preview,"_yaw",0f); Set(preview,"_targetYaw",720f);
                var advance = typeof(CharacterPreviewRig).GetMethod("AdvanceRotation",Private);
                advance.Invoke(preview,new object[]{1f/60f});
                Check(preview.Yaw > 0f && preview.Yaw < 720f, "旋转平滑推进而非瞬间跳转");
                for(int i=0;i<120;i++) advance.Invoke(preview,new object[]{1f/60f});
                Check(Mathf.Abs(preview.Yaw-720f)<0.02f, "可以连续旋转多圈，不受正负 180 度限制");
                Frame(center,true);
                typeof(InventoryScreen).GetMethod("OnApplicationFocus",Private).Invoke(ui,new object[]{false});
                float lost = preview.Yaw;
                Frame(center + Vector2.right * 20f,true);
                Check(Mathf.Approximately(lost,preview.Yaw), "失焦取消拖动，不会粘住鼠标");
                Frame(center,false);

                var save = player.AddComponent<GameSaveController>();
                GameSaveData data = save.Capture();
                Directory.CreateDirectory(Path.GetDirectoryName(jsonPath));
                File.WriteAllText(jsonPath,JsonUtility.ToJson(data,true));
                var loaded = JsonUtility.FromJson<GameSaveData>(File.ReadAllText(jsonPath));
                Check(loaded.Version == 2 && loaded.Equipment.Count == 1 && loaded.Equipment[0].SlotId == "torso",
                    "真实 JSON 文件保存装备稳定槽 ID");
                Check(loaded.Backpack.Count == 1 && loaded.Backpack[0].SlotIndex == 10 && loaded.Pocket[0].SlotIndex == 3,
                    "真实 JSON 文件保存背包与口袋的任意格子位置");
                holder.Backpack.Clear(); holder.Pocket.Clear(); holder.Equipment.Clear();
                CallApply(save,loaded);
                Check(holder.Backpack.GetSlot(10).Item == hat && holder.Pocket.GetSlot(3).Amount == 12 &&
                    holder.Equipment.GetSlot(1).Item == shirt, "真实控制器恢复三个容器的位置与装备");
                CallApply(save,loaded);
                Check(holder.Backpack.CountOf(hat) == 1 && holder.Equipment.CountOf(shirt) == 1,
                    "重复恢复不复制物品或装备");
                string serializedHolder = EditorJsonUtility.ToJson(holder);
                Set(holder,"_equipment",null);
                EditorJsonUtility.FromJsonOverwrite(serializedHolder,holder);
                Check(holder.Equipment.Capacity == 4 && holder.Equipment.GetSlot(1).Item == shirt &&
                    holder.Backpack.GetSlot(10).Item == hat,"Unity 组件序列化保留装备定义和稀疏格子");
                loaded.Equipment[0].SlotId="head";
                Check(!save.RunPrecheck(loaded).CanProceed && holder.Equipment.GetSlot(1).Item == shirt,
                    "坏装备存档在清空前被拒绝，当前装备保留");
                var legacy = JsonUtility.FromJson<GameSaveData>("{\"Version\":1,\"Backpack\":[{\"ItemId\":\"wood\",\"Amount\":4}],\"Pocket\":[]}");
                Check(save.RunPrecheck(legacy).CanProceed,"真实 v1 JSON 缺少新字段仍兼容");
            }
            catch(Exception error) { failed++; report.AppendLine("FAIL "+error); }
            finally
            {
                if (mouse != null) InputSystem.RemoveDevice(mouse);
                InputSystem.settings = previousSettings;
                if (testSettings != null) Object.DestroyImmediate(testSettings);
                previousMouse?.MakeCurrent();
                if(previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                if(test.IsValid() && test.isLoaded) EditorSceneManager.CloseScene(test,true);
                InputModeState.SetPlayerDead(mode==InputMode.Dead); InputModeState.Current=mode;
                Cursor.lockState=cursorLock; Cursor.visible=cursor;
                if(AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                if(File.Exists(jsonPath)) File.Delete(jsonPath);
            }
            report.AppendLine($"RESULT passed={passed} failed={failed}");
            string output=Path.Combine(Path.GetDirectoryName(Application.dataPath),"OptimizationReview","inventory-results.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(output)); File.WriteAllText(output,report.ToString());
            if(failed>0) Debug.LogError(report.ToString()); else Debug.Log(report.ToString());
            if(Application.isBatchMode) EditorApplication.Exit(failed==0?0:1);
        }

        private static void CallApply(GameSaveController controller,GameSaveData data) =>
            typeof(GameSaveController).GetMethod("ApplyInventory",Private).Invoke(controller,new object[]{data});
    }
}
#endif
