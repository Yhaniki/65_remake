using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Sdo.Game;

namespace Sdo.UI.Util
{
    /// <summary>
    /// 大廳美術的載入器 —— 照 <see cref="LobbySelArt"/> 的樣板,只換資料夾 leaf。
    ///
    /// 🔴 資料夾是 <b>STATECOMMUNITYHALL</b>(社區大廳,官方 <c>CStateCommunityHall</c>)而**不是** UI/LOBBY。
    ///    兩者都叫「大廳」但是兩套不同的版面:UI/LOBBY 是單欄六列的青色版(另一個 state 用的),
    ///    STATECOMMUNITYHALL 才是玩家實際看到的那個 —— 星空底、**兩欄三列**的紫色房卡、
    ///    右下角一排「創建舞台 / 快速進入 / 等待舞台 / 活動查詢」。版位檔 STATECOMMUNITYHALL.XML。
    ///    (兩邊的 .an 檔名大量重複(Lobby26/28/47/98…),所以看檔名分不出來,只能看它裁的是哪張圖:
    ///     這一套幾乎整包裁自同一張 STAGE.PNG 圖集。)
    ///
    /// 解析路徑固定是 <see cref="SdoExtracted.Root"/>/UI/STATECOMMUNITYHALL,**不掃 assets/** ——
    /// data_root.txt 指到哪一份 DATA,大廳就吃那一份(與 RoomUiArt 同一條政策)。
    ///
    /// <c>bleed: true</c> 是刻意的:透明區存的是 (255,255,255,0),而房卡是圓角 ——
    /// 雙線性取樣會把那片白拖進圓角邊,結果每張卡都鑲一圈白邊。
    /// 把不透明的 RGB 往透明區膨脹一圈就沒了(alpha 完全不動,純外觀修正)。
    ///
    /// 找不到檔案回 null / 空陣列 —— 呼叫端不必判斷(<c>UIKit.AddSprite</c> 容忍 null,只是畫面缺圖)。
    /// </summary>
    public static class LobbyArt
    {
        public const string FolderName = "STATECOMMUNITYHALL";

        private static string _dir;
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, Sprite[]> _framesCache = new Dictionary<string, Sprite[]>();

        /// <summary>Resolved 大廳 art folder (lazy). Settable for tests (clears the cache).</summary>
        public static string Dir
        {
            get { return _dir ?? (_dir = Path.Combine(SdoExtracted.Root, "UI", FolderName)); }
            set { _dir = value; _cache.Clear(); _framesCache.Clear(); }
        }

        /// <summary>First frame of a LOBBY .an as a sprite (cached); null if missing. Name may include or omit ".an".</summary>
        public static Sprite An(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return null;
            if (_cache.TryGetValue(anName, out var s) && s != null) return s;
            s = SdoExtracted.LoadAn1(Dir, anName, bleed: true);
            _cache[anName] = s;
            return s;
        }

        /// <summary>
        /// ALL frames of a LOBBY .an as sprites (cached).
        ///
        /// 目前只有數字條在用(LobbyNum1 → num1\0..9.png 共 10 格、LobbyNum2 → num2\0..9.png):
        /// 那是「按 index 取第幾個數字」,所以**一定**要整組,不能只拿第一幀 —— 見 <see cref="Digit"/>。
        /// (Lobby45「物品商店」雖然也是 15 幀的閃爍動畫,但大廳沒有做那個動畫,走 <see cref="An"/> 取首幀。)
        /// </summary>
        public static Sprite[] AnFrames(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return new Sprite[0];
            if (_framesCache.TryGetValue(anName, out var s) && s != null) return s;
            s = SdoExtracted.LoadAn(Dir, anName, bleed: true);
            _framesCache[anName] = s;
            return s;
        }

        /// <summary>
        /// 數字條的第 <paramref name="digit"/> 格(0..9)。<paramref name="anName"/> 是 LobbyNum1 / LobbyNum2。
        /// 超出範圍或素材缺了回 null —— 呼叫端直接餵給 <c>UIKit.ApplySprite</c> 就會變成看不見的那格。
        /// </summary>
        public static Sprite Digit(string anName, int digit)
        {
            if (digit < 0 || digit > 9) return null;
            var frames = AnFrames(anName);
            return frames != null && digit < frames.Length ? frames[digit] : null;
        }
    }
}
