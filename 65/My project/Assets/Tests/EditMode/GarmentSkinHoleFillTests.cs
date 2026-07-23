using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// Unit-tests <see cref="SdoAvatarBuilder.FillBackFacingSkinHoles"/> on synthetic meshes (no game data). A backless
    /// one-piece (024976 金姬兰) ships its own torso skin with a real GAP cut out of the upper-centre BACK; the see-through
    /// lace over it showed the scene straight through (使用者:「背後肩部破洞」). The fix fan-fills that interior back hole
    /// with skin while leaving the neck / waist / arm-hole openings alone.
    ///
    /// The real skin meshes DUPLICATE vertices along UV seams, so these tests build the grid as two halves that share a
    /// mid seam column at the SAME positions but DIFFERENT indices, and put the hole straddling that seam — exactly the
    /// case where the boundary only closes if edges are counted in welded (position) space (an earlier index-space walk
    /// left 024976's hole open). A regression in the weld/boundary logic reopens the hole and fails here without a render.
    /// </summary>
    public class GarmentSkinHoleFillTests
    {
        // A 5×5 conceptual grid split into a LEFT half (cols 0..2) and RIGHT half (cols 2..4). Column 2 exists in BOTH
        // halves at identical positions but distinct indices = a UV seam. `holeQuadC/R` removes one quad (−1 = none);
        // removing a seam-bordering quad makes the hole boundary straddle the seam.
        private static MshLoader.SubMesh BuildSeamedGrid(float z, int holeQuadC, int holeQuadR)
        {
            const int N = 5;
            var verts = new List<Vector3>();
            var left = new int[N, N];   // valid for cols 0..2
            var right = new int[N, N];  // valid for cols 2..4
            for (int r = 0; r < N; r++)
                for (int c = 0; c <= 2; c++) { left[c, r] = verts.Count; verts.Add(new Vector3(c - 2, r - 2, z)); }
            for (int r = 0; r < N; r++)
                for (int c = 2; c <= 4; c++) { right[c, r] = verts.Count; verts.Add(new Vector3(c - 2, r - 2, z)); }
            // left[2,*] and right[2,*] are twins: same (x=0) position, different index.

            var tris = new List<int>();
            void Quad(int[,] map, int c, int r)
            {
                int v00 = map[c, r], v10 = map[c + 1, r], v11 = map[c + 1, r + 1], v01 = map[c, r + 1];
                tris.Add(v00); tris.Add(v10); tris.Add(v11);
                tris.Add(v00); tris.Add(v11); tris.Add(v01);
            }
            for (int r = 0; r < N - 1; r++)
                for (int c = 0; c < N - 1; c++)
                {
                    if (c == holeQuadC && r == holeQuadR) continue;   // remove one quad → the hole
                    Quad(c < 2 ? left : right, c, r);
                }

            var mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            var sub = new MshLoader.SubMesh
            {
                Mesh = mesh,
                Dds = "W_Basic_Coat2.dds",
                BindVerts = verts.ToArray(),
                BoneHrc = new int[verts.Count * 4],
                BoneWt = new float[verts.Count * 4],
            };
            for (int i = 0; i < verts.Count; i++) sub.BoneWt[i * 4] = 1f;
            return sub;
        }

        [Test]
        public void BackHoleStraddlingSeam_IsFilled_AndSkinArraysStayInLockstep()
        {
            // remove the left-half quad that borders the seam (cols 1..2, rows 1..2) → the hole boundary crosses the seam
            var sub = BuildSeamedGrid(+1f, holeQuadC: 1, holeQuadR: 1);
            int before = sub.Mesh.vertexCount;
            int triBefore = sub.Mesh.triangles.Length;

            SdoAvatarBuilder.FillBackFacingSkinHoles(sub);

            Assert.Greater(sub.Mesh.vertexCount, before, "the interior back hole was filled (a fan-centre vertex added)");
            Assert.Greater(sub.Mesh.triangles.Length, triBefore, "fan triangles were added to close the hole");
            // skinning must stay parallel to the vertex buffer or CPU skinning throws / mis-indexes
            Assert.AreEqual(sub.Mesh.vertexCount, sub.BindVerts.Length, "BindVerts extended with the new vertex");
            Assert.AreEqual(sub.Mesh.vertexCount * 4, sub.BoneHrc.Length, "BoneHrc extended (4 per vertex)");
            Assert.AreEqual(sub.Mesh.vertexCount * 4, sub.BoneWt.Length, "BoneWt extended (4 per vertex)");
        }

        [Test]
        public void FrontHole_IsNotFilled()
        {
            var sub = BuildSeamedGrid(-1f, holeQuadC: 1, holeQuadR: 1);   // z<0 → front-facing, must be left open
            int before = sub.Mesh.vertexCount;

            SdoAvatarBuilder.FillBackFacingSkinHoles(sub);

            Assert.AreEqual(before, sub.Mesh.vertexCount, "a FRONT hole (mean z ≤ 0) is never filled");
        }

        [Test]
        public void SeamAlone_ProducesNoSpuriousFill()
        {
            var sub = BuildSeamedGrid(+1f, holeQuadC: -1, holeQuadR: -1);   // no hole removed — only the seam
            int before = sub.Mesh.vertexCount;

            SdoAvatarBuilder.FillBackFacingSkinHoles(sub);

            Assert.AreEqual(before, sub.Mesh.vertexCount, "a solid patch with only a UV seam has no hole to fill");
        }
    }
}
