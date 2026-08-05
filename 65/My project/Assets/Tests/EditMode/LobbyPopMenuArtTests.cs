using NUnit.Framework;
using UnityEngine;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 大廳右上「放大鏡 / NEW筆」拉開的下拉選單條(官方 POPMENU.XML 的 Formal_Pop_Menu / Apply_Pop_Menu)。
    ///
    /// 官方那幾張 *PopMenu1/2.an 切自 My3dHouseSmall.png 上的**一整片垂直漸層**(底色 alpha 140→40、
    /// 粉紅→紫)。選單沒有背板(XML 寫 background="empty.an"),照原樣疊起來最下面那條就透出背後的大廳畫面、
    /// 深到看起來像「被選中所以底變色了」——使用者連續回報兩次的就是這件事。
    ///
    /// 這裡把兩件事釘住:
    ///   • 五條(含滑過態)的底壓完之後**完全一樣**;
    ///   • 滑過仍然只有字/圖示/右邊三角變黃。
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
        /// 這一組在圖集裡左右相反(normal 在右半、hover 在左半),但底切自**同一片**漸層,壓底的規則一樣適用。</summary>
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
            if (LobbyArt.AnSolo(HallItems[0, 0]) == null)
                Assert.Ignore("STATECOMMUNITYHALL 美術不在這個環境裡(沒有 DATA root)。");
        }

        [Test]
        public void Every_Row_Flattens_To_The_Same_Background()
        {
            RequireArt();
            foreach (var (name, _) in Items())
            {
                var s = LobbyArt.AnSoloFlatBg(name);
                Assert.IsNotNull(s, name + " 載不到");
                AssertBgIsFlat(s, name + "(normal)");
            }
        }

        [Test]
        public void Hover_Row_Keeps_The_Same_Background()
        {
            RequireArt();
            // 滑過的那條底要跟其他四條一模一樣 —— 否則畫面上又會變成「選了底就變色」。
            foreach (var (name, hover) in Items())
            {
                var s = LobbyArt.AnSoloHoverFlatBg(name, hover);
                Assert.IsNotNull(s, name + " 的滑過態載不到");
                AssertBgIsFlat(s, name + "(hover)");
            }
        }

        [Test]
        public void Hover_Still_Turns_The_Text_And_Arrow_Yellow()
        {
            RequireArt();
            // 壓底不能把「滑過會變黃」一起壓掉:字/圖示要出現黃像素,右邊還要多一個三角。
            foreach (var (name, hover) in Items())
            {
                var n = LobbyArt.AnSoloFlatBg(name);
                var h = LobbyArt.AnSoloHoverFlatBg(name, hover);
                Assert.Greater(YellowCount(h, 0), YellowCount(n, 0) + 20,
                    name + ":滑過應該有一整批黃字/黃圖示");
                Assert.AreEqual(0, YellowCount(n, TriX0), name + ":沒滑過時右邊不該有三角");
                Assert.Greater(YellowCount(h, TriX0), 20, name + ":滑過時右邊要有黃三角");
            }
        }

        [Test]
        public void Official_Art_Really_Is_A_Gradient()
        {
            RequireArt();
            // 這條記錄的是 bug 成因(也是上面三條存在的理由):官方**原圖**的底一條比一條深、
            // 最下面那條幾乎透明。哪天素材換成五條同底,這裡會紅 —— 那時壓底就只是多餘而不是必要了。
            var first = RowBg(LobbyArt.AnSolo(HallItems[0, 0]));
            var last = RowBg(LobbyArt.AnSolo(HallItems[HallItems.GetLength(0) - 1, 0]));
            Assert.Greater(first.a - last.a, 40,
                "官方原圖的底本來是漸層(第一條最不透明、最後一條最透),這個前提不成立了");
        }

        // ---- helpers ----

        /// <summary>兩顆鈕的選單條全部 —— 兩組都吃同一片漸層,所以兩組都要壓平(而且要壓成同一色,
        /// 不然兩個選單並排看起來會是兩種粉)。</summary>
        private static System.Collections.Generic.IEnumerable<(string, string)> Items()
        {
            foreach (var table in new[] { HallItems, ApplyItems })
                for (int i = 0; i < table.GetLength(0); i++)
                    yield return (table[i, 0], table[i, 1]);
        }

        /// <summary>整條左邊界內側的每一個底像素都必須正好是 <see cref="LobbyArt.PopMenuBg"/>。</summary>
        private static void AssertBgIsFlat(Sprite s, string label)
        {
            var px = s.texture.GetPixels32();
            int w = s.texture.width, h = s.texture.height;
            var want = LobbyArt.PopMenuBg;
            for (int y = 0; y < h; y++)
                for (int x = BgX0; x <= BgX1 && x < w; x++)
                {
                    var c = px[y * w + x];
                    Assert.IsTrue(c.r == want.r && c.g == want.g && c.b == want.b && c.a == want.a,
                        label + " 的底在 (" + x + "," + y + ") 是 " + (Color32)c + ",應該是統一底色 " + (Color32)want);
                }
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
