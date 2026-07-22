using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Sdo.Game;
using Sdo.Localization;
using Sdo.Settings;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>
    /// OPTION dialog — a faithful rebuild of the original OPTIONDLG on the official pink frame (<see cref="OptionDlgArt"/>).
    /// FOUR tabs, using the OFFICIAL baked tab art (遊戲/音效/鍵盤 + a custom 進階): 音效 (three volume sliders), 鍵盤
    /// (faithful 4-key rebinding), 遊戲 (OptionGameWindow — 遊戲畫面/泛光/NOTES面板/特效/視角/呼叫卡/透明度, its labels
    /// and option captions BAKED into the board so we overlay only selection dots + the transparency handle), and 進階
    /// (display settings — 視窗大小/顯示模式/垂直同步/語言 — the only page with dynamic text, drawn in 華康儷中黑 with the
    /// official colours: 標題/pill 標籤 = #FFF7D9, 內容 = #A5187F). Replaces the old plain SettingsModal.
    /// </summary>
    public sealed class OptionDlgModal : MonoBehaviour
    {
        // ---- palette ----
        private static readonly Color TitleCream = new Color32(0xFF, 0xF7, 0xD9, 0xFF);    // 選項標題 + pill 標籤字 (官方指定)
        private static readonly Color ContentMagenta = new Color32(0xA5, 0x18, 0x7F, 0xFF); // 內容字 (官方指定)
        private static readonly Color Header = new Color32(0xC0, 0x36, 0x86, 0xFF);         // ◀▶ selector arrows
        private static readonly Color KeyText = new Color32(0x00, 0x00, 0xB0, 0xFF);        // blue fallback for glyphless keys

        private static readonly string[] ModeIds = { "Windowed", "Fullscreen", "Borderless" };
        // 遊戲畫面(全屏/窗口) ↔ 進階(顯示模式/視窗大小) 連動用：800×600 在 ResolutionPreset 的索引(找不到退 0)。
        private static readonly int Res800Index = Mathf.Max(0, ResolutionPreset.IndexOf(800, 600));

        private CanvasGroup _cg;
        private RectTransform _window; private CanvasGroup _windowCg; private WindowAnim _anim; // ROOMDLG-style spin-zoom in/out
        private RectTransform _audioTab, _keyTab, _gameTab, _advTab;
        private MiniSelect _resSel, _modeSel, _langSel;
        private Slider _sBgm, _sMusic, _sSfx;
        private Image _board, _audioBoard, _advBoard;                               // 遊戲 / 音效 / 進階 boards
        private Image[] _tabBtns; private Sprite[] _tabNormal, _tabActive;          // official tab art + active-state swap
        // Each pill's art is baked with its own internal padding, so a shared Y leaves them visually uneven. These are
        // the hand-tuned per-tab downward nudges (px) that line the four pills up against the frame, one value per
        // state. Index = internal id: 0=音效, 1=鍵盤, 2=遊戲, 3=進階.
        private static readonly float[] TabNudgeNormal = { 1f, 1f, 2f, 2f };
        private static readonly float[] TabNudgeActive = { 1f, 1f, 1f, 2f };
        private float _tabBarY;                                                     // pill top edge before the nudge
        private readonly TextMeshProUGUI[,] _capLabel = new TextMeshProUGUI[2, 4]; // [slot 0=primary/1=aux, lane] — text fallback for glyphless keys
        private readonly Image[,] _capImg = new Image[2, 4];                        // key-cap chip sprite (purple idle / silver capturing)
        private readonly Image[,] _capGlyph = new Image[2, 4];                      // bound-key letter glyph (LOBBYDLG/KEYS/*.PNG)

        // working copy (committed to settings on Save)
        private int _resIndex, _modeIndex; private bool _vsync;
        private float _bgm, _music, _sfx;
        private Language _lang, _entryLang; private bool _applied;
        private readonly string[] _prim = new string[4];
        private readonly string[] _aux = new string[4];
        private int _capSlot = -1, _capLane = -1;                   // active key-capture target (-1 = none)
        private IMECompositionMode _imeBeforeOpen = IMECompositionMode.Auto;   // 房間(聊天)開著 On；關窗時還原
        private readonly List<Action> _advDots = new List<Action>(); // 進階 tab dots (vsync) repaint from their bool

        // 遊戲 (OptionGameWindow) working copy — committed to settings.gameplay on Save. See BuildGame.
        private bool _gpAspectFill, _gpBloom, _gpNotesLeft, _gpFxPlayer, _gpFxScene, _gpViewAuto, _gpCallShow;
        private int _gpViewFixed;       // 「固定」視角鎖第幾台鏡頭（遊戲中 F2 切到哪台就記哪台；這裡只是跟著存/還原）
        private bool _gpPlayFullSong;   // 進階「完奏模式」（放在進階頁最上面，存 settings.gameplay.playFullSong）
        private bool _gpSongSpeed;      // 進階「歌曲變速」（存 settings.gameplay.songSpeed）
        private float _gpPanelOpacity;
        private Slider _gpOpacitySlider;
        private readonly List<Action> _gameRefresh = new List<Action>();   // re-paint every game-tab dot from its bool

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

            // tabs — OFFICIAL baked pill art (each carries its 中文), active tab swaps to its deeper "pushed" state.
            // Visual left→right = 鍵盤 | 音效 | 遊戲 | 進階 so 遊戲 sits SECOND-FROM-THE-RIGHT and 進階 rightmost (as
            // requested). Internal index: 0=audio, 1=keyboard, 2=game, 3=advanced; tabX[idx] places it at its screen x.
            _tabBtns = new Image[4];
            _tabNormal = new[] { OptionDlgArt.TabAudioN, OptionDlgArt.TabKeyN, OptionDlgArt.TabGameN, OptionDlgArt.TabAdvN };
            _tabActive = new[] { OptionDlgArt.TabAudioA, OptionDlgArt.TabKeyA, OptionDlgArt.TabGameA, OptionDlgArt.TabAdvA };
            // Placed by CENTRE (pivot 0.5,1) with a FIXED size — normal/active are same-size so the sprite swap never
            // moves the pill. Built in visual left→right order (鍵盤 | 音效 | 遊戲 | 進階) so pills shingle
            // left-under-right; ShowTab raises the active pill to the front. Centres are computed from three knobs so
            // the layout is retuned by changing ONE number:
            //   ▶ TabSpacing = 相鄰 tab 中心距(px)。越小越擠(<pill寬 91-99 會重疊成連在一起)，越大越開。目前 80。
            //   ▶ TabBarCenterX = 整條 tab bar 的水平中心(px)。想整條左右移就改這個(視窗 800 寬、對話框中心≈414)。
            //   ▶ TabBarY = tab 的上緣高度(px，越小越高)。
            const float TabSpacing = 86f, TabBarCenterX = 412f, TabBarY = 183f;
            _tabBarY = TabBarY;
            int[] visualOrder = { 1, 0, 2, 3 };          // create L→R by index (鍵盤,音效,遊戲,進階)
            for (int slot = 0; slot < visualOrder.Length; slot++)
            {
                int id = visualOrder[slot];
                float cx = TabBarCenterX + (slot - (visualOrder.Length - 1) / 2f) * TabSpacing;
                var img = UIKit.AddImage(root, "Tab" + id, Color.white, raycast: true);
                img.sprite = _tabNormal[id];
                var rt = img.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(cx, -(TabBarY + TabNudgeNormal[id]));
                rt.sizeDelta = _tabNormal[id] != null ? _tabNormal[id].rect.size : new Vector2(94f, 38f);
                var btn = img.gameObject.AddComponent<Button>(); btn.targetGraphic = img;
                btn.onClick.AddListener(() => ShowTab(id));
                _tabBtns[id] = img;
            }

            // per-tab boards (only one shown at a time): 遊戲 uses OptScreenBoard (all labels+captions baked), 音效 uses
            // OptVolumeBoard (labels + MIN…MAX baked), 進階 uses the clean AdvBoard (we draw its text); 鍵盤 shows none
            // (the 4-key board is baked into the frame art).
            _board = UIKit.AddSprite(root, "Board", OptionDlgArt.Board, 236, 225);
            _audioBoard = UIKit.AddSprite(root, "AudioBoard", OptionDlgArt.AudioBoard, 236, 225);
            _advBoard = UIKit.AddSprite(root, "AdvBoard", OptionDlgArt.AdvBoard, 236, 220); // 進階板整體比其它板高 5px（連同下方文字一起上移）

            _audioTab = TabBody(root, "AudioTab");
            _keyTab = TabBody(root, "KeyTab");
            _gameTab = TabBody(root, "GameTab");
            _advTab = TabBody(root, "AdvTab");
            BuildAudio(_audioTab);
            BuildKeyboard(_keyTab);
            BuildGame(_gameTab);
            BuildAdvanced(_advTab);

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

            // 設定裡「每個按鈕按下都發 SE_0001」(官方 UI click)：所有 Button 都已建好且併入 _window，統一在此掃描掛上，
            // 一次涵蓋 tab／關閉／保存／退出／默認／◀▶ 選擇器／圓點選項。鍵帽 (EventTrigger) 已在 KeyCap 各自處理。
            foreach (var btn in _window.GetComponentsInChildren<Button>(true)) UiSfx.AttachClick(btn);

            SetVisible(false);
        }

        private RectTransform TabBody(RectTransform root, string name)
        {
            var rt = UIKit.NewRect(root, name);
            UIKit.Stretch(rt);   // full 800×600 overlay; children are placed at absolute XML coords
            return rt;
        }

        // ---------------------------------------------------------------- 進階 tab (display settings, dynamic text)
        // The only page whose text we draw (everything else is baked). Left labels sit on 深紛紅 label-pills (華康儷中黑,
        // #FFF7D9); values / captions are #A5187F. Row 1 (垂直同步) reuses the board's baked template pill + 2 circles
        // at the standard option columns; rows 2-4 (視窗大小/顯示模式/語言) add a cropped pill + a ◀value▶ selector.
        private void BuildAdvanced(RectTransform b)
        {
            var resNames = new string[ResolutionPreset.Presets.Length];
            for (int i = 0; i < resNames.Length; i++) resNames[i] = ResolutionPreset.Presets[i].ToString();

            // 六列，起始 y=238（比原本 243 高 5px，連同進階板一起上移）、列距 26（沿用「遊戲」頁 GameRowY 的列距）。radio 圓點在列頂 +12。
            const float y0 = 238f, step = 26f, dotDown = 12f;
            float yFull = y0, ySpeed = y0 + step, yVsync = y0 + step * 2f;
            float yRes = y0 + step * 3f, yMode = y0 + step * 4f, yLang = y0 + step * 5f;

            // Row 1 — 完奏模式（原「無失敗模式」，移到最上面）：HP 歸零不判失敗，整首照打到曲末。用 board 烘好的模板 pill + 兩顆圈。
            AdvLabel(b, yFull, "settings.play_full_song", bakedPill: true);
            AdvDot(b, 392f, yFull + dotDown, "common.enabled", () => _gpPlayFullSong, () => { _gpPlayFullSong = true; RefreshAdv(); });
            AdvDot(b, 481f, yFull + dotDown, "common.disabled", () => !_gpPlayFullSong, () => { _gpPlayFullSong = false; RefreshAdv(); });

            // Row 2 — 歌曲變速：開啟/關閉，預設開啟。非模板列 → 自繪深紫空心圓框(ownFrame)才有圈可看。
            AdvLabel(b, ySpeed, "settings.song_speed", bakedPill: false);
            AdvDot(b, 392f, ySpeed + dotDown, "common.enabled", () => _gpSongSpeed, () => { _gpSongSpeed = true; RefreshAdv(); }, ownFrame: true);
            AdvDot(b, 481f, ySpeed + dotDown, "common.disabled", () => !_gpSongSpeed, () => { _gpSongSpeed = false; RefreshAdv(); }, ownFrame: true);

            // Row 3 — 垂直同步：開啟/關閉。非模板列 → 自繪深紫空心圓框。
            AdvLabel(b, yVsync, "settings.vsync", bakedPill: false);
            AdvDot(b, 392f, yVsync + dotDown, "common.enabled", () => _vsync, () => { _vsync = true; RefreshAdv(); }, ownFrame: true);
            AdvDot(b, 481f, yVsync + dotDown, "common.disabled", () => !_vsync, () => { _vsync = false; RefreshAdv(); }, ownFrame: true);

            // Rows 4-6 — 視窗大小 / 顯示模式 / 語言：cropped pill label + a selector
            AdvLabel(b, yRes, "settings.resolution", bakedPill: false);
            _resSel = AdvSelector(b, yRes, resNames, i => _resIndex = i);

            AdvLabel(b, yMode, "settings.display_mode", bakedPill: false);
            _modeSel = AdvSelector(b, yMode, new[] { L("display.windowed"), L("display.fullscreen"), L("display.borderless") }, i =>
            {
                _modeIndex = i;
                // 顯示模式 → 遊戲畫面(全屏/窗口)連動：視窗=窗口(pillarbox)、全螢幕/無邊框全螢幕=全屏(stretch)。「主要就是看是選視窗或全螢幕」。
                _gpAspectFill = ModeIndexToAspectFill(i);
                RefreshGame();
            });

            AdvLabel(b, yLang, "settings.language", bakedPill: false);
            _langSel = AdvSelector(b, yLang, new[] { "繁體中文", "简体中文", "English", "日本語" }, i =>
            {
                _lang = IndexToLang(i);
                LocalizationManager.SetLanguage(_lang);           // live preview
            });
        }

        // 進階 helpers ---------------------------------------------------------
        private const float AdvPillX = 254f, AdvPillW = 95f, AdvPillH = 21f;

        // A left-side 深紛紅 label pill + its 華康儷中黑 caption. bakedPill=true uses the pill already on the board
        // (row 1, the template); otherwise a cropped AdvPill sprite is placed at (AdvPillX, pillTopY).
        private void AdvLabel(RectTransform b, float pillTopY, string key, bool bakedPill)
        {
            if (!bakedPill) UIKit.AddSprite(b, "advpill", OptionDlgArt.AdvPill, AdvPillX, pillTopY);
            var t = AdvText(b, "advlab", key, 14, TitleCream, TextAlignmentOptions.Center);
            PlaceTL(t.rectTransform, AdvPillX, pillTopY + 1.5f, AdvPillW, AdvPillH - 3f);
        }

        // A radio dot overlaid on a board circle at screen (cx,cy) + a 華康儷中黑 caption. Selected → filled RadioOn;
        // otherwise transparent so the (empty) circle shows through. The whole dot+caption box is clickable. Rows that sit
        // on the board's baked template circle leave <paramref name="ownFrame"/> false; rows placed on blank board area
        // pass ownFrame:true so a 淡紫圓框 (RadioOff 合成 orb) is drawn under the dot (否則未選狀態沒有圈可看).
        private void AdvDot(RectTransform b, float cx, float cy, string capKey, Func<bool> isOn, Action onPick, bool ownFrame = false)
        {
            var box = UIKit.NewRect(b, "advopt");
            box.anchorMin = box.anchorMax = new Vector2(0f, 1f); box.pivot = new Vector2(0f, 1f);
            box.anchoredPosition = new Vector2(cx - 9f, -(cy - 10f)); box.sizeDelta = new Vector2(72f, 20f);
            var hit = box.gameObject.AddComponent<Image>(); hit.color = new Color(1f, 1f, 1f, 0f); hit.raycastTarget = true;
            var btn = box.gameObject.AddComponent<Button>(); btn.targetGraphic = hit;
            btn.onClick.AddListener(() => { onPick(); RefreshAdv(); });
            if (ownFrame) OptionCircle(box, OptionDlgArt.RadioOff);   // 淡紫圓墊底(未選也看得見);baked 模板列已自帶不需要
            var dot = OptionDot(box);
            var cap = AdvText(box, "cap", capKey, 13, ContentMagenta, TextAlignmentOptions.Left);
            UIKit.Stretch(cap, 20, 0, 0, 0);
            _advDots.Add(() => { if (dot != null) dot.color = isOn() ? Color.white : new Color(1f, 1f, 1f, 0f); });
        }

        private MiniSelect AdvSelector(RectTransform b, float pillTopY, string[] opts, Action<int> onChange)
            => Selector(b, 388f, pillTopY - 3f, 172f, opts, 0, onChange);

        private void RefreshAdv() { foreach (var r in _advDots) r(); }

        // Localized TMP in 華康儷中黑 BOLD (falls back to Cjk if the face isn't installed) — used only on the 進階 tab.
        private TextMeshProUGUI AdvText(Transform p, string name, string key, float size, Color col, TextAlignmentOptions align)
        {
            var t = UIKit.AddLocText(p, name, key, size, col, align);
            var f = UIFont.Lihei; if (f != null) t.font = f;
            t.fontStyle = FontStyles.Bold;
            return t;
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
            // 拖動即時預覽:三個滑桿各自更新 working-copy 後 PushLiveAudio → AudioMix 立刻套用(聽得到);保存才寫進 settings。
            _sBgm = AddSlider(b, 0, v => { _bgm = v; PushLiveAudio(); });
            _sMusic = AddSlider(b, 1, v => { _music = v; PushLiveAudio(); });
            _sSfx = AddSlider(b, 2, v => { _sfx = v; PushLiveAudio(); });
        }

        // 把目前 working-copy 的三個音量推進 AudioMix → BGM/遊戲音樂/遊戲音效 即時套用(未存檔)。
        private void PushLiveAudio() => Sdo.Game.AudioMix.Set(_bgm, _music, _sfx);

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
            // 真人按下鍵帽才發 SE_0001（自動跳格的 BeginCapture 不經這裡，故不會連環叫）。
            down.callback.AddListener(_ => { UiSfx.Play(UiSfx.Click); BeginCapture(s, ln); });
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

        // ---------------------------------------------------------------- 遊戲 tab (OptionGameWindow)
        // The board (OptScreenBoard) has EVERY label + option caption BAKED (遊戲畫面 全屏/窗口, 全屏泛光效果 開啟/關閉,
        // NOTES面板位置 屏幕左邊/屏幕中央, 遊戲特效 人物特效/場景特效, 遊戲視角 固定/默認, 呼叫卡遊戲中顯示 開啟/關閉, 面板
        // 透明度 MIN…MAX). So we overlay ONLY: a selection dot on each baked circle (col A x=392, col B x=481; rows
        // 248/274/300/326/352/378) + the transparency handle riding the baked track (screen x 415..545 @ y≈405). Some
        // rows are wired to real behaviour (see GameplaySettings); 全屏泛光/音符面板/呼叫卡 just persist for fidelity.
        private static readonly float[] GameRowY = { 248f, 274f, 300f, 326f, 352f, 378f };
        private const float GameOptAX = 392f, GameOptBX = 481f;

        private void BuildGame(RectTransform b)
        {
            // row 1 遊戲畫面: A=全屏(fill→Stretch) · B=窗口(→Pillarbox 左右黑邊)。與進階頁「顯示模式/視窗大小」雙向連動：
            // 全屏 → 無邊框全螢幕 + 800×600(拉伸)；窗口 → 視窗 + 800×600（見 SetAspectFillLinked）。
            GameDot(b, GameOptAX, GameRowY[0], () => _gpAspectFill,  () => SetAspectFillLinked(true));
            GameDot(b, GameOptBX, GameRowY[0], () => !_gpAspectFill, () => SetAspectFillLinked(false));
            // row 2 全屏泛光效果: A=開啟 · B=關閉 (persist only)
            GameDot(b, GameOptAX, GameRowY[1], () => _gpBloom,  () => _gpBloom = true);
            GameDot(b, GameOptBX, GameRowY[1], () => !_gpBloom, () => _gpBloom = false);
            // row 3 NOTES面板位置: A=屏幕左邊 · B=屏幕中央 (persist only)
            GameDot(b, GameOptAX, GameRowY[2], () => _gpNotesLeft,  () => _gpNotesLeft = true);
            GameDot(b, GameOptBX, GameRowY[2], () => !_gpNotesLeft, () => _gpNotesLeft = false);
            // row 4 遊戲特效: A=人物特效 · B=場景特效 (two INDEPENDENT checkboxes)
            GameDot(b, GameOptAX, GameRowY[3], () => _gpFxPlayer, () => _gpFxPlayer = !_gpFxPlayer);
            GameDot(b, GameOptBX, GameRowY[3], () => _gpFxScene,  () => _gpFxScene = !_gpFxScene);
            // row 5 遊戲視角: A=固定(!auto) · B=默認(auto)   ← baked order: 固定 left, 默認 right
            GameDot(b, GameOptAX, GameRowY[4], () => !_gpViewAuto, () => _gpViewAuto = false);
            GameDot(b, GameOptBX, GameRowY[4], () => _gpViewAuto,  () => _gpViewAuto = true);
            // row 6 呼叫卡遊戲中顯示: A=開啟 · B=關閉 (persist only)
            GameDot(b, GameOptAX, GameRowY[5], () => _gpCallShow,  () => _gpCallShow = true);
            GameDot(b, GameOptBX, GameRowY[5], () => !_gpCallShow, () => _gpCallShow = false);
            // 面板透明度 slider (0..1.4X, = GameplaySettings.MaxPanelOpacity) → note 面板 alpha 倍率 (ScreenGameplay.boardAlpha). Handle travel is kept
            // INSIDE the baked MIN(…405)/MAX(526…) text — handle is 43px wide so centre range 430..502 clears both.
            _gpOpacitySlider = AddBakedSlider(b, 430f, 502f, 405f, v => _gpPanelOpacity = v);
        }

        // A selection dot overlaid on a baked board circle at screen (cx,cy) + a clickable box covering the dot and its
        // baked caption. Selected → filled RadioOn shows; otherwise transparent so the baked (empty) circle shows.
        private void GameDot(RectTransform b, float cx, float cy, Func<bool> isOn, Action onPick)
        {
            var box = UIKit.NewRect(b, "gopt");
            box.anchorMin = box.anchorMax = new Vector2(0f, 1f); box.pivot = new Vector2(0f, 1f);
            box.anchoredPosition = new Vector2(cx - 9f, -(cy - 10f)); box.sizeDelta = new Vector2(80f, 20f);
            var hit = box.gameObject.AddComponent<Image>(); hit.color = new Color(1f, 1f, 1f, 0f); hit.raycastTarget = true;
            var btn = box.gameObject.AddComponent<Button>(); btn.targetGraphic = hit;
            btn.onClick.AddListener(() => { onPick(); RefreshGame(); });
            var dot = OptionDot(box);
            _gameRefresh.Add(() => { if (dot != null) dot.color = isOn() ? Color.white : new Color(1f, 1f, 1f, 0f); });
        }

        // A filled RadioOn dot pinned to the left of a clickable option box (box-local x=9 = the baked circle centre).
        private static Image OptionDot(RectTransform box) => OptionCircle(box, OptionDlgArt.RadioOn);

        // A 15×15 radio orb (RadioOn orange / RadioOff lavender) pinned at box-local x=9 — the baked circle centre.
        private static Image OptionCircle(RectTransform box, Sprite sprite)
        {
            var img = UIKit.AddImage(box, "circle", Color.white); img.raycastTarget = false;
            img.sprite = sprite;
            var rt = img.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = sprite != null ? sprite.rect.size : new Vector2(15f, 15f);
            rt.anchoredPosition = new Vector2(9f, 0f);
            return img;
        }

        private void RefreshGame() { foreach (var r in _gameRefresh) r(); }

        // 遊戲畫面(全屏/窗口) → 連動進階(顯示模式 + 視窗大小)：全屏=無邊框全螢幕+800×600(拉伸)、窗口=視窗+800×600。
        // 更新選擇器一律 notify:false，避免回頭觸發 _modeSel.onChange 又改回 _gpAspectFill（雙向遞迴）。
        private void SetAspectFillLinked(bool fill)
        {
            _gpAspectFill = fill;
            _modeIndex = AspectFillToModeIndex(fill);
            _resIndex = Res800Index;
            _modeSel?.Set(_modeIndex, false);
            _resSel?.Set(_resIndex, false);
            RefreshGame();
        }

        // A handle-only slider whose official OptionDlg_Transparence handle rides between two screen x's (x0..x1) at a
        // baked track's y — the track/MIN/MAX are baked, so track+fill are invisible. minValue 0 .. maxValue 1.6.
        private Slider AddBakedSlider(RectTransform p, float x0, float x1, float y, Action<float> onChange)
        {
            const float H = 22f; float w = x1 - x0;
            var rt = UIKit.NewRect(p, "tslider");
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x0, -(y - H / 2f)); rt.sizeDelta = new Vector2(w, H);
            var bg = rt.gameObject.AddComponent<Image>(); bg.color = new Color(1f, 1f, 1f, 0f); bg.raycastTarget = true; // drag area
            var slider = rt.gameObject.AddComponent<Slider>();

            var fillArea = UIKit.NewRect(rt, "FillArea");
            fillArea.anchorMin = Vector2.zero; fillArea.anchorMax = Vector2.one; fillArea.offsetMin = Vector2.zero; fillArea.offsetMax = Vector2.zero;
            var fill = UIKit.AddImage(fillArea, "Fill", new Color(1f, 1f, 1f, 0f)).rectTransform;
            fill.anchorMin = Vector2.zero; fill.anchorMax = Vector2.one; fill.sizeDelta = Vector2.zero;

            var handle = UIKit.AddImage(fill, "Handle", Color.white); handle.sprite = OptionDlgArt.SliderHandle; handle.raycastTarget = false;
            handle.rectTransform.anchorMin = handle.rectTransform.anchorMax = new Vector2(1f, 0.5f);
            handle.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            handle.rectTransform.sizeDelta = OptionDlgArt.SliderHandle != null ? OptionDlgArt.SliderHandle.rect.size : new Vector2(43, 23);
            handle.rectTransform.anchoredPosition = Vector2.zero;

            slider.fillRect = fill; slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0f; slider.maxValue = GameplaySettings.MaxPanelOpacity; slider.targetGraphic = bg;
            slider.onValueChanged.AddListener(v => onChange(v));
            return slider;
        }

        // ---------------------------------------------------------------- key capture (Update)
        private static readonly KeyCode[] Bindable = BuildBindable();
        private static readonly KeyCode[] CaptureScan = BuildCaptureScan();   // Bindable + Esc（取消）
        private readonly KeyDownEdge _rawEdge = new KeyDownEdge();            // 實體按鍵的「剛按下」邊緣（繞過 IME）

        /// <summary>可綁的鍵（給測試檢查每顆都有對應的虛擬鍵碼，raw 路徑才不會漏鍵）。</summary>
        public static IReadOnlyList<KeyCode> BindableKeys => Bindable;

        private static KeyCode[] BuildCaptureScan()
        {
            var l = new List<KeyCode>(Bindable) { KeyCode.Escape };
            return l.ToArray();
        }

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
            // 綁鍵不能只靠 Input.GetKeyDown：中文輸入法組字態會把按鍵吃掉（房間為了聊天開著 IME 組字），
            // 那條路要先切英數才收得到。改成主走 RawKeyboard 的實體鍵狀態（IME 攔不到），Unity Input 只當後備
            // （非 Windows / 沒對應虛擬鍵碼時）。狀態每幀都要更新，所以 Tick 擺在提早 return 之前。
            bool capturing = _cg != null && _cg.alpha >= 0.5f && _capSlot >= 0;
            // GetAsyncKeyState 讀的是全系統鍵盤狀態 → 視窗沒焦點時別把別的程式的按鍵綁進來。
            KeyCode? raw = _rawEdge.Tick(CaptureScan, RawKeyboard.IsHeld, capturing && Application.isFocused);
            if (!capturing) return;

            if (raw == KeyCode.Escape || Input.GetKeyDown(KeyCode.Escape)) { CancelCapture(); return; }

            KeyCode? hit = raw;
            if (hit == null)
                foreach (var k in Bindable) if (Input.GetKeyDown(k)) { hit = k; break; }
            if (hit == null) return;

            (_capSlot == 0 ? _prim : _aux)[_capLane] = hit.Value.ToString();
            // 一鍵只能綁一處：把「其它」位置上與剛綁相同的鍵清空(含主鍵位↔輔助鍵位跨排)，並刷新被清掉的鍵帽。
            foreach (var pos in ClearDuplicateBinding(_prim, _aux, _capSlot, _capLane)) RefreshCap(pos.slot, pos.lane);
            // 綁好一格後自動跳到「同一排」右邊那格繼續設定；到最後一格 (lane 3) 回捲到第一格 (lane 0)。
            // 主鍵位 (slot 0) 與輔助鍵位 (slot 1) 各自獨立循環 —— slot 不變、只推進 lane。BeginCapture 內的
            // CancelCapture 會先把剛綁好的那格刷回帶新字符的閒置態，再點亮下一格 (要 Esc 或點別處才停)。
            int slot = _capSlot, nextLane = (_capLane + 1) % 4;
            BeginCapture(slot, nextLane);
        }

        // ---------------------------------------------------------------- open / apply / defaults / close
        /// 設定 modal 是否正顯示中（疊在房間上）。房間用它 gate ESC，避免開設定時 ESC 誤退回選角色。
        public bool IsOpen => _cg != null && _cg.alpha > 0f;

        public void Open()
        {
            // 設定視窗（尤其鍵盤頁）不打字，關掉 IME 組字：房間為了聊天把它開成 On，中文輸入法下按鍵會進組字被吃掉，
            // 還會把選字視窗蓋在對話框上。同時放掉聊天輸入框的 focus，免得綁鍵按的字母也打進聊天欄。關窗還原。
            _imeBeforeOpen = Input.imeCompositionMode;
            Input.imeCompositionMode = IMECompositionMode.Off;
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);

            var s = DisplaySettingsManager.Settings;
            _resIndex = Mathf.Max(0, ResolutionPreset.IndexOf(s.display.width, s.display.height));
            _modeIndex = Array.IndexOf(ModeIds, s.display.displayMode); if (_modeIndex < 0) _modeIndex = 0;
            _vsync = s.display.vsync;
            _bgm = s.audio.bgm; _music = s.audio.gameMusic; _sfx = s.audio.sfx;
            _entryLang = LocalizationManager.Current; _lang = _entryLang; _applied = false;
            s.keys ??= new KeyBindSettings();
            Array.Copy(KeyBindSettings.SanitizeNames(s.keys.lane4, KeyBindSettings.DefaultPrimary), _prim, 4);
            Array.Copy(KeyBindSettings.SanitizeNames(s.keys.lane4aux, KeyBindSettings.DefaultAux), _aux, 4);
            s.gameplay ??= new GameplaySettings();
            LoadGame(s.gameplay);
            // 遊戲畫面(全屏/窗口)與進階顯示模式連動 → 開窗時以顯示模式為準同步(主要看視窗 vs 全螢幕)，讓兩頁一開就一致
            // (含修正舊版存檔裡不一致的組合)。
            _gpAspectFill = ModeIndexToAspectFill(_modeIndex);
            CancelCapture();

            _resSel.Set(_resIndex, false);
            _modeSel.Set(_modeIndex, false);
            _langSel.Set(LangToIndex(_lang), false);
            RefreshAdv();
            if (_sBgm) _sBgm.value = _bgm;
            if (_sMusic) _sMusic.value = _music;
            if (_sSfx) _sSfx.value = _sfx;
            RefreshAllCaps();
            if (_gpOpacitySlider) _gpOpacitySlider.value = _gpPanelOpacity;
            RefreshGame();

            ShowTab(0);   // open on the 音效 (audio) page by default
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
            string newMode = ModeIds[Mathf.Clamp(_modeIndex, 0, ModeIds.Length - 1)];
            // 只有「顯示」設定真的改了才重設解析度/全螢幕。否則每次按保存(即使只改音量/按鍵/遊戲頁)都會 Screen.SetResolution
            // 重建視窗 → 畫面抽動/黑一下,使用者看起來像「跳出去」。用改前的值比對,沒變就跳過 ApplyDisplay。
            bool displayChanged = s.display.width != res.Width || s.display.height != res.Height
                                  || s.display.displayMode != newMode || s.display.vsync != _vsync;
            s.display.width = res.Width; s.display.height = res.Height;
            s.display.displayMode = newMode;
            s.display.vsync = _vsync;
            s.audio.bgm = _bgm; s.audio.gameMusic = _music; s.audio.sfx = _sfx;
            s.language = LanguageInfo.Code(_lang);
            s.keys ??= new KeyBindSettings();
            s.keys.lane4 = (string[])_prim.Clone();
            s.keys.lane4aux = (string[])_aux.Clone();
            s.gameplay ??= new GameplaySettings();
            StoreGame(s.gameplay);

            // Save() 一次落地兩個檔：[Option] 進 DATA/PROFILE/config.ini、鍵盤頁的 4 鍵鍵位進同層的 keymaps.ini。
            DisplaySettingsManager.Save();
            if (displayChanged) DisplaySettingsManager.ApplyDisplay();   // 沒改顯示就不重建視窗(避免保存像「跳出去」)
            Sdo.Game.AudioMix.Set(_bgm, _music, _sfx);   // 三類分別套用(背景音樂/遊戲音樂/遊戲音效),非舊的全域 AudioListener
            // 遊戲畫面 (全屏/黑邊) 立即套用：其餘遊戲頁偏好（特效/視角/透明度）在下一場遊戲開局讀取。
            AspectController.SetMode(_gpAspectFill ? AspectMode.Stretch : AspectMode.Pillarbox);
            _applied = true; _entryLang = _lang;
            // 保存 = 只儲存,不關對話框(使用者要求)。只播確認音,不跳提示;關閉走「退出」鈕。
            UiSfx.Play(UiSfx.Click);
        }

        private void ResetDefaults()
        {
            var d = new GameSettings();                                  // fresh defaults
            _resIndex = Mathf.Max(0, ResolutionPreset.IndexOf(d.display.width, d.display.height));
            _modeIndex = Array.IndexOf(ModeIds, d.display.displayMode); if (_modeIndex < 0) _modeIndex = 0;
            _vsync = d.display.vsync; _bgm = d.audio.bgm; _music = d.audio.gameMusic; _sfx = d.audio.sfx;
            Array.Copy(d.keys.lane4, _prim, 4);
            Array.Copy(d.keys.lane4aux, _aux, 4);
            LoadGame(d.gameplay);
            _gpAspectFill = ModeIndexToAspectFill(_modeIndex);   // 同 Open：遊戲畫面跟顯示模式連動
            CancelCapture();

            _resSel.Set(_resIndex, false);
            _modeSel.Set(_modeIndex, false);
            RefreshAdv();
            if (_sBgm) _sBgm.value = _bgm;
            if (_sMusic) _sMusic.value = _music;
            if (_sSfx) _sSfx.value = _sfx;
            RefreshAllCaps();
            if (_gpOpacitySlider) _gpOpacitySlider.value = _gpPanelOpacity;
            RefreshGame();
        }

        // ---- 遊戲 tab working-copy <-> GameplaySettings ----
        private void LoadGame(GameplaySettings g)
        {
            g ??= new GameplaySettings();
            _gpAspectFill = g.fullscreenFill; _gpBloom = g.bloom; _gpNotesLeft = g.notesPanelLeft;
            _gpFxPlayer = g.effectCharacter; _gpFxScene = g.effectScene; _gpViewAuto = g.cameraAuto;
            _gpViewFixed = g.cameraFixed;
            _gpCallShow = g.callCardInGame;
            _gpPlayFullSong = g.playFullSong;
            _gpSongSpeed = g.songSpeed;
            _gpPanelOpacity = Mathf.Clamp(g.panelOpacity, 0f, GameplaySettings.MaxPanelOpacity);
        }

        private void StoreGame(GameplaySettings g)
        {
            g.fullscreenFill = _gpAspectFill; g.bloom = _gpBloom; g.notesPanelLeft = _gpNotesLeft;
            g.effectCharacter = _gpFxPlayer; g.effectScene = _gpFxScene; g.cameraAuto = _gpViewAuto;
            g.cameraFixed = _gpViewFixed;
            g.callCardInGame = _gpCallShow;
            g.playFullSong = _gpPlayFullSong;
            g.songSpeed = _gpSongSpeed;
            g.panelOpacity = Mathf.Clamp(_gpPanelOpacity, 0f, GameplaySettings.MaxPanelOpacity);
        }

        private void Close()
        {
            CancelCapture();
            Input.imeCompositionMode = _imeBeforeOpen;   // 還原開窗前的組字模式（房間=On，聊天才打得出中文）
            if (!_applied && LocalizationManager.Current != _entryLang)
                LocalizationManager.SetLanguage(_entryLang);            // revert live language preview
            if (!_applied)
            {
                var a = DisplaySettingsManager.Settings.audio;          // 退出未存 → 還原三個音量的 live 預覽到已存值
                Sdo.Game.AudioMix.Set(a.bgm, a.gameMusic, a.sfx);
            }
            AnimatedHide();
        }

        // Tab indices: 0=音效, 1=鍵盤, 2=遊戲, 3=進階. Each shows its body + its board (鍵盤 has none — the 4-key board
        // is baked into the frame). The active tab swaps to its deeper "pushed" pill art; the rest use the normal art.
        private void ShowTab(int i)
        {
            _audioTab.gameObject.SetActive(i == 0);
            _keyTab.gameObject.SetActive(i == 1);
            _gameTab.gameObject.SetActive(i == 2);
            _advTab.gameObject.SetActive(i == 3);
            if (_audioBoard != null) _audioBoard.gameObject.SetActive(i == 0);
            if (_board != null) _board.gameObject.SetActive(i == 2);       // 遊戲 board (baked labels)
            if (_advBoard != null) _advBoard.gameObject.SetActive(i == 3); // 進階 board (clean)
            // swap the sprite (size stays fixed → no jump; normal/active are same-size crops) and re-apply that state's
            // per-tab downward nudge, since a pill's art can sit differently in its normal vs pushed crop.
            for (int t = 0; t < _tabBtns.Length; t++)
            {
                if (_tabBtns[t] == null) continue;
                bool active = t == i;
                _tabBtns[t].sprite = active ? _tabActive[t] : _tabNormal[t];
                var rt = _tabBtns[t].rectTransform;
                var p = rt.anchoredPosition;
                p.y = -(_tabBarY + (active ? TabNudgeActive[t] : TabNudgeNormal[t]));
                rt.anchoredPosition = p;
            }
            if (_tabBtns[i] != null) _tabBtns[i].transform.SetAsLastSibling();   // active pill on top of its overlapping neighbours
        }

        private void SetVisible(bool on)
        {
            _cg.alpha = on ? 1f : 0f;
            _cg.interactable = on;
            _cg.blocksRaycasts = on;
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

            var val = UIKit.AddText(box, "Val", "", 14, ContentMagenta, TextAlignmentOptions.Center);
            var lf = UIFont.Lihei; if (lf != null) val.font = lf;
            val.fontStyle = FontStyles.Bold;   // 紫色內容字加粗 (官方指定)
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

        private static void PlaceTL(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y); rt.sizeDelta = new Vector2(w, h);
        }

        // ---------------------------------------------------------------- pure helpers
        /// <summary>遊戲畫面「全屏(拉伸)/窗口」→ 進階「顯示模式」索引：全屏=無邊框全螢幕(Borderless,2)、窗口=視窗(Windowed,0)。
        /// 官方需求：全屏對應「全螢幕視窗化」。純函式(可測)。</summary>
        public static int AspectFillToModeIndex(bool fill) => fill ? 2 : 0;

        /// <summary>進階「顯示模式」索引 → 遊戲畫面是否全屏(拉伸)：視窗(0)=窗口(false)，全螢幕(1)/無邊框全螢幕(2)=全屏(true)。
        /// 「主要就是看是選視窗或全螢幕」。純函式(可測)。</summary>
        public static bool ModeIndexToAspectFill(int modeIndex) => modeIndex != 0;

        private static Language IndexToLang(int i) => i switch
        {
            0 => Language.TraditionalChinese, 1 => Language.SimplifiedChinese, 2 => Language.English, _ => Language.Japanese,
        };
        private static int LangToIndex(Language l) => l switch
        {
            Language.TraditionalChinese => 0, Language.SimplifiedChinese => 1, Language.English => 2, _ => 3,
        };

        /// <summary>一鍵只能綁一處：綁定 (keepSlot,keepLane) 後，把 prim/aux 兩排裡「其它」所有等於同一鍵名的格子
        /// 清成 ""（含主鍵位↔輔助鍵位跨排），回傳被清掉的位置清單（讓呼叫端刷新那些鍵帽）。剛綁的那格與空鍵都不動。
        /// 純函式，可測。</summary>
        public static List<(int slot, int lane)> ClearDuplicateBinding(string[] prim, string[] aux, int keepSlot, int keepLane)
        {
            var cleared = new List<(int slot, int lane)>();
            var key = (keepSlot == 0 ? prim : aux)?[keepLane];
            if (string.IsNullOrEmpty(key)) return cleared;              // 剛綁的是空 → 不去清別的空格
            for (int s = 0; s < 2; s++)
            {
                var arr = s == 0 ? prim : aux;
                if (arr == null) continue;
                for (int l = 0; l < arr.Length && l < 4; l++)
                {
                    if (s == keepSlot && l == keepLane) continue;       // 剛綁的那格保留
                    if (arr[l] == key) { arr[l] = ""; cleared.Add((s, l)); }
                }
            }
            return cleared;
        }

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
