using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Sdo.Localization;
using Sdo.Settings;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>設定模態：畫面（視窗大小/顯示模式/VSync）、音效（三段音量）、語言（即時切換）。</summary>
    public sealed class SettingsModal : MonoBehaviour
    {
        private static readonly string[] ModeIds = { "Windowed", "Fullscreen", "Borderless" };

        private CanvasGroup _cg;
        private RectTransform _videoTab, _audioTab, _langTab;
        private Button _vsyncBtn; private TextMeshProUGUI _vsyncLabel;
        private Cycler _resCycler, _modeCycler, _langCycler;

        private int _resIndex, _modeIndex; private bool _vsync;
        private float _bgm, _music, _sfx;
        private Language _lang, _entryLang;
        private bool _applied;

        private static string L(string k) => LocalizationManager.Get(k);

        public void Build(RectTransform parent)
        {
            var root = UIKit.NewRect(parent, "SettingsModal");
            UIKit.Stretch(root);
            _cg = root.gameObject.AddComponent<CanvasGroup>();

            var dim = UIKit.AddImage(root, "Dim", new Color(0, 0, 0, 0.6f), true);
            UIKit.Stretch(dim.rectTransform);

            var panel = UIKit.AddImage(root, "Panel", UITheme.Panel).rectTransform;
            UIKit.Anchor(panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            panel.sizeDelta = new Vector2(680, 480);

            var title = UIKit.AddLocText(panel, "Title", "settings.title", 24, UITheme.Text, TextAlignmentOptions.Center);
            UIKit.Anchor(title.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1));
            title.rectTransform.sizeDelta = new Vector2(0, 44); title.rectTransform.anchoredPosition = new Vector2(0, -8);

            // tabs
            string[] tabKeys = { "settings.tab.video", "settings.tab.audio", "settings.tab.language" };
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                var tab = UIKit.AddLocButton(panel, "Tab" + i, tabKeys[i], UITheme.Secondary, UITheme.Text, 16);
                var trt = tab.GetComponent<RectTransform>();
                UIKit.Anchor(trt, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
                trt.sizeDelta = new Vector2(150, 36); trt.anchoredPosition = new Vector2(24 + i * 158, -56);
                tab.onClick.AddListener(() => ShowTab(idx));
            }

            _videoTab = MakeTabBody(panel);
            _audioTab = MakeTabBody(panel);
            _langTab = MakeTabBody(panel);

            BuildVideoTab(_videoTab);
            BuildAudioTab(_audioTab);
            BuildLangTab(_langTab);

            // footer
            var apply = UIKit.AddLocButton(panel, "Apply", "common.apply", UITheme.Primary, UITheme.OnPrimary, 18);
            var art = apply.GetComponent<RectTransform>();
            UIKit.Anchor(art, new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0));
            art.sizeDelta = new Vector2(150, 46); art.anchoredPosition = new Vector2(-24, 16);
            apply.onClick.AddListener(Apply);

            var close = UIKit.AddLocButton(panel, "Close", "common.close", UITheme.Secondary, UITheme.Text, 18);
            var clt = close.GetComponent<RectTransform>();
            UIKit.Anchor(clt, new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0));
            clt.sizeDelta = new Vector2(150, 46); clt.anchoredPosition = new Vector2(-186, 16);
            close.onClick.AddListener(Close);

            SetVisible(false);
        }

        private RectTransform MakeTabBody(RectTransform panel)
        {
            var body = UIKit.NewRect(panel, "TabBody");
            body.anchorMin = new Vector2(0, 0); body.anchorMax = new Vector2(1, 1);
            body.offsetMin = new Vector2(24, 76); body.offsetMax = new Vector2(-24, -100);
            return body;
        }

        private void Row(RectTransform body, string labelKey, int row, out RectTransform fieldRect)
        {
            float y = -row * 56f;
            var lab = UIKit.AddLocText(body, "L" + row, labelKey, 17, UITheme.Text);
            UIKit.Anchor(lab.rectTransform, new Vector2(0, 1), new Vector2(0.4f, 1), new Vector2(0, 1));
            lab.rectTransform.sizeDelta = new Vector2(0, 40); lab.rectTransform.anchoredPosition = new Vector2(0, y - 4);
            fieldRect = UIKit.NewRect(body, "F" + row);
            UIKit.Anchor(fieldRect, new Vector2(0.4f, 1), new Vector2(1, 1), new Vector2(0, 1));
            fieldRect.sizeDelta = new Vector2(0, 40); fieldRect.anchoredPosition = new Vector2(0, y - 4);
        }

        private void BuildVideoTab(RectTransform body)
        {
            Row(body, "settings.resolution", 0, out var f0);
            var resNames = new string[ResolutionPreset.Presets.Length];
            for (int i = 0; i < resNames.Length; i++) resNames[i] = ResolutionPreset.Presets[i].ToString();
            _resCycler = UIKit.AddCycler(f0, "Res", resNames, 0, out var rrt);
            UIKit.Stretch(rrt); _resCycler.Changed += i => _resIndex = i;

            Row(body, "settings.display_mode", 1, out var f1);
            _modeCycler = UIKit.AddCycler(f1, "Mode",
                new[] { L("display.windowed"), L("display.fullscreen"), L("display.borderless") }, 0, out var mrt);
            UIKit.Stretch(mrt); _modeCycler.Changed += i => _modeIndex = i;

            Row(body, "settings.vsync", 2, out var f2);
            _vsyncBtn = UIKit.AddButton(f2, "VSync", out _vsyncLabel, UITheme.Secondary, UITheme.Text, 16);
            var vrt = _vsyncBtn.GetComponent<RectTransform>();
            UIKit.Anchor(vrt, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f));
            vrt.sizeDelta = new Vector2(120, 38); vrt.anchoredPosition = new Vector2(60, 0);
            _vsyncBtn.onClick.AddListener(() => { _vsync = !_vsync; RefreshVSyncLabel(); });
        }

        private void BuildAudioTab(RectTransform body)
        {
            AddSlider(body, "settings.bgm", 0, v => _bgm = v);
            AddSlider(body, "settings.game_music", 1, v => _music = v);
            AddSlider(body, "settings.sfx", 2, v => _sfx = v);
        }

        private Slider _sBgm, _sMusic, _sSfx;

        private Slider AddSlider(RectTransform body, string labelKey, int row, Action<float> onChange)
        {
            Row(body, labelKey, row, out var f);
            var rt = UIKit.NewRect(f, "Slider");
            UIKit.Anchor(rt, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(0, 0.5f));
            rt.offsetMin = new Vector2(0, -10); rt.offsetMax = new Vector2(-70, 10);
            var bg = rt.gameObject.AddComponent<Image>(); bg.color = new Color(1, 1, 1, 0.12f); bg.raycastTarget = true;
            var slider = rt.gameObject.AddComponent<Slider>();

            var fillArea = UIKit.NewRect(rt, "FillArea");
            fillArea.anchorMin = new Vector2(0, 0); fillArea.anchorMax = new Vector2(1, 1);
            fillArea.offsetMin = new Vector2(2, 2); fillArea.offsetMax = new Vector2(-2, -2);
            var fill = UIKit.AddImage(fillArea, "Fill", UITheme.Accent).rectTransform;
            fill.anchorMin = Vector2.zero; fill.anchorMax = Vector2.one; fill.sizeDelta = Vector2.zero;

            slider.fillRect = fill; slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0; slider.maxValue = 1; slider.targetGraphic = bg;

            var valText = UIKit.AddText(f, "Val", "", 15, UITheme.TextDim, TextAlignmentOptions.Center);
            UIKit.Anchor(valText.rectTransform, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f));
            valText.rectTransform.sizeDelta = new Vector2(64, 30); valText.rectTransform.anchoredPosition = new Vector2(-2, 0);

            slider.onValueChanged.AddListener(v => { onChange(v); valText.text = Mathf.RoundToInt(v * 100) + "%"; });

            if (row == 0) _sBgm = slider; else if (row == 1) _sMusic = slider; else _sSfx = slider;
            return slider;
        }

        private void BuildLangTab(RectTransform body)
        {
            Row(body, "settings.language", 0, out var f);
            var names = new[] { "繁體中文", "简体中文", "English", "日本語" };
            _langCycler = UIKit.AddCycler(f, "Lang", names, 0, out var lrt);
            UIKit.Stretch(lrt);
            _langCycler.Changed += i =>
            {
                _lang = IndexToLang(i);
                LocalizationManager.SetLanguage(_lang);   // live preview
            };
        }

        private static Language IndexToLang(int i) => i switch
        {
            0 => Language.TraditionalChinese,
            1 => Language.SimplifiedChinese,
            2 => Language.English,
            _ => Language.Japanese,
        };

        private static int LangToIndex(Language l) => l switch
        {
            Language.TraditionalChinese => 0,
            Language.SimplifiedChinese => 1,
            Language.English => 2,
            _ => 3,
        };

        private void RefreshVSyncLabel()
        {
            if (_vsyncLabel != null) _vsyncLabel.text = L(_vsync ? "common.on" : "common.off");
        }

        public void Open()
        {
            var s = DisplaySettingsManager.Settings;
            _resIndex = Mathf.Max(0, ResolutionPreset.IndexOf(s.display.width, s.display.height));
            _modeIndex = Array.IndexOf(ModeIds, s.display.displayMode); if (_modeIndex < 0) _modeIndex = 0;
            _vsync = s.display.vsync;
            _bgm = s.audio.bgm; _music = s.audio.gameMusic; _sfx = s.audio.sfx;
            _entryLang = LocalizationManager.Current; _lang = _entryLang; _applied = false;

            _resCycler.Set(_resIndex, false);
            _modeCycler.Set(_modeIndex, false);
            _langCycler.Set(LangToIndex(_lang), false);
            RefreshVSyncLabel();
            if (_sBgm) _sBgm.value = _bgm;
            if (_sMusic) _sMusic.value = _music;
            if (_sSfx) _sSfx.value = _sfx;

            ShowTab(0);
            SetVisible(true);
        }

        private void ShowTab(int i)
        {
            _videoTab.gameObject.SetActive(i == 0);
            _audioTab.gameObject.SetActive(i == 1);
            _langTab.gameObject.SetActive(i == 2);
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

            DisplaySettingsManager.Save();
            DisplaySettingsManager.ApplyDisplay();
            AudioListener.volume = _music;
            _applied = true; _entryLang = _lang;
            Toast.Show(L("settings.title") + " ✓");
            SetVisible(false);
        }

        private void Close()
        {
            if (!_applied && LocalizationManager.Current != _entryLang)
                LocalizationManager.SetLanguage(_entryLang);   // revert live preview
            SetVisible(false);
        }

        private void SetVisible(bool on)
        {
            _cg.alpha = on ? 1f : 0f;
            _cg.interactable = on;
            _cg.blocksRaycasts = on;
        }
    }
}
