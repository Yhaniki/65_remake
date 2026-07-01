using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Renders the official 3D-note glyphs (the real NOTES.MSH / JUDGELINE.MSH arrow meshes) FLAT, aligned 1:1 to the
    /// remake's existing 2D note layout — same lanes, same scroll Y — instead of guessing a tilted perspective camera.
    /// The host (<see cref="ScreenGameplay"/>) already knows each note's on-screen position, so it hands them here as
    /// world positions and this pool draws the arrow mesh there: the flat XZ mesh is stood into the screen plane, scaled
    /// to the note size, spun per lane, textured by the beat-colour family, and drawn ADDITIVE (glow-on-black = clean 去背).
    /// The mesh gives the exact arrow silhouette; additive glow avoids the sprite's background box.
    /// </summary>
    [DefaultExecutionOrder(95)]
    public sealed class Note3dHighway : MonoBehaviour
    {
        public float noteFrameFps = 6f;    // glow-frame cycle (0 = freeze; exe cycles per render-frame = a fast flicker)
        public float flattenX = 90f;       // rotate the XZ arrow mesh flat into the screen (±90); F4 toggle if it faces away
        public float baseRotZ = 0f;        // global spin added to every arrow (0/90/180/270) to fix the base direction
        public bool visible;

        // note mesh native half-width (X ±10.98) → the item Size (design px) maps to scale = Size / (2*meshHalfW).
        const float MeshHalfW = 10.98f;

        Material _addTemplate;
        Mesh _noteMesh, _judgeMesh;
        Texture2D[][] _fam;                // [family 0..2][glow frame 0..3]
        Texture2D[] _judgeFrames;          // 3 receptor frames
        Transform _root;
        int _layer;
        readonly List<GameObject> _pool = new List<GameObject>();
        int _used;
        bool _built;

        public bool Ready => _built;

        /// <summary>One glyph to draw this frame at a known on-screen world position (from the 2D layout).</summary>
        public struct Item { public Vector3 World; public float Size, RotZ; public int Family; public bool Receptor; }

        /// <summary>Load meshes/textures. layer = the ORTHO play-field layer (0), so the glyphs draw in the same camera
        /// and space as the 2D notes they replace.</summary>
        public void Build(int layer)
        {
            if (_built) return;
            _layer = layer;
            _addTemplate = new Material(Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Sprites/Default"));
            if (!LoadAssets()) { Debug.LogWarning("[note3d] mesh assets failed to load"); return; }
            _root = new GameObject("Note3dMeshes_root").transform;
            _built = true;
        }

        bool LoadAssets()
        {
            string dir = Path.Combine(SdoExtracted.Root, "3DNOTES");
            _noteMesh = ParseMsh(TryRead(Path.Combine(dir, "NOTES.MSH")));
            _judgeMesh = ParseMsh(TryRead(Path.Combine(dir, "JUDGELINE.MSH")));
            if (_noteMesh == null) return false;
            if (_judgeMesh == null) _judgeMesh = _noteMesh;
            _fam = new Texture2D[3][];
            string[] pfx = { "NOTES_", "NOTES1_", "NOTES2_" };   // 0=magenta 1=blue 2=green (NoteBeatColor)
            for (int f = 0; f < 3; f++) { _fam[f] = new Texture2D[4]; for (int i = 0; i < 4; i++) _fam[f][i] = LoadTex(Path.Combine(dir, pfx[f] + i + ".DDS")); }
            _judgeFrames = new Texture2D[3];
            for (int i = 0; i < 3; i++) _judgeFrames[i] = LoadTex(Path.Combine(dir, "JUDGELINE_" + i + ".DDS"));
            return true;
        }

        // keyed glow texture (bg → alpha 0), mesh orientation (flipV=false); additive draw makes the keyed bg add nothing.
        static Texture2D LoadTex(string path) { try { return File.Exists(path) ? DdsLoader.LoadDxt1Alpha(File.ReadAllBytes(path), flipV: false) : null; } catch { return null; } }
        static byte[] TryRead(string p) { try { return File.Exists(p) ? File.ReadAllBytes(p) : null; } catch { return null; } }

        /// <summary>Draw the frame's note + receptor glyphs at their (already-computed 2D) world positions.</summary>
        public void SetItems(List<Item> items)
        {
            if (!_built) return;
            _root.gameObject.SetActive(visible);
            if (!visible) { return; }
            int frame = noteFrameFps > 0f ? (int)(Time.time * noteFrameFps) : 0;
            _used = 0;
            for (int i = 0; i < items.Count; i++) Place(items[i], frame);
            for (int i = _used; i < _pool.Count; i++) if (_pool[i].activeSelf) _pool[i].SetActive(false);
        }

        void Place(Item it, int frame)
        {
            var go = Rent();
            var mf = go.GetComponent<MeshFilter>();
            var mr = go.GetComponent<MeshRenderer>();
            Texture2D tex;
            if (it.Receptor) { if (mf.sharedMesh != _judgeMesh) mf.sharedMesh = _judgeMesh; tex = _judgeFrames != null ? _judgeFrames[frame % 3] : null; }
            else { if (mf.sharedMesh != _noteMesh) mf.sharedMesh = _noteMesh; var fam = _fam[Mathf.Clamp(it.Family, 0, 2)]; tex = fam != null ? fam[frame & 3] : null; }
            if (tex != null && mr.sharedMaterial.mainTexture != tex) mr.sharedMaterial.mainTexture = tex;
            go.transform.position = it.World;
            // flat XZ arrow → stood into the screen plane (flattenX about X), then spun per lane about the view normal (Z).
            go.transform.localRotation = Quaternion.Euler(0f, 0f, it.RotZ + baseRotZ) * Quaternion.Euler(flattenX, 0f, 0f);
            go.transform.localScale = Vector3.one * (it.Size / (2f * MeshHalfW));
            if (!go.activeSelf) go.SetActive(true);
        }

        GameObject Rent()
        {
            if (_used < _pool.Count) return _pool[_used++];
            var go = new GameObject("note3d-mesh");
            go.transform.SetParent(_root, false);
            go.layer = _layer;
            go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
            mr.sortingOrder = 6;   // over the board (-30) / at the 2D note order — like the sprite it replaces
            mr.sharedMaterial = new Material(_addTemplate);   // own additive instance (per-note texture, no cross-bleed)
            _pool.Add(go); _used++;
            return go;
        }

        // Parse a 3DNOTES .MSH single submesh → Unity Mesh (verts + uv + tris). Header: "Mesh00000030"(12)+submeshCount(4)
        // then [fvf(4)][idxBytes(4)][opt(4)][indices][vertBytes(4)][stride(4)][verts]. pos @0, uv @stride-8 (note stride40
        // uv@32). Verts verbatim; double-sided additive material → winding/facing irrelevant.
        static Mesh ParseMsh(byte[] d)
        {
            if (d == null || d.Length < 20 || Encoding.ASCII.GetString(d, 0, 12) != "Mesh00000030") return null;
            int I(int p) => BitConverter.ToInt32(d, p);
            float F(int p) => BitConverter.ToSingle(d, p);
            int q = 16 + 4;   // skip magic(12)+submeshCount(4)+fvf(4)
            int idxBytes = I(q); q += 4;
            q += 4;           // opt
            int idxCount = idxBytes / 2;
            if (idxCount <= 0 || q + idxBytes > d.Length) return null;
            var tris = new int[idxCount];
            for (int i = 0; i < idxCount; i++) { tris[i] = (ushort)(d[q] | (d[q + 1] << 8)); q += 2; }
            int vertBytes = I(q); q += 4;
            int stride = I(q); q += 4;
            if (stride < 16 || vertBytes <= 0 || q + vertBytes > d.Length) return null;
            int vcount = vertBytes / stride, uvOff = stride - 8;
            var verts = new Vector3[vcount]; var uvs = new Vector2[vcount];
            for (int i = 0; i < vcount; i++)
            {
                int b = q + i * stride;
                verts[i] = new Vector3(F(b), F(b + 4), F(b + 8));
                uvs[i] = new Vector2(F(b + uvOff), F(b + uvOff + 4));
            }
            var mm = new Mesh { name = "note3d-msh" };
            mm.vertices = verts; mm.uv = uvs; mm.triangles = tris; mm.RecalculateBounds();
            return mm;
        }
    }
}
