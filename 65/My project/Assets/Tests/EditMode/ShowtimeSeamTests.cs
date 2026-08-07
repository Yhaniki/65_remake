using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    /// <summary>
    /// ShowTime 自動視窗 → 手動的接縫。使用者回報:「自動的結尾我其實一直跟著按,可是就是會 miss;
    /// 自動長條按一半和一般按鍵都容易在自動結束的邊界斷掉。」這裡把接縫的記錄與兩條判準釘住。
    /// </summary>
    public class ShowtimeSeamTests
    {
        private sealed class Note   // 呼叫端的音符 handle 只當識別用,測試用最小替身
        {
            public readonly string Name;
            public Note(string name) { Name = name; }
            public override string ToString() => Name;
        }

        private static ShowtimeSeam<Note> Seam(double graceMs = 240.0)
            => new ShowtimeSeam<Note>(4) { GraceMs = graceMs };

        // ---- 視窗內的按鍵記錄 ----

        [Test]
        public void Records_Press_Time_And_The_Exact_Note_It_Aimed_At()
        {
            var s = Seam();
            var n = new Note("A");
            s.OnPress(2, 1234.5, n);

            var list = s.PressesFor(2);
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual(1234.5, list[0].AtMs, 1e-9);
            Assert.AreSame(n, list[0].Aimed);
        }

        [Test]
        public void Lanes_Are_Independent()
        {
            var s = Seam();
            s.OnPress(0, 100.0, new Note("A"));
            Assert.AreEqual(1, s.PressesFor(0).Count);
            Assert.AreEqual(0, s.PressesFor(1).Count);
        }

        [Test]
        public void Out_Of_Range_Lane_Is_Ignored_Not_Thrown()
        {
            var s = Seam();
            Assert.DoesNotThrow(() => s.OnPress(-1, 0.0, new Note("A")));
            Assert.DoesNotThrow(() => s.OnPress(99, 0.0, new Note("A")));
            Assert.DoesNotThrow(() => s.OnRelease(99, 0.0));
            Assert.AreEqual(0, s.PressesFor(99).Count);
            Assert.AreEqual(-1.0, s.ReleaseMsFor(99), 1e-9);
            Assert.IsFalse(s.ReleasedAfterLastPress(99));
        }

        // 舊版每軌只有單一 _stPressNote 插槽:視窗末尾連按兩下,第一下被第二下蓋掉,那顆音符沒人補判 → MISS。
        [Test]
        public void Keeps_Every_Press_In_The_Window_Not_Just_The_Last()
        {
            var s = Seam();
            var a = new Note("A"); var b = new Note("B");
            s.OnPress(1, 100.0, a);
            s.OnPress(1, 180.0, b);

            var list = s.PressesFor(1);
            Assert.AreEqual(2, list.Count);
            Assert.AreSame(a, list[0].Aimed);
            Assert.AreSame(b, list[1].Aimed);
        }

        [Test]
        public void Press_Buffer_Is_Capped_Dropping_The_Oldest()
        {
            var s = Seam();
            for (int i = 0; i < ShowtimeSeam<Note>.MaxPressesPerLane + 2; i++)
                s.OnPress(0, i * 50.0, new Note("N" + i));

            var list = s.PressesFor(0);
            Assert.AreEqual(ShowtimeSeam<Note>.MaxPressesPerLane, list.Count);
            Assert.AreEqual("N2", list[0].Aimed.Name);   // 最舊的兩筆被擠掉(它們最可能早就被自動打掉了)
        }

        [Test]
        public void A_Press_Aiming_At_Nothing_Is_Still_Recorded_As_Null()
        {
            var s = Seam();
            s.OnPress(0, 10.0, null);   // 附近沒有可打的音符 → 補判時什麼都不做,但不能當成「沒按過」
            Assert.AreEqual(1, s.PressesFor(0).Count);
            Assert.IsNull(s.PressesFor(0)[0].Aimed);
        }

        // ---- 放開:長條尾判要用真實放開時刻 ----

        [Test]
        public void Release_After_The_Last_Press_Means_The_Player_Let_Go_Inside_The_Window()
        {
            var s = Seam();
            s.OnPress(3, 100.0, new Note("H"));
            s.OnRelease(3, 260.0);

            Assert.IsTrue(s.ReleasedAfterLastPress(3));
            Assert.AreEqual(260.0, s.ReleaseMsFor(3), 1e-9);
        }

        [Test]
        public void Press_After_The_Last_Release_Means_The_Key_Is_Still_Down_At_The_Seam()
        {
            var s = Seam();
            s.OnPress(3, 100.0, new Note("H"));
            s.OnRelease(3, 150.0);
            s.OnPress(3, 200.0, new Note("I"));   // 放開後又按下 → 接縫時是按著的

            Assert.IsFalse(s.ReleasedAfterLastPress(3));
        }

        [Test]
        public void Never_Released_Is_Not_A_Release()
        {
            var s = Seam();
            s.OnPress(0, 100.0, new Note("H"));
            Assert.IsFalse(s.ReleasedAfterLastPress(0));
            Assert.AreEqual(-1.0, s.ReleaseMsFor(0), 1e-9);
        }

        // ---- 接縫的武裝 / 消化 / 寬限期 ----

        [Test]
        public void Seam_Is_Armed_By_Window_End_And_Consumed_In_One_Frame()
        {
            var s = Seam();
            Assert.IsFalse(s.JustEnded);

            s.MarkWindowEnded(5000.0);
            Assert.IsTrue(s.JustEnded);
            Assert.AreEqual(5000.0, s.EndedAtMs, 1e-9);

            s.ConsumeSeamFrame();
            Assert.IsFalse(s.JustEnded);   // 補判是一幀的事,不會每幀重播
        }

        [Test]
        public void Consuming_The_Seam_Frame_Clears_The_Latches_But_Keeps_The_Grace_Window()
        {
            var s = Seam(240.0);
            s.OnPress(0, 100.0, new Note("A"));
            s.OnRelease(0, 150.0);
            s.MarkWindowEnded(200.0);
            s.ConsumeSeamFrame();

            Assert.AreEqual(0, s.PressesFor(0).Count);
            Assert.IsFalse(s.ReleasedAfterLastPress(0));
            Assert.IsTrue(s.InGrace(300.0));   // 寬限期還在:接下來幾十 ms 的提早按鍵仍要被放過
        }

        [Test]
        public void Grace_Window_Expires_And_Is_Never_Open_Before_A_Window_Ends()
        {
            var s = Seam(240.0);
            Assert.IsFalse(s.InGrace(0.0));    // 從沒進過 ShowTime → 沒有寬限期

            s.MarkWindowEnded(1000.0);
            Assert.IsTrue(s.InGrace(1000.0));
            Assert.IsTrue(s.InGrace(1240.0));  // 邊界含在內
            Assert.IsFalse(s.InGrace(1240.1));
        }

        [Test]
        public void Clear_Resets_Everything()
        {
            var s = Seam();
            s.OnPress(0, 10.0, new Note("A"));
            s.OnRelease(0, 20.0);
            s.MarkWindowEnded(30.0);
            s.Clear();

            Assert.AreEqual(0, s.PressesFor(0).Count);
            Assert.IsFalse(s.JustEnded);
            Assert.AreEqual(-1.0, s.EndedAtMs, 1e-9);
            Assert.IsFalse(s.InGrace(30.0));
        }

        // ---- 規則一:寬限期內的提早按鍵要忽略,不能當場判 MISS ----
        // 判定窗(精2)= Perfect 59.85 / Cool 119.7 / Bad 179.55 / Miss 239.4ms。自動視窗把玩家的節奏帶偏,
        // 視窗一結束他沿用自動節奏按下去 → 抓到 200ms 後才到的音符 → 舊行為當場判死。

        [Test]
        public void Early_Press_Landing_In_The_Miss_Band_Is_Ignored_So_The_Note_Can_Still_Be_Hit()
        {
            // 音符在 1000ms,玩家在 800ms 按下(提早 200ms → 落在 Bad 與 MissBoundary 之間)
            Assert.IsTrue(ShowtimeSeamRules.IgnoreEarlyPress(Judgment.Miss, 1000.0, 800.0));
        }

        [Test]
        public void Late_Press_Landing_In_The_Miss_Band_Still_Misses()
        {
            // 音符早就過線了 —— 放過去就是漏打,不能因為在寬限期就免罰
            Assert.IsFalse(ShowtimeSeamRules.IgnoreEarlyPress(Judgment.Miss, 1000.0, 1200.0));
        }

        [Test]
        public void Real_Grades_Are_Never_Ignored()
        {
            foreach (var j in new[] { Judgment.Perfect, Judgment.Cool, Judgment.Bad })
                Assert.IsFalse(ShowtimeSeamRules.IgnoreEarlyPress(j, 1000.0, 800.0), "{0} 不該被忽略", j);
        }

        [Test]
        public void Out_Of_Range_Press_Is_Not_This_Rules_Business()
        {
            // JudgeHit 回 null = 根本沒打到任何音符,呼叫端本來就不判定
            Assert.IsFalse(ShowtimeSeamRules.IgnoreEarlyPress(null, 1000.0, 100.0));
        }

        [Test]
        public void Exactly_On_Time_Is_Not_Early()
        {
            Assert.IsFalse(ShowtimeSeamRules.IgnoreEarlyPress(Judgment.Miss, 1000.0, 1000.0));
        }

        // ---- 規則二:進行中的長條佔著該軌時,別去抓長條結束後才到的音符 ----

        [Test]
        public void A_Note_After_The_Running_Holds_End_Belongs_To_The_Hold_Not_To_A_New_Press()
        {
            // 自動幫你按住的長條在 2000ms 結束;你在視窗結束後按鍵想接手,手動路徑卻抓到 2400ms 那顆
            Assert.IsTrue(ShowtimeSeamRules.PressBelongsToRunningHold(2000.0, 2400.0));
        }

        [Test]
        public void A_Note_Before_The_Holds_End_Is_A_Genuine_Second_Note()
        {
            // 同一軌上長條還沒結束就有另一顆音符 → 那是真的要打的(疊譜),不能吞掉
            Assert.IsFalse(ShowtimeSeamRules.PressBelongsToRunningHold(2000.0, 1900.0));
        }

        [Test]
        public void A_Note_Exactly_At_The_Holds_End_Is_A_Genuine_Second_Note()
        {
            Assert.IsFalse(ShowtimeSeamRules.PressBelongsToRunningHold(2000.0, 2000.0));
        }
    }
}
