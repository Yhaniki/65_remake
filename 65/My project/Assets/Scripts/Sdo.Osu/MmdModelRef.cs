using System;

namespace Sdo.Osu
{
    /// <summary>
    /// 「別人身上穿的是哪一個模型」的完整答案 ＝ <b>packId ＋ 這一包裡的哪一個 .pmx</b>。
    ///
    /// 🔴 <b>為什麼 packId 一個人不夠。</b>packId 是**整個資料夾**的內容指紋(見 <see cref="ModelPackId"/>),
    /// 而一個資料夾裡可以有好幾個 .pmx —— 角色本體、他的武器、他的影子、同一個作者的三個角色。
    /// 本機自己顯示時走的是「使用者在設定面板選的那一個」(<c>MmdModelCatalog.Entry.PmxPath</c>),
    /// 但傳到對面的只有一個 packId,於是收端只能從資料夾反推 → 反推永遠拿到同一個。
    ///
    /// 實測踩過:<c>NerissaRavencroft/</c> 裡有 <c>NerissaRavencroft.pmx</c>(10.8 MB 的角色)、
    /// <c>Shadow.pmx</c>、<c>nerissa_spear.pmx</c>(一把槍)。傳出去的檔名在 manifest 裡是**小寫**的
    /// (<see cref="SafeRelPath.Normalize"/>),而小寫之後 <c>nerissa_spear</c> 的 <c>'_'</c>(0x5F)
    /// 排在 <c>nerissar…</c> 的 <c>'r'</c>(0x72)前面 —— 於是收端照字典序挑,挑到那把槍:
    /// 房間裡站著一個穿模型的人,別人畫面上是一支黑色的槍在跳舞。
    ///
    /// 所以外觀(<c>NetAvatarLook</c>)除了 packId 還要帶這個相對路徑。兩個值在**專案內部**
    /// 常常要一起被搬過好幾層(房間快照 → 遠端角色 → 頭貼),為了不讓任何一層「只搬了 packId」,
    /// 中間那幾層一律搬 <see cref="Join"/> 出來的**單一字串**:漏搬會整個模型不見(看得出來),
    /// 而不是安靜地換成另一個模型(看不出來)。
    ///
    /// 純字串邏輯,零 IO、零 UnityEngine —— server 與遊戲編同一份。
    /// </summary>
    public static class MmdModelRef
    {
        /// <summary>packId 與包內路徑的分隔符。
        ///
        /// 挑 <c>'|'</c> 的理由:<see cref="SafeRelPath.IsSafe"/> 明確擋掉路徑裡的 <c>'|'</c>
        /// (Windows 不允許的檔名字元),而 packId 是 <c>sha256:</c> + 32 個 hex —— 兩邊都不可能
        /// 含有它,所以切開來永遠不會有歧義。</summary>
        public const char Sep = '|';

        /// <summary>packId ＋ 包內路徑 → 一個字串。沒有 packId 一律回空字串(＝這個人沒穿模型);
        /// 沒有指定檔案就只有 packId(＝舊 client / 一包只有一個模型,收端自己挑)。</summary>
        public static string Join(string packId, string file)
        {
            if (string.IsNullOrEmpty(packId)) return "";
            string f = SafeRelPath.Normalize(file ?? "");
            return f.Length == 0 ? packId : packId + Sep + f;
        }

        /// <summary>取出 packId(＝ blob 傳輸與「本機有沒有這一份」用的身分)。</summary>
        public static string PackOf(string modelRef)
        {
            if (string.IsNullOrEmpty(modelRef)) return "";
            int i = modelRef.IndexOf(Sep);
            return i < 0 ? modelRef : modelRef.Substring(0, i);
        }

        /// <summary>取出包內路徑(沒帶就是空字串 → 收端自己從資料夾挑)。</summary>
        public static string FileOf(string modelRef)
        {
            if (string.IsNullOrEmpty(modelRef)) return "";
            int i = modelRef.IndexOf(Sep);
            return i < 0 ? "" : modelRef.Substring(i + 1);
        }

        /// <summary>
        /// 這個包內路徑可以收嗎?
        ///
        /// 它是**別人送來的字串**,而收端會拿它去 <c>Path.Combine</c> 一個真實資料夾 ——
        /// 所以走與傳檔清單同一道關(<see cref="SafeRelPath.IsSafe"/>,擋 <c>..</c>、絕對路徑、
        /// drive 前綴、NTFS data stream…),另外再要求它真的是一個 <c>.pmx</c>。
        /// 不合格 → 當成「他沒指定」,收端退回自己挑,而不是拒絕整個模型。
        /// </summary>
        public static bool IsSafeFile(string file)
        {
            if (string.IsNullOrEmpty(file)) return false;
            if (!file.EndsWith(".pmx", StringComparison.OrdinalIgnoreCase)) return false;
            return SafeRelPath.IsSafe(file);
        }

        /// <summary>
        /// <paramref name="fullPath"/> 相對於 <paramref name="root"/> 的路徑,正規化成 manifest 的形式
        /// (小寫、<c>/</c> 分隔)—— 傳出去的就是這個字串,而收端磁碟上的檔名也正是這個形式
        /// (下載時照 manifest 落地)。不在 <paramref name="root"/> 底下就回空字串。
        ///
        /// 兩邊的分隔符可能不同(專案內部到處是 Windows 路徑),比對時一律先正規化。
        /// </summary>
        public static string RelPathUnder(string root, string fullPath)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(fullPath)) return "";
            string r = SafeRelPath.Normalize(root).TrimEnd('/');
            string f = SafeRelPath.Normalize(fullPath);
            if (r.Length == 0) return "";
            if (f.Length <= r.Length + 1) return "";
            if (!f.StartsWith(r, StringComparison.Ordinal)) return "";
            if (f[r.Length] != '/') return "";
            return f.Substring(r.Length + 1);
        }
    }
}
