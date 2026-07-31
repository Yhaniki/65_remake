using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Sdo.Game;

namespace Sdo.UI.Util
{
    /// <summary>
    /// 玩家資訊視窗(官方 <c>DATA/UI/PLAYERINFORMATIONDLG</c>)的貼圖來源。結構照 <see cref="LobbySelArt"/>:
    /// 懶解析 Dir、An/AnFrames 帶 Dictionary 快取、找不到回 null(呼叫端不必判斷,UIKit.AddSprite 容忍 null)。
    ///
    /// 🔴 **不做 alpha bleed。** 這個資料夾的圖集把三態鈕**貼著排**(私聊 normal 在 (360,0,93,31)、
    ///    hover 就在 (453,0) —— 中間 0 px 間隔),bleed 會把隔壁那顆的不透明像素往外擴進我們的裁切邊,
    ///    變成一條彩色細邊。ROOM 那邊敢 bleed 是因為它的圖集四周是透明白底。
    ///
    /// 這裡另外提供 <see cref="AtlasCrop"/>(同 <see cref="RoomUiArt.AtlasCrop"/>):分頁標籤那條 350×39 的
    /// 長條在官方是 12 個 .an 檔在指同一張 BaseBoard2.png 的 8 個 y,用座標直接取比記 12 個檔名清楚得多。
    /// </summary>
    public static class PlayerInfoArt
    {
        public const string FolderName = "PLAYERINFORMATIONDLG";

        /// <summary>分頁標籤圖集(546×546)。整條 350×39 的 tab bar,一張只畫「自己那一格」。</summary>
        public const string TabAtlas = "BASEBOARD2.PNG";

        private static string _dir;
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, Sprite[]> _framesCache = new Dictionary<string, Sprite[]>();

        /// <summary>解析後的 PLAYERINFORMATIONDLG 資料夾(懶解析)。可設定給測試用(會清快取)。</summary>
        public static string Dir
        {
            get { return _dir ?? (_dir = Path.Combine(SdoExtracted.Root, "UI", FolderName)); }
            set { _dir = value; _cache.Clear(); _framesCache.Clear(); }
        }

        /// <summary>一個 .an 的第一幀(已快取);找不到回 null。名字可含可不含 ".an"。</summary>
        public static Sprite An(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return null;
            if (_cache.TryGetValue(anName, out var s) && s != null) return s;
            s = SdoExtracted.LoadAn1(Dir, anName);
            _cache[anName] = s;
            return s;
        }

        /// <summary>一個 .an 的全部幀(已快取)。這個視窗目前沒有動畫,留著是為了與其它 *Art 樣板一致。</summary>
        public static Sprite[] AnFrames(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return new Sprite[0];
            if (_framesCache.TryGetValue(anName, out var s) && s != null) return s;
            s = SdoExtracted.LoadAn(Dir, anName);
            _framesCache[anName] = s;
            return s;
        }

        /// <summary>直接以官方 .an 的 top-left 座標裁圖集(y 在這裡是「從上往下」,與 .an 檔一致)。</summary>
        public static Sprite AtlasCrop(string imageName, int x, int y, int w, int h)
        {
            if (string.IsNullOrEmpty(imageName) || w <= 0 || h <= 0) return null;
            string key = "atlas:" + imageName + ":" + x + "," + y + "," + w + "," + h;
            if (_cache.TryGetValue(key, out var s) && s != null) return s;

            var tex = SdoExtracted.LoadTextureRaw(Dir, imageName)
                      ?? SdoExtracted.LoadTextureRaw(Dir, imageName.ToUpperInvariant());
            if (tex == null) return null;
            if (x < 0 || y < 0 || x + w > tex.width || y + h > tex.height) return null;

            var rect = new Rect(x, tex.height - y - h, w, h);
            s = Sprite.Create(tex, rect, new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
            _cache[key] = s;
            return s;
        }

        // ---------------------------------------------------------------- 具名版位

        /// <summary>主框。0=女(BaseBoard 629×512 粉紫「个人信息」)、1=男(BaseBoard_man 625×502 星空「个人资料」)。</summary>
        public static Sprite Board(int gender)
            => An(gender == 1 ? "PlayerInformationDlg0_MAN" : "PlayerInformationDlg0");

        /// <summary>
        /// 分頁標籤那條 350×39。官方把四格疊在同一個座標,每張只畫自己那一格 + 底下那條橘線,
        /// 所以「畫面上的 tab bar」= 把每一格各自的狀態圖疊起來。
        /// 已選在 y=240 起、未選在 y=396 起,每格間隔 39(對得上官方 Dlg4/6、7/9、10/12、158/160 六個 .an)。
        ///
        /// ⚠️ <c>index 3 + 未選</c>(官方 PlayerInformationDlg158.an,y=513)會**回 null**:那個 .an 寫的是
        ///    (0,513,350,39) → 底邊 552,而 BaseBoard2.png 只有 546 高 —— 官方那份資料自己就越界了,
        ///    <see cref="AtlasCrop"/> 的邊界檢查會擋下來。目前只畫得出兩個分頁所以碰不到;哪天真的要開第四格
        ///    (拼图卡片),得先決定那 6px 怎麼補,不要以為是載入器壞了。
        /// </summary>
        public static Sprite TabStrip(int index, bool selected)
        {
            if (index < 0 || index > 3) return null;
            return AtlasCrop(TabAtlas, 0, (selected ? 240 : 396) + index * 39, 350, 39);
        }

        // 底部動作鈕(93×31)。三態都在 BaseBoard2_man.png —— 女版框也用同一組,官方就是這樣共用的。
        public static Sprite WhisperN => An("PlayerInformationDlg17");
        public static Sprite WhisperH => An("PlayerInformationDlg18");
        public static Sprite WhisperP => An("PlayerInformationDlg19");
        public static Sprite AddFriendN => An("PlayerInformationDlg20");
        public static Sprite AddFriendH => An("PlayerInformationDlg21");
        public static Sprite AddFriendP => An("PlayerInformationDlg22");
        public static Sprite DelFriendN => An("PlayerInformationDlg39");
        public static Sprite DelFriendH => An("PlayerInformationDlg40");
        public static Sprite DelFriendP => An("PlayerInformationDlg41");

        // 確定鈕。
        // 🔴 用 **_MAN** 那三個檔,不要用 PlayerInformationDlg29/30/31:後者寫的是 BaseBoard_man.png
        //    (0,512,86,35),但這份資料包裡那顆鈕實際落在 (0,502,101,37) —— 用 29 會把鈕切掉一角、
        //    還帶到下一顆的邊。29_MAN/30_MAN/31_MAN 的座標才對得上。
        public static Sprite OkN => An("PlayerInformationDlg29_MAN");
        public static Sprite OkH => An("PlayerInformationDlg30_MAN");
        public static Sprite OkP => An("PlayerInformationDlg31_MAN");

        // 右上角的 X(29×29)。
        public static Sprite CloseN => An("PlayerInformationDlg14");
        public static Sprite CloseH => An("PlayerInformationDlg15");
        public static Sprite CloseP => An("PlayerInformationDlg16");

        /// <summary>比率長條的填色(232×19 粉紅圓角)。官方 ProgressBar 的 forename,我們拿它做 Filled 填充。</summary>
        public static Sprite RateBar => An("PlayerInformationDlg65");
    }
}
