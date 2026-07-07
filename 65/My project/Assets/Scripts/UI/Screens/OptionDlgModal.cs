using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Sdo.Localization;
using Sdo.Settings;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>
    /// OPTION dialog — a faithful rebuild of the original OPTIONDLG on the exact official pink frame
    /// (<see cref="OptionDlgArt"/>, baked Chinese painted out) with all text overlaid as dynamic localized TMP.
    /// Three tabs: 畫面 (resolution / display mode / vsync / language, from the real DisplaySettings), 音效
    /// (three volume sliders), 鍵盤 (faithful 4-key rebinding — primary + auxiliary — persisted to
    /// <see cref="KeyBindSettings"/> and consumed by ScreenGameplay). Replaces the old plain SettingsModal.
    /// Coordinates follow OPTIONDLG.XML's 800×600 layout (top-left origin, y-down → AddSprite maps it).
    /// </summary>
    public sealed class OptionDlgModal : MonoBehaviour
    {
        // ---- palette (dark text reads on the light sunken board; white on the magenta pills/frame) ----
        private static readonly Color BtnText = Color.white;
        private static readonly Color Header = new Color32(0xC0, 0x36, 0x86, 0xFF); // section headers (matches the original magenta pills)
        private static readonly Color LabelC = new Color32(0x7A, 0x2C, 0x5C, 0xFF); // row labels
        private static readonly Color ValueC = new Color32(0x53, 0x2A, 0x48, 0xFF); // selector/percent values
        private static readonly Color KeyText = new Color32(0x00, 0x00, 0xB0, 0xFF);   // blue text fallback for keys with no glyph (matches the baked glyph blue)

        private static readonly string[] ModeIds = { "Windowed", "Fullscreen", "Borderless" };

        private CanvasGroup _cg;
        private RectTransform _window; private CanvasGroup _windowCg; private WindowAnim _anim; // ROOMDLG-style spin-zoom in/out
        private RectTransform _videoTab, _audioTab, _keyTab;
        private MiniSelect _resSel, _modeSel, _langSel;
        private Image _vsyncDot; private TextMeshProUGUI _vsyncTxt;
        private Slider _sBgm, _sMusic, _sSfx;
        private Image _audioBoard;
        private readonly TextMeshProUGUI[,] _capLabel = new TextMeshProUGUI[2, 4]; // [slot 0=primary/1=aux, lane] — text fallback for glyphless keys
        private readonly Image[,] _capImg = new Image[2, 4];                        // key-cap chip sprite (purple idle / silver capturing)
        private readonly Image[,] _capGlyph = new Image[2, 4];                      // bound-key letter glyph (LOBBYDLG/KEYS/*.PNG)
        private Image _board;                                                       // generic board (hidden on the keyboard tab so the baked 4-key board shows)

        // working copy (committed to settings on Save)
        private int _resIndex, _modeIndex; private bool _vsync;
        private float _bgm, _music, _sfx;
        private Language _lang, _entryLang; private bool _applied;
        private readonly string[] _prim = new string[4];
        private readonly string[] _aux = new string[4];
        private int _capSlot = -1, _capLane = -1;                   // active key-capture target (-1 = none)

        private static string L(string k) => LocalizationManager.Get(k);

        // ---------------------------------------------------------------- build
        public void Build(RectTransform parent)
        {
            var root = UIKit.NewRect(parent, "OptionDlgModal");
            UIKit.Stretch(root);
            _cg = root.gameObject.AddComponent<CanvasGroup>();

            var dim = UIKit.AddImage(root, "Dim", new Color(0, 0, 0, 0.55f), true);
            UIKit.Stretch(dim.rectTransform);

            // official pink frame (4 quadrants, title text already removed in the clean atlas)
            UIKit.AddSprite(root, "FrameTL", OptionDlgArt.FrameTL, 220, 128);
            UIKit.AddSprite(root, "FrameTR", OptionDlgArt.FrameTR, 476, 128);
            UIKit.AddSprite(root, "FrameBL", OptionDlgArt.FrameBL, 220, 384);
            UIKit.AddSprite(root, "FrameBR", OptionDlgArt.FrameBR, 476, 384);

            // title "選項 OPTION" is baked into the frame art — no TMP overlay

            // close (X)
            var close = UIKit.AddSpriteButton(root, "Close", OptionDlgArt.CloseN, OptionDlgArt.CloseN, OptionDlgArt.CloseP, 561, 134);
            close.onClick.AddListener(Close);

            // tabs (reuse a cleaned button pill as the chip; active = full colour, inactive = dimmed).
            // Visual left→right = 鍵盤 | 音效 | 畫面; tabX[i] places tab index i (0=video,1=audio,2=keyboard) at
            // its screen x while indices keep their meaning (ShowTab / boards / bodies unchanged).
            _tabBtns = new Image[3];
            string[] tabKeys = { "option.tab.video", "option.tab.audio", "option.tab.keyboard" };
            float[] tabX = { 456f, 351f, 246f }; // video→right, audio→middle, keyboard→left
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                var b = UIKit.AddSpriteButton(root, "Tab" + i, OptionDlgArt.SaveN, OptionDlgArt.SaveN, OptionDlgArt.SaveP, tabX[i], 182);
                _tabBtns[i] = b.targetGraphic as Image;
                var lab = UIKit.AddLocText(b.transform, "L", tabKeys[i], 15, BtnText, TextAlignmentOptions.Center);
                UIKit.Stretch(lab);
                b.onClick.AddListener(() => ShowTab(idx));
            }

            // per-tab boards (only one shown at a time): 畫面 uses the empty OptScreenBoard + TMP labels;
            // 音效 uses OptVolumeBoard which has its labels + MIN…MAX tracks baked in; 鍵盤 shows neither
            // (the 4-key board is baked into the frame art).
            _board = UIKit.AddSprite(root, "Board", OptionDlgArt.Board, 236, 225);
            _audioBoard = UIKit.AddSprite(root, "AudioBoard", OptionDlgArt.AudioBoard, 236, 225);

            _videoTab = TabBody(root, "VideoTab");
            _audioTab = TabBody(root, "AudioTab");
            _keyTab = TabBody(root, "KeyTab");
            BuildVideo(_videoTab);
            BuildAudio(_audioTab);
            BuildKeyboard(_keyTab);

            // action buttons — text is baked into the button art (保存/退出/默認設置, both states), so no TMP label
            SpriteBtn(root, "Default", OptionDlgArt.DefaultN, OptionDlgArt.DefaultP, 261, 448, ResetDefaults);
            SpriteBtn(root, "Save", OptionDlgArt.SaveN, OptionDlgArt.SaveP, 368, 448, Apply);
            SpriteBtn(root, "Exit", OptionDlgArt.ExitN, OptionDlgArt.ExitP, 475, 448, Close);

            // Wrap everything except the dim into a centred window so open/close spin-zoom the dialog exactly like the
            // song-select (MUSICSELDLG) UI (WindowAnim + Frameround whoosh). The dim stays full-screen behind it.
            _window = UIKit.NewRect(root, "Window");
            UIKit.Stretch(_window);
            _window.pivot = new Vector2(0.5f, 0.5f);
            _windowCg = _window.gameObject.AddComponent<CanvasGroup>();
            _anim = _window.gameObject.AddComponent<WindowAnim>();
            var kids = new List<Transform>();
            foreach (Transform c in root)
                if (c != (Transform)_window && c.gameObject != dim.gameObject) kids.Add(c);
            foreach (var c in kids) c.SetParent(_window, false);

            SetVisible(false);
        }

        private Image[] _tabBtns;

        private RectTransform TabBody(RectTransform root, string name)
        {
            var rt = UIKit.NewRect(root, name);
            UIKit.Stretch(rt);   // full 800×600 overlay; children are placed at absolute XML coords
            return rt;
        }

        // ---------------------------------------------------------------- 畫面 tab
        private void BuildVideo(RectTransform b)
        {
            var resNames = new string[ResolutionPreset.Presets.Length];
            for (int i = 0; i < resNames.Length; i++) resNames[i] = ResolutionPreset.Presets[i].ToString();

            LocLabel(b, "L0", "settings.resolution", 262, 254, 120, 24, 15, LabelC, TextAlignmentOptions.Left);
            _resSel = Selector(b, 386, 252, 180, resNames, 0, i => _resIndex = i);

            LocLabel(b, "L1", "settings.display_mode", 262, 292, 120, 24, 15, LabelC, TextAlignmentOptions.Left);
            _modeSel = Selector(b, 386, 290, 180,
                new[] { L("display.windowed"), L("display.fullscreen"), L("display.borderless") }, 0, i => _modeIndex = i);

            LocLabel(b, "L2", "settings.vsync", 262, 330, 120, 24, 15, LabelC, TextAlignmentOptions.Left);
            _vsyncDot = AddToggle(b, 390, 331, () => { _vsync = !_vsync; RefreshVSync(); });
            _vsyncTxt = Label(b, "VSyncTxt", "", 414, 330, 90, 24, 15, ValueC, TextAlignmentOptions.Left);

            LocLabel(b, "L3", "settings.language", 262, 368, 120, 24, 15, LabelC, TextAlignmentOptions.Left);
            _langSel = Selector(b, 386, 366, 180,
                new[] { "繁體中文", "简体中文", "English", "日本語" }, 0, i =>
                {
                    _lang = IndexToLang(i);
                    LocalizationManager.SetLanguage(_lang);           // live preview
                });
        }

        // ---------------------------------------------------------------- 音效 tab
        // Labels (背景音樂/遊戲音樂/遊戲音效) and MIN…MAX tracks are baked into OptVolumeBoard, so we only drop a
        // bare handle-slider onto each baked line. Baked line spans screen x 298..519; MIN text ends ~291, MAX
        // starts ~525. The handle centre range is 315..503 (extended 5px each side per user tuning).
        // Cy = line centre + 3 (the handle sprite's pill sits ~3px above its box centre) so it rides the line.
        private const float AudTrackX = 315f, AudTrackW = 188f;
        private static readonly float[] AudTrackCy = { 282.5f, 339.5f, 396.5f };

        private void BuildAudio(RectTransform b)
        {
            _sBgm = AddSlider(b, 0, v => _bgm = v);
            _sMusic = AddSlider(b, 1, v => _music = v);
            _sSfx = AddSlider(b, 2, v => _sfx = v);
        }

        // ---------------------------------------------------------------- 鍵盤 tab (4-key only)
        // Faithful to OPTIONDLG.XML Win4key: the 主鍵位設置/輔助鍵位 headers, the 8 colour arrows and the note
        // text are BAKED into the official frame art (revealed here by hiding the generic board). We overlay only
        // the single "4鍵" sub-tab chip and the 8 clickable key caps at the exact k4/k42 pixel coords, each showing
        // its bound key as text (the original draws the char too — there is no letter/number art in the atlas).
        private static readonly float[] PrimX = { 261f, 298f, 335f, 372f }; // k4-1..4   (主鍵位, top-left px)
        private static readonly float[] AuxX = { 415f, 452f, 489f, 526f };  // k42-1..4  (輔助鍵位, top-left px)
        private const float PrimY = 288f, AuxY = 289f;

        private void BuildKeyboard(RectTransform b)
        {
            // sub-tab bar: 6鍵/激鼓 shown for fidelity but INERT (non-interactive sprites, no click handler);
            // the active 4鍵 chip is drawn last so it sits on top of its neighbours (all at 250,230).
            UIKit.AddSprite(b, "TabDrum", OptionDlgArt.DrumTab, 250, 230);
            UIKit.AddSprite(b, "TabSix", OptionDlgArt.SixKeyTab, 250, 230);
            UIKit.AddSprite(b, "Tab4Key", OptionDlgArt.FourKeyTab, 250, 230);
            for (int lane = 0; lane < 4; lane++) KeyCap(b, 0, lane, PrimX[lane], PrimY);
            for (int lane = 0; lane < 4; lane++) KeyCap(b, 1, lane, AuxX[lane], AuxY);
        }

        private void KeyCap(RectTransform b, int slot, int lane, float x, float y)
        {
            var img = UIKit.AddSprite(b, "cap" + slot + "_" + lane, OptionDlgArt.KeyCapNormal, x, y, raycast: true);
            int s = slot, ln = lane;
            // Select on pointer-DOWN (turns purple immediately, stays purple while held / capturing). No Button →
            // no ColorTint darkening on press, and the cap can't grab a key press (Space/Enter) during capture.
            var trigger = img.gameObject.AddComponent<EventTrigger>();
            var down = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            down.callback.AddListener(_ => BeginCapture(s, ln));
            trigger.triggers.Add(down);

            // bound-key letter: a 27×36 blue/white-outline glyph PNG centred on the chip (official look).
            var glyph = UIKit.AddImage(img.transform, "g", new Color(1f, 1f, 1f, 0f));
            var grt = glyph.rectTransform;
            grt.anchorMin = grt.anchorMax = grt.pivot = new Vector2(0.5f, 0.5f);
            grt.sizeDelta = new Vector2(27f, 36f);
            grt.anchoredPosition = new Vector2(0f, -1f);   // matches the original's slight downward nudge

            // text fallback (drawn only for keys with no glyph, e.g. a hand-edited binding)
            var lab = UIKit.AddText(img.transform, "k", "", 16, KeyText, TextAlignmentOptions.Center);
            UIKit.Stretch(lab);

            _capImg[slot, lane] = img;
            _capGlyph[slot, lane] = glyph;
            _capLabel[slot, lane] = lab;
        }

        // ---------------------------------------------------------------- key capture (Update)
        private static readonly KeyCode[] Bindable = BuildBindable();

        // Only keys with a glyph in LOBBYDLG/KEYS are bindable — matches the original, whose scan-code→glyph
        // switch (FUN_00461170) rejects (returns 0 for) anything it can't draw (Shift/Ctrl/Enter/…).
        private static KeyCode[] BuildBindable()
        {
            var l = new List<KeyCode>();
            for (var k = KeyCode.A; k <= KeyCode.Z; k++) l.Add(k);
            for (var k = KeyCode.Alpha0; k <= KeyCode.Alpha9; k++) l.Add(k);
            for (var k = KeyCode.Keypad0; k <= KeyCode.Keypad9; k++) l.Add(k);
            l.AddRange(new[] { KeyCode.LeftArrow, KeyCode.RightArrow, KeyCode.UpArrow, KeyCode.DownArrow,
                KeyCode.Space, KeyCode.Comma, KeyCode.Period, KeyCode.Slash, KeyCode.Semicolon, KeyCode.Quote,
                KeyCode.LeftBracket, KeyCode.RightBracket,
                KeyCode.Home, KeyCode.End, KeyCode.PageUp, KeyCode.PageDown, KeyCode.Insert, KeyCode.Delete });
            return l.ToArray();
        }

        private void BeginCapture(int slot, int lane)
        {
            CancelCapture();
            _capSlot = slot; _capLane = lane;
            // selected → purple chip; keep the current letter showing (it swaps only when a new key is bound)
            if (_capImg[slot, lane] != null) UIKit.ApplySprite(_capImg[slot, lane], OptionDlgArt.KeyCapCapturing);
        }

        private void CancelCapture()
        {
            if (_capSlot >= 0) RefreshCap(_capSlot, _capLane);
            _capSlot = _capLane = -1;
        }

        private void Update()
        {
            if (_cg == null || _cg.alpha < 0.5f || _capSlot < 0) return;
            if (Input.GetKeyDown(KeyCode.Escape)) { CancelCapture(); return; }
            foreach (var k in Bindable)
            {
                if (!Input.GetKeyDown(k)) continue;
                (_capSlot == 0 ? _prim : _aux)[_capLane] = k.ToString();
                int slot = _capSlot, lane = _capLane;
                _capSlot = _capLane = -1;
                RefreshCap(slot, lane);
                break;
            }
        }

        // ---------------------------------------------------------------- open / apply / defaults / close
        public void Open()
        {
            var s = DisplaySettingsManager.Settings;
            _resIndex = Mathf.Max(0, ResolutionPreset.IndexOf(s.display.width, s.display.height));
            _modeIndex = Array.IndexOf(ModeIds, s.display.displayMode); if (_modeIndex < 0) _modeIndex = 0;
            _vsync = s.display.vsync;
            _bgm = s.audio.bgm; _music = s.audio.gameMusic; _sfx = s.audio.sfx;
            _entryLang = LocalizationManager.Current; _lang = _entryLang; _applied = false;
            s.keys ??= new KeyBindSettings();
            Array.Copy(KeyBindSettings.SanitizeNames(s.keys.lane4, KeyBindSettings.DefaultPrimary), _prim, 4);
            Array.Copy(KeyBindSettings.SanitizeNames(s.keys.lane4aux, KeyBindSettings.DefaultAux), _aux, 4);
            CancelCapture();

            _resSel.Set(_resIndex, false);
            _modeSel.Set(_modeIndex, false);
            _langSel.Set(LangToIndex(_lang), false);
            RefreshVSync();
            if (_sBgm) _sBgm.value = _bgm;
            if (_sMusic) _sMusic.value = _music;
            if (_sSfx) _sSfx.value = _sfx;
            RefreshAllCaps();

            ShowTab(1);   // open on the 音效 (audio) page by default
            SetVisible(true);
            if (_windowCg != null) _windowCg.blocksRaycasts = true;
            if (_anim != null) { _anim.ResetOpen(); _anim.PlayIn(); }   // spin-zoom in (same as song-select)
            UiSfx.Play(UiSfx.FrameRound);
        }

        /// <summary>Spin-zoom the dialog out (song-select style), then hide it (dim included).</summary>
        private void AnimatedHide()
        {
            if (_anim == null) { SetVisible(false); return; }
            if (_windowCg != null) _windowCg.blocksRaycasts = false;   // freeze input during the fade
            UiSfx.Play(UiSfx.FrameRound);
            _anim.PlayOut(() => SetVisible(false));
        }

        private void Apply()
        {
            var s = DisplaySettingsManager.Settings;
            var res = ResolutionPreset.Presets[Mathf.Clamp(_resIndex, 0, ResolutionPreset.Presets.Length - 1)];
            s.display.width = res.Width; s.display.height = res.Height;
            s.display.displayMode = ModeIds[Mathf.Clamp(_modeIndex, 0, ModeIds.Length - 1)];
            s.display.vsync = _vsync;
            s.audio.bgm = _bgm; s.audio.gameMusic = _music; s.audio.sfx = _sfx;
            s.language = LanguageInfo.Code(_lang);
            s.keys ??= new KeyBindSettings();
            s.keys.lane4 = (string[])_prim.Clone();
            s.keys.lane4aux = (string[])_aux.Clone();

            DisplaySettingsManager.Save();
            DisplaySettingsManager.ApplyDisplay();
            AudioListener.volume = _music;
            _applied = true; _entryLang = _lang;
            AnimatedHide();
        }

        private void ResetDefaults()
        {
            var d = new GameSettings();                                  // fresh defaults
            _resIndex = Mathf.Max(0, ResolutionPreset.IndexOf(d.display.width, d.display.height));
            _modeIndex = Array.IndexOf(ModeIds, d.display.displayMode); if (_modeIndex < 0) _modeIndex = 0;
            _vsync = d.display.vsync; _bgm = d.audio.bgm; _music = d.audio.gameMusic; _sfx = d.audio.sfx;
            Array.Copy(d.keys.lane4, _prim, 4);
            Array.Copy(d.keys.lane4aux, _aux, 4);
            CancelCapture();

            _resSel.Set(_resIndex, false);
            _modeSel.Set(_modeIndex, false);
            RefreshVSync();
            if (_sBgm) _sBgm.value = _bgm;
            if (_sMusic) _sMusic.value = _music;
            if (_sSfx) _sSfx.value = _sfx;
            RefreshAllCaps();
        }

        private void Close()
        {
            CancelCapture();
            if (!_applied && LocalizationManager.Current != _entryLang)
                LocalizationManager.SetLanguage(_entryLang);            // revert live language preview
            AnimatedHide();
        }

        private void ShowTab(int i)
        {
            _videoTab.gameObject.SetActive(i == 0);
            _audioTab.gameObject.SetActive(i == 1);
            _keyTab.gameObject.SetActive(i == 2);
            if (_board != null) _board.gameObject.SetActive(i == 0);       // 畫面 board
            if (_audioBoard != null) _audioBoard.gameObject.SetActive(i == 1); // 音效 board (baked labels); 鍵盤 shows the frame's baked 4-key board
            for (int t = 0; t < 3; t++)
                if (_tabBtns[t] != null) _tabBtns[t].color = (t == i) ? Color.white : new Color(1, 1, 1, 0.55f);
        }

        private void SetVisible(bool on)
        {
            _cg.alpha = on ? 1f : 0f;
            _cg.interactable = on;
            _cg.blocksRaycasts = on;
        }

        // ---------------------------------------------------------------- widget refreshers
        private void RefreshVSync()
        {
            if (_vsyncDot != null) _vsyncDot.sprite = _vsync ? OptionDlgArt.RadioOn : OptionDlgArt.RadioOff;
            if (_vsyncTxt != null) _vsyncTxt.text = L(_vsync ? "common.on" : "common.off");
        }

        private void RefreshAllCaps()
        {
            for (int s = 0; s < 2; s++) for (int l = 0; l < 4; l++) RefreshCap(s, l);
        }

        private void RefreshCap(int slot, int lane)
        {
            if (_capImg[slot, lane] != null) UIKit.ApplySprite(_capImg[slot, lane], OptionDlgArt.KeyCapNormal); // back to purple idle
            var name = (slot == 0 ? _prim : _aux)[lane];
            var glyph = KeysArt.Glyph(name);
            if (_capGlyph[slot, lane] != null)
            {
                _capGlyph[slot, lane].sprite = glyph;
                _capGlyph[slot, lane].color = glyph != null ? Color.white : new Color(1f, 1f, 1f, 0f);
            }
            if (_capLabel[slot, lane] != null)
                _capLabel[slot, lane].text = glyph != null ? "" : ShortKeyName(name); // text only when no glyph exists
        }

        // ---------------------------------------------------------------- builders / helpers
        private void SpriteBtn(RectTransform p, string name, Sprite n, Sprite pushed, float x, float y, Action onClick)
        {
            var b = UIKit.AddSpriteButton(p, name, n, n, pushed, x, y);
            b.onClick.AddListener(() => onClick());
        }

        private Image AddToggle(RectTransform p, float x, float y, Action onClick)
        {
            var img = UIKit.AddSprite(p, "toggle", OptionDlgArt.RadioOff, x, y, raycast: true);
            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => onClick());
            return img;
        }

        /// <summary>A bare handle-slider for the 音效 tab: the MIN…MAX track is baked into OptVolumeBoard, so the
        /// track/fill are invisible (baked line shows through) and only the official handle sprite rides the value.
        /// <paramref name="row"/> indexes <see cref="AudTrackCy"/> (handle vertical centre, screen y).</summary>
        private Slider AddSlider(RectTransform p, int row, Action<float> onChange)
        {
            const float H = 20f;
            float cy = AudTrackCy[row];
            var rt = UIKit.NewRect(p, "slider" + row);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(AudTrackX, -(cy - H / 2f)); rt.sizeDelta = new Vector2(AudTrackW, H);
            var bg = rt.gameObject.AddComponent<Image>(); bg.color = new Color(1f, 1f, 1f, 0f); bg.raycastTarget = true; // invisible drag area
            var slider = rt.gameObject.AddComponent<Slider>();

            var fillArea = UIKit.NewRect(rt, "FillArea");
            fillArea.anchorMin = Vector2.zero; fillArea.anchorMax = Vector2.one;
            fillArea.offsetMin = new Vector2(1, 1); fillArea.offsetMax = new Vector2(-1, -1);
            var fill = UIKit.AddImage(fillArea, "Fill", new Color(1f, 1f, 1f, 0f)).rectTransform; // invisible; drives the handle position
            fill.anchorMin = Vector2.zero; fill.anchorMax = Vector2.one; fill.sizeDelta = Vector2.zero;

            // official handle sprite parented to the fill's right edge → follows the value, riding the baked line
            var handle = UIKit.AddImage(fill, "Handle", Color.white); handle.sprite = OptionDlgArt.SliderHandle; handle.raycastTarget = false;
            handle.rectTransform.anchorMin = handle.rectTransform.anchorMax = new Vector2(1f, 0.5f);
            handle.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            handle.rectTransform.sizeDelta = OptionDlgArt.SliderHandle != null ? OptionDlgArt.SliderHandle.rect.size : new Vector2(43, 23);
            handle.rectTransform.anchoredPosition = Vector2.zero;

            slider.fillRect = fill; slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0; slider.maxValue = 1; slider.targetGraphic = bg;
            slider.onValueChanged.AddListener(v => onChange(v));
            return slider;
        }

        private MiniSelect Selector(RectTransform p, float x, float y, float w, string[] opts, int start, Action<int> onChange)
        {
            var box = UIKit.NewRect(p, "sel");
            box.anchorMin = box.anchorMax = new Vector2(0f, 1f); box.pivot = new Vector2(0f, 1f);
            box.anchoredPosition = new Vector2(x, -y); box.sizeDelta = new Vector2(w, 26f);

            var val = UIKit.AddText(box, "Val", "", 15, ValueC, TextAlignmentOptions.Center);
            UIKit.Stretch(val, 26, 0, 26, 0);

            var prev = ArrowButton(box, "Prev", "◀", true);
            var next = ArrowButton(box, "Next", "▶", false);

            var ms = new MiniSelect(opts, val, onChange);
            prev.onClick.AddListener(() => ms.Step(-1));
            next.onClick.AddListener(() => ms.Step(1));
            ms.Set(start, false);
            return ms;
        }

        private Button ArrowButton(RectTransform box, string name, string glyph, bool left)
        {
            var b = UIKit.AddButton(box, name, out var lbl, new Color(1, 1, 1, 0f), Header, 16);
            lbl.text = glyph;
            var rt = b.GetComponent<RectTransform>();
            UIKit.Anchor(rt, new Vector2(left ? 0 : 1, 0), new Vector2(left ? 0 : 1, 1), new Vector2(left ? 0 : 1, 0.5f));
            rt.sizeDelta = new Vector2(26f, 0f);
            rt.anchoredPosition = Vector2.zero;
            return b;
        }

        private TextMeshProUGUI Label(RectTransform p, string name, string txt, float x, float y, float w, float h,
            float size, Color col, TextAlignmentOptions align)
        {
            var t = UIKit.AddText(p, name, txt, size, col, align);
            PlaceTL(t.rectTransform, x, y, w, h);
            return t;
        }

        private TextMeshProUGUI LocLabel(RectTransform p, string name, string key, float x, float y, float w, float h,
            float size, Color col, TextAlignmentOptions align)
        {
            var t = UIKit.AddLocText(p, name, key, size, col, align);
            PlaceTL(t.rectTransform, x, y, w, h);
            return t;
        }

        private static void PlaceTL(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y); rt.sizeDelta = new Vector2(w, h);
        }

        // ---------------------------------------------------------------- pure helpers
        private static Language IndexToLang(int i) => i switch
        {
            0 => Language.TraditionalChinese, 1 => Language.SimplifiedChinese, 2 => Language.English, _ => Language.Japanese,
        };
        private static int LangToIndex(Language l) => l switch
        {
            Language.TraditionalChinese => 0, Language.SimplifiedChinese => 1, Language.English => 2, _ => 3,
        };

        /// <summary>Short chip caption for a KeyCode name (fits a 37px key-cap). Pure/testable.</summary>
        public static string ShortKeyName(string keyName)
        {
            if (string.IsNullOrEmpty(keyName)) return "";
            if (keyName.StartsWith("Keypad") && keyName.Length == 7) return keyName.Substring(6); // Keypad0-9 -> plain digit (matches official "4 5 8 6")
            if (keyName.StartsWith("Alpha")) return keyName.Substring(5);
            switch (keyName)
            {
                case "LeftArrow": return "←"; case "DownArrow": return "↓";
                case "UpArrow": return "↑"; case "RightArrow": return "→";
                case "LeftShift": return "⇧L"; case "RightShift": return "⇧R";
                case "LeftControl": return "^L"; case "RightControl": return "^R";
                case "Space": return "␣"; case "Return": return "⏎"; case "KeypadEnter": return "#⏎";
                case "Comma": return ","; case "Period": return "."; case "Slash": return "/"; case "Semicolon": return ";";
            }
            return keyName.Length <= 3 ? keyName : keyName.Substring(0, 3);
        }

        /// <summary>Tiny ◀ value ▶ selector (themed for the light board; UIKit.AddCycler is white-on-dark).</summary>
        private sealed class MiniSelect
        {
            private readonly string[] _opts; private readonly TextMeshProUGUI _label; private readonly Action<int> _onChange;
            public int Index { get; private set; }
            public MiniSelect(string[] opts, TextMeshProUGUI label, Action<int> onChange)
            { _opts = opts ?? new string[0]; _label = label; _onChange = onChange; }
            public void Step(int d) => Set(Index + d, true);
            public void Set(int i, bool notify)
            {
                if (_opts.Length == 0) return;
                Index = ((i % _opts.Length) + _opts.Length) % _opts.Length;
                if (_label != null) _label.text = _opts[Index];
                if (notify) _onChange?.Invoke(Index);
            }
        }
    }
}
