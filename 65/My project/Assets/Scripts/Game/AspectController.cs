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
        /// stays correct on window resize; in Stretch the rect is the whole screen and all four bars collapse to zero.
        ///
        /// 每條邊往**內容側多蓋 <see cref="BarOverlapPx"/> 個實體像素**:相機視口(pixelRect,整數像素)與這張
        /// Overlay canvas 的邊界即使 <see cref="Fit43Rect"/> 已對齊像素格,驅動/管線的 rounding 仍可能在交界留下
        /// 一列「被相機畫到、卻沒被黑條蓋住」的次像素邊緣 —— 背景一動它就跟著變色(黑邊旁那條會閃色的雜訊線)。
        /// 多蓋 1px 一勞永逸;犧牲的是 800×600 邏輯畫面最外圈不到一個設計像素,肉眼看不出來。
        /// 只在該側真的有黑邊時才長(Stretch 模式四條都是零寬,長出去會變成畫面邊緣的黑線)。</summary>
        private void UpdateBars(Rect r)
        {
            SetBar(_barL, 0f, 0f, r.xMin, 1f, Vector2.zero, r.xMin > 0f ? new Vector2(BarOverlapPx, 0f) : Vector2.zero);
            SetBar(_barR, r.xMax, 0f, 1f, 1f, r.xMax < 1f ? new Vector2(-BarOverlapPx, 0f) : Vector2.zero, Vector2.zero);
            SetBar(_barB, 0f, 0f, 1f, r.yMin, Vector2.zero, r.yMin > 0f ? new Vector2(0f, BarOverlapPx) : Vector2.zero);
            SetBar(_barT, 0f, r.yMax, 1f, 1f, r.yMax < 1f ? new Vector2(0f, -BarOverlapPx) : Vector2.zero, Vector2.zero);
        }

        /// <summary>黑邊往內容側的重疊量(實體像素 —— 這張 canvas 沒有 CanvasScaler,scaleFactor 固定 1)。</summary>
        private const float BarOverlapPx = 1f;

        private static void SetBar(RectTransform rt, float xMin, float yMin, float xMax, float yMax,
                                   Vector2 offMin, Vector2 offMax)
        {
            if (rt == null) return;
            rt.anchorMin = new Vector2(xMin, yMin);
            rt.anchorMax = new Vector2(xMax, yMax);
            rt.offsetMin = offMin;
            rt.offsetMax = offMax;
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

        /// <summary>Centred 4:3 sub-rect of the current screen (pillarbox on wide screens, letterbox on tall).
        ///
        /// 先算**整數像素**的內容框再換回 normalized:直接用比值算出來的 normalized rect 幾乎一定落在像素中間,
        /// 相機的 pixelRect 會自己 round(邊界移動最多半像素),而黑邊條那張 Overlay canvas 是照原始 normalized
        /// 值鋪頂點的 —— 兩者對不齊,交界就露出一列會跟著背景變色的雜訊。邊界落在像素格線上,兩邊才會一致。</summary>
        private static Rect Fit43Rect()
        {
            int sw = Mathf.Max(1, Screen.width), sh = Mathf.Max(1, Screen.height);
            if ((float)sw / sh > TargetAspect)            // window wider than 4:3 → bars left/right
            {
                int w = Mathf.Clamp(Mathf.RoundToInt(sh * TargetAspect), 1, sw);
                int x = (sw - w) / 2;                     // 置中(奇數餘數落在右邊,無所謂 —— 兩側邊界仍在格線上)
                return new Rect((float)x / sw, 0f, (float)w / sw, 1f);
            }
            int h = Mathf.Clamp(Mathf.RoundToInt(sw / TargetAspect), 1, sh);   // taller than 4:3 → bars top/bottom
            int y = (sh - h) / 2;
            return new Rect(0f, (float)y / sh, 1f, (float)h / sh);
        }
    }
}
