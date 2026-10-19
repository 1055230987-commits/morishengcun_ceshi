# 验证工具、环境与使用方法

更新：2026-10-19。正常 Unity 导入、编辑器回归和完整打包是主要验证流程；本机缓存编译只是补充。

## 环境

| 用途 | 要求 |
|---|---|
| Unity 回归 | Unity 6000.3.10f1；等待 Packages 成功解析及脚本编译。当前 URP 17.3.0、Input System 1.18.0、AI Navigation 2.0.10，实际以工程包文件为准。 |
| 独立逻辑断言 | .NET SDK 10，运行两个测试工程。NuGet.Config 清空外部源，工程不使用额外 NuGet 包。首次仍会生成 obj/bin。 |
| 静态语法辅助检查 | Python 3；两个脚本只有标准库依赖。它们不能替代编译器。 |
| 编译参数与隔离副本工具 | Node.js 18.17 或更高，无 npm 依赖。已在 Node.js 24 环境验证。 |

## 常规入口

在工程根目录运行：

```powershell
dotnet run --project OptimizationReview/StairSnapChecks/StairSnapChecks.csproj
dotnet run --project OptimizationReview/SkyMathChecks/SkyMathChecks.csproj
python OptimizationReview/check_csharp_quotes.py Assets/_Project
python OptimizationReview/check_shader_properties.py
```

StairSnapChecks 编译链接工程真实逻辑源码；Unity 框架替代类型位于 TestStubs，仅验证逻辑，不验证物理或渲染。SkyMathChecks 验证昼夜数学。断言数量会随源码变化，以当次输出为准。

Unity 编辑模式中，先保存未命名场景，再使用“游戏 → 工程”下的独立验证菜单：敌人导航、圆形仇恨与死亡复活、背包拖放装备与预览。测试会生成报告或临时测试资源；不要在未保存编辑工作时启动测试。自动测试不能代替主地图 Play、GPU 画面、手感、性能和完整打包验收。

## prepare-compilation.cjs：本机编译参数

```powershell
node Tools/Validation/prepare-compilation.cjs --dry-run
node Tools/Validation/prepare-compilation.cjs
```

从 Library/Bee/artifacts 中选择最近的 Game.Runtime、Game.Editor 编译响应文件，输出到被忽略的 OptimizationReview/validation。可用 `--artifacts <目录>` 指定来源。工具补入当前源码和必要的编辑器引用，不修改游戏源码、不生成 .meta 或重写 GUID。

必须先让 Unity 完成一次导入和编译。全新克隆缺少 Library 缓存时该入口会明确停止；缓存不是应提交的源码。生成后从工程根目录，用对应 Unity 安装目录中的 NetCoreRuntime/dotnet 与 DotNetSdkRoslyn/csc.dll 依次编译 Runtime、Editor 响应文件。安装路径因机器而异，不在脚本中硬编码。本机编译通过不等于完成 Player 打包。

## stage-unity-project.cjs：独立工程副本

```powershell
node Tools/Validation/stage-unity-project.cjs --dry-run
node Tools/Validation/stage-unity-project.cjs
```

每次在 OptimizationReview/validation-projects 中创建独立时间戳目录，只复制 Assets、Packages、ProjectSettings，不覆盖既有副本，不复制 Library、.git 和旧验证产物。需要足够磁盘空间；首次打开仍需 Unity 解析包。历史上此环境出现过独立副本包解析失败，所以“副本已建立”不能标记为“测试通过”。

两个工具使用自身路径定位工程，不依赖调用时的当前目录。`--dry-run` 不写文件。

## 历史档案

`Archive/2026-10/` 保存十个原始 .cjs 和旧 README。原文按字节归档，备注列出写入范围、备份依赖和限制。旧脚本中的固定 ID 数量、固定缓存目录与历史快照只适用于当时；不要把它们直接作为当前验收标准，也不要批量执行修复脚本。
