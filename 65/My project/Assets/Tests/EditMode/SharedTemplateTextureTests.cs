using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>Guards the fix for 使用者回報「070030 鞋子 貼圖錯誤」: 070030 女鞋 REUSES 012989's mesh, so its material
    /// names embed the TEMPLATE id ("012989_woman_shoes.dds" / "_texanimex(012989_woman_shoes)100_1.dds"), but it ships
    /// its OWN yellow recolour under 070030_*. <see cref="SdoAvatarBuilder.SwapLeadingId"/> rewrites the leading 6-digit
    /// id to the mesh's own id so the item wears ITS textures (yellow atlas + yellow-heart .an) instead of the template's
    /// pink ones. Pure string logic — the on-disk existence gate lives in the (I/O) PreferOwnIdTexture caller.</summary>
    public class SharedTemplateTextureTests
    {
        [Test]
        public void PlainDds_SwapsLeadingTemplateId()
        {
            Assert.AreEqual("070030_woman_shoes.dds",
                SdoAvatarBuilder.SwapLeadingId("012989_woman_shoes.dds", "070030"));
        }

        [Test]
        public void TexAnimPlaceholder_SwapsIdInsideParens_KeepsInterval()
        {
            // the id inside the (...) is swapped; the interval/suffix ("100_1.dds") is preserved so the .an lookup +
            // frame interval still parse — TexAnimEx.TryParse on the result yields Name="070030_woman_shoes".
            Assert.AreEqual("_texanimex(070030_woman_shoes)100_1.dds",
                SdoAvatarBuilder.SwapLeadingId("_texanimex(012989_woman_shoes)100_1.dds", "070030"));
        }

        [Test]
        public void SameId_NoSwap()
        {
            Assert.IsNull(SdoAvatarBuilder.SwapLeadingId("070030_woman_shoes.dds", "070030"));
            Assert.IsNull(SdoAvatarBuilder.SwapLeadingId("_texanimex(070030_woman_shoes)100_1.dds", "070030"));
        }

        [Test]
        public void NoLeadingSixDigitId_NoSwap()
        {
            // shared skin bases (W_Basic_*) and non-id aliases (sh1226_*) must be left untouched.
            Assert.IsNull(SdoAvatarBuilder.SwapLeadingId("W_Basic_Pants2.dds", "070030"));
            Assert.IsNull(SdoAvatarBuilder.SwapLeadingId("sh1226_woman_one.dds", "070030"));
            Assert.IsNull(SdoAvatarBuilder.SwapLeadingId("12345_woman_shoes.dds", "070030"));   // only 5 digits
        }

        [Test]
        public void NullOrEmpty_NoSwap()
        {
            Assert.IsNull(SdoAvatarBuilder.SwapLeadingId(null, "070030"));
            Assert.IsNull(SdoAvatarBuilder.SwapLeadingId("", "070030"));
            Assert.IsNull(SdoAvatarBuilder.SwapLeadingId("012989_woman_shoes.dds", null));
            Assert.IsNull(SdoAvatarBuilder.SwapLeadingId("012989_woman_shoes.dds", ""));
        }

        [Test]
        public void RoundTrips_ThroughTexAnimParse()
        {
            // the swapped placeholder must re-parse to the OWN-id .an name (this is what PreferOwnIdTexture then probes).
            var swapped = SdoAvatarBuilder.SwapLeadingId("_texanimex(012989_woman_shoes)100_1.dds", "070030");
            Assert.IsTrue(TexAnimEx.TryParse(swapped, out var spec));
            Assert.AreEqual("070030_woman_shoes", spec.Name);
            Assert.AreEqual(100f, spec.IntervalMs);
        }
    }
}
