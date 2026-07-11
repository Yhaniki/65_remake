using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Cycles a set of shared materials' main texture through an ordered frame sequence — the faithful remake of the
    /// original FIFA crowd / spotlight effect. In sdo_stand_alone, Scene_LoadBackground (FUN_004b43c0 cases 0xc/0xd)
    /// loads N texture frames per prop (crowd renqun = 9, spotlight shanguang = 4) into a side array, and the per-frame
    /// draw (FUN_004ad250) advances the frame index on a 300 ms timer: DAT_00678510=(idx+1)%9 for the crowd and
    /// DAT_0067850c=(idx+1)&amp;3 for the lights. The mesh geometry is static (no .mot); ONLY the bound texture changes,
    /// so the crowd appears to wave and the lights to flash. The frame index is derived from the wall clock so several
    /// independent animators (crowd + lights) stay aligned to the same 300 ms boundaries without a shared counter.
    /// </summary>
    public sealed class MapobjTexAnimator : MonoBehaviour
    {
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private Material[] _mats;
        private Texture[] _frames;
        private float _interval;     // seconds per frame
        private bool _holdLast;      // true -> stop at the last frame (play-once, for SCN0016 building lights)
        private float _startTime;    // Time.time when Init was called; makes intervals relative to scene load
        private int _last = -1;
        private bool _warnedNoTex;   // one-shot: named a texture-less material so the log isn't spammed

        /// <param name="mats">shared submesh materials whose _MainTex to drive (all set to the same frame)</param>
        /// <param name="frames">ordered frame textures (already loaded); cycled round-robin</param>
        /// <param name="intervalMs">ms between frames (300 in the original)</param>
        /// <param name="holdLast">when true, advance once then hold the last frame instead of looping</param>
        public void Init(Material[] mats, Texture[] frames, float intervalMs, bool holdLast = false)
        {
            _mats = mats;
            _frames = frames;
            _interval = Mathf.Max(0.001f, intervalMs / 1000f);
            _holdLast = holdLast;
            _startTime = Time.time;
            Debug.Log($"[texanim] {name}: Init {frames?.Length ?? 0} frames × {_mats?.Length ?? 0} mats @ {intervalMs}ms, holdLast={holdLast}");
            Apply(0);   // start on frame 0 so the MSH's embedded (possibly wrong/white) material never shows
        }

        private void Update()
        {
            if (_frames == null || _frames.Length == 0) return;
            int raw = (int)((Time.time - _startTime) / _interval);
            int idx = _holdLast ? Mathf.Min(raw, _frames.Length - 1) : raw % _frames.Length;
            if (idx != _last) Apply(idx);
        }

        private void Apply(int idx)
        {
            if (_holdLast && idx > 0 && _last == 0)
                Debug.Log($"[texanim] HoldLast: switched to final frame {idx}/{(_frames?.Length ?? 0) - 1}");
            _last = idx;
            if (_mats == null || _frames == null || idx < 0 || idx >= _frames.Length) return;
            var t = _frames[idx];
            for (int i = 0; i < _mats.Length; i++)
            {
                var m = _mats[i];
                if (m == null) continue;
                // A submesh whose base texture failed to load fell back to a texture-less shader (Unlit/Color). Setting
                // .mainTexture on it makes Unity spam "Material '' … doesn't have a texture property '_MainTex'" with no
                // clue which prop. Skip it and name the offending object ONCE (the holder is "<baseName>_texanim").
                if (m.HasProperty(MainTexId)) m.mainTexture = t;
                else WarnNoMainTex(m);
            }
        }

        private void WarnNoMainTex(Material m)
        {
            if (_warnedNoTex) return;
            _warnedNoTex = true;
            Debug.LogWarning($"[texanim] '{name}': material '{m.name}' (shader '{(m.shader != null ? m.shader.name : "null")}') " +
                             "has no _MainTex — this prop's base texture failed to load, so its animated frames can't show (flat colour instead).");
        }
    }
}
