using System;

namespace Sdo.Osu
{
    /// <summary>
    /// Official SDO "3D note" (hiteft3D) colour rule, reproduced from the decompiled exe
    /// (sdo_stand_alone.exe.c:139485: <c>index = (tickInBar / 12) % 4</c>, family map @139639).
    /// A note's colour is the BEAT QUANTIZATION of its position — NOT its lane/direction:
    /// 192 ticks per 4/4 bar, 12 ticks per 16th → <c>tick/12</c> is the 16th slot, and <c>% 4</c>
    /// reduces to the 16th position WITHIN a beat:
    /// <list type="bullet">
    ///   <item>0 = on the beat (quarter) → <see cref="Magenta"/> (arrow has a gold core = the "yellow" look)</item>
    ///   <item>2 = the off-8th (half-beat) → <see cref="Blue"/></item>
    ///   <item>1 | 3 = the 16ths → <see cref="Green"/></item>
    /// </list>
    /// Because only <c>% 4</c> matters, only BEAT alignment is needed (not the bar origin): any beat-aligned
    /// reference works, so we phase off the active uninherited tempo point's time. .gn charts emit one
    /// uninherited point per BPM segment (on a beat), so this is exact for single- and multi-BPM charts.
    /// </summary>
    public static class NoteBeatColor
    {
        /// <summary>On-beat family — the NOTES textures (magenta arrow with a gold core).</summary>
        public const int Magenta = 0;
        /// <summary>Off-8th family — the NOTES1 textures (blue arrow).</summary>
        public const int Blue = 1;
        /// <summary>16th family — the NOTES2 textures (green arrow).</summary>
        public const int Green = 2;

        /// <summary>Colour family (0..2) for a note, given the beat length (ms per quarter) and a beat-aligned
        /// phase (ms). Mirrors <c>(round((t-phase)/(beat/4)) % 4)</c> → {0→magenta, 2→blue, 1|3→green}.</summary>
        public static int Family(double startTimeMs, double beatLenMs, double phaseMs)
        {
            if (!(beatLenMs > 0.0)) return Magenta;                       // no tempo info → treat everything as on-beat
            double sixteenth = beatLenMs / 4.0;
            int grid = (int)Math.Round((startTimeMs - phaseMs) / sixteenth, MidpointRounding.AwayFromZero);
            int q = ((grid % 4) + 4) % 4;                                 // 16th-within-beat, always 0..3 (handles negatives)
            return q == 0 ? Magenta : q == 2 ? Blue : Green;
        }

        /// <summary>Colour family for a note at <paramref name="startTimeMs"/> using the beatmap's tempo map:
        /// phases off the active uninherited timing point (or the map BPM if the chart carries none).</summary>
        public static int Family(int startTimeMs, OsuBeatmap map)
        {
            ResolveTempo(map, startTimeMs, out double beatLen, out double phase);
            return Family(startTimeMs, beatLen, phase);
        }

        /// <summary>Resolve the tempo (ms/quarter) + a beat-aligned phase active at <paramref name="timeMs"/>:
        /// the last uninherited (tempo) point at/before the note (TimingPoints are time-sorted); before the first
        /// point → that first point; no uninherited points → the map BPM (phase 0).</summary>
        internal static void ResolveTempo(OsuBeatmap map, double timeMs, out double beatLen, out double phase)
        {
            beatLen = 0.0; phase = 0.0; bool found = false;
            var tps = map.TimingPoints;
            for (int i = 0; i < tps.Count; i++)
            {
                if (!tps[i].Uninherited) continue;
                if (!found || tps[i].TimeMs <= timeMs) { beatLen = tps[i].BeatLength; phase = tps[i].TimeMs; found = true; }
                else break;                                              // first tempo point after the note → active one already captured
            }
            if (!found) { beatLen = map.Bpm > 0.0 ? 60000.0 / map.Bpm : 0.0; phase = 0.0; }
        }
    }
}
