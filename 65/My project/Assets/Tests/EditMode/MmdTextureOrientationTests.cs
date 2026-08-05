using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// 一個 MMD 模型的貼圖可以是 PNG / TGA / BMP 混著用（LaplusDarknesss：頭髮 <c>.png</c>、
    /// 臉/身體/眼睛/皮膚 <c>.tga</c>），所以 <see cref="MmdAvatar"/> 那三條解碼路徑的**上下方向必須一致**。
    ///
    /// 不一致的症狀是「一部分貼圖是正的、一部分上下顛倒」，而且它**跟 UV 完全無關** —— 那時去調
    /// <c>mmdFlipV</c> 只會把本來正的那部分也弄反，兩邊永遠不會同時對。這個測試把三條路徑釘在一起。
    ///
    /// 同時釘住反向的那一半：SDO 自己的素材（3D note 特效、角色部件）走的是 D3D 列序
    /// （圖的上緣 = <c>SetPixels32</c> 第 0 列 = Unity 取樣的下緣），那條**不能**跟著改。
    /// </summary>
    public class MmdTextureOrientationTests
    {
        private const int W = 4, H = 4;
        private static readonly Color32 Top = new Color32(255, 0, 0, 255);      // 圖的上半＝紅
        private static readonly Color32 Bottom = new Color32(0, 0, 255, 255);   // 圖的下半＝藍

        private readonly List<Texture2D> _made = new List<Texture2D>();

        [TearDown]
        public void TearDown()
        {
            foreach (var t in _made) if (t != null) Object.DestroyImmediate(t);
            _made.Clear();
        }

        private Texture2D Keep(Texture2D t) { _made.Add(t); return t; }

        // ---------------------------------------------------------------- 測資

        /// <summary>上半紅、下半藍的圖，編成 PNG（Unity 自己的編碼器 → 之後用它自己的解碼器讀回來）。</summary>
        private byte[] MakePng()
        {
            var t = Keep(new Texture2D(W, H, TextureFormat.RGBA32, false));
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)                       // SetPixels32 的第 0 列 = Unity 的下緣
                for (int x = 0; x < W; x++)
                    px[y * W + x] = y < H / 2 ? Bottom : Top;
            t.SetPixels32(px); t.Apply(false);
            return t.EncodeToPNG();
        }

        /// <summary>同一張圖的未壓縮 32-bit TGA。<paramref name="topLeft"/>＝檔頭 bit5（原點在左上），
        /// false（預設、也是絕大多數 TGA）＝原點在左下，檔案裡第一列是圖的**下**緣。</summary>
        private static byte[] MakeTga(bool topLeft)
        {
            var d = new byte[18 + W * H * 4];
            d[2] = 2;                                        // uncompressed true-colour
            d[12] = W & 0xff; d[13] = (byte)(W >> 8);
            d[14] = H & 0xff; d[15] = (byte)(H >> 8);
            d[16] = 32;                                      // bpp
            d[17] = (byte)((topLeft ? 0x20 : 0x00) | 0x08);  // bit5 origin, low nibble = alpha bits
            int i = 18;
            for (int row = 0; row < H; row++)
            {
                // row 0 是檔案裡的第一列：topLeft 時它是圖的上緣，否則是下緣。
                bool isTopHalf = topLeft ? row < H / 2 : row >= H / 2;
                var c = isTopHalf ? Top : Bottom;
                for (int x = 0; x < W; x++) { d[i++] = c.b; d[i++] = c.g; d[i++] = c.r; d[i++] = 255; }
            }
            return d;
        }

        private Texture2D DecodePng(byte[] png)
        {
            var t = Keep(new Texture2D(2, 2, TextureFormat.RGBA32, false));
            Assert.IsTrue(t.LoadImage(png), "PNG 解碼失敗");
            return t;
        }

        // ---------------------------------------------------------------- 三條路徑同向

        [Test]
        public void PngAndTga_DecodeToTheSameOrientation()
        {
            var png = DecodePng(MakePng());
            var tga = Keep(DdsLoader.LoadTga(MakeTga(topLeft: false), sdoRowOrder: false, readable: true));
            Assert.IsNotNull(tga, "TGA 解碼失敗");

            // GetPixel 的 y=0 是 Unity 取樣的下緣。兩條路徑對同一張圖必須給出同一個答案，
            // 否則同一具身體上 .png 與 .tga 的材質會一正一反。
            Assert.AreEqual((Color)Bottom, png.GetPixel(0, 0), "PNG：下緣應該是藍的");
            Assert.AreEqual((Color)Bottom, tga.GetPixel(0, 0), "TGA 與 PNG 上下相反 → 模型會一部分正一部分顛倒");
            Assert.AreEqual((Color)Top, png.GetPixel(0, H - 1));
            Assert.AreEqual((Color)Top, tga.GetPixel(0, H - 1));
        }

        [Test]
        public void TopLeftOriginTga_DecodesTheSameWayToo()
        {
            // 檔頭 bit5 是「檔案裡的列序」，不是「圖該怎麼擺」—— 兩種原點的同一張圖要解出同一個結果。
            var bottomLeft = Keep(DdsLoader.LoadTga(MakeTga(topLeft: false), sdoRowOrder: false, readable: true));
            var topLeftTga = Keep(DdsLoader.LoadTga(MakeTga(topLeft: true), sdoRowOrder: false, readable: true));

            Assert.AreEqual((Color)Bottom, topLeftTga.GetPixel(0, 0));
            Assert.AreEqual(bottomLeft.GetPixel(0, 0), topLeftTga.GetPixel(0, 0));
            Assert.AreEqual(bottomLeft.GetPixel(0, H - 1), topLeftTga.GetPixel(0, H - 1));
        }

        // ---------------------------------------------------------------- SDO 那一半不准跟著動

        [Test]
        public void SdoRowOrder_StaysTheD3dConvention_AndIsTheOppositeOfTheMmdOne()
        {
            // SDO 自己的素材（ScreenGameplay.Effects 的特效四邊形、SdoAvatarBuilder 的部件）用的是 D3D 列序：
            // 圖的**上**緣放在 SetPixels32 的第 0 列 ＝ Unity 取樣的**下**緣。這是整條 SDO 管線的前提，
            // 修 MMD 那一邊的時候絕對不能順手把這邊一起翻掉。
            // （沒帶參數的多載就是 sdoRowOrder:true —— 它不 readable，所以這裡驗的是那個值本身。）
            var sdo = Keep(DdsLoader.LoadTga(MakeTga(topLeft: false), sdoRowOrder: true, readable: true));
            var mmd = Keep(DdsLoader.LoadTga(MakeTga(topLeft: false), sdoRowOrder: false, readable: true));

            Assert.AreEqual((Color)Top, sdo.GetPixel(0, 0), "SDO 列序＝圖的上緣在 Unity 的下緣");
            Assert.AreNotEqual(sdo.GetPixel(0, 0), mmd.GetPixel(0, 0), "兩種列序本來就該相反");
        }

        [Test]
        public void MmdTgaTexturesStayReadable_OrTheAlphaScanSeesNothing()
        {
            // MmdAvatar 得逐材質統計貼圖的 alpha 才分得出不透明/裁切/半透明，那是 GetPixels32。
            // 貼圖被 Apply(_, makeNoLongerReadable:true) 丟掉 CPU 那一份的話，統計會整個抓不到 →
            // 所有用 .tga 的材質悄悄全被判成不透明（該裁切的地方變成一塊實心）。
            var tga = Keep(DdsLoader.LoadTga(MakeTga(topLeft: false), sdoRowOrder: false, readable: true));
            Assert.DoesNotThrow(() => tga.GetPixels32(), "MMD 用的 TGA 必須留著 CPU 那一份");
            Assert.AreEqual(W * H, tga.GetPixels32().Length);
        }
    }
}
