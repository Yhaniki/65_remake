using NUnit.Framework;
using UnityEngine;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 大廳右上「放大鏡 / NEW筆」拉開的下拉選單條(官方 POPMENU.XML 的 Formal_Pop_Menu / Apply_Pop_Menu)。
    ///
    /// 需求只有一句:**滑過去只准字變色,底一個像素都不要動**。
    ///
    /// 官方那幾張 *PopMenu1/2.an 切自 My3dHouseSmall.png 上的一整片垂直漸層(底色 alpha 140→40、粉紅→紫),
    /// 那是美術本來的樣子、要照原樣留著 —— 曾經把五條的底壓成同一色,那是在改底圖不是在修滑過態
    /// (使用者:「原本漸層色的背景要保留」)。所以這裡兩件事一起釘:
    ///   • 滑過態與 normal 的差異**只出現在金黃色的像素上**(黃字/黃圖示/右邊黃三角);
    ///   • 官方那片漸層原封不動(連五條之間的深淺差都還在)。
    /// </summary>
    public class LobbyPopMenuArtTests
    {
        /// <summary>{normal, hover} —— 與 <c>LobbyScreen.HallMenuItems</c>(放大鏡那顆)同一份。</summary>
        private static readonly string[,] HallItems =
        {
            { "FamilyPopMenu1",  "FamilyPopMenu2"  },   // 家族
            { "ChangePopMenu1",  "ChangePopMenu2"  },   // 奖励兑换
            { "WeddingPopMenu1", "WeddingPopMenu2" },   // 情侣密友证
            { "RankPopMenu1",    "RankPopMenu2"    },   // 排行榜
            { "SetPopMenu1",     "SetPopMenu2"     },   // 设置
        };

        /// <summary>{normal, hover} —— 與 <c>LobbyScreen.ApplyMenuItems</c>(NEW筆那顆)同一份。
        /// 這一組在圖集裡左右相反(normal 在右半、hover 在左半),底切自同一片漸層。</summary>
        private static readonly string[,] ApplyItems =
        {
            { "StagePopMenu1",    "StagePopMenu2"    },   // 舞台
            { "ShoppingPopMenu1", "ShoppingPopMenu2" },   // 商店
            { "HousePopMenu1",    "HousePopMenu2"    },   // E模式小屋
            { "PlayingPopMenu1",  "PlayingPopMenu2"  },   // 游乐场
        };

        // 底的取樣範圍:左邊界內側。五條的圖示最早都從 x=6 才開始有柔邊,x≤5 任何一條都是純底。
        private const int BgX0 = 0, BgX1 = 5;
        // 右邊三角(滑過才有)的水平範圍。
        private const int TriX0 = 108;

        private static void RequireArt()
        {
            if (LobbyArt.AnSoloRow(HallItems[0, 0]) == null)
                Assert.Ignore("STATECOMMUNITYHALL 美術不在這個環境裡(沒有 DATA root)。");
        }

        [Test]
        public void White_Matte_Removal_Is_Off_For_These_Rows()
        {
            RequireArt();
            // 這條記錄真兇:放大鏡那組的滑過圖把右緣的暗分隔線清成 (255,255,255,0)(52 顆),於是
            // DeMatteWhite 的自動偵測只在**它**身上判定「這張是在白底上合成的」→ 連橫條自己那片半透明的底
            // 都被 un-composite(粉紫 242,50,175 → 深紅 228,0,92)。normal 那張沒有白透明 texel 所以不受影響
            // —— 兩張處理得不一樣,滑過去底色就跟著換。
            // 只測放大鏡那組:NEW筆那組(ApplyItems)兩張都沒有白透明 texel、偵測不會觸發 —— 這也正好解釋
            // 為什麼使用者只在「左邊數來第二個按鈕」的選單看到底變色。
            // 哪天有人把選單條改回預設的 AnSolo,這條會紅。
            for (int i = 0; i < HallItems.GetLength(0); i++)
            {
                string hover = HallItems[i, 1];
                var demat = LobbyArt.AnSolo(hover);        // 預設路徑（會去白底）
                var raw = LobbyArt.AnSoloRow(hover);       // 選單條走的路徑（不去白底）
                Assert.IsNotNull(demat); Assert.IsNotNull(raw);
                var dp = demat.texture.GetPixels32();
                var rp = raw.texture.GetPixels32();
                int w = raw.texture.width, h = raw.texture.height;
                int mid = (h / 2) * w + BgX0;              // 左邊界內側＝純底
                Assert.AreNotEqual(dp[mid].b, rp[mid].b,
                    hover + ":這張圖走預設路徑時底**會**被改掉 —— 選單條必須走 AnSoloRow");
            }
        }

        [Test]
        public void Hover_Only_Differs_On_Yellow_Pixels()
        {
            RequireArt();
            // 這是整條需求的本體:滑過態與 normal 逐像素比對,凡是**變了**的像素,新值必須是金黃色。
            // 底色(粉紅/紫)若有任何一顆被 hover 那張帶進來,這裡就會紅。
            foreach (var (name, hover) in Items())
            {
                var n = LobbyArt.AnSoloRow(name);
                var h = LobbyArt.AnSoloHover(name, hover);
                Assert.IsNotNull(h, name + " 的滑過態載不到");
                Assert.AreEqual(n.texture.width, h.texture.width, name + " 尺寸對不上");

                var np = n.texture.GetPixels32();
                var hp = h.texture.GetPixels32();
                int w = n.texture.width;
                for (int i = 0; i < np.Length; i++)
                {
                    Color32 a = np[i], b = hp[i];
                    if (a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a) continue;
                    Assert.IsTrue(b.r > 150 && b.r - b.b > 60,
                        name + " 滑過時 (" + (i % w) + "," + (i / w) + ") 從 " + (Color32)a + " 變成 "
                        + (Color32)b + " —— 那不是金黃色,底不該被動到");
                }
            }
        }

        [Test]
        public void Hover_Keeps_The_Official_Gradient_Background()
        {
            RequireArt();
            // 底的取樣帶:滑過態要跟 normal 完全一樣(不是「跟別條一樣」——官方本來就一條比一條深)。
            foreach (var (name, hover) in Items())
            {
                var n = LobbyArt.AnSoloRow(name);
                var h = LobbyArt.AnSoloHover(name, hover);
                var np = n.texture.GetPixels32();
                var hp = h.texture.GetPixels32();
                int w = n.texture.width, ht = n.texture.height;
                for (int y = 0; y < ht; y++)
                    for (int x = BgX0; x <= BgX1 && x < w; x++)
                    {
                        Color32 a = np[y * w + x], b = hp[y * w + x];
                        Assert.IsTrue(a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a,
                            name + " 的底在 (" + x + "," + y + ") 被滑過態改掉了");
                    }
            }
        }

        [Test]
        public void Hover_Still_Turns_The_Text_And_Arrow_Yellow()
        {
            RequireArt();
            // 「底不要動」不能做成「滑過什麼都不變」:字/圖示要變黃,右邊還要多一個黃三角。
            foreach (var (name, hover) in Items())
            {
                var n = LobbyArt.AnSoloRow(name);
                var h = LobbyArt.AnSoloHover(name, hover);
                Assert.Greater(YellowCount(h, 0), YellowCount(n, 0) + 20,
                    name + ":滑過應該有一整批黃字/黃圖示");
                Assert.AreEqual(0, YellowCount(n, TriX0), name + ":沒滑過時右邊不該有三角");
                Assert.Greater(YellowCount(h, TriX0), 20, name + ":滑過時右邊要有黃三角");
            }
        }

        [Test]
        public void The_Official_Gradient_Runs_Across_The_Rows()
        {
            RequireArt();
            // 官方原圖的底一條比一條深(最下面那條幾乎透明)。這是刻意保留的樣子 —— 哪天有人又想「統一底色」,
            // 這條會提醒他那是官方美術,不是 bug。
            var first = RowBg(LobbyArt.AnSolo(HallItems[0, 0]));
            var last = RowBg(LobbyArt.AnSolo(HallItems[HallItems.GetLength(0) - 1, 0]));
            Assert.Greater(first.a - last.a, 40,
                "官方原圖的底是漸層(第一條最不透明、最後一條最透),這個前提不成立了");
        }

        // ---- helpers ----

        /// <summary>兩顆鈕的選單條全部。</summary>
        private static System.Collections.Generic.IEnumerable<(string, string)> Items()
        {
            foreach (var table in new[] { HallItems, ApplyItems })
                for (int i = 0; i < table.GetLength(0); i++)
                    yield return (table[i, 0], table[i, 1]);
        }

        /// <summary>某條圖左邊界內側的底色(拿中間那一列當代表)。</summary>
        private static Color32 RowBg(Sprite s)
        {
            var px = s.texture.GetPixels32();
            int w = s.texture.width, h = s.texture.height;
            return px[(h / 2) * w + BgX0];
        }

        /// <summary>x ≥ <paramref name="fromX"/> 的範圍裡「金黃色」像素的數量(官方那個黃是 255,191,53)。</summary>
        private static int YellowCount(Sprite s, int fromX)
        {
            var px = s.texture.GetPixels32();
            int w = s.texture.width, h = s.texture.height, n = 0;
            for (int y = 0; y < h; y++)
                for (int x = fromX; x < w; x++)
                {
                    var c = px[y * w + x];
                    if (c.a > 200 && c.r > 200 && c.g > 140 && c.g < 220 && c.b < 110) n++;
                }
            return n;
        }
    }
}
