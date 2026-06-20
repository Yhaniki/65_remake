using NUnit.Framework;
using TMPro;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>Verifies the bundled Source Han Sans (思源黑體) loads and TMP can rasterize CJK from it — i.e. the UI
    /// shows real Chinese, not 方塊字. (The earlier OS-dynamic-font path could not be rasterized by TMP.)</summary>
    public class UIFontTests
    {
        [Test]
        public void Cjk_LoadsBundledSourceHanSans_AndRasterizesChinese()
        {
            TMP_FontAsset f = UIFont.Cjk;
            Assert.IsNotNull(f, "UIFont.Cjk null — bundled font not found AND OS fallback failed");
            Assert.AreEqual("SourceHanSansTC", f.name,
                "expected the bundled Source Han Sans (Resources/Fonts) — got the OS fallback, so the font didn't import/load");
            // cover ALL UI languages: 繁中 + 簡中(语言/简体/应用/取消) + 日本語(かな) + Latin — none should 缺字.
            Assert.IsTrue(f.TryAddCharacters("繁體中文設定 简体中文语言应用取消 日本語かな"),
                "TMP could not rasterize some CJK glyphs (SC/JP) from the bundled font — coverage gap");
        }
    }
}
