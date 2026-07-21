using System;
using System.Collections.Generic;
using System.Text;

namespace Sdo.Osu
{
    /// <summary>
    /// One planned .dps row: a slice of one .mot played over <see cref="Beats"/> beats of the song.
    /// Mirrors the row the game reads back (see Sdo.Game.DpsLoader): motion name + frame range + duration.
    /// </summary>
    public struct RandomDpsRow
    {
        public int StartBeat;    // beats since the FIRST NOTE (row preamble a) — informational, the engine plays rows in order
        public int Beats;        // row length in beats (preamble b)
        public string Mot;       // motion filename, e.g. "wdance0062.mot"
        public int StartFrame;   // mid@244
        public int EndFrame;     // mid@248 (inclusive)
        public float DurSec;     // mid@252 — what actually paces playback
    }

    /// <summary>Inputs for one generated choreography. All timing is relative to the FIRST NOTE.</summary>
    public sealed class RandomDpsRequest
    {
        /// <summary>Song tempo (beats/min) — sets how many beats a motion's frames cover.</summary>
        public double Bpm = 120.0;

        /// <summary>Seconds of dance to fill: first note → last note (the dancer idles outside that span).</summary>
        public double DanceSeconds;

        /// <summary>Motion pool to draw from (e.g. every <c>wdanceNNNN.mot</c> in MOTION/). Only used when
        /// <see cref="Groups"/> is empty — the fallback planner of an index that carries no groups.</summary>
        public IReadOnlyList<string> Pool;

        /// <summary>What the BODY of the dance is assembled from: the official choreographies' three-motion groups
        /// (openings excluded — those are <see cref="Intros"/>), each with the frame slice every row plays. One draw
        /// takes a WHOLE group, replayed verbatim, so the body is real choreography rather than single clips stitched
        /// end to end. Empty → the <see cref="Pool"/> planner.</summary>
        public IReadOnlyList<IntroSlice[]> Groups;

        /// <summary>Openings, one entry per official .dps: every row of its opening — the rows up to (not including)
        /// its FOURTH distinct motion, each with the frame slice it plays. One entry is picked (seeded) and replayed
        /// VERBATIM as this dance's opening, so a generated dance starts the way a real one does instead of jumping
        /// straight into a random clip. May be empty → pure random from beat 0.</summary>
        public IReadOnlyList<IntroSlice[]> Intros;

        /// <summary>Most of the dance span the opening may eat. Official openings run ~40 s (up to 170 s), which on a
        /// short song would leave no room for a single random row — so the opening is truncated at this fraction.</summary>
        public double IntroMaxSpanFraction = 0.5;

        /// <summary>Motion name → its frame count (&gt;= 1). Missing/unreadable motions should fall back to a default.</summary>
        public Func<string, int> FrameCount;

        /// <summary>RNG seed — derived from the SONG's identity, so the same song always generates the same dance.</summary>
        public uint Seed;

        /// <summary>Motion playback rate the .dps durations assume (the original's dance clock).</summary>
        public double Fps = 30.0;

        /// <summary>Rows are quantised to whole multiples of this many beats (motions too short for one unit are
        /// skipped). 8 = the generator's default (two 4/4 bars).</summary>
        public int BeatUnit = 8;

        /// <summary>Name written into the header's chart field (informational; must not contain ".mot").</summary>
        public string ChartName = "ext.gn";
    }

    /// <summary>
    /// Generates a random-but-deterministic .dps choreography for a song that has none — the external (osu/StepMania)
    /// songs of the user <c>Songs/</c> library, which ship no dance. Two stages:
    ///
    ///   1. THE OPENING — one official dance's opening (<see cref="RandomDpsRequest.Intros"/>: every row up to its
    ///      fourth distinct motion) replayed VERBATIM, row by row, frame slice by frame slice. These rows are not
    ///      quantised and never dropped; they are only truncated against the song's own span. Replaying the slices (and
    ///      not just the motion NAMES) is what makes the opening play as choreography: official rows chain through one
    ///      clip (…0-109, 110-160…), so re-starting each row at frame 0 would replay the clip from the top and snap.
    ///
    ///   2. THE REST — more official choreography, a THREE-MOTION GROUP at a time
    ///      (<see cref="RandomDpsRequest.Groups"/>): one draw takes a whole group and replays its rows verbatim, the
    ///      same way the opening does, until the song is full; the last row is truncated to land exactly on the last
    ///      note. Groups are drawn with the seam in mind — a draw that would re-enter the clip just played at a frame
    ///      that doesn't continue it walks on to the next group, since re-entering a clip snaps instead of blending.
    ///      With no groups (a V2 index) this falls back to <c>tools/bms_sdo/random_dps.py</c>'s mot-slice planner,
    ///      verbatim: pick a random motion from the pool, play it from frame 0 for as many whole
    ///      <see cref="RandomDpsRequest.BeatUnit"/>-beat units as it can cover, discard the rest of the clip, pick
    ///      another; a motion too short for one unit places nothing and is re-rolled, and a tail row covers the
    ///      leftover beats.
    ///
    /// The dance is anchored on the FIRST NOTE (the span is first→last note — the engine's dance clock already starts
    /// there), not on the music-start marker.
    ///
    /// Deterministic: same <see cref="RandomDpsRequest.Seed"/> + same pool ⇒ byte-identical output (own xorshift RNG,
    /// never <c>System.Random</c>, whose sequence is not contractual). Pure — no file I/O; the caller supplies the
    /// pool, the openings and the frame counts.
    /// </summary>
    public static class RandomDps
    {
        public const int RowStride = 317;    // 12 preamble + 16 name + 289 mid — one of the two strides DpsLoader accepts
        private const int MidSize = RowStride - 28;
        private const int NameSize = 16;
        private const int HeaderChartField = 256;
        private const int DefaultFrames = 240;   // 8 s at 30 fps — what an unreadable motion is assumed to be

        /// <summary>Plan the rows of one dance. Empty when the span is too short for a single row.</summary>
        public static List<RandomDpsRow> Plan(RandomDpsRequest req)
        {
            var rows = new List<RandomDpsRow>();
            if (req == null || req.Pool == null || req.Pool.Count == 0) return rows;
            double bpm = req.Bpm > 0 ? req.Bpm : 120.0;
            double fps = req.Fps > 0 ? req.Fps : 30.0;
            int unit = Math.Max(1, req.BeatUnit);
            int endBeat = (int)Math.Floor(Math.Max(0.0, req.DanceSeconds) * bpm / 60.0);
            if (endBeat < unit) return rows;

            var rng = new Rng(req.Seed);
            int beat = 0;          // beats placed so far (row preamble; the engine never reads it — it paces on DurSec)
            double cumSec = 0.0;   // seconds placed so far = Σ DurSec; always a whole number of frames

            // The dance's whole length in FRAMES — what every verbatim row is truncated against. Rows are whole frames
            // and beats are re-derived from cumSec, so measuring what is left in beats would leave (or overshoot by) up
            // to half a beat of the song; measuring in frames lands the last row exactly on the last note.
            int endFrames = (int)Math.Round(endBeat * 60.0 / bpm * fps);

            // (1) THE OPENING — one official dance's opening rows, replayed verbatim: each row is that dance's own
            // frame slice of its own clip, so the clips chain exactly as they do in the original. Not quantised and
            // never dropped (that is the whole point of having an opening); only truncated so it can't outrun the song
            // — or, on a short song, eat more than IntroMaxSpanFraction of it and leave no room for a body row.
            int quota = FramesLeft(0.0, (int)(endBeat * req.IntroMaxSpanFraction), bpm, fps);
            PlaySlices(rows, req, PickIntro(req.Intros, ref rng), ref beat, ref cumSec, quota, endBeat, endFrames, bpm, fps);

            // (2) THE REST — official choreography too, a whole three-motion group per draw, replayed verbatim exactly
            // like the opening. No quota: the group's rows are truncated against the song alone, so the last one lands
            // on the last note. A draw that places nothing means the song is full (or the groups are unusable) → stop.
            if (req.Groups != null && req.Groups.Count > 0)
            {
                while (Placed(cumSec, fps) < endFrames)
                {
                    var group = PickGroup(req.Groups, rows, ref rng);
                    if (PlaySlices(rows, req, group, ref beat, ref cumSec, int.MaxValue, endBeat, endFrames, bpm, fps) == 0) break;
                }
                return rows;
            }

            // (2b) NO GROUPS (a V2 index) — random_dps.py's mot-slice planner, verbatim.
            int emptyPicks = 0;
            int emptyLimit = Math.Max(1, req.Pool.Count * 2);
            while (beat + unit <= endBeat)
            {
                string mot = req.Pool[rng.Next(req.Pool.Count)];
                int frames = Frames(req, mot);

                // One row per motion, played from its first frame: as many whole beat-units as the motion (and the
                // song's remainder) can cover. A motion shorter than one unit places nothing and is dropped.
                double motBeats = frames / fps * bpm / 60.0;
                int rowBeats = Quantise(Math.Min(endBeat - beat, motBeats), unit);
                if (rowBeats < unit)
                {
                    if (++emptyPicks >= emptyLimit) break;   // pool is all too short for a unit → stop planning
                    continue;
                }
                emptyPicks = 0;

                int rowFrames = FramesFor(cumSec, rowBeats * 60.0 / bpm, fps);
                rowFrames = Math.Min(rowFrames, frames);
                rows.Add(Row(beat, rowBeats, mot, 0, rowFrames, fps));
                cumSec += rowFrames / fps;
                beat += rowBeats;
            }

            AppendTail(rows, req, beat, endBeat, cumSec, bpm, fps, ref rng);
            return rows;
        }

        /// <summary>Plan + serialize: the bytes of a PAS00003 .dps file. Empty array when nothing could be planned.</summary>
        public static byte[] Build(RandomDpsRequest req)
        {
            var rows = Plan(req);
            return rows.Count == 0 ? new byte[0] : Serialize(rows, req?.ChartName ?? "ext.gn");
        }

        /// <summary>FNV-1a 32-bit — the seed derivation (a song's identity string → its dance).</summary>
        public static uint Fnv(string s)
        {
            uint h = 2166136261u;
            if (s != null) foreach (char ch in s) { h ^= (byte)ch; h *= 16777619u; }
            return h;
        }

        // ---- planning helpers ----

        /// <summary>Replay one official entry (an opening or a body group) row by row, verbatim: every row keeps its
        /// own clip and frame slice, so the clips chain exactly as they do in the original. Rows are truncated against
        /// the song's remaining span (and, for the opening, against <paramref name="quota"/>); planning stops as soon
        /// as one no longer fits. Returns how many rows were placed — 0 means the song is full.</summary>
        private static int PlaySlices(List<RandomDpsRow> rows, RandomDpsRequest req, IEnumerable<IntroSlice> slices,
                                      ref int beat, ref double cumSec, int quota, int endBeat, int endFrames,
                                      double bpm, double fps)
        {
            if (slices == null) return 0;
            int placed = 0;
            foreach (var slice in slices)
            {
                if (string.IsNullOrEmpty(slice.Mot)) continue;

                // With a slice, its length IS the row (never clamped to FrameCount: a stale index would truncate a
                // perfectly good 284..410 slice down to the fallback length). Without one (V1 index) → the whole clip.
                int frames = slice.HasRange ? slice.Frames : Frames(req, slice.Mot);
                frames = Math.Min(frames, Math.Min(quota, endFrames - Placed(cumSec, fps)));
                if (frames <= 0) break;   // no room left in the song (or in the opening's quota)

                double sec = frames / fps;
                // Beats follow the seconds actually placed (not the other way round): re-deriving the cursor from
                // cumSec keeps the sub-beat remainder of each row from accumulating into an over-long dance.
                int rowBeats = Math.Min(endBeat, (int)Math.Round((cumSec + sec) * bpm / 60.0)) - beat;
                rows.Add(Row(beat, rowBeats, slice.Mot, slice.StartF, frames, fps));
                beat += rowBeats;
                cumSec += sec;
                quota -= frames;
                placed++;
            }
            return placed;
        }

        // One draw, then the seam: a group that opens on the clip just played, at a frame that doesn't continue it,
        // would re-enter the SAME clip — ResolveMot hands back the one MotLoader instance, so nothing blends and the
        // dancer snaps back mid-pose. Walk on from the draw to the first group that doesn't (one RNG draw either way,
        // and the walk only ever runs on a collision). Every group colliding (a one-group index) → take the draw.
        private static IntroSlice[] PickGroup(IReadOnlyList<IntroSlice[]> groups, List<RandomDpsRow> rows, ref Rng rng)
        {
            int start = rng.Next(groups.Count);
            for (int i = 0; i < groups.Count; i++)
            {
                var pick = groups[(start + i) % groups.Count];
                if (pick != null && pick.Length > 0 && !ReEntersLastClip(pick[0], rows)) return pick;
            }
            return groups[start];
        }

        private static bool ReEntersLastClip(IntroSlice next, List<RandomDpsRow> rows)
        {
            if (rows.Count == 0) return false;
            var last = rows[rows.Count - 1];
            return string.Equals(next.Mot, last.Mot, StringComparison.OrdinalIgnoreCase)
                   && next.StartF != last.EndFrame + 1;   // resuming the clip where it stopped is exactly what we want
        }

        // The remaining beats never fill a whole unit, so the last row takes them as-is: prefer a motion long enough to
        // cover them outright (so it isn't cut mid-phrase), else play whatever the picked one has.
        private static void AppendTail(List<RandomDpsRow> rows, RandomDpsRequest req, int beat, int endBeat,
                                       double cumSec, double bpm, double fps, ref Rng rng)
        {
            int beats = endBeat - beat;
            if (beats <= 0) return;

            int want = FramesFor(cumSec, beats * 60.0 / bpm, fps);
            string mot = null;
            int rowFrames = want;
            for (int i = 0; i < Math.Max(1, req.Pool.Count * 2); i++)
            {
                string c = req.Pool[rng.Next(req.Pool.Count)];
                if (Frames(req, c) < want) continue;
                mot = c;
                break;
            }
            if (mot == null)
            {
                mot = req.Pool[rng.Next(req.Pool.Count)];
                rowFrames = Math.Min(want, Frames(req, mot));
            }
            rows.Add(Row(beat, beats, mot, 0, rowFrames, fps));
        }

        /// <summary>The one place a row is emitted (opening / main loop / tail all go through it): <paramref name="frames"/>
        /// frames of <paramref name="mot"/> starting at <paramref name="startFrame"/>, paced at the motion's own rate.</summary>
        private static RandomDpsRow Row(int beat, int beats, string mot, int startFrame, int frames, double fps)
        {
            frames = Math.Max(1, frames);
            return new RandomDpsRow
            {
                StartBeat = beat,
                Beats = beats,
                Mot = mot,
                StartFrame = startFrame,
                EndFrame = startFrame + frames - 1,
                DurSec = (float)(frames / fps),
            };
        }

        // Row length in FRAMES for a row that should last rowSec, as random_dps.py computes it (banker's rounding, like
        // python's round()). cumSec is always a whole number of frames, so this is round(rowSec*fps) — each row rounds
        // independently, exactly as the tool does; do not "improve" it or the output stops matching.
        private static int FramesFor(double cumSec, double rowSec, double fps)
        {
            int end = (int)Math.Round((cumSec + rowSec) * fps);
            int start = (int)Math.Round(cumSec * fps);
            return Math.Max(1, end - start);
        }

        /// <summary>Frames still available before <paramref name="beatsLeft"/> beats of the song run out — what the
        /// opening's rows are truncated against.</summary>
        private static int FramesLeft(double cumSec, int beatsLeft, double bpm, double fps)
        {
            if (beatsLeft <= 0) return 0;
            int end = (int)Math.Round((cumSec + beatsLeft * 60.0 / bpm) * fps);
            int start = (int)Math.Round(cumSec * fps);
            return Math.Max(0, end - start);
        }

        /// <summary>Whole frames placed so far (cumSec is always a whole number of frames, so this is exact).</summary>
        private static int Placed(double cumSec, double fps) => (int)Math.Round(cumSec * fps);

        private static int Quantise(double beats, int unit) => (int)Math.Floor(beats + 1e-9) / unit * unit;

        private static int Frames(RandomDpsRequest req, string mot)
        {
            int f = req.FrameCount != null ? req.FrameCount(mot) : DefaultFrames;
            return f > 0 ? f : DefaultFrames;
        }

        // One official dance's opening, whole. No RNG draw when there are no openings — a pool-only plan then matches
        // the python tool's output exactly.
        private static List<IntroSlice> PickIntro(IReadOnlyList<IntroSlice[]> intros, ref Rng rng)
        {
            var list = new List<IntroSlice>();
            if (intros == null || intros.Count == 0) return list;
            var pick = intros[rng.Next(intros.Count)];
            if (pick != null)
                foreach (var s in pick)
                    if (!string.IsNullOrEmpty(s.Mot)) list.Add(s);
            return list;
        }

        // ---- serialization (PAS00003) ----

        // Header: magic(8) type(4) chartName(256) −1(4) rowCount(4); then rows of RowStride bytes:
        // preamble(12: startBeat, beats, 1) + name(16, NUL-padded) + mid(289, zero but for the three fields the
        // engine reads: startFrame@244, endFrame@248, durSec@252).
        private static byte[] Serialize(List<RandomDpsRow> rows, string chartName)
        {
            int header = 8 + 4 + HeaderChartField + 4 + 4;
            var buf = new byte[header + rows.Count * RowStride];
            int o = 0;
            o += Write(buf, o, Encoding.ASCII.GetBytes("PAS00003"));
            o += WriteI32(buf, o, 4);                                  // dps type, as the python generator writes
            o += Write(buf, o, Ascii(SafeChartName(chartName), HeaderChartField));
            o += WriteI32(buf, o, -1);                                 // 0xFFFFFFFF tail, as in the original files
            o += WriteI32(buf, o, rows.Count);

            foreach (var r in rows)
            {
                int rs = o;
                WriteI32(buf, rs, r.StartBeat);
                WriteI32(buf, rs + 4, r.Beats);
                WriteI32(buf, rs + 8, 1);
                Write(buf, rs + 12, Ascii(r.Mot, NameSize));
                int mid = rs + 28;
                WriteI32(buf, mid + 244, r.StartFrame);
                WriteI32(buf, mid + 248, r.EndFrame);
                Write(buf, mid + 252, BitConverter.GetBytes(r.DurSec));
                o += RowStride;
            }
            return buf;
        }

        // The loader finds rows by scanning the WHOLE file for ".mot", so a chart name carrying that substring would be
        // mistaken for the first row and throw the stride check off every real row after it.
        private static string SafeChartName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "ext.gn";
            var s = name.Replace(".mot", "_mot").Replace(".MOT", "_MOT");
            return s.Length > HeaderChartField - 1 ? s.Substring(0, HeaderChartField - 1) : s;
        }

        private static byte[] Ascii(string s, int size)
        {
            var b = new byte[size];
            if (string.IsNullOrEmpty(s)) return b;
            var a = Encoding.ASCII.GetBytes(s);
            Array.Copy(a, b, Math.Min(a.Length, size - 1));   // always NUL-terminated
            return b;
        }

        private static int Write(byte[] dst, int at, byte[] src) { Array.Copy(src, 0, dst, at, src.Length); return src.Length; }
        private static int WriteI32(byte[] dst, int at, int v) { return Write(dst, at, BitConverter.GetBytes(v)); }

        /// <summary>xorshift32 — a tiny, fixed, platform-independent PRNG so a song's dance is reproducible forever
        /// (System.Random's sequence is an implementation detail and may change between runtimes).</summary>
        private struct Rng
        {
            private uint _s;
            public Rng(uint seed) { _s = seed != 0 ? seed : 2463534242u; }

            public uint NextU()
            {
                _s ^= _s << 13;
                _s ^= _s >> 17;
                _s ^= _s << 5;
                return _s;
            }

            /// <summary>Uniform in [0, n).</summary>
            public int Next(int n) => n <= 1 ? 0 : (int)(NextU() % (uint)n);
        }
    }
}
