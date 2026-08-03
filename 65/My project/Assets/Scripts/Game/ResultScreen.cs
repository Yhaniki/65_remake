using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 結算畫面 (result screen) — port of the ONLINE Dance!Online panel
    /// (Extracted/UI/STATIS/ITEMSTATISTIC/DDRITEMSTATISTIC.XML, 800×600 design space). Drawn with
    /// SpriteRenderers on the HUD ortho camera, ON TOP of the live stage backdrop. Layout:
    ///   • background = StatisItem0..7 tiles (4×2 grid at design y=115 / y=371),
    ///   • (the song name + level are NOT drawn here — the gameplay HUD's bottom song-info row, 歌曲名 + LV with the
    ///     time field dropped, is kept visible below the panel instead; see ScreenGameplay.ShowResultSongInfo),
    ///   • a YouWin / YouLose banner cropped from BALANCE.png (Statis28 = win, Statis30 = lose) that scales 3→1,
    ///   • up to 6 rank rows that slide in from the right (x 800→0, 1s, staggered), each showing the rank badge,
    ///     nick, combo / perfect / cool / bad / miss, hit-rate (or the 100 all-combo marker) and score,
    ///   • a bottom reward block: 經驗 EXP (current / +earned / total) and G幣 coins, plus the OK / save buttons.
    /// Number columns use the original digit strips (Num8 / Num3 / score_num / score_numS).
    /// Owned and driven by <see cref="ScreenGameplay"/>: <see cref="Build"/> once, <see cref="Show"/> at the settle
    /// beat, <see cref="Tick"/> every result frame. <see cref="OnConfirm"/> fires when OK is pressed.
    /// </summary>
    public sealed class ResultScreen
    {
        public struct Row
        {
            public int Rank;            // 1-based place — **嚴格**順序(照 (分數, 座位) 排,同分也一定分先後)
            /// <summary>畫在名次牌上的名次 —— **同分並列**(競賽排名 1,1,3;使用者指定)。0 = 用 <see cref="Rank"/>。
            ///
            /// 🔴 與 <see cref="Rank"/> 分開是刻意的:輸贏定格與 WIN/LOSE 旗只能有一個第一名(那要嚴格順序,
            /// 見 <c>ScreenGameplay.TickFinishPoseDecision</c>),但**寫在畫面上的名次**同分要一樣。</summary>
            public int DisplayRank;
            public int UserId;
            public string Name;
            public int Perfect, Cool, Bad, Miss, MaxCombo;
            public double Accuracy;     // 0..100
            public long Score;
            public string Grade;        // "S" / "A+" / ... (kept for callers; not drawn in the online layout)
            public bool IsLocal;
            public Texture Head;
            public bool FullCombo;      // 100% — shows the AllCombo marker instead of the hit-rate digits
        }

        // sorting orders (above the HUD; the HUD/board is hidden at result anyway)
        private const int OrderBg = 120, OrderRow = 130, OrderRowText = 134, OrderBanner = 138, OrderBtn = 140, OrderText = 144;

        public System.Action OnConfirm;
        /// <summary>沒人按「確定」就自動確定的秒數,從面板出現算起(≤0 = 不自動,一直等玩家按)。
        /// 線上由 <see cref="ScreenGameplay.resultAutoConfirmSec"/> 設 —— 一個人把結算畫面放著不管,
        /// 整間房就都開不了下一局。走的是跟按確定完全同一條路(<see cref="OnConfirm"/>)。</summary>
        public float autoConfirmSec = 0f;
        public bool Visible { get; private set; }

        private Camera _cam;
        private GameObject _root;
        private GameObject _bannerWin, _bannerLose;
        private readonly List<GameObject> _rowRoots = new List<GameObject>();
        private SpriteRenderer _okBtn, _saveBtn;
        private float _showStart = -1f;
        private System.Action<string> _playSe;     // SE hook (ScreenGameplay.PlaySe)
        private bool[] _rowSnd;
        // result sequence flags/timers: rows (SE_0020, 500ms apart) → EXP/G roll (SE_0021) → win/lose banner zoom (SE_0022)
        private bool _expSnd, _bannerShown, _bannerLocalWon, _gameOver;
        private bool _confirmed;           // 確定已送出(按的或逾時自動的)— 一局只送一次
        private bool _showBanner = true;   // 出 YOU WIN/LOSE 旗? 自由模式=false (仍播 SE_0022);GAME OVER 也不出旗
        private bool _showRank = true;     // 畫每列最左的名次數字? 自由模式=false (沒有排名);GAME OVER 圖不受影響照畫
        private float _bannerStart;
        // GAME OVER (RANK/7.png) sprite — drawn IN the failed (local) player's rank column as a normal row child, so it
        // slides in with that row (no separate banner / animation) and replaces their rank number.
        private Sprite _overSprite;

        // digit strips + badges (loaded once)
        private Sprite[] _num8, _num3, _scoreNum, _scoreNumS;
        private Sprite _percent, _dot, _allCombo;
        private readonly Dictionary<int, Sprite> _rankBadge = new Dictionary<int, Sprite>();
        private readonly Dictionary<string, Sprite> _gradeSprites = new Dictionary<string, Sprite>();   // 成績字 (02/): S→A++ / A+ / A / B / C / D

        // bottom reward totals — count up with the shared score-style roll+pop (RollingDigits)
        private RollingDigits _expTotal, _gTotal;
        private long _expTarget, _coinsTarget;
        private GameObject _rewardRoot;
        private bool _rewardArmed;            // totals start rolling once the rank rows have slid in

        // per-row avatar head: local player = live RenderTexture (set by ScreenGameplay), others = tinted placeholder box
        private Texture _localHead;
        private Sprite _placeholderHead;
        // ---- F4-tunable layout (live; see ScreenGameplay "Result" tab) ----
        public float nickX = 109f, nickYOff = 10f, nickSize = 22f;          // nickname: column x / vertical offset from RowY / font px (tuned)
        public float headBoxX = 30f, headBoxYOff = 12f, headBoxSize = 48f;  // head portrait FRAME slot: left x / top offset from RowY / SQUARE size (tuned)
        // Official AvatarShow draws the full 3D head with NO frame scissor (FUN_0043e2f0 culls only against the SCREEN,
        // not the slot), so hair / hats / ears spill ABOVE the frame line. We mirror that WITHOUT distortion: the local
        // head quad keeps the slot WIDTH but is TALLER — extended UPWARD by headOverflowTop px, bottom-anchored to the slot
        // bottom. The head cam frames the FACE into the slot region and the HAIR into this overflow strip (the RT keeps a
        // transparent margin above the hair, so it's NEVER cut). The RT aspect (ScreenGameplay) matches w/(slot+overflow).
        // (Opponents' placeholder stays clamped to the slot.)
        public float headOverflowTop = 6f;
        private readonly List<(Label3D lbl, float rowY)> _nicks = new List<(Label3D, float)>();
        private struct HeadObj { public GameObject go; public SpriteRenderer sr; public Material mat; public float rowY; public bool placeholder; }
        private readonly List<HeadObj> _headObjs = new List<HeadObj>();

        // row target Y per rank (online DDRITEMSTATISTIC Rank1..6 windows) and slide tuning
        private static readonly float[] RowY = { 162f, 215f, 268f, 321f, 374f, 424f };   // STATISTIC Rank1..6 targety (step 53)
        private const float RowSlideSec = 0.45f, RowStaggerSec = 0.35f, RowStartX = 800f;  // players slide in 350ms apart (SE_0020)
        private const float ExpHoldSec = 1.2f;       // after the rows: hold while EXP/G count up (SE_0021)
        // YOU WIN/LOSE banner — only the ANIMATION START centre + time are tunable (F4 "Banner" tab); the END position
        // and size are FIXED at the official spot. The banner slides start→end while scaling screen-width→1.
        public float bannerStartX = 440f, bannerStartY = 95f, bannerStartScale = 2.89f, bannerAnimSec = 0.3f;   // tuned
        private const float BannerEndX = 643f, BannerEndY = 71f;   // fixed final centre (official); final scale = 1
        private bool _bannerStatic;   // preview hold (no anim) at _bannerStaticT
        private float _bannerStaticT; // 0 = animation START (start pos + screen-width), 1 = END (final pos/size)

        /// <summary>Load art + build the static panel (bg + buttons + banners), hidden. Call once.</summary>
        public void Build(Camera hudCam)
        {
            _cam = hudCam;
            _root = new GameObject("ResultScreen");
            string dir = SdoExtracted.ResultStatisDir;

            // background: Statis0..3 at design y=115, Statis4..7 at y=371 (each native-size, top-left placed).
            for (int i = 0; i < 8; i++)
            {
                var s = SdoExtracted.LoadAn1(dir, "Statis" + i + ".an");
                if (s != null) Place(NewSR("Bg" + i, s, OrderBg), (i % 4) * 256, (i < 4) ? 115 : 371);
            }

            // Every glyph/badge/button below goes through the matte-cleaning premultiplied path (see LoadPanelSprite):
            // the art carries a low-alpha whitish rim that reads fine at the official 1:1 but blooms into a white haze
            // once the 800×600 design is magnified to the window.
            _num8 = LoadPanelSprites(dir, "Num8.an");
            _num3 = LoadPanelSprites(dir, "Num3.an");
            _scoreNum = LoadPanelSprites(dir, "score_num.an");
            _scoreNumS = LoadPanelSprites(dir, "score_numS.an");
            _percent = LoadPanelSprite(dir, "percent.an");
            _dot = LoadPanelSprite(dir, "dot.an");
            _allCombo = LoadPanelSprite(dir, "100.an");
            // 成績 letters (02/): map our grade band → the official sprite (A0=A++, A1=A+, A2=A, …).
            _gradeSprites["S"]  = LoadPanelImage(dir, "02/A0.PNG");
            _gradeSprites["A+"] = LoadPanelImage(dir, "02/A1.PNG");
            _gradeSprites["A"]  = LoadPanelImage(dir, "02/A2.PNG");
            _gradeSprites["B"]  = LoadPanelImage(dir, "02/B2.PNG");
            _gradeSprites["C"]  = LoadPanelImage(dir, "02/C2.PNG");
            _gradeSprites["D"]  = LoadPanelImage(dir, "02/D2.PNG");
            _gradeSprites["F"]  = LoadPanelImage(dir, "02/F0.PNG");   // HP-out fail grade

            // win/lose banners — single sprites cropped from BALANCE.png (Statis28 = win @ design (487,38), Statis30 = lose @ (488,38)).
            _bannerWin = BuildBanner("BannerWin", dir, "Statis28.an", 487, 38);
            _bannerLose = BuildBanner("BannerLose", dir, "Statis30.an", 488, 38);
            // GAME OVER (RANK/7.png) — drawn in the failed player's rank column (see BuildRow), not a separate banner.
            _overSprite = LoadPanelImage(dir, "RANK/7.PNG");

            // buttons (OK = Statis25, save-record = Statis22), bottom-right — same premultiplied path as the banner.
            _okBtn = BuildButton("OkBtn", dir, "Statis25.an", 694, 493);
            _saveBtn = BuildButton("SaveBtn", dir, "Statis22.an", 595, 493);

            // 1×1 white sprite used (tinted) as the placeholder head box for rows without a live portrait.
            var pht = new Texture2D(1, 1); pht.SetPixel(0, 0, Color.white); pht.Apply();
            _placeholderHead = Sprite.Create(pht, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);

            _root.SetActive(false);
        }

        /// <summary>Load one 結算 graphic with its baked whitish rim removed and its RGB premultiplied.
        /// 使用者回報「按鈕角落有白邊、沒去背」。兩種病因在這裡一起治:
        ///  (1) 真 matte —— 確定/保存錄像 兩顆鈕在圓角外有一圈孤立的「低透明度純白」(a=1..21,離鈕身 2px 以上還有,四角最厚)。
        ///      AlphaBleed 只改 a≤8 的 RGB、alpha 一律留著,所以那圈白照樣畫;straight-alpha 下 bilinear 把 800×600 放大到
        ///      視窗時「顏色與覆蓋率分開內插」,白就被拉進邊緣 → 圓角外顯出方形白邊。兩顆鈕還多一層:它們是共用 BALANCE.png
        ///      上下緊鄰的 crop(Statis25 y=1、Statis22 y=55,右邊還接著 hover/pushed 態),邊界取樣會吃到隔壁。
        ///  (2) 烘進圖裡的柔白邊 —— %、100、數字、成績字、名次牌沒有孤立 matte,但字身外那一圈 a&lt;48 的白是美術畫上去的。
        ///      官方 1:1 看起來正常,放大後整圈糊成白霧。量測顯示光靠 premult 只降 12%(它本來就是「正確」的像素),把那層清掉
        ///      才降 65~80%,而字身(a≥48)一個像素都不動。
        /// 修法與 WIN/LOSE 旗一致:裁到自己的 texture(不再與鄰居相鄰)、cleanMatte 清掉純白低-alpha、RGB 預乘 alpha(透明處變
        /// (0,0,0,0))。<see cref="NewSR"/> 會自動把這種 texture 配上 premult 材質。pad = 0:版面依 sprite 尺寸對齊,加 pad 會
        /// 讓圖位移 1px;crop 最外圈本來就接近全透明,不需要留邊。shader 被 build 剝掉時退回原本的 straight-alpha 路徑
        /// (見 BuildScript.RequiredShaders)。</summary>
        private static Sprite LoadPanelSprite(string dir, string an)
        {
            if (SdoExtracted.PremultUiMaterial != null)
            {
                var s = SdoExtracted.LoadAnSoloPremultiplied(dir, an, pad: 0, cleanMatte: true);
                if (s != null) return s;
            }
            return SdoExtracted.LoadAn1(dir, an, bleed: true);
        }

        /// <summary>Every frame of a digit strip through <see cref="LoadPanelSprite"/>'s treatment.</summary>
        private static Sprite[] LoadPanelSprites(string dir, string an)
        {
            if (SdoExtracted.PremultUiMaterial != null)
            {
                var s = SdoExtracted.LoadAnPremultiplied(dir, an, pad: 0, cleanMatte: true);
                if (s.Length > 0) return s;
            }
            return SdoExtracted.LoadAn(dir, an, bleed: true);
        }

        /// <summary>A bare PNG (成績字 02\, 名次牌 RANK\) through <see cref="LoadPanelSprite"/>'s treatment.</summary>
        private static Sprite LoadPanelImage(string dir, string image)
        {
            if (SdoExtracted.PremultUiMaterial != null)
            {
                var s = SdoExtracted.LoadImagePremultiplied(dir, image, pad: 0, cleanMatte: true);
                if (s != null) return s;
            }
            return SdoExtracted.LoadImage(dir, image, bleed: true);
        }

        /// <summary>確定 / 保存錄像 button at design (x,y), top-left placed (see <see cref="LoadPanelSprite"/>).</summary>
        private SpriteRenderer BuildButton(string name, string dir, string an, float x, float y)
        {
            var sr = NewSR(name, LoadPanelSprite(dir, an), OrderBtn);
            Place(sr, x, y);
            return sr;
        }

        // A banner placed at design (x,y) under its own root whose origin is the banner centre, so localScale
        // pivots there for the 3→1 scale-in.
        private GameObject BuildBanner(string name, string dir, string an, float x, float y)
        {
            var go = new GameObject(name); go.transform.SetParent(_root.transform, false);
            // The banner is magnified harder than anything else on the panel (zooms screen-width→1, on top of the
            // 800×600→window stretch), so it was the first thing to get the premultiplied treatment — with the default
            // STRAIGHT-alpha material bilinear interpolates colour and coverage separately across each glyph's
            // opaque→transparent edge, smearing the bright candy bevel outward as a pale 「白邊」 halo (worst on the U's
            // flat top). It now shares the whole panel's loader, which ALSO scrubs the baked whitish rim the art carries
            // outside the letters (363 / 281 texels sit more than 2px clear of the glyphs — a real matte, not AA).
            var sr = NewSR(name + "Img", LoadPanelSprite(dir, an), OrderBanner);
            Place(sr, x, y);
            go.transform.position = sr.transform.position;        // root at the banner centre
            sr.transform.SetParent(go.transform, true);
            go.SetActive(false);
            return go;
        }

        /// <summary>Populate the panel with this round's ranked rows + song info + the local reward, then start the
        /// banner scale-in and row slide-in. <paramref name="localWon"/> picks the YouWin / YouLose banner.</summary>
        public void Show(string songTitle, string difficulty, Row[] rows, bool localWon,
                         int expGained, int coinsGained, Texture localHead = null, bool gameOver = false,
                         System.Action<string> playSe = null, bool showBanner = true, bool showRank = true)
        {
            ClearRows();
            _playSe = playSe; _rowSnd = new bool[rows != null ? rows.Length : 0];
            _expSnd = false; _bannerShown = false; _bannerStatic = false; _rewardArmed = false; _localHead = localHead; _gameOver = gameOver;
            _confirmed = false;
            _showBanner = showBanner; _showRank = showRank;
            string dir = SdoExtracted.ResultStatisDir;

            // Song name + level are no longer drawn at the top of the panel — the gameplay HUD's bottom song-info row
            // (歌曲名 + LV, time field dropped) is kept visible below the panel by ScreenGameplay.ShowResultSongInfo.
            // (songTitle / difficulty params are retained for the API; ScreenGameplay supplies the bottom row.)

            for (int i = 0; i < rows.Length && i < RowY.Length; i++)
                BuildRow(dir, rows[i], RowY[i]);

            // STATIC avatar heads in the baked frames (don't slide with the rows): each online row carries its own live 3D portrait.
            for (int i = 0; i < rows.Length && i < RowY.Length; i++)
                BuildHeadBox(rows[i], RowY[i]);

            // bottom reward block (local player): 經驗 EXP and G幣 coins.
            BuildRewardBlock(dir, expGained, coinsGained);

            // win/lose banner is the LAST beat (after rows + EXP) — hidden now; Tick reveals it. GAME OVER has no banner:
            // it rides along inside the failed player's row (drawn in BuildRow), so there's nothing to reveal here.
            _bannerLocalWon = localWon;
            if (_bannerWin) _bannerWin.SetActive(false);
            if (_bannerLose) _bannerLose.SetActive(false);

            _root.SetActive(true);
            Visible = true;
            _showStart = Time.time;
        }

        /// <summary>
        /// Replace provisional rows when the authoritative online result arrives after this panel opened.
        /// Keeps the current reveal timeline instead of restarting the result sequence.
        /// </summary>
        public void ReplaceRows(Row[] rows, bool localWon, int expGained, int coinsGained, Texture localHead = null)
        {
            if (!Visible || rows == null) return;

            float elapsed = Mathf.Max(0f, Time.time - _showStart);
            ClearRows();
            _localHead = localHead;
            _rowSnd = new bool[rows.Length];
            string dir = SdoExtracted.ResultStatisDir;
            int count = Mathf.Min(rows.Length, RowY.Length);
            for (int i = 0; i < count; i++)
            {
                BuildRow(dir, rows[i], RowY[i]);
                _rowSnd[i] = elapsed >= i * RowStaggerSec;
            }
            for (int i = 0; i < count; i++) BuildHeadBox(rows[i], RowY[i]);

            BuildRewardBlock(dir, expGained, coinsGained);
            _rewardArmed = false;
            _bannerLocalWon = localWon;
            bool bannerVisible = _bannerShown && _showBanner && !_gameOver;
            if (_bannerWin) _bannerWin.SetActive(bannerVisible && localWon);
            if (_bannerLose) _bannerLose.SetActive(bannerVisible && !localWon);
        }

        // One ranked row at its design RowY, under its own root so the whole row slides in from the right.
        private void BuildRow(string dir, Row r, float y)
        {
            // rowRoot stays at the ORIGIN while children are parented (worldPositionStays=true → child local =
            // its design world position). The off-screen start offset (+RowStartX) is applied AFTER all children
            // are attached, so the whole row shifts as one unit; Tick then slides it back to 0.
            var rowRoot = new GameObject("Row" + r.Rank); rowRoot.transform.SetParent(_root.transform, false);
            _rowRoots.Add(rowRoot);

            // rank badge (rank/<n>.png) at (0, y-8) — STATISTIC rank NumLabel y=-8. The failed (local) player shows the
            // GAME OVER graphic in this slot instead of their rank number; either way it's a row child → slides in with the row.
            // 自由模式 (_showRank=false) 沒有名次可言 → 這格留空,但 GAME OVER 圖照畫(它交代的是死亡,不是名次)。
            if (r.IsLocal && _gameOver)
            {
                if (_overSprite) Child(rowRoot, NewSR("GameOver", _overSprite, OrderRow), 0, y - 8);
            }
            else if (_showRank)
            {
                int shown = r.DisplayRank > 0 ? r.DisplayRank : r.Rank;   // 同分並列(1,1,3);沒填就用嚴格名次
                if (_rankBadge.TryGetValue(shown, out var badge) == false)
                { badge = LoadPanelImage(dir, "rank/" + Mathf.Clamp(shown, 1, 8) + ".PNG"); _rankBadge[shown] = badge; }
                if (badge) Child(rowRoot, NewSR("Rank", badge, OrderRow), 0, y - 8);
            }

            // nick — BOLD PURE WHITE, NO shadow/outline, vertically CENTRED on the stat numbers (官方). F4-tunable.
            var nick = TextStyles.NewLabel("Nick", TextStyles.Style.HeadName, OrderRowText, nickSize, TextAnchor.MiddleLeft);
            nick.SetColors(Color.white, new Color(0f, 0f, 0f, 0f));
            nick.Text = r.Name ?? "";
            nick.Position = SdoLayout.ToWorld(nickX, y + nickYOff, -3f);
            nick.root.transform.SetParent(rowRoot.transform, true);
            _nicks.Add((nick, y));

            // combo + perfect (Num8, medium), then cool / bad / miss (Num3, small)
            DrawNum(rowRoot, _num8, r.MaxCombo, 256, y + 3, true);
            DrawNum(rowRoot, _num8, r.Perfect, 345, y + 3, true);
            DrawNum(rowRoot, _num3, r.Cool, 412, y + 6, true);
            DrawNum(rowRoot, _num3, r.Bad, 467, y + 6, true);
            DrawNum(rowRoot, _num3, r.Miss, 530, y + 6, true);

            // hit rate — or the "100" all-combo marker when it's a full combo
            if (r.FullCombo && _allCombo) Child(rowRoot, NewSR("AllCombo", _allCombo, OrderRow), 591, y + 6);
            else
            {
                int acc100 = Mathf.Clamp(Mathf.RoundToInt((float)(r.Accuracy * 100.0)), 0, 10000);  // 99.90 -> 9990
                DrawNum(rowRoot, _num3, acc100 / 100, 584, y + 6, true);
                if (_dot) Child(rowRoot, NewSR("Dot", _dot, OrderRow), 598, y + 8);
                DrawNumFixed(rowRoot, _num3, acc100 % 100, 2, 605, y + 6);
                if (_percent) Child(rowRoot, NewSR("Pct", _percent, OrderRow), 624, y + 6);
            }

            // TOTAL SCORE — faithful NumLabel: 6 cells from x=664 (Num3), hidezero → reads right-aligned.
            DrawNumLabel(rowRoot, _num3, r.Score, 664, 6, y + 6);
            // 成績 (RESULT) — the grade letter from 02/ (A++ / A+ / A / B / C / D), at the level column.
            if (r.Grade != null && _gradeSprites.TryGetValue(r.Grade, out var gradeSpr) && gradeSpr)
                Child(rowRoot, NewSR("Grade", gradeSpr, OrderRow), 740, y - 6);

            rowRoot.transform.localPosition = new Vector3(RowStartX, 0f, 0f);   // push the assembled row off-screen right
        }

        // 經驗 / G幣 block at the bottom-left of the panel. The "G" / "G+" / "经验值" / "总计" captions are baked into
        // the StatisItem background art, so the numbers are positioned (and right-aligned) to hug those glyphs.
        // Layout follows the official screen — base value IN FRONT, item bonus BEHIND, animated total below:
        //   G (coins):  [base]G+  [bonus]G        总计 [TOTAL]G
        //   经验值:     [base] + [bonus]          总计： [TOTAL]
        // The remake has no item bonuses → bonus = 0; the TOTAL counts up (RollingDigits, score-style roll+pop).
        // (The three small EXP/榮譽/徽章 tab icons from the original are intentionally omitted.)
        private const float SmallPitch = 10f, BigPitch = 20f;   // score_numS / score_num digit advance (px)
        private void BuildRewardBlock(string dir, int expGained, int coinsGained)
        {
            if (_rewardRoot) { _rewardRoot.SetActive(false); DestroyOwned(_rewardRoot); }
            _rewardRoot = new GameObject("Reward"); _rewardRoot.transform.SetParent(_root.transform, false);
            _expTarget = expGained; _coinsTarget = coinsGained;

            // Faithful NumLabel layout (engine: labelnum cells from XML x, hidezero → right-aligned). XML fields:
            //   G1 x77 n4 / G2 x157 n3 (score_numS) ; exp x328 n5 / expadd x408 n5 (score_numS)
            DrawNumLabel(_rewardRoot, _scoreNumS, coinsGained, 77, 4, 495);   // G base (before "G+")
            DrawNumLabel(_rewardRoot, _scoreNumS, 0, 157, 3, 495);           // G bonus (before top "G")
            DrawNumLabel(_rewardRoot, _scoreNumS, expGained, 328, 5, 495);    // 經驗 base (default exp, in front)
            DrawNumLabel(_rewardRoot, _scoreNumS, 0, 408, 5, 495);           // 經驗 bonus (item加乘, none → 0)

            // animated TOTALs (count up like the in-game score). Right edge = XML x + labelnum × digit-width:
            //   G x89 n5 → 89+5×20=189 ; expall x350 n5 → 350+5×20=450. Both right-aligned (hidezero).
            _gTotal = new RollingDigits(_rewardRoot.transform, _scoreNum, 6, OrderRow, rightX: 89f + 5f * BigPitch, y: 526f, pitch: BigPitch, rightAlign: true);
            _expTotal = new RollingDigits(_rewardRoot.transform, _scoreNum, 6, OrderRow, rightX: 350f + 5f * BigPitch, y: 526f, pitch: BigPitch, rightAlign: true);
        }

        // STATIC head box inside the baked frame for the row at design RowY. Local player → a quad textured with the
        // live head-portrait RenderTexture (ScreenGameplay renders every participant as a close-up at a 45° angle);
        // rows without avatar data fall back to the original tinted placeholder box.
        private void BuildHeadBox(Row row, float rowY)
        {
            float topY = rowY - headBoxYOff;
            Texture head = row.Head != null ? row.Head : (row.IsLocal ? _localHead : null);
            if (head != null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                DestroyOwned(go.GetComponent<Collider>());
                var mr = go.GetComponent<MeshRenderer>();
                // Unlit/Transparent so the RT's transparent (alpha-0) background shows the panel/stage through — no black box.
                var mat = new Material(Shader.Find("Unlit/Transparent")) { mainTexture = head };
                mr.sharedMaterial = mat; mr.sortingOrder = OrderRow;
                go.transform.SetParent(_root.transform, true);    // static (not in the sliding rows)
                PlaceHeadQuad(go.transform, rowY);                // larger-than-slot, bottom-anchored → hair spills out the top
                _headObjs.Add(new HeadObj { go = go, mat = mat, rowY = rowY, placeholder = false });
            }
            else
            {
                var sr = NewSR("HeadBox", _placeholderHead, OrderRow);   // NewSR already parents to _root
                sr.color = new Color(0.42f, 0.52f, 0.48f, 0.85f);        // neutral grey-green placeholder
                SdoLayout.PlaceBox(sr, headBoxX, topY, headBoxSize, headBoxSize, -2f);
                _headObjs.Add(new HeadObj { sr = sr, rowY = rowY, placeholder = true });
            }
        }

        // Position the live-head quad: slot WIDTH × (slot + headOverflowTop) tall, anchored so its BOTTOM edge stays on the
        // frame-slot bottom and its centre-x on the slot centre. The extra height grows UPWARD past the slot top, so the
        // face sits in the slot and hair/hats spill into the strip above — no width distortion (only the height extends),
        // matching the official un-clipped AvatarShow. The RT is framed (ScreenGameplay) so the hair lands in this strip with a
        // transparent margin on top → never cut. headOverflowTop=0 = exactly the slot (clamped).
        private void PlaceHeadQuad(Transform t, float rowY)
        {
            float topY = rowY - headBoxYOff;
            float w = headBoxSize;
            float h = headBoxSize + Mathf.Max(0f, headOverflowTop);
            float cx = SdoLayout.WorldX(headBoxX) + w / 2f;                 // slot centre-x
            float cy = SdoLayout.WorldY(topY) - headBoxSize + h / 2f;       // bottom pinned to slot bottom; grows up by overflow
            t.position = new Vector3(cx, cy, -2f);
            t.localScale = new Vector3(w, h, 1f);
        }

        // Live-apply the F4 layout sliders (nick position/size + head-box position/size) to the existing elements.
        private void ApplyTuning()
        {
            foreach (var (lbl, rowY) in _nicks)
                if (lbl != null)
                {
                    // nick is a child of its sliding row-root (home at origin), so set LOCAL pos = design coord → slides + retunes.
                    lbl.root.transform.localPosition = SdoLayout.ToWorld(nickX, rowY + nickYOff, -3f);
                    lbl.PxSize = nickSize;
                }
            foreach (var hb in _headObjs)
            {
                float topY = hb.rowY - headBoxYOff;
                if (hb.placeholder) { if (hb.sr) SdoLayout.PlaceBox(hb.sr, headBoxX, topY, headBoxSize, headBoxSize, -2f); }
                else if (hb.go) PlaceHeadQuad(hb.go.transform, hb.rowY);
            }
        }

        /// <summary>Animate the banner scale-in and row slide-in; hit-test the OK / save buttons.</summary>
        public void Tick()
        {
            if (!Visible) return;
            ApplyTuning();   // live F4 nick / head-box layout sliders
            float el = Time.time - _showStart;

            // (1) rows slide in from +RowStartX to 0, ONE BY ONE 500ms apart — each fires SE_0020 as it starts.
            for (int i = 0; i < _rowRoots.Count; i++)
            {
                float start = i * RowStaggerSec;
                if (el >= start && _rowSnd != null && i < _rowSnd.Length && !_rowSnd[i]) { _rowSnd[i] = true; _playSe?.Invoke("SE_0020"); }
                float t = Mathf.Clamp01((el - start) / RowSlideSec);
                float dx = Mathf.Lerp(RowStartX, 0f, EaseOut(t));
                var p = _rowRoots[i].transform.localPosition; p.x = dx; _rowRoots[i].transform.localPosition = p;
            }
            float rowsInAt = _rowRoots.Count > 0 ? (_rowRoots.Count - 1) * RowStaggerSec + RowSlideSec : 0f;

            // (2) once all rows are in: count up EXP / G (SE_0021).
            if (!_rewardArmed && el >= rowsInAt)
            { _rewardArmed = true; _expTotal?.SetTarget(_expTarget, Time.time); _gTotal?.SetTarget(_coinsTarget, Time.time); }
            if (!_expSnd && el >= rowsInAt) { _expSnd = true; _playSe?.Invoke("SE_0021"); }
            if (_rewardArmed) { _expTotal?.Tick(Time.time); _gTotal?.Tick(Time.time); }

            // (3) LAST beat (SE_0022): the結算 chime ALWAYS fires — 自由模式 與 GAME OVER 也要有,只是不出 YOU WIN/LOSE 旗
            // (自由模式無輸贏字幕;GAME OVER 已用死亡字幕交代)。一般排名輸贏才把旗從 ~螢幕寬 zoom 進到定位。
            float bannerAt = rowsInAt + ExpHoldSec;
            bool wantBanner = _showBanner && !_gameOver;
            var banner = _bannerLocalWon ? _bannerWin : _bannerLose;
            if (!_bannerShown && el >= bannerAt)
            {
                _bannerShown = true; _playSe?.Invoke("SE_0022");
                if (wantBanner) { _bannerStart = Time.time; _bannerStatic = false; if (banner) banner.SetActive(true); }
            }
            if (_bannerShown && wantBanner) UpdateBanner(banner);

            // OK (Enter / click) confirms; save-record is a P1 stub (no-op for now)
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Escape)) { Confirm(); return; }
            if (Input.GetMouseButtonDown(0) && _cam != null)
            {
                var w = _cam.ScreenToWorldPoint(Input.mousePosition);
                if (_okBtn && _okBtn.sprite && _okBtn.bounds.Contains(new Vector3(w.x, w.y, _okBtn.transform.position.z))) { Confirm(); return; }
            }
            // 沒人按:面板開著 autoConfirmSec 秒後自己按下去(線上 = 30 秒)。
            if (autoConfirmSec > 0f && el >= autoConfirmSec) Confirm();
        }

        // 「確定」的唯一出口(按鈕 / Enter / Esc / 逾時自動都走這裡)。一局只送一次 —— 自動確定之後
        // 面板還在 Tick,不擋的話會每一幀再送一次同一個確定。
        private void Confirm()
        {
            if (_confirmed) return;
            _confirmed = true;
            OnConfirm?.Invoke();
        }

        // Position + scale the active WIN/LOSE banner (live F4 bannerX/Y/finalScale/animSec). Zooms from ~screen-width
        // unless held static (preview). GAME OVER no longer uses this — it's a plain row child now.
        private void UpdateBanner(GameObject banner)
        {
            if (!banner) return;
            float t = _bannerStatic ? _bannerStaticT : EaseOut(Mathf.Clamp01((Time.time - _bannerStart) / Mathf.Max(0.01f, bannerAnimSec)));
            // WIN/LOSE: slide the (tunable) START centre → FIXED END centre, scaling the (tunable) START size → 1.
            banner.transform.position = Vector3.Lerp(SdoLayout.ToWorld(bannerStartX, bannerStartY, 0f),
                                                     SdoLayout.ToWorld(BannerEndX, BannerEndY, 0f), t);
            banner.transform.localScale = Vector3.one * Mathf.Lerp(bannerStartScale, 1f, t);
        }

        /// <summary>F4 preview: hold the WIN/LOSE banner STATIC at the animation START (atStart=true) or END (false),
        /// so the start point can be placed live.</summary>
        public void PreviewBanner(bool win, bool atStart) { ShowOneBanner(win); _bannerStatic = true; _bannerStaticT = atStart ? 0f : 1f; }
        /// <summary>F4 test: replay the WIN/LOSE animation (start pos + screen-width → final pos/size).</summary>
        public void PlayBannerTest(bool win) { ShowOneBanner(win); _bannerStatic = false; _bannerStart = Time.time; _playSe?.Invoke("SE_0022"); }
        private void ShowOneBanner(bool win)
        {
            _gameOver = false; _showBanner = true; _bannerLocalWon = win; _bannerShown = true;   // F4 preview always animates the banner
            if (_bannerWin) _bannerWin.SetActive(win);
            if (_bannerLose) _bannerLose.SetActive(!win);
        }

        public void Hide() { if (_root) _root.SetActive(false); Visible = false; }

        // ---- helpers ----

        private static float EaseOut(float t) => 1f - (1f - t) * (1f - t);

        private SpriteRenderer NewSR(string name, Sprite spr, int order)
        {
            var sr = new GameObject(name).AddComponent<SpriteRenderer>();
            sr.transform.SetParent(_root.transform, false);
            sr.sprite = spr; sr.sortingOrder = order;
            // A LoadPanelSprite crop has its RGB already × alpha and MUST draw with Blend One OneMinusSrcAlpha, or it
            // comes out dark. Pair it here so every caller (buttons, banners, digits, badges) gets it without thinking.
            // PER-TEXTURE material, never the shared UI one: a SpriteRenderer with a custom material does not rebind
            // _MainTex per renderer, so one shared instance would make every sprite on this panel draw the SAME texture
            // (使用者回報「數字位置畫出 YOU WIN、確定鈕變成保存錄像」). See SdoExtracted.PremultSpriteMaterial.
            if (spr != null && SdoExtracted.IsPremultTexture(spr.texture))
            {
                var mat = SdoExtracted.PremultSpriteMaterial(spr.texture);
                if (mat != null) sr.sharedMaterial = mat;
            }
            return sr;
        }

        private static void Place(SpriteRenderer sr, float x, float y) => SdoLayout.PlaceTopLeft(sr, x, y, 0f);

        private void Child(GameObject parent, SpriteRenderer sr, float x, float y)
        { Place(sr, x, y); sr.transform.SetParent(parent.transform, true); }

        // left-aligned digit run at (x,y); leading zeros hidden when hideZero (value 0 still shows one "0").
        private void DrawNum(GameObject parent, Sprite[] digits, long value, float x, float y, bool hideZero)
        {
            if (digits == null || digits.Length < 10) return;
            string s = (value < 0 ? 0 : value).ToString();
            float cx = x;
            for (int i = 0; i < s.Length; i++)
            {
                int d = s[i] - '0';
                var sr = NewSR("d", digits[d], OrderRow);
                Place(sr, cx, y); sr.transform.SetParent(parent.transform, true);
                cx += digits[d].bounds.size.x;
            }
        }

        // Faithful NumLabel layout (decompiled SetNumber FUN_00470d60 + SetRect FUN_0043dd60): `labelnum` fixed cells
        // laid out from baseX, each cell = the digit-strip width (fixed pitch); the value fills the RIGHTMOST cells,
        // leading-zero cells stay blank (hidezero). The number therefore reads RIGHT-ALIGNED within [baseX, baseX+labelnum*pitch].
        private void DrawNumLabel(GameObject parent, Sprite[] digits, long value, float baseX, int labelnum, float y)
        {
            if (digits == null || digits.Length < 10) return;
            float pitch = digits[0].bounds.size.x;
            string s = (value < 0 ? 0 : value).ToString();
            for (int k = 0; k < s.Length && k < labelnum; k++)        // k = 0 → rightmost (lowest) digit
            {
                int d = s[s.Length - 1 - k] - '0';
                float cellLeft = baseX + (labelnum - 1 - k) * pitch;  // fill rightmost cells; leading cells stay blank
                var sr = NewSR("d", digits[d], OrderRow);
                Place(sr, cellLeft, y); sr.transform.SetParent(parent.transform, true);
            }
        }

        // fixed-width digit run (e.g. the 2-digit accuracy decimals) — pads with leading zeros.
        private void DrawNumFixed(GameObject parent, Sprite[] digits, long value, int width, float x, float y)
        {
            if (digits == null || digits.Length < 10) return;
            string s = (value < 0 ? 0 : value).ToString().PadLeft(width, '0');
            float cx = x;
            for (int i = 0; i < s.Length; i++)
            {
                int d = s[i] - '0';
                var sr = NewSR("d", digits[d], OrderRow);
                Place(sr, cx, y); sr.transform.SetParent(parent.transform, true);
                cx += digits[d].bounds.size.x;
            }
        }

        private void ClearRows()
        {
            foreach (var go in _rowRoots)
                if (go) { go.SetActive(false); DestroyOwned(go); }
            _rowRoots.Clear();
            foreach (var hb in _headObjs)
            {
                if (hb.go) { hb.go.SetActive(false); DestroyOwned(hb.go); }
                if (hb.sr)
                {
                    hb.sr.gameObject.SetActive(false);
                    DestroyOwned(hb.sr.gameObject);
                }
                if (hb.mat) DestroyOwned(hb.mat);
            }
            _headObjs.Clear();
            _nicks.Clear();   // the Label3D objects live under row-roots (destroyed above)
        }

        private static void DestroyOwned(UnityEngine.Object obj)
        {
            if (!obj) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(obj);
            else UnityEngine.Object.DestroyImmediate(obj);
        }
    }
}
