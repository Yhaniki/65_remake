using System;
using System.Text;

namespace Sdo.Osu
{
    /// <summary>
    /// 相對路徑的安全驗證與正規化。給「歌曲檔案跨網路傳輸」用:上傳端送來的每個路徑，
    /// **收端會直接拿它在自己的磁碟上建檔**，所以這是防 path traversal 的關鍵一關。
    ///
    /// 🔴 **client 與 server 兩邊都要跑，而且 server 絕不信任上傳者送來的清單。**
    /// 一個沒擋住的 <c>..\..\..\Windows\System32\</c> 就是任意檔案覆寫。
    ///
    /// 純字串邏輯，零 IO、零 UnityEngine —— server(net8.0)與遊戲(Unity Mono)編同一份。
    /// </summary>
    public static class SafeRelPath
    {
        /// <summary>
        /// 路徑總長度上限。Windows 的傳統 MAX_PATH 是 260，而收端的 base
        /// (<c>&lt;DATA&gt;\ADDON\SONG\connect\&lt;歌名&gt;\</c>)本身就吃掉不少，
        /// 所以相對路徑這邊留保守一點。
        /// </summary>
        public const int MaxLength = 180;

        /// <summary>單一路徑片段(檔名/資料夾名)的長度上限。</summary>
        public const int MaxSegmentLength = 100;

        /// <summary>
        /// Windows 的裝置保留名。**注意 <c>CON.txt</c> 也一樣被保留** —— 判斷要看第一個
        /// 句點之前的部分，不是整個檔名。在這些名字上開檔會變成操作裝置而不是檔案。
        /// </summary>
        private static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>
        /// 這個相對路徑可以安全地拿去建檔嗎?
        /// 拒絕:空白、太長、絕對路徑、drive 前綴、UNC、<c>..</c> 片段、空片段、控制字元、
        /// <c>:</c>(NTFS alternate data stream)、通配字元、Windows 保留名、以句點或空白結尾的片段。
        /// </summary>
        public static bool IsSafe(string relPath)
        {
            string reason;
            return IsSafe(relPath, out reason);
        }

        /// <summary>帶原因的版本(給錯誤訊息與測試用 —— 測試失敗時能直接看出是哪一條規則擋的)。</summary>
        public static bool IsSafe(string relPath, out string reason)
        {
            reason = null;

            if (string.IsNullOrEmpty(relPath)) { reason = "空路徑"; return false; }
            if (relPath.Length > MaxLength) { reason = "路徑過長(" + relPath.Length + " > " + MaxLength + ")"; return false; }

            // 兩種分隔符都當成分隔符處理 —— 送過來的可能是 Windows 或 Unix 風格。
            // 先看有沒有絕對路徑的跡象。
            if (relPath[0] == '/' || relPath[0] == '\\') { reason = "絕對路徑"; return false; }

            // drive 前綴 "C:" / UNC "\\\\server"。冒號在下面會被逐字元擋掉，
            // 但這裡先明確判斷好給出精確的原因。
            if (relPath.Length >= 2 && relPath[1] == ':') { reason = "含 drive 前綴"; return false; }

            var parts = relPath.Split('/', '\\');
            for (int i = 0; i < parts.Length; i++)
            {
                var seg = parts[i];

                if (seg.Length == 0) { reason = "空的路徑片段"; return false; }
                if (seg.Length > MaxSegmentLength) { reason = "片段過長:" + seg; return false; }
                if (seg == "." || seg == "..") { reason = "含 " + seg + " 片段"; return false; }

                // Windows 會把結尾的句點與空白默默 trim 掉 —— 也就是 "evil." 與 "evil" 會開到同一個檔。
                // 放行的話，同一份 manifest 在不同 OS 上會產生不同的檔案集合。
                char last = seg[seg.Length - 1];
                if (last == '.' || last == ' ') { reason = "片段以句點或空白結尾:" + seg; return false; }
                if (seg[0] == ' ') { reason = "片段以空白開頭:" + seg; return false; }

                for (int c = 0; c < seg.Length; c++)
                {
                    char ch = seg[c];
                    if (ch < 0x20 || ch == 0x7F) { reason = "含控制字元"; return false; }
                    // ':' = NTFS alternate data stream("a.ogg:hidden" 會寫進隱藏資料流)
                    // '*' '?' = 通配字元;'<' '>' '|' '"' = Windows 不允許的檔名字元
                    if (ch == ':' || ch == '*' || ch == '?' || ch == '<' || ch == '>' || ch == '|' || ch == '"')
                    {
                        reason = "含不合法字元 '" + ch + "'";
                        return false;
                    }
                }

                if (IsReservedName(seg)) { reason = "Windows 保留名:" + seg; return false; }
            }

            return true;
        }

        /// <summary>
        /// 是 Windows 裝置保留名嗎?比對第一個句點之前的部分(<c>CON.txt</c> 也算)，大小寫不敏感。
        /// </summary>
        public static bool IsReservedName(string segment)
        {
            if (string.IsNullOrEmpty(segment)) return false;
            int dot = segment.IndexOf('.');
            string stem = dot >= 0 ? segment.Substring(0, dot) : segment;
            for (int i = 0; i < ReservedNames.Length; i++)
            {
                if (string.Equals(stem, ReservedNames[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// 正規化成 manifest / packId 用的形式:一律 <c>/</c> 分隔、**小寫**。
        ///
        /// 為什麼要小寫:Windows 的檔案系統大小寫不敏感，同一個資料夾在兩台機器上可能是
        /// <c>BGM.ogg</c> 與 <c>bgm.ogg</c>。不正規化的話 packId 會不一樣 → 明明是同一首歌卻
        /// 判定成缺歌並重新傳一次。一律用 <c>ToLowerInvariant</c> 而不是 <c>ToLower()</c> ——
        /// 土耳其語 locale 的 'I' 會小寫成沒有點的 'ı',那會讓同一份檔案在不同語系的機器上
        /// 算出不同的 packId。
        /// </summary>
        public static string Normalize(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return string.Empty;
            var sb = new StringBuilder(relPath.Length);
            for (int i = 0; i < relPath.Length; i++)
            {
                char c = relPath[i];
                sb.Append(c == '\\' ? '/' : char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}
