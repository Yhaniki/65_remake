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
    /// Drawn with IMGUI (<c>GUI.Window</c>), like the project's other tool panels (see MmdDebug): the window drags by
    /// its title bar and the scroll view brings its own draggable slider — no canvas art, no layout groups, nothing to
    /// mis-lay-out. This browser never existed in the original game, so it deliberately wears the plain dev-tool look
    /// instead of imitating MUSICSELDLG.
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
        private const float DesignX = 44f, DesignY = 84f, DesignW = 237f, DesignH = 300f;
        private const float RowH = 22f, RowGap = 2f;
        private const float TopH = 20f, TabH = 22f, Pad = 6f;   // TopH = the close-button strip / drag handle (no title)
        private const float ListTop = TopH + TabH + 6f;
        private const float BarW = 18f;                     // IMGUI's vertical scrollbar gutter

        private RectTransform _host;    // the screen's Root — the design (800×600) space we position against
        private Image _blocker;         // invisible click blocker under the window
        private Camera _uiCam;

        private Rect _rect;             // window rect, in GUI (screen) pixels
        private bool _placed;           // default position resolved once (from the design-space anchor)
        private bool _open;
        private Vector2 _scroll;

        private IReadOnlyList<SongCatalog.Entry> _pool = new List<SongCatalog.Entry>();
        private List<SongBucket> _buckets = new List<SongBucket>();
        private SongGroupMode _mode = SongGroupMode.Folder;   // 預設分類 = 資料夾
        private string _activeKey;
        private Action<SongBucket> _onPick;

        private GUIStyle _winStyle, _rowStyle, _countStyle, _emptyStyle;
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
        /// should load into the song list — on a row click, and whenever the grouping tab changes.</summary>
        public static SongGroupPanel Create(RectTransform host, Action<SongBucket> onPick)
        {
            var root = UIKit.NewRect(host, "GroupPanel");
            UIKit.Stretch(root);
            var p = root.gameObject.AddComponent<SongGroupPanel>();
            p._host = host;
            p._onPick = onPick;
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

        /// <summary>Show the panel; select <paramref name="key"/>'s bucket if it still exists, else the first one.
        /// Either way the host gets an <c>onPick</c> so the song list matches what the panel highlights.</summary>
        public void Open(string key)
        {
            _open = true;
            transform.SetAsLastSibling();   // blocker above the dialog, so it wins the raycast
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
            float viewH = Mathf.Max(RowH, _rect.height - ListTop - Pad);
            float y = index * (RowH + RowGap);
            float max = Mathf.Max(0f, _buckets.Count * (RowH + RowGap) - viewH);
            _scroll.y = Mathf.Clamp(y - viewH * 0.5f, 0f, max);
        }

        // ---------------- draw ----------------

        private void OnGUI()
        {
            if (!_open) return;
            EnsureStyles();
            SizeToDiscColumn();
            _rect = GUI.Window(WindowId, _rect, DrawWindow, GUIContent.none, _winStyle);   // no title, no white frame
            _rect.x = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, Screen.width - _rect.width));
            _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, Screen.height - _rect.height));
        }

        private void DrawWindow(int id)
        {
            float w = _rect.width, h = _rect.height;

            if (GUI.Button(new Rect(w - 52f, 2f, 46f, 16f), L("common.close")))
            {
                UiSfx.Play(UiSfx.Click);
                Close();
                return;
            }

            // grouping tabs — Group / Song / Artist / BPM
            int cur = Array.IndexOf(SongGrouping.Modes, _mode);
            int next = GUI.Toolbar(new Rect(Pad, TopH, w - Pad * 2f, TabH), cur, _tabLabels);
            if (next != cur && next >= 0)
            {
                UiSfx.Play(UiSfx.Click);
                SetMode(SongGrouping.Modes[next]);
            }

            var view = new Rect(Pad, ListTop, w - Pad * 2f, h - ListTop - Pad);
            if (_buckets.Count == 0)
            {
                GUI.Label(view, L("songselect.group_empty"), _emptyStyle);
                GUI.DragWindow(new Rect(0f, 0f, w, TopH));
                return;
            }

            // bucket list — one row per section, scrolled by IMGUI's own draggable slider on the right
            float rowW = view.width - BarW;
            var content = new Rect(0f, 0f, rowW, _buckets.Count * (RowH + RowGap));
            _scroll = GUI.BeginScrollView(view, _scroll, content);
            var bg = GUI.backgroundColor;
            for (int i = 0; i < _buckets.Count; i++)
            {
                var b = _buckets[i];
                var r = new Rect(0f, i * (RowH + RowGap), rowW, RowH);
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

            GUI.DragWindow(new Rect(0f, 0f, w, TopH));   // drag by the top strip (where the close button sits)
        }

        private void EnsureStyles()
        {
            if (_rowStyle != null) return;

            // Flat, borderless window plate: the built-in window style draws a light 3D frame ("white edge"), so the
            // background is swapped for a plain 1×1 texture and every border/padding zeroed.
            _bgTex = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            _bgTex.SetPixel(0, 0, new Color(0.10f, 0.07f, 0.14f, 0.94f));
            _bgTex.Apply();
            _winStyle = new GUIStyle(GUI.skin.window)
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

            _rowStyle = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft };
            _rowStyle.padding = new RectOffset(8, 34, 2, 2);   // keep the label clear of the count on the right
            _countStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleRight };
            _countStyle.padding = new RectOffset(0, 8, 0, 0);
            _emptyStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true };

            _tabLabels = new string[SongGrouping.Modes.Length];
            for (int i = 0; i < _tabLabels.Length; i++) _tabLabels[i] = L(TabKey(SongGrouping.Modes[i]));
        }

        private void OnDestroy()
        {
            if (_bgTex != null) Destroy(_bgTex);
        }

        /// <summary>Lock the window to the width of the dialog's CD column beneath it (and re-derive it whenever the
        /// window/resolution changes). The dragged POSITION is kept; only the size follows the design rect.</summary>
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
        /// a BPM band as "140-159" (BPM 未知 for songs with no BPM).</summary>
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
