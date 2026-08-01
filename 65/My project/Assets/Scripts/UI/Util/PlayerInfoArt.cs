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

        /// <summary>解析後的 PLAYERINFORMATIONDLG 資料夾(懶解析)。可設定給測試用(會清快取)。</summary>
        public static string Dir
        {
            get { return _dir ?? (_dir = Path.Combine(SdoExtracted.Root, "UI", FolderName)); }
            set { _dir = value; _cache.Clear(); _framesCache.Clear(); }
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
        public static Sprite AnRaw(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return null;
            string key = "raw:" + anName;
            if (_cache.TryGetValue(key, out var s) && s != null) return s;
            // .an 檔記的是「哪張圖的哪一塊」,直接照它的裁切走 AtlasCrop。
            if (TryReadAnCrop(anName, out string img, out int x, out int y, out int w, out int h))
                s = AtlasCrop(img, x, y, w, h);
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
        {
            if (string.IsNullOrEmpty(imageName) || w <= 0 || h <= 0) return null;
            string key = "atlas:" + imageName + ":" + x + "," + y + "," + w + "," + h;
            if (_cache.TryGetValue(key, out var s) && s != null) return s;

            var tex = SdoExtracted.LoadTextureRaw(Dir, imageName)
                      ?? SdoExtracted.LoadTextureRaw(Dir, imageName.ToUpperInvariant());
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

        /// <summary>主框(官方 <c>DailogBg</c>):BaseBoard_man.png (0,0,625,502),擺在 (93,56)。</summary>
        public static Sprite Board => AnRaw("PlayerInformationDlg0_MAN");   // 整張大底板 → 不走 solo(見 AnRaw)

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
