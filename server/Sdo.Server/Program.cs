using System;
using System.IO;
using Sdo.Server.Net;

namespace Sdo.Server
{
    /// <summary>
    /// 入口。解析參數 → 建目錄 → 跑 <see cref="Hub"/>。
    ///
    /// Ctrl-C / SIGTERM 會讓 Hub 收線後正常結束(systemd 重啟時不會留下半開的 socket)。
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            // 🔴 stdout 被重導向(systemd → journald,或 `sdo-server > log.txt`)時,.NET 交給我們的是一個
            // AutoFlush=false 的 StreamWriter —— 日誌會卡在緩衝區裡,`journalctl -f` 看起來像「server 沒在動」,
            // 要等緩衝區滿或程式結束才一次噴出來。對一個要靠日誌看發生什麼事的常駐服務來說這不能接受。
            // (直接接終端機時 .NET 本來就會 flush;這行把兩種情況統一成「寫一行就看得到一行」。)
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

            ServerOptions opts;
            string error;
            bool wantsHelp;
            if (!ServerOptions.TryParse(args, out opts, out error, out wantsHelp))
            {
                if (wantsHelp) { Console.WriteLine(ServerOptions.Usage); return 0; }
                Console.Error.WriteLine("參數錯誤: " + error);
                Console.Error.WriteLine();
                Console.Error.WriteLine(ServerOptions.Usage);
                return 2;
            }

            try
            {
                Directory.CreateDirectory(opts.DataDir);
                Directory.CreateDirectory(opts.BlobDir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("建不出資料目錄 '" + opts.DataDir + "': " + ex.Message);
                return 3;
            }

            var hub = new Hub(opts);

            // 🔴 憑證載不起來就**不要啟動**。退回明文是最糟的選擇:使用者以為連線是加密的,
            // 實際上不是,而且完全沒有徵兆(client 那邊也連得上,因為它只是連不到 TLS 而已)。
            if (hub.TlsError != null)
            {
                Console.Error.WriteLine("[sdo-server] TLS 設定有問題,拒絕以明文啟動: " + hub.TlsError);
                return 4;
            }

            // Ctrl-C(SIGINT)與 systemd 的 SIGTERM 都要能讓它乾淨收線。
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;              // 不要讓 runtime 直接砍掉我們
                Console.WriteLine();
                Console.WriteLine("[sdo-server] 收到中斷,正在收線…");
                hub.Stop();
            };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => hub.Stop();

            try
            {
                hub.Run();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[sdo-server] 致命錯誤: " + ex);
                return 1;
            }

            Console.WriteLine("[sdo-server] 已停止。");
            return 0;
        }
    }
}
