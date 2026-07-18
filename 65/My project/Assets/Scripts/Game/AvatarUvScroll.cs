using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Drives the 炫 (dazzle) hair effect — the FAITHFUL remake of the standalone/online client's MODEL-id band
    /// [40000,49999] "animated" items (see <see cref="SpecialMotionItems.IsUvScrollHair"/> for the decompile + Frida
    /// trail). Ground truth captured on the live client (hook_xuan_hair.js): the hair mesh's ONLY texture-stage effect
    /// is the texture-coordinate transform (flag 0x10000) with U=0 and V scrolling at a constant ~2.0 units/sec. The
    /// 炫 texture is a vertical colour gradient with a bright highlight band, so scrolling V sweeps that sheen through
    /// the hair — that IS the "不斷變色". No colour is fabricated: we scroll the real texture, exactly like the client.
    ///
    /// The offset math lives in <see cref="SpecialMotionItems.ScrollOffsetV"/> (pure, unit-tested); this component only
    /// samples the wall clock, applies the V offset to the material(s), and forces REPEAT wrap so the scroll tiles.
    /// Attached by <see cref="SdoAvatarBuilder"/> only to hairs in the band, so ordinary hair is untouched.
    /// </summary>
    public sealed class AvatarUvScroll : MonoBehaviour
    {
        private Material[] _mats;
        private float _rate;
        private float _startTime;

        /// <param name="mats">the hair materials to scroll (their shader must use TRANSFORM_TEX, e.g. UnlitDoubleSided)</param>
        /// <param name="unitsPerSec">V texture-units per second (measured 2.0 on the live client)</param>
        public void Init(Material[] mats, float unitsPerSec)
        {
            _mats = mats;
            _rate = unitsPerSec;
            _startTime = Time.time;
            if (_mats != null)
            {
                foreach (var m in _mats)
                    if (m != null && m.mainTexture != null) m.mainTexture.wrapMode = TextureWrapMode.Repeat;   // scroll must tile
            }
            Apply();   // start on offset 0 so the first drawn frame already carries the scroll
        }

        private void Update() => Apply();

        private void Apply()
        {
            if (_mats == null) return;
            float v = SpecialMotionItems.ScrollOffsetV(Time.time - _startTime, _rate);
            for (int i = 0; i < _mats.Length; i++)
            {
                var m = _mats[i];
                if (m == null) continue;
                var o = m.mainTextureOffset;
                o.y = v;                       // U stays as-authored (client keeps U=0); only V scrolls
                m.mainTextureOffset = o;
            }
        }
    }
}
