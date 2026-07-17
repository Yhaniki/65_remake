using System.IO;
using NUnit.Framework;
using Sdo.Game;
using Sdo.Settings;

namespace Sdo.Tests
{
    public class RoomConfigSongFolderTests
    {
        [Test]
        public void ParseStringList_Trims_Drops_Empty_Normalises_Slashes()
        {
            var r = RoomConfig.ParseStringList(@"D:/a, E:/b ,, C:\x\y ");
            Assert.AreEqual(new[] { "D:/a", "E:/b", "C:/x/y" }, r);
        }

        [Test]
        public void ParseStringList_Empty_Or_Null_Is_Empty()
        {
            Assert.AreEqual(0, RoomConfig.ParseStringList("").Length);
            Assert.AreEqual(0, RoomConfig.ParseStringList(null).Length);
            Assert.AreEqual(0, RoomConfig.ParseStringList("  ,  ,").Length);
        }

        [Test]
        public void ParseStringList_Splits_On_Semicolons()
        {
            var r = RoomConfig.ParseStringList("D:/a; E:/b ; C:/c");
            Assert.AreEqual(new[] { "D:/a", "E:/b", "C:/c" }, r);
        }

        [Test]
        public void ParseStringList_Accepts_Both_Semicolons_And_Commas()
        {
            var r = RoomConfig.ParseStringList("D:/a;E:/b,F:/c");
            Assert.AreEqual(new[] { "D:/a", "E:/b", "F:/c" }, r);
        }

        [Test]
        public void ParseStringList_Strips_Leading_Slashes_Before_A_Drive()
        {
            // The StepMania-style config the user pastes: leading slashes in front of the drive are noise.
            var r = RoomConfig.ParseStringList("////D:/StepMania/Songs");
            Assert.AreEqual(new[] { "D:/StepMania/Songs" }, r);
        }

        [Test]
        public void ParseStringList_Leaves_A_Unc_Path_Alone()
        {
            var r = RoomConfig.ParseStringList(@"\\server\share\Songs");
            Assert.AreEqual(new[] { "//server/share/Songs" }, r);
        }

        [Test]
        public void ParseInto_Reads_AdditionalSongFolders()
        {
            RoomConfig.additionalSongFolders = new string[0];
            RoomConfig.ParseInto("AdditionalSongFolders=D:/test,E:/more\n");
            Assert.AreEqual(new[] { "D:/test", "E:/more" }, RoomConfig.additionalSongFolders);
            RoomConfig.additionalSongFolders = new string[0];   // leave clean for other tests
        }

        [Test]
        public void ParseInto_Reads_Semicolon_AdditionalSongFolders_With_Leading_Slashes()
        {
            RoomConfig.additionalSongFolders = new string[0];
            RoomConfig.ParseInto("AdditionalSongFolders=////D:/test;E:/more\n");
            Assert.AreEqual(new[] { "D:/test", "E:/more" }, RoomConfig.additionalSongFolders);
            RoomConfig.additionalSongFolders = new string[0];   // leave clean for other tests
        }

        [Test]
        public void ParseInto_Reads_AddonFolder()
        {
            RoomConfig.addonFolder = "";
            RoomConfig.ParseInto("AddonFolder=////D:/SdoAddon\n");
            Assert.AreEqual("D:/SdoAddon", RoomConfig.addonFolder, "same cleaning as the song folders (drive-prefix slashes stripped)");
            RoomConfig.addonFolder = "";   // leave clean for other tests
        }

        [Test]
        public void AddonDir_Defaults_Under_Root_When_No_Config_Override()
        {
            RoomConfig.addonFolder = "";
            Assert.AreEqual(Path.Combine(SdoExtracted.Root, "ADDON"), SdoExtracted.AddonDir);
            StringAssert.EndsWith(Path.Combine("ADDON", "SONG"), SdoExtracted.AddonSongsDir);
        }

        [Test]
        public void AddonDir_Honours_The_Config_Override()
        {
            RoomConfig.addonFolder = "D:/SdoAddon";
            // The whole plugin tree relocates under the custom folder — SONG lives directly beneath it.
            Assert.AreEqual(Path.GetFullPath("D:/SdoAddon"), SdoExtracted.AddonDir);
            StringAssert.EndsWith(Path.Combine("SdoAddon", "SONG"), SdoExtracted.AddonSongsDir);
            RoomConfig.addonFolder = "";   // leave clean for other tests
        }
    }
}
