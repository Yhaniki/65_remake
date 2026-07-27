using System;

namespace Sdo.Server
{
    /// <summary>
    /// 入口。目前只是骨架 —— 監聽、連線處理與房間 hub 在 B6/B7 階段填實。
    /// 現階段的價值:讓 <c>dotnet build</c> 能真的編一次共用原始碼(Sdo.Net + Sdo.Osu),
    /// 在 LangVersion 8.0 下把「用了 Unity 編不過的語法」這種問題當場擋下來。
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            Console.WriteLine("sdo-server (protocol v" + Sdo.Net.NetProto.Version + ")");
            Console.WriteLine("尚未實作監聽 —— 目前只是編譯與測試用的骨架。");
            return 0;
        }
    }
}
