using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Sdo.Settings
{
    /// <summary>
    /// 全遊戲唯一的 data-root 解析器。<see cref="Sdo.Game.SdoExtracted"/>（美術/音樂/譜面）與
    /// <see cref="ProfileManager"/>（存檔）都從這裡取根目錄 —— 以前兩邊各自複製了一份解析邏輯，結果 editor 下
    /// 資產讀 <c>assets/sdox_offline/Extracted</c>、存檔卻寫到 <c>&lt;My project&gt;/DATA/PROFILE</c>（只要那個資料夾
    /// 剛好存在就會命中），而且 SDO_DATA_ROOT 覆寫只對資產生效，存檔搬不動。單一來源就不會再漂移。
    ///
    /// 解析順序（取第一個「看起來像 SDO 資料樹」的候選，見 <see cref="LooksLikeGameDataRoot"/>）：
    ///   0) 覆寫：環境變數 <c>SDO_DATA_ROOT</c>，或 exe 同層 / repo 根的 <c>data_root.txt</c> 第一行
    ///   1) editor/dev：<c>&lt;repo&gt;/assets/sdox_offline/Extracted</c>（含 git 主 worktree 與 <c>&lt;name&gt;-suffix</c> 兄弟 repo）
    ///   2) 建置版：<c>&lt;exe&gt;/DATA</c>
    ///   3) 非 editor 的 dev 版型：repo 相對的 Extracted
    ///   4) 都不成立 → 假設建置版版型 <c>&lt;exe&gt;/DATA</c>（即使還不存在）
    ///
    /// 排序/挑選是純函式（<see cref="CandidateRoots"/> / <see cref="PickRoot"/> / <see cref="ExtractedRootFor"/>），可單元測試；
    /// 只有 <see cref="Resolve"/> 會碰 Unity 與磁碟。放在 Sdo.Settings（leaf assembly）讓 Sdo.Game / Sdo.UI 都能參照。
    /// </summary>
    public static class SdoDataRoot
    {
        /// <summary>存檔資料夾名（root 底下）：config.ini + keymaps.ini + favorites.json + 每個使用者一個 8 位數編號資料夾。</summary>
        public const string ProfileDirName = "PROFILE";

        private static string _root;

        /// <summary>SDO 資料樹根目錄。第一次取用時解析；可設值（測試/工具覆寫）。</summary>
        public static string Root
        {
            get { return _root ?? (_root = Resolve()); }
            set { _root = value; }
        }

        /// <summary>存檔根目錄：&lt;root&gt;/PROFILE。</summary>
        public static string ProfileDir { get { return Path.Combine(Root, ProfileDirName); } }

        // ---------------- pure (unit-tested) ----------------

        /// <summary>repo 目錄 → 它的 dev 資料樹路徑（&lt;repo&gt;/assets/sdox_offline/Extracted）。null/空 → null。</summary>
        public static string ExtractedRootFor(string repo)
        {
            if (string.IsNullOrEmpty(repo)) return null;
            try { return Path.GetFullPath(Path.Combine(repo, "assets", "sdox_offline", "Extracted")); }
            catch { return null; }
        }

        /// <summary>exe 所在資料夾 → 建置版資料樹（&lt;exe&gt;/DATA）。null/空 → "DATA"（相對路徑，最後的保險）。</summary>
        public static string DataDirFor(string exeDir)
        {
            if (string.IsNullOrEmpty(exeDir)) return "DATA";
            try { return Path.GetFullPath(Path.Combine(exeDir, "DATA")); }
            catch { return "DATA"; }
        }

        /// <summary>候選 root（依優先序、已去重）。純函式：不碰磁碟，也不看 Unity。</summary>
        /// <param name="configured">覆寫路徑（env / data_root.txt），沒有 → null</param>
        /// <param name="exeDir">Application.dataPath 的上一層</param>
        /// <param name="repo">repo 根（editor 下 = dataPath 往上三層）</param>
        /// <param name="devRepos">其它可能放著資料樹的 repo（git 主 worktree、兄弟 repo）；null 可</param>
        /// <param name="isEditor">editor 下 dev 資料樹優先於 &lt;exe&gt;/DATA</param>
        public static List<string> CandidateRoots(string configured, string exeDir, string repo, IList<string> devRepos, bool isEditor)
        {
            var list = new List<string>();
            AddUnique(list, configured);

            if (isEditor)
            {
                AddUnique(list, ExtractedRootFor(repo));
                if (devRepos != null)
                    foreach (var r in devRepos) AddUnique(list, ExtractedRootFor(r));
            }

            // exeDir 不明時不加：DataDirFor 會回相對路徑 "DATA"，變成「目前工作目錄」下的 DATA —— 那是誰的目錄不一定，
            // 只能當最後的 fallback（PickRoot 的 fallback 參數），不能當候選。
            if (!string.IsNullOrEmpty(exeDir)) AddUnique(list, DataDirFor(exeDir));   // 建置版：exe 同層 DATA
            AddUnique(list, ExtractedRootFor(repo));   // 非 editor 也允許 dev 版型（editor 時已在上面加過，會被去重）
            return list;
        }

        /// <summary>第一個通過 <paramref name="looksLikeRoot"/> 的候選；都不通過 → <paramref name="fallback"/>。純函式。</summary>
        public static string PickRoot(IEnumerable<string> candidates, string fallback, Func<string, bool> looksLikeRoot)
        {
            if (candidates != null && looksLikeRoot != null)
                foreach (var c in candidates)
                {
                    if (string.IsNullOrEmpty(c)) continue;
                    bool ok;
                    try { ok = looksLikeRoot(c); } catch { ok = false; }
                    if (ok) return c;
                }
            return fallback;
        }

        private static void AddUnique(List<string> list, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { path = Path.GetFullPath(path); } catch { }
            foreach (var p in list)
                if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(path);
        }

        // ---------------- disk / Unity ----------------

        /// <summary>這個資料夾長得像 SDO 資料樹嗎（有 AVATAR/*.HRC、3DEFT、SCENE 或 UI/GAMEPLAY 其中之一）。
        /// 這道檢查也是為什麼只有 PROFILE 的空殼 DATA 資料夾不會被誤認成 root。</summary>
        public static bool LooksLikeGameDataRoot(string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            try
            {
                if (!Directory.Exists(root)) return false;
                if (File.Exists(Path.Combine(root, "AVATAR", "FEMALE.HRC"))) return true;
                if (File.Exists(Path.Combine(root, "AVATAR", "MALE.HRC"))) return true;
                if (Directory.Exists(Path.Combine(root, "3DEFT"))) return true;
                if (Directory.Exists(Path.Combine(root, "SCENE"))) return true;
                if (Directory.Exists(Path.Combine(root, "UI", "GAMEPLAY"))) return true;
            }
            catch { }
            return false;
        }

        private static string Resolve()
        {
            string exeDir = null, repo = null;
            try { exeDir = Directory.GetParent(Application.dataPath)?.FullName; } catch { }
            try { repo = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..")); } catch { }

            bool isEditor = false;
            try { isEditor = Application.isEditor; } catch { }

            var devRepos = new List<string>();
            if (isEditor && !string.IsNullOrEmpty(repo))
            {
                var primary = ResolveGitPrimaryWorktree(repo);
                if (!string.IsNullOrEmpty(primary)) devRepos.Add(primary);
                var sibling = DashPrefixSibling(repo);
                if (!string.IsNullOrEmpty(sibling)) devRepos.Add(sibling);
            }

            var candidates = CandidateRoots(ConfiguredRoot(exeDir, repo), exeDir, repo, devRepos, isEditor);
            return PickRoot(candidates, DataDirFor(exeDir), LooksLikeGameDataRoot);
        }

        /// <summary>覆寫來源：env <c>SDO_DATA_ROOT</c> 優先，否則 exe 同層 / repo 根的 <c>data_root.txt</c> 第一行。
        /// 讓遊戲指向任何備好的 DATA 樹（例如剪枝過的乾淨包）而不必重 build。沒設 → null。</summary>
        private static string ConfiguredRoot(string exeDir, string repo)
        {
            try
            {
                var e = Environment.GetEnvironmentVariable("SDO_DATA_ROOT");
                if (!string.IsNullOrEmpty(e)) return e.Trim();
            }
            catch { }

            foreach (var dir in new[] { exeDir, repo })
            {
                if (string.IsNullOrEmpty(dir)) continue;
                try
                {
                    var f = Path.Combine(dir, "data_root.txt");
                    if (File.Exists(f))
                    {
                        var line = File.ReadAllText(f).Trim();
                        if (!string.IsNullOrEmpty(line)) return line;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>worktree 的 .git 檔 → 主 worktree 的路徑（資料樹只放在主 repo）。非 worktree → repo 自己；失敗 → null。</summary>
        private static string ResolveGitPrimaryWorktree(string repo)
        {
            try
            {
                var dotGit = Path.Combine(repo, ".git");
                if (Directory.Exists(dotGit)) return repo;
                if (!File.Exists(dotGit)) return null;

                var text = File.ReadAllText(dotGit).Trim();
                const string prefix = "gitdir:";
                if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

                var gitDir = text.Substring(prefix.Length).Trim();
                if (!Path.IsPathRooted(gitDir))
                    gitDir = Path.GetFullPath(Path.Combine(repo, gitDir));

                var dir = new DirectoryInfo(gitDir);
                while (dir != null && !string.Equals(dir.Name, ".git", StringComparison.OrdinalIgnoreCase))
                    dir = dir.Parent;
                return dir?.Parent?.FullName;
            }
            catch { return null; }
        }

        /// <summary>"H:\65_remake-shop" → "H:\65_remake"：worktree 命名慣例下的主 repo。沒有 '-' → null。</summary>
        private static string DashPrefixSibling(string repo)
        {
            try
            {
                var parent = Directory.GetParent(repo)?.FullName;
                var name = Path.GetFileName(repo);
                var dash = name.IndexOf('-');
                if (parent == null || dash <= 0) return null;
                return Path.Combine(parent, name.Substring(0, dash));
            }
            catch { return null; }
        }
    }
}
