using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Reusable count-up + per-digit "pop" math, shared by the in-game score (ScreenGameplay.UpdateScoreDigits)
    /// and the result-screen EXP / G幣 totals (<see cref="RollingDigits"/>). Mirrors the decompiled CtlNumLabel
    /// roll (step = delta/20 applied once every ~50ms, snap to target at 999ms — i.e. ~20 discrete updates/s,
    /// so digits don't blur at 60Hz) and the score digit bounce (scale 1.0→1.3→1.0, eased, over the whole roll).
    /// Pure helpers (the only Unity dependency is Mathf.SmoothStep for the ease).
    /// </summary>
    public static class RollingNumber
    {
        public const float RollMs = 999f;
        public const float RollSec = RollMs / 1000f;

        /// <summary>Value shown at <paramref name="now"/> (Time.time secs) while rolling from
        /// <paramref name="from"/> to <paramref name="target"/> since <paramref name="animAt"/>.</summary>
        public static long ValueAt(long from, long target, float animAt, float now)
        {
            float ms = (now - animAt) * 1000f;
            if (ms >= RollMs) return target;
            long step = (target - from) / 20;            // 0x21c = (target - cur) / 0x14
            long ticks = (long)(ms / 50f);               // one step per ~50ms (0x32)
            return from + step * ticks;
        }

        public static bool IsRolling(float animAt, float now) => (now - animAt) * 1000f < RollMs;

        /// <summary>Per-digit pop scale (1.0→1.3→1.0, ease in/out) over the roll; <paramref name="t"/> = secs
        /// since the digit (re)appeared. Returns 1 outside the window.</summary>
        public static float Bounce(float t)
        {
            if (t < 0f || t >= RollSec) return 1f;
            float u = t / RollSec;
            float tri = u < 0.5f ? u * 2f : (1f - u) * 2f;        // 0→1→0
            return 1f + 0.3f * Mathf.SmoothStep(0f, 1f, tri);
        }
    }
}
