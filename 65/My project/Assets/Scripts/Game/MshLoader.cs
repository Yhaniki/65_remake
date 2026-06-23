using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Loads an SDO ".msh" (faithful port of bms_sdo/msh_reader.py). Parses ALL submeshes; each submesh has
    /// material ranges (D3DXATTRIBUTERANGE: attrib,faceStart,faceCount,vertStart,vertCount,extra) and a bone
    /// palette footer (hrc_bone_count | palette_size N | inv_bind[N][16] | hrc_to_local[hrc_count]). Multi-range
    /// submeshes split the palette PER RANGE (vertex bone byte = local index into ITS range's palette → HRC bone).
    /// Resolving bones correctly per range fixes the hand/arm skinning. Vertex skin (FVF XYZBn+LASTBETA_UBYTE4):
    ///   stride44(0x1158)=rigid bone0; 48(0x115A)=3-bone; 52(0x115C)=4-bone. X negated -> Unity, winding reversed.
    /// </summary>
    public static class MshLoader
    {
        public sealed class SubMesh
        {
            public Mesh Mesh; public string Dds;
            public string[] DdsNames;                          // all material names (one per attrib range)
            public List<(int Attrib, int FStart, int FCount)> Ranges;  // material face ranges (mesh has 1 Unity submesh per range when >1)
            public Vector3[] BindVerts; public int[] BoneHrc; public float[] BoneWt;  // BoneHrc/BoneWt length = vcount*4 (HRC bone indices)
            public Dictionary<int, Matrix4x4> MshInvBindByHrc;  // HRC bone -> MSH inverse-bind (Unity space) for retarget skinning
        }
        public sealed class Result { public List<SubMesh> Submeshes = new List<SubMesh>(); }

        private sealed class Range { public int Attrib, FStart, FCount, VStart, VCount; public int[] PalHrc; }

        public static Result Load(byte[] d)
        {
            if (d == null || d.Length < 16 || System.Text.Encoding.ASCII.GetString(d, 0, 12) != "Mesh00000030") return null;
            int p = 12;
            int submeshCount = (int)U32(d, ref p);
            if (submeshCount <= 0 || submeshCount > 16) return null;
            var res = new Result();
            for (int s = 0; s < submeshCount; s++)
            {
                var sm = ParseSubmesh(d, ref p);
                if (sm == null) break;
                res.Submeshes.Add(sm);
                if (s < submeshCount - 1) { int nx = ScanNextSubmesh(d, p); if (nx >= d.Length) break; p = nx; }
            }
            return res.Submeshes.Count > 0 ? res : null;
        }

        private static SubMesh ParseSubmesh(byte[] d, ref int p)
        {
            uint fvf = U32(d, ref p);
            int idxSize = (int)U32(d, ref p); U32(d, ref p); // options
            if (idxSize <= 0 || (idxSize & 1) != 0 || p + idxSize > d.Length) return null;
            int idxCount = idxSize / 2;
            var tris = new int[idxCount];
            for (int i = 0; i < idxCount; i++) { tris[i] = (ushort)(d[p] | (d[p + 1] << 8)); p += 2; }

            int vertSize = (int)U32(d, ref p);
            int stride = (int)U32(d, ref p);
            if (stride <= 0 || vertSize <= 0 || vertSize % stride != 0) return null;
            int vcount = vertSize / stride;
            int vertOff = p; p += vertSize;

            for (int i = 0; i < 6; i++) U32(d, ref p);      // r3 reserved[6]
            int numMat = (int)U32(d, ref p);
            int firstMat = p;
            var ddsNames = new List<string>();
            for (int m = 0; m < numMat && p + 408 <= d.Length; m++)
            {
                ddsNames.Add(ReadCStr(d, p + 17 * 4, 320));
                p = firstMat + (m + 1) * 408;
            }
            // probe trailing material entries (some avatar MSH under-report numMat)
            int probe = firstMat + numMat * 408;
            while (ddsNames.Count < 32 && probe + 408 <= d.Length)
            {
                string nm = TryMaterialName(d, probe); if (nm == null) break;
                ddsNames.Add(nm); probe += 408;
            }
            if (probe > p) p = probe;
            string dds = ddsNames.Count > 0 ? ddsNames[0] : null;

            int triTotal = idxCount / 3;
            var ranges = ScanRanges(d, p, vcount, triTotal, System.Math.Max(1, numMat), out int rangeTableEnd);
            var pal = ScanBonePalette(d, p, out int palStart, out int palEnd, out Matrix4x4[] slotInv);
            // per-range palettes live between the range table and the main palette
            if (pal != null && ranges.Count > 0 && palStart > rangeTableEnd)
            {
                int block = palStart - rangeTableEnd, perSize = block / ranges.Count;
                if (perSize > 0 && perSize % 4 == 0 && perSize * ranges.Count == block)
                {
                    int perN = perSize / 4;
                    for (int ri = 0; ri < ranges.Count; ri++)
                    {
                        int rs = rangeTableEnd + ri * perSize; var arr = new int[perN];
                        for (int li = 0; li < perN; li++) { uint raw = U32At(d, rs + li * 4); arr[li] = raw == 0xFFFFFFFF ? -1 : (int)raw; }
                        ranges[ri].PalHrc = arr;
                    }
                }
            }
            if (pal != null && palEnd > 0) p = palEnd;

            // build geometry + resolve bones
            var verts = new Vector3[vcount]; var uvs = new Vector2[vcount];
            var cols = new Color32[vcount];
            int uvOff = stride - 8;
            // Rigid stage props with FVF 0x142 (stride 24) / 0x152 (stride 36) carry a per-vertex D3DCOLOR = BAKED
            // scene lighting (the SCN0008 night tomb is dark — the floating pyramids etc. are darkened by this, not
            // full-bright). It sits right before the uv (offset uvOff-4 = stride-12). Read it into colors32 so the
            // mapobj shader can multiply it in (matching SceneLoader for SCENE.MSH). 0x112 (stride 32) has a NORMAL
            // there, not a colour, so skip it → white (no darkening). White default keeps non-lit props unchanged.
            bool hasDiffuse = (stride == 24 || stride == 36);
            int diffOff = stride - 12;
            int nW = stride == 44 ? 1 : stride == 48 ? 2 : stride == 52 ? 3 : 0;
            int boneOff = 12 + nW * 4;
            int[] mainMap = pal != null ? pal : null;
            var bHrc = new int[vcount * 4]; var bWt = new float[vcount * 4];
            // vertex -> range (by vertex_start/count)
            for (int i = 0; i < vcount; i++)
            {
                int b = vertOff + i * stride;
                verts[i] = new Vector3(F(d, b), F(d, b + 4), F(d, b + 8));   // verbatim — D3D9 & Unity are both LH (no -X)
                uvs[i] = new Vector2(F(d, b + uvOff), F(d, b + uvOff + 4));   // V NOT flipped (reference msh_reader uses v direct)
                // D3DCOLOR 0xAARRGGBB (LE bytes B,G,R,A); keep RGB (the baked darkening), alpha stays 255 (cut-out is the texture's).
                cols[i] = hasDiffuse ? new Color32(d[b + diffOff + 2], d[b + diffOff + 1], d[b + diffOff], 255)
                                     : new Color32(255, 255, 255, 255);
                // weights
                float w0, w1, w2, w3;
                if (stride == 44) { w0 = 1f; w1 = w2 = w3 = 0f; }
                else if (stride == 48) { w0 = F(d, b + 12); w1 = F(d, b + 16); w2 = 1f - w0 - w1; w3 = 0f; }
                else if (stride == 52) { w0 = F(d, b + 12); w1 = F(d, b + 16); w2 = F(d, b + 20); w3 = 1f - w0 - w1 - w2; }
                else { w0 = 1f; w1 = w2 = w3 = 0f; }
                w0 = Mathf.Clamp01(w0); w1 = Mathf.Clamp01(w1); w2 = Mathf.Clamp01(w2); w3 = Mathf.Clamp01(w3);
                float sum = w0 + w1 + w2 + w3;
                if (sum <= 1e-6f) { w0 = 1f; w1 = w2 = w3 = 0f; }
                else if (Mathf.Abs(sum - 1f) > 1e-3f) { w0 /= sum; w1 /= sum; w2 /= sum; w3 /= sum; }
                bWt[i * 4] = w0; bWt[i * 4 + 1] = w1; bWt[i * 4 + 2] = w2; bWt[i * 4 + 3] = w3;
                // bone bytes -> HRC via this vertex's range palette (or main)
                int[] map = mainMap;
                if (ranges.Count > 0)
                {
                    for (int ri = 0; ri < ranges.Count; ri++)
                        if (i >= ranges[ri].VStart && i < ranges[ri].VStart + ranges[ri].VCount) { if (ranges[ri].PalHrc != null) map = ranges[ri].PalHrc; break; }
                }
                for (int k = 0; k < 4; k++)
                {
                    int local = stride == 44 ? (k == 0 ? d[b + 16] : 0) : d[b + boneOff + k];
                    int hrc = (map != null && local >= 0 && local < map.Length) ? map[local] : 0;
                    bHrc[i * 4 + k] = hrc < 0 ? 0 : hrc;
                }
            }
            // NO winding reversal — D3D9 & Unity share LH front-face winding, and we no longer X-flip the verts.

            var mesh = new Mesh { name = "msh" };
            if (vcount > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts; mesh.uv = uvs; mesh.colors32 = cols;   // colors32 = baked vertex lighting (white when none)
            // split into one Unity submesh per material range so each can take its own texture (faithful to the
            // D3DXATTRIBUTERANGE table — SceneLoader does the same). Single-range meshes use one submesh as before.
            if (ranges.Count > 1)
            {
                mesh.subMeshCount = ranges.Count;
                for (int s = 0; s < ranges.Count; s++)
                {
                    var r = ranges[s]; var sub = new int[r.FCount * 3];
                    for (int f = 0; f < r.FCount; f++)
                    { int o = (r.FStart + f) * 3, w = f * 3; sub[w] = tris[o]; sub[w + 1] = tris[o + 1]; sub[w + 2] = tris[o + 2]; }
                    mesh.SetTriangles(sub, s);
                }
            }
            else mesh.triangles = tris;
            mesh.RecalculateBounds();

            // MSH inverse-bind matrices (Unity space), keyed by HRC bone, for retarget skinning (Fix: arms shear
            // under MOT with the hrc_bind fallback). slotInv[i] is the palette slot's inv-bind; pal[i] -> HRC bone.
            Dictionary<int, Matrix4x4> mshInv = null;
            if (pal != null && slotInv != null)
            {
                mshInv = new Dictionary<int, Matrix4x4>();
                for (int i = 0; i < pal.Length && i < slotInv.Length; i++) { int hrc = pal[i]; if (hrc >= 0) mshInv[hrc] = slotInv[i]; }
            }
            var rangeList = new List<(int, int, int)>();
            foreach (var r in ranges) rangeList.Add((r.Attrib, r.FStart, r.FCount));
            return new SubMesh
            {
                Mesh = mesh, Dds = dds, DdsNames = ddsNames.ToArray(), Ranges = rangeList,
                // Only mark a submesh skinnable when it actually carries per-vertex bone weights (stride 44/48/52 ->
                // nW>0). Rigid stage props (fvf 0x112/0x142, stride 32/24 — corals, the FIFA crowd, sea/TV screens)
                // have NO weights: their "bone index" bytes alias the normal/diffuse, so skinning them at the bind
                // pose collapses the mesh to the origin (SCN0014 corals "lay flat at centre"). With BoneHrc=null the
                // loader renders them VERBATIM (their MSH verts are already the final world geometry). Avatars and
                // genuinely-deforming props (GUATAN, stride 44) keep weights -> still skinned.
                BindVerts = (Vector3[])verts.Clone(), BoneHrc = (pal != null && nW > 0) ? bHrc : null, BoneWt = bWt,
                MshInvBindByHrc = mshInv
            };
        }

        // returns local_to_hrc array (palette_size) or null; sets palStart/palEnd; outputs per-slot inv-bind (Unity)
        private static int[] ScanBonePalette(byte[] d, int start, out int palStart, out int palEnd, out Matrix4x4[] slotInv)
        {
            palStart = -1; palEnd = -1; slotInv = null;
            int limit = System.Math.Min(d.Length - 8, start + 0x4000);
            for (int off = start; off + 8 <= limit; off += 4)
            {
                int hrcCount = (int)U32At(d, off), palSize = (int)U32At(d, off + 4);
                if (hrcCount < 1 || hrcCount > 512 || palSize < 1 || palSize > 128) continue;
                int matOff = off + 8, mapOff = matOff + palSize * 64, mapEnd = mapOff + hrcCount * 4;
                if (mapEnd > d.Length) continue;
                if (!LooksLikeMatrix(d, matOff)) continue;
                int valid = 0;
                for (int i = 0; i < hrcCount; i++) { uint v = U32At(d, mapOff + i * 4); if (v == 0xFFFFFFFF || v < (uint)palSize) valid++; }
                if (valid < hrcCount - 1) continue;
                var localToHrc = new int[palSize]; for (int i = 0; i < palSize; i++) localToHrc[i] = -1;
                for (int i = 0; i < hrcCount; i++) { uint v = U32At(d, mapOff + i * 4); if (v != 0xFFFFFFFF && v < (uint)palSize) localToHrc[v] = i; }
                bool full = true; for (int i = 0; i < palSize; i++) if (localToHrc[i] < 0) { full = false; break; }
                if (!full) continue;
                // per-slot 4x4 inv-bind (row-major D3D), converted to Unity column-vector space (same X*Rᵀ*X as HRC)
                slotInv = new Matrix4x4[palSize];
                for (int i = 0; i < palSize; i++)
                {
                    var R = new Matrix4x4();
                    for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) R[r, c] = F(d, matOff + i * 64 + (r * 4 + c) * 4);
                    slotInv[i] = HrcLoader.ToUnityLocal(R);
                }
                palStart = off; palEnd = mapEnd; return localToHrc;
            }
            return null;
        }

        private static List<Range> ScanRanges(byte[] d, int searchFrom, int vertTotal, int triTotal, int numMat, out int tableEnd)
        {
            tableEnd = searchFrom;
            int searchTo = System.Math.Min(d.Length, searchFrom + 0x300);
            for (int baseOff = searchFrom; baseOff < searchTo - 8; baseOff += 4)
            {
                int cnt = (int)U32At(d, baseOff);
                if (cnt <= 0 || cnt > 32) continue;
                int entOff = baseOff + 4; if (entOff + cnt * 24 > d.Length) continue;
                var tmp = new List<Range>(); bool ok = true; int faceSum = 0, prevFs = -1;
                for (int i = 0; i < cnt; i++)
                {
                    int o = entOff + i * 24;
                    int a = (int)U32At(d, o), fs = (int)U32At(d, o + 4), fc = (int)U32At(d, o + 8), vs = (int)U32At(d, o + 12), vc = (int)U32At(d, o + 16);
                    if (fc == 0 || fs < prevFs || fs + fc > triTotal || vs + vc > vertTotal) { ok = false; break; }
                    prevFs = fs; faceSum += fc; tmp.Add(new Range { Attrib = a, FStart = fs, FCount = fc, VStart = vs, VCount = vc });
                }
                if (!ok || faceSum != triTotal) continue;
                bool attrOk = true; foreach (var r in tmp) if (r.Attrib >= numMat) { attrOk = false; break; }
                if (!attrOk) continue;
                tableEnd = entOff + cnt * 24; return tmp;
            }
            return new List<Range>();
        }

        private static bool LooksLikeMatrix(byte[] d, int off)
        {
            if (off + 64 > d.Length) return false;
            for (int i = 0; i < 12; i++) { float v = F(d, off + i * 4); if (float.IsNaN(v) || Mathf.Abs(v) > 4f) return false; }
            if (Mathf.Abs(F(d, off + 48)) > 5000 || Mathf.Abs(F(d, off + 52)) > 5000 || Mathf.Abs(F(d, off + 56)) > 5000) return false;
            float w = F(d, off + 60); return w > 0.99f && w < 1.01f;
        }

        // Submesh-header marker (first u32). Avatar/character meshes use 0x1158/115A/115C; rigid stage props use the
        // smaller FVFs 0x112 / 0x142 / 0x152 (e.g. FIFA_QIUBEI is two 0x142 submeshes — the trophy's ball + cup). The
        // opt==101 tag plus the structural check below (a sane vert section right after the index data) make a false
        // mid-data match effectively impossible, so widening this list is safe for the avatar meshes too.
        private static readonly uint[] FvfStrides = { 0x1158, 0x115A, 0x115C, 0x112, 0x142, 0x152 };
        private static int ScanNextSubmesh(byte[] d, int start)
        {
            for (int off = start; off + 12 <= d.Length; off += 4)
            {
                uint fvf = U32At(d, off);
                bool valid = false; foreach (var f in FvfStrides) if (fvf == f) { valid = true; break; }
                if (!valid) continue;
                int idx = (int)U32At(d, off + 4), opt = (int)U32At(d, off + 8);
                if (idx <= 0 || idx >= 10000000 || (idx & 1) != 0 || opt != 101) continue;
                // structural sanity: a real submesh's vertex section (vertSize, stride) follows the index data
                int vp = off + 12 + idx;
                if (vp + 8 > d.Length) continue;
                int vsz = (int)U32At(d, vp), str = (int)U32At(d, vp + 4);
                if (str >= 16 && str <= 64 && vsz > 0 && vsz % str == 0) return off;
            }
            return d.Length;
        }

        private static string TryMaterialName(byte[] d, int entryStart)
        {
            int nameOff = entryStart + 17 * 4; int end = nameOff;
            while (end < d.Length && end < entryStart + 0x65 * 4 && d[end] != 0) end++;
            if (end <= nameOff || end >= entryStart + 0x65 * 4) return null;
            string s = System.Text.Encoding.ASCII.GetString(d, nameOff, end - nameOff);
            return s.ToLowerInvariant().Contains(".dds") ? s : null;
        }

        private static uint U32(byte[] d, ref int p) { uint v = U32At(d, p); p += 4; return v; }
        private static uint U32At(byte[] d, int p) => (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24));
        private static float F(byte[] d, int o) => System.BitConverter.ToSingle(d, o);
        private static string ReadCStr(byte[] d, int o, int max) { int n = 0; while (n < max && o + n < d.Length && d[o + n] != 0) n++; return System.Text.Encoding.ASCII.GetString(d, o, n); }
    }
}
