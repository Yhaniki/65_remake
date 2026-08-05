using System;

namespace Sdo.Osu
{
    /// <summary>
    /// 決定一個 MMD 模型資料夾的哪些檔案可以跨網路傳輸。與 <see cref="SongPackFilter"/> 同一個角色、同一套
    /// 判定值(<see cref="PackFileVerdict"/>)、同一條鐵則:**client 與 server 編同一份**,client 用它決定
    /// 上傳什麼、算什麼進 packId,server 用它重驗上傳者送來的清單。🔴 絕不信任上傳者。
    ///
    /// 為什麼不共用歌曲那份:兩邊的白名單幾乎不重疊。歌曲要 <c>.osu/.mp3/.ogg</c>,模型要
    /// <c>.pmx</c> 與一堆貼圖格式(<c>.spa/.sph</c> 是 MMD 的 sphere map,<c>.tga/.dds</c> 常見於模型包);
    /// 反過來把兩張白名單聯集起來就等於「歌曲資料夾可以挾帶 .pmx、模型資料夾可以挾帶 mp3」——
    /// 白名單制的意義就沒了。
    ///
    /// <b>模型包特別容易挾帶東西。</b>網路上流通的 MMD 模型包常常整包附 readme、規約書、PSD 原檔、
    /// 甚至作者自己的工具程式 —— 所以這裡除了照樣擋執行檔/壓縮檔/影片,還明確擋掉 <c>.psd</c> 這類
    /// 又大又完全用不到的原始檔。<c>.txt</c> 是**故意收的**:MMD 模型的使用規約幾乎都放在
    /// readme.txt,把模型傳給別人卻把規約留在自己這邊是不對的。
    ///
    /// 純字串與數字邏輯,零 IO、零 UnityEngine。
    /// </summary>
    public static class ModelPackFilter
    {
        /// <summary>
        /// 可以傳的副檔名。
        /// 模型本體 <c>.pmx</c>;貼圖 <c>.png .bmp .tga .jpg .jpeg .dds</c>;
        /// MMD 的 sphere map <c>.spa</c>(加算)<c>.sph</c>(乘算)—— 內容就是 bmp/png,只是副檔名不同;
        /// <c>.ini</c> 是模型作者可能附的設定檔(但 <see cref="GeneratedFileName"/> 那一個除外,見下);
        /// <c>.txt</c> 是使用規約/readme(見上面,故意收)。
        ///
        /// <c>.pmd</c>(舊版 MMD 格式)不在裡面 —— PmxLoader 讀不了它,傳過去對方也用不了。
        /// </summary>
        private static readonly string[] Allowed =
        {
            ".pmx",
            ".png", ".bmp", ".tga", ".jpg", ".jpeg", ".dds",
            ".spa", ".sph",
            ".ini", ".txt",
        };

        /// <summary>影片。與 <see cref="SongPackFilter"/> 同一份(模型包偶爾附作者的宣傳影片)。</summary>
        private static readonly string[] Videos =
        {
            ".mp4", ".avi", ".flv", ".wmv", ".mkv", ".mov", ".webm",
            ".mpg", ".mpeg", ".m4v", ".ts", ".rmvb", ".asf", ".ogv", ".3gp",
        };

        /// <summary>執行檔與腳本。安全考量:絕不把可執行的東西搬到別人的磁碟上。</summary>
        private static readonly string[] Executables =
        {
            ".exe", ".dll", ".bat", ".cmd", ".sh", ".ps1", ".msi",
            ".scr", ".lnk", ".com", ".vbs", ".js", ".jar", ".pif", ".cpl",
        };

        private static readonly string[] Archives =
        {
            ".zip", ".rar", ".7z", ".gz", ".tar", ".bz2", ".lzh",
        };

        /// <summary>貼圖(套較嚴的大小上限)。</summary>
        private static readonly string[] Images = { ".png", ".bmp", ".tga", ".jpg", ".jpeg", ".dds", ".spa", ".sph" };

        /// <summary>
        /// 目錄深度上限:模型資料夾本身(0)+ 一層子夾(1)。
        /// MMD 模型的慣例佈局就是這樣:<c>模型.pmx</c> 加上 <c>textures/</c>、<c>Toon/</c>、<c>Sph/</c>。
        /// (更深的那種「整合包裡包好幾個模型」不是一個 pack —— 掃描時每個含 .pmx 的資料夾各自是一個模型,
        /// 見 <c>MmdModelCatalog</c>。)
        /// </summary>
        public const int MaxDepth = 1;

        /// <summary>
        /// 單張貼圖的上限。比歌曲那邊(4 MB)寬:2048² 的 PNG 人物貼圖 1.5 MB 是常態,
        /// 4096² 的高解析模型會撞到 4 MB。8 MB 仍然擋得住「改名成 .png 的影片」。
        /// </summary>
        public const long MaxImageFileBytes = 8L * 1024 * 1024;

        /// <summary>
        /// 單一 .pmx 的上限。實測初音那份 8 MB;帶大量表情/多套服裝的模型可以到 20-30 MB。
        /// 沿用 <see cref="NetPackLimits.MaxSingleFileBytes"/>(32 MB)。
        /// </summary>
        public const long MaxPmxFileBytes = NetPackLimits.MaxSingleFileBytes;

        /// <summary>
        /// **遊戲自己寫進模型資料夾的**布料調校檔(<c>MmdClothProfile.FileName</c> 的值 —— 那邊在
        /// Sdo.Game,這裡是零依賴的底層,不能引用,所以兩邊各存一份、改一邊要記得改另一邊)。
        ///
        /// 🔴 <b>它絕對不能算進 packId。</b>packId 是模型在網路上的身分,而這個檔是**本機調校的產物**:
        /// 存過布料的人與沒存過的人,手上明明是同一份模型,算出來的 id 卻不一樣 ——
        /// 於是「我明明有這個模型」卻被判定成沒有,白白再下載一份幾十 MB 的重複副本,
        /// 而且畫面上完全看不出為什麼(實測踩過:差別就只有這一個 6 KB 的檔)。
        ///
        /// 傳給別人也不對:布料參數是從**對方的** mmdScale / 重力 / 碰撞半徑推出來的,
        /// 搬到別台機器上未必是作者調好的那個手感。收端沒有它就從 .pmx 自己轉換一份,
        /// 這正是 <see cref="PackFileVerdict.Generated"/> 的語意。
        /// </summary>
        public const string GeneratedFileName = "physics.ini";

        /// <summary>只看路徑與副檔名的判定(不需要知道檔案大小)。</summary>
        public static PackFileVerdict ClassifyPath(string relPath)
        {
            if (!SafeRelPath.IsSafe(relPath)) return PackFileVerdict.UnsafePath;
            if (SongPackFilter.Depth(relPath) > MaxDepth) return PackFileVerdict.TooDeep;

            string name = SongPackFilter.FileNameOf(relPath);
            if (string.Equals(name, GeneratedFileName, StringComparison.OrdinalIgnoreCase))
                return PackFileVerdict.Generated;
            string ext = SongPackFilter.ExtensionOf(name);
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

            string ext = SongPackFilter.ExtensionOf(SongPackFilter.FileNameOf(relPath));
            if (Has(Images, ext) && lengthBytes > MaxImageFileBytes) return PackFileVerdict.TooBig;

            return PackFileVerdict.Include;
        }

        public static bool IsTransferable(string relPath, long lengthBytes)
            => Classify(relPath, lengthBytes) == PackFileVerdict.Include;

        /// <summary>這個相對路徑是模型本體嗎(＝ .pmx)。一個 pack 至少要有一個,否則它不是模型。</summary>
        public static bool IsModelFile(string relPath)
            => string.Equals(SongPackFilter.ExtensionOf(SongPackFilter.FileNameOf(relPath)), ".pmx", StringComparison.Ordinal);

        private static bool Has(string[] set, string ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            for (int i = 0; i < set.Length; i++)
                if (string.Equals(set[i], ext, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
