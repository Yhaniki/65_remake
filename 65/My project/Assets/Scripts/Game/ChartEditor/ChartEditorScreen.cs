using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Sdo.Osu;
using Sdo.Ruleset;
using Sdo.Settings;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Sdo.Game
{
    /// <summary>
    /// 譜面（.gn）編輯器 —— 第一版：唯讀預覽。
    ///
    /// 畫面本體直接重用 <see cref="ScreenGameplay"/>（editorMode）：一樣的音符板、受擊線、note 皮、捲動/變速數學，
    /// 只是不載場景與舞者（＝純黑背景）、不判定也不結算，時間可以自由拖。旁邊多一條跟著捲動的音樂波形
    /// （<see cref="ChartEditorOverlay"/>）與小節/拍格線，方便對拍。
    ///
    /// 進入方式：Unity 編輯器 <c>Tools ▸ SDO ▸ Boot Into Chart Editor</c>（寫 EditorPrefs），或執行檔設環境變數
    /// <c>SDO_EDITOR=1</c>／<c>SDO_EDITOR=sdom1197k.gn</c>（見 <see cref="ScreenGameplay.DevVar"/>）。
    /// 進去之後按 F1 開歌單，隨時換另一首歌的 k.gn 繼續看。
    ///
    /// 之後要加「編輯」時的落點都留好了：時間軸/幾何走 ScreenGameplay 的 Editor* API，格線用 <see cref="BeatGrid"/>；
    /// 存檔預計以原加密（sdom/rewu/ddrm）覆蓋原檔並自動備份 .bak —— 那需要一個 frame-level 的 .gn 讀寫模型
    /// （目前 <see cref="GnChart"/> 只解析成 OsuBeatmap，是單向的）。
    /// </summary>
    public sealed class ChartEditorScreen : MonoBehaviour
    {
        public const string EnvVar = "SDO_EDITOR";
        /// <summary>上次開的那首（PlayerPrefs key）。沒有紀錄時才會退回 <see cref="PickDefaultGn"/>（編號最大的那首）。</summary>
        public const string PrefLastGn = "sdo.editor.lastGn";
        private const string PrefLastDiff = "sdo.editor.lastDiff";
        // 波形每格 2ms：一般速度下 1 格 ≈ 1 螢幕像素，波形才不會一格一格像階梯（10ms 一格 ≈ 5px，明顯粗）。
        // 4 分鐘的歌 = 12 萬格 × 2 條（RMS/峰值）× 4B ≈ 1MB，可以接受。畫的時候會依當下縮放把不到 1px 的格併起來。
        private const double WaveBucketMs = 2.0;

        public static ChartEditorScreen Instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (string.IsNullOrEmpty(ScreenGameplay.DevVar(EnvVar))) return;
            if (Instance != null) return;
            var go = new GameObject("ChartEditor");
            Instance = go.AddComponent<ChartEditorScreen>();
            DontDestroyOnLoad(go);
        }

        private ScreenGameplay _game;
        private ChartEditorOverlay _overlay;
        private HashSet<GameObject> _preRoots;      // 建立遊玩畫面之前就存在的場景根物件 → 換歌時只拆新長出來的
        private Coroutine _peaksCo;

        private SongCatalog.Entry _entry;
        private string _gn = "";
        private int _diff;                          // 0=easy 1=normal 2=hard
        private float _speed = 1.5f;                // 音符下落速度檔位（＝房間的「速度」）
        private string _status = "";
        private bool _loading;
        private BeatGrid _grid;                     // 依 map 快取（OnGUI/按鍵每幀會問好幾次）
        private OsuBeatmap _gridFor;

        // 打拍測試（校時）
        private bool _beatTest;
        private float _beatBpm = 120f;
        private float _beatDiv = 1f;                // 每幾拍一顆（1 = 4 分音符）
        private float _beatBpmPending = 120f;       // 滑桿上的值；放開滑鼠才真的重建（見 Update）
        private float _beatDivPending = 1f;
        private int _misses;
        private readonly HitErrorStats _stats = new HitErrorStats();
        private double _globalOffset = RoomConfig.globalOffsetMs;   // 判定 offset（毫秒）
        private float _judgeOffsetY = RoomConfig.judgeOffsetY;      // 判定線視覺偏移（設計 px）
        private double _songOffset;                                 // 這首歌自己的 offset（毫秒，F11/F12）

        // 歌單視窗
        private bool _showList;
        private string _search = "";
        private Vector2 _listScroll;
        private readonly List<SongCatalog.Entry> _filtered = new List<SongCatalog.Entry>();
        private string _filterFor = null;

        private void Start()
        {
            Application.runInBackground = true;
            string want = ScreenGameplay.DevVar(EnvVar) ?? "";
            string gn = want.EndsWith(".gn", StringComparison.OrdinalIgnoreCase) ? want : PlayerPrefs.GetString(PrefLastGn, "");
            _diff = Mathf.Clamp(PlayerPrefs.GetInt(PrefLastDiff, 0), 0, 2);
            if (string.IsNullOrEmpty(gn) || !File.Exists(SongPaths.Gn(gn) ?? "")) gn = PickDefaultGn();
            if (string.IsNullOrEmpty(gn)) { _status = "找不到任何可開的 .gn（song_catalog.json 或 MUSIC 資料夾是空的）"; _showList = true; return; }
            LoadSong(gn, _diff);
        }

        // 預設要開哪一首：編號（fileId）最大的那首 —— 也就是最後匯入的新歌，通常就是正要看的那首。
        // 條件是「有譜」而且「DATA 樹裡真的有檔案」：目錄有 4346 筆，但有些歌的檔案不在這棵樹裡
        // （例如第一筆 sdom0.gn 新手教学），只看目錄會開出一片空白。
        private static string PickDefaultGn()
        {
            SongCatalog.Entry best = null;
            foreach (var e in SongCatalog.All)
            {
                if (best != null && e.fileId <= best.fileId) continue;   // 先比編號，再去碰硬碟
                if (!(e.HasChart(0) || e.HasChart(1) || e.HasChart(2))) continue;
                var p = SongPaths.Gn(e.gn);
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) best = e;
            }
            return best?.gn;
        }

        // ---------- 載入 / 換歌 ----------

        // 換歌：Destroy 是延到幀尾才真的執行 → 先拆、隔一幀再建，否則新的「拆除基準快照」會把還沒消失的舊物件
        // 當成常駐物件記進去，下次換歌就拆不掉（一路累積下去）。
        private void LoadSong(string gn, int diff)
        {
            if (_loading) return;
            StartCoroutine(LoadSongCo(gn, diff));
        }

        private IEnumerator LoadSongCo(string gn, int diff)
        {
            _loading = true;
            var e = SongCatalog.Get(gn);
            if (e != null && !e.HasChart(diff))                     // 這首沒有這個難度 → 挑一個有的
                for (int d = 0; d < 3; d++) if (e.HasChart(d)) { diff = d; break; }

            Teardown();
            yield return null;                                      // 等舊的音符板/HUD 真的被銷毀

            _entry = e; _gn = gn; _diff = Mathf.Clamp(diff, 0, 2);
            PlayerPrefs.SetString(PrefLastGn, _gn);
            PlayerPrefs.SetInt(PrefLastDiff, _diff);

            string gnPath = SongPaths.Gn(_gn), oggPath = SongPaths.Ogg(_gn);
            if (gnPath == null || !File.Exists(gnPath)) { _status = "找不到譜面檔：" + gnPath; _loading = false; yield break; }
            _status = (oggPath != null && File.Exists(oggPath)) ? "" : "找不到音樂檔（只能看譜，沒有聲音/波形）";

            // ScreenGameplay 什麼都不掛在自己身上（音符板/HUD 都是新的場景根物件）→ 先記下現有的根，換歌時照差集拆。
            _preRoots = new HashSet<GameObject>(SceneManager.GetActiveScene().GetRootGameObjects());

            var game = new GameObject("ScreenGameplay").AddComponent<ScreenGameplay>();   // 欄位在它的 Start() 之前讀
            game.editorMode = true;              // 純黑背景（不載場景/舞者）＋不判定/不結算＋可自由 seek
            game.gnPath = gnPath;
            game.oggPath = oggPath;
            game.difficulty = _diff;
            game.autoPlay = false;
            game.assistTick = false;
            game.effectCharacter = false;        // 不放 combo 特效
            game.effectScene = false;            // 不放場景常駐特效
            game.scrollSpeedMul = _speed;
            game.useMusicStartOffset = true;     // type-10 音樂起點：音符照樣領先音樂 count-in 拍（波形也會跟著位移）
            _songOffset = SongOffsets.Get(_gn);   // 這首歌上次調好的 offset（F11/F12）
            game.songOffsetMs = (float)_songOffset;
            _game = game;

            if (_overlay == null)
            {
                var ogo = new GameObject("ChartEditorOverlay");
                ogo.transform.SetParent(transform, false);
                _overlay = ogo.AddComponent<ChartEditorOverlay>();
            }
            EnsureOverlay();
            _overlay.Game = _game;
            _overlay.Peaks = null;
            _overlay.showWaveform = true;
            _overlay.showJudgeLine = false;
            _overlay.showHitError = false;
            _peaksCo = StartCoroutine(BuildPeaksCo(_game));
            _loading = false;
        }

        private void EnsureOverlay()
        {
            if (_overlay != null) return;
            var ogo = new GameObject("ChartEditorOverlay");
            ogo.transform.SetParent(transform, false);
            _overlay = ogo.AddComponent<ChartEditorOverlay>();
        }

        // ---------- 打拍測試（校時）----------
        //
        // 不讀 .gn、不放音樂：只有固定 BPM 的等距音符（全部在 R 軌）＋每顆音符一聲節拍音（assist tick）。
        // 打下去 → osu 式誤差條告訴你偏早還偏晚，統計出來的中位數就是該補的 global offset。

        private void ToggleBeatTest()
        {
            if (_loading) return;
            _beatTest = !_beatTest;
            if (_beatTest) StartCoroutine(LoadBeatTestCo());
            else LoadSong(_gn, _diff);
        }

        private IEnumerator LoadBeatTestCo()
        {
            _loading = true;
            Teardown();
            yield return null;

            _stats.Clear();
            _status = "";
            _preRoots = new HashSet<GameObject>(SceneManager.GetActiveScene().GetRootGameObjects());

            var game = new GameObject("ScreenGameplay").AddComponent<ScreenGameplay>();
            game.editorMode = true;
            game.beatTestMode = true;            // 合成譜（LoadChart 走 BeatTestChart）＋判定會跑
            game.beatTestBpm = _beatBpm;
            game.beatTestBeatsPerNote = _beatDiv;
            game.assistTick = true;              // 節拍音 = 每顆音符一聲 clap
            game.autoPlay = false;
            game.effectCharacter = false;
            game.effectScene = false;
            game.scrollSpeedMul = _speed;
            game.judgeOffsetY = _judgeOffsetY;
            game.EditorOnHit = OnBeatTestHit;
            _game = game;

            EnsureOverlay();
            _overlay.Game = _game;
            _overlay.Peaks = null;
            _overlay.showWaveform = false;       // 沒有音樂 → 沒有波形
            _overlay.showJudgeLine = true;
            _overlay.showHitError = true;
            _overlay.ClearHits();
            _loading = false;

            while (_game != null && !_game.EditorReady) yield return null;
            if (_game == null) yield break;
            _game.EditorGlobalOffsetMs = _globalOffset;   // 時鐘建好之後才套（_clock 在 BootBuildCo 之後才有值）
            _game.EditorSetPaused(false);                 // 直接開始（打拍測試沒有什麼好等的）
        }

        private void OnBeatTestHit(double deltaMs, Sdo.Ruleset.Judgment j)
        {
            if (_overlay != null) _overlay.AddHit(deltaMs, j);
            if (!double.IsNaN(deltaMs)) _stats.Add(deltaMs);   // miss 不進統計（同 osu）
            else _misses++;
        }

        private void Teardown()
        {
            if (_peaksCo != null) { StopCoroutine(_peaksCo); _peaksCo = null; }
            if (_game != null) _speed = _game.scrollSpeedMul;   // F5/F6 調過的下落速度：換歌要留著
            _game = null;
            _grid = null; _gridFor = null;
            if (_overlay != null) { _overlay.Game = null; _overlay.Peaks = null; }
            Time.timeScale = 1f;                 // 暫停中換歌：新畫面的協程/音訊要在正常流速下起跑
            if (_preRoots == null) return;
            foreach (var go in SceneManager.GetActiveScene().GetRootGameObjects())
                if (!_preRoots.Contains(go)) Destroy(go);
            _preRoots = null;
        }

        // 音樂波形：AudioClip 解好之後把 PCM 分批讀出來壓成每格峰值。一首 4 分鐘立體聲有 ~2100 萬個取樣，
        // 一次 GetData 會卡住一大幀 → 每幀讀一塊（AudioClip.GetData 的 offset 版本），邊讀邊餵給 Builder。
        private IEnumerator BuildPeaksCo(ScreenGameplay game)
        {
            // 等「音檔載入試過了」而不是等 clip 出現 —— 沒有 .ogg 的歌永遠不會有 clip，等 clip 會卡住一條協程。
            while (game != null && game == _game && !game.EditorAudioReady) yield return null;
            if (game == null || game != _game) yield break;
            var clip = game.EditorClip;
            if (clip == null) yield break;   // 沒有音樂 → 沒有波形（狀態列已經說了）

            if (clip.loadState != AudioDataLoadState.Loaded)
            {
                clip.LoadAudioData();
                while (clip.loadState == AudioDataLoadState.Loading) yield return null;
                if (clip.loadState != AudioDataLoadState.Loaded) { _status = "音樂無法解碼 → 沒有波形"; yield break; }
            }

            int ch = Mathf.Max(1, clip.channels);
            int frames = clip.samples;                       // 每聲道的取樣數
            var builder = new WaveformPeaks.Builder(ch, clip.frequency, WaveBucketMs);
            const int ChunkFrames = 1 << 18;
            var buf = new float[ChunkFrames * ch];

            for (int off = 0; off < frames; off += ChunkFrames)
            {
                if (game != _game) yield break;               // 中途換歌 → 丟掉
                int n = Mathf.Min(ChunkFrames, frames - off);
                if (!clip.GetData(buf, off)) { _status = "讀不到 PCM → 沒有波形"; yield break; }
                builder.Feed(buf, n * ch);                    // 最後一塊 GetData 會從頭繞回來補滿 → 只餵真正有效的 n 幀
                yield return null;
            }

            var peaks = builder.Finish();
            if (game != _game) yield break;
            if (_overlay != null)
            {
                _overlay.Peaks = peaks;
                _overlay.PeaksOffsetMs = game.EditorMusicDelaySec * 1000.0;   // 波形第 0 格 = 音樂真正開始的譜面時間
            }
            _peaksCo = null;
        }

        // ---------- 鍵盤 ----------

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F1)) _showList = !_showList;
            if (Input.GetKeyDown(KeyCode.F2)) ToggleBeatTest();
            if (Input.GetKeyDown(KeyCode.Escape) && _showList) _showList = false;
            if (_game == null || !_game.EditorReady) return;

            if (Input.GetKeyDown(KeyCode.Space)) _game.EditorSetPaused(!_game.EditorPaused);

            if (_beatTest)
            {
                // 打拍測試：方向鍵是「軌道鍵」（A/S/W/D + ←↓↑→，R 軌 = D 或 →），不能拿來 seek/調格線。
                if (Input.GetKeyDown(KeyCode.Backspace)) { _stats.Clear(); _misses = 0; _overlay?.ClearHits(); }
                // BPM / 密度改了要重建整份合成譜（幾千顆音符）→ 等滑鼠放開再做，不然拖滑桿會每幀重建一次。
                if (!_loading && !Input.GetMouseButton(0)
                    && (Mathf.Abs(_beatBpmPending - _beatBpm) > 0.5f || Mathf.Abs(_beatDivPending - _beatDiv) > 0.01f))
                {
                    _beatBpm = _beatBpmPending; _beatDiv = _beatDivPending;
                    StartCoroutine(LoadBeatTestCo());
                }
                return;
            }

            if (Input.GetKeyDown(KeyCode.F3) && _overlay != null) _overlay.showWaveform = !_overlay.showWaveform;
            if (Input.GetKeyDown(KeyCode.G) && _overlay != null) _overlay.showGrid = !_overlay.showGrid;
            if (Input.GetKeyDown(KeyCode.Tab)) CycleDifficulty();

            if (Input.GetKeyDown(KeyCode.Home)) _game.EditorSeekMs(0);
            if (Input.GetKeyDown(KeyCode.End)) _game.EditorSeekMs(_game.EditorEndMs);

            // 單首 offset（StepMania 編輯器的 F11/F12）：一次 0.02 秒，按住 Alt 微調 0.001 秒。
            // 正 = 音符相對音樂往後。改完立刻寫回 song_offsets.ini，正式遊玩就吃得到。
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            double stepMs = (alt ? SongOffsets.FineStepSec : SongOffsets.StepSec) * 1000.0;
            if (Input.GetKeyDown(KeyCode.F11)) NudgeSongOffset(-stepMs);
            if (Input.GetKeyDown(KeyCode.F12)) NudgeSongOffset(+stepMs);

            // Ctrl+↑/↓ = 顯示縮放（StepMania 的 Ctrl+Up/Down 改 scroll speed）：↓ 變窄、↑ 變寬。
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrl && Input.GetKeyDown(KeyCode.DownArrow)) { _game.EditorZoom(+1); return; }
            if (ctrl && Input.GetKeyDown(KeyCode.UpArrow)) { _game.EditorZoom(-1); return; }

            // 上下 = 一格 snap（＝格線的細分）、PgUp/PgDn = 一小節；左右 = 調格線細分（同參考工具）
            if (Input.GetKeyDown(KeyCode.UpArrow)) SeekBySnap(-1);
            if (Input.GetKeyDown(KeyCode.DownArrow)) SeekBySnap(+1);
            if (Input.GetKeyDown(KeyCode.PageUp)) SeekByMeasure(-1);
            if (Input.GetKeyDown(KeyCode.PageDown)) SeekByMeasure(+1);
            if (Input.GetKeyDown(KeyCode.LeftArrow)) StepSubdivisions(-1);
            if (Input.GetKeyDown(KeyCode.RightArrow)) StepSubdivisions(+1);

            float wheel = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(wheel) > 0.01f) SeekBySnap(wheel > 0f ? -1 : +1);
        }

        // 單首 offset：即時套進時鐘（音符會當場位移，跟 StepMania 一樣邊聽邊調）＋寫回 song_offsets.ini。
        private void NudgeSongOffset(double deltaMs)
        {
            if (_game == null || string.IsNullOrEmpty(_gn)) return;
            _songOffset = Math.Round(Mathf.Clamp((float)(_songOffset + deltaMs), SongOffsets.MinMs, SongOffsets.MaxMs), 3);
            _game.EditorSongOffsetMs = _songOffset;
            SongOffsets.Set(_gn, (float)_songOffset);
        }

        private BeatGrid Grid()
        {
            var map = _game != null ? _game.EditorMap : null;
            if (map == null) return null;
            if (_grid == null || !ReferenceEquals(_gridFor, map)) { _grid = BeatGrid.From(map); _gridFor = map; }
            return _grid;
        }

        // 目前位置往前/後一格 snap（吸附到格線上：先量化，再走一格）
        private void SeekBySnap(int dir)
        {
            var g = Grid(); if (g == null) return;
            int div = Mathf.Max(1, _overlay != null ? _overlay.subdivisions : 4);
            double step = 1.0 / div;                                    // 拍
            double beat = g.MsToBeat(_game.EditorNowMs);
            double snapped = Math.Round(beat / step) * step;
            if (Math.Abs(snapped - beat) > step * 0.02) beat = snapped; // 沒在格線上 → 先吸附
            else beat = snapped + dir * step;                           // 已經在格線上 → 走一格
            _game.EditorSeekMs(g.BeatToMs(Math.Max(0.0, beat)));
        }

        private void SeekByMeasure(int dir)
        {
            var g = Grid(); if (g == null) return;
            int m = g.MeasureAt(_game.EditorNowMs + (dir > 0 ? 1.0 : -1.0));   // 剛好站在小節線上時往回才不會原地不動
            _game.EditorSeekMs(g.MeasureStartMs(Math.Max(0, m + (dir > 0 ? 1 : 0))));
        }

        private void StepSubdivisions(int dir)
        {
            if (_overlay == null) return;
            int[] steps = { 1, 2, 3, 4, 6, 8, 12, 16 };                 // 4分 → 64分（每拍細分數）
            int i = Array.IndexOf(steps, _overlay.subdivisions);
            if (i < 0) i = 3;
            _overlay.subdivisions = steps[Mathf.Clamp(i + dir, 0, steps.Length - 1)];
        }

        private void CycleDifficulty()
        {
            if (_entry == null) { LoadSong(_gn, (_diff + 1) % 3); return; }
            for (int k = 1; k <= 3; k++)
            {
                int d = (_diff + k) % 3;
                if (_entry.HasChart(d)) { LoadSong(_gn, d); return; }
            }
        }

        // ---------- IMGUI ----------

        private void OnGUI()
        {
            var box = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleLeft, padding = new RectOffset(8, 8, 4, 4) };
            if (_beatTest) { DrawBeatTestGui(box); return; }

            GUI.Box(new Rect(0, 0, Screen.width, 54), GUIContent.none);

            bool ready = _game != null && _game.EditorReady;
            var map = ready ? _game.EditorMap : null;
            string title = _entry != null && !string.IsNullOrEmpty(_entry.title) ? _entry.title : _gn;
            string[] diffNames = { "簡單", "普通", "困難" };

            GUILayout.BeginArea(new Rect(6, 4, Screen.width - 12, 46));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("歌單 (F1)", GUILayout.Width(80))) _showList = !_showList;
            if (GUILayout.Button("打拍測試 (F2)", GUILayout.Width(100))) ToggleBeatTest();
            GUILayout.Label($"♪ {title}   [{_gn}]", box, GUILayout.Width(280));

            for (int d = 0; d < 3; d++)
            {
                bool has = _entry == null || _entry.HasChart(d);
                GUI.enabled = has && ready;
                bool on = d == _diff;
                string lv = _entry != null && has ? $" LV{_entry.Diff(d)}" : "";
                if (GUILayout.Toggle(on, diffNames[d] + lv, GUI.skin.button, GUILayout.Width(78)) && !on) LoadSong(_gn, d);
                GUI.enabled = true;
            }

            if (map != null)
                GUILayout.Label($"BPM {map.Bpm:0.##}   {map.TotalNotes} notes", box, GUILayout.Width(180));
            if (!string.IsNullOrEmpty(_status))
                GUILayout.Label(_status, box);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // ---- transport ----
            GUILayout.BeginHorizontal();
            GUI.enabled = ready;
            if (GUILayout.Button(ready && !_game.EditorPaused ? "❚❚ 暫停" : "▶ 播放", GUILayout.Width(80)))
                _game.EditorSetPaused(!_game.EditorPaused);
            if (GUILayout.Button("⏮ 開頭", GUILayout.Width(70))) _game.EditorSeekMs(0);

            double now = ready ? _game.EditorNowMs : 0.0;
            double end = ready ? Math.Max(1.0, _game.EditorEndMs) : 1.0;
            // 這一列右邊還有 時間/小節拍/縮放/流速/單首offset 幾格固定寬的讀數 → 進度條吃剩下的寬度
            float pos = GUILayout.HorizontalSlider((float)now, 0f, (float)end, GUILayout.Width(Mathf.Max(120, Screen.width - 900)));
            if (ready && Mathf.Abs(pos - (float)now) > 1f) _game.EditorSeekMs(pos);   // 拖 slider = seek（暫停中也可以）

            GUILayout.Label($"{Fmt(now)} / {Fmt(end)}", box, GUILayout.Width(110));
            var g = ready ? Grid() : null;
            if (g != null)
            {
                double beat = g.MsToBeat(now);
                GUILayout.Label($"小節 {g.MeasureAt(now)}  拍 {(beat % 4 + 4) % 4 + 1:0.00}", box, GUILayout.Width(130));
            }
            float speed = ready ? _game.EditorScrollSpeed : _speed;   // F5/F6 與 Ctrl+↑↓ 都直接改 ScreenGameplay → 顯示以它為準
            GUILayout.Label($"縮放 {speed:0.00}× (Ctrl+↑↓/F5F6)", box, GUILayout.Width(150));
            GUILayout.Label($"流速 {(ready ? _game.EditorRate : 1.0):0.00}× ([ ]，= 回 1×)", box, GUILayout.Width(150));
            GUILayout.Label($"單首offset {_songOffset:+0.#;-0.#;0} ms (F11/F12)", box, GUILayout.Width(180));
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            GUI.Label(new Rect(8, Screen.height - 22, Screen.width - 16, 20f),
                "空白=播放/暫停  ↑↓=一格  Ctrl+↑↓=區域窄/寬  PgUp/PgDn=一小節  ←→=格線細分" +
                (_overlay != null ? $"（每拍 {_overlay.subdivisions} 格）" : "") +
                "  F11/F12=單首offset(±20ms，Alt=±1ms)  F1=歌單  F2=打拍測試  F3=波形  G=格線  Tab=難度");

            if (_showList) DrawSongList();
        }

        private static string Fmt(double ms)
        {
            if (ms < 0) ms = 0;
            int t = (int)(ms / 1000.0);
            return $"{t / 60:00}:{t % 60:00}.{(int)(ms % 1000) / 100}";
        }

        // ---------- 打拍測試的面板 ----------
        //
        // 左上：BPM / 節拍密度 / 重來。右側：兩個 offset 滑桿、即時統計、osu 式誤差直方圖、建議值與存檔。
        private void DrawBeatTestGui(GUIStyle box)
        {
            bool ready = _game != null && _game.EditorReady;
            GUI.Box(new Rect(0, 0, Screen.width, 30), GUIContent.none);

            GUILayout.BeginArea(new Rect(6, 3, Screen.width - 12, 26));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("← 回譜面 (F2)", GUILayout.Width(110))) ToggleBeatTest();
            GUILayout.Label("打拍測試：跟著節拍音打 R 軌（D 或 →）", box, GUILayout.Width(260));

            GUILayout.Label("BPM", GUILayout.Width(32));
            _beatBpmPending = Mathf.Round(GUILayout.HorizontalSlider(_beatBpmPending, 60f, 240f, GUILayout.Width(140)));
            GUILayout.Label($"{_beatBpmPending:0}", box, GUILayout.Width(36));

            GUILayout.Label("音符", GUILayout.Width(32));
            _beatDivPending = GUILayout.Toggle(_beatDivPending < 0.75f, "8 分", GUI.skin.button, GUILayout.Width(50)) ? 0.5f : 1f;

            if (GUILayout.Button("重來 (Backspace)", GUILayout.Width(120)))
            { _stats.Clear(); _misses = 0; _overlay?.ClearHits(); }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            // ---- 右下面板 ----（不能貼著上面：誤差條就畫在軌道右邊、與受擊線同高，蓋到就看不到了）
            const float PanelW = 380f;
            float panelH = Mathf.Min(440f, Screen.height * 0.66f);
            var r = new Rect(Screen.width - PanelW - 8, Screen.height - panelH - 30f, PanelW, panelH);
            GUILayout.BeginArea(r, GUI.skin.box);

            GUILayout.Label("判定 offset（毫秒）— 正 = 判定往後（整體打太早就往正的調）");
            GUILayout.BeginHorizontal();
            double off = GUILayout.HorizontalSlider((float)_globalOffset, -150f, 150f, GUILayout.Width(240));
            off = Math.Round(off);
            GUILayout.Label($"{_globalOffset:+0;-0;0} ms", box, GUILayout.Width(70));
            GUILayout.EndHorizontal();
            if (ready && Math.Abs(off - _globalOffset) > 0.5) { _globalOffset = off; _game.EditorGlobalOffsetMs = off; }

            GUILayout.Space(4);
            GUILayout.Label("判定線位置（px）— 完美時機的音符落在受擊線 + 這個位移（藍線）");
            GUILayout.BeginHorizontal();
            float jy = GUILayout.HorizontalSlider(_judgeOffsetY, -60f, 60f, GUILayout.Width(240));
            jy = Mathf.Round(jy);
            GUILayout.Label($"{_judgeOffsetY:+0;-0;0} px", box, GUILayout.Width(70));
            GUILayout.EndHorizontal();
            if (ready && Mathf.Abs(jy - _judgeOffsetY) > 0.5f) { _judgeOffsetY = jy; _game.judgeOffsetY = jy; }

            GUILayout.Space(6);
            int n = _stats.Count;
            double mean = _stats.Mean, median = _stats.Median, ur = _stats.UnstableRate;
            GUILayout.Label($"打了 {n} 下（miss {_misses}）");
            if (n > 0)
            {
                GUILayout.Label($"平均 {mean:+0.0;-0.0;0.0} ms（{(mean < 0 ? "偏早" : "偏晚")}）   中位數 {median:+0.0;-0.0;0.0} ms");
                GUILayout.Label($"UR {ur:0.0}（越小越穩；10 × 標準差）");
            }
            else GUILayout.Label("跟著節拍音打右鍵，資料會即時累積。");

            GUILayout.Space(6);
            DrawHistogram(GUILayoutUtility.GetRect(PanelW - 20, 120));

            GUILayout.Space(6);
            double suggested = _stats.SuggestedOffset(_globalOffset);
            bool enough = n >= 20;
            GUI.enabled = enough && ready;
            if (Math.Abs(suggested - _globalOffset) < 0.5 && enough)
                GUILayout.Label($"目前的 offset 就很準了（{n} 下）");
            else
                GUILayout.Label(enough
                    ? $"建議 offset：{suggested:+0;-0;0} ms（依中位數，打太亂會自動保守）"
                    : $"再打幾下（至少 20 下）才給建議…目前 {n}");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("套用建議值", GUILayout.Width(110)))
            { _globalOffset = Math.Round(suggested); if (ready) _game.EditorGlobalOffsetMs = _globalOffset; }
            GUI.enabled = true;
            if (GUILayout.Button("存進 config.ini", GUILayout.Width(120)))
            {
                RoomConfig.globalOffsetMs = (float)_globalOffset;
                RoomConfig.judgeOffsetY = _judgeOffsetY;
                RoomConfig.Save();
                _status = "已存進 config.ini（下次開遊戲生效）";
            }
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status);
            GUILayout.EndArea();

            GUI.Label(new Rect(8, Screen.height - 22, Screen.width - 16, 20f),
                "空白=播放/暫停   D 或 → =打右軌   Backspace=重來   F2=回譜面   誤差條：左=太早 右=太晚，箭頭=你的平均偏移");
        }

        // osu 式打擊誤差直方圖（HitEventTimingDistributionGraph）：中央 = 準時，左邊 = 太早、右邊 = 太晚。
        private void DrawHistogram(Rect area)
        {
            GUI.Box(area, GUIContent.none);
            var bins = _stats.Histogram(25, out double binSize);
            int max = 1;
            foreach (var b in bins) if (b > max) max = b;

            float w = area.width / bins.Length;
            for (int i = 0; i < bins.Length; i++)
            {
                if (bins[i] <= 0) continue;
                float h = (area.height - 16f) * bins[i] / max;
                var bar = new Rect(area.x + i * w, area.yMax - 14f - h, Mathf.Max(1f, w - 1f), h);
                // 離中央越遠越紅：中央（準）藍、外圍（不準）黃紅
                float t = Mathf.Abs(i - bins.Length / 2) / (float)(bins.Length / 2);
                GUI.color = Color.Lerp(new Color(0.4f, 0.8f, 1f), new Color(1f, 0.35f, 0.2f), t);
                GUI.DrawTexture(bar, Texture2D.whiteTexture);
            }
            GUI.color = Color.white;
            GUI.Label(new Rect(area.x + 2, area.yMax - 16f, 90, 16), $"-{binSize * 25:0} ms");
            GUI.Label(new Rect(area.center.x - 12, area.yMax - 16f, 40, 16), "0");
            GUI.Label(new Rect(area.xMax - 60, area.yMax - 16f, 60, 16), $"+{binSize * 25:0} ms");
        }

        private void DrawSongList()
        {
            var r = new Rect(Screen.width - 430, 60, 420, Mathf.Min(560, Screen.height - 90));
            GUILayout.BeginArea(r, GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label("搜尋", GUILayout.Width(32));
            _search = GUILayout.TextField(_search ?? "");
            if (GUILayout.Button("×", GUILayout.Width(24))) _search = "";
            GUILayout.EndHorizontal();

            // 只在 Layout 階段重篩：IMGUI 的 Layout 與 Repaint 兩趟必須產生一樣多的控制項，
            // 在 Repaint 時改清單長度會直接丟 "GUILayout: Mismatched LayoutGroup"。
            if (Event.current.type == EventType.Layout) Refilter();
            GUILayout.Label($"{_filtered.Count} / {SongCatalog.All.Count} 首（點一下換歌，難度用上面的按鈕或 Tab）");
            _listScroll = GUILayout.BeginScrollView(_listScroll);
            int shown = 0;
            foreach (var e in _filtered)
            {
                if (shown++ > 400) { GUILayout.Label("…（太多了，打字縮小範圍）"); break; }
                string lvs = $"{(e.HasChart(0) ? e.Diff(0).ToString() : "-")}/{(e.HasChart(1) ? e.Diff(1).ToString() : "-")}/{(e.HasChart(2) ? e.Diff(2).ToString() : "-")}";
                if (GUILayout.Button($"{e.title}  —  {e.artist}\n{e.gn}   BPM {e.bpm:0.#}   LV {lvs}", GUILayout.Height(38)))
                { LoadSong(e.gn, _diff); _showList = false; }
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void Refilter()
        {
            string q = (_search ?? "").Trim();
            if (_filterFor == q) return;
            _filterFor = q;
            _filtered.Clear();
            foreach (var e in SongCatalog.All)
            {
                if (q.Length > 0
                    && (e.title == null || e.title.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                    && (e.artist == null || e.artist.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                    && (e.gn == null || e.gn.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                _filtered.Add(e);
            }
        }
    }
}
