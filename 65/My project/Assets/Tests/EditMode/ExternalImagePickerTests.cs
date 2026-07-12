using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    public class ExternalImagePickerTests
    {
        [Test]
        public void Jacket_Wins_Over_Banner_And_Background()
        {
            var f = new[] { "jacket.png", "banner.png", "bg.png" };
            Assert.AreEqual("jacket.png", ExternalImagePicker.Pick(f, "banner.png", "bg.png", ""));
        }

        [Test]
        public void Explicit_Banner_Tag_Used()
        {
            var f = new[] { "a.png", "b.png" };
            Assert.AreEqual("a.png", ExternalImagePicker.Pick(f, "a.png", "", ""));
        }

        [Test]
        public void Banner_By_Filename_Hint()
        {
            var f = new[] { "songBanner.jpg", "x.png" };
            Assert.AreEqual("songBanner.jpg", ExternalImagePicker.Pick(f, "", "", ""));
        }

        [Test]
        public void Banner_Beats_Background()
        {
            var f = new[] { "the-banner.png", "the-bg.png" };
            Assert.AreEqual("the-banner.png", ExternalImagePicker.Pick(f, "", "", ""));
        }

        [Test]
        public void Background_Tag_Then_Hint()
        {
            Assert.AreEqual("back.jpg", ExternalImagePicker.Pick(new[] { "back.jpg" }, "", "back.jpg", ""));
            Assert.AreEqual("background.png", ExternalImagePicker.Pick(new[] { "background.png" }, "", "", ""));
        }

        [Test]
        public void Cdtitle_Never_Chosen_As_Tile()
        {
            // the only image is the cdtitle logo → no tile.
            Assert.AreEqual("", ExternalImagePicker.Pick(new[] { "cdtitle.png" }, "", "", "cdtitle.png"));
        }

        [Test]
        public void Last_Resort_Any_NonCdtitle_Image()
        {
            var f = new[] { "cdtitle.png", "whatever.png" };
            Assert.AreEqual("whatever.png", ExternalImagePicker.Pick(f, "", "", "cdtitle.png"));
        }

        [Test]
        public void Empty_When_No_Images()
        {
            Assert.AreEqual("", ExternalImagePicker.Pick(new string[0], "", "", ""));
        }
    }
}
