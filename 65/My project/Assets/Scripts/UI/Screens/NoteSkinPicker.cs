using UnityEngine;
using UnityEngine.UI;
using Sdo.Localization;
using Sdo.UI.Catalog;
using Sdo.UI.Core;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>選音符（noteskin）模態：列出可用 skin，選擇後寫入 session。</summary>
    public sealed class NoteSkinPicker : MonoBehaviour
    {
        private CanvasGroup _cg;
        private GameSession _session;
        private RectTransform _listContent;

        private static string L(string k) => LocalizationManager.Get(k);

        public void Build(RectTransform parent, GameSession session)
        {
            _session = session;
            var root = UIKit.NewRect(parent, "NoteSkinPicker");
            UIKit.Stretch(root);
            _cg = root.gameObject.AddComponent<CanvasGroup>();

            var dim = UIKit.AddImage(root, "Dim", new Color(0, 0, 0, 0.6f), true);
            UIKit.Stretch(dim.rectTransform);

            var panel = UIKit.AddImage(root, "Panel", UITheme.Panel).rectTransform;
            UIKit.Anchor(panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            panel.sizeDelta = new Vector2(420, 460);

            var title = UIKit.AddLocText(panel, "Title", "note.title", 22, UITheme.Text, TextAlignmentOptions.Center);
            UIKit.Anchor(title.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1));
            title.rectTransform.sizeDelta = new Vector2(0, 44); title.rectTransform.anchoredPosition = new Vector2(0, -8);

            var scroll = UIKit.AddVerticalScroll(panel, "List", out _listContent, 6f, 8);
            UIKit.Stretch(scroll.GetComponent<RectTransform>(), 16, 70, 16, 56);

            var close = UIKit.AddLocButton(panel, "Close", "common.close", UITheme.Secondary, UITheme.Text, 18);
            var clt = close.GetComponent<RectTransform>();
            UIKit.Anchor(clt, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0));
            clt.sizeDelta = new Vector2(160, 44); clt.anchoredPosition = new Vector2(0, 14);
            close.onClick.AddListener(() => SetVisible(false));

            SetVisible(false);
        }

        public void Open()
        {
            UIKit.Clear(_listContent);
            foreach (var s in NoteSkinCatalog.Available)
            {
                var skin = s;
                bool cur = _session.NoteSkin == skin.Id;
                var btn = UIKit.AddButton(_listContent, skin.Id, out var label, cur ? UITheme.Primary : UITheme.Secondary, UITheme.Text, 17);
                label.text = (cur ? "✓ " : "") + skin.NameZh + "  (" + skin.Id + ")";
                UIKit.Layout(btn.gameObject, 44);
                btn.onClick.AddListener(() =>
                {
                    _session.NoteSkin = skin.Id;
                    Toast.Show(skin.NameZh);
                    Open();   // refresh checkmarks
                });
            }
            SetVisible(true);
        }

        private void SetVisible(bool on)
        {
            _cg.alpha = on ? 1f : 0f;
            _cg.interactable = on;
            _cg.blocksRaycasts = on;
        }
    }
}
