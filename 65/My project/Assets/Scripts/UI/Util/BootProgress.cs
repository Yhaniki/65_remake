using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Full-screen boot loading overlay, shown on the front-end world canvas while the app loads (official catalog
    /// parse + external Songs/ scan + font warmup) — so the window shows a filling bar instead of a long black freeze.
    /// Tech-minimalist look: a pure-black backdrop, a thin white centred progress bar with a pulsing "scan head" at its
    /// leading edge, a letter-spaced LOADING caption + percentage framing the bar, and the current phase / a hint line
    /// at the bottom. Built as the LAST child of the canvas root so it sits above the screen/modal layers, and removed
    /// once boot finishes.
    /// </summary>
    public sealed class BootProgress
    {
        private const float W = 440f, H = 3f, BarY = 6f;   // thin centred track (design px; canvas is centred, y-up)

        private GameObject _go;
        private Image _fill, _cap;
        private TextMeshProUGUI _pct, _label;

        public static BootProgress Create(RectTransform parent)
        {
            var bp = new BootProgress();
            var layer = UIKit.NewRect(parent, "BootProgress");
            UIKit.Stretch(layer);
            layer.SetAsLastSibling();
            bp._go = layer.gameObject;

            // Pure-black backdrop (also blocks clicks reaching the screens beneath). Deliberately NOT the random
            // LOADING_*.PNG tip art — the minimalist black canvas is the whole point.
            var bg = UIKit.AddImage(layer, "bg", Color.black, true);
            UIKit.Stretch(bg.rectTransform);

            // All boot text is set in 俐方體 Cubic 11 (a bundled OFL pixel font) for the tech-minimalist look — sized in
            // multiples of its 11px native grid (11 / 22) so the pixels stay crisp. Falls back to the CJK face if absent.
            var pf = UIFont.Cubic;

            // LOADING caption above the bar — uppercase Latin with wide tracking reads as the "tech" accent.
            var cap = UIKit.AddText(layer, "caption", "LOADING", 22f, new Color(1f, 1f, 1f, 0.5f), TextAlignmentOptions.Center);
            if (pf != null) cap.font = pf;
            cap.characterSpacing = 8f;
            Center(cap.rectTransform, W, 30f, 0f, BarY + 38f);

            // Track: a very dim white hairline. Fill: solid white, grown from the left in Set().
            var track = UIKit.AddImage(layer, "track", new Color(1f, 1f, 1f, 0.13f));
            Center(track.rectTransform, W, H, 0f, BarY);

            var fill = UIKit.AddImage(layer, "fill", Color.white);
            var frt = fill.rectTransform;
            frt.anchorMin = frt.anchorMax = new Vector2(0.5f, 0.5f);
            frt.pivot = new Vector2(0f, 0.5f);                 // grow rightward from the track's left edge
            frt.sizeDelta = new Vector2(0f, H);
            frt.anchoredPosition = new Vector2(-W / 2f, BarY);
            bp._fill = fill;

            // Leading-edge "scan head": a bright tick that rides the fill's right edge and pulses (BootProgressPulse).
            var cell = UIKit.AddImage(layer, "cap", Color.white);
            cell.rectTransform.anchorMin = cell.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            cell.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            cell.rectTransform.sizeDelta = new Vector2(2f, H + 6f);
            bp._cap = cell;
            layer.gameObject.AddComponent<BootProgressPulse>().target = cell;

            // Percentage just below the bar.
            bp._pct = UIKit.AddText(layer, "pct", "0%", 11f, new Color(1f, 1f, 1f, 0.72f), TextAlignmentOptions.Center);
            if (pf != null) bp._pct.font = pf;
            Center(bp._pct.rectTransform, W, 20f, 0f, BarY - 22f);

            // Bottom info: the live phase / song title (載入資料 → 掃描歌曲 → 建立介面 → 準備字型).
            bp._label = UIKit.AddText(layer, "label", "", 11f, new Color(1f, 1f, 1f, 0.85f), TextAlignmentOptions.Center);
            if (pf != null) bp._label.font = pf;
            Center(bp._label.rectTransform, 720f, 22f, 0f, -206f);

            bp.Set(0f, "");
            return bp;
        }

        private static void Center(RectTransform rt, float w, float h, float x, float y)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, y);
        }

        /// <summary>Update the bar (0..1) and the sub-label (current phase / song title).</summary>
        public void Set(float p01, string label)
        {
            p01 = Mathf.Clamp01(p01);
            if (_fill != null) _fill.rectTransform.sizeDelta = new Vector2(W * p01, H);
            if (_cap != null)
            {
                // Sit on the fill's leading edge; hide at the very start/end so a full/empty bar reads clean.
                _cap.rectTransform.anchoredPosition = new Vector2(-W / 2f + W * p01, BarY);
                _cap.enabled = p01 > 0.0001f && p01 < 0.9999f;
            }
            if (_pct != null) _pct.text = Mathf.RoundToInt(p01 * 100f) + "%";
            if (_label != null && label != null) _label.text = label;
        }

        public void Destroy()
        {
            if (_go != null) Object.Destroy(_go);
            _go = null;
        }
    }

    /// <summary>Pulses a graphic's alpha (the boot bar's leading-edge scan head). Unscaled time so it is independent of
    /// any timeScale changes; it simply pauses during a synchronous load freeze and resumes on the next rendered frame.</summary>
    public sealed class BootProgressPulse : MonoBehaviour
    {
        public Graphic target;
        public float min = 0.3f, max = 1f, speed = 3.2f;

        private void Update()
        {
            if (target == null) return;
            float t = 0.5f * (1f + Mathf.Sin(Time.unscaledTime * speed));
            var c = target.color; c.a = Mathf.Lerp(min, max, t); target.color = c;
        }
    }
}
