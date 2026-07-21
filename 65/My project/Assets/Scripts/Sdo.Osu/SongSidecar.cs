using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Sdo.Osu
{
    /// <summary>One song's block in a folder's <see cref="SongSidecar"/>.</summary>
    public sealed class SongSidecarEntry
    {
        /// <summary>Which song in the folder this block describes — the <see cref="ExternalSongGrouper"/> key
        /// (e.g. <c>audio:song.mp3</c>). "" = the folder's only song.</summary>
        public string SongKey = "";

        /// <summary>Bare filename (same folder) of the generated CD disc image; "" = not generated yet.</summary>
        public string CdImage = "";

        /// <summary>Bare filename (same folder) of the generated .dps choreography — external songs ship no dance, so
        /// one is generated on first play and recorded here (delete the line to have it rebuilt); "" = not yet.</summary>
        public string Dps = "";

        /// <summary>Which generator built <see cref="Dps"/> (<see cref="SongSidecar.DpsGenerator"/>). A dance built by
        /// an older one is rebuilt on the next play — otherwise a fix to the generator would never reach the songs a
        /// player has already danced. 0 = unknown/absent.</summary>
        public int DpsVersion;

        /// <summary>Reserved: the dance (.mot) and camera files a user will drop next to the chart. Parsed and
        /// round-tripped today, not yet consumed by gameplay.</summary>
        public string Mot = "";
        public string Camera = "";

        /// <summary>Per-song timing tweak in MILLISECONDS (chart editor F11/F12 → Ctrl+S). Positive delays the music.
        /// External osu/StepMania charts often carry a <c>#OFFSET</c> that doesn't quite match their (re-encoded) audio;
        /// this is where the editor's hand-calibrated correction persists so it also applies in gameplay. 0 = none.</summary>
        public float OffsetMs;

        /// <summary>Tags we don't know about, kept in order so rewriting the file never eats a user's own lines.</summary>
        public readonly List<KeyValuePair<string, string>> Extra = new List<KeyValuePair<string, string>>();
    }

    /// <summary>
    /// The per-folder sidecar (<c>sdo.header</c>) that records what the game attached to the songs of a user song
    /// folder: today the generated CD disc image, later the dance (.mot) and camera files. It is read on every scan
    /// and is what lets the disc be built ONCE — a folder with a recorded CD image never composes one again.
    ///
    /// Format is StepMania's header grammar (<c>#TAG:value;</c>, <c>//</c> comments) so it stays hand-editable. A song
    /// folder may hold SEVERAL songs (several osu beatmap sets dropped in flat, several .sm files), so — exactly like a
    /// .sm opens each chart with <c>#NOTES:</c> — each song opens a block with <c>#SONG:&lt;key&gt;;</c>, the key being
    /// its identity inside the folder (<see cref="ExternalSongGrouper.KeyOf"/>). A one-song folder writes an empty key,
    /// and tags before any <c>#SONG:</c> belong to that empty-key song, so a hand-written single-song file needs no
    /// <c>#SONG:</c> line at all.
    ///
    /// Pure/testable: text in, entries out.
    /// </summary>
    public static class SongSidecar
    {
        /// <summary>Sidecar filename, one per song folder.</summary>
        public const string FileName = "sdo.header";

        public const int Version = 1;

        /// <summary>Current dance generator. Bump whenever <see cref="RandomDps"/> would choreograph a song
        /// differently: a recorded dance with an older <c>#DPSVER</c> is rebuilt on the next play.</summary>
        // 3 = the body replays official three-motion groups too; 2 = openings replay the official rows (slices);
        // 1 = whole-clip openings
        public const int DpsGenerator = 3;

        private const string TagVersion = "VERSION";
        private const string TagSong = "SONG";
        private const string TagCdImage = "CDIMAGE";
        private const string TagDps = "DPS";
        private const string TagDpsVer = "DPSVER";
        private const string TagMot = "MOT";
        private const string TagCamera = "CAMERA";
        private const string TagOffsetMs = "OFFSETMS";

        /// <summary>Parse a sidecar. Malformed input yields whatever blocks were readable — never throws.</summary>
        public static List<SongSidecarEntry> Parse(string text)
        {
            var entries = new List<SongSidecarEntry>();
            if (string.IsNullOrEmpty(text)) return entries;

            SongSidecarEntry cur = null;
            foreach (var tag in Tags(text))
            {
                string name = tag.Key;
                string value = tag.Value;

                if (name == TagVersion) continue;                     // informational; we accept any version
                if (name == TagSong)
                {
                    cur = Find(entries, value);
                    if (cur == null) { cur = new SongSidecarEntry { SongKey = value }; entries.Add(cur); }
                    continue;
                }

                // Tags before any #SONG: describe the folder's single song (hand-written files need no #SONG line).
                if (cur == null)
                {
                    cur = Find(entries, "");
                    if (cur == null) { cur = new SongSidecarEntry(); entries.Add(cur); }
                }

                switch (name)
                {
                    case TagCdImage: cur.CdImage = value; break;
                    case TagDps: cur.Dps = value; break;
                    case TagDpsVer: cur.DpsVersion = ParseInt(value); break;
                    case TagMot: cur.Mot = value; break;
                    case TagCamera: cur.Camera = value; break;
                    case TagOffsetMs: cur.OffsetMs = ParseFloat(value); break;
                    default: cur.Extra.Add(new KeyValuePair<string, string>(name, value)); break;
                }
            }
            return entries;
        }

        /// <summary>Serialize entries back to sidecar text (the empty MOT/CAMERA lines are what tells a user where to
        /// name those files once they drop them in).</summary>
        public static string Write(IReadOnlyList<SongSidecarEntry> entries)
        {
            var sb = new StringBuilder();
            sb.Append("// SDO song header — generated by the game. Delete a #CDIMAGE / #DPS line (or the whole file) to\n");
            sb.Append("// have the CD disc image / the dance rebuilt. #MOT/#CAMERA are read but not yet used.\n");
            sb.Append('#').Append(TagVersion).Append(':').Append(Version.ToString(CultureInfo.InvariantCulture)).Append(";\n");
            if (entries != null)
                foreach (var e in entries)
                {
                    if (e == null) continue;
                    sb.Append('\n');
                    sb.Append('#').Append(TagSong).Append(':').Append(Clean(e.SongKey)).Append(";\n");
                    sb.Append('#').Append(TagCdImage).Append(':').Append(Clean(e.CdImage)).Append(";\n");
                    sb.Append('#').Append(TagDps).Append(':').Append(Clean(e.Dps)).Append(";\n");
                    if (e.DpsVersion > 0)
                        sb.Append('#').Append(TagDpsVer).Append(':').Append(e.DpsVersion.ToString(CultureInfo.InvariantCulture)).Append(";\n");
                    sb.Append('#').Append(TagMot).Append(':').Append(Clean(e.Mot)).Append(";\n");
                    sb.Append('#').Append(TagCamera).Append(':').Append(Clean(e.Camera)).Append(";\n");
                    if (e.OffsetMs != 0f)   // 0 = no shift → don't clutter the file (absent reads back as 0)
                        sb.Append('#').Append(TagOffsetMs).Append(':').Append(e.OffsetMs.ToString("0.###", CultureInfo.InvariantCulture)).Append(";\n");
                    foreach (var x in e.Extra)
                        sb.Append('#').Append(Clean(x.Key)).Append(':').Append(Clean(x.Value)).Append(";\n");
                }
            return sb.ToString();
        }

        /// <summary>Record <paramref name="cdFile"/> as the CD image of the song <paramref name="songKey"/> in
        /// <paramref name="text"/> (the folder's current sidecar, "" when it has none), leaving every other song's
        /// block — and this song's other tags — untouched. Returns the new file text.</summary>
        public static string SetCdImage(string text, string songKey, string cdFile)
        {
            var entries = Parse(text);
            string key = Clean(songKey);
            var entry = Find(entries, key);
            if (entry == null) { entry = new SongSidecarEntry { SongKey = key }; entries.Add(entry); }
            entry.CdImage = Clean(cdFile);
            return Write(entries);
        }

        /// <summary>Record <paramref name="dpsFile"/> as the generated choreography of the song
        /// <paramref name="songKey"/> (stamped with the generator that built it), leaving every other song's block —
        /// and this song's other tags — untouched.</summary>
        public static string SetDps(string text, string songKey, string dpsFile, int generator = DpsGenerator)
        {
            var entries = Parse(text);
            string key = Clean(songKey);
            var entry = Find(entries, key);
            if (entry == null) { entry = new SongSidecarEntry { SongKey = key }; entries.Add(entry); }
            entry.Dps = Clean(dpsFile);
            entry.DpsVersion = generator;
            return Write(entries);
        }

        /// <summary>Record the per-song timing offset (ms) for <paramref name="songKey"/>, leaving every other song's
        /// block — and this song's other tags — untouched. The chart editor's Ctrl+S calls this for external songs so a
        /// hand-calibrated offset survives to gameplay and the next session. Returns the new file text.</summary>
        public static string SetOffset(string text, string songKey, float offsetMs)
        {
            var entries = Parse(text);
            string key = Clean(songKey);
            var entry = Find(entries, key);
            if (entry == null) { entry = new SongSidecarEntry { SongKey = key }; entries.Add(entry); }
            entry.OffsetMs = offsetMs;
            return Write(entries);
        }

        /// <summary>The song's block, or null.</summary>
        public static SongSidecarEntry Find(IReadOnlyList<SongSidecarEntry> entries, string songKey)
        {
            if (entries == null) return null;
            string key = Clean(songKey);
            foreach (var e in entries)
                if (e != null && string.Equals(Clean(e.SongKey), key, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        /// <summary>Filename to generate the CD disc image under, inside the song's own folder. The folder's only song
        /// (key "") gets the plain <c>cd.png</c>; in a multi-song folder the song's key is slugged into the name so a
        /// human can tell the discs apart, and the key's hash is appended so they can't collide — slugging alone would
        /// hand every CJK-named track in a folder the same filename (the slug of "恋.mp3" and "愛.mp3" is the same).</summary>
        public static string CdFileName(string songKey)
        {
            string key = Clean(songKey);
            if (key.Length == 0) return "cd.png";
            string slug = Slug(key);
            return slug.Length > 0 ? "cd_" + slug + "_" + FnvHex(key) + ".png" : "cd_" + FnvHex(key) + ".png";
        }

        /// <summary>Filename to generate the choreography under, inside the song's own folder. Same scheme as
        /// <see cref="CdFileName"/>: the folder's only song gets the plain <c>dance.dps</c>, and in a multi-song folder
        /// the song's key is slugged in (with its hash, so two songs can't collide on one name).</summary>
        public static string DpsFileName(string songKey)
        {
            string key = Clean(songKey);
            if (key.Length == 0) return "dance.dps";
            string slug = Slug(key);
            return slug.Length > 0 ? "dance_" + slug + "_" + FnvHex(key) + ".dps" : "dance_" + FnvHex(key) + ".dps";
        }

        // ---- lexing: #NAME:value; with // comments, as in .sm ----

        private static IEnumerable<KeyValuePair<string, string>> Tags(string text)
        {
            int i = 0;
            int n = text.Length;
            while (i < n)
            {
                char c = text[i];
                if (c == '/' && i + 1 < n && text[i + 1] == '/')      // comment → skip to end of line
                {
                    while (i < n && text[i] != '\n') i++;
                    continue;
                }
                if (c != '#') { i++; continue; }

                int colon = text.IndexOf(':', i + 1);
                int semi = text.IndexOf(';', i + 1);
                int nl = text.IndexOf('\n', i + 1);
                if (colon < 0) yield break;                            // no more well-formed tags
                if (nl >= 0 && colon > nl) { i = nl + 1; continue; }   // '#' on a line of its own → not a tag

                // A value ends at its ';' — but a line that forgot one ends at the line break, or it would swallow the
                // next tag whole (its ';' is the one we'd find).
                int end = n;
                if (semi >= 0 && (nl < 0 || semi < nl)) end = semi;
                else if (nl >= 0) end = nl;
                if (end < colon) { i = colon + 1; continue; }

                string name = text.Substring(i + 1, colon - i - 1).Trim().ToUpperInvariant();
                string value = text.Substring(colon + 1, end - colon - 1).Trim();
                if (name.Length > 0) yield return new KeyValuePair<string, string>(name, Clean(value));
                i = end + 1;
            }
        }

        private static int ParseInt(string s)
            => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v > 0 ? v : 0;

        private static float ParseFloat(string s)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;

        // Values are single-line tokens: strip the separators that would corrupt the file if a filename ever held them.
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
                if (ch != ';' && ch != '#' && ch != '\r' && ch != '\n') sb.Append(ch);
            return sb.ToString().Trim();
        }

        /// <summary>Longest slug we put in a disc filename — the hash suffix carries the uniqueness, so this only has
        /// to stay readable and keep long keys from pushing the path over the limit.</summary>
        private const int MaxSlug = 40;

        // Song key → a filesystem-safe, readable slug: "audio:my song.mp3" → "audio_my_song_mp3". Letters and digits
        // of ANY script survive (a folder of CJK-named tracks keeps readable disc names); the separators Windows
        // forbids in a filename do not.
        private static string Slug(string key)
        {
            var sb = new StringBuilder(key.Length);
            bool lastUnderscore = false;
            foreach (char ch in key)
            {
                char c = char.ToLowerInvariant(ch);
                bool ok = char.IsLetterOrDigit(c) || c == '-';
                if (ok) { sb.Append(c); lastUnderscore = false; }
                else if (!lastUnderscore && sb.Length > 0) { sb.Append('_'); lastUnderscore = true; }
                if (sb.Length >= MaxSlug) break;
            }
            return sb.ToString().Trim('_');
        }

        // FNV-1a 32-bit → 8 hex chars (same hash the catalog uses for external gns).
        private static string FnvHex(string s)
        {
            uint h = 2166136261u;
            foreach (char ch in s) { h ^= (byte)ch; h *= 16777619u; }
            return h.ToString("x8");
        }
    }
}
