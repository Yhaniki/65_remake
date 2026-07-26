using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// SCN0004 海灘太陽的鏡頭光斑。Ghidra 完全沒有反編譯出可見性(0x418880)與繪製(0x418990)，
    /// 常數與公式是逐指令反組譯的;這裡把每一條釘住。
    /// </summary>
    public class SceneLensFlareTests
    {
        [Test]
        public void Element_Table_Is_Nineteen_Rows_In_The_Disassembled_Order()
        {
            // ★ 19 筆不是 18 筆 —— 三個獨立證據:配置引數 count=0x13、頂點迴圈 cmp ebx,0x130(=19×0x10)、
            // DrawPrimitive 迴圈 cmp esi,0x4c(=19×4)。表起點是 0x542c98，欄位順序 {v, ARGB, t, size}，
            // v 在最前面 —— 讀成 {t,size,uv,argb} 的話前 17 筆看起來也「合理」，直到最後一筆撞上表尾常數。
            Assert.AreEqual(19, SceneLensFlare.Elements.Length);
            var e0 = SceneLensFlare.Elements[0];
            Assert.AreEqual(-0.25f, e0.T, 1e-6f, "第 0 顆在太陽的反方向(t 為負)");
            Assert.AreEqual(8f, e0.Size, 1e-6f);
            Assert.AreEqual(0.375f, e0.V, 1e-6f);
            var last = SceneLensFlare.Elements[18];
            Assert.AreEqual(2.8f, last.T, 1e-5f);
            Assert.AreEqual(19.5f, last.Size, 1e-6f);
            // 最大的那顆(t=2.4、半邊長 130.5)
            Assert.AreEqual(130.5f, SceneLensFlare.Elements[17].Size, 1e-6f);
            // v 只會是 atlas 前 5 列(0/0.125/0.25/0.375/0.5) —— BMP 是 8 列，後 3 列全黑
            foreach (var e in SceneLensFlare.Elements)
            {
                Assert.GreaterOrEqual(e.V, 0f);
                Assert.LessOrEqual(e.V + SceneLensFlare.RowV, 1f);
                Assert.AreEqual(0f, (e.V * 8f) % 1f, 1e-4f, "v 必須落在 1/8 的格線上");
            }
        }

        [Test]
        public void Visibility_Angle_Is_Strictly_Between_Zero_And_Forty()
        {
            var eye = Vector3.zero;
            // 正對太陽 → 夾角 0 → **不畫**(原版怪癖:嚴格大於 0)
            Assert.IsFalse(SceneLensFlare.IsWithinAngle(eye, Vector3.forward, Vector3.forward * 100f),
                "夾角剛好 0 時官方反而不畫");
            // 20 度 → 畫
            var sun20 = Quaternion.Euler(0f, 20f, 0f) * Vector3.forward * 100f;
            Assert.IsTrue(SceneLensFlare.IsWithinAngle(eye, Vector3.forward, sun20));
            Assert.AreEqual(20f, SceneLensFlare.AngleDeg(eye, Vector3.forward, sun20), 1e-2f);
            // 40 度整 → 不畫(上限也是嚴格)
            var sun40 = Quaternion.Euler(0f, 40f, 0f) * Vector3.forward * 100f;
            Assert.IsFalse(SceneLensFlare.IsWithinAngle(eye, Vector3.forward, sun40));
            // 背後 → 不畫
            Assert.IsFalse(SceneLensFlare.IsWithinAngle(eye, Vector3.forward, Vector3.back * 100f));
        }

        [Test]
        public void Ndc_Maps_To_The_Hardcoded_800x600_Screen()
        {
            // sx = (ndc.x + 1) × 400、sy = 300 − ndc.y × 300 —— 官方寫死半寬高，不取當前解析度。
            Assert.AreEqual(new Vector2(400f, 300f), SceneLensFlare.ToScreen(Vector2.zero), "NDC 原點 = 畫面中心");
            Assert.AreEqual(new Vector2(0f, 300f), SceneLensFlare.ToScreen(new Vector2(-1f, 0f)));
            Assert.AreEqual(new Vector2(800f, 300f), SceneLensFlare.ToScreen(new Vector2(1f, 0f)));
            Assert.AreEqual(new Vector2(400f, 0f), SceneLensFlare.ToScreen(new Vector2(0f, 1f)), "NDC 上 = 螢幕 y=0");
            Assert.AreEqual(new Vector2(400f, 600f), SceneLensFlare.ToScreen(new Vector2(0f, -1f)));
        }

        [Test]
        public void Intensity_Is_Always_Clamped_To_One_Inside_The_Viewport()
        {
            const float cx = 400f, cy = 300f;
            // 出界判定已保證 dist <= cx·√2，所以 I = (1.25 − r)×50 ≥ 12.5 → 恆為 1。
            // 這條測試釘住「原版沒有離中心越遠越暗的漸變」，免得日後有人以為是我們漏做。
            Assert.AreEqual(1f, SceneLensFlare.Intensity(0f, cx), 1e-6f);
            float corner = SceneLensFlare.AxisDistance(new Vector2(800f, 600f), cx, cy);
            Assert.AreEqual(1f, SceneLensFlare.Intensity(corner, cx), 1e-6f, "畫面最角落也還是 1");
            // 只有跑到畫面外(不可能發生，因為前面就擋掉了)才會衰減
            Assert.Less(SceneLensFlare.Intensity(cx * 1.4142136f * 1.3f, cx), 1f);
        }

        [Test]
        public void Spread_Only_Opens_Up_When_The_Sun_Is_Near_The_Centre()
        {
            // k = 1;dist < w/8(800 → 100)時 k = 2 − dist/100，最大 2。
            Assert.AreEqual(2f, SceneLensFlare.SpreadK(0f, 800f), 1e-5f, "太陽正中央 → 鬼影拉最開");
            Assert.AreEqual(1.5f, SceneLensFlare.SpreadK(50f, 800f), 1e-5f);
            Assert.AreEqual(1f, SceneLensFlare.SpreadK(100f, 800f), 1e-5f, "剛好 w/8 → 回到 1");
            Assert.AreEqual(1f, SceneLensFlare.SpreadK(400f, 800f), 1e-5f);
        }

        [Test]
        public void Elements_Lie_On_The_Sun_To_Centre_Axis()
        {
            var sun = new Vector2(200f, 150f);
            var centre = new Vector2(400f, 300f);
            // t=0 → 就在太陽上;係數是 k×t×0.8，所以 t = 1/0.8 = 1.25 才剛好到中心。
            Assert.AreEqual(sun, SceneLensFlare.ElementScreenPos(sun, centre, 1f, 0f));
            var mid = SceneLensFlare.ElementScreenPos(sun, centre, 1f, 1.25f);
            Assert.AreEqual(centre.x, mid.x, 1e-3f, "t=1.25 落在畫面中心");
            Assert.AreEqual(centre.y, mid.y, 1e-3f);
            // t=2.5 → 中心的對稱點(太陽的另一側)
            var opp = SceneLensFlare.ElementScreenPos(sun, centre, 1f, 2.5f);
            Assert.AreEqual(centre.x + (centre.x - sun.x), opp.x, 1e-3f);
            // 負的 t → 往太陽的反方向
            var back = SceneLensFlare.ElementScreenPos(sun, centre, 1f, -0.25f);
            Assert.Less(back.x, sun.x);
        }

        [Test]
        public void Atlas_Rows_Are_Mapped_Top_Down_Like_D3D9_Not_Unity_Bottom_Up()
        {
            // ★ 官方是 D3D9 慣例:V=0 在**影像頂端**、V 增加往下走;Unity 的 V=0 在貼圖**底端**。
            // 直接照抄 V 會取到鏡射後的列 —— 而 LENSFLARE.BMP 的內容只在「由上往下」的第 0..4 列
            // (第 5..7 列整片全黑),所以照抄會讓好幾顆光斑取到全黑完全不出現(最明顯的是 idx 17 那個
            // 半邊長 130.5 的大亮環),其餘的也取到錯的圖形。
            // 這條測試釘住換算:影像第 r 列(由上往下) → Unity V ∈ [1−(r+1)/8, 1−r/8]。
            for (int r = 0; r < 5; r++)
            {
                float officialV = r / 8f;
                float unityTop = 1f - officialV;                 // quad 上緣
                float unityBottom = 1f - (officialV + 0.125f);   // quad 下緣
                Assert.Greater(unityTop, unityBottom, "上緣的 Unity V 必須大於下緣(V 往上長)");
                Assert.AreEqual(0.125f, unityTop - unityBottom, 1e-6f, "一列剛好 1/8");
                Assert.LessOrEqual(unityTop, 1f + 1e-6f);
                Assert.GreaterOrEqual(unityBottom, 0.375f - 1e-6f, "有內容的 5 列都落在貼圖上半部");
            }
            // 全黑的第 5..7 列對應 Unity V < 0.375 —— 任何元素都不該取到那裡。
            foreach (var e in SceneLensFlare.Elements)
                Assert.GreaterOrEqual(1f - (e.V + SceneLensFlare.RowV), 0.375f - 1e-6f,
                    $"v={e.V} 會取到 BMP 全黑的列(第 5..7 列) —— V 軸翻反了");
            // 官方表裡確實用到了「細亮環」那一列(v=0.125),而且是最大的那顆(半邊長 130.5)。
            var bigRing = System.Array.Find(SceneLensFlare.Elements, x => x.Size > 130f);
            Assert.AreEqual(0.125f, bigRing.V, 1e-6f, "最大的那顆是細亮環(BMP 由上往下第 1 列)");
        }

        [Test]
        public void Only_Scn0004_Has_A_Lens_Flare()
        {
            Assert.IsTrue(SceneLensFlareCatalog.Has("SCN0004"));
            Assert.IsTrue(SceneLensFlareCatalog.Has("scn0004"), "資料夾比對不分大小寫");
            Assert.IsFalse(SceneLensFlareCatalog.Has("SCN0005"));
            Assert.IsFalse(SceneLensFlareCatalog.Has(null));
        }

        [Test]
        public void Lifetime_Matches_The_Official_Ten_Seconds()
        {
            // +0xa8 = 10000 ms，而 +0xac 的建立時間戳全 exe 只寫過一次 → 10 秒後永遠不再畫。
            Assert.AreEqual(10000f, SceneLensFlare.LifetimeMs, 1e-3f);
            Assert.AreEqual(new Vector3(33f, 175f, -3f), SceneLensFlare.SunPos, "太陽世界座標");
            Assert.AreEqual(40f, SceneLensFlare.MaxAngleDeg, 1e-3f);
            Assert.AreEqual(0.8f, SceneLensFlare.AxisScale, 1e-6f);
            Assert.AreEqual(0.125f, SceneLensFlare.RowV, 1e-6f, "atlas 8 列");
        }
    }
}
