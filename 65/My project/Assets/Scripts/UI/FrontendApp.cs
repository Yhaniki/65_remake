using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using Sdo.Game;
using Sdo.Localization;
using Sdo.Settings;
using Sdo.UI.Core;
using Sdo.UI.Screens;
using Sdo.UI.Util;

namespace Sdo.UI
{
    /// <summary>
    /// Front-end entry point. Self-boots (RuntimeInitialize), takes over from the gameplay scene by
    /// destroying any auto-spawned ScreenGameplay (runs first via a very low execution order — zero edits
    /// to ScreenGameplay), builds the canvas + screens + modals procedurally, and drives the flow.
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    public sealed class FrontendApp : MonoBehaviour
    {
        public static FrontendApp Instance { get; private set; }

        /// <summary>The front-end UI camera (frames the 800×600 4:3 world canvas). Exposed so a screen that mounts a 3D
        /// scene behind its UI (e.g. RoomScreen → RoomScene3D) can mask the 3D layers off this camera while shown.</summary>
        public Camera UiCam => _uiCam;

        /// <summary>True while the 商城 (avatar shop) modal is open over whatever screen is behind it. A backing screen
        /// (e.g. GenderSelectScreen) checks this so its own ESC handler stays out of the way while the shop is up.</summary>
        public bool ShopOpen => _shop != null && _shop.IsOpen;

        /// <summary>True while any room-reachable modal (商城 / 儲物櫃 / 設定) is layered over the current screen. The room
        /// gates its ESC→選角色 on this so ESC inside a modal doesn't jump past it. Modals don't change Flow.Current, so
        /// the room can't tell them apart from a screen check alone.</summary>
        public bool AnyModalOpen => ShopOpen
            || (_wardrobe != null && _wardrobe.IsOpen)
            || (_option != null && _option.IsOpen);

        private AppContext _ctx;
        private readonly Dictionary<ScreenId, UIScreenBase> _screens = new Dictionary<ScreenId, UIScreenBase>();
        private OptionDlgModal _option;
        private NoteSkinPicker _notePicker;
        private ResultsModal _results;
        private ShopScreen _shop;
        private WardrobeScreen _wardrobe;
        private int _killGuardFrames = 3;
        private GameObject _canvasGo;                 // the whole front-end canvas (hidden while gameplay runs)
        private Camera _uiCam;                        // camera that frames the 800×600 UI at a fixed 4:3 (AspectController)
        private ScreenGameplay _activeGame;                // the running gameplay instance (null = in the front-end)
        private bool _returningFromGame;              // 回房轉場已啟動（Update 每幀都會偵測 ResultConfirmed → 只觸發一次轉場）
        private HashSet<GameObject> _preGameRoots;    // scene roots that existed before launch -> kept on exit

        // Suppress the play screen's self-boot before any scene script runs (BeforeSceneLoad always precedes
        // ScreenGameplay's AfterSceneLoad Boot). The front-end is the entry point and launches gameplay on demand, so a
        // stray auto-booted ScreenGameplay (and the orphan avatar it would leave behind) must never come into being.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void SuppressGameplayAutoBoot()
        {
            // DEV: SDO_PROBE → dead-file probe mode: suppress gameplay AND the front-end (Boot returns early), so the
            // only thing that runs is UsedAssetsProbe (touches every loadable file, then quits). See UsedAssetsProbe.
            if (!string.IsNullOrEmpty(ScreenGameplay.DevVar("SDO_PROBE"))) { ScreenGameplay.AutoBootSuppressed = true; return; }
            // DEV: SDO_SCENE → skip the front-end and boot straight into that gameplay scene (for testing a specific
            // stage's render/effects, e.g. SDO_SCENE=SCN0008). Editor reads it from EditorPrefs (Tools/SDO menu), a
            // player build from the env var — see ScreenGameplay.DevVar. Leaves AutoBoot un-suppressed so ScreenGameplay.Boot runs.
            if (!string.IsNullOrEmpty(ScreenGameplay.DevVar("SDO_SCENE"))) return;
            ScreenGameplay.AutoBootSuppressed = true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (UsedAssetsProbe.LaunchIfRequested()) return;                          // DEV: SDO_PROBE → run the probe instead of the app
            if (!string.IsNullOrEmpty(ScreenGameplay.DevVar("SDO_SCENE"))) return;   // DEV: no front-end in scene-test mode (env var or Tools/SDO menu)
            // DEV: SDO_EDITOR → 譜面編輯器（ChartEditorScreen 自己開起來）：不要前端，也不要大廳 BGM。
            if (!string.IsNullOrEmpty(ScreenGameplay.DevVar(ChartEditorScreen.EnvVar))) return;
            if (Instance != null) return;
            var go = new GameObject("FrontendApp");
            Instance = go.AddComponent<FrontendApp>();
            DontDestroyOnLoad(go);
        }

        private void Start()
        {
            KillStrayGameplay();

            var lang = LanguageInfo.FromCode(DisplaySettingsManager.Settings?.language);
            LocalizationManager.Init(lang);

            var vol = DisplaySettingsManager.Settings?.audio;   // 開機即把已存的三個音量套進 AudioMix(BGM/歌曲/SE 一開始就對)
            if (vol != null) AudioMix.Set(vol.bgm, vol.gameMusic, vol.sfx);

            _ctx = AppContext.CreateMock();

            // OPTION 遊戲頁「遊戲畫面」偏好：全屏(填滿) = Stretch，視窗化(左右黑邊) = Pillarbox。必須在 CreateWorldCanvas
            // 註冊 UI 相機（→ AspectController 首次 Apply）之前設好靜態 Mode，之後開的遊戲相機也沿用同一個 Mode。
            AspectController.Mode = (DisplaySettingsManager.Settings?.gameplay?.fullscreenFill ?? true)
                ? AspectMode.Stretch : AspectMode.Pillarbox;

            // Fixed 800×600 (4:3) world-space canvas, framed by a camera the AspectController fits to the window
            // (stretched to fill by default) — same 4:3 frame as the play screen, so the whole app is consistent 4:3.
            var canvas = UIKit.CreateWorldCanvas("FrontendCanvas", new Vector2(800, 600), out _uiCam, 0);
            _canvasGo = canvas.gameObject;
            var root = (RectTransform)canvas.transform;
            UIKit.Stretch(UIKit.AddImage(root, "AppBg", UITheme.Bg).rectTransform);

            var screenLayer = UIKit.NewRect(root, "Screens");
            UIKit.Stretch(screenLayer);
            Make<GenderSelectScreen>(screenLayer);   // 單機開場的男/女選擇畫面（Flow 的入口狀態）
            Make<LobbyScreen>(screenLayer);
            Make<RoomScreen>(screenLayer);
            Make<SongSelectScreen>(screenLayer);

            _ctx.Flow.ScreenChanged += (from, to) => { ShowOnly(to); UpdateBgm(to); };

            var modalLayer = UIKit.NewRect(root, "Modals");
            UIKit.Stretch(modalLayer);
            _option = new GameObject("OptionDlg").AddComponent<OptionDlgModal>();
            _option.transform.SetParent(modalLayer, false);
            _option.Build(modalLayer);
            _notePicker = new GameObject("NotePicker").AddComponent<NoteSkinPicker>();
            _notePicker.transform.SetParent(modalLayer, false);
            _notePicker.Build(modalLayer, _ctx.Session);
            _results = new GameObject("Results").AddComponent<ResultsModal>();
            _results.transform.SetParent(modalLayer, false);
            _results.Build(modalLayer);
            _shop = new GameObject("Shop").AddComponent<ShopScreen>();
            _shop.transform.SetParent(modalLayer, false);
            _shop.Build(modalLayer, _ctx.Session);
            _wardrobe = new GameObject("Wardrobe").AddComponent<WardrobeScreen>();
            _wardrobe.transform.SetParent(modalLayer, false);
            _wardrobe.Build(modalLayer, _ctx.Session);
            Toast.Init(modalLayer);

            Nav.OpenSettings = () => _option.Open();
            Nav.OpenNoteSkinPicker = () => _notePicker.Open();
            Nav.OpenShop = () => ScreenTransition.Run(() => _shop.Open());   // 進商城：漸黑 → loading → 漸亮（同房間進出效果）
            Nav.OpenWardrobe = () => _wardrobe.Open();                        // 儲物櫃有自己的視窗開闔動畫(WindowAnim)，不套轉場
            Nav.StartGame = StartGameplay;
            // 進房間轉場漸亮時，房間 UI 從四邊滑入（男女選擇→房間、遊戲→房間 共用；商城進出不觸發，房間仍在底下）。
            Nav.PlayRoomEntrance = () => { if (_screens.TryGetValue(ScreenId.Room, out var r) && r is RoomScreen rr) rr.PlayEntrance(); };

            WarmupFont();
            ShowOnly(_ctx.Flow.Current);
            UpdateBgm(_ctx.Flow.Current);   // 開場即起隨機大廳 BGM(男/女選擇畫面)

            // DEV: SDO_ROOM → boot straight into the waiting room (create a mock room + show it), for inspecting the
            // 3D room + ROOM UI without clicking through the lobby. Editor reads it from EditorPrefs, a build from env.
            if (!string.IsNullOrEmpty(ScreenGameplay.DevVar("SDO_ROOM"))) EnterRoom();
            // DEV: SDO_SHOP → boot into the waiting room then open the 商城 (shop) modal (Tools ▸ SDO ▸ Boot Into Shop).
            if (!string.IsNullOrEmpty(ScreenGameplay.DevVar("SDO_SHOP"))) { EnterRoom(); Nav.OpenShop?.Invoke(); }
        }

        // 大廳系畫面(男/女選擇 + ROOM)播 UI/BGM 資料夾的隨機 BGM(不連續重複)並淡回;選歌畫面=淡出禁音但軌道繼續播
        // (離開選歌回房間再淡回同一首);遊戲(有歌)/Lobby 才真的停。商城是疊在 ROOM/GenderSel 上的 modal(不改 Flow)→ BGM 持續。
        private static void UpdateBgm(ScreenId to)
        {
            if (to == ScreenId.GenderSel || to == ScreenId.Room) { BgmPlayer.Play(); BgmPlayer.SetMuted(false); }
            else if (to == ScreenId.SongSelect) BgmPlayer.SetMuted(true);   // 線性淡出 0.2s → 禁音,仍在播
            else BgmPlayer.Stop();
        }

        /// <summary>Create a mock room (host = local player) if none, and show the waiting room. Used by the SDO_ROOM
        /// dev hook and the room capture test.</summary>
        public void EnterRoom()
        {
            if (_ctx == null) return;
            if (_ctx.Rooms.CurrentRoom == null) _ctx.Rooms.CreateRoom(Sdo.UI.Services.GameMode.Normal);
            _ctx.Flow.GoTo(ScreenId.Room);
        }

        private void Update()
        {
            _ctx?.Chat?.Tick();
            if (_killGuardFrames > 0 && _activeGame == null) { _killGuardFrames--; KillStrayGameplay(); }
            if (_activeGame != null)
            {
                if (!_activeGame.Finished)
                {
                    if (Input.GetKeyDown(KeyCode.Escape)) AbortGameplay();   // quit early during play, no settlement
                }
                // Finished: ScreenGameplay owns the win/lose 定格 pose + STATIS result panel itself (its own ResultScreen).
                // That sequence plays out AFTER Finished flips at song-end, so we must NOT tear down on Finished — we
                // wait for the player to confirm the panel (OnConfirm sets ResultConfirmed), then return to the room.
                else if (_activeGame.ResultConfirmed) ReturnFromGameplay();
            }
        }

        // ---- gameplay hand-off (host pressed Start in the room) ----

        // Spawn the faithful play screen (ScreenGameplay) configured from the session selection, and hide the whole
        // front-end while it runs. The session carries everything ScreenGameplay needs; the only mapping is resolving the
        // chart/audio paths in the music tree (sibling of SdoExtracted.Root) and the per-song choreography by fileId.
        private void StartGameplay()
        {
            if (_activeGame != null) return;
            _returningFromGame = false;   // 新的一局：解除上次回房轉場的守門
            var s = _ctx.Session;
            if (!s.HasSong) { Toast.Show(LocalizationManager.Get("room.need_song")); return; }

            string gnPath = SongPaths.Gn(s.SongGn);     // e.g. .../MUSIC/sdom1197k.gn（SongPaths 內部走 SdoExtracted.MusicDir）
            string oggPath = SongPaths.Ogg(s.SongGn);   // chart letter (k/t) dropped: sdom1197k -> sdom1197.ogg

            // Snapshot the current scene roots (canvas, EventSystem, Main Camera, …) so TeardownGameplay can destroy
            // exactly what ScreenGameplay spawns (it parents nothing to us — every board/avatar/scene object is a new root).
            _preGameRoots = new HashSet<GameObject>(SceneManager.GetActiveScene().GetRootGameObjects());

            _ctx.Flow.GoTo(ScreenId.Gameplay);
            if (_canvasGo != null) _canvasGo.SetActive(false);
            if (_uiCam != null) _uiCam.enabled = false;   // stop the UI cam clearing over the play screen

            var game = new GameObject("ScreenGameplay").AddComponent<ScreenGameplay>();   // fields read in its Start() next frame
            game.gnPath = gnPath;
            game.oggPath = oggPath;
            game.difficulty = (int)s.Difficulty;                 // Easy/Normal/Hard -> 0/1/2
            game.songOffsetMs = SongCatalog.OffsetMs(s.SongGn);  // 這首譜自己的 offset（手改在 song_name_overrides.json 的 offsetMs）
            game.localPlayerName = s.LocalPlayerName;             // 頭上名字 = 房間同一個名字 (玩家001…)
            game.localPlayerMale = s.Gender == 1;
            game.avatarParts = ProfileManager.Active != null ? ProfileManager.Active.EquippedAvatarParts() : game.avatarParts;
            game.dpsPath = "DANCE/" + s.SongFileId + ".DPS";     // per-song choreography (missing -> generic dance fallback)
            game.scenePath = "SCENE/" + s.StageFolder;           // selected 3D stage
            game.autoPlay = false;                               // real play (A/S/W/D + numpad), not the demo auto-player
            game.scrollSpeedMul = s.Speed;                       // 房間「速度」檔位 → 下落速度（固定基準 ManiaScroll.DefaultReferenceBpm，osu式內部變速）
            game.roomNoteType = s.NoteType;                      // 房間 win2 選的 note 皮（-1=隨機, 0..10=指定, 10=3D）→ 開局套用同一個皮
            game.laneKeyOverride = DisplaySettingsManager.Settings?.keys?.ToLaneKeys(); // OPTION 鍵盤頁自訂鍵位（null → 預設 ASWD/numpad）
            game.showtimeMode = s.GameMode == 2;                 // 選歌模式選單：2 = ShowTime（氣條/集氣）模式；否則一般玩法
            game.dropDirection = s.DropDirection;                // 房間 win2「掉落方式」→ note 面板上/下 + 捲動方向（0=向上 1=向下 2=傾斜）
            var gp = DisplaySettingsManager.Settings?.gameplay;  // OPTION 遊戲頁偏好 → 開局套用
            if (gp != null)
            {
                game.effectCharacter = gp.effectCharacter;       // 人物特效（100/200/300 combo EFT）
                game.effectScene = gp.effectScene;               // 場景特效（常駐背景 EFT）
                game.cameraAuto = gp.cameraAuto;                 // 遊戲視角：默認(自動導播)/固定
                game.cameraFixedIndex = gp.cameraFixed;          // 固定視角鎖第幾台＝上次在遊戲中用 F2 切到的那台
                game.onCamModeChanged = PersistCamMode;          // 遊戲中 F2 換鏡頭 → 記住（見 PersistCamMode）
                game.boardAlpha = gp.panelOpacity;               // 面板透明度（note 面板 alpha 倍率）
                game.playFullSong = gp.playFullSong;             // 進階「整首打完」：HP 歸零不立即退出，打到曲末
                game.notesPanelLeft = gp.notesPanelLeft;         // NOTES面板位置：屏幕左邊/屏幕中央（水平位移）
                game.constantScroll = !gp.songSpeed;             // 進階「歌曲變速」關 → 整首固定流速（忽略譜面 BPM 變化 / SV）
            }
            _activeGame = game;
        }

        // 遊戲中按 F2 換鏡頭 → 存進 OPTION 遊戲頁的「遊戲視角」：切到固定鏡頭就記住是第幾台且標籤變「固定」，
        // 循環回自動導播就變回「默認」（台號保留）。落地到 settings.json（裝置層）＋ per-user config.ini 的 [Option]，
        // 否則下次開機 config.ini 會用舊值把它蓋回去。值沒變就不寫檔。
        private static void PersistCamMode(int camMode)
        {
            var s = DisplaySettingsManager.Settings;
            if (s == null) return;
            s.gameplay ??= new GameplaySettings();
            if (!s.gameplay.SetFromCamMode(camMode, ScreenGameplay.FixedCamCount)) return;
            DisplaySettingsManager.Save();
            RoomConfig.CaptureOptionFrom(s);
            RoomConfig.Save();
        }

        // Result panel confirmed: ScreenGameplay already showed its own STATIS settlement (score / EXP / G幣 / replay),
        // so the front-end just tears the gameplay session down and returns to the room. (The legacy ResultsModal is
        // intentionally unused now that the play screen settles itself; kept built only so older call sites compile.)
        private void ReturnFromGameplay() => TransitionToRoomFromGame();

        // Esc during play: abandon the run with no settlement and go straight back to the room.
        private void AbortGameplay() => TransitionToRoomFromGame();

        // 遊戲 → 房間：漸黑 → 全黑時拆遊戲場景並切回房間（建 3D 房間的卡頓藏在黑幕下）→ 漸亮，房間 UI 從四邊滑入。
        // 轉場的黑幕獨立於前端 canvas（gameplay 期間前端 canvas 關閉），所以能蓋住還在跑的遊戲畫面。
        private void TransitionToRoomFromGame()
        {
            if (_returningFromGame) return;   // Update 每幀都會偵測 ResultConfirmed → swap(_activeGame=null) 生效前先擋住重入
            _returningFromGame = true;
            ScreenTransition.Run(
                () => { TeardownGameplay(); _ctx.Flow.GoTo(ScreenId.Room); },
                onReveal: Nav.PlayRoomEntrance);
        }

        // Tear the gameplay session down and restore the front-end. ScreenGameplay owns the scene and never reparents into
        // us, so we destroy every root it added (diff against the pre-launch snapshot) and reset the time scale its
        // debug pause/speed keys may have changed, then re-show the front-end canvas. Does NOT change the flow state —
        // the caller decides where to go next (room directly, or via the results modal).
        private void TeardownGameplay()
        {
            _activeGame = null;
            Time.timeScale = 1f;
            if (_preGameRoots != null)
            {
                foreach (var go in SceneManager.GetActiveScene().GetRootGameObjects())
                    if (!_preGameRoots.Contains(go)) Destroy(go);
                _preGameRoots = null;
            }
            if (_canvasGo != null) _canvasGo.SetActive(true);
            if (_uiCam != null) _uiCam.enabled = true;
        }

        // 讓一個「全螢幕開發工具」（目前是譜面編輯器）接管畫面：把整個前端 canvas + UI 相機 + 大廳 BGM 收掉，
        // 跟進 gameplay 時同一套（EnterGameplay/TeardownGameplay 就是這樣做的），只是不經正常的 flow 切換。
        // 停用 canvas 會連同底下的螢幕 MonoBehaviour（含 GenderSelectScreen.Update）一起停 → 不會雙重吃輸入。
        public void HideForTool()
        {
            if (_canvasGo != null) _canvasGo.SetActive(false);
            if (_uiCam != null) _uiCam.enabled = false;
            BgmPlayer.Stop();
        }

        // 工具退出：把前端還原（flow 從沒變過，所以 Current 仍是原畫面），BGM 依當前畫面恢復。
        public void ShowAfterTool()
        {
            if (_canvasGo != null) _canvasGo.SetActive(true);
            if (_uiCam != null) _uiCam.enabled = true;
            if (_ctx != null && _ctx.Flow != null) UpdateBgm(_ctx.Flow.Current);
        }

        private void Make<T>(RectTransform parent) where T : UIScreenBase
        {
            var rt = UIKit.NewRect(parent, typeof(T).Name);
            UIKit.Stretch(rt);
            var screen = rt.gameObject.AddComponent<T>();
            screen.Build(_ctx);
            _screens[screen.Id] = screen;
        }

        private void ShowOnly(ScreenId id)
        {
            foreach (var kv in _screens)
            {
                // 選歌(MusicSelDlg) 是疊在房間上的 modal：顯示選歌時房間留在底下（3D 場景 + 整組 UI 都不隱藏），
                // 選歌畫面直接壓在上面（它自己有半透明黑幕把房間調暗並吃掉點擊）。其它畫面照常互斥。
                bool visible = kv.Key == id || (id == ScreenId.SongSelect && kv.Key == ScreenId.Room);
                kv.Value.SetVisible(visible);
            }
        }

        private static void KillStrayGameplay()
        {
            // The committed ScreenGameplay self-boots into any scene; the front-end is the entry point, so remove the
            // auto-booted one. Gameplay is launched on demand from StartGameplay() (host pressed Start), never here.
            foreach (var g in FindObjectsByType<ScreenGameplay>(FindObjectsSortMode.None))
                Destroy(g.gameObject);
        }

        private void WarmupFont()
        {
            var sb = new StringBuilder();
            sb.Append("0123456789%×♪★✓●◀▶ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");
            int budget = 4000;
            foreach (var e in SongCatalog.All)
            {
                if (budget <= 0) break;
                if (!string.IsNullOrEmpty(e.title)) { sb.Append(e.title); budget -= e.title.Length; }
                if (!string.IsNullOrEmpty(e.artist)) { sb.Append(e.artist); budget -= e.artist.Length; }
            }
            UIFont.Warmup(sb.ToString());
        }
    }
}
