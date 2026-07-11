using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Drives the SCN0003 (車庫) disco-floor formation: 256 tiles, each its OWN material, re-textured every 300 ms to
    /// BOX_&lt;BoxFloorPattern.Table[step*256 + tile]&gt; as the step cycles 0..22 — the moving pattern the original shows
    /// (decompiled Scene_UpdateSceneObjects case 3). Materials are in tile-index order (= box instance order).
    /// </summary>
    public sealed class BoxFloorAnimator : MonoBehaviour
    {
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private Material[] _mats;     // one per tile, in tile-index order
        private Texture2D[] _frames;  // BOX_0..BOX_5
        private float _accMs;
        private int _step;
        private bool _warnedNoTex;    // one-shot: named a texture-less tile material so the log isn't spammed

        public void Init(Material[] mats, Texture2D[] frames)
        {
            _mats = mats; _frames = frames; _step = 0; Apply();
        }

        private void Update()
        {
            if (_mats == null) return;
            _accMs += Time.deltaTime * 1000f;
            if (_accMs < BoxFloorPattern.IntervalMs) return;
            _accMs -= BoxFloorPattern.IntervalMs;
            _step = (_step + 1) % BoxFloorPattern.Steps;
            Apply();
        }

        private void Apply()
        {
            var tbl = BoxFloorPattern.Table;
            int baseIdx = _step * BoxFloorPattern.Tiles;
            int n = Mathf.Min(_mats.Length, BoxFloorPattern.Tiles);
            for (int i = 0; i < n; i++)
            {
                if (_mats[i] == null) continue;
                int f = tbl[baseIdx + i];
                if (f < 0 || f >= _frames.Length || _frames[f] == null) continue;
                if (_mats[i].HasProperty(MainTexId)) _mats[i].mainTexture = _frames[f];   // texture-capable tile
                else if (!_warnedNoTex)                                                    // fallback tile (BOX_ texture failed) -> name it once
                {
                    _warnedNoTex = true;
                    Debug.LogWarning($"[boxfloor] '{name}': tile material '{_mats[i].name}' (shader '{(_mats[i].shader != null ? _mats[i].shader.name : "null")}') " +
                                     "has no _MainTex — a BOX_ floor texture failed to load, so this tile can't animate.");
                }
            }
        }
    }
}
