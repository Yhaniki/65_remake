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
        /// <summary>第五格分頁(星座守護)的圖集 —— 與前四格不同一張。</summary>
        public const string ZodiacAtlas = "ZODIAC.PNG";

        private static string _dir;
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, Sprite[]> _framesCache = new Dictionary<string, Sprite[]>();
        private static readonly Dictionary<string, Sprite> _circleCache = new Dictionary<string, Sprite>();

        /// <summary>解析後的 PLAYERINFORMATIONDLG 資料夾(懶解析)。可設定給測試用(會清快取)。</summary>
        public static string Dir
        {
            get { return _dir ?? (_dir = Path.Combine(SdoExtracted.Root, "UI", FolderName)); }
            set { _dir = value; _cache.Clear(); _framesCache.Clear(); _circleCache.Clear(); }
        }

        /// <summary>一個 .an 的第一幀(已快取);找不到回 null。名字可含可不含 ".an"。</summary>
        /// <summary>
        /// 一個 .an 的第一幀(已快取)。
        ///
        /// 🔴 走 <c>LoadAnSolo</c>(**複製到自己的貼圖**)而不是 <c>LoadAn1</c>(共用圖集):
        ///    這一包的按鈕在 BaseBoard_man.png / BaseBoard2_man.png 裡是**彼此貼著**排的
        ///    (例如三態直接上下相鄰),共用圖集取樣時雙線性會把隔壁那顆鈕的不透明像素拖進這一顆的邊緣
        ///    —— 畫面上就是每顆鈕鑲一圈白/淺色邊(使用者回報)。切到自己的貼圖上就沒有鄰居可滲。
        ///    <c>pad: 0</c> 是刻意的:pad 會在四周加透明邊,而 <c>UIKit.AddSprite</c> 依 sprite 尺寸把左上角錨在 (x,y),
        ///    每加 N 就把整張圖往右下推 N px。載不到 solo crop 時退回舊路(至少畫得出來)。
        /// </summary>
        public static Sprite An(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return null;
            if (_cache.TryGetValue(anName, out var s) && s != null) return s;
            s = SdoExtracted.LoadAnSolo(Dir, anName, pad: 0) ?? SdoExtracted.LoadAn1(Dir, anName, bleed: true);
            _cache[anName] = s;
            return s;
        }

        /// <summary>
        /// 同 <see cref="An"/> 但給**圓形鈕**用(這一包只有右上角那顆 X):
        /// <c>LoadAnSoloCircleMip</c> = AlphaFlood + 圓形 smoothstep alpha 遮罩 + 3× 超取樣(mipmap/Trilinear,
        /// 以 ppu = 3 交出去,所以顯示尺寸不變)。
        ///
        /// 🔴 這顆 X(<c>BaseBoard_man.png (972,618,29,29)</c>)的圓盤**外面烤了一圈純白光暈**:
        ///    半徑 15~16 那兩環是 RGB=(255,255,255)、α 43~83 的白texel,而真正的圓邊(帶色 AA)在半徑 14。
        ///    <see cref="An"/> 那條路(<c>LoadAnSolo</c>)動不了它 —— <c>DeMatteWhite</c> 只還原 RGB 不碰 alpha,
        ///    把純白 un-composite 回去還是純白;<c>AlphaBleed</c> 更只管全透明區。於是那圈白暈原封不動畫在
        ///    深色底板上 = 圓外一圈亮邊,再被「800×600 設計拉到 1040×807 視窗」的 **1.3 倍非整數縮放**
        ///    拉開,就成了使用者回報的「邊緣鋸齒狀」(白暈的 α 從 131 陡降到 10,階梯全露出來)。
        ///    CircleMask 把 maskR 之外的 alpha 收成 0 → 白暈整圈消失;超取樣讓 1.3 倍放大時是 GPU 面積降取樣
        ///    而不是 29px 貼圖硬撐 → 邊緣乾淨。載不到就退回 <see cref="An"/>。
        /// </summary>
        public static Sprite AnCircleAA(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return null;
            if (_circleCache.TryGetValue(anName, out var s) && s != null) return s;
            s = SdoExtracted.LoadAnSoloCircleMip(Dir, anName, pad: 0) ?? An(anName);
            _circleCache[anName] = s;
            return s;
        }

        /// <summary>
        /// 給**整張大底圖**用的載入(視窗底板、每一頁的底圖)。走 <see cref="AtlasCrop"/> ——
        /// 也就是「複製到自己的貼圖 + 四鄰擴散」,而**不是** <see cref="An"/> 的 solo 那條路、
        /// 也不是共用圖集。
        ///
        /// 🔴 兩條路都不行,原因不同:
        ///    • solo(<see cref="An"/>)是為「圖集裡彼此貼著的小鈕」設計的,它的 DeMatteWhite + Clamp
        ///      會把大底圖邊緣那圈半透明像素壓成深色 → 視窗外圍多一條黑邊。
        ///    • 共用圖集(LoadAn1)則會在邊緣取樣時把**鄰居**拖進來 —— BaseBoard_man.png 上
        ///      底板是 (0,0,625,502)、基本頁底圖是 (624,0,348,337),兩者**貼著**(甚至重疊 1px),
        ///      所以底板右緣會滲出一條鄰居的深色雜訊(使用者連兩輪回報「框旁邊切錯的黑色雜訊」)。
        ///    複製到自己的貼圖之後兩個問題都不存在:沒有鄰居可滲,也不做任何邊緣壓暗。
        /// </summary>
        /// <param name="trimRight">
        /// 從 .an 給的寬度**再砍掉右邊幾欄**。這是給「.an 的 crop 與右邊鄰居重疊」的圖用的 ——
        /// 見 <see cref="Board"/>,那不是我們算錯,是這份資料包的 .an 自己多寫了一欄。
        /// </param>
        public static Sprite AnRaw(string anName, int trimRight = 0)
        {
            if (string.IsNullOrEmpty(anName)) return null;
            string key = "raw:" + anName + (trimRight > 0 ? ":t" + trimRight : "");
            if (_cache.TryGetValue(key, out var s) && s != null) return s;
            // .an 檔記的是「哪張圖的哪一塊」,直接照它的裁切走 AtlasCrop。
            if (TryReadAnCrop(anName, out string img, out int x, out int y, out int w, out int h))
                s = AtlasCrop(img, x, y, Mathf.Max(1, w - trimRight), h);
            if (s == null) s = SdoExtracted.LoadAn1(Dir, anName, bleed: true);   // 讀不到裁切資訊時的退路
            _cache[key] = s;
            return s;
        }

        /// <summary>
        /// 讀一個 .an 的裁切資訊。這種 .an 是**純文字**,內容就一行:<c>圖檔名 (x, y, w, h)</c>
        /// (數字之間可能有空格,而且同一行可能重複兩次 —— 只取第一組)。
        /// 讀不到就回 false,呼叫端自己退回舊路。
        /// </summary>
        private static bool TryReadAnCrop(string anName, out string image, out int x, out int y, out int w, out int h)
        {
            image = null; x = y = w = h = 0;
            try
            {
                string file = Path.Combine(Dir, anName.EndsWith(".an", System.StringComparison.OrdinalIgnoreCase)
                                                ? anName : anName + ".an");
                if (!File.Exists(file))
                {
                    file = Path.Combine(Dir, anName.ToUpperInvariant() + ".AN");
                    if (!File.Exists(file)) return false;
                }
                string text = File.ReadAllText(file);
                var m = System.Text.RegularExpressions.Regex.Match(
                    text, @"([^\s(]+)\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)");
                if (!m.Success) return false;
                image = m.Groups[1].Value;
                x = int.Parse(m.Groups[2].Value);
                y = int.Parse(m.Groups[3].Value);
                w = int.Parse(m.Groups[4].Value);
                h = int.Parse(m.Groups[5].Value);
                return w > 0 && h > 0;
            }
            catch { return false; }
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

        /// <summary>
        /// 直接以官方 .an 的 top-left 座標裁圖集(y 在這裡是「從上往下」,與 .an 檔一致)。
        ///
        /// 🔴 裁下來的像素會**複製到自己的貼圖**上,不是共用整張圖集。
        ///    共用圖集時 sprite 的邊緣取樣會把 rect **外面**的像素拖進來 —— BaseBoard2_man.png 上
        ///    那幾條分頁彼此上下貼著、周圍還是白的,結果四格分頁全部鑲一圈白底(使用者回報「沒去背」)。
        ///    自己的貼圖沒有鄰居可滲,而且 Clamp 之後邊緣就是自己的顏色。
        ///    順帶把「透明區存 (255,255,255,0)」那種白 matte 的 RGB 換成鄰近不透明色(AlphaBleed 的同一招),
        ///    否則半透明的邊會滲出白線。
        /// </summary>
        public static Sprite AtlasCrop(string imageName, int x, int y, int w, int h)
            => AtlasCropper.Crop(Dir, imageName, x, y, w, h);

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
            // 🔴 第五格「星座守護」不在 BaseBoard2_man.png 裡 —— 它是另一個圖集 Zodiac.png
            //    (ZoSelect_a 未選 101,988,350×37 / ZoSelect_b 已選 101,949,350×39)。
            //    查表那組只涵蓋前四格,第五格要單獨走。
            if (index == 4)
                return selected
                    ? AtlasCrop(ZodiacAtlas, 101, 949, 350, 39)
                    // 🔴 官方 ZoSelect_a 寫的是 (101,988,350,**37**) → 底邊 1025,而 Zodiac.png 只有 1024 高
                    //    —— **官方那份資料自己越界**(女版 Dlg158 也有同一個毛病)。AtlasCrop 對越界回 null,
                    //    所以照抄的話第五格「未選」狀態整個畫不出來(實機:分頁條只剩四格)。夾成 36,少一列像素看不出來。
                    : AtlasCrop(ZodiacAtlas, 101, 988, 350, 36);
            if (index < 0 || index > 3) return null;
            var t = selected ? TabSelectedRect : TabNormalRect;
            int x = t[index, 0], y = t[index, 1], w = t[index, 2], h = t[index, 3];
            int off = x < 0 ? -x : 0;
            dx = off;
            return AtlasCrop(TabAtlas, x + off, y, w - off, h);
        }

        // ---------------------------------------------------------------- 具名版位
        // 名稱照官方 XML 的元件名取,方便回頭對 PLAYERINFORMATIONDLG_MAN.XML 的 <Window name="WinPlayerInfo">。

        /// <summary>
        /// 主框(官方 <c>DailogBg</c>):BaseBoard_man.png (0,0,625,502),擺在 (93,56)。
        ///
        /// 🔴 <c>trimRight: 1</c> 是必要的 —— **這份資料包的 .an 多寫了一欄**:
        ///    crop 寫 625 寬(atlas 欄 0..624),但底板自己的框只畫到 atlas x=619,620..623 是**全透明**,
        ///    而 x=624 又冒出一條 alpha≈82 的深紫 (97,72,168) —— 那是**右邊鄰居**
        ///    <c>PlayerInformationDlg34_man (624,0,348,337)</c> 的左邊緣,兩者重疊了 1 欄。
        ///    照 625 裁,畫面上就會在視窗外框**外面**隔 4px 浮著一條半透明深紫線
        ///    (絕對 x=93+624=717,實機截圖在螢幕 x=925 量到,黑底下特別清楚)——
        ///    使用者連續五輪回報的「框旁邊那條雜訊」最後剩的就是它。
        ///    佐證:同一個 .an 在 Super Dance Online 那份資料包裡寫的是 <c>(0,0,624,502)</c>,官方自己修過。
        /// </summary>
        public static Sprite Board => AnRaw("PlayerInformationDlg0_MAN", trimRight: 1);   // 整張大底板 → 不走 solo(見 AnRaw)

        // 知名度那一排(官方 <c>zhimingdu1..10</c>,10 格 22×22 貼著排)。
        // 這三張是**獨立的 22×22 PNG**,不是圖集裁切(Playerstar.png / Playermoon.png / Playersun.png),
        // 所以沒有鄰居可滲,不必擔心共用圖集那套問題。
        // 🔴 太陽走 <see cref="AnRaw"/> 而不是 <see cref="An"/>:星與月的 alpha bbox 是 (2,2,20,19)/(2,2,19,19),
        //    四周留了透明邊;**太陽是 (0,0,22,22)** —— 外圈光暈一路半透明到邊,而 An 走的 LoadAnSolo
        //    帶 DeMatteWhite,會把那圈半透明邊壓暗成一道黑框(同 AnRaw 註解裡「大底圖不能走 solo」的原因)。
        public static Sprite FameStar => An("PlayerStar");
        public static Sprite FameMoon => An("PlayerMoon");
        public static Sprite FameSun => AnRaw("PlayerSun");


        // 賽事信息頁的三個子分頁(家族徽章 / 星座勛章 / 寵物勛章)在 BaseBoard2_man.png 上的裁切。
        // 三張都是「整條 322-328 寬、只畫自己那一格、其餘透明」,官方把三個 CheckBox 疊在同一點 (350,264)。
        //
        // 🔴 **不要照 XML 的 CheckBox 名字綁圖** —— _man 版的 bgnormal 整組錯開了一格
        //    (xunzhang0 掛的是家族的圖、familly 掛的是寵物的圖)。女版對得上,是男版重繪時把編號對調
        //    卻沒改 XML(用 .msk 的可點區交叉驗過:圖錯、遮罩對)。這裡照**畫面上實際的左中右順序**排。
        // 🔴 寵物那張 xunzhang7/8_man.an 寫 (12,400,325,31),但 row 0 在 x 102..208 有一條不透明線 ——
        //    那是上面「星座勛章」那格的底邊。所以往下切一列 (12,401,325,30),擺放時 y 補 +1。
        private static readonly int[,] SubTabNormal =
        {
            { 0, 337, 328, 31 },   // 家族徽章 xunzhang1_man
            { 6, 369, 322, 31 },   // 星座勛章 xunzhang4_man
            { 12, 401, 325, 30 },  // 寵物勛章 xunzhang7_man ← 上緣裁掉 1 列
        };
        private static readonly int[,] SubTabSelected =
        {
            { 6, 436, 322, 31 },   // xunzhang3_man
            { 6, 473, 322, 31 },   // xunzhang6_man
            { 2, 509, 322, 31 },   // xunzhang9_man
        };

        /// <summary>賽事信息頁第 <paramref name="index"/> 個子分頁(0=家族 1=星座 2=寵物)的圖。</summary>
        public static Sprite SubTab(int index, bool selected)
        {
            if (index < 0 || index > 2) return null;
            var t = selected ? SubTabSelected : SubTabNormal;
            return AtlasCrop(TabAtlas, t[index, 0], t[index, 1], t[index, 2], t[index, 3]);
        }

        /// <summary>
        /// 成就頁空格的問號。
        ///
        /// 🔴 **不走 <c>EffortNone_man.an</c>**:那個 .an 寫 <c>Effort_man.png (322,120,45,45)</c>,
        ///    但「?」字形實際只落在框內 rel (18,1,22,35) —— 偏右偏上。整張 45×45 貼進槽裡,一排問號會整體偏左上。
        ///    往上位移 .an 框也不行(會吃到上面 SkillBtn2_man (324,93,109,28) 的底邊),
        ///    所以這裡直接切**字形本身** (340,121,22,35),由呼叫端置中貼進 45×45 的槽。
        /// </summary>
        public static Sprite EffortNone => AtlasCrop("Effort_man.png", 340, 121, 22, 35);

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

        // 右上角的 X(29×29,close (662,73))。三態都走 AnCircleAA —— 它是這一包唯一的圓形鈕,
        // 而且外圈烤了白光暈,走 An() 會在圓外留一圈鋸齒亮邊(見 AnCircleAA 的註解)。
        // 🔴 三態要一起換:SpriteSwap 是滑過去就換圖,只修 normal 的話白暈會在 hover 的瞬間跳出來又消失。
        //    (pushed 的 .an 指到和 normal 同一塊 crop,所以那兩張本來就該長一樣。)
        public static Sprite CloseN => AnCircleAA("PlayerInformationDlg14");
        public static Sprite CloseH => AnCircleAA("PlayerInformationDlg15");
        public static Sprite CloseP => AnCircleAA("PlayerInformationDlg16");

        /// <summary>「天使认证」鈕的**灰色**版(官方那顆 Angel 鈕的 <c>bggray="AngelAttGray.an"</c>)。
        /// 這個重製版沒有天使系統 → 基本信息頁一律畫這張,和官方「還沒認證」的畫面一樣。</summary>
        public static Sprite AngelCertGray => An("AngelAttGray");

        /// <summary>
        /// 「见习天使」那四個字 —— **官方的美術字素材**,不是用字型排出來的。
        /// <c>ANGELNAME_MAN.AN</c> 的第一幀 = <c>BaseBoard2_man.png (439,419,89,20)</c>,**灰色**版:
        /// 官方在「還沒認證」的狀態就是顯示這張(第二幀起是各等級的紫色名稱,那是認證之後才換的)。
        /// 尺寸 89×20 正好對上 XML 的 <c>LBAngelName w=90 h=20</c>。
        /// 🔴 用圖不用文字:那四個字是烤好的美術字,系統字型排出來的筆畫、字距與灰階都對不上官方。
        /// </summary>
        public static Sprite AngelNameTrainee => An("AngelName_man");

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

        /// <summary>
        /// 經驗值那條進度條的填色(**黃**,232×19)。官方 <c>pro_exp</c> 的 forename 寫的是
        /// <c>PlayerInformationDlgYellow65.an</c>,但**那個 .an 的圖檔名寫錯了**:它寫
        /// <c>BaseBoard_man.PNG (700,991,232,19)</c> —— 座標是對的,圖不對。
        ///   • 女版 <c>BaseBoard.PNG</c> 的 (700,991,232,19) 是一條**填滿整格**的黃條(alpha bbox 0..231 × 0..18),
        ///     正上方 (700,972,232,19) 就是 TP 值那條粉紅(<c>PlayerInformationDlg65.an</c>)——
        ///     同一張圖、上下相鄰、同尺寸,兩條本來就是一套。
        ///   • 男版 <c>BaseBoard_man.PNG</c> 的同一塊,條體只有 **228×15**(右邊 7 欄、下面 4 列全透明),
        ///     那張圖上的進度條是另一種排版。照 .an 走的話,經驗條會比隔壁的 TP 條**細一圈又偏上**
        ///     —— 使用者回報「經驗值那個黃條不是官方的素材」指的就是這個。
        /// 所以這裡**照官方座標裁女版圖**。這是本類「素材一律走男版」的唯一例外,原因是官方自己把圖檔名寫錯,
        /// 別「順手」改回 _man —— 那正是壞掉的那條路。(這份資料包寫錯 .an 的前科見 <see cref="Board"/>、
        /// <see cref="TabStrip"/> 的第五格。)
        ///
        /// 🔴 走 <see cref="AtlasCrop"/> 而不是 <see cref="An"/>:一來 .an 指的圖是錯的,二來這一塊的上下鄰居
        ///    (粉紅條在 y=990 結束、黃條 y=991 開始)是**貼著**的,共用圖集會把粉紅滲進黃條的上緣。
        /// </summary>
        public static Sprite ExpBar => AtlasCrop("BaseBoard.PNG", 700, 991, 232, 19);
    }
}
