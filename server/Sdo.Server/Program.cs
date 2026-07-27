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
