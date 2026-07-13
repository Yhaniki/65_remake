using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// 外部歌（osu/StepMania）沒有官方舞蹈 → 現場生一支（<see cref="RandomDps"/>）：
    ///   開場 = 照抄一支官方 DPS 的開場 row（每個 row 是某支 clip 的一段幀切片，官方就是這樣把一支 clip 接著播完），
    ///   之後 = random_dps.py 的 mot-slice（隨機抽 mot、從第 0 幀播、8 拍量化），
    /// 且同一首歌永遠生出同一支舞。
    /// </summary>
    public class RandomDpsTests
    {
        private static readonly string[] Pool =
        {
            "wdance0002.mot", "wdance0008.mot", "wdance0052.mot", "wdance0062.mot", "wdance0402.mot",
        };

        // 真實幀數（資料樹實測）。長短差很多才測得出問題 —— 之前每支都假設 120 幀，正是這個假資料
        // 讓「開場被當成整支 clip 播完 / 太短就被丟掉」的 bug 躲過測試。
        private static readonly Dictionary<string, int> RealFrames = new Dictionary<string, int>
        {
            { "wrest0001.mot", 124 }, { "wdance0002.mot", 95 }, { "wdance0008.mot", 214 },
            { "wdance0052.mot", 2465 }, { "wdance0062.mot", 1072 }, { "wdance0077.mot", 1208 },
            { "wdance0402.mot", 594 }, { "w_005501.mot", 161 }, { "w_005502.mot", 149 },
        };

        private static int Frames(string mot) => RealFrames.TryGetValue(mot, out int f) ? f : DpsIndex.DefaultFrames;

        // 官方 11435.DPS 的開場（逐字）：同一支 clip 連著兩段，再換下一支。
        private static IntroSlice[] Official() => new[]
        {
            new IntroSlice("w_005501.mot", 0, 109),
            new IntroSlice("w_005501.mot", 110, 160),
            new IntroSlice("w_005502.mot", 0, 77),
        };

        private static RandomDpsRequest Req(double seconds = 120.0, uint seed = 1234u,
                                            IReadOnlyList<IntroSlice[]> intros = null, double bpm = 120.0)
            => new RandomDpsRequest
            {
                Bpm = bpm,
                DanceSeconds = seconds,
                Pool = Pool,
                Intros = intros,
                FrameCount = Frames,
                Seed = seed,
                ChartName = "ext_test.gn",
            };

        private static List<IntroSlice[]> One(IntroSlice[] intro) => new List<IntroSlice[]> { intro };

        // ---- 開場：照抄官方 ----

        [Test]
        public void Intro_Replays_The_Official_Opening_Verbatim()
        {
            var rows = RandomDps.Plan(Req(intros: One(Official())));

            Assert.GreaterOrEqual(rows.Count, 4);
            var want = Official();
            for (int i = 0; i < want.Length; i++)
            {
                Assert.AreEqual(want[i].Mot, rows[i].Mot, "row " + i + " 的動作");
                Assert.AreEqual(want[i].StartF, rows[i].StartFrame, "row " + i + " 的起始幀");
                Assert.AreEqual(want[i].EndF, rows[i].EndFrame, "row " + i + " 的結束幀");
                Assert.AreEqual(want[i].Frames / 30f, rows[i].DurSec, 1e-4f, "row " + i + " 的長度 = 幀數/30");
            }
            CollectionAssert.Contains(Pool, rows[want.Length].Mot, "開場之後就回到一般 pool");
        }

        [Test]
        public void Intro_Slices_Stay_Frame_Continuous()
        {
            // 官方是「同一支 clip 接著播」：第二個 row 從第一個 row 收尾的地方接下去，不是從頭重播
            // （重播＝ResolveMot 拿到同一個 MotLoader instance → 不觸發 blend → 幀從 109 硬跳回 0）。
            var rows = RandomDps.Plan(Req(intros: One(Official())));
            Assert.AreEqual(rows[0].Mot, rows[1].Mot);
            Assert.AreNotEqual(0, rows[1].StartFrame, "同一支 clip 的第二段不能從頭重播");
            Assert.AreEqual(rows[0].EndFrame + 1, rows[1].StartFrame, "照抄官方的接續（109 → 110）");
        }

        [Test]
        public void Intro_Is_Not_Quantised_And_Never_Dropped()
        {
            // 60 BPM：一個 row 單位（8 拍）= 8 秒 = 240 幀，比開場每一段都長 → 舊做法會把整組開場丟光。
            var rows = RandomDps.Plan(Req(bpm: 60.0, intros: One(Official())));

            Assert.AreEqual("w_005501.mot", rows[0].Mot, "開場不受 8 拍量化限制，一定要放");
            Assert.AreEqual(110, rows[1].StartFrame);
            Assert.AreEqual("w_005502.mot", rows[2].Mot);
        }

        [Test]
        public void Intro_Is_Clamped_To_The_Song_Span()
        {
            // 12 秒的歌（IntroMaxSpanFraction=0.5 → 開場最多 6 秒），開場本體是 12.9 秒 → 要被裁進去，不能超出。
            var rows = RandomDps.Plan(Req(seconds: 12.0, intros: One(Official())));

            int endBeat = 24;   // 12s @120BPM
            var last = rows[rows.Count - 1];
            Assert.AreEqual(endBeat, last.StartBeat + last.Beats, "整支舞不能超出歌的 note 區間");
            double introSec = 0;
            foreach (var r in rows) { if (!r.Mot.StartsWith("w_00")) break; introSec += r.DurSec; }
            Assert.LessOrEqual(introSec, 6.0 + 0.1, "開場最多吃掉一半歌長");
        }

        [Test]
        public void Intro_Never_Eats_The_Whole_Song()
        {
            // 超長開場（wdance0052 整支 82 秒的一大段）+ 60 秒的歌 → 仍必須留得下至少一個 random row。
            var huge = new[] { new IntroSlice("wdance0052.mot", 0, 2464) };
            var rows = RandomDps.Plan(Req(seconds: 60.0, intros: One(huge)));

            Assert.Greater(rows.Count, 1);
            bool anyPool = false;
            foreach (var r in rows) if (System.Array.IndexOf(Pool, r.Mot) >= 0 && r.StartFrame == 0) anyPool = true;
            Assert.IsTrue(anyPool, "開場把整首歌吃光了");
        }

        [Test]
        public void Intro_Clip_Missing_From_The_Index_Keeps_Its_Slice()
        {
            // 索引沒列到幀數（FrameCount 回退 240）也不能拿回退值去裁切片：284..410 是官方的真實範圍。
            var rows = RandomDps.Plan(Req(intros: One(new[] { new IntroSlice("wdance0400.mot", 284, 410) })));

            Assert.AreEqual(284, rows[0].StartFrame);
            Assert.AreEqual(410, rows[0].EndFrame);
        }

        [Test]
        public void Intro_Without_A_Range_Plays_The_Whole_Clip()
        {
            // 舊索引（V1）只有名字 → 整支播完。
            var rows = RandomDps.Plan(Req(intros: One(new[] { new IntroSlice("wdance0008.mot", 0, -1) })));

            Assert.AreEqual(0, rows[0].StartFrame);
            Assert.AreEqual(213, rows[0].EndFrame, "wdance0008 = 214 幀");
        }

        // ---- 開場之後：random_dps.py 的 mot-slice ----

        [Test]
        public void After_The_Intro_The_Plan_Is_The_Python_Mot_Slice_Plan()
        {
            var rows = RandomDps.Plan(Req(intros: One(Official())));

            for (int i = 3; i < rows.Count - 1; i++)   // 尾 row 收剩下的拍數，不受量化限制
            {
                CollectionAssert.Contains(Pool, rows[i].Mot);
                Assert.AreEqual(0, rows[i].StartFrame, "隨機段每支 mot 都從第 0 幀播");
                Assert.AreEqual(0, rows[i].Beats % 8, "隨機段的 row 長度是 8 拍的整數倍");
                Assert.LessOrEqual(rows[i].EndFrame + 1, Frames(rows[i].Mot), "不能播超過 mot 本身的長度");
            }
        }

        [Test]
        public void No_Intro_List_Falls_Back_To_The_Pool()
        {
            var none = RandomDps.Build(Req(intros: null));
            var empty = RandomDps.Build(Req(intros: new List<IntroSlice[]>()));

            Assert.AreEqual(none, empty, "沒有開場清單 → 純 python 規劃，且不消耗 RNG");
            foreach (var r in RandomDps.Plan(Req(intros: null)))
            {
                CollectionAssert.Contains(Pool, r.Mot);
                Assert.AreEqual(0, r.StartFrame);
            }
        }

        [Test]
        public void Pool_All_Too_Short_Still_Emits_The_Intro()
        {
            // pool 全是不足 8 拍的短 clip（wdance0002 = 95 幀 = 3.2 秒 = 6.3 拍 @120BPM）→ 隨機段放棄，
            // 但開場與尾 row 還是要在。
            var req = Req(intros: One(Official()));
            req.Pool = new[] { "wdance0002.mot" };
            var rows = RandomDps.Plan(req);

            Assert.AreEqual("w_005501.mot", rows[0].Mot);
            var last = rows[rows.Count - 1];
            Assert.AreEqual(240, last.StartBeat + last.Beats, "尾 row 仍把 120 秒 @120BPM 的區間收完");
        }

        // ---- 整體形狀 / 決定性 / 寫檔 ----

        [Test]
        public void Rows_Are_Contiguous_And_Fill_The_Span()
        {
            var rows = RandomDps.Plan(Req(seconds: 120.0, intros: One(Official())));

            int beat = 0;
            double total = 0.0;
            foreach (var r in rows)
            {
                Assert.AreEqual(beat, r.StartBeat, "row 必須首尾相接（引擎照順序播）");
                beat += r.Beats;
                total += r.DurSec;
                Assert.Greater(r.EndFrame, 0);
            }
            Assert.AreEqual(240, beat, "120 秒 @120BPM = 240 拍，要鋪滿");
            Assert.AreEqual(120.0, total, 0.5, "整支舞的長度 ≈ 第一個 note → 最後一個 note");
        }

        [Test]
        public void Short_Span_Plans_Nothing()
        {
            Assert.AreEqual(0, RandomDps.Plan(Req(seconds: 1.0, intros: One(Official()))).Count, "不足一個 row 單位 → 不生");
            Assert.AreEqual(0, RandomDps.Build(Req(seconds: 1.0)).Length);
        }

        [Test]
        public void Same_Seed_Same_Dance_Different_Seed_Different_Dance()
        {
            var intros = One(Official());
            intros.Add(new[] { new IntroSlice("wdance0077.mot", 200, 250) });   // 有得選，seed 才有意義

            var a = RandomDps.Build(Req(seed: 42u, intros: intros));
            var b = RandomDps.Build(Req(seed: 42u, intros: intros));
            var c = RandomDps.Build(Req(seed: 43u, intros: intros));

            Assert.AreEqual(a, b, "同一首歌（同 seed）每次都要生出一模一樣的 .dps");
            Assert.AreNotEqual(a, c, "不同歌（不同 seed）不該生出同一支舞");
        }

        [Test]
        public void Generated_File_Loads_Back_Through_The_Engine_Loader()
        {
            var req = Req(seconds: 45.0, intros: One(Official()));
            var plan = RandomDps.Plan(req);
            var dps = DpsLoader.Load(RandomDps.Build(req));

            Assert.IsNotNull(dps, "生出來的檔要能被遊戲的 DpsLoader 讀回來（PAS00003 + 317 stride）");
            Assert.AreEqual(plan.Count, dps.Rows.Length);
            for (int i = 0; i < plan.Count; i++)
            {
                Assert.AreEqual(plan[i].Mot, dps.Rows[i].Mot, "row " + i + " 的動作名");
                Assert.AreEqual(plan[i].StartFrame, dps.Rows[i].StartF, "row " + i + " 的起始幀（開場的非 0 起點要能來回）");
                Assert.AreEqual(plan[i].EndFrame, dps.Rows[i].EndF);
                Assert.AreEqual(plan[i].DurSec, dps.Rows[i].Dur, 1e-4f);
            }
            Assert.AreEqual(45.0, dps.Total, 0.5, "整支舞的長度 ≈ 曲子的 note 區間");
        }

        // ---- DANCE/DPSINDEX.TXT：動作池／開場／幀數是離線 (tools/build_dps_index.py) 烘好的，遊戲只讀這個檔 ----

        [Test]
        public void Index_Parses_Pool_Intros_And_Frames()
        {
            var idx = DpsIndex.Parse(string.Join("\n", new[]
            {
                "# comment",
                "V 2",
                "F wdance0002.mot 95",
                "F WREST0001.MOT 124",
                "P wdance0002.mot",
                "P WDANCE0003.MOT",
                "I w_005501.mot:0:109|w_005501.mot:110:160|w_005502.mot:0:77",
                "",
                "Z unknown tag is skipped",
            }));

            Assert.IsFalse(idx.IsEmpty);
            CollectionAssert.AreEqual(new[] { "wdance0002.mot", "wdance0003.mot" }, idx.Pool, "名稱一律轉小寫");
            Assert.AreEqual(1, idx.Intros.Count);
            Assert.AreEqual(3, idx.Intros[0].Length);
            Assert.AreEqual("w_005501.mot", idx.Intros[0][1].Mot);
            Assert.AreEqual(110, idx.Intros[0][1].StartF);
            Assert.AreEqual(160, idx.Intros[0][1].EndF);
            Assert.AreEqual(95, idx.Frames("wdance0002.mot"));
            Assert.AreEqual(124, idx.Frames("wrest0001.mot"), "F 的名稱大小寫不敏感");
        }

        [Test]
        public void Index_Accepts_The_Old_Rangeless_Format_And_Drops_Broken_Ranges()
        {
            var old = DpsIndex.Parse("P a.mot\nI wdance0008.mot|wdance0062.mot\n");
            Assert.AreEqual(1, old.Intros.Count);
            Assert.IsFalse(old.Intros[0][0].HasRange, "V1 只有名字 → 沒有幀範圍（整支播）");

            var broken = DpsIndex.Parse("P a.mot\nI wdance0008.mot:10:5|wdance0062.mot:0:9\n");
            Assert.AreEqual(0, broken.Intros.Count, "壞掉的幀範圍 → 丟掉整組開場（半組會把兩支不相干的 clip 接在一起）");

            var junk = DpsIndex.Parse("P a.mot\nI wdance0008.mot:x:9\n");
            Assert.AreEqual(0, junk.Intros.Count);
        }

        [Test]
        public void Index_Missing_Or_Junk_Is_Empty_And_Frames_Fall_Back()
        {
            Assert.IsTrue(DpsIndex.Parse(null).IsEmpty, "索引檔不在 → 不生舞（沿用 fallback 動作），不是崩潰");
            Assert.IsTrue(DpsIndex.Parse("# nothing here\nV 2\nF bad\nF x.mot zero\n").IsEmpty);
            Assert.AreEqual(DpsIndex.DefaultFrames, DpsIndex.Parse("").Frames("wdance0002.mot"));
        }
    }
}
