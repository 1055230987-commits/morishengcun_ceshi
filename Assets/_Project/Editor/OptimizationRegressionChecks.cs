using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game.Environment;
using Game.GameTime;
using Game.Items;
using Game.Player;
using Game.Rendering;
using Game.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>仅在独立验证副本中通过 -executeMethod 调用，不改写原型场景。</summary>
    public static class OptimizationRegressionChecks
    {
        private static readonly List<Object> Cleanup = new List<Object>();
        private static readonly List<string> Results = new List<string>();
        private static int _failures;

        public static void RunBatch()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("请在独立项目副本的 batchmode 中运行。");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Run("CratePartialLoot", CratePartialLoot);
            Run("SupplyCrateScale", SupplyCrateScale);
            Run("TreePartialYieldAndReset", TreePartialYieldAndReset);
            Run("MaterialRestoreAndDisposal", MaterialRestoreAndDisposal);
            Run("VolumeProfileIsolation", VolumeProfileIsolation);
            Run("DenseInteractionQuery", DenseInteractionQuery);
            Run("LightingSubscriptionLifecycle", LightingSubscriptionLifecycle);
            Run("SkyDirectionsAndMaterialIsolation", SkyDirectionsAndMaterialIsolation);
            Run("SavedSceneIntegrity", SavedSceneIntegrity);
            string file = Path.Combine(Path.GetDirectoryName(Application.dataPath), "regression-results.txt");
            File.WriteAllLines(file, Results);
            Debug.Log($"OPTIMIZATION REGRESSION: {Results.Count - _failures}/{Results.Count} passed");
            EditorApplication.Exit(_failures == 0 ? 0 : 1);
        }

        private static void Run(string name, Action test)
        {
            try { test(); Results.Add("PASS " + name); }
            catch (Exception ex)
            {
                _failures++;
                Results.Add("FAIL " + name + ": " + ex);
                Debug.LogException(ex);
            }
            finally
            {
                for (int i = Cleanup.Count - 1; i >= 0; i--)
                    if (Cleanup[i] != null) Object.DestroyImmediate(Cleanup[i]);
                Cleanup.Clear();
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static GameObject Go(string name)
        {
            var go = new GameObject(name);
            Cleanup.Add(go);
            return go;
        }

        private static ItemDefinition Item()
        {
            var item = ItemDefinition.CreateRuntime("test", "Test", 99);
            Cleanup.Add(item);
            return item;
        }

        private static object Invoke(object target, string method, params object[] args)
        {
            return target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(target, args);
        }

        private static void Set(object target, string field, object value)
        {
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        }

        private static PlayerInventory Actor()
        {
            PlayerInventory actor = Go("Actor").AddComponent<PlayerInventory>();
            actor.Backpack.Capacity = 1;
            return actor;
        }

        private static void CratePartialLoot()
        {
            ItemDefinition item = Item();
            PlayerInventory actor = Actor();
            actor.Backpack.Add(item, 98);
            var crate = Go("Crate").AddComponent<InteractableCrate>();
            crate.ConfigureLoot(item, 2, true);
            Assert(crate.Interact(actor.gameObject).Succeeded, "First partial pickup failed");
            Assert(actor.Backpack.CountOf(item) == 99 && crate.CanInteract(actor.gameObject), "Remainder was lost");
            Assert(!crate.Interact(actor.gameObject).Succeeded, "Full inventory must reject pickup");
            actor.Backpack.Clear();
            Assert(crate.Interact(actor.gameObject).Succeeded, "Remainder pickup failed");
            Assert(actor.Backpack.CountOf(item) == 1 && !crate.CanInteract(actor.gameObject), "Loot was duplicated");
        }

        private static void SupplyCrateScale()
        {
            var item = Item();
            var actor = Actor();
            var crate = Go("Supply").AddComponent<InteractableCrate>();
            crate.ConfigureLoot(item, 2, false);
            for (int i = 0; i < 3; i++)
                Assert(crate.Interact(actor.gameObject).Succeeded, "Supply must refill");
            Assert(actor.Backpack.CountOf(item) == 6, "Incorrect supply count");
            Assert(Mathf.Approximately(crate.transform.localScale.y, 0.85f), "Supply shrank repeatedly");
        }

        private static void TreePartialYieldAndReset()
        {
            var item = Item();
            var actor = Actor();
            var tree = Go("Tree").AddComponent<InteractableTree>();
            tree.ConfigureYield(item, 2, 10);
            tree.transform.localPosition = new Vector3(2, 3, 4);
            Invoke(tree, "Awake");
            actor.Backpack.Add(item, 98);
            Assert(tree.Interact(actor.gameObject).Succeeded, "Partial chop failed");
            Assert(tree.ChopsRemaining == 10, "Partial yield consumed durability");
            actor.Backpack.Clear();
            tree.Interact(actor.gameObject);
            Assert(actor.Backpack.CountOf(item) == 1 && tree.ChopsRemaining == 9, "Pending yield incorrect");
            tree.transform.localPosition = Vector3.zero;
            tree.ResetTree();
            Assert(tree.transform.localPosition == new Vector3(2, 3, 4), "Tree position not restored");
            Assert(tree.ChopsRemaining == 10, "Tree durability not restored");
        }

        private static void MaterialRestoreAndDisposal()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert(shader != null, "URP Lit missing");
            var original = new Material(shader);
            Cleanup.Add(original);
            original.SetFloat("_ZWrite", 1);
            original.renderQueue = 2000;
            var renderer = Go("Renderer").AddComponent<MeshRenderer>();
            renderer.sharedMaterial = original;
            var fade = new RendererFadeMaterials(new Renderer[] { renderer });
            fade.Apply(0.2f);
            Material instance = renderer.sharedMaterial;
            Assert(instance != original && instance.renderQueue == 3000, "No separate fade material");
            fade.Restore();
            Assert(renderer.sharedMaterial == original && original.GetFloat("_ZWrite") == 1 &&
                original.renderQueue == 2000, "Original render state changed");
            fade.Apply(0.5f);
            Assert(renderer.sharedMaterial == instance, "Fade material not reused");
            fade.Dispose();
            Assert(renderer.sharedMaterial == original && instance == null, "Fade material leaked");
        }

        private static void VolumeProfileIsolation()
        {
            var source = ScriptableObject.CreateInstance<VolumeProfile>();
            Cleanup.Add(source);
            var bloom = source.Add<Bloom>(true);
            Cleanup.Add(bloom);
            bloom.intensity.value = 0.17f;
            var go = Go("PostProcessing");
            go.SetActive(false);
            var volume = go.AddComponent<Volume>();
            volume.sharedProfile = source;
            var post = go.AddComponent<DayNightPostProcessing>();
            post.Setup(source, volume);
            Invoke(post, "Awake");
            var instance = volume.profile;
            Invoke(post, "ApplyValues", 1f, 0.5f);
            Assert(instance != source, "Shared profile was reused");
            Assert(Mathf.Approximately(bloom.intensity.value, 0.17f), "Shared Bloom was mutated");
            Assert(instance.TryGet<Bloom>(out var copy) && copy != bloom, "Volume component was not cloned");
            Invoke(post, "OnDisable");
            Assert(instance == null && !volume.HasInstantiatedProfile(), "Profile instance not released");
        }

        private static void DenseInteractionQuery()
        {
            var item = Item();
            var actor = Actor();
            var probe = actor.gameObject.AddComponent<InteractionProbe>();
            probe.InteractableMask = 1 << 8;
            Set(probe, "_maxAngle", 360f);
            var crate = Go("DenseCrate").AddComponent<InteractableCrate>();
            crate.ConfigureLoot(item, 1, true);
            for (int i = 0; i < 80; i++)
            {
                var child = new GameObject("Collider" + i);
                child.transform.SetParent(crate.transform);
                child.layer = 8;
                child.AddComponent<BoxCollider>();
            }
            Physics.SyncTransforms();
            Invoke(probe, "CollectCandidates");
            Assert(probe.BufferCapacity >= 128 && probe.LastCandidateCount == 1, "Saturated query lost candidate");
            Assert(probe.TryInteract(), "Dense candidate could not be selected");
            Assert(!probe.TryInteract(), "Depleted crate still selected");
        }

        private static void LightingSubscriptionLifecycle()
        {
            var go = Go("Lighting");
            go.SetActive(false);
            var time = go.AddComponent<TimeOfDaySystem>();
            var lighting = go.AddComponent<DayNightLightingController>();
            Set(lighting, "_timeSystem", time);
            for (int i = 0; i < 3; i++) Invoke(lighting, "OnEnable");
            var callbacks = typeof(TimeOfDaySystem).GetField("OnTimeChanged",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(time) as Delegate;
            Assert(callbacks == null, "Lighting still subscribes alongside Update");
            Invoke(lighting, "Update");
            Assert(!float.IsNaN(lighting.NightAmount), "Lighting update invalid");
            Invoke(lighting, "OnDisable");
        }

        private static void SavedSceneIntegrity()
        {
            Scene scene = EditorSceneManager.OpenScene(GreyboxLevelBuilder.ScenePath, OpenSceneMode.Single);
            Assert(PrototypeSceneValidation.ValidateIds(scene).Count == 0, "Duplicate/empty IDs remain");
            int targets = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                    Assert(behaviour != null, "Missing script reference");
                foreach (InteractableBase target in root.GetComponentsInChildren<InteractableBase>(true))
                {
                    targets++;
                    foreach (Collider collider in target.GetComponentsInChildren<Collider>(true))
                        Assert(collider.gameObject.layer == 8, "Interactable collider excluded by mask");
                }
                foreach (InteractionProbe probe in root.GetComponentsInChildren<InteractionProbe>(true))
                    Assert(probe.InteractableMask.value == 256, "Incorrect interaction mask");
            }
            Assert(targets == 23, "Unexpected interactable count");
            Assert(EditorBuildSettings.scenes.Length > 0 &&
                EditorBuildSettings.scenes[0].path == GreyboxLevelBuilder.ScenePath &&
                EditorBuildSettings.scenes[0].enabled, "Incorrect startup scene");
        }

        private static void SkyDirectionsAndMaterialIsolation()
        {
            Shader shader = Shader.Find("Game/ProceduralSkyCelestial");
            Assert(shader != null, "Custom sky shader missing");
            var source = new Material(shader);
            Cleanup.Add(source);
            source.SetFloat("_NightAmount", 0.42f);
            Material previous = RenderSettings.skybox;
            var go = Go("SkyLifecycle");
            go.SetActive(false);
            var time = go.AddComponent<TimeOfDaySystem>();
            var lighting = go.AddComponent<DayNightLightingController>();
            var sun = Go("Sun").AddComponent<Light>();
            var moon = Go("Moon").AddComponent<Light>();
            sun.type = moon.type = LightType.Directional;
            Set(lighting, "_timeSystem", time);
            Set(lighting, "_skyboxMaterial", source);
            Set(lighting, "_sunLight", sun);
            Set(lighting, "_moonLight", moon);
            try
            {
                Invoke(lighting, "OnEnable");
                Material instance = RenderSettings.skybox;
                Assert(instance != source && instance != null, "Sky must use an owned instance");
                foreach (float hour in new[] { 0f, 5f, 9f, 12f, 15f, 19f, 24f })
                {
                    time.SetHour(hour);
                    Invoke(lighting, "Update");
                    Vector3 skySun = instance.GetVector("_SunDirection");
                    Vector3 skyMoon = instance.GetVector("_MoonDirection");
                    Assert(Vector3.Dot(skySun, -sun.transform.forward) > 0.9999f, "Sky and sunlight disagree");
                    Assert(Vector3.Dot(skyMoon, -moon.transform.forward) > 0.9999f, "Sky and moonlight disagree");
                    Assert(Vector3.Dot(skySun, skyMoon) < -0.9999f, "Moon not opposite the sun");
                    Assert(Mathf.Abs(instance.GetFloat("_NightAmount") - lighting.NightAmount) < 0.0001f,
                        "Sky has a different night weight");
                    Assert(Mathf.Abs(source.GetFloat("_NightAmount") - 0.42f) < 0.0001f, "Shared sky was changed");
                }
                Invoke(lighting, "BeforeSceneSave", go.scene, "unused");
                Assert(RenderSettings.skybox == previous, "Temporary sky would be serialized");
                Invoke(lighting, "OnDisable");
                Assert(instance == null && RenderSettings.skybox == previous, "Sky instance leaked");
                Invoke(lighting, "OnEnable");
                time.SetHour(12);
                Invoke(lighting, "Update");
                Assert(Mathf.Abs(lighting.SunElevation - 65f) < 0.001f, "Re-enable failed");
            }
            finally
            {
                Invoke(lighting, "OnDisable");
                RenderSettings.skybox = previous;
            }
        }
    }
}
