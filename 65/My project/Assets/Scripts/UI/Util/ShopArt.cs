using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Sdo.Game;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Loads the original 商城 (SHOP) UI art — mirrors <see cref="RoomUiArt"/> / <see cref="RoomDlgArt"/> exactly, only
    /// the folder leaf differs (SHOP). The .an files reference crops of SHOP.png / atlas pages in the same folder, so
    /// <see cref="SdoExtracted.LoadAn1"/> (PNG + crop + Y-flip) loads them directly — no DDS decode needed. Folder
    /// resolution reads DATA/UI/SHOP under <see cref="SdoExtracted.Root"/> ONLY — no assets/ scan (the resolved data
    /// root, e.g. the pruned clean pack via data_root.txt, is the single UI source). Returns null for a missing asset; callers guard.
    /// </summary>
    public static class ShopArt
    {
        private static string _dir;
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, Sprite[]> _framesCache = new Dictionary<string, Sprite[]>();
        private static readonly HashSet<Texture2D> _deMatted = new HashSet<Texture2D>();

        // 商城「白邊」根因:美術幾乎每張都是白底去背 PNG,鈕/格/頁籤外緣一圈亮 matte。原圖原生尺寸疊黑底乾淨,但整個商城 UI 從
        // 800×600 設計稿拉伸放大時,預設 straight-alpha 材質的 bilinear 把「顏色」與「覆蓋率(alpha)」分開內插 —— 透明區留著亮 RGB、
        // alpha 卻淡出 → 亮邊滲成半透明白邊 (方形格/圓鈕/頁籤全中)。純 GPU 取樣 artifact,DeMatteWhite/AlphaBleed/圓形遮罩都治不好
        // (AlphaFlood 還會把鈕頂高光灌進外圈 → 放大又被抓回)。根治 = 整個商城改 premultiplied alpha:每張都自貼圖 RGB×alpha 讓透明
        // 像素變 (0,0,0,0),配 Blend One OneMinusSrcAlpha 材質 (UIKit.ApplySprite 依 SdoExtracted.IsPremultTexture 自動掛),內插
        // 只淡化覆蓋率、邊緣保持原色 → 任何倍率都無白邊且平滑,黑底放大 == 原生 (同結算 YOU WIN 旗的修法;黑底三欄模擬已驗證)。
        // 對不透明底圖 (Shop0 全屏背景等) premult 是 no-op → 安全。全身購买的白是字體描邊(美術)不受影響。

        // SHOP 美術很多是「白底去背」PNG：AA 邊緣殘留半透明白 → 疊在深色 UI 上會有白邊。AlphaBleed 只補全透明像素、
        // 補不到半透明白邊，所以每張底圖再做一次白色 de-matte 把白邊洗掉 (每個 texture 只做一次)。(premult 路徑無此問題,只有
        // fallback 到共用大圖的 LoadAn1 才需要。)
        private static Sprite DeMatte(Sprite s)
        {
            if (s != null && s.texture != null && _deMatted.Add(s.texture)) SdoExtracted.DeMatteWhite(s.texture);
            return s;
        }

        /// <summary>Resolved SHOP art folder (lazy). Settable for tests.</summary>
        public static string Dir
        {
            get { return _dir ?? (_dir = ResolveDir()); }
            set { _dir = value; _cache.Clear(); _framesCache.Clear(); }
        }

        private static string ResolveDir()
        {
            try
            {
                // Use the resolved data root ONLY — no assets/ scan (data_root.txt points this at the clean pack).
                return Path.Combine(SdoExtracted.Root, "UI", "SHOP");
            }
            catch { return Path.Combine(SdoExtracted.Root, "UI", "SHOP"); }
        }

        /// <summary>First frame of a SHOP .an as a sprite (cached). 每張都載成「自己的貼圖 + premultiplied alpha」
        /// (LoadAnSoloPremultiplied) — SHOP UI 放大時 straight-alpha 會滲白邊,premult 徹底消掉 (見上方註解)。UIKit.ApplySprite
        /// 依 SdoExtracted.IsPremultTexture 自動把 premult 材質掛到 Image 上,所以呼叫端不需改。pad:0 保持原尺寸不位移。
        /// 極少數載不到 crop → 回退舊的共用大圖路徑 (LoadAn1 + DeMatte,straight-alpha)。</summary>
        public static Sprite An(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return null;
            if (_cache.TryGetValue(anName, out var s) && s != null) return s;
            // premult 需要 Sdo/SpritePremultiply 材質才畫得對;材質在時走 premult(消白邊),萬一 shader 被剝掉則退回舊 straight-alpha
            // LoadAnSolo(白邊回來但不會整片變暗)——用 PremultUiMaterial 是否為 null 當這個 gate。
            s = (SdoExtracted.PremultUiMaterial != null
                    ? SdoExtracted.LoadAnSoloPremultiplied(Dir, anName, pad: 0, cleanMatte: true)   // cleanMatte 清鈕外緣殘留的低-alpha 白 matte(右上白邊)
                    : SdoExtracted.LoadAnSolo(Dir, anName, pad: 0))
                ?? DeMatte(SdoExtracted.LoadAn1(Dir, anName, bleed: true));
            _cache[anName] = s;
            return s;
        }

        /// <summary>Alias of <see cref="An"/> (An 現在本來就是自貼圖、無鄰居白邊)。保留給既有 solo:true 呼叫點。</summary>
        public static Sprite AnSolo(string anName) => An(anName);

        /// <summary>All frames of a SHOP .an (cached).</summary>
        public static Sprite[] AnFrames(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return new Sprite[0];
            if (_framesCache.TryGetValue(anName, out var s) && s != null) return s;
            s = SdoExtracted.LoadAn(Dir, anName, bleed: true);
            foreach (var fr in s) DeMatte(fr);
            _framesCache[anName] = s;
            return s;
        }
    }
}
