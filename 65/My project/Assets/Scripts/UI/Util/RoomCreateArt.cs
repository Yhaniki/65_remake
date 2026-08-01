using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Sdo.Game;

namespace Sdo.UI.Util
{
    /// <summary>
    /// 「創建遊戲房間」對話框(官方 <c>DATA/UI/LOBBYDLG/ROOMCREATEDLG</c>)的貼圖來源。
    /// 結構照 <see cref="PlayerInfoArt"/>:懶解析 Dir、Dictionary 快取、找不到回 null
    /// (呼叫端不必判斷,<c>UIKit.AddSprite</c> 容忍 null)。
    ///
    /// 整個對話框只有**一張圖集** <c>CREATEROOM.PNG</c>(512×512)—— 底圖、確定/取消三態、下拉箭頭三態、
    /// 下拉列、兩張房型圖、選取框全在裡面。另外四張 NEW1..4.PNG 是「New」閃爍標記,這個重製版沒有新模式,不用。
    ///
    /// 🔴 **小圖走 solo、底圖走共用圖集**,兩者的理由相反:
    ///    ・小圖(按鈕三態、下拉列、房型圖)在圖集裡是**貼著排**的 —— btn_OK_1 (44,288) 與 btn_OK_2 (117,288)
    ///      中間 0 px、ChoseState1 (302,0) 與 ChoseState3 (302,26) 上下相鄰、NormalRoom 與 CohabitRoom
    ///      左右相鄰。共用圖集取樣時雙線性會把隔壁那張的不透明像素拖進邊緣,變成一圈彩色細邊
    ///      (同 PLAYERINFORMATIONDLG 那包踩過的坑)→ 一律 <see cref="An"/>(複製到自己的貼圖)。
    ///    ・底圖 <c>background.an (0,0,302,288)</c> 反而**不能**走 solo:solo 那條路會 DeMatteWhite + Clamp,
    ///      把大底圖邊緣那圈半透明像素壓成深色 → 框外多一條黑邊。而它右邊 297..301 已驗證全透明、
    ///      下方 y=288 起才是 btn_Down_1,共用圖集不會滲到鄰居,所以走 LoadAn1 最安全。
    ///      **bleed 要關掉**:底圖的柔邊陰影 alpha 只有 3~82,bleed 會把它染髒。
    /// </summary>
    public static class RoomCreateArt
    {
        public const string FolderName = "ROOMCREATEDLG";

        private static string _dir;
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();

        /// <summary>解析後的資料夾(懶解析)。可設定給測試用(會清快取)。</summary>
        public static string Dir
        {
            get { return _dir ?? (_dir = Path.Combine(SdoExtracted.Root, "UI", "LOBBYDLG", FolderName)); }
            set { _dir = value; _cache.Clear(); }
        }

        /// <summary>小圖(按鈕三態、下拉列、房型圖、選取框)。走 solo —— 理由見類別註解。</summary>
        public static Sprite An(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return null;
            if (_cache.TryGetValue(anName, out var s) && s != null) return s;
            s = SdoExtracted.LoadAnSolo(Dir, anName, pad: 0) ?? SdoExtracted.LoadAn1(Dir, anName, bleed: true);
            _cache[anName] = s;
            return s;
        }

        /// <summary>對話框底圖(<c>background.an</c> = CREATEROOM.PNG (0,0,302,288),擺在 (301,151))。
        /// 走共用圖集且**不 bleed** —— 理由見類別註解。</summary>
        public static Sprite Board
        {
            get
            {
                const string key = "raw:background";
                if (_cache.TryGetValue(key, out var s) && s != null) return s;
                s = SdoExtracted.LoadAn1(Dir, "background", bleed: false);
                _cache[key] = s;
                return s;
            }
        }

        // ---------------------------------------------------------------- 具名版位
        // 名稱照官方 ROOMCREATEDLG.XML 的元件取,方便回頭對照。

        // 確定 / 取消(Dialog-OK (362,388) / Dialog-Cancel (466,388),各 73×30)。
        // 🔴 官方三態的 pushed 裁切框比 hover 往上 1px(y=287 而不是 288),抓到的是底圖陰影 ——
        //    那是官方刻意做的「按下往下位移 1px」,不是切錯,照抄就對。
        public static Sprite OkN => An("btn_OK_1");
        public static Sprite OkH => An("btn_OK_2");
        public static Sprite OkP => An("btn_OK_3");
        public static Sprite CancelN => An("btn_cancel_1");
        public static Sprite CancelH => An("btn_cancel_2");
        public static Sprite CancelP => An("btn_cancel_3");

        /// <summary>遊戲模式下拉的 ▼ 鈕(<c>mode</c> (544,256) 22×21)。</summary>
        public static Sprite ArrowN => An("btn_Down_1");
        public static Sprite ArrowH => An("btn_Down_2");
        public static Sprite ArrowP => An("btn_Down_3");

        // 下拉清單的列。ChoseState1 = 淡綠(normal)、ChoseState3 = 淡黃(hover/選中),都是 166×26。
        // 🔴 **不要用 ChoseState2** —— XML 完全沒引用它,裁出來是圖集雜訊(切到房型圖左緣)。
        public static Sprite RowN => An("ChoseState1");
        public static Sprite RowH => An("ChoseState3");

        // 房型二選一(NormalRoom (345,292) / CohabitRoom (467,292),各 84×85)。
        // 兩張縮圖都把「普通房间」「爱的小屋」四個字烤在圖上 → 程式不要另外畫字。
        public static Sprite NormalRoom => An("NormalRoom");
        public static Sprite CohabitRoom => An("CohabitRoom");
        /// <summary>愛的小屋不可選時的灰階版。官方是**換圖**,不是加一層遮罩。</summary>
        public static Sprite CohabitRoomGray => An("CohabitRoomGray");
        /// <summary>選中的房型套的橘色空心高亮框(84×85,中間是空的 —— 疊在縮圖**上面**,不是底板)。
        /// 對應的 <c>unchoose.an</c> 量過是全透明,所以「未選」= 什麼都不畫,不需要那張圖。</summary>
        public static Sprite ChooseBox => An("choosebox");
    }
}
