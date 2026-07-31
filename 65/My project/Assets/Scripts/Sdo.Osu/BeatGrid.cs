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

        private readonly double[] _segMs;     // 段起點（毫秒；播放頭「到達」那一拍的時刻，停拍還沒開始）
        private readonly double[] _segBeat;   // 段起點（拍）
        private readonly double[] _segStop;   // 該起拍上定格幾毫秒（#STOPS；沒有就 0）
        private readonly double[] _beatLen;   // 停完之後一拍幾毫秒（0 = warp：拍子走、時間不走）

        /// <summary>格線種類：小節線 / 拍線 / 細分線。</summary>
        public enum LineKind { Measure, Beat, Sub }

        /// <summary>一小節 192 row（= 一拍 48 row）—— .gn 的 slot 解析度，也是所有 snap 的最小公倍數。</summary>
        public const int RowsPerMeasure = 192;
        public const int RowsPerBeat = RowsPerMeasure / BeatsPerMeasure;   // 48

        public readonly struct Line
        {
            public readonly double Ms;
            public readonly double Beat;
            public readonly LineKind Kind;
            /// <summary>這條線落在幾分音上：4/8/12/16/24/32；再細的（48/64/192…）為 0。</summary>
            public readonly int Snap;
            public Line(double ms, double beat, LineKind kind, int snap) { Ms = ms; Beat = beat; Kind = kind; Snap = snap; }
        }

        /// <summary>
        /// 這個拍點落在幾分音上（StepMania 的 note quantization）：4=正拍、8=反拍、12=三連、16、24、32；
        /// 更細的回 0。判斷方式是「一小節 192 row」裡的位置能被誰整除 —— 4分每 48 row、8分每 24、12分每 16、
        /// 16分每 12、24分每 8、32分每 6。（192 全都整除，所以用全域 row 取模跟用小節內 row 取模等價。）
        /// </summary>
        public static int SnapOf(double beat)
        {
            long row = (long)Math.Round(beat * RowsPerBeat);
            if (row < 0) row = -row;
            if (row % 48 == 0) return 4;
            if (row % 24 == 0) return 8;
            if (row % 16 == 0) return 12;
            if (row % 12 == 0) return 16;
            if (row % 8 == 0) return 24;
            if (row % 6 == 0) return 32;
            return 0;
        }

        public int SegmentCount => _segMs.Length;

        /// <summary>
        /// 由 <see cref="OsuBeatmap"/> 建格線。譜面帶了 <see cref="OsuBeatmap.GridSegments"/>（真實時間軸，
        /// StepMania 的負 BPM / #STOPS 都在裡面）就用它；否則從 <see cref="OsuBeatmap.TimingPoints"/> 反推
        /// （.gn/.osu 的老路，沒有 warp 也沒有停拍，兩者等價），沒有 timing point 時退回 <see cref="OsuBeatmap.Bpm"/>。
        /// </summary>
        public static BeatGrid From(OsuBeatmap map)
            => map != null && map.GridSegments != null && map.GridSegments.Count > 0
                ? new BeatGrid(map.GridSegments)
                : new BeatGrid(map?.TimingPoints, map != null && map.Bpm > 0 ? map.Bpm : 120.0);

        /// <summary>
        /// 直接吃「拍 ↔ 真實時間」分段（<see cref="GridSegment"/>，由 <see cref="SmChart"/> 產生）。
        /// 分段必須依拍遞增，時間也遞增（warp 是時間不動的垂直跳、停拍是拍不動的水平段）。
        /// </summary>
        public BeatGrid(IReadOnlyList<GridSegment> segments)
        {
            int n = segments != null ? segments.Count : 0;
            if (n == 0)
            {
                _segBeat = new[] { 0.0 }; _segMs = new[] { 0.0 };
                _segStop = new[] { 0.0 }; _beatLen = new[] { 500.0 };
                return;
            }
            _segBeat = new double[n]; _segMs = new double[n];
            _segStop = new double[n]; _beatLen = new double[n];
            for (int i = 0; i < n; i++)
            {
                var g = segments[i];
                _segBeat[i] = g.StartBeat; _segMs[i] = g.StartMs;
                _segStop[i] = g.StopMs > 0.0 ? g.StopMs : 0.0;
                _beatLen[i] = g.BeatLengthMs > 0.0 ? g.BeatLengthMs : 0.0;
            }
        }

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
            // 首段的起點 = 第一個 timing point 的 ms，就是「beat 0」的錨點 —— 這一定要跟音符時間同一個錨：
            // SmChart 把 beat 0 放在 −#OFFSET（負 offset → 正 ms），osu 把它放在第一個 timing point 的 time。
            // 千萬別在 ms 0 硬插一段 beat 0（舊 bug）：那會把 beat 重新編號，害負 offset 的 SM 譜（如 Hibana，
            // offset −0.041 → 首段在 +41ms）整條格線位移 |offset|、音符落不到線上；first-TP 在正 ms 的 osu 譜同理歪掉。
            // 首段之前的時間（beat < 0）本來就沒有格線要畫（LinesInWindow 只吐 beat ≥ 0），不需要補段。

            _segMs = ms.ToArray();
            _beatLen = len.ToArray();
            _segStop = new double[_segMs.Length];   // timing point 反推的路上沒有停拍可言（.gn/.osu 沒有 #STOPS）
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

        /// <summary>這一拍在畫面上的時刻。定格（#STOPS）發生在那一拍**之後**，所以站在停拍上回的是定格的起點。</summary>
        public double BeatToMs(double beat)
        {
            int s = SegAtBeat(beat);
            double ms = _segMs[s] + (beat - _segBeat[s]) * _beatLen[s];
            if (_segStop[s] > 0.0 && beat > _segBeat[s]) ms += _segStop[s];
            return ms;
        }

        /// <summary>這個時刻播放頭停在哪一拍。定格中回定格的那一拍；warp（時間不前進）回起跳拍。</summary>
        public double MsToBeat(double ms)
        {
            int s = SegAtMs(ms);
            double t = ms - _segMs[s];
            if (t <= _segStop[s]) return _segBeat[s];      // 定格中：拍子不動
            t -= _segStop[s];
            if (_beatLen[s] <= 0.0) return _segBeat[s];    // warp：這一段在時間軸上沒有厚度
            return _segBeat[s] + t / _beatLen[s];
        }

        /// <summary>該毫秒位置的 BPM（分段常數）；定格/warp 段沒有速度可言 → 0。</summary>
        public double BpmAt(double ms)
        {
            double bl = _beatLen[SegAtMs(ms)];
            return bl > 0.0 ? 60000.0 / bl : 0.0;
        }

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
                outLines.Add(new Line(ms, beat, kind, SnapOf(beat)));
            }
        }
    }
}
