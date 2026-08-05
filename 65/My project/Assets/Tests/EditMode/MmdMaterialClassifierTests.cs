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
        // 這些是 YYB 初音**逐材質、只看它自己那塊 UV** 量出來的真實數字(見 MmdAvatar.MeasureMaterialAlpha)。
        // 拿整張 atlas 統計的舊數字全在這裡:C.png 整張 mid=0.270 → 外套/袖子那 7 個材質全被判成半透明,
        // 可是它們真正貼到的那幾塊 mid=0.000。
        [TestCase(1f, 0f,      0f,      false, MmdMaterialRenderMode.Opaque, TestName = "YYB_C03_Jacket_IsOpaque_NotBlend")]
        [TestCase(1f, 0f,      0f,      false, MmdMaterialRenderMode.Opaque, TestName = "YYB_Hair02_IsOpaque")]
        [TestCase(1f, 1f,      0f,      false, MmdMaterialRenderMode.Blend,  TestName = "YYB_Hairshadow_IsBlend")]
        [TestCase(1f, 0.893f,  0f,      false, MmdMaterialRenderMode.Blend,  TestName = "YYB_Q201_IsBlend")]
        [TestCase(1f, 0.099f,  0.042f,  false, MmdMaterialRenderMode.Cutout, TestName = "YYB_Face02_IsCutout")]
        [TestCase(1f, 0f,      0f,      false, MmdMaterialRenderMode.Opaque, TestName = "YYB_Q01_IsOpaque")]
        public void Classify_RealYybPerMaterialUvStats(
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

        // MMD 貼圖的 alpha 通道常留著一整片 225~254 的雜訊(YYB 的 C.png 有 27%,而它連一個全透明像素都沒有)。
        // 那不是作者畫的半透明,是沒清乾淨的通道 —— 0.9 以上疊起來肉眼與不透明分不出來,卻足以把整件衣服
        // 推進半透明佇列。判「半透明」的上界因此訂在 0.9,不是 0.98。
        [TestCase((byte)0,   false, true,  TestName = "FullyTransparent_IsHole")]
        [TestCase((byte)15,  false, true,  TestName = "AlmostTransparent_IsHole")]
        [TestCase((byte)16,  true,  false, TestName = "JustAboveHole_IsTranslucent")]
        [TestCase((byte)128, true,  false, TestName = "HalfAlpha_IsTranslucent")]
        [TestCase((byte)228, true,  false, TestName = "JustBelowNearOpaque_IsTranslucent")]
        [TestCase((byte)229, false, false, TestName = "NearOpaque_CountsAsOpaque")]
        [TestCase((byte)254, false, false, TestName = "AlphaChannelNoise_CountsAsOpaque")]
        [TestCase((byte)255, false, false, TestName = "FullyOpaque_CountsAsOpaque")]
        public void TexelClassification_TreatsNearOpaqueAsOpaque(byte alpha, bool translucent, bool hole)
        {
            Assert.AreEqual(translucent, MmdMaterialClassifier.IsTranslucent(alpha), $"IsTranslucent({alpha})");
            Assert.AreEqual(hole, MmdMaterialClassifier.IsHole(alpha), $"IsHole({alpha})");
            Assert.IsFalse(translucent && hole, "一個 texel 不可能同時是洞又是半透明");
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

            // ── 使用者回報的兩個畫面問題,兩個都是「太多材質被判成半透明 + 半透明不寫深度」造成的。
            //    整具身體是**一個** SkinnedMeshRenderer,Unity 不對 submesh 做距離排序:同一個 queue 就照
            //    材質順序畫。於是後面的材質永遠蓋過前面的,與誰在前誰在後無關。

            // (1)「肩膀透視會看到背後的頭髮」:外套/袖子(C01..C05, C07)真正貼到的 UV 是全不透明的,
            //     它們被整張 C.png 的 alpha 雜訊拖進了半透明佇列,於是排在後面的雙馬尾(mat 22)蓋過它們。
            foreach (int i in new[] { 11, 12, 13, 14, 23, 25 })
                Assert.AreEqual((int)RenderQueue.Geometry, materials[i].renderQueue,
                    $"material {i} '{pmx.Materials[i].NameJp}' 用到的 UV 是不透明的,不該進半透明佇列 "
                    + "(進去了 → 後面的頭髮會蓋在肩膀上)");

            // (2)「頭上一塊陰影」:Hairshadow 是作者畫的半透明陰影面(它那塊 UV 真的整片半透明),
            //     它排在瀏海(19)/側髮(20)後面 → 不寫深度就會蓋在頭髮上。
            Assert.AreEqual("Hairshadow", pmx.Materials[21].NameJp, "unexpected YYB fixture version");
            Assert.AreEqual((int)RenderQueue.Transparent, materials[21].renderQueue, "髮影本來就是半透明的");

            // 半透明一律寫深度 + 丟掉 alpha=0(MMD 固定管線就是這個狀態)。
            for (int i = 0; i < materials.Length; i++)
            {
                if (hidden[i] || materials[i].renderQueue != (int)RenderQueue.Transparent) continue;
                Assert.AreEqual((int)BlendMode.SrcAlpha, materials[i].GetInt("_SrcBlend"));
                Assert.AreEqual((int)BlendMode.OneMinusSrcAlpha, materials[i].GetInt("_DstBlend"));
                Assert.AreEqual((int)BlendMode.One, materials[i].GetInt("_SrcBlendAlpha"));
                Assert.AreEqual((int)BlendMode.OneMinusSrcAlpha, materials[i].GetInt("_DstBlendAlpha"));
                Assert.AreEqual(1f, materials[i].GetFloat("_ZWrite"),
                    $"material {i} '{pmx.Materials[i].NameJp}' 半透明也要寫深度");
                Assert.AreEqual(MmdMaterialClassifier.BlendClipCutoff, materials[i].GetFloat("_Cutoff"), 1e-6f,
                    $"material {i} 寫了深度就必須丟掉 alpha=0 的 texel");
            }
        }

        [TestCase(MmdMaterialRenderMode.Opaque, "Opaque",            (int)RenderQueue.Geometry,    (int)BlendMode.One,      (int)BlendMode.Zero,             (int)BlendMode.One, (int)BlendMode.Zero,             1f, 0f)]
        [TestCase(MmdMaterialRenderMode.Cutout, "TransparentCutout", (int)RenderQueue.AlphaTest,   (int)BlendMode.One,      (int)BlendMode.Zero,             (int)BlendMode.One, (int)BlendMode.Zero,             1f, 1f)]
        [TestCase(MmdMaterialRenderMode.Blend,  "Transparent",       (int)RenderQueue.Transparent, (int)BlendMode.SrcAlpha, (int)BlendMode.OneMinusSrcAlpha, (int)BlendMode.One, (int)BlendMode.OneMinusSrcAlpha, 1f, 1f)]
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
