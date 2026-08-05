using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// Unit tests for <see cref="MmdHeadBounds"/> — finding the HEAD on an MMD model so the head-portrait cameras (the
    /// room 頭貼 and the 結算 left headshot) can frame it. The SDO avatar locates its head by renderer name (FACE/HAIR);
    /// an MMD model is one skinned mesh, so the head is found by skinning: the 頭 bone's subtree, minus the hair that
    /// hangs BELOW the chin (a twintail reaching the waist would otherwise blow the head AABB up to a whole body).
    ///
    /// The fixture is a synthetic PMX built in memory (a head bone with a skull, a twintail chain hanging off it, and a
    /// body), so the tests never depend on the git-ignored Miku asset.
    /// </summary>
    public class MmdHeadBoundsTests
    {
        // bone layout: 0 センター(body) → 1 首 → 2 頭 → 3 twintail → 4 twintail tip
        private const int Center = 0, Neck = 1, Head = 2, Tail = 3, TailTip = 4;
        private static readonly Vector3 HeadPos = new Vector3(0f, 10f, 0f);

        /// <summary>把每個頂點各自包成一個單頂點「三角形」的材質,好讓 <see cref="MmdHeadBounds.VisibleNonHairVertices"/>
        /// 有東西可以對照:<paramref name="mats"/> 逐項 = (材質名, 這個材質吃掉幾個頂點, diffuse alpha),照順序切
        /// <see cref="PmxLoader.Indices"/>。</summary>
        private static void Paint(PmxLoader p, params (string name, int count, float alpha)[] mats)
        {
            p.Indices = new int[p.Positions.Length];
            for (int v = 0; v < p.Positions.Length; v++) p.Indices[v] = v;
            int at = 0;
            foreach (var m in mats)
            {
                p.Materials.Add(new PmxLoader.Material
                {
                    NameJp = m.name,
                    Diffuse = new Color(1f, 1f, 1f, m.alpha),
                    IndexStart = at,
                    IndexCount = m.count,
                });
                at += m.count;
            }
        }

        private static PmxLoader BuildModel(params (Vector3 pos, int bone)[] verts)
        {
            var p = new PmxLoader();
            p.Bones = new List<PmxLoader.Bone>
            {
                new PmxLoader.Bone { NameJp = "センター", Parent = -1,     Position = new Vector3(0f, 5f, 0f) },
                new PmxLoader.Bone { NameJp = "首",       Parent = Center, Position = new Vector3(0f, 9f, 0f) },
                new PmxLoader.Bone { NameJp = "頭",       Parent = Neck,   Position = HeadPos },
                new PmxLoader.Bone { NameJp = "髪1",      Parent = Head,   Position = new Vector3(1f, 11f, 0f) },
                new PmxLoader.Bone { NameJp = "髪2",      Parent = Tail,   Position = new Vector3(1f, 4f, 0f) },
            };
            p.Positions = new Vector3[verts.Length];
            p.BoneIdx = new int[verts.Length * 4];
            p.BoneWt = new float[verts.Length * 4];
            for (int v = 0; v < verts.Length; v++)
            {
                p.Positions[v] = verts[v].pos;
                p.BoneIdx[v * 4] = verts[v].bone; p.BoneWt[v * 4] = 1f;
                for (int k = 1; k < 4; k++) p.BoneIdx[v * 4 + k] = -1;
            }
            return p;
        }

        [Test]
        public void FindBone_LocatesHeadByJapaneseName()
        {
            var pmx = BuildModel((HeadPos, Head));
            Assert.AreEqual(Head, MmdHeadBounds.FindBone(pmx.Bones, MmdHeadBounds.HeadBoneJp));
            Assert.AreEqual(-1, MmdHeadBounds.FindBone(pmx.Bones, "存在しない"));
        }

        [Test]
        public void Subtree_IsHeadPlusItsHairChain_NotTheBody()
        {
            var parent = new[] { -1, Center, Neck, Head, Tail };
            var set = MmdHeadBounds.Subtree(parent, Head);
            Assert.IsTrue(set[Head] && set[Tail] && set[TailTip], "head + hair chain are in the subtree");
            Assert.IsFalse(set[Center] || set[Neck], "body/neck are not");
        }

        [Test]
        public void Subtree_CyclicHierarchyDoesNotHang()
        {
            var set = MmdHeadBounds.Subtree(new[] { 1, 0 }, 0);   // 0↔1 cycle
            Assert.IsTrue(set[0]);
        }

        [Test]
        public void DominantBone_PicksTheHeaviestSlot()
        {
            var idx = new[] { 7, 3, -1, -1 };
            var wt = new[] { 0.3f, 0.7f, 0f, 0f };
            Assert.AreEqual(3, MmdHeadBounds.DominantBone(idx, wt, 0));
        }

        [Test]
        public void TryCompute_MeasuresTheHeadItself_NotTheHairHangingOffIt()
        {
            var pmx = BuildModel(
                (new Vector3(0f, 14f, 0f), Head),        // hair cap / crown, rigid on the head bone
                (new Vector3(-2f, 11f, 1f), Head),       // skull
                (new Vector3(2f, 9f, -1f), Head),        // jaw, just under the bone
                (new Vector3(3f, 9.5f, 0f), Tail),       // twintail root — a HAIR bone, not the head
                (new Vector3(4f, 2f, 0f), TailTip),      // twintail tip at the waist
                (new Vector3(0f, 5f, 0f), Center));      // body

            Assert.IsTrue(MmdHeadBounds.TryCompute(pmx, out int head, out Bounds b));
            Assert.AreEqual(Head, head);

            // bounds are HEAD-BONE-LOCAL (p − headPos), so the head bone itself is y = 0.
            Assert.AreEqual(4f, b.max.y, 1e-4f, "top of the head");
            Assert.AreEqual(-1f, b.min.y, 1e-4f, "the jaw — not the twintail tip 8 units below the bone");
            Assert.AreEqual(2f, b.max.x, 1e-4f, "the skull's own width — the twintail bones are NOT the head");
            Assert.AreEqual(-2f, b.min.x, 1e-4f);
            Assert.Less(b.size.y, 6f, "head-sized, not body-sized");
        }

        [Test]
        public void TryCompute_FallsBackToTheSubtree_WhenNothingIsSkinnedToTheHeadBoneItself()
        {
            // a model that hangs ALL its head geometry off child bones → the head-proper set is empty, so the clipped
            // subtree is used instead (and the twintail tip at the waist is still dropped).
            var pmx = BuildModel(
                (new Vector3(0f, 14f, 0f), Tail),        // head geometry, but skinned to a child bone
                (new Vector3(-2f, 11f, 1f), Tail),
                (new Vector3(3f, 9.5f, 0f), Tail),
                (new Vector3(4f, 2f, 0f), TailTip),      // hanging past the chin → DROPPED by the clip
                (new Vector3(0f, 5f, 0f), Center));

            Assert.IsTrue(MmdHeadBounds.TryCompute(pmx, out int head, out Bounds b));
            Assert.AreEqual(Head, head);
            Assert.AreEqual(4f, b.max.y, 1e-4f);
            Assert.AreEqual(-0.5f, b.min.y, 1e-4f, "clipped at 0.45×4 below the bone — the waist-level vertex is gone");
        }

        // ---- 頭髮不算在頭的大小裡 ---------------------------------------------------------------------------------
        // 髮皮/前髪就長在頭骨上,skinning 分不出來,所以要靠材質名字剔掉。份量每包模型差很多(Ika +21%、YYB +11%),
        // 算進去的話同一個角色在不同模型底下頭一大一小,結算那排也跟旁邊 SDO 的頭貼對不上。

        /// <summary>頭骨上的四個頂點:臉(−1..+2)、貼在顱骨上的髮皮(+4)、被變形藏起來的重複髮片(+5)、身體。</summary>
        private static PmxLoader BuildHairyHead(string hairMatName = "前髪")
        {
            var pmx = BuildModel(
                (new Vector3(-2f, 12f, 1f), Head),       // 臉/顱骨 頂
                (new Vector3(2f, 9.5f, -1f), Head),      // 下巴,略低於頭骨
                (new Vector3(0f, 14f, 0f), Head),        // 髮皮 —— 也綁在頭骨上
                (new Vector3(0f, 15f, 0f), Head),        // alpha=0 的重複髮片(根本沒畫出來)
                (new Vector3(0f, 5f, 0f), Center));      // 身體
            Paint(pmx, ("顔", 2, 1f), (hairMatName, 1, 1f), (hairMatName + "+", 1, 0f), ("body", 1, 1f));
            return pmx;
        }

        [Test]
        public void IsHairMaterial_RecognisesJapaneseChineseAndEnglishNames()
        {
            foreach (var n in new[] { "前髪", "後髪==========", "鬓髪", "髪辫", "ヘアアクセサリー", "头发", "Hair01", "HAIRSHADOW", "帽子" })
                Assert.IsTrue(MmdHeadBounds.IsHairMaterial(n, null), n + " 應該算頭髮");
            foreach (var n in new[] { "顔", "脸颊==========", "face01", "まつげ", "眉毛", "Hatsune body", "口腔" })
                Assert.IsFalse(MmdHeadBounds.IsHairMaterial(n, null), n + " 不是頭髮");
            Assert.IsTrue(MmdHeadBounds.IsHairMaterial("マテリアル1", "Hair02"), "英文名認出來也算");
        }

        [Test]
        public void TryCompute_ExcludesHairAndHiddenMaterialsFromTheHeadBox()
        {
            Assert.IsTrue(MmdHeadBounds.TryCompute(BuildHairyHead(), out _, out Bounds b));
            Assert.AreEqual(2f, b.max.y, 1e-4f, "顱頂 —— 髮皮(+4)與藏起來的髮片(+5)都不算");
            Assert.AreEqual(-0.5f, b.min.y, 1e-4f, "下巴");
        }

        [Test]
        public void TryCompute_KeepsTheHair_WhenTheModelHasNoMaterialTableToTellItApart()
        {
            var pmx = BuildHairyHead();
            pmx.Materials.Clear(); pmx.Indices = null;   // 合成/解析不全的模型
            Assert.IsTrue(MmdHeadBounds.TryCompute(pmx, out _, out Bounds b));
            Assert.AreEqual(5f, b.max.y, 1e-4f, "認不出頭髮 → 量法跟以前一樣,不會更糟");
        }

        [Test]
        public void TryCompute_FallsBackToTheHairyBox_WhenStrippingHairWouldEatTheFace()
        {
            // 臉的材質名字裡帶「髪」(例:整顆頭跟頭髮共用一個材質)→ 剔完只剩零星頂點,框塌掉 → 整塊退回去。
            var pmx = BuildModel(
                (new Vector3(-2f, 12f, 1f), Head),
                (new Vector3(2f, 9.5f, -1f), Head),
                (new Vector3(0f, 14f, 0f), Head),
                (new Vector3(0f, 10.1f, 0f), Head),      // 唯一的非頭髮頂點 → 剔完框幾乎是零
                (new Vector3(0f, 5f, 0f), Center));
            Paint(pmx, ("髪と顔", 3, 1f), ("鼻", 1, 1f), ("body", 1, 1f));

            Assert.IsTrue(MmdHeadBounds.TryCompute(pmx, out _, out Bounds b));
            Assert.AreEqual(4f, b.max.y, 1e-4f, "剔到只剩 MinSkullFrac 以下 → 退回含髮的量法");
            Assert.AreEqual(-0.5f, b.min.y, 1e-4f);
        }

        [Test]
        public void TryCompute_HairExclusionAlsoAppliesToTheSubtreeFallback()
        {
            // 頭幾何全掛在子骨上(頭骨本身沒東西)→ 走 subtree 那條;那裡同樣要剔掉頭髮。
            var pmx = BuildModel(
                (new Vector3(-2f, 12f, 1f), Tail),       // 臉
                (new Vector3(2f, 9.5f, -1f), Tail),
                (new Vector3(0f, 14f, 0f), Tail),        // 頭髮
                (new Vector3(0f, 5f, 0f), Center));
            Paint(pmx, ("顔", 2, 1f), ("後髪", 1, 1f), ("body", 1, 1f));

            Assert.IsTrue(MmdHeadBounds.TryCompute(pmx, out _, out Bounds b));
            Assert.AreEqual(2f, b.max.y, 1e-4f, "subtree fallback 也不含頭髮");
        }

        [Test]
        public void VisibleNonHairVertices_IsNull_WhenEveryMaterialLooksLikeHair()
        {
            var pmx = BuildHairyHead();
            pmx.Materials.Clear();
            Paint(pmx, ("髪", 5, 1f));
            Assert.IsNull(MmdHeadBounds.VisibleNonHairVertices(pmx), "全被剔光 → 當成認不出來,不過濾");
        }

        [Test]
        public void TryCompute_FailsCleanly_WhenThereIsNoHeadBoneOrNoHeadGeometry()
        {
            var noHead = BuildModel((new Vector3(0f, 5f, 0f), Center));
            noHead.Bones[Head].NameJp = "atama";   // not the MMD standard name
            Assert.IsFalse(MmdHeadBounds.TryCompute(noHead, out _, out _), "no 頭 bone → caller keeps its own framing");

            var bodyOnly = BuildModel((new Vector3(0f, 5f, 0f), Center));
            Assert.IsFalse(MmdHeadBounds.TryCompute(bodyOnly, out _, out _), "nothing skinned to the head");
        }
    }
}
