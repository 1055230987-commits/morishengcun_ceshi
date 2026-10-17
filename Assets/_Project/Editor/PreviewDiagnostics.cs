#if UNITY_EDITOR
using System.Text;
using Game.Player;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Game.EditorTools
{
    /// <summary>
    /// 角色预览诊断：把预览装置的【运行期真实状态】打出来。
    ///
    /// 【为什么需要它】之前一直在从场景 YAML 文件反推问题，但预览装置的关键引用
    /// （_camera / _pivot / _renderTexture / _light）都是【非序列化的私有字段】——
    /// 它们只存在于内存里，文件里根本看不到。所以"预览是黑的"这类问题
    /// 不能靠读场景文件判断，只能在编辑器里问活着的对象。
    ///
    /// 本工具一次打出：
    ///   装置的层级与激活状态
    ///   相机：是否启用、targetTexture、cullingMask、renderingLayerMask、位置
    ///   RenderTexture：是否创建、尺寸、格式
    ///   模型：子物体数、Renderer 数、材质与 shader 名、renderingLayerMask
    ///   灯：数量、强度、是否启用
    ///   界面：InventoryScreen 拿到的贴图是不是同一张
    ///
    /// 菜单：「游戏 → 工程 → 诊断角色预览（打印真实状态）」
    /// </summary>
    public static class PreviewDiagnostics
    {
        private const string PreviewRootName = "主角模型预览";

        [MenuItem("游戏/工程/诊断角色预览（打印真实状态）", priority = 105)]
        public static void Run()
        {
            var sb = new StringBuilder("===== 角色预览诊断 =====\n");

            GameObject root = GameObject.Find(PreviewRootName);
            if (root == null)
            {
                Debug.LogError("[预览诊断] 场景里找不到「主角模型预览」。请先跑「重建到当前场景」。");
                return;
            }

            CharacterPreviewRig rig = root.GetComponent<CharacterPreviewRig>();
            if (rig == null)
            {
                Debug.LogError("[预览诊断]「主角模型预览」上没有 CharacterPreviewRig 组件。");
                return;
            }

            sb.AppendLine($"装载模式：{(Application.isPlaying ? "Play" : "编辑")}");
            sb.AppendLine($"装置 active={root.activeSelf} activeInHierarchy={root.activeInHierarchy} 位置={root.transform.position}");
            sb.AppendLine();

            // ---- 子物体统计 ----
            sb.AppendLine("【子物体】");
            var counts = new System.Collections.Generic.Dictionary<string, int>();
            var activeCounts = new System.Collections.Generic.Dictionary<string, int>();
            foreach (Transform child in root.transform)
            {
                counts.TryGetValue(child.name, out int n); counts[child.name] = n + 1;
                if (child.gameObject.activeSelf)
                {
                    activeCounts.TryGetValue(child.name, out int a); activeCounts[child.name] = a + 1;
                }
                else
                {
                    activeCounts.TryGetValue(child.name, out int a); activeCounts[child.name] = a;
                }
            }
            foreach (var p in counts)
            {
                activeCounts.TryGetValue(p.Key, out int act);
                sb.AppendLine($"    {p.Key}：{p.Value} 个，其中 active {act} 个");
            }
            sb.AppendLine();

            // ---- 相机 ----
            sb.AppendLine("【相机】");
            Camera[] cameras = root.GetComponentsInChildren<Camera>(true);
            sb.AppendLine($"    数量（含未激活）：{cameras.Length}");

            foreach (Camera cam in cameras)
            {
                if (cam == null) continue;
                bool isEnabled = cam.enabled;
                bool goActive = cam.gameObject.activeInHierarchy;

                sb.AppendLine(
                    $"    「{cam.name}」enabled={isEnabled} inHierarchy={goActive}\n" +
                    $"        targetTexture={(cam.targetTexture != null ? cam.targetTexture.name : "【null】")}\n" +
                    $"        cullingMask=0x{cam.cullingMask:X8}  实际层={DescribeMask(cam.cullingMask)}\n" +
                    $"        clearFlags={cam.clearFlags}  背景={cam.backgroundColor}\n" +
                    $"        near={cam.nearClipPlane} far={cam.farClipPlane} fov={cam.fieldOfView}\n" +
                    $"        位置={cam.transform.position} 朝向={cam.transform.forward}");

                // RenderTexture
                if (cam.targetTexture != null)
                {
                    RenderTexture rt = cam.targetTexture;
                    sb.AppendLine(
                        $"        RT：{rt.width}x{rt.height} format={rt.format} " +
                        $"IsCreated={rt.IsCreated()} depth={rt.depth}");
                }
            }
            sb.AppendLine();

            // ---- 预览模型 ----
            sb.AppendLine("【模型】");
            Transform pivot = FindChild(root.transform, "旋转轴");
            if (pivot == null)
            {
                sb.AppendLine("    【找不到「旋转轴」】");
            }
            else
            {
                Transform model = FindChild(pivot, "预览模型");
                sb.AppendLine($"    旋转轴 active={pivot.gameObject.activeSelf}");

                if (model == null)
                {
                    sb.AppendLine("    【「旋转轴」下找不到「预览模型」】← 这就会导致预览全空");
                }
                else
                {
                    Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
                    sb.AppendLine($"    预览模型 active={model.gameObject.activeSelf}  Renderer 数={renderers.Length}");

                    if (renderers.Length == 0)
                    {
                        sb.AppendLine("        【没有 Renderer】← 模型没被正确克隆，预览必然是空的");
                    }

                    foreach (Renderer r in renderers)
                    {
                        if (r == null) continue;
                        string mats = "";
                        foreach (Material m in r.sharedMaterials)
                        {
                            mats += m == null
                                ? "【null 材质】"
                                : $"「{m.name}」shader=「{(m.shader != null ? m.shader.name : "【null shader】")}」";
                            mats += " ";
                        }

                        sb.AppendLine(
                            $"        「{r.name}」enabled={r.enabled} inHierarchy={r.gameObject.activeInHierarchy}\n" +
                            $"            层={LayerMask.LayerToName(r.gameObject.layer)}({r.gameObject.layer}) " +
                            $"renderingLayerMask={r.renderingLayerMask}\n" +
                            $"            材质：{mats}");
                    }
                }
            }
            sb.AppendLine();

            // ---- 灯 ----
            sb.AppendLine("【灯】");
            Light[] lights = root.GetComponentsInChildren<Light>(true);
            int enabledLights = 0;
            foreach (Light l in lights)
            {
                if (l == null) continue;
                bool on = l.enabled && l.gameObject.activeInHierarchy;
                if (on) enabledLights++;

                UniversalAdditionalLightData data = l.GetUniversalAdditionalLightData();
                sb.AppendLine(
                    $"    「{l.name}」启用={on} 类型={l.type} 强度={l.intensity} 范围={l.range}\n" +
                    $"        cullingMask=0x{l.cullingMask:X8} renderingLayers={(data != null ? data.renderingLayers.ToString() : "【无】")}\n" +
                    $"        位置={l.transform.position}");
            }
            sb.AppendLine($"    实际点亮的数量：{enabledLights}（正常应为 2）");
            sb.AppendLine();

            // ---- 界面取到的贴图 ----
            sb.AppendLine("【背包界面】");
            PlayerInventory playerInventory = Object.FindFirstObjectByType<PlayerInventory>();
            if (playerInventory == null)
            {
                sb.AppendLine("    【场景里找不到 PlayerInventory】");
            }
            else
            {
                InventoryScreen screen = playerInventory.GetComponent<InventoryScreen>();
                sb.AppendLine(screen == null
                    ? "    玩家身上没有 InventoryScreen 组件"
                    : $"    InventoryScreen 存在，active={screen.gameObject.activeSelf}");
            }

            Debug.Log(sb.ToString());

            // ---- 结论提示 ----
            Debug.Log(BuildVerdict(root, cameras, pivot, enabledLights));
        }

        /// <summary>根据收集到的状态给出最可能的结论，省得人工比对。</summary>
        private static string BuildVerdict(GameObject root, Camera[] cameras, Transform pivot, int enabledLights)
        {
            var sb = new StringBuilder("===== 结论 =====\n");

            Transform model = pivot != null ? FindChild(pivot, "预览模型") : null;

            if (model == null)
            {
                sb.AppendLine("★ 「旋转轴」下没有「预览模型」。" +
                              "说明 SetSourceFrom 没跑成功 —— 检查玩家的「外观」子物体是否存在且有 MeshRenderer。");
                return sb.ToString();
            }

            Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                sb.AppendLine("★ 「预览模型」下没有任何 Renderer。" +
                              "玩家的「外观」层级里没有 MeshFilter+MeshRenderer，所以克隆出来是空的。");
                return sb.ToString();
            }

            // 【注意】预览相机的 enabled 是【故意设成 false】的 ——
            // 它是离屏相机，靠 Camera.Render() 画到 RenderTexture，不输出到屏幕。
            // 所以这里【不能】用 enabled 当判据，否则会把正常状态误报成故障（本工程踩过）。
            // 正确的判据是：有没有相机、它有没有 targetTexture、cullingMask 是否包含模型层。
            Camera previewCamera = null;
            foreach (Camera c in cameras)
            {
                if (c != null) { previewCamera = c; break; }
            }

            if (previewCamera == null)
            {
                sb.AppendLine("★ 装置里一台相机都没有。");
                return sb.ToString();
            }

            if (previewCamera.targetTexture == null)
            {
                sb.AppendLine("★ 预览相机没有 targetTexture —— 渲染结果无处可去，界面拿到的是 null。");
                return sb.ToString();
            }

            int modelLayer = model.gameObject.layer;
            if ((previewCamera.cullingMask & (1 << modelLayer)) == 0)
            {
                sb.AppendLine($"★ 预览相机的 cullingMask 不包含模型的层「{LayerMask.LayerToName(modelLayer)}({modelLayer})」——" +
                              "相机看不见模型，画面只有清屏色。");
                return sb.ToString();
            }

            // 模型是否落在相机视野里。
            //
            // 【关键】不能拿 model.position 当"模型位置"！预览模型是一个【容器节点】，
            // 它自己的 transform 原点通常在 (0,0,0)，而可见几何体可能整体偏移在上方。
            // 而且 FrameCamera() 的视线瞄准点是 `_pivot.position + up * _lookAtHeight`
            // （身体中部），不是 _pivot.position。
            // 所以要用【所有 Renderer 的合并包围盒中心】来判断 —— 那才是"模型身体在哪"。
            // （本工程踩过：用容器根节点算，把正常状态误报成"16.3° 超出视野"。）
            Bounds modelBounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) modelBounds.Encapsulate(renderers[i].bounds);

            Vector3 modelCenter = modelBounds.center;
            Vector3 toModel = modelCenter - previewCamera.transform.position;
            float distance = toModel.magnitude;
            float angle = Vector3.Angle(previewCamera.transform.forward, toModel);
            float halfFov = previewCamera.fieldOfView * 0.5f;

            // 竖直方向还要考虑屏幕宽高比（水平半角通常更大，所以竖直半角是更严的约束）
            sb.AppendLine($"    模型包围盒：center={modelCenter} size={modelBounds.size}");
            sb.AppendLine($"    相机到【模型中心】距离={distance:F3}，偏离相机正前方={angle:F2}°" +
                          $"（fov={previewCamera.fieldOfView}，竖直半角≈{halfFov:F1}°）");
            sb.AppendLine($"    相机世界位置={previewCamera.transform.position}  forward={previewCamera.transform.forward}");

            if (angle > halfFov)
            {
                sb.AppendLine($"★ 模型中心偏离相机正前方 {angle:F1}°，超出竖直半视野角 {halfFov:F1}° ——" +
                              "模型很可能在画面外。\n" +
                              "    FrameCamera() 的瞄准点是 _pivot.position + up*_lookAtHeight，" +
                              "如果模型的实际几何中心离那个点很远，就会出画。");
                return sb.ToString();
            }

            bool anyMaterialNull = false, anyShaderNull = false;
            foreach (Renderer r in renderers)
            {
                foreach (Material m in r.sharedMaterials)
                {
                    if (m == null) anyMaterialNull = true;
                    else if (m.shader == null) anyShaderNull = true;
                }
            }

            if (anyMaterialNull)
            {
                sb.AppendLine("★ 有 Renderer 的材质是 null —— 会渲染成【纯黑】。" +
                              "通常是原材质的 sharedMaterials 里有空位。");
                return sb.ToString();
            }

            if (anyShaderNull)
            {
                sb.AppendLine("★ 有材质丢了 shader —— 会渲染成【粉红或黑色】。");
                return sb.ToString();
            }

            if (enabledLights == 0)
            {
                sb.AppendLine("★ 预览区一盏灯都没亮 —— 用的是受光材质（URP/Lit），没光就是黑的。" +
                              "这是【最可能】的原因。");
                return sb.ToString();
            }

            // 灯亮了但可能被 rendering layer 过滤掉
            uint modelRenderingLayer = renderers[0].renderingLayerMask;
            bool layerMismatch = false;
            foreach (Light l in root.GetComponentsInChildren<Light>(true))
            {
                if (l == null || !l.enabled || !l.gameObject.activeInHierarchy) continue;
                UniversalAdditionalLightData data = l.GetUniversalAdditionalLightData();
                if (data == null) { layerMismatch = true; break; }
                if ((data.renderingLayers & modelRenderingLayer) == 0) { layerMismatch = true; break; }
            }

            if (layerMismatch)
            {
                sb.AppendLine("★ 灯亮了，但灯的 renderingLayers 和模型的 renderingLayerMask 不匹配 ——" +
                              "URP 会认为这盏灯不照这个物体（Forward+ 下这是主要的光照过滤路径）。" +
                              $"\n   模型 renderingLayerMask={modelRenderingLayer}（bit {(int)Mathf.Log(modelRenderingLayer, 2)}）");
                return sb.ToString();
            }

            sb.AppendLine("没有发现明显的结构性问题。可能的原因：" +
                          "\n  - 相机位置/朝向不对（模型在视野外）" +
                          "\n  - RenderTexture 未创建或尺寸为 0" +
                          "\n  - 界面上贴图的绘制区域/缩放问题（预览有内容但显示不出来）");
            return sb.ToString();
        }

        private static Transform FindChild(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name == name) return child;
            }
            return null;
        }

        private static string DescribeMask(int mask)
        {
            if (mask == 0) return "（空）";
            var sb = new StringBuilder();
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                string n = LayerMask.LayerToName(i);
                sb.Append(string.IsNullOrEmpty(n) ? $"{i} " : $"{n}({i}) ");
            }
            return sb.ToString().Trim();
        }
    }
}
#endif
