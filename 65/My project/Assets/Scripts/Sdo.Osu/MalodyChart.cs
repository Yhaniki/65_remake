using System;
using System.Collections.Generic;

namespace Sdo.Osu
{
    /// <summary>
    /// Malody <c>.mc</c> (JSON) reader → <see cref="OsuBeatmap"/>, so external Malody songs play through the same
    /// gameplay path as .gn / .osu / .sm. Only 4-key charts are used (mode 0 = Key, mode_ext.column == 4); the game
    /// highway is hardwired to 4K. One .mc file is ONE difficulty (unlike .sm), so several .mc sharing a folder + audio
    /// are the difficulties of one song (grouped by audio, see <see cref="ExternalSongGrouper.GroupMalody"/>).
    ///
    /// Format (verified against real 4K charts, $ver 0):
    ///   { "meta": { "mode":0, "creator", "background", "version"(=difficulty name), "preview"(ms into audio),
    ///               "song": { "title", "artist" }, "mode_ext": { "column":4 } },
    ///     "time": [ { "beat":[bar,num,den], "bpm":160.0 }, ... ],   // BPM timeline (beat = bar + num/den, in beats)
    ///     "note": [ { "beat":[b,n,d], "column":0..3 },              // tap
    ///               { "beat":[b,n,d], "column":0..3, "endbeat":[b,n,d] }, // long note (hold)
    ///               { "beat":[0,0,1], "sound":"song.ogg", "vol":100, "offset":359, "type":1 } ], // bgm track (last)
    ///     "effect": [ ... ] }   // scroll-speed track — ignored (rare in 4K; a wrong reading is worse than constant scroll)
    ///
    /// Timing: a beat array [a,b,c] is the position a + b/c BEATS (quarter notes). The single bgm note (the one carrying
    /// a "sound") gives the audio file and its <c>offset</c> in ms. The offset is SUBTRACTED from every note's beat-time,
    /// exactly like StepMania's #OFFSET (empirically: aligning this folder's notes onto audio onsets peaks at −offset,
    /// not +offset) — so noteMs = beatToMs(beat) − offset. beat→ms reuses the .gn piecewise-BPM builder (same domain,
    /// same assembly). No mines / stops exist in Malody key mode.
    /// </summary>
    public static class MalodyChart
    {
        /// <summary>One playable note (or hold). Beat/EndBeat are in BEATS (quarter notes).</summary>
        public struct McNote
        {
            public int Column;
            public double Beat;
            public double EndBeat;
            public bool IsHold;
        }

        /// <summary>Parsed .mc: metadata + BPM timeline + the playable notes (the bgm/sound note is split off into
        /// <see cref="AudioFile"/>/<see cref="AudioOffsetMs"/>, not kept in <see cref="Notes"/>).</summary>
        public sealed class McSong
        {
            public int Mode = -1;          // 0 = Key
            public int Column;             // mode_ext.column (4 = 4K)
            public string Title = "";
            public string Artist = "";
            public string Creator = "";
            public string Version = "";    // difficulty name ("4K Another Lv.28")
            public string Background = "";
            public string AudioFile = "";  // from the bgm sound note
            public int PreviewMs = -1;     // meta.preview (ms into the audio); -1 = none
            public double AudioOffsetMs;   // bgm note "offset" (ms); SUBTRACTED from beat times
            public readonly List<double> BpmBeats = new List<double>();
            public readonly List<double> BpmValues = new List<double>();
            public readonly List<McNote> Notes = new List<McNote>();

            public double FirstBpm => BpmValues.Count > 0 ? BpmValues[0] : 0.0;
        }

        /// <summary>True for a 4-key Key-mode chart (the only kind the 4K highway can play).</summary>
        public static bool Is4KKey(McSong s) => s != null && s.Mode == 0 && s.Column == 4;

        /// <summary>Beat array [a,b,c] → a + b/c beats (b/c is the sub-beat fraction). Malformed → 0.</summary>
        public static double BeatOf(object beatArray)
        {
            var a = MiniJson.AsArray(beatArray);
            if (a == null || a.Count < 3) return 0.0;
            double bar = a[0] is double x ? x : 0.0;
            double num = a[1] is double y ? y : 0.0;
            double den = a[2] is double z ? z : 1.0;
            if (den == 0.0) return bar;   // 分母壞掉（0）→ 忽略小數部分，音符落在整拍上（別憑空多推一整拍）
            return bar + num / den;
        }

        /// <summary>Parse a .mc file's text into an <see cref="McSong"/> (metadata + notes). Pure/testable; never throws
        /// (a malformed file yields an empty song → the scanner drops it).</summary>
        public static McSong Parse(string text)
        {
            var song = new McSong();
            var root = MiniJson.Parse(text);
            if (root == null) return song;

            var meta = MiniJson.Get(root, "meta");
            song.Mode = MiniJson.GetInt(meta, "mode", -1);
            song.Creator = MiniJson.GetString(meta, "creator");
            song.Background = MiniJson.GetString(meta, "background");
            song.Version = MiniJson.GetString(meta, "version");
            // meta.preview is ms into the audio; some charts omit it → -1.
            song.PreviewMs = MiniJson.Has(meta, "preview") ? MiniJson.GetInt(meta, "preview", -1) : -1;

            var modeExt = MiniJson.Get(meta, "mode_ext");
            song.Column = MiniJson.GetInt(modeExt, "column");

            var songMeta = MiniJson.Get(meta, "song");
            song.Title = MiniJson.GetString(songMeta, "title");
            song.Artist = MiniJson.GetString(songMeta, "artist");

            // BPM timeline: one entry per tempo, "beat" in beats.
            var time = MiniJson.AsArray(MiniJson.Get(root, "time"));
            if (time != null)
                foreach (var t in time)
                {
                    double bpm = MiniJson.GetDouble(t, "bpm");
                    if (bpm <= 0.0) continue;
                    song.BpmBeats.Add(BeatOf(MiniJson.Get(t, "beat")));
                    song.BpmValues.Add(bpm);
                }

            // Notes: playable ones carry "column"; the single "sound" note is the bgm track (audio + offset), not a note.
            var notes = MiniJson.AsArray(MiniJson.Get(root, "note"));
            if (notes != null)
                foreach (var n in notes)
                {
                    if (MiniJson.Has(n, "sound"))
                    {
                        // bgm / sound-effect note. type 1 (or missing) = the background music track.
                        if (song.AudioFile.Length == 0)
                        {
                            song.AudioFile = MiniJson.GetString(n, "sound");
                            song.AudioOffsetMs = MiniJson.GetDouble(n, "offset");
                        }
                        continue;
                    }
                    if (!MiniJson.Has(n, "column")) continue;
                    int col = MiniJson.GetInt(n, "column", -1);
                    if (col < 0) continue;
                    var note = new McNote { Column = col, Beat = BeatOf(MiniJson.Get(n, "beat")) };
                    if (MiniJson.Has(n, "endbeat"))
                    {
                        double end = BeatOf(MiniJson.Get(n, "endbeat"));
                        if (end > note.Beat) { note.EndBeat = end; note.IsHold = true; }
                    }
                    song.Notes.Add(note);
                }

            return song;
        }

        /// <summary>Convert a parsed <see cref="McSong"/> (one 4K chart) to a playable <see cref="OsuBeatmap"/>.</summary>
        public static OsuBeatmap ToBeatmap(McSong song)
        {
            var map = new OsuBeatmap { Keys = 4 };
            if (song == null) return map;

            float headerBpm = (float)(song.FirstBpm > 0 ? song.FirstBpm : 120.0);
            map.Bpm = headerBpm;
            map.Title = song.Title;
            map.Version = song.Version;
            map.MusicStartOffsetMs = 0.0;   // audio starts at note-clock 0; the bgm offset is folded into each note's ms.

            // Piecewise-constant BPM timeline (reuses the .gn builder — same domain, same assembly, as SmChart does).
            GnChart.BuildBpmTimeline(headerBpm, song.BpmBeats, song.BpmValues,
                out double[] segBeat, out double[] segBpm, out double[] segMs);
            double offMs = song.AudioOffsetMs;   // subtracted (StepMania-style), see class doc

            // One timing point per BPM segment (drives ManiaScroll's BPM-change scrolling), shifted by −offMs so a note
            // sitting on a segment start lands on the same clock as the segment.
            for (int s = 0; s < segBeat.Length; s++)
            {
                double t = GnChart.BeatToMs(segBeat, segBpm, segMs, segBeat[s]) - offMs;
                map.TimingPoints.Add(new OsuTimingPoint(t, 60000.0 / Math.Max(1.0, segBpm[s])));
            }

            foreach (var n in song.Notes)
            {
                if (n.Column < 0 || n.Column > 3) continue;   // 4K guard
                int start = Ms(segBeat, segBpm, segMs, n.Beat, offMs);
                if (n.IsHold)
                {
                    int end = Ms(segBeat, segBpm, segMs, n.EndBeat, offMs);
                    map.HitObjects.Add(new OsuHitObject(n.Column, start, end > start ? end : (int?)null));
                }
                else
                {
                    map.HitObjects.Add(new OsuHitObject(n.Column, start));
                }
            }
            map.HitObjects.Sort((a, b) => a.StartTimeMs.CompareTo(b.StartTimeMs));
            return map;
        }

        private static int Ms(double[] sb, double[] sp, double[] sm, double beat, double offMs)
        {
            double ms = GnChart.BeatToMs(sb, sp, sm, beat) - offMs;
            if (ms < 0) ms = 0;
            return (int)Math.Round(ms);
        }
    }
}
