using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Sdo.Game;
using Sdo.Localization;
using Sdo.UI.Catalog;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>
    /// 分類瀏覽：the floating panel behind song-select's 資料夾 category. A row of grouping tabs
    /// (資料夾 / 歌名 / 歌手 / BPM — <see cref="SongGroupMode"/>) over a scrolling list of that grouping's buckets,
    /// each showing how many songs it holds; clicking a bucket hands its songs to the host screen, which loads them
    /// into the main 12-row song list.
    ///
    /// Drawn with IMGUI (<c>GUI.Window</c>), like the project's other tool panels: the window drags by
    /// its title bar and the scroll view brings its own draggable slider — no canvas art, no layout groups, nothing to
    /// mis-lay-out. This browser never existed in the original game, so it deliberately wears the plain dev-tool look
    /// instead of imitating MUSICSELDLG. It starts sized to the CD column beneath it but is FREELY RESIZABLE — drag its
    /// right / bottom edge or bottom-right corner (long osu pack folder names often need it wider than the disc column).
    ///
    /// IMGUI draws over the UI canvas but does NOT consume its clicks, so <see cref="_blocker"/> — an invisible UGUI
    /// raycast target tracking the window's rect — keeps a click on a bucket from also landing on the dialog beneath
    /// (the vinyl, a song row, …). Grouping itself is StepMania's; see <see cref="SongGrouping"/>.
    /// </summary>
    public sealed class SongGroupPanel : MonoBehaviour
    {
        private const int WindowId = 0x5D60;
        // The window is sized/placed off the dialog's DISC COLUMN (MusicSelDlg diskwin: x44 w237), so it is exactly as
        // wide as the CD panel underneath it at any resolution; the height runs down to just above 場景選擇 (y399).
        // 以下全部是「設計座標(800×600)」的尺寸，畫之前一律過 PX() 換成實際螢幕像素(見 _s)。
        private const float DesignX = 44f, DesignY = 84f, DesignW = 237f, DesignH = 300f;
        private const float RowH = 22f, RowGap = 2f;
        private const float TopH = 20f, TabH = 22f, Pad = 6f;   // TopH = the close-button strip / drag handle (no title)
        private const float ListTop = TopH + TabH + 6f;
        private const float BarW = 18f;                     // IMGUI's vertical scrollbar gutter
        private const float EdgeGrab = 6f;                  // width of the draggable resize border (fits the list's Pad margin, clear of the scrollbar)
        private const float MinW = 150f, MinH = 130f;       // don't let a resize shrink the panel past usefulness

        private RectTransform _host;    // the screen's Root — the design (800×600) space we position against
        private Image _blocker;         // invisible click blocker under the window
        private Camera _uiCam;

        private Rect _rect;             // window rect, in GUI (screen) pixels
        private bool _placed;           // default position resolved once (from the design-space anchor)
        private bool _open;
        private Vector2 _scroll;

        // Resize state. Until the user drags an edge the size tracks the CD column (SizeToDiscColumn); after that
        // _userSized freezes their chosen size. During a drag _resizeGrab is the (window-local) offset from the cursor
        // to the corner being moved, and _resizeTo is the size we want applied AFTER GUI.Window returns (a size set
        // inside the window callback would be clobbered by GUI.Window's own return value).
        private bool _userSized;
        private bool _resizeRight, _resizeBottom;
        private Vector2 _resizeGrab;
        private Vector2 _resizeTo;

        private IReadOnlyList<SongCatalog.Entry> _pool = new List<SongCatalog.Entry>();
        private List<SongBucket> _buckets = new List<SongBucket>();
        private SongGroupMode _mode = SongGroupMode.Folder;   // 預設分類 = 資料夾
        private string _activeKey;
        private Action<SongBucket> _onPick;
        private Action _onRefresh;

        // 更新 (re-scan) in progress: the bucket list is replaced by a progress line and every control is inert, so
        // nothing can pick a bucket whose Entry objects the scan is about to throw away. Driven by SetBusy.
        private bool _busy;
        private string _busyLine = "";

        // 解析度縮放。IMGUI 一律以**實際螢幕像素**作畫，而遊戲畫面是 800×600 的設計框被拉伸/加黑邊後鋪滿螢幕
        // (AspectController)。視窗外框本來就跟著解析度長大(SizeToDiscColumn)，但裡面的列高/頁籤/字級/捲軸若照舊
        // 用固定像素，畫面一放大就只有框變大、字還是那麼小。_s = 設計像素→螢幕像素的倍率，畫面裡每個尺寸都乘上它。
        private float _s = 1f;
        private float PX(float design) => design * _s;

        private GUIStyle _winStyle, _rowStyle, _countStyle, _emptyStyle;
        private GUISkin _baseSkin, _skin;   // _skin = 依 _s 放大過的內建 skin 複本(字級、捲軸寬…)
        private float _skinScale = -1f;     // _skin 是用哪個 _s 做的（變了才重做）
        private Texture2D _bgTex;       // flat plate behind the window (replaces the skin's framed background)
        private string[] _tabLabels;

        public bool Visible => _open;
        public SongGroupMode Mode => _mode;
        public string ActiveKey => _activeKey;

        /// <summary>True while the mouse is over the open window — the host screen then leaves the wheel to us
        /// (so scrolling the bucket list doesn't also step through songs).</summary>
        public bool PointerOver
        {
            get
            {
                if (!_open) return false;
                var m = Input.mousePosition;
                return _rect.Contains(new Vector2(m.x, Screen.height - m.y));   // GUI space is y-down
            }
        }

        private static string L(string k) => LocalizationManager.Get(k);

        /// <summary>Build the panel (hidden). <paramref name="onPick"/> fires with the bucket whose songs the host
        /// should load into the song list — on a row click, and whenever the grouping tab changes.
        /// <paramref name="onRefresh"/> fires when the 更新 button is pressed: the host re-scans the song folders and
        /// feeds the panel a new pool (see SongSelectScreen.BeginRescan).</summary>
        public static SongGroupPanel Create(RectTransform host, Action<SongBucket> onPick, Action onRefresh = null)
        {
            var root = UIKit.NewRect(host, "GroupPanel");
            UIKit.Stretch(root);
            var p = root.gameObject.AddComponent<SongGroupPanel>();
            p._host = host;
            p._onPick = onPick;
            p._onRefresh = onRefresh;
            p._rect = new Rect(DesignX, DesignY, DesignW, DesignH);   // px fallback; SizeToDiscColumn re-derives it

            p._blocker = UIKit.AddImage(root, "GroupPanelBlocker", new Color(0f, 0f, 0f, 0f), raycast: true);
            p._blocker.rectTransform.anchorMin = p._blocker.rectTransform.anchorMax = new Vector2(0f, 1f);
            p._blocker.rectTransform.pivot = new Vector2(0f, 1f);
            p._blocker.gameObject.SetActive(false);
            return p;
        }

        // ---------------- host API ----------------

        /// <summary>Set the songs the panel groups (the external / user Songs/ library) and rebuild the buckets.</summary>
        public void SetPool(IReadOnlyList<SongCatalog.Entry> pool)
        {
            _pool = pool ?? new List<SongCatalog.Entry>();
            Rebuild();
        }

        /// <summary>Swap in a freshly scanned pool and land back on <paramref name="key"/>'s bucket (else the first
        /// one), firing <c>onPick</c> so the host's row list is rebuilt from the NEW entries. Unlike <see cref="Open"/>
        /// this never changes visibility — a 更新 finishing must not pop the window back up if the player closed it
        /// meanwhile, nor hide it if they didn't.</summary>
        public void Reload(IReadOnlyList<SongCatalog.Entry> pool, string key)
        {
            SetPool(pool);
            PickByKey(key);
        }

        /// <summary>Put the panel into (or out of) its 更新 progress state: while busy the bucket list is hidden
        /// behind <paramref name="line"/> and both the refresh button and the buckets are inert. The host calls this
        /// around the re-scan and hands it the scanner's progress text.</summary>
        public void SetBusy(bool busy, string line = "")
        {
            _busy = busy;
            _busyLine = line ?? "";
        }

        /// <summary>Show the panel; select <paramref name="key"/>'s bucket if it still exists, else the first one.
        /// Either way the host gets an <c>onPick</c> so the song list matches what the panel highlights.</summary>
        public void Open(string key)
        {
            _open = true;
            transform.SetAsLastSibling();   // blocker above the dialog, so it wins the raycast
            RefreshScale();                 // PickByKey → ScrollTo 會用到 _s，先量好（OnGUI 還沒跑過）
            PlaceDefault();
            Rebuild();
            PickByKey(key);
        }

        public void Close()
        {
            _open = false;
            if (_blocker != null) _blocker.gameObject.SetActive(false);
        }

        private void OnDisable() => Close();

        // ---------------- data ----------------

        private void SetMode(SongGroupMode mode)
        {
            if (_mode == mode) return;
            _mode = mode;
            _activeKey = null;
            _scroll = Vector2.zero;
            Rebuild();
            PickByKey(null);   // land on the first bucket of the new grouping (never leave the list stale/empty)
        }

        /// <summary>Select the bucket named <paramref name="key"/>, else the first bucket; fires onPick.</summary>
        private void PickByKey(string key)
        {
            int i = SongGrouping.IndexOfKey(_buckets, key);
            if (i < 0 && _buckets.Count > 0) i = 0;
            if (i < 0) { _activeKey = null; _onPick?.Invoke(null); return; }
            Pick(_buckets[i]);
            ScrollTo(i);
        }

        private void Pick(SongBucket b)
        {
            _activeKey = b?.Key;
            _onPick?.Invoke(b);
        }

        private void Rebuild() => _buckets = SongGrouping.Build(_pool, _mode);

        /// <summary>Scroll bucket <paramref name="index"/> into view (a restored bucket can be far down the list).</summary>
        private void ScrollTo(int index)
        {
            float viewH = Mathf.Max(PX(RowH), _rect.height - PX(ListTop) - PX(Pad));
            float y = index * PX(RowH + RowGap);
            float max = Mathf.Max(0f, _buckets.Count * PX(RowH + RowGap) - viewH);
            _scroll.y = Mathf.Clamp(y - viewH * 0.5f, 0f, max);
        }

        // ---------------- draw ----------------

        private void OnGUI()
        {
            if (!_open) return;
            RefreshScale();   // 先量這一幀的「設計像素→螢幕像素」倍率，下面所有尺寸/字級都跟著它
            EnsureSkin();
            EnsureStyles();
            if (!_userSized) SizeToDiscColumn();   // default size = the disc column; once the user drags an edge it sticks

            // 整體不透明度（config.ini 的 SongUiAlpha，預設 0.6）：GUI.color 的 alpha 會一路乘到視窗底板與內部文字/按鈕，
            // 所以整個面板連同內容一起變半透明，讓底下的唱片欄若隱若現。GUI.color 會帶進 window callback（IMGUI 全域）。
            var prevColor = GUI.color;
            var prevSkin = GUI.skin;
            GUI.skin = _skin;   // 放大版 skin：捲軸/按鈕/字級都照 _s（GUI.skin 是 IMGUI 全域，畫完要換回去）
            GUI.color = new Color(prevColor.r, prevColor.g, prevColor.b, prevColor.a * Mathf.Clamp01(Sdo.Settings.RoomConfig.songUiAlpha));
            _rect = GUI.Window(WindowId, _rect, DrawWindow, GUIContent.none, _winStyle);   // no title, no white frame
            GUI.color = prevColor;
            GUI.skin = prevSkin;

            // Apply a size the resize-grip drag asked for this frame (see _resizeTo), then keep the window sane: a size
            // between the minimum and the viewport, and a position that keeps it fully on screen.
            if (_userSized) { _rect.width = _resizeTo.x; _rect.height = _resizeTo.y; }
            _rect.width = Mathf.Clamp(_rect.width, PX(MinW), Screen.width);
            _rect.height = Mathf.Clamp(_rect.height, PX(MinH), Screen.height);
            _rect.x = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, Screen.width - _rect.width));
            _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, Screen.height - _rect.height));
        }

        private void DrawWindow(int id)
        {
            float w = _rect.width, h = _rect.height;

            // 更新 (re-scan the song folders) sits left of 關閉, so songs added / edited / removed on disk can be
            // picked up without restarting the game. Inert while a scan is running (its label becomes the progress).
            GUI.enabled = !_busy;
            if (GUI.Button(new Rect(w - PX(108f), PX(2f), PX(52f), PX(16f)), L("songselect.group_refresh")))
            {
                UiSfx.Play(UiSfx.Click);
                GUI.enabled = true;
                _onRefresh?.Invoke();
                return;
            }
            GUI.enabled = true;

            if (GUI.Button(new Rect(w - PX(52f), PX(2f), PX(46f), PX(16f)), L("common.close")))
            {
                UiSfx.Play(UiSfx.Click);
                Close();
                return;
            }

            // While re-scanning, the buckets on screen point at Entry objects the scan is about to replace — so the
            // whole body is swapped for the progress line and nothing below is drawn (nothing to click, nothing stale).
            if (_busy)
            {
                GUI.Label(new Rect(PX(Pad), PX(ListTop), w - PX(Pad * 2f), h - PX(ListTop) - PX(Pad)), _busyLine, _emptyStyle);
                HandleResizeGrip(w, h);
                GUI.DragWindow(new Rect(0f, 0f, w, PX(TopH)));
                return;
            }

            // grouping tabs — Group / Song / Artist / BPM
            int cur = Array.IndexOf(SongGrouping.Modes, _mode);
            int next = GUI.Toolbar(new Rect(PX(Pad), PX(TopH), w - PX(Pad * 2f), PX(TabH)), cur, _tabLabels);
            if (next != cur && next >= 0)
            {
                UiSfx.Play(UiSfx.Click);
                SetMode(SongGrouping.Modes[next]);
            }

            var view = new Rect(PX(Pad), PX(ListTop), w - PX(Pad * 2f), h - PX(ListTop) - PX(Pad));
            if (_buckets.Count == 0)
            {
                GUI.Label(view, L("songselect.group_empty"), _emptyStyle);
                HandleResizeGrip(w, h);
                GUI.DragWindow(new Rect(0f, 0f, w, PX(TopH)));
                return;
            }

            // bucket list — one row per section, scrolled by IMGUI's own draggable slider on the right
            float rowW = view.width - PX(BarW);
            var content = new Rect(0f, 0f, rowW, _buckets.Count * PX(RowH + RowGap));
            _scroll = GUI.BeginScrollView(view, _scroll, content);
            var bg = GUI.backgroundColor;
            for (int i = 0; i < _buckets.Count; i++)
            {
                var b = _buckets[i];
                var r = new Rect(0f, i * PX(RowH + RowGap), rowW, PX(RowH));
                bool sel = string.Equals(b.Key, _activeKey, StringComparison.OrdinalIgnoreCase);
                GUI.backgroundColor = sel ? new Color(1f, 0.45f, 0.75f) : bg;   // picked bucket tints pink
                if (GUI.Button(r, LabelOf(b.Key, _mode), _rowStyle))
                {
                    UiSfx.Play(UiSfx.Click);
                    Pick(b);
                }
                GUI.Label(r, b.Count.ToString(), _countStyle);   // song count, right-aligned in the same row
            }
            GUI.backgroundColor = bg;
            GUI.EndScrollView();

            HandleResizeGrip(w, h);   // AFTER the scroll view, so its scrollbar claims its own gutter first
            GUI.DragWindow(new Rect(0f, 0f, w, PX(TopH)));   // drag by the top strip (where the close button sits)
        }

        // Free resize: the window's right edge / bottom edge / bottom-right corner are drag handles (each a thin strip
        // in the list's padding, clear of the scrollbar). hotControl capture keeps the drag alive even when the cursor
        // leaves the window — same mechanism GUI.DragWindow uses. The new size is stashed in _resizeTo and applied in
        // OnGUI (setting _rect.width/height here would be overwritten by GUI.Window's own return value).
        private void HandleResizeGrip(float w, float h)
        {
            var rightEdge  = new Rect(w - PX(EdgeGrab), PX(TopH), PX(EdgeGrab), h - PX(TopH));   // below the close strip
            var bottomEdge = new Rect(0f, h - PX(EdgeGrab), w, PX(EdgeGrab));
            int id = GUIUtility.GetControlID(FocusType.Passive);
            var e = Event.current;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    bool onR = rightEdge.Contains(e.mousePosition);
                    bool onB = bottomEdge.Contains(e.mousePosition);
                    if (onR || onB)
                    {
                        GUIUtility.hotControl = id;
                        _userSized = true;
                        _resizeRight = onR; _resizeBottom = onB;
                        _resizeGrab = new Vector2(w, h) - e.mousePosition;   // keep the grabbed point under the cursor
                        _resizeTo = new Vector2(w, h);
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        if (_resizeRight)  _resizeTo.x = e.mousePosition.x + _resizeGrab.x;
                        if (_resizeBottom) _resizeTo.y = e.mousePosition.y + _resizeGrab.y;
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        _resizeRight = _resizeBottom = false;
                        e.Use();
                    }
                    break;
            }

            if (e.type == EventType.Repaint)   // a subtle triangle-of-dots grip in the bottom-right corner
            {
                var gc = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, 0.4f);
                Dot(w, h, 4f, 4f); Dot(w, h, 4f, 9f); Dot(w, h, 9f, 4f);
                Dot(w, h, 4f, 14f); Dot(w, h, 9f, 9f); Dot(w, h, 14f, 4f);
                GUI.color = gc;
            }
        }

        private void Dot(float w, float h, float dx, float dy)
            => GUI.DrawTexture(new Rect(w - PX(dx), h - PX(dy), PX(2f), PX(2f)), Texture2D.whiteTexture);

        /// <summary>
        /// 依 <see cref="_s"/> 做一份放大版的內建 skin。IMGUI 內建 skin 的字級、捲軸寬度、按鈕內距全是固定像素，
        /// 解析度拉大時完全不會跟著長大 —— 這是「視窗變大但字還是小小的」的根因。
        /// 字級走「基準字級 × 倍率」重新點陣化(不是把小字拉大)，所以放大後依然銳利；用 GUI.matrix 硬縮則會糊掉。
        /// 只有倍率變了(改解析度/切視窗)才重做，平常一幀都不花。
        /// </summary>
        private void EnsureSkin()
        {
            if (_baseSkin == null) _baseSkin = GUI.skin;   // 內建 skin：一定要在換上自己那份之前抓
            if (_skin != null && Mathf.Abs(_skinScale - _s) < 0.02f) return;

            if (_skin != null) Destroy(_skin);
            _skin = Instantiate(_baseSkin);               // 深複製(GUIStyle 是純資料，不是 UnityEngine.Object)
            _skin.hideFlags = HideFlags.HideAndDontSave;
            _skinScale = _s;

            ScaleStyle(_skin.label); ScaleStyle(_skin.button);
            ScaleStyle(_skin.box); ScaleStyle(_skin.window);
            ScaleStyle(_skin.toggle); ScaleStyle(_skin.textField);
            ScaleStyle(_skin.verticalScrollbar); ScaleStyle(_skin.verticalScrollbarThumb);
            ScaleStyle(_skin.verticalScrollbarUpButton); ScaleStyle(_skin.verticalScrollbarDownButton);
            ScaleStyle(_skin.horizontalScrollbar); ScaleStyle(_skin.horizontalScrollbarThumb);
            ScaleStyle(_skin.horizontalScrollbarLeftButton); ScaleStyle(_skin.horizontalScrollbarRightButton);

            _rowStyle = null;   // 自己的樣式是從 skin 複製出來的 → 一起重建（EnsureStyles 以 _rowStyle 當旗標）
        }

        // 只放大「跟著解析度該變大」的量：字級、固定寬高(捲軸粗細)、內距/外距。
        // border 不動 —— 那是九宮格對應到來源貼圖的像素數，乘上去只會把邊框拉糊。
        private void ScaleStyle(GUIStyle st)
        {
            if (st == null) return;
            // fontSize 0 = 「用字型自己的預設級數」→ 先問出那個級數當基準(自己的 font > skin 的 font > 12)，
            // ×倍率後寫回去。倍率 1 時寫回去的就是原本那個級數 = 完全沒動（800×600 不會有任何改變）。
            int basePt = st.fontSize > 0 ? st.fontSize
                       : st.font != null && st.font.fontSize > 0 ? st.font.fontSize
                       : _baseSkin.font != null && _baseSkin.font.fontSize > 0 ? _baseSkin.font.fontSize : 12;
            st.fontSize = Mathf.Max(1, Mathf.RoundToInt(basePt * _s));
            if (st.fixedWidth > 0f) st.fixedWidth = PX(st.fixedWidth);
            if (st.fixedHeight > 0f) st.fixedHeight = PX(st.fixedHeight);
            st.padding = ScaleOffset(st.padding);
            st.margin = ScaleOffset(st.margin);
        }

        private RectOffset ScaleOffset(RectOffset r)
            => new RectOffset(Mathf.RoundToInt(PX(r.left)), Mathf.RoundToInt(PX(r.right)),
                              Mathf.RoundToInt(PX(r.top)), Mathf.RoundToInt(PX(r.bottom)));

        private void EnsureStyles()
        {
            if (_rowStyle != null) return;

            // Flat, borderless window plate: the built-in window style draws a light 3D frame ("white edge"), so the
            // background is swapped for a plain 1×1 texture and every border/padding zeroed.
            if (_bgTex == null)
            {
                _bgTex = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
                _bgTex.SetPixel(0, 0, new Color(0.10f, 0.07f, 0.14f, 0.94f));
                _bgTex.Apply();
            }
            // 一律從**放大過的** _skin 複製（不是內建 GUI.skin），字級才會是這個解析度該有的大小。
            _winStyle = new GUIStyle(_skin.window)
            {
                border = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
                overflow = new RectOffset(0, 0, 0, 0),
                contentOffset = Vector2.zero,
            };
            _winStyle.normal.background = _bgTex;
            _winStyle.onNormal.background = _bgTex;
            _winStyle.focused.background = _bgTex;
            _winStyle.onFocused.background = _bgTex;

            _rowStyle = new GUIStyle(_skin.button) { alignment = TextAnchor.MiddleLeft };
            _rowStyle.padding = ScaleOffset(new RectOffset(8, 34, 2, 2));   // keep the label clear of the count on the right
            _countStyle = new GUIStyle(_skin.label) { alignment = TextAnchor.MiddleRight };
            _countStyle.padding = ScaleOffset(new RectOffset(0, 8, 0, 0));
            _emptyStyle = new GUIStyle(_skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true };

            _tabLabels = new string[SongGrouping.Modes.Length];
            for (int i = 0; i < _tabLabels.Length; i++) _tabLabels[i] = L(TabKey(SongGrouping.Modes[i]));
        }

        private void OnDestroy()
        {
            if (_bgTex != null) Destroy(_bgTex);
            if (_skin != null) Destroy(_skin);
        }

        /// <summary>
        /// 量出這一幀「設計像素(800×600) → 螢幕像素」的倍率。拉伸模式(<see cref="AspectMode.Stretch"/>)下 x/y 倍率
        /// 不一樣(例:1920×1080 是 2.4 / 1.8)，取小的當統一倍率，內容才不會在窄的那一軸被撐爆。量不到(還沒有
        /// host/相機)就沿用上一次的值。
        /// </summary>
        private void RefreshScale()
        {
            if (!DesignToGui(Vector2.zero, out var a) || !DesignToGui(new Vector2(DesignW, DesignH), out var b)) return;
            float s = Mathf.Min(Mathf.Abs(b.x - a.x) / DesignW, Mathf.Abs(b.y - a.y) / DesignH);
            if (s > 0.05f) _s = s;
        }

        /// <summary>Default size = the width of the dialog's CD column beneath it (re-derived each frame so it tracks
        /// the resolution) — UNTIL the user drags a resize edge, after which <see cref="_userSized"/> gates this off and
        /// their chosen size sticks. The dragged POSITION is always kept.</summary>
        private void SizeToDiscColumn()
        {
            if (!DesignToGui(new Vector2(DesignX, DesignY), out var tl) ||
                !DesignToGui(new Vector2(DesignX + DesignW, DesignY + DesignH), out var br)) return;
            _rect.width = Mathf.Abs(br.x - tl.x);
            _rect.height = Mathf.Abs(br.y - tl.y);
        }

        // ---------------- placement / click blocking ----------------

        /// <summary>Put the window where the dialog's left column is (design 800×600 coords), once. After that the
        /// user's dragged position sticks.</summary>
        private void PlaceDefault()
        {
            if (_placed) return;
            if (DesignToGui(new Vector2(DesignX, DesignY), out var p)) _rect.position = p;
            _placed = true;
        }

        /// <summary>Design (800×600, y-down) point → GUI (screen pixel, y-down) point.</summary>
        private bool DesignToGui(Vector2 design, out Vector2 gui)
        {
            gui = Vector2.zero;
            if (_host == null) return false;
            var cam = UiCam;
            if (cam == null) return false;
            var r = _host.rect;
            var local = new Vector2(r.xMin + design.x, r.yMax - design.y);
            var screen = cam.WorldToScreenPoint(_host.TransformPoint(local));
            gui = new Vector2(screen.x, Screen.height - screen.y);
            return true;
        }

        /// <summary>GUI (screen pixel, y-down) point → design (800×600, y-down) point.</summary>
        private bool GuiToDesign(Vector2 gui, out Vector2 design)
        {
            design = Vector2.zero;
            if (_host == null) return false;
            var screen = new Vector2(gui.x, Screen.height - gui.y);
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_host, screen, UiCam, out var local)) return false;
            var r = _host.rect;
            design = new Vector2(local.x - r.xMin, r.yMax - local.y);
            return true;
        }

        // Keep the invisible UGUI blocker on top of, and exactly under, the IMGUI window: IMGUI is drawn over the
        // canvas but shares the mouse with it, so without this a click on a bucket would ALSO hit whatever dialog
        // widget sits beneath the window (the vinyl's spin toggle, a song row, …).
        private void LateUpdate()
        {
            if (_blocker == null) return;
            if (!_open) { if (_blocker.gameObject.activeSelf) _blocker.gameObject.SetActive(false); return; }
            if (!GuiToDesign(_rect.position, out var tl) ||
                !GuiToDesign(_rect.position + new Vector2(_rect.width, _rect.height), out var br)) return;

            var rt = _blocker.rectTransform;
            rt.anchoredPosition = new Vector2(tl.x, -tl.y);
            rt.sizeDelta = new Vector2(Mathf.Abs(br.x - tl.x), Mathf.Abs(br.y - tl.y));
            if (!_blocker.gameObject.activeSelf) _blocker.gameObject.SetActive(true);
        }

        private Camera UiCam
        {
            get
            {
                if (_uiCam == null)
                {
                    var c = GetComponentInParent<Canvas>();
                    _uiCam = c != null ? c.worldCamera : null;
                }
                return _uiCam;
            }
        }

        // ---------------- labels ----------------

        /// <summary>Display name of a section: the folder name as-is; the letter buckets as 0-9 / A..Z / 其他;
        /// a BPM band as "100-149" (BPM 未知 for songs with no BPM).</summary>
        public static string LabelOf(string key, SongGroupMode mode)
        {
            switch (mode)
            {
                case SongGroupMode.Folder:
                    return string.IsNullOrEmpty(key) ? L("songselect.group_uncat") : key;
                case SongGroupMode.Bpm:
                    return key == SongGrouping.UnknownBpm ? L("songselect.group_bpm_unknown") : key;
                default:
                    if (key == SongGrouping.Num) return "0-9";
                    if (key == SongGrouping.Other) return L("songselect.group_other");
                    return key;
            }
        }

        private static string TabKey(SongGroupMode m)
        {
            switch (m)
            {
                case SongGroupMode.Title: return "songselect.group_title";
                case SongGroupMode.Artist: return "songselect.group_artist";
                case SongGroupMode.Bpm: return "songselect.group_bpm";
                default: return "songselect.group_folder";
            }
        }
    }
}
