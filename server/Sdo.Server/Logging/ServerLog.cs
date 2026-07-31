using System;
using System.Globalization;

namespace Sdo.Server.Logging
{
    /// <summary>
    /// server 的日誌出口。每一行都是 <c>2026-07-31 14:03:12 [sdo-server] …</c>,
    /// 同時進畫面(stdout/stderr)與 <c>&lt;data&gt;/logs/</c> 底下依日期分的檔案。
    ///
    /// 為什麼時間戳要自己加、而不是靠外面的工具:實際的部署是 tmux + `tee`(見 DEPLOY.md §14),
    /// tee 不會加時間;journald 會加,但那份時間只存在 journal 裡,`sdoctl log` 撈出來的
    /// 純文字檔就沒有了。「這件事發生在幾點」是排查連線問題的第一個問題 ——
    /// 只有寫在行內才到得了每一個看得到這行的地方。
    ///
    /// 為什麼 server 自己寫檔、而不是交給 logrotate:tmux 那套沒有 journald 幫忙輪替,
    /// server.log 會一直長到把磁碟寫滿(DEPLOY.md §10 原本只能叫人手動 truncate)。
    ///
    /// 整段(畫面 + 檔案)包在同一把鎖裡 —— 兩邊的行順序不一致的話,拿畫面上的順序去推論
    /// 事件因果會得到錯的結論。
    /// </summary>
    public static class ServerLog
    {
        private const string Tag = "[sdo-server] ";
        private static readonly object Gate = new object();
        private static LogSink _sink;
        private static string _initError;
        private static long _capBytes;

        /// <summary>時間戳的時鐘。預設本機時間(看 log 的人用的就是那個時間)。測試會換掉。</summary>
        internal static Func<DateTime> Clock = () => DateTime.Now;

        /// <summary>
        /// 開始寫檔。<paramref name="capBytes"/> &lt;= 0 或 <paramref name="dir"/> 空 = 只印畫面。
        /// 建不出目錄不是致命錯誤 —— 少了 log 檔還是能跑,但 <see cref="Describe"/> 會講出來。
        /// </summary>
        public static void Init(string dir, long capBytes)
        {
            lock (Gate)
            {
                if (_sink != null) { _sink.Dispose(); _sink = null; }
                _initError = null;
                _capBytes = capBytes;
                if (string.IsNullOrEmpty(dir) || capBytes <= 0) return;
                try { _sink = new LogSink(dir, capBytes); }
                catch (Exception ex) { _sink = null; _initError = ex.Message; }
            }
        }

        /// <summary>開機橫幅用:log 現在寫去哪、留多少。</summary>
        public static string Describe()
        {
            lock (Gate)
            {
                if (_initError != null) return "⚠️  寫不出 log 檔(" + _initError + "),只印畫面";
                if (_sink == null) return "只印畫面(--log-mb 0)";
                if (_sink.Error != null) return "⚠️  寫不出 log 檔(" + _sink.Error + "),只印畫面";
                return "→ " + _sink.CurrentPath + "(依日期分檔,總量上限 "
                     + (_capBytes / (1024 * 1024)) + " MB,超過從最舊的刪)";
            }
        }

        /// <summary>一般訊息。</summary>
        public static void Write(string line)
        {
            string notice;
            string stamped = Stamp(line);
            lock (Gate)
            {
                Console.WriteLine(stamped);
                notice = WriteToFile(stamped);
            }
            // 鎖外面才印 —— 這一句是**檔案換檔時**產生的,在鎖裡回頭呼叫自己會遞迴進 Write。
            if (notice != null) Write(notice);
        }

        /// <summary>錯誤訊息(走 stderr,檔案裡與一般訊息同格式)。</summary>
        public static void Error(string line)
        {
            string notice;
            string stamped = Stamp(line);
            lock (Gate)
            {
                Console.Error.WriteLine(stamped);
                notice = WriteToFile(stamped);
            }
            if (notice != null) Write(notice);
        }

        /// <summary>收尾(把檔案關掉)。程式結束時呼叫;AutoFlush 已經保證內容不會漏。</summary>
        public static void Close()
        {
            lock (Gate)
            {
                if (_sink == null) return;
                _sink.Dispose();
                _sink = null;
            }
        }

        /// <summary>寫進檔案,回傳 sink「有話要說」的那一句(刪了哪些舊檔之類),沒有就 null。</summary>
        private static string WriteToFile(string stamped)
        {
            if (_sink == null) return null;
            _sink.Write(stamped);
            return _sink.TakeNotice();
        }

        internal static string Stamp(string line)
            => Clock().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + Tag + line;
    }
}
