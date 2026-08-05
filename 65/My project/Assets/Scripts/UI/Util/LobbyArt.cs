using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Sdo.Game;
using Sdo.Settings.Vfs;

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
        private static readonly Dictionary<string, Sprite> _soloCache = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, Sprite[]> _framesCache = new Dictionary<string, Sprite[]>();

        /// <summary>Resolved 大廳 art folder (lazy). Settable for tests (clears the cache).</summary>
        public static string Dir
        {
            get { return _dir ?? (_dir = Path.Combine(SdoExtracted.Root, "UI", FolderName)); }
            set { _dir = value; _cache.Clear(); _soloCache.Clear(); _framesCache.Clear(); }
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
        /// 同 <see cref="An"/>,但那一幀是**複製到自己的貼圖**上再切(<c>LoadAnSolo</c>)。
        ///
        /// <see cref="An"/> 的 <c>bleed</c> 只能救「透明區存 (255,255,255,0)」那種白暈,救不了
        /// **圖集鄰居**:大廳這一包幾乎整包裁自同一張 STAGE.PNG,鈕與鈕在圖裡是**貼著**的,
        /// 雙線性取樣會把隔壁那顆鈕的不透明像素拖進這一顆的邊 —— 畫面上就是每顆鈕鑲一圈白/淺色邊
        /// (創建舞台/快速進入/等待舞台/道具包/郵件/右上角圓鈕全中)。切到自己的貼圖上就沒有鄰居了。
        ///
        /// <c>pad: 0</c> 是刻意的:pad 會在四周加透明邊,而 <c>UIKit.AddSprite</c> 依 sprite 尺寸把左上角錨在 (x,y)
        /// → 每加 N 就把可見圖往右下推 N px(整批位移)。載不到 solo crop 時自動退回共用圖集那條路,安全。
        /// </summary>
        public static Sprite AnSolo(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return null;
            if (_soloCache.TryGetValue(anName, out var s) && s != null) return s;
            s = SdoExtracted.LoadAnSolo(Dir, anName, pad: 0) ?? An(anName);
            _soloCache[anName] = s;
            return s;
        }

        /// <summary>
        /// 下拉選單項目的「滑過態」——**只把 hover 圖真的畫了東西的地方疊到 normal 圖上**。
        ///
        /// 官方 PopMenu 每項給兩張圖(如 <c>FamilyPopMenu1/2</c>),兩張的**底色像素其實完全一樣**,
        /// hover 那張只是把文字/圖示改成金黃、右邊多一個黃三角。但 hover 那張同時把 normal 右緣那條
        /// 半透明暗邊(α≈40~100 的黑)清成全透明 —— 條與條疊起來時那條暗邊就是分隔線,整張換掉的話
        /// 滑過的那一條看起來就「底跟著變了」(使用者回報:底圖不要變色,但三角形和字變黃要留著)。
        ///
        /// 合成規則:兩張差得夠多(<see cref="HoverDiffThreshold"/>)**而且** hover 那邊不透明度 ≥ normal 才採用
        /// hover 的像素;其餘一律保留 normal。→ 黃字/黃圖示/三角(hover 畫上去的東西)進來,
        /// 被 hover 清掉的暗邊與任何底色微差留在 normal 那邊。
        ///
        /// 任何一張載不到、尺寸對不上、或退回共用圖集(sprite 只是大圖的一小塊、像素對不起來)就直接回 normal
        /// —— 最壞情況是滑過沒有黃字,不會畫錯。
        /// </summary>
        public static Sprite AnSoloHover(string normalAn, string hoverAn)
        {
            if (string.IsNullOrEmpty(hoverAn)) return AnSolo(normalAn);
            string key = "hov:" + normalAn + "|" + hoverAn;
            if (_soloCache.TryGetValue(key, out var s) && s != null) return s;
            s = MergeHover(AnSolo(normalAn), AnSolo(hoverAn));
            _soloCache[key] = s;
            return s;
        }

        /// <summary>採用 hover 像素的門檻(R+G+B+A 四通道差的總和)。底色的取樣誤差遠低於它,黃字/三角遠高於它。</summary>
        private const int HoverDiffThreshold = 30;

        private static Sprite MergeHover(Sprite normal, Sprite hover)
        {
            if (normal == null) return hover;
            if (hover == null) return normal;
            var nt = normal.texture; var ht = hover.texture;
            if (nt == null || ht == null || nt.width != ht.width || nt.height != ht.height) return normal;
            // 兩張都必須是「自己一張貼圖」(AnSolo 那條路)。退回 An() 的話 sprite 只是共用圖集的一小塊,
            // 整張 GetPixels32 拿到的是別人的像素 —— 對不起來,直接放棄合成。
            if (normal.rect.width != nt.width || normal.rect.height != nt.height) return normal;
            if (hover.rect.width != ht.width || hover.rect.height != ht.height) return normal;

            Color32[] np, hp;
            try { np = nt.GetPixels32(); hp = ht.GetPixels32(); }
            catch { return normal; }   // 不可讀的貼圖(理論上不會:solo 那條路自己建的貼圖都可讀)

            for (int i = 0; i < np.Length; i++)
            {
                Color32 a = np[i], b = hp[i];
                int d = Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) + Mathf.Abs(a.a - b.a);
                if (d > HoverDiffThreshold && b.a >= a.a) np[i] = b;
            }

            var tex = new Texture2D(nt.width, nt.height, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            tex.SetPixels32(np); tex.Apply(false);
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                                 new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
        }

        /// <summary>同 <see cref="AnSolo"/> 但**超取樣**(<c>LoadAnSoloMip</c>:3× 存、邏輯尺寸顯示)。
        /// 大廳的鈕在預設 800×600 視窗下差不多 1:1,放大/全螢幕時硬邊會鋸齒、模糊濾鏡又糊;
        /// 交給 GPU 面積降取樣才會是乾淨的 ~1px 抗鋸齒邊。載不到就退回 <see cref="AnSolo"/>。</summary>
        public static Sprite AnSoloAA(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return null;
            string key = "aa:" + anName;
            if (_soloCache.TryGetValue(key, out var s) && s != null) return s;
            s = SdoExtracted.LoadAnSoloMip(Dir, anName, pad: 0) ?? AnSolo(anName);
            _soloCache[key] = s;
            return s;
        }

        /// <summary>同 <see cref="AnSoloAA"/> 但給**圓形圖示鈕**用(右上角那排 hall10..28 的 34px 圓盤、
        /// 底下那排表情/喇叭/寵物/幫助)。它們的圓邊是「寬軟 AA 邊」,<see cref="AnSoloAA"/> 的 α&lt;128→0 硬裁
        /// 會把軟邊 binarise 成 1-bit 圓 → 邊緣破碎;<c>LoadAnSoloCircleMip</c> 用 smoothstep 圓邊(順便剪掉外圈光暈)
        /// 再超取樣。載不到就退回 <see cref="AnSoloAA"/>。</summary>
        public static Sprite AnSoloCircleAA(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return null;
            string key = "circ:" + anName;
            if (_soloCache.TryGetValue(key, out var s) && s != null) return s;
            s = SdoExtracted.LoadAnSoloCircleMip(Dir, anName, pad: 0) ?? AnSoloAA(anName);
            _soloCache[key] = s;
            return s;
        }

        /// <summary>
        /// 大底圖專用(視窗底板那種整片的圖)—— 照 .an 的 crop 複製到自己的貼圖。
        ///
        /// 🔴 房間信息的底圖 <c>stageinfoBG.an = stage.png (0,533,341,423)</c> **兩條舊路都不能走**:
        ///    共用圖集會滲鄰居(裁切框右邊隔 1px 就是一片不透明橘色 (255,190,115)、下面貼著一塊紫色),
        ///    solo 又會把大圖邊緣那圈半透明壓成黑邊。理由與 <see cref="PlayerInfoArt.AnRaw"/> 完全相同,
        ///    實作也共用同一份(<see cref="AtlasCropper"/>)。
        /// 讀不到 .an 的裁切資訊時退回共用圖集那條路(至少畫得出來)。
        /// </summary>
        public static Sprite AnRaw(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return null;
            string key = "raw:" + anName;
            if (_cache.TryGetValue(key, out var s) && s != null) return s;
            if (TryReadAnCrop(anName, out string img, out int x, out int y, out int w, out int h))
                s = AtlasCropper.Crop(Dir, img, x, y, w, h);
            if (s == null) s = SdoExtracted.LoadAn1(Dir, anName, bleed: false);
            _cache[key] = s;
            return s;
        }

        /// <summary>
        /// 讀一個 .an 的裁切資訊。這種 .an 是**純文字**,內容就一行:<c>圖檔名 (x, y, w, h)</c>
        /// (數字之間可能有空格,而且同一行可能重複兩次 —— 只取第一組)。讀不到就回 false。
        /// </summary>
        private static bool TryReadAnCrop(string anName, out string image, out int x, out int y, out int w, out int h)
        {
            image = null; x = y = w = h = 0;
            try
            {
                string file = Path.Combine(Dir, anName.EndsWith(".an", System.StringComparison.OrdinalIgnoreCase)
                                                ? anName : anName + ".an");
                if (!VfsFile.Exists(file))
                {
                    file = Path.Combine(Dir, anName.ToUpperInvariant() + ".AN");
                    if (!VfsFile.Exists(file)) return false;
                }
                var m = System.Text.RegularExpressions.Regex.Match(
                    VfsFile.ReadAllText(file), @"([^\s(]+)\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)");
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
