using System;

namespace Sdo.Osu
{
    /// <summary>
    /// The PLAINTEXT 300-byte StepFile header of an SDO <c>.gn</c> chart — read without decrypting anything.
    ///
    /// An SDOM .gn is <c>[456B 資源名前綴][300B 明文表頭][LCG 密文]</c>, and the密文 decrypts to the SAME 300 bytes
    /// again ("重複表頭") followed by the note data. So every number song-select needs — fileId, BPM, the three
    /// difficulty levels, their note counts and durations — is sitting in the clear at the front of the file. The
    /// scanner reads it straight; the LCG seed is only needed later, to play the notes.
    ///
    /// Field offsets: docs/reverse-engineering/SDOM_STEPFILE_HEADER.md. Deliberately does NOT decode the title
    /// (offset 108) — it is GB2312 and this runtime has no cp936 codec (same reason <see cref="GnChart"/> leaves it
    /// blank); titles come from the pack's sdo_pack.tsv (<see cref="SdoPackIndex"/>) or the song catalog.
    /// </summary>
    public struct GnHeader
    {
        public bool Valid;
        /// <summary>Where the plaintext header starts (456 for SDOM, 0 for an already-plain StepFile).</summary>
        public int Offset;
        public int FileId;
        public float Bpm;
        /// <summary>[easy, normal, hard] — the official 1..20-ish level shown as LV.</summary>
        public int[] Levels;
        /// <summary>[easy, normal, hard] LDUR note counts (header statistic).</summary>
        public int[] Notes;
        /// <summary>[easy, normal, hard] play time in seconds.</summary>
        public int[] Durations;

        private static uint U32(byte[] d, int o) => (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
        private static short I16(byte[] d, int o) => (short)(d[o] | (d[o + 1] << 8));

        /// <summary>Read the header out of a .gn's leading bytes (a prefix is enough — 4 KB covers every known file).
        /// <c>Valid=false</c> when this isn't a .gn at all. Pure.</summary>
        public static GnHeader Read(byte[] raw)
        {
            var h = new GnHeader { Levels = new int[3], Notes = new int[3], Durations = new int[3] };
            int off = FindHeaderOffset(raw);
            if (off < 0) return h;
            h.Valid = true;
            h.Offset = off;
            h.FileId = (int)U32(raw, off);
            h.Bpm = BitConverter.ToSingle(BitConverter.GetBytes(U32(raw, off + 16)), 0);
            if (float.IsNaN(h.Bpm) || float.IsInfinity(h.Bpm) || h.Bpm <= 0f || h.Bpm > 100000f) h.Bpm = 0f;
            for (int d = 0; d < 3; d++)
            {
                h.Levels[d] = I16(raw, off + 20 + d * 2);
                h.Notes[d] = (int)U32(raw, off + 40 + d * 4);
                h.Durations[d] = (int)U32(raw, off + 272 + d * 4);
                if (h.Levels[d] < 0 || h.Levels[d] > 999) h.Levels[d] = 0;
                if (h.Notes[d] < 0 || h.Notes[d] > 1000000) h.Notes[d] = 0;
                if (h.Durations[d] < 0 || h.Durations[d] > 100000) h.Durations[d] = 0;
            }
            return h;
        }

        /// <summary>First offset carrying a StepFile header: <c>'gn'\0\0</c> at +4 and <c>address_easy == 300</c> at
        /// +284, with the two following addresses ordered. Unlike <see cref="GnChart"/>'s own scan this does NOT check
        /// <c>address_end</c> against the file length — the caller may only have handed us a PREFIX of the file (and a
        /// raw [NX] container's address_end is a garbage value anyway; see tools/nx/nx_to_gn.py).</summary>
        private static int FindHeaderOffset(byte[] raw, int scanMax = 0x4000)
        {
            if (raw == null || raw.Length < 300) return -1;
            int limit = Math.Min(scanMax, raw.Length - 300);
            for (int off = 0; off <= limit; off++)
            {
                if (raw[off + 4] != (byte)'g' || raw[off + 5] != (byte)'n' || raw[off + 6] != 0 || raw[off + 7] != 0) continue;
                if (U32(raw, off + 284) != 300) continue;
                uint an = U32(raw, off + 288), ah = U32(raw, off + 292);
                if (!(300 <= an && an <= ah)) continue;
                return off;
            }
            return -1;
        }
    }
}
