using System;

namespace Sdo.Osu
{
    /// <summary>一個檔案能不能跟著歌一起傳出去,以及不能的話是為什麼。</summary>
    public enum PackFileVerdict
    {
        /// <summary>可以傳。</summary>
        Include = 0,
        /// <summary>影片。使用者明確要求過濾掉 —— 而且這個遊戲根本不播影片(全 repo 沒有 VideoPlayer)。</summary>
        Video,
        /// <summary>執行檔 / 腳本。安全考量:絕不把可執行的東西搬到別人的磁碟上。</summary>
        Executable,
        /// <summary>壓縮檔。又大又冗餘(內容通常就是旁邊那些檔)。</summary>
        Archive,
        /// <summary>遊戲自己生成的東西(CD 圖 / 舞蹈 / 側車檔)—— 收端會自己重生,傳了是浪費。</summary>
        Generated,
        /// <summary>
        /// 跟著一起傳,但**不參與 packId**。
        ///
        /// 給的是「這份東西的一部分,可是不構成它的身分」那一類 —— 目前只有模型資料夾裡的
        /// <c>physics.ini</c>(布料調校)與 <c>desktop.ini</c>。收端需要它(不然頭髮的手感不一樣),
        /// 但它一旦進了 packId,同一份模型就會因為「有沒有調過布料」而變成兩個不同的 id。
        /// 見 <see cref="ModelPackFilter.GeneratedFileName"/> 與 <see cref="ModelPackId.BuildManifest"/>。
        /// </summary>
        Companion,
        /// <summary>不在白名單裡的副檔名。不認得的東西一律不傳。</summary>
        UnknownType,
        /// <summary>單檔太大。</summary>
        TooBig,
        /// <summary>路徑不安全(見 <see cref="SafeRelPath"/>)。</summary>
        UnsafePath,
        /// <summary>目錄層數太深。</summary>
        TooDeep,
    }

    /// <summary>
    /// 決定一首外部歌的哪些檔案可以跨網路傳輸。**client 與 server 編同一份**:
    /// client 用它決定要上傳什麼、要算什麼進 packId;server 用它重新驗證上傳者送來的清單
    /// (🔴 **絕不信任 host** —— 重算 packId、重驗每個路徑、重算每個檔的 SHA-256)。
    ///
    /// 白名單制:只有明確認得的副檔名可以傳。影片/執行檔/壓縮檔另外有專屬的判定值，
    /// 不是因為白名單擋不住它們(擋得住)，而是為了能回報給 host 看:
    /// 「跳過 3 個影片檔(共 87 MB)」比「跳過 3 個檔」有用得多。
    ///
    /// 純字串與數字邏輯，零 IO、零 UnityEngine。
    /// </summary>
    public static class SongPackFilter
    {
        /// <summary>
        /// 可以傳的副檔名。
        /// 譜面 <c>.osu .sm .gn .mc</c>;歌包索引 <c>.tsv</c>(sdo_pack.tsv);音檔 <c>.ogg .mp3 .wav</c>;
        /// 圖 <c>.png .jpg .jpeg .bmp</c>;osu 的分鏡 <c>.osb</c> 與 <c>skin.ini</c>。
        /// 與 <c>ExternalScanCache.Chartish</c> 對齊，另外多收 osu 需要的兩個。
        /// </summary>
        private static readonly string[] Allowed =
        {
            ".osu", ".sm", ".gn", ".mc", ".tsv",
            ".ogg", ".mp3", ".wav",
            ".png", ".jpg", ".jpeg", ".bmp",
            ".osb", ".ini",
        };

        /// <summary>
        /// 影片。<c>.ts</c> 在別的場合是 TypeScript，但在歌曲資料夾裡它是 MPEG transport stream。
        /// </summary>
        private static readonly string[] Videos =
        {
            ".mp4", ".avi", ".flv", ".wmv", ".mkv", ".mov", ".webm",
            ".mpg", ".mpeg", ".m4v", ".ts", ".rmvb", ".asf", ".ogv", ".3gp",
        };

        /// <summary>執行檔與腳本。</summary>
        private static readonly string[] Executables =
        {
            ".exe", ".dll", ".bat", ".cmd", ".sh", ".ps1", ".msi",
            ".scr", ".lnk", ".com", ".vbs", ".js", ".jar", ".pif", ".cpl",
        };

        private static readonly string[] Archives =
        {
            ".zip", ".rar", ".7z", ".osz", ".osk", ".gz", ".tar", ".bz2",
        };

        /// <summary>圖片(套較嚴的大小上限 —— 4K 背景圖對 800×600 的遊戲毫無意義)。</summary>
        private static readonly string[] Images = { ".png", ".jpg", ".jpeg", ".bmp" };

        /// <summary>
        /// 目錄深度上限:歌曲資料夾本身(0)+ 一層子夾(1)。
        /// osu 的資料夾偶爾會有一層 <c>sb/</c> 放分鏡素材,再深就不正常了。
        /// </summary>
        public const int MaxDepth = 1;

        /// <summary>只看路徑與副檔名的判定(不需要知道檔案大小)。</summary>
        public static PackFileVerdict ClassifyPath(string relPath)
        {
            if (!SafeRelPath.IsSafe(relPath)) return PackFileVerdict.UnsafePath;

            if (Depth(relPath) > MaxDepth) return PackFileVerdict.TooDeep;

            string name = FileNameOf(relPath);
            if (IsGenerated(name)) return PackFileVerdict.Generated;

            string ext = ExtensionOf(name);
            if (Has(Videos, ext)) return PackFileVerdict.Video;
            if (Has(Executables, ext)) return PackFileVerdict.Executable;
            if (Has(Archives, ext)) return PackFileVerdict.Archive;
            if (!Has(Allowed, ext)) return PackFileVerdict.UnknownType;

            return PackFileVerdict.Include;
        }

        /// <summary>路徑 + 大小的完整判定。</summary>
        public static PackFileVerdict Classify(string relPath, long lengthBytes)
        {
            var v = ClassifyPath(relPath);
            if (v != PackFileVerdict.Include) return v;

            if (lengthBytes < 0) return PackFileVerdict.TooBig;   // 讀不到大小 → 當成不能傳
            if (lengthBytes > NetPackLimits.MaxSingleFileBytes) return PackFileVerdict.TooBig;

            string ext = ExtensionOf(FileNameOf(relPath));
            if (Has(Images, ext) && lengthBytes > NetPackLimits.MaxImageFileBytes) return PackFileVerdict.TooBig;

            return PackFileVerdict.Include;
        }

        public static bool IsTransferable(string relPath, long lengthBytes)
            => Classify(relPath, lengthBytes) == PackFileVerdict.Include;

        /// <summary>
        /// 是遊戲自己在歌曲資料夾裡生出來的東西嗎?
        /// 側車檔(<c>sdoinfo.dat</c> 與改名前的 <c>sdo.header</c>)、合成的 CD 碟圖
        /// (<c>cd.png</c> / <c>cd_&lt;slug&gt;_&lt;hash&gt;.png</c>)、生成的舞蹈
        /// (<c>dance.dps</c> / <c>dance_&lt;…&gt;.dps</c>)。
        ///
        /// 🔴 這份判定是 <c>ExternalScanCache</c>(快取失效判斷)與本檔(傳輸過濾)**共用的唯一真相**。
        /// 兩邊各寫一份的話,某天新增一種生成物只改了一邊 —— 快取那邊會變成「播完一首歌就讓自己的
        /// 快取失效」,傳輸這邊會變成「把收端自己會重生的東西傳過去」,而且都不會有測試抓到。
        /// </summary>
        public static bool IsGenerated(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            if (string.Equals(fileName, SongSidecar.FileName, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(fileName, SongSidecar.LegacyFileName, StringComparison.OrdinalIgnoreCase)) return true;
            string n = fileName.ToLowerInvariant();
            // 舞蹈那一半的判定住在 SongSidecar —— 命名規則是它的 DpsFileName 定的,而「哪些 .dps 是我們生的」
            // 也決定了 #DPS 手寫指向客製舞時不該被覆寫(見 SongSidecar.IsGeneratedDpsName)。
            return n == "cd.png" || n.StartsWith("cd_", StringComparison.Ordinal)
                || SongSidecar.IsGeneratedDpsName(n);
        }

        // ---- 小工具(不用 System.IO.Path —— 那會跟著平台的分隔符,而我們處理的是 wire 上的路徑) ----

        /// <summary>路徑裡的目錄層數(<c>a.osu</c> → 0、<c>sb/a.png</c> → 1)。</summary>
        public static int Depth(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return 0;
            int n = 0;
            for (int i = 0; i < relPath.Length; i++)
                if (relPath[i] == '/' || relPath[i] == '\\') n++;
            return n;
        }

        /// <summary>取最後一段(檔名)。</summary>
        public static string FileNameOf(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return string.Empty;
            int cut = -1;
            for (int i = relPath.Length - 1; i >= 0; i--)
                if (relPath[i] == '/' || relPath[i] == '\\') { cut = i; break; }
            return cut < 0 ? relPath : relPath.Substring(cut + 1);
        }

        /// <summary>副檔名(含點,小寫)。沒有副檔名回空字串。</summary>
        public static string ExtensionOf(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return string.Empty;
            int dot = fileName.LastIndexOf('.');
            if (dot < 0 || dot == fileName.Length - 1) return string.Empty;
            return fileName.Substring(dot).ToLowerInvariant();
        }

        private static bool Has(string[] set, string ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            for (int i = 0; i < set.Length; i++)
                if (string.Equals(set[i], ext, StringComparison.Ordinal)) return true;
            return false;
        }
    }

    /// <summary>
    /// 傳輸相關的大小上限。
    ///
    /// 為什麼不直接用 <c>Sdo.Net.NetLimits</c>:依賴方向是 <c>Sdo.Net → Sdo.Osu</c>,
    /// 反過來引用會變成循環依賴。所以歌曲過濾這幾個數字放在 Sdo.Osu 這邊,
    /// <c>NetLimits</c> 那邊的同名常數直接指過來(有測試斷言兩邊相等,防漂移)。
    /// </summary>
    public static class NetPackLimits
    {
        /// <summary>單檔上限。32 MB 擋的是「改名成 .ogg 的影片」—— 正常音檔 3-10 MB 不會撞到。</summary>
        public const long MaxSingleFileBytes = 32L * 1024 * 1024;

        /// <summary>圖片單檔上限。</summary>
        public const long MaxImageFileBytes = 4L * 1024 * 1024;

        /// <summary>
        /// 單首歌(過濾後)的檔案數上限。
        ///
        /// 🔴 200 太低:**key 音**的圖每個 note 一個 wav,幾百個檔是正常的,不是可疑的。
        /// 實機上 STAGER 有 291 個檔就直接被擋掉(server 回 <c>tooBig — 檔案數不合理:291</c>),
        /// 而且症狀很難懂 —— 缺歌的人永遠補不到,每次回房又重試一次。
        ///
        /// 上限仍然要有,因為整份 manifest 是**一個訊息**送的,受
        /// <see cref="Sdo.Net.NetLimits.MaxFramePayload"/>(256 KB)限制。每一項是
        /// <c>{"path":…,"len":…,"sha256":64 hex}</c>,最壞情況(長路徑)抓 300 bytes,
        /// 600 × 300 ≈ 176 KB 還留得下餘裕。要再往上調的話先確認那個算式(有測試釘著)。
        /// </summary>
        public const int MaxPackFiles = 600;
    }
}
