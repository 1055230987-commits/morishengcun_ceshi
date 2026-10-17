# 末日生存 · 第一阶段原型（Player / Camera / World Boundary / 灰盒）

本文档说明**当前阶段实际做了什么、每个脚本的职责、Inspector 要配什么、怎么测**，
以及**哪些地方是故意留白的、以后扩展时要改哪里**。

---

## 0. 工程现状（动手前的事实）

| 项 | 值 |
| --- | --- |
| Unity 版本 | `6000.3.10f1` |
| 渲染管线 | URP 17.3.0 |
| 输入系统 | Input System **1.18.0**，`activeInputHandler: 1` → **只启用新 Input System** |
| 旧输入 API | `UnityEngine.Input.GetAxis` 之类**会直接抛异常**，不要用 |
| 已有场景 | `Assets/Scenes/SampleScene.unity`（模板场景，**未改动**） |
| 输入动作资产 | `Assets/InputSystem_Actions.inputactions`（含 `Player/Move`、`Player/Look`、`Player/Interact`） |
| 动作包装类 | 该资产的 `.meta` 里 `generateWrapperCode: 0` → **不会生成 `InputSystem_Actions` 类**，代码里不要引用它 |

> 提醒：`SetNamedSecurityInfoW failed` 那条报错与本工程无关，是沙箱环境的 PowerShell 权限问题。

---

## 1. 目录结构（本阶段实际创建的文件）

```
Assets/
├── Scenes/
│   └── Prototype_Greybox.unity      ← 新建原型场景（相机 + 光 + 地图边界，几何体由工具生成）
└── Game/
    ├── Player/
    │   ├── Input/
    │   │   ├── MovementIntent.cs           移动意图（输入层与移动层之间的唯一契约）
    │   │   ├── ICharacterInputSource.cs    输入源接口（键盘/手柄/摇杆/AI/网络都实现它）
    │   │   └── PlayerInputSource.cs        本地输入源：唯一直接读键盘/手柄的脚本
    │   ├── Movement/
    │   │   └── PlayerController.cs         角色移动执行者：消费意图 + 边界裁剪，不读输入
    │   └── Interaction/
    │       └── IInteractable.cs            交互接口 + 交互请求/结果（暂未接具体交互）
    ├── Camera/
    │   └── TopDownCameraRig.cs             斜俯视跟随相机（只跟随，不管输入/角色）
    ├── World/
    │   ├── Area/
    │   │   ├── MapAreaDefinition.cs        地图数据（ScriptableObject）：尺寸/边界/层数/出生点
    │   │   ├── AreaBounds.cs               场景里的「本场景是哪张地图」入口 + 边界裁剪 API
    │   │   └── Area_Prototype.asset        原型地图数据资产
    │   └── Materials/
    │       ├── M_GreyGround.mat / M_GreyStructure.mat / M_GreyProp.mat
    ├── Save/
    │   └── WorldObjectId.cs                稳定对象 ID（为存档/联机预留，当前仅承载数据）
    └── GreyboxLevelBuilder.cs              一键生成灰盒场景（#if UNITY_EDITOR 包裹）
```

> **为什么生成器不放在 `Editor/` 文件夹里**：放进去会被编进 `Assembly-CSharp-Editor`，
> 而本工程的增量编译在这条跨程序集引用上不稳定（反复出现"找不到类型"，且 Play 按钮被禁用）。
> 放在普通目录 + `#if UNITY_EDITOR`：运行时构建里等于不存在，编辑器里又能直接看到运行时类型，没有跨程序集问题。
> `Assets/Game/Editor/` 目录已经空掉，可以直接删除。

**未创建（按你的要求刻意不做）**：背包、物品数据、建筑网格、存档读写、战斗、动画、UI、任务、联机。
`Items/`、`Building/` 文件夹同样留空不建，避免提前架构。

---

## 2. 每个脚本干什么

### 2.1 `MovementIntent`（struct）
一次移动请求的纯数据：`Direction`（世界方向，长度即强度）、`SpeedMultiplier`、`FacingDirection`、
`InteractPressed`、`AimScreenPosition`。
**没有任何 Unity 组件依赖**，所以 AI、回放、网络包都能直接构造它。

### 2.2 `ICharacterInputSource`（interface）
```csharp
MovementIntent CurrentIntent { get; }
bool InputEnabled { get; set; }
```
`PlayerController` 只认这个接口。以后接手柄、虚拟摇杆、AI，只需要新写一个实现，
**不用碰角色移动代码，也不用在移动逻辑里搜 `Input.GetKey`**。

### 2.3 `PlayerInputSource`（MonoBehaviour）
把 Input System 动作翻译成 `MovementIntent`：
- 默认读 **Project-wide 动作**（`Player/Move` = WASD/方向键/左摇杆，`Player/Interact` = E），
  所以**不改任何设置、不拖任何引用就能跑**；
- 也可以在 Inspector 里手动拖 `InputActionReference` 覆盖（做按键重映射/分区输入时用）；
- 移动方向按「相机参考系」换算（`SetMovementReference`），因此相机转视角后 `W` 仍是屏幕上方；
- `InputEnabled = false` 可以冻结输入（以后开背包/过场/死亡时用）。

### 2.4 `PlayerController`（MonoBehaviour，要求 CharacterController）
- 每个 `Update`：取 `CurrentIntent` → 加速/减速得到水平速度 → 加重力 → **交给 `AreaBounds` 裁剪水平位移** → `CharacterController.Move` → 按移动方向平滑转身。
- 不读输入，不做相机逻辑。换成 AI 输入源即可驱动 NPC。
- 撞到边界时会消掉朝外的速度分量，贴边不抖动。

### 2.5 `IInteractable` / `InteractionRequest` / `InteractionResult`
统一交互接口，**当前不实现具体交互**。关键设计：
- 入口是 `Interact(GameObject actor)`，不是「本地玩家按下 E」，所以 AI 和联机玩家都能调用；
- 返回 `InteractionResult` 而不是直接改状态，方便以后加校验/回滚/网络确认；
- `ObjectId` 要求稳定 ID（存档与同步都依赖它）。
  具体实现（树/箱子/门/工作台）留到下一阶段。

### 2.6 `MapAreaDefinition`（ScriptableObject）
地图数据：`MapId`、显示名、`SizeXZ`（边界尺寸）、`WalkableInset`（可行走区比外框内收多少）、
`FloorCount` / `LowestFloorIndex`（**多层建筑预留，当前恒为 1 层**）、出生点。
提供 `ClampMove / ClampPosition / Contains`。

> 「当前位置」在概念上应是 `(X, Z, Floor)` 三元组，而不是只看 X/Z。
> 本阶段 Floor 恒为 0，所以 `MapAreaDefinition` 里只保留了层数字段，
> **每层各自的地块数据**等做家园建造时再引入（见第 6 节）。

### 2.7 `AreaBounds`（MonoBehaviour）
挂在场景根节点 `World` 上：
- 持有 `MapAreaDefinition` 引用 → **「这个场景是哪张地图」的唯一入口**；
- 给 `PlayerController` 提供 `ClampMove`；
- 在 Scene 视图选中它时会画出「地图外框（青色）」和「实际可行走范围（黄色）」。
- 忘了指定资产时不会崩，会用回退尺寸并打印警告。

### 2.8 `TopDownCameraRig`（MonoBehaviour）
斜俯视跟随相机，层级会自动建立/校验：
```
Main Camera(主相机)        Camera 本体（位置 = 吊臂前方 boomDistance 处）
└─ 相机吊臂                 本脚本所在物体：水平跟随 Target，并整体看向「视线点」
   └─ 视线点                视线点锚点（只用于可视化调参，无脚本）
   └─（Camera 通过脚本被摆到 Boom 的 +Z 延长线上）
```
- 每帧 `LateUpdate` 用指数平滑跟随 `Target`，**不读输入、不控制角色**；
- 只暴露参数：跟随目标、视点高度、视线前瞻、后撤距离、离地高度、FOV、平滑速度、俯仰角限制；
- 俯仰角不是直接填的，而是由 `高度` / `后撤距离` / `视点高度` 反算：
  `pitch = atan2(后撤距离, 高度 - 视点高度)`。
  默认 18 / 12 / 1.4 → 俯仰约 **53.5°**，吊臂长约 20.1m。
- 想确认实际俯仰角，选中 `相机吊臂`，看 `主相机` 的 Transform 的 X 旋转（应约 53.5）。

### 2.9 `WorldObjectId`（MonoBehaviour）
稳定对象 ID + 类型标签。**刻意不用 `GetInstanceID()`、不用 Hierarchy 路径**——
它们在不同运行/不同客户端/不同加载顺序下都会变，做存档和联机会很难受。
灰盒生成器已经给每个体块写上了形如 `world/wall/north`、`建筑A/屋顶` 的 ID。

### 2.10 `GreyboxPrimitives` / `GreyboxLevelBuilder`（Editor 工具）
- `GreyboxPrimitives`：用 Cube 拼体块的底层工具（块、墙、分组、材质资产）。
- `GreyboxLevelBuilder`：菜单 **`游戏/灰盒/...`**
  - `创建原型场景`：新建并保存 `Prototype_Greybox.unity`（会先问是否保存当前场景）
  - `重建到当前场景`：把灰盒重新生成到当前场景（**只会清理它自己生成的 `世界`/`道具` 子节点**）
  - `把玩家放到场景视图中心`：把「玩家」挪到 Scene 视图中心，方便试位置

生成内容：地面、四面围墙（同时是物理边界）、2 个带门洞/窗洞/屋顶的灰盒建筑、
L 形围墙、集装箱、8 棵树、5 块石头、5 个箱子。**全是普通场景对象，可以手动改、可以删。**

---

## 3. 怎么跑起来（两条路，二选一）

### 路线 A：直接用我已经放好的场景（推荐先试这条）
1. 回到 Unity，等它导入/编译完成（右下角转圈结束）。
2. 打开 `Assets/Scenes/Prototype_Greybox.unity`。
3. 菜单 `游戏 → 灰盒 → 重建到当前场景`（生成地面、围墙、建筑、树、箱子、玩家）。
4. `Ctrl+S` 保存。
5. 按 Play。

### 路线 B：让工具从零建场景
菜单 `游戏 → 灰盒 → 创建原型场景`，会自动建好并保存到
`Assets/Scenes/Prototype_Greybox.unity`，然后直接 Play。

> 两条路都会复用同一个地图资产 `Assets/Game/World/Area/Area_Prototype.asset`，不会重复创建。

---

## 4. Inspector 需要配什么

### 必须做的只有一件事
**什么都不用做**。输入默认走 Project-wide 动作（`Project Settings → Input System → Default Actions Asset`
已经指向 `Assets/InputSystem_Actions.inputactions`），`玩家` 和 `相机吊臂` 的引用由生成器自动接好。

### 想调手感时改这些
| 对象 | 组件 | 字段 | 默认 | 作用 |
| --- | --- | --- | --- | --- |
| `Player` | `PlayerController` | `_moveSpeed` | 4.5 | 移动速度 m/s |
| | | `_acceleration` | 30 | 加减速，越大越"硬" |
| | | `_rotationSpeed` | 720 | 转身速度 度/秒 |
| | | `_useCameraRelativeMovement` | ✔ | 按屏幕方向移动（关掉则按世界轴） |
| | | `_gravity` | −20 | 重力 |
| | | `_intentSourceBehaviour` / `_area` | 自动查找 | 想手动指定输入源或地图时用 |
| `Player` | `PlayerInputSource` | `_moveAction` / `_lookAction` / `_interactAction` | 空 | 留空=用默认动作；想重映射就拖 `InputActionReference` 进来 |
| | | `_reportPointerPosition` | ✔ | 是否上报鼠标位置（以后瞄准/鼠标交互用） |
| `相机吊臂` | `TopDownCameraRig` | `_target` | 玩家 | 跟随目标（生成器已接；AI/载具可换） |
| | | `_lookHeight` | 1.4 | 视线点高度（只参与俯仰角计算） |
| | | `_worldLookOffset` | (0, 1.4, 5.5) | **视线点的世界坐标偏移。Z 越大画面越往前看、角色越靠屏幕下方。因为是世界坐标，角色转身时画面不动（角色被定住）** |
| | | `_backDistance` | 5 | 水平后撤距离（越小越接近正俯视） |
| | | `_height` | 20.06 | 相机离地高度。`俯视角 = atan2(_backDistance, _height - _lookHeight)`，当前约 **74.7°** |
| | | `_pitchLimits` | 20 / 85 | 俯仰角上下限（**约 75° 是"地形铺满画面"的临界点，再高会露天空**） |
| | | `_followTargetYaw` | ✗ | 关闭 = 固定方位视角；勾选 = 视角跟角色朝向转 |
| | | `_fieldOfView` | 50 | FOV（也可以直接改 Camera 组件，两者同步） |
| | | `_followSharpness` | 14 | 跟随平滑（越大越紧跟） |
| | | `_rotationSharpness` | 18 | 转向平滑 |
| `World` | `AreaBounds` | `_definition` | Area_Prototype | **换地图就换这个资产** |
| `Area_Prototype` | `MapAreaDefinition` | `_sizeXZ` | 60 × 60 | 地图尺寸 |
| | | `_walkableInset` | 0.6 | 可行走区内收（约等于围墙厚度） |
| | | `_floorCount` | 1 | 层数（多层建筑预留） |
| | | `_defaultSpawnPosition` | (0, 0.1, −4) | 出生点 |

### 镜头参数怎么调才直观
1. 先调 `_height` 和 `_backDistance`：这两个决定「俯视程度 + 镜头远近」。
2. 再调 `_lookAhead` 决定角色在画面中的高度位置（0.5 左右非常接近正俯视；10 左右接近《地球末日》那种"看前方"）。
3. 最后调 `_fieldOfView`（50~60 之间最接近手游客厅感，想更压抑用 35~40）。

---

## 5. 测试步骤（逐条确认）

### 5.1 玩家移动正常
- [ ] 按 Play，W/A/S/D（或方向键、手柄左摇杆）能动，斜向走速度不会变快（已做归一化）。
- [ ] 松开按键后角色**平滑减速**停住，不是瞬间急停。
- [ ] 角色面朝移动方向（看胶囊头部的朝向小方块）。
- [ ] 手柄插上后**不用改代码**也能动（`Player/Move` 已经绑了 `Gamepad/leftStick`）。

### 5.2 边界有效
- [ ] 一直往一个方向走，会在围墙前停下，**不会翻出去 / 不会掉出地图**。
- [ ] 贴着墙走不会抖动、不会"贴墙吸住"。
- [ ] 选中 Hierarchy 里的 `World`，Scene 视图出现青色外框（60×60）和黄色可行走框。
- [ ] 验证方法二：把 `Area_Prototype` 的 `_sizeXZ` 改成 `20 × 20`，Play 后活动范围应明显变小（改完记得改回 60）。

### 5.3 摄像机跟随正常
- [ ] 角色移动时相机平滑跟随，不抖动、不穿模。
- [ ] 相机始终保持斜俯视；**角色转身时视角方位不变**（固定方位视角，与《地球末日》一致）。
- [ ] 不动时（角色静止）相机完全稳定，没有缓动漂移。

> 说明：默认 `_followTargetYaw = false`（固定方位）。
> 如果你想让视角跟着角色朝向转（例如第三人称追尾手感），在 `相机吊臂` 的
> `TopDownCameraRig` 上勾选 `_followTargetYaw` 即可，无需改代码。

### 5.4 镜头参数可以调节
- [ ] Play 状态下拖动 `相机吊臂` 上的 `_height`，相机高度实时变化，越拉越接近正俯视。
- [ ] 拖动 `_backDistance`，镜头远近变化（0 时几乎垂直俯视）。
- [ ] 拖动 `_lookAhead`，角色在屏幕上的上下位置移动。
- [ ] 拖动 `_fieldOfView`，视野宽窄变化。
- [ ] 选中 `主相机`，看 Transform 的 X 旋转是否约 53.5°（对应 `_height=18` / `_backDistance=12`）。
- [ ] 拖动 `_followSharpness` 能明显感到跟随的"软硬"差别。

### 5.5 灰盒场景可用于测试遮挡与比例
- [ ] 走到 `建筑A`（西边小屋）门口，进门后**屋顶/墙体会挡住里面的视线**，可以判断遮挡是否合理。
- [ ] 走到 `建筑A` 西墙，能看到墙上留了窗洞（下墙裙 0.9m + 窗 1.1m），可以判断窗台高度是否合理。
- [ ] 站在 `箱子_00`（1m 立方）旁边，箱子大约到小腿/膝盖位置 → **说明人物 1.8m 的比例是对的**。
- [ ] 站在任意一棵树旁，树干高约 3m、总高约 5~6m，明显高于人物。
- [ ] 绕 `L形墙_A/B` 走一圈，验证"绕过去才看得到后面"的遮挡关系。
- [ ] 滚轮拉远/拉近（Scene 视图）对整体布局有直觉，后续可以据此定地图尺寸。

---

## 6. 提前指出的问题（现在保持简单，但结构上别写死）

1. **相机方位固定，视线点却按角色朝向前瞻**
   现在 `_followTargetYaw = false`，视角方位固定；但「视线点」仍会沿角色前方偏移 `_lookAhead`。
   角色转身时镜头会有轻微横向偏移（手感上更像"略微看向角色关注方向"）。
   如果觉得晃，把 `_lookAhead` 调小，或让它沿相机前方而不是角色前方偏移（一行改动）。

2. **地图边界用「正方形 + 内收」**
   够用于一块野外区域，但**家园是玩家自己扩建的**，边界应该由「已建造地块」推导，
   而不是一个固定半径。到那时 `MapAreaDefinition` 要拆成
   `MapDefinition`（静态信息）+ `AreaLayout`（每层的地块集合）。
   现在不要做，但**别把边界数据写进场景对象**——我已经放在 SO 里了。

3. **多层建筑：现在的地面是单层平面**
   `FloorCount` 字段已留，但真正做地下室/二楼时，需要：
   - 每层一个「当前层可见性」开关（否则二楼地板会挡着一楼）；
   - 位置改用 `(X, Z, Floor)` 而不是 `Vector3`；
   - 楼梯/电梯的层级切换逻辑。
   现在的 `AreaBounds.ClampMove` 只处理 X/Z，**做多层时必须换成按当前 Floor 取对应边界**。

4. **灰盒对象直接放在场景里**
   对原型是对的（可手动调、Unity 能存），但**存档不能依赖场景对象**。
   已有 `WorldObjectId` 作为稳定键；等做存档时再加
   「SaveData(纯数据) ↔ 场景对象」的绑定/生成层，届时场景里的灰盒可以逐步换成数据驱动的生成。

5. **输入只有一个源**
   `PlayerInputSource` 每帧只读一个动作。以后要做
   「本地分屏 / 手柄热插拔 / 输入重绑定 UI」，建议在它外面再包一层
   `PlayerInputRouter`（按 PlayerIndex 分发），现在不做。

6. **交互接口的 `CanInteract` 需要距离判断**
   现在接口里没有「交互范围」概念。做第一个真实交互物时，建议把范围放在
   `InteractableBase`（抽象基类）里，而不是塞进 `IInteractable` 接口，保持接口最小。

7. **没有 asmdef**
   所有脚本都在 `Assembly-CSharp`，改一行代码就要全量重编译（当前工程编译一次约 2.5s，尚可接受）。
   等脚本超过 ~50 个或想写单元测试时，再拆 `Game.Runtime` / `Game.Editor` 两个 asmdef。

8. **`Assets/Game/Camera/CameraScreenAligner.cs` 是一个空占位文件**
   已无任何代码（类已删除），但文件还在。请在 Project 窗口里右键删除它，保持工程干净。

---

## 7. 昼夜循环与动态光照模块

### 7.1 三个脚本的分工（互相独立，可以只用一个）

| 脚本 | 职责 | 能否单独用 |
| --- | --- | --- |
| `Game/Time/TimeOfDaySystem.cs` | **只管时间**：一整天多少秒、现在是几点、第几天、现在是白天还是夜里。对外发事件给玩法用。 | 可以（纯逻辑，不碰画面） |
| `Game/Environment/DayNightLightingController.cs` | **只管画面**：太阳/月亮方向颜色强度、天空盒、星空、月亮圆盘、雾、环境光。 | 可以 |
| `Game/Environment/DayNightPostProcessing.cs` | **可选**：色调映射 / 曝光 / 对比 / 泛光 / 暗角随时间变化。删掉不影响光照。 | 可以 |

时间约定：`0 点 = 午夜`，`6 点 = 日出`，`12 点 = 正午`，`18 点 = 日落`。
阶段划分（`05~07 黎明 / 07~17 白天 / 17~22 黄昏 / 22~05 夜晚`）写在
`TimeOfDaySystem.EvaluatePhase` 里，要改直接在那一处改。

### 7.2 怎么装到场景里

**菜单 `游戏 → 环境 → 创建昼夜场景`**
→ 新建并保存 `Prototype_Greybox.unity`，自动生成：

```
昼夜环境                      ← 根节点（DayNightEnvironment）
├ TimeOfDaySystem            时间
├ DayNightLightingController 光照
├ DayNightPostProcessing     后期处理
├ 太阳                       平行光，带软阴影
├ 月亮                       平行光，无阴影，只在夜里亮
├ 星空                       粒子，跟随相机，夜里淡入
├ 月亮圆盘                   跟随相机，夜里淡入
└ 全局Volume                 后期处理 Volume
```

> GameObject 名字是中文，但**组件和字段名是英文**（`_sunYaw`、`_dayLengthSeconds`…）。
> 这是刻意的：Unity 靠字段名保存 Inspector 里调好的数据，字段一旦改名，
> 场景里所有已调参数都会清零。所以显示名可以中文，字段名不行。

同时生成两个**会存盘的资产**（重启不丢，可以手改）：
- `Assets/Game/Environment/M_NightSky.mat`（天空盒材质）
- `Assets/Game/Environment/DayNightProfile.asset`（后期处理 Profile）

**如果只是想给现有场景加上：菜单 `游戏 → 环境 → 给当前场景接入昼夜`**

> 安全说明：`Install` 只会**删掉场景里其它多余的平行光**（因为两盏平行光会争 URP 的主光，
> 导致阴影闪烁）。它不会动你的物体、玩家、相机。

### 7.3 玩法怎么接（重点，对应你说的"带玩法"）

时间系统**不直接做任何玩法惩罚**，只把状态和事件给你，避免以后想改规则时要拆代码：

```csharp
using Game.GameTime;
using UnityEngine;

public class NightSpawner : MonoBehaviour
{
    [SerializeField] private TimeOfDaySystem _time;
    [SerializeField] private GameObject _zombiePrefab;

    private void OnEnable()
    {
        _time.OnPhaseChanged += HandlePhaseChanged;
        _time.OnNewDay += day => Debug.Log($"第 {day} 天开始");
        _time.OnHourChanged += (day, hour) => { /* 每小时扣饥饿/结算 */ };
    }

    private void OnDisable()
    {
        _time.OnPhaseChanged -= HandlePhaseChanged;
    }

    private void HandlePhaseChanged(DayPhase phase)
    {
        // 入夜开始刷怪，天亮停止
        bool danger = phase == DayPhase.Night;
        Debug.Log(danger ? "天黑了，危险上升" : "天亮了，危险解除");
    }

    private void Update()
    {
        if (_time.IsNight) { /* 掉温度、开手电、降低视野 */ }
    }
}
```

存档只需要存两个数：`DayNumber` 和 `NormalizedTime`，读档用 `Restore(day, normalizedTime)`。
快进时间：`AdvanceHours(8f)`（例如"睡觉到早上"）。

### 7.4 想调效果改哪里

在 `昼夜环境` 的 `DayNightLightingController` 上：

| 想要的 | 改哪个 |
| --- | --- |
| 一天多长 | `TimeOfDaySystem._dayLengthSeconds`（默认 480 秒 = 8 分钟一天） |
| 夜晚过快点 | `TimeOfDaySystem._nightTimeScale`（1 = 白天一样快，2 = 夜晚快一倍） |
| 太阳从哪个方向升起 | `_sunYaw` |
| 日出日落偏多红 | `_sunColorKeys`（每个时间点一个颜色） |
| 正午多亮 / 夜里全黑 | `_sunIntensityCurve`、`_minAmbientIntensity` |
| 天空多蓝 / 夜空多深 | `_skyTintKeys`、`_dayExposure`、`_nightExposure` |
| 大气浑浊感 | `_dayAtmosphereThickness` / `_nightAtmosphereThickness` |
| 雾多浓、藏边界 | `_dayFogDensity` / `_nightFogDensity` / `_fogColorKeys` |
| 星星数量/亮度 | `_starCount` / `_starBrightness` |
| 月亮大小/补光强度 | `_moonDiscSize` / `_moonMaxIntensity` |

**调试技巧**：勾上 `_debugOverrideTime`，然后拖 `_debugHour` 滑块（0~24），
时间会停在那一格，可以一格格看日出日落，不用等 8 分钟。
（这个滑块只在编辑器里生效，正式包里不受影响。）

### 7.5 有意没做的事（避免性能坑）

- **不写自定义天空盒 shader**：用 Unity 内置 `Skybox/Procedural`。写实度够、性能好、参数可调，
  而且不会因为 shader 写错整个场景变粉。
- **不做体积云 / 体积雾 / 光追阴影**：中低端设备直接跪，俯视角也看不出好处。
- **不做天文级太阳轨迹**：俯视角分辨不出来。
- **不换 HDRP**：为了光照换管线，整个 URP 工程都要动，不值。

### 7.6 已知注意事项

1. **打包前确认 `Skybox/Procedural` 在 Always Included Shaders 里**。
   菜单工具会自动加进去（会打一条 Log）。不加的话打包运行时会 `Shader.Find` 失败 → 天空变粉。
2. **运行时创建的材质/Profile 不会存盘**。所以请用菜单工具生成资产版；
   如果 Inspector 里 `_skyboxMaterial` / `_profile` 为空，脚本会临时兜底（当次运行有效）。
3. **两盏平行光**：`Sun` 有阴影、`Moon` 无阴影。这样只有主光投影，性能最省。
   如果你希望月光也投影，把 `Moon` 的 `Shadows` 打开即可，但夜景阴影开销会翻倍。
4. **夜色不要全黑**：`_minAmbientIntensity` 默认 0.25，保证俯视角还能看清路。
   想更硬核就往 0.05 调——但那样玩家会什么都看不见。

---

## 8. 下一阶段建议顺序

1. 接第一个真实交互：树 → 采集木材，同时引入 `ItemData`（ScriptableObject）+ 极简背包。
2. 按 `(X, Z, Floor)` 引入建筑网格，先把「一层 + 二楼地板 + 楼梯」跑通。
3. 再做存档：`SaveData`（纯数据）+ `WorldObjectId` 绑定 + JSON 落盘。**注意把时间（DayNumber + NormalizedTime）一起存进来**。
4. 用 `TimeOfDaySystem` 的事件接第一版玩法：入夜刷怪 / 天亮停止 / 按天结算。
5. 联机留到最后；届时应只需要替换 `ICharacterInputSource` 的实现和加一层指令下发。
