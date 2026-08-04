using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using Sdo.Osu;
using Sdo.Ruleset;
using Sdo.Settings;

namespace Sdo.Game
{
    /// <summary>
    /// SDO-faithful playable screen in the original 800×600 frame (DdrGamePlay.xml), art loaded at
    /// runtime from the extracted game tree (SdoExtracted). Geometry = EXACT values decoded from the
    /// decompilation (doc/GAMEPLAY_SCREEN_ANATOMY.md). Iteratively verified via the headless capture
    /// (Tests/PlayMode/CaptureTest -> test-output/gameplay/play-capture.png). Self-boots.
    /// </summary>
    public sealed partial class ScreenGameplay : MonoBehaviour
    {
        // ---- tunables ----
        // HP system level 0/1/2 (DAT_00674f04+0x75; NOT the chart difficulty). Deltas per SDO_HP_FORMULA.md:
        // L0 miss -50 (dies in 23), L1 -40 (29), L2 -30 (39). Official observed = 39 misses → level 2
        // (Perfect +2 / Cool +1 / Bad -5 / Miss -30): lighter miss AND proportionally lighter Bad drain.
        public int healthLevel = 2;
        public bool autoPlay = true;
        // F7 打拍音:譜面上每個音符響一聲 click。F8 自動打擊。兩者都是「開發/練習用開關」,按下去後**下一首歌會延續**
        // (靜態欄位 = 同一次執行內有效),但**不寫進設定檔** —— 重開遊戲就回到預設。FrontendApp 每次開局都會把
        // autoPlay 設回 false(正常遊玩),所以 F8 的延續要靠 s_autoPlay 這個「玩家按過才存在」的覆寫值在 Start 蓋回去。
        public bool assistTick;
        private static bool s_assistTick;
        private static bool? s_autoPlay;   // null = 玩家這次執行還沒按過 F8 → 照 FrontendApp/Inspector 給的值
        // DEBUG: force a grade on every manual hit (-1 = real timing window). F4 panel selects it.
        public int forcedJudge = -1;
        private static readonly string[] ForceJudgeLabels = { "Real", "Perfect", "Cool", "Bad", "Miss" };
        // Note scroll = osu!mania-style (Sequential + relative beat-length scaling) at a FIXED base tempo:
        // the base speed is the SAME for every song (NOT scaled by the song's BPM), calibrated with the
        // official px/s = BPM×speed×1.6 at referenceBpm (config.ini [Room] scrollBaseBpm, 預設 130 =
        // ManiaScroll.DefaultReferenceBpm). Mid-song BPM changes /
        // osu SV still vary the scroll locally (see ManiaScroll). scrollSpeedMul = the room "速度" step
        // (RoomConfig.speedSteps), set by FrontendApp from the session. constantScroll = osu "Constant Speed" mod
        // (kill all variation) — wired to OPTION 進階「歌曲變速」關閉 (GameplaySettings.songSpeed == false).
        public float scrollSpeedMul = 2.5f;   // 速度 step (1.0..8.0); FrontendApp wires GameSession.Speed in
        // Room win2 "note" selection (GameSession.NoteType) → the gameplay skin applied at boot via SelectSkin.
        // -2 = unset (standalone/F4 boot: keep stock); -1 = 隨機 (random skin); 0..11 = the specific note skin
        // (0..10 = the 2D skins in NoteTypeEftSuffix order, 11 = the 3D hiteft3D skin) — same order as the room's NoteEftArt.
        public int roomNoteType = -2;
        // base-tempo anchor when NOT following the song's BPM。預設＝ config.ini [Room] scrollBaseBpm（手改可整體調快/調慢
        // 所有歌的下落速度；預設 130 = ManiaScroll.DefaultReferenceBpm）。遊戲中 F 面板的滑桿仍可即時覆蓋（不寫回檔案）。
        public float referenceBpm = Sdo.Settings.RoomConfig.scrollBaseBpm;
        public bool scrollFollowsSongBpm = false; // true = base speed follows the song's own BPM (official px/s = BPM×speed×1.6); false = fixed referenceBpm for every song
        public bool constantScroll = false;   // true = ignore BPM/SV variation (perfectly linear scroll)
        public bool useMusicStartOffset = true;  // true = start the music (and the dancer) at the chart's type-10 音樂起止 marker (skip the leading count-in measure so notes line up with the song)
        public float judgeLineY = 70f;        // receptor / hit line Y (design px). UPSCROLL: notes rise to it.
        // 判定線的視覺偏移（設計 px）：完美時機的音符落在 judgeLineY + judgeOffsetY，受擊線圖本身不動。
        // 純視覺（不改判定時間 —— 那是 GameplayClock.OffsetMs 的事）。預設 0 = 正中受擊線；用編輯器的打拍測試調。
        public float judgeOffsetY = Sdo.Settings.RoomConfig.judgeOffsetY;

        // 依名次調整站位（config.ini [Room] rankBasedFormation，預設開）：開＝比賽中即時第一名滑進隊形的
        // 領隊格（中央前排，導播鏡頭的錨點）；關＝每個人整場站在房間座位順序的格子，名次變動不換位。
        // 純視覺、每台各自生效（不進網路協定，也不影響判定/分數/名次）。見 TickDancerSlots。
        public bool rankBasedFormation = Sdo.Settings.RoomConfig.rankBasedFormation;

        /// <summary>
        /// 單首歌的 offset（毫秒，<see cref="SongCatalog.Entry.offsetMs"/> ← song_table.csv）：補「這首譜跟音檔沒對齊」。
        /// <b>動的是音樂，不是音符</b>（同 StepMania：你在調的是音樂相對譜面的位置）—— 它加在音樂的
        /// count-in 上（<see cref="MusicCountInSec"/>），所以譜面時鐘/音符/判定線都不動，只有音樂前後挪。
        /// 正 = 音樂往後（延後播放）＝ 音符相對音樂變早。
        /// 由 FrontendApp（開局）或譜面編輯器（F11/F12 即時調）設。
        /// </summary>
        public float songOffsetMs;

        /// <summary>
        /// 單首歌的**舞蹈** offset（毫秒）—— 跟 <see cref="songOffsetMs"/> **完全獨立**。動的只有舞者：整段 DPS
        /// 往前/往後挪（<see cref="_danceStartSec"/> 加它），音樂/音符/判定都不受影響。給「舞蹈跟音樂沒對齊、
        /// 但音樂本身跟音符是對的」這種情況單獨微調用。來源是外部歌 sidecar 的 <c>#DPSOFFSETMS</c>（預設 0）。
        /// 正 = 舞蹈延後。由 FrontendApp（開局，<see cref="SongCatalog.DpsOffsetMs"/>）設。
        /// </summary>
        public float dpsOffsetMs;

        /// <summary>
        /// **全曲共用**的音樂 offset（毫秒）—— 已停用，設 0。
        /// 曾經以為官方那批 k.gn 的譜面時間軸整體跟音檔差了固定一段（每首都一樣），所以放一個全域 −25。
        /// 後來逐首手校（sdom2675 之後）發現**沒有這種全域常數**，每首的殘差各不相同，該由各自的
        /// <see cref="songOffsetMs"/>（song_table.csv 的 offsetMs）處理。於是把全域歸零，
        /// 並把原本那 −25 烘進 sdom2675 之後每首的 offsetMs，讓那批已校過的歌行為不變。
        /// 保留這個常數只為讓 <see cref="MusicCountInSec"/> 的算式與排程/波形路徑維持單一入口。
        /// </summary>
        public const double GlobalSongOffsetMs = 0.0;

        // 全域 offset（config.ini [Room] globalOffsetMs）：**使用者的個人偏好**，預設 0。
        // 機器的音訊延遲已經由下面兩段自動補掉了，這裡只留給「我就是想打早一點/晚一點」的人。
        // 正 = 譜面時鐘往前推 → 同一下打擊的 delta 變大（判得比較晚）→ 整體打太早的人要調正的。用打拍測試量。
        private float _globalOffsetMs = Sdo.Settings.RoomConfig.globalOffsetMs;

        // ---- 輸出延遲補償（StepMania 的核心作法）----
        //
        // StepMania 的歌曲時鐘**不是**「我送了多少音訊出去」，而是去問音效卡「**現在正從喇叭出來的是第幾個取樣**」
        // （DirectSound 的播放游標 → RageSound::GetPositionSecondsInternal → pos_map.Search → GameState::m_fMusicSeconds）。
        // 因此緩衝區裡那一大段「已混音、還沒出喇叭」的音訊，從來不算進時鐘裡 —— 輸出延遲在判定路徑上**自動抵銷**。
        // 它敢把 Windows DSound 的 writeahead 開到 8192 frames（186ms）而 GlobalOffsetSeconds 預設 0，就是這個原因；
        // GetPlayLatency() 只拿去「提前排程」打拍音（ScreenGameplay.cpp:1220-1225），從不進判定。
        //
        // Unity 沒有播放游標 API —— AudioSettings.dspTime 是**混音**游標，它領先喇叭一整個輸出緩衝。所以我們用「算」的
        // 補回同一件事：把譜面時鐘往回推那段距離，讓 CurrentMs 代表「此刻正在出喇叭的那個譜面時間」。
        // ChartSecondsFromDsp = rate×(dsp − anchor) + countIn，所以 dsp 退 L 秒 ⇔ 譜面時間退 rate×L 秒
        // —— 一個常數，直接疊在 clock offset 上就行，不必動任何錨點。
        //
        // 關鍵不變式：**排程（PlayScheduled）一律走原始 dspTime，只有「讀時鐘」走播放游標。** 兩者若一起搬就抵銷掉了。
        // 於是：譜面 T 的打拍音排在 raw dsp(T) → L 秒後出喇叭 → 那一刻時鐘剛好讀到 T；音符也在那一刻通過判定線。
        // 聽到的 click、看到的音符、判定的時間三者對齊。
        //
        // L 由兩段組成：
        private double _outputLatencySec;   // ① FMOD 的混音緩衝（bufferLength × numBuffers / sampleRate）—— 算得到，隨設定變

        /// <summary>
        /// ② 混音緩衝**以外**、Unity 沒有 API 看得到的那段輸出延遲 —— 只能實測後寫死。
        ///
        /// 這個值只補**聲音輸出時鐘**。編輯器波形直接讀 <c>AudioClip.GetData</c> 的原始 PCM，
        /// 不經混音、DSP buffer、驅動或喇叭；因此改這個值絕不能移動波形。格式特有的視覺規則
        /// 另由 <see cref="WaveformVisualOffsetMsFor(int)"/> 處理。
        ///
        /// 本機用打拍測試量出來的（打拍測試面板 F2，聽節拍器打 100 下取中位數）：
        ///
        /// | DSP buffer | ① 算得到的 | 聽覺中位數 | 看音符打拍中位數 | 殘差 = ② |
        /// |---|---|---|---|---|
        /// | 1024×4 @48k | 85.3 ms | +32.8 | +1.6 | **31.2 ms** |
        /// | 512×4 @48k | 42.7 ms | +33.2 | +4.4 | **28.8 ms** |
        ///
        /// **殘差不隨 buffer 改變**（若它正比於 FMOD 緩衝，512 下該掉到 15.6ms，實測沒掉）→ 確認是 buffer 以外的固定量。
        /// 於是兩種 buffer 下需要的補償都是同一個 ~33ms，<c>m_DSPBufferSize</c> 從此純粹是「會不會爆音」的取捨，
        /// 換它不必重新校時 —— 這正是「時鐘讀播放游標」換來的。
        ///
        /// 取 33（＝聽覺中位數）而不是 31：那 ~2ms 的差是**輸入延遲**（Update 輪詢 + 鍵盤），
        /// 一併吸收掉，跟著音樂打的人 delta 才會真的落在 0。這個校準只回答「譜面時鐘顯示 T 時，
        /// 喇叭是否正在播放 T」；離線 PCM 波形不參與，也不能拿它反推 decoder trim。
        ///
        /// osu!lazer 的處境與解法完全相同 —— 它也量不到，也是寫死一個平台常數再讓使用者微調
        /// （<c>FramedBeatmapClock.WINDOWS_BASE_AUDIO_OFFSET = 15</c> / 實驗性 WASAPI 再 −25）。
        /// 差別只在我們這個數字是這台機器實測的，不是猜的。別台機器有出入 → 只校聲音/判定時鐘；
        /// 波形仍保持原始 PCM 加格式明定的視覺位移。
        /// </summary>
        private const double DriverLatencyMs = 33.0;

        private static double MeasureOutputLatencySec()
        {
            AudioSettings.GetDSPBufferSize(out int bufferLength, out int numBuffers);
            int rate = AudioSettings.outputSampleRate;
            if (bufferLength <= 0 || numBuffers <= 0 || rate <= 0) return 0.0;
            return (double)bufferLength * numBuffers / rate;
        }

        /// <summary>譜面時鐘要往回推的總量（真實秒）＝ ① 算得到的混音緩衝 ＋ ② 量不到的驅動延遲常數。</summary>
        private double ClockLatencySec => _outputLatencySec + DriverLatencyMs / 1000.0;

        /// <summary>同上，換算成**譜面毫秒**（流速 r 時，真實 L 秒 = r×L 秒的譜面時間）。</summary>
        private double ClockLatencyChartMs => ClockLatencySec * 1000.0 * _musicRate;

        // 時鐘 offset = 使用者偏好 − 輸出延遲（後者就是「讀播放游標而不是混音游標」的等價量）。
        // rate 會變 → SetGameRate 之後必須重算；換音訊裝置也會變 → OnAudioConfigurationChanged 重量。
        private void ApplyClockOffset() => _clock.OffsetMs = _globalOffsetMs - ClockLatencyChartMs;

        private void OnAudioConfigChanged(bool deviceChanged)
        {
            _outputLatencySec = MeasureOutputLatencySec();   // 換裝置 → buffer/取樣率可能全變
            ApplyClockOffset();
        }

        // AudioSettings.OnAudioConfigurationChanged 是**靜態**事件：編輯器每按一次 F2 就重建一次 ScreenGameplay，
        // 不解除的話舊實例會留在委派鏈上（洩漏；裝置一變就對著已銷毀的物件呼叫）。
        private void OnDestroy()
        {
            // 聊天框自己會關掉 IME —— 不關的話回到房間時輸入法還開著,房間的按鍵會被吃掉。
            if (_chat != null) { _chat.Destroy(); _chat = null; }
            EditorRestoreCameraShift();   // 編輯器把相機推下去過的話要推回來（相機可能是前端共用的那一台）
            DisposeOsuKeysounds();
            AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigChanged;
            if (_noteVisualRoot) Destroy(_noteVisualRoot.gameObject);   // tear down the pooled note visuals (root-level like the old per-note objects) with this screen
        }

        /// <summary>
        /// 音樂真正的 count-in（秒）＝ 譜面的 type-10 無聲數拍 ＋ 單首 offset ＋ 全曲共用 offset。
        /// dsp ↔ 譜面時間的換算**一律**用這個值（AudioChartSeconds / 變速 / 暫停 / seek / 打拍音排程），
        /// 少用一處就會音畫不同步。錨點與 count-in 一起搬時，「譜面時間 → dsp」的映射是不變的
        /// （anchor' = anchor + Δ/rate），所以打拍音仍然對在音符上，只有音樂本身被挪走。
        /// </summary>
        private double MusicCountInSec => _musicStartDelaySec + (songOffsetMs + GlobalSongOffsetMs) / 1000.0;
        private ManiaScroll _scroll;          // built from _map after LoadChart (BuildScroll)
        // Chart/audio paths. Normally set by FrontendApp from the song selection; left EMPTY by default so no
        // absolute path is baked in. When this component is run standalone (dev), Start() fills a default from
        // SdoExtracted.MusicDir (see ResolveDevDefaults).
        public string gnPath = "";   // official chart (e.g. <MusicDir>/sdom1435K.gn)
        public string oggPath = "";  // matching song audio (e.g. <MusicDir>/sdom1435.ogg); ogg/mp3/wav
        public int difficulty = 2;            // 0=easy 1=normal 2=hard
        // External chart (user Songs/ folder). When chartFormat != 0 LoadChart parses chartPath INSTEAD of gnPath:
        // 1=osu (chartPath = one .osu file), 2=sm (chartPath = a .sm, chartIndex = which #NOTES block),
        // 3=gn 歌曲包 (chartPath = a .gn holding all three difficulties, chartIndex = which one, chartSeed = its key).
        public string chartPath = "";
        public int chartIndex;
        public int chartFormat;               // 0=official .gn, 1=osu, 2=sm, 3=gn 歌曲包, 4=Malody .mc (Sdo.Osu.SongFormat)
        public long chartSeed;                // chartFormat 3: 該 .gn 的 LCG 金鑰（0 = 未知→只用共用 seed 池）
        /// <summary>可選：換掉「解 mp3」這一步（路徑, 對拍方式）→ PCM。譜面編輯器塞
        /// <see cref="EditorAudioCache"/> 進來，換歌就不必每首重解一次。null = 照常自己解。</summary>
        public System.Func<string, Mp3Decoder.Mp3Sync, System.Threading.Tasks.Task<Mp3Pcm>> mp3Decoder;

        /// <summary>這種譜的 mp3 該用哪一套對拍（見 <see cref="Mp3Decoder.Mp3Sync"/>）。純函式，編輯器的
        /// 預抓也要用同一套，不然預抓的 PCM 位置跟實際播的不一樣。</summary>
        public static Mp3Decoder.Mp3Sync Mp3SyncFor(int format)
            => format == 1 || format == 3 || format == 4 ? Mp3Decoder.Mp3Sync.Osu : Mp3Decoder.Mp3Sync.StepMania;
        public int chartLevel;                // external chart LV (osu!mania 星數×7) — shown as the LV label so it matches song-select
        // ---- 生成編舞（外部歌）要的「整首歌」資料 —— 一首歌只能有一支舞，換難度不能換舞（見 Sdo.Osu.DanceInputs）----
        // 這首**歌**的 BPM（選歌畫面顯示的那個，SongCatalog.Entry.bpm）；<= 0 = 不知道 → 退回這張譜自己算的。
        public double songBpm;
        // 這首歌**每個難度**的譜（空格子是 ""）：舞蹈長度＝所有難度的最早第一顆 → 最晚最後一顆，所以玩哪個難度
        // 都跳得完、也都是同一支舞。只在生成那一次讀（ExternalChartIO.Windows）。
        public string[] songChartPaths;
        public int[] songChartIndices;   // 對應每格的 .sm #NOTES 區塊／.gn 包難度（osu/.mc 恆 0）
        public string songDisplayName = "";   // external: the catalog's display title (an osu pack's real per-song name);
                                               // _map.Title would be the shared pack label ("SDO Pack8"). Official = "" (resolved from the .gn catalog).
        private const int ExternalLeadInMs = 2000;   // min ms the first external note is pushed to, so it scrolls in from the edge (count-in)

        /// <summary>
        /// 外部譜（osu/StepMania）要往後推多少毫秒才進場（gameplay 的無聲 count-in）。純函式，方便測。
        ///   • 正式遊玩：把第一顆音符推到至少 <see cref="ExternalLeadInMs"/>，讓它從邊緣捲進來而不是一開場就貼在受擊線上。
        ///   • 編輯器（<paramref name="editorMode"/>）：回 0 —— 編譜要 WYSIWYG，音符必須落在**真實音檔時間**上
        ///     （第一顆＝.sm 的 beat×60/BPM−OFFSET），這樣時間讀數＝StepMania 的秒數、波形對得起來、拍號也正確。
        ///     套了 lead-in 會把整張譜往後推 ~1.5s（且不是整數拍），時間軸就跟音檔/.sm 全對不上（見 BeatGrid 會誤插 beat0）。
        /// </summary>
        public static int ExternalLeadInMsFor(bool editorMode, int firstNoteMs)
            => editorMode ? 0 : Math.Max(0, ExternalLeadInMs - firstNoteMs);

        // (2) 3D avatar — WOMAN default outfit: body-part .msh files (relative to Extracted/),
        // assembled in shared model space (bind pose). Skeleton/skinning/motion come next.
        public string[] avatarParts =
        {
            "AVATAR/900007_WOMAN_FACE.MSH",
            "AVATAR/900017_WOMAN_HAIR.MSH",
            "AVATAR/900018_WOMAN_COAT.MSH",
            "AVATAR/900019_WOMAN_PANT.MSH",
            "AVATAR/900020_WOMAN_SHOES.MSH",
            "AVATAR/900011_WOMAN_HAND.MSH",
        };
        public string skeletonHrc = "AVATAR/FEMALE.HRC";       // Biped skeleton the WOMAN parts are skinned to
        // 體型 (faithful SDO body shape): in-game body index 0=瘦(thin) 1=標準(standard) 2..4=胖(progressively fatter).
        // The original scales each torso/limb-root bone's cross-section, keeping height (see SdoBodyShape). Default 0 = thin.
        public int bodyShapeIndex = 0;
        public bool maleBody = false;                          // WOMAN avatar -> female weight baseline (90)
        private SdoAvatar _avatar;                             // gameplay dancer — kept so the F4 panel can re-shape it live
        private float _bodyShapeB = 1f;                        // live body weight B driven by the F4 control (1 = standard)
        private static readonly string[] BodyShapeLabels = { "Thin", "Std", "Chubby", "Fat", "XFat" };  // body index 0..4 presets
        // 舞台待機 idle(rest cat 0x15,DPS 開始前/結束後循環的那支 — 023_gameplay:4135)。
        // WREST0056 是 cat 0 的大廳待機,擺在這裡是錯的。同場的遠端舞者也照這一組挑(見 RemoteRestMot)——
        // 兩邊各寫一份字面值的話,改了一邊沒改另一邊就會變成「只有別人的待機動作不一樣」。
        internal const string FemaleGameplayRestMot = "MOTION/WREST0072.MOT";
        internal const string MaleGameplayRestMot   = "MOTION/MREST0082.MOT";
        // 🔴 MMD 顯示時,結算左側**每一格頭貼一律用這一支**待機,不照各自的性別挑。
        // 畫出來的身體是同一個 MMD 模型(模型沒有性別之分),一格放男版 MREST0082、一格放女版 WREST0072,
        // 看起來就是「同一個人卻在做兩套動作」= 使用者回報的「結算頭貼不同步」。
        // 注意**幀號本來就是對齊的**:兩支都是 MaxTime=63(64 幀迴圈)、相位都是 0,走的是同一條
        // SdoAvatar.LoopFrame —— 官方那條 lockstep(hook 錄到同一支 mot 的 cursor spread=0.000)沒有被破壞,
        // 差的只是「動作內容」。所以修的是挑哪一支,不是時鐘。
        // 固定挑女版(而不是「本機玩家的性別」)是為了兩台看到的是同一套。
        internal const string MmdPortraitRestMot = FemaleGameplayRestMot;
        public string danceMot = "MOTION/WDANCE0002.MOT";      // fallback dance motion if no DPS
        public string restMot = FemaleGameplayRestMot;         // 男版在 ConfigureAvatarGender 換成 MaleGameplayRestMot
        public string dpsPath = "DANCE/11435.DPS";             // per-song choreography for sdom1435 (sequences motion slices)
        // External (osu/StepMania) songs have no official .dps: these identify the song so ExternalDps can generate
        // one — deterministically, once — into its folder and record it in the folder's sdoinfo.dat (see EnsureExternalDance).
        public string externalFolder = "";     // the song's folder (SongCatalog.Entry.folderPath)
        public string externalSongKey = "";    // which song in that folder ("" = its only one; ExternalSongGrouper key)
        // 舞蹈的 RNG seed：資料夾的內容指紋(SongCatalog.Entry.packId)。缺歌傳檔的兩端資料夾名不同、
        // .dps 又不隨檔傳(收端自己重生)，所以 seed 只能吃這個，吃資料夾名會讓兩邊跳不同的舞。"" → 退回資料夾名。
        public string externalPackId = "";
        private readonly Dictionary<string, MotLoader> _motCache = new Dictionary<string, MotLoader>();
        // 這首歌的動作外掛樹（overlay）：一個自帶 DANCE + MOTION/AUMOTION 的歌包，查 .mot 時先贏、找不到才退回 base
        // 資料根。由這首歌 .dps 的所在樹推導（見 MotionOverlay）；"" = 沒有外掛，只用 base 根。每次載歌前重設。
        private string _motOverrideRoot = "";

        // EXACT note-board geometry (4-key, left board X=0): lane LEFT-EDGE X 0/69/138/207 (pitch 69 exact).
        // These match NOTES_BOARD1.PNG's own lane-divider columns (texture x = 14,83,152,221,290 → 69px pitch),
        // so when the board is drawn 1:1 (native, no scaling) at boardX=0 the notes sit exactly on its lanes.
        // The track has a left margin (TrackMarginX); all lane X + TrackCenterX include it.
        private const float TrackMarginX = 14f;
        private static readonly float[] LaneLeftX = { 0f + TrackMarginX, 69f + TrackMarginX, 138f + TrackMarginX, 207f + TrackMarginX };
        private const float LaneCx0 = 34.5f;  // lane center offset (pitch/2)
        // 官方 2D noteskin 是**整組 1:1 原生像素 blit**（跟 NOTES_BOARD1 315×600 一樣不縮放）。證據：
        //   ① 每一套 4-key skin（NOTEIMAGE_5/6/8/9/10/11/PET/SHOWTIME）的圖尺寸完全一致 —
        //      *HOLDHEADACTIVE* = 100×80、*_JUDGELINE* = 100×100、*_LONG*（長條 body）= 100×64。
        //   ② 繪製函式 NewNote_DrawNoteWithEffects_004909c0 用 source rect（單位＝貼圖像素，捲動時直接寫
        //      Pic_GetHeight 級的值進 param_5[1]/[3]）當目的高度做 Y 翻轉，旋轉中心取 Pic_GetWidth/GetHeight
        //      的一半 → dest 尺寸 == 貼圖原生尺寸，沒有任何縮放係數。
        // 貼圖 100 寬 > 車道 pitch 69 是**故意的**：箭頭實體只佔 x[22..77]≈56px，外圈是給旋轉/±20~30px 擺動
        // 特效（KeyCfg byte5c8 的 note 特效模式）留的透明 padding，相鄰車道的 padding 互相重疊不影響畫面。
        // ⚠️ 舊值 82/92 是目測估的，畫出來的箭頭只有 56×0.82≈46px，比官方小 18%（使用者實機比對回報）。
        private const float NoteW = 100f;      // 2D 落下音符 / 炸彈本體 / 長條 的顯示寬 = 貼圖原生寬（1:1）
        private const float ReceptorW = 100f;  // JUDGELINE 也是 100×100，同樣 1:1（官方 receptor 的圖案本來就比音符略小 54 vs 56 —— 是圖畫的，不是縮放）
        // 3D skin（hiteft3D）畫的是 NOTES.MSH/JUDGELINE.MSH 的**幾何**、不是這組貼圖，尺寸另外校準過
        // （0.73×82≈60px ≈ 官方 2D 箭頭實體 56px，本來就對）→ 保留它原本的基準寬，本次只修 2D。
        private const float Note3dBaseW = 82f;
        private const int Keys = 4;
        // notes must stay within the note board and never cover the HP bar (y 18..29). A SpriteMask clips note
        // sprites to this Y band; 向上 the band is [30, 600] — the 30px strip hides notes behind the top frame/HP bar
        // and the bottom is the board/frame bottom (600). 向下 flips the whole board about y300, so the hidden strip
        // mirrors to the bottom → the band becomes [0, 570] (see NotePanelLayout.ClipTopY/ClipBottomY, set in
        // ApplyPanelLayout). Defaults here are the 向上左邊 values used by the standalone/F4 boot before it resolves.
        private float _clipTopY = 30f;
        private float _clipBottomY = 600f;
        // DDR lane order: 0=Left 1=Down 2=Up 3=Right (matches NOTEIMAGE_5 + the original).
        private static readonly string[] Dir5 = { "left", "down", "up", "right" };
        // two manual key sets per lane (Left/Down/Up/Right): A S W D, and numpad 4 5 8 6 (right-hand cross).
        // These are the DEFAULTS; the OPTION dialog's keyboard tab can override them per user (persisted in
        // GameSettings.keys). FrontendApp injects the resolved bindings into laneKeyOverride at launch; when it's
        // null (e.g. the SDO_SCENE dev boot that spawns gameplay directly) the defaults below are used.
        private static readonly KeyCode[][] DefaultLaneKeys =
        {
            new[] { KeyCode.A, KeyCode.LeftArrow },   // 0 Left
            new[] { KeyCode.S, KeyCode.DownArrow },   // 1 Down
            new[] { KeyCode.W, KeyCode.UpArrow },     // 2 Up
            new[] { KeyCode.D, KeyCode.RightArrow },  // 3 Right
        };
        /// <summary>User key bindings resolved from settings (per lane: {primary, aux}); null → DefaultLaneKeys.
        /// Set by FrontendApp.StartGameplay so the Game assembly stays decoupled from Sdo.Settings.</summary>
        public KeyCode[][] laneKeyOverride;

        // EXACT HUD coords (DdrGamePlay.xml absolute) + EFT positions (decompiled)
        private static readonly Vector2 HpSize = new Vector2(238, 11);
        private static readonly Vector2 HpPos = new Vector2(TrackCenterX - 119f, 18); // centred on the track (0..275)
        private static readonly Vector2 HpEftSize = new Vector2(64, 32);  // real HpEft1.png size
        private static readonly Vector2 ScorePos = new Vector2(290, 18);
        private const float ScoreDigitPitch = 25f;       // 29 + alt(-4)
        // PERFECT/COMBO/digits form ONE rigid cluster (JudgeWord → COMBO word → number). Its bounding box spans from the
        // PERFECT word's top (~JudgeWordCenter.y − 20) to the digits' bottom (~ComboDigitY + 33). The three anchors are
        // offset in lock-step so that box is centred in the play area BELOW the judgment band — NOT the whole board: the
        // receptors are 100×100 drawn 1:1 (ReceptorW=100) about judgeLineY=70, so the judgment band is [70 ± 50] = [20, 120].
        // The usable band below it is [120, 600] (board bottom); its centre = 360, shared by every noteskin (向上/up-scroll).
        // (The anchors below stay at the 358-centre values — the 1:1 receptor fix moved the ideal centre by 2px, far below
        // the tolerance these were placed at; re-tuning them would churn the HUD for no visible gain.)
        // (History: originally ~277 = biased up; then board-midline 300; now below-judgment centre 358.)
        private static readonly Vector2 JudgeWordCenter = new Vector2(TrackCenterX, 259);
        private const float ComboWordY = 318f;
        private const float ComboDigitY = 369f, ComboDigitStep = 42f, ComboDigitW = 48f;
        // The COMBO word and the digits must render at ONE per-pixel scale so the label and the number read as the
        // same font (native COMBO.PNG = 117×33, each digit = 67×72). Deriving the word width from the digit width
        // locks word/number to the source-art ratio; a hardcoded 100 drew the word at 0.855× vs the digits' 0.716×.
        private const float ComboWordW = ComboDigitW * 2.5f;   // ≈ 83.8, 117/67=1.74

        private OsuBeatmap _map;
        private ManiaJudgmentEngine _engine;
        private ScoreProcessor _score;
        private HealthProcessor _health;
        private readonly GameplayClock _clock = new GameplayClock();
        private AudioSource _audio, _sfx, _ambient;
        private readonly Dictionary<string, AudioClip> _seCache = new Dictionary<string, AudioClip>();
        // ---- 打拍音 (F7, StepMania assist tick) ----
        // 每個有音符的 row 響一聲 clap(練習/對拍用;音色=官方 theme 的 assist tick.ogg,見 BuildAssistTick)。
        // 開關**跨歌延續**(s_assistTick,同一次執行內),但不落地存檔 —
        // 等同 StepMania 的 GAMESTATE->m_StoredSongOptions.m_bAssistTick(「Store this change, so it sticks if we change songs」)。
        // 排程走音訊時鐘(PlayScheduled),不是「這幀到了就播」,所以不吃 frame rate 抖動。
        private readonly AssistTick _tick = new AssistTick();
        private AudioClip _tickClip;
        private double _tickOnsetSec;           // 打拍音檔開頭的前導靜音(秒) —— 排程要提早這麼多,見 MeasureOnsetSec
        private AudioSource[] _tickVoices;      // 音源池:密集 16 分音符時,前一聲還在響(或還沒響)就換下一個音源
        private double[] _tickBusyUntil;        // 每個音源忙到哪個 dspTime(排程落點 + 音檔長度);<= 現在 = 空閒
        private double _tickClipLenSec;         // 目前打拍音檔的長度(秒)——音源要被佔住這麼久
        // 池的大小**看譜面**決定(見 BuildAssistTick):一顆 tick 從被排程到播完會佔住一個音源
        // lookahead + 音檔長度 那麼久,密集段一個視窗內十幾顆是常態。固定 8 個會輪回去蓋掉還沒響的排程 → 那幾聲
        // 直接消失(「按鍵很密的時候沒有打拍音」)。上限 24 是留給音樂/音效的發聲數(Unity Real Voices 預設 32)。
        private const int MinTickVoices = 8, MaxTickVoices = 24;
        private const double TickVoiceWindowMs = AssistTick.DefaultLookaheadMs + 250.0;   // 250 = 音檔長度上限(合成 clap 150ms)+餘裕
        // Per-scene ambient SE (decompiled SeMgr_PlayVoiceTimed, gated on scene id in Gameplay_Update): only a few
        // scenes carry an intermittent ambience (sea waves / stadium crowd / underwater bubbles / garden); see
        // AmbientSeName + TickAmbient. Most scenes are BGM/song-only.
        private AudioClip _ambientClip;          // loaded ambient clip (null = this scene has no ambience)
        private float _nextAmbientAt = -1f;      // realtime when the next ambient one-shot may fire (<0 = not armed yet)
        private bool _started, _failed, _ended;
        // HP 曾經歸零(一次性 latch,整首不再清除)。_failed = 「立刻中斷遊玩」,完奏模式不會設;_hpDead = 「這局死過」,
        // 兩種模式都會設 —— 結算的 GAME OVER / 評分 F 看的是它,完奏模式打完整首照樣算輸。見 Update 的 HP-out 段。
        private bool _hpDead;
        private double _songStartDspTime, _clockStart = -1;
        // The chart's music-start offset (type-10 音樂起止 marker) in seconds — the silent count-in the notes
        // scroll through before the audio comes in. This holds the MARKER ONLY (always >= 0); the hand-set
        // offsetMs and the global offset are added separately in MusicCountInSec, which is what actually drives
        // the audio schedule / the beat-0<->clip-position mapping (AudioChartSeconds). MusicCountInSec may go
        // NEGATIVE when the offsets pull the music ahead of beat 0 (GameRate.ScheduleMusic then starts the clip
        // mid-way). Set with _clockStart in OpeningSequence().
        private double _musicStartDelaySec;
        // When the DPS dance begins, in beat-0 note-clock seconds: the FIRST NOTE's time, NOT the music-start
        // marker. The choreography spans first→last note (DpsLoader.Total ≈ last−first note), so on charts whose
        // marker sits well before the first note (a long intro — e.g. sdom1226: marker at beat 0 but first note
        // ~5.4 s in) anchoring on the marker made the dancer lead the song by the whole intro. The dancer holds
        // the standby idle until this beat, then starts the DPS. Read every frame by the avatar's DanceTimeSec.
        private double _danceStartSec;
        // Opening lead-in. While the READY->GO animation plays, _clockStart is parked this far in the future so the
        // song stays stopped, the notes stay hidden and the dancer holds its idle. When GO finishes, OpeningSequence()
        // re-anchors _clockStart StartLeadSec ahead and schedules the song, so neither starts before the opening does.
        private const double OpeningParkSec = 30.0;   // > opening length, big enough to keep notes off-screen
        private const double StartLeadSec = 0.1;       // small shared lead: sample-accurate PlayScheduled + chart sync
        // Opening camera intro. The original enters gameplay on the crane: for the first few seconds the note board
        // is absent (decompiled state 3 — NoteBoard_Update / NewNote_StartPlayback don't run yet) while the opening
        // shot flies in; the board + READY text appear together only when the intro ends (state 3->4). We replicate
        // that by holding the whole track hidden for openingIntroSec while the director crane runs, then revealing it
        // with the READY/GO overlay. The crane (director shot 0) keeps running across the reveal.
        public float openingIntroSec = 1f;     // camera-only lead before the track + READY appear (tuned to 1s)
        public float camIntroSkipSec = 0.5f;     // skip the first N seconds of the director's shot 0 (start from the 1s frame, cut the front); F4-tunable
        private bool _camIntroSkipped;         // one-shot: the skip is applied once when the director first runs (at reveal)
        private float _introStartRt = -1f;     // realtime the intro began; <0 = no intro (track shown immediately)
        private bool _trackVisible = true;     // false during the opening hold (board + HP bar hidden, see SetTrackVisible)

        // Boot / loading screen: a full-screen loading tip image (閉撰敃氪/DatasSDO/LOADING/LOADING_N.PNG, random) + a
        // "Loading..." badge (LOADINGS_N.PNG, random) in the bottom-right corner, drawn over EVERYTHING on the main
        // camera from the very first rendered frame. It stays up until (a) the local build is ready AND (b) the online
        // ReadyGate passes AND (c) a minimum on-screen time — then fades out. This both hides the "crammed in the middle"
        // startup (follow-effects — ground star-ring / head marker / hand trails — only settle onto their bones in the
        // first LateUpdate) and gives a proper loading screen. The front-end's own fade-to-black is already gone by the
        // time Start() runs (StartGameplay hides the whole canvas), so gameplay owns this reveal itself.
        private SpriteRenderer _bootCover;     // full-screen loading image (or a black fallback if the art is missing)
        private SpriteRenderer _bootBadge;     // LOADINGS_* "Loading..." corner badge
        public float loadingMinSec = 1f;       // the loading screen shows for at LEAST this long, then a straight cut (no fade)
        private bool _sceneBootDone;           // Start() finished the synchronous build (scene/avatar/board/HUD placed)
        private bool _audioReady;              // the song audio load attempt has finished (clip decoded, or failed)
        private bool _bootRevealed;            // the loading screen has finished revealing → the opening (READY/GO) may run
        private float _bootShownRt;            // realtime the loading screen first appeared → base for the minimum display time
        // Online sync gate: return true only once the scene is loaded AND every connected player is ready, so the synced
        // song start fires for everyone together. Null = offline/solo (local readiness only). The netcode layer assigns
        // this; BootRevealCo holds the loading screen until it returns true. See BootRevealCo / LocalBootReady.
        public System.Func<bool> ReadyGate;

        /// <summary>
        /// 本機這一端載完了(場景/角色/譜面/音訊都就緒),但**還沒**開跑 —— 連線層在這裡回報
        /// <c>setPlayState(loaded)</c>,server 收齊所有人的才廣播 gameplayStarted 讓
        /// <see cref="ReadyGate"/> 放行。只會被呼叫一次。null = 離線/單機。
        ///
        /// 為什麼不讓連線層自己去輪詢 <c>LocalBootReady()</c>:那是 private,而且「載完了」的定義
        /// (場景 + 音訊 + follow 特效落位)本來就該由 gameplay 自己說,不該在外面再寫一份。
        /// </summary>
        public System.Action LocalReady;

        // ---- 連線:分數流(M4-c)---------------------------------------------------------------------------------
        // 只傳分數,不傳按鍵記錄:舞蹈是 DPS 編舞驅動的(同一首歌大家跳一樣),收端可以從相鄰兩筆
        // 判定計數推導出「跳/停」的 gate,所以 replay frame 是多餘的頻寬。

        /// <summary>本機這一刻的成績 —— 分數流的一筆就是送這個。</summary>
        public struct NetScoreSnapshot
        {
            public double TimeMs;                                  // 歌曲時間(負 = 還沒開始)
            public long Score;
            public int Combo, MaxCombo, Perfect, Cool, Bad, Miss;
            public float Hp;                                       // 0..1
        }

        /// <summary>分數流用的歌曲時鐘(ms;負 = 還沒開始)。
        ///
        /// 🔴 刻意用**原始牆鐘**(開跳到現在),不是校正過的 <c>_clock.CurrentMs</c>:tMs 會被拿去跟
        /// **別台**的 tMs 比(server 的領隊取樣、本機名單的同刻取樣)。校正裡含每台自己的音訊延遲設定
        /// (GlobalOffsetSeconds),兩台設不同就會憑空多出一段固定偏差。牆鐘則是共同的「開跳後第幾毫秒」。
        /// 右側名單的本機分數歷程(<c>RecordLocalScoreSample</c>)也用這個時鐘,才查得準。</summary>
        private double NetClockMs => _clockStart >= 0 ? (Time.timeAsDouble - _clockStart) * 1000.0 : -1.0;

        /// <summary>連線層每 ~200ms 讀一次送上去。離線沒人讀。</summary>
        public NetScoreSnapshot NetScore
        {
            get
            {
                var s = default(NetScoreSnapshot);
                s.TimeMs = NetClockMs;
                s.Score = TotalScore;
                if (_score != null)
                {
                    s.Combo = _score.Combo; s.MaxCombo = _score.MaxCombo;
                    s.Perfect = _score.PerfectCount; s.Cool = _score.CoolCount;
                    s.Bad = _score.BadCount; s.Miss = _score.MissCount;
                }
                double hp = _health != null ? _health.Health : HealthProcessor.MaxHealth;
                s.Hp = Mathf.Clamp01((float)((hp - HealthProcessor.FloorHealth)
                                             / (HealthProcessor.MaxHealth - HealthProcessor.FloorHealth)));
                return s;
            }
        }

        /// <summary>房內其他舞者的名字 + 目前分數(server 彙整後推來的)。</summary>
        /// <summary>
        /// 一位遠端玩家的最新一筆成績。右側名單只用 Name/Score,但**遠端舞者的跳/停**需要
        /// 判定計數與 combo —— 那是從相鄰兩筆的差推出「這個 8 拍有沒有斷/有沒有音符」的原料
        /// (見 <see cref="Sdo.Ruleset.DanceGate"/>,也是分數流不必傳按鍵記錄的原因)。
        /// </summary>
        public struct NetPlayerScore
        {
            public int UserId;
            public string Name;
            public long Score;
            public int Combo;
            public int Perfect, Cool, Bad, Miss;
            /// <summary>這一筆是他的**哪個譜面時刻**(ms;≤0 = 不知道)。右側名單靠它把本機的分數倒帶到
            /// 同一刻再畫,否則自己那一列永遠比別人快一步(見 <c>RosterLocalScore</c>)。</summary>
            public double TimeMs;

            /// <summary>他的 HP 歸零了(frame 的 hp 欄位 == 0)。<b>預設 false = 活著</b> ——
            /// 離線 / mockOpponents 那條路徑不填這個欄位,語意必須是「沒說 = 沒死」。</summary>
            public bool Dead;

            /// <summary>他人已經不在這一場了(中途 Esc 回房間 / 斷線)。預設 false 的理由同上。</summary>
            public bool Left;

            public Sdo.Ruleset.DanceJudgeCounts Counts
                => new Sdo.Ruleset.DanceJudgeCounts(Perfect, Cool, Bad, Miss);
        }

        /// <summary>
        /// 連線:右側名單/名次要用的**真**對手。null = 離線 → 走 <see cref="mockOpponents"/> 或 solo。
        /// 每次 8 拍結算(<c>RefreshRanking</c>)讀一次。
        /// </summary>
        public System.Func<NetPlayerScore[]> NetOpponents;

        /// <summary>Server-authoritative leader userId for the active online match; zero means unavailable.</summary>
        public System.Func<int> NetLeaderUserId;

        /// <summary>
        /// 連線:結算面板要用的真資料(server 的 resultsReady)。null / 回 null = 用本機算的那份。
        /// </summary>
        public System.Func<ResultScreen.Row[]> NetResultRows;

        /// <summary>
        /// 結算面板「沒人按確定」時自動確定的秒數(0 = 不自動,一直等玩家按)。
        /// 連線時由 FrontendApp 設 30 秒:自動確定跟按確定是同一條路(ResultScreen.OnConfirm),
        /// 一樣拆遊戲、送 playFinished、轉場回房間 —— 差別只在沒人按也會走。
        /// 單機留 0(想看多久就看多久,反正沒人在等)。
        /// </summary>
        public float resultAutoConfirmSec = 0f;

        // ---- result / finish sequence (歌曲結束 → 輸贏定格動作 → 結算面板; decompiled FinishSequenceTick phase4..6) ----
        private enum ResultPhase { None, FinishPose, Settle, Replay }
        private ResultPhase _resultPhase = ResultPhase.None;
        private float _resultPhaseStart;          // Time.time the current result phase began
        private bool _localWon;                   // local player is the round winner (rank 1) — drives win/lose pose + FINISHED
        private bool _localWonForRecord;          // 戰績用的「贏」:並列第一也算(見 LocalWonForRecord)
        private bool _gameOver;                   // HP ran out (failed) — result shows GAME OVER instead of YouWin/Lose
        // 輸贏定格的官方 clip(cat5 = 贏、cat4 = 輸),男女各一支。本機用下面兩個欄位(ConfigureAvatarGender 依
        // 本機性別挑);場上其他人**各挑自己性別**的那一支(見 PlayRemoteFinishPoses)—— 所以字面值要有名字,
        // 不能只活在本機那兩個欄位裡。
        public const string FemaleWinMot = "WWIN0002.MOT";
        public const string MaleWinMot = "MWIN0001.MOT";
        public const string FemaleLoseMot = "WLOST0003.MOT";
        public const string MaleLoseMot = "MREST0004.MOT";
        public string winMot = FemaleWinMot;      // winner 定格 pose (cat5); male = MWIN0001.MOT
        public string loseMot = FemaleLoseMot;    // loser 定格 pose (cat4); male = MREST0004.MOT
        public float finishPoseSec = 2.5f;        // hold the win/lose 定格 pose this long before the panel settles
        public float settleSec = 0.6f;            // brief beat between the pose and the background replay starting
        public bool enableResultSfx = true;       // play SE_0014(win)/SE_0015(lose) jingle + the SE_0020/0022 tally chimes
        // 打擊紀錄 (osu-style key-frame replay) + dance-gate track. Recorded during play; the gate track drives the
        // result-screen BACKGROUND dance loop (hits hidden); the key frames are the groundwork for replay viewing
        // (P1, hits shown). See Sdo.Ruleset.Replay and docs/systems/replay-local.md.
        private readonly Sdo.Ruleset.Replay _replay = new Sdo.Ruleset.Replay();
        private readonly List<(double tMs, bool on)> _danceTrack = new List<(double, bool)>();
        private double _replayLoopStart;          // Time.timeAsDouble the background replay loop began
        private double _replayLenMs;              // background replay loop length (song length)
        private double _replayOffsetMs;           // where in the loop the replay STARTS — biased to the chart climax + random jitter so each settle opens on a different slice (not always the song's opening)
        private ResultScreen _result;             // 結算面板 (STATIS panel) — built lazily, shown at the settle beat
        private string _songTitle = "song";       // resolved song title (captured when the HUD song label is built)

        // 結算頭像: render the LOCAL avatar's head into a RenderTexture for its result row (45° 3/4 view, idle moves).
        public bool resultHeadPortrait = true;
        public int headPortraitLayer = 11;        // dedicated layer for the ISOLATED idle head avatar (head cam renders only this)
        // 取景基準 = **頭骨 (Bip01_Head) 的 rest 位置，而且只有它**（使用者：「不該算臉或頭髮，就是對頭的骨骼的位置就好」）。
        // 骨架每套裝扮都同一副 → 換髮型、戴帽子、穿翅膀，頭都恆等大、恆等位置；相機不再依賴任何 mesh 的 bounds。
        // 舊版量 renderer bounds 的「髮頂」自動算距離：穿「Ribbon Star M」(037939 翅膀) 時翅膀比頭高一大截，量到的高度
        // 變 2.5 倍（12.6→31.5）→ 相機被甩遠 → 結算大頭貼變成框裡的小人。量幾何就會有這種事，所以不量了。
        // Camera matched to the official AvatarShow render (RE'd from sdo.bin.c). The shared 3D cam is PerspectiveFovLH
        // fovY=π/4=45°, LookAtLH eye(-3,46,-181)→at(-2,38,21) up(0,1,0) → +Z view tilted DOWN ~2.27° (Δy −8/202).
        // Per the OFFICIAL screenshots the result/ranking heads are a 3/4-ANGLED HEAD CLOSE-UP (head ~fills the frame, hair/
        // accessories spill above the top, only a sliver of shoulder shows) — i.e. the head-closeup mode (mode 7: model yaw
        // −30°, scale 2.6), NOT a frontal full-body framing. Yaw gives the 3/4. 官方是逐 costume 的 scale 表（無單一值），
        // 我們改成「相對頭骨的固定取景」：下面兩個常數照那個正確構圖（翅膀不算進去時）反推，模型單位，世界值 = ×headAvatarScale。
        public float headZoom = 1f;                // 微調：>1 = 拉遠（頭變小、上方留白更多）；<1 = 放大
        public float headPortraitDist = HeadBoneFraming.DistModel;     // 相機距離（模型單位，相對頭骨）
        public float headPortraitFov = 45f;        // 官方 fovY = π/4 = 45°（已對齊）
        public float headPitchDeg = 2.3f;          // 官方相機俯角 atan(8/202)≈2.27°（略俯視頭部）
        public Vector3 headAimOffset = new Vector3(-2f, HeadBoneFraming.AimUpModel, 0f);   // 瞄準點相對頭骨的偏移（模型
                                                   // 單位）：X 把臉擺正，Y 抬到臉／髮之間，臉才落在框內、頭髮往上溢出
        public float headAvatarScale = 1.05f;     // idle avatar uniform scale — tuned
        public float headAvatarYaw = 30f;         // 模型 Y 旋轉 = 3/4 斜角（官方頭部近拍 mode7 = −30°；轉模型不轉相機）。可調/翻號
        private Camera _headCam; private RenderTexture _headRt; private SdoAvatar _headAvatar;
        private string _headRestMot;              // 本機那一格頭貼現在放的待機(換 MMD/換回 SDO 才重載,見 SyncLocalHeadPortraitIdle)
        private Vector3 _headModelPos = new Vector3(0f, 50f, 0f);   // head bone REST pos (model space) — cam targets this so it stays FIXED (no per-frame bob chase)
        private static readonly Vector3 HeadAvatarSpot = new Vector3(5000f, 0f, 5000f);   // isolated parking spot (off the stage)
        private readonly Dictionary<int, RoomHeadPortrait> _resultHeadPortraits = new Dictionary<int, RoomHeadPortrait>();

        private readonly List<RuntimeNote> _notes = new List<RuntimeNote>();
        private readonly List<RuntimeNote> _notesByMapIndex = new List<RuntimeNote>();
        private readonly List<double> _noteStarts = new List<double>();   // _notes[i].Note.StartTimeMs, ascending — drives NoteScan.UpperBound
        private int _firstAlive;                                          // cursor: index of the earliest still-live note (see NoteScan.Advance)
        private double _bombPrevNow;                                       // 上一幀的譜面時間,用來偵測炸彈「跨過判定線」的那一幀(見 TickBombs / StepMania CrossedMineRow)
        private bool _bombPrevValid;                                       // false = 這首歌還沒 tick 過炸彈(第一幀把 prev 設成 now,避免把開場前就過去的炸彈誤判成剛跨線)
        private readonly Stack<NoteVisual> _visualFree = new Stack<NoteVisual>();   // returned note-visual bundles waiting to be re-rented
        private readonly List<NoteVisual> _visualAll = new List<NoteVisual>();      // every bundle ever created (for teardown/debug)
        private Transform _noteVisualRoot;                                // identity origin parent so all pooled note GameObjects live under one node
        private readonly RuntimeNote[] _holding = new RuntimeNote[Keys];
        private readonly Sprite[][] _noteFrames = new Sprite[Keys][];
        private Sprite[] _bombFrames;                              // ZD00..ZD03 炸彈動畫 (NOTEIMAGE 共用,非每軌;隨 note skin 換)
        private Sprite _bombExplodeSprite;                        // 引爆特效圖 = StepMania 的 Fallback Tap Explosion Dim HitMine → DATA/NOTEIMAGE/BOMB_EXPLODE.png
        private const string MineSeName = "player_mine";          // StepMania theme 的 Player mine.ogg → DATA/SE/player_mine.wav
        private readonly Texture2D[] _holdTex = new Texture2D[Keys];
        private readonly Sprite[] _holdTail = new Sprite[Keys];    // 尾帽的「下緣封口」版 (*_long_bottom) — 尾端在下方時用
        /// <summary>
        /// 尾帽的「**上緣**封口」版 (<c>*_long_head</c>)。null = 這個 skin 沒有這張(NOTEIMAGE_5/8/9/10/pet)。
        ///
        /// 🔴 官方對長條尾端的封口給了**兩張圖**,不是一張加翻轉:<c>*_long_head</c> 上緣是黑描邊、下緣開口,
        /// <c>*_long_bottom</c> 反過來。兩張互為上下翻轉**只在左右軌成立**(那個箭頭上下對稱);上下軌翻了
        /// 箭頭就指反,所以美術各畫一張 —— 這正是它們必須分兩張存在的原因。見 <see cref="HoldCapOrient"/>。
        /// </summary>
        private readonly Sprite[] _holdCapHead = new Sprite[Keys];
        // 官方在這一軌的兩個封口槽位有沒有放帽子圖(見 CapSlotHasArt)。NOTEIMAGE_8 兩端都是 false。
        private readonly bool[] _holdCapAtHead = new bool[Keys];
        private readonly bool[] _holdCapAtTail = new bool[Keys];
        private readonly bool[] _holdTailFlipX = new bool[Keys];   // combined-name skins share one cap across a lane pair → mirror it
        // 這張尾帽存的是「上緣封口」那個朝向 → 尾端在下時要翻過來。只有在 skin 缺 _holdCapHead(資產不全)
        // 時才會是 true;由圖的內容判定,不是 skin 名字(見 CapContentCenterY)。
        private readonly bool[] _holdTailFlipY = new bool[Keys];
        private readonly bool[] _holdCapPerLane = new bool[Keys];   // true = 每軌預畫 cap (NOTEIMAGE_6 箭頭)：依軌向畫好，不吃 scroll 方向翻轉
        private SpriteRenderer _missOverlay;                       // track-wide red wash flashed on a miss (covers all 4 lanes reliably)
        private readonly Sprite[] _recIdle = new Sprite[Keys];      // 待機動畫的第一幀(擺位/建 SpriteRenderer 用的代表圖)
        /// <summary>
        /// 判定區的**待機循環**(官方 JUDGELINE.AN)。🔴 官方的判定區不是一張靜態圖 —— 它一直在 2 幀之間
        /// 循環,那就是玩家看到的「閃爍」。舊版只載了第 1 幀當靜態底圖,於是 NOTEIMAGE_6/8/9/10/11 的閃爍
        /// 全部不見,而且 NOTEIMAGE_6 的第 2 幀(待機的另一半)被誤當成按下爆發的第一幀。
        /// null 或只有 1 幀 → 不動(NOTEIMAGE_5 的官方 .an 就是同一張放兩次,以及 3D skin 的單張箭頭)。
        /// </summary>
        private readonly Sprite[][] _recIdleFrames = new Sprite[Keys][];
        /// <summary>待機循環的播放速度。官方 .an 不帶時間資訊(引擎用固定影格率播),這個值是目測配的;
        /// F4 除錯面板可以調。太快會變抖動,太慢就看不出在閃。</summary>
        public float recIdleFps = 6f;
        // Keydown receptor feedback = a ONE-SHOT burst (官方 KEYDOWN_JUDGELINE.AN),fired on the key-PRESS
        // transition then resolving back to the idle loop. It is press-driven only — NOT tied to whether the key
        // stays held (decompiled CtlNotesShow_TriggerLanePress = "play judgeline press effect for a lane once").
        private readonly Sprite[][] _recDownFrames = new Sprite[Keys][];
        private readonly float[] _recDownStart = new float[Keys];   // when the press burst began; <0 = idle loop
        public float recKeydownStepSec = 0.03f;                     // per-frame hold time for the keydown burst
        private readonly SpriteRenderer[] _receptors = new SpriteRenderer[Keys];
        public float noteAnimFps = 12f;
        public float bombAnimFps = 5f;   // 炸彈 ZD00..ZD03 循環速度(比音符慢,不然轉太快)
        public float bombExplodeGain = 3f;    // 爆炸圖亮度增益(additive 疊在亮亮的譜面板上,1× 看起來太淡)
        // 爆炸圖大小 = NoteW × 此值。1.558 = 舊的 (LaneW 82 × 1.9) ÷ NoteW 100 —— note 改回 1:1 時**刻意保持
        // 爆炸的絕對像素寬 155.8 不變**：這個值是實機目測調的，而 BOMB_EXPLODE.png 是 128×128（1:1 會是 1.28），
        // 手上沒有官方炸彈爆炸的畫面可以裁定哪個對，所以不順手改它、留給後續考據。
        public float bombExplodeZoom = 1.558f;

        // ---- lane click flash (decompiled NoteBoard_DrawClickFlash_00498bd0) ----
        // notes_board_click{1..4}.png (1..4 = lane) lights the struck lane. The original tints the strip with a
        // 3-frame white×alpha cycle 255→130→0, advancing on a ~timer; a tap plays it once, a held long-note loops
        // it (the strip is redrawn every frame the lane is being struck). Faithful = plain alpha blend: the strip
        // carries its own teal translucency + top-biased alpha gradient (brightest at the hit line).
        private static readonly float[] ClickFlashAlpha = { 1f, 130f / 255f, 0f };   // decompiled local_20[0..2]
        public float clickFlashStepSec = 0.07f;          // per-frame hold time (decompiled timer step)
        public float clickFlashBright = 0.4f;            // overall opacity ×; scales the alpha cycle (keeps the 255:130:0 ratio)
        private readonly Sprite[] _clickFlashSpr = new Sprite[Keys];
        private readonly SpriteRenderer[] _clickFlashSr = new SpriteRenderer[Keys];
        private readonly float[] _clickFlashStart = new float[Keys];   // when (re)triggered; <0 = inactive
        private const float ClickStripTopY = 12f;        // board surface top (texture y0..11 is transparent)

        private Camera _cam;
        private const float TrackCenterX = 138f + TrackMarginX;   // centre of the 4-lane track (span 0..276) + left margin

        // HUD
        private SpriteRenderer _hpBg, _hpTex, _hpBackFrame, _hpGlow, _hpSolidBack;
        private Sprite[] _hpGlowFrames; private float _hpGlowT;
        private SpriteRenderer _judgeWord; private float _judgeWordAt = -10f;
        private readonly Sprite[] _judgeSprites = new Sprite[4];
        private SpriteRenderer _comboWord;
        private readonly Sprite[] _comboDigitSprites = new Sprite[10];
        private readonly List<SpriteRenderer> _comboDigits = new List<SpriteRenderer>();
        private int _lastComboShown = -1; private float _comboPopAt = -10f;
        private SpriteRenderer[] _scoreDigits;
        private readonly Sprite[] _scoreDigitSprites = new Sprite[10];
        private Sprite[] _burstFrames, _readyFrames, _goFrames;
        private Sprite[] _burstFramesUD;   // self-contained skins' UP/DOWN-lane hit frames (jz*_ud); null = non-directional (use _burstFrames for all lanes)
        private Sprite[] _lnEndFrames;     // 長條完成的 END burst (官方 Eft_LnEnd 槽) — 6 frames, per LnEndArt
        private Material _addMat;           // additive material template; each burst clones its own instance
        private Material _hpGlowMat;        // HP-edge glow's OWN additive instance (dedicated so its _TintColor can be driven bright, and no _MainTex cross-bleed with bursts)
        private SpriteRenderer _readyGo;   // opening READY/GO overlay (centre screen)
        private SpriteRenderer _gameOverGo;   // 死亡字幕 GAME OVER overlay (centre screen; HP-out death only)
        private Sprite[] _gameOverFrames;     // GAMEOVER00/01/02 (EFFECT/GAMEOVER; sequence per GAMEOVER.AN)
        public float gameOverScale = 1f;      // GAME OVER 字幕以原生像素 × 此係數繪製 (per-skin 圖尺寸差很多:439×249 / 600×150 / 466×76…)
        public float gameOverFrameSec = 0.12f;// 掃入幀時長 (motion-blur 00→01→定格清晰 02)
        public float readyGoScale = 1f;        // READY/GO 以「原生像素」尺寸繪製 × 此係數 (官方 .an 逐幀 blit 原尺寸;
                                               // PET=198px、標準=300px。舊版硬撐 360px → PET 這種小圖被放大到太大)
        private readonly List<BurstFx> _fx = new List<BurstFx>();    // all live bursts: taps overlap freely (no gating)
        private readonly List<HandRibbon> _handTrails = new List<HandRibbon>();  // hand glow ribbons (world-space palm ribbons) for live tuning
        // Head emoji cut-ins (UI/PLAYINGEXP): combo milestones / consecutive misses / low HP pop a 4s camera-facing
        // billboard at the dancer's head front-right. See PlayingEmoji.cs + LoadEmojiArt/CreateHeadEmoji/ShowEmoji.
        private PlayingEmoji _emoji;
        private Sprite[] _emHH, _emSHSH, _emJRKL, _emKJ, _emHE, _emH, _emY, _emJS, _emGTH;
        private readonly EmojiTriggers _emojiState = new EmojiTriggers();   // pure trigger logic (combo / miss-run / low-HP)
        private readonly Stack<Material> _matPool = new Stack<Material>();  // reuse burst material instances (no per-hit GC)
        private SpriteRenderer _board;          // framed note-board (NOTES_BOARD1, chamfered), drawn 1:1 native
        private Texture2D _boardSrc;            // cached ORIGINAL board texture (kept so alpha can be re-scaled live)
        private Texture2D _boardGenTex;         // last generated (alpha-scaled) texture, destroyed before regen
        private float _boardAlphaApplied = -1f; // tracks the boardAlpha last baked into _board's sprite
        // DEBUG tuning sliders (toggle with F4). Drag in-game to tune; values apply live.
        public float boardAlpha = 1.4f;     // board alpha MULTIPLIER on the original texture: 1=native (~62%, the
                                            // original look), ~1.4=official (deep but inner detail still shows),
                                            // ~2.6=fully opaque. Multiplies the real alpha curve so detail survives.
                                            // OPTION 遊戲頁「面板透明度」滑桿 → FrontendApp 開局前把 gameplay.panelOpacity 灌進來。
        // OPTION 遊戲頁「遊戲特效」兩個勾選（FrontendApp 開局前設定）：關掉就不生對應特效。預設 true = 全開。
        public bool effectCharacter = true; // 人物特效：每 100 combo 的 100/200/300 COMBO.EFT（SpawnComboBurst）
        public bool effectScene = true;     // 場景特效：場景常駐背景 EFT（魔法陣/雪/極光/發光…，SpawnSceneEffects）
        // 進階「完奏模式」：HP 歸零不切斷歌曲，整首照打(判定續行)到曲末 —— 但死亡照算：從歸零那刻起分數凍結
        // (P/C/B/M 判定統計仍繼續記錄)、**舞者停舞回待機**(血用完了就不能再跳舞，跟一般模式死掉一樣)，
        // 結算一樣出 GAME OVER、評分 F。見 Update 的 HP-out 段與 _hpDead。
        public bool playFullSong = false;
        // 掉 miss 也照跳舞（config.ini opt_danceIgnoreMiss，預設關；OPTION 沒有這個選項）：開著時 8 拍結算不再因為
        // 這個 block 有 Bad/Miss 而讓舞者停下來（見 Sdo.Ruleset.DanceGate），**連血量都不管**（優先權最大）：
        // 完奏模式血用完照樣跳到曲末。整首跳舞不受 combo/miss/HP 影響。
        public bool danceIgnoreMiss = false;
        // 無理短長條 → 一般 note（預設開；OPTION 尚未接 UI，先由 GameplaySettings.collapseShortHolds / config.ini 灌進來）：
        // 載譜後把長度短於 180 BPM 16 分音符 (OsuBeatmap.ShortHoldMaxMs ≈83ms) 的 long note 收成單顆 note，見 LoadChart。
        // 這開關只管**外部轉檔譜**(chartFormat 1/2/4 = osu/sm/mc)：官方 k.gn (chartFormat 0) 與 .gn 歌曲包 (3) 是
        // SDO 原生譜，開著也不會被改（格式 gating 見 OsuBeatmap.AllowsShortHoldCollapse）。
        public bool collapseShortHolds = true;
        // 進階「歌曲炸彈」（OPTION 進階頁 → GameplaySettings.songBombs）：true=照譜面原樣有雷（預設）；
        // false=載譜後把譜面上的炸彈整顆拿掉（OsuBeatmap.RemoveBombs，見 LoadChart）。
        // 炸彈不計分也不計 miss，拿掉不動滿分／TotalNotes。
        public bool songBombs = true;
        // OPTION 遊戲頁「遊戲視角」：true=默認(自動導播，開場吊臂+自動切鏡) / false=固定(鎖 cameraFixedIndex 那台，無開場運鏡)。
        public bool cameraAuto = true;
        public int cameraFixedIndex = 0;    // 固定視角鎖第幾台（0..FixedCamCount-1）＝上次在遊戲中用 F2 切到的那台
        // 遊戲中按 F2 換鏡頭時回報新的模式（-1=自動導播 / 0..n-1=固定鏡頭）。前端(FrontendApp)接起來寫回 OPTION 設定，
        // 所以下一局會停在同一台，OPTION「遊戲視角」的標籤也會跟著變成 固定/默認。
        public Action<int> onCamModeChanged;
        public float boardX = 0f;           // board horizontal nudge (design px); 0 keeps texture lanes aligned 1:1 to the track
        // ── NOTE-PANEL POSITION (two orthogonal player settings, wired in by FrontendApp before boot; see NotePanelLayout).
        // dropDirection = Room win2「掉落方式」(0=向上 top/up-scroll, 1=向下 bottom/down-scroll, 2=傾斜→沒實作、不在選單裡，
        //                 舊設定檔留下的值比照向上);
        // notesPanelLeft = OPTION 遊戲頁「NOTES面板位置」(true=屏幕左邊 預設 / false=屏幕中央). ApplyPanelLayout() turns these
        // into the geometry the board/receptors/notes/HP/score/combo all read: _panelOffsetX (加在每個面板相對 X 上),
        // judgeLineY (受擊線 Y, top↔bottom), _scrollSign (+1 上捲 / −1 下捲). Standalone/F4 boot keeps the defaults (向上左邊).
        public int dropDirection = 0;        // 掉落方式：0=向上 1=向下（2=傾斜是舊值，比照向上）
        public bool notesPanelLeft = true;   // NOTES面板位置：true=左邊(預設) / false=置中
        /// <summary>實際生效的面板位置＝<see cref="NotePanelLayout.EffectivePanelLeft"/>(玩家設定, ShowTime)：
        /// **ShowTime 一律靠左**（該模式的 HUD 是絕對座標，board 置中會被壓到；理由詳見該函式）。板面幾何、周邊 HUD
        /// 級聯、血條位移全部讀這個值，不要再直接讀 <see cref="notesPanelLeft"/>（那是玩家原始設定，不改）。</summary>
        public bool PanelLeftEffective => NotePanelLayout.EffectivePanelLeft(notesPanelLeft, showtimeMode);
        private float _panelOffsetX = 0f;    // resolved 水平位移 (design px)：0=左, +242.5=中
        private int _scrollSign = +1;        // +1=notes rise up to the judge line (向上), −1=notes fall down (向下)
        // ── 遊戲中的聊天框(官方 winchat)。訊息內容/顏色/該不該顯示全部由前端(FrontendApp)決定後推進來 ——
        // Sdo.Game 不能引用 Sdo.UI(asmdef 是 UI → Game 的單向依賴),所以這裡只留中性的委派與 GameplayChatLine。
        private GameplayChat _chat;
        private GameplayChat.ExpressionPanelArt _pendingChatExpressions;   // 前端可能在 BuildHud 之前就設好
        private List<GameplayChatLine> _pendingChatSeed;
        /// <summary>玩家在遊戲中送出的一句話(原始字串;表情指令/密語的解析在前端做,跟房間同一條路)。</summary>
        public System.Action<string> onChatSend;
        /// <summary>玩家切了聊天頻道(值＝前端 ChatChannel 的整數)。</summary>
        public System.Action<int> onChatChannel;
        /// <summary>玩家從表情面板點了一個表情。</summary>
        public System.Action<int> onChatExpression;
        /// <summary>正在用聊天框打字 —— 這期間 lane 鍵與所有遊戲熱鍵都要停掉(不然打「w」會踩到上鍵)。</summary>
        public bool ChatTyping => _chat != null && _chat.Typing;
        // ── 周邊 HUD 隨面板位置左右重排（大分數/名次/名單/LV·時間 不跟著 board 平移；置中時要讓開中央的 board）。官方
        // 向下置中 = 向上置中 的水平鏡射：分數/名次/名單這一坨與 LV·時間 互換左右邊。以下是設計px(800寬,置中 board≈242..557)
        // 的初版座標，可在 Inspector/F4 微調。左邊模式沿用官方原本右側級聯(board 在左,右邊空著)。
        private float _scoreBaseX = 290f;    // 大分數 8 位數起始 X（每幀 UpdateScoreDigits 讀）; LayoutSideHud 依模式設定
        public float hudScoreRightX = 561f, hudScoreLeftX = 20f;                  // 大分數 起始 X（右/左）
        public float hudRankRightX = 680f, hudRankLeftX = 120f;                   // 粉紅名次 N/M 中心 X（右/左）
        public float hudRosterNameRightX = 577f, hudRosterScoreRightX = 781f;     // 小人名+分數 名單（右＝官方預設）
        public float hudRosterNameLeftX = 19f, hudRosterScoreLeftX = 223f;        // 名單鏡射到左邊（about x=400）
        public float hudAttrLeftX = 204f, hudAttrRightX = 548f;                   // 「LV: 时间:」整組基準 X（左下/右下）— 右下留邊避免時間值超出 800 框
        // 向下置中：血條從頂端移到 note board 下面（受擊線在底部，血條跟著鏡射到底部框上；置中時橫向留在中央、避開左下歌名/右下LV）。
        private float _hpYOffset = 0f;             // 血條整組 Y 位移（design px）；ApplyPanelLayout 依模式設定
        public float hudHpDownYOffset = 552f;      // 向下置中的血條下移量（≈ 15→567，把頂端血條鏡射到板底）
        // 向下（受擊線在板底）時「判定字 → COMBO 字樣 → 連段數字」**整組**上移，離下方的受擊線/音符遠一點。
        // 負值＝往上（design y 往下長）。**三行必須用同一個位移**：它們的間距是照 JudgeWordCenter/ComboWordY/
        // ComboDigitY 排好的一整叢，只搬其中一行就會把間距吃掉 —— 彈跳(pop)放到最大時判定字和 COMBO 會疊在一起。
        private float _judgeComboYOffset = 0f;         // 目前生效的整組 Y 位移；ApplyPanelLayout 依掉落方式設定
        public float hudJudgeComboDownYOffset = -20f;  // 向下模式的上移量（design px）
        // 文字整體大小比例（config.ini [Room]，1.0 = 官方原尺寸）。comboTextScale 縮放的是「COMBO 字樣＋數字」整組
        // （位置與字距一起等比例，見 UpdateComboDigits 的共用支點），judgeTextScale 縮放 PERFECT/COOL/BAD/MISS。
        public float comboTextScale = Sdo.Settings.RoomConfig.comboTextScale;
        public float judgeTextScale = Sdo.Settings.RoomConfig.judgeTextScale;
        // 同兩組文字的不透明度（config.ini [Room]，1.0 = 全不透明；預設 0.6 讓字不擋住下落中的音符）。
        // 判定字不淡出（官方是顯示一段時間後直接消失），judgeTextAlpha 就是它整段的亮度 —— 見 JudgeWordShowSec。
        public float comboTextAlpha = Sdo.Settings.RoomConfig.comboTextAlpha;
        public float judgeTextAlpha = Sdo.Settings.RoomConfig.judgeTextAlpha;
        // 打中彈跳的峰值倍率（config.ini [Room]，官方 2.0＝彈到靜止大小的兩倍；1.0＝不彈跳）。見 PopScale。
        public float comboTextPop = Sdo.Settings.RoomConfig.comboTextPop;
        public float judgeTextPop = Sdo.Settings.RoomConfig.judgeTextPop;
        public float burstSize = 1.3f;      // hit-burst size multiplier
        public float burstBright = 1.5f;    // hit-burst brightness (additive _TintColor; 1.0 = stock)
        public float holdDropDim = 0.5f;    // 中途放開(Bad/Miss)的長條不消失，改用這個亮度繼續流走 (0.5 = 50%)
        public float lnEndSize = 0.7f;      // 長條結尾爆發大小 ×burstSize
        public float lnEndSpeed = 0.5f;     // 長條結尾爆發播放速度 (0.5 = 半速 → 每幀 60ms)
        // 結尾爆發亮度 ×burstBright。命中爆發是「additive×2 層 + burstBright 1.5」刻意炸亮的，結尾沿用會整片泛光；
        // 這裡壓亮度，且 SpawnLnEndBurst 只畫單層 (見 SpawnBurstFrames 的 doubleLayer)。
        public float lnEndBright = 1.0f;
        // ── hiteft3D: the "3D" note skin's hit effect = a real 3DEFT played at the receptor via the EftEffect particle
        // engine (instead of the flat sprite flipbook). Selected in the F4 STAGE tab note-skin selector (index past the
        // 2D skins). The official 3D skin's hit is HIT.EFT — a note-ARROW-shaped flash (the map_g\NOTES textures = "固定
        // 的note配合") rendered GOLD/yellow (the texture data is white; the yellow is a play-time diffuse tint). AU_HIT
        // (white sparks) and the colour-band / power variants are also offered so the exact official look can be dialled
        // in live. See SpawnHit3d / SelectSkin / EnableHit3dSkin.
        internal bool _hit3dMode;           // true = the 3D hit burst is active (replaces the 2D sprite burst)
        // candidate 3D hit EFTs (F4-cycled). 0 = HIT (official note-arrow); others for comparison/tuning.
        internal static readonly string[] Hit3dEftNames = { "HIT", "AU_HIT", "HIT_LONG", "HIT_SUO", "POWER_Y", "HUANGSE" };
        internal int hit3dEftIdx = 0;       // which of Hit3dEftNames to play
        public float hit3dScale = 110f;     // effScale in design px; base is matched to the note so note3dMaster scales all together; F4-tunable
        // ONE proportional master for the whole 3D skin: multiplies the note mesh, receptors, hold body/cap and hit EFT
        // TOGETHER so "整體等比例放大" is a single knob (they keep their relative sizes). 1.0 = the matched base sizes below.
        public float note3dMaster = 1f;
        public float hit3dBright = 1f;      // extra additive brightness on top of burstBright; F4-tunable
        public float hit3dMotion = 1f;      // velocity damping (HIT doesn't rise; AU_HIT rises ~20× its size → lower it there)
        // The official hit EFT diffuse is WHITE (no gold in the data); this Tint MULTIPLIES it, so it IS the on-screen
        // colour. The old (1,0.80,0.25) rendered ORANGE — B=0.25 was the culprit. Warm pale YELLOW keeps R,G high and
        // B ~0.5 (arrow ≈255,242,140). Set B→1 for the truest-to-file warm-white. F4-tunable.
        public Color hit3dTint = new Color(1f, 0.95f, 0.55f);
        public float hit3dZ = 0f;           // world Z (same plane as the sprite burst — in front of board/notes)
        private readonly EftEffect[] _hit3dLive = new EftEffect[Keys];   // official: ONE effect slot per lane, reset on every hit (no additive stacking)
        // ── 3D-note COLOURED falling notes (the other half of the "3D" skin). The official 3D mode colours each note by
        // BEAT QUANTIZATION (NoteBeatColor): on-beat = magenta(+gold core), off-8th = blue, 16ths = green — a single
        // up-arrow glyph (3DNOTES\NOTES_/NOTES1_/NOTES2_, 4 glow frames each) rotated per lane. Enabled alongside the
        // 3D hit when the F4 "3D" skin is selected; the falling-note SpriteRenderers read _note3dFamily each frame.
        internal bool _note3dMode;                                   // true = colour falling notes by beat (3D skin)
        private Sprite[][] _note3dFamily;                            // [family 0..2][glow frame 0..3]; loaded lazily
        // up-arrow → per-lane rotation (Unity Z, CCW+). Lanes: 0=left(←) 1=down(↓) 2=up(↑) 3=right(→).
        private static readonly float[] Note3dRot = { 90f, 180f, 0f, -90f };
        public bool note3dFlip180;                                   // F4 safety: +180° all note rotations if the glyph loads pointing the wrong way
        // 3D receptor (JUDGELINE) 顯示寬度 × ReceptorW × note3dMaster；F4 可調。
        // 官方 JUDGELINE.MSH 與 NOTES.MSH 是**同一組頂點**（x ±10.9845 / z ±10.4824 完全相同）→ 受擊區箭頭跟落下的
        // 音符**一樣大**。我們的受擊區畫的是整張 128px JUDGELINE 精靈，而 mesh 只覆蓋它 u 0.0217..0.9948（97.31%），
        // 所以：精靈寬 = 音符寬 ÷ 0.9731 = (Note3dBaseW 82 × Note3dHighway.noteSize 0.73) ÷ 0.9731 ≈ 61.5 →
        // ÷ ReceptorW 100 ≈ 0.6151。（舊值 0.82 是目測「補回精靈邊界」補過頭，實機看起來就是判定區圖案太大；
        // 0.669 則是 ReceptorW 還是 92 時的同一個 61.5px —— 2D 改 1:1 後照著重新標定，3D 的實際大小沒變。）
        internal const float Receptor3dScaleDefault = 0.6151f;   // = 61.5 ÷ ReceptorW；抽成 const 讓回歸測試盯住這條換算
        public float receptor3dScale = Receptor3dScaleDefault;
        public float note3dHoldWidth = 0.73f;                        // 3D hold body/cap width × Note3dBaseW (matches the 0.73 note mesh)
        public float note3dHoldHeadGap = 0f;                         // 3D hold body TOP offset from the note head (0 = connect; +px tucks the long lower)
        public float note3dCapOffset = 0f;                           // 3D tail-cap fine offset (design px) on top of the auto weld at the tail edge
        // ── OFFICIAL LONG.MSH constants. The official hold draws the FULLY-OPAQUE LONG textures (ColorKey=0, zero
        // transparent texels — the dark interior is meant to show; silhouette = geometry, NOT alpha): a body quad
        // sampling only the chevron band of LONG_0_1 (the fat outer silver rails sit OUTSIDE it and are never drawn),
        // V = 1 − z·0.03205128 anchored at the TAIL end (z in mesh units; the texture repeats every 31.2 units on a
        // 22.0074-wide strip → wrap addressing), plus a WELDED cap TRIANGLE (base ±11.0037 at z≈0, tip at z=−10.815)
        // sampling LONG_0_0 v 0.5574→0.8939. The V anchor makes the chevrons stay glued to the cap.
        // 規則與常數的推導都在 HoldBodyUv（純函式、有單元測試）；這裡只是本檔用的短別名。
        private const float LongU0 = HoldBodyUv.U0, LongU1 = HoldBodyUv.U1;
        private const float LongCapLenRatio = HoldBodyUv.CapLenRatio;
        private const float LongCapU0 = HoldBodyUv.CapU0, LongCapU1 = HoldBodyUv.CapU1, LongCapUTip = HoldBodyUv.CapUTip;
        private const float LongCapV0 = HoldBodyUv.CapV0, LongCapVTip = HoldBodyUv.CapVTip;
        private Texture2D _capTex;                                   // LONG_0_0 (opaque) — the cap triangle's texture
        private Material _capMeshMat;                                // shared material for all cap triangles (solid, opaque texture)
        private string _note3dDir;                                   // 3DNOTES dir (body/cap textures loaded from here)
        // receptor press-pulse: the official 3D mode plays JUDGELINE_2.MOT on keydown = a scale pop (~0.89→1.1→1.0). We
        // reproduce the visible "變大" as a sine bump on the receptor scale, gated to the 3D skin.
        public float receptorPressAmt = 0.15f;                       // peak extra scale on press
        public float receptorPressSec = 0.15f;                       // pulse duration
        private readonly Vector3[] _recBaseScale = new Vector3[Keys];   // base receptor scale (from PlaceReceptors) the pulse multiplies
        // real 3D mesh highway (NOTES_BOARD runway + NOTES arrows + JUDGELINE receptors, meshes under a tilted 3D group).
        public bool note3dMesh = true;                               // F4: use the real 3D-mesh highway; off = the 2D coloured-sprite fallback
        private Note3dHighway _highway;
        private readonly List<Note3dHighway.Item> _highwayItems = new List<Note3dHighway.Item>();
        // HP-bar leading-edge glow (HpEft). Was sharing _addMat's stock (.5,.5,.5,.5) tint -> half-dim; official is much brighter.
        public float hpGlowBright = 1.2f;   // HpEft brightness (additive _TintColor; 1.0 = old stock dim, 1.2 = tuned to official)
        public float hpGlowOffsetX = -20f;  // glow centre X offset from the fill leading edge (design px). HpEft.png's bright/widest core sits at ~0.78 of its 64px width, so -20 lands that core flush ON the fill edge (less negative = core drifts right).
        // hand glow (original = ribbon off Hand+Finger0 bones, decomp FUN_004c2130/004c1ea0).
        public float handTrailWidth = 0.5f; // width multiplier (1 = faithful 2×|Hand→Finger0|); 0.5 tuned to match the original on-screen
        public float handTrailTime = 0.24f; // lifetime (s); original = 8 segments × 30ms
        private bool _showDebugUI = false;   // F4 toggles the tuning panel; hidden by default
        private bool _showRateUI = false;    // F9 toggles the 遊戲流速 (music rate) test panel
        private Vector2 _dbgScroll;          // scroll for the tuning sliders so they never push the playtest controls off-panel
        private int _dbgTab;                 // F4 panel tab: 0=Play, 1=Combo, 2=Stage — keeps each group's sliders roomy
        private static readonly string[] DbgTabs = { "Play", "Combo", "Stage", "Emoji", "Result", "Banner" };
        private static readonly (string label, EmojiKind kind)[] EmojiTestButtons =
        {
            ("50→HH", EmojiKind.HH), ("150→SHSH", EmojiKind.SHSH), ("350→JRKL", EmojiKind.JRKL), ("550→KJ", EmojiKind.KJ),
            ("800→HE", EmojiKind.HE),
            ("miss10→H", EmojiKind.H), ("miss30→Y", EmojiKind.Y), ("miss50→JS", EmojiKind.JS), ("lowHP→GTH", EmojiKind.GTH),
        };
        private TrackedTextMesh _musicName;                       // bottom song title — per-char so its letter-spacing can be tightened
        private TextMesh _lvText, _timeText, _info, _fpsText;
        // 底列白字(LV/時間)的光柵尺寸管理：跟歌名同一條規則(em 盒實體 px × 2 超取樣)，三個值才是同一種字重。
        private readonly HudTextRaster _hudTextRaster = new HudTextRaster();
        // 「時間」欄拆成三個獨立文字物件，讓數字變動時「冒號」與「總長」的位置都定住不動：
        //   _timeMin  ＝ 倒數的「分」，右對齊 → 右緣釘在冒號錨點，分是「—」或數字都不影響冒號 x。
        //   _timeText ＝ 倒數的「: 秒」，左對齊在冒號錨點 → 冒號位置固定；秒往右長不影響冒號。
        //   _timeTotal＝ 整首固定的「總長」，位置只釘一次。
        private TextMesh _timeMin, _timeTotal;
        private int _timeMeasure;             // 0=待量測 1=量測中 2=已定位
        private const float TimeMinW = 10f;   // 「分」欄寬(design px)：欄位左緣 → 冒號錨點的距離
        private const float CountdownDx = 5f; // 倒數「分:秒」整組再往左移的 px（標籤「時間:」與總長不動；冒號固定關係不變）
        private float _timeTotalDx = 40f;     // 欄位左緣(baseX+132) → 總長欄左緣 的水平距離；量到實寬後更新
        private float _attrBaseX = 204f;      // 最近一次 PlaceAttrRow 的 baseX（量測後重排要用）
        private SpriteRenderer _lblSong, _lblAttr;   // bottom "歌曲名:" / "LV: 时间:" labels
        private Sprite _lvOnlyLabel;                  // "LV:"-only crop of GAMEPLAY2, shown at result (time field dropped)
        private float _fps;
        private double _totalMs;
        private int _lastMilestone;       // last combo milestone (50/100/150…) already celebrated
        private long _shownScore, _scoreFrom, _scoreTarget;  // (8) score commits every 8 beats, then counts up + zooms
        private double _nextScoreCommitMs; private float _scoreAnimAt = -10f;

        // ---- ShowTime (氣條) mode ----
        // Good hits fill an energy gauge; SPACE releases a timed auto-PERFECT window whose score bonus stacks
        // +1 each release. Faithful to the stand-alone exe (docs/reverse-engineering/SDO_SHOWTIME.md); the
        // gauge/bonus math is the pure, unit-tested Sdo.Ruleset.ShowtimeMeter. In real play the room "模式"=
        // ShowTime drives showtimeMode (FrontendApp sets it from GameSession.GameMode==2); F7 toggles it for
        // dev. Space is free (lanes = ASWD / numpad). Default OFF so a direct/scene-test boot is normal play.
        public bool showtimeMode = false;

        /// <summary>
        /// 這場的房間模式:0=自由 1=普通 2=ShowTime(對應 <c>GameSession.GameMode</c> 的編碼)。
        ///
        /// 之所以除了 <see cref="showtimeMode"/> 還要留完整的模式代號:曲末要決定「這場記不記勝負」,
        /// 而政策是**只有普通與 ShowTime 才記**(見 <c>Sdo.Settings.PlayStats.RecordsWinLoss</c>)——
        /// 光看 showtimeMode 這個 bool 分不出「自由模式」與「普通模式」。
        /// </summary>
        public int gameMode = 0;

        /// <summary>
        /// 本機是不是這一輪的第一名。曲末(<c>EnterResult</c>)先用本機名單推算,
        /// server 的 <c>resultsReady</c> 到達後由 <c>CalculateResultOutcome</c> 覆寫成權威值。
        /// 戰績落地要讀它 —— 所以得公開(以前只有畫面內部用)。
        /// </summary>
        public bool LocalWon => _localWon;

        /// <summary>
        /// 戰績(勝/負場)要記的那個「贏」——**同分也算贏**。
        ///
        /// 🔴 與 <see cref="LocalWon"/> 不同,而且是刻意的(使用者指定):名次面板只能有一個第一名、
        /// 場上也只有一個人做勝利定格(平手照座位序),但兩個人打成平手時**兩邊都記勝場** ——
        /// 誰也沒輸給誰。旁觀者恆 false(它不是參賽者)。規則本身在 <c>RankingBoard.LocalTiedForTop</c>。
        /// </summary>
        public bool LocalWonForRecord => _localWonForRecord;

        // energy meter geometry (design px). Frame = MyEnergy0(256×45)@(8,7) metallic trough + MyEnergy1(100×45)@(264,7)
        // gauge head with a black status panel (design 297..354) holding the badge cluster. Official ONLINE fill
        // (sdo.bin FUN_0040dc00/0040e210/0040e0f0): the moving fill is a 3D-EFT electric particle STRIP slid
        // horizontally inside a scissored viewport over the channel — per band it re-bases empty→full and swaps to a
        // different band effect (yellow→blue→red). The remake reproduces that look in 2D with the official ENERGY_Y/
        // ENERGY_B/ENERGY_R 11-frame 85×17 electric-plasma capsules (same PLAYSHOWTIME art family): the capsule is the
        // sliding strip — its RIGHT (head) end rides the fill tip, the tail is cropped at the channel start, frames
        // cycle for the live crackle, drawn ADDITIVE so the black background vanishes and the plasma glows.
        // Channel measured from MYENERGY0.PNG pixels: groove x22..~265 (runs 2px into MyEnergy1), rows y15..27;
        // official strip viewport top/bottom = y14..29; fill right end tucks to ~272 under the chrome swoosh.
        public Vector2 energyFramePos = new Vector2(8, 7);     // MyEnergy0 top-left (static rail)
        public Vector2 energyFillPos = new Vector2(22, 15);    // fill channel top-left (groove starts at design x22)
        public Vector2 energyFillSize = new Vector2(250, 14);  // channel w×h (22..272 × 15..29, official strip window)
        public Vector2 energyBadgePos = new Vector2(311, 21);  // EnergyLevel1/2/3 badge (MyEnergy2/3/4 = ×2/×4/×8)
        public Vector2 energyEftPos = new Vector2(304, 12);    // EnergyEft glow — FIXED in the panel (XML), not tip-riding
        public Vector2 energyMiniPos = new Vector2(279, 15);   // EnergyProgress mini 14×4 chunk (500ms band-up flash)
        public float energyMiniFlashMs = 500f;                 // official flash duration (EnergyProgress range 0..500 = elapsed ms)
        // official strip/glow are engine-tick effects (fast crackle), not the slow ~10fps UI .an tick — and the D3D9
        // gamma-space additive runs HOT, so the additive materials get a >1 tint boost (same class of fix as the
        // combo-burst white-hot, see BallCoreIntensity).
        public float energyFillFps = 40f;                      // ENERGY_* plasma frame cycle (11 frames, fast electric crackle)
        public float energyGlowFps = 20f;                      // EnergyEft panel glow + tip flare frame cycle
        public float energyFillBright = 1f;                    // even ribbon already runs bright (overlap-tiled) → neutral tint
        public float energyGlowBright = 2f;
        private bool _energyHudOn;                             // last SetEnergyHudVisible state (gates per-frame re-enables)
        private readonly ShowtimeMeter _showtime = new ShowtimeMeter();
        // DIAGNOSTIC (SDO_SHOWTIME_DEMO=1): continuously PingPong the gauge fill 0→cap2→0 (~8s) so the yellow/blue/red
        // bands + their head glow can be captured without waiting for slow autoplay fill. Does not touch meter logic.
        public static bool DebugGaugeSweep;
        // ShowTime auto→manual HANDOFF. During the window AutoPlay forces PERFECT and HandleInput is NOT called, so a
        // real key the player presses INSIDE the window (anticipating a note at the seam) has its GetKeyDown edge
        // consumed on an auto frame and lost — the boundary tap / hold-head would then MISS when manual resumes. Fix:
        // ObserveShowtimeInput records, per lane, each in-window press's time (_stPressMs), release time (_stReleaseMs)
        // and the EXACT note it aimed at (_stPressNote); on the single seam frame ReplayShowtimeSeamPress replays that
        // press onto THAT note only, graded at the real press time, so the note earns its true grade instead of a MISS —
        // and a held hold-head keeps going. Precise-targeted on purpose (a re-searched neighbour / any-held-key replay
        // caused phantom hits + wrong-note misses). [user-reported handoff bug]
        private bool _stJustEnded;                          // true only on the frame a ShowTime window ended → seam carry-over
        private readonly double[] _stPressMs = new double[Keys];   // last real DOWN-edge time (ms) seen inside the window, per lane (-1 = none)
        private readonly RuntimeNote[] _stPressNote = new RuntimeNote[Keys];   // the EXACT note that in-window press aimed at (null = none) → replay onto it precisely, never a re-searched neighbour
        private readonly double[] _stReleaseMs = new double[Keys];   // last real key-UP time (ms) inside the window (-1 = none) → grade a released hold's tail at the TRUE release, not the seam
        private double _nowMs;                              // this frame's song time (ms), shared with the HUD tick
        private SpriteRenderer _energyFrameL, _energyFrameR, _energyFill, _energyBadge;   // official frame + fill + level badge
        private SpriteRenderer _energyMini;                 // mini band-up flash chunk (EnergyProgress @279,15)
        private Sprite[] _energyBadgeSpr;                   // MyEnergy2/3/4 (×2/×4/×8 multiplier badges, band 0/1/2)
        private Sprite[] _energyFillSpr;                    // MyEnergy5/6/7 = official YELLOW/BLUE/RED 14×4 mini chunks (band 0/1/2)
        private Material _energyFillMat, _energyEftMat;     // own additive instances (never share the sprite default)
        // THE OFFICIAL GAUGE EFFECTS (POWER_Y/B/R.EFT = online indices 0x2b/0x28/0x2a, byte-walked table): the strip
        // body (RAI electric ribbons trailing the head), the pulsing head glow (AEF_4_02 + NAGA00 + RING_L origin
        // emitters, 0.32s re-fire) and all the flicker live INSIDE these files — the remake plays them verbatim
        // through EftEffect. One instance per band; only the ACTIVE band's head anchor sits at the fill head, the
        // rest park at x=-10000 (the official hidden-gauge park). Official transform: rot(0,90°,0), scale 100 wu ×
        // 0.8 px/wu = 80 design px per EFT unit; the value only ever TRANSLATES the effect (FUN_0040e210).
        // The POWER effects are WORLD-QUAD ribbons designed for the official's dedicated perspective camera; they
        // cannot render straight onto the flat overlay (edge-on). So the remake mirrors the official EXACTLY: a
        // dedicated perspective camera (eye z=-1000, PerspectiveLH 488×15 zn800 zf1200) renders the effect on its own
        // layer into a RenderTexture, which is composited additively onto the bar channel. Only headX translates.
        private static readonly string[] GaugeStripEft = { "POWER_Y", "POWER_B", "POWER_R" };
        private const int GaugeLayer = 6;                   // free layer; only _gaugeCam renders it
        private static readonly Vector3 GaugeOrigin = new Vector3(0f, 20000f, 0f);   // isolated world region for the RT camera
        private readonly GameObject[] _gaugeStrip = new GameObject[3];
        private readonly Transform[] _gaugeAnchor = new Transform[3];
        private Camera _gaugeCam; private RenderTexture _gaugeRT; private MeshRenderer _gaugeComposite;
        public float energyStripScale = 100f;               // official effect scale (the dedicated cam matches official px/unit)
        // Legacy Particles/Additive does `2×tex×_TintColor`, but the official EFT draw is plain MODULATE (1×, sdo.bin.c
        // FUN_0098d660 @664480 COLOROP=D3DTOP_MODULATE, NOT MODULATE2X). SetCol maps diffuse straight through (k=_bright/255),
        // so _bright=1 renders the gauge 2× TOO BRIGHT → the pale-blue ribbon (0.51,0.51,1.0) clips R,G to white (藍變白) and
        // the whole strip washes out (no contrast for the head flash). 0.5 = the faithful 1× (2×0.5=1). F4-tunable.
        public float energyStripBright = 0.5f;              // engine-faithful colour gain (compensates the Legacy shader's built-in 2×)
        // Gauge crackle SPEED: >1 ticks the POWER effect faster → ribbons re-spawn more often (denser overlapping
        // generations = "電流比較多") AND move faster ("動比較快"), and the head glow spawns more overlapping stars
        // (helps "多顆疊加"). Brightness can't add density (it just clips blue→white); tick-speed can. Head STAYS at the
        // stable core (all-axis int-trunc), it just flashes faster. Does NOT affect the fill-head position (driven
        // externally). 1 = faithful cadence; 2 = user-requested livelier current. F4-tunable.
        public float energyStripSpeed = 2f;
        // OFFICIAL fill drive (sdo.bin.c FUN_0040e0f0/e210): the fill is NOT a solid bar — it's the POWER EFT electric
        // ribbon, positioned by sliding the effect origin (headX) over [-305, 0] world. Three per-band eased POSITIONS
        // (NOT a smoothed counter) + STATEFUL HYSTERETIC band selection: only re-select when the active band's eased
        // position leaves (-305, 0], so it can never flicker (the old counter-bucket-per-frame = 前後跳). Only ONE
        // POWER effect is live at a time (Y/B/R); a band-up cleanly swaps colour + refills from empty (twice, ~500ms).
        private readonly float[] _gaugeCur = { -305f, -305f, -305f };   // eased head position per band (init empty)
        private int _gaugeActive = 0;                                   // persistent active band index (hysteresis)
        private const float GaugeFullP = 0f;                            // full head position (official +0x90)
        // Official empty was worldX −305 = the RT camera's visible LEFT edge (design x22), so at 0 fill the POWER
        // head-glow halo half-poked into the channel ("頭光在0就有"). User-confirmed behaviour: the head glow is ON from
        // song start (see _gaugeGlowFromStart), so it sits AT the empty base and only nudges left of the visible edge.
        // Park the empty head gaugeEmptyHideP world-units LEFT of the visible window (small = glow peeks at the left edge
        // straight away; the fill still reaches GaugeFullP=0 at full). F4-tunable (Combo tab).
        public float gaugeEmptyHideP = 5f;
        private float GaugeBaseP => -305f - gaugeEmptyHideP;            // empty head position (a bit left of the visible left edge)
        // Once the opening 3-stage energy intro has run and the song has started, the head glow stays lit even at 0 fill
        // (user: "開始歌曲的時候就要開始亮 不管有沒有按鍵"). Reset each opening so a retry re-arms it. See UpdateEnergyBar drawHead.
        private bool _gaugeGlowFromStart;
        private float _energyMiniT0 = -1f;                  // realtime the current band-up flash began (<0 = idle)
        private Sprite[] _showtimeHitFrames;               // EFT_SHOWTIME/EFT_HIT golden hit flipbook (12 frames)
        public float showtimeHitScale = 1.5f;              // showtime hit burst size ×
        private SpriteRenderer[] _bannerSr; private Transform _bannerRoot;   // SHOW TIME intro banner (ShowTime0..5 tiles)
        private float _bannerStart = -1f;                  // realtime the intro began (<0 = idle)
        private float _bannerDismiss = -1f;                // realtime the slide-out began (<0 = still holding at centre)
        public float bannerInSec = 1.0f, bannerHoldSec = 1.0f, bannerOutSec = 1.0f, bannerScale = 1.0f;   // XML: 1000ms spiral-in, hold, 1000ms slide-out; native scale
        // ShowTime SFX — EXACT online SE names (sdo.bin.c: 0x50/0x4e/0x4f/0x52/0x53). Files live in sdox_offline/SE/,
        // reachable via SeDir's fallback, so these play as-is. electricity.wav (0x51) loops the whole window — that
        // needs a looping AudioSource (deferred); the one-shots below are wired. There is NO bonus-tally chime.
        public string seRelease = "showtimeboom";    // 0x50 — one-shot burst on release
        public string seAnnounce = "showtime";       // 0x4e — "SHOW TIME!" announcer
        public string seArm = "showtimeactive";      // 0x4f — energy crosses into a new level
        public string seWarn3s = "showtimewarning";  // 0x52 — 3001 ms remaining
        public string seWarn07s = "showtimeend";     // 0x53 — 701 ms remaining
        private Sprite[] _savedBurstFrames; private bool _burstSwapped;   // hit burst deque swap (EFT_SHOWTIME REPLACES normal)
        private int _lastArmed = -1; private bool _warn3, _warn07;        // arm-cue + one-shot warning latches
        // official HUD anims (Frida/decompile-confirmed): space.an = 2-image press pulse (s01 hand → s02 fist+flash);
        // EnergyEft1/2/3.an = 10-frame glow behind the level badge; EnergyBonus.an = digit font with count-up + per-digit
        // scale-pop (1.0→1.3→1.0, 500ms) via RollingDigits.
        private SpriteRenderer _spaceSpr; private Sprite[] _spaceFrames;
        private SpriteRenderer _energyEftSpr; private Sprite[][] _energyEftFrames;   // [level 0/1/2][frame]
        private SpriteRenderer _bonusIcon;                  // GamePlay44.an — the "+" glyph (static, @544,23)
        private RollingDigits _bonusRoll;                   // official EnergyBonus digit font (20×26) + pop, @(525,23)
        private RollingDigits _scoreRoll;                   // official EnergyScore digit font (30×39, BIG) + pop, @(300,10)
        private long _scoreRollLast = 0, _bonusRollLast = 0;   // last committed value → SetTarget (fire the pop) ONLY on change
        // breakdance: on release the dancer swaps its choreography to a breaking_{E|N|H}_{n}.dps for the window
        // (online FUN_0092cd80 swaps the active dance pointer), reverting to the song DPS at window end.
        private DpsLoader _songDps, _breakDps; private System.Func<float> _songDanceTime; private bool _dpsSwapped;
        // breakdance chaining: a break DPS is ~10s (E) / ~14s (N) / ~19s (H). Play one; when it ends, if the window
        // still has room for another full break start a fresh one, otherwise HAND BACK to the song choreography for the
        // tail (the song clock runs underneath) — user-requested (official parks the dancer in idle rest instead).
        private double _breakStartMs; private float _breakTotal; private bool _breakIdled;
        // OFFICIAL breaking selection (FUN_0092d280 @611650: at SONG LOAD each tier's variant is rand-rolled ONCE —
        // E=rand%6, N=rand&7, H=rand&7 — and stays fixed for the whole song; at release the TIER LETTER = the RELEASED
        // ENERGY LEVEL (0→E, 1→N, 2→H — FUN_0092d3f0 @611772), NOT the song difficulty). Windows are pas-sized to
        // ~9.5/13.8/19.6s precisely so ONE break of the matching tier (~10/14/19s) fills the window.
        private readonly int[] _breakRolls = new int[3];
        // OFFICIAL window duration (FUN_00643030 @348192-348202): accumulate WHOLE dance segments (pas) of chart time
        // until the tier budget (WindowDurationsMs = 8000/12000/18000 is a THRESHOLD, not the length) is reached ⇒
        // windowMs = ceil(budget / pasMs) × pasMs. Typical pas = 8 beats; Frida-measured 11.9s (lv0 @121bpm) and
        // 16.7s (lv1 @86bpm) both reproduce exactly with 8-beat pas. Tunable if a chart uses 16-beat segments.
        public float showtimePasBeats = 8f;
        // Idle tail: the window must outlast the CHOSEN break dance by at least this much so the break always plays to
        // completion and then parks in RestMot idle before the window closes (official idle tail ≈0.6–2.7s), rather than
        // being cut off mid-move. Only lengthens the window when break.Total + this exceeds the pas-rounded budget
        // (remake break DPS run ~6.8–20.1s; the pas window is song-BPM-dependent, so a long break can outrun it).
        public float showtimeBreakIdleTailMs = 1500f;
        // dancer aura during the window. online FUN_0092cec0 starts 3D effect index 0x2c on the dancer — and in the
        // ONLINE client's renumbered 3DEFT table (DAT_00b933c4, byte-verified against 閉撰敃氪/sdo.bin file offset
        // 0x7933c4) **0x2c = body_star.eft** (star-twinkle billboards + streaks — the tight body glow of the videos),
        // NOT kuanghuan1 (that name comes from the older offline/TW numbering; kuanghuan1 is a room-wide confetti
        // field and reads nothing like the official glow).
        // online FUN_00930e50 (0x169c branch): the aura follows the dancer ROOT (Bip01) X/Z each frame at a FIXED waist
        // height (Y=40), uniform SCALE 20, rot 0, rendered in the SCENE pass with the normal perspective stage camera.
        // The old anchor was a child of _ringTr whose localScale=22 multiplied the +8 offset to +176u (3 dancer heights
        // overhead) — now a free-standing anchor is driven at (pelvis.x, showtimeAuraY, pelvis.z) every frame.
        public string showtimeAuraEft = "BODY_STAR"; public float showtimeAuraScale = 20f; public float showtimeAuraY = 40f;
        private GameObject _auraGo, _auraAnchor;
        // board burst around the note board on SPACE activation. ONLINE table (same renumbering as the aura):
        // centre **0x2d = boom.eft** @(-90,333,0) rot(90°,0,0) scale 50 — a ~1s ring-of-columns + shockwave flash;
        // sides **0x27 = edge4.eft** ×2 @(-490,-400,0)/(-130,-400,0) rot 0 scale 70 — spinning tornado meshes textured
        // with the rai_00..03 lightning flipbook + naga00 sparks rising ~700px: the FULL-HEIGHT blue lightning columns
        // hugging the board's left/right edges. edge4's root loops (life -45) until the handle is killed at window end.
        // The official draws them through a dedicated camera (eye z-1000, PerspectiveLH 800×600 zn800 zf1200) in a LATE
        // pass AFTER the UI ⇒ 0.8 px per world unit at the z=0 plane, over everything. The remake renders them on the
        // board overlay (main ortho cam, layer 0) at the projected design px with effScale = officialScale×0.8 and
        // EftEffect.SortingOrder lifting them above notes/HUD; billboards face the ortho cam (BillboardCam).
        public bool showtimeBoardBurst = true;
        public string showtimeBurstCenterEft = "BOOM", showtimeBurstSideEft = "EDGE4";
        public Vector2 showtimeBurstCenterPx = new Vector2(328f, 34f);    // 0x2d BOOM (projected design px)
        public Vector2 showtimeBurstSide1Px = new Vector2(8f, 620f);      // 0x27 EDGE4 left  (base 20px below screen, grows UP)
        public Vector2 showtimeBurstSide2Px = new Vector2(296f, 620f);    // 0x27 EDGE4 right
        public float showtimeBurstCenterScale = 40f, showtimeBurstSideScale = 56f;   // official 50/70 × 0.8 px-per-unit
        public float showtimeBurstSideSpeed = 2f;                         // side EDGE4 lightning runs Nx faster (user: 電流太慢, ≥2×)
        public float showtimeBurstZ = -2f;                                // in front of the note board
        public int showtimeBurstOrder = 80;                               // official late pass draws OVER notes + HUD
        private readonly List<GameObject> _boardBurstGos = new List<GameObject>();
        public float showtimeAnimFps = 10f;                 // .an UI-sprite tick (~100ms/frame; engine default)
        // energy-bar INTRO animation (online FUN_0040dc00: slide in from off-screen ~500ms, then a 3-stage stepped
        // fill demo ~1200ms/stage). _energyIntroOffX = live X slide offset; _energyIntroFill = demo fill 0..1 (-1 = live).
        private float _energyIntroOffX = 0f, _energyIntroFill = -1f;
        public float energyIntroStageSec = 1.2f;   // official demo tween: 1200ms per band lap (no slide-in)
        // note colour flash in the last 3001ms of the window (online +0x1bac8 render branch @688456: set at the 3001ms
        // warning, a sine pulse tints the gold note until the skin reverts). User-observed: the gold note oscillates
        // RED ↔ YELLOW at ~1 s per full cycle (NOT the old white↔red 200ms).
        private Color _noteTint = Color.white;
        public Color showtimeEndRed = new Color(1f, 0.15f, 0.15f, 1f);
        public Color showtimeEndYellow = new Color(1f, 0.82f, 0.15f, 1f);   // the gold note colour (top of the pulse)
        public float showtimeEndFlashMs = 3001f, showtimeEndFlashPeriodMs = 1000f;   // ~1 s red→yellow→red
        public float showtimeNoteScale = 1.15f;             // notes grow a little larger during the auto-hit window
        private string _preShowtimeNoteDir;                 // note skin to restore when a window ends
        private static Sprite _solidSprite;                 // 1×1 white fallback sprite (used only if official art missing)
        // local total for ranking/result = base score + folded ShowTime bonus (exe merges 0x840 at song end).
        private long TotalScore => (_score?.Score ?? 0L) + (showtimeMode ? _showtime.Bonus : 0L);

        // ---- ranking UI (head nameplate + centre rank N/M + right-side roster list) ----
        // The remake renders ONE dancer; opponents are a configurable mock roster so the rank/list read
        // like the official multiplayer screen (see RankingBoard for the pure ordering logic).
        public bool mockOpponents = false;           // 預設關閉測試對手(離線單人=solo rank 1/1、清單只有本機);真連線時再開
        // 自由模式:**只藏名次**(使用者指定)——遊戲中的 N/M 與右側名單、結算列最左的名次數字、
        // YOU WIN/LOSE 旗都不出。G幣/EXP 照給(名次照算,只是不畫),HP 歸零照樣 GAME OVER。
        // 勝負場的戰績另有規則(自由模式不記),那條在 Sdo.Settings.PlayStats.RecordsWinLoss,與這個旗標無關。
        public bool freeMode = false;
        /// <summary>
        /// 旁觀模式(需求 10):進場**只看別人跳舞**。
        ///
        /// 關掉:音符(連生成都不生)、音符板、受擊線、判定字、連擊、血條、分數、名次 N/M、鍵盤輸入、自己的舞者。
        /// 保留:3D 場景、導播運鏡、右側名單(誰領先正是旁觀者要看的)、旁觀者名單、歌曲資訊列。
        ///
        /// 穿線方式照 <see cref="freeMode"/> 的慣例(FrontendApp 在 AddComponent 之後、Start 之前設欄位)。
        /// </summary>
        public bool spectatorMode = false;
        public string localPlayerName = "玩家";       // local player's display name (hardcoded default, tunable)
        public int playerLevel = 1;                  // character level — scales the round-end coin/honor reward (Sdo.Ruleset.Reward).
                                                     // 前端每局注入 ProfileManager.Level（這個角色的等級）；自 boot 時維持 1。
        public bool localPlayerMale = false;         // set by FrontendApp from GameSession.Gender before Start()
        private static readonly string[] OpponentNames =
            { "炫炎輪火", "Polaris晴天坊", "小醜麵具", "奶茶布丁", "醉小蛇" };
        private const int RosterRows = 6;            // PKSCORE digits only cover 0..6, so the room caps at 6 players
        private readonly List<PlayerEntry> _roster = new List<PlayerEntry>();
        private long _finalEst = 100000;             // estimated strong final score; scales the mock opponents
        private Label3D[] _rosterName, _rosterScore;              // right-side list rows (name + score)
        private readonly Sprite[] _pkDigits = new Sprite[7];      // PKSCORE 0..6 (pink rank glyphs)
        private SpriteRenderer _rankCurD, _rankSlash, _rankTotD;  // centre "N / M": current digit, slash, total digit
        private HeadMarker _headMarker;
        private Sprite[] _arrowFrames;               // UI/ARROW 000..008 (animated rainbow downward arrow)
        private Sprite _slashSprite;                 // GAMEPLAY61.PNG (the "/" between rank digits, 25×29 like PKSCORE)
        // layout tunables (design px, 800×600 top-left; DdrGamePlay.xml nick=577 / score=717..781), Inspector/F4-tunable
        public float rosterFirstY = 108f, rosterRowStep = 18f, rosterNameX = 577f, rosterScoreX = 781f, rosterFontWorld = 24f;
        // rank "N / M": laid out on the SCORE's column pitch so M (total) sits under the score's tens digit.
        // slash x = ScorePos.x + 5*pitch + 14 = 429 → N at col4 (404), M at col6/tens (454). rankY below the score.
        public float rankCenterX = 429f, rankY = 74f, rankDigitW = 25f, rankPitch = 26f;
        // spectators (旁觀玩家): GAMEPLAY18 title sprite + light-blue names below the roster. DdrGamePlay.xml
        // had lookerTitle@(696,190) + looker rows@(696,212..) step13 colour 0xff9DCBFF。
        public bool showSpectators = false;          // 離線預設關閉(沒有觀眾);連線有觀眾時由 FrontendApp 打開
        /// <summary>
        /// 旁觀者的名字(需求 10:要真名)。FrontendApp 從 <c>matchStarting.spectatorNames</c> 灌進來。
        ///
        /// 🔴 這裡本來是 <c>private static readonly string[]</c> 的假名 —— 而 <c>_lookerRows</c> 的長度是**從它**取的。
        /// 所以要能顯示真名,這個欄位必須是實例的、可寫的,而且列數上限要自己定(<see cref="MaxLookerRows"/>)
        /// 而不是跟著資料長度 —— 不然中途有人進來旁觀、名單變長,就得重建整排 Label3D。
        /// 改成固定配 10 列、多的截掉,<see cref="SetSpectatorNames"/> 只改文字。
        /// </summary>
        public string[] spectatorNames = new string[0];
        /// <summary>旁觀名單最多畫幾列(座標 lookerFirstY + i*16 到第 10 列就碰到畫面底了)。</summary>
        public const int MaxLookerRows = 10;
        private SpriteRenderer _lookerTitle;
        private Label3D[] _lookerRows;
        public float lookerTitleX = 694f, lookerTitleY = 214f, lookerX = 698f, lookerFirstY = 241f, lookerRowStep = 16f, lookerFontWorld = 18f;   // names start 5px lower than before so the list clears the 旁觀玩家 header
        /// <summary>旁觀時左上那條「Press Ctrl+Q to quit look on mode」(官方 GAMEPLAY19.PNG,361×25)。
        /// 只有 <see cref="spectatorMode"/> 才建;參賽者的畫面上不該出現(那顆熱鍵也只吃旁觀)。</summary>
        private SpriteRenderer _spectateHint;
        public float spectateHintX = 8f, spectateHintY = 6f;   // 左上角(design px);圖本身左對齊貼邊
        // dancer dance/stop gate. The decision is made ONLY at the 8-beat settlement (same cadence as the score
        // commit) — a break NO LONGER stops the dancer mid-block, it just records the flag and is judged at the
        // next boundary. At each settlement we re-decide dance-vs-stop for the upcoming block (two conditions):
        //   1. broke this block (any Bad/Miss) -> keep dancing IFF the current combo is still > 30 (a strong run
        //      carries through one break; a broken run with combo <= 30 stops).
        //   2. no Bad/Miss this block but notes WERE judged -> keep/resume dancing (a clean block always dances,
        //      even at low combo). No break and NO notes at all -> hold the current state (a stopped dancer does
        //      NOT resume on an empty block). See UpdateDanceGate / ApplyEvent. Avatar honours _dancing via DanceEnabled.
        private bool _dancing = true;          // is the avatar performing the DPS dance (false -> standby idle)?
        private bool _blockHadBreak;           // any Bad/Miss (combo break) since the last 8-beat settlement?
        private bool _blockHadNote;            // any note judged since the last 8-beat settlement?
        private double _nextDanceSettleMs;     // next 8-beat settlement boundary (ms)
        private readonly bool[] _digitVisible = new bool[8]; // was this digit shown last frame (to detect a new digit appearing)
        private readonly float[] _digitPopAt = new float[8]; // when each digit last started its bounce
        private bool _scoreCommitPop;                        // a commit just happened -> pop all currently-visible digits
        private bool _scoreArmed;                            // no digit pops until the score first changes (initial "0" is static)

        // Set by the front-end (FrontendApp, BeforeSceneLoad — always runs before this AfterSceneLoad Boot) so the
        // play screen never self-boots a stray instance: the front-end owns startup and launches gameplay on demand.
        // Without this, the auto-booted instance's Start() spawns a root-level Avatar3D (+ board/scene) that survives
        // the front-end's kill — the kill only destroys the ScreenGameplay object, not the separate roots it created — so a
        // leftover dancer lingers and the real launch then doubles it (two avatars on the dance-spot).
        public static bool AutoBootSuppressed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (AutoBootSuppressed) return;
            if (FindAnyObjectByType<ScreenGameplay>() != null) return;
            var g = new GameObject("ScreenGameplay").AddComponent<ScreenGameplay>();
            // DEV: SDO_SCENE forces a specific stage (set before Start reads scenePath) for render testing.
            var fs = DevVar("SDO_SCENE");
            if (!string.IsNullOrEmpty(fs)) g.scenePath = fs.Contains("/") ? fs : "SCENE/" + fs.ToUpperInvariant();
            // DEV: SDO_SCENE_ONLY=1 boots straight into a CLEAN stage to iterate on background EFTs (the SCN0008 magic
            // circle, snow, aurora…). Reuses observe mode's gating (no notes/music, hidden board/HP/receptors/ranking,
            // idle dancer on the dance spot, fixed cam0) + hides the rest of the gameplay HUD in Start(). The scene and
            // its persistent EFTs still spawn in TryLoadScene, so only the stage + idle dancer + EFTs are shown.
            var sceneOnly = DevVar("SDO_SCENE_ONLY");
            if (!string.IsNullOrEmpty(sceneOnly) && sceneOnly != "0") g.observeBurstMode = true;
            var demoSweep = DevVar("SDO_SHOWTIME_DEMO");
            if (!string.IsNullOrEmpty(demoSweep) && demoSweep != "0") DebugGaugeSweep = true;
            var iso = DevVar("SDO_SHOWTIME_ISO");
            if (!string.IsNullOrEmpty(iso) && int.TryParse(iso, out int isoN)) EftEffect.PowerIsolate = isoN;
            // DEV: SDO_NOTETYPE=0..10 forces a specific note skin at boot (ApplyRoomNoteSkin) so the dead-art
            // smoke test can exercise every EFFECT/NOTEIMAGE skin, not just the stock one.
            var noteType = DevVar("SDO_NOTETYPE");
            if (!string.IsNullOrEmpty(noteType) && int.TryParse(noteType, out int ntv)) g.roomNoteType = ntv;
        }

        /// <summary>DEV scene-override config. A player build (dance.exe) reads the OS env var (set in the terminal
        /// before launch). The editor is launched by Unity Hub and does NOT inherit terminal `$env:` vars, so in the
        /// editor we fall back to EditorPrefs — set via the <c>Tools/SDO</c> menu (SdoDevBootMenu). Env var wins.</summary>
        public static string DevVar(string name)
        {
            var v = System.Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(v)) return v;
#if UNITY_EDITOR
            v = UnityEditor.EditorPrefs.GetString(name, "");
            if (!string.IsNullOrEmpty(v)) return v;
#endif
            return null;
        }

        private void Start()
        {
            // 跨歌延續的開關(F7 打拍音 / F8 自動打擊)。在這裡套用:FrontendApp 是在 AddComponent 之後、Start 之前
            // 才設欄位的,所以要等到 Start 才蓋得過它那句 autoPlay = false。
            assistTick = s_assistTick;
            if (s_autoPlay.HasValue) autoPlay = s_autoPlay.Value;
            ResolveDevDefaults();
            ConfigureAvatarGender();
            _cam = Camera.main ?? new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            SdoLayout.SetupCamera(_cam);
            // 譜面編輯器：換一首歌就重建整個畫面，每次閃一秒載入圖很擾人 → 不放載入畫面（相機的清除色本來就是黑）。
            if (editorMode) loadingMinSec = 0f;
            else BuildBootCover();          // put the loading screen up FIRST...
            _bootShownRt = Time.realtimeSinceStartup;
            StartCoroutine(BootBuildCo());  // ...then build the (heavy) stage behind it — see BootBuildCo
        }

        private void ConfigureAvatarGender()
        {
            if (AvatarPartsNeedFallback(avatarParts, localPlayerMale))
                avatarParts = SdoRoomAvatar.DefaultParts(localPlayerMale);

            if (localPlayerMale)
            {
                skeletonHrc = SdoRoomAvatar.MaleHrc;
                maleBody = true;
                danceMot = "MOTION/MDANCE0002.MOT";
                restMot = MaleGameplayRestMot;
                winMot = MaleWinMot;
                loseMot = MaleLoseMot;
            }

            // 飛行翅膀 → 舞台待機 idle 換成 flystay clip (rest cat 0x2c)。Only the idle/rest changes; the DPS dance is
            // unaffected. 飛行翅膀 = 硬編 5 id + 線上實測名單(離線無法從資料推;見 SpecialMotionItems)。競技場沒有走路,
            // 故只換 idle;前傾滑動 fly-walk 只在房間走動時用。
            // 註:這是 remake 的選擇,不是照抄官方 —— cat 0x2c 在反編譯裡只出現在房間路徑(023:4138 / 028:2437),官方舞台
            // 的待機是 Dancer_PlayIdle_004a73b0(027:1992)的 Motion_PickRandom(cat 0)。See [[sdo-special-item-idle-walk]].
            if (SpecialMotionItems.WearsFlyingWing(avatarParts))
                restMot = SpecialMotionItems.FlyIdleMot(localPlayerMale);
        }

        private static bool AvatarPartsNeedFallback(string[] parts, bool male)
        {
            if (parts == null || parts.Length == 0) return true;
            for (int i = 0; i < parts.Length; i++)
            {
                string u = (parts[i] ?? "").ToUpperInvariant();
                if (male && u.Contains("_WOMAN_")) return true;
                if (!male && u.Contains("_MAN_")) return true;
            }
            return false;
        }

        // Build the stage AFTER the loading screen has rendered. The scene/avatar/chart load is heavy and fully
        // synchronous; running it inline in Start() blocks the frame, so the loading image would only appear once the
        // load already finished (a long black screen before it). Yielding one frame first lets BuildBootCover's sprite
        // render (< ~30ms, well under 0.5s) — the loading tip shows immediately and the build runs visibly behind it.
        // Update() no-ops until _sceneBootDone, so nothing drives the half-built stage during the two boot frames.
        private IEnumerator BootBuildCo()
        {
            yield return null;   // let the boot cover render this frame before the heavy synchronous build below
            LoadArt();
            if (!LoadChart()) yield break;
            BuildScroll();
            BuildBoard();
            // observe mode: no notes (clean stage to watch the burst)。
            // 旁觀模式也不生:一顆音符都不存在 → 沒有東西可捲、可判、可扣血,整條遊玩路徑自然全空,
            // 不用在十幾個地方各加一個 if(spectatorMode)(那才是會漏掉一處的做法)。
            if (!observeBurstMode && !spectatorMode) SpawnNotes();
            foreach (var n in _notes) { double t = n.Note.EndTimeMs ?? n.Note.StartTimeMs; if (t > _totalMs) _totalMs = t; }
            // 🔴 旁觀沒有生音符 → 上面那圈跑不到,_totalMs 會留 0,而歌曲結束的判定是
            // 「now > baseEndMs + 1000」→ 旁觀者的畫面會在**開場一秒後**就跳結算。
            // 曲長改從譜面本身量(音符沒生,但譜是載好的)。
            if (_totalMs <= 0.0 && _map != null) _totalMs = _map.LastNoteMs;
            BuildHud();
            ApplyRoomNoteSkin();   // AFTER BuildHud so _comboWord exists → LoadComboJudgeArt can assign the skin's COMBO.PNG
                                   // (room win2 note selection → matching gameplay skin: board + hit burst + combo/judge, incl. 3D)
            // 編輯器：不載舞者、不載 3D 場景（也就沒有 SceneCam/背景 quad）→ 主相機的 SolidColor 黑直接成為背景。
            // 旁觀:不載**自己**的舞者(沒下場的人不該出現在場上),但場景與導播運鏡照載 —— 那正是要看的東西。
            //
            // 🔴 共用資產與導播鏡頭都**不能**綁在「有沒有本機舞者」上,兩者旁觀時都要:
            //   • LoadSharedDanceAssets —— 場上其他人的骨架/編舞從這裡來(少了它 SpawnExtraDancers 直接 return,
            //     旁觀者看到的是一個空場)。
            //   • LoadCvCameras —— 它同時設舞位(_danceSpot)與導播鏡頭(_dirCv/_camReady)。少了它 _camReady 恆 false,
            //     相機停在原點的預設朝向 —— 實機回報「旁觀進去舞台,鏡頭卡在天花板」就是這個。
            if (!editorMode)
            {
                LoadSharedDanceAssets();
                if (use3dCamera) LoadCvCameras();
                if (!spectatorMode) TryLoadAvatar();
                TryLoadScene();
            }
            // 同場其他舞者(M8)。一定要在 TryLoadAvatar 之後:它們共用那邊解析好的骨架/動作/編舞
            // (_sharedHrc / _sharedDanceMot / _sharedDps),而 SdoAvatar 對那三個只讀 → 共用安全。
            if (!editorMode) SpawnExtraDancers();
            // 判定窗:StepMania(YHANIKI)的「精N」毫秒窗,與 BPM 無關(原版是 tick 窗 = 歌越快越嚴,見 FromSdoBpm)。
            // 以精4 為基準(Perfect 45 / Cool 90 / Bad 135 / Miss 180 ms)乘精度係數;預設精2(×1.33)。
            // SM 5 段折成 SDO 4 段:MARVELOUS+PERFECT→Perfect、GREAT→Cool、GOOD→Bad、BOO(含更外面)→Miss。
            // 精度在 config.ini [Room] judgeLevel 手改(1~8、9=JUSTICE)。
            _engine = new ManiaJudgmentEngine(JudgmentWindows.FromStepManiaJudge(Sdo.Settings.RoomConfig.judgeLevel));
            _outputLatencySec = MeasureOutputLatencySec();                       // 這台機器的輸出緩衝有多長（= 混音游標領先喇叭多久）
            AudioSettings.OnAudioConfigurationChanged += OnAudioConfigChanged;   // 換音訊裝置 → buffer/取樣率變 → 重量
            ApplyClockOffset();   // 全域 offset（這台機器的延遲）− 輸出延遲（讓時鐘＝正在出喇叭的位置，同 StepMania）
            _score = new ScoreProcessor(_map.TotalNotes);
            // 完奏模式：HP 歸零不結束歌曲(見 Update 的 IsFailed 判定) → HP 必須鎖死在地板，否則後面的 combo 會把血補回來。
            _health = new HealthProcessor(healthLevel, lockOnDeath: playFullSong);
            _showtime.Reset();   // fresh ShowTime gauge/bonus per song
            _stJustEnded = false; for (int i = 0; i < Keys; i++) { _stPressMs[i] = -1.0; _stReleaseMs[i] = -1.0; _stPressNote[i] = null; }   // clear the auto→manual handoff latches
            _gaugeCur[0] = _gaugeCur[1] = _gaugeCur[2] = GaugeBaseP; _gaugeActive = 0;   // gauge positions re-init empty
            // official FUN_0092d280: breaking variants are rolled ONCE per song load (E=rand%6, N/H=rand&7) and stay
            // fixed for every release; the tier letter is picked at release time by the released energy level.
            _breakRolls[0] = UnityEngine.Random.Range(1, 7);
            _breakRolls[1] = UnityEngine.Random.Range(1, 9);
            _breakRolls[2] = UnityEngine.Random.Range(1, 9);
            RefreshRanking();   // initial roster/rank (rank 1/N) before the first score commit
            _audio = gameObject.AddComponent<AudioSource>();
            _sfx = gameObject.AddComponent<AudioSource>();
            _ambient = gameObject.AddComponent<AudioSource>();
            BuildOsuKeysoundAudio();
            BuildAssistTick();   // F7 打拍音:本譜的 tick 時間軸 + 排程用的音源池
            var ambName = editorMode ? null : AmbientSeName(SceneMapId());   // load the per-scene ambience (sea/stadium/underwater/garden) if any
            if (!string.IsNullOrEmpty(ambName)) StartCoroutine(LoadAmbientCo(ambName));
            // OPTION 遊戲頁「固定」視角：鎖定上次記住的那台（F2 切過就是那台，預設 FixedEye[0]＝鏡頭 1），
            // 跳過自動導播的開場運鏡。默認視角則維持 -1(自動)。
            if (!cameraAuto) _camMode = Mathf.Clamp(cameraFixedIndex, 0, FixedEye.Length - 1);
            // Enter on the crane with no note board: hold the track hidden while the opening shot flies in, then
            // OpeningSequence() reveals it with READY. Only when there's actually a 3D crane to watch AND the camera is
            // on the auto-director (固定視角沒有吊臂運鏡，直接顯示 note 面板)。
            if (use3dCamera && _camReady && openingIntroSec > 0f && cameraAuto) { _introStartRt = Time.realtimeSinceStartup; SetTrackVisible(false); }
            if (observeBurstMode) { _dancing = false; _camMode = 0; SetTrackVisible(false); _introStartRt = -1f;   // idle dancer, fixed cam, hidden track
                HideComboAndJudge(); HideHudForPanel(); }   // also clear the rest of the gameplay HUD (score/combo/judge/song labels/ranking) for a clean stage
            // 旁觀:自己沒有舞者也沒有音符 → 停掉本機的舞蹈閘門,並把判定字/連擊收掉。
            // 音符板與血條交給 SetTrackVisible 的旁觀分支(它才是唯一收口,開場揭示後還會再呼叫一次)。
            if (spectatorMode) { _dancing = false; SetTrackVisible(_trackVisible); HideComboAndJudge(); }
            // 編輯器：沒有開場運鏡，音符板直接出來；HP/分數/名次/歌曲列全部收掉，只留板子+受擊線+音符。
            if (editorMode) { _dancing = false; _introStartRt = -1f; SetTrackVisible(true); HideHudForEditor(); }
            _sceneBootDone = true;            // the synchronous build above is complete (scene/avatar/board/HUD placed)
            StartCoroutine(LoadAndPlayAudio());
            StartCoroutine(BootRevealCo());   // hold the loading screen until everything's ready (+ online gate), then reveal
        }

        // Full-screen loading screen on the main (ortho) camera, drawn above everything (huge sortingOrder, nearest z),
        // so the boot frames show a proper loading tip instead of the half-placed scene. A random LOADING_N.PNG fills the
        // frame; a random LOADINGS_N.PNG "Loading..." badge sits bottom-right. Falls back to opaque black if the art is
        // missing. Removed by BootRevealCo.
        private void BuildBootCover()
        {
            var bg = LoadingArt.RandomBackground();
            if (bg != null)
            {
                _bootCover = NewSR("BootLoadingBg", bg, 32000);   // above HUD/notes/board and the scene backdrop quad
                _bootCover.color = Color.white;
                SdoLayout.PlaceBox(_bootCover, 0f, 0f, SdoLayout.Width, SdoLayout.Height, -50f);   // stretch to fill the whole 800×600 frame (no gap)
            }
            else   // no loading art found → plain opaque black (still hides the half-placed startup)
            {
                var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                tex.SetPixel(0, 0, Color.black); tex.Apply();
                var spr = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
                _bootCover = NewSR("BootCover", spr, 32000);
                _bootCover.color = Color.black;
                _bootCover.transform.position = SdoLayout.ToWorld(SdoLayout.Width / 2f, SdoLayout.Height / 2f, -50f);
                _bootCover.transform.localScale = new Vector3(SdoLayout.Width + 200f, SdoLayout.Height + 200f, 1f);
            }

            // Bottom-right "Loading..." corner badge — disabled for now (keep the logic; may re-enable later).
            // var badge = LoadingArt.RandomBadge();
            // if (badge != null)
            // {
            //     _bootBadge = NewSR("BootLoadingBadge", badge, 32001);   // above the background image
            //     _bootBadge.color = Color.white;
            //     const float m = 8f;   // bottom-right corner, small margin (design px, top-left origin)
            //     PlaceAspect(_bootBadge, SdoLayout.Width - LoadingArt.BadgeW / 2f - m,
            //                             SdoLayout.Height - LoadingArt.BadgeH / 2f - m, LoadingArt.BadgeW, -51f);
            // }
        }

        // Is the LOCAL build ready to be shown? The scene/avatar/board/HUD are built synchronously in Start (so
        // _sceneBootDone covers them + the first LateUpdate that settles the follow-effects onto their bones), and the
        // song audio has finished loading (_audioReady). This is the "all objects prepared" condition — a real state
        // check, NOT a fixed frame count. The ONLINE gate (all peers ready + synced start) is layered on top in BootRevealCo.
        private bool LocalBootReady() => _sceneBootDone && _audioReady;

        // Hold the loading screen until everything is genuinely ready, then reveal the stage INSTANTLY (no fade):
        //   (1) the local build is ready (scene/avatar/board placed + follow-effects settled + audio decoded);
        //   (2) the online ReadyGate passes (scene loaded on all peers + everyone ready → synced start); null = offline;
        //   (3) a minimum on-screen time so the loading art never just flickers past.
        // Uses realtime throughout so it works while the gameplay clock is parked far ahead. Sets _bootRevealed at the
        // end so OpeningSequence only plays READY/GO once the stage is actually visible.
        private IEnumerator BootRevealCo()
        {
            float shownAt = _bootShownRt;   // count the minimum display time from when the loading screen appeared (before the build)
            while (!LocalBootReady()) yield return null;                       // (1) local objects prepared
            // 連線:先告訴 server「我這邊載完了」,再等它說「大家都好了」。順序不能顛倒 ——
            // 反過來就是每台都在等別人先講,誰也不會開場(server 的推進條件是「沒人還在 waitingForLoad」)。
            if (LocalReady != null) { LocalReady(); LocalReady = null; }
            while (ReadyGate != null && !ReadyGate()) yield return null;       // (2) online: all users ready + synced
            while (Time.realtimeSinceStartup - shownAt < loadingMinSec) yield return null;   // (3) minimum display time

            if (_bootCover != null) { Destroy(_bootCover.gameObject); _bootCover = null; }   // straight cut — no fade
            if (_bootBadge != null) { Destroy(_bootBadge.gameObject); _bootBadge = null; }
            _bootRevealed = true;   // release the opening (READY/GO) — the stage is now visible
        }

        // Standalone-dev convenience: if no chart/audio was assigned (i.e. not launched via FrontendApp), point at a
        // default song under the resolved music tree. No-op once FrontendApp has set gnPath. Keeps absolute paths out.
        private void ResolveDevDefaults()
        {
            if (!string.IsNullOrEmpty(gnPath)) return;
            if (chartFormat != 0) return;   // external chart (osu/StepMania) assigned → never fall back to the dev song (would overwrite oggPath/gnPath → 播成 sdom1435)
            var music = SdoExtracted.MusicDir;
            gnPath = Path.Combine(music, "sdom1435K.gn");
            oggPath = Path.Combine(music, "sdom1435.ogg");
        }

        // ---- SE playback (shipped SE/*.wav) ----
        private void PlaySe(string name) { if (isActiveAndEnabled) StartCoroutine(PlaySeCo(name)); }
        private IEnumerator PlaySeCo(string name)
        {
            if (!_seCache.TryGetValue(name, out var clip))
            {
                var path = Path.Combine(SdoExtracted.SeDir, name + ".wav");
                if (File.Exists(path))
                    using (var req = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.WAV))
                    {
                        yield return req.SendWebRequest();
                        if (req.result == UnityWebRequest.Result.Success) clip = DownloadHandlerAudioClip.GetContent(req);
                    }
                _seCache[name] = clip;
            }
            if (clip != null && _sfx != null) _sfx.PlayOneShot(clip, AudioMix.Sfx);   // 遊戲音效 音量
        }

        // ---- 打拍音 (F7) — StepMania ScreenGameplay::PlayTicks() 的移植 ----

        // 本譜的 tick 時間軸(每個音符的頭,同時間的只留一個)+ 排程用的音源池。
        // 音色 = **官方 StepMania theme 的那顆 clap**(Themes/<theme>/Sounds/ScreenGameplay assist tick.ogg,
        // 這裡取 CyberiaStyle 6),放在 SE/assist_tick.ogg(package_build 會鏡射進 DATA/SE)。載不到就退回自己合成的
        // clap(AssistTick.RenderClap) —— 純函式、有測試,所以沒有那個檔案時打拍音仍然可用,不會靜音。
        private const string TickSeName = "assist_tick";

        private void BuildAssistTick()
        {
            _tick.Load(NoteStartTimes());
            // 池大小 = 這張譜在一個「排程視窗」內最多幾顆 tick 同時在飛(AssistTick.PeakInWindow),夾在 8..24。
            // 一般譜 8 個綽綽有餘;16 分連打/dump 段落要十幾個,不夠就會蓋掉自己還沒響的排程 → 密集段沒聲音。
            int voices = _tick.VoicesNeeded(TickVoiceWindowMs, MinTickVoices, MaxTickVoices);
            _tickVoices = new AudioSource[voices];
            _tickBusyUntil = new double[voices];
            for (int i = 0; i < voices; i++)
            {
                var a = gameObject.AddComponent<AudioSource>();
                a.playOnAwake = false; a.loop = false; a.volume = AudioMix.Sfx;
                // 優先度壓在音樂/音效之下(預設 128;數字越大越低)：發聲數上限是全域的(專案 Real Voices = 32),
                // 極密的譜真的把池吃滿時,該被虛擬化掉的是一聲 clap,不能是歌。
                a.priority = 200;
                _tickVoices[i] = a;
            }
            SetTickClip(SynthClapClip());         // 先掛 fallback,音源池才有 clip 可用(合成的 clap 沒有前導靜音)
            StartCoroutine(LoadTickClipCo());     // 官方 clap 載好就換上去(遠早於 READY/GO 結束,不影響第一個 tick)
        }

        private AudioClip SynthClapClip()
        {
            int rate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            var pcm = AssistTick.RenderClap(rate);
            var clip = AudioClip.Create("AssistTickSynth", pcm.Length, 1, rate, false);
            clip.SetData(pcm, 0);
            return clip;
        }

        private IEnumerator LoadTickClipCo()
        {
            foreach (var ext in new[] { ".ogg", ".wav" })   // theme 檔是 .ogg;若哪天換成 wav 也吃
            {
                var path = Path.Combine(SdoExtracted.SeDir, TickSeName + ext);
                if (!File.Exists(path)) continue;
                var type = ext == ".ogg" ? AudioType.OGGVORBIS : AudioType.WAV;
                using (var req = UnityWebRequestMultimedia.GetAudioClip("file://" + path, type))
                {
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success) { Debug.LogWarning("[tick] " + path + ": " + req.error); continue; }
                    var clip = DownloadHandlerAudioClip.GetContent(req);
                    if (clip == null) continue;
                    SetTickClip(clip);   // 順便量前導靜音（官方那顆 clap 前面有 ~30ms 空白 → 排程要提早）
                    yield break;
                }
            }
            // 找不到通常不是「檔案沒有」，而是**資料根解錯了**（例：worktree 沒有 gitignore 掉的 data_root.txt
            // → Root 退回 sdox_offline/Extracted，那裡根本沒有 SE）。把實際找過的資料夾印出來，不用再猜。
            Debug.LogWarning($"[tick] 找不到 {TickSeName}.ogg/.wav（找過 {SdoExtracted.SeDir}）→ 改用合成的 clap");
        }

        /// <summary>
        /// 音檔開頭到「起音」的時間（秒）—— 排程時要提早這麼多，音檔本身不動。
        ///
        /// <c>PlayScheduled</c> 排的是 <b>clip 的第 0 個取樣</b>，不是「聽得到的那一刻」。官方的
        /// <c>assist_tick.ogg</c>（StepMania theme 的 clap）前面有一段空白 —— 實測 44.1kHz / 全長 81.4ms，
        /// 前 26ms 完全是 0，起音在 ~29.8ms、峰值在 34.3ms。於是每一聲 click 都比排程時間晚 ~30ms 才進耳朵。
        ///
        /// 這在校時上特別惡毒：它是一段**只污染耳朵、不污染眼睛**的假延遲 —— 音符的畫面位置是譜面時鐘畫出來的
        /// （看著 note 打 → 量到 ~+5ms），但聽著 click 打會白白多吃這 30ms，於是兩個測試永遠對不起來，
        /// 整包還會被誤認成「音效卡延遲」。
        ///
        /// 補法照 StepMania：**提早排程，不動音檔**（它的 tick 也是把落點寫進 RageSoundParams::StartTime，
        /// 從不去改 wav）。切音檔還多一個風險 —— 起音偵測抓歪就把起音本身削掉了。
        ///
        /// 起音 = 第一個達到峰值 1% 的取樣。這顆 clap 從 1% 爬到 50% 只要 3.3ms，所以門檻取哪裡差不了幾 ms，
        /// 而且那點殘差是常數，會被 globalOffsetMs 一併吸收。
        /// </summary>
        private static double MeasureOnsetSec(AudioClip clip)
        {
            if (clip == null || clip.samples <= 0 || clip.frequency <= 0) return 0.0;
            int ch = Mathf.Max(1, clip.channels);
            var data = new float[clip.samples * ch];
            if (!clip.GetData(data, 0)) return 0.0;   // 壓縮在記憶體裡的 clip 讀不到 → 當作沒有前導靜音

            float peak = 0f;
            for (int i = 0; i < data.Length; i++) { float a = Mathf.Abs(data[i]); if (a > peak) peak = a; }
            if (peak <= 1e-6f) return 0.0;            // 整段靜音

            float th = peak * 0.01f;
            for (int i = 0; i < data.Length; i++)
                if (Mathf.Abs(data[i]) >= th) return (i / ch) / (double)clip.frequency;
            return 0.0;
        }

        private void SetTickClip(AudioClip clip)
        {
            _tickClip = clip;
            _tickOnsetSec = MeasureOnsetSec(clip);
            _tickClipLenSec = clip != null ? clip.length : 0.0;   // 音源被佔住多久(排程落點 + 這個長度)
            if (_tickVoices != null) foreach (var v in _tickVoices) if (v != null) v.clip = clip;
            if (_tickOnsetSec > 0.001)
                Debug.Log($"[tick] 前導靜音 {_tickOnsetSec * 1000.0:0.0} ms → 排程提早這麼多"
                        + "（PlayScheduled 排的是第 0 取樣；不補的話每一聲都晚這麼多，校時會誤認成音效卡延遲）");
        }

        // 該發 tick 的音符(炸彈除外,見 AssistTick.HasTick)的起始時間。
        private IEnumerable<double> NoteStartTimes()
        {
            foreach (var n in _notes) if (AssistTick.HasTick(n.Note)) yield return n.Note.StartTimeMs;
        }

        // 每幀:把「地平線之前」的 tick 全部排進音訊時鐘。tick 的譜面時間 → dspTime 用的是**音樂本身的映射**
        // (_songStartDspTime + (t − 數拍前導)),跟歌曲同一支時鐘,所以 click 落點是取樣級精準,不受 frame rate 影響。
        //
        // ---- 要提早多少排? 從「聽得到的那一刻」倒推 ----
        // 目標:譜面時間 T 的 click,要在**時鐘讀到 T** 的那一刻進耳朵。
        //   • 時鐘 = 播放游標 = ChartFromDsp(dspNow) − L·rate  (L = 輸出延遲,見 ApplyClockOffset)
        //     → 時鐘讀到 T 的時刻是 dspTime = Draw(T) + L      (Draw = DspFromChartSeconds)
        //   • 排在 dsp D 的聲音,要 L 之後才出喇叭,而且音檔前面還有 onset 秒的靜音
        //     → 實際聽到的時刻是 dspTime = D + L + onset
        //   兩式相等 ⇒ **D = Draw(T) − onset**。輸出延遲 L 自己消掉了(它對時鐘和聲音一視同仁),
        //   真正要補的只有音檔的前導靜音 —— 而那是「提早排程」,音檔一個位元組都不用動(同 StepMania)。
        //
        // 排得到的條件是 D > dspNow,代進去化簡 ⇒ T > now + (L + onset)·rate = now + TickLeadChartMs。
        // 所以地平線的起點要推到那裡:早於它的 tick 已經來不及排進混音,撿了也只會被 clamp 成「立刻播」而慢半拍。
        // (StepMania 同一句:fPositionSeconds += SOUND->GetPlayLatency() + TickEarly + 0.25f — ScreenGameplay.cpp:1220-1225)
        //
        // 關閉時每幀把游標推到地平線起點:中途才按 F7 打開只會從當下的音符開始響(不會把前面累積的一次倒光)。
        private double TickLeadChartMs => (ClockLatencySec + _tickOnsetSec) * 1000.0 * _musicRate;

        private void TickAssist(double nowMs)
        {
            if (_tickVoices == null) return;
            double schedulableMs = nowMs + TickLeadChartMs;   // 早於此的 tick 已經來不及排準了
            if (!assistTick || _ended || _paused || Time.timeScale <= 0f) { _tick.Rewind(schedulableMs); return; }
            double horizon = schedulableMs + AssistTick.DefaultLookaheadMs;
            while (_tick.TryDequeue(horizon, out double tMs))
            {
                // 譜面時間 → dsp:除以流速(StepMania 同一句 fSecondsUntil /= m_fMusicRate,「2x music rate 就是等一半的時間」)
                // 再減掉音檔的前導靜音 → 起音(而不是第 0 取樣)才落在音符上。
                double dsp = GameRate.DspFromChartSeconds(tMs / 1000.0, _songStartDspTime, _musicRate, MusicCountInSec)
                           - _tickOnsetSec;
                double at = Math.Max(dsp, AudioSettings.dspTime);
                int i = PickTickVoice();
                var v = _tickVoices[i];
                _tickBusyUntil[i] = at + _tickClipLenSec;
                v.volume = AudioMix.Sfx;
                v.Stop();   // 輪到的音源可能還在響上一聲(超密集譜)→ 蓋掉
                v.PlayScheduled(at);
            }
        }

        // 挑一個音源來排這一聲。**先挑真正空閒的**(排程已經播完的);全都忙 → 挑最早結束的那個,蓋掉的
        // 才會是最舊的一聲。舊版是無條件輪替 —— 密集段一輪回來時,那個音源上的排程往往還沒響,Stop() 會把
        // 排程**取消**掉(不是截斷),於是整聲不見。池夠大時這裡幾乎永遠拿得到空閒音源。
        private int PickTickVoice()
        {
            double now = AudioSettings.dspTime;
            int best = 0; double bestUntil = double.MaxValue;
            for (int i = 0; i < _tickVoices.Length; i++)
            {
                double until = _tickBusyUntil[i];
                if (until <= now) return i;
                if (until < bestUntil) { bestUntil = until; best = i; }
            }
            return best;
        }

        // 作廢所有「已排程但還沒響」的打拍音(改流速/暫停時它們的 dsp 落點已經失效),游標退回現在重排。
        private void ResetScheduledTicks()
        {
            if (_tickVoices == null) return;
            foreach (var v in _tickVoices) if (v != null) v.Stop();
            for (int i = 0; i < _tickBusyUntil.Length; i++) _tickBusyUntil[i] = 0.0;   // 全部音源重新算空閒
            _tick.Rewind(_nowMs + TickLeadChartMs);   // 同 TickAssist:早於此的 tick 已經來不及排準,別撿
        }

        // F7 按下去當場響一聲(開/關的聽覺回饋 — 這畫面沒有 StepMania 那條除錯字幕)
        private void PlayTickOnce()
        {
            if (_tickVoices == null || _tickClip == null) return;
            int i = PickTickVoice();
            var v = _tickVoices[i];
            _tickBusyUntil[i] = AudioSettings.dspTime + _tickClipLenSec;
            v.Stop(); v.volume = AudioMix.Sfx; v.Play();
        }

        // ---- per-scene ambient SE (decompiled Gameplay_Update PlayVoiceTimed switch on scene id) ----
        // Faithful to SeMgr_PlayVoiceTimed: the ambience is NOT a loop — it plays ONCE when the gap timer elapses
        // and the channel is free, then re-arms the next gap = clip length + rand(0..29)s. Only these five scene ids
        // carry an ambience; every other scene is BGM/song-only. (See memory sdo-se-soundbank.)
        private static string AmbientSeName(int mapId)
        {
            switch (mapId)
            {
                case 4:  return "SE_0030";     // scn0004 sea/beach — waves (~15s)
                case 12: return "VOICE_0017";  // scn0012 fifa stadium (day)   — crowd cheer
                case 13: return "VOICE_0017";  // scn0013 fifa stadium (night) — crowd cheer
                case 14: return "SE_0031";     // scn0014 haidi/underwater — bubbles (~8.6s)
                case 15: return "SE_0033";     // scn0015 garden — nature
                default: return null;
            }
        }

        private IEnumerator LoadAmbientCo(string name)
        {
            var path = Path.Combine(SdoExtracted.SeDir, name + ".wav");
            if (!File.Exists(path)) { Debug.LogWarning("[ambient] missing " + path); yield break; }
            using (var req = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.WAV))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success) _ambientClip = DownloadHandlerAudioClip.GetContent(req);
                else Debug.LogWarning("[ambient] load fail: " + req.error);
            }
            // Guarantee one play right at the opening: a venue that carries an ambience should always sound it once
            // the moment you arrive, then fall back to the intermittent gap timer (clip length + rand 0..29s) for
            // every play after that. Arming at "now" makes TickAmbient fire on the first eligible frame (once _started).
            if (_ambientClip != null) _nextAmbientAt = Time.realtimeSinceStartup;
        }

        // One frame of the intermittent ambience. Runs only during live play — not in observe / avatar-debug, and not
        // once the song has ended (the result sequence is silent except its own jingles). Uses wall-clock (realtime),
        // matching the original's ms-paced HUD/effect timers.
        private void TickAmbient()
        {
            if (_ambientClip == null || _ambient == null) return;
            if (!_started || _ended || observeBurstMode || avatarDebug) return;
            if (_nextAmbientAt < 0f || Time.realtimeSinceStartup < _nextAmbientAt || _ambient.isPlaying) return;
            _ambient.PlayOneShot(_ambientClip, AudioMix.SceneSfx);   // 場景環境音 = 遊戲音效 音量
            _nextAmbientAt = Time.realtimeSinceStartup + _ambientClip.length + UnityEngine.Random.Range(0f, 29f);
        }

        // ---------- art (from Extracted) ----------

        // Active note-board skin folder (NOTEIMAGE_5 by default). SetNoteBoardSkin() swaps it LIVE for the F4 NoteType
        // test (the falling-note + receptor SpriteRenderers read the reloaded arrays each frame — see LoadBoardArt).
        private string _noteDir;
        private string NoteDir => _noteDir ?? (_noteDir = Path.Combine(SdoExtracted.Root, "NOTEIMAGE", "NOTEIMAGE_5"));

        /// <summary>Point the board at a different NOTEIMAGE_&lt;suffix&gt; skin and reload its per-lane art live. Active notes
        /// (n.Head.sprite from _noteFrames) and receptors (UpdateHud from _recIdle/_recDownFrames) re-read the arrays each
        /// frame, so the swap shows instantly. Click-flash + board background are shared (NOTEIMAGE root) and don't change.</summary>
        internal void SetNoteBoardSkin(string suffix)
            => ApplyNoteDir(Path.Combine(SdoExtracted.Root, "NOTEIMAGE", "NOTEIMAGE_" + suffix));

        // Reload the board from note dir <dir> and re-point live notes/holds. Split out of SetNoteBoardSkin so
        // ShowTime can restore an ARBITRARY previous skin dir (not just a NOTEIMAGE_<suffix>) when a window ends.
        private void ApplyNoteDir(string dir)
        {
            _noteDir = dir;
            LoadBoardArt();
            PlaceReceptors(1f);   // re-size receptors for the 2D glyph (undo any 3D-skin receptor scaling)
            for (int c = 0; c < Keys; c++) _recDownStart[c] = -1f;   // snap receptors to idle (skins differ in keydown frame count)
            // Heads re-read _noteFrames each frame, but a hold's Body texture + Tail sprite are bound ONCE at spawn — so
            // re-point already-spawned holds here, otherwise the long body + bottom cap keep the old skin until respawn.
            foreach (var n in _notes)
            {
                if (n == null || n.Done) continue;
                int c = n.Note.Lane;
                if (c < 0 || c >= Keys) continue;
                if (n.Body && _holdTex[c] != null)
                {
                    var mr = n.Body.GetComponent<MeshRenderer>();
                    if (mr && mr.sharedMaterial) { mr.sharedMaterial.mainTexture = _holdTex[c]; var sd = Shader.Find("Sprites/Default"); if (sd) mr.sharedMaterial.shader = sd; }   // back to 2D alpha-blend
                }
                if (n.Tail && _holdTail[c] != null)
                {
                    n.Tail.sprite = _holdTail[c]; n.Tail.flipX = _holdTailFlipX[c]; n.Tail.flipY = _holdTailFlipY[c];
                }
                // 換了 skin,兩端**要不要**畫封口也跟著變(多數 skin 只畫尾端)—— 已經生出來的 hold
                // 要重掛,否則會一直留著上一個 skin 的帽子。反向(換到有封口的 skin)只對之後生成的
                // hold 生效:這條路是 F4 的即時換皮測試,不值得為它把整批 visual 重建一次。
                if (n.Vis != null)
                {
                    bool wantTail = _holdCapAtTail[c] && _holdTail[c] != null;
                    bool wantHead = _holdCapAtHead[c] && _holdCapHead[c] != null;
                    if (!wantTail && n.Vis.Tail) n.Vis.Tail.enabled = false;
                    if (!wantHead && n.Vis.HeadCap) n.Vis.HeadCap.enabled = false;
                    n.Tail = wantTail ? n.Vis.Tail : null;
                    n.HeadCap = wantHead ? n.Vis.HeadCap : null;
                }
                if (n.Cap3d != null) n.Cap3d.SetActive(false);   // 3D cap triangle off with the 2D skin
            }
        }

        // Load (or RELOAD) the per-lane note-board art from NoteDir: falling-note frames, receptor idle + keydown frames,
        // hold body/caps. 判定區的分幀規則見 LoadJudgeLineArt;長條兩端的封口見 HoldCapArt。
        private void LoadBoardArt()
        {
            // 判定區的兩支官方動畫整份讀進來(四軌合在同一個 .an 裡,順序 left/down/up/right = Dir5)。
            // 讀不到就是空陣列 → LoadJudgeLineArt 退回檔名規則,見它的註解。
            var idleAn = SdoExtracted.LoadAn(NoteDir, "judgeline.an", bleed: true);
            var downAn = SdoExtracted.LoadAn(NoteDir, "keydown_judgeline.an", bleed: true);
            // 官方的槽位表:長條兩端要不要畫封口、畫哪張,都寫在這裡面(見 SdoExtracted.AnFrameNames)。
            var anSlots = SdoExtracted.AnFrameNames(NoteDir, "noteimage.an");
            for (int c = 0; c < Keys; c++)
            {
                string d = Dir5[c];
                // bleed:true — 這些 note/receptor PNG 的透明區是 (255,255,255,0) 白底,bilinear 會把白拉進邊緣成白邊 → 先把不透明色 dilate 進透明區清掉
                var fr = new Sprite[4]; bool ok = true;
                for (int f = 0; f < 4; f++) { fr[f] = SdoExtracted.LoadImage(NoteDir, d + "holdheadactive" + f + ".png", bleed: true); if (fr[f] == null) ok = false; }
                if (ok) _noteFrames[c] = fr;
                LoadJudgeLineArt(c, d, idleAn, downAn);
                string baseLong = (d == "left" || d == "right") ? "rightleft_long" : "updown_long";
                var bodySpr = SdoExtracted.LoadImage(NoteDir, baseLong + ".png");
                if (bodySpr != null) { _holdTex[c] = bodySpr.texture; _holdTex[c].wrapMode = TextureWrapMode.Repeat; SdoExtracted.AlphaBleed(_holdTex[c]); }
                // end cap: prefer a PER-LANE cap ({left|right|down|up}_long_bottom — NOTEIMAGE_6, a mini-arrow drawn to match
                // the LANE's arrow direction), else the combined cap (rightleft/updown_long_bottom — NOTEIMAGE_5/8, a shared
                // "funnel" that must point away from the body). 這一張是長條**下端**的封口;上端的封口是另一張,
                // 在下面的 headCap 找 —— 官方兩個朝向都備了圖(NOTEIMAGE_MOVEDOWN.AN 用的就是另一張),
                // 所以正常路徑一次翻轉都不需要,per-lane 的箭頭也就不會被翻到指錯方向。
                var perLaneCap = SdoExtracted.LoadImage(NoteDir, d + "_long_bottom.png");
                var capSpr = perLaneCap ?? SdoExtracted.LoadImage(NoteDir, baseLong + "_bottom.png");
                _holdCapPerLane[c] = perLaneCap != null;
                // 官方槽位表決定這一軌**兩端各要不要**畫封口(見 CapSlotHasArt)。資料夾裡有圖不代表官方
                // 有在用 —— 靠判定線那端多數 skin(5/8/9/10/PET)就是空的,只有 6/11/showtime 有。
                _holdCapAtHead[c] = CapSlotHasArt(anSlots, CapSlotHead, c);
                _holdCapAtTail[c] = CapSlotHasArt(anSlots, CapSlotTail, c);
                // 同一頂尾帽的「上緣封口」版 = 官方**向下捲專用**的那張(NOTEIMAGE_MOVEDOWN.AN 用的就是它)。
                // 每個 skin 的叫法不同,所以照這個順序找:
                //   *_long_head        NOTEIMAGE_6 每軌一張(箭頭要跟著軌向,翻不得)
                //   {rightleft|updown}_long_head    NOTEIMAGE_11 / showtime 合併的一張
                //   {rightleft|updown}_long_bottom_d  NOTEIMAGE_5/8/9/10/pet ——「_d」= movedown
                var headCap = SdoExtracted.LoadImage(NoteDir, d + "_long_head.png")
                              ?? SdoExtracted.LoadImage(NoteDir, baseLong + "_head.png")
                              ?? SdoExtracted.LoadImage(NoteDir, baseLong + "_bottom_d.png")
                              ?? SdoExtracted.LoadImage(NoteDir, d + "_long_bottom_d.png");
                // 拿到兩張之後,誰是「上緣封口」版由**圖自己**說了算(見 CapContentCenterY)——
                // 不看檔名、也不看 skin 叫什麼。NOTEIMAGE_8 的 updown 那對就是對調存放的
                // (它的 updown_long_bottom 才是上緣封口那張),這樣判就自動歸位。
                if (headCap != null && capSpr != null)
                {
                    float headCy = CapContentCenterY(headCap), tailCy = CapContentCenterY(capSpr);
                    if (!float.IsNaN(headCy) && !float.IsNaN(tailCy) && headCy < tailCy)
                    {
                        var swap = capSpr; capSpr = headCap; headCap = swap;
                    }
                }
                // 兩張都拿到就**不必翻轉** —— 官方本來就備了兩個朝向,翻轉是只有一張時的代用品。
                // 真的只有一張(資產不全)才需要翻,而「這張存的是哪個朝向」同樣問圖本身:內容落在
                // 畫布上半 = 它其實是上緣封口那版,那麼尾端在下時就得翻過來。
                // 🔴 以前這裡寫的是 `(d=="up"||d=="down") && NoteDir.EndsWith("NOTEIMAGE_8")` —— 一條寫死
                //    skin 名字的特例,而它想判斷的正是這件事。改看內容之後,每個 skin(含將來新增的)都
                //    自動判對,不必再有任何一條 skin 特例。
                bool flipY = false;
                if (headCap == null && capSpr != null && capSpr.texture != null)
                {
                    float tailCy = CapContentCenterY(capSpr);
                    flipY = !float.IsNaN(tailCy) && tailCy > capSpr.texture.height * 0.5f;
                }
                if (capSpr != null) { _holdTail[c] = SdoExtracted.CleanCapCopy(capSpr); _holdTailFlipX[c] = false; _holdTailFlipY[c] = flipY; }
                _holdCapHead[c] = headCap != null ? SdoExtracted.CleanCapCopy(headCap) : null;
            }
            // 炸彈 (note_type 1) 動畫：ZD00..ZD03,整組共用(非每軌);隨 note skin 一起換。
            var zd = new List<Sprite>();
            for (int f = 0; f < 4; f++) { var s = SdoExtracted.LoadImage(NoteDir, "ZD0" + f + ".png", bleed: true); if (s != null) zd.Add(s); }
            _bombFrames = zd.Count > 0 ? zd.ToArray() : null;
        }

        /// <summary>
        /// 判定區(receptor)一軌的兩支動畫。官方把它們分成兩個 .an,而**待機那支本身就是動畫**:
        /// <list type="bullet">
        /// <item><c>JUDGELINE.AN</c> — 待機**循環**,每軌 2 幀。使用者說的「判定區的圖示會閃爍」就是它。</item>
        /// <item><c>KEYDOWN_JUDGELINE.AN</c> — 按下時放一次的爆發,每軌 5 幀(重複同一張來控時長)。</item>
        /// </list>
        ///
        /// 🔴 舊版是**用檔名猜**的:待機 = judgeline1 一張、按下 = judgeline2..6。那條規則只有 NOTEIMAGE_5
        ///    對得上 —— NOTEIMAGE_6 的 judgeline2 是**待機的另一半**,被當成按下爆發的第一幀,於是閃爍整個
        ///    消失;NOTEIMAGE_8/9/10/11 的 judgeline_f2 同樣是待機的第二幀,而真正的按下幀叫
        ///    judgeline_f2_1 / _f2_2。
        ///
        /// 🔴 那兩支 .an(以及 *_f2_1/_f2_2.png)目前**不在 DATA/NOTEIMAGE 裡** —— 死檔探測沒看到有人讀
        ///    它們,整批被搬進 DATA_quarantine(正是因為這裡以前用猜的)。所以先試著讀 .an,檔案搬回來就
        ///    自動照官方走;讀不到才退回下面這份**照那兩支 .an 抄下來**的檔名規則:
        ///   • 編號式 6 張 (NOTEIMAGE_5):待機 = 1;按下 = 2,3,4,5,6
        ///   • 編號式 4 張 (NOTEIMAGE_6):待機 = 1,2;按下 = 3,4
        ///   • _f2 式 (8/9/10/11/pet/showtime):待機 = judgeline + judgeline_f2;按下 = judgeline_f2_1/_f2_2
        /// </summary>
        private void LoadJudgeLineArt(int lane, string d, Sprite[] idleAn, Sprite[] downAn)
        {
            var idle = LaneSlice(idleAn, lane);
            var down = LaneSlice(downAn, lane);

            if (idle == null)
            {
                var numbered = new List<Sprite>();
                for (int f = 1; f <= 6; f++)
                {
                    var s = SdoExtracted.LoadImage(NoteDir, d + "_judgeline" + f + ".png", bleed: true);
                    if (s == null) break;
                    numbered.Add(s);
                }
                if (numbered.Count > 0)
                {
                    // 磁碟上有幾張就分得出是哪一種:6 張 = NOTEIMAGE_5(待機只有第 1 張),否則 = NOTEIMAGE_6(待機 1,2)。
                    int idleCount = Mathf.Min(numbered.Count >= 6 ? 1 : 2, numbered.Count);
                    idle = numbered.GetRange(0, idleCount).ToArray();
                    if (down == null && numbered.Count > idleCount)
                        down = BurstFrames(numbered.GetRange(idleCount, numbered.Count - idleCount));
                }
                else
                {
                    var lst = new List<Sprite>();
                    var b = SdoExtracted.LoadImage(NoteDir, d + "_judgeline.png", bleed: true);
                    var f2 = SdoExtracted.LoadImage(NoteDir, d + "_judgeline_f2.png", bleed: true);
                    if (b != null) lst.Add(b);
                    if (f2 != null) lst.Add(f2);
                    if (lst.Count > 0) idle = lst.ToArray();
                }
            }
            if (down == null)
            {
                // _f2 式的按下幀。這兩張目前還在 DATA_quarantine,所以正常會落空 → 下面退回待機末幀。
                var lst = new List<Sprite>();
                var d1 = SdoExtracted.LoadImage(NoteDir, d + "_judgeline_f2_1.png", bleed: true);
                var d2 = SdoExtracted.LoadImage(NoteDir, d + "_judgeline_f2_2.png", bleed: true);
                if (d1 != null) lst.Add(d1);
                if (d2 != null) lst.Add(d2);
                if (lst.Count > 0) down = BurstFrames(lst);
            }

            _recIdleFrames[lane] = idle;
            _recIdle[lane] = idle != null && idle.Length > 0 ? idle[0] : null;
            // 按下沒有自己的幀(_f2_1/_f2_2 還在 quarantine)→ 用待機的最後一幀,至少按下去有反應。
            // 這正是舊版的外觀,所以退回這條不會比以前差。
            if ((down == null || down.Length == 0) && idle != null && idle.Length > 0)
                down = new[] { idle[idle.Length - 1] };
            _recDownFrames[lane] = down;
        }

        // NOTEIMAGE.AN 的組別(16 個一組 = 4 軌 × 4 幀,軌序 left/down/up/right = Dir5):
        //   0 = note 頭  1 = 長條身體  2 = 靠判定線那端的封口  3 = 尾端的封口
        private const int CapSlotHead = 2;
        private const int CapSlotTail = 3;

        /// <summary>
        /// 官方在這一軌的這個封口槽位**放了帽子圖嗎**。
        ///
        /// 放的是 note 頭(<c>*HoldHeadActive0</c>)就代表「這一端不畫封口」—— 那個槽位官方拿 note 頭
        /// 當填充。NOTEIMAGE_5/8/9/10/pet 的靠判定線端是這樣(只有 6/11/showtime 兩端都封)。
        ///
        /// 🔴 所以「有沒有這個檔案」不能拿來當判準,只有 .an 說了算。.an 讀不到(或短得不合理)時
        /// 回 true = 照舊畫,寧可多畫一頂也不要讓長條忽然變成沒有收邊的斷面。
        ///
        /// 🔴 反過來也一樣:.an **本身寫錯**就去修 .an,不要在這裡補 skin 特例。NOTEIMAGE_8 的原檔把
        /// **尾端**那一組也複製成 note 頭(四張 <c>*_LONG_BOTTOM(_D).PNG</c> 俱全卻沒有任何 .an 指到),
        /// 照走等於整條長條沒有尾帽(使用者回報「向上向下都不見了」)。修正檔在
        /// <c>art\upscaled\NOTEIMAGE\NOTEIMAGE_8\</c>,由 <c>NoteSkinAnCapSlotTests</c> 釘住。
        /// </summary>
        private static bool CapSlotHasArt(string[] anSlots, int group, int lane)
        {
            int i = group * 16 + lane * 4;
            if (anSlots == null || i >= anSlots.Length) return group == CapSlotTail;   // 資訊不足:尾端照舊畫,頭端不畫(舊行為)
            var name = anSlots[i] ?? "";
            return name.IndexOf("holdheadactive", System.StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>
        /// 這張尾帽圖的**不透明內容**在紋理裡的垂直重心(Unity 紋理座標:0 = 最下緣,height-1 = 最上緣)。
        /// <c>NaN</c> = 整張全透明 / 讀不到。
        ///
        /// 用途:分辨同一頂尾帽的「上緣封口」與「下緣封口」兩版。官方那兩張互為上下鏡像,所以
        /// **內容比較靠上的那張就是上緣封口那版** —— 這是圖本身就帶著的資訊,不必去猜檔名。
        ///
        /// 🔴 刻意不看檔名、也不看 skin 叫什麼:官方的命名根本不一致(NOTEIMAGE_6 叫 <c>*_long_head</c>,
        /// 其餘叫 <c>*_long_bottom_d</c>),而且 NOTEIMAGE_8 的 updown 那對是**對調存放**的 ——
        /// 以前就是為了它硬寫了一條 <c>EndsWith("NOTEIMAGE_8")</c> 的特例加一個翻轉旗標。改看內容之後
        /// 每個 skin 都自動歸位,將來多一個 skin 也不必再補特例。
        /// </summary>
        private static float CapContentCenterY(Sprite s)
        {
            var tex = s != null ? s.texture : null;
            if (tex == null) return float.NaN;
            var px = tex.GetPixels32();
            int w = tex.width, h = tex.height;
            double sum = 0.0;
            long n = 0;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                    if (px[row + x].a > 128) { sum += y; n++; }
            }
            return n > 0 ? (float)(sum / n) : float.NaN;
        }

        /// <summary>四軌合在同一個 .an 裡(順序 left/down/up/right,與 <see cref="Dir5"/> 相同)——
        /// 切出第 <paramref name="lane"/> 軌的那幾幀。長度不是 4 的倍數就當這份 .an 不可信,回 null。</summary>
        private static Sprite[] LaneSlice(Sprite[] all, int lane)
        {
            if (all == null || all.Length == 0 || all.Length % Keys != 0) return null;
            int per = all.Length / Keys;
            var slice = new Sprite[per];
            System.Array.Copy(all, lane * per, slice, 0, per);
            return slice;
        }

        /// <summary>把手上這幾張圖攤成官方按下爆發的節奏。官方 KEYDOWN_JUDGELINE.AN 的每一格就是一幀,
        /// 靠**重複同一張**控時長(NOTEIMAGE_6/8/9/10/11 都是 前者×3 → 後者×2)。已經有 5 張以上
        /// (NOTEIMAGE_5 的 2..6)就一張一幀照播。少了這一手,只有兩張的 skin 按下去只閃 0.06 秒,看不見。</summary>
        private static Sprite[] BurstFrames(IList<Sprite> src)
        {
            if (src == null || src.Count == 0) return null;
            if (src.Count >= 5) { var all = new Sprite[src.Count]; src.CopyTo(all, 0); return all; }
            var outp = new List<Sprite>(5);
            for (int i = 0; i < 3; i++) outp.Add(src[0]);
            for (int i = 0; i < 2; i++) outp.Add(src.Count > 1 ? src[1] : src[0]);
            return outp.ToArray();
        }

        // 3D-note skin: load the three beat-colour families (magenta / blue / green) from 3DNOTES\ as 4-frame glow sets.
        // One up-arrow glyph per family (NOTES_ / NOTES1_ / NOTES2_, frames 0..3), rotated per lane at draw time. Loaded
        // once and kept for the song; a family that fails to load leaves that slot null (ScrollNotes falls back to 2D).
        private void LoadNote3dFamilies()
        {
            if (_note3dFamily != null && _note3dFamily[0] != null && _note3dFamily[1] != null && _note3dFamily[2] != null) return;   // fully loaded → keep; retry on partial failure
            string dir = Path.Combine(SdoExtracted.Root, "3DNOTES");
            string[] prefix = { "NOTES_", "NOTES1_", "NOTES2_" };   // NoteBeatColor: 0=magenta, 1=blue, 2=green
            var fam = new Sprite[3][];
            for (int f = 0; f < 3; f++)
            {
                var frames = new Sprite[4]; bool ok = true;
                for (int i = 0; i < 4; i++) { frames[i] = LoadDdsSprite(Path.Combine(dir, prefix[f] + i + ".DDS")); if (frames[i] == null) ok = false; }
                if (ok) fam[f] = frames; else Debug.LogWarning("[note3d] missing/failed family " + prefix[f] + " under " + dir);
            }
            _note3dFamily = fam;
        }

        // Load a DXT1 note glyph (transparent-background arrow) as an UPRIGHT sprite. DdsLoader.LoadDxt1Alpha honours the
        // BC1 punch-through alpha AND flips V during decode (the arrow points UP before Note3dRot rotates it per lane) —
        // the flip is in-decode because the texture is uploaded non-readable, so a GetPixels32 flip here would throw.
        // ppu 1 to match the play field (1 design px = 1 world unit); the note head keeps its own material (SpawnNotes).
        private static Sprite LoadDdsSprite(string path)
        {
            var tex = LoadDdsTex(path, flipV: true);   // sprites flip V (DDS-top→row0 = upside-down otherwise)
            return tex != null ? Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect) : null;
        }

        // Load a keyed DXT1 note/board glyph as a Texture2D. flipV=false for a MESH texture (hold body — its UVs already
        // match the row-0-at-top convention); flipV=true for a sprite. Background colour is keyed out (LoadDxt1Alpha).
        private static Texture2D LoadDdsTex(string path, bool flipV, bool desilver = false)
        {
            if (!File.Exists(path)) return null;
            try { return DdsLoader.LoadDxt1Alpha(File.ReadAllBytes(path), flipV, desilver); }
            catch { return null; }
        }

        // 3D-note skin board art (the other half of the "3D" skin, beyond the coloured falling notes): swap the RECEPTORS
        // to the JUDGELINE grey arrow (one glyph, rotated per lane in UpdateHud) and the HOLD BODY to the LONG chevron
        // strip. Loaded from 3DNOTES\; a failed load leaves the current 2D art in place. Leaving the 3D skin (SetNoteType
        // → SetNoteBoardSkin) reloads the NOTEIMAGE art, so this is fully reversible.
        private void LoadBoard3dSkin()
        {
            string dir = Path.Combine(SdoExtracted.Root, "3DNOTES");
            _note3dDir = dir;
            var jl0 = LoadDdsSprite(Path.Combine(dir, "JUDGELINE_0.DDS"));               // receptor idle (grey up-arrow)
            var jl1 = LoadDdsSprite(Path.Combine(dir, "JUDGELINE_1.DDS"));               // keydown pulse frames
            var jl2 = LoadDdsSprite(Path.Combine(dir, "JUDGELINE_2.DDS"));
            // Official LONG textures are FULLY OPAQUE (exe loads with ColorKey=0, zero transparent texels): load them
            // verbatim — NO keying, NO desilver, NO flip (Unity v == D3D v for the unflipped decode, so the official
            // mesh UVs apply directly). The dark 68,51,51 interior is part of the look; silhouette comes from geometry.
            _capTex = LoadDdsOpaque(Path.Combine(dir, "LONG_0_0.DDS"));
            if (_capTex != null)
            {
                if (_capMeshMat == null) _capMeshMat = new Material(Shader.Find("Sdo/NoteCutout") ?? Shader.Find("Sprites/Default"));
                _capMeshMat.mainTexture = _capTex;
            }
            var down = new List<Sprite>(); if (jl1) down.Add(jl1); if (jl2) down.Add(jl2);
            for (int c = 0; c < Keys; c++)
            {
                if (jl0 != null) { _recIdle[c] = jl0; _recIdleFrames[c] = null; }   // 3D skin 的待機是單張灰箭頭,不閃
                if (down.Count > 0) _recDownFrames[c] = down.ToArray();
                _recDownStart[c] = -1f;                                                  // snap receptors to idle
            }
            if (jl0 != null) PlaceReceptors(receptor3dScale);                            // re-size receptors for the 128px JUDGELINE glyph (fixes 太大)
            ReloadHoldBody();   // load LONG_0_1 body (opaque, official) + re-point spawned bodies
        }

        // Plain opaque DDS decode (official: ColorKey disabled, textures carry no transparency). wrap=Repeat because the
        // official body V mapping goes negative on long holds (V = 1 − z/31.2, tail-anchored).
        private static Texture2D LoadDdsOpaque(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var t = DdsLoader.Load(File.ReadAllBytes(path));
                if (t != null) t.wrapMode = TextureWrapMode.Repeat;
                return t;
            }
            catch { return null; }
        }

        // (Re)load the LONG body texture (opaque, verbatim — official) and re-point every spawned hold body to it.
        private void ReloadHoldBody()
        {
            if (string.IsNullOrEmpty(_note3dDir)) return;
            var longTex = LoadDdsOpaque(Path.Combine(_note3dDir, "LONG_0_1.DDS"));
            if (longTex == null) return;
            for (int c = 0; c < Keys; c++) _holdTex[c] = longTex;
            var cs = Shader.Find("Sdo/NoteCutout");
            foreach (var n in _notes)
            {
                if (n == null || n.Done || !n.Body) continue;
                int c = n.Note.Lane; if (c < 0 || c >= Keys) continue;
                var mr = n.Body.GetComponent<MeshRenderer>();
                if (mr && mr.sharedMaterial) { mr.sharedMaterial.mainTexture = longTex; if (cs) mr.sharedMaterial.shader = cs; }
            }
        }

        // Lazily build the official cap TRIANGLE for a hold: base edge (±0.5, 0) welded at the tail end, tip at
        // (0, −LongCapLenRatio) pointing AWAY from the judge line — real geometry like LONG.MSH (verts 0/1/2), sampling
        // LONG_0_0 v 0.5574→0.8939. Scaled by holdW on both axes in ScrollNotes. The junction texel rows of LONG_0_0 and
        // LONG_0_1 are identical, so the butt joint against the body quad is seamless by construction.
        private GameObject CreateHoldCap()
        {
            var go = new GameObject("HoldCap3d");
            go.transform.SetParent(NoteVisualRoot, false);   // pooled with the note visual that owns it (position is set world-space in ScrollNotes)
            var mf = go.AddComponent<MeshFilter>(); var mr = go.AddComponent<MeshRenderer>();
            mf.mesh = new Mesh
            {
                vertices = new[] { new Vector3(-0.5f, 0f), new Vector3(0.5f, 0f), new Vector3(0f, -LongCapLenRatio) },
                uv = new[] { new Vector2(LongCapU0, LongCapV0), new Vector2(LongCapU1, LongCapV0), new Vector2(LongCapUTip, LongCapVTip) },
                triangles = new[] { 0, 2, 1, 0, 1, 2 }   // both windings (no culling worry)
            };
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
            mr.sharedMaterial = _capMeshMat;
            mr.sortingOrder = 3;   // same plane as the body; the note head (6) covers both
            return go;
        }

        // Re-place the receptor SpriteRenderers at ReceptorW×widthMul with the CURRENT _recIdle sprite. Needed because the
        // receptors are positioned once in BuildBoard; swapping the sprite (2D↔3D) changes its native size, so the stale
        // localScale must be recomputed for the new glyph (the 3D JUDGELINE is 128px vs the smaller 2D receptor → 太大).
        private void PlaceReceptors(float widthMul)
        {
            float eff = widthMul * (_note3dMode ? note3dMaster : 1f);   // 3D skin: receptors scale with the proportional master
            for (int c = 0; c < Keys; c++)
                if (_receptors[c] != null)
                {
                    _receptors[c].sprite = _recIdle[c];
                    PlaceAspect(_receptors[c], PX(LaneLeftX[c] + LaneCx0), judgeLineY, ReceptorW * eff);
                    _recBaseScale[c] = _receptors[c].transform.localScale;   // base for the press-pulse (UpdateHud)
                }
        }

        private void LoadArt()
        {
            LoadBoardArt();
            // lane click-flash strips (notes_board_click1..4.png) live in NOTEIMAGE root, not the skin folder
            var boardDir = Path.Combine(SdoExtracted.Root, "NOTEIMAGE");
            for (int c = 0; c < Keys; c++) _clickFlashSpr[c] = SdoExtracted.LoadImage(boardDir, "notes_board_click" + (c + 1) + ".png");
            // bleed: dilate the transparent-white matte so bilinear filtering can't pull white into the glyph
            // edges (the "white halo" the source PNGs show on PERFECT/COOL/… and the combo digits).
            _judgeSprites[0] = SdoExtracted.Eft("PERFECT.PNG", bleed: true);
            _judgeSprites[1] = SdoExtracted.Eft("COOL.PNG", bleed: true);
            _judgeSprites[2] = SdoExtracted.Eft("BAD.PNG", bleed: true);
            _judgeSprites[3] = SdoExtracted.Eft("MISS.PNG", bleed: true);
            for (int i = 0; i < 10; i++) _comboDigitSprites[i] = SdoExtracted.Eft("0" + i + ".PNG", bleed: true);
            var sd = SdoExtracted.LoadAn(SdoExtracted.GameplayUiDir, "teamfree.an");
            for (int i = 0; i < 10 && i < sd.Length; i++) _scoreDigitSprites[i] = sd[i];
            var bf = new List<Sprite>();                 // (6) hit burst = EFT_13/EFT_HIT0..11.PNG
            for (int i = 0; i < 12; i++) { var s = SdoExtracted.LoadImage(SdoExtracted.EftDir(13), "EFT_HIT" + i + ".PNG"); if (s != null) bf.Add(s); }
            _burstFrames = bf.Count > 0 ? bf.ToArray() : null;
            LoadLnEndArt(8);                            // stock skin = EFT_13 (index 8) -> its .DGE LnEnd slot = PUBLICEFT
            _readyFrames = new List<Sprite>().ToArray();
            var rf = new List<Sprite>(); for (int i = 0; i < 10; i++) { var s = SdoExtracted.Eft("READY0" + i + ".PNG"); if (s != null) rf.Add(s); } _readyFrames = rf.ToArray();
            var gf = new List<Sprite>(); for (int i = 1; i <= 6; i++) { var s = SdoExtracted.Eft("GO0" + i + ".PNG"); if (s != null) gf.Add(s); } _goFrames = gf.ToArray(); // GO01..GO06 only
            LoadEmojiArt();   // head-emoji cut-in PNG sequences (UI/PLAYINGEXP)
            // EFT_HIT bursts are opaque-on-black -> additive blending so black reads as transparent glow.
            // SpriteRenderers SHARING one custom material instance all sample the last-written sprite -> bursts
            // cross-bleed & jitter. Each burst clones its OWN instance of this template (see SpawnBurst) so every burst
            // animates independently. (This is NOT a [PerRendererData] question — measured on 6000.4.11f1, a shared
            // instance of Sprites/Default bleeds identically even though it does tag _MainTex that way. The rule is:
            // one material instance may only serve renderers drawing the SAME texture; no material at all is safe.
            // See SdoExtracted.PremultSpriteMaterial — the 結算 panel shipped this exact bug once.)
            var sh = Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Sprites/Default");
            _addMat = new Material(sh);
            // HP glow gets its OWN clip-capable additive instance: same look as Particles/Additive, plus a world-X
            // scissor (Sdo/HpGlowClip) so a low-HP flash can't spill past the bar frame. Falls back to plain additive.
            _hpGlowMat = new Material(Shader.Find("Sdo/HpGlowClip") ?? sh);
        }

        /// <summary>載譜（官方 .gn / 外部 osu·sm），成功後套用譜面修整：<see cref="collapseShortHolds"/> 開著、
        /// **且這首是外部轉檔譜 (osu/sm/mc)** 時把「無理的短 long note」(短於 180 BPM 的 16 分音符) 收成一般 note
        /// ——官方 k.gn 與 .gn 歌曲包是原生譜，一律照原樣打（見 OsuBeatmap.AllowsShortHoldCollapse）；
        /// <see cref="songBombs"/> **關著**時把炸彈整顆拿掉。
        /// 修整必須在這裡、在任何吃 _map 的東西 (判定、TotalNotes→滿分、捲動、note 皮) 建起來之前做完。</summary>
        private bool LoadChart()
        {
            if (!LoadChartRaw()) return false;
            if (collapseShortHolds && _map != null && OsuBeatmap.AllowsShortHoldCollapse((SongFormat)chartFormat))
            {
                int collapsed = _map.CollapseShortHolds();
                if (collapsed > 0) Debug.Log($"[Step1] collapsed {collapsed} short hold(s) (< {OsuBeatmap.ShortHoldMaxMs:0.#} ms) into taps");
            }
            EnsureExternalDance();
            // 炸彈**在生成外部舞蹈之後**才拿掉：舞蹈長度平常是自己重讀每個難度的原始譜量的（不受這些修整影響），
            // 但量不到時會退回手上這張 _map 的頭尾時間 —— 開/關這個選項不該讓同一首歌生出兩種舞。
            // 之後才建的東西（判定、TotalNotes→滿分、打拍音時間軸、note 皮）看到的就是一張沒有炸彈的譜。
            if (!songBombs && _map != null)
            {
                int bombs = _map.RemoveBombs();
                if (bombs > 0) Debug.Log($"[Step1] 歌曲炸彈關閉：移除 {bombs} 顆 mine");
            }
            return true;
        }

        /// <summary>An external (osu/StepMania) song ships no choreography — its DANCE/&lt;id&gt;.DPS doesn't exist — so
        /// generate one for it (once; recorded in the song folder's sdoinfo.dat, see <see cref="ExternalDps"/>) and dance
        /// that instead of looping the single fallback clip. Official songs and songs whose .dps is already there are
        /// untouched.</summary>
        private void EnsureExternalDance()
        {
            if (editorMode) return;   // 編輯器只校時/看譜，不生成也不寫 .dps 進使用者的歌資料夾
            if (chartFormat == 0 || _map == null || string.IsNullOrEmpty(externalFolder)) return;
            if (!string.IsNullOrEmpty(dpsPath) && File.Exists(Path.Combine(SdoExtracted.Root, dpsPath))) return;
            // songBpm / songChartPaths 是**這首歌**的（不是這張譜的）：一首歌一支舞，換難度不換舞（見 Sdo.Osu.DanceInputs）。
            string generated = ExternalDps.EnsureFor(externalFolder, externalSongKey, externalPackId, _map, songBpm,
                                                     chartFormat, chartSeed, songChartPaths, songChartIndices);
            if (!string.IsNullOrEmpty(generated)) dpsPath = generated;   // absolute → LoadAsset uses it as-is
        }

        private bool LoadChartRaw()
        {
            // 打拍測試：不讀 .gn、也不放音樂 —— 用固定 BPM 的等距音符當節拍器（assist tick 每顆音符響一聲）。
            if (beatTestMode)
            {
                _map = BeatTestChart.Build(beatTestBpm, BeatTestDurationSec, BeatTestChart.RightLane, beatTestBeatsPerNote);
                return true;
            }
            // (1) external user chart (osu / StepMania) from the Songs/ folder — the difficulty was already resolved
            // to a concrete chart file at selection time (see SongSelectScreen.OnConfirm / FrontendApp.StartGameplay).
            if (chartFormat != 0 && !string.IsNullOrEmpty(chartPath) && File.Exists(chartPath))
            {
                // 四種格式的解析在 ExternalChartIO —— 生成編舞時要用同一套去量這首歌的每個難度。
                try { _map = ExternalChartIO.Parse(chartFormat, chartPath, chartIndex, chartSeed); }
                catch (Exception ex) { Debug.LogError($"[Step1] external chart parse failed: {ex.Message}"); _map = new OsuBeatmap(); }
                if (_map.Bpm <= 0.0) _map.Bpm = 120.0;   // guard: a chart with no parseable BPM must not feed 0 into the judge windows
                if (chartLevel > 0) _map.Level = chartLevel;   // LV label = the song-select 星數×7 level (Parse/ToBeatmap don't know it)
                // external charts have no count-in → push the first note out so it scrolls in from the edge (see ApplyLeadIn).
                // 編輯器例外：回 0（見 ExternalLeadInMsFor）—— 編譜要 WYSIWYG，音符要落在真實音檔時間上（時間讀數＝StepMania 的秒數）。
                // .gn 歌曲包例外：它是原生 SDO 譜，本來就自帶無聲 count-in（type-10 音樂起止 → MusicStartOffsetMs，
                // 跟內建歌一模一樣），再疊一次 lead-in 會把整張譜推離它自己的音樂。
                if (_map.HitObjects.Count > 0 && chartFormat != 3)
                {
                    int leadIn = ExternalLeadInMsFor(editorMode, (int)_map.FirstNoteMs);
                    if (leadIn > 0) _map.ApplyLeadIn(leadIn);
                }
                if (_map.HitObjects.Count > 0) { Debug.Log($"[Step1] loaded external {Path.GetFileName(chartPath)}: {_map.HitObjects.Count} notes, bpm {_map.Bpm}, lv {_map.Level}"); return true; }
                Debug.LogError("[Step1] external chart has no 4K notes: " + chartPath); return false;
            }

            // (2) official .gn chart
            if (!string.IsNullOrEmpty(gnPath) && File.Exists(gnPath))
            {
                _map = GnChart.Load(File.ReadAllBytes(gnPath), difficulty, GnKeyTable.SeedsFor(gnPath));
                if (_map.HitObjects.Count > 0) { Debug.Log($"[Step1] loaded {Path.GetFileName(gnPath)}: {_map.HitObjects.Count} notes, bpm {_map.Bpm}"); return true; }
            }
            var path = Path.Combine(Application.streamingAssetsPath, "Step1", "chart.osu");
            if (!File.Exists(path)) { Debug.LogError("[Step1] no chart (.gn or .osu)"); return false; }
            _map = OsuBeatmapParser.Parse(File.ReadAllText(path));
            return true;
        }

        /// <summary>Candidate LCG seeds for an external .gn: the pack's own key for THIS chart first, then the shared
        /// pool from the key table. [NX] gives every chart a distinct key, so the pack's own is what actually opens it;
        /// the pool is there for a pack shipped without a sidecar (or with a stale one) whose charts happen to use the
        /// common seeds. Pure — public for tests.</summary>
        public static uint[] GnSeedsFor(long ownSeed) => GnSeedsFor(ownSeed, GnKeyTable.SdomSeeds);

        /// <summary>Testable core of <see cref="GnSeedsFor(long)"/>: own key first, then the pool minus a duplicate.</summary>
        public static uint[] GnSeedsFor(long ownSeed, uint[] pool)
        {
            pool = pool ?? Array.Empty<uint>();
            if (ownSeed <= 0) return pool;
            uint own = (uint)ownSeed;
            var list = new List<uint>(pool.Length + 1) { own };
            foreach (var s in pool) if (s != own) list.Add(s);
            return list.ToArray();
        }

        private IEnumerator LoadAndPlayAudio()
        {
            if (observeBurstMode)
            {
                // no music, no READY/GO opening: park the gameplay clock far ahead so the song timer stays "-:-" and
                // the dancer holds its standby idle (negative dance time). Bursts are fired manually (keys 1-5 / F4).
                _clockStart = Time.timeAsDouble + 1e9; _started = true;
                _audioReady = true;   // no song to wait for → the loading screen can reveal
                yield break;
            }
            // 打拍測試：完全不放音樂（節拍音是 assist tick 排出來的）。沒有這道門，下面那個 fallback 會把
            // 示範曲 Bassdrop.mp3 撈出來播 —— 校時的時候背後放歌是最不該發生的事。
            if (beatTestMode) { _audioReady = true; StartCoroutine(EditorOpeningCo()); yield break; }
            yield return LoadOsuKeysoundsCo();
            bool externalTrackMissing = chartFormat != 0 &&
                (IsVirtualOsuTrack || string.IsNullOrEmpty(oggPath) || !File.Exists(oggPath));
            if (externalTrackMissing)
            {
                Debug.Log(IsVirtualOsuTrack ? "[keysound] virtual osu track: using silent transport" : "[Step1] external audio missing: using silent transport");
                _audioReady = true;
                if (editorMode) { StartCoroutine(EditorOpeningCo()); yield break; }
                _clockStart = Time.timeAsDouble + OpeningParkSec;
                _started = true;
                StartCoroutine(OpeningSequence());
                yield break;
            }
            string path = (!string.IsNullOrEmpty(oggPath) && File.Exists(oggPath))
                ? oggPath : Path.Combine(Application.streamingAssetsPath, "Step1", "Bassdrop.mp3");
            // 走哪個解碼器看**檔案內容**，不是副檔名 —— 外面撿來的歌曲庫常有名不符實的檔（[NX] 那包就有 4 個
            // Ogg 取名叫 .mp3）。餵錯解碼器不會報錯，只會解出 0 個取樣 → 這首歌整首沒聲音。見 AudioFileType。
            var kind = AudioFileType.Of(path);
            if (kind == AudioKind.Mp3 && File.Exists(path))
            {
                // Unity can't decode mp3 from a file on desktop → decode with the bundled NLayer on a worker thread.
                // osu (chartFormat 1) and StepMania (2) decode mp3 to different positions; match the chart's home game
                // so it lines up at global-offset 0 (see Mp3Decoder.Mp3Sync). Non-external mp3 (dev fallback) → StepMania.
                // .gn 歌曲包 (3) 也走 Osu：那譜是照原版 .ogg 打的，包裡的 mp3 是後來轉出來的，而轉檔器一定會在檔頭
                // 塞編碼器延遲(priming)。Osu 這條正好是「把 priming 修掉」，解出來的位置才會回到原版 ogg 的時間。
                var sync = Mp3SyncFor(chartFormat);
                // 譜面編輯器會把這個換成「解過就直接給、還會預抓前後兩首」的快取（EditorAudioCache）——
                // 整包 mp3 的歌一首一秒多的解碼，校時時全卡在換歌上。正式遊玩沒設，就每次自己解。
                var task = mp3Decoder != null ? mp3Decoder(path, sync)
                                              : System.Threading.Tasks.Task.Run(() => Mp3Decoder.Decode(path, sync));
                while (!task.IsCompleted) yield return null;
                var clip = Mp3Decoder.ToClip(task.Result, "mp3song");
                if (clip != null) { _audio.clip = clip; _audio.volume = AudioMix.Music; }
                else Debug.LogWarning("[Step1] mp3 decode failed: " + path);
            }
            else
            {
                // ogg (official + external) and wav decode natively via UnityWebRequestMultimedia.
                var type = kind == AudioKind.Ogg ? AudioType.OGGVORBIS
                         : kind == AudioKind.Wav ? AudioType.WAV
                         : AudioType.MPEG;
                using (var req = UnityWebRequestMultimedia.GetAudioClip(SdoExtracted.FileUri(path), type))
                {
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success) { _audio.clip = DownloadHandlerAudioClip.GetContent(req); _audio.volume = AudioMix.Music; }   // 遊戲音樂 音量
                    else Debug.LogWarning("[Step1] audio unavailable (ok for headless): " + req.error);
                }
            }
            _audioReady = true;   // song decoded (or failed) → the loading screen may now reveal the stage
            // 編輯器：沒有 READY/GO 開場，也不自己起播 —— 停在 0ms 等使用者按播放（見 EditorOpeningCo）。
            if (editorMode) { StartCoroutine(EditorOpeningCo()); yield break; }
            // Park the clock far ahead (song stopped, notes hidden, dancer idle, timer "- : -") and DON'T start the
            // song here. OpeningSequence() starts the song + gameplay clock the instant the GO animation finishes, so
            // they never begin mid-opening (mirrors the original: state-4 AdvancePlayTime fires when the GO anim slot
            // clears, not on a fixed lead-in).
            _clockStart = Time.timeAsDouble + OpeningParkSec;
            _started = true;
            StartCoroutine(OpeningSequence());
        }

        // (5) opening READY -> GO animation (EFT_2 READY00..09 / GO00..14) + ready voice VOICE_0003.
        // Starts the song + gameplay clock at the very end, once GO has fully played out.
        private IEnumerator OpeningSequence()
        {
            _gaugeGlowFromStart = false;   // re-arm: head glow stays dark until this song's intro finishes and playback starts
            // Hold the whole opening until the loading screen has revealed the stage (so READY/GO never plays under the
            // loading cover and gets caught mid-animation on reveal). BootRevealCo sets _bootRevealed once it's faded out.
            while (!_bootRevealed) yield return null;
            // Camera-only intro: hold the note board hidden while the crane flies in (measured from scene start so the
            // camera always gets its full lead, even if the audio loaded slowly), then reveal the track. The board +
            // receptors appear together with the READY text — decompiled state 3->4 (NoteBoard_Update / StartPlayback).
            if (_introStartRt >= 0f)
            {
                while (Time.realtimeSinceStartup - _introStartRt < openingIntroSec) yield return null;
                if (!showtimeMode) SetTrackVisible(true);   // non-showtime reveals the board here; showtime reveals it later
            }
            // ShowTime opening ORDER (user-confirmed): SHOW TIME banner spirals in + HOLDS → energy-bar 3-stage intro
            // anim runs under it → +0.5s beat → banner slides out/disappears → note board appears → ready-go. Board +
            // energy bar stay HIDDEN during the banner spiral-in; the banner does NOT leave until the energy anim is done.
            if (showtimeMode)
            {
                SetTrackVisible(false);                                 // note board hidden during banner + energy anim
                SetEnergyHudVisible(false);                             // energy bar hidden until the intro anim reveals it
                PlaySe("showtime");                                     // 0x4e "SHOW TIME!" announce
                TriggerBanner();                                        // banner spirals in, then holds at centre
                float bs = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - bs < bannerInSec) yield return null;   // wait for the spiral-in only
                PlaySe("showtimeenegy");                                // 0x4d — energy bar appears + 3-stage fill demo
                yield return EnergyIntroAnim();                         // banner HOLDS at centre through the whole demo
                yield return new WaitForSecondsRealtime(0.5f);          // beat after the energy anim finishes
                DismissBanner();                                        // NOW the banner slides out (down)
                while (!BannerGone) yield return null;                  // wait until it has fully left
                SetTrackVisible(true);                                  // NOW the note board appears → ready-go next
            }
            if (_readyGo != null)
            {
                float t0 = Time.realtimeSinceStartup;
                PlaySe(showtimeMode ? "readygo_showtime" : "VOICE_0003");  // 0x4c readygo_showtime in ShowTime, else the normal ready-go voice
                _readyGo.enabled = true;
                yield return PlayFrames(_readyFrames, 1.0f);      // READY: 10 frames @ 100ms/frame (decompiled StartReadyAnim param=100), native size
                while (Time.realtimeSinceStartup - t0 < 2.0f) yield return null;  // HOLD on READY — wait for the voice's "go" cue
                // GO frames: 01-03 = "GO!" appearing (G->Go->GO), 04-06 = it blurs/fades out. So play the
                // appear half, HOLD the sharp full "GO!", then play the disappear half — not all 6 straight.
                int half = Mathf.Max(1, _goFrames.Length / 2);
                yield return PlayFrameRange(_goFrames, 0, half, 0.1f);        // appear (native size)
                float h0 = Time.realtimeSinceStartup; while (Time.realtimeSinceStartup - h0 < 0.5f) yield return null;  // hold "GO!"
                yield return PlayFrameRange(_goFrames, half, _goFrames.Length, 0.1f);  // disappear
                _readyGo.enabled = false;
            }
            // GO is done -> start the song and the gameplay clock together. Both use the same StartLeadSec offset on
            // their own time base (dspTime / timeAsDouble) so the audio and the chart stay aligned, as before. Runs even
            // if the READY/GO overlay was missing, so the song never fails to start.
            // Delay the music by the chart's music-start offset (type-10 音樂起止 marker) so the leading count-in
            // measure is silent. The NOTES stay on the beat-0 clock (they scroll in during the count-in). The DANCER
            // is anchored separately to the FIRST NOTE (its DanceTimeSec subtracts _danceStartSec): the DPS spans
            // first→last note, so on a long-intro chart (marker ≪ first note) the dancer holds the standby idle
            // through the whole intro and starts the choreography on the first downbeat — not on the marker, which
            // would make it lead the song (sdom1226: marker beat 0 vs first note ~5.4 s ⇒ was 5.4 s early).
            double markerSec = (useMusicStartOffset && _map != null) ? _map.MusicStartOffsetMs / 1000.0 : 0.0;
            _musicStartDelaySec = markerSec;   // 只放 type-10 無聲數拍;手動 offset(songOffsetMs)＋全曲 offset(GlobalSongOffsetMs)一律走 MusicCountInSec，別在這裡折進去(會雙重套用)
            // 音樂與舞蹈的 offset **各走各的**:音樂 = songOffsetMs(→ MusicCountInSec，挪音檔位置),舞蹈 = dpsOffsetMs
            // (只挪舞者)。兩者互不連動,預設都 0;音符/判定永遠釘在譜面時鐘。這樣「音樂對音符是準的、只有舞者
            // 飄」可以單獨修舞者而不動音樂。正值都是往後挪。
            double danceOffsetSec = dpsOffsetMs / 1000.0;
            _danceStartSec = ((useMusicStartOffset && _map != null) ? Math.Max(markerSec, _map.FirstNoteMs / 1000.0) : 0.0) + danceOffsetSec;
            Debug.Log($"[dps-offset] gn={System.IO.Path.GetFileName(gnPath ?? "?")} dps={System.IO.Path.GetFileName(dpsPath ?? "?")} songOffsetMs={songOffsetMs} dpsOffsetMs={dpsOffsetMs} marker={markerSec:F2}s firstNote={(_map != null ? _map.FirstNoteMs / 1000.0 : -1):F2}s -> danceStart={_danceStartSec:F2}s");  // TODO 診斷用，查完刪
            // 兩段前導(共用 lead + 無聲數拍 + offset)都是**譜面時間**;dspTime 是真實時間,所以除以流速換回真實秒數。
            // 開場排程走 GameRate.ScheduleMusic(能處理負 count-in:offset 負得比前導多時 clip 第 0 秒已來不及播 →
            // 從中途切入),餵的是 feat 管線的 MusicCountInSec(= marker + songOffsetMs + GlobalSongOffsetMs)。
            GameRate.ScheduleMusic(AudioSettings.dspTime, StartLeadSec, MusicCountInSec, _musicRate,
                                   out _songStartDspTime, out double playAtDsp, out double clipSkipSec);
            OnOsuTransportStarted();
            if (_audio != null && _audio.clip != null)
            {
                _audio.pitch = _timeScale;
                if (clipSkipSec >= _audio.clip.length)
                    Debug.LogWarning($"[Step1] offset {(songOffsetMs + GlobalSongOffsetMs):F0}ms 把音樂整首推到 clip 之外 — 這首不播音樂");
                else
                {
                    if (clipSkipSec > 0.0) _audio.time = (float)clipSkipSec;
                    _audio.PlayScheduled(playAtDsp);
                }
            }
            _clockStart = Time.timeAsDouble + StartLeadSec;   // timeAsDouble 已被 timeScale 縮放 → 譜面時鐘自動吃流速
            _clock.Reset();   // re-seed the smoothing clock onto the freshly-anchored timeline (drop any parked-clock frames)
            if (showtimeMode) _gaugeGlowFromStart = true;   // song is playing → head glow stays lit even at 0 fill (no key needed)
        }

        // The song audio's TRUE playback position, mapped back onto the beat-0 note timeline (seconds), or null when
        // the audio isn't actually playing: the READY/GO lead-in and the silent type-10 count-in (dspTime hasn't
        // reached the scheduled start), observe-burst mode (no clip), or after the clip finishes. dspTime is the sound
        // device's own clock; the clip plays from _songStartDspTime, and the notes lead the music by the count-in
        // (_musicStartDelaySec), so chart time = clip position + count-in. GameplayClock slews the note clock onto this.
        private double? AudioChartSeconds()
        {
            // 編輯器：dsp ↔ 譜面時間的映射是 EditorSeekMs 錨的，跟「有沒有音檔在播」無關 → 沒有音樂也交得出真值。
            // 打拍測試（F2）非交不可：那個模式**沒有音樂**，但你聽到的 click 是 PlayScheduled 排進 dsp 時鐘的。
            // 這裡若回 null，譜面時鐘就純靠 wall clock 自走 —— 於是「格線/判定」走 wall、「聽到的 click」走 dsp，
            // 兩支時鐘只在 seek 那一刻對過一次：會慢慢漂，而且視窗一失焦（wall 停、dsp 照跑）回來就固定錯開一段
            if (IsVirtualOsuTrack) return VirtualOsuChartSeconds();
            // （實測：+108ms → 切出去再切回來變 −104ms）。鎖上 dsp 之後，殘留的固定偏移才等於「這台機器的真實延遲」。
            // 暫停中不交：timeScale=0 讓 wall 停住而 dsp 照跑，拿它當真值會把時鐘推著往前爬。
            if (editorMode)
                return _paused ? (double?)null
                    : GameRate.ChartSecondsFromDsp(AudioSettings.dspTime, _songStartDspTime, _musicRate, MusicCountInSec);
            if (_audio == null || _audio.clip == null || !_audio.isPlaying) return null;
            double dsp = AudioSettings.dspTime;
            if (dsp < _songStartDspTime) return null;                // scheduled start not reached yet (still in lead-in)
            // clip position → beat-0 chart time。流速 r 時 clip 位置 = r×(dsp − 起播點),所以譜面時間也要乘 r
            // (r=1 時就是原式)。改速度/暫停會重新錨定 _songStartDspTime,這條式子因此永遠連續。
            return GameRate.ChartSecondsFromDsp(dsp, _songStartDspTime, _musicRate, MusicCountInSec);
        }

        // Energy-bar INTRO (online FUN_0040dc00/0040e210/0040e0f0 + demo blocks @360861-360887): the bar does NOT
        // slide in — WinMyEnergy is a plain full-screen window simply shown (the old slide-in was a remake invention;
        // the only XML slide-ins are the SHOWTIME banner and the song-title strip). The official demo tweens the gauge
        // 0→cap0→cap1→cap2 at 1200ms per stage (each band re-basing = green→yellow→red lap), then snaps to 0 and the
        // live fill takes over.
        private IEnumerator EnergyIntroAnim()
        {
            SetEnergyHudVisible(true);
            _energyIntroOffX = 0f;
            _energyIntroFill = 0f;                                   // demo fill starts empty
            for (int stage = 1; stage <= 3; stage++)                 // 3-stage stepped fill demo
            {
                float from = (stage - 1) / 3f, to = stage / 3f, s0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - s0 < energyIntroStageSec)
                { _energyIntroFill = Mathf.Lerp(from, to, (Time.realtimeSinceStartup - s0) / energyIntroStageSec); yield return null; }
                _energyIntroFill = to;
            }
            // Hold at FULL until the eased RED head (band 2, ≈500ms ease lag) actually reaches the tip, so stage 3
            // finishes drawing before the snap. Without this the red cleared mid-slide ("紅色那段沒畫完就直接清空").
            // Capped so it can never hang if the ease stalls.
            _energyIntroFill = 1f;
            float holdS0 = Time.realtimeSinceStartup;
            while (GaugeFullP - _gaugeCur[2] > 5f && Time.realtimeSinceStartup - holdS0 < 1.2f)
                yield return null;
            // official (FUN_0040dc00 demo @360861-360868): after the 3-stage sweep the gauge SNAPS to 0 in ~1ms — not a
            // slow shrink. Hard-reset the eased positions to empty so live tracking starts from 0 instantly.
            _energyIntroFill = -1f;
            _gaugeCur[0] = _gaugeCur[1] = _gaugeCur[2] = GaugeBaseP; _gaugeActive = 0;
        }

        // NATIVE pixel size: each READY/GO frame is drawn at its own texture width (×readyGoScale), NOT a fixed width —
        // the official .an blits each frame at its authored size. A per-skin skin like EFT_PET (198×55) is smaller than
        // the standard skins (300×100); forcing one width blew the small ones up (PET 太大).
        private float ReadyGoWidth(Sprite s) => (s != null ? s.rect.width : 300f) * readyGoScale;

        private IEnumerator PlayFrames(Sprite[] frames, float dur)
        {
            if (frames == null || frames.Length == 0) { yield return new WaitForSecondsRealtime(dur); yield break; }
            float t = 0;
            while (t < dur)
            {
                int fi = Mathf.Clamp((int)(t / dur * frames.Length), 0, frames.Length - 1);
                _readyGo.sprite = frames[fi];
                PlaceAspect(_readyGo, 400f, 300f, ReadyGoWidth(frames[fi]), -5f);   // centre of screen, over the avatar
                t += Time.deltaTime; yield return null;
            }
        }

        // play frames[from..to) holding each for secPerFrame (decompiled 100ms/frame)
        private IEnumerator PlayFrameRange(Sprite[] frames, int from, int to, float secPerFrame)
        {
            if (frames == null || frames.Length == 0) yield break;
            for (int i = from; i < to && i < frames.Length; i++)
            {
                _readyGo.sprite = frames[i];
                PlaceAspect(_readyGo, 400f, 300f, ReadyGoWidth(frames[i]), -5f);
                float t = 0; while (t < secPerFrame) { t += Time.deltaTime; yield return null; }
            }
        }

        // ---------- build ----------

        private SpriteRenderer NewSR(string name, Sprite spr, int order)
        {
            var sr = new GameObject(name).AddComponent<SpriteRenderer>();
            sr.sprite = spr; sr.sortingOrder = order; return sr;
        }

        // shared 1×1 white sprite (pixelsPerUnit 1 → 1 world-unit bounds), tinted per use for the solid energy bar.
        private static Sprite SolidSprite()
        {
            if (_solidSprite == null)
            {
                var t = new Texture2D(1, 1) { name = "SolidWhite" };
                t.SetPixel(0, 0, Color.white); t.Apply();
                _solidSprite = Sprite.Create(t, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            }
            return _solidSprite;
        }

        // energy-bar colour by level: 0 green, 1 yellow, 2 red (the g/y/r segments of the original meter).
        private static Color EnergyColor(int level) =>
            level >= 2 ? new Color(1f, 0.32f, 0.30f) :
            level == 1 ? new Color(1f, 0.83f, 0.28f) :
                         new Color(0.38f, 1f, 0.48f);

        // place a sprite keeping its native aspect, fitted to a column of width `w`, centered at (cx, cy) design.
        private void PlaceAspect(SpriteRenderer sr, float cx, float cy, float w, float z = 0f)
        {
            if (sr.sprite == null) { sr.transform.position = SdoLayout.ToWorld(cx, cy, z); return; }
            var b = sr.sprite.bounds.size;
            float h = b.x > 1e-4f ? w * b.y / b.x : w;
            SdoLayout.PlaceBox(sr, cx - w / 2f, cy - h / 2f, w, h, z);
        }

        /// <summary>Panel-relative design X → shifted by the horizontal anchor (屏幕左邊=+0 / 屏幕中央=+242.5). Wrap EVERY
        /// note-panel X (board, receptors, notes, holds, bursts, click strips, HP bar, score, judge word, combo) in this
        /// so the whole cluster moves as a unit. Non-panel HUD (中央名次/右側清單/旁觀) keeps its own screen anchor.</summary>
        private float PX(float designX) => designX + _panelOffsetX;

        /// <summary>Resolve the note-panel geometry from the two player settings (dropDirection + notesPanelLeft) into the
        /// live fields the renderer reads. Called once before BuildBoard places the receptors; cheap enough to re-call if
        /// the settings change (the board re-places its X every frame, so a live change of just the offset also takes).</summary>
        private void ApplyPanelLayout()
        {
            var layout = NotePanelLayout.Resolve(dropDirection, PanelLeftEffective);
            _panelOffsetX = layout.OffsetX;
            judgeLineY = layout.JudgeLineY;
            _scrollSign = layout.ScrollSign;
            // 向下：整塊 note board 上下顛倒，note 隱藏區(頂端 30px 缺角/血條後)也要跟著鏡射到板底 → 帶 [0, 570]。
            _clipTopY = layout.ClipTopY;
            _clipBottomY = layout.ClipBottomY;
            // 向下置中：血條移到 note board 下面（板底受擊線那頭）；其餘模式血條留頂端。
            _hpYOffset = (layout.Bottom && !PanelLeftEffective) ? hudHpDownYOffset : 0f;
            // 向下（含傾斜）：受擊線在板底，判定字＋COMBO＋數字整組往上讓開一點（左邊/置中都一樣）。
            _judgeComboYOffset = layout.Bottom ? hudJudgeComboDownYOffset : 0f;
            // 聊天框跟著同一組設定走：置中＋向下 → 搬到畫面右上，其餘留在官方的右下角。
            _chat?.SetPanelLayout(PanelLeftEffective, layout.Bottom);
        }

        /// <summary>Arrange the surrounding HUD (大分數 / 粉紅名次 N/M / 小人名+分數名單 / 底部 LV·時間) around the note
        /// board. These do NOT ride the board's PX offset — instead they sit to the sides so a centred board doesn't
        /// cover them, and swap sides by drop direction (官方 向下置中 = 向上置中 的水平鏡射):
        ///   左邊模式  → 沿用官方右側級聯（board 在左，右邊空著）。
        ///   向上置中 → 分數/名次/名單 靠右，LV·時間 留左下。
        ///   向下置中 → 分數/名次/名單 靠左，LV·時間 移右下。
        /// Called at the end of BuildHud (after the elements exist) and safe to re-call if the layout changes.</summary>
        private void LayoutSideHud()
        {
            bool center = !PanelLeftEffective;   // ShowTime 一律靠左 → 周邊 HUD 也照左邊模式的官方級聯排
            bool down = _scrollSign < 0;
            float scoreX, rankX, rNameX, rScoreX, attrX;
            if (!center) { scoreX = 290f; rankX = 429f; rNameX = 577f; rScoreX = 781f; attrX = hudAttrLeftX; }          // 左邊：官方預設
            else if (!down) { scoreX = hudScoreRightX; rankX = hudRankRightX; rNameX = hudRosterNameRightX; rScoreX = hudRosterScoreRightX; attrX = hudAttrLeftX; }   // 向上置中：靠右 + LV·時間 左下
            else { scoreX = hudScoreLeftX; rankX = hudRankLeftX; rNameX = hudRosterNameLeftX; rScoreX = hudRosterScoreLeftX; attrX = hudAttrRightX; }                 // 向下置中：靠左 + LV·時間 右下

            _scoreBaseX = scoreX;      // UpdateScoreDigits 每幀讀
            rankCenterX = rankX;       // UpdateRankDisplay 每幀讀
            rosterNameX = rNameX; rosterScoreX = rScoreX;
            if (_rosterName != null)   // 名單位置只在建/重排時套用 → 這裡重置
                for (int row = 0; row < RosterRows; row++)
                {
                    float y = rosterFirstY + row * rosterRowStep;
                    if (_rosterName[row] != null) _rosterName[row].Position = SdoLayout.ToWorld(rosterNameX, y, -3f);
                    if (_rosterScore[row] != null) _rosterScore[row].Position = SdoLayout.ToWorld(rosterScoreX, y, -3f);
                }
            PlaceAttrRow(attrX);
        }

        /// <summary>Move the bottom「LV: 时间:」label + LV value + time value as one group (keep the shipped relative
        /// offsets 0 / +36 / +132). The 歌曲名 label+value stay bottom-left in every mode.</summary>
        private void PlaceAttrRow(float baseX)
        {
            _attrBaseX = baseX;   // 量測完成後要能依同一 baseX 重排
            float fieldX = baseX + 132f;          // 「時間」欄左緣（維持原設計 336−204）
            float colX = fieldX + TimeMinW - CountdownDx;  // 冒號錨點＝分欄右緣：分右對齊到此、「: 秒」左對齊自此，冒號 x 恆定
            if (_lblAttr) SdoLayout.PlaceTopLeft(_lblAttr, baseX, 575f);
            if (_lvText) _lvText.transform.position = SdoLayout.ToWorld(baseX + 36f, 585f, -1f);    // 240−204
            if (_timeMin) _timeMin.transform.position = SdoLayout.ToWorld(colX, 585f, -1f);         // 分：右對齊
            if (_timeText) _timeText.transform.position = SdoLayout.ToWorld(colX, 585f, -1f);       // : 秒：左對齊（冒號固定）
            if (_timeTotal) _timeTotal.transform.position = SdoLayout.ToWorld(fieldX + _timeTotalDx, 585f, -1f); // 總長：釘在秒欄右側
        }

        private void BuildBoard()
        {
            ApplyPanelLayout();   // resolve 掉落方式/面板位置 → _panelOffsetX + judgeLineY + _scrollSign before the receptors are placed
            // Single framed board (NOTES_BOARD1.PNG, 315×600) over the stage backdrop. It keeps the chamfered top
            // corners + side frame, AND its lane-divider grid is 69px pitch (texture x 14,83,152,221,290) which
            // matches the 4 note lanes — so it MUST be drawn 1:1 native (PlaceTopLeft, no scaling) at boardX=0,
            // making texture x == design x so notes land exactly on the board lanes. Any stretch would skew them.
            // Opacity = boardAlpha MULTIPLIES the original per-pixel alpha (see ApplyBoardAlpha): preserves the real
            // alpha curve (inner detail), can exceed native to match the deep official board, keeps the cut-out
            // chamfer transparent — no backing rect. The original texture is cached so the slider can rebake live.
            _boardSrc = SdoExtracted.LoadTextureRaw(Path.Combine(SdoExtracted.Root, "NOTEIMAGE"), "notes_board1.png");
            if (_boardSrc != null) SdoExtracted.AlphaBleed(_boardSrc);
            if (_boardSrc != null)
            {
                _board = NewSR("Board", null, -30);
                _board.color = Color.white;
                ApplyBoardAlpha();
                _board.flipY = _scrollSign < 0;   // 向下：整塊 note board 上下顛倒（缺角/框跟著翻到下方對齊底部受擊線）
                SdoLayout.PlaceTopLeft(_board, PX(boardX), 0f, 10f);
            }
            for (int c = 0; c < Keys; c++)
            {
                _recDownStart[c] = -1f;   // idle (frame 1) until a press fires the burst
                var sr = NewSR("Receptor" + c, _recIdle[c], 0);
                PlaceAspect(sr, PX(LaneLeftX[c] + LaneCx0), judgeLineY, ReceptorW);
                _receptors[c] = sr;
            }
            // per-lane click-flash overlays (notes_board_click{c+1}): above the board (-30), behind the receptors
            // (0) + notes (5). Native 1:1 like the board so the 67px strip sits in its 69px lane (1px margin).
            // Clipped by the NoteClip mask like the notes: the strip art starts at the board surface (y12) which is
            // BEHIND the HP bar (y18..29) — the glow must stop at the judge area and never light the HP bar row.
            for (int c = 0; c < Keys; c++)
            {
                _clickFlashStart[c] = -1f;
                if (_clickFlashSpr[c] == null) continue;
                var fsr = NewSR("ClickFlash" + c, _clickFlashSpr[c], -20);
                fsr.color = new Color(1f, 1f, 1f, 0f); fsr.enabled = false;
                fsr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;
                fsr.sharedMaterial = new Material(Shader.Find("Sprites/Default"));   // own material: masked sprites must not batch (texture cross-bleed)
                // 向上: the strip emanates DOWN from the top board surface (y12). 向下: mirror it about the board centre
                // (y300) and flipY so the same glow emanates UP from the bottom receptors.
                float stripH = fsr.sprite != null ? fsr.sprite.bounds.size.y : 0f;
                fsr.flipY = _scrollSign < 0;
                SdoLayout.PlaceTopLeft(fsr, PX(LaneLeftX[c] + 1f), _scrollSign > 0 ? ClickStripTopY : (600f - ClickStripTopY - stripH), 9f);
                _clickFlashSr[c] = fsr;
            }
            // miss flash: the click glow sprite TILED across all 4 lanes → the SAME soft glow as the white click flash, just
            // red and covering every lane (per-lane strips render too faint on the outer lanes). One tiled renderer, so no
            // outer-lane fade-out. Driven by the same 3-frame click-flash cycle. Above the strips (-20), behind notes (5).
            // Clipped by the NoteClip mask like the strips — the red wash must not light the HP bar row either.
            var glowSpr = SdoExtracted.LoadImage(Path.Combine(SdoExtracted.Root, "NOTEIMAGE"), "notes_board_click1.png");
            _missOverlay = NewSR("MissFlash", glowSpr, -19);
            _missOverlay.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;
            _missOverlay.sharedMaterial = new Material(Shader.Find("Sprites/Default"));   // own material: masked sprites must not batch (texture cross-bleed)
            float trackW = LaneLeftX[Keys - 1] + 69f - LaneLeftX[0];
            if (glowSpr != null) { _missOverlay.drawMode = SpriteDrawMode.Tiled; _missOverlay.tileMode = SpriteTileMode.Continuous; _missOverlay.size = new Vector2(trackW, 558f); }
            float missY = ClickStripTopY + 279f;   // ≈ board centre; mirror about y300 for 向下 so the wash tracks the receptors
            _missOverlay.flipY = _scrollSign < 0;  // 向下：漸層亮端跟軌條光一樣翻向底部受擊線
            _missOverlay.transform.position = SdoLayout.ToWorld(PX(LaneLeftX[0] + trackW / 2f), _scrollSign > 0 ? missY : (600f - missY), 9f);
            _missOverlay.color = new Color(1f, 0f, 0f, 0f); _missOverlay.enabled = false;
            BuildNoteClip();
        }

        // a SpriteMask spanning the board's play band [_clipTopY, _clipBottomY] (向上 [30,600] / 向下 [0,570], mirrored
        // with the drop direction in ApplyPanelLayout); note head/tail (SpawnNotes), the lane click-flash strips and the
        // miss red wash (BuildBoard) are flagged VisibleInsideMask so they're clipped to it — never drawn over the HP
        // bar or past the (向下-flipped) board frame. Built after ApplyPanelLayout so _clipTopY/_clipBottomY are resolved.
        private void BuildNoteClip()
        {
            var go = new GameObject("NoteClip");
            var mask = go.AddComponent<SpriteMask>();
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var px = new Color32[16]; for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(px); tex.Apply();
            mask.sprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
            float h = _clipBottomY - _clipTopY, cy = (_clipTopY + _clipBottomY) / 2f;
            go.transform.position = SdoLayout.ToWorld(SdoLayout.Width / 2f, cy, 8f);
            go.transform.localScale = new Vector3((SdoLayout.Width + 200f) / 4f, h / 4f, 1f);   // wide (no horizontal clip) × the play band
        }

        // (Re)bake the board sprite from the cached original at the current boardAlpha multiplier. Cheap to call
        // only when boardAlpha changes; destroys the previous generated texture so live tuning doesn't leak.
        private void ApplyBoardAlpha()
        {
            if (_board == null || _boardSrc == null) return;
            var oldTex = _boardGenTex; var oldSprite = _board.sprite;
            _board.sprite = SdoExtracted.AlphaScaledSprite(_boardSrc, boardAlpha);
            _boardGenTex = _board.sprite != null ? _board.sprite.texture : null;
            if (oldSprite != null) Destroy(oldSprite);
            if (oldTex != null) Destroy(oldTex);
            _boardAlphaApplied = boardAlpha;
        }

        // Show/hide the whole gameplay panel — note board + receptors + per-lane click strips + the HP bar — as one
        // unit. Hidden during the opening camera intro so only the venue + crane show, then revealed with the READY
        // text (decompiled state 3->4). Click strips re-enable themselves on a hit and the HP glow is re-driven by
        // UpdateHpBar (which early-outs while _trackVisible is false), so on hide we just force them off.
        private void SetTrackVisible(bool on)
        {
            // 旁觀模式:音符板/受擊線/血條**永遠**不出(需求 10)。這裡是唯一的收口 ——
            // 開場揭示(OpeningSequence)之後還會再呼叫一次 SetTrackVisible(true),
            // 所以不能只在 Start 關一次,要在這個函式裡把旁觀夾進去。
            // 名單(SetRankingVisible)刻意還是跟著 on 走:旁觀者要看的正是誰領先。
            bool trackOn = on && !spectatorMode;
            _trackVisible = trackOn;   // UpdateHpBar 讀它早退 → 旁觀時不會被重新打開
            if (_board) _board.enabled = trackOn;
            // ShowTime mode has no HP bar (only the 集氣 energy gauge) — keep the whole HP widget hidden even when the
            // track is shown. UpdateHpBar also early-outs in ShowTime so it can't re-enable _hpGlow.
            bool hpOn = trackOn && !showtimeMode;
            if (_hpSolidBack) _hpSolidBack.enabled = hpOn;
            if (_hpBg) _hpBg.enabled = hpOn;
            if (_hpTex) _hpTex.enabled = hpOn;
            if (_hpBackFrame) _hpBackFrame.enabled = hpOn;
            if (_hpGlow) _hpGlow.enabled = hpOn;         // UpdateHpBar refines this (low HP -> off) once visible again
            for (int c = 0; c < Keys; c++)
            {
                if (_receptors[c]) _receptors[c].enabled = trackOn;
                if (!trackOn && _clickFlashSr[c] != null) _clickFlashSr[c].enabled = false;
            }
            // 3D-mesh 音符不是 SpriteRenderer，不吃上面那些 enabled；而且藏板子之後 ScrollNotes 通常也不會再被呼叫
            // （EnterResult → Update 直接 return），沒人幫它收 → 最後一幀的箭頭會留在畫面上。這裡直接收起整個 pool；
            // 要再顯示不必做事，ScrollNotes 每幀都會自己打開。
            if (!trackOn && _highway != null) _highway.visible = false;
            SetRankingVisible(on);   // hide the roster list + rank during the opening hold / observe mode
        }

        private GameObject CreateHoldBody(int col)
        {
            var go = new GameObject("HoldBody");
            var mf = go.AddComponent<MeshFilter>(); var mr = go.AddComponent<MeshRenderer>();
            var m = new Mesh
            {
                vertices = new[] { new Vector3(-0.5f, -0.5f), new Vector3(0.5f, -0.5f), new Vector3(0.5f, 0.5f), new Vector3(-0.5f, 0.5f) },
                uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) },
                triangles = new[] { 0, 1, 2, 0, 2, 3 }
            };
            mf.mesh = m;
            // 3D skin: SOLID cut-out hold body (opaque chevrons, clipped background + edges → no white fringe); else 2D alpha-blend.
            var bodyShader = Shader.Find(_note3dMode ? "Sdo/NoteCutout" : "Sprites/Default") ?? Shader.Find("Sprites/Default");
            mr.sharedMaterial = new Material(bodyShader) { mainTexture = _holdTex[col] };
            mr.sortingOrder = 3;
            return go;
        }

        // Build the note DATA only — no GameObjects. On a 5k–10k-note chart, spawning a SpriteRenderer + material
        // per note up front is a load-time hitch (thousands of Shader.Find + Instantiate) and tens of MB of idle
        // materials, when only ~100 are ever on-screen. The visuals are rented from a pool as notes scroll in
        // (RentVisual) and returned as they scroll off (ReturnVisual), so cost tracks the visible window, not chart
        // length. Notes are kept START-TIME-ASCENDING so the per-frame scans can window with NoteScan.
        private void SpawnNotes()
        {
            _notesByMapIndex.Clear();
            for (int mapIndex = 0; mapIndex < _map.HitObjects.Count; mapIndex++)
            {
                var h = _map.HitObjects[mapIndex];
                var runtime = new RuntimeNote(h, NoteBeatColor.Family(h.StartTimeMs, _map));
                _notes.Add(runtime);
                _notesByMapIndex.Add(runtime);
            }
            // window/break rely on ascending start (loaders sort, but be defensive). 判定時間相同時再比顯示時間 ——
            // StepMania warp(負 BPM)那一批音符判定時刻全部一樣、只有畫面位置不同,ScrollNotes 的提早 break 是照
            // 顯示順序走的,排錯會讓 warp 那批少畫幾顆。
            _notes.Sort((a, b) =>
            {
                int c = a.Note.StartTimeMs.CompareTo(b.Note.StartTimeMs);
                return c != 0 ? c : a.Note.ScrollTimeMs.CompareTo(b.Note.ScrollTimeMs);
            });
            _noteStarts.Clear();
            foreach (var n in _notes) _noteStarts.Add(n.Note.StartTimeMs);
            _firstAlive = 0;
            _bombPrevValid = false;   // 重新載譜:炸彈跨線游標重置(第一幀重新對齊 now)
        }

        private Transform NoteVisualRoot => _noteVisualRoot != null ? _noteVisualRoot
            : (_noteVisualRoot = new GameObject("NotesPool").transform);   // identity origin: transform.position writes are world-space, so parenting is neutral

        // Hand note n a pooled visual (no-op if it already holds one). Binds the CURRENT skin's head sprite and,
        // for a hold, the body texture/shader + tail sprite/flips — the same bindings SpawnNotes used to bake once,
        // now applied on rent so a live skin swap (F4 / ShowTime) reaches every note as it re-appears.
        private void RentVisual(RuntimeNote n)
        {
            if (n.Vis != null) return;
            var v = _visualFree.Count > 0 ? _visualFree.Pop() : CreateVisual();
            n.Vis = v;
            int c = Mathf.Clamp(n.Note.Lane, 0, Keys - 1);
            // reset the transient state a previous tenant may have left (colour tint / 3D rotation); sprite is set per-frame.
            v.Head.color = Color.white;
            if (v.Head.transform.localRotation != Quaternion.identity) v.Head.transform.localRotation = Quaternion.identity;
            v.Head.sprite = _noteFrames[c] != null ? _noteFrames[c][0] : _recIdle[c];
            n.Head = v.Head;
            if (n.Note.IsHold)
            {
                if (_holdTex[c] != null)
                {
                    if (v.Body == null) CreateVisualBody(v);
                    var sh = Shader.Find(_note3dMode ? "Sdo/NoteCutout" : "Sprites/Default") ?? Shader.Find("Sprites/Default");
                    if (sh) v.BodyMr.sharedMaterial.shader = sh;
                    v.BodyMr.sharedMaterial.mainTexture = _holdTex[c];
                    v.BodyMr.sharedMaterial.color = Color.white;
                    n.Body = v.Body;
                }
                if (_holdTail[c] != null && _holdCapAtTail[c])
                {
                    if (v.Tail == null) v.Tail = CreateCapRenderer("HoldTail");
                    v.Tail.sprite = _holdTail[c]; v.Tail.color = Color.white;
                    v.Tail.flipX = _holdTailFlipX[c]; v.Tail.flipY = _holdTailFlipY[c];   // mirror the shared combined-skin cap per lane
                    n.Tail = v.Tail;
                }
                // 靠判定線那端的封口(官方組 3)。只有真的有圖的 skin 才建 —— 見 CapSlotHasArt。
                if (_holdCapAtHead[c] && _holdCapHead[c] != null && _holdTail[c] != null)
                {
                    if (v.HeadCap == null) v.HeadCap = CreateCapRenderer("HoldHeadCap");
                    v.HeadCap.color = Color.white; v.HeadCap.flipX = false; v.HeadCap.flipY = false;
                    n.HeadCap = v.HeadCap;
                }
            }
            n.Cap3d = v.Cap3d;   // reuse the cap triangle if this bundle built one in a previous life
        }

        // Return note n's visual to the pool (idempotent). Disables every part and stashes the lazily-built cap back
        // on the bundle so the next hold on it reuses the triangle.
        private void ReturnVisual(RuntimeNote n)
        {
            var v = n.Vis;
            if (v == null) return;
            v.Head.enabled = false;
            if (v.Body) v.Body.SetActive(false);
            if (v.Tail) v.Tail.enabled = false;
            if (v.HeadCap) v.HeadCap.enabled = false;
            v.Cap3d = n.Cap3d;                       // ScrollNotes may have created it this life; keep it on the bundle
            if (v.Cap3d) v.Cap3d.SetActive(false);
            n.Head = null; n.Tail = null; n.HeadCap = null; n.Body = null; n.Cap3d = null; n.Vis = null;
            _visualFree.Push(v);
        }

        private NoteVisual CreateVisual()
        {
            var v = new NoteVisual();
            var head = NewSR("Note", null, 5);
            head.transform.SetParent(NoteVisualRoot, false);
            head.enabled = false;
            head.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;   // clipped to the note board (NoteClip mask)
            head.sharedMaterial = new Material(Shader.Find("Sprites/Default"));   // own material: masked sprites must not batch (texture cross-bleed)
            v.Head = head;
            _visualAll.Add(v);
            return v;
        }

        private void CreateVisualBody(NoteVisual v)
        {
            var go = CreateHoldBody(0);   // texture/shader re-bound per rent; the placeholder col is irrelevant to the mesh
            go.transform.SetParent(NoteVisualRoot, false);
            go.SetActive(false);
            v.Body = go; v.BodyMf = go.GetComponent<MeshFilter>(); v.BodyMr = go.GetComponent<MeshRenderer>();
        }

        /// <summary>長條某一端的封口 SpriteRenderer(兩端各一個,見 NoteVisual.Tail / HeadCap)。</summary>
        private SpriteRenderer CreateCapRenderer(string name)
        {
            var cap = NewSR(name, null, 4);
            cap.transform.SetParent(NoteVisualRoot, false);
            cap.enabled = false;
            cap.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;
            cap.sharedMaterial = new Material(Shader.Find("Sprites/Default"));   // own material -> no mask batch cross-bleed
            return cap;
        }

        // Skip the alive cursor past notes retired by hits/misses outside ScrollNotes, returning each skipped
        // note's visual so the pool never leaks the sprites of a note that was judged away and then out-scrolled by
        // the cursor. Called once per frame at the top of ScrollNotes.
        private void AdvanceAliveWindow()
        {
            while (_firstAlive < _notes.Count && _notes[_firstAlive].Done)
            {
                ReturnVisual(_notes[_firstAlive]);
                _firstAlive++;
            }
        }

        // Return every note's visual to the pool (e.g. song end, or an editor seek that re-arms the whole chart).
        private void ReturnAllVisuals() { foreach (var n in _notes) ReturnVisual(n); }

        private void BuildHud()
        {
            // HP bar (WinMyHp), official textures only, XML draw order (back->front):
            // bloodBG2 bg, MyHp fill (clipped to HP%), FullHp overlay, MyHpBack frame (black-keyed,
            // so its black centre is transparent and the fill shows through), HpEft glow at the edge.
            // Solid opaque base UNDER it all: bloodBG2 + the keyed frame are semi-transparent, and the hit bursts
            // (order 6, ~235px at the receptors) reach this row — without an opaque base they shine through the bar.
            var hpBaseTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            hpBaseTex.SetPixel(0, 0, Color.black); hpBaseTex.Apply();
            _hpSolidBack = NewSR("HpSolidBase", Sprite.Create(hpBaseTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f), 14);
            SdoLayout.PlaceBox(_hpSolidBack, PX(TrackCenterX - 123f), 15 + _hpYOffset, 246, 18);   // the MyHpBack frame's full rect (PX = 面板位置 左/中; _hpYOffset = 向下置中下移)
            _hpBg = NewSR("HpBg(bloodBG2)", SdoExtracted.Hud("bloodBG2.an"), 15); SdoLayout.PlaceBox(_hpBg, PX(HpPos.x), HpPos.y + _hpYOffset, HpSize.x, HpSize.y);
            _hpTex = NewSR("HpFill", SdoExtracted.Hud("MyHp.an"), 16); // official MyHp.png (top-bottom gradient)
            // MyHpBack is a dark-gray frame (centre ~24, edges ~65); key out the dark centre (<45) so the
            // red fill shows through, keeping only the lighter rounded frame edges on top.
            _hpBackFrame = NewSR("MyHpBack", SdoExtracted.LoadImageBlackKeyed(SdoExtracted.GameplayUiDir, "MyHpBack.png", 45), 18); SdoLayout.PlaceBox(_hpBackFrame, PX(TrackCenterX - 123f), 15 + _hpYOffset, 246, 18);
            _hpGlowFrames = SdoExtracted.LoadAn(SdoExtracted.GameplayUiDir, "HpEft.an");
            _hpGlow = NewSR("HpEft", _hpGlowFrames.Length > 0 ? _hpGlowFrames[0] : null, 19);

            _scoreDigits = new SpriteRenderer[8];
            for (int i = 0; i < _scoreDigits.Length; i++) { _scoreDigits[i] = NewSR("ScoreD" + i, null, 25); _scoreDigits[i].enabled = false; _digitPopAt[i] = -10f; }

            _judgeWord = NewSR("JudgeWord", null, 41); _judgeWord.color = new Color(1, 1, 1, 0);
            for (int i = 0; i < 7; i++) { var sr = NewSR("ComboD" + i, null, 41); sr.enabled = false; _comboDigits.Add(sr); }
            _comboWord = NewSR("ComboWord", SdoExtracted.Eft("COMBO.PNG", bleed: true), 40); _comboWord.enabled = false;

            // bottom song info — official label graphics + value text (DdrGamePlay.xml positions)
            _lblSong = NewSR("LblSong", SdoExtracted.Hud("GamePlay1.an", bleed: true), 30); SdoLayout.PlaceTopLeft(_lblSong, 11, 575);   // "歌曲名:" (bleed = kill the transparent-white matte halo)
            _lblAttr = NewSR("LblAttr", SdoExtracted.Hud("GamePlay2.an", bleed: true), 30); SdoLayout.PlaceTopLeft(_lblAttr, 204, 575);   // "LV: 时间:"
            _lvOnlyLabel = CropLeftSprite(_lblAttr.sprite, 34);   // GAMEPLAY2 cols 0..28 = "LV:"; the result screen swaps to this so "时间:" disappears with its value
            // values sit at x per DdrGamePlay.xml, but y = the label graphics' vertical centre (575+~20/2 ≈ 585),
            // MiddleLeft-anchored so they're vertically centred with "歌曲名:" / "LV: 时间:".
            // External (user Songs/) songs carry their catalog display name — for an osu "pack" set that's the real
            // per-song name (promoted from the .osu Version); _map.Title would be the shared pack label ("SDO Pack8").
            // Official songs read the import-time UTF-8 catalog (keyed by .gn filename; GB2312 never decoded at runtime),
            // then fall back to _map.Title (set only on the .osu path), then "song".
            var songTitle = chartFormat != 0 && !string.IsNullOrEmpty(songDisplayName) ? songDisplayName : SongCatalog.Title(gnPath);
            if (string.IsNullOrEmpty(songTitle)) songTitle = _map.Title;
            if (string.IsNullOrEmpty(songTitle)) songTitle = "song";
            // 「歌曲名:」後面那格是固定寬（右邊緊接 LV / 时间），長標題會直接壓過去 → 砍到跟選歌清單同一個上限
            songTitle = SongTextLimits.ClampTitle(songTitle);
            // song name / LV / time value text — white, two sizes smaller (13 -> 11) per request.
            // Same font/size as NewText (LegacyRuntime, fontSize 64, characterSize 11×0.2, order 42, MiddleLeft) but
            // laid out per-glyph so the letter-spacing can be tightened (字靠緊一點).
            // designPx 11 = 舊的 characterSize 11×0.2 換算過來的同一個顯示高度；光柵尺寸現在由螢幕決定(見 TrackedTextMesh)。
            _musicName = new TrackedTextMesh("MusicName", Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"),
                11f, Color.white, 42, TextAnchor.MiddleLeft, TextStyles.GameSongTitleTrackEm);
            _musicName.Position = SdoLayout.ToWorld(80, 585, -1);
            _musicName.Text = songTitle;
            _songTitle = songTitle;   // keep for the result panel
            _lvText = NewText("MusicLev", 240, 585, 11, Color.white); _lvText.text = _map.Level.ToString();
            // 「時間」欄拆成三個獨立文字物件（見欄位宣告處說明）：分(右對齊)｜: 秒(左對齊，冒號固定)｜總長(固定)。
            // 冒號與總長位置都不隨數字寬度位移。總長 x 由「: 秒」最寬字串的實測寬度在 Update 釘一次（見 380 附近）。
            int tot0 = (int)Math.Round(_totalMs / 1000.0);   // initial: "--:--  [total]"
            _timeMin = NewText("MusicTimeMin", 336, 585, 11, Color.white); _timeMin.anchor = TextAnchor.MiddleRight; _timeMin.text = "0";
            _timeText = NewText("MusicTime", 336, 585, 11, Color.white); _timeText.text = " : 00";   // 先放最寬秒字串供量測
            _timeTotal = NewText("MusicTimeTotal", 336, 585, 11, Color.white); _timeTotal.text = $"{tot0 / 60} : {tot0 % 60:00}";
            _timeTotal.GetComponent<Renderer>().enabled = false;   // 量到寬度、釘好位置前先不顯示（免得疊在倒數欄上）
            // 已隱藏：右上統計（P/C/B/M + combo + F2 相機標籤）不建立就不顯示也不更新（_info 保持 null，更新處有守門）
            // _info = NewText("Info", 610, 8, 10, Color.white);
            // 已隱藏：左上除錯 FPS 不建立就不顯示（_fpsText 保持 null，更新處有守門）
            // _fpsText = NewText("Fps", 6, 9, 11, new Color(0.5f, 1f, 0.5f, 1f));   // debug FPS (top-left)
            _readyGo = NewSR("ReadyGo", null, 50); _readyGo.enabled = false;
            _gameOverGo = NewSR("GameOverText", null, 55); _gameOverGo.enabled = false;   // above the READY/GO overlay (50)
            // (死亡字幕的幀在死亡當下才依「當前 note skin」載入 → LoadGameOverFrames;每個 skin 各有一組 GAMEOVER 圖)
            BuildRankingUi();
            BuildEnergyHud();
            LayoutSideHud();   // 依面板位置把 大分數/名次/名單/LV·時間 排到 board 兩側（置中時讓開中央）
            BuildChat();       // 右下角(置中向下 → 右上)的聊天框；訊息由前端推進來
            UpdateHpBar();
        }

        // 遊戲中的聊天框(官方 winchat)。編輯器/場景測試模式不需要,也沒有前端在餵訊息 → 不建。
        private void BuildChat()
        {
            if (editorMode || observeBurstMode) return;
            _chat = new GameplayChat();
            _chat.Build(_cam, PanelLeftEffective, _scrollSign < 0);
            _chat.OnSend = txt => onChatSend?.Invoke(txt);
            _chat.OnChannel = ch => onChatChannel?.Invoke(ch);
            _chat.OnExpression = id => onChatExpression?.Invoke(id);
            if (_pendingChatExpressions != null) _chat.SetExpressionArt(_pendingChatExpressions);
            if (_pendingChatSeed != null) _chat.Seed(_pendingChatSeed);
            _pendingChatSeed = null;
            SeedChatDemo();
        }

        // DEV: SDO_CHATDEMO=1 → 一進場就塞幾行假對話,並算成「剛剛有人說話」(所以字是顯示的)。
        // 純粹是為了**實機截圖**檢查聊天框:版位(右下/右上)、行距、顏色、黑邊在真的螢幕上長什麼樣 ——
        // 正常玩的時候要等到有人開口才會有字,截圖很難抓。顏色 hex 與 ChatPalette 同值(那個型別在 Sdo.UI,這裡拿不到)。
        private void SeedChatDemo()
        {
            string demo = DevVar("SDO_CHATDEMO");
            if (string.IsNullOrEmpty(demo)) return;
            _chat.DebugKeepText = true;   // 釘住不淡出,否則開場十秒後就拍不到字了
            if (demo == "2") _chat.DebugOpenExpressionPanel();   // 綠框表情盤
            else if (demo == "3") _chat.DebugOpenModeMenu();     // 家族/好友/當前/回復
            string[] who = { "Eithwa", "小明", "路人甲" };
            string[] say = { "這首好難", "衝啊!", "一起跳", "GO GO GO", "換一首吧" };
            for (int i = 0; i < 6; i++)
                _chat.Push(new GameplayChatLine
                {
                    Name = who[i % who.Length] + ":",
                    Body = say[i % say.Length],
                    ColorHex = "FFFFFF",
                });
            _chat.Push(new GameplayChatLine { Body = "系統:歡迎來到舞台", ColorHex = "F0C24A" });
        }

        /// <summary>前端推一行聊天進來(已決定好名字/顏色/表情圖)。畫面還沒建好就先收進暫存,建好一次灌。</summary>
        public void PushChatLine(GameplayChatLine line)
        {
            if (_chat != null) { _chat.Push(line); return; }
            (_pendingChatSeed ?? (_pendingChatSeed = new List<GameplayChatLine>())).Add(line);
        }

        /// <summary>進遊戲時把房間裡講過的話帶過來(不當作「有人剛說話」,所以字仍是藏著的)。</summary>
        public void SeedChatLines(List<GameplayChatLine> lines)
        {
            if (_chat != null) { _chat.Seed(lines); return; }
            _pendingChatSeed = lines != null ? new List<GameplayChatLine>(lines) : null;
        }

        /// <summary>表情面板的官方素材與內容(前端提供;與房間表情選單同一組圖)。</summary>
        public void SetChatExpressionArt(GameplayChat.ExpressionPanelArt art)
        {
            _pendingChatExpressions = art;
            _chat?.SetExpressionArt(art);
        }

        /// <summary>整個聊天框顯不顯示 —— 結算面板出來時整組收掉(那時已經不在打歌了)。</summary>
        public void SetChatVisible(bool on) => _chat?.SetVisible(on);

        // ShowTime energy meter: official frame + an animated electric-plasma fill strip (ENERGY_Y/B/R), the badge
        // cluster fixed in the right-end panel (mini flash chunk + EnergyEft glow + ×2/×4/×8 badge), a blinking
        // "SPACE" prompt when releasable, and the ENERGYSCORE/ENERGYBONUS number rolls. Built always (cheap) but
        // shown only in showtimeMode (F7 dev toggle flips it via SetEnergyHudVisible). Layout authority:
        // PLAYSHOWTIME/GAMEPLAYSHOWTIME.XML + sdo.bin gauge object (see the field-block comment above).
        private void BuildEnergyHud()
        {
            // official meter frame (MyEnergy0 left trough + MyEnergy1 right end), 1:1 native at the XML coords
            var frameL = SdoExtracted.ShowtimeArt("MyEnergy0.an");
            var frameR = SdoExtracted.ShowtimeArt("MyEnergy1.an");
            _energyFrameL = NewSR("EnergyFrameL", frameL, 24); if (frameL) SdoLayout.PlaceTopLeft(_energyFrameL, energyFramePos.x, energyFramePos.y, -0.05f);
            _energyFrameR = NewSR("EnergyFrameR", frameR, 24); if (frameR) SdoLayout.PlaceTopLeft(_energyFrameR, energyFramePos.x + 256f, energyFramePos.y, -0.05f);
            // THE FILL = the actual official gauge particle effects. The official bar is not 2D art at all: it plays
            // POWER_Y/B/R.EFT (online indices 0x2b/0x28/0x2a) through a dedicated camera clipped to the channel — the
            // electric ribbon, the pulsing head glow and the sparks are all INSIDE those EFT files. So the remake now
            // simply runs them through EftEffect (the validated particle engine): one instance per band, world-rect
            // clip = the channel (Sdo/GlowClipRect template — every particle material clones it), fixed official
            // geometry (rot Y=90°: the 20-unit RAI ribbon trails LEFT of the head; scale 80 = official 100 × 0.8
            // px/wu), and ONLY the head anchor translates with the fill — exactly FUN_0040e210 (translation only,
            // constant scale). Inactive bands park at x=-10000 like the official hidden gauge.
            // The FILL is the official POWER_Y/B/R.EFT electric ribbon rendered by a dedicated camera into an RT and
            // composited onto the channel (BuildGaugeStrips) — there is NO solid 2D fill (official has none). A tiny
            // flat sprite is kept ONLY as a fallback if the EFTs fail to load.
            _energyFill = NewSR("EnergyFill", SolidSprite(), 25); _energyFill.enabled = false;
            if (_addMat != null) { _energyFillMat = new Material(_addMat); TintBoost(_energyFillMat, energyFillBright); _energyFill.sharedMaterial = _energyFillMat; }
            BuildGaugeStrips();
            // mini EnergyProgress chunk (MyEnergy5/6/7, 14×4 @279,15): the official 500ms band-up flash
            _energyFillSpr = new[] { SdoExtracted.ShowtimeArt("MyEnergy5.an"), SdoExtracted.ShowtimeArt("MyEnergy6.an"), SdoExtracted.ShowtimeArt("MyEnergy7.an") };
            _energyMini = NewSR("EnergyMini", null, 25);
            // level badge (MyEnergy2/3/4 = ×2/×4/×8) — shown for the armed/released tier, over the frame
            _energyBadgeSpr = new[] { SdoExtracted.ShowtimeArt("MyEnergy2.an"), SdoExtracted.ShowtimeArt("MyEnergy3.an"), SdoExtracted.ShowtimeArt("MyEnergy4.an") };
            _energyBadge = NewSR("EnergyBadge", null, 26);
            _showtimeHitFrames = LoadShowtimeHitFrames();   // golden EFT_SHOWTIME/EFT_HIT hit burst
            BuildBanner();                                  // SHOW TIME intro overlay
            // official EnergyEft glow (10-frame .an) FIXED behind the badge (@304,12 in the panel). The frames are
            // opaque black-background electric art → ADDITIVE, so only the crackle glows inside the black panel.
            _energyEftFrames = new[] { SdoExtracted.ShowtimeFrames("EnergyEft1.an"), SdoExtracted.ShowtimeFrames("EnergyEft2.an"), SdoExtracted.ShowtimeFrames("EnergyEft3.an") };
            _energyEftSpr = NewSR("EnergyEft", null, 25);   // behind the badge (26)
            if (_addMat != null) { _energyEftMat = new Material(_addMat); TintBoost(_energyEftMat, energyGlowBright); _energyEftSpr.sharedMaterial = _energyEftMat; }
            // official SPACE press-prompt: space.an 2-image pulse (s01 hand → s02 fist+flash), @(284,56)
            _spaceFrames = SdoExtracted.ShowtimeFrames("space.an");
            _spaceSpr = NewSR("SpacePrompt", (_spaceFrames != null && _spaceFrames.Length > 0) ? _spaceFrames[0] : null, 27);
            if (_spaceSpr.sprite) SdoLayout.PlaceTopLeft(_spaceSpr, 284f, 56f, -0.2f);
            // official EnergyBonus number: digit font (ENERGYBONUS 0-9, 20×26) with count-up + per-digit scale-pop (RollingDigits), @(525,23) + static icon GamePlay44 @(544,23)
            // hidezero + fixed 8-slot field ⇒ the number RIGHT-aligns to the field's right edge (x + labelnum*w).
            // EnergyBonus: field 525..525+8*20=685 → right edge 685; EnergyScore: 300..300+8*30=540 → right edge 540.
            var bonusDigits = SdoExtracted.ShowtimeDigits("ENERGYBONUS");
            if (bonusDigits != null) _bonusRoll = new RollingDigits(transform, bonusDigits, 8, 27, 685f, 23f, 20f, rightAlign: true, z: -0.2f);
            _bonusIcon = NewSR("EnergyBonusIcon", SdoExtracted.ShowtimeArt("GamePlay44.an"), 27); if (_bonusIcon.sprite) SdoLayout.PlaceTopLeft(_bonusIcon, 544f, 23f, -0.2f);
            var scoreDigits = SdoExtracted.ShowtimeDigits("ENERGYSCORE");
            if (scoreDigits != null) _scoreRoll = new RollingDigits(transform, scoreDigits, 8, 27, 540f, 10f, 30f, rightAlign: true, z: -0.2f);
            _scoreRoll?.SetTarget(0, Time.time); _bonusRoll?.SetTarget(0, Time.time);   // "0 + 0" primed (shown when the HUD reveals)
            // official: the WHOLE WinMyEnergy cluster stays HIDDEN until the energy-bar intro anim starts (after the
            // "SHOW TIME!" announce + banner) — EnergyIntroAnim reveals it. F7 dev-toggle still flips it directly.
            SetEnergyHudVisible(false);
        }

        // Legacy Particles/Additive tint boost: col = 2·vertex·_TintColor·tex, so _TintColor 0.5 = neutral. k>1 runs
        // the additive HOT — compensates the original D3D9 gamma-space blending (same fix family as BallCoreIntensity).
        private static void TintBoost(Material m, float k)
        {
            if (m != null && m.HasProperty("_TintColor"))
                m.SetColor("_TintColor", new Color(0.5f * k, 0.5f * k, 0.5f * k, Mathf.Clamp01(0.5f * k)));
        }

        // Build the official gauge exactly like the client: a dedicated perspective camera renders the POWER_Y/B/R.EFT
        // effects (on GaugeLayer, in an isolated world region) into a RenderTexture, which UpdateEnergyBar composites
        // additively onto the bar channel. The camera reproduces D3DXMatrixPerspectiveLH(488,15,zn800,zf1200) with
        // eye z=-1000: fovY=2·atan(7.5/800)=1.074°, aspect=488/15, near/far 800/1200. Only the ACTIVE band's head
        // anchor sits at headX (∈[-305,0] world, official +0x8c/+0x90); the rest park off-frustum.
        private void BuildGaugeStrips()
        {
            // RT sized to the 488×15 viewport aspect (2× supersample); alpha kept for the additive-into-black render
            // URP render graph requires a camera's target RT to have a depth buffer (depthStencilFormat != None), so 16-bit depth here.
            _gaugeRT = new RenderTexture(976, 30, 16) { name = "gaugeRT", antiAliasing = 1, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var camGo = new GameObject("GaugeCam") { layer = GaugeLayer };
            camGo.transform.position = GaugeOrigin + new Vector3(0f, 0f, -1000f);   // eye z=-1000, looking +Z at the effects
            camGo.transform.rotation = Quaternion.identity;                          // forward = +Z
            _gaugeCam = camGo.AddComponent<Camera>();
            _gaugeCam.orthographic = false;
            _gaugeCam.fieldOfView = 2f * Mathf.Atan2(7.5f, 800f) * Mathf.Rad2Deg;    // vertical FOV for a 15-unit near-plane height at zn=800
            _gaugeCam.aspect = 488f / 15f;
            _gaugeCam.nearClipPlane = 800f; _gaugeCam.farClipPlane = 1200f;
            _gaugeCam.cullingMask = 1 << GaugeLayer; _gaugeCam.targetTexture = _gaugeRT;
            _gaugeCam.clearFlags = CameraClearFlags.SolidColor; _gaugeCam.backgroundColor = new Color(0, 0, 0, 0);
            _gaugeCam.allowMSAA = false; _gaugeCam.allowHDR = false;
            if (_cam != null) _cam.cullingMask &= ~(1 << GaugeLayer);               // main cam shows the gauge only via the RT
            if (_sceneCam != null) _sceneCam.cullingMask &= ~(1 << GaugeLayer);

            for (int b = 0; b < 3; b++)
            {
                var path = Path.Combine(SdoExtracted.Root, "3DEFT", GaugeStripEft[b] + ".EFT");
                if (!File.Exists(path)) { Debug.LogWarning("[showtime] gauge EFT missing " + path); continue; }
                if (!_namedEftCache.TryGetValue(GaugeStripEft[b], out var file))
                {
                    file = EftFile.Load(File.ReadAllBytes(path));
                    _namedEftCache[GaugeStripEft[b]] = file;
                }
                var anchor = new GameObject("GaugeHead" + b).transform;
                anchor.position = GaugeOrigin + new Vector3(-10000f, 0f, 0f);        // parked off-frustum
                _gaugeAnchor[b] = anchor;
                var go = new GameObject("GaugeStrip_" + GaugeStripEft[b]) { layer = GaugeLayer };
                go.transform.position = anchor.position;
                go.transform.rotation = Quaternion.Euler(0f, 90f, 0f);              // official rot(0,90°,0)
                var eff = go.AddComponent<EftEffect>();
                eff.Persistent = true;                                              // loops (0.32s carrier re-fire)
                eff.EffectName = GaugeStripEft[b];
                eff.BillboardCam = _gaugeCam;                                       // head-glow billboards face the dedicated cam
                eff.SpeedMul = energyStripSpeed;                                    // livelier crackle: faster + denser re-spawn (user: 電流要更多更快)
                eff.Init(file, energyStripScale, anchor, ResolveEftTex, _addMat, GaugeLayer, energyStripBright, 0f, 0.6f, ResolveEftMesh);
                SetLayerRecursive(go, GaugeLayer);
                _gaugeStrip[b] = go;
            }

            // the composite quad on the main overlay (layer 0): additive One-One so the black RT background leaves the
            // frame untouched. OFFICIAL geometry (round-5 RE): the scissor viewport is the FULL {22,14,488,15} strip
            // (design x22..510 — the glow may spill right of the channel over the badge area, that's official), and the
            // projection's _22 is NEGATED (FUN_0040dc00 L21499: gauge+0x134 = proj float[5] = D3D _22) so world +Y
            // renders DOWNWARD (design +y). So: full RT width u[0..1] (= worldX −305..+305 = design 22..510, the head
            // sweep −305..0 lands on the 22..266 channel), and V FLIPPED. The old quad cropped u>0.5 → the head glow's
            // +Z-biased cone scatter (→ world +X ahead of the head) never showed = "頭光不見"; and the unflipped V made
            // particles drift UP instead of the official sink-down ("平的往上" user report).
            var addSh = Shader.Find("Sdo/AdditiveRGB") ?? Shader.Find("Sdo/UnlitAdditiveOverlay");
            var qgo = new GameObject("GaugeComposite");
            var mf = qgo.AddComponent<MeshFilter>();
            float x0 = SdoLayout.WorldX(22f), x1 = SdoLayout.WorldX(22f + 488f);   // official viewport {22,14,488,15}
            float yT = SdoLayout.WorldY(14f), yB = SdoLayout.WorldY(14f + 15f);
            mf.mesh = new Mesh
            {
                vertices = new[] { new Vector3(x0, yB, -0.1f), new Vector3(x1, yB, -0.1f), new Vector3(x1, yT, -0.1f), new Vector3(x0, yT, -0.1f) },
                uv = new[] { new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f) },   // full RT, V flipped (official proj _22 < 0)
                triangles = new[] { 0, 2, 1, 0, 3, 2 }
            };
            _gaugeComposite = qgo.AddComponent<MeshRenderer>();
            _gaugeComposite.sharedMaterial = new Material(addSh) { mainTexture = _gaugeRT };
            _gaugeComposite.sortingOrder = 26;   // the POWER head glow composites OVER the ENERGY body (25)
            _gaugeComposite.enabled = false;     // shown with the HUD (SetEnergyHudVisible)
        }

        private static Sprite[] LoadShowtimeHitFrames()
        {
            var dir = SdoExtracted.EftDir2("SHOWTIME");     // EFFECT/EFT_SHOWTIME
            var fr = new List<Sprite>();
            for (int i = 0; i < 12; i++) { var s = SdoExtracted.LoadImage(dir, "EFT_HIT" + i + ".PNG"); if (s != null) fr.Add(s); }
            return fr.Count > 0 ? fr.ToArray() : null;
        }

        // SHOW TIME intro banner: the 6 ShowTime0..5 tiles assembled into the big logo, parented to a centre root so it
        // scales/spins/fades as one. Hidden until a release fires it (TriggerBanner → UpdateBanner drives the anim).
        private void BuildBanner()
        {
            var tiles = new[] { "ShowTime0.an", "ShowTime1.an", "ShowTime2.an", "ShowTime3.an", "ShowTime4.an", "ShowTime5.an" };
            var pos = new[] { new Vector2(91, 78), new Vector2(347, 78), new Vector2(603, 78), new Vector2(91, 334), new Vector2(347, 334), new Vector2(603, 334) };
            var root = new GameObject("ShowTimeBanner").transform;
            root.position = SdoLayout.ToWorld(400f, 300f, -3f);   // pivot at screen centre
            _bannerSr = new SpriteRenderer[6];
            for (int i = 0; i < 6; i++)
            {
                var sr = NewSR("Banner" + i, SdoExtracted.ShowtimeArt(tiles[i]), 60);
                SdoLayout.PlaceTopLeft(sr, pos[i].x, pos[i].y, -3f);   // absolute, then re-parent keeping world pos
                sr.transform.SetParent(root, true);
                _bannerSr[i] = sr;
            }
            _bannerRoot = root;
            _bannerRoot.gameObject.SetActive(false);
        }

        private void TriggerBanner()
        {
            if (_bannerRoot == null) return;
            _bannerRoot.gameObject.SetActive(true);
            _bannerStart = Time.time;
            _bannerDismiss = -1f;                          // spiral in, then HOLD until DismissBanner()
        }

        // Begin the banner's slide-out (called after the energy-bar intro anim + a 0.5s beat). No-op if already gone.
        private void DismissBanner()
        {
            if (_bannerRoot != null && _bannerStart >= 0f && _bannerDismiss < 0f) _bannerDismiss = Time.time;
        }

        // True once the banner has fully slid off (or was never shown) — OpeningSequence waits on this before ready-go.
        private bool BannerGone => _bannerRoot == null || _bannerStart < 0f;

        // Drive the intro banner: spiral in, HOLD at centre indefinitely (until DismissBanner), then slide out. No-op idle.
        // "SHOW TIME" song-start intro (online WinShowTime). EXACT decompiled composition (Circumgyrate = spin about a
        // LOCAL pivot, TransForm = linear position lerp; standard parent→child matrix multiply):
        //   parent Cirwin1 spins +θ about (400,300) · child TransShowTime1 slides y −600→0 · grandchild Cirwin2 spins −θ.
        // Net on the (upright) tiles = translate by Rz(θ)·(0, slideY): ONE clockwise orbit spiralling in from the top
        // (top→right→bottom→left→centre) over 1000 ms; the ±θ spins cancel so the letters stay upright the whole way.
        // Then hold (until the energy anim finishes + 0.5s → DismissBanner), then TransShowTime2 slides down off the bottom.
        private void UpdateBanner()
        {
            if (_bannerRoot == null || _bannerStart < 0f) return;
            float t = Time.time - _bannerStart;
            float offX, offY;   // design-px offset of the whole (upright) tile group from screen centre (400,300)
            if (t < bannerInSec)                            // spiral IN
            {
                float p = Mathf.Clamp01(t / bannerInSec);
                float ang = 2f * Mathf.PI * p;             // Cirwin sweeps 0→360° over the period
                float slideY = -600f * (1f - p);           // TransShowTime1 slides −600→0
                offX = -slideY * Mathf.Sin(ang);           // = Rz(θ)·(0, slideY)
                offY = slideY * Mathf.Cos(ang);
            }
            else if (_bannerDismiss < 0f) { offX = 0f; offY = 0f; }   // HOLD at centre until dismissed
            else                                            // slide OUT (down) once dismissed
            {
                float k = (Time.time - _bannerDismiss) / bannerOutSec;
                if (k >= 1f) { _bannerStart = -1f; _bannerDismiss = -1f; _bannerRoot.gameObject.SetActive(false); return; }
                offX = 0f; offY = 600f * k;                // TransShowTime2 slide out (down)
            }
            _bannerRoot.position = SdoLayout.ToWorld(400f + offX, 300f + offY, -3f);
            _bannerRoot.localScale = Vector3.one * bannerScale;
            _bannerRoot.localRotation = Quaternion.identity;   // tiles stay UPRIGHT (Cirwin1 +θ and Cirwin2 −θ cancel)
        }

        private void SetEnergyHudVisible(bool on)
        {
            _energyHudOn = on;                               // gates the per-frame re-enables in UpdateEnergyBar
            if (_energyFrameL) _energyFrameL.enabled = on;
            if (_energyFrameR) _energyFrameR.enabled = on;
            if (_energyFill) _energyFill.enabled = on;                 // ENERGY even-ribbon body (solid fill)
            if (_gaugeComposite) _gaugeComposite.enabled = on;         // RT composite = the authentic POWER head glow over it
            if (!on)                                          // park all gauge strips off the RT frustum
                for (int b = 0; b < 3; b++)
                    if (_gaugeAnchor[b] != null) _gaugeAnchor[b].position = GaugeOrigin + new Vector3(-10000f, 0f, 0f);
            if (_energyMini) _energyMini.enabled = false;    // only during the 500ms band-up flash
            if (_energyBadge) _energyBadge.enabled = on && _showtime.ArmedLevel >= 0;
            if (_energyEftSpr) _energyEftSpr.enabled = on && _showtime.ArmedLevel >= 0;
            if (_spaceSpr) _spaceSpr.enabled = on && _showtime.Ready;
            if (_bonusIcon) _bonusIcon.enabled = on && _showtime.Bonus > 0;
            // the ShowTime score/bonus rolls are children of the same official WinMyEnergy window → same visibility
            _scoreRoll?.SetVisible(on);
            _bonusRoll?.SetVisible(on);
        }

        private TextMesh NewText(string name, float x, float y, int px, Color col)
        {
            var go = new GameObject(name); go.transform.position = SdoLayout.ToWorld(x, y, -1);
            var tm = go.AddComponent<TextMesh>();
            tm.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            tm.GetComponent<MeshRenderer>().sortingOrder = 42;
            tm.fontSize = 64; tm.characterSize = px * 0.2f; tm.anchor = TextAnchor.MiddleLeft; tm.color = col;
            // 光柵尺寸交給 HudTextRaster 跟著螢幕走(顯示大小不變) —— 上面那對 64/px×0.2 只是「設計基準」，
            // 直接用它等於把字圖縮 4~5 倍畫，跟同排走實體 px 光柵的歌名並排就一銳一糊、字重也不同。
            _hudTextRaster.Add(tm, px);
            return tm;
        }

        // Crop a label sprite to its left `width` px (same texture, top-left preserved) — used to keep just the "LV:"
        // half of the combined "LV: 时间:" label when the time field is dropped on the result screen.
        private static Sprite CropLeftSprite(Sprite src, int width)
        {
            if (src == null) return null;
            var r = src.rect;                                   // pixel rect within the texture
            float w = Mathf.Min(width, r.width);
            return Sprite.Create(src.texture, new Rect(r.x, r.y, w, r.height),
                                 new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
        }

        // Pose crossfade length for the dancer — the original's 500ms, recovered from the decomp:
        //   · the MotionDriver tick decays the weight linearly, blendW -= dt·clip.blendRate (FUN_0040a890)
        //   · EVERY clip carries the SAME rate: the MOT-clip ctor hard-codes 0.002f (FUN_004093c0 writes 0x3b03126f
        //     to +0xc) and the .MOT parser only ever fills +8/+0x10/+0x14, never that field
        //   · dt is MILLISECONDS — the driver ctor's default cursor speed is 0.03 frames/dt (FUN_00409c60 writes
        //     0x3cf5c28f), i.e. exactly 30fps, matching the per-slice speed (EndF-StartF)/durSec·0.001
        // so the weight falls 1→0 in 1/0.002 = 500ms. We ease with smoothstep where the original ramps linearly: same
        // hand-off window, softer ends. SdoAvatar's own 1.0s default was written for the room's idle↔walk.
        private const float DanceBlendSec = 0.5f;

        /// <summary>
        /// 這一場的**共用**資產:骨架、後備舞蹈 clip、待機 clip、這首歌的編舞(DPS)與它的動作外掛樹。
        /// 本機舞者與場上其他人吃的是同一份 —— LoadAsset 每次都重讀重解,六隻各載一次是白花時間,
        /// 而 SdoAvatar 對 HrcLoader / MotLoader / DpsLoader **只讀**(Setup 把會被改的狀態全配成
        /// per-instance 陣列),所以共用是安全的。See SpawnExtraDancers。
        ///
        /// 🔴 與「建本機那隻 avatar」分開是必要的:**旁觀者沒有自己的舞者,但場上其他人照樣要出**。
        /// 這段以前長在 <see cref="TryLoadAvatar"/> 裡,而旁觀時整個 TryLoadAvatar 被跳過 → _sharedHrc
        /// 是 null → SpawnExtraDancers 第一行就 return → 旁觀者進到舞台看到的是一個空場。
        /// </summary>
        private void LoadSharedDanceAssets()
        {
            // skeleton + dance motion (skinned, CPU). Missing/invalid -> falls back to the static bind pose.
            _sharedHrc = LoadAsset(skeletonHrc, b => HrcLoader.Load(b));
            _sharedDanceMot = LoadAsset(danceMot, b => MotLoader.Load(b));   // fallback dance clip if no DPS
            _sharedRestMot = LoadAsset(restMot, b => MotLoader.Load(b));     // standby idle (rest cat 0x15) — looped before the DPS starts and after it ends
            // 動作外掛（overlay）：一個歌包把它自帶的 .dps 和 .mot 用跟 base 資料根一樣的樹狀結構擺在一起
            // （…/patch Datas/DANCE + …/patch Datas/MOTION|AUMOTION）。這首歌的 .dps 從哪棵樹讀出來，它的 .mot
            // 就在那棵樹 → 設成 overlay，讓 ResolveMot 先查它、找不到才退回 base（含 base 沒有的 W_00xxxx.MOT）。
            // 必須在載 dps／PrewarmDpsMotions 之前設好；純由 dpsPath 推導，不必從歌單一路穿路徑過來。
            string dpsFull = string.IsNullOrEmpty(dpsPath) ? ""
                : Path.Combine(SdoExtracted.Root, dpsPath.Replace('/', Path.DirectorySeparatorChar));
            _motOverrideRoot = MotionOverlay.RootForDps(dpsFull, SdoExtracted.Root);
            _motCache.Clear();   // 快取以動作名為鍵，不含樹；換歌換 overlay 時清掉，免得沿用上一包的解析結果
            if (!string.IsNullOrEmpty(_motOverrideRoot))
                Debug.Log($"[avatar] 動作外掛樹: {_motOverrideRoot}（AUMOTION/MOTION 先於 base 根）");
            // per-song choreography (DPS): sequence motion slices to the music clock (debug now dances too)
            _sharedDps = LoadAsset(dpsPath, b => DpsLoader.Load(b));
            if (_sharedDps != null)
            {
                Debug.Log($"[avatar] DPS {dpsPath}: {_sharedDps.Rows.Length} rows, {_sharedDps.Total:F1}s");
                PrewarmDpsMotions(_sharedDps);   // read every clip NOW (behind the loading cover), not lazily mid-song
            }
        }

        /// <summary>
        /// 這一場的舞蹈時鐘(秒)= 距離**第一顆音符**多久,負值代表編舞還沒開始(READY/GO 與前奏)。
        ///
        /// 為什麼不用音樂時鐘:DPS 的跨度是「第一顆音符 → 最後一顆」,錨在這裡才不會在長前奏的譜上
        /// 提前跳完。_clockStart 還是 -1(音訊還在解碼)時一律回報「還沒開始」—— 拿它去減會得到
        /// 「從開機到現在」的牆鐘時間,每次進場都從編舞的隨機一段開始(「進遊戲先亂跳一段舞才回 idle」)。
        ///
        /// 🔴 這支以前是長在 <see cref="TryLoadAvatar"/> 裡的 lambda,而**旁觀者沒有本機舞者** ——
        /// 場上其他人是靠 <c>av.DanceTimeSec = _avatar.DanceTimeSec</c> 借用它的,借到 null 就整場
        /// 走不進 DPS 那條路(SdoAvatar.LateUpdate 的條件之一),六個人一起站著:使用者回報的
        /// 「旁觀的人沒辦法看到玩家跳舞」。抽成方法之後,沒有本機舞者也拿得到同一顆時鐘。
        /// </summary>
        private float SongDanceTimeSec()
            => _clockStart < 0.0 ? -1f : (float)(Time.timeAsDouble - _clockStart - _danceStartSec);

        private void TryLoadAvatar()
        {
            var parent = new GameObject("Avatar3D");
            HrcLoader hrc = _sharedHrc;          // 共用資產已由 LoadSharedDanceAssets 載好(旁觀也載,見那裡的理由)
            MotLoader mot = _sharedDanceMot;
            SdoAvatar avatar = null;
            if (hrc != null)
            {
                avatar = parent.AddComponent<SdoAvatar>(); avatar.Setup(hrc, mot);
                avatar.BlendSec = DanceBlendSec;                                              // short pose hand-off on every DPS slice (see the const)
                _avatar = avatar;                                                             // F4 panel re-shapes this live
                _bodyShapeB = SdoBodyShape.WeightFromIndex(bodyShapeIndex, maleBody);
                avatar.SetBodyShape(_bodyShapeB);                                             // 體型: thin/standard/fat (default thin)
                avatar.RestMot = _sharedRestMot;
                var dps = _sharedDps;
                if (dps != null)
                {
                    avatar.Dps = dps;
                    avatar.MotResolver = ResolveMot;
                    // Dance time = time since the FIRST NOTE (beat-0 note clock minus _danceStartSec), NOT the music
                    // clock: the DPS spans first→last note, so anchoring here keeps it from leading the song on
                    // long-intro charts. Stays negative through the READY/GO lead-in AND the intro (count-in + any
                    // musical intro before the first note) -> avatar holds the rest idle, then starts the DPS on the
                    // first downbeat.
                    // _clockStart is still the "not anchored yet" sentinel (-1) from here until LoadAndPlayAudio
                    // finishes decoding the song — a second or more on an external mp3. Subtracting it would make the
                    // dance time the WALL CLOCK since app start (a different, arbitrary point of the choreography every
                    // run: "進遊戲先亂跳一段舞才回 idle"), so report "before the dance" until the clock is real.
                    avatar.DanceTimeSec = SongDanceTimeSec;
                    // 8-beat dance-gate decision / HP-out -> dancer holds the standby idle。HP 看的是 _hpDead 而不是
                    // _failed：完奏模式歌不切斷(_failed 不設)，但「血用完了就不能繼續跳舞」——死了就回待機站著到曲末。
                    // 例外：danceIgnoreMiss 開著時血量完全不管，死了照跳（見 DanceGate.Enabled）。
                    avatar.DanceEnabled = () => DanceGate.Enabled(_dancing, _failed, _hpDead, danceIgnoreMiss);
                    Debug.Log($"[avatar] DPS {dpsPath}: {dps.Rows.Length} rows, {dps.Total:F1}s");
                    PrewarmDpsMotions(dps);   // read every clip NOW (behind the loading cover), not lazily mid-song
                }
            }

            // Load the WOMAN body parts via the shared builder (same loop the lobby avatar + head portrait use).
            var built = SdoAvatarBuilder.LoadParts(parent, avatar, avatarParts, SdoAvatarBuilder.SkinStyle.Gameplay);
            Bounds bounds = built.Bounds; bool any = built.Any; int parts = built.Parts;
            if (!any) { Debug.LogWarning("[avatar] no parts loaded"); return; }
            Debug.Log($"[avatar] {(localPlayerMale ? "MAN" : "WOMAN")}: {parts} parts, skeleton={(hrc != null ? hrc.Names.Length + " bones" : "none")}, mot={(mot != null ? mot.MaxTime + 1 + " frames" : "none")}");
            if (avatar != null) MmdAvatarSwap.Register(avatar);   // config.ini [Mmd] 開著 → 舞者換成 MMD 模型 (SDO stays the motion driver)
            var handYellow = new Color(1f, 0.86f, 0.25f);
            if (use3dCamera && _camReady)
            {
                // Decompiled placement: the dancer stands FEET-DOWN on the floor dance-spot (table @0x582690; solo =
                // origin). Feet Y in model space = FeetYAt(0) (lowest skinned vertex at the bind pose); lift so the feet
                // land on _danceSpot.y, and put the model's XZ root at the spot. The cameras then frame it VERBATIM.
                parent.transform.localScale = Vector3.one;
                float feetY = avatar != null ? avatar.FeetYAt(0f) : 0f;   // pose @0 + lowest-vert Y
                parent.transform.position = new Vector3(_danceSpot.x, _danceSpot.y - feetY, _danceSpot.z);
                Vector3 chestLocal = avatar != null ? avatar.BoneModelPos("Bip01_Spine1") : new Vector3(0f, 38f, 0f);
                _avatarChest = parent.transform.position + chestLocal;   // star-ring / bounds / debug framing only
                _avatarRoot = parent.transform;
                // 飛行翅膀:整場常駐懸浮(見 UpdateFlyHover)。這裡只記下貼地基準與「有沒有在飛」,不再去量 flystay 比
                // dance 高多少 —— 那個 Δ 建立在「flystay 自己會浮」的錯誤前提上,實測它相對站姿只有 +3.4(女)/+1.2(男),
                // 常常算出 0 讓整個懸浮靜默失效(見 FlyHoverRealDataTests)。
                _flyBaseRootY = _danceSpot.y - feetY;
                _flying = SpecialMotionItems.WearsFlyingWing(avatarParts);
                _flyLiftCur = SpecialMotionItems.HoverY(_flying);   // 一進場就浮著,不要從地面升起來
                _flyHoverArmed = true;                              // 只有這條 3D 舞台路徑量過基準 → 只有它能動 root.y
                if (avatar != null) avatar.PoseInitialIdle();   // arm the idle so the first frame doesn't crossfade from the measurement T-pose
                if (!avatarDebug && avatar != null)
                    try   // never let a hand-glow hiccup abort scene/audio setup (which run AFTER TryLoadAvatar)
                    {
                        CreateHandTrail(parent.transform, avatar, "Bip01_L_Hand", "Bip01_L_Finger0", handYellow);
                        CreateHandTrail(parent.transform, avatar, "Bip01_R_Hand", "Bip01_R_Finger0", handYellow);
                    }
                    catch (System.Exception e) { Debug.LogError("[handtrail] creation failed (non-fatal): " + e); }
                // 地面星環:跟著自己的骨盆走,顏色 = 自己那一隊(沒組隊就是官方原本的白)。
                CreateGroundStarRing(_avatarChest.x, _avatarChest.z, 0.6f, avatar, parent.transform, TeamOf(LocalDancerSlotIndex));
                if (avatar != null)
                    try { CreateHeadEmoji(avatar); }   // head-emoji billboard at the dancer's head front-right
                    catch (System.Exception e) { Debug.LogError("[emoji] creation failed (non-fatal): " + e); }
                if (avatar != null)
                    try { CreateHeadMarker(avatar); }  // local player's nameplate (arrow + name) above the head
                    catch (System.Exception e) { Debug.LogError("[headmarker] creation failed (non-fatal): " + e); }
                SetLayerRecursive(parent, SceneLayer);
            }
            else
            {
                float k = 360f / Mathf.Max(bounds.size.y, 1e-3f);     // fit ~360 design px tall
                parent.transform.localScale = new Vector3(k, k, k);
                parent.transform.position = new Vector3(175f, -bounds.center.y * k, 5f);  // right side, vertically centred
                if (avatar != null)
                    try   // never let a hand-glow hiccup abort scene/audio setup (which run AFTER TryLoadAvatar)
                    {
                        CreateHandTrail(parent.transform, avatar, "Bip01_L_Hand", "Bip01_L_Finger0", handYellow);
                        CreateHandTrail(parent.transform, avatar, "Bip01_R_Hand", "Bip01_R_Finger0", handYellow);
                    }
                    catch (System.Exception e) { Debug.LogError("[handtrail] creation failed (non-fatal): " + e); }
                CreateGroundStarRing(parent.transform.position.x, parent.transform.position.y + bounds.min.y * k, 0f, null, null);
            }
        }

        public bool use3dCamera = true;               // render avatar+stage in the .cv perspective camera (faithful)
        // DEBUG: isolate the avatar — hide the stage scene, lock a fixed front camera. Off = full stage + cameras.
        public bool avatarDebug = false;
        // BURST OBSERVE MODE: a clean stage for studying the combo-burst EFTs — dancer stands idle (no DPS dance),
        // no note board / notes / receptors / HP bar, no music, fixed camera (cam 0). Fire bursts with keys 1-5
        // (=100..500COMBO), 0 = FINISHED, or the F4 panel; SLOW-MOTION with [ (slower) / ] (faster), \ = pause toggle,
        // = (equals) = reset to 1×. Set false in the Inspector for normal gameplay.
        public bool observeBurstMode = false;
        // ---- 整體遊戲流速 (F9 測試面板) = StepMania 的 music rate ----
        // 音樂用 AudioSource.pitch 變速變調(= RageSound SetPlaybackRate),其餘全部掛在 Time.timeScale 上一起變慢:
        // 譜面時鐘是 Time.timeAsDouble(scaled)驅動的,所以音符/判定/舞者/特效/HUD 自動跟著走。dspTime 是真實時間,
        // 不吃 timeScale,所以 dsp↔譜面時間的換算要自己帶 rate(GameRate),改速度時還得重新錨定否則會跳拍。
        private float _timeScale = 1f;   // = 目前流速(未暫停時)。F4/F9 面板與 [ ] \ = 都走 SetGameRate。
        private double _musicRate = 1.0; // 同上,雙精度版(dsp 換算用)
        private bool _paused;            // \ 暫停:timeScale=0 且音樂 Pause(否則音樂會自己跑掉)
        private double _pauseChartSec;   // 暫停當下的譜面時間(秒)→ 恢復時用它重新錨定音訊
        private const int SceneLayer = 4;             // the perspective stage layer
        // The default camera is the AUTO-DIRECTOR (decompiled CameraSeq, a CAMERA/*.CDT shot list): a sequence of
        // shots, each a moving .cv dolly shown for its own durationMs, auto-cutting to the next and looping. F2
        // (gameplay cmd 0x3c) cycles AUTO(-1) -> these 6 FIXED cameras (0..5) -> back to AUTO.
        // Which .CDT loads is chosen by (mapId x player count): CameraMgr_LoadCamerasBin_0040e0e0 indexes the
        // s_6_cdt table from a switch(mapId-3) gated by playerCount (021_gameplay_0046b8a0 ~0x4768a9). The exact
        // jump tables are reproduced in SelectCdtPath/SoloCdt/GroupCdt. SCN0009 (mapId 9) = the PALACE -> palace_1.cdt.
        public int playerCount = 1;            // dancers in the scene -> solo(<=3) / group(4..6) / large(>6) CDT bucket
        private const string CdtFallback = "CAMERA/1.CDT";
        // Decompiled world model (VERBATIM — no re-centring). The stage is at native coords; the dancer stands on a
        // floor "dance-spot" (table @0x582690 indexed by (slot+mode*6)*0x48; SOLO = entry1 = (0,0,0)); and the camera
        // anchor (CameraMgr+0x340, set every frame to the active dancer's spot — 021_gameplay 7375) is ADDED to the
        // .cv eye/target ONLY for shots whose CDT flag = 1. For solo the spot is the origin, so the anchor is zero and
        // every camera is just its raw .cv/table value. (The old _avatarChest re-centring was the source of the
        // wrong angles + the fly-in; it's gone.)
        // 飛行翅膀:穿著就整場浮 10,和姿勢無關 —— 待機、跳舞、勝負定格一律同高,與房間同一顆 SpecialMotionItems.HoverY。
        //
        // 這是 remake 刻意偏離官方的一條(使用者要求「跳舞畫面也要浮起來」)。官方舞台其實不浮:那個 y+=10 只寫在
        // Player_UpdateTransform_004ab4a0(028:2614),而它全 exe 只有一個呼叫端(023:1044),掛在 StateRoom 的 vtable
        // @0x5491b4 上 —— 房間是 StateMgr_SwitchState case 5,跳舞是 case 8 的另一個 class(Gameplay_ctor_004742d0,
        // 不繼承 StateRoom)。舞台的 Y 由 Dancer_UpdateTransform_004a8080(027:2593)寫成「隊形表 Y + DPS step Y」,
        // 兩者實測全為 0(隊形表 @0x582690 中間分量皆 0;2176 個官方 .DPS 共 85,525 row 的 Y 分量亦全 0)。
        //
        // 舊版在這裡量「flystay 比 dance ready pose 高多少」當抬升量,前提是「flystay 自己會浮」——實測錯的
        // (flystay 相對站姿只有 +3.4 女 / +1.2 男),Δ 常常算成 0 讓整段靜默失效。也不再看 IsRestPose:那個 gate 會讓
        // 勝負定格(PlayOneShot)被當成 rest 而沉下去、回放又浮起來,一沉一浮。
        //
        // 地面星環釘在 FloorY(FloorRing 只吃 X/Z),故舞者浮起、星環仍貼地;相機是 verbatim CDT 不補償 → 舞者在畫面
        // 內往上浮。見 [[sdo-special-item-idle-walk]]。
        private void UpdateFlyHover()
        {
            if (!_flyHoverArmed || _avatarRoot == null) return;   // 2D/編輯器路徑沒量過基準 → 一律不碰 root.y
            float target = SpecialMotionItems.HoverY(_flying);
            if (_flyLiftCur != target)
                _flyLiftCur = Mathf.Abs(target - _flyLiftCur) < 0.01f
                            ? target
                            : Mathf.Lerp(_flyLiftCur, target, 1f - Mathf.Exp(-Time.deltaTime / 0.25f));   // 平滑(τ≈0.25s)
            var p = _avatarRoot.position;
            _avatarRoot.position = new Vector3(p.x, _flyBaseRootY + _flyLiftCur, p.z);
        }

        private Vector3 _danceSpot = Vector3.zero;     // solo floor spot (0,0,0); dancer's feet stand here
        private Vector3 _avatarChest;                  // dancer chest world point (star-ring / bounds / debug framing only)
        private bool _camReady;                        // director shots loaded
        private float _camSwitchTime;                  // F2 label/timing only
        private Camera _sceneCam;
        private RenderTexture _sceneRT;                // the stage backdrop RT (window-shaped; see MaintainSceneRt)
        private RtResizeTracker _sceneRtTrack;         // debounced window-resize → RT re-allocation
        public float sceneSupersample = RtSizing.DefaultSupersample;   // set to 1 to render at window-native resolution
        private Material _backdropMat; private bool _backdropFlip;   // F9 toggles the stage V-flip (safety net)
        private Transform _avatarRoot;   // the Avatar3D root (for the debug front-camera framing)
        private FormationPreview _formation;   // 隊形假人預覽(F10,延遲建立)
        // 飛行翅膀懸浮(見 UpdateFlyHover):穿著就整場浮 HoverY,與姿勢/是否在跳舞無關。
        private bool _flying;          // 這位舞者穿了會飛的翅膀
        private bool _flyHoverArmed;   // 只有 3D 舞台路徑量過 _flyBaseRootY;沒 arm 就完全不碰 root.y(2D/編輯器)
        private float _flyBaseRootY;   // 貼地時的 root.y(= danceSpot.y − danceFeetY)
        private float _flyLiftCur;     // 目前已套用的懸浮,平滑收斂到 HoverY(_flying)
        private int _camMode = -1;                     // -1 = auto-director (default); 0..5 = fixed F2 camera
        private CvLoader[] _dirCv; private int[] _dirDurMs; private bool[] _dirAbs;   // director shots + per-shot absolute(:0)/relative(:1)
        private int _dirShot; private float _dirShotStart;

        // 6 fixed F2 cameras — EXACT decompiled values (eye @DAT_005824f0 / target @DAT_00582538), absolute world coords.
        private static readonly Vector3[] FixedEye = {
            new Vector3(-3, 46, -181), new Vector3(-96, 85, -126), new Vector3(147, 97, -85),
            new Vector3(-3, 163, -154), new Vector3(-1, 476, -60), new Vector3(-4, 38, -346),
        };
        private static readonly Vector3[] FixedTgt = {
            new Vector3(-2, 38, 21), new Vector3(-11, 38, 66), new Vector3(-29, 38, 110),
            new Vector3(-2, 38, 21), new Vector3(-2, 38, 21), new Vector3(-2, 38, 21),
        };
        public void SetCamModeForTest(int m) { _camMode = m; _camSwitchTime = Time.time; }   // headless capture hook
        public void SpawnComboBurstForTest(int tier) => SpawnComboBurst(tier);               // headless combo-burst capture hook
        public Transform AvatarRootForTest => _avatarRoot;                                    // for framing the capture camera on the dancer
        public float FlyLiftForTest => _flyLiftCur;                                           // 目前套用的飛行懸浮高度
        // Hide the bright stage geometry (palace walls/floor + mapobj props + ground star-ring) so a headless capture
        // shows the ADDITIVE combo burst on the SceneCam's black background — the only way to verify the effect's true
        // colour/brightness/height (on the lit palace the additive glow washes out, exactly like the official's dark
        // night scene makes it pop). Keeps the avatar (for height reference) and the eft effects.
        public void HideStageForTest()
        {
            var s = GameObject.Find("StageScene"); if (s != null) s.SetActive(false);
            // FindObjectsByType(FindObjectSortMode.None) doesn't resolve in this project's engine reference set
            // (the monolithic UnityEngine.dll shadows CoreModule), so keep the legacy call and suppress the warning.
#pragma warning disable 0618
            foreach (var mr in FindObjectsOfType<Renderer>())
#pragma warning restore 0618
            {
                string n = mr.gameObject.name;
                if (n.EndsWith("_mesh") || n == "GroundStarRing" || n.StartsWith("Star")) mr.enabled = false;
            }
        }

        // F2 (decompiled gameplay cmd 0x3c): AUTO(-1) -> fixed 0..n-1 -> AUTO. Returning to AUTO RESUMES the
        // current director shot (only restarts that shot's timer) — matching CameraSeq_SetPlaying(0)->AdvanceA,
        // which never rewinds the sequence index to 0. It MUST NOT reset _dirShot (that re-played the intro crane).
        private void CycleCamMode()
        {
            int n = FixedEye.Length;
            _camMode++;
            if (_camMode > n - 1) _camMode = -1;
            _camSwitchTime = Time.time;
            if (_camMode < 0)
            {
                // Decompiled CameraSeq_SetPlaying(0): `if(index!=0) index--; AdvanceA()` (AdvanceA does index++).
                // Net: shot 0 -> advances to shot 1; shot N>0 -> replays N. So returning to AUTO STRUCTURALLY
                // never replays shot 0 (the intro crane that flies in from outside the venue).
                if (_dirShot == 0 && _dirCv != null && _dirCv.Length > 1) _dirShot = 1;
                _dirShotStart = Time.time;
            }
            onCamModeChanged?.Invoke(_camMode);   // 記住玩家的選擇（OPTION「遊戲視角」＋下一局的開場鏡頭）
        }

        // F10:開/關「隊形」假人預覽 —— 在舞台地板上立最多 6 個替身，位置逐字取自反編譯的 slot 表
        // (FormationCatalog，table @0x582690)。開著時 ←→ 切隊形 TYPE(1..3)、↑↓ 改人數 COUNT(1..6)。
        // slot 0(金色)＝領隊/第一名/相機錨點；預覽期間把單人舞者藏起來，讓替身站它的位置。
        // 純研究/視覺化工具（沒有計分、沒有音符、沒有連線）。
        //
        // 原 formation 分支綁 F2，但本分支 F2/F3 都已被佔用（F2 = RoomScreen 開始遊戲、GenderSelectScreen
        // 譜面編輯器、以及 KeyMap 的 Hotkey.Camera 預設值；F3 = RoomScreen 家族除錯），所以改綁 F10。
        private void ToggleFormationPreview()
        {
            if (_formation == null)
            {
                var go = new GameObject("FormationPreview");
                go.transform.SetParent(transform, false);
                _formation = go.AddComponent<FormationPreview>();
                _formation.Layer = SceneLayer;
            }
            _formation.Cam = _sceneCam;      // (重新)綁定 —— 舞台相機可能在第一次 toggle 之後才建好
            _formation.Anchor = _danceSpot;
            _formation.Toggle();
            if (_avatarRoot != null) _avatarRoot.gameObject.SetActive(!_formation.Active);   // 預覽時藏起單人舞者
        }

        /// <summary>F2 可循環的固定鏡頭台數（前端把玩家選到的那台存進 OPTION 設定時要夾範圍）。</summary>
        public static int FixedCamCount => FixedEye.Length;
        // Result hand-off (read by the front-end once the song/run has ended). _score is plain managed state, so it
        // stays readable after this GameObject is destroyed as long as the caller grabs the reference first.
        public bool Finished => _ended;          // song played out (or failed) — time to settle
        public bool Failed => _hpDead;           // HP ran out (完奏模式也算 —— 歌只是沒被切斷)
        public ScoreProcessor Score => _score;   // final judgement tallies + score (null only if Start() bailed early)
        // Set when the player confirms (OK / Enter / Esc) on the STATIS result panel. The front-end (FrontendApp)
        // polls this to know the run is fully done — Finished alone fires at song-end, BEFORE the win/lose pose +
        // result panel play out, so tearing down on Finished would cut the whole settle sequence short.
        public bool ResultConfirmed { get; private set; }

        // Test hooks for the re-entry assertion (CameraReentryTest): drive the real cycle + observe state.
        public int CamModeForTest => _camMode;
        public int DirShotForTest { get => _dirShot; set => _dirShot = value; }
        public int FixedCamCountForTest => _camReady ? FixedEye.Length : 0;
        public void CycleCamModeForTest() => CycleCamMode();
        public Camera SceneCamForTest => _sceneCam;
        public Vector3 DanceSpotForTest => _danceSpot;
        public void RestartDirectorForTest() { _camMode = -1; _dirShot = 0; _dirShotStart = Time.time; }   // shot 0 @ t=0 (crane start)

        // Load the auto-director shot list (.cdt) chosen by (map, player count). Each shot is a .cv dolly played
        // verbatim; its CDT flag says whether it's absolute world (:0) or dance-spot-relative (:1). The 6 fixed F2
        // cams are the hardcoded decompiled table (FixedEye/FixedTgt), not files.
        private void LoadCvCameras()
        {
            _danceSpot = SoloDanceSpot();
            // 單人時相機錨點就是本機的位置 —— 多人時 TickDancerSlots 會把它改成 slot 0 的占用者
            // (官方的鏡頭跟第一名)。先在這裡對齊,離線/單人的行為就與加多人之前完全一樣。
            _camAnchorSpot = _danceSpot;
            var cdt = LoadAsset(SelectCdtPath(), b => CdtLoader.Load(b));
            if (cdt != null)
            {
                var dcv = new System.Collections.Generic.List<CvLoader>();
                var dur = new System.Collections.Generic.List<int>();
                var abs = new System.Collections.Generic.List<bool>();
                foreach (var s in cdt.Shots)
                {
                    var cv = LoadAsset(("CAMERA/" + s.CvRelPath.Replace('\\', '/')).ToUpperInvariant(), b => CvLoader.Load(b));
                    if (cv == null) continue;
                    dcv.Add(cv); dur.Add(s.DurationMs); abs.Add(s.Flag == 0);   // CDT flag 0 = absolute world, 1 = +danceSpot
                }
                _dirCv = dcv.ToArray(); _dirDurMs = dur.ToArray(); _dirAbs = abs.ToArray();
            }
            _camReady = _dirCv != null && _dirCv.Length > 0;
            _avatarChest = _danceSpot + new Vector3(0f, 38f, 0f);   // provisional; refined once the avatar poses
            _dirShotStart = Time.time;
        }

        // Solo dance-spot = decompiled floor table @0x582690 entry1 = (0,0,0). (Multiplayer would index by slot/mode.)
        private Vector3 SoloDanceSpot() => Vector3.zero;

        // EXACT decompiled mapId(3..34) -> CDT. mapId 3..18 = the 16 classic maps, from the OFFLINE 021_gameplay
        // jump tables (solo/small @0x4780b8 = _1 variants; group 4..6 @0x4780f8 = base). SCN0009 = mapId 9 = PALACE.
        // mapId 19..34 = the NEWER maps: the offline standalone caps its switch at mapId 18 (>18 falls back to
        // 1.cdt/3.cdt), so those come from the ONLINE client (sdo.bin) gameplay switch @0x73d425 (solo, byte-remap
        // @0x73dccc -> JT @0x73dc48) / group @0x73d43a. scn####=mapId#### verified (palace9/railway17/subway20/
        // basketball26). ⚠️ SCN0022 = mapId 22 = the 墓地/tomb -> 3ren.cdt (solo) / 6ren.cdt (group) = the "mu di"
        // director whose .cv up-vectors roll ~60° (the tilted-map shots). null entry = fall through to the numeric
        // fallback. Missing files are handled by SelectCdtPath's File.Exists chain, so unshipped maps stay safe.
        private static readonly string[] SoloCdt  = { "Garage_1","sea_1","Christmas_","playground_","sky_1","egypt_1","palace_1","huache_1",null,"fifa_1","fifa_1","ocean_1","Ghosthill_1","street_1","railway_1","houseboat_1",
                                                      "luoma_3","underground_3","zhanwei_3","3ren","jiaoshi_3","xuejing_3ren","spring_3g","basketball_3","narnia_3","niaochao_3","airpot_3","jiedao3","mj3ren","xk3","xk3","7.9_6ren" };
        private static readonly string[] GroupCdt = { "Garage","sea","Christmas","playground","sky","egypt","palace","huache",null,"fifa","fifa","ocean","Ghosthill","street","railway","houseboat",
                                                      "luoma_6","underground_shan","zhanwei_6","6ren","jiaoshi_6","xuejing_6ren","spring_6g","basketball_6yi","narnia_6yi","niaochao_6yi","airpot_6yi","jiedao6","mj6ren","xk6","xk6","7.9_6ren" };

        // scenePath "SCENE/SCN0009" -> 9 (matches the decompiled mapId = DAT_00674f04+0x5c).
        private int SceneMapId()
        {
            var m = System.Text.RegularExpressions.Regex.Match(scenePath ?? "", @"SCN(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        // mapId 3-34 → CDT stem; solo(n<=3) vs group(n=4-6); fallback chain: map→numeric→1
        private string SelectCdtPath()
        {
            int map = SceneMapId();
            int n   = Mathf.Max(1, playerCount);
            string[] table  = n <= 3 ? SoloCdt : GroupCdt;
            string fallback = n == 1 ? "1" : n <= 3 ? "3" : "6";
            string mapped   = map >= 3 && map <= 2 + SoloCdt.Length ? table[map - 3] : null;
            foreach (var c in new[] { mapped, fallback, "1" })
            {
                if (c == null) continue;
                string rel = "CAMERA/" + c + ".CDT";
                if (File.Exists(Path.Combine(SdoExtracted.Root, rel.Replace('/', Path.DirectorySeparatorChar))))
                    return rel;
            }
            return CdtFallback;
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        { go.layer = layer; foreach (Transform c in go.transform) SetLayerRecursive(c.gameObject, layer); }

        public string scenePath = "SCENE/SCN0009";   // stage scene (SCENE.MSH + .dds)
        // The stage is a 3D room (perspective); the HUD/track is 2D-ortho. Render the scene with a dedicated
        // perspective camera on a separate layer as the background, with the ortho camera overlaying on top.
        private Bounds AvatarWorldBounds()
        {
            var fallback = new Bounds(new Vector3(_avatarChest.x, 31f, _avatarChest.z), new Vector3(40f, 64f, 40f));
            if (_avatarRoot == null) return fallback;
            var rends = _avatarRoot.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) return fallback;
            Bounds b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            return b;
        }

        // An SDO "mapobj": a stage prop (HRC skeleton + MSH skin + MOT motion, exactly like an avatar) placed at
        // fixed transforms. WHICH props a scene mounts (and where) is the decompiled Scene_LoadBackground table,
        // keyed by scene folder — see SceneMapobjCatalog (generated from SDO_SCENE_MAPOBJ_TABLE.json). Switching the
        // selected stage now switches its props too: e.g. SCN0009 -> GUATAN x4, SCN0004 -> sea/beach/boat group.
        private struct MapobjInstance { public Vector3 Pos; public float Scale; public Vector3 EulerDeg; }

        // EXPERIMENT: GPU-skin the animated mapobj props (SkinnedMeshRenderer) instead of CPU-skinning one shared
        // mesh per group. Each animated copy then GPU-skins itself (no per-vertex CPU work, no mesh upload) at the
        // cost of losing the shared-mesh draw batching. Static props are unaffected (they stay frozen + instanced).
        // Set false to fall back to the committed CPU+instancing path. The dancer is NOT affected (CPU path).
        // DISABLED: the GPU-skin path inflates animated props (SCN0009 GUATAN, SCN0012/13 FIFA_QIUBEI render
        // oversized). Until the bindpose/bone-scale reconstruction is matched to the CPU path, animated mapobjs use
        // the proven CPU driver+clones path. Flip back to true only to debug the GPU-skin scale regression.
        public bool mapobjGpuSkin = false;

        // "SCENE/SCN0009" -> "SCN0009" (the catalog key); tolerates trailing or back slashes.
        private string SceneFolder()
        {
            var p = (scenePath ?? "").Replace('\\', '/').TrimEnd('/');
            int slash = p.LastIndexOf('/');
            return slash >= 0 ? p.Substring(slash + 1) : p;
        }

        private void TryLoadMapobjs()
        {
            foreach (var g in SceneMapobjCatalog.ForFolder(SceneFolder()))
            {
                // SCN0022 坟墓: three props are drawn as camera-facing billboards instead of the fixed-orientation mesh
                // (see SpawnSceneFlames / SpawnSceneGhosts) — skip the meshes here so they aren't double-drawn:
                //   FENMU/LANHUO (SHAN.MSH, 鬼火 flame) — loaded but never linked as a child in the official (030 idx 2).
                //   FENMU/GUI, FENMU/GUI2 (LABA11/12, 飛鬼) — the official Billboard_AddEntry's them; the flat .mot-baked
                //     quad foreshortens/goes edge-on from the stage angle (hard, "solid-division"). Ghost billboards fly
                //     via the .mot and always face the camera. (sheguang stays a mesh — it's a directional sweeping beam.)
                if (g.Folder.Equals("FENMU/LANHUO", System.StringComparison.OrdinalIgnoreCase) ||
                    g.Folder.Equals("FENMU/GUI", System.StringComparison.OrdinalIgnoreCase) ||
                    g.Folder.Equals("FENMU/GUI2", System.StringComparison.OrdinalIgnoreCase)) continue;
                var insts = new MapobjInstance[g.Instances.Length];
                for (int i = 0; i < insts.Length; i++)
                {
                    var p = g.Instances[i];
                    insts[i] = new MapobjInstance { Pos = new Vector3(p.X, p.Y, p.Z), Scale = p.Scale };
                }
                AddMapobj("SCENE/MAPOBJ/" + g.Folder, g.Msh, g.Hrc, g.Mot, insts);
            }
        }

        // Scene NPCs ("場景的人"): full skinned avatars placed around the stage (e.g. SCN0017 subway passengers).
        // The model+skeleton live in AVATAR/, the motion in MOTION/, so AddMapobj is reused with motRelDir="MOTION".
        // One AddMapobj call per NPC (each has its own model + facing); static NPCs freeze at the bind pose, the DJ
        // animates its .mot. See SceneAvatarCatalog (decompiled from StageScene_LoadAvatarsAndMotions).
        private void TryLoadSceneAvatars()
        {
            int i = 0;
            foreach (var a in SceneAvatarCatalog.ForFolder(SceneFolder()))
            {
                var inst = new[] { new MapobjInstance { Pos = a.Pos, Scale = 1f, EulerDeg = a.EulerDeg } };
                // stagger each NPC's loop phase so a crowd sharing one idle clip doesn't move in lockstep (the
                // original advances them out of sync). A prime-ish step spreads the ~10 NPCs across the clip.
                // opaque:true — these are CHARACTERS: their skin/face DDS alpha (e.g. the DJ's nanrendj.dds DXT3) is
                // NOT a 去背 cut-out; the generic alpha path would punch holes in the face. Render them solid.
                AddMapobj("AVATAR", a.Msh, a.Hrc, a.Mot, inst, motRelDir: "MOTION", phaseOffsetSec: i * 0.83f, opaque: true);
                i++;
            }
        }

        // Build one mapobj group ONCE, then place it at every instance transform. The MSH is parsed a single time
        // and the skinned meshes are SHARED across instances: a STATIC prop (no .mot) is skinned to its bind pose
        // once and then frozen (its SdoAvatar disables itself — zero per-frame work); an ANIMATED prop is driven by
        // ONE SdoAvatar (instance 0) whose looping .mot updates the shared meshes, and the other instances simply
        // render those same meshes at their own transform. So N copies cost 1 parse + (1 or 0) skin/frame + N draws,
        // not N×everything — this is what keeps the dense scenes cheap (box ×256, deng ×72, the room/saloon prop
        // walls). Lockstep copies look identical to the original (every instance plays the same clip in phase).
        // Materials/textures are read-only, so one set per submesh is shared too. Stage layer, native SDO coords.
        private void AddMapobj(string relDir, string mshFile, string hrcFile, string motFile, MapobjInstance[] instances, string motRelDir = null, float phaseOffsetSec = 0f, bool opaque = false)
        {
            if (instances == null || instances.Length == 0) return;
            var dir = Path.Combine(SdoExtracted.Root, relDir.Replace('/', Path.DirectorySeparatorChar));
            var mshPath = Path.Combine(dir, mshFile);
            if (!File.Exists(mshPath)) { Debug.LogWarning("[mapobj] missing " + mshPath); return; }
            string baseName = Path.GetFileNameWithoutExtension(mshFile);   // GameObject-name / log label
            var r = MshLoader.Load(File.ReadAllBytes(mshPath));            // parse ONCE; every instance shares these meshes
            if (r == null || r.Submeshes.Count == 0) { Debug.LogWarning("[mapobj] parse fail " + baseName); return; }
            HrcLoader hrc = LoadAsset(relDir + "/" + hrcFile, b => HrcLoader.Load(b));
            // SCN0003 disco floor: 256 tiles, each its OWN material, animated as a moving formation (NOT the shared-
            // material path — they must NOT pulse in lockstep). See BoxFloorPattern / BoxFloorAnimator.
            if (instances.Length == BoxFloorPattern.Tiles && baseName.ToUpperInvariant() == "BOX" && SceneFolder().ToUpperInvariant() == "SCN0003")
            { SpawnBoxFloor(dir, r, hrc, instances); return; }
            // SCN0006 遊樂場拱門: 72 顆燈泡跑馬燈,同樣需要「每顆自己的 material」。見 ArchDengMarquee。
            if (instances.Length == ArchDengMarquee.Bulbs && baseName.ToUpperInvariant() == "DENG" && SceneFolder().ToUpperInvariant() == "SCN0006")
            { SpawnArchDeng(dir, r, hrc, instances); return; }
            // motFile may be null (static prop — e.g. SCN0010 house): skinned to the bind pose once, then frozen.
            // motRelDir lets the .mot live in a different tree than the mesh (scene NPCs: mesh in AVATAR/, .mot in MOTION/).
            MotLoader mot = string.IsNullOrEmpty(motFile) ? null : LoadAsset((motRelDir ?? relDir) + "/" + motFile, b => MotLoader.Load(b));
            var fallbackCol = new Color(0.72f, 0.70f, 0.66f);

            // DIAG (mapobj placement): the parsed mesh bounds (verbatim/baked world coords) + where we place it. For a
            // world-baked prop the bounds center is its real spot and we place at (0,0,0); a model-centered prop has a
            // ~origin center and relies on its placement. Helps spot mis-placed props (e.g. SCN0014 corals).
            {
                Bounds bb = r.Submeshes[0].Mesh.bounds;
                for (int s = 1; s < r.Submeshes.Count; s++) bb.Encapsulate(r.Submeshes[s].Mesh.bounds);
                Debug.Log($"[mapobj.diag] {baseName}: bakedCenter={bb.center} size={bb.size} | inst0.pos={instances[0].Pos} scale={instances[0].Scale} | hrc={(hrc != null ? hrc.Names.Length + "b" : "none")} mot={(mot != null ? "yes" : "no")} subs={r.Submeshes.Count}");
            }

            bool animated = hrc != null && mot != null;

            // RIGID ATTACH (no per-vertex weights): the original binds the whole mesh to ONE HRC bone whose transform
            // positions / orients / scales it. These stage meshes are authored in that bone's LOCAL space — notably
            // 3ds-Max Z-up, so the 'LineXX' bone rotates local-Z -> world-Y to STAND THEM UP. We don't per-vertex-skin
            // a no-weight mesh, so a STATIC prop bakes the leaf bone's bind-world into the verts once (SCN0014 corals
            // lay flat at the origin without this; FIFA_GUANGGAO's bone is identity -> no-op). An ANIMATED prop instead
            // bone-FOLLOWS the leaf bone each frame (below) so its .mot plays — e.g. the SEA_SCREEN video wall spins
            // 360° yaw. Weighted props (GUATAN, the avatar) keep BoneHrc and are skinned normally, so they're skipped.
            bool rigidNoWeights = hrc != null && hrc.BindWorld != null;
            if (rigidNoWeights)
                foreach (var sub in r.Submeshes) if (sub.BoneHrc != null) { rigidNoWeights = false; break; }
            int[] leafBones = rigidNoWeights ? HrcLeafBones(hrc) : System.Array.Empty<int>();
            // STATIC rigid prop: bake each submesh's leaf-bone bind-world into its verts once (submesh i -> leaf i;
            // multi-part props like the trophy put each part on its own bone — but the trophy is animated, below).
            if (rigidNoWeights && !animated && leafBones.Length > 0)
            {
                for (int s = 0; s < r.Submeshes.Count; s++)
                {
                    int bone = leafBones[System.Math.Min(s, leafBones.Length - 1)];
                    Matrix4x4 m = hrc.BindWorld[bone];
                    if (m.isIdentity) continue;
                    var sub = r.Submeshes[s];
                    var vts = sub.Mesh.vertices;
                    for (int i = 0; i < vts.Length; i++) vts[i] = m.MultiplyPoint3x4(vts[i]);
                    sub.Mesh.vertices = vts; sub.Mesh.RecalculateBounds();
                }
                Debug.Log($"[mapobj] {baseName}: rigid-bind {r.Submeshes.Count} submesh(es) to {leafBones.Length} leaf bone(s)");
            }

            // shared materials, one set per submesh (built once; reused by every instance). GPU-instancing
            // capable (Sdo/UnlitInstanced) so a group's copies batch into instanced draws on the GPU. A material
            // whose texture carries real alpha (DXT3/DXT5 cut-out) uses the alpha-blended instanced twin so its
            // transparent regions "去背" instead of painting solid (faithful to the original's per-material blend).
            // Glow props flagged AlphaBlendOverlay (SCN0022 sheguang searchlight) have a banded DXT3 alpha → smooth it so
            // the beam gradient doesn't show concentric "tree-ring" steps (年輪). FULL strength: the beam is a pure gradient
            // with no detail to protect, so flatten every step (the ghost uses PreserveDetail to keep its face). Scoped.
            // SpotGlow 也要去階梯:DXT3 只有 4-bit alpha(16 階),光錐那種平滑衰減會被量化成一圈一圈的
            // 同心台階,邊緣讀起來就是硬的 —— 實測 SCN0019 的 dengzhu_.dds 只有 14 個相異 alpha 值、
            // SCN0016 的 guang1_.dds 只有 12 個,而且全是 17 的倍數(= 純 4-bit 量化)。這才是「聚光燈很硬」
            // 的來源;shader 的 _Spread 只在光錐「外面」補一圈暈,救不了光錐自己的台階,所以先前調 spread
            // 完全沒有改善。
            var glowMode = SceneMapobjUvScrollCatalog.FindRenderMode(SceneFolder(), baseName);
            var glowSmooth = glowMode == SceneMapobjUvScrollCatalog.RenderMode.AlphaBlendOverlay ||
                             glowMode == SceneMapobjUvScrollCatalog.RenderMode.SpotGlow
                              ? DdsLoader.AlphaSmooth.Full : DdsLoader.AlphaSmooth.None;
            var subMats = new List<Material[]>(r.Submeshes.Count);
            // official per-material flags (MSH record +0x194), parallel to subMats — RenderMode.OfficialMaterialAlpha
            // consults them so a multi-material prop only re-blends the materials the artist marked transparent.
            var subMatFlags = new List<uint[]>(r.Submeshes.Count);
            foreach (var sub in r.Submeshes)
            {
                Material[] mats;
                uint[] flags;
                // Only the rigid no-weight stage props (billboards / decals / glows — corals, lights, banners,
                // ground decals) take the alpha-blend treatment; SKINNED props (GUATAN platform, MAO cats) keep the
                // opaque path verbatim so the validated scenes don't regress. (All the reported "沒去背" props are rigid.)
                // 去背 is driven GENERICALLY by the texture, not by an asset list: any material whose DDS carries
                // real alpha (DXT3/DXT5 transparent texels — ResolveDds's `a*`) is alpha-cut, whether the prop is
                // rigid OR skinned. (The old code limited this to rigid props, which left SKINNED cut-outs — SCN0010's
                // feather plumes MAO/MAO1, the SCN0009 掛毯 GUATAN banner — painting their transparent background
                // solid. Opaque-texture props are unaffected: a* is false for them.)
                // VOLUMETRIC 3-D solid (carousel carriage: many verts, thick on all axes) -> alpha uses CUTOUT
                // (alpha-test + ZWrite On) so it isn't see-through and writes depth; FLAT decals/billboards/glows/
                // banners/feathers -> alpha-blend (soft 去背). The volumetric test is what keeps a solid prop from
                // turning see-through, so removing the rigid gate can't regress one.
                Vector3 bsz = sub.Mesh.bounds.size;
                bool separatedFaces = HasSeparatedOpposingFaces(sub.Mesh);
                bool volumetric = sub.Mesh.vertexCount >= 200 && Mathf.Min(bsz.x, Mathf.Min(bsz.y, bsz.z)) > 20f;
                bool singleSidedAlpha = separatedFaces && !volumetric;
                // per-submesh material (cloth/skin split like the avatar): multi-range submesh -> one material per range
                if (sub.Ranges != null && sub.Ranges.Count > 1 && sub.Mesh.subMeshCount == sub.Ranges.Count)
                {
                    mats = new Material[sub.Ranges.Count];
                    flags = new uint[sub.Ranges.Count];
                    for (int s = 0; s < sub.Ranges.Count; s++)
                    {
                        int a = sub.Ranges[s].Attrib;
                        string nm = (sub.DdsNames != null && a >= 0 && a < sub.DdsNames.Length && !string.IsNullOrEmpty(sub.DdsNames[a])) ? sub.DdsNames[a] : sub.Dds;
                        flags[s] = (sub.MatFlags != null && a >= 0 && a < sub.MatFlags.Length) ? sub.MatFlags[a] : sub.DdsFlags;
                        var tex = ResolveDds(dir, nm, out bool a2, out bool glow2, out bool hc2, glowSmooth);
                        // depth-write (cutout) a VOLUMETRIC solid OR an ANIMATED hard-cutout cloth (GUATAN 掛毯): a
                        // moving alpha-blend banner has no ZWrite, so its folds + the scene behind bleed through ("穿模").
                        mats[s] = NewMapobjMat(tex, fallbackCol, a2 && !opaque, a2 && !opaque && (volumetric || (animated && hc2)), a2 && !opaque && singleSidedAlpha, glow2);
                    }
                }
                else
                {
                    var tex = ResolveDds(dir, sub.Dds, out bool a1, out bool glow1, out bool hc1, glowSmooth);
                    // depth-write (cutout) a VOLUMETRIC solid OR an ANIMATED hard-cutout cloth (GUATAN 掛毯) — see above.
                    mats = new[] { NewMapobjMat(tex, fallbackCol, a1 && !opaque, a1 && !opaque && (volumetric || (animated && hc1)), a1 && !opaque && singleSidedAlpha, glow1) };
                    flags = new[] { sub.DdsFlags };
                }
                subMats.Add(mats);
                subMatFlags.Add(flags);
            }

            // SCN0021 saloon ceiling light bars: the 12 deng meshes are NOT independently animated — they share ONE
            // 198×12 on/off marquee driven from saloon/deng/1's 001(dim)/002(lit) (StageScene_UpdatePatternBillboards).
            // Register each bar's materials with the shared driver instead of the per-prop tex-anim path (which can't
            // express a cross-bar pattern and reads as random flicker). Static rendering below still draws the meshes.
            if (SceneFolder().Equals("SCN0021", System.StringComparison.OrdinalIgnoreCase) &&
                System.Text.RegularExpressions.Regex.IsMatch(baseName, @"^DENG\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                int bar = TrailingInt(baseName) - 1;   // DENG1 -> bar 0 (leftmost across the dome)
                var marquee = EnsureSaloonDengMarquee();
                foreach (var ms in subMats) marquee.Register(bar, ms);
            }

            // Animated texture overlay (faithful to the original's UIPicMap frame-swap): a few static props — the FIFA
            // crowd (renqun) and spotlights (shanguang) — are textured by a per-frame DDS sequence cycled every 300 ms,
            // NOT by their MSH material. Drive the shared submesh materials through that sequence. The geometry stays
            // frozen; only the bound texture changes. Critical for SCN0013 night, whose crowd frames are renamed on
            // disk (fifanight_renqun001..009.dds) and so are unreachable by the MSH-material path (rendered white).
            var texAnim = SceneMapobjTexAnimCatalog.Find(SceneFolder(), baseName);
            // Model-embedded "_TexAnimEx(NAME)interval_..." materials (SCN0016 city buildings): no hand-authored
            // catalog entry — read the frame list from "<NAME>.an" in the prop's folder and the interval from the
            // material name. Falls through to the same animator wiring below.
            if (texAnim == null && r.Submeshes.Count > 0 && TexAnimEx.TryParse(r.Submeshes[0].Dds, out var exSpec))
            {
                var anPath = Path.Combine(dir, exSpec.Name + ".an");
                if (File.Exists(anPath))
                {
                    var exFrames = TexAnimEx.ParseAn(File.ReadAllText(anPath));
                    if (exFrames.Length > 0)
                    {
                        ResolveDds(dir, exFrames[0], out bool exAlpha);   // transparent iff the first frame carries alpha
                        // SCN0016 buildings light up once then stay lit (official: play-once tex-anim, not looping)
                        bool holdLast = SceneFolder().Equals("SCN0016", System.StringComparison.OrdinalIgnoreCase);
                        texAnim = new MapobjTexAnim(baseName.ToUpperInvariant(), exFrames, exSpec.IntervalMs > 0f ? exSpec.IntervalMs : 300f, exAlpha, holdLast);
                    }
                }
            }
            if (texAnim != null)
            {
                var frames = new List<Texture2D>(texAnim.Frames.Length);
                bool texAnimAdditive = false;
                foreach (var fn in texAnim.Frames)
                {
                    var t = ResolveDds(dir, fn, out _, out bool frameGlow);
                    if (t != null) { frames.Add(t); texAnimAdditive |= frameGlow; }
                }
                if (frames.Count > 0)
                {
                    var animMats = new List<Material>();
                    foreach (var ms in subMats) if (ms != null) foreach (var m in ms) if (m != null) animMats.Add(m);
                    // The MSH material is a placeholder (often unresolved -> NewMapobjMat tinted it the fallback beige
                    // with no texture). Reset _Color to white so the swapped frame shows true-colour, not tinted.
                    foreach (var m in animMats) m.color = Color.white;
                    // Self-illuminated light-up props (SCN0016 FANGZI7/8) ship a baked per-vertex DIFFUSE of (0,0,0);
                    // UnlitInstanced multiplies texture × vertexColor, so a black vertex colour renders the whole
                    // building black (= invisible against the night sky). Their brightness is the swapped frame, not
                    // baked scene lighting — neutralise near-black vertex colours to white. Props that already carry
                    // white/lit vertex colours (FIFA crowd, sea screen, SCN0011 lights) are left untouched.
                    foreach (var sub in r.Submeshes)
                    {
                        var c = sub.Mesh.colors32;
                        if (c == null || c.Length == 0) continue;
                        bool anyDark = false;
                        for (int i = 0; i < c.Length; i++) if (c[i].r < 8 && c[i].g < 8 && c[i].b < 8) { anyDark = true; break; }
                        if (!anyDark) continue;
                        var w = new Color32[c.Length];
                        for (int i = 0; i < w.Length; i++) w[i] = new Color32(255, 255, 255, 255);
                        sub.Mesh.colors32 = w;
                        Debug.Log($"[mapobj] {baseName}: neutralised black baked vertex colour ({c.Length} verts) for self-illuminated texanim");
                    }
                    // Transparent props (FIFA crowd / spotlights) are alpha-cutout sprites — the opaque mapobj shader
                    // paints their transparent regions solid (stands read empty/black). Switch those to the two-sided
                    // alpha-blended overlay so only the sprite shows. Opaque props (the sea video wall) keep their
                    // material. Same Material instances the renderers use, so this applies to the rendered mesh too.
                    if (texAnim.Transparent)
                    {
                        var overlay = Shader.Find(texAnimAdditive ? "Sdo/UnlitAdditiveOverlay" : "Sdo/UnlitOverlay");
                        if (overlay != null) foreach (var m in animMats) m.shader = overlay;
                    }
                    // OPAQUE video screen drawn ON TOP of a coincident base-scene blank-screen placeholder: SCN0020's
                    // base SCENE.MSH bakes its own TVLITTLE_ blank TV screen at the SAME plane as this TV6 video. That
                    // placeholder is alpha-Blend (Transparent queue, ZWrite Off), so it draws AFTER the opaque video and
                    // can't be depth-occluded by it → it overpaints the live frames and the screen looks frozen. Push the
                    // video's render queue past the base-scene transparent so the video wins (it still depth-tests, so a
                    // nearer prop/dancer still occludes it). Scoped to SCN0020 (the only scene with a coincident blank-
                    // screen placeholder); the validated SCN0014/SCN0017 video walls keep their normal opaque order.
                    if (!texAnim.Transparent && SceneFolder().Equals("SCN0020", System.StringComparison.OrdinalIgnoreCase))
                        foreach (var m in animMats) if (m != null) m.renderQueue = 3100;   // > Transparent(3000), covers the placeholder
                    var holder = new GameObject(baseName + "_texanim");   // root: torn down with the play screen
                    holder.AddComponent<MapobjTexAnimator>().Init(animMats.ToArray(), frames.ToArray(), texAnim.IntervalMs, texAnim.HoldLast);
                    Debug.Log($"[mapobj] {baseName}: texture-anim {frames.Count}/{texAnim.Frames.Length} frames @ {texAnim.IntervalMs}ms, transparent={texAnim.Transparent}");
                }
                else Debug.LogWarning($"[mapobj] {baseName}: texture-anim found no frames in {dir}");
            }

            // Per-scene render-mode override (decoupled from UV-scroll so it also reaches non-scrolling props like
            // the SCN0016 JIGUANG spotlights). Swaps the shader the MSH loader picked for the catalogued target.
            var renderMode = SceneMapobjUvScrollCatalog.FindRenderMode(SceneFolder(), baseName);
            if (renderMode != SceneMapobjUvScrollCatalog.RenderMode.KeepMaterial)
            {
                // OfficialMaterialAlpha is PER-MATERIAL: only the materials the artist flagged transparent get
                // re-blended (SCN0014 TV = beam only, its screen/frame/projector stay opaque). Every other mode keeps
                // the historical prop-wide behaviour.
                for (int si = 0; si < subMats.Count; si++)
                {
                    var ms = subMats[si]; if (ms == null) continue;
                    var fl = si < subMatFlags.Count ? subMatFlags[si] : null;
                    for (int mi = 0; mi < ms.Length; mi++)
                    {
                        if (ms[mi] == null) continue;
                        if (!SceneMapobjUvScrollCatalog.AppliesToMaterial(renderMode, fl != null && mi < fl.Length ? fl[mi] : 0u)) continue;
                        ApplyMapobjRenderMode(ms[mi], renderMode);
                    }
                }
                Debug.Log($"[mapobj] {baseName}: render-mode {renderMode}");
            }

            // Explicit render-queue override (Target.Queue). Separate from the render MODE because it answers a
            // different question — not "how is it blended" but "who wins when it overlaps the stage". SCN0004's
            // sea/shore water must sit BEHIND the huts and the pier: pushing a ZWrite-off transparent prop before
            // the stage's AlphaTest pass (2450) means the stage is drawn afterwards and simply paints over it
            // wherever the stage has geometry. MUST run AFTER ApplyMapobjRenderMode — assigning Material.shader
            // resets a custom renderQueue back to the shader's default, so setting it earlier would be undone.
            if (SceneMapobjUvScrollCatalog.TryFindTarget(SceneFolder(), baseName, out var queueTarget) && queueTarget.Queue > 0)
            {
                foreach (var ms in subMats) if (ms != null) foreach (var m in ms) if (m != null) m.renderQueue = queueTarget.Queue;
                Debug.Log($"[mapobj] {baseName}: render-queue {queueTarget.Queue}");
            }

            // UV-scroll (the original streams texture coords on some props): e.g. SCN0014 corals scroll V so their glow
            // marquees. Drive the shared submesh materials' main-tex offset. Needs Repeat wrap (DdsLoader sets it).
            // Motion is per-entry: most props stream linearly, but SCN0004's sea/wave ROCK (sine) and the
            // SCN0012/0013 ad boards + SCN0029 screen HOLD-then-WIPE (dwell-step). Entries that exist only to carry
            // a RenderMode (SCN0016 JIGUANG, SCN0022 SHEGUANG, SCN0024 DONGHUA) report Animates == false and are skipped.
            if (SceneMapobjUvScrollCatalog.TryFindTarget(SceneFolder(), baseName, out var uvTarget) && uvTarget.Animates)
            {
                var scrollMats = new List<Material>();
                foreach (var ms in subMats) if (ms != null) foreach (var m in ms) if (m != null) scrollMats.Add(m);
                if (scrollMats.Count > 0)
                {
                    var holder = new GameObject(baseName + "_uvscroll");
                    holder.AddComponent<MapobjUvScroll>().Init(scrollMats.ToArray(), uvTarget);
                    Debug.Log($"[mapobj] {baseName}: uv-{uvTarget.Motion} speed={uvTarget.Speed} " +
                              $"amp={uvTarget.Amplitude} step={uvTarget.Step} dwell={uvTarget.DwellMs}ms");
                }
            }

            // ANIMATED rigid prop (no weights, has .mot): the mesh RIGIDLY FOLLOWS its leaf bone's animated world each
            // frame, so the .mot plays without per-vertex skinning — e.g. the SCN0014 sea video wall spins 360° yaw
            // (its .mot is ~550 frames of rotation). The verts stay in bone-local space (NOT baked); one SdoAvatar
            // drives the bone FK and a follower transform carries the mesh. Texture-anim (if any) still drives the look.
            if (rigidNoWeights && animated && leafBones.Length > 0)
            {
                for (int idx = 0; idx < instances.Length; idx++)
                {
                    var parent = new GameObject($"{baseName}_{idx}");
                    parent.transform.position = instances[idx].Pos;
                    parent.transform.rotation = Quaternion.Euler(instances[idx].EulerDeg);
                    parent.transform.localScale = Vector3.one * instances[idx].Scale;
                    var avatar = parent.AddComponent<SdoAvatar>();
                    avatar.Setup(hrc, mot);                                   // drives the bone FK from the .mot (no parts -> no skin)
                    avatar.Fps = MapobjMotionFps(SceneFolder(), baseName);    // SCN0016 floor lights play at half speed
                    avatar.PhaseOffsetSec = phaseOffsetSec;
                    AttachSceneEftsToMapobj(baseName, avatar, parent.transform);
                    // each submesh rides its own leaf bone (trophy: ball on Sphere01, cup on Cylinder01) so the .mot
                    // spins/animates every part; the verts stay in bone-local space (NOT baked).
                    for (int s = 0; s < r.Submeshes.Count; s++)
                    {
                        int bone = leafBones[System.Math.Min(s, leafBones.Length - 1)];
                        var srcMesh = r.Submeshes[s].Mesh;
                        var src = srcMesh.vertices;                                   // original bone-local verts (bake source)
                        var bakeMesh = UnityEngine.Object.Instantiate(srcMesh);      // per-instance clone the baker overwrites
                        bakeMesh.name = baseName + "_bake" + s;
                        // IDENTITY child under the avatar root: the baker writes MODEL-space verts (root = identity in the FK),
                        // i.e. mesh.vert = boneWorld·srcVert each frame. A Transform follower (rotation+lossyScale) shears a
                        // rotating non-uniform-scale prop (the spinning DING wheel went elliptical/變形); baking the full
                        // matrix is faithful for any matrix and identical for uniform-scale props (sea screen / trees).
                        AddMapobjMeshChild(parent.transform, baseName + "_mesh", bakeMesh, subMats[s]);
                        avatar.AddBoneMeshBaker(bone, bakeMesh, src,
                            ShouldApplyRigidBindScale(SceneFolder(), baseName, srcMesh, hrc.BindWorld[bone].lossyScale));
                    }
                    SetLayerRecursive(parent, SceneLayer);
                }
                Debug.Log($"[mapobj] {baseName}: {instances.Length}× rigid bone-follow, {r.Submeshes.Count} submesh(es) on {leafBones.Length} bone(s) (animated .mot)");
                return;
            }

            // GPU-skinning experiment: each animated instance is its own GPU-skinned avatar (SMR per skinned part).
            // The per-vertex blend runs on the GPU; only the bone FK is CPU. Static props skip this (no skinning) and
            // keep the frozen-shared-mesh + instancing path below.
            if (animated && mapobjGpuSkin)
            {
                foreach (var sub in r.Submeshes)
                    if (sub.BindVerts != null && sub.BoneHrc != null)
                        SdoAvatar.PrepareGpuMesh(sub.Mesh, hrc, sub.BoneHrc, sub.BoneWt, sub.MshInvBindByHrc);   // bind data once (shared source mesh)
                for (int idx = 0; idx < instances.Length; idx++)
                {
                    var parent = new GameObject($"{baseName}_{idx}");
                    parent.transform.position = instances[idx].Pos;
                    parent.transform.rotation = Quaternion.Euler(instances[idx].EulerDeg);
                    parent.transform.localScale = Vector3.one * instances[idx].Scale;
                    var avatar = parent.AddComponent<SdoAvatar>();
                    avatar.GpuSkinning = true;
                    avatar.Setup(hrc, mot);
                    avatar.Fps = MapobjMotionFps(SceneFolder(), baseName);    // SCN0016 floor lights play at half speed
                    AttachSceneEftsToMapobj(baseName, avatar, parent.transform);
                    int si = 0;
                    foreach (var sub in r.Submeshes)
                    {
                        if (sub.BindVerts != null && sub.BoneHrc != null)
                            avatar.AddGpuSmr(sub.Mesh, baseName + "_smr").sharedMaterials = subMats[si];
                        else
                            AddMapobjMeshChild(parent.transform, baseName + "_mesh", sub.Mesh, subMats[si]);   // unskinned submesh
                        si++;
                    }
                    SetLayerRecursive(parent, SceneLayer);
                }
                Debug.Log($"[mapobj] {baseName}: {instances.Length}× animated(GPU-skin), {hrc.Names.Length} bones");
                return;
            }

            var placed = new List<Transform>(instances.Length);   // for the position-scroll driver below
            for (int idx = 0; idx < instances.Length; idx++)
            {
                var parent = new GameObject($"{baseName}_{idx}");
                parent.transform.position = instances[idx].Pos;
                parent.transform.rotation = Quaternion.Euler(instances[idx].EulerDeg);
                parent.transform.localScale = Vector3.one * instances[idx].Scale;
                if (idx == 0)
                {
                    // driver: owns the skinned meshes (+ the SdoAvatar that animates them, null DPS -> auto-loops .mot)
                    SdoAvatar avatar = hrc != null ? parent.AddComponent<SdoAvatar>() : null;
                    if (avatar != null)
                    {
                        avatar.Setup(hrc, mot);
                        avatar.Fps = MapobjMotionFps(SceneFolder(), baseName);    // SCN0016 floor lights play at half speed
                        avatar.PhaseOffsetSec = phaseOffsetSec;
                        AttachSceneEftsToMapobj(baseName, avatar, parent.transform);
                    }
                    int si = 0;
                    foreach (var sub in r.Submeshes)
                    {
                        AddMapobjMeshChild(parent.transform, baseName + "_mesh", sub.Mesh, subMats[si++]);
                        if (avatar != null && sub.BindVerts != null && sub.BoneHrc != null)
                            avatar.AddPart(sub.Mesh, sub.BindVerts, sub.BoneHrc, sub.BoneWt, sub.MshInvBindByHrc);
                    }
                    // static prop: pose the bind frame once, then stop updating (clones share the frozen result).
                    if (avatar != null && !animated) { avatar.FeetYAt(0f); avatar.enabled = false; }
                }
                else
                {
                    // clone: render the SAME (driver-skinned) meshes at this transform — no avatar, no extra skinning
                    int si = 0;
                    foreach (var sub in r.Submeshes) AddMapobjMeshChild(parent.transform, baseName + "_mesh", sub.Mesh, subMats[si++]);
                }
                SetLayerRecursive(parent, SceneLayer);
                placed.Add(parent.transform);
            }
            // Props the original SLIDES every tick (SCN0010 花車's two street-front HOUSEs loop past the parade).
            // Nothing else in the remake moves a prop's transform without a .mot, so it gets its own tiny driver.
            var posScroll = SceneMapobjPositionScrollCatalog.Find(SceneFolder(), baseName);
            if (posScroll != null && placed.Count == posScroll.Start.Length)
            {
                var holder = new GameObject(baseName + "_posscroll");
                holder.AddComponent<MapobjPositionScroll>()
                      .Init(placed.ToArray(), posScroll.Start, posScroll.Axis, posScroll.Step,
                            posScroll.TickMs, posScroll.WrapAt, posScroll.WrapTo);
                Debug.Log($"[mapobj] {baseName}: position-scroll {posScroll.PerSecond:0.###}/s, lap {posScroll.LapSeconds:0.##}s");
            }
            else if (posScroll != null)
                Debug.LogWarning($"[mapobj] {baseName}: position-scroll expects {posScroll.Start.Length} instance(s), got {placed.Count}");
            Debug.Log($"[mapobj] {baseName}: {instances.Length}× {(animated ? "animated(shared)" : hrc != null ? "static-skinned" : "static")}, {(hrc != null ? hrc.Names.Length + " bones" : "no skel")}");
        }

        // SCN0001 新天地 的兩面霓虹招牌。每個字是 SCENE.MSH 裡獨立的一個材質/range,所以逐字綁定就是
        // 「照材質名找到那個 submesh」—— SceneLoader.Result.MaterialNames 就是為此存在。
        // 亮版 = SceneLoader 原本建好的 material(貼圖就是材質名那張);暗版另外複製一份,換上少一條底線的
        // DDS,並照官方切成 alpha-test GREATER 160 的 cutout(官方暗態是關混色 + alphatest ref 160,
        // 只留最亮的核心;只換貼圖不換狀態的話,暗版的外暈殘影會糊在招牌上)。
        private void SpawnNeonSigns(MeshRenderer mr, SceneLoader.Result res, string dir)
        {
            var signs = SceneNeonSignCatalog.ForFolder(SceneFolder());
            if (signs.Count == 0 || mr == null || res.MaterialNames == null) return;
            var cutout = Shader.Find("Sdo/SceneVertexCutout");
            SceneNeonSign driver = null;
            int bound = 0;
            foreach (var sign in signs)
            {
                var idx = new int[sign.Length];
                var lit = new Material[sign.Length];
                var dark = new Material[sign.Length];
                bool ok = true;
                for (int i = 0; i < sign.Length && ok; i++)
                {
                    idx[i] = System.Array.FindIndex(res.MaterialNames,
                        n => string.Equals(n, sign.LitDds[i], System.StringComparison.OrdinalIgnoreCase));
                    if (idx[i] < 0) { Debug.LogWarning($"[neon] {SceneFolder()}: 找不到材質 {sign.LitDds[i]}"); ok = false; break; }
                    lit[i] = res.Materials[idx[i]];
                    var darkTex = ResolveDds(dir, sign.DarkDds[i]);
                    if (darkTex == null) { Debug.LogWarning($"[neon] 缺暗版貼圖 {sign.DarkDds[i]}"); ok = false; break; }
                    dark[i] = new Material(lit[i]) { name = "neon_dark_" + sign.DarkDds[i], mainTexture = darkTex };
                    if (cutout != null) dark[i].shader = cutout;
                    if (dark[i].HasProperty("_Cutoff")) dark[i].SetFloat("_Cutoff", 160f / 255f);   // 官方 ALPHAREF 0xA0
                }
                if (!ok) continue;
                if (driver == null) driver = new GameObject("NeonSigns").AddComponent<SceneNeonSign>();
                driver.AddSign(mr, idx, lit, dark);
                bound++;
            }
            if (driver != null)
                Debug.Log($"[neon] {SceneFolder()}: {bound}/{signs.Count} 面招牌接上 (blink {SceneNeonSign.BlinkMs}ms / wipe {SceneNeonSign.WipeMs}ms)");
        }

        // SCN0006 遊樂場 拱門燈泡: 72 個 placement 各自一份 material,交給一個共用的 ArchDengMarquee 驅動。
        // 為什麼要特例:一般的 placement 迴圈讓所有 instance 共用同一組 material,而跑馬燈的定義就是
        // 「這一 tick 第 12 顆亮、第 13 顆暗」—— 共用材質根本表達不出來。與 SCN0003 的 BoxFloor 同一種處理。
        // 燈泡是 4 頂點的平面 quad,靠自己 HRC leaf 的 bind-world 擺到拱門上,所以先把 bind 烘進共用 mesh
        // (和 BoxFloor 一樣;不烘的話 72 顆會全部疊在原點)。
        private void SpawnArchDeng(string dir, MshLoader.Result r, HrcLoader hrc, MapobjInstance[] instances)
        {
            var dim = ResolveDds(dir, "1.dds", out bool dimAlpha, out bool dimGlow, out _);
            var lit = ResolveDds(dir, "2_.dds");
            if (dim == null || lit == null)
            {
                Debug.LogWarning($"[mapobj] ArchDeng: 少了貼圖 (1.dds={dim != null}, 2_.dds={lit != null}) — 退回一般路徑");
                return;
            }
            var mesh = r.Submeshes[0].Mesh;
            if (hrc != null && hrc.BindWorld != null)
            {
                int[] leaves = HrcLeafBones(hrc);
                if (leaves.Length > 0)
                {
                    Matrix4x4 m = hrc.BindWorld[leaves[0]];
                    if (!m.isIdentity)
                    {
                        var vts = mesh.vertices;
                        for (int i = 0; i < vts.Length; i++) vts[i] = m.MultiplyPoint3x4(vts[i]);
                        mesh.vertices = vts; mesh.RecalculateBounds();
                    }
                }
            }
            var holder = new GameObject("DENG_marquee");
            var marquee = holder.AddComponent<ArchDengMarquee>();
            marquee.SetFrames(dim, lit);
            var fallbackCol = new Color(0.72f, 0.70f, 0.66f);
            int n = Mathf.Min(instances.Length, ArchDengMarquee.Bulbs);
            for (int idx = 0; idx < n; idx++)
            {
                var go = new GameObject("DENG_" + idx);
                go.transform.SetParent(holder.transform, false);
                go.transform.localPosition = instances[idx].Pos;
                go.transform.localScale = Vector3.one * instances[idx].Scale;
                go.AddComponent<MeshFilter>().mesh = mesh;
                // 每顆燈自己一份 material — 這正是特例存在的理由。1.dds/2_.dds 都是 32×32 DXT3 帶 alpha 的
                // 小燈泡,走一般的 alpha-blend 判定即可(平面 4 頂點 → 不是 volumetric,不會被判成 cutout)。
                var mat = NewMapobjMat(lit, fallbackCol, dimAlpha, false, false, dimGlow);
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                marquee.Register(idx, new[] { mat });
            }
            SetLayerRecursive(holder, SceneLayer);
            Debug.Log($"[mapobj] DENG arch marquee: {n}/{instances.Length} bulbs, {ArchDengMarquee.IntervalMs}ms, " +
                      $"groups {ArchDengMarquee.GroupACount}%{ArchDengMarquee.GroupACount + 3} + {ArchDengMarquee.GroupBCount}%{ArchDengMarquee.GroupBCount + 3}");
        }

        // SCN0003 disco floor: place the box tile mesh at all 256 instance transforms, each with its OWN opaque
        // material, then drive them as a moving formation (BoxFloorAnimator re-textures each per the decompiled
        // BoxFloorPattern table every 300 ms). Tile index = instance order (= the table's tile index).
        private void SpawnBoxFloor(string dir, MshLoader.Result r, HrcLoader hrc, MapobjInstance[] instances)
        {
            var fallbackCol = new Color(0.72f, 0.70f, 0.66f);
            var frames = new Texture2D[6];
            for (int i = 0; i < 6; i++) frames[i] = ResolveDds(dir, "BOX_" + i + ".dds");
            var mesh = r.Submeshes[0].Mesh;
            // The tile mesh is authored at Y=+14.6 (bone-local); its HRC leaf bind-world translates Y−14.6 to seat it
            // on the floor. Bake that bind-world into the shared mesh once (the rigid-attach the normal path does) —
            // without it the tiles float at ~ankle height. (BOX bind = pure Y offset, no rotation.)
            if (hrc != null && hrc.BindWorld != null)
            {
                int[] leaves = HrcLeafBones(hrc);
                if (leaves.Length > 0)
                {
                    Matrix4x4 m = hrc.BindWorld[leaves[0]];
                    if (!m.isIdentity)
                    {
                        var vts = mesh.vertices;
                        for (int i = 0; i < vts.Length; i++) vts[i] = m.MultiplyPoint3x4(vts[i]);
                        mesh.vertices = vts; mesh.RecalculateBounds();
                    }
                }
            }
            var mats = new Material[instances.Length];
            var holder = new GameObject("BOX_floor");
            for (int idx = 0; idx < instances.Length; idx++)
            {
                var go = new GameObject("BOX_" + idx);
                go.transform.SetParent(holder.transform, false);
                go.transform.localPosition = instances[idx].Pos;
                go.transform.localScale = Vector3.one * instances[idx].Scale;
                go.AddComponent<MeshFilter>().mesh = mesh;
                var m = NewMapobjMat(frames[0], fallbackCol);   // opaque tile; the animator swaps its texture per the pattern
                mats[idx] = m;
                go.AddComponent<MeshRenderer>().sharedMaterial = m;
            }
            holder.AddComponent<BoxFloorAnimator>().Init(mats, frames);
            SetLayerRecursive(holder, SceneLayer);
            Debug.Log($"[mapobj] BOX disco floor: {instances.Length} tiles, pattern {BoxFloorPattern.Steps} steps");
        }

        // One GPU-instancing-capable unlit material for a mapobj submesh (Cull Back, texture × tint), so a group's
        // shared-mesh copies batch into instanced GPU draws. Falls back to the built-in Unlit shaders if the custom
        // one isn't present (then no instancing, but identical look). tex==null -> flat fallback colour.
        private static Material NewMapobjMat(Texture2D tex, Color fallbackCol, bool alpha = false, bool cutout = false, bool singleSidedAlpha = false, bool additiveGlow = false)
        {
            // opaque -> instanced opaque; flat alpha decal/billboard/glow -> alpha-blend (Cull Off, ZWrite Off);
            // mirrored separated alpha planes -> alpha-blend + Cull Back, so only the facing mirror is visible;
            // VOLUMETRIC alpha solid (carousel carriage) -> alpha-test cutout (ZWrite On) so it doesn't
            // render see-through ("穿透").
            string name = alpha && additiveGlow && !cutout ? "Sdo/UnlitAdditiveOverlay"
                        : cutout ? "Sdo/UnlitInstancedCutout"
                        : singleSidedAlpha ? "Sdo/UnlitInstancedAlphaCullBack"
                        : alpha ? "Sdo/UnlitInstancedAlpha"
                        : "Sdo/UnlitInstanced";
            var inst = Shader.Find(name);
            if (inst != null)
            {
                var m = new Material(inst) { enableInstancing = true };
                if (tex != null) m.mainTexture = tex; else m.color = fallbackCol;   // _MainTex defaults to white -> tint shows
                return m;
            }
            string fb = cutout ? "Unlit/Transparent Cutout" : alpha ? "Unlit/Transparent" : "Unlit/Texture";
            return tex != null ? new Material(Shader.Find(fb)) { mainTexture = tex }
                               : new Material(Shader.Find("Unlit/Color")) { color = fallbackCol };
        }

        // .mot playback rate (fps) for an animated mapobj. Default 30. SCN0016 floor lights DI1-21 play at HALF speed
        // in the original (decompiled motion-speed 0.015 vs the default 0.030 → 15 fps, scene-0x10 init ~line 130258);
        // at 30 fps their coordinated slow fade plays 2× too fast and reads as fast/chaotic sequential flicker.
        private static float MapobjMotionFps(string folder, string baseName)
        {
            if (string.Equals(folder, "SCN0016", System.StringComparison.OrdinalIgnoreCase) &&
                baseName != null &&
                System.Text.RegularExpressions.Regex.IsMatch(baseName, @"^DI\d+$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return 15f;
            return 30f;
        }

        private static void ApplyMapobjRenderMode(Material mat, SceneMapobjUvScrollCatalog.RenderMode mode)
        {
            if (mat == null || mode == SceneMapobjUvScrollCatalog.RenderMode.KeepMaterial) return;
            if (mode == SceneMapobjUvScrollCatalog.RenderMode.AdditiveOverlay)
            {
                var shader = Shader.Find("Sdo/UnlitAdditiveOverlay");
                if (shader != null) mat.shader = shader;
                if (mat.HasProperty("_Color")) mat.color = Color.white;
            }
            else if (mode == SceneMapobjUvScrollCatalog.RenderMode.AlphaBlendOverlay)
            {
                // two-sided standard alpha-blend — undo an additive false-positive (SCN0022 sheguang searchlight). Faint
                // the alpha (SCN0015 窗光 lesson) so the beam is a soft translucent shaft, not a solid additive-bright bar.
                var shader = Shader.Find("Sdo/UnlitOverlay");
                if (shader != null) mat.shader = shader;
                if (mat.HasProperty("_Color")) mat.color = new Color(1f, 1f, 1f, GhostSpriteAlpha);
            }
            else if (mode == SceneMapobjUvScrollCatalog.RenderMode.ForceAlphaBlend)
            {
                // Override whatever shader the MSH loader chose with the standard alpha-blend shader.
                // Required when LooksLikeAdditiveGlow incorrectly classifies a texture that D3D9 uses as
                // SrcAlpha/InvSrcAlpha (standard blend), not SrcAlpha/One (additive).
                // D3D9 capture shows CULL=3 (CW = single-sided). Use CullBack variant to match;
                // Cull Off would render the quad twice (front + back), doubling the effective opacity.
                var shader = Shader.Find("Sdo/UnlitInstancedAlphaCullBack");
                if (shader != null) mat.shader = shader;
                // Alpha multiplier for the window beam. Texture max alpha is 33%; this value scales
                // it further so the overall opacity can be tuned without touching the DDS asset.
                if (mat.HasProperty("_Color")) mat.color = new Color(1f, 1f, 1f, 0.2f);
            }
            else if (mode == SceneMapobjUvScrollCatalog.RenderMode.OfficialMaterialAlpha)
            {
                // The artist flagged this material transparent (MSH +0x194 & 0x3f). The engine has ONE transparent
                // mode — standard SrcAlpha/InvSrcAlpha at full texture alpha — so drop whatever the heuristics chose
                // (additive glow / alpha-test cutout) for the plain instanced alpha-blend material.
                var shader = Shader.Find("Sdo/UnlitInstancedAlpha");
                if (shader != null) mat.shader = shader;
                if (mat.HasProperty("_Color")) mat.color = Color.white;
            }
            else if (mode == SceneMapobjUvScrollCatalog.RenderMode.ForceOpaque)
            {
                // 官方旗標 0 = 不透明批,alpha 通道是死的 —— 這不是「cutout 但不 clip」,是**根本不是 cutout**。
                // 只把 _Cutoff 設 -1 會把材質留在 Sdo/UnlitInstancedCutout,而那支跟真正的不透明
                // Sdo/UnlitInstanced 差在**頂點色的乘法空間**:
                //   Cutout        : c.rgb *= i.col.rgb                                    (linear 空間)
                //   UnlitInstanced: GammaToLinear(LinearToGamma(tex) × _Color × i.col)     (gamma 空間,刻意複製 D3D9)
                // SCN0004 的海床是**兩片相鄰的 mapobj**:外海 SEA_UP 走不透明批、近岸 SEA_DOWN 走這裡,
                // 兩片的頂點色都不是白的(SEA_UP 24 種、SEA_DOWN 12 種偏暗藍灰),於是同一片海被兩套亮度
                // 數學畫出來 —— 接縫上就是一條亮度階差,也就是使用者看到的「海水裡奇怪的分割線」。
                // 換成不透明 shader 兩件事一起成立:官方語意正確(不透明批不看 alpha),而且跟鄰片同一條
                // 顏色路徑。_Cutoff 仍設 -1,以防 fallback 落回 cutout。
                var opaque = Shader.Find("Sdo/UnlitInstanced");
                if (opaque != null) mat.shader = opaque;
                if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", -1f);
            }
            else if (mode == SceneMapobjUvScrollCatalog.RenderMode.SpotGlow)
            {
                // Soft searchlight beam (SCN0016 spotlights): additive shader that blurs the texture along its
                // width so the light spreads sideways and the narrow hard alpha edge becomes a soft falloff.
                var shader = Shader.Find("Sdo/UnlitSpotGlow");
                if (shader != null) mat.shader = shader;
                if (mat.HasProperty("_Color")) mat.color = Color.white;
            }
        }

        // One renderer for a mapobj submesh: a child GameObject with a MeshFilter pointing at the (possibly shared)
        // mesh and a MeshRenderer with the shared material set. Used for both the driver and its clone instances.
        private static void AddMapobjMeshChild(Transform parent, string name, Mesh mesh, Material[] mats)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().mesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            if (mats != null && mats.Length == 1) mr.sharedMaterial = mats[0];
            else if (mats != null && mats.Length > 1) mr.sharedMaterials = mats;
        }

        // Props whose bind/.mot scale IS the effect and must survive the generic guard below. Scene folder + mesh
        // base name (both upper-invariant), so the exception can never leak to a same-named prop in another scene.
        //   SCN0024/DONGHUA — 雪景的探照燈。它是一片 94×443.5 的光錐 quad,長軸(local Z)被 HRC rest bind 與
        //     .mot 的常數 scale key 同時拉長 ×3.93,那個拉長「就是光柱本身」。通用防呆會丟掉它(maxScale 3.88 > 2,
        //     且 mesh 443 單位不小於 80),結果只剩一截 453 單位的短樁埋在背景建築裡 —— 使用者看到的「探照燈沒做出來」。
        //     只放行這一支:整條規則放寬會一併改到 17_DITIE/SKY(×18.7)與 FIFA_QIUBEI(×2.17),那兩個現在是對的。
        private static bool IsRigidBindScaleException(string folder, string baseName) =>
            string.Equals(folder, "SCN0024", System.StringComparison.OrdinalIgnoreCase) &&
            string.Equals(baseName, "DONGHUA", System.StringComparison.OrdinalIgnoreCase);

        private static bool ShouldApplyRigidBindScale(string folder, string baseName, Mesh mesh, Vector3 bindScale)
        {
            if (IsRigidBindScaleException(folder, baseName)) return true;
            if (mesh == null) return true;
            if (HasSeparatedOpposingFaces(mesh)) return true;

            float maxScale = Mathf.Max(Mathf.Abs(bindScale.x), Mathf.Max(Mathf.Abs(bindScale.y), Mathf.Abs(bindScale.z)));
            if (maxScale <= 2f) return true;

            Vector3 size = mesh.bounds.size;
            float maxSize = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            return maxSize < 80f;
        }

        private static bool HasSeparatedOpposingFaces(Mesh mesh)
        {
            if (mesh == null || mesh.vertexCount < 6) return false;
            var verts = mesh.vertices;
            var tris = mesh.triangles;
            int triCount = tris.Length / 3;
            if (triCount < 2 || triCount > 512) return false;

            var normals = new Vector3[triCount];
            var centers = new Vector3[triCount];
            int n = 0;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int ia = tris[t], ib = tris[t + 1], ic = tris[t + 2];
                if (ia < 0 || ib < 0 || ic < 0 || ia >= verts.Length || ib >= verts.Length || ic >= verts.Length) continue;
                Vector3 a = verts[ia], b = verts[ib], c = verts[ic];
                Vector3 normal = Vector3.Cross(b - a, c - a);
                float mag = normal.magnitude;
                if (mag < 1e-4f) continue;
                normals[n] = normal / mag;
                centers[n] = (a + b + c) / 3f;
                n++;
            }

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    float dot = Vector3.Dot(normals[i], normals[j]);
                    if (dot > -0.95f) continue;
                    float separation = Mathf.Abs(Vector3.Dot(centers[j] - centers[i], normals[i]));
                    if (separation > 5f) return true;
                }
            }
            return false;
        }

        // Leaf bones of an HRC (bones that are no one's parent) in index order. A rigid no-weight prop attaches each
        // submesh to a bone; for a single-part prop there's one leaf (corals, crowd), for a multi-part prop the leaves
        // line up with the submeshes in order (FIFA_QIUBEI: leaf[0]=under Sphere01 -> the ball submesh, leaf[1]=under
        // Cylinder01 -> the cup submesh). Each leaf's bind-world is what positions/orients/scales that part.
        private static int[] HrcLeafBones(HrcLoader hrc)
        {
            if (hrc == null || hrc.Names == null) return System.Array.Empty<int>();
            int bc = hrc.Names.Length;
            var hasChild = new bool[bc];
            for (int i = 0; i < bc; i++) { int p = hrc.Parent[i]; if (p >= 0 && p < bc) hasChild[p] = true; }
            var leaves = new List<int>();
            for (int i = 0; i < bc; i++) if (!hasChild[i]) leaves.Add(i);
            return leaves.ToArray();
        }

        // Trailing integer in a name ("DENG12" -> 12, "DENG" -> 0). Used to index a numbered series of props.
        private static int TrailingInt(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            int i = name.Length; while (i > 0 && char.IsDigit(name[i - 1])) i--;
            return (i < name.Length && int.TryParse(name.Substring(i), out int n)) ? n : 0;
        }

        // SCN0021 saloon ceiling-light marquee: one shared driver for all 12 deng bars (lazily created on the first
        // deng, frames loaded once from saloon/deng/1 — the only deng folder that ships 001/002). See SaloonDengMarquee.
        private SaloonDengMarquee _saloonDeng;
        private SaloonDengMarquee EnsureSaloonDengMarquee()
        {
            if (_saloonDeng == null)
            {
                var go = new GameObject("SALOON_DENG_marquee");   // root: torn down with the play screen
                _saloonDeng = go.AddComponent<SaloonDengMarquee>();
                var shared = Path.Combine(SdoExtracted.Root, "SCENE", "MAPOBJ", "SALOON", "DENG", "1");
                var dim = ResolveDds(shared, "001.dds", out _, out _);
                var lit = ResolveDds(shared, "002.dds", out _, out _);
                _saloonDeng.SetFrames(dim, lit);
                Debug.Log($"[mapobj] SCN0021 deng marquee: dim={(dim != null)} lit={(lit != null)} from {shared}");
            }
            return _saloonDeng;
        }

        private void ApplySceneMaterialUvScroll(string folder, Material[] mats, int[] materialIds)
        {
            if (mats == null || materialIds == null) return;
            var speeds = new List<Vector2>();
            var groups = new List<List<Material>>();
            for (int i = 0; i < mats.Length && i < materialIds.Length; i++)
            {
                Vector2 v = SceneMapobjUvScrollCatalog.Find(folder, SceneMapobjUvScrollCatalog.SceneObject, materialIds[i]);
                if (v == Vector2.zero || mats[i] == null) continue;
                ApplyMapobjRenderMode(mats[i], SceneMapobjUvScrollCatalog.FindRenderMode(folder, SceneMapobjUvScrollCatalog.SceneObject, materialIds[i]));
                int group = -1;
                for (int g = 0; g < speeds.Count; g++)
                {
                    if (speeds[g] == v) { group = g; break; }
                }
                if (group < 0)
                {
                    group = speeds.Count;
                    speeds.Add(v);
                    groups.Add(new List<Material>());
                }
                groups[group].Add(mats[i]);
            }

            for (int g = 0; g < groups.Count; g++)
            {
                var holder = new GameObject($"StageScene_uvscroll_{g}");
                holder.AddComponent<MapobjUvScroll>().Init(groups[g].ToArray(), speeds[g]);
                Debug.Log($"[scene] {folder}: uv-scroll {groups[g].Count} material(s) @ {speeds[g]}");
            }
        }

        private void TryLoadScene()
        {
            const int sceneLayer = SceneLayer;   // builtin "Water" layer, repurposed for the 3D stage
            Bounds b = new Bounds(_avatarChest, new Vector3(120f, 120f, 120f));
            if (!avatarDebug)
            {
                var dir = Path.Combine(SdoExtracted.Root, scenePath.Replace('/', Path.DirectorySeparatorChar));
                var mshPath = Path.Combine(dir, "SCENE.MSH");
                if (!File.Exists(mshPath)) { Debug.LogWarning("[scene] missing " + mshPath); return; }
                SceneLoader.Result res;
                try { res = SceneLoader.Load(File.ReadAllBytes(mshPath), dir); }
                catch (System.Exception e) { Debug.LogWarning("[scene] load fail: " + e.Message); return; }
                if (res == null || res.Mesh == null) { Debug.LogWarning("[scene] parse fail"); return; }
                var go = new GameObject("StageScene") { layer = sceneLayer };
                go.AddComponent<MeshFilter>().mesh = res.Mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = res.Materials;
                ApplySceneMaterialUvScroll(SceneFolder(), res.Materials, res.MaterialIds);
                SpawnNeonSigns(go.GetComponent<MeshRenderer>(), res, dir);   // SCN0001 兩面招牌的逐字閃爍
                _pendingLensFlareDir = SceneLensFlareCatalog.Has(SceneFolder()) ? dir : null;   // SCN0004 太陽光斑
                b = res.Mesh.bounds;
                // render at NATIVE SDO world coords (no lift). The .cv cameras + the avatar dance spot (_avatarChest)
                // are authored in this same space with the dancer standing on the native floor, so they line up.
                Debug.Log($"[scene] {SceneFolder()}: {res.Materials.Length} subsets, bounds c={b.center} s={b.size}");
                TryLoadMapobjs();   // stage props on the same layer
                TryLoadSceneAvatars();   // background NPCs ("場景的人" — e.g. SCN0017 subway passengers)
                SpawnSceneFlames();   // camera-facing BillboardSet sprites (SCN0022 坟墓 鬼火) — a scene prop, always on
                SpawnSceneGhosts();   // .mot-driven camera-facing sprites (SCN0022 坟墓 飛鬼) — flat mesh would foreshorten
                if (effectScene) SpawnSceneEffects();   // 場景特效開關 (OPTION 遊戲頁)：常駐背景 EFT (SCN0008 magic circle, snow, aurora, …)
            }

            // Perspective camera renders the stage(+avatar, same layer) to a RenderTexture; a full-screen background
            // quad in the main ortho cam shows that RT (reliably displays; depth-stacked cameras came out all-black).
            // The RT is WINDOW-shaped and oversampled (RtSizing) — sizing it 4:3 (the old height×4/3) made it narrower
            // than the pixels the Stretch-mode quad spreads it over, so the stage/avatar got magnified horizontally.
            // The 4:3 projection is pinned below instead of being inferred from the RT shape. 4× MSAA on top keeps the
            // avatar/stage edges smooth. Re-allocated on window resize by MaintainSceneRt().
            RtSizing.SlotRtSize(Screen.width, Screen.height, RtSizing.LogicalW, RtSizing.LogicalH,
                                sceneSupersample, out int rtW, out int rtH);
            var sceneRT = new RenderTexture(rtW, rtH, 24) { name = "sceneRT", antiAliasing = 4, filterMode = FilterMode.Bilinear };
            _sceneRT = sceneRT;
            _sceneRtTrack.Reset(Screen.width, Screen.height);
            Debug.Log($"[scene] backdrop RT {rtW}x{rtH} (window {Screen.width}x{Screen.height}, ss={sceneSupersample:0.##})");
            var camGo = new GameObject("SceneCam") { layer = sceneLayer };
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = false; cam.fieldOfView = 45f;
            cam.cullingMask = 1 << sceneLayer; cam.targetTexture = sceneRT;
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
            // EXACT decompiled projection (Camera_ctor 004_camera_0040a420.c: fovY=0x3f490fdb=45°, aspect=0x3faaaaab=4/3,
            // zNear=0x40a00000=5, zFar=0x45ea6000=7500). The old near=1 / far=bounds×4 wrecked depth precision on big
            // scenes — SCN0020's ~11.5k-unit ground plane pushed far to ~64000, so the 1:64000 ratio z-fought (破圖),
            // worst on fixed cam5 (eye z=-346) which spans the whole stage in depth. 5/7500 = ~1:1500, matching the
            // original for most maps. (The gameplay module's own projection — 023_gameplay 0x482340 — actually uses
            // far=0x47927c00=75000; D3D9's linear W-buffer never z-fought at that range, but Unity's Z-buffer does,
            // hence the low 7500 compromise.) A handful of venues have a SKY ceiling FAR above the play area — FIFA
            // day (SCN0012 top Y≈11949) / night (SCN0013≈8157), SCN0018≈16284 — that sits BEYOND 7500 in view-depth,
            // so at 7500 the sky is clipped and the top of the frame renders as the black clear colour (回報: 足球場
            // 天空全黑 / 夜晚方形黑塊). Raise far JUST enough to reach that ceiling — ×1.5 covers the extra view-depth
            // when the camera looks up at a high AND distant sky point (night needs ~10.5k for an 8.2k-high sky) —
            // capped at 20000 so the near/far ratio stays ≤4000 (well under the z-fight range). Flat venues
            // (SCN0020 top≈2582, every other map ≤5.1k) clamp back to exactly 7500, so nothing that already works regresses.
            float sceneTopY = b.max.y;   // b = res.Mesh.bounds, native coords — same space as this camera
            float sceneFar = Mathf.Clamp(sceneTopY * 1.5f, 7500f, 20000f);
            cam.nearClipPlane = 5f; cam.farClipPlane = sceneFar;
            Debug.Log($"[scene] {SceneFolder()}: camera far={sceneFar:F0} (sky top Y={sceneTopY:F0})");
            _sceneCam = cam;
            // The local marker is built by TryLoadAvatar before this camera exists. Promote only here, after
            // scene setup succeeded; an early scene-load return therefore leaves its legacy HUD fallback intact.
            if (_headMarker != null && use3dCamera)
                _headMarker.EnableDepthTestedWorld(SceneLayer);
            if (avatarDebug)
            {
                // clean STRAIGHT-FRONT orthographic view of the avatar (matches the reference avatar_viewer framing,
                // no perspective foreshortening) over a black background. Front dir = cam0's horizontal view dir.
                Bounds ab = AvatarWorldBounds();
                Vector3 fwd = FixedTgt[0] - FixedEye[0]; fwd.y = 0f;   // horizontal dir of fixed cam0
                if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.back;
                fwd.Normalize();
                cam.orthographic = true;
                cam.orthographicSize = Mathf.Max(ab.extents.y, ab.extents.x, 1f) * 1.45f;   // extra room for dance motion
                cam.transform.position = ab.center - fwd * Mathf.Max(800f, ab.size.magnitude * 4f);
                cam.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
            }
            else if (use3dCamera && _camReady)
            {
                // initial pose = fixed cam0 (absolute); the live camera is driven verbatim every frame in Update
                cam.transform.position = FixedEye[0]; cam.transform.LookAt(FixedTgt[0], Vector3.up);
            }
            else
            {
                float pitchUp = CvCameraPitchUp();   // backdrop-only: borrow the .cv up-pitch (~14°)
                float dz = b.extents.z * 0.85f;
                cam.transform.position = b.center + new Vector3(0f, -b.extents.y * 0.22f, -dz);
                cam.transform.LookAt(b.center + new Vector3(0f, dz * Mathf.Tan(pitchUp), 0f), Vector3.up);
            }
            if (_cam != null) _cam.cullingMask &= ~(1 << sceneLayer);   // main cam shows the stage only via the quad

            // full-screen background quad textured with the scene render. NATURAL (un-flipped) UVs: the live screen
            // (and the headless capture, which matches it) showed the stage+avatar UPSIDE-DOWN with a flipped V, so
            // the quad samples sceneRT bottom-at-v=0 / top-at-v=1. F9 toggles a flip at runtime if a platform differs.
            var quad = new GameObject("SceneBackdrop");
            var mf = quad.AddComponent<MeshFilter>();
            mf.mesh = new Mesh
            {
                vertices = new[] { new Vector3(-400, -300, 90), new Vector3(400, -300, 90), new Vector3(400, 300, 90), new Vector3(-400, 300, 90) },
                uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) },
                triangles = new[] { 0, 2, 1, 0, 3, 2 }   // face -Z toward the camera (at z=-100)
            };
            _backdropMat = new Material(Shader.Find("Unlit/Texture")) { mainTexture = sceneRT };
            quad.AddComponent<MeshRenderer>().sharedMaterial = _backdropMat;
            SpawnLensFlare(quad.layer);
        }

        // SCN0004 太陽的鏡頭光斑鏈。畫在合成 quad(z=90)前面的 z=89，也就是「3D 場景之後、2D HUD 之前」——
        // 與官方 Gameplay_PostRender 的呼叫位置等價(HUD 在另一台相機，之後才畫)。
        private string _pendingLensFlareDir;
        private void SpawnLensFlare(int backdropLayer)
        {
            if (string.IsNullOrEmpty(_pendingLensFlareDir)) return;
            var atlas = SceneLensFlare.LoadAtlas(_pendingLensFlareDir);
            if (atlas == null) { Debug.LogWarning("[flare] 缺 LENSFLARE.BMP: " + _pendingLensFlareDir); return; }
            // 一定要用 _sceneCam(渲染到 sceneRT 的那台舞台相機)。掃 Camera.allCameras 取第一台透視相機
            // 會抓到別的東西 —— 實測抓到一台停在 (0,34,3794) 朝 +Z 的相機，太陽在它正後方 177.8°，
            // 於是可見性判定永遠不過、光斑一次都沒畫出來。
            var stage = _sceneCam;
            if (stage == null) { Debug.LogWarning("[flare] _sceneCam 還沒建立"); return; }
            var go = new GameObject("SunLensFlare");
            var lf = go.AddComponent<SceneLensFlare>();
            lf.Init(stage, atlas, backdropLayer);
            if (!string.IsNullOrEmpty(DevVar("SDO_FLARE_DIAG"))) lf.DiagEverySec = 1f;
            Debug.Log($"[flare] {SceneFolder()}: {SceneLensFlare.Elements.Length} 顆光斑, 壽命 {lf.LifetimeSec}s (官方 10s 後永遠消失)");
            _pendingLensFlareDir = null;
        }

        private static Sprite _piyoriSprite;
        private static Texture2D _piyoriTex;
        // yuanpan.eft emitter[0]: a 14-segment annulus mesh (inner:outer = 0.18:0.27), each segment textured with
        // the FULL real generic\zako\z_piyori1 (a HOLLOW gold star) — i.e. 14 hollow stars round the ring, drawn the
        // engine's way (a band mesh, not sprites). Additive, flat on floor, spins, follows the dancer's pelvis.
        // ringOuterRadius = spread (ring radius); ringBrightness = additive glow level; both live-tunable in the F4 panel.
        public float ringOuterRadius = 22f, ringSpinDeg = 20f, ringBrightness = 0.9f;
        private Transform _ringTr; private Material _ringMat; private FloorRing _floorRing;   // 本機那一個(連打特效/相機都掛在它身上)

        /// <summary>場上每一個星環(含遠端舞者的)。F4 的大小/亮度/轉速滑桿一次套用到全部。</summary>
        private struct RingRef { public Transform Tr; public Material Mat; public FloorRing Ring; }
        private readonly List<RingRef> _rings = new List<RingRef>();

        // 組隊時腳下那圈**彩色光暈**的大小,單位是「星環外半徑的幾倍(邊長)」。
        // 2.67 = 讓光暈最亮的那一圈(CR.TGA 的環帶尖峰在半徑 0.625 處)正好落在星環的中線(0.833)上:
        // 0.625 × 2.67/2 ≈ 0.833。改大 = 光暈往外擴。
        public float teamGlowScale = 2.67f;

        /// <param name="team">0=A 1=B 2=C,其他 = 沒組隊(白)。組隊時腳下的星環就是自己那一隊的顏色。</param>
        /// <param name="local">true = 本機那一位 —— 只有它的 ref 會存進 <see cref="_ringTr"/> 那組
        /// (combo 特效/完奏特效/相機都拿它當錨點,指到別人身上會讓特效跑到別人腳下)。</param>
        /// <returns>這一圈星環的 transform —— 那也是**這位舞者的特效錨點**(FINISHED 掛在贏家腳下,
        /// 見 <see cref="FinishedEftAnchor"/>);2D 退化路徑一樣有,只是不跟著骨盆走。</returns>
        private Transform CreateGroundStarRing(float x, float yOrZ, float floorY, SdoAvatar avatar, Transform avatarParent,
                                               int team = TeamColors.Free, bool local = true)
        {
            string zako = Path.Combine(SdoExtracted.Root, "3DEFT", "GENERIC", "ZAKO");

            if (use3dCamera)   // faithful ring-band MESH lying flat on the floor at the dancer's feet
            {
                if (_piyoriTex == null) _piyoriTex = SdoExtracted.LoadTextureRaw(zako, "Z_PIYORI1_W.png");   // z_piyori1 desaturated -> white hollow star
                var ringGo = new GameObject("GroundStarRing");
                // mesh built at UNIT outer radius (inner = decoded 0.18:0.27); transform.localScale = ringOuterRadius
                // sets the spread, so size/brightness/spin can all be dragged live in the F4 panel (ApplyRingDebug).
                ringGo.AddComponent<MeshFilter>().mesh = FloorRing.BuildBand(14, 0.18f / 0.27f, 1f);
                var mr = ringGo.AddComponent<MeshRenderer>();
                var mat = _addMat != null ? new Material(_addMat) : new Material(Shader.Find("Sprites/Default"));
                if (_piyoriTex != null) mat.mainTexture = _piyoriTex;
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;

                var fr = ringGo.AddComponent<FloorRing>();
                fr.FloorY = floorY;
                _rings.Add(new RingRef { Tr = ringGo.transform, Mat = mat, Ring = fr });
                if (local) { _ringTr = ringGo.transform; _ringMat = mat; _floorRing = fr; }
                AddTeamGlowDisc(ringGo.transform, team);
                ApplyRingDebug();
                if (avatar != null && avatarParent != null)   // follow pelvis (root GO is static; bones dance)
                {
                    int b = avatar.BoneIndex("Bip01_Pelvis");
                    if (b < 0) b = avatar.BoneIndex("Bip01_Spine");
                    if (b < 0) b = avatar.BoneIndex("Bip01");
                    if (b >= 0)
                    {
                        var anchor = new GameObject("RingAnchor");
                        anchor.transform.SetParent(avatarParent, false);
                        avatar.AddAnchor(b, anchor.transform);
                        fr.Follow = anchor.transform;
                    }
                }
                if (fr.Follow == null) ringGo.transform.position = new Vector3(x, floorY, yOrZ);   // FloorRing sets rotation each frame
                SetLayerRecursive(ringGo, SceneLayer);
                return ringGo.transform;
            }
            else   // 2D fallback: sprite ellipse over the feet (no follow)
            {
                if (_piyoriSprite == null) _piyoriSprite = SdoExtracted.LoadImage(zako, "Z_PIYORI1_W.png") ?? MakeStar5Sprite();
                var ringGo = new GameObject("GroundStarRing");
                const int n = 14; var stars = new SpriteRenderer[n];
                for (int i = 0; i < n; i++)
                {
                    var sr = new GameObject("Star" + i).AddComponent<SpriteRenderer>();
                    sr.transform.SetParent(ringGo.transform, false);
                    sr.sprite = _piyoriSprite; sr.sortingOrder = -8;
                    if (_addMat != null) sr.sharedMaterial = new Material(_addMat);
                    stars[i] = sr;
                }
                var ring = ringGo.AddComponent<StarRing>();
                ring.Stars = stars; ring.Spin = 0.6f; ring.Tint = Color.white;   // 2D 退化路徑沒有隊伍光暈(它只在 3D 舞台出現)
                ringGo.transform.position = new Vector3(x, yOrZ + 4f, 6f);
                ring.Billboard = true; ring.Rx = 70f; ring.Ry = 20f; ring.BaseScale = 36f / 64f;
                return ringGo.transform;
            }
        }

        // Live-apply the F4 ring sliders. Mesh is unit-radius, so localScale = spread; _TintColor.rgb = brightness
        // (legacy-particle additive ×2 → ringBrightness*0.5 = native); keep _TintColor.a = 1 so the SrcAlpha-One blend
        // doesn't dim it a SECOND time (that earlier double-dim is what made it vanish).
        /// <summary>
        /// 組隊時腳下多疊的那圈**彩色光暈**(官方 yuanpan_r/_g/_b.eft 相對 yuanpan.eft 多出來的那一支)。
        ///
        /// 🔴 官方**不是**把白星環染色 —— 反編譯的 <c>FUN_004a6720</c> 用舞者結構 +0x2e1 那個 byte 去查
        /// <c>{0, 10, 11, 12}</c>,整支換成 yuanpan / yuanpan_r / _g / _b.eft;四份檔案的差別只有
        /// 「root 播放清單 1 支變 2 支」與「多出來那支 emitter 的貼圖是 generic\map_g\cr / cg / cb」。
        /// 也就是說星環本身永遠是白的,隊伍色是**底下多疊的一片平躺彩色環形光暈**。這裡照做:
        /// 一張貼著官方那張貼圖的平面 quad,掛在星環底下(跟著它的 localScale 一起縮放、一起跟著骨盆走)。
        ///
        /// (CR/CG/CB 原檔是 .TGA,Unity 的 <c>Texture2D.LoadImage</c> 不吃 —— 已在 Extracted 同目錄
        /// 轉出同名 .png,與那棵樹裡 BMP→PNG 的雙胞胎慣例一致。)
        /// </summary>
        private void AddTeamGlowDisc(Transform ringTr, int team)
        {
            if (!TeamColors.IsTeam(team) || ringTr == null) return;
            string tex = team == 0 ? "CR.png" : team == 1 ? "CG.png" : "CB.png";
            var t = SdoExtracted.LoadTextureRawLinear(Path.Combine(SdoExtracted.Root, "3DEFT", "GENERIC", "MAP_G"), tex);
            if (t == null) { Debug.LogWarning("[ring] 隊伍光暈貼圖載不到:" + tex); return; }

            var go = new GameObject("TeamGlow");
            go.transform.SetParent(ringTr, false);              // 吃星環的縮放/位置/自轉
            go.transform.localScale = Vector3.one * teamGlowScale;
            go.AddComponent<MeshFilter>().mesh = FlatQuadMesh();
            var mr = go.AddComponent<MeshRenderer>();
            var m = _addMat != null ? new Material(_addMat) : new Material(Shader.Find("Sprites/Default"));
            m.mainTexture = t;
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 1f));   // 顏色來自貼圖,這裡只給中性亮度
            mr.sharedMaterial = m;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
        }

        private static Mesh _flatQuad;
        /// <summary>邊長 1、位於 XY 平面、中心在原點的 quad —— 與星環環帶同一個平面(父物件已轉成平躺)。</summary>
        private static Mesh FlatQuadMesh()
        {
            if (_flatQuad != null) return _flatQuad;
            _flatQuad = new Mesh
            {
                name = "TeamGlowQuad",
                vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                                   new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) },
                uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) },
                triangles = new[] { 0, 2, 1, 0, 3, 2 },
            };
            _flatQuad.RecalculateBounds();
            return _flatQuad;
        }

        private void ApplyRingDebug()
        {
            float tb = Mathf.Clamp01(ringBrightness * 0.5f);
            for (int i = 0; i < _rings.Count; i++)
            {
                var r = _rings[i];
                if (r.Tr != null) r.Tr.localScale = Vector3.one * ringOuterRadius;   // 子物件(隊伍光暈)跟著縮放
                if (r.Mat != null && r.Mat.HasProperty("_TintColor"))
                    r.Mat.SetColor("_TintColor", new Color(tb, tb, tb, 1f));   // 星環永遠是白的(隊伍色在底下那圈光暈)
                if (r.Ring != null) r.Ring.SpinDegPerSec = ringSpinDeg;
            }
        }

        // filled white 5-point star with a faint halo, on black -> additive reads as a crisp star (matches the SDO floor ring)
        private static Sprite MakeStar5Sprite()
        {
            const int S = 64, SS = 2; var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            var px = new Color32[S * S]; float c = (S - 1) / 2f;
            const int P = 5; const float Ro = 0.94f, Ri = 0.40f;
            var vx = new float[2 * P]; var vy = new float[2 * P];
            for (int k = 0; k < 2 * P; k++)
            {
                float ang = -Mathf.PI / 2f + k * Mathf.PI / P;   // first point straight up
                float rr = (k % 2 == 0) ? Ro : Ri;
                vx[k] = Mathf.Cos(ang) * rr; vy[k] = Mathf.Sin(ang) * rr;
            }
            for (int y = 0; y < S; y++) for (int x = 0; x < S; x++)
            {
                float cover = 0f, glow = 0f;
                for (int sy = 0; sy < SS; sy++) for (int sx = 0; sx < SS; sx++)   // supersample for smooth edges
                {
                    float fx = ((x + (sx + 0.5f) / SS) - c) / (c + 0.5f);
                    float fy = ((y + (sy + 0.5f) / SS) - c) / (c + 0.5f);
                    if (PointInPoly(fx, fy, vx, vy)) cover += 1f;
                    glow += Mathf.Clamp01(1f - Mathf.Sqrt(fx * fx + fy * fy) * 1.7f);
                }
                cover /= SS * SS; glow = glow / (SS * SS) * 0.3f;
                byte b = (byte)(Mathf.Clamp01(cover + glow * (1f - cover)) * 255f);
                px[y * S + x] = new Color32(b, b, b, 255);
            }
            tex.SetPixels32(px); tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 1f);
        }

        private static bool PointInPoly(float x, float y, float[] vx, float[] vy)
        {
            bool inside = false; int nv = vx.Length;
            for (int i = 0, j = nv - 1; i < nv; j = i++)
                if (((vy[i] > y) != (vy[j] > y)) && (x < (vx[j] - vx[i]) * (y - vy[i]) / (vy[j] - vy[i]) + vx[i]))
                    inside = !inside;
            return inside;
        }

        // procedural 4-point sparkle (bright core + thin diagonal glints) on black -> additive reads as a star
        private static Sprite MakeStarSprite()
        {
            const int S = 32; var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            var px = new Color32[S * S]; float c = (S - 1) / 2f;
            for (int y = 0; y < S; y++) for (int x = 0; x < S; x++)
            {
                float dx = (x - c) / c, dy = (y - c) / c;
                float core = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy) * 1.8f);
                float spike = Mathf.Clamp01(1f - Mathf.Abs(dx) * 9f) * Mathf.Clamp01(1f - Mathf.Abs(dy) * 1.5f)
                            + Mathf.Clamp01(1f - Mathf.Abs(dy) * 9f) * Mathf.Clamp01(1f - Mathf.Abs(dx) * 1.5f);
                float v = Mathf.Clamp01(core * core + spike * 0.6f);
                byte b = (byte)(v * 255);
                px[y * S + x] = new Color32(b, b, b, 255);   // additive: brightness = the star, black = transparent
            }
            tex.SetPixels32(px); tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 1f);
        }

        // Original hand glow (decomp FUN_004a6e10 / FUN_004c2130): a WORLD-SPACE ribbon — NOT a camera-facing
        // TrailRenderer. Each cross-section is built from the live bone world positions: inner = Hand,
        // outer = 2*Finger0 - Hand, so the band has a real palm WIDTH that thins/widens as the hand rotates and
        // "comes out of the palm". We anchor BOTH bones (so HandRibbon reads their world positions each frame),
        // then HandRibbon sweeps a fading mesh. Width is derived live (no fixed value); gold verts on an additive
        // material. Lifetime/width are tunable (F4). See HandRibbon.cs.
        private void CreateHandTrail(Transform parent, SdoAvatar avatar, string handBone, string fingerBone, Color col)
        {
            int hi = avatar.BoneIndex(handBone); if (hi < 0) return;
            int fi = avatar.BoneIndex(fingerBone); if (fi < 0) { Debug.LogWarning($"[handtrail] no {fingerBone}; skipping ribbon"); return; }

            // anchors track the two bone world positions every Pose (scene scale 1 -> positions are world units)
            var handGo = new GameObject("HandAnchor_" + handBone);
            var fingerGo = new GameObject("FingerAnchor_" + fingerBone);
            if (use3dCamera) { handGo.layer = SceneLayer; fingerGo.layer = SceneLayer; }
            avatar.AddAnchor(hi, handGo.transform);
            avatar.AddAnchor(fi, fingerGo.transform);

            var go = new GameObject("HandRibbon_" + handBone);
            if (use3dCamera) go.layer = SceneLayer;
            var rib = go.AddComponent<HandRibbon>();          // RequireComponent adds MeshFilter + MeshRenderer
            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                var mat = _addMat != null ? new Material(_addMat) : new Material(Shader.Find("Sprites/Default"));
                if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", Color.white);   // full gold (the _addMat default 0.5 grey would dim it)
                mr.sharedMaterial = mat;
                // MUST NOT be negative. A negative sortingOrder pulls this additive (ZWrite Off) ribbon to the FRONT
                // of the scene draw order — ahead of the background scene geometry (the SCN0009 palace shell / baked
                // 背景人物 / GUATAN 掛毯), all at the default sortingOrder 0. Whatever then draws after it — opaque
                // geometry (sortingOrder outranks the material renderQueue, so -4 jumped it ahead of the opaque pass)
                // or an alpha-blended subset in the same transparent pass — paints over the ribbon's pixels, so the
                // hand glow VANISHED wherever it crossed a background figure, even though the hand is NEARER the camera
                // (the ribbon writes no depth, so its nearness alone can't keep it on top). At 0 the ribbon sorts
                // naturally with the scene (sortingOrder tie → renderQueue then distance): its Transparent queue /
                // nearer distance land it ON TOP of the background, while ZTest LEqual still clips it when the hand
                // genuinely passes behind solid geometry. Behind-the-HUD is unaffected — the HUD is a separate camera
                // composited over the scene RenderTexture. (Reported: SCN0009 手部光條被背景人物遮擋.)
                mr.sortingOrder = 0;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
            }
            rib.hand = handGo.transform; rib.finger = fingerGo.transform;
            // 上面那兩個錨點是 SDO 骨架的手。MMD 顯示開著時畫面上的手是 MMD 身體的手,兩者手臂長度不一樣 → 光條會
            // 在手掌外面浮一截。每幀問一次現在該掛誰(見 MmdAvatarSwap.HandSourceFor);沒有 MMD 身體就用上面的錨點。
            rib.Source = MmdAvatarSwap.HandSourceFor(avatar, handBone.Contains("_L_"));
            rib.color = col; rib.life = handTrailTime; rib.widthMul = handTrailWidth;
            _handTrails.Add(rib);
        }

        // ---- head emoji cut-ins (UI/PLAYINGEXP) -------------------------------------------------------------------
        private static readonly string PlayingExpDir = Path.Combine(SdoExtracted.Root, "UI", "PLAYINGEXP");

        // The frames were AUTHORED 64×64; the shipped PNGs may be an hq3x upscale (192×192, tools/upscale_playingexp.py).
        // LoadImageAtDesignWidth pins pixelsPerUnit to tex.width/64, so a 192px frame draws at the SAME world size as
        // the 64px original — only sharper. Never hard-code 1 here or an upscaled set would pop out 3× too big.
        private const int EmojiDesignPx = 64;

        // Load a <prefix>NNN.PNG sequence (000..count-1) as sprites. Cut-ins hold each frame 50ms and last ~4s, so the
        // short sequence loops (PlayingEmoji does the looping); we just load the frames once here.
        // bleed:true dilates the transparent-WHITE matte — these frames store a (255,255,255) matte with HARD binary
        // alpha, so without it bilinear filtering blends each glyph edge straight into white = the "白邊" halo.
        private static Sprite[] LoadEmojiSeq(string prefix, int count)
        {
            var arr = new List<Sprite>(count);
            for (int i = 0; i < count; i++)
            {
                var s = SdoExtracted.LoadImageAtDesignWidth(PlayingExpDir, $"{prefix}{i:D3}.PNG", EmojiDesignPx,
                                                            bleed: true, mip: true);
                if (s != null) arr.Add(s);
            }
            return arr.Count > 0 ? arr.ToArray() : null;
        }

        private void LoadEmojiArt()
        {
            _emHH = LoadEmojiSeq("HH", 7);       // 50 combo
            _emSHSH = LoadEmojiSeq("SHSH", 16);  // 150 combo
            _emJRKL = LoadEmojiSeq("JRKL", 8);   // 350 combo
            _emKJ = LoadEmojiSeq("KJ", 14);      // 550 combo
            _emHE = LoadEmojiSeq("HE", 8);       // 800 combo
            _emH = LoadEmojiSeq("H", 10);        // 10 consecutive bad/miss
            _emY = LoadEmojiSeq("Y", 4);         // 30 consecutive bad/miss
            _emJS = LoadEmojiSeq("JS", 6);       // 50 consecutive bad/miss
            _emGTH = LoadEmojiSeq("GTH", 8);     // cumulative 100 misses (was low-HP; low HP now only plays VOICE_0012)
        }

        // Build the emoji billboard. It anchors to the dancer's formation SLOT in world space (the dance-spot the
        // dancer stands on) rather than the bobbing skeleton, so it's stable while dancing and follows smoothly if a
        // formation later relocates the dancer to a new slot. PlayingEmoji rotates it to face the camera each frame.
        private void CreateHeadEmoji(SdoAvatar avatar)
        {
            var go = new GameObject("HeadEmoji");
            if (use3dCamera) go.layer = SceneLayer;
            var sr = go.AddComponent<SpriteRenderer>();   // default Sprites/Default material = alpha blend (faithful)
            sr.sortingOrder = 50;                          // above the ground ring / bursts
            sr.enabled = false;
            var em = go.AddComponent<PlayingEmoji>();
            em.sr = sr;
            // current slot world coordinate: the dancer's root (placed on its dance-spot; future formations move it).
            em.SlotGetter = () => _avatarRoot != null ? _avatarRoot.position : _danceSpot;
            em.CamGetter = () => _sceneCam != null ? _sceneCam : _cam;
            _emoji = em;
        }

        // Map an EmojiKind to its loaded PNG sequence.
        private Sprite[] FramesFor(EmojiKind k)
        {
            switch (k)
            {
                case EmojiKind.HH: return _emHH;
                case EmojiKind.SHSH: return _emSHSH;
                case EmojiKind.JRKL: return _emJRKL;
                case EmojiKind.KJ: return _emKJ;
                case EmojiKind.HE: return _emHE;
                case EmojiKind.H: return _emH;
                case EmojiKind.Y: return _emY;
                case EmojiKind.JS: return _emJS;
                case EmojiKind.GTH: return _emGTH;
                default: return null;
            }
        }

        // Per-emoji loop count (how many times the short sequence repeats before it stops).
        private static int EmojiLoops(EmojiKind k)
        {
            switch (k)
            {
                case EmojiKind.HH: return 3;
                case EmojiKind.SHSH: return 1;
                case EmojiKind.JRKL: return 3;
                case EmojiKind.KJ: return 2;
                case EmojiKind.HE: return 3;
                case EmojiKind.H: return 2;
                case EmojiKind.Y: return 5;
                case EmojiKind.JS: return 3;   // (not specified by spec — default)
                case EmojiKind.GTH: return 3;
                default: return 1;
            }
        }

        // Single emoji slot: the latest trigger replaces whatever is playing (restarts the cut-in).
        private void ShowEmoji(EmojiKind kind)
        {
            if (kind == EmojiKind.None || _emoji == null) return;
            var frames = FramesFor(kind);
            if (frames != null && frames.Length > 0) _emoji.Play(frames, EmojiLoops(kind));
        }

        // Combo milestones / consecutive-miss cut-ins — pure decision in EmojiTriggers (unit-tested).
        private void UpdateEmojiOnJudge(Judgment j) => ShowEmoji(_emojiState.OnJudge(j, _score.Combo));

        // Read every distinct choreography clip up front, while the loading cover is still up, so SdoAvatar.LateUpdate
        // never hits the disk mid-song. A generated external dance pulls from a large random pool of wdanceNNNN.mot
        // clips, and each one that first appeared during play used to cost a File.ReadAllBytes + MOT parse on the main
        // thread — a periodic hitch. ResolveMot caches even a missing clip, so play is guaranteed touch-free afterwards.
        private void PrewarmDpsMotions(DpsLoader dps)
        {
            if (dps == null || dps.Rows == null) return;
            var seen = new HashSet<string>();
            foreach (var row in dps.Rows)
                if (!string.IsNullOrEmpty(row.Mot) && seen.Add(row.Mot))
                    ResolveMot(row.Mot);   // populates _motCache under the exact (gendered) key LateUpdate will look up
        }

        // DPS row -> MotLoader, cached. The choreography clips live in AUMOTION/ (fall back to MOTION/). 每棵樹都先
        // AUMOTION 再 MOTION；樹的順序由 MotRoots() 決定 —— 歌包外掛樹（若有）先於 base 資料根。
        private MotLoader ResolveMot(string rawName) => ResolveMotFor(rawName, localPlayerMale);

        /// <summary>同 <see cref="ResolveMot"/>,但性別映射(W→M)照 <paramref name="male"/> 而不是本機玩家。
        /// 場上其他人的動作要走這條:本機是男的話,女生玩家的 WWIN0002 會被本機那條路換成 MWIN0002 —— 撈到
        /// 別人性別的 clip,套在女骨架上就是一團扭曲。快取的鍵是**映射後**的名字,所以兩種性別各自命中自己那份。</summary>
        private MotLoader ResolveMotFor(string rawName, bool male)
        {
            if (string.IsNullOrEmpty(rawName)) return null;
            string name = ResolveGenderedMotName(rawName, male);
            if (_motCache.TryGetValue(name, out var cached)) return cached;
            MotLoader m = null; string triedPath = null, why = null;

            // 歌曲資料夾最優先：dps 點名的 .mot 若就放在這首歌自己的資料夾（外部 osu/SM 歌 = 歌曲當下所在
            // 資料夾），直接用它 —— 先於 overlay 樹與 base 根。這讓使用者把自訂舞步 .mot 丟進歌資料夾即可覆蓋
            // 該片段（外部歌的 .dps 直接躺在歌資料夾、不在 DANCE/ 下，本來拿不到 overlay，此路補上）。先試
            // gendered 名（尊重男版），再試原始 dps 名（讓歌自帶的女版 .mot 對男玩家也生效）。
            m = TryLoadMotFromSongFolder(name, ref triedPath, ref why);
            if (m == null && !string.Equals(name, rawName, System.StringComparison.Ordinal))
                m = TryLoadMotFromSongFolder(rawName, ref triedPath, ref why);

            if (m == null)
            foreach (var root in MotRoots())
            {
                foreach (var dir in new[] { "AUMOTION", "MOTION" })
                {
                    var p = Path.Combine(root, dir, name);
                    if (!File.Exists(p)) continue;
                    triedPath = p;
                    try
                    {
                        var bytes = File.ReadAllBytes(p);
                        m = MotLoader.Load(bytes);
                        if (m == null) why = bytes.Length == 0 ? "empty file (0 bytes)" : "corrupt / not a valid MOT (bad header)";
                    }
                    catch (System.Exception e) { why = e.Message; }
                    if (m != null) break;
                }
                if (m != null) break;
            }
            if (m == null)
            {
                // This is the hole that hid the sdom5085 freeze: a DPS clip that's missing/empty/corrupt used to fail
                // silently, so the dancer just held the previous clip's last frame (looked frozen). Name it in the log.
                Debug.LogWarning($"[avatar] DPS motion unresolved: {name} — {(triedPath == null ? "not found in AUMOTION/ or MOTION/" : why)} (dancer holds the previous clip)");
                SdoLog.MissingAsset("mot", triedPath ?? name, triedPath == null ? "not found" : why);
            }
            _motCache[name] = m;   // cache even null to avoid re-probing missing files
            return m;
        }

        /// <summary>在這首歌自己的資料夾（<see cref="externalFolder"/>；官方歌為 ""）裡直接找 <paramref name="name"/>
        /// 這顆 .mot 並載入，找不到／載入失敗回 null。不分大小寫（<c>WDANCE0531.MOT</c> ↔ <c>wdance0531.mot</c>）。
        /// 命中時把路徑寫進 <paramref name="triedPath"/>，載入失敗把原因寫進 <paramref name="why"/>（供 ResolveMot 記錄）。</summary>
        private MotLoader TryLoadMotFromSongFolder(string name, ref string triedPath, ref string why)
        {
            if (string.IsNullOrEmpty(externalFolder) || string.IsNullOrEmpty(name)) return null;
            string folder = Path.IsPathRooted(externalFolder) ? externalFolder
                            : Path.Combine(SdoExtracted.Root, externalFolder);
            if (!Directory.Exists(folder)) return null;

            string hit = Path.Combine(folder, name);
            if (!File.Exists(hit))
                hit = MotionOverlay.MatchFileName(Directory.GetFiles(folder), name);   // 大小寫不同也命中
            if (string.IsNullOrEmpty(hit) || !File.Exists(hit)) return null;

            triedPath = hit;
            try
            {
                var bytes = File.ReadAllBytes(hit);
                var m = MotLoader.Load(bytes);
                if (m == null) why = bytes.Length == 0 ? "empty file (0 bytes)" : "corrupt / not a valid MOT (bad header)";
                return m;
            }
            catch (System.Exception e) { why = e.Message; return null; }
        }

        /// <summary>查動作片段的資料樹，依優先順序：這首歌的外掛包（若有，<see cref="_motOverrideRoot"/>）先，
        /// 再 base 資料根。歌包自帶的 .mot 因此能覆蓋／補足 base；base 沒有的（W_00xxxx.MOT）也找得到。</summary>
        private IEnumerable<string> MotRoots()
        {
            if (!string.IsNullOrEmpty(_motOverrideRoot)) yield return _motOverrideRoot;
            yield return SdoExtracted.Root;
        }

        private string ResolveGenderedMotName(string name, bool male)
        {
            if (!male) return name;
            string file = Path.GetFileName(name.Replace('\\', '/'));
            if (string.IsNullOrEmpty(file) || file[0] != 'W') return name;

            string maleName = "M" + file.Substring(1);
            foreach (var root in MotRoots())
                foreach (var dir in new[] { "AUMOTION", "MOTION" })
                    if (File.Exists(Path.Combine(root, dir, maleName))) return maleName;
            return name;
        }

        // resolve a material's .dds name to a file in the avatar dir (case-insensitive), load it
        private Texture2D ResolveDds(string dir, string ddsName) => ResolveDds(dir, ddsName, out _);
        // smooth overload: low-pass the DXT3 4-bit alpha to kill the "tree-ring" banding on a glow gradient
        // (SCN0022 ghost/searchlight). Their DDS has only ~9-12 alpha levels; see DdsLoader.SmoothAlpha.
        private Texture2D ResolveDds(string dir, string ddsName, DdsLoader.AlphaSmooth smooth)
            => ResolveDds(dir, ddsName, out _, out _, out _, smooth);

        // Resolve a mapobj texture by material name and report whether it carries real alpha (so the caller can
        // alpha-blend its "去背" cut-out instead of painting it opaque). Reads the file once for both.
        private Texture2D ResolveDds(string dir, string ddsName, out bool hasAlpha)
        {
            return ResolveDds(dir, ddsName, out hasAlpha, out _);
        }

        private Texture2D ResolveDds(string dir, string ddsName, out bool hasAlpha, out bool additiveGlow)
        {
            return ResolveDds(dir, ddsName, out hasAlpha, out additiveGlow, out _);
        }

        // hardCutout: the texture is a HARD cut-out (mostly-opaque body + real holes, e.g. the SCN0009 掛毯 GUATAN),
        // classified by alpha DISTRIBUTION exactly like SCENE.MSH materials. Such props must render depth-writing
        // (alpha-TEST/cutout, ZWrite On), NOT alpha-BLEND (ZWrite Off) — otherwise a moving two-sided cloth's own
        // back faces and the pillars/people behind it bleed THROUGH it ("穿模"). The official used alpha-test here.
        private Texture2D ResolveDds(string dir, string ddsName, out bool hasAlpha, out bool additiveGlow, out bool hardCutout, DdsLoader.AlphaSmooth smooth = DdsLoader.AlphaSmooth.None)
        {
            hasAlpha = false;
            additiveGlow = false;
            hardCutout = false;
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(ddsName)) return null;
            string name = Path.GetFileName(ddsName.Replace('\\', '/'));
            string direct = Path.Combine(dir, name);
            string hit = File.Exists(direct) ? direct : null;
            if (hit == null)
            {
                string stem = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
                foreach (var f in Directory.GetFiles(dir, "*.*"))
                    if (Path.GetExtension(f).ToLowerInvariant() == ".dds" && Path.GetFileNameWithoutExtension(f).ToLowerInvariant() == stem) { hit = f; break; }
            }
            if (hit == null) return null;
            try
            {
                var bytes = File.ReadAllBytes(hit);
                hasAlpha = DdsLoader.HasAlpha(bytes);
                additiveGlow = hasAlpha && DdsLoader.LooksLikeAdditiveGlow(bytes);
                hardCutout = hasAlpha && !additiveGlow && DdsLoader.GetSceneAlphaMode(bytes) == DdsAlphaMode.Cutout;
                // Alpha-blended cut-out props (e.g. SCN0026 背景汽車 — flat DXT3 billboards on a WHITE matte) bled a
                // white halo at the silhouette under straight alpha blending. Edge-bleed the decoded RGB so the
                // transparent matte carries the prop's own colour instead. Additive glows are excluded: their low-
                // alpha RGB IS the glow and must not be dilated. No-op on opaque textures, so it's safe by default.
                return DdsLoader.Load(bytes, bleedAlphaEdges: hasAlpha && !additiveGlow, smooth: smooth);
            }
            catch { return null; }
        }

        // the original stage/dance camera (CAMERA/1/CAM0000.CV) — extract its up-pitch (eye knee-height -> chest target)
        private float CvCameraPitchUp()
        {
            var cv = LoadAsset("CAMERA/1/CAM0000/000.CV", b => CvLoader.Load(b));
            if (cv != null && cv.Eye.Length > 0 && cv.Target.Length > 0)
            {
                Vector3 eye = cv.Eye[cv.Eye.Length / 2], tgt = cv.Target[0];
                Vector3 d = tgt - eye; float horiz = new Vector2(d.x, d.z).magnitude;
                if (horiz > 1e-3f) return Mathf.Clamp(Mathf.Atan2(d.y, horiz), 0f, 0.6f);
            }
            return 14f * Mathf.Deg2Rad;
        }

        private T LoadAsset<T>(string rel, System.Func<byte[], T> load) where T : class
        {
            if (string.IsNullOrEmpty(rel)) return null;
            var path = Path.Combine(SdoExtracted.Root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) { Debug.LogWarning("[avatar] missing " + rel); return null; }
            try { return load(File.ReadAllBytes(path)); }
            catch (System.Exception e) { Debug.LogWarning($"[avatar] load fail {rel}: {e.Message}"); return null; }
        }

        // ---------- loop ----------

        // ---- 整體遊戲流速 (StepMania music rate) ----
        // 一次改三件事,缺一就會音畫不同步:
        //   (1) Time.timeScale = rate → 音符/判定/舞者/特效/協程 全部跟著慢(譜面時鐘是 scaled time 驅動的);
        //   (2) AudioSource.pitch = rate → 音樂本身變速(連音高,同 RageSound SetPlaybackRate);
        //   (3) 重新錨定 dsp↔譜面時間(dspTime 是真實時間,不吃 timeScale)。不錨定的話譜面時間會當場跳掉。
        // 判定窗口刻意**不**跟著縮放(仍是譜面 ms)→ 越快越難,同 StepMania。
        private void SetGameRate(double rate)
        {
            rate = GameRate.Clamp(rate);
            if (Math.Abs(rate - _musicRate) < 1e-6) return;
            double dspNow = AudioSettings.dspTime;
            double chartSecNow = GameRate.ChartSecondsFromDsp(dspNow, _songStartDspTime, _musicRate, MusicCountInSec);

            _musicRate = rate; _timeScale = (float)rate;
            if (!_paused) Time.timeScale = _timeScale;
            ApplyClockOffset();   // 輸出延遲補償是「rate × L」→ 流速一變就要重算（見 ClockLatencyChartMs）

            _songStartDspTime = GameRate.AnchorForChartSeconds(dspNow, chartSecNow, rate, MusicCountInSec);
            if (_audio != null && _audio.clip != null)
            {
                _audio.pitch = _timeScale;
                if (dspNow < _songStartDspTime) _audio.SetScheduledStartTime(_songStartDspTime);   // 還在 lead-in/數拍:起播點也要重排
            }
            ResetScheduledTicks();   // 已排進音訊時鐘的打拍音是舊速度算的 → 全部作廢重排
            OnOsuPlaybackRateChanged((_paused ? _pauseChartSec : chartSecNow) * 1000.0);
        }

        // \ 暫停/恢復。音樂也要停 —— 只把 timeScale 歸零的話音樂會自顧自跑掉,恢復時整首歌就對不上了。
        private void SetPaused(bool paused)
        {
            if (paused == _paused) return;
            if (paused)
            {
                _pauseChartSec = GameRate.ChartSecondsFromDsp(AudioSettings.dspTime, _songStartDspTime, _musicRate, MusicCountInSec);
                if (_audio != null && _audio.clip != null) _audio.Pause();
                Time.timeScale = 0f;   // Time.timeAsDouble 隨之凍結 → 譜面時鐘自己就停了,不需另外存
                ResetScheduledTicks();
                OnOsuPlaybackPaused(_pauseChartSec * 1000.0);
            }
            else
            {
                // 恢復也必須是**取樣級**的：UnPause() 跟 Play() 一樣要等下一個混音回呼才真的出聲（最多一個 DSP
                // buffer ≈ 11~21ms），而打拍音/判定都掛在 dsp 錨點上 → 每暫停一次，音樂就慢掉不到一個 buffer。
                // 改成排程起播：起播點 startDsp、dsp 錨點、譜面時鐘的 wall 基準三者錨在同一刻，餘裕本身完全消掉。
                double lead = AudioScheduleLeadSec();
                double startDsp = AudioSettings.dspTime + lead;
                _songStartDspTime = GameRate.AnchorForChartSeconds(startDsp, _pauseChartSec, _musicRate, MusicCountInSec);
                if (_audio != null && _audio.clip != null)
                {
                    double clipSec = _pauseChartSec - MusicCountInSec;
                    _audio.Stop();
                    _audio.pitch = _timeScale;
                    if (clipSec < 0.0) { _audio.timeSamples = 0; _audio.PlayScheduled(_songStartDspTime); }   // 還在無聲數拍裡
                    else if (clipSec < _audio.clip.length)
                    {
                        _audio.timeSamples = Math.Min(_audio.clip.samples - 1,
                            Math.Max(0, (int)Math.Round(clipSec * _audio.clip.frequency)));   // 整數取樣（clip 是 DecompressOnLoad）
                        _audio.PlayScheduled(startDsp);
                    }
                }
                Time.timeScale = _timeScale;
                // 譜面時鐘同樣錨到 startDsp：在音樂真正出聲的那一刻，譜面時間剛好等於暫停時的位置。
                // （timeAsDouble 吃 timeScale，所以餘裕要乘流速。）
                _clockStart = Time.timeAsDouble - (_pauseChartSec - lead * _musicRate);
                _clock.Reset();
                OnOsuPlaybackResumed(_pauseChartSec * 1000.0, startDsp);
            }
            _paused = paused;
        }

        // 舊名保留(F4 面板/觀察模式在用):現在等同「改流速」,音樂會跟著變 —— 以前只動 timeScale,音樂照原速播,是會走音的。
        private void SetTimeScale(float s) => SetGameRate(s);

        // Stage backdrop RT upkeep: re-allocate it when the window settles at a new size (the same RenderTexture instance
        // is kept, so _backdropMat's texture reference stays wired), and pin the official 4:3 projection every frame.
        // The RT follows the WINDOW shape now (RtSizing), so without the pin the camera would infer its aspect from the
        // RT and a wide window would widen the field of view — the decompiled Camera_ctor aspect is exactly 4/3.
        private void MaintainSceneRt()
        {
            if (_sceneCam != null) _sceneCam.aspect = RtSizing.LogicalW / RtSizing.LogicalH;
            if (_sceneRT == null) return;
            if (!_sceneRtTrack.Tick(Screen.width, Screen.height, Time.unscaledTime)) return;
            RtSizing.SlotRtSize(Screen.width, Screen.height, RtSizing.LogicalW, RtSizing.LogicalH,
                                sceneSupersample, out int w, out int h);
            RtSizing.Apply(_sceneRT, w, h);
        }

        private void Update()
        {
            if (!_sceneBootDone) return;   // stage is still building behind the loading screen — nothing to drive yet
            MaintainSceneRt();
            _musicName?.Tick();            // 視窗/全螢幕一變就重新以實體 px 光柵化歌名（否則取樣不對 → 殘影/糊）
            // LV/時間值同一套光柵；真的換了尺寸就重量「: 秒」欄寬(字寬會微調)，好把總長欄重新釘回原位。
            if (_hudTextRaster.Tick()) _timeMeasure = 0;
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f), 0.1f);   // smoothed debug FPS
            TickDancerPerf();   // SDO_DANCERS 開著時每 2 秒印一行幀時間(M8 的量測依據,見 ScreenGameplay.Dancers.cs)
            TickDancerSlots();  // 多人:每幀把舞者往該站的格子滑一步,並讓相機錨點跟著第一名
            if (_fpsText) _fpsText.text = "FPS " + Mathf.RoundToInt(_fps);
            // 遊戲中的聊天框:自己吃 Tab/滑鼠/文字輸入。**要在所有熱鍵之前** —— 這一幀它可能剛進入打字模式,
            // 下面每一段都得看 ChatTyping 決定放不放行(打字時整片鍵盤都是文字,不是遊戲鍵)。
            _chat?.Tick();
            bool chatTyping = ChatTyping;
            // 測試用（已停用）：F4 開/關除錯滑桿面板
            // if (Input.GetKeyDown(KeyCode.F4)) _showDebugUI = !_showDebugUI;        // toggle the tuning sliders
            // 隊形假人預覽(←→ 切隊形、↑↓ 改人數)。F10 是刻意選的：F2/F3 已被房間畫面與相機切換佔用。
            if (!chatTyping && Input.GetKeyDown(KeyCode.F10)) ToggleFormationPreview();
            // 以下功能鍵的鍵位都能在 DATA/PROFILE/keymaps.ini 的 [Hotkeys] 改（預設＝括號裡那顆），見 Sdo.Settings.KeyMap。
            // Auto（自動）模式開關(預設 F8) — 開啟後自動打擊所有音符（原測試用 DebugMeshOnly 已停用）。s_autoPlay = 跨歌延續。
            if (!chatTyping && KeyMap.Down(Hotkey.AutoPlay)) { autoPlay = !autoPlay; s_autoPlay = autoPlay; PlaySe("SE_0001"); Debug.Log("[dbg] autoPlay=" + autoPlay); }   // 按下發出 SE_0001
            // 打拍音(預設 F7；StepMania assist tick)— 每個音符響一聲 click，方便對拍。s_assistTick = 跨歌延續（不存檔）。
            if (!chatTyping && KeyMap.Down(Hotkey.AssistTick))
            {
                assistTick = !assistTick; s_assistTick = assistTick;
                if (assistTick) { _tick.Rewind(_nowMs); PlayTickOnce(); }   // 從當下的音符開始響（不補播過去的）
                Debug.Log("[dbg] Assist Tick is " + (assistTick ? "ON" : "OFF"));
            }
            // DEBUG（暫時停用）：切換 ShowTime（氣條）模式。註解掉避免誤觸；F7 現在給打拍音，要測時請自己挑個沒用到的鍵。
            // { showtimeMode = !showtimeMode; SetEnergyHudVisible(showtimeMode); SetTrackVisible(_trackVisible); Debug.Log("[showtime] mode=" + showtimeMode); }   // SetTrackVisible refreshes HP-bar visibility for the new mode
            // (已移除) 測試用 combo 爆發按鍵 B / 1-5 / 0 —— 會在遊玩時誤觸,清掉。要觀察爆發時自己臨時加回。
            // 測試用（已停用）：F5 直接跳到結算（Shift+F5 強制 GAME OVER）
            // if (Input.GetKeyDown(KeyCode.F5) && _started && !_ended)
            // {
            //     // DEBUG F5: cut the song short → jump to the result sequence. Shift+F5 forces HP-out → the GAME OVER
            //     // death flow (Frameextrude + 死亡字幕 + no win/lose pose), for verifying it without grinding HP to zero.
            //     if (!showtimeMode && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) _failed = true;
            //     _ended = true; EnterResult();
            // }
            // 加速 note(預設 F5，下一速度檔)／減速 note(預設 F6，上一速度檔)— 跟房間「速度」功能一樣，依速度檔位表步進，按下播 SE_0001
            if (!chatTyping && KeyMap.Down(Hotkey.SpeedUp)) StepScrollSpeed(+1);
            if (!chatTyping && KeyMap.Down(Hotkey.SpeedDown)) StepScrollSpeed(-1);
            // 流速（= StepMania music rate）：音樂、音符、舞者、特效一起變速。[ 慢一格 / ] 快一格（0.05 步進，同 SM 的
            // 兩位小數 rate）、\ 暫停/恢復（音樂也停）、= 回 1×。
            // 正式遊玩已停用（會誤觸）；只留給譜面編輯器（它的 HUD 就寫著這幾個鍵）。
            // 編輯器模式的暫停/變速要走 Editor* 版本（會重新錨定 dsp↔譜面時間；SetPaused 的恢復路徑假設音源是 Pause 過的，
            // 但編輯器 seek 是 Stop→Play，直接用會恢復不了聲音）。
            if (editorMode && !chatTyping)
            {
                if (Input.GetKeyDown(KeyCode.LeftBracket)) EditorSetRate(GameRate.Step(_musicRate, -1));
                if (Input.GetKeyDown(KeyCode.RightBracket)) EditorSetRate(GameRate.Step(_musicRate, +1));
                if (Input.GetKeyDown(KeyCode.Backslash)) EditorSetPaused(!_paused);
                if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadEquals)) EditorSetRate(GameRate.Normal);
            }
            ApplyRingDebug();   // live floor-ring spread/brightness/spin from the F4 sliders
            TickAmbient();      // intermittent per-scene ambience (sea/stadium/underwater/garden)
            UpdateFlyHover();   // 飛行翅膀:整場常駐懸浮(待機/跳舞/定格同高)
            if (_board) { if (!Mathf.Approximately(boardAlpha, _boardAlphaApplied)) ApplyBoardAlpha(); _board.flipY = _scrollSign < 0; SdoLayout.PlaceTopLeft(_board, PX(boardX), 0f, 10f); }   // live board opacity + X nudge + 向下上下翻 (PX = 面板位置 左/中)
            // 測試用（已停用）：F9 開流速測試面板；Shift+F9 舞台背景上下翻轉的保險開關（RenderTexture 的 V 方向已依
            // graphicsUVStartsAtTop 自動判斷，但萬一這台機器仍然上下顛倒就用它救）。遊玩時會誤觸，需要時再解開。
            // if (Input.GetKeyDown(KeyCode.F9))
            // {
            //     if ((Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) && _backdropMat != null)
            //     {
            //         _backdropFlip = !_backdropFlip;
            //         _backdropMat.mainTextureScale = new Vector2(1f, _backdropFlip ? -1f : 1f);
            //         _backdropMat.mainTextureOffset = new Vector2(0f, _backdropFlip ? 1f : 0f);
            //     }
            //     else _showRateUI = !_showRateUI;   // F9: 流速測試面板
            // }
            if (_sceneCam != null && use3dCamera && !avatarDebug && _camReady)
            {
                // 換鏡頭(預設 F2；decompiled gameplay cmd 0x3c): camMode++ over 0..5, past 5 wraps to -1 = the auto-director.
                if (!chatTyping && KeyMap.Down(Hotkey.Camera)) CycleCamMode();
                Vector3 eye, tgt, up = Vector3.up;   // up = the .cv per-frame up vector (Camera_Update's LookAtLH 4th arg); non-vertical => roll/tilt
                if (_camMode < 0 && _dirCv != null && _dirCv.Length > 0)
                {
                    // Hold the director pinned to the START of shot 0 while the loading screen is still up — the camera
                    // must only begin its move once we're actually "in" the game (revealed), not run underneath the cover.
                    // Re-stamping _dirShotStart every hidden frame keeps elapsed ≈ 0, so it starts fresh from shot 0 at reveal.
                    if (!_bootRevealed) { _dirShot = 0; _dirShotStart = Time.time; }
                    // Start the opening crane from its camIntroSkipSec frame (cut the first second of shot 0). Applied ONCE,
                    // the first revealed frame, by shifting the shot-start back so elapsed begins at camIntroSkipSec.
                    else if (!_camIntroSkipped && camIntroSkipSec > 0f) { _dirShotStart -= camIntroSkipSec; _camIntroSkipped = true; }
                    // AUTO-DIRECTOR: animate the current shot's .cv over its durationMs, then auto-cut to the next.
                    float durSec = Mathf.Max(0.1f, _dirDurMs[_dirShot] / 1000f);
                    float el = Time.time - _dirShotStart;
                    if (el >= durSec) { _dirShot = (_dirShot + 1) % _dirCv.Length; _dirShotStart = Time.time; el = 0f; durSec = Mathf.Max(0.1f, _dirDurMs[_dirShot] / 1000f); }
                    _dirCv[_dirShot].Sample(el / durSec, out eye, out tgt, out up);     // VERBATIM .cv eye/target/UP (Camera_Update)
                    // Camera_GetEyePos/GetTargetPos: add the dance-spot anchor ONLY for relative (:1) shots;
                    // absolute (:0) shots (e.g. the opening crane) use raw .cv world coords. Solo spot = 0 either way.
                    // The anchor is a POSITION offset — it never touches the up vector (a direction).
                    // 🔴 用 _camAnchorSpot 而不是 _danceSpot。這兩個在單人時是同一個值(都是原點),
                    // 但多人時**相機要跟著第一名**(官方:slot 0 是中央前排 = 鏡頭錨點,而第一名會滑進去),
                    // 不是跟著本機。_danceSpot 的語意仍然是「本機舞者站哪」——
                    // 它另外還有 6 個 read site 都是那個意思,改它的語意會一起弄壞那些。
                    if (!_dirAbs[_dirShot]) { eye += _camAnchorSpot; tgt += _camAnchorSpot; }
                }
                else
                {
                    // FIXED camera: the exact decompiled static eye/target (DAT_005824f0/0x582538), absolute world.
                    // These are built via Camera_ctor_default (up = world-up), so they stay level — keep up = Vector3.up.
                    int fi = Mathf.Clamp(_camMode, 0, FixedEye.Length - 1);
                    eye = FixedEye[fi]; tgt = FixedTgt[fi];
                }
                _sceneCam.transform.position = eye;
                _sceneCam.transform.LookAt(tgt, up);   // up carries the .cv roll → the auto-director's tilted-map shots
            }
            if (!_started) return;
            // Note timeline = the wall clock (Time.timeAsDouble - _clockStart), but re-locked every frame onto the
            // TRUE audio playback position so the notes never drift off the music over a long song (crystal drift)
            // or after an audio-buffer stall. GameplayClock advances smoothly on the wall delta and slews back onto
            // the audio; when the audio isn't playing yet (lead-in / silent count-in) AudioChartSeconds() is null and
            // it free-runs on the wall clock. CurrentMs (offset-corrected, smoothed) is what drives everything below.
            double wallSec = Time.timeAsDouble - _clockStart;
            _clock.Tick(wallSec, AudioChartSeconds());
            double now = _clock.CurrentMs;
            _nowMs = now;
            if (showtimeMode) UpdateBanner();   // song-end SHOW TIME flourish must tick post-song too (UpdateHud stops when _ended)
            TickAssist(now);   // F7 打拍音：把接下來 250ms 內的 tick 排進音訊時鐘（關閉時只推游標）
            // 譜面編輯器：只把音符捲過去 —— 不扣血、不計分、不結算（時間由 ChartEditorScreen 自由 seek）。
            TickOsuSampleEvents(now);
            // 判定照跑（含一般編譜模式）：只回報誤差給 osu 式誤差條，讓你邊看譜邊跟著打、即時看出偏早/偏晚。
            if (editorMode)
            {
                EditorJudgeTick(now);
                ScrollNotes(now);
                UpdateFx(); UpdateClickFlash();   // 爆發/受擊閃光也要有人推幀＋回收：少了這行，每打中一下就永久留一張 frame 0 疊上去（additive → 越疊越白）
                EditorTick(now);
                return;
            }
            if (_ended) { ResultTick(); UpdateFx(); return; }   // post-song: finish sequence drives avatar/camera/panel; gameplay frozen (FX still tick out)
            ScrollNotes(now);
            bool showtimeWasActive = _showtime.Active;
            double showtimeEndBeforeTick = _showtime.UntilMs;
            TickShowtime(now);   // ShowTime: SPACE release + window expiry (before judging so this frame already auto-hits)
            TickOsuSampleEvents(now);   // re-check after a ShowTime transition so this frame's note uses the DSP queue
            // 旁觀:不吃鍵盤(需求 10)。這一條不是「反正沒有音符所以無害」—— HandleInput 會亮受擊閃光,
            // 旁觀者按到方向鍵就會在沒有音符板的畫面上閃出四條光。
            bool manualPlay = !_failed && !_showtime.Active && !autoPlay && !spectatorMode;
            if (!_failed && !spectatorMode)
            {
                if (_showtime.Active) AutoPlay(now, showtime: true);   // ShowTime window: force PERFECT, ignore manual input
                else if (showtimeWasActive)
                {
                    // The frame can cross UntilMs before judging runs. Finish every head strictly inside the old
                    // window so a DSP-scheduled keysound can never exist without its matching auto-PERFECT.
                    AutoPlay(showtimeEndBeforeTick - 0.0001, showtime: true);
                    if (autoPlay) { AutoPlay(now); _stJustEnded = false; }
                    else { HandleInput(now); AutoMiss(now); }
                }
                else if (autoPlay) { AutoPlay(now); _stJustEnded = false; }   // dev auto-play never handoffs → drop any pending seam flag
                else { HandleInput(now); AutoMiss(now); }
            }
            TickBombs(now, detonate: manualPlay);   // 炸彈:手動打時踩到(該軌按著)引爆;F8自動/ShowTime自動避雷,只安全流過
            if (!spectatorMode) UpdateDanceGate(now);   // dancer dance/stop decision (after judging, so this frame's misses count)
            TickRemoteGates(now);      // 遠端舞者各自的跳/停(從分數流推導,與本機同一個規則函式)
            TickRemotePresence(now);   // 死了 / 中途離場的遠端玩家當場停舞(分數流推不出這兩件事)
            RecordGate(now);        // log gate transitions for the result-screen background replay
            RecordLocalScoreSample(NetClockMs);   // 右側名單要把自己的分數倒帶到遠端那一刻(見 RosterLocalScore)
            // 🔴 長條「按住期間」不再另外放 burst。以前這裡會在**判定長條頭的同一個 Update** 裡再生一發循環用的
            // burst，於是頭部等於連放兩發（一發是 ApplyEvent 的 tap burst），比一般 tap 多閃一下；長條若短於一輪
            // 動畫（≈12 幀 × BurstSecPerFrame ≈ 0.36s）就剛好只多閃那一次，使用者回報的就是它。
            // 現在:頭部＝一般 tap 的發光，結尾放開＝tap burst + 官方 LnEnd 爆發 (EndHold)。按住期間的持續回饋
            // 由官方本來就有的軌道閃光條負責 (TriggerClickFlash/UpdateClickFlash，decompiled 00498bd0)。
            UpdateClickFlash();
            UpdateFx(); UpdateHud();
            // ShowTime mode has NO HP failure — only the 集氣 (energy) gauge matters. The song must never GAME OVER on
            // HP-out; it only ends naturally at the song's end (below).
            // HP-out (一次性 latch _hpDead):
            //   • 一般模式:立刻 _failed —— 判定/舞蹈凍結,馬上切進 GAME OVER 結算。
            //   • 完奏模式(playFullSong):歌不切斷,整首照打到曲末 —— 但「死了就是死了」:
            //       (1) 分數就地凍結 (ScoreProcessor.FreezeScore) —— 之後打再好都不再加分;
            //       (2) P/C/B/M 判定統計、combo、特效照常繼續累計(結算的判定數是整首的);
            //       (3) HP 鎖在地板 (HealthProcessor lockOnDeath),不會被後面的 combo 補回來;
            //       (4) 舞者停舞 —— 血用完就不能繼續跳舞,回待機站著到曲末(DanceEnabled 看 _hpDead);
            //       (5) 曲末結算一樣算 GAME OVER / 輸(見 EnterResult 的 _gameOver 與評分 F)。
            if (!showtimeMode && !_hpDead && _health != null && _health.IsFailed)
            {
                _hpDead = true;
                if (playFullSong) _score?.FreezeScore();
                else _failed = true;
            }
            // 結束判定:等「音樂播完」再 +1 秒才進結算動作,但加 10 秒上限避免長尾奏/長音檔等太久。
            //   notesEndMs = 最後一顆音符;musicEndMs = 音檔播完的譜面時間 (MusicCountInSec + clip.length)×1000
            //   (clip 起播被 offset 跳過一段不影響終點,終點恆為 clip.length)。
            //   • 音檔在最後音符後 10 秒內會結束 → 以「音檔結束」為基準(等音樂放完)。
            //   • 音檔 10 秒內不會結束(尾奏過長/音檔比譜面長很多) → 以「最後音符」為基準,不苦等尾奏。
            //   • 沒有音檔(觀察/爆發模式)或音檔比音符短 → 一律用最後音符。
            //   兩種基準最後都再 +1 秒緩衝才 EnterResult(音樂/最後音符播完後的定格前置)。
            double notesEndMs = _totalMs;
            double baseEndMs = notesEndMs;
            // A virtual keysound map has no backing clip; its automatic samples are the song. Honour their final
            // audible tail under the same 10-second outro cap used for ordinary backing audio.
            if (_osuTimelineEndMs > notesEndMs && _osuTimelineEndMs <= notesEndMs + 10000.0) baseEndMs = _osuTimelineEndMs;
            if (_audio != null && _audio.clip != null)
            {
                double musicEndMs = (MusicCountInSec + _audio.clip.length) * 1000.0;
                if (musicEndMs > notesEndMs && musicEndMs <= notesEndMs + 10000.0)
                    baseEndMs = Math.Max(baseEndMs, musicEndMs);
            }
            if (!_ended && (_failed || now > baseEndMs + 1000)) { _ended = true; EnterResult(); }
        }

        // Song finished (or HP-out): freeze gameplay, hide the note board, play the win/lose 定格 pose on the
        // winning dancer, and fire the FINISHED burst on the winner. Mirrors decompiled FinishSequenceTick phase4
        // (021_gameplay:2674) — the winner (top score) plays cat5, everyone else cat4.
        private void EnterResult()
        {
            if (_audio) _audio.Stop();                        // stop the song (natural end already silent; matters for F5 mid-song cut)
            if (showtimeMode) { SetEnergyHudVisible(false); _scoreRoll?.SetVisible(false); _bonusRoll?.SetVisible(false); }   // hide the gauge AND the big/small ShowTime score at song end (not on the result panel)
            RebuildRoster();                                  // finalize scores so the rank/winner is current
            _gameOver = _hpDead;                              // HP-out → GAME OVER (overrides win/lose banner);完奏模式打完整首也算
            // STAGE 1 (win/lose pose): clear ONLY the note board (+HP/receptors) and its combo/judgment words.
            // The top score, centre rank and right-side roster STAY visible until the result panel appears.
            SetTrackVisible(false);                           // note board + HP + receptors + click strips
            // A window still OPEN when the song ends never gets another Tick — Update returns at `_ended` (top of the
            // frame) before TickShowtime — so OnShowtimeEnd would never run and the swap it undoes would stick: the
            // dancer keeps the 7-20s breakdance DPS, and the result screen's background replay (which only re-points
            // DanceTimeSec at the song-length loop clock) would break for its first few seconds and then stand in the
            // standby idle for the rest of EVERY lap. BuildDanceIntervals would take its ceiling from the break's
            // Total too, dropping under ReplayMinRunMs so the randomised start collapses to 0 as well. Close the
            // window here; that also kills the aura + EDGE4 columns, which must go because the board is now hidden.
            // The meter itself stays Active (only its own Tick clears that) — nothing reads it past `_ended`, and the
            // result panel still needs its Bonus.
            if (showtimeMode) { if (_showtime.Active) OnShowtimeEnd(); else ClearShowtimeWindowFx(); }
            // SetTrackVisible(false) also hid the ranking — but it must STAY up through the win/lose pose (final
            // standings). Re-show it here with the final order; only HideHudForPanel (result panel) hides it.
            if (_rosterName != null) { UpdateRosterList(); UpdateRankDisplay(); SetRankingVisible(true); }
            HideComboAndJudge();                              // combo number + judgment word (part of the play board)
            ClearGameplayFx();                                // tear down in-flight bursts/holds (F5 mid-song leaves a hold burst looping)
            if (_emoji != null) _emoji.Stop();                // clear any head emoji cut-in so it doesn't linger into the result
            ReturnAllVisuals();                               // also kill any note sprites still in flight (return them to the pool)
            if (_gameOver)
            {
                // 血條用完死掉 (HP-out): 不放結束勝利/失敗的定格動作與 FINISHED effect;改播死亡字幕 GAME OVER (置中) +
                // Frameextrude 音效。多人時只有「全員陣亡」才走這條;只要有人沒死,倖存者在歌曲結束照走原本輸贏流程 —
                // 本重製為單人(mock 對手無 HP、不會死),故 _failed(本人陣亡)== 全員陣亡。
                PlaySe("Frameextrude");
                LoadGameOverFrames();                             // 依當前 note skin 選對應的 GAMEOVER 圖 (per-skin)
                StartCoroutine(GameOverAnim());
            }
            _resultPhase = ResultPhase.FinishPose; _resultPhaseStart = Time.time;
            // 輸贏定格(本機 + 場上其他人)。線上**不在這一幀決定** —— 見 TickFinishPoseDecision。
            _finishPoseDone = false; _finishPoseAuthoritative = false; _finishedEftSpawned = false;
            _remoteFinishWinner = -1;
            TickFinishPoseDecision();                         // 離線/單人:資料都在本機,同一幀就定案(行為不變)
        }

        /// <summary>權威名次到手前,最多等這麼久就用本機名單定輸贏(秒)。
        /// 等過了先用本機名單演,權威名次再晚到還是會覆蓋(見 <see cref="TickFinishPoseDecision"/>)。</summary>
        private const float FinishDecideMaxSec = 1.0f;
        private bool _finishPoseDone = true;     // 這一局的輸贏定格已經演過(不管是權威還是本機猜的)
        private bool _finishPoseAuthoritative;   // 演的那次是用 server 權威名次決定的 → 不會再改
        private bool _finishedEftSpawned;        // FINISHED 特效放過了(改判時不重放,也收不回來)
        private bool _finishPoseShownWon;        // **現在演著的**是勝利姿勢嗎(翻案要不要重演比的是它,不是 _localWon)

        /// <summary>
        /// 決定並播出輸贏定格(本機 cat5/cat4 + FINISHED + 短曲,以及場上其他人的定格)。
        ///
        /// 🔴 線上要等 server 的權威名次(resultsReady),等不到才退回本機名單:
        /// 曲末**這一刻**本機手上的對手分數,是 5Hz 分數流的最後一筆 —— 少了他最後零點幾秒打的音符。
        /// 拿它定輸贏,同分/接近時兩台都會覺得自己贏,於是「結算面板寫我第 2 名,人卻在跳勝利動作」
        /// (使用者回報)。權威名次通常在一個往返內就到;真的沒到,等過 <see cref="FinishDecideMaxSec"/> 時
        /// 對手的**最終**成績也早就從分數流補上了,而且平手照座位序(<see cref="RankingBoard"/>)兩台一致。
        ///
        /// 一局最多演兩次:先用本機名單猜的那次,以及權威名次晚到而且**改判**時的重演(硬切)。
        /// 短曲只在第一次放、FINISHED 只放一次 —— 重演的是姿勢,不是整套演出。
        /// </summary>
        /// <param name="force">true = 不再等,現在就定案(面板要開了,見 ResultTick)。</param>
        private void TickFinishPoseDecision(bool force = false)
        {
            if (_finishPoseAuthoritative) return;              // 已經照權威名次演過 → 不會再有更好的答案
            int place = NetLocalPlace();                       // >0 = 權威名次已到
            bool authoritative = place > 0 || NetWinnerUserId() != 0;
            bool online = NetResultRows != null;
            if (!authoritative)
            {
                if (_finishPoseDone) return;                   // 本機猜的那次演過了,等權威名次來翻案
                if (online && !force && Time.time - _resultPhaseStart < FinishDecideMaxSec) return;   // 再等一下
            }
            // 權威名次晚到(>FinishDecideMaxSec)時會走到這裡第二次:改判就把定格重演一次。演錯 1 秒總比
            // 「面板寫第 2 名、人在跳勝利動作」整段演完好 —— 那正是這次要修的回報。
            bool redo = _finishPoseDone;
            _finishPoseDone = true;
            _finishPoseAuthoritative = authoritative;

            // 名單也一起定案:權威結果在的話直接用它(曲末那一刻的分數流還少了大家最後零點幾秒),
            // 不在就用「等到現在」的最新分數流重算 —— 兩者都比 EnterResult 當下那一份完整。
            if (!RosterFromNetRows()) RebuildRoster();
            // rank 1 = highest score = winner。
            // 🔴 旁觀者不在名單裡 → LocalRank 回 rank 0(「找不到本機」)。**不能**寫 place <= 1:那個 0 會被
            // 判成贏(旁觀者跟著演勝利定格、權威列裡查不到自己時也會誤判)。與 CalculateResultOutcome 的定格判定
            // 同一條:== 1。結算面板那面旗是另一條(並列名次排進前半就出,見 RankingBoard.IsWinningPlace)。
            if (place <= 0) place = RankingBoard.LocalRank(_roster).rank;
            _localWon = !spectatorMode && place == 1;
            // 勝負場的記錄是**另一回事**:同分兩邊都記勝場(使用者指定)。定格/旗子只能有一個第一名,
            // 但打成平手的兩個人誰也沒輸給誰。見 RankingBoard.LocalTiedForTop 與 LocalWonForRecord。
            _localWonForRecord = !spectatorMode && RankingBoard.LocalTiedForTop(_roster);
            // 右側名單/名次跟著定案的分數。SetRankingVisible 是顯示與否的**唯一**政策點(自由模式一律不出),
            // 少了它自由模式會在定格這 2.5 秒把名單掀出來。
            if (_rosterName != null) { UpdateRosterList(); UpdateRankDisplay(); SetRankingVisible(true); }
            // 🔴 比的是「**現在演著的**是哪一種姿勢」,不是 _localWon 的舊值 —— 面板開場的
            // CalculateResultOutcome 也會寫 _localWon(ResultsOnline.cs),拿它當基準的話會漏掉那種翻案。
            bool localChanged = !redo || _localWon != _finishPoseShownWon;
            if (!_gameOver && localChanged)
            {
                _finishPoseShownWon = _localWon;
                if (_avatar != null)                              // win/lose 定格 pose (cat5/cat4), held on its last frame
                {
                    var mot = ResolveMot(_localWon ? winMot : loseMot);
                    // 翻案是**硬切**(定格→定格,平滑過場只會糊成一團);第一次照舊讓它從舞蹈平順接進定格。
                    if (mot != null) { if (redo) _avatar.SnapNextClip(); _avatar.PlayOneShot(mot, true); }
                }
                // 旁觀者不放輸贏短曲 —— 它沒有輸也沒有贏,而 _localWon 恆 false 會讓它每次都聽到「輸了」的音效。
                // 翻案時**不再**放一次(兩聲短曲比一聲錯的還糟)。
                if (enableResultSfx && !spectatorMode && !redo) PlaySe(_localWon ? "SE_0014" : "SE_0015");   // win/lose jingle
            }
            // 場上其他人的輸贏定格。GAME OVER 時**照樣要放**:那是本機血條見底的死亡流程,
            // 別人並沒有死 —— 本機一死就讓全場站著不動,那是把自己的結局套到別人身上。
            // (翻案時它自己會判斷贏家有沒有換人,沒換就不動 —— 重播會把定格倒回第 0 幀。)
            PlayRemoteFinishPoses(redo);
            // FINISHED = 官方掛在**第一名舞者**腳下的完奏特效。一定要在 PlayRemoteFinishPoses 之後 ——
            // 贏家是那邊定案的,錨點要跟做勝利動作的是同一個人(見 FinishedEftAnchor)。
            // GAME OVER(本機血條見底)不放:那條路整套輸贏演出都不演。
            // 翻案翻成別人贏也收不回來(特效自己會在 5 秒內結束),但至少不會再放第二次。
            if (!_gameOver && !_finishedEftSpawned)
            {
                var eftAnchor = FinishedEftAnchor();
                if (eftAnchor != null) { _finishedEftSpawned = true; SpawnNamedEft("FINISHED", 5f, eftAnchor); }
            }
        }

        // 死亡字幕的「哪一組」= 官方由**同一個變體 id S**(DAT_00674f04+0x68)同時決定 note_image 與 gameover
        // (Gameplay_OnLoadComplete jump table @0x474ed4: S=3→gameover8, 4→gameover9, 5→gameover10, 6→gameover5,
        // 7/8→gameover2, 其餘→gameover5;而 note_image8/9/10↔S3/4/5、note_image5↔S7/8、note_image6↔S1/2/6、
        // note_image11↔S9、note_image_pet↔S10)。⇒ gameover 是綁「note-image(board)」不是 EFT 命中特效編號。
        // 對照 board→gameover:  8→GAMEOVER8 · 9→GAMEOVER9 · 10→GAMEOVER10 · 5→GAMEOVER2 · 6/11/PET→GAMEOVER5。
        private static string GameOverSuffixForBoard(string board)
        {
            switch (board)
            {
                case "8":   return "8";
                case "9":   return "9";
                case "10":  return "10";
                case "5":   return "2";     // note_image5 配 gameover2 (官方 S=7/8)
                case "11":  return "2";     // 使用者指定 EFT_14(board11)→GAMEOVER2 (離線反編譯是 default gameover5,覆寫)
                case "PET": return "8";     // 使用者指定 PET→GAMEOVER8 (離線反編譯其實是 default gameover5,這裡刻意覆寫)
                default:    return "5";     // board 6 → gameover5 (官方 S=1/2/6 走 default)
            }
        }

        private void LoadGameOverFrames()
        {
            // stock(-1)=開機預設 EFT_2(board6);3D skin 無 note_image → 用官方 default gameover5。
            int t = _hit3dMode ? -1 : (_eftNoteType >= 0 ? _eftNoteType : 0);
            string board = (t >= 0 && t < NoteTypeBoardSuffix.Length) ? NoteTypeBoardSuffix[t] : "6";
            string dir = Path.Combine(SdoExtracted.Root, "EFFECT", "GAMEOVER" + GameOverSuffixForBoard(board));
            if (!Directory.Exists(dir)) dir = Path.Combine(SdoExtracted.Root, "EFFECT", "GAMEOVER");   // 保險退回基本組
            var gof = new List<Sprite>();
            foreach (var gn in new[] { "GAMEOVER00.PNG", "GAMEOVER01.PNG", "GAMEOVER02.PNG" })
            { var gs = SdoExtracted.LoadImage(dir, gn, bleed: true); if (gs != null) gof.Add(gs); }
            _gameOverFrames = gof.ToArray();
        }

        // 死亡字幕 GAME OVER: motion-blur 幀掃入 (00→01) 後停在清晰的 02,置中畫面 (400,300)。frame list 取自
        // GAMEOVER.AN (00,01,02×10 → 定格 02)。定格幀持續顯示,直到結算面板出現 (ShowResultPanel 關掉它)。
        private IEnumerator GameOverAnim()
        {
            if (_gameOverGo == null || _gameOverFrames == null || _gameOverFrames.Length == 0) yield break;
            _gameOverGo.enabled = true;
            for (int i = 0; i < _gameOverFrames.Length; i++)
            {
                _gameOverGo.sprite = _gameOverFrames[i];
                PlaceAspect(_gameOverGo, 400f, 300f, _gameOverFrames[i].rect.width * gameOverScale, -6f);   // native size, centre screen, above READY/GO plane
                float t = 0f; while (t < gameOverFrameSec) { t += Time.deltaTime; yield return null; }
            }
            // holds on the last (crisp) frame — already placed above; ShowResultPanel disables the overlay.
        }

        // STAGE 1: combo number + judgment word (these belong to the note board, gone during the win/lose pose).
        private void HideComboAndJudge()
        {
            foreach (var d in _comboDigits) if (d) d.enabled = false;
            if (_comboWord) _comboWord.enabled = false;
            if (_judgeWord) _judgeWord.enabled = false;
        }

        // STAGE 2 (result panel appears): hide the remaining gameplay HUD — top score, centre rank + right-side
        // roster, bottom song-info labels, and the head nameplate ("玩家" under the arrow) — so only the panel +
        // background dance show.
        private void HideHudForPanel()
        {
            SetRankingVisible(false);                          // centre rank readout + right-side roster list
            if (_scoreDigits != null) foreach (var d in _scoreDigits) if (d) d.enabled = false;
            if (_lblSong) _lblSong.enabled = false;
            if (_lblAttr) _lblAttr.enabled = false;
            if (_musicName != null) _musicName.SetActive(false);
            if (_lvText) _lvText.gameObject.SetActive(false);
            if (_timeMin) _timeMin.gameObject.SetActive(false);
            if (_timeText) _timeText.gameObject.SetActive(false);
            if (_timeTotal) _timeTotal.gameObject.SetActive(false);
            if (_info) _info.gameObject.SetActive(false);
            // 旁觀提示條(Ctrl+Q)跟著其餘 HUD 收掉:結算面板一開,那顆熱鍵的去路換成面板自己的流程。
            if (_spectateHint) _spectateHint.enabled = false;
            // 頭上的名牌（箭頭 + 名字）結算/回放全程保留不隱藏 — 不呼叫 _headMarker.Hide()。
        }

        // On the result panel, the old top song-name/level row is gone; instead the gameplay HUD's bottom song-info row
        // (歌曲名 + LV) stays visible just below the panel (it ends at design y≈565, the row sits at y=575). The 時間 field
        // is dropped: the time value is hidden and the combined "LV: 时间:" label is swapped to the "LV:"-only crop.
        private void ShowResultSongInfo()
        {
            if (_lblSong) _lblSong.enabled = true;                          // "歌曲名:"
            // 結算列固定回官方預設位置：不管遊戲時 面板位置(左/中) 或 掉落方式(上/下) 把「LV: 时间:」整組推到左下或右下，
            // 結算時 歌名+LV 都要回到 GamePlay 預設欄位（歌名值 x=80、LV 標籤 x=204、LV 值 x=240）。
            // ── 過去只重置 LV 標籤(_lblAttr) 沒重置 LV 值(_lvText)，向下置中模式下 _lvText 仍停在 548+36=584，數字就跑到最右邊。
            if (_musicName != null)
            {
                _musicName.SetActive(true);                                // song title value
                _musicName.Position = SdoLayout.ToWorld(80f, 585f, -1f);
            }
            if (_lvText)
            {
                _lvText.gameObject.SetActive(true);                        // LV value
                _lvText.transform.position = SdoLayout.ToWorld(240f, 585f, -1f);
            }
            if (_lblAttr)
            {
                if (_lvOnlyLabel != null) _lblAttr.sprite = _lvOnlyLabel;  // "LV:" only (drop "时间:")
                SdoLayout.PlaceTopLeft(_lblAttr, 204, 575);                // re-place: cropped sprite has narrower bounds
                _lblAttr.enabled = true;
            }
            if (_timeMin) _timeMin.gameObject.SetActive(false);           // 時間欄位移除（分）
            if (_timeText) _timeText.gameObject.SetActive(false);         // 時間欄位移除（: 秒）
            if (_timeTotal) _timeTotal.gameObject.SetActive(false);       // 連同總長欄一起移除
        }

        // Drive the post-song sequence: hold the win/lose pose, then settle the panel, then loop the background
        // replay. Phase A implements FinishPose; Settle/Replay are filled in by later phases.
        private void ResultTick()
        {
            UpdateHeadPortraitCam();          // keep the local head-portrait cam tracking the (moving) head each frame
            SyncLocalHeadPortraitIdle();      // 本機那一格:MMD 顯示時改用大家共用的那支待機(F8 可即時開關 → 每幀比)
            SyncResultHeadPortraitTuning();   // 遠端那幾格跟著同一組(F4 可調的)取景參數走
            float el = Time.time - _resultPhaseStart;
            switch (_resultPhase)
            {
                case ResultPhase.FinishPose:
                    TickFinishPoseDecision();   // 線上:權威名次到了(或等夠了)才放輸贏定格
                    // 面板要開了 → 不管等到沒等到都得先定案。finishPoseSec 是可調欄位,調到比
                    // FinishDecideMaxSec 還短時,少了這個 force 就會整局都不放定格(面板一開就沒人再呼叫它)。
                    if (el >= finishPoseSec)
                    {
                        TickFinishPoseDecision(force: true);
                        ShowResultPanel(); _resultPhase = ResultPhase.Settle; _resultPhaseStart = Time.time;
                    }
                    break;
                case ResultPhase.Settle:
                    _result?.Tick();   // slide rows in / scale the banner / poll the OK button
                    // After a brief beat start the background replay loop (decompiled phase6 → dance engine state 4).
                    if (el >= settleSec) { StartBackgroundReplay(); _resultPhase = ResultPhase.Replay; _resultPhaseStart = Time.time; }
                    break;
                case ResultPhase.Replay:
                    _result?.Tick();   // panel stays interactive; the avatar's delegates (below) loop the recorded dance
                    break;
            }
        }

        // Begin the result-screen BACKGROUND replay: drop the win/lose pose and re-drive the avatar's DPS dance
        // from a LOOPING song clock, replaying the recorded dance-gate so the original stop/start gaps come back.
        // Notes/board stay hidden (SetTrackVisible(false) already in effect); only the lit stage + dancer show.
        private void StartBackgroundReplay()
        {
            // 迴圈長度與起點是**全場共用**的(場上每個人都吃同一顆時鐘,回放才是同一段演出),所以先算,
            // 而且不能因為本機沒有舞者(旁觀)就整段跳過 —— 別人的回放正是旁觀者要看的東西。
            _replayLenMs = _totalMs > 1.0 ? _totalMs : Math.Max(1.0, _replay.LengthMs);
            // Start the loop on a GOOD slice, not always the song's opening: a ≥20s stretch where the #1 dancer is
            // actually dancing (gate ON + within the choreography), biased to its busiest window, with per-visit jitter.
            _replayOffsetMs = ReplayStartPicker.Pick(_noteStarts, BuildDanceIntervals(), UnityEngine.Random.value, ReplayMinRunMs);
            _replayLoopStart = Time.timeAsDouble;
            System.Func<float> loopTimeSec = () => (float)(LoopMs() / 1000.0);
            if (_avatar != null)
            {
                _avatar.ClearOneShot();                                   // resume the DPS dance path
                _avatar.SnapNextClip();                                   // 定格 pose → 回放舞蹈 走硬切，不做平滑過場
                foreach (var rib in _handTrails) if (rib) rib.Clear();    // 手在硬切處瞬移 → 清掉光條歷史，別從定格 pose 連一條光帶到回放起點；回放開始後光條自然重新累積成連續光帶（後面 mot 的手部光繼續做）
                _avatar.DanceTimeSec = loopTimeSec;
                _avatar.DanceEnabled = () => GateAt(LoopMs());
            }
            StartRemoteBackgroundReplay(loopTimeSec);   // 場上其他人跟著同一顆迴圈時鐘一起再跳一遍
        }

        // Minimum continuous dance the replay start must have ahead of it (the #1 dancer keeps dancing ≥ this long).
        private const double ReplayMinRunMs = 20000.0;

        // Continuous [start,end] ms spans where the looped dancer is actually dancing: the recorded dance gate
        // (_danceTrack — default ON before the first event) clamped to [0, min(loop length, choreography end)],
        // since past Dps.Total the avatar holds idle. Feeds ReplayStartPicker so the random start lands in real dance.
        private List<(double start, double end)> BuildDanceIntervals()
        {
            double ceil = _replayLenMs;
            // 編舞是全場共用的同一份(_sharedDps 就是 _avatar.Dps)—— 取它而不是只取本機的,旁觀時本機沒有
            // 舞者但場上有人在跳,天花板照樣要吃編舞長度,否則起點會挑到編舞結束後的那段空白。
            var dps = _avatar != null && _avatar.Dps != null ? _avatar.Dps : _sharedDps;
            if (dps != null && dps.Total > 0f) ceil = Math.Min(ceil, dps.Total * 1000.0);
            var ivs = new List<(double, double)>();
            if (ceil <= 0.0) return ivs;
            bool on = true; double segStart = 0.0;                    // gate defaults ON from t=0 (matches GateAt)
            for (int i = 0; i < _danceTrack.Count; i++)
            {
                if (_danceTrack[i].tMs >= ceil) break;                // _danceTrack is time-ordered → rest are later too
                if (_danceTrack[i].on == on) continue;                // no state change
                double t = Math.Max(0.0, _danceTrack[i].tMs);
                if (on) { if (t > segStart) ivs.Add((segStart, t)); } // ON → OFF: close the run
                else segStart = t;                                    // OFF → ON: open a run
                on = _danceTrack[i].on;
            }
            if (on && ceil > segStart) ivs.Add((segStart, ceil));     // trailing ON run to the ceiling
            return ivs;
        }

        // Current position within the looping background replay (ms, 0.._replayLenMs), starting at _replayOffsetMs.
        private double LoopMs()
        {
            double t = (Time.timeAsDouble - _replayLoopStart) * 1000.0 + _replayOffsetMs;
            return _replayLenMs > 1.0 ? (t % _replayLenMs) : t;
        }

        // Build + show the STATIS result panel with this round's ranked rows (decompiled phase6).
        private void ShowResultPanel()
        {
            if (_gameOverGo) _gameOverGo.enabled = false;   // 死亡字幕收起 — 結算面板要接手
            HideHudForPanel();   // stage 2: now hide the score / rank / roster / song-info / nameplate
            ShowResultSongInfo();   // ...but keep the bottom 歌名/LV row (time field dropped) as the result's song-info
            if (_result == null)
            {
                _result = new ResultScreen();
                _result.Build(_cam);
                _result.OnConfirm = () =>
                {
                    // Hosted by the front-end (lobby/room flow) → just flag it; FrontendApp tears gameplay down and
                    // returns to the room. Standalone (self-boot) → reload the scene to replay.
                    if (AutoBootSuppressed) ResultConfirmed = true;
                    else UnityEngine.SceneManagement.SceneManager.LoadScene(
                        UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);   // 確定 → 重玩 (reload)
                };
            }
            _result.autoConfirmSec = resultAutoConfirmSec;   // 連線 = 30 秒後自己按確定(每次開面板都重設,面板本身只建一次)
            string diff = _map != null ? "Lv " + _map.Level : "";
            var rows = PrepareResultRows();   // also rebuilds _roster and attaches every participant portrait
            // round-end reward for the LOCAL player (Arrowgene emulator formulas — see Sdo.Ruleset.Reward).
            CalculateResultOutcome(rows, out bool localWon, out int expGained, out int coinsGained);
            // 經驗值落地：加進 active 角色的 profile.json（到門檻自動升等，曲線見 PlayerLevel）。旁觀者的
            // expGained 本來就是 0（見 CalculateResultOutcome）→ 不碰存檔。本局的 G幣/榮譽用進場時的等級算，升上去
            // 的等級下一局才生效；伺服器最終名次晚到只會刷新面板（RefreshNetResultRows），不會重複入帳。
            if (expGained > 0) ProfileManager.AddExperience(expGained);
            // 自由模式**照給** G幣/EXP,只是名次不畫;沒獎勵的只有旁觀者(沒下場,而且 place 會是 0 = 找不到本機)。
            // 旁觀者沒有自己的舞者 → 沒有頭貼可拍(BuildLocalHeadPortrait 會回 null,結算列用預設圖)。
            Texture head = spectatorMode ? null : BuildLocalHeadPortrait();   // live 3D head for the local row (null → placeholder)
            // 自由模式不出 YOU WIN/LOSE 字幕 (但結算最後的 SE_0022 音效仍要有 → ResultScreen 內處理)。GAME OVER 同理不出旗。
            // 旁觀也不出:那面旗是「你贏了/輸了」,而旁觀者兩者都不是。
            // 自由模式也沒有名次 → 結算列最左的名次數字不畫(GAME OVER 圖仍照畫)。
            // 結算面板一出來就把聊天框整組收掉:那時已經不在打歌,而且結算的「確定」也是 Enter —— 兩個
            // Enter 用途疊在一起會很怪(而且聊天列會壓在結算面板上)。
            SetChatVisible(false);
            _result.Show(_songTitle, diff, rows, localWon, expGained, coinsGained, head, _gameOver, PlaySe,
                         showBanner: !freeMode && !spectatorMode, showRank: !freeMode);
        }

        // Turn the final roster + score into ranked result rows. The local player uses real judgment counts;
        // mock opponents get plausible counts synthesised from their score (no real per-opponent judging).
        private ResultScreen.Row[] BuildResultRows()
        {
            RebuildRoster();
            // 連線:server 的 resultsReady 才是每個人**真正**的判定數(對手的判定計數本機根本沒有,
            // 下面那條路是拿分數反推出來的假數字 —— 只適合離線的假對手)。
            if (NetResultRows != null)
            {
                var netRows = NetResultRows();
                if (netRows != null && netRows.Length > 0) return netRows;
            }
            var order = RankingBoard.SortedIndices(_roster);
            // 音符總數:通常就是生出來的音符數,但旁觀模式**不生音符**(_notes 是空的)→ 會變成
            // 「這首歌只有 1 顆音符」,每個人的判定數都被反推成 1。改成生不出來時退回譜面本身的數字。
            // (連線時上面就 return 了 —— 用的是 server 的真判定數;這條只是離線/退化路徑。)
            int total = Math.Max(1, _notes.Count > 0 ? _notes.Count : (_map != null ? _map.TotalNotes : 0));
            long top = order.Length > 0 ? Math.Max(1L, _roster[order[0]].Score) : 1L;
            var rows = new ResultScreen.Row[order.Length];
            for (int i = 0; i < order.Length; i++)
            {
                var p = _roster[order[i]];
                ResultScreen.Row r;
                if (p.IsLocal && _score != null)
                {
                    int P = _score.PerfectCount, C = _score.CoolCount, B = _score.BadCount, M = _score.MissCount;
                    int judged = Math.Max(1, P + C + B + M);
                    r = new ResultScreen.Row { Perfect = P, Cool = C, Bad = B, Miss = M, MaxCombo = _score.MaxCombo, Accuracy = (P + C) * 100.0 / judged, Score = TotalScore };
                }
                else
                {
                    double accFrac = Mathf.Clamp01((float)(p.Score / (double)top) * 0.97f);
                    int hits = (int)Math.Round(total * accFrac);
                    int P = (int)Math.Round(hits * 0.85), C = hits - P, M = total - hits;
                    r = new ResultScreen.Row { Perfect = P, Cool = C, Bad = 0, Miss = Math.Max(0, M), MaxCombo = M == 0 ? hits : (int)Math.Round(hits * 0.6), Accuracy = accFrac * 100.0, Score = p.Score };
                }
                r.Rank = i + 1; r.Name = p.Name; r.IsLocal = p.IsLocal;
                r.FullCombo = (r.Bad + r.Miss) == 0;
                // HP-out (死過就算) → 評分 F for the local player; everyone else by accuracy band.
                r.Grade = (p.IsLocal && _hpDead) ? "F" : Sdo.Ruleset.Grade.FromAccuracy(r.Accuracy);
                rows[i] = r;
            }
            // 名次牌上寫的是**同分並列、不跳號**的名次(1,1,2);Rank 那一欄留給輸贏定格用的嚴格順序。
            var scores = new long[rows.Length];
            for (int i = 0; i < rows.Length; i++) scores[i] = rows[i].Score;
            var display = RankingBoard.DisplayRanks(scores);
            for (int i = 0; i < rows.Length; i++) rows[i].DisplayRank = display[i];
            return rows;
        }

        // Build the scroll positioner from the loaded chart + the selected speed step. Base speed either follows
        // the song's own BPM (official px/s = BPM×speed×1.6) or is anchored to referenceBpm for every song;
        // osu-style mid-song BPM/SV variation on top (or none if constantScroll).
        private void BuildScroll()
        {
            _scroll = ManiaScroll.Build(_map, scrollSpeedMul, constantScroll, referenceBpm, scrollFollowsSongBpm);
            Debug.Log($"[Step1] scroll vBase={_scroll.BaseVelocity:F0}px/s (speed {scrollSpeedMul}×"
                + $", {(scrollFollowsSongBpm ? $"follow songBpm {_map.Bpm:F1}" : $"fixed {referenceBpm}bpm")})"
                + $", {_map.TimingPoints.Count} timing pts, constant={constantScroll}");
        }

        // 向上 (up-scroll, _scrollSign +1): future notes are BELOW the hit line and RISE to it. 向下 (_scrollSign −1):
        // future notes are ABOVE and FALL to it. Distance comes from ManiaScroll (osu Sequential integration), so
        // mid-song BPM changes / SV vary it locally; the sign just picks which side of the judge line notes come from.
        // Signed on-screen distance (design-px) from the judge line to a note at noteMs. SDO online/NX frame_type 33
        // 捲動速度 is a SET current speed: the whole field scrolls at CurrentScrollSpeed(now) (snaps at an instant
        // event, ramps linearly when the slot carries a duration), so a ×10 event genuinely pushes the notes out
        // (the intended gimmick). No type-33 (offline .gn) → CurrentScrollSpeed is 1 → identical to before.
        private float ScrollPx(double now, double noteMs)
        {
            if (_scroll == null) BuildScroll();
            float sv = _map != null ? (float)_map.CurrentScrollSpeed(now) : 1f;
            return sv * (float)_scroll.PixelDistance(now, noteMs);
        }

        private float YForTime(double noteMs, double now)
            => judgeLineY + judgeOffsetY + _scrollSign * ScrollPx(now, noteMs);

        private void ScrollNotes(double now)
        {
            bool use3d = _note3dMode && note3dMesh && EnsureHighway();   // real 3D mesh highway (else 2D coloured-sprite path)
            _highwayItems.Clear();
            AdvanceAliveWindow();
            // Notes are start-time sorted, so once one is far enough ahead to still be off the bottom (up-scroll) /
            // top (down-scroll) of the board, every later note is too. `aheadPx` is the exact on-screen distance from
            // the judge line to the far entry edge (+ margin); break there instead of walking the whole 10k chart.
            float aheadPx = Mathf.Max(_clipBottomY + 60f - judgeLineY, judgeLineY - (_clipTopY - 60f)) + 200f;
            for (int i = _firstAlive; i < _notes.Count; i++)
            {
                var n = _notes[i];
                if (n.Done) { ReturnVisual(n); continue; }
                // 「歌曲變速」關(constantScroll)→ warp(負 BPM)掃掉的裝飾音**不畫**:那個模式把所有 timing point
                // 丟掉,連 warp 的 1ms 超高速顯示窗都沒了,整段被跳過的拍子會疊成一坨捲進來(見 WarpDecoration)。
                // 判定不受影響 —— IsFake 本來就不判定,warp 炸彈的「按住穿過 = 自動打擊」照跑。
                if (WarpDecoration.IsHidden(n.Note, constantScroll, editorMode))
                {
                    ReturnVisual(n);
                    // 退場得自己負責:TickBombs 把 IsFake 的退場讓給顯示端,而下面那條「流出畫面才收」的路徑
                    // 這一顆永遠走不到了。炸彈要等跨線游標真的越過它才收(理由同 offPast 的 bombPending)。
                    if (WarpDecoration.CanRetire(n.Note, now, _bombPrevNow)) n.Done = true;
                    continue;
                }
                if (n.Note.ScrollTimeMs > now && ScrollPx(now, n.Note.ScrollTimeMs) > aheadPx)
                {
                    // Note is past the far edge of the board. With frame_type 33 捲動速度 the current speed can jump/
                    // ramp, so a note that's off-board this frame may be back on it the next — we must keep hiding it
                    // (never leave a stale visual frozen at its last spot). Constant-scroll charts stay sorted, so the
                    // classic early-out is still safe and cheaper there.
                    if (_map != null && _map.ScrollSpeeds.Count > 0) { ReturnVisual(n); continue; }
                    break;   // this + all later notes are still below/above the board
                }
                int c = n.Note.Lane;
                bool held = _holding[c] == n;        // a held long-note head stays pinned to the judge line
                // 長條頭按住時釘在判定線；一旦尾端(END)通過判定線 (now ≥ EndTimeMs) 就整條隱藏 — 判定仍在跑
                // (還按著 → 等放開評 tail，或 release 窗口過了才 AutoMiss)，但畫面上直接消失，不留一顆釘在判定線的頭。
                if (held && n.Note.EndTimeMs.HasValue && now >= n.Note.EndTimeMs.Value) { ReturnVisual(n); continue; }
                // 位置一律用 ScrollTimeMs(顯示用時間),而不是判定時間 —— 兩者只有 StepMania warp(負 BPM)會不一樣:
                // warp 是零秒跳過一段拍子,那段音符判定時刻全部擠在同一瞬間,但畫面上仍要照拍子鋪開(見 OsuHitObject)。
                float yRaw = held ? judgeLineY : YForTime(n.Note.ScrollTimeMs, now);
                float yEnd = n.Note.EndTimeMs.HasValue ? YForTime(n.Note.ScrollEndTimeMs, now) : yRaw;
                // a note that has flowed off the top (above the clip band, past the HP bar) is no longer VISIBLE,
                // but it is NOT retired yet: it stays alive and judgeable until its miss window actually elapses.
                // On slow songs the off-top point comes BEFORE MissBoundary, so retiring it here would skip the
                // Miss — instead we just hide it and let AutoMiss (or a late in-window press) judge it at the proper
                // time, then retire it once it's been judged. (disappears at the same spot as before, still scored.)
                bool offPast = !held && (_scrollSign > 0
                    ? Mathf.Max(yRaw, yEnd) < _clipTopY - 36f       // up-scroll: flowed off the TOP past the judge line
                    : Mathf.Min(yRaw, yEnd) > _clipBottomY + 36f);  // down-scroll: flowed off the BOTTOM past the judge line
                if (offPast)
                {
                    ReturnVisual(n);
                    // hit late / auto-missed -> now fully retired. warp 掃掉的裝飾音永遠不會被判定,流出畫面就直接收掉
                    // (不收的話它會一直卡在 _firstAlive 前面,每幀都被掃到)。
                    //
                    // 例外:warp 內的**炸彈**是「按住自動打擊」的觸發器(TickBombs → WarpMineStep),而
                    // ScrollNotes 跑在 TickBombs 前面(見 Tick 的呼叫順序)。warp 的顯示窗只有
                    // WarpDisplayMs(1ms)、遠短於一幀,所以播放頭跨過 warp 時刻的那一幀,這批炸彈已經被超高速
                    // 捲動甩出畫面 —— offPast 與 TickBombs 的跨線偵測落在**同一幀**。這裡先收掉的話,同一幀的
                    // TickBombs 只會看到 Done 而整批跳過,gimmick 永遠不會發生(實測 [blue]Dreadnought
                    // 按住穿 warp 全 miss)。等跨線游標 _bombPrevNow 真的越過它之後再收,下一幀就收得到。
                    bool bombPending = n.Note.IsBomb && n.Note.IsFake && n.Note.StartTimeMs > _bombPrevNow;
                    if ((n.HeadJudged || n.Note.IsFake) && !bombPending) n.Done = true;
                    continue;
                }
                bool visible = held || (_scrollSign > 0
                    ? Mathf.Min(yRaw, yEnd) <= _clipBottomY + 60f   // up-scroll: shown once it enters from the bottom
                    : Mathf.Max(yRaw, yEnd) >= _clipTopY - 60f);    // down-scroll: shown once it enters from the top; SpriteMask clips it to the board
                if (!visible) { ReturnVisual(n); continue; }
                RentVisual(n);                    // entered the board -> hand it a pooled visual (skin-bound), then render as before
                n.Head.enabled = true;
                float y = yRaw;   // NO clamp — notes keep flowing past the receptor (the mask hides them above the HP bar)
                int frame = ((int)(Time.time * noteAnimFps)) & 3;   // 4-frame glow cycle (= the official _0.._3 frames)
                bool stWin = showtimeMode && _showtime.Active;
                float noteScale = stWin ? showtimeNoteScale : 1f;       // notes grow a little during the auto-hit window
                float noteW = NoteW * noteScale;
                if (n.Note.IsBomb)
                {
                    // 炸彈：ZD00..ZD03 循環動畫(用較慢的 bombAnimFps),平面 sprite —— 不旋轉、不吃 3D 箭頭、無長條/尾。
                    if (n.Head.transform.localRotation != Quaternion.identity) n.Head.transform.localRotation = Quaternion.identity;
                    if (_bombFrames != null && _bombFrames.Length > 0)
                        n.Head.sprite = _bombFrames[((int)(Time.time * bombAnimFps)) % _bombFrames.Length];
                    n.Head.color = Color.white;
                    PlaceAspect(n.Head, PX(LaneLeftX[c] + LaneCx0), y, noteW, 1f);
                    continue;
                }
                // 中途放開 (Bad/Miss) 的長條不直接消失：整條 (頭/身/尾) 調暗到 holdDropDim，繼續往判定線外流走。
                Color noteCol = showtimeMode ? _noteTint : Color.white;   // showtime: gold→red flash over the window's last 3s
                if (n.Dropped) noteCol = new Color(noteCol.r * holdDropDim, noteCol.g * holdDropDim, noteCol.b * holdDropDim, noteCol.a);
                bool tintNote = showtimeMode || n.Dropped;                // else leave the renderers at their default white
                if (tintNote) { n.Head.color = noteCol; if (n.Tail) n.Tail.color = noteCol; if (n.HeadCap) n.HeadCap.color = noteCol; }
                if (use3d)
                {
                    // 3D-MESH head: draw the real NOTES.MSH arrow FLAT at this note's exact 2D position (same lane + scroll
                    // Y as the sprite), textured by the beat family, additive. The 2D head sprite is hidden; the hold
                    // body/tail below stay 2D. RotZ = the per-lane arrow direction.
                    // HOLD head is forced to family 0 = the on-beat (4th) MAGENTA (洋紅), regardless of its beat position.
                    _highwayItems.Add(new Note3dHighway.Item {
                        World = SdoLayout.ToWorld(PX(LaneLeftX[c] + LaneCx0), y, -0.5f),
                        Size = Note3dBaseW * note3dMaster * noteScale, RotZ = Note3dRot[c] + (note3dFlip180 ? 180f : 0f),
                        Family = n.Note.EndTimeMs.HasValue ? 0 : n.ColorFamily });
                    n.Head.enabled = false;
                }
                else
                {
                    if (_note3dMode && _note3dFamily != null && _note3dFamily[n.ColorFamily] != null)
                    {
                        // 2D fallback skin: colour by beat (magenta/blue/green up-arrow), rotated to point the lane's way.
                        n.Head.sprite = _note3dFamily[n.ColorFamily][frame];
                        n.Head.transform.localRotation = Quaternion.Euler(0f, 0f, Note3dRot[c] + (note3dFlip180 ? 180f : 0f));
                    }
                    else
                    {
                        if (_noteFrames[c] != null) n.Head.sprite = _noteFrames[c][frame];
                        if (n.Head.transform.localRotation != Quaternion.identity) n.Head.transform.localRotation = Quaternion.identity;   // restore after leaving 3D skin
                    }
                    PlaceAspect(n.Head, PX(LaneLeftX[c] + LaneCx0), y, noteW, 1f);
                }

                if (n.Note.EndTimeMs.HasValue)
                {
                    // 2D skin: 長條也 1:1（*_LONG 是 100×64 → 寬 NoteW 時 tileH 剛好 64，圖樣不變形，見下面的 tile 分支）。
                    // 3D skin: hold width matches the note mesh, scaled by the master (+ showtime grow).
                    float holdW = (_note3dMode ? Note3dBaseW * note3dHoldWidth * note3dMaster : NoteW) * noteScale;
                    float cx = PX(LaneLeftX[c] + 34.5f);
                    // 尾端 = 離判定線最遠的那一頭（帽子焊在這裡，貼圖也以這裡為錨）。向上捲時在下方(較大 design y)，
                    // 向下捲時在上方(較小 y) —— 舊版一律取 Max，於是 向下 模式錨到了「頭」那一端，造成兩個 bug：
                    // ① 圖樣方向跟帽子相反；② 頭被按住釘在判定線、長條又長到兩端都被裁時，兩個 V 都變常數 → 整條凍住不捲動。
                    float tailY = HoldBodyUv.TailY(y, yEnd, _scrollSign);
                    if (_note3dMode)
                    {
                        // OFFICIAL cap = a welded TRIANGLE at the tail end (LONG.MSH verts 0/1/2), pointing away from the
                        // judge line — real geometry, not a sprite. The 2D sprite tail stays hidden while the 3D skin is on.
                        if (n.Tail) n.Tail.enabled = false;
                        if (n.HeadCap) n.HeadCap.enabled = false;
                        if (n.Cap3d == null && _capMeshMat != null) n.Cap3d = CreateHoldCap();
                        if (n.Cap3d != null)
                        {
                            // cap sits at the note's END on the side AWAY from the judge line (= tailY). 向下 (down-scroll):
                            // the tail is ABOVE the head → the offset pushes the other way and the triangle flips
                            // (scale.y·_scrollSign) so it still points away from the (now bottom) judge line.
                            float capBaseY = tailY + _scrollSign * note3dCapOffset;
                            float capLen = holdW * LongCapLenRatio;
                            float capFar = capBaseY + _scrollSign * capLen;   // the tip end (design y)
                            bool capVis = Mathf.Max(capBaseY, capFar) >= _clipTopY && Mathf.Min(capBaseY, capFar) <= _clipBottomY;
                            n.Cap3d.SetActive(capVis);
                            if (capVis)
                            {
                                n.Cap3d.transform.position = SdoLayout.ToWorld(cx, capBaseY, 0.6f);
                                n.Cap3d.transform.localScale = new Vector3(holdW, holdW * _scrollSign, 1f);
                            }
                        }
                    }
                    else
                    {
                        // 長條**兩端**各封一個口(官方 NOTEIMAGE.AN 的組 3 = 靠判定線端、組 4 = 尾端)。
                        // 封口的黑描邊一定朝**外**、開口那側接長條,所以同一時間兩端用的是不同朝向的那張:
                        //   在上面那一端 → 上緣封口的圖      在下面那一端 → 下緣封口的圖
                        // 這條規則與捲動方向無關(向上/向下都自動成立),因為它只看「這一端在上還是在下」。
                        //
                        // 🔴 以前只畫尾端、而且只有一張圖靠 flipY 湊另一個朝向。NOTEIMAGE_6 的帽子是 per-lane
                        //    箭頭、翻了就指錯方向,那條路只好不翻 —— 代價是向下捲時「下緣封口」正對著長條,
                        //    接縫橫著一條黑邊(使用者回報);而靠判定線那端從頭到尾沒有收邊,是硬切的斷面。
                        //    官方本來就兩個朝向都備了圖,照它畫就兩個問題一起沒了。
                        bool tailOnTop = _scrollSign < 0;
                        var capUpper = _holdCapHead[c] ?? _holdTail[c];   // 上緣封口(缺圖時退回同一張)
                        var capLower = _holdTail[c];                      // 下緣封口
                        if (n.Tail)
                        {
                            n.Tail.enabled = true;
                            n.Tail.sprite = tailOnTop ? capUpper : capLower;
                            n.Tail.flipY = _holdCapHead[c] != null
                                ? false   // 兩個朝向都有圖 → 一律不翻(per-lane 箭頭才不會被翻到指錯)
                                : HoldCapOrient.FlipY(_holdCapPerLane[c], _holdTailFlipY[c], tailOnTop);
                            PlaceAspect(n.Tail, cx, yEnd, holdW, 0.5f);
                        }
                        // 靠判定線那端。它在另一頭,所以封口朝向與尾端相反(用另一張圖)。
                        if (n.HeadCap)
                        {
                            n.HeadCap.enabled = true;
                            n.HeadCap.sprite = tailOnTop ? capLower : capUpper;
                            n.HeadCap.flipY = false;
                            PlaceAspect(n.HeadCap, cx, y, holdW, 0.5f);
                        }
                        if (n.Cap3d != null && n.Cap3d.activeSelf) n.Cap3d.SetActive(false);
                    }
                    if (n.Body)
                    {
                        float top = Mathf.Max(Mathf.Min(y, yEnd) + (_note3dMode ? note3dHoldHeadGap : 0f), _clipTopY);
                        float bot = Mathf.Min(Mathf.Max(y, yEnd), _clipBottomY);
                        float len = Mathf.Max(0f, bot - top), midY = (top + bot) / 2f;
                        n.Body.SetActive(len > 0.5f);
                        if (tintNote) { var bmr = n.Body.GetComponent<MeshRenderer>(); if (bmr && bmr.sharedMaterial) bmr.sharedMaterial.color = noteCol; }   // body follows the same tint (showtime flash / dropped-hold dim)
                        if (len > 0.5f)
                        {
                            n.Body.transform.position = SdoLayout.ToWorld(cx, midY, 0.6f);
                            n.Body.transform.localScale = new Vector3(holdW, len, 1);
                            var m = n.Body.GetComponent<MeshFilter>().mesh; var uv = m.uv;
                            // 兩條邊離**尾端**多遠（design px，恆 ≥0）：用未裁切的 tailY 當錨，長條再長、頭再怎麼被
                            // 釘在判定線上，圖樣都會跟著音符流動（見 HoldBodyUv）。
                            float dBot = HoldBodyUv.DistFromTail(tailY, bot, _scrollSign);
                            float dTop = HoldBodyUv.DistFromTail(tailY, top, _scrollSign);
                            if (_note3dMode)
                            {
                                // OFFICIAL body mapping (NoteMesh_ClampVertexAlpha_004c28d0): sample ONLY the chevron U
                                // band of LONG_0_1 (the fat outer silver rails are outside it, never drawn) and
                                // V = 1 − z·(1/31.2) with z ANCHORED AT THE TAIL (cap weld z=0.0287 → V≈0.999) — the
                                // chevrons stay glued to the cap and point the same way it does, no matter how the body is
                                // clamped/consumed.
                                float vBot = HoldBodyUv.BodyV(dBot, holdW), vTop = HoldBodyUv.BodyV(dTop, holdW);
                                uv[0].x = LongU0; uv[3].x = LongU0; uv[1].x = LongU1; uv[2].x = LongU1;
                                uv[0].y = vBot; uv[1].y = vBot; uv[2].y = vTop; uv[3].y = vTop;
                            }
                            else
                            {
                                // 2D skin: tile the body texture square along the length (拼接, not stretch).
                                // Anchor the tile phase to the (UNCLAMPED) tail, NOT to the clamped edge — otherwise, on a
                                // hold long enough that BOTH ends clamp to the clip band, the quad and its V=0..tiles UV
                                // render identically every frame and the body looks FROZEN in place. Phasing off the tail
                                // makes the pattern flow with the note. wrapMode=Repeat (set at load) tiles the out-of-range V.
                                float tileH = holdW * (_holdTex[c].height / (float)_holdTex[c].width);
                                float invTile = 1f / Mathf.Max(tileH, 1e-3f);
                                uv[0].x = 0f; uv[3].x = 0f; uv[1].x = 1f; uv[2].x = 1f;
                                uv[0].y = dBot * invTile; uv[1].y = uv[0].y;
                                uv[2].y = dTop * invTile; uv[3].y = uv[2].y;
                            }
                            m.uv = uv;
                        }
                    }
                }
            }
            // 3D-mesh heads: draw the collected note glyphs flat at their 2D positions (the 2D board + receptors + hold
            // bodies stay as they are — only the note HEAD becomes the real arrow mesh).
            if (use3d) { _highway.visible = true; _highway.SetItems(_highwayItems); }
            else if (_highway != null && _highway.visible) _highway.visible = false;
        }

        // Build the flat 3D-mesh note pool lazily. Draws in the ORTHO play field (layer 0), so it works whether or not
        // the perspective stage camera is up.
        private bool EnsureHighway()
        {
            if (_highway != null) return _highway.Ready;
            _highway = new GameObject("Note3dMeshHost").AddComponent<Note3dHighway>();
            _highway.Build(0);
            return _highway.Ready;
        }

        // ---------- input / judge ----------

        /// <summary>
        /// 一次按鍵**實際發生**的譜面時間（ms）。<c>Input.GetKeyDown</c> 是在 Update 輪詢的:鍵是在「上一幀到這一幀
        /// 之間」的某處按下的,直接拿這一幀的 now 當打擊時間會**系統性偏晚半幀** —— 60fps 平均 +8ms、30fps +16ms,
        /// 而且偏差會跟著 fps 飄(掉幀時判定莫名變嚴)。
        ///
        /// StepMania 對「沒有事件時戳的輪詢裝置」就是取上次輪詢與現在的**中點**(InputHandler.cpp:5-16,
        /// <c>di.ts = m_LastUpdate.Half()</c> —— 註解原文:「will pretend the button was pressed at the midpoint
        /// since the last update, which will smooth out the error」)。取中點後平均偏差 ≈ 0,且不隨 fps 變。
        ///
        /// (StepMania 另有專屬高優先權輸入執行緒能拿到**真**事件時戳,Player::Step 再用 tm.Ago() 回推按下當時的
        /// 音樂時間;Unity 舊版 Input 沒有時戳,中點是能做到的最好近似。osu!lazer 同樣只有幀時間,連中點都沒取。)
        /// </summary>
        private double PressTimeMs(double now) => now - 0.5 * Time.deltaTime * 1000.0;   // deltaTime 已吃 timeScale → 與譜面時間同單位

        private void HandleInput(double now)
        {
            // 聊天打字中:整片鍵盤都是文字,一顆 lane 鍵都不判定(「w」不能同時是上鍵)。這期間音符照掉、
            // 長條會斷 —— 那是使用者選 Tab 開打字時就接受的代價(送出後自動退出正是為了縮短這段)。
            if (ChatTyping) return;
            int mask = 0;
            double press = PressTimeMs(now);
            var laneKeys = laneKeyOverride ?? DefaultLaneKeys;
            for (int lane = 0; lane < Keys; lane++)
            {
                bool down = false, anyHeld = false, anyUp = false;
                foreach (var k in laneKeys[lane])
                { if (Input.GetKeyDown(k)) down = true; if (Input.GetKey(k)) anyHeld = true; if (Input.GetKeyUp(k)) anyUp = true; }
                if (anyHeld) mask |= 1 << lane;
                if (down) { PressLane(lane, press); _recDownStart[lane] = Time.time; }   // any press fires the one-shot keydown burst
                else if (_stJustEnded) ReplayShowtimeSeamPress(lane, now, anyHeld);    // ShowTime auto→manual SEAM: replay the in-window press that lost its GetKeyDown edge onto the exact note it aimed at
                if (anyUp && !anyHeld) ReleaseLane(lane, press);   // released only when no set key is still held（放開同樣是輪詢邊緣 → 同一個中點修正）
            }
            if (_stJustEnded) { _stJustEnded = false; for (int i = 0; i < Keys; i++) { _stPressMs[i] = -1.0; _stReleaseMs[i] = -1.0; _stPressNote[i] = null; } }   // seam carry-over is a one-frame event
            _replay.Record(now, mask);   // osu-style 打擊紀錄 (appends only when the held-key bitmask changes)
        }

        // Record dance-gate transitions (the effective DanceEnabled each frame). Tiny: only changes at the
        // 8-beat settle or on HP-out. Drives the result-screen BACKGROUND replay so the looped dance reproduces the
        // original performance's stop/start gaps (the DPS choreography itself is deterministic from time).
        private void RecordGate(double now)
        {
            bool g = DanceGate.Enabled(_dancing, _failed, _hpDead, danceIgnoreMiss);   // 與 DanceEnabled 同一條式子（停舞也要進 replay）
            if (_danceTrack.Count == 0 || _danceTrack[_danceTrack.Count - 1].on != g) _danceTrack.Add((now, g));
        }

        // The dance gate that was in effect at song-relative time tMs (default dancing before the first event).
        private bool GateAt(double tMs)
        {
            bool on = true;
            for (int i = 0; i < _danceTrack.Count; i++) { if (_danceTrack[i].tMs > tMs) break; on = _danceTrack[i].on; }
            return on;
        }

        private void AutoPlay(double now, bool showtime = false)
        {
            // auto-play applies the F4 "Force hit grade" if one is selected, else Perfect — so picking Cool/Bad/Miss
            // in the panel immediately drives what auto-play hits with. A Miss isn't "held"/removed: it flows off.
            // In a ShowTime window every note is a forced PERFECT (exe forces grade 4 via +0x109b0), ignoring forcedJudge.
            Judgment grade = showtime ? Judgment.Perfect : (forcedJudge >= 0 ? (Judgment)forcedJudge : Judgment.Perfect);
            // only notes whose head is due (start ≤ now) can be judged this frame — window the scan to them (held
            // holds have start in the past, so they stay in-window and their tails are still ended below).
            int hi = NoteScan.UpperBound(_noteStarts, _firstAlive, now);
            for (int i = _firstAlive; i < hi; i++)
            {
                var n = _notes[i];
                if (n.Done) continue;
                if (n.Note.IsBomb) continue;   // 炸彈自動玩時避開,不打(由 TickBombs 處理)
                if (n.Note.IsFake) continue;   // warp 掃掉的裝飾音不判定,自動玩也不打
                if (!n.HeadJudged && now >= n.Note.StartTimeMs)
                {
                    n.HeadJudged = true; ApplyEvent(grade, n.Note.Lane);
                    _recDownStart[n.Note.Lane] = Time.time;   // auto-press: fire the keydown burst (head only, never the hold tail)
                    PlayOsuHitSample(n.Note, grade);
                    if (grade == Judgment.Miss) { if (n.Note.IsHold) n.Dropped = true; }   // flows past the receptor (bar dimmed), then ScrollNotes removes it
                    else if (n.Note.IsHold) { _holding[n.Note.Lane] = n; SpawnHit3dLong(n.Note.Lane); }   // 3D: continuous HIT_LONG for the hold
                    else n.Done = true;
                }
                if (n.HeadJudged && !n.Done && grade != Judgment.Miss && n.Note.IsHold && _holding[n.Note.Lane] == n
                    && n.Note.EndTimeMs.HasValue && now >= n.Note.EndTimeMs.Value)
                {
                    // cap 被 warp 掃掉的長條結尾不判定(不進滿分分母)→ 自動玩也不能補一個評價,不然分母對不上。
                    _holding[n.Note.Lane] = null;
                    if (!n.Note.IsFakeTail) ApplyEvent(grade, n.Note.Lane);
                    EndHold(n.Note.Lane, n, n.Note.IsFakeTail ? Judgment.Perfect : grade);
                }
            }
        }

        // ShowTime driver: on SPACE (when the gauge is ready) release an auto-PERFECT window; each frame checks
        // for expiry. Called every gameplay frame after ScrollNotes and before judging. No-op unless showtimeMode.
        private void TickShowtime(double now)
        {
            if (!showtimeMode) return;
            if (_auraGo != null && _auraAnchor != null)   // official FUN_00930e50: follow dancer root X/Z, Y pinned
            {
                var src = _floorRing != null && _floorRing.Follow != null ? _floorRing.Follow.position
                          : new Vector3(_avatarChest.x, 0f, _avatarChest.z);
                _auraAnchor.transform.position = new Vector3(src.x, showtimeAuraY, src.z);
            }
            if (!_showtime.Active)
            {
                int armed = _showtime.ArmedLevel;                              // level-up cue (0x4f showtimeactive) on each new band
                if (armed > _lastArmed)
                {
                    if (!string.IsNullOrEmpty(seArm)) PlaySe(seArm);
                    _energyMiniT0 = Time.time;                                 // official 500ms EnergyProgress band-up flash
                }
                _lastArmed = armed;
                // 釋放氣條（預設 Space；鍵位可在 keymaps.ini 的 [Hotkeys] showtime 改）。打字中不吃 —— 空白鍵是文字。
                if (!ChatTyping && KeyMap.Down(Hotkey.Showtime) && _showtime.TryActivate(now, ComputeShowtimeWindowMs())) OnShowtimeStart();
            }
            else
            {
                ObserveShowtimeInput(now);                                      // record real key presses for a clean auto→manual handoff
                double rem = _showtime.RemainingMs(now);                        // pre-end warnings at exact thresholds
                if (!_warn3 && rem < 3001.0 && !string.IsNullOrEmpty(seWarn3s)) { _warn3 = true; PlaySe(seWarn3s); }
                if (!_warn07 && rem < 701.0 && !string.IsNullOrEmpty(seWarn07s)) { _warn07 = true; PlaySe(seWarn07s); }
                // Break ends BEFORE the window closes → park the dancer in IDLE REST until the window's time is up
                // (official FUN_00930400 @613781 cat 0x15 loop), then hand back to the song dance at window END
                // (OnShowtimeEnd). We do NOT chain a second break and do NOT hand to the song mid-window: the break DPS
                // stays assigned with an ever-growing dance time, so once it passes break.Total the avatar auto-plays
                // RestMot (SdoAvatar @395). The old code instead handed straight back to the song here — which for a
                // song with NO DPS nulled Dps/DanceTimeSec and left the dancer stuck on the break's last frame
                // ("卡在breaking舞蹈的最後一frame"). Break lengths ≈ the pas-sized window, so the idle tail is short.
                if (_dpsSwapped && !_breakIdled && _avatar != null && (_nowMs - _breakStartMs) >= _breakTotal * 1000.0)
                {
                    _breakIdled = true;   // latch: reached the idle tail (avatar now holds RestMot until the window ends)
                    Debug.Log("[showtime] break finished mid-window → idle rest until window end");
                }
            }
            if (_showtime.Tick(now)) OnShowtimeEnd();   // true on the single frame the window ends
            UpdateBoardPulse(now);                      // board 呼吸閃爍 (first 3s of the window)
            // note RED flash: the gold showtime note is tinted toward red over the LAST 3001ms of the window (online
            // +0x1bac8 render branch, fsin ~200ms), then reverts to the normal skin at window end. Applied in ScrollNotes.
            _noteTint = Color.white;
            if (_showtime.Active && _showtime.RemainingMs(now) < showtimeEndFlashMs)
            {
                // 1s cycle red↔yellow: at the trough (s=0) full red, at the peak (s=1) gold — one red→yellow→red per period
                float s = 0.5f + 0.5f * Mathf.Sin((float)now * (2f * Mathf.PI / Mathf.Max(1f, showtimeEndFlashPeriodMs)));
                _noteTint = Color.Lerp(showtimeEndRed, showtimeEndYellow, s);
            }
        }

        // Note-board "surround" effect during the auto-hit window (online FUN_009cc620 692184-692195, offline 104764-104822):
        // NOT an overlay/EFT — the whole board sprite's alpha is driven by a TRIANGLE WAVE 0→255→0 over a 256 ms period for
        // the FIRST 3001 ms of the window (a ~4 Hz breathe), then back to normal. White, whole board, one modulate.
        private void UpdateBoardPulse(double now)
        {
            if (_board == null) return;
            float a = 1f;
            if (_showtime.Active)
            {
                double e = _showtime.WindowMs - _showtime.RemainingMs(now);   // ms since the window opened
                if (e >= 0.0 && e < 3001.0)
                {
                    int k = (int)(e % 256.0);
                    int av = (k * 2 <= 255) ? k * 2 : 510 - k * 2;            // triangle 0→255→0, period 256 ms
                    a = av / 255f;
                }
            }
            var c = _board.color;
            if (!Mathf.Approximately(c.a, a)) { c.a = a; _board.color = c; }
        }

        // Entering the auto-PERFECT window: REPLACE the hit burst with the golden EFT_SHOWTIME flipbook (online: the
        // shared deque is swapped, not layered), swap the note board to NOTEIMAGE_SHOWTIME (offline-only — online keeps
        // the base skin; kept here as the requested "showtime note" look), fire the SHOW TIME banner + release SFX.
        private void OnShowtimeStart()
        {
            _preShowtimeNoteDir = NoteDir;   // remember the active skin (F4-selected or default) to restore on exit
            for (int i = 0; i < Keys; i++) { _stPressMs[i] = -1.0; _stReleaseMs[i] = -1.0; _stPressNote[i] = null; }   // fresh handoff latches for this window
            ApplyNoteDir(Path.Combine(SdoExtracted.Root, "NOTEIMAGE", "NOTEIMAGE_SHOWTIME"));   // golden showtime notes (online DOES swap)
            if (_showtimeHitFrames != null) { _savedBurstFrames = _burstFrames; _burstFrames = _showtimeHitFrames; _burstSwapped = true; }
            // Frida实机: release fires 0x50 showtimeboom + 0x51 electricity(loop) + 0x4e showtime. The big "SHOW TIME"
            // logo is the song-START intro (see OpeningSequence), NOT here; the release indicator is the corner lean (TODO).
            _warn3 = _warn07 = false;
            if (!string.IsNullOrEmpty(seRelease)) PlaySe(seRelease);      // 0x50 showtimeboom
            PlaySe("electricity");                                        // 0x51 electricity (loops the window in-client; one-shot here for now)
            if (!string.IsNullOrEmpty(seAnnounce)) PlaySe(seAnnounce);    // 0x4e "SHOW TIME!" voice on release (Frida: exe fires this on space)
            SwapToBreakdance();                                           // dancer → breaking_{E|N|H}_{n}.dps for the window
            SpawnShowtimeAura();                                          // star-glow aura on the dancer (online effect 0x2c = body_star)
            SpawnBoardBurst();                                            // board flash (0x2d BOOM centre + 0x27 EDGE4 lightning columns ×2)
            Debug.Log($"[showtime] release lv{_showtime.ReleasedLevel} → {_showtime.WindowMs:0}ms window, bonus ×{_showtime.BonusMultiplier}");
        }

        // Window ended: restore the pre-showtime note skin + hit burst + the song dance (there is NO bonus-tally chime).
        private void OnShowtimeEnd()
        {
            _stJustEnded = true;   // arm the auto→manual seam carry-over for this frame's HandleInput (replay held/just-pressed keys)
            if (_preShowtimeNoteDir != null) { ApplyNoteDir(_preShowtimeNoteDir); _preShowtimeNoteDir = null; }
            if (_burstSwapped) { _burstFrames = _savedBurstFrames; _savedBurstFrames = null; _burstSwapped = false; }
            if (_dpsSwapped && _avatar != null) { _avatar.Dps = _songDps; _avatar.DanceTimeSec = _songDanceTime; _dpsSwapped = false; }   // 接回原本歌曲舞蹈
            _breakIdled = false;   // reset the idle-tail latch for the next release
            ClearShowtimeWindowFx();      // dancer body_star aura + EDGE4 side lightning columns
            _lastArmed = _showtime.ArmedLevel;   // re-arm cue can fire again as energy re-climbs
            Debug.Log($"[showtime] window end — bonus so far +{_showtime.Bonus}");
        }

        // Tear down the ShowTime window's WORLD EFTs: the dancer's yellow body_star aura (0x2c) + the two EDGE4
        // side lightning columns (0x27). Called at normal window end AND from EnterResult when the song ends
        // mid-window — the note board is hidden there, so these must go too (else they linger over the result).
        private void ClearShowtimeWindowFx()
        {
            if (_auraGo != null) { Destroy(_auraGo); _auraGo = null; }   // clear the dancer aura
            if (_auraAnchor != null) { Destroy(_auraAnchor); _auraAnchor = null; }
            for (int i = 0; i < _boardBurstGos.Count; i++) if (_boardBurstGos[i] != null) Destroy(_boardBurstGos[i]);   // clear any board-burst survivors
            _boardBurstGos.Clear();
        }

        // OFFICIAL break pick (FUN_0092d280/FUN_0092d3f0): tier letter = the RELEASED ENERGY LEVEL (0→E ×2, 1→N ×4,
        // 2→H ×8 — NOT the song difficulty); the variant number was rand-rolled ONCE at song load (_breakRolls) and
        // repeats for every release in the song. Break lengths (E≈10s/N≈14s/H≈19s) match the pas-sized windows.
        private DpsLoader PickBreakDps(int level)
        {
            level = Mathf.Clamp(level, 0, 2);
            string tier = level == 0 ? "E" : level == 1 ? "N" : "H";
            int n = _breakRolls[level] > 0 ? _breakRolls[level] : 1;
            var bd = LoadAsset("DANCE/BREAKING_" + tier + "_" + n + ".DPS", b => DpsLoader.Load(b));
            return (bd != null && bd.Rows != null && bd.Rows.Length > 0) ? bd : null;
        }

        // OFFICIAL window length (FUN_00643030 @348192-348202): the tier budget (8000/12000/18000ms) rounded UP to
        // whole dance segments (pas) of chart time — the exe walks the song's pas list accumulating each segment's
        // milliseconds until the budget is reached. Typical pas = 8 beats (showtimePasBeats): reproduces the Frida
        // measurements exactly (11.9s lv0 @121bpm, 16.7s lv1 @86bpm).
        // The official's break DPS ≈ fills the pas window (short idle tail). The remake's break DPS are FIXED-length
        // (~6.8–20.1s) while the pas window scales with the SONG's BPM, so a long break can outrun a fast-song window
        // and get cut off ("動作還沒跳完 時間就結束了"). Guard: never return less than break.Total + a short idle tail,
        // so the chosen break always completes and then idles briefly before the window ends (see SwapToBreakdance).
        private double ComputeShowtimeWindowMs()
        {
            int lvl = _showtime.ArmedLevel;
            if (lvl < 0) return 0.0;
            var durs = _showtime.WindowDurationsMs;
            double budget = durs[Mathf.Clamp(lvl, 0, durs.Length - 1)];
            double bpm = _map != null && _map.Bpm > 1f ? _map.Bpm : 120.0;
            double pasMs = showtimePasBeats * 60000.0 / bpm;
            double pasWindow = pasMs <= 1.0 ? budget : System.Math.Ceiling(budget / pasMs - 1e-9) * pasMs;
            var bd = PickBreakDps(lvl);                                   // same variant SwapToBreakdance will play (_breakRolls fixed at load)
            double breakWindow = (bd != null ? bd.Total * 1000.0 : 0.0) + showtimeBreakIdleTailMs;
            return System.Math.Max(pasWindow, breakWindow);
        }

        // Enter breakdance for the window: swap the dancer to a break DPS (played once from `fromMs`). When the break
        // finishes before the window closes, TickShowtime lets it lapse into RestMot idle; the song dance is restored
        // at window end (OnShowtimeEnd).
        private void SwapToBreakdance()
        {
            if (_avatar == null || _dpsSwapped) return;   // works even if the song had no DPS (falls back on restore)
            var bd = PickBreakDps(_showtime.ReleasedLevel);
            if (bd == null) return;
            _songDps = _avatar.Dps; _songDanceTime = _avatar.DanceTimeSec; _dpsSwapped = true; _breakIdled = false;
            StartBreakSegment(bd, _nowMs);
        }

        // Play one break DPS from `fromMs`. DanceTimeSec is an unclamped elapsed-seconds function, so once it passes
        // the break's Total the avatar lapses into RestMot idle (SdoAvatar @395) — the "break ends early → idle" tail.
        private void StartBreakSegment(DpsLoader bd, double fromMs)
        {
            _breakDps = bd; _breakStartMs = fromMs; _breakTotal = bd.Total > 0.1f ? bd.Total : 1f;
            _avatar.Dps = bd;
            _avatar.DanceTimeSec = () => (float)((_nowMs - _breakStartMs) / 1000.0);
        }

        // Dancer aura for the window (online effect 0x2c = body_star.eft in this client's 3DEFT table): star twinkles
        // + streaks hugging the body. Official FUN_00930e50: position = (dancer-root X, 40, dancer-root Z) every frame,
        // uniform scale 20, scene camera. The follow anchor is FREE-STANDING (never a child of the ×22-scaled _ringTr —
        // that inherited scale used to lift the old +8 offset to +176u, three dancer-heights overhead) and is driven
        // from TickShowtime at (pelvis.x, showtimeAuraY, pelvis.z).
        private void SpawnShowtimeAura()
        {
            if (string.IsNullOrEmpty(showtimeAuraEft) || _auraGo != null) return;
            if (!_namedEftCache.TryGetValue(showtimeAuraEft, out var file))
            {
                var path = Path.Combine(SdoExtracted.Root, "3DEFT", showtimeAuraEft + ".EFT");
                if (!File.Exists(path)) { Debug.LogWarning("[showtime] aura EFT missing " + path); return; }
                file = EftFile.Load(File.ReadAllBytes(path));
                _namedEftCache[showtimeAuraEft] = file;
            }
            var pelvis = _floorRing != null && _floorRing.Follow != null ? _floorRing.Follow.position
                         : new Vector3(_avatarChest.x, 0f, _avatarChest.z);
            _auraAnchor = new GameObject("ShowtimeAuraAnchor");
            _auraAnchor.transform.position = new Vector3(pelvis.x, showtimeAuraY, pelvis.z);
            _auraGo = new GameObject("ShowtimeAura");
            _auraGo.transform.position = _auraAnchor.transform.position;
            int layer = use3dCamera ? SceneLayer : 0;
            var eff = _auraGo.AddComponent<EftEffect>();
            eff.Persistent = true;   // loops for the whole window; destroyed at OnShowtimeEnd
            eff.Init(file, showtimeAuraScale, _auraAnchor.transform, ResolveEftTex, _addMat, layer, comboBurstBright, comboGlow, comboGlowSpread, ResolveEftMesh);
            if (use3dCamera) SetLayerRecursive(_auraGo, SceneLayer);
        }

        // Board burst on activation (online 0x2d BOOM centre + 0x27 EDGE4 ×2 sides — this client's table; see the
        // field-block comment). EDGE4 loops (root life −45) = the full-height lightning columns for the whole window;
        // BOOM is the ~1s centre ring/shockwave flash. All killed at OnShowtimeEnd (official kills the handles there).
        // Rendered on the board overlay (main ortho camera, layer 0) at the official projected screen positions with
        // SortingOrder lifting them over notes/HUD (official draws this pass after the UI). No dedicated camera /
        // no cullingMask edits (an earlier attempt at that blanked the scene).
        private void SpawnBoardBurst()
        {
            if (!showtimeBoardBurst) return;
            // centre BOOM = ONE-SHOT (official plays it once on the space press — not looped for the window);
            // side EDGE4 = PERSISTENT (root loops → the full-height lightning columns stay up the whole window).
            SpawnOneBoardBurst(showtimeBurstCenterEft, showtimeBurstCenterPx, showtimeBurstCenterScale, Quaternion.Euler(90f, 0f, 0f), persistent: false);
            SpawnOneBoardBurst(showtimeBurstSideEft, showtimeBurstSide1Px, showtimeBurstSideScale, Quaternion.identity, persistent: true, speedMul: showtimeBurstSideSpeed);
            SpawnOneBoardBurst(showtimeBurstSideEft, showtimeBurstSide2Px, showtimeBurstSideScale, Quaternion.identity, persistent: true, speedMul: showtimeBurstSideSpeed);
        }

        private void SpawnOneBoardBurst(string name, Vector2 px, float scale, Quaternion rot, bool persistent, float speedMul = 1f)
        {
            if (!_namedEftCache.TryGetValue(name, out var file))
            {
                var path = Path.Combine(SdoExtracted.Root, "3DEFT", name + ".EFT");
                if (!File.Exists(path)) { Debug.LogWarning("[showtime] board-burst EFT missing " + path); return; }
                file = EftFile.Load(File.ReadAllBytes(path));
                _namedEftCache[name] = file;
            }
            var go = new GameObject("ShowtimeBurst_" + name);
            go.transform.position = SdoLayout.ToWorld(px.x, px.y, showtimeBurstZ);
            go.transform.rotation = rot;               // effect-space rotation (particles are children; billboards re-orient themselves)
            var eff = go.AddComponent<EftEffect>();
            eff.Persistent = persistent;               // false = one-shot BOOM (auto-destroys when spent); true = looping EDGE4 columns
            eff.SpeedMul = speedMul;                   // side EDGE4 lightning columns run ≥2× faster (user request); centre BOOM stays 1×
            eff.EffectName = name;
            eff.BillboardCam = _cam;                   // billboard toward the ortho overlay camera (layer 0), not the stage cam
            eff.SortingOrder = showtimeBurstOrder;     // official late pass: over notes + HUD
            eff.Init(file, scale, null, ResolveEftTex, _addMat, 0, comboBurstBright, comboGlow, comboGlowSpread, ResolveEftMesh);
            _boardBurstGos.Add(go);                    // one-shot registers too, so OnShowtimeEnd can null-check the (maybe-gone) GO
        }

        // Record real key DOWN edges DURING a ShowTime window. HandleInput isn't called here (AutoPlay forces PERFECT),
        // so Unity's per-frame GetKeyDown edge would otherwise be lost. HandleInput's seam branch replays these on the
        // frame the window ends, so a note the player pressed for near the handoff is judged instead of missed.
        private void ObserveShowtimeInput(double now)
        {
            if (ChatTyping) return;   // 打字中的按鍵是文字,不能被記成「窗內按過這條 lane」
            var laneKeys = laneKeyOverride ?? DefaultLaneKeys;
            for (int lane = 0; lane < Keys; lane++)
                foreach (var k in laneKeys[lane])
                {
                    if (Input.GetKeyDown(k)) { _stPressMs[lane] = now; _stPressNote[lane] = NearestHittable(lane, now); }   // latch the press time AND the exact note it aimed at, for a precise seam handoff
                    if (Input.GetKeyUp(k)) _stReleaseMs[lane] = now;                                                        // latch the release time so a released hold's tail is graded at the TRUE let-go, not the seam
                }
        }

        // ShowTime auto→manual SEAM replay (one seam frame only). During the window HandleInput isn't called, so a real
        // press the player made INSIDE the window — aiming at a note near the window's end — lost its GetKeyDown edge on
        // an auto frame. ObserveShowtimeInput recorded that press's EXACT target note (_stPressNote) + time (_stPressMs);
        // here we replay it onto THAT note only (never a re-searched neighbour), and only when it is still unjudged and
        // the real press-time timing is an actual hit. That is what lets the boundary tap / hold-head the player pressed
        // (and is still holding) earn its grade instead of flowing off into a MISS — without inventing phantom hits.
        private void ReplayShowtimeSeamPress(int lane, double now, bool held)
        {
            if (_holding[lane] != null) return;                    // an auto/pre-window hold is still running this lane → let it finish (don't grab a 2nd note)
            var n = _stPressNote[lane];                            // the note this in-window press aimed at (null = no real in-window press → no phantom hit from a resting/held-through key)
            if (n == null || n.Done || n.HeadJudged) return;       // already auto-perfected during the window, or never aimed → nothing to hand off
            var j = _engine.JudgeHit(n.Note.StartTimeMs, _stPressMs[lane]);   // grade at the player's REAL press time
            if (j == null || j.Value == Judgment.Miss) return;     // press too far off the aimed note → leave it for normal manual play (a fresh post-seam press), don't force a seam miss
            n.HeadJudged = true; ApplyEvent(j.Value, lane); _recDownStart[lane] = Time.time;   // keydown burst on the replayed press too
            PlayOsuHitSample(n.Note, j.Value);
            if (!n.Note.IsHold) { n.Done = true; return; }         // tap → done
            if (j.Value == Judgment.Bad) { n.BundledFail = true; n.Dropped = true; return; }   // bad hold head → never held: dimmed bar, AutoMiss fails the tail later (matches PressLane)
            if (held) { _holding[lane] = n; return; }              // still holding across the seam → hold continues (tail judged on the later real release / AutoMiss)
            if (n.Note.IsFakeTail) { EndHold(lane, n, Judgment.Perfect); return; }   // cap 被 warp 掃掉 → 結尾不判定(見 ReleaseLane)
            // player already let go INSIDE the window → judge the tail at the TRUE release time (clamped ≤ seam), not a lingering auto-Perfect and not the over-lenient seam time
            double relMs = _stReleaseMs[lane] >= 0.0 ? Math.Min(_stReleaseMs[lane], now) : now;
            var tail = _engine.JudgeHoldTail(n.Note.EndTimeMs ?? n.Note.StartTimeMs, relMs) ?? Judgment.Miss;
            ApplyEvent(tail, lane);
            EndHold(lane, n, tail);
        }

        private void PressLane(int lane, double now)
        {
            var n = NearestHittable(lane, now); if (n == null) return;
            Judgment jv;
            if (forcedJudge >= 0) jv = (Judgment)forcedJudge;                         // debug: force a grade on the hit
            else { var j = _engine.JudgeHit(n.Note.StartTimeMs, now); if (j == null) return; jv = j.Value; }
            n.HeadJudged = true; ApplyEvent(jv, lane);
            PlayOsuHitSample(n.Note, jv);
            if (jv == Judgment.Miss) { if (n.Note.IsHold) n.Dropped = true; }   // keep flowing past the receptor (dimmed if it's a bar); ScrollNotes removes it off the top
            else if (n.Note.IsHold) { if (jv == Judgment.Bad) { n.BundledFail = true; n.Dropped = true; } else { _holding[lane] = n; SpawnHit3dLong(lane); } }   // Bad head = never held → dimmed bar; 3D: continuous HIT_LONG for the hold
            else n.Done = true;
        }

        private void ReleaseLane(int lane, double now)
        {
            var n = _holding[lane]; if (n == null) return;
            _holding[lane] = null;
            // cap 被 warp 掃掉的長條:結尾**不判定**(見 OsuHitObject.IsFakeTail)。放開得再早也不算 Bad/Miss ——
            // 播放頭永遠不會經過那個放開時刻,玩家沒有「按對結尾」的機會。整條不設 Dropped(不調暗),就照原亮度
            // 繼續往判定線外流,ScrollNotes 流出畫面時收掉。
            if (n.Note.IsFakeTail) { StopHit3dLong(lane); return; }
            var tail = _engine.JudgeHoldTail(n.Note.EndTimeMs ?? n.Note.StartTimeMs, now) ?? Judgment.Miss;
            ApplyEvent(tail, lane);
            EndHold(lane, n, tail);
        }

        // A hold stops being held. Two outcomes:
        //   COMPLETED (Perfect/Cool tail) → the official LnEnd burst at the receptor, note retired.
        //   DROPPED   (Bad/Miss tail = let go off the tail time, or never released at all) → the bar is NOT deleted: it
        //             keeps scrolling at holdDropDim brightness (ScrollNotes) until it flows off the board, then retires.
        // Either way the 3D skin's looping HIT_LONG is torn down (→ its own HIT_SUO terminator).
        private void EndHold(int lane, RuntimeNote n, Judgment tail)
        {
            StopHit3dLong(lane);
            if (tail == Judgment.Bad || tail == Judgment.Miss) { n.Dropped = true; return; }
            SpawnLnEndBurst(lane);
            n.Done = true;
        }

        private RuntimeNote NearestHittable(int lane, double now)
        {
            RuntimeNote best = null; double bestAbs = double.MaxValue;
            // a hittable head is within ±MissBoundary of now; notes past now+MissBoundary can't be nearer, so bound
            // the scan there (the late side, now−MissBoundary, is already ≥ the first still-live note).
            int hi = NoteScan.UpperBound(_noteStarts, _firstAlive, now + _engine.Windows.MissBoundary);
            for (int i = _firstAlive; i < hi; i++)
            {
                var n = _notes[i];
                // 炸彈不當一般 note 判定;warp(負 BPM)掃掉的裝飾音也不判定 —— 播放頭是瞬間跳過那段拍子的,不用打
                if (n.Done || n.HeadJudged || n.Note.IsBomb || n.Note.IsFake || n.Note.Lane != lane) continue;
                double d = Math.Abs(n.Note.StartTimeMs - now);
                if (d < bestAbs && d <= _engine.Windows.MissBoundary) { bestAbs = d; best = n; }
            }
            return best;
        }

        private void AutoMiss(double now)
        {
            // everything AutoMiss acts on (a passed unhit head, a bundled-fail tail, a held hold's end) has start ≤ now;
            // future notes have nothing to miss yet, so window the scan to start ≤ now.
            int hi = NoteScan.UpperBound(_noteStarts, _firstAlive, now);
            for (int i = _firstAlive; i < hi; i++)
            {
                var n = _notes[i];
                if (n.Done) continue;
                if (n.Note.IsBomb) continue;   // 炸彈不會 miss(避開才對);由 TickBombs 處理
                if (n.Note.IsFake) continue;   // warp 掃掉的裝飾音不會 miss(打不到也不用打);流出畫面時由 ScrollNotes 收掉
                // head never pressed: miss the head (+ the tail, for a bar), then keep flowing off the top — a bar the
                // player never owned scrolls on DIMMED (holdDropDim), same as one dropped mid-way.
                // (cap 被 warp 掃掉的長條只 miss 頭部 —— 結尾不在滿分分母裡,補一次 Miss 會多扣一下。)
                if (!n.HeadJudged && _engine.HasPassed(n.Note.StartTimeMs, now)) { n.HeadJudged = true; ApplyEvent(Judgment.Miss); if (n.Note.IsHold) { if (!n.Note.IsFakeTail) ApplyEvent(Judgment.Miss); n.Dropped = true; } continue; }
                // bad head → the tail misses too once it passes. Score it ONCE (clear the flag), but do NOT retire the note:
                // the dimmed bar keeps scrolling like every other failed hold, and ScrollNotes retires it off the board.
                if (n.BundledFail && n.Note.EndTimeMs.HasValue && _engine.HasPassed(n.Note.EndTimeMs.Value, now)) { if (!n.Note.IsFakeTail) ApplyEvent(Judgment.Miss); n.BundledFail = false; continue; }
                // A long note's END is judged on the RELEASE — a real release inside the (widened) tail window is
                // graded by ReleaseLane. Holding through without letting go earns NOTHING: once the tail release window
                // has fully passed with the key still held, the tail is a MISS. Gate on the TAIL boundary (not the press
                // boundary), else a note held into the extra tail leniency is force-missed before its release could score.
                // cap 被 warp 掃掉的長條例外:按著頭一路撐到 cap 那一瞬間就算完成(StepMania Player.cpp:407 的
                // HNS_OK),不判定、也不 miss —— 只放 LnEnd 特效並收掉整條。
                if (_holding[n.Note.Lane] == n && n.Note.EndTimeMs.HasValue && _engine.HoldTailHasPassed(n.Note.EndTimeMs.Value, now))
                {
                    _holding[n.Note.Lane] = null;
                    if (n.Note.IsFakeTail) { EndHold(n.Note.Lane, n, Judgment.Perfect); continue; }
                    ApplyEvent(Judgment.Miss); EndHold(n.Note.Lane, n, Judgment.Miss);   // never released → tail miss
                }
            }
        }

        // 炸彈 (note_type 1 = avoid-note) 引爆判定 —— 照 StepMania (YHANIKI) 官方 PlayerMinus::CrossedMineRow
        // (src/Player.cpp:1077):炸彈**只在通過判定線的那一幀**檢查該軌鍵是否正被按著(IsButtonDown);
        // 按著 → 引爆(mine 音 + 扣血),沒按 → 那一刻就永久算安全避開,之後手指再壓也不會回頭補炸。
        //
        // 關鍵:這不是對稱 ±窗、也不是「窗內任一幀按到就炸」。舊版用 ±(Perfect×0.8) 窗會誤爆:
        //   (1) 炸彈還在判定線上方 ~48ms 就炸,看起來像「還沒到就爆」;
        //   (2) 你其實是提早按下一顆音符,卻落在前一顆炸彈的窗內 → 被當成踩雷。
        // 官方模型只認「炸彈抵達判定線的瞬間你的腳在不在上面」,兩個誤爆都不會發生。
        // (StepMania 另有一條 Step 新按下路徑,但它只認「離按下點最近的音符剛好是炸彈」;近處有真音符時
        //  炸彈會被讓過。跨線瞬間的按著檢查已涵蓋「站在上面踩爆」,又不會把打鄰近音符的按鍵誤判成踩雷,故從略。)
        //
        // 唯一的例外是 **warp 內的炸彈**:那裡官方的 Step 路徑不是誤爆來源,而是整個 gimmick 的本體
        // (按住穿過 warp = 自動打擊)。它只對 IsFake 的炸彈開,一般炸彈維持上面的模型 —— 見 WarpMineStep。
        //
        // detonate=false(F8 自動打擊 / ShowTime / 已陣亡):自動避雷 —— 照樣推進跨線游標與退場,但不引爆。
        // 編輯器不判定 → 不呼叫這裡,炸彈只是照 ScrollNotes 顯示/流過。
        private void TickBombs(double now, bool detonate)
        {
            if (ChatTyping) detonate = false;   // 打字中按到的鍵是文字 —— 跟 F8 自動避雷同樣待遇,不引爆
            double retire = _engine.Windows.MissBoundary;   // 退場邊界:過判定線這麼久才收掉(視覺續捲到此,不算 miss)
            if (!_bombPrevValid) { _bombPrevNow = now; _bombPrevValid = true; }   // 第一幀對齊:prev==now → 沒有任何跨線
            double prev = _bombPrevNow;
            _bombPrevNow = now;                             // 每幀都推進,detonate=false 時也要,才能把自動避雷期間的跨線消化掉
            int hi = NoteScan.UpperBound(_noteStarts, _firstAlive, now + retire);
            var laneKeys = laneKeyOverride ?? DefaultLaneKeys;
            for (int i = _firstAlive; i < hi; i++)
            {
                var n = _notes[i];
                if (n.Done || !n.Note.IsBomb) continue;
                double t = n.Note.StartTimeMs;
                // 早已通過 → 消失。warp 內的炸彈例外:它和同一批 warp 裝飾音是一起被超高速刷過判定線的,
                // 退場交給 ScrollNotes(流出畫面才收,見那裡的 IsFake 分支),才不會只有炸彈提早幾百 ms 憑空消失。
                if (now - t > retire) { if (!n.Note.IsFake) n.Done = true; continue; }
                if (!detonate) continue;                             // 自動避雷:只推進/退場,不引爆
                if (!(prev < t && t <= now)) continue;               // 只在「這一幀剛跨過判定線」時檢查一次(嚴格 < 防重複)
                bool held = false;
                foreach (var k in laneKeys[n.Note.Lane]) if (Input.GetKey(k)) { held = true; break; }
                if (!held) continue;
                if (n.Note.IsFake) WarpMineStep(n, now);             // warp 內的炸彈是**觸發器**,見 WarpMineStep
                else ExplodeBomb(n);                                 // 跨線瞬間手指壓在該軌上 → 引爆(= CrossedMineRow + IsButtonDown)
            }
        }

        // 「按住穿過 warp 會自動打擊」—— StepMania 的炸彈在 warp 裡是**觸發器**,不是目標。
        // 官方鏈路(Player.cpp):
        //   1. Update:458 `for(; m_iMineRowLastCrossed <= iRowNow; ++) CrossedMineRow(...)` —— warp 讓 beat 一瞬間
        //      跳過幾百拍,這個迴圈會把中間**每一個 row 逐一補呼叫**,所以一幀內觸發幾十次;
        //   2. CrossedMineRow:1077 —— 註解寫的是「Hold the panel while crossing a mine will cause the mine to
        //      explode」,但它按住時呼叫的是 `Step(t, now)`,也就是**完整的按鍵判定流程**,不是只引爆;
        //   3. Step:662 —— `GetClosestNote` 撿該軌最近的**還沒判定**的音符,照 GetElapsedTimeFromBeat 算誤差給分。
        // 譜面長這樣([blue]Dreadnought 的 gimmick 段,[blue]bbkkbkk beat 95 也是同一招):同一軌上每 78ms
        // 一組「warp 內的炸彈 + 一條 78ms 短長條」,炸彈與長條頭**落在同一個判定時刻**,連成一長串。
        // 玩家按住不放時沒有新的 keydown,那一串長條頭本來永遠沒人判定 → 全部 miss;炸彈就是用來**補按**的。
        //
        // 為什麼官方撿到的是長條頭、不是炸彈(所以**不會爆炸**):GetClosestNote 從「現在的 beat」往外找第一顆
        // 還沒判定的音符。長條頭就在落地拍上(距離 0),炸彈在 warp 內側(beat 更遠),所以先撿到長條頭。
        // 正在按著的前一條長條頭早就判定過了,GetClosestNoteDirectional 的 `GetTapNoteScore != TNS_NONE`
        // 會跳過它 —— 官方不需要、也沒有「這一軌正被佔用」的概念。
        //
        // **觸發什麼由落地點決定**:接回正 BPM 的那個位置上是 tap 就判 tap、是長條就判長條頭、是炸彈就爆炸,
        // 那裡什麼都沒有就**空轉**(絕不能拿觸發器自己來爆 —— 大多數 warp 炸彈只是被跳過的裝飾牆)。
        // 這也正是官方 GetClosestNote 的結果:它從「現在的 beat」往外找第一顆還沒判定的音符,落地拍上的距離 0
        // 必然最先撿到;撿到 mine 才走 Step 的 mine 分支爆炸,撿不到就 score = TNS_NONE 什麼也沒發生。
        // 實測落地點分布 —— Dreadnought 213/216 是長條;bbkkbkk 15 個 tap、2 個長條、1 個炸彈,其餘 874 是空的;
        // Elisha 2765 顆全部是空的(那面炸彈牆純粹是視覺裝飾)。
        //
        // 和官方的兩點差異(都是為了不破壞 remake 既有的不變式):
        //  • 落地點只認**非 IsFake** 的音符。warp 內的裝飾音不在滿分分母裡(OsuBeatmap.TotalNotes 排除 IsFake),
        //    判定它們會多出分子、打破滿分;而且它們是「被跳過的那段」,本來就不是「接回去」的位置。
        //  • 換手前要先把該軌正按著的長條收尾。官方每條 hold 各自由 IsButtonDown 續命,remake 只有單一
        //    `_holding[lane]` 插槽,直接被 PressLane 覆蓋的話舊那條就成了沒人判定的孤兒。這裡走 ReleaseLane
        //    (等同「放開舊的、按下新的」),而這串 gimmick 長條的 cap 全都落在下一段 warp 裡 → IsFakeTail
        //    → ReleaseLane 乾淨放手、不判定也不扣分,正是官方 hold 不必放開就算完成的效果。
        //    (實測 Dreadnought 213 顆目標全撿得到,舊長條收尾 211 顆、來不及 0 顆。)
        private void WarpMineStep(RuntimeNote bomb, double now)
        {
            var target = LandingNote(bomb);
            if (target == null) return;                                   // 落地點是空的 → 空轉,不爆炸
            if (target.Note.IsBomb) { ExplodeBomb(target); return; }      // 落地點是炸彈 → 爆的是**那一顆**
            int lane = bomb.Note.Lane;
            if (_holding[lane] != null) ReleaseLane(lane, now);           // 舊長條先收尾,再把該軌交給新的
            PressLane(lane, now);   // = 官方的 Step():照真實誤差給分(落地點與觸發器同時刻 → 誤差只有一幀)
            // 觸發器本身不動:官方的 Step 撿到的是別顆音符時,這顆炸彈原封不動留在譜上照樣畫出來
            // (只有 GetClosestNote 真的撿到它才會 SetTapNote(TAP_EMPTY))。跨線那一幀只成立一次,不會重複觸發。
        }

        /// <summary>warp「接回正 BPM 的那個位置」上、與觸發器同軌的那顆音符(還沒判定的)。判定時刻和觸發器
        /// 完全相同 —— warp 內的東西被壓在同一個瞬間,而落地拍就是那個瞬間的出口。IsFake 的不算(那是被跳過的
        /// 裝飾,不是接回去的位置)。找不到 = 落地點是空的。</summary>
        private RuntimeNote LandingNote(RuntimeNote bomb)
        {
            int t = bomb.Note.StartTimeMs;
            int hi = NoteScan.UpperBound(_noteStarts, _firstAlive, t);   // 落地點的 StartTimeMs 正好等於 t
            for (int i = _firstAlive; i < hi; i++)
            {
                var n = _notes[i];
                if (n.Done || n.HeadJudged || n.Note.IsFake) continue;
                if (n.Note.Lane == bomb.Note.Lane && n.Note.StartTimeMs == t) return n;
            }
            return null;
        }

        // 踩到炸彈的代價**只有扣血**(等同一次 Miss 的 HP 量),其餘一律不動:不斷 combo、不計 miss、
        // 不進判定統計/分數、不彈判定字樣、不觸發整排紅閃、不算跳舞判定的 break、不影響 ShowTime 氣條。
        // 所以它不走 ApplyEvent(那是「判定」的入口),直接扣 HP —— 死亡照樣由 Update 的 _health.IsFailed 接手。
        // 回饋只留爆炸特效 + 踩雷音(比照 StepMania HitMine,雷本來就不出判定)。
        private void ExplodeBomb(RuntimeNote n)
        {
            PlaySe(MineSeName);                       // StepMania theme 的爆炸音 (DATA/SE/player_mine.wav)
            SpawnBombExplosion(n.Note.Lane);          // StepMania 的 HitMine 爆炸圖 (不是受擊線按下動畫)
            _health.Apply(Judgment.Miss);             // 只扣血 (level 0 = -50);combo/統計完全不受影響
            n.Done = true;                            // 引爆後移除
        }

        // 引爆特效：在判定線該軌位置放一張 StepMania HitMine 爆炸圖,放大+淡出後移除。上層(order 8)、吃 note board mask。
        private void SpawnBombExplosion(int lane)
        {
            if (_bombExplodeSprite == null)
                _bombExplodeSprite = SdoExtracted.LoadImage(Path.Combine(SdoExtracted.Root, "NOTEIMAGE"), "BOMB_EXPLODE.png", bleed: true);
            if (_bombExplodeSprite == null) return;
            var sr = NewSR("BombExplode", _bombExplodeSprite, 8);
            sr.transform.SetParent(NoteVisualRoot, false);
            sr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;
            // StepMania 的 HitMineCommand 第一件事就是 blend,add —— 這張圖是黑底無 alpha,靠 ADDITIVE 讓黑變透明
            // (用 Sprites/Default alpha-blend 就會看到黑方塊)。Legacy Particles/Additive 是 2×tint×tex,_TintColor 0.5 = 1× 中性。
            var sh = Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Sprites/Default");
            var mat = new Material(sh);
            // Legacy additive = 2 × _TintColor × tex × vertexColor;0.5 = 1× 中性 → ×gain 才不會被亮譜面板吃掉。
            // (vertexColor 走 SpriteRenderer.color,會被夾在 0..1,所以增益只能加在 _TintColor 上。)
            float g = Mathf.Max(0f, bombExplodeGain) * 0.5f;
            if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", new Color(g, g, g, 0.5f));
            sr.sharedMaterial = mat;
            StartCoroutine(BombExplodeCo(sr, lane));
        }

        // 逐字照 StepMania noteskin metrics 的 HitMineCommand：
        //   blend,add; diffuse,1,1,1,1; zoom,1; rotationz,0; linear,0.3; rotationz,90; linear,0.3; rotationz,180; diffusealpha,0
        // → 大小固定(不放大)、0°→180° 等速轉(300°/s)、後半段才淡出;全長 0.6s。
        private IEnumerator BombExplodeCo(SpriteRenderer sr, int lane)
        {
            const float dur = 0.6f;   // linear,0.3 ×2
            float cx = PX(LaneLeftX[lane] + LaneCx0), cy = judgeLineY + judgeOffsetY;
            float w = NoteW * bombExplodeZoom;   // zoom 固定
            for (float t = 0f; t < 1f; t += Time.deltaTime / dur)
            {
                float a = t <= 0.5f ? 1f : Mathf.Max(0f, 1f - (t - 0.5f) * 2f);   // 前半全亮,後半 diffusealpha→0
                sr.color = new Color(a, a, a, 1f);                                 // additive:壓 RGB 就是淡出
                PlaceAspect(sr, cx, cy, w, -0.4f);
                sr.transform.localRotation = Quaternion.Euler(0f, 0f, -Mathf.Lerp(0f, 180f, t));
                yield return null;
            }
            if (sr != null) Destroy(sr.gameObject);
        }

        // 一次「判定」的統一出口:計分/扣血/氣條/表情/跳舞判定/判定字樣/特效全在這裡。
        // (炸彈不是判定,不走這裡 —— 它只扣血,見 ExplodeBomb。)
        private void ApplyEvent(Judgment j, int lane = -1)
        {
            _score.Apply(j);
            NotifyLocalComboMilestone(j);
            _health.Apply(j);
            if (showtimeMode) _showtime.OnJudge(j);                               // ShowTime: fill the gauge (normal) or accrue the bonus (in a window)
            UpdateEmojiOnJudge(j);                                                // combo-milestone / consecutive-miss emoji cut-ins
            _blockHadNote = true;                                                // a note was judged this block (-> not an empty block)
            if (j == Judgment.Bad || j == Judgment.Miss) _blockHadBreak = true;   // break -> NOT stopped now; the dancer is re-decided at the next 8-beat settlement
            _judgeWord.sprite = _judgeSprites[(int)j]; _judgeWordAt = Time.time;
            if (lane >= 0 && (j == Judgment.Perfect || j == Judgment.Cool))   // tap: fire immediately, may overlap
            {
                if (_hit3dMode) SpawnHit3d(lane);                              // 3D skin: real AU_HIT.EFT burst at the receptor
                else if (_burstFrames != null) SpawnBurst(lane);              // 2D skins: sprite flipbook burst (during a window _burstFrames IS the EFT_SHOWTIME set)
            }
            // 3D skin: the official has NO lane click-strip glow on press and NO red board flash on miss — suppress both.
            if (lane >= 0 && j != Judgment.Miss && !_note3dMode) TriggerClickFlash(lane);   // light the struck lane's click strip (any contact, not a miss)
            if (j == Judgment.Miss && !_note3dMode) TriggerMissFlash();
        }

        // Every 8 beats (the score-settlement cadence) re-decide whether the dancer keeps dancing — a break NEVER
        // stops it mid-block, only this boundary does. Rules live in Sdo.Ruleset.DanceGate.NextState:
        //   1. block had a break (Bad/Miss) -> dance only if the current combo is still > 30, else stop.
        //   2. block had NO break but DID judge notes -> dance (clean block always dances, even at low combo).
        //      No break and NO notes at all -> keep the current state (a stopped dancer does not resume on silence).
        //   (danceIgnoreMiss 開著 -> 有判定的 block 一律跳，忽略 1.)
        // while() so a long frame that skips a boundary still settles. _dancing is read by the avatar each frame.
        private void UpdateDanceGate(double now)
        {
            // 規則本體在 Sdo.Ruleset.DanceGate —— **遠端舞者用的是同一個函式**(見那邊的註解:
            // 各寫一份的話門檻一改,別人畫面上的舞者就會靜默對不上,而且沒有測試抓得到)。
            double settleMs = Sdo.Ruleset.DanceGate.SettleMs(_map.Bpm);   // 8 beats = 2 bars, same as the score commit
            if (_nextDanceSettleMs <= 0) _nextDanceSettleMs = settleMs;
            while (now >= _nextDanceSettleMs)
            {
                // 決策本體是純函式（Sdo.Ruleset.DanceGate）：斷了 → combo > 30 才續跳；乾淨且有音符 → 跳；
                // 空 block → 維持現況。danceIgnoreMiss 開著時「斷了就停」那條整個豁免（見欄位註解）。
                _dancing = DanceGate.NextState(_dancing, _blockHadBreak, _blockHadNote, _score.Combo, danceIgnoreMiss);
                _blockHadBreak = false;
                _blockHadNote = false;
                _nextDanceSettleMs += settleMs;
            }
        }

        private const float BurstWidth = 235f;            // hit-burst draw size for the REFERENCE skin (EFT_13, 300px native)
        private const float BurstNativeRef = 300f;        // EFT_13 native px — bursts render native-proportional to this so
                                                          // a smaller skin (EFT_2=150, EFT_14=128) draws smaller, not stretched up to BurstWidth
        // Every burst is a ONE-SHOT that may overlap others on the same lane (no gating) — a long note's head is just a
        // normal hit, so it gets exactly this and nothing else. Each burst gets its OWN material clone so overlapping
        // bursts never bleed.
        private void SpawnBurst(int lane)
        {
            // directional skins (PET/8/9/10) ship separate frames for left-right vs up-down lanes; lanes 1(down)/2(up) use
            // the _ud set, lanes 0(left)/3(right) use _rl (_burstFrames). Non-directional skins leave _burstFramesUD null.
            var frames = (_burstFramesUD != null && (lane == 1 || lane == 2)) ? _burstFramesUD : _burstFrames;
            SpawnBurstFrames(lane, frames);
        }

        // Spawn an arbitrary flipbook at the lane's receptor (hit burst, or the long-note LnEnd burst). sizeMul/speedMul/
        // brightMul scale THIS burst only; doubleLayer=false draws a SINGLE additive layer (the LnEnd burst — the hit
        // burst's 2-layer stack is a deliberate over-bright punch that makes the LnEnd art bloom all over the lane).
        private void SpawnBurstFrames(int lane, Sprite[] frames,
                                      float sizeMul = 1f, float speedMul = 1f, float brightMul = 1f, bool doubleLayer = true)
        {
            if (frames == null || frames.Length == 0) return;
            var mat = _matPool.Count > 0 ? _matPool.Pop() : (_addMat != null ? new Material(_addMat) : null);  // own instance, pooled
            // brightness: the additive shader is Blend SrcAlpha One, and its _TintColor defaults to (.5,.5,.5,.5) ->
            // the .5 alpha halves the burst (too dark). Drive _TintColor by burstBright (1.0 = stock, higher = brighter).
            if (mat != null) { float t = 0.5f * burstBright * brightMul; mat.SetColor("_TintColor", new Color(t, t, t, Mathf.Clamp01(t))); }
            var sr = NewSR("Burst", frames[0], 6);
            if (mat != null) sr.sharedMaterial = mat;                   // additive -> black bg becomes transparent glow
            // native-proportional: scale by THIS skin's frame size vs the reference, so every skin keeps its true relative
            // size (the old fixed BurstWidth stretched a small 150px skin up to the 300px skin's footprint -> "too big").
            float burstNativeW = frames[0] != null ? frames[0].rect.width : BurstNativeRef;   // native px (PPU-independent)
            PlaceAspect(sr, PX(LaneLeftX[lane] + LaneCx0), judgeLineY, BurstWidth * burstSize * sizeMul * (burstNativeW / BurstNativeRef));
            SpriteRenderer sr2 = null;
            if (doubleLayer)
            {
                sr2 = NewSR("Burst+", frames[0], 6);                   // 2nd additive layer -> vivid in-game glow
                // INVARIANT: both layers must always be set to the SAME sprite (see the advance in
                // ScreenGameplay.Effects: `fx.Sr.sprite = spr; if (fx.Sr2) fx.Sr2.sprite = spr;`). They share ONE
                // material instance, and a shared instance across DIFFERENT textures makes both draw the last-written
                // one — offsetting layer 2 by a frame for a trail effect would silently corrupt the animation.
                if (mat != null) sr2.sharedMaterial = mat;
                sr2.transform.SetParent(sr.transform, false);
            }
            _fx.Add(new BurstFx { Sr = sr, Sr2 = sr2, Mat = mat, Start = Time.time, Frames = frames,
                                  SecPerFrame = BurstSecPerFrame / Mathf.Max(0.01f, speedMul) });
        }

        private sealed class RuntimeNote
        {
            public readonly OsuHitObject Note;
            public readonly int ColorFamily;   // 3D-note beat-quantization colour (0=magenta,1=blue,2=green); used only in _note3dMode
            public bool HeadJudged, BundledFail, Done;
            public bool Dropped;       // a hold the player never owned: head missed / head Bad / let go mid-bar. The bar is
                                       // NOT deleted — it keeps scrolling at holdDropDim brightness until it flows off.
            // Visuals are POOLED (see NoteVisual): a note owns GameObjects ONLY while it is on-screen — rented in
            // ScrollNotes when it enters the board, returned when it scrolls off or is retired. A 10k-note chart
            // therefore keeps only the ~visible window (≈100) worth of sprites alive at once instead of 10k.
            // Head/Body/Tail/Cap3d mirror Vis's parts while rented (null when off-screen) so the per-frame render
            // code reads them exactly as before; the `if (n.Body)` / `if (n.Tail)` guards already handle null.
            public NoteVisual Vis;
            // Tail = 尾端封口;HeadCap = 靠判定線那端的封口(官方兩端各一個槽位,見 CapSlotHasArt)。
            public SpriteRenderer Head, Tail, HeadCap;
            public GameObject Body, Cap3d;
            public RuntimeNote(OsuHitObject n, int colorFamily) { Note = n; ColorFamily = colorFamily; }
        }

        // A reusable bundle of the GameObjects one on-screen note needs. Rented from _visualFree when a note enters
        // the visible window and returned when it leaves. Body/Tail/Cap3d are built lazily the first time a hold on
        // this bundle needs them, then kept for reuse (a later tap simply leaves them inactive). Each Head/Tail owns
        // its OWN material so masked sprites never batch-bleed textures (see the SpriteMask material note).
        private sealed class NoteVisual
        {
            // Tail = 尾端(離判定線最遠)的封口;HeadCap = 靠判定線那端的封口。官方兩端各有一個槽位,
            // 有些 skin 只用其中一個、NOTEIMAGE_8 兩個都不用(見 CapSlotHasArt),所以 HeadCap 常是 null。
            public SpriteRenderer Head, Tail, HeadCap;
            public GameObject Body, Cap3d;
            public MeshFilter BodyMf; public MeshRenderer BodyMr;
        }
    }
}
