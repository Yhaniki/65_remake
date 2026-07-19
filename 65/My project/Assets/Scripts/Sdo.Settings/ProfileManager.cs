using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Sdo.Settings
{
    /// <summary>
    /// 本機使用者(角色)存放區。每個 user 是資料夾 DATA/PROFILE/&lt;id&gt;（id = 零填 8 位數 == 資料夾名），裡面只放
    /// 衣服/道具（profile.json）。收藏夾(favorites.json)跟 settings.json 一樣是全帳號共用，放在 PROFILE 那層
    /// （舊版 per-user 的 favorites.json 開機時一次性併入）。目前登入的本機 user 記在 DATA/PROFILE/active.txt。
    ///
    /// 單機 v1 首次開機自動種兩個角色 —— 00000000(女) 與 00000001(男) —— 並以 00000000 為 active。刻意先不做
    /// 登入/選角 UI；<see cref="SetActive"/> + 編號資料夾本身就是多帳號的底層，未來線上版換掉 backing store
    /// （伺服器帳號 → 同一個 <see cref="UserProfile"/> 形狀）即可，呼叫端不動。
    ///
    /// 根目錄一律問 <see cref="SdoDataRoot"/>（同 assembly，維持 leaf），跟美術/音樂共用同一個 data root。id 格式/
    /// 編號/挑選都是純函式（<see cref="FormatId"/> / <see cref="TryParseId"/> / <see cref="NextFreeId"/>），可單元測試。
    /// </summary>
    public static class ProfileManager
    {
        public const string DirName = SdoDataRoot.ProfileDirName;
        public const string ActiveFileName = "active.txt";
        public const string ProfileFileName = "profile.json";
        public const string DefaultId = "00000000";

        // 首次開機種下的兩個預設角色（見 EnsureSeeded）：女=00000000(gender 0)、男=00000001(gender 1)。單機開場的
        // 男女選擇畫面(GenderSelectScreen)就是在這兩個帳號間切 active —— 選性別 == 選 profile。
        public const string FemaleSeedId = "00000000";   // 女 (gender 0 / WOMAN)
        public const string MaleSeedId = "00000001";     // 男 (gender 1 / MAN)

        private static string _root;          // DATA/PROFILE
        private static UserProfile _active;
        private static string _activeDir;     // DATA/PROFILE/<activeId>

        /// <summary>目前登入的本機角色（Boot() 後必非 null；失敗會退成記憶體內預設）。</summary>
        public static UserProfile Active => _active ?? (_active = new UserProfile());

        /// <summary>active 角色的資料夾（DATA/PROFILE/&lt;id&gt;）；Boot() 前為空字串（表示尚未落地，走 legacy 路徑）。</summary>
        public static string ActiveDir => _activeDir ?? "";

        /// <summary>切換 active user 後觸發。收藏夾是全帳號共用（PROFILE 層），不隨切換重載。</summary>
        public static event Action ActiveChanged;

        /// <summary>&lt;data root&gt;/PROFILE（延遲解析；可設值供測試覆寫）。根目錄由 <see cref="SdoDataRoot"/> 決定 ——
        /// 跟美術/音樂/譜面同一個根（含 SDO_DATA_ROOT / data_root.txt 覆寫），存檔不會再跟資產分家。</summary>
        public static string Root
        {
            get => _root ?? (_root = SdoDataRoot.ProfileDir);
            set { _root = value; }
        }

        // ---------------- boot / activate ----------------

        /// <summary>解析/建立 active user 與其資料夾。開機時呼叫一次，且在 <see cref="RoomConfig.Load"/> 之前
        /// （Load 會把舊位置的 config.ini 搬進 DATA/PROFILE 的全域檔，需要先有 PROFILE 資料夾）。任何 IO 失敗都退回記憶體內預設角色，不擋開機。</summary>
        public static void Boot()
        {
            try
            {
                Directory.CreateDirectory(Root);
                EnsureSeeded();
                string id = ReadActiveId();
                if (id == null || !Directory.Exists(Path.Combine(Root, id)))
                    id = FirstExistingId() ?? DefaultId;
                Activate(id, notify: false);
                MigrateLegacyFavorites();
                Favorites.Load(Root);   // 收藏夾在 PROFILE 層（全帳號共用，跟 settings.json 同層），不跟著 user
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Profile] boot failed, using in-memory default: {e.Message}");
                _active = new UserProfile();
                _activeDir = null;
                Favorites.Load(null);
            }
        }

        /// <summary>切換到指定 id 的 user（載入其 profile.json + 收藏 + 記錄成 active）。id 不存在則忽略。</summary>
        public static void SetActive(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            var dir = Path.Combine(Root, id);
            if (!Directory.Exists(dir))
            {
                string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                if (id == FemaleSeedId) WriteProfile(dir, new UserProfile(FemaleSeedId, "玩家001", 0) { createdAt = now });
                else if (id == MaleSeedId) WriteProfile(dir, new UserProfile(MaleSeedId, "玩家002", 1) { createdAt = now });
            }
            if (!Directory.Exists(dir)) return;
            Activate(id, notify: true);
            // config.ini 是全域一份（DATA/PROFILE/config.ini）→ 換人不重載設定，設定不跟著使用者。
            ActiveChanged?.Invoke();
        }

        private static void Activate(string id, bool notify)
        {
            _activeDir = Path.Combine(Root, id);
            Directory.CreateDirectory(_activeDir);
            _active = LoadProfile(_activeDir) ?? new UserProfile(id, DefaultNameForId(id), 0);
            _active.id = id;                 // 資料夾名是權威 id
            _active.Sanitize();
            WriteProfile(_activeDir, _active);
            WriteActiveId(id);
            if (notify) ActiveChanged?.Invoke();
        }

        /// <summary>把 active 角色寫回 profile.json（更新 lastPlayedAt）。</summary>
        public static void Save()
        {
            if (string.IsNullOrEmpty(_activeDir) || _active == null) return;
            try
            {
                _active.Sanitize();
                _active.lastPlayedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                File.WriteAllText(Path.Combine(_activeDir, ProfileFileName), JsonUtility.ToJson(_active, true), new UTF8Encoding(false));
            }
            catch (Exception e) { Debug.LogError($"[Profile] save failed: {e.Message}"); }
        }

        // ---------------- enumerate / create ----------------

        /// <summary>掃出所有現存 user（依 id 由小到大）。缺 profile.json → 用資料夾名補一個預設。</summary>
        public static List<UserProfile> List()
        {
            var res = new List<UserProfile>();
            try
            {
                if (!Directory.Exists(Root)) return res;
                var dirs = new List<string>(Directory.GetDirectories(Root));
                dirs.Sort(StringComparer.Ordinal);
                foreach (var d in dirs)
                {
                    var name = Path.GetFileName(d);
                    if (!TryParseId(name, out _)) continue;   // 只認 8 位數編號資料夾
                    var p = LoadProfile(d) ?? new UserProfile(name, DefaultNameForId(name), 0);
                    p.id = name; p.Sanitize();
                    res.Add(p);
                }
            }
            catch (Exception e) { Debug.LogWarning($"[Profile] list failed: {e.Message}"); }
            return res;
        }

        /// <summary>用下一個空編號建立一個 user 資料夾 + profile.json，回傳新角色。</summary>
        public static UserProfile Create(string name, int gender)
        {
            var ids = new List<string>();
            foreach (var p in List()) ids.Add(p.id);
            string id = NextFreeId(ids);
            var prof = new UserProfile(id, string.IsNullOrEmpty(name) ? DefaultNameForId(id) : name, gender)
            {
                createdAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
            WriteProfile(Path.Combine(Root, id), prof);
            return prof;
        }

        // ---------------- seeding ----------------

        /// <summary>首次開機（沒有任何編號資料夾）時種兩個預設角色：00000000 女、00000001 男。</summary>
        private static void EnsureSeeded()
        {
            string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            EnsureSeedProfile(FemaleSeedId, "玩家001", 0, now);
            EnsureSeedProfile(MaleSeedId, "玩家002", 1, now);
            if (ReadActiveId() == null) WriteActiveId(FemaleSeedId);
        }

        private static void EnsureSeedProfile(string id, string defaultName, int gender, string now)
        {
            var dir = Path.Combine(Root, id);
            var p = LoadProfile(dir);
            if (p == null)
            {
                WriteProfile(dir, new UserProfile(id, defaultName, gender) { createdAt = now });
                return;
            }

            bool changed = p.id != id || p.gender != gender || string.IsNullOrEmpty(p.name) || string.IsNullOrEmpty(p.createdAt);
            p.id = id;
            p.gender = gender;
            if (string.IsNullOrEmpty(p.name)) p.name = defaultName;
            if (string.IsNullOrEmpty(p.createdAt)) p.createdAt = now;
            if (changed) WriteProfile(dir, p);
        }

        // ---------------- migration ----------------

        /// <summary>一次性遷移：舊版把 favorites.json 放在各 user 資料夾（DATA/PROFILE/&lt;id&gt;/）。把殘留的
        /// per-user 收藏依 id 由小到大併入 PROFILE 層的共用檔，然後刪掉舊檔 —— user 資料夾從此只放衣服(profile.json)。
        /// 兩個種子帳號是同一位玩家（選性別==選 profile），合併不會混到別人的收藏。合併去重、可重入（失敗下次開機再試）。</summary>
        private static void MigrateLegacyFavorites()
        {
            try
            {
                var legacy = new List<string>();
                foreach (var d in Directory.GetDirectories(Root))
                {
                    if (!TryParseId(Path.GetFileName(d), out _)) continue;   // 只認 8 位數編號資料夾
                    var f = Path.Combine(d, Favorites.FileName);
                    if (File.Exists(f)) legacy.Add(f);
                }
                if (legacy.Count == 0) return;
                legacy.Sort(StringComparer.Ordinal);   // id 小 → 大 = 合併後的「加入順序」

                var shared = Path.Combine(Root, Favorites.FileName);
                var docs = new List<string>();
                if (File.Exists(shared)) docs.Add(File.ReadAllText(shared, Encoding.UTF8));   // 共用檔已有內容 → 其順序優先
                foreach (var f in legacy) docs.Add(File.ReadAllText(f, Encoding.UTF8));
                File.WriteAllText(shared, Favorites.MergeDocs(docs), new UTF8Encoding(false));
                foreach (var f in legacy) File.Delete(f);
            }
            catch (Exception e) { Debug.LogWarning($"[Profile] favorites migrate failed: {e.Message}"); }
        }

        // ---------------- pure helpers (unit-tested) ----------------

        /// <summary>整數 → 8 位數零填 id 字串。負數夾成 0。</summary>
        public static string FormatId(int n) => Mathf.Max(0, n).ToString("D8", CultureInfo.InvariantCulture);

        /// <summary>單機男女選擇 → 對應的預設 profile id：1(男)→<see cref="MaleSeedId"/>、其它(0/女)→<see cref="FemaleSeedId"/>。
        /// 純函式（GenderSelectScreen 選性別時用來決定要 SetActive 哪個帳號）。</summary>
        public static string SeededIdForGender(int gender) => gender == 1 ? MaleSeedId : FemaleSeedId;

        /// <summary>把資料夾名（"00000001"）解析成整數 id；非全數字/超界 → false。</summary>
        public static bool TryParseId(string name, out int value)
        {
            value = -1;
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var c in name) if (c < '0' || c > '9') return false;
            return int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
        }

        /// <summary>回傳現有 id 中最小的未使用編號（8 位數字串）。空 → "00000000"。</summary>
        public static string NextFreeId(IEnumerable<string> existing)
        {
            var used = new HashSet<int>();
            if (existing != null)
                foreach (var e in existing) if (TryParseId(e, out var v)) used.Add(v);
            int n = 0;
            while (used.Contains(n)) n++;
            return FormatId(n);
        }

        // ---------------- io helpers ----------------

        private static UserProfile LoadProfile(string dir)
        {
            try
            {
                var path = Path.Combine(dir, ProfileFileName);
                if (!File.Exists(path)) return null;
                var p = JsonUtility.FromJson<UserProfile>(File.ReadAllText(path, Encoding.UTF8));
                return p?.Sanitize();
            }
            catch (Exception e) { Debug.LogWarning($"[Profile] read {dir} failed: {e.Message}"); return null; }
        }

        private static void WriteProfile(string dir, UserProfile p)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ProfileFileName), JsonUtility.ToJson(p.Sanitize(), true), new UTF8Encoding(false));
        }

        private static string ReadActiveId()
        {
            try
            {
                var path = Path.Combine(Root, ActiveFileName);
                if (!File.Exists(path)) return null;
                var id = File.ReadAllText(path, Encoding.UTF8).Trim();
                return TryParseId(id, out _) ? id : null;
            }
            catch { return null; }
        }

        private static void WriteActiveId(string id)
        {
            try { File.WriteAllText(Path.Combine(Root, ActiveFileName), id, new UTF8Encoding(false)); }
            catch (Exception e) { Debug.LogWarning($"[Profile] write active failed: {e.Message}"); }
        }

        /// <summary>目前最小編號的現存 user id；沒有任何 → null。</summary>
        private static string FirstExistingId()
        {
            try
            {
                if (!Directory.Exists(Root)) return null;
                string best = null; int bestV = int.MaxValue;
                foreach (var d in Directory.GetDirectories(Root))
                {
                    var name = Path.GetFileName(d);
                    if (TryParseId(name, out var v) && v < bestV) { bestV = v; best = name; }
                }
                return best;
            }
            catch { return null; }
        }

        private static string DefaultNameForId(string id)
            => TryParseId(id, out var v) ? "玩家" + (v + 1).ToString("000", CultureInfo.InvariantCulture) : "玩家001";
    }
}
