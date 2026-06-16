using System;
using System.IO;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Loads an SDO stage scene (SCENE.MSH): a single static mesh (FVF 0x142 = pos+diffuse+uv, no skin)
    /// split into per-material subsets. The skin-info footer holds a D3DXATTRIBUTERANGE-style table:
    /// header [2,1,672,nMat] then nMat contiguous 6-int records [matId, faceStart, faceCount, vertStart,
    /// vertCount, ext] (verified: 24 records cover all 3878 faces of SCN0009). Each subset -> a Unity
    /// submesh + its .dds material, so the room (floor/walls/audience) is textured correctly.
    /// </summary>
    public static class SceneLoader
    {
        public sealed class Result { public Mesh Mesh; public Material[] Materials; }

        public static Result Load(byte[] d, string sceneDir)
        {
            if (d == null || d.Length < 16 || System.Text.Encoding.ASCII.GetString(d, 0, 4) != "Mesh") return null;
            int p = 12;
            uint submeshes = U32(d, ref p); if (submeshes < 1) return null;

            U32(d, ref p);                                   // fvf (0x142)
            int idxBytes = (int)U32(d, ref p); U32(d, ref p); // options
            int idxCount = idxBytes / 2;
            // Verbatim indices: verts are NOT X-negated (below), and D3D9-LH & Unity share the CW-front convention,
            // so the original winding is the correct facing. (The old reverse-winding here flipped the facing, which
            // only stayed hidden because the room was drawn double-sided.)
            var tris = new int[idxCount];
            for (int i = 0; i < idxCount; i++) { tris[i] = (ushort)(d[p] | (d[p + 1] << 8)); p += 2; }

            int vertBytes = (int)U32(d, ref p);
            int stride = (int)U32(d, ref p); if (stride < 16) return null;
            int vcount = vertBytes / stride;
            int uvOff = stride - 8;
            var verts = new Vector3[vcount]; var uvs = new Vector2[vcount];
            for (int i = 0; i < vcount; i++)
            {
                int b = p + i * stride;
                verts[i] = new Vector3(F(d, b), F(d, b + 4), F(d, b + 8));   // verbatim (D3D9 & Unity both LH); same as avatar
                uvs[i] = new Vector2(F(d, b + uvOff), F(d, b + uvOff + 4));   // V NOT flipped (same convention as the avatar)
            }
            p += vertBytes;
            p += 24;                                          // reserved[6]
            int numMat = (int)U32(d, ref p);
            if (numMat <= 0 || numMat > 256) return null;
            var ddsNames = new string[numMat];
            for (int m = 0; m < numMat; m++)
            {
                if (p + 408 > d.Length) return null;
                ddsNames[m] = ReadCStr(d, p + 17 * 4, 48);
                p += 408;
            }
            // footer attribute table: [hdr0,hdr1,hdr2,nMat] then nMat * 6-int records
            int fp = p;
            uint U(int o) => o + 4 <= d.Length ? (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24)) : 0;
            int rec = fp + 16;                                // skip 4-int header
            int faces = idxCount / 3;
            var subsets = new System.Collections.Generic.List<(int mat, int fStart, int fCount)>();
            for (int m = 0; m < numMat && rec + 24 <= d.Length; m++, rec += 24)
            {
                int matId = (int)U(rec), fStart = (int)U(rec + 4), fCount = (int)U(rec + 8);
                if (matId < 0 || matId >= numMat || fCount <= 0 || fStart + fCount > faces) break;
                subsets.Add((matId, fStart, fCount));
            }
            if (subsets.Count == 0) subsets.Add((0, 0, faces));   // fallback: whole mesh = material 0

            var mesh = new Mesh { name = "scene" };
            if (vcount > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts; mesh.uv = uvs;
            mesh.subMeshCount = subsets.Count;
            var mats = new Material[subsets.Count];
            // cutout: walls (no alpha) stay opaque; DXT3 audience billboards discard their transparent background
            var shader = Shader.Find("Unlit/Transparent Cutout") ?? Shader.Find("Unlit/Texture");
            for (int s = 0; s < subsets.Count; s++)
            {
                var (matId, fStart, fCount) = subsets[s];
                // SINGLE-SIDED with backface culling, matching the original's D3D cull. The room's walls/columns
                // face INWARD, so a camera behind one (e.g. fixed cam5 at z=-346, behind the back columns at z=-300)
                // sees their culled back faces = sees THROUGH them to the dancer — instead of the column blocking.
                var sub = new int[fCount * 3];
                for (int f = 0; f < fCount; f++)
                {
                    int o = (fStart + f) * 3, w = f * 3;
                    sub[w] = tris[o]; sub[w + 1] = tris[o + 1]; sub[w + 2] = tris[o + 2];
                }
                mesh.SetTriangles(sub, s);
                Texture2D tex = null;
                var ddsPath = Path.Combine(sceneDir, ddsNames[matId]);
                if (File.Exists(ddsPath)) tex = DdsLoader.Load(File.ReadAllBytes(ddsPath));
                mats[s] = tex != null ? new Material(shader) { mainTexture = tex } : new Material(shader) { color = new Color(0.3f, 0.3f, 0.35f) };
            }
            mesh.RecalculateBounds();
            return new Result { Mesh = mesh, Materials = mats };
        }

        private static uint U32(byte[] d, ref int p) { uint v = (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24)); p += 4; return v; }
        private static float F(byte[] d, int o) => BitConverter.ToSingle(d, o);
        private static string ReadCStr(byte[] d, int o, int max) { int n = 0; while (n < max && o + n < d.Length && d[o + n] != 0) n++; return System.Text.Encoding.ASCII.GetString(d, o, n); }
    }
}
