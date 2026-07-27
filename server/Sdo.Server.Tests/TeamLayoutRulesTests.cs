using NUnit.Framework;
using Sdo.Net;

namespace Sdo.Tests
{
    /// <summary>
    /// 組隊站位的合法性規則。
    ///
    /// 🔴 為什麼這條規則必須存在:官方**只有三張**組隊座標表(2v2、3v3、2v2v2,逐字重製自
    /// EXE 的 0x582be8 / 0x582c78 / 0x582c30)。3v2、4v1、5 人 這些組合沒有站位資料。
    /// 使用者的決定是「湊不出來就不能開始遊戲」，而不是退回個人隊形 —— 退回會讓玩家以為
    /// 分隊生效了卻看到單人站位，那是靜默的錯誤行為。
    ///
    /// server 端在 requestStart 時獨立驗這一條(client 的預檢只是為了提早灰掉「開始」鈕)。
    /// </summary>
    public class TeamLayoutRulesTests
    {
        // ---- 合法的三種 ----

        [Test]
        public void Two_Teams_Of_Two_Is_V2v2()
        {
            TeamLayout l;
            Assert.IsTrue(TeamLayoutRules.TryLayoutFor(2, 2, 0, out l));
            Assert.AreEqual(TeamLayout.V2v2, l);
        }

        [Test]
        public void Two_Teams_Of_Three_Is_V3v3()
        {
            TeamLayout l;
            Assert.IsTrue(TeamLayoutRules.TryLayoutFor(3, 3, 0, out l));
            Assert.AreEqual(TeamLayout.V3v3, l);
        }

        [Test]
        public void Three_Teams_Of_Two_Is_V2v2v2()
        {
            TeamLayout l;
            Assert.IsTrue(TeamLayoutRules.TryLayoutFor(2, 2, 2, out l));
            Assert.AreEqual(TeamLayout.V2v2v2, l);
        }

        [Test]
        public void Which_Team_Letters_Are_Used_Does_Not_Matter()
        {
            // 玩家可能只選了 B 和 C(沒人選 A)—— 那還是合法的 2v2。
            // 實際的「隊伍 → 座標表 slot」映射由呼叫端按隊伍編號順序處理。
            TeamLayout l;
            Assert.IsTrue(TeamLayoutRules.TryLayoutFor(0, 2, 2, out l));
            Assert.AreEqual(TeamLayout.V2v2, l);

            Assert.IsTrue(TeamLayoutRules.TryLayoutFor(2, 0, 2, out l));
            Assert.AreEqual(TeamLayout.V2v2, l);

            Assert.IsTrue(TeamLayoutRules.TryLayoutFor(0, 3, 3, out l));
            Assert.AreEqual(TeamLayout.V3v3, l);
        }

        // ---- 🔴 湊不出來的必須擋掉 ----

        [Test]
        public void Uneven_Two_Team_Splits_Are_Rejected()
        {
            // 這是最常見的情況:6 人房裡 5 個人準備好。沒有 3v2 的座標表。
            TeamLayout l;
            Assert.IsFalse(TeamLayoutRules.TryLayoutFor(3, 2, 0, out l), "3v2 沒有官方座標表");
            Assert.AreEqual(TeamLayout.None, l);

            Assert.IsFalse(TeamLayoutRules.TryLayoutFor(4, 1, 0, out l), "4v1");
            Assert.IsFalse(TeamLayoutRules.TryLayoutFor(4, 2, 0, out l), "4v2");
            Assert.IsFalse(TeamLayoutRules.TryLayoutFor(1, 1, 0, out l), "1v1 也沒有表");
        }

        [Test]
        public void Uneven_Three_Team_Splits_Are_Rejected()
        {
            TeamLayout l;
            Assert.IsFalse(TeamLayoutRules.TryLayoutFor(2, 2, 1, out l), "2v2v1");
            Assert.IsFalse(TeamLayoutRules.TryLayoutFor(3, 2, 1, out l), "3v2v1");
            Assert.IsFalse(TeamLayoutRules.TryLayoutFor(1, 1, 1, out l), "1v1v1");
            Assert.IsFalse(TeamLayoutRules.TryLayoutFor(3, 3, 3, out l), "3v3v3 = 9 人,超過房間上限");
        }

        [Test]
        public void Everyone_On_One_Team_Is_Not_A_Team_Match()
        {
            // 全部同隊不是對戰 —— 沒有對手。
            TeamLayout l;
            Assert.IsFalse(TeamLayoutRules.TryLayoutFor(5, 0, 0, out l));
            Assert.IsFalse(TeamLayoutRules.TryLayoutFor(6, 0, 0, out l));
            Assert.IsFalse(TeamLayoutRules.TryLayoutFor(2, 0, 0, out l));
        }

        [Test]
        public void No_Players_Is_Rejected()
        {
            TeamLayout l;
            Assert.IsFalse(TeamLayoutRules.TryLayoutFor(0, 0, 0, out l));
        }

        [Test]
        public void Negative_Counts_Are_Rejected()
        {
            // 防呆:計數不該是負的,但如果哪裡算錯了，不要靜默給出一個版型。
            TeamLayout l;
            Assert.IsFalse(TeamLayoutRules.TryLayoutFor(-1, 2, 0, out l));
            Assert.IsFalse(TeamLayoutRules.TryLayoutFor(2, 2, -3, out l));
        }

        // ---- 陣列版 ----

        [Test]
        public void Array_Overload_Requires_Exactly_Three_Entries()
        {
            TeamLayout l;
            Assert.IsTrue(TeamLayoutRules.TryLayoutFor(new[] { 2, 2, 0 }, out l));
            Assert.AreEqual(TeamLayout.V2v2, l);

            Assert.IsFalse(TeamLayoutRules.TryLayoutFor(new[] { 2, 2 }, out l), "長度不對");
            Assert.IsFalse(TeamLayoutRules.TryLayoutFor(new[] { 2, 2, 0, 0 }, out l));
            Assert.IsFalse(TeamLayoutRules.TryLayoutFor(null, out l));
        }

        // ---- 人數 / 隊數 ----

        [Test]
        public void Totals_Match_The_Coordinate_Tables()
        {
            Assert.AreEqual(4, TeamLayoutRules.TotalDancers(TeamLayout.V2v2));
            Assert.AreEqual(6, TeamLayoutRules.TotalDancers(TeamLayout.V3v3));
            Assert.AreEqual(6, TeamLayoutRules.TotalDancers(TeamLayout.V2v2v2));
            Assert.AreEqual(0, TeamLayoutRules.TotalDancers(TeamLayout.None));

            Assert.AreEqual(2, TeamLayoutRules.TeamCount(TeamLayout.V2v2));
            Assert.AreEqual(2, TeamLayoutRules.TeamCount(TeamLayout.V3v3));
            Assert.AreEqual(3, TeamLayoutRules.TeamCount(TeamLayout.V2v2v2));
            Assert.AreEqual(0, TeamLayoutRules.TeamCount(TeamLayout.None));
        }

        // ---- 房主一鍵分隊 ----

        [Test]
        public void CanAssign_Requires_An_Exact_Player_Count()
        {
            // 4 個人選 3v3 是不行的 —— assignTeams 的 server 端驗證。
            Assert.IsTrue(TeamLayoutRules.CanAssign(TeamLayout.V2v2, 4));
            Assert.IsFalse(TeamLayoutRules.CanAssign(TeamLayout.V2v2, 5));
            Assert.IsFalse(TeamLayoutRules.CanAssign(TeamLayout.V2v2, 6));

            Assert.IsTrue(TeamLayoutRules.CanAssign(TeamLayout.V3v3, 6));
            Assert.IsFalse(TeamLayoutRules.CanAssign(TeamLayout.V3v3, 4));

            Assert.IsTrue(TeamLayoutRules.CanAssign(TeamLayout.V2v2v2, 6));
            Assert.IsFalse(TeamLayoutRules.CanAssign(TeamLayout.V2v2v2, 5));

            Assert.IsFalse(TeamLayoutRules.CanAssign(TeamLayout.None, 4));
        }

        [Test]
        public void AssignTeams_Deals_Round_Robin_By_Seat_Order()
        {
            // 輪流發牌而不是「前半 A 後半 B」—— 房間裡相鄰的人會被分到不同隊，
            // 比較符合「隨機分隊」的直覺。
            var v2v2 = TeamLayoutRules.AssignTeams(TeamLayout.V2v2, 4);
            Assert.AreEqual(new[] { 0, 1, 0, 1 }, v2v2);

            var v3v3 = TeamLayoutRules.AssignTeams(TeamLayout.V3v3, 6);
            Assert.AreEqual(new[] { 0, 1, 0, 1, 0, 1 }, v3v3);

            var v222 = TeamLayoutRules.AssignTeams(TeamLayout.V2v2v2, 6);
            Assert.AreEqual(new[] { 0, 1, 2, 0, 1, 2 }, v222);
        }

        [Test]
        public void AssignTeams_Result_Is_Itself_A_Legal_Layout()
        {
            // 自我一致性:一鍵分隊分出來的結果，一定要通得過 TryLayoutFor。
            // (否則會出現「房主按了分隊，然後按開始卻被 badTeams 擋住」這種荒謬情況。)
            foreach (var layout in new[] { TeamLayout.V2v2, TeamLayout.V3v3, TeamLayout.V2v2v2 })
            {
                int n = TeamLayoutRules.TotalDancers(layout);
                var assigned = TeamLayoutRules.AssignTeams(layout, n);
                Assert.IsNotNull(assigned, TeamLayoutRules.ToWire(layout));

                var counts = new int[TeamLayoutRules.MaxTeams];
                foreach (var t in assigned) counts[t]++;

                TeamLayout back;
                Assert.IsTrue(TeamLayoutRules.TryLayoutFor(counts, out back),
                    TeamLayoutRules.ToWire(layout) + " 分隊後應該仍是合法版型");
                Assert.AreEqual(layout, back, "而且應該是同一個版型");
            }
        }

        [Test]
        public void AssignTeams_Returns_Null_On_Mismatch()
        {
            Assert.IsNull(TeamLayoutRules.AssignTeams(TeamLayout.V3v3, 5));
            Assert.IsNull(TeamLayoutRules.AssignTeams(TeamLayout.None, 4));
        }

        // ---- wire ----

        [Test]
        public void Layouts_Round_Trip_Through_Wire()
        {
            foreach (var l in new[] { TeamLayout.None, TeamLayout.V2v2, TeamLayout.V3v3, TeamLayout.V2v2v2 })
            {
                TeamLayout back;
                Assert.IsTrue(TeamLayoutRules.TryParseLayout(TeamLayoutRules.ToWire(l), out back), TeamLayoutRules.ToWire(l));
                Assert.AreEqual(l, back);
            }
        }

        [Test]
        public void Wire_Names_Are_Stable()
        {
            // 這些字串是 wire format 的一部分。
            Assert.AreEqual("2v2", TeamLayoutRules.ToWire(TeamLayout.V2v2));
            Assert.AreEqual("3v3", TeamLayoutRules.ToWire(TeamLayout.V3v3));
            Assert.AreEqual("2v2v2", TeamLayoutRules.ToWire(TeamLayout.V2v2v2));
            Assert.AreEqual("none", TeamLayoutRules.ToWire(TeamLayout.None));
        }

        [Test]
        public void Unknown_Wire_Layout_Fails_To_None()
        {
            TeamLayout l;
            Assert.IsFalse(TeamLayoutRules.TryParseLayout("4v4", out l));
            Assert.AreEqual(TeamLayout.None, l);
            Assert.IsFalse(TeamLayoutRules.TryParseLayout("", out l));
            Assert.IsFalse(TeamLayoutRules.TryParseLayout(null, out l));
        }

        [Test]
        public void None_Is_Negative_So_Callers_Can_Test_For_Team_Mode_With_A_Sign_Check()
        {
            // 協定裡用 resolved.teamLayout >= 0 判斷「是不是組隊模式」,所以 None 必須是負的。
            Assert.Less((int)TeamLayout.None, 0);
            Assert.GreaterOrEqual((int)TeamLayout.V2v2, 0);
        }
    }
}
