using System;
using System.Collections.Generic;

namespace Sdo.Osu
{
    /// <summary>
    /// Loads an official SDO ".gn" chart (decrypt + StepFile parse) into an <see cref="OsuBeatmap"/>
    /// so the existing gameplay code can consume it. Ported + verified from the decompilation:
    ///   decrypt: LCG  state*=0x3D09; plain = cipher - (state>>16)   (recompile/sdo_gn.c)
    ///   StepFile: 300-byte header (bpm@16, addresses@284) + StepFrames per difficulty
    ///   frame_type -> lane: lane = frameType-2 -> 2=Left(0) 3=Down(1) 4=Up(2) 5=Right(3); note_type 2=holdStart 3=holdEnd
    ///   note time: beat = measurement*4 + 4*slot/interval ;  ms = beat*60000/BPM
    ///
    /// Three on-disk encryptions are handled (see doc/GN_加解密說明.md):
    ///   ddrm  : DDRM container ('drmd' magic) — seed1/seed2 live in the header, decrypt in place.
    ///   plain : file already starts with a StepFile ('gn' @4, address_easy==300 @284).
    ///   sdom  : SDOM (Malaysia stand-alone) — a resource-name prefix then an inner StepFile whose
    ///           body is LCG-encrypted. The seed is NOT in the file; it must be supplied. This runtime
    ///           keeps NO UnityEngine dependency (Sdo.Osu has noEngineReferences), so the seed list is
    ///           passed in by the caller from the precomputed key table (tools/gn_keytable.py ->
    ///           StreamingAssets/gn_keytable.json, loaded by Sdo.Game.GnKeyTable). Because the LCG
    ///           keystream depends only on the low 24 bits of state, the whole corpus uses only ~150
    ///           distinct seeds; trying them all and validating the doubled header is microseconds.
    ///   rewu  : 熱舞 Online online charts — the WHOLE file is LCG-encrypted (0x3D09) with no plaintext
    ///           prefix. Same story as sdom (seed not in the file → supplied from the key table); the
    ///           decrypted file IS a StepFile ('gn'\0\0 @4, address_easy==300 @284). Seeds share the
    ///           SDOM candidate pool.
    /// See doc/SM_GN_NOTE_FORMAT.md.
    /// </summary>
    public static class GnChart
    {
        private const uint MagicDdrm = 0x6D726464u; // 'drmd' little-endian
        private const int StepHeader = 300;

        private static uint U32(byte[] d, int o) => (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
        private static short I16(byte[] d, int o) => (short)(d[o] | (d[o + 1] << 8));

        private static void Lcg(uint seed, byte[] buf, int off, int len)
        {
            if (seed == 0) return;
            uint st = seed;
            for (int i = 0; i < len; i++) { st *= 0x3D09u; buf[off + i] = (byte)(buf[off + i] - (byte)(st >> 16)); }
        }

        // lane = stepFrameType - 2 (decompiled 024_note_0048ba80.c:636/2294/2490 all compute `frameType - 2`):
        // 2=Left(0) 3=Down(1) 4=Up(2) 5=Right(3). (Earlier this had 3/4 swapped per a wrong doc table; the exe and
        // SDOM_STEPFILE_HEADER.md agree on the sequential order, so Down/Up notes were rendering reversed.)
        private static int Lane(int frameType) => (frameType >= 2 && frameType <= 5) ? frameType - 2 : -1;

        /// <summary>Legacy entry point: handles DDRM/plain (no SDOM seeds available).</summary>
        public static OsuBeatmap Load(byte[] raw, int difficulty = 0) => Load(raw, difficulty, null);

        /// <summary>
        /// difficulty: 0=easy 1=normal 2=hard. <paramref name="sdomSeeds"/> are candidate LCG seeds
        /// for SDOM files (per-file seed first is fine); ignored for DDRM/plain. Pass
        /// Sdo.Game.GnKeyTable.SeedsFor(gnPath).
        /// </summary>
        public static OsuBeatmap Load(byte[] raw, int difficulty, uint[] sdomSeeds)
        {
            if (raw == null || raw.Length < StepHeader) return new OsuBeatmap();
            if (difficulty < 0) difficulty = 0; if (difficulty > 2) difficulty = 2;

            byte[] body = Decrypt(raw, sdomSeeds);
            if (body == null || body.Length < StepHeader) return new OsuBeatmap();
            return ParseStepFile(body, difficulty);
        }

        /// <summary>Detect the on-disk encryption and return the plaintext StepFile body, or null.</summary>
        private static byte[] Decrypt(byte[] raw, uint[] sdomSeeds)
        {
            // --- DDRM container: seeds in header ---
            if (raw.Length >= 0x54 && U32(raw, 0) == MagicDdrm)
            {
                uint seed1 = U32(raw, 0x0c);
                var block1 = new byte[32]; Array.Copy(raw, 0x20, block1, 0, 32); Lcg(seed1, block1, 0, 32);
                uint seed2 = U32(block1, 4);
                int bodyLen = raw.Length - 0x54;
                var body = new byte[bodyLen]; Array.Copy(raw, 0x54, body, 0, bodyLen); Lcg(seed2, body, 0, bodyLen);
                return body;
            }

            // --- plain: the file IS the StepFile ---
            if (raw.Length >= StepHeader && raw[4] == (byte)'g' && raw[5] == (byte)'n' && U32(raw, 284) == 300)
                return raw;

            // --- SDOM: resource prefix + inner StepFile with LCG-encrypted (doubled-header) body ---
            int off = FindSdomInnerOffset(raw);
            if (off >= 0 && sdomSeeds != null && sdomSeeds.Length > 0)
            {
                int encLen = raw.Length - off - StepHeader;            // encrypted region length
                if (encLen >= StepHeader)
                {
                    var probe = new byte[StepHeader];
                    foreach (var seed in sdomSeeds)
                    {
                        Array.Copy(raw, off + StepHeader, probe, 0, StepHeader);
                        Lcg(seed, probe, 0, StepHeader);
                        if (HeaderMatches(probe, raw, off))            // decrypted header == plaintext doubled header
                        {
                            var body = new byte[encLen];
                            Array.Copy(raw, off + StepHeader, body, 0, encLen);
                            Lcg(seed, body, 0, encLen);
                            return body;
                        }
                    }
                }
                // fall through to rewu on no SDOM match: a random cipher can spuriously satisfy
                // FindSdomInnerOffset, so never short-circuit to null here.
            }

            // --- rewu: 熱舞 Online whole-file LCG. The ENTIRE file is LCG-encrypted with no plaintext
            // resource prefix, so FindSdomInnerOffset misses it. Decrypt from offset 0 with each candidate
            // seed; the correct one yields a StepFile ('gn'\0\0 @4, address_easy==300 @284). Probe the
            // 300-byte header first (cheap) before decrypting the whole file. Seeds come from the same
            // pool as SDOM (GnKeyTable now includes rewu seeds). ---
            if (sdomSeeds != null && sdomSeeds.Length > 0 && raw.Length >= StepHeader)
            {
                var probe = new byte[StepHeader];
                foreach (var seed in sdomSeeds)
                {
                    Array.Copy(raw, 0, probe, 0, StepHeader);
                    Lcg(seed, probe, 0, StepHeader);
                    if (probe[4] == (byte)'g' && probe[5] == (byte)'n' && probe[6] == 0 && probe[7] == 0 && U32(probe, 284) == 300)
                    {
                        var body = new byte[raw.Length];
                        Array.Copy(raw, 0, body, 0, raw.Length);
                        Lcg(seed, body, 0, body.Length);
                        return body;
                    }
                }
            }
            return null;
        }

        /// <summary>probe[0..300] == raw[off..off+300] ?</summary>
        private static bool HeaderMatches(byte[] probe, byte[] raw, int off)
        {
            for (int i = 0; i < StepHeader; i++) if (probe[i] != raw[off + i]) return false;
            return true;
        }

        /// <summary>First offset where an inner StepFile starts ('gn'\0\0 @+4, address_easy==300 @+284), or -1.</summary>
        private static int FindSdomInnerOffset(byte[] raw, int scanMax = 0x4000)
        {
            int n = raw.Length;
            if (n < StepHeader + 8) return -1;
            int limit = Math.Min(scanMax, n - StepHeader);
            for (int off = 0; off < limit; off++)
            {
                if (raw[off + 4] != (byte)'g' || raw[off + 5] != (byte)'n' || raw[off + 6] != 0 || raw[off + 7] != 0) continue;
                uint ae = U32(raw, off + 284), an = U32(raw, off + 288), ah = U32(raw, off + 292), aend = U32(raw, off + 296);
                if (ae != 300) continue;
                if (!(300 <= an && an <= ah && ah <= aend && aend <= (uint)(n - off))) continue;
                return off;
            }
            return -1;
        }

        private static OsuBeatmap ParseStepFile(byte[] body, int difficulty)
        {
            int bodyLen = body.Length;
            var map = new OsuBeatmap { Keys = 4 };
            float headerBpm = BitConverter.ToSingle(BitConverter.GetBytes(U32(body, 16)), 0);
            map.Bpm = headerBpm;                          // nominal/initial tempo; BPM changes (type 1) below
            map.Level = I16(body, 20 + difficulty * 2);   // levels[3] @ offset 20 (easy/normal/hard)
            // NOTE: the .gn header carries a song title @108 but it is GB2312 (cp936). This runtime
            // (.NET Standard 2.1 / IL2CPP) has no cp936 codec, so decoding here would only ever produce
            // mojibake. Titles are decoded GB2312->UTF-8 at import time (tools/build_song_catalog.py ->
            // StreamingAssets/song_catalog.json) and looked up by .gn filename via SongCatalog. Title
            // is intentionally left empty on the .gn path; the caller fills it from the catalog.
            uint[] addr = { U32(body, 284), U32(body, 288), U32(body, 292), U32(body, 296) };
            int start = (int)addr[difficulty], end = (int)addr[difficulty + 1];
            if (start < 300 || end > bodyLen || end <= start) { start = 300; end = bodyLen; }

            // --- pass 1: scan StepFrames in BEAT space, collecting BPM-change events (type 1) and
            // raw note events (type 2..5). Times are resolved to ms in pass 2 so multi-BPM charts
            // (≈5% of the corpus, e.g. sdom1220/1305/1432: ÷2/×2 gimmicks around the header BPM) stay
            // synced to the music — previously every note was timed with the single header BPM. ---
            var bpmBeats = new List<double>();
            var bpmVals = new List<double>();
            var noteBeat = new List<double>();
            var noteLane = new List<int>();
            var noteType = new List<int>();
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
                    double beat = meas * 4.0 + 4.0 * i / Math.Max(1, iv);
                    if (ft == 1)
                    {
                        // BPM change: the 4 slot bytes ARE a little-endian float32 — NOT u0/u1/nt.
                        // (e.g. 340.0 = 00 00 AA 43, so its u0 int16 is 0; do NOT gate on u0!=0 here.)
                        float bpm = BitConverter.ToSingle(body, off);
                        if (!float.IsNaN(bpm) && !float.IsInfinity(bpm) && bpm > 1f && bpm < 100000f)
                        { bpmBeats.Add(beat); bpmVals.Add(bpm); }
                        continue;
                    }
                    if (lane < 0) continue;
                    short u0 = I16(body, off);
                    byte nt = body[off + 3];
                    if (u0 == 0) continue;
                    noteBeat.Add(beat); noteLane.Add(lane); noteType.Add(nt);
                }
            }

            // --- build the piecewise-constant BPM timeline (segment start beat / bpm / cumulative ms) ---
            BuildBpmTimeline(headerBpm, bpmBeats, bpmVals, out double[] segBeat, out double[] segBpm, out double[] segMs);

            // scroll/timing points (ms): one uninherited point per BPM segment. Single-BPM charts get a
            // single point at 0; ManiaScroll then runs at a constant base velocity. (.gn has no SV.)
            for (int s = 0; s < segBeat.Length; s++)
                map.TimingPoints.Add(new OsuTimingPoint(segMs[s], 60000.0 / Math.Max(1.0, segBpm[s])));

            // --- pass 2: resolve note beats -> ms via the BPM timeline; pair holds (file order = beat order) ---
            var openHoldMs = new int[4]; for (int i = 0; i < 4; i++) openHoldMs[i] = -1;
            for (int k = 0; k < noteBeat.Count; k++)
            {
                int lane = noteLane[k], nt = noteType[k];
                int ms = (int)Math.Round(BeatToMs(segBeat, segBpm, segMs, noteBeat[k]));
                if (nt == 2) openHoldMs[lane] = ms;                 // hold start
                else if (nt == 3)                                    // hold end
                {
                    if (openHoldMs[lane] >= 0) { map.HitObjects.Add(new OsuHitObject(lane, openHoldMs[lane], ms)); openHoldMs[lane] = -1; }
                    else map.HitObjects.Add(new OsuHitObject(lane, ms));
                }
                else map.HitObjects.Add(new OsuHitObject(lane, ms)); // tap
            }
            map.HitObjects.Sort((a, b) => a.StartTimeMs.CompareTo(b.StartTimeMs));
            return map;
        }

        /// <summary>
        /// Build a piecewise-constant BPM timeline from the header (initial) BPM + type-1 BPM-change
        /// events. Output arrays are parallel and sorted by start beat: <paramref name="segBeat"/> =
        /// segment start (beats), <paramref name="segBpm"/> = its BPM, <paramref name="segMs"/> =
        /// cumulative milliseconds at that start beat. With no events the result is a single segment
        /// {beat 0, headerBpm, 0 ms} — identical to the old single-BPM timing. Pure/testable-ish helper.
        /// </summary>
        internal static void BuildBpmTimeline(float headerBpm, List<double> beats, List<double> bpms,
            out double[] segBeat, out double[] segBpm, out double[] segMs)
        {
            // order events by (beat, original index) so equal-beat events keep the LAST one in file order.
            int n = beats?.Count ?? 0;
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = i;
            Array.Sort(idx, (a, b) =>
            {
                int c = beats[a].CompareTo(beats[b]);
                return c != 0 ? c : a.CompareTo(b);
            });

            var sb = new List<double> { 0.0 };
            var sp = new List<double> { Math.Max(1.0, headerBpm) };   // leading segment = header BPM
            for (int j = 0; j < n; j++)
            {
                double b = beats[idx[j]];
                double v = Math.Max(1.0, bpms[idx[j]]);
                if (b <= 0.0) { sp[0] = v; continue; }                 // event at/before 0 overrides the lead
                if (b == sb[sb.Count - 1]) { sp[sp.Count - 1] = v; }    // same beat as last → keep latest
                else { sb.Add(b); sp.Add(v); }
            }

            segBeat = sb.ToArray();
            segBpm = sp.ToArray();
            segMs = new double[segBeat.Length];
            segMs[0] = 0.0;
            for (int s = 1; s < segBeat.Length; s++)
                segMs[s] = segMs[s - 1] + (segBeat[s] - segBeat[s - 1]) * 60000.0 / segBpm[s - 1];
        }

        /// <summary>Convert a beat position to ms using the piecewise BPM timeline from <see cref="BuildBpmTimeline"/>.</summary>
        internal static double BeatToMs(double[] segBeat, double[] segBpm, double[] segMs, double beat)
        {
            int lo = 0, hi = segBeat.Length - 1, s = 0;
            while (lo <= hi) { int mid = (lo + hi) >> 1; if (segBeat[mid] <= beat) { s = mid; lo = mid + 1; } else hi = mid - 1; }
            return segMs[s] + (beat - segBeat[s]) * 60000.0 / Math.Max(1.0, segBpm[s]);
        }
    }
}
