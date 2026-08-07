using NUnit.Framework;
using Sdo.Net;

namespace Sdo.Tests
{
    /// <summary>
    /// ShowTime 釋放事件的合法範圍(client 與 server 編譯同一份 <see cref="ShowtimeReleaseRules"/>)。
    ///
    /// 為什麼這些常數值得釘住:這則事件的三個欄位會直接驅動**別人畫面上**那隻舞者 ——
    /// 壞掉的 windowMs 會讓對方的舞者卡在街舞裡整首歌、光環永遠不熄;
    /// 壞掉的 level/variant 會去讀不存在的 BREAKING_?_?.DPS。
    /// 所以「server 不轉發不合法的值」與「client 收到怪值也要夾回來」兩件事都要有測試。
    /// </summary>
    public class ShowtimeReleaseRulesTests
    {
        [Test]
        public void Level_Accepts_The_Three_Official_Tiers_Only()
        {
            // 官方只有三段(綠/黃/紅 → breaking_E / _N / _H)。
            Assert.IsTrue(ShowtimeReleaseRules.IsValidLevel(0));
            Assert.IsTrue(ShowtimeReleaseRules.IsValidLevel(1));
            Assert.IsTrue(ShowtimeReleaseRules.IsValidLevel(2));
            Assert.IsFalse(ShowtimeReleaseRules.IsValidLevel(-1));
            Assert.IsFalse(ShowtimeReleaseRules.IsValidLevel(3));
        }

        [Test]
        public void Variant_Spans_The_Union_Of_The_Official_Rolls()
        {
            // 官方 E 骰 1..6、N/H 骰 1..8 → 聯集 1..8。0 是「沒骰過」的哨兵,不是合法變體。
            Assert.IsFalse(ShowtimeReleaseRules.IsValidVariant(0));
            Assert.IsTrue(ShowtimeReleaseRules.IsValidVariant(1));
            Assert.IsTrue(ShowtimeReleaseRules.IsValidVariant(8));
            Assert.IsFalse(ShowtimeReleaseRules.IsValidVariant(9));
        }

        [Test]
        public void Variant_Upper_Bound_Follows_The_Tier_Because_E_Only_Has_Six()
        {
            // 資產就是這樣:BREAKING_E_1..6、BREAKING_N|H_1..8。E 配 7/8 對不到檔案 ——
            // 症狀會是「他有光環但沒跳街舞」,所以在轉發前就要擋掉。
            Assert.AreEqual(6, ShowtimeReleaseRules.MaxVariantFor(0));
            Assert.AreEqual(8, ShowtimeReleaseRules.MaxVariantFor(1));
            Assert.AreEqual(8, ShowtimeReleaseRules.MaxVariantFor(2));

            Assert.IsTrue(ShowtimeReleaseRules.IsValidPair(0, 6));
            Assert.IsFalse(ShowtimeReleaseRules.IsValidPair(0, 7));
            Assert.IsTrue(ShowtimeReleaseRules.IsValidPair(1, 8));
            Assert.IsFalse(ShowtimeReleaseRules.IsValidPair(1, 9));

            Assert.AreEqual(6, ShowtimeReleaseRules.ClampVariant(0, 8));
            Assert.AreEqual(8, ShowtimeReleaseRules.ClampVariant(2, 8));
        }

        [Test]
        public void Window_Covers_A_Slow_Song_Pas_Window()
        {
            // 視窗 = 檔位預算(最長 18000ms)往上進位到整段 pas(8 拍)。BPM 30 的歌一段就 16 秒,
            // 進位後 32 秒 —— 這是**合法**的長度,上界不能訂得比它低。
            Assert.IsTrue(ShowtimeReleaseRules.IsValidWindowMs(32000.0));
            Assert.IsTrue(ShowtimeReleaseRules.IsValidWindowMs(ShowtimeReleaseRules.MinWindowMs));
            Assert.IsTrue(ShowtimeReleaseRules.IsValidWindowMs(ShowtimeReleaseRules.MaxWindowMs));
            Assert.IsFalse(ShowtimeReleaseRules.IsValidWindowMs(0.0));
            Assert.IsFalse(ShowtimeReleaseRules.IsValidWindowMs(-1.0));
            Assert.IsFalse(ShowtimeReleaseRules.IsValidWindowMs(ShowtimeReleaseRules.MaxWindowMs + 1.0));
        }

        [Test]
        public void IsValid_Requires_All_Three_Fields()
        {
            Assert.IsTrue(ShowtimeReleaseRules.IsValid(1, 4, 12000.0));
            Assert.IsFalse(ShowtimeReleaseRules.IsValid(5, 4, 12000.0));
            Assert.IsFalse(ShowtimeReleaseRules.IsValid(1, 0, 12000.0));
            Assert.IsFalse(ShowtimeReleaseRules.IsValid(0, 7, 12000.0));   // E 只有 6 支
            Assert.IsFalse(ShowtimeReleaseRules.IsValid(1, 4, 999999.0));
        }

        [Test]
        public void Clamps_Bring_Hostile_Values_Back_Into_Range()
        {
            // 收端的正確反應是「夾回來」而不是「不畫」—— 特效沒出現比舞者壞掉難查。
            Assert.AreEqual(0, ShowtimeReleaseRules.ClampLevel(-3));
            Assert.AreEqual(ShowtimeReleaseRules.MaxLevel, ShowtimeReleaseRules.ClampLevel(99));
            Assert.AreEqual(ShowtimeReleaseRules.MinVariant, ShowtimeReleaseRules.ClampVariant(1, 0));
            Assert.AreEqual(ShowtimeReleaseRules.MaxVariant, ShowtimeReleaseRules.ClampVariant(1, 1000));
            Assert.AreEqual(ShowtimeReleaseRules.MinWindowMs, ShowtimeReleaseRules.ClampWindowMs(-5.0));
            Assert.AreEqual(ShowtimeReleaseRules.MaxWindowMs, ShowtimeReleaseRules.ClampWindowMs(1e9));
            Assert.AreEqual(12000.0, ShowtimeReleaseRules.ClampWindowMs(12000.0));
        }

        [Test]
        public void First_Release_Of_A_Match_Is_Always_Accepted()
        {
            // 0 = 這一場還沒放行過他的任何一則(server 每場清空那張表)。
            Assert.IsTrue(ShowtimeReleaseRules.AcceptsAt(0.0, 1.0));
        }

        [Test]
        public void Flood_Guard_Rejects_Back_To_Back_Releases_But_Allows_A_Real_Second_Window()
        {
            double last = 100000.0;
            Assert.IsFalse(ShowtimeReleaseRules.AcceptsAt(last, last + 1.0));
            Assert.IsFalse(ShowtimeReleaseRules.AcceptsAt(last, last + ShowtimeReleaseRules.MinIntervalMs - 1.0));
            Assert.IsTrue(ShowtimeReleaseRules.AcceptsAt(last, last + ShowtimeReleaseRules.MinIntervalMs));

            // 真正的第二次釋放:視窗本身至少 8 秒、之後還要重新集滿一整段氣 —— 遠遠超過門檻,
            // 這條防洪絕不可以擋到正常玩法。
            Assert.IsTrue(ShowtimeReleaseRules.AcceptsAt(last, last + 8000.0));
        }
    }
}
