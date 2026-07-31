using System;
using System.Globalization;

namespace Sdo.Osu
{
    /// <summary>
    /// Parses a (subset of) osu! mania .osu file: [General], [Difficulty] and [HitObjects].
    /// Step 1 only needs audio filename, key count and the note list (Tap + Hold).
    /// </summary>
    public static class OsuBeatmapParser
    {
        public static OsuBeatmap Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));

            var map = new OsuBeatmap();
            string section = "";

            // osu applies a fixed +24 ms to EVERY time (hit objects, timing points…) when loading a beatmap whose
            // file-format version is < 5 — "to correct timing changes applied at a game client level" (osu!lazer
            // LegacyBeatmapDecoder.EARLY_VERSION_TIMING_OFFSET = 24). Reproduce it so old maps convert in sync; modern
            // maps (the "osu file format v14" the vast majority carry) get 0.
            int timeOffset = EarlyVersionTimingOffset(text);

            foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("//")) continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2);
                    continue;
                }

                switch (section)
                {
                    case "General":
                        ParseKeyValue(line, "AudioFilename", v => map.AudioFilename = v);
                        ParseKeyValue(line, "Mode", v => map.Mode = ParseInt(v));
                        ParseKeyValue(line, "SamplesMatchPlaybackRate",
                            v => map.SamplesMatchPlaybackRate = TryInt(v, 0) != 0);
                        break;
                    case "Metadata":
                        ParseKeyValue(line, "Title", v => map.Title = v);
                        ParseKeyValue(line, "Version", v => map.Version = v);
                        break;
                    case "Difficulty":
                        // CircleSize == key count for mania. OD/HP deliberately ignored.
                        ParseKeyValue(line, "CircleSize", v => map.Keys = (int)Math.Round(ParseDouble(v)));
                        break;
                    case "TimingPoints":
                        // "time,beatLength,meter,...". Uninherited points (beatLength > 0) are tempo
                        // changes; inherited points (beatLength < 0) are osu! SV (green) lines. ALL of
                        // them are kept (for the note scroll); the first uninherited also sets map.Bpm.
                        ParseTimingPoint(line, map, timeOffset);
                        break;
                    case "Events":
                        ParseSampleEvent(line, map, timeOffset);
                        break;
                    case "HitObjects":
                        var ho = ParseHitObject(line, map.Keys, timeOffset);
                        if (ho.HasValue) map.HitObjects.Add(ho.Value);
                        break;
                }
            }

            map.SampleEvents.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
            return map;
        }
        /// <summary>True only for osu!'s reserved silent backing-track name.</summary>
        public static bool IsVirtualAudioFilename(string filename)
        {
            filename = Dequote(filename);
            return string.Equals(filename, "virtual", StringComparison.OrdinalIgnoreCase);
        }


        /// <summary>
        /// Add a timing point (time,beatLength) to <see cref="OsuBeatmap.TimingPoints"/>. The first
        /// uninherited point (beatLength &gt; 0) also sets map.Bpm = 60000/beatLength. Inherited points
        /// keep their negative beatLength (osu! SV encoding); see <see cref="OsuTimingPoint"/>.
        /// </summary>
        /// <summary>osu's early-format timing correction: +24 ms on all times when the file-format version &lt; 5, else 0.
        /// Reads the "osu file format vN" header (first non-empty line). Pure — unit-tested.</summary>
        public const int EarlyVersionOffsetMs = 24;
        public static int EarlyVersionTimingOffset(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int i = text.IndexOf("osu file format v", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return 0;
            i += "osu file format v".Length;
            int j = i;
            while (j < text.Length && char.IsDigit(text[j])) j++;
            return int.TryParse(text.Substring(i, j - i), out int ver) && ver > 0 && ver < 5 ? EarlyVersionOffsetMs : 0;
        }

        private static void ParseTimingPoint(string line, OsuBeatmap map, int timeOffset = 0)
        {
            var p = line.Split(',');
            if (p.Length < 2) return;
            if (!double.TryParse(p[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
                return;
            time += timeOffset;
            if (!double.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var beatLength))
                return;
            int sampleVolume = p.Length > 5 ? TryInt(p[5], 100) : 100;
            sampleVolume = Math.Max(0, Math.Min(100, sampleVolume));
            map.TimingPoints.Add(new OsuTimingPoint(time, beatLength, sampleVolume));
            if (beatLength > 0.0 && map.Bpm <= 0.0) map.Bpm = 60000.0 / beatLength;
        }
        private static void ParseSampleEvent(string line, OsuBeatmap map, int timeOffset)
        {
            var p = SplitCsv(line);
            if (p.Length < 4) return;
            string kind = p[0].Trim();
            if (!string.Equals(kind, "Sample", StringComparison.OrdinalIgnoreCase) && kind != "5") return;
            if (!double.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var time)) return;
            string filename = Dequote(p[3]);
            if (filename.Length == 0) return;
            int layer = TryInt(p[2], 0);
            int volume = p.Length > 4 ? TryInt(p[4], 100) : 100;
            map.SampleEvents.Add(new OsuSampleEvent(time + timeOffset, layer, filename, volume));
        }

        private static string[] SplitCsv(string line)
        {
            var fields = new System.Collections.Generic.List<string>();
            var field = new System.Text.StringBuilder();
            bool quoted = false;
            foreach (char c in line ?? "")
            {
                if (c == '"') { quoted = !quoted; field.Append(c); }
                else if (c == ',' && !quoted) { fields.Add(field.ToString()); field.Length = 0; }
                else field.Append(c);
            }
            fields.Add(field.ToString());
            return fields.ToArray();
        }


        /// <summary>
        /// Lightweight metadata read for the folder scanner: title/artist/creator/version, key count (CircleSize),
        /// audio filename, first-BPM, [Events] background filename, and the [HitObjects] line count — WITHOUT
        /// allocating the full note list. Cheap enough to run over thousands of candidate .osu files at boot.
        /// </summary>
        public static OsuMeta ReadMeta(string text)
        {
            var m = new OsuMeta();
            if (string.IsNullOrEmpty(text)) return m;
            string section = "";
            foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("//")) continue;
                if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2); continue; }
                switch (section)
                {
                    case "General":
                        ParseKeyValue(line, "AudioFilename", v => m.AudioFilename = v);
                        ParseKeyValue(line, "Mode", v => m.Mode = ParseInt(v));
                        ParseKeyValue(line, "PreviewTime", v => m.PreviewTime = ParseInt(v));
                        break;
                    case "Metadata":
                        ParseKeyValue(line, "Title", v => { if (string.IsNullOrEmpty(m.Title)) m.Title = v; });
                        ParseKeyValue(line, "Artist", v => { if (string.IsNullOrEmpty(m.Artist)) m.Artist = v; });
                        ParseKeyValue(line, "Creator", v => m.Creator = v);
                        ParseKeyValue(line, "Version", v => m.Version = v);
                        ParseKeyValue(line, "BeatmapSetID", v => m.BeatmapSetId = TryInt(v, -1));
                        break;
                    case "Difficulty":
                        ParseKeyValue(line, "CircleSize", v => m.Keys = (int)Math.Round(ParseDouble(v)));
                        break;
                    case "TimingPoints":
                        if (m.Bpm <= 0.0)
                        {
                            var p = line.Split(',');
                            if (p.Length >= 2 &&
                                double.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var bl) &&
                                bl > 0.0)
                                m.Bpm = 60000.0 / bl;
                        }
                        break;
                    case "Events":
                        var eventParts = SplitCsv(line);
                        if (eventParts.Length >= 4 &&
                            (eventParts[0].Trim().Equals("Sample", StringComparison.OrdinalIgnoreCase) || eventParts[0].Trim() == "5") &&
                            Dequote(eventParts[3]).Length > 0)
                            m.KeysoundCount++;
                        // First "0,0,\"bg.jpg\",..." line is the background; a leading "1,..." video is a fallback bg.
                        if (string.IsNullOrEmpty(m.BackgroundFilename))
                        {
                            if (eventParts.Length >= 3 && (eventParts[0].Trim() == "0" || eventParts[0].Trim() == "1"))
                                m.BackgroundFilename = Dequote(eventParts[2]);
                        }
                        break;
                    case "HitObjects":
                        m.NoteCount++;   // one object per line (tap or hold)
                        var hitParts = line.Split(new[] { ',' }, 6);
                        if (hitParts.Length > 5)
                        {
                            bool hold = (TryInt(hitParts[3], 0) & 128) != 0;
                            ParseHitSample(hitParts[5], hold, out string sampleName, out _);
                            if (sampleName.Length > 0) m.KeysoundCount++;
                        }
                        break;
                }
            }
            return m;
        }

        /// <summary>Strip surrounding quotes / normalise backslashes from an osu! filename token.</summary>
        private static string Dequote(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"') s = s.Substring(1, s.Length - 2);
            return s.Replace('\\', '/').Trim();
        }

        private static void ParseKeyValue(string line, string key, Action<string> set)
        {
            // osu uses "Key:Value" (General/Metadata/Difficulty have no spaces around ':' guaranteed).
            int idx = line.IndexOf(':');
            if (idx < 0) return;
            var k = line.Substring(0, idx).Trim();
            if (!string.Equals(k, key, StringComparison.Ordinal)) return;
            set(line.Substring(idx + 1).Trim());
        }

        /// <summary>
        /// HitObject line: x,y,time,type,hitSound,objectParams...
        /// mania: lane = floor(x * keys / 512). type bit 0 (1) = tap, bit 7 (128) = hold.
        /// For holds the first objectParams field (before ':') is the end time.
        /// </summary>
        private static OsuHitObject? ParseHitObject(string line, int keys, int timeOffset = 0)
        {
            var parts = line.Split(new[] { ',' }, 6);
            if (parts.Length < 5) return null;
            if (keys <= 0) keys = 4;

            int x = ParseInt(parts[0]);
            int time = ParseInt(parts[2]) + timeOffset;
            int type = ParseInt(parts[3]);

            int lane = (int)Math.Floor(x * (double)keys / 512.0);
            if (lane < 0) lane = 0;
            if (lane > keys - 1) lane = keys - 1;

            bool isHold = (type & 128) != 0;
            string sampleField = parts.Length > 5 ? parts[5] : "";
            ParseHitSample(sampleField, isHold, out string sampleFilename, out int sampleVolume);
            if (isHold)
            {
                // parts[5] = "endTime:hitSample..."
                if (parts.Length < 6) return null;
                var endField = parts[5];
                int colon = endField.IndexOf(':');
                var endStr = colon >= 0 ? endField.Substring(0, colon) : endField;
                int end = ParseInt(endStr) + timeOffset;
                return new OsuHitObject(lane, time, end, customSampleFilename: sampleFilename,
                    customSampleVolume: sampleVolume);
            }

            return new OsuHitObject(lane, time, customSampleFilename: sampleFilename,
                customSampleVolume: sampleVolume);
        }
        private static void ParseHitSample(string field, bool isHold, out string filename, out int volume)
        {
            filename = "";
            volume = 0;
            if (string.IsNullOrEmpty(field)) return;
            if (isHold)
            {
                int colon = field.IndexOf(':');
                if (colon < 0 || colon + 1 >= field.Length) return;
                field = field.Substring(colon + 1);
            }
            var p = field.Split(new[] { ':' }, 5);
            if (p.Length < 5) return;
            volume = Math.Max(0, Math.Min(100, TryInt(p[3], 0)));
            filename = Dequote(p[4]);
        }


        private static int ParseInt(string s) =>
            int.Parse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);

        private static int TryInt(string s, int fallback) =>
            int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        private static double ParseDouble(string s) =>
            double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>Lightweight .osu metadata (no hit-object list) produced by <see cref="OsuBeatmapParser.ReadMeta"/>
    /// for the external-song folder scan.</summary>
    public sealed class OsuMeta
    {
        public int Mode;                 // 3 = mania
        public int Keys;                 // CircleSize (mania key/column count)
        public string Title = "";
        public string Artist = "";
        public string Creator = "";
        public string Version = "";      // difficulty name
        public string AudioFilename = "";
        public string BackgroundFilename = "";
        public double Bpm;
        public int BeatmapSetId = -1;    // [Metadata] BeatmapSetID；未上傳/自製/轉檔譜大多是 -1，只能當分組的次要線索
        public int NoteCount;            // number of [HitObjects] lines
        public int KeysoundCount;        // storyboard Sample events + hit objects with a custom sample filename
        public int PreviewTime = -1;     // [General] PreviewTime (ms); -1 = none (試聽起點；長度用預設)
    }
}
