using System;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>Guards the fix for 使用者回報「Flower Lace Dress 這件衣服透明度沒做出來」(item 1224976 = model 24976,
    /// cat 150 OnePieceFemale → 024976_WOMAN_ONE). A real sheer LACE dress was being decoded as a hard Cutout (clip +
    /// a→1) → the semi-transparent lace sleeves rendered as solid black, so the skin never showed through.
    ///
    /// The distribution signal that separates a genuine sheer fabric from the two look-alikes is the fraction of
    /// GENUINELY-translucent (mid-alpha) texels (<see cref="DdsLoader.TranslucentFraction"/>):
    ///   • sheer lace/mesh (Flower Lace 024976) is translucent everywhere → HIGH (measured 0.27-0.35);
    ///   • a SOLID dress with hard lace-hem holes is bimodal a≈0 + a≈1 → LOW (眉画犹思 037888 ≈ 0.09);
    ///   • a broken all-0 alpha channel (璀璨繁星 男褲) ≈ 0.
    /// <see cref="SdoAvatarBuilder.GarmentAlphaMode"/> then routes a high fraction to alpha-BLEND (skin shows through)
    /// while keeping the solid-with-holes case Cutout and the broken/soft-solid case Opaque.
    ///
    /// SUPERSEDED for garments that carry official flags: transparency now comes from the mesh's per-material flag
    /// (<see cref="SdoAvatarBuilder.OfficialAlphaMode"/>, pinned by <see cref="GarmentAlphaRealDataTests"/>). This
    /// histogram path remains the FALLBACK for meshes with no flag table, so its behaviour is still pinned here.</summary>
    public class GarmentSheerAlphaTests
    {
        // --- The OFFICIAL rule (flag-driven) — what the retail engine actually does ---------------------------------

        [Test]
        public void OfficialAlphaMode_AnyNonZeroFlag_IsBlend()
        {
            // The engine's only test on the .msh value is `flags & 0x3f`: non-zero → deferred (alpha-blended) batch.
            // Every non-zero value seen in the shipped corpus must therefore blend: 1 (620 cloth textures, 99.0% of
            // them carry real alpha), 2 (9,640, 75.9%), plus the rare 0x11 / 0x12.
            foreach (uint f in new uint[] { 1u, 2u, 0x11u, 0x12u })
                Assert.AreEqual(DdsAlphaMode.Blend, SdoAvatarBuilder.OfficialAlphaMode(f, textureHasAlpha: true),
                    $"flag 0x{f:x} joins the deferred batch → must blend");
        }

        [Test]
        public void OfficialAlphaMode_ZeroFlag_IsOpaque_EvenWithHoles()
        {
            // flag 0 = the opaque batch: the engine never touches alpha state, so holes included are ignored. Only
            // 0.3% of flag-0 cloth textures carry real alpha at all — this is the genuinely solid class (璀璨繁星's
            // broken all-0 alpha draws solid officially exactly because of this).
            Assert.AreEqual(DdsAlphaMode.Opaque, SdoAvatarBuilder.OfficialAlphaMode(0u, true));
        }

        [Test]
        public void OfficialAlphaMode_Flag1IsNotOpaque()
        {
            // Regression guard for 使用者「Flower Lace Dress 比官方的黑」: flag 1 was briefly read as "opaque + UV
            // transform", which painted 024976's liner — the SAME near-black lace texture — solid over the dress.
            Assert.AreNotEqual(DdsAlphaMode.Opaque, SdoAvatarBuilder.OfficialAlphaMode(1u, true));
        }

        [Test]
        public void OfficialAlphaMode_TransparentBatch_SplitsByTranslucent()
        {
            // The flag gates opaque-vs-transparent; WITHIN the transparent batch the texture's mid-alpha fraction picks
            // the KIND. High translucent = real sheer fabric → Blend (金姬兰 lace 0.35, 京族 紗裙 0.325).
            Assert.AreEqual(DdsAlphaMode.Blend,
                SdoAvatarBuilder.OfficialAlphaMode(2u, textureHasAlpha: true, translucent: 0.35f));
            Assert.AreEqual(DdsAlphaMode.Blend,
                SdoAvatarBuilder.OfficialAlphaMode(1u, true, 0.25f));
            // Low translucent = a silhouette CUT-OUT (solid cloth, its mid-alphas only the AA edge) → Cutout, so a
            // ruffle tier's AA edge doesn't read see-through (使用者 娃娃 003834: translucent 0.005).
            Assert.AreEqual(DdsAlphaMode.Cutout,
                SdoAvatarBuilder.OfficialAlphaMode(2u, true, 0.005f));
            Assert.AreEqual(DdsAlphaMode.Cutout,
                SdoAvatarBuilder.OfficialAlphaMode(1u, true, 0.153f));   // 003136 紅色不羈牛仔 → solid
            // flag 0 stays Opaque regardless of translucent — the flag still gates transparency (no seesaw).
            Assert.AreEqual(DdsAlphaMode.Opaque, SdoAvatarBuilder.OfficialAlphaMode(0u, true, 0.9f));
            // just under / over the bar.
            Assert.AreEqual(DdsAlphaMode.Cutout, SdoAvatarBuilder.OfficialAlphaMode(2u, true, 0.20f));
            Assert.AreEqual(DdsAlphaMode.Blend,  SdoAvatarBuilder.OfficialAlphaMode(2u, true, 0.21f));
        }

        [Test]
        public void OfficialAlphaMode_TwoArgOverload_AssumesSheer()
        {
            // The 2-arg overload (texel mix unknown) assumes sheer, so a transparent flag → Blend, never Cutout.
            // The Cutout split only exists on the 3-arg overload that gets the translucent fraction (test above).
            foreach (uint f in new uint[] { 0u, 1u, 2u, 0x11u, 0x12u })
            {
                Assert.AreNotEqual(DdsAlphaMode.Cutout, SdoAvatarBuilder.OfficialAlphaMode(f, true));
                Assert.AreNotEqual(DdsAlphaMode.Cutout, SdoAvatarBuilder.OfficialAlphaMode(f, false));
            }
        }

        [Test]
        public void OfficialAlphaMode_NoAlphaChannel_IsOpaque()
        {
            // Blending a DXT1 / all-255 texture is a no-op; Opaque also keeps it out of the transparent queue.
            Assert.AreEqual(DdsAlphaMode.Opaque, SdoAvatarBuilder.OfficialAlphaMode(MshLoader.MatFlagTransparentMask, textureHasAlpha: false));
        }

        [Test]
        public void MatFlagAt_MissingTable_IsNull_SoCallersFallBack()
        {
            Assert.IsNull(SdoAvatarBuilder.MatFlagAt(null, 0));
            Assert.IsNull(SdoAvatarBuilder.MatFlagAt(new MshLoader.SubMesh(), 0));                       // MatFlags == null
            var sub = new MshLoader.SubMesh { MatFlags = new uint[] { 2u } };
            Assert.IsNull(SdoAvatarBuilder.MatFlagAt(sub, 1), "out-of-range index must not throw or wrap");
            Assert.IsNull(SdoAvatarBuilder.MatFlagAt(sub, -1));
            Assert.AreEqual(2u, SdoAvatarBuilder.MatFlagAt(sub, 0));
        }

        // 4×4 DXT3 (one block): 16 explicit 4-bit alpha nibbles (texel i = nibbles[i]) + an opaque colour payload.
        // DXT3 alpha byte k packs texel 2k in the low nibble, texel 2k+1 in the high nibble.
        private static byte[] MakeDxt3(byte[] nibbles)
        {
            var block = new byte[16];
            for (int k = 0; k < 8; k++)
                block[k] = (byte)((nibbles[2 * k] & 0xF) | ((nibbles[2 * k + 1] & 0xF) << 4));
            var d = new byte[128 + 16];
            d[0] = (byte)'D'; d[1] = (byte)'D'; d[2] = (byte)'S'; d[3] = (byte)' ';
            BitConverter.GetBytes(4).CopyTo(d, 12);   // height
            BitConverter.GetBytes(4).CopyTo(d, 16);   // width
            System.Text.Encoding.ASCII.GetBytes("DXT3").CopyTo(d, 84);
            block.CopyTo(d, 128);
            return d;
        }

        private static byte[] Fill(byte nibble) { var n = new byte[16]; for (int i = 0; i < 16; i++) n[i] = nibble; return n; }

        // --- TranslucentFraction: the mid-alpha signal ------------------------------------------------------------

        [Test]
        public void TranslucentFraction_AllMidAlpha_IsOne()
        {
            // nibble 8 → alpha 136, squarely inside the translucent band (32<a<224) → every texel counts.
            Assert.AreEqual(1f, DdsLoader.TranslucentFraction(MakeDxt3(Fill(8))), 1e-4f);
        }

        [Test]
        public void TranslucentFraction_SolidAndTransparent_AreNotTranslucent()
        {
            Assert.AreEqual(0f, DdsLoader.TranslucentFraction(MakeDxt3(Fill(15))), 1e-4f, "opaque (a=255) is not translucent");
            Assert.AreEqual(0f, DdsLoader.TranslucentFraction(MakeDxt3(Fill(0))), 1e-4f, "fully transparent (a=0) is not translucent");
        }

        [Test]
        public void TranslucentFraction_BimodalSolidWithHoles_ScoresLow()
        {
            // 眉画犹思-style solid dress: half opaque body (a=255) + half hard holes (a=0), no midtones → 0, NOT sheer.
            var n = new byte[16];
            for (int i = 0; i < 8; i++) n[i] = 15;    // opaque body
            for (int i = 8; i < 16; i++) n[i] = 0;    // hard lace-hem holes
            Assert.AreEqual(0f, DdsLoader.TranslucentFraction(MakeDxt3(n)), 1e-4f);
        }

        [Test]
        public void TranslucentFraction_SheerFabric_ScoresHigh()
        {
            // Flower-Lace-style: 8 translucent threads + 4 opaque trim + 4 holes → translucent = 8/16 = 0.5.
            var n = new byte[16];
            for (int i = 0; i < 8; i++) n[i] = 8;      // translucent lace (a=136)
            for (int i = 8; i < 12; i++) n[i] = 15;    // opaque trim
            for (int i = 12; i < 16; i++) n[i] = 0;    // holes
            Assert.AreEqual(0.5f, DdsLoader.TranslucentFraction(MakeDxt3(n)), 1e-4f);
        }

        [Test]
        public void TranslucentFraction_BandExcludesNearOpaqueAndNearTransparent()
        {
            // nibble 14 → a=238 (near-opaque, ≥224) and nibble 1 → a=17 (near-transparent, ≤32): both OUTSIDE the band.
            Assert.AreEqual(0f, DdsLoader.TranslucentFraction(MakeDxt3(Fill(14))), 1e-4f);
            Assert.AreEqual(0f, DdsLoader.TranslucentFraction(MakeDxt3(Fill(1))), 1e-4f);
        }

        [Test]
        public void TranslucentFraction_Dxt1HasNoAlpha_IsZero()
        {
            var d = new byte[128 + 8];
            d[0] = (byte)'D'; d[1] = (byte)'D'; d[2] = (byte)'S'; d[3] = (byte)' ';
            BitConverter.GetBytes(4).CopyTo(d, 12); BitConverter.GetBytes(4).CopyTo(d, 16);
            System.Text.Encoding.ASCII.GetBytes("DXT1").CopyTo(d, 84);
            Assert.AreEqual(0f, DdsLoader.TranslucentFraction(d), 1e-4f);
        }

        // --- GarmentAlphaMode: the pure decision, pinned with the real measured fractions -------------------------

        [Test]
        public void GarmentAlphaMode_FlowerLaceDress_IsBlend()
        {
            // 024976_WOMAN_ONE_A: scene=Cutout, hardTransp≈0.109, translucent≈0.348 → sheer fabric → BLEND (skin shows).
            Assert.AreEqual(DdsAlphaMode.Blend,
                SdoAvatarBuilder.GarmentAlphaMode(DdsAlphaMode.Cutout, 0.109f, 0.348f, true));
            // 024976_WOMAN_ONE_B (0.274) is the LOWEST verified sheer anchor — it must stay above the 0.21 bar.
            Assert.AreEqual(DdsAlphaMode.Blend,
                SdoAvatarBuilder.GarmentAlphaMode(DdsAlphaMode.Cutout, 0.072f, 0.274f, true));
            // even if the classifier had called it Blend, a genuine sheer fabric stays Blend (not force-opaqued).
            Assert.AreEqual(DdsAlphaMode.Blend,
                SdoAvatarBuilder.GarmentAlphaMode(DdsAlphaMode.Blend, 0.10f, 0.40f, true));
        }

        [Test]
        public void GarmentAlphaMode_FringeTopWithSoftEdges_StaysCutout()
        {
            // 紅色不羈牛仔 003136_WOMAN_COAT_: a SOLID feather-fringe top (73% of visible texels fully opaque) whose
            // mid-alpha (≈0.153) is all EDGE gradient on the fringes, not a sheer body. The original 0.15 bar classed
            // it sheer → the whole top rendered transparent (empty shop card + invisible when worn). The bar now sits
            // at 0.21, the midpoint between the highest fringe look-alike (0.153) and the lowest verified sheer (0.274).
            Assert.AreEqual(DdsAlphaMode.Cutout,
                SdoAvatarBuilder.GarmentAlphaMode(DdsAlphaMode.Cutout, 0.193f, 0.153f, true));
            // just under the bar stays Cutout; the verified sheer anchors (0.274+) stay Blend (test above).
            Assert.AreEqual(DdsAlphaMode.Cutout,
                SdoAvatarBuilder.GarmentAlphaMode(DdsAlphaMode.Cutout, 0.10f, 0.20f, true));
        }

        [Test]
        public void GarmentAlphaMode_SolidDressWithHoles_StaysCutout()
        {
            // 眉画犹思 037888_WOMAN_ONE: scene=Cutout, hardTransp≈0.165, translucent≈0.092 → NOT sheer → keep Cutout
            // (solid body opaque, holes clip to skin). Blend here would make the whole solid dress see-through.
            Assert.AreEqual(DdsAlphaMode.Cutout,
                SdoAvatarBuilder.GarmentAlphaMode(DdsAlphaMode.Cutout, 0.165f, 0.092f, true));
        }

        [Test]
        public void GarmentAlphaMode_BrokenAllZeroAlpha_IsForcedOpaque()
        {
            // 璀璨繁星 男褲: alpha exported all-0 (>70% holes), few midtones → force OPAQUE (else a see-through wireframe).
            Assert.AreEqual(DdsAlphaMode.Opaque,
                SdoAvatarBuilder.GarmentAlphaMode(DdsAlphaMode.Cutout, 0.94f, 0.02f, true));
            Assert.AreEqual(DdsAlphaMode.Opaque,
                SdoAvatarBuilder.GarmentAlphaMode(DdsAlphaMode.Blend, 0.94f, 0.02f, true));
        }

        [Test]
        public void GarmentAlphaMode_SoftShadedSolid_IsForcedOpaque()
        {
            // A solid garment whose soft alpha is lighting/AA (scene=Blend) but has few real midtones → OPAQUE, unchanged.
            Assert.AreEqual(DdsAlphaMode.Opaque,
                SdoAvatarBuilder.GarmentAlphaMode(DdsAlphaMode.Blend, 0.05f, 0.08f, true));
        }

        [Test]
        public void GarmentAlphaMode_MostlyHolesWinsOverMidtones()
        {
            // Ambiguous 023425_WOMAN_COAT (hardTransp≈0.78, translucent≈0.19): >70% holes reads as broken/near-empty →
            // the hole guard beats the midtone test → OPAQUE (safer than a see-through wireframe).
            Assert.AreEqual(DdsAlphaMode.Opaque,
                SdoAvatarBuilder.GarmentAlphaMode(DdsAlphaMode.Cutout, 0.78f, 0.19f, true));
        }

        [Test]
        public void GarmentAlphaMode_NonGarmentAndOpaque_AreUntouched()
        {
            // Accessories (wings/glasses/hair) pass bodyGarment=false → their own alpha is never overridden here.
            Assert.AreEqual(DdsAlphaMode.Cutout,
                SdoAvatarBuilder.GarmentAlphaMode(DdsAlphaMode.Cutout, 0.9f, 0.9f, false));
            Assert.AreEqual(DdsAlphaMode.Blend,
                SdoAvatarBuilder.GarmentAlphaMode(DdsAlphaMode.Blend, 0.9f, 0.9f, false));
            // an opaque garment is a no-op regardless of the fractions.
            Assert.AreEqual(DdsAlphaMode.Opaque,
                SdoAvatarBuilder.GarmentAlphaMode(DdsAlphaMode.Opaque, 0f, 0f, true));
        }
    }
}
