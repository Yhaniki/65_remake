using System.Globalization;
using System.Threading;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 歌單搜尋比對（<see cref="SongCatalog.Matches"/>）—— 譜面編輯器 F1 歌單「用 fileId 編號找歌」的核心邏輯。
    /// 重點是 fileId 跟 gn 檔名的號碼不一樣（k 譜 = 10000+N），所以拿編號比 gn 是找不到的，一定要有這條路。
    /// 編號比對**只對官方歌**：外部歌的 fileId 是掃描流水號，不是編號。
    /// </summary>
    public class SongCatalogSearchTests
    {
        private static SongCatalog.Entry E(string gn, int fileId, string title, string artist)
            => new SongCatalog.Entry { gn = gn, fileId = fileId, title = title, artist = artist };

        // 外部歌照 ExternalSongLibrary.ToEntry 的樣子造：gn 是路徑 hash、fileId 是 -(第幾首 + 1000)。
        private static SongCatalog.Entry Ext(int index, string title, string artist)
            => new SongCatalog.Entry
            {
                gn = "ext_1a2b3c4dk.gn", fileId = -(index + 1000),
                title = title, artist = artist, external = true,
            };

        [Test]
        public void Empty_Query_Matches_Everything()
        {
            var e = E("sdom0001k.gn", 10001, "野蛮游戏", "群星");
            Assert.IsTrue(SongCatalog.Matches(e, ""));
            Assert.IsTrue(SongCatalog.Matches(e, null));
            Assert.IsTrue(SongCatalog.Matches(e, "   "));
        }

        [Test]
        public void Matches_By_FileId_Exact()
            => Assert.IsTrue(SongCatalog.Matches(E("sdom0026k.gn", 10026, "x", "y"), "10026"));

        [Test]
        public void Matches_By_FileId_Substring()
            => Assert.IsTrue(SongCatalog.Matches(E("sdom0026k.gn", 10026, "x", "y"), "1002"));

        // 這一條就是整個功能的理由：fileId 10026 找得到，但 gn "sdom0026k.gn" 裡根本沒有 "10026"。
        [Test]
        public void FileId_Search_Finds_What_Gn_Cannot()
        {
            var e = E("sdom0026k.gn", 10026, "歌名", "曲師");
            Assert.IsTrue(SongCatalog.Matches(e, "10026"), "fileId 應該找得到");
            Assert.IsFalse(e.gn.Contains("10026"), "gn 檔名裡沒有這個編號 —— 正是要靠 fileId 的原因");
        }

        [Test]
        public void Matches_By_Title_And_Artist_CaseInsensitive()
        {
            var e = E("sdom0001k.gn", 10001, "Butterfly", "Smile.dk");
            Assert.IsTrue(SongCatalog.Matches(e, "butter"));
            Assert.IsTrue(SongCatalog.Matches(e, "SMILE"));
        }

        [Test]
        public void Matches_By_Gn()
            => Assert.IsTrue(SongCatalog.Matches(E("sdom1197k.gn", 11197, "x", "y"), "1197k"));

        [Test]
        public void NonMatching_Query_Rejected()
            => Assert.IsFalse(SongCatalog.Matches(E("sdom0001k.gn", 10001, "Butterfly", "Smile"), "zzz999"));

        // 非數字字串不會誤中 fileId（"butter" 不是 "10001" 的子字串），fileId 比對只對數字有意義。
        [Test]
        public void Text_Query_Does_Not_Accidentally_Hit_FileId()
            => Assert.IsFalse(SongCatalog.Matches(E("aaa.gn", 10001, "曲名", "作者"), "999"));

        // ---- 編號查只有官方歌 ----

        // 這就是這條規則的理由：外部歌 fileId = -1002，「-1002」裡含子字串 "1002" ——
        // 沒擋的話，打官方 1002 號會順手撈出一首毫不相干的外部歌。
        [Test]
        public void External_Song_Is_Not_Findable_By_Number()
        {
            var e = Ext(2, "某首外部歌", "某人");
            Assert.IsFalse(SongCatalog.Matches(e, "1002"), "外部歌不該被編號找到");
            Assert.IsFalse(SongCatalog.Matches(e, "-1002"), "連完整的流水號也不該找得到");
        }

        // 同一個字串，官方歌照樣找得到 —— 擋的是外部歌，不是把編號查關掉。
        [Test]
        public void Official_Song_Still_Findable_By_The_Same_Number()
            => Assert.IsTrue(SongCatalog.Matches(E("sdom1002k.gn", 11002, "x", "y"), "11002"));

        // 擋掉的只有編號那條路：名稱／曲師／gn 對外部歌照常有效。
        [Test]
        public void External_Song_Still_Findable_By_Text_And_Gn()
        {
            var e = Ext(2, "Butterfly", "Smile.dk");
            Assert.IsTrue(SongCatalog.Matches(e, "butter"));
            Assert.IsTrue(SongCatalog.Matches(e, "SMILE"));
            Assert.IsTrue(SongCatalog.Matches(e, "ext_1a2b3c4d"));
            Assert.IsTrue(SongCatalog.Matches(e, ""), "空字串仍然不過濾");
        }

        [Test]
        public void Null_Entry_Never_Matches()
            => Assert.IsFalse(SongCatalog.Matches(null, "anything"));

        [Test]
        public void Query_Is_Trimmed()
            => Assert.IsTrue(SongCatalog.Matches(E("sdom0026k.gn", 10026, "x", "y"), "  10026  "));

        // 編號一律用 InvariantCulture 轉字串 → 就算跑在會把數字塑形成別種字符的 locale 也照樣比對得到。
        [Test]
        public void FileId_Matching_Is_Culture_Invariant()
        {
            var prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("ar-SA");
                Assert.IsTrue(SongCatalog.Matches(E("sdom0026k.gn", 10026, "x", "y"), "10026"));
            }
            finally { Thread.CurrentThread.CurrentCulture = prev; }
        }
    }
}
