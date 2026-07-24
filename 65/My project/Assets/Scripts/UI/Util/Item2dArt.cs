using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Sdo.Game;

namespace Sdo.UI.Util
{
    /// <summary>
    /// 非衣服商品 (道具/藥水/特效卡/寵物/寵物食物) 的 2D 商品圖示載入器。
    ///
    /// 檔案怎麼找 = 官方那條路 (<see cref="DressCatalog"/>)：modelId → DRESS.TXT 的檔名 (帶拼音,如
    /// <c>100000_xiaolaba.an</c>) → 在 UI/ITEM2D_PACK* / UI/MATCHITEMS 找。.an 的內容是「圖集 + 裁切框」
    /// (<c>daoju_a.png (360,400,90,100)</c>) 或整張圖，跟 SHOP 的 art 同格式 → 直接複用
    /// <see cref="SdoExtracted.LoadAn1"/>。找不到 → null (呼叫端改畫 3D 或留空格)。
    /// </summary>
    public static class Item2dArt
    {
        private static readonly Dictionary<int, Sprite> _cache = new Dictionary<int, Sprite>();

        public static void Reset() => _cache.Clear();

        /// <summary>商品圖示 (.an 第一幀)；這個商品不是「2D 圖示」型 → null。</summary>
        public static Sprite Icon(int modelId)
        {
            if (_cache.TryGetValue(modelId, out var s)) return s;
            s = null;
            var path = DressCatalog.IconPath(modelId);
            if (path != null)
            {
                try { s = SdoExtracted.LoadAn1(Path.GetDirectoryName(path), Path.GetFileName(path), bleed: true); }
                catch { s = null; }
            }
            _cache[modelId] = s;
            return s;
        }

        /// <summary>這個商品有沒有 2D 圖示。</summary>
        public static bool Has(int modelId) => DressCatalog.IconPath(modelId) != null;
    }
}
