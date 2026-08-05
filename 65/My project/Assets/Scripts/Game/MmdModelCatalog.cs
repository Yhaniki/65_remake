using System;
using System.Collections.Generic;
using System.IO;

namespace Sdo.Game
{
    /// <summary>
    /// The list of MMD models available to <see cref="MmdAvatarSwap"/>. Drop a model (the whole extracted folder: the
    /// .pmx plus its textures/Toon/Sph sub-folders) under <c>DATA/ADDON/MODEL/&lt;name&gt;/</c> and it shows up in the
    /// 設定面板的 MMD 分頁; nothing is hardcoded to any one model.
    ///
    /// 一個 <b>drop</b> ＝ MODEL/ 底下的一個資料夾 ＝ 使用者丟進來的那一包。掃描對「那一包長什麼樣」很寬容,
    /// 因為壓縮檔解出來的樣子差很多:
    /// <list type="bullet">
    /// <item>.pmx 可能就在那一層,也可能深埋好幾層(<c>MODEL/包/包/包/01-モデル/角色/x.pmx</c> —— 有些作者
    /// 連壓兩層,底下再分角色資料夾)。所以往下找到 <see cref="MaxDepth"/> 層。</item>
    /// <item>一包裡可以有<b>好幾個模型</b>(同一個作者的三個角色)。以前一個資料夾只認一個 .pmx,那三個裡
    /// 只有一個進得了清單。現在每個 .pmx 都是一個項目 —— 除了……</item>
    /// <item>……<b>語言版本</b>:同一具 mesh 的 -JP / -EN / -CN 三份併成一個,取 JP 那份,因為
    /// <see cref="MmdBoneMap"/> 認的是日文骨名(拿 EN 那份解析得動但沒有一根骨對得上,模型會站著不動)。</item>
    /// <item><b>組立キット</b>:有些包附「可以拼上去的零件」(裙子/靴子/手套/內搭各自一個 .pmx,幾十個),
    /// 那些不是角色,全列出來只會把清單淹掉。零件的 mesh 依定義是成品的子集合,所以一包裡候選超過
    /// <see cref="KitCandidateThreshold"/> 個時,只留頂點數達到「這一包最大的
    /// <see cref="KitMinVertexFraction"/>」的那幾個 —— 讀的是表頭(<see cref="PmxLoader.ProbeVertexCount"/>),
    /// 不解析幾何。篩掉幾個會寫進 log,不會靜靜吃掉。</item>
    /// </list>
    ///
    /// 名字(＝設定面板顯示、config.ini 存的那個字串)的規則刻意讓**單模型的包完全不受影響**:
    /// 一包只認出一個模型 → 名字就是資料夾名(跟以前一模一樣);認出好幾個 → 每個用自己的 .pmx 檔名,
    /// 檔名撞了才補上層資料夾名。
    ///
    /// Pure logic (the filesystem and the header probe are injected) so it can be unit-tested without touching disk;
    /// see <see cref="Discover(IEnumerable{string})"/> for the real-disk entry point.
    /// </summary>
    public static class MmdModelCatalog
    {
        /// <summary>One installed model: its display name and the .pmx to parse.</summary>
        public sealed class Entry
        {
            public string Name;      // display name, e.g. "IkaHatunemiku2025"
            public string Dir;       // folder holding the .pmx (the texture base dir)
            public string PmxPath;   // the .pmx to load
            /// <summary>使用者丟進來的那一包的根資料夾(<c>MODEL/&lt;包名&gt;</c>)。貼圖在 <see cref="Dir"/> 底下
            /// 找不到時,會退到整包裡照檔名找 —— 組立キット 型的包會把 .pmx 和貼圖分在不同的樹枝上
            /// (十六夜咲夜:.pmx 在 <c>01-モデル/角色/</c>,28 張貼圖全在隔壁的 <c>02-共通テクスチャ/</c>,
            /// 而 PMX 裡寫的是**純檔名沒有目錄**)。見 <see cref="MmdAvatar"/> 的 ResolvePath。</summary>
            public string Root;
            public override string ToString() => Name;
        }

        /// <summary>How deep below a drop folder we look for .pmx files. 組立キット 那種包會把零件分到
        /// 「體型/鞋型/部位/款式」四五層資料夾裡去,要撈得到才篩得掉(靠深度剛好漏掉不算篩掉)。</summary>
        public const int MaxDepth = 10;

        /// <summary>一包最多看這麼多個 .pmx。防的是有人把整顆硬碟指進來,不是正常的模型包。</summary>
        public const int MaxCandidatesPerDrop = 512;

        /// <summary>一包裡候選超過這個數量,才會啟動組立キット篩選(見類別註解)。</summary>
        public const int KitCandidateThreshold = 3;

        /// <summary>組立キット篩選的門檻:頂點數要達到這一包最大的這個比例才算完整角色。</summary>
        public const float KitMinVertexFraction = 0.4f;

        /// <summary>Scan <paramref name="roots"/> on the real filesystem, in order (earlier roots win a name clash).</summary>
        public static List<Entry> Discover(IEnumerable<string> roots)
            => Discover(roots, Directory.Exists, SafeDirs, SafeFiles, PmxLoader.ProbeVertexCount);

        private static IEnumerable<string> SafeDirs(string d)
        { try { return Directory.GetDirectories(d); } catch { return new string[0]; } }

        private static IEnumerable<string> SafeFiles(string d)
        { try { return Directory.GetFiles(d); } catch { return new string[0]; } }

        /// <summary>Scan with an injected filesystem, without the header probe — 組立キット 篩選因此不會啟動
        /// (見 <see cref="KeepFullModels"/>)。給不在意那一段的測試用。</summary>
        public static List<Entry> Discover(IEnumerable<string> roots, Func<string, bool> dirExists,
                                           Func<string, IEnumerable<string>> subDirs, Func<string, IEnumerable<string>> files)
            => Discover(roots, dirExists, subDirs, files, null);

        /// <summary>Scan with an injected filesystem (the unit-testable core). Entries are sorted by name; a model found
        /// under an earlier root shadows a same-named one under a later root (so a dev-tree model beats a packaged one).
        /// <paramref name="vertexCountOf"/> answers <see cref="PmxLoader.ProbeVertexCount"/> (-1 = 不知道).</summary>
        public static List<Entry> Discover(IEnumerable<string> roots, Func<string, bool> dirExists,
                                           Func<string, IEnumerable<string>> subDirs, Func<string, IEnumerable<string>> files,
                                           Func<string, int> vertexCountOf)
        {
            var found = new List<Entry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // by name: first root wins
            if (roots != null)
                foreach (var root in roots)
                {
                    if (string.IsNullOrEmpty(root) || !dirExists(root)) continue;
                    // root 自己直接放著 .pmx 也算一包(只看它自己那一層,不然整棵 MODEL/ 會被併成一包)
                    AddDrop(root, 0, found, seen, subDirs, files, vertexCountOf);
                    foreach (var drop in subDirs(root))
                    {
                        if (IsHidden(LeafName(drop))) continue;   // .net/ = 別人傳來的,不是使用者裝的(見 IsHidden)
                        AddDrop(drop, MaxDepth, found, seen, subDirs, files, vertexCountOf);
                    }
                }
            found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return found;
        }

        // 一包 → 0..n 個 Entry。
        private static void AddDrop(string dropDir, int maxDepth, List<Entry> found, HashSet<string> seen,
                                    Func<string, IEnumerable<string>> subDirs, Func<string, IEnumerable<string>> files,
                                    Func<string, int> vertexCountOf)
        {
            var candidates = new List<string>();
            Collect(dropDir, 0, maxDepth, candidates, subDirs, files);
            if (candidates.Count == 0) return;
            if (candidates.Count > MaxCandidatesPerDrop)
            {
                SdoLog.Note("mmd", $"[mmd] '{LeafName(dropDir)}' 底下有 {candidates.Count} 個 .pmx —— 只看前 "
                                   + $"{MaxCandidatesPerDrop} 個(這不像是一包模型,像是把整個資料夾指進來了)");
                candidates.RemoveRange(MaxCandidatesPerDrop, candidates.Count - MaxCandidatesPerDrop);
            }

            candidates = CollapseLanguageVariants(candidates);
            int before = candidates.Count;
            if (candidates.Count > KitCandidateThreshold) candidates = KeepFullModels(candidates, vertexCountOf);
            if (candidates.Count < before)
                SdoLog.Note("mmd", $"[mmd] '{LeafName(dropDir)}': {before} 個 .pmx 裡有 {before - candidates.Count} 個看起來是"
                                   + $"組立零件(頂點數不到最大的 {KitMinVertexFraction:P0}),沒有列進模型清單");

            if (candidates.Count == 1)
            {
                // 單模型的包:名字＝**放著那個 .pmx 的資料夾**名(與舊行為一字不差 —— 解出來是
                // MODEL/pack/miku/miku.pmx 時叫 "miku",不是 "pack")。
                Add(found, seen, LeafName(DirOf(candidates[0])), candidates[0], dropDir);
                return;
            }
            // 一包好幾個模型 → 各自用自己的 .pmx 檔名;檔名撞了就補上層資料夾名,再撞就編號。
            var stems = new List<string>(candidates.Count);
            foreach (var p in candidates) stems.Add(Path.GetFileNameWithoutExtension(p));
            stems = ShortenNames(stems);
            var names = new List<string>(stems);
            // 撞名要照**原始檔名**數,不能邊改邊數 —— 改掉第一個之後第二個就只剩自己一份了。
            for (int i = 0; i < candidates.Count; i++)
                if (CountOf(stems, stems[i]) > 1)
                    names[i] = LeafName(DirOf(candidates[i])) + " / " + stems[i];
            var localSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < candidates.Count; i++)
            {
                string unique = names[i];
                for (int n = 2; !localSeen.Add(unique); n++) unique = names[i] + " (" + n + ")";
                Add(found, seen, unique, candidates[i], dropDir);
            }
        }

        /// <summary>
        /// 把一組檔名縮短成看得完的顯示名:砍掉**結尾那一整組括號**。MMD 模型的檔名很愛把整套穿搭寫進括號
        /// (「RQ01_十六夜咲夜Ver2.20_Type-A(ダンス_帽子_ポニテA_チューブトップ_ボレロ_ショーパン_ピンヒール)」
        /// —— 60 個字,設定面板一列只放得下十幾個)。
        ///
        /// <b>只有在砍完仍然彼此不同的時候才砍</b>:同一包裡若有兩個模型只差在括號內容(同一角色的兩套穿搭),
        /// 砍掉就分不出來了,那寧可留長的。全形括號()與半形()都認。
        /// </summary>
        public static List<string> ShortenNames(List<string> stems)
        {
            if (stems == null || stems.Count == 0) return stems;
            var shortened = new List<string>(stems.Count);
            bool any = false;
            foreach (var s in stems)
            {
                string t = StripTrailingParenthetical(s);
                if (t != s) any = true;
                shortened.Add(t);
            }
            if (!any) return stems;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in shortened) if (!seen.Add(s)) return stems;   // 砍完撞名 → 整組留原樣
            return shortened;
        }

        /// <summary>結尾是 <c>(…)</c> 或 <c>(…)</c> 就把那一整組拿掉(砍完是空的就不砍)。</summary>
        public static string StripTrailingParenthetical(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            string t = s.TrimEnd();
            if (t.Length < 3) return s;
            char close = t[t.Length - 1];
            char open = close == ')' ? '(' : close == '）' ? '（' : '\0';
            if (open == '\0') return s;
            int depth = 0;
            for (int i = t.Length - 1; i >= 0; i--)
            {
                if (t[i] == close) depth++;
                else if (t[i] == open && --depth == 0)
                {
                    string head = t.Substring(0, i).TrimEnd(' ', '_', '-');
                    return head.Length > 0 ? head : s;
                }
            }
            return s;
        }

        private static int CountOf(List<string> names, string want)
        {
            int n = 0;
            foreach (var s in names) if (string.Equals(s, want, StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }

        // 同一個名字已經有人佔了(比較前面的 root、或同一個 root 比較前面的一包)→ 那一份遮住這一份,
        // 這正是「開發樹的模型蓋過打包進去的同名模型」靠的規則。
        private static void Add(List<Entry> found, HashSet<string> seen, string name, string pmx, string root)
        {
            if (!seen.Add(name)) return;
            found.Add(new Entry { Name = name, Dir = DirOf(pmx), PmxPath = pmx, Root = root });
        }

        // 這一包底下所有的 .pmx(含子資料夾,最多 maxDepth 層)。順序固定(依路徑排序),與檔案系統的列舉順序無關。
        private static void Collect(string dir, int depth, int maxDepth, List<string> outl,
                                    Func<string, IEnumerable<string>> subDirs, Func<string, IEnumerable<string>> files)
        {
            var here = new List<string>();
            foreach (var f in files(dir))
                if (!string.IsNullOrEmpty(f) && f.EndsWith(".pmx", StringComparison.OrdinalIgnoreCase)) here.Add(f);
            here.Sort(StringComparer.OrdinalIgnoreCase);
            outl.AddRange(here);
            if (depth >= maxDepth) return;
            var subs = new List<string>(subDirs(dir));
            subs.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var sub in subs)
            {
                if (IsHidden(LeafName(sub))) continue;
                Collect(sub, depth + 1, maxDepth, outl, subDirs, files);
            }
        }

        /// <summary>
        /// 同一個資料夾裡、同一具 mesh 的 <c>-JP</c> / <c>-EN</c> / <c>-CN</c> 三份併成一份,取 JP 那份 ——
        /// <see cref="MmdBoneMap"/> 認的是日文骨名,拿 EN 那份解析得動但一根骨都對不上,模型會站著不動。
        /// 檔名裡沒有語言標記的就是它自己一組,不會被併掉。
        /// </summary>
        public static List<string> CollapseLanguageVariants(List<string> pmxPaths)
        {
            var outl = new List<string>();
            var groups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // key → index into outl
            foreach (var p in pmxPaths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                string stem = Path.GetFileNameWithoutExtension(p);
                string bare = StripLanguageTag(stem);
                if (bare == null) { outl.Add(p); continue; }          // 沒有語言標記 → 自成一組
                string key = DirOf(p).ToUpperInvariant() + "|" + bare.ToUpperInvariant();
                if (!groups.TryGetValue(key, out int at)) { groups[key] = outl.Count; outl.Add(p); continue; }
                if (PrefersOver(p, outl[at])) outl[at] = p;
            }
            return outl;
        }

        // 檔名去掉語言標記後的字串,或 null = 這個檔名沒有語言標記。
        private static string StripLanguageTag(string stem)
        {
            if (string.IsNullOrEmpty(stem)) return null;
            foreach (var sep in new[] { '-', '_' })
                foreach (var lang in new[] { "JP", "EN", "CN" })
                {
                    string tag = sep + lang;
                    int i = stem.LastIndexOf(tag, StringComparison.OrdinalIgnoreCase);
                    if (i < 0) continue;
                    if (i + tag.Length != stem.Length) continue;      // 只認結尾的標記
                    return stem.Substring(0, i);
                }
            return null;
        }

        // 同一組語言版本裡,a 是不是比 b 該留:JP 優先,其次字典序小的(與列舉順序無關)。
        private static bool PrefersOver(string a, string b)
        {
            bool ja = IsJapanese(a), jb = IsJapanese(b);
            if (ja != jb) return ja;
            return string.CompareOrdinal(a, b) < 0;
        }

        private static bool IsJapanese(string path)
        {
            string up = (Path.GetFileNameWithoutExtension(path) ?? "").ToUpperInvariant();
            return up.EndsWith("-JP", StringComparison.Ordinal) || up.EndsWith("_JP", StringComparison.Ordinal);
        }

        /// <summary>
        /// 組立キット篩選:只留頂點數達到「這一包最大的 <see cref="KitMinVertexFraction"/>」的那幾個。
        /// 頂點數問不出來(-1)的一律留著 —— 不知道就不淘汰。全部都問不出來時原封不動回傳。
        /// </summary>
        public static List<string> KeepFullModels(List<string> pmxPaths, Func<string, int> vertexCountOf)
        {
            if (pmxPaths == null || pmxPaths.Count == 0 || vertexCountOf == null) return pmxPaths;
            var verts = new int[pmxPaths.Count];
            int max = 0;
            for (int i = 0; i < pmxPaths.Count; i++)
            {
                verts[i] = vertexCountOf(pmxPaths[i]);
                if (verts[i] > max) max = verts[i];
            }
            if (max <= 0) return pmxPaths;                    // 一個都問不出來 → 不淘汰
            int floor = (int)(max * KitMinVertexFraction);
            var outl = new List<string>(pmxPaths.Count);
            for (int i = 0; i < pmxPaths.Count; i++)
                if (verts[i] < 0 || verts[i] >= floor) outl.Add(pmxPaths[i]);
            return outl.Count > 0 ? outl : pmxPaths;
        }

        /// <summary>
        /// 點開頭的資料夾不算使用者裝的模型。
        ///
        /// 目前只有一個:<c>&lt;ADDON&gt;/MODEL/.net/</c> —— 連線時從別人那邊下載回來的模型
        /// (見 <see cref="MmdModelStore"/>)。它們的資料夾名是一串 hash,而且不是使用者自己選的東西,
        /// 不該出現在設定面板的模型清單裡。需要它們的地方(依 packId 找模型)是直接拼路徑,
        /// 不靠這個掃描。
        /// </summary>
        public static bool IsHidden(string folderName)
            => !string.IsNullOrEmpty(folderName) && folderName[0] == '.';

        /// <summary>The .pmx a folder should load when the caller only has a DIRECTORY to go on (a downloaded remote
        /// pack): the Japanese-named one, else the alphabetically first — deterministic regardless of
        /// directory-enumeration order. Null when the folder holds no .pmx.
        ///
        /// 🔴 本機選的模型<b>不要</b>走這裡:一個資料夾可以有好幾個模型,那時 <see cref="Entry.PmxPath"/> 才是
        /// 答案,從資料夾反推只會永遠拿到同一個。</summary>
        public static string PickPmx(IEnumerable<string> files)
        {
            string jp = null, best = null;
            if (files != null)
                foreach (var f in files)
                {
                    if (string.IsNullOrEmpty(f)) continue;
                    if (!f.EndsWith(".pmx", StringComparison.OrdinalIgnoreCase)) continue;
                    string up = f.ToUpperInvariant();
                    if (up.Contains("-JP") || up.Contains("_JP"))
                    {
                        if (jp == null || string.CompareOrdinal(f, jp) < 0) jp = f;
                    }
                    if (best == null || string.CompareOrdinal(f, best) < 0) best = f;
                }
            return jp ?? best;
        }

        /// <summary>The last path segment, tolerating a trailing separator ("a/b/" → "b").</summary>
        public static string LeafName(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return "";
            string d = dir.Replace('\\', '/').TrimEnd('/');
            int i = d.LastIndexOf('/');
            string leaf = i < 0 ? d : d.Substring(i + 1);
            return leaf.Length == 0 ? d : leaf;
        }

        /// <summary>The folder a file path sits in ("a/b/c.pmx" → "a/b"). Accepts either separator and <b>keeps the one
        /// it was given</b> — this becomes <see cref="Entry.Dir"/>, i.e. the texture base dir, and everything else in the
        /// project hands around Windows paths. Empty when there is no separator.</summary>
        public static string DirOf(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return "";
            int i = filePath.LastIndexOfAny(PathSeparators);
            return i < 0 ? "" : filePath.Substring(0, i);
        }

        private static readonly char[] PathSeparators = { '/', '\\' };

        /// <summary>Index of the model to start on: the one whose name matches <paramref name="want"/> (case-insensitive,
        /// substring — so <c>-mmdmodel miku</c> finds "IkaHatunemiku2025"), else 0. -1 when nothing is installed.</summary>
        public static int IndexOf(List<Entry> models, string want)
        {
            if (models == null || models.Count == 0) return -1;
            if (!string.IsNullOrEmpty(want))
            {
                for (int i = 0; i < models.Count; i++)
                    if (string.Equals(models[i].Name, want, StringComparison.OrdinalIgnoreCase)) return i;
                for (int i = 0; i < models.Count; i++)
                    if (models[i].Name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) return i;
                // 名字改了(一包從單模型變成多模型,項目改用 .pmx 檔名)之後,舊 config.ini 存的資料夾名要還認得出來
                for (int i = 0; i < models.Count; i++)
                    if (LeafName(models[i].Dir).IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) return i;
            }
            return 0;
        }
    }
}
