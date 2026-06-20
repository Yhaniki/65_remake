using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// The locally-controlled dancer's nameplate: an animated downward arrow (UI/ARROW, the rainbow
    /// .AN cycle) above the player's name. It PROJECTS a point a fixed WORLD distance above the head
    /// bone through the gameplay camera and draws the arrow + name at that screen point. Anchoring the
    /// offset in WORLD space (not screen px) keeps the marker a consistent distance above the head from
    /// any camera angle/zoom — a fixed pixel gap looks fine head-on but drifts far when the camera pulls
    /// back. The text/arrow keep a constant on-screen SIZE (readable at any distance).
    ///
    /// sortingOrder is set BELOW the note board (which is −30) so the board occludes the marker where
    /// they overlap — yet still above the opaque RenderTexture backdrop, so it stays over the dancer.
    /// Runs at DefaultExecutionOrder(100) — AFTER the skeleton has posed and the head anchor moved.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class HeadMarker : MonoBehaviour
    {
        public System.Func<Vector3> AnchorGetter;   // head-bone world position (in the scene camera's space)
        public System.Func<Camera> CamGetter;        // the gameplay camera used to PROJECT the head to screen

        public float upWorld = 20f;       // screen-up offset, in WORLD-unit-equivalents (scales with depth)
        public float nameFontPx = 22f;    // name size (design px, constant on-screen)
        public float arrowDesignW = 20f;  // arrow width (design px)
        public float arrowGapPx = 3f;     // gap between the name top and the arrow bottom (design px)
        public float frameMs = 200f;      // arrow animation: per-frame hold

        // draw order: behind the note board (−30) but above the opaque scene backdrop.
        private const int NameOrder = -31, ArrowOrder = -33, Zdepth = 10;

        private SpriteRenderer _arrow;
        private Label3D _name;
        private Sprite[] _arrowFrames;
        private float _arrowStart;

        /// <summary>Build the arrow + name children on the HUD layer (default 0). <paramref name="arrowFrames"/>
        /// = the ARROW.AN colour cycle (000..008); null/empty hides the arrow.</summary>
        public void Init(Sprite[] arrowFrames, string playerName)
        {
            _arrowFrames = arrowFrames;
            _arrowStart = Time.time;

            var ag = new GameObject("Arrow");
            ag.transform.SetParent(transform, false);
            _arrow = ag.AddComponent<SpriteRenderer>();   // Sprites/Default = alpha blend
            if (arrowFrames != null && arrowFrames.Length > 0) _arrow.sprite = arrowFrames[0];
            _arrow.sortingOrder = ArrowOrder;

            _name = TextStyles.NewLabel("Name", TextStyles.Style.HeadName, NameOrder, nameFontPx, TextAnchor.MiddleCenter);
            _name.Text = playerName ?? string.Empty;
        }

        public void SetName(string playerName) { if (_name != null) _name.Text = playerName ?? string.Empty; }

        /// <summary>Hide the arrow + name and STOP tracking (the name label is a separate root object that LateUpdate
        /// re-shows every frame, so disabling this component alone wouldn't hide it). Used when the result panel opens.</summary>
        public void Hide()
        {
            if (_arrow != null) _arrow.enabled = false;
            if (_name != null) _name.SetActive(false);
            enabled = false;   // stop LateUpdate from re-enabling them
        }

        private void LateUpdate()
        {
            Camera cam = CamGetter != null ? CamGetter() : null;
            if (cam == null || AnchorGetter == null) return;

            Vector3 headW = AnchorGetter();
            Vector3 vp = cam.WorldToViewportPoint(headW);
            bool visible = vp.z > 0f && vp.x > -0.1f && vp.x < 1.1f;   // in front of the camera and roughly on-screen
            if (_arrow != null) _arrow.enabled = visible && _arrow.sprite != null;
            if (_name != null) _name.SetActive(visible);
            if (!visible) return;

            // px-per-world at the head's depth, measured along the camera's RIGHT axis — which is ALWAYS
            // perpendicular to the view, so it never foreshortens. (Offsetting along world-up instead collapsed
            // to ~0 in a top-down shot, dropping the name onto the head.) We then push the name UP in screen
            // space by upWorld×this, giving a consistent gap above the head at any camera angle/zoom.
            Vector3 vpRight = cam.WorldToViewportPoint(headW + cam.transform.right);
            float pxPerWorld = Mathf.Abs(vpRight.x - vp.x) * SdoLayout.Width;

            float hx = vp.x * SdoLayout.Width;
            float nameY = (1f - vp.y) * SdoLayout.Height - upWorld * pxPerWorld;   // up on screen

            if (_name != null)
            {
                _name.PxSize = nameFontPx;
                _name.Position = SdoLayout.ToWorld(hx, nameY, -Zdepth);
            }

            if (_arrow != null && _arrowFrames != null && _arrowFrames.Length > 0)
            {
                int f = (int)((Time.time - _arrowStart) * 1000f / Mathf.Max(1f, frameMs)) % _arrowFrames.Length;
                _arrow.sprite = _arrowFrames[f];
                var b = _arrow.sprite.bounds.size;
                float s = b.x > 1e-4f ? arrowDesignW / b.x : 1f;   // fit to arrowDesignW, keep aspect
                _arrow.transform.localScale = new Vector3(s, s, 1f);
                float arrowY = nameY - nameFontPx * 0.5f - arrowGapPx - b.y * s * 0.5f;
                _arrow.transform.position = SdoLayout.ToWorld(hx, arrowY, -Zdepth);
            }
        }
    }
}
