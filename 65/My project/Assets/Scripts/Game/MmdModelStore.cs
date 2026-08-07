using System.Collections.Generic;
using System.IO;
using Sdo.Osu;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 「這個 <c>packId</c> 的模型在本機的哪個資料夾?」以及反過來「我選的這個模型的 packId 是什麼?」
    ///
    /// 連線功能需要這兩個方向:
    ///   • **往外**:我要在 <c>setLook</c> 裡宣告我身上穿的是哪個模型 → 要自己選的模型的 packId。
    ///   • **往內**:別人的外觀帶著一個 packId → 我要知道本機有沒有、在哪(有就直接顯示,沒有才去下載)。
    ///
    /// packId 是**全檔內容雜湊**(見 <see cref="ModelPackId"/>),一份 10 MB 的模型算一次約 30 ms。
    /// 所以算過就記著:本機安裝的模型不會在遊戲執行中被換掉,同一個資料夾重算永遠是同一個答案。
    /// (真的手動換了檔案 → 重開遊戲。這比每次進房間都重掃一次整個模型資料夾划算得多。)
    ///
    /// 下載回來的模型放**專屬的一層** <c>&lt;DATA&gt;/ADDON/MODEL/.net/&lt;hex&gt;/</c>:
    ///   • 資料夾名就是 packId 的 hex,所以「有沒有這一份」是一次 <c>Directory.Exists</c>,不用掃描比對;
    ///   • 開頭的 <c>.</c> 讓 <see cref="MmdModelCatalog"/> 跳過它 —— 這些東西不該出現在設定面板的
    ///     模型清單裡(它們的名字是一串 hash,而且是別人的模型,不是使用者自己裝的)。
    /// </summary>
    public static class MmdModelStore
    {
        /// <summary>下載回來的模型放這一層(相對於模型根目錄)。開頭的點讓模型掃描器跳過它。</summary>
        public const string NetSubDir = ".net";

        // 資料夾 → packId。算一次就記著(見類別說明)。
        private static readonly Dictionary<string, string> _packIdByDir = new Dictionary<string, string>();

        /// <summary>這個模型資料夾的 packId(算過就用記著的)。算不出來(讀不到 / 不是合法模型包)回空字串。</summary>
        public static string PackIdOf(string modelDir)
        {
            if (string.IsNullOrEmpty(modelDir)) return "";
            string hit;
            if (_packIdByDir.TryGetValue(modelDir, out hit)) return hit;

            float t0 = Time.realtimeSinceStartup;
            string reason;
            string id = ModelPackId.ForFolder(modelDir, out reason);
            _packIdByDir[modelDir] = id;
            // 原因一定要寫出來。空的 packId = 對外宣告「我沒穿模型」,別人看到的是 SDO 穿搭 ——
            // 那個降級是對的,但沒有理由的話,使用者只會看到「我明明選了模型,別人卻看不到」。
            if (string.IsNullOrEmpty(id))
                SdoLog.Note("mmd", "[mmd] 算不出 packId,不能分享給別人(" + reason + "):" + modelDir);
            else
                SdoLog.Note("mmd", $"[mmd] packId {id} ← {Path.GetFileName(modelDir)} ({(Time.realtimeSinceStartup - t0) * 1000f:F0} ms)");
            return id;
        }

        /// <summary>算過的答案作廢(下載完成、或手動改了模型資料夾之後)。</summary>
        public static void Forget(string modelDir)
        {
            if (!string.IsNullOrEmpty(modelDir)) _packIdByDir.Remove(modelDir);
        }

        /// <summary>
        /// 這個 packId 的模型在本機的哪個資料夾?找不到回 null。
        ///
        /// 先看下載區(一次 Exists,不用掃描),再看使用者自己裝的模型 —— 後者要逐個算 packId,
        /// 但那個答案會被記住,而且**通常第一個就中**(自己穿的那個一定是自己裝的)。
        ///
        /// 🔴 回的是**整包的根**(下載區那一層 / 使用者那一包的 <see cref="MmdModelCatalog.Entry.Root"/>),
        /// 不是 .pmx 所在那一層:packId 算在整包上,對方宣告的包內路徑(<c>NetAvatarLook.MmdFile</c>)
        /// 也是相對於整包 —— 三者要對得起來就只能是同一個資料夾。.pmx 在包裡自己一層的包
        /// (ラプラス:<c>PMX/*.pmx</c>)由 <see cref="FindPmx"/> 往下找一層。
        /// </summary>
        public static string DirForPack(string packId, IEnumerable<MmdModelCatalog.Entry> installed)
        {
            if (!SongPackId.IsWellFormed(packId)) return null;

            string net = NetDirFor(packId);
            if (net != null && Directory.Exists(net) && HasPmx(net)) return net;

            if (installed != null)
                foreach (var e in installed)
                {
                    if (e == null || string.IsNullOrEmpty(e.Dir)) continue;
                    // 算在整包上(同 MmdAvatarSwap.ModelDir/LocalPackId)—— 這三處必須是同一個資料夾,
                    // 否則「我明明有這個模型」會因為算在不同的樹枝上而對不起來。
                    string packDir = e.Root ?? e.Dir;
                    if (string.Equals(PackIdOf(packDir), packId, System.StringComparison.Ordinal)) return packDir;
                }
            return null;
        }

        /// <summary>下載中的暫存資料夾尾綴。</summary>
        public const string PartSuffix = ".part";

        /// <summary>
        /// **下載中**的位元組先落在這裡,驗過 packId 才改名成 <see cref="NetDirFor"/>。
        ///
        /// 🔴 下載**絕對不能直接寫進最終位置**。<see cref="DirForPack"/> 判斷「這份模型在不在」只看
        /// 「資料夾存在 + 裡面有 .pmx」,而下載到一半的資料夾**這兩個條件都成立** ——
        /// 於是 <c>MmdAvatarSwap</c> 的補建迴圈(0.25 秒一輪)會在下載開始後的下一輪就跑去解析那個
        /// 還被寫入端鎖著的 .pmx,撞出 <c>Sharing violation</c>,然後把那隻角色標成 <c>Failed</c>
        /// 停在 SDO 身體上(實測踩過:下載開始 0.25 秒後就 read/parse fail)。
        /// 改名是原子的,所以「<c>.net/&lt;hex&gt;</c> 存在」永遠等於「這份模型是完整的」。
        /// </summary>
        public static string NetTempDirFor(string packId)
        {
            string dir = NetDirFor(packId);
            return dir == null ? null : dir + PartSuffix;
        }

        /// <summary>下載回來的這個 packId 該放哪(不保證存在)。算不出位置回 null。</summary>
        public static string NetDirFor(string packId)
        {
            if (!SongPackId.IsWellFormed(packId)) return null;
            string root = NetRoot();
            if (string.IsNullOrEmpty(root)) return null;
            return Path.Combine(root, packId.Substring(SongPackId.Prefix.Length));
        }

        /// <summary>
        /// 下載區的根目錄 <c>&lt;ADDON&gt;/MODEL/.net</c>。算不出位置(拿不到資料根)就回 null。
        ///
        /// 🔴 一定要跟**使用者自己丟模型的那一層**同一棵樹(<see cref="MmdAvatarSwap.ModelRoots"/> 的第一個,
        /// 見那裡的註解):ADDON 是 reserved 目錄、永遠不進 pak,而且 config.ini 的 <c>AddonFolder=</c>
        /// 可以把整棵指到別顆碟 —— 下載回來的模型跟著走才對。舊的 <c>&lt;DATA&gt;/MODEL</c> 是早期打包腳本
        /// 放模型的位置,寫進去等於把別人的模型倒進遊戲自己的資料樹裡。
        /// </summary>
        public static string NetRoot()
        {
            string addonModel = null;
            try { addonModel = SdoExtracted.AddonModelDir; } catch { }
            if (string.IsNullOrEmpty(addonModel)) return null;
            return Path.Combine(addonModel, NetSubDir);
        }

        /// <summary>這個資料夾裡有 .pmx 嗎(含一層子資料夾)?(下載到一半 / 被刪掉一半的資料夾不算數)</summary>
        public static bool HasPmx(string dir) => FindPmx(dir) != null;

        /// <summary>
        /// 這一包裡要載哪一個 .pmx —— <b>對方說了算</b>。
        ///
        /// <paramref name="relFile"/> ＝ 他外觀宣告的包內路徑(<c>NetAvatarLook.MmdFile</c>,
        /// 相對於包根、小寫、<c>/</c> 分隔)。一個資料夾可以有好幾個 .pmx(角色 ＋ 武器 ＋ 影子 ＋
        /// 同作者的另外兩個角色),而 packId 是**整包**的指紋 —— 少了這個路徑就只能用猜的,
        /// 而猜錯的畫面是「他變成同一包裡的另一個東西」(實測:一支黑色的槍在跳舞)。
        ///
        /// 他沒說(舊 client)、或說的那個檔不在(他手動改過檔名)→ 退回 <see cref="FindPmx"/> 猜一個,
        /// 那仍然比整個不顯示好:對方本來就穿著這一包裡的某個模型。
        ///
        /// 路徑是逐檔比對出來的,不是 <c>Path.Combine</c> 拼出來的:傳輸清單一律小寫,而使用者**自己裝的**
        /// 那一份是原始大小寫(同一份模型兩台都有 = 零上傳那條路)。Windows 開得起來,但回傳的路徑會是
        /// 拼出來的那個大小寫,而它會被當成解析快取的 key —— 回磁碟上真正的那一個才不會同一份模型解析兩次。
        /// </summary>
        public static string PmxForPack(string packDir, string relFile)
        {
            if (string.IsNullOrEmpty(packDir)) return null;
            if (Sdo.Osu.MmdModelRef.IsSafeFile(relFile))
            {
                string hit = MatchRelPath(packDir, relFile);
                if (hit != null) return hit;
                SdoLog.Note("mmd", "[mmd] 對方說他穿的是 '" + relFile + "',但這一包裡沒有這個檔 —— 改用猜的:" + packDir);
            }
            return FindPmx(packDir);
        }

        // 逐檔比「正規化後的相對路徑」(頂層 + 一層子資料夾,與 ModelPackFilter.MaxDepth 一致)。
        // 兩邊都經過 SafeRelPath.Normalize(小寫 + '/'),所以大小寫與分隔符的差異在這裡自然消失。
        private static string MatchRelPath(string packDir, string relFile)
        {
            try
            {
                if (!Directory.Exists(packDir)) return null;
                string hit = MatchIn(packDir, packDir, relFile);
                if (hit != null) return hit;
                foreach (var sub in Directory.GetDirectories(packDir))
                {
                    hit = MatchIn(packDir, sub, relFile);
                    if (hit != null) return hit;
                }
            }
            catch { }
            return null;
        }

        private static string MatchIn(string root, string dir, string relFile)
        {
            foreach (var f in Directory.GetFiles(dir))
                if (string.Equals(Sdo.Osu.MmdModelRef.RelPathUnder(root, f), relFile, System.StringComparison.Ordinal))
                    return f;
            return null;
        }

        /// <summary>
        /// 這一包裡**猜**要載哪一個 .pmx —— 頂層優先,沒有才找子資料夾。找不到回 null。
        /// (對方有講他穿哪一個時走 <see cref="PmxForPack"/>,那個以他講的為準;這裡只服務「他沒講」。)
        ///
        /// 🔴 <b>一定要看子資料夾。</b>分享出去的是整包(見 <c>MmdAvatarSwap.ModelDir</c>),而很多模型包
        /// 把 .pmx 放在自己的一層裡(ラプラス:<c>PMX/*.pmx</c> + <c>sourceimages/*.tga</c>)——
        /// 只看頂層的話,下載回來的那一包會被判定成「裡面沒有 .pmx」,於是
        /// <see cref="DirForPack"/> 永遠回 null → 明明已經裝好了還是每次進房都重下載一次。
        /// (深度只到一層,與 <see cref="ModelPackFilter.MaxDepth"/> 一致 —— 傳得進來的就掃得到。)
        ///
        /// 猜法看檔案大小(見 <see cref="MmdModelCatalog.PickPmx(IEnumerable{string}, System.Func{string, long})"/>):
        /// 一包裡好幾個 .pmx 時,角色本體幾乎必然是最大的那一個,配件(武器/影子)差一個數量級。
        /// </summary>
        public static string FindPmx(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return null;
                string top = MmdModelCatalog.PickPmx(Directory.GetFiles(dir), SizeOf);
                if (top != null) return top;
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    string hit = MmdModelCatalog.PickPmx(Directory.GetFiles(sub), SizeOf);
                    if (hit != null) return hit;
                }
            }
            catch { }
            return null;
        }

        // 檔案大小(問不出來 = -1,PickPmx 會退回字典序)。
        private static long SizeOf(string path)
        {
            try { return new FileInfo(path).Length; } catch { return -1; }
        }
    }
}
