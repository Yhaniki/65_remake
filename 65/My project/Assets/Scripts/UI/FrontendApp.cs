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
        private SettingsModal _settings;
        private NoteSkinPicker _notePicker;
        private ResultsModal _results;
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

            // Fixed 800×600 (4:3) world-space canvas, framed by a camera the AspectController fits to the window
            // (stretched to fill by default) — same 4:3 frame as the play screen, so the whole app is consistent 4:3.
            var canvas = UIKit.CreateWorldCanvas("FrontendCanvas", new Vector2(800, 600), out _uiCam, 0);
            _canvasGo = canvas.gameObject;
            var root = (RectTransform)canvas.transform;
            UIKit.Stretch(UIKit.AddImage(root, "AppBg", UITheme.Bg).rectTransform);

            var screenLayer = UIKit.NewRect(root, "Screens");
            UIKit.Stretch(screenLayer);
            Make<LobbyScreen>(screenLayer);
            Make<RoomScreen>(screenLayer);
            Make<SongSelectScreen>(screenLayer);

            _ctx.Flow.ScreenChanged += (from, to) => ShowOnly(to);

            var modalLayer = UIKit.NewRect(root, "Modals");
            UIKit.Stretch(modalLayer);
            _settings = new GameObject("Settings").AddComponent<SettingsModal>();
            _settings.transform.SetParent(modalLayer, false);
            _settings.Build(modalLayer);
            _notePicker = new GameObject("NotePicker").AddComponent<NoteSkinPicker>();
            _notePicker.transform.SetParent(modalLayer, false);
            _notePicker.Build(modalLayer, _ctx.Session);
            _results = new GameObject("Results").AddComponent<ResultsModal>();
            _results.transform.SetParent(modalLayer, false);
            _results.Build(modalLayer);
            Toast.Init(modalLayer);

            Nav.OpenSettings = () => _settings.Open();
            Nav.OpenNoteSkinPicker = () => _notePicker.Open();
            Nav.StartGame = StartGameplay;

            WarmupFont();
            ShowOnly(_ctx.Flow.Current);

            // DEV: SDO_ROOM → boot straight into the waiting room (create a mock room + show it), for inspecting the
            // 3D room + ROOM UI without clicking through the lobby. Editor reads it from EditorPrefs, a build from env.
            if (!string.IsNullOrEmpty(ScreenGameplay.DevVar("SDO_ROOM"))) EnterRoom();
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
            game.dpsPath = "DANCE/" + s.SongFileId + ".DPS";     // per-song choreography (missing -> generic dance fallback)
            game.scenePath = "SCENE/" + s.StageFolder;           // selected 3D stage
            game.autoPlay = false;                               // real play (A/S/W/D + numpad), not the demo auto-player
            game.scrollSpeedMul = s.Speed;                       // 房間「速度」檔位 → 下落速度（固定基準140，osu式內部變速）
            game.coupleMode = (s.GameMode == 2);                 // 情侶模式(LOVER): 男女對跳+愛心+結尾拍照 (線上 client mode byte +0x62=0x0c)
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
                kv.Value.SetVisible(kv.Key == id);
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
