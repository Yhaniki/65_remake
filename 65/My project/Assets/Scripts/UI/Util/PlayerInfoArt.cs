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
    /// 🔴 **素材一律走男版**(<c>BaseBoard_man.png</c> / <c>BaseBoard2_man.png</c>),不再依性別換皮。
    ///    以前 <c>Board(gender)</c> 會換成男版底圖,但 <c>PlayerInfoModal</c> 的版位常數整組抄自
    ///    **女版** <c>PLAYERINFORMATIONDLG.XML</c> —— 圖是男版、座標是女版,關閉鈕、分頁條、底部那排全部差幾個 px。
    ///    現在版位與素材統一取自 <c>PLAYERINFORMATIONDLG_MAN.XML</c> 的 <c>WinPlayerInfo</c>,所以底圖只剩一張。
    ///
    /// 🔴 **不做 alpha bleed。** 這個資料夾的圖集把三態鈕**貼著排**(私聊 normal 在 (360,0,93,31)、
    ///    hover 就在 (453,0) —— 中間 0 px 間隔),bleed 會把隔壁那顆的不透明像素往外擴進我們的裁切邊,
    ///    變成一條彩色細邊。ROOM 那邊敢 bleed 是因為它的圖集四周是透明白底。
    ///
    /// 這裡另外提供 <see cref="AtlasCrop"/>(同 <see cref="RoomUiArt.AtlasCrop"/>):分頁條那四格是唯一
    /// **不能**走 <see cref="An"/> 的東西,原因見 <see cref="TabStrip"/>。
    /// </summary>
    public static class PlayerInfoArt
    {
        public const string FolderName = "PLAYERINFORMATIONDLG";

        /// <summary>分頁條圖集(BaseBoard2_man.png,546×546)。底部那排 93×31 的動作鈕也都在這張裡。</summary>
        public const string TabAtlas = "BASEBOARD2_MAN.PNG";

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

        // ---------------------------------------------------------------- 分頁條

        // 四格 × 兩態的圖集座標,逐字取自各自的 .an(x,y,w,h 是 BaseBoard2_man.png 的左上原點座標):
        //   未選(高 37)  Dlg4_MAN (0,173,350,37)  Dlg7_MAN (3,210,350,37)  Dlg10_MAN (3,246,350,37)  Dlg158_MAN (3,282,350,37)
        //   已選(高 39)  Dlg6_MAN (0,4,356,39)    Dlg9_MAN (-3,47,350,39)  Dlg12_MAN (-5,90,350,39)  Dlg160_MAN (0,134,350,39)
        //
        // 🔴 **不等距、寬高也不一致**(已選的第一格是 356 寬,其餘 350;未選矮 2px),不要想用「起點 + index*39」
        //    這種公式算 —— 女版是等距的,男版不是,套公式會四格全錯位。查表。
        private static readonly int[,] TabNormalRect =
        {
            { 0, 173, 350, 37 }, { 3, 210, 350, 37 }, { 3, 246, 350, 37 }, { 3, 282, 350, 37 },
        };
        private static readonly int[,] TabSelectedRect =
        {
            { 0, 4, 356, 39 }, { -3, 47, 350, 39 }, { -5, 90, 350, 39 }, { 0, 134, 350, 39 },
        };

        /// <summary>
        /// 分頁條的第 <paramref name="index"/> 格(0-3)在選中/未選狀態的圖。官方把四格疊在同一個座標
        /// (<c>playerTabCheck0..3</c> 全在 (333,116)),未選的圖只畫自己那一格、其餘透明;選中的圖除了自己那格
        /// 還畫滿整條底線 —— 所以畫面上的 tab bar 是把四張疊起來,不是一張圖切四段。
        ///
        /// <paramref name="dx"/> 是擺放時要往右補的位移,呼叫端要擺在 <c>TabX + dx</c>。
        ///
        /// 🔴 這四格**一定要走 <see cref="AtlasCrop"/>,不能走 <see cref="An"/>**:Dlg9_MAN / Dlg12_MAN 的 x 是
        ///    **負數**(-3 / -5),而 <c>SdoExtracted.SpriteFromFrame</c> 對越界的處理是「退回整張圖」**且不報錯**
        ///    —— 用 An() 會把整張 546×546 圖集貼到分頁條的位置,畫面爛掉還查不到錯。AtlasCrop 對負值回 null。
        /// 🔴 負的 x 代表「左邊多留幾格透明」,所以這裡把裁切起點夾到 0、寬度扣掉同樣的差額,再用 dx 把圖往右
        ///    擺回去 —— 只夾 x 不補位移的話,選中的第 2/3 格會整張往左偏 3/5 px。
        /// </summary>
        public static Sprite TabStrip(int index, bool selected, out float dx)
        {
            dx = 0f;
            if (index < 0 || index > 3) return null;
            var t = selected ? TabSelectedRect : TabNormalRect;
            int x = t[index, 0], y = t[index, 1], w = t[index, 2], h = t[index, 3];
            int off = x < 0 ? -x : 0;
            dx = off;
            return AtlasCrop(TabAtlas, x + off, y, w - off, h);
        }

        // ---------------------------------------------------------------- 具名版位
        // 名稱照官方 XML 的元件名取,方便回頭對 PLAYERINFORMATIONDLG_MAN.XML 的 <Window name="WinPlayerInfo">。

        /// <summary>主框(官方 <c>DailogBg</c>):BaseBoard_man.png (0,0,625,502),擺在 (93,56)。</summary>
        public static Sprite Board => An("PlayerInformationDlg0_MAN");

        // 底部那一排動作鈕,全部 93×31、全部在 BaseBoard2_man.png 裡。
        public static Sprite WhisperN => An("PlayerInformationDlg17");     // Dialog     (108,507)
        public static Sprite WhisperH => An("PlayerInformationDlg18");
        public static Sprite WhisperP => An("PlayerInformationDlg19");
        public static Sprite AddFriendN => An("PlayerInformationDlg20");   // AddFriend  (208,507)
        public static Sprite AddFriendH => An("PlayerInformationDlg21");
        public static Sprite AddFriendP => An("PlayerInformationDlg22");
        public static Sprite DelFriendN => An("PlayerInformationDlg39");   // DelFriend  (208,508) —— 與 AddFriend 同格互斥
        public static Sprite DelFriendH => An("PlayerInformationDlg40");
        public static Sprite DelFriendP => An("PlayerInformationDlg41");
        public static Sprite MailN => An("PlayerInformationDlg23");        // SendMail   (308,507)
        public static Sprite MailH => An("PlayerInformationDlg24");
        public static Sprite MailP => An("PlayerInformationDlg25");
        /// <summary>加黑名單(官方 <c>AddEnemy</c> (408,508))。同格互斥的另一半是 <c>DelEnemy</c>
        /// (Dlg61/62/63,(408,507)) —— 沒有黑名單資料可切,所以只放這一顆,要做的時候照 AddFriend/DelFriend 那樣換圖。</summary>
        public static Sprite EnemyN => An("PlayerInformationDlg58");
        public static Sprite EnemyH => An("PlayerInformationDlg59");
        public static Sprite EnemyP => An("PlayerInformationDlg60");
        public static Sprite BuyLookN => An("BuyOtherEquipedButton1");     // BuyOtherEquipedButton (508,507)
        public static Sprite BuyLookH => An("BuyOtherEquipedButton2");
        public static Sprite BuyLookP => An("BuyOtherEquipedButton3");

        // 確定鈕(Confirm (608,507),101×37)。
        // 🔴 官方男版 XML 寫的就是 29_man/30_man/31_man,別「順手」換成 Dlg29/30/31:後者的 .an 寫
        //    BaseBoard_man.png (0,512,86,35),但這份資料包裡那顆鈕實際落在 (0,502,101,37) —— 用 29 會把鈕
        //    切掉一角、還帶到下一顆的邊。
        public static Sprite OkN => An("PlayerInformationDlg29_MAN");
        public static Sprite OkH => An("PlayerInformationDlg30_MAN");
        public static Sprite OkP => An("PlayerInformationDlg31_MAN");

        // 右上角的 X(29×29,close (662,73))。
        public static Sprite CloseN => An("PlayerInformationDlg14");
        public static Sprite CloseH => An("PlayerInformationDlg15");
        public static Sprite CloseP => An("PlayerInformationDlg16");

        // 左側那一直排功能鈕(官方順序由上而下)。素材散在好幾張圖集裡,照 .an 走就好。
        public static Sprite VipN => An("Vip0");                           // BtnVipSystem      (296,212) 36×36
        public static Sprite VipH => An("Vip1");
        public static Sprite VipP => An("Vip2");
        /// <summary>手鐲(<c>BtnBangleDlg</c> (295,249) 38×35)。🔴 官方只給 normal/hover 兩態,pushed 沒有素材。</summary>
        public static Sprite BangleN => An("BaseBoard_man0");
        public static Sprite BangleH => An("BaseBoard_man1");
        public static Sprite CertN => An("btnCateNormal");                 // BtnCertificateDlg (298,286) 35×35
        public static Sprite CertH => An("btnCateHover");
        public static Sprite CertP => An("btnCatePushed");
        public static Sprite HonourN => An("HonourDlgPanelShowNormal");    // BtnHonourShow     (296,318) 38×35
        public static Sprite HonourH => An("HonourDlgPanelShowHover");
        public static Sprite HonourP => An("HonourDlgPanelShowPushed");
        /// <summary>天使(<c>PlayerAngelButton</c> (298,353) 32×34)。🔴 這三個 .an 是**多幀動畫**(官方那顆會閃),
        /// 這裡只取第一幀 —— 沒有動畫驅動,取全幀也只會停在第一張。</summary>
        public static Sprite AngelN => An("PlayerAngelButton");
        public static Sprite AngelH => An("PlayerAngelButton1");
        public static Sprite AngelP => An("PlayerAngelButton2");
        public static Sprite CraftN => An("hecheng1");                     // hechengshu        (298,388) 32×33
        public static Sprite CraftH => An("hecheng2");
        public static Sprite CraftP => An("hecheng3");
        public static Sprite EcN => An("btn_ec0");                         // btn_ec            (298,421) 32×34
        public static Sprite EcH => An("btn_ec1");
        public static Sprite EcP => An("btn_ec2");
        /// <summary>寵物(<c>Showpet</c> (298,455) 32×34)。同格還有 <c>NoShowpet</c>(pet05/06/07_man)是「隱藏寵物」
        /// 的另一半狀態 —— 沒有寵物系統可切,只放這一顆。</summary>
        public static Sprite PetN => An("pet01_man");
        public static Sprite PetH => An("pet02_man");
        public static Sprite PetP => An("pet03_man");

        /// <summary>
        /// 底部三顆開關(<c>OpenBill</c> (351,454) / <c>OpenInvite</c> (460,454) / <c>OpenInfo</c> (570,454))的整列圖,
        /// PlayerInformationDlg32.png 整張 105×21(勾選框 + 中文說明字)。
        ///
        /// 🔴 官方的 <c>bghover</c> 也是這一張,只有 <c>bgpushed</c> 是 Dlg33 —— 而 Dlg33 是
        ///    BaseBoard_man.png (1004,615,15,15) 的**一枚 15×15 勾**,它是要「疊在框上」的,不是整列的替換圖。
        ///    拿它去餵 UGUI 的 SpriteSwap 會被拉伸成 105×21 的糊塊(SpriteSwap 只換 overrideSprite、不會改 rect 大小)。
        ///    所以這裡只給這一張;哪天開關真的有狀態了,勾要另外做一個 15×15 的子物件疊上去。
        /// </summary>
        public static Sprite SwitchBox => An("PlayerInformationDlg32");

        /// <summary>比率長條的填色(232×19 粉紅圓角)。官方 ProgressBar 的 forename,我們拿它做 Filled 填充。</summary>
        public static Sprite RateBar => An("PlayerInformationDlg65");
    }
}
