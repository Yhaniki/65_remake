namespace Sdo.Settings
{
    /// <summary>
    /// 知名度(名声)的等級政策 —— 「累計知名度 → 等級」與「買一件東西加幾點」這兩條規則的**唯一一份**。
    ///
    /// 為什麼抽成一個 static class:大廳右下角要顯示、商城買東西要累加、之後個人資料頁大概也會用到,
    /// 這條換算一旦散在三個畫面裡就一定會各長各的(門檻不一致 → 同一個數字在兩個地方顯示成不同等級)。
    /// 做法照 <see cref="PlayStats.RecordsWinLoss"/> 那個先例:政策是**純函式**,不碰 Unity 也不碰磁碟,測得動。
    ///
    /// 🔴 <c>Sdo.Settings</c> 是 leaf assembly(asmdef references 只有 <c>Sdo.Net</c>),**不能碰 Sdo.Shop 的任何型別**
    /// (同樣的約束見 <see cref="EquipSave"/> 為什麼把 slot 存成裸 int)。所以這個檔一律只吃 <c>int</c> ——
    /// 呼叫端自己從 <c>ShopItem.Price</c> 取出數字再傳進來。
    /// </summary>
    public static class FameLevel
    {
        /// <summary>
        /// 每一級的**起始**累計知名度(index 0 = LV 1)。嚴格遞增。
        ///
        /// 前兩項 0 / 15 是**官方畫面實值**:大廳右下角出現過 <c>LV 1 (0)</c> 與 <c>LV 2 (15)</c>,
        /// 所以第 0、1 項不是我們挑的,是對出來的。其餘往上是自訂的成長曲線。
        ///
        /// 曲線怎麼長的:每一級所需的**增量**是 15 → 25 → 40 → 60 → 85 → …,增量的增量固定 +5
        /// (10、15、20、25…)。搭配 <see cref="FameForPurchase"/> 的「約 1 件衣服 1 點」,
        /// 讀起來就是「升 LV2 要買 15 件、LV3 再買 25 件、LV4 再買 40 件…」——
        /// 前幾級升得快(新玩家逛個商城就能動),後面愈來愈慢(不會逛一天就頂天)。
        /// 排到 LV 15(2940 點 ≈ 買三千件)已經遠超過離線版玩得到的量,再往上排只是憑空編數字。
        /// </summary>
        private static readonly int[] Thresholds =
        {
            0,      // LV 1  ← 官方
            15,     // LV 2  ← 官方
            40,     // LV 3
            80,     // LV 4
            140,    // LV 5
            225,    // LV 6
            340,    // LV 7
            490,    // LV 8
            680,    // LV 9
            915,    // LV 10
            1200,   // LV 11
            1540,   // LV 12
            1940,   // LV 13
            2405,   // LV 14
            2940,   // LV 15
        };

        /// <summary>最高等級(= 門檻表長度)。超過最後一項的累計值一律停在這一級,不外插 ——
        /// 表外的門檻是編的,與其顯示一個沒有依據的 LV 99,不如封頂。</summary>
        public static int MaxLevel { get { return Thresholds.Length; } }

        /// <summary>
        /// 累計知名度 → 等級(從 1 起算)。
        /// 負數/壞資料一律當 0(照 <see cref="PlayStats.AddPlay"/> 的「負數一律當 0」——
        /// 壞掉的來源不該把顯示拉到 LV 0 那種不存在的等級)。
        /// </summary>
        public static int LevelFor(int fame)
        {
            if (fame < 0) fame = 0;
            int lv = 1;
            for (int i = 1; i < Thresholds.Length; i++)
            {
                if (fame < Thresholds[i]) break;   // 門檻嚴格遞增 → 第一個沒跨過的就是上限,可以直接停
                lv = i + 1;
            }
            return lv;
        }

        /// <summary>
        /// 大廳右下角那一行的完整字串,格式逐字照官方:<c>LV 2 (15)</c>
        /// (LV 與數字之間一個空格、等級與括號之間一個空格、括號內是**累計值**而不是距離下一級的差)。
        /// 負數同樣當 0 → <c>LV 1 (0)</c>。
        /// </summary>
        public static string Label(int fame)
        {
            if (fame < 0) fame = 0;
            return "LV " + LevelFor(fame) + " (" + fame + ")";
        }

        /// <summary>
        /// 買一件商品該加多少知名度。<paramref name="price"/> 傳 <c>ShopItem.Price</c> 的原值即可。
        ///
        /// 換算 = **每 <see cref="PricePerFame"/> 元 1 點,無條件捨去,但只要真的花了錢就至少 1 點**。
        /// 為什麼不是直接加價格:官方那個 <c>LV 2 (15)</c> 只有 15 點,而衣服動輒上千元 ——
        /// 直接加價格買第一件就會衝到表尾,等級失去意義。除以 1000 之後「一件普通衣服 ≈ 1 點」,
        /// 15 點剛好是「買了十幾件」,對得上官方的量級;貴的套裝(三千多)則一次給 3 點,還是有差別。
        ///
        /// 🔴 <c>Price</c> 可能是 <b>0(免費)</b> 或 <b>-1(未定義的 placeholder)</b>(見 <c>ShopItem.Price</c> 原註解),
        /// 這兩種一律給 0 點:沒花錢就不該漲知名度,否則免費/壞價的商品會變成刷等級的入口
        /// (消耗品可以重複買、不受「已擁有」擋)。
        /// </summary>
        public static int FameForPurchase(int price)
        {
            if (price <= 0) return 0;
            int n = price / PricePerFame;
            return n > 0 ? n : 1;
        }

        /// <summary>幾元換 1 點知名度。見 <see cref="FameForPurchase"/> 對量級的說明。</summary>
        public const int PricePerFame = 1000;
    }
}
