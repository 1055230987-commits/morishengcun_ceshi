// ---------------------------------------------------------------------------
//  末日生存 — 从 XAPK 逆向提取的模型：按名字辅助挂贴图
//
//  背景：这批 FBX 只有几何与 UV，材质/贴图未随模型导入
//        （原包被 BNDLTOOL 重打包，跨包材质引用无法可靠还原）。
//        贴图已单独导出到 Assets/_Project/Art/Textures/Extracted/。
//
//  这个脚本做两件事：
//    1) 扫描选定目录下的 FBX，按「模型名 ↔ 贴图名」的相似度推荐贴图并可选自动创建材质；
//    2) 生成一份匹配报告，方便你人工核对没匹配上的。
//
//  用法：菜单  Tools ▸ 末日生存 ▸ 提取资源贴图匹配…
//        或在 Project 窗口右键一个文件夹 ▸ 同样菜单（只处理该文件夹）
//
//  放在 Assets/_Project/Editor/ 下即可（编辑器脚本，不进运行时构建）。
// ---------------------------------------------------------------------------

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace DSH.ExtractedAssets
{
    public class TextureMatcherWindow : EditorWindow
    {
        private const string DefaultTextureDir = "Assets/_Project/Art/Textures/Extracted";
        private const string MaterialDir       = "Assets/_Project/Art/Materials/Extracted";
        private const string ReportPath        = "Assets/_Project/Docs/提取资源贴图匹配报告.md";

        private string _modelFolder = "Assets/_Project/Art/Environment";
        private string _textureFolder = DefaultTextureDir;
        private float _threshold = 0.62f;
        private bool _assignExisting = true;
        private bool _createMissingMaterials = true;
        private int _maxModels = 300;

        private Vector2 _scroll;
        private string _lastSummary = "";

        [MenuItem("Tools/末日生存/提取资源贴图匹配…")]
        public static void Open() => GetWindow<TextureMatcherWindow>("贴图匹配");

        [MenuItem("Assets/末日生存/提取资源贴图匹配…", priority = 100)]
        private static void OpenForSelection()
        {
            var w = GetWindow<TextureMatcherWindow>("贴图匹配");
            var sel = Selection.activeObject;
            if (sel != null)
            {
                var p = AssetDatabase.GetAssetPath(sel);
                if (!string.IsNullOrEmpty(p))
                {
                    if (File.Exists(p)) p = Path.GetDirectoryName(p)?.Replace('\\', '/');
                    w._modelFolder = p;
                }
            }
            w.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("从 XAPK 提取的模型 — 贴图匹配", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "模型只有几何和 UV；贴图是单独导出的一整套。\n" +
                "本工具按模型名与贴图名的相似度为每个模型推荐贴图，并可选自动生成材质。",
                MessageType.Info);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("模型目录", GUILayout.Width(70));
                _modelFolder = EditorGUILayout.TextField(_modelFolder);
                if (GUILayout.Button("选", GUILayout.Width(30)))
                {
                    var p = EditorUtility.OpenFolderPanel("选择模型目录", "Assets", "");
                    if (!string.IsNullOrEmpty(p))
                    {
                        var idx = p.Replace('\\', '/').IndexOf("/Assets/", StringComparison.Ordinal);
                        if (idx >= 0) _modelFolder = p.Replace('\\', '/').Substring(idx + 1);
                    }
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("贴图目录", GUILayout.Width(70));
                _textureFolder = EditorGUILayout.TextField(_textureFolder);
            }

            _threshold = EditorGUILayout.Slider("匹配阈值", _threshold, 0.3f, 0.95f);
            _assignExisting = EditorGUILayout.Toggle("给已导入材质贴图", _assignExisting);
            _createMissingMaterials = EditorGUILayout.Toggle("为未匹配材质者新建材质", _createMissingMaterials);
            _maxModels = EditorGUILayout.IntSlider("单次最多处理", _maxModels, 10, 5000);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("扫描并预览（不写文件）", GUILayout.Height(28)))
                    Run(false);
                if (GUILayout.Button("执行并生成报告", GUILayout.Height(28)))
                    Run(true);
            }

            if (!string.IsNullOrEmpty(_lastSummary))
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("上次结果", EditorStyles.boldLabel);
                _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(120));
                EditorGUILayout.TextArea(_lastSummary);
                EditorGUILayout.EndScrollView();
            }
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.ToLowerInvariant();
            s = Regex.Replace(s, @"^t_|^m_", "");
            s = Regex.Replace(s, @"_[0-9a-f]{4,}$", "");       // 去 hex 后缀
            s = Regex.Replace(s, @"_\d+x\d+(_\d+)?$", "");     // 去分辨率后缀
            s = Regex.Replace(s, @"[^a-z0-9]+", " ");
            return s.Trim();
        }

        private static double Similarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
            var ta = new HashSet<string>(a.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            var tb = new HashSet<string>(b.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            if (ta.Count == 0 || tb.Count == 0) return 0;
            int inter = ta.Intersect(tb).Count();
            double jac = (double)inter / (ta.Count + tb.Count - inter);

            // 包含关系加成
            if (a.Contains(b) || b.Contains(a)) jac = Math.Max(jac, 0.75);
            return jac;
        }

        private void Run(bool execute)
        {
            if (!AssetDatabase.IsValidFolder(_textureFolder))
            {
                EditorUtility.DisplayDialog("贴图目录不存在", _textureFolder, "好");
                return;
            }
            if (!AssetDatabase.IsValidFolder(_modelFolder))
            {
                EditorUtility.DisplayDialog("模型目录不存在", _modelFolder, "好");
                return;
            }

            var texPaths = AssetDatabase.FindAssets("t:Texture2D", new[] { _textureFolder })
                .Select(AssetDatabase.GUIDToAssetPath).ToArray();
            if (texPaths.Length == 0)
            {
                EditorUtility.DisplayDialog("没找到贴图", _textureFolder, "好");
                return;
            }
            var texByNorm = new Dictionary<string, string>();
            foreach (var p in texPaths)
            {
                var n = Normalize(Path.GetFileNameWithoutExtension(p));
                if (!texByNorm.ContainsKey(n)) texByNorm[n] = p;
            }

            var modelPaths = AssetDatabase.FindAssets("t:Model", new[] { _modelFolder })
                .Select(AssetDatabase.GUIDToAssetPath).Take(_maxModels).ToArray();

            if (execute && _createMissingMaterials && !AssetDatabase.IsValidFolder(MaterialDir))
            {
                Directory.CreateDirectory(MaterialDir);
                AssetDatabase.Refresh();
            }

            int matched = 0, unmatched = 0, created = 0, assigned = 0;
            var report = new StringBuilder();
            report.AppendLine("# 提取资源 · 贴图匹配报告");
            report.AppendLine();
            report.AppendLine($"- 模型目录：`{_modelFolder}`（{modelPaths.Length} 个）");
            report.AppendLine($"- 贴图目录：`{_textureFolder}`（{texPaths.Length} 张）");
            report.AppendLine($"- 匹配阈值：{_threshold:0.00}");
            report.AppendLine();
            report.AppendLine("| 模型 | 匹配贴图 | 相似度 | 处理 |");
            report.AppendLine("|---|---|---:|---|");

            try
            {
                for (int i = 0; i < modelPaths.Length; i++)
                {
                    var mp = modelPaths[i];
                    if (execute && i % 25 == 0)
                        EditorUtility.DisplayProgressBar("贴图匹配", mp, (float)i / modelPaths.Length);

                    var mn = Normalize(Path.GetFileNameWithoutExtension(mp));
                    string best = null; double bestScore = 0;
                    foreach (var kv in texByNorm)
                    {
                        double s = Similarity(mn, kv.Key);
                        if (s > bestScore) { bestScore = s; best = kv.Value; }
                    }

                    string action = "未匹配";
                    if (best != null && bestScore >= _threshold)
                    {
                        matched++;
                        action = "已匹配";
                        if (execute)
                        {
                            if (_assignExisting) assigned += AssignToImporter(mp, best);
                            if (_createMissingMaterials) created += CreateMaterial(mp, best);
                        }
                    }
                    else
                    {
                        unmatched++;
                        if (execute && _createMissingMaterials)
                            created += CreateMaterial(mp, null);
                    }

                    report.AppendLine($"| `{Path.GetFileName(mp)}` | " +
                                      (best != null ? $"`{Path.GetFileName(best)}`" : "—") +
                                      $" | {bestScore:0.00} | {action} |");
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (execute)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath) ?? "Assets");
                File.WriteAllText(ReportPath, report.ToString(), new UTF8Encoding(false));
                AssetDatabase.ImportAsset(ReportPath);
            }

            _lastSummary =
                $"模型 {modelPaths.Length} 个\n" +
                $"匹配上 {matched} 个，未匹配 {unmatched} 个\n" +
                $"贴图 {texPaths.Length} 张\n" +
                (execute
                    ? $"已给材质贴图 {assigned} 处，新建材质 {created} 个\n报告：{ReportPath}"
                    : "（预览模式，未写入任何文件）");
            Repaint();
            Debug.Log("[贴图匹配] " + _lastSummary);
        }

        /// <summary>把贴图直接挂到 FBX 导入器的材质槽（影响该模型所有材质）。</summary>
        private static int AssignToImporter(string modelPath, string texPath)
        {
            var importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
            if (importer == null) return 0;
            var tex = AssetDatabase.LoadAssetAtPath<Texture>(texPath);
            if (tex == null) return 0;

            var mats = importer.GetExternalObjectMap();
            var src = importer.GetExternalObjectMap()
                .Where(kv => kv.Key.type == typeof(Material)).ToArray();
            if (src.Length == 0) return 0;

            int n = 0;
            foreach (var kv in src)
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GetAssetPath(kv.Value));
                if (m == null) continue;
                m.mainTexture = tex;
                EditorUtility.SetDirty(m);
                n++;
            }
            return n;
        }

        /// <summary>为该模型建一个同名材质（贴图可空），放到 MaterialDir。</summary>
        private static int CreateMaterial(string modelPath, string texPath)
        {
            var baseName = Path.GetFileNameWithoutExtension(modelPath);
            // 文件名形如 <网格名>_<bundle短名>_<hex>，取第一段做材质名
            var parts = baseName.Split('_');
            var matName = parts.Length > 0 ? parts[0] : baseName;
            var matPath = $"{MaterialDir}/M_{matName}.mat";
            if (File.Exists(matPath))
            {
                if (!string.IsNullOrEmpty(texPath))
                {
                    var exist = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                    var t = AssetDatabase.LoadAssetAtPath<Texture>(texPath);
                    if (exist != null && t != null && exist.mainTexture == null)
                    {
                        exist.mainTexture = t;
                        EditorUtility.SetDirty(exist);
                    }
                }
                return 0;
            }

            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null) return 0;
            var mat = new Material(shader) { name = $"M_{matName}" };
            // 提取资源里树叶/布片/格栅大量是单面片几何，默认给双面，避免缺面
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
            mat.DisableKeyword("_CULL_FRONT");
            mat.DisableKeyword("_CULL_BACK");
            if (!string.IsNullOrEmpty(texPath))
            {
                var t = AssetDatabase.LoadAssetAtPath<Texture>(texPath);
                if (t != null) mat.mainTexture = t;
            }
            AssetDatabase.CreateAsset(mat, matPath);
            return 1;
        }
    }
}
#endif
