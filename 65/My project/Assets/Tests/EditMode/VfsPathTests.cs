using NUnit.Framework;
using Sdo.Settings.Vfs;

namespace Sdo.Tests
{
    /// <summary>VfsPath 的純函式:正規化、reserved 判定、查表雜湊、萬用字元比對。
    /// 這些是 pak 索引鍵的定義,打包器(Python)必須產出一模一樣的結果 ——
    /// 規格見 docs/architecture/data-packaging.md §4.1。</summary>
    public class VfsPathTests
    {
        // ---------------- Normalize ----------------

        [Test]
        public void Normalize_BackslashBecomesForwardSlash()
        {
            Assert.AreEqual("UI/GAMEPLAY/x.png", VfsPath.Normalize(@"UI\GAMEPLAY\x.png"));
        }

        [Test]
        public void Normalize_KeepsCase()
        {
            // 正規化不動大小寫 —— 真實檔案系統那層要拿它去拼路徑。大小寫不敏感是 Hash 的事。
            Assert.AreEqual("Ui/GamePlay/X.Png", VfsPath.Normalize("Ui/GamePlay/X.Png"));
        }

        [Test]
        public void Normalize_DropsLeadingAndDuplicateAndTrailingSlashes()
        {
            Assert.AreEqual("A/B", VfsPath.Normalize("/A//B/"));
            Assert.AreEqual("A/B", VfsPath.Normalize("./A/./B"));
        }

        [Test]
        public void Normalize_FoldsDotDot()
        {
            Assert.AreEqual("A/C", VfsPath.Normalize("A/B/../C"));
            Assert.AreEqual("", VfsPath.Normalize("A/.."));
        }

        [Test]
        public void Normalize_EscapingRootIsNull()
        {
            // pak 內一條 ../../windows/system32/… 就能讓解包寫到任意位置 —— 一定要在這裡擋掉。
            Assert.IsNull(VfsPath.Normalize(".."));
            Assert.IsNull(VfsPath.Normalize("A/../.."));
            Assert.IsNull(VfsPath.Normalize(@"..\..\windows\system32\drivers\etc\hosts"));
        }

        [Test]
        public void Normalize_RootedOrStreamPathIsNull()
        {
            Assert.IsNull(VfsPath.Normalize(@"C:\Windows"));
            Assert.IsNull(VfsPath.Normalize("x.png:Zone.Identifier"));
        }

        [Test]
        public void Normalize_NullOrEmptyIsNull()
        {
            Assert.IsNull(VfsPath.Normalize(null));
            Assert.IsNull(VfsPath.Normalize(""));
        }

        [Test]
        public void Normalize_DotIsRoot()
        {
            Assert.AreEqual("", VfsPath.Normalize("."));
            Assert.AreEqual("", VfsPath.Normalize("/"));
        }

        // ---------------- IsReserved ----------------

        [Test]
        public void IsReserved_AllFourRoots()
        {
            Assert.IsTrue(VfsPath.IsReserved("PROFILE"));
            Assert.IsTrue(VfsPath.IsReserved("ADDON/SONG/foo/bar.osu"));
            Assert.IsTrue(VfsPath.IsReserved("CACHE/external_song_cache.json"));
            Assert.IsTrue(VfsPath.IsReserved("REPLAY/2026-08-05.rpy"));
        }

        [Test]
        public void IsReserved_CaseInsensitive()
        {
            Assert.IsTrue(VfsPath.IsReserved("profile/00000000/profile.json"));
            Assert.IsTrue(VfsPath.IsReserved("Addon/MODEL"));
        }

        [Test]
        public void IsReserved_OnlyWholeFirstSegment()
        {
            Assert.IsFalse(VfsPath.IsReserved("AVATAR/x.dds"));
            Assert.IsFalse(VfsPath.IsReserved("PROFILES/x"));          // 前綴相同但不是同一段
            Assert.IsFalse(VfsPath.IsReserved("UI/PROFILE/x.png"));    // 不在第一段
            Assert.IsFalse(VfsPath.IsReserved(""));
            Assert.IsFalse(VfsPath.IsReserved(null));
        }

        // ---------------- Hash ----------------

        [Test]
        public void Hash_IsCaseInsensitiveForAscii()
        {
            // NTFS 大小寫不敏感,程式碼裡對同一個檔大小寫混用 —— 查表必須收斂到同一個鍵。
            Assert.AreEqual(VfsPath.Hash("AVATAR/FEMALE.HRC"), VfsPath.Hash("avatar/female.hrc"));
            Assert.AreEqual(VfsPath.Hash("Ui/GamePlay/X.Png"), VfsPath.Hash("UI/GAMEPLAY/X.PNG"));
        }

        [Test]
        public void Hash_DistinguishesDifferentPaths()
        {
            Assert.AreNotEqual(VfsPath.Hash("AVATAR/A.DDS"), VfsPath.Hash("AVATAR/B.DDS"));
            Assert.AreNotEqual(VfsPath.Hash("A/B"), VfsPath.Hash("AB"));
        }

        [Test]
        public void Hash_NonAsciiIsNotCaseFolded()
        {
            // 只轉 ASCII 的 a-z:UTF-8 續接位元組都 >= 0x80,不該被 -32 動到。
            Assert.AreEqual(VfsPath.Hash("歌/曲.gn"), VfsPath.Hash("歌/曲.GN"));
            Assert.AreNotEqual(VfsPath.Hash("歌/曲.gn"), VfsPath.Hash("歌/曲2.gn"));
        }

        [Test]
        public void Hash_KnownVector()
        {
            // 釘死演算法(FNV-1a 64,對 ASCII 大寫後的 UTF-8)—— 打包器要產出同樣的值。
            // 空字串 = FNV offset basis。
            Assert.AreEqual(14695981039346656037UL, VfsPath.Hash(""));
            Assert.AreEqual(VfsPath.Hash("A"), VfsPath.Hash("a"));
        }

        // ---------------- GlobMatch ----------------

        [Test]
        public void GlobMatch_Extension()
        {
            Assert.IsTrue(VfsPath.GlobMatch("body.dds", "*.dds"));
            Assert.IsTrue(VfsPath.GlobMatch("BODY.DDS", "*.dds"));   // 大小寫不敏感
            Assert.IsFalse(VfsPath.GlobMatch("body.png", "*.dds"));
        }

        [Test]
        public void GlobMatch_QuestionMark()
        {
            Assert.IsTrue(VfsPath.GlobMatch("a.png", "?.png"));
            Assert.IsFalse(VfsPath.GlobMatch("ab.png", "?.png"));
        }

        [Test]
        public void GlobMatch_MatchAllForms()
        {
            // "*.*" 要跟 .NET Directory.GetFiles 一樣連沒有副檔名的檔也收 —— 呼叫端搬過來不該少拿檔。
            Assert.IsTrue(VfsPath.GlobMatch("noextension", "*.*"));
            Assert.IsTrue(VfsPath.GlobMatch("noextension", "*"));
            Assert.IsTrue(VfsPath.GlobMatch("noextension", null));
        }

        [Test]
        public void GlobMatch_MultipleStarsBacktrack()
        {
            Assert.IsTrue(VfsPath.GlobMatch("wdance0012_a.mot", "wdance*_*.mot"));
            Assert.IsFalse(VfsPath.GlobMatch("wdance0012.mot", "wdance*_*.mot"));
        }

        // ---------------- FileName / IsUnder ----------------

        [Test]
        public void FileName_LastSegment()
        {
            Assert.AreEqual("x.png", VfsPath.FileName("UI/GAMEPLAY/x.png"));
            Assert.AreEqual("x.png", VfsPath.FileName("x.png"));
        }

        [Test]
        public void IsUnder_RecursiveVsDirectChildren()
        {
            Assert.IsTrue(VfsPath.IsUnder("UI/GAMEPLAY/x.png", "UI", true));
            Assert.IsFalse(VfsPath.IsUnder("UI/GAMEPLAY/x.png", "UI", false));  // 不是直接子項
            Assert.IsTrue(VfsPath.IsUnder("UI/x.png", "UI", false));
        }

        [Test]
        public void IsUnder_EmptyDirIsRoot()
        {
            Assert.IsTrue(VfsPath.IsUnder("UI/GAMEPLAY/x.png", "", true));
            Assert.IsFalse(VfsPath.IsUnder("UI/GAMEPLAY/x.png", "", false));
            Assert.IsTrue(VfsPath.IsUnder("x.png", "", false));
        }

        [Test]
        public void IsUnder_PrefixIsNotEnough()
        {
            Assert.IsFalse(VfsPath.IsUnder("UICOMMON/x.png", "UI", true));   // 前綴像但不是同一段
            Assert.IsFalse(VfsPath.IsUnder("UI", "UI", true));               // 自己不算在自己底下
        }

        [Test]
        public void IsUnder_CaseInsensitive()
        {
            Assert.IsTrue(VfsPath.IsUnder("ui/gameplay/x.png", "UI/GAMEPLAY", false));
        }
    }
}
