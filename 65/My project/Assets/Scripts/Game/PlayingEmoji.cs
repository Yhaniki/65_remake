using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// The dancer's emoji cut-in: a camera-facing (billboard) animated sprite that pops up beside the dancer when a
    /// milestone / failure / low-HP condition fires (see ScreenGameplay's ShowEmoji hooks). Faithful to the original SDO
    /// PLAYINGEXP cut-ins: each frame holds <see cref="frameMs"/> and the short PNG sequence loops a fixed number of
    /// times (per-emoji, passed to <see cref="Play"/>) — e.g. HH (7 frames) ×3 loops.
    ///
    /// POSITION references the dancer's formation SLOT in world space (<see cref="SlotGetter"/> returns the slot's
    /// world coordinate — the dance-spot the dancer's feet stand on) plus a fixed world-axis offset (x,y,z). It does
    /// NOT chase the bobbing skeleton; it tracks the slot, so it stays put while the dancer dances in place — and when a
    /// formation moves the dancer to a different slot, the emoji SMOOTHLY eases over (frame-rate-independent lerp).
    /// ORIENTATION always faces the active camera (Quaternion.LookRotation), so it reads from any camera angle.
    ///
    /// Runs at DefaultExecutionOrder(100) — AFTER SdoAvatar.LateUpdate has posed the skeleton / moved the slot anchor.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class PlayingEmoji : MonoBehaviour
    {
        public SpriteRenderer sr;                    // the quad we draw the emoji on
        public System.Func<Vector3> SlotGetter;      // current formation-slot world position (dance-spot) to anchor to
        public System.Func<Camera> CamGetter;        // the camera to face (gameplay 3D scene cam)

        // tunables (public, F-panel-friendly). Offset is in WORLD axes relative to the slot world coordinate.
        public float xOff = 14.4f;      // world X
        public float yOff = 59.3f;      // world Y (up from the slot floor — ~head height)
        public float zOff = -20.8f;     // world Z (− = toward the default camera side)
        public float worldScale = 0.25f;
        public float frameMs = 200f;    // per-frame hold
        public float followLerp = 9f;   // slot-follow smoothing rate (higher = snappier); used when the slot moves

        private Sprite[] _frames;
        private int _loops = 1;          // how many times to play the sequence before stopping
        private float _start = -1f;
        private Vector3 _displayPos;     // smoothed position (eases toward slot+offset)
        private bool _haveDisplay;       // false until the first frame snaps to the target (no ease-in from origin)

        /// <summary>Begin a cut-in: play <paramref name="frames"/> for <paramref name="loops"/> full loops, then stop.
        /// Latest call wins (single emoji slot). Position snaps to the current slot on the first frame.</summary>
        public void Play(Sprite[] frames, int loops)
        {
            if (frames == null || frames.Length == 0 || sr == null) return;
            _frames = frames;
            _loops = Mathf.Max(1, loops);
            _start = Time.time;
            _haveDisplay = false;        // snap to the slot at THIS trigger (no slide-in from the previous spot)
            sr.sprite = frames[0];
            sr.enabled = true;
        }

        public void Stop() { _start = -1f; if (sr != null) sr.enabled = false; }

        private void LateUpdate()
        {
            if (_start < 0f || _frames == null || sr == null) return;

            // advance frames; stop after _loops full passes of the sequence.
            float t = Time.time - _start;
            int gframe = (int)(t * 1000f / Mathf.Max(1f, frameMs));
            if (gframe < 0) gframe = 0;
            if (gframe >= _frames.Length * _loops) { Stop(); return; }
            sr.sprite = _frames[gframe % _frames.Length];

            // target = current slot world coordinate + world-axis offset; smoothly follow it (snap on the first frame).
            Vector3 slot = SlotGetter != null ? SlotGetter() : Vector3.zero;
            Vector3 target = slot + new Vector3(xOff, yOff, zOff);
            if (!_haveDisplay) { _displayPos = target; _haveDisplay = true; }
            else _displayPos = Vector3.Lerp(_displayPos, target, 1f - Mathf.Exp(-followLerp * Time.deltaTime));
            transform.position = _displayPos;
            transform.localScale = Vector3.one * worldScale;

            Camera cam = CamGetter != null ? CamGetter() : Camera.main;
            if (cam != null)
                transform.rotation = Quaternion.LookRotation(_displayPos - cam.transform.position, cam.transform.up);
        }
    }
}
