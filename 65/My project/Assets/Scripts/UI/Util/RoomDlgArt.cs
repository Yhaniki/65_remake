using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Sdo.Game;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Loads the original ROOMDLG (MUSICSELDLG) song-select art. The .an files reference crops of
    /// MUSICSELDLG.PNG / SCENE.PNG sitting in the same folder, so <see cref="SdoExtracted.LoadAn1"/>
    /// (PNG + crop + Y-flip,底圖快取) loads them directly — no DDS decode needed.
    ///
    /// Folder resolution reads DATA/UI/ROOMDLG under <see cref="SdoExtracted.Root"/> ONLY — no assets/ scan. The
    /// resolved data root (e.g. the pruned clean pack, pointed at via data_root.txt) is the single source for UI art.
    /// Returns null for a missing asset; callers guard.
    /// </summary>
    public static class RoomDlgArt
    {
        private static string _dir;
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, Sprite[]> _framesCache = new Dictionary<string, Sprite[]>();

        /// <summary>Resolved ROOMDLG art folder (lazy). Settable for tests.</summary>
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
                return Path.Combine(SdoExtracted.Root, "UI", "ROOMDLG");
            }
            catch
            {
                return Path.Combine(SdoExtracted.Root, "UI", "ROOMDLG");
            }
        }

        /// <summary>
        /// Pure pick: first candidate that <paramref name="exists"/>, else the LAST candidate
        /// (the built DATA fallback, which may not exist yet on a fresh package). Null if empty.
        /// </summary>
        public static string PickDir(IList<string> ordered, Func<string, bool> exists)
        {
            if (ordered == null || ordered.Count == 0) return null;
            foreach (var c in ordered)
                if (c != null && exists(c)) return c;
            return ordered[ordered.Count - 1];
        }

        // ROOMDLG「白邊」根因(與商城/結算同一件事,見 ShopArt 與 ResultPanelMatteTests):MUSICSELDLG.PNG 是白底去背的共用
        // 圖集,幾乎每個 crop 外緣都留著一圈 (255,255,255,α≈5~30) 的白 matte。舊的 An() 直接從共用大圖裁、用預設 straight-alpha
        // 材質畫,而整個 UI 是從 800×600 設計稿被拉伸放大到視窗的 —— bilinear 把「顏色」與「覆蓋率(alpha)」分開內插,透明區留著
        // 亮白 RGB、alpha 卻淡出 → 白邊沿著 CROP 的矩形滲出來(所以圓角鈕外面看到的白框是方的),取樣還會越界吃到 atlas 鄰居。
        // 根治 = 每張都自貼圖 + cleanMatte(清掉那圈低-α 白) + premultiplied alpha,配 Blend One OneMinusSrcAlpha 材質
        // (UIKit.ApplySprite 依 SdoExtracted.IsPremultTexture 自動掛)→ 內插只淡化覆蓋率、邊緣保色,任何倍率都乾淨。
        // 實測 ROOMDLG 全 48 張帶 matte 的圖:被 cleanMatte 清掉的 texel 平均 α ≤ 20(多數是 0),沒有一張會掉可見內容。
        //
        // 兩個必須留在舊路徑的例外 —— 見 AnRaw:premult 材質(Sdo/SpritePremultiply)沒有 Stencil/_ClipRect,而且 ApplySprite
        // 會把尺寸改回貼圖原生大小。

        /// <summary>First frame of a ROOMDLG .an, on its OWN texture with cleanMatte + PREMULTIPLIED alpha (cached);
        /// null if missing. **呼叫端必須用 <see cref="UIKit.ApplySprite"/>(或 AddSprite/AddSpriteButton)指派**,直接寫
        /// <c>Image.sprite =</c> 會讓 premult 貼圖用預設 straight-alpha 材質畫 → 整張偏暗。需要舊的共用大圖行為時用
        /// <see cref="AnRaw"/>。shader 被剝掉(PremultUiMaterial == null)或 crop 失敗時自動退回 AnRaw,只是白邊回來,
        /// 不會整片變暗。</summary>
        public static Sprite An(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return null;
            if (_cache.TryGetValue(anName, out var s) && s != null) return s;
            s = (SdoExtracted.PremultUiMaterial != null
                    ? SdoExtracted.LoadAnSoloPremultiplied(Dir, anName, pad: 0, cleanMatte: true)   // pad:0 → 尺寸同 atlas crop,不位移
                    : null)
                ?? AnRaw(anName);
            _cache[anName] = s;
            return s;
        }

        /// <summary>First frame straight off the SHARED atlas, straight-alpha (the pre-premult behaviour). Only for the
        /// two places where a premultiplied sprite would actually break something:
        ///   • <b>UI Mask / RectMask2D 的 graphic 與其子孫</b> —— Sdo/SpritePremultiply 沒有 Stencil block 也沒有
        ///     _ClipRect,遮罩對它無效(唱片圓盤 MusicSelDlg106 就是 Mask 的 showMaskGraphic);
        ///   • <b>被拉伸到非原生尺寸的圖</b> —— 要走 ApplySprite 才會掛上 premult 材質,而 ApplySprite 會把 sizeDelta 改回
        ///     貼圖原生大小(場景縮圖 Scene*.an 是硬塞進 205×90 的框)。
        /// 這兩類本來就沒有白邊問題(實測 matte 的 α 幾乎全 0),留在舊路徑沒有損失。</summary>
        public static Sprite AnRaw(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return null;
            string key = "raw:" + anName;
            if (_cache.TryGetValue(key, out var s) && s != null) return s;
            s = SdoExtracted.LoadAn1(Dir, anName);
            _cache[key] = s;
            return s;
        }

        /// <summary>ALL frames of a ROOMDLG .an as sprites (cached), each premultiplied like <see cref="An"/>.
        /// Multi-frame .an = one sprite per option, e.g. LABEL_SDO.an holds the 13 SDO mode-name slices
        /// (自由模式=frame 0, 普通模式=frame 1, …) — those render in SdoComboBox's value slot, so they need the same
        /// treatment as the ▲ next to them. Same rule: assign through ApplySprite.</summary>
        public static Sprite[] AnFrames(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return new Sprite[0];
            if (_framesCache.TryGetValue(anName, out var s) && s != null) return s;
            s = (SdoExtracted.PremultUiMaterial != null
                    ? SdoExtracted.LoadAnPremultiplied(Dir, anName, pad: 0, cleanMatte: true)
                    : null);
            if (s == null || s.Length == 0) s = SdoExtracted.LoadAn(Dir, anName);
            _framesCache[anName] = s;
            return s;
        }
    }
}
