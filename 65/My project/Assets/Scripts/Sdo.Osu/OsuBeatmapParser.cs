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
                        // "time,beatLength,meter,...". The first UNINHERITED point (beatLength > 0)
                        // gives the song tempo: BPM = 60000 / beatLength. Inherited points are negative.
                        if (map.Bpm <= 0.0) ParseTimingPoint(line, map);
                        break;
                    case "HitObjects":
                        var ho = ParseHitObject(line, map.Keys);
                        if (ho.HasValue) map.HitObjects.Add(ho.Value);
                        break;
                }
            }

            return map;
        }

        /// <summary>First uninherited timing point (beatLength &gt; 0) sets map.Bpm = 60000/beatLength.</summary>
        private static void ParseTimingPoint(string line, OsuBeatmap map)
        {
            var p = line.Split(',');
            if (p.Length < 2) return;
            if (!double.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var beatLength))
                return;
            if (beatLength > 0.0) map.Bpm = 60000.0 / beatLength;
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
        private static OsuHitObject? ParseHitObject(string line, int keys)
        {
            var parts = line.Split(',');
            if (parts.Length < 5) return null;
            if (keys <= 0) keys = 4;

            int x = ParseInt(parts[0]);
            int time = ParseInt(parts[2]);
            int type = ParseInt(parts[3]);

            int lane = (int)Math.Floor(x * (double)keys / 512.0);
            if (lane < 0) lane = 0;
            if (lane > keys - 1) lane = keys - 1;

            bool isHold = (type & 128) != 0;
            if (isHold)
            {
                // parts[5] = "endTime:hitSample..."
                if (parts.Length < 6) return null;
                var endField = parts[5];
                int colon = endField.IndexOf(':');
                var endStr = colon >= 0 ? endField.Substring(0, colon) : endField;
                int end = ParseInt(endStr);
                return new OsuHitObject(lane, time, end);
            }

            return new OsuHitObject(lane, time);
        }

        private static int ParseInt(string s) =>
            int.Parse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);

        private static double ParseDouble(string s) =>
            double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
