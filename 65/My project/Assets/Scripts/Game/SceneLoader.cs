using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Loads an SDO stage scene (SCENE.MSH). The file is "Mesh" + an 8-byte tag, then a u32 BLOCK COUNT at
    /// offset 12, then that many concatenated mesh blocks (verified against the decompiled
    /// Model_loadFromData_0041a7e0). Almost every map ships a single block, but a few are split — SCN0026
    /// (basketball court) is 35 blocks, one per textured prop (CHE/DENG/LABA/court 1.dds…24.dds). The old
    /// loader parsed only block 0 (a 109-face, 14.dds stub), so SCN0026 rendered almost entirely black.
    ///
    /// Each block: FVF 0x142 (pos+diffuse+uv, no skin) header [fvf, idxBytes, opt], the u16 index buffer,
    /// [vertBytes, stride] + the vertex buffer, a 24-byte bbox, u32 nMat, nMat×408-byte material records, then
    /// a footer: header [type, _, footerSize, count] + count×24-byte D3DXATTRIBUTERANGE records ([matId,
    /// faceStart, faceCount, vertStart, vertCount, ext], contiguous) + count×4-byte tail lists; footerSize =
    /// count×28. After the footer a type-specific tail (type 2 = a per-object index list + a 64-byte bbox-node
    /// tree) runs out to the next block. Each block-subset becomes a Unity submesh + its .dds material; all
    /// blocks merge into one mesh so the whole room (floor/walls/audience/props) is textured correctly.
    /// </summary>
    public static class SceneLoader
    {
        public sealed class Result { public Mesh Mesh; public Material[] Materials; public int[] MaterialIds; }

        public struct SceneSubset { public int MatId; public int FaceStart; public int FaceCount; }

        /// <summary>One parsed mesh block. Pure data (byte offsets into the file) so it is unit-testable
        /// without Unity; <see cref="Load"/> turns it into geometry + materials.</summary>
        public sealed class SceneBlock
        {
            public int Offset;        // block start (byte)
            public uint Fvf;
            public int IdxStart;      // byte offset of the u16 index buffer
            public int IdxCount;      // number of u16 indices
            public int VertStart;     // byte offset of the vertex buffer
            public int VertCount;
            public int Stride;        // vertex stride in bytes
            public string[] DdsNames; // per-material .dds filename
            public SceneSubset[] Subsets;
            public int Type;          // footer mesh type (0 skin / 1 plain / 2 bone-or-static)
            public int Next;          // byte offset of the next block, or -1 if last/unparseable
        }

        /// <summary>
        /// Splits a SCENE.MSH buffer into its mesh blocks. PURE (byte[] -> blocks), no Unity types, so the
        /// block-boundary maths (the part that broke SCN0026) is unit-tested directly. Stops early — keeping
        /// the blocks parsed so far — on any out-of-range field rather than throwing.
        /// </summary>
        public static List<SceneBlock> ParseBlocks(byte[] d)
        {
            var list = new List<SceneBlock>();
            if (d == null || d.Length < 16 || System.Text.Encoding.ASCII.GetString(d, 0, 4) != "Mesh") return list;
            int blockCount = (int)U(d, 12);
            if (blockCount < 1 || blockCount > 8192) blockCount = 1;   // sanity: every shipped scene is 1..35

            int p = 16;
            for (int blk = 0; blk < blockCount; blk++)
            {
                if (p < 0 || p + 12 > d.Length) break;
                var b = new SceneBlock { Offset = p, Fvf = U(d, p) };
                int idxBytes = (int)U(d, p + 4);                  // [p+8] = opt (0x65), unused
                b.IdxStart = p + 12;
                b.IdxCount = idxBytes / 2;
                int vsec = b.IdxStart + idxBytes;
                if (vsec + 8 > d.Length) break;
                int vertBytes = (int)U(d, vsec);
                b.Stride = (int)U(d, vsec + 4);
                if (b.Stride < 16) break;
                b.VertStart = vsec + 8;
                b.VertCount = vertBytes / b.Stride;
                int post = b.VertStart + vertBytes;               // 24-byte bbox + u32 nMat follow
                if (post + 28 > d.Length) break;
                int numMat = (int)U(d, post + 24);
                if (numMat <= 0 || numMat > 256) break;
                int matnames = post + 28;
                if ((long)matnames + (long)numMat * 408 + 16 > d.Length) break;
                b.DdsNames = new string[numMat];
                for (int m = 0; m < numMat; m++) b.DdsNames[m] = ReadCStr(d, matnames + m * 408 + 17 * 4, 48);

                int fp = matnames + numMat * 408;                 // footer
                b.Type = (int)U(d, fp);
                int footerSize = (int)U(d, fp + 8);
                int count = (int)U(d, fp + 12);

                // attribute table: count×24-byte contiguous records (count == numMat). Verified vs SCN0009:
                // 24 records cover all 3878 faces. The 4-byte-per-entry tail lists sit AFTER the records
                // (footerSize = count×28), so the record stride is 24 here, not 28.
                int faces = b.IdxCount / 3;
                int rec = fp + 16;
                var subs = new List<SceneSubset>();
                for (int m = 0; m < numMat && rec + 24 <= d.Length; m++, rec += 24)
                {
                    int matId = (int)U(d, rec), fStart = (int)U(d, rec + 4), fCount = (int)U(d, rec + 8);
                    if (matId < 0 || matId >= numMat || fCount <= 0 || fStart + fCount > faces) break;
                    subs.Add(new SceneSubset { MatId = matId, FaceStart = fStart, FaceCount = fCount });
                }
                if (subs.Count == 0) subs.Add(new SceneSubset { MatId = 0, FaceStart = 0, FaceCount = faces });
                b.Subsets = subs.ToArray();

                // advance to the next block. footerSize covers the records + tail lists; then a type-specific
                // tail. type 2 (every SCN0026 block): count×3 floats, a u16 index list, then a 64-byte node tree.
                int afterFooter = fp + 16 + footerSize;
                int next;
                if (b.Type == 2)
                {
                    int b2 = afterFooter + count * 12;
                    if (b2 + 8 > d.Length) next = -1;
                    else
                    {
                        int nIdx = (int)U(d, b2 + 4);
                        int nodesAt = b2 + 8 + nIdx * 2;
                        if (nodesAt + 4 > d.Length) next = -1;
                        else next = nodesAt + 4 + (int)U(d, nodesAt) * 64;
                    }
                }
                else if (b.Type == 1) next = afterFooter + count * 12 + 4;
                else next = -1;   // type 0 / unknown tail layout: stop rather than mis-seek

                b.Next = next;
                list.Add(b);
                if (next < 0 || next <= p || next > d.Length) break;
                p = next;
            }
            return list;
        }

        public static Result Load(byte[] d, string sceneDir)
        {
            var blocks = ParseBlocks(d);
            if (blocks.Count == 0) return null;

            // SCN0020 (19_subway): classify scene alpha by DISTRIBUTION so hard-edged structural materials (the
            // truss GANGJIA, the dance floor WUTAIDIMIAN, the PANGGUAN spectators) render as depth-writing
            // Cutout/Opaque instead of non-occluding alpha-Blend. Scoped to this scene to avoid touching the
            // validated去背 of other maps.
            bool histoAlpha = string.Equals(Path.GetFileName(sceneDir?.TrimEnd('/', '\\') ?? ""), "SCN0020",
                                             StringComparison.OrdinalIgnoreCase);
            // cutout × VERTEX COLOUR: walls stay opaque, DXT3 audience billboards discard their transparent
            // background, and the per-vertex baked lighting/tint darkens the scene (e.g. SCN0008 night).
            var cutoutShader = Shader.Find("Sdo/SceneVertexCutout") ?? Shader.Find("Unlit/Transparent Cutout") ?? Shader.Find("Unlit/Texture");
            var alphaShader = Shader.Find("Sdo/SceneVertexAlpha") ?? Shader.Find("Unlit/Transparent") ?? cutoutShader;

            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var cols = new List<Color32>();
            bool anyDiffuse = false;
            var subTris = new List<int[]>();
            var subMats = new List<Material>();
            var subMatIds = new List<int>();
            // Decode each .dds once even when several blocks reuse it (SCN0026 1.dds spans 3 blocks); a fresh
            // Material per subset still references the shared texture (matches the original per-subset draw).
            var texCache = new Dictionary<string, (Texture2D tex, DdsAlphaMode mode)>(StringComparer.OrdinalIgnoreCase);

            foreach (var b in blocks)
            {
                int baseVert = verts.Count;
                int uvOff = b.Stride - 8;
                bool hasDiffuse = uvOff >= 16;   // FVF 0x142: pos(12)+DIFFUSE(4)+uv(8) -> diffuse at offset 12
                for (int i = 0; i < b.VertCount; i++)
                {
                    int o = b.VertStart + i * b.Stride;
                    verts.Add(new Vector3(F(d, o), F(d, o + 4), F(d, o + 8)));   // verbatim (D3D9 & Unity both LH)
                    uvs.Add(new Vector2(F(d, o + uvOff), F(d, o + uvOff + 4)));   // V NOT flipped (same as avatar)
                    if (hasDiffuse)
                    {
                        // per-vertex DIFFUSE = baked scene lighting/tint (D3DCOLOR 0xAARRGGBB). Keep RGB (the
                        // darkening); alpha stays 255 (cut-out comes from the texture, not the vertex).
                        uint c = (uint)(d[o + 12] | (d[o + 13] << 8) | (d[o + 14] << 16) | (d[o + 15] << 24));
                        cols.Add(new Color32((byte)((c >> 16) & 0xff), (byte)((c >> 8) & 0xff), (byte)(c & 0xff), 255));
                        anyDiffuse = true;
                    }
                    else cols.Add(new Color32(255, 255, 255, 255));   // neutral: the shader multiply is a no-op
                }

                var tris = new int[b.IdxCount];
                for (int i = 0; i < b.IdxCount; i++) { int q = b.IdxStart + i * 2; tris[i] = (ushort)(d[q] | (d[q + 1] << 8)); }
                foreach (var s in b.Subsets)
                {
                    // SINGLE-SIDED with backface culling (the shader culls), matching the original's D3D cull so
                    // inward-facing walls/columns block the dancer instead of being see-through from behind.
                    var sub = new int[s.FaceCount * 3];
                    for (int f = 0; f < s.FaceCount; f++)
                    {
                        int o = (s.FaceStart + f) * 3, w = f * 3;
                        sub[w] = tris[o] + baseVert; sub[w + 1] = tris[o + 1] + baseVert; sub[w + 2] = tris[o + 2] + baseVert;
                    }

                    string name = b.DdsNames[s.MatId] ?? "";
                    if (!texCache.TryGetValue(name, out var tm))
                    {
                        Texture2D tex = null; DdsAlphaMode mode = DdsAlphaMode.Opaque;
                        var ddsPath = Path.Combine(sceneDir, name);
                        if (name.Length > 0 && File.Exists(ddsPath))
                        {
                            var bytes = File.ReadAllBytes(ddsPath);
                            tex = DdsLoader.Load(bytes);
                            mode = histoAlpha ? DdsLoader.GetSceneAlphaMode(bytes) : DdsLoader.GetAlphaMode(bytes);
                        }
                        tm = (tex, mode); texCache[name] = tm;
                    }

                    var shader = tm.mode == DdsAlphaMode.Blend ? alphaShader : cutoutShader;
                    var mat = tm.tex != null ? new Material(shader) { mainTexture = tm.tex } : new Material(shader) { color = new Color(0.3f, 0.3f, 0.35f) };
                    // Soft DDS alpha is alpha-blended. Pure hard alpha stays a cutout. Opaque DDS disables clipping.
                    if (tm.mode != DdsAlphaMode.Blend)
                        mat.SetFloat("_Cutoff", tm.mode == DdsAlphaMode.Cutout ? 0.5f : -1f);
                    subTris.Add(sub); subMats.Add(mat); subMatIds.Add(s.MatId);
                }
            }
            if (subTris.Count == 0) return null;

            var mesh = new Mesh { name = "scene" };
            if (verts.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts); mesh.SetUVs(0, uvs);
            if (anyDiffuse) mesh.SetColors(cols);   // baked vertex lighting -> the shader multiplies it in
            mesh.subMeshCount = subTris.Count;
            for (int s = 0; s < subTris.Count; s++) mesh.SetTriangles(subTris[s], s);
            mesh.RecalculateBounds();
            return new Result { Mesh = mesh, Materials = subMats.ToArray(), MaterialIds = subMatIds.ToArray() };
        }

        private static uint U(byte[] d, int o) => (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
        private static float F(byte[] d, int o) => BitConverter.ToSingle(d, o);
        private static string ReadCStr(byte[] d, int o, int max) { int n = 0; while (n < max && o + n < d.Length && d[o + n] != 0) n++; return System.Text.Encoding.ASCII.GetString(d, o, n); }
    }
}
