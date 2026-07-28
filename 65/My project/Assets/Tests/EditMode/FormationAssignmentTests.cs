using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    /// <summary>
    /// 分數 → 隊形 slot 的指派。
    ///
    /// 🔴 為什麼這組測試重要:每一台 client 都各自算一次站位,結果**必須一樣**。
    /// 不一樣的話同一個人在不同人的畫面上站不同格 —— 而那不會有任何錯誤訊息,
    /// 只會有「他的角色在我這邊站中間、在他自己那邊站旁邊」這種沒人回報得清楚的怪事。
    /// 所以「同分怎麼辦」不能靠「剛好」,要有明文規則並且釘住。
    /// </summary>
    public class FormationAssignmentTests
    {
        [Test]
        public void With_No_Dancers_Nothing_Blows_Up()
        {
            CollectionAssert.IsEmpty(FormationAssignment.SlotForDancer(new long[0]));
            Assert.AreEqual(-1, FormationAssignment.TopScorer(new long[0]));
            CollectionAssert.IsEmpty(FormationAssignment.SlotForDancer(null));
        }

        [Test]
        public void A_Solo_Dancer_Takes_The_Leader_Slot()
        {
            CollectionAssert.AreEqual(new[] { 0 }, FormationAssignment.SlotForDancer(new long[] { 0 }));
        }

        [Test]
        public void Everyone_Starts_In_Seat_Order_When_The_Leader_Is_Already_Winning()
        {
            // 舞者 0 分數最高 → 不用換位,每個人都在自己的格子。
            var slots = FormationAssignment.SlotForDancer(new long[] { 900, 500, 100 });
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, slots);
        }

        [Test]
        public void The_Current_Leader_Slides_Into_Slot_Zero_And_Swaps_With_Whoever_Was_There()
        {
            // 舞者 2 是第一名 → 它去 slot 0,而原本在 slot 0 的舞者 0 去 slot 2(互換,不是整排推移)。
            var slots = FormationAssignment.SlotForDancer(new long[] { 300, 200, 900 });
            CollectionAssert.AreEqual(new[] { 2, 1, 0 }, slots);
        }

        [Test]
        public void Every_Slot_Is_Used_Exactly_Once()
        {
            // 這是「指派」而不是「排序」:六個人一定剛好占滿六格,不能有人重複或漏格
            // (重複 = 兩個人站同一點疊在一起;漏格 = 隊形看起來缺一個人)。
            var slots = FormationAssignment.SlotForDancer(new long[] { 10, 90, 20, 80, 30, 70 });
            var seen = new bool[slots.Length];
            foreach (var s in slots)
            {
                Assert.GreaterOrEqual(s, 0);
                Assert.Less(s, slots.Length);
                Assert.IsFalse(seen[s], "slot " + s + " 被指派了兩次");
                seen[s] = true;
            }
        }

        [Test]
        public void A_Tie_Goes_To_The_Lower_Dancer_Index()
        {
            // 🔴 決定性的規則。兩台各挑一個人當第一名的話,那個人的站位在兩台上就不一樣。
            Assert.AreEqual(0, FormationAssignment.TopScorer(new long[] { 500, 500, 500 }));
            Assert.AreEqual(1, FormationAssignment.TopScorer(new long[] { 100, 500, 500 }));

            var slots = FormationAssignment.SlotForDancer(new long[] { 100, 500, 500 });
            CollectionAssert.AreEqual(new[] { 1, 0, 2 }, slots, "同分時由座位序在前的那位(舞者 1)進領隊格");
        }

        [Test]
        public void Everyone_On_Zero_Keeps_Seat_Order()
        {
            // 開場那一刻大家都是 0 分 —— 這時就該是「照座位序」,而不是隨便挑一個人站中間。
            var slots = FormationAssignment.SlotForDancer(new long[] { 0, 0, 0, 0, 0, 0 });
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5 }, slots);
        }

        [Test]
        public void The_Slide_Converges_Towards_The_Target_Without_Overshooting()
        {
            // 官方是每幀 cur*0.9 + target*0.1(固定比例,不是時間相關)。
            float cur = 0f;
            for (int i = 0; i < 200; i++) cur = FormationAssignment.SlideStep(cur, 100f);
            Assert.AreEqual(100f, cur, 0.5f, "應該收斂到目標");

            // 單步不能越過目標(不然會來回抖)
            float one = FormationAssignment.SlideStep(0f, 100f);
            Assert.Greater(one, 0f);
            Assert.Less(one, 100f);
            Assert.AreEqual(10f, one, 0.001f);
        }

        [Test]
        public void The_Slide_Also_Works_Backwards()
        {
            // 被擠掉的人要往回走 —— 同一個算式,方向相反。
            float cur = 100f;
            for (int i = 0; i < 200; i++) cur = FormationAssignment.SlideStep(cur, 0f);
            Assert.AreEqual(0f, cur, 0.5f);
        }
    }
}
