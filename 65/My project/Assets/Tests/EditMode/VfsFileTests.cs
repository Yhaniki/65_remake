using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Sdo.Settings;
using Sdo.Settings.Vfs;

namespace Sdo.Tests
{
    /// <summary>VfsFile —— 「路徑在 DATA 樹底下就走 VFS，否則走真實檔案系統」的門面。
    ///
    /// 這是 275 個 IO 呼叫點的遷移工具：改成 VfsFile 之後語意不變（散裝層就是真實檔案系統），
    /// 但 pak 掛上去時同一段程式碼會自動改讀 pak。這裡釘死的就是那個「不變」。</summary>
    public class VfsFileTests
    {
        private string _root;
        private string _savedRoot;

        [SetUp]
        public void SetUp()
        {
            _savedRoot = SdoDataRoot.Root;
            _root = Path.Combine(Path.GetTempPath(), "sdo_vfsfile_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            SdoDataRoot.Root = _root;
            SdoVfs.Initialise(_root);
        }

        [TearDown]
        public void TearDown()
        {
            SdoVfs.Reset();
            SdoDataRoot.Root = _savedRoot;
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        private string Write(string rel, string content)
        {
            var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content, new UTF8Encoding(false));
            return full;
        }

        private static string Text(byte[] b) { return b == null ? null : Encoding.UTF8.GetString(b); }

        // ---------------- 路徑換算 ----------------

        [Test]
        public void RelUnderRoot_StripsTheRoot()
        {
            Assert.AreEqual("UI/GAMEPLAY/x.png",
                VfsFile.RelUnderRoot(Path.Combine(_root, "UI", "GAMEPLAY", "x.png")));
        }

        [Test]
        public void RelUnderRoot_OutsideTheRootIsNull()
        {
            Assert.IsNull(VfsFile.RelUnderRoot(@"C:\Windows\win.ini"));
            Assert.IsNull(VfsFile.RelUnderRoot(_root));            // 根自己不是「根底下的東西」
            Assert.IsNull(VfsFile.RelUnderRoot(_root + "2\\x"));   // 前綴像但不是同一層目錄
            Assert.IsNull(VfsFile.RelUnderRoot(null));
        }

        [Test]
        public void RelUnderRoot_FoldsDotSegmentsEvenWhenThePrefixMatches()
        {
            // Path.Combine 不做摺疊，所以前綴會直接命中、"UI/../UI/..." 原封不動被剝出來。
            // 查表鍵必須是正規形式，否則同一個檔會因為寫法不同算成兩筆（快取各存一份、pak 查不到）。
            var messy = Path.Combine(_root, "UI", "..", "UI", "GAMEPLAY", "x.png");
            Assert.AreEqual("UI/GAMEPLAY/x.png", VfsFile.RelUnderRoot(messy));
        }

        [Test]
        public void RelUnderRoot_FoldsWhenThePrefixDoesNotMatchLiterally()
        {
            // 快路徑打不中（這裡是根自己帶了 ".."）時要退回完整正規化，不能直接放棄。
            var messyRoot = Path.Combine(_root, "sub", "..");
            var messy = Path.Combine(messyRoot, "UI", "GAMEPLAY", "x.png");
            Assert.AreEqual("UI/GAMEPLAY/x.png", VfsFile.RelUnderRoot(messy));
        }

        [Test]
        public void RelUnderRoot_DotsInFileNamesAreNotDotSegments()
        {
            Assert.AreEqual("UI/a.b.c.png", VfsFile.RelUnderRoot(Path.Combine(_root, "UI", "a.b.c.png")));
            Assert.AreEqual("x.png", VfsFile.RelUnderRoot(Path.Combine(_root, "x.png")));
        }

        [Test]
        public void AbsFor_RoundTrips()
        {
            var abs = VfsFile.AbsFor("UI/GAMEPLAY/x.png");
            Assert.AreEqual("UI/GAMEPLAY/x.png", VfsFile.RelUnderRoot(abs));
        }

        // ---------------- 檔案（散裝層 = 現在的行為） ----------------

        [Test]
        public void ReadsFilesUnderTheRoot()
        {
            var abs = Write("UI/GAMEPLAY/x.png", "hello");

            Assert.IsTrue(VfsFile.Exists(abs));
            Assert.AreEqual("hello", Text(VfsFile.ReadAllBytes(abs)));
            Assert.AreEqual("hello", VfsFile.ReadAllText(abs));
            Assert.AreEqual(5, VfsFile.Length(abs));
            Assert.IsNotNull(VfsFile.ResolveRealPath(abs));
        }

        [Test]
        public void ReadsFilesOutsideTheRootDirectly()
        {
            // ADDON 可以被 config 指到別的碟、外部歌資料夾也在 DATA 之外 —— 那些必須照走真實檔案系統。
            var outside = Path.Combine(Path.GetTempPath(), "sdo_vfsfile_outside_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllText(outside, "external", new UTF8Encoding(false));
                Assert.IsTrue(VfsFile.Exists(outside));
                Assert.AreEqual("external", VfsFile.ReadAllText(outside));
                Assert.AreEqual(outside, VfsFile.ResolveRealPath(outside));
            }
            finally { try { File.Delete(outside); } catch { } }
        }

        [Test]
        public void MissingIsNullNotAnException()
        {
            var abs = Path.Combine(_root, "nope", "missing.dds");
            Assert.IsFalse(VfsFile.Exists(abs));
            Assert.IsNull(VfsFile.ReadAllBytes(abs));
            Assert.IsNull(VfsFile.ReadAllText(abs));
            Assert.IsNull(VfsFile.ReadAllLines(abs));
            Assert.IsNull(VfsFile.OpenRead(abs));
            Assert.IsNull(VfsFile.ResolveRealPath(abs));
            Assert.AreEqual(-1, VfsFile.Length(abs));
        }

        [Test]
        public void ReadAllLines_HandlesCrlfAndLf()
        {
            var abs = Write("a.txt", "one\r\ntwo\nthree");
            CollectionAssert.AreEqual(new[] { "one", "two", "three" }, VfsFile.ReadAllLines(abs));
        }

        [Test]
        public void ReadAllLines_MatchesFileReadAllLinesOnTrailingNewline()
        {
            // File.ReadAllLines("a\nb\n") 是 ["a","b"]，不是 ["a","b",""]。多出來的空元素會讓
            // 「每行 Split/Parse」的呼叫端走進格式錯誤分支 —— .an / list.txt 這類表格檔全是那樣讀的。
            var abs = Write("a.txt", "one\ntwo\n");
            CollectionAssert.AreEqual(File.ReadAllLines(abs), VfsFile.ReadAllLines(abs));
            CollectionAssert.AreEqual(new[] { "one", "two" }, VfsFile.ReadAllLines(abs));

            var empty = Write("empty.txt", "");
            CollectionAssert.AreEqual(File.ReadAllLines(empty), VfsFile.ReadAllLines(empty));
        }

        [Test]
        public void OpenRead_Streams()
        {
            var abs = Write("a.txt", "streamed");
            using (var s = VfsFile.OpenRead(abs))
            using (var r = new StreamReader(s))
                Assert.AreEqual("streamed", r.ReadToEnd());
        }

        // ---------------- 目錄 ----------------

        [Test]
        public void GetFiles_ReturnsRoundTrippablePaths()
        {
            Write("AVATAR/a.dds", "1");
            Write("AVATAR/b.png", "2");
            Write("AVATAR/HAIR/c.dds", "3");

            var dir = Path.Combine(_root, "AVATAR");
            var top = VfsFile.GetFiles(dir, "*.dds");
            CollectionAssert.AreEqual(new[] { Path.Combine(_root, "AVATAR", "a.dds") }, top);

            // 回傳值要能直接餵回門面 —— 遷移後的呼叫端就是這樣接下去用的。
            Assert.AreEqual("1", Text(VfsFile.ReadAllBytes(top[0])));

            var all = VfsFile.GetFiles(dir, "*", true);
            Assert.AreEqual(3, all.Length);
        }

        [Test]
        public void GetFiles_MissingDirIsEmptyArray()
        {
            // Directory.GetFiles 對不存在的目錄會丟例外；門面一律回空陣列，呼叫端不必包 try。
            CollectionAssert.IsEmpty(VfsFile.GetFiles(Path.Combine(_root, "NOPE")));
            CollectionAssert.IsEmpty(VfsFile.GetFiles(@"C:\definitely\not\here"));
        }

        [Test]
        public void GetDirectories_ListsSubfolders()
        {
            Write("AVATAR/HAIR/a.dds", "1");
            Write("AVATAR/COAT/b.dds", "2");

            var subs = VfsFile.GetDirectories(Path.Combine(_root, "AVATAR")).OrderBy(s => s).ToArray();
            CollectionAssert.AreEqual(
                new[] { Path.Combine(_root, "AVATAR", "COAT"), Path.Combine(_root, "AVATAR", "HAIR") }, subs);
        }

        [Test]
        public void DirectoryExists_TrueForRealAndForPakOnly()
        {
            Write("AVATAR/a.dds", "1");
            Assert.IsTrue(VfsFile.DirectoryExists(Path.Combine(_root, "AVATAR")));
            Assert.IsFalse(VfsFile.DirectoryExists(Path.Combine(_root, "NOPE")));

            // pak 裡沒有「目錄項」，所以目錄存在與否只能靠「底下有沒有檔」推。
            SdoVfs.Mount(new PakLike("pak:base", "SCENE/only-in-pak.msh"), SdoVfs.PriorityPakBase);
            Assert.IsTrue(VfsFile.DirectoryExists(Path.Combine(_root, "SCENE")));
        }

        // ---------------- pak 層（未來行為） ----------------

        [Test]
        public void MaterialiseRealPath_ExtractsPakOnlyFilesToCache()
        {
            // Unity 沒有記憶體 ogg 解碼器 —— UnityWebRequestMultimedia 只吃 file://，
            // Mp3Decoder.Decode 吃的是路徑。音訊要能從 pak 播出來就得先落到磁碟上。
            SdoVfs.Mount(new PakLike("pak:music", "MUSIC/song.ogg"), SdoVfs.PriorityPakBase);
            var abs = Path.Combine(_root, "MUSIC", "song.ogg");

            Assert.IsNull(VfsFile.ResolveRealPath(abs), "前提：pak 內的檔沒有實體");

            var real = VfsFile.MaterialiseRealPath(abs);
            Assert.IsNotNull(real);
            Assert.IsTrue(File.Exists(real));
            Assert.AreEqual("pak-content", File.ReadAllText(real));
            StringAssert.Contains("CACHE", real, "要落在可刪的 CACHE，不是散裝樹裡");
            StringAssert.EndsWith(".ogg", real, "副檔名要留著 —— AudioType 的判斷靠它");

            // 已經解過就沿用，不重寫。
            var again = VfsFile.MaterialiseRealPath(abs);
            Assert.AreEqual(real, again);
        }

        [Test]
        public void MaterialiseRealPath_IsAPassthroughForLooseFiles()
        {
            // 散裝時（開發常態、以及刻意不打包的 BGM）必須是零成本直通，不能複製到 CACHE。
            var abs = Write("BGM/bgm_000.ogg", "loose track");
            Assert.AreEqual(abs, VfsFile.MaterialiseRealPath(abs));
        }

        [Test]
        public void MaterialiseRealPath_TrimsTheCacheToItsLimit()
        {
            // 沒有上限的話，把 8.3 GB 的曲庫全播過一輪就會在 CACHE 裡再長出 8.3 GB。
            var saved = VfsFile.AudioCacheLimitBytes;
            try
            {
                VfsFile.AudioCacheLimitBytes = 20;      // 每個 payload 是 11 bytes → 只放得下一個

                SdoVfs.Mount(new PakLike("pak:a", "MUSIC/a.ogg"), SdoVfs.PriorityPakBase);
                SdoVfs.Mount(new PakLike("pak:b", "MUSIC/b.ogg"), SdoVfs.PriorityPakBase + 1);

                var first = VfsFile.MaterialiseRealPath(Path.Combine(_root, "MUSIC", "a.ogg"));
                Assert.IsNotNull(first);
                var second = VfsFile.MaterialiseRealPath(Path.Combine(_root, "MUSIC", "b.ogg"));
                Assert.IsNotNull(second);

                // 剛解出來的那個絕不能被自己的修剪刪掉 —— 不然呼叫端拿到的路徑當場失效。
                Assert.IsTrue(File.Exists(second), "剛具現化的檔必須留著");

                var dir = Path.GetDirectoryName(second);
                long total = new DirectoryInfo(dir).GetFiles().Sum(f => f.Length);
                Assert.LessOrEqual(total, VfsFile.AudioCacheLimitBytes);
            }
            finally { VfsFile.AudioCacheLimitBytes = saved; }
        }

        [Test]
        public void MaterialiseRealPath_MissingIsNull()
        {
            Assert.IsNull(VfsFile.MaterialiseRealPath(Path.Combine(_root, "MUSIC", "nope.ogg")));
            Assert.IsNull(VfsFile.MaterialiseRealPath(null));
        }

        [Test]
        public void PakOnlyFile_IsReadableButHasNoRealPath()
        {
            SdoVfs.Mount(new PakLike("pak:base", "MUSIC/song.mp3"), SdoVfs.PriorityPakBase);
            var abs = Path.Combine(_root, "MUSIC", "song.mp3");

            Assert.IsTrue(VfsFile.Exists(abs));
            Assert.AreEqual("pak-content", Text(VfsFile.ReadAllBytes(abs)));
            // 沒有 file:// 可用 —— UnityWebRequestMultimedia 那條路必須靠這個判斷改走記憶體解碼。
            Assert.IsNull(VfsFile.ResolveRealPath(abs));
        }

        [Test]
        public void LooseFileOverridesPak()
        {
            SdoVfs.Mount(new PakLike("pak:base", "AVATAR/x.dds"), SdoVfs.PriorityPakBase);
            var abs = Path.Combine(_root, "AVATAR", "x.dds");
            Assert.AreEqual("pak-content", Text(VfsFile.ReadAllBytes(abs)));

            Write("AVATAR/x.dds", "loose-content");
            Assert.AreEqual("loose-content", Text(VfsFile.ReadAllBytes(abs)));
        }

        /// <summary>只有一個檔、而且沒有真實路徑的一層 —— 模擬 pak。</summary>
        private sealed class PakLike : IVfsProvider
        {
            private readonly string _path;
            private static readonly byte[] Content = Encoding.UTF8.GetBytes("pak-content");

            public PakLike(string name, string path) { Name = name; _path = path; }
            public string Name { get; private set; }

            public bool TryGet(string normalized, out VfsEntry entry)
            {
                entry = default(VfsEntry);
                if (!string.Equals(normalized, _path, StringComparison.OrdinalIgnoreCase)) return false;
                entry = new VfsEntry { Path = _path, Size = Content.Length, RealPath = null };
                return true;
            }

            public byte[] ReadAllBytes(string normalized)
            {
                return string.Equals(normalized, _path, StringComparison.OrdinalIgnoreCase)
                    ? (byte[])Content.Clone() : null;
            }

            public Stream OpenRead(string normalized)
            {
                var b = ReadAllBytes(normalized);
                return b == null ? null : new MemoryStream(b, false);
            }

            public System.Collections.Generic.IEnumerable<VfsEntry> EnumerateUnder(string normalizedDir, bool recursive)
            {
                if (VfsPath.IsUnder(_path, normalizedDir ?? "", recursive))
                    yield return new VfsEntry { Path = _path, Size = Content.Length, RealPath = null };
            }
        }
    }
}
