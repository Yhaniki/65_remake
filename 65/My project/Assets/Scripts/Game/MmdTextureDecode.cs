using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 一張解好的圖 —— **純資料**,沒有任何 <see cref="Texture2D"/>。
    /// <see cref="Color32"/> / <see cref="TextureWrapMode"/> 都只是 struct 與 enum,不碰 engine,
    /// 所以整個型別可以在背景執行緒上產生,主執行緒只負責最後那一步(建 Texture2D ＋ 上傳)。
    /// </summary>
    public sealed class MmdDecodedImage
    {
        public Color32[] Pixels;
        public int Width, Height;
        /// <summary>要不要建 mipmap 鏈。<b>逐格式照舊</b>:PNG/JPG 有(<c>LoadImage</c> 的行為),TGA/BMP 沒有。</summary>
        public bool MipChain;
        /// <summary>TGA 是 Clamp,PNG/BMP 是 Repeat —— 照原本三條路徑各自的設定,不要統一。</summary>
        public TextureWrapMode Wrap;
        /// <summary>這張圖有沒有真的用到 alpha。沒有就建 RGB24(與 <c>LoadImage</c> 對不透明 PNG 的選擇一致,
        /// 2048² 省 4 MB);TGA 一律 RGBA32(原本就是)。</summary>
        public bool HasAlpha;
        /// <summary>false = 建 RGB24 也可以(見 <see cref="HasAlpha"/>);true = 一定要 RGBA32。</summary>
        public bool ForceRgba32;
    }

    /// <summary>
    /// MMD 模型貼圖的**背景執行緒**解碼 —— 「讀檔 ＋ 把位元組變成像素」這一整段。
    ///
    /// 🔴 為什麼要自己解 PNG:量過的成本裡,一張 2048² 貼圖約 140 ms 的「解碼＋mipmap」,而
    /// <c>Texture2D.LoadImage</c> **只能在主執行緒呼叫** —— 走它就等於整段都留在主執行緒上,
    /// 而那正是「收到別人的模型時整個畫面凍住、連 ping 都停」的來源。自己解之後,
    /// 主執行緒只剩 <c>SetPixels32 + Apply</c>(mipmap 生成 ＋ 上傳 GPU),實測量級從 140 ms 掉到 20~30 ms。
    ///
    /// 支援的三條路徑與 <c>MmdAvatar.LoadTexture</c> 原本的三條**一一對應**(TGA / BMP / PNG),
    /// 而且方向、wrap、mipmap 全部照舊 —— 一個 MMD 模型會混用 .png 與 .tga,任何一條走偏的症狀都是
    /// 「一部分貼圖正、一部分上下顛倒」,而那看起來像 UV 的問題,查起來會繞很遠。
    ///
    /// <b>解不了就回 null</b>(JPG、16-bit PNG、隔行掃描 PNG、壞檔…)—— 呼叫端退回主執行緒的
    /// <c>LoadImage</c>,那是原本的行為,最壞情況就是「跟改動前一樣」,不會變成一張破圖。
    /// </summary>
    public static class MmdTextureDecode
    {
        /// <summary>這個副檔名/魔數走得到背景解碼嗎(走不到就是主執行緒 <c>LoadImage</c>)。</summary>
        public static MmdDecodedImage TryDecode(byte[] bytes, string ext)
        {
            if (bytes == null || bytes.Length < 8) return null;
            try
            {
                if (string.Equals(ext, ".tga", StringComparison.OrdinalIgnoreCase))
                {
                    var px = DdsLoader.TgaPixels(bytes, sdoRowOrder: false, out int w, out int h);
                    // sdoRowOrder:false ＝ 與 LoadImage 同向。MMD 模型混用 PNG 與 TGA,方向不一致就是半具身體顛倒。
                    return px == null ? null : new MmdDecodedImage
                    {
                        Pixels = px, Width = w, Height = h,
                        MipChain = false, Wrap = TextureWrapMode.Clamp, ForceRgba32 = true, HasAlpha = true,
                    };
                }
                if (bytes[0] == 'B' && bytes[1] == 'M') return DecodeBmp(bytes);
                if (IsPng(bytes)) return DecodePng(bytes);
            }
            catch { return null; }   // 壞檔 → 退回 LoadImage(它自己也會失敗,但錯誤處理集中在一處)
            return null;
        }

        private static bool IsPng(byte[] d)
            => d.Length > 8 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G'
            && d[4] == 0x0D && d[5] == 0x0A && d[6] == 0x1A && d[7] == 0x0A;

        // ---------------------------------------------------------------- BM

        /// <summary>BMP → 像素。與原本 <c>MmdAvatar.DecodeBmp</c> 同一段邏輯(RGBA32、Repeat、無 mipmap)。</summary>
        private static MmdDecodedImage DecodeBmp(byte[] d)
        {
            if (d.Length < 54) return null;
            int dataOff = BitConverter.ToInt32(d, 10), w = BitConverter.ToInt32(d, 18), h = BitConverter.ToInt32(d, 22);
            int bpp = BitConverter.ToUInt16(d, 28), comp = BitConverter.ToInt32(d, 30);
            if (comp != 0 || (bpp != 24 && bpp != 32) || w <= 0 || h == 0) return null;
            bool topDown = h < 0;
            int H = Math.Abs(h), bpe = bpp / 8, stride = ((w * bpe + 3) / 4) * 4;
            if (dataOff < 0 || (long)dataOff + (long)stride * H > d.Length) return null;
            var px = new Color32[w * H];
            for (int y = 0; y < H; y++)
            {
                int srcRow = dataOff + (topDown ? (H - 1 - y) : y) * stride, dstRow = y * w;
                for (int x = 0; x < w; x++)
                {
                    int s = srcRow + x * bpe;
                    px[dstRow + x] = new Color32(d[s + 2], d[s + 1], d[s], bpe == 4 ? d[s + 3] : (byte)255);
                }
            }
            return new MmdDecodedImage
            {
                Pixels = px, Width = w, Height = H,
                MipChain = false, Wrap = TextureWrapMode.Repeat, ForceRgba32 = true, HasAlpha = bpe == 4,
            };
        }

        // ---------------------------------------------------------------- PNG

        /// <summary>
        /// PNG → 像素。支援 bit depth 8、color type 0/2/3/4/6、interlace 0(＝實務上 MMD 貼圖的全部)。
        /// 其他變體(16-bit、Adam7 隔行)回 null 讓呼叫端退回 <c>LoadImage</c>。
        ///
        /// 列序:PNG 的第 0 列是圖的**上**緣,<c>SetPixels32</c> 的第 0 列是**下**緣 → 這裡直接寫反過來,
        /// 出來的陣列與 <c>LoadImage</c> 的結果逐像素相同(<c>MmdPngDecodeTests</c> 就是拿
        /// <c>EncodeToPNG</c>／<c>LoadImage</c> 當黃金樣本比對的)。
        /// </summary>
        private static MmdDecodedImage DecodePng(byte[] d)
        {
            int w = 0, h = 0, bitDepth = 0, colorType = -1, interlace = 0;
            byte[] palette = null, trns = null;
            var idat = new MemoryStream();

            int p = 8;
            while (p + 8 <= d.Length)
            {
                int len = ReadBe32(d, p);
                if (len < 0 || p + 12 + len > d.Length) break;
                string type = "" + (char)d[p + 4] + (char)d[p + 5] + (char)d[p + 6] + (char)d[p + 7];
                int dataAt = p + 8;
                switch (type)
                {
                    case "IHDR":
                        if (len < 13) return null;
                        w = ReadBe32(d, dataAt); h = ReadBe32(d, dataAt + 4);
                        bitDepth = d[dataAt + 8]; colorType = d[dataAt + 9];
                        if (d[dataAt + 10] != 0 || d[dataAt + 11] != 0) return null;   // 未知的壓縮/濾波方式
                        interlace = d[dataAt + 12];
                        break;
                    case "PLTE": palette = Slice(d, dataAt, len); break;
                    case "tRNS": trns = Slice(d, dataAt, len); break;
                    case "IDAT": idat.Write(d, dataAt, len); break;
                    case "IEND": p = d.Length; break;
                }
                p = dataAt + len + 4;   // + CRC
            }

            if (w <= 0 || h <= 0 || bitDepth != 8 || interlace != 0) return null;
            if (colorType != 0 && colorType != 2 && colorType != 3 && colorType != 4 && colorType != 6) return null;
            if (colorType == 3 && palette == null) return null;
            if ((long)w * h > 64L * 1024 * 1024) return null;   // 防呆:壞檔頭撐出天文數字的配置

            int channels = colorType == 0 ? 1 : colorType == 2 ? 3 : colorType == 3 ? 1 : colorType == 4 ? 2 : 4;
            int stride = w * channels;
            byte[] raw = Inflate(idat.ToArray(), (long)(stride + 1) * h);
            if (raw == null || raw.Length < (long)(stride + 1) * h) return null;

            // 反濾波:每一列前面有一個 filter byte,而 Paeth/Up 會參照**上一列已還原**的位元組。
            var cur = new byte[stride];
            var prev = new byte[stride];
            var px = new Color32[w * h];
            int src = 0;
            for (int y = 0; y < h; y++)
            {
                int filter = raw[src++];
                Buffer.BlockCopy(raw, src, cur, 0, stride);
                src += stride;
                Unfilter(filter, cur, prev, channels);
                // PNG 第 0 列 = 圖的上緣;SetPixels32 第 0 列 = 下緣 → 反著寫。
                Emit(cur, px, (h - 1 - y) * w, w, colorType, palette, trns);
                var swap = prev; prev = cur; cur = swap;
            }

            bool hasAlpha = colorType == 4 || colorType == 6 || (colorType == 3 && trns != null)
                         || (colorType == 0 && trns != null) || (colorType == 2 && trns != null);
            return new MmdDecodedImage
            {
                Pixels = px, Width = w, Height = h,
                MipChain = true, Wrap = TextureWrapMode.Repeat, ForceRgba32 = false, HasAlpha = hasAlpha,
            };
        }

        private static void Unfilter(int filter, byte[] cur, byte[] prev, int bpp)
        {
            int n = cur.Length;
            switch (filter)
            {
                case 0: break;
                case 1:
                    for (int i = bpp; i < n; i++) cur[i] = (byte)(cur[i] + cur[i - bpp]);
                    break;
                case 2:
                    for (int i = 0; i < n; i++) cur[i] = (byte)(cur[i] + prev[i]);
                    break;
                case 3:
                    for (int i = 0; i < n; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0;
                        cur[i] = (byte)(cur[i] + ((a + prev[i]) >> 1));
                    }
                    break;
                case 4:
                    for (int i = 0; i < n; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0;
                        int b = prev[i];
                        int c = i >= bpp ? prev[i - bpp] : 0;
                        int pp = a + b - c, pa = Math.Abs(pp - a), pb = Math.Abs(pp - b), pc = Math.Abs(pp - c);
                        int pred = (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
                        cur[i] = (byte)(cur[i] + pred);
                    }
                    break;
                default: break;   // 未知的 filter → 當成 None(壞檔,總比丟例外好)
            }
        }

        private static void Emit(byte[] row, Color32[] px, int dst, int w, int colorType, byte[] pal, byte[] trns)
        {
            switch (colorType)
            {
                case 0:   // 灰階
                    for (int x = 0; x < w; x++)
                    {
                        byte g = row[x];
                        byte a = (byte)255;
                        if (trns != null && trns.Length >= 2 && trns[1] == g) a = 0;   // tRNS:單一透明灰階值
                        px[dst + x] = new Color32(g, g, g, a);
                    }
                    break;
                case 2:   // RGB
                    for (int x = 0; x < w; x++)
                    {
                        int s = x * 3;
                        byte a = (byte)255;
                        if (trns != null && trns.Length >= 6 && trns[1] == row[s] && trns[3] == row[s + 1] && trns[5] == row[s + 2]) a = 0;
                        px[dst + x] = new Color32(row[s], row[s + 1], row[s + 2], a);
                    }
                    break;
                case 3:   // 調色盤
                    for (int x = 0; x < w; x++)
                    {
                        int i = row[x], s = i * 3;
                        byte r = (byte)(s + 2 < pal.Length ? pal[s] : 0);
                        byte g = (byte)(s + 2 < pal.Length ? pal[s + 1] : 0);
                        byte b = (byte)(s + 2 < pal.Length ? pal[s + 2] : 0);
                        byte a = (byte)(trns != null && i < trns.Length ? trns[i] : 255);
                        px[dst + x] = new Color32(r, g, b, a);
                    }
                    break;
                case 4:   // 灰階 + alpha
                    for (int x = 0; x < w; x++)
                    {
                        int s = x * 2; byte g = row[s];
                        px[dst + x] = new Color32(g, g, g, row[s + 1]);
                    }
                    break;
                default:  // 6 = RGBA
                    for (int x = 0; x < w; x++)
                    {
                        int s = x * 4;
                        px[dst + x] = new Color32(row[s], row[s + 1], row[s + 2], row[s + 3]);
                    }
                    break;
            }
        }

        /// <summary>zlib(RFC 1950)→ 原始位元組。前 2 byte 是 zlib 表頭、尾巴 4 byte 是 adler32,
        /// 中間才是 <see cref="DeflateStream"/> 認得的 raw deflate。</summary>
        private static byte[] Inflate(byte[] z, long expected)
        {
            if (z == null || z.Length < 3 || expected <= 0 || expected > int.MaxValue) return null;
            var outBuf = new byte[expected];
            int got = 0;
            using (var src = new MemoryStream(z, 2, z.Length - 2, false))
            using (var inf = new DeflateStream(src, CompressionMode.Decompress))
            {
                while (got < outBuf.Length)
                {
                    int n = inf.Read(outBuf, got, outBuf.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
            }
            return got == outBuf.Length ? outBuf : null;
        }

        private static int ReadBe32(byte[] d, int at)
            => (d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3];

        private static byte[] Slice(byte[] d, int at, int len)
        {
            var b = new byte[len];
            Buffer.BlockCopy(d, at, b, 0, len);
            return b;
        }
    }
}
