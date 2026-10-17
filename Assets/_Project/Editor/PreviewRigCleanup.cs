#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using Game.Player;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// 预览装置的修复工具。
    ///
    /// 【背景】CharacterPreviewRig 用 GetOrCreateChild 复用同名子物体，但早期版本依赖一个
    /// 非序列化的 _built 字段 —— 脚本重载后该字段丢失，于是每次 Build 都 new 出另一套
    /// 相机和灯。场景里因此积累到 18 套（18 个相机 + 35 盏灯 = 71 个子物体）。
    ///
    /// 【为什么不能"按名字去重"】试过，而且【失败】。原因是：
    /// GetOrCreateChild 只保活它找到的【第一个】同名子物体，但每个名字的"第一个"
    /// 可能分属不同批次。于是"按名字各保留第一个"会拼出一套**跨批次的残缺组合** ——
    /// 实际结果就是保留了 active 的相机/主光/旋转轴，却保留了 active=0 的补光，
    /// 预览因此缺一盏灯而变黑（本工程实际发生过）。
    ///
    /// 【正确做法】整套重置：把这个装置的所有子物体全部删掉，再让
    /// CharacterPreviewRig.SetSourceFrom() 从零重建一套。这样结果只可能是"完整的一套"。
    ///
    /// 【为什么必须在编辑器里做、不能手改场景 YAML】
    /// 场景里每个 GameObject 的 m_Component、父物体的 m_Children 都靠 fileID 互相引用。
    /// 手删文档会留下悬空引用（MissingReference，甚至场景打开即报错）。
    /// 交给 Unity 的 DestroyImmediate 由引擎统一清理引用才安全。
    /// </summary>
    public static class PreviewRigCleanup
    {
        /// <summary>装置根节点的名字。与 GreyboxLevelBuilder.BuildCharacterPreview 一致。</summary>
        private const string PreviewRootName = "主角模型预览";

        // ------------------------------------------------------------------ 主入口：整套重置

        [MenuItem("游戏/工程/重置预览装置（删光重建）", priority = 103)]
        public static void ResetRig()
        {
            GameObject root = GameObject.Find(PreviewRootName);

            if (root == null)
            {
                Debug.LogWarning(
                    $"[预览重置] 场景里找不到「{PreviewRootName}」。\n" +
                    "请跑一次「游戏 → 灰盒 → 重建到当前场景」来生成它。");
                return;
            }

            CharacterPreviewRig rig = root.GetComponent<CharacterPreviewRig>();
            if (rig == null)
            {
                Debug.LogError($"[预览重置]「{PreviewRootName}」上没有 CharacterPreviewRig 组件，无法重建。");
                return;
            }

            // ---- 先把模型来源找出来，找不到就【不要删】----
            //
            // 这是上一版最大的问题：删完再调 SetSourceFrom，而 SetSourceFrom 在
            // source == null 时会【静默返回】—— 结果留下一个空装置，工具却报成功。
            // 所以先取来源，确认拿得到再动手。
            GameObject modelSource = FindModelSource();

            if (modelSource == null)
            {
                Debug.LogError(
                    "[预览重置] 中止：找不到模型来源。\n" +
                    "    需要场景里有一个 tag 为 Player 的物体（其下最好有名为「外观」的子物体）。\n" +
                    "    没有来源的话，清空装置只会让它彻底空掉，所以这次什么都不做。");
                return;
            }

            Renderer[] sourceRenderers = modelSource.GetComponentsInChildren<Renderer>(true);
            if (sourceRenderers.Length == 0)
            {
                Debug.LogError(
                    $"[预览重置] 中止：「{modelSource.name}」及其子物体里没有任何 Renderer，\n" +
                    "    克隆出来必然是空的。请先给玩家加占位外观（生成器会做这件事）。");
                return;
            }

            // ---- 1) 删光所有子物体 ----
            int removed = 0;

            // 用数组快照，避免边遍历边删
            Transform[] children = new Transform[rig.transform.childCount];
            for (int i = 0; i < rig.transform.childCount; i++) children[i] = rig.transform.GetChild(i);

            foreach (Transform child in children)
            {
                if (child == null) continue;
                Undo.DestroyObjectImmediate(child.gameObject);
                removed++;
            }

            // ---- 2) 从零重建 ----
            //
            // 【必须用 Rebuild 而不是 SetSourceFrom】
            // SetSourceFrom 内部第一句是 Build()，而 Build() 有 `_built` 守卫 ——
            // 同一会话里 _built 已经是 true，Build 会直接返回、什么都不建。
            // 结果就是：子物体被删掉了，相机/灯/旋转轴一个都没回来
            // （本工程实际发生过：自检报"没有生成「预览相机」"）。
            // Rebuild 会先清掉 _built 和所有引用，再走完整流程。
            bool rebuilt = rig.Rebuild(modelSource);

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

            if (!rebuilt)
            {
                Debug.LogError(
                    $"[预览重置] 已删除 {removed} 个子物体，但 Rebuild 报告失败（返回 false）。\n" +
                    "    可能原因：模型来源无效，或 gameObject.scene 不可用。\n" +
                    "    场景已标记为脏，请【不要保存】，把这个报错发出来。");
                return;
            }

            // ---- 3) 自检 ----
            string problem = VerifyRig(rig.transform, out string detail);

            if (problem != null)
            {
                Debug.LogError(
                    $"[预览重置] Rebuild 返回成功，但【自检失败】：\n" +
                    $"    {problem}\n{detail}\n" +
                    "场景已标记为脏，请【不要保存】，先把这个报错发出来。");
                return;
            }

            AssetDatabase.SaveAssets();

            Debug.Log(
                $"[预览重置] 成功：删除 {removed} 个子物体，重建后自检通过。\n{detail}\n" +
                "请按 Ctrl+S 保存场景，然后按 Tab 打开背包验证预览。");
        }

        /// <summary>
        /// 自检重建结果。返回 null 表示通过，否则返回问题描述。
        ///
        /// 【为什么必须有这一步】上一版工具删完就调 SetSourceFrom 然后直接报"成功"，
        /// 而 SetSourceFrom 在来源为空时会静默返回 —— 于是工具"成功地"留下了一个空装置。
        /// 工具必须验证自己的结果，不能只看自己的调用有没有抛异常。
        /// </summary>
        private static string VerifyRig(Transform root, out string detail)
        {
            var sb = new StringBuilder();

            Transform camera = null, pivot = null, mainLight = null, fillLight = null;
            int extraCameras = 0, extraPivots = 0;

            foreach (Transform child in root)
            {
                switch (child.name)
                {
                    case "预览相机":
                        if (camera == null) camera = child; else extraCameras++;
                        break;
                    case "旋转轴":
                        if (pivot == null) pivot = child; else extraPivots++;
                        break;
                    case "预览主光":
                        if (mainLight == null) mainLight = child;
                        break;
                    case "预览补光":
                        if (fillLight == null) fillLight = child;
                        break;
                }

                sb.AppendLine($"    {child.name}（active={child.gameObject.activeSelf}，子物体 {child.childCount} 个）");
            }

            if (camera == null) { detail = sb.ToString(); return "没有生成「预览相机」"; }
            if (pivot == null) { detail = sb.ToString(); return "没有生成「旋转轴」"; }
            if (mainLight == null) { detail = sb.ToString(); return "没有生成「预览主光」"; }
            if (fillLight == null) { detail = sb.ToString(); return "没有生成「预览补光」"; }

            Transform model = null;
            foreach (Transform child in pivot)
            {
                if (child.name == "预览模型") { model = child; break; }
            }

            if (model == null) { detail = sb.ToString(); return "「旋转轴」下没有「预览模型」"; }

            int modelRenderers = model.GetComponentsInChildren<Renderer>(true).Length;
            if (modelRenderers == 0) { detail = sb.ToString(); return "「预览模型」下没有任何 Renderer"; }

            if (extraCameras > 0 || extraPivots > 0)
            {
                detail = sb.ToString();
                return $"重建后仍有重复：相机多 {extraCameras} 个、旋转轴多 {extraPivots} 个";
            }

            sb.AppendLine($"    预览模型：{modelRenderers} 个 Renderer");

            Camera cam = camera.GetComponent<Camera>();
            if (cam != null)
            {
                sb.AppendLine($"    相机：targetTexture={(cam.targetTexture != null ? cam.targetTexture.name : "【null】")}，" +
                              $"cullingMask=0x{cam.cullingMask:X8}");
                if (cam.targetTexture == null) { detail = sb.ToString(); return "相机的 targetTexture 是 null"; }
            }

            detail = sb.ToString();
            return null;
        }

        /// <summary>找玩家的「外观」子物体作为预览模型来源（与生成器同一套逻辑）。</summary>
        private static GameObject FindModelSource()
        {
            GameObject player = GameObject.FindGameObjectWithTag("Player");
            if (player == null) return null;

            Transform visual = player.transform.Find("外观");
            return visual != null ? visual.gameObject : player;
        }

        // ------------------------------------------------------------------ 诊断：只读

        [MenuItem("游戏/工程/统计预览装置重复情况（只读）", priority = 104)]
        public static void Report()
        {
            GameObject root = GameObject.Find(PreviewRootName);

            if (root == null)
            {
                Debug.LogWarning($"[预览统计] 场景里找不到「{PreviewRootName}」。");
                return;
            }

            CharacterPreviewRig rig = root.GetComponent<CharacterPreviewRig>();
            if (rig == null)
            {
                Debug.LogWarning($"[预览统计]「{PreviewRootName}」上没有 CharacterPreviewRig 组件。");
                return;
            }

            var counts = new Dictionary<string, int>();
            var activeCounts = new Dictionary<string, int>();

            foreach (Transform child in rig.transform)
            {
                string key = child.name;
                counts.TryGetValue(key, out int n);
                counts[key] = n + 1;

                if (child.gameObject.activeSelf)
                {
                    activeCounts.TryGetValue(key, out int a);
                    activeCounts[key] = a + 1;
                }
            }

            var report = new StringBuilder();
            int redundant = 0;

            foreach (var pair in counts)
            {
                activeCounts.TryGetValue(pair.Key, out int active);
                int extra = pair.Value - 1;
                redundant += Mathf.Max(0, extra);

                report.AppendLine(
                    $"    {pair.Key}：{pair.Value} 个（其中 active 的 {active} 个）" +
                    (extra > 0 ? $" ← 多出 {extra} 个" : ""));
            }

            Debug.Log(
                $"[预览统计] 装置「{root.name}」：\n{report}\n" +
                $"合计多出 {redundant} 个子物体。\n" +
                (redundant > 0
                    ? "要修复请跑「游戏 → 工程 → 重置预览装置（删光重建）」。\n" +
                      "注意：不要试图只删除多出来的部分 —— 每个名字的 active 状态可能分属不同批次，" +
                      "按名字各留一个会拼出一套残缺组合（预览会缺灯变黑，本工程实际踩过）。"
                    : "（无冗余，装置是干净的。）"));
        }
    }
}
#endif
