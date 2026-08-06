using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

        /// <summary>Bare filename (same folder) of the .dps choreography — external songs ship no dance, so one is
        /// generated on first play and recorded here (delete the line to have it rebuilt); "" = not yet.
        ///
        /// 🔴 也可以由使用者**手寫**,指向自己放進歌曲資料夾的客製 .dps(檔名不是 <see cref="SongSidecar.DpsFileName"/>
        /// 那一套 → <see cref="SongSidecar.IsGeneratedDpsName"/> 為 false)。那種一律照用:不看
        /// <see cref="DpsVersion"/>、不重生成、也不會被覆寫掉。</summary>
        public string Dps = "";

        /// <summary>Which generator built <see cref="Dps"/> (<see cref="SongSidecar.DpsGenerator"/>). A dance built by
        /// an older one is rebuilt on the next play — otherwise a fix to the generator would never reach the songs a
        /// player has already danced. 0 = unknown/absent.
        ///
        /// 只對**遊戲自己生的**舞有意義:使用者手寫的 <see cref="Dps"/> 不是這個產生器造的,拿版本號去汰換它等於
        /// 把人家的客製編舞蓋掉(實測症狀:手寫 <c>#DPS:12951.DPS;</c> 沒補 <c>#DPSVER</c> → 被當成 0 = 舊版 →
        /// 第一次開這首歌就被改寫成生成的 dance.dps)。</summary>
        public int DpsVersion;

        /// <summary>Reserved: the dance (.mot) and camera files a user will drop next to the chart. Parsed and
        /// round-tripped today, not yet consumed by gameplay.</summary>
        public string Mot = "";
        public string Camera = "";

        /// <summary>Per-song timing tweak in MILLISECONDS (chart editor F11/F12 → Ctrl+S). Positive delays the music.
        /// External osu/StepMania charts often carry a <c>#OFFSET</c> that doesn't quite match their (re-encoded) audio;
        /// this is where the editor's hand-calibrated correction persists so it also applies in gameplay. 0 = none.</summary>
        public float OffsetMs;

        /// <summary>Per-song DANCE timing tweak in MILLISECONDS — <b>independent of <see cref="OffsetMs"/></b>. Positive
        /// delays the dancer (shifts the whole DPS later). The music offset moves the audio; this moves ONLY the
        /// choreography, so a song whose dance drifts against the music can be nudged without touching the audio sync.
        /// Always written (even 0) so the knob is visible and hand-editable in every song's block. 0 = no dance shift.</summary>
        public float DpsOffsetMs;

        /// <summary>Tags we don't know about, kept in order so rewriting the file never eats a user's own lines.</summary>
        public readonly List<KeyValuePair<string, string>> Extra = new List<KeyValuePair<string, string>>();
    }

    /// <summary>
    /// The per-folder sidecar (<c>sdoinfo.dat</c>, was <c>sdo.header</c> — see <see cref="LegacyFileName"/>) that
    /// records what the game attached to the songs of a user song
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
        public const string FileName = "sdoinfo.dat";

        /// <summary>The pre-rename sidecar name. Still READ when a folder has no <see cref="FileName"/>, so CD discs,
        /// generated dances and hand-calibrated offsets recorded before the rename keep applying; the next write
        /// migrates the folder to <see cref="FileName"/> and removes this one (see <see cref="WriteText"/>).</summary>
        public const string LegacyFileName = "sdo.header";

        public const int Version = 1;

        /// <summary>Current dance generator. Bump whenever <see cref="RandomDps"/> would choreograph a song
        /// differently: a recorded dance with an older <c>#DPSVER</c> is rebuilt on the next play.</summary>
        // 5 = the seed is the CHARTS' content (their SHA-256, as a set), not the folder's NAME — 缺歌傳檔 renames the
        //     folder on the way over and does not carry the .dps, so a 4-era dance differs between the two sides of a
        //     transfer (see Sdo.Game.ExternalDps). 🔴 This bump is what makes the fix reach songs already danced:
        //     without it the sender keeps replaying the .dps its OLD seed built and the two sides still disagree.
        // 4 = length/tempo are per-SONG (every difficulty's note windows unioned + the catalog BPM), so every
        //     difficulty gets the same dance — a 3-era dance was planned off whichever chart happened to generate it,
        //     and off a chart already through lead-in/short-hold collapse at that (see DanceInputs);
        // 3 = the body replays official three-motion groups too; 2 = openings replay the official rows (slices);
        // 1 = whole-clip openings
        public const int DpsGenerator = 5;

        private const string TagVersion = "VERSION";
        private const string TagSong = "SONG";
        private const string TagCdImage = "CDIMAGE";
        private const string TagDps = "DPS";
        private const string TagDpsVer = "DPSVER";
        private const string TagMot = "MOT";
        private const string TagCamera = "CAMERA";
        private const string TagOffsetMs = "OFFSETMS";
        private const string TagDpsOffsetMs = "DPSOFFSETMS";

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
                    case TagDpsOffsetMs: cur.DpsOffsetMs = ParseFloat(value); break;
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
            // 手寫 #DPS 是支援的用法(自帶客製編舞),而它跟自動生成的差別在檔名 —— 檔案裡講一句,免得使用者
            // 以為那一行遲早會被蓋掉(在修好之前確實會)。
            sb.Append("// #DPS may point at your OWN .dps in this folder (any name other than dance*.dps): it is then\n");
            sb.Append("// always used as-is — never regenerated, never overwritten, #DPSVER ignored.\n");
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
                    // DPS offset is ALWAYS written (even 0), unlike OFFSETMS: it's a hand-tuning knob we want visible in
                    // every block so a user can find and nudge it. Absent still reads back as 0 (hand-written files are fine).
                    sb.Append('#').Append(TagDpsOffsetMs).Append(':').Append(e.DpsOffsetMs.ToString("0.###", CultureInfo.InvariantCulture)).Append(";\n");
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

        /// <summary>Record the per-song DANCE timing offset (ms) for <paramref name="songKey"/>, independent of the music
        /// <see cref="SetOffset"/>, leaving every other song's block — and this song's other tags — untouched.</summary>
        public static string SetDpsOffset(string text, string songKey, float dpsOffsetMs)
        {
            var entries = Parse(text);
            string key = Clean(songKey);
            var entry = Find(entries, key);
            if (entry == null) { entry = new SongSidecarEntry { SongKey = key }; entries.Add(entry); }
            entry.DpsOffsetMs = dpsOffsetMs;
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

        // ---- file IO: the one place that knows the on-disk name and the legacy fallback ----

        /// <summary>The path a folder's sidecar should be READ from: the current <see cref="FileName"/> if it exists,
        /// else the legacy <see cref="LegacyFileName"/> (records written before the rename), else the current path.</summary>
        public static string ReadPath(string folder)
        {
            string cur = Path.Combine(folder, FileName);
            if (File.Exists(cur)) return cur;
            string legacy = Path.Combine(folder, LegacyFileName);
            return File.Exists(legacy) ? legacy : cur;
        }

        /// <summary>The folder's sidecar text (current name, else the legacy one); "" when neither exists / is unreadable
        /// (a corrupt sidecar reads back as "nothing recorded yet").</summary>
        public static string ReadText(string folder)
        {
            try { string p = ReadPath(folder); return File.Exists(p) ? File.ReadAllText(p) : ""; }
            catch { return ""; }
        }

        /// <summary>Write a folder's sidecar to the current <see cref="FileName"/> and drop a leftover
        /// <see cref="LegacyFileName"/> so a folder never carries both — the legacy file's content was just folded into
        /// the new one on the way in (via <see cref="ReadText"/>).</summary>
        public static void WriteText(string folder, string text)
        {
            string path = Path.Combine(folder, FileName);
            File.WriteAllText(path, text);
            try
            {
                string legacy = Path.Combine(folder, LegacyFileName);
                if (!string.Equals(path, legacy, StringComparison.OrdinalIgnoreCase) && File.Exists(legacy))
                    File.Delete(legacy);
            }
            catch { /* a leftover legacy file is harmless: the current one wins reads */ }
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
            if (key.Length == 0) return GeneratedDpsName;
            string slug = Slug(key);
            return slug.Length > 0 ? GeneratedDpsPrefix + slug + "_" + FnvHex(key) + ".dps"
                                   : GeneratedDpsPrefix + FnvHex(key) + ".dps";
        }

        /// <summary>生成的舞蹈檔名(單首歌的資料夾)。</summary>
        private const string GeneratedDpsName = "dance.dps";
        /// <summary>多首歌的資料夾裡,生成的舞蹈檔名前綴(<c>dance_&lt;slug&gt;_&lt;hash&gt;.dps</c>)。</summary>
        private const string GeneratedDpsPrefix = "dance_";

        /// <summary>
        /// 這個 .dps 檔名是**遊戲自己生的**嗎(<see cref="DpsFileName"/> 的產物:<c>dance.dps</c> /
        /// <c>dance_&lt;…&gt;.dps</c>)?
        ///
        /// 反過來說,false = 使用者自己放進歌曲資料夾、在 <c>#DPS</c> 指名的客製編舞。那一支永遠照用:
        /// <c>#DPSVER</c> 是「我這一版產生器造的」的標記,對別人手寫的檔案沒有發言權,拿它去汰換等於把客製舞蓋掉
        /// (見 <c>Sdo.Game.ExternalDps.EnsureFor</c>)。<see cref="SongPackFilter.IsGenerated"/> 也走這一份。
        /// </summary>
        public static bool IsGeneratedDpsName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string n = Clean(fileName).ToLowerInvariant();
            return n == GeneratedDpsName || n.StartsWith(GeneratedDpsPrefix, StringComparison.Ordinal);
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
