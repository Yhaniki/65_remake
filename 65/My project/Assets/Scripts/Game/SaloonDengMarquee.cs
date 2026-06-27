using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Drives the SCN0021 saloon ceiling light bars (DENG1..DENG12) through the original's shared marquee.
    ///
    /// Faithful remake of <c>StageScene_UpdatePatternBillboards_004b0aa0</c> (029_scene_004ad250.c): the 12 bars are
    /// NOT independently animated — they share ONE 198-row × 12-col on/off table (<see cref="SaloonDengPattern"/>),
    /// advanced one row every 100 ms. On each row, bar i shows the lit frame (002.dds) when the table bit is set,
    /// else the dim frame (001.dds). A single shared driver is required: per-bar independent cyclers (the generic
    /// tex-anim path) can't express "bar 3 on / bar 4 off this row", which reads as random flicker. The row index is
    /// derived from the wall clock so the whole bar wall stays phase-locked to one timeline.
    /// </summary>
    public sealed class SaloonDengMarquee : MonoBehaviour
    {
        private Texture _dim, _lit;
        private readonly List<Material>[] _bars = new List<Material>[SaloonDengPattern.Bars];
        private float _interval = SaloonDengPattern.IntervalMs / 1000f;
        private float _startTime;
        private int _last = -1;

        private void Awake() { _startTime = Time.time; }

        /// <summary>The two shared frame textures: dim (001.dds, "off") and lit (002.dds, "on").</summary>
        public void SetFrames(Texture dim, Texture lit) { _dim = dim; _lit = lit; }

        /// <summary>Register a bar's render materials (bar 0 = DENG1 = leftmost). Materials are forced to white _Color
        /// so the swapped texture shows true-colour (DENG2..12 carry the fallback beige with no texture).</summary>
        public void Register(int bar, Material[] mats)
        {
            if (bar < 0 || bar >= _bars.Length || mats == null) return;
            var list = _bars[bar] ?? (_bars[bar] = new List<Material>());
            foreach (var m in mats)
                if (m != null)
                {
                    if (m.HasProperty("_Color")) m.color = Color.white;
                    if (_dim != null) m.mainTexture = _dim;   // seed before the first Update so no bar flashes white/beige
                    list.Add(m);
                }
        }

        private void Update()
        {
            int row = (int)((Time.time - _startTime) / _interval) % SaloonDengPattern.Rows;
            if (row == _last) return;
            _last = row;
            var lit = SaloonDengPattern.Lit[row];
            for (int b = 0; b < _bars.Length; b++)
            {
                var list = _bars[b];
                if (list == null) continue;
                var t = lit[b] ? _lit : _dim;
                for (int i = 0; i < list.Count; i++) if (list[i] != null) list[i].mainTexture = t;
            }
        }
    }
}
