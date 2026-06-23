using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using Sdo.UI.Screens;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>Pure-logic cover for the ROOMDLG song-select extraction points:
    /// folder resolution order (RoomDlgArt.PickDir) and page slicing (SongSelectScreen.PageSlice).</summary>
    public class RoomDlgArtTests
    {
        // ---- RoomDlgArt.PickDir : prefer 閉撰敃氪/DatasSDO, fall back to built DATA ----

        [Test]
        public void PickDir_Returns_First_Existing()
        {
            var ordered = new List<string> { "online/DatasSDO/UI/ROOMDLG", "DATA/UI/ROOMDLG" };
            // only the built fallback exists -> it is chosen
            Assert.AreEqual("DATA/UI/ROOMDLG",
                RoomDlgArt.PickDir(ordered, p => p == "DATA/UI/ROOMDLG"));
        }

        [Test]
        public void PickDir_Prefers_Online_Over_Built_When_Both_Exist()
        {
            var ordered = new List<string> { "online/DatasSDO/UI/ROOMDLG", "DATA/UI/ROOMDLG" };
            Assert.AreEqual("online/DatasSDO/UI/ROOMDLG",
                RoomDlgArt.PickDir(ordered, _ => true));   // both exist -> first (online) wins
        }

        [Test]
        public void PickDir_Falls_Back_To_Last_When_None_Exist()
        {
            var ordered = new List<string> { "a", "b", "DATA/UI/ROOMDLG" };
            Assert.AreEqual("DATA/UI/ROOMDLG", RoomDlgArt.PickDir(ordered, _ => false));
        }

        [Test]
        public void PickDir_Empty_Is_Null()
        {
            Assert.IsNull(RoomDlgArt.PickDir(new List<string>(), _ => true));
            Assert.IsNull(RoomDlgArt.PickDir(null, _ => true));
        }

        // ---- SongSelectScreen.PageSlice : 12-row page window ----

        private static List<SongCatalog.Entry> N(int count)
        {
            var l = new List<SongCatalog.Entry>();
            for (int i = 0; i < count; i++) l.Add(new SongCatalog.Entry { gn = i + ".gn", fileId = i });
            return l;
        }

        [Test]
        public void PageSlice_FullFirstPage()
        {
            var s = SongSelectScreen.PageSlice(N(30), 0, 12);
            Assert.AreEqual(12, s.Count);
            Assert.AreEqual(0, s[0].fileId);
            Assert.AreEqual(11, s[11].fileId);
        }

        [Test]
        public void PageSlice_LastPage_Partial()
        {
            var s = SongSelectScreen.PageSlice(N(30), 2, 12);   // items 24..29
            Assert.AreEqual(6, s.Count);
            Assert.AreEqual(24, s[0].fileId);
            Assert.AreEqual(29, s[5].fileId);
        }

        [Test]
        public void PageSlice_OutOfRange_Empty()
        {
            Assert.AreEqual(0, SongSelectScreen.PageSlice(N(5), 9, 12).Count);
            Assert.AreEqual(0, SongSelectScreen.PageSlice(null, 0, 12).Count);
            Assert.AreEqual(0, SongSelectScreen.PageSlice(N(5), 0, 0).Count);
        }
    }
}
