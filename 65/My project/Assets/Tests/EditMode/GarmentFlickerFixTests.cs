using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>Guards the fix for 使用者回報 商城試穿「艷紅 京族新娘裝」(002178_WOMAN_ONE) 與「粉藍 印度紗衣」
    /// (002262_WOMAN_ONE) 時「褲子會一直閃爍」. Two independent roots, two pure fixes:
    ///
    ///   1. <see cref="MshLoader.PickSubmeshDds"/> — a SINGLE-range submesh must use the texture its
    ///      D3DXATTRIBUTERANGE attrib names (that table IS the engine's material assignment). The old
    ///      "first non-Basic name" guess grabbed a NEIGHBOUR texture on multi-material tables: 002262's pants
    ///      submesh (range → pant.dds, opaque DXT1) got the sheer 纱衣 coat2_ texture listed first → the pants
    ///      were mis-textured AND became a translucent renderer that fought the drape. A range attrib naming a
    ///      Basic SKIN texture is deliberately NOT honoured (211 corpus meshes would flip dressed→naked; kept on
    ///      the historical pick until visually verified).
    ///
    ///   2. <see cref="SdoAvatarBuilder.TransparentGarmentQueue"/> — Unity sorts same-queue transparent renderers
    ///      per frame by bounds distance. 002178 ships its 白褲 and 外層紗裙 as TWO submeshes (= two renderers)
    ///      sharing one translucent pant_ texture; their skinned bounds are nearly identical, so the sort order
    ///      flipped with the idle motion and the pants alternated between "painted over the skirt" and
    ///      "depth-clipped behind it" every few frames. Distinct queue values in LOAD order (file submesh order =
    ///      the official D3D9 draw order) pin the composite.</summary>
    public class GarmentFlickerFixTests
    {
        private static List<(int, int, int)> Ranges(params (int, int, int)[] r) => new List<(int, int, int)>(r);

        // --- 1. MshLoader.PickSubmeshDds: single-range attrib is authoritative ---------------------------------

        [Test]
        public void SingleRange_HonorsRangeAttribTexture()
        {
            // 002262_WOMAN_ONE sub1 (the pants): names list the sheer coat2_ FIRST, but the range says attrib=1.
            var names = new[] { "002262_woman_coat2_.dds", "002262_woman_pant.dds", "002262_woman_coat2_.dds" };
            Assert.AreEqual("002262_woman_pant.dds", MshLoader.PickSubmeshDds(names, Ranges((1, 0, 488))));
        }

        [Test]
        public void SingleRange_BasicSkinAttrib_KeepsFirstNonBasicPick()
        {
            // 211 corpus meshes have a single-range table pointing at a Basic skin texture while a real cloth
            // name exists — honouring it would undress them, so the historical pick wins.
            var names = new[] { "000378_man_coat.dds", "M_Basic_Coat.dds" };
            Assert.AreEqual("000378_man_coat.dds", MshLoader.PickSubmeshDds(names, Ranges((1, 0, 100))));
        }

        [Test]
        public void SingleRange_AttribOutOfBounds_FallsBackToFirstNonBasic()
        {
            var names = new[] { "W_Basic_Coat2.dds", "000696_woman_coat.dds" };
            Assert.AreEqual("000696_woman_coat.dds", MshLoader.PickSubmeshDds(names, Ranges((7, 0, 10))));
        }

        [Test]
        public void SingleRange_EmptyAttribName_FallsBackToFirstNonBasic()
        {
            var names = new[] { "002178_woman_coat.dds", "" };
            Assert.AreEqual("002178_woman_coat.dds", MshLoader.PickSubmeshDds(names, Ranges((1, 0, 10))));
        }

        [Test]
        public void NoRanges_SkinListedFirst_PicksGarmentTexture()
        {
            // 2-material coats list the skin base first; without a range table the garment name must still win.
            var names = new[] { "W_Basic_Coat2.dds", "023441_woman_one.dds" };
            Assert.AreEqual("023441_woman_one.dds", MshLoader.PickSubmeshDds(names, Ranges()));
            Assert.AreEqual("023441_woman_one.dds", MshLoader.PickSubmeshDds(names, null));
        }

        [Test]
        public void MultiRange_UsesFirstNonBasicFallback()
        {
            // Multi-range submeshes are textured per range by the builder; Dds is only the fallback name.
            var names = new[] { "W_Basic_Coat2.dds", "002262_woman_pant.dds" };
            Assert.AreEqual("002262_woman_pant.dds", MshLoader.PickSubmeshDds(names, Ranges((0, 0, 5), (1, 5, 5))));
        }

        [Test]
        public void AllBasicNames_ReturnsFirst()
        {
            // Pure-skin parts (HAND) only carry a Basic material — unchanged behaviour.
            var names = new[] { "W_Basic_Hand.dds" };
            Assert.AreEqual("W_Basic_Hand.dds", MshLoader.PickSubmeshDds(names, Ranges((0, 0, 12))));
        }

        [Test]
        public void EmptyNames_ReturnsNull()
        {
            Assert.IsNull(MshLoader.PickSubmeshDds(new string[0], Ranges()));
            Assert.IsNull(MshLoader.PickSubmeshDds(null, null));
        }

        // --- 2. SdoAvatarBuilder.TransparentGarmentQueue: deterministic transparent draw order ------------------

        [Test]
        public void Queue_IsMonotonicInLoadOrder()
        {
            // 002178: pants submesh loads before the over-skirt submesh → pants always draw first, skirt always
            // blends over them — the two-state flicker cannot happen.
            int pants = SdoAvatarBuilder.TransparentGarmentQueue(0);
            int skirt = SdoAvatarBuilder.TransparentGarmentQueue(1);
            Assert.Less(pants, skirt);
            for (int i = 0; i < 100; i++)
                Assert.LessOrEqual(SdoAvatarBuilder.TransparentGarmentQueue(i), SdoAvatarBuilder.TransparentGarmentQueue(i + 1));
        }

        [Test]
        public void Queue_StaysInsideTransparentBand()
        {
            Assert.AreEqual(3000, SdoAvatarBuilder.TransparentGarmentQueue(0));
            Assert.AreEqual(3000, SdoAvatarBuilder.TransparentGarmentQueue(-5), "negative order clamps to the band start");
            Assert.AreEqual(3400, SdoAvatarBuilder.TransparentGarmentQueue(9999), "cap keeps deep lists below overlay 4000");
        }

        // --- 3. Blend garments must actually RENDER ---------------------------------------------------------------
        // A ColorMask-0 depth PREPASS was once added to stop a garment showing its own far side. It made every blended
        // garment vanish entirely (verified by rendering the same dress with this shader vs Unlit/Texture: opaque drew
        // the full dress, the 2-pass sheer shader drew nothing), which is what produced 「紅色不羈牛仔 是透明的」, the
        // grid of empty shop cards and finally 「身體透明」. These pin the single-pass shape so it cannot come back.

        [Test]
        public void SheerShader_IsSinglePass_NoDepthPrepass()
        {
            var sh = Shader.Find("Sdo/UnlitAvatarSheer");
            Assert.IsNotNull(sh, "Sdo/UnlitAvatarSheer missing — blend garments would fall back to an opaque shader");
            Assert.AreEqual(1, sh.passCount,
                "a second (prepass) pass makes every blended garment invisible in this project — see the shader header");
            Assert.AreEqual(3000, sh.renderQueue, "blend garments draw after the opaque body, like the engine's deferred list");
        }

        [Test]
        public void SheerShader_DensityMatchesTheStackedOfficialLook()
        {
            var sh = Shader.Find("Sdo/UnlitAvatarSheer");
            Assert.IsNotNull(sh);
            var mat = new Material(sh);
            try
            {
                // 1.0 = raw texture alpha (使用者:「又太透明」); 2.0 = fabric over itself twice, verified against the client.
                Assert.AreEqual(2f, mat.GetFloat("_Density"), 1e-4f);
                Assert.IsTrue(mat.HasProperty(SdoAvatarBuilder.SheerFabricProp),
                    "the shop card reads _SheerFabric to tell real weave from solid cut-out cloth");
            }
            finally { Object.DestroyImmediate(mat); }
        }
    }
}
