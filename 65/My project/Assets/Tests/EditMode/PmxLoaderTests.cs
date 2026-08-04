using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// Unit tests for <see cref="PmxLoader"/> — the runtime MMD .pmx parser used to display an MMD model in place of
    /// the native SDO avatar. The core tests build a MINIMAL valid PMX 2.0 blob in memory (UTF-8 text, 1-byte indices)
    /// so they never depend on the (git-ignored) Miku asset; a final smoke test parses the real model when present.
    /// </summary>
    public class PmxLoaderTests
    {
        // ---- tiny PMX 2.0 writer (BinaryWriter is always little-endian, matching PMX) ----
        private static void WText(BinaryWriter w, string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            w.Write(b.Length); w.Write(b);
        }
        private static void WV3(BinaryWriter w, float x, float y, float z) { w.Write(x); w.Write(y); w.Write(z); }

        private static byte[] BuildMinimalPmx()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(new byte[] { (byte)'P', (byte)'M', (byte)'X', (byte)' ' });
                w.Write(2.0f);
                w.Write((byte)8);
                // globals: enc=1(UTF8), extraUV=0, vIdx=1, tIdx=1, mIdx=1, bIdx=1, morphIdx=1, rbIdx=1
                w.Write(new byte[] { 1, 0, 1, 1, 1, 1, 1, 1 });
                WText(w, "テスト"); WText(w, "Test");   // model name JP / EN
                WText(w, ""); WText(w, "");             // comments

                // --- 3 vertices, one per weight-deform type we support ---
                w.Write(3);
                // vert 0: BDEF1, bone 0
                WV3(w, 0, 0, 0); WV3(w, 0, 0, 1); w.Write(0f); w.Write(0f);
                w.Write((byte)0); w.Write((byte)0); w.Write(1f);              // deform, bone0, edgeScale
                // vert 1: BDEF2, bones 0/1, w=0.7
                WV3(w, 1, 0, 0); WV3(w, 0, 0, 1); w.Write(1f); w.Write(0f);
                w.Write((byte)1); w.Write((byte)0); w.Write((byte)1); w.Write(0.7f); w.Write(1f);
                // vert 2: BDEF4, bones 0,1,-1,-1  weights .5,.5,0,0
                WV3(w, 0, 1, 0); WV3(w, 0, 0, 1); w.Write(0f); w.Write(1f);
                w.Write((byte)2);
                w.Write((byte)0); w.Write((byte)1); w.Write((byte)0xFF); w.Write((byte)0xFF);   // -1 = 0xFF (sbyte)
                w.Write(0.5f); w.Write(0.5f); w.Write(0f); w.Write(0f); w.Write(1f);

                // --- faces: one triangle ---
                w.Write(3);
                w.Write((byte)0); w.Write((byte)1); w.Write((byte)2);

                // --- textures ---
                w.Write(1);
                WText(w, "tex/body.png");

                // --- materials: one, texture 0, double-sided, 3 surface indices ---
                w.Write(1);
                WText(w, "MatJP"); WText(w, "MatEN");
                w.Write(0.8f); w.Write(0.6f); w.Write(0.4f); w.Write(1f);    // diffuse RGBA
                WV3(w, 0, 0, 0);                                            // specular RGB
                w.Write(5f);                                               // specular strength
                WV3(w, 0.1f, 0.1f, 0.1f);                                  // ambient RGB
                w.Write((byte)0x01);                                       // draw flags: double-sided
                w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f);        // edge colour
                w.Write(1f);                                              // edge scale
                w.Write((byte)0);                                         // texture index 0
                w.Write((byte)0xFF);                                      // sphere index -1
                w.Write((byte)0);                                         // sphere mode
                w.Write((byte)1);                                         // toon ref = shared
                w.Write((byte)0);                                         // shared toon 0
                WText(w, "");                                             // memo
                w.Write(3);                                              // surface count

                // --- bones: root + child (indexed tail) ---
                w.Write(2);
                WText(w, "全ての親"); WText(w, "root");
                WV3(w, 0, 0, 0); w.Write((byte)0xFF); w.Write(0); w.Write((ushort)0x0000);
                WV3(w, 0, 1, 0);                                         // tail offset (flag bit0 = 0)
                WText(w, "センター"); WText(w, "center");
                WV3(w, 0, 1, 0); w.Write((byte)0); w.Write(0); w.Write((ushort)0x0001);
                w.Write((byte)0);                                        // indexed tail -> bone 0

                w.Flush();
                return ms.ToArray();
            }
        }

        [Test]
        public void Load_RejectsBadMagic()
        {
            Assert.IsNull(PmxLoader.Load(null));
            Assert.IsNull(PmxLoader.Load(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        }

        [Test]
        public void Load_ParsesHeaderAndName()
        {
            var p = PmxLoader.Load(BuildMinimalPmx());
            Assert.IsNotNull(p);
            Assert.AreEqual(2.0f, p.Version, 1e-4f);
            Assert.AreEqual("テスト", p.NameJp);
            Assert.AreEqual("Test", p.NameEn);
        }

        [Test]
        public void Load_ParsesVerticesAndWeights()
        {
            var p = PmxLoader.Load(BuildMinimalPmx());
            Assert.AreEqual(3, p.VertexCount);
            Assert.AreEqual(new Vector3(1, 0, 0), p.Positions[1]);
            Assert.AreEqual(new Vector2(1f, 0f), p.Uvs[1]);

            // BDEF1: single influence, weight 1
            Assert.AreEqual(0, p.BoneIdx[0]);
            Assert.AreEqual(1f, p.BoneWt[0], 1e-5f);
            // BDEF2: 0.7 / 0.3 across bones 0,1
            Assert.AreEqual(0.7f, p.BoneWt[4], 1e-5f);
            Assert.AreEqual(0.3f, p.BoneWt[5], 1e-5f);
            Assert.AreEqual(1, p.BoneIdx[5]);
            // BDEF4: two used + two empty slots (-1 index / 0 weight)
            Assert.AreEqual(0.5f, p.BoneWt[8], 1e-5f);
            Assert.AreEqual(0.5f, p.BoneWt[9], 1e-5f);
            Assert.AreEqual(-1, p.BoneIdx[10]);
            Assert.AreEqual(0f, p.BoneWt[10], 1e-5f);
        }

        [Test]
        public void Load_ParsesFacesTexturesMaterials()
        {
            var p = PmxLoader.Load(BuildMinimalPmx());
            Assert.AreEqual(new[] { 0, 1, 2 }, p.Indices);
            Assert.AreEqual(1, p.TexturePaths.Length);
            Assert.AreEqual("tex/body.png", p.TexturePaths[0]);

            Assert.AreEqual(1, p.Materials.Count);
            var m = p.Materials[0];
            Assert.AreEqual(0, m.TextureIndex);
            Assert.AreEqual(-1, m.SphereIndex);
            Assert.IsTrue(m.DoubleSided);
            Assert.AreEqual(0, m.IndexStart);
            Assert.AreEqual(3, m.IndexCount);
        }

        [Test]
        public void Load_ParsesBonesAndParenting()
        {
            var p = PmxLoader.Load(BuildMinimalPmx());
            Assert.AreEqual(2, p.Bones.Count);
            Assert.AreEqual("全ての親", p.Bones[0].NameJp);
            Assert.AreEqual(-1, p.Bones[0].Parent);
            Assert.AreEqual("センター", p.Bones[1].NameJp);
            Assert.AreEqual(0, p.Bones[1].Parent);
            Assert.AreEqual(new Vector3(0, 1, 0), p.Bones[1].Position);
        }

        // Smoke test against the real Miku model — parses the whole file end-to-end if it's present on disk.
        // Ignored (not failed) when the git-ignored asset isn't available (CI / a fresh checkout).
        [Test]
        public void Load_RealMikuModel_WhenPresent()
        {
            string path = FindMiku();
            if (path == null) { Assert.Ignore("Miku .pmx not found under assets/IkaHatunemiku2025"); return; }
            var p = PmxLoader.Load(File.ReadAllBytes(path));
            Assert.IsNotNull(p, "real Miku PMX failed to parse");
            Assert.Greater(p.VertexCount, 1000, "expected a real mesh");
            Assert.Greater(p.Materials.Count, 0);
            Assert.Greater(p.Bones.Count, 20, "expected a full MMD skeleton");
            // reaching bone parsing without an exception means faces/textures/materials all consumed the right byte counts
            Assert.AreEqual(p.Indices.Length % 3, 0, "index count should be a whole number of triangles");
        }

        private static string FindMiku()
        {
            try
            {
                // assets = grandparent of SdoExtracted.Root (.../assets/sdox_offline/Extracted)
                var assets = Directory.GetParent(Directory.GetParent(SdoExtracted.Root).FullName).FullName;
                var dir = Path.Combine(assets, "IkaHatunemiku2025");
                if (!Directory.Exists(dir)) return null;
                foreach (var f in Directory.GetFiles(dir, "*.pmx", SearchOption.TopDirectoryOnly)) return f;
                foreach (var f in Directory.GetFiles(dir, "*.Pmx", SearchOption.TopDirectoryOnly)) return f;
            }
            catch { }
            return null;
        }
    }
}
