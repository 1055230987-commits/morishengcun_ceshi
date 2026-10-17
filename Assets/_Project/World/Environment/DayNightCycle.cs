using System;

namespace Game.Environment
{
    /// <summary>原型的全天轨道与暮光权重。方向从观察者指向太阳；月亮取反向。</summary>
    public static class DayNightCycle
    {
        public const float SunriseHour = 5f;
        public const float SunsetHour = 19f;
        public const float DefaultMaximumElevation = 65f;

        public readonly struct State
        {
            public readonly float X, Y, Z, Elevation, NightAmount, TwilightAmount;

            public State(float x, float y, float z, float elevation, float night, float twilight)
            {
                X = x; Y = y; Z = z;
                Elevation = elevation; NightAmount = night; TwilightAmount = twilight;
            }
        }

        public static State Evaluate(float hour, float maximumElevation = DefaultMaximumElevation)
        {
            if (float.IsNaN(hour) || float.IsInfinity(hour)) hour = 12f;
            if (float.IsNaN(maximumElevation) || float.IsInfinity(maximumElevation))
                maximumElevation = DefaultMaximumElevation;
            double h = ((double)hour % 24.0 + 24.0) % 24.0;
            double angle;
            if (h >= SunriseHour && h <= SunsetHour)
                angle = Math.PI * (h - SunriseHour) / (SunsetHour - SunriseHour);
            else
            {
                double elapsed = h < SunriseHour ? h + 24.0 - SunsetHour : h - SunsetHour;
                angle = Math.PI + Math.PI * elapsed / (24.0 - SunsetHour + SunriseHour);
            }
            double tilt = Math.Max(5.0, Math.Min(85.0, maximumElevation)) * Math.PI / 180.0;
            float x = (float)Math.Cos(angle);
            float y = (float)(Math.Sin(angle) * Math.Sin(tilt));
            float z = (float)(Math.Sin(angle) * Math.Cos(tilt));
            float elevation = (float)(Math.Asin(Math.Max(-1.0, Math.Min(1.0, y))) * 180.0 / Math.PI);
            float night = 1f - Smooth01((elevation + 8f) / 14f);
            float twilight = (1f - Smooth01(Math.Abs(elevation) / 12f)) * (1f - night);
            return new State(x, y, z, elevation, night, twilight);
        }

        private static float Smooth01(float value)
        {
            value = Math.Max(0f, Math.Min(1f, value));
            return value * value * (3f - 2f * value);
        }
    }
}
