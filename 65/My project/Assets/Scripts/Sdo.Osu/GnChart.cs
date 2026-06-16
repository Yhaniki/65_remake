using System;
using System.Collections.Generic;

namespace Sdo.Osu
{
    /// <summary>
    /// Loads an official SDO ".gn" chart (decrypt + StepFile parse) into an <see cref="OsuBeatmap"/>
    /// so the existing gameplay code can consume it. Ported + verified from the decompilation:
    ///   decrypt: LCG  state*=0x3D09; plain = cipher - (state>>16)   (recompile/sdo_gn.c)
    ///   StepFile: 300-byte header (bpm@16, addresses@284) + StepFrames per difficulty
    ///   frame_type -> lane: 2=Left(0) 4=Down(1) 3=Up(2) 5=Right(3); note_type 2=holdStart 3=holdEnd
    ///   note time: beat = measurement*4 + 4*slot/interval ;  ms = beat*60000/BPM
    /// See doc/GN_加解密說明.md and doc/SM_GN_NOTE_FORMAT.md.
    /// </summary>
    public static class GnChart
    {
        private static uint U32(byte[] d, int o) => (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
        private static short I16(byte[] d, int o) => (short)(d[o] | (d[o + 1] << 8));

        private static void Lcg(uint seed, byte[] buf, int off, int len)
        {
            if (seed == 0) return;
            uint st = seed;
            for (int i = 0; i < len; i++) { st *= 0x3D09u; buf[off + i] = (byte)(buf[off + i] - (byte)(st >> 16)); }
        }

        private static int Lane(int frameType)
        {
            switch (frameType) { case 2: return 0; case 4: return 1; case 3: return 2; case 5: return 3; default: return -1; }
        }

        /// <summary>difficulty: 0=easy 1=normal 2=hard.</summary>
        public static OsuBeatmap Load(byte[] raw, int difficulty = 0)
        {
            if (raw == null || raw.Length < 0x54) return new OsuBeatmap();
            if (difficulty < 0) difficulty = 0; if (difficulty > 2) difficulty = 2;

            // --- decrypt ---
            uint seed1 = U32(raw, 0x0c);
            var block1 = new byte[32]; Array.Copy(raw, 0x20, block1, 0, 32); Lcg(seed1, block1, 0, 32);
            uint seed2 = U32(block1, 4);
            int bodyLen = raw.Length - 0x54;
            var body = new byte[bodyLen]; Array.Copy(raw, 0x54, body, 0, bodyLen); Lcg(seed2, body, 0, bodyLen);

            // --- StepFile header ---
            var map = new OsuBeatmap { Keys = 4 };
            map.Bpm = BitConverter.ToSingle(BitConverter.GetBytes(U32(body, 16)), 0);
            map.Level = I16(body, 20 + difficulty * 2);   // levels[3] @ offset 20 (easy/normal/hard)
            // NOTE: the .gn header carries a song title @108 but it is GB2312 (cp936). This runtime
            // (.NET Standard 2.1 / IL2CPP) has no cp936 codec, so decoding here would only ever produce
            // mojibake. Titles are decoded GB2312->UTF-8 at import time (tools/build_song_catalog.py ->
            // StreamingAssets/song_catalog.json) and looked up by .gn filename via SongCatalog. Title
            // is intentionally left empty on the .gn path; the caller fills it from the catalog.
            uint[] addr = { U32(body, 284), U32(body, 288), U32(body, 292), U32(body, 296) };
            int start = (int)addr[difficulty], end = (int)addr[difficulty + 1];
            if (start < 300 || end > bodyLen || end <= start) { start = 300; end = bodyLen; }

            // --- StepFrames ---
            var openHoldMs = new int[4]; for (int i = 0; i < 4; i++) openHoldMs[i] = -1;
            int off = start;
            while (off + 8 <= end)
            {
                int meas = (int)U32(body, off);
                int ft = I16(body, off + 4);
                int iv = (ushort)I16(body, off + 6);
                off += 8;
                int lane = Lane(ft);
                for (int i = 0; i < iv && off + 4 <= end; i++, off += 4)
                {
                    short u0 = I16(body, off);
                    byte nt = body[off + 3];
                    if (lane < 0 || u0 == 0) continue;
                    double beat = meas * 4.0 + 4.0 * i / Math.Max(1, iv);
                    int ms = (int)Math.Round(beat * 60000.0 / Math.Max(1f, map.Bpm));
                    if (nt == 2) openHoldMs[lane] = ms;                 // hold start
                    else if (nt == 3)                                    // hold end
                    {
                        if (openHoldMs[lane] >= 0) { map.HitObjects.Add(new OsuHitObject(lane, openHoldMs[lane], ms)); openHoldMs[lane] = -1; }
                        else map.HitObjects.Add(new OsuHitObject(lane, ms));
                    }
                    else map.HitObjects.Add(new OsuHitObject(lane, ms)); // tap
                }
            }
            map.HitObjects.Sort((a, b) => a.StartTimeMs.CompareTo(b.StartTimeMs));
            return map;
        }
    }
}
