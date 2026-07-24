using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>Tests for <see cref="MshLoader.IsScaledBindMatrix"/> — the relaxed bone-palette classifier added so
    /// avatar parts authored in a SCALED bone-local space (e.g. 025149_WOMAN_HAIR「蔚蓝舞动 女发」, whose verts sit at
    /// Y≈5 and are lifted onto the head by a ~10× bind) resolve their MSH inverse-bind instead of collapsing to the
    /// origin as a bald/invisible mesh. The classifier accepts rotation × (possibly anisotropic) scale — ORTHOGONAL
    /// columns — while rejecting skewed / coincidental structural matches.</summary>
    public class MshLoaderTests
    {
        // row-major 4x4 with the given 3x3 columns c* and translation t (w = 1).
        private static float[] M(
            float m00, float m01, float m02,
            float m10, float m11, float m12,
            float m20, float m21, float m22,
            float tx, float ty, float tz)
            => new float[] { m00, m01, m02, 0f,  m10, m11, m12, 0f,  m20, m21, m22, 0f,  tx, ty, tz, 1f };

        [Test]
        public void Identity_IsAccepted()
        {
            Assert.IsTrue(MshLoader.IsScaledBindMatrix(M(1, 0, 0,  0, 1, 0,  0, 0, 1,  0, 0, 0)));
        }

        [Test]
        public void RealScaledBoneLocalHairBind_IsAccepted()
        {
            // 025149_WOMAN_HAIR.MSH palette slot0 (verbatim from the file): an orthogonal rotation × ~9.86 uniform
            // scale with a −54.7 Y translation that lifts the tiny bone-local verts onto the head. The OLD strict test
            // rejected this (element 10.11 > 4) → hair fell back to the HRC bind and vanished. It must now be accepted.
            var m = M(
                0.000f, 10.110f, -0.000f,
                9.701f, -0.000f, -0.577f,
               -0.586f, -0.000f, -9.855f,
              -54.672f, -0.044f,  1.310f);
            Assert.IsTrue(MshLoader.IsScaledBindMatrix(m));
        }

        [Test]
        public void AnisotropicButOrthogonalScale_IsAccepted()
        {
            // rotation-free, per-axis scale (2, 5, 3) — columns orthogonal, lengths unequal → still a valid bind.
            Assert.IsTrue(MshLoader.IsScaledBindMatrix(M(2, 0, 0,  0, 5, 0,  0, 0, 3,  1, 2, 3)));
        }

        [Test]
        public void ShearedNonOrthogonalMatrix_IsRejected()
        {
            // c0=(1,0,0), c1=(0.5,1,0): |cos| = 0.5/1.118 ≈ 0.45 ≫ tolerance → not a rotation×scale (skew) → reject.
            Assert.IsFalse(MshLoader.IsScaledBindMatrix(M(1, 0.5f, 0,  0, 1, 0,  0, 0, 1,  0, 0, 0)));
        }

        [Test]
        public void NonUnitWComponent_IsRejected()
        {
            var m = M(1, 0, 0,  0, 1, 0,  0, 0, 1,  0, 0, 0);
            m[15] = 0.5f;   // a real bind always has w == 1
            Assert.IsFalse(MshLoader.IsScaledBindMatrix(m));
        }

        [Test]
        public void ZeroOrDegenerateColumn_IsRejected()
        {
            Assert.IsFalse(MshLoader.IsScaledBindMatrix(M(0, 0, 0,  0, 1, 0,  0, 0, 1,  0, 0, 0)));
        }

        [Test]
        public void GarbageAndNull_AreRejected()
        {
            Assert.IsFalse(MshLoader.IsScaledBindMatrix(null));
            Assert.IsFalse(MshLoader.IsScaledBindMatrix(new float[9]));           // too short
            Assert.IsFalse(MshLoader.IsScaledBindMatrix(M(9000, 0, 0,  0, 1, 0,  0, 0, 1,  0, 0, 0)));   // element ≫ cap
            Assert.IsFalse(MshLoader.IsScaledBindMatrix(M(1, 0, 0,  0, 1, 0,  0, 0, 1,  99999, 0, 0))); // translation ≫ cap
        }

        // ---- submesh-count header cap (MshLoader.MaxSubmeshCount) ----
        // A minimal .msh with ONE real single-bone submesh but an arbitrary declared submeshCount in the header. Load()
        // reads the count first: > MaxSubmeshCount is rejected (null); otherwise it parses the real submesh (the walk
        // self-terminates at EOF when no further submesh header is found). Layout mirrors ParseSubmesh:
        // "Mesh00000030" | u32 submeshCount | u32 fvf | u32 idxSize | u32 opt | idx | u32 vertSize | u32 stride | verts |
        //  u32 reserved[6] | u32 numMat | 408-byte material (name @ +68).
        private static byte[] BuildMshWithDeclaredCount(int declaredCount)
        {
            var b = new List<byte>();
            void U32(int v) { b.Add((byte)v); b.Add((byte)(v >> 8)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 24)); }
            b.AddRange(Encoding.ASCII.GetBytes("Mesh00000030"));
            U32(declaredCount);           // header submesh count (may over-declare the real body)
            U32(0x1156);                  // fvf (stride-40 single-bone rigid)
            U32(6);                       // idxSize (3 ushort indices)
            U32(101);                     // options
            b.AddRange(new byte[6]);      // indices
            U32(120);                     // vertSize = stride*3
            U32(40);                      // stride
            b.AddRange(new byte[120]);    // vertex block (all zero)
            for (int i = 0; i < 6; i++) U32(0);   // reserved[6]
            U32(1);                       // numMat
            int start = b.Count;
            b.AddRange(new byte[408]);    // one material record
            var raw = Encoding.ASCII.GetBytes("x.dds");
            for (int i = 0; i < raw.Length; i++) b[start + 17 * 4 + i] = raw[i];   // name @ +68 (NUL-terminated)
            return b.ToArray();
        }

        [Test]
        public void Load_HeaderDeclaringMoreThan16Submeshes_IsAccepted()
        {
            // Regression: the old cap was 16, which made Load() return null for the 綿羊雪橇 wing
            // 029157_WOMAN_CHIBANG (56 submeshes) → its shop card / room wing rendered nothing.
            var r = MshLoader.Load(BuildMshWithDeclaredCount(56));
            Assert.IsNotNull(r, "a header declaring 56 submeshes (≤ MaxSubmeshCount) must be accepted, not rejected");
            Assert.AreEqual(1, r.Submeshes.Count, "the single real submesh body is parsed; the walk stops at EOF");
        }

        [Test]
        public void Load_HeaderDeclaringExactlyMaxSubmeshCount_IsAccepted()
        {
            Assert.IsNotNull(MshLoader.Load(BuildMshWithDeclaredCount(MshLoader.MaxSubmeshCount)));
        }

        [Test]
        public void Load_HeaderDeclaringAboveCap_IsRejected()
        {
            // Above the cap stays a cheap early reject (guards a corrupt count in a non-mesh buffer sharing the magic).
            Assert.IsNull(MshLoader.Load(BuildMshWithDeclaredCount(MshLoader.MaxSubmeshCount + 1)));
        }

        [Test]
        public void Load_HeaderDeclaringZeroOrNegative_IsRejected()
        {
            Assert.IsNull(MshLoader.Load(BuildMshWithDeclaredCount(0)));
            Assert.IsNull(MshLoader.Load(BuildMshWithDeclaredCount(-1)));
        }
    }
}
