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
                        // "time,beatLength,meter,...". Uninherited points (beatLength > 0) are tempo
                        // changes; inherited points (beatLength < 0) are osu! SV (green) lines. ALL of
                        // them are kept (for the note scroll); the first uninherited also sets map.Bpm.
                        ParseTimingPoint(line, map);
                        break;
                    case "HitObjects":
                        var ho = ParseHitObject(line, map.Keys);
                        if (ho.HasValue) map.HitObjects.Add(ho.Value);
                        break;
                }
            }

            return map;
        }

        /// <summary>
        /// Add a timing point (time,beatLength) to <see cref="OsuBeatmap.TimingPoints"/>. The first
        /// uninherited point (beatLength &gt; 0) also sets map.Bpm = 60000/beatLength. Inherited points
        /// keep their negative beatLength (osu! SV encoding); see <see cref="OsuTimingPoint"/>.
        /// </summary>
        private static void ParseTimingPoint(string line, OsuBeatmap map)
        {
            var p = line.Split(',');
            if (p.Length < 2) return;
            if (!double.TryParse(p[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
                return;
            if (!double.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var beatLength))
                return;
            map.TimingPoints.Add(new OsuTimingPoint(time, beatLength));
            if (beatLength > 0.0 && map.Bpm <= 0.0) map.Bpm = 60000.0 / beatLength;
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
                        // First "0,0,\"bg.jpg\",..." line is the background; a leading "1,..." video is a fallback bg.
                        if (string.IsNullOrEmpty(m.BackgroundFilename))
                        {
                            var p = line.Split(',');
                            if (p.Length >= 3 && (p[0].Trim() == "0" || p[0].Trim() == "1"))
                                m.BackgroundFilename = Dequote(p[2]);
                        }
                        break;
                    case "HitObjects":
                        m.NoteCount++;   // one object per line (tap or hold)
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
        public int PreviewTime = -1;     // [General] PreviewTime (ms); -1 = none (試聽起點；長度用預設)
    }
}
