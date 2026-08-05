using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Sdo.Settings;
using Sdo.Settings.Vfs;

namespace Sdo.Tests
{
    /// <summary>SDOPAK v1 的 round-trip、損毀偵測、whiteout、加密、以及掛進 VFS 之後的整體行為。
    ///
    /// 寫入器與讀取器共用同一份 PakFormat 定義，所以這裡的測試就是「格式契約」本身 ——
    /// tools/build_pak.py 要產出通得過同樣檢查的檔。規格見 docs/architecture/data-packaging.md §3。</summary>
    public class PakTests
    {
        private string _dir;
        private string _savedRoot;

        [SetUp]
        public void SetUp()
        {
            _savedRoot = SdoDataRoot.Root;
            _dir = Path.Combine(Path.GetTempPath(), "sdo_pak_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            SdoVfs.Reset();
            SdoDataRoot.Root = _savedRoot;
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        private static byte[] B(string s) { return Encoding.UTF8.GetBytes(s); }
        private static string S(byte[] b) { return b == null ? null : Encoding.UTF8.GetString(b); }

        private string WritePak(PakWriter w, string name = "base_test.pak")
        {
            var p = Path.Combine(_dir, name);
            w.WriteTo(p);
            return p;
        }

        private static PakProvider Open(string path)
        {
            PakProvider p;
            Assert.IsTrue(PakProvider.TryOpen(path, out p), "TryOpen 應該成功: " + path);
            return p;
        }

        // ---------------- round-trip ----------------

        [Test]
        public void RoundTrips_Plain()
        {
            var path = WritePak(new PakWriter(1)
                .Add("UI/GAMEPLAY/x.png", B("hello"))
                .Add("AVATAR/body.dds", B("a very compressible payload aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")));

            using (var pak = Open(path))
            {
                Assert.AreEqual(2, pak.Count);
                Assert.AreEqual("hello", S(pak.ReadAllBytes("UI/GAMEPLAY/x.png")));
                StringAssert.StartsWith("a very compressible", S(pak.ReadAllBytes("AVATAR/body.dds")));
            }
        }

        [Test]
        public void RoundTrips_Encrypted()
        {
            var path = WritePak(new PakWriter(7, encrypt: true).Add("SCENE/a.msh", B("secret payload")));

            // 明文不該出現在檔案裡 —— 這就是「不能整包拷走直接用」的那一層。
            var raw = File.ReadAllBytes(path);
            StringAssert.DoesNotContain("secret payload", Encoding.UTF8.GetString(raw));
            StringAssert.DoesNotContain("SCENE/a.msh", Encoding.UTF8.GetString(raw));   // 索引也加密了

            using (var pak = Open(path))
                Assert.AreEqual("secret payload", S(pak.ReadAllBytes("SCENE/a.msh")));
        }

        [Test]
        public void RoundTrips_HeaderOnlyEncryption()
        {
            // 音訊用:只加密前 4096 bytes —— 播放器直接開會失敗，但串流時只需解前 4 KB。
            // ⚠️ payload 不能有短週期 —— 見 HeaderOnlyPayload 的註解。
            var payload = HeaderOnlyPayload();

            var path = WritePak(new PakWriter(3, encrypt: true)
                .Add("MUSIC/song.mp3", payload, compress: false, cryptRange: PakFormat.CryptHeaderOnly));

            var raw = File.ReadAllBytes(path);
            // 尾段是明文（在檔案裡找得到），開頭不是。
            var tail = new byte[256];
            Buffer.BlockCopy(payload, payload.Length - 256, tail, 0, 256);
            Assert.IsTrue(IndexOfBytes(raw, tail) >= 0, "尾段應該維持明文");
            var head = new byte[256];
            Buffer.BlockCopy(payload, 0, head, 0, 256);
            Assert.IsTrue(IndexOfBytes(raw, head) < 0, "前 4KB 應該被加密");

            using (var pak = Open(path))
                CollectionAssert.AreEqual(payload, pak.ReadAllBytes("MUSIC/song.mp3"));
        }

        [Test]
        public void EmptyFileRoundTrips()
        {
            var path = WritePak(new PakWriter(1).Add("UI/empty.txt", new byte[0]));
            using (var pak = Open(path))
            {
                var b = pak.ReadAllBytes("UI/empty.txt");
                Assert.IsNotNull(b);
                Assert.AreEqual(0, b.Length);
            }
        }

        [Test]
        public void LookupIsCaseInsensitive()
        {
            var path = WritePak(new PakWriter(1).Add("AVATAR/Body.DDS", B("x")));
            using (var pak = Open(path))
            {
                Assert.AreEqual("x", S(pak.ReadAllBytes("avatar/body.dds")));
                Assert.AreEqual("x", S(pak.ReadAllBytes("AVATAR/BODY.DDS")));
            }
        }

        [Test]
        public void OutputIsDeterministic()
        {
            // patch diff 的前提：同輸入 → 同 bytes。條目排序過、沒有時間戳。
            Func<PakWriter> make = () => new PakWriter(1).Add("b.txt", B("bbb")).Add("a.txt", B("aaa"));
            CollectionAssert.AreEqual(make().Build(), make().Build());

            // 加入順序不同也要一樣（排序是照 pathHash，不是照加入順序）。
            var reordered = new PakWriter(1).Add("a.txt", B("aaa")).Add("b.txt", B("bbb"));
            CollectionAssert.AreEqual(make().Build(), reordered.Build());
        }

        // ---------------- whiteout ----------------

        [Test]
        public void Whiteout_IsReportedAndHidesContent()
        {
            var path = WritePak(new PakWriter(2).AddWhiteout("SCENE/gone.msh"));
            using (var pak = Open(path))
            {
                VfsEntry e;
                Assert.IsTrue(pak.TryGet("SCENE/gone.msh", out e), "whiteout 必須回報「有這一筆」，上層才知道要停止往下找");
                Assert.IsTrue(e.IsWhiteout);
                Assert.IsNull(pak.ReadAllBytes("SCENE/gone.msh"));
            }
        }

        // ---------------- 壞檔 ----------------

        [Test]
        public void NonPakFile_IsRejectedQuietly()
        {
            var p = Path.Combine(_dir, "not_a.pak");
            File.WriteAllText(p, "this is not a pak");
            PakProvider prov;
            Assert.IsFalse(PakProvider.TryOpen(p, out prov));
            Assert.IsNull(prov);
        }

        [Test]
        public void MissingFile_IsRejectedQuietly()
        {
            PakProvider prov;
            Assert.IsFalse(PakProvider.TryOpen(Path.Combine(_dir, "nope.pak"), out prov));
        }

        [Test]
        public void CorruptedPayload_ReadsAsNullNotGarbage()
        {
            // 壞掉的 DDS/MSH 會變成整個畫面亂掉，那種問題查起來很貴 —— CRC 對不上就回 null。
            var path = WritePak(new PakWriter(1).Add("AVATAR/x.dds", B("original content here"), compress: false));
            var raw = File.ReadAllBytes(path);
            raw[PakFormat.HeaderSize + 3] ^= 0xFF;      // 動資料區第一個條目的內容
            File.WriteAllBytes(path, raw);

            using (var pak = Open(path))
                Assert.IsNull(pak.ReadAllBytes("AVATAR/x.dds"), "CRC 對不上就該回 null");
        }

        [Test]
        public void TamperedEncryptedIndex_FailsTheMac()
        {
            var path = WritePak(new PakWriter(5, encrypt: true).Add("UI/x.png", B("payload")));
            var raw = File.ReadAllBytes(path);
            PakFormat.Header h;
            Assert.IsTrue(PakFormat.TryReadHeader(raw, out h));

            raw[h.IndexOffset] ^= 0x01;
            File.WriteAllBytes(path, raw);

            PakProvider prov;
            Assert.IsFalse(PakProvider.TryOpen(path, out prov), "索引被改過就不該開得起來");
        }

        // ---------------- 打包器的防呆 ----------------

        [Test]
        public void ReservedPrefixesCannotBePacked()
        {
            // 這四個是可寫的明碼區，打包進去只會製造「玩家存檔被 pak 蓋掉」那種災難。
            foreach (var p in new[] { "PROFILE/config.ini", "ADDON/SONG/a.osu", "CACHE/x.json", "REPLAY/r.rpy" })
                Assert.Throws<ArgumentException>(() => new PakWriter(1).Add(p, B("x")), p + " 應該被拒絕");
        }

        [Test]
        public void EscapingPathsCannotBePacked()
        {
            Assert.Throws<ArgumentException>(() => new PakWriter(1).Add("../../windows/system32/x", B("x")));
            Assert.Throws<ArgumentException>(() => new PakWriter(1).Add(@"C:\Windows\x", B("x")));
        }

        // ---------------- 掛進 VFS ----------------

        [Test]
        public void MountedPak_ServesFilesAndLooseOverrides()
        {
            var pakPath = WritePak(new PakWriter(1)
                .Add("AVATAR/x.dds", B("from-pak"))
                .Add("AVATAR/only-pak.dds", B("pak-only")));

            var root = Path.Combine(_dir, "root");
            Directory.CreateDirectory(root);
            SdoDataRoot.Root = root;
            SdoVfs.Initialise(root);
            SdoVfs.Mount(Open(pakPath), SdoVfs.PriorityPakBase);

            Assert.AreEqual("from-pak", S(SdoVfs.ReadAllBytes("AVATAR/x.dds")));
            Assert.AreEqual("pak-only", S(SdoVfs.ReadAllBytes("AVATAR/only-pak.dds")));
            Assert.IsNull(SdoVfs.ResolveRealPath("AVATAR/only-pak.dds"), "pak 內的檔沒有實體");

            // 丟一個同名散裝檔就該蓋掉 pak 內的 —— 開發覆寫 / 熱修 / mod 全靠這條。
            var looseFile = Path.Combine(root, "AVATAR", "x.dds");
            Directory.CreateDirectory(Path.GetDirectoryName(looseFile));
            File.WriteAllText(looseFile, "from-loose", new UTF8Encoding(false));
            Assert.AreEqual("from-loose", S(SdoVfs.ReadAllBytes("AVATAR/x.dds")));
            Assert.IsNotNull(SdoVfs.ResolveRealPath("AVATAR/x.dds"));
        }

        [Test]
        public void PatchPakOverridesAndDeletes()
        {
            var basePak = WritePak(new PakWriter(1)
                .Add("SCENE/keep.msh", B("keep"))
                .Add("SCENE/replaced.msh", B("old"))
                .Add("SCENE/gone.msh", B("doomed")), "base_x.pak");

            var patchPak = WritePak(new PakWriter(2)
                .Add("SCENE/replaced.msh", B("new"))
                .AddWhiteout("SCENE/gone.msh"), "patch_001.pak");

            var root = Path.Combine(_dir, "root2");
            Directory.CreateDirectory(root);
            SdoDataRoot.Root = root;
            SdoVfs.Initialise(root);
            SdoVfs.Mount(Open(basePak), SdoVfs.PriorityPakBase);
            SdoVfs.Mount(Open(patchPak), SdoVfs.PriorityPakPatch);

            Assert.AreEqual("keep", S(SdoVfs.ReadAllBytes("SCENE/keep.msh")));
            Assert.AreEqual("new", S(SdoVfs.ReadAllBytes("SCENE/replaced.msh")));
            Assert.IsFalse(SdoVfs.Exists("SCENE/gone.msh"), "whiteout 應該讓它消失");

            CollectionAssert.AreEquivalent(
                new[] { "SCENE/keep.msh", "SCENE/replaced.msh" },
                SdoVfs.EnumerateFiles("SCENE", "*", true).ToArray());
        }

        [Test]
        public void VfsFile_ReadsThroughPakWithAbsoluteLookingPaths()
        {
            // 遷移過的 275 個呼叫點傳的都是絕對路徑 —— 這條就是它們在 pak 化之後的實際路徑。
            var pakPath = WritePak(new PakWriter(1).Add("UI/GAMEPLAY/x.png", B("art")));
            var root = Path.Combine(_dir, "root3");
            Directory.CreateDirectory(root);
            SdoDataRoot.Root = root;
            SdoVfs.Initialise(root);
            SdoVfs.Mount(Open(pakPath), SdoVfs.PriorityPakBase);

            var abs = Path.Combine(root, "UI", "GAMEPLAY", "x.png");
            Assert.IsTrue(VfsFile.Exists(abs));
            Assert.AreEqual("art", S(VfsFile.ReadAllBytes(abs)));
            Assert.IsNull(VfsFile.ResolveRealPath(abs));

            var listed = VfsFile.GetFiles(Path.Combine(root, "UI", "GAMEPLAY"));
            CollectionAssert.AreEqual(new[] { abs }, listed);
            Assert.AreEqual("art", S(VfsFile.ReadAllBytes(listed[0])), "列舉回來的路徑要能直接餵回去讀");
        }

        // ---------------- 開機自動掛載 ----------------

        [Test]
        public void Initialise_AutoMountsPaksInPakIdOrder()
        {
            var root = Path.Combine(_dir, "autoroot");
            Directory.CreateDirectory(root);

            // 檔名故意跟 pakId 的順序相反 —— 掛載順序必須看 pakId，不看檔名。
            new PakWriter(11).Add("AVATAR/x.dds", B("from-base-11")).WriteTo(Path.Combine(root, "zzz.pak"));
            new PakWriter(301).Add("AVATAR/x.dds", B("from-patch-301")).WriteTo(Path.Combine(root, "aaa.pak"));

            SdoDataRoot.Root = root;
            SdoVfs.Initialise(root);

            Assert.AreEqual("from-patch-301", S(SdoVfs.ReadAllBytes("AVATAR/x.dds")),
                            "pakId 大的（patch）要蓋掉 pakId 小的（base）");

            var names = SdoVfs.LayerNames();
            Assert.AreEqual(3, names.Count, "散裝層 + 兩個 pak");
            StringAssert.Contains("loose", names[0]);
        }

        [Test]
        public void Initialise_SkipsBrokenPaksInsteadOfThrowing()
        {
            // 一個壞卷不該讓整個遊戲開不起來 —— 少一層頂多是某些資產讀不到。
            var root = Path.Combine(_dir, "brokenroot");
            Directory.CreateDirectory(root);
            new PakWriter(10).Add("UI/ok.png", B("fine")).WriteTo(Path.Combine(root, "good.pak"));
            File.WriteAllText(Path.Combine(root, "broken.pak"), "not a pak at all");

            SdoDataRoot.Root = root;
            Assert.DoesNotThrow(() => SdoVfs.Initialise(root));
            Assert.AreEqual("fine", S(SdoVfs.ReadAllBytes("UI/ok.png")));
        }

        [Test]
        public void PakOnlyFolderIsRecognisedAsADataRoot()
        {
            // pak 化之後 AVATAR/FEMALE.HRC 之類的路徑在磁碟上都不存在了。少了 *.pak 這條判準，
            // PickRoot 會認不出打包好的 DATA → 遊戲開起來什麼美術都沒有。
            var root = Path.Combine(_dir, "pakonly");
            Directory.CreateDirectory(root);
            Assert.IsFalse(SdoDataRoot.LooksLikeGameDataRoot(root), "空資料夾不算");

            new PakWriter(10).Add("UI/x.png", B("art")).WriteTo(Path.Combine(root, "base_core.pak"));
            Assert.IsTrue(SdoDataRoot.LooksLikeGameDataRoot(root));
        }

        // ---------------- 跨語言契約 ----------------

        [Test]
        public void ReadsPythonProducedPak()
        {
            // 正式打包走 tools/sdopak.py（C# 這邊的 PakWriter 只是為了讓 round-trip 測試不依賴 Python）。
            // 兩份實作一漂移，這個測試就紅。
            //
            // ⚠️ 不能改成「比對 byte 完全一致」：C# 的 DeflateStream 與 Python 的 zlib 對同一份輸入會產生
            //    不同但都合法的 deflate 位元流。要驗的是「C# 讀得懂 Python 產的檔」。
            //
            // fixture 由 tools/tests/test_sdopak.py 產生（--write），內容表也在那裡。
            var fixture = Path.Combine(Application.dataPath, "Tests", "EditMode", "Fixtures", "contract_v1.pak.bytes");
            Assert.IsTrue(File.Exists(fixture),
                "缺 fixture —— 跑 python tools/tests/test_sdopak.py --write。找的位置: " + fixture);

            // PakProvider 只吃真實檔案路徑，直接開它。
            using (var pak = Open(fixture))
            {
                Assert.AreEqual(6, pak.Count, "5 個檔 + 1 筆 whiteout");

                Assert.AreEqual("hello from python", S(pak.ReadAllBytes("UI/GAMEPLAY/x.png")));
                Assert.AreEqual(string.Concat(System.Linq.Enumerable.Repeat("compressible ", 64)),
                                S(pak.ReadAllBytes("AVATAR/body.dds")), "deflate 分支");
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, pak.ReadAllBytes("SCENE/tiny.msh"), "store 分支");

                // 非 ASCII 路徑：雜湊只轉 ASCII 大寫，多位元組字元不能被動到。
                Assert.AreEqual("cjk path payload", S(pak.ReadAllBytes("MUSIC/歌曲/測試.gn")));

                // 大小寫不敏感查表要跨語言一致。
                Assert.AreEqual("hello from python", S(pak.ReadAllBytes("ui/gameplay/X.PNG")));

                // 只加密表頭的音訊條目：整份要能完整還原。
                var mp3 = pak.ReadAllBytes("MUSIC/song.mp3");
                Assert.IsNotNull(mp3);
                Assert.AreEqual(PakFormat.HeaderCryptBytes + 512, mp3.Length);
                CollectionAssert.AreEqual(HeaderOnlyPayload(), mp3);

                // whiteout 要被認出來（而不是當成一般檔案）。
                VfsEntry e;
                Assert.IsTrue(pak.TryGet("SCENE/deleted.msh", out e));
                Assert.IsTrue(e.IsWhiteout);
                Assert.IsNull(pak.ReadAllBytes("SCENE/deleted.msh"));
            }
        }

        /// <summary>對應 tools/tests/test_sdopak.py 的 header_only_payload()。
        /// ⚠️ 不能用 (i*31)&amp;0xFF —— 那個對 i 的週期剛好 256，頭尾兩段會完全相同。</summary>
        private static byte[] HeaderOnlyPayload()
        {
            var payload = new byte[PakFormat.HeaderCryptBytes + 512];
            uint state = 12345;
            for (int i = 0; i < payload.Length; i++)
            {
                state = state * 1103515245u + 12345u;
                payload[i] = (byte)(state >> 16);
            }
            return payload;
        }

        // ---------------- 加解密單元 ----------------

        [Test]
        public void Keystream_IsSeekable()
        {
            var key = PakCrypto.DataKey(1);
            var whole = new byte[64];
            PakCrypto.XorKeystream(key, whole, 0, whole.Length, 0);

            // 從中間任意位置起算，結果要跟整段加密的同一段一致 —— 這是隨機存取的前提。
            for (int at = 1; at < 40; at += 7)
            {
                var part = new byte[16];
                PakCrypto.XorKeystream(key, part, 0, part.Length, at);
                for (int i = 0; i < part.Length; i++)
                    Assert.AreEqual(whole[at + i], part[i], "位移 " + at + " 的第 " + i + " 個位元組對不上");
            }
        }

        [Test]
        public void Keystream_IsSymmetricAndKeyDependent()
        {
            var data = B("round trip me");
            var enc = (byte[])data.Clone();
            PakCrypto.XorKeystream(PakCrypto.DataKey(1), enc, 0, enc.Length, 100);
            CollectionAssert.AreNotEqual(data, enc);
            PakCrypto.XorKeystream(PakCrypto.DataKey(1), enc, 0, enc.Length, 100);
            CollectionAssert.AreEqual(data, enc);

            // 不同卷 → 不同金鑰。
            CollectionAssert.AreNotEqual(PakCrypto.DataKey(1), PakCrypto.DataKey(2));
            CollectionAssert.AreNotEqual(PakCrypto.DataKey(1), PakCrypto.IndexKey(1));
        }

        [Test]
        public void Crc32_MatchesTheStandard()
        {
            // 標準 CRC-32(IEEE) —— Python zlib.crc32 同一顆。釘死向量，打包器才有得對。
            Assert.AreEqual(0x414FA339u, PakFormat.Crc32(B("The quick brown fox jumps over the lazy dog")));
            Assert.AreEqual(0xCBF43926u, PakFormat.Crc32(B("123456789")));
            Assert.AreEqual(0u, PakFormat.Crc32(new byte[0]));
        }

        private static int IndexOfBytes(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                    if (haystack[i + j] != needle[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }
    }
}
