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
        /// 進站密碼。空 = 不檢查(用 <c>--password ""</c> 明確關掉)。
        ///
        /// 預設值與 client 的 <c>RoomConfig.DefaultServerPassword</c> **必須一致** ——
        /// 兩邊都不改就能直接連上,而且密碼機制是啟用的(不是空密碼放行)。
        /// 有一個測試釘住這件事:改了一邊忘了另一邊的話,誰都連不進來,
        /// 而錯誤訊息只會說「密碼不對」,不會告訴你是預設值漂移了。
        ///
        /// ⚠️ 這只是個門檻,不是認證 —— <c>playerId</c> 完全由 client 自稱,連線也沒有加密。
        /// </summary>
        public string Password = DefaultPassword;

        /// <summary>
        /// 預設進站密碼。**指向共用的 <see cref="NetLimits.DefaultServerPassword"/>** ——
        /// client 端(<c>RoomConfig</c>)也是指同一個常數,所以兩邊不可能漂移。
        /// </summary>
        public const string DefaultPassword = NetLimits.DefaultServerPassword;

        public int MaxRooms = NetLimits.DefaultMaxRooms;
        public int MaxConnections = NetLimits.DefaultMaxConnections;

        /// <summary>歌曲檔案保留時數(使用者要求「最多留一天」)。</summary>
        public int TtlHours = NetLimits.DefaultBlobTtlHours;

        /// <summary>blob 目錄的總容量上限(GB)。</summary>
        public int MaxTotalBlobGb = NetLimits.DefaultMaxTotalBlobGb;

        // ---- 公網化(M10)。四個都預設關閉/寬鬆 → LAN 的行為完全不變。----

        /// <summary>
        /// token 檔路徑(一行一個 token,見 <see cref="Sdo.Net.Server.AuthTokens"/>)。
        /// 空 = 不啟用 token 認證(身分沿用 client 自稱的,只適合 LAN)。
        /// </summary>
        public string TokensFile = "";

        /// <summary>只接受這些來源(逗號分隔;<c>192.168.0.</c> 這種以 . 結尾的是前綴網段)。空 = 不限。</summary>
        public string AllowFrom = "";

        /// <summary>同一個 IP 的連線數上限。一份 client 正常用 2 條(control + file)。</summary>
        public int MaxPerIp = Sdo.Net.Server.OriginPolicy.DefaultMaxPerIp;

        /// <summary>每人每小時的上傳位元組上限。0 = 不限。</summary>
        public long UploadBytesPerHour;

        /// <summary>
        /// TLS 憑證檔(PKCS#12 / <c>.pfx</c>,必須含私鑰)。空 = 不加密(明文 TCP)。
        ///
        /// 為什麼是 .pfx 而不是 pem 一對:<see cref="System.Security.Cryptography.X509Certificates.X509Certificate2"/>
        /// 讀 pfx 是一行,而「憑證與私鑰是不是同一組」這件事由檔案格式本身保證 ——
        /// 給兩個路徑的話,配錯的症狀是握手失敗,而錯誤訊息不會告訴你是配錯了。
        /// </summary>
        public string TlsCertFile = "";

        /// <summary>
        /// pfx 的密碼。空 = 沒有密碼。
        /// ⚠️ 命令列參數在同一台機器上是**任何人都看得到的**(<c>ps</c> / <c>/proc</c>),
        /// 所以有密碼的憑證請用 <see cref="TlsCertPassFile"/>。
        /// </summary>
        public string TlsCertPass = "";

        /// <summary>pfx 密碼的檔案路徑(只讀第一行、去掉尾端換行)。與 <see cref="TlsCertPass"/> 二選一。</summary>
        public string TlsCertPassFile = "";

        /// <summary>TLS 有沒有開。</summary>
        public bool TlsEnabled => !string.IsNullOrEmpty(TlsCertFile);

        /// <summary>房號池的洗牌種子。0 = 用開機時間(讓每次啟動的房號順序不同)。</summary>
        public int CodeSeed;

        /// <summary>印出每一筆收發的訊息(除錯用;訊息量大時很吵)。</summary>
        public bool Verbose;

        /// <summary>log 檔的目錄。空 = <c>&lt;data&gt;/logs</c>(見 <see cref="LogDirOrDefault"/>)。</summary>
        public string LogDir = "";

        /// <summary>
        /// 所有 log 檔加起來的容量上限(MB)。0 = 不寫檔案,只印畫面。
        /// 超過就從最舊的那天開始刪(見 <see cref="Sdo.Server.Logging.LogRetention"/>)。
        /// </summary>
        public int LogCapMb = DefaultLogCapMb;

        /// <summary>log 保留量的預設值:100 MB(使用者要求)。</summary>
        public const int DefaultLogCapMb = 100;

        /// <summary>blob 存放位置。</summary>
        public string BlobDir => Path.Combine(DataDir, "blobs");

        /// <summary>
        /// log 實際寫去哪。預設放在 data 底下 —— 部署時本來就只有 data 這一個目錄
        /// 保證存在而且 server 有寫入權(DEPLOY.md §3),多一個要另外授權的路徑只會多一種
        /// 「開機就寫不出 log」的失敗方式。
        /// </summary>
        public string LogDirOrDefault
            => string.IsNullOrEmpty(LogDir) ? Path.Combine(DataDir, "logs") : LogDir;

        /// <summary>log 容量上限(位元組)。</summary>
        public long LogCapBytes => (long)LogCapMb * 1024L * 1024L;

        public static string Usage =>
            "sdo-server — 勁舞團重製版連線伺服器\n" +
            "\n" +
            "用法: sdo-server [選項]\n" +
            "\n" +
            "  --port <n>           監聽 port(預設 27015)\n" +
            "  --bind <addr>        綁定位址(預設 0.0.0.0 = 全部介面;127.0.0.1 = 只聽本機)\n" +
            "  --data <dir>         資料目錄(預設 ./data)\n" +
            "  --password <pw>      進站密碼(預設 " + DefaultPassword + ";給空字串 \"\" = 不檢查)\n" +
            "  --max-rooms <n>      同時開房上限(預設 " + NetLimits.DefaultMaxRooms + ")\n" +
            "  --max-conns <n>      連線數上限(預設 " + NetLimits.DefaultMaxConnections + ")\n" +
            "  --ttl-hours <n>      歌曲暫存保留時數(預設 " + NetLimits.DefaultBlobTtlHours + ")\n" +
            "  --max-blob-gb <n>    歌曲暫存總容量上限 GB(預設 " + NetLimits.DefaultMaxTotalBlobGb + ")\n" +
            "  --code-seed <n>      房號洗牌種子(預設隨機;給固定值可重現)\n" +
            "  --log-dir <dir>      log 檔目錄(預設 <data>/logs;依日期一天一個檔)\n" +
            "  --log-mb <n>         log 檔總容量上限 MB(預設 " + DefaultLogCapMb + ";超過從最舊的刪,0 = 不寫檔)\n" +
            "  -v, --verbose        印出每筆訊息\n" +
            "\n" +
            "公網化(預設全關 → LAN 行為不變):\n" +
            "  --tokens <file>      token 檔(一行一個;啟用後**身分由 server 決定**,不再信 client 自稱)\n" +
            "  --allow-from <list>  只接受這些來源(逗號分隔;192.168.0. = 前綴網段)\n" +
            "  --max-per-ip <n>     同一個 IP 的連線數上限(預設 " + Sdo.Net.Server.OriginPolicy.DefaultMaxPerIp + ")\n" +
            "  --upload-mb-hour <n> 每人每小時上傳上限 MB(0 = 不限)\n" +
            "  --tls-cert <file>    TLS 憑證(.pfx,含私鑰)。給了就加密;開機會印出憑證指紋,\n" +
            "                       自簽憑證要把那串填進 client 的 config.ini serverCertFingerprint\n" +
            "  --tls-pass <pw>      pfx 密碼(⚠️ 命令列在本機是公開的,建議用 --tls-pass-file)\n" +
            "  --tls-pass-file <f>  從檔案讀 pfx 密碼(只讀第一行)\n" +
            "\n" +
            "  -h, --help           顯示這段說明\n" +
            "\n" +
            "⚠️ 上面那四個公網化參數**一個都不給**時:沒有帳號認證、沒有加密,身分由 client 自稱。\n" +
            "   那是 LAN／信任的朋友之間的模式(也是預設)。要開在公網,四個都要給 ——\n" +
            "   完整步驟見 server/OPERATIONS.md。開機時會把現在受哪些保護印出來,\n" +
            "   ⚠️ 開頭的每一行就是一個缺口。\n";

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
                    case "--log-dir":
                        if (!NextString(args, ref i, out opts.LogDir, out error)) return false;
                        break;
                    case "--log-mb":
                        if (!NextInt(args, ref i, out opts.LogCapMb, out error)) return false;
                        break;

                    case "--tokens":
                        if (!NextString(args, ref i, out opts.TokensFile, out error)) return false;
                        break;
                    case "--allow-from":
                        if (!NextString(args, ref i, out opts.AllowFrom, out error)) return false;
                        break;
                    case "--max-per-ip":
                        if (!NextInt(args, ref i, out opts.MaxPerIp, out error)) return false;
                        break;
                    case "--upload-mb-hour":
                        {
                            int mb;
                            if (!NextInt(args, ref i, out mb, out error)) return false;
                            opts.UploadBytesPerHour = mb <= 0 ? 0 : (long)mb * 1024L * 1024L;
                            break;
                        }

                    case "--tls-cert":
                        if (!NextString(args, ref i, out opts.TlsCertFile, out error)) return false;
                        break;
                    case "--tls-pass":
                        if (!NextString(args, ref i, out opts.TlsCertPass, out error)) return false;
                        break;
                    case "--tls-pass-file":
                        if (!NextString(args, ref i, out opts.TlsCertPassFile, out error)) return false;
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

            LogDir = (LogDir ?? "").Trim();
            // 負數當成 0(不寫檔),不要因為打錯一個減號就讓 server 起不來 —— log 設定不值得擋開機。
            if (LogCapMb < 0) LogCapMb = 0;

            if (MaxRooms < 1) MaxRooms = 1;
            if (MaxConnections < 2) MaxConnections = 2;      // 至少要能容納兩個人才有「多人」
            if (TtlHours < 1) TtlHours = 1;
            if (MaxTotalBlobGb < 1) MaxTotalBlobGb = 1;
            TokensFile = (TokensFile ?? "").Trim();
            AllowFrom = (AllowFrom ?? "").Trim();
            if (MaxPerIp < 0) MaxPerIp = 0;
            if (UploadBytesPerHour < 0) UploadBytesPerHour = 0;

            TlsCertFile = (TlsCertFile ?? "").Trim();
            TlsCertPassFile = (TlsCertPassFile ?? "").Trim();
            TlsCertPass = TlsCertPass ?? "";        // 密碼**不 Trim**:尾端空白可能是密碼的一部分
            // 憑證路徑要在開機時就擋下來,不要等第一個人連進來才發現讀不到 ——
            // 那時候的症狀是「所有人都連不上」,而 log 只會有一行握手失敗。
            if (TlsEnabled && !File.Exists(TlsCertFile))
            {
                error = "--tls-cert 找不到憑證檔: " + TlsCertFile;
                return false;
            }
            if (TlsCertPassFile.Length > 0 && !File.Exists(TlsCertPassFile))
            {
                error = "--tls-pass-file 找不到檔案: " + TlsCertPassFile;
                return false;
            }
            if (TlsCertPassFile.Length > 0 && !TlsEnabled)
            {
                error = "給了 --tls-pass-file 卻沒給 --tls-cert";
                return false;
            }

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
