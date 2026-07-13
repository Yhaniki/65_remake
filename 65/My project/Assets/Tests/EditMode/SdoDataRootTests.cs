using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Sdo.Settings;

namespace Sdo.Tests
{
    /// <summary>SdoDataRoot 的純函式部分（候選排序 / 挑選 / fallback）。資產與存檔共用這一份解析，所以這裡的
    /// 順序就是「editor 讀哪棵樹、存檔寫哪裡」的唯一真相。</summary>
    public class SdoDataRootTests
    {
        private const string Repo = @"H:\65_remake";
        private const string ExeDir = @"H:\65_remake\65\My project";      // editor 下 = Assets 的上一層
        private static string Full(string p) => Path.GetFullPath(p);
        private static string Extracted(string repo) => Full(Path.Combine(repo, "assets", "sdox_offline", "Extracted"));
        private static string Data(string dir) => Full(Path.Combine(dir, "DATA"));

        [Test]
        public void CandidateRoots_Editor_PrefersDevTree_OverExeData()
        {
            var c = SdoDataRoot.CandidateRoots(null, ExeDir, Repo, null, isEditor: true);
            Assert.AreEqual(Extracted(Repo), c[0]);   // dev 資料樹先
            Assert.AreEqual(Data(ExeDir), c[1]);      // <exe>/DATA 後（editor 下的空殼 DATA 不該搶先）
        }

        [Test]
        public void CandidateRoots_Player_PrefersExeData()
        {
            var c = SdoDataRoot.CandidateRoots(null, ExeDir, Repo, null, isEditor: false);
            Assert.AreEqual(Data(ExeDir), c[0]);
            Assert.AreEqual(Extracted(Repo), c[1]);   // dev 版型仍留作後備
        }

        [Test]
        public void CandidateRoots_ConfiguredOverride_WinsEverywhere()
        {
            foreach (var editor in new[] { true, false })
            {
                var c = SdoDataRoot.CandidateRoots(@"H:\65_remake_clean\DATA", ExeDir, Repo, null, editor);
                Assert.AreEqual(Full(@"H:\65_remake_clean\DATA"), c[0], "editor=" + editor);
            }
        }

        [Test]
        public void CandidateRoots_Editor_IncludesWorktreeRepos_InOrder_AndDedupes()
        {
            const string worktree = @"H:\65_remake-shop";
            var devRepos = new List<string> { Repo, Repo, @"H:\65_remake-primary" };   // 主 worktree 可能重複回報
            var c = SdoDataRoot.CandidateRoots(null, ExeDir, worktree, devRepos, isEditor: true);

            Assert.AreEqual(Extracted(worktree), c[0]);              // 自己的 repo 先
            Assert.AreEqual(Extracted(Repo), c[1]);                  // 再來是 git 主 worktree
            Assert.AreEqual(Extracted(@"H:\65_remake-primary"), c[2]);
            CollectionAssert.AllItemsAreUnique(c);                   // 去重
        }

        [Test]
        public void CandidateRoots_SkipsNullAndEmptyInputs()
        {
            // exeDir/repo 都不明 → 一個候選都不給（絕不塞相對路徑 "DATA"，那會被 GetFullPath 綁到目前工作目錄）。
            var c = SdoDataRoot.CandidateRoots(null, null, null, new List<string> { null, "" }, isEditor: true);
            CollectionAssert.IsEmpty(c);
        }

        [Test]
        public void CandidateRoots_AreAllAbsolute()
        {
            var c = SdoDataRoot.CandidateRoots(null, ExeDir, Repo, new List<string> { @"H:\65_remake-shop" }, isEditor: true);
            foreach (var p in c) Assert.IsTrue(Path.IsPathRooted(p), p + " 不是絕對路徑");
        }

        [Test]
        public void PickRoot_TakesFirstMatch()
        {
            var candidates = new[] { @"C:\a", @"C:\b", @"C:\c" };
            Assert.AreEqual(@"C:\b", SdoDataRoot.PickRoot(candidates, "fallback", p => p != @"C:\a"));
        }

        [Test]
        public void PickRoot_NoMatch_UsesFallback()
        {
            var candidates = new[] { @"C:\a", @"C:\b" };
            Assert.AreEqual("fallback", SdoDataRoot.PickRoot(candidates, "fallback", p => false));
        }

        [Test]
        public void PickRoot_PredicateThrows_TreatedAsNoMatch()
        {
            var candidates = new[] { @"C:\a", @"C:\b" };
            Func<string, bool> pred = p => p == @"C:\a" ? throw new IOException("boom") : true;
            Assert.AreEqual(@"C:\b", SdoDataRoot.PickRoot(candidates, "fallback", pred));
        }

        [Test]
        public void PickRoot_EmptyCandidates_UsesFallback()
        {
            Assert.AreEqual("fallback", SdoDataRoot.PickRoot(new string[0], "fallback", p => true));
            Assert.AreEqual("fallback", SdoDataRoot.PickRoot(null, "fallback", p => true));
        }

        [Test]
        public void LooksLikeGameDataRoot_RejectsProfileOnlyShell()
        {
            // 只有 PROFILE 的空殼 DATA（editor 下 <My project>/DATA 就是這樣長出來的）不能被當成資料樹 ——
            // 這正是存檔曾經跟資產分家的根因。
            var tmp = Path.Combine(Path.GetTempPath(), "sdo_root_test_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(tmp, "PROFILE", "00000000"));
                Assert.IsFalse(SdoDataRoot.LooksLikeGameDataRoot(tmp));

                Directory.CreateDirectory(Path.Combine(tmp, "SCENE"));
                Assert.IsTrue(SdoDataRoot.LooksLikeGameDataRoot(tmp));   // 有真資料才算數
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        [Test]
        public void LooksLikeGameDataRoot_RejectsMissingOrEmptyPath()
        {
            Assert.IsFalse(SdoDataRoot.LooksLikeGameDataRoot(null));
            Assert.IsFalse(SdoDataRoot.LooksLikeGameDataRoot(""));
            Assert.IsFalse(SdoDataRoot.LooksLikeGameDataRoot(Path.Combine(Path.GetTempPath(), "sdo_no_such_dir_" + Guid.NewGuid().ToString("N"))));
        }

        [Test]
        public void ProfileDir_IsProfileUnderRoot()
        {
            var saved = SdoDataRoot.Root;
            try
            {
                SdoDataRoot.Root = @"H:\65_remake_clean\DATA";
                Assert.AreEqual(Path.Combine(@"H:\65_remake_clean\DATA", "PROFILE"), SdoDataRoot.ProfileDir);
            }
            finally { SdoDataRoot.Root = saved; }
        }
    }
}
