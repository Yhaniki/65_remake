using UnityEngine;
using Sdo.UI.Core;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>
    /// Base for procedurally-built front-end screens. Each screen lives on its own full-stretch root
    /// (under the screen layer) and is shown/hidden via a CanvasGroup driven by the FlowManager.
    /// </summary>
    public abstract class UIScreenBase : MonoBehaviour
    {
        protected AppContext Ctx;
        protected RectTransform Root;
        private CanvasGroup _cg;
        private bool _visible;

        public abstract ScreenId Id { get; }

        public void Build(AppContext ctx)
        {
            Ctx = ctx;
            Root = (RectTransform)transform;
            UIKit.Stretch(Root);
            _cg = gameObject.AddComponent<CanvasGroup>();
            BuildUI();
            SetVisible(false);
        }

        protected abstract void BuildUI();

        public virtual void OnShow() { }
        public virtual void OnHide() { }

        public void SetVisible(bool on)
        {
            if (_cg == null) return;
            _cg.alpha = on ? 1f : 0f;
            _cg.interactable = on;
            _cg.blocksRaycasts = on;
            // Fire OnShow/OnHide only on an actual state change: 選歌 overlays the room (both visible at once), so the
            // room gets SetVisible(true) again while already shown — it must NOT re-run OnShow (rebuild) each time.
            if (on == _visible) return;
            _visible = on;
            if (on) OnShow(); else OnHide();
        }

        protected void GoTo(ScreenId target) => Ctx.Flow.GoTo(target);
    }
}
