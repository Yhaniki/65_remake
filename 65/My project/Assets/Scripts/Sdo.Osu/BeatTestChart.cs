using System;

namespace Sdo.Osu
{
    /// <summary>
    /// 打拍測試用的合成譜：沒有音樂、沒有 .gn，只有固定 BPM 的等距音符（預設 4 分音符、全部落在同一軌），
    /// 讓人邊聽節拍音（assist tick 每顆音符響一聲）邊打，用來校 global offset / 判定線位置。
    ///
    /// 純邏輯、無引擎相依（見 BeatTestChartTests）。
    /// </summary>
    public static class BeatTestChart
    {
        /// <summary>右軌（LUDR 的 R）。GnChart 的 lane 對應：0=Left 1=Down 2=Up 3=Right。</summary>
        public const int RightLane = 3;

        /// <summary>第一顆音符至少離開場多久（毫秒）——不然一按播放音符就已經在受擊線上了。會對齊到拍。</summary>
        public const double LeadInMs = 3000.0;

        /// <summary>
        /// 產生 <paramref name="durationSec"/> 秒的等距音符。
        /// <paramref name="beatsPerNote"/>=1 → 4 分音符（每拍一顆）；0.5 → 8 分音符。
        /// </summary>
        public static OsuBeatmap Build(double bpm, double durationSec = 600.0, int lane = RightLane, double beatsPerNote = 1.0)
        {
            bpm = Math.Max(20.0, Math.Min(400.0, bpm));
            beatsPerNote = Math.Max(0.25, beatsPerNote);
            double beatMs = 60000.0 / bpm;
            double stepMs = beatMs * beatsPerNote;

            var map = new OsuBeatmap { Keys = 4, Bpm = bpm, Level = 0 };
            map.TimingPoints.Add(new OsuTimingPoint(0.0, beatMs));

            // 起點對齊到拍（第一顆落在整拍上，節拍音才不會跟拍子錯開）
            double firstMs = Math.Ceiling(LeadInMs / stepMs) * stepMs;
            double endMs = Math.Max(firstMs + stepMs, durationSec * 1000.0);
            for (double t = firstMs; t <= endMs; t += stepMs)
                map.HitObjects.Add(new OsuHitObject(lane, (int)Math.Round(t)));

            return map;
        }
    }
}
