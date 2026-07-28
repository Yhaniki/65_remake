using System.IO;
using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// 「把歌曲資料夾掃成清單」這一層 —— packId 與上傳清單都建在它上面。
    ///
    /// 這裡守的重點是**穩定性**:同一個資料夾在不同電腦、不同路徑、不同大小寫下必須算出同一個 packId,
    /// 否則「你有沒有這首歌」永遠比不對,缺歌傳檔整套就沒有意義。
    /// </summary>
    public class SongPackScanTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sdo_pack_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }

        private void Write(string rel, string content)
        {
            var p = Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, content);
        }

        [Test]
        public void Chart_Files_Get_A_Content_Hash_But_Audio_Does_Not()
        {
            // 音檔不算 SHA-256 是刻意的:大歌庫幾 GB,開機掃描不可能讀完。
            // (檔名, 長度) 對「這是不是同一個資料夾」已經夠強,下載完還會逐檔驗 hash。
            Write("song.osu", "[General]\nTitle: x\n");
            Write("audio.mp3", new string('a', 500));

            var files = SongPackScan.Enumerate(_dir, hashEverything: false);
            Assert.AreEqual(2, files.Count, "兩個檔都該列進來");
            foreach (var f in files)
            {
                if (f.RelPath.EndsWith(".osu")) Assert.IsNotNull(f.Sha256, "譜面一定要有 hash");
                else Assert.IsNull(f.Sha256, "音檔不該算 hash(那是開機掃描的成本殺手)");
            }
        }

        [Test]
        public void Everything_Gets_Hashed_When_Asked()
        {
            // 要上傳時每個檔都要 hash,收端才能逐檔驗證。
            Write("song.osu", "chart");
            Write("audio.mp3", "audio");
            foreach (var f in SongPackScan.Enumerate(_dir, hashEverything: true))
                Assert.IsNotNull(f.Sha256, f.RelPath + " 上傳清單裡每個檔都要有 hash");
        }

        [Test]
        public void Videos_And_Executables_Are_Excluded()
        {
            // 需求:影片自動過濾(而且它們是傳檔量最大的東西)。執行檔更不能傳。
            Write("song.osu", "chart");
            Write("bg.mp4", "video");
            Write("evil.exe", "MZ");
            Write("pack.zip", "PK");

            var files = SongPackScan.Enumerate(_dir, hashEverything: false);
            CollectionAssert.AreEquivalent(new[] { "song.osu" }, Names(files),
                "只有譜面該留下 —— 影片/執行檔/壓縮檔都要被擋掉");
        }

        [Test]
        public void Generated_Files_Are_Excluded()
        {
            // 收端自己會重新生成(sdoinfo.dat / cd*.png / dance*.dps),傳過去只是浪費。
            Write("song.osu", "chart");
            Write("sdoinfo.dat", "generated");
            var files = SongPackScan.Enumerate(_dir, hashEverything: false);
            CollectionAssert.AreEquivalent(new[] { "song.osu" }, Names(files));
        }

        [Test]
        public void One_Subfolder_Deep_Is_Included()
        {
            Write("song.osu", "chart");
            Write("sub/extra.osu", "chart2");
            var files = SongPackScan.Enumerate(_dir, hashEverything: false);
            CollectionAssert.AreEquivalent(new[] { "song.osu", "sub/extra.osu" }, Names(files),
                "歌曲資料夾本身 + 一層子夾都要算進來");
        }

        [Test]
        public void The_Same_Folder_Moved_Or_Renamed_Keeps_Its_PackId()
        {
            // 🔴 這是整個缺歌機制的地基:packId 只能看**內容**,不能看它放在哪裡。
            // 現有的歌曲編號是「絕對路徑的 hash」→ 換台電腦完全不同,絕不能拿來比對。
            Write("song.osu", "chart");
            Write("audio.mp3", "audio");
            var a = SongPackScan.Compute(_dir);
            Assert.IsNotNull(a);

            var moved = _dir + "_moved";
            Directory.Move(_dir, moved);
            try
            {
                Assert.AreEqual(a, SongPackScan.Compute(moved), "搬了位置 packId 不該變");
            }
            finally
            {
                Directory.Move(moved, _dir);   // 交還給 TearDown 刪
            }
        }

        [Test]
        public void Adding_Or_Removing_A_Video_Does_Not_Change_The_PackId()
        {
            // 影片本來就不在傳輸清單裡 → 有沒有它都是「同一首歌」。
            // 不然一邊有影片一邊沒有就會被當成兩首不同的歌,永遠互相認為對方缺歌。
            Write("song.osu", "chart");
            var before = SongPackScan.Compute(_dir);
            Write("bg.mp4", "video");
            Assert.AreEqual(before, SongPackScan.Compute(_dir));
        }

        [Test]
        public void Editing_The_Chart_Changes_The_PackId()
        {
            // 譜改了就是另一份譜(它有算 SHA-256)—— 否則會拿到一份對不上的譜面。
            Write("song.osu", "chart v1");
            var before = SongPackScan.Compute(_dir);
            Write("song.osu", "chart v2");
            Assert.AreNotEqual(before, SongPackScan.Compute(_dir));
        }

        [Test]
        public void Changing_Only_The_Audio_Length_Changes_The_PackId()
        {
            // 音檔沒算 hash,但**長度**有進 manifest → 換了音檔還是認得出來。
            Write("song.osu", "chart");
            Write("audio.mp3", "short");
            var before = SongPackScan.Compute(_dir);
            Write("audio.mp3", "a much longer audio file");
            Assert.AreNotEqual(before, SongPackScan.Compute(_dir), "音檔長度變了應該算不同的包");
        }

        [Test]
        public void An_Empty_Or_Missing_Folder_Has_No_PackId()
        {
            Assert.IsNull(SongPackScan.Compute(_dir), "空資料夾沒有 packId");
            Assert.IsNull(SongPackScan.Compute(Path.Combine(_dir, "nope")));
            Assert.IsNull(SongPackScan.Compute(null));
            CollectionAssert.IsEmpty(SongPackScan.Enumerate(null, false));
        }

        [Test]
        public void PackId_Is_Well_Formed()
        {
            Write("song.osu", "chart");
            Assert.IsTrue(SongPackId.IsWellFormed(SongPackScan.Compute(_dir)));
        }

        private static string[] Names(System.Collections.Generic.List<PackFileEntry> files)
        {
            var a = new string[files.Count];
            for (int i = 0; i < files.Count; i++) a[i] = files[i].RelPath;
            return a;
        }
    }
}
