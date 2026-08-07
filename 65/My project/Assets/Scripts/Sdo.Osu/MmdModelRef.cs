using System;
using System.Globalization;

namespace Sdo.Osu
{
    /// <summary>
    /// 「別人身上穿的是哪一個模型、多大」的完整答案 ＝ <b>packId ＋ 這一包裡的哪一個 .pmx ＋ 大小倍率</b>。
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
    /// 🔴 <b>第三段是大小倍率(<c>mmdScale</c>)。</b>模型自動對齊舞者身高之後,使用者還會再乘一個倍率把
    /// 「這個模型看起來偏大/偏小」修掉 —— 那個修正**必須跟著人走**,否則同一個人在自己畫面上與在別人
    /// 畫面上是兩個大小(而且頭上名字牌的高度是照身高算的,見 <c>MmdHeadroom</c>,於是別人那邊的名字
    /// 還會插進他的頭裡)。倍率剛好是 1(絕大多數人)時**不寫進字串**,舊 client 的兩段格式原封不動。
    ///
    /// 純字串邏輯,零 IO、零 UnityEngine —— server 與遊戲編同一份。
    /// </summary>
    public static class MmdModelRef
    {
        /// <summary>大小倍率的合理範圍與預設值。與 config.ini <c>mmdScale</c> 的夾值同一組
        /// (<c>RoomConfig.Clamp</c>);別人送來的數字一律夾在這裡面才往下傳。</summary>
        public const float MinScale = 0.3f, MaxScale = 3f, DefaultScale = 1f;
        /// <summary>packId 與包內路徑的分隔符。
        ///
        /// 挑 <c>'|'</c> 的理由:<see cref="SafeRelPath.IsSafe"/> 明確擋掉路徑裡的 <c>'|'</c>
        /// (Windows 不允許的檔名字元),而 packId 是 <c>sha256:</c> + 32 個 hex —— 兩邊都不可能
        /// 含有它,所以切開來永遠不會有歧義。</summary>
        public const char Sep = '|';

        /// <summary>packId ＋ 包內路徑 → 一個字串(大小用預設的 1×)。</summary>
        public static string Join(string packId, string file) => Join(packId, file, DefaultScale);

        /// <summary>packId ＋ 包內路徑 ＋ 大小倍率 → 一個字串。沒有 packId 一律回空字串(＝這個人沒穿模型);
        /// 沒有指定檔案就只有 packId(＝舊 client / 一包只有一個模型,收端自己挑);倍率 1× 不寫出來
        /// (＝與舊格式逐字相同,不會讓「他的外觀變了嗎」誤判成變了)。</summary>
        public static string Join(string packId, string file, float scale)
        {
            if (string.IsNullOrEmpty(packId)) return "";
            string f = SafeRelPath.Normalize(file ?? "");
            float s = ClampScale(scale);
            if (IsDefaultScale(s)) return f.Length == 0 ? packId : packId + Sep + f;
            // 倍率在第三段 → 檔名那一段就算是空的也要留著,不然 '|' 的位置會對不上。
            return packId + Sep + f + Sep + s.ToString("0.###", CultureInfo.InvariantCulture);
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
            if (i < 0) return "";
            int j = modelRef.IndexOf(Sep, i + 1);
            return j < 0 ? modelRef.Substring(i + 1) : modelRef.Substring(i + 1, j - i - 1);
        }

        /// <summary>取出大小倍率。沒帶 / 帶了看不懂的東西 → <see cref="DefaultScale"/>(＝只做自動對齊身高),
        /// 而不是拒絕整個模型:大小錯了頂多人偏大偏小,不值得讓他整具身體消失。</summary>
        public static float ScaleOf(string modelRef)
        {
            if (string.IsNullOrEmpty(modelRef)) return DefaultScale;
            int i = modelRef.IndexOf(Sep);
            if (i < 0) return DefaultScale;
            int j = modelRef.IndexOf(Sep, i + 1);
            if (j < 0) return DefaultScale;
            float s;
            if (!float.TryParse(modelRef.Substring(j + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out s))
                return DefaultScale;
            return ClampScale(s);
        }

        /// <summary>把別人送來的倍率夾成能用的值。0 / 負數 / NaN ＝「他沒說」→ 預設 1×
        /// (夾成 <see cref="MinScale"/> 的話,一個亂填的 0 會讓他在別人畫面上縮成一個小點)。</summary>
        public static float ClampScale(float scale)
        {
            if (float.IsNaN(scale) || !(scale > 0f)) return DefaultScale;
            return scale < MinScale ? MinScale : (scale > MaxScale ? MaxScale : scale);
        }

        /// <summary>這個倍率等於「不調整」嗎(浮點比較留一點容差:值會經過字串來回一趟)。</summary>
        public static bool IsDefaultScale(float scale) => Math.Abs(scale - DefaultScale) < 1e-3f;

        /// <summary>兩個倍率視為同一個嗎(＝外觀沒變 / 不必重建身體)。</summary>
        public static bool SameScale(float a, float b) => Math.Abs(a - b) < 1e-3f;

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
