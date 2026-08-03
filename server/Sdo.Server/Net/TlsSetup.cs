using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Sdo.Net;

namespace Sdo.Server.Net
{
    /// <summary>
    /// TLS 憑證的載入與指紋計算。
    ///
    /// 為什麼獨立一支:憑證載入是**開機就要失敗**的那類檢查。埋在連線路徑裡的話,
    /// 「憑證讀不到」的症狀會變成「每個人都連不上,log 只有一行握手失敗」——
    /// 那個訊息不會告訴你是密碼打錯還是檔案沒有私鑰。
    /// </summary>
    public static class TlsSetup
    {
        /// <summary>
        /// 讀 pfx。失敗時回 null 並填 <paramref name="error"/>(呼叫端應該直接讓 server 開不起來)。
        /// </summary>
        public static X509Certificate2 Load(ServerOptions opts, out string error)
        {
            error = null;
            if (opts == null || !opts.TlsEnabled) return null;

            string pass;
            if (!TryResolvePassword(opts, out pass, out error)) return null;

            X509Certificate2 cert;
            try
            {
                // 刻意用預設的 key storage flag:EphemeralKeySet 在 Windows 上與 SslStream 有已知的
                // 「credentials not recognized」問題,而這支程式主要跑 Linux、測試跑 Windows ——
                // 兩邊都要能動比省一個暫存檔重要。
                cert = new X509Certificate2(opts.TlsCertFile, pass, X509KeyStorageFlags.DefaultKeySet);
            }
            catch (CryptographicException ex)
            {
                // 密碼錯與檔案壞在這裡是同一種例外 → 兩種可能都講出來,不要猜。
                error = "讀不到 TLS 憑證(密碼不對,或檔案不是有效的 pfx):" + ex.Message;
                return null;
            }
            catch (Exception ex)
            {
                error = "讀 TLS 憑證失敗:" + ex.GetType().Name + ": " + ex.Message;
                return null;
            }

            if (!cert.HasPrivateKey)
            {
                // 只有公鑰的憑證握手一定失敗。這是最常見的手滑(匯出時忘了勾私鑰)。
                error = "TLS 憑證沒有私鑰 —— 匯出 pfx 時要包含私鑰: " + opts.TlsCertFile;
                cert.Dispose();
                return null;
            }
            return cert;
        }

        /// <summary>pfx 密碼:<c>--tls-pass-file</c> 優先(命令列參數在本機是公開的)。</summary>
        private static bool TryResolvePassword(ServerOptions opts, out string pass, out string error)
        {
            pass = opts.TlsCertPass ?? "";
            error = null;
            if (string.IsNullOrEmpty(opts.TlsCertPassFile)) return true;
            try
            {
                var lines = File.ReadAllLines(opts.TlsCertPassFile);
                // 第一行就是密碼(尾端的 \r\n 由 ReadAllLines 去掉)。空檔 = 沒有密碼。
                pass = lines.Length > 0 ? lines[0] : "";
                return true;
            }
            catch (Exception ex)
            {
                // 🔴 不要把讀到的內容放進錯誤訊息 —— 那正是密碼本身。
                error = "讀不到 --tls-pass-file(" + opts.TlsCertPassFile + "):" + ex.GetType().Name;
                return false;
            }
        }

        /// <summary>
        /// 憑證的 SHA-256 指紋(小寫十六進位)。**開機印出來給玩家複製** ——
        /// 自簽憑證沒有 CA 背書,client 只能靠釘選這串來確認「連到的是這台」。
        /// </summary>
        public static string Fingerprint(X509Certificate2 cert)
            => cert == null ? "" : TlsPinning.ToHex(cert.GetCertHash(HashAlgorithmName.SHA256));
    }
}
