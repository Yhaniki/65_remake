using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// The dancer's hand glow, faithful to the original (decomp FUN_004a6e10 / FUN_004c2130 / FUN_004c1ea0):
    /// a WORLD-SPACE ribbon — NOT a camera-facing TrailRenderer. Each cross-section's two edges are the real
    /// bone world positions: <c>inner = Hand</c>, <c>outer = 2*Finger0 - Hand</c>. So the band has a true palm
    /// WIDTH that thins/widens as the hand rotates and visibly "comes out of the palm" (a billboard trail can't).
    /// Nodes are time-sampled and fade along their length (orig: 8 nodes x 30ms ~= 0.24s; white verts x a gold
    /// additive texture -> here gold verts on an additive material with an alpha fade toward the tail).
    /// The GameObject stays at the world origin: the mesh vertices are world-space (the anchors report world
    /// positions), matching the original where positions are submitted raw (FVF XYZ, identity world transform).
    /// </summary>
    [DefaultExecutionOrder(100)]   // sample AFTER SdoAvatar.LateUpdate has posed the bones / moved the anchors
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class HandRibbon : MonoBehaviour
    {
        public Transform hand, finger;   // anchors tracking the Hand and Finger0 bone world positions
        public float life = 0.24f;       // node lifetime in seconds (original: 8 segments x 30ms)
        public float widthMul = 1f;      // scales the palm half-width about the finger centreline (1 = faithful 2*|Hand-Finger0|)
        public Color color = Color.white;

        // optional external clock (song time) so it advances headless; falls back to Time.time
        public System.Func<float> Now;

        private Mesh _mesh;
        private readonly List<float> _t = new List<float>();
        private readonly List<Vector3> _inner = new List<Vector3>();
        private readonly List<Vector3> _outer = new List<Vector3>();
        // reused mesh buffers (no per-frame GC)
        private readonly List<Vector3> _vb = new List<Vector3>();
        private readonly List<Color> _cb = new List<Color>();
        private readonly List<Vector2> _ub = new List<Vector2>();
        private readonly List<int> _ib = new List<int>();

        private void Awake()
        {
            _mesh = new Mesh { name = "HandRibbon" };
            _mesh.MarkDynamic();
            var mf = GetComponent<MeshFilter>() ?? gameObject.AddComponent<MeshFilter>();
            mf.mesh = _mesh;
            transform.position = Vector3.zero; transform.rotation = Quaternion.identity; transform.localScale = Vector3.one;
        }

        private float Clock => Now != null ? Now() : Time.time;

        private void LateUpdate()   // after SdoAvatar.LateUpdate has posed the bones / moved the anchors
        {
            if (hand == null || finger == null) { return; }
            float now = Clock;
            Vector3 h = hand.position, f = finger.position;
            if (h == Vector3.zero && f == Vector3.zero) { return; }   // avatar not posed yet -> don't streak from the origin
            Vector3 half = (f - h) * widthMul;                 // palm half-width vector (world); rotates with the hand
            _inner.Add(f - half); _outer.Add(f + half); _t.Add(now);
            while (_t.Count > 0 && now - _t[0] > life) { _t.RemoveAt(0); _inner.RemoveAt(0); _outer.RemoveAt(0); }   // expire by time window
            Rebuild(now);
        }

        private void Rebuild(float now)
        {
            int n = _t.Count;
            _vb.Clear(); _cb.Clear(); _ub.Clear(); _ib.Clear();
            if (n < 2) { _mesh.Clear(); return; }
            for (int i = 0; i < n; i++)
            {
                float age = life > 1e-5f ? Mathf.Clamp01((now - _t[i]) / life) : 0f;   // 0 = newest (at the hand), 1 = oldest (tail)
                Color c = color; c.a = color.a * (1f - age);                            // fade alpha toward the tail (texture-fade analogue)
                _vb.Add(_inner[i]); _vb.Add(_outer[i]);
                _cb.Add(c); _cb.Add(c);
                _ub.Add(new Vector2(0f, age)); _ub.Add(new Vector2(1f, age));           // U across width, V along length
            }
            for (int i = 0; i < n - 1; i++)
            {
                int a0 = i * 2, b0 = i * 2 + 1, a1 = (i + 1) * 2, b1 = (i + 1) * 2 + 1;
                _ib.Add(a0); _ib.Add(a1); _ib.Add(b0);
                _ib.Add(b0); _ib.Add(a1); _ib.Add(b1);
            }
            _mesh.Clear();
            _mesh.SetVertices(_vb);
            _mesh.SetColors(_cb);
            _mesh.SetUVs(0, _ub);
            _mesh.SetTriangles(_ib, 0);
            _mesh.RecalculateBounds();
        }

        public void Clear() { _t.Clear(); _inner.Clear(); _outer.Clear(); if (_mesh != null) _mesh.Clear(); }
    }
}
