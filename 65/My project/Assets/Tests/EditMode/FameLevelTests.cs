using NUnit.Framework;
using Sdo.Settings;

namespace Sdo.Tests
{
    /// <summary>
    /// 知名度的等級換算與「買東西加幾點」的政策。
    ///
    /// 這條測試守的是大廳右下角那一行字:官方畫面實測是 <c>LV 1 (0)</c> 與 <c>LV 2 (15)</c>,
    /// 門檻表挪動一格、或格式少了一個空格,顯示出來的還是「看起來很像」的字串 —— 沒有人會發現它跟官方對不上。
    /// 另一半守的是量級:知名度是購物累加的,換算選錯一個數量級的話,買第一件衣服就會直接頂天。
    /// </summary>
    public class FameLevelTests
    {
        // ---- 官方那兩個資料點 ----

        [Test]
        public void Official_Data_Points_Match()
        {
            // 門檻表第 0、1 項不是我們挑的,是從官方畫面對出來的。
            Assert.AreEqual(1, FameLevel.LevelFor(0));
            Assert.AreEqual(2, FameLevel.LevelFor(15));
        }

        [Test]
        public void Label_Format_Is_LV_Level_Space_Paren_Total()
        {
            // 逐字對官方:LV + 空格 + 等級 + 空格 + (累計值)。括號裡是**累計**,不是距離下一級的差。
            Assert.AreEqual("LV 2 (15)", FameLevel.Label(15));
            Assert.AreEqual("LV 1 (0)", FameLevel.Label(0));
        }

        [Test]
        public void Level_Holds_Until_The_Next_Threshold()
        {
            // 14 還在 LV 1(門檻是「達到才升」,不是「超過才升」)。
            Assert.AreEqual(1, FameLevel.LevelFor(14));
            Assert.AreEqual(2, FameLevel.LevelFor(16));
            Assert.AreEqual("LV 1 (14)", FameLevel.Label(14));
        }

        // ---- 門檻表本身 ----

        [Test]
        public void Thresholds_Are_Strictly_Increasing()
        {
            // 「嚴格遞增」在外部看得到的形狀 = 等級隨累計值只會往上、而且一次只跳一級
            //(有兩個門檻相同的話,會出現一次跳兩級)。掃到表尾之外一點,順便確認封頂。
            int prev = FameLevel.LevelFor(0);
            Assert.AreEqual(1, prev);
            for (int fame = 1; fame <= 4000; fame++)
            {
                int lv = FameLevel.LevelFor(fame);
                Assert.GreaterOrEqual(lv, prev, "等級不該隨累計值下降 (fame=" + fame + ")");
                Assert.LessOrEqual(lv - prev, 1, "一次跳超過一級 = 有兩個門檻撞在一起 (fame=" + fame + ")");
                prev = lv;
            }
            Assert.AreEqual(FameLevel.MaxLevel, prev, "掃到 4000 應該已經到頂");
        }

        [Test]
        public void Levels_Reach_Into_The_Teens()
        {
            // 需求:往上要排到十幾級。只有三四級的話等級這個東西沒什麼好看的。
            Assert.GreaterOrEqual(FameLevel.MaxLevel, 12);
        }

        [Test]
        public void Level_Caps_At_MaxLevel()
        {
            // 表外不外插(表外的門檻是編的)→ 天文數字也停在最高級,不會冒出 LV 99。
            Assert.AreEqual(FameLevel.MaxLevel, FameLevel.LevelFor(int.MaxValue));
        }

        // ---- 壞資料 ----

        [Test]
        public void Negative_Fame_Is_Treated_As_Zero()
        {
            // 照 PlayStats.AddPlay 的「負數一律當 0」——壞掉的來源不該讓畫面顯示 LV 0 那種不存在的等級。
            Assert.AreEqual(1, FameLevel.LevelFor(-1));
            Assert.AreEqual(1, FameLevel.LevelFor(int.MinValue));
            Assert.AreEqual("LV 1 (0)", FameLevel.Label(-50));
        }

        [Test]
        public void Sanitize_Clamps_Negative_Fame()
        {
            var p = new UserProfile { fame = -1 };
            p.Sanitize();
            Assert.AreEqual(0, p.fame);
        }

        [Test]
        public void Sanitize_Keeps_A_Valid_Fame()
        {
            var p = new UserProfile { fame = 15 };
            p.Sanitize();
            Assert.AreEqual(15, p.fame);
        }

        // ---- 購物換算 ----

        [Test]
        public void Free_And_Undefined_Prices_Give_Nothing()
        {
            // ShopItem.Price 可能是 0(免費)或 -1(未定義的 placeholder)。沒花錢就不該漲知名度,
            // 否則免費/壞價的消耗品(可以重複買)就是一個刷等級的入口。
            Assert.AreEqual(0, FameLevel.FameForPurchase(0));
            Assert.AreEqual(0, FameLevel.FameForPurchase(-1));
            Assert.AreEqual(0, FameLevel.FameForPurchase(int.MinValue));
        }

        [Test]
        public void Cheap_Purchase_Still_Gives_One_Point()
        {
            // 便宜的東西不會因為除下去變 0 而完全沒回饋 —— 花了錢就至少 1 點。
            Assert.AreEqual(1, FameLevel.FameForPurchase(1));
            Assert.AreEqual(1, FameLevel.FameForPurchase(FameLevel.PricePerFame - 1));
            Assert.AreEqual(1, FameLevel.FameForPurchase(FameLevel.PricePerFame));
        }

        [Test]
        public void Expensive_Purchase_Scales_With_Price()
        {
            Assert.AreEqual(3, FameLevel.FameForPurchase(FameLevel.PricePerFame * 3));
            Assert.AreEqual(3, FameLevel.FameForPurchase(FameLevel.PricePerFame * 4 - 1));   // 無條件捨去
        }

        [Test]
        public void Fifteen_Ordinary_Garments_Reach_Level_Two()
        {
            // 官方 LV 2 (15) 的量級對照:一件普通衣服(約 1000 元上下)≈ 1 點 → 十幾件才升到 LV 2。
            // 這條釘的是換算的「數量級」;真的買一件就跳好幾級的話,等級就沒有意義了。
            int fame = 0;
            for (int i = 0; i < 14; i++) fame += FameLevel.FameForPurchase(900);
            Assert.AreEqual(1, FameLevel.LevelFor(fame), "買 14 件還不該升級");
            fame += FameLevel.FameForPurchase(900);
            Assert.AreEqual(2, FameLevel.LevelFor(fame), "第 15 件剛好到官方的 LV 2 (15)");
            Assert.AreEqual("LV 2 (15)", FameLevel.Label(fame));
        }
    }
}
