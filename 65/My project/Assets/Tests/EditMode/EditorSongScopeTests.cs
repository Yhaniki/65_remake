using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 譜面編輯器的「只走這個資料夾」範圍邏輯。整包匯進來的歌要一首一首校時，Q/E 必須留在同一包裡 ——
    /// 走整份目錄的話，按一下 E 就掉到官方兩千多首中間去了。
    /// </summary>
    public class EditorSongScopeTests
    {
        private static SongCatalog.Entry Ext(string gn, string group)
            => new SongCatalog.Entry { gn = gn, external = true, group = group };

        private static SongCatalog.Entry Official(string gn)
            => new SongCatalog.Entry { gn = gn };

        private static List<SongCatalog.Entry> Library() => new List<SongCatalog.Entry>
        {
            Official("sdom0001k.gn"),
            Ext("ext_aaa k.gn".Replace(" ", ""), "NX Patch"),
            Ext("ext_bbbk.gn", "osu pack"),
            Ext("ext_ccck.gn", "NX Patch"),
            Official("sdom0002k.gn"),
            Ext("ext_dddk.gn", "NX Patch"),
        };

        [Test]
        public void ScopeOfSplitsExternalFoldersFromTheBuiltInCatalog()
        {
            Assert.AreEqual("NX Patch", EditorSongScope.ScopeOf(Ext("ext_aaak.gn", "NX Patch")));
            Assert.AreEqual(EditorSongScope.OfficialScope, EditorSongScope.ScopeOf(Official("sdom0001k.gn")));
            // 外部歌但沒有 group（散落在根目錄）→ 沒有可鎖的資料夾，歸官方那一堆
            Assert.AreEqual(EditorSongScope.OfficialScope, EditorSongScope.ScopeOf(Ext("ext_zzzk.gn", "")));
            Assert.AreEqual(EditorSongScope.OfficialScope, EditorSongScope.ScopeOf(null));
        }

        [Test]
        public void InScopeKeepsOnlyThatFolderInOriginalOrder()
        {
            var lib = Library();
            var nx = EditorSongScope.InScope(lib, "NX Patch");
            CollectionAssert.AreEqual(new[] { "ext_aaak.gn", "ext_ccck.gn", "ext_dddk.gn" }, Gns(nx));

            var official = EditorSongScope.InScope(lib, EditorSongScope.OfficialScope);
            CollectionAssert.AreEqual(new[] { "sdom0001k.gn", "sdom0002k.gn" }, Gns(official));
        }

        [Test]
        public void AllScopeReturnsEverything()
            => Assert.AreEqual(6, EditorSongScope.InScope(Library(), EditorSongScope.All).Count);

        [Test]
        public void AnEmptyScopeFallsBackToTheWholeLibrary()
        {
            // 鎖的資料夾被刪掉/改名了（重掃之後 group 變了）——寧可讓 Q/E 還能動，也不要卡在空清單裡。
            var lib = Library();
            Assert.AreEqual(lib.Count, EditorSongScope.InScope(lib, "資料夾不見了").Count);
        }

        [Test]
        public void StepWrapsAroundWithinTheFolder()
        {
            var nx = EditorSongScope.InScope(Library(), "NX Patch");
            Assert.AreEqual("ext_ccck.gn", EditorSongScope.Step(nx, "ext_aaak.gn", +1).gn);
            Assert.AreEqual("ext_aaak.gn", EditorSongScope.Step(nx, "ext_ccck.gn", -1).gn);
            Assert.AreEqual("ext_aaak.gn", EditorSongScope.Step(nx, "ext_dddk.gn", +1).gn, "最後一首往後 → 回到第一首");
            Assert.AreEqual("ext_dddk.gn", EditorSongScope.Step(nx, "ext_aaak.gn", -1).gn, "第一首往前 → 跳到最後一首");
        }

        [Test]
        public void StepFromASongOutsideTheScopeStartsAtTheTop()
        {
            // 剛換完資料夾，正在看的還是別包的歌 —— 按 E 要進到新資料夾的第一首，不是什麼都不做。
            var nx = EditorSongScope.InScope(Library(), "NX Patch");
            Assert.AreEqual("ext_aaak.gn", EditorSongScope.Step(nx, "sdom0001k.gn", +1).gn);
            Assert.IsNull(EditorSongScope.Step(new List<SongCatalog.Entry>(), "whatever", +1));
        }

        [Test]
        public void FoldersListsExternalGroupsOnceEach()
        {
            CollectionAssert.AreEqual(new[] { "NX Patch", "osu pack" }, EditorSongScope.Folders(Library()));
            Assert.AreEqual(0, EditorSongScope.Folders(null).Count);
        }

        [Test]
        public void LabelNamesTheSpecialScopes()
        {
            Assert.AreEqual("全部", EditorSongScope.Label(EditorSongScope.All));
            Assert.AreEqual("官方內建", EditorSongScope.Label(EditorSongScope.OfficialScope));
            Assert.AreEqual("NX Patch", EditorSongScope.Label("NX Patch"));
        }

        private static string[] Gns(List<SongCatalog.Entry> list)
        {
            var gns = new string[list.Count];
            for (int i = 0; i < list.Count; i++) gns[i] = list[i].gn;
            return gns;
        }
    }
}
