using System;
using System.Collections.Generic;
using System.IO;
using Sdo.Osu;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Boot-time cache of the external-song scan. The scan's real cost is PARSING every candidate chart (ReadMeta on
    /// each .osu, plus a full parse + star-rating on the three chosen ones per song); the directory walk is cheap by
    /// comparison. So on each boot we still walk the tree, but for every folder whose source files are unchanged since
    /// last time we reuse the parsed result instead of re-parsing it.
    ///
    /// A folder's "unchanged" token is a <see cref="Signature"/> over its chart/audio/image files' (name, size, mtime)
    /// — cheap file stats, no content read. Generated artifacts (the <c>sdoinfo.dat</c> sidecar, the composed
    /// <c>cd*.png</c> disc, the <c>dance*.dps</c>) are excluded, so composing a disc or building a dance never
    /// invalidates the cache line for the very song you just played. A cache hit still re-reads the tiny sidecar
    /// (<see cref="ExternalSongScanner.ReapplySidecar"/>) so a disc built since caching is picked up.
    ///
    /// Stored as one JSON file in a per-user writable dir; its path is resolved on the main thread and handed to the
    /// scan worker. Any read/parse failure just means a cold cache — the scan re-parses and rewrites it.
    /// </summary>
    public static class ExternalScanCache
    {
        // Bump when the PARSE RESULT for the same source files changes (not just the schema), so old cache lines are
        // dropped and every folder re-parses with the new logic. v2: osu pack sets now show the song name (Version)
        // instead of the shared pack-label title — cached titles from v1 must be discarded to pick that up.
        // v3: the displayed LV changed from star × 5 to star × 7, so every cached `level` is stale.
        // v4: .gn song packs are scanned now — old lines for a pack folder cached it as "yields nothing".
        // v5: Malody .mc charts are scanned now — a folder holding them was cached as "yields nothing" (and its .mc
        //     weren't in the signature, so the .ogg/.jpg-only signature would still hit and hide the new song).
        // v6: each chart now carries an Etterna MinaCalc `msd` (for the MinaCalc difficulty display) — old lines lack it.
        // v7: .sm tags missing their ';' no longer swallow the following lines (SmChart tolerates a leading-'#' cut),
        //     so folders cached with a mangled title ("M@GIC☆ #SUBTITLE:… #ARTIST:…") must re-parse.
        // v8: 顯示等級的天花板從 99 放寬到 999（ManiaStarRating.LevelMax / ManiaMsd.LevelMax）。舊快取裡撞到 99 的
        //     `level` 都是被壓過的死值，不整份作廢重掃就永遠停在 99。
        // v9: osu 星數等級不再把炸彈當成可打音符（ManiaStarRating 現在跟 ManiaMsd 一樣跳過 IsBomb）。舊快取裡
        //     炸彈多的譜 `level` 被灌水過（灑滿雷的慢譜可以虛高好幾十級），得整份作廢重算。
        // v10: Folder 加 packId(缺歌傳檔要用)。
        // v11: `notes` 的語意換成**判定次數**（長條的放開也算一次，＝全接的最大 combo，也＝官方 .gn 表頭 notes 的
        //     算法）。舊快取存的是「物件數」（長條算一顆），長條多的譜會少報好幾十顆，得整份作廢重算。
        //     (兩件事各自在自己的分支上都編到 v10 —— 合併之後那個號碼下有兩種不相容的快取:一種缺 packId、
        //      一種 `notes` 是舊語意。停在 10 會把兩種都當成有效 → 跳到 11 讓它們一起作廢。)
        // v12: keysounded osu maps with `AudioFilename: virtual` now use an empty base track; old caches pointed at
        //      the folder's first sample instead, so all external-song scan records must be rebuilt.
        // v13: osu PreviewTime:0 now means "use the midpoint", and zero no longer hides a positive preview point from
        //      another difficulty in the same set. Cached previewStartMs values must be rebuilt.
        //      (⚠️ 上面那兩條在 song-loader 分支上原本編成 v11/v12 —— 與這條分支的 v11 撞號,
        //       正是 v11 括號裡寫的那件事又發生一次:兩個分支各自往下編,合併之後同一個號碼底下
        //       會有兩種不相容的快取。所以整組往後挪,Version 直接跳到 13 把兩邊的舊快取一起作廢。)
        // v14: SafeRelPath.MaxSegmentLength 從 100 放寬到 160(osu 的譜面檔名破百是常態)。
        //      🔴 這條**非跳不可**,而且原因不在 sig:packId 只看「可傳的那些檔」,而放寬過濾規則
        //      之後同一個資料夾的可傳檔案集合變了(那些 .osu 從 UnsafePath 變成 Include)→ packId 變了,
        //      但 sig(檔名/大小/mtime)一個位元都沒動 → 快取命中 → **舊 packId 被沿用**。
        //      後果是缺歌的人永遠補不到:房主宣稱舊 packId,而上傳時重掃資料夾會納入那些 .osu,
        //      server 重算後與宣稱的不符 → 整批不收(Hub.Blobs「重算的 packId 與宣稱的不符」)。
        //      上面 Folder.packId 那句「sig 沒變時它一定也沒變」的前提是**過濾規則不變**,這次它變了。
        // v15: 難度（`level` 與 `msd`）多了炸彈＋變速加成（Sdo.Osu.ChartDifficultyBonus）——同一張譜多灑了雷、
        //      或多了 BPM 換段 / osu 綠線 SV / 停拍，難度會往上走一點點（最多 +8%）。舊快取存的是沒有這層的值。
        //      (⚠️ 第三次撞號:這條在 main 上原本編成 v13,與這條分支的 v13/v14 撞在一起 —— 又是兩邊各自
        //       往下編。合併時照上面立的規矩整組往後挪到 15,把兩邊的舊快取一次作廢。)
        // v16: 純 keysound 合輯(一個 beatmap set 塞了 N 首不同曲子,全部 AudioFilename: virtual)現在會按譜長
        //      拆成 N 首歌,而不是併成一首只留三個難度槽(其餘整首消失);標題也改成曲名而不是整包的標籤。
        //      舊快取每個這種資料夾都只存了一首歌,不作廢就永遠看不到其他曲子。
        public const int Version = 16;

        // JsonUtility-friendly records (plain [Serializable], public fields, no UnityEngine.Object refs → safe to
        // serialize on the scan worker thread). Empty difficulty slots are simply ABSENT from `charts` — never a null
        // array element (which JsonUtility can't represent).
        [Serializable] public sealed class Chart { public int slot; public string file = ""; public int idx, notes, level, dur; public float msd; }

        [Serializable]
        public sealed class Song
        {
            public string songKey = "", title = "", artist = "", audioPath = "", imagePath = "";
            public string cdImagePath = "", motPath = "", cameraPath = "";
            public string previewAudioPath = "", dpsPath = "";   // .gn pack: its own preview clip / choreography
            public double bpm;
            public int format, previewStartMs, previewLengthMs, audioDurationSec;
            public int fileId;      // .gn pack: the official song number its art/preview/dance are named by
            public long gnSeed;     // .gn pack: LCG seed (uint32 — long so it survives JsonUtility unsigned)
            public List<Chart> charts = new List<Chart>();
        }

        [Serializable]
        public sealed class Folder
        {
            public string path = "", sig = "", group = "";   // group is the same for every song in a folder
            // 這個資料夾的跨電腦身分(SongPackId)。與 sig 同一個失效條件 —— sig 是 file-stat token,
            // 檔案有任何增刪改都會變,而 packId 只看「可傳的那些檔」的內容,所以 sig 沒變時它一定也沒變。
            public string packId = "";
            public List<Song> songs = new List<Song>();
        }

        // calc = 產生這份快取時用的**掃描設定鍵**（見 ExternalSongLibrary：難度算法 RoomConfig.difficultyCalc ＋
        // 「無理短長條收合」opt_collapseShortHolds，串成 "minacalc|ch1" 這種字串）。分槽（哪三張譜留下、誰是困難）
        // 是掃描期用那套算法決定的，所以換一套就得整份作廢重掃一次 —— 快取裡每首歌只留三張譜，第四張的資料根本不在
        // 裡面，不重讀就不可能知道它在新算法下是不是更難。收合開關同理：它會改變存起來的 `notes`（少掉幾次放開判定）。
        // 重掃只有換設定後那一次，之後照常吃快取。
        [Serializable] private sealed class CacheData { public int version = Version; public string calc = ""; public List<Folder> folders = new List<Folder>(); }

        // ---- signature: what makes a folder's parse result stale ----

        // .gn = a native SDO chart; .tsv = a pack's sdo_pack.tsv (re-running the converter must invalidate the folder,
        // since titles/seeds/art paths all come from there).
        private static readonly string[] Chartish = { ".osu", ".sm", ".gn", ".mc", ".tsv", ".ogg", ".mp3", ".wav", ".png", ".jpg", ".jpeg", ".bmp" };

        /// <summary>A token that changes iff the folder's SOURCE files change — file stats only, no content read.
        /// Generated files (the sidecar, composed <c>cd*.png</c> discs, <c>dance*.dps</c>) are skipped so runtime output
        /// never invalidates a song's own cache line. "" if the folder holds nothing scannable / can't be read (→ the
        /// caller treats "" as an always-miss and never caches it).</summary>
        public static string Signature(string folderPath)
        {
            try
            {
                var items = new List<string>();
                foreach (var f in Directory.EnumerateFiles(folderPath))
                {
                    string name = Path.GetFileName(f);
                    if (IsGenerated(name)) continue;
                    string ext = Path.GetExtension(name).ToLowerInvariant();
                    if (Array.IndexOf(Chartish, ext) < 0) continue;
                    var fi = new FileInfo(f);
                    items.Add(name.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks);
                }
                if (items.Count == 0) return "";
                items.Sort(StringComparer.Ordinal);   // FS enumeration order is not stable → sort for a deterministic token
                return Hash(string.Join("\n", items));
            }
            catch { return ""; }
        }

        // The scan's own output living in the song folder: the sidecar, and the disc / dance it names (SongSidecar's
        // CdFileName/DpsFileName produce exactly these — "cd.png"/"cd_<slug>_<hash>.png", "dance.dps"/"dance_<…>.dps").
        //
        // 這份判定搬到了 Sdo.Osu.SongPackFilter.IsGenerated,因為多人連線的「哪些檔要傳給別人」
        // 需要同一份答案,而那邊 server(net8.0)也編得到。兩處各留一份的話,將來多一種生成物
        // 只改了一邊 —— 快取這邊會變成「播完一首歌就讓自己的快取失效」,傳輸那邊會變成「把收端
        // 自己會重生的東西傳過去」,而且兩種都不會有測試抓到。
        private static bool IsGenerated(string name) => SongPackFilter.IsGenerated(name);

        private static string Hash(string s)   // FNV-1a 64-bit
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
            return h.ToString("x16");
        }

        // ---- load / save ----

        /// <summary>Read the cache into a path → folder map (case-insensitive). Empty on any failure / version bump /
        /// 難度算法換過（<paramref name="calc"/> 與寫檔時那次不同 → 分槽結果已經不能用了，見 <see cref="CacheData"/>）。</summary>
        public static Dictionary<string, Folder> Load(string cacheFilePath, string calc)
        {
            var map = new Dictionary<string, Folder>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (string.IsNullOrEmpty(cacheFilePath) || !File.Exists(cacheFilePath)) return map;
                var data = JsonUtility.FromJson<CacheData>(File.ReadAllText(cacheFilePath));
                if (data == null || data.version != Version || data.folders == null) return map;
                if (!string.Equals(data.calc ?? "", calc ?? "", StringComparison.Ordinal)) return map;
                foreach (var f in data.folders)
                    if (f != null && !string.IsNullOrEmpty(f.path)) map[f.path] = f;
            }
            catch { /* corrupt / old cache → cold start */ }
            return map;
        }

        /// <summary>Persist the current scan's folder lines (best-effort — a write failure just makes next boot cold).
        /// <paramref name="calc"/> = 這次掃描用的難度算法，下次開機用它比對（見 <see cref="Load"/>）。</summary>
        public static void Save(string cacheFilePath, List<Folder> folders, string calc)
        {
            try
            {
                if (string.IsNullOrEmpty(cacheFilePath)) return;
                var data = new CacheData { version = Version, calc = calc ?? "", folders = folders ?? new List<Folder>() };
                var dir = Path.GetDirectoryName(cacheFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(cacheFilePath, JsonUtility.ToJson(data));
            }
            catch { /* cache is best-effort */ }
        }

        // ---- mapping ExternalSong <-> cache record ----

        /// <summary>A folder cache line from a freshly-parsed (or refreshed) song list. group is taken from the songs
        /// (they all share it); an empty folder still caches as "yields nothing" so it isn't re-parsed either.</summary>
        public static Folder ToFolder(string path, string sig, List<ExternalSong> songs, string packId = null)
        {
            var f = new Folder { path = path, sig = sig, packId = packId ?? "" };
            if (songs != null && songs.Count > 0)
            {
                f.group = songs[0].Group ?? "";
                foreach (var s in songs) if (s != null) f.songs.Add(ToSong(s));
            }
            return f;
        }

        private static Song ToSong(ExternalSong s)
        {
            var o = new Song
            {
                songKey = s.SongKey ?? "", title = s.Title ?? "", artist = s.Artist ?? "",
                audioPath = s.AudioPath ?? "", imagePath = s.ImagePath ?? "",
                cdImagePath = s.CdImagePath ?? "", motPath = s.MotPath ?? "", cameraPath = s.CameraPath ?? "",
                previewAudioPath = s.PreviewAudioPath ?? "", dpsPath = s.DpsPath ?? "",
                bpm = s.Bpm, format = (int)s.Format, fileId = s.FileId, gnSeed = s.GnSeed,
                previewStartMs = s.PreviewStartMs, previewLengthMs = s.PreviewLengthMs, audioDurationSec = s.AudioDurationSec,
            };
            for (int i = 0; i < 3; i++)
            {
                var c = s.Charts[i];
                if (c == null) continue;
                o.charts.Add(new Chart { slot = i, file = c.FilePath ?? "", idx = c.ChartIndex, notes = c.NoteCount, level = c.Level, dur = c.DurationSec, msd = c.Msd });
            }
            return o;
        }

        /// <summary>Rebuild a folder's songs from its cache line (no parse). The stored group is reused verbatim: a
        /// folder can only change group by moving or by its files changing, and both change the key/signature → a miss,
        /// so a hit's group is always still valid.</summary>
        public static List<ExternalSong> FromFolder(Folder f)
        {
            var list = new List<ExternalSong>();
            if (f == null || f.songs == null) return list;
            foreach (var cs in f.songs) if (cs != null) list.Add(FromSong(cs, f.group, f.path));
            return list;
        }

        private static ExternalSong FromSong(Song o, string group, string folderPath)
        {
            var s = new ExternalSong
            {
                Group = group ?? "", FolderPath = folderPath ?? "", SongKey = o.songKey ?? "",
                Title = o.title ?? "", Artist = o.artist ?? "", Bpm = o.bpm,
                AudioPath = o.audioPath ?? "", AudioDurationSec = o.audioDurationSec, ImagePath = o.imagePath ?? "",
                Format = (SongFormat)o.format,
                CdImagePath = o.cdImagePath ?? "", MotPath = o.motPath ?? "", CameraPath = o.cameraPath ?? "",
                PreviewAudioPath = o.previewAudioPath ?? "", DpsPath = o.dpsPath ?? "",
                FileId = o.fileId, GnSeed = (uint)o.gnSeed,
                PreviewStartMs = o.previewStartMs, PreviewLengthMs = o.previewLengthMs,
            };
            if (o.charts != null)
                foreach (var c in o.charts)
                    if (c != null && c.slot >= 0 && c.slot < 3)
                        s.Charts[c.slot] = new ExternalChart
                        {
                            FilePath = c.file ?? "", ChartIndex = c.idx, NoteCount = c.notes, Level = c.level, DurationSec = c.dur, Msd = c.msd,
                        };
            return s;
        }
    }
}
