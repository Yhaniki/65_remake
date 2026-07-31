using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Sdo.Server.Logging
{
    /// <summary>
    /// 把 log 行落到磁碟:一天一個檔(<c>sdo-server-2026-07-31.log</c>),
    /// 全部加起來超過上限就從最舊的開始刪。
    ///
    /// ★ 時鐘是注入的(<c>clock</c>)—— 「跨日換檔」與「刪到剩多少」都是只有等真的過一天
    ///   才看得到的行為,不注入就只能靠人工在正式環境觀察,而那正是最不會被驗證的那種程式碼。
    ///
    /// ★ 決策(誰該被刪)全在 <see cref="LogRetention"/>,這裡只做 I/O。
    ///
    /// thread-safe:<see cref="Hub"/> 的 log 從 actor loop 出來,但連線 task 也會直接叫
    /// (TLS 握手失敗那些),所以整個 <see cref="Write"/> 包在同一把鎖裡。log 的量級是
    /// 每秒幾行,鎖爭用不是問題。
    /// </summary>
    internal sealed class LogSink : IDisposable
    {
        private readonly string _dir;
        private readonly long _capBytes;
        private readonly Func<DateTime> _clock;
        private readonly object _gate = new object();

        private StreamWriter _writer;
        private DateTime _day;
        private int _seq;
        private long _bytes;          // 目前這個檔已寫入的位元組(粗估,只用來決定何時換段)
        private string _name;         // 目前這個檔的檔名
        private string _notice;       // 待印的一句話(見 TakeNotice)

        /// <summary>寫檔壞掉的原因。非 null = 從此只剩畫面輸出。</summary>
        public string Error { get; private set; }

        /// <summary>目前正在寫哪個檔(完整路徑)。開機時印出來,才知道要去哪裡撈。</summary>
        public string CurrentPath
        {
            get { lock (_gate) return _name == null ? null : Path.Combine(_dir, _name); }
        }

        public LogSink(string dir, long capBytes, Func<DateTime> clock = null)
        {
            _dir = dir;
            _capBytes = capBytes;
            _clock = clock ?? (() => DateTime.Now);
            lock (_gate) Open(_clock().Date);
        }

        /// <summary>
        /// 寫一行(呼叫端已經加好時間戳與前綴)。
        ///
        /// 寫檔失敗**不能讓 server 掛掉** —— 磁碟滿了的正確反應是繼續服務、少了 log 檔,
        /// 而不是把一場正在進行的遊戲踢掉。所以吞掉例外,只記一次原因。
        /// </summary>
        public void Write(string line)
        {
            lock (_gate)
            {
                if (Error != null) return;
                try
                {
                    var now = _clock();
                    if (_writer == null || now.Date != _day) Open(now.Date);
                    else if (_bytes >= LogRetention.SegmentBytes) Open(_day, _seq + 1);
                    if (_writer == null) return;

                    _writer.WriteLine(line);
                    _bytes += Encoding.UTF8.GetByteCount(line ?? "") + Environment.NewLine.Length;
                }
                catch (Exception ex)
                {
                    Error = ex.Message;
                    _notice = "⚠️  log 檔寫不下去了(" + ex.Message + "),之後只剩畫面輸出";
                    CloseWriter();
                }
            }
        }

        /// <summary>
        /// 取走「有話要說」(刪了哪些舊檔、寫檔壞了)並清空。
        ///
        /// 為什麼不在這個類別裡直接印:印一行 log 會再繞回 <see cref="Write"/>,而 Write 正是
        /// 觸發換檔/清理的地方 —— 在鎖裡面回頭呼叫自己,是「清理時剛好跨日」那種只在半夜出現一次
        /// 的當機。交給呼叫端在鎖外面印,順序也才正確(先有那一行,再有它引發的清理訊息)。
        /// </summary>
        public string TakeNotice()
        {
            lock (_gate)
            {
                var n = _notice;
                _notice = null;
                return n;
            }
        }

        public void Dispose()
        {
            lock (_gate) CloseWriter();
        }

        // ---- 內部:開檔 / 換檔 / 清理。全部在 _gate 裡呼叫。----

        private void Open(DateTime day, int seq = 0)
        {
            CloseWriter();
            Directory.CreateDirectory(_dir);

            // seq = 0 → 自己找:接續當天已經有的最後一段(server 重啟不該蓋掉今天的 log),
            // 那一段滿了就開下一段。
            if (seq <= 0)
            {
                seq = LastSeqOf(day);
                long existing = LengthOf(LogRetention.FileName(day, seq));
                if (existing >= LogRetention.SegmentBytes) seq++;
            }

            _day = day;
            _seq = seq;
            _name = LogRetention.FileName(day, seq);
            string path = Path.Combine(_dir, _name);
            _bytes = LengthOf(_name);

            // AutoFlush:server 被 kill 時緩衝區裡的東西就沒了,而那幾行往往正是要看的
            // (Program.cs 對 stdout 也是同樣的理由)。log 的量級撐得住每行 flush。
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                                       new UTF8Encoding(false)) { AutoFlush = true };

            Sweep();
        }

        private void CloseWriter()
        {
            if (_writer == null) return;
            try { _writer.Dispose(); } catch { /* 收尾失敗沒有補救動作 */ }
            _writer = null;
        }

        /// <summary>當天已經寫到第幾段(沒有就是 1)。</summary>
        private int LastSeqOf(DateTime day)
        {
            int best = 1;
            foreach (var f in ListLogFiles())
            {
                DateTime d; int s;
                if (!LogRetention.TryParseName(f.Name, out d, out s)) continue;
                if (d == day && s > best) best = s;
            }
            return best;
        }

        private long LengthOf(string name)
        {
            try
            {
                var fi = new FileInfo(Path.Combine(_dir, name));
                return fi.Exists ? fi.Length : 0;
            }
            catch { return 0; }
        }

        private List<LogFileInfo> ListLogFiles()
        {
            var list = new List<LogFileInfo>();
            string[] paths;
            try { paths = Directory.GetFiles(_dir); }
            catch { return list; }

            for (int i = 0; i < paths.Length; i++)
            {
                string name = Path.GetFileName(paths[i]);
                if (!LogRetention.IsLogName(name)) continue;
                long len;
                try { len = new FileInfo(paths[i]).Length; } catch { continue; }
                list.Add(new LogFileInfo { Name = name, Bytes = len });
            }
            return list;
        }

        /// <summary>總量超過上限就從最舊的開始刪。換檔時做一次就夠(檔案只在那時候變多)。</summary>
        private void Sweep()
        {
            var doomed = LogRetention.Plan(ListLogFiles(), _capBytes, _name);
            if (doomed.Count == 0) return;

            int deleted = 0;
            long freed = 0;
            for (int i = 0; i < doomed.Count; i++)
            {
                long len = LengthOf(doomed[i]);
                try { File.Delete(Path.Combine(_dir, doomed[i])); }
                catch { continue; }
                deleted++;
                freed += len;
            }
            if (deleted > 0)
                _notice = "log 超過 " + (_capBytes / (1024 * 1024)) + " MB,刪掉 " + deleted
                        + " 個舊檔(釋出 " + (freed / (1024 * 1024)) + " MB)";
        }
    }
}
