using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using Sdo.Game;
using Sdo.Game.Net;
using Sdo.Net;
using Sdo.Localization;
using Sdo.Settings;
using Sdo.UI.Catalog;
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
    public sealed partial class FrontendApp : MonoBehaviour
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
            || (_option != null && _option.IsOpen)
            || JoinRoomOpen;

        /// <summary>「輸入房號」框。選男女畫面按「加入」時自己叫它 <c>Open()</c> —— 加入流程的邏輯屬於那個畫面。</summary>
        public JoinRoomModal JoinRoom => _joinRoom;

        /// <summary>True while 輸入房號 框開著。背後的畫面用它讓自己的 ESC 處理讓路(同 <see cref="ShopOpen"/>)。</summary>
        public bool JoinRoomOpen => _joinRoom != null && _joinRoom.IsOpen;

        private AppContext _ctx;
        private readonly Dictionary<ScreenId, UIScreenBase> _screens = new Dictionary<ScreenId, UIScreenBase>();
        private OptionDlgModal _option;
        private NoteSkinPicker _notePicker;
        private ResultsModal _results;
        private ShopScreen _shop;
        private WardrobeScreen _wardrobe;
        private JoinRoomModal _joinRoom;
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

            // 依 config.ini 的 [Net] serverAddress 決定單機還是連線 —— 這是唯一的分流點。
            // 留空(預設)＝完全走原本的單機路徑,連線層一行都不會被建起來。
            _ctx = AppContext.Create();

            // OPTION 遊戲頁「遊戲畫面」偏好：全屏(填滿) = Stretch，視窗化(左右黑邊) = Pillarbox。必須在 CreateWorldCanvas
            // 註冊 UI 相機（→ AspectController 首次 Apply）之前設好靜態 Mode，之後開的遊戲相機也沿用同一個 Mode。
            AspectController.Mode = (DisplaySettingsManager.Settings?.gameplay?.fullscreenFill ?? false)
                ? AspectMode.Stretch : AspectMode.Pillarbox;

            // Fixed 800×600 (4:3) world-space canvas, framed by a camera the AspectController fits to the window
            // (stretched to fill by default) — same 4:3 frame as the play screen, so the whole app is consistent 4:3.
            var canvas = UIKit.CreateWorldCanvas("FrontendCanvas", new Vector2(800, 600), out _uiCam, 0);
            _canvasGo = canvas.gameObject;
            var root = (RectTransform)canvas.transform;
            UIKit.Stretch(UIKit.AddImage(root, "AppBg", UITheme.Bg).rectTransform);

            // Layers are created empty here; the (slow) screen/modal building + catalog parse + external Songs/ scan run
            // in BootCo behind a progress bar so the window shows a filling bar instead of a long black freeze. The
            // BootProgress overlay is created LAST inside BootCo so it renders above these layers.
            var screenLayer = UIKit.NewRect(root, "Screens");
            UIKit.Stretch(screenLayer);
            var modalLayer = UIKit.NewRect(root, "Modals");
            UIKit.Stretch(modalLayer);
            StartCoroutine(BootCo(root, screenLayer, modalLayer));
        }

        // Staged boot with a progress bar. The genuinely slow part is (a) the official catalog parse and (b) the
        // external Songs/ folder scan (reads + note-counts every candidate osu/StepMania chart); both advance the bar.
        //
        // config.ini 的 LoadExternalSongs=0 → 慢的那一半（掃歌）整個不跑，剩下的官方歌單解析＋建介面快到不值得
        // 蓋一張載入畫面上去，所以連 BootProgress 都不建（prog 保持 null，下面每個 Set 都是 no-op）——
        // 玩家看到的就是官方原本那樣直接進男/女選擇畫面，沒有黑底白條的載入過場。
        private IEnumerator BootCo(RectTransform root, RectTransform screenLayer, RectTransform modalLayer)
        {
            bool ext = RoomConfig.loadExternalSongs;
            var prog = ext ? BootProgress.Create(root) : null;   // last child of root → above the (empty) screen/modal layers
            if (prog != null) yield return null;                  // let the overlay render before any heavy work

            // Phase 1 — official song catalog (one atomic JsonUtility parse; coarse pre/post steps).
            prog?.Set(0.05f, "載入歌曲資料…");
            yield return null;
            var _ = SongCatalog.All;   // force EnsureLoaded (the big catalog parse + name overrides)
            prog?.Set(0.15f, "載入歌曲資料…");
            yield return null;

            // Phase 2 — scan DATA/ADDON/SONG (+ legacy Songs/ + AdditionalSongFolders) for osu/StepMania songs. The
            // ADDON plugin folders are created first so a fresh install shows the player where to drop songs. The bar's
            // sub-label shows the folder being read and its detail line the current song + running count.
            // 外部歌曲關掉時整個 Phase 2 跳過：不建 ADDON 資料夾（不然關著功能還在硬碟上長出空資料夾），也不掃。
            if (ext)
            {
                SdoExtracted.EnsureAddonDirs();
                yield return ExternalSongLibrary.ScanAndRegisterCo((f, folder, detail) =>
                    prog?.Set(0.15f + 0.55f * Mathf.Clamp01(f),
                              string.IsNullOrEmpty(folder) ? "掃描歌曲資料夾…" : folder, detail));
            }

            // Phase 3 —— 連線(只有 config.ini 填了 [Net] serverAddress 才會走到)。
            // 連線在 AppContext.Create 就已經開始了(背景 thread),這裡只是等它完成並顯示進度。
            //
            // 🔴 順序很重要:這一段**必須在建畫面之前**。連不上的時候它會把 _ctx 換成單機版,
            // 而畫面是在 Build(ctx) 時把 ctx 抓進自己的欄位的 —— 先建畫面就會抓到一個已經死掉的連線,
            // 而且畫面的版面(選男女畫面的按鈕是兩顆還是三顆)也是依連線狀態決定的,晚了就來不及。
            yield return WaitForConnectionCo(prog);

            // Phase 4 — build the screens (SongSelect now sees the external songs registered above).
            prog?.Set(0.78f, "建立介面…");
            yield return null;
            Make<GenderSelectScreen>(screenLayer);   // 單機開場的男/女選擇畫面（Flow 的入口狀態）
            Make<LobbyScreen>(screenLayer);
            Make<RoomScreen>(screenLayer);
            Make<SongSelectScreen>(screenLayer);
            _ctx.Flow.ScreenChanged += (from, to) => { ShowOnly(to); UpdateBgm(to); };

            // Phase 5 — modals + Nav wiring.
            prog?.Set(0.87f, "建立介面…");
            yield return null;
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
            // 「輸入房號」框(選男女畫面按加入時彈)。單機也建 —— 建一個隱藏的 modal 沒有成本,
            // 而且不必在兩條路徑上各寫一次 null 判斷。
            _joinRoom = new GameObject("JoinRoom").AddComponent<JoinRoomModal>();
            _joinRoom.transform.SetParent(modalLayer, false);
            _joinRoom.Build(modalLayer);
            Toast.Init(modalLayer);

            Nav.OpenSettings = () => _option.Open();
            Nav.OpenNoteSkinPicker = () => _notePicker.Open();
            Nav.OpenShop = () => ScreenTransition.Run(() => _shop.Open());   // 進商城：漸黑 → loading → 漸亮（同房間進出效果）
            Nav.OpenWardrobe = () => _wardrobe.Open();                        // 儲物櫃有自己的視窗開闔動畫(WindowAnim)，不套轉場
            Nav.StartGame = StartGameplay;
            // 進房間轉場漸亮時，房間 UI 從四邊滑入（男女選擇→房間、遊戲→房間 共用；商城進出不觸發，房間仍在底下）。
            Nav.PlayRoomEntrance = () => { if (_screens.TryGetValue(ScreenId.Room, out var r) && r is RoomScreen rr) rr.PlayEntrance(); };

            // Phase 6 — font atlas warmup (rasterises the CJK glyphs of the visible song titles).
            prog?.Set(0.94f, "準備字型…");
            yield return null;
            WarmupFont();
            prog?.Set(1f, "");
            yield return null;

            prog?.Destroy();
            ShowOnly(_ctx.Flow.Current);
            UpdateBgm(_ctx.Flow.Current);   // 開場即起隨機大廳 BGM(男/女選擇畫面)

            // DEV: SDO_ROOM → boot straight into the waiting room (create a mock room + show it), for inspecting the
            // 3D room + ROOM UI without clicking through the lobby. Editor reads it from EditorPrefs, a build from env.
            if (!string.IsNullOrEmpty(ScreenGameplay.DevVar("SDO_ROOM"))) EnterRoom();
            // DEV: SDO_SHOP → boot into the waiting room then open the 商城 (shop) modal (Tools ▸ SDO ▸ Boot Into Shop).
            if (!string.IsNullOrEmpty(ScreenGameplay.DevVar("SDO_SHOP"))) { EnterRoom(); Nav.OpenShop?.Invoke(); }
            // DEV: SDO_JOINDLG → 開機直接彈「輸入房號」框,用來截圖檢查那個框的排版。
            // (它是官方密碼框抹掉字後疊 TMP 的,字沒對準只有實機截圖看得出來 —— 不能只信烘圖工具的輸出。)
            if (!string.IsNullOrEmpty(ScreenGameplay.DevVar("SDO_JOINDLG")) && _joinRoom != null)
                _joinRoom.Open(_ => { });
            // DEV: SDO_JOINFIRST=1 → 開機直接加入 server 上第一間房。
            // 同機多開兩份 client 測連線時,房號是 server 隨機配的 —— 這個 hook 讓第二份不必把房號抄過去,
            // 直接問 roomList 拿第一間。要先有另一份 client(SDO_ROOM=1)開好房。
            if (!string.IsNullOrEmpty(ScreenGameplay.DevVar("SDO_JOINFIRST")) && _ctx.Net != null)
                StartCoroutine(DevJoinFirstRoomCo());
        }

        /// <summary>SDO_JOINFIRST 的實作:問房間列表 → 加入第一間 → 進房間畫面。純除錯用。</summary>
        private IEnumerator DevJoinFirstRoomCo()
        {
            var net = _ctx.Net;
            Sdo.Net.NetRoomListEntry[] rooms = null;
            net.RequestRoomList(r => rooms = r);

            float deadline = Time.realtimeSinceStartup + 5f;
            while (rooms == null && Time.realtimeSinceStartup < deadline) yield return null;
            if (rooms == null || rooms.Length == 0)
            {
                Debug.LogWarning("[dev] SDO_JOINFIRST:server 上沒有房間(先讓另一份 client 用 SDO_ROOM=1 開房)");
                yield break;
            }

            // 挑**人最多**的那間,不是第一間 —— 之前跑測試留下的空房會排在前面,
            // 挑到那間就會看到一間只有自己的房間(而且症狀跟「加入失敗」長得一樣)。
            int pick = 0;
            for (int i = 1; i < rooms.Length; i++)
                if (rooms[i].Count > rooms[pick].Count) pick = i;
            int code = rooms[pick].Code;
            // 與玩家真的按「加入」走同一條政策(座位滿了自動轉旁觀)—— dev 路徑自己寫一份的話,
            // 「滿房自動旁觀」就只有其中一條路測得到。
            net.JoinOrSpectate(code,
                (result, asSpectator) =>
                {
                    if (result == Sdo.Net.NetProto.JoinOk)
                    {
                        Debug.Log("[dev] SDO_JOINFIRST:進了房 " + code + (asSpectator ? "(旁觀身分)" : "(座位)"));
                        _ctx.Flow.GoTo(ScreenId.Room);
                        return;
                    }
                    Debug.LogWarning("[dev] SDO_JOINFIRST:加入 " + code + " 失敗:" + result);
                },
                trigger => Debug.Log("[dev] SDO_JOINFIRST:房間 " + code + " 回了 " + trigger + " → 改用旁觀身分"));
        }

        /// <summary>
        /// 等連線握手完成。單機模式(<c>_ctx.Net == null</c>)直接跳過。
        ///
        /// **連不上就退回單機,不會卡在開機畫面** —— 這很重要:玩家可能只是忘了關掉
        /// config.ini 的 serverAddress,或伺服器剛好沒開。那種情況下讓他能照常單機玩,
        /// 遠比讓他盯著一個永遠不動的進度條好。
        /// </summary>
        private IEnumerator WaitForConnectionCo(BootProgress prog)
        {
            var net = _ctx.Net;
            if (net == null) yield break;

            // prog 可能是 null:config.ini 關掉外部歌曲(LoadExternalSongs=0)時整張載入畫面都不建(見 BootCo)。
            prog?.Set(0.72f, "連線伺服器…", Sdo.Settings.RoomConfig.serverAddress);

            float deadline = Time.realtimeSinceStartup + ConnectTimeoutSec;
            while (Time.realtimeSinceStartup < deadline)
            {
                net.Pump();   // 握手是在 Pump 裡送出與收下的

                if (net.IsConnected)
                {
                    prog?.Set(0.76f, "已連上伺服器", Sdo.Settings.RoomConfig.serverAddress);
                    net.ErrorReceived += OnNetError;   // 見 OnNetError:沒接的話這些錯誤全被丟掉
                    _netReady = true;
                    yield break;
                }

                if (net.LinkState == NetLinkState.Failed || net.LinkState == NetLinkState.Closed)
                    break;

                yield return null;
            }

            // 逾時或失敗 → 退回單機。**只寫 log,不告訴玩家。**
            // 沒填 serverAddress 或伺服器沒開的人,本來就是要單機玩的 —— 對他們來說「連不上」
            // 不是壞消息也不是他能處理的事,一進畫面就彈一句話只是把單機開場弄髒。
            string why = string.IsNullOrEmpty(net.LastError) ? "連線逾時" : net.LastError;
            Debug.LogWarning("[net] 連不上伺服器,改用單機模式:" + why);
            net.Disconnect("bootFailed");
            _ctx = AppContext.CreateMock();
        }

        /// <summary>開機連線的等待上限。超過就退回單機。</summary>
        private const float ConnectTimeoutSec = 6f;

        /// <summary>
        /// server 回的 <c>error{code}</c> —— **沒有被任何請求認領的那些**。
        ///
        /// 🔴 這個訂閱以前不存在,於是那些錯誤整個被丟掉:按「旁觀」但旁觀席滿了、
        /// 旁觀者想搶回座位但沒空位、非房主誤送 host-only 操作…… 玩家看到的都是
        /// 「按了沒反應」,而 log 也只有 server 那邊有。連線層的原則是不做樂觀更新
        /// (按了不會先改畫面),所以**失敗一定要說出來**,否則就變成靜默失敗。
        ///
        /// (帶 rq 且有人在等的錯誤不會走到這裡 —— 那些由發起請求的地方自己處理,
        ///  例如加入房間的失敗原因是寫在輸入房號的框裡。)
        /// </summary>
        /// <summary>
        /// server 回絕某個操作。**一律只寫 log,不彈 toast。**
        ///
        /// 這些全是「按了但條件不符」的例行拒絕(不是房主、房間開打了、座位滿了…),而畫面本身
        /// 已經表達了狀態 —— 不是房主就沒有房主的按鈕、房間滿了列表上就寫著人數。彈出來只是把
        /// 畫面弄髒,而真正需要追原因時看的是 log。log 印本地化後的同一句話,不必再對照 code。
        /// </summary>
        private void OnNetError(string code, string msg)
        {
            string key = NetErrorKey(code);
            string human = key != null ? LocalizationManager.Get(key) : null;
            Debug.LogWarning("[net] server error: " + code
                + (human != null ? " — " + human : "")
                + (string.IsNullOrEmpty(msg) ? "" : " (" + msg + ")"));
        }

        /// <summary>error code → 本地化 key,**只給 log 用**(回 null = 沒有對應的人話,印 code 就好)。</summary>
        private static string NetErrorKey(string code)
        {
            switch (code)
            {
                case Sdo.Net.NetProto.ErrNotHost:    return "neterr.not_host";
                case Sdo.Net.NetProto.ErrNotInRoom:  return "neterr.not_in_room";
                case Sdo.Net.NetProto.ErrBadSeat:    return "neterr.bad_seat";
                case Sdo.Net.NetProto.ErrBadState:   return "neterr.bad_state";
                case Sdo.Net.NetProto.ErrNoSong:     return "neterr.no_song";
                case Sdo.Net.NetProto.ErrFull:       return "neterr.full";
                case Sdo.Net.NetProto.ErrLookerFull: return "neterr.looker_full";
                case Sdo.Net.NetProto.ErrBadTeams:   return "room.teams_need_layout";   // 已經有一句更精確的
                case Sdo.Net.NetProto.ErrProto:      return "neterr.proto";
                default: return null;   // rateLimit / badJson:沒有對應的人話,log 印 code
            }
        }

        private bool _netReady;

        // 大廳系畫面(男/女選擇 + ROOM)播 UI/BGM 資料夾的隨機 BGM(不連續重複)並淡回;選歌畫面=淡出禁音但軌道繼續播
        // (離開選歌回房間再淡回同一首);遊戲(有歌)/Lobby 才真的停。商城是疊在 ROOM/GenderSel 上的 modal(不改 Flow)→ BGM 持續。
        private static void UpdateBgm(ScreenId to)
        {
            if (to == ScreenId.GenderSel || to == ScreenId.Room) { BgmPlayer.Play(); BgmPlayer.SetMuted(false); }
            else if (to == ScreenId.SongSelect) BgmPlayer.SetMuted(true);   // 線性淡出 0.2s → 禁音,仍在播
            else BgmPlayer.Stop();
        }

        /// <summary>
        /// 關掉連線。正常退出時呼叫 —— 讓 server 立刻知道我們走了,
        /// 而不是等 15 秒的 ping 逾時才把座位清掉(那段時間別人會看到一個不動的幽靈玩家)。
        /// </summary>
        private void OnApplicationQuit()
        {
            if (_ctx != null && _ctx.Net != null) _ctx.Net.Disconnect("appQuit");
        }

        private void OnDestroy()
        {
            if (_ctx != null && _ctx.Net != null) _ctx.Net.Disconnect("appDestroy");
        }

        /// <summary>Create a mock room (host = local player) if none, and show the waiting room. Used by the SDO_ROOM
        /// dev hook and the room capture test.</summary>
        public void EnterRoom()
        {
            if (_ctx == null) return;
            if (_ctx.Rooms.CurrentRoom != null) { _ctx.Flow.GoTo(ScreenId.Room); return; }

            // 線上模式:建房是非同步的(要等 server 配房號),所以在回呼裡才切畫面。
            if (_ctx.Net != null)
            {
                _ctx.Net.CreateRoom("", (result, code) =>
                {
                    if (result == Sdo.Net.NetProto.JoinOk) { _ctx.Flow.GoTo(ScreenId.Room); return; }
                    // server 回的是協定代碼(full / …)。原本直接貼在畫面上,玩家看到的是
                    // 「建立房間失敗:full」—— 半句英文,而且沒說接下來能做什麼。
                    Debug.LogWarning("[net] createRoom 失敗:" + result);
                    Toast.Show(LocalizationManager.Get(result == Sdo.Net.NetProto.JoinFull
                        ? "room.create_failed_full" : "room.create_failed"));
                });
                return;
            }

            _ctx.Rooms.CreateRoom(Sdo.UI.Services.GameMode.Normal);
            _ctx.Flow.GoTo(ScreenId.Room);
        }

        private void Update()
        {
            // 🔴 連線 pump 必須在最前面,而且在 `if (_activeGame != null)` **之外** ——
            // 房間狀態、聊天、開場通知在遊戲中與不在遊戲中都要收。
            // (原本整個 hotkey 區塊被圈在「遊戲中」,很容易誤把 pump 也放進去。)
            if (_ctx != null && _ctx.Net != null) _ctx.Net.Pump();
            TickNetGameplay();   // 遊玩中每 200ms 把本機成績送上去(見那邊的註解)
            // 缺歌傳檔:同樣要在遊戲中也繼續跑 —— 下載可能跨過「別人在打歌、我留在房間」那段。
            NetSongTransfer.Tick(_ctx, this);

            _ctx?.Chat?.Tick();
            if (_killGuardFrames > 0 && _activeGame == null) { _killGuardFrames--; KillStrayGameplay(); }
            if (_activeGame != null)
            {
                if (!_activeGame.Finished)
                {
                    // 中離（預設 ESC，可在 DATA/PROFILE/keymaps.ini 的 [Hotkeys] quit 改）：不結算直接退出。
                    if (KeyMap.Down(Hotkey.Quit)) AbortGameplay();
                    // 旁觀退出(需求 10):Ctrl+Q → 直接離開房間回選角色畫面。
                    // 只在旁觀時吃 —— 參賽者按到不能把自己踢出比賽。
                    else if (_activeGame.spectatorMode && CtrlHeld() && KeyMap.Down(Hotkey.SpectatorQuit))
                        QuitSpectating();
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
            // 沒選歌就不開場 —— 只寫 log:房間面板的歌名欄是空的,那已經說明了一切。
            if (!s.HasSong) { Debug.Log("[room] " + LocalizationManager.Get("room.need_song")); return; }

            // 隨機難度：房間只鎖定「難度範圍」(SongRandomRange)，實際歌曲/難度到這裡(進遊戲)才抽 → 每局重抽，
            // 同一個隨機設定每次進遊戲都是不同歌。easy/normal/hard 一起搜(見 SongListModel.RandomCandidates)。
            // 🔴 連線時**不要**重抽:這一場要玩哪一首是 server echo 的(RoomScreen.ApplyResolvedRound 已經套好),
            //    每台自己再抽一次就會各玩一首歌。s.SongIsRandom 在套用 resolved 時已被清掉,這個判斷是第二道保險。
            bool online = _ctx.Net != null && _ctx.Net.Match != null;
            if (s.SongIsRandom && !online)
            {
                var pool = SongListModel.RandomCandidates(SongListModel.FromCatalog().All, s.SongRandomRange);
                if (pool.Count > 0)
                {
                    var cand = pool[Random.Range(0, pool.Count)];
                    s.SongGn = cand.Song.gn;
                    s.SongFileId = cand.Song.fileId;
                    s.SongArtist = cand.Song.artist;
                    s.Difficulty = (Difficulty)cand.Difficulty;
                }
            }

            string gnPath = SongPaths.Gn(s.SongGn);     // e.g. .../MUSIC/sdom1197k.gn（SongPaths 內部走 SdoExtracted.MusicDir）
            string oggPath = SongPaths.Ogg(s.SongGn);   // chart letter (k/t) dropped: sdom1197k -> sdom1197.ogg

            // Snapshot the current scene roots (canvas, EventSystem, Main Camera, …) so TeardownGameplay can destroy
            // exactly what ScreenGameplay spawns (it parents nothing to us — every board/avatar/scene object is a new root).
            _preGameRoots = new HashSet<GameObject>(SceneManager.GetActiveScene().GetRootGameObjects());

            _ctx.Flow.GoTo(ScreenId.Gameplay);
            if (_canvasGo != null) _canvasGo.SetActive(false);
            if (_uiCam != null) _uiCam.enabled = false;   // stop the UI cam clearing over the play screen

            var game = new GameObject("ScreenGameplay").AddComponent<ScreenGameplay>();   // fields read in its Start() next frame
            if (s.IsExternalSong)
            {
                // external osu/StepMania (user Songs/ folder): ScreenGameplay.LoadChart parses chartPath directly,
                // bypassing .gn; audio is the resolved file (ogg/mp3/wav). There is no official DANCE/<negId>.DPS, so
                // LoadChart generates the song's choreography from these two (ExternalDps: once, deterministically,
                // recorded in the song folder's sdoinfo.dat) instead of looping the single fallback clip.
                // 外部歌才需要:把「解 mp3」換成有快取/預抓的版本 —— 選歌確認時(OnConfirm)已背景預解,這裡
                // 命中就秒進;沒命中(random 歌 / retry)也只是照常背景解,不會更糟。sync 由 LoadAndPlayAudio
                // 自己用 Mp3SyncFor 算,GameplaySongAudioCache 只照收到的 sync 存取,key 也含 sync,不會串位置。
                game.mp3Decoder = GameplaySongAudioCache.Get;
                game.chartFormat = s.ExternalChartFormat;
                game.chartPath = s.ExternalChartPath;
                game.chartIndex = s.ExternalChartIndex;
                game.chartSeed = s.ExternalChartSeed;   // .gn 歌曲包：這首譜自己的解密金鑰
                game.chartLevel = s.ExternalLevel;
                game.gnPath = "";
                game.oggPath = s.ExternalAudioPath;
                game.externalFolder = s.ExternalFolderPath;
                game.externalSongKey = s.ExternalSongKey;
                // 生成編舞吃「這首歌的」BPM 跟「所有難度的譜」，不是只看選到這張（換難度不換舞，見 Sdo.Osu.DanceInputs）
                game.songBpm = s.ExternalSongBpm;
                game.songChartPaths = s.ExternalSongChartPaths;
                game.songChartIndices = s.ExternalSongChartIndices;
                game.songDisplayName = s.SongTitle;   // catalog display name (osu pack → real song name), not the .osu pack-label Title
            }
            else
            {
                game.gnPath = gnPath;
                game.oggPath = oggPath;
            }
            game.difficulty = (int)s.Difficulty;                 // Easy/Normal/Hard -> 0/1/2
            game.songOffsetMs = SongCatalog.OffsetMs(s.SongGn);  // 這首譜自己的 offset（手改在 song_table.csv 的 offsetMs）
            game.dpsOffsetMs = SongCatalog.DpsOffsetMs(s.SongGn); // 舞蹈**獨立** offset（外部歌 sidecar 的 #DPSOFFSETMS，預設 0）
            game.localPlayerName = s.LocalPlayerName;             // 頭上名字 = 房間同一個名字 (玩家001…)
            game.localPlayerMale = s.Gender == 1;
            game.avatarParts = ProfileManager.Active != null ? ProfileManager.Active.EquippedAvatarParts() : game.avatarParts;
            if (ProfileManager.Active != null) game.bodyShapeIndex = ProfileManager.Active.bodyShapeIndex;   // 遊戲舞者用這個角色自己的體型 (胖瘦)
            game.playerLevel = ProfileManager.Level;             // 這個角色的等級（profile.json，沒設過吃 config.ini 預設）→ 結算 G幣/榮譽獎勵照它算
            // per-song choreography (missing -> generic dance fallback). A .gn 歌曲包 ships the song's OWN official
            // .DPS next to it — an absolute path, which LoadAsset takes as-is, so it dances the real choreography
            // instead of the one ExternalDps would generate.
            game.dpsPath = !string.IsNullOrEmpty(s.ExternalDpsPath) ? s.ExternalDpsPath : "DANCE/" + s.SongFileId + ".DPS";
            game.scenePath = "SCENE/" + s.StageFolder;           // selected 3D stage
            // DEV: SDO_AUTOPLAY=1 → 用內建的 demo auto-player 代打。
            // 驗連線的分數流需要「分數真的會漲」,而亂按 lane 鍵在節奏遊戲裡幾乎全是 MISS
            // (負分被夾到 0)→ 兩台都停在 0,證明不了任何事。autoPlay 打得準,分數才會動。
            game.autoPlay = !string.IsNullOrEmpty(ScreenGameplay.DevVar("SDO_AUTOPLAY"));
            game.scrollSpeedMul = s.Speed;                       // 房間「速度」檔位 → 下落速度（固定基準 config.ini scrollBaseBpm，osu式內部變速）
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
                game.collapseShortHolds = gp.collapseShortHolds; // 無理短長條(<180BPM 16分)收成一般 note；只對外部轉檔譜(osu/sm/mc)，官方/歌曲包 .gn 不動
                game.constantScroll = !gp.songSpeed;             // 進階「歌曲變速」關 → 整首固定流速（忽略譜面 BPM 變化 / SV）
                game.songBombs = gp.songBombs;                   // 進階「歌曲炸彈」關 → 載譜時把譜面上的炸彈整顆拿掉
            }
            WireNetGameplay(game);
            _activeGame = game;
        }

        // ---- 連線:同步進場 ------------------------------------------------------------------------------------
        // 三件事:
        //   ① 本機載完了 → setPlayState(loaded) 再 readyForGameplay(兩段式,照 osu:loaded=程式載完、
        //      readyForGameplay=人準備好;server 的推進條件只看「沒人還在 waitingForLoad」)。
        //   ② ReadyGate:等 server 廣播 gameplayStarted 才放行 → 所有人同一刻開場。
        //   ③ 🔴 **逃生**:server 有 30 秒載入逾時(R15)會強制推進,但萬一那個廣播沒到(掉包/斷線),
        //      這邊不能永遠停在 loading 畫面。所以本機也放一條逾時,時間比 server 的長一點
        //      (讓 server 先處理;它處理完就會廣播,正常情況永遠用不到這條)。
        private bool _netGateOpenSeen;
        private float _netGateArmedRt;
        private long _netMatchId;
        private const float NetGateLocalTimeoutSec = 45f;   // > server 的 LoadTimeoutMs(30s)
        private const float NetResultAutoConfirmSec = 30f;  // 連線:結算面板放著沒按 → 30 秒後自動確定回房間

        private void WireNetGameplay(ScreenGameplay game)
        {
            var net = _ctx.Net;
            var match = net != null ? net.Match : null;
            if (net == null || match == null) return;   // 離線/單機 → ReadyGate 留 null,行為與加連線之前一樣

            _netMatchId = match.MatchId;
            _netGateOpenSeen = false;
            _netGateArmedRt = Time.realtimeSinceStartup;
            game.playerCount = Mathf.Max(1, match.Participants.Length);
            // 隊形:**只信 server echo 的那份**(隨機隊形是房主抽的,server 驗過範圍再發給所有人)。
            // 各台自己讀 GameSession.Formation 的話,隨機那格會各抽一次 → 每台的站位都不一樣。
            if (match.Resolved != null)
            {
                game.formationType = match.Resolved.FormationType;
                // 組隊站位版型(-1 = 不組隊)。同理只信 server echo 的那份 ——
                // 各台自己算會用不同時刻的人數快照算出不同版型。
                game.teamLayout = (int)match.Resolved.TeamLayout;
            }
            FillNetDancers(game, match, net.UserId);

            // 這一行是「隨機值有沒有同步」的唯一客觀證據:兩台的這一行**必須逐字相同**
            // (使用者的原話:「就算是 Room 裡面隨機場景,也要隨機到一樣的」)。
            // 靠截圖比對場景很難說得準 —— 兩張圖看起來像不像不是證據,這行字一樣才是。
            // tools\verify_online.ps1 就是拿兩邊的這行做 diff。
            Debug.Log("[net] resolved match=" + match.MatchId
                      + " scene=" + (match.Resolved != null ? match.Resolved.SceneId : -1)
                      + " formation=" + game.formationType
                      + " teamLayout=" + game.teamLayout
                      + " randomSong=" + (match.Resolved != null && match.Resolved.IsRandomSong
                                          ? match.Resolved.RandomSong.Gn : "-")
                      + " dancers=" + game.playerCount
                      + " spectator=" + (!net.IsMatchParticipant));

            // 旁觀(需求 10):不是這一場的參與者 → 只看別人跳舞。
            // 判斷用 server 給的參與者名單,不是本機的「我按了旁觀鈕嗎」—— server 才是唯一權威
            // (它可能因為缺歌/沒準備而把你排除在這一場之外,那時你也是旁觀者)。
            game.spectatorMode = !net.IsMatchParticipant;
            // 旁觀名單:server 在 matchStarting 裡帶了真名(需求 10:不要假名)。
            // 🔴 連線時**一律**建那排 label(不是「開場那一刻有人旁觀才建」)。
            // 依當下人數決定的話,開局沒人旁觀 → _lookerRows 根本沒建 → 之後有人進來旁觀,
            // SetSpectatorNames 沒有東西可以寫,那個人永遠不會出現在名單上。
            // 空的列本來就不顯示(ApplySpectatorNames 會把沒人的那幾列關掉),所以先建不會有副作用。
            game.showSpectators = true;
            game.spectatorNames = match.SpectatorNames ?? new string[0];

            game.LocalReady = () =>
            {
                // 旁觀者不送:server 的 setPlayState 只認**這一場的參與者**(座位上的人),旁觀送過去
                // 一律回 notInRoom —— server log 上那兩行「✗ user N 的請求被拒:notInRoom」就是它
                // (loaded + readyForGameplay 各一行)。而且他本來就不該參與「等所有人載完才開場」的
                // 同步:他要看的就是別人開場,自己載完直接看。
                if (!net.IsMatchParticipant) return;
                net.SetPlayState(Sdo.Net.PlayState.Loaded, _netMatchId);
                net.SetPlayState(Sdo.Net.PlayState.ReadyForGameplay, _netMatchId);
            };
            game.ReadyGate = () =>
            {
                if (net.GameplayGateOpen) _netGateOpenSeen = true;
                if (_netGateOpenSeen) return true;
                if (Time.realtimeSinceStartup - _netGateArmedRt > NetGateLocalTimeoutSec)
                {
                    Debug.LogWarning("[net] gameplayStarted 沒收到,本機逾時後照樣開場(match " + _netMatchId + ")");
                    _netGateOpenSeen = true;
                    return true;
                }
                return false;
            };

            // ---- 分數流 ----
            _netOpponents.Clear();
            _netResultRows = null;
            _netFrameNextAt = 0f;
            _netPlayFinishedSent = false;
            net.FramesReceived += OnNetFrames;
            net.ResultsReady += OnNetResults;
            net.ComboMilestoneReceived += OnNetComboMilestone;
            // 右側名單/名次:讀 server 推來的最新一筆。**不做插值/推測** —— 分數是別人的權威資料,
            // 猜出來的數字會讓名次在兩台上不一樣。
            game.NetOpponents = () =>
            {
                if (_netOpponents.Count == 0) return _netOpponentsEmpty;
                var arr = new ScreenGameplay.NetPlayerScore[_netOpponents.Count];
                int i = 0;
                foreach (var kv in _netOpponents)
                    arr[i++] = new ScreenGameplay.NetPlayerScore
                    {
                        UserId = kv.Key,
                        Name = kv.Value.Name,
                        Score = kv.Value.Score,
                        Combo = kv.Value.Combo,
                        Perfect = kv.Value.Perfect, Cool = kv.Value.Cool,
                        Bad = kv.Value.Bad, Miss = kv.Value.Miss,
                    };
                return arr;
            };
            game.NetLeaderUserId = () => net.LeaderUserId;
            game.NetResultRows = () => _netResultRows;
            // 結算畫面沒人按確定 → 30 秒後自己按(ResultScreen 會走 OnConfirm,跟按確定完全同一條路:
            // 送 playFinished、拆遊戲、轉場回房間)。連線才需要 —— 一個人掛在結算畫面,整間房都開不了下一局。
            game.resultAutoConfirmSec = NetResultAutoConfirmSec;
            game.LocalComboMilestone = combo => net.SendComboMilestone(_netMatchId, combo);
        }

        // ---- 分數流:收 / 送 ------------------------------------------------------------------------------------

        // 一位遠端玩家的最新一筆。除了名字/分數,還要帶判定計數與 combo ——
        // 遠端舞者的跳/停是從相鄰兩筆的差推出來的(Sdo.Ruleset.DanceGate)。
        private sealed class NetOppState
        {
            public string Name;
            public long Score;
            public int Combo;
            public int Perfect, Cool, Bad, Miss;
        }
        private readonly Dictionary<int, NetOppState> _netOpponents = new Dictionary<int, NetOppState>();
        private static readonly ScreenGameplay.NetPlayerScore[] _netOpponentsEmpty = new ScreenGameplay.NetPlayerScore[0];
        private ResultScreen.Row[] _netResultRows;
        private float _netFrameNextAt;
        private bool _netPlayFinishedSent;
        private const float NetFrameIntervalSec = 0.2f;   // 5 Hz;server 也是 5 Hz 往下推(NetLimits.ServerFrameHz)

        private void OnNetFrames(NetFrameRow[] rows)
        {
            if (rows == null) return;
            var net = _ctx.Net;
            int me = net != null ? net.UserId : 0;
            var match = net != null ? net.Match : null;
            for (int i = 0; i < rows.Length; i++)
            {
                var r = rows[i];
                if (r.UserId == me) continue;              // 自己的那筆用本機真值,不要繞一圈回來
                NetOppState st;
                if (!_netOpponents.TryGetValue(r.UserId, out st))
                {
                    st = new NetOppState { Name = MatchNameOf(match, r.UserId) };
                    _netOpponents[r.UserId] = st;
                }
                st.Score = r.Score;
                st.Combo = r.Combo;
                st.Perfect = r.Perfect; st.Cool = r.Cool; st.Bad = r.Bad; st.Miss = r.Miss;
            }
        }

        /// <summary>
        /// 把這一場的參與者灌進打歌畫面(每個人自己的性別/穿搭/體型/名字),**依座位序**。
        ///
        /// 🔴 順序一定要是座位序而且每台一致 —— 隊形的 slot 指派是照這個順序算的
        /// (`FormationAssignment.SlotForDancer`),順序不同的話同一個人在不同人的畫面上站不同格。
        /// server 已經是照座位序發 participants 的,這裡再排一次是為了不依賴那個順序
        /// (協定上沒有保證,而依賴一個沒寫進協定的順序正是最難查的那種 bug)。
        /// </summary>
        private static void FillNetDancers(ScreenGameplay game, NetMatchStart match, int myUserId)
        {
            var src = match != null ? match.Participants : null;
            if (src == null || src.Length == 0) { game.netDancers = null; game.localDancerIndex = 0; return; }

            var list = new List<NetMatchParticipant>(src);
            list.Sort((a, b) => a.Seat != b.Seat ? a.Seat.CompareTo(b.Seat) : a.UserId.CompareTo(b.UserId));

            var arr = new ScreenGameplay.DancerInfo[list.Count];
            int localIdx = -1;
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                arr[i] = new ScreenGameplay.DancerInfo
                {
                    UserId = p.UserId,
                    Name = p.Name ?? "",
                    Male = p.Look != null && p.Look.Male,
                    Parts = p.Look != null ? p.Look.Parts : null,
                    BodyIndex = p.Look != null ? p.Look.BodyIndex : 0,
                    Team = p.Team,
                };
                if (p.UserId == myUserId) localIdx = i;
            }
            game.netDancers = arr;
            // 旁觀者不在名單裡 → -1(它沒有自己的舞者,但別人的照出)。
            game.localDancerIndex = localIdx;
            Debug.Log("[dancers] 這一場 " + arr.Length + " 位舞者,本機是第 " + localIdx + " 位");
        }

        private static string MatchNameOf(NetMatchStart match, int userId)
        {
            if (match != null && match.Participants != null)
                for (int i = 0; i < match.Participants.Length; i++)
                    if (match.Participants[i].UserId == userId) return match.Participants[i].Name ?? "";
            return "";
        }

        private void OnNetResults(NetResultRow[] rows)
        {
            if (rows == null || rows.Length == 0) { _netResultRows = null; return; }
            int me = _ctx.Net != null ? _ctx.Net.UserId : 0;
            var outRows = new ResultScreen.Row[rows.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                var r = rows[i];
                int judged = Mathf.Max(1, r.Perfect + r.Cool + r.Bad + r.Miss);
                outRows[i] = new ResultScreen.Row
                {
                    Rank = i + 1,                       // server 已經照分數排好了
                    UserId = r.UserId,
                    Name = r.Name ?? "",
                    IsLocal = r.UserId == me,
                    Score = r.Score,
                    Perfect = r.Perfect, Cool = r.Cool, Bad = r.Bad, Miss = r.Miss,
                    MaxCombo = r.MaxCombo,
                    Accuracy = (r.Perfect + r.Cool) * 100.0 / judged,
                    Grade = Sdo.Ruleset.Grade.FromAccuracy((r.Perfect + r.Cool) * 100.0 / judged),
                    FullCombo = (r.Bad + r.Miss) == 0,
                };
            }
            _netResultRows = outRows;
            _activeGame?.RefreshNetResultRows();
        }

        /// <summary>
        /// 遊玩中每 200ms 把本機成績送上去,曲末送一次 playFinished。
        /// 由 Update 呼叫(<see cref="_activeGame"/> 活著時)。
        ///
        /// 🔴 playFinished 一定要送,而且中途離開(Esc)也要送:不送的話房間會卡在 playing,
        /// 要等 server 的逾時才恢復,那段時間誰都不能再開一局。
        /// </summary>
        private void TickNetGameplay()
        {
            var net = _ctx.Net;
            if (net == null || net.Match == null || _activeGame == null) return;
            SyncSpectatorNames(net);               // 中途有人進來/離開旁觀 → 右側名單要跟著變(旁觀者與參賽者都看得到)
            if (!net.IsMatchParticipant) return;   // 旁觀者不送成績

            // 🔴 曲末就送 playFinished,**不要等玩家把結算畫面關掉**。
            // 結算畫面是在等按鍵的:沒人在鍵盤前面(或有人去泡茶)的話,server 那邊
            // 這一場永遠不會結束 —— 房間卡在 playing、誰都不能再開一局,而畫面上一切正常。
            // (實機驗證抓到的:兩台都打完了,server 的 log 就是沒有「場結算」那一行。)
            // 離開畫面時還是會再呼叫一次,但 _netPlayFinishedSent 已經 latch 住了。
            if (_activeGame.Finished) SendNetPlayFinished();

            var snap = _activeGame.NetScore;
            if (Time.unscaledTime >= _netFrameNextAt)
            {
                _netFrameNextAt = Time.unscaledTime + NetFrameIntervalSec;
                net.SendFrame(_netMatchId, snap.TimeMs, snap.Score, snap.Combo, snap.MaxCombo, snap.Hp,
                              snap.Perfect, snap.Cool, snap.Bad, snap.Miss);
            }
        }

        // 上一次套進畫面的旁觀名單(用來判斷有沒有變 —— 每幀重寫十個 Label3D 是白工)。
        private string _spectatorNamesKey;

        /// <summary>
        /// 把房間快照裡的旁觀者名單推進遊戲畫面(需求 10:右側要真名)。
        ///
        /// 為什麼不訂閱 <c>RoomUpdated</c> 事件而是每幀比對:遊戲中房間畫面已經被拆掉,
        /// 訂閱者的生命週期要自己管(進場訂閱、離場取消,少一邊就是洩漏或 NRE)。
        /// 每幀比一個字串便宜得多,而且 <see cref="TickNetGameplay"/> 本來就每幀跑。
        /// </summary>
        private void SyncSpectatorNames(NetClient net)
        {
            var snap = net.Room;
            var specs = snap != null ? snap.Spectators : null;
            int n = specs != null ? specs.Length : 0;

            var sb = new System.Text.StringBuilder(64);
            for (int i = 0; i < n; i++) { sb.Append(specs[i].Name); sb.Append('\n'); }
            string key = sb.ToString();
            if (key == _spectatorNamesKey) return;
            _spectatorNamesKey = key;

            var names = new string[n];
            for (int i = 0; i < n; i++) names[i] = specs[i].Name ?? "";
            _activeGame.SetSpectatorNames(names);
        }

        /// <summary>這一局結束(正常打完 / 中途離開)→ 告訴 server,房間才會離開 playing。只會送一次。</summary>
        private void SendNetPlayFinished()
        {
            var net = _ctx.Net;
            if (net == null || net.Match == null || _netPlayFinishedSent) return;
            if (!net.IsMatchParticipant) return;
            _netPlayFinishedSent = true;
            var snap = _activeGame != null ? _activeGame.NetScore : default(ScreenGameplay.NetScoreSnapshot);
            net.SendPlayFinished(_netMatchId, snap.Score, snap.Combo, snap.MaxCombo,
                                 snap.Perfect, snap.Cool, snap.Bad, snap.Miss);
            // 🔴 這裡**不退訂** —— 曲末就會呼叫這支(見 TickNetGameplay),而結算畫面還開著:
            // 退了的話 server 之後推的 resultsReady 就收不到,結算的名次會停在最後一筆 frame。
            // 退訂放在真的離開打歌畫面的那條路徑(DetachNetGameplay)。
        }

        /// <summary>
        /// 「結算看完了,我人回房間了」。
        ///
        /// 為什麼要單獨一則:<see cref="SendNetPlayFinished"/> 是**曲末**就送的(不等玩家關掉結算面板),
        /// 所以 server 判定結算的那一刻,人還在看成績。留在房間的人這段時間應該繼續看到那幾格的
        /// PLAYING 徽章 —— 它該跟著「人回來了沒」,不是「歌放完了沒」。
        ///
        /// 沒有這一則也不會壞:server 有 <see cref="Sdo.Net.NetLimits.ResultsGraceMs"/> 的逾時兜底
        /// (那是給斷線 / 直接關掉遊戲的人用的),只是徽章會多掛幾十秒才消失。
        ///
        /// 送不出去(這一場已經被 server 收掉了 → error{badState})只會進 log,不影響回房。
        /// </summary>
        private void SendNetBackToRoom()
        {
            var net = _ctx != null ? _ctx.Net : null;
            if (net == null || net.Match == null || !net.IsMatchParticipant) return;
            net.SetPlayState(Sdo.Net.PlayState.Idle, _netMatchId);
        }

        /// <summary>離開打歌畫面:把這一局的訂閱收掉。</summary>
        private void DetachNetGameplay()
        {
            var net = _ctx.Net;
            if (net == null) return;
            net.FramesReceived -= OnNetFrames;
            net.ResultsReady -= OnNetResults;
            net.ComboMilestoneReceived -= OnNetComboMilestone;
        }

        // 遊戲中按換鏡頭鍵（預設 F2）→ 存進 OPTION 遊戲頁的「遊戲視角」：切到固定鏡頭就記住是第幾台且標籤變「固定」，
        // 循環回自動導播就變回「默認」（台號保留）。落地在 config.ini 的 [Option]（DisplaySettingsManager.Save 會寫）。
        // 值沒變就不寫檔。
        private static void PersistCamMode(int camMode)
        {
            var s = DisplaySettingsManager.Settings;
            if (s == null) return;
            s.gameplay ??= new GameplaySettings();
            if (!s.gameplay.SetFromCamMode(camMode, ScreenGameplay.FixedCamCount)) return;
            DisplaySettingsManager.Save();
        }

        // Result panel confirmed: ScreenGameplay already showed its own STATIS settlement (score / EXP / G幣 / replay),
        // so the front-end just tears the gameplay session down and returns to the room. (The legacy ResultsModal is
        // intentionally unused now that the play screen settles itself; kept built only so older call sites compile.)
        private void ReturnFromGameplay() { SendNetPlayFinished(); SendNetBackToRoom(); DetachNetGameplay(); TransitionToRoomFromGame(); }

        // Esc during play: abandon the run with no settlement and go straight back to the room.
        // 🔴 中途離開也要送 playFinished(帶當下的部分分數)—— 不送的話房間會卡在 playing,
        //    要等 server 的逾時才恢復,那段時間誰都不能再開一局。
        private void AbortGameplay() { SendNetPlayFinished(); SendNetBackToRoom(); DetachNetGameplay(); TransitionToRoomFromGame(); }

        /// <summary>Ctrl 按著嗎(左右都算)。優先問實體鍵位(不受輸入法影響),不支援時退回 Unity Input。</summary>
        private static bool CtrlHeld()
        {
            if (RawKeyboard.Supported)
                return RawKeyboard.IsHeld(KeyCode.LeftControl) || RawKeyboard.IsHeld(KeyCode.RightControl);
            return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        }

        /// <summary>
        /// 旁觀中按 Ctrl+Q:直接離開房間回選角色畫面(需求 10)。
        ///
        /// 順序照 <c>RoomScreen.OnLeave</c> 的既有慣例:<b>離房要在轉場全黑時才做</b>。
        /// 那邊的註解記錄了不這麼做的後果 —— 離房會觸發房間狀態回呼去重畫還沒被黑幕蓋住的畫面,
        /// 而且 <c>CurrentRoom</c> 沒清乾淨的話換身分再進房會變成 <c>IsHost=false</c>。
        /// 這裡多一步 StopSpectate:先把旁觀席退掉,server 才不會留一個幽靈觀眾。
        /// </summary>
        private void QuitSpectating()
        {
            if (_returningFromGame) return;
            _returningFromGame = true;
            var net = _ctx != null ? _ctx.Net : null;
            ScreenTransition.Run(() =>
            {
                TeardownGameplay();
                if (net != null && net.IsSpectating) net.StopSpectate();
                _ctx.Rooms?.LeaveRoom();
                _ctx.Flow.GoTo(ScreenId.GenderSel);
            });
        }

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
