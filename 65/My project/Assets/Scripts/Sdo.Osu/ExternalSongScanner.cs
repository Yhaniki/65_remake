using System;
using System.Collections.Generic;
using System.IO;

namespace Sdo.Osu
{
    /// <summary>
    /// Scans user song folders — <c>Songs/&lt;group&gt;/…</c> (and each AdditionalSongFolders root, treated as another
    /// Songs root, i.e. a group-parent, exactly like StepMania) — for osu!mania and StepMania charts, producing
    /// <see cref="ExternalSong"/> records. Engine-free (System.IO only) so it stays in Sdo.Osu and is unit-testable;
    /// the Unity glue (register into the catalog, load textures) lives in Sdo.Game.ExternalSongLibrary.
    ///
    /// A song folder is the FIRST folder holding chart files on the way down from a group — so a pack downloaded as one
    /// more folder level is still found, while a song folder's own subfolders (a StepMania editor's FileBackup/, an osu
    /// storyboard dir) stay assets rather than becoming songs. One such folder may hold MORE THAN ONE song: several
    /// beatmap sets dropped in flat, or several .sm files. <see cref="ExternalSongGrouper"/> splits them by audio file;
    /// each song then fills its own hard/normal/easy slots from ITS three HARDEST 4K difficulties
    /// (<see cref="ExternalDifficultyPicker"/>; 「最難」依目前那套難度算法 — 見 <see cref="SlotByMsd"/>). Only 4K is used (osu CircleSize==4 &amp; Mode==3; StepMania
    /// dance-single), and only songs whose audio file is actually present. Missing dance choreography is fine —
    /// gameplay falls back to a generic dance.
    /// </summary>
    public static class ExternalSongScanner
    {
        public sealed class SongDir { public string Group = ""; public string Path = ""; }

        public sealed class ScanProgress { public int Done; public int Total; public string Current = ""; }

        /// <summary>
        /// 分槽（哪張譜進簡單/普通/困難）要用哪套難度：true = MinaCalc 的 MSD 換算等級（<c>DifficultyCalc</c> 的預設），
        /// false = osu 星數等級。**這個欄位本身預設 false**（純資料組件，不讀設定層），呼叫端每次掃描前都要灌值。
        /// 由呼叫端在掃描前灌進來（<c>RoomConfig.difficultyCalc</c>；這個組件不認得設定層）。玩家選了哪套算法，
        /// 連「一首歌有 4 個以上難度時要留哪三張」都照那套 —— 兩套的高低順序不一定一樣。
        ///
        /// 掃描結果會進快取，所以快取也記著它是用哪套算的（<c>ExternalScanCache</c>）：換一套 → 冷快取重掃一次，
        /// 之後照常吃快取。
        /// </summary>
        public static bool SlotByMsd;

        /// <summary>
        /// 掃描時算音符數要不要照「無理短長條→一般 note」收合（<c>RoomConfig.optCollapseShortHolds</c>，
        /// <see cref="OsuBeatmap.CollapseShortHolds"/>）。收掉的長條少一次「放開」判定，選歌那欄要跟著少，
        /// 不然顯示的數字就不等於玩家實際打得到的 combo（實測一張 .sm 收 2 條 → 999 vs 997）。
        /// **這個欄位本身預設 false**（純資料組件，不讀設定層），呼叫端每次掃描前都要灌值，同 <see cref="SlotByMsd"/>；
        /// 快取也要記著它是用哪個值算的（<c>ExternalScanCache</c> 的 calc 鍵），換設定 → 冷快取重掃一次。
        /// 只影響 osu/sm/mc 這些外部轉檔譜 —— .gn 歌曲包的音符數讀表頭，本來就是官方自己算的判定次數。
        /// </summary>
        public static bool CollapseShortHolds;

        /// <summary>分槽用的等級表。<paramref name="byMsd"/> 且**每張譜都算得出 MSD** → 用 MSD 換算等級；只要有一張
        /// 缺（空譜/太短算不出來）就整首退回 osu 等級 —— 兩種尺度混在一起比，排出來只會更亂。純函式。</summary>
        public static List<int> SlotLevels(IReadOnlyList<ChartStats> stats, bool byMsd)
        {
            var lv = new List<int>(stats?.Count ?? 0);
            if (stats == null) return lv;
            bool useMsd = byMsd;
            if (useMsd) foreach (var s in stats) if (s.Msd <= 0f) { useMsd = false; break; }
            foreach (var s in stats) lv.Add(useMsd ? ManiaMsd.ToLevel(s.Msd) : s.Level);
            return lv;
        }

        private static readonly string[] AudioExt = { ".ogg", ".mp3", ".wav" };
        private static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".bmp" };

        /// <summary>How deep below a group folder we still look for charts. Packs nest one or two levels; the cap is
        /// there so a mis-configured root (e.g. C:\) can't walk the world.</summary>
        private const int MaxDepth = 8;

        /// <summary>Flatten roots → the list of song folders to load. The group is the folder one level under a root
        /// (the browse tab); chart folders nested deeper inside it still belong to that group. Groups and folders are
        /// walked in alphabetical order for a stable, readable browse order.</summary>
        public static List<SongDir> BuildWorklist(IReadOnlyList<string> roots)
        {
            var work = new List<SongDir>();
            if (roots == null) return work;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // junction/symlink loop guard
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                try { if (!Directory.Exists(root)) continue; } catch { continue; }

                string rootGroup = DirName(root);   // the shared group for songs sitting loose directly under the root

                // Charts dropped straight into a root (no group folder) still count — the root names their group.
                Probe(root, out bool rootCharts, out _);
                if (rootCharts) work.Add(new SongDir { Group = rootGroup, Path = root });

                string[] groups;
                try { groups = Directory.GetDirectories(root); } catch { continue; }
                Array.Sort(groups, StringComparer.OrdinalIgnoreCase);
                foreach (var groupDir in groups)
                {
                    // A folder one level under the root is one of two things:
                    //   • an UNPACKED SINGLE — osu! drops every song as its own folder straight under Songs/, with no
                    //     group level. Hundreds of these must NOT each become a one-song browse tab, so they all share
                    //     the root's group (rootGroup).
                    //   • a PACK — a StepMania group folder, or an osu pack that itself holds song subfolders (or several
                    //     sets dropped flat). It becomes its OWN group, named after the pack folder.
                    // The tell is whether the folder is itself a song (charts sit directly in it). A flat multi-song
                    // folder is a pack too: it is a song here, but LoadFolder re-groups it under its own name.
                    Probe(groupDir, out bool isSong, out _);
                    string group = isSong ? rootGroup : DirName(groupDir);
                    Collect(work, groupDir, group, 0, visited);
                }
            }
            return work;
        }

        private static void Collect(List<SongDir> work, string dir, string group, int depth, HashSet<string> visited)
        {
            string full;
            try { full = Path.GetFullPath(dir); } catch { return; }
            if (!visited.Add(full)) return;

            Probe(dir, out bool isSong, out _);
            if (isSong) work.Add(new SongDir { Group = group, Path = dir });
            if (depth >= MaxDepth) return;

            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { return; }
            Array.Sort(subs, StringComparer.OrdinalIgnoreCase);
            foreach (var sub in subs)
            {
                // Inside a song folder, subfolders are its assets — a StepMania editor's FileBackup/ (dozens of
                // autosaved .sm), an osu storyboard dir — so only descend into one that is a song in its own right:
                // charts AND its own audio. Outside a song folder we always keep walking, because a pack is just one
                // more folder level, and one stray chart lying at pack level must not hide the songs beneath it.
                if (isSong)
                {
                    Probe(sub, out bool subCharts, out bool subAudio);
                    if (!subCharts || !subAudio) continue;
                }
                Collect(work, sub, group, depth + 1, visited);
            }
        }

        /// <summary>Does this folder hold chart files / audio files? One enumeration, stops as soon as both are known
        /// (the whole tree is walked before boot can show a determinate progress bar, so this stays cheap).</summary>
        private static void Probe(string dir, out bool charts, out bool audio)
        {
            charts = false; audio = false;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".osu" || ext == ".sm" || ext == ".gn" || ext == ".mc") charts = true;
                    else if (Array.IndexOf(AudioExt, ext) >= 0) audio = true;
                    if (charts && audio) return;
                }
            }
            catch { /* unreadable folder → not a song folder */ }
        }

        private static string DirName(string dir)
        {
            try { return new DirectoryInfo(dir).Name; } catch { return ""; }
        }

        /// <summary>Non-incremental scan (fallback / harness). Prefer BuildWorklist + LoadFolder when you want to
        /// yield between folders for a progress bar.</summary>
        public static List<ExternalSong> Scan(IReadOnlyList<string> roots, Action<ScanProgress> onProgress = null)
        {
            var work = BuildWorklist(roots);
            var songs = new List<ExternalSong>();
            var prog = new ScanProgress { Total = work.Count };
            for (int i = 0; i < work.Count; i++)
            {
                var found = LoadFolder(work[i].Group, work[i].Path);
                songs.AddRange(found);
                prog.Done = i + 1;
                prog.Current = found.Count > 0 ? found[0].Title : "";
                onProgress?.Invoke(prog);
            }
            return songs;
        }

        /// <summary>Load one folder → every playable 4K song in it (empty list when it holds none).</summary>
        public static List<ExternalSong> LoadFolder(string group, string songDir)
        {
            var songs = new List<ExternalSong>();
            string[] files;
            try { files = Directory.GetFiles(songDir); } catch { return songs; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);   // deterministic: grouping must not ride on FS order

            var osu = new List<string>();
            var sm = new List<string>();
            var gn = new List<string>();
            var mc = new List<string>();
            var audio = new List<string>();
            var images = new List<string>();   // basenames
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".osu") osu.Add(f);
                else if (ext == ".sm") sm.Add(f);
                else if (ext == ".gn") gn.Add(f);
                else if (ext == ".mc") mc.Add(f);
                else if (Array.IndexOf(AudioExt, ext) >= 0) audio.Add(f);
                else if (Array.IndexOf(ImageExt, ext) >= 0) images.Add(Path.GetFileName(f));
            }

            var pack = ReadPackIndex(songDir);
            var drafts = new List<Draft>();
            try
            {
                DraftOsu(drafts, osu);
                DraftSm(drafts, sm);
                DraftGn(drafts, gn, pack);
                DraftMalody(drafts, mc);
            }
            catch { /* a malformed folder must never abort the whole scan */ }
            if (drafts.Count == 0) return songs;

            // Songs are the drafts whose audio we can actually find: a chart with no music is unplayable, and dropping
            // it is what keeps chart-only junk (an editor's autosaves, an orphaned .sm) out of the song list. Guessing
            // "the folder's first audio file" is only safe when the folder holds ONE song — with several it would hand
            // song B the music of song A, and silence beats playing the wrong track.
            var kept = new List<Draft>(drafts.Count);
            var tracks = new List<string>(drafts.Count);
            foreach (var d in drafts)
            {
                string audioPath = ResolveAudio(audio, d.AudioName);
                if (audioPath.Length == 0) audioPath = ResolveAudioStem(audio, d.AudioStem);   // .gn: any extension
                if (audioPath.Length == 0 && !d.VirtualAudio) continue;
                kept.Add(d); tracks.Add(audioPath);
            }
            if (kept.Count == 0 && drafts.Count == 1 && audio.Count == 1)
            {
                kept.Add(drafts[0]); tracks.Add(audio[0]);   // sole song whose chart names its audio wrongly
            }
            if (kept.Count == 0) return songs;

            bool sole = kept.Count == 1;
            // A folder that holds SEVERAL songs (several osu sets / several .sm dropped in flat) is its OWN pack: name
            // it after the folder rather than letting its songs dissolve into the parent group's flat song list. A
            // single-song folder keeps the group it was found under (the pack it sits in). DirName("") never fires
            // because a multi-song folder always has a name.
            // EXCEPTION — an SDO song pack: it is multi-song BY CONSTRUCTION (hundreds of .gn in one music folder) and
            // that folder's own name says nothing about the pack ("patch music" / "MUSIC"), so it keeps the group it was
            // found under — which is the pack's real name, the folder the player dropped in.
            string packGroup = sole || AllGn(kept) ? group : DirName(songDir);
            if (string.IsNullOrEmpty(packGroup)) packGroup = group;

            var claimed = ClaimedImages(kept);
            var sidecar = ReadSidecar(songDir);
            RemoveGeneratedCds(images, kept, sole, sidecar);
            for (int i = 0; i < kept.Count; i++)
                songs.Add(Materialize(kept[i], packGroup, songDir, tracks[i], images, sole, claimed, sidecar));

            Disambiguate(songs, kept);
            ApplyServerConfig(songs, songDir);
            return songs;
        }

        /// <summary>
        /// 一個 <c>.gn</c> 歌包**自己的** serverconfig（<c>patch Datas\config2</c> / <c>ServerConfigND.dat</c>）：
        /// 給包裡每首歌它在官方歌單裡的**列號**與**標籤**（NEW/HOT/推薦/古典）。找不到檔就什麼都不做，
        /// 包照舊用檔名排序、沒有標籤。格式見 docs/reverse-engineering/SDO_SERVERCONFIG.md。
        ///
        /// 快取命中的路徑也要呼叫一次（<see cref="ExternalScanCache"/> 的簽章只看歌資料夾自己的檔案，
        /// 而 serverconfig 住在隔壁資料夾 —— 改了它不會讓快取失效），所以這裡是 public。
        /// </summary>
        public static void ApplyServerConfig(List<ExternalSong> songs, string songDir)
        {
            if (songs == null || songs.Count == 0 || string.IsNullOrEmpty(songDir)) return;
            bool anyPack = false;
            foreach (var s in songs) if (s != null && s.Format == SongFormat.Gn) { anyPack = true; break; }
            if (!anyPack) return;

            byte[] raw = null;
            foreach (var p in SdoServerConfig.ConfigCandidates(songDir))
            {
                try { if (File.Exists(p)) { raw = File.ReadAllBytes(p); break; } }
                catch { /* 讀不到就換下一個候選 */ }
            }
            if (raw == null) return;

            var byId = SdoServerConfig.ById(SdoServerConfig.Parse(raw));
            if (byId.Count == 0) return;
            foreach (var s in songs)
            {
                if (s == null || s.Format != SongFormat.Gn) continue;
                if (!byId.TryGetValue(SdoServerConfig.SongIdOf(s.FileId), out var row)) continue;
                s.PackOrder = row.Order;
                s.Badge = row.Badge;
                s.PackHidden = row.Hidden;
            }
        }

        // The discs we generate are written INTO the song folder, so on the next scan they are just more images sitting
        // next to the cover — and a folder whose cover carries no filename hint would hand the picker its own disc back
        // (a disc composed from a disc). Take every name the sidecar records as a CD, plus the names this folder's songs
        // would generate, out of the cover pool.
        private static void RemoveGeneratedCds(List<string> images, List<Draft> kept, bool sole,
            List<SongSidecarEntry> sidecar)
        {
            var cds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in sidecar)
                if (e != null && !string.IsNullOrEmpty(e.CdImage)) cds.Add(e.CdImage);
            foreach (var d in kept) cds.Add(SongSidecar.CdFileName(sole ? "" : d.Key));
            images.RemoveAll(f => cds.Contains(f));
        }

        /// <summary>The folder's sidecar (sdoinfo.dat, or the legacy sdoinfo.dat), or an empty list when it has none /
        /// is unreadable.</summary>
        private static List<SongSidecarEntry> ReadSidecar(string songDir)
            => SongSidecar.Parse(SongSidecar.ReadText(songDir));   // ReadText picks current name, else legacy; "" on any error

        /// <summary>Re-read a folder's sidecar and refresh ONLY its sidecar-derived paths (CD disc / mot / camera) on an
        /// already-built song. Used when a scan result is reused from a cache: a disc composed since the cache was
        /// written (song-select builds it once, then records it) must still be picked up without re-parsing the folder,
        /// so the disc keeps being built exactly once, ever. Reproduces a fresh parse's sidecar step. Pure/testable.</summary>
        public static void ReapplySidecar(ExternalSong song)
        {
            if (song == null || string.IsNullOrEmpty(song.FolderPath)) return;
            // A pack song's jacket comes from sdo_pack.tsv, not from sdoinfo.dat, and it is cached alongside the song —
            // so hold it across the clear below, or every cache hit would strip the pack's real jackets off and leave
            // the whole pack showing the NONE disc. (Its preview clip / choreography aren't sidecar fields at all, so
            // they survive untouched.) A re-run of the converter rewrites the .tsv → new signature → full re-parse.
            string packCd = song.Format == SongFormat.Gn ? song.CdImagePath ?? "" : "";
            song.CdImagePath = ""; song.MotPath = ""; song.CameraPath = ""; song.OffsetMs = 0f; song.DpsOffsetMs = 0f;
            var sidecar = ReadSidecar(song.FolderPath);
            ApplySidecar(song, SongSidecar.Find(sidecar, song.SongKey), song.FolderPath);
            if (packCd.Length > 0) song.CdImagePath = packCd;
        }

        /// <summary>Kept for callers that only want the folder's first song (e.g. a preview probe).</summary>
        public static ExternalSong LoadOne(string group, string songDir)
        {
            var songs = LoadFolder(group, songDir);
            return songs.Count > 0 ? songs[0] : null;
        }

        // ---- drafting: everything decided from the chart files alone, before folder-wide resolution ----

        /// <summary>One song, still unresolved against the folder's audio/image files.</summary>
        private sealed class Draft
        {
            public string Key = "";
            public SongFormat Format;
            public readonly ExternalChart[] Charts = new ExternalChart[3];
            public string Title = "", Artist = "", Version = "";
            public double Bpm;
            public int PreviewStartMs = -1, PreviewLengthMs;
            public string AudioName = "", BannerName = "", BackgroundName = "", CdTitleName = "";
            public bool VirtualAudio;
            /// <summary>.gn only, and only when the sidecar didn't name the audio: the chart stem to match against the
            /// folder with ANY audio extension (see <see cref="ResolveAudioStem"/>). "" = use <see cref="AudioName"/>.</summary>
            public string AudioStem = "";
            // ---- SDO pack (Format == Gn) ----
            public int FileId;
            public uint Seed;
            // Paths RELATIVE to the song folder, straight out of sdo_pack.tsv (a pack keeps its art, previews and
            // choreography in sibling trees, e.g. "../patch Datas/UI/MUSIC/ICONS/10040.PNG"). "" = the pack has none.
            public string CdRel = "", PreviewRel = "", DpsRel = "";
        }

        private static bool AllGn(List<Draft> drafts)
        {
            if (drafts == null || drafts.Count == 0) return false;
            foreach (var d in drafts) if (d.Format != SongFormat.Gn) return false;
            return true;
        }

        /// <summary>The folder's <c>sdo_pack.tsv</c> indexed by .gn name, or an empty map when it has none. A pack
        /// without the sidecar still scans — it just falls back to the .gn's own header + filename conventions, and
        /// (having no seed) can't be decrypted at play time unless its seeds happen to be in the shared pool.</summary>
        private static Dictionary<string, SdoPackSong> ReadPackIndex(string songDir)
        {
            try
            {
                var path = Path.Combine(songDir, SdoPackIndex.FileName);
                if (File.Exists(path))
                    return SdoPackIndex.ByGn(SdoPackIndex.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8)));
            }
            catch { /* unreadable/corrupt sidecar → scan the .gn files bare */ }
            return new Dictionary<string, SdoPackSong>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// One draft per .gn — a native SDO chart carries ALL THREE difficulties in one file, so unlike osu/StepMania
        /// there is nothing to rank or pick: slot d is difficulty d, and an empty difficulty (0 notes, which several
        /// official songs really do ship) simply leaves its slot null so song-select greys it out.
        ///
        /// Everything numeric is read from the .gn's own PLAINTEXT header (<see cref="GnHeader"/>) — no decryption, no
        /// note parsing, so a 199-song pack scans as fast as reading 199 file headers. The sidecar only adds what the
        /// file can't say: the decryption seed, the UTF-8 title/artist, and where the pack keeps art/preview/dance.
        ///
        /// That header level is also the FINAL level: a .gn is never re-rated (no <see cref="StatsOf"/>, so no
        /// <see cref="ManiaStarRating"/> and <see cref="ExternalChart.Msd"/> stays 0). The star / MinaCalc calculators
        /// exist to give osu/StepMania/Malody charts a level they don't have — a .gn already has one, official or
        /// user-dropped alike, and it stays exactly as the chart author wrote it.
        /// </summary>
        private static void DraftGn(List<Draft> drafts, List<string> gnFiles, Dictionary<string, SdoPackSong> pack)
        {
            foreach (var path in gnFiles)
            {
                string name = Path.GetFileName(path);
                GnHeader h;
                try { h = GnHeader.Read(ReadPrefix(path, GnHeaderProbeBytes)); }
                catch { continue; }
                pack.TryGetValue(name, out var row);
                if (!h.Valid && row == null) continue;   // not a .gn we can make sense of

                var d = new Draft { Key = name, Format = SongFormat.Gn };
                bool any = false;
                for (int s = 0; s < 3; s++)
                {
                    int notes = h.Valid ? h.Notes[s] : 0;
                    if (notes <= 0 && row != null) notes = row.Notes[s];
                    if (notes <= 0) continue;                      // difficulty not in this chart → leave the slot empty
                    int level = h.Valid ? h.Levels[s] : 0;
                    if (level <= 0 && row != null) level = row.Levels[s];
                    int dur = h.Valid ? h.Durations[s] : 0;
                    if (dur <= 0 && row != null) dur = row.Durations[s];
                    d.Charts[s] = new ExternalChart
                    {
                        FilePath = path, ChartIndex = s, NoteCount = notes,
                        Level = level > 0 ? level : -1, DurationSec = dur,
                    };
                    any = true;
                }
                if (!any) continue;

                d.FileId = h.Valid && h.FileId > 0 ? h.FileId : (row?.FileId ?? 0);
                d.Bpm = h.Valid && h.Bpm > 0f ? h.Bpm : (row?.Bpm ?? 0.0);
                d.Title = !string.IsNullOrEmpty(row?.Title) ? row.Title : Path.GetFileNameWithoutExtension(path);
                d.Artist = row?.Artist ?? "";
                d.Seed = row?.Seed ?? 0u;
                d.CdRel = row?.Cd ?? "";
                d.PreviewRel = row?.Preview ?? "";
                d.DpsRel = row?.Dps ?? "";
                // The music file: what the sidecar names, else the chart name with its difficulty letter dropped
                // (sdom0040K.gn → sdom0040.ogg/.mp3/.wav — the engine's own convention, see Sdo.Game.SongPaths).
                if (!string.IsNullOrEmpty(row?.Audio)) d.AudioName = ExternalSongGrouper.BaseName(row.Audio);
                else { d.AudioStem = GnAudioStem(path); d.AudioName = d.AudioStem + ".ogg"; }
                d.PreviewStartMs = -1;   // a pack ships a real preview CLIP instead of a window into the full song
                drafts.Add(d);
            }
        }

        /// <summary>How much of a .gn to read to find its header. The plaintext header sits at 456 in every known file
        /// and <see cref="GnHeader"/> scans at most 0x4000 — reading 20 KB is generous and keeps a 199-song pack's scan
        /// off the "read every chart in full" path.</summary>
        private const int GnHeaderProbeBytes = 20 * 1024;

        private static byte[] ReadPrefix(string path, int count)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var buf = new byte[(int)Math.Min(count, Math.Max(0, fs.Length))];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = fs.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read == buf.Length) return buf;
                var exact = new byte[read];
                Array.Copy(buf, exact, read);
                return exact;
            }
        }

        /// <summary>"sdom0040K.gn" → "sdom0040": the chart name with its difficulty-set letter (k = 一般譜, t = 另一份譜,
        /// both play the same track) dropped. That is the engine's own chart↔music naming rule (Sdo.Game.SongPaths).</summary>
        internal static string GnAudioStem(string gnPath)
        {
            string stem = Path.GetFileNameWithoutExtension(gnPath) ?? "";
            if (stem.Length > 1)
            {
                char last = char.ToUpperInvariant(stem[stem.Length - 1]);
                if (last == 'K' || last == 'T') stem = stem.Substring(0, stem.Length - 1);
            }
            return stem;
        }

        /// <summary>Match a bare stem against the folder's audio files, whatever extension it turned up as (the [NX]
        /// pack ships .mp3 where the original game had .ogg). "" when the folder has none of them.</summary>
        private static string ResolveAudioStem(List<string> available, string stem)
        {
            if (string.IsNullOrEmpty(stem)) return "";
            foreach (var ext in AudioExt)
            {
                string want = stem + ext;
                foreach (var f in available)
                    if (string.Equals(Path.GetFileName(f), want, StringComparison.OrdinalIgnoreCase)) return f;
            }
            return "";
        }

        private static void DraftOsu(List<Draft> drafts, List<string> osuFiles)
        {
            var candFile = new List<string>();
            var candMeta = new List<OsuMeta>();
            var candName = new List<string>();
            foreach (var path in osuFiles)
            {
                OsuMeta meta;
                try { meta = OsuBeatmapParser.ReadMeta(File.ReadAllText(path)); }
                catch { continue; }
                if (meta.Mode != 3) continue;                // mania only
                if (meta.Keys != 4) continue;                // 4K only
                if (meta.NoteCount <= 0) continue;
                candFile.Add(path); candMeta.Add(meta); candName.Add(Path.GetFileName(path));
            }
            if (candFile.Count == 0) return;

            foreach (var g in ExternalSongGrouper.GroupOsu(candMeta, candName))
            {
                var counts = new List<int>(g.Charts.Count);
                foreach (var i in g.Charts) counts.Add(candMeta[i].NoteCount);

                var d = new Draft { Key = g.Key, Format = SongFormat.Osu };
                // Rate EVERY chart in the group (星數×7 → 等級 + 譜長), then slot hard/normal/easy by that level —
                // not by note count — so the hard slot is the genuinely hardest chart. (Full-parses every diff of
                // the song rather than only the chosen 3; the scan cache absorbs the cost on every rescan.)
                var stats = new List<ChartStats>(g.Charts.Count);
                foreach (var i in g.Charts) stats.Add(OsuStats(candFile[i]));
                // 分槽用目前那套難度算法（見 SlotByMsd）—— 選了 minacalc 就連「留哪三張」都照 MSD 排。
                var slots = ExternalDifficultyPicker.Assign(SlotLevels(stats, SlotByMsd), counts);   // per SONG — never across the folder
                for (int s = 0; s < 3; s++)
                {
                    int c = slots[s];
                    if (c < 0) continue;
                    int i = g.Charts[c];
                    var st = stats[c];
                    d.Charts[s] = new ExternalChart
                    {
                        // 音符數 = 判定次數(長條的放開也算一次,見 ChartStats.JudgedNotes);
                        // 解析失敗(0)才退回 [HitObjects] 的行數。
                        FilePath = candFile[i], ChartIndex = 0,
                        NoteCount = st.JudgedNotes > 0 ? st.JudgedNotes : candMeta[i].NoteCount,
                        Level = st.Level, DurationSec = st.DurationSec, Msd = st.Msd,
                    };
                }

                // Display fields: hardest chart first, but a single chart often leaves a field blank (converters emit
                // empty artists, PreviewTime:-1, an empty [Events] bg) — so take the first chart that HAS each field.
                int lead = g.Charts[0];
                d.Title = First(g.Charts, i => candMeta[i].Title, Path.GetFileNameWithoutExtension(candFile[lead]));
                d.Artist = First(g.Charts, i => candMeta[i].Artist, "");
                d.Version = First(g.Charts, i => candMeta[i].Version, "");
                // Basenamed like the grouping key and like the .sm branch: a chart may spell its audio/background with
                // a folder prefix or backslashes, and the folder's files are matched by filename.
                d.AudioName = ExternalSongGrouper.BaseName(First(g.Charts, i => candMeta[i].AudioFilename, ""));
                // `virtual` is osu!'s reserved no-backing-track sentinel, never an audio filename to resolve. Keep it
                // unconditional: even a malformed/partially parsed keysound map must not borrow the folder's first sample.
                d.VirtualAudio = OsuBeatmapParser.IsVirtualAudioFilename(d.AudioName);
                d.BackgroundName = ExternalSongGrouper.BaseName(First(g.Charts, i => candMeta[i].BackgroundFilename, ""));
                foreach (var i in g.Charts) { if (candMeta[i].Bpm > 0) { d.Bpm = candMeta[i].Bpm; break; } }
                d.PreviewStartMs = -1;
                // Zero is the common "no useful preview point chosen" osu export; only a positive value is explicit.
                foreach (var i in g.Charts) { if (candMeta[i].PreviewTime > 0) { d.PreviewStartMs = candMeta[i].PreviewTime; break; } }
                d.PreviewLengthMs = 0;   // osu carries no preview length → default window
                drafts.Add(d);
            }
        }

        private static void DraftSm(List<Draft> drafts, List<string> smFiles)
        {
            foreach (var smPath in smFiles)
            {
                SmChart.SmSong smSong;
                try { smSong = SmChart.Parse(File.ReadAllText(smPath)); }
                catch { continue; }

                var candIndex = new List<int>();
                var candCount = new List<int>();
                var candMeter = new List<int>();
                for (int i = 0; i < smSong.Charts.Count; i++)
                {
                    var ch = smSong.Charts[i];
                    if (!SmChart.IsDanceSingle(ch)) continue;
                    int notes = SmChart.PlayableNoteCount(smSong, i);   // warp(負 BPM)掃掉的音符打不到 → 不算
                    if (notes <= 0) continue;
                    candIndex.Add(i); candCount.Add(notes); candMeter.Add(ch.Meter);
                }
                if (candIndex.Count == 0) continue;

                string audioName = ExternalSongGrouper.BaseName(smSong.Music);
                // The same song shipped as both .osu and .sm (same audio) stays ONE song — the .osu wins, as before.
                if (audioName.Length > 0 && HasAudio(drafts, audioName)) continue;

                var d = new Draft { Key = ExternalSongGrouper.SmKeyOf(Path.GetFileName(smPath)), Format = SongFormat.Sm };
                // Rate EVERY candidate (same star scale as osu; meter fallback), then slot hard/normal/easy by that
                // level — not by note count — so the hard slot is always the genuinely hardest chart.
                var candStats = new List<ChartStats>(candIndex.Count);
                for (int k = 0; k < candIndex.Count; k++) candStats.Add(SmStats(smSong, candIndex[k], candMeter[k]));
                var slots = ExternalDifficultyPicker.Assign(SlotLevels(candStats, SlotByMsd), candCount);
                for (int s = 0; s < 3; s++)
                {
                    int c = slots[s];
                    if (c < 0) continue;
                    var st = candStats[c];
                    d.Charts[s] = new ExternalChart
                    {
                        // 音符數 = 判定次數(長條的放開也算一次,見 ChartStats.JudgedNotes);
                        // 解析失敗(0)才退回 PlayableNoteCount(長條只算一顆)。
                        FilePath = smPath, ChartIndex = candIndex[c],
                        NoteCount = st.JudgedNotes > 0 ? st.JudgedNotes : candCount[c],
                        Level = st.Level, DurationSec = st.DurationSec, Msd = st.Msd,
                    };
                }
                d.Title = Coalesce(smSong.Title, Path.GetFileNameWithoutExtension(smPath));
                d.Artist = smSong.Artist ?? "";
                d.Version = "";
                d.Bpm = smSong.FirstPositiveBpm;   // 負 BPM 開頭的譜要顯示第一個正的,不然選歌畫面會出現負 BPM
                d.AudioName = audioName;
                d.BannerName = ExternalSongGrouper.BaseName(smSong.Banner);
                d.BackgroundName = ExternalSongGrouper.BaseName(smSong.Background);
                d.CdTitleName = ExternalSongGrouper.BaseName(smSong.CdTitle);
                d.PreviewStartMs = smSong.SampleStart >= 0 ? (int)Math.Round(smSong.SampleStart * 1000.0) : -1;
                d.PreviewLengthMs = smSong.SampleLength > 0 ? (int)Math.Round(smSong.SampleLength * 1000.0) : 0;
                drafts.Add(d);
            }
        }

        /// <summary>Draft the Malody .mc charts in a folder. One .mc is one difficulty; charts sharing a folder + audio
        /// (the bgm note) are the difficulties of one song, so they group by audio (<see cref="ExternalSongGrouper.GroupMalody"/>)
        /// and each song fills its own hard/normal/easy slots from ITS highest-note-count charts. Only 4-key Key-mode
        /// charts are kept. Runs after osu/StepMania, so the same song shipped as both stays ONE song (the earlier
        /// format's audio shadows the .mc), exactly like .osu shadows .sm.</summary>
        private static void DraftMalody(List<Draft> drafts, List<string> mcFiles)
        {
            var candFile = new List<string>();
            var candSong = new List<MalodyChart.McSong>();
            var candName = new List<string>();
            foreach (var path in mcFiles)
            {
                MalodyChart.McSong s;
                try { s = MalodyChart.Parse(File.ReadAllText(path)); }
                catch { continue; }
                if (!MalodyChart.Is4KKey(s)) continue;      // 4K Key mode only
                if (s.Notes.Count <= 0) continue;
                candFile.Add(path); candSong.Add(s); candName.Add(Path.GetFileName(path));
            }
            if (candFile.Count == 0) return;

            var audioNames = new List<string>(candSong.Count);
            var counts = new List<int>(candSong.Count);
            foreach (var s in candSong) { audioNames.Add(ExternalSongGrouper.BaseName(s.AudioFile)); counts.Add(s.Notes.Count); }

            foreach (var g in ExternalSongGrouper.GroupMalody(audioNames, counts, candName))
            {
                // The same song shipped as both .osu/.sm and .mc (same audio) stays ONE song — the earlier format wins.
                string audioName = ExternalSongGrouper.AudioNameOf(g.Key);
                if (audioName.Length > 0 && HasAudio(drafts, audioName)) continue;

                var slotCounts = new List<int>(g.Charts.Count);
                foreach (var i in g.Charts) slotCounts.Add(counts[i]);

                var d = new Draft { Key = g.Key, Format = SongFormat.Malody };
                // Rate EVERY chart in the group (星數×N → 等級 + 譜長；同 osu/SM 一致的量尺), then slot by that level —
                // not by note count — so the hard slot is the genuinely hardest chart.
                var mStats = new List<ChartStats>(g.Charts.Count);
                foreach (var i in g.Charts) mStats.Add(MalodyStats(candFile[i]));
                var slots = ExternalDifficultyPicker.Assign(SlotLevels(mStats, SlotByMsd), slotCounts);   // per SONG — never across the folder
                for (int s = 0; s < 3; s++)
                {
                    int c = slots[s];
                    if (c < 0) continue;
                    int i = g.Charts[c];
                    var st = mStats[c];
                    d.Charts[s] = new ExternalChart
                    {
                        // 音符數 = 判定次數(長條的放開也算一次,見 ChartStats.JudgedNotes);
                        // 解析失敗(0)才退回 .mc 的 note 筆數。
                        FilePath = candFile[i], ChartIndex = 0,
                        NoteCount = st.JudgedNotes > 0 ? st.JudgedNotes : counts[i],
                        Level = st.Level, DurationSec = st.DurationSec, Msd = st.Msd,
                    };
                }

                int lead = g.Charts[0];
                d.Title = First(g.Charts, i => candSong[i].Title, Path.GetFileNameWithoutExtension(candFile[lead]));
                d.Artist = First(g.Charts, i => candSong[i].Artist, "");
                d.Version = First(g.Charts, i => candSong[i].Version, "");
                d.AudioName = ExternalSongGrouper.BaseName(First(g.Charts, i => candSong[i].AudioFile, ""));
                d.BackgroundName = ExternalSongGrouper.BaseName(First(g.Charts, i => candSong[i].Background, ""));
                foreach (var i in g.Charts) { if (candSong[i].FirstBpm > 0) { d.Bpm = candSong[i].FirstBpm; break; } }
                d.PreviewStartMs = -1;
                foreach (var i in g.Charts) { if (candSong[i].PreviewMs >= 0) { d.PreviewStartMs = candSong[i].PreviewMs; break; } }
                d.PreviewLengthMs = 0;   // Malody carries no preview length → default window
                drafts.Add(d);
            }
        }

        private static bool HasAudio(List<Draft> drafts, string audioName)
        {
            foreach (var d in drafts)
                if (string.Equals(d.AudioName, audioName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ---- resolution against the folder's files ----

        private static ExternalSong Materialize(Draft d, string group, string dir, string audioPath,
            List<string> images, bool sole, HashSet<string> claimed, List<SongSidecarEntry> sidecar)
        {
            var song = new ExternalSong
            {
                Group = group,
                FolderPath = dir,
                SongKey = sole ? "" : d.Key,   // sole song → "" keeps the folder-only gn (and its favourites)
                Format = d.Format,
                Title = d.Title,
                Artist = d.Artist,
                Bpm = d.Bpm,
                PreviewStartMs = d.PreviewStartMs,
                PreviewLengthMs = d.PreviewLengthMs,
                AudioPath = audioPath,
                // 掃描時「只讀 .osu/.sm」——不碰音檔。mp3 要整檔逐幀解碼才算得出長度(NLayer)，是開機掃描
                // 最慢的一步；ogg/wav 雖只讀檔頭也一併省下。時間欄先用譜尾(StatsOf.DurationSec)，等玩家真的
                // 選到這首歌時，SongSelectScreen 才補上真正的音檔長度(一次、背景執行緒)。
                AudioDurationSec = 0,
                ImagePath = ResolveImage(dir, ImagePool(images, d, sole, claimed), d),
            };
            for (int s = 0; s < 3; s++) song.Charts[s] = d.Charts[s];
            ApplySidecar(song, SongSidecar.Find(sidecar, song.SongKey), dir);
            song.FileId = d.FileId;
            song.GnSeed = d.Seed;
            ApplyPackResources(song, dir, d.CdRel, d.PreviewRel, d.DpsRel);
            return song;
        }

        /// <summary>Point a pack song at the art / preview clip / choreography the pack ships for it (paths are
        /// relative to the song folder and routinely climb out of it into a sibling tree). Runs AFTER the sidecar so a
        /// pack's own CD art wins over a disc we composed on an earlier run — the pack's is the real jacket. A recorded
        /// file that isn't there is dropped, exactly like a sidecar entry.</summary>
        private static void ApplyPackResources(ExternalSong song, string dir, string cdRel, string previewRel, string dpsRel)
        {
            string cd = SidecarFile(dir, cdRel);
            if (cd.Length > 0) song.CdImagePath = cd;
            song.PreviewAudioPath = SidecarFile(dir, previewRel);
            song.DpsPath = SidecarFile(dir, dpsRel);
        }

        // What the folder's sdoinfo.dat already records for this song: the CD disc built on an earlier run (skip
        // composing it again) and — reserved — its dance/camera files. A recorded name whose file is gone is ignored,
        // so deleting cd.png is all it takes to have the disc rebuilt.
        private static void ApplySidecar(ExternalSong song, SongSidecarEntry e, string dir)
        {
            if (e == null) return;
            song.CdImagePath = SidecarFile(dir, e.CdImage);
            song.MotPath = SidecarFile(dir, e.Mot);
            song.CameraPath = SidecarFile(dir, e.Camera);
            song.OffsetMs = e.OffsetMs;
            song.DpsOffsetMs = e.DpsOffsetMs;
        }

        private static string SidecarFile(string dir, string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            try
            {
                var path = Path.Combine(dir, name);
                // sidecar 可能存可攜式的正斜線絕對路徑（Path.Combine 讓絕對路徑勝出）→ 回傳前正規化成本機分隔符。
                return File.Exists(path) ? path.Replace('/', Path.DirectorySeparatorChar) : "";
            }
            catch { return ""; }
        }

        /// <summary>The audio file a chart names, matched case-insensitively against the folder ("" = not there).</summary>
        private static string ResolveAudio(List<string> available, string named)
        {
            if (!string.IsNullOrEmpty(named))
                foreach (var f in available)
                    if (string.Equals(Path.GetFileName(f), named, StringComparison.OrdinalIgnoreCase)) return f;
            return "";
        }

        // Images another song in this folder explicitly claimed (its banner/background) are off-limits to the rest —
        // otherwise ExternalImagePicker's "any image left" rule hands every song the same cover.
        private static HashSet<string> ClaimedImages(List<Draft> drafts)
        {
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in drafts)
            {
                if (d.BannerName.Length > 0) claimed.Add(d.BannerName);
                if (d.BackgroundName.Length > 0) claimed.Add(d.BackgroundName);
            }
            return claimed;
        }

        private static List<string> ImagePool(List<string> images, Draft d, bool sole, HashSet<string> claimed)
        {
            if (sole || claimed.Count == 0) return images;
            var pool = new List<string>(images.Count);
            foreach (var f in images)
                if (!claimed.Contains(f) || NameEq(f, d.BannerName) || NameEq(f, d.BackgroundName)) pool.Add(f);
            return pool;
        }

        private static string ResolveImage(string dir, List<string> pool, Draft d)
        {
            string chosen = ExternalImagePicker.Pick(pool, d.BannerName, d.BackgroundName, d.CdTitleName,
                                                     preferBackground: d.Format == SongFormat.Sm);
            return string.IsNullOrEmpty(chosen) ? "" : Path.Combine(dir, chosen);
        }

        // Several songs in one folder routinely share a title — for two very different reasons that want opposite
        // display:
        //   • A sped-up EDIT: the same song re-timed onto a second audio file. The Title is the real song name and
        //     the osu Version is the variant ("Normal" / "Nightcore") → keep the title, tag the variant.
        //   • An osu PACK / compilation SET: one beatmap set holding SEVERAL distinct songs (each its own audio),
        //     where the shared Title is just the pack label ("SDO Pack8") and each song's real name lives in its
        //     Version ("Aoi Shiori"). The pack label is noise → promote the Version to be the title.
        // The tell is how many songs collapse onto the one Title: an edit is a pair, a pack is many. At THREE or more
        // we read the shared Title as a pack label. (Below that stays "Title (variant)", so the edit pair is safe.)
        private static void Disambiguate(List<ExternalSong> songs, List<Draft> drafts)
        {
            if (songs.Count < 2) return;

            // Pack set: drop the shared pack label, show each song's own name (its osu Version) instead.
            var byTitle = CountTitles(songs);
            for (int i = 0; i < songs.Count; i++)
            {
                if (byTitle[songs[i].Title] < 3) continue;
                string version = (drafts[i].Version ?? "").Trim();
                if (version.Length > 0) songs[i].Title = version;   // "SDO Pack8" → "Aoi Shiori"
            }

            // Whatever still shares a title — an edit pair, or pack songs whose Version was blank/duplicated — gets a
            // distinguishing tag appended (the osu difficulty name, else the audio file name).
            var stillClash = CountTitles(songs);
            for (int i = 0; i < songs.Count; i++)
            {
                if (stillClash[songs[i].Title] < 2) continue;
                string tag = (drafts[i].Version ?? "").Trim();
                // A promoted pack song already shows its Version as the title — tag it by audio, not "(X (X))".
                if (tag.Length == 0 || string.Equals(tag, songs[i].Title, StringComparison.OrdinalIgnoreCase))
                    tag = Path.GetFileNameWithoutExtension(drafts[i].AudioName);
                if (!string.IsNullOrEmpty(tag)) songs[i].Title += " (" + tag + ")";
            }
        }

        private static Dictionary<string, int> CountTitles(List<ExternalSong> songs)
        {
            var n = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in songs) { n.TryGetValue(s.Title ?? "", out int c); n[s.Title ?? ""] = c + 1; }
            return n;
        }

        /// <summary>Level + play time + 判定次數 of ONE chart. All come from the same full parse — the star rating already
        /// needs it, so the 時間 column costs nothing extra (the scan still only full-parses the 3 chosen charts).</summary>
        public struct ChartStats
        {
            public int Level; public int DurationSec; public float Msd;
            /// <summary>選歌畫面那欄音符數 ＝ 這張譜的**判定次數**（<see cref="OsuBeatmap.TotalNotes"/>：長條的頭與放開各一次，
            /// 炸彈與 warp 掃掉的裝飾音不算）＝ 全接時的最大 combo。官方 .gn 表頭寫的 notes 就是這個數
            /// （實測 25 首 × 3 難度：68/75 等於 TotalNotes，其餘 7 個是沒有長條、兩種算法同值的譜），
            /// 外部歌照著算才會和官方歌讀起來一樣。0 = 解析失敗（呼叫端退回自己那份便宜的物件數）。</summary>
            public int JudgedNotes;
        }

        // Level from osu!mania star rating (star × 7, clamped). Level 0 / no duration on failure.
        private static ChartStats OsuStats(string osuPath)
        {
            try { return StatsOf(OsuBeatmapParser.Parse(File.ReadAllText(osuPath)), 0); }
            catch { return new ChartStats { Level = 0 }; }
        }

        // Same for a StepMania chart: convert to the osu representation and use the SAME star scale as osu; fall back
        // to the chart's own #NOTES meter if the conversion/rating fails.
        private static ChartStats SmStats(SmChart.SmSong song, int blockIndex, int fallbackMeter)
        {
            try { return StatsOf(SmChart.ToBeatmap(song, blockIndex), fallbackMeter); }
            catch { return new ChartStats { Level = fallbackMeter }; }
        }

        // Malody: parse the .mc and rate it on the SAME osu!mania star scale as osu/SM. Level 0 / no duration on failure.
        private static ChartStats MalodyStats(string mcPath)
        {
            try { return StatsOf(MalodyChart.ToBeatmap(MalodyChart.Parse(File.ReadAllText(mcPath))), 0); }
            catch { return new ChartStats { Level = 0 }; }
        }

        /// <summary>Rating + duration + 判定次數 of a parsed chart. Duration = the last note's time (hold ends included),
        /// which is the same measure the official catalog's dur* carries — so both kinds of song read alike in the list.
        ///
        /// <see cref="ChartStats.JudgedNotes"/> 要和 <c>ScreenGameplay.LoadChart</c> 看到的是同一張譜,所以
        /// <see cref="CollapseShortHolds"/> 開著時**先收合再數** —— 但收合放在難度算完之後:收掉的是幾條無理短長條,
        /// 讓它去動星等/MSD 只會讓顯示的 LV 無聲飄一格。呼叫這支的三條路線(osu/sm/mc)都是
        /// <see cref="OsuBeatmap.AllowsShortHoldCollapse"/> 為 true 的外部轉檔譜;.gn 歌曲包不走這裡(音符數讀表頭)。</summary>
        public static ChartStats StatsOf(OsuBeatmap beatmap, int fallbackLevel)
        {
            if (beatmap == null) return new ChartStats { Level = fallbackLevel };
            var stats = new ChartStats
            {
                Level = ManiaStarRating.Level(beatmap),
                DurationSec = (int)Math.Round(Math.Max(0.0, beatmap.LastNoteMs) / 1000.0),
                // Etterna MinaCalc overall MSD (raw). Computed from the same parsed beatmap — the slot ordering still
                // uses the osu star LEVEL; this is carried alongside for the MinaCalc difficulty display.
                Msd = ManiaMsd.Overall(beatmap),
            };
            if (CollapseShortHolds) beatmap.CollapseShortHolds();
            stats.JudgedNotes = beatmap.TotalNotes;
            return stats;
        }

        private static string First(List<int> charts, Func<int, string> field, string fallback)
        {
            foreach (var i in charts)
            {
                var v = field(i);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return fallback ?? "";
        }

        private static bool NameEq(string a, string b)
            => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
               string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string Coalesce(string a, string b) => !string.IsNullOrEmpty(a) ? a : (b ?? "");
    }
}
