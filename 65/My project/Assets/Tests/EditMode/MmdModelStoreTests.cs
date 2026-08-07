using System.IO;
using NUnit.Framework;
using Sdo.Game;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// 「這個 packId 的模型在本機哪裡」的解析(<see cref="MmdModelStore"/>)。
    ///
    /// 這是連線那條路上唯一會碰檔案系統的一段,而它決定的是**「要不要去下載」** ——
    /// 答錯的兩個方向都很難查:說沒有 → 每次進房都重下一份已經在硬碟上的模型;
    /// 說有(但其實是別份)→ 那個人在你畫面上長成別人的樣子,而且沒有任何錯誤訊息。
    /// </summary>
    public class MmdModelStoreTests
    {
        private string _tmp;

        [SetUp]
        public void MakeTemp()
        {
            _tmp = Path.Combine(Path.GetTempPath(), "sdo_mmdstore_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tmp);
        }

        [TearDown]
        public void DropTemp()
        {
            try { if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true); } catch { }
        }

        private string MakeModel(string name, byte pmxFill = 7, byte texFill = 3)
        {
            string dir = Path.Combine(_tmp, name);
            Directory.CreateDirectory(Path.Combine(dir, "textures"));
            File.WriteAllBytes(Path.Combine(dir, "model.pmx"), Fill(2048, pmxFill));
            File.WriteAllBytes(Path.Combine(dir, "textures", "t.png"), Fill(4096, texFill));
            return dir;
        }

        private static byte[] Fill(int n, byte seed)
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = (byte)(i * 31 + seed);
            return b;
        }

        // ---------------------------------------------------------------- packId
        [Test]
        public void PackIdOf_IsStable_AndCached()
        {
            string dir = MakeModel("miku");
            string a = MmdModelStore.PackIdOf(dir);
            Assert.IsTrue(SongPackId.IsWellFormed(a), "算不出 packId:" + a);
            Assert.AreEqual(a, MmdModelStore.PackIdOf(dir), "同一個資料夾要得到同一個答案");
        }

        [Test]
        public void PackIdOf_IsEmpty_WhenTheFolderIsNotAModel()
        {
            // 一包貼圖沒有 .pmx → 不是模型 → 不能分享,也不該產生一個看起來合法的 id。
            string dir = Path.Combine(_tmp, "justpics");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "a.png"), Fill(256, 1));
            Assert.AreEqual("", MmdModelStore.PackIdOf(dir));
            Assert.AreEqual("", MmdModelStore.PackIdOf(Path.Combine(_tmp, "does-not-exist")));
            Assert.AreEqual("", MmdModelStore.PackIdOf(null));
        }

        [Test]
        public void TwoDifferentModels_GetDifferentPackIds()
        {
            string a = MakeModel("a", pmxFill: 7);
            string b = MakeModel("b", pmxFill: 9);
            Assert.AreNotEqual(MmdModelStore.PackIdOf(a), MmdModelStore.PackIdOf(b));
        }

        [Test]
        public void Forget_MakesItRecompute_AfterTheFolderChanges()
        {
            // 快取是「同一個資料夾永遠是同一個答案」的假設換來的。下載完成之後那個假設不成立
            // (資料夾剛剛才被寫出來)→ 一定要能作廢,否則新裝好的模型會被記成「算不出來」。
            string dir = Path.Combine(_tmp, "later");
            Directory.CreateDirectory(dir);
            Assert.AreEqual("", MmdModelStore.PackIdOf(dir), "空資料夾應該算不出 packId");

            File.WriteAllBytes(Path.Combine(dir, "model.pmx"), Fill(1024, 5));
            Assert.AreEqual("", MmdModelStore.PackIdOf(dir), "沒作廢之前應該還是記著舊答案");

            MmdModelStore.Forget(dir);
            Assert.IsTrue(SongPackId.IsWellFormed(MmdModelStore.PackIdOf(dir)), "作廢之後應該重算得出來");
        }

        // ---------------------------------------------------------------- 依 packId 找資料夾
        [Test]
        public void DirForPack_FindsAnInstalledModelByContent()
        {
            string dir = MakeModel("miku");
            string pack = MmdModelStore.PackIdOf(dir);
            var installed = new[] { new MmdModelCatalog.Entry { Name = "miku", Dir = dir, PmxPath = Path.Combine(dir, "model.pmx") } };

            Assert.AreEqual(dir, MmdModelStore.DirForPack(pack, installed));
        }

        [Test]
        public void DirForPack_ReturnsNull_ForSomethingWeDoNotHave()
        {
            // 這個 null 就是「去下載」的觸發條件 —— 錯報成「有」的話那個人會永遠是 SDO 穿搭。
            string dir = MakeModel("miku");
            var installed = new[] { new MmdModelCatalog.Entry { Name = "miku", Dir = dir, PmxPath = Path.Combine(dir, "model.pmx") } };
            string other = ModelPackId.Compute(new[] { new PackFileEntry("x.pmx", 1, new string('d', 64)) });

            Assert.IsNull(MmdModelStore.DirForPack(other, installed));
            Assert.IsNull(MmdModelStore.DirForPack("not-a-pack-id", installed));
            Assert.IsNull(MmdModelStore.DirForPack("", installed));
            Assert.IsNull(MmdModelStore.DirForPack(null, installed));
        }

        [Test]
        public void NetDirFor_IsNamedAfterTheHash_AndIsHiddenFromTheModelPicker()
        {
            string pack = ModelPackId.Compute(new[] { new PackFileEntry("x.pmx", 1, new string('a', 64)) });
            string dir = MmdModelStore.NetDirFor(pack);
            if (dir == null) Assert.Ignore("拿不到資料根(這台沒有 DATA)");

            // 資料夾名 = packId 的 hex → 「有沒有這一份」是一次 Exists,不用掃描比對。
            StringAssert.Contains(pack.Substring(SongPackId.Prefix.Length), dir.Replace('\\', '/'));
            // 而且它在 .net 底下 → 模型掃描器會跳過(別人的模型不該出現在自己的模型清單裡)。
            StringAssert.Contains("/" + MmdModelStore.NetSubDir + "/", dir.Replace('\\', '/') + "/");
            Assert.IsTrue(MmdModelCatalog.IsHidden(MmdModelStore.NetSubDir));
        }

        [Test]
        public void NetRoot_SitsUnderTheAddonModelFolder()
        {
            // 下載回來的模型要跟**使用者自己丟模型的那一層**同一棵樹(<ADDON>/MODEL):
            // ADDON 是 reserved 目錄(永遠不進 pak),而且 config.ini 的 AddonFolder= 可以把整棵指到
            // 別顆碟 —— 寫進遊戲自己的 <DATA>/MODEL 的話,那兩件事都不成立。
            string root = MmdModelStore.NetRoot();
            if (root == null) Assert.Ignore("拿不到資料根(這台沒有 DATA)");
            Assert.AreEqual(Path.Combine(SdoExtracted.AddonModelDir, MmdModelStore.NetSubDir), root);
            StringAssert.Contains("/ADDON/MODEL/" + MmdModelStore.NetSubDir, root.Replace('\\', '/'));
        }

        [Test]
        public void NetDirFor_RefusesAMalformedPackId()
        {
            // 直接拿它去拼路徑的話,一個惡意的「packId」就是路徑穿越。
            Assert.IsNull(MmdModelStore.NetDirFor("../../evil"));
            Assert.IsNull(MmdModelStore.NetDirFor("sha256:../evil"));
            Assert.IsNull(MmdModelStore.NetDirFor(""));
            Assert.IsNull(MmdModelStore.NetDirFor(null));
        }

        // ---------------------------------------------------------------- 一包好幾個模型:載哪一個
        /// <summary>
        /// 實測那一包的形狀:<c>NerissaRavencroft/</c> ＝ 角色本體 10.8 MB ＋ <c>Shadow.pmx</c> 0.45 MB
        /// ＋ <c>nerissa_spear.pmx</c>(一把槍)0.46 MB。傳出去的檔名是**小寫**的,而小寫之後
        /// <c>nerissa_spear</c> 的 <c>'_'</c> 排在 <c>nerissar…</c> 的 <c>'r'</c> 前面。
        /// </summary>
        private string MakeMultiModelPack()
        {
            string dir = Path.Combine(_tmp, "nerissaravencroft");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "nerissaravencroft.pmx"), Fill(8192, 1));
            File.WriteAllBytes(Path.Combine(dir, "shadow.pmx"), Fill(512, 2));
            File.WriteAllBytes(Path.Combine(dir, "nerissa_spear.pmx"), Fill(600, 3));
            return dir;
        }

        [Test]
        public void PmxForPack_LoadsTheOneHeSaidHeIsWearing()
        {
            // 這就是修的那件事:一包裡有角色、影子、一把槍,他說的那個才是他。
            string dir = MakeMultiModelPack();
            Assert.AreEqual(Path.Combine(dir, "shadow.pmx"), MmdModelStore.PmxForPack(dir, "shadow.pmx"));
            Assert.AreEqual(Path.Combine(dir, "nerissa_spear.pmx"), MmdModelStore.PmxForPack(dir, "nerissa_spear.pmx"));
        }

        [Test]
        public void PmxForPack_GuessesTheBiggest_WhenHeDidNotSay()
        {
            // 舊 client 只送 packId。字典序會挑到那把槍(小寫的 '_' 排在 'r' 前面)——
            // 角色本體幾乎必然是包裡最大的那一個,所以猜大的。
            string dir = MakeMultiModelPack();
            Assert.AreEqual(Path.Combine(dir, "nerissaravencroft.pmx"), MmdModelStore.PmxForPack(dir, ""));
            Assert.AreEqual(Path.Combine(dir, "nerissaravencroft.pmx"), MmdModelStore.PmxForPack(dir, null));
        }

        [Test]
        public void PmxForPack_FallsBackToTheGuess_WhenTheNamedFileIsGone()
        {
            // 他手動改過檔名 / 包被動過。退回猜一個仍然比「整個不顯示」好 —— 他本來就穿著這一包裡的某個模型。
            string dir = MakeMultiModelPack();
            Assert.AreEqual(Path.Combine(dir, "nerissaravencroft.pmx"), MmdModelStore.PmxForPack(dir, "nope.pmx"));
        }

        [Test]
        public void PmxForPack_RefusesToLeaveThePack()
        {
            // 那個字串是別人送來的,而這裡會拿它去 Combine 一個真實資料夾。
            string dir = MakeMultiModelPack();
            string outside = Path.Combine(_tmp, "outside.pmx");
            File.WriteAllBytes(outside, Fill(99999, 9));

            string got = MmdModelStore.PmxForPack(dir, "../outside.pmx");
            Assert.AreNotEqual(outside, got, "路徑穿越被放行了");
            Assert.AreEqual(Path.Combine(dir, "nerissaravencroft.pmx"), got, "危險路徑應該當成『他沒說』");
        }

        [Test]
        public void PmxForPack_MatchesTheLowercasedNameAgainstAnInstalledFolder()
        {
            // 傳輸清單一律小寫,但**使用者自己裝的**那一份是原始大小寫。同一份模型兩台都有時
            // (零上傳的那條路),對得起來才不會白猜一個。
            string dir = Path.Combine(_tmp, "Nerissa");
            Directory.CreateDirectory(Path.Combine(dir, "PMX"));
            File.WriteAllBytes(Path.Combine(dir, "PMX", "NerissaRavencroft.pmx"), Fill(256, 4));
            File.WriteAllBytes(Path.Combine(dir, "PMX", "Nerissa_Spear.pmx"), Fill(4096, 5));

            Assert.AreEqual(Path.Combine(dir, "PMX", "NerissaRavencroft.pmx"),
                            MmdModelStore.PmxForPack(dir, "pmx/nerissaravencroft.pmx"),
                            "大小寫不同就找不到 → 會退回猜,而這裡猜的是比較大的那把槍");
        }

        [Test]
        public void DirForPack_ReturnsThePackRoot_NotTheFolderTheePmxSitsIn()
        {
            // packId 算在整包上,對方宣告的包內路徑也是相對於整包 —— 三者要對得起來就只能是同一個資料夾。
            // (.pmx 在自己一層的包:ラプラス PMX/*.pmx + sourceimages/*.tga。)
            string root = Path.Combine(_tmp, "laplus");
            Directory.CreateDirectory(Path.Combine(root, "pmx"));
            Directory.CreateDirectory(Path.Combine(root, "sourceimages"));
            File.WriteAllBytes(Path.Combine(root, "pmx", "la.pmx"), Fill(1024, 6));
            File.WriteAllBytes(Path.Combine(root, "sourceimages", "t.png"), Fill(2048, 7));

            string pack = MmdModelStore.PackIdOf(root);
            Assert.IsTrue(SongPackId.IsWellFormed(pack), "測試前提壞了:算不出 packId");
            var installed = new[] { new MmdModelCatalog.Entry
            {
                Name = "la", Root = root,
                Dir = Path.Combine(root, "pmx"), PmxPath = Path.Combine(root, "pmx", "la.pmx"),
            } };

            Assert.AreEqual(root, MmdModelStore.DirForPack(pack, installed));
            Assert.AreEqual(Path.Combine(root, "pmx", "la.pmx"), MmdModelStore.PmxForPack(root, "pmx/la.pmx"));
        }

        [Test]
        public void HasPmx_TellsAHalfDownloadedFolderFromARealOne()
        {
            string dir = Path.Combine(_tmp, "half");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "textures.png"), Fill(64, 1));
            Assert.IsFalse(MmdModelStore.HasPmx(dir), "只有貼圖不算一份模型");

            File.WriteAllBytes(Path.Combine(dir, "m.pmx"), Fill(64, 2));
            Assert.IsTrue(MmdModelStore.HasPmx(dir));
        }
    }
}
