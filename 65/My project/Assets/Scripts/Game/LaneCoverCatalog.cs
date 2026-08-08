using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Sdo.Settings;

namespace Sdo.Game
{
    /// <summary>
    /// 裝了哪些**擋板圖**(lane cover) —— 蓋在音符板遠端、把音符壓到最後一刻才冒出來的那張圖(IIDX 的 lane cover)。
    ///
    /// 圖放 <c>&lt;DATA&gt;/ADDON/LANE/</c>(開發樹 <c>assets/LANE/</c>)。ADDON 就是「玩家自己丟東西進來」的那棵樹
    /// (SONG / NOTESKIN / THEME / MODEL / LANE)，<see cref="SdoExtracted.EnsureAddonDirs"/> 開機會把它建好，
    /// config.ini 的 <c>AddonFolder=</c> 還能把整棵 ADDON 指到別的碟；而且 ADDON 是 reserved 目錄、
    /// **永遠不進 pak**，所以圖放這裡自動就不會被打包 —— 不必在 build_pak.py 另外開規則。
    ///
    /// 一張圖 = 清單一項：
    /// <list type="bullet">
    ///   <item><b>Id</b>＝相對根目錄的路徑、一律用 <c>/</c>(例 <c>1/img_customize_051.jpg</c>)。存進 config.ini 的就是它
    ///         —— 不同 collection 有同名檔(<c>img_customize_003.jpg</c> 在 1/3/4d 都有)，只存檔名會撞。</item>
    ///   <item><b>Display</b>＝面板上顯示的名字。根目錄放一份 <see cref="NamesFile"/>(<c>&lt;相對路徑&gt;\t&lt;名字&gt;</c>)
    ///         就照它顯示(附的那份是曲名)；沒有對到就用檔名。</item>
    /// </list>
    ///
    /// 掃描/解析都是純函式(注入 filesystem)，所以整組可以單元測試，不必真的有檔案。
    /// </summary>
    public static class LaneCoverCatalog
    {
        /// <summary>ADDON 底下放擋板圖的那一層。</summary>
        public const string FolderName = "LANE";

        /// <summary>顯示名稱對照表的檔名(放在 <see cref="FolderName"/> 根目錄；<c>&lt;相對路徑&gt;\t&lt;名字&gt;</c>，
        /// <c>#</c> 開頭是註解)。沒有這個檔就一律用檔名當名字。</summary>
        public const string NamesFile = "names.tsv";

        /// <summary>認得的圖檔副檔名。Unity 的 <c>Texture2D.LoadImage</c> 吃 PNG/JPG；BMP 走專案自己的載入器。</summary>
        public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp" };

        /// <summary>清單裡的一張擋板圖。</summary>
        public readonly struct Entry
        {
            /// <summary>存進 config.ini 的識別字＝相對根目錄的路徑(分隔符一律 <c>/</c>)。</summary>
            public readonly string Id;
            /// <summary>面板上顯示的名字(<see cref="NamesFile"/> 對到的，否則檔名)。</summary>
            public readonly string Display;
            /// <summary>檔案的完整路徑。</summary>
            public readonly string Path;

            public Entry(string id, string display, string path) { Id = id; Display = display; Path = path; }
        }

        // ---------------------------------------------------------------- 純函式核心（單元測試直接叫）

        /// <summary>副檔名看起來是圖嗎。</summary>
        public static bool IsImage(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            foreach (var ext in ImageExtensions)
                if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary><paramref name="path"/> 相對於 <paramref name="root"/> 的識別字(分隔符正規化成 <c>/</c>)。
        /// 不在 root 底下 → null。純函式。</summary>
        public static string RelativeId(string root, string path)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path)) return null;
            string r = Norm(root).TrimEnd('/');
            string p = Norm(path);
            if (p.Length <= r.Length + 1 || !p.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase)) return null;
            return p.Substring(r.Length + 1);
        }

        /// <summary><see cref="NamesFile"/> 的內容 → 「相對路徑 → 顯示名稱」。<c>#</c> 開頭與沒有 tab 的行略過；
        /// 路徑不分大小寫、分隔符正規化。純函式。</summary>
        public static Dictionary<string, string> ParseNames(string tsv)
        {
            var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(tsv)) return res;
            foreach (var raw in tsv.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                string key = Norm(line.Substring(0, tab).Trim());
                string name = line.Substring(tab + 1).Trim();
                if (key.Length == 0 || name.Length == 0) continue;
                res[key] = name;                      // 同一個路徑寫兩次 → 後面那行贏
            }
            return res;
        }

        /// <summary>
        /// 掃 <paramref name="roots"/>(依序，**前面的根贏**同 Id 的後來者 —— 開發樹的圖蓋掉打包進去的)。
        /// 每個根底下遞迴找圖，名字查該根自己的 <see cref="NamesFile"/>。結果依顯示名稱排序(不分大小寫)。
        /// filesystem 全部注入 → 純函式，測試不必碰磁碟。
        /// </summary>
        /// <param name="dirExists">目錄在不在。</param>
        /// <param name="filesRecursive">這個根底下**所有**檔案(含子資料夾)的完整路徑。</param>
        /// <param name="readText">讀一個文字檔；讀不到回 null。</param>
        public static List<Entry> Discover(IEnumerable<string> roots, Func<string, bool> dirExists,
                                           Func<string, IEnumerable<string>> filesRecursive,
                                           Func<string, string> readText)
        {
            var found = new List<Entry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (roots == null) return found;
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root) || !dirExists(root)) continue;
                var names = ParseNames(readText(CombineUnix(root, NamesFile)));
                var files = filesRecursive(root);
                if (files == null) continue;
                foreach (var file in files)
                {
                    if (!IsImage(file)) continue;
                    string id = RelativeId(root, file);
                    if (id == null || !seen.Add(id)) continue;
                    found.Add(new Entry(id, names.TryGetValue(id, out var n) ? n : StemOf(id), file));
                }
            }
            found.Sort((a, b) =>
            {
                int c = string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
            });
            return found;
        }

        /// <summary>清單 → 設定面板那一列的選項(第一個永遠是「不使用」)。純函式。</summary>
        public static string[] OptionsOf(IReadOnlyList<Entry> entries)
        {
            var res = new string[(entries?.Count ?? 0) + 1];
            res[0] = RoomConfig.laneCoverNone;
            for (int i = 0; i < (entries?.Count ?? 0); i++) res[i + 1] = entries[i].Id;
            return res;
        }

        /// <summary>Id → 顯示名稱(「不使用」照原樣、找不到就用檔名，都不會回空字串)。純函式。</summary>
        public static string DisplayOf(IReadOnlyList<Entry> entries, string id)
        {
            id = (id ?? "").Trim();
            if (id.Length == 0 || RoomConfig.IsLaneCoverNone(id)) return RoomConfig.laneCoverNone;
            if (entries != null)
                foreach (var e in entries)
                    if (string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e.Display;
            return StemOf(id);
        }

        /// <summary>Id → 檔案完整路徑；「不使用」/沒裝那張圖 → null。純函式。</summary>
        public static string PathOf(IReadOnlyList<Entry> entries, string id)
        {
            id = (id ?? "").Trim();
            if (entries == null || id.Length == 0 || RoomConfig.IsLaneCoverNone(id)) return null;
            foreach (var e in entries)
                if (string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e.Path;
            return null;
        }

        private static string Norm(string s) => (s ?? "").Replace('\\', '/');

        private static string CombineUnix(string dir, string leaf) => Norm(dir).TrimEnd('/') + "/" + leaf;

        /// <summary>相對路徑 → 檔名(去掉目錄與副檔名)。<see cref="NamesFile"/> 沒對到時的名字。</summary>
        public static string StemOf(string id)
        {
            string p = Norm(id);
            int slash = p.LastIndexOf('/');
            if (slash >= 0) p = p.Substring(slash + 1);
            int dot = p.LastIndexOf('.');
            return dot > 0 ? p.Substring(0, dot) : p;
        }

        // ---------------------------------------------------------------- 執行期（真的去讀磁碟）

        private static List<Entry> _cache;

        /// <summary>要掃的根目錄，依序（前面的贏同名的後來者）：正式位置 <c>&lt;DATA&gt;/ADDON/LANE</c>，
        /// 然後是開發樹的 <c>&lt;repo&gt;/assets/LANE</c>（編輯器才有 —— 打包後的 player 沒有 repo，
        /// <see cref="MmdAvatarSwap.RepoAssetsDir"/> 會回 null，那時就只剩 ADDON 那個根）。</summary>
        public static IEnumerable<string> Roots()
        {
            string addon = null; try { addon = SdoExtracted.AddonLaneDir; } catch { }
            if (!string.IsNullOrEmpty(addon)) yield return addon;

            // dev：<repo>/assets/LANE。**要從 PROJECT 推導、不能從資料根推**（data_root.txt 會把資料根指到
            // 樹外的 clean\DATA，從那裡往上走根本沒有 assets/ —— 跟 MMD 模型踩過的是同一個坑）。
            string assets = null; try { assets = MmdAvatarSwap.RepoAssetsDir(); } catch { }
            if (!string.IsNullOrEmpty(assets)) yield return Path.Combine(assets, FolderName);
        }

        /// <summary>裝了哪些擋板圖(掃一次之後快取住；<see cref="Refresh"/> 重掃)。</summary>
        public static IReadOnlyList<Entry> All() => _cache ??= Discover(Roots(), DirExists, FilesRecursive, ReadText);

        /// <summary>丟掉快取，下次 <see cref="All"/> 重新掃(玩家中途丟了新圖進去)。</summary>
        public static void Refresh() => _cache = null;

        /// <summary>設定面板那一列的選項。</summary>
        public static string[] Options() => OptionsOf(All());

        /// <summary>目前設定值對應的圖在哪；「不使用」/找不到 → null(＝不畫擋板)。</summary>
        public static string PathOf(string id) => PathOf(All(), id);

        /// <summary>目前設定值在面板上顯示成什麼。</summary>
        public static string DisplayOf(string id) => DisplayOf(All(), id);

        // 設定面板住在 Sdo.Settings，它不能反向參照 Sdo.Game → 跟 MMD 模型清單一樣用注入的
        // （見 StartupConfigSchema.LaneCoversProvider）。開場面板在選性別畫面就會畫，AfterSceneLoad 來得及。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            StartupConfigSchema.LaneCoversProvider = Options;
            StartupConfigSchema.LaneCoverDisplay = DisplayOf;
        }

        private static bool DirExists(string d)
        { try { return Directory.Exists(d); } catch { return false; } }

        private static IEnumerable<string> FilesRecursive(string d)
        { try { return Directory.GetFiles(d, "*", SearchOption.AllDirectories); } catch { return Array.Empty<string>(); } }

        private static string ReadText(string p)
        { try { return File.Exists(p) ? File.ReadAllText(p) : null; } catch { return null; } }
    }
}
