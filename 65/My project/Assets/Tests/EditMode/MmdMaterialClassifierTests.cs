using System.IO;
using System.Reflection;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sdo.Tests
{
    public class MmdMaterialClassifierTests
    {
        [TestCase(1f, 0.26965f, 0f,       false, MmdMaterialRenderMode.Blend,  TestName = "YYB_C_IsBlend")]
        [TestCase(1f, 0.80097f, 0f,       false, MmdMaterialRenderMode.Blend,  TestName = "YYB_Q2_IsBlend")]
        [TestCase(1f, 0.25909f, 0.70597f, false, MmdMaterialRenderMode.Blend,  TestName = "YYB_Q3_IsBlend")]
        [TestCase(1f, 0.01104f, 0.03779f, false, MmdMaterialRenderMode.Cutout, TestName = "YYB_Body_IsCutout")]
        [TestCase(1f, 0f,       0f,       false, MmdMaterialRenderMode.Opaque, TestName = "YYB_Q_IsOpaque")]
        public void Classify_RealYybTextureStats(
            float authoredAlpha,
            float translucentFraction,
            float transparentFraction,
            bool doubleSided,
            MmdMaterialRenderMode expected)
        {
            Assert.AreEqual(expected, MmdMaterialClassifier.Classify(
                authoredAlpha, translucentFraction, transparentFraction, doubleSided));
        }

        [Test]
        public void AuthoredZeroAlpha_RemainsHidden_RegardlessOfTextureDistribution()
        {
            Assert.AreEqual(MmdMaterialRenderMode.Hidden,
                MmdMaterialClassifier.Classify(0f, 0.80f, 0.70f, false));
        }

        [Test]
        public void AuthoredVisibleMaterial_IsNeverHiddenByTextureDistribution()
        {
            foreach (float mid in new[] { 0f, 0.15f, 0.27f, 0.80f, 1f })
            foreach (float hole in new[] { 0f, 0.04f, 0.71f, 1f })
                Assert.AreNotEqual(MmdMaterialRenderMode.Hidden,
                    MmdMaterialClassifier.Classify(1f, mid, hole, false),
                    $"visible PMX material was hidden for mid={mid}, hole={hole}");
        }

        [Test]
        public void DoubleSidedOpaqueMaterial_StaysOpaque()
        {
            Assert.AreEqual(MmdMaterialRenderMode.Opaque,
                MmdMaterialClassifier.Classify(1f, 0f, 0f, true));
        }

        [Test]
        public void AuthoredPartialAlpha_IsBlend_EvenWithOpaqueTexture()
        {
            Assert.AreEqual(MmdMaterialRenderMode.Blend,
                MmdMaterialClassifier.Classify(0.5f, 0f, 0f, false));
        }

        [TestCase(0.0199f, 0f,    MmdMaterialRenderMode.Opaque, TestName = "BelowSoftAlphaThreshold_IsOpaque")]
        [TestCase(0.02f,   0f,    MmdMaterialRenderMode.Blend,  TestName = "SoftAlphaThreshold_IsBlend")]
        [TestCase(0.149f,  0.02f, MmdMaterialRenderMode.Cutout, TestName = "HolesWithOnlyEdgeAlpha_AreCutout")]
        [TestCase(0.15f,   0.90f, MmdMaterialRenderMode.Blend,  TestName = "BroadTranslucencyWinsOverHoles")]
        public void Classify_PinsAlphaThresholds(
            float translucentFraction,
            float transparentFraction,
            MmdMaterialRenderMode expected)
        {
            Assert.AreEqual(expected, MmdMaterialClassifier.Classify(
                1f, translucentFraction, transparentFraction, false));
        }

        [Test]
        public void RealYyb_AuthoredVisibleSubmeshesAreNotRemoved_WhenPresent()
        {
            string repo = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
            string dir = Path.Combine(repo, "assets", "MODEL", "YYB Hatsune Miku_10th");
            string path = Path.Combine(dir, "YYB Hatsune Miku_10th_v1.02.pmx");
            if (!File.Exists(path)) Assert.Ignore("YYB material fixture is not installed");

            var pmx = PmxLoader.Load(File.ReadAllBytes(path));
            Assert.IsNotNull(pmx, "YYB PMX failed to parse");
            Assert.AreEqual(31, pmx.Materials.Count, "unexpected YYB fixture version");
            MmdAvatar.Prewarm(pmx, dir);

            var cacheField = typeof(MmdAvatar).GetField("_sharedByModel", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(cacheField);
            object cache = cacheField.GetValue(null);
            object shared = cache.GetType().GetProperty("Item").GetValue(cache, new object[] { pmx });
            var sharedType = shared.GetType();
            var hidden = (bool[])sharedType.GetField("Hide").GetValue(shared);
            var mesh = (Mesh)sharedType.GetField("Mesh").GetValue(shared);
            var materials = (Material[])sharedType.GetField("Materials").GetValue(shared);

            for (int i = 0; i < pmx.Materials.Count; i++)
            {
                if (pmx.Materials[i].Diffuse.a < 0.05f) continue;
                Assert.IsFalse(hidden[i], $"authored-visible material {i} {pmx.Materials[i].NameJp} was hidden");
                Assert.AreEqual((ulong)pmx.Materials[i].IndexCount, mesh.GetIndexCount(i),
                    $"material {i} lost its submesh triangles");
            }

            foreach (int i in new[] { 11, 12, 13, 14, 23, 24, 25, 26, 27, 28, 29, 30 })
            {
                Assert.AreEqual((int)RenderQueue.Transparent, materials[i].renderQueue, $"material {i} should blend");
                Assert.AreEqual((int)BlendMode.SrcAlpha, materials[i].GetInt("_SrcBlend"));
                Assert.AreEqual((int)BlendMode.OneMinusSrcAlpha, materials[i].GetInt("_DstBlend"));
                Assert.AreEqual((int)BlendMode.One, materials[i].GetInt("_SrcBlendAlpha"));
                Assert.AreEqual((int)BlendMode.OneMinusSrcAlpha, materials[i].GetInt("_DstBlendAlpha"));
                Assert.AreEqual(0f, materials[i].GetFloat("_ZWrite"));
            }
        }

        [TestCase(MmdMaterialRenderMode.Opaque, "Opaque",            (int)RenderQueue.Geometry,    (int)BlendMode.One,      (int)BlendMode.Zero,             (int)BlendMode.One, (int)BlendMode.Zero,             1f, 0f)]
        [TestCase(MmdMaterialRenderMode.Cutout, "TransparentCutout", (int)RenderQueue.AlphaTest,   (int)BlendMode.One,      (int)BlendMode.Zero,             (int)BlendMode.One, (int)BlendMode.Zero,             1f, 1f)]
        [TestCase(MmdMaterialRenderMode.Blend,  "Transparent",       (int)RenderQueue.Transparent, (int)BlendMode.SrcAlpha, (int)BlendMode.OneMinusSrcAlpha, (int)BlendMode.One, (int)BlendMode.OneMinusSrcAlpha, 0f, 0f)]
        public void Apply_ConfiguresUnityRenderState(
            MmdMaterialRenderMode mode,
            string renderType,
            int queue,
            int srcBlend,
            int dstBlend,
            int srcBlendAlpha,
            int dstBlendAlpha,
            float zWrite,
            float alphaClip)
        {
            var shader = Shader.Find("Sdo/MmdModel");
            Assert.IsNotNull(shader, "MMD shader must be included for material-state tests");
            var material = new Material(shader);
            try
            {
                MmdMaterialClassifier.Apply(material, mode);
                Assert.AreEqual(renderType, material.GetTag("RenderType", false));
                Assert.AreEqual(queue, material.renderQueue);
                Assert.AreEqual(srcBlend, material.GetInt("_SrcBlend"));
                Assert.AreEqual(dstBlend, material.GetInt("_DstBlend"));
                Assert.AreEqual(srcBlendAlpha, material.GetInt("_SrcBlendAlpha"));
                Assert.AreEqual(dstBlendAlpha, material.GetInt("_DstBlendAlpha"));
                Assert.AreEqual(zWrite, material.GetFloat("_ZWrite"));
                Assert.AreEqual(alphaClip, material.GetFloat("_AlphaClip"));
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void Blend_OverOpaqueRenderTarget_DoesNotPunchAHoleInAlpha()
        {
            var shader = Shader.Find("Sdo/MmdModel");
            Assert.IsNotNull(shader);
            var material = new Material(shader);
            var source = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            var target = RenderTexture.GetTemporary(4, 4, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var readback = new Texture2D(4, 4, TextureFormat.RGBA32, false, true);
            var previous = RenderTexture.active;
            try
            {
                source.SetPixel(0, 0, new Color(1f, 0f, 0f, 0.5f));
                source.Apply(false, false);
                material.SetColor("_Color", Color.white);
                material.SetFloat("_Cull", 0f);
                MmdMaterialClassifier.Apply(material, MmdMaterialRenderMode.Blend);

                RenderTexture.active = target;
                GL.Clear(true, true, new Color(0f, 0f, 1f, 1f));
                Graphics.Blit(source, target, material, 0);
                readback.ReadPixels(new Rect(0, 0, 4, 4), 0, 0);
                readback.Apply(false, false);

                // With one SrcAlpha/OneMinus pair for both channels this was a²+(1-a)=0.75, and a RawImage then
                // composited the room/UI through the forehead overlay. Separate alpha factors keep opaque-underlay A=1.
                Assert.That(readback.GetPixel(2, 2).a, Is.GreaterThan(0.98f));
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(readback);
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(material);
            }
        }
    }
}
