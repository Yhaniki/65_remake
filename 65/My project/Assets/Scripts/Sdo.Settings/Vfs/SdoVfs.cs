using System;
using System.Collections.Generic;
using System.IO;

namespace Sdo.Settings.Vfs
{
    /// <summary>
    /// 全遊戲讀取 DATA 樹的唯一入口 —— 一疊由高到低的層,查檔時取第一個命中的。
    /// 格式與掛載規則見 <c>docs/architecture/data-packaging.md</c>。
    ///
    /// 路徑一律是「相對 DATA 根的相對路徑」,寫法隨意(<c>UI/GAMEPLAY/x.png</c>、
    /// <c>ui\gameplay\X.PNG</c> 都行),進來後由 <see cref="VfsPath.Normalize"/> 收斂。
    /// 找不到的東西一律回 null / false / -1,**不丟例外** —— 呼叫端搬過來時原本就都是
    /// <c>File.Exists</c> 先問再讀的寫法。
    ///
    /// 目前只掛一層 <see cref="LooseDirProvider"/>(= 現在的行為,零變化);pak 層之後再加進來,
    /// 呼叫端不用再改一次。
    ///
    /// <para><b>執行緒</b>:查詢與讀取是執行緒安全的(背景預讀資產會用到)。但
    /// <see cref="Initialise"/> 會碰 <see cref="SdoDataRoot.Root"/>,那裡面讀 <c>Application.dataPath</c>
    /// 只能在主執行緒 —— 所以開機時要明確呼叫一次,別讓背景執行緒去觸發延遲初始化。</para>
    /// </summary>
    public static class SdoVfs
    {
        /// <summary>base_*.pak 的優先權基準。</summary>
        public const int PriorityPakBase = 100;
        /// <summary>music_*.pak 的優先權基準。</summary>
        public const int PriorityPakMusic = 200;
        /// <summary>patch_NNN.pak 的優先權基準(加上 pakId,數字大者更高)。</summary>
        public const int PriorityPakPatch = 300;
        /// <summary>散裝檔案層 —— 永遠壓在所有 pak 上面,丟一個同名檔就蓋掉 pak 內的。</summary>
        public const int PriorityLoose = 1000;

        private struct Layer
        {
            public IVfsProvider Provider;
            public int Priority;
        }

        private static readonly object Gate = new object();
        private static volatile Layer[] _layers = new Layer[0];   // 已依優先權由高到低排好
        private static volatile LooseDirProvider _reserved;       // PROFILE/ADDON/CACHE/REPLAY 專用
        private static volatile bool _initialised;

        // ---------------- 掛載 ----------------

        /// <summary>用 <see cref="SdoDataRoot.Root"/> 建立預設的層疊。開機時在主執行緒呼叫一次。
        /// 重複呼叫是安全的(第二次以後直接跳過);要換根請先 <see cref="Reset"/>。</summary>
        public static void Initialise()
        {
            if (_initialised) return;
            Initialise(SdoDataRoot.Root);
        }

        /// <summary>用指定的根建立層疊 —— 測試與工具用。會清掉現有的層,並掃載 <c>&lt;root&gt;/*.pak</c>。</summary>
        public static void Initialise(string dataRoot)
        {
            lock (Gate)
            {
                DisposeLayersLocked();
                _layers = new Layer[0];
                _reserved = new LooseDirProvider(dataRoot);
                MountLocked(_reserved, PriorityLoose);
                MountPaksLocked(dataRoot);
                _initialised = true;
            }
        }

        /// <summary>掃 <c>&lt;root&gt;/*.pak</c> 並依 <c>pakId</c> 掛載。
        ///
        /// 優先權 = <see cref="PriorityPakBase"/> + pakId,所以掛載順序完全由**卷自己的 id** 決定,
        /// 不看檔名 —— 改名不會改變覆蓋關係。分卷表(<c>tools/build_pak.py</c> 的 VOLUMES)發號的規則是
        /// base 10–19、music 20+、patch 300+,對應 <see cref="PriorityPakMusic"/> / <see cref="PriorityPakPatch"/>
        /// 那兩個區段。散裝層永遠是 1000,壓在全部之上。
        ///
        /// 壞掉/開不起來的卷安靜跳過 —— 一個壞卷不該讓整個遊戲開不起來,少一層頂多是某些資產讀不到。</summary>
        private static void MountPaksLocked(string dataRoot)
        {
            if (string.IsNullOrEmpty(dataRoot)) return;

            string[] paks;
            try
            {
                if (!Directory.Exists(dataRoot)) return;
                paks = Directory.GetFiles(dataRoot, "*.pak", SearchOption.TopDirectoryOnly);
            }
            catch { return; }

            Array.Sort(paks, StringComparer.OrdinalIgnoreCase);   // 同 pakId 時順序才不會隨檔案系統跳動
            foreach (var p in paks)
            {
                PakProvider prov;
                if (!PakProvider.TryOpen(p, out prov)) continue;
                MountLocked(prov, PriorityPakBase + (int)prov.PakId);
            }
        }

        /// <summary>拆掉所有層,回到未初始化狀態(測試的 TearDown 用)。
        /// 會把 pak 的檔案控制代碼關掉 —— Windows 上沒關就刪不掉那個檔/資料夾。</summary>
        public static void Reset()
        {
            lock (Gate)
            {
                DisposeLayersLocked();
                _layers = new Layer[0];
                _reserved = null;
                _initialised = false;
            }
        }

        private static void DisposeLayersLocked()
        {
            foreach (var l in _layers)
            {
                var d = l.Provider as IDisposable;
                if (d != null) { try { d.Dispose(); } catch { } }
            }
        }

        /// <summary>加一層。同優先權時後掛的排在前面(較高)。</summary>
        public static void Mount(IVfsProvider provider, int priority)
        {
            if (provider == null) return;
            lock (Gate) { MountLocked(provider, priority); }
        }

        private static void MountLocked(IVfsProvider provider, int priority)
        {
            var list = new List<Layer>(_layers);
            int at = 0;
            while (at < list.Count && list[at].Priority >= priority) at++;
            list.Insert(at, new Layer { Provider = provider, Priority = priority });
            _layers = list.ToArray();
        }

        /// <summary>目前掛著的層,由高到低 —— 診斷用。</summary>
        public static IList<string> LayerNames()
        {
            var snapshot = _layers;
            var names = new List<string>(snapshot.Length);
            foreach (var l in snapshot) names.Add(l.Priority + " " + l.Provider.Name);
            return names;
        }

        private static Layer[] Layers()
        {
            // 延遲初始化只是保險 —— 正規流程是開機時在主執行緒明確呼叫 Initialise()。這裡吞掉例外是為了
            // 守住「查不到就回 null,不丟例外」的合約:呼叫端全是 File.Exists 先問再讀的寫法,不預期例外。
            if (!_initialised) { try { Initialise(); } catch { } }
            return _layers;
        }

        /// <summary>列舉用的目錄正規化:null / 空 / "." 一律是根("")。無效路徑 → null。</summary>
        private static string NormalizeDir(string relDir)
        {
            if (string.IsNullOrEmpty(relDir)) return "";
            return VfsPath.Normalize(relDir);
        }

        // ---------------- 查詢 ----------------

        /// <summary>找出這個路徑在最高層的條目。命中 whiteout → 回 false(那正是 whiteout 的用途:
        /// 停在這裡,不要再往下找)。</summary>
        public static bool TryGetEntry(string rel, out VfsEntry entry)
        {
            entry = default(VfsEntry);

            var norm = VfsPath.Normalize(rel);
            if (string.IsNullOrEmpty(norm)) return false;

            // reserved 目錄完全不查 pak —— 它們是可寫的明碼區,只認真實檔案系統。
            if (VfsPath.IsReserved(norm))
            {
                var res = _reserved;
                if (res == null) { Layers(); res = _reserved; }
                return res != null && res.TryGet(norm, out entry);
            }

            foreach (var layer in Layers())
            {
                VfsEntry e;
                if (!layer.Provider.TryGet(norm, out e)) continue;
                if (e.IsWhiteout) return false;
                entry = e;
                return true;
            }
            return false;
        }

        /// <summary>檔案存在嗎。</summary>
        public static bool Exists(string rel)
        {
            VfsEntry e;
            return TryGetEntry(rel, out e);
        }

        /// <summary>解開後的位元組數;不存在 → -1。</summary>
        public static long GetSize(string rel)
        {
            VfsEntry e;
            return TryGetEntry(rel, out e) ? e.Size : -1L;
        }

        /// <summary>整份讀出;不存在 → null。</summary>
        public static byte[] ReadAllBytes(string rel)
        {
            var norm = VfsPath.Normalize(rel);
            if (string.IsNullOrEmpty(norm)) return null;

            var provider = OwnerOf(norm);
            return provider == null ? null : provider.ReadAllBytes(norm);
        }

        /// <summary>開串流;不存在 → null。呼叫端負責 Dispose。</summary>
        public static Stream OpenRead(string rel)
        {
            var norm = VfsPath.Normalize(rel);
            if (string.IsNullOrEmpty(norm)) return null;

            var provider = OwnerOf(norm);
            return provider == null ? null : provider.OpenRead(norm);
        }

        /// <summary>UTF-8 文字讀出(自動吃 BOM);不存在 → null。</summary>
        public static string ReadAllText(string rel)
        {
            var bytes = ReadAllBytes(rel);
            if (bytes == null) return null;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        /// <summary>這個路徑在真實檔案系統上的絕對位置;若它只存在於 pak 裡 → null。
        ///
        /// 給那些只吃 <c>file://</c> 的 API 用(<c>UnityWebRequestMultimedia</c> 讀音訊、外部工具),
        /// 也是「這個檔能不能就地覆寫」的判準。回 null 不代表檔案不存在 —— 要問存在與否用
        /// <see cref="Exists"/>。</summary>
        public static string ResolveRealPath(string rel)
        {
            VfsEntry e;
            return TryGetEntry(rel, out e) ? e.RealPath : null;
        }

        /// <summary>擁有這個路徑最終版本的那一層;命中 whiteout 或找不到 → null。</summary>
        private static IVfsProvider OwnerOf(string norm)
        {
            if (VfsPath.IsReserved(norm))
            {
                var res = _reserved;
                if (res == null) { Layers(); res = _reserved; }
                if (res == null) return null;
                VfsEntry re;
                return res.TryGet(norm, out re) ? res : null;
            }

            foreach (var layer in Layers())
            {
                VfsEntry e;
                if (!layer.Provider.TryGet(norm, out e)) continue;
                return e.IsWhiteout ? null : layer.Provider;
            }
            return null;
        }

        // ---------------- 列舉 ----------------

        /// <summary><paramref name="relDir"/> 底下的檔案(正規化路徑),各層合併:高層蓋掉低層的同名檔、
        /// whiteout 把低層的剔掉、最後去重。順序不保證。
        ///
        /// <paramref name="pattern"/> 只比對檔名(<c>*</c> 與 <c>?</c>,大小寫不敏感);
        /// null / <c>"*"</c> / <c>"*.*"</c> = 全部。</summary>
        public static IEnumerable<string> EnumerateFiles(string relDir, string pattern = "*", bool recursive = false)
        {
            var norm = NormalizeDir(relDir);
            if (norm == null) return new string[0];   // 無效路徑（逃出根之類）→ 空,不丟例外

            var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);   // 路徑 → 要不要收
            var order = new List<string>();

            foreach (var provider in ProvidersFor(norm))
            {
                foreach (var e in provider.EnumerateUnder(norm, recursive))
                {
                    if (e.Path == null) continue;
                    if (seen.ContainsKey(e.Path)) continue;   // 高層已經表過態(收下或 whiteout 掉)

                    bool keep = !e.IsWhiteout && VfsPath.GlobMatch(VfsPath.FileName(e.Path), pattern);
                    seen[e.Path] = keep;
                    if (keep) order.Add(e.Path);
                }
            }
            return order;
        }

        /// <summary><paramref name="relDir"/> 底下的直接子目錄(正規化路徑),各層合併去重。
        /// 由檔案路徑推導 —— VFS 沒有「空目錄」這種東西,pak 也不存目錄項。</summary>
        public static IEnumerable<string> EnumerateDirectories(string relDir)
        {
            var norm = NormalizeDir(relDir);
            if (norm == null) return new string[0];

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            int start = norm.Length == 0 ? 0 : norm.Length + 1;

            foreach (var provider in ProvidersFor(norm))
            {
                foreach (var e in provider.EnumerateUnder(norm, true))
                {
                    if (e.Path == null || e.IsWhiteout || e.Path.Length <= start) continue;
                    int slash = e.Path.IndexOf('/', start);
                    if (slash < 0) continue;   // 直接子檔,不是子目錄

                    var sub = e.Path.Substring(0, slash);
                    if (seen.Add(sub)) order.Add(sub);
                }
            }
            return order;
        }

        /// <summary>要問哪些層 —— reserved 目錄只問真實檔案系統那層。</summary>
        private static IEnumerable<IVfsProvider> ProvidersFor(string norm)
        {
            if (VfsPath.IsReserved(norm))
            {
                var res = _reserved;
                if (res == null) { Layers(); res = _reserved; }
                if (res != null) yield return res;
                yield break;
            }

            foreach (var layer in Layers()) yield return layer.Provider;
        }
    }
}
