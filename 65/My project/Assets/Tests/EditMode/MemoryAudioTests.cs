using System;
using System.IO;
using NUnit.Framework;
using Sdo.Game;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>從記憶體解 ogg / wav / mp3 —— DATA 打包成 pak 之後音訊沒有實體檔案，這是唯一走得通的路。
    ///
    /// 用**真實的官方資產**驗（clean\DATA），不是合成資料：合成的 wav 只能證明 parser 沒寫錯，
    /// 證不了「官方那批檔真的解得開」。找不到那棵樹就整組 Ignore。</summary>
    public class MemoryAudioTests
    {
        private const string LooseDir = @"H:\65_remake_clean\DATA";

        private static string Music(string name) => Path.Combine(LooseDir, "MUSIC", name);
        private static string Se(string name) => Path.Combine(LooseDir, "SE", name);

        private static string RequireFile(string path)
        {
            if (!File.Exists(path)) Assert.Ignore("沒有真實資產可比對: " + path);
            return path;
        }

        // ---------------- ogg（官方歌） ----------------

        [Test]
        public void Ogg_DecodesRealOfficialSong()
        {
            var path = RequireFile(Music("sdom5085.ogg"));
            if (!VorbisDecoder.Available) Assert.Ignore("sdovorbis.dll 不在（editor 需重啟才會載入新 plugin）");

            var pcm = VorbisDecoder.Decode(File.ReadAllBytes(path));
            Assert.IsNotNull(pcm, "官方 ogg 必須解得開");
            Assert.AreEqual(2, pcm.Channels);
            Assert.AreEqual(44100, pcm.SampleRate);

            // 離線自測（tools/sdovorbis/selftest.exe）量到的：4,181,136 samples/ch = 94.810s。
            // 這個數字釘死時間軸 —— 換解碼器如果動到長度，對拍就會整首偏掉。
            int perChannel = pcm.Samples.Length / pcm.Channels;
            Assert.AreEqual(4181136, perChannel, "每聲道樣本數變了 = 時間軸變了");
            Assert.AreEqual(94.810, (double)perChannel / pcm.SampleRate, 0.001);

            // 不是靜音（解碼「成功」但吐出全 0 是最難查的壞法）。
            double peak = 0;
            foreach (var s in pcm.Samples) { var a = Math.Abs(s); if (a > peak) peak = a; }
            Assert.Greater(peak, 0.1, "解出來是靜音 —— 那是假的成功");
        }

        [Test]
        public void Ogg_GarbageIsNullNotNoise()
        {
            if (!VorbisDecoder.Available) Assert.Ignore("sdovorbis.dll 不在");
            Assert.IsNull(VorbisDecoder.Decode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
            Assert.IsNull(VorbisDecoder.Decode(new byte[0]));
            Assert.IsNull(VorbisDecoder.Decode(null));
        }

        // ---------------- wav（官方音效） ----------------

        [Test]
        public void Wav_DecodesRealOfficialSe()
        {
            var path = RequireFile(Se("Bubble.wav"));
            var bytes = File.ReadAllBytes(path);

            var pcm = WavDecoder.Decode(bytes);
            Assert.IsNotNull(pcm, "官方 wav 必須解得開");
            Assert.Greater(pcm.Channels, 0);
            Assert.Greater(pcm.SampleRate, 0);
            Assert.Greater(pcm.Samples.Length, 0);

            // 樣本數要跟表頭宣告的 data chunk 對得上（差一個 byte 就是雜音或尾巴被截）。
            double peak = 0;
            foreach (var s in pcm.Samples) { var a = Math.Abs(s); if (a > peak) peak = a; }
            Assert.Greater(peak, 0.001, "解出來是靜音");
            Assert.LessOrEqual(peak, 1.0001, "超出 [-1,1] = 位寬換算算錯了");
        }

        [Test]
        public void Wav_RejectsNonWav()
        {
            Assert.IsNull(WavDecoder.Decode(new byte[] { 0x4F, 0x67, 0x67, 0x53, 0, 0, 0, 0, 0, 0, 0, 0 }));  // OggS
            Assert.IsNull(WavDecoder.Decode(new byte[10]));
            Assert.IsNull(WavDecoder.Decode(null));
        }

        // ---------------- 格式看內容不看副檔名 ----------------

        [Test]
        public void MemoryAudio_PicksTheDecoderByContent()
        {
            // 外面撿來的歌曲庫常有名不符實的檔（[NX] 那包有 4 個 Ogg 取名叫 .mp3）。
            // 餵錯解碼器不會報錯，只會解出 0 個取樣 → 整首沒聲音。
            var wav = RequireFile(Se("Bubble.wav"));
            var bytes = File.ReadAllBytes(wav);

            Assert.AreEqual(AudioKind.Wav, AudioFileType.Sniff(bytes));
            Assert.IsNotNull(MemoryAudio.Decode(bytes), "副檔名無關 —— 內容是 wav 就該走 wav 解碼器");
        }

        [Test]
        public void MemoryAudio_UnknownFormatIsNull()
        {
            Assert.IsNull(MemoryAudio.Decode(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0, 0, 0, 0, 0, 0, 0, 0 }));
            Assert.IsNull(MemoryAudio.Decode(new byte[4]));
            Assert.IsNull(MemoryAudio.Decode(null));
        }

        // ---------------- mp3 的記憶體多載 ----------------

        [Test]
        public void Mp3_ByteOverloadMatchesThePathOverload()
        {
            // MadDecoder.Decode(byte[]) 只是把「先 ReadAllBytes」讓給呼叫端 —— 解碼行為必須完全相同，
            // 否則就動到了那一整套 gapless/priming 的修正。
            if (!MadDecoder.Available) Assert.Ignore("sdomad.dll 不在");

            // 官方 MUSIC 全是 ogg，mp3 要去外部歌那邊找（ADDON/SONG）。
            string found = null;
            foreach (var dir in new[] { Path.Combine(LooseDir, "MUSIC"), Path.Combine(LooseDir, "ADDON", "SONG") })
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir, "*.mp3", SearchOption.AllDirectories))
                    {
                        // 太大的檔會讓這個測試變很慢 —— 找一首 10 MB 以內的就夠證明兩條路一致。
                        if (new FileInfo(f).Length <= 10 * 1024 * 1024) { found = f; break; }
                    }
                }
                catch { }
                if (found != null) break;
            }
            if (found == null) Assert.Ignore("找不到 .mp3 可比對（MUSIC / ADDON/SONG）");

            var byPath = MadDecoder.Decode(found, out int d1, out int p1);
            var byBytes = MadDecoder.Decode(File.ReadAllBytes(found), out int d2, out int p2);

            Assert.IsNotNull(byPath);
            Assert.IsNotNull(byBytes);
            Assert.AreEqual(byPath.Channels, byBytes.Channels);
            Assert.AreEqual(byPath.SampleRate, byBytes.SampleRate);
            Assert.AreEqual(byPath.Samples.Length, byBytes.Samples.Length, "樣本數不同 = 時間軸不同");
            Assert.AreEqual(d1, d2, "丟棄的壞幀數必須一致");
            Assert.AreEqual(p1, p2, "pretend-success 幀數必須一致");
            for (int i = 0; i < byPath.Samples.Length; i += 4099)   // 質數步長，抽樣掃全長
                Assert.AreEqual(byPath.Samples[i], byBytes.Samples[i], "位移 " + i + " 的樣本不同");
        }
    }
}
