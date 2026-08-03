using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Sdo.Settings;

namespace Sdo.UI.Screens
{
    /// <summary>
    /// 開場（選性別）畫面左側那塊面板 —— 原本只有「玩家名稱」一個小框，現在整塊撐大成
    /// 「角色左邊、性別核取方塊上面」那一整片空白區（設計座標 800×600 下的 x 20..316 / y 10..512），
    /// 底下掛上 <see cref="StartupConfigSchema"/> 那張表：**config.ini 裡遊戲內沒有其它 UI 可以改的設定全集**，
    /// 依 連線 / 遊玩 / 歌曲 / 顯示 分四頁，上面一排 tab 切換。右上角一顆鈕展開/收合（預設收合＝只剩標題列＋名稱列，
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
        private const float RowH = 18f, LabelW = 104f, ValueW = 54f, ListH = 314f, HelpH = 52f, Pad = 5f;
        private const float ValueEntryW = 40f, UnitW = 17f;

        /// <summary>體型（胖瘦）index 0..4 的名稱。對應 <c>SdoBodyShape.WeightFromIndex</c>：1＝標準(×1.0)。</summary>
        private static readonly string[] BodyShapeNames = { "瘦", "標準", "微胖", "胖", "很胖" };
        private const string BodyShapeHelp = "角色的胖瘦（瘦…很胖）。拖動時左邊 3D 預覽立刻跟著變；按「儲存設定」才寫進這個角色的存檔。";

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

        private GUIStyle _title, _label, _value, _unit, _help, _status_, _tabStyle;

        /// <summary>換性別 → 重新帶入該帳號的名字，並清掉狀態訊息。</summary>
        public void SetName(string name)
        {
            NameText = name ?? "";
            _status = "";
        }

        /// <summary>ESC：展開中 → 收合並回 true（畫面就不要拿去結束遊戲）；本來就收合 → false。</summary>
        public bool HandleEscape()
        {
            if (!Expanded) return false;
            Collapse();
            return true;
        }

        private void Collapse()
        {
            Expanded = false;
            _edit.Clear();      // 沒按儲存就收起來 → 丟掉打到一半的文字，下次展開重新從設定值帶入
            _reveal.Clear();
            GUI.FocusControl(null);
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
            if (Event.current.type == EventType.Repaint) _hoverHelp = "";

            // 底板自己畫、內容區往內縮 —— BeginArea(rect, style) 只把 style 當背景畫，不會套它的 padding，
            // 直接排版的話文字會貼到外框上。
            var full = new Rect(PanelX, PanelY, PanelW, Expanded ? ExpandedH : CollapsedH);
            GUI.Box(full, GUIContent.none);
            GUILayout.BeginArea(new Rect(full.x + Pad, full.y + Pad, full.width - Pad * 2f, full.height - Pad * 2f));
            DrawHeader();
            DrawNameRow();
            if (Expanded)
            {
                DrawBodyRow();
                GUILayout.Space(2f);
                DrawTabs();
                DrawRows();
                DrawHelp();
                DrawFooter();
            }
            GUILayout.EndArea();

            GUI.matrix = oldMatrix;
        }

        // ---------------------------------------------------------------- 版塊
        private void DrawHeader()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Expanded ? "玩家設定" : "玩家名稱", _title);
            GUILayout.FlexibleSpace();
            // 右上角：展開 / 縮小
            if (GUILayout.Button(Expanded ? "▲ 縮小" : "▼ 展開", GUILayout.Width(52f), GUILayout.Height(RowH)))
            {
                if (Expanded) Collapse();
                else { Expanded = true; _edit.Clear(); _status = ""; }
            }
            GUILayout.EndHorizontal();
        }

        private void DrawNameRow()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("名稱", _label, GUILayout.Width(30f));
            NameText = GUILayout.TextField(NameText ?? "", 24, GUILayout.Height(RowH + 2f));
            if (GUILayout.Button("儲存", GUILayout.Width(44f), GUILayout.Height(RowH + 2f)))
                _status = SaveName != null ? SaveName(NameText) : "";
            GUILayout.EndHorizontal();
            if (!Expanded && !string.IsNullOrEmpty(_status)) GUILayout.Label(_status, _status_);
        }

        // 體型（胖瘦）—— 跟名稱一樣是「這個角色」的東西，所以擺在名稱下面、不進任何一個分頁
        // （分頁裡放的是 config.ini 的本機設定，體型在 profile.json）。
        private void DrawBodyRow()
        {
            if (BodyShapeGet == null) return;
            int cur = Mathf.Clamp(BodyShapeGet(), 0, BodyShapeNames.Length - 1);
            GUILayout.BeginHorizontal();
            GUILayout.Label("體型", _label, GUILayout.Width(30f));
            GUILayout.Space(2f);
            int nv = Mathf.RoundToInt(GUILayout.HorizontalSlider(cur, 0f, BodyShapeNames.Length - 1f, GUILayout.Height(RowH)));
            GUILayout.Label(BodyShapeNames[Mathf.Clamp(nv, 0, BodyShapeNames.Length - 1)], _value, GUILayout.Width(ValueW));
            GUILayout.EndHorizontal();
            if (nv != cur) BodyShapeSet?.Invoke(nv);
            Hover(BodyShapeHelp);
        }

        private void DrawTabs()
        {
            var cats = StartupConfigSchema.Categories;
            GUILayout.BeginHorizontal();
            for (int i = 0; i < cats.Length; i++)
            {
                bool on = i == _tab;
                if (GUILayout.Toggle(on, cats[i], _tabStyle, GUILayout.Height(RowH + 2f)) && !on)
                {
                    _tab = i;
                    _scroll = Vector2.zero;
                    GUI.FocusControl(null);
                }
            }
            GUILayout.EndHorizontal();
        }

        private void DrawRows()
        {
            _byTab ??= BuildTabs();
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(ListH));
            foreach (var f in _byTab[Mathf.Clamp(_tab, 0, _byTab.Length - 1)]) DrawField(f);
            GUILayout.EndScrollView();
        }

        private void DrawHelp()
        {
            GUILayout.Label(string.IsNullOrEmpty(_hoverHelp) ? "滑鼠移到設定上會顯示說明。" : _hoverHelp,
                            _help, GUILayout.Height(HelpH));
        }

        /// <summary>剛畫完那一列（<c>GUILayoutUtility.GetLastRect</c>）如果被滑鼠壓著，就把說明記下來給說明列用。</summary>
        private void Hover(string help)
        {
            if (Event.current.type != EventType.Repaint || string.IsNullOrEmpty(help)) return;
            if (GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition)) _hoverHelp = help;
        }

        private void DrawFooter()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(_status, _status_);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("儲存設定", GUILayout.Width(64f), GUILayout.Height(RowH + 2f))) SaveConfig();
            GUILayout.EndHorizontal();
        }

        // ---------------------------------------------------------------- 單列
        private void DrawField(ConfigField f)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(f.Label, _label, GUILayout.Width(LabelW));
            switch (f.Kind)
            {
                case ConfigFieldKind.Toggle: DrawToggle(f); break;
                case ConfigFieldKind.Slider: DrawSlider(f); break;
                case ConfigFieldKind.Choice: DrawChoice(f); break;
                default: DrawText(f); break;
            }
            GUILayout.EndHorizontal();
            Hover(f.Help);
        }

        private void DrawToggle(ConfigField f)
        {
            bool on = f.GetBool();
            bool nv = GUILayout.Toggle(on, on ? " 開啟" : " 關閉", GUILayout.Height(RowH));
            if (nv != on) f.SetBool(nv);
        }

        // 滑桿 + 右邊可直接輸入數字的小欄位（判定精度除外 —— 它的值是「精4」「JUSTICE」，打字沒有意義，
        // 那一列右邊就純顯示）。拖滑桿會把輸入暫存丟掉，欄位才會跟著跑。
        private void DrawSlider(ConfigField f)
        {
            float v = f.GetNumber();
            GUILayout.Space(2f);
            float nv = GUILayout.HorizontalSlider(v, f.Min, f.Max, GUILayout.Height(RowH));
            if (!Mathf.Approximately(nv, v)) { f.SetNumber(nv); _edit.Remove(f.Key); }

            if (f.NoValueEntry) { GUILayout.Label(f.NumberText(), _value, GUILayout.Width(ValueW)); return; }

            string cur = _edit.TryGetValue(f.Key, out var buf) ? buf : f.NumberText();
            string typed = GUILayout.TextField(cur, 8, GUILayout.Width(ValueEntryW), GUILayout.Height(RowH));
            if (!string.Equals(typed, cur, StringComparison.Ordinal))
            {
                // 打到一半的原字串留在暫存（"-" / "" / "1." 也留著），解析得出來才套用；夾範圍交給 SetNumber。
                _edit[f.Key] = typed;
                if (float.TryParse(typed.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                    f.SetNumber(t);
            }
            GUILayout.Label(f.Unit ?? "", _unit, GUILayout.Width(UnitW));
        }

        private void DrawChoice(ConfigField f)
        {
            if (GUILayout.Button("◀", GUILayout.Width(20f), GUILayout.Height(RowH))) f.StepChoice(-1);
            GUILayout.Label(f.ChoiceText(), _value);
            if (GUILayout.Button("▶", GUILayout.Width(20f), GUILayout.Height(RowH))) f.StepChoice(1);
        }

        private void DrawText(ConfigField f)
        {
            bool masked = f.Secret && !_reveal.Contains(f.Key);
            string cur = _edit.TryGetValue(f.Key, out var buf) ? buf : (f.Get?.Invoke() ?? "");
            string nv = masked
                ? GUILayout.PasswordField(cur, '●', GUILayout.Height(RowH))
                : GUILayout.TextField(cur, GUILayout.Height(RowH));
            if (!string.Equals(nv, cur, StringComparison.Ordinal))
            {
                _edit[f.Key] = nv;      // 打到一半的原字串留在暫存（"" / "12a" 也留著，畫面才不會跳回舊值）
                f.Set?.Invoke(nv);      // 解析得出來就即時套用；解析不出來 Set 自己會保留舊值
            }
            if (f.Secret && GUILayout.Button(masked ? "顯" : "隱", GUILayout.Width(22f), GUILayout.Height(RowH)))
            {
                if (masked) _reveal.Add(f.Key); else _reveal.Remove(f.Key);
            }
        }

        // ---------------------------------------------------------------- 存檔
        private void SaveConfig()
        {
            GUI.FocusControl(null);
            try
            {
                StartupConfigSchema.ApplyAndSave();
                _edit.Clear();   // 夾正後的值重新從設定讀（例如 port 打 99999 會被夾成 65535）
                _status = "設定已儲存";
            }
            catch (Exception e)
            {
                _status = "儲存失敗：" + e.Message;
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
            _label = new GUIStyle(GUI.skin.label) { wordWrap = false, clipping = TextClipping.Clip };
            _value = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft };
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
                wordWrap = false, fontSize = 10, normal = { textColor = new Color(0.7f, 0.9f, 0.7f) },
            };
            _tabStyle = new GUIStyle(GUI.skin.button) { fontSize = 11, padding = new RectOffset(2, 2, 2, 2) };
        }
    }
}
