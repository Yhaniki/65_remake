using System;
using System.Collections.Generic;

namespace Sdo.Osu
{
    /// <summary>
    /// 譜面「拍 ↔ 毫秒」互轉與格線列舉（純邏輯，無引擎相依 — 見 BeatGridTests）。
    ///
    /// .gn 是 4/4：一個 measurement = 4 拍（GnChart: <c>beat = measurement*4 + 4*slot/interval</c>），所以小節線
    /// 就是每 4 拍一條。變速（type-1）在 <see cref="OsuBeatmap.TimingPoints"/> 裡是一段一段的 uninherited 點
    /// （<c>TimeMs</c> = 該段起點毫秒、<c>BeatLength</c> = 該段一拍幾毫秒 = GnChart.BuildBpmTimeline 的輸出），
    /// 這裡把它們接回一條分段線性的時間軸，讓編輯器畫的小節/拍線在變速歌上也跟音符對得起來。
    /// </summary>
    public sealed class BeatGrid
    {
        /// <summary>.gn 固定 4/4：一小節 4 拍。</summary>
        public const int BeatsPerMeasure = 4;

        private readonly double[] _segMs;     // 段起點（毫秒）
        private readonly double[] _segBeat;   // 段起點（拍）
        private readonly double[] _beatLen;   // 該段一拍幾毫秒

        /// <summary>格線種類：小節線 / 拍線 / 細分線。</summary>
        public enum LineKind { Measure, Beat, Sub }

        public readonly struct Line
        {
            public readonly double Ms;
            public readonly double Beat;
            public readonly LineKind Kind;
            public Line(double ms, double beat, LineKind kind) { Ms = ms; Beat = beat; Kind = kind; }
        }

        public int SegmentCount => _segMs.Length;

        /// <summary>由 <see cref="OsuBeatmap"/> 建格線（沒有 timing point 時退回 <see cref="OsuBeatmap.Bpm"/>）。</summary>
        public static BeatGrid From(OsuBeatmap map)
            => new BeatGrid(map?.TimingPoints, map != null && map.Bpm > 0 ? map.Bpm : 120.0);

        public BeatGrid(IReadOnlyList<OsuTimingPoint> points, double fallbackBpm = 120.0)
        {
            var ms = new List<double>();
            var len = new List<double>();
            if (points != null)
            {
                foreach (var p in points)
                {
                    if (!p.Uninherited) continue;                       // inherited(SV) 點不動時間軸
                    if (ms.Count > 0 && p.TimeMs <= ms[ms.Count - 1])   // 同/早於前一段起點 → 後者覆蓋，不新增段
                    { len[len.Count - 1] = p.BeatLength; continue; }
                    ms.Add(p.TimeMs);
                    len.Add(p.BeatLength);
                }
            }
            if (ms.Count == 0) { ms.Add(0.0); len.Add(60000.0 / Math.Max(1.0, fallbackBpm)); }
            if (ms[0] > 0.0) { ms.Insert(0, 0.0); len.Insert(0, len[0]); }   // 補上從 0 起的首段（.osu 可能沒有）

            _segMs = ms.ToArray();
            _beatLen = len.ToArray();
            _segBeat = new double[_segMs.Length];
            _segBeat[0] = 0.0;
            for (int s = 1; s < _segMs.Length; s++)
                _segBeat[s] = _segBeat[s - 1] + (_segMs[s] - _segMs[s - 1]) / _beatLen[s - 1];
        }

        private int SegAtMs(double ms)
        {
            int lo = 0, hi = _segMs.Length - 1, s = 0;
            while (lo <= hi) { int mid = (lo + hi) >> 1; if (_segMs[mid] <= ms) { s = mid; lo = mid + 1; } else hi = mid - 1; }
            return s;
        }

        private int SegAtBeat(double beat)
        {
            int lo = 0, hi = _segBeat.Length - 1, s = 0;
            while (lo <= hi) { int mid = (lo + hi) >> 1; if (_segBeat[mid] <= beat) { s = mid; lo = mid + 1; } else hi = mid - 1; }
            return s;
        }

        public double BeatToMs(double beat)
        {
            int s = SegAtBeat(beat);
            return _segMs[s] + (beat - _segBeat[s]) * _beatLen[s];
        }

        public double MsToBeat(double ms)
        {
            int s = SegAtMs(ms);
            return _segBeat[s] + (ms - _segMs[s]) / _beatLen[s];
        }

        /// <summary>該毫秒位置的 BPM（分段常數）。</summary>
        public double BpmAt(double ms) => 60000.0 / _beatLen[SegAtMs(ms)];

        /// <summary>小節 m 的起點毫秒。</summary>
        public double MeasureStartMs(int measure) => BeatToMs(measure * (double)BeatsPerMeasure);

        /// <summary>該毫秒落在第幾小節（0 起算；負時間回 0 之前的負小節）。</summary>
        public int MeasureAt(double ms) => (int)Math.Floor(MsToBeat(ms) / BeatsPerMeasure);

        /// <summary>
        /// 列舉 [msFrom, msTo] 內的格線。<paramref name="divisionsPerBeat"/> = 每拍細分數（1 = 只有拍線、4 = 16 分…）。
        /// 只吐 beat ≥ 0 的線（譜面沒有負拍）。<paramref name="maxLines"/> 是防呆上限：真的超過就提早收工，
        /// 不會讓縮放到極慢的畫面把 CPU 吃光。
        /// </summary>
        public void LinesInWindow(double msFrom, double msTo, int divisionsPerBeat, List<Line> outLines, int maxLines = 4096)
        {
            if (outLines == null) return;
            outLines.Clear();
            if (msTo < msFrom) { var t = msFrom; msFrom = msTo; msTo = t; }
            int div = Math.Max(1, divisionsPerBeat);

            double beatFrom = MsToBeat(msFrom), beatTo = MsToBeat(msTo);
            long i0 = (long)Math.Floor(beatFrom * div);
            long i1 = (long)Math.Ceiling(beatTo * div);
            if (i0 < 0) i0 = 0;
            if (i1 < i0) return;
            if (i1 - i0 > maxLines) i1 = i0 + maxLines;

            int perMeasure = div * BeatsPerMeasure;
            for (long i = i0; i <= i1; i++)
            {
                double beat = i / (double)div;
                double ms = BeatToMs(beat);
                if (ms < msFrom || ms > msTo) continue;
                LineKind kind = (i % perMeasure == 0) ? LineKind.Measure
                              : (i % div == 0) ? LineKind.Beat
                              : LineKind.Sub;
                outLines.Add(new Line(ms, beat, kind));
            }
        }
    }
}
