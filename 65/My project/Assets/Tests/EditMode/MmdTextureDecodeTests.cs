using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// 自己解 PNG 的正確性 —— 判準只有一個:<b>與 Unity 的 <c>Texture2D.LoadImage</c> 逐像素相同</b>。
    ///
    /// 為什麼要自己解:<c>LoadImage</c> 只能在主執行緒呼叫,而一張 2048² 的「解碼＋mipmap」約 140 ms。
    /// 收到別人的 MMD 模型時十張連著解 = 畫面凍 1.4 秒、ping 跟著停、server 有機會判斷斷線。
    /// 自己解就能整段丟到背景執行緒,主執行緒只剩 <c>SetPixels32 + Apply</c>。
    ///
    /// 🔴 <b>方向是最容易錯、又最難從症狀看出來的一件事</b>:PNG 的第 0 列是圖的上緣,
    /// <c>SetPixels32</c> 的第 0 列是下緣。反了的話,MMD 模型會變成「.png 的部位上下顛倒、
    /// .tga 的部位正常」—— 那看起來像 UV 的問題,查起來會繞很遠。所以這裡連座標一起比。
    /// </summary>
    public class MmdTextureDecodeTests
    {
        /// <summary>
        /// 不對稱、有梯度、有 alpha —— 上下顛倒、RGB 交換、alpha 漏掉都會被抓到。
        ///
        /// <paramref name="alpha"/> false 時來源必須是 <b>RGB24</b>:<c>EncodeToPNG</c> 看的是**貼圖格式**,
        /// 不是內容 —— RGBA32 就算每個 alpha 都是 255 也照樣寫出帶 alpha 通道的 PNG(color type 6),
        /// 於是「不透明 PNG」那條分支根本測不到。
        /// </summary>
        private static Texture2D MakeSource(int w, int h, bool alpha)
        {
            var src = new Texture2D(w, h, alpha ? TextureFormat.RGBA32 : TextureFormat.RGB24, false);
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = new Color32((byte)(x * 255 / (w - 1)),
                                                (byte)(y * 255 / (h - 1)),
                                                (byte)((x + 2 * y) & 0xff),
                                                alpha ? (byte)((x * y) & 0xff) : (byte)255);
            src.SetPixels32(px);
            src.Apply(false);
            return src;
        }

        private static void AssertMatchesLoadImage(byte[] png, string what)
        {
            var reference = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Assert.IsTrue(reference.LoadImage(png), what + ": Unity 自己也讀不了這個 PNG(測試資料壞了)");
            var expect = reference.GetPixels32();

            var got = MmdTextureDecode.TryDecode(png, ".png");
            Assert.IsNotNull(got, what + ": 自己的解碼器拒收 —— 會退回主執行緒的 LoadImage,分幀就白做了");
            Assert.AreEqual(reference.width, got.Width, what + ": 寬不對");
            Assert.AreEqual(reference.height, got.Height, what + ": 高不對");
            Assert.AreEqual(expect.Length, got.Pixels.Length, what + ": 像素數不對");

            for (int i = 0; i < expect.Length; i++)
            {
                var e = expect[i]; var g = got.Pixels[i];
                if (e.r == g.r && e.g == g.g && e.b == g.b && e.a == g.a) continue;
                int x = i % got.Width, y = i / got.Width;
                Assert.Fail($"{what}: ({x},{y}) 與 LoadImage 不同 —— 期待 " +
                            $"({e.r},{e.g},{e.b},{e.a}),得到 ({g.r},{g.g},{g.b},{g.a})。" +
                            "整張都差一個上下翻轉的話就是列序寫反了(PNG 第 0 列是上緣,SetPixels32 第 0 列是下緣)");
            }
            Object.DestroyImmediate(reference);
        }

        [Test]
        public void Rgba_MatchesLoadImagePixelForPixel()
        {
            var src = MakeSource(64, 48, alpha: true);       // 非正方形:寬高寫反會當場爆
            AssertMatchesLoadImage(src.EncodeToPNG(), "RGBA");
            Object.DestroyImmediate(src);
        }

        [Test]
        public void Opaque_MatchesLoadImagePixelForPixel()
        {
            // 全不透明 → EncodeToPNG 會省掉 alpha 通道(color type 2),是另一條解碼分支。
            var src = MakeSource(37, 29, alpha: false);      // 奇數尺寸:掃描列 stride 算錯會爆
            AssertMatchesLoadImage(src.EncodeToPNG(), "RGB");
            Object.DestroyImmediate(src);
        }

        [Test]
        public void OpaquePng_IsBuiltAsRgb24_NotRgba32()
        {
            var src = MakeSource(16, 16, alpha: false);
            var got = MmdTextureDecode.TryDecode(src.EncodeToPNG(), ".png");
            Assert.IsNotNull(got);
            Assert.IsFalse(got.HasAlpha, "不透明的 PNG 被判成有 alpha —— 2048² 會白吃 4 MB");
            Assert.IsTrue(got.MipChain, "PNG 沒有 mipmap 鏈 —— 與 LoadImage 的行為不一致,遠處會閃");
            Assert.AreEqual(TextureWrapMode.Repeat, got.Wrap);
            Object.DestroyImmediate(src);
        }

        [Test]
        public void Jpeg_FallsBackToTheMainThread()
        {
            var src = MakeSource(16, 16, alpha: false);
            // JPG 沒有自己解 —— 一定要回 null,呼叫端才會走 LoadImage 那條(而不是畫出一張破圖)。
            Assert.IsNull(MmdTextureDecode.TryDecode(src.EncodeToJPG(), ".jpg"),
                          "JPG 被自己的解碼器收下了 —— 它不認得 JPEG,結果會是一張壞掉的貼圖");
            Object.DestroyImmediate(src);
        }

        [Test]
        public void Garbage_IsRejected_NotCrashed()
        {
            Assert.IsNull(MmdTextureDecode.TryDecode(null, ".png"));
            Assert.IsNull(MmdTextureDecode.TryDecode(new byte[] { 1, 2, 3 }, ".png"));
            // PNG 的魔數對、後面全是垃圾 —— 解碼器要自己吞掉,不能把例外丟到協程上。
            var fake = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 9, 9, 9, 9, 9, 9 };
            Assert.IsNull(MmdTextureDecode.TryDecode(fake, ".png"));
        }

        /// <summary>
        /// TGA 那條走的是 <c>DdsLoader.TgaPixels</c> —— 它與 <c>LoadTga</c> 必須是同一段程式,
        /// 不然「PNG 正、TGA 顛倒」的老問題會從另一個方向長回來。
        /// </summary>
        [Test]
        public void Tga_GoesThroughTheSameDecoderAsLoadTga()
        {
            var tga = BuildTga32(8, 6);
            var viaDecode = MmdTextureDecode.TryDecode(tga, ".tga");
            Assert.IsNotNull(viaDecode, "TGA 沒走到背景解碼");
            Assert.IsFalse(viaDecode.MipChain, "TGA 本來就沒有 mipmap —— 別在這裡順手改行為");
            Assert.AreEqual(TextureWrapMode.Clamp, viaDecode.Wrap, "TGA 本來是 Clamp");

            var reference = DdsLoader.LoadTga(tga, sdoRowOrder: false, readable: true);
            Assert.IsNotNull(reference);
            var expect = reference.GetPixels32();
            Assert.AreEqual(expect.Length, viaDecode.Pixels.Length);
            for (int i = 0; i < expect.Length; i++)
                Assert.AreEqual(expect[i], viaDecode.Pixels[i], $"TGA 第 {i} 個像素與 LoadTga 不同");
            Object.DestroyImmediate(reference);
        }

        // 未壓縮 32-bit、bottom-left 原點的最小 TGA。
        private static byte[] BuildTga32(int w, int h)
        {
            var d = new byte[18 + w * h * 4];
            d[2] = 2;                                   // 未壓縮全彩
            d[12] = (byte)(w & 0xff); d[13] = (byte)(w >> 8);
            d[14] = (byte)(h & 0xff); d[15] = (byte)(h >> 8);
            d[16] = 32;
            int at = 18;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    d[at++] = (byte)(x * 7);            // B
                    d[at++] = (byte)(y * 11);           // G
                    d[at++] = (byte)(x + y);            // R
                    d[at++] = (byte)(200 - x);          // A
                }
            return d;
        }
    }
}
