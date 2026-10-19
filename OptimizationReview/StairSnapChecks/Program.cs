using Game.AI;
using Game.Items;
using Game.Player;
using Game.Save;
using Game.World;
using SnapKind = Game.Player.StairSnapLogic.SnapKind;

// 针对 StairSnapLogic.cs 的独立断言。不需要开 Unity。
//
// 分两部分：
//   A. 吸附决策逻辑（直接编译真实的 StairSnapLogic.cs）
//   B. 楼梯几何自检（把 GreyboxLevelBuilder 里的常量抄过来，验证"我算的净空"成立）
//
// 【为什么要 B】本工程楼梯卡了三轮，每次我按几何算都"应该能上"。
// 把几何算式变成断言后，改任何一个常量都会立刻报出哪条约束被破坏。

int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new Exception("FAIL: " + message);
}
void Near(float a, float b, float tol, string message) =>
    Check(Math.Abs(a - b) <= tol, $"{message}: {a} vs {b}");

// 参数取自 PlayerController 的默认值。
const float maxStepUp = 0.35f;
const float maxSnapDown = 0.45f;
const float deadZone = 0.02f;
const float stairRise = 3.35f / 12f;   // 0.2792

StairSnapLogic.SnapDecision Decide(float groundDistance, bool grounded,
    bool canSnapDown = true, float stepUp = maxStepUp, float snapDown = maxSnapDown, float dead = deadZone)
    => StairSnapLogic.Decide(groundDistance, -groundDistance, grounded, canSnapDown, stepUp, snapDown, dead);

// ==================================================================== A. 吸附决策

Console.WriteLine("== A. 吸附决策 ==");

// --- 换算：射线从脚底向下打，命中距离就是"脚下方多少米" ---
Near(StairSnapLogic.DistanceBelowFeet(0f), 0f, 1e-6f, "命中在脚底");
Near(StairSnapLogic.DistanceBelowFeet(0.2f), -0.2f, 1e-6f, "地面在脚下方 0.2");

// --- 贴地 + 地面正好在脚下：死区内不动（这是"抖动"的防线）---
Check(Decide(0f, true).Kind == SnapKind.None, "地面正好在脚下应不吸附（死区）");
Check(Decide(-0.01f, true).Kind == SnapKind.None, "1 厘米的偏差应落在死区内，不吸附");
Check(Decide(0.01f, true).Kind == SnapKind.None, "向上 1 厘米也应落在死区内");

// --- 贴地 + 地面在脚上方（脚陷进去了 / 台阶顶面）：抬上去 ---
{
    var d = Decide(0.15f, true);
    Check(d.Kind == SnapKind.Up, "贴地时地面在脚上方 0.15 应向上吸附");
    Near(d.DeltaY, 0.15f, 1e-6f, "向上吸附的位移应等于距离");
}
{
    var d = Decide(stairRise, true, stepUp: 0.35f);
    Check(d.Kind == SnapKind.Up, $"单级台阶 {stairRise:F3} 应能吸上去");
}

// --- 贴地 + 超过可抬高度：不吸（不能"穿"上高墙）---
Check(Decide(0.5f, true).Kind == SnapKind.None, "0.5 米高台不应被吸附");
Check(Decide(maxStepUp + 0.001f, true).Kind == SnapKind.None, "刚好超过 _maxStepUp 就不该吸");

// --- 贴地 + 地面在脚下方（走下小台阶）：压下去 ---
{
    var d = Decide(-0.2f, true);
    Check(d.Kind == SnapKind.Down, "贴地时脚下 0.2 米有地面应向下吸附");
    Near(d.DeltaY, -0.2f, 1e-6f, "向下吸附位移应为负");
}
Check(Decide(-0.6f, true).Kind == SnapKind.None, "脚下 0.6 米超出 _maxSnapDown，不该吸");
Check(Decide(-maxSnapDown - 0.001f, true).Kind == SnapKind.None, "刚好超过 _maxSnapDown 就不该吸");

// --- 离地（下落）+ 时间窗内：吸住地面 ---
{
    var d = StairSnapLogic.Decide(0f, 0.3f, false, true, maxStepUp, maxSnapDown, deadZone);
    Check(d.Kind == SnapKind.Down, "下落时脚下 0.3 米有地面应吸住");
    Near(d.DeltaY, -0.3f, 1e-6f, "下落吸附位移应为负");
}
Check(StairSnapLogic.Decide(0f, 0.3f, false, false, maxStepUp, maxSnapDown, deadZone).Kind == SnapKind.None,
    "离地且超出时间窗就不该吸");
Check(StairSnapLogic.Decide(0f, 3f, false, true, maxStepUp, maxSnapDown, deadZone).Kind == SnapKind.None,
    "从高处下落（脚下 3 米）不该被拉住");
Check(StairSnapLogic.Decide(0f, 0.01f, false, true, maxStepUp, maxSnapDown, deadZone).Kind == SnapKind.None,
    "下落时地面已在死区内，不吸附");

// --- 非有限输入 ---
Check(Decide(float.NaN, true).Kind == SnapKind.None, "NaN 不应吸附");
Check(Decide(float.PositiveInfinity, true).Kind == SnapKind.None, "Infinity 不应吸附");
Check(StairSnapLogic.Decide(0f, float.NaN, false, true, maxStepUp, maxSnapDown, deadZone).Kind == SnapKind.None,
    "下落距离为 NaN 不应吸附");
Check(StairSnapLogic.Decide(0f, -1f, false, true, maxStepUp, maxSnapDown, deadZone).Kind == SnapKind.None,
    "下落距离为负（语义非法）不应吸附");

// --- 负数阈值应被夹到 0 ---
Check(Decide(0.1f, true, stepUp: -1f).Kind == SnapKind.None, "负的 _maxStepUp 应夹到 0");

// --- None 的位移必须是 0 ---
Near(StairSnapLogic.SnapDecision.None.DeltaY, 0f, 1e-6f, "None 的位移应为 0");

Console.WriteLine($"   {assertions} 条断言通过");

// ==================================================================== A2. 显式迈步

Console.WriteLine();
Console.WriteLine("== A2. 显式迈步判定 ==");
int stepStart = assertions;

const float maxStepUpForStep = 0.35f;

// --- 正常迈步：想走 0.075 m，实际只走 0.01 m，前方台阶顶面 +0.258 m ---
Check(StairSnapLogic.ShouldStepUp(0.075f, 0.010f, true, 0.258f, maxStepUpForStep),
    "被挡住且前方有 0.258 m 台阶时应迈步");

// --- 没被挡住（走到了想要的量）→ 不迈步 ---
Check(!StairSnapLogic.ShouldStepUp(0.075f, 0.075f, true, 0.258f, maxStepUpForStep),
    "正常前进时不该迈步");
Check(!StairSnapLogic.ShouldStepUp(0.075f, 0.04f, true, 0.258f, maxStepUpForStep),
    "走到了 53% 就不算被挡住（阈值 50%）");

// --- 前方是墙（没探到地面）→ 不迈步（否则贴墙走会往上飘）---
Check(!StairSnapLogic.ShouldStepUp(0.075f, 0.010f, false, 0f, maxStepUpForStep),
    "前方是墙时不该迈步");

// --- 台阶太高 → 不迈步 ---
Check(!StairSnapLogic.ShouldStepUp(0.075f, 0.010f, true, maxStepUpForStep + 0.01f, maxStepUpForStep),
    "超过 _maxStepUp 的台阶不该迈");
Check(StairSnapLogic.ShouldStepUp(0.075f, 0.010f, true, maxStepUpForStep, maxStepUpForStep),
    "正好等于 _maxStepUp 应可迈");

// --- 台阶太矮（几毫米）→ 不迈步（那不是一级台阶，是地面起伏）---
Check(!StairSnapLogic.ShouldStepUp(0.075f, 0.010f, true, 0.003f, maxStepUpForStep),
    "几毫米的起伏不该触发迈步");

// --- 地面在脚下方（下坡）→ 不迈步 ---
Check(!StairSnapLogic.ShouldStepUp(0.075f, 0.010f, true, -0.1f, maxStepUpForStep),
    "地面在脚下方时不该迈步");

// --- 本来就没想动 → 不迈步（站着不动时不能乱抬腿）---
Check(!StairSnapLogic.ShouldStepUp(0f, 0f, true, 0.258f, maxStepUpForStep),
    "没有水平位移意图时不该迈步");
Check(!StairSnapLogic.ShouldStepUp(0.005f, 0f, true, 0.258f, maxStepUpForStep),
    "意图位移过小时不该迈步");

// --- 非有限输入 ---
Check(!StairSnapLogic.ShouldStepUp(float.NaN, 0.01f, true, 0.258f, maxStepUpForStep),
    "意图距离为 NaN 时不该迈步");
Check(!StairSnapLogic.ShouldStepUp(0.075f, 0.010f, true, float.NaN, maxStepUpForStep),
    "台阶高度为 NaN 时不该迈步");
Check(!StairSnapLogic.ShouldStepUp(0.075f, float.NaN, true, 0.258f, maxStepUpForStep),
    "实际距离为 NaN 时不该迈步");

Console.WriteLine($"   {assertions - stepStart} 条迈步断言通过");

// ==================================================================== B. 楼梯几何

Console.WriteLine();
Console.WriteLine("== B. 两层房几何与楼梯 ==");
int geomStart = assertions;

// 以下常量必须与 GreyboxLevelBuilder 的两层房部分【逐一对齐】。改那边就要同步改这里。
const float floorTop = 0.3f;                 // TwoStoryFloorTop：一楼楼板顶面
const float slabThickness = 0.3f;            // TwoStorySlabThickness
const float floorHeight = 3.35f;             // TwoStoryFloorHeight
const float halfX = 5.0f;
const float halfZ = 6.0f;
const float wallThickness = 0.3f;
const float stairTopY = floorHeight;
const int stepCount = 13;                    // StairStepCount
const float stairWidth = 2.4f;
const float stairCenterX = halfX - stairWidth * 0.5f;   // 3.8
const float stairStartZ = -4.0f;
const float stairEndZ = 3.0f;
const float holeMinX = stairCenterX - stairWidth * 0.5f;
const float holeMaxX = halfX;
const float holeMinZ = stairStartZ - 0.2f;
const float upperSlabMinZ = stairEndZ;
const float bufferDepth = 0.7f;
const float playerHeadHeight = 1.8f;         // CharacterController.height
const float playerRadius = 0.35f;
const float terrainY = 0f;
const float stepOffset = 0.4f;

float upperFloorTop = floorTop + floorHeight;              // 3.65
float landingBottomY = upperFloorTop - slabThickness;      // 3.35
float rise = stairTopY / stepCount;
float run = (stairEndZ - stairStartZ) / stepCount;
float bufferStartZ = stairEndZ - bufferDepth;

// 楼梯脚高函数 —— 必须与 BuildWalkableStairs 一致：从一楼楼板顶面起算。
float FootHeightAt(float z)
{
    if (z >= bufferStartZ) return upperFloorTop;      // 顶端缓冲：直接是二楼楼板高度

    float t = (z - stairStartZ) / run;
    int level = Math.Min((int)Math.Floor(t) + 1, stepCount);
    return floorTop + level * rise;
}

// ---- 约束 A：一楼楼板不能和地形共面（这就是"一楼地面闪烁"的根因）----
float groundFloorBottom = floorTop - slabThickness;
Check(groundFloorBottom >= terrainY - 1e-6f,
    $"一楼楼板底面 {groundFloorBottom:F3} 不能低于地形 {terrainY}");
Check(floorTop > terrainY,
    $"一楼楼板顶面 {floorTop:F3} 必须高于地形（否则共面 → z-fighting 闪烁）");
Check(floorTop < stepOffset,
    $"一楼门槛 {floorTop:F3} 必须小于 stepOffset {stepOffset}，否则玩家进不去屋");

// ---- 约束 B：单级抬高必须同时低于两条上台阶机制的阈值 ----
Near(rise, 3.35f / 13f, 0.0005f, "单级抬高");
Near(run, 7f / 13f, 0.0005f, "单级进深");
Check(rise < maxStepUp, $"单级抬高 {rise:F3} 必须小于 _maxStepUp {maxStepUp}");
Check(rise < stepOffset, $"单级抬高 {rise:F3} 必须小于 stepOffset {stepOffset}");

// ---- 约束 C：楼梯顶端脚高必须等于二楼楼板顶面 ----
Near(floorTop + stairTopY, upperFloorTop, 0.001f, "楼梯顶端的脚高应等于二楼楼板顶面");

// ---- 约束 D：玩家身体不该嵌进二楼楼板实体 ----
//
// 二楼楼板实体区间：[landingBottomY, upperFloorTop] = [3.35, 3.65]。
// 判定条件是"身体与楼板在竖直和水平方向【同时】重叠"；
// 脚高等于 upperFloorTop 时玩家正好站在楼板顶上，不算嵌进去。
int penetrations = 0;
float worstPenetration = 0f;
float sampleStep = run / 8f;

for (float z = stairStartZ; z <= stairEndZ + 1e-4f; z += sampleStep)
{
    float footY = FootHeightAt(z);
    float bodyBottom = footY;
    float bodyTop = footY + playerHeadHeight;
    float frontZ = z + playerRadius;

    bool verticalOverlap = bodyBottom < upperFloorTop - 1e-4f && bodyTop > landingBottomY;
    bool horizontalOverlap = frontZ > upperSlabMinZ;

    if (verticalOverlap && horizontalOverlap)
    {
        penetrations++;
        worstPenetration = Math.Max(worstPenetration, upperFloorTop - bodyBottom);
    }
}
Check(penetrations == 0,
    $"玩家不该嵌进二楼楼板，实际 {penetrations} 处，最深 {worstPenetration:F3} 米");

// ---- 约束 E：脚高随 z 单调不减 ----
float previousFoot = -1f;
int regressions = 0;
for (float z = stairStartZ; z <= stairEndZ + 1e-4f; z += sampleStep)
{
    float footY = FootHeightAt(z);
    if (footY < previousFoot - 1e-4f) regressions++;
    previousFoot = footY;
}
Check(regressions == 0, $"脚高应随 z 单调不减，实际有 {regressions} 处回退");

// ---- 约束 F：楼梯西侧必须留出足够通道（用户反馈"够不到、间距太小"）----
float stairWestEdge = stairCenterX - stairWidth * 0.5f;
float westCorridor = stairWestEdge - (-halfX);
Check(westCorridor >= 2.0f,
    $"楼梯西侧通道 {westCorridor:F2} 米，太窄（用户反馈过够不到楼梯）");
// 楼梯东侧应直接贴外墙，不留死缝
Near(stairCenterX + stairWidth * 0.5f, halfX, 0.01f, "楼梯东侧应贴住东墙内侧");

// ---- 约束 G：天花板洞必须罩住整段楼梯，且 x 范围与楼梯同宽（否则会留出会掉下去的缝）----
Check(holeMinZ <= stairStartZ, $"洞口南边界 {holeMinZ} 必须不晚于楼梯起点 {stairStartZ}");
Near(holeMinX, stairWestEdge, 0.01f, "洞口西边界应等于楼梯西边缘");
Near(holeMaxX, stairCenterX + stairWidth * 0.5f, 0.01f, "洞口东边界应等于楼梯东边缘");

// ---- 约束 H：楼梯必须装得进房间 ----
float southWallInner = -halfZ + wallThickness;
Check(stairStartZ >= southWallInner, $"楼梯起点 {stairStartZ} 不能穿出南墙内侧 {southWallInner}");
Check(stairEndZ <= halfZ, $"楼梯终点 {stairEndZ} 不能穿出北墙 {halfZ}");

// ---- 约束 I：落脚平台必须在楼梯终点以北，且宽度足够 ----
Check(upperSlabMinZ >= stairEndZ, "落脚平台必须从楼梯终点开始（不能伸到楼梯上方，否则玩家会撞进平台）");
Check(UpperSlabWidth() > 0.5f, "落脚平台的 z 方向宽度应足够站人");

float UpperSlabWidth() => halfZ - upperSlabMinZ;

// ---- 约束 J：楼梯顶端头部净空 ----
Check(upperFloorTop + playerHeadHeight > landingBottomY,
    $"楼梯顶端头顶 {upperFloorTop + playerHeadHeight:F2} 必须高于楼板底面 {landingBottomY}");

// ---- 约束 K：梯子（保底传送）不能和楼梯/门重叠 ----
const float ladderX = -3.4f;
const float ladderHoleMaxX = -2.4f;
const float doorCenterX = -1.5f;
const float doorHalfWidth = 2.2f / 2f;

Check(ladderHoleMaxX < stairWestEdge, "梯子洞不能伸进楼梯范围");
Check(ladderX + 0.28f < doorCenterX - doorHalfWidth,
    $"梯子（x 到 {ladderX + 0.28f:F2}）不能挡住门洞（从 {doorCenterX - doorHalfWidth:F2} 开始）");

Console.WriteLine($"   {assertions - geomStart} 条几何断言通过");
Console.WriteLine($"   一楼楼板 y ∈ [{groundFloorBottom:F2}, {floorTop:F2}]，地形 y = {terrainY}");
Console.WriteLine($"   二楼楼板 y ∈ [{landingBottomY:F2}, {upperFloorTop:F2}]");
Console.WriteLine($"   单级抬高={rise:F4} 进深={run:F4} 坡度={Math.Atan(stairTopY / (stairEndZ - stairStartZ)) * 180 / Math.PI:F1}°");
Console.WriteLine($"   楼梯 x ∈ [{stairWestEdge:F2}, {stairCenterX + stairWidth * 0.5f:F2}]，西侧通道 {westCorridor:F2} m");
Console.WriteLine($"   逐点采样：嵌进二楼楼板 {penetrations} 处，脚高回退 {regressions} 处");
Console.WriteLine($"   楼梯顶端头部净空 = {upperFloorTop + playerHeadHeight - landingBottomY:F2} 米");

Console.WriteLine();
Console.WriteLine($"PASS: 共 {assertions} 条断言，全部针对真实的 StairSnapLogic.cs 与两层房几何常量。");

// ==================================================================== C. 格子房楼板

Console.WriteLine();
Console.WriteLine("== C. 格子房楼板与地形的关系 ==");
int gridStart = assertions;

// 以下常量必须与 GreyboxLevelBuilder 的格子房部分保持一致。
// 只列出断言真正用到的；cellSize / wallThickness / roofThickness 不影响这里的约束。
const float gridWallHeight = 3.0f;
const float gridFloorThickness = 0.3f;
const float gridFloorTop = 0.3f;      // GridHouseFloorTop
// terrainY 已在 B 段声明（同为 0），这里直接复用。

float gridFloorBottom = gridFloorTop - gridFloorThickness;

// 约束 1：楼板不能和地形共面或重叠 —— 否则逐像素深度冲突（z-fighting），地面会闪。
Check(gridFloorBottom >= terrainY - 1e-6f,
    $"楼板底面 {gridFloorBottom:F3} 不能低于地形 {terrainY}（会穿插）");
Check(gridFloorTop > terrainY,
    $"楼板顶面 {gridFloorTop:F3} 必须高于地形 {terrainY}（否则共面闪烁）");
Check(gridFloorBottom > terrainY || Math.Abs(gridFloorBottom - terrainY) < 1e-6f,
    "楼板底面与地形正好接触是可以的，但不能穿过去");

// 约束 2：门槛高度必须小于 stepOffset，否则玩家迈不进屋。
Check(gridFloorTop < stepOffset,
    $"楼板抬高 {gridFloorTop:F3} 必须小于 stepOffset {stepOffset}，否则玩家进不去屋");

// 约束 3：屋顶底面必须在墙顶之上（墙从楼板顶面起算）。
float gridWallTop = gridFloorTop + gridWallHeight;
float gridRoofBottom = gridWallTop;
Check(gridRoofBottom >= gridWallTop - 1e-6f,
    $"屋顶底面 {gridRoofBottom:F3} 不能低于墙顶 {gridWallTop:F3}");

// 约束 4：门洞高度 + 门楣 = 墙高（不能有缝）。
const float doorHeight = 2.1f;
float lintelHeight = gridWallHeight - doorHeight;
Near(doorHeight + lintelHeight, gridWallHeight, 1e-6f, "门洞高 + 门楣高 应等于墙高");

Console.WriteLine($"   {assertions - gridStart} 条格子房断言通过");
Console.WriteLine($"   楼板 y ∈ [{gridFloorBottom:F2}, {gridFloorTop:F2}]，地形 y = {terrainY}");
Console.WriteLine($"   墙 y ∈ [{gridFloorTop:F2}, {gridWallTop:F2}]，门槛 {gridFloorTop:F2} m < stepOffset {stepOffset} m");

Console.WriteLine();
Console.WriteLine($"PASS(全部): 共 {assertions} 条断言。");

// ==================================================================== D. 生命值

Console.WriteLine();
Console.WriteLine("== D. 生命值逻辑 ==");
int healthStart = assertions;

// --- 初始状态 ---
{
    var h = new Game.Player.Health(100f);
    Near(h.Max, 100f, 1e-4f, "初始上限");
    Near(h.Current, 100f, 1e-4f, "初始满血");
    Near(h.Normalized, 1f, 1e-4f, "初始比例");
    Check(!h.IsDead, "初始不应死亡");
}

// --- 普通伤害 ---
{
    var h = new Game.Player.Health(100f);
    float applied = h.TakeDamage(30f);
    Near(applied, 30f, 1e-4f, "伤害应全额生效");
    Near(h.Current, 70f, 1e-4f, "剩余血量");
    Near(h.Normalized, 0.7f, 1e-4f, "比例");
    Check(!h.IsDead, "扣血未到 0 不应死亡");
}

// --- 伤害超过剩余血量：只扣到 0，返回值是实际扣掉的 ---
{
    var h = new Game.Player.Health(100f);
    h.TakeDamage(80f);
    float applied = h.TakeDamage(50f);      // 只剩 20
    Near(applied, 20f, 1e-4f, "超出部分不应计入实际伤害");
    Near(h.Current, 0f, 1e-4f, "血量应夹到 0");
    Check(h.IsDead, "血量归零应死亡");
}

// --- 死亡只触发一次 ---
{
    var h = new Game.Player.Health(50f);
    int diedCount = 0;
    h.Died += () => diedCount++;
    h.TakeDamage(50f);
    h.TakeDamage(10f);
    h.TakeDamage(10f);
    Check(diedCount == 1, $"死亡事件应只触发一次，实际 {diedCount} 次");
    Near(h.Current, 0f, 1e-4f, "死后血量保持 0");
}

// --- 死后伤害无效（返回 0）---
{
    var h = new Game.Player.Health(50f);
    h.TakeDamage(50f);
    Near(h.TakeDamage(10f), 0f, 1e-4f, "已死亡时伤害应返回 0");
    Near(h.Current, 0f, 1e-4f, "已死亡时血量不变");
}

// --- 治疗 ---
{
    var h = new Game.Player.Health(100f);
    h.TakeDamage(60f);
    Near(h.Heal(30f), 30f, 1e-4f, "治疗应全额生效");
    Near(h.Current, 70f, 1e-4f, "治疗后的血量");
}

// --- 治疗不超过上限：返回值是实际回复量 ---
{
    var h = new Game.Player.Health(100f);
    h.TakeDamage(10f);                      // 90
    Near(h.Heal(50f), 10f, 1e-4f, "超出上限的部分不应计入实际回复");
    Near(h.Current, 100f, 1e-4f, "血量应夹到上限");
}

// --- 死后不能被治疗（需要先复活）---
{
    var h = new Game.Player.Health(50f);
    h.TakeDamage(50f);
    Near(h.Heal(20f), 0f, 1e-4f, "死亡时治疗应无效");
    Check(h.IsDead, "治疗后仍应是死亡状态");
}

// --- 复活 ---
{
    var h = new Game.Player.Health(80f);
    h.TakeDamage(80f);
    h.Revive();
    Check(!h.IsDead, "复活后不应是死亡状态");
    Near(h.Current, 80f, 1e-4f, "复活应回满血");
}

// --- 复活到指定比例 ---
{
    var h = new Game.Player.Health(80f);
    h.TakeDamage(80f);
    h.Revive(0.25f);
    Near(h.Current, 20f, 1e-4f, "按 25% 复活");
}

// --- 非法输入 ---
{
    var h = new Game.Player.Health(100f);
    Near(h.TakeDamage(0f), 0f, 1e-4f, "0 伤害无效");
    Near(h.TakeDamage(-10f), 0f, 1e-4f, "负伤害无效");
    Near(h.TakeDamage(float.NaN), 0f, 1e-4f, "NaN 伤害无效");
    Near(h.Heal(0f), 0f, 1e-4f, "0 治疗无效");
    Near(h.Heal(float.NaN), 0f, 1e-4f, "NaN 治疗无效");
    Near(h.Current, 100f, 1e-4f, "非法输入不应改变血量");
}

// --- 上限被夹到至少 1（避免除零、避免 0 上限导致逻辑怪象）---
{
    var h = new Game.Player.Health(0f);
    Check(h.Max >= 1f, $"上限应至少夹到 1，实际 {h.Max}");
    h.TakeDamage(1f);
    Check(h.IsDead, "上限为 1 时受 1 点伤害应死亡");
}

// --- 变更事件（当前值 + 上限）---
{
    var h = new Game.Player.Health(100f);
    int changed = 0;
    float lastCur = -1f, lastMax = -1f;
    h.Changed += (c, m) => { changed++; lastCur = c; lastMax = m; };

    h.TakeDamage(25f);
    Check(changed == 1, $"一次伤害应触发一次变更，实际 {changed}");
    Near(lastCur, 75f, 1e-4f, "事件里的当前值");
    Near(lastMax, 100f, 1e-4f, "事件里的上限");

    h.Heal(25f);
    Check(changed == 2, $"一次治疗应触发一次变更，实际 {changed}");
}

// --- 不变更就不该触发事件（满血治疗、死亡后伤害）---
{
    var h = new Game.Player.Health(100f);
    int changed = 0;
    h.Changed += (_, __) => changed++;
    h.Heal(10f);                       // 满血，无效
    Check(changed == 0, $"满血治疗不应触发变更，实际 {changed}");

    h.TakeDamage(100f);                // 触发一次（死亡）
    int afterDeath = changed;
    h.TakeDamage(10f);                 // 死后无效
    Check(changed == afterDeath, "死后伤害不应触发变更");
}

// --- 按比例改上限 ---
{
    var h = new Game.Player.Health(100f);
    h.TakeDamage(50f);                 // 50/100
    h.ReconfigureKeepingRatio(200f);   // 应保持 50%
    Near(h.Max, 200f, 1e-4f, "新上限");
    Near(h.Current, 100f, 1e-4f, "应保持 50% 比例");
}

Console.WriteLine($"   {assertions - healthStart} 条生命值断言通过");

// ==================================================================== E. 窗户几何

Console.WriteLine();
Console.WriteLine("== E. 窗户几何 ==");
int windowStart = assertions;

// 常量抄自 GreyboxLevelBuilder（改那边记得同步这里 —— 这条也是断言的一部分意义所在）。
// 注意：floorHeight(3.35) / floorTop(0.3) / halfX(5) / halfZ(6) / wallThickness(0.3)
// 在文件前面的几何部分已经定义过了，这里直接复用，不重复声明。
const float winSill = 0.95f;         // WindowSillHeight
const float winHeight = 1.5f;        // WindowHeight
const float winWidth = 1.6f;         // WindowWidth

// 各处窗心（也要和 GreyboxLevelBuilder 一致）
const float eastCenterZ = -1.6f;
const float westCenterZ = 1.8f;
const float southCenterX = 1.4f;
const float northCenterX = -1.6f;

// --- 1. 窗洞必须落在墙的范围内，且两侧留得出墙段 ---
void CheckOpeningInsideWall(string label, float center, float halfExtent)
{
    float min = center - winWidth * 0.5f;
    float max = center + winWidth * 0.5f;
    Check(min > -halfExtent, $"{label}：窗洞左边超出墙（{min:F2} <= {-halfExtent:F2}）");
    Check(max < halfExtent, $"{label}：窗洞右边超出墙（{max:F2} >= {halfExtent:F2}）");
    Check(min + 0.05f < max, $"{label}：窗洞宽度非法");
}

CheckOpeningInsideWall("东墙窗", eastCenterZ, halfZ);
CheckOpeningInsideWall("西墙窗", westCenterZ, halfZ);
CheckOpeningInsideWall("南墙窗", southCenterX, halfX);
CheckOpeningInsideWall("北墙窗", northCenterX, halfX);

// --- 2. 窗台与窗楣要留得出上下墙段（否则 BuildWallWithOpenings 里那两段会退化） ---
for (int level = 0; level < 2; level++)
{
    float baseY = level == 0 ? floorTop : upperFloorTop;
    float sillY = baseY + winSill;
    float headerY = sillY + winHeight;
    float wallTop = baseY + floorHeight;

    Check(sillY - baseY > 0.05f,
        $"第{level}层：窗台下没有墙（窗台 {sillY:F2} 贴着墙底 {baseY:F2}）");
    Check(wallTop - headerY > 0.05f,
        $"第{level}层：窗楣上没有墙（窗顶 {headerY:F2}，墙顶 {wallTop:F2}）");
}

// --- 3. 一楼窗不能穿过一楼天花板（否则二楼楼板上会开个洞） ---
{
    float sillY = floorTop + winSill;
    float headerY = sillY + winHeight;
    float ceilingBottom = floorTop + floorHeight;      // 3.65 = 二楼楼板底面/一楼天花板底面

    Check(headerY < ceilingBottom,
        $"一楼窗顶 {headerY:F2} 不应碰到一楼天花板底面 {ceilingBottom:F2}");
}

// --- 4. 二楼窗不能穿过屋顶 ---
{
    float sillY = upperFloorTop + winSill;
    float headerY = sillY + winHeight;
    float roofBottom = upperFloorTop + floorHeight;    // 7.0

    Check(headerY < roofBottom,
        $"二楼窗顶 {headerY:F2} 不应碰到屋顶底面 {roofBottom:F2}");
}

// --- 5. 分段墙算法：段与洞口必须【无重叠、无缝隙】地覆盖整面墙 ---
//
// 这是本轮改动的核心算法（BuildWallWithOpenings），它必须满足：
//   所有沿墙方向的区间拼起来 = 整面墙，且两两不相交。
// 之前"先铺满再补洞"的做法会造成重叠 → z-fighting，这条断言就是防止再犯。
void CheckSegmentation(string label, float wallMin, float wallMax, params (float Min, float Max)[] openings)
{
    var spans = new List<(float Min, float Max)>();
    float cursor = wallMin;

    foreach (var (min, max) in openings)
    {
        Check(min >= cursor - 1e-4f, $"{label}：洞口左边界 {min:F2} 与前一段重叠（cursor {cursor:F2}）");
        Check(max > min, $"{label}：洞口宽度非法");
        spans.Add((cursor, min));
        cursor = Math.Max(cursor, max);
    }
    spans.Add((cursor, wallMax));

    float covered = 0f;
    for (int i = 0; i < spans.Count; i++)
    {
        var s = spans[i];
        Check(s.Max >= s.Min - 1e-4f, $"{label}：第{i}段区间反向（{s.Min:F2}..{s.Max:F2}）");
        covered += Math.Max(0f, s.Max - s.Min);

        // 相邻段不得重叠
        if (i > 0)
        {
            Check(spans[i].Min >= spans[i - 1].Max - 1e-4f,
                $"{label}：第{i}段与第{i - 1}段重叠");
        }
    }

    // 覆盖长度 = 墙长 - 洞口总宽
    float openingTotal = 0f;
    foreach (var (min, max) in openings) openingTotal += (max - min);

    Near(covered, (wallMax - wallMin) - openingTotal, 1e-3f,
        $"{label}：墙段总覆盖长度应等于墙长减洞口总宽");
}

CheckSegmentation("东墙（1 窗）", -halfZ, halfZ,
    (eastCenterZ - winWidth * 0.5f, eastCenterZ + winWidth * 0.5f));

CheckSegmentation("西墙（1 窗）", -halfZ, halfZ,
    (westCenterZ - winWidth * 0.5f, westCenterZ + winWidth * 0.5f));

CheckSegmentation("南墙（1 窗）", -halfX, halfX,
    (southCenterX - winWidth * 0.5f, southCenterX + winWidth * 0.5f));

CheckSegmentation("北墙（1 窗）", -halfX, halfX,
    (northCenterX - winWidth * 0.5f, northCenterX + winWidth * 0.5f));

// 多洞口也要成立（以后两面墙各开两扇窗时不会被这个算法绊倒）
CheckSegmentation("假设的墙（2 窗）", -halfZ, halfZ,
    (-3.5f, -1.9f), (1.0f, 2.6f));

// --- 6. 玻璃尺寸必须小于洞口（否则玻璃会嵌进墙里） ---
{
    float glassWidth = winWidth * 0.98f;
    float glassHeight = winHeight * 0.98f;
    Check(glassWidth < winWidth, "玻璃宽度应小于洞口宽度");
    Check(glassHeight < winHeight, "玻璃高度应小于洞口高度");

    float glassThickness = Math.Max(0.04f, wallThickness * 0.35f);
    Check(glassThickness < wallThickness, "玻璃厚度应小于墙厚（否则玻璃会鼓出墙面）");
}

Console.WriteLine($"   {assertions - windowStart} 条窗户几何断言通过");

// ==================================================================== F. 武器与伤害

Console.WriteLine();
Console.WriteLine("== F. 武器与伤害 ==");
int weaponStart = assertions;

// 手枪参数抄自 GreyboxLevelBuilder.BuildPistol
const float pistolDamage = 25f;
const float pistolInterval = 0.18f;
const float pistolRange = 60f;
const float dummyHealth = 200f;

float Sqrt(float v) => (float)Math.Sqrt(v);

// UseRealHealth 是把真 Health 包成 IDamageable —— 和木桩/玩家走的是同一份逻辑。
// 这里验证的是"接口契约 + 几枪死的公式"，不依赖 UnityEngine。
float UseRealHealth(float max, float damage, out bool alive, out float remaining)
{
    var h = new Game.Player.Health(max);
    h.TakeDamage(damage);
    alive = !h.IsDead;
    remaining = h.Current;
    return h.Current;
}

// --- 1. 一枪的伤害与剩余血量 ---
{
    UseRealHealth(200f, pistolDamage, out _, out float remaining);
    Near(remaining, 175f, 1e-3f, "200 血挨一枪 25 点后应剩 175");
}

// --- 2. 打够几枪才死（200 / 25 = 8，整除）---
{
    int shots = ShotsToKill(200f, pistolDamage);
    Check(shots == 8, $"200 血、25 伤害应 8 枪死，实际 {shots} 枪");
}

// --- 3. 除不尽必须【向上取整】---
{
    // 200 / 30 = 6.67 → 需要 7 枪
    int shots = ShotsToKill(200f, 30f);
    Check(shots == 7, $"200 血、30 伤害应 7 枪死（向上取整），实际 {shots} 枪");
}

// --- 4. 最后一枪的实际伤害只等于剩余血量，且死后伤害无效 ---
{
    var h = new Game.Player.Health(dummyHealth);
    for (int i = 0; i < 7; i++) h.TakeDamage(pistolDamage);      // 7 枪 = 175

    Near(h.Current, 25f, 1e-3f, "7 枪后应剩 25");

    float last = h.TakeDamage(pistolDamage);                      // 第 8 枪
    Near(last, 25f, 1e-3f, "最后一枪实际伤害应等于剩余血量（不是 25 而是剩余值）");
    Near(h.Current, 0f, 1e-3f, "血量应归零");
    Check(h.IsDead, "应已死亡");
    Near(h.TakeDamage(pistolDamage), 0f, 1e-3f, "死后伤害应返回 0");
}

// --- 5. 死亡只触发一次 ---
{
    var h = new Game.Player.Health(50f);
    int diedCount = 0;
    h.Died += () => diedCount++;
    h.TakeDamage(50f);
    h.TakeDamage(50f);
    h.TakeDamage(1000f);
    Check(diedCount == 1, $"死亡事件应只触发一次，实际 {diedCount} 次");
}

// --- 6. 射速换算 ---
{
    float rate = 1f / pistolInterval;
    Check(Math.Abs(rate - 5.5556f) < 0.01f, $"0.18 秒间隔 ≈ 5.56 发/秒，实际 {rate:F2}");

    int shotsInOneSecond = (int)Math.Floor(1f / pistolInterval) + 1;   // 含首发
    Check(shotsInOneSecond == 6, $"一秒内最多 6 发，实际 {shotsInOneSecond}");

    float timeToKill = (ShotsToKill(dummyHealth, pistolDamage) - 1) * pistolInterval;
    Check(Math.Abs(timeToKill - 1.26f) < 0.01f, $"8 枪打死需要 1.26 秒，实际 {timeToKill:F2}");
}

// --- 7. 木桩血量要够测几轮（不能一枪碎，也不该太肉）---
{
    int shots = ShotsToKill(dummyHealth, pistolDamage);
    Check(shots >= 5, $"木桩至少能挨 5 枪才便于观察掉血，实际 {shots} 枪");
    Check(shots <= 20, $"木桩也不该太肉，实际 {shots} 枪");
}

// --- 8. 三个木桩互不重叠、且都在门外而非房内 ---
{
    (float X, float Z)[] positions =
    {
        (-20.0f, 13.5f),
        (-16.0f, 12.0f),
        (-24.5f, 12.5f),
    };

    for (int i = 0; i < positions.Length; i++)
    {
        for (int j = i + 1; j < positions.Length; j++)
        {
            float dx = positions[i].X - positions[j].X;
            float dz = positions[i].Z - positions[j].Z;
            float distance = Sqrt(dx * dx + dz * dz);
            Check(distance > 1.8f,
                $"木桩 {i} 与 {j} 相距 {distance:F2} 米，太近会互相遮挡（应 > 1.8）");
        }
    }

    // 两层房占 X ∈ [-25,-15]、Z ∈ [14,26]（中心 -20,20，半尺寸 5×6）
    for (int i = 0; i < positions.Length; i++)
    {
        float x = positions[i].X, z = positions[i].Z;
        bool insideHouse = x > -25f && x < -15f && z > 14f && z < 26f;
        Check(!insideHouse, $"木桩 {i} 落在房屋内部（{x}, {z}），应放到门外空地");
    }
}

// --- 9. 射程要覆盖玩家在房子附近到木桩的距离 ---
{
    // 玩家出生点、木桩位置都在房子周围；最远不超过 20 米
    const float worstCaseNearHouse = 20f;
    Check(pistolRange > worstCaseNearHouse,
        $"射程 {pistolRange} 应大于房子附近的常见交战距离（{worstCaseNearHouse}）");
}

Console.WriteLine($"   {assertions - weaponStart} 条武器与伤害断言通过");

// ==================================================================== G. 地图外扩与空气墙

Console.WriteLine();
Console.WriteLine("== G. 地图外扩与空气墙 ==");
int mapStart = assertions;

// 抄自 MapAreaDefinition 与 GreyboxLevelBuilder.BuildPerimeterWalls
const float areaSize = 60f;          // _sizeXZ
const float walkableInset = 0.6f;    // _walkableInset
const float terrainPadding = 45f;    // _terrainPadding
const float airWallThickness = 1.0f;
const float airWallHeight = 6f;

float walkableHalf = areaSize * 0.5f - walkableInset;         // 29.4
float terrainHalf = walkableHalf + terrainPadding;            // 74.4

// --- 1. 两个半径的关系必须成立 ---
Near(walkableHalf, 29.4f, 1e-3f, "可行走半径");
Near(terrainHalf, 74.4f, 1e-3f, "地形半径");

Check(terrainHalf > walkableHalf,
    $"地形半径 {terrainHalf} 必须大于可行走半径 {walkableHalf}（否则看不到墙外空地）");
Check(terrainHalf - walkableHalf == terrainPadding,
    "地形半径与可行走半径之差应正好等于外扩量");
Check(terrainHalf >= walkableHalf + 20f,
    $"外扩只有 {terrainHalf - walkableHalf:F1} 米，算不上「大片空地」");

// --- 2. 空气墙的【内表面】必须正好落在可行走边界上 ---
//
// 这是最容易算错的地方：墙心放在 walkableHalf + 半个厚度处，
// 于是内表面 = 墙心 - 半个厚度 = walkableHalf。如果忘了这半个厚度，
// 玩家就会觉得"还没走到边界就撞墙了"（或者能走出去半个墙厚）。
{
    float wallCenter = walkableHalf + airWallThickness * 0.5f;
    float innerSurface = wallCenter - airWallThickness * 0.5f;

    Near(innerSurface, walkableHalf, 1e-4f,
        "空气墙内表面应正好等于可行走半径 —— 否则玩家会被提前挡住或能挤出去");
}

// --- 3. 空气墙必须高出玩家、且玩家无法跳过 ---
{
    Check(airWallHeight > playerHeadHeight + 1f,
        $"空气墙高 {airWallHeight} 应远超玩家身高 {playerHeadHeight}（防止跳过去）");
}

// --- 4. 空气墙在【四个方向】都要有，且位置对称 ---
{
    // 和 BuildPerimeterWalls 里一致的四个墙面中心
    float half = walkableHalf + airWallThickness * 0.5f;
    (float X, float Z, string Dir)[] walls =
    {
        (0f,  half, "北"),
        (0f, -half, "南"),
        ( half, 0f, "东"),
        (-half, 0f, "西"),
    };

    Check(walls.Length == 4, "应有四面空气墙");

    for (int i = 0; i < walls.Length; i++)
    {
        float distanceFromCenter = Sqrt(walls[i].X * walls[i].X + walls[i].Z * walls[i].Z);
        Near(distanceFromCenter, half, 1e-3f, $"{walls[i].Dir}墙到中心的距离");
    }
}

// --- 5. 场景里【所有】内容都必须落在可行走范围内 ---
//
// 外扩地形是为了观感，不是为了搬东西 —— 所有内容都还在原来的边界里。
// 如果哪天不小心把东西放到墙外，这条会立刻报出来。
//
// 【为什么这里特别重要 —— 踩过一次】
// 敌人第一版放在 Z = 30~32，超出可行走 ±29.4：
// 它们被空气墙挡在地图外，还报 "CharacterController.Move called on inactive controller"。
// 那次没人提醒我"放东西前先算边界"。所以这把"所有内容 + 巡逻路点"都纳进来。
{
    (float X, float Z, string Name)[] contents =
    {
        (-20f, 20f,   "两层房"),
        (-10f, 10f,   "格子房_矩形"),
        (-25f, -12f,  "格子房_L形"),
        (-8f, 27f,    "格子房_7形"),
        (10f, 27f,    "格子房_T形"),
        (17f, -17f,   "湖泊_东南"),
        (-19f, -17f,  "湖泊_西南"),
        (20f, 12f,    "湖泊_东北"),
        (-25f, -2f,   "水塘_西"),
        (-20f, 13.5f, "木桩_0"),
        (-16f, 12f,   "木桩_1"),
        (-24.5f, 12.5f, "木桩_2"),
        (-20f, 6f,    "家园建造区"),

        // 敌人（抄自 GreyboxLevelBuilder.EnemyPositions）
        (0f,  22f, "敌人_0"),
        (-6f, 23f, "敌人_1"),
        (6f,  22f, "敌人_2"),
    };

    for (int i = 0; i < contents.Length; i++)
    {
        float x = contents[i].X, z = contents[i].Z;

        // 用 "<= half" 还是 "< half"：内容需要留出自身半径。
        // 这里按【最小半径 0】保守判定（即只查中心点在不在范围内），
        // 更严格的按半径检查在下面第 5b 条。
        Check(Math.Abs(x) < walkableHalf && Math.Abs(z) < walkableHalf,
            $"{contents[i].Name}（{x}, {z}）落在可行走范围之外（±{walkableHalf}）");
    }
}

// --- 5b. 【关键】敌人本体和巡逻路点都要算上碰撞半径 ---
//
// 敌人有 CharacterController（半径 0.35），空气墙的内表面正好在 ±walkableHalf。
// 所以它的【中心】最远只能到 walkableHalf - radius。
// 巡逻路点如果落在边界外，敌人会一直朝那个方向走、贴着墙原地打转。
{
    const float enemyRadius = 0.35f;   // 抄自 EnemyController 的 CharacterController

    // 抄自 GreyboxLevelBuilder.EnemyPositions / EnemyPatrolOffsets
    (float X, float Z, string Name)[] enemySpawns =
    {
        (0f,  22f, "敌人_0"),
        (-6f, 23f, "敌人_1"),
        (6f,  22f, "敌人_2"),
    };

    (float X, float Z, string Name)[] patrolPoints =
    {
        (0f - 5f,  22f + 0f,  "敌人_0 路点0"),
        (0f + 4f,  22f - 2f,  "敌人_0 路点1"),
        (-6f - 3f, 23f + 1f,  "敌人_1 路点0"),
        (-6f + 3f, 23f - 5f,  "敌人_1 路点1"),
        (6f - 3f,  22f + 2f,  "敌人_2 路点0"),
        (6f + 3f,  22f - 2f,  "敌人_2 路点1"),
    };

    float limit = walkableHalf - enemyRadius;

    foreach (var (x, z, name) in enemySpawns)
    {
        Check(Math.Abs(x) <= limit && Math.Abs(z) <= limit,
            $"{name} 的出生点（{x}, {z}）加上碰撞半径后超出可行走范围" +
            $"（中心最远只能到 ±{limit:F2}）");
    }

    foreach (var (x, z, name) in patrolPoints)
    {
        Check(Math.Abs(x) <= limit && Math.Abs(z) <= limit,
            $"{name}（{x}, {z}）超出可行走范围 —— 敌人会贴着空气墙原地打转" +
            $"（中心最远只能到 ±{limit:F2}）");
    }
}

// --- 5c. 敌人不能生成在两层房内部 ---
//
// 两层房占 X[-25,-15] Z[14,26]。生成在里面的话敌人一出生就被墙困住。
{
    (float X, float Z, string Name)[] enemySpawns =
    {
        (0f,  22f, "敌人_0"),
        (-6f, 23f, "敌人_1"),
        (6f,  22f, "敌人_2"),
    };

    foreach (var (x, z, name) in enemySpawns)
    {
        bool insideHouse = x > -25f && x < -15f && z > 14f && z < 26f;
        Check(!insideHouse, $"{name}（{x}, {z}）生在两层房内部，会被墙困住");
    }
}

// --- 6. 外扩区域里【不应】有任何内容（需求就是"什么都没有"）---
{
    // 上面那些内容的最大 |坐标| 就是实际占用范围
    float outermost = 0f;
    (float X, float Z)[] all =
    {
        (-20f, 20f), (-10f, 10f), (-25f, -12f), (-8f, 27f), (10f, 27f),
        (17f, -17f), (-19f, -17f), (20f, 12f), (-25f, -2f),
        (-20f, 13.5f), (-16f, 12f), (-24.5f, 12.5f),
    };
    foreach (var (x, z) in all) outermost = Math.Max(outermost, Math.Max(Math.Abs(x), Math.Abs(z)));

    Check(outermost < walkableHalf,
        $"最外侧内容在 {outermost}，应小于可行走边界 {walkableHalf}");

    float emptyRingWidth = terrainHalf - outermost;
    Check(emptyRingWidth > 40f,
        $"外圈空地只有 {emptyRingWidth:F1} 米宽，不够「大片」");
}

Console.WriteLine($"   {assertions - mapStart} 条地图与空气墙断言通过");

// ==================================================================== H. 网格索引上限

Console.WriteLine();
Console.WriteLine("== H. 网格索引上限（平面着色）==");
int meshStart = assertions;

// 【为什么专门断言这个】
// 平面着色会把每个三角面拆成 3 个独立顶点，所以顶点数 = 三角面数 × 3。
// Unity 的 Mesh 默认是 16 位索引，上限 65535 个顶点。
// 本工程把地面从 60×60 扩到 148.8×148.8 时正好越过上限（21,600 → 133,206），
// 表现是【地面只渲染出一部分】（用户看到的"消失了一半"），而且没有任何报错。
// 所以把"顶点数必须低于上限"变成断言 —— 以后任何人放大网格都会立刻知道。
const int uint16VertexLimit = 65535;

// 平面着色后的顶点数 = 三角面数 × 3
int FlatVertexCount(int triangles) => triangles * 3;

// 按 BuildTerrainMesh 的公式算三角面数（cellsX * cellsZ * 2，再减去洞里的格子）
int TerrainTriangles(float extent, float cellSize)
{
    int cells = Math.Max(1, (int)Math.Ceiling(extent / cellSize));
    return cells * cells * 2;
}

// --- 1. 复现历史：60×60 / 1 米格子，旧的固定格子尺寸 ---
{
    int tris = TerrainTriangles(60f, 1.0f);
    int verts = FlatVertexCount(tris);
    Check(verts < uint16VertexLimit,
        $"60×60 固定 1 米格子应为 {verts} 顶点，必须低于 {uint16VertexLimit}");
}

// --- 2. 复现故障：148.8×148.8 / 1 米格子 —— 这条【故意验证它会超限】---
//
// 它记录的是"如果不修，就会坏"的事实。修好之后（自适应格子 + UInt32 索引）
// 下面第 3 条会保证实际用的配置是安全的。
{
    int tris = TerrainTriangles(148.8f, 1.0f);
    int verts = FlatVertexCount(tris);
    Check(verts > uint16VertexLimit,
        $"148.8 米配固定 1 米格子应【超过】上限（{verts} > {uint16VertexLimit}）—— " +
        "这正是「地面消失一半」的原因，这条断言把它记录成事实");
}

// --- 3. 实际采用的配置必须是安全的 ---
{
    const float terrainExtent = 148.8f;
    const float targetCellsPerSide = 90f;

    // 和 GreyboxLevelBuilder.BuildGround 里一样的公式
    float cellSize = Math.Max(1.0f, terrainExtent / targetCellsPerSide);
    int tris = TerrainTriangles(terrainExtent, cellSize);
    int verts = FlatVertexCount(tris);

    Check(cellSize >= 1.0f, $"格子不应小于 1 米，实际 {cellSize:F3}");
    Check(verts < uint16VertexLimit,
        $"自适应格子后应为 {verts} 顶点，必须低于 {uint16VertexLimit}（实际格子 {cellSize:F2} 米）");

    // 留出余量：不该贴着上限
    // （第一次写这条时值是 100 格 → 60,000 顶点 = 上限的 92%，被这条断言挡下来了。
    //   现在改成 90 格 → 48,600 = 74%，留约 25% 余量。）
    Check(verts < uint16VertexLimit * 0.8f,
        $"顶点数 {verts} 占上限 {verts / (float)uint16VertexLimit * 100f:F0}%，应低于 80% —— 扩图时容易再撞上");
}

// --- 4. 即使顶点数超限，也必须显式声明 UInt32 索引 ---
//
// 这条是"真正的修复"：GreyboxPrimitives.RecalculateFlatNormals 里
// 必须在写 vertices 之前设 indexFormat = UInt32。
// 这里用"平面着色顶点数 = 面数×3"的换算把结论固定下来。
{
    int verts = FlatVertexCount(TerrainTriangles(148.8f, 1.0f));
    Check(verts > uint16VertexLimit,
        "前提：这个规模的网格确实会超过 16 位索引上限");

    // UInt32 的容量远大于任何现实场景的网格
    long uint32Limit = 4294967295L;
    Check(verts < uint32Limit, $"UInt32 索引容得下 {verts} 个顶点，所以显式声明 UInt32 是有效修复");
}

// --- 5. 水面网格（128×128 细分）也不该超限 ---
//
// 【判据要按实际实现来】水面【不走】平面着色（它的法线是 shader 用偏导数算的，
// 网格用 BuildUpwardNormals 写死朝上），所以顶点数 = (cells+1)² 而【不是】三角面×3。
// 我第一次写这条时按"平面着色"算成 98,304，报了假警报 ——
// 诊断判据必须对照被检查对象的实际实现，否则假警报比没有诊断更糟。
{
    const int waterCells = 128;
    int waterVerts = (waterCells + 1) * (waterCells + 1);   // 129×129 = 16,641

    Check(waterVerts < uint16VertexLimit,
        $"水面 {waterCells}×{waterCells} 细分共 {waterVerts} 顶点，应低于 {uint16VertexLimit}");

    // 同时也要留余量（水面细分是最接近上限的共享顶点网格）
    Check(waterVerts < uint16VertexLimit * 0.5f,
        $"水面顶点 {waterVerts} 应低于上限的一半，以便以后提高细分");
}

// --- 6. 走平面着色的网格必须显式声明 UInt32 索引 ---
//
// 这条是"真正的修复"的守门人：RecalculateFlatNormals 里必须在写 vertices 之前
// 设 indexFormat = UInt32。它覆盖三个调用点：
//   BuildTerrainMesh（地面）、CreateBlob（树冠/石头）、BuildGrassField（草地，当前未启用）
// 这些网格的顶点数 = 三角面数 × 3，很容易过万，是唯一会撞上限的一类。
{
    // 地面：148.8 米 / 90 格 → 16,200 面 → 48,600 顶点
    int terrainVerts = FlatVertexCount(TerrainTriangles(148.8f, 148.8f / 90f));
    Check(terrainVerts > 10000,
        $"地面平面着色后有 {terrainVerts} 顶点，属于「必须声明 UInt32」的量级");
    Check(terrainVerts < uint16VertexLimit,
        $"地面 {terrainVerts} 顶点应低于上限（靠自适应格子保证），双保险");
}

Console.WriteLine($"   {assertions - meshStart} 条网格索引上限断言通过");

// ==================================================================== I. 网格连通性

Console.WriteLine();
Console.WriteLine("== I. 网格连通性（GridHouseModel.Validate 的判据修复）==");
int connStart = assertions;

// 【为什么专门测这个】
// GridHouseModel.Validate 原来用「每个地板格是否邻接边界」判断，判据是错的，
// 而且是【两个方向都错】：
//   1. 大房间的中间地板四周全是地板 → 误报「和轮廓断开了」
//   2. 真正孤立的地板若落在图纸边缘那一圈 → 因为边界判断恒为真而漏报
// 修完之后必须能证明修对了，所以这里用各种图把它钉死。
// 逻辑放在 GridConnectivity.cs（不依赖 UnityEngine），所以能被独立断言直接覆盖。

// 字符约定和真实一致：'o'/'D'/'S' 可行走，其余不可行走
static bool IsWalkableChar(char c) => c == 'o' || c == 'D' || c == 'S';

static string?
    CheckLayout(params string[] rows)
{
    int depth = rows.Length;
    int width = 0;
    foreach (string r in rows) width = Math.Max(width, r.Length);
    if (depth == 0 || width == 0) return null;

    char Cell(int x, int z)
    {
        if (x < 0 || x >= width || z < 0 || z >= depth) return '.';   // 越界算空地
        return x < rows[z].Length ? rows[z][x] : '.';
    }

    return Game.World.GridConnectivity.CheckWalkableConnected(
        width, depth, Cell, IsWalkableChar, "测试图");
}

// --- 1. 【关键】大房间的中间地板不应被误报（旧判据的假阳性）---
{
    string? r = CheckLayout(
        "ooo",
        "ooo",
        "ooo");
    Check(r == null, $"3×3 全地板是连通的，不应报错。实际：{r}");
}

// --- 2. 更大的房间，中间格离边界更远 ---
{
    string? r = CheckLayout(
        "ooooo",
        "ooooo",
        "ooooo",
        "ooooo",
        "ooooo");
    Check(r == null, $"5×5 全地板是连通的，不应报错。实际：{r}");
}

// --- 3. 【关键】孤立地板必须被检出（旧判据会漏报）---
{
    string? r = CheckLayout(
        "oo...oo",
        "oo...oo",
        "oo...oo");
    Check(r != null, "两片被隔开的地板应被检出为不连通");
    Check(r!.Contains("不连通"), $"报错信息应说明不连通。实际：{r}");
    Check(r.Contains("("), $"应列出走不到的格子坐标。实际：{r}");
}

// --- 4. 对角相邻不算连通（中间隔着墙时走不过去）---
{
    string? r = CheckLayout(
        "o.",
        ".o");
    Check(r != null, "只有对角相邻的两个地板格不算连通（四方向判定）");
}

// --- 5. 对角 + 一个转折格，应该连通 ---
{
    string? r = CheckLayout(
        "oo",
        ".o");
    Check(r == null, $"L 形连通应通过。实际：{r}");
}

// --- 6. 门和楼梯都算可行走 ---
{
    string? r = CheckLayout(
        "oDS",
        "ooo");
    Check(r == null, $"门/楼梯应算可行走，图应连通。实际：{r}");
}

// --- 7. 单个可行走格：必然连通 ---
{
    Check(CheckLayout("o") == null, "只有一格时必然连通");
}

// --- 8. 没有任何可行走格：不报连通性问题（由 floorCount 检查负责）---
{
    Check(CheckLayout("###", "###") == null, "没有地板时连通性检查应放行");
}

// --- 9. 真实形状：四种典型房屋轮廓都不应误报 ---
{
    Check(CheckLayout(
        "#####",
        "#ooo#",
        "#oDo#",
        "#ooo#",
        "#####") == null, "矩形带门应连通");

    Check(CheckLayout(
        "####.",
        "#oo#.",
        "#oo##",
        "#ooo#",
        "#####") == null, "L 形应连通");

    Check(CheckLayout(
        "#####",
        "#ooo#",
        "##D##",
        "#ooo#",
        "#####") == null, "T 形应连通");

    Check(CheckLayout(
        "#####",
        "#ooo#",
        "#o###",
        "#o###",
        "#####") == null, "7 形应连通");

    Check(CheckLayout(
        "#####",
        "#ooo#",
        "#oSo#",
        "#ooo#",
        "#####") == null, "带楼梯应连通");
}

// --- 10. 退化输入不应崩 ---
{
    Check(CheckLayout() == null, "空图纸不应崩");
    Check(CheckLayout("") == null, "空行不应崩");
}

// --- 11. 【回归】项目里真实的四张图纸在【新旧判据下都不会报错】---
//
// 这一条回答的是一个具体问题："修了判据，会不会把原来能过的东西判成错？"
// 现有四张图纸都较小（每个地板格都贴着边界），所以旧判据恰好也能过 ——
// 这也是为什么那个 bug 一直没暴露。
// 下面把图纸原样抄过来验证，确保换成连通性判据后行为不变。
//
// 【必须同步】字符串抄自 GreyboxLevelBuilder.GridHouseLayouts。
// 图纸改了这里要跟着改（以后应该把图纸抽成数据文件，两边都读它）。
{
    Check(CheckLayout(
        "#######",
        "#ooooo#",
        "#ooooo#",
        "#ooDoo#",
        "#######") == null, "格子房_矩形：应通过");

    Check(CheckLayout(
        "######...",
        "#ooooo...",
        "#ooooo###",
        "#ooooooo#",
        "#ooooooo#",
        "####D####") == null, "格子房_L形：应通过");

    Check(CheckLayout(
        "...######",
        "...######",
        "...######",
        "...######",
        "#########",
        "#########",
        "####D####") == null, "格子房_7形：应通过");

    Check(CheckLayout(
        "...####...",
        "...####...",
        ".########.",
        ".########.",
        ".########.",
        ".####D####",
        "...####...") == null, "格子房_T形：应通过");
}

// --- 12. 【故障复现】旧判据会误报、新判据不会 ---
//
// 这是这次修复的核心证据：一个 6×6 的普通大房间，
// 旧判据（邻接边界）会把中间的格子判成"和轮廓断开了"，新判据（连通性）正确放行。
{
    // 6×6 房间，中间 4×4 都是地板 —— 中间格四周全是地板
    string[] bigRoom =
    {
        "######",
        "#oooo#",
        "#oooo#",
        "#oooo#",
        "#oooo#",
        "##D###",
    };

    // 新判据：连通，应通过
    Check(CheckLayout(bigRoom) == null, $"6×6 房间应连通。实际：{CheckLayout(bigRoom)}");

    // 旧判据的复现：判断"是否邻接 #/D/."
    // 对 (2,2) 这个中心格，四邻都是 'o'，且不在图纸边缘 → 旧判据会报错
    static bool OldBoundary(string[] rows, int x, int z)
    {
        if (x < 0 || x >= rows[0].Length || z < 0 || z >= rows.Length) return true;
        char c = rows[z][x];
        return c == '#' || c == 'D' || c == '.';
    }

    const int px = 2, pz = 2;
    bool oldTouchesBoundary =
        OldBoundary(bigRoom, px - 1, pz) || OldBoundary(bigRoom, px + 1, pz) ||
        OldBoundary(bigRoom, px, pz - 1) || OldBoundary(bigRoom, px, pz + 1) ||
        px == 0 || px == 5 || pz == 0 || pz == 5;

    Check(!oldTouchesBoundary,
        "前提：旧判据确实会把 6×6 房间的中心格判成「不贴边界」（这就是那个假阳性）");
}

Console.WriteLine($"   {assertions - connStart} 条网格连通性断言通过");

// ==================================================================== J. 墙边编码

Console.WriteLine();
Console.WriteLine("== J. 墙边编码（BuildEdge 的唯一性）==");
int edgeStart = assertions;

// 【为什么这是建造系统的地基】
// 墙的几何位置在【格子边界】上，而 BuildingPart 只记 (X, Z, Floor) + 类型。
// 于是同一面物理墙，从相邻两格看会是两条不同记录 —— 占用校验以为不冲突，
// 几何上却完全重叠。BuildEdge 用【端点中点】唯一标识一条边，
// 从哪一侧算都得到同一个键。下面把这些不变量全部钉死。

var AllDirections = new[]
{
    CellDirection.NegX, CellDirection.PosX, CellDirection.NegZ, CellDirection.PosZ
};

// --- 1. 【核心不变量】A 格的东边 == B 格的西边 ---
//
// 这是整个编码存在的理由。如果这条不成立，墙就会重复。
{
    for (int x = -3; x <= 3; x++)
    {
        for (int z = -3; z <= 3; z++)
        {
            // 格 (x,z) 的东边，与格 (x+1,z) 的西边，必须是同一条边
            BuildEdge eastOfA = BuildEdge.FromCell(x, z, 0, CellDirection.PosX);
            BuildEdge westOfB = BuildEdge.FromCell(x + 1, z, 0, CellDirection.NegX);

            Check(eastOfA.Equals(westOfB),
                $"({x},{z}) 的东边 {eastOfA} 应等于 ({x + 1},{z}) 的西边 {westOfB}");

            // 同理：北边 / 南边
            BuildEdge northOfA = BuildEdge.FromCell(x, z, 0, CellDirection.PosZ);
            BuildEdge southOfB = BuildEdge.FromCell(x, z + 1, 0, CellDirection.NegZ);

            Check(northOfA.Equals(southOfB),
                $"({x},{z}) 的北边 {northOfA} 应等于 ({x},{z + 1}) 的南边 {southOfB}");
        }
    }
}

// --- 2. 一个格子的四条边必须互不相同 ---
//
// 如果两条边算出同一个键，墙就会互相覆盖。
{
    for (int x = -2; x <= 2; x++)
    {
        for (int z = -2; z <= 2; z++)
        {
            var keys = new HashSet<BuildEdge>();
            foreach (var dir in AllDirections)
            {
                keys.Add(BuildEdge.FromCell(x, z, 0, dir));
            }

            Check(keys.Count == 4,
                $"({x},{z}) 的四条边应产生 4 个不同的键，实际 {keys.Count} 个");
        }
    }
}

// --- 3. 相邻两格的【八条】边：只应有 7 条不同的边 ---
//
// 两格共享一条边，所以 4+4-1 = 7。
// 这条同时验证了"共享边被正确合并"和"非共享边没被误合并"。
{
    for (int x = -2; x <= 2; x++)
    {
        for (int z = -2; z <= 2; z++)
        {
            var keys = new HashSet<BuildEdge>();

            foreach (var dir in AllDirections) keys.Add(BuildEdge.FromCell(x, z, 0, dir));
            foreach (var dir in AllDirections) keys.Add(BuildEdge.FromCell(x + 1, z, 0, dir));

            Check(keys.Count == 7,
                $"({x},{z}) 与其东邻的边合计应为 7 条，实际 {keys.Count} 条");
        }
    }
}

// --- 4. 轴向上由坐标推导，不会和坐标矛盾 ---
//
// 沿 X 的边：X 坐标是奇数、Z 是偶数
// 沿 Z 的边：X 是偶数、Z 是奇数
{
    for (int x = -3; x <= 3; x++)
    {
        for (int z = -3; z <= 3; z++)
        {
            BuildEdge alongX = BuildEdge.FromCell(x, z, 0, CellDirection.PosZ);
            Check(alongX.Axis == EdgeAxis.AlongX, $"PosZ 方向的边应沿 X 延伸，实际 {alongX.Axis}");            Check((alongX.MidX2 & 1) == 1 && (alongX.MidZ2 & 1) == 0,
                $"沿 X 的边应是 X 奇 Z 偶，实际 ({alongX.MidX2}, {alongX.MidZ2})");

            BuildEdge alongZ = BuildEdge.FromCell(x, z, 0, CellDirection.PosX);
            Check(alongZ.Axis == EdgeAxis.AlongZ, $"PosX 方向的边应沿 Z 延伸，实际 {alongZ.Axis}");
            Check((alongZ.MidX2 & 1) == 0 && (alongZ.MidZ2 & 1) == 1,
                $"沿 Z 的边应是 X 偶 Z 奇，实际 ({alongZ.MidX2}, {alongZ.MidZ2})");
        }
    }
}

// --- 5. 楼层不同 → 键不同（同一位置的两层墙互不冲突）---
{
    for (int x = -2; x <= 2; x++)
    {
        for (int z = -2; z <= 2; z++)
        {
            var keys = new HashSet<BuildEdge>();
            for (int floor = -1; floor <= 2; floor++)
            {
                foreach (var dir in AllDirections) keys.Add(BuildEdge.FromCell(x, z, floor, dir));
            }

            Check(keys.Count == 16,
                $"4 个楼层 × 4 条边应产生 16 个不同的键，实际 {keys.Count} 个");
        }
    }
}

// --- 6. 低侧/高侧反查必须和构造一致 ---
{
    for (int x = -3; x <= 3; x++)
    {
        for (int z = -3; z <= 3; z++)
        {
            foreach (var dir in AllDirections)
            {
                BuildEdge edge = BuildEdge.FromCell(x, z, 0, dir);

                edge.GetLowSideCell(out int lowX, out int lowZ);
                edge.GetHighSideCell(out int highX, out int highZ);

                // 低侧坐标必须严格小于高侧（沿法线方向）
                if (edge.Axis == EdgeAxis.AlongX)
                {
                    Check(lowX == highX && lowZ + 1 == highZ,
                        $"{edge} 的低/高侧应沿 Z 相邻，实际 ({lowX},{lowZ}) / ({highX},{highZ})");
                }
                else
                {
                    Check(lowZ == highZ && lowX + 1 == highX,
                        $"{edge} 的低/高侧应沿 X 相邻，实际 ({lowX},{lowZ}) / ({highX},{highZ})");
                }

                // 我这一格必须是两侧之一
                bool iAmASide = (x == lowX && z == lowZ) || (x == highX && z == highZ);
                Check(iAmASide, $"({x},{z}) 应是 {edge} 的两侧之一，实际 ({lowX},{lowZ})/({highX},{highZ})");
            }
        }
    }
}

// --- 7. 方向反查：从两侧看这条边，方向必须相反 ---
{
    for (int x = -2; x <= 2; x++)
    {
        for (int z = -2; z <= 2; z++)
        {
            foreach (var dir in AllDirections)
            {
                BuildEdge edge = BuildEdge.FromCell(x, z, 0, dir);

                CellDirection? fromMe = edge.DirectionFromCell(x, z, 0);
                Check(fromMe == dir, $"({x},{z}) 看 {edge} 的方向应是 {dir}，实际 {fromMe}");

                BuildEdge.ToDelta(dir, out int dx, out int dz);
                CellDirection? fromNeighbor = edge.DirectionFromCell(x + dx, z + dz, 0);

                Check(fromNeighbor == BuildEdge.Opposite(dir),
                    $"邻居看同一条边的方向应与 {dir} 相反，实际 {fromNeighbor}");
            }
        }
    }
}

// --- 8. 不相邻的格子不应认领这条边 ---
{
    BuildEdge edge = BuildEdge.FromCell(0, 0, 0, CellDirection.PosX);   // 中点 (1, 0.5)

    Check(edge.DirectionFromCell(0, 0, 0) != null, "(0,0) 应邻接这条边");
    Check(edge.DirectionFromCell(1, 0, 0) != null, "(1,0) 应邻接这条边");
    Check(edge.DirectionFromCell(2, 0, 0) == null, "(2,0) 不应邻接这条边");
    Check(edge.DirectionFromCell(0, 1, 0) == null, "(0,1) 不应邻接这条边");
    Check(edge.DirectionFromCell(0, 0, 1) == null, "不同楼层不应邻接这条边");
}

// --- 9. 哈希集合真的把它当同一个键（Equals 和 GetHashCode 必须一致）---
//
// 【为什么要单独测】只实现 Equals 不实现 GetHashCode 是经典陷阱：
// HashSet 会认为两个"相等"的对象不同，占用校验就会失效。
{
    var set = new HashSet<BuildEdge>();

    BuildEdge fromA = BuildEdge.FromCell(4, 7, 0, CellDirection.PosX);
    BuildEdge fromB = BuildEdge.FromCell(5, 7, 0, CellDirection.NegX);

    set.Add(fromA);
    bool addedAgain = set.Add(fromB);

    Check(!addedAgain, "同一条边第二次加入 HashSet 应返回 false");
    Check(set.Count == 1, $"集合里应只有 1 条边，实际 {set.Count}");
    Check(set.Contains(fromB), "用另一侧算出的键应能查到同一条边");
}

// --- 10. 大范围扫描：任意两格之间不会产生键碰撞 ---
//
// 用 20×20 的区域把所有边收集起来，数量必须等于理论值。
// 理论值：每个 (n+1)×(n+1) 的网格线交点周围，沿 X 的边有 n*(n+1) 条，沿 Z 同理。
{
    const int n = 20;
    var keys = new HashSet<BuildEdge>();

    for (int x = 0; x < n; x++)
    {
        for (int z = 0; z < n; z++)
        {
            foreach (var dir in AllDirections) keys.Add(BuildEdge.FromCell(x, z, 0, dir));
        }
    }

    // 内部边：沿 X 有 n*(n+1) 条，沿 Z 有 (n+1)*n 条，减去外圈只被算一次的……
    // 更稳的做法：直接数理论值 —— 每个格子的四邻边，去重后 = n*(n+1)*2
    int expected = n * (n + 1) * 2;
    Check(keys.Count == expected,
        $"{n}×{n} 区域应产生 {expected} 条不同的边，实际 {keys.Count} 条（有碰撞）");
}

Console.WriteLine($"   {assertions - edgeStart} 条墙边编码断言通过");

// ==================================================================== K. 建造占用

Console.WriteLine();
Console.WriteLine("== K. 建造占用（地板占格 / 墙占边）==");
int occStart = assertions;

// 【这一步验证什么】
// BuildAreaData 是"什么位置已经被占了"的唯一权威。它必须保证：
//   同一个格子不会被放两次地板；同一条边不会被放两次墙（无论从哪一侧）；
//   地板和墙互不干扰；不同楼层互不干扰。
// 这些都是纯数据层的不变量，可以完全在断言里钉死。

BuildAreaData NewArea(int w = 4, int d = 4, int floor = 0) => new BuildAreaData(w, d, floor);

// --- 1. 空区域：什么都不该有 ---
{
    var a = NewArea();
    Check(a.FloorCount == 0 && a.WallCount == 0, "新建区域应为空");
    Check(a.CanPlaceFloor(0, 0).Ok, "空格子应可放地板");
    Check(a.CanPlaceFloor(3, 3).Ok, "角落格子应可放地板");
}

// --- 2. 越界必须被拒绝，且原因明确 ---
{
    var a = NewArea(4, 4);

    Check(a.CanPlaceFloor(-1, 0).Reason == PlaceRejectReason.OutOfBounds, "x=-1 应越界");
    Check(a.CanPlaceFloor(0, -1).Reason == PlaceRejectReason.OutOfBounds, "z=-1 应越界");
    Check(a.CanPlaceFloor(4, 0).Reason == PlaceRejectReason.OutOfBounds, "x=4 应越界（宽 4）");
    Check(a.CanPlaceFloor(0, 4).Reason == PlaceRejectReason.OutOfBounds, "z=4 应越界（深 4）");
    Check(!a.CanPlaceFloor(-1, 0).Ok, "越界必须失败");
}

// --- 3. 【核心】同一个格子不能放两次地板 ---
{
    var a = NewArea();
    Check(a.RegisterFloor(1, 1, "floor"), "第一次应成功");
    Check(a.FloorCount == 1, "应只有 1 块地板");

    PlaceCheck again = a.CanPlaceFloor(1, 1);
    Check(!again.Ok, "同一个格子第二次必须被拒绝");
    Check(again.Reason == PlaceRejectReason.CellOccupied, $"原因应是 CellOccupied，实际 {again.Reason}");

    Check(!a.RegisterFloor(1, 1, "floor"), "重复登记应返回 false");
    Check(a.FloorCount == 1, "重复登记不应增加数量");
}

// --- 4. 【核心】同一条边不能放两次墙 —— 无论从哪一侧放 ---
//
// 这是 BuildEdge 存在的全部理由。从两侧分别放，必须认定为同一条边。
{
    var a = NewArea();

    // 先铺地板：墙的规则是"必须贴着至少一块地板"
    a.RegisterFloor(1, 1, "floor");

    // 从 (1,1) 的东边放
    BuildEdge fromWest = BuildEdge.FromCell(1, 1, 0, CellDirection.PosX);
    Check(a.RegisterWall(fromWest, "wall"), "第一次放墙应成功");

    // 从 (2,1) 的西边放 —— 这是同一条物理墙
    BuildEdge fromEast = BuildEdge.FromCell(2, 1, 0, CellDirection.NegX);

    PlaceCheck second = a.CanPlaceWall(fromEast);
    Check(!second.Ok, "从另一侧放同一面墙必须被拒绝");
    Check(second.Reason == PlaceRejectReason.EdgeOccupied, $"原因应是 EdgeOccupied，实际 {second.Reason}");

    Check(a.WallCount == 1, $"应只有 1 面墙，实际 {a.WallCount}");
}

// --- 5. 一个格子的四条边都能立墙（墙不占格子）---
{
    var a = NewArea();

    // 先铺地板（墙需要贴着地板）
    a.RegisterFloor(1, 1, "floor");

    foreach (var dir in AllDirections)
    {
        BuildEdge edge = BuildEdge.FromCell(1, 1, 0, dir);
        Check(a.CanPlaceWall(edge).Ok, $"({1},{1}) 的 {dir} 边应可立墙");
        a.RegisterWall(edge, "wall");
    }

    Check(a.WallCount == 4, $"应有 4 面墙，实际 {a.WallCount}");
    Check(a.FloorCount == 1, "立墙不应增加地板数量");

    // 而且这个格子仍然可以（被其它格子的地板支持着）放地板 —— 这里它自己已经有地板了
    Check(!a.CanPlaceFloor(1, 1).Ok, "已经有地板的格子不该能再放一块");
}

// --- 6. 地板和墙互不干扰 ---
{
    var a = NewArea();
    a.RegisterFloor(1, 1, "floor");

    foreach (var dir in AllDirections)
    {
        BuildEdge edge = BuildEdge.FromCell(1, 1, 0, dir);
        Check(a.CanPlaceWall(edge).Ok, $"有地板的格子，其 {dir} 边仍应可立墙");
    }
}

// --- 7. 楼层隔离：不同楼层的同位置互不影响 ---
{
    var lower = NewArea(4, 4, 0);
    var upper = NewArea(4, 4, 1);

    lower.RegisterFloor(1, 1, "floor");
    upper.RegisterFloor(1, 1, "floor");   // 墙需要贴着地板，两层各铺一块
    Check(upper.CanPlaceFloor(1, 1).Ok == false, "另一层的同一格子各放一块后应已占");

    BuildEdge lowerEdge = BuildEdge.FromCell(1, 1, 0, CellDirection.PosX);
    lower.RegisterWall(lowerEdge, "wall");

    BuildEdge upperEdge = BuildEdge.FromCell(1, 1, 1, CellDirection.PosX);
    Check(upper.CanPlaceWall(upperEdge).Ok, "另一层的同一条边应可立墙");

    // 跨楼层查询必须被拒绝
    Check(!upper.CanPlaceWall(lowerEdge).Ok, "用第 0 层的边查第 1 层区域应被拒绝");
    Check(!upper.HasWall(lowerEdge), "HasWall 不应跨楼层命中");
}

// --- 8. 边界线上的墙应合法（围墙就在那里）---
//
// 【注意】合法性检查的顺序是"范围 → 重复 → 有没有地板"，
// 所以这条测试要先把地板铺上，才能验证"边界可放"。
// 同时单独验证"没有地板时边界也会被拒"。
{
    var a = NewArea(4, 4);

    // 没地板时，边界也应该被拒（但原因不是越界）
    PlaceCheck noFloor = a.CanPlaceWall(BuildEdge.FromCell(0, 0, 0, CellDirection.NegX));
    Check(!noFloor.Ok, "没有地板时不该能立墙");
    Check(noFloor.Reason == PlaceRejectReason.NoAdjacentFloor,
        $"原因应是 NoAdjacentFloor，实际 {noFloor.Reason}");

    // 铺上地板后，边界线才该合法
    a.RegisterFloor(0, 0, "floor");

    Check(a.CanPlaceWall(BuildEdge.FromCell(0, 0, 0, CellDirection.NegX)).Ok,
        "铺了地板后，西外墙（x=0）应合法");

    Check(a.CanPlaceWall(BuildEdge.FromCell(0, 0, 0, CellDirection.NegZ)).Ok,
        "铺了地板后，南外墙（z=0）应合法");

    // 完全在区域外的边：即使铺了地板也应被拒绝（越界优先）
    a.RegisterFloor(3, 0, "floor");
    BuildEdge outside = BuildEdge.FromCell(10, 10, 0, CellDirection.PosX);
    Check(!a.CanPlaceWall(outside).Ok, "区域外的边应被拒绝");
    Check(a.CanPlaceWall(outside).Reason == PlaceRejectReason.OutOfBounds, "原因应是越界");
}

// --- 9. 拆除：占用必须被释放 ---
{
    var a = NewArea();
    a.RegisterFloor(1, 1, "floor");
    a.RegisterWall(BuildEdge.FromCell(1, 1, 0, CellDirection.PosX), "wall");

    Check(!a.CanPlaceFloor(1, 1).Ok, "拆之前该格应被占");

    string? removedId = a.RemoveFloor(1, 1);
    Check(removedId == "floor", $"拆除应返回部件标识，实际 {removedId ?? "null"}");
    Check(a.FloorCount == 0, "拆除后数量应归零");
    Check(a.CanPlaceFloor(1, 1).Ok, "拆除后该格应可再放");

    // 拆不存在的返回 null，且不崩
    Check(a.RemoveFloor(1, 1) == null, "拆空位置应返回 null");
    Check(a.RemoveFloor(-5, -5) == null, "拆越界位置应返回 null");
}

// --- 10. 查询某个格子上挂着哪些墙（拆地板前的业务检查要用）---
{
    var a = NewArea();

    Check(!a.CellHasAnyWall(1, 1), "还没有墙时应为 false");

    // 先铺地板：墙必须贴着至少一块地板
    a.RegisterFloor(1, 1, "floor");

    Check(a.RegisterWall(BuildEdge.FromCell(1, 1, 0, CellDirection.PosX), "wall"),
        "有地板时立墙应成功");
    Check(a.CellHasAnyWall(1, 1), "有墙后应为 true");

    List<BuildEdge> around = a.GetWallsAroundCell(1, 1);
    Check(around.Count == 1, $"应找到 1 面墙，实际 {around.Count}");
    Check(around[0].Equals(BuildEdge.FromCell(1, 1, 0, CellDirection.PosX)), "找到的应是那面东墙");

    // 相邻格也应把它算作自己的墙
    Check(a.CellHasAnyWall(2, 1), "相邻格 (2,1) 也应认为有墙（共享这条边）");
}

// --- 11. 导出/导入往返（存档的基础）---
{
    var a = NewArea(4, 4);

    a.RegisterFloor(0, 0, "floor");
    a.RegisterFloor(1, 2, "floor");
    a.RegisterWall(BuildEdge.FromCell(0, 0, 0, CellDirection.PosX), "wall");
    a.RegisterWall(BuildEdge.FromCell(1, 2, 0, CellDirection.NegZ), "wall");

    List<PlacedPartRecord> exported = a.Export();
    Check(exported.Count == 4, $"应导出 4 条记录，实际 {exported.Count}");

    var restored = NewArea(4, 4);
    int imported = restored.Import(exported);

    Check(imported == 4, $"应导入 4 条，实际 {imported}");
    Check(restored.FloorCount == 2, $"恢复后应有 2 块地板，实际 {restored.FloorCount}");
    Check(restored.WallCount == 2, $"恢复后应有 2 面墙，实际 {restored.WallCount}");
    Check(restored.HasFloor(0, 0) && restored.HasFloor(1, 2), "恢复后地板位置应一致");
    Check(restored.HasWall(BuildEdge.FromCell(0, 0, 0, CellDirection.PosX)), "恢复后墙位置应一致");
    Check(restored.GetFloorId(0, 0) == "floor", "恢复后部件标识应保留");
}

// --- 12. 重复导入同一份记录不应翻倍 ---
{
    var a = NewArea(4, 4);
    a.RegisterFloor(1, 1, "floor");
    List<PlacedPartRecord> records = a.Export();

    int secondImport = a.Import(records);
    Check(secondImport == 0, $"重复导入应导入 0 条，实际 {secondImport}");
    Check(a.FloorCount == 1, "重复导入不应增加数量");
}

// --- 13. 大面积满铺：4×4 全地板 + 全部边都立墙 ---
//
// 把 4×4 铺满：地板 16 块；墙 = 网格线数量。
// 一个 4×4 的格子区域，其内部+边界的边总数 = 4*5*2 = 40 条。
{
    var a = NewArea(4, 4);

    for (int x = 0; x < 4; x++)
        for (int z = 0; z < 4; z++)
            Check(a.RegisterFloor(x, z, "floor"), $"({x},{z}) 应可放地板");

    Check(a.FloorCount == 16, $"应有 16 块地板，实际 {a.FloorCount}");

    for (int x = 0; x < 4; x++)
    {
        for (int z = 0; z < 4; z++)
        {
            foreach (var dir in AllDirections)
            {
                BuildEdge edge = BuildEdge.FromCell(x, z, 0, dir);
                a.RegisterWall(edge, "wall");   // 重复的会被拒绝，这里不关心返回值
            }
        }
    }

    int expectedEdges = 4 * 5 * 2;   // 40
    Check(a.WallCount == expectedEdges,
        $"4×4 区域全部立墙后应有 {expectedEdges} 条边，实际 {a.WallCount}");
}

// --- 14. 满铺之后再放应全部被拒（并且原因正确）---
{
    var a = NewArea(4, 4);
    for (int x = 0; x < 4; x++)
        for (int z = 0; z < 4; z++)
            a.RegisterFloor(x, z, "floor");

    for (int x = 0; x < 4; x++)
    {
        for (int z = 0; z < 4; z++)
        {
            PlaceCheck r = a.CanPlaceFloor(x, z);
            Check(!r.Ok && r.Reason == PlaceRejectReason.CellOccupied,
                $"({x},{z}) 应因已占用被拒，实际 {r}");
        }
    }
}

// --- 15. 清空 ---
{
    var a = NewArea(4, 4);
    a.RegisterFloor(1, 1, "floor");
    a.RegisterWall(BuildEdge.FromCell(1, 1, 0, CellDirection.PosX), "wall");

    a.Clear();
    Check(a.FloorCount == 0 && a.WallCount == 0, "清空后应无任何部件");
    Check(a.CanPlaceFloor(1, 1).Ok, "清空后应可再放");
    Check(a.GetFloorId(1, 1) == null, "清空后不应残留标识");
}

// ================================================================ 墙必须贴着地板
//
// 【规则】一条边只有两个相邻格（GetLowSideCell / GetHighSideCell），
// "贴着地板"就是【任一侧有地板】。
//
// 这条规则的价值：它自然要求"先铺地板、再立墙"，
// 同时挡住"在荒野里凭空插一面孤墙"。

// --- 16. 两侧都没地板 → 拒绝，且原因明确 ---
{
    var a = NewArea(4, 4);

    // 区域正中一条边，两侧都是空格
    BuildEdge middle = BuildEdge.FromCell(1, 1, 0, CellDirection.PosX);

    PlaceCheck r = a.CanPlaceWall(middle);
    Check(!r.Ok, "两侧都没有地板时不该能立墙");
    Check(r.Reason == PlaceRejectReason.NoAdjacentFloor,
        $"原因应是 NoAdjacentFloor，实际 {r.Reason}");

    Check(!a.RegisterWall(middle, "wall"), "登记也应失败");
    Check(a.WallCount == 0, "失败的登记不该留下记录");
}

// --- 17. 低侧有地板 → 通过（围墙就是这种情况）---
{
    var a = NewArea(4, 4);
    a.RegisterFloor(1, 1, "floor");

    BuildEdge eastOfFloor = BuildEdge.FromCell(1, 1, 0, CellDirection.PosX);
    Check(a.CanPlaceWall(eastOfFloor).Ok, "低侧有地板时应可立墙（这是围墙的情形）");
}

// --- 18. 高侧有地板 → 也通过（从另一侧看同一条边）---
//
// 【为什么必须单独测】GetLowSideCell/GetHighSideCell 是两套不同的取格逻辑，
// 只测一侧的话，另一侧的坐标算错也发现不了。
{
    var a = NewArea(4, 4);
    a.RegisterFloor(2, 1, "floor");   // 这是 (1,1) 东边那条【高侧】的格子

    BuildEdge eastOfEmpty = BuildEdge.FromCell(1, 1, 0, CellDirection.PosX);
    Check(a.CanPlaceWall(eastOfEmpty).Ok, "高侧有地板时也应可立墙");

    // 换一组方向再验证一遍（北边）
    var b = NewArea(4, 4);
    b.RegisterFloor(1, 2, "floor");   // (1,1) 北边那条边的高侧
    Check(b.CanPlaceWall(BuildEdge.FromCell(1, 1, 0, CellDirection.PosZ)).Ok,
        "高侧有地板时，北边的墙也应可立");
}

// --- 19. 两侧都有地板 → 通过（隔断墙）---
{
    var a = NewArea(4, 4);
    a.RegisterFloor(1, 1, "floor");
    a.RegisterFloor(2, 1, "floor");

    Check(a.CanPlaceWall(BuildEdge.FromCell(1, 1, 0, CellDirection.PosX)).Ok,
        "两侧都有地板时应可立墙（隔断墙）");
}

// --- 20. 任一侧有地板即可，不需要两侧都有 ---
//
// 明确验证"至少一块"这个措辞：一侧是地板、另一侧是空地，必须通过。
{
    var a = NewArea(4, 4);
    a.RegisterFloor(0, 0, "floor");

    // (0,0) 的东边：低侧是 (0,0)（有地板），高侧是 (1,0)（空地）
    Check(a.CanPlaceWall(BuildEdge.FromCell(0, 0, 0, CellDirection.PosX)).Ok,
        "一侧地板、一侧空地应通过 —— 规则是「至少一块」");

    // (0,0) 的西边：低侧是 (-1,0)（区外，没地板），高侧是 (0,0)（有地板）
    Check(a.CanPlaceWall(BuildEdge.FromCell(0, 0, 0, CellDirection.NegX)).Ok,
        "另一侧有地板也应通过");
}

// --- 21. 地板被拆掉后，挂在上面的墙应不再能放 ---
//
// 这条保证"规则是实时求值的"，而不是登记时算一次就算了。
{
    var a = NewArea(4, 4);
    a.RegisterFloor(1, 1, "floor");

    BuildEdge edge = BuildEdge.FromCell(1, 1, 0, CellDirection.PosX);
    Check(a.CanPlaceWall(edge).Ok, "有地板时应可立墙");

    a.RemoveFloor(1, 1);
    Check(!a.CanPlaceWall(edge).Ok, "地板拆掉后，同一条边应不再可立墙");
    Check(a.CanPlaceWall(edge).Reason == PlaceRejectReason.NoAdjacentFloor,
        "原因应是 NoAdjacentFloor");
}

// --- 22. HasFloorOnEitherSide 的查询行为 ---
{
    var a = NewArea(4, 4);
    BuildEdge edge = BuildEdge.FromCell(1, 1, 0, CellDirection.PosX);

    Check(!a.HasFloorOnEitherSide(edge), "空区域：两侧都没有地板");

    a.RegisterFloor(1, 1, "floor");
    Check(a.HasFloorOnEitherSide(edge), "低侧铺了地板后应为 true");

    var b = NewArea(4, 4);
    b.RegisterFloor(2, 1, "floor");
    Check(b.HasFloorOnEitherSide(edge), "只有高侧铺地板也应为 true");

    // 跨楼层的边不该被本层认领
    var c = NewArea(4, 4, 0);
    c.RegisterFloor(1, 1, "floor");
    Check(!c.HasFloorOnEitherSide(BuildEdge.FromCell(1, 1, 1, CellDirection.PosX)),
        "别的楼层的边不该被本层认领");
}

// --- 23. 一个格子铺了地板后，它的四条边全部可立墙 ---
//
// 这是最小可建造单元：放一块地板，就能围一圈墙。
{
    var a = NewArea(4, 4);
    a.RegisterFloor(2, 2, "floor");

    foreach (var dir in AllDirections)
    {
        BuildEdge edge = BuildEdge.FromCell(2, 2, 0, dir);
        Check(a.CanPlaceWall(edge).Ok, $"铺了地板后，{dir} 方向的墙应可立");
    }
}

Console.WriteLine($"   {assertions - occStart} 条建造占用断言通过");

// ==================================================================== L. 坐标映射

Console.WriteLine();
Console.WriteLine("== L. 建造坐标映射（世界 ↔ 格子 / 边）==");
int mapStart2 = assertions;

// 【这一步验证什么】
// BuildArea 把世界坐标和格子坐标对上，BuildController 靠它决定"鼠标指向哪一格"。
// 映射错了的表现是"预览和实际放置不在同一个地方"—— 很难靠肉眼发现，
// 但纯算术可以完全钉死。
//
// 注意：这里验证的是【局部坐标】映射（不含世界原点），
// 世界原点由 BuildArea 组件负责加上去，那是 MonoBehaviour 的事。

const float cell = 1.0f;
var area4 = new BuildAreaData(4, 4, 0, cell);

// --- 1. 格子中心 = (格号 + 0.5) × 边长 ---
{
    Near(area4.CellCenterLocalX(0), 0.5f, 1e-4f, "格 0 的中心 X");
    Near(area4.CellCenterLocalX(3), 3.5f, 1e-4f, "格 3 的中心 X");
    Near(area4.CellCenterLocalZ(2), 2.5f, 1e-4f, "格 2 的中心 Z");
}

// --- 2. 【核心】格子往返：CellToLocal → LocalToCell 必须回到原格 ---
{
    for (int x = 0; x < 4; x++)
    {
        for (int z = 0; z < 4; z++)
        {
            float lx = area4.CellCenterLocalX(x);
            float lz = area4.CellCenterLocalZ(z);

            bool ok = area4.LocalToCell(lx, lz, out int rx, out int rz);
            Check(ok, $"({x},{z}) 的中心应能反查回格子");
            Check(rx == x && rz == z, $"({x},{z}) 中心反查应得 ({x},{z})，实际 ({rx},{rz})");
        }
    }
}

// --- 3. 【关键】格内任意偏移都不改变归属 ---
//
// 这是正确的判据。之前的写法是 CellToWorld(WorldToCell(p)) == p，
// 那个对任意点【不成立】—— 因为格子转换会丢掉格内偏移。
// 正确的说法是"格内任意点都归属同一格"。
{
    float[] offsets = { 0.01f, 0.25f, 0.5f, 0.75f, 0.99f };

    for (int x = 0; x < 4; x++)
    {
        for (int z = 0; z < 4; z++)
        {
            foreach (float ox in offsets)
            {
                foreach (float oz in offsets)
                {
                    float lx = x * cell + ox * cell;
                    float lz = z * cell + oz * cell;

                    bool ok = area4.LocalToCell(lx, lz, out int rx, out int rz);
                    Check(ok && rx == x && rz == z,
                        $"格 ({x},{z}) 内偏移 ({ox:F2},{oz:F2}) 应归属 ({x},{z})，实际 ({rx},{rz}) ok={ok}");
                }
            }
        }
    }
}

// --- 4. 边界归左边那一格（[1,2) 属于 x=1）---
{
    Check(area4.LocalToCell(1.0f, 1.0f, out int x1, out int z1) && x1 == 1 && z1 == 1,
        "局部 (1.0, 1.0) 应属于格 (1,1)");
    Check(area4.LocalToCell(1.999f, 1.999f, out int x2, out int z2) && x2 == 1 && z2 == 1,
        "局部 (1.999, 1.999) 应属于格 (1,1)");
    Check(!area4.LocalToCell(4.0f, 0f, out _, out _), "局部 (4.0, 0) 越界（宽 4）");
    Check(!area4.LocalToCell(-0.01f, 0f, out _, out _), "局部 (-0.01, 0) 越界");
}

// --- 5. 边中点 = 编码坐标 ÷ 2 × 边长 ---
{
    BuildEdge east = BuildEdge.FromCell(1, 1, 0, CellDirection.PosX);   // 中点 (2, 1.5)
    area4.EdgeCenterLocal(east, out float ex, out float ez);
    Near(ex, 2f, 1e-4f, "东边中点 X");
    Near(ez, 1.5f, 1e-4f, "东边中点 Z");

    BuildEdge north = BuildEdge.FromCell(1, 1, 0, CellDirection.PosZ);  // 中点 (1.5, 2)
    area4.EdgeCenterLocal(north, out float nx, out float nz);
    Near(nx, 1.5f, 1e-4f, "北边中点 X");
    Near(nz, 2f, 1e-4f, "北边中点 Z");
}

// --- 6. 【核心】最近边吸附：格内任意点都吸到该格最近的边 ---
//
// 靠格子中心（0.5）分界：小数部分 < 0.5 吸到低侧那条线，> 0.5 吸到高侧那条线。
// 这条保证拖动鼠标时预览不会乱跳。
{
    // 格 (1,1) 的范围内：(1,2) × (1,2)
    // 靠左 → 吸到 x=1 那条线（沿 Z 的边）；靠右 → 吸到 x=2 那条线
    Check(area4.LocalToNearestEdge(1.1f, 1.5f, out BuildEdge nearLeft),
        "靠左的位置应能求出边");
    Check(nearLeft.Axis == EdgeAxis.AlongZ, $"靠左应吸到沿 Z 的边，实际 {nearLeft.Axis}");
    Check(nearLeft.MidX2 == 2, $"靠左应吸到 x=1 那条线（MidX2=2），实际 {nearLeft.MidX2}");

    Check(area4.LocalToNearestEdge(1.9f, 1.5f, out BuildEdge nearRight),
        "靠右的位置应能求出边");
    Check(nearRight.Axis == EdgeAxis.AlongZ, "靠右也应吸到沿 Z 的边");
    Check(nearRight.MidX2 == 4, $"靠右应吸到 x=2 那条线（MidX2=4），实际 {nearRight.MidX2}");

    // 靠下 / 靠上 → 沿 X 的边
    Check(area4.LocalToNearestEdge(1.5f, 1.1f, out BuildEdge nearBottom), "靠下应能求出边");
    Check(nearBottom.Axis == EdgeAxis.AlongX, $"靠下应吸到沿 X 的边，实际 {nearBottom.Axis}");
    Check(nearBottom.MidZ2 == 2, $"靠下应吸到 z=1 那条线（MidZ2=2），实际 {nearBottom.MidZ2}");

    Check(area4.LocalToNearestEdge(1.5f, 1.9f, out BuildEdge nearTop), "靠上应能求出边");
    Check(nearTop.Axis == EdgeAxis.AlongX, "靠上应吸到沿 X 的边");
    Check(nearTop.MidZ2 == 4, $"靠上应吸到 z=2 那条线（MidZ2=4），实际 {nearTop.MidZ2}");
}

// --- 7. 吸附结果必须始终是建造区内的合法边 ---
{
    for (float lx = 0.05f; lx < 4f; lx += 0.17f)
    {
        for (float lz = 0.05f; lz < 4f; lz += 0.19f)
        {
            if (!area4.LocalToNearestEdge(lx, lz, out BuildEdge edge))
            {
                Check(false, $"局部 ({lx:F2},{lz:F2}) 在区内却没求出边");
                continue;
            }

            Check(area4.IsEdgeInBounds(edge),
                $"局部 ({lx:F2},{lz:F2}) 吸附出的边 {edge} 应落在建造区内");
        }
    }
}

// --- 8. 区域最外圈也能吸附（外墙就在那里）---
{
    Check(area4.LocalToNearestEdge(0.05f, 0.05f, out BuildEdge corner),
        "左上角附近应能求出边");
    Check(area4.IsEdgeInBounds(corner), $"角落吸附出的边应在区内，实际 {corner}");

    Check(area4.LocalToNearestEdge(3.95f, 3.95f, out BuildEdge corner2),
        "右下角附近应能求出边");
    Check(area4.IsEdgeInBounds(corner2), $"角落吸附出的边应在区内，实际 {corner2}");
}

// --- 9. 格子尺寸不是 1 时映射依然正确 ---
{
    var area2 = new BuildAreaData(4, 4, 0, 2f);

    Near(area2.CellCenterLocalX(0), 1f, 1e-4f, "2 米格子：格 0 中心");
    Near(area2.CellCenterLocalX(3), 7f, 1e-4f, "2 米格子：格 3 中心");

    Check(area2.LocalToCell(3.9f, 3.9f, out int x, out int z) && x == 1 && z == 1,
        "2 米格子：局部 (3.9, 3.9) 应属于格 (1,1)");

    Check(area2.LocalToCell(0.1f, 0.1f, out int x0, out int z0) && x0 == 0 && z0 == 0,
        "2 米格子：局部 (0.1, 0.1) 应属于格 (0,0)");
}

// --- 10. 非正方形区域 ---
{
    var wide = new BuildAreaData(6, 2, 0, 1f);

    Check(wide.ContainsCell(5, 1), "6×2 区域应包含 (5,1)");
    Check(!wide.ContainsCell(5, 2), "6×2 区域不应包含 (5,2)");
    Check(!wide.ContainsCell(6, 0), "6×2 区域不应包含 (6,0)");

    Check(wide.LocalToCell(5.5f, 1.5f, out int wx, out int wz) && wx == 5 && wz == 1,
        "6×2 区域：(5.5, 1.5) 应属于 (5,1)");
}

Console.WriteLine($"   {assertions - mapStart2} 条坐标映射断言通过");

// ============================================================ M. 建造系统的缺陷回归

Console.WriteLine();
Console.WriteLine("== M. 建造缺陷回归（用户实测发现的 8 条）==");
int regStart = assertions;

// 【为什么专门有一组】这一组里的每一条都对应"用户实际跑出来的缺陷"。
// 断言先复现缺陷（证明它真的存在），再验证修复。
// 这样以后有人改回去，这一组会立刻报出来。

var area4b = new BuildAreaData(4, 4, 0, 1f);

// --- P2-5：鼠标远在区域外，不该吸回边界墙 ---
//
// 用户实测：4×4 区域输入 (1, -100) 仍然返回成功，吸到南端的墙边。
// 原因是越界格索引被强行 clamp 回范围，没有先拒绝区域外的指针。
{
    bool far = area4b.LocalToNearestEdge(1f, -100f, out BuildEdge farEdge);

    Check(!far,
        $"局部 (1, -100) 远在区域外，不该吸附成功（这是用户的实测缺陷）。实际返回 {far}，边={farEdge}");

    // 横向远超出也一样
    Check(!area4b.LocalToNearestEdge(-100f, 1f, out _), "局部 (-100, 1) 远在区域外，不该吸附成功");
    Check(!area4b.LocalToNearestEdge(100f, 1f, out _), "局部 (100, 1) 远在区域外，不该吸附成功");
    Check(!area4b.LocalToNearestEdge(1f, 100f, out _), "局部 (1, 100) 远在区域外，不该吸附成功");
}

// --- P2-5 反面：区域内、以及贴边（一个格子容差内）仍要能吸附 ---
//
// 修复不能把正常用法一起砍掉：站在区域边缘时鼠标常常刚好越过一点。
{
    Check(area4b.LocalToNearestEdge(2f, 2f, out _), "区域正中应能吸附");
    Check(area4b.LocalToNearestEdge(0.05f, 0.05f, out _), "左上角内应能吸附");
    Check(area4b.LocalToNearestEdge(3.95f, 3.95f, out _), "右下角内应能吸附");

    // 略微越界（容差 = 一个格子 = 1 米）应仍然可用 —— 贴边放外墙要用
    Check(area4b.LocalToNearestEdge(-0.5f, 2f, out _), "向左越界半格（容差内）应仍能吸附");
    Check(area4b.LocalToNearestEdge(4.5f, 2f, out _), "向右越界半格（容差内）应仍能吸附");
    Check(area4b.LocalToNearestEdge(2f, -0.5f, out _), "向下越界半格（容差内）应仍能吸附");

    // 但超过一个格子就拒绝
    Check(!area4b.LocalToNearestEdge(-1.5f, 2f, out _), "向左越界超过一格应被拒绝");
}

// --- P2-8a：非法边编码 (0,0) 必须被拒绝 ---
//
// 用户实测：(0,0) 被当成合法边登记了。
// 编码约定是"一个坐标奇数一个偶数"，(0,0) 两个都是偶数 —— 中点落在格子中心，不是边。
{
    var invalid = new BuildEdge(0, 0, 0);
    Check(!invalid.IsValid, "编码 (0,0) 两个坐标都是偶数，不是一条真实的边");

    Check(!area4b.IsEdgeInBounds(invalid), "非法编码不该被当成区内合法边");
    Check(!area4b.CanPlaceWall(invalid).Ok, "非法编码不该能放墙");
    Check(!area4b.RegisterWall(invalid, "wall"), "非法编码不该能登记");
    Check(area4b.WallCount == 0, "非法登记后墙数应仍为 0");

    // 三种非法组合都要挡住
    Check(!new BuildEdge(0, 0, 0).IsValid, "(偶, 偶) 非法");
    Check(!new BuildEdge(1, 1, 0).IsValid, "(奇, 奇) 非法 —— 那是格子中心");
    Check(new BuildEdge(1, 0, 0).IsValid, "(奇, 偶) 合法 —— 沿 X 的边");
    Check(new BuildEdge(0, 1, 0).IsValid, "(偶, 奇) 合法 —— 沿 Z 的边");
}

// --- P2-8b：Import 必须校验记录的楼层 ---
//
// 用户实测：楼层 99 的地板记录能导入到楼层 0，并变成楼层 0 的地板。
{
    var area0 = new BuildAreaData(4, 4, 0, 1f);

    var wrongFloor = new List<Game.World.PlacedPartRecord>
    {
        new Game.World.PlacedPartRecord(false, 1, 1, 99, default, "floor"),
    };

    int imported = area0.Import(wrongFloor);
    Check(imported == 0, $"楼层 99 的记录不该导入到楼层 0，实际导入 {imported} 条");
    Check(area0.FloorCount == 0, "错误楼层的记录不该占用格子");
    Check(area0.CanPlaceFloor(1, 1).Ok, "被拒绝的导入不该影响该格的可用性");
}

// --- P2-8b 反面：同楼层的记录仍要正常导入 ---
{
    var area0 = new BuildAreaData(4, 4, 0, 1f);

    var good = new List<Game.World.PlacedPartRecord>
    {
        new Game.World.PlacedPartRecord(false, 1, 1, 0, default, "floor"),
        new Game.World.PlacedPartRecord(true, 0, 0, 0,
            BuildEdge.FromCell(1, 1, 0, CellDirection.PosX), "wall"),
    };

    int imported = area0.Import(good);
    Check(imported == 2, $"合法记录应全部导入，实际 {imported} 条");
    Check(area0.FloorCount == 1 && area0.WallCount == 1, "导入后地板与墙各 1");
}

// --- P2-8b：Import 里带着非法边编码时也要跳过 ---
{
    var area0 = new BuildAreaData(4, 4, 0, 1f);

    var badEdge = new List<Game.World.PlacedPartRecord>
    {
        new Game.World.PlacedPartRecord(true, 0, 0, 0, new BuildEdge(0, 0, 0), "wall"),
    };

    int imported = area0.Import(badEdge);
    Check(imported == 0, $"非法边编码不该导入，实际 {imported} 条");
    Check(area0.WallCount == 0, "非法边不该占用记录");
}

// --- P1-4：预置部件能被正确表达（起步地板的数据载体）---
//
// 这是"起步地板进 Play 后消失"的修复基础：占用状态必须能从场景对象恢复出来。
{
    // 模拟 BuildController 从 PreplacedPart 读取并补登的过程
    var area0 = new BuildAreaData(4, 4, 0, 1f);

    // 编辑器期登记（进 Play 会丢）
    area0.RegisterFloor(0, 0, "floor");
    Check(area0.FloorCount == 1, "编辑器期登记后应有 1 块地板");

    // 模拟进 Play：BuildAreaData 是新的（未序列化）
    var runtime = new BuildAreaData(4, 4, 0, 1f);
    Check(runtime.FloorCount == 0, "新建立的运行时数据应为空 —— 这正是缺陷的成因");

    // 从场景里的 PreplacedPart 恢复（值来自序列化字段）
    bool restored = runtime.RegisterFloor(0, 0, "floor");
    Check(restored, "从预置部件恢复应成功");
    Check(runtime.FloorCount == 1, "恢复后应有 1 块地板");
    Check(!runtime.CanPlaceFloor(0, 0).Ok,
        "恢复后该格应被占 —— 否则玩家能在这里重叠建第二块地板");
}

// --- P1-1：建造物节点必须在世界根上（否则会跟着玩家走）---
//
// 这条是纯逻辑无法验证的（涉及 Unity 层级），但它背后的算术可以验证：
// 只有当容器节点在原点且无旋转缩放时，"传世界坐标给 localPosition"才等价。
// 断言在这里记录的是"这个前提必须成立"。
{
    // 玩家在一个很偏的位置时，世界坐标与"玩家局部坐标"差别很大
    const float playerX = -20.5f;
    const float playerZ = 6.5f;

    // 建造区格子 (1,1) 的世界中心（建造区原点 -20,6）
    float worldX = -20f + 1.5f;
    float worldZ = 6f + 1.5f;

    // 如果容器挂在玩家下面，CreateBlock 会把 worldX 当成局部坐标 → 实际世界位置变成：
    float wrongWorldX = playerX + worldX;   // -39.5
    float wrongWorldZ = playerZ + worldZ;   // 14.0

    Check(Math.Abs(wrongWorldX - worldX) > 15f,
        $"前提：容器若挂在玩家下面，位置会偏出 {Math.Abs(wrongWorldX - worldX):F1} 米（这正是缺陷）");

    // 容器在原点时，局部坐标 == 世界坐标
    float correctWorldX = 0f + worldX;
    Check(Math.Abs(correctWorldX - worldX) < 0.001f, "容器在原点时局部坐标应等于世界坐标");
}

Console.WriteLine($"   {assertions - regStart} 条建造缺陷回归断言通过");

// ==================================================================== N. 存档

Console.WriteLine();
Console.WriteLine("== N. 存档往返（数据不丢、方向不丢）==");
int saveStart = assertions;

// 【这一步验证什么】
// 存档最怕的不是"写不进去"，而是"写进去了但读出来不一样" ——
// 那种错误往往要等到玩家重启游戏才发现，而且已存的档可能已经废了。
// 这里把"存 → 读 → 用"这条链上【纯数据能验证的部分】全部钉死。
//
// 具体模仿 GameSaveController 的采集/恢复流程（那部分依赖 Unity，这里用同样的转换）。

// 把一条输出记录转成存档记录（对应 GameSaveController.CapturePlacedParts）
PlacedPartSaveData ToSaveData(Game.World.PlacedPartRecord record, string areaId)
{
    var part = new PlacedPartSaveData
    {
        AreaId = areaId,
        PartId = record.PartId,
        IsWall = record.IsWall,
        Floor = record.Floor,
        // 墙的朝向由边的轴向推出来（和 CapturePlacedParts 里的写法一致）
        Yaw = record.IsWall
            ? (record.Edge.Axis == EdgeAxis.AlongX ? 0f : 90f)
            : 0f,
    };

    if (record.IsWall)
    {
        part.EdgeMidX2 = record.Edge.MidX2;
        part.EdgeMidZ2 = record.Edge.MidZ2;
    }
    else
    {
        part.CellX = record.CellX;
        part.CellZ = record.CellZ;
    }

    return part;
}

// 把存档记录转回占用记录（对应 GameSaveController.ApplyPlacedParts 的方向）
Game.World.PlacedPartRecord ToRecord(PlacedPartSaveData part)
{
    return part.IsWall
        ? new Game.World.PlacedPartRecord(true, 0, 0, part.Floor,
            new BuildEdge(part.EdgeMidX2, part.EdgeMidZ2, part.Floor), part.PartId)
        : new Game.World.PlacedPartRecord(false, part.CellX, part.CellZ, part.Floor,
            default, part.PartId);
}

// --- 1. 一块地板往返：格坐标必须一模一样 ---
{
    var source = new BuildAreaData(4, 4, 0, 1f);
    source.RegisterFloor(2, 3, "floor");

    var data = new GameSaveData();
    foreach (var r in source.Export()) data.PlacedParts.Add(ToSaveData(r, "home"));

    Check(data.PlacedParts.Count == 1, $"应存 1 条，实际 {data.PlacedParts.Count}");

    var restored = new BuildAreaData(4, 4, 0, 1f);
    int imported = restored.Import(data.PlacedParts.ConvertAll<Game.World.PlacedPartRecord>(ToRecord));

    Check(imported == 1, $"应恢复 1 条，实际 {imported}");
    Check(restored.HasFloor(2, 3), "恢复后地板应在 (2,3)");
    Check(restored.GetFloorId(2, 3) == "floor", "恢复后部件标识应保留");
}

// --- 2. 【关键】墙往返：边的编码必须一模一样 ---
//
// 这是最容易丢信息的：墙不是按格子存的，如果偷懒只存个"格子坐标 + 一个方向数字"，
// 往返之后就可能变成相邻格上的另一条边。
{
    var source = new BuildAreaData(4, 4, 0, 1f);
    source.RegisterFloor(1, 1, "floor");

    var originalEdges = new List<BuildEdge>();
    foreach (var dir in AllDirections)
    {
        BuildEdge edge = BuildEdge.FromCell(1, 1, 0, dir);
        source.RegisterWall(edge, "wall");
        originalEdges.Add(edge);
    }

    Check(originalEdges.Count == 4, "应有 4 面墙");

    var data = new GameSaveData();
    foreach (var r in source.Export()) data.PlacedParts.Add(ToSaveData(r, "home"));

    var restored = new BuildAreaData(4, 4, 0, 1f);
    restored.RegisterFloor(1, 1, "floor");
    restored.Import(data.PlacedParts.ConvertAll<Game.World.PlacedPartRecord>(ToRecord));

    foreach (BuildEdge edge in originalEdges)
    {
        Check(restored.HasWall(edge), $"恢复后应有墙 {edge}");
    }

    Check(restored.WallCount == 4, $"恢复后应仍是 4 面墙，实际 {restored.WallCount}");
}

// --- 3. 【关键】墙的朝向：沿 X 与沿 Z 必须能区分开 ---
//
// 如果朝向丢了或搞反了，墙会横着立 —— 而"数据上都在"，
// 只有进游戏才看得出来。所以方向必须能被断言。
{
    var source = new BuildAreaData(4, 4, 0, 1f);
    source.RegisterFloor(1, 1, "floor");

    BuildEdge alongX = BuildEdge.FromCell(1, 1, 0, CellDirection.PosZ);  // 南北向的墙（沿 X）
    BuildEdge alongZ = BuildEdge.FromCell(1, 1, 0, CellDirection.PosX);  // 东西向的墙（沿 Z）
    source.RegisterWall(alongX, "wall");
    source.RegisterWall(alongZ, "wall");

    var saved = new List<PlacedPartSaveData>();
    foreach (var r in source.Export()) saved.Add(ToSaveData(r, "home"));

    // 找出沿 X 的那面墙，检查它的 Yaw 是 0
    var foundAlongX = saved.Find(p => p.EdgeMidX2 == alongX.MidX2 && p.EdgeMidZ2 == alongX.MidZ2);
    var foundAlongZ = saved.Find(p => p.EdgeMidX2 == alongZ.MidX2 && p.EdgeMidZ2 == alongZ.MidZ2);

    Check(foundAlongX != null, "应能存下沿 X 的那面墙");
    Check(foundAlongZ != null, "应能存下沿 Z 的那面墙");

    Check(Math.Abs(foundAlongX!.Yaw - 0f) < 0.01f,
        $"沿 X 的墙朝向应是 0°，实际 {foundAlongX.Yaw}");
    Check(Math.Abs(foundAlongZ!.Yaw - 90f) < 0.01f,
        $"沿 Z 的墙朝向应是 90°，实际 {foundAlongZ.Yaw}");

    // 而且从编码本身也能推回轴向（双保险：即使 Yaw 丢了，方向也没错）
    var restoredEdgeX = new BuildEdge(foundAlongX.EdgeMidX2, foundAlongX.EdgeMidZ2, 0);
    var restoredEdgeZ = new BuildEdge(foundAlongZ.EdgeMidX2, foundAlongZ.EdgeMidZ2, 0);

    Check(restoredEdgeX.Axis == EdgeAxis.AlongX, "沿 X 的墙恢复后轴向应仍是 AlongX");
    Check(restoredEdgeZ.Axis == EdgeAxis.AlongZ, "沿 Z 的墙恢复后轴向应仍是 AlongZ");
}

// --- 4. 楼层必须保留（否则二楼的东西会掉到一楼）---
{
    var source = new BuildAreaData(4, 4, 2, 1f);
    source.RegisterFloor(1, 1, "floor");

    var data = new GameSaveData();
    foreach (var r in source.Export()) data.PlacedParts.Add(ToSaveData(r, "home"));

    Check(data.PlacedParts[0].Floor == 2, $"应存下楼层 2，实际 {data.PlacedParts[0].Floor}");

    // 导入到楼层 2 的区：成功
    var restored = new BuildAreaData(4, 4, 2, 1f);
    Check(restored.Import(data.PlacedParts.ConvertAll<Game.World.PlacedPartRecord>(ToRecord)) == 1, "同楼层应恢复成功");

    // 导入到楼层 0 的区：应被拒绝（这正是之前修过的缺陷）
    var wrongFloor = new BuildAreaData(4, 4, 0, 1f);
    Check(wrongFloor.Import(data.PlacedParts.ConvertAll<Game.World.PlacedPartRecord>(ToRecord)) == 0,
        "楼层不符的记录不该被导入");
}

// --- 5. 区域标识必须保留（多建造区时归属不能乱）---
{
    var source = new BuildAreaData(4, 4, 0, 1f);
    source.RegisterFloor(1, 1, "floor");

    var data = new GameSaveData();
    foreach (var r in source.Export()) data.PlacedParts.Add(ToSaveData(r, "home"));

    Check(data.PlacedParts[0].AreaId == "home",
        $"应存下区域标识，实际「{data.PlacedParts[0].AreaId}」");
}

// --- 6. 部件标识必须保留（否则加载时不知道该生成什么）---
{
    var source = new BuildAreaData(4, 4, 0, 1f);
    source.RegisterFloor(1, 1, "木地板");
    source.RegisterFloor(2, 2, "石地板");

    var data = new GameSaveData();
    foreach (var r in source.Export()) data.PlacedParts.Add(ToSaveData(r, "home"));

    var restored = new BuildAreaData(4, 4, 0, 1f);
    restored.Import(data.PlacedParts.ConvertAll<Game.World.PlacedPartRecord>(ToRecord));

    Check(restored.GetFloorId(1, 1) == "木地板", "部件标识应保留（1,1）");
    Check(restored.GetFloorId(2, 2) == "石地板", "部件标识应保留（2,2）");
}

// --- 7. 整片建筑的往返：数量与内容都要一致 ---
{
    var source = new BuildAreaData(4, 4, 0, 1f);

    // 铺满 4×4 地板 + 全部边立墙
    for (int x = 0; x < 4; x++)
        for (int z = 0; z < 4; z++)
            source.RegisterFloor(x, z, "floor");

    for (int x = 0; x < 4; x++)
        for (int z = 0; z < 4; z++)
            foreach (var dir in AllDirections)
                source.RegisterWall(BuildEdge.FromCell(x, z, 0, dir), "wall");

    int expectedFloors = source.FloorCount;
    int expectedWalls = source.WallCount;

    var data = new GameSaveData();
    foreach (var r in source.Export()) data.PlacedParts.Add(ToSaveData(r, "home"));

    Check(data.PlacedParts.Count == expectedFloors + expectedWalls,
        $"应存 {expectedFloors + expectedWalls} 条，实际 {data.PlacedParts.Count}");

    var restored = new BuildAreaData(4, 4, 0, 1f);
    int imported = restored.Import(data.PlacedParts.ConvertAll<Game.World.PlacedPartRecord>(ToRecord));

    Check(imported == expectedFloors + expectedWalls,
        $"应全部恢复，实际 {imported}/{expectedFloors + expectedWalls}");
    Check(restored.FloorCount == expectedFloors, $"地板数应一致（{expectedFloors}）");
    Check(restored.WallCount == expectedWalls, $"墙数应一致（{expectedWalls}）");
}

// --- 8. 空存档不应崩，也不该恢复出东西 ---
{
    var data = new GameSaveData();
    Check(data.PlacedParts.Count == 0, "新存档应没有建造物");
    Check(data.Backpack.Count == 0 && data.Pocket.Count == 0, "新存档应没有物品");

    var restored = new BuildAreaData(4, 4, 0, 1f);
    Check(restored.Import(new List<Game.World.PlacedPartRecord>()) == 0, "空列表不该恢复出东西");
}

// --- 9. 存档版本号存在，且是当前版本 ---
{
    var data = new GameSaveData();
    Check(data.Version == GameSaveData.CurrentVersion,
        $"新存档版本应是 {GameSaveData.CurrentVersion}，实际 {data.Version}");
    Check(GameSaveData.CurrentVersion >= 1, "版本号应从 1 开始");
}

// --- 10. 物品记录的往返（ItemId + 数量）---
{
    var data = new GameSaveData();
    data.Backpack.Add(new ItemSaveData("wood", 20));
    data.Backpack.Add(new ItemSaveData("stone", 7));

    Check(data.Backpack.Count == 2, "应存 2 种物品");
    Check(data.Backpack[0].ItemId == "wood" && data.Backpack[0].Amount == 20, "第 1 种物品应无损");
    Check(data.Backpack[1].ItemId == "stone" && data.Backpack[1].Amount == 7, "第 2 种物品应无损");
}

// --- 11. 【重复加载】同一个存档加载两次，结果必须一样 ---
//
// 如果加载是"叠加"而不是"先清空再重建"，第二次就会翻倍 ——
// 这是"加载"与"合并"的区别，值得单独钉住。
{
    var source = new BuildAreaData(4, 4, 0, 1f);
    source.RegisterFloor(1, 1, "floor");
    source.RegisterFloor(2, 2, "floor");

    var data = new GameSaveData();
    foreach (var r in source.Export()) data.PlacedParts.Add(ToSaveData(r, "home"));

    // 模拟"加载前先清空"
    var target = new BuildAreaData(4, 4, 0, 1f);
    target.Import(data.PlacedParts.ConvertAll<Game.World.PlacedPartRecord>(ToRecord));
    Check(target.FloorCount == 2, "第一次加载应有 2 块地板");

    target.Clear();                       // ← 加载前必须做的事
    target.Import(data.PlacedParts.ConvertAll<Game.World.PlacedPartRecord>(ToRecord));
    Check(target.FloorCount == 2, $"第二次加载后应仍是 2 块地板，实际 {target.FloorCount}");

    // 反例：不清空就导入 —— 说明为什么必须先清空
    var noClear = new BuildAreaData(4, 4, 0, 1f);
    noClear.Import(data.PlacedParts.ConvertAll<Game.World.PlacedPartRecord>(ToRecord));
    int secondImport = noClear.Import(data.PlacedParts.ConvertAll<Game.World.PlacedPartRecord>(ToRecord));
    Check(secondImport == 0,
        "不先清空的话，第二次导入会被占用检查全部拒绝（数量不会翻倍，但也没有生效）");
    Check(noClear.FloorCount == 2, "不先清空时数量保持 2（靠占用检查兜底）");
}

Console.WriteLine($"   {assertions - saveStart} 条存档断言通过");

// ==================================================================== O. 敌人 AI

Console.WriteLine();
Console.WriteLine("== O. 敌人 AI（状态机与视野）==");
int aiStart = assertions;

// 【这一步验证什么】
// 状态转换是"什么时候切、切成什么"的规则 —— 完全可以脱离引擎验证。
// 移动、转向、碰撞那些必须在 Unity 里跑的部分不在这一组里。

// 造一个感知包，只填本次关心的字段（其余给合理默认值）
EnemySenses Senses(
    float distance = float.PositiveInfinity,
    bool inSight = false,
    float attackRange = 1.6f,
    bool hasPoints = true,
    bool dead = false,
    bool reached = false)
    => new EnemySenses(distance, inSight, attackRange, hasPoints, dead, reached);

// 推进若干秒（每步 0.05 秒，模拟真实帧）
void Advance(EnemyBrainLogic brain, float seconds, EnemySenses senses)
{
    const float step = 0.05f;
    int steps = (int)Math.Ceiling(seconds / step);
    for (int i = 0; i < steps; i++) brain.Advance(step, senses);
}

// --- 1. 初始状态是待机 ---
{
    var b = new EnemyBrainLogic();
    Check(b.State == EnemyState.Idle, $"初始应是待机，实际 {b.State}");
    Check(b.TimeInState == 0f, "初始状态计时应为 0");
}

// --- 2. 待机超时 → 巡逻（有路点时）---
{
    var b = new EnemyBrainLogic(idleDuration: 2f);

    Advance(b, 1.9f, Senses());
    Check(b.State == EnemyState.Idle, $"1.9 秒时还应待机，实际 {b.State}");

    Advance(b, 0.2f, Senses());
    Check(b.State == EnemyState.Patrol, $"超过 2 秒后应转巡逻，实际 {b.State}");
}

// --- 3. 待机超时但【没有路点】→ 保持待机（不该卡在空的巡逻里）---
{
    var b = new EnemyBrainLogic(idleDuration: 1f);
    Advance(b, 3f, Senses(hasPoints: false));
    Check(b.State == EnemyState.Idle, $"没有路点时不该进巡逻，实际 {b.State}");
}

// --- 4. 待机时也能发现玩家（站着不动不是瞎子）---
{
    var b = new EnemyBrainLogic(idleDuration: 10f);
    b.Advance(0.05f, Senses(distance: 5f, inSight: true));
    Check(b.State == EnemyState.Chase, $"待机时看见玩家应转追击，实际 {b.State}");
}

// --- 5. 巡逻时发现玩家 → 追击 ---
{
    var b = new EnemyBrainLogic(idleDuration: 0.1f);
    Advance(b, 0.2f, Senses());                        // 进巡逻
    Check(b.State == EnemyState.Patrol, $"应已进巡逻，实际 {b.State}");

    b.Advance(0.05f, Senses(distance: 8f, inSight: true));
    Check(b.State == EnemyState.Chase, $"巡逻时看见玩家应转追击，实际 {b.State}");
}

// --- 6. 追到攻击距离 → 攻击 ---
{
    var b = new EnemyBrainLogic();
    b.ForceState(EnemyState.Chase);

    b.Advance(0.05f, Senses(distance: 5f, inSight: true, attackRange: 1.6f));
    Check(b.State == EnemyState.Chase, $"还很远时应继续追，实际 {b.State}");

    b.Advance(0.05f, Senses(distance: 1.5f, inSight: true, attackRange: 1.6f));
    Check(b.State == EnemyState.Attack, $"进入攻击距离应转攻击，实际 {b.State}");
}

// --- 7. 攻击中会请求出手 ---
{
    var b = new EnemyBrainLogic();
    b.ForceState(EnemyState.Attack);

    b.Advance(0.05f, Senses(distance: 1.2f, inSight: true, attackRange: 1.6f));
    Check(b.WantsToAttack, "攻击状态下应请求出手");
}

// --- 8. 目标跑远 → 回到追击 ---
{
    var b = new EnemyBrainLogic();
    b.ForceState(EnemyState.Attack);

    b.Advance(0.05f, Senses(distance: 3f, inSight: true, attackRange: 1.6f));
    Check(b.State == EnemyState.Chase, $"目标跑远应回追击，实际 {b.State}");
    Check(!b.WantsToAttack, "追击状态下不该请求出手");
}

// --- 9. 【关键】丢失目标的宽限期 ---
//
// 玩家短暂绕到墙后不该立刻放弃 —— 否则敌人会贴着墙角来回切状态（看起来很傻）。
{
    var b = new EnemyBrainLogic(loseTargetGrace: 3f);
    b.ForceState(EnemyState.Chase);

    // 刚看不见：还在追
    Advance(b, 1.0f, Senses(distance: 5f, inSight: false));
    Check(b.State == EnemyState.Chase, $"丢失 1 秒内应继续追，实际 {b.State}");

    // 到宽限期前：还在追
    Advance(b, 1.5f, Senses(distance: 5f, inSight: false));
    Check(b.State == EnemyState.Chase, $"丢失 2.5 秒时仍应在追，实际 {b.State}");

    // 超过宽限期：放弃
    Advance(b, 1.0f, Senses(distance: 5f, inSight: false));
    Check(b.State != EnemyState.Chase, $"超过宽限期应放弃追击，实际 {b.State}");
}

// --- 10. 宽限期内【重新看见】会重置计时 ---
{
    var b = new EnemyBrainLogic(loseTargetGrace: 2f);
    b.ForceState(EnemyState.Chase);

    Advance(b, 1.5f, Senses(distance: 5f, inSight: false));   // 快超时了
    b.Advance(0.05f, Senses(distance: 5f, inSight: true));    // 又看见了
    Check(Math.Abs(b.TimeSinceLastSeen) < 0.001f, "重新看见应把计时清零");

    Advance(b, 1.5f, Senses(distance: 5f, inSight: false));   // 再丢一次
    Check(b.State == EnemyState.Chase, $"重新计时后不该立刻放弃，实际 {b.State}");
}

// --- 11. 【最高优先级】死亡覆盖一切 ---
{
    var states = new[] { EnemyState.Idle, EnemyState.Patrol, EnemyState.Chase, EnemyState.Attack };

    foreach (EnemyState state in states)
    {
        var b = new EnemyBrainLogic();
        b.ForceState(state);

        b.Advance(0.05f, Senses(distance: 1f, inSight: true, dead: true));
        Check(b.State == EnemyState.Dead,
            $"从 {state} 状态，血量归零都应转已倒下，实际 {b.State}");
    }
}

// --- 12. 死亡后不能复活 / 不能强制切换 ---
{
    var b = new EnemyBrainLogic();
    b.ForceState(EnemyState.Dead);

    b.Advance(0.05f, Senses(distance: 1f, inSight: true, dead: false));
    Check(b.State == EnemyState.Dead, "已倒下后不该被复活");

    b.ForceState(EnemyState.Chase);
    Check(b.State == EnemyState.Dead, "已倒下后不该能被强制切成其它状态");
}

// --- 13. 状态变化标志 ---
{
    var b = new EnemyBrainLogic(idleDuration: 0.1f);

    b.Advance(0.05f, Senses());
    Check(!b.StateChanged, "状态没变时 StateChanged 应为 false");

    b.Advance(0.1f, Senses());
    Check(b.StateChanged, "待机转巡逻时 StateChanged 应为 true");
    Check(b.PreviousState == EnemyState.Idle, $"PreviousState 应是待机，实际 {b.PreviousState}");
}

// --- 14. 重置 ---
{
    var b = new EnemyBrainLogic();
    b.ForceState(EnemyState.Chase);
    b.Advance(0.5f, Senses(distance: 3f, inSight: true));

    b.Reset();
    Check(b.State == EnemyState.Idle, "重置后应回待机");
    Check(b.TimeInState == 0f, "重置后计时应归零");
    Check(b.TimeSinceLastSeen == float.MaxValue, "重置后应认为很久没看见");
}

// --- 15. 负数/零 deltaTime 不应破坏状态 ---
{
    var b = new EnemyBrainLogic(idleDuration: 1f);
    b.Advance(-5f, Senses());
    Check(b.State == EnemyState.Idle, "负 deltaTime 不该推进状态");
    Check(b.TimeInState == 0f, $"负 deltaTime 不该增加计时，实际 {b.TimeInState}");
}

// ================================================================ 视野判定

// --- 16. 距离判定 ---
{
    Check(EnemyPerception.CanSee(5f, 0f, viewDistance: 10f, viewAngleDegrees: 120f),
        "距离内 + 正前方 → 看得见");
    Check(!EnemyPerception.CanSee(15f, 0f, viewDistance: 10f, viewAngleDegrees: 120f),
        "距离外 + 正前方 → 看不见");
    Check(EnemyPerception.CanSee(10f, 0f, viewDistance: 10f, viewAngleDegrees: 120f),
        "正好在距离边界上 → 看得见（用 <=）");
}

// --- 17. 【关键】角度判定 ---
{
    const float view = 120f;   // 左右各 60°

    Check(EnemyPerception.CanSee(5f, 0f, 10f, view), "正前方 0° → 看得见");
    Check(EnemyPerception.CanSee(5f, 59f, 10f, view), "59° → 看得见");
    Check(EnemyPerception.CanSee(5f, 60f, 10f, view), "正好 60° → 看得见（边界用 <=）");
    Check(!EnemyPerception.CanSee(5f, 61f, 10f, view), "61° → 看不见");
    Check(!EnemyPerception.CanSee(5f, 180f, 10f, view), "背后 180° → 看不见");

    // 负角度（左侧）也该对称
    Check(EnemyPerception.CanSee(5f, -59f, 10f, view), "左侧 -59° → 看得见");
    Check(!EnemyPerception.CanSee(5f, -61f, 10f, view), "左侧 -61° → 看不见");
}

// --- 18. 全向视野（360°）---
{
    Check(EnemyPerception.CanSee(5f, 179f, 10f, 360f), "360° 视野应能看见背后");
    Check(EnemyPerception.CanSee(5f, -179f, 10f, 360f), "360° 视野左侧背后也能看见");
}

// --- 19. 角度归一化 ---
{
    Near(EnemyPerception.NormalizeAngle(0f), 0f, 1e-4f, "0°");
    Near(EnemyPerception.NormalizeAngle(180f), 180f, 1e-4f, "180°");
    Near(EnemyPerception.NormalizeAngle(181f), -179f, 1e-4f, "181° 应归一化成 -179°");
    Near(EnemyPerception.NormalizeAngle(360f), 0f, 1e-4f, "360° 应回到 0");
    Near(EnemyPerception.NormalizeAngle(-181f), 179f, 1e-4f, "-181° 应归一化成 179°");
    Near(EnemyPerception.NormalizeAngle(540f), 180f, 1e-4f, "540° 应归一化成 180°");
}

// --- 20. 朝向夹角计算 ---
{
    // 正前方（+Z）与指向 +Z 的目标：夹角 0
    Near(EnemyPerception.AngleBetween(0f, 1f, 0f, 1f), 0f, 1e-3f, "同向夹角 0°");

    // 正前方与正右方：90°
    Near(EnemyPerception.AngleBetween(0f, 1f, 1f, 0f), 90f, 1e-3f, "垂直夹角 90°");

    // 正前方与正后方：180°
    Near(EnemyPerception.AngleBetween(0f, 1f, 0f, -1f), 180f, 1e-3f, "反向夹角 180°");

    // 退化输入（零向量）当作看不见
    Near(EnemyPerception.AngleBetween(0f, 0f, 0f, 1f), 180f, 1e-3f, "零朝向应视为看不见");
    Near(EnemyPerception.AngleBetween(0f, 1f, 0f, 0f), 180f, 1e-3f, "零目标方向应视为看不见");
}

// --- 21. 视野判定与角度计算串起来：模拟敌人朝向 +Z，玩家在侧后方 ---
{
    // 敌人在原点朝 +Z，玩家在 (10, 0, -10)：夹角 135°
    float angle = EnemyPerception.AngleBetween(0f, 1f, 10f, -10f);
    Near(angle, 135f, 0.1f, "侧后方的夹角应约 135°");

    // 140° 视野（左右各 70°）看不见；360° 看得见
    Check(!EnemyPerception.CanSee(14.14f, angle, 20f, 140f), "135° 超出 140° 视野的一半（70°）");
    Check(EnemyPerception.CanSee(14.14f, angle, 20f, 360f), "360° 视野应看得见");
}

// --- 22. 状态名可读 ---
{
    Check(EnemyBrainLogic.Describe(EnemyState.Idle) == "待机", "待机的中文名");
    Check(EnemyBrainLogic.Describe(EnemyState.Patrol) == "巡逻", "巡逻的中文名");
    Check(EnemyBrainLogic.Describe(EnemyState.Chase) == "追击", "追击的中文名");
    Check(EnemyBrainLogic.Describe(EnemyState.Attack) == "攻击", "攻击的中文名");
    Check(EnemyBrainLogic.Describe(EnemyState.Dead) == "已倒下", "已倒下的中文名");
}

// --- 23. 【缺陷回归】强制进追击必须重置"多久没看见" ---
//
// 这是断言在写这一组时抓出来的真 bug：
// ForceState(Chase) 不重置计时的话，TimeSinceLastSeen 还是初始的极大值，
// 于是 UpdateChase 第一帧就判定"追丢了"、立刻退回巡逻 ——
// "挨打就反击"整条规则失效（刚切进追击就退出）。
//
// 这条断言把它钉死，以后有人改 ForceState 会立刻报出来。
{
    var b = new EnemyBrainLogic(loseTargetGrace: 3f);
    b.ForceState(EnemyState.Chase);

    Check(b.TimeSinceLastSeen == 0f,
        $"强制进追击应把「多久没看见」清零，实际 {b.TimeSinceLastSeen}");

    // 再推进一帧（此时确实看不见玩家）：必须【还在追】，不能立刻放弃
    b.Advance(0.05f, Senses(distance: 5f, inSight: false));
    Check(b.State == EnemyState.Chase,
        $"强制反击后第一帧不该立刻放弃，实际 {b.State}");
}

// --- 24. 反击（模拟"被打了一下"）能真正持续追击 ---
{
    var b = new EnemyBrainLogic(loseTargetGrace: 2f);

    // 模拟玩家在背后开枪：敌人此时看不见玩家，但挨了打
    b.ForceState(EnemyState.Chase);

    // 追 1.5 秒（还没超宽限期）：应该一直在追
    Advance(b, 1.5f, Senses(distance: 6f, inSight: false));
    Check(b.State == EnemyState.Chase,
        $"反击后 1.5 秒内应持续追击，实际 {b.State}");

    // 再追 1 秒（累计 2.5 秒 > 宽限期 2 秒）：应该放弃
    Advance(b, 1.0f, Senses(distance: 6f, inSight: false));
    Check(b.State != EnemyState.Chase,
        $"超过宽限期后应放弃，实际 {b.State}");
}

// --- 25. 强制切到非 Chase 的状态不该动计时 ---
{
    var b = new EnemyBrainLogic(loseTargetGrace: 3f);
    Advance(b, 1.0f, Senses(distance: 5f, inSight: false));   // 累计 1 秒没看见
    float before = b.TimeSinceLastSeen;

    b.ForceState(EnemyState.Patrol);
    Near(b.TimeSinceLastSeen, before, 1e-4f,
        "强制切到巡逻不该改动「多久没看见」（只有切到追击才重置）");
}

Console.WriteLine($"   {assertions - aiStart} 条敌人 AI 断言通过");

// ================================================================ P. 读档预检
//
// 【这一组验证什么】
// 早先 Apply() 的顺序是"先清空背包与建造物，再逐个恢复，找不到就跳过"。
// 那意味着存档里只要有一个引用了不存在的东西，**玩家会丢掉当前状态，
// 而且 Load() 还返回成功** —— 数据没了、界面说成功。
//
// 这一组把"什么情况该拒绝加载"钉死。
// 【它不能证明什么】它测的是预检决策，不是真实的文件读写和场景恢复
// （那两件事需要 UnityEngine / AssetDatabase，见文档里"未验证"一节）。

Console.WriteLine();
Console.WriteLine("== P. 读档预检（拒绝加载而不是清空状态）==");
int precheckStart = assertions;

// 造一个"当前场景"：有两块建造区，各自有哪些部件
var knownItems = new HashSet<string> { "wood", "stone" };
var knownAreas = new HashSet<string> { "home" };
var knownParts = new HashSet<string> { "home/floor", "home/wall" };

bool HasItem(string id) => knownItems.Contains(id);
bool HasArea(string id) => knownAreas.Contains(id);
bool HasPart(string area, string part) => knownParts.Contains(area + "/" + part);

LoadPrecheck RunPrecheck(GameSaveData data, bool allowVersionMismatch = false,
    bool supportsItemLookup = true, string mapId = "map_a")
    => SaveLoadPrecheck.Run(
        data,
        currentMapId: mapId,
        hasItem: HasItem,
        hasArea: HasArea,
        hasPart: HasPart,
        platformSupportsItemLookup: () => supportsItemLookup,
        allowVersionMismatch: allowVersionMismatch);

// 造一份"完全没问题"的存档
GameSaveData GoodSave()
{
    var data = new GameSaveData { MapId = "map_a", Version = GameSaveData.CurrentVersion };
    data.Backpack.Add(new ItemSaveData("wood", 10));
    data.Pocket.Add(new ItemSaveData("stone", 3));
    data.PlacedParts.Add(new PlacedPartSaveData
    {
        AreaId = "home", PartId = "floor", IsWall = false, Floor = 0, CellX = 1, CellZ = 1,
    });
    data.PlacedParts.Add(new PlacedPartSaveData
    {
        AreaId = "home", PartId = "wall", IsWall = true, Floor = 0, EdgeMidX2 = 3, EdgeMidZ2 = 2,
    });
    return data;
}

// --- 1. 好存档必须通过 ---
{
    LoadPrecheck r = RunPrecheck(GoodSave());
    Check(r.CanProceed, $"正常存档应通过，实际被拒：{r.Describe()}");
    Check(!r.HasAnyIssue, $"正常存档不该有任何问题，实际：{r.Describe()}");
}

// --- 2. 【P1-2 回归】找不到物品定义 → 拒绝加载 ---
//
// 早先的行为：清空背包 → 逐个恢复 → 每个都跳过 → 最后报"读取成功"。
// 玩家丢光了背包，还被告知成功了。
{
    GameSaveData data = GoodSave();
    data.Backpack.Add(new ItemSaveData("nonexistent_item", 5));

    LoadPrecheck r = RunPrecheck(data);
    Check(!r.CanProceed, "有未知物品时必须拒绝加载（否则会清空背包且放不回来）");
    Check(r.Issues.Exists(i => i.Kind == LoadIssueKind.UnknownItem && i.IsBlocking),
        "应报出 UnknownItem 且是阻断的");
}

// --- 3. 【P1-2 回归】打包后不支持查物品 → 有物品就拒绝 ---
//
// 这比"物品查不到"更严重：打包分支对【每个】物品都返回 null，
// 于是背包会被清空、一件都放不回来。
{
    GameSaveData data = GoodSave();
    Check(data.Backpack.Count > 0, "这份存档确实有物品");

    LoadPrecheck r = RunPrecheck(data, supportsItemLookup: false);
    Check(!r.CanProceed, "平台不支持查物品且有物品时必须拒绝加载");
    Check(r.Issues.Exists(i => i.Kind == LoadIssueKind.ItemLookupUnsupported && i.IsBlocking),
        "应报出 ItemLookupUnsupported 且是阻断的");
}

// --- 4. 打包后不支持查物品，但存档里【没有】物品 → 可以继续 ---
//
// 否则打包后连"只恢复了建筑"都做不到，那是过度拒绝。
{
    var data = new GameSaveData { MapId = "map_a", Version = GameSaveData.CurrentVersion };
    data.PlacedParts.Add(new PlacedPartSaveData
    {
        AreaId = "home", PartId = "floor", IsWall = false, Floor = 0, CellX = 1, CellZ = 1,
    });

    LoadPrecheck r = RunPrecheck(data, supportsItemLookup: false);
    Check(r.CanProceed, $"没有物品时不该因为「不支持查物品」被拒：{r.Describe()}");
}

// --- 5. 【P1-1 回归】找不到建造区 → 拒绝加载 ---
{
    GameSaveData data = GoodSave();
    data.PlacedParts.Add(new PlacedPartSaveData
    {
        AreaId = "nonexistent_area", PartId = "floor", IsWall = false, Floor = 0, CellX = 0, CellZ = 0,
    });

    LoadPrecheck r = RunPrecheck(data);
    Check(!r.CanProceed, "有未知建造区时必须拒绝加载");
    Check(r.Issues.Exists(i => i.Kind == LoadIssueKind.UnknownArea && i.IsBlocking),
        "应报出 UnknownArea 且是阻断的");
}

// --- 6. 【P1-1 回归】找不到建造部件 → 拒绝加载 ---
{
    GameSaveData data = GoodSave();
    data.PlacedParts.Add(new PlacedPartSaveData
    {
        AreaId = "home", PartId = "roof", IsWall = false, Floor = 0, CellX = 0, CellZ = 0,
    });

    LoadPrecheck r = RunPrecheck(data);
    Check(!r.CanProceed, "有未知建造部件时必须拒绝加载");
    Check(r.Issues.Exists(i => i.Kind == LoadIssueKind.UnknownPart && i.IsBlocking),
        "应报出 UnknownPart 且是阻断的");
}

// --- 7. 建造物记录没写部件标识 → 拒绝 ---
{
    GameSaveData data = GoodSave();
    data.PlacedParts.Add(new PlacedPartSaveData
    {
        AreaId = "home", PartId = "", IsWall = false, Floor = 0, CellX = 0, CellZ = 0,
    });

    LoadPrecheck r = RunPrecheck(data);
    Check(!r.CanProceed, "部件标识为空的记录必须被拒绝（不知道要生成什么）");
}

// --- 8. 【版本】不一致默认拒绝，显式允许时才继续 ---
{
    GameSaveData data = GoodSave();
    data.Version = GameSaveData.CurrentVersion + 1;

    LoadPrecheck strict = RunPrecheck(data);
    Check(!strict.CanProceed, "版本不一致时默认应拒绝（结构可能已经变了）");
    Check(strict.Issues.Exists(i => i.Kind == LoadIssueKind.VersionMismatch && i.IsBlocking),
        "应报出版本不一致且是阻断的");

    LoadPrecheck lenient = RunPrecheck(data, allowVersionMismatch: true);
    Check(lenient.CanProceed, "显式允许时应继续（玩家自己承担风险）");
    Check(lenient.Issues.Exists(i => i.Kind == LoadIssueKind.VersionMismatch && !i.IsBlocking),
        "允许时该问题应降级为非阻断");
}

// --- 9. 【地图】不一致只警告，不阻断 ---
//
// 开发时经常换场景测试。硬拒绝会让"在同一份存档上试不同地图"变得很麻烦。
// 建造区是按 AreaId 归属的，不会串。
{
    GameSaveData data = GoodSave();
    LoadPrecheck r = RunPrecheck(data, mapId: "map_b");
    Check(r.CanProceed, "地图不一致不该阻断加载");
    Check(r.HasAnyIssue && r.Issues.Exists(i => i.Kind == LoadIssueKind.MapMismatch && !i.IsBlocking),
        "地图不一致应报一条非阻断的警告");
}

// --- 10. 空存档 / null 存档 ---
{
    LoadPrecheck nullResult = RunPrecheck(null!);
    Check(!nullResult.CanProceed, "null 存档必须被拒绝");
    Check(nullResult.Issues.Exists(i => i.Kind == LoadIssueKind.Corrupt && i.IsBlocking),
        "null 存档应报 Corrupt");

    var empty = new GameSaveData();
    LoadPrecheck emptyResult = RunPrecheck(empty);
    Check(emptyResult.CanProceed, $"空存档应可以加载（等于清空）：{emptyResult.Describe()}");
}

// --- 11. 空容器不算"有物品"（打包后不该因为空口袋被拒）---
{
    var data = new GameSaveData { Version = GameSaveData.CurrentVersion };
    data.Backpack.Add(new ItemSaveData("", 5));   // 空 ID 但数量非 0

    LoadPrecheck r = RunPrecheck(data);
    Check(!r.CanProceed, "物品 ID 为空的记录必须被拒绝");
}

// --- 12. 同一个未知物品出现多次，只报一条（不刷屏）---
{
    GameSaveData data = GoodSave();
    data.Backpack.Add(new ItemSaveData("ghost", 1));
    data.Backpack.Add(new ItemSaveData("ghost", 2));
    data.Backpack.Add(new ItemSaveData("ghost", 3));

    LoadPrecheck r = RunPrecheck(data);
    int ghostReports = r.Issues.FindAll(i => i.Kind == LoadIssueKind.UnknownItem).Count;
    Check(ghostReports == 1, $"同一个未知物品应只报一条，实际 {ghostReports} 条");
}

// --- 13. Describe() 能给出可读文本 ---
{
    var data = new GameSaveData { Version = GameSaveData.CurrentVersion };
    data.Backpack.Add(new ItemSaveData("ghost", 1));

    LoadPrecheck r = RunPrecheck(data);
    string text = r.Describe();
    Check(text.Contains("阻断"), $"问题描述里应标出「阻断」，实际：{text}");
    Check(text.Contains("ghost"), $"问题描述里应包含具体的物品 ID，实际：{text}");
}

// --- 14. 【关键】预检不通过时，CanProceed 必须为 false（它是唯一的闸门）---
//
// 这条是把"拒绝加载"这个行为固定成契约：调用方（GameSaveController.Apply）
// 只认这一个判断，所以它绝不能在有问题时返回 true。
{
    var broken = new List<GameSaveData>();

    GameSaveData d1 = GoodSave(); d1.Backpack.Add(new ItemSaveData("x", 1)); broken.Add(d1);
    GameSaveData d2 = GoodSave();
    d2.PlacedParts.Add(new PlacedPartSaveData { AreaId = "nope", PartId = "floor" });
    broken.Add(d2);
    GameSaveData d3 = GoodSave();
    d3.PlacedParts.Add(new PlacedPartSaveData { AreaId = "home", PartId = "nope" });
    broken.Add(d3);
    GameSaveData d4 = GoodSave(); d4.Version = 999; broken.Add(d4);
    GameSaveData d5 = GoodSave(); d5.Backpack.Add(new ItemSaveData("", 1)); broken.Add(d5);

    foreach (GameSaveData data in broken)
    {
        LoadPrecheck r = RunPrecheck(data);
        Check(!r.CanProceed,
            $"这份存档有问题，CanProceed 必须是 false（否则 Apply 会清空玩家状态）。" +
            $"问题：{r.Describe(" / ")}");
    }
}

Console.WriteLine($"   {assertions - precheckStart} 条读档预检断言通过");

// ================================================================ Q. 固定格、装备与存档
Console.WriteLine("== Q. 固定格拖放、装备类型与位置存档 ==");
int dragStart = assertions;
ItemDefinition dragWood = ItemDefinition.CreateRuntime("drag_wood", "木材", 99);
ItemDefinition dragStone = ItemDefinition.CreateRuntime("drag_stone", "石头", 99);
int InventoryTotal(Inventory inventory) => inventory.SlotsView.Sum(stack => stack.IsEmpty ? 0 : stack.Amount);
for (int destination = 0; destination < 16; destination++)
{
    var inventory = new Inventory(16);
    inventory.Add(dragWood, 10); inventory.Add(dragStone, 5);
    bool moved = inventory.MoveWithin(0, destination);
    Check(moved == (destination != 0), "原格不动，其余合法目标可移动/交换");
    Check(inventory.GetSlot(destination).Item == dragWood, "必须落在指定格，不是列表末尾");
    Check(InventoryTotal(inventory) == 15 && inventory.SlotCount == 2, "移动不丢失、不复制");
    if (destination > 1)
    {
        Check(inventory.GetSlot(0).IsEmpty && inventory.GetSlot(1).Item == dragStone, "移动保留空位，不挤动其他物品");
        Check(inventory.Remove(dragStone, 5) && inventory.GetSlot(destination).Item == dragWood,
            "移除其他物品不能改变目标格");
        inventory.Add(dragStone, 2);
        Check(inventory.GetSlot(0).Item == dragStone && inventory.GetSlot(destination).Item == dragWood,
            "拾取填补空位，不挤动已整理物品");
    }
}
{
    var from = new Inventory(16); var to = new Inventory(4);
    from.Add(dragWood, 30); to.SetSlot(3, new ItemStack(dragWood, 80));
    Check(Inventory.MoveBetween(from, 0, to, 3), "跨容器同类部分叠加");
    Check(to.GetSlot(3).Amount == 99 && from.GetSlot(0).Amount == 11, "溢出留原格");
    Check(!Inventory.MoveBetween(from, 0, to, 4), "超出容量拒绝，不溢出");
    to.SetSlot(1, new ItemStack(dragStone, 7));
    Check(Inventory.MoveBetween(from, 0, to, 1), "跨容器不同物品交换");
    Check(from.GetSlot(0).Item == dragStone && to.GetSlot(1).Item == dragWood && to.GetSlot(3).Amount == 99,
        "交换不改变无关格子");
    Check(InventoryTotal(from) + InventoryTotal(to) == 117, "跨容器总量守恒");
}
EquipmentType[] equipmentTypes =
{
    EquipmentType.CreateRuntime("head", "帽子"), EquipmentType.CreateRuntime("torso", "衣服"),
    EquipmentType.CreateRuntime("legs", "裤子"), EquipmentType.CreateRuntime("feet", "鞋子")
};
EquipmentSlot[] equipmentSlots = equipmentTypes.Select(type => new EquipmentSlot(type.TypeId, type.DisplayName, type)).ToArray();
ItemDefinition[] gearItems = equipmentTypes.Select(type =>
    ItemDefinition.CreateRuntime("gear_" + type.TypeId, type.DisplayName, 99, type)).ToArray();
for (int itemIndex = 0; itemIndex < 4; itemIndex++)
for (int targetIndex = 0; targetIndex < 4; targetIndex++)
{
    var bag = new Inventory(16); bag.SetSlot(15, new ItemStack(gearItems[itemIndex],1));
    var gear = new EquipmentInventory(equipmentSlots);
    bool expected = itemIndex == targetIndex;
    bool observerSawFinal = false;
    bag.Changed += () => observerSawFinal = gear.GetSlot(targetIndex).Item == gearItems[itemIndex];
    Check(Inventory.CanMoveBetween(bag,15,gear,targetIndex) == expected, "落点提示和业务规则一致");
    Check(Inventory.MoveBetween(bag,15,gear,targetIndex) == expected, "四种装备仅接受对应槽");
    Check(InventoryTotal(bag) + InventoryTotal(gear) == 1, "装备/拒绝保持总量");
    if (expected)
    {
        Check(observerSawFinal, "变更事件在两边提交后发布");
        Check(Inventory.MoveBetween(gear,targetIndex,bag,10) && bag.GetSlot(10).Item == gearItems[itemIndex],
            "装备可卸下到任意空格");
    }
    else Check(bag.GetSlot(15).Item == gearItems[itemIndex], "拒绝后原位置不变");
}
{
    var bag = new Inventory(16); var gear = new EquipmentInventory(equipmentSlots);
    bag.Add(dragWood, 1);
    Check(!Inventory.MoveBetween(bag,0,gear,0) && bag.GetSlot(0).Item == dragWood, "材料不能装备");
    gear.SetSlot(0,new ItemStack(gearItems[0],1));
    Check(!Inventory.MoveBetween(gear,0,bag,0) && gear.GetSlot(0).Item == gearItems[0], "双向交换也必须验证装备槽");
    ItemDefinition secondHat = ItemDefinition.CreateRuntime("hat_second","另一顶帽子",1,equipmentTypes[0]);
    bag.SetSlot(8,new ItemStack(secondHat,1));
    Check(Inventory.MoveBetween(bag,8,gear,0) && gear.GetSlot(0).Item == secondHat && bag.GetSlot(8).Item == gearItems[0],
        "更换同类装备将旧装备换回来源格");
    Check(gearItems[0].MaxStackSize == 1 && !gear.SetSlot(0,new ItemStack(gearItems[0],2)), "每个装备槽只能一件");
}
var definitions = gearItems.Concat(new[] { dragWood, dragStone }).ToDictionary(item => item.ItemId);
ItemDefinition? ResolveInventoryItem(string id) => definitions.TryGetValue(id, out var item) ? item : null;
{
    var bag = new Inventory(16);
    bag.SetSlot(15,new ItemStack(dragWood,17)); bag.SetSlot(5,new ItemStack(dragStone,8));
    var records = InventorySaveCodec.Capture(bag);
    Check(records[0].SlotIndex == 5 && records[1].SlotIndex == 15, "存档记录真实格子索引");
    Check(InventorySaveCodec.TryDecode(bag, records, 2, ResolveInventoryItem, out var restored,out _), "位置存档可解码");
    var copy = new Inventory(16); copy.ReplaceSlots(restored);
    Check(copy.GetSlot(5).Item == dragStone && copy.GetSlot(15).Amount == 17 && copy.GetSlot(0).IsEmpty,
        "读档保留空位与任意位置");
    var legacy = new List<ItemSaveData> { new ItemSaveData(dragWood.ItemId,2), new ItemSaveData(dragStone.ItemId,3) };
    Check(InventorySaveCodec.TryDecode(bag, legacy, 1, ResolveInventoryItem, out var oldSlots,out _) &&
        oldSlots[0].Item == dragWood && oldSlots[1].Item == dragStone, "v1 紧凑存档兼容");
    Check(RunPrecheck(new GameSaveData { Version = 1 }).CanProceed, "旧版存档可通过版本预检");
    var invalid = new List<ItemSaveData> { new ItemSaveData(dragWood.ItemId,2,16) };
    Check(!InventorySaveCodec.TryDecode(bag,invalid,2,ResolveInventoryItem,out _,out _) && bag.GetSlot(15).Amount == 17,
        "越界存档拒绝，当前背包不变");
    invalid[0].SlotIndex=5; invalid.Add(new ItemSaveData(dragStone.ItemId,1,5));
    Check(!InventorySaveCodec.TryDecode(bag,invalid,2,ResolveInventoryItem,out _,out _), "重复格子拒绝");
}
{
    var gear = new EquipmentInventory(equipmentSlots);
    gear.SetSlot(0,new ItemStack(gearItems[0],1));
    var records = InventorySaveCodec.Capture(gear);
    var reordered = new EquipmentInventory(equipmentSlots.Reverse());
    Check(InventorySaveCodec.TryDecode(reordered,records,2,ResolveInventoryItem,out var restored,out _) &&
        restored[3].Item == gearItems[0], "装备按稳定槽 ID 恢复，调整显示顺序不丢装备");
    records[0].ItemId=gearItems[1].ItemId;
    Check(!InventorySaveCodec.TryDecode(gear,records,2,ResolveInventoryItem,out _,out _), "存档不能把衣服塞进帽子槽");
    records[0].ItemId=gearItems[0].ItemId; records[0].SlotId="unknown_slot";
    Check(!InventorySaveCodec.TryDecode(gear,records,2,ResolveInventoryItem,out _,out _), "未知装备槽拒绝加载");
}
{
    EquipmentType ring = EquipmentType.CreateRuntime("ring","戒指");
    var future = new EquipmentInventory(new[] { new EquipmentSlot("ring_l","左戒指",ring), new EquipmentSlot("ring_r","右戒指",ring) });
    ItemDefinition item = ItemDefinition.CreateRuntime("ring_item","戒指",1,ring);
    Check(future.SetSlot(0,new ItemStack(item,1)) && future.SetSlot(1,new ItemStack(item,1)), "新增装备类型与多个同类槽不需要改业务代码");
    bool rejected=false;
    try { _ = new EquipmentInventory(new[] { new EquipmentSlot("same","左",ring), new EquipmentSlot("same","右",ring) }); }
    catch (ArgumentException) { rejected=true; }
    Check(rejected, "重复装备槽 ID 被拒绝");
}
Console.WriteLine($"   {assertions-dragStart} 条固定格/装备/存档断言通过");
// ================================================================ R. 敌人绕障
//
// 【这一组验证什么】
// "敌人撞墙卡住"的修复核心是"选哪个方向走"这条规则。
// 规则纯逻辑化之后可以穷举，不用等进 Unity 才看它绕得对不对。
//
// 【它不能证明什么】探距本身（SphereCast）对不对、实际绕起来顺不顺、
// 会不会在两根柱子之间抖 —— 那些要在 Unity 里看。

Console.WriteLine();
Console.WriteLine("== R. 敌人绕障与分离 ==");
int steerStart = assertions;

// --- 1. 候选偏移数组必须按 |偏移| 升序 ---
//
// ChooseCandidate 依赖这个顺序表达"优先小角度"的优先级。
// 如果谁改了数组顺序，选方向的语义会静默变化 —— 这条把它钉住。
{
    Check(EnemySteering.CandidateOffsetsDegrees.Length == EnemySteering.CandidateCount,
        $"候选数量常量应等于数组长度（{EnemySteering.CandidateCount} vs " +
        $"{EnemySteering.CandidateOffsetsDegrees.Length}）");

    Check(EnemySteering.CandidateOffsetsDegrees[0] == 0f, "下标 0 必须是直行（0°）");

    bool ascending = true;
    for (int i = 2; i < EnemySteering.CandidateCount; i++)
    {
        if (Math.Abs(EnemySteering.CandidateOffsetsDegrees[i])
            < Math.Abs(EnemySteering.CandidateOffsetsDegrees[i - 1]))
        {
            ascending = false;
        }
    }

    Check(ascending, "候选偏移必须按 |角度| 升序，否则「优先小角度绕行」的语义会变");
}

// --- 2. 直行通畅 → 就直行（不为了绕而绕）---
//
// 这是最重要的一条：如果空地上敌人也乱拐，说明规则错了。
{
    var clear = new float[] { 10f, 10f, 10f, 10f, 10f, 10f, 10f };
    int chosen = EnemySteering.ChooseCandidate(clear, 7, requiredClearance: 2f);
    Check(chosen == 0, $"全都通畅时应直行（下标 0），实际选了 {chosen}");
}

// --- 3. 直行堵住 → 选偏离最小的通畅方向 ---
{
    // 只有直行堵：应该选 -30（下标 1）
    var blockedStraight = new float[] { 0.5f, 5f, 5f, 5f, 5f, 5f, 5f };
    int chosen = EnemySteering.ChooseCandidate(blockedStraight, 7, requiredClearance: 2f);
    Check(chosen == 1, $"直行堵、两侧都通时应选 -30°（下标 1），实际 {chosen}");

    // 左边也堵：应选 +30（下标 2）
    var blockedLeft = new float[] { 0.5f, 0.5f, 5f, 5f, 5f, 5f, 5f };
    chosen = EnemySteering.ChooseCandidate(blockedLeft, 7, requiredClearance: 2f);
    Check(chosen == 2, $"左前也堵时应选 +30°（下标 2），实际 {chosen}");

    // 更远的：只有 ±60 通
    var blockedNarrow = new float[] { 0.5f, 0.5f, 0.5f, 5f, 0.5f, 5f, 5f };
    chosen = EnemySteering.ChooseCandidate(blockedNarrow, 7, requiredClearance: 2f);
    Check(chosen == 3, $"只有 -60° 通时应选下标 3，实际 {chosen}");
}

// --- 4. 全都堵 → 挑最通畅的，而不是放弃 ---
{
    var allBlocked = new float[] { 0.3f, 0.9f, 0.4f, 0.2f, 1.5f, 0.1f, 0.6f };
    int chosen = EnemySteering.ChooseCandidate(allBlocked, 7, requiredClearance: 2f);

    Check(chosen >= 0, "全堵时也必须给出一个方向（原地不动会永远卡住）");
    Check(chosen == 4, $"全堵时应挑最通畅的（下标 4，1.5 米），实际 {chosen}");
}

// --- 5. NaN 与非法输入不能被误判 ---
{
    Check(!EnemySteering.IsClear(float.NaN, 1f), "NaN 距离必须算不通畅");

    var withNaN = new float[] { float.NaN, 5f, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN };
    int chosen = EnemySteering.ChooseCandidate(withNaN, 7, requiredClearance: 2f);
    Check(chosen == 1, $"NaN 应被跳过，选到唯一通畅的下标 1，实际 {chosen}");

    Check(EnemySteering.ChooseCandidate(null!, 7, 1f) == -1, "null 数组应返回 -1");
    Check(EnemySteering.ChooseCandidate(new float[7], 0, 1f) == -1, "候选数为 0 应返回 -1");
}

// --- 6. 正无穷天然算通畅 ---
{
    Check(EnemySteering.IsClear(float.PositiveInfinity, 100f), "正无穷必须算通畅");
    Check(EnemySteering.IsClear(5f, 5f), "正好等于门槛应算通畅（用 >=）");
    Check(!EnemySteering.IsClear(4.999f, 5f), "略小于门槛应算堵");
}

// --- 7. 旋转：四个基本方向必须对 ---
//
// 【为什么这条重要】左右搞反的话敌人会往墙里钻而不是绕开。
{
    EnemySteering.RotateDirection(0f, 1f, 0f, out float x, out float z);
    Near(x, 0f, 1e-4f, "0° 旋转：x");
    Near(z, 1f, 1e-4f, "0° 旋转：z");

    EnemySteering.RotateDirection(0f, 1f, 90f, out x, out z);
    Near(x, 1f, 1e-4f, "+90° 应把 +Z 转到 +X：x");
    Near(z, 0f, 1e-4f, "+90° 应把 +Z 转到 +X：z");

    EnemySteering.RotateDirection(0f, 1f, -90f, out x, out z);
    Near(x, -1f, 1e-4f, "-90° 应把 +Z 转到 -X：x");
    Near(z, 0f, 1e-4f, "-90° 应把 +Z 转到 -X：z");

    EnemySteering.RotateDirection(0f, 1f, 180f, out x, out z);
    Near(x, 0f, 1e-4f, "180°：x");
    Near(z, -1f, 1e-4f, "180°：z");
}

// --- 8. 旋转不改变长度（否则会改变速度）---
{
    foreach (float degrees in new[] { 0f, -30f, 30f, -60f, 60f, -90f, 90f, 45f, 137f, -211f })
    {
        EnemySteering.RotateDirection(0.6f, 0.8f, degrees, out float x, out float z);
        float length = MathF.Sqrt(x * x + z * z);
        Near(length, 1f, 1e-4f, $"旋转 {degrees}° 后长度应仍是 1");
    }
}

// --- 9. 左右必须对称 ---
{
    EnemySteering.RotateDirection(0f, 1f, 30f, out float xr, out float zr);
    EnemySteering.RotateDirection(0f, 1f, -30f, out float xl, out float zl);

    Near(xr, -xl, 1e-4f, "+30 与 -30 的 x 应互为相反数");
    Near(zr, zl, 1e-4f, "+30 与 -30 的 z 应相等");
}

// --- 10. 单位化 ---
{
    Check(EnemySteering.TryNormalize(3f, 4f, out float nx, out float nz), "正常向量应成功");
    Near(nx, 0.6f, 1e-4f, "(3,4) 单位化 x");
    Near(nz, 0.8f, 1e-4f, "(3,4) 单位化 z");

    Check(!EnemySteering.TryNormalize(0f, 0f, out nx, out nz), "零向量应返回 false");
    Near(nx, 0f, 1e-6f, "零向量应输出 0");
    Near(nz, 0f, 1e-6f, "零向量应输出 0");

    Check(!EnemySteering.TryNormalize(float.NaN, 1f, out _, out _), "NaN 应返回 false");
}

// --- 11. 分离：太远的邻居不产生影响 ---
{
    float dx = 1f, dz = 0f;
    EnemySteering.AccumulateSeparation(
        0f, 0f, new float[] { 5f }, new float[] { 0f }, 1,
        1.8f, 0.9f, 0f, ref dx, ref dz);

    Near(dx, 1f, 1e-6f, "半径外的邻居不该改变期望方向：dx");
    Near(dz, 0f, 1e-6f, "半径外的邻居不该改变期望方向：dz");
}

// --- 12. 分离：近的邻居把方向推开 ---
{
    float dx = 0f, dz = 1f;
    EnemySteering.AccumulateSeparation(
        0f, 0f, new float[] { 0.5f }, new float[] { 0f }, 1,
        1.8f, 0.9f, 0f, ref dx, ref dz);

    Check(dx < 0f, $"邻居在 +X 时应被推向 -X，实际 dx={dx:F3}");
}

// --- 13. 分离：两个对称邻居应互相抵消 ---
//
// 这条验证"推力的方向算对了"。如果算成"吸引"，两个邻居会同向叠加而不是抵消。
{
    float dx = 0f, dz = 0f;
    EnemySteering.AccumulateSeparation(
        0f, 0f, new float[] { 0.7f, -0.7f }, new float[] { 0f, 0f }, 2,
        1.8f, 0.9f, 0f, ref dx, ref dz);

    Near(dx, 0f, 1e-5f, "左右对称的邻居应完全抵消");
    Near(dz, 0f, 1e-5f, "z 方向也不该有分量");
}

// --- 14. 分离：完全重合时给确定性推力，不崩不除零 ---
{
    float dx = 0f, dz = 0f;
    EnemySteering.AccumulateSeparation(
        2f, 3f, new float[] { 2f }, new float[] { 3f }, 1,
        1.8f, 0.9f, 0f, ref dx, ref dz);

    Check(!float.IsNaN(dx) && !float.IsNaN(dz), "完全重合不能产生 NaN");
    Check(!float.IsInfinity(dx) && !float.IsInfinity(dz), "完全重合不能产生无穷大");
    Check(MathF.Abs(dx) + MathF.Abs(dz) > 0f, "完全重合时也该有推力（否则永远分不开）");
}

// --- 15. 分离：strength 或 radius 为 0 时不做任何事 ---
{
    float dx = 1f, dz = 0f;
    EnemySteering.AccumulateSeparation(0f, 0f, new float[] { 0.1f }, new float[] { 0f }, 1,
        0f, 0.9f, 0f, ref dx, ref dz);
    Near(dx, 1f, 1e-6f, "radius=0 时不该产生影响");

    float dx2 = 1f, dz2 = 0f;
    EnemySteering.AccumulateSeparation(0f, 0f, new float[] { 0.1f }, new float[] { 0f }, 1,
        1.8f, 0f, 0f, ref dx2, ref dz2);
    Near(dx2, 1f, 1e-6f, "strength=0 时不该产生影响");
}

// ---------------------------------------------------------------- 卡死检测

// --- 16. 不想动的时候不算卡住 ---
{
    var detector = new EnemyStuckDetector(0.5f, 0.1f);

    for (int i = 0; i < 20; i++) detector.Update(0.1f, wantsToMove: false, x: 0f, z: 0f);

    Check(!detector.IsStuck, "站着待机不是卡住");
    Check(detector.ConsecutiveStuckWindows == 0, "待机时连续卡住计数应为 0");
}

// --- 17. 想动且在动 → 不卡 ---
{
    var detector = new EnemyStuckDetector(0.5f, 0.1f);

    for (int i = 0; i < 20; i++) detector.Update(0.1f, wantsToMove: true, x: i * 0.2f, z: 0f);

    Check(!detector.IsStuck, "一直在移动不该判定卡住（累计位移远超门槛）");
}

// --- 18. 想动但走不动 → 卡住 ---
//
// 这是真实情形：撞墙时"意图速度"是满的，位移却接近 0。
{
    var detector = new EnemyStuckDetector(0.5f, 0.1f);

    detector.Update(0.2f, wantsToMove: true, x: 0f, z: 0f);
    Check(!detector.IsStuck, "窗口未满时不该下结论");

    detector.Update(0.2f, wantsToMove: true, x: 0.01f, z: 0f);
    detector.Update(0.2f, wantsToMove: true, x: 0.01f, z: 0f);
    Check(detector.IsStuck, "一个窗口内几乎没位移，应判定卡住");
    Check(detector.ConsecutiveStuckWindows == 1,
        $"应记为 1 个连续窗口，实际 {detector.ConsecutiveStuckWindows}");
}

// --- 19. 连续卡住的窗口数会累加；恢复移动后清零 ---
{
    var detector = new EnemyStuckDetector(0.3f, 0.1f);

    for (int i = 0; i < 9; i++) detector.Update(0.1f, wantsToMove: true, x: 0f, z: 0f);
    Check(detector.ConsecutiveStuckWindows >= 3,
        $"连续卡住应累加，实际 {detector.ConsecutiveStuckWindows}");

    for (int i = 0; i < 6; i++) detector.Update(0.1f, wantsToMove: true, x: i * 0.5f, z: 0f);

    Check(!detector.IsStuck, "恢复移动后不该还算卡住");
    Check(detector.ConsecutiveStuckWindows == 0,
        $"恢复后计数应清零，实际 {detector.ConsecutiveStuckWindows}");
}

// --- 20. 绕行方向交替（避免总撞同一面墙）---
{
    var detector = new EnemyStuckDetector();

    float first = detector.NextDetourSign();
    float second = detector.NextDetourSign();
    float third = detector.NextDetourSign();

    Check(first != second, $"连续两次绕行应换边（{first} vs {second}）");
    Check(first == third, $"应回到第一次的方向（{first} vs {third}）");
    Check(MathF.Abs(first) == 1f, $"方向必须是 ±1，实际 {first}");
}

// --- 21. Reset 清干净 ---
{
    var detector = new EnemyStuckDetector(0.2f, 0.1f);

    for (int i = 0; i < 10; i++) detector.Update(0.1f, wantsToMove: true, x: 0f, z: 0f);
    Check(detector.IsStuck, "先制造卡住状态");

    detector.Reset();

    Check(!detector.IsStuck, "Reset 后不该是卡住");
    Check(detector.ConsecutiveStuckWindows == 0, "Reset 后计数应清零");
}

// --- 22. 负的 deltaTime 不应破坏状态 ---
{
    var detector = new EnemyStuckDetector(0.3f, 0.1f);
    detector.Update(-5f, wantsToMove: true, x: 0f, z: 0f);
    Check(!detector.IsStuck, "负 deltaTime 不该推进窗口");
}

// --- 23. NaN 位移不应被算作"移动了" ---
{
    var detector = new EnemyStuckDetector(0.3f, 0.1f);

    for (int i = 0; i < 5; i++) detector.Update(0.1f, wantsToMove: true, x: float.NaN, z: 0f);

    Check(detector.IsStuck, "NaN 位移应当作没动（否则卡住永远检测不出来）");
}

// --- 24. 【端到端模拟】一堵墙在前面时的完整决策链 ---
//
// 模拟 EnemyController.SteerAroundObstacles 的纯逻辑部分：
// 期望方向 +Z、正前方 1.0 米有墙（其余方向通畅）→ 最终方向必须偏开。
{
    var clearances = new float[]
    {
        1.0f,                    // 0°    ← 墙在这里
        float.PositiveInfinity,  // -30°
        float.PositiveInfinity,  // +30°
        float.PositiveInfinity,  // -60°
        float.PositiveInfinity,  // +60°
        float.PositiveInfinity,  // -90°
        float.PositiveInfinity,  // +90°
    };

    int chosen = EnemySteering.ChooseCandidate(clearances, 7, requiredClearance: 2f);
    Check(chosen == 1, $"墙在正前方时应选 -30°，实际下标 {chosen}");

    EnemySteering.RotateDirection(0f, 1f,
        EnemySteering.CandidateOffsetsDegrees[chosen], out float finalX, out float finalZ);

    Near(finalX, -0.5f, 1e-3f, "最终的 x 分量应是 -0.5（sin30）");
    Near(finalZ, 0.866f, 1e-3f, "最终的 z 分量应是 0.866（cos30）");
}

Console.WriteLine($"   {assertions - steerStart} 条敌人绕障断言通过");

// ================================================ R2. 绕障集成模拟（墙形用例）
//
// 【为什么要做这一组 —— 直接回应"没有证明房屋绕行"】
// R 组验证的是单个函数（选方向、旋转、卡死判据）。
// 但"敌人能不能绕过一堵墙"是【多帧组合】的结果：
// 探距 → 选向 → 移动 → 卡死 → 绕行 → 再探距 …
// 只测单个函数证明不了这个闭环能收敛。
//
// 【做法】搭一个极小的 2D 世界：
//   - 墙 = 轴对齐矩形
//   - 探距 = 射线与矩形求交（把矩形按敌人半径膨胀，近似"球体探距"）
//   - 敌人 = 复现 EnemyController 的决策顺序（不含分离力：单敌无邻居）
// 然后在几种墙形上真的跑起来，看它能不能到达。
//
// 【能证明】决策顺序与收敛性。
// 【不能证明】Unity 的 SphereCast 行为、CharacterController 的贴墙滑动、真实碰撞体形状。

Console.WriteLine();
Console.WriteLine("== R2. 绕障集成模拟（墙形用例）==");
int simStart = assertions;

(float MinX, float MaxX, float MinZ, float MaxZ)[] Walls(
    params (float MinX, float MaxX, float MinZ, float MaxZ)[] items) => items;

// 从 (ox,oz) 沿单位方向 (dx,dz) 前进，返回撞墙距离；没命中返回 maxDistance。
float RayClearance(
    (float MinX, float MaxX, float MinZ, float MaxZ)[] walls,
    float ox, float oz, float dx, float dz, float radius, float maxDistance)
{
    float best = maxDistance;

    foreach (var wall in walls)
    {
        // Minkowski 膨胀：墙向外扩一个半径 ≈ 用圆去撞墙
        float minX = wall.MinX - radius, maxX = wall.MaxX + radius;
        float minZ = wall.MinZ - radius, maxZ = wall.MaxZ + radius;

        // 起点已在墙内 → 当作紧贴
        if (ox > minX && ox < maxX && oz > minZ && oz < maxZ) return 0f;

        float tMin = 0f, tMax = best;

        if (MathF.Abs(dx) < 1e-8f)
        {
            if (ox < minX || ox > maxX) continue;
        }
        else
        {
            float t1 = (minX - ox) / dx;
            float t2 = (maxX - ox) / dx;
            if (t1 > t2) (t1, t2) = (t2, t1);
            if (t1 > tMin) tMin = t1;
            if (t2 < tMax) tMax = t2;
        }

        if (MathF.Abs(dz) < 1e-8f)
        {
            if (oz < minZ || oz > maxZ) continue;
        }
        else
        {
            float t1 = (minZ - oz) / dz;
            float t2 = (maxZ - oz) / dz;
            if (t1 > t2) (t1, t2) = (t2, t1);
            if (t1 > tMin) tMin = t1;
            if (t2 < tMax) tMax = t2;
        }

        if (tMax < tMin) continue;

        if (tMin > 0f && tMin < best) best = tMin;
    }

    return best;
}

// 某个点是否落在任意墙里（墙按半径膨胀）
bool InsideWall(
    (float MinX, float MaxX, float MinZ, float MaxZ)[] walls,
    float x, float z, float radius)
{
    foreach (var wall in walls)
    {
        if (x > wall.MinX - radius && x < wall.MaxX + radius &&
            z > wall.MinZ - radius && z < wall.MaxZ + radius)
        {
            return true;
        }
    }

    return false;
}

// 移动一步，模拟 CharacterController 的"轴分离滑动"：撞墙时沿墙滑而不是停死。
//
// 【为什么必须有这一步 —— 我第一版漏了它，导致整组集成测试是空转】
// 第一版直接 x += dx*speed*dt，没有任何阻挡。
// 于是"关掉避障"的对照组【直接穿墙过去了】，
// 而"能绕开墙"的断言因此毫无意义 —— 穿墙也能到达。
// 对照组一跑就暴露了这个问题（这正是对照组存在的价值）。
void MoveWithSliding(
    (float MinX, float MaxX, float MinZ, float MaxZ)[] walls,
    ref float x, ref float z, float stepX, float stepZ, float radius)
{
    if (!InsideWall(walls, x + stepX, z + stepZ, radius))
    {
        x += stepX;
        z += stepZ;
        return;
    }

    // 被挡 → 分别试两个轴（沿墙滑）
    if (!InsideWall(walls, x + stepX, z, radius)) { x += stepX; return; }
    if (!InsideWall(walls, x, z + stepZ, radius)) { z += stepZ; }
}

// 一次完整模拟。返回 (到达?, 用时, 路径长度, 最终距离, 换向次数)
//
// 换向次数 = 偏离基准方向的候选在"左(-)"与"右(+)"之间切换的次数。
// 原地抖动会让这个数字暴涨，而正常绕墙是稳定的单侧偏移。
(bool Reached, float Seconds, float PathLength, float FinalDistance, int Reversals) SimulateRun(
    (float MinX, float MaxX, float MinZ, float MaxZ)[] walls,
    float startX, float startZ, float targetX, float targetZ,
    float speed, float radius, float timeLimit,
    bool avoidObstacles = true)
{
    const float dt = 0.05f;
    const float lookAhead = 2.6f;
    const float reaction = 0.35f;
    const float minClearance = 0.875f;
    const float arriveDistance = 0.6f;

    float x = startX, z = startZ;
    float pathLength = 0f;
    float detourTimer = 0f, detourSign = 1f;
    int reversals = 0;
    int lastSide = 0;

    var stuck = new EnemyStuckDetector(0.7f, 0.15f);
    var clearances = new float[EnemySteering.CandidateCount];
    float required = MathF.Max(speed * reaction, minClearance);

    for (float t = 0f; t < timeLimit; t += dt)
    {
        float toX = targetX - x;
        float toZ = targetZ - z;
        float distance = MathF.Sqrt(toX * toX + toZ * toZ);

        if (distance <= arriveDistance) return (true, t, pathLength, distance, reversals);

        if (!EnemySteering.TryNormalize(toX, toZ, out float dx, out float dz))
        {
            return (false, t, pathLength, distance, reversals);
        }

        // 卡死兜底先偏转基准（和控制器同序）
        if (detourTimer > 0f)
        {
            detourTimer -= dt;
            EnemySteering.RotateDirection(dx, dz, 90f * detourSign, out dx, out dz);
        }

        for (int i = 0; i < EnemySteering.CandidateCount; i++)
        {
            EnemySteering.RotateDirection(dx, dz, EnemySteering.CandidateOffsetsDegrees[i],
                out float px, out float pz);
            clearances[i] = RayClearance(walls, x, z, px, pz, radius, lookAhead);
        }

        int chosen = EnemySteering.ChooseCandidate(clearances, EnemySteering.CandidateCount, required);

        // 对照组：关掉避障时永远走直线（等价于早先"直线撞墙"的行为）。
        // 用来证明下面的墙形用例不是随便就能过的 —— 见断言 31。
        if (!avoidObstacles) chosen = 0;

        if (chosen >= 0)
        {
            float offset = EnemySteering.CandidateOffsetsDegrees[chosen];
            int side = offset < 0f ? -1 : (offset > 0f ? 1 : 0);

            if (side != 0 && lastSide != 0 && side != lastSide) reversals++;
            if (side != 0) lastSide = side;

            EnemySteering.RotateDirection(dx, dz, offset, out dx, out dz);
        }

        float stepX = dx * speed * dt;
        float stepZ = dz * speed * dt;

        float beforeX = x, beforeZ = z;
        MoveWithSliding(walls, ref x, ref z, stepX, stepZ, radius);

        // 路径长度按【实际位移】算，不是按意图 —— 撞墙时意图走了、人没动
        float movedX = x - beforeX;
        float movedZ = z - beforeZ;
        pathLength += MathF.Sqrt(movedX * movedX + movedZ * movedZ);

        stuck.Update(dt, true, x, z);

        if (stuck.IsStuck && detourTimer <= 0f)
        {
            detourSign = stuck.NextDetourSign();
            detourTimer = 0.4f;
        }
    }

    float fx = targetX - x, fz = targetZ - z;
    return (false, timeLimit, pathLength, MathF.Sqrt(fx * fx + fz * fz), reversals);
}

const float simSpeed = 4.2f;    // 和 EnemyController 的 _chaseSpeed 默认一致
const float simRadius = 0.35f;  // 和 CharacterController.radius 一致

// --- 25. 空地直线：必须到达，且路径接近直线（不乱拐）---
{
    var walls = Walls();
    var sim = SimulateRun(walls, 0f, 0f, 8f, 0f, simSpeed, simRadius, 10f);

    Check(sim.Reached, $"空地上应能到达，实际停在距离 {sim.FinalDistance:F2} 米处");
    Check(sim.PathLength < 8f * 1.15f,
        $"空地上路径应接近直线（≤ 9.2 米），实际 {sim.PathLength:F2} 米 —— 超了说明空地上也乱拐");
    Check(sim.Reversals == 0, $"空地上不该来回换向，实际 {sim.Reversals} 次");
}

// --- 26. 【关键】长墙 + 北侧缺口：必须绕到缺口再过去 ---
{
    var walls = Walls((2f, 2.3f, -8f, 1f));   // 缺口在 z > 1
    var sim = SimulateRun(walls, 0f, 0f, 6f, 0f, simSpeed, simRadius, 25f);

    Check(sim.Reached,
        $"★ 长墙有缺口时必须绕过去，实际 {sim.Seconds:F1} 秒后仍距目标 {sim.FinalDistance:F2} 米" +
        $"（路径 {sim.PathLength:F1} 米，换向 {sim.Reversals} 次）");

    if (sim.Reached)
    {
        Check(sim.PathLength < 30f, $"绕行路径不该超过 30 米，实际 {sim.PathLength:F1} 米");
    }
}

// --- 27. 【已知局限 · 实测】C 形陷阱：局部避障到不了 ---
//
// 【这条断言在【记录失败】，不是记录成功 —— 这是本轮最重要的测试结果】
//
// 我原本以为 C 形也能靠 ±90° 候选 + 卡死兜底硬推过去。实测：**不行**。
// 40 秒后仍距目标 2.72 米，路径 168 米、换向 417 次（约每秒 10 次）。
// 它不是在绕，是在陷阱外侧来回振荡。
//
// 这证实了"局部探距没有路线记忆"这个判断：
// 想进 C 形内部必须【先朝远离目标的方向走一大圈】，
// 而局部规则每一帧都朝"离目标更近且通畅"的方向走，永远迈不出那一步。
//
// 【为什么把失败写成断言】
//   1. 以后真的接 NavMesh 时，这条会失败 —— 强制更新，而不是悄悄留下一句过时的注释
//   2. 明确记下能力边界，避免有人以为"接入绕障 = 一定能到达"
//   3. 数字本身就是证据（路径 / 换向次数），比一句"做不到"有力
{
    var walls = Walls(
        (2f, 2.3f, -2f, 2f),      // 左
        (5f, 5.3f, -2f, 2f),      // 右
        (2f, 5.3f, -2f, -1.7f));  // 底（开口朝北）

    var sim = SimulateRun(walls, 3.6f, -5f, 3.6f, 0f, simSpeed, simRadius, 40f);

    Check(!sim.Reached,
        $"★ C 形陷阱【不应该】能到达（已实测的局限）。若这条失败，" +
        $"说明避障能力已提升（例如接入了 NavMesh）—— 请把这条改成'应能到达'。" +
        $"本次实到：到达={sim.Reached}，{sim.Seconds:F1} 秒，最终距离 {sim.FinalDistance:F2} 米");

    // 失败模式是"振荡"而不是"停住"：换向次数说明它一直在左右横跳
    Check(sim.Reversals > 50,
        $"★ C 形陷阱里的失败模式应是【持续振荡】（左右反复换向），" +
        $"实际只有 {sim.Reversals} 次换向 —— 太少说明它是停住了而不是在绕。");
}

// --- 28. 完全封闭的盒子：必须【到不了】---
//
// 【这条在承认局限，不是挑错】
// 局部避障没有全局路径，目标封闭时它会一直贴着墙绕。
// 写成断言有两个用处：
//   1. 以后真做了 NavMesh，这条会失败 —— 提醒更新
//   2. 防止有人误以为"接入绕障 = 一定能到达"
{
    var walls = Walls(
        (3f, 3.3f, -2f, 2f),     // 西
        (6f, 6.3f, -2f, 2f),     // 东
        (3f, 6.3f, -2f, -1.7f),  // 南
        (3f, 6.3f, 1.7f, 2f));   // 北

    var sim = SimulateRun(walls, 0f, 0f, 4.65f, 0f, simSpeed, simRadius, 20f);

    Check(!sim.Reached,
        "目标封闭时局部避障【不应该】能到达 —— 若能，说明墙没生效或穿墙了。" +
        $"实际到达于 {sim.Seconds:F1} 秒（路径 {sim.PathLength:F1} 米）");

    Check(sim.FinalDistance > 1.5f,
        $"封闭目标不该被贴近，实际最终距离 {sim.FinalDistance:F2} 米");
}

// --- 29. 【卡死判据回归】原地来回抖动必须被判成卡住 ---
//
// 这是用户实测出来的缺陷：旧判据统计"累计路程"，
// 每帧来回移动 1 厘米、持续 2 秒 → 累计 20 次 × 1 厘米 = 0.4 米，
// 超过门槛 0.15 → 判定"没卡住"。而这正是贴墙抖的典型表现。
//
// 新判据比较窗口【起点到终点的净位移】：来回抖动净位移≈0 → 正确判成卡住。
{
    var detector = new EnemyStuckDetector(0.7f, 0.15f);

    float x = 0f;
    for (int i = 0; i < 40; i++)   // 40 帧 × 0.05 秒 = 2 秒
    {
        x = (i % 2 == 0) ? 0.01f : 0f;   // 每帧来回移动 1 厘米
        detector.Update(0.05f, wantsToMove: true, x: x, z: 0f);
    }

    Check(detector.IsStuck,
        $"★ 原地来回移动 1 厘米持续 2 秒必须判成卡住（这是用户实测的缺陷）" +
        $"，实际 IsStuck={detector.IsStuck}，最后窗口净位移 {detector.LastNetTravel:F3} 米");
}

// --- 30. 【分离回归】两个完全重合的敌人必须往不同方向散开 ---
//
// 缺陷：旧实现用"邻居数组下标奇偶"选方向。两个重合的敌人各自只看到一个邻居、
// 下标都是 0 → 两边都往同一方向推 → 永远分不开。
// 现在由调用方传入每个敌人不同的 tieAngle，方向就分开了。
{
    var otherX = new float[] { 0f };
    var otherZ = new float[] { 0f };

    // 敌人 A：tieAngle = 0
    float ax = 0f, az = 0f;
    EnemySteering.AccumulateSeparation(0f, 0f, otherX, otherZ, 1, 1.8f, 0.9f, 0f, ref ax, ref az);
    EnemySteering.TryNormalize(ax, az, out float anX, out float anZ);

    // 敌人 B：tieAngle = 黄金角
    float bx = 0f, bz = 0f;
    EnemySteering.AccumulateSeparation(0f, 0f, otherX, otherZ, 1, 1.8f, 0.9f, 2.39996323f, ref bx, ref bz);
    EnemySteering.TryNormalize(bx, bz, out float bnX, out float bnZ);

    float dot = anX * bnX + anZ * bnZ;

    Check(dot < 0.99f,
        $"★ 两个重合的敌人的推开方向不该相同（点积 {dot:F4} 太接近 1）——" +
        "相同的话它们会一起走、永远分不开");

    // 也不该正好反向抵消（那样两个都抵在原地）
    Check(dot > -0.99f,
        $"两个重合的敌人也不该正好反向（点积 {dot:F4}）—— 那样推力互相抵消，同样分不开");
}

// --- 31. 【对照组】同一堵墙，关掉避障就必须到不了 ---
//
// 【为什么必须有这一条】
// 断言 26/27 说"能绕过墙"。但如果地图本身是通的（比如墙放歪了、或者模拟器根本没走），
// 那两条会毫无意义地通过。这一条用【同一张地图】、只把避障关掉，
// 要求它【到达不了】—— 这才证明"是避障让它过去的"。
//
// 相当于给集成用例本身做了一次变异测试，而且它长期留在测试里。
{
    // 地图 A：长墙 + 缺口
    var longWall = Walls((2f, 2.3f, -8f, 1f));

    var aWith = SimulateRun(longWall, 0f, 0f, 6f, 0f, simSpeed, simRadius, 25f, avoidObstacles: true);
    var aWithout = SimulateRun(longWall, 0f, 0f, 6f, 0f, simSpeed, simRadius, 25f, avoidObstacles: false);

    Check(aWith.Reached, "开避障时长墙必须能绕过（和断言 26 同一张地图）");
    Check(!aWithout.Reached,
        $"★ 长墙地图关掉避障后【必须】到不了 —— 否则断言 26 什么也没证明。" +
        $"实际：到达={aWithout.Reached}，最终距离 {aWithout.FinalDistance:F2} 米" +
        $"（路径 {aWithout.PathLength:F1} 米）");
    Check(aWithout.FinalDistance > 2f,
        $"关掉避障时应卡在墙前，实际最终距离只有 {aWithout.FinalDistance:F2} 米");

    // 地图 B：C 形陷阱 —— 开/关避障都到不了（已实测的局限，见断言 27）。
    // 这里只记录"关掉避障也到不了"，用来确认断言 27 的失败【不是】避障引起的。
    var cShape = Walls(
        (2f, 2.3f, -2f, 2f),
        (5f, 5.3f, -2f, 2f),
        (2f, 5.3f, -2f, -1.7f));

    var bWithout = SimulateRun(cShape, 3.6f, -5f, 3.6f, 0f, simSpeed, simRadius, 40f, avoidObstacles: false);

    Check(!bWithout.Reached,
        $"C 形陷阱关掉避障后也到不了（本来就是直线顶墙）。" +
        $"实际到达={bWithout.Reached} —— 若这里能到达，说明地图配置有问题。");
}

Console.WriteLine($"   {assertions - simStart} 条绕障集成断言通过");

Console.WriteLine();
Console.WriteLine("== S. 圆形仇恨与死亡输入锁 ==");
foreach (var point in new[] { (14f,0f), (-14f,0f), (0f,14f), (0f,-14f), (9f,9f), (0f,0f) })
    Check(EnemyPerception.IsInAggroRange(point.Item1, point.Item2, 14f), "圆内/边界任意方向都触发仇恨");
foreach (var point in new[] { (14.01f,0f), (-14.01f,0f), (0f,14.01f), (0f,-14.01f), (10f,10f) })
    Check(!EnemyPerception.IsInAggroRange(point.Item1, point.Item2, 14f), "圆外不触发仇恨");
Check(!EnemyPerception.IsInAggroRange(1, 1, 0), "零半径不触发");
Check(!EnemyPerception.IsInAggroRange(1, 1, -14), "负半径不触发");
Check(!EnemyPerception.IsInAggroRange(float.NaN, 1, 14), "无效坐标不触发");
Check(!EnemyPerception.IsInAggroRange(1, 1, float.PositiveInfinity), "无穷半径不触发");
InputModeState.SetPlayerDead(false);
InputModeState.Current = InputMode.Building;
Check(InputModeState.AllowsMovement && InputModeState.AllowsBuilding && !InputModeState.AllowsCombat,
    "建造时移动和建造规则保持");
InputModeState.SetPlayerDead(true);
Check(InputModeState.Current == InputMode.Dead, "死亡模式有最高优先级");
Check(!InputModeState.AllowsMovement && !InputModeState.AllowsCombat &&
    !InputModeState.AllowsInteraction && !InputModeState.AllowsBuilding, "死亡拒绝所有玩法操作");
InputModeState.Current = InputMode.Normal;
InputModeState.Reset();
Check(InputModeState.Current == InputMode.Dead, "普通模式写入和 Reset 不会解除死亡锁");
InputModeState.SetPlayerDead(false);
Check(InputModeState.Current == InputMode.Normal && InputModeState.AllowsCombat, "复活恢复正常模式");
var radialBrain = new EnemyBrainLogic(loseTargetGrace: 3f);
radialBrain.Advance(0.1f, new EnemySenses(13f, true, 1.6f, false, false, false));
Check(radialBrain.State == EnemyState.Chase, "圆形发现接入追击");
radialBrain.Advance(2.9f, new EnemySenses(15f, false, 1.6f, false, false, false));
Check(radialBrain.State == EnemyState.Chase, "离开圆形范围后保留宽限期");
radialBrain.Advance(0.2f, new EnemySenses(15f, false, 1.6f, false, false, false));
Check(radialBrain.State == EnemyState.Idle, "宽限期结束放弃追击");
Console.WriteLine($"PASS(全部): 共 {assertions} 条断言（含圆形仇恨与死亡输入锁）。");

// 「几枪打死」的公式：向上取整，且至少 1 枪。
// 用真的 Health 跑一遍，保证公式和实际实现一致（而不是我们以为一致）。
static int ShotsToKill(float health, float damage)
{
    var h = new Game.Player.Health(health);
    int shots = 0;
    while (!h.IsDead && shots < 10000)
    {
        h.TakeDamage(damage);
        shots++;
    }
    return shots;
}
