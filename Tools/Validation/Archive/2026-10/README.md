# 2026 年 10 月历史辅助工具档案

这些文件从 OptimizationReview 原样复制保留，原位置未清理。旧项目说明存为 README-original-2026-10-10.md。归档用于追溯，不是日常批量运行入口。当前入口见上两级 README。

原始环境：Windows；当时使用 Node.js、本机 Unity 6000.3.10f1 及其 Library 缓存。脚本通常要求工作目录为工程根目录，依赖文件路径以源码为准。历史备份、Library、日志和生成物没有随 Git 上传，换机无法仅靠这些原脚本完整复现。

| 原脚本 | 用途／写入范围 | 使用限制 |
|---|---|---|
| prepare-validation.cjs | 生成本机编译响应文件；会补写缺少的 C# .meta | 固定缓存目录 1900b0aE.dag；缺少 .meta 时会创建新 GUID。日常请用新入口，并先让 Unity 导入。 |
| stage-validation.cjs | 把三个工程目录复制到 OptimizationReview/UnityValidation | 固定输出目录，重复运行可能覆盖副本。请用新的时间戳副本入口。 |
| verify-static.cjs | 核对历史场景并生成 changed-files.json | 固定 298 个 ID、历史对象顺序；依赖 backup-2026-10-10。当前场景不适用。 |
| verify-lighting.cjs | 核对一次光照修改并写校验 JSON | 依赖 lighting-backup-2026-10-12 和特定允许改动的对象。不是当前通用检查。 |
| verify-lighting-isolation.cjs | 核对当时 35 盏预览灯隔离结果并写 JSON | 依赖 lighting-root-cause-backup-2026-10-12 及特定场景差异。 |
| fix-sky.cjs | 历史天空修复；包含工程文件写入 | 一次性迁移脚本，执行前必须逐行审查源码、备份和目标路径，不可当作检查工具。 |
| repair-preview-lights.cjs | 历史预览灯修复；写场景 | 针对旧场景状态，不保证重复执行或适用于当前预览。 |
| tune-lighting-contrast.cjs | 历史光照对比度调整；写工程资源／配置 | 参数属于当时场景，不是通用优化按钮。 |
| update-materials.cjs | 历史材质更新；写材质等资源 | 需核对全部路径与当前材质状态后才考虑迁移。 |
| update-scene.cjs | 历史场景更新；写场景等文件 | 一次性修复，不可用于刷新手工布局。 |

没有为归档脚本承诺当前通过结果。任何确需继续使用的功能，应先迁移为有环境检查、有范围约束的当前工具，再记录运行结果。
