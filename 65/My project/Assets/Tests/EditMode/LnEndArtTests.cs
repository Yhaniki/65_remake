using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// The long-note END burst source table must stay pinned to the official EFFECT\GAME_EFT_&lt;n&gt;.DGE manifests
    /// (the Eft_LnEnd / Eft_Longnote_End slot). Skin index order is ScreenGameplay.NoteTypeEftSuffix:
    /// {2, 5, 8, 9, 10, 11, 7, 12, 13, 14, PET}.
    /// </summary>
    public class LnEndArtTests
    {
        [Test]
        public void Table_CoversEvery2dSkin()
        {
            Assert.AreEqual(11, LnEndArt.Folders.Length);   // == ScreenGameplay.NoteTypeEftSuffix.Length
        }

        // GAME_EFT_8/9/10/7/PET.DGE point at their OWN folder's Eft_Longnote_End.an.
        [TestCase(2, "EFT_8")]
        [TestCase(3, "EFT_9")]
        [TestCase(4, "EFT_10")]
        [TestCase(6, "EFT_7")]
        [TestCase(10, "EFT_PET")]
        public void SelfContainedSkins_UseTheirOwnFolder(int noteType, string folder)
        {
            Assert.AreEqual(folder, LnEndArt.Folder(noteType));
            Assert.AreEqual("EFT_0_3.PNG", LnEndArt.FrameFile(noteType, 3));
        }

        // GAME_EFT_2/5/11/12/13/14.DGE all point at PUBLICEFT\Eft_LnEnd.an — even 2/5, whose folders happen to ship a
        // stale EFT_0_* set from the older eft_1/3/4 skin layout. The manifest wins.
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(5)]
        [TestCase(7)]
        [TestCase(8)]
        [TestCase(9)]
        public void FamilySkins_ShareThePublicSet(int noteType)
        {
            Assert.AreEqual("PUBLICEFT", LnEndArt.Folder(noteType));
            Assert.AreEqual("EFT_LNEND0.PNG", LnEndArt.FrameFile(noteType, 0));
            Assert.AreEqual("EFT_LNEND5.PNG", LnEndArt.FrameFile(noteType, LnEndArt.FrameCount - 1));
        }

        [TestCase(-1)]
        [TestCase(11)]
        [TestCase(999)]
        public void OutOfRangeSkin_FallsBackToTheSharedSet(int noteType)
        {
            Assert.AreEqual(LnEndArt.SharedFolder, LnEndArt.Folder(noteType));
        }
    }
}
