using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    /// <summary>
    /// 歌曲收尾(<see cref="SongEndGate"/>):最後一顆音符打完之後,什麼時候切結算、音樂怎麼收。
    ///
    /// 🔴 這裡釘住的是實機回報的那個坑:osu 多曲包的譜只鋪了音檔的前半段(實例:最後音符 139.0 秒、
    /// 音檔 284.2 秒),舊規則在譜末 +1 秒把還在大聲播的音樂一刀切掉 —— 聽起來就是「一打完就跳結算」。
    /// 現在這種歌改成從譜末慢慢淡出 4 秒。**音檔跟著譜一起收的正常歌不受影響**(仍是等它播完 +1 秒,
    /// 不淡出),那條路壞掉的話全部的歌都會被多切掉 4 秒的尾奏。
    /// </summary>
    public class SongEndGateTests
    {
        // ---- 音檔跟著譜一起收(絕大多數的歌)= 維持原行為 ----

        [Test]
        public void No_Audio_Ends_One_Second_After_The_Last_Note()
        {
            // 觀察/爆發模式沒有音檔 → 沒有尾巴可等,也沒有東西可淡出。
            var plan = SongEndGate.For(notesEndMs: 100000, audibleEndMs: 0);
            Assert.AreEqual(101000, plan.EndAtMs, 1e-6);
            Assert.IsFalse(plan.FadesOut);
        }

        [Test]
        public void Audio_Shorter_Than_The_Chart_Ends_One_Second_After_The_Last_Note()
        {
            var plan = SongEndGate.For(notesEndMs: 100000, audibleEndMs: 98000);
            Assert.AreEqual(101000, plan.EndAtMs, 1e-6);
            Assert.IsFalse(plan.FadesOut);
        }

        [Test]
        public void A_Short_Outro_Is_Played_Out_In_Full_Then_One_Second()
        {
            // 尾巴 3 秒 → 等音樂自己播完(它自然就靜了,不需要淡出),再 +1 秒。
            var plan = SongEndGate.For(notesEndMs: 100000, audibleEndMs: 103000);
            Assert.AreEqual(104000, plan.EndAtMs, 1e-6);
            Assert.IsFalse(plan.FadesOut, "自然收尾的音樂不該被淡出蓋掉");
        }

        [Test]
        public void The_Threshold_Itself_Still_Plays_The_Outro_Out()
        {
            // 剛好 4 秒 = 還不算長尾奏(門檻是「超過」)。
            var plan = SongEndGate.For(notesEndMs: 100000, audibleEndMs: 100000 + SongEndGate.LongOutroMs);
            Assert.AreEqual(100000 + SongEndGate.LongOutroMs + SongEndGate.SettleMs, plan.EndAtMs, 1e-6);
            Assert.IsFalse(plan.FadesOut);
        }

        // ---- 長尾奏 = 從譜末淡出 4 秒 ----

        [Test]
        public void A_Long_Outro_Fades_Out_From_The_Last_Note()
        {
            var plan = SongEndGate.For(notesEndMs: 100000, audibleEndMs: 200000);
            Assert.IsTrue(plan.FadesOut);
            Assert.AreEqual(100000, plan.FadeStartMs, 1e-6, "淡出從最後一顆音符就開始");
            Assert.AreEqual(104000, plan.EndAtMs, 1e-6, "淡完 4 秒才進結算");
        }

        [Test]
        public void Just_Over_The_Threshold_Already_Fades()
        {
            var plan = SongEndGate.For(notesEndMs: 100000, audibleEndMs: 100000 + SongEndGate.LongOutroMs + 1);
            Assert.IsTrue(plan.FadesOut);
            Assert.AreEqual(100000 + SongEndGate.FadeMs, plan.EndAtMs, 1e-6);
        }

        [Test]
        public void The_Reported_Song_Fades_Instead_Of_Being_Cut_Off()
        {
            // easy ln pack [avicii - levels (skrillex remix)]:最後音符 139017ms、音檔 284.21 秒。
            const double notesEnd = 139017.0, musicEnd = 284210.0;
            var plan = SongEndGate.For(notesEnd, musicEnd);
            Assert.IsTrue(plan.FadesOut, "還有 145 秒沒播完,不能一刀切");
            Assert.AreEqual(139017.0, plan.FadeStartMs, 1e-6);
            Assert.AreEqual(143017.0, plan.EndAtMs, 1e-6);
            Assert.IsFalse(plan.EndedAt(142999.0), "淡出途中還沒結束");
            Assert.IsTrue(plan.EndedAt(143018.0));
        }

        // ---- 淡出曲線 ----

        [Test]
        public void Volume_Is_Untouched_Before_The_Fade_And_Silent_After()
        {
            var plan = SongEndGate.For(notesEndMs: 100000, audibleEndMs: 200000);
            Assert.AreEqual(1.0, plan.VolumeAt(0), 1e-9);
            Assert.AreEqual(1.0, plan.VolumeAt(99999), 1e-9);
            Assert.AreEqual(1.0, plan.VolumeAt(100000), 1e-9, "淡出的第一刻仍是原音量");
            Assert.AreEqual(0.0, plan.VolumeAt(104000), 1e-9);
            Assert.AreEqual(0.0, plan.VolumeAt(999999), 1e-9);
        }

        [Test]
        public void Volume_Falls_On_The_Perceptual_Square_Curve()
        {
            var plan = SongEndGate.For(notesEndMs: 100000, audibleEndMs: 200000);
            // 感知刻度等速 → 振幅是平方(與音量滑桿 AudioMix.Gain 同一條曲線)。
            Assert.AreEqual(0.5625, plan.VolumeAt(101000), 1e-9);   // 剩 0.75 → 0.5625
            Assert.AreEqual(0.25, plan.VolumeAt(102000), 1e-9);     // 剩 0.5  → 0.25
            Assert.AreEqual(0.0625, plan.VolumeAt(103000), 1e-9);   // 剩 0.25 → 0.0625
        }

        [Test]
        public void Volume_Decreases_Monotonically_Across_The_Whole_Fade()
        {
            var plan = SongEndGate.For(notesEndMs: 100000, audibleEndMs: 200000);
            double prev = 1.0;
            for (double t = 100000; t <= 104000; t += 50)
            {
                double v = plan.VolumeAt(t);
                Assert.LessOrEqual(v, prev + 1e-12, "t=" + t + " 音量回升了");
                Assert.GreaterOrEqual(v, 0.0);
                prev = v;
            }
        }

        [Test]
        public void A_Non_Fading_Song_Keeps_Full_Volume_To_The_End()
        {
            // 不淡出的歌:整段收尾都是原音量 —— 千萬不要因為「反正要結束了」就把它壓掉。
            var plan = SongEndGate.For(notesEndMs: 100000, audibleEndMs: 103000);
            Assert.AreEqual(1.0, plan.VolumeAt(100000), 1e-9);
            Assert.AreEqual(1.0, plan.VolumeAt(103500), 1e-9);
            Assert.AreEqual(1.0, plan.VolumeAt(104000), 1e-9);
        }
    }
}
