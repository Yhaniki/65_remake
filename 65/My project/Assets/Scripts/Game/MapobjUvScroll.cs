using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Scrolls a set of shared materials' main-texture UV offset over time — the faithful remake of SDO's stage UV
    /// animation (decompiled scene updates in FUN_004ad250 set texture-coord offset 0x58=U / 0x5c=V on render states
    /// each tick). E.g. the SCN0014 underwater corals scroll V to make their glow stream like a marquee
    /// (StageScene_UpdateMultiPlacement_004b0330: V += _DAT_00589034 = 0.004 every 50 ms -> 0.08 / s, wrapping at 1).
    /// Requires the texture's wrap mode to be Repeat (DdsLoader sets it) so the offset tiles instead of clamping.
    /// </summary>
    public sealed class MapobjUvScroll : MonoBehaviour
    {
        private Material[] _mats;
        private Vector2 _speed;        // UV units per second
        private Vector2 _offset;

        public void Init(Material[] mats, Vector2 speedPerSec)
        {
            _mats = mats;
            _speed = speedPerSec;
        }

        private void Update()
        {
            if (_mats == null) return;
            _offset += _speed * Time.deltaTime;
            _offset.x = Mathf.Repeat(_offset.x, 1f);   // keep small for float precision over long sessions
            _offset.y = Mathf.Repeat(_offset.y, 1f);
            for (int i = 0; i < _mats.Length; i++) if (_mats[i] != null) _mats[i].mainTextureOffset = _offset;
        }
    }
}
