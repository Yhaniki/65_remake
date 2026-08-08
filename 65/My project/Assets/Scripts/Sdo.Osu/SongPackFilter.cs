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
        /// <summary>
        /// 工具的產出物,不是這首歌的一部分:遊戲自己生的 CD 圖與舞蹈(收端會自己重生,傳了是浪費),
        /// 以及譜面編輯器的自動存檔資料夾 <c>FileBackup/</c>(見 <see cref="SongPackFilter.EditorBackupDir"/>)。
        /// (側車檔 <c>sdoinfo.dat</c> 曾經也在這一類,現在是 <see cref="Companion"/> —— 它要傳,只是不進 packId。)
        /// </summary>
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
        /// 圖 <c>.png .jpg .jpeg .bmp .dds</c>;osu 的分鏡 <c>.osb</c> 與 <c>skin.ini</c>;
        /// **歌自帶的編舞** <c>.dps</c>、它點名的動作 <c>.mot</c>、鏡頭 <c>.cdt</c>。
        /// 與 <c>ExternalScanCache.Chartish</c> 對齊，另外多收 osu 需要的兩個與客製編舞那一組。
        ///
        /// 🔴 <c>.dps</c>/<c>.mot</c>/<c>.cdt</c> 是**使用者自己放進歌曲資料夾**的客製編舞
        /// (sidecar 的 <c>#DPS</c>/<c>#MOT</c>/<c>#CAMERA</c> 指名的那些)。不傳的話收端只會拿到
        /// 自動生成的舞 —— 同一首歌、同一場,房主跳的是作者編的,別人跳的是亂數生的。
        /// 遊戲**自己生的** <c>dance*.dps</c> 仍然不傳(<see cref="IsGenerated"/> 把它擋掉,
        /// 收端用同一個 seed 重生成,見 <c>Sdo.Game.ExternalDps</c>)。
        /// </summary>
        private static readonly string[] Allowed =
        {
            ".osu", ".sm", ".gn", ".mc", ".tsv",
            ".ogg", ".mp3", ".wav",
            ".png", ".jpg", ".jpeg", ".bmp", ".dds",
            ".osb", ".ini",
            ".dps", ".mot", ".cdt",
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
        private static readonly string[] Images = { ".png", ".jpg", ".jpeg", ".bmp", ".dds" };

        /// <summary>
        /// 跟著歌一起走、但**不屬於歌的身分**的檔(<see cref="PackFileVerdict.Companion"/>)——
        /// 側車檔 <c>sdoinfo.dat</c>(與改名前的 <c>sdo.header</c>)。兩件事都必須成立:
        ///
        /// • <b>要傳</b> —— 它是「這首歌用哪一支編舞、哪一張 CD 圖、offset 校到多少」的**唯一指標**。
        ///   不傳的話,客製 <c>.dps</c>/<c>.mot</c>/CD 圖就算檔案到了收端也沒人去指它:
        ///   收端照樣生一份自動編舞、照樣自己合成一張碟,作者編的那一支永遠不會被跳到。
        ///
        /// • <b>不算進 packId</b> —— 這個檔是**執行期會被改寫的**:第一次選到這首歌會寫進
        ///   <c>#CDIMAGE</c>,第一次玩會寫進 <c>#DPS</c>/<c>#DPSVER</c>。算進身分的話,同一份歌會因為
        ///   「玩過沒」而變成兩個 packId —— 送端玩過一次自己的 id 就變了(server 上那包再也對不上),
        ///   收端下載完玩一次也一樣(「我明明有這首歌」被判定成沒有,每次回房重載一次)。
        ///   這與 <see cref="ModelPackFilter.GeneratedFileName"/>(physics.ini)是同一個道理。
        ///
        /// 🔴 它同時也是 <see cref="IsGenerated"/> 的成員(那份是給 <c>ExternalScanCache</c> 的:
        /// 「執行期寫出來的東西不該讓自己的掃描快取失效」)。兩個問題、兩個答案,不要合併 ——
        /// 「不算來源檔」不等於「不要傳給別人」。
        /// </summary>
        private static readonly string[] NotPartOfTheSong = { SongSidecar.FileName, SongSidecar.LegacyFileName };

        /// <summary>
        /// 目錄深度上限:歌曲資料夾本身(0)+ 一層子夾(1)。
        /// osu 的資料夾偶爾會有一層 <c>sb/</c> 放分鏡素材,再深就不正常了。
        /// </summary>
        public const int MaxDepth = 1;

        /// <summary>
        /// 譜面編輯器的自動存檔資料夾(StepMania / ArrowVortex 存進 <c>&lt;歌&gt;/FileBackup/</c>,
        /// 每存一次一個帶時間戳的 .sm)。
        ///
        /// 🔴 <b>整個資料夾不傳,而且不算進 packId。</b> 它是編輯歷史,不是這首歌 ——
        ///
        /// • <b>算進 packId 的話,每存一次譜這首歌就換一個身分</b> → 房裡每個人都得重下一遍一份
        ///   只多了幾個備份檔的相同歌曲,而畫面上完全看不出為什麼。
        /// • <b>傳過去也只是浪費</b>:實測一首編過幾輪的歌就躺著 24 個 .sm(每個約 11 KB),
        ///   而且備份多的歌還會往 <see cref="NetPackLimits.MaxPackFiles"/>(600)那條上限逼近。
        /// • 收端拿到它更糟:那些 .sm 沒有旁邊的音檔,<c>ExternalSongScanner</c> 也已經刻意不把它
        ///   當成一首歌(見那邊的 <c>Collect</c>),於是它們只會佔磁碟。
        /// </summary>
        public const string EditorBackupDir = "filebackup";

        /// <summary>這個相對路徑是躺在編輯器自動存檔資料夾裡的嗎(<see cref="EditorBackupDir"/>)?</summary>
        public static bool IsEditorBackup(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return false;
            int cut = -1;
            for (int i = 0; i < relPath.Length; i++)
                if (relPath[i] == '/' || relPath[i] == '\\') { cut = i; break; }
            if (cut <= 0) return false;                       // 沒有目錄那一段 → 它是根目錄下的檔案
            return string.Equals(relPath.Substring(0, cut), EditorBackupDir, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>只看路徑與副檔名的判定(不需要知道檔案大小)。</summary>
        public static PackFileVerdict ClassifyPath(string relPath)
        {
            if (!SafeRelPath.IsSafe(relPath)) return PackFileVerdict.UnsafePath;

            if (Depth(relPath) > MaxDepth) return PackFileVerdict.TooDeep;

            // 編輯器的自動存檔整個資料夾不要 —— 它是編輯歷史,不是這首歌(見 EditorBackupDir)。
            if (IsEditorBackup(relPath)) return PackFileVerdict.Generated;

            string name = FileNameOf(relPath);
            // 側車檔先判 —— 它也在 IsGenerated 裡(那份是掃描快取的問題),但它要傳,只是不進 packId。
            for (int i = 0; i < NotPartOfTheSong.Length; i++)
                if (string.Equals(name, NotPartOfTheSong[i], StringComparison.OrdinalIgnoreCase))
                    return PackFileVerdict.Companion;
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
            // companion 也要過大小這一關 —— 它會跟著傳,所以「宣稱 200 MB 的 sdoinfo.dat」不能免檢。
            if (v != PackFileVerdict.Include && v != PackFileVerdict.Companion) return v;

            if (lengthBytes < 0) return PackFileVerdict.TooBig;   // 讀不到大小 → 當成不能傳
            if (lengthBytes > NetPackLimits.MaxSingleFileBytes) return PackFileVerdict.TooBig;

            string ext = ExtensionOf(FileNameOf(relPath));
            if (Has(Images, ext) && lengthBytes > NetPackLimits.MaxImageFileBytes) return PackFileVerdict.TooBig;

            return v;
        }

        /// <summary>
        /// 這個檔可以跨網路傳嗎。companion(<c>sdoinfo.dat</c>)**算可以** —— 它跟著歌走,
        /// 只是不參與 packId(見 <see cref="NotPartOfTheSong"/>)。三端(上傳掃描、server 收檔驗證、
        /// 下載端收檔驗證)都問這個函式,所以這裡與 <see cref="SongPackScan.Enumerate"/> /
        /// <see cref="SongPackId.ScanFolder"/> 的判斷必須一致,否則送端傳了、收端拒收,
        /// 而錯誤訊息會說「這個檔不該傳」。
        /// </summary>
        public static bool IsTransferable(string relPath, long lengthBytes)
        {
            var v = Classify(relPath, lengthBytes);
            return v == PackFileVerdict.Include || v == PackFileVerdict.Companion;
        }

        /// <summary>
        /// 是遊戲自己在歌曲資料夾裡生出來的東西嗎?
        /// 側車檔(<c>sdoinfo.dat</c> 與改名前的 <c>sdo.header</c>)、合成的 CD 碟圖
        /// (<c>cd.png</c> / <c>cd_&lt;slug&gt;_&lt;hash&gt;.png</c>)、生成的舞蹈
        /// (<c>dance.dps</c> / <c>dance_&lt;…&gt;.dps</c>)。
        ///
        /// 🔴 這份判定是 <c>ExternalScanCache</c>(快取失效判斷)與本檔(傳輸過濾)**共用的唯一真相**。
        /// 兩邊各寫一份的話,某天新增一種生成物只改了一邊 —— 快取那邊會變成「播完一首歌就讓自己的
        /// 快取失效」,傳輸這邊會變成「把收端自己會重生的東西傳過去」,而且都不會有測試抓到。
        ///
        /// 🔴 <b>「是生成物」≠「不要傳」。</b> 側車檔兩者都算生成物,但它是 <see cref="NotPartOfTheSong"/>
        /// 的成員 → <see cref="ClassifyPath"/> 先把它判成 <see cref="PackFileVerdict.Companion"/>(傳,
        /// 但不進 packId),只有碟圖與生成的舞真的被排除。
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
