#if UNITY_EDITOR
using Game.Environment;
using Game.GameTime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    /// <summary>
    /// 昼夜环境的编辑器工具：
    ///   1. 生成天空盒材质资产
    ///   2. 生成后期处理 VolumeProfile 资产（存盘，可手改）
    ///   3. 一键把"时间系统 + 光照 + 后期处理"整套配好
    ///   4. 把天空 shader 加进 Always Included Shaders（打包后才不会丢天空）
    ///
    /// 本文件位于 Editor/ 文件夹，属于 Game.Editor 程序集（见同目录 Game.Editor.asmdef），
    /// 因此可以正常使用 using 引用 Game.Runtime 里的类型。
    /// </summary>
    public static class DayNightSceneTools
    {
        public const string EnvironmentName = "昼夜环境";
        public const string SkyMaterialPath = "Assets/_Project/Rendering/Sky/M_Sky_Celestial.mat";
        public const string PostProfilePath = "Assets/_Project/Rendering/VolumeProfiles/DA_DayNight_Profile.asset";
        public const string ScenePath = "Assets/_Project/Scenes/Prototype/SC_Prototype_Greybox.unity";
        public const string SkyShaderName = "Game/ProceduralSkyCelestial";

        [MenuItem("游戏/环境/创建昼夜场景", priority = 10)]
        public static void CreateDayNightScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            Install();
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"[昼夜] 昼夜原型场景已创建：{ScenePath}");
        }

        [MenuItem("游戏/环境/给当前场景接入昼夜", priority = 11)]
        public static void AddToCurrentScene()
        {
            Install();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            Debug.Log("[昼夜] 当前场景已接入昼夜系统。");
        }

        [MenuItem("游戏/环境/重建天空材质与后期处理配置", priority = 12)]
        public static void RebuildAssetsOnly()
        {
            Material sky = GetOrCreateSkyMaterial(true);
            VolumeProfile profile = GetOrCreatePostProfile(true);
            Debug.Log($"[昼夜] 已重建天空材质与后期处理配置：{AssetDatabase.GetAssetPath(sky)} / {AssetDatabase.GetAssetPath(profile)}");
        }

        [MenuItem("游戏/环境/仅应用光照预设（较暗夜晚）", priority = 13)]
        public static void ApplyLightingPresetToCurrentScene()
        {
            if (Application.isPlaying)
            {
                Debug.LogWarning("[昼夜] 请退出播放模式后应用光照预设，以便保存参数。");
                return;
            }

            Scene scene = SceneManager.GetActiveScene();
            // 修复已打开场景中的旧预览灯，不依赖重新加载磁盘场景。
            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (Game.Player.CharacterPreviewRig preview in root.GetComponentsInChildren<Game.Player.CharacterPreviewRig>(true))
                preview.Build();
            int applied = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (DayNightLightingController lighting in root.GetComponentsInChildren<DayNightLightingController>(true))
            {
                // 已有组件不会重新读取 C# 字段初始值，重建入口必须显式写回序列化参数。
                Undo.RecordObject(lighting, "应用光照预设");
                var data = new SerializedObject(lighting);
                data.FindProperty("_dayAmbientIntensity").floatValue = 0.18f;
                data.FindProperty("_minAmbientIntensity").floatValue = 0.025f;
                data.FindProperty("_moonMaxIntensity").floatValue = 0.045f;
                data.FindProperty("_nightExposure").floatValue = 0.22f;
                data.FindProperty("_nightFogDensity").floatValue = 0.012f;
                data.FindProperty("_overrideAmbient").boolValue = true;
                data.ApplyModifiedProperties();

                foreach (string field in new[] { "_sunLight", "_moonLight" })
                {
                    Light light = data.FindProperty(field).objectReferenceValue as Light;
                    if (light == null) continue;
                    Undo.RecordObject(light, "应用光照阴影预设");
                    light.shadows = LightShadows.Soft;
                    light.shadowStrength = 1f;
                    EditorUtility.SetDirty(light);
                }

                EditorUtility.SetDirty(lighting);
                applied++;
            }

            if (applied == 0)
            {
                Debug.LogWarning("[昼夜] 当前场景没有昼夜光照控制器，请先执行「游戏 → 环境 → 给当前场景接入昼夜」。");
                return;
            }

            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (DayNightPostProcessing post in root.GetComponentsInChildren<DayNightPostProcessing>(true))
            {
                Undo.RecordObject(post, "应用昼夜后期预设");
                var data = new SerializedObject(post);
                data.FindProperty("_nightPostExposure").floatValue = -0.4f;
                data.FindProperty("_warmFilter").colorValue = new Color(1f, 0.94f, 0.86f);
                data.FindProperty("_coldFilter").colorValue = new Color(0.86f, 0.92f, 1f);
                data.FindProperty("_duskContrast").floatValue = 6f;
                data.FindProperty("_dayBloomIntensity").floatValue = 0.06f;
                data.FindProperty("_nightBloomIntensity").floatValue = 0.18f;
                data.FindProperty("_bloomThreshold").floatValue = 1.2f;
                data.FindProperty("_bloomTint").colorValue = Color.white;
                data.FindProperty("_dayVignette").floatValue = 0.08f;
                data.FindProperty("_nightVignette").floatValue = 0.2f;
                data.ApplyModifiedProperties();
                EditorUtility.SetDirty(post);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
            Debug.Log($"[昼夜] 已向当前场景的 {applied} 套光照写入预设：白天补光 0.18、夜间补光 0.025、月光 0.045、阴影强度 1。时间保持不变；保存场景即可保留。");
        }

        // ------------------------------------------------------------------ 安装

        /// <summary>在当前场景里生成/刷新整套昼夜环境，并把灰盒地形一起建好。</summary>
        public static void Install()
        {
            Material skyMaterial = GetOrCreateSkyMaterial(false);
            VolumeProfile profile = GetOrCreatePostProfile(false);
            RegisterAlwaysIncludedShader();

            EnvironmentRig rig = GetOrCreateEnvironment();

            // 灰盒地形/玩家/相机（如果还没有的话）。
            GreyboxLevelBuilder.Build();

            WireReferencing(rig, skyMaterial);
            WireCamera(rig, profile);

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        }

        private class EnvironmentRig
        {
            public GameObject Root;
            public DayNightLightingController Lighting;
            public DayNightPostProcessing PostProcessing;
            public TimeOfDaySystem Time;
            public Light Sun;
            public Light Moon;
            public Volume Volume;
        }

        private static EnvironmentRig GetOrCreateEnvironment()
        {
            EnvironmentRig rig = new EnvironmentRig();

            // 按组件类型查找，而不是按 GameObject 名字 —— 改名不会破坏工具。
            rig.Lighting = Object.FindFirstObjectByType<DayNightLightingController>();
            GameObject root;

            if (rig.Lighting != null)
            {
                root = rig.Lighting.gameObject;
            }
            else
            {
                root = new GameObject(EnvironmentName);
                Undo.RegisterCreatedObjectUndo(root, "创建昼夜环境");
                rig.Lighting = Undo.AddComponent<DayNightLightingController>(root);
            }

            rig.Root = root;

            rig.Time = root.GetComponent<TimeOfDaySystem>();
            if (rig.Time == null) rig.Time = Undo.AddComponent<TimeOfDaySystem>(root);

            rig.PostProcessing = root.GetComponent<DayNightPostProcessing>();
            if (rig.PostProcessing == null) rig.PostProcessing = Undo.AddComponent<DayNightPostProcessing>(root);

            rig.Sun = EnsureLight(root.transform, "太阳", true, 1f, new Color(1f, 0.96f, 0.9f));
            rig.Moon = EnsureLight(root.transform, "月亮", true, 0.045f, new Color(0.64f, 0.72f, 0.95f));

            // 场景里模板自带的 Directional Light 会和 太阳 打架（两盏平行光会争 URP 主光），换过来之后删掉。
            RemoveOtherDirectionalLights(rig.Sun, rig.Moon);

            rig.Volume = EnsureVolume(root.transform);

            SetObjectReference(rig.Lighting, "_sunLight", rig.Sun);
            SetObjectReference(rig.Lighting, "_moonLight", rig.Moon);
            SetObjectReference(rig.Lighting, "_timeSystem", rig.Time);
            SetObjectReference(rig.PostProcessing, "_timeSystem", rig.Time);
            SetObjectReference(rig.PostProcessing, "_lighting", rig.Lighting);
            SetObjectReference(rig.PostProcessing, "_volume", rig.Volume);

            EditorUtility.SetDirty(rig.Lighting);
            EditorUtility.SetDirty(rig.PostProcessing);
            EditorUtility.SetDirty(rig.Time);
            EditorUtility.SetDirty(rig.Volume);

            return rig;
        }

        private static Light EnsureLight(Transform parent, string name, bool castShadows, float intensity, Color color)
        {
            Transform existing = parent.Find(name);
            GameObject go;

            if (existing != null)
            {
                go = existing.gameObject;
            }
            else
            {
                go = new GameObject(name);
                go.transform.SetParent(parent, false);
                Undo.RegisterCreatedObjectUndo(go, "创建" + name);
            }

            Light light = go.GetComponent<Light>();
            if (light == null) light = Undo.AddComponent<Light>(go);

            light.type = LightType.Directional;
            light.color = color;
            light.intensity = intensity;
            light.shadows = castShadows ? LightShadows.Soft : LightShadows.None;
            light.shadowStrength = 1f;
            light.shadowBias = 0.05f;
            light.shadowNormalBias = 0.4f;
            light.lightmapBakeType = LightmapBakeType.Realtime;

            go.transform.localRotation = Quaternion.Euler(50f, -30f, 0f);
            return light;
        }

        private static Volume EnsureVolume(Transform parent)
        {
            Transform existing = parent.Find("全局Volume");
            GameObject go;

            if (existing != null)
            {
                go = existing.gameObject;
            }
            else
            {
                go = new GameObject("全局Volume");
                go.transform.SetParent(parent, false);
                Undo.RegisterCreatedObjectUndo(go, "创建全局Volume");
            }

            Volume volume = go.GetComponent<Volume>();
            if (volume == null) volume = Undo.AddComponent<Volume>(go);

            volume.isGlobal = true;
            volume.priority = 1f;
            return volume;
        }

        /// <summary>删掉不是我们管理的平行光，避免两盏灯争主光导致阴影闪烁。</summary>
        private static void RemoveOtherDirectionalLights(Light sun, Light moon)
        {
            Light[] lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            foreach (Light light in lights)
            {
                if (light == null || light == sun || light == moon) continue;
                if (light.gameObject.scene != sun.gameObject.scene) continue;
                if (light.GetComponentInParent<Game.Player.CharacterPreviewRig>() != null) continue;
                if (light.type != LightType.Directional) continue;
                if (light.transform.IsChildOf(sun.transform.parent)) continue;

                Debug.Log($"[昼夜] 删除重复的平行光「{light.name}」（已由「{EnvironmentName}/太阳」接管主光）。");
                Object.DestroyImmediate(light.gameObject);
            }
        }

        private static void WireReferencing(EnvironmentRig rig, Material skyMaterial)
        {
            if (rig.Lighting != null && skyMaterial != null)
            {
                SetObjectReference(rig.Lighting, "_skyboxMaterial", skyMaterial);
            }

            Camera main = Camera.main;
            if (main != null && rig.Lighting != null)
            {
                SetObjectReference(rig.Lighting, "_skyFollowTarget", main.transform);
                EditorUtility.SetDirty(main.gameObject);
            }
        }

        private static void WireCamera(EnvironmentRig rig, VolumeProfile profile)
        {
            Camera main = Camera.main;
            if (main == null) return;

            UniversalAdditionalCameraData data = main.GetUniversalAdditionalCameraData();
            if (data != null)
            {
                data.renderPostProcessing = true;
                EditorUtility.SetDirty(data);
            }

            if (profile != null && rig.Volume != null)
            {
                rig.Volume.sharedProfile = profile;
                rig.PostProcessing.Setup(profile, rig.Volume);
            }

            if (rig.Volume != null && rig.Volume.gameObject.layer != main.gameObject.layer)
            {
                // Volume 只在相机能看到的 layer 上生效：把 Volume 放到相机同 layer，避免"看不到 Volume"的坑。
                rig.Volume.gameObject.layer = main.gameObject.layer;
            }

            EditorUtility.SetDirty(main.gameObject);
        }

        // ------------------------------------------------------------------ 资产生成

        public static Material GetOrCreateSkyMaterial(bool forceRebuild)
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(SkyMaterialPath);
            if (existing != null && !forceRebuild)
            {
                return existing;
            }

            Shader shader = Shader.Find(SkyShaderName);
            if (shader == null)
            {
                Debug.LogError($"[昼夜] 找不到 shader「{SkyShaderName}」，无法生成天空材质。");
                return existing;
            }

            if (existing == null)
            {
                if (!EditorFileUtility.EnsureAssetFolderForFile(SkyMaterialPath))
                {
                    Debug.LogError($"[昼夜] 无法创建天空材质目录：{SkyMaterialPath}");
                    return null;
                }

                existing = new Material(shader) { name = "M_NightSkyCelestial" };
                AssetDatabase.CreateAsset(existing, SkyMaterialPath);
            }
            else
            {
                existing.shader = shader;
            }

            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();
            return existing;
        }

        public static VolumeProfile GetOrCreatePostProfile(bool forceRebuild)
        {
            VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(PostProfilePath);
            if (profile == null)
            {
                if (!EditorFileUtility.EnsureAssetFolderForFile(PostProfilePath))
                {
                    Debug.LogError($"[昼夜] 无法创建后期处理配置目录：{PostProfilePath}");
                    return null;
                }

                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, PostProfilePath);
            }

            profile.components.RemoveAll(component => component == null);
            Tonemapping tonemapping = EnsureProfileComponent<Tonemapping>(profile, out bool newTonemapping);
            if (newTonemapping || forceRebuild)
            {
                tonemapping.active = true;
                tonemapping.mode.Override(TonemappingMode.Neutral);
            }

            ColorAdjustments colors = EnsureProfileComponent<ColorAdjustments>(profile, out bool newColors);
            if (newColors || forceRebuild)
            {
                colors.active = true;
                colors.colorFilter.Override(Color.white);
                colors.contrast.Override(0f);
                colors.postExposure.Override(0f);
                colors.saturation.Override(0f);
            }

            Bloom bloom = EnsureProfileComponent<Bloom>(profile, out bool newBloom);
            if (newBloom || forceRebuild)
            {
                bloom.active = true;
                bloom.intensity.Override(0.06f);
                bloom.threshold.Override(1.2f);
                bloom.tint.Override(Color.white);
                bloom.scatter.Override(0.55f);
                bloom.highQualityFiltering.Override(false);
            }

            Vignette vignette = EnsureProfileComponent<Vignette>(profile, out bool newVignette);
            if (newVignette || forceRebuild)
            {
                vignette.active = true;
                vignette.intensity.Override(0.08f);
                vignette.smoothness.Override(0.45f);
                vignette.color.Override(Color.black);
            }

            foreach (VolumeComponent component in profile.components)
                EditorUtility.SetDirty(component);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            return profile;
        }

        private static T EnsureProfileComponent<T>(VolumeProfile profile, out bool created) where T : VolumeComponent
        {
            created = !profile.TryGet(out T component);
            if (created) component = profile.Add<T>(true);
            // Profile.Add 只创建内存对象，必须注册为子资产，否则重启后引用变成 null。
            if (!AssetDatabase.Contains(component))
                AssetDatabase.AddObjectToAsset(component, profile);
            return component;
        }

        /// <summary>
        /// 把天空 shader 加进 Always Included Shaders。
        /// 不加的话，打包后运行时会 Shader.Find 失败 → 天空变粉。
        /// </summary>
        public static void RegisterAlwaysIncludedShader()
        {
            Shader shader = Shader.Find(SkyShaderName);
            if (shader == null) return;

            Object graphicsSettings = AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset");
            if (graphicsSettings == null) return;

            SerializedObject serialized = new SerializedObject(graphicsSettings);
            SerializedProperty list = serialized.FindProperty("m_AlwaysIncludedShaders");
            if (list == null) return;

            for (int i = 0; i < list.arraySize; i++)
            {
                if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader)
                {
                    return;
                }
            }

            int index = list.arraySize;
            list.InsertArrayElementAtIndex(index);
            list.GetArrayElementAtIndex(index).objectReferenceValue = shader;
            serialized.ApplyModifiedProperties();

            Debug.Log($"[昼夜] 已把「{SkyShaderName}」加入 Always Included Shaders（打包后天空才不会丢）。");
        }

        // ------------------------------------------------------------------ 工具

        private static void SetObjectReference(Object target, string fieldName, Object value)
        {
            if (target == null) return;

            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(fieldName);
            if (property == null)
            {
                Debug.LogWarning($"[昼夜] 字段「{fieldName}」不存在于 {target.GetType().Name} 上。");
                return;
            }

            property.objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
#endif
