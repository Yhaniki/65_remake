using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 遊戲歌單視圖：歌名 / 曲師 / BPM / 難度 / 音符數 / 單首 offset。
    ///
    /// 資料來自 <see cref="SongTable"/>（StreamingAssets/song_table.csv，全部歌曲資料的唯一來源）；
    /// 這個類別只負責「歌單怎麼看」——k/t 兩份譜的過濾、搜尋比對、.ogg 檔名推導。
    /// 文字在 import 時就已經 GB2312 → UTF-8 解好（見 SongTable 的型別註解），runtime 只碰 Unicode。
    /// </summary>
    public static class SongCatalog
    {
        [Serializable] public class Entry
        {
            public string gn; public int fileId; public string title; public string artist;
            public float bpm = -1f;
            public int diffEasy = -1, diffNormal = -1, diffHard = -1;
            public int notesEasy, notesNormal, notesHard;
            public int durEasy, durNormal, durHard;   // seconds per difficulty
            // Etterna MinaCalc raw overall MSD per difficulty (external osu/sm/mc songs only; 0 = none — .gn packs
            // keep their own header level and are never rated). Shown instead of the osu level when
            // RoomConfig.difficultyCalc == "minacalc". See ManiaMsd / Sdo.Osu.Mina.
            public float msdEasy, msdNormal, msdHard;

            /// <summary>
            /// 屏蔽：這首歌**不出現在遊戲裡**（選歌畫面、搜尋、隨機、房間換歌），來源是 song_table.csv 的
            /// <c>hidden</c> 欄（見 <see cref="SongTable.Row.hidden"/>；k/t 兩列共用一個值，改 k 列就好）。
            ///
            /// 濾掉的地方只有一個：<see cref="Sdo.UI.Catalog.SongListModel.Curate"/> —— 遊戲裡每一份「給人看的歌單」
            /// 都是從那裡出來的。**這一列仍然在目錄裡**（<see cref="All"/> / <see cref="Get"/> 查得到），
            /// 所以舊成績、房間紀錄、字型預熱這些拿 gn 反查歌名的地方不會突然變成空白；譜面編輯器
            /// （走 <see cref="Primary"/>）也照樣打得開 —— 屏蔽是「玩家看不到」，不是「資料不見了」。
            ///
            /// 外部歌（掃描進來的 osu / StepMania / 歌包）不在這張表裡 → 一律 false。
            /// </summary>
            public bool hidden;

            // ---- external (user Songs/ folder: osu / StepMania) — absent/false for the official .gn catalog ----
            public bool external;          // true → this row is a scanned external song, not an official .gn
            public string group = "";      // group folder name (the drill-in 資料夾 category groups by this)
            public string audioPath = "";  // absolute audio file (full-song ogg/mp3/wav) for gameplay + preview
            public string imagePath = "";  // absolute SOURCE cover (jacket→banner→background); "" → placeholder disc
            public string folderPath = ""; // the song's folder — where its CD disc + sdoinfo.dat sidecar live
            public string songKey = "";    // identity WITHIN the folder ("" = the folder's only song); see ExternalSongGrouper
            /// <summary>
            /// 這首歌所在**資料夾**的跨電腦身分(<c>Sdo.Osu.SongPackId</c>)。"" = 還沒算/官方歌。
            ///
            /// 🔴 為什麼不能用 <see cref="gn"/> 或 <see cref="fileId"/> 當身分:外部歌的 gn 是
            /// 「絕對路徑的 hash」、fileId 是「掃描到的第幾首」—— **換一台電腦兩個都完全不同**。
            /// 缺歌傳檔要靠「你有沒有這個包」比對,所以一定要一個只看內容的 id。
            /// 一個資料夾可能有多首歌(<see cref="songKey"/> 分),它們共用同一個 packId。
            /// </summary>
            public string packId = "";
            public string cdPath = "";     // absolute generated CD disc image; "" → compose it from imagePath on first use
            public int chartFormat;        // 0=none, 1=osu, 2=sm, 3=gn 歌曲包 (Sdo.Osu.SongFormat)
            // ---- .gn 歌曲包 (chartFormat 3；見 Sdo.Osu.SdoPackIndex) ----
            public string previewPath = ""; // 專屬試聽短檔（包裡的 exper/<fileId>.ogg）；"" → 用全曲擷取一段
            public string dpsPath = "";     // 包裡自帶的官方編舞（DANCE/<fileId>.DPS）；"" → 開局自己生一份
            public long chartSeed;          // .gn 的 LCG 解密金鑰（uint32）；0 = 未知→退回共用 seed 池
            public string chartEasy = "", chartNormal = "", chartHard = "";   // absolute chart file per slot ("" if empty)
            public int chartIdxEasy, chartIdxNormal, chartIdxHard;            // .sm #NOTES block index per slot (osu: 0)
            public int previewStartMs = -1;   // 試聽起點(ms)：osu PreviewTime / SM #SAMPLESTART；-1 = 未指定→中段
            public int previewLengthMs;       // 試聽長度(ms)：SM #SAMPLELENGTH；0 = 未指定→預設長度
            // ---- 歌包自帶的 serverconfig（Sdo.Osu.SdoServerConfig；docs/reverse-engineering/SDO_SERVERCONFIG.md）----
            /// <summary>這首歌在歌包 serverconfig 歌曲表裡的列號；-1 = 沒有（官方歌、或包裡沒這個檔）。
            /// 官方選單反序畫，所以歌單排序用它**降冪**（表的最後一列 = 最上面）。</summary>
            public int packOrder = -1;
            /// <summary>歌包指定的標籤（0=無 1=NEW 2=HOT 3=推薦 4=古典，= Sdo.Osu.SongBadge）。</summary>
            public int badge;

            /// <summary>Absolute chart file path for difficulty d (external songs only); "" if that slot is empty.</summary>
            public string ChartPath(int d) => d <= 0 ? chartEasy : (d == 1 ? chartNormal : chartHard);
            /// <summary>.sm note-block index for difficulty d (external songs only; osu always 0).</summary>
            public int ChartIndex(int d) => d <= 0 ? chartIdxEasy : (d == 1 ? chartIdxNormal : chartIdxHard);

            /// <summary>
            /// 單首 offset（毫秒）——StepMania 的 song offset、osu 的 beatmap offset。補「這首譜跟音檔沒對齊」
            /// （譜是別人打的、音檔換過版本）。**動的是音樂＋舞蹈（DPS 掛在音樂時間軸上），不動音符/判定**：
            /// 正 = 音樂延後播放（音檔跑在譜面前面時用），負 = 提早（負得比前導還多時，ScreenGameplay 走
            /// GameRate.ScheduleMusic 從 clip 中途切入）。
            ///
            /// 來源是 song_table.csv 的 <c>offsetMs</c> 欄，跟歌名一樣是**手改**的；同一首歌的 k/t 兩列共用
            /// 同一個值（同一個音檔），寫檔的工具會自動同步兩列。沒填就是 0。
            ///
            /// 跟機器的音訊延遲**無關**（那個在 ScreenGameplay 的時鐘上自動補掉，全部歌一體適用）。
            /// </summary>
            public float offsetMs;

            /// <summary>單首**舞蹈** offset(毫秒)——跟 <see cref="offsetMs"/> 完全獨立。動的只有舞者(整段 DPS 前後挪),
            /// 音樂/音符/判定都不動。來源是外部歌資料夾 sidecar 的 <c>#DPSOFFSETMS</c>(<see cref="SongSidecarEntry.DpsOffsetMs"/>);
            /// 官方 csv 歌沒這欄 → 一律 0。正 = 舞蹈延後。給 ScreenGameplay 的 dpsOffsetMs 用(見 FrontendApp)。</summary>
            public float dpsOffsetMs;

            /// <summary>Difficulty level for d (0=easy,1=normal,2=hard); -1 if unknown.</summary>
            public int Diff(int d) => d <= 0 ? diffEasy : (d == 1 ? diffNormal : diffHard);
            public float Msd(int d) => d <= 0 ? msdEasy : (d == 1 ? msdNormal : msdHard);

            /// <summary>難度數字的「顯示值」——依 <see cref="Sdo.Settings.RoomConfig.difficultyCalc"/> 決定：
            /// minacalc → round(max(MSD^1.9×0.1, MSD))，上限 999（<see cref="Sdo.Osu.ManiaMsd.ToLevel"/>）；
            /// 否則 osu 星數×7 等級（<see cref="Diff"/>）。**選歌 / 房間 / 遊戲一律用這個**,才不會切畫面時數字跳掉。
            ///
            /// **新的等級算法只套用在「難度得自己重算」的外部譜面(osu / StepMania / Malody)。.gn 一律不動**：
            /// 官方 DATA/MUSIC 的 .gn 是 <c>external == false</c>，外部資料夾裡的 .gn 歌包則是
            /// <c>chartFormat == SongFormat.Gn</c>——兩者都自帶檔頭寫死的難度(<see cref="Sdo.Osu.GnHeader"/>)，
            /// 那是這首歌本來的等級，換算計器不該把它改掉。</summary>
            public int DisplayLevel(int d)
            {
                if (external && chartFormat != (int)Sdo.Osu.SongFormat.Gn &&
                    Sdo.Settings.RoomConfig.difficultyCalc == "minacalc")
                {
                    float m = Msd(d);
                    if (m > 0f) return Sdo.Osu.ManiaMsd.ToLevel(m);
                }
                return Diff(d);
            }
            public int NoteCount(int d) => d <= 0 ? notesEasy : (d == 1 ? notesNormal : notesHard);
            public int DurationSec(int d) => d <= 0 ? durEasy : (d == 1 ? durNormal : durHard);

            /// <summary>Whether difficulty d has a real, playable chart. Judged by the actual NOTE COUNT
            /// (an empty chart has 0 notes), NOT the level field — some songs store level 0 for a
            /// difficulty that carries no notes (e.g. 動畫歌曲串燒 sdom2140k: easy=3417 notes, normal/hard=0).
            /// Those empty difficulties are greyed out / non-selectable in song-select.</summary>
            public bool HasChart(int d) => NoteCount(d) > 0;

            // 一個難度槽的完整內容（重排時整組搬，不能只搬數字 —— 譜面路徑/#NOTES 索引跟著走才不會選到別張譜）。
            private struct Slot
            {
                public int Diff, Notes, Dur, ChartIdx;
                public float Msd;
                public string Chart;
                public static Slot Empty => new Slot { Diff = -1, Chart = "" };   // 同 Entry 的欄位預設（-1 = 未知等級）
            }

            private Slot ReadSlot(int d)
            {
                if (d <= 0) return new Slot { Diff = diffEasy, Notes = notesEasy, Dur = durEasy, ChartIdx = chartIdxEasy, Msd = msdEasy, Chart = chartEasy };
                if (d == 1) return new Slot { Diff = diffNormal, Notes = notesNormal, Dur = durNormal, ChartIdx = chartIdxNormal, Msd = msdNormal, Chart = chartNormal };
                return new Slot { Diff = diffHard, Notes = notesHard, Dur = durHard, ChartIdx = chartIdxHard, Msd = msdHard, Chart = chartHard };
            }

            private void WriteSlot(int d, Slot s)
            {
                if (d <= 0) { diffEasy = s.Diff; notesEasy = s.Notes; durEasy = s.Dur; chartIdxEasy = s.ChartIdx; msdEasy = s.Msd; chartEasy = s.Chart ?? ""; }
                else if (d == 1) { diffNormal = s.Diff; notesNormal = s.Notes; durNormal = s.Dur; chartIdxNormal = s.ChartIdx; msdNormal = s.Msd; chartNormal = s.Chart ?? ""; }
                else { diffHard = s.Diff; notesHard = s.Notes; durHard = s.Dur; chartIdxHard = s.ChartIdx; msdHard = s.Msd; chartHard = s.Chart ?? ""; }
            }

            /// <summary>
            /// 把三個難度槽重排成 easy ≤ normal ≤ hard —— **依 <see cref="DisplayLevel"/>，也就是目前那套難度算法**
            /// （<see cref="Sdo.Settings.RoomConfig.difficultyCalc"/>）。
            ///
            /// 掃描期是用 osu 等級把譜面分進三個槽的（<see cref="Sdo.Osu.ExternalDifficultyPicker"/>）。兩套算法對
            /// 同一首歌的高低順序不見得一樣，所以切成 minacalc 之後,困難格的數字可能反而比普通格小 —— 選了哪套算法
            /// 就整體都照那套，包含「哪張譜是困難」。重排只是換槽（同一首歌的同三張譜，路徑/索引跟著搬），
            /// 不必重掃、也不動任何檔案。
            ///
            /// 排法沿用掃描期那支 hard-first 的純函式：等級高的先拿 hard，同級用音符數多的，再同就保持原順序；
            /// 譜不足三張時**低位留空**（跟掃描期一致 —— 只有一張譜就是「只有困難」，易/普灰掉）。
            ///
            /// **.gn 一律不重排**（官方 DATA/MUSIC 的歌、以及外部資料夾裡的 .gn 歌包）：那三格是譜面自己的
            /// 易/普/難，不是算出來的名次 —— 有些歌只有簡單格有譜（例：sdom2140k easy 3417 notes、其餘 0），
            /// 拿去 hard-first 重排會把它搬進困難格。這跟 <see cref="DisplayLevel"/> 不動 .gn 是同一條規則。
            /// </summary>
            public void SortSlotsByDisplayLevel()
            {
                if (!external || chartFormat == (int)Sdo.Osu.SongFormat.Gn) return;
                // 顯示用的就是掃描期分槽用的那套（osu 等級）→ 槽位本來就對，一個位元組都不要動。
                if (Sdo.Settings.RoomConfig.difficultyCalc != "minacalc") return;

                var lv = new List<int>(3); var notes = new List<int>(3); var data = new List<Slot>(3);
                for (int d = 0; d < 3; d++)
                {
                    if (!HasChart(d)) continue;
                    // 有譜卻沒 MSD（空譜/太短算不出來 → DisplayLevel 退回 osu 等級）：整首歌就會拿兩種尺度互比，
                    // 排出來只會更亂 → 維持掃描期的順序。
                    if (Msd(d) <= 0f) return;
                    lv.Add(DisplayLevel(d)); notes.Add(NoteCount(d)); data.Add(ReadSlot(d));
                }
                if (data.Count < 2) return;
                // 已經是遞增就不要動：同分時 Assign 會照音符數重排，那種「同級但換位」的搬動是沒必要的雜訊。
                bool ascending = true;
                for (int i = 1; i < lv.Count; i++) if (lv[i] < lv[i - 1]) { ascending = false; break; }
                if (ascending) return;

                var order = Sdo.Osu.ExternalDifficultyPicker.Assign(lv, notes);   // [easyIdx, normalIdx, hardIdx]
                for (int d = 0; d < 3; d++) WriteSlot(d, order[d] >= 0 ? data[order[d]] : Slot.Empty);
            }
        }

        /// <summary>Sanity bound for a hand-typed offsetMs. A stray extra digit (30 -> 3000000) would otherwise
        /// push the music start minutes away / off the end of the clip. ±60 s: imported charts (osu/StepMania)
        /// often carry a long silent lead-in or plainly mis-cut audio, so several seconds is legitimate — this
        /// only guards against runaway typos. Kept in lock-step with the editor nudge clamp (ChartEditorScreen).</summary>
        public const float MaxOffsetMs = 60000f;

        private static Dictionary<string, Entry> _byGn;   // key = lowercase .gn filename
        private static List<Entry> _all;                  // in file order
        private static List<Entry> _primary;              // k-only view of _all (lazy)

        /// <summary>
        /// 目錄內容的版本號 —— 每次外部歌那一半被改動（<see cref="RegisterExternal"/> 真的加了東西、
        /// <see cref="ReplaceExternal"/>、<see cref="Invalidate"/>）就 +1。
        ///
        /// 誰需要它：把目錄**複製一份**留著用的地方（<see cref="Sdo.UI.Catalog.SongListModel"/> 是 snapshot，
        /// 選歌畫面在開機時建一次就一直用）。線上房間下載別人的歌會在**畫面之外**重掃歌庫
        /// （NetSongTransfer.ImportCo → ExternalSongLibrary.ScanAndRegisterCo），那份 snapshot 不會自己更新 →
        /// 換自己當房主去選歌時，搜尋整個看不到剛下載的歌。比版本號就知道該不該重建。
        /// </summary>
        public static int Revision { get; private set; }

        /// <summary>All catalog entries in file order (empty if no table). Includes BOTH chart variants —
        /// for a browsable list you almost always want <see cref="Primary"/> instead.</summary>
        public static IReadOnlyList<Entry> All { get { EnsureLoaded(); return _all; } }

        /// <summary>
        /// 每首歌在原始資料裡有**兩份譜**：sdomNNNN<b>k</b>.gn（鍵盤）與 sdomNNNN<b>t</b>.gn（毯子）。
        /// 兩者共用同一個標題／曲師／音檔，但難度與音符數不同 —— 例 sdom0001：k = LV 3/4/5、easy 510 notes；
        /// t = LV 1/3/5、easy 284 notes（毯子譜比較鬆）。
        ///
        /// 重製版是**純鍵盤**，所以任何「給人瀏覽的清單」都只該出現 k：不濾的話目錄的 4325 筆會讓每首歌
        /// 在清單裡出現兩次（2166 首 × 2），而且因為兩列的歌名是同步的，連標題都一模一樣，
        /// 看起來就是整份清單重複。
        ///
        /// <see cref="All"/> 刻意**不濾**：以 gn 反查標題／曲師、字型預熱都需要兩種變體都在。
        /// </summary>
        public static bool IsPrimaryVariant(string gnPathOrName)
        {
            if (string.IsNullOrEmpty(gnPathOrName)) return false;
            var name = Path.GetFileName(gnPathOrName).ToLowerInvariant();
            if (name.EndsWith(".gn")) name = name.Substring(0, name.Length - 3);
            return name.Length > 0 && name[name.Length - 1] == 'k';
        }

        /// <summary>只有鍵盤譜（k）的清單，檔案順序。**所有給人瀏覽的清單都該用這個**（選歌畫面、譜面編輯器），
        /// 不是 <see cref="All"/>。
        ///
        /// 這裡**不濾**被屏蔽的歌（<see cref="Entry.hidden"/>）—— 它只是「k 列」這個視圖。玩家看得到的那份歌單
        /// 是 <see cref="Sdo.UI.Catalog.SongListModel.Curate"/>，屏蔽在那裡濾；譜面編輯器要的正好相反：
        /// 一首歌會被屏蔽多半就是它有問題（缺音檔、offset 沒調），編輯器得看得到它才修得了。</summary>
        public static IReadOnlyList<Entry> Primary
        {
            get
            {
                EnsureLoaded();
                if (_primary == null)
                {
                    _primary = new List<Entry>();
                    foreach (var e in _all) if (e != null && IsPrimaryVariant(e.gn)) _primary.Add(e);
                }
                return _primary;
            }
        }

        /// <summary>Look up by a .gn path or filename (case-insensitive). Null if absent / no table.</summary>
        public static Entry Get(string gnPathOrName)
        {
            if (string.IsNullOrEmpty(gnPathOrName)) return null;
            EnsureLoaded();
            var key = Path.GetFileName(gnPathOrName).ToLowerInvariant();
            return _byGn.TryGetValue(key, out var e) ? e : null;
        }

        public static string Title(string gnPathOrName) => Get(gnPathOrName)?.title;
        public static string Artist(string gnPathOrName) => Get(gnPathOrName)?.artist;

        /// <summary>Merge scanned external songs (user Songs/ folder — osu / StepMania) into the catalog, AFTER the
        /// official catalog is loaded. Entries whose gn already exists are skipped (idempotent). The song-select
        /// list picks these up because <see cref="All"/> now includes them (browsed via the 資料夾 group category;
        /// they are excluded from the 全部 tab — see SongSelectScreen.CategoryBase).</summary>
        public static void RegisterExternal(IEnumerable<Entry> entries)
        {
            if (entries == null) return;
            EnsureLoaded();
            foreach (var e in entries)
            {
                if (e == null || string.IsNullOrEmpty(e.gn)) continue;
                var key = e.gn.ToLowerInvariant();
                if (_byGn.ContainsKey(key)) continue;
                _byGn[key] = e;
                _all.Add(e);
                _primary = null;   // external gns end in 'k' → they belong to the Primary view; drop its cache
                Revision++;        // 目錄變了 → 拿著 snapshot 的畫面該重建（見 Revision）
            }
        }

        /// <summary>Swap the WHOLE external half of the catalog for a fresh scan's entries: every previously
        /// registered <c>external</c> row is dropped first, then <paramref name="entries"/> registered. This is what a
        /// RE-scan needs (see ExternalSongLibrary.ScanAndRegisterCo, driven at boot and by 選歌 → 分類瀏覽 → 更新):
        /// <see cref="RegisterExternal"/> alone only ever adds, so a song deleted from disk — or one whose gn changed
        /// because its folder/audio was renamed — would linger in the list pointing at a file that is gone.
        ///
        /// Official (.gn) rows are untouched. Entry OBJECTS are replaced wholesale, so anything holding an Entry
        /// reference across a re-scan must re-resolve it by gn (<see cref="Get"/>) — gns are content-derived and
        /// stable, which is also why favourites and the restored selection survive.</summary>
        public static void ReplaceExternal(IEnumerable<Entry> entries)
        {
            EnsureLoaded();
            if (DropExternalRows(_all, _byGn) > 0) _primary = null;
            // 無條件 +1：就算掃出來的歌跟上一次一模一樣，Entry **物件**已經整批換掉了 —— 誰還握著舊物件
            // （選歌畫面的 SongListModel snapshot、_bucketSongs）就等於握著孤兒，必須重建（見 Revision）。
            Revision++;
            RegisterExternal(entries);
        }

        /// <summary>Remove every <c>external</c> row from a catalog's two views, keeping the official ones and their
        /// order; returns how many were dropped. Pure (no IO, no globals) — the testable half of
        /// <see cref="ReplaceExternal"/>.</summary>
        public static int DropExternalRows(List<Entry> all, Dictionary<string, Entry> byGn)
        {
            int n = all != null ? all.RemoveAll(e => e != null && e.external) : 0;
            if (byGn == null) return n;
            var stale = new List<string>();
            foreach (var kv in byGn) if (kv.Value != null && kv.Value.external) stale.Add(kv.Key);
            foreach (var k in stale) byGn.Remove(k);
            return n;
        }

        /// <summary>這首歌有沒有被屏蔽（song_table.csv 的 <c>hidden</c> 欄）；查不到 = false。見 <see cref="Entry.hidden"/>。</summary>
        public static bool IsHidden(string gnPathOrName) => Get(gnPathOrName)?.hidden ?? false;

        /// <summary>這首譜的單首 offset（毫秒）；沒設過 = 0。見 <see cref="Entry.offsetMs"/>。</summary>
        public static float OffsetMs(string gnPathOrName) => Get(gnPathOrName)?.offsetMs ?? 0f;

        /// <summary>這首譜的單首**舞蹈** offset（毫秒）；跟音樂 offset 獨立，沒設過 = 0。見 <see cref="Entry.dpsOffsetMs"/>。</summary>
        public static float DpsOffsetMs(string gnPathOrName) => Get(gnPathOrName)?.dpsOffsetMs ?? 0f;

        private static float ClampOffsetMs(float ms)
            => float.IsNaN(ms) ? 0f : (ms < -MaxOffsetMs ? -MaxOffsetMs : (ms > MaxOffsetMs ? MaxOffsetMs : ms));

        /// <summary>
        /// 這首歌是否符合搜尋字串 —— 比對 標題／曲師／gn 檔名／<b>fileId 編號（只有官方歌）</b>，
        /// 全部是 case-insensitive 子字串。空字串／null／全空白 → 一律符合（＝不過濾）。
        /// 純邏輯、不碰硬碟，給歌單搜尋用（見 SongCatalogSearchTests）。
        ///
        /// fileId 是歌曲編號，封面圖（<c>NNNN.PNG</c>）、試聽（<c>exper/NNNN.ogg</c>）、編舞（<c>DANCE/NNNN.DPS</c>）都用它，
        /// 跟 gn 檔名裡的號碼**不一樣**：sdom0001<b>k</b>.gn 的 fileId 是 10001、sdom0001<b>t</b>.gn 是 1。
        /// 所以「照封面上的編號找歌」只有這個欄位辦得到 —— 拿編號去比 gn 子字串是找不到的。
        ///
        /// 🔴 <b>外部歌（<see cref="Entry.external"/>）不吃編號比對</b>：它們沒有官方編號，
        /// <c>fileId</c> 是掃描時發的流水號 <c>-(第幾首 + 1000)</c>（見 ExternalSongLibrary.ToEntry）——
        /// 換一台電腦就完全不同、玩家也從來沒看過這個數字。放著比對只會讓「打 1002 找官方 1002 號」
        /// 順手撈出 fileId = -1002 的那首外部歌（負號中間的數字照樣是子字串）。編號查只對官方歌有意義。
        /// </summary>
        public static bool Matches(Entry e, string query)
        {
            if (e == null) return false;
            if (string.IsNullOrWhiteSpace(query)) return true;
            query = query.Trim();
            return Has(e.title, query) || Has(e.artist, query) || Has(e.gn, query)
                || (!e.external
                    && e.fileId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                           .IndexOf(query, StringComparison.Ordinal) >= 0);   // 編號一律 ASCII 數字 → Ordinal
        }

        private static bool Has(string hay, string needle)
            => !string.IsNullOrEmpty(hay) && hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>丟掉快取（改過 song_table.csv 之後用）。下次存取重新從 <see cref="SongTable"/> 建。</summary>
        public static void Invalidate()
        {
            _byGn = null; _all = null; _primary = null;
            Revision++;
            SongTable.Invalidate();
        }

        private static void EnsureLoaded()
        {
            if (_byGn != null) return;
            _byGn = new Dictionary<string, Entry>(StringComparer.Ordinal);
            _all = new List<Entry>();
            _primary = null;
            foreach (var r in SongTable.Rows)
            {
                var e = FromRow(r);
                if (e == null) continue;
                _byGn[e.gn] = e; _all.Add(e);
            }
        }

        /// <summary><see cref="SongTable.Row"/> → 歌單 entry（純轉換，有測試）。null / 沒有 gn → null。</summary>
        public static Entry FromRow(SongTable.Row r)
        {
            if (r == null || string.IsNullOrEmpty(r.gn)) return null;
            return new Entry
            {
                gn = r.gn.ToLowerInvariant(),
                fileId = r.fileId,
                title = r.title,
                artist = r.artist,
                bpm = r.bpm,
                diffEasy = At(r.levels, 0, -1), diffNormal = At(r.levels, 1, -1), diffHard = At(r.levels, 2, -1),
                notesEasy = At(r.noteCounts, 0, 0), notesNormal = At(r.noteCounts, 1, 0), notesHard = At(r.noteCounts, 2, 0),
                durEasy = At(r.durations, 0, 0), durNormal = At(r.durations, 1, 0), durHard = At(r.durations, 2, 0),
                offsetMs = ClampOffsetMs(r.offsetMs),
                hidden = r.hidden,
            };
        }

        private static int At(int[] a, int i, int fallback) => a != null && i < a.Length ? a[i] : fallback;

        /// <summary>
        /// Full-song audio filename for a chart gn: "sdom2784k.gn" -> "sdom2784.ogg" (the two charts of a
        /// song share one .ogg, which is named after the stem). Null for an empty/degenerate name.
        ///
        /// Single source of truth for gameplay (FrontendApp) AND the song-select preview fallback, which
        /// used to derive it two different ways — a regex 'sdom\d+' vs this stem. They agree for a plain
        /// sdomNNNN{k,t}.gn, but not for a hand-added chart whose stem carries a suffix (sdom1234_1k.gn,
        /// used to slot in a song whose number is already taken): the regex would silently resolve it to
        /// sdom1234.ogg — the OTHER song's audio.
        /// </summary>
        public static string MainOggName(string gnPathOrName)
        {
            var stem = Stem(gnPathOrName);
            return stem.Length > 0 ? stem + ".ogg" : null;
        }

        /// <summary>gn filename/path -> chart-pair stem, e.g. "sdom0001k.gn" / "SDOM0001T" -> "sdom0001".
        /// The k/t suffix distinguishes the two charts of one song; both share a title.</summary>
        public static string Stem(string gnPathOrName)
        {
            var name = Path.GetFileName(gnPathOrName ?? string.Empty).ToLowerInvariant();
            if (name.EndsWith(".gn")) name = name.Substring(0, name.Length - 3);
            if (name.Length > 0 && (name[name.Length - 1] == 'k' || name[name.Length - 1] == 't'))
                name = name.Substring(0, name.Length - 1);
            return name;
        }
    }
}
