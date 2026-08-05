using System;
using System.Security.Cryptography;
using System.Text;

namespace Sdo.Settings.Vfs
{
    /// <summary>
    /// SDOPAK 的金鑰派生與串流加解密。規格見 <c>docs/architecture/data-packaging.md</c> §5。
    ///
    /// <para>⚠️ <b>這是混淆,不是保護。</b> 用戶端的金鑰必然在執行檔裡,有決心的人幾十分鐘就能取出
    /// —— 我們自己就是這樣把原版 SDO 拆開的。它能達成的實際目標只有兩個,而這兩個有價值:
    /// 別人沒法把 DATA 整包拷走直接用、玩家沒法隨手換掉 .dds 作弊。不要對它有超出「防君子」的期待。</para>
    ///
    /// <para><b>資料區是一條 CTR 金鑰流。</b> counter block = 資料區內的絕對位移 / 16。CTR 模式最致命的錯誤是
    /// 同金鑰重用 counter(金鑰流重用 = 直接破),用絕對位移當起點時條目之間不重疊 ⇒ 金鑰流永不重複,
    /// 而且仍可隨機存取:要讀位移 O 的條目,從 counter O/16 起算、跳過 O%16 bytes 即可。</para>
    /// </summary>
    public static class PakCrypto
    {
        /// <summary>HKDF 的 salt —— 用 magic,兩邊都拿得到、不必另外傳。</summary>
        private static readonly byte[] Salt = PakFormat.Magic;

        public const string InfoData = "sdopak:data:";
        public const string InfoIndex = "sdopak:idx:";
        public const string InfoMac = "sdopak:mac:";

        // master key 的四段。刻意分開寫、由不同的常數湊出來,讓 `strings` 撈不到一整條金鑰。
        // 再次強調:這只是提高門檻。反組譯出 CombineMaster() 就全拿到了。
        private static readonly byte[] Seg0 = { 0x53, 0x44, 0x4F, 0x2D, 0x50, 0x41, 0x4B, 0x2D };
        private static readonly byte[] Seg1 = { 0x9C, 0x41, 0xE7, 0x0B, 0x76, 0xD2, 0x38, 0xA5 };
        private static readonly byte[] Seg2 = { 0x1F, 0xB8, 0x64, 0xCA, 0x03, 0x9D, 0x52, 0xE6 };
        private static readonly byte[] Seg3 = { 0x77, 0x2A, 0xF1, 0x48, 0xBE, 0x05, 0xC3, 0x91 };

        private static byte[] _master;

        /// <summary>master key = SHA-256(seg0‖seg1‖seg2‖seg3)。</summary>
        public static byte[] MasterKey
        {
            get
            {
                if (_master != null) return _master;
                var buf = new byte[Seg0.Length + Seg1.Length + Seg2.Length + Seg3.Length];
                int o = 0;
                Buffer.BlockCopy(Seg0, 0, buf, o, Seg0.Length); o += Seg0.Length;
                Buffer.BlockCopy(Seg1, 0, buf, o, Seg1.Length); o += Seg1.Length;
                Buffer.BlockCopy(Seg2, 0, buf, o, Seg2.Length); o += Seg2.Length;
                Buffer.BlockCopy(Seg3, 0, buf, o, Seg3.Length);
                using (var sha = SHA256.Create()) _master = sha.ComputeHash(buf);
                return _master;
            }
        }

        /// <summary>HKDF-SHA256(RFC 5869),<paramref name="length"/> ≤ 32 —— 單一 expand 區塊就夠,
        /// 兩邊(C# / Python)都只要幾行 HMAC。</summary>
        public static byte[] Hkdf(byte[] ikm, byte[] salt, string info, int length)
        {
            if (length <= 0 || length > 32) throw new ArgumentOutOfRangeException("length", "只支援 1..32");

            byte[] prk;
            using (var h = new HMACSHA256(salt ?? new byte[32])) prk = h.ComputeHash(ikm ?? new byte[0]);

            var infoBytes = Encoding.UTF8.GetBytes(info ?? "");
            var t = new byte[infoBytes.Length + 1];
            Buffer.BlockCopy(infoBytes, 0, t, 0, infoBytes.Length);
            t[infoBytes.Length] = 0x01;

            byte[] okm;
            using (var h = new HMACSHA256(prk)) okm = h.ComputeHash(t);

            var outBuf = new byte[length];
            Buffer.BlockCopy(okm, 0, outBuf, 0, length);
            return outBuf;
        }

        /// <summary>某一卷的資料區金鑰(AES-128 → 16 bytes)。</summary>
        public static byte[] DataKey(uint pakId) { return Hkdf(MasterKey, Salt, InfoData + pakId, 16); }

        /// <summary>某一卷的索引金鑰(AES-128 → 16 bytes)。</summary>
        public static byte[] IndexKey(uint pakId) { return Hkdf(MasterKey, Salt, InfoIndex + pakId, 16); }

        /// <summary>某一卷的索引 MAC 金鑰(HMAC-SHA256 → 32 bytes)。</summary>
        public static byte[] MacKey(uint pakId) { return Hkdf(MasterKey, Salt, InfoMac + pakId, 32); }

        /// <summary>AES-128-CTR:把 <paramref name="buf"/> 的一段就地 XOR 金鑰流。
        /// 加密與解密是同一個操作。
        ///
        /// <paramref name="streamPos"/> 是這段資料在**整個金鑰流**裡的位移(資料區用「相對資料區起點的
        /// 絕對位移」,索引區從 0 起算)。這個參數就是不重用 counter 的保證,傳錯等於把加密整個作廢。</summary>
        public static void XorKeystream(byte[] key, byte[] buf, int offset, int count, long streamPos)
        {
            if (key == null || buf == null || count <= 0) return;
            if (offset < 0 || count > buf.Length - offset) throw new ArgumentOutOfRangeException("count");
            if (streamPos < 0) throw new ArgumentOutOfRangeException("streamPos");

            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.Mode = CipherMode.ECB;      // CTR 是自己疊的：ECB 只用來加密 counter block
                aes.Padding = PaddingMode.None;

                using (var enc = aes.CreateEncryptor())
                {
                    var counter = new byte[16];
                    var block = new byte[16];
                    long blockIndex = streamPos / 16;
                    int skip = (int)(streamPos % 16);

                    int i = 0;
                    while (i < count)
                    {
                        FillCounter(counter, blockIndex);
                        enc.TransformBlock(counter, 0, 16, block, 0);
                        for (int k = skip; k < 16 && i < count; k++, i++)
                            buf[offset + i] ^= block[k];
                        skip = 0;
                        blockIndex++;
                    }
                }
            }
        }

        /// <summary>counter block:前 8 bytes 為 0,後 8 bytes 是大端序的區塊序號。
        /// 大端序是刻意的 —— Python 那端 <c>blockIndex.to_bytes(8,'big')</c> 一行就對上。</summary>
        private static void FillCounter(byte[] counter, long blockIndex)
        {
            for (int i = 0; i < 8; i++) counter[i] = 0;
            for (int i = 0; i < 8; i++) counter[15 - i] = (byte)(blockIndex >> (8 * i));
        }

        /// <summary>HMAC-SHA256 取前 16 bytes —— 索引區的完整性標記。
        ///
        /// ⚠️ 金鑰同樣在執行檔裡,所以它只擋「改了檔沒重簽」,擋不住有心人重簽。條目層級的完整性靠 CRC32,
        /// 那是防損毀不是防竄改。</summary>
        public static byte[] IndexMac(uint pakId, byte[] cipherText)
        {
            using (var h = new HMACSHA256(MacKey(pakId)))
            {
                var full = h.ComputeHash(cipherText ?? new byte[0]);
                var mac = new byte[16];
                Buffer.BlockCopy(full, 0, mac, 0, 16);
                return mac;
            }
        }

        /// <summary>定時比較 —— 這裡其實不需要(攻擊者本來就有金鑰),但養成習慣不吃虧。</summary>
        public static bool MacEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
