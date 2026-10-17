using System;
using System.Collections.Generic;
using Game.Player;
using Game.Save;
using Game.World;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    /// <summary>生成后配置交互层，构建时阻止重复或缺失的存档标识进入包体。</summary>
    public sealed class PrototypeSceneValidation : IProcessSceneWithReport
    {
        public const string InteractionLayer = "Interactable";
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null) return;
            List<string> errors = ValidateIds(scene);
            if (errors.Count != 0)
                throw new BuildFailedException(string.Join("\n", errors));
        }

        public static List<string> ValidateIds(Scene scene)
        {
            var errors = new List<string>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (WorldObjectId id in root.GetComponentsInChildren<WorldObjectId>(true))
            {
                if (string.IsNullOrWhiteSpace(id.ObjectId))
                    errors.Add($"{scene.name}/{id.name}: 缺少 WorldObjectId");
                else if (!ids.Add(id.ObjectId))
                    errors.Add($"{scene.name}/{id.name}: 重复 WorldObjectId '{id.ObjectId}'");
            }
            return errors;
        }

        [MenuItem("游戏/工程/校验当前场景 ID")]
        public static void ValidateCurrentScene()
        {
            List<string> errors = ValidateIds(SceneManager.GetActiveScene());
            if (errors.Count == 0) Debug.Log("[场景校验] ID 均非空且唯一。");
            else foreach (string error in errors) Debug.LogError(error);
        }

        // 仅用于整个原型重新生成之后；普通编辑/构建只校验，绝不静默改存档键。
        public static void FinalizeGeneratedScene(Scene scene)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            string map = string.IsNullOrEmpty(scene.name) ? "prototype" : scene.name;
            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (WorldObjectId id in root.GetComponentsInChildren<WorldObjectId>(true))
            {
                string original = string.IsNullOrWhiteSpace(id.ObjectId) ? id.ObjectType : id.ObjectId;
                string candidate = original;
                int sequence = 1;
                while (string.IsNullOrWhiteSpace(candidate) || !ids.Add(candidate))
                    candidate = $"{map}/{original}/generated-{sequence++:D4}";
                if (candidate == id.ObjectId) continue;
                id.SetId(candidate);
                EditorUtility.SetDirty(id);
            }
            ConfigureInteractionLayers(scene);
        }

        [MenuItem("游戏/工程/配置当前场景交互层")]
        public static void ConfigureCurrentScene()
        {
            ConfigureInteractionLayers(SceneManager.GetActiveScene());
        }

        public static void ConfigureInteractionLayers(Scene scene)
        {
            int layer = LayerMask.NameToLayer(InteractionLayer);
            if (layer < 0)
                throw new InvalidOperationException("请先在 Tags and Layers 中创建 Interactable 层。");
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (InteractableBase interactable in root.GetComponentsInChildren<InteractableBase>(true))
                foreach (Collider collider in interactable.GetComponentsInChildren<Collider>(true))
                {
                    if (collider.gameObject.layer == layer) continue;
                    Undo.RecordObject(collider.gameObject, "Configure interaction layer");
                    collider.gameObject.layer = layer;
                    EditorUtility.SetDirty(collider.gameObject);
                }
                foreach (InteractionProbe probe in root.GetComponentsInChildren<InteractionProbe>(true))
                {
                    Undo.RecordObject(probe, "Configure interaction mask");
                    probe.InteractableMask = 1 << layer;
                    EditorUtility.SetDirty(probe);
                }
            }
            EditorSceneManager.MarkSceneDirty(scene);
        }
    }
}
