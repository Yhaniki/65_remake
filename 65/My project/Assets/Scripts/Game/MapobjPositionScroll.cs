using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Slides a set of prop transforms along one axis and wraps them — the SDO stage props the original moves by
    /// rewriting their world position every tick (rather than by a .mot or a UV scroll).
    ///
    /// Only SCN0010 花車 uses it: two copies of the street-front HOUSE ride past the float on a loop, so the parade
    /// looks like it is travelling. Verbatim from StageScene_UpdateScrollPair_004b40e0 (disassembled at 0x4b40e0
    /// because Ghidra dropped the `this` on the object writes):
    ///   every 30 ms:  x += −1.0 ;  if (x &lt;= −2168) x = +2168        ← the FPU compare is `A &lt;= wrapAt`, not `&lt;`
    ///   objects[0].pos = (xA, 0, 0)   objects[1].pos = (xB, 0, 0)     ← y and z are rewritten to 0 every tick
    /// The two accumulators start 2168 apart (0 and +2168), which is exactly half the 4336-unit span, so one house
    /// is always entering as the other leaves — no gap. −1.0 / 30 ms = −33.333 units/s; a full lap is 130.08 s.
    ///
    /// Discrete 30 ms steps are kept (not a continuous lerp) so the motion matches the original tick-for-tick.
    /// </summary>
    public sealed class MapobjPositionScroll : MonoBehaviour
    {
        private Transform[] _targets;
        private float[] _start;        // per-target initial coordinate on the moving axis
        private Vector3 _axis;         // unit axis the props travel along (SCN0010: −X, so (1,0,0) with a negative step)
        private float _step;           // signed units per tick
        private float _tickSec;
        private float _wrapAt;         // when the coordinate reaches this (inclusive) it jumps to WrapTo
        private float _wrapTo;
        private float _elapsed;
        private int _tick = -1;

        public void Init(Transform[] targets, float[] startCoord, Vector3 axis, float step, float tickMs,
                         float wrapAt, float wrapTo)
        {
            _targets = targets; _start = startCoord; _axis = axis.normalized;
            _step = step; _tickSec = Mathf.Max(0.001f, tickMs * 0.001f);
            _wrapAt = wrapAt; _wrapTo = wrapTo;
            Apply(0);
        }

        private void Update()
        {
            _elapsed += Time.deltaTime;
            int t = (int)(_elapsed / _tickSec);
            if (t != _tick) Apply(t);
        }

        /// <summary>Place every target as of tick <paramref name="tick"/>. Pure function of the tick index, so it
        /// cannot drift and tests can jump straight to any tick.</summary>
        public void Apply(int tick)
        {
            _tick = tick;
            if (_targets == null) return;
            for (int i = 0; i < _targets.Length; i++)
            {
                if (_targets[i] == null) continue;
                _targets[i].position = _axis * CoordAt(i, tick);
            }
        }

        /// <summary>The moving coordinate of target <paramref name="i"/> at a tick. The original decrements once per
        /// tick and snaps to <c>wrapTo</c> the moment it passes <c>wrapAt</c>, so the value lives in the half-open
        /// span (wrapAt, wrapTo]; fold with Repeat over that span instead of looping, so any tick is O(1).</summary>
        public float CoordAt(int i, int tick)
        {
            float span = Mathf.Abs(_wrapTo - _wrapAt);
            if (span < 1e-4f) return _start[i];
            float raw = _start[i] + _step * tick;
            // moving negative: fold into (wrapAt, wrapTo]. Repeat() returns [0,span), and 0 must map to wrapTo
            // (the snap is inclusive at wrapAt), hence the `span -` and the wrapAt offset.
            float off = Mathf.Repeat(_wrapTo - raw, span);
            return Mathf.Approximately(off, 0f) ? _wrapTo : _wrapTo - off;
        }
    }
}
