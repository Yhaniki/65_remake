using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// Unit tests for <see cref="MmdModelCatalog"/> — the "one folder with a .pmx = one MMD model" scan behind
    /// DATA/MODEL. The filesystem is injected, so these run without any model installed.
    /// </summary>
    public class MmdModelCatalogTests
    {
        // ---- a fake tree: dir -> (sub-dirs, files) ----
        private sealed class Fs
        {
            private readonly Dictionary<string, List<string>> _dirs = new Dictionary<string, List<string>>();
            private readonly Dictionary<string, List<string>> _files = new Dictionary<string, List<string>>();

            /// <summary>Add a file, materialising every parent folder on the way (like unzipping into the tree).</summary>
            public Fs File(string path)
            {
                int i = path.LastIndexOf('/');
                string dir = path.Substring(0, i);
                Dir(dir);
                if (!_files.TryGetValue(dir, out var fl)) _files[dir] = fl = new List<string>();
                fl.Add(path);
                return this;
            }

            public Fs Dir(string dir)
            {
                if (_dirs.ContainsKey(dir)) return this;
                _dirs[dir] = new List<string>();
                int i = dir.LastIndexOf('/');
                if (i > 0)
                {
                    string parent = dir.Substring(0, i);
                    Dir(parent);
                    _dirs[parent].Add(dir);
                }
                return this;
            }

            public bool Exists(string d) => _dirs.ContainsKey(d);
            public IEnumerable<string> Dirs(string d) => _dirs.TryGetValue(d, out var l) ? l : new List<string>();
            public IEnumerable<string> Files(string d) => _files.TryGetValue(d, out var l) ? l : new List<string>();

            public List<MmdModelCatalog.Entry> Scan(params string[] roots)
                => MmdModelCatalog.Discover(roots, Exists, Dirs, Files);

            /// <summary>Scan with a fake header probe (path → vertex count, -1 = 不知道).</summary>
            public List<MmdModelCatalog.Entry> Scan(System.Func<string, int> verts, params string[] roots)
                => MmdModelCatalog.Discover(roots, Exists, Dirs, Files, verts);
        }

        private static string[] Names(List<MmdModelCatalog.Entry> models)
        {
            var n = new string[models.Count];
            for (int i = 0; i < models.Count; i++) n[i] = models[i].Name;
            return n;
        }

        [Test]
        public void EachFolderWithAPmx_IsOneModel()
        {
            var fs = new Fs()
                .File("/DATA/MODEL/Miku/miku.pmx")
                .File("/DATA/MODEL/Miku/textures/hair.png")   // the model's own sub-folders are NOT models
                .File("/DATA/MODEL/Miku/Toon/toon01.bmp")
                .File("/DATA/MODEL/Rin/rin.pmx");

            var models = fs.Scan("/DATA/MODEL");

            Assert.AreEqual(2, models.Count);
            Assert.AreEqual("Miku", models[0].Name);
            Assert.AreEqual("/DATA/MODEL/Miku", models[0].Dir);      // == the texture base dir
            Assert.AreEqual("/DATA/MODEL/Miku/miku.pmx", models[0].PmxPath);
            Assert.AreEqual("Rin", models[1].Name);
        }

        [Test]
        public void FindsAModelNestedOneFolderDeeper()   // a zip that extracts as MODEL/pack/miku/miku.pmx
        {
            var models = new Fs().File("/DATA/MODEL/pack/miku/miku.pmx").Scan("/DATA/MODEL");

            Assert.AreEqual(1, models.Count);
            Assert.AreEqual("miku", models[0].Name);
        }

        [Test]
        public void RootHoldingThePmxIsItselfTheModel()   // the legacy layout: assets/IkaHatunemiku2025/*.pmx
        {
            var models = new Fs().File("/assets/IkaHatunemiku2025/Ika-HatsuneMiku 2025-JP.Pmx").Scan("/assets/IkaHatunemiku2025");

            Assert.AreEqual(1, models.Count);
            Assert.AreEqual("IkaHatunemiku2025", models[0].Name);
        }

        // ---------------------------------------------------------------- 一包好幾個模型

        [Test]
        public void AFolderHoldingSeveralModels_ListsEveryOne()
        {
            // 以前一個資料夾只認一個 .pmx → 同一個作者的三個角色只有一個進得了清單。
            var models = new Fs()
                .File("/M/pack/koakuma.pmx")
                .File("/M/pack/meiling.pmx")
                .File("/M/pack/sakuya.pmx")
                .Scan("/M");

            CollectionAssert.AreEqual(new[] { "koakuma", "meiling", "sakuya" }, Names(models));
            Assert.AreEqual("/M/pack/sakuya.pmx", models[2].PmxPath);
            Assert.AreEqual("/M/pack", models[2].Dir, "貼圖基準資料夾 = 放著那個 .pmx 的資料夾");
        }

        [Test]
        public void LanguageVariantsCollapse_SoTheyAreNotThreeSeparateModels()
        {
            // 這一條與「一包好幾個模型」是對立的規則,兩者必須同時成立:-JP/-EN/-CN 是同一具 mesh 的三份,
            // 不是三個模型;而且要留 JP 那份(MmdBoneMap 認日文骨名,EN 那份解析得動但一根骨都對不上)。
            var models = new Fs()
                .File("/M/Ika/Ika-HatsuneMiku 2025-CN.pmx")
                .File("/M/Ika/Ika-HatsuneMiku 2025-EN.Pmx")
                .File("/M/Ika/Ika-HatsuneMiku 2025-JP.Pmx")
                .Scan("/M");

            Assert.AreEqual(1, models.Count);
            Assert.AreEqual("Ika", models[0].Name, "只認出一個模型的包,名字還是資料夾名");
            Assert.AreEqual("/M/Ika/Ika-HatsuneMiku 2025-JP.Pmx", models[0].PmxPath);
        }

        [Test]
        public void SameFileNameInDifferentSubfolders_GetsItsFolderAsAPrefix()
        {
            var models = new Fs()
                .File("/M/pack/a/model.pmx")
                .File("/M/pack/b/model.pmx")
                .Scan("/M");

            CollectionAssert.AreEqual(new[] { "a / model", "b / model" }, Names(models));
        }

        // ---------------------------------------------------------------- 埋得很深的包

        [Test]
        public void FindsModelsBuriedSeveralFoldersDeep()
        {
            // 真實案例:十六夜咲夜Ver2.20_RQスタイル —— 壓縮檔裡又包了兩層同名資料夾,底下才分角色。
            const string p = "/M/RQ/RQ/RQ/01-model";
            var models = new Fs()
                .File(p + "/sakuya/RQ01_sakuya.pmx")
                .File(p + "/koakuma/RQ03_koakuma.pmx")
                .Scan("/M");

            CollectionAssert.AreEqual(new[] { "RQ01_sakuya", "RQ03_koakuma" }, Names(models));
        }

        // ---------------------------------------------------------------- 組立キット

        [Test]
        public void AssemblyKitParts_AreNotListedAsModels()
        {
            // 有些包附「可以拼上去的零件」(裙子/靴子/手套各一個 .pmx,幾十個)。零件的 mesh 依定義是成品的
            // 子集合 → 用表頭裡的頂點數就分得開,不必解析幾何。
            var fs = new Fs()
                .File("/M/kit/01-model/sakuya/RQ01_sakuya.pmx")
                .File("/M/kit/01-model/koakuma/RQ03_koakuma.pmx")
                .File("/M/kit/parts/skirt/tight.pmx")
                .File("/M/kit/parts/boots/pinheel.pmx")
                .File("/M/kit/parts/gloves/slender.pmx")
                .File("/M/kit/common/umbrella.pmx");

            var models = fs.Scan(path =>
                path.Contains("RQ01") ? 27290 :
                path.Contains("RQ03") ? 34968 :
                path.Contains("umbrella") ? 320 : 8000, "/M");

            CollectionAssert.AreEqual(new[] { "RQ01_sakuya", "RQ03_koakuma" }, Names(models));
        }

        [Test]
        public void UnknownVertexCounts_NeverDropAModel()
        {
            // 表頭問不出來(-1)＝「不知道」,不是 0 —— 不知道就不能淘汰,否則一個註解特別長的模型會憑空消失。
            var fs = new Fs()
                .File("/M/kit/big.pmx").File("/M/kit/a.pmx").File("/M/kit/b.pmx").File("/M/kit/c.pmx");

            var models = fs.Scan(path => path.EndsWith("big.pmx") ? 40000 : -1, "/M");

            CollectionAssert.AreEqual(new[] { "a", "b", "big", "c" }, Names(models));
        }

        [Test]
        public void WithoutAHeaderProbe_NothingIsFilteredOut()
        {
            var models = new Fs()
                .File("/M/kit/a.pmx").File("/M/kit/b.pmx").File("/M/kit/c.pmx").File("/M/kit/d.pmx")
                .Scan("/M");   // 沒有探針的那個多載

            Assert.AreEqual(4, models.Count);
        }

        [Test]
        public void MissingRootIsSkipped_NotAnError()
        {
            var models = new Fs().File("/DATA/MODEL/Miku/miku.pmx").Scan("/nope", "/DATA/MODEL");

            Assert.AreEqual(1, models.Count);
        }

        [Test]
        public void EarlierRootWinsASameNamedModel()   // dev drop-box shadows the packaged copy
        {
            var fs = new Fs()
                .File("/DATA/MODEL/Miku/packaged.pmx")
                .File("/assets/MODEL/Miku/dev.pmx");

            var models = fs.Scan("/assets/MODEL", "/DATA/MODEL");

            Assert.AreEqual(1, models.Count);
            Assert.AreEqual("/assets/MODEL/Miku/dev.pmx", models[0].PmxPath);
        }

        [Test]
        public void JapaneseVariantWins_BecauseTheBoneMapKeysOnJpNames()
        {
            var files = new[]
            {
                "/m/Ika-HatsuneMiku 2025-CN.pmx",
                "/m/Ika-HatsuneMiku 2025-EN.Pmx",
                "/m/Ika-HatsuneMiku 2025-JP.Pmx",
            };

            Assert.AreEqual("/m/Ika-HatsuneMiku 2025-JP.Pmx", MmdModelCatalog.PickPmx(files));
        }

        [Test]
        public void WithoutAJpVariant_ThePickIsDeterministic()   // enumeration order must not decide which model loads
        {
            var a = MmdModelCatalog.PickPmx(new[] { "/m/b.pmx", "/m/a.pmx" });
            var b = MmdModelCatalog.PickPmx(new[] { "/m/a.pmx", "/m/b.pmx" });

            Assert.AreEqual("/m/a.pmx", a);
            Assert.AreEqual(a, b);
        }

        [Test]
        public void NonPmxFilesAreIgnored()
        {
            Assert.IsNull(MmdModelCatalog.PickPmx(new[] { "/m/readme.txt", "/m/model.pmd", "/m/tex.png" }));
        }

        /// <summary>The wiring the fake filesystem can't check: that <see cref="MmdAvatarSwap.ModelRoots"/> points at folders
        /// that actually exist on this machine and that the scan finds a real model there. Ignored on a checkout with no
        /// model installed (same contract as PmxLoaderTests' real-model smoke test).</summary>
        [Test]
        public void Discover_OnRealDisk_FindsAnInstalledModel_WhenPresent()
        {
            var roots = new List<string>(MmdAvatarSwap.ModelRoots());
            CollectionAssert.IsNotEmpty(roots, "ModelRoots resolved to nothing — SdoExtracted.Root is broken");

            var models = MmdModelCatalog.Discover(roots);
            if (models.Count == 0) Assert.Ignore("no MMD model installed (drop one into " + roots[0] + ")");

            foreach (var m in models)
            {
                Assert.IsTrue(System.IO.File.Exists(m.PmxPath), "listed a .pmx that isn't there: " + m.PmxPath);
                Assert.IsTrue(System.IO.Directory.Exists(m.Dir), "texture dir missing: " + m.Dir);
                Assert.IsNotEmpty(m.Name);
            }
        }

        [Test]
        public void IndexOf_MatchesNameOrSubstring_CaseInsensitively()
        {
            var models = new Fs()
                .File("/M/IkaHatunemiku2025/a.pmx")
                .File("/M/Rin/b.pmx")
                .Scan("/M");   // sorted: IkaHatunemiku2025, Rin

            Assert.AreEqual(0, MmdModelCatalog.IndexOf(models, "ikahatunemiku2025"));   // exact, ignoring case
            Assert.AreEqual(0, MmdModelCatalog.IndexOf(models, "miku"));                // substring (-mmdmodel miku)
            Assert.AreEqual(1, MmdModelCatalog.IndexOf(models, "Rin"));
            Assert.AreEqual(0, MmdModelCatalog.IndexOf(models, "nothing-like-this"));   // unknown → first model
            Assert.AreEqual(0, MmdModelCatalog.IndexOf(models, null));
            Assert.AreEqual(-1, MmdModelCatalog.IndexOf(new List<MmdModelCatalog.Entry>(), "miku"));   // none installed
        }
    }
}
