using UnityEngine;
using UnityEngine.UI;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Cycles an <see cref="Image"/> through a fixed sprite sequence at a constant frame rate. The frame index is
    /// derived from the absolute UNSCALED clock (so it ignores timeScale/pause), which also means several animators
    /// sharing the same <see cref="Frames"/>/<see cref="Fps"/> stay in lock-step — e.g. every NEW badge in the song
    /// list flashes together. Drives <see cref="Image.sprite"/> only; placement/size are the caller's job (the SDO
    /// .an sequences used here keep a constant frame size, so no per-frame resize is needed).
    /// </summary>
    [RequireComponent(typeof(Image))]
    public sealed class SpriteSeqAnim : MonoBehaviour
    {
        public Sprite[] Frames;
        public float Fps = 12f;
        public bool Loop = true;

        private Image _img;
        private float _startTime;

        private void Awake() { _img = GetComponent<Image>(); }

        public void SetFrames(Sprite[] frames, bool restart = false, bool loop = true)
        {
            Frames = frames;
            Loop = loop;
            if (restart) _startTime = Time.unscaledTime;
            if (_img == null) _img = GetComponent<Image>();
            if (_img != null && Frames != null && Frames.Length > 0 && Frames[0] != null)
                _img.sprite = Frames[0];
        }

        private void Update()
        {
            if (_img == null || Frames == null || Frames.Length == 0 || Fps <= 0f) return;
            int i = (int)((Time.unscaledTime - _startTime) * Fps);
            if (Loop)
            {
                i %= Frames.Length;
                if (i < 0) i += Frames.Length;
            }
            else
            {
                i = Mathf.Clamp(i, 0, Frames.Length - 1);
            }
            var s = Frames[i];
            if (s != null && _img.sprite != s) _img.sprite = s;
        }
    }
}
