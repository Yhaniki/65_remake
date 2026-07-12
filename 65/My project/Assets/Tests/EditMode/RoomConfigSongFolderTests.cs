using NUnit.Framework;
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
        public void ParseInto_Reads_AdditionalSongFolders()
        {
            RoomConfig.additionalSongFolders = new string[0];
            RoomConfig.ParseInto("AdditionalSongFolders=D:/test,E:/more\n");
            Assert.AreEqual(new[] { "D:/test", "E:/more" }, RoomConfig.additionalSongFolders);
            RoomConfig.additionalSongFolders = new string[0];   // leave clean for other tests
        }
    }
}
