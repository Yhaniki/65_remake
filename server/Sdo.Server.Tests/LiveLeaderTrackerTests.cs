using NUnit.Framework;
using Sdo.Net.Server;
using Sdo.Server.Net;

namespace Sdo.Tests
{
    /// <summary>
    /// 權威 leader 的規則測試。這裡釘住的是 <see cref="LiveLeaderTracker"/> 的三層去震盪
    /// (同一時刻取樣 / 換人節流 / 決定性 tie-break),取代舊的「領先 300 分才換」——
    /// 那個門檻在高 combo 下(單筆判定就好幾千分)等於不存在,擋不住時間錯位造成的假超車。
    ///
    /// 兩人固定是 A = (userId 10, seat 0)、B = (userId 20, seat 1),所以開場 leader 是 A。
    /// 常數:對齊窗 500ms、換人節流 1000ms,都是**歌曲時間**。
    ///
    /// 每個 helper 送完 frame 就跑一次 <c>Resolve</c> —— server 每 200ms push 一次就是這個節奏,
    /// 所以測試看到的是真實的呼叫序列,而不是「攢一堆再算一次」的簡化版。
    /// </summary>
    [TestFixture]
    public class LiveLeaderTrackerTests
    {
        private const int A = 10;
        private const int B = 20;

        [Test]
        public void Initial_Leader_Is_Lowest_Seat_Then_UserId()
        {
            var tracker = new LiveLeaderTracker(new[]
            {
                Player(40, 4),
                Player(20, 1),
                Player(10, 1),
            });

            Assert.AreEqual(10, tracker.CurrentUserId);
        }

        [Test]
        public void Opening_Frames_Do_Not_Hand_The_Leader_To_Whoever_Reports_First()
        {
            var tracker = TwoPlayers();
            var active = Active();

            // 取樣點 = 0 − 500 = −500:那個時刻誰都還沒有分數,所以 B 報了多少都不算。
            Assert.AreEqual(A, Feed(tracker, active, 0, 0, 999999),
                "開場的領隊格由座位序決定,不是誰的第一筆 frame 先到");
        }

        [Test]
        public void A_Fresher_Opponent_Frame_Cannot_Steal_The_Leader_Until_The_Sample_Point_Reaches_It()
        {
            var tracker = TwoPlayers();
            var active = Active();

            Assert.AreEqual(A, Feed(tracker, active, 0, 0, 0));
            Assert.AreEqual(A, Feed(tracker, active, 800, 0, 0));
            Assert.AreEqual(A, Feed(tracker, active, 1600, 0, 0));

            // B 的 frame 跑到歌曲時間 2400 並爆了 50000 分,A 還停在 1600。
            // 舊版直接拿「B 在 2400 的分數」比「A 在 1600 的分數」→ 換人。但 A 在 1600→2400
            // 之間也可能爆同樣多分,只是還沒送到 —— 那是假超車,不該動領隊格。
            Assert.AreEqual(A, Record(tracker, active, B, 2400, 50000),
                "取樣點在 1900,B 的 50000 是 2400 的資料 —— 還不算數");

            // A 補上同一個時刻的資料(他真的沒得分)。取樣點沒有推進 → 還是不換。
            Assert.AreEqual(A, Record(tracker, active, A, 2400, 0));

            // 時間再走,取樣點 2400 終於看得到那 50000 —— 這才是真的超車。
            Assert.AreEqual(B, Feed(tracker, active, 2900, 0, 50000),
                "兩人在同一時刻的分數可比了,這時該換");
        }

        [Test]
        public void Leader_Switch_Is_Throttled_No_Matter_How_Big_The_Score_Jump_Is()
        {
            var tracker = TwoPlayers();
            var active = Active();

            Assert.AreEqual(A, Feed(tracker, active, 0, 0, 0));
            Assert.AreEqual(A, Feed(tracker, active, 1000, 0, 5000), "取樣點 500,兩人都還是 0");
            Assert.AreEqual(B, Feed(tracker, active, 1600, 0, 5000),
                "取樣點 1100 看得到 B 的 5000 → 換人");

            // A 反超 895000 分 —— 任何固定門檻都擋不住這種量級(高 combo 一筆判定就幾千分)。
            // 節流是**頻率**上限而不是分數門檻,所以它擋得住。
            Assert.AreEqual(B, Feed(tracker, active, 1900, 900000, 5000));
            Assert.AreEqual(B, Feed(tracker, active, 2400, 900000, 5000),
                "取樣點 1900 已經看到 A 領先 895000 分,但距離上次換位只有 800ms → 不准換");
            Assert.AreEqual(A, Feed(tracker, active, 2700, 900000, 5000),
                "取樣點 2200,距離上次換位 1100ms → 節流放行");
        }

        [Test]
        public void Equal_Scores_At_The_Sample_Point_Leave_The_Leader_Alone()
        {
            var tracker = TwoPlayers();
            var active = Active();

            Feed(tracker, active, 0, 0, 0);
            Feed(tracker, active, 1000, 7777, 7777);
            Assert.AreEqual(A, Feed(tracker, active, 1600, 7777, 7777), "同分不換位");

            // 讓 B 上去,再打回同分 —— 反向也不能換回來。
            Feed(tracker, active, 2600, 7777, 90000);
            Assert.AreEqual(B, Feed(tracker, active, 3200, 7777, 90000));

            Feed(tracker, active, 4300, 90000, 90000);
            Assert.AreEqual(B, Feed(tracker, active, 5400, 90000, 90000),
                "節流早就放行了,是「嚴格領先才換」讓同分維持現狀");
        }

        [Test]
        public void A_Dropped_Frame_Holds_The_Last_Known_Score_Instead_Of_Resetting_It()
        {
            var tracker = TwoPlayers();
            var active = Active();

            Feed(tracker, active, 0, 0, 0);
            Assert.AreEqual(A, Feed(tracker, active, 1000, 0, 8000));

            // B 之後的 frame 全掉了(lossy 送就是會這樣)。A 繼續推進歌曲時間。
            Assert.AreEqual(B, Record(tracker, active, A, 2000, 0),
                "取樣點 1500 拿 B 在 1000 的 8000(sample-and-hold)—— 掉包不等於 0 分");
            Assert.AreEqual(B, Record(tracker, active, A, 3000, 0));
        }

        [Test]
        public void A_Stalled_Player_Does_Not_Freeze_The_Ranking()
        {
            var tracker = TwoPlayers();
            var active = Active();

            Feed(tracker, active, 0, 0, 0);
            Feed(tracker, active, 1000, 0, 9000);
            Assert.AreEqual(B, Feed(tracker, active, 1600, 0, 9000));

            // B 卡住了(打完了 / 卡頓 / 掉線中),從此不再送 frame。A 一路追上。
            // 取樣點若取「所有人最新時間的**最小值**」就會永遠釘在 500 → 整場排名凍結。
            Assert.AreEqual(B, Record(tracker, active, A, 2600, 99999),
                "取樣點 2100 還看不到 A 的 99999");
            Assert.AreEqual(A, Record(tracker, active, A, 3600, 99999),
                "取樣點推進到 3100 → A 領先且節流放行。卡住的 B 不能把排名凍住");
        }

        [Test]
        public void Frame_Time_Going_Backwards_Does_Not_Corrupt_The_Series()
        {
            var tracker = TwoPlayers();
            var active = Active();

            Feed(tracker, active, 0, 0, 0);
            Feed(tracker, active, 900, 0, 0);
            Feed(tracker, active, 1000, 1000, 5000);

            // client 的時鐘回退(或同一個 tMs 重送)。這一筆不能插進序列尾巴 —— 序列亂序的話
            // 取樣會從那筆倒退的資料開始命中,分數會憑空跳。
            Assert.AreEqual(A, Record(tracker, active, B, 500, 111),
                "取樣點 500:B 在那時候是 0 分。倒退那筆若被 append 進尾巴,這裡會誤讀成 111");

            // 而倒退那筆帶的**分數**要生效(它是最新的真相),取代原本的 5000。
            Assert.AreEqual(A, Record(tracker, active, A, 1500, 1000),
                "取樣點 1000:B 是 111 而不是 5000,所以 A 的 1000 分還在領先");
        }

        [Test]
        public void Tied_Challengers_Are_Broken_Deterministically_By_Seat()
        {
            // 陣列順序故意與座位序相反 —— 挑人不能靠「先掃到誰」。
            var tracker = new LiveLeaderTracker(new[] { Player(30, 2), Player(20, 1), Player(10, 0) });
            var active = new[] { 10, 20, 30 };
            Assert.AreEqual(10, tracker.CurrentUserId);

            Record3(tracker, active, 0, 0, 0, 0);
            Record3(tracker, active, 1000, 0, 5000, 5000);

            Assert.AreEqual(20, Record3(tracker, active, 1600, 0, 5000, 5000),
                "兩個挑戰者同分 → 取座位序在前的那位,每台算出來才會是同一個人");
        }

        [Test]
        public void Removed_Leader_Is_Replaced_And_New_Tracker_Starts_Clean()
        {
            var players = new[] { Player(A, 0), Player(B, 1) };
            var active = Active();
            var tracker = new LiveLeaderTracker(players);

            Feed(tracker, active, 0, 0, 0);
            Feed(tracker, active, 1000, 0, 900);
            Assert.AreEqual(B, Feed(tracker, active, 1600, 0, 900));

            // leader 離場 = 補位,不受節流限制(領隊格不能空著)。只剩一個人時誰補位沒得選 ——
            // 「補位挑誰」由 Leader_Leaving_Is_Backfilled_By_The_Current_Top_Score 那條(三人)釘住。
            Assert.AreEqual(A, tracker.Resolve(new[] { A }));

            // 新的一場:序列與節流時限都要從零開始。
            // (「Hub 真的會為新的一場重建 tracker」是另一回事,由整合測試
            //  Frames_Carry_A_Time_Aligned_And_Throttled_Authoritative_Leader 的最後一段釘住 ——
            //  沿用舊 tracker 的話,第二場的 tMs 全部 ≤ 上一場最後一筆,Record 會一路走「覆蓋最後
            //  一筆」→ 時間軸永不前進 → 整首歌卡在上一場的殘留結果。)
            var nextMatch = new LiveLeaderTracker(players);
            Assert.AreEqual(A, Feed(nextMatch, active, 0, 0, 0));
            Assert.AreEqual(A, Feed(nextMatch, active, 1000, 0, 50000));
            Assert.AreEqual(B, Feed(nextMatch, active, 1600, 0, 50000),
                "第二場的第一次換位不該被上一場的節流時限擋住");
        }

        [Test]
        public void Leader_Leaving_Is_Backfilled_By_The_Current_Top_Score_And_Then_Throttled()
        {
            var tracker = new LiveLeaderTracker(new[] { Player(10, 0), Player(20, 1), Player(30, 2) });
            var all = new[] { 10, 20, 30 };

            Record3(tracker, all, 0, 0, 0, 0);
            Record3(tracker, all, 1000, 0, 9000, 5000);
            Assert.AreEqual(20, Record3(tracker, all, 1600, 0, 9000, 5000));
            Assert.AreEqual(20, Record3(tracker, all, 2600, 0, 9000, 5000));

            // leader(20)離場。補位要挑「取樣點 2100 上分數最高的」= 30 的 5000,
            // **不是**座位序最前的 10(那時候 0 分,全場最後一名)。挑錯人的話玩家會看到兩次換位:
            // 先滑到 10、等滿一輪節流才滑到真正的第一名 —— 而中央前排是鏡頭錨點,鏡頭也多跑一趟。
            var rest = new[] { 10, 30 };
            Assert.AreEqual(30, tracker.Resolve(rest), "補位挑當下最高分,不是挑座位序");

            // 補位也算動過一格 → 節流照樣 arm。10 在取樣點 2900 已經領先 994999 分,
            // 但距離補位只過了 800ms → 不准換。沒有這一層,斷線那一瞬間中央那格會連跳兩次。
            Assert.AreEqual(30, Record(tracker, rest, 10, 2800, 999999));
            Assert.AreEqual(30, Record(tracker, rest, 30, 3400, 5000),
                "補位之後節流要生效");

            Assert.AreEqual(10, Record(tracker, rest, 30, 3700, 5000),
                "節流只是延後,不是鎖死 —— 取樣點 3200 距離補位滿 1100ms 就放行");
        }

        [Test]
        public void No_One_Active_Means_No_Leader()
        {
            var tracker = TwoPlayers();
            Assert.AreEqual(0, tracker.Resolve(new int[0]));
            Assert.AreEqual(0, tracker.CurrentUserId);
        }

        private static LiveLeaderTracker TwoPlayers()
            => new LiveLeaderTracker(new[] { Player(A, 0), Player(B, 1) });

        private static int[] Active() => new[] { A, B };

        /// <summary>兩人在同一個歌曲時間各送一筆(最常見:大家的時鐘差不多齊),回傳當下的 leader。</summary>
        private static int Feed(LiveLeaderTracker tracker, int[] active, double tMs, long scoreA, long scoreB)
        {
            tracker.Record(active, A, tMs, scoreA);
            tracker.Record(active, B, tMs, scoreB);
            return tracker.Resolve(active);
        }

        /// <summary>只有一個人送了一筆(對手掉包 / 卡住 / 時鐘落後)。</summary>
        private static int Record(LiveLeaderTracker tracker, int[] active, int userId, double tMs, long score)
        {
            tracker.Record(active, userId, tMs, score);
            return tracker.Resolve(active);
        }

        private static int Record3(LiveLeaderTracker tracker, int[] active, double tMs,
                                   long score10, long score20, long score30)
        {
            tracker.Record(active, 10, tMs, score10);
            tracker.Record(active, 20, tMs, score20);
            tracker.Record(active, 30, tMs, score30);
            return tracker.Resolve(active);
        }

        private static NetMatchPlayerSnapshot Player(int userId, int seat)
            => new NetMatchPlayerSnapshot { UserId = userId, Seat = seat };
    }
}
