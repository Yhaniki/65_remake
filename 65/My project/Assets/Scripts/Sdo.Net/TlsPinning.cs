namespace Sdo.Net
{
    /// <summary>
    /// 憑證指紋的比對規則(TLS 釘選 / certificate pinning)。**純字串處理,零 crypto 型別** ——
    /// 所以 server 與 Unity client 共用同一份,而且測得起來。
    ///
    /// 為什麼需要釘選:公網化最現實的情境是「朋友開一台,用自己簽的憑證」。自簽憑證沒有 CA 背書,
    /// 一般的驗證流程一定會失敗,於是最容易犯的錯就是在驗證 callback 裡直接 <c>return true</c> ——
    /// 那樣 TLS 只剩裝飾:任何人都能在中間插一台假 server,加密照樣成立,只是加密給攻擊者。
    ///
    /// 正確做法:server 開機時印出自己憑證的 SHA-256 指紋,玩家把那串貼進 <c>config.ini</c>。
    /// 之後 client 只接受**指紋一模一樣**的憑證,鏈結錯誤(自簽、沒有 CA)就可以忽略 ——
    /// 那正是釘選的意義。沒填指紋時就走一般的 CA 驗證(有正式憑證的人適用)。
    ///
    /// 🔴 <see cref="Matches"/> 在任何一邊為空時一律回 false。「沒設定指紋」絕對不能等於「什麼都符合」,
    /// 那是同一類錯誤的第二個版本(而且更難發現:它只在使用者沒填的時候才變成漏洞)。
    /// </summary>
    public static class TlsPinning
    {
        /// <summary>SHA-256 十六進位字串的長度。</summary>
        public const int HexLength = 64;

        /// <summary>
        /// 正規化一個指紋:去掉 <c>:</c>、<c>-</c>、空白,轉小寫。
        /// 不是 64 個十六進位字元 → 回空字串(= 沒有可用的指紋)。
        ///
        /// 容忍分隔符是刻意的:openssl 印出來的是 <c>AA:BB:CC…</c>,Windows 憑證管理員印的是
        /// 空白分隔,而使用者是用複製貼上的 —— 為了一個冒號就說「指紋錯誤」只會讓人以為功能壞了。
        /// </summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(HexLength);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ':' || c == '-' || c == ' ' || c == '\t' || c == '\r' || c == '\n') continue;
                if (c >= '0' && c <= '9') { sb.Append(c); continue; }
                if (c >= 'a' && c <= 'f') { sb.Append(c); continue; }
                if (c >= 'A' && c <= 'F') { sb.Append((char)(c + 32)); continue; }
                return "";   // 出現十六進位以外的字元 → 整串不採用(不要猜使用者的意思)
            }
            return sb.Length == HexLength ? sb.ToString() : "";
        }

        /// <summary>這個設定值是一個可用的指紋嗎(= 使用者有要求釘選)?</summary>
        public static bool Configured(string pin) => Normalize(pin).Length == HexLength;

        /// <summary>
        /// 釘選的指紋與實際收到的憑證指紋相符嗎?
        /// **任何一邊空/格式不對 → false**(見類別註解的紅字)。
        /// </summary>
        public static bool Matches(string pinned, string actual)
        {
            string a = Normalize(pinned);
            string b = Normalize(actual);
            if (a.Length != HexLength || b.Length != HexLength) return false;
            return string.Equals(a, b, System.StringComparison.Ordinal);
        }

        /// <summary>把位元組指紋印成小寫十六進位(server 開機時印給使用者複製的那一串)。</summary>
        public static string ToHex(byte[] hash)
        {
            if (hash == null || hash.Length == 0) return "";
            var sb = new System.Text.StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
