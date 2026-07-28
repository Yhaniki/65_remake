using NUnit.Framework;
using Sdo.Net;
using Sdo.Server.Net;

namespace Sdo.Tests
{
    /// <summary>
    /// 每連線的 rate limit(R19)。
    ///
    /// 為什麼值得測:它是「壞掉或惡意的 client 把 server 打爆」的唯一防線,但**同時也是
    /// 誤傷正常玩家的唯一來源**。兩個方向都要釘住:
    ///   • 超限要真的擋(不然防線是假的);
    ///   • 正常流量不能被擋,而且 strikes 要能歸零 ——
    ///     只會往上加的計數器會讓玩家「玩了一陣子突然斷線」,而且完全指不到任何一次爆量。
    ///
    /// 用注入的時間戳(不用真時鐘)→ 秒級跑完而且不會因為機器忙就飄。
    /// </summary>
    public class RateLimitTests
    {
        private RateCounters _r;

        /// <summary>
        /// 真實的 Unix 毫秒基準。
        ///
        /// 🔴 **不要用 1000 這種小數字當時間戳。** 視窗的錨點初值是 0,而判斷是
        /// `now - windowStart >= windowMs` —— 用小數字的話第一筆(now=1000)不會觸發換窗,
        /// 於是整個測試在一個「從 0 開始」的視窗裡跑,行為與線上完全不同
        /// (線上的 now 是 1.7e12,第一筆一定換窗並把錨點設成當下)。
        /// 我第一版就是用 1000,結果 chat 那條測出「視窗提早重置」——**是測試錯了,不是程式錯了**。
        /// </summary>
        private const long T0 = 1_700_000_000_000L;

        [SetUp]
        public void SetUp() => _r = new RateCounters();

        // ---- control:32/s ----

        [Test]
        public void Control_Allows_Exactly_The_Budget_Then_Blocks()
        {
            for (int i = 0; i < NetLimits.RateControlPerSec; i++)
                Assert.IsTrue(_r.AllowControl(T0), "第 " + (i + 1) + " 筆應該放行(額度 "
                                                     + NetLimits.RateControlPerSec + "/s)");
            Assert.IsFalse(_r.AllowControl(T0), "第 " + (NetLimits.RateControlPerSec + 1) + " 筆該被擋");
        }

        [Test]
        public void Control_Recovers_In_The_Next_Window()
        {
            for (int i = 0; i < NetLimits.RateControlPerSec; i++) _r.AllowControl(T0);
            Assert.IsFalse(_r.AllowControl(T0 + 999), "同一秒內還是擋");
            Assert.IsTrue(_r.AllowControl(T0 + 1000), "下一個視窗要放行 —— 不然玩家會被永久靜音");
        }

        // ---- 各種訊息走各自的桶 ----

        [Test]
        public void Each_Kind_Has_Its_Own_Budget()
        {
            // 🔴 這條很重要:走動(10/s + 換方向的 edge)如果跟 setReady/chatSay 搶同一個窗,
            // 搶輸的那個會被靜默丟掉,而且 strikes 還會累積到斷線。分開的桶才不會互相餓死。
            for (int i = 0; i < NetLimits.RateControlPerSec; i++) _r.AllowControl(T0);
            Assert.IsFalse(_r.AllowControl(T0), "control 已經用完");
            Assert.IsTrue(_r.AllowFrame(T0), "frame 是另一個桶");
            Assert.IsTrue(_r.AllowMove(T0), "move 是另一個桶");
            Assert.IsTrue(_r.AllowChat(T0), "chat 是另一個桶");
        }

        [Test]
        public void Frame_Budget_Covers_The_Real_Send_Rate()
        {
            // client 正常是 5/s(每 200ms 一筆)。額度給到 20 是留給 8 拍邊界那些額外的筆 ——
            // 這條測試釘住「正常流量不會被擋」,那比「超限會被擋」更容易在調參數時弄壞。
            Assert.GreaterOrEqual(NetLimits.RateFramePerSec, 10,
                "frame 額度不能低到擋掉正常的 5/s + 8 拍邊界的額外筆數");
            for (int i = 0; i < 10; i++)
                Assert.IsTrue(_r.AllowFrame(T0), "正常的分數流不該被擋(第 " + (i + 1) + " 筆)");
        }

        // ---- chat:每 3 秒 5 則 ----

        [Test]
        public void Chat_Uses_A_Multi_Second_Window()
        {
            for (int i = 0; i < NetLimits.RateChatPerWindow; i++)
                Assert.IsTrue(_r.AllowChat(T0), "第 " + (i + 1) + " 則該放行");
            Assert.IsFalse(_r.AllowChat(T0), "超過就擋");
            Assert.IsFalse(_r.AllowChat(T0 + NetLimits.RateChatWindowMs - 1), "視窗還沒過");
            Assert.IsTrue(_r.AllowChat(T0 + NetLimits.RateChatWindowMs), "視窗過了要放行");
        }

        // ---- 進度回報:500ms 節流 ----

        [Test]
        public void Availability_Progress_Is_Throttled_By_Interval_Not_Count()
        {
            // 這一條是「時間間隔」而不是「視窗計數」:進度回報是連續量,間隔才有意義。
            // client 自己也會節流,但 server 要獨立擋 —— 不能假設對方的 client 沒被改過。
            Assert.IsTrue(_r.AllowAvailProgress(T0), "第一筆放行");
            Assert.IsFalse(_r.AllowAvailProgress(T0 + NetLimits.AvailProgressThrottleMs - 1), "太快");
            Assert.IsTrue(_r.AllowAvailProgress(T0 + NetLimits.AvailProgressThrottleMs), "間隔到了");
        }

        // ---- strikes ----

        [Test]
        public void Strikes_Are_The_Callers_Job_And_Must_Be_Clearable()
        {
            // RateCounters 只負責「這一筆放不放行」;累計/歸零 strikes 是 Hub 的職責
            // (HandleFrame:擋下就 Strikes++、放行就歸零,超過 20 才斷線)。
            // 🔴 歸零那一步不能漏 —— 不歸零的話它是一個**只會往上加的計數器**:
            //    正常玩家偶爾爆一下(切畫面時一批訊息擠在一起)累積個二十次就會莫名被踢,
            //    而症狀是「玩了一陣子突然斷線」,完全指不到任何一次爆量。
            Assert.AreEqual(0, _r.Strikes, "一開始是 0");
            _r.Strikes++;
            _r.Strikes = 0;
            Assert.AreEqual(0, _r.Strikes, "可以歸零");
        }

        [Test]
        public void A_Fresh_Connection_Starts_With_A_Full_Budget()
        {
            // 視窗起點是 0 而 nowMs 是 Unix 毫秒(很大)→ 第一筆一定落在新視窗。
            // 寫死成「第一筆就擋」的實作會讓每條新連線的 hello 被丟掉,而那看起來像「連不上」。
            var fresh = new RateCounters();
            Assert.IsTrue(fresh.AllowControl(T0), "新連線的第一筆 control 一定要放行");
            Assert.IsTrue(fresh.AllowFrame(T0));
            Assert.IsTrue(fresh.AllowChat(T0));
            Assert.IsTrue(fresh.AllowMove(T0));
        }
    }
}
