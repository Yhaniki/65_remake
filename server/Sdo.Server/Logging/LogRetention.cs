using System;
using System.Collections.Generic;
using System.Globalization;

namespace Sdo.Server.Logging
{
    /// <summary>一個 log 檔在磁碟上的樣子。清理決策只需要這兩件事。</summary>
    public struct LogFileInfo
    {
        /// <summary>檔名(不含目錄)。</summary>
        public string Name;
        public long Bytes;
    }

    /// <summary>
    /// log 檔的命名與「總量超過就從舊的開始刪」的決策。**純函式** —— 不碰檔案系統,
    /// 所以可以直接單元測試(同 <see cref="Sdo.Net.Server.BlobIndex"/> 的作法)。
    ///
    /// 🔴 這是**會刪磁碟上的東西**的程式碼,而且錯的方向不對稱:少刪只是佔空間,
    ///    多刪會把還在查的那天的證據弄不見。所以兩道保險:
    ///      • 只認得自己命名規則的檔案(<see cref="TryParseName"/>),別人的檔案一概不碰 ——
    ///        log 目錄被指到一個共用資料夾時,這是「刪掉使用者的東西」與「什麼都沒發生」的差別。
    ///      • 正在寫的那個檔永遠留著(<c>keepName</c>)。
    /// </summary>
    public static class LogRetention
    {
        /// <summary>預設總容量上限:100 MB(使用者要求)。</summary>
        public const long DefaultCapBytes = 100L * 1024L * 1024L;

        /// <summary>
        /// 單檔切段的大小。一天一檔是常態,但 <c>-v</c> 開著時一天可以噴掉遠不只 100 MB ——
        /// 那時候「照日期分檔」會變成一個大到無法回收的檔案:它超過上限,卻又是正在寫的那個,
        /// 刪不得(刪了等於把今天的 log 整個丟掉)。所以同一天寫滿一段就換 <c>.2</c>、<c>.3</c>,
        /// 讓回收的粒度小於上限。正常用量下永遠只會有 <c>.log</c> 那一個。
        /// </summary>
        public const long SegmentBytes = 8L * 1024L * 1024L;

        private const string Prefix = "sdo-server-";
        private const string Suffix = ".log";

        /// <summary>
        /// 某天第 <paramref name="seq"/> 段的檔名。第 1 段不帶序號 ——
        /// 絕大多數日子只有它,檔名就是乾淨的 <c>sdo-server-2026-07-31.log</c>。
        /// </summary>
        public static string FileName(DateTime day, int seq)
            => Prefix + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
             + (seq > 1 ? "." + seq.ToString(CultureInfo.InvariantCulture) : "") + Suffix;

        /// <summary>檔名 → (日期, 段號)。不是我們產的檔名一律 false。</summary>
        public static bool TryParseName(string name, out DateTime day, out int seq)
        {
            day = default(DateTime);
            seq = 0;
            if (string.IsNullOrEmpty(name)) return false;
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            if (!name.EndsWith(Suffix, StringComparison.Ordinal)) return false;

            string middle = name.Substring(Prefix.Length, name.Length - Prefix.Length - Suffix.Length);
            string datePart = middle;
            seq = 1;

            int dot = middle.IndexOf('.');
            if (dot >= 0)
            {
                datePart = middle.Substring(0, dot);
                string seqPart = middle.Substring(dot + 1);
                if (!int.TryParse(seqPart, NumberStyles.None, CultureInfo.InvariantCulture, out seq)) return false;
                // 「.1」不是我們會產的(第 1 段不帶序號),當成不認得 —— 否則同一段會有兩個名字。
                if (seq < 2) return false;
            }

            if (!DateTime.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                        DateTimeStyles.None, out day)) return false;
            return true;
        }

        /// <summary>是我們產的 log 檔嗎?(掃目錄時先過這一關。)</summary>
        public static bool IsLogName(string name)
        {
            DateTime day; int seq;
            return TryParseName(name, out day, out seq);
        }

        /// <summary>
        /// 哪些檔該刪。從**新到舊**累加大小,第一個讓總量超過 <paramref name="capBytes"/> 的檔案
        /// 以及所有比它舊的,全部刪掉 —— 也就是「超出的話從舊的開始刪」。
        ///
        /// 為什麼超過之後一律全刪、而不是跳過大檔繼續留小的舊檔:留下來的日期會有洞,
        /// 而查問題時「這天沒有 log」與「這天 server 沒開」看起來一模一樣。
        /// </summary>
        /// <param name="capBytes">總量上限。&lt;= 0 = 不限,什麼都不刪。</param>
        /// <param name="keepName">正在寫的那個檔名(永遠不刪);沒有就給 null。</param>
        public static List<string> Plan(IEnumerable<LogFileInfo> files, long capBytes, string keepName)
        {
            var doomed = new List<string>();
            if (files == null || capBytes <= 0) return doomed;

            var known = new List<LogFileInfo>();
            foreach (var f in files)
                if (IsLogName(f.Name)) known.Add(f);

            // 新 → 舊。同一天則段號大的算新。
            known.Sort(NewestFirst);

            long total = 0;
            bool overflowed = false;
            for (int i = 0; i < known.Count; i++)
            {
                var f = known[i];
                if (string.Equals(f.Name, keepName, StringComparison.Ordinal))
                {
                    total += f.Bytes;       // 正在寫的檔佔的空間仍然算進總量,只是不刪
                    continue;
                }
                if (!overflowed && total + f.Bytes <= capBytes)
                {
                    total += f.Bytes;
                    continue;
                }
                overflowed = true;
                doomed.Add(f.Name);
            }

            return doomed;
        }

        private static int NewestFirst(LogFileInfo a, LogFileInfo b)
        {
            DateTime da, db; int sa, sb;
            TryParseName(a.Name, out da, out sa);
            TryParseName(b.Name, out db, out sb);
            int c = db.CompareTo(da);
            if (c != 0) return c;
            // 段號要用數字比,不能靠檔名字串排序(那樣 .10 會排在 .2 前面)。
            return sb.CompareTo(sa);
        }
    }
}
