using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Drives the ScnRoom waiting lights (GUANG1..GUANG8) through the original's shared marquee.
    ///
    /// Faithful remake of <c>StageScene_UpdatePatternEmitters_004b1eb0</c> (029_scene_004ad250.c): the eight lights are
    /// NOT independently animated — they share ONE 24-row × 8-col on/off table (<see cref="RoomDengPattern"/>), advanced
    /// one row every 150 ms. On each row, light i shows the lit frame (ROOMOBJ_DENGDAI2_) when the table bit is set,
    /// else the dim frame (ROOMOBJ_DENGDAI1_). A single shared driver is required: per-light independent cyclers (the
    /// generic tex-anim path) can't express "GUANG3 on / GUANG4 off this row", which reads as random flicker. The row
    /// index is derived from the wall clock so all eight lights stay phase-locked to one timeline. Room-scoped twin of
    /// <see cref="SaloonDengMarquee"/>, kept separate so that validated path is never disturbed.
    /// </summary>
    public sealed class RoomDengMarquee : MonoBehaviour
    {
        private Texture _dim, _lit;
        private readonly List<Material>[] _lights = new List<Material>[RoomDengPattern.Lights];
        private readonly float _interval = RoomDengPattern.IntervalMs / 1000f;
        private float _startTime;
        private int _last = -1;

        private void Awake() { _startTime = Time.time; }

        /// <summary>The two shared frame textures: dim (ROOMOBJ_DENGDAI1_, "off") and lit (ROOMOBJ_DENGDAI2_, "on").</summary>
        public void SetFrames(Texture dim, Texture lit) { _dim = dim; _lit = lit; }

        public bool HasFrames => _dim != null || _lit != null;

        /// <summary>Register a light's render materials (light 0 = GUANG1). Materials are forced to white _Color so the
        /// swapped texture shows true-colour, and seeded with the dim frame before the first Update so no light flashes
        /// its baked (possibly white/beige) material for one frame.</summary>
        public void Register(int light, Material[] mats)
        {
            if (light < 0 || light >= _lights.Length || mats == null) return;
            var list = _lights[light] ?? (_lights[light] = new List<Material>());
            foreach (var m in mats)
                if (m != null)
                {
                    if (m.HasProperty("_Color")) m.color = Color.white;
                    if (_dim != null) m.mainTexture = _dim;
                    list.Add(m);
                }
        }

        private void Update()
        {
            int row = (int)((Time.time - _startTime) / _interval) % RoomDengPattern.Rows;
            if (row == _last) return;
            _last = row;
            var lit = RoomDengPattern.Lit[row];
            for (int g = 0; g < _lights.Length; g++)
            {
                var list = _lights[g];
                if (list == null) continue;
                var t = lit[g] ? _lit : _dim;
                for (int i = 0; i < list.Count; i++) if (list[i] != null) list[i].mainTexture = t;
            }
        }
    }
}
