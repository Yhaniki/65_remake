using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Sdo.Settings;
using Sdo.Settings.Vfs;

namespace Sdo.Tests
{
    /// <summary>對**真實打包出來的加密 pak** 驗證讀取路徑 —— 不是合成資料。
    ///
    /// 產生方式：
    ///   python tools/build_pak.py --source H:\65_remake_clean\DATA --out H:\65_remake\Build\pak_e2e --encrypt
    ///
    /// 沒有那些檔案時整組 Ignore（不是 Fail）—— 打包產物 11 GB，不會進版控，
    /// 別台機器 / CI 上跑不該因此變紅。有檔案時才是真的在驗。</summary>
    public class PakRealDataTests
    {
        private const string PakDir = @"H:\65_remake\Build\pak_e2e";
        private const string LooseDir = @"H:\65_remake_clean\DATA";

        private string _savedRoot;

        [SetUp]
        public void SetUp()
        {
            if (!Directory.Exists(PakDir) || !File.Exists(Path.Combine(PakDir, "base_se.pak")))
                Assert.Ignore("沒有真實 pak 產物（" + PakDir + "）—— 跳過。跑 tools/build_pak.py 產生。");
            _savedRoot = SdoDataRoot.Root;
        }

        [TearDown]
        public void TearDown()
        {
            SdoVfs.Reset();
            if (_savedRoot != null) SdoDataRoot.Root = _savedRoot;
        }

        private static PakProvider Open(string name)
        {
            PakProvider p;
            Assert.IsTrue(PakProvider.TryOpen(Path.Combine(PakDir, name), out p), "開不起來: " + name);
            return p;
        }

        [Test]
        public void EncryptedPak_RoundTripsRealAssetsByteForByte()
        {
            // SE 是 store + 只加密表頭 4KB；AVATAR 是 deflate + 全檔加密。兩條分支都要驗。
            var cases = new[]
            {
                new { Pak = "base_se.pak", Rel = "SE/Bubble.wav" },
                new { Pak = "base_core.pak", Rel = "UI/GAMEPLAY/PlayNoteEx.an" },
            };

            foreach (var c in cases)
            {
                var loose = Path.Combine(LooseDir, c.Rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(loose)) continue;   // 這棵樹沒有這個檔就跳過這一筆

                using (var pak = Open(c.Pak))
                {
                    var fromPak = pak.ReadAllBytes(c.Rel);
                    Assert.IsNotNull(fromPak, c.Rel + " 應該讀得到");
                    CollectionAssert.AreEqual(File.ReadAllBytes(loose), fromPak,
                        c.Rel + " 從加密 pak 解出來的內容必須跟散裝原檔一模一樣");
                }
            }
        }

        [Test]
        public void AvatarPak_HasEveryLooseFileAndReadsThemBack()
        {
            // 67,503 個檔全比對太慢，抽樣 40 個 —— 涵蓋 deflate + 全檔加密那條分支。
            var looseAvatar = Path.Combine(LooseDir, "AVATAR");
            if (!Directory.Exists(looseAvatar)) Assert.Ignore("沒有散裝 AVATAR 可比對");

            using (var pak = Open("base_avatar.pak"))
            {
                Assert.Greater(pak.Count, 60000, "AVATAR 應該有六萬多筆");

                var files = Directory.GetFiles(looseAvatar, "*", SearchOption.AllDirectories);
                int step = Math.Max(1, files.Length / 40);
                int checkedCount = 0;

                for (int i = 0; i < files.Length; i += step)
                {
                    var rel = "AVATAR/" + files[i].Substring(looseAvatar.Length + 1).Replace('\\', '/');
                    var fromPak = pak.ReadAllBytes(rel);
                    Assert.IsNotNull(fromPak, rel + " 在 pak 裡找不到");
                    CollectionAssert.AreEqual(File.ReadAllBytes(files[i]), fromPak, rel + " 內容對不上");
                    checkedCount++;
                }
                Assert.Greater(checkedCount, 10, "至少要抽驗到十幾個檔才有意義");
            }
        }

        [Test]
        public void MaterialiseRealPath_ExtractsRealAudioFromTheEncryptedPak()
        {
            // 這是音訊能從 pak 播出來的關鍵一步：解密 → 落到 CACHE → 交給吃 file:// 的載入路徑。
            var loose = Path.Combine(LooseDir, "SE", "Bubble.wav");
            if (!File.Exists(loose)) Assert.Ignore("沒有散裝 SE/Bubble.wav 可比對");

            var root = Path.Combine(Path.GetTempPath(), "sdo_pak_real_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                // 只掛 SE 那一卷，root 指到臨時目錄 —— CACHE 才不會寫進真正的產物目錄。
                File.Copy(Path.Combine(PakDir, "base_se.pak"), Path.Combine(root, "base_se.pak"));
                SdoDataRoot.Root = root;
                SdoVfs.Initialise(root);

                var abs = Path.Combine(root, "SE", "Bubble.wav");
                Assert.IsTrue(VfsFile.Exists(abs), "應該從 pak 讀得到");
                Assert.IsNull(VfsFile.ResolveRealPath(abs), "前提：pak 內的檔沒有實體");

                var real = VfsFile.MaterialiseRealPath(abs);
                Assert.IsNotNull(real, "具現化必須成功，否則 UnityWebRequestMultimedia 拿不到東西播");
                Assert.IsTrue(File.Exists(real));
                CollectionAssert.AreEqual(File.ReadAllBytes(loose), File.ReadAllBytes(real),
                    "解出來的 wav 必須跟原檔一模一樣 —— 差一個 byte 就是雜音");
                StringAssert.EndsWith(".wav", real, "副檔名要留著，AudioType 的判斷靠它");
            }
            finally
            {
                SdoVfs.Reset();
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        [Test]
        public void EveryVolumeOpensAndMountsInPakIdOrder()
        {
            var paks = Directory.GetFiles(PakDir, "*.pak");
            Assert.Greater(paks.Length, 5, "應該有好幾卷");

            var ids = new System.Collections.Generic.List<uint>();
            foreach (var p in paks)
            {
                PakProvider prov;
                Assert.IsTrue(PakProvider.TryOpen(p, out prov), Path.GetFileName(p) + " 開不起來");
                using (prov)
                {
                    Assert.Greater(prov.Count, 0, Path.GetFileName(p) + " 是空的");
                    ids.Add(prov.PakId);
                }
            }
            // pakId 必須唯一 —— 撞號會讓兩卷搶同一個優先權，覆蓋關係就變成隨機的。
            CollectionAssert.AllItemsAreUnique(ids, "pakId 撞號：掛載順序會變成不確定");
        }
    }
}
