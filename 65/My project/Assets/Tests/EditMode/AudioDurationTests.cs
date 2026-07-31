using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// The 時間 column for user songs = the AUDIO FILE's length (a chart routinely ends before the track does), read
    /// straight from the file headers. Synthetic files here, so the tests need nothing on disk.
    /// </summary>
    public class AudioDurationTests
    {
        // ---- WAV: data-chunk bytes ÷ byte rate ----

        private static byte[] Wav(uint byteRate, uint dataBytes, bool declareDataSize = true)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms, Encoding.ASCII);
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36u + dataBytes);
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(16u);
            w.Write((ushort)1);          // pcm
            w.Write((ushort)2);          // channels
            w.Write(44100u);             // sample rate
            w.Write(byteRate);           // byte rate — the field we read
            w.Write((ushort)4);          // block align
            w.Write((ushort)16);         // bits
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(declareDataSize ? dataBytes : 0u);
            w.Write(new byte[dataBytes]);
            w.Flush();
            ms.Position = 0;
            return ms.ToArray();
        }

        [Test]
        public void Wav_Length_Is_DataSize_Over_ByteRate()
        {
            // 176400 B/s (44.1k stereo 16-bit) × 3s of audio
            var bytes = Wav(176400u, 176400u * 3u);
            Assert.AreEqual(3.0, AudioDuration.WavSeconds(new MemoryStream(bytes)), 1e-6);
        }

        [Test]
        public void Wav_With_Unset_Data_Size_Falls_Back_To_The_Bytes_That_Follow()
        {
            var bytes = Wav(1000u, 2500u, declareDataSize: false);   // streamed wav: data size written as 0
            Assert.AreEqual(2.5, AudioDuration.WavSeconds(new MemoryStream(bytes)), 1e-6);
        }

        [Test]
        public void Wav_Garbage_Is_Zero_Not_A_Throw()
        {
            Assert.AreEqual(0.0, AudioDuration.WavSeconds(new MemoryStream(new byte[] { 1, 2, 3, 4 })));
            Assert.AreEqual(0.0, AudioDuration.WavSeconds(new MemoryStream(Encoding.ASCII.GetBytes("RIFFxxxxNOPE"))));
            Assert.AreEqual(0.0, AudioDuration.WavSeconds(null));
        }

        // ---- OGG: last page's granule position ÷ sample rate ----

        /// <summary>An Ogg page: "OggS", version, flags, granule, serial, seq, crc, 1 segment of <paramref name="body"/>.</summary>
        private static void OggPage(BinaryWriter w, ulong granule, byte[] body)
        {
            w.Write(Encoding.ASCII.GetBytes("OggS"));
            w.Write((byte)0);            // version
            w.Write((byte)0);            // header type
            w.Write(granule);            // granule position — total samples so far
            w.Write(0u);                 // serial
            w.Write(0u);                 // page sequence
            w.Write(0u);                 // crc
            w.Write((byte)1);            // segment count
            w.Write((byte)body.Length);  // segment table
            w.Write(body);
        }

        private static byte[] Ogg(uint sampleRate, ulong lastGranule, bool trailingUnfinishedPage = false)
        {
            var id = new MemoryStream();
            var p = new BinaryWriter(id, Encoding.ASCII);
            p.Write((byte)0x01);                              // vorbis identification packet
            p.Write(Encoding.ASCII.GetBytes("vorbis"));
            p.Write(0u);                                      // vorbis version
            p.Write((byte)2);                                 // channels
            p.Write(sampleRate);
            p.Write(new byte[16]);                            // bitrate/blocksize/framing — unread
            p.Flush();

            var ms = new MemoryStream();
            var w = new BinaryWriter(ms, Encoding.ASCII);
            OggPage(w, 0UL, id.ToArray());                    // first page: the id header
            OggPage(w, lastGranule, new byte[8]);             // audio page carrying the final granule
            if (trailingUnfinishedPage)
                OggPage(w, ulong.MaxValue, new byte[8]);      // granule −1 = no packet ends here → must be skipped
            w.Flush();
            return ms.ToArray();
        }

        [Test]
        public void Ogg_Length_Is_Last_Granule_Over_Sample_Rate()
        {
            var bytes = Ogg(44100u, 44100UL * 125UL);   // 125 s
            Assert.AreEqual(125.0, AudioDuration.OggSeconds(new MemoryStream(bytes)), 1e-6);
        }

        [Test]
        public void Ogg_Skips_A_Final_Page_With_No_Granule()
        {
            var bytes = Ogg(48000u, 48000UL * 90UL, trailingUnfinishedPage: true);
            Assert.AreEqual(90.0, AudioDuration.OggSeconds(new MemoryStream(bytes)), 1e-6);
        }

        [Test]
        public void Ogg_Non_Vorbis_Is_Zero()
        {
            var bytes = Ogg(44100u, 44100UL);
            bytes[28] = 0x05;   // break the packet type of the identification header
            Assert.AreEqual(0.0, AudioDuration.OggSeconds(new MemoryStream(bytes)));
            Assert.AreEqual(0.0, AudioDuration.OggSeconds(new MemoryStream(new byte[80])));
        }

        // ---- Seconds(): dispatch by extension, never throws ----

        [Test]
        public void Seconds_Reads_A_Real_File_And_Rounds()
        {
            string path = Path.Combine(Path.GetTempPath(), "sdo_dur_test.wav");
            try
            {
                File.WriteAllBytes(path, Wav(1000u, 2600u));   // 2.6 s → 3
                Assert.AreEqual(3, AudioDuration.Seconds(path));
            }
            finally { try { File.Delete(path); } catch { } }
        }

        [Test]
        public void Seconds_Missing_Or_Unknown_Is_Zero()
        {
            Assert.AreEqual(0, AudioDuration.Seconds(null));
            Assert.AreEqual(0, AudioDuration.Seconds(""));
            Assert.AreEqual(0, AudioDuration.Seconds(Path.Combine(Path.GetTempPath(), "sdo_no_such_file.ogg")));
        }
    }
}
