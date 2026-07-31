using System;
using System.Collections.Generic;
using System.Globalization;

namespace Sdo.Osu
{
    /// <summary>One row of a pack's <c>sdo_pack.tsv</c> — everything about a .gn that the file itself can't tell us.</summary>
    public sealed class SdoPackSong
    {
        public string Gn = "";              // chart filename, e.g. "sdom0040K.gn"
        /// <summary>LCG decryption seed. 0 = unknown → the runtime falls back to the shared seed pool.</summary>
        public uint Seed;
        public int FileId;                  // 官方歌曲編號 — 封面/試聽/編舞都用它命名
        public double Bpm;
        public readonly int[] Levels = new int[3];      // easy/normal/hard LV
        public readonly int[] Notes = new int[3];       // easy/normal/hard 音符數
        public readonly int[] Durations = new int[3];   // easy/normal/hard 秒數
        // Paths RELATIVE to the .tsv's folder ("" = the pack doesn't ship one).
        public string Audio = "", Cd = "", Preview = "", Dps = "";
        public string Title = "", Artist = "";
    }

    /// <summary>
    /// Reads the <c>sdo_pack.tsv</c> sidecar that <c>tools/nx/nx_to_gn.py</c> writes next to a converted SDO song pack
    /// (the [NX]Patch <c>patch music/</c> folder and anything shaped like it).
    ///
    /// Why a sidecar at all — the .gn files carry their own numbers (see <see cref="GnHeader"/>), but two things can
    /// only come from outside:
    ///   • the LCG SEED. [NX] re-encrypted every chart with its own key (199 charts, 199 keys), so the runtime's shared
    ///     seed pool can't open them; the key table is computed once, offline, and travels with the pack.
    ///   • the TITLE/ARTIST. They live in SongList.dat as GB2312/Big5, and this runtime has no cp936 codec — the tool
    ///     decodes them at conversion time and writes UTF-8 here (same pattern as the shop's shop_names.tsv).
    /// It also records where the pack keeps each song's music / CD art / preview clip / choreography, so the scanner
    /// never has to guess a pack's directory layout.
    ///
    /// Format: UTF-8, tab-separated, <c>#</c> comments and blank lines ignored. The first non-comment line NAMES the
    /// columns, so column order is free and unknown columns are skipped — an older game reading a newer pack just
    /// ignores what it doesn't know. Missing/garbled cells degrade to defaults rather than dropping the row.
    /// Engine-free and pure so it unit-tests without Unity.
    /// </summary>
    public static class SdoPackIndex
    {
        public const string FileName = "sdo_pack.tsv";

        /// <summary>Parse a sidecar's text → its rows (empty list for null/garbage). Never throws.</summary>
        public static List<SdoPackSong> Parse(string text)
        {
            var rows = new List<SdoPackSong>();
            if (string.IsNullOrEmpty(text)) return rows;
            Dictionary<string, int> col = null;
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                var cells = line.Split('\t');
                if (col == null) { col = Header(cells); continue; }   // first non-comment line names the columns
                var song = Row(col, cells);
                if (song != null) rows.Add(song);
            }
            return rows;
        }

        /// <summary>Index the rows by .gn filename (lower-case) for lookup while scanning a folder.</summary>
        public static Dictionary<string, SdoPackSong> ByGn(List<SdoPackSong> rows)
        {
            var map = new Dictionary<string, SdoPackSong>(StringComparer.OrdinalIgnoreCase);
            if (rows != null)
                foreach (var r in rows)
                    if (r != null && !string.IsNullOrEmpty(r.Gn)) map[r.Gn] = r;
            return map;
        }

        private static Dictionary<string, int> Header(string[] cells)
        {
            var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < cells.Length; i++)
            {
                var name = cells[i].Trim();
                if (name.Length > 0 && !col.ContainsKey(name)) col[name] = i;
            }
            return col;
        }

        private static SdoPackSong Row(Dictionary<string, int> col, string[] cells)
        {
            string gn = Cell(col, cells, "gn");
            if (gn.Length == 0) return null;   // a row with no chart is nothing
            var s = new SdoPackSong
            {
                Gn = gn,
                Seed = (uint)Num(col, cells, "seed", 0),
                FileId = (int)Num(col, cells, "fileId", 0),
                Bpm = Real(col, cells, "bpm"),
                Audio = Cell(col, cells, "audio"),
                Cd = Cell(col, cells, "cd"),
                Preview = Cell(col, cells, "preview"),
                Dps = Cell(col, cells, "dps"),
                Title = Cell(col, cells, "title"),
                Artist = Cell(col, cells, "artist"),
            };
            const string d = "ENH";   // easy / normal / hard column suffixes: lvE notesN durH …
            for (int i = 0; i < 3; i++)
            {
                s.Levels[i] = (int)Num(col, cells, "lv" + d[i], 0);
                s.Notes[i] = (int)Num(col, cells, "notes" + d[i], 0);
                s.Durations[i] = (int)Num(col, cells, "dur" + d[i], 0);
            }
            return s;
        }

        private static string Cell(Dictionary<string, int> col, string[] cells, string name)
            => col.TryGetValue(name, out int i) && i < cells.Length ? cells[i].Trim() : "";

        // Seeds are uint32 and routinely exceed int.MaxValue → parse as long, clamp to 0 on anything unparseable.
        private static long Num(Dictionary<string, int> col, string[] cells, string name, long fallback)
        {
            var v = Cell(col, cells, name);
            return long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) && n >= 0 ? n : fallback;
        }

        private static double Real(Dictionary<string, int> col, string[] cells, string name)
        {
            var v = Cell(col, cells, name);
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && n > 0 ? n : 0.0;
        }
    }
}
