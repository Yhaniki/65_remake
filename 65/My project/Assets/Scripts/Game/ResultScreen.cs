using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 結算畫面 (result screen) — port of the ONLINE Dance!Online panel
    /// (Extracted/UI/STATIS/ITEMSTATISTIC/DDRITEMSTATISTIC.XML, 800×600 design space). Drawn with
    /// SpriteRenderers on the HUD ortho camera, ON TOP of the live stage backdrop. Layout:
    ///   • background = StatisItem0..7 tiles (4×2 grid at design y=115 / y=371),
    ///   • a top song-info row (songname/songlevel),
    ///   • a YouWin / YouLose banner cropped from BALANCE.png (Statis28 = win, Statis30 = lose) that scales 3→1,
    ///   • up to 6 rank rows that slide in from the right (x 800→0, 1s, staggered), each showing the rank badge,
    ///     nick, combo / perfect / cool / bad / miss, hit-rate (or the 100 all-combo marker) and score,
    ///   • a bottom reward block: 經驗 EXP (current / +earned / total) and G幣 coins, plus the OK / save buttons.
    /// Number columns use the original digit strips (Num8 / Num3 / score_num / score_numS).
    /// Owned and driven by <see cref="Step1Game"/>: <see cref="Build"/> once, <see cref="Show"/> at the settle
    /// beat, <see cref="Tick"/> every result frame. <see cref="OnConfirm"/> fires when OK is pressed.
    /// </summary>
    public sealed class ResultScreen
    {
        public struct Row
        {
            public int Rank;            // 1-based place
            public string Name;
            public int Perfect, Cool, Bad, Miss, MaxCombo;
            public double Accuracy;     // 0..100
            public long Score;
            public string Grade;        // "S" / "A+" / ... (kept for callers; not drawn in the online layout)
            public bool IsLocal;
            public bool FullCombo;      // 100% — shows the AllCombo marker instead of the hit-rate digits
        }

        // sorting orders (above the HUD; the HUD/board is hidden at result anyway)
        private const int OrderBg = 120, OrderRow = 130, OrderRowText = 134, OrderBanner = 138, OrderBtn = 140, OrderText = 144;

        public System.Action OnConfirm;
        public bool Visible { get; private set; }

        private Camera _cam;
        private GameObject _root;
        private GameObject _bannerWin, _bannerLose, _bannerOver;   // _bannerOver = GAME OVER (RANK/7.png) on HP-out
        private readonly List<GameObject> _rowRoots = new List<GameObject>();
        private SpriteRenderer _okBtn, _saveBtn;
        private float _showStart = -1f;
        private System.Action<string> _playSe;     // SE hook (Step1Game.PlaySe)
        private bool[] _rowSnd;
        // result sequence flags/timers: rows (SE_0020, 500ms apart) → EXP/G roll (SE_0021) → win/lose banner zoom (SE_0022)
        private bool _expSnd, _bannerShown, _bannerLocalWon, _gameOver;
        private float _bannerStart, _overImgW = 50f;
        public float gameOverScale = 4f, gameOverY = 230f;   // GAME OVER: final scale + centre Y (zoom from screen-width)

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

        // per-row avatar head: local player = live RenderTexture (set by Step1Game), others = tinted placeholder box
        private Texture _localHead;
        private Sprite _placeholderHead;
        // ---- F4-tunable layout (live; see Step1Game "Result" tab) ----
        public float nickX = 109f, nickYOff = 10f, nickSize = 22f;          // nickname: column x / vertical offset from RowY / font px (tuned)
        public float headBoxX = 30f, headBoxYOff = 12f, headBoxSize = 48f;  // head portrait box: left x / top offset from RowY / SQUARE size (tuned)
        private readonly List<(Label3D lbl, float rowY)> _nicks = new List<(Label3D, float)>();
        private struct HeadObj { public GameObject go; public SpriteRenderer sr; public float rowY; public bool placeholder; }
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

            _num8 = SdoExtracted.LoadAn(dir, "Num8.an");
            _num3 = SdoExtracted.LoadAn(dir, "Num3.an");
            _scoreNum = SdoExtracted.LoadAn(dir, "score_num.an");
            _scoreNumS = SdoExtracted.LoadAn(dir, "score_numS.an");
            _percent = SdoExtracted.LoadAn1(dir, "percent.an");
            _dot = SdoExtracted.LoadAn1(dir, "dot.an");
            _allCombo = SdoExtracted.LoadAn1(dir, "100.an");
            // 成績 letters (02/): map our grade band → the official sprite (A0=A++, A1=A+, A2=A, …).
            _gradeSprites["S"]  = SdoExtracted.LoadImage(dir, "02/A0.PNG");
            _gradeSprites["A+"] = SdoExtracted.LoadImage(dir, "02/A1.PNG");
            _gradeSprites["A"]  = SdoExtracted.LoadImage(dir, "02/A2.PNG");
            _gradeSprites["B"]  = SdoExtracted.LoadImage(dir, "02/B2.PNG");
            _gradeSprites["C"]  = SdoExtracted.LoadImage(dir, "02/C2.PNG");
            _gradeSprites["D"]  = SdoExtracted.LoadImage(dir, "02/D2.PNG");

            // win/lose banners — single sprites cropped from BALANCE.png (Statis28 = win @ design (487,38), Statis30 = lose @ (488,38)).
            _bannerWin = BuildBanner("BannerWin", dir, "Statis28.an", 487, 38);
            _bannerLose = BuildBanner("BannerLose", dir, "Statis30.an", 488, 38);
            // GAME OVER (RANK/7.png) — shown CENTRED when HP runs out, instead of the win/lose banner.
            var overSpr = SdoExtracted.LoadImage(dir, "RANK/7.PNG");
            _bannerOver = BuildBannerCentered("BannerOver", overSpr, 400f, gameOverY);
            if (overSpr) _overImgW = overSpr.bounds.size.x;

            // buttons (OK = Statis25, save-record = Statis22), bottom-right.
            _okBtn = NewSR("OkBtn", SdoExtracted.LoadAn1(dir, "Statis25.an"), OrderBtn); Place(_okBtn, 694, 493);
            _saveBtn = NewSR("SaveBtn", SdoExtracted.LoadAn1(dir, "Statis22.an"), OrderBtn); Place(_saveBtn, 595, 493);

            // 1×1 white sprite used (tinted) as the placeholder head box for rows without a live portrait.
            var pht = new Texture2D(1, 1); pht.SetPixel(0, 0, Color.white); pht.Apply();
            _placeholderHead = Sprite.Create(pht, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);

            _root.SetActive(false);
        }

        // A banner placed at design (x,y) under its own root whose origin is the banner centre, so localScale
        // pivots there for the 3→1 scale-in.
        private GameObject BuildBanner(string name, string dir, string an, float x, float y)
        {
            var go = new GameObject(name); go.transform.SetParent(_root.transform, false);
            var sr = NewSR(name + "Img", SdoExtracted.LoadAn1(dir, an), OrderBanner);
            Place(sr, x, y);
            go.transform.position = sr.transform.position;        // root at the banner centre
            sr.transform.SetParent(go.transform, true);
            go.SetActive(false);
            return go;
        }

        // A banner whose CENTRE sits at design (cx,cy) — used for the centred GAME OVER image. Scale pivots at the centre.
        private GameObject BuildBannerCentered(string name, Sprite spr, float cx, float cy)
        {
            var go = new GameObject(name); go.transform.SetParent(_root.transform, false);
            go.transform.position = SdoLayout.ToWorld(cx, cy, 0f);
            var sr = NewSR(name + "Img", spr, OrderBanner);
            sr.transform.SetParent(go.transform, false);
            sr.transform.localPosition = Vector3.zero;            // sprite (centre-pivot) sits at the go origin = (cx,cy)
            go.SetActive(false);
            return go;
        }

        /// <summary>Populate the panel with this round's ranked rows + song info + the local reward, then start the
        /// banner scale-in and row slide-in. <paramref name="localWon"/> picks the YouWin / YouLose banner.</summary>
        public void Show(string songTitle, string difficulty, Row[] rows, bool localWon,
                         int expGained, int coinsGained, Texture localHead = null, bool gameOver = false,
                         System.Action<string> playSe = null)
        {
            ClearRows();
            _playSe = playSe; _rowSnd = new bool[rows != null ? rows.Length : 0];
            _expSnd = false; _bannerShown = false; _bannerStatic = false; _rewardArmed = false; _localHead = localHead; _gameOver = gameOver;
            string dir = SdoExtracted.ResultStatisDir;

            // song info row (yellow), top-left (songname 13,72 / songlevel 176,72)
            NewText("SongName", songTitle ?? "", 16, 13, 72, TextAnchor.UpperLeft, TextStyles.FaceYellow);
            NewText("SongLevel", difficulty ?? "", 16, 176, 72, TextAnchor.UpperLeft, TextStyles.FaceYellow);

            for (int i = 0; i < rows.Length && i < RowY.Length; i++)
                BuildRow(dir, rows[i], RowY[i]);

            // STATIC avatar heads in the baked frames (don't slide with the rows): local = live 3D portrait, others placeholder
            for (int i = 0; i < rows.Length && i < RowY.Length; i++)
                BuildHeadBox(rows[i].IsLocal, RowY[i]);

            // bottom reward block (local player): 經驗 EXP and G幣 coins.
            BuildRewardBlock(dir, expGained, coinsGained);

            // banner is the LAST beat (after rows + EXP) — keep all hidden for now; Tick reveals the right one.
            _bannerLocalWon = localWon;
            if (_bannerWin) _bannerWin.SetActive(false);
            if (_bannerLose) _bannerLose.SetActive(false);
            if (_bannerOver) _bannerOver.SetActive(false);

            _root.SetActive(true);
            Visible = true;
            _showStart = Time.time;
        }

        // One ranked row at its design RowY, under its own root so the whole row slides in from the right.
        private void BuildRow(string dir, Row r, float y)
        {
            // rowRoot stays at the ORIGIN while children are parented (worldPositionStays=true → child local =
            // its design world position). The off-screen start offset (+RowStartX) is applied AFTER all children
            // are attached, so the whole row shifts as one unit; Tick then slides it back to 0.
            var rowRoot = new GameObject("Row" + r.Rank); rowRoot.transform.SetParent(_root.transform, false);
            _rowRoots.Add(rowRoot);

            // rank badge (rank/<n>.png) at (0, y-8) — STATISTIC rank NumLabel y=-8
            if (_rankBadge.TryGetValue(r.Rank, out var badge) == false)
            { badge = SdoExtracted.LoadImage(dir, "rank/" + Mathf.Clamp(r.Rank, 1, 8) + ".PNG"); _rankBadge[r.Rank] = badge; }
            if (badge) Child(rowRoot, NewSR("Rank", badge, OrderRow), 0, y - 8);

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
            if (_rewardRoot) Object.Destroy(_rewardRoot);
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
        // live head-portrait RenderTexture (Step1Game renders the avatar as a close-up at a 45° angle, idle moves);
        // other rows → a tinted placeholder box (opponents have no avatar data, matching the original's placeholder).
        private void BuildHeadBox(bool isLocal, float rowY)
        {
            float topY = rowY - headBoxYOff;
            if (isLocal && _localHead != null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Object.Destroy(go.GetComponent<Collider>());
                var mr = go.GetComponent<MeshRenderer>();
                // Unlit/Transparent so the RT's transparent (alpha-0) background shows the panel/stage through — no black box.
                var mat = new Material(Shader.Find("Unlit/Transparent")) { mainTexture = _localHead };
                mr.sharedMaterial = mat; mr.sortingOrder = OrderRow;
                go.transform.SetParent(_root.transform, true);    // static (not in the sliding rows)
                go.transform.position = new Vector3(SdoLayout.WorldX(headBoxX) + headBoxSize / 2f,
                                                    SdoLayout.WorldY(topY) - headBoxSize / 2f, -2f);
                go.transform.localScale = new Vector3(headBoxSize, headBoxSize, 1f);
                _headObjs.Add(new HeadObj { go = go, rowY = rowY, placeholder = false });
            }
            else
            {
                var sr = NewSR("HeadBox", _placeholderHead, OrderRow);   // NewSR already parents to _root
                sr.color = new Color(0.42f, 0.52f, 0.48f, 0.85f);        // neutral grey-green placeholder
                SdoLayout.PlaceBox(sr, headBoxX, topY, headBoxSize, headBoxSize, -2f);
                _headObjs.Add(new HeadObj { sr = sr, rowY = rowY, placeholder = true });
            }
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
                else if (hb.go)
                {
                    hb.go.transform.position = new Vector3(SdoLayout.WorldX(headBoxX) + headBoxSize / 2f,
                                                           SdoLayout.WorldY(topY) - headBoxSize / 2f, -2f);
                    hb.go.transform.localScale = new Vector3(headBoxSize, headBoxSize, 1f);
                }
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

            // (3) LAST: reveal the result banner — GAME OVER (centred) if HP failed, else YouWin/YouLose — zooming from
            // ~screen-width down to its size (SE_0022).
            float bannerAt = rowsInAt + ExpHoldSec;
            var banner = _gameOver ? _bannerOver : (_bannerLocalWon ? _bannerWin : _bannerLose);
            if (!_bannerShown && el >= bannerAt)
            {
                _bannerShown = true; _bannerStart = Time.time; _bannerStatic = false; _playSe?.Invoke("SE_0022");
                if (banner) banner.SetActive(true);
            }
            if (_bannerShown) UpdateBanner(banner);

            // OK (Enter / click) confirms; save-record is a P1 stub (no-op for now)
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Escape)) { OnConfirm?.Invoke(); return; }
            if (Input.GetMouseButtonDown(0) && _cam != null)
            {
                var w = _cam.ScreenToWorldPoint(Input.mousePosition);
                if (_okBtn && _okBtn.sprite && _okBtn.bounds.Contains(new Vector3(w.x, w.y, _okBtn.transform.position.z))) OnConfirm?.Invoke();
            }
        }

        // Position + scale the active result banner. Win/Lose use the F4 bannerX/Y/finalScale/animSec (live);
        // GAME OVER keeps its centred build position + gameOverScale. Zooms from ~screen-width unless held static (preview).
        private void UpdateBanner(GameObject banner)
        {
            if (!banner) return;
            float t = _bannerStatic ? _bannerStaticT : EaseOut(Mathf.Clamp01((Time.time - _bannerStart) / Mathf.Max(0.01f, bannerAnimSec)));
            if (_gameOver)
            {
                // GAME OVER: centred (build position), zoom screen-width → gameOverScale.
                float ovStart = SdoLayout.Width / Mathf.Max(1f, _overImgW);
                banner.transform.localScale = Vector3.one * Mathf.Lerp(ovStart, gameOverScale, t);
                return;
            }
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
            _gameOver = false; _bannerLocalWon = win; _bannerShown = true;
            if (_bannerWin) _bannerWin.SetActive(win);
            if (_bannerLose) _bannerLose.SetActive(!win);
            if (_bannerOver) _bannerOver.SetActive(false);
        }

        public void Hide() { if (_root) _root.SetActive(false); Visible = false; }

        // ---- helpers ----

        private static float EaseOut(float t) => 1f - (1f - t) * (1f - t);

        private SpriteRenderer NewSR(string name, Sprite spr, int order)
        {
            var sr = new GameObject(name).AddComponent<SpriteRenderer>();
            sr.transform.SetParent(_root.transform, false);
            sr.sprite = spr; sr.sortingOrder = order;
            return sr;
        }

        private static void Place(SpriteRenderer sr, float x, float y) => SdoLayout.PlaceTopLeft(sr, x, y, 0f);

        private void Child(GameObject parent, SpriteRenderer sr, float x, float y)
        { Place(sr, x, y); sr.transform.SetParent(parent.transform, true); }

        private Label3D NewText(string name, string text, float px, float x, float y, TextAnchor anchor, Color face)
        {
            var lbl = TextStyles.NewLabel(name, TextStyles.Style.ListOther, OrderRowText, px, anchor);
            lbl.SetColors(face, new Color(0.30f, 0.06f, 0f, 1f));
            lbl.Text = text;
            lbl.Position = SdoLayout.ToWorld(x, y, -3f);
            lbl.root.transform.SetParent(_root.transform, true);
            return lbl;
        }

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
            foreach (var go in _rowRoots) if (go) Object.Destroy(go);
            _rowRoots.Clear();
            foreach (var hb in _headObjs) { if (hb.go) Object.Destroy(hb.go); if (hb.sr) Object.Destroy(hb.sr.gameObject); }
            _headObjs.Clear();
            _nicks.Clear();   // the Label3D objects live under row-roots (destroyed above)
        }
    }
}
