using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Sdo.Game;
using Sdo.Localization;
using Sdo.UI.Catalog;
using Sdo.UI.Core;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>選歌：難度分頁 + 分頁歌曲列表 + 搜尋 + RANDOM + 舞台選擇 + 確定。</summary>
    public sealed class SongSelectScreen : UIScreenBase
    {
        public override ScreenId Id => ScreenId.SongSelect;
        private const int PageSize = 12;

        private SongListModel _model;
        private List<SongCatalog.Entry> _filtered = new List<SongCatalog.Entry>();
        private SongCatalog.Entry _selected;
        private int _difficulty = 0;   // default Easy (overwritten by Session in OnShow)
        private int _page;

        private RectTransform _listContent;
        private TMP_InputField _search;
        private TextMeshProUGUI _pageLabel;
        private Button[] _tabs;
        private TextMeshProUGUI _prevTitle, _prevArtist, _prevInfo;
        private Cycler _stageCycler;
        private List<StageInfo> _stages;

        private static string L(string k) => LocalizationManager.Get(k);

        protected override void BuildUI()
        {
            _model = SongListModel.FromCatalog();
            _stages = new List<StageInfo>(StageCatalog.Stages);

            UIKit.AddImage(Root, "Bg", UITheme.Bg);

            // header
            var header = UIKit.AddImage(Root, "Header", UITheme.Header).rectTransform;
            UIKit.Anchor(header, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1));
            header.sizeDelta = new Vector2(0, 56); header.anchoredPosition = Vector2.zero;
            var title = UIKit.AddLocText(header, "Title", "songselect.title", 22, UITheme.Text);
            UIKit.Anchor(title.rectTransform, new Vector2(0, 0), new Vector2(0.5f, 1), new Vector2(0, 0.5f));
            title.rectTransform.offsetMin = new Vector2(18, 0);
            var back = UIKit.AddLocButton(header, "Back", "common.back", UITheme.Secondary, UITheme.Text, 15);
            var brt = back.GetComponent<RectTransform>();
            UIKit.Anchor(brt, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f));
            brt.sizeDelta = new Vector2(96, 34); brt.anchoredPosition = new Vector2(-12, 0);
            back.onClick.AddListener(() => GoTo(ScreenId.Room));

            // ---- right preview panel ----
            var right = UIKit.AddImage(Root, "Preview", UITheme.Panel).rectTransform;
            right.anchorMin = new Vector2(1, 0); right.anchorMax = new Vector2(1, 1); right.pivot = new Vector2(1, 0.5f);
            right.sizeDelta = new Vector2(296, -(56 + 12 + 12));
            right.anchoredPosition = new Vector2(-12, -((56 + 12) - 12) * 0.5f);

            _prevTitle = UIKit.AddText(right, "PTitle", "-", 22, UITheme.Text, TextAlignmentOptions.Center, true);
            UIKit.Anchor(_prevTitle.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1));
            _prevTitle.rectTransform.sizeDelta = new Vector2(-24, 60); _prevTitle.rectTransform.anchoredPosition = new Vector2(0, -16);

            _prevArtist = UIKit.AddText(right, "PArtist", "", 16, UITheme.TextDim, TextAlignmentOptions.Center);
            UIKit.Anchor(_prevArtist.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1));
            _prevArtist.rectTransform.sizeDelta = new Vector2(-24, 26); _prevArtist.rectTransform.anchoredPosition = new Vector2(0, -78);

            _prevInfo = UIKit.AddText(right, "PInfo", "", 16, UITheme.Accent, TextAlignmentOptions.Center);
            UIKit.Anchor(_prevInfo.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1));
            _prevInfo.rectTransform.sizeDelta = new Vector2(-24, 26); _prevInfo.rectTransform.anchoredPosition = new Vector2(0, -110);

            // stage selector
            var stageTitle = UIKit.AddLocText(right, "StageTitle", "songselect.stage", 16, UITheme.TextDim);
            UIKit.Anchor(stageTitle.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, 1));
            stageTitle.rectTransform.sizeDelta = new Vector2(-24, 24); stageTitle.rectTransform.anchoredPosition = new Vector2(16, -168);

            var stageNames = new string[_stages.Count];
            for (int i = 0; i < _stages.Count; i++) stageNames[i] = _stages[i].NameZh;
            _stageCycler = UIKit.AddCycler(right, "StageCycler", stageNames, IndexOfStage(Ctx.Session.StageId), out var scrt);
            UIKit.Anchor(scrt, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1));
            scrt.sizeDelta = new Vector2(-24, 40); scrt.anchoredPosition = new Vector2(0, -196);

            // random + confirm
            var random = UIKit.AddLocButton(right, "Random", "songselect.random", UITheme.Accent, UITheme.OnPrimary, 18);
            var rrt = random.GetComponent<RectTransform>();
            UIKit.Anchor(rrt, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 0));
            rrt.sizeDelta = new Vector2(-24, 48); rrt.anchoredPosition = new Vector2(0, 76);
            random.onClick.AddListener(OnRandom);

            var confirm = UIKit.AddLocButton(right, "Confirm", "songselect.confirm", UITheme.Primary, UITheme.OnPrimary, 20);
            var cfrt = confirm.GetComponent<RectTransform>();
            UIKit.Anchor(cfrt, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 0));
            cfrt.sizeDelta = new Vector2(-24, 54); cfrt.anchoredPosition = new Vector2(0, 16);
            confirm.onClick.AddListener(OnConfirm);

            // ---- left list panel ----
            var left = UIKit.AddImage(Root, "ListPanel", UITheme.Panel).rectTransform;
            left.anchorMin = new Vector2(0, 0); left.anchorMax = new Vector2(1, 1);
            left.offsetMin = new Vector2(12, 12); left.offsetMax = new Vector2(-(296 + 20), -(56 + 12));

            // difficulty tabs
            string[] tabKeys = { "difficulty.easy", "difficulty.normal", "difficulty.hard" };
            _tabs = new Button[3];
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                var tab = UIKit.AddLocButton(left, "Tab" + i, tabKeys[i], UITheme.Secondary, UITheme.Text, 16);
                var trt = tab.GetComponent<RectTransform>();
                UIKit.Anchor(trt, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
                trt.sizeDelta = new Vector2(96, 34); trt.anchoredPosition = new Vector2(12 + i * 102, -10);
                tab.onClick.AddListener(() => SetDifficulty(idx));
                _tabs[i] = tab;
            }

            _search = UIKit.AddInputField(left, "Search", L("songselect.search"), 15);
            var sert = _search.GetComponent<RectTransform>();
            UIKit.Anchor(sert, new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1));
            sert.sizeDelta = new Vector2(128, 34); sert.anchoredPosition = new Vector2(-12, -11);
            _search.onValueChanged.AddListener(_ => { _page = 0; ApplyFilter(); });

            // Tight rows + a taller viewport so a full page (PageSize) shows at once — no scrolling needed.
            var listScroll = UIKit.AddVerticalScroll(left, "ListScroll", out _listContent, 2f, 4);
            UIKit.Stretch(listScroll.GetComponent<RectTransform>(), 8, 44, 8, 50);

            // paging bar
            var prev = UIKit.AddLocButton(left, "Prev", "songselect.prev", UITheme.Secondary, UITheme.Text, 15);
            var pvrt = prev.GetComponent<RectTransform>();
            UIKit.Anchor(pvrt, new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0));
            pvrt.sizeDelta = new Vector2(120, 36); pvrt.anchoredPosition = new Vector2(12, 10);
            prev.onClick.AddListener(() => ChangePage(-1));

            var next = UIKit.AddLocButton(left, "Next", "songselect.next", UITheme.Secondary, UITheme.Text, 15);
            var nxrt = next.GetComponent<RectTransform>();
            UIKit.Anchor(nxrt, new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0));
            nxrt.sizeDelta = new Vector2(120, 36); nxrt.anchoredPosition = new Vector2(-12, 10);
            next.onClick.AddListener(() => ChangePage(1));

            _pageLabel = UIKit.AddText(left, "PageLabel", "", 15, UITheme.TextDim, TextAlignmentOptions.Center);
            UIKit.Anchor(_pageLabel.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0));
            _pageLabel.rectTransform.sizeDelta = new Vector2(220, 36); _pageLabel.rectTransform.anchoredPosition = new Vector2(0, 28);
        }

        public override void OnShow()
        {
            _difficulty = (int)Ctx.Session.Difficulty;
            ApplyFilter();
            UpdatePreview();
        }

        private int IndexOfStage(int id)
        {
            for (int i = 0; i < _stages.Count; i++) if (_stages[i].Id == id) return i;
            return 0;
        }

        private void SetDifficulty(int d)
        {
            _difficulty = Mathf.Clamp(d, 0, 2);
            RenderTabs();
            RenderPage();
            UpdatePreview();
        }

        private void RenderTabs()
        {
            for (int i = 0; i < _tabs.Length; i++)
                if (_tabs[i] != null && _tabs[i].targetGraphic is Image img)
                    img.color = (i == _difficulty) ? UITheme.Primary : UITheme.Secondary;
        }

        private void ApplyFilter()
        {
            _filtered = _model.Filter(_search != null ? _search.text : null);
            int maxPage = Mathf.Max(0, (_filtered.Count - 1) / PageSize);
            _page = Mathf.Clamp(_page, 0, maxPage);
            RenderTabs();
            RenderPage();
        }

        private void ChangePage(int delta)
        {
            // wrap around: prev on the first page -> last page, next on the last page -> first page.
            int pages = Mathf.Max(1, (_filtered.Count + PageSize - 1) / PageSize);
            _page = ((_page + delta) % pages + pages) % pages;
            RenderPage();
        }

        private void RenderPage()
        {
            UIKit.Clear(_listContent);
            int maxPage = Mathf.Max(0, (_filtered.Count - 1) / PageSize);
            _pageLabel.text = _filtered.Count == 0
                ? L("songselect.no_songs")
                : L("songselect.page").Replace("{0}", (_page + 1).ToString()).Replace("{1}", (maxPage + 1).ToString());

            int start = _page * PageSize;
            int end = Mathf.Min(start + PageSize, _filtered.Count);
            for (int i = start; i < end; i++) AddSongRow(_filtered[i]);
        }

        private void AddSongRow(SongCatalog.Entry e)
        {
            bool sel = ReferenceEquals(e, _selected);
            var rowImg = UIKit.AddImage(_listContent, "S" + e.fileId, sel ? UITheme.RowSelected : UITheme.Row, true);
            UIKit.Layout(rowImg.gameObject, 32);   // 12 rows fit the viewport without scrolling
            var btn = rowImg.gameObject.AddComponent<Button>();
            btn.targetGraphic = rowImg;
            btn.onClick.AddListener(() => Select(e));

            // icon (best effort) or placeholder
            var iconRt = UIKit.AddImage(rowImg.transform, "Icon", new Color(0.3f, 0.24f, 0.42f, 1f)).rectTransform;
            UIKit.Anchor(iconRt, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f));
            iconRt.sizeDelta = new Vector2(28, 28); iconRt.anchoredPosition = new Vector2(20, 0);
            var sprite = SongIcons.Load(e.fileId);
            if (sprite != null) iconRt.GetComponent<Image>().sprite = sprite;
            else
            {
                var ic = UIKit.AddText(iconRt, "i", string.IsNullOrEmpty(e.title) ? "?" : e.title.Substring(0, 1), 15, UITheme.Text, TextAlignmentOptions.Center);
                UIKit.Stretch(ic.rectTransform);
            }

            var titleT = UIKit.AddText(rowImg.transform, "T", e.title ?? e.gn, 15, UITheme.Text, TextAlignmentOptions.Left);
            UIKit.Anchor(titleT.rectTransform, new Vector2(0, 0.5f), new Vector2(0.62f, 1), new Vector2(0, 0.5f));
            titleT.rectTransform.offsetMin = new Vector2(44, 0); titleT.rectTransform.offsetMax = new Vector2(0, 0);

            var artistT = UIKit.AddText(rowImg.transform, "A", e.artist ?? "", 12, UITheme.TextDim, TextAlignmentOptions.Left);
            UIKit.Anchor(artistT.rectTransform, new Vector2(0, 0), new Vector2(0.62f, 0.5f), new Vector2(0, 0.5f));
            artistT.rectTransform.offsetMin = new Vector2(44, 0); artistT.rectTransform.offsetMax = new Vector2(0, 0);

            int lvl = e.Diff(_difficulty);
            var info = UIKit.AddText(rowImg.transform, "L", InfoText(e, lvl), 13, UITheme.Accent, TextAlignmentOptions.Right);
            UIKit.Anchor(info.rectTransform, new Vector2(0.62f, 0), new Vector2(1, 1), new Vector2(1, 0.5f));
            info.rectTransform.offsetMin = new Vector2(0, 0); info.rectTransform.offsetMax = new Vector2(-12, 0);
        }

        private string InfoText(SongCatalog.Entry e, int lvl)
        {
            var parts = new List<string>();
            if (lvl >= 0) parts.Add(L("songselect.level").Replace("{0}", lvl.ToString()));
            int dur = e.DurationSec(_difficulty);
            if (dur > 0) parts.Add(L("songselect.length").Replace("{0}", FormatDuration(dur)));
            return string.Join("   ", parts);
        }

        /// <summary>Seconds -> m:ss (e.g. 146 -> "2:26").</summary>
        private static string FormatDuration(int sec)
            => (sec / 60) + ":" + (sec % 60).ToString("00");

        private void Select(SongCatalog.Entry e)
        {
            _selected = e;
            RenderPage();
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            if (_selected == null)
            {
                _prevTitle.text = "-";
                _prevArtist.text = "";
                _prevInfo.text = "";
                return;
            }
            _prevTitle.text = _selected.title ?? _selected.gn;
            _prevArtist.text = _selected.artist ?? "";
            _prevInfo.text = InfoText(_selected, _selected.Diff(_difficulty));
        }

        private void OnRandom()
        {
            if (_filtered.Count == 0) return;
            int seed = System.Environment.TickCount;
            var pick = SongListModel.PickRandomFrom(_filtered, seed);
            if (pick == null) return;
            int idx = _filtered.IndexOf(pick);
            if (idx >= 0) _page = idx / PageSize;
            _selected = pick;
            RenderPage();
            UpdatePreview();
        }

        private void OnConfirm()
        {
            if (_selected == null) { Toast.Show(L("songselect.need_pick")); return; }
            var s = Ctx.Session;
            s.SongGn = _selected.gn;
            s.SongFileId = _selected.fileId;
            s.SongTitle = _selected.title ?? _selected.gn;
            s.SongArtist = _selected.artist;
            s.Difficulty = (Difficulty)_difficulty;
            var stage = _stages[Mathf.Clamp(_stageCycler.Index, 0, _stages.Count - 1)];
            s.StageId = stage.Id;
            s.StageFolder = stage.Folder;

            Ctx.Rooms.SetSong(s.SongTitle);
            GoTo(ScreenId.Room);
        }
    }
}
