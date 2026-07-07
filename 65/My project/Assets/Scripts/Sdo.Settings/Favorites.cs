using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Sdo.Settings
{
    /// <summary>
    /// per-user 收藏夾。用歌曲的 .gn 檔名（小寫）當 key —— 選歌清單只列 k 譜面，所以一首歌一個 key，穩定不重複。
    /// 存成 DATA/PROFILE/&lt;id&gt;/favorites.json；<see cref="ProfileManager"/> 在開機/切換 user 時指向對應資料夾。
    ///
    /// 集合運算、key 正規化、JSON 讀寫都是純函式（<see cref="Key"/> / <see cref="Parse"/> / <see cref="Serialize"/>），
    /// 可單元測試。線上版要同步收藏時，換掉 <see cref="Load"/>/<see cref="Save"/> 的 backing store 即可。
    /// </summary>
    public static class Favorites
    {
        [Serializable] private class Doc { public string[] gns = new string[0]; }   // init 消 CS0649（JsonUtility 會填）

        public const string FileName = "favorites.json";

        private static readonly HashSet<string> _set = new HashSet<string>(StringComparer.Ordinal);   // 查詢用 O(1)
        private static readonly List<string> _order = new List<string>();   // 加入順序（舊→新）；favorites.json 也照這順序存
        private static string _path;   // 目前 favorites.json 路徑（null → 只在記憶體，不落地）

        /// <summary>收藏內容變動（新增/移除/重載）時觸發，讓選歌畫面重繪。</summary>
        public static event Action Changed;

        public static int Count => _set.Count;
        public static bool IsFav(string gn) => _set.Contains(Key(gn));

        /// <summary>收藏的 key（加入順序：舊 → 新）。</summary>
        public static IReadOnlyList<string> Ordered => _order;

        /// <summary>收藏的 key（最近加入的排最前面：新 → 舊）—— 收藏頁的顯示順序。</summary>
        public static IEnumerable<string> NewestFirst()
        {
            for (int i = _order.Count - 1; i >= 0; i--) yield return _order[i];
        }

        /// <summary>正規化 .gn 路徑/檔名 → 儲存 key（取檔名、去空白、小寫）。null/空 → ""。</summary>
        public static string Key(string gn)
            => string.IsNullOrEmpty(gn) ? "" : Path.GetFileName(gn).Trim().ToLowerInvariant();

        /// <summary>加入收藏（放到順序尾端 = 最新）；已存在或空 key → false（沒有變動）。</summary>
        public static bool Add(string gn)
        {
            var k = Key(gn);
            if (k.Length == 0 || !_set.Add(k)) return false;
            _order.Add(k);   // 尾端 = 最新加入 → 收藏頁靠 NewestFirst() 排到最上面
            Save(); Changed?.Invoke(); return true;
        }

        /// <summary>移出收藏；本來就不在 → false。</summary>
        public static bool Remove(string gn)
        {
            var k = Key(gn);
            if (!_set.Remove(k)) return false;
            _order.Remove(k);
            Save(); Changed?.Invoke(); return true;
        }

        /// <summary>切換收藏狀態，回傳切換後「是否已收藏」。</summary>
        public static bool Toggle(string gn)
        {
            if (IsFav(gn)) { Remove(gn); return false; }
            return Add(gn);
        }

        // ---------------- persistence（由 ProfileManager 在開機/切換時驅動）----------------

        /// <summary>把收藏綁到某個 user 資料夾並讀入其 favorites.json。dir 為 null → 清空、只在記憶體。</summary>
        public static void Load(string dir)
        {
            _path = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, FileName);
            _set.Clear(); _order.Clear();
            try
            {
                if (_path != null && File.Exists(_path))
                    foreach (var k in Parse(File.ReadAllText(_path, Encoding.UTF8)))
                        if (_set.Add(k)) _order.Add(k);   // 檔案順序 = 加入順序（舊→新）
            }
            catch (Exception e) { Debug.LogWarning($"[Favorites] load failed: {e.Message}"); }
            Changed?.Invoke();
        }

        public static void Save()
        {
            if (_path == null) return;
            try { File.WriteAllText(_path, Serialize(_order), new UTF8Encoding(false)); }   // 照加入順序存，重載才不會丟「最近加入」資訊
            catch (Exception e) { Debug.LogError($"[Favorites] save failed: {e.Message}"); }
        }

        // ---------------- pure (unit-tested) ----------------

        /// <summary>解析 favorites.json → 正規化的 key（去重、去空）。壞 JSON → 空。</summary>
        public static IEnumerable<string> Parse(string json)
        {
            var res = new List<string>();
            if (string.IsNullOrWhiteSpace(json)) return res;
            Doc doc;
            try { doc = JsonUtility.FromJson<Doc>(json); } catch { return res; }
            if (doc?.gns == null) return res;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in doc.gns) { var k = Key(g); if (k.Length > 0 && seen.Add(k)) res.Add(k); }
            return res;
        }

        /// <summary>把 key 序列化成 favorites.json（保留傳入順序 = 加入順序、去重、去空）。不排序，才留得住「最近加入」。</summary>
        public static string Serialize(IEnumerable<string> keys)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (keys != null)
                foreach (var k in keys) if (!string.IsNullOrEmpty(k) && seen.Add(k)) list.Add(k);
            return JsonUtility.ToJson(new Doc { gns = list.ToArray() }, true);
        }

        /// <summary>測試/重置用：清掉記憶體集合與綁定路徑。</summary>
        public static void ResetForTests()
        {
            _set.Clear();
            _order.Clear();
            _path = null;
        }
    }
}
