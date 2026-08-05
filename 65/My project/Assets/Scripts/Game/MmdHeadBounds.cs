using System;
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
    ///
    /// **不含頭髮**(2026-08-05):skinning 抓出來的那一塊還是帶著「貼在顱骨上的髮皮 + 前髪/鬢髪」—— 它們就長在
    /// 頭骨上,不歸子骨管。頭髮的份量**每個模型差很多**,所以連帶讓頭貼的取景每個模型都不一樣:
    /// <list type="bullet">
    /// <item>Ika 初音:純頭 2.576、含髮 3.119(髮多出 <b>21%</b>)</item>
    /// <item>YYB 初音:純頭 2.545、含髮 2.818(髮多出 <b>11%</b>)</item>
    /// </list>
    /// 兩具模型的**純頭**只差 1.2%(身高對齊之後都是 ~9.0 SDO 單位),含髮卻差 8.8% —— 頭貼相機的距離是
    /// 「框高 × <see cref="MmdAvatar.PortraitFrameDist"/>」,於是同一個角色在兩包模型底下頭一大一小,跟旁邊那幾格
    /// SDO 頭貼也對不上(使用者回報:「結算畫面 MMD 的人左邊大頭貼跟其他人不一致」)。實測:結算頭貼裡臉只佔畫面
    /// 高的 45.3%(Ika)/ 49.5%(YYB),而 <see cref="MmdAvatar.PortraitFrameDist"/> 這組常數本來就是**照沒有頭髮的
    /// 頭**訂的(54.9%)—— 常數沒錯,錯的是餵給它的框。
    ///
    /// 所以量之前先把**頭髮材質**整片剔掉(<see cref="IsHairMaterial"/>),連同 alpha=0 的隱藏材質(那是模型自己
    /// 用變形隱藏的重複髮片,根本沒畫出來)。剔完兩具模型都回到 54.9%。認不出頭髮的模型(材質沒有可辨識的名字、
    /// 或整顆頭跟頭髮共用一個材質)量到的就跟以前一樣,不會更糟;剔到只剩一點點時 <see cref="MinSkullFrac"/> 會
    /// 把整塊退回去。
    /// </summary>
    public static class MmdHeadBounds
    {
        public const string HeadBoneJp = "頭";

        /// <summary>材質名字裡出現這些字就當**頭髮**(含髮飾),不計入頭的大小。日文/簡繁中文/英文都要認得,因為
        /// 模型包來自哪裡都有:Ika 是 <c>前髪 / 後髪 / 鬓髪 / 髪辫 / ヘアアクセサリー</c>,YYB 是
        /// <c>Hair01 / Hair02 / Hairshadow</c>。比對不分大小寫。
        ///
        /// 刻意**不**收「hat / cap / accessory」這類字:初音的模型名字本身就有 <c>Hatsune</c>(含 "hat"),
        /// 收了會把臉整片誤殺。<c>帽子</c>(日文/中文的帽子)沒有這個問題,可以收。</summary>
        public static readonly string[] HairNameKeys = { "髪", "髮", "发", "ヘア", "ﾍｱ", "hair", "帽子" };

        /// <summary>材質 diffuse alpha 低於這個值 = 模型自己用變形藏起來的(重複髮片 / 隱藏身體 / 眼球 sphere),
        /// 根本沒畫出來 → 不能算進頭的大小。與 <c>MmdAvatar.BuildMaterials</c> 的隱藏門檻同一個值。</summary>
        public const float HiddenAlpha = 0.05f;

        /// <summary>剔掉頭髮之後的框至少要有原本(含髮)框的這個比例,否則就當「認錯了」整塊退回含髮的量法。
        /// 防的是把臉本身誤判成頭髮的模型(例如臉的材質叫「髪と顔」)—— 真的只是拿掉頭髮的話,實測兩具模型分別
        /// 剩 0.83 / 0.90,離 0.35 很遠。</summary>
        public const float MinSkullFrac = 0.35f;

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

        /// <summary>這個材質是頭髮(或髮飾)嗎 —— 名字含 <see cref="HairNameKeys"/> 任一個字(不分大小寫)。
        /// 日文名與英文名任一個中就算。</summary>
        public static bool IsHairMaterial(string nameJp, string nameEn) => HasHairKey(nameJp) || HasHairKey(nameEn);

        private static bool HasHairKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < HairNameKeys.Length; i++)
                if (s.IndexOf(HairNameKeys[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>每個頂點「算不算進頭的大小」的遮罩:被**畫得出來且不是頭髮**的材質用到的頂點才算 true。
        /// 材質表或索引缺席(合成測試模型 / 解析不完整)回 <c>null</c> ＝ 不過濾,量法跟以前一樣。
        ///
        /// 用「哪個材質畫到它」而不是「它屬於哪個材質」:PMX 的材質是一段連續的索引範圍,同一個頂點可能被相鄰兩
        /// 個材質共用(接縫),只要還有一個非頭髮材質畫到它,它就仍是頭的一部分。</summary>
        public static bool[] VisibleNonHairVertices(PmxLoader pmx)
        {
            if (pmx == null || pmx.Materials == null || pmx.Materials.Count == 0 ||
                pmx.Indices == null || pmx.VertexCount == 0) return null;
            var keep = new bool[pmx.VertexCount];
            bool any = false;
            foreach (var m in pmx.Materials)
            {
                if (m == null || m.Diffuse.a < HiddenAlpha || IsHairMaterial(m.NameJp, m.NameEn)) continue;
                int end = Mathf.Min(m.IndexStart + m.IndexCount, pmx.Indices.Length);
                for (int i = Mathf.Max(0, m.IndexStart); i < end; i++)
                {
                    int v = pmx.Indices[i];
                    if (v < 0 || v >= keep.Length) continue;
                    keep[v] = true; any = true;
                }
            }
            return any ? keep : null;   // 全被剔光 = 這個模型的名字認不得 → 不過濾
        }

        /// <summary>AABB of the head geometry in the head bone's REST-LOCAL space (i.e. model-space offsets from
        /// <paramref name="headPos"/>; MMD rest bones carry no rotation, so this is just p − headPos). False if no
        /// vertex is skinned to the head subtree.</summary>
        public static bool TryLocalBounds(Vector3[] positions, int[] boneIdx, float[] boneWt, bool[] inHead, Vector3 headPos, out Bounds local)
            => TryLocalBounds(positions, boneIdx, boneWt, inHead, null, headPos, out local);

        /// <summary>同上,外加逐頂點的遮罩 <paramref name="keep"/>(<c>null</c> ＝ 全收):
        /// <see cref="VisibleNonHairVertices"/> 用它把頭髮/隱藏材質擋在框外。</summary>
        public static bool TryLocalBounds(Vector3[] positions, int[] boneIdx, float[] boneWt, bool[] inHead,
                                          bool[] keep, Vector3 headPos, out Bounds local)
        {
            local = default;
            if (positions == null || inHead == null) return false;

            // pass 1: the top of the head — the head slab's height reference.
            float top = float.NegativeInfinity; bool any = false;
            for (int v = 0; v < positions.Length; v++)
            {
                if (!InHead(boneIdx, boneWt, inHead, keep, v)) continue;
                if (positions[v].y > top) top = positions[v].y;
                any = true;
            }
            if (!any) return false;

            // pass 2: keep chin-upward, drop what hangs below it (a twintail skinned into the set would otherwise blow
            // the AABB up to a body — matters for the subtree fallback, where the hair bones ARE the head geometry).
            float headH = top - headPos.y;
            float cut = headH > 1e-4f ? headPos.y - KeepBelowFrac * headH : float.NegativeInfinity;
            bool got = false;
            for (int v = 0; v < positions.Length; v++)
            {
                if (!InHead(boneIdx, boneWt, inHead, keep, v)) continue;
                var p = positions[v];
                if (p.y < cut) continue;
                var off = p - headPos;
                if (!got) { local = new Bounds(off, Vector3.zero); got = true; }
                else local.Encapsulate(off);
            }
            return got;
        }

        private static bool InHead(int[] boneIdx, float[] boneWt, bool[] inHead, bool[] keep, int v)
        {
            if (keep != null && (v >= keep.Length || !keep[v])) return false;
            int b = DominantBone(boneIdx, boneWt, v);
            return b >= 0 && b < inHead.Length && inHead[b];
        }

        /// <summary>How many vertices have their dominant bone in <paramref name="set"/>.</summary>
        public static int CountDominant(int[] boneIdx, float[] boneWt, bool[] set, int vertexCount)
            => CountDominant(boneIdx, boneWt, set, null, vertexCount);

        /// <summary>同上,外加逐頂點遮罩(<c>null</c> ＝ 全收)。</summary>
        public static int CountDominant(int[] boneIdx, float[] boneWt, bool[] set, bool[] keep, int vertexCount)
        {
            int n = 0;
            for (int v = 0; v < vertexCount; v++) if (InHead(boneIdx, boneWt, set, keep, v)) n++;
            return n;
        }

        /// <summary>Locate the head bone and measure its rest-local head AABB — **頭髮不算在內**(見類別註解)。
        /// False if the model has no 頭 bone or nothing is skinned to it or its children (then the caller keeps its
        /// non-MMD framing).</summary>
        public static bool TryCompute(PmxLoader pmx, out int headBone, out Bounds headLocal)
        {
            headBone = -1; headLocal = default;
            if (pmx == null || pmx.Bones == null || pmx.VertexCount == 0) return false;
            headBone = FindBone(pmx.Bones, HeadBoneJp);
            if (headBone < 0) return false;

            var keep = VisibleNonHairVertices(pmx);
            if (keep == null) return TryMeasure(pmx, headBone, null, out headLocal);   // 認不出頭髮 → 照舊全收

            if (!TryMeasure(pmx, headBone, keep, out headLocal)) return TryMeasure(pmx, headBone, null, out headLocal);
            // 剔到只剩一小塊 = 把臉本身當成頭髮剔掉了(材質名字騙了我們)→ 整塊退回含髮的量法,寧可跟以前一樣。
            if (TryMeasure(pmx, headBone, null, out var withHair) && headLocal.size.y < MinSkullFrac * withHair.size.y)
                headLocal = withHair;
            return true;
        }

        /// <summary>頭骨那一塊的 rest-local AABB,<paramref name="keep"/> ＝ <see cref="VisibleNonHairVertices"/> 的
        /// 遮罩(<c>null</c> ＝ 全收,含頭髮)。</summary>
        private static bool TryMeasure(PmxLoader pmx, int headBone, bool[] keep, out Bounds headLocal)
        {
            headLocal = default;
            Vector3 headPos = pmx.Bones[headBone].Position;

            var parent = new int[pmx.Bones.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = pmx.Bones[i].Parent;
            var subtree = Subtree(parent, headBone);

            // the head PROPER — what is skinned straight onto the head bone (skull + face; the rigid hair cap that also
            // rides it is dropped by `keep`)
            var headOnly = new bool[pmx.Bones.Count];
            headOnly[headBone] = true;
            int nHead = CountDominant(pmx.BoneIdx, pmx.BoneWt, headOnly, keep, pmx.VertexCount);
            int nSubtree = CountDominant(pmx.BoneIdx, pmx.BoneWt, subtree, keep, pmx.VertexCount);
            if (nSubtree > 0 && nHead >= MinHeadVertexFrac * nSubtree &&
                TryLocalBounds(pmx.Positions, pmx.BoneIdx, pmx.BoneWt, headOnly, keep, headPos, out headLocal))
                return true;

            // fallback: this model hangs its head geometry off child bones → take the subtree, clipped below the chin
            return TryLocalBounds(pmx.Positions, pmx.BoneIdx, pmx.BoneWt, subtree, keep, headPos, out headLocal);
        }
    }
}
