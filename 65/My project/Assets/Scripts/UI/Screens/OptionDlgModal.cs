using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
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
        private static readonly Color Title = Color.white;
        private static readonly Color BtnText = Color.white;
        private static readonly Color Header = new Color32(0xC0, 0x36, 0x86, 0xFF); // section headers (matches the original magenta pills)
        private static readonly Color LabelC = new Color32(0x7A, 0x2C, 0x5C, 0xFF); // row labels
        private static readonly Color ValueC = new Color32(0x53, 0x2A, 0x48, 0xFF); // selector/percent values
        private static readonly Color TrackBg = new Color32(0xE7, 0xC2, 0xDA, 0xFF);
        private static readonly Color TrackFill = new Color32(0xD6, 0x3F, 0x99, 0xFF);

        private static readonly string[] ModeIds = { "Windowed", "Fullscreen", "Borderless" };

        private CanvasGroup _cg;
        private RectTransform _videoTab, _audioTab, _keyTab;
        private MiniSelect _resSel, _modeSel, _langSel;
        private Image _vsyncDot; private TextMeshProUGUI _vsyncTxt;
        private Slider _sBgm, _sMusic, _sSfx;
        private readonly TextMeshProUGUI[] _pctTxt = new TextMeshProUGUI[3];
        private readonly TextMeshProUGUI[,] _capLabel = new TextMeshProUGUI[2, 4]; // [slot 0=primary/1=aux, lane]

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

            LocLabel(root, "Title", "option.title", 240, 133, 300, 26, 18, Title, TextAlignmentOptions.Left);

            // close (X)
            var close = UIKit.AddSpriteButton(root, "Close", OptionDlgArt.CloseN, OptionDlgArt.CloseN, OptionDlgArt.CloseP, 561, 134);
            close.onClick.AddListener(Close);

            // tabs (reuse a cleaned button pill as the chip; active = full colour, inactive = dimmed)
            _tabBtns = new Image[3];
            string[] tabKeys = { "option.tab.video", "option.tab.audio", "option.tab.keyboard" };
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                var b = UIKit.AddSpriteButton(root, "Tab" + i, OptionDlgArt.SaveN, OptionDlgArt.SaveN, OptionDlgArt.SaveP, 246 + i * 105, 182);
                _tabBtns[i] = b.targetGraphic as Image;
                var lab = UIKit.AddLocText(b.transform, "L", tabKeys[i], 15, BtnText, TextAlignmentOptions.Center);
                UIKit.Stretch(lab);
                b.onClick.AddListener(() => ShowTab(idx));
            }

            // shared sunken board behind every tab's content
            UIKit.AddSprite(root, "Board", OptionDlgArt.Board, 236, 225);

            _videoTab = TabBody(root, "VideoTab");
            _audioTab = TabBody(root, "AudioTab");
            _keyTab = TabBody(root, "KeyTab");
            BuildVideo(_videoTab);
            BuildAudio(_audioTab);
            BuildKeyboard(_keyTab);

            // action buttons (cleaned pills + dynamic labels), XML positions
            SpriteBtn(root, "Default", OptionDlgArt.DefaultN, OptionDlgArt.DefaultP, 261, 448, "option.default", ResetDefaults);
            SpriteBtn(root, "Save", OptionDlgArt.SaveN, OptionDlgArt.SaveP, 368, 448, "option.save", Apply);
            SpriteBtn(root, "Exit", OptionDlgArt.ExitN, OptionDlgArt.ExitP, 475, 448, "option.exit", Close);

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
        private void BuildAudio(RectTransform b)
        {
            _sBgm = AudioRow(b, "settings.bgm", 260, 0, v => _bgm = v);
            _sMusic = AudioRow(b, "settings.game_music", 310, 1, v => _music = v);
            _sSfx = AudioRow(b, "settings.sfx", 360, 2, v => _sfx = v);
        }

        private Slider AudioRow(RectTransform b, string key, float y, int idx, Action<float> onChange)
        {
            LocLabel(b, "AL" + idx, key, 262, y, 100, 24, 15, LabelC, TextAlignmentOptions.Left);
            var slider = AddSlider(b, 366, y + 3, 150, v =>
            {
                onChange(v);
                if (_pctTxt[idx] != null) _pctTxt[idx].text = Mathf.RoundToInt(v * 100) + "%";
            });
            _pctTxt[idx] = Label(b, "AP" + idx, "", 524, y, 44, 24, 14, ValueC, TextAlignmentOptions.Right);
            return slider;
        }

        // ---------------------------------------------------------------- 鍵盤 tab
        private static readonly string[] ArrowGlyph = { "←", "↓", "↑", "→" }; // Left Down Up Right
        private static readonly float[] LaneCx = { 318f, 386f, 454f, 522f };

        private void BuildKeyboard(RectTransform b)
        {
            LocLabel(b, "KP", "option.key.primary", 260, 250, 130, 22, 15, Header, TextAlignmentOptions.Left);
            for (int lane = 0; lane < 4; lane++)
                Label(b, "arw" + lane, ArrowGlyph[lane], LaneCx[lane] - 16, 248, 32, 24, 22, Header, TextAlignmentOptions.Center);
            for (int lane = 0; lane < 4; lane++)
                _capLabel[0, lane] = KeyCap(b, 0, lane, 278);

            LocLabel(b, "KA", "option.key.aux", 260, 336, 130, 22, 15, Header, TextAlignmentOptions.Left);
            for (int lane = 0; lane < 4; lane++)
                _capLabel[1, lane] = KeyCap(b, 1, lane, 364);
        }

        private TextMeshProUGUI KeyCap(RectTransform b, int slot, int lane, float y)
        {
            float x = LaneCx[lane] - 18f;
            var img = UIKit.AddSprite(b, "cap" + slot + "_" + lane, OptionDlgArt.KeyCap, x, y, raycast: true);
            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            int s = slot, ln = lane;
            btn.onClick.AddListener(() => BeginCapture(s, ln));
            var lab = UIKit.AddText(img.transform, "k", "", 15, Color.white, TextAlignmentOptions.Center);
            UIKit.Stretch(lab);
            return lab;
        }

        // ---------------------------------------------------------------- key capture (Update)
        private static readonly KeyCode[] Bindable = BuildBindable();

        private static KeyCode[] BuildBindable()
        {
            var l = new List<KeyCode>();
            for (var k = KeyCode.A; k <= KeyCode.Z; k++) l.Add(k);
            for (var k = KeyCode.Alpha0; k <= KeyCode.Alpha9; k++) l.Add(k);
            for (var k = KeyCode.Keypad0; k <= KeyCode.Keypad9; k++) l.Add(k);
            l.AddRange(new[] { KeyCode.LeftArrow, KeyCode.RightArrow, KeyCode.UpArrow, KeyCode.DownArrow,
                KeyCode.Space, KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftControl, KeyCode.RightControl,
                KeyCode.Comma, KeyCode.Period, KeyCode.Slash, KeyCode.Semicolon, KeyCode.Return, KeyCode.KeypadEnter });
            return l.ToArray();
        }

        private void BeginCapture(int slot, int lane)
        {
            CancelCapture();
            _capSlot = slot; _capLane = lane;
            _capLabel[slot, lane].text = "…";
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

            ShowTab(0);
            SetVisible(true);
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
            Toast.Show(L("option.title") + " ✓");
            SetVisible(false);
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
            SetVisible(false);
        }

        private void ShowTab(int i)
        {
            _videoTab.gameObject.SetActive(i == 0);
            _audioTab.gameObject.SetActive(i == 1);
            _keyTab.gameObject.SetActive(i == 2);
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
            var lab = _capLabel[slot, lane];
            if (lab == null) return;
            var name = (slot == 0 ? _prim : _aux)[lane];
            lab.text = ShortKeyName(name);
        }

        // ---------------------------------------------------------------- builders / helpers
        private void SpriteBtn(RectTransform p, string name, Sprite n, Sprite pushed, float x, float y, string key, Action onClick)
        {
            var b = UIKit.AddSpriteButton(p, name, n, n, pushed, x, y);
            var lab = UIKit.AddLocText(b.transform, "L", key, 15, BtnText, TextAlignmentOptions.Center);
            UIKit.Stretch(lab);
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

        /// <summary>Fill-bar slider (proven layout) with the official handle sprite riding the fill's right edge.</summary>
        private Slider AddSlider(RectTransform p, float x, float y, float w, Action<float> onChange)
        {
            var rt = UIKit.NewRect(p, "slider");
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y); rt.sizeDelta = new Vector2(w, 14f);
            var bg = rt.gameObject.AddComponent<Image>(); bg.color = TrackBg; bg.raycastTarget = true;
            var slider = rt.gameObject.AddComponent<Slider>();

            var fillArea = UIKit.NewRect(rt, "FillArea");
            fillArea.anchorMin = Vector2.zero; fillArea.anchorMax = Vector2.one;
            fillArea.offsetMin = new Vector2(1, 1); fillArea.offsetMax = new Vector2(-1, -1);
            var fill = UIKit.AddImage(fillArea, "Fill", TrackFill).rectTransform;
            fill.anchorMin = Vector2.zero; fill.anchorMax = Vector2.one; fill.sizeDelta = Vector2.zero;

            // official handle sprite parented to the fill's right edge → follows the value
            var handle = UIKit.AddImage(fill, "Handle", Color.white); handle.sprite = OptionDlgArt.SliderHandle;
            handle.rectTransform.anchorMin = handle.rectTransform.anchorMax = new Vector2(1f, 0.5f);
            handle.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            handle.rectTransform.sizeDelta = OptionDlgArt.SliderHandle != null ? OptionDlgArt.SliderHandle.rect.size : new Vector2(14, 18);
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
            if (keyName.StartsWith("Keypad")) return "#" + keyName.Substring(6);
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
