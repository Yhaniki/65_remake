using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Sdo.Game
{
    /// <summary>How a fixed 4:3 (800×600) frame is fit to a non-4:3 window/screen.</summary>
    public enum AspectMode
    {
        /// <summary>Non-uniformly stretch the 4:3 frame to fill the whole screen (no bars; content distorts on 16:9).</summary>
        Stretch,
        /// <summary>Keep the 4:3 shape centred with black bars (pillarbox on wide, letterbox on tall).</summary>
        Pillarbox,
    }

    /// <summary>
    /// Forces the whole game (gameplay play-screen camera + the front-end UI camera) into one consistent 4:3
    /// presentation, matching the original SDO 800×600 frame. Cameras register themselves; this drives their
    /// <see cref="Camera.aspect"/> / <see cref="Camera.rect"/> so the SAME 4:3 logical frame is shown everywhere.
    ///
    /// Two modes, switchable in code via <see cref="Mode"/> (default <see cref="AspectMode.Stretch"/> — there is
    /// no settings UI yet): Stretch fills the screen (the requested "把 4:3 拉到全螢幕"); Pillarbox keeps the 4:3
    /// shape with black bars. A backdrop camera clears the bars black in Pillarbox mode.
    /// </summary>
    [DefaultExecutionOrder(20000)]   // run AFTER gameplay/UI cameras are positioned each frame
    public sealed class AspectController : MonoBehaviour
    {
        public const float TargetAspect = 800f / 600f;   // 4:3

        /// <summary>Active fit mode. Default = Stretch (fill). Flip to Pillarbox in code; no in-game setting yet.</summary>
        public static AspectMode Mode = AspectMode.Stretch;

        private static AspectController _inst;
        private static readonly List<Camera> _cams = new List<Camera>();
        private Camera _backdrop;                         // clears the letterbox bars black (Pillarbox only)
        private RectTransform _barL, _barR, _barT, _barB; // opaque black bars painted OVER everything (see BuildBars)
        private int _lastW, _lastH;
        private AspectMode _lastMode = (AspectMode)(-1);

        /// <summary>Register a screen-output camera (the play-screen main cam, or the front-end UI cam) to be 4:3-fit.</summary>
        public static void Register(Camera cam)
        {
            if (cam == null) return;
            EnsureInstance();
            if (!_cams.Contains(cam)) _cams.Add(cam);
            Apply(true);
        }

        /// <summary>Switch fit mode at runtime (Stretch ⇄ Pillarbox). Kept for a future settings toggle.</summary>
        public static void SetMode(AspectMode mode) { Mode = mode; Apply(true); }

        /// <summary>Normalized viewport rect (Unity coords, bottom-left origin) of the actual 4:3 game frame on
        /// screen: the whole screen in Stretch, the centred pillar/letterbox sub-rect in Pillarbox. Used to crop
        /// the black bars out of a screenshot so only the game frame is saved.</summary>
        public static Rect ContentRect => Mode == AspectMode.Pillarbox ? Fit43Rect() : new Rect(0f, 0f, 1f, 1f);

        private static void EnsureInstance()
        {
            if (_inst != null) return;
            var go = new GameObject("AspectController");
            DontDestroyOnLoad(go);
            _inst = go.AddComponent<AspectController>();
            _inst.BuildBackdrop();
            _inst.BuildBars();
        }

        private void BuildBackdrop()
        {
            var bgGo = new GameObject("AspectBackdrop");
            bgGo.transform.SetParent(transform, false);
            _backdrop = bgGo.AddComponent<Camera>();
            _backdrop.clearFlags = CameraClearFlags.SolidColor;
            _backdrop.backgroundColor = Color.black;
            _backdrop.cullingMask = 0;                    // draws nothing — just clears the full screen black
            _backdrop.depth = -1000f;                     // behind every registered camera
            _backdrop.rect = new Rect(0f, 0f, 1f, 1f);
            _backdrop.orthographic = true;
            _backdrop.enabled = Mode == AspectMode.Pillarbox;
        }

        /// <summary>
        /// Opaque black bars painted OVER every camera (Screen-Space-Overlay canvas) to frame the 4:3 content rect.
        /// The <see cref="_backdrop"/> camera only clears black BEHIND everything, so any camera that renders
        /// full-screen but isn't registered here (e.g. the scene's default "Main Camera", which the front-end never
        /// uses — only gameplay reuses it — so it just draws the empty scene's skybox) bleeds into the pillarbox bars.
        /// In Stretch mode nobody notices because the full-screen UI cam covers it; in Pillarbox the UI cam only fills
        /// the centred sub-rect, exposing that stray camera in the left/right bars. These top-most bars cover it for
        /// good, regardless of render pipeline or which cameras happen to be alive.
        /// </summary>
        private void BuildBars()
        {
            var go = new GameObject("AspectBars");
            go.transform.SetParent(transform, false);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;         // above every in-game (world-space) canvas
            _barL = MakeBar(go.transform, "BarL");
            _barR = MakeBar(go.transform, "BarR");
            _barT = MakeBar(go.transform, "BarT");
            _barB = MakeBar(go.transform, "BarB");
            UpdateBars(ContentRect);
        }

        private static RectTransform MakeBar(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = Color.black;
            img.raycastTarget = false;                    // never eat clicks — bars only cover the dead margin anyway
            return rt;
        }

        /// <summary>Frame the (normalized) content rect with the four black bars. Anchors are screen fractions, so this
        /// stays correct on window resize; in Stretch the rect is the whole screen and all four bars collapse to zero.</summary>
        private void UpdateBars(Rect r)
        {
            SetBar(_barL, 0f, 0f, r.xMin, 1f);            // left of content
            SetBar(_barR, r.xMax, 0f, 1f, 1f);           // right of content
            SetBar(_barB, 0f, 0f, 1f, r.yMin);           // below content
            SetBar(_barT, 0f, r.yMax, 1f, 1f);           // above content
        }

        private static void SetBar(RectTransform rt, float xMin, float yMin, float xMax, float yMax)
        {
            if (rt == null) return;
            rt.anchorMin = new Vector2(xMin, yMin);
            rt.anchorMax = new Vector2(xMax, yMax);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private void LateUpdate()
        {
            // Re-fit only when something that affects the result changed (cheap; handles window resize / fullscreen toggle).
            if (Screen.width != _lastW || Screen.height != _lastH || Mode != _lastMode) Apply(false);
        }

        private static void Apply(bool force)
        {
            if (_inst == null) return;
            _inst._lastW = Screen.width; _inst._lastH = Screen.height; _inst._lastMode = Mode;
            _cams.RemoveAll(c => c == null);

            bool pillar = Mode == AspectMode.Pillarbox;
            Rect r = pillar ? Fit43Rect() : new Rect(0f, 0f, 1f, 1f);
            foreach (var c in _cams)
            {
                c.rect = r;
                if (pillar) c.ResetAspect();              // viewport rect is exactly 4:3 → correct, undistorted
                else c.aspect = TargetAspect;             // full-screen viewport + forced 4:3 projection → stretched fill
            }
            if (_inst._backdrop != null) _inst._backdrop.enabled = pillar;
            _inst.UpdateBars(r);                          // top-most black bars frame the content rect (collapse in Stretch)
        }

        /// <summary>Centred 4:3 sub-rect of the current screen (pillarbox on wide screens, letterbox on tall).</summary>
        private static Rect Fit43Rect()
        {
            float wa = (float)Screen.width / Mathf.Max(1, Screen.height);
            if (wa > TargetAspect)                        // window wider than 4:3 → bars left/right
            {
                float w = TargetAspect / wa;
                return new Rect((1f - w) * 0.5f, 0f, w, 1f);
            }
            float h = wa / TargetAspect;                  // window taller than 4:3 → bars top/bottom
            return new Rect(0f, (1f - h) * 0.5f, 1f, h);
        }
    }
}
