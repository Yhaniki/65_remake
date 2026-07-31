using System.IO;
using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// 用檔頭判斷音檔格式。這條路救的是一個查不出來的錯：副檔名叫 .mp3、內容其實是 Ogg 的檔（[NX] 那包 232 個
    /// .mp3 裡有 4 個是這樣）被送進 mp3 解碼器 —— NLayer 不丟例外，只是解出 0 個取樣，玩起來就是「這首歌沒聲音」。
    /// </summary>
    public class AudioFileTypeTests
    {
        private static byte[] Head(params byte[] b)
        {
            var h = new byte[AudioFileType.HeaderBytes];
            for (int i = 0; i < b.Length && i < h.Length; i++) h[i] = b[i];
            return h;
        }

        private static byte[] Ascii(string s, int pad = AudioFileType.HeaderBytes)
        {
            var h = new byte[pad];
            for (int i = 0; i < s.Length && i < h.Length; i++) h[i] = (byte)s[i];
            return h;
        }

        [Test]
        public void OggIsRecognisedByItsMagic()
            => Assert.AreEqual(AudioKind.Ogg, AudioFileType.Sniff(Ascii("OggS")));

        [Test]
        public void WavNeedsBothRiffAndWave()
        {
            Assert.AreEqual(AudioKind.Wav, AudioFileType.Sniff(Ascii("RIFF____WAVE")));
            // RIFF 也可能是 AVI/其他 —— 沒有 WAVE 就別當成音檔硬解
            Assert.AreEqual(AudioKind.Unknown, AudioFileType.Sniff(Ascii("RIFF____AVI ")));
        }

        [Test]
        public void Mp3IsRecognisedTaggedOrBare()
        {
            Assert.AreEqual(AudioKind.Mp3, AudioFileType.Sniff(Ascii("ID3")), "ID3v2 標籤開頭");
            Assert.AreEqual(AudioKind.Mp3, AudioFileType.Sniff(Head(0xFF, 0xFB)), "裸 MPEG1 Layer3 frame sync");
            Assert.AreEqual(AudioKind.Mp3, AudioFileType.Sniff(Head(0xFF, 0xE3)), "MPEG2.5 也算");
        }

        [Test]
        public void GarbageIsUnknownNotGuessedAsMp3()
        {
            // 0xFF 0xFF 的 layer 欄位是保留值 → 不是 frame sync，別硬當 mp3。
            Assert.AreEqual(AudioKind.Unknown, AudioFileType.Sniff(Head(0xFF, 0xFF)));
            Assert.AreEqual(AudioKind.Unknown, AudioFileType.Sniff(Head(0x00, 0x01, 0x02, 0x03)));
            Assert.AreEqual(AudioKind.Unknown, AudioFileType.Sniff(null));
            Assert.AreEqual(AudioKind.Unknown, AudioFileType.Sniff(new byte[] { 0x4F, 0x67 }));   // 太短
        }

        [Test]
        public void FromExtensionIsTheFallback()
        {
            Assert.AreEqual(AudioKind.Mp3, AudioFileType.FromExtension("a/b/song.MP3"));
            Assert.AreEqual(AudioKind.Ogg, AudioFileType.FromExtension("song.ogg"));
            Assert.AreEqual(AudioKind.Wav, AudioFileType.FromExtension("song.wav"));
            Assert.AreEqual(AudioKind.Unknown, AudioFileType.FromExtension("song.flac"));
        }

        [Test]
        public void ContentBeatsTheExtension()
        {
            // 這就是 sdom0158.mp3 的情形：叫 .mp3，裡面是 Ogg。
            string path = Path.Combine(Path.GetTempPath(), "sdo_liar_" + Path.GetRandomFileName() + ".mp3");
            try
            {
                File.WriteAllBytes(path, Ascii("OggS\0\0\0", 64));
                Assert.AreEqual(AudioKind.Ogg, AudioFileType.Of(path));
                Assert.IsFalse(AudioFileType.IsMp3(path), "餵給 mp3 解碼器只會解出 0 個取樣 → 整首沒聲音");
            }
            finally { try { File.Delete(path); } catch { } }
        }

        [Test]
        public void AnUnreadableOrUnknownFileFallsBackToItsExtension()
        {
            string path = Path.Combine(Path.GetTempPath(), "sdo_missing_" + Path.GetRandomFileName() + ".ogg");
            Assert.AreEqual(AudioKind.Ogg, AudioFileType.Of(path), "檔不在 → 只能靠副檔名");
            Assert.AreEqual(AudioKind.Unknown, AudioFileType.Of(""));
        }
    }
}
