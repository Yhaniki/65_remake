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

        // other players' nick colour (online XML uses 0xffa4b4ef — pale blue); local stays yellow.
        private static readonly Color NickOther = new Color(0xA4 / 255f, 0xB4 / 255f, 0xEF / 255f, 1f);

        public System.Action OnConfirm;
        public bool Visible { get; private set; }

        private Camera _cam;
        private GameObject _root;
        private GameObject _bannerWin, _bannerLose;
        private readonly List<GameObject> _rowRoots = new List<GameObject>();
        private SpriteRenderer _okBtn, _saveBtn;
        private float _showStart = -1f;
        private System.Action<string> _playSe;     // SE hook (Step1Game.PlaySe) — rows pop with SE_0020, finish SE_0022
        private bool[] _rowSnd; private bool _finalSnd;

        // digit strips + badges (loaded once)
        private Sprite[] _num8, _num3, _scoreNum, _scoreNumS;
        private Sprite _percent, _dot, _allCombo, _pad, _level;
        private readonly Dictionary<int, Sprite> _rankBadge = new Dictionary<int, Sprite>();

        // row target Y per rank (online DDRITEMSTATISTIC Rank1..6 windows) and slide tuning
        private static readonly float[] RowY = { 162f, 216f, 270f, 324f, 374f, 424f };
        private const float RowSlideSec = 1.0f, RowStaggerSec = 0.12f, BannerScaleSec = 0.3f, RowStartX = 800f;

        /// <summary>Load art + build the static panel (bg + buttons + banners), hidden. Call once.</summary>
        public void Build(Camera hudCam)
        {
            _cam = hudCam;
            _root = new GameObject("ResultScreen");
            string dir = SdoExtracted.ItemStatisDir;

            // background: StatisItem0..3 at design y=115, StatisItem4..7 at y=371 (each native-size, top-left placed).
            for (int i = 0; i < 8; i++)
            {
                var s = SdoExtracted.LoadAn1(dir, "StatisItem" + i + ".an");
                if (s != null) Place(NewSR("Bg" + i, s, OrderBg), (i % 4) * 256, (i < 4) ? 115 : 371);
            }

            _num8 = SdoExtracted.LoadAn(dir, "Num8.an");
            _num3 = SdoExtracted.LoadAn(dir, "Num3.an");
            _scoreNum = SdoExtracted.LoadAn(dir, "score_num.an");
            _scoreNumS = SdoExtracted.LoadAn(dir, "score_numS.an");
            _percent = SdoExtracted.LoadAn1(dir, "percent.an");
            _dot = SdoExtracted.LoadAn1(dir, "dot.an");
            _allCombo = SdoExtracted.LoadAn1(dir, "100.an");
            _pad = SdoExtracted.LoadAn1(dir, "pad.an");
            _level = SdoExtracted.LoadAn1(dir, "Statis12.an");

            // win/lose banners — single sprites cropped from BALANCE.png (Statis28 = win @ design (487,38), Statis30 = lose @ (488,38)).
            _bannerWin = BuildBanner("BannerWin", dir, "Statis28.an", 487, 38);
            _bannerLose = BuildBanner("BannerLose", dir, "Statis30.an", 488, 38);

            // buttons (OK = Statis25, save-record = Statis22), bottom-right.
            _okBtn = NewSR("OkBtn", SdoExtracted.LoadAn1(dir, "Statis25.an"), OrderBtn); Place(_okBtn, 694, 493);
            _saveBtn = NewSR("SaveBtn", SdoExtracted.LoadAn1(dir, "Statis22.an"), OrderBtn); Place(_saveBtn, 595, 493);

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

        /// <summary>Populate the panel with this round's ranked rows + song info + the local reward, then start the
        /// banner scale-in and row slide-in. <paramref name="localWon"/> picks the YouWin / YouLose banner.</summary>
        public void Show(string songTitle, string difficulty, Row[] rows, bool localWon,
                         int expGained, int coinsGained, System.Action<string> playSe = null)
        {
            ClearRows();
            _playSe = playSe; _rowSnd = new bool[rows != null ? rows.Length : 0]; _finalSnd = false;
            string dir = SdoExtracted.ItemStatisDir;

            // song info row (yellow), top-left (songname 13,72 / songlevel 176,72)
            NewText("SongName", songTitle ?? "", 16, 13, 72, TextAnchor.UpperLeft, TextStyles.FaceYellow);
            NewText("SongLevel", difficulty ?? "", 16, 176, 72, TextAnchor.UpperLeft, TextStyles.FaceYellow);

            for (int i = 0; i < rows.Length && i < RowY.Length; i++)
                BuildRow(dir, rows[i], RowY[i]);

            // bottom reward block (local player): 經驗 EXP and G幣 coins.
            BuildRewardBlock(dir, expGained, coinsGained);

            if (_bannerWin) _bannerWin.SetActive(localWon);
            if (_bannerLose) _bannerLose.SetActive(!localWon);

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

            // rank badge (rank/<n>.png) at (0, y-13)
            if (_rankBadge.TryGetValue(r.Rank, out var badge) == false)
            { badge = SdoExtracted.LoadImage(dir, "rank/" + Mathf.Clamp(r.Rank, 1, 8) + ".PNG"); _rankBadge[r.Rank] = badge; }
            if (badge) Child(rowRoot, NewSR("Rank", badge, OrderRow), 0, y - 13);

            if (_pad) Child(rowRoot, NewSR("Pad", _pad, OrderRow), 56, y + 1);

            // nick (local = yellow, others = pale blue)
            var nick = NewText("Nick", r.Name ?? "", 16, 134, y + 1, TextAnchor.UpperLeft,
                               r.IsLocal ? TextStyles.FaceYellow : NickOther);
            nick.root.transform.SetParent(rowRoot.transform, true);

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
                if (_dot) Child(rowRoot, NewSR("Dot", _dot, OrderRow), 595, y + 8);
                DrawNumFixed(rowRoot, _num3, acc100 % 100, 2, 605, y + 6);
                if (_percent) Child(rowRoot, NewSR("Pct", _percent, OrderRow), 624, y + 6);
            }

            DrawNum(rowRoot, _num3, r.Score, 664, y + 6, true);
            if (_level) Child(rowRoot, NewSR("Level", _level, OrderRow), 740, y - 6);

            rowRoot.transform.localPosition = new Vector3(RowStartX, 0f, 0f);   // push the assembled row off-screen right
        }

        // 經驗 / G幣 block at the bottom-left of the panel (design coords from DDRITEMSTATISTIC.XML).
        // Mapping (the remake has no persistent profile, so "current" = 0 and "total" = earned this round):
        //   exp (328,495)=current(0)  expadd (408,495)=+earned  expall (350,526)=total
        //   G1  (77,495) =+earned     G      (89,526) =total coins
        private void BuildRewardBlock(string dir, int expGained, int coinsGained)
        {
            // labels (經驗 / 榮譽 / 徽章 icons)
            var lbExp = NewSR("LbExp", SdoExtracted.LoadAn1(dir, "LB_Exp.an"), OrderRowText); if (lbExp.sprite) Place(lbExp, 34, 565);
            var lbHonor = NewSR("LbHonor", SdoExtracted.LoadAn1(dir, "LB_Honor.an"), OrderRowText); if (lbHonor.sprite) Place(lbHonor, 78, 565);
            var lbBedge = NewSR("LbBedge", SdoExtracted.LoadAn1(dir, "LB_Bedge.an"), OrderRowText); if (lbBedge.sprite) Place(lbBedge, 124, 565);

            // EXP: current / +earned / total
            DrawNum(_root, _scoreNumS, 0, 328, 495, true);
            DrawNum(_root, _scoreNumS, expGained, 408, 495, true);
            DrawNum(_root, _scoreNum, expGained, 350, 526, true);

            // G幣 coins: +earned / total
            DrawNum(_root, _scoreNumS, coinsGained, 77, 495, true);
            DrawNum(_root, _scoreNum, coinsGained, 89, 526, true);
        }

        /// <summary>Animate the banner scale-in and row slide-in; hit-test the OK / save buttons.</summary>
        public void Tick()
        {
            if (!Visible) return;
            float el = Time.time - _showStart;

            // banner scale-in 3→1 over BannerScaleSec (root sits at the banner centre, so localScale pivots there)
            float bs = Mathf.Lerp(3f, 1f, Mathf.Clamp01(el / BannerScaleSec));
            if (_bannerWin && _bannerWin.activeSelf) _bannerWin.transform.localScale = new Vector3(bs, bs, 1f);
            if (_bannerLose && _bannerLose.activeSelf) _bannerLose.transform.localScale = new Vector3(bs, bs, 1f);

            // rows slide in from +RowStartX (design px) to 0, staggered — each one pops with SE_0020 as it starts
            for (int i = 0; i < _rowRoots.Count; i++)
            {
                float start = i * RowStaggerSec;
                if (el >= start && _rowSnd != null && i < _rowSnd.Length && !_rowSnd[i]) { _rowSnd[i] = true; _playSe?.Invoke("SE_0020"); }
                float t = Mathf.Clamp01((el - start) / RowSlideSec);
                float dx = Mathf.Lerp(RowStartX, 0f, EaseOut(t));   // design px offset
                var p = _rowRoots[i].transform.localPosition; p.x = dx; _rowRoots[i].transform.localPosition = p;
            }
            // all rows in -> the settle/finish chime (SE_0022)
            if (!_finalSnd && _rowRoots.Count > 0 && el >= (_rowRoots.Count - 1) * RowStaggerSec + RowSlideSec)
            { _finalSnd = true; _playSe?.Invoke("SE_0022"); }

            // OK (Enter / click) confirms; save-record is a P1 stub (no-op for now)
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Escape)) { OnConfirm?.Invoke(); return; }
            if (Input.GetMouseButtonDown(0) && _cam != null)
            {
                var w = _cam.ScreenToWorldPoint(Input.mousePosition);
                if (_okBtn && _okBtn.sprite && _okBtn.bounds.Contains(new Vector3(w.x, w.y, _okBtn.transform.position.z))) OnConfirm?.Invoke();
            }
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
        }
    }
}
