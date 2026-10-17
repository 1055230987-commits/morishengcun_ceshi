// ---------------------------------------------------------------------------
//  末日生存 — 双面显示批量设置
//
//  为什么需要：从 XAPK 提取的模型里，树叶、布片、格栅、铁丝网等大量使用**单面片几何**。
//  Unity 默认 Render Face = Front（背面剔除），这些物件会缺面/看起来是空的，
//  而原游戏里它们是双面渲染的。
//
//  机制（已在本工程的 Unity 6000.3.10f1 + URP 上核实）：
//    * URP Lit / SimpleLit / Unlit 用 **_Cull** 属性控制剔除：
//          0 = Both（双面，Render Face = Both）
//          1 = Back
//          2 = Front（默认，单面）
//    * 部分 Shader Graph / 自定义 shader 用关键词 _CULL_FRONT / _CULL_BACK 控制，
//      两者会被一并清理。
//    * 注意：Unity 6000.3 的 ModelImporter **没有** materialDoubleSided 之类的属性
//      （已在 UnityEditor/UnityEngine 全部程序集里搜过），所以双面只能在材质层面设置。
//
//  用法：菜单 Tools ▸ 末日生存 ▸ 双面显示批量设置…
//        或在 Project 窗口右键某个文件夹 ▸ 末日生存 ▸ 实体资源设为双面显示
// ---------------------------------------------------------------------------

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DSH.ExtractedAssets
{
    public class DoubleSidedWindow : EditorWindow
    {
        private const string DefaultCreateDir = "Assets/_Project/Art/Materials/Extracted";
        private const string ReportPath = "Assets/_Project/Docs/双面显示设置报告.md";

        private string _matFolder = "Assets/_Project/Art/Environment";
        private string _vfxFolder = "Assets/_Project/Art/VFX";
        private bool _doExisting = true;
        private bool _doCreate = false;
        private string _createDir = DefaultCreateDir;
        private bool _onlyLikelyThin = false;
        private int _maxItems = 20000;

        private Vector2 _scroll;
        private string _summary = "";

        // 单面片几何的高频关键词：叶子、布、格栅、网、帘、旗、纸、贴花等
        private static readonly string[] ThinKeywords =
        {
            "leaf", "leaves", "foliage", "branch", "grass", "plant", "bush", "tree",
            "cloth", "fabric", "rag", "curtain", "carpet", "flag", "banner", "tarp", "sail",
            "fence", "grid", "grating", "lattice", "mesh_", "net", "wire", "chain",
            "paper", "poster", "sign", "decal", "sticker", "label", "card", "billboard",
            "glass", "window", "screen", "monitor", "plane", "quad", "card_", "sprite"
        };

        [MenuItem("Tools/末日生存/双面显示批量设置…")]
        public static void Open() => GetWindow<DoubleSidedWindow>("双面显示");

        [MenuItem("Assets/末日生存/实体资源设为双面显示", priority = 101)]
        private static void QuickApply()
        {
            var sel = Selection.activeObject;
            var target = sel != null ? AssetDatabase.GetAssetPath(sel) : "Assets/_Project/Art";
            if (!string.IsNullOrEmpty(target) && File.Exists(target))
            {
                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) target = dir.Replace('\\', '/');
            }

            int skipped;
            int n = SetMaterialsDoubleSided(new[] { target }, false, out skipped);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[双面显示] 已把 {n} 个材质设为双面（{skipped} 个 shader 无 _Cull，已跳过）。范围：{target}");
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("双面显示批量设置", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "树叶/布片/格栅这类单面片几何在 Unity 默认被背面剔除，会缺面。\n" +
                "已核实：Unity 6000.3 没有模型导入器层面的双面开关，只能在材质上设 _Cull = 0（Render Face = Both）。",
                MessageType.Info);

            EditorGUILayout.Space();
            _matFolder = FolderField("实体材质", _matFolder);
            _vfxFolder = FolderField("VFX 材质", _vfxFolder);

            EditorGUILayout.Space();
            _doExisting = EditorGUILayout.ToggleLeft("把范围内已有材质的 Render Face 设为 Both", _doExisting);
            _onlyLikelyThin = EditorGUILayout.ToggleLeft("　　只处理名字像单面片的材质（叶子/布/网/格栅…）", _onlyLikelyThin);

            _doCreate = EditorGUILayout.ToggleLeft("为还没有材质的模型新建双面材质（URP/Lit）", _doCreate);
            using (new EditorGUI.DisabledScope(!_doCreate))
                _createDir = FolderField("　新建到", _createDir);

            _maxItems = EditorGUILayout.IntSlider("单次最多处理", _maxItems, 100, 50000);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("扫描预览（不写文件）", GUILayout.Height(28))) Run(false);
                if (GUILayout.Button("执行并生成报告", GUILayout.Height(28))) Run(true);
            }

            if (!string.IsNullOrEmpty(_summary))
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("上次结果", EditorStyles.boldLabel);
                _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(160));
                EditorGUILayout.TextArea(_summary);
                EditorGUILayout.EndScrollView();
            }
        }

        private static string FolderField(string label, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(80));
                var v = EditorGUILayout.TextField(value);
                if (GUILayout.Button("选", GUILayout.Width(30)))
                {
                    var p = EditorUtility.OpenFolderPanel("选择目录", "Assets", "");
                    if (!string.IsNullOrEmpty(p))
                    {
                        p = p.Replace('\\', '/');
                        var i = p.IndexOf("/Assets/", StringComparison.Ordinal);
                        if (i >= 0) v = p.Substring(i + 1);
                    }
                }
                return v;
            }
        }

        private static IEnumerable<string> Collect(string filter, string folder)
        {
            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
                return Array.Empty<string>();
            return AssetDatabase.FindAssets(filter, new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .OrderBy(p => p, StringComparer.Ordinal);
        }

        private static bool LooksThin(string pathOrName)
        {
            var s = pathOrName.ToLowerInvariant();
            return ThinKeywords.Any(s.Contains);
        }

        private void Run(bool execute)
        {
            var matFolders = new List<string>();
            if (_doExisting)
            {
                if (!string.IsNullOrEmpty(_matFolder)) matFolders.Add(_matFolder);
                if (!string.IsNullOrEmpty(_vfxFolder)) matFolders.Add(_vfxFolder);
            }

            var matPaths = matFolders.SelectMany(f => Collect("t:Material", f))
                                     .Distinct().Take(_maxItems).ToArray();

            int total = 0, already = 0, noCull = 0, willChange = 0, filteredOut = 0;
            var noCullShaders = new SortedDictionary<string, int>();

            foreach (var p in matPaths)
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(p);
                if (m == null) continue;
                total++;
                if (_onlyLikelyThin && !LooksThin(p) && !LooksThin(m.name)) { filteredOut++; continue; }
                if (!m.HasProperty("_Cull"))
                {
                    noCull++;
                    var sn = m.shader != null ? m.shader.name : "(无 shader)";
                    int c; noCullShaders.TryGetValue(sn, out c);
                    noCullShaders[sn] = c + 1;
                    continue;
                }
                if (Mathf.Approximately(m.GetFloat("_Cull"), 0f)) already++;
                else willChange++;
            }

            var modelPaths = _doCreate
                ? Collect("t:Model", _matFolder).Take(_maxItems).ToArray()
                : Array.Empty<string>();

            var head = new StringBuilder();
            head.AppendLine($"材质扫描：{total} 个（将改 {willChange}，已是双面 {already}，"
                            + $"shader 无 _Cull {noCull}"
                            + (_onlyLikelyThin ? $"，因名称过滤跳过 {filteredOut}" : "") + "）");
            if (_doCreate) head.AppendLine($"模型扫描：{modelPaths.Length} 个（用于新建材质）");

            if (!execute)
            {
                _summary = head + "\n（预览模式，未写入任何文件）";
                Repaint();
                return;
            }

            int changed = 0, created = 0;
            try
            {
                if (_doExisting)
                {
                    for (int i = 0; i < matPaths.Length; i++)
                    {
                        if (i % 200 == 0)
                            EditorUtility.DisplayProgressBar("设置材质双面", matPaths[i],
                                (float)i / Mathf.Max(1, matPaths.Length));
                        var m = AssetDatabase.LoadAssetAtPath<Material>(matPaths[i]);
                        if (m == null) continue;
                        if (_onlyLikelyThin && !LooksThin(matPaths[i]) && !LooksThin(m.name)) continue;
                        if (ApplyDoubleSided(m)) changed++;
                    }
                }

                if (_doCreate)
                {
                    if (!AssetDatabase.IsValidFolder(_createDir))
                    {
                        Directory.CreateDirectory(_createDir);
                        AssetDatabase.Refresh();
                    }
                    for (int i = 0; i < modelPaths.Length; i++)
                    {
                        if (i % 200 == 0)
                            EditorUtility.DisplayProgressBar("新建双面材质", modelPaths[i],
                                (float)i / Mathf.Max(1, modelPaths.Length));
                        created += CreateDoubleSidedMaterial(modelPaths[i], _createDir);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var tail = new StringBuilder();
            tail.AppendLine($"材质改为双面：{changed}");
            tail.AppendLine($"新建材质：{created}");
            if (noCullShaders.Count > 0)
            {
                tail.AppendLine("以下 Shader 无 _Cull 属性（已跳过，需手动处理）：");
                foreach (var kv in noCullShaders.OrderByDescending(k => k.Value).Take(20))
                    tail.AppendLine($"    {kv.Key}  ×{kv.Value}");
            }

            try
            {
                var dir = Path.GetDirectoryName(ReportPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(ReportPath, BuildReport(matPaths, willChange, already, noCull,
                    changed, created, noCullShaders, modelPaths.Length), new UTF8Encoding(false));
                AssetDatabase.ImportAsset(ReportPath);
                tail.AppendLine($"报告：{ReportPath}");
            }
            catch (Exception e)
            {
                tail.AppendLine("报告写出失败：" + e.Message);
            }

            _summary = head + "\n" + tail;
            Repaint();
            Debug.Log("[双面显示] " + _summary);
        }

        /// <summary>把单个材质设为双面。返回是否真的改了。</summary>
        public static bool ApplyDoubleSided(Material m)
        {
            if (m == null || !m.HasProperty("_Cull")) return false;
            bool changed = false;

            if (!Mathf.Approximately(m.GetFloat("_Cull"), 0f))
            {
                m.SetFloat("_Cull", 0f);   // 0 = Both
                changed = true;
            }

            if (m.HasProperty("_DoubleSidedEnable") &&
                !Mathf.Approximately(m.GetFloat("_DoubleSidedEnable"), 1f))
            {
                m.SetFloat("_DoubleSidedEnable", 1f);
                changed = true;
            }

            // 用关键词控制剔除的自定义/ShaderGraph shader
            if (m.IsKeywordEnabled("_CULL_FRONT")) { m.DisableKeyword("_CULL_FRONT"); changed = true; }
            if (m.IsKeywordEnabled("_CULL_BACK")) { m.DisableKeyword("_CULL_BACK"); changed = true; }

            if (changed) EditorUtility.SetDirty(m);
            return changed;
        }

        public static int SetMaterialsDoubleSided(IEnumerable<string> folders, bool onlyThin, out int skipped)
        {
            skipped = 0;
            int n = 0;
            foreach (var f in folders)
                foreach (var p in Collect("t:Material", f))
                {
                    var m = AssetDatabase.LoadAssetAtPath<Material>(p);
                    if (m == null) continue;
                    if (onlyThin && !LooksThin(p) && !LooksThin(m.name)) continue;
                    if (!m.HasProperty("_Cull")) { skipped++; continue; }
                    if (ApplyDoubleSided(m)) n++;
                }
            return n;
        }

        private static int CreateDoubleSidedMaterial(string modelPath, string dir)
        {
            var baseName = Path.GetFileNameWithoutExtension(modelPath);
            // 文件名形如 <网格名>_<bundle短名>_<hex>，取前缀做材质名
            var idx = baseName.LastIndexOf('_');
            if (idx > 0)
            {
                var idx2 = baseName.LastIndexOf('_', idx - 1);
                if (idx2 > 0) baseName = baseName.Substring(0, idx2);
            }

            var matPath = $"{dir}/M_{baseName}.mat";
            if (File.Exists(matPath)) return 0;

            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null) return 0;

            var mat = new Material(shader) { name = $"M_{baseName}" };
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
            AssetDatabase.CreateAsset(mat, matPath);
            return 1;
        }

        private static string BuildReport(string[] matPaths, int willChange, int already, int noCull,
            int changed, int created, SortedDictionary<string, int> noCullShaders, int modelCount)
        {
            var L = new List<string>();
            L.Add("# 双面显示设置报告");
            L.Add("");
            L.Add($"- 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            L.Add($"- 材质总数：{matPaths.Length}（已是双面 {already}，待改 {willChange}，shader 无 `_Cull` {noCull}）");
            L.Add($"- 实际改掉：{changed}");
            L.Add($"- 新建材质：{created}（扫描模型 {modelCount} 个）");
            L.Add("");
            L.Add("## 原理");
            L.Add("");
            L.Add("URP Lit/SimpleLit/Unlit 的 `_Cull`：`0` = Both（双面）、`1` = Back、`2` = Front（默认）。");
            L.Add("已核实 Unity 6000.3 的 `ModelImporter` 没有双面相关属性，所以只能在材质上设置。");
            L.Add("");
            if (noCullShaders.Count > 0)
            {
                L.Add("## 无 `_Cull` 属性、被跳过的 Shader");
                L.Add("");
                L.Add("| Shader | 材质数 |");
                L.Add("|---|---:|");
                foreach (var kv in noCullShaders.OrderByDescending(k => k.Value))
                    L.Add($"| `{kv.Key}` | {kv.Value} |");
                L.Add("");
                L.Add("这些 shader 可能需要用别的属性或关键词控制剔除，请手动确认。");
                L.Add("");
            }
            L.Add("## 注意");
            L.Add("");
            L.Add("双面渲染会让 GPU 少剔除一半面，开销略增。对封闭实体（岩石、箱子）");
            L.Add("影响只是性能；对薄片（树叶、布、格栅）则是**必须**，否则会缺面。");
            L.Add("如果只想处理薄片，勾选「只处理名字像单面片的材质」。");
            return string.Join("\n", L);
        }
    }
}
#endif
