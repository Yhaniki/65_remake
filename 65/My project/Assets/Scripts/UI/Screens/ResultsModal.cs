using System;
using TMPro;
using UnityEngine;
using Sdo.Localization;
using Sdo.Ruleset;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>結算模態:歌曲跳完(或失敗)後顯示分數與判定統計,按「返回」回到房間。</summary>
    public sealed class ResultsModal : MonoBehaviour
    {
        private CanvasGroup _cg;
        private TextMeshProUGUI _title, _scoreVal;
        private TextMeshProUGUI _perfect, _cool, _bad, _miss, _maxCombo;
        private Action _onClose;

        private static string L(string k) => LocalizationManager.Get(k);

        public void Build(RectTransform parent)
        {
            var root = UIKit.NewRect(parent, "ResultsModal");
            UIKit.Stretch(root);
            _cg = root.gameObject.AddComponent<CanvasGroup>();

            var dim = UIKit.AddImage(root, "Dim", new Color(0, 0, 0, 0.72f), true);
            UIKit.Stretch(dim.rectTransform);

            var panel = UIKit.AddImage(root, "Panel", UITheme.Panel).rectTransform;
            UIKit.Anchor(panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            panel.sizeDelta = new Vector2(520, 460);

            _title = UIKit.AddText(panel, "Title", "", 32, UITheme.Ready, TextAlignmentOptions.Center);
            UIKit.Anchor(_title.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1));
            _title.rectTransform.sizeDelta = new Vector2(0, 56); _title.rectTransform.anchoredPosition = new Vector2(0, -18);

            var scoreLbl = UIKit.AddLocText(panel, "ScoreLbl", "result.score", 18, UITheme.TextDim, TextAlignmentOptions.Center);
            UIKit.Anchor(scoreLbl.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1));
            scoreLbl.rectTransform.sizeDelta = new Vector2(0, 26); scoreLbl.rectTransform.anchoredPosition = new Vector2(0, -86);

            _scoreVal = UIKit.AddText(panel, "ScoreVal", "0", 48, UITheme.Accent, TextAlignmentOptions.Center);
            UIKit.Anchor(_scoreVal.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1));
            _scoreVal.rectTransform.sizeDelta = new Vector2(0, 64); _scoreVal.rectTransform.anchoredPosition = new Vector2(0, -116);

            // judgement breakdown (PERFECT/COOL/BAD/MISS are the in-game proper nouns -> not localized)
            _perfect = StatRow(panel, "PERFECT", 0, new Color(0.62f, 0.78f, 1f));
            _cool = StatRow(panel, "COOL", 1, new Color(0.55f, 0.86f, 0.62f));
            _bad = StatRow(panel, "BAD", 2, new Color(0.95f, 0.82f, 0.45f));
            _miss = StatRow(panel, "MISS", 3, new Color(0.92f, 0.5f, 0.5f));
            _maxCombo = StatRow(panel, L("result.max_combo"), 4, UITheme.Text);

            var back = UIKit.AddLocButton(panel, "Back", "result.back", UITheme.Primary, UITheme.OnPrimary, 20);
            var brt = back.GetComponent<RectTransform>();
            UIKit.Anchor(brt, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0));
            brt.sizeDelta = new Vector2(240, 52); brt.anchoredPosition = new Vector2(0, 22);
            back.onClick.AddListener(Close);

            SetVisible(false);
        }

        // one "LABEL                 value" line in the breakdown block (rows stack downward). Both texts span the
        // panel width with 48px side padding; the label is left-aligned and the value right-aligned, so short values
        // never collide. Returns the value text so Open() can fill it.
        private TextMeshProUGUI StatRow(RectTransform panel, string label, int row, Color labelColor)
        {
            float y = -196f - row * 44f;
            var lab = UIKit.AddText(panel, "L" + row, label, 20, labelColor, TextAlignmentOptions.Left);
            UIKit.Anchor(lab.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1));
            lab.rectTransform.sizeDelta = new Vector2(-96, 40); lab.rectTransform.anchoredPosition = new Vector2(0, y);

            var val = UIKit.AddText(panel, "V" + row, "0", 20, UITheme.Text, TextAlignmentOptions.Right);
            UIKit.Anchor(val.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1));
            val.rectTransform.sizeDelta = new Vector2(-96, 40); val.rectTransform.anchoredPosition = new Vector2(0, y);
            return val;
        }

        /// <summary>Show the settlement for a finished run. <paramref name="onClose"/> fires when the player dismisses it.</summary>
        public void Open(ScoreProcessor score, bool failed, Action onClose)
        {
            _onClose = onClose;
            _title.text = L(failed ? "result.failed" : "result.clear");
            _title.color = failed ? UITheme.Danger : UITheme.Ready;

            // score = the same on-screen formula the HUD showed (ScoreProcessor.Score), 0 if the run never started
            _scoreVal.text = (score != null ? score.Score : 0).ToString("N0");
            _perfect.text = (score != null ? score.PerfectCount : 0).ToString();
            _cool.text = (score != null ? score.CoolCount : 0).ToString();
            _bad.text = (score != null ? score.BadCount : 0).ToString();
            _miss.text = (score != null ? score.MissCount : 0).ToString();
            _maxCombo.text = (score != null ? score.MaxCombo : 0).ToString();

            SetVisible(true);
        }

        private void Close()
        {
            SetVisible(false);
            var cb = _onClose; _onClose = null;
            cb?.Invoke();
        }

        private void SetVisible(bool on)
        {
            _cg.alpha = on ? 1f : 0f;
            _cg.interactable = on;
            _cg.blocksRaycasts = on;
        }
    }
}
