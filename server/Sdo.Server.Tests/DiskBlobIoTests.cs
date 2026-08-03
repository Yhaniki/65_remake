using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using Sdo.Osu;
using Sdo.Server.Files;

namespace Sdo.Tests
{
    /// <summary>
    /// blob 倉庫的磁碟層。這裡真的碰檔案(用臨時目錄),因為要守的正是「磁碟上發生的事」:
    /// 檔名怎麼組、hash 對不上時收不收、pack json 存得回來嗎。
    /// </summary>
    public class DiskBlobIoTests
    {
        private string _dir;
        private DiskBlobIo _io;

        private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sdo_blob_" + Path.GetRandomFileName());
            _io = new DiskBlobIo(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }

        private static string PackId(string hex32) => SongPackId.Prefix + hex32;

        [Test]
        public void The_Pack_File_Name_Drops_The_Sha256_Prefix()
        {
            // 🔴 Windows 上冒號是 NTFS alternate data stream 的分隔符,不是「不合法字元」——
            // packs/sha256:abcd.json 會安靜地寫成 packs/sha256 的隱形附屬串流:
            // 寫入成功、GetFiles 看不到、重開 server 之後所有包都「消失」。
            // 開發時 server 就跑在 Windows 上,所以這是實際會踩到的坑。
            var id = PackId("0123456789abcdef0123456789abcdef");
            Assert.AreEqual("0123456789abcdef0123456789abcdef", DiskBlobIo.PackFileName(id));

            // 只看檔名 —— 完整路徑本來就有磁碟機的冒號(C:\…)。
            var name = Path.GetFileName(_io.PackPath(id));
            Assert.AreEqual("0123456789abcdef0123456789abcdef.json", name);
            StringAssert.DoesNotContain(":", name, "檔名裡不能有冒號");
        }

        [Test]
        public void A_Malformed_PackId_Gets_No_Path_At_All()
        {
            // packId 直接變成檔名 → 格式不對就必須在組路徑前擋掉,不能讓它落到檔案系統上。
            Assert.IsNull(DiskBlobIo.PackFileName("../../etc/passwd"));
            Assert.IsNull(_io.PackPath("sha256:短"));
            Assert.IsNull(_io.PackPath(null));
        }

        [Test]
        public void A_Blob_Path_Is_Only_Built_For_A_Real_Sha256()
        {
            // sha 也是 client 送來的,也會變成檔名。
            Assert.IsNotNull(_io.BlobPath(ShaA));
            Assert.IsNull(_io.BlobPath("../../../evil"), "路徑穿越必須擋掉");
            Assert.IsNull(_io.BlobPath("ABCDEF"), "大寫/太短都不算");
            Assert.IsNull(_io.BlobPath(null));
        }

        [Test]
        public void Committing_A_File_Whose_Hash_Does_Not_Match_Is_Refused()
        {
            // 🔴 絕不相信上傳者宣稱的 hash —— 收下來自己重算。對不上就不收,而且暫存要清掉。
            var tmp = Path.Combine(_io.TmpDir, "bogus.bin");
            File.WriteAllText(tmp, "hello");

            Assert.IsFalse(_io.CommitBlob(tmp, ShaA), "hash 不符不能收");
            Assert.IsFalse(File.Exists(tmp), "不收的暫存檔要刪掉,不然那些位元組永遠沒人引用");
            Assert.IsFalse(_io.HasBlob(ShaA));
        }

        [Test]
        public void Committing_A_File_With_The_Right_Hash_Stores_It()
        {
            var tmp = Path.Combine(_io.TmpDir, "good.bin");
            File.WriteAllText(tmp, "hello");
            var sha = DiskBlobIo.HashFile(tmp);

            Assert.IsTrue(_io.CommitBlob(tmp, sha));
            Assert.IsTrue(_io.HasBlob(sha));
            Assert.AreEqual(5, _io.BlobLength(sha));
            Assert.AreEqual("hello", Encoding.UTF8.GetString(_io.ReadBlob(sha)));
            CollectionAssert.Contains(new List<string>(_io.ListBlobShas()), sha);
        }

        [Test]
        public void Committing_The_Same_Content_Twice_Is_A_Dedupe_Hit_Not_An_Error()
        {
            // 內容尋址的重點:同一份檔案第二次上傳不該失敗,也不該存兩份。
            var a = Path.Combine(_io.TmpDir, "a.bin");
            var b = Path.Combine(_io.TmpDir, "b.bin");
            File.WriteAllText(a, "same");
            File.WriteAllText(b, "same");
            var sha = DiskBlobIo.HashFile(a);

            Assert.IsTrue(_io.CommitBlob(a, sha));
            Assert.IsTrue(_io.CommitBlob(b, sha), "第二次是去重命中,要算成功");
            Assert.IsFalse(File.Exists(b), "第二份暫存要清掉");
            Assert.AreEqual(1, new List<string>(_io.ListBlobShas()).Count);
        }

        [Test]
        public void A_Pack_Record_Round_Trips_Through_Disk()
        {
            var id = PackId("11111111111111111111111111111111");
            var pack = new BlobPack { PackId = id, LastUsedUtcMs = 123456789L };
            pack.Files.Add(new PackFileEntry("song.osu", 40, ShaA));
            pack.Files.Add(new PackFileEntry("audio.mp3", 1000, null));

            Assert.IsTrue(_io.SavePack(pack));
            Assert.IsTrue(_io.HasPack(id));

            var back = _io.LoadPack(id);
            Assert.IsNotNull(back);
            Assert.AreEqual(id, back.PackId);
            Assert.AreEqual(123456789L, back.LastUsedUtcMs);
            Assert.AreEqual(2, back.Files.Count);
            Assert.AreEqual(1040, back.TotalBytes);
            Assert.AreEqual("song.osu", back.Files[0].RelPath);
            Assert.AreEqual(ShaA, back.Files[0].Sha256);
        }

        [Test]
        public void Touch_Updates_The_Last_Used_Stamp()
        {
            // 這個時間戳是 TTL 的唯一依據(不能用檔案系統 atime,Linux 常掛 noatime)。
            var id = PackId("22222222222222222222222222222222");
            var pack = new BlobPack { PackId = id, LastUsedUtcMs = 1000 };
            pack.Files.Add(new PackFileEntry("song.osu", 10, ShaA));
            _io.SavePack(pack);

            _io.Touch(id, 999000);
            Assert.AreEqual(999000, _io.LoadPack(id).LastUsedUtcMs);
        }

        [Test]
        public void Pack_Records_List_Each_Sha_Once()
        {
            // 同一個包裡兩個檔內容相同(共用一份 blob)→ 引用清單不該重複,
            // 否則引用計數會虛高,該刪的檔案永遠不會被刪。
            var id = PackId("33333333333333333333333333333333");
            var pack = new BlobPack { PackId = id, LastUsedUtcMs = 5 };
            pack.Files.Add(new PackFileEntry("a.osu", 10, ShaA));
            pack.Files.Add(new PackFileEntry("b.osu", 10, ShaA));
            _io.SavePack(pack);

            var recs = _io.ListPackRecords();
            Assert.AreEqual(1, recs.Count);
            Assert.AreEqual(1, recs[0].Shas.Length);
            Assert.AreEqual(20, recs[0].TotalBytes);
        }

        [Test]
        public void Leftover_Upload_Temp_Dirs_Are_Cleared()
        {
            // 上一次執行被 kill 掉留下的半個上傳:沒有任何 pack 引用它們,不清就是永久佔用。
            Directory.CreateDirectory(Path.Combine(_io.TmpDir, "77"));
            File.WriteAllText(Path.Combine(_io.TmpDir, "77", "half.osu"), "partial");

            Assert.AreEqual(1, _io.ClearTemp());
            CollectionAssert.IsEmpty(Directory.GetDirectories(_io.TmpDir));
        }

        [Test]
        public void The_Janitor_Deletes_An_Expired_Pack_And_Its_Blob_From_Disk()
        {
            // 端到端(在真磁碟上)驗一次 M5 的驗收條件:
            // 「改 packs/*.json 的 lastUsedUtc 到 25 h 前 → janitor 刪掉」。
            var tmp = Path.Combine(_io.TmpDir, "x.bin");
            File.WriteAllText(tmp, "content");
            var sha = DiskBlobIo.HashFile(tmp);
            Assert.IsTrue(_io.CommitBlob(tmp, sha));

            long now = 1_700_000_000_000L;
            var id = PackId("44444444444444444444444444444444");
            var pack = new BlobPack { PackId = id, LastUsedUtcMs = now - 25L * 3600L * 1000L };
            pack.Files.Add(new PackFileEntry("song.osu", 7, sha));
            _io.SavePack(pack);

            var janitor = new BlobJanitor(_io, 24, 0, now - BlobJanitor.SweepIntervalMs);
            Assert.IsTrue(janitor.Due(now), "已經過了一個間隔,該掃了");

            var r = janitor.Sweep(now, new HashSet<string>());
            Assert.AreEqual(1, r.PacksDeleted);
            Assert.AreEqual(1, r.BlobsDeleted);
            Assert.IsFalse(_io.HasPack(id));
            Assert.IsFalse(_io.HasBlob(sha));
            Assert.AreEqual(0, r.UsedBytesAfter);
        }

        [Test]
        public void The_Janitor_Does_Not_Sweep_Immediately_At_Boot()
        {
            // 🔴 開機那一刻還沒有任何房間 → pinned 集合是空的。這時候掃會把上一輪還在用的包
            //    全部當成沒人要的刪掉(server 重啟一次,所有人剛下載好的歌就沒了)。
            long now = 1_700_000_000_000L;
            var janitor = new BlobJanitor(_io, 24, 0, now);
            Assert.IsFalse(janitor.Due(now), "剛開機不該馬上掃");
            Assert.IsTrue(janitor.Due(now + BlobJanitor.SweepIntervalMs), "等一個間隔之後才掃");
        }

        [Test]
        public void The_Janitor_Keeps_A_Pinned_Pack_On_Disk()
        {
            long now = 1_700_000_000_000L;
            var id = PackId("55555555555555555555555555555555");
            var pack = new BlobPack { PackId = id, LastUsedUtcMs = now - 99L * 3600L * 1000L };
            pack.Files.Add(new PackFileEntry("song.osu", 7, ShaA));
            _io.SavePack(pack);

            var janitor = new BlobJanitor(_io, 24, 0, now - BlobJanitor.SweepIntervalMs);
            var r = janitor.Sweep(now, new HashSet<string> { id });

            Assert.AreEqual(0, r.PacksDeleted);
            Assert.IsTrue(_io.HasPack(id), "房間正在用的歌再舊也不能刪");
        }
    }
}
