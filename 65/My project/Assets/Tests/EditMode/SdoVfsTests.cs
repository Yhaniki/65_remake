using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Sdo.Settings.Vfs;

namespace Sdo.Tests
{
    /// <summary>SdoVfs 的層疊解析:覆蓋、whiteout、reserved 目錄、列舉合併。
    ///
    /// 這裡釘死的是「哪一層贏」——pak 進來之後這套規則不會再改,所以 275 個 IO 呼叫點只需要搬一次。
    /// 規格見 docs/architecture/data-packaging.md §4.2 / §4.3。</summary>
    public class SdoVfsTests
    {
        private string _tmp;

        [SetUp]
        public void SetUp()
        {
            _tmp = Path.Combine(Path.GetTempPath(), "sdo_vfs_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tmp);
        }

        [TearDown]
        public void TearDown()
        {
            SdoVfs.Reset();   // 靜態狀態：不還原會污染同一個 domain 裡的其它測試
            try { if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true); } catch { }
        }

        private void WriteFile(string rel, string content)
        {
            var full = Path.Combine(_tmp, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content, new UTF8Encoding(false));
        }

        private static string Text(byte[] b)
        {
            return b == null ? null : Encoding.UTF8.GetString(b);
        }

        // ---------------- 散裝層（= 目前的行為，零變化） ----------------

        [Test]
        public void Loose_ReadsFileAndReportsRealPath()
        {
            WriteFile("UI/GAMEPLAY/x.txt", "hello");
            SdoVfs.Initialise(_tmp);

            Assert.IsTrue(SdoVfs.Exists("UI/GAMEPLAY/x.txt"));
            Assert.AreEqual("hello", Text(SdoVfs.ReadAllBytes("UI/GAMEPLAY/x.txt")));
            Assert.AreEqual(5, SdoVfs.GetSize("UI/GAMEPLAY/x.txt"));
            Assert.IsNotNull(SdoVfs.ResolveRealPath("UI/GAMEPLAY/x.txt"));
        }

        [Test]
        public void Loose_AcceptsBackslashAndAnyCase()
        {
            WriteFile("UI/GAMEPLAY/x.txt", "hello");
            SdoVfs.Initialise(_tmp);

            // NTFS 大小寫不敏感，程式碼裡本來就混用 —— 搬過來的呼叫端不該因此壞掉。
            Assert.IsTrue(SdoVfs.Exists(@"ui\gameplay\x.txt"));
            Assert.AreEqual("hello", Text(SdoVfs.ReadAllBytes(@"UI\GAMEPLAY\X.TXT")));
        }

        [Test]
        public void Missing_ReturnsFalseAndNull_NeverThrows()
        {
            SdoVfs.Initialise(_tmp);

            Assert.IsFalse(SdoVfs.Exists("nope/nothing.dds"));
            Assert.IsNull(SdoVfs.ReadAllBytes("nope/nothing.dds"));
            Assert.IsNull(SdoVfs.OpenRead("nope/nothing.dds"));
            Assert.IsNull(SdoVfs.ResolveRealPath("nope/nothing.dds"));
            Assert.AreEqual(-1, SdoVfs.GetSize("nope/nothing.dds"));
        }

        [Test]
        public void InvalidPath_IsTreatedAsMissing()
        {
            WriteFile("x.txt", "hello");
            SdoVfs.Initialise(_tmp);

            Assert.IsFalse(SdoVfs.Exists("../../../windows/system32/drivers/etc/hosts"));
            Assert.IsNull(SdoVfs.ReadAllBytes(@"C:\Windows\win.ini"));
            Assert.IsFalse(SdoVfs.Exists(null));
        }

        [Test]
        public void OpenRead_StreamsContent()
        {
            WriteFile("a.txt", "streamed");
            SdoVfs.Initialise(_tmp);

            using (var s = SdoVfs.OpenRead("a.txt"))
            using (var r = new StreamReader(s))
                Assert.AreEqual("streamed", r.ReadToEnd());
        }

        [Test]
        public void ReadAllText_StripsBom()
        {
            var full = Path.Combine(_tmp, "bom.txt");
            File.WriteAllText(full, "withbom", new UTF8Encoding(true));
            SdoVfs.Initialise(_tmp);

            Assert.AreEqual("withbom", SdoVfs.ReadAllText("bom.txt"));
        }

        // ---------------- 層疊：覆蓋與 whiteout ----------------

        [Test]
        public void HigherLayerWins()
        {
            var low = new FakeProvider("pak:low").Add("AVATAR/x.dds", "from-pak");
            SdoVfs.Initialise(_tmp);                              // 散裝層 = PriorityLoose
            SdoVfs.Mount(low, SdoVfs.PriorityPakBase);

            Assert.AreEqual("from-pak", Text(SdoVfs.ReadAllBytes("AVATAR/x.dds")));

            WriteFile("AVATAR/x.dds", "from-loose");               // 丟一個同名檔就該蓋掉 pak 內的
            Assert.AreEqual("from-loose", Text(SdoVfs.ReadAllBytes("AVATAR/x.dds")));
        }

        [Test]
        public void PatchLayerOutranksBaseLayer()
        {
            SdoVfs.Initialise(_tmp);
            SdoVfs.Mount(new FakeProvider("pak:base").Add("SCENE/a.msh", "base"), SdoVfs.PriorityPakBase);
            SdoVfs.Mount(new FakeProvider("pak:patch").Add("SCENE/a.msh", "patched"), SdoVfs.PriorityPakPatch);

            Assert.AreEqual("patched", Text(SdoVfs.ReadAllBytes("SCENE/a.msh")));
        }

        [Test]
        public void Whiteout_HidesLowerLayer()
        {
            SdoVfs.Initialise(_tmp);
            SdoVfs.Mount(new FakeProvider("pak:base").Add("SCENE/gone.msh", "still here"), SdoVfs.PriorityPakBase);
            SdoVfs.Mount(new FakeProvider("pak:patch").Whiteout("SCENE/gone.msh"), SdoVfs.PriorityPakPatch);

            Assert.IsFalse(SdoVfs.Exists("SCENE/gone.msh"));
            Assert.IsNull(SdoVfs.ReadAllBytes("SCENE/gone.msh"));
            Assert.AreEqual(-1, SdoVfs.GetSize("SCENE/gone.msh"));
        }

        [Test]
        public void Whiteout_CanItselfBeOverriddenByAHigherLayer()
        {
            SdoVfs.Initialise(_tmp);
            SdoVfs.Mount(new FakeProvider("pak:base").Add("SCENE/gone.msh", "base"), SdoVfs.PriorityPakBase);
            SdoVfs.Mount(new FakeProvider("pak:patch").Whiteout("SCENE/gone.msh"), SdoVfs.PriorityPakPatch);

            WriteFile("SCENE/gone.msh", "restored");   // 散裝層在最上面 —— 玩家丟回來就該看得到
            Assert.AreEqual("restored", Text(SdoVfs.ReadAllBytes("SCENE/gone.msh")));
        }

        [Test]
        public void PakBackedEntry_HasNoRealPath()
        {
            SdoVfs.Initialise(_tmp);
            SdoVfs.Mount(new FakeProvider("pak:base").Add("MUSIC/song.mp3", "audio"), SdoVfs.PriorityPakBase);

            // 存在，但沒有 file:// 可用 —— UnityWebRequestMultimedia 那條路必須靠這個判斷改走記憶體解碼。
            Assert.IsTrue(SdoVfs.Exists("MUSIC/song.mp3"));
            Assert.IsNull(SdoVfs.ResolveRealPath("MUSIC/song.mp3"));
        }

        // ---------------- reserved 目錄 ----------------

        [Test]
        public void ReservedDirs_NeverConsultPaks()
        {
            SdoVfs.Initialise(_tmp);
            // 惡意/誤打包的 pak 宣稱自己有 PROFILE 底下的檔 —— 絕不能讓它蓋掉玩家存檔。
            SdoVfs.Mount(new FakeProvider("pak:evil")
                .Add("PROFILE/config.ini", "from-pak")
                .Add("ADDON/SONG/a.osu", "from-pak")
                .Add("CACHE/x.json", "from-pak")
                .Add("REPLAY/r.rpy", "from-pak"), SdoVfs.PriorityPakPatch);

            Assert.IsFalse(SdoVfs.Exists("PROFILE/config.ini"));
            Assert.IsFalse(SdoVfs.Exists("ADDON/SONG/a.osu"));
            Assert.IsFalse(SdoVfs.Exists("CACHE/x.json"));
            Assert.IsFalse(SdoVfs.Exists("REPLAY/r.rpy"));
        }

        [Test]
        public void ReservedDirs_ReadFromRealDiskAndKeepRealPath()
        {
            WriteFile("PROFILE/config.ini", "real save");
            SdoVfs.Initialise(_tmp);
            SdoVfs.Mount(new FakeProvider("pak:evil").Add("PROFILE/config.ini", "from-pak"), SdoVfs.PriorityPakPatch);

            Assert.AreEqual("real save", Text(SdoVfs.ReadAllBytes("PROFILE/config.ini")));
            Assert.IsNotNull(SdoVfs.ResolveRealPath("PROFILE/config.ini"));   // 可寫區一定要有真實路徑
        }

        // ---------------- 列舉 ----------------

        [Test]
        public void EnumerateFiles_NonRecursiveIsDirectChildrenOnly()
        {
            WriteFile("UI/a.png", "a");
            WriteFile("UI/GAMEPLAY/b.png", "b");
            SdoVfs.Initialise(_tmp);

            CollectionAssert.AreEquivalent(new[] { "UI/a.png" }, SdoVfs.EnumerateFiles("UI").ToArray());
            CollectionAssert.AreEquivalent(new[] { "UI/a.png", "UI/GAMEPLAY/b.png" },
                                           SdoVfs.EnumerateFiles("UI", "*", true).ToArray());
        }

        [Test]
        public void EnumerateFiles_AppliesPatternToFileNameOnly()
        {
            WriteFile("AVATAR/a.dds", "1");
            WriteFile("AVATAR/b.png", "2");
            SdoVfs.Initialise(_tmp);

            CollectionAssert.AreEquivalent(new[] { "AVATAR/a.dds" }, SdoVfs.EnumerateFiles("AVATAR", "*.dds").ToArray());
        }

        [Test]
        public void EnumerateFiles_MergesLayersAndDeduplicates()
        {
            WriteFile("AVATAR/shared.dds", "loose");
            SdoVfs.Initialise(_tmp);
            SdoVfs.Mount(new FakeProvider("pak:base")
                .Add("AVATAR/shared.dds", "pak")
                .Add("AVATAR/only-in-pak.dds", "pak"), SdoVfs.PriorityPakBase);

            var files = SdoVfs.EnumerateFiles("AVATAR", "*", true).ToArray();
            CollectionAssert.AreEquivalent(new[] { "AVATAR/shared.dds", "AVATAR/only-in-pak.dds" }, files);
            Assert.AreEqual(2, files.Length, "同名檔跨層出現只能列一次");
            Assert.AreEqual("loose", Text(SdoVfs.ReadAllBytes("AVATAR/shared.dds")));
        }

        [Test]
        public void EnumerateFiles_WhiteoutRemovesLowerLayerEntry()
        {
            SdoVfs.Initialise(_tmp);
            SdoVfs.Mount(new FakeProvider("pak:base")
                .Add("SCENE/keep.msh", "k")
                .Add("SCENE/gone.msh", "g"), SdoVfs.PriorityPakBase);
            SdoVfs.Mount(new FakeProvider("pak:patch").Whiteout("SCENE/gone.msh"), SdoVfs.PriorityPakPatch);

            CollectionAssert.AreEquivalent(new[] { "SCENE/keep.msh" }, SdoVfs.EnumerateFiles("SCENE", "*", true).ToArray());
        }

        [Test]
        public void EnumerateFiles_RootAndMissingDir()
        {
            WriteFile("top.txt", "t");
            WriteFile("UI/a.png", "a");
            SdoVfs.Initialise(_tmp);

            CollectionAssert.AreEquivalent(new[] { "top.txt" }, SdoVfs.EnumerateFiles("").ToArray());
            CollectionAssert.AreEquivalent(new[] { "top.txt" }, SdoVfs.EnumerateFiles(null).ToArray());
            CollectionAssert.IsEmpty(SdoVfs.EnumerateFiles("DOES/NOT/EXIST", "*", true).ToArray());
        }

        [Test]
        public void EnumerateDirectories_MergesLayers()
        {
            WriteFile("AVATAR/HAIR/a.dds", "a");
            SdoVfs.Initialise(_tmp);
            SdoVfs.Mount(new FakeProvider("pak:base")
                .Add("AVATAR/COAT/b.dds", "b")
                .Add("AVATAR/HAIR/c.dds", "c")
                .Add("AVATAR/loose-file.dds", "d"), SdoVfs.PriorityPakBase);

            CollectionAssert.AreEquivalent(new[] { "AVATAR/HAIR", "AVATAR/COAT" },
                                           SdoVfs.EnumerateDirectories("AVATAR").ToArray());
        }

        // ---------------- 掛載表 ----------------

        [Test]
        public void Layers_AreOrderedHighestFirst()
        {
            SdoVfs.Initialise(_tmp);
            SdoVfs.Mount(new FakeProvider("pak:base"), SdoVfs.PriorityPakBase);
            SdoVfs.Mount(new FakeProvider("pak:patch"), SdoVfs.PriorityPakPatch);

            var names = SdoVfs.LayerNames();
            Assert.AreEqual(3, names.Count);
            StringAssert.Contains("loose", names[0]);
            StringAssert.Contains("pak:patch", names[1]);
            StringAssert.Contains("pak:base", names[2]);
        }

        // ---------------- 測試替身 ----------------

        /// <summary>記憶體版的一層。用來測 whiteout 與「pak 內的檔沒有真實路徑」——
        /// LooseDirProvider 兩者都做不到（真實檔案系統沒有刪除標記）。</summary>
        private sealed class FakeProvider : IVfsProvider
        {
            private readonly Dictionary<string, byte[]> _files =
                new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _whiteouts =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public FakeProvider(string name) { Name = name; }
            public string Name { get; private set; }

            public FakeProvider Add(string path, string content)
            {
                _files[path] = Encoding.UTF8.GetBytes(content);
                return this;
            }

            public FakeProvider Whiteout(string path)
            {
                _whiteouts.Add(path);
                return this;
            }

            public bool TryGet(string normalized, out VfsEntry entry)
            {
                entry = default(VfsEntry);
                if (normalized == null) return false;

                if (_whiteouts.Contains(normalized))
                {
                    entry = new VfsEntry { Path = normalized, IsWhiteout = true };
                    return true;
                }

                byte[] data;
                if (!_files.TryGetValue(normalized, out data)) return false;
                entry = new VfsEntry { Path = normalized, Size = data.Length, IsWhiteout = false, RealPath = null };
                return true;
            }

            public byte[] ReadAllBytes(string normalized)
            {
                byte[] data;
                if (normalized == null || _whiteouts.Contains(normalized)) return null;
                return _files.TryGetValue(normalized, out data) ? (byte[])data.Clone() : null;
            }

            public Stream OpenRead(string normalized)
            {
                var data = ReadAllBytes(normalized);
                return data == null ? null : new MemoryStream(data, false);
            }

            public IEnumerable<VfsEntry> EnumerateUnder(string normalizedDir, bool recursive)
            {
                foreach (var w in _whiteouts)
                    if (VfsPath.IsUnder(w, normalizedDir ?? "", recursive))
                        yield return new VfsEntry { Path = w, IsWhiteout = true };

                foreach (var kv in _files)
                    if (VfsPath.IsUnder(kv.Key, normalizedDir ?? "", recursive))
                        yield return new VfsEntry { Path = kv.Key, Size = kv.Value.Length, RealPath = null };
            }
        }
    }
}
