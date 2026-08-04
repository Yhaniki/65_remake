using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// MMD 模型包的身分(<see cref="ModelPackId"/>)與傳輸過濾(<see cref="ModelPackFilter"/>)。
    ///
    /// 這兩件事是「別人看得到我的模型」整條路的地基,而且是**唯一同時跑在 client 與 server 上**的一段:
    /// client 用它決定上傳什麼、算出自己外觀的 packId;server 用同一份程式碼重驗上傳者送來的清單。
    /// 兩邊算出不同的答案 = 上傳永遠被拒(而且錯誤訊息會指向完全無關的地方),所以這裡的規則
    /// 必須被釘死。純函式,不碰檔案。
    /// </summary>
    public class ModelPackTests
    {
        private static PackFileEntry F(string rel, long len, string sha) => new PackFileEntry(rel, len, sha);
        private static string Sha(char c) => new string(c, 64);

        // ---------------------------------------------------------------- 過濾:白名單
        [Test]
        public void ModelFilter_Takes_ThePmx_ItsTextures_AndTheLicenceReadme()
        {
            Assert.AreEqual(PackFileVerdict.Include, ModelPackFilter.Classify("miku.pmx", 8 << 20));
            Assert.AreEqual(PackFileVerdict.Include, ModelPackFilter.Classify("textures/body.png", 1 << 20));
            Assert.AreEqual(PackFileVerdict.Include, ModelPackFilter.Classify("sph/eye.bmp", 1 << 18));
            Assert.AreEqual(PackFileVerdict.Include, ModelPackFilter.Classify("sph/hair.spa", 1 << 18));
            Assert.AreEqual(PackFileVerdict.Include, ModelPackFilter.Classify("toon/toon_skin.sph", 1 << 14));
            Assert.AreEqual(PackFileVerdict.Include, ModelPackFilter.Classify("physics.ini", 4096));
            // 使用規約幾乎都在 readme.txt —— 把模型傳給別人卻把規約留下來是不對的。
            Assert.AreEqual(PackFileVerdict.Include, ModelPackFilter.Classify("readme.txt", 8192));
        }

        [Test]
        public void ModelFilter_Rejects_Executables_Archives_Videos_AndAnythingElse()
        {
            Assert.AreEqual(PackFileVerdict.Executable, ModelPackFilter.Classify("tool.exe", 1000));
            Assert.AreEqual(PackFileVerdict.Executable, ModelPackFilter.Classify("sub/install.bat", 100));
            Assert.AreEqual(PackFileVerdict.Archive, ModelPackFilter.Classify("model.zip", 1000));
            Assert.AreEqual(PackFileVerdict.Video, ModelPackFilter.Classify("promo.mp4", 1000));
            Assert.AreEqual(PackFileVerdict.UnknownType, ModelPackFilter.Classify("source.psd", 1000));
            // .pmd 是舊版 MMD 格式,PmxLoader 讀不了 → 傳過去對方也用不了,不收。
            Assert.AreEqual(PackFileVerdict.UnknownType, ModelPackFilter.Classify("old.pmd", 1000));
        }

        [Test]
        public void ModelFilter_Rejects_PathTraversal_AndTooDeep()
        {
            Assert.AreEqual(PackFileVerdict.UnsafePath, ModelPackFilter.Classify("../evil.pmx", 100));
            Assert.AreEqual(PackFileVerdict.UnsafePath, ModelPackFilter.Classify("/abs/evil.pmx", 100));
            // 慣例佈局是 模型.pmx + textures/ 一層;再深就不是一份模型的正常長相。
            Assert.AreEqual(PackFileVerdict.Include, ModelPackFilter.Classify("textures/a.png", 100));
            Assert.AreEqual(PackFileVerdict.TooDeep, ModelPackFilter.Classify("a/b/c.png", 100));
        }

        [Test]
        public void ModelFilter_Caps_TextureSize_LooserThanSongs_ButStillCaps()
        {
            // 2048² 的人物貼圖 1.5 MB 是常態,4096² 會撞到歌曲那邊的 4 MB —— 所以放寬到 8 MB。
            Assert.AreEqual(PackFileVerdict.Include, ModelPackFilter.Classify("textures/face.png", 6L << 20));
            Assert.AreEqual(PackFileVerdict.TooBig, ModelPackFilter.Classify("textures/face.png", 9L << 20));
            // 但「改名成 .png 的影片」仍然擋得住。
            Assert.AreEqual(PackFileVerdict.TooBig, ModelPackFilter.Classify("textures/face.png", 200L << 20));
        }

        [Test]
        public void ModelFilter_And_SongFilter_DoNotAcceptEachOthersContent()
        {
            // 兩張白名單如果聯集起來,白名單制就沒有意義了 —— 歌曲資料夾可以挾帶 .pmx、
            // 模型資料夾可以挾帶 mp3。這條釘住「它們是分開的」。
            Assert.AreEqual(PackFileVerdict.UnknownType, ModelPackFilter.Classify("song.mp3", 1000));
            Assert.AreEqual(PackFileVerdict.UnknownType, ModelPackFilter.Classify("chart.osu", 1000));
            Assert.AreEqual(PackFileVerdict.UnknownType, SongPackFilter.Classify("miku.pmx", 1000));
            Assert.AreEqual(PackFileVerdict.UnknownType, SongPackFilter.Classify("sph/hair.spa", 1000));
        }

        // ---------------------------------------------------------------- packId
        [Test]
        public void PackId_IsStable_RegardlessOfFileOrder()
        {
            var a = new List<PackFileEntry> { F("miku.pmx", 10, Sha('a')), F("textures/b.png", 20, Sha('b')) };
            var b = new List<PackFileEntry> { F("textures/b.png", 20, Sha('b')), F("miku.pmx", 10, Sha('a')) };
            Assert.AreEqual(ModelPackId.Compute(a), ModelPackId.Compute(b));
            Assert.IsTrue(SongPackId.IsWellFormed(ModelPackId.Compute(a)), "packId 格式要與歌曲共用(server 用同一個驗證)");
        }

        [Test]
        public void PackId_ChangesWhenAnyFileContentChanges_IncludingTextures()
        {
            // 這是與歌曲最大的差別:歌曲只 hash 譜面(音檔換掉但長度一樣 → id 不變,刻意的取捨),
            // 模型每個檔都 hash。換一張貼圖一定要換 id,否則別人會拿快取裡的舊模型當成新的。
            var baseline = new List<PackFileEntry> { F("miku.pmx", 10, Sha('a')), F("textures/b.png", 20, Sha('b')) };
            var swappedTexture = new List<PackFileEntry> { F("miku.pmx", 10, Sha('a')), F("textures/b.png", 20, Sha('c')) };
            Assert.AreNotEqual(ModelPackId.Compute(baseline), ModelPackId.Compute(swappedTexture));

            var renamed = new List<PackFileEntry> { F("miku.pmx", 10, Sha('a')), F("textures/z.png", 20, Sha('b')) };
            Assert.AreNotEqual(ModelPackId.Compute(baseline), ModelPackId.Compute(renamed));
        }

        [Test]
        public void PackId_IsEmpty_WhenAnyHashIsMissing()
        {
            // 沒 hash 就照樣算的話,會產生一個看起來合法、但跟真正的 id 不同的字串,
            // 而那個錯誤要到 server 拒收才會被發現。寧可算不出來。
            var half = new List<PackFileEntry> { F("miku.pmx", 10, Sha('a')), F("textures/b.png", 20, "") };
            Assert.AreEqual("", ModelPackId.Compute(half));
            Assert.AreEqual("", ModelPackId.Compute(new List<PackFileEntry>()));
            Assert.AreEqual("", ModelPackId.Compute(null));
        }

        [Test]
        public void PackId_IgnoresHashCase()
        {
            var lower = new List<PackFileEntry> { F("miku.pmx", 10, new string('a', 64)) };
            var upper = new List<PackFileEntry> { F("miku.pmx", 10, new string('A', 64)) };
            Assert.AreEqual(ModelPackId.Compute(lower), ModelPackId.Compute(upper));
        }

        // ---------------------------------------------------------------- 整包驗證
        [Test]
        public void ValidPack_NeedsAPmx_NotJustTextures()
        {
            string why;
            var texturesOnly = new List<PackFileEntry> { F("textures/b.png", 20, Sha('b')) };
            Assert.IsFalse(ModelPackId.IsValidPack(texturesOnly, out why));
            Assert.AreEqual("noPmx", why, "一包貼圖沒有模型本體,收下來也沒有任何用途");

            var withModel = new List<PackFileEntry> { F("miku.pmx", 10, Sha('a')), F("textures/b.png", 20, Sha('b')) };
            Assert.IsTrue(ModelPackId.IsValidPack(withModel, out why), why);
        }

        [Test]
        public void ValidPack_RejectsAnythingTheFilterRejects()
        {
            string why;
            var withExe = new List<PackFileEntry> { F("miku.pmx", 10, Sha('a')), F("tool.exe", 20, Sha('b')) };
            Assert.IsFalse(ModelPackId.IsValidPack(withExe, out why));
            StringAssert.Contains("Executable", why);

            var traversal = new List<PackFileEntry> { F("miku.pmx", 10, Sha('a')), F("../evil.png", 20, Sha('b')) };
            Assert.IsFalse(ModelPackId.IsValidPack(traversal, out why));
            StringAssert.Contains("UnsafePath", why);
        }

        [Test]
        public void ValidPack_RejectsAWholeAssetLibraryUploadedAsOneModel()
        {
            string why;
            // 每一張都在單檔上限之內(8 MB),只是加起來超過整包上限 —— 此時要被整包那道關擋下來,
            // 而不是靠單檔限制偺倭擋到。
            var huge = new List<PackFileEntry> { F("miku.pmx", 20L << 20, Sha('a')) };
            for (int i = 0; i < 17; i++) huge.Add(F("textures/t" + i + ".png", ModelPackFilter.MaxImageFileBytes, Sha('b')));
            Assert.IsFalse(ModelPackId.IsValidPack(huge, out why));
            StringAssert.Contains("packTooBig", why);

            var manyFiles = new List<PackFileEntry> { F("miku.pmx", 10, Sha('a')) };
            for (int i = 0; i <= NetPackLimits.MaxPackFiles; i++) manyFiles.Add(F("textures/t" + i + ".png", 10, Sha('b')));
            Assert.IsFalse(ModelPackId.IsValidPack(manyFiles, out why));
            StringAssert.Contains("tooManyFiles", why);
        }

        [Test]
        public void ValidPack_AcceptsARealisticMikuSizedPack()
        {
            // 實測 assets/MODEL/IkaHatunemiku2025:一個 .pmx + 十張 2048² PNG + 一疊 sph/toon,約 10 MB。
            var files = new List<PackFileEntry> { F("ika-miku.pmx", 8L << 20, Sha('a')) };
            for (int i = 0; i < 10; i++) files.Add(F("textures/t" + i + ".png", 1200L * 1024, Sha((char)('b' + i))));
            for (int i = 0; i < 8; i++) files.Add(F("sph/s" + i + ".bmp", 256L * 1024, Sha('z')));
            files.Add(F("readme.txt", 8192, Sha('r')));

            string why;
            Assert.IsTrue(ModelPackId.IsValidPack(files, out why), why);
            Assert.IsTrue(SongPackId.IsWellFormed(ModelPackId.Compute(files)));
        }
    }
}
