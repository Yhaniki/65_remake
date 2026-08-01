using System.Collections.Generic;
using UnityEngine;
using Sdo.Game;

namespace Sdo.UI.Util
{
    /// <summary>
    /// 「照官方 .an 的座標從圖集裁一塊,**複製到自己的貼圖**」——大底圖(視窗底板、分頁內容板、對話框框體)
    /// 專用的載入路徑。原本長在 <see cref="PlayerInfoArt"/> 裡,<see cref="LobbyArt"/> 的房間信息底圖也要用,
    /// 所以抽出來共用(兩份實作遲早會長歪)。
    ///
    /// 🔴 為什麼大底圖不能走另外兩條路(這兩件事都是使用者連續回報好幾輪才查出來的):
    ///    ・**共用圖集**(<c>LoadAn1</c>):sprite 的邊緣取樣會把 rect **外面**的像素拖進來。
    ///      BaseBoard_man.png 上底板 (0,0,625,502) 與基本頁底圖 (624,0,348,337) 是貼著的;
    ///      stage.png 上 stageinfoBG (0,533,341,423) 右邊隔 1px 就是一片不透明橘色 ——
    ///      兩者都會在框邊滲出一條鄰居的顏色。
    ///    ・**solo**(<c>LoadAnSolo</c>):它的 DeMatteWhite 是為「圖集裡彼此貼著的小鈕」設計的,
    ///      套在大底圖上會把邊緣那圈半透明像素壓成深色 → 框外多一條黑邊。
    ///    複製到自己的貼圖 + Clamp 之後兩個問題都不存在:沒有鄰居可滲,也不做任何邊緣壓暗。
    /// </summary>
    public static class AtlasCropper
    {
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();

        /// <summary>清快取(給測試換資料夾時用)。</summary>
        public static void Clear() { _cache.Clear(); }

        /// <summary>
        /// 直接以官方 .an 的 top-left 座標裁圖集(y 在這裡是「從上往下」,與 .an 檔一致)。
        /// 超出圖片範圍回 null —— 官方有些 .an 的 crop 真的會超界(見 PlayerInfoArt 的 ZoSelect_a)。
        /// </summary>
        public static Sprite Crop(string dir, string imageName, int x, int y, int w, int h)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(imageName) || w <= 0 || h <= 0) return null;
            string key = dir + "|" + imageName + ":" + x + "," + y + "," + w + "," + h;
            if (_cache.TryGetValue(key, out var s) && s != null) return s;

            var tex = SdoExtracted.LoadTextureRaw(dir, imageName)
                      ?? SdoExtracted.LoadTextureRaw(dir, imageName.ToUpperInvariant());
            if (tex == null) return null;
            if (x < 0 || y < 0 || x + w > tex.width || y + h > tex.height) return null;

            var px = tex.GetPixels32(0);
            var cut = new Color32[w * h];
            for (int row = 0; row < h; row++)
            {
                // 圖集是左下原點,.an 的 y 是左上原點 → 逐列反轉。
                int srcRow = tex.height - y - h + row;
                System.Array.Copy(px, srcRow * tex.width + x, cut, row * w, w);
            }
            // 透明像素的 RGB 常常是純白(工具的預設 matte)。把它換成同列最近的不透明色,
            // 邊緣做雙線性混色時才不會混出一圈白。
            BleedTransparent(cut, w, h);

            var own = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            own.SetPixels32(cut);
            own.Apply(false, false);

            s = Sprite.Create(own, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
            _cache[key] = s;
            return s;
        }

        /// <summary>
        /// 把透明像素的 RGB 換成鄰近不透明像素的顏色(alpha 完全不動,純外觀修正)。
        ///
        /// 🔴 **水平與垂直都要做,而且要跑好幾輪。** 只做水平的話,格子**上下**那片透明區的 RGB 還是
        ///    工具留下的純白 —— UI 放大顯示時雙線性會把那片白混進格子的邊,每一格就鑲一圈白邊
        ///    (使用者回報「tab 沒去背」)。每一輪把不透明區往外擴一圈,三輪就夠蓋住雙線性取樣摸得到的範圍。
        /// </summary>
        private static void BleedTransparent(Color32[] px, int w, int h)
        {
            const int Rounds = 3;
            // known = 「這個像素的 RGB 已經可信」。一開始只有不透明的算,每輪把它往外擴一圈 ——
            // 沒有這個標記的話,第二輪讀到的還是原本那片白,多跑幾輪等於白跑。
            var known = new bool[w * h];
            for (int i = 0; i < px.Length; i++) known[i] = px[i].a > 8;

            for (int round = 0; round < Rounds; round++)
            {
                var srcKnown = (bool[])known.Clone();
                var src = (Color32[])px.Clone();
                bool changed = false;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int k = y * w + x;
                        if (srcKnown[k]) continue;
                        if (TryNeighbour(src, srcKnown, w, h, x - 1, y, ref px[k]) ||
                            TryNeighbour(src, srcKnown, w, h, x + 1, y, ref px[k]) ||
                            TryNeighbour(src, srcKnown, w, h, x, y - 1, ref px[k]) ||
                            TryNeighbour(src, srcKnown, w, h, x, y + 1, ref px[k]))
                        { known[k] = true; changed = true; }
                    }
                if (!changed) break;
            }
        }

        private static bool TryNeighbour(Color32[] src, bool[] known, int w, int h, int x, int y, ref Color32 dst)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return false;
            int k = y * w + x;
            if (!known[k]) return false;
            var n = src[k];
            dst.r = n.r; dst.g = n.g; dst.b = n.b;   // alpha 保持原樣(透明的還是透明)
            return true;
        }
    }
}
