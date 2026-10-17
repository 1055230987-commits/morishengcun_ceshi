#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Text;
using Game.Player;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    public static class LightingAuditTools
    {
        private const string RequestPath = "OptimizationReview/lighting-audit.request";
        private const string ReportPath = "OptimizationReview/lighting-editor-audit.txt";
        private static string ProjectPath(string path) => Path.Combine(Path.GetDirectoryName(Application.dataPath), path);

        // 仅当本地显式创建请求文件时执行一次；正常重载不运行测试。
        [InitializeOnLoadMethod]
        private static void ScheduleRequestedAudit()
        {
            if (File.Exists(ProjectPath(RequestPath))) EditorApplication.delayCall += RunRequestedAudit;
        }

        private static void RunRequestedAudit()
        {
            if (!File.Exists(ProjectPath(RequestPath))) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.delayCall += RunRequestedAudit;
                return;
            }
            File.Delete(ProjectPath(RequestPath));
            Audit();
        }

        [MenuItem("游戏/环境/检查光照隔离并导出诊断", priority = 14)]
        public static void Audit()
        {
            var report = new StringBuilder();
            report.AppendLine("Time: " + DateTime.Now.ToString("O"));
            report.AppendLine("Scene: " + SceneManager.GetActiveScene().path);
            report.AppendLine("Playing: " + Application.isPlaying);
            report.AppendLine("Pipeline: " + GraphicsSettings.currentRenderPipeline);
            report.AppendLine("Sun source: " + RenderSettings.sun);
            report.AppendLine("Ambient: " + RenderSettings.ambientMode + " sky=" + RenderSettings.ambientSkyColor);
            foreach (var lighting in Object.FindObjectsByType<Game.Environment.DayNightLightingController>(FindObjectsSortMode.None))
                report.AppendLine($"DayNight: {lighting.name}; enabled={lighting.isActiveAndEnabled}; elevation={lighting.SunElevation}; night={lighting.NightAmount}");
            foreach (var time in Object.FindObjectsByType<Game.GameTime.TimeOfDaySystem>(FindObjectsSortMode.None))
                report.AppendLine($"TimeOfDay: {time.name}; hour={time.Hour}");
            foreach (var camera in Object.FindObjectsByType<UnityEngine.Camera>(FindObjectsSortMode.None))
            {
                report.AppendLine($"Camera: {camera.name}; active={camera.isActiveAndEnabled}; target={camera.targetTexture}");
                if (camera.TryGetComponent<UniversalAdditionalCameraData>(out var data))
                    report.AppendLine($"  Renderer={data.scriptableRenderer?.GetType().Name}; shadows={data.renderShadows}; post={data.renderPostProcessing}");
            }
            foreach (Light light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                report.AppendLine($"Light: {light.name}; active={light.isActiveAndEnabled}; type={light.type}; intensity={light.intensity}; range={light.range}; renderingMask={light.renderingLayerMask}; shadows={light.shadows}/{light.shadowStrength}");
            }
            foreach (SceneView view in SceneView.sceneViews)
                report.AppendLine("SceneView lighting: " + view.sceneLighting);
            Shader water = Shader.Find("Game/Water/RealisticWater");
            if (water != null)
                foreach (var message in ShaderUtil.GetShaderMessages(water))
                    report.AppendLine("Water shader: " + message.severity + " " + message.message);

            if (!Application.isPlaying)
            {
                try { VerifyPreviewLifecycle(); report.AppendLine("PASS preview isolation, legacy duplicates, rebuild, enable/disable, texture disposal"); }
                catch (Exception error) { report.AppendLine("FAIL preview regression: " + error); Debug.LogException(error); }
            }
            Directory.CreateDirectory(ProjectPath("OptimizationReview"));
            File.WriteAllText(ProjectPath(ReportPath), report.ToString());
            Debug.Log("[光照诊断] 已导出 " + ProjectPath(ReportPath));
        }

        public static void VerifyPreviewLifecycle()
        {
            Scene previewScene = EditorSceneManager.NewPreviewScene();
            GameObject root = new GameObject("PreviewIsolationRegression");
            root.SetActive(false);
            SceneManager.MoveGameObjectToScene(root, previewScene);
            try
            {
                // 模拟脚本重载后已有多组序列化子对象、但非序列化字段为空。
                for (int i = 0; i < 3; i++)
                foreach (string name in new[] { "预览主光", "预览补光" })
                {
                    var child = new GameObject(name);
                    child.transform.SetParent(root.transform, false);
                    child.AddComponent<Light>().type = LightType.Directional;
                }
                var pivot = new GameObject("旋转轴");
                pivot.transform.SetParent(root.transform, false);
                var renderer = pivot.AddComponent<MeshRenderer>();
                var rig = root.AddComponent<CharacterPreviewRig>();
                root.SetActive(true);
                rig.Build();
                int count = root.transform.childCount;
                for (int i = 0; i < 5; i++)
                {
                    rig.Build();
                    Light[] active = root.GetComponentsInChildren<Light>().Where(l => l.isActiveAndEnabled).ToArray();
                    Require(active.Length == 2, "Expected two active preview lights");
                    Require(active.All(l => l.type == LightType.Point && l.range <= 6f), "Preview light can illuminate world or compete for main light");
                    Require(active.All(l => (uint)l.GetUniversalAdditionalLightData().renderingLayers == CharacterPreviewRig.PreviewRenderingLayerMask), "Wrong URP light rendering layer");
                    Require(renderer.renderingLayerMask == CharacterPreviewRig.PreviewRenderingLayerMask, "Existing preview renderer was not migrated");
                    Require(root.transform.childCount == count, "Rebuild duplicated generated children");
                    RenderTexture texture = rig.Texture;
                    rig.enabled = false;
                    Require(texture == null && !rig.IsReady, "Preview texture was not released");
                    rig.enabled = true;
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
                EditorSceneManager.ClosePreviewScene(previewScene);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
