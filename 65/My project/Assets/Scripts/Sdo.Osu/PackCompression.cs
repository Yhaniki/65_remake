using System;
using System.IO;
using System.IO.Compression;

namespace Sdo.Osu
{
    /// <summary>
    /// 傳檔用的逐檔壓縮(raw deflate)。**client 與 server 編同一份** —— 兩邊對「壓縮後的位元組長什麼樣」
    /// 必須有完全相同的認知,不然收端切不出檔案邊界。
    ///
    /// <b>為什麼要壓。</b>MMD 模型的貼圖常常是**未壓縮的 .tga**(4096² RGBA = 64 MB 一張)。
    /// 實測一份模型:353 MB → 39 MB(11%),.tga 壓到 5-11%,而已經壓過的 .png 幾乎不動(99%)。
    /// 沒有這一層的話,那種模型不是撞上限被拒,就是要每個同房的人下載 353 MB。
    ///
    /// 🔴 <b>sha256 與 packId 永遠算「原始」內容。</b>壓縮只存在於線路上與 server 的磁碟上,
    /// 模型的身分完全不受影響 —— 開了壓縮之後 packId 不變,既有的包也不用重算。
    /// 收端解壓完照樣拿原始內容去比對 sha256,所以壓縮壞掉一定會被抓到,不會靜靜地寫出壞檔。
    ///
    /// 用 <see cref="DeflateStream"/>(raw deflate,不是 gzip)—— 兩邊都是 .NET,不需要 gzip 的檔頭與 CRC
    /// (內容正確性本來就由 sha256 保證,再加一層 CRC 只是多花位元組)。
    /// </summary>
    public static class PackCompression
    {
        /// <summary>壓縮檔的副檔名(server 的 blob 與 client 的接收暫存都用它)。</summary>
        public const string Extension = ".z";

        /// <summary>串流搬運用的緩衝大小。64 KiB —— 與傳輸的 chunk 同級,不必更大。</summary>
        private const int BufferBytes = 64 * 1024;

        /// <summary>
        /// <paramref name="srcPath"/> → <paramref name="dstPath"/>(deflate)。回傳壓縮後的位元組數,失敗回 -1。
        /// 失敗時不會留下半個目的檔。
        /// </summary>
        public static long CompressFile(string srcPath, string dstPath)
        {
            if (string.IsNullOrEmpty(srcPath) || string.IsNullOrEmpty(dstPath)) return -1;
            try
            {
                var dir = Path.GetDirectoryName(dstPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                using (var src = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var dst = new FileStream(dstPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    // Optimal 而不是 Fastest:這些位元組要在網路上走,而且 server 會把壓縮版**存起來**
                    // 給之後每一個人下載 —— 壓一次、傳很多次,值得多花那點 CPU。
                    using (var z = new DeflateStream(dst, CompressionLevel.Optimal, leaveOpen: true))
                        Copy(src, z);
                    return dst.Length;
                }
            }
            catch
            {
                TryDelete(dstPath);
                return -1;
            }
        }

        /// <summary>
        /// <paramref name="srcPath"/>(deflate)→ <paramref name="dstPath"/>(原始)。成功回 true。
        /// 失敗時不會留下半個目的檔 —— 半個檔會讓呼叫端的 sha256 比對指向「內容不符」而不是真正的原因。
        /// </summary>
        public static bool DecompressFile(string srcPath, string dstPath)
        {
            if (string.IsNullOrEmpty(srcPath) || string.IsNullOrEmpty(dstPath)) return false;
            try
            {
                var dir = Path.GetDirectoryName(dstPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                using (var src = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var z = new DeflateStream(src, CompressionMode.Decompress))
                using (var dst = new FileStream(dstPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    Copy(z, dst);
                return true;
            }
            catch
            {
                TryDelete(dstPath);
                return false;
            }
        }

        // Stream.CopyTo 在這個專案的 netstandard2.0 目標上有,但自己搬一次可以固定緩衝大小,
        // 而且兩個方向共用同一段(壓縮那邊不能用 CopyTo —— 要寫進 DeflateStream 而不是從它讀)。
        private static void Copy(Stream from, Stream to)
        {
            var buf = new byte[BufferBytes];
            int n;
            while ((n = from.Read(buf, 0, buf.Length)) > 0) to.Write(buf, 0, n);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
