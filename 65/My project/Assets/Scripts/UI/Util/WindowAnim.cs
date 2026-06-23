using System;
using System.Collections;
using UnityEngine;

namespace Sdo.UI.Util
{
    /// <summary>
    /// ROOMDLG-style dialog open/close transition: a quick spin-zoom IN (rotate + scale up, fade in) on open and a
    /// shrink-OUT (rotate + scale down, fade out) on close. Operates on this object's RectTransform and a sibling
    /// CanvasGroup, on UNSCALED time (ignores timeScale). Defaults match the original MUSICSELDLG feel: a fast
    /// spin-in (~0.2s) and a slower shrink-fade-out (~0.5s).
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class WindowAnim : MonoBehaviour
    {
        public float InSpinDeg = 360f;     // one full turn while zooming in
        public float InDuration = 0.2f;    // fast
        public float InStartScale = 0.2f;
        public float OutSpinDeg = -360f;   // spin the other way while shrinking out
        public float OutDuration = 0.5f;
        public float OutEndScale = 0.05f;

        private RectTransform _rt;
        private CanvasGroup _cg;
        private Coroutine _co;

        private void Awake() => Cache();

        private void Cache()
        {
            if (_rt == null) _rt = (RectTransform)transform;
            if (_cg == null) _cg = GetComponent<CanvasGroup>();
        }

        /// <summary>Spin-zoom the window in from <see cref="InStartScale"/> to its resting open state.</summary>
        public void PlayIn()
        {
            Cache();
            if (_co != null) StopCoroutine(_co);
            _co = StartCoroutine(Run(InStartScale, 1f, InSpinDeg, 0f, 0f, 1f, InDuration, EaseOutCubic, null));
        }

        /// <summary>Shrink-fade the window out, then invoke <paramref name="onDone"/> (e.g. the actual screen switch).</summary>
        public void PlayOut(Action onDone)
        {
            Cache();
            if (_co != null) StopCoroutine(_co);
            _co = StartCoroutine(Run(1f, OutEndScale, 0f, OutSpinDeg, 1f, 0f, OutDuration, EaseInCubic, onDone));
        }

        /// <summary>Snap to the fully-open resting state (scale 1, no rotation, opaque); cancels any running anim.</summary>
        public void ResetOpen()
        {
            Cache();
            if (_co != null) { StopCoroutine(_co); _co = null; }
            _rt.localScale = Vector3.one;
            _rt.localRotation = Quaternion.identity;
            _cg.alpha = 1f;
        }

        private IEnumerator Run(float s0, float s1, float r0, float r1, float a0, float a1,
            float dur, Func<float, float> ease, Action onDone)
        {
            Apply(s0, r0, a0);   // pin the start state this frame so the transition never flashes at full size
            float t = 0f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                float k = ease(Mathf.Clamp01(t / dur));
                Apply(Mathf.LerpUnclamped(s0, s1, k), Mathf.LerpUnclamped(r0, r1, k), Mathf.Lerp(a0, a1, k));
                yield return null;
            }
            Apply(s1, r1, a1);
            _co = null;
            onDone?.Invoke();
        }

        private void Apply(float scale, float rotZ, float alpha)
        {
            _rt.localScale = new Vector3(scale, scale, 1f);
            _rt.localRotation = Quaternion.Euler(0f, 0f, rotZ);
            _cg.alpha = alpha;
        }

        private static float EaseOutCubic(float x) => 1f - Mathf.Pow(1f - x, 3f);
        private static float EaseInCubic(float x) => x * x * x;
    }
}
