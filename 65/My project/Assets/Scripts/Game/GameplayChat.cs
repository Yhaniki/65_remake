using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 遊戲畫面的聊天框(官方 <c>GAMEPLAYNEWLEAN.XML</c> 的 <c>winchat</c>):右下角一條常駐的
    /// 「当前 ▸ 輸入框 ▸ 送出 ▸ 表情」,旁邊浮著最近幾行訊息。
    ///
    /// 跟房間左下角那個聊天欄的差別:
    ///   • **文字會自己藏起來** —— 進場預設不顯示,有人講話才整批冒出來,之後 10 秒沒人說話就直接消失
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
    /// <see cref="Typing"/> 為 true,<c>ScreenGameplay</c> 據此停掉功能熱鍵(F8/速度/相機/中離…)。
    /// 🔴 **lane 鍵不停** —— 使用者指定「不要擋,就是都要」:打字中每一顆 lane 鍵照樣判定,所以字母鍵位
    /// 打「w」會同時進草稿又踩到上鍵。中文輸入法只在打字期間才開(<see cref="Input.imeCompositionMode"/>),
    /// IME 組字時 ASWD 會被吃掉是輸入法自己的行為,不是這裡擋的。
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

        /// <summary>DEV(SDO_CHATDEMO):訊息文字不自動消失 —— 正常玩的時候字只在有人說話後的 10 秒內看得到,
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
        // 訊息行裡的表情小圖邊長。素材是 24×24 但**內容只佔中間 ~18**(四周留了 2~3px 透明),所以框要開得比
        // 行高(13)大,畫出來才跟旁邊的字一樣醒目 —— 13 的框只剩約 10px 的圖,使用者回報「聊天區內 emoji 太小個」。
        // 20 的框 → 視覺約 15px,略大於字的 em 盒(≈12.7);上下各溢出行高 3.5px,表情行本來就只有這一個圖,不會壓到字。
        private const float EmojiPx = 20f;
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
        private Label3D _measureLabel;   // 只拿來量字寬(不顯示):推訊息進來時先算它要折成幾列
        private SpriteRenderer _caret;
        private SpriteRenderer _composeLine;   // IME 組字底線(還沒選字上屏的那一段)

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
        private double _lastActivitySec = -1; // 最後一則訊息 / 最後一次送出 → 文字何時消失
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
            public string whisperTarget;      // 點名字要密語誰(null = 這行的名字不可點)
            public float nameX0, nameX1;      // 名字欄在 design 座標的水平範圍(命中用)
            public float nameY0, nameY1;      // 同上,垂直
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
            // 量字寬專用的隱形 label:設定與訊息列的 head 一模一樣(同字級/同字型),量出來才等於實際排出來的寬度。
            // 不共用 _draftLabel —— 那顆的字是使用者正在打的草稿,拿去當量尺會把畫面上的草稿洗掉。
            _measureLabel = NewLabel("ChatMeasure", OrderText);
            _measureLabel.SetActive(false);
            _caret = NewSR("ChatCaret", WhitePixel(), OrderText);
            _caret.enabled = false;
            // 組字底線:這裡的字是 legacy TextMesh(Label3D),富文字沒有 <u> 可用 → 自己擺一條 1px 白線。
            _composeLine = NewSR("ChatComposeLine", WhitePixel(), OrderText);
            _composeLine.enabled = false;

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
            AddWrapped(line);
            TrimLines();
            _lastActivitySec = Time.unscaledTimeAsDouble;
            RebuildRows();
        }

        /// <summary>把歷史一次灌進來(進遊戲時把房間裡講過的話帶過來)。**不**當作「有人說話」,所以字仍然是藏著的。</summary>
        public void Seed(IReadOnlyList<GameplayChatLine> lines)
        {
            _lines.Clear();
            if (lines != null)
            {
                // 一則訊息可能折成好幾列,但至少一列 → 取最後 MaxLines 則就一定夠填滿畫面(多的由 TrimLines 砍掉)。
                int from = Mathf.Max(0, lines.Count - GameplayChatLayout.MaxLines);
                for (int i = from; i < lines.Count; i++) AddWrapped(lines[i]);
            }
            TrimLines();
            RebuildRows();
        }

        private void TrimLines()
        {
            while (_lines.Count > GameplayChatLayout.MaxLines) _lines.RemoveAt(0);
        }

        /// <summary>
        /// 把一則訊息折成 1~N 個**顯示列**再放進 <see cref="_lines"/>。
        ///
        /// 這條聊天列沒有自動折行(一列一顆 TextMesh),長訊息以前就是直接畫到欄寬外面去。折成好幾列之後,
        /// 原本的排版(<c>LineTopY</c>)、淡出、14 列上限全部照舊 —— 續列就是「沒有名字的一般行」。
        /// 名字只留在第一列(密語點擊的方框也只掛在那一列,跟房間左下角的行為一致)。
        /// </summary>
        private void AddWrapped(GameplayChatLine line)
        {
            float full = GameplayChatLayout.ListW;
            var frames = line.ExpressionFrames;
            bool hasEmoji = frames != null && frames.Length > 0;

            if (_measureLabel == null) { _lines.Add(line); return; }   // 還沒 Build → 照舊(不折)

            if (!hasEmoji)
            {
                string s = line.PlainText();
                var parts = WrapToWidth(s, full, full);
                if (parts.Count <= 1) { _lines.Add(line); return; }
                string name = line.Name ?? "";
                for (int i = 0; i < parts.Count; i++)
                {
                    // 第一列保留名字(＝密語點擊範圍);PlainText() 會把 名字 + " " + 內容 接回原樣。
                    if (i == 0 && name.Length > 0 && parts[0].StartsWith(name, StringComparison.Ordinal))
                        _lines.Add(new GameplayChatLine
                        {
                            Name = name,
                            Body = parts[0].Substring(name.Length).TrimStart(),
                            ColorHex = line.ColorHex,
                            WhisperTarget = line.WhisperTarget,
                        });
                    else
                        _lines.Add(new GameplayChatLine { Body = parts[i], ColorHex = line.ColorHex });
                }
                return;
            }

            // 有表情圖:名字 + 表情前的字 + 小圖固定留在第一列,只有表情後面那段字要折。
            // 第一列剩下的寬 = 欄寬 − 名字/前字 − 小圖(排法見 ApplyRow)。
            string head = line.Name ?? "";
            string lead = line.Lead ?? "";
            if (lead.Length > 0) head = head.Length > 0 ? head + " " + lead : lead;
            float headW = head.Length > 0 ? MeasureWidth(head) + 3f : 0f;
            float firstW = Mathf.Max(EmojiPx, full - headW - EmojiPx - 3f);

            var tailParts = WrapToWidth((line.Body ?? "").Trim(), firstW, full);
            var firstLine = line;                       // struct → 這是複本,原本那份不動
            firstLine.Body = tailParts[0];
            _lines.Add(firstLine);
            for (int i = 1; i < tailParts.Count; i++)
                _lines.Add(new GameplayChatLine { Body = tailParts[i], ColorHex = line.ColorHex });
        }

        /// <summary>用量尺 label 把字切成幾段(寬度單位 = design px,同 <see cref="GameplayChatLayout.ListW"/>)。</summary>
        private List<string> WrapToWidth(string s, float firstWidth, float restWidth)
        {
            _measureLabel.Text = s ?? "";
            return ChatTextWrap.Wrap(s, _measureLabel.PrefixWidth, firstWidth, restWidth);
        }

        private float MeasureWidth(string s)
        {
            _measureLabel.Text = s ?? "";
            return _measureLabel.MeasuredWidth;
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
            if (!_typing)
            {
                _draftLabel.SetActive(false);
                if (_caret != null) _caret.enabled = false;
                if (_composeLine != null) _composeLine.enabled = false;
                return;
            }
            string composing = Input.compositionString ?? "";
            string shown = ClipToWidth(_draft + composing);
            _draftLabel.Text = shown;
            _draftLabel.SetColors(Color.white, Color.black);
            _draftLabel.SetActive(true);
            UpdateComposeLine(shown, composing);
        }

        /// <summary>
        /// 還沒選字上屏的那一段畫底線 —— 與房間左下/大廳的輸入框(TMP 自己包 <c>&lt;u&gt;</c>)、
        /// 房間頭上打字泡同一種回饋;這裡的字是 legacy <c>TextMesh</c>,只能自己擺線(見 <see cref="ImeCompose"/>)。
        /// 線的兩端就用 <c>Label3D</c> 量出來的字寬,和游標(<see cref="TickCaret"/>)走同一把尺 → 對得上。
        /// </summary>
        private void UpdateComposeLine(string shown, string composing)
        {
            if (_composeLine == null || _draftLabel == null) return;
            int start = ImeCompose.ShownStart(shown != null ? shown.Length : 0,
                                              composing != null ? composing.Length : 0);
            int len = (shown != null ? shown.Length : 0) - start;
            if (len <= 0) { _composeLine.enabled = false; return; }
            float x0 = _draftLabel.PrefixWidth(start);
            float w = _draftLabel.MeasuredWidth - x0;
            if (w <= 0.5f) { _composeLine.enabled = false; return; }
            _composeLine.enabled = true;
            // y = 字的下緣(游標從 EditDy+1 站到 +13);線壓在那條下面一點,不吃到字。
            SdoLayout.PlaceBox(_composeLine, _layout.BarItemX(GameplayChatLayout.EditDx) + x0,
                               _layout.BarItemY(GameplayChatLayout.EditDy + 13f), w, 1f, -4f);
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
            // 點訊息列的名字 → 密語那個人(同房間左下角聊天列)
            if (TryClickWhisperName(p)) return;
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
            // 選單直接接在條上那顆 chatmode 鈕的上方(ModeMenuTopY),與它連成等距的一柱五顆;
            // 框內四顆用官方 ROOMPOPMENU 的槽位(2 / 27 / 52 / 77 —— 節距一律 25),
            // 左緣(框內 x=2)剛好切齊條上那顆。
            // 槽位是照 49×25 那三張定的,所以每顆要再扣掉自己圖裡多留的透明邊(ModeArtTopPad/LeftPad)
            // —— 只有「當前」非零 —— 四顆才會等距、切齊。
            float top = _layout.ModeMenuTopY;
            float x = _layout.BarItemX(GameplayChatLayout.ModeBtnDx) - GameplayChatLayout.ModeMenuSlotX;
            for (int i = 0; i < _modeMenuBtns.Count; i++)
            {
                GameplayChatLayout.ModeArtTopLeft(ChannelMenu[i],
                                                  x + GameplayChatLayout.ModeMenuSlotX,
                                                  top + GameplayChatLayout.ModeMenuSlotY[i],
                                                  out float bx, out float by);
                SdoLayout.PlaceTopLeft(_modeMenuBtns[i], bx, by, -6f);
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
            _draft = ChatDraft.WithExpression(_draft, cmd, DraftLimit);
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
                SetNameHitBox(row, line, x, top);
                return;
            }

            // 名字 + 表情前的字 → 表情小圖 → 表情後的字(照使用者輸入時 emoji 的位置排)
            string head = line.Name ?? "";
            string lead = line.Lead ?? "";
            if (lead.Length > 0) head = head.Length > 0 ? head + " " + lead : lead;
            row.head.Text = head;
            row.head.Position = SdoLayout.ToWorld(x, midY, -4f);
            SetNameHitBox(row, line, x, top);

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

        /// <summary>
        /// 記住這一行**名字欄**的方框,點下去就密語那個人(＝房間左下角聊天列點名字的同一件事;那邊靠 TMP 的
        /// <c>&lt;link&gt;</c> + raycast,遊戲畫面沒有 UGUI 可用,只能自己記範圍再比對滑鼠 —— 見類別註解)。
        ///
        /// 名字是 head 那顆 Label3D 的**前綴**(後面接的是表情前的字),所以用 <see cref="Label3D.PrefixWidth"/>
        /// 量到冒號為止;整串一起排的 tracking 也算進去了,量出來就是畫面上那一段。
        /// </summary>
        private static void SetNameHitBox(Row row, GameplayChatLine line, float x, float top)
        {
            row.whisperTarget = null;
            if (string.IsNullOrEmpty(line.WhisperTarget) || string.IsNullOrEmpty(line.Name)) return;
            float w = row.head.PrefixWidth(line.Name.Length);
            if (w <= 0f) return;
            row.whisperTarget = line.WhisperTarget;
            row.nameX0 = x;
            row.nameX1 = x + w;
            row.nameY0 = top;
            row.nameY1 = top + GameplayChatLayout.LineH;
        }

        /// <summary>點到某一行的名字 → 把 <c>[名字] </c> 塞進輸入框(沿用已打的內容,舊的 [名字] 前綴換掉),
        /// 跟房間的 <c>InsertWhisperTarget</c> 一樣。字沒顯示的時候(淡出後)不可點 —— 看不見的東西不該吃點擊。</summary>
        private bool TryClickWhisperName(Vector2 p)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                // activeSelf = ApplyAlpha 判定「這一行現在畫得出來」(訊息淡掉之後整列是關的)
                if (!row.has || row.whisperTarget == null || !row.go.activeSelf) continue;
                if (p.x < SdoLayout.WorldX(row.nameX0) || p.x > SdoLayout.WorldX(row.nameX1)) continue;
                if (p.y > SdoLayout.WorldY(row.nameY0) || p.y < SdoLayout.WorldY(row.nameY1)) continue;
                InsertWhisperTarget(row.whisperTarget);
                return true;
            }
            return false;
        }

        /// <summary>把 <c>[名字] </c> 放到輸入框最前面。已經有 <c>[舊名字]</c> 就換掉,只留使用者打的內容。</summary>
        public void InsertWhisperTarget(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!_typing) BeginTyping();
            _draft = ChatDraft.WithWhisperTarget(_draft, name);
            if (_draft.Length > DraftLimit) _draft = _draft.Substring(0, DraftLimit);
            RefreshDraftLabel();
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
                // 版面給的是**視覺**左上角(568,＝官方 friendchatmode/Familychatmode 的 y);再依這張圖自己多留的
                // 透明邊換回 sprite 左上角 —— 「當前」那張因此落在官方 chatmode 的 565。不補這一下,切頻道的
                // 瞬間按鈕會在條上跳 3px。
                GameplayChatLayout.ModeArtTopLeft(ch,
                                                  _layout.BarItemX(GameplayChatLayout.ModeBtnDx),
                                                  _layout.BarItemY(GameplayChatLayout.ModeBtnDy),
                                                  out float mx, out float my);
                SdoLayout.PlaceTopLeft(_modeBtn, mx, my, -1f);
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
