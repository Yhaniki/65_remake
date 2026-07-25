using System;
using System.IO;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 長條(hold)身體貼圖對應的回歸測試。使用者回報的三個症狀都在這裡釘住：
    ///   ① 向下模式「3d note hold 方向相反」——圖樣要跟尾帽同向（V 錨在尾端，不是頭）。
    ///   ② 「長條很長的時候不會捲動」——頭被按住釘在判定線 + 兩端都被裁時，V 仍必須逐幀變動。
    ///   ③ 「長條白邊不連續」——取樣的 U 範圍不能碰到 LONG_0_1 外側那排一節一節的銀色膠囊高光。
    /// ①② 是純函式測試；③ 讀真實 DDS（沒資料就 Ignore，比照 GarmentAlphaRealDataTests）。
    /// </summary>
    public class HoldBodyUvTests
    {
        const float HoldW = 59.86f;    // LaneW 82 × note3dHoldWidth 0.73 = 實機長條寬度

        // ── ① 尾端 = 離判定線最遠的那一頭 ────────────────────────────────────────────────────────────────
        [Test]
        public void TailY_IsTheEndAwayFromTheJudgeLine_InBothScrollDirections()
        {
            // 向上捲：判定線在頂端(70)，音符由下往上 → 尾在下方(較大 design y)。
            Assert.AreEqual(400f, HoldBodyUv.TailY(headY: 200f, endY: 400f, scrollSign: +1), 1e-4f);
            // 向下捲：判定線在板底(530)，音符由上往下 → 尾在上方(較小 y)。
            Assert.AreEqual(200f, HoldBodyUv.TailY(headY: 400f, endY: 200f, scrollSign: -1), 1e-4f);
        }

        [Test]
        public void DistFromTail_IsNonNegative_AndGrowsTowardTheHead()
        {
            // 向上捲：尾在 400（下），頭在 200（上）→ 越往上離尾越遠。
            float tUp = HoldBodyUv.TailY(200f, 400f, +1);
            Assert.AreEqual(0f, HoldBodyUv.DistFromTail(tUp, 400f, +1), 1e-4f);
            Assert.AreEqual(200f, HoldBodyUv.DistFromTail(tUp, 200f, +1), 1e-4f);
            // 向下捲：尾在 200（上），頭在 400（下）→ 越往下離尾越遠。恆為正，不會變號。
            float tDown = HoldBodyUv.TailY(400f, 200f, -1);
            Assert.AreEqual(0f, HoldBodyUv.DistFromTail(tDown, 200f, -1), 1e-4f);
            Assert.AreEqual(200f, HoldBodyUv.DistFromTail(tDown, 400f, -1), 1e-4f);
        }

        [Test]
        public void BodyV_IsGluedToTheCapWeld_AndFallsTowardTheHead()
        {
            // 尾端那一格永遠落在焊點 V ≈ 0.999（＝尾帽貼圖的接縫），否則圖樣就跟帽子對不上。
            const float weld = 1f - HoldBodyUv.ZBase * HoldBodyUv.VPerUnit;   // ≈ 0.99908
            Assert.AreEqual(weld, HoldBodyUv.BodyV(0f, HoldW), 1e-5f);
            // 離尾端越遠 V 越小（單調）—— 這就是「圖樣跟著尾帽同向」。
            Assert.Less(HoldBodyUv.BodyV(50f, HoldW), HoldBodyUv.BodyV(10f, HoldW));
        }

        /// <summary>向下模式的方向回歸：同一條長條在 向上 / 向下 兩種模式下，「離判定線較遠的那一端」拿到的 V 必須
        /// 相同（都是焊點），「較近的那一端」也必須相同。舊版一律 Max(y,yEnd) 時，向下模式兩者剛好對調 → 方向相反。</summary>
        [Test]
        public void DownScroll_MirrorsUpScroll_NotReversesIt()
        {
            // 向上：頭 200(近判定線 70)、尾 400。 向下：鏡射到 頭 400(近判定線 530)、尾 200。
            float upTail = HoldBodyUv.TailY(200f, 400f, +1), downTail = HoldBodyUv.TailY(400f, 200f, -1);
            float upFarV = HoldBodyUv.BodyV(HoldBodyUv.DistFromTail(upTail, 400f, +1), HoldW);      // 尾端
            float upNearV = HoldBodyUv.BodyV(HoldBodyUv.DistFromTail(upTail, 200f, +1), HoldW);     // 頭端
            float downFarV = HoldBodyUv.BodyV(HoldBodyUv.DistFromTail(downTail, 200f, -1), HoldW);  // 尾端
            float downNearV = HoldBodyUv.BodyV(HoldBodyUv.DistFromTail(downTail, 400f, -1), HoldW); // 頭端
            Assert.AreEqual(upFarV, downFarV, 1e-5f, "尾端(帽子)那一格的 V 兩個方向必須一致");
            Assert.AreEqual(upNearV, downNearV, 1e-5f, "頭端那一格的 V 兩個方向必須一致");
            Assert.Less(downNearV, downFarV, "向下模式：越靠近判定線 V 越小（跟向上同一個規則）");
        }

        // ── ② 很長的長條、頭被按住 → 仍要捲動 ──────────────────────────────────────────────────────────
        [TestCase(+1, 70f, 30f, 600f, TestName = "LongHeldHold_KeepsScrolling_UpScroll")]
        [TestCase(-1, 530f, 0f, 570f, TestName = "LongHeldHold_KeepsScrolling_DownScroll")]
        public void LongHeldHold_KeepsScrolling(int scrollSign, float judgeLineY, float clipTop, float clipBottom)
        {
            // 情境：長條比整塊面板還長，頭已經按住釘在判定線上。這時身體四邊形的兩條邊都不動了 —— 一條是被釘住的
            // 頭、另一條被裁在 clip band 上。唯一還在動的只有「未裁切的尾端」，所以 V 一定要以它為錨，否則整條圖樣
            // 凍在原地（＝使用者說的「長條很長的時候不會捲動」；舊版在向下模式正是如此）。
            float head = judgeLineY;                       // held → pinned
            float lastTop = float.NaN, lastBot = float.NaN;
            float Frame(float endY)
            {
                float tail = HoldBodyUv.TailY(head, endY, scrollSign);
                float top = Math.Max(Math.Min(head, endY), clipTop);
                float bot = Math.Min(Math.Max(head, endY), clipBottom);
                if (!float.IsNaN(lastTop))
                {
                    Assert.AreEqual(lastTop, top, 1e-3f, "前提：四邊形上緣逐幀不動");
                    Assert.AreEqual(lastBot, bot, 1e-3f, "前提：四邊形下緣逐幀不動");
                }
                lastTop = top; lastBot = bot;
                return HoldBodyUv.BodyV(HoldBodyUv.DistFromTail(tail, bot, scrollSign), HoldW);
            }
            // 尾端隨時間往判定線靠近 800 → 700 px（向上捲在下方、向下捲在上方，都用 scrollSign 推）。
            float v0 = Frame(judgeLineY + scrollSign * 800f);
            float v1 = Frame(judgeLineY + scrollSign * 700f);
            Assert.Greater(Math.Abs(v1 - v0), 0.5f, "長條 V 必須跟著尾端逐幀變動：100px 的移動應該換掉好幾個 chevron 週期");
        }

        // ── ③ U 範圍不可碰到外側銀色膠囊 ─────────────────────────────────────────────────────────────────
        [Test]
        public void DrawnUBand_IsInsideTheOfficialBand()
        {
            Assert.Greater(HoldBodyUv.U0, HoldBodyUv.OfficialU0, "左緣要往內縮");
            Assert.Less(HoldBodyUv.U1, HoldBodyUv.OfficialU1, "右緣要往內縮");
            Assert.Greater(HoldBodyUv.U1 - HoldBodyUv.U0, 0.9f * (HoldBodyUv.OfficialU1 - HoldBodyUv.OfficialU0),
                           "只是修邊，不該把長條縮掉一大截");
        }

        /// <summary>真實貼圖檢查：沿著長條的兩條邊取樣 LONG_0_1，亮度**不可以**隨 V 大幅起伏 —— 起伏就代表取到了
        /// 外側那排一節一節的銀色膠囊高光，畫面上就是「白邊不連續」。官方 U 值在這個門檻下會失敗（實測左 120／右 136）。</summary>
        [Test]
        public void LongBodyEdges_DoNotSampleTheDiscontinuousSilverRail()
        {
            string path = Path.Combine(SdoExtracted.Root, "3DNOTES", "LONG_0_1.DDS");
            if (!File.Exists(path)) Assert.Ignore("3DNOTES/LONG_0_1.DDS not found — needs the game data (data_root.txt)");
            var px = DecodeDxt1Rgb(File.ReadAllBytes(path), out int w, out int h);
            if (px == null) Assert.Ignore("LONG_0_1.DDS is not a plain DXT1 128×128 texture");

            foreach (var (name, u) in new[] { ("左緣", HoldBodyUv.U0), ("右緣", HoldBodyUv.U1) })
            {
                float min = float.MaxValue, max = float.MinValue;
                for (int v = 0; v < h; v++)
                {
                    float l = Luma(BilinearU(px, w, u, v));
                    if (l < min) min = l; if (l > max) max = l;
                }
                Assert.Less(max - min, 40f, $"{name} (u={u:F4}) 沿 V 的亮度起伏 {max - min:F1} 太大 → 取到銀色膠囊了");
            }
        }

        // 沿 u 的雙線性取樣（GPU 語意：texel 中心在 (x+0.5)/w），單一列 v。
        static (float r, float g, float b) BilinearU((byte r, byte g, byte b)[] px, int w, float u, int v)
        {
            float fx = u * w - 0.5f;
            int x0 = (int)Math.Floor(fx); float t = fx - x0;
            int a = ((x0 % w) + w) % w, b = ((x0 + 1) % w + w) % w;
            var pa = px[v * w + a]; var pb = px[v * w + b];
            return (pa.r * (1 - t) + pb.r * t, pa.g * (1 - t) + pb.g * t, pa.b * (1 - t) + pb.b * t);
        }

        static float Luma((float r, float g, float b) c) => 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;

        /// <summary>最小 DXT1 解碼（row 0 = 貼圖頂端，跟 DdsLoader 的不翻轉路徑一致）。測試自己解，才不會用「被測程式」
        /// 當自己的答案；上傳到 GPU 的 Texture2D 是 non-readable，也讀不回來。</summary>
        static (byte r, byte g, byte b)[] DecodeDxt1Rgb(byte[] d, out int w, out int h)
        {
            w = h = 0;
            if (d == null || d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S' || d[3] != ' ') return null;
            h = BitConverter.ToInt32(d, 12); w = BitConverter.ToInt32(d, 16);
            if (System.Text.Encoding.ASCII.GetString(d, 84, 4) != "DXT1") return null;
            int bx = (w + 3) / 4, by = (h + 3) / 4;
            if (128 + bx * by * 8 > d.Length) return null;
            var px = new (byte, byte, byte)[w * h];
            var c = new (byte r, byte g, byte b)[4];
            for (int b = 0; b < bx * by; b++)
            {
                int o = 128 + b * 8;
                ushort c0 = BitConverter.ToUInt16(d, o), c1 = BitConverter.ToUInt16(d, o + 2);
                c[0] = From565(c0); c[1] = From565(c1);
                if (c0 > c1)
                {
                    c[2] = ((byte)((2 * c[0].r + c[1].r) / 3), (byte)((2 * c[0].g + c[1].g) / 3), (byte)((2 * c[0].b + c[1].b) / 3));
                    c[3] = ((byte)((c[0].r + 2 * c[1].r) / 3), (byte)((c[0].g + 2 * c[1].g) / 3), (byte)((c[0].b + 2 * c[1].b) / 3));
                }
                else
                {
                    c[2] = ((byte)((c[0].r + c[1].r) / 2), (byte)((c[0].g + c[1].g) / 2), (byte)((c[0].b + c[1].b) / 2));
                    c[3] = (0, 0, 0);
                }
                uint bits = BitConverter.ToUInt32(d, o + 4);
                for (int i = 0; i < 16; i++)
                {
                    int x = (b % bx) * 4 + (i & 3), y = (b / bx) * 4 + (i >> 2);
                    if (x >= w || y >= h) continue;
                    px[y * w + x] = c[(int)((bits >> (i * 2)) & 3)];
                }
            }
            return px;
        }

        static (byte r, byte g, byte b) From565(ushort v)
            => ((byte)(((v >> 11) & 31) * 255 / 31), (byte)(((v >> 5) & 63) * 255 / 63), (byte)((v & 31) * 255 / 31));
    }
}
