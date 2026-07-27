using System;
using System.Globalization;
using System.IO;
using Sdo.Net;

namespace Sdo.Server
{
    /// <summary>
    /// 伺服器的啟動選項。全部從命令列來 —— 刻意不做設定檔:
    /// server 的部署方式是 systemd unit(參數寫在 unit 檔裡),多一個設定檔只是多一個
    /// 「改了沒生效」的來源。
    /// </summary>
    public sealed class ServerOptions
    {
        /// <summary>監聽 port。與 client 的 <c>config.ini [Net] serverPort</c> 對應。</summary>
        public int Port = 27015;

        /// <summary>綁定位址。預設綁全部介面;要只聽本機就給 <c>127.0.0.1</c>。</summary>
        public string Bind = "0.0.0.0";

        /// <summary>歌曲暫存與其他落地資料的根目錄。</summary>
        public string DataDir = "data";

        /// <summary>
        /// 進站密碼。空 = 不檢查。
        /// ⚠️ 這只是個門檻,不是認證 —— <c>playerId</c> 完全由 client 自稱,連線也沒有加密。
        /// </summary>
        public string Password = "";

        public int MaxRooms = NetLimits.DefaultMaxRooms;
        public int MaxConnections = NetLimits.DefaultMaxConnections;

        /// <summary>歌曲檔案保留時數(使用者要求「最多留一天」)。</summary>
        public int TtlHours = NetLimits.DefaultBlobTtlHours;

        /// <summary>blob 目錄的總容量上限(GB)。</summary>
        public int MaxTotalBlobGb = NetLimits.DefaultMaxTotalBlobGb;

        /// <summary>房號池的洗牌種子。0 = 用開機時間(讓每次啟動的房號順序不同)。</summary>
        public int CodeSeed;

        /// <summary>印出每一筆收發的訊息(除錯用;訊息量大時很吵)。</summary>
        public bool Verbose;

        /// <summary>blob 存放位置。</summary>
        public string BlobDir => Path.Combine(DataDir, "blobs");

        public static string Usage =>
            "sdo-server — 勁舞團重製版連線伺服器\n" +
            "\n" +
            "用法: sdo-server [選項]\n" +
            "\n" +
            "  --port <n>           監聽 port(預設 27015)\n" +
            "  --bind <addr>        綁定位址(預設 0.0.0.0 = 全部介面;127.0.0.1 = 只聽本機)\n" +
            "  --data <dir>         資料目錄(預設 ./data)\n" +
            "  --password <pw>      進站密碼(預設無)\n" +
            "  --max-rooms <n>      同時開房上限(預設 " + NetLimits.DefaultMaxRooms + ")\n" +
            "  --max-conns <n>      連線數上限(預設 " + NetLimits.DefaultMaxConnections + ")\n" +
            "  --ttl-hours <n>      歌曲暫存保留時數(預設 " + NetLimits.DefaultBlobTtlHours + ")\n" +
            "  --max-blob-gb <n>    歌曲暫存總容量上限 GB(預設 " + NetLimits.DefaultMaxTotalBlobGb + ")\n" +
            "  --code-seed <n>      房號洗牌種子(預設隨機;給固定值可重現)\n" +
            "  -v, --verbose        印出每筆訊息\n" +
            "  -h, --help           顯示這段說明\n" +
            "\n" +
            "⚠️ MVP 階段沒有帳號認證、沒有加密 —— 身分由 client 自稱。\n" +
            "   請只在 LAN 或信任的朋友之間使用,不要直接開在公網。\n";

        /// <summary>
        /// 解析命令列。回 false 時 <paramref name="error"/> 有原因(或 <paramref name="wantsHelp"/> 為 true)。
        /// 純函式 —— 可以直接單元測試,不用真的啟動 server。
        /// </summary>
        public static bool TryParse(string[] args, out ServerOptions opts, out string error, out bool wantsHelp)
        {
            opts = new ServerOptions();
            error = null;
            wantsHelp = false;
            if (args == null) return true;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "-h":
                    case "--help":
                        wantsHelp = true;
                        return false;

                    case "-v":
                    case "--verbose":
                        opts.Verbose = true;
                        break;

                    case "--port":
                        if (!NextInt(args, ref i, out opts.Port, out error)) return false;
                        break;
                    case "--bind":
                        if (!NextString(args, ref i, out opts.Bind, out error)) return false;
                        break;
                    case "--data":
                        if (!NextString(args, ref i, out opts.DataDir, out error)) return false;
                        break;
                    case "--password":
                        if (!NextString(args, ref i, out opts.Password, out error)) return false;
                        break;
                    case "--max-rooms":
                        if (!NextInt(args, ref i, out opts.MaxRooms, out error)) return false;
                        break;
                    case "--max-conns":
                        if (!NextInt(args, ref i, out opts.MaxConnections, out error)) return false;
                        break;
                    case "--ttl-hours":
                        if (!NextInt(args, ref i, out opts.TtlHours, out error)) return false;
                        break;
                    case "--max-blob-gb":
                        if (!NextInt(args, ref i, out opts.MaxTotalBlobGb, out error)) return false;
                        break;
                    case "--code-seed":
                        if (!NextInt(args, ref i, out opts.CodeSeed, out error)) return false;
                        break;

                    default:
                        error = "不認得的選項: " + a;
                        return false;
                }
            }

            return opts.Validate(out error);
        }

        /// <summary>夾值 + 檢查。</summary>
        public bool Validate(out string error)
        {
            error = null;

            // port 0 是合法的:讓 OS 配一個空閒 port(整合測試用;實際部署會給明確的 port)。
            if (Port < 0 || Port > 65535) { error = "--port 必須在 0..65535(0 = 讓系統挑)"; return false; }
            if (string.IsNullOrWhiteSpace(Bind)) { error = "--bind 不能是空的"; return false; }
            if (string.IsNullOrWhiteSpace(DataDir)) { error = "--data 不能是空的"; return false; }

            Bind = Bind.Trim();
            DataDir = DataDir.Trim();
            Password = (Password ?? "").Trim();

            if (MaxRooms < 1) MaxRooms = 1;
            if (MaxConnections < 2) MaxConnections = 2;      // 至少要能容納兩個人才有「多人」
            if (TtlHours < 1) TtlHours = 1;
            if (MaxTotalBlobGb < 1) MaxTotalBlobGb = 1;

            return true;
        }

        private static bool NextString(string[] args, ref int i, out string value, out string error)
        {
            value = null;
            error = null;
            if (i + 1 >= args.Length) { error = args[i] + " 後面要跟一個值"; return false; }
            value = args[++i];
            return true;
        }

        private static bool NextInt(string[] args, ref int i, out int value, out string error)
        {
            value = 0;
            string s;
            if (!NextString(args, ref i, out s, out error)) return false;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                error = args[i - 1] + " 要一個整數,收到:" + s;
                return false;
            }
            return true;
        }

        public override string ToString()
            => string.Format(CultureInfo.InvariantCulture,
                "bind={0}:{1} data={2} password={3} maxRooms={4} maxConns={5} ttl={6}h blobCap={7}GB",
                Bind, Port, DataDir, string.IsNullOrEmpty(Password) ? "(none)" : "(set)",
                MaxRooms, MaxConnections, TtlHours, MaxTotalBlobGb);
    }
}
