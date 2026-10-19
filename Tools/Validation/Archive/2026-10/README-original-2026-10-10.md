# Unity 原型优化记录（2026-10-10）

原始 Assets、Packages、ProjectSettings 位于 backup-2026-10-10/。完整变更清单见 changed-files.json，重复 ID 迁移映射见 id-migration.json。项目原先没有 Git。

## 已完成

- 默认构建入口切到原型场景，保留模板资产。
- 箱子和树木保留背包装不下的产出；补给箱只缩放一次；树木复位恢复位置与原材质。
- 昼夜光照统一通过 Update 刷新，移除重复订阅，固定时间仍支持天空跟随相机。
- 建筑和树木拥有独立透明材质，恢复时重新挂回原材质；销毁、重新缓存时释放副本。预览模型兜底材质也有释放路径。
- 后期处理在编辑器预览和运行时均使用独立 Profile 和组件副本，禁用时释放并恢复之前的实例。
- 修复 129 个对象的重复 ID；场景共 298 个唯一 ID，原有唯一 ID 保留。
- 49 个交互碰撞体切到 Interactable（第 8 层）；查询缓冲满时扩容重查，排除已不可交互目标。
- 灰盒生成后统一配置交互层和消除重复 ID；构建时拒绝空或重复 ID。普通编辑不会自动重写存档键。
- HUD 背包文本按事件缓存，只在重绘时绘制；消息按交互操作计时消失。
- 关闭建筑位置日志，删除 4 个无引用空脚本及其 .meta。
- Mobile URP 开启水面 Shader 依赖的 Depth/Opaque Texture。这是兼容性修复，会增加渲染成本；简化水面仍需真机验证后决定。

## 验证

- Unity 自带 Roslyn 编译 Game.Runtime、Game.Editor：通过。
- 移除 UNITY_EDITOR 宏和 UnityEditor 引用后编译运行时代码：通过；这不是完整 Player 构建。
- 静态检查：298 个唯一 ID、2420 个 YAML 文档身份与顺序不变、内部引用无悬空，入口 GUID 和交互层配置正确。
- 原项目 Packages/manifest.json 未修改。
- 已提供 8 项 Unity 回归检查，但本环境未执行到测试：独立副本包管理器报 The "path" argument must be of type string. Received undefined。原始清单和缓存重试仍失败；禁用包管理器后无法加载 URP/Input System。因此不能将运行回归记为通过。
- 未执行完整构建、Play Mode 验收或 CPU/GPU/真机性能测量，未声称具体帧率提升。

## 验收与复跑

编辑器此前已打开。处理未保存的编辑后，请从磁盘重新打开原型场景，避免旧内存场景覆盖本次文件。

1. 菜单「游戏 → 工程 → 校验当前场景 ID」应通过。
2. 背包只剩 1 个堆叠空间时领取 2 个产出，腾出空间后应还能领取剩余 1 个。
3. 反复进出建筑，检查墙体透明恢复、楼层碰撞；连续砍树并复位，检查位置、产出和材质数量。
4. 昼夜切换及禁用/启用后期控制器，检查共享 Profile 是否保持原值。
5. 在目标设备测量室外、建筑切换、连续采集、背包界面的帧时间和 GC。

运行 node OptimizationReview/verify-static.cjs 可重跑静态检查。prepare-validation.cjs 生成使用本机 Unity 现有依赖的编译响应文件。Unity 独立副本在包解析正常后可通过 -executeMethod Game.EditorTools.OptimizationRegressionChecks.RunBatch 运行回归。

本轮保留现有原型 UI 布局。正式 UI 改版、对象池、渲染降档应在目标设备与性能测量明确后另做。
