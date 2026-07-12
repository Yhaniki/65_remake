using System;
using System.Collections.Generic;
using System.Globalization;

namespace Sdo.Osu
{
    /// <summary>
    /// StepMania <c>.sm</c> (simfile) reader → <see cref="OsuBeatmap"/>, so external StepMania songs play through
    /// the same gameplay path as .gn / .osu. Only the 4-panel <c>dance-single</c> charts are used (the game highway
    /// is hardwired to 4K — see ScreenGameplay); other StepsType blocks are ignored.
    ///
    /// Format (verified against assets/SM-YHANIKI-master, StepMania 3.9):
    ///   MSD tags <c>#TAG:v1:v2:...;</c>. Header tags: #TITLE #ARTIST #MUSIC #BANNER #BACKGROUND #CDTITLE #OFFSET #BPMS.
    ///   #NOTES has 6 fields then the note body: StepsType:Description:Difficulty:Meter:Radar:NoteData .
    ///   Note body: measures separated by ',', each measure = N rows (one line each), one char per column
    ///   (dance-single order = Left,Down,Up,Right → lanes 0..3). Char: 0 empty, 1 tap, 2 hold-head, 3 hold/roll-tail,
    ///   4 roll-head (treated as hold), M mine (ignored). Row beat = (measureIndex + row/rowsInMeasure)*4.
    ///   Timing: note seconds = beatToSeconds(beat, #BPMS) − #OFFSET + Σ(stops strictly before the note's beat).
    ///   (StepMania TimingData.GetElapsedTimeFromBeat: subtracts m_fBeat0OffsetInSeconds, then adds every
    ///   #STOPS/#FREEZES whose start beat is < the note's beat — a stop exactly AT the beat comes before the stop,
    ///   so it is NOT added; verified NotesLoaderSM.cpp:141 + TimingData.cpp:162-174.) A stop also freezes the
    ///   highway for its duration — carried on <see cref="OsuBeatmap.Stops"/> and applied by <see cref="ManiaScroll"/>.
    /// See doc/SM_GN_NOTE_FORMAT.md.
    /// </summary>
    public static class SmChart
    {
        public sealed class SmNotes
        {
            public string StepsType = "";
            public string Difficulty = "";
            public int Meter;
            public string NoteData = "";
        }

        public sealed class SmSong
        {
            public string Title = "";
            public string Artist = "";
            public string Music = "";
            public string Banner = "";
            public string Background = "";
            public string CdTitle = "";
            public double Offset;
            public double SampleStart = -1.0;   // #SAMPLESTART 秒；-1 = 未指定 → 試聽退回中段
            public double SampleLength = -1.0;  // #SAMPLELENGTH 秒；-1/0 = 未指定 → 用預設長度
            public readonly List<double> BpmBeats = new List<double>();
            public readonly List<double> BpmValues = new List<double>();
            // #STOPS / #FREEZES (freeze) segments: parallel lists of start beat + freeze seconds.
            public readonly List<double> StopBeats = new List<double>();
            public readonly List<double> StopSeconds = new List<double>();
            public readonly List<SmNotes> Charts = new List<SmNotes>();

            public double FirstBpm => BpmValues.Count > 0 ? BpmValues[0] : 0.0;
        }

        /// <summary>True for a 4-panel single chart (the only kind the 4K highway can play).</summary>
        public static bool IsDanceSingle(SmNotes n)
            => n != null && n.StepsType != null &&
               n.StepsType.Trim().Equals("dance-single", StringComparison.OrdinalIgnoreCase);

        /// <summary>Parse a .sm file's text into a <see cref="SmSong"/> (metadata + raw #NOTES blocks). Pure/testable.</summary>
        public static SmSong Parse(string text)
        {
            var song = new SmSong();
            if (string.IsNullOrEmpty(text)) return song;

            foreach (var stmt in Statements(text))
            {
                // stmt[0] = tag name (already upper-cased, no '#'); stmt[1..] = colon-separated params.
                if (stmt.Length == 0) continue;
                // A tag's value is everything after the first ':' up to ';'. Because Statements() split the whole
                // statement on ':', a value that itself contains ':' (a title, or an "MM:SS" sample time) was split
                // across parts — Rest() rejoins parts[1..] to recover it. NOTES sub-fields (StepsType/Difficulty/Meter)
                // never contain ':', so they keep using Val() (single part).
                switch (stmt[0])
                {
                    case "TITLE":      song.Title = Rest(stmt, 1); break;
                    case "ARTIST":     song.Artist = Rest(stmt, 1); break;
                    case "MUSIC":      song.Music = Rest(stmt, 1); break;
                    case "BANNER":     song.Banner = Rest(stmt, 1); break;
                    case "BACKGROUND": song.Background = Rest(stmt, 1); break;
                    case "CDTITLE":    song.CdTitle = Rest(stmt, 1); break;
                    case "OFFSET":       song.Offset = ParseDouble(Rest(stmt, 1)); break;
                    case "SAMPLESTART":  song.SampleStart = ParseSmTime(Rest(stmt, 1)); break;
                    case "SAMPLELENGTH": song.SampleLength = ParseSmTime(Rest(stmt, 1)); break;
                    case "BPMS":         ParseBpms(Rest(stmt, 1), song); break;
                    // #STOPS and its older alias #FREEZES are the same thing (NotesLoaderSM.cpp:141).
                    case "STOPS":
                    case "FREEZES":      ParseStops(Rest(stmt, 1), song); break;
                    case "NOTES":
                        // NOTES:StepsType:Description:Difficulty:Meter:Radar:NoteData(:Attacks)
                        if (stmt.Length >= 7)
                            song.Charts.Add(new SmNotes
                            {
                                StepsType = Val(stmt, 1),
                                Difficulty = Val(stmt, 3),
                                Meter = ParseInt(Val(stmt, 4)),
                                NoteData = stmt.Length > 6 ? stmt[6] : "",   // keep raw (newlines/commas preserved)
                            });
                        break;
                }
            }
            return song;
        }

        /// <summary>Number of judged objects in a dance-single note body (taps + hold-heads). Pure/testable —
        /// used to rank difficulties by note count (a hold counts once, like an osu! [HitObjects] line).</summary>
        public static int NoteCount(string noteData)
        {
            if (string.IsNullOrEmpty(noteData)) return 0;
            int count = 0;
            foreach (var line in noteData.Replace("\r", "").Split('\n'))
            {
                var row = line.Trim();
                if (row.Length == 0 || row[0] == ',') continue;
                int cols = Math.Min(4, row.Length);
                for (int c = 0; c < cols; c++)
                {
                    char ch = row[c];
                    if (ch == '1' || ch == '2' || ch == '4') count++;   // tap / hold-head / roll-head
                }
            }
            return count;
        }

        /// <summary>Convert one dance-single chart to a playable <see cref="OsuBeatmap"/> (4 lanes).</summary>
        public static OsuBeatmap ToBeatmap(SmSong song, int chartIndex)
        {
            var map = new OsuBeatmap { Keys = 4 };
            if (song == null || chartIndex < 0 || chartIndex >= song.Charts.Count) return map;
            var chart = song.Charts[chartIndex];

            float headerBpm = (float)(song.FirstBpm > 0 ? song.FirstBpm : 120.0);
            map.Bpm = headerBpm;
            map.Level = chart.Meter;
            map.Title = song.Title;
            map.MusicStartOffsetMs = 0.0;   // audio starts at note-clock 0; the OFFSET is folded into each note's ms.

            // Piecewise-constant BPM timeline (reuses the .gn builder — same domain, same assembly).
            GnChart.BuildBpmTimeline(headerBpm, song.BpmBeats, song.BpmValues,
                out double[] segBeat, out double[] segBpm, out double[] segMs);
            double offMs = song.Offset * 1000.0;

            // #STOPS/#FREEZES sorted by beat (freeze ms alongside), so every beat→ms conversion can add the
            // cumulative freeze before it — StepMania folds stops into note times (TimingData::GetElapsedTimeFromBeat).
            BuildStops(song.StopBeats, song.StopSeconds, out double[] stopBeat, out double[] stopMs);

            // timing points (ms): one uninherited point per BPM segment (drives ManiaScroll BPM-change scrolling).
            // Shift by the cumulative freeze at each segment's start beat so a BPM change after a stop lands at its
            // real song time (else the scroll segments would drift by the stop duration).
            for (int s = 0; s < segBeat.Length; s++)
            {
                double t = GnChart.BeatToMs(segBeat, segBpm, segMs, segBeat[s]) - offMs
                           + CumulativeStopMs(stopBeat, stopMs, segBeat[s]);
                map.TimingPoints.Add(new OsuTimingPoint(t, 60000.0 / Math.Max(1.0, segBpm[s])));
            }

            // --- parse the note body: measures split on ',', rows split on '\n' ---
            var openHead = new double[4]; for (int i = 0; i < 4; i++) openHead[i] = -1.0;
            var measures = SplitMeasures(chart.NoteData);
            for (int m = 0; m < measures.Count; m++)
            {
                var rows = measures[m];
                int lpm = rows.Count;
                if (lpm == 0) continue;
                for (int j = 0; j < lpm; j++)
                {
                    var row = rows[j];
                    double beat = (m + (double)j / lpm) * 4.0;
                    int cols = Math.Min(4, row.Length);
                    for (int c = 0; c < cols; c++)
                    {
                        char ch = row[c];
                        if (ch == '1')
                            map.HitObjects.Add(new OsuHitObject(c, Ms(segBeat, segBpm, segMs, stopBeat, stopMs, beat, offMs)));
                        else if (ch == '2' || ch == '4')
                            openHead[c] = beat;
                        else if (ch == '3')
                        {
                            if (openHead[c] >= 0.0)
                            {
                                int start = Ms(segBeat, segBpm, segMs, stopBeat, stopMs, openHead[c], offMs);
                                int end = Ms(segBeat, segBpm, segMs, stopBeat, stopMs, beat, offMs);
                                map.HitObjects.Add(new OsuHitObject(c, start, end > start ? end : (int?)null));
                                openHead[c] = -1.0;
                            }
                        }
                        // '0', 'M' (mine), and anything else → no note.
                    }
                }
            }
            map.HitObjects.Sort((a, b) => a.StartTimeMs.CompareTo(b.StartTimeMs));

            // Freeze windows for the highway (ManiaScroll): each stop begins at its own note-clock time — the
            // cumulative freeze BEFORE it (a stop's own duration is not yet applied at its start beat) — and lasts
            // its freeze duration. A note sitting exactly on the stop beat is hit right as the freeze begins.
            for (int i = 0; i < stopBeat.Length; i++)
            {
                double startMs = GnChart.BeatToMs(segBeat, segBpm, segMs, stopBeat[i]) - offMs
                                 + CumulativeStopMs(stopBeat, stopMs, stopBeat[i]);
                if (startMs < 0) startMs = 0;
                map.Stops.Add(new ScrollStop(startMs, stopMs[i]));
            }
            return map;
        }

        // Cumulative freeze (ms) that applies to a note at <paramref name="beat"/>: the sum of every stop whose
        // start beat is STRICTLY before it. StepMania breaks on `stopBeat >= beat` (TimingData.cpp:171), i.e. a
        // stop exactly on the note's beat comes before the freeze, so it is not counted. stopBeat is ascending.
        private static double CumulativeStopMs(double[] stopBeat, double[] stopMs, double beat)
        {
            double acc = 0.0;
            for (int i = 0; i < stopBeat.Length && stopBeat[i] < beat; i++) acc += stopMs[i];
            return acc;
        }

        // Sort the parsed stops by beat and convert seconds→ms into parallel ascending arrays.
        private static void BuildStops(List<double> beats, List<double> seconds,
            out double[] stopBeat, out double[] stopMs)
        {
            int n = beats?.Count ?? 0;
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = i;
            Array.Sort(idx, (a, b) =>
            {
                int c = beats[a].CompareTo(beats[b]);
                return c != 0 ? c : a.CompareTo(b);
            });
            stopBeat = new double[n];
            stopMs = new double[n];
            for (int i = 0; i < n; i++) { stopBeat[i] = beats[idx[i]]; stopMs[i] = seconds[idx[i]] * 1000.0; }
        }

        private static int Ms(double[] sb, double[] sp, double[] sm, double[] stopBeat, double[] stopMs, double beat, double offMs)
        {
            double ms = GnChart.BeatToMs(sb, sp, sm, beat) - offMs + CumulativeStopMs(stopBeat, stopMs, beat);
            if (ms < 0) ms = 0;
            return (int)Math.Round(ms);
        }

        // ---- MSD tokenizer ----

        // Split a .sm into statements. Strips // line comments, then cuts on ';'. Each statement that begins with
        // '#' is returned as its ':'-separated parts with the tag name upper-cased in [0]. The note body (last part
        // of #NOTES) keeps its newlines/commas since only ':' and ';' are consumed here.
        private static IEnumerable<string[]> Statements(string text)
        {
            var noComments = StripComments(text);
            foreach (var chunk in noComments.Split(';'))
            {
                int hash = chunk.IndexOf('#');
                if (hash < 0) continue;
                var body = chunk.Substring(hash + 1);
                var parts = body.Split(':');
                if (parts.Length == 0) continue;
                parts[0] = parts[0].Trim().ToUpperInvariant();
                yield return parts;
            }
        }

        private static string StripComments(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                int comment = line.IndexOf("//", StringComparison.Ordinal);
                sb.Append(comment >= 0 ? line.Substring(0, comment) : line).Append('\n');
            }
            return sb.ToString();
        }

        private static List<List<string>> SplitMeasures(string noteData)
        {
            var measures = new List<List<string>>();
            var current = new List<string>();
            if (string.IsNullOrEmpty(noteData)) return measures;
            foreach (var raw in noteData.Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line[0] == ',') { measures.Add(current); current = new List<string>(); continue; }
                current.Add(line);
            }
            measures.Add(current);
            return measures;
        }

        private static void ParseBpms(string val, SmSong song)
        {
            if (string.IsNullOrEmpty(val)) return;
            foreach (var pair in val.Split(','))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                if (TryDouble(pair.Substring(0, eq), out double beat) &&
                    TryDouble(pair.Substring(eq + 1), out double bpm) && bpm > 0)
                {
                    song.BpmBeats.Add(beat);
                    song.BpmValues.Add(bpm);
                }
            }
        }

        // #STOPS:beat=seconds,beat=seconds,...  (same grammar as #BPMS). A non-positive freeze is ignored
        // (StepMania keeps 0-length stops but they are a no-op for timing and would only add a dead scroll point).
        private static void ParseStops(string val, SmSong song)
        {
            if (string.IsNullOrEmpty(val)) return;
            foreach (var pair in val.Split(','))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                if (TryDouble(pair.Substring(0, eq), out double beat) &&
                    TryDouble(pair.Substring(eq + 1), out double sec) && sec > 0)
                {
                    song.StopBeats.Add(beat);
                    song.StopSeconds.Add(sec);
                }
            }
        }

        /// <summary>Parse a StepMania time value — plain seconds ("45.0") or colon time ("1:23" / "0:01:23"). −1 if empty/bad.</summary>
        private static double ParseSmTime(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return -1.0;
            if (s.IndexOf(':') >= 0)
            {
                double total = 0.0;
                foreach (var p in s.Split(':'))
                {
                    if (!TryDouble(p, out double v)) return -1.0;
                    total = total * 60.0 + v;
                }
                return total;
            }
            return TryDouble(s, out double d) ? d : -1.0;
        }

        private static string Val(string[] parts, int i) => i < parts.Length ? parts[i].Trim() : "";
        // Rejoin parts[from..] with ':' — recovers a tag value that contained ':' (title / MM:SS sample time).
        private static string Rest(string[] parts, int from)
            => (parts == null || from >= parts.Length) ? "" : string.Join(":", parts, from, parts.Length - from).Trim();
        private static bool TryDouble(string s, out double v)
            => double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        private static double ParseDouble(string s) => TryDouble(s, out var v) ? v : 0.0;
        private static int ParseInt(string s)
            => int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }
}
