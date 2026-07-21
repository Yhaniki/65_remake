using System;
using System.Collections.Generic;

namespace Sdo.Osu
{
    /// <summary>
    /// Note-scroll positioner, ported from osu!mania's default (Sequential algorithm + relative beat-length
    /// scaling) but anchored to a FIXED base tempo so every song scrolls at the same base speed — what the
    /// user asked for ("不依 BPM 改變速度基準，base 140 BPM").
    ///
    /// How it mirrors osu (see assets/osu-master):
    ///   • The base velocity (px/s at multiplier 1.0) is constant — it does NOT scale with the song's BPM.
    ///     It is calibrated with the official SDO formula  px/s = BPM × speed × 1.6  at a REFERENCE BPM of
    ///     140 (see docs / memory sdo-note-scroll-speed): vBase = 140 × speedMul × 1.6.
    ///   • Within a song the scroll still VARIES: each segment's multiplier =
    ///       SV × (baseBeatLength / localBeatLength)
    ///     exactly like osu's MultiplierControlPoint (Velocity=1 for mania). At the base tempo with SV=1 the
    ///     multiplier is 1.0 (→ base speed); a ÷2/×2 BPM gimmick or an osu! green-line SV locally speeds up /
    ///     slows down the notes — RelativeScaleBeatLengths=true in mania.
    ///     基準速度不是 osu 的「時間最多的 BPM」而是「站得住腳的最快 BPM」——見
    ///     <see cref="BaseBeatLength"/> 與 docs/architecture/scroll-base-bpm.md（非對稱理由：快讀不到、慢讀得到）。
    ///   • Position integrates the multiplier across segments (osu SequentialScrollAlgorithm), so a note's
    ///     on-screen distance from the judge line = vBase × ∫ multiplier dτ between now and the note time.
    ///
    /// <see cref="ConstantScroll"/> reproduces osu's "Constant Speed" mod: all multipliers are ignored, so
    /// the scroll is perfectly linear at vBase regardless of BPM/SV.
    ///
    /// Pure logic (no UnityEngine) — fully unit-testable.
    /// </summary>
    public sealed class ManiaScroll
    {
        /// <summary>Reference tempo the constant base speed is calibrated to (the user's "base 140 BPM").</summary>
        public const double DefaultReferenceBpm = 130.0;

        /// <summary>
        /// 判定「同一個速度」的相對容差（±3%）。osu 的 most-common beat length 是**逐一 exact match**，
        /// 譜面把同一段音樂寫成 163.5/166/167/167.9/168.2/169/170 這種抖動 BPM 時，明明佔一半以上時間的
        /// 主速度會被切成七小塊，反而輸給單一一段慢速（sdom5028 My Dearest：84bpm 38% 勝過 168 家族 53%）。
        /// 先把相差在容差內的 beat length 併成一群再比總時長，就不會被抖動騙走。詳見
        /// docs/architecture/scroll-base-bpm.md。
        /// </summary>
        public const double TempoClusterTolerance = 0.03;

        // ---- 「快的當基準」門檻（非對稱規則，見 docs/architecture/scroll-base-bpm.md §3）----
        // 玩家對「變快」和「變慢」的容忍度不對稱：慢下來還讀得到，快上去就直接爆掉。所以基準不是「時間最多的
        // 速度」，而是「站得住腳的最快速度」——只要某個速度撐得夠久（連續一段夠長，或累計佔比夠高）且**真的有
        // note**，它就當基準；其餘較慢的段落乘 <1 變慢（安全），只有零碎的短爆發才會被留在 >1。
        // 這幾個常數就是「長時間要怎麼算」的答案，之後要調就調這裡（都有單元測試釘住語意）。

        /// <summary>最長「連續」一段有多久算撐得住（ms）。6 秒 ≈ 8 小節 @160bpm，已經是要重新讀譜的長度。</summary>
        public const double FastTempoMinRunMs = 6000.0;

        /// <summary>或者：累計時間佔全譜的比例夠高也算（就算被切成好幾段）。</summary>
        public const double FastTempoMinTimeFraction = 0.20;

        /// <summary>而且那段裡要真的有 note（佔全譜 note 的比例）；沒 note 的間奏再快也不用讀。</summary>
        public const double FastTempoMinNoteFraction = 0.05;

        /// <summary>基準最多只能比「時間最多的速度」快這麼多倍，免得一段 ×4 的 gimmick 讓整首慢到爬。</summary>
        public const double MaxBaseTempoRatio = 2.5;

        /// <summary>
        /// 爭議譜（長時間快段 + 長時間慢段各佔一半）時，快段相對基準的倍率上限。
        /// 這種譜的基準改用**選歌畫面顯示的 BPM**（譜面表頭）——玩家看到的數字就是他調速度檔位的依據；
        /// 但表頭常常寫的就是慢的那一群（25 首爭議譜裡有 15 首），照抄會讓快段回到 2.0× 飛出去，
        /// 所以再夾這條上限：快段最多 1.5×，不夠就把基準往上抬。使用者定奪 2026-07-21。
        /// </summary>
        public const double FastSectionMaxMultiplier = 1.5;

        /// <summary>Official SDO scroll constant: on-screen px/s = BPM × speed × <see cref="OfficialPxPerBpmSpeed"/>.</summary>
        public const double OfficialPxPerBpmSpeed = 1.6;

        private readonly double _vBase;        // design-px per second at multiplier 1.0
        private readonly double[] _time;       // control-point start times (ms), ascending
        private readonly double[] _mult;       // multiplier in [_time[i], _time[i+1])
        private readonly double[] _prefix;     // ∫ multiplier dτ (ms) from _time[0] to _time[i]

        private ManiaScroll(double vBase, double[] time, double[] mult, double[] prefix)
        {
            _vBase = vBase; _time = time; _mult = mult; _prefix = prefix;
        }

        /// <summary>Base velocity in design-px/s (the speed where multiplier == 1.0).</summary>
        public double BaseVelocity => _vBase;

        /// <summary>vBase = referenceBpm × speedMul × 1.6 (design-px/s).</summary>
        public static double BaseVelocityFor(double speedMul, double referenceBpm = DefaultReferenceBpm)
            => referenceBpm * speedMul * OfficialPxPerBpmSpeed;

        /// <summary>
        /// Build a scroll positioner for <paramref name="map"/> at the given speed step.
        /// <paramref name="constantScroll"/> = osu "Constant Speed" mod (ignore all BPM/SV variation).
        /// </summary>
        public static ManiaScroll Build(OsuBeatmap map, double speedMul,
            bool constantScroll = false, double referenceBpm = DefaultReferenceBpm, bool followSongBpm = false)
        {
            // Base-velocity anchor. Default: fixed referenceBpm (every song scrolls at the same base speed).
            // followSongBpm: anchor to THIS song's most-common BPM, so the base tempo scrolls at the official
            // px/s = songBpm × speed × 1.6 and mid-song ½/×2 changes scale it to currentBpm × speed × 1.6
            // (the internal multiplier uses the same most-common beat length, so it is 1.0 at the base tempo).
            double anchorBpm = referenceBpm;
            if (followSongBpm && map != null)
            {
                double baseBeat = (map.TimingPoints != null && map.TimingPoints.Count > 0)
                    ? BaseBeatLength(map.TimingPoints, NoteTimes(map), LastObjectMs(map), map.Bpm) : 0.0;
                if (baseBeat <= 0.0) baseBeat = 60000.0 / Math.Max(1.0, map.Bpm);
                anchorBpm = 60000.0 / Math.Max(1e-9, baseBeat);
            }
            double vBase = BaseVelocityFor(speedMul, anchorBpm);
            var pts = (map == null || constantScroll) ? null : BuildMultiplierPoints(map);
            if (pts == null || pts.Count == 0)
                return new ManiaScroll(vBase, new[] { 0.0 }, new[] { 1.0 }, new[] { 0.0 });

            int n = pts.Count;
            var time = new double[n];
            var mult = new double[n];
            var prefix = new double[n];
            for (int i = 0; i < n; i++) { time[i] = pts[i].time; mult[i] = pts[i].mult; }
            prefix[0] = 0.0;
            for (int i = 1; i < n; i++)
                prefix[i] = prefix[i - 1] + (time[i] - time[i - 1]) * mult[i - 1];
            return new ManiaScroll(vBase, time, mult, prefix);
        }

        /// <summary>
        /// On-screen distance (design-px) a note travels between <paramref name="fromMs"/> and
        /// <paramref name="toMs"/>. Positive when toMs &gt; fromMs (note in the future). Use as the note's
        /// distance from the judge line: Y = judgeLineY + PixelDistance(now, noteMs).
        /// </summary>
        public double PixelDistance(double fromMs, double toMs)
            => _vBase / 1000.0 * (WeightedMsAt(toMs) - WeightedMsAt(fromMs));

        /// <summary>∫ multiplier dτ from _time[0] to <paramref name="t"/> (ms). Extrapolates outside the range.</summary>
        private double WeightedMsAt(double t)
        {
            int lo = 0, hi = _time.Length - 1, s = 0;
            while (lo <= hi) { int mid = (lo + hi) >> 1; if (_time[mid] <= t) { s = mid; lo = mid + 1; } else hi = mid - 1; }
            return _prefix[s] + (t - _time[s]) * _mult[s];
        }

        // ---- control-point aggregation (mirrors osu DrawableScrollingRuleset.load + MultiplierControlPoint) ----

        private static List<(double time, double mult)> BuildMultiplierPoints(OsuBeatmap map)
        {
            var tps = map.TimingPoints;
            if (tps == null || tps.Count == 0) return null;

            double lastObjMs = LastObjectMs(map);
            double baseBeat = BaseBeatLength(tps, NoteTimes(map), lastObjMs, map.Bpm);
            if (baseBeat <= 0.0) baseBeat = 60000.0 / Math.Max(1.0, map.Bpm);

            // sort by (time, original index) so equal-time points keep the LAST one (osu collapses same-time).
            int n = tps.Count;
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = i;
            Array.Sort(idx, (a, b) =>
            {
                int c = tps[a].TimeMs.CompareTo(tps[b].TimeMs);
                return c != 0 ? c : a.CompareTo(b);
            });

            var result = new List<(double time, double mult)>(n);
            double curBeat = baseBeat;   // last uninherited beat length (before any → treat as base → mult 1)
            double curSv = 1.0;          // last inherited SV multiplier
            for (int j = 0; j < n; j++)
            {
                var p = tps[idx[j]];
                if (p.Uninherited) curBeat = p.BeatLength > 0.0 ? p.BeatLength : curBeat;
                else curSv = p.SpeedMultiplier;
                double mult = curSv * baseBeat / Math.Max(1e-9, curBeat);
                if (result.Count > 0 && result[result.Count - 1].time == p.TimeMs)
                    result[result.Count - 1] = (p.TimeMs, mult);   // same time → overwrite (keep latest)
                else
                    result.Add((p.TimeMs, mult));
            }
            return result;
        }

        /// <summary>Largest note time (hold end or tap), or 0 if none.</summary>
        private static double LastObjectMs(OsuBeatmap map)
        {
            double last = 0.0;
            var hos = map.HitObjects;
            if (hos != null)
                for (int i = 0; i < hos.Count; i++)
                {
                    var h = hos[i];
                    double t = h.EndTimeMs ?? h.StartTimeMs;
                    if (t > last) last = t;
                }
            return last;
        }

        /// <summary>
        /// The uninherited beat length covering the most total time (osu Beatmap.GetMostCommonBeatLength),
        /// plus one deviation from osu: beat lengths within <see cref="TempoClusterTolerance"/> of each other
        /// count as the SAME tempo, so a chart that jitters its main tempo (167.9 / 168.2 / 169 / 170 …)
        /// isn't out-voted by one uniform slow section (see docs/architecture/scroll-base-bpm.md).
        ///
        /// Each tempo segment is weighted by its duration (this point → next tempo point, last → lastObjMs).
        /// <paramref name="preferredBpm"/> (譜面表頭 BPM，也就是選歌畫面顯示的那個) wins as the group's
        /// representative when it falls inside the winning group; otherwise the longest-lasting member does.
        /// Returns 0 if there are no uninherited points.
        /// </summary>
        public static double MostCommonBeatLength(IReadOnlyList<OsuTimingPoint> tps, double lastObjMs,
            double preferredBpm = 0.0)
            => BaseBeatLength(tps, null, lastObjMs, preferredBpm);

        /// <summary>
        /// 譜面的**基準速度**（base beat length，多快算 multiplier 1.0）。
        ///
        /// 規則（完整說明與語料統計見 docs/architecture/scroll-base-bpm.md）：
        ///   1. 每個 uninherited timing point 依「持續時間」加權（osu GetMostCommonBeatLength 的做法，
        ///      第一段一律從時間 0 起算）。
        ///   2. 相差 ±<see cref="TempoClusterTolerance"/> 內的 beat length 視為同一個速度（併群）。
        ///   3. **非對稱**取捨：不是選時間最多的群，而是選「站得住腳的最快的群」——連續一段 ≥
        ///      <see cref="FastTempoMinRunMs"/> 或累計 ≥ <see cref="FastTempoMinTimeFraction"/>，
        ///      且該群的 note 佔比 ≥ <see cref="FastTempoMinNoteFraction"/>。理由：慢下來玩家讀得到，
        ///      快上去讀不到，所以寧可讓其他段落變慢（乘 &lt;1），只有零碎短爆發才留在 &gt;1。
        ///      保險：基準最多是時間最多那群的 <see cref="MaxBaseTempoRatio"/> 倍；沒有任何群合格就退回它。
        ///   4. 若「最快合格群」≠「時間最多的群」（＝長時間快段與長時間慢段各佔一塊的爭議譜），
        ///      基準改用 <paramref name="preferredBpm"/>（選歌畫面顯示的 BPM），夾在兩群之間，
        ///      再保證快段不超過 <see cref="FastSectionMaxMultiplier"/>×。
        ///   5. 群的代表值優先取 <paramref name="preferredBpm"/>（譜面表頭 BPM＝選歌畫面顯示的數字）。
        ///
        /// <paramref name="noteMs"/> 可為 null（沒有 note 資訊 → 跳過步驟 3 的 note 佔比檢查）。
        /// 沒有任何 uninherited point 時回 0。
        /// </summary>
        public static double BaseBeatLength(IReadOnlyList<OsuTimingPoint> tps, IReadOnlyList<double> noteMs,
            double lastObjMs, double preferredBpm = 0.0)
        {
            if (tps == null) return 0.0;
            // collect uninherited points sorted by time
            var times = new List<double>();
            var beats = new List<double>();
            for (int i = 0; i < tps.Count; i++)
                if (tps[i].Uninherited) { times.Add(tps[i].TimeMs); beats.Add(tps[i].BeatLength); }
            int m = times.Count;
            if (m == 0) return 0.0;

            var order = new int[m];
            for (int i = 0; i < m; i++) order[i] = i;
            Array.Sort(order, (a, b) => times[a].CompareTo(times[b]));

            // 每段的 [t0,t1)：第一段一律從 0 起算（osu GetMostCommonBeatLength 的 `i==0 ? 0 : t.Time`），
            // 所以開頭那段就算 timing point 不在 0 也從歌曲起點開始計。
            double end = Math.Max(lastObjMs, times[order[m - 1]]);
            var durByBeat = new Dictionary<double, double>();
            var distinct = new List<double>();      // 相異 beat length，依首次出現（時間）排序
            var segT0 = new double[m]; var segT1 = new double[m];
            for (int k = 0; k < m; k++)
            {
                double t0 = (k == 0) ? 0.0 : times[order[k]];
                double t1 = (k + 1 < m) ? times[order[k + 1]] : end;
                double bl = beats[order[k]];
                segT0[k] = t0; segT1[k] = Math.Max(t0, t1);
                durByBeat.TryGetValue(bl, out double acc);
                durByBeat[bl] = acc + Math.Max(0.0, t1 - t0);
                if (!distinct.Contains(bl)) distinct.Add(bl);
            }
            double chartMs = Math.Max(1.0, end);
            int noteTotal = noteMs != null ? noteMs.Count : 0;

            // ---- 每一群（seed = 該群代表的 beat length）的總時長 / 最長連續 / note 數 ----
            double mostCommonSeed = distinct[0]; double mostCommonTotal = -1.0, mostCommonSeedDur = -1.0;
            var qualified = new List<double>();   // 撐得住且有 note 的群（之後由快到慢挑第一個沒超過上限的）
            foreach (double seed in distinct)
            {
                double total = 0.0;
                foreach (double bl in distinct)
                    if (SameTempo(bl, seed)) total += durByBeat[bl];

                // 最長連續：時間相鄰且同群的段落要接起來算（同一個速度常被切成好幾個 timing point）。
                double longestRun = 0.0, runStart = 0.0; bool inRun = false;
                for (int k = 0; k < m; k++)
                {
                    bool member = SameTempo(beats[order[k]], seed);
                    if (member && !inRun) { inRun = true; runStart = segT0[k]; }
                    if (inRun && (!member || k == m - 1))
                    {
                        double runEnd = member ? segT1[k] : segT0[k];
                        if (runEnd - runStart > longestRun) longestRun = runEnd - runStart;
                        inRun = member && k < m - 1;
                    }
                }

                int notes = 0;
                if (noteTotal > 0)
                    for (int k = 0; k < m; k++)
                    {
                        if (!SameTempo(beats[order[k]], seed)) continue;
                        for (int i = 0; i < noteTotal; i++)
                            if (noteMs[i] >= segT0[k] && (noteMs[i] < segT1[k] || k == m - 1)) notes++;
                    }

                // 時間最多的群（＝ osu 的答案，也是保底）
                double seedDur = durByBeat[seed];
                if (total > mostCommonTotal + 1e-9 || (total > mostCommonTotal - 1e-9 && seedDur > mostCommonSeedDur))
                { mostCommonTotal = total; mostCommonSeedDur = seedDur; mostCommonSeed = seed; }

                // 合格 = 撐得夠久 且 真的有 note
                bool longEnough = longestRun >= FastTempoMinRunMs || total / chartMs >= FastTempoMinTimeFraction;
                bool hasNotes = noteTotal == 0 || (double)notes / noteTotal >= FastTempoMinNoteFraction;
                if (longEnough && hasNotes) qualified.Add(seed);
            }

            // 由快到慢挑第一個沒超過上限的合格群。上限＝基準最多比「時間最多的群」快 MaxBaseTempoRatio 倍
            // （一段 ×4 的 gimmick 撐再久也不該讓整首爬行；此時退而求其次挑次快的，都沒有就用時間最多的）。
            qualified.Sort();                     // beat length 由小到大 = 由快到慢
            double winner = mostCommonSeed;
            for (int i = 0; i < qualified.Count; i++)
            {
                if (qualified[i] >= mostCommonSeed) break;                        // 沒有比它更快的了
                if (mostCommonSeed / qualified[i] <= MaxBaseTempoRatio + 1e-9) { winner = qualified[i]; break; }
            }

            // 一群的代表值：優先用表頭 BPM（選歌畫面顯示的那個速度）——只要它落在該群裡；否則取群內時間最長的成員。
            double Representative(double seed)
            {
                if (preferredBpm > 0.0)
                {
                    double pb = 60000.0 / preferredBpm;
                    if (SameTempo(pb, seed)) return pb;
                }
                double b = seed; double bd = -1.0;
                foreach (double bl in distinct)
                {
                    if (!SameTempo(bl, seed)) continue;
                    if (durByBeat[bl] > bd) { bd = durByBeat[bl]; b = bl; }
                }
                return b;
            }

            double winnerBeat = Representative(winner);
            if (winner == mostCommonSeed) return winnerBeat;   // 沒爭議：快群本身就是時間最多的群

            // ---- 爭議譜：長時間的快段和長時間的慢段各佔一塊（例：Electric Shock 125 76% / 250 24%）----
            // 使用者定奪：這種就用**選歌畫面顯示的 BPM**（譜面表頭）當基準——玩家看到的數字就是他調速度檔位
            // 的依據。但表頭往往寫的就是慢的那一群，照抄快段會回到 2.0×，所以再夾 FastSectionMaxMultiplier：
            // 快段最多 1.5×（不夠就把基準往上抬），且基準不超過快群本身。
            double hiBpm = 60000.0 / winnerBeat;
            double loBpm = 60000.0 / Representative(mostCommonSeed);
            double bpm = preferredBpm > 0.0 ? Math.Min(Math.Max(preferredBpm, loBpm), hiBpm) : loBpm;
            bpm = Math.Max(bpm, hiBpm / FastSectionMaxMultiplier);
            bpm = Math.Min(bpm, hiBpm);
            return 60000.0 / bpm;
        }

        /// <summary>兩個 beat length 是否算同一個速度（相對誤差在 <see cref="TempoClusterTolerance"/> 內）。</summary>
        private static bool SameTempo(double beatLength, double seed)
            => seed > 0.0 && Math.Abs(beatLength / seed - 1.0) <= TempoClusterTolerance;

        /// <summary>note 的判定時刻（tap / hold 頭），給 <see cref="BaseBeatLength"/> 算 note 佔比用。</summary>
        private static List<double> NoteTimes(OsuBeatmap map)
        {
            var list = new List<double>();
            var hos = map != null ? map.HitObjects : null;
            if (hos != null)
                for (int i = 0; i < hos.Count; i++) list.Add(hos[i].StartTimeMs);
            return list;
        }
    }
}
