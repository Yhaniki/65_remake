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

        // 左下角的「圓形」鈕 (♂/♀/reset/購物車) 的所有狀態變體:美術是白底出圖,圓盤外緣一圈 (255,255,255,~0) 白 matte,
        // 放大時 bilinear 會把白拉進邊緣 → 圓形遮罩 (LoadAnSoloCircular) 專門切掉。刻意只列這幾顆真圓的鈕:
        // 全身購買 (Shop174/175/176) 是長條、非圓,且沒有白 matte(透明區是洋紅光暈),不可切,故不列入。其它素材也不受影響。
        private static readonly HashSet<string> CircularOrbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Shop45", "Shop46",            // 男 (normal / pushed)
            "Shop47", "Shop48",            // 女
            "Shop15", "Shop16", "Shop17",  // reset 復原穿搭 (normal / hover / pushed)
            "Shop206", "Shop207", "Shop208", // 購物車
        };

        private static bool IsCircular(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return false;
            var key = anName.EndsWith(".an", StringComparison.OrdinalIgnoreCase) ? anName.Substring(0, anName.Length - 3) : anName;
            return CircularOrbs.Contains(key);
        }

        // SHOP 美術很多是「白底去背」PNG：AA 邊緣殘留半透明白 → 疊在深色 UI 上會有白邊。AlphaBleed 只補全透明像素、
        // 補不到半透明白邊，所以每張底圖再做一次白色 de-matte 把白邊洗掉 (每個 texture 只做一次)。
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

        /// <summary>First frame of a SHOP .an as a sprite (cached). 每張都載成「自己的貼圖」(LoadAnSolo) — SHOP 的大圖集
        /// (ShopBtn.png) 空白區是透明白 (255,255,255,0)，sprite crop 緊貼鄰居時 bilinear 會把鄰居的透明白拉進邊緣成白邊
        /// (#1「左下角按鈕白邊」)。切到獨立貼圖 + Clamp → 取樣不會越過自己的邊界抓到鄰居；pad:0 保持原尺寸不位移。
        /// AlphaBleed/DeMatteWhite 仍在 LoadAnSolo 內處理自身邊緣。極少數載不到 crop → 回退舊的共用大圖路徑。</summary>
        public static Sprite An(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return null;
            if (_cache.TryGetValue(anName, out var s) && s != null) return s;
            // 圓形鈕走 LoadAnSoloCircular (flood + 圓形遮罩,消放大白邊);其餘走一般 LoadAnSolo。gating 放在這個唯一 choke
            // point,連 RefreshToggles 切換男女時重貼 sprite 也自動吃到遮罩 (若在呼叫端 gating,切一次就被無遮罩的 sprite 蓋回)。
            s = (IsCircular(anName)
                    ? SdoExtracted.LoadAnSoloCircular(Dir, anName, pad: 0)
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
