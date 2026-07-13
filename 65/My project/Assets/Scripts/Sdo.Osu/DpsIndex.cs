using System;
using System.Collections.Generic;

namespace Sdo.Osu
{
    /// <summary>
    /// One row of an official dance's opening: a SLICE of a motion (<see cref="StartF"/>..<see cref="EndF"/>, both
    /// inclusive frames), not the whole clip. Official choreography plays a clip in consecutive slices — the next row
    /// resumes where this one ended — which is why the slice, not just the motion name, has to be carried.
    /// <see cref="EndF"/> &lt; <see cref="StartF"/> means "range unknown" (an old index) → play the whole clip.
    /// </summary>
    public readonly struct IntroSlice
    {
        public string Mot { get; }
        public int StartF { get; }
        public int EndF { get; }

        public IntroSlice(string mot, int startF, int endF)
        {
            Mot = mot;
            StartF = startF;
            EndF = endF;
        }

        public bool HasRange => EndF >= StartF;
        public int Frames => HasRange ? EndF - StartF + 1 : 0;
    }

    /// <summary>
    /// The baked dance index (<c>DANCE/DPSINDEX.TXT</c>, built offline by <c>tools/build_dps_index.py</c>) — everything
    /// <see cref="RandomDps"/> needs to choreograph a song that has none, so the game never has to walk MOTION/ (7k
    /// files) or crack open the 2k official .dps at startup:
    ///
    ///   <c>P &lt;motion&gt;</c>                        the pool of generic dance clips (wdanceNNNN.mot)
    ///   <c>I &lt;mot&gt;:&lt;start&gt;:&lt;end&gt;|…</c>  one official dance's OPENING, row by row — every row up to
    ///                                             (not including) its fourth distinct motion, slices and all
    ///   <c>F &lt;motion&gt; &lt;frames&gt;</c>            a motion's length in frames
    ///
    /// Lines are order-independent; anything else (blank, <c>#</c> comment, <c>V</c> version, unknown tag) is skipped,
    /// so the file can grow tags without breaking older builds. A bare motion name in an <c>I</c> line (the V1 format)
    /// parses as "range unknown" and still works. Pure: text in, index out — never throws.
    /// </summary>
    public sealed class DpsIndex
    {
        /// <summary>Path of the index inside the data tree.</summary>
        public const string RelPath = "DANCE/DPSINDEX.TXT";

        /// <summary>Assumed length of a motion the index says nothing about (8 s at 30 fps).</summary>
        public const int DefaultFrames = 240;

        private readonly List<string> _pool = new List<string>();
        private readonly List<IntroSlice[]> _intros = new List<IntroSlice[]>();
        private readonly Dictionary<string, int> _frames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> Pool => _pool;

        /// <summary>Openings harvested from the official choreographies: each entry is one dance's opening rows.</summary>
        public IReadOnlyList<IntroSlice[]> Intros => _intros;

        /// <summary>Frame count of a motion (&gt;= 1); <see cref="DefaultFrames"/> when the index doesn't list it.</summary>
        public int Frames(string mot)
        {
            if (!string.IsNullOrEmpty(mot) && _frames.TryGetValue(mot, out int f) && f > 0) return f;
            return DefaultFrames;
        }

        /// <summary>True when the index carries no motions to dance with (missing/empty file → no generated dance).</summary>
        public bool IsEmpty => _pool.Count == 0;

        public static DpsIndex Parse(string text)
        {
            var idx = new DpsIndex();
            if (string.IsNullOrEmpty(text)) return idx;

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length < 3 || line[0] == '#') continue;
                char tag = line[0];
                string val = line.Substring(2).Trim();
                if (val.Length == 0) continue;

                switch (tag)
                {
                    case 'P':
                        idx._pool.Add(val.ToLowerInvariant());
                        break;

                    case 'I':
                        var intro = ParseIntro(val);
                        if (intro != null) idx._intros.Add(intro);   // a malformed row drops the WHOLE opening
                        break;

                    case 'F':
                        int sp = val.LastIndexOf(' ');
                        if (sp <= 0) break;
                        if (!int.TryParse(val.Substring(sp + 1).Trim(), out int frames) || frames <= 0) break;
                        idx._frames[val.Substring(0, sp).Trim().ToLowerInvariant()] = frames;
                        break;
                }
            }
            return idx;
        }

        // "<mot>:<start>:<end>|<mot>:<start>:<end>|…" — one segment per row of the official opening, in order.
        // A bare "<mot>" (V1) → range unknown. Any malformed segment kills the opening: half an opening would splice
        // two unrelated clips together.
        private static IntroSlice[] ParseIntro(string val)
        {
            var segs = val.ToLowerInvariant().Split('|');
            var rows = new List<IntroSlice>(segs.Length);
            foreach (var seg in segs)
            {
                var s = seg.Trim();
                if (s.Length == 0) continue;

                int c1 = s.IndexOf(':');
                if (c1 < 0) { rows.Add(new IntroSlice(s, 0, -1)); continue; }   // V1: name only → whole clip

                int c2 = s.IndexOf(':', c1 + 1);
                if (c2 < 0) return null;
                string mot = s.Substring(0, c1).Trim();
                if (mot.Length == 0) return null;
                if (!int.TryParse(s.Substring(c1 + 1, c2 - c1 - 1).Trim(), out int start)) return null;
                if (!int.TryParse(s.Substring(c2 + 1).Trim(), out int end)) return null;
                if (start < 0 || end < start) return null;
                rows.Add(new IntroSlice(mot, start, end));
            }
            return rows.Count > 0 ? rows.ToArray() : null;
        }
    }
}
