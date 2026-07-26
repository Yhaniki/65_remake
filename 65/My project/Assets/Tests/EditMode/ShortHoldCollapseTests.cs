using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// 無理短長條 → 一般 note（<see cref="OsuBeatmap.CollapseShortHolds"/>）。門檻＝180 BPM 的 16 分音符
    /// (60000/180/4 ≈ 83.3 ms)，「16 分以下、不含 16 分」＝短於這個長度的 long note 才收掉。
    /// </summary>
    public class ShortHoldCollapseTests
    {
        private static OsuBeatmap Map(params OsuHitObject[] notes)
        {
            var m = new OsuBeatmap { Keys = 4, Bpm = 180 };
            m.HitObjects.AddRange(notes);
            return m;
        }

        [Test]
        public void Threshold_Is_16th_At_180Bpm()
        {
            Assert.AreEqual(60000.0 / 180.0 / 4.0, OsuBeatmap.ShortHoldMaxMs, 1e-9);
            Assert.AreEqual(83.33, OsuBeatmap.ShortHoldMaxMs, 0.01);
        }

        [Test]
        public void Short_Hold_Becomes_Tap()
        {
            var m = Map(new OsuHitObject(2, 1000, 1060));   // 60 ms — 按不出來的裝飾 hold
            Assert.AreEqual(1, m.CollapseShortHolds());
            Assert.IsFalse(m.HitObjects[0].IsHold);
            Assert.AreEqual(2, m.HitObjects[0].Lane, "lane 不變");
            Assert.AreEqual(1000, m.HitObjects[0].StartTimeMs, "判定時間不變");
        }

        [Test]
        public void Hold_Of_Exactly_A_16th_Is_Kept()
        {
            // 180 BPM 的 16 分 = 83.33 ms，整數 ms 譜會存成 83（或 84）→ 取整容差讓它留著（規格：不含 16 分）
            var m = Map(new OsuHitObject(0, 0, 83), new OsuHitObject(1, 500, 584));
            Assert.AreEqual(0, m.CollapseShortHolds());
            Assert.IsTrue(m.HitObjects[0].IsHold);
            Assert.IsTrue(m.HitObjects[1].IsHold);
        }

        [Test]
        public void Long_Holds_Are_Kept()
        {
            var m = Map(new OsuHitObject(3, 2000, 2500));   // 半秒長條
            Assert.AreEqual(0, m.CollapseShortHolds());
            Assert.IsTrue(m.HitObjects[0].IsHold);
            Assert.AreEqual(2500, m.HitObjects[0].EndTimeMs.Value);
        }

        [Test]
        public void Cutoff_Is_Absolute_Time_Not_Song_Bpm()
        {
            // 220 BPM 的 16 分 (68.2→68 ms) 比 180 BPM 的 16 分短 → 收掉；
            // 150 BPM 的 16 分 (100 ms) 比它長 → 留著。歌曲自己的 BPM 不影響門檻。
            var m = Map(new OsuHitObject(0, 0, 68), new OsuHitObject(1, 1000, 1100));
            m.Bpm = 220;
            Assert.AreEqual(1, m.CollapseShortHolds());
            Assert.IsFalse(m.HitObjects[0].IsHold);
            Assert.IsTrue(m.HitObjects[1].IsHold);
        }

        [Test]
        public void Taps_Untouched_And_TotalNotes_Drops_Per_Collapsed_Hold()
        {
            var m = Map(
                new OsuHitObject(0, 0),               // tap
                new OsuHitObject(1, 100, 130),        // 30 ms  → tap
                new OsuHitObject(2, 200, 400),        // 200 ms → 留
                new OsuHitObject(3, 500, 550));       // 50 ms  → tap
            Assert.AreEqual(1 + 2 + 2 + 2, m.TotalNotes, "收之前：tap 1 顆 + 3 個長條各 2 次判定");
            Assert.AreEqual(2, m.CollapseShortHolds());
            Assert.AreEqual(1 + 1 + 2 + 1, m.TotalNotes, "收之後：只剩下那個長條算 2 次判定");
            Assert.IsFalse(m.HitObjects[0].IsHold);
            Assert.IsTrue(m.HitObjects[2].IsHold);
        }

        [Test]
        public void Custom_Cutoff_Is_Honoured()
        {
            var m = Map(new OsuHitObject(0, 0, 200));
            Assert.AreEqual(0, m.CollapseShortHolds(), "預設門檻下 200 ms 是正常長條");
            Assert.AreEqual(1, m.CollapseShortHolds(250.0), "自訂門檻 250 ms → 收掉");
            Assert.IsFalse(m.HitObjects[0].IsHold);
        }

        [Test]
        public void Empty_Chart_Is_A_Noop()
        {
            Assert.AreEqual(0, new OsuBeatmap().CollapseShortHolds());
        }

        // ── 格式 gating：只有「別的遊戲轉過來的譜」能收短長條；SDO 原生 .gn 一律照原樣打 ──────────────
        [Test]
        public void Only_Converted_External_Formats_May_Collapse()
        {
            Assert.IsTrue(OsuBeatmap.AllowsShortHoldCollapse(SongFormat.Osu), "osu 轉檔譜 → 收");
            Assert.IsTrue(OsuBeatmap.AllowsShortHoldCollapse(SongFormat.Sm), "StepMania → 收");
            Assert.IsTrue(OsuBeatmap.AllowsShortHoldCollapse(SongFormat.Malody), "Malody .mc → 收");
        }

        [Test]
        public void Native_Gn_Charts_Are_Never_Collapsed()
        {
            Assert.IsFalse(OsuBeatmap.AllowsShortHoldCollapse(SongFormat.None), "官方 k.gn（DATA/MUSIC）不修改");
            Assert.IsFalse(OsuBeatmap.AllowsShortHoldCollapse(SongFormat.Gn), ".gn 歌曲包（[NX] 轉出來的原生譜）也不修改");
        }

        // cap 被 StepMania warp(負 BPM)掃掉的長條**不收**：它的判定長度短只是因為尾端被夾到 warp 那一瞬間，
        // 拍子上整條是正常長度（StepMania 照 beat 間距整條畫出來）。收成 tap 就把整條裝飾長條抹掉了。
        // 真實案例：deadsoul[Blue]（Blue's 6th step）有 62 條，判定長度只剩 ~57 ms。見 SmChartTests。
        [Test]
        public void A_Hold_Whose_Cap_Was_Warped_Away_Is_Never_Collapsed()
        {
            var m = Map(new OsuHitObject(0, 1000, 1057, false, false, 1000.0, 1057.0, isFakeTail: true),
                        new OsuHitObject(1, 2000, 2057));   // 同樣長度、沒有 warp → 照收
            Assert.AreEqual(1, m.CollapseShortHolds());
            Assert.IsTrue(m.HitObjects[0].IsHold, "cap 被 warp 掃掉的長條整條留著");
            Assert.IsFalse(m.HitObjects[1].IsHold);
        }

        [Test]
        public void A_Warped_Cap_Hold_Only_Counts_Its_Head_Towards_The_Total()
        {
            // 結尾不用放開（見 OsuHitObject.IsFakeTail）→ 只有頭部一個判定；算兩個滿分就永遠差那一下。
            var m = Map(new OsuHitObject(0, 1000, 1500, false, false, 1000.0, 1500.0, isFakeTail: true),
                        new OsuHitObject(1, 2000, 2500));
            Assert.AreEqual(1 + 2, m.TotalNotes);
        }

        [Test]
        public void Lead_In_Keeps_The_Warped_Cap_Flag()
        {
            var m = Map(new OsuHitObject(0, 1000, 1057, false, false, 1000.0, 1057.0, isFakeTail: true));
            m.ApplyLeadIn(2000);
            Assert.IsTrue(m.HitObjects[0].IsFakeTail, "平移之後旗標不能掉 —— 掉了就變成要按結尾的短長條");
            Assert.AreEqual(3000, m.HitObjects[0].StartTimeMs);
            Assert.AreEqual(3057, m.HitObjects[0].EndTimeMs.Value);
        }

        [Test]
        public void Timing_Points_And_Stops_Are_Not_Touched()
        {
            var m = Map(new OsuHitObject(0, 0, 40));
            m.TimingPoints.Add(new OsuTimingPoint(0, 461.5));
            m.Stops.Add(new ScrollStop(1000, 250));
            m.MusicStartOffsetMs = 1234;
            m.CollapseShortHolds();
            Assert.AreEqual(461.5, m.TimingPoints[0].BeatLength, 1e-9);
            Assert.AreEqual(250.0, m.Stops[0].DurationMs, 1e-9);
            Assert.AreEqual(1234.0, m.MusicStartOffsetMs, 1e-9);
        }
    }
}
