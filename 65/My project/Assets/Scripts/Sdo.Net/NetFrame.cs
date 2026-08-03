using System.IO;

namespace Sdo.Net
{
    /// <summary>frame 讀取的結果。</summary>
    public enum FrameStatus
    {
        /// <summary>讀到一個完整的 frame。</summary>
        Ok = 0,
        /// <summary>對方正常關閉連線(在 frame 邊界上)。</summary>
        Eof,
        /// <summary>讀到一半連線就斷了 —— 對方掛掉或網路斷。</summary>
        Truncated,
        /// <summary>
        /// length 欄位超過 <see cref="NetLimits.MaxFramePayload"/>。**這個檢查發生在 allocate 之前** ——
        /// 見 <see cref="NetFrame.TryParseHeader"/> 的註解。
        /// </summary>
        TooLarge,
        /// <summary>kind 不是認得的值。</summary>
        BadKind,
    }

    /// <summary>
    /// 網路封包的框架層:<c>[uint32 LE payloadLen][byte kind][payload]</c>。
    ///
    /// **kind 為什麼要存在**:同一條 TCP 連線上要混跑兩種東西 —— JSON control 訊息，和檔案傳輸的
    /// 原始位元組。把二進位資料 base64 塞進 JSON 會多 33% 流量而且大量產生字串垃圾(一首歌幾十 MB
    /// 的話很有感)，所以 chunk 走 kind=1 直接送 raw bytes。兩種 frame 共用同一個 reader。
    ///
    /// **header 的編解碼刻意做成純函式**(<see cref="WriteHeader"/> / <see cref="TryParseHeader"/>):
    /// client 端是背景 thread 的同步讀取，server 端是 async 讀取，兩邊的 IO 寫法不一樣但
    /// **驗證邏輯必須一模一樣** —— 抽成純函式就不可能漂移，而且可以直接單元測試
    /// (不用真的開 socket 就能驗「超長 length 有沒有在 allocate 前被擋掉」)。
    /// </summary>
    public static class NetFrame
    {
        /// <summary>4 bytes length + 1 byte kind。</summary>
        public const int HeaderBytes = 5;

        // ---- 純函式:header 編解碼 ----

        /// <summary>把 header 寫進 buf[offset..offset+5)。呼叫端負責確保空間足夠。</summary>
        public static void WriteHeader(byte[] buf, int offset, int payloadLen, byte kind)
        {
            // little-endian,明確逐 byte 寫 —— 不用 BitConverter，因為它跟著平台的 endianness，
            // 在 big-endian 平台上會產生不相容的 wire format(現在不會遇到，但這種東西寫死比較安心)。
            buf[offset + 0] = (byte)(payloadLen & 0xFF);
            buf[offset + 1] = (byte)((payloadLen >> 8) & 0xFF);
            buf[offset + 2] = (byte)((payloadLen >> 16) & 0xFF);
            buf[offset + 3] = (byte)((payloadLen >> 24) & 0xFF);
            buf[offset + 4] = kind;
        }

        /// <summary>
        /// 從 buf[offset..offset+5) 解出 header 並驗證。
        ///
        /// 🔴 **這裡是防 OOM 的唯一防線。** 呼叫端拿到 payloadLen 之後才會去 allocate 那麼大的緩衝區，
        /// 所以上限檢查必須在這一步就做完。少了它，對方送一個 length=0xFFFFFFFF 的 header
        /// (壞掉的 client、或惡意的封包)就能讓我們試圖配置 4 GB 直接倒掉。
        /// </summary>
        public static FrameStatus TryParseHeader(byte[] buf, int offset, out int payloadLen, out byte kind)
        {
            payloadLen = 0;
            kind = 0;
            // 用 uint 組再轉 int:如果對方送 >= 0x80000000，直接當 int 讀會變負數，
            // 負數的長度檢查很容易寫出漏洞(例如 `len > Max` 對負數是 false)。先用 uint 比大小。
            uint raw = (uint)buf[offset]
                     | ((uint)buf[offset + 1] << 8)
                     | ((uint)buf[offset + 2] << 16)
                     | ((uint)buf[offset + 3] << 24);
            kind = buf[offset + 4];

            if (raw > (uint)NetLimits.MaxFramePayload) return FrameStatus.TooLarge;
            if (kind != NetLimits.FrameKindJson && kind != NetLimits.FrameKindChunk) return FrameStatus.BadKind;

            payloadLen = (int)raw;
            return FrameStatus.Ok;
        }

        /// <summary>把一個完整的 frame 編成位元組(測試與小訊息用;大量發送請直接寫進 stream)。</summary>
        public static byte[] Encode(byte kind, byte[] payload, int offset, int count)
        {
            var frame = new byte[HeaderBytes + count];
            WriteHeader(frame, 0, count, kind);
            if (count > 0) System.Array.Copy(payload, offset, frame, HeaderBytes, count);
            return frame;
        }

        public static byte[] Encode(byte kind, byte[] payload)
            => Encode(kind, payload ?? new byte[0], 0, payload == null ? 0 : payload.Length);

        // ---- 同步 IO(client 的背景 reader thread 用;測試也用這條) ----

        /// <summary>寫一個 frame。</summary>
        public static void Write(Stream s, byte kind, byte[] payload, int offset, int count)
        {
            var header = new byte[HeaderBytes];
            WriteHeader(header, 0, count, kind);
            s.Write(header, 0, HeaderBytes);
            if (count > 0) s.Write(payload, offset, count);
        }

        public static void Write(Stream s, byte kind, byte[] payload)
            => Write(s, kind, payload ?? new byte[0], 0, payload == null ? 0 : payload.Length);

        /// <summary>
        /// 讀一個完整的 frame。回 <see cref="FrameStatus.Ok"/> 時 payload 是剛好長度的新陣列
        /// (payloadLen == 0 時是長度 0 的陣列，不是 null)。
        /// </summary>
        public static FrameStatus TryRead(Stream s, out byte kind, out byte[] payload)
        {
            kind = 0;
            payload = null;

            var header = new byte[HeaderBytes];
            int got = ReadFully(s, header, 0, HeaderBytes);
            if (got == 0) return FrameStatus.Eof;                  // 乾淨的收線:剛好在 frame 邊界
            if (got < HeaderBytes) return FrameStatus.Truncated;    // header 讀一半就斷 = 不正常

            int len;
            var st = TryParseHeader(header, 0, out len, out kind);
            if (st != FrameStatus.Ok) return st;                   // ← allocate 之前就擋掉

            payload = new byte[len];
            if (len > 0 && ReadFully(s, payload, 0, len) < len) { payload = null; return FrameStatus.Truncated; }
            return FrameStatus.Ok;
        }

        /// <summary>
        /// 讀滿 count bytes,回實際讀到的數量。
        /// Stream.Read 是允許少讀的(NetworkStream 尤其常見 —— TCP 不保證一次 Read 拿到整個訊息)，
        /// 忘記迴圈就會得到「偶爾才發生、訊息長度剛好跨過 MTU 才重現」的那種 bug。
        /// </summary>
        private static int ReadFully(Stream s, byte[] buf, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buf, offset + total, count - total);
                if (n <= 0) break;   // 0 = EOF
                total += n;
            }
            return total;
        }
    }
}
