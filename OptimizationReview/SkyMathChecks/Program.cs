using Game.Environment;

int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new Exception(message);
}
void Near(float a, float b, float tolerance, string message) =>
    Check(Math.Abs(a - b) <= tolerance, $"{message}: {a} vs {b}");

Near(DayNightCycle.Evaluate(5).Elevation, 0, 0.001f, "Sunrise");
Near(DayNightCycle.Evaluate(19).Elevation, 0, 0.001f, "Sunset");
Near(DayNightCycle.Evaluate(12).Elevation, 65, 0.001f, "Noon peak");
Near(DayNightCycle.Evaluate(0).Elevation, -65, 0.001f, "Midnight below horizon");
Near(DayNightCycle.Evaluate(0).NightAmount, 1, 0.001f, "Midnight night");
Near(DayNightCycle.Evaluate(12).NightAmount, 0, 0.001f, "Noon day");
Check(DayNightCycle.Evaluate(5).X * DayNightCycle.Evaluate(19).X < 0, "Opposite sunrise/sunset directions");
for (int minute = 0; minute <= 1440; minute++)
{
    float hour = minute / 60f;
    var state = DayNightCycle.Evaluate(hour);
    Near(state.X * state.X + state.Y * state.Y + state.Z * state.Z, 1, 0.00001f, "Unit direction");
    Check(state.NightAmount >= 0 && state.NightAmount <= 1, "Night weight bounds");
    Check(state.TwilightAmount >= 0 && state.TwilightAmount <= 1, "Twilight bounds");
    Near(state.Elevation, DayNightCycle.Evaluate(hour + 24).Elevation, 0.001f, "24-hour periodicity");
    if (hour > 5 && hour < 19) Check(state.Y > 0, "Day sun above horizon");
    if (hour < 5 || hour > 19) Check(state.Y < 0, "Night moon above horizon");
}
for (int i = 0; i <= 420; i++)
{
    float offset = i / 60f;
    Near(DayNightCycle.Evaluate(12 - offset).Elevation,
        DayNightCycle.Evaluate(12 + offset).Elevation, 0.001f, "Morning/afternoon symmetry");
}
foreach (float boundary in new[] { 0f, 5f, 19f, 24f })
{
    var before = DayNightCycle.Evaluate(boundary - 0.0001f);
    var after = DayNightCycle.Evaluate(boundary + 0.0001f);
    Near(before.X, after.X, 0.0002f, "Continuous X");
    Near(before.Y, after.Y, 0.0002f, "Continuous Y");
    Near(before.Z, after.Z, 0.0002f, "Continuous Z");
    // 两次采样间隔 0.0002 小时；暮光斜率上限约 1.93/小时。
    Near(before.NightAmount, after.NightAmount, 0.00042f, "Continuous twilight");
}
foreach (float tilt in new[] { 5f, 45f, 65f, 85f })
    Near(DayNightCycle.Evaluate(12, tilt).Elevation, tilt, 0.001f, "Configurable noon height");
Check(float.IsFinite(DayNightCycle.Evaluate(float.NaN).Elevation), "Non-finite input");
Console.WriteLine($"PASS: {assertions} assertions against actual DayNightCycle.cs");
foreach (float hour in new[] { 0f, 5f, 6f, 9f, 12f, 15f, 18f, 19f, 22f, 24f })
{
    var state = DayNightCycle.Evaluate(hour);
    Console.WriteLine($"{hour,5:0.00}h  sun={state.Elevation,7:0.00}deg  night={state.NightAmount:0.000}");
}
