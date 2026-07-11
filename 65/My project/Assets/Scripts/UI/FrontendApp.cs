using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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
        private HashSet<GameObject> _preGameRoots;    // scene roots that existed before launch -> kept on exit

        // Suppress the play screen's self-boot before any scene script runs (BeforeSceneLoad always precedes
        // ScreenGameplay's AfterSceneLoad Boot). The front-end is the entry point and launches gameplay on demand, so a
        // stray auto-booted ScreenGameplay (and the orphan avatar it would leave behind) must never come into being.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void SuppressGameplayAutoBoot()
        {
            // DEV: SDO_SCENE → skip the front-end and boot straight into that gameplay scene (for testing a specific
            // stage's render/effects, e.g. SDO_SCENE=SCN0008). Editor reads it from EditorPrefs (Tools/SDO menu), a
            // player build from the env var — see ScreenGameplay.DevVar. Leaves AutoBoot un-suppressed so ScreenGameplay.Boot runs.
            if (!string.IsNullOrEmpty(ScreenGameplay.DevVar("SDO_SCENE"))) return;
            ScreenGameplay.AutoBootSuppressed = true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!string.IsNullOrEmpty(ScreenGameplay.DevVar("SDO_SCENE"))) return;   // DEV: no front-end in scene-test mode (env var or Tools/SDO menu)
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

            _ctx.Flow.ScreenChanged += (from, to) => ShowOnly(to);

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
            Nav.OpenShop = () => _shop.Open();
            Nav.OpenWardrobe = () => _wardrobe.Open();
            Nav.StartGame = StartGameplay;

            WarmupFont();
            ShowOnly(_ctx.Flow.Current);

            // DEV: SDO_ROOM → boot straight into the waiting room (create a mock room + show it), for inspecting the
            // 3D room + ROOM UI without clicking through the lobby. Editor reads it from EditorPrefs, a build from env.
            if (!string.IsNullOrEmpty(ScreenGameplay.DevVar("SDO_ROOM"))) EnterRoom();
            // DEV: SDO_SHOP → boot into the waiting room then open the 商城 (shop) modal (Tools ▸ SDO ▸ Boot Into Shop).
            if (!string.IsNullOrEmpty(ScreenGameplay.DevVar("SDO_SHOP"))) { EnterRoom(); Nav.OpenShop?.Invoke(); }
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
            var s = _ctx.Session;
            if (!s.HasSong) { Toast.Show(LocalizationManager.Get("room.need_song")); return; }

            string musicDir = SdoExtracted.MusicDir;                                    // built: DATA/MUSIC; dev: sdox_offline/music
            string gnPath = Path.Combine(musicDir, s.SongGn);                           // e.g. .../MUSIC/sdom1197k.gn
            string oggBase = Regex.Match(s.SongGn ?? "", @"sdom\d+").Value;             // chart letter (k/t) dropped: sdom1197k -> sdom1197
            string oggPath = oggBase.Length > 0 ? Path.Combine(musicDir, oggBase + ".ogg") : null;

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
            game.localPlayerName = s.LocalPlayerName;             // 頭上名字 = 房間同一個名字 (玩家001…)
            game.localPlayerMale = s.Gender == 1;
            game.avatarParts = ProfileManager.Active != null ? ProfileManager.Active.EquippedAvatarParts() : game.avatarParts;
            game.dpsPath = "DANCE/" + s.SongFileId + ".DPS";     // per-song choreography (missing -> generic dance fallback)
            game.scenePath = "SCENE/" + s.StageFolder;           // selected 3D stage
            game.autoPlay = false;                               // real play (A/S/W/D + numpad), not the demo auto-player
            game.scrollSpeedMul = s.Speed;                       // 房間「速度」檔位 → 下落速度（固定基準140，osu式內部變速）
            game.roomNoteType = s.NoteType;                      // 房間 win2 選的 note 皮（-1=隨機, 0..10=指定, 10=3D）→ 開局套用同一個皮
            game.laneKeyOverride = DisplaySettingsManager.Settings?.keys?.ToLaneKeys(); // OPTION 鍵盤頁自訂鍵位（null → 預設 ASWD/numpad）
            game.showtimeMode = s.GameMode == 2;                 // 選歌模式選單：2 = ShowTime（氣條/集氣）模式；否則一般玩法
            var gp = DisplaySettingsManager.Settings?.gameplay;  // OPTION 遊戲頁偏好 → 開局套用
            if (gp != null)
            {
                game.effectCharacter = gp.effectCharacter;       // 人物特效（100/200/300 combo EFT）
                game.effectScene = gp.effectScene;               // 場景特效（常駐背景 EFT）
                game.cameraAuto = gp.cameraAuto;                 // 遊戲視角：默認(自動導播)/固定(鏡頭1)
                game.boardAlpha = gp.panelOpacity;               // 面板透明度（note 面板 alpha 倍率）
                game.playFullSong = gp.playFullSong;             // 進階「整首打完」：HP 歸零不立即退出，打到曲末
            }
            _activeGame = game;
        }

        // Result panel confirmed: ScreenGameplay already showed its own STATIS settlement (score / EXP / G幣 / replay),
        // so the front-end just tears the gameplay session down and returns to the room. (The legacy ResultsModal is
        // intentionally unused now that the play screen settles itself; kept built only so older call sites compile.)
        private void ReturnFromGameplay()
        {
            TeardownGameplay();
            _ctx.Flow.GoTo(ScreenId.Room);
        }

        // Esc during play: abandon the run with no settlement and go straight back to the room.
        private void AbortGameplay()
        {
            TeardownGameplay();
            _ctx.Flow.GoTo(ScreenId.Room);
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
