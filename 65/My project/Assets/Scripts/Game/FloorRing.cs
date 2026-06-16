using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Faithful reproduction of yuanpan.eft's floor ring, drawn the way the engine does it
    /// (SDO <c>Particle_InitFromTemplate FUN_004beeb0</c> ring-geometry branch): an annulus MESH of N
    /// segment-quads, inner radius : outer radius = 0.18 : 0.27, each quad mapped with the FULL texture
    /// (u 0→1 across the arc, v 1 inner → 0 outer) so every segment shows one complete <c>z_piyori1</c>
    /// hollow star. Rendered additive, lying flat on the floor, spinning about its normal
    /// (<c>Effect_SetTransformAnimated_004bcba0</c> accumulates a rotation delta and wraps at 360°), and
    /// tracking the dancer via a pelvis-bone anchor (the avatar root GO is fixed; the dance moves the bones).
    /// </summary>
    public sealed class FloorRing : MonoBehaviour
    {
        public Transform Follow;             // dancer pelvis anchor; ring re-centres on its X/Z each frame
        public float FloorY = 0.6f;
        public float SpinDegPerSec = 20f;    // >0 = counter-clockwise viewed from above
        private float _spin;

        private void LateUpdate()   // after SdoAvatar.LateUpdate has posed the skeleton + moved the anchor
        {
            if (Follow) { var p = Follow.position; transform.position = new Vector3(p.x, FloorY, p.z); }
            _spin += SpinDegPerSec * Time.deltaTime;
            if (_spin > 360f) _spin -= 360f; else if (_spin < 0f) _spin += 360f;
            transform.rotation = Quaternion.Euler(90f, 0f, 0f) * Quaternion.AngleAxis(_spin, Vector3.forward);
        }

        /// <summary>Build the ring-band mesh exactly like the engine: N quads, inner radius ri, outer ro,
        /// full-texture UV per segment (so each segment = one complete star), white vertex colour.</summary>
        public static Mesh BuildBand(int segs, float ri, float ro)
        {
            int vc = segs * 4;
            var verts = new Vector3[vc]; var uv = new Vector2[vc]; var col = new Color32[vc]; var tris = new int[segs * 6];
            var white = new Color32(255, 255, 255, 255);
            for (int i = 0; i < segs; i++)
            {
                float a0 = i * Mathf.PI * 2f / segs, a1 = (i + 1) * Mathf.PI * 2f / segs;
                float c0 = Mathf.Cos(a0), s0 = Mathf.Sin(a0), c1 = Mathf.Cos(a1), s1 = Mathf.Sin(a1);
                int v = i * 4;
                verts[v + 0] = new Vector3(c0 * ri, s0 * ri, 0f); uv[v + 0] = new Vector2(0f, 1f); // inner @ a0
                verts[v + 1] = new Vector3(c1 * ri, s1 * ri, 0f); uv[v + 1] = new Vector2(1f, 1f); // inner @ a1
                verts[v + 2] = new Vector3(c1 * ro, s1 * ro, 0f); uv[v + 2] = new Vector2(1f, 0f); // outer @ a1
                verts[v + 3] = new Vector3(c0 * ro, s0 * ro, 0f); uv[v + 3] = new Vector2(0f, 0f); // outer @ a0
                col[v] = col[v + 1] = col[v + 2] = col[v + 3] = white;
                int t = i * 6;
                tris[t + 0] = v + 0; tris[t + 1] = v + 2; tris[t + 2] = v + 1;
                tris[t + 3] = v + 0; tris[t + 4] = v + 3; tris[t + 5] = v + 2;
            }
            var m = new Mesh { name = "YuanpanRing" };
            m.vertices = verts; m.uv = uv; m.colors32 = col; m.triangles = tris;
            m.RecalculateBounds();
            return m;
        }
    }
}
