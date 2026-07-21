using System.IO;

namespace Sdo.Osu
{
    /// <summary>音檔實際的容器格式。<see cref="Unknown"/> = 認不出來（呼叫端就退回副檔名猜）。</summary>
    public enum AudioKind { Unknown = 0, Mp3, Ogg, Wav }

    /// <summary>
    /// 看**內容**判斷音檔格式，不是看副檔名。
    ///
    /// 為什麼需要：外面撿來的歌曲庫裡，副檔名跟內容對不上是常態 —— [NX] 那包 232 個 .mp3 裡就有 4 個
    /// （sdom0158/0225/0439/1186）其實是 Ogg Vorbis 只是取名叫 .mp3。照副檔名派工的話，這些檔會被送進
    /// mp3 解碼器，NLayer 解出 <b>0 個取樣</b>、不丟例外 —— 表現出來就是「這首歌沒有聲音」，而且不會有任何
    /// 錯誤訊息可查。反過來（.ogg 裡面是 mp3）也一樣會靜音。
    ///
    /// 判別用各格式檔頭的 magic，讀 12 bytes 就夠。認不出來時回 <see cref="AudioKind.Unknown"/>，
    /// 讓呼叫端沿用原本的副檔名判斷（別把「沒見過的格式」硬塞進某個解碼器）。
    /// 純函式、不碰 Unity。
    /// </summary>
    public static class AudioFileType
    {
        /// <summary>檔頭要讀幾個 byte 才夠判斷（RIFF 的 "WAVE" 在 offset 8）。</summary>
        public const int HeaderBytes = 12;

        /// <summary>從檔頭 bytes 判斷格式。純函式。</summary>
        public static AudioKind Sniff(byte[] head)
        {
            if (head == null || head.Length < 4) return AudioKind.Unknown;
            if (head[0] == 'O' && head[1] == 'g' && head[2] == 'g' && head[3] == 'S') return AudioKind.Ogg;
            // RIFF....WAVE（中間 4 bytes 是長度）
            if (head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F')
                return head.Length >= 12 && head[8] == 'W' && head[9] == 'A' && head[10] == 'V' && head[11] == 'E'
                     ? AudioKind.Wav : AudioKind.Unknown;
            if (head[0] == 'I' && head[1] == 'D' && head[2] == '3') return AudioKind.Mp3;   // ID3v2 標籤
            // 裸 MPEG frame sync：11 個 1（0xFF + 高 3 bit）。0xFF 0xFF… 之類的垃圾擋掉（layer 0 = 保留值）。
            if (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0 && (head[1] & 0x06) != 0x00) return AudioKind.Mp3;
            return AudioKind.Unknown;
        }

        /// <summary>從副檔名猜（<see cref="Sniff"/> 認不出來時的後備）。</summary>
        public static AudioKind FromExtension(string path)
        {
            var ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            if (ext == ".mp3") return AudioKind.Mp3;
            if (ext == ".ogg") return AudioKind.Ogg;
            if (ext == ".wav") return AudioKind.Wav;
            return AudioKind.Unknown;
        }

        /// <summary>這個檔實際是什麼格式：先看內容，認不出來才退回副檔名。讀不到檔就只看副檔名。</summary>
        public static AudioKind Of(string path)
        {
            if (string.IsNullOrEmpty(path)) return AudioKind.Unknown;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var head = new byte[HeaderBytes];
                    int read = 0;
                    while (read < head.Length)
                    {
                        int n = fs.Read(head, read, head.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < head.Length) System.Array.Resize(ref head, read);
                    var kind = Sniff(head);
                    if (kind != AudioKind.Unknown) return kind;
                }
            }
            catch { /* 讀不到 → 只能靠副檔名 */ }
            return FromExtension(path);
        }

        /// <summary>這個檔要不要走 mp3 解碼器（桌面版 Unity 不解 mp3）。看的是內容。</summary>
        public static bool IsMp3(string path) => Of(path) == AudioKind.Mp3;
    }
}
