using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// 生成編舞（<see cref="RandomDps"/>）的兩個輸入是「這首**歌**的」，不是「玩家選到那張譜的」
    /// （<see cref="DanceInputs"/>）—— 一首歌只能有一支舞，簡單/普通/困難不能各生一支。
    /// 舞的長度＝所有難度的最早第一顆音符 → 最晚最後一顆。
    /// </summary>
    public class DanceInputsTests
    {
        // 同一首歌的三張譜：easy 晚進早收、hard 從頭打到尾，頭尾都不一樣。
        private static List<ChartWindow> ThreeDifficulties() => new List<ChartWindow>
        {
            new ChartWindow(12_000, 108_000),   // easy
            new ChartWindow(8_400, 121_500),    // normal
            new ChartWindow(5_200, 118_900),    // hard
        };

        [Test]
        public void Union_Is_The_Earliest_First_Note_To_The_Latest_Last_Note()
        {
            // 最早 5.2s（hard）→ 最晚 121.5s（normal），跨難度各取一端
            Assert.AreEqual((121_500 - 5_200) / 1000.0, DanceInputs.UnionSeconds(ThreeDifficulties()), 1e-9);
        }

        [Test]
        public void Union_Covers_Every_Single_Difficulty_Window()
        {
            double union = DanceInputs.UnionSeconds(ThreeDifficulties());
            foreach (var w in ThreeDifficulties())
                Assert.GreaterOrEqual(union, (w.LastMs - w.FirstMs) / 1000.0,
                                      "任何一個難度的譜都要塞得進這支舞，不然玩那個難度會跳到一半沒舞");
        }

        [Test]
        public void Union_Ignores_Charts_That_Measured_To_Nothing()
        {
            var withJunk = ThreeDifficulties();
            withJunk.Add(new ChartWindow(0, 0));              // 解析不出音符
            withJunk.Add(new ChartWindow(900_000, 1_000));    // 尾比頭早 → 壞資料

            Assert.AreEqual(DanceInputs.UnionSeconds(ThreeDifficulties()), DanceInputs.UnionSeconds(withJunk), 1e-9,
                            "壞掉的那格不該把舞拉長或壓扁");
        }

        [Test]
        public void Union_Of_Nothing_Is_Zero()
        {
            Assert.AreEqual(0.0, DanceInputs.UnionSeconds(new List<ChartWindow>()), 1e-9);
            Assert.AreEqual(0.0, DanceInputs.UnionSeconds(null), 1e-9);
        }

        [Test]
        public void Song_Window_And_Bpm_Win_Over_The_Loaded_Chart()
        {
            var i = DanceInputs.For(unionSeconds: 116.3, songBpm: 175.0, mapSpanSeconds: 96.0, mapBpm: 87.5);

            Assert.AreEqual(116.3, i.Seconds, 1e-9);
            Assert.AreEqual(175.0, i.Bpm, 1e-9);
            Assert.IsTrue(i.PerSong);
        }

        [Test]
        public void Different_Difficulties_Of_One_Song_Give_Identical_Inputs()
        {
            double union = DanceInputs.UnionSeconds(ThreeDifficulties());   // 三個難度都量到同一個 union

            var easy = DanceInputs.For(union, 175.0, mapSpanSeconds: 96.0, mapBpm: 87.5);
            var normal = DanceInputs.For(union, 175.0, mapSpanSeconds: 113.1, mapBpm: 175.0);
            var hard = DanceInputs.For(union, 175.0, mapSpanSeconds: 113.7, mapBpm: 350.0);

            Assert.AreEqual(easy.Seconds, normal.Seconds, 1e-9);
            Assert.AreEqual(easy.Seconds, hard.Seconds, 1e-9);
            Assert.AreEqual(easy.Bpm, normal.Bpm, 1e-9);
            Assert.AreEqual(easy.Bpm, hard.Bpm, 1e-9);
        }

        [Test]
        public void Unmeasurable_Charts_Fall_Back_To_This_Chart_Span()
        {
            var i = DanceInputs.For(unionSeconds: 0.0, songBpm: 175.0, mapSpanSeconds: 118.4, mapBpm: 87.5);

            Assert.AreEqual(118.4, i.Seconds, 1e-9, "一個難度都量不到 → 退回這張譜的 span（仍生得出舞）");
            Assert.AreEqual(175.0, i.Bpm, 1e-9, "BPM 這一邊還是用歌的");
            Assert.IsFalse(i.PerSong, "有一邊退回譜 → 這支舞就不保證跨難度一致了");
        }

        [Test]
        public void Unknown_Song_Bpm_Falls_Back_To_This_Chart_Bpm()
        {
            var i = DanceInputs.For(116.3, songBpm: -1.0, mapSpanSeconds: 118.4, mapBpm: 87.5);

            Assert.AreEqual(116.3, i.Seconds, 1e-9);
            Assert.AreEqual(87.5, i.Bpm, 1e-9);
            Assert.IsFalse(i.PerSong);
        }

        [Test]
        public void Nothing_Known_At_All_Is_Zero_Length_At_The_Default_Grid()
        {
            var i = DanceInputs.For(0.0, 0.0, 0.0, 0.0);

            Assert.AreEqual(0.0, i.Seconds, 1e-9, "0 秒 → 呼叫端（ExternalDps）判定太短，不生");
            Assert.AreEqual(DanceInputs.FallbackBpm, i.Bpm, 1e-9);
            Assert.IsFalse(i.PerSong);
        }

        // ---- 真正要的結果：三個難度生出來的 .dps 位元組完全一樣 ----

        [Test]
        public void One_Song_One_Dance_Whichever_Difficulty_Generates_It()
        {
            const uint seed = 0xc0ffeeu;   // seed 是歌的身分（資料夾+songKey），本來就跟難度無關
            double union = DanceInputs.UnionSeconds(ThreeDifficulties());

            byte[] fromEasy = Build(seed, DanceInputs.For(union, 175.0, mapSpanSeconds: 96.0, mapBpm: 87.5));
            byte[] fromNormal = Build(seed, DanceInputs.For(union, 175.0, mapSpanSeconds: 113.1, mapBpm: 175.0));
            byte[] fromHard = Build(seed, DanceInputs.For(union, 175.0, mapSpanSeconds: 113.7, mapBpm: 350.0));

            Assert.Greater(fromEasy.Length, 0);
            Assert.AreEqual(fromEasy, fromNormal, "先玩簡單還是先玩普通，生出來的舞必須一模一樣");
            Assert.AreEqual(fromEasy, fromHard, "困難也是同一支");
        }

        private static readonly string[] Pool = { "wdance0002.mot", "wdance0008.mot", "wdance0062.mot" };

        private static readonly Dictionary<string, int> FrameTable = new Dictionary<string, int>
        {
            { "wdance0002.mot", 95 }, { "wdance0008.mot", 214 }, { "wdance0062.mot", 1072 },
        };

        private static byte[] Build(uint seed, DanceInputs i) => RandomDps.Build(new RandomDpsRequest
        {
            Bpm = i.Bpm,
            DanceSeconds = i.Seconds,
            Pool = Pool,
            Groups = new List<IntroSlice[]>
            {
                new[] { new IntroSlice("wdance0002.mot", 0, 94), new IntroSlice("wdance0008.mot", 0, 213) },
                new[] { new IntroSlice("wdance0062.mot", 0, 599) },
            },
            FrameCount = m => FrameTable.TryGetValue(m, out int f) ? f : DpsIndex.DefaultFrames,
            Seed = seed,
            ChartName = "ext_test.gn",
        });
    }
}
