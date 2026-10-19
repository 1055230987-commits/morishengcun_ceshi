# 自己保存与上传工程

## 每次使用

**只保存本地：** 双击工程根目录的 **仅保存到本地Git.cmd**。它固定使用本地保存模式，不询问远程地址、不连接 GitHub。查看清单，输入改动说明，再输入 Y 确认；说明留空或确认时不输入 Y 则取消。

**需要选择本地或上传：** 使用原来的 **保存项目到Git.cmd**，操作如下。

1. 先在 Unity 保存场景、资源和代码，等待保存结束。脚本只提交磁盘文件，不会替你保存编辑器内存中的内容，也不会证明游戏已测试通过。
2. 在工程根目录双击 **保存项目到Git.cmd**，核对文件清单。
3. 选择 `1` 保存到本机，或 `2` 保存并上传到远程仓库。选择 `0`、关闭窗口或确认时不输入 Y，均不会开始提交。
4. 输入一句本次改动说明，例如“调整地图布局，修复背包拖动”，再输入 Y 确认。

本地保存会建立新的 Git 提交。上传会推送当前分支；两者是不同动作。脚本不会自动拉取、合并、重置、清理或强制覆盖远程。Unity 未退出也可保存，但检查期间不要同时保存资源；内容变化会使脚本停止。

## 第一次上传前

需要 Windows、Git for Windows，以及已初始化的工程仓库。Windows 自带 PowerShell 5.1 即可，不要求安装 Python、Node 或 .NET SDK。

本工程目标远程仓库为 https://github.com/1055230987-commits/morishengcun_ceshi.git 。首次克隆通常已经带有 origin，先用 `git remote -v` 检查。只有尚未配置时，才在工程目录配置：

```powershell
git remote add origin <你的仓库地址>
```

将 `<你的仓库地址>` 替换成真实 HTTPS 或 SSH 地址。不要把密码或令牌写进地址、脚本或项目文件。登录交给 Git 的凭据管理器或 SSH。已经有 origin 时，先检查 `git remote -v`，不要重复添加或随意改地址。

若 Git 提示作者身份缺失，设置本仓库的作者名称与邮箱后重试：

```powershell
git config user.name "你的名称"
git config user.email "你的提交邮箱"
```

## 检查和停止条件

- 显示当前工程、分支以及所有修改、删除和新增文件；确认会提交清单中的全部变化。
- 发现合并冲突、未完成的合并／变基、分离 HEAD 或已有手动暂存内容时停止，避免覆盖人工选择。
- 检查新增、修改及删除资源的 `.meta` 配对，包括文件夹 `.meta`。
- 拦截常见生成目录、密钥文件名、私钥和 GitHub 令牌格式；这只是辅助检查，不能代替人工审查。
- 单文件达到 100 MiB 时停止；达到 20 MiB 时提醒。大型资源需先人工决定 Git LFS 等管理方式。
- 确认前不暂存；确认后清单或内容变化则停止。若错误发生在暂存之后，会保留暂存内容供你检查，不自动撤销。
- 远程拒绝、网络中断或登录失败时，本地提交仍然保留。处理原因后选择上传，可在没有新文件变化时重试。

## 命令行入口

从任何目录使用脚本绝对路径均可，默认工程是脚本所在仓库。

```powershell
# 只检查，不暂存、提交或上传
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Tools\Git\Save-Project.ps1 -Mode Preview

# 明确执行本地保存，仍会要求确认
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Tools\Git\Save-Project.ps1 -Mode Save -Message "本次改动说明"
```

自动化测试可使用 `-Yes`，但必须显式指定 `-Mode Save` 或 `SaveAndPush`；有文件变化时还必须指定非空 `-Message`。日常双击不需要这些参数。默认远程名为 origin，可用 `-Remote` 指定其他已配置的远程。

脚本以 UTF-8 BOM 保存，确保 Windows PowerShell 5.1 正确识别中文。双击入口只对本次 PowerShell 进程放行脚本执行，不改全局执行策略。

维护者可用 `node Tools/Git/test-save-project.cjs` 运行隔离集成测试，需要 Node.js 18.17 或更高。测试只在被忽略的 OptimizationReview/git-workflow-tests 下创建独立仓库，使用本地裸仓库测试推送，不连接 GitHub，也不修改主仓库的提交。测试记录保留在对应目录，不随 Git 上传。

2026-10-19 验证：Windows PowerShell 5.1 下 18 项独立仓库检查通过，另已检查根目录双击入口取消后不改变主仓库提交或暂存区。这里记录的是工具检查结果，不代表游戏已完成 Play 或打包验收。
