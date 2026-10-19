# 测试源码与本机验证产物

更新：2026-10-19。当前测试入口、环境要求和历史工具目录见 `../Tools/Validation/README.md`；日常 Git 保存与上传见 `../Tools/Git/README.md`。

## 入库的源码

- `StairSnapChecks/`：工程真实逻辑源码的独立断言；含 TestStubs。需要 .NET SDK 10。
- `SkyMathChecks/`：昼夜数学检查；需要 .NET SDK 10。
- `check_csharp_quotes.py`：C# 引号静态辅助检查；需要 Python 3。
- `check_shader_properties.py`：ShaderLab 属性块辅助检查；需要 Python 3。

两个测试工程的项目文件、Program.cs、NuGet.Config 和桩文件都应提交；bin/obj 不提交。独立断言不能代替 Unity 物理、渲染、主场景 Play 或完整打包验证。

从工程根目录运行：

```powershell
dotnet run --project OptimizationReview/StairSnapChecks/StairSnapChecks.csproj
dotnet run --project OptimizationReview/SkyMathChecks/SkyMathChecks.csproj
python OptimizationReview/check_csharp_quotes.py Assets/_Project
python OptimizationReview/check_shader_properties.py
```

Unity 回归入口位于“游戏 → 工程”菜单；首次导入必须先等待对应版本 Unity 完成包解析与编译。各入口会生成临时资源或结果文件，使用要求见工具说明。

## 不入库的本机资料

本目录中的备份、独立 Unity 副本、Library 衍生编译参数、bin/obj、DLL、日志、测试结果与报告继续忽略。它们不能替代源码与可重复运行的步骤。

旧的散落 .cjs 不再作为维护入口；原样归档在 `../Tools/Validation/Archive/2026-10/`，包括旧 README。旧记录中的 298 个对象、历史备份路径和固定缓存目录只对应当时状态，不是当前验收标准。
