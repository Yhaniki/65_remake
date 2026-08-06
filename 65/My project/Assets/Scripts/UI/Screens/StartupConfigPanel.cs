using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Sdo.Localization;
using Sdo.Settings;

namespace Sdo.UI.Screens
{
    /// <summary>
    /// 開場（選性別）畫面左側那塊面板 —— 原本只有「玩家名稱」一個小框，現在整塊撐大成
    /// 「角色左邊、性別核取方塊上面」那一整片空白區（設計座標 800×600 下的 x 20..316 / y 10..512），
    /// 底下掛上 <see cref="StartupConfigSchema"/> 那張表：**config.ini 裡遊戲內沒有其它 UI 可以改的設定全集**，
    /// 依 連線 / 遊玩 / 歌曲 / 顯示 / MMD 分頁，上面一排 tab 切換。右上角一顆鈕展開/收合（預設收合＝只剩標題列＋名稱列，
    /// 跟改版前看起來一樣大）。
    ///
    /// 走 IMGUI（同原本的名稱小框與譜面編輯器）：這裡要的是可捲動清單、可打字的欄位與滑桿，IMGUI 直接就有。
    /// 版面用**設計像素**寫（800×600），畫之前把 <c>GUI.matrix</c> 縮放到 4:3 內容區 → 不管視窗多大，面板都貼在
    /// 背景圖的同一個位置（IMGUI 本身是螢幕像素，不縮的話全螢幕時會縮到左上角一小坨）。
    ///
    /// 值只在按「儲存設定」時才落地（<see cref="StartupConfigSchema.ApplyAndSave"/> → config.ini）；
    /// 文字欄位打到一半的內容放在 <see cref="_edit"/> 暫存，才不會因為「12」中途變成空字串就被 parse 回舊值。
    /// </summary>
    public sealed class StartupConfigPanel
    {
        // ---- 版面（設計像素，800×600 左上原點）----
        public const float PanelX = 20f, PanelY = 10f, PanelW = 296f;
        /// <summary>收合高度：標題列＋名稱列（≒改版前那個小框）。</summary>
        public const float CollapsedH = 68f;
        /// <summary>展開高度：下緣 512，剛好停在男/女核取方塊（y=530）上面，也不碰到右邊的角色。</summary>
        public const float ExpandedH = 502f;
        public const float RowH = 18f, HelpH = 52f, Pad = 5f;
        private const float LabelW = 104f, ValueW = 54f;
        private const float ValueEntryW = 40f, UnitW = 17f;
        /// <summary>「名稱」「體型」那兩列左邊標籤欄的寬度。兩列共用同一個值才會對齊 —— 而且要放得下
        /// 最長的語言（中文兩個字 24px、英文 Name/Build 約 30px）。</summary>
        private const float NameLabelW = 38f;

        // ---- 直向預算 ----
        // 「儲存設定」那一列**不進 GUILayout**，改用釘在面板底的固定矩形（見 Draw）：它是這塊面板唯一的落地
        // 入口，被裁掉就等於改了設定存不了。上面那疊（標題/名稱/體型/分頁/清單/說明）只要有一列比預估高一點點
        // （字型、skin margin 都會影響），BeginArea 就從最後一個元素開始裁 —— 剛好就是這顆鈕。
        /// <summary>底下那一列（狀態字 + 儲存設定鈕）的高度。</summary>
        public const float FooterH = RowH + 2f;
        /// <summary>GUILayout 兩個元素之間的預設留白（內建 skin 的 margin＝4）。</summary>
        public const float Gap = 4f;
        /// <summary>清單上面那幾列在 layout 裡吃掉的高度：標題、名稱、體型、Space(2)、分頁。</summary>
        public const float TopH = RowH + Gap + (RowH + 2f) + Gap + RowH + Gap + 2f + Gap + (RowH + 2f);
        /// <summary>預估以外的餘裕。字型/skin 換了之後每列高個 1~2 px 也還塞得下（不然又是「最後一列被裁掉」）。</summary>
        public const float Safety = 12f;
        /// <summary>設定清單（可捲動）的高度＝面板扣掉上面那疊、說明列、底下那一列之後剩下的。
        /// 寫成算式而不是硬編，改 <see cref="ExpandedH"/> / <see cref="HelpH"/> 時不用再自己算一次。
        /// （StartupConfigPanelLayoutTests 會盯著它 —— 版面常數再被調動時，這邊剩幾 px 要看得見。）</summary>
        public const float ListH = ExpandedH - 2f * Pad - (FooterH + Pad) - HelpH - Gap - TopH - Gap - Safety;
        /// <summary>下拉那顆 ▼ 佔的寬度（<see cref="DrawChoice"/> 自己排矩形時用）。</summary>
        private const float ArrowW = 16f;
        /// <summary>下拉清單一列的高度。</summary>
        public const float DropItemH = RowH;
        /// <summary>下拉清單最多同時看得到幾列 —— 再多就用滾輪捲（MMD 模型可以裝幾十個，
        /// 清單一路長下去會蓋掉整個畫面、還會伸出面板外）。</summary>
        public const int DropMaxRows = 8;
        /// <summary>下拉清單外框到內容的留白。</summary>
        public const float DropPad = 1f;
        /// <summary>選項多到要捲時，右緣那條捲動指示條的寬度。</summary>
        private const float DropBarW = 3f;

        /// <summary>
        /// 把一個高 <paramref name="h"/> 的東西擺在這一列的**上下正中間**（x / 寬另外給）。純幾何 —— 有單元測試。
        ///
        /// 每一列都得自己排矩形、自己置中,不能把控制項交給 GUILayout 排:每種控制項的 style 帶的
        /// margin 都不一樣(label 4 / textField 4 / button 4 / slider 上下不對稱),列與列的高度因此長短不一;
        /// 滑桿更是連高度都不歸 layout 管 —— <c>horizontalSlider</c> 有 <c>fixedHeight</c>,
        /// <c>GUILayout.Height</c> 對它無效,它只會黏在自己那一列的上緣(使用者回報「橫的拉桿要在欄位的上下中間」)。
        /// </summary>
        public static Rect CenterV(Rect row, float x, float w, float h)
            => new Rect(x, row.y + Mathf.Max(0f, (row.height - h) * 0.5f), w, Mathf.Min(h, row.height));
        /// <summary>每一列右邊留給捲軸的安全邊距（見 <see cref="DrawField"/>）。</summary>
        public const float RowRightPad = 2f;

        /// <summary>體型（胖瘦）index 0..4 的名稱 key。對應 <c>SdoBodyShape.WeightFromIndex</c>：1＝標準(×1.0)。</summary>
        private static readonly string[] BodyShapeKeys =
            { "cfg.panel.body.0", "cfg.panel.body.1", "cfg.panel.body.2", "cfg.panel.body.3", "cfg.panel.body.4" };

        /// <summary>面板自己那些字（標題/鈕/狀態）。跟設定表一樣走 <c>cfg.*</c>，字在 tools/build_localization.py。
        /// 每幀現解 —— OPTION 對話框可以在遊戲中途換語言。</summary>
        private static string T(string key) => StartupConfigSchema.L(key);

        /// <summary>面板是否展開中。房間/選性別畫面用它 gate ESC 與 F2（展開時那兩顆鍵歸面板管）。</summary>
        public bool Expanded { get; private set; }

        /// <summary>名稱輸入框的內容。切性別時由畫面重設（<see cref="SetName"/>）。</summary>
        public string NameText = "";

        /// <summary>按「儲存」時呼叫，回傳要顯示的狀態訊息。由 GenderSelectScreen 接上改名邏輯。</summary>
        public Func<string, string> SaveName;

        /// <summary>體型（胖瘦）index 0..4 的讀/寫。null＝不顯示體型那一列（面板本身不知道角色是誰）。
        /// 寫入只改記憶體＋即時刷新 3D 預覽，落地留給 <see cref="SaveExtra"/>（拖曳中每幀存檔太傷）。</summary>
        public Func<int> BodyShapeGet;
        public Action<int> BodyShapeSet;

        /// <summary>按「儲存設定」時，除了 config.ini 之外還要落地的東西（體型 → profile.json）。</summary>
        public Action SaveExtra;

        private int _tab;
        private Vector2 _scroll;
        private string _status = "";
        private string _hoverHelp = "";
        private readonly HashSet<string> _reveal = new HashSet<string>();          // 密碼/token 已按「顯」的欄位
        private readonly Dictionary<string, string> _edit = new Dictionary<string, string>();  // 文字欄位的編輯暫存
        private List<ConfigField>[] _byTab;

        // ---- 下拉選單（Choice 那種列）----
        // 展開中的清單畫在**所有東西之後**（見 Draw 尾端）：它必須蓋過設定清單，而 ScrollView / BeginArea 會把
        // 畫在裡面的東西裁掉。滑鼠事件則反過來**在最前面**先處理（見 HandleDropEvents）—— IMGUI 的事件是照
        // 繪製順序發的，最後才畫的清單本來會拿到別人挑剩的點擊。
        private ConfigField _drop;              // 展開中的那一列（null＝沒有展開）
        private Vector2 _dropRowScreen;         // 那一列的左上角（螢幕座標）—— 跨 Area/ScrollView 的 clip 唯一可靠的座標
        private Vector2 _dropRowSize;           // 那一列的寬高（設計像素）
        private Rect _dropRect;                 // 上一趟 Repaint 畫出來的清單矩形（設計像素）＝這一幀的命中範圍
        private int _dropTop;                   // 捲到第幾個選項在最上面
        private bool _dropSeen;                 // 這一幀那一列有沒有被畫到（切分頁之後就不會了 → 自動收起來）
        private Texture2D _texDropBg, _texDropHot, _texDropSel, _texDropBar;

        private GUIStyle _title, _label, _value, _choice, _unit, _help, _status_, _tabStyle, _arrow;

        /// <summary>換性別 → 重新帶入該帳號的名字，並清掉狀態訊息。</summary>
        public void SetName(string name)
        {
            NameText = name ?? "";
            _status = "";
        }

        /// <summary>ESC：下拉展開中 → 只收下拉；面板展開中 → 收合並回 true（畫面就不要拿去結束遊戲）；
        /// 本來就收合 → false。</summary>
        public bool HandleEscape()
        {
            if (_drop != null) { CloseDrop(); return true; }
            if (!Expanded) return false;
            Collapse();
            return true;
        }

        private void Collapse()
        {
            Expanded = false;
            CloseDrop();
            _edit.Clear();      // 沒按儲存就收起來 → 丟掉打到一半的文字，下次展開重新從設定值帶入
            _reveal.Clear();
            GUI.FocusControl(null);
        }

        private void CloseDrop()
        {
            _drop = null;
            _dropRect = default;
            _dropTop = 0;
        }

        /// <summary>畫面每幀從 OnGUI 呼叫。<paramref name="content"/> ＝ 800×600 內容區在螢幕上的矩形。
        /// 橫豎各自縮放（Stretch 模式下畫面本來就是非等比拉伸的）—— 等比縮的話面板會比背景圖高幾 %，
        /// 下緣就會壓到男/女核取方塊。</summary>
        public void Draw(Rect content)
        {
            float sx = content.width / 800f, sy = content.height / 600f;
            var oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(new Vector3(content.x, content.y, 0f), Quaternion.identity, new Vector3(sx, sy, 1f));
            EnsureStyles();
            // 說明列：**只在 Repaint 這一趟**清空重收 —— 每一格畫完就用 GetLastRect 比對滑鼠位置，說明列畫在
            // 它們後面所以同一趟就拿得到。（先前寫成每趟都清、又用上一趟的值 → Layout 趟收不到、Repaint 趟
            // 收到的又已經來不及畫，說明永遠是空的。）
            if (Event.current.type == EventType.Repaint) { _hoverHelp = ""; _dropSeen = false; }
            // 下拉展開中：先把它的滑鼠事件吃掉，底下那些控制項才不會搶走（IMGUI 事件照繪製順序發）。
            if (_drop != null) HandleDropEvents();

            // 底板自己畫、內容區往內縮 —— BeginArea(rect, style) 只把 style 當背景畫，不會套它的 padding，
            // 直接排版的話文字會貼到外框上。
            var full = new Rect(PanelX, PanelY, PanelW, Expanded ? ExpandedH : CollapsedH);
            GUI.Box(full, GUIContent.none);
            var inner = new Rect(full.x + Pad, full.y + Pad, full.width - Pad * 2f, full.height - Pad * 2f);
            // 「儲存設定」那一列先從 layout 區裡切出去、自己畫在面板底 —— 它絕不能被 BeginArea 裁掉（見 FooterH）。
            var footer = new Rect(inner.x, inner.yMax - FooterH, inner.width, FooterH);
            if (Expanded) inner.height -= FooterH + Pad;
            GUILayout.BeginArea(inner);
            DrawHeader();
            DrawNameRow();
            if (Expanded)
            {
                DrawBodyRow();
                GUILayout.Space(2f);
                DrawTabs();
                DrawRows();
                DrawHelp();
            }
            GUILayout.EndArea();
            if (Expanded) DrawFooter(footer);
            // 下拉清單最後畫 —— 蓋在設定清單上面，而且不在任何 BeginArea/ScrollView 裡（那兩個會把它裁掉）。
            if (Event.current.type == EventType.Repaint && _drop != null)
            {
                if (_dropSeen) DrawDrop();
                else CloseDrop();       // 那一列這一幀沒被畫到（切了分頁 / 面板收起來了）
            }

            GUI.matrix = oldMatrix;
        }

        // ---------------------------------------------------------------- 版塊
        private void DrawHeader()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(T(Expanded ? "cfg.panel.title" : "cfg.panel.title_collapsed"), _title);
            GUILayout.FlexibleSpace();
            // 右上角：展開 / 縮小
            if (GUILayout.Button(T(Expanded ? "cfg.panel.collapse" : "cfg.panel.expand"),
                                 GUILayout.Width(52f), GUILayout.Height(RowH)))
            {
                if (Expanded) Collapse();
                else { Expanded = true; _edit.Clear(); _status = ""; }
            }
            GUILayout.EndHorizontal();
        }

        private void DrawNameRow()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(T("cfg.panel.name"), _label, GUILayout.Width(NameLabelW));
            NameText = GUILayout.TextField(NameText ?? "", 24, GUILayout.Height(RowH + 2f));
            if (GUILayout.Button(T("cfg.panel.save_name"), GUILayout.Width(44f), GUILayout.Height(RowH + 2f)))
                _status = SaveName != null ? SaveName(NameText) : "";
            GUILayout.EndHorizontal();
            if (!Expanded && !string.IsNullOrEmpty(_status)) GUILayout.Label(_status, _status_);
        }

        // 體型（胖瘦）—— 跟名稱一樣是「這個角色」的東西，所以擺在名稱下面、不進任何一個分頁
        // （分頁裡放的是 config.ini 的本機設定，體型在 profile.json）。
        private void DrawBodyRow()
        {
            if (BodyShapeGet == null) return;
            int cur = Mathf.Clamp(BodyShapeGet(), 0, BodyShapeKeys.Length - 1);
            // 跟設定清單的列同一套排法（自己排、垂直置中）—— 這一列的滑桿以前也黏在上緣。
            var row = NextRow();
            GUI.Label(new Rect(row.x, row.y, NameLabelW, row.height), T("cfg.panel.body"), _label);
            float x = row.x + NameLabelW + Gap;
            float trackW = Mathf.Max(8f, row.xMax - x - ValueW - Gap);
            int nv = Mathf.RoundToInt(GUI.HorizontalSlider(CenterV(row, x, trackW, SliderTrackH),
                                                          cur, 0f, BodyShapeKeys.Length - 1f));
            GUI.Label(CenterV(row, x + trackW + Gap, ValueW, RowH),
                      T(BodyShapeKeys[Mathf.Clamp(nv, 0, BodyShapeKeys.Length - 1)]), _value);
            if (nv != cur) BodyShapeSet?.Invoke(nv);
            Hover(T("cfg.panel.body.help"), row);
        }

        private void DrawTabs()
        {
            var cats = StartupConfigSchema.Categories;
            GUILayout.BeginHorizontal();
            for (int i = 0; i < cats.Length; i++)
            {
                bool on = i == _tab;
                // cats[i] 是分頁的 key（換語言不會讓「現在在第幾頁」跑掉），畫出來的是它的譯名。
                if (GUILayout.Toggle(on, StartupConfigSchema.CategoryText(cats[i]), _tabStyle,
                                     GUILayout.Height(RowH + 2f)) && !on)
                {
                    _tab = i;
                    _scroll = Vector2.zero;
                    CloseDrop();
                    GUI.FocusControl(null);
                }
            }
            GUILayout.EndHorizontal();
        }

        private void DrawRows()
        {
            _byTab ??= BuildTabs();
            // 只有直的捲軸。橫的一旦出現就代表某一列把版面撐寬了(MMD 模型名可以很長),那時整張表會被推著
            // 左右跑、每一列的標籤都對不上 —— 寧可讓那一列自己被裁掉,見 DrawChoice。
            //
            // 🔴 直的那條**不能常駐**(alwaysShowVertical 曾經是 true)：IMGUI 只在「內容真的高過視窗」時才替
            //    捲軸讓出寬度,常駐卻讓它照畫不誤 —— 於是不需要捲的分頁(連線頁 9 列)捲軸就直接壓在最右邊那格
            //    上面,輸入框/▼ 鈕伸到它底下(使用者回報過兩次:先是選項列的 ▶,再來是連線頁的輸入框)。
            //    交給 IMGUI 自己決定要不要畫,「畫」與「讓位」才會是同一個判斷。
            _scroll = GUILayout.BeginScrollView(_scroll, false, false,
                                                GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView,
                                                GUILayout.Height(ListH));
            foreach (var f in _byTab[Mathf.Clamp(_tab, 0, _byTab.Length - 1)]) DrawField(f);
            GUILayout.EndScrollView();
        }

        private void DrawHelp()
        {
            GUILayout.Label(string.IsNullOrEmpty(_hoverHelp) ? T("cfg.panel.help_hint") : _hoverHelp,
                            _help, GUILayout.Height(HelpH));
        }

        /// <summary>剛畫完那一列如果被滑鼠壓著，就把說明記下來給說明列用。
        /// 矩形由呼叫端給（每一列都是自己排的 —— <c>GetLastRect</c> 現在拿到的是列裡最後一個小控制項）。</summary>
        private void Hover(string help, Rect row)
        {
            if (Event.current.type != EventType.Repaint || string.IsNullOrEmpty(help)) return;
            if (row.Contains(Event.current.mousePosition)) _hoverHelp = help;
        }

        /// <summary>底下那一列：左邊狀態字、右邊「儲存設定」。<b>固定矩形、不走 GUILayout</b> —— 它釘在面板底緣，
        /// 上面那疊怎麼長都不會把它推出畫面外（先前就是被 BeginArea 裁掉半顆鈕）。</summary>
        private void DrawFooter(Rect r)
        {
            // 84 而不是原本的 64：這顆鈕的字是翻譯過的，最長的語言（"Save settings"）在 64 px 裡會被切掉半個字。
            const float BtnW = 84f;
            GUI.Label(new Rect(r.x, r.y, Mathf.Max(0f, r.width - BtnW - Gap), r.height), _status, _status_);
            if (GUI.Button(new Rect(r.xMax - BtnW, r.y, BtnW, r.height), T("cfg.panel.save_config"))) SaveConfig();
        }

        // ---------------------------------------------------------------- 單列
        //
        // 一列＝**一塊自己排的固定高度矩形**，裡面的東西全部自己擺、全部垂直置中（見 <see cref="CenterV"/>）。
        // 交給 GUILayout 排的話每種控制項的 style margin 都不一樣，列與列的高度跟著長短不一（使用者回報
        // 「欄位的高度跑版」）；滑桿更是連高度都不歸 layout 管，只會黏在自己那一列的上緣。
        private void DrawField(ConfigField f)
        {
            var row = NextRow();
            GUI.Label(new Rect(row.x, row.y, LabelW, row.height), f.Label, _label);
            float x = row.x + LabelW + Gap;
            var rest = new Rect(x, row.y, Mathf.Max(0f, row.xMax - x), row.height);
            switch (f.Kind)
            {
                case ConfigFieldKind.Toggle: DrawToggle(f, rest); break;
                case ConfigFieldKind.Slider: DrawSlider(f, rest); break;
                case ConfigFieldKind.Choice: DrawChoice(f, rest); break;
                case ConfigFieldKind.Action: DrawAction(f, rest); break;
                default: DrawText(f, rest); break;
            }
            Hover(f.Help, row);
        }

        /// <summary>跟 layout 要下一列（整列寬、固定高），並補上列與列之間的留白。
        /// 右邊那一小條安全邊距：捲軸真的出現時（分頁列數多到要捲），IMGUI 讓出來的寬度未必含 skin 的
        /// margin —— 差那一兩 px 就是「輸入框最後一個字被捲軸壓著」。</summary>
        private static Rect NextRow()
        {
            var r = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                                             GUILayout.ExpandWidth(true), GUILayout.Height(RowH));
            GUILayout.Space(Gap);
            return new Rect(r.x, r.y, Mathf.Max(0f, r.width - RowRightPad), r.height);
        }

        // 按鈕列（不是設定值 —— 例：把目前的 MMD 物理存成模型自己的 physics.ini）。按下去回傳的那句話直接進
        // 底下的狀態列,因為這種動作會失敗（資料夾唯讀、還沒有模型…),使用者要看得到結果。
        private void DrawAction(ConfigField f, Rect r)
        {
            const float BtnW = 56f;
            var names = f.Actions ?? Array.Empty<string>();
            float x = r.x;
            for (int i = 0; i < names.Length; i++)
            {
                if (GUI.Button(CenterV(r, x, BtnW, RowH), names[i]))
                {
                    string msg = f.Invoke?.Invoke(i);
                    if (!string.IsNullOrEmpty(msg)) _status = msg;
                }
                x += BtnW + 4f;
            }
            // 狀態字（「physics.ini（未套用）」）**自己占下面一列**：擠在鈕右邊只剩十幾 px，那句話會被
            // 折成一直條（使用者回報「擠成一坨」）。縮排對齊值欄，看起來就是那顆鈕的附註。
            string state = f.StateText?.Invoke() ?? "";
            if (string.IsNullOrEmpty(state)) return;
            var line = NextRow();
            float sx = line.x + LabelW + Gap;
            GUI.Label(new Rect(sx, line.y, Mathf.Max(0f, line.xMax - sx), line.height), state, _choice);
        }

        private void DrawToggle(ConfigField f, Rect r)
        {
            bool on = f.GetBool();
            // 開/關那兩個字是共用的（common.enabled/disabled）；前面那個空格是核取方塊與字的間距。
            bool nv = GUI.Toggle(CenterV(r, r.x, r.width, RowH), on,
                                 " " + LocalizationManager.Get(on ? "common.enabled" : "common.disabled"));
            if (nv != on) f.SetBool(nv);
        }

        // 滑桿 + 右邊可直接輸入數字的小欄位（判定精度除外 —— 它的值是「精4」「JUSTICE」，打字沒有意義，
        // 那一列右邊就純顯示）。拖滑桿會把輸入暫存丟掉，欄位才會跟著跑。
        private void DrawSlider(ConfigField f, Rect r)
        {
            float v = f.GetNumber();
            float rightW = f.NoValueEntry ? ValueW : ValueEntryW + UnitW;
            float trackW = Mathf.Max(8f, r.width - rightW - Gap);
            float nv = GUI.HorizontalSlider(CenterV(r, r.x, trackW, SliderTrackH), v, f.Min, f.Max);
            if (!Mathf.Approximately(nv, v)) { f.SetNumber(nv); _edit.Remove(f.Key); }

            float x = r.x + trackW + Gap;
            if (f.NoValueEntry)
            {
                GUI.Label(CenterV(r, x, ValueW, RowH), f.NumberText(), _value);
                return;
            }

            string cur = _edit.TryGetValue(f.Key, out var buf) ? buf : f.NumberText();
            string typed = GUI.TextField(CenterV(r, x, ValueEntryW, RowH), cur, 8);
            if (!string.Equals(typed, cur, StringComparison.Ordinal))
            {
                // 打到一半的原字串留在暫存（"-" / "" / "1." 也留著），解析得出來才套用；夾範圍交給 SetNumber。
                _edit[f.Key] = typed;
                if (float.TryParse(typed.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                    f.SetNumber(t);
            }
            GUI.Label(CenterV(r, x + ValueEntryW, UnitW, RowH), f.Unit ?? "", _unit);
        }

        /// <summary>滑桿那條軌道自己的高度 —— skin 給的（<c>fixedHeight</c>），沒給就用整列高。
        /// 拿它去 <see cref="CenterV"/> 才置得中：軌道比一列矮，貼著列頂畫就是使用者看到的「拉桿偏上」。</summary>
        private static float SliderTrackH
        {
            get
            {
                float h = GUI.skin != null && GUI.skin.horizontalSlider != null
                        ? GUI.skin.horizontalSlider.fixedHeight : 0f;
                return h > 0f ? h : RowH;
            }
        }

        // 「目前值 ▼」一顆鈕，按下去彈出整份清單（見 DrawDrop）—— 選項多的時候（MMD 模型可以裝幾十個）
        // 一格一格按過去找不到人要的那個，而且看不到有哪些可選。
        //
        // 這一格拿到的是**這一列扣掉標籤欄之後剩下的矩形**（見 DrawField / NextRow）：
        //
        // 🔴 不能用 GUILayout 排。選項字串是執行期才知道的（MMD 模型名＝資料夾名，可以長到 40 幾個字），
        //    Button/Label 會照內容算寬度 → 那一列把整張表撐得比面板還寬（clipping = Clip 只影響「畫」不影響
        //    「排」），每一列的標籤欄跟著被推出畫面外。
        // 🔴 改成「固定寬」也不對：右邊那條垂直捲軸吃掉多少寬是 skin 決定的（fixedWidth + margin），固定寬等於
        //    在猜那個數字 —— 猜多了留白、猜少了 ▼ 就被捲軸壓在底下按不到（使用者回報過的就是這個）。
        //
        // 整列那塊空矩形（GUIContent.none + GUIStyle.none）是唯一 ExpandWidth 的元素，於是它**剛好等於這一列
        // 真正剩下的寬**（捲軸已經在 ScrollView 那層扣掉了）。長名字則被中間那格裁掉，撐不寬版面。
        private void DrawChoice(ConfigField f, Rect r)
        {
            if (GUI.Button(r, GUIContent.none)) ToggleDrop(f);
            GUI.Label(new Rect(r.x + 4f, r.y, Mathf.Max(0f, r.width - ArrowW - 4f), r.height), f.ChoiceText(), _choice);
            GUI.Label(new Rect(r.xMax - ArrowW, r.y, ArrowW, r.height), _drop == f ? "▲" : "▼", _arrow);

            // 這一列在螢幕上的位置：清單畫在 BeginArea/ScrollView 外面，只有螢幕座標兩邊都算得準。
            // （Layout 那一趟 GetRect 回的是空矩形，所以只在 Repaint 記。）
            if (_drop == f && Event.current.type == EventType.Repaint)
            {
                _dropRowScreen = GUIUtility.GUIToScreenPoint(new Vector2(r.x, r.y));
                _dropRowSize = new Vector2(r.width, r.height);
                _dropSeen = true;
            }
        }

        private void ToggleDrop(ConfigField f)
        {
            if (_drop == f) { CloseDrop(); return; }
            _drop = f;
            _dropRect = default;
            // 一打開就要看得到「現在選的是哪個」——裝了 40 個模型時，清單從頭捲起等於還是要自己找。
            int cur = f.SelectedChoiceIndex();
            int shown = Mathf.Min(f.Options().Length, DropMaxRows);
            _dropTop = cur < shown ? 0 : cur - shown + 1;
        }

        /// <summary>下拉清單的矩形（設計像素、面板座標）。預設貼在那一列下面；下面塞不下就翻到上面；
        /// 兩邊都塞不下（清單本身比面板還高的極端情形）就往上頂到面板內。純幾何 —— 有單元測試。</summary>
        public static Rect DropRect(Rect row, int count)
        {
            int shown = Mathf.Clamp(count, 1, DropMaxRows);
            float h = shown * DropItemH + DropPad * 2f;
            float top = PanelY + Pad, bottom = PanelY + ExpandedH - Pad;
            float y = row.yMax;
            if (y + h > bottom) y = row.y - h;                       // 下面放不下 → 翻到上面
            y = Mathf.Clamp(y, top, Mathf.Max(top, bottom - h));
            return new Rect(row.x, y, row.width, h);
        }

        /// <summary>滑鼠落在清單的第幾個選項上（<paramref name="scrollTop"/>＝捲到第幾個在最上面）。
        /// 純幾何 —— 有單元測試。</summary>
        public static int DropIndexAt(Rect drop, float mouseY, int scrollTop, int count)
        {
            int i = scrollTop + Mathf.FloorToInt((mouseY - drop.y - DropPad) / DropItemH);
            return Mathf.Clamp(i, 0, Mathf.Max(0, count - 1));
        }

        /// <summary>捲動位置的合法範圍（選項少於一頁時只能是 0）。純幾何 —— 有單元測試。</summary>
        public static int ClampDropTop(int top, int count)
            => Mathf.Clamp(top, 0, Mathf.Max(0, count - Mathf.Min(count, DropMaxRows)));

        // 下拉展開中的滑鼠/滾輪：**在這一幀畫任何東西之前**處理掉。清單是最後才畫的，照 IMGUI 的順序它只拿得到
        // 別人挑剩的事件（點在清單上會被底下那一列的鈕先吃掉）。命中範圍用上一趟 Repaint 算出來的矩形。
        private void HandleDropEvents()
        {
            var e = Event.current;
            if (_dropRect.width <= 0f) return;          // 還沒畫過（剛按開那一幀）
            bool inside = _dropRect.Contains(e.mousePosition);
            var opts = _drop.Options();

            if (e.type == EventType.MouseDown)
            {
                if (inside) _drop.SelectChoice(DropIndexAt(_dropRect, e.mousePosition.y, _dropTop, opts.Length));
                CloseDrop();
                e.Use();                                // 點外面＝關掉就好，不要順便按到底下的東西
            }
            else if (e.type == EventType.ScrollWheel && inside)
            {
                _dropTop = ClampDropTop(_dropTop + (e.delta.y > 0f ? 1 : -1), opts.Length);
                e.Use();
            }
        }

        private void DrawDrop()
        {
            var opts = _drop.Options();
            if (opts.Length == 0) { CloseDrop(); return; }           // 一個模型都沒裝

            var pos = GUIUtility.ScreenToGUIPoint(_dropRowScreen);
            var row = new Rect(pos.x, pos.y, _dropRowSize.x, _dropRowSize.y);
            _dropRect = DropRect(row, opts.Length);
            int shown = Mathf.Min(opts.Length, DropMaxRows);
            _dropTop = ClampDropTop(_dropTop, opts.Length);
            int cur = _drop.SelectedChoiceIndex();

            // 不透明底：底下就是設定清單，用半透明的 skin box 會兩層字疊在一起看不清。
            GUI.DrawTexture(_dropRect, _texDropBg);
            GUI.Box(_dropRect, GUIContent.none);
            bool bar = opts.Length > shown;
            float itemW = _dropRect.width - DropPad * 2f - (bar ? DropBarW + 1f : 0f);
            for (int i = 0; i < shown; i++)
            {
                int idx = _dropTop + i;
                var ir = new Rect(_dropRect.x + DropPad, _dropRect.y + DropPad + i * DropItemH, itemW, DropItemH);
                if (ir.Contains(Event.current.mousePosition)) GUI.DrawTexture(ir, _texDropHot);
                else if (idx == cur) GUI.DrawTexture(ir, _texDropSel);
                GUI.Label(new Rect(ir.x + 3f, ir.y, Mathf.Max(0f, ir.width - 3f), ir.height), _drop.ChoiceTextAt(idx), _choice);
            }
            if (!bar) return;
            // 右緣那條：看得出來「上面/下面還有」。長度＝看得到的比例，位置＝捲到哪。
            float trackH = _dropRect.height - DropPad * 2f;
            float knobH = Mathf.Max(6f, trackH * shown / opts.Length);
            float knobY = _dropRect.y + DropPad + (trackH - knobH) * _dropTop / Mathf.Max(1, opts.Length - shown);
            GUI.DrawTexture(new Rect(_dropRect.xMax - DropPad - DropBarW, knobY, DropBarW, knobH), _texDropBar);
        }

        private void DrawText(ConfigField f, Rect r)
        {
            // 34 而不是原本的 22：「顯/隱」是一個字，翻成 Show/Hide、表示/隠す 就要寬一點才不會被切掉。
            const float RevealW = 34f;
            bool masked = f.Secret && !_reveal.Contains(f.Key);
            float boxW = f.Secret ? Mathf.Max(8f, r.width - RevealW - Gap) : r.width;
            string cur = _edit.TryGetValue(f.Key, out var buf) ? buf : (f.Get?.Invoke() ?? "");
            var box = CenterV(r, r.x, boxW, RowH);
            string nv = masked ? GUI.PasswordField(box, cur, '●') : GUI.TextField(box, cur);
            if (!string.Equals(nv, cur, StringComparison.Ordinal))
            {
                _edit[f.Key] = nv;      // 打到一半的原字串留在暫存（"" / "12a" 也留著，畫面才不會跳回舊值）
                f.Set?.Invoke(nv);      // 解析得出來就即時套用；解析不出來 Set 自己會保留舊值
            }
            if (f.Secret && GUI.Button(CenterV(r, r.xMax - RevealW, RevealW, RowH),
                                       T(masked ? "cfg.panel.reveal" : "cfg.panel.hide")))
            {
                if (masked) _reveal.Add(f.Key); else _reveal.Remove(f.Key);
            }
        }

        // ---------------------------------------------------------------- 存檔
        private void SaveConfig()
        {
            GUI.FocusControl(null);
            CloseDrop();
            try
            {
                StartupConfigSchema.ApplyAndSave();
                SaveExtra?.Invoke();   // config.ini 以外的（體型 → profile.json）—— 說明列講的就是這顆鈕
                _edit.Clear();   // 夾正後的值重新從設定讀（例如 port 打 99999 會被夾成 65535）
                _status = T("cfg.panel.saved");
            }
            catch (Exception e)
            {
                _status = StartupConfigSchema.L("cfg.panel.save_failed", e.Message);
                Debug.LogWarning("[StartupConfig] save failed: " + e);
            }
        }

        private static List<ConfigField>[] BuildTabs()
        {
            var cats = StartupConfigSchema.Categories;
            var res = new List<ConfigField>[cats.Length];
            for (int i = 0; i < cats.Length; i++) res[i] = StartupConfigSchema.InCategory(cats[i]);
            return res;
        }

        // IMGUI 樣式只能在 OnGUI 期間（GUI.skin 有效）建 → lazy。
        private void EnsureStyles()
        {
            if (_title != null) return;
            _title = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            // MiddleLeft：每一列都是自己排的固定高度矩形，靠上的話標籤會跟旁邊置中的欄位差一兩 px。
            _label = new GUIStyle(GUI.skin.label)
            {
                wordWrap = false, clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft,
            };
            _value = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft };
            _choice = new GUIStyle(_value) { clipping = TextClipping.Clip, wordWrap = false };
            _unit = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft, fontSize = 10, padding = new RectOffset(1, 0, 0, 0),
                normal = { textColor = new Color(0.72f, 0.72f, 0.72f) },
            };
            _help = new GUIStyle(GUI.skin.box)
            {
                wordWrap = true, alignment = TextAnchor.UpperLeft, fontSize = 10,
                padding = new RectOffset(5, 5, 3, 3),
                normal = { textColor = new Color(0.82f, 0.82f, 0.82f) },
            };
            _status_ = new GUIStyle(GUI.skin.label)
            {
                // MiddleLeft：底下那一列是固定高度的矩形（見 DrawFooter），靠左上會跟旁邊的鈕對不齊。
                wordWrap = false, fontSize = 10, alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(0.7f, 0.9f, 0.7f) },
            };
            _tabStyle = new GUIStyle(GUI.skin.button) { fontSize = 11, padding = new RectOffset(2, 2, 2, 2) };
            _arrow = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 10 };
            _texDropBg = Solid(new Color(0.13f, 0.13f, 0.14f, 1f));
            _texDropSel = Solid(new Color(0.24f, 0.34f, 0.45f, 1f));
            _texDropHot = Solid(new Color(0.33f, 0.45f, 0.58f, 1f));
            _texDropBar = Solid(new Color(0.55f, 0.55f, 0.58f, 1f));
        }

        /// <summary>下拉清單自己畫底色用的 1×1 貼圖（skin 的 box 是半透明的，疊在設定清單上兩層字會糊在一起）。</summary>
        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }
    }
}
