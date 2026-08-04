using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Where the HEAD is on an MMD model — the geometry a head-portrait camera has to frame (the room 頭貼 and the
    /// 結算 left headshot). Pure maths over the parsed <see cref="PmxLoader"/> arrays; no Unity object is touched, so
    /// this is unit-tested.
    ///
    /// The SDO avatar finds its head by renderer NAME (the "*_FACE_*" / "*_HAIR_*" parts). An MMD model is ONE skinned
    /// mesh with no such split, so the head must be found by SKINNING instead — and the right set is the vertices skinned
    /// DIRECTLY to 頭: the skull, the face and the hair cap that sits rigidly on it. That is the head a portrait frames,
    /// and it is what the SDO FACE+HAIR box means too.
    ///
    /// Taking 頭 AND ITS DESCENDANTS instead would look reasonable and is wrong: the twintails/ponytail/bangs hang off
    /// head-CHILD bones (they are the cloth-simulated ones), so on Miku that set measures 8.02 × 4.31 × 8.45 instead of
    /// the head's actual 3.68 × 3.12 × 3.11 — a box a third taller and nearly 3× deeper, pulling the camera back until
    /// the head is small and framed too high. The subtree (clipped below the chin, see <see cref="KeepBelowFrac"/>) is
    /// kept only as a FALLBACK for a model that skins nothing directly to 頭.
    /// </summary>
    public static class MmdHeadBounds
    {
        public const string HeadBoneJp = "頭";

        /// <summary>Fallback only: how far BELOW the head bone the subtree slab reaches, as a fraction of the hair height
        /// above it. The chin sits a little under the bone; the long hair hanging past that is not part of the portrait.</summary>
        public const float KeepBelowFrac = 0.45f;

        /// <summary>The head bone must carry at least this share of the head subtree's vertices for its OWN geometry to be
        /// taken as the head (Miku: 45%). Below it, the model skins the head to child bones → use the subtree fallback.</summary>
        public const float MinHeadVertexFrac = 0.1f;

        /// <summary>Index of the bone named <paramref name="nameJp"/>, or -1.</summary>
        public static int FindBone(IList<PmxLoader.Bone> bones, string nameJp)
        {
            if (bones == null) return -1;
            for (int i = 0; i < bones.Count; i++) if (bones[i] != null && bones[i].NameJp == nameJp) return i;
            return -1;
        }

        /// <summary><paramref name="root"/> and every bone under it (parent chains are walked with a hop guard, so a
        /// malformed cyclic hierarchy can't hang the caller).</summary>
        public static bool[] Subtree(int[] parent, int root)
        {
            int n = parent != null ? parent.Length : 0;
            var inSet = new bool[n];
            if (root < 0 || root >= n) return inSet;
            inSet[root] = true;
            for (int i = 0; i < n; i++)
            {
                if (inSet[i]) continue;
                int p = i, hops = 0;
                while (p >= 0 && p < n && hops++ <= n)
                {
                    if (inSet[p]) { inSet[i] = true; break; }
                    p = parent[p];
                }
            }
            return inSet;
        }

        /// <summary>The bone carrying the most weight for vertex <paramref name="v"/> (PMX packs 4 slots per vertex), or -1.</summary>
        public static int DominantBone(int[] boneIdx, float[] boneWt, int v)
        {
            if (boneIdx == null || boneWt == null) return -1;
            int o = v * 4;
            if (o < 0 || o + 3 >= boneIdx.Length || o + 3 >= boneWt.Length) return -1;
            int best = -1; float bw = 0f;
            for (int k = 0; k < 4; k++)
            {
                int b = boneIdx[o + k]; float w = boneWt[o + k];
                if (b < 0 || w <= bw) continue;
                best = b; bw = w;
            }
            return best;
        }

        /// <summary>AABB of the head geometry in the head bone's REST-LOCAL space (i.e. model-space offsets from
        /// <paramref name="headPos"/>; MMD rest bones carry no rotation, so this is just p − headPos). False if no
        /// vertex is skinned to the head subtree.</summary>
        public static bool TryLocalBounds(Vector3[] positions, int[] boneIdx, float[] boneWt, bool[] inHead, Vector3 headPos, out Bounds local)
        {
            local = default;
            if (positions == null || inHead == null) return false;

            // pass 1: the top of the hair — the head slab's height reference.
            float top = float.NegativeInfinity; bool any = false;
            for (int v = 0; v < positions.Length; v++)
            {
                int b = DominantBone(boneIdx, boneWt, v);
                if (b < 0 || b >= inHead.Length || !inHead[b]) continue;
                if (positions[v].y > top) top = positions[v].y;
                any = true;
            }
            if (!any) return false;

            // pass 2: keep chin-upward, drop the hair that hangs below it (twintails → they'd blow the AABB up to a body).
            float headH = top - headPos.y;
            float cut = headH > 1e-4f ? headPos.y - KeepBelowFrac * headH : float.NegativeInfinity;
            bool got = false;
            for (int v = 0; v < positions.Length; v++)
            {
                int b = DominantBone(boneIdx, boneWt, v);
                if (b < 0 || b >= inHead.Length || !inHead[b]) continue;
                var p = positions[v];
                if (p.y < cut) continue;
                var off = p - headPos;
                if (!got) { local = new Bounds(off, Vector3.zero); got = true; }
                else local.Encapsulate(off);
            }
            return got;
        }

        /// <summary>How many vertices have their dominant bone in <paramref name="set"/>.</summary>
        public static int CountDominant(int[] boneIdx, float[] boneWt, bool[] set, int vertexCount)
        {
            int n = 0;
            for (int v = 0; v < vertexCount; v++)
            {
                int b = DominantBone(boneIdx, boneWt, v);
                if (b >= 0 && b < set.Length && set[b]) n++;
            }
            return n;
        }

        /// <summary>Locate the head bone and measure its rest-local head AABB. False if the model has no 頭 bone or
        /// nothing is skinned to it or its children (then the caller keeps its non-MMD framing).</summary>
        public static bool TryCompute(PmxLoader pmx, out int headBone, out Bounds headLocal)
        {
            headBone = -1; headLocal = default;
            if (pmx == null || pmx.Bones == null || pmx.VertexCount == 0) return false;
            headBone = FindBone(pmx.Bones, HeadBoneJp);
            if (headBone < 0) return false;
            Vector3 headPos = pmx.Bones[headBone].Position;

            var parent = new int[pmx.Bones.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = pmx.Bones[i].Parent;
            var subtree = Subtree(parent, headBone);

            // the head PROPER — what is skinned straight onto the head bone (skull + face + rigid hair cap)
            var headOnly = new bool[pmx.Bones.Count];
            headOnly[headBone] = true;
            int nHead = CountDominant(pmx.BoneIdx, pmx.BoneWt, headOnly, pmx.VertexCount);
            int nSubtree = CountDominant(pmx.BoneIdx, pmx.BoneWt, subtree, pmx.VertexCount);
            if (nSubtree > 0 && nHead >= MinHeadVertexFrac * nSubtree &&
                TryLocalBounds(pmx.Positions, pmx.BoneIdx, pmx.BoneWt, headOnly, headPos, out headLocal))
                return true;

            // fallback: this model hangs its head geometry off child bones → take the subtree, clipped below the chin
            return TryLocalBounds(pmx.Positions, pmx.BoneIdx, pmx.BoneWt, subtree, headPos, out headLocal);
        }
    }
}
