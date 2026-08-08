using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>Unit tests for MotLoader.SampleScale / ScaleVaries — the scale track that drives the SCN0008
    /// delta_line bars' "extend" (scale.Y 0→2 over the clip). Pure logic, no Unity scene.</summary>
    public class MotLoaderTests
    {
        // a 3-keyframe node whose scale.Y ramps 0→1→2 (x/z constant 1); Scl layout = (x,y,z,time) per key.
        private static MotLoader.Node RampY()
        {
            return new MotLoader.Node
            {
                Sc = 3,
                Scl = new float[] {
                    1f, 0f, 1f, 0f,
                    1f, 1f, 1f, 15f,
                    1f, 2f, 1f, 30f,
                },
            };
        }

        [Test]
        public void SampleScale_HitsKeyframes()
        {
            var n = RampY();
            Assert.AreEqual(new Vector3(1f, 0f, 1f), MotLoader.SampleScale(n, 0f));
            Assert.AreEqual(new Vector3(1f, 1f, 1f), MotLoader.SampleScale(n, 15f));
            Assert.AreEqual(new Vector3(1f, 2f, 1f), MotLoader.SampleScale(n, 30f));
        }

        [Test]
        public void SampleScale_InterpolatesBetweenKeyframes()
        {
            var n = RampY();
            Assert.AreEqual(0.5f, MotLoader.SampleScale(n, 7.5f).y, 1e-4f);   // midway 0→1
            Assert.AreEqual(1.5f, MotLoader.SampleScale(n, 22.5f).y, 1e-4f);  // midway 1→2
        }

        [Test]
        public void SampleScale_SingleKeyframe_ReturnsThatValue()
        {
            var n = new MotLoader.Node { Sc = 1, Scl = new float[] { 1f, 3f, 1f, 0f } };
            Assert.AreEqual(new Vector3(1f, 3f, 1f), MotLoader.SampleScale(n, 99f));
        }

        // ---- 同一根骨兩份 node 的挑法 (ResolveDuplicateNodes) ----------------------------------------------------
        // 官方有 16 支 MOT 把某根骨寫了兩次 (翅膀/尾巴 _G rig 的殘骨)。挑「pos 首鍵對得上 HRC rest」那份;
        // 白色九尾狐 021089/021090 的最後一份是別的掛點殘留,照單全收會讓尾巴轉 180° 跑到臉前面。

        // 單骨 HRC:rest 平移 = (x,y,z)。檔案是 row-major/row-vector,平移在第 4 列。
        private static byte[] OneBoneHrc(float x, float y, float z)
        {
            var b = new byte[16 + 112];
            System.Text.Encoding.ASCII.GetBytes("Hierachy0020").CopyTo(b, 0);
            System.BitConverter.GetBytes(1).CopyTo(b, 12);                       // bone_count
            float[] m = { 1, 0, 0, 0,  0, 1, 0, 0,  0, 0, 1, 0,  x, y, z, 1 };
            for (int i = 0; i < 16; i++) System.BitConverter.GetBytes(m[i]).CopyTo(b, 16 + i * 4);
            System.Text.Encoding.ASCII.GetBytes("Bip01").CopyTo(b, 16 + 84);
            return b;
        }

        // bone 0 被寫了兩份靜態 node,位移分別是 a 和 b (rot/scale 都是單位值)。
        private static byte[] TwoRootNodesMot(Vector3 a, Vector3 b)
        {
            var buf = new System.Collections.Generic.List<byte>();
            buf.AddRange(System.Text.Encoding.ASCII.GetBytes("Animation0017"));
            while (buf.Count < 16) buf.Add(0);
            void F(float v) => buf.AddRange(System.BitConverter.GetBytes(v));
            void I(int v) => buf.AddRange(System.BitConverter.GetBytes(v));
            void Node(Vector3 p)
            {
                I(0); I(0); I(1); I(1); I(1);                      // bone_id, flag, rot/scale/pos counts
                F(0f); F(0f); F(0f); F(1f); F(0f);                 // rot key (quat + time)
                F(1f); F(1f); F(1f); F(0f);                        // scale key
                F(p.x); F(p.y); F(p.z); F(0f);                     // pos key
            }
            Node(a); Node(b);
            F(0f);                                                  // max_time footer
            return buf.ToArray();
        }

        [Test]
        public void ResolveDuplicateNodes_PicksTheNodeThatMatchesTheRest()
        {
            var rest = new Vector3(0f, 33.92f, 3.97f);              // 白色九尾狐 (021090) 的 root rest
            var stray = new Vector3(0f, 37.01f, 0f);                // 檔案最後那份殘留 —— 照後者覆寫就是它
            var hrc = HrcLoader.Load(OneBoneHrc(rest.x, rest.y, rest.z));

            var mot = MotLoader.Load(TwoRootNodesMot(rest, stray)); // 對的在前、殘留在後
            Assert.IsTrue(mot.HasDuplicateNodes);
            Assert.AreEqual(stray, MotLoader.SamplePos(mot.Bones[0], 0f), "未解析前是「後者覆寫」");
            mot.ResolveDuplicateNodes(hrc);
            Assert.AreEqual(rest, MotLoader.SamplePos(mot.Bones[0], 0f));

            var mot2 = MotLoader.Load(TwoRootNodesMot(stray, rest)); // 反過來 (14 支官方檔是這種) → 仍選對得上 rest 的
            mot2.ResolveDuplicateNodes(hrc);
            Assert.AreEqual(rest, MotLoader.SamplePos(mot2.Bones[0], 0f));
        }

        [Test]
        public void ResolveDuplicateNodes_TieKeepsTheLastNode_AndIsIdempotent()
        {
            var rest = new Vector3(0f, 45f, 2.52f);
            var hrc = HrcLoader.Load(OneBoneHrc(rest.x, rest.y, rest.z));
            var mot = MotLoader.Load(TwoRootNodesMot(rest, rest));   // 整份複製 (025924/025925) → 平手
            var last = mot.Bones[0];
            mot.ResolveDuplicateNodes(hrc);
            Assert.AreSame(last, mot.Bones[0], "平手要維持原本的「取最後一份」");
            mot.ResolveDuplicateNodes(hrc);
            Assert.AreSame(last, mot.Bones[0]);
        }

        [Test]
        public void ResolveDuplicateNodes_NoDuplicates_IsANoOp()
        {
            var hrc = HrcLoader.Load(OneBoneHrc(0f, 1f, 2f));
            var mot = MotLoader.Load(TwoRootNodesMot(new Vector3(9f, 9f, 9f), new Vector3(9f, 9f, 9f)));
            var single = MotLoader.Load(SingleNodeMot(new Vector3(9f, 9f, 9f)));
            Assert.IsFalse(single.HasDuplicateNodes);
            single.ResolveDuplicateNodes(hrc);
            Assert.AreEqual(new Vector3(9f, 9f, 9f), MotLoader.SamplePos(single.Bones[0], 0f), "沒有重複就不該動到任何軌");
            single.ResolveDuplicateNodes(null);
            Assert.IsTrue(mot.HasDuplicateNodes);
        }

        private static byte[] SingleNodeMot(Vector3 p)
        {
            var two = TwoRootNodesMot(p, p);
            // 砍掉第二份 node (每份 20 + 20 + 16 + 16 = 72 bytes),footer 保留
            var one = new byte[two.Length - 72];
            System.Array.Copy(two, 0, one, 0, 16 + 72);
            System.Array.Copy(two, two.Length - 4, one, one.Length - 4, 4);
            return one;
        }

        [Test]
        public void ScaleVaries_TrueForRamp_FalseForConstant()
        {
            Assert.IsTrue(MotLoader.ScaleVaries(RampY()));
            var flat = new MotLoader.Node
            {
                Sc = 2,
                Scl = new float[] { 1f, 1f, 1f, 0f, 1f, 1f, 1f, 30f },
            };
            Assert.IsFalse(MotLoader.ScaleVaries(flat));
            Assert.IsFalse(MotLoader.ScaleVaries(new MotLoader.Node { Sc = 1, Scl = new float[] { 1f, 1f, 1f, 0f } }));
            Assert.IsFalse(MotLoader.ScaleVaries(null));
        }
    }
}
