using NUnit.Framework;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    public class EmblemArtTests
    {
        [Test]
        public void FileName_Blank_Returns_Empty()
        {
            Assert.AreEqual("", EmblemArt.FileName(""));
            Assert.AreEqual("", EmblemArt.FileName("   "));
            Assert.AreEqual("", EmblemArt.FileName(null));
        }

        [Test]
        public void FileName_BaseName_Appends_Png()
        {
            Assert.AreEqual("SMALL43.PNG", EmblemArt.FileName("SMALL43"));
            Assert.AreEqual("SMALL43.PNG", EmblemArt.FileName("  SMALL43  "));   // 去頭尾空白
            Assert.AreEqual("small0.PNG", EmblemArt.FileName("small0"));         // 大小寫原樣(Windows 檔案系統不分大小寫)
        }

        [Test]
        public void FileName_BareNumber_Expands_To_Small()
        {
            Assert.AreEqual("SMALL43.PNG", EmblemArt.FileName("43"));
            Assert.AreEqual("SMALL0.PNG", EmblemArt.FileName("0"));
        }

        [Test]
        public void FileName_Already_Has_Extension_Used_As_Is()
        {
            Assert.AreEqual("SMALL43.png", EmblemArt.FileName("SMALL43.png"));
            Assert.AreEqual("Small43.PNG", EmblemArt.FileName("Small43.PNG"));   // .png 比對不分大小寫，原樣保留
        }
    }
}
