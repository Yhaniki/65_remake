using System;
using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// Reading a .gn's PLAINTEXT StepFile header — the numbers song-select shows for a native SDO chart, taken
    /// without decrypting anything. Field offsets: docs/reverse-engineering/SDOM_STEPFILE_HEADER.md.
    /// </summary>
    public class GnHeaderTests
    {
        /// <summary>A minimal SDOM .gn: <paramref name="prefix"/> bytes of resource names, then the 300-byte header.</summary>
        internal static byte[] Gn(int prefix = 456, int fileId = 10040, float bpm = 137.7f,
            int[] levels = null, int[] notes = null, int[] durations = null, int bodyBytes = 64)
        {
            levels = levels ?? new[] { 1, 4, 5 };
            notes = notes ?? new[] { 141, 318, 357 };
            durations = durations ?? new[] { 129, 129, 129 };
            var raw = new byte[prefix + 300 + bodyBytes];
            void U32(int o, uint v) { raw[o] = (byte)v; raw[o + 1] = (byte)(v >> 8); raw[o + 2] = (byte)(v >> 16); raw[o + 3] = (byte)(v >> 24); }
            void I16(int o, int v) { raw[o] = (byte)v; raw[o + 1] = (byte)(v >> 8); }

            int h = prefix;
            U32(h, (uint)fileId);
            raw[h + 4] = (byte)'g'; raw[h + 5] = (byte)'n';
            U32(h + 16, BitConverter.ToUInt32(BitConverter.GetBytes(bpm), 0));
            for (int d = 0; d < 3; d++)
            {
                I16(h + 20 + d * 2, levels[d]);
                U32(h + 40 + d * 4, (uint)notes[d]);
                U32(h + 272 + d * 4, (uint)durations[d]);
            }
            // address_easy/normal/hard/end — the three difficulty regions inside the body.
            U32(h + 284, 300);
            U32(h + 288, (uint)(300 + bodyBytes / 3));
            U32(h + 292, (uint)(300 + 2 * bodyBytes / 3));
            U32(h + 296, (uint)(300 + bodyBytes));
            return raw;
        }

        [Test]
        public void ReadsEveryFieldSongSelectNeeds()
        {
            var h = GnHeader.Read(Gn());
            Assert.IsTrue(h.Valid);
            Assert.AreEqual(456, h.Offset);
            Assert.AreEqual(10040, h.FileId);
            Assert.AreEqual(137.7f, h.Bpm, 1e-3f);
            CollectionAssert.AreEqual(new[] { 1, 4, 5 }, h.Levels);
            CollectionAssert.AreEqual(new[] { 141, 318, 357 }, h.Notes);
            CollectionAssert.AreEqual(new[] { 129, 129, 129 }, h.Durations);
        }

        [Test]
        public void FindsAPlainStepFileAtOffsetZero()
        {
            var h = GnHeader.Read(Gn(prefix: 0));
            Assert.IsTrue(h.Valid);
            Assert.AreEqual(0, h.Offset);
            Assert.AreEqual(10040, h.FileId);
        }

        [Test]
        public void ReadsFromAPrefixOfTheFile()
        {
            // The scanner only reads the first 20 KB of each chart; the header must still be found even though
            // address_end then points past the bytes we hold.
            var full = Gn(bodyBytes: 40000);
            var prefix = new byte[900];
            Array.Copy(full, prefix, prefix.Length);
            var h = GnHeader.Read(prefix);
            Assert.IsTrue(h.Valid);
            Assert.AreEqual(10040, h.FileId);
        }

        [Test]
        public void NonChartsAreNotValid()
        {
            Assert.IsFalse(GnHeader.Read(null).Valid);
            Assert.IsFalse(GnHeader.Read(new byte[10]).Valid);
            Assert.IsFalse(GnHeader.Read(new byte[4096]).Valid);   // all zeroes: no 'gn', no address_easy == 300
        }

        [Test]
        public void ImplausibleNumbersAreZeroedNotPropagated()
        {
            // A garbled header must not put a nonsense BPM into the judge windows or a nonsense LV on the row.
            var raw = Gn(bpm: float.NaN, levels: new[] { 9999, 4, 5 }, notes: new[] { -1, 318, 357 });
            var h = GnHeader.Read(raw);
            Assert.IsTrue(h.Valid);
            Assert.AreEqual(0f, h.Bpm);
            Assert.AreEqual(0, h.Levels[0]);
            Assert.AreEqual(0, h.Notes[0]);
            Assert.AreEqual(318, h.Notes[1]);
        }
    }
}
