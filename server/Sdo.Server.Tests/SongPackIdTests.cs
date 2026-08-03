using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// packId —— 外部歌在網路上的唯一身份。
    ///
    /// 這組測試守的是幾個「錯了就會很難查」的性質:
    ///   • 同一份歌搬到別的路徑/改大小寫 → **同一個 packId**(否則每個人的歌庫路徑不同,
    ///     就永遠判定成缺歌並互相重傳)
    ///   • 改一張譜 → **packId 一定要變**(否則對方拿到舊譜，判定全錯而且查不出原因)
    ///   • 加/刪影片檔 → **packId 不變**(影片被過濾掉了,不該影響身份)
    ///   • 檔案列舉順序不影響結果(檔案系統的順序不保證穩定)
    /// </summary>
    public class SongPackIdTests
    {
        private string _tmp;

        [SetUp]
        public void MakeTempDir()
        {
            _tmp = Path.Combine(Path.GetTempPath(), "sdo_packid_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tmp);
        }

        [TearDown]
        public void RemoveTempDir()
        {
            try { if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true); } catch { }
        }

        private string Folder(string name)
        {
            var p = Path.Combine(_tmp, name);
            Directory.CreateDirectory(p);
            return p;
        }

        private static void WriteFile(string folder, string relPath, string content)
        {
            var full = Path.Combine(folder, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content, new UTF8Encoding(false));
        }

        private static void WriteBytes(string folder, string relPath, int byteCount, byte fill)
        {
            var full = Path.Combine(folder, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            var buf = new byte[byteCount];
            for (int i = 0; i < byteCount; i++) buf[i] = fill;
            File.WriteAllBytes(full, buf);
        }

        /// <summary>建一個典型的 osu 風格歌曲資料夾。</summary>
        private string MakeTypicalSong(string folderName)
        {
            var f = Folder(folderName);
            WriteFile(f, "song [Easy].osu", "osu file format v14\n[HitObjects]\n256,192,1000,1,0\n");
            WriteFile(f, "song [Hard].osu", "osu file format v14\n[HitObjects]\n256,192,500,1,0\n");
            WriteBytes(f, "audio.mp3", 4096, 0xAA);
            WriteBytes(f, "bg.jpg", 2048, 0xBB);
            return f;
        }

        // ---- 基本 ----

        [Test]
        public void Well_Formed_Id_Has_Prefix_And_Fixed_Length()
        {
            var f = MakeTypicalSong("a");
            var id = SongPackId.ForFolder(f);

            Assert.IsTrue(id.StartsWith(SongPackId.Prefix), id);
            Assert.AreEqual(SongPackId.Prefix.Length + SongPackId.HexChars, id.Length, id);
            Assert.IsTrue(SongPackId.IsWellFormed(id), id);
        }

        [Test]
        public void Empty_Folder_Yields_Empty_Id()
        {
            // 沒有可傳的檔案 → 沒有身份。呼叫端要能分辨這種情況(不能拿空字串當成合法 id)。
            Assert.AreEqual("", SongPackId.ForFolder(Folder("empty")));
            Assert.IsFalse(SongPackId.IsWellFormed(""));
        }

        [Test]
        public void Folder_With_Only_Filtered_Files_Yields_Empty_Id()
        {
            var f = Folder("onlyvideo");
            WriteBytes(f, "movie.mp4", 1024, 0x01);
            WriteFile(f, "readme.txt", "hi");
            Assert.AreEqual("", SongPackId.ForFolder(f));
        }

        [Test]
        public void Missing_Folder_Yields_Empty_Id()
        {
            Assert.AreEqual("", SongPackId.ForFolder(Path.Combine(_tmp, "nope")));
            Assert.AreEqual("", SongPackId.ForFolder(null));
            Assert.AreEqual("", SongPackId.ForFolder(""));
        }

        // ---- 🔴 跨機器一致性 ----

        [Test]
        public void Same_Content_In_A_Different_Folder_Name_Gives_The_Same_Id()
        {
            // 這是整個設計的核心:別人的歌庫路徑一定跟你不一樣。
            // (現有的 ExternalSongLibrary.gn 就是把絕對路徑餵進 FNV，所以完全不能用。)
            var a = MakeTypicalSong("here");
            var b = MakeTypicalSong("somewhere_else_entirely");

            Assert.AreEqual(SongPackId.ForFolder(a), SongPackId.ForFolder(b));
        }

        [Test]
        public void Filename_Case_Differences_Do_Not_Change_The_Id()
        {
            // Windows 的檔案系統大小寫不敏感 —— 同一份歌在兩台機器上可能是 BGM.ogg / bgm.ogg。
            var a = Folder("case_a");
            WriteFile(a, "song.osu", "chart");
            WriteBytes(a, "audio.mp3", 100, 1);

            var b = Folder("case_b");
            WriteFile(b, "SONG.osu", "chart");
            WriteBytes(b, "AUDIO.MP3", 100, 1);

            Assert.AreEqual(SongPackId.ForFolder(a), SongPackId.ForFolder(b));
        }

        [Test]
        public void Modification_Times_Do_Not_Affect_The_Id()
        {
            // 對比 ExternalScanCache.Signature —— 它刻意包含 mtime(那是給「本機檔案有沒有變」用的),
            // 但 mtime 一複製就變,所以絕不能進 packId。
            var a = MakeTypicalSong("mtime_a");
            var b = MakeTypicalSong("mtime_b");

            var stamp = new System.DateTime(2001, 2, 3, 4, 5, 6, System.DateTimeKind.Utc);
            foreach (var file in Directory.GetFiles(b)) File.SetLastWriteTimeUtc(file, stamp);

            Assert.AreEqual(SongPackId.ForFolder(a), SongPackId.ForFolder(b));
        }

        // ---- 🔴 該變的時候要變 ----

        [Test]
        public void Editing_A_Chart_Changes_The_Id()
        {
            // 最重要的一條。改幾個 note 的位置很可能不改變檔案長度,所以譜面非算內容 hash 不可 ——
            // 沒有這條保護，對方會拿著舊譜跟你打同一首歌，判定全錯而且完全查不出原因。
            var a = Folder("chart_a");
            WriteFile(a, "song.osu", "osu file format v14\n[HitObjects]\n256,192,1000,1,0\n");
            WriteBytes(a, "audio.mp3", 100, 1);
            var id1 = SongPackId.ForFolder(a);

            // 同樣長度、只改一個字元(1000 → 1001)。
            WriteFile(a, "song.osu", "osu file format v14\n[HitObjects]\n256,192,1001,1,0\n");
            var id2 = SongPackId.ForFolder(a);

            Assert.AreNotEqual(id1, id2, "改譜面內容一定要換 packId,即使長度沒變");
        }

        [Test]
        public void Adding_Or_Removing_A_Chart_Changes_The_Id()
        {
            var a = MakeTypicalSong("charts");
            var id1 = SongPackId.ForFolder(a);

            WriteFile(a, "song [Insane].osu", "osu file format v14\n[HitObjects]\n1,2,3,1,0\n");
            var id2 = SongPackId.ForFolder(a);
            Assert.AreNotEqual(id1, id2);

            File.Delete(Path.Combine(a, "song [Insane].osu"));
            Assert.AreEqual(id1, SongPackId.ForFolder(a), "刪回去應該回到原本的 id");
        }

        [Test]
        public void Changing_Audio_Length_Changes_The_Id()
        {
            var a = MakeTypicalSong("audiolen");
            var id1 = SongPackId.ForFolder(a);

            WriteBytes(a, "audio.mp3", 8192, 0xAA);   // 長度變了
            Assert.AreNotEqual(id1, SongPackId.ForFolder(a));
        }

        [Test]
        public void Changing_Audio_Content_At_The_Same_Length_Does_Not_Change_The_Id()
        {
            // ⚠️ 這是**刻意的成本取捨**,把它測出來當文件。
            // 音檔不算內容 hash,因為一個大歌庫的音檔是好幾 GB,每次開機掃描全讀會慢到無法接受。
            // 後果:換了音檔但長度剛好一樣時,packId 不變 → 對方以為你有這首歌。
            // 但這不會拿到壞檔:下載端會逐檔驗 SHA-256(傳輸 manifest 帶了每個檔的完整 hash),
            // 不符就重新取。代價只是多花一次流量。
            var a = MakeTypicalSong("audiocontent");
            var id1 = SongPackId.ForFolder(a);

            WriteBytes(a, "audio.mp3", 4096, 0xCC);   // 同長度,不同內容
            Assert.AreEqual(id1, SongPackId.ForFolder(a),
                "音檔內容不進 packId —— 這是刻意的;逐檔 SHA-256 驗證會在下載時抓到不符");
        }

        // ---- 🔴 過濾掉的檔不該影響身份 ----

        [Test]
        public void Adding_A_Video_Does_Not_Change_The_Id()
        {
            // 影片被過濾掉,所以有沒有它都是「同一首歌」。
            // 這條很實際:同一首 osu 圖，有人下載了帶影片的版本、有人沒有 —— 他們該能一起玩。
            var a = MakeTypicalSong("video");
            var id1 = SongPackId.ForFolder(a);

            WriteBytes(a, "bg.mp4", 1024 * 1024, 0x77);
            Assert.AreEqual(id1, SongPackId.ForFolder(a));
        }

        [Test]
        public void Adding_Generated_Artifacts_Does_Not_Change_The_Id()
        {
            // 播過一次歌之後遊戲會生出 CD 圖與舞蹈 —— 那不該讓這首歌變成「另一首歌」。
            var a = MakeTypicalSong("generated");
            var id1 = SongPackId.ForFolder(a);

            WriteFile(a, "sdoinfo.dat", "#SONG:x;");
            WriteBytes(a, "cd.png", 500, 0x11);
            WriteBytes(a, "dance.dps", 500, 0x22);
            WriteBytes(a, "cd_slug_1a2b.png", 500, 0x33);
            WriteBytes(a, "dance_slug.dps", 500, 0x44);

            Assert.AreEqual(id1, SongPackId.ForFolder(a));
        }

        [Test]
        public void Adding_An_Unknown_File_Type_Does_Not_Change_The_Id()
        {
            var a = MakeTypicalSong("unknown");
            var id1 = SongPackId.ForFolder(a);

            WriteFile(a, "readme.txt", "hello");
            Assert.AreEqual(id1, SongPackId.ForFolder(a));
        }

        // ---- manifest 的性質 ----

        [Test]
        public void Manifest_Is_Order_Independent()
        {
            // 檔案系統的列舉順序不保證穩定(同一顆硬碟重開機都可能不同),
            // 所以 manifest 一定要排序 —— 不然同一個資料夾會算出不同的 packId。
            var f1 = new List<PackFileEntry>
            {
                new PackFileEntry("a.osu", 10, "aa"),
                new PackFileEntry("b.mp3", 20, ""),
                new PackFileEntry("c.png", 30, ""),
            };
            var f2 = new List<PackFileEntry>
            {
                new PackFileEntry("c.png", 30, ""),
                new PackFileEntry("a.osu", 10, "aa"),
                new PackFileEntry("b.mp3", 20, ""),
            };

            Assert.AreEqual(SongPackId.BuildManifest(f1), SongPackId.BuildManifest(f2));
            Assert.AreEqual(SongPackId.Compute(f1), SongPackId.Compute(f2));
        }

        [Test]
        public void Manifest_Ignores_Non_Chart_Hashes_Even_If_Supplied()
        {
            // 上傳流程會算「每個檔」的 hash,開機掃描只算譜面的 —— 兩邊必須得到同一個 packId。
            // 所以 BuildManifest 對非譜面檔一律無視傳進來的 hash。
            var withAllHashes = new List<PackFileEntry>
            {
                new PackFileEntry("a.osu", 10, "chartsha"),
                new PackFileEntry("b.mp3", 20, "audiosha"),
            };
            var chartOnly = new List<PackFileEntry>
            {
                new PackFileEntry("a.osu", 10, "chartsha"),
                new PackFileEntry("b.mp3", 20, ""),
            };

            Assert.AreEqual(SongPackId.Compute(chartOnly), SongPackId.Compute(withAllHashes));
        }

        [Test]
        public void Manifest_Is_Case_Normalized_And_Separator_Normalized()
        {
            var a = new List<PackFileEntry> { new PackFileEntry("SB\\Overlay.PNG", 10, "") };
            var b = new List<PackFileEntry> { new PackFileEntry("sb/overlay.png", 10, "") };
            Assert.AreEqual(SongPackId.Compute(a), SongPackId.Compute(b));
        }

        [Test]
        public void Empty_Manifest_Yields_Empty_Id()
        {
            Assert.AreEqual("", SongPackId.FromManifest(""));
            Assert.AreEqual("", SongPackId.FromManifest(null));
            Assert.AreEqual("", SongPackId.Compute(new List<PackFileEntry>()));
            Assert.AreEqual("", SongPackId.Compute(null));
        }

        [Test]
        public void NeedsContentHash_Covers_Charts_Only()
        {
            Assert.IsTrue(SongPackId.NeedsContentHash("a.osu"));
            Assert.IsTrue(SongPackId.NeedsContentHash("a.sm"));
            Assert.IsTrue(SongPackId.NeedsContentHash("a.gn"));
            Assert.IsTrue(SongPackId.NeedsContentHash("a.mc"));
            Assert.IsTrue(SongPackId.NeedsContentHash("sdo_pack.tsv"), "歌包索引決定歌名/seed,改了要換 id");

            Assert.IsFalse(SongPackId.NeedsContentHash("a.mp3"), "音檔太大,只用 (檔名,長度)");
            Assert.IsFalse(SongPackId.NeedsContentHash("a.png"));
        }

        // ---- 掃描 ----

        [Test]
        public void ScanFolder_Reports_What_It_Skipped()
        {
            // host 要能看到「跳過 2 個影片(共 3 MB)」這種回報。
            var f = MakeTypicalSong("stats");
            WriteBytes(f, "bg.mp4", 1024 * 1024, 1);
            WriteBytes(f, "bg2.avi", 2 * 1024 * 1024, 1);
            WriteFile(f, "readme.txt", "x");
            WriteFile(f, "sdoinfo.dat", "#SONG:x;");
            WriteBytes(f, "tool.exe", 100, 1);

            List<PackFileEntry> files;
            PackScanStats stats;
            Assert.IsTrue(SongPackId.ScanFolder(f, false, out files, out stats));

            Assert.AreEqual(4, stats.IncludedFiles, "2 譜 + 音檔 + 圖");
            Assert.AreEqual(2, stats.SkippedVideos);
            Assert.AreEqual(3 * 1024 * 1024, stats.SkippedVideoBytes);
            Assert.AreEqual(1, stats.SkippedUnknown);
            Assert.AreEqual(1, stats.SkippedGenerated);
            Assert.AreEqual(1, stats.SkippedExecutables);
        }

        [Test]
        public void ScanFolder_Hashes_Only_Charts_When_Not_Asked_For_All()
        {
            var f = MakeTypicalSong("hashmode");

            List<PackFileEntry> cheap;
            PackScanStats s1;
            Assert.IsTrue(SongPackId.ScanFolder(f, false, out cheap, out s1));
            foreach (var e in cheap)
            {
                if (SongPackId.NeedsContentHash(e.RelPath))
                    Assert.AreNotEqual("", e.Sha256, e.RelPath + " 是譜面,應該有 hash");
                else
                    Assert.AreEqual("", e.Sha256, e.RelPath + " 不是譜面,便宜模式不該讀它的內容");
            }

            List<PackFileEntry> full;
            PackScanStats s2;
            Assert.IsTrue(SongPackId.ScanFolder(f, true, out full, out s2));
            foreach (var e in full)
                Assert.AreNotEqual("", e.Sha256, e.RelPath + " 上傳模式下每個檔都要有 hash");
        }

        [Test]
        public void ScanFolder_Picks_Up_One_Level_Subfolders()
        {
            var f = MakeTypicalSong("subdir");
            WriteBytes(f, "sb/overlay.png", 256, 0x5A);

            List<PackFileEntry> files;
            PackScanStats stats;
            Assert.IsTrue(SongPackId.ScanFolder(f, false, out files, out stats));

            bool found = false;
            foreach (var e in files) if (e.RelPath == "sb/overlay.png") found = true;
            Assert.IsTrue(found, "一層子夾的檔案要收進來");
        }

        [Test]
        public void RelPaths_Are_Normalized_To_Forward_Slashes()
        {
            // wire 上的路徑一律 '/' —— 收端可能是 Linux。
            var f = MakeTypicalSong("slashes");
            WriteBytes(f, "sb/overlay.png", 256, 1);

            List<PackFileEntry> files;
            PackScanStats stats;
            Assert.IsTrue(SongPackId.ScanFolder(f, false, out files, out stats));

            foreach (var e in files)
                Assert.IsFalse(e.RelPath.Contains("\\"), "相對路徑不該含反斜線:" + e.RelPath);
        }

        // ---- 格式驗證(server 收到 client 送來的字串要驗) ----

        [Test]
        public void IsWellFormed_Rejects_Junk()
        {
            var good = SongPackId.ForFolder(MakeTypicalSong("wf"));
            Assert.IsTrue(SongPackId.IsWellFormed(good));

            Assert.IsFalse(SongPackId.IsWellFormed(null));
            Assert.IsFalse(SongPackId.IsWellFormed(""));
            Assert.IsFalse(SongPackId.IsWellFormed("deadbeef"), "缺前綴");
            Assert.IsFalse(SongPackId.IsWellFormed("sha256:short"), "長度不對");
            Assert.IsFalse(SongPackId.IsWellFormed("sha256:" + new string('g', SongPackId.HexChars)), "不是 hex");
            Assert.IsFalse(SongPackId.IsWellFormed("sha256:" + new string('A', SongPackId.HexChars)), "大寫 hex 不接受(我們一律小寫)");
            Assert.IsFalse(SongPackId.IsWellFormed("md5:" + new string('a', SongPackId.HexChars)));
        }

        [Test]
        public void HashFile_Returns_Empty_For_Missing_File()
        {
            Assert.AreEqual("", SongPackId.HashFile(Path.Combine(_tmp, "nope.osu")));
        }

        [Test]
        public void HashFile_Is_Stable_And_Full_Length()
        {
            var f = Folder("hashfile");
            WriteFile(f, "a.osu", "content");
            var p = Path.Combine(f, "a.osu");

            var h1 = SongPackId.HashFile(p);
            var h2 = SongPackId.HashFile(p);

            Assert.AreEqual(h1, h2);
            Assert.AreEqual(64, h1.Length, "傳輸 manifest 用的是全長 SHA-256");
        }
    }
}
