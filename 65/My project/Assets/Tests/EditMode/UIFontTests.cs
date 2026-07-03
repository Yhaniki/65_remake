using NUnit.Framework;
using TMPro;
using UnityEngine;
using Sdo.Game;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>Font resolution now mirrors the official client: PRIMARY = OS SimSun (the face the exe hardcodes
    /// in FontD3DWin.cpp), FALLBACK = bundled Source Han Sans. OS-dependent by nature, so the SimSun assertions
    /// gate on the face being installed — there, a fallback result means TMP's rasterization probe rejected
    /// simsun.ttc (the old 方塊字 failure mode) and the loader must be fixed, not the test.</summary>
    public class UIFontTests
    {
        // cover ALL UI languages: 繁中 + 簡中(语言/简体/应用/取消) + 日本語(かな) + Latin — none should 缺字.
        private const string AllUiLangChars = "繁體中文設定简体中文语言应用取消日本語かなAbc123";

        private static bool OsHasSimSun()
        {
            foreach (var n in Font.GetOSInstalledFontNames())
                if (n == "SimSun" || n == "NSimSun" || n == "宋体") return true;
            return false;
        }

        [Test]
        public void Cjk_PrefersOsSimSun_WhenInstalled()
        {
            TMP_FontAsset f = UIFont.Cjk;
            Assert.IsNotNull(f, "UIFont.Cjk null — OS SimSun, bundled font AND OS fallback all failed");
            if (OsHasSimSun())
                Assert.AreEqual("OS_SimSun", f.name,
                    "SimSun is installed but TMP fell back to '" + f.name + "' — simsun.ttc failed the raster probe");
        }

        [Test]
        public void Cjk_RasterizesAllUiLanguages_IncludingFallbacks()
        {
            TMP_FontAsset f = UIFont.Cjk;
            Assert.IsNotNull(f, "UIFont.Cjk null");
            // glyphs the primary lacks may legitimately come from the fallback table → search it too.
            foreach (char c in AllUiLangChars)
                Assert.IsTrue(f.HasCharacter(c, searchFallbacks: true, tryAddCharacter: true),
                    "缺字 '" + c + "' (primary=" + f.name + ") — coverage gap across primary+fallbacks");
        }

        [Test]
        public void TextMeshFont_PrefersOsSimSun_WhenInstalled()
        {
            Font f = TextStyles.CjkFont();
            Assert.IsNotNull(f, "TextStyles.CjkFont null — every source failed");
            if (OsHasSimSun())
            {
                Assert.IsTrue(f.dynamic, "expected a dynamic OS font when SimSun is installed, got " + f.name);
                StringAssert.Contains("SimSun", string.Join(",", f.fontNames),
                    "in-game HUD font did not resolve to OS SimSun");
            }
        }
    }
}
