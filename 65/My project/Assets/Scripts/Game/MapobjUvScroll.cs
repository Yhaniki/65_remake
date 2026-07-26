using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Drives a set of shared materials' main-texture UV offset over time — the faithful remake of SDO's stage UV
    /// animation (the decompiled scene updates in FUN_004ad250 set texture-coord offset 0x58=U / 0x5c=V on the
    /// render state each tick, after OR-ing 0x10000 into +0x48 to enable the texture transform).
    /// Requires the texture's wrap mode to be Repeat (DdsLoader sets it) so the offset tiles instead of clamping.
    ///
    /// Three motions, all taken verbatim from the original (see <see cref="SceneMapobjUvScrollCatalog.Motion"/>):
    ///   Linear    — offset += speed·dt, wrapping at 1. The common case (SCN0014 corals, SCN0025/0028 fountains…).
    ///   Sine      — offset = amplitude·sin(phase), phase += rate·dt, phase wrapping at 2π. SCN0004's sea and
    ///               shore wave ROCK BACK AND FORTH rather than stream, which a linear scroll cannot express.
    ///   DwellStep — hold still for dwellMs, then sweep one `step` at `speed`, repeat. SCN0012/0013's ad boards
    ///               (hold 2 s → wipe half the texture → next advert) and SCN0029's 4-panel screen (hold 5 s →
    ///               slide a quarter). The sweep is fast relative to the dwell, so it reads as a cut with a wipe.
    /// </summary>
    public sealed class MapobjUvScroll : MonoBehaviour
    {
        private Material[] _mats;
        private SceneMapobjUvScrollCatalog.Motion _motion;
        private Vector2 _speed;        // Linear: UV/s · Sine: rad/s · DwellStep: UV/s while sweeping (signed)
        private Vector2 _amplitude;    // Sine only: peak UV offset per axis (signed)
        private Vector2 _step;         // DwellStep only: signed UV covered by one leg
        private float _dwellSec;       // DwellStep only
        private Vector2 _offset;
        private Vector2 _phase;        // Sine only
        private Vector2 _legLeft;      // DwellStep only: signed UV still to cover in this leg
        private float _dwellLeft;      // DwellStep only: seconds still to hold

        /// <summary>Linear scroll (kept as the simple entry point most props use).</summary>
        public void Init(Material[] mats, Vector2 speedPerSec)
        {
            _mats = mats;
            _motion = SceneMapobjUvScrollCatalog.Motion.Linear;
            _speed = speedPerSec;
            Apply();
        }

        /// <summary>Drive from a catalog entry (any motion).</summary>
        public void Init(Material[] mats, SceneMapobjUvScrollCatalog.Target t)
        {
            _mats = mats;
            _motion = t.Motion;
            _speed = t.Speed;
            _amplitude = t.Amplitude;
            _step = t.Step;
            _dwellSec = t.DwellMs * 0.001f;
            _offset = t.Start;
            _legLeft = Vector2.zero;
            _dwellLeft = _dwellSec;      // the original holds FIRST, then sweeps
            Apply();
        }

        private void Update() { Step(Time.deltaTime); }

        /// <summary>One tick. Split out from Update so EditMode tests can drive it deterministically.</summary>
        public void Step(float dt)
        {
            if (_mats == null) return;
            switch (_motion)
            {
                case SceneMapobjUvScrollCatalog.Motion.Sine:
                    // phase wraps at 2π exactly like the original (`if (6.2831855 < p) p = 0`). The offset itself is
                    // signed and must NOT be folded into [0,1): that would teleport the texture at every zero crossing.
                    _phase += _speed * dt;
                    _phase.x = Mathf.Repeat(_phase.x, 2f * Mathf.PI);
                    _phase.y = Mathf.Repeat(_phase.y, 2f * Mathf.PI);
                    _offset = new Vector2(_amplitude.x * Mathf.Sin(_phase.x), _amplitude.y * Mathf.Sin(_phase.y));
                    break;

                case SceneMapobjUvScrollCatalog.Motion.DwellStep:
                    StepDwell(dt);
                    break;

                default:
                    _offset += _speed * dt;
                    break;
            }
            // Fold the ACCUMULATING motions into [0,1) — Repeat wrap makes that visually identical and keeps float
            // precision over a long song. Sine offsets are signed and bounded; folding them would teleport the
            // texture at every zero crossing, so they are left alone. Leg bookkeeping is kept as a REMAINING
            // distance, never an absolute position, so this fold can't desync the dwell state machine.
            if (_motion != SceneMapobjUvScrollCatalog.Motion.Sine)
            {
                _offset.x = Mathf.Repeat(_offset.x, 1f);
                _offset.y = Mathf.Repeat(_offset.y, 1f);
            }
            Apply();
        }

        // Hold → sweep one step → hold → … Leg progress is tracked as REMAINING signed distance (not an absolute
        // target) so folding the applied offset into [0,1) can never desync the bookkeeping.
        private void StepDwell(float dt)
        {
            while (dt > 0f)
            {
                if (_dwellLeft > 0f)
                {
                    float used = Mathf.Min(dt, _dwellLeft);
                    _dwellLeft -= used;
                    dt -= used;
                    if (_dwellLeft > 0f) return;
                    _legLeft = _step;           // dwell finished → start the next leg
                    continue;
                }
                // sweeping: advance, but never past the end of the leg (the leftover time rolls into the dwell)
                float legTime = LegTimeLeft();
                float move = Mathf.Min(dt, legTime);
                _offset += _speed * move;
                _legLeft -= _speed * move;
                dt -= move;
                if (move >= legTime) { _legLeft = Vector2.zero; _dwellLeft = _dwellSec; }
                if (move <= 0f) return;         // speed is zero on the stepping axis — nothing to do
            }
        }

        // Seconds left in the current leg = remaining distance / speed, taken on whichever axis actually steps.
        private float LegTimeLeft()
        {
            float t = float.MaxValue;
            if (!Mathf.Approximately(_step.x, 0f) && !Mathf.Approximately(_speed.x, 0f))
                t = Mathf.Min(t, _legLeft.x / _speed.x);
            if (!Mathf.Approximately(_step.y, 0f) && !Mathf.Approximately(_speed.y, 0f))
                t = Mathf.Min(t, _legLeft.y / _speed.y);
            return t == float.MaxValue ? 0f : Mathf.Max(0f, t);
        }

        private void Apply()
        {
            if (_mats == null) return;
            for (int i = 0; i < _mats.Length; i++) if (_mats[i] != null) _mats[i].mainTextureOffset = _offset;
        }

        /// <summary>Current applied UV offset — for tests / diagnostics.</summary>
        public Vector2 Offset => _offset;
    }
}
