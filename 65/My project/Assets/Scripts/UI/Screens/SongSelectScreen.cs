using System.Collections;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
using UnityEngine.UI;
using Sdo.Game;
using Sdo.Localization;
using Sdo.Settings;
using Sdo.UI.Catalog;
using Sdo.UI.Core;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>
    /// 選歌（ROOMDLG / MUSICSELDLG）忠實重製：原版美術依 RoomDlg/MusicSelDlg.xml 的 800×600 座標佈局
    /// （9 宮格底圖、可換唱片封面、難度三態頁籤、6 音樂分類頁籤、12 列歌單 + time/level 欄 + NEW 標籤、
    /// 搜尋、翻頁、歌曲資訊(演唱者/BPM/音符數)、場景預覽(真實名稱+縮圖)、模式/隊形/旁觀人數下拉、
    /// OK/Cancel/Close）。選歌會試聽 exper/&lt;fileId&gt;.ogg。資料流沿用 SongListModel→SongCatalog→GameSession。
    /// </summary>
    public sealed class SongSelectScreen : UIScreenBase
    {
        public override ScreenId Id => ScreenId.SongSelect;
        private const int PageSize = 12;
        private const int NewBadgeCount = 5;          // 最大編號的 N 首歌掛 NEW 標籤
        private const float NewBadgeFps = 12f;        // NEWSIGN.an colour-cycle speed (14 frames ≈ 1.2s loop)
        private const float PreviewVolume = 0.55f;
        private const float RoomDimBrightness = 0.8f; // 底下房間調暗到 ≈80% 亮度(黑幕 alpha = 1−此值);取代原本全黑

        // ---- row layout (from MusicSelDlg.xml) ----
        // The 435-wide highlight strip (MusicSelDlg95/73) spans the whole row from x≈299. The NEW badge sits flush
        // at the strip's left edge; the song name is indented past it (same indent on every row). RowTop0 is paired
        // with the header band (difficulty y84 / category y138) so the first row tucks under the category tabs with
        // no visible gap (the category sprite has ~12px transparent bottom padding).
        private const float RowTop0 = 165f, RowPitch = 21f;
        private const float HiX = 299f, BadgeX = 301f, NameX = 362f, NameW = 252f;
        private const float TimeX = 622f, TimeW = 72f, LevelX = 700f, LevelW = 36f, RowH = 19f;
        private const float BadgeW = 56f, BadgeH = 23f;
        // The animated NEWSIGN "New" sits in FRONT; the static new.an "★NEW!" sits BEHIND as a glow/backing (托襯).
        // Offset the animated "New" right past the backing's star and 1px up so it centres on the backing glow.
        private const float NewAnimDX = 18f, NewAnimDY = -1f;

        private static readonly Color ColComboList = FromArgb(0xff018194); // green-box dropdown list text (RGB 1,129,148)
        private static readonly Color ColRow = FromArgb(0xffc43e94);    // song name / level / time — 亮玫瑰洋紅(藍降,較原 c94bb3 飽和、比 a8318f 亮) + bold in AddRowText
        private static readonly Color ColInfo = FromArgb(0xff7cddd8);   // left info block (演唱者/BPM/音符數) — 貼近烘死標籤的亮青(僅比原 82e6e2 微深) + bold in MakeInfo
        private static readonly Color ColPage = FromArgb(0xff842200);   // page counter
        private static readonly Color ColCaption = FromArgb(0xffffffff); // scene caption

        // data (unchanged flow)
        private SongListModel _model;
        private List<SongCatalog.Entry> _filtered = new List<SongCatalog.Entry>();
        private SongCatalog.Entry _selected;
        private int _difficulty;   // 0=easy/1=normal/2=hard; set from Session in OnShow
        private int _page;
        private HashSet<int> _newIds = new HashSet<int>();   // fileIds that get a NEW badge (top-N newest)

        // disk (song jacket, swapped per selection; jacket is circular-masked so a square cover can't sweep out)
        private RectTransform _diskRoot;
        private Image _diskJacket;
        private Spinner _diskSpin;
        private bool _diskSpinPaused;   // 使用者點唱片切換的「停止轉動」；停轉時下一首也不轉；跳出再進 reset(OnShow)
        private bool _diskCanSpin;      // 目前唱片是不是真封面(可轉)；RANDOM/NONE=false
        private Sprite _iconRandom, _iconNone;   // ICONS/RANDOM.PNG (隨機) and ICONS/NONE.PNG (no cover) — both static (no spin)

        // difficulty tabs
        private Image[] _diffImg;
        private Sprite[] _diffNormal, _diffPushed;

        // category tabs (全部/隨機/收藏/最新/勁樂/懷舊) — toggle: the selected tab stays pushed
        private const int CatAll = 0, CatRandom = 1, CatFav = 2, CatNewest = 3, CatJam = 4, CatNostalgia = 5;
        private Image[] _catImg;
        private Sprite[] _catNormal, _catPushed;
        private int _category = CatAll;

        // 隨機 difficulty ranges — shown AS the list rows when the 隨機 tab is active; OK picks a random song from the pool.
        private struct RandRange { public string Key; public int Min, Max; }
        private static readonly RandRange[] RandRanges =
        {
            new RandRange { Key = "songselect.rand_1_5",  Min = 1,  Max = 5 },
            new RandRange { Key = "songselect.rand_1_9",  Min = 1,  Max = 9 },
            new RandRange { Key = "songselect.rand_5_9",  Min = 5,  Max = 9 },
            new RandRange { Key = "songselect.rand_all",  Min = 0,  Max = 99 },
            new RandRange { Key = "songselect.rand_5up",  Min = 5,  Max = 99 },
            new RandRange { Key = "songselect.rand_9up",  Min = 9,  Max = 99 },
            new RandRange { Key = "songselect.rand_13up", Min = 13, Max = 99 },
        };
        private int _randRange = 3;   // default = 全部

        // list rows
        private Image[] _rowHi, _rowNew, _rowNewBg;
        private Button[] _rowBtn;
        private PointerClickProxy[] _rowCtx;   // per-row right-click → 收藏加/刪彈出選單
        private TextMeshProUGUI[] _rowName, _rowTime, _rowLevel;

        // 收藏右鍵彈出選單（MUSICPOP.XML：Pop_Add=MusicSelDlg124/125/126、Pop_Del=127/128/129），疊在 Root 上、跟著滑鼠。
        // 不用全螢幕 overlay 擋點擊 → 選單開著時歌曲列仍是活的：右鍵另一首會直接換過去；「點在選單外就關」由 Update 判定。
        private GameObject _favPopup;
        private int _favPopupFrame;   // 開選單的幀（同一幀的 mousedown 不判關）
        private Camera _uiCam;   // 世界空間 canvas 的相機（滑鼠螢幕座標 → 800×600 設計座標用）
        private Sprite _hiNormal, _hiPushed;
        private Sprite[] _newFrames = new Sprite[0];   // NEWSIGN.an animation frames (colour-cycling NEW tag)
        private bool _newBadgeArt;                     // any NEW badge art (animation and/or new.an backing) loaded

        // misc widgets
        private TMP_InputField _search;
        private TextMeshProUGUI _pageLabel;
        private TextMeshProUGUI _infoArtist, _infoBpm, _infoNotes;

        // scene preview (real names + thumbnails; selector covers scene ids 0..30)
        private List<StageInfo> _stages;
        private int _sceneIndex;
        private Image _sceneBig;
        private TextMeshProUGUI _sceneName;

        // music preview (exper/<fileId>.ogg)
        private AudioSource _preview;
        private Coroutine _previewCo;
        private int _previewId = -1;
        private float _previewGateTime;   // unscaled time before which previews hold (set on entry so the open spin settles first)
        // Fallback when a song has no dedicated exper preview: loop a 20s window from the MIDDLE of the full song.
        private bool _previewWindow;
        private float _previewWinStart, _previewWinEnd;
        private const float PreviewWindowSec = 20f;

        // window open/close transition (spin-zoom in, shrink-fade out) — all dialog art lives under _window so it
        // animates as one piece; the combo popups parent themselves to Root, so they stay clear of the spin.
        private RectTransform _window;
        private CanvasGroup _windowCg;
        private WindowAnim _anim;
        private bool _closing;

        // 半透明黑幕：選歌是疊在房間上的 overlay(房間 3D + 整組 UI 都留在底下不隱藏)，這張黑幕把整個房間調暗到
        // ≈RoomDimBrightness 亮度，並吃掉點擊(raycast)避免點到底下的房間按鈕。full-screen，墊在對話框後面。
        private Image _dimScrim;

        private static string L(string k) => LocalizationManager.Get(k);

        protected override void BuildUI()
        {
            _model = SongListModel.FromCatalog();
            ComputeNewIds();
            _stages = new List<StageInfo>();
            foreach (var s in StageCatalog.Stages)
                if (s.Id >= 0 && s.Id <= StageCatalog.MaxSelectableId) _stages.Add(s);

            // 場景選擇器初值：反映 session/config 目前的場景（具體場景 → 對到清單位置+1；隨機 → 0）。這樣重開遊戲後
            // config 記住的場景會顯示在預覽，且不點場景直接確認也不會把持久化的場景覆蓋回隨機。
            _sceneIndex = 0;
            if (!Ctx.Session.StageRandom)
                for (int i = 0; i < _stages.Count; i++)
                    if (_stages[i].Id == Ctx.Session.StageId) { _sceneIndex = i + 1; break; }

            BuildBackground();
            BuildDisk();
            BuildDifficultyTabs();
            BuildCategoryTabs();
            BuildRows();
            BuildPaging();
            BuildSearch();
            BuildInfoBlock();
            BuildScenePreview();
            BuildBottomBar();
            BuildActionButtons();
            WrapInWindow();
            BuildDimScrim();   // AFTER WrapInWindow so it stays on Root (not inside the spinning _window)
        }

        // The dim scrim behind the dialog (取代原本全黑). 選歌不再是取代房間的獨立畫面，而是「疊在房間上」的 overlay：
        // 房間(3D 場景 + 整組 UI 按鈕)全部留在底下不隱藏，這張全螢幕半透明黑幕把整個房間調暗到 ≈RoomDimBrightness 亮度
        // (官方 MusicSelDlg 也是房間上的 modal)，同時 raycastTarget=true 吃掉點擊避免點到底下的房間按鈕。建在 WrapInWindow
        // 之後 → 留在 Root(不進會旋轉縮放的 _window)當穩定的全螢幕背景；設為第一個 sibling → 畫在最底層(房間之上、對話框之下)。
        private void BuildDimScrim()
        {
            _dimScrim = UIKit.AddImage(Root, "DimScrim", new Color(0f, 0f, 0f, 1f - RoomDimBrightness), raycast: true);
            UIKit.Stretch(_dimScrim.rectTransform);
            _dimScrim.rectTransform.SetAsFirstSibling();
        }

        // Re-parent everything built above under a single centred, pivot-0.5 window container so the open/close
        // transition can spin + scale + fade the whole dialog as one piece, then wire the press click (SE_0001)
        // onto every button under it. The combo popups create themselves under Root afterwards, so they're left out
        // of the spin (their own row clicks attach SE_0001 in SdoComboBox).
        private void WrapInWindow()
        {
            _window = UIKit.NewRect(Root, "Window");
            UIKit.Stretch(_window);
            _window.pivot = new Vector2(0.5f, 0.5f);
            _windowCg = _window.gameObject.AddComponent<CanvasGroup>();
            _anim = _window.gameObject.AddComponent<WindowAnim>();

            var kids = new List<Transform>();
            foreach (Transform c in Root) if (c != (Transform)_window) kids.Add(c);
            foreach (var c in kids) c.SetParent(_window, false);

            foreach (var b in _window.GetComponentsInChildren<Button>(true)) UiSfx.AttachClick(b);
        }

        public override void OnShow()
        {
            // open whoosh + fast spin-zoom in (Frameround.wav; ~0.2s)
            _closing = false;
            if (_windowCg != null) _windowCg.blocksRaycasts = true;
            if (_anim != null) { _anim.ResetOpen(); _anim.PlayIn(); }
            UiSfx.Play(UiSfx.FrameRound);

            _previewGateTime = Time.unscaledTime + 1f;   // hold music ~1s until the open spin settles
            _difficulty = (int)Ctx.Session.Difficulty;
            // Scene / random-range / mode·formation·looker combos PERSIST across visits — the screen is built once
            // and those fields/widgets are never reset here, so it reopens exactly as it was left.
            // EXCEPTIONS reset on every open: the category tab snaps back to 全部, and the search box is cleared —
            // so re-entering always shows the full list, not a stale filter/tab.
            _category = CatAll;
            _diskSpinPaused = false;   // 跳出再進 → 唱片轉動 reset：預設會轉（下面選歌時 SetDiskSpinning 會轉起來）
            if (_search != null)
            {
                _search.SetTextWithoutNotify(string.Empty);   // no notify: ApplyFilter below re-filters with empty text
                var searchPh = _search.placeholder;
                if (searchPh != null) searchPh.gameObject.SetActive(true);   // restore prompt (custom listeners hid it while typing)
            }
            RenderCategoryTabs();
            UpdateScene();

            if (_category == CatRandom)
            {
                // last visit ended on the 隨機 menu -> reopen it (range rows + RANDOM disc), don't jump to a song.
                RenderDiffTabs();
                StopPreview();
                _selected = null;
                SetDiskJacket(_iconRandom);
                SetDiskSpinning(false);
                _page = 0;
                RenderPage();
                UpdateInfo();
            }
            else
            {
                ApplyFilter();          // -> RenderDiffTabs + RenderPage
                // re-select last time's song if it's in the list (else the first), jumping to its page so it's visible.
                var prev = FindPreviousSelection();
                if (prev != null) { _page = _filtered.IndexOf(prev) / PageSize; Select(prev); }
                else if (_filtered.Count > 0) Select(_filtered[0]);
                else { _selected = null; StopPreview(); UpdateInfo(); UpdateDisk(); }   // empty -> NONE disc, no music
            }
        }

        // Close whoosh + shrink-fade out (Frameround.wav; ~0.5s), THEN switch screens. Freezes input on the window
        // for the duration so a stray click during the fade can't re-trigger selection or navigation.
        private void CloseTo(ScreenId target)
        {
            if (_closing) return;
            _closing = true;
            if (_windowCg != null) _windowCg.blocksRaycasts = false;
            UiSfx.Play(UiSfx.FrameRound);
            CloseFavPopup();
            StopPreview();
            if (_anim != null) _anim.PlayOut(() => GoTo(target));
            else GoTo(target);
        }

        public override void OnHide() { CloseFavPopup(); StopPreview(); }
        private void OnDisable() { CloseFavPopup(); StopPreview(); }   // covers canvas SetActive(false) on gameplay hand-off

        private void ComputeNewIds()
        {
            // NEW badge = the highest-fileId (newest) songs. Pick the top-N by fileId explicitly
            // (independent of the browse list's sort order) so the badge logic stays robust.
            _newIds.Clear();
            var byNew = new List<SongCatalog.Entry>(_model.All);
            byNew.Sort((a, b) => b.fileId.CompareTo(a.fileId));
            for (int i = 0; i < byNew.Count && i < NewBadgeCount; i++) _newIds.Add(byNew[i].fileId);
        }

        // ---------------- build helpers ----------------

        private void BuildBackground()
        {
            int[] xs = { 27, 283, 539 };
            int[] ys = { 20, 276, 531 };
            int n = 0;
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    UIKit.AddSprite(Root, "bg" + n, RoomDlgArt.An("MusicSelDlg" + (n++) + ".an"), xs[c], ys[r]);
        }

        private void BuildDisk()
        {
            // diskwin (44,78,237×237). The vinyl (MusicSelDlg106) is the circular MASK + base; the song jacket is a
            // child clipped to that circle, so even a SQUARE cover stays a disc and can't sweep into the info block
            // when spinning. The whole thing rotates (Circumgyrate 360°/1000ms) with a stop-then-spin-up on select.
            var root = UIKit.NewRect(Root, "disk");
            root.anchorMin = root.anchorMax = new Vector2(0f, 1f);
            root.pivot = new Vector2(0.5f, 0.5f);
            root.sizeDelta = new Vector2(237f, 237f);
            root.anchoredPosition = new Vector2(44 + 237 / 2f, -(78 + 237 / 2f));
            _diskRoot = root;
            _iconRandom = SongIcons.LoadNamed("RANDOM.PNG");   // 隨機 disc (ICONS/RANDOM.PNG)
            _iconNone = SongIcons.LoadNamed("NONE.PNG");        // "no cover" disc (ICONS/NONE.PNG)
            var baseImg = root.gameObject.AddComponent<Image>();
            baseImg.sprite = RoomDlgArt.An("MusicSelDlg106.an");
            baseImg.raycastTarget = true;   // 點唱片區域 → 切換停轉/轉動（ToggleDiskSpin）
            var mask = root.gameObject.AddComponent<Mask>();
            mask.showMaskGraphic = true;   // the vinyl shows as the base; children clip to its circular alpha

            var jr = UIKit.NewRect(root, "jacket");
            UIKit.Stretch(jr);
            _diskJacket = jr.gameObject.AddComponent<Image>();
            _diskJacket.raycastTarget = false;
            _diskJacket.color = new Color(1f, 1f, 1f, 0f);   // hidden until a song is picked

            _diskSpin = root.gameObject.AddComponent<Spinner>();

            // 點唱片：切換停止/轉動（不影響音樂）。透明角落也算唱片區域(矩形命中)，夠用。
            var spinToggle = root.gameObject.AddComponent<Button>();
            spinToggle.targetGraphic = baseImg;
            spinToggle.transition = Selectable.Transition.None;
            spinToggle.onClick.AddListener(ToggleDiskSpin);
        }

        private void BuildDifficultyTabs()
        {
            _diffImg = new Image[3];
            _diffNormal = new Sprite[3];
            _diffPushed = new Sprite[3];
            int[] nrm = { 15, 18, 21 };   // easy / normal / hard normal-state ids
            int[] psh = { 17, 20, 23 };   // pushed (selected) ids
            int[] x = { 283, 426, 569 };  // nudged right 1px (was 282/425/568)
            for (int i = 0; i < 3; i++)
            {
                _diffNormal[i] = RoomDlgArt.An("MusicSelDlg" + nrm[i] + ".an");
                _diffPushed[i] = RoomDlgArt.An("MusicSelDlg" + psh[i] + ".an");
                var img = UIKit.AddSprite(Root, "diff" + i, _diffNormal[i], x[i], 84, raycast: true);
                var btn = img.gameObject.AddComponent<Button>();
                btn.targetGraphic = img;
                btn.transition = Selectable.Transition.None;
                int idx = i;
                btn.onClick.AddListener(() => SetDifficulty(idx));
                _diffImg[i] = img;
            }
        }

        private void BuildCategoryTabs()
        {
            // 6 音樂分類頁籤 (全部/隨機/收藏/最新/勁樂/懷舊). Toggle: the selected tab stays PUSHED (like the difficulty
            // tabs), so we drive the sprite ourselves (Transition.None) instead of UGUI's spring-back SpriteSwap.
            int[][] ids = {
                new[]{24,25,26}, new[]{30,31,32}, new[]{33,34,35},
                new[]{36,37,38}, new[]{42,43,44}, new[]{39,40,41},
            };
            // nudged right 1px + up 8px (was x−1 / y138) so the tab row clears the first song name below.
            int[] x = { 293, 367, 442, 517, 593, 668 };
            _catImg = new Image[6];
            _catNormal = new Sprite[6];
            _catPushed = new Sprite[6];
            for (int i = 0; i < 6; i++)
            {
                _catNormal[i] = RoomDlgArt.An("MusicSelDlg" + ids[i][0] + ".an");
                _catPushed[i] = RoomDlgArt.An("MusicSelDlg" + ids[i][2] + ".an");
                var img = UIKit.AddSprite(Root, "musictype" + i, _catNormal[i], x[i], 130, raycast: true);
                var btn = img.gameObject.AddComponent<Button>();
                btn.targetGraphic = img;
                btn.transition = Selectable.Transition.None;
                int idx = i;
                btn.onClick.AddListener(() => SetCategory(idx));
                _catImg[i] = img;
            }
        }

        private void RenderCategoryTabs()
        {
            for (int i = 0; i < 6; i++)
                if (_catImg[i] != null)
                    UIKit.ApplySprite(_catImg[i], i == _category ? _catPushed[i] : _catNormal[i]);
        }

        private void SetCategory(int c)
        {
            _category = Mathf.Clamp(c, 0, 5);
            RenderCategoryTabs();
            _page = 0;
            if (_category == CatRandom)
            {
                StopPreview();
                _selected = null;
                SetDiskJacket(_iconRandom);   // RANDOM disc, no spin
                SetDiskSpinning(false);
                RenderPage();      // -> RenderRandomRows
                UpdateInfo();      // clears the value block (no song picked)
            }
            else
            {
                ApplyFilter();     // category + search -> rows
                if (_filtered.Count > 0) Select(_filtered[0]);
                else { _selected = null; StopPreview(); UpdateInfo(); UpdateDisk(); }   // empty -> NONE disc, no spin, no music
            }
        }

        // Show a sprite on the disc jacket (clipped to the vinyl circle); hide if null.
        private void SetDiskJacket(Sprite s)
        {
            if (_diskJacket == null) return;
            _diskJacket.sprite = s;
            _diskJacket.color = s != null ? Color.white : new Color(1f, 1f, 1f, 0f);
        }

        // Spin only for real song covers; the RANDOM / NONE discs stay parked upright. 使用者可用點唱片全域停轉
        // (_diskSpinPaused)：停轉時就算換到有封面的下一首也不轉（音樂照播，見 UpdateDisk 仍回 true）。
        private void SetDiskSpinning(bool canSpin)
        {
            _diskCanSpin = canSpin;
            if (_diskSpin == null) return;
            if (canSpin && !_diskSpinPaused) { _diskSpin.enabled = true; _diskSpin.Restart(); }   // stop, then spin up on cover change
            else { _diskSpin.enabled = false; if (_diskRoot != null) _diskRoot.localRotation = Quaternion.identity; }
        }

        // 點唱片區域：切換「停止轉動」。停=原地凍結（完全不碰音樂試聽）；開=真封面才轉、從當前角度接續全速
        // （RANDOM/NONE 維持不轉）。跳出畫面再進來會 reset 成會轉（見 OnShow）。
        private void ToggleDiskSpin()
        {
            _diskSpinPaused = !_diskSpinPaused;
            if (_diskSpin == null) return;
            if (_diskSpinPaused) _diskSpin.enabled = false;          // 停：原地凍結
            else if (_diskCanSpin) _diskSpin.enabled = true;         // 開：接續轉（真封面才轉）
        }

        private void BuildRows()
        {
            _hiNormal = RoomDlgArt.An("MusicSelDlg95.an");   // 435×19 row strip
            _hiPushed = RoomDlgArt.An("MusicSelDlg73.an");   // selected strip
            // Animated NEW tag (front layer): NEWSIGN.an cycles 4 colour crops of scene.png (14 frames). The static
            // new.an "★NEW!" backing is loaded below; if newsign is missing, that backing alone is still a valid badge.
            _newFrames = RoomDlgArt.AnFrames("newsign.an");
            _rowHi = new Image[PageSize];
            _rowNew = new Image[PageSize];
            _rowNewBg = new Image[PageSize];
            var newBgSprite = RoomDlgArt.An("new.an");   // static ★NEW! glow plate behind the animation
            _newBadgeArt = newBgSprite != null || _newFrames.Length > 0;
            _rowBtn = new Button[PageSize];
            _rowCtx = new PointerClickProxy[PageSize];
            _rowName = new TextMeshProUGUI[PageSize];
            _rowTime = new TextMeshProUGUI[PageSize];
            _rowLevel = new TextMeshProUGUI[PageSize];

            for (int i = 0; i < PageSize; i++)
            {
                float top = RowTop0 + RowPitch * i;

                // strip (incl. the purple selected box MusicSelDlg73) nudged right 2px + down 2px (was HiX / top−1).
                // The pushed (selected) sprite gets an extra 1px drop in ApplyRowHi; here we place the normal strip.
                var hi = UIKit.AddSprite(Root, "row" + i + "hi", _hiNormal, HiX + 2f, top + 1f, raycast: true);
                var btn = hi.gameObject.AddComponent<Button>();
                btn.targetGraphic = hi;
                btn.transition = Selectable.Transition.None;
                _rowHi[i] = hi;
                _rowBtn[i] = btn;

                // 右鍵處理器（與 Button 左鍵 onClick 並存）：右鍵歌曲列 → 收藏加/刪彈出選單。RenderPage 每頁重接對應歌曲。
                _rowCtx[i] = hi.gameObject.AddComponent<PointerClickProxy>();

                // song name / time / level nudged down 2px to sit centred in the row strip (was at `top`).
                float textTop = top + 2f;
                _rowName[i] = AddRowText("row" + i + "name", NameX, textTop, NameW, ColRow, TextAlignmentOptions.Left);
                _rowTime[i] = AddRowText("row" + i + "time", TimeX, textTop, TimeW, ColRow, TextAlignmentOptions.Center);
                _rowLevel[i] = AddRowText("row" + i + "lvl", LevelX, textTop, LevelW, ColRow, TextAlignmentOptions.Center);

                // NEW badge: the original static new.an "★NEW!" plate, flush at the strip's left edge.
                var nbg = UIKit.AddImage(Root, "row" + i + "newbg", Color.white);
                UIKit.ApplySprite(nbg, newBgSprite);   // sizes to 63×25, hides if missing
                float gw = newBgSprite != null ? newBgSprite.rect.width : BadgeW, gh = newBgSprite != null ? newBgSprite.rect.height : BadgeH;
                Place(nbg.rectTransform, BadgeX - 3f, top - 2, gw, gh);   // NEW 標籤再往左 3px
                nbg.gameObject.SetActive(false);
                _rowNewBg[i] = nbg;

                // NEW badge foreground (animated NEWSIGN.an "New", offset to sit over the backing glow) — DISABLED:
                // the colour-cycling overlay didn't read well over the ★NEW! plate, so we show the static badge only.
                // To re-enable, uncomment this block (SetRowNewActive already toggles _rowNew together with the backing).
                // var nb = UIKit.AddImage(Root, "row" + i + "new", Color.white);
                // Sprite nf0 = _newFrames.Length > 0 ? _newFrames[0] : null;
                // nb.sprite = nf0;
                // if (nf0 == null) nb.color = new Color(1f, 1f, 1f, 0f);
                // float bw = nf0 != null ? nf0.rect.width : BadgeW, bh = nf0 != null ? nf0.rect.height : BadgeH;
                // Place(nb.rectTransform, BadgeX + NewAnimDX, top - 2 + NewAnimDY, bw, bh);
                // if (_newFrames.Length > 1)
                // {
                //     var anim = nb.gameObject.AddComponent<SpriteSeqAnim>();
                //     anim.Frames = _newFrames;
                //     anim.Fps = NewBadgeFps;
                // }
                // nb.gameObject.SetActive(false);
                // _rowNew[i] = nb;
            }
        }

        private TextMeshProUGUI AddRowText(string name, float x, float y, float w, Color col, TextAlignmentOptions align)
        {
            var t = UIKit.AddText(Root, name, "", 14, col, align, false);
            t.fontStyle = FontStyles.Bold;   // 官方歌單字偏粗 → 加粗改善「太細」
            Place(t.rectTransform, x, y, w, RowH);
            return t;
        }

        // Show/hide a row's NEW badge — the animated "New" foreground and its static new.an glow backing together.
        private void SetRowNewActive(int i, bool on)
        {
            if (_rowNew[i] != null) _rowNew[i].gameObject.SetActive(on);
            if (_rowNewBg[i] != null) _rowNewBg[i].gameObject.SetActive(on);
        }

        // Apply a row's strip sprite. The purple SELECTED box (MusicSelDlg73) sits 1px lower than the normal strip
        // (MusicSelDlg95), so re-anchor the rect down 1px while it's shown and back to the strip baseline otherwise.
        private void ApplyRowHi(int i, bool selected)
        {
            var img = _rowHi[i];
            if (img == null) return;
            UIKit.ApplySprite(img, selected ? _hiPushed : _hiNormal);
            float top = RowTop0 + RowPitch * i + 1f;
            img.rectTransform.anchoredPosition = new Vector2(HiX + 2f, -(top + (selected ? 1f : 0f)));
        }

        private void BuildPaging()
        {
            UIKit.AddSpriteButton(Root, "btleft", RoomDlgArt.An("MusicSelDlg96.an"),
                RoomDlgArt.An("MusicSelDlg97.an"), RoomDlgArt.An("MusicSelDlg98.an"), 577, 425)
                .onClick.AddListener(() => ChangePage(-1));
            UIKit.AddSpriteButton(Root, "btright", RoomDlgArt.An("MusicSelDlg99.an"),
                RoomDlgArt.An("MusicSelDlg100.an"), RoomDlgArt.An("MusicSelDlg101.an"), 712, 425)
                .onClick.AddListener(() => ChangePage(1));

            _pageLabel = UIKit.AddText(Root, "pagelabel", "", 14, ColPage, TextAlignmentOptions.Center);
            Place(_pageLabel.rectTransform, 601, 427, 110, 18);   // nudged down 2px to sit centred in the baked slot
        }

        private void BuildSearch()
        {
            _search = UIKit.AddInputField(Root, "SearchBox", L("songselect.search"), 13);   // issue #8 (localized)
            Place(_search.GetComponent<RectTransform>(), 368, 427, 180, 20);
            _search.characterLimit = 32;
            // Search runs on ENTER only (not per-keystroke). onSubmit fires on Enter for a SingleLine field (and
            // NOT on blur, unlike onEndEdit) — then RunSearch re-filters and focuses+previews the top result.
            _search.onSubmit.AddListener(_ => RunSearch());
            // Hide the hint the moment the field gains focus (caret shows); restore it on blur if still empty (#7).
            var ph = _search.placeholder;
            if (ph != null)
            {
                _search.onSelect.AddListener(_ => ph.gameObject.SetActive(false));
                _search.onDeselect.AddListener(_ => ph.gameObject.SetActive(string.IsNullOrEmpty(_search.text)));
            }
        }

        private void BuildInfoBlock()
        {
            // The 演唱者 / BPM labels are BAKED into the dialog art (MusicSelDlg3.an); their rows are centred at
            // y≈315 / y≈333 and the baked colon ends ~x104 — so we draw VALUES ONLY, left-aligned just past it.
            // The 音符數 label is NOT baked, so we add it from lbl_notes.an ("音符数：") as the 3rd row (same teal
            // colour / glyph size as the baked labels), with its value past it too.
            // 值往下 3px（青字視覺基線比烘死標籤偏高 → 下移對齊）：307→310 / 325→328 / 343→346
            _infoArtist = MakeInfo("info_artist", 110, 310);   // value after baked "演唱者："
            _infoBpm = MakeInfo("info_bpm", 110, 328);         // value after baked "BPM："
            _infoNotes = MakeInfo("info_notes", 110, 346);     // value after the lbl_notes label below

            var notesLbl = UIKit.AddImage(Root, "info_notes_lbl", Color.white);
            notesLbl.sprite = RoomDlgArt.An("lbl_notes.an");
            notesLbl.raycastTarget = false;
            if (notesLbl.sprite == null) notesLbl.color = new Color(1f, 1f, 1f, 0f);
            Place(notesLbl.rectTransform, 53, 343, 66, 16);    // 音符數 label x (nudged right to line up with 演唱者/BPM)
        }

        private TextMeshProUGUI MakeInfo(string name, float x, float y)
        {
            var t = UIKit.AddText(Root, name, "", 13, ColInfo, TextAlignmentOptions.Left);
            t.fontStyle = FontStyles.Bold;   // 與歌單同步加粗
            Place(t.rectTransform, x, y, 160, 16);
            return t;
        }

        private void BuildScenePreview()
        {
            // "場景選擇" caption is baked into the dialog background art — no text drawn here.
            _sceneBig = UIKit.AddImage(Root, "scenebigpic", Color.white);
            Place(_sceneBig.rectTransform, 60, 399, 205, 90);   // explicit size (thumbnail stretched to fit)

            _sceneName = UIKit.AddText(Root, "scenename", "", 12, ColCaption, TextAlignmentOptions.Center);
            Place(_sceneName.rectTransform, 96, 494, 136, 16);   // (#8) nudged up ~2px

            UIKit.AddSpriteButton(Root, "scenepre", RoomDlgArt.An("MusicSelDlg96.an"),
                RoomDlgArt.An("MusicSelDlg97.an"), RoomDlgArt.An("MusicSelDlg98.an"), 74, 490)
                .onClick.AddListener(() => SceneStep(-1));
            UIKit.AddSpriteButton(Root, "scenenext", RoomDlgArt.An("MusicSelDlg99.an"),
                RoomDlgArt.An("MusicSelDlg100.an"), RoomDlgArt.An("MusicSelDlg101.an"), 229, 490)
                .onClick.AddListener(() => SceneStep(1));
        }

        private void BuildBottomBar()
        {
            // 模式 / 隊形 / 旁觀人數 (issues #7/#13). The "模式選擇 / 隊形選擇 / 旁觀人數" captions are BAKED INTO the
            // dialog background art (the MusicSelDlg0..8 9-grid from MUSICSELDLG.PNG), so we do NOT draw caption text
            // ourselves — only the green dropdown boxes, placed under the baked captions at the original value slots
            // (offline xml: lblTeamMode x289 / lblFormation x571 / lookernum x661, all y≈484).
            Sprite listMode = RoomDlgArt.An("ShopDlg16.an"), listModeH = RoomDlgArt.An("ShopDlg17.an");   // green list rows (mode)
            Sprite listSm = RoomDlgArt.An("ShopDlg18.an"), listSmH = RoomDlgArt.An("ShopDlg19.an");       // green list rows (formation/looker)
            Sprite arrow = RoomDlgArt.An("MusicSelDlg196.an");   // orange ▲
            const float slotY = 488f, slotH = 22f;

            // Collapsed = value centred in the baked slot + ▲; list is green on expand. Mode VALUE uses the original
            // name slices from LABEL_SDO.an (13 SDO modes, 258×23 each: 自由=frame 0, 普通=frame 1, SHOWTIME=frame 5).
            // The mode slot is the full 258-wide value area (offline lblTeamMode), arrow at btnTeamMode x522.
            // Formation/looker use white text (no name-slice art for those values).
            var sdoModes = RoomDlgArt.AnFrames("LABEL_SDO.an");
            Sprite[] modeSprites = (sdoModes != null && sdoModes.Length >= 6) ? new[] { sdoModes[0], sdoModes[1], sdoModes[5] } : null;
            SdoComboBox.Create(Root, "modeCombo", 289, slotY, 258, slotH, 522, arrow, listMode, listModeH,
                new[] { L("songselect.mode_free"), L("songselect.mode_normal"), L("songselect.mode_showtime") }, modeSprites, Mathf.Clamp(Ctx.Session.GameMode, 0, 2), Color.white, ColComboList, i => Ctx.Session.GameMode = i,
                listAsText: true);   // expanded list = green text rows (like formation/looker); collapsed value keeps the LABEL_SDO sprite
            SdoComboBox.Create(Root, "formCombo", 571, slotY, 56, slotH, 627, arrow, listSm, listSmH,
                new[] { L("songselect.form_basic"), L("songselect.form_fan"), L("songselect.form_ring"), L("songselect.form_random") }, null, Mathf.Clamp(Ctx.Session.Formation, 0, 3), Color.white, ColComboList, i => Ctx.Session.Formation = i);
            SdoComboBox.Create(Root, "lookerCombo", 661, slotY, 56, slotH, 717, arrow, listSm, listSmH,
                LookerOptions(), null, Mathf.Clamp(Ctx.Session.LookerCount, 0, 10), Color.white, ColComboList, i => Ctx.Session.LookerCount = i);
        }

        private static string[] LookerOptions()
        {
            var o = new string[11];
            for (int i = 0; i <= 10; i++) o[i] = i.ToString();
            return o;
        }

        private void BuildActionButtons()
        {
            UIKit.AddSpriteButton(Root, "ok", RoomDlgArt.An("MusicSelDlg64.an"),
                RoomDlgArt.An("MusicSelDlg65.an"), RoomDlgArt.An("MusicSelDlg66.an"), 215, 542)
                .onClick.AddListener(OnConfirm);
            UIKit.AddSpriteButton(Root, "cancel", RoomDlgArt.An("MusicSelDlg67.an"),
                RoomDlgArt.An("MusicSelDlg68.an"), RoomDlgArt.An("MusicSelDlg69.an"), 503, 542)
                .onClick.AddListener(() => CloseTo(ScreenId.Room));
            UIKit.AddSpriteButton(Root, "close", RoomDlgArt.An("MusicSelDlg70.an"),
                RoomDlgArt.An("MusicSelDlg71.an"), RoomDlgArt.An("MusicSelDlg72.an"), 732, 28)
                .onClick.AddListener(() => CloseTo(ScreenId.Room));
        }

        // ---------------- data flow ----------------

        private void SetDifficulty(int d)
        {
            _difficulty = Mathf.Clamp(d, 0, 2);
            RenderDiffTabs();
            RenderPage();
            UpdateInfo();
        }

        private void RenderDiffTabs()
        {
            for (int i = 0; i < 3; i++)
                if (_diffImg[i] != null)
                    UIKit.ApplySprite(_diffImg[i], i == _difficulty ? _diffPushed[i] : _diffNormal[i]);
        }

        private void ApplyFilter()
        {
            _filtered = SongListModel.Filter(CategoryBase(), _search != null ? _search.text : null);
            int maxPage = Mathf.Max(0, (_filtered.Count - 1) / PageSize);
            _page = Mathf.Clamp(_page, 0, maxPage);
            RenderDiffTabs();
            RenderPage();
        }

        // Enter in the search box: apply the current text as the filter, jump to page 0, and focus + preview the
        // top result (Select highlights the row, updates info/disc and starts the preview). Empty result -> NONE disc.
        private void RunSearch()
        {
            _page = 0;
            ApplyFilter();
            if (_filtered.Count > 0) Select(_filtered[0]);
            else { _selected = null; StopPreview(); UpdateInfo(); UpdateDisk(); }
        }

        // The song subset for the active category. 全部 = all; 收藏 = 本機 user 收藏的歌; 最新 = NEW-badge songs;
        // 勁樂/懷舊 unconfigured = empty. (隨機 never reaches here — it renders the range rows instead.)
        private List<SongCatalog.Entry> CategoryBase()
        {
            var all = _model.All;
            var res = new List<SongCatalog.Entry>();
            if (_category == CatAll) { res.AddRange(all); }
            else if (_category == CatFav)
            {
                // 收藏頁：最近加入的排最上面（照收藏加入順序反向），對回歌單條目。
                var byKey = new Dictionary<string, SongCatalog.Entry>();
                foreach (var e in all) { var k = Favorites.Key(e.gn); if (!byKey.ContainsKey(k)) byKey[k] = e; }
                foreach (var k in Favorites.NewestFirst())
                    if (byKey.TryGetValue(k, out var e)) res.Add(e);
            }
            else if (_category == CatNewest) { foreach (var e in all) if (_newIds.Contains(e.fileId)) res.Add(e); }
            return res;
        }

        // The song confirmed last time (Session), if it's in the current list — used to re-focus it on re-entry.
        private SongCatalog.Entry FindPreviousSelection()
        {
            var s = Ctx.Session;
            if (s == null) return null;
            if (s.SongFileId > 0)
                foreach (var e in _filtered) if (e.fileId == s.SongFileId) return e;
            if (!string.IsNullOrEmpty(s.SongGn))
                foreach (var e in _filtered) if (e.gn == s.SongGn) return e;
            return null;
        }

        private void ChangePage(int delta)
        {
            if (_category == CatRandom) return;   // 隨機 rows are a single fixed page
            int pages = Mathf.Max(1, (_filtered.Count + PageSize - 1) / PageSize);
            // remember the focused ROW within the page so paging lands on the same row on the next page.
            int row = _selected != null ? _filtered.IndexOf(_selected) - _page * PageSize : 0;
            if (row < 0 || row >= PageSize) row = 0;
            _page = ((_page + delta) % pages + pages) % pages;
            var slice = PageSlice(_filtered, _page, PageSize);
            if (slice.Count > 0) Select(slice[Mathf.Clamp(row, 0, slice.Count - 1)]);   // re-focus same row (preview + info follow)
            else RenderPage();
        }

        /// <summary>Entries shown on the current page (pure slice — keeps the row-fill testable).</summary>
        public static List<SongCatalog.Entry> PageSlice(IReadOnlyList<SongCatalog.Entry> all, int page, int pageSize)
        {
            var res = new List<SongCatalog.Entry>();
            if (all == null || pageSize <= 0) return res;
            int start = page * pageSize;
            int end = Mathf.Min(start + pageSize, all.Count);
            for (int i = start; i < end; i++) res.Add(all[i]);
            return res;
        }

        private void RenderPage()
        {
            if (_category == CatRandom) { RenderRandomRows(); return; }

            int maxPage = Mathf.Max(0, (_filtered.Count - 1) / PageSize);
            _pageLabel.text = _filtered.Count == 0 ? "0 / 0" : (_page + 1) + " / " + (maxPage + 1);

            var slice = PageSlice(_filtered, _page, PageSize);
            for (int i = 0; i < PageSize; i++)
            {
                bool has = i < slice.Count;
                _rowHi[i].gameObject.SetActive(has);
                _rowName[i].gameObject.SetActive(has);
                _rowTime[i].gameObject.SetActive(has);
                _rowLevel[i].gameObject.SetActive(has);
                _rowBtn[i].onClick.RemoveAllListeners();
                if (_rowCtx[i] != null) _rowCtx[i].Clicked = null;
                if (!has) { SetRowNewActive(i, false); continue; }

                var e = slice[i];
                bool sel = ReferenceEquals(e, _selected);
                ApplyRowHi(i, sel);
                _rowName[i].alignment = TextAlignmentOptions.Left;
                _rowName[i].text = e.title ?? e.gn;
                int lvl = e.Diff(_difficulty);
                _rowLevel[i].text = lvl >= 0 ? lvl.ToString() : "-";
                int dur = e.DurationSec(_difficulty);
                _rowTime[i].text = dur > 0 ? FormatDuration(dur) : "";
                SetRowNewActive(i, _newBadgeArt && _newIds.Contains(e.fileId));
                // 左鍵選歌：先發 SE_0001 再 focus+試聽。（RenderPage 上面 RemoveAllListeners 會連 WrapInWindow 掛的 click SFX 一起清掉 → 這裡補回）
                _rowBtn[i].onClick.AddListener(() => { UiSfx.Play(UiSfx.Click); Select(e); });
                // 右鍵這首歌 → 先 focus+試聽該首（跟左鍵一樣選中並播放），再開收藏加/刪彈出選單（跟著滑鼠）。
                if (_rowCtx[i] != null)
                    _rowCtx[i].Clicked = ev => { if (ev.button == PointerEventData.InputButton.Right) { Select(e); ShowFavPopup(e, ev.position); } };
            }
        }

        // 隨機 mode: the row list shows the difficulty-range options instead of songs; OK then picks a random song.
        private void RenderRandomRows()
        {
            _pageLabel.text = "1 / 1";
            for (int i = 0; i < PageSize; i++)
            {
                bool has = i < RandRanges.Length;
                _rowHi[i].gameObject.SetActive(has);
                _rowName[i].gameObject.SetActive(has);
                _rowTime[i].gameObject.SetActive(false);
                _rowLevel[i].gameObject.SetActive(false);
                SetRowNewActive(i, false);
                _rowBtn[i].onClick.RemoveAllListeners();
                if (_rowCtx[i] != null) _rowCtx[i].Clicked = null;   // 隨機難度列不是歌曲 → 無收藏右鍵
                if (!has) continue;

                ApplyRowHi(i, i == _randRange);
                _rowName[i].alignment = TextAlignmentOptions.Left;   // same left indent as song names
                _rowName[i].text = L(RandRanges[i].Key);
                int idx = i;
                _rowBtn[i].onClick.AddListener(() => SelectRandRange(idx));
            }
        }

        private void SelectRandRange(int i)
        {
            _randRange = Mathf.Clamp(i, 0, RandRanges.Length - 1);
            RenderRandomRows();   // re-highlight the picked range
        }

        // Songs eligible for the current 隨機 range (level at the active difficulty within [min,max]).
        private List<SongCatalog.Entry> RandomPool()
        {
            var r = RandRanges[Mathf.Clamp(_randRange, 0, RandRanges.Length - 1)];
            return SongListModel.InLevelRange(_model.All, _difficulty, r.Min, r.Max);
        }

        private void Select(SongCatalog.Entry e)
        {
            _selected = e;
            RenderPage();
            UpdateInfo();
            if (UpdateDisk()) PlayPreview(e);   // cover present -> preview; NONE disc -> no music
            else StopPreview();
        }

        private void UpdateInfo()
        {
            // Values only — labels are baked into the art (演唱者/BPM) or drawn as the lbl_notes sprite (音符數).
            _infoArtist.text = _selected != null ? (_selected.artist ?? "") : "";
            _infoBpm.text = (_selected != null && _selected.bpm > 0f) ? Mathf.RoundToInt(_selected.bpm).ToString() : "";
            _infoNotes.text = _selected != null ? _selected.NoteCount(_difficulty).ToString() : "";
        }

        // Returns true if a real cover is shown (spins); false for the NONE disc (no cover -> static, no music).
        private bool UpdateDisk()
        {
            var jacket = _selected != null ? SongIcons.Load(_selected.fileId) : null;
            if (jacket != null) { SetDiskJacket(jacket); SetDiskSpinning(true); return true; }   // real cover -> spins
            SetDiskJacket(_iconNone); SetDiskSpinning(false); return false;                       // no cover -> NONE disc, no spin
        }

        // ---------------- scene preview ----------------

        // Scene selector positions: 0 = 隨機 (random); 1.._stages.Count = _stages[pos-1].
        private void SceneStep(int delta)
        {
            int n = _stages.Count + 1;   // +1 for the random slot
            _sceneIndex = ((_sceneIndex + delta) % n + n) % n;
            UpdateScene();
        }

        private void UpdateScene()
        {
            Sprite thumb;
            if (_sceneIndex <= 0)
            {
                if (_sceneName != null) _sceneName.text = L("songselect.random");
                thumb = RoomDlgArt.An("RandomScene.an") ?? RoomDlgArt.An("Scene1.an");
            }
            else
            {
                var stage = _stages[Mathf.Clamp(_sceneIndex - 1, 0, _stages.Count - 1)];
                if (_sceneName != null) _sceneName.text = stage.DisplayName;
                // thumbnail = Scene{sceneId+1}.an (EXE rule), NOT by list position.
                thumb = RoomDlgArt.An("Scene" + (stage.Id + 1) + ".an") ?? RoomDlgArt.An("Scene1.an");
            }
            if (_sceneBig != null)
            {
                _sceneBig.sprite = thumb;
                _sceneBig.color = thumb != null ? Color.white : new Color(1f, 1f, 1f, 0f);
            }
        }

        // ---------------- music preview (exper/<fileId>.ogg) ----------------

        // Start (or replace) the looping preview for the selected song. Cancels any in-flight load first so rapid
        // selection never stacks coroutines or leaves a stale clip playing (debounce on fileId).
        private void PlayPreview(SongCatalog.Entry e)
        {
            if (e == null) return;
            if (e.fileId == _previewId && _preview != null && _preview.isPlaying) return;
            StopPreview();
            _previewId = e.fileId;
            _previewCo = StartCoroutine(LoadPreviewCo(e.fileId, e.gn));
        }

        /// <summary>Full-song ogg name for a chart gn ("sdom2784k.gn" -> "sdom2784.ogg"), matching the on-disk
        /// stem-based main audio; null if the gn isn't an sdom chart.</summary>
        private static string MainOggName(string gn)
        {
            if (string.IsNullOrEmpty(gn)) return null;
            var n = gn.ToLowerInvariant();
            if (n.EndsWith(".gn")) n = n.Substring(0, n.Length - 3);
            if (n.Length > 0 && (n[n.Length - 1] == 'k' || n[n.Length - 1] == 't')) n = n.Substring(0, n.Length - 1);
            return n.Length > 0 ? n + ".ogg" : null;
        }

        // Mirrors ScreenGameplay.LoadAndPlayAudio: file:// + raw path (no URI escaping — the music tree is ASCII and
        // every loader in the repo concatenates raw), AudioType.OGGVORBIS, result==Success guard, GetContent,
        // graceful no-op when the file is missing. Loops at a modest volume so it sits under the UI.
        //
        // Every exper/<fileId>.ogg is a real, pre-decoded preview clip (the official preview .sdm are decoded to
        // valid Vorbis at import time via donor headers — see tools/decode_previews). GetContent is still wrapped
        // in try/catch so that even a malformed file could never throw out of the coroutine (it just no-ops).
        private IEnumerator LoadPreviewCo(int fileId, string gn)
        {
            // Prefer the dedicated exper/<fileId>.ogg preview clip; if none exists, fall back to the FULL song
            // and loop a 20s window from its middle (see Update()).
            string path = Path.Combine(SdoExtracted.MusicDir, "exper", fileId + ".ogg");
            bool isPreviewClip = File.Exists(path);
            if (!isPreviewClip)
            {
                var ogg = MainOggName(gn);
                if (ogg == null) { _previewCo = null; yield break; }
                path = Path.Combine(SdoExtracted.MusicDir, ogg);
                if (!File.Exists(path)) { _previewCo = null; yield break; }
            }

            AudioClip clip = null;
            var req = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.OGGVORBIS);
            yield return req.SendWebRequest();
            if (_previewId != fileId) { req.Dispose(); _previewCo = null; yield break; }   // superseded mid-load
            if (req.result == UnityWebRequest.Result.Success)
            {
                try { clip = DownloadHandlerAudioClip.GetContent(req); }
                catch (System.Exception ex) { clip = null; Debug.LogWarning("[SongSelect] preview decode fail: " + ex.Message); }
            }
            else Debug.LogWarning("[SongSelect] preview load fail: " + req.error);
            req.Dispose();

            // race guard: selection changed (or hidden) while loading -> drop the stale clip.
            if (clip == null || _previewId != fileId) { _previewCo = null; yield break; }

            // Hold until the entry gate passes (~1s after OnShow) so music starts only once the open spin settles.
            // After that first second the gate is in the past, so later selections play immediately.
            while (Time.unscaledTime < _previewGateTime)
            {
                if (_previewId != fileId) { _previewCo = null; yield break; }   // superseded while waiting
                yield return null;
            }
            _previewCo = null;

            EnsurePreviewSource();
            _preview.clip = clip;
            if (isPreviewClip)
            {
                _previewWindow = false;
                _preview.loop = true;         // short preview clip: loop the whole thing
                _preview.time = 0f;
                _preview.Play();
            }
            else
            {
                // No preview clip -> loop a 20s window centred in the full song. Update() bounces time
                // back to the window start (AudioSource.loop only loops the whole clip, not a sub-range).
                float len = clip.length;
                float win = Mathf.Min(PreviewWindowSec, len);
                float start = Mathf.Clamp(len * 0.5f - win * 0.5f, 0f, Mathf.Max(0f, len - win));
                _previewWinStart = start;
                _previewWinEnd = start + win;
                _previewWindow = true;
                _preview.loop = false;
                _preview.time = start;
                _preview.Play();
            }
        }

        private void EnsurePreviewSource()
        {
            if (_preview != null) return;
            // Audio needs a listener; gameplay relies on the scene's. If the front-end has none yet, add one here
            // (only when truly absent, to avoid Unity's "multiple audio listeners" warning).
            if (FindAnyObjectByType<AudioListener>() == null) gameObject.AddComponent<AudioListener>();
            _preview = gameObject.AddComponent<AudioSource>();
            _preview.playOnAwake = false;
            _preview.loop = true;
            _preview.volume = PreviewVolume;
            _preview.spatialBlend = 0f;
        }

        private void StopPreview()
        {
            if (_previewCo != null) { StopCoroutine(_previewCo); _previewCo = null; }
            _previewId = -1;
            _previewWindow = false;
            if (_preview != null) { _preview.Stop(); _preview.clip = null; }
        }

        // ---------------- confirm ----------------

        private void OnConfirm()
        {
            // 隨機 mode: pick a random song from the selected difficulty-range pool.
            if (_category == CatRandom && _selected == null)
            {
                var pool = RandomPool();
                if (pool.Count == 0) { Toast.Show(L("songselect.need_pick")); return; }
                _selected = pool[Random.Range(0, pool.Count)];
            }
            if (_selected == null) { Toast.Show(L("songselect.need_pick")); return; }
            var s = Ctx.Session;
            s.SongGn = _selected.gn;
            s.SongFileId = _selected.fileId;
            s.SongTitle = _selected.title ?? _selected.gn;
            s.SongArtist = _selected.artist;
            s.Difficulty = (Difficulty)_difficulty;
            // scene: slot 0 = random -> pick an actual scene now; else the chosen stage.
            bool randomScene = _sceneIndex <= 0 && _stages.Count > 0;
            var stage = randomScene
                ? _stages[Random.Range(0, _stages.Count)]
                : _stages[Mathf.Clamp(_sceneIndex - 1, 0, _stages.Count - 1)];
            s.StageId = stage.Id;
            s.StageFolder = stage.Folder;
            s.StageRandom = randomScene;   // 房間第二層圖：選隨機就顯示 RANDOM，選具體場景就顯示該場景縮圖
            RoomConfig.defaultScene = randomScene ? -1 : stage.Id;   // 持久化：玩家選的場景寫回 config.ini（隨機→-1；刪檔→回隨機）
            RoomConfig.Save();
            // mode/formation/looker are written live by the dropdown callbacks.

            Ctx.Rooms.SetSong(s.SongTitle);
            CloseTo(ScreenId.Room);
        }

        // ---------------- 收藏右鍵彈出選單 ----------------

        // 右鍵歌曲列 → 單鈕選單「添加收藏夹 / 从收藏夹删除」(原版 MUSICPOP)：位置跟著滑鼠、夾在畫面內；按下切換收藏
        // 並收起，點選單外任一處(全螢幕 overlay)也收起。收藏狀態存在 active user 的 favorites.json。
        private void ShowFavPopup(SongCatalog.Entry e, Vector2 screenPos)
        {
            CloseFavPopup();
            if (e == null || string.IsNullOrEmpty(e.gn)) return;
            bool isFav = Favorites.IsFav(e.gn);
            Sprite n = RoomDlgArt.An(isFav ? "MusicSelDlg127.an" : "MusicSelDlg124.an");   // del / add 常態
            Sprite h = RoomDlgArt.An(isFav ? "MusicSelDlg128.an" : "MusicSelDlg125.an");   // hover
            Sprite p = RoomDlgArt.An(isFav ? "MusicSelDlg129.an" : "MusicSelDlg126.an");   // pushed
            float w = n != null ? n.rect.width : 94f, ht = n != null ? n.rect.height : 30f;

            // 位置：滑鼠螢幕座標 → 800×600 設計座標(左上、y-down)，夾進畫面。
            Vector2 tl = ScreenToDesign(screenPos);
            float x = Mathf.Clamp(tl.x, 0f, 800f - w);
            float y = Mathf.Clamp(tl.y, 0f, 600f - ht);
            var btn = UIKit.AddSpriteButton(Root, "FavBtn", n, h, p, x, y);
            _favPopup = btn.gameObject;
            _favPopup.transform.SetAsLastSibling();
            _favPopupFrame = Time.frameCount;
            UiSfx.AttachClick(btn);      // 按下 添加/刪除收藏夾 → SE_0001
            UiHoverSfx.Attach(btn);      // 滑過 添加/刪除收藏夾 → Menufloat
            var entry = e;
            btn.onClick.AddListener(() =>
            {
                Favorites.Toggle(entry.gn);
                CloseFavPopup();
                if (_category == CatFav) RefreshFavCategory();   // 收藏頁下移除 → 該列消失
                else RenderPage();
            });
        }

        private void CloseFavPopup()
        {
            if (_favPopup != null) { Destroy(_favPopup); _favPopup = null; }
        }

        // 收藏選單開著時：點在選單「外」就關掉它。不吃事件（沒有 overlay 擋著）→ 底下的歌曲列照樣收到那個點擊，
        // 所以右鍵另一首會直接關舊選單並換到那首（Select + 重開選單）；左鍵另一首也會換過去並關選單。
        private void Update()
        {
            // Keep the fallback preview (full-song middle) looping within its 20s window.
            if (_previewWindow && _preview != null && _preview.clip != null)
            {
                if (!_preview.isPlaying || _preview.time >= _previewWinEnd)
                {
                    _preview.time = _previewWinStart;
                    if (!_preview.isPlaying) _preview.Play();
                }
            }

            if (_favPopup == null) return;
            if (Time.frameCount == _favPopupFrame) return;   // 剛開的那一幀不判關，避免自我關閉
            if (!Input.GetMouseButtonDown(0) && !Input.GetMouseButtonDown(1)) return;
            var rt = (RectTransform)_favPopup.transform;
            if (!RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition, _uiCam))
                CloseFavPopup();   // 點到選單按鈕本身則不關（交給按鈕的 onClick 切換收藏後自行收起）
        }

        // 收藏頁在加/刪後重建清單：保留當前選取(還在的話)，否則選第一首；空了就清資訊/唱片。
        private void RefreshFavCategory()
        {
            var keep = _selected;
            ApplyFilter();
            if (keep != null && _filtered.Contains(keep)) { _page = _filtered.IndexOf(keep) / PageSize; Select(keep); }
            else if (_filtered.Count > 0) { _page = 0; Select(_filtered[0]); }
            else { _selected = null; StopPreview(); UpdateInfo(); UpdateDisk(); RenderPage(); }
        }

        // 滑鼠螢幕座標 → 800×600 設計座標(左上原點、y 往下)。用世界空間 canvas 的相機做反投影（與 Place 的 (x,-y) 慣例一致）。
        private Vector2 ScreenToDesign(Vector2 screenPos)
        {
            if (_uiCam == null) { var c = GetComponentInParent<Canvas>(); _uiCam = c != null ? c.worldCamera : null; }
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(Root, screenPos, _uiCam, out var lp))
            {
                var r = Root.rect;
                return new Vector2(lp.x - r.xMin, r.yMax - lp.y);
            }
            return Vector2.zero;
        }

        // ---------------- small utils ----------------

        /// <summary>Place a rect at MusicSelDlg.xml top-left pixel coords (y-down) at fixed w×h.</summary>
        private void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, -y);
        }

        private static string FormatDuration(int sec) => (sec / 60) + ":" + (sec % 60).ToString("00");

        /// <summary>0xAARRGGBB -> Color.</summary>
        private static Color FromArgb(uint argb)
        {
            return new Color(
                ((argb >> 16) & 0xFF) / 255f,
                ((argb >> 8) & 0xFF) / 255f,
                (argb & 0xFF) / 255f,
                ((argb >> 24) & 0xFF) / 255f);
        }
    }
}
