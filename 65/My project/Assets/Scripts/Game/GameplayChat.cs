using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 遊戲畫面的聊天框(官方 <c>GAMEPLAYNEWLEAN.XML</c> 的 <c>winchat</c>):右下角一條常駐的
    /// 「当前 ▸ 輸入框 ▸ 送出 ▸ 表情」,旁邊浮著最近幾行訊息。
    ///
    /// 跟房間左下角那個聊天欄的差別:
    ///   • **文字會自己藏起來** —— 進場預設不顯示,有人講話才整批冒出來,之後 10 秒沒人說話就淡掉
    ///     (見 <see cref="GameplayChatFade"/>)。底板那一條是常駐的。
    ///   • **不彈頭上泡、不做關鍵字動作** —— 舞者正在跳編舞,插一個聊天動作會把舞打斷(使用者指定)。
    ///   • **沒有「X 進入舞台遊戲」那種系統廣播** —— 那是房間的事;過濾在前端(<c>FrontendApp</c>)做,
    ///     這裡收到什麼就畫什麼。
    ///   • NOTES 面板選「屏幕中央 + 向下」時整組搬到畫面右上(<see cref="GameplayChatLayout"/>)。
    ///
    /// 畫法跟 <see cref="ResultScreen"/> 一樣是 HUD 正交相機上的 SpriteRenderer + <see cref="Label3D"/>
    /// (遊戲執行中前端那張 canvas 是關掉的,而且這專案的 URP overlay 相機畫不出 UGUI world canvas),
    /// 所以連點擊命中也是自己用 bounds 做。
    ///
    /// 打字用 Tab 開(或滑鼠點輸入框),Enter 送出後**自動退出**回到打歌,Esc 取消。打字期間
    /// <see cref="Typing"/> 為 true,<c>ScreenGameplay</c> 據此停掉 lane 鍵與所有熱鍵 —— 否則打「w」會
    /// 同時踩到上鍵。中文輸入法也只在打字期間才開(<see cref="Input.imeCompositionMode"/>),不然 IME
    /// 會把 ASWD 整個吃掉,連歌都打不了。
    /// </summary>
    public sealed class GameplayChat
    {
        /// <summary>玩家按 Enter 送出的原始字串(表情指令 / 密語 [名字] 的解析在前端做,跟房間同一條路)。</summary>
        public System.Action<string> OnSend;
        /// <summary>切換聊天頻道(值＝前端 <c>ChatChannel</c> 的整數;順序見 <see cref="ChannelOrder"/>)。</summary>
        public System.Action<int> OnChannel;
        /// <summary>表情面板點下去 → 送出那個表情。</summary>
        public System.Action<int> OnExpression;

        /// <summary>
        /// 正在打字 —— 遊戲的 lane 鍵/熱鍵這期間全部停用。
        ///
        /// 🔴 **退出打字的那一幀仍然回 true**。Esc 在這裡是「取消打字」,但它同時也是遊戲的中離鍵
        /// (<c>FrontendApp</c> 的 Hotkey.Quit),而兩邊都是用 <c>Input.GetKeyDown</c> 各自輪詢的 ——
        /// 這一幀我們先吃掉 Esc 把 Typing 關成 false,FrontendApp 晚一步跑到時看到的就是 false,
        /// 於是同一顆 Esc 又被當成中離、整場直接退出。多守一幀把那顆按鍵完整吃掉。
        /// (送出用的 Enter 同理:結算面板的「確定」也是 Enter。)
        /// </summary>
        public bool Typing => _typing || _typingReleasedFrame == Time.frameCount;

        private bool _typing;
        private int _typingReleasedFrame = -1;

        /// <summary>DEV(SDO_CHATDEMO):訊息文字不自動淡掉 —— 正常玩的時候字只在有人說話後的 10 秒內看得到,
        /// 而實機截圖大多是開場好一陣子才按下去的,不釘住就永遠拍不到字。</summary>
        public bool DebugKeepText;

        /// <summary>DEV(SDO_CHATDEMO=2 / 3):開場就把表情面板 / 頻道選單展開 —— 無頭截圖沒有滑鼠可以點,
        /// 而這兩塊用的是官方素材(綠框表情盤、家族/好友/當前/回復),只有實機截圖看得出來對不對。</summary>
        public void DebugOpenExpressionPanel() => ToggleExpressionPanel();
        public void DebugOpenModeMenu() => ToggleModeMenu();

        /// <summary>chatmode 彈出選單由上而下的頻道(＝前端 <c>ChatChannel</c> 的整數):家族 0 / 好友 1 / 當前 2 / 回復 3。
        /// 順序與房間的 <c>chatmodemenu</c> 完全相同。</summary>
        public static readonly int[] ChannelMenu = { 0, 1, 2, 3 };

        /// <summary>面板裡的一個表情:id、動畫小圖、以及點下去要塞進輸入框的指令文字(如 <c>/GO</c>)。</summary>
        public struct Expression { public int Id; public Sprite[] Frames; public string Command; }

        /// <summary>表情面板的官方素材(ROOMPOPMENU / EXPRESSIONINFO)—— 與房間左下角那個表情選單同一組圖、
        /// 同一份版面(165×152 的綠框、上緣分頁標籤、底部翻頁箭頭與頁碼)。前端注入,這裡只負責畫與命中。</summary>
        public sealed class ExpressionPanelArt
        {
            public Sprite[] PageBackgrounds;   // 每頁一張 165×132(ExpressionInfoPage)
            public Sprite Tab;                 // 38×18「普通表情」分頁標籤
            public Sprite[] ArrowLeft;         // 3 態 17×17
            public Sprite[] ArrowRight;
            public Expression[][] Pages;       // 每頁最多 24 個(6×4)
        }

        private const int OrderBar = 60, OrderBtn = 62, OrderText = 64, OrderPanel = 70, OrderPanelIcon = 72,
                          OrderPanelText = 74;
        // 字級比房間(13)大很多:房間那欄疊在 3D 房間上、字多且擠;遊戲中只有幾行浮在舞台上,要一眼看得清。
        // em 盒 = 0.704 × 18 ≈ 12.7 設計 px,配 13px 的行高(GameplayChatLayout.LineH)—— 字大、行距又比
        // 官方的 14 更密,整叢字看起來是一整塊而不是散開的幾行。
        private const float TextPx = 18f;
        private const float EdgePx = 0.9f;           // 黑邊厚度跟著字級走(房間 13px 用 0.7)
        private const float CaretBlinkSec = 0.53f;
        private const float PressFlashSec = 0.09f;   // 按鈕按下那一下的 pushed 圖持續時間
        private const int DraftLimit = 80;           // XML EditBox limittext="80"
        private const float EmojiPx = 13f;           // 訊息行裡的表情小圖邊長(行高 14)
        // 表情面板的版面 = 房間 RebuildExpressionMenu 的同一組數字(ROOMPOPMENU):底圖在 (0,20)、
        // 分頁標籤 (5,3)、格子 24×24 起於 (4,24) 每 26px、翻頁箭頭 (103,131)/(146,131)、頁碼 (118..136,133)。
        private const float ExprBgY = 20f, ExprTabX = 5f, ExprTabY = 3f;
        private const float ExprCellX0 = 4f, ExprCellY0 = 24f, ExprCellStep = 26f, ExprCellSize = 24f;
        private const float ExprArrowLX = 103f, ExprArrowRX = 146f, ExprArrowY = 131f, ExprArrowSize = 17f;
        private const int ExprCols = 6, ExprRows = 4;   // 一頁 24 格

        private static readonly Vector2[] EdgeCross =
        {
            new Vector2(EdgePx, 0f), new Vector2(-EdgePx, 0f),
            new Vector2(0f, EdgePx), new Vector2(0f, -EdgePx),
        };

        // chatmode 三態圖,索引 = ChatChannel(0 家族 / 1 好友 / 2 當前 / 3 回復)。當前裁自 ChatROOM.png(51×30),
        // 其餘裁自 SmallButton.png(49×25)。與房間的 ChatModeArt 是同一張對照表。
        private static readonly string[][] ModeArt =
        {
            new[] { "Room203.an", "Room204.an", "Room205.an" },  // 家族
            new[] { "Room200.an", "Room201.an", "Room202.an" },  // 好友
            new[] { "Room4.an", "Room5.an", "Room6.an" },        // 當前
            new[] { "Room206.an", "Room207.an", "Room208.an" },  // 回復
        };

        private Camera _cam;
        private GameObject _root;
        private GameplayChatLayout _layout;

        private SpriteRenderer _bar, _barTail, _modeBtn, _sendBtn, _exprBtn;
        private Label3D _draftLabel;
        private SpriteRenderer _caret;

        private readonly List<GameplayChatLine> _lines = new List<GameplayChatLine>();
        private readonly List<Row> _rows = new List<Row>();

        private ExpressionPanelArt _exprArt;
        private GameObject _exprPanel;
        private SpriteRenderer _exprBg, _exprTab, _exprArrowL, _exprArrowR;
        private float _exprOriginX, _exprOriginY;   // 面板左上角(design);格子命中用
        private Label3D _exprPageLabel;
        private int _exprPage;
        private bool _exprOpen;

        private GameObject _modeMenu;
        private readonly List<SpriteRenderer> _modeMenuBtns = new List<SpriteRenderer>();
        private bool _modeMenuOpen;

        private string _draft = "";
        private int _channel = 2;             // 目前頻道 = ChatChannel 的整數(2 = 當前)
        private double _lastActivitySec = -1; // 最後一則訊息 / 最後一次送出 → 文字何時淡掉
        private float _alpha = -1f;           // 目前套用中的文字不透明度(−1 = 還沒套過)
        private bool _visible = true;         // 整個聊天框(含底板)在不在場上 —— 結算/GAME OVER 時關掉
        private int _hover = -1;              // 0=chatmode 1=send 2=expr,−1=沒有
        private int _pressed = -1;            // 同上,按下那一瞬間顯示 pushed 圖
        private float _pressedAt = -99f;

        private sealed class Row
        {
            public GameObject go;
            public Label3D head;              // 名字 + 表情前的字(或整行)
            public Label3D tail;              // 表情後的字
            public SpriteRenderer emoji;
            public Sprite[] frames;
            public Color face;                // 這一行的字色(淡入淡出時只換 alpha)
            public bool has;                  // 這一格有沒有對應到訊息
            public bool hasTail;
        }

        // ---- build ----

        /// <summary>建出整條聊天列(訊息文字預設隱藏)。<paramref name="hudCam"/> = 遊戲的 800×600 正交主相機。</summary>
        public void Build(Camera hudCam, bool panelLeft, bool bottomDrop)
        {
            _cam = hudCam;
            _layout = GameplayChatLayout.Resolve(panelLeft, bottomDrop);
            _root = new GameObject("GameplayChat");

            _bar = NewSR("ChatBar", Art("GamePlay3.an"), OrderBar);
            _barTail = NewSR("ChatBarTail", Art("GamePlay4.an"), OrderBar);   // 官方底板是兩片,這是右端收尾
            _modeBtn = NewSR("ChatMode", null, OrderBtn);
            _sendBtn = NewSR("ChatSend", null, OrderBtn);
            _exprBtn = NewSR("ChatExpr", null, OrderBtn);

            _draftLabel = NewLabel("ChatDraft", OrderText);
            _draftLabel.SetActive(false);
            _caret = NewSR("ChatCaret", WhitePixel(), OrderText);
            _caret.enabled = false;

            for (int i = 0; i < GameplayChatLayout.MaxLines; i++) _rows.Add(NewRow(i));

            ApplyLayout();
            ApplyAlpha(0f, force: true);
        }

        /// <summary>表情面板的官方素材與內容(前端提供)。在 <see cref="Build"/> 之後呼叫。</summary>
        public void SetExpressionArt(ExpressionPanelArt art)
        {
            _exprArt = art;
            if (_exprPanel != null) { UnityEngine.Object.Destroy(_exprPanel); _exprPanel = null; }
            _exprOpen = false;
        }

        /// <summary>NOTES 面板設定變了(或開場才解析出來)→ 重新決定右下 / 右上並重排。</summary>
        public void SetPanelLayout(bool panelLeft, bool bottomDrop)
        {
            var next = GameplayChatLayout.Resolve(panelLeft, bottomDrop);
            if (next.TopAnchored == _layout.TopAnchored) return;
            _layout = next;
            ApplyLayout();
        }

        /// <summary>整個聊天框顯不顯示(結算畫面 / GAME OVER 時整組收掉)。</summary>
        public void SetVisible(bool on)
        {
            if (_visible == on) return;
            _visible = on;
            if (!on && _typing) CancelTyping();
            if (_root != null) _root.SetActive(on);
        }

        public void Destroy()
        {
            if (_typing) CancelTyping();
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
        }

        // ---- feed ----

        /// <summary>推一行進聊天框(前端已決定顏色/名字/表情圖)。超過 <see cref="GameplayChatLayout.MaxLines"/> 就吃掉最舊的。</summary>
        public void Push(GameplayChatLine line)
        {
            _lines.Add(line);
            while (_lines.Count > GameplayChatLayout.MaxLines) _lines.RemoveAt(0);
            _lastActivitySec = Time.unscaledTimeAsDouble;
            RebuildRows();
        }

        /// <summary>把歷史一次灌進來(進遊戲時把房間裡講過的話帶過來)。**不**當作「有人說話」,所以字仍然是藏著的。</summary>
        public void Seed(IReadOnlyList<GameplayChatLine> lines)
        {
            _lines.Clear();
            if (lines != null)
            {
                int from = Mathf.Max(0, lines.Count - GameplayChatLayout.MaxLines);
                for (int i = from; i < lines.Count; i++) _lines.Add(lines[i]);
            }
            RebuildRows();
        }

        // ---- per-frame ----

        public void Tick()
        {
            if (!_visible || _root == null) return;
            HandleMouse();
            if (_typing) HandleTyping();
            else if (Input.GetKeyDown(KeyCode.Tab)) BeginTyping();

            RefreshButtonArt();
            ApplyAlpha(GameplayChatFade.TextAlpha(_typing || DebugKeepText, _lastActivitySec, Time.unscaledTimeAsDouble),
                       force: false);
            TickEmojiFrames();
            TickCaret();
        }

        // ---- typing ----

        private void BeginTyping()
        {
            if (_typing) return;
            _typing = true;
            _typingReleasedFrame = -1;
            _draft = "";
            Input.imeCompositionMode = IMECompositionMode.On;
            // IME 候選視窗要跟著輸入框走(不設的話會冒在畫面左下角)。compositionCursorPos 吃的是螢幕像素。
            if (_cam != null)
            {
                var w = SdoLayout.ToWorld(_layout.BarItemX(GameplayChatLayout.EditDx),
                                          _layout.BarItemY(GameplayChatLayout.EditDy + 14f), 0f);
                var s = _cam.WorldToScreenPoint(w);
                Input.compositionCursorPos = new Vector2(s.x, s.y);
            }
            RefreshDraftLabel();
        }

        private void CancelTyping()
        {
            if (_typing) _typingReleasedFrame = Time.frameCount;   // 這一幀 Typing 仍回 true(見該屬性的註解)
            _typing = false;
            _draft = "";
            CloseExpressionPanel();
            CloseModeMenu();
            Input.imeCompositionMode = IMECompositionMode.Auto;
            RefreshDraftLabel();
        }

        private void HandleTyping()
        {
            if (Input.GetKeyDown(KeyCode.Escape)) { CancelTyping(); return; }

            string composing = Input.compositionString ?? "";
            foreach (char c in Input.inputString)
            {
                if (c == '\b') { if (_draft.Length > 0) _draft = _draft.Substring(0, _draft.Length - 1); }
                else if (c == '\n' || c == '\r') { /* 送出走下面的 Enter 分支;字元本身吃掉 */ }
                else if (c == '\t') { /* 開啟打字的那一顆 Tab 不進字串 */ }
                else if (!char.IsControl(c) && _draft.Length < DraftLimit) _draft += c;
            }
            // 中文輸入法還在組字時的 Enter 是「選字」,不是送出 —— 房間那邊也是這樣擋的。
            bool enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
            if (enter && composing.Length == 0) { Send(); return; }
            RefreshDraftLabel();
        }

        private void Send()
        {
            string txt = (_draft ?? "").Trim();
            if (txt.Length > 0)
            {
                _lastActivitySec = Time.unscaledTimeAsDouble;
                OnSend?.Invoke(txt);
            }
            CancelTyping();   // 使用者指定:送出後自動退出,馬上回到打歌
        }

        private void RefreshDraftLabel()
        {
            if (_draftLabel == null) return;
            if (!_typing) { _draftLabel.SetActive(false); if (_caret != null) _caret.enabled = false; return; }
            _draftLabel.Text = ClipToWidth(_draft + (Input.compositionString ?? ""));
            _draftLabel.SetColors(Color.white, Color.black);
            _draftLabel.SetActive(true);
        }

        /// <summary>輸入框只有 <see cref="GameplayChatLayout.EditW"/> 這麼寬:太長就砍掉開頭、留尾巴(游標那一端)。</summary>
        private string ClipToWidth(string s)
        {
            if (string.IsNullOrEmpty(s) || _draftLabel == null) return s ?? "";
            _draftLabel.Text = s;
            if (_draftLabel.MeasuredWidth <= GameplayChatLayout.EditW) return s;
            for (int cut = 1; cut < s.Length; cut++)
            {
                string t = s.Substring(cut);
                _draftLabel.Text = t;
                if (_draftLabel.MeasuredWidth <= GameplayChatLayout.EditW) return t;
            }
            return s.Substring(s.Length - 1);
        }

        private void TickCaret()
        {
            if (_caret == null) return;
            if (!_typing) { _caret.enabled = false; return; }
            bool on = ((int)(Time.unscaledTime / CaretBlinkSec) & 1) == 0;
            _caret.enabled = on;
            if (!on) return;
            float x = _layout.BarItemX(GameplayChatLayout.EditDx) + (_draftLabel != null ? _draftLabel.MeasuredWidth : 0f);
            SdoLayout.PlaceBox(_caret, x + 1f, _layout.BarItemY(GameplayChatLayout.EditDy + 1f), 2f, 12f, -4f);
        }

        // ---- mouse ----

        private void HandleMouse()
        {
            if (_cam == null) return;
            var w = _cam.ScreenToWorldPoint(Input.mousePosition);
            var p = new Vector2(w.x, w.y);

            _hover = Hit(_modeBtn, p) ? 0 : Hit(_sendBtn, p) ? 1 : Hit(_exprBtn, p) ? 2 : -1;
            if (!Input.GetMouseButtonDown(0)) return;

            if (_hover >= 0) { _pressed = _hover; _pressedAt = Time.unscaledTime; }
            if (_hover == 0) { ToggleModeMenu(); return; }
            if (_hover == 1) { if (_typing) Send(); else BeginTyping(); return; }
            if (_hover == 2) { ToggleExpressionPanel(); return; }
            // 展開中的面板優先吃點擊(它們蓋在訊息區上)
            if (_modeMenuOpen && TryClickModeMenu(p)) return;
            if (_exprOpen && TryClickExpressionPanel(p)) return;
            // 點在輸入框那一段 → 進打字模式(跟房間點左下輸入框一樣)
            if (!_typing && HitEditBox(p)) BeginTyping();
        }

        private bool HitEditBox(Vector2 p)
        {
            float x0 = _layout.BarItemX(GameplayChatLayout.EditDx) - 4f;
            float x1 = x0 + GameplayChatLayout.EditW + 8f;
            float y0 = _layout.BarItemY(0f), y1 = y0 + GameplayChatLayout.BarH;
            return p.x >= SdoLayout.WorldX(x0) && p.x <= SdoLayout.WorldX(x1)
                && p.y <= SdoLayout.WorldY(y0) && p.y >= SdoLayout.WorldY(y1);
        }

        private static bool Hit(SpriteRenderer sr, Vector2 p)
        {
            if (sr == null || !sr.enabled || sr.sprite == null) return false;
            var b = sr.bounds;
            return p.x >= b.min.x && p.x <= b.max.x && p.y >= b.min.y && p.y <= b.max.y;
        }

        // ---- chatmode 彈出選單(家族 / 好友 / 當前 / 回復) ----
        // 跟房間一樣是「按下去展開一列」,不是點一下換一個 —— 玩家要能直接挑,而不是猜自己按了幾下。

        private void ToggleModeMenu()
        {
            if (_modeMenuOpen) { CloseModeMenu(); return; }
            CloseExpressionPanel();
            BuildModeMenu();
            _modeMenuOpen = true;
            _modeMenu.SetActive(true);
        }

        private void CloseModeMenu()
        {
            _modeMenuOpen = false;
            if (_modeMenu != null) _modeMenu.SetActive(false);
        }

        private void BuildModeMenu()
        {
            if (_modeMenu != null) { PlaceModeMenu(); return; }
            _modeMenu = new GameObject("ChatModeMenu");
            _modeMenu.transform.SetParent(_root.transform, false);
            for (int i = 0; i < ChannelMenu.Length; i++)
            {
                var btn = new GameObject("mode" + ChannelMenu[i]).AddComponent<SpriteRenderer>();
                btn.transform.SetParent(_modeMenu.transform, false);
                btn.sortingOrder = OrderPanelIcon;
                SetSprite(btn, Art(ModeArt[ChannelMenu[i]][0]));
                _modeMenuBtns.Add(btn);
            }
            PlaceModeMenu();
        }

        private void PlaceModeMenu()
        {
            if (_modeMenu == null) return;
            // 選單框底邊貼齊底板;框內四顆用官方 ROOMPOPMENU 的槽位(2 / 27 / 52 / 77),
            // 左緣(框內 x=2)剛好切齊條上那顆 chatmode 鈕。
            // 每顆再扣掉它自己圖裡被 cleanMatte 削掉的透明上緣(ModeArtTopPad),四顆才會**貼齊**。
            float top = _layout.PopupTopY(GameplayChatLayout.ModeMenuH);
            float x = _layout.BarItemX(GameplayChatLayout.ModeBtnDx) - GameplayChatLayout.ModeMenuSlotX;
            for (int i = 0; i < _modeMenuBtns.Count; i++)
            {
                int ch = ChannelMenu[i];
                float pad = ch < GameplayChatLayout.ModeArtTopPad.Length
                    ? GameplayChatLayout.ModeArtTopPad[ch] : 0f;
                SdoLayout.PlaceTopLeft(_modeMenuBtns[i], x + GameplayChatLayout.ModeMenuSlotX,
                                       top + GameplayChatLayout.ModeMenuSlotY[i] - pad, -6f);
            }
        }

        private bool TryClickModeMenu(Vector2 p)
        {
            for (int i = 0; i < _modeMenuBtns.Count; i++)
                if (Hit(_modeMenuBtns[i], p))
                {
                    _channel = ChannelMenu[i];
                    CloseModeMenu();
                    OnChannel?.Invoke(_channel);
                    return true;
                }
            return false;
        }

        // ---- 表情面板(官方 ROOMPOPMENU / EXPRESSIONINFO 的綠框,與房間同一組素材) ----

        private void ToggleExpressionPanel()
        {
            if (_exprArt == null || _exprArt.Pages == null || _exprArt.Pages.Length == 0) return;
            if (_exprOpen) { CloseExpressionPanel(); return; }
            CloseModeMenu();
            BuildExpressionPanel();
            _exprOpen = true;
            _exprPanel.SetActive(true);
        }

        private void CloseExpressionPanel()
        {
            _exprOpen = false;
            if (_exprPanel != null) _exprPanel.SetActive(false);
        }

        private void BuildExpressionPanel()
        {
            if (_exprPanel != null) { RefreshExpressionPage(); return; }
            _exprPanel = new GameObject("ChatExprPanel");
            _exprPanel.transform.SetParent(_root.transform, false);

            _exprBg = NewPanelSR(_exprPanel, "ExpBg", OrderPanel);
            _exprTab = NewPanelSR(_exprPanel, "NormalExp", OrderPanelIcon);
            SetSprite(_exprTab, _exprArt.Tab);
            // 🔴 **不畫格子裡的表情**:EXPRESSIONINFO 那張底圖本身就已經把 24 個表情畫好了,
            //    再疊一層 SmallFrames 上去就會變成兩層重影(使用者回報「emoji 面板有殘影」)。
            //    房間也是這樣做的 —— 它的格子是 alpha 0.001 的透明命中區,只負責吃點擊。
            //    這裡連物件都不建,點擊直接用格子的矩形算(見 ExprCellRect)。
            _exprArrowL = NewPanelSR(_exprPanel, "preexp", OrderPanelIcon);
            _exprArrowR = NewPanelSR(_exprPanel, "nextexp", OrderPanelIcon);
            _exprPageLabel = NewLabel("ExprPage", OrderPanelText);
            _exprPageLabel.root.transform.SetParent(_exprPanel.transform, true);

            RefreshExpressionPage();
        }

        /// <summary>把目前這一頁的底圖/表情/頁碼畫上去,並依版位(右下/右上)重新擺放整塊面板。</summary>
        private void RefreshExpressionPage()
        {
            if (_exprPanel == null || _exprArt == null) return;
            int pages = Mathf.Max(1, _exprArt.Pages.Length);
            _exprPage = Mathf.Clamp(_exprPage, 0, pages - 1);
            var page = _exprArt.Pages[_exprPage];

            float x0 = _layout.ExprPanelX;                                   // 右緣切齊表情鈕
            float y0 = _layout.PopupTopY(GameplayChatLayout.ExprPanelH);     // 底邊貼齊底板

            var bg = _exprArt.PageBackgrounds;
            SetSprite(_exprBg, bg != null && bg.Length > 0 ? bg[Mathf.Min(_exprPage, bg.Length - 1)] : null);
            SdoLayout.PlaceTopLeft(_exprBg, x0, y0 + ExprBgY, -5f);
            SdoLayout.PlaceTopLeft(_exprTab, x0 + ExprTabX, y0 + ExprTabY, -6f);

            // 格子不畫任何東西(表情已經在底圖裡),只記住面板原點供命中計算。
            _exprOriginX = x0; _exprOriginY = y0;

            var al = _exprArt.ArrowLeft; var ar = _exprArt.ArrowRight;
            SetSprite(_exprArrowL, al != null && al.Length > 0 ? al[0] : null);
            SetSprite(_exprArrowR, ar != null && ar.Length > 0 ? ar[0] : null);
            SdoLayout.PlaceBox(_exprArrowL, x0 + ExprArrowLX, y0 + ExprArrowY, ExprArrowSize, ExprArrowSize, -6f);
            SdoLayout.PlaceBox(_exprArrowR, x0 + ExprArrowRX, y0 + ExprArrowY, ExprArrowSize, ExprArrowSize, -6f);

            _exprPageLabel.Text = (_exprPage + 1) + " / " + pages;
            _exprPageLabel.SetColors(ExprPageColor, new Color(1f, 1f, 1f, 0f));   // 官方頁碼是純桃紅字,沒有描邊
            _exprPageLabel.Position = SdoLayout.ToWorld(x0 + 118f, y0 + ExprArrowY + 8f, -7f);
            _exprPageLabel.SetActive(true);
        }

        /// <summary>ROOMPOPMENU 的頁碼顏色 0xffbb2077。</summary>
        private static readonly Color ExprPageColor = new Color32(0xBB, 0x20, 0x77, 0xFF);

        /// <summary>第 <paramref name="slot"/> 格的命中矩形(design 座標,左上角 + 邊長)。格子本身不畫任何東西。</summary>
        private void ExprCellRect(int slot, out float x, out float y)
        {
            x = _exprOriginX + ExprCellX0 + (slot % ExprCols) * ExprCellStep;
            y = _exprOriginY + ExprCellY0 + (slot / ExprCols) * ExprCellStep;
        }

        private bool TryClickExpressionPanel(Vector2 p)
        {
            if (Hit(_exprArrowL, p)) { StepExpressionPage(-1); return true; }
            if (Hit(_exprArrowR, p)) { StepExpressionPage(+1); return true; }
            var page = _exprArt.Pages[_exprPage];
            int n = page != null ? Mathf.Min(page.Length, ExprCols * ExprRows) : 0;
            for (int i = 0; i < n; i++)
            {
                if (page[i].Id <= 0) continue;
                ExprCellRect(i, out float cx, out float cy);
                if (p.x >= SdoLayout.WorldX(cx) && p.x <= SdoLayout.WorldX(cx + ExprCellSize)
                 && p.y <= SdoLayout.WorldY(cy) && p.y >= SdoLayout.WorldY(cy + ExprCellSize))
                {
                    InsertExpression(page[i]);
                    CloseExpressionPanel();
                    return true;
                }
            }
            // 點在面板底圖上(格子之間的空隙)也算吃掉,不要穿透去點到後面的輸入框
            return Hit(_exprBg, p);
        }

        private void StepExpressionPage(int delta)
        {
            int pages = Mathf.Max(1, _exprArt.Pages.Length);
            _exprPage = ((_exprPage + delta) % pages + pages) % pages;
            RefreshExpressionPage();
        }

        /// <summary>點表情 → **把指令塞進輸入框**(跟房間一樣),不是直接送出 —— 這樣可以在表情後面接著打字。</summary>
        private void InsertExpression(Expression e)
        {
            string cmd = string.IsNullOrEmpty(e.Command) ? "" : e.Command;
            if (cmd.Length == 0) { OnExpression?.Invoke(e.Id); return; }   // 沒有指令文字才退回直接送
            if (!_typing) BeginTyping();
            string draft = _draft ?? "";
            if (draft.Length > 0 && !draft.EndsWith(" ")) draft += " ";
            _draft = (draft + cmd + " ");
            if (_draft.Length > DraftLimit) _draft = _draft.Substring(0, DraftLimit);
            RefreshDraftLabel();
        }

        // ---- rows ----

        private void RebuildRows()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                int src = _lines.Count - 1 - i;   // i = 由新到舊
                var row = _rows[i];
                row.has = src >= 0;
                if (!row.has) { row.frames = null; row.go.SetActive(false); continue; }
                ApplyRow(row, _lines[src], i);
            }
            _alpha = -1f;   // 下一次 ApplyAlpha 一定重套(新行不要沿用上一輪的 alpha)
        }

        private void ApplyRow(Row row, GameplayChatLine line, int idxFromNewest)
        {
            row.face = ParseHex(line.ColorHex);
            float top = _layout.LineTopY(idxFromNewest, _lines.Count);
            float midY = top + GameplayChatLayout.LineH * 0.5f;
            float x = GameplayChatLayout.ListX;

            var frames = line.ExpressionFrames;
            bool hasEmoji = frames != null && frames.Length > 0;
            row.frames = hasEmoji ? frames : null;

            if (!hasEmoji)
            {
                row.head.Text = line.PlainText();
                row.head.Position = SdoLayout.ToWorld(x, midY, -4f);
                row.hasTail = false;
                row.emoji.enabled = false;
                return;
            }

            // 名字 + 表情前的字 → 表情小圖 → 表情後的字(照使用者輸入時 emoji 的位置排)
            string head = line.Name ?? "";
            string lead = line.Lead ?? "";
            if (lead.Length > 0) head = head.Length > 0 ? head + " " + lead : lead;
            row.head.Text = head;
            row.head.Position = SdoLayout.ToWorld(x, midY, -4f);

            float cursor = x + (head.Length > 0 ? row.head.MeasuredWidth + 3f : 0f);
            row.emoji.enabled = true;
            SetSprite(row.emoji, frames[0]);
            SdoLayout.PlaceBox(row.emoji, cursor, midY - EmojiPx * 0.5f, EmojiPx, EmojiPx, -4f);
            cursor += EmojiPx + 3f;

            string tail = (line.Body ?? "").Trim();
            row.hasTail = tail.Length > 0;
            if (row.hasTail)
            {
                row.tail.Text = tail;
                row.tail.Position = SdoLayout.ToWorld(cursor, midY, -4f);
            }
        }

        private void TickEmojiFrames()
        {
            if (_alpha <= 0f) return;
            int f = (int)(Time.unscaledTime * 8f);
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row.frames == null || !row.emoji.enabled) continue;
                SetSprite(row.emoji, row.frames[f % row.frames.Length]);
            }
        }

        private void ApplyAlpha(float a, bool force)
        {
            if (!force && Mathf.Approximately(a, _alpha)) return;
            _alpha = a;
            bool on = a > 0.001f;
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                bool show = row.has && on;
                if (row.go.activeSelf != show) row.go.SetActive(show);
                if (!show) continue;
                var face = row.face; face.a = a;
                var edge = new Color(0f, 0f, 0f, a);
                row.head.SetColors(face, edge);
                row.head.SetActive(true);
                row.tail.SetColors(face, edge);
                row.tail.SetActive(row.hasTail);
                var c = row.emoji.color; c.a = a; row.emoji.color = c;
            }
        }

        // ---- helpers ----

        private void RefreshButtonArt()
        {
            bool flash = _pressed >= 0 && Time.unscaledTime - _pressedAt < PressFlashSec;
            if (!flash) _pressed = -1;
            int State(int id) => _pressed == id ? 2 : _hover == id ? 1 : 0;

            int ch = Mathf.Clamp(_channel, 0, ModeArt.Length - 1);
            var art = ModeArt[ch];
            if (_modeBtn != null)
            {
                SetSprite(_modeBtn, Art(art[State(0)]));
                // 扣掉這張圖自己的透明上緣 —— 「當前」(30 高)與其餘三張(25 高)的視覺上緣位置不同,
                // 不補這一下,切頻道的瞬間按鈕會在條上跳 2px。
                float pad = ch < GameplayChatLayout.ModeArtTopPad.Length
                    ? GameplayChatLayout.ModeArtTopPad[ch] : 0f;
                SdoLayout.PlaceTopLeft(_modeBtn, _layout.BarItemX(GameplayChatLayout.ModeBtnDx),
                                       _layout.BarItemY(GameplayChatLayout.ModeBtnDy) - pad, -1f);
            }
            if (_sendBtn != null)
            {
                int s = State(1);
                SetSprite(_sendBtn, Art(s == 2 ? "GamePlay7.an" : s == 1 ? "GamePlay6.an" : "GamePlay5.an"));
                SdoLayout.PlaceTopLeft(_sendBtn, _layout.BarItemX(GameplayChatLayout.SendBtnDx),
                                       _layout.BarItemY(GameplayChatLayout.SendBtnDy), -1f);
            }
            if (_exprBtn != null)
            {
                int s = State(2);
                SetSprite(_exprBtn, Art(s == 2 ? "Room71.an" : s == 1 ? "Room70.an" : "Room69.an"));
                SdoLayout.PlaceTopLeft(_exprBtn, _layout.BarItemX(GameplayChatLayout.ExprBtnDx),
                                       _layout.BarItemY(GameplayChatLayout.ExprBtnDy), -1f);
            }
        }

        private void ApplyLayout()
        {
            // 底板**原生尺寸**擺放(GamePlay3.an = 255×38,ppu 1,素材本身就是滿版):PlaceTopLeft 不動 scale,
            // 所以圓角/邊框粗細不可能被拉扁。不要改用 PlaceBox —— 那條路會把 sprite 縮放到指定尺寸,
            // 素材尺寸只要有一點出入(pad / de-matte 重裁)整條就會變形。
            if (_bar != null) SdoLayout.PlaceTopLeft(_bar, GameplayChatLayout.BarX, _layout.BarY, 0f);
            if (_barTail != null) SdoLayout.PlaceTopLeft(_barTail, GameplayChatLayout.BarTailX, _layout.BarY, 0f);
            RefreshButtonArt();
            if (_draftLabel != null)
                _draftLabel.Position = SdoLayout.ToWorld(_layout.BarItemX(GameplayChatLayout.EditDx),
                                                        _layout.BarItemY(GameplayChatLayout.EditDy + 7f), -4f);
            if (_exprPanel != null) RefreshExpressionPage();
            if (_modeMenu != null) PlaceModeMenu();
            RebuildRows();
        }

        private SpriteRenderer NewPanelSR(GameObject parent, string name, int order)
        {
            var sr = new GameObject(name).AddComponent<SpriteRenderer>();
            sr.transform.SetParent(parent.transform, false);
            sr.sortingOrder = order;
            return sr;
        }

        private Row NewRow(int i)
        {
            var go = new GameObject("ChatRow" + i);
            go.transform.SetParent(_root.transform, false);
            var row = new Row { go = go };
            row.head = NewLabel("head", OrderText);
            row.tail = NewLabel("tail", OrderText);
            row.head.root.transform.SetParent(go.transform, true);
            row.tail.root.transform.SetParent(go.transform, true);
            row.emoji = new GameObject("emoji").AddComponent<SpriteRenderer>();
            row.emoji.transform.SetParent(go.transform, false);
            row.emoji.sortingOrder = OrderText;
            row.emoji.enabled = false;
            go.SetActive(false);
            return row;
        }

        private Label3D NewLabel(string name, int order)
        {
            var l = new Label3D(name, TextStyles.CjkFont(), Color.white, Color.black, EdgeCross, order, TextPx,
                                TextAnchor.MiddleLeft, 0, -4f);
            l.root.transform.SetParent(_root.transform, true);
            return l;
        }

        private SpriteRenderer NewSR(string name, Sprite spr, int order)
        {
            var sr = new GameObject(name).AddComponent<SpriteRenderer>();
            sr.transform.SetParent(_root.transform, false);
            if (_defaultSpriteMat == null) _defaultSpriteMat = sr.sharedMaterial;   // 引擎的 Sprites-Default
            sr.sortingOrder = order;
            SetSprite(sr, spr);
            return sr;
        }

        private static Material _defaultSpriteMat;

        /// <summary>
        /// 指派圖並**挑對材質**。PLAYNEWLEAN 那組(底板/三顆鈕)是 premultiplied 貼圖 —— 必須配
        /// <c>Sdo/SpritePremultiply</c>,否則邊緣外那圈 (255,255,255,0) 會在放大取樣時被拌進來,四周浮一層白。
        /// 前端送來的表情面板素材是一般 straight-alpha,維持引擎預設材質(跟房間畫出來的一樣)。
        ///
        /// 🔴 材質是 <b>per-texture</b> 的(<see cref="SdoExtracted.PremultSpriteMaterial"/>):同一個 Material 實例
        /// 餵給畫不同貼圖的 SpriteRenderer,它們會全部取樣到最後寫進去的那一張(見該屬性的說明)。
        /// </summary>
        private static void SetSprite(SpriteRenderer sr, Sprite spr)
        {
            if (sr == null) return;
            sr.sprite = spr;
            var tex = spr != null ? spr.texture : null;
            var mat = tex != null && SdoExtracted.IsPremultTexture(tex)
                ? SdoExtracted.PremultSpriteMaterial(tex)
                : null;
            sr.sharedMaterial = mat ?? _defaultSpriteMat;   // shader 被 strip → 退回預設(會偏暗,但不會不見)
        }

        // PLAYNEWLEAN 的 .an → sprite,**一定要快取**:按鈕圖是每幀依 hover 狀態指派的,
        // 而 SdoExtracted 每次呼叫都會 Sprite.Create 一顆新的(貼圖本身有快取,sprite 沒有)。
        private static readonly Dictionary<string, Sprite> ArtCache = new Dictionary<string, Sprite>();
        private static Sprite Art(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return null;
            if (ArtCache.TryGetValue(anName, out var s) && s != null) return s;
            s = SdoExtracted.ChatArt(anName);
            ArtCache[anName] = s;
            return s;
        }

        private static Sprite _white;
        private static Sprite WhitePixel()
        {
            if (_white != null) return _white;
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, Color.white);
            t.Apply();
            _white = Sprite.Create(t, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            return _white;
        }

        private static Color ParseHex(string hex)
        {
            if (!string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString("#" + hex, out var c)) return c;
            return Color.white;
        }
    }
}
