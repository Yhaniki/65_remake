using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using Sdo.Osu;
using Sdo.Ruleset;

namespace Sdo.Game
{
    /// <summary>
    /// SDO-faithful playable screen in the original 800×600 frame (DdrGamePlay.xml), art loaded at
    /// runtime from the extracted game tree (SdoExtracted). Geometry = EXACT values decoded from the
    /// decompilation (doc/GAMEPLAY_SCREEN_ANATOMY.md). Iteratively verified via the headless capture
    /// (Tests/PlayMode/CaptureTest -> H:/65_remake/play-capture.png). Self-boots.
    /// </summary>
    public sealed partial class ScreenGameplay : MonoBehaviour
    {
        // ---- tunables ----
        // HP system level 0/1/2 (DAT_00674f04+0x75; NOT the chart difficulty). Deltas per SDO_HP_FORMULA.md:
        // L0 miss -50 (dies in 23), L1 -40 (29), L2 -30 (39). Official observed = 39 misses → level 2
        // (Perfect +2 / Cool +1 / Bad -5 / Miss -30): lighter miss AND proportionally lighter Bad drain.
        public int healthLevel = 2;
        public bool autoPlay = true;
        // DEBUG: force a grade on every manual hit (-1 = real timing window). F4 panel selects it.
        public int forcedJudge = -1;
        private static readonly string[] ForceJudgeLabels = { "Real", "Perfect", "Cool", "Bad", "Miss" };
        // Note scroll = osu!mania-style (Sequential + relative beat-length scaling) at a FIXED base tempo:
        // the base speed is the SAME for every song (NOT scaled by the song's BPM), calibrated with the
        // official px/s = BPM×speed×1.6 at referenceBpm=140. Mid-song BPM changes / osu SV still vary the
        // scroll locally (see ManiaScroll). scrollSpeedMul = the room "速度" step (RoomConfig.speedSteps),
        // set by FrontendApp from the session. constantScroll = osu "Constant Speed" mod (kill all variation).
        public float scrollSpeedMul = 2.5f;   // 速度 step (1.0..8.0); FrontendApp wires GameSession.Speed in
        // Room win2 "note" selection (GameSession.NoteType) → the gameplay skin applied at boot via SelectSkin.
        // -2 = unset (standalone/F4 boot: keep stock); -1 = 隨機 (random skin); 0..10 = the specific note skin
        // (0..9 = the 2D skins in NoteTypeEftSuffix order, 10 = the 3D hiteft3D skin) — same order as the room's NoteEftArt.
        public int roomNoteType = -2;
        public float referenceBpm = 140f;     // base-tempo anchor for the constant base speed
        public bool constantScroll = false;   // true = ignore BPM/SV variation (perfectly linear scroll)
        public float judgeLineY = 70f;        // receptor / hit line Y (design px). UPSCROLL: notes rise to it.
        private ManiaScroll _scroll;          // built from _map after LoadChart (BuildScroll)
        // Chart/audio paths. Normally set by FrontendApp from the song selection; left EMPTY by default so no
        // absolute path is baked in. When this component is run standalone (dev), Start() fills a default from
        // SdoExtracted.MusicDir (see ResolveDevDefaults).
        public string gnPath = "";   // official chart (e.g. <MusicDir>/sdom1435K.gn)
        public string oggPath = "";  // matching song audio (e.g. <MusicDir>/sdom1435.ogg)
        public int difficulty = 2;            // 0=easy 1=normal 2=hard
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
        public string danceMot = "MOTION/WDANCE0002.MOT";      // fallback dance motion if no DPS
        public string restMot = "MOTION/WREST0072.MOT";        // in-game standby idle (decompiled: rest-table category 0x15, played before/after the DPS — 023_gameplay:4135). male = MREST0082.MOT. (WREST0056 was cat 0, the lobby idle — wrong here.)
        public string dpsPath = "DANCE/11435.DPS";             // per-song choreography for sdom1435 (sequences motion slices)
        private readonly Dictionary<string, MotLoader> _motCache = new Dictionary<string, MotLoader>();

        // EXACT note-board geometry (4-key, left board X=0): lane LEFT-EDGE X 0/69/138/207 (pitch 69 exact).
        // These match NOTES_BOARD1.PNG's own lane-divider columns (texture x = 14,83,152,221,290 → 69px pitch),
        // so when the board is drawn 1:1 (native, no scaling) at boardX=0 the notes sit exactly on its lanes.
        // The track has a left margin (TrackMarginX); all lane X + TrackCenterX include it.
        private const float TrackMarginX = 14f;
        private static readonly float[] LaneLeftX = { 0f + TrackMarginX, 69f + TrackMarginX, 138f + TrackMarginX, 207f + TrackMarginX };
        private const float LaneCx0 = 34.5f;  // lane center offset (pitch/2)
        private const float LaneW = 82f;      // note draw size
        private const float ReceptorW = 92f;  // receptors are a touch larger than notes
        private const int Keys = 4;
        // notes must stay within the note board and never cover the HP bar (y 18..29). A SpriteMask clips note
        // sprites to this Y band; the board top (0) is above the HP bar so notes are clipped just below it (30),
        // and the bottom is the board/frame bottom (600) so nothing shows beneath the board (e.g. in editor view).
        private const float NotesClipTop = 30f;
        private const float NotesClipBottom = 600f;
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
        private static readonly Vector2 JudgeWordCenter = new Vector2(TrackCenterX, 216);
        private const float ComboWordY = 275f;
        private const float ComboDigitY = 326f, ComboDigitStep = 42f, ComboDigitW = 48f;
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
        // Per-scene ambient SE (decompiled SeMgr_PlayVoiceTimed, gated on scene id in Gameplay_Update): only a few
        // scenes carry an intermittent ambience (sea waves / stadium crowd / underwater bubbles / garden); see
        // AmbientSeName + TickAmbient. Most scenes are BGM/song-only.
        private AudioClip _ambientClip;          // loaded ambient clip (null = this scene has no ambience)
        private float _nextAmbientAt = -1f;      // realtime when the next ambient one-shot may fire (<0 = not armed yet)
        private bool _started, _failed, _ended;
        private double _songStartDspTime, _clockStart = -1;
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

        // ---- result / finish sequence (歌曲結束 → 輸贏定格動作 → 結算面板; decompiled FinishSequenceTick phase4..6) ----
        private enum ResultPhase { None, FinishPose, Settle, Replay }
        private ResultPhase _resultPhase = ResultPhase.None;
        private float _resultPhaseStart;          // Time.time the current result phase began
        private bool _localWon;                   // local player is the round winner (rank 1) — drives win/lose pose + FINISHED
        private bool _gameOver;                   // HP ran out (failed) — result shows GAME OVER instead of YouWin/Lose
        public string winMot = "WWIN0002.MOT";    // winner 定格 pose (cat5); male = MWIN0001.MOT
        public string loseMot = "WLOST0003.MOT";  // loser 定格 pose (cat4); male = MREST0004.MOT
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
        private ResultScreen _result;             // 結算面板 (STATIS panel) — built lazily, shown at the settle beat
        private string _songTitle = "song";       // resolved song title (captured when the HUD song label is built)

        // 結算頭像: render the LOCAL avatar's head into a RenderTexture for its result row (45° 3/4 view, idle moves).
        public bool resultHeadPortrait = true;
        public int headPortraitLayer = 11;        // dedicated layer for the ISOLATED idle head avatar (head cam renders only this)
        // The cam FOLLOWS the avatar's head bone (so the head is ALWAYS framed); the avatar is yawed/scaled for the 3/4
        // angle. Tune yaw (angle) + dist/fov (zoom) + a small aim offset (centre the face). All F4-tunable (Result tab).
        // Camera matched to the official AvatarShow render (RE'd from sdo.bin.c). The shared 3D cam is PerspectiveFovLH
        // fovY=π/4=45°, LookAtLH eye(-3,46,-181)→at(-2,38,21) up(0,1,0) → +Z view tilted DOWN ~2.27° (Δy −8/202).
        // Per the OFFICIAL screenshots the result/ranking heads are a 3/4-ANGLED HEAD CLOSE-UP (head ~fills the frame, hair/
        // accessories spill above the top, only a sliver of shoulder shows) — i.e. the head-closeup mode (mode 7: model yaw
        // −30°, scale 2.6), NOT a frontal full-body framing. The official zooms via a per-costume scale TABLE (no single
        // value), so we MEASURE this avatar's hair-top and compute a TIGHT distance: head fills the frame with the hair
        // captured inside the RT (margin above → never cut). headAutoFrame does that; headZoom fine-tunes. Yaw gives the 3/4.
        public bool headAutoFrame = true;          // auto distance+aim from the measured head bounds (no magic numbers)
        public float headZoom = 1f;                // auto-frame fine multiplier: >1 = zoom OUT (smaller head, more top margin)
        public float headPortraitDist = 28f;       // manual cam distance (used only when headAutoFrame is OFF)
        public float headPortraitFov = 45f;        // 官方 fovY = π/4 = 45°（已對齊）
        public float headPitchDeg = 2.3f;          // 官方相機俯角 atan(8/202)≈2.27°（略俯視頭部）
        public Vector3 headAimOffset = new Vector3(-2.1f, 9f, 0f);     // manual look-target offset (used only when auto OFF; X
                                                   // is always applied to centre the face horizontally)
        public float headAvatarScale = 1.05f;     // idle avatar uniform scale — tuned
        public float headAvatarYaw = 30f;         // 模型 Y 旋轉 = 3/4 斜角（官方頭部近拍 mode7 = −30°；轉模型不轉相機）。可調/翻號
        private Camera _headCam; private RenderTexture _headRt; private SdoAvatar _headAvatar;
        private Vector3 _headModelPos = new Vector3(0f, 50f, 0f);   // head bone REST pos (model space) — cam targets this so it stays FIXED (no per-frame bob chase)
        private static readonly Vector3 HeadAvatarSpot = new Vector3(5000f, 0f, 5000f);   // isolated parking spot (off the stage)

        private readonly List<RuntimeNote> _notes = new List<RuntimeNote>();
        private readonly RuntimeNote[] _holding = new RuntimeNote[Keys];
        private readonly Sprite[][] _noteFrames = new Sprite[Keys][];
        private readonly Texture2D[] _holdTex = new Texture2D[Keys];
        private readonly Sprite[] _holdTail = new Sprite[Keys];
        private readonly bool[] _holdTailFlipX = new bool[Keys];   // combined-name skins share one cap across a lane pair → mirror it
        private readonly bool[] _holdTailFlipY = new bool[Keys];   // (per-lane-name skins like NOTEIMAGE_6 are pre-drawn → no flip)
        private SpriteRenderer _missOverlay;                       // track-wide red wash flashed on a miss (covers all 4 lanes reliably)
        private readonly Sprite[] _recIdle = new Sprite[Keys];
        // Keydown receptor feedback = a ONE-SHOT 5-frame burst (KEYDOWN_JUDGELINE.AN = *_judgeline 2→3→4→5→6),
        // fired on the key-PRESS transition then resolving back to the idle frame 1 (JUDGELINE.AN = *_judgeline1).
        // It is press-driven only — NOT tied to whether the key stays held (decompiled CtlNotesShow_TriggerLanePress
        // = "play judgeline press effect for a lane once").
        private readonly Sprite[][] _recDownFrames = new Sprite[Keys][];
        private readonly float[] _recDownStart = new float[Keys];   // when the press burst began; <0 = idle (frame 1)
        public float recKeydownStepSec = 0.03f;                     // per-frame hold time for the 5-frame keydown burst
        private readonly SpriteRenderer[] _receptors = new SpriteRenderer[Keys];
        public float noteAnimFps = 12f;

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
        private readonly BurstFx[] _holdBurst = new BurstFx[Keys];   // the looping hold burst per lane (gated: 1 round at a time)
        private readonly Stack<Material> _matPool = new Stack<Material>();  // reuse burst material instances (no per-hit GC)
        private SpriteRenderer _board;          // framed note-board (NOTES_BOARD1, chamfered), drawn 1:1 native
        private Texture2D _boardSrc;            // cached ORIGINAL board texture (kept so alpha can be re-scaled live)
        private Texture2D _boardGenTex;         // last generated (alpha-scaled) texture, destroyed before regen
        private float _boardAlphaApplied = -1f; // tracks the boardAlpha last baked into _board's sprite
        // DEBUG tuning sliders (toggle with F4). Drag in-game to tune; values apply live.
        public float boardAlpha = 1.4f;     // board alpha MULTIPLIER on the original texture: 1=native (~62%, the
                                            // original look), ~1.4=official (deep but inner detail still shows),
                                            // ~2.6=fully opaque. Multiplies the real alpha curve so detail survives.
        public float boardX = 0f;           // board horizontal nudge (design px); 0 keeps texture lanes aligned 1:1 to the track
        public float burstSize = 1.3f;      // hit-burst size multiplier
        public float burstBright = 1.5f;    // hit-burst brightness (additive _TintColor; 1.0 = stock)
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
        public float receptor3dScale = 0.82f;                        // 3D receptor (JUDGELINE) size × ReceptorW — bumped to visually match the note mesh (sprite has more tile margin); × note3dMaster; F4-tunable
        public float note3dHoldWidth = 0.73f;                        // 3D hold body/cap width × LaneW (matches the 0.73 note mesh)
        public float note3dHoldHeadGap = 0f;                         // 3D hold body TOP offset from the note head (0 = connect; +px tucks the long lower)
        public float note3dCapOffset = 0f;                           // 3D tail-cap fine offset (design px) on top of the auto weld at the tail edge
        // ── OFFICIAL LONG.MSH constants (reverse-engineered — FUN_0041a7e0 body rebuild + the mesh's baked vertices).
        // The official hold draws the FULLY-OPAQUE LONG textures (ColorKey=0, zero transparent texels — the dark interior
        // is meant to show; silhouette = geometry, NOT alpha): a body quad sampling ONLY U 0.2243..0.7683 of LONG_0_1
        // (the fat outer silver rails sit OUTSIDE this band and are never drawn), V = 1 − z·0.03205128 anchored at the
        // TAIL end (z in mesh units; texture repeats every 31.2 units on a 22.0074-wide strip → wrap addressing), plus a
        // WELDED cap TRIANGLE (base ±11.0037 at z≈0, tip at z=−10.815) sampling LONG_0_0 v 0.5574→0.8939. The V anchor
        // makes the chevrons stay glued to the cap; the junction rows of the two textures are identical → seamless.
        private const float LongU0 = 0.2243f, LongU1 = 0.7683f;      // body U band (official mesh verts)
        private const float LongVPerUnit = 0.03205128f;              // V per mesh z-unit (1/31.2)
        private const float LongMeshW = 22.0074f;                    // strip width in mesh units (≈ note width 21.97)
        private const float LongZBase = 0.0287f;                     // cap/body weld z
        private const float LongCapLenRatio = 10.844f / 22.0074f;    // cap length ÷ strip width (tip z −10.8154 … base 0.0287)
        private const float LongCapU0 = 0.2255f, LongCapU1 = 0.7666f, LongCapUTip = 0.4964f;
        private const float LongCapV0 = 0.5574f, LongCapVTip = 0.8939f;
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
        private Vector2 _dbgScroll;          // scroll for the tuning sliders so they never push the playtest controls off-panel
        private int _dbgTab;                 // F4 panel tab: 0=Play, 1=Combo, 2=Stage — keeps each group's sliders roomy
        private static readonly string[] DbgTabs = { "Play", "Combo", "Stage", "Emoji", "Result", "Banner" };
        private static readonly (string label, EmojiKind kind)[] EmojiTestButtons =
        {
            ("50→HH", EmojiKind.HH), ("150→SHSH", EmojiKind.SHSH), ("350→JRKL", EmojiKind.JRKL), ("550→KJ", EmojiKind.KJ),
            ("800→HE", EmojiKind.HE),
            ("miss10→H", EmojiKind.H), ("miss30→Y", EmojiKind.Y), ("miss50→JS", EmojiKind.JS), ("lowHP→GTH", EmojiKind.GTH),
        };
        private TextMesh _musicName, _lvText, _timeText, _info, _fpsText;
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
        public bool freeMode = false;                // 自由模式: no ranking UI during play, no G幣/EXP reward; HP-out still shows GAME OVER
        public string localPlayerName = "玩家";       // local player's display name (hardcoded default, tunable)
        public int playerLevel = 1;                  // character level — scales the round-end coin/honor reward (Sdo.Ruleset.Reward)
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
        // spectators (旁觀玩家): GAMEPLAY18 title sprite + fake light-blue names below the roster. DdrGamePlay.xml
        // had lookerTitle@(696,190) + looker rows@(696,212..) step13 colour 0xff9DCBFF — we use fake names.
        public bool showSpectators = false;          // 預設關閉測試旁觀名單(全是假名);真連線有觀眾時再開
        private static readonly string[] SpectatorNames = { "酷", "美麗", "悲晴吉克", "路過旅人", "小幫手" };
        private SpriteRenderer _lookerTitle;
        private Label3D[] _lookerRows;
        public float lookerTitleX = 694f, lookerTitleY = 214f, lookerX = 698f, lookerFirstY = 241f, lookerRowStep = 16f, lookerFontWorld = 18f;   // names start 5px lower than before so the list clears the 旁觀玩家 header
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
            ResolveDevDefaults();
            ConfigureAvatarGender();
            _cam = Camera.main ?? new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            SdoLayout.SetupCamera(_cam);
            BuildBootCover();               // put the loading screen up FIRST...
            _bootShownRt = Time.realtimeSinceStartup;
            StartCoroutine(BootBuildCo());  // ...then build the (heavy) stage behind it — see BootBuildCo
        }

        private void ConfigureAvatarGender()
        {
            if (AvatarPartsNeedFallback(avatarParts, localPlayerMale))
                avatarParts = SdoRoomAvatar.DefaultParts(localPlayerMale);

            if (!localPlayerMale) return;

            skeletonHrc = SdoRoomAvatar.MaleHrc;
            maleBody = true;
            danceMot = "MOTION/MDANCE0002.MOT";
            restMot = "MOTION/MREST0082.MOT";
            winMot = "MWIN0001.MOT";
            loseMot = "MREST0004.MOT";
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
            if (!observeBurstMode) SpawnNotes();   // observe mode: no notes (clean stage to watch the burst)
            foreach (var n in _notes) { double t = n.Note.EndTimeMs ?? n.Note.StartTimeMs; if (t > _totalMs) _totalMs = t; }
            BuildHud();
            ApplyRoomNoteSkin();   // AFTER BuildHud so _comboWord exists → LoadComboJudgeArt can assign the skin's COMBO.PNG
                                   // (room win2 note selection → matching gameplay skin: board + hit burst + combo/judge, incl. 3D)
            TryLoadAvatar();
            TryLoadScene();
            _engine = new ManiaJudgmentEngine(JudgmentWindows.FromSdoBpm(_map.Bpm));
            _score = new ScoreProcessor(_map.TotalNotes);
            _health = new HealthProcessor(healthLevel);
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
            var ambName = AmbientSeName(SceneMapId());   // load the per-scene ambience (sea/stadium/underwater/garden) if any
            if (!string.IsNullOrEmpty(ambName)) StartCoroutine(LoadAmbientCo(ambName));
            // Enter on the crane with no note board: hold the track hidden while the opening shot flies in, then
            // OpeningSequence() reveals it with READY. Only when there's actually a 3D crane to watch.
            if (use3dCamera && _camReady && openingIntroSec > 0f) { _introStartRt = Time.realtimeSinceStartup; SetTrackVisible(false); }
            if (observeBurstMode) { _dancing = false; _camMode = 0; SetTrackVisible(false); _introStartRt = -1f;   // idle dancer, fixed cam, hidden track
                HideComboAndJudge(); HideHudForPanel(); }   // also clear the rest of the gameplay HUD (score/combo/judge/song labels/ranking) for a clean stage
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
            if (clip != null && _sfx != null) _sfx.PlayOneShot(clip);
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
            _ambient.PlayOneShot(_ambientClip);
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
                if (n.Cap3d != null) n.Cap3d.SetActive(false);   // 3D cap triangle off with the 2D skin
            }
        }

        // Load (or RELOAD) the per-lane note-board art from NoteDir: falling-note frames, receptor idle + keydown frames,
        // hold body/tail. Receptor naming differs per skin — NOTEIMAGE_5/6 use numbered *_judgeline1..6, while
        // NOTEIMAGE_8/9/10/11/pet use *_judgeline + *_judgeline_f2 — so try the numbered scheme first, then _f2.
        private void LoadBoardArt()
        {
            for (int c = 0; c < Keys; c++)
            {
                string d = Dir5[c];
                var fr = new Sprite[4]; bool ok = true;
                for (int f = 0; f < 4; f++) { fr[f] = SdoExtracted.LoadImage(NoteDir, d + "holdheadactive" + f + ".png"); if (fr[f] == null) ok = false; }
                if (ok) _noteFrames[c] = fr;
                _recIdle[c] = SdoExtracted.LoadImage(NoteDir, d + "_judgeline1.png") ?? SdoExtracted.LoadImage(NoteDir, d + "_judgeline.png");
                var rdf = new List<Sprite>();                         // keydown burst: numbered *_judgeline2..6, else *_judgeline_f2
                for (int f = 2; f <= 6; f++) { var s = SdoExtracted.LoadImage(NoteDir, d + "_judgeline" + f + ".png"); if (s != null) rdf.Add(s); }
                if (rdf.Count == 0) { var f2 = SdoExtracted.LoadImage(NoteDir, d + "_judgeline_f2.png"); if (f2 != null) rdf.Add(f2); }
                if (rdf.Count == 0 && _recIdle[c] != null) rdf.Add(_recIdle[c]);
                _recDownFrames[c] = rdf.ToArray();
                string baseLong = (d == "left" || d == "right") ? "rightleft_long" : "updown_long";
                var bodySpr = SdoExtracted.LoadImage(NoteDir, baseLong + ".png");
                if (bodySpr != null) { _holdTex[c] = bodySpr.texture; _holdTex[c].wrapMode = TextureWrapMode.Repeat; SdoExtracted.AlphaBleed(_holdTex[c]); }
                // end cap: prefer a PER-LANE cap ({left|right|down|up}_long_bottom — NOTEIMAGE_6, drawn per direction), else
                // the combined cap (rightleft/updown_long_bottom — NOTEIMAGE_5/8). Caps render correct un-flipped on every
                // skin EXCEPT NOTEIMAGE_8, whose updown cap is stored upside-down → its up & down lanes need a vertical flip.
                var capSpr = SdoExtracted.LoadImage(NoteDir, d + "_long_bottom.png")
                             ?? SdoExtracted.LoadImage(NoteDir, baseLong + "_bottom.png");
                bool flipY = (d == "up" || d == "down") && NoteDir.EndsWith("NOTEIMAGE_8");
                if (capSpr != null) { _holdTail[c] = SdoExtracted.CleanCapCopy(capSpr); _holdTailFlipX[c] = false; _holdTailFlipY[c] = flipY; }
            }
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
                if (jl0 != null) _recIdle[c] = jl0;
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
                    PlaceAspect(_receptors[c], LaneLeftX[c] + LaneCx0, judgeLineY, ReceptorW * eff);
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
            _readyFrames = new List<Sprite>().ToArray();
            var rf = new List<Sprite>(); for (int i = 0; i < 10; i++) { var s = SdoExtracted.Eft("READY0" + i + ".PNG"); if (s != null) rf.Add(s); } _readyFrames = rf.ToArray();
            var gf = new List<Sprite>(); for (int i = 1; i <= 6; i++) { var s = SdoExtracted.Eft("GO0" + i + ".PNG"); if (s != null) gf.Add(s); } _goFrames = gf.ToArray(); // GO01..GO06 only
            LoadEmojiArt();   // head-emoji cut-in PNG sequences (UI/PLAYINGEXP)
            // EFT_HIT bursts are opaque-on-black -> additive blending so black reads as transparent glow.
            // The Particles/Additive shader's _MainTex is NOT [PerRendererData], so SpriteRenderers SHARING one
            // material all sample the last-written sprite -> bursts cross-bleed & jitter. Each burst clones its
            // OWN instance of this template (see SpawnBurst) so every burst animates independently.
            var sh = Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Sprites/Default");
            _addMat = new Material(sh);
            // HP glow gets its OWN clip-capable additive instance: same look as Particles/Additive, plus a world-X
            // scissor (Sdo/HpGlowClip) so a low-HP flash can't spill past the bar frame. Falls back to plain additive.
            _hpGlowMat = new Material(Shader.Find("Sdo/HpGlowClip") ?? sh);
        }

        private bool LoadChart()
        {
            // (3) official .gn chart first
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
            string path = (!string.IsNullOrEmpty(oggPath) && File.Exists(oggPath))
                ? oggPath : Path.Combine(Application.streamingAssetsPath, "Step1", "Bassdrop.mp3");
            var type = path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ? AudioType.OGGVORBIS : AudioType.MPEG;
            using (var req = UnityWebRequestMultimedia.GetAudioClip("file://" + path, type))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success) _audio.clip = DownloadHandlerAudioClip.GetContent(req);
                else Debug.LogWarning("[Step1] audio unavailable (ok for headless): " + req.error);
            }
            _audioReady = true;   // song decoded (or failed) → the loading screen may now reveal the stage
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
            _songStartDspTime = AudioSettings.dspTime + StartLeadSec;
            if (_audio != null && _audio.clip != null) _audio.PlayScheduled(_songStartDspTime);
            _clockStart = Time.timeAsDouble + StartLeadSec;
            if (showtimeMode) _gaugeGlowFromStart = true;   // song is playing → head glow stays lit even at 0 fill (no key needed)
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

        private void BuildBoard()
        {
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
                SdoLayout.PlaceTopLeft(_board, boardX, 0f, 10f);
            }
            for (int c = 0; c < Keys; c++)
            {
                _recDownStart[c] = -1f;   // idle (frame 1) until a press fires the burst
                var sr = NewSR("Receptor" + c, _recIdle[c], 0);
                PlaceAspect(sr, LaneLeftX[c] + LaneCx0, judgeLineY, ReceptorW);
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
                SdoLayout.PlaceTopLeft(fsr, LaneLeftX[c] + 1f, ClickStripTopY, 9f);
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
            _missOverlay.transform.position = SdoLayout.ToWorld(LaneLeftX[0] + trackW / 2f, ClickStripTopY + 279f, 9f);
            _missOverlay.color = new Color(1f, 0f, 0f, 0f); _missOverlay.enabled = false;
            BuildNoteClip();
        }

        // a SpriteMask spanning the board's play band [NotesClipTop, NotesClipBottom]; note head/tail (SpawnNotes),
        // the lane click-flash strips and the miss red wash (BuildBoard) are flagged VisibleInsideMask so they're
        // clipped to it — never drawn over the HP bar or below the board.
        private void BuildNoteClip()
        {
            var go = new GameObject("NoteClip");
            var mask = go.AddComponent<SpriteMask>();
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var px = new Color32[16]; for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(px); tex.Apply();
            mask.sprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
            float h = NotesClipBottom - NotesClipTop, cy = (NotesClipTop + NotesClipBottom) / 2f;
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
            _trackVisible = on;
            if (_board) _board.enabled = on;
            // ShowTime mode has no HP bar (only the 集氣 energy gauge) — keep the whole HP widget hidden even when the
            // track is shown. UpdateHpBar also early-outs in ShowTime so it can't re-enable _hpGlow.
            bool hpOn = on && !showtimeMode;
            if (_hpSolidBack) _hpSolidBack.enabled = hpOn;
            if (_hpBg) _hpBg.enabled = hpOn;
            if (_hpTex) _hpTex.enabled = hpOn;
            if (_hpBackFrame) _hpBackFrame.enabled = hpOn;
            if (_hpGlow) _hpGlow.enabled = hpOn;         // UpdateHpBar refines this (low HP -> off) once visible again
            for (int c = 0; c < Keys; c++)
            {
                if (_receptors[c]) _receptors[c].enabled = on;
                if (!on && _clickFlashSr[c] != null) _clickFlashSr[c].enabled = false;
            }
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

        private void SpawnNotes()
        {
            foreach (var h in _map.HitObjects)
            {
                int c = Mathf.Clamp(h.Lane, 0, Keys - 1);
                var head = NewSR("Note", _noteFrames[c] != null ? _noteFrames[c][0] : _recIdle[c], 5);
                head.enabled = false;   // hidden until it scrolls into view (else it sits at screen centre)
                head.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;   // clipped to the note board (NoteClip mask)
                head.sharedMaterial = new Material(Shader.Find("Sprites/Default"));   // own material: masked sprites must not batch (texture cross-bleed)
                GameObject body = null; SpriteRenderer tail = null;
                if (h.IsHold)
                {
                    if (_holdTex[c] != null) body = CreateHoldBody(c);
                    if (_holdTail[c] != null)
                    {
                        tail = NewSR("HoldTail", _holdTail[c], 4);
                        tail.flipX = _holdTailFlipX[c]; tail.flipY = _holdTailFlipY[c];   // mirror the shared combined-skin cap per lane
                        tail.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;
                        // own material -> no mask batch cross-bleed. (In 3D mode the sprite tail stays hidden — the cap is real triangle geometry.)
                        tail.sharedMaterial = new Material(Shader.Find("Sprites/Default"));
                    }
                }
                if (body) body.SetActive(false);   // hide body+tail too until scrolled in (else they pile at screen centre)
                if (tail) tail.enabled = false;
                // precompute the beat-quantization colour for the 3D skin (cheap; used only when _note3dMode is on)
                _notes.Add(new RuntimeNote(h, head, body, tail, NoteBeatColor.Family(h.StartTimeMs, _map)));
            }
        }

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
            SdoLayout.PlaceBox(_hpSolidBack, TrackCenterX - 123f, 15, 246, 18);   // the MyHpBack frame's full rect
            _hpBg = NewSR("HpBg(bloodBG2)", SdoExtracted.Hud("bloodBG2.an"), 15); SdoLayout.PlaceBox(_hpBg, HpPos.x, HpPos.y, HpSize.x, HpSize.y);
            _hpTex = NewSR("HpFill", SdoExtracted.Hud("MyHp.an"), 16); // official MyHp.png (top-bottom gradient)
            // MyHpBack is a dark-gray frame (centre ~24, edges ~65); key out the dark centre (<45) so the
            // red fill shows through, keeping only the lighter rounded frame edges on top.
            _hpBackFrame = NewSR("MyHpBack", SdoExtracted.LoadImageBlackKeyed(SdoExtracted.GameplayUiDir, "MyHpBack.png", 45), 18); SdoLayout.PlaceBox(_hpBackFrame, TrackCenterX - 123f, 15, 246, 18);
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
            // Title from the import-time UTF-8 catalog (keyed by .gn filename); GB2312 is never
            // decoded at runtime. Fall back to _map.Title (set only on the .osu path) then "song".
            var songTitle = SongCatalog.Title(gnPath);
            if (string.IsNullOrEmpty(songTitle)) songTitle = _map.Title;
            if (string.IsNullOrEmpty(songTitle)) songTitle = "song";
            // song name / LV / time value text — white, two sizes smaller (13 -> 11) per request.
            _musicName = NewText("MusicName", 80, 585, 11, Color.white); _musicName.text = songTitle;
            _songTitle = songTitle;   // keep for the result panel
            _lvText = NewText("MusicLev", 240, 585, 11, Color.white); _lvText.text = _map.Level.ToString();
            int tot0 = (int)Math.Round(_totalMs / 1000.0);   // initial: "--:--  [total]"
            _timeText = NewText("MusicTime", 336, 585, 11, Color.white); _timeText.text = FullWidth($"- : -    {tot0 / 60} : {tot0 % 60:00}");
            _info = NewText("Info", 610, 8, 10, Color.white);
            _fpsText = NewText("Fps", 6, 9, 11, new Color(0.5f, 1f, 0.5f, 1f));   // debug FPS (top-left)
            _readyGo = NewSR("ReadyGo", null, 50); _readyGo.enabled = false;
            _gameOverGo = NewSR("GameOverText", null, 55); _gameOverGo.enabled = false;   // above the READY/GO overlay (50)
            // (死亡字幕的幀在死亡當下才依「當前 note skin」載入 → LoadGameOverFrames;每個 skin 各有一組 GAMEOVER 圖)
            BuildRankingUi();
            BuildEnergyHud();
            UpdateHpBar();
        }

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
            return tm;
        }

        // Style the bottom time field like the original. The colon is written as " : " (an ASCII colon with an EQUAL
        // space on each side) so it sits centred between the digits — the full-width colon glyph was left-biased.
        // Only the placeholder dash is widened to an em dash ("— : —"); digits stay half-width (tight).
        private static string FullWidth(string s) => s.Replace('-', '—');   // U+2014 em dash

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

        private static Color PartColor(string name)
        {
            name = name.ToUpperInvariant();
            if (name.Contains("HAIR")) return new Color(0.30f, 0.20f, 0.16f);
            if (name.Contains("FACE") || name.Contains("HAND")) return new Color(0.96f, 0.82f, 0.72f);
            if (name.Contains("COAT")) return new Color(0.42f, 0.62f, 0.92f);
            if (name.Contains("PANT")) return new Color(0.86f, 0.86f, 0.92f);
            if (name.Contains("SHOES")) return new Color(0.22f, 0.20f, 0.26f);
            return new Color(0.80f, 0.75f, 0.70f);
        }

        private void TryLoadAvatar()
        {
            var parent = new GameObject("Avatar3D");
            // skeleton + dance motion (skinned, CPU). Missing/invalid -> falls back to the static bind pose.
            HrcLoader hrc = LoadAsset(skeletonHrc, b => HrcLoader.Load(b));
            MotLoader mot = LoadAsset(danceMot, b => MotLoader.Load(b));   // fallback dance clip if no DPS
            SdoAvatar avatar = null;
            if (hrc != null)
            {
                avatar = parent.AddComponent<SdoAvatar>(); avatar.Setup(hrc, mot);
                _avatar = avatar;                                                             // F4 panel re-shapes this live
                _bodyShapeB = SdoBodyShape.WeightFromIndex(bodyShapeIndex, maleBody);
                avatar.SetBodyShape(_bodyShapeB);                                             // 體型: thin/standard/fat (default thin)
                avatar.RestMot = LoadAsset(restMot, b => MotLoader.Load(b));   // standby idle (rest cat 0x15) — looped before the DPS starts and after it ends
                // per-song choreography (DPS): sequence motion slices to the music clock (debug now dances too)
                var dps = LoadAsset(dpsPath, b => DpsLoader.Load(b));
                if (dps != null)
                {
                    avatar.Dps = dps;
                    avatar.MotResolver = ResolveMot;
                    // raw (un-clamped) dance time: negative during the READY/GO lead-in -> avatar plays the rest idle
                    avatar.DanceTimeSec = () => (float)(Time.timeAsDouble - _clockStart);
                    avatar.DanceEnabled = () => _dancing && !_failed;   // 8-beat dance-gate decision / HP-out (failed) -> dancer holds the standby idle
                    Debug.Log($"[avatar] DPS {dpsPath}: {dps.Rows.Length} rows, {dps.Total:F1}s");
                }
            }

            Bounds bounds = default; bool any = false; int parts = 0;
            foreach (var rel in avatarParts)
            {
                var path = Path.Combine(SdoExtracted.Root, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) { Debug.LogWarning("[avatar] missing " + rel); continue; }
                var r = MshLoader.Load(File.ReadAllBytes(path));
                if (r == null || r.Submeshes.Count == 0) { Debug.LogWarning("[avatar] parse fail " + rel); continue; }
                var avatarDir = Path.GetDirectoryName(path);
                int si = 0;
                foreach (var sub in r.Submeshes)   // each submesh = its own texture + skin (COAT/PANT have 2)
                {
                    var go = new GameObject(Path.GetFileNameWithoutExtension(rel) + "_" + si++);
                    go.transform.SetParent(parent.transform, false);
                    go.AddComponent<MeshFilter>().mesh = sub.Mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    // Each SUBMESH has its OWN material: the cloth submesh -> the garment DDS (e.g.
                    // 900019_woman_pant.dds = red shorts), the skin submesh -> a shared W_Basic_*.dds (bare arms/
                    // legs/face). The PANT/COAT each have a cloth submesh + a skin submesh; resolve per submesh (and
                    // per range for the 2-material skin submeshes) by the MSH material name. (Using the part DDS for
                    // every submesh painted the skin submeshes with cloth -> navy arms + red calves.)
                    // Hair (and any open/thin part) must render TWO-SIDED: single-sided Cull Back hides the
                    // inward-facing strands, so from the front you see through gaps and lose part of the hair.
                    // Body parts stay single-sided (closed solids -> correct occlusion, less overdraw).
                    bool twoSided = rel.ToUpperInvariant().Contains("HAIR");
                    var ds = twoSided ? Shader.Find("Sdo/UnlitDoubleSided") : null;
                    var texShader = ds != null ? ds : Shader.Find("Unlit/Texture");
                    if (sub.Ranges != null && sub.Ranges.Count > 1 && sub.Mesh.subMeshCount == sub.Ranges.Count)
                    {
                        var mats = new Material[sub.Ranges.Count];
                        for (int s = 0; s < sub.Ranges.Count; s++)
                        {
                            int a = sub.Ranges[s].Attrib;
                            string nm = (sub.DdsNames != null && a >= 0 && a < sub.DdsNames.Length && !string.IsNullOrEmpty(sub.DdsNames[a])) ? sub.DdsNames[a] : sub.Dds;
                            Texture2D t = ResolveDds(avatarDir, nm);
                            mats[s] = t != null ? new Material(texShader) { mainTexture = t }
                                                : new Material(Shader.Find("Unlit/Color")) { color = PartColor(rel) };
                        }
                        mr.sharedMaterials = mats;
                    }
                    else
                    {
                        Texture2D tex = ResolveDds(avatarDir, sub.Dds);
                        if (tex != null) mr.sharedMaterial = new Material(texShader) { mainTexture = tex };
                        else { mr.sharedMaterial = new Material(Shader.Find("Unlit/Color")) { color = PartColor(rel) }; }
                    }
                    if (avatar != null && sub.BindVerts != null && sub.BoneHrc != null)
                        avatar.AddPart(sub.Mesh, sub.BindVerts, sub.BoneHrc, sub.BoneWt, sub.MshInvBindByHrc);
                    var mb = sub.Mesh.bounds;
                    if (!any) { bounds = mb; any = true; } else bounds.Encapsulate(mb);
                }
                parts++;
            }
            if (!any) { Debug.LogWarning("[avatar] no parts loaded"); return; }
            Debug.Log($"[avatar] {(localPlayerMale ? "MAN" : "WOMAN")}: {parts} parts, skeleton={(hrc != null ? hrc.Names.Length + " bones" : "none")}, mot={(mot != null ? mot.MaxTime + 1 + " frames" : "none")}");
            var handYellow = new Color(1f, 0.86f, 0.25f);
            if (use3dCamera) LoadCvCameras();
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
                if (avatar != null) avatar.PoseInitialIdle();   // arm the idle so the first frame doesn't crossfade from the measurement T-pose
                if (!avatarDebug && avatar != null)
                    try   // never let a hand-glow hiccup abort scene/audio setup (which run AFTER TryLoadAvatar)
                    {
                        CreateHandTrail(parent.transform, avatar, "Bip01_L_Hand", "Bip01_L_Finger0", handYellow);
                        CreateHandTrail(parent.transform, avatar, "Bip01_R_Hand", "Bip01_R_Finger0", handYellow);
                    }
                    catch (System.Exception e) { Debug.LogError("[handtrail] creation failed (non-fatal): " + e); }
                CreateGroundStarRing(_avatarChest.x, _avatarChest.z, 0.6f, avatar, parent.transform);   // follows the dancer's pelvis
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
        private float _timeScale = 1f;   // current (non-paused) slow-motion factor for burst observation
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
        private Vector3 _danceSpot = Vector3.zero;     // solo floor spot (0,0,0); dancer's feet stand here
        private Vector3 _avatarChest;                  // dancer chest world point (star-ring / bounds / debug framing only)
        private bool _camReady;                        // director shots loaded
        private float _camSwitchTime;                  // F2 label/timing only
        private Camera _sceneCam;
        private Material _backdropMat; private bool _backdropFlip;   // F9 toggles the stage V-flip (safety net)
        private Transform _avatarRoot;   // the Avatar3D root (for the debug front-camera framing)
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
        }
        // Result hand-off (read by the front-end once the song/run has ended). _score is plain managed state, so it
        // stays readable after this GameObject is destroyed as long as the caller grabs the reference first.
        public bool Finished => _ended;          // song played out (or failed) — time to settle
        public bool Failed => _failed;           // HP ran out
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

        // EXACT decompiled mapId(3..18) -> CDT, recovered from the 021_gameplay jump tables (disassembled):
        // solo/small (playerCount<=3) @0x4780b8 pushes the _1 variants; group (4..6) @0x4780f8 pushes the base.
        // SCN0009 = mapId 9 = the PALACE -> palace_1.cdt (solo) / palace.cdt (group). null entry = decompiled fallback.
        private static readonly string[] SoloCdt  = { "Garage_1","sea_1","Christmas_","playground_","sky_1","egypt_1","palace_1","huache_1",null,"fifa_1","fifa_1","ocean_1","Ghosthill_1","street_1","railway_1","houseboat_1" };
        private static readonly string[] GroupCdt = { "Garage","sea","Christmas","playground","sky","egypt","palace","huache",null,"fifa","fifa","ocean","Ghosthill","street","railway","houseboat" };

        // scenePath "SCENE/SCN0009" -> 9 (matches the decompiled mapId = DAT_00674f04+0x5c).
        private int SceneMapId()
        {
            var m = System.Text.RegularExpressions.Regex.Match(scenePath ?? "", @"SCN(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        // mapId 3-18 → CDT stem; solo(n<=3) vs group(n=4-6); fallback chain: map→numeric→1
        private string SelectCdtPath()
        {
            int map = SceneMapId();
            int n   = Mathf.Max(1, playerCount);
            string[] table  = n <= 3 ? SoloCdt : GroupCdt;
            string fallback = n == 1 ? "1" : n <= 3 ? "3" : "6";
            string mapped   = map >= 3 && map <= 18 ? table[map - 3] : null;
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
            var subMats = new List<Material[]>(r.Submeshes.Count);
            foreach (var sub in r.Submeshes)
            {
                Material[] mats;
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
                    for (int s = 0; s < sub.Ranges.Count; s++)
                    {
                        int a = sub.Ranges[s].Attrib;
                        string nm = (sub.DdsNames != null && a >= 0 && a < sub.DdsNames.Length && !string.IsNullOrEmpty(sub.DdsNames[a])) ? sub.DdsNames[a] : sub.Dds;
                        var tex = ResolveDds(dir, nm, out bool a2, out bool glow2);
                        mats[s] = NewMapobjMat(tex, fallbackCol, a2 && !opaque, a2 && !opaque && volumetric, a2 && !opaque && singleSidedAlpha, glow2);
                    }
                }
                else
                {
                    var tex = ResolveDds(dir, sub.Dds, out bool a1, out bool glow1);
                    mats = new[] { NewMapobjMat(tex, fallbackCol, a1 && !opaque, a1 && !opaque && volumetric, a1 && !opaque && singleSidedAlpha, glow1) };
                }
                subMats.Add(mats);
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
                foreach (var ms in subMats) if (ms != null) foreach (var m in ms) if (m != null) ApplyMapobjRenderMode(m, renderMode);
                Debug.Log($"[mapobj] {baseName}: render-mode {renderMode}");
            }

            // UV-scroll (the original streams texture coords on some props): e.g. SCN0014 corals scroll V so their glow
            // marquees. Drive the shared submesh materials' main-tex offset. Needs Repeat wrap (DdsLoader sets it).
            Vector2 uvScroll = SceneMapobjUvScrollCatalog.Find(SceneFolder(), baseName);
            if (uvScroll != Vector2.zero)
            {
                var scrollMats = new List<Material>();
                foreach (var ms in subMats) if (ms != null) foreach (var m in ms) if (m != null) scrollMats.Add(m);
                if (scrollMats.Count > 0)
                {
                    var holder = new GameObject(baseName + "_uvscroll");
                    holder.AddComponent<MapobjUvScroll>().Init(scrollMats.ToArray(), uvScroll);
                    Debug.Log($"[mapobj] {baseName}: uv-scroll {uvScroll}");
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
                        avatar.AddBoneMeshBaker(bone, bakeMesh, src, ShouldApplyRigidBindScale(srcMesh, hrc.BindWorld[bone].lossyScale));
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
            }
            Debug.Log($"[mapobj] {baseName}: {instances.Length}× {(animated ? "animated(shared)" : hrc != null ? "static-skinned" : "static")}, {(hrc != null ? hrc.Names.Length + " bones" : "no skel")}");
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

        private static bool ShouldApplyRigidBindScale(Mesh mesh, Vector3 bindScale)
        {
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
                b = res.Mesh.bounds;
                // render at NATIVE SDO world coords (no lift). The .cv cameras + the avatar dance spot (_avatarChest)
                // are authored in this same space with the dancer standing on the native floor, so they line up.
                Debug.Log($"[scene] {SceneFolder()}: {res.Materials.Length} subsets, bounds c={b.center} s={b.size}");
                TryLoadMapobjs();   // stage props on the same layer
                TryLoadSceneAvatars();   // background NPCs ("場景的人" — e.g. SCN0017 subway passengers)
                SpawnSceneEffects();   // persistent background EFTs (SCN0008 magic circle, snow, aurora, …)
            }

            // Perspective camera renders the stage(+avatar, same layer) to a RenderTexture; a full-screen background
            // quad in the main ortho cam shows that RT (reliably displays; depth-stacked cameras came out all-black).
            // Size the RT to the on-screen 4:3 region (≈1:1 at fullscreen instead of upscaling a flat 800×600) and give
            // it 4× MSAA, so the 3D avatar/stage edges stay smooth fullscreen rather than jagged.
            int rtH = Mathf.Clamp(Screen.height, 600, 1600);
            int rtW = Mathf.RoundToInt(rtH * (4f / 3f));
            var sceneRT = new RenderTexture(rtW, rtH, 24) { name = "sceneRT", antiAliasing = 4, filterMode = FilterMode.Bilinear };
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
        }

        private static Sprite _piyoriSprite;
        private static Texture2D _piyoriTex;
        // yuanpan.eft emitter[0]: a 14-segment annulus mesh (inner:outer = 0.18:0.27), each segment textured with
        // the FULL real generic\zako\z_piyori1 (a HOLLOW gold star) — i.e. 14 hollow stars round the ring, drawn the
        // engine's way (a band mesh, not sprites). Additive, flat on floor, spins, follows the dancer's pelvis.
        // ringOuterRadius = spread (ring radius); ringBrightness = additive glow level; both live-tunable in the F4 panel.
        public float ringOuterRadius = 22f, ringSpinDeg = 20f, ringBrightness = 0.9f;
        private Transform _ringTr; private Material _ringMat; private FloorRing _floorRing;   // live refs for debug tuning
        private void CreateGroundStarRing(float x, float yOrZ, float floorY, SdoAvatar avatar, Transform avatarParent)
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
                _ringTr = ringGo.transform; _ringMat = mat; _floorRing = fr;
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
                ring.Stars = stars; ring.Spin = 0.6f; ring.Tint = Color.white;
                ringGo.transform.position = new Vector3(x, yOrZ + 4f, 6f);
                ring.Billboard = true; ring.Rx = 70f; ring.Ry = 20f; ring.BaseScale = 36f / 64f;
            }
        }

        // Live-apply the F4 ring sliders. Mesh is unit-radius, so localScale = spread; _TintColor.rgb = brightness
        // (legacy-particle additive ×2 → ringBrightness*0.5 = native); keep _TintColor.a = 1 so the SrcAlpha-One blend
        // doesn't dim it a SECOND time (that earlier double-dim is what made it vanish).
        private void ApplyRingDebug()
        {
            if (_ringTr == null) return;
            _ringTr.localScale = Vector3.one * ringOuterRadius;
            if (_ringMat != null && _ringMat.HasProperty("_TintColor"))
            {
                float tb = Mathf.Clamp01(ringBrightness * 0.5f);
                _ringMat.SetColor("_TintColor", new Color(tb, tb, tb, 1f));
            }
            if (_floorRing != null) _floorRing.SpinDegPerSec = ringSpinDeg;
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
                mr.sortingOrder = -4;   // in front of the body, behind the HUD
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
            }
            rib.hand = handGo.transform; rib.finger = fingerGo.transform;
            rib.color = col; rib.life = handTrailTime; rib.widthMul = handTrailWidth;
            _handTrails.Add(rib);
        }

        // ---- head emoji cut-ins (UI/PLAYINGEXP) -------------------------------------------------------------------
        private static readonly string PlayingExpDir = Path.Combine(SdoExtracted.Root, "UI", "PLAYINGEXP");

        // Load a <prefix>NNN.PNG sequence (000..count-1) as sprites. Cut-ins hold each frame 50ms and last ~4s, so the
        // short sequence loops (PlayingEmoji does the looping); we just load the frames once here.
        // bleed:true dilates the transparent-WHITE matte — these frames store a (255,255,255) matte with HARD binary
        // alpha, so without it bilinear filtering blends each glyph edge straight into white = the "白邊" halo.
        private static Sprite[] LoadEmojiSeq(string prefix, int count)
        {
            var arr = new List<Sprite>(count);
            for (int i = 0; i < count; i++)
            {
                var s = SdoExtracted.LoadImage(PlayingExpDir, $"{prefix}{i:D3}.PNG", bleed: true);
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

        // DPS row -> MotLoader, cached. The choreography clips live in AUMOTION/ (fall back to MOTION/).
        private MotLoader ResolveMot(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            name = ResolveGenderedMotName(name);
            if (_motCache.TryGetValue(name, out var cached)) return cached;
            MotLoader m = null;
            foreach (var dir in new[] { "AUMOTION", "MOTION" })
            {
                var p = Path.Combine(SdoExtracted.Root, dir, name);
                if (File.Exists(p)) { try { m = MotLoader.Load(File.ReadAllBytes(p)); } catch { } if (m != null) break; }
            }
            _motCache[name] = m;   // cache even null to avoid re-probing missing files
            return m;
        }

        private string ResolveGenderedMotName(string name)
        {
            if (!localPlayerMale) return name;
            string file = Path.GetFileName(name.Replace('\\', '/'));
            if (string.IsNullOrEmpty(file) || file[0] != 'W') return name;

            string maleName = "M" + file.Substring(1);
            foreach (var dir in new[] { "AUMOTION", "MOTION" })
            {
                if (File.Exists(Path.Combine(SdoExtracted.Root, dir, maleName))) return maleName;
            }
            return name;
        }

        // resolve a material's .dds name to a file in the avatar dir (case-insensitive), load it
        private Texture2D ResolveDds(string dir, string ddsName) => ResolveDds(dir, ddsName, out _);

        // Resolve a mapobj texture by material name and report whether it carries real alpha (so the caller can
        // alpha-blend its "去背" cut-out instead of painting it opaque). Reads the file once for both.
        private Texture2D ResolveDds(string dir, string ddsName, out bool hasAlpha)
        {
            return ResolveDds(dir, ddsName, out hasAlpha, out _);
        }

        private Texture2D ResolveDds(string dir, string ddsName, out bool hasAlpha, out bool additiveGlow)
        {
            hasAlpha = false;
            additiveGlow = false;
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
                // Alpha-blended cut-out props (e.g. SCN0026 背景汽車 — flat DXT3 billboards on a WHITE matte) bled a
                // white halo at the silhouette under straight alpha blending. Edge-bleed the decoded RGB so the
                // transparent matte carries the prop's own colour instead. Additive glows are excluded: their low-
                // alpha RGB IS the glow and must not be dilated. No-op on opaque textures, so it's safe by default.
                return DdsLoader.Load(bytes, bleedAlphaEdges: hasAlpha && !additiveGlow);
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

        // Slow-motion for burst observation: scales Time.timeScale (so the 50Hz EFT sim + everything slows together).
        private void SetTimeScale(float s) { _timeScale = Mathf.Clamp(s, 0.03f, 2f); Time.timeScale = _timeScale; }

        private void Update()
        {
            if (!_sceneBootDone) return;   // stage is still building behind the loading screen — nothing to drive yet
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f), 0.1f);   // smoothed debug FPS
            if (_fpsText) _fpsText.text = "FPS " + Mathf.RoundToInt(_fps);
            if (Input.GetKeyDown(KeyCode.F4)) _showDebugUI = !_showDebugUI;        // toggle the tuning sliders
            if (Input.GetKeyDown(KeyCode.F8)) { EftEffect.DebugMeshOnly = !EftEffect.DebugMeshOnly; Debug.Log("[dbg] DebugMeshOnly=" + EftEffect.DebugMeshOnly + " (isolate the delta_line 3-colour mesh: hides disc/lightbars/MW, mesh at 5×)"); }
            if (Input.GetKeyDown(KeyCode.F7)) { showtimeMode = !showtimeMode; SetEnergyHudVisible(showtimeMode); SetTrackVisible(_trackVisible); Debug.Log("[showtime] mode=" + showtimeMode); }   // DEBUG F7: toggle ShowTime (氣條) mode — SetTrackVisible refreshes HP-bar visibility for the new mode
            if (Input.GetKeyDown(KeyCode.B)) SpawnComboBurst(0);   // DEBUG B: fire the 100COMBO floor ring burst on demand
            // BURST OBSERVE controls: 1-5 fire 100..500COMBO, 0 fires FINISHED; [ / ] slow/speed time, \ pause, = reset.
            if (Input.GetKeyDown(KeyCode.Alpha1)) SpawnComboBurst(0);
            if (Input.GetKeyDown(KeyCode.Alpha2)) SpawnComboBurst(1);
            if (Input.GetKeyDown(KeyCode.Alpha3)) SpawnComboBurst(2);
            if (Input.GetKeyDown(KeyCode.Alpha4)) SpawnComboBurst(3);
            if (Input.GetKeyDown(KeyCode.Alpha5)) SpawnComboBurst(4);
            if (Input.GetKeyDown(KeyCode.Alpha0)) SpawnNamedEft("FINISHED", 5f);
            if (Input.GetKeyDown(KeyCode.F5) && _started && !_ended)
            {
                // DEBUG F5: cut the song short → jump to the result sequence. Shift+F5 forces HP-out → the GAME OVER
                // death flow (Frameextrude + 死亡字幕 + no win/lose pose), for verifying it without grinding HP to zero.
                if (!showtimeMode && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) _failed = true;
                _ended = true; EnterResult();
            }
            if (Input.GetKeyDown(KeyCode.LeftBracket)) SetTimeScale(_timeScale * 0.5f);    // [ slower
            if (Input.GetKeyDown(KeyCode.RightBracket)) SetTimeScale(_timeScale * 2f);     // ] faster
            if (Input.GetKeyDown(KeyCode.Backslash)) { if (Time.timeScale > 0f) Time.timeScale = 0f; else SetTimeScale(_timeScale); }  // \ pause/resume
            if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadEquals)) SetTimeScale(1f);   // = reset 1×
            ApplyRingDebug();   // live floor-ring spread/brightness/spin from the F4 sliders
            TickAmbient();      // intermittent per-scene ambience (sea/stadium/underwater/garden)
            if (_board) { if (!Mathf.Approximately(boardAlpha, _boardAlphaApplied)) ApplyBoardAlpha(); SdoLayout.PlaceTopLeft(_board, boardX, 0f, 10f); }   // live board opacity + X nudge
            // F9: toggle the stage backdrop V-flip (safety net — the RenderTexture vertical convention is auto-gated
            // on graphicsUVStartsAtTop, but if the stage still shows upside-down on this machine, F9 flips it).
            if (Input.GetKeyDown(KeyCode.F9) && _backdropMat != null)
            {
                _backdropFlip = !_backdropFlip;
                _backdropMat.mainTextureScale = new Vector2(1f, _backdropFlip ? -1f : 1f);
                _backdropMat.mainTextureOffset = new Vector2(0f, _backdropFlip ? 1f : 0f);
            }
            if (_sceneCam != null && use3dCamera && !avatarDebug && _camReady)
            {
                // F2 (decompiled gameplay cmd 0x3c): camMode++ over 0..5, past 5 wraps to -1 = the auto-director.
                if (Input.GetKeyDown(KeyCode.F2)) CycleCamMode();
                Vector3 eye, tgt;
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
                    _dirCv[_dirShot].Sample(el / durSec, out eye, out tgt);     // VERBATIM .cv eye/target (Camera_Update)
                    // Camera_GetEyePos/GetTargetPos: add the dance-spot anchor ONLY for relative (:1) shots;
                    // absolute (:0) shots (e.g. the opening crane) use raw .cv world coords. Solo spot = 0 either way.
                    if (!_dirAbs[_dirShot]) { eye += _danceSpot; tgt += _danceSpot; }
                }
                else
                {
                    // FIXED camera: the exact decompiled static eye/target (DAT_005824f0/0x582538), absolute world.
                    int fi = Mathf.Clamp(_camMode, 0, FixedEye.Length - 1);
                    eye = FixedEye[fi]; tgt = FixedTgt[fi];
                }
                _sceneCam.transform.position = eye;
                _sceneCam.transform.LookAt(tgt, Vector3.up);
            }
            if (!_started) return;
            double now = (Time.timeAsDouble - _clockStart) * 1000.0;
            _nowMs = now;
            _clock.SetAudioSeconds(now / 1000.0);
            if (showtimeMode) UpdateBanner();   // song-end SHOW TIME flourish must tick post-song too (UpdateHud stops when _ended)
            if (_ended) { ResultTick(); UpdateFx(); return; }   // post-song: finish sequence drives avatar/camera/panel; gameplay frozen (FX still tick out)
            ScrollNotes(now);
            TickShowtime(now);   // ShowTime: SPACE release + window expiry (before judging so this frame already auto-hits)
            if (!_failed)
            {
                if (_showtime.Active) AutoPlay(now, showtime: true);   // ShowTime window: force PERFECT, ignore manual input
                else if (autoPlay) { AutoPlay(now); _stJustEnded = false; }   // dev auto-play never handoffs → drop any pending seam flag
                else { HandleInput(now); AutoMiss(now); }
            }
            UpdateDanceGate(now);   // dancer dance/stop decision (after judging, so this frame's misses count)
            RecordGate(now);        // log gate transitions for the result-screen background replay
            // long note held -> continuous burst that loops ONE full animation at a time (gated). Only this
            // hold case waits for the round to finish; taps fire freely above.
            for (int lane = 0; lane < Keys; lane++)
                if (_holding[lane] != null && !_hit3dMode && _burstFrames != null && _holdBurst[lane] == null) _holdBurst[lane] = SpawnBurst(lane, true);  // 3D skin fires only the one-shot head burst
            UpdateClickFlash();
            UpdateFx(); UpdateHud();
            // ShowTime mode has NO HP failure — only the 集氣 (energy) gauge matters. The song must never GAME OVER on
            // HP-out; it only ends naturally at the song's end (below). Normal mode still fails on HP-out.
            if (!showtimeMode && _health != null && _health.IsFailed) _failed = true;
            if (!_ended && (_failed || now > _totalMs + 2000)) { _ended = true; EnterResult(); }
        }

        // Song finished (or HP-out): freeze gameplay, hide the note board, play the win/lose 定格 pose on the
        // winning dancer, and fire the FINISHED burst on the winner. Mirrors decompiled FinishSequenceTick phase4
        // (021_gameplay:2674) — the winner (top score) plays cat5, everyone else cat4.
        private void EnterResult()
        {
            if (_audio) _audio.Stop();                        // stop the song (natural end already silent; matters for F5 mid-song cut)
            if (showtimeMode) { SetEnergyHudVisible(false); _scoreRoll?.SetVisible(false); _bonusRoll?.SetVisible(false); }   // hide the gauge AND the big/small ShowTime score at song end (not on the result panel)
            RebuildRoster();                                  // finalize scores so the rank/winner is current
            var (rank, _) = RankingBoard.LocalRank(_roster);
            _localWon = rank <= 1;                            // rank 1 = highest score = winner
            _gameOver = _failed;                              // HP-out → GAME OVER (overrides win/lose banner)
            // STAGE 1 (win/lose pose): clear ONLY the note board (+HP/receptors) and its combo/judgment words.
            // The top score, centre rank and right-side roster STAY visible until the result panel appears.
            SetTrackVisible(false);                           // note board + HP + receptors + click strips
            if (showtimeMode) ClearShowtimeWindowFx();        // song ended mid-window → kill the body_star aura + EDGE4 side lightning (they follow the now-hidden board)
            // SetTrackVisible(false) also hid the ranking — but it must STAY up through the win/lose pose (final
            // standings). Re-show it here with the final order; only HideHudForPanel (result panel) hides it.
            if (_rosterName != null) { UpdateRosterList(); UpdateRankDisplay(); SetRankingVisible(true); }
            HideComboAndJudge();                              // combo number + judgment word (part of the play board)
            ClearGameplayFx();                                // tear down in-flight bursts/holds (F5 mid-song leaves a hold burst looping)
            if (_emoji != null) _emoji.Stop();                // clear any head emoji cut-in so it doesn't linger into the result
            foreach (var n in _notes)                         // also kill any note sprites still in flight
            { if (n.Head) n.Head.enabled = false; if (n.Body) n.Body.SetActive(false); if (n.Tail) n.Tail.enabled = false; if (n.Cap3d) n.Cap3d.SetActive(false); }
            if (_gameOver)
            {
                // 血條用完死掉 (HP-out): 不放結束勝利/失敗的定格動作與 FINISHED effect;改播死亡字幕 GAME OVER (置中) +
                // Frameextrude 音效。多人時只有「全員陣亡」才走這條;只要有人沒死,倖存者在歌曲結束照走原本輸贏流程 —
                // 本重製為單人(mock 對手無 HP、不會死),故 _failed(本人陣亡)== 全員陣亡。
                PlaySe("Frameextrude");
                LoadGameOverFrames();                             // 依當前 note skin 選對應的 GAMEOVER 圖 (per-skin)
                StartCoroutine(GameOverAnim());
            }
            else
            {
                if (_avatar != null)                              // win/lose 定格 pose (cat5/cat4), held on its last frame
                {
                    var mot = ResolveMot(_localWon ? winMot : loseMot);
                    if (mot != null) _avatar.PlayOneShot(mot, true);
                }
                // FINISHED is a combo-style burst attached to the WINNER's dancer (follows _ringTr). The remake renders
                // only the local avatar, so it shows when the local player is the winner; otherwise no rendered dancer.
                if (_localWon) SpawnNamedEft("FINISHED", 5f);
                if (enableResultSfx) PlaySe(_localWon ? "SE_0014" : "SE_0015");   // win/lose jingle (off until clips verified)
            }
            _resultPhase = ResultPhase.FinishPose; _resultPhaseStart = Time.time;
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
            if (_musicName) _musicName.gameObject.SetActive(false);
            if (_lvText) _lvText.gameObject.SetActive(false);
            if (_timeText) _timeText.gameObject.SetActive(false);
            if (_info) _info.gameObject.SetActive(false);
            if (_headMarker) _headMarker.Hide();               // arrow + the "玩家" name label (separate root object)
        }

        // On the result panel, the old top song-name/level row is gone; instead the gameplay HUD's bottom song-info row
        // (歌曲名 + LV) stays visible just below the panel (it ends at design y≈565, the row sits at y=575). The 時間 field
        // is dropped: the time value is hidden and the combined "LV: 时间:" label is swapped to the "LV:"-only crop.
        private void ShowResultSongInfo()
        {
            if (_lblSong) _lblSong.enabled = true;                          // "歌曲名:"
            if (_musicName) _musicName.gameObject.SetActive(true);         // song title value
            if (_lvText) _lvText.gameObject.SetActive(true);              // LV value
            if (_lblAttr)
            {
                if (_lvOnlyLabel != null) _lblAttr.sprite = _lvOnlyLabel;  // "LV:" only (drop "时间:")
                SdoLayout.PlaceTopLeft(_lblAttr, 204, 575);                // re-place: cropped sprite has narrower bounds
                _lblAttr.enabled = true;
            }
            if (_timeText) _timeText.gameObject.SetActive(false);         // 時間欄位移除
        }

        // Drive the post-song sequence: hold the win/lose pose, then settle the panel, then loop the background
        // replay. Phase A implements FinishPose; Settle/Replay are filled in by later phases.
        private void ResultTick()
        {
            UpdateHeadPortraitCam();   // keep the local head-portrait cam tracking the (moving) head each frame
            float el = Time.time - _resultPhaseStart;
            switch (_resultPhase)
            {
                case ResultPhase.FinishPose:
                    if (el >= finishPoseSec) { ShowResultPanel(); _resultPhase = ResultPhase.Settle; _resultPhaseStart = Time.time; }
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
            if (_avatar == null) return;
            _avatar.ClearOneShot();                                   // resume the DPS dance path
            _replayLenMs = _totalMs > 1.0 ? _totalMs : Math.Max(1.0, _replay.LengthMs);
            _replayLoopStart = Time.timeAsDouble;
            _avatar.DanceTimeSec = () => (float)((LoopMs() ) / 1000.0);
            _avatar.DanceEnabled = () => GateAt(LoopMs());
        }

        // Current position within the looping background replay (ms, 0.._replayLenMs).
        private double LoopMs()
        {
            double t = (Time.timeAsDouble - _replayLoopStart) * 1000.0;
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
            string diff = _map != null ? "Lv " + _map.Level : "";
            var rows = BuildResultRows();   // also rebuilds _roster so the rank/total below are current
            // round-end reward for the LOCAL player (Arrowgene emulator formulas — see Sdo.Ruleset.Reward).
            var (place, players) = RankingBoard.LocalRank(_roster);
            int bad = _score != null ? _score.BadCount : 0, miss = _score != null ? _score.MissCount : 0;
            // 自由模式不加 G幣/EXP
            int expGained = freeMode ? 0 : Sdo.Ruleset.Reward.Experience(bad, miss, place, players);
            int coinsGained = freeMode ? 0 : Sdo.Ruleset.Reward.Coins(bad, miss, place, players, playerLevel);
            Texture head = BuildLocalHeadPortrait();   // live 3D head for the local row (null → placeholder)
            // 自由模式不出 YOU WIN/LOSE 字幕 (但結算最後的 SE_0022 音效仍要有 → ResultScreen 內處理)。GAME OVER 同理不出旗。
            _result.Show(_songTitle, diff, rows, _localWon, expGained, coinsGained, head, _gameOver, PlaySe, showBanner: !freeMode);
        }

        // Turn the final roster + score into ranked result rows. The local player uses real judgment counts;
        // mock opponents get plausible counts synthesised from their score (no real per-opponent judging).
        private ResultScreen.Row[] BuildResultRows()
        {
            RebuildRoster();
            var order = RankingBoard.SortedIndices(_roster);
            int total = Math.Max(1, _notes.Count);
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
                // HP-out (failed) → 評分 F for the local player; everyone else by accuracy band.
                r.Grade = (p.IsLocal && _failed) ? "F" : Sdo.Ruleset.Grade.FromAccuracy(r.Accuracy);
                rows[i] = r;
            }
            return rows;
        }

        // Build the scroll positioner from the loaded chart + the selected speed step. Constant base speed
        // across songs (referenceBpm anchor) with osu-style mid-song BPM/SV variation (or none if constantScroll).
        private void BuildScroll()
        {
            _scroll = ManiaScroll.Build(_map, scrollSpeedMul, constantScroll, referenceBpm);
            Debug.Log($"[Step1] scroll vBase={_scroll.BaseVelocity:F0}px/s (speed {scrollSpeedMul}× @ {referenceBpm}bpm)"
                + $", {_map.TimingPoints.Count} timing pts, constant={constantScroll}");
        }

        // UPSCROLL (matches the official screen): future notes are below the hit line and RISE to it. Distance
        // comes from ManiaScroll (osu Sequential integration), so mid-song BPM changes / SV vary it locally.
        private float YForTime(double noteMs, double now)
        {
            if (_scroll == null) BuildScroll();
            return judgeLineY + (float)_scroll.PixelDistance(now, noteMs);
        }

        private void ScrollNotes(double now)
        {
            bool use3d = _note3dMode && note3dMesh && EnsureHighway();   // real 3D mesh highway (else 2D coloured-sprite path)
            _highwayItems.Clear();
            foreach (var n in _notes)
            {
                if (n.Done) { n.Head.enabled = false; if (n.Body) n.Body.SetActive(false); if (n.Tail) n.Tail.enabled = false; if (n.Cap3d) n.Cap3d.SetActive(false); continue; }
                int c = n.Note.Lane;
                bool held = _holding[c] == n;        // a held long-note head stays pinned to the judge line
                float yRaw = held ? judgeLineY : YForTime(n.Note.StartTimeMs, now);
                float yEnd = n.Note.EndTimeMs.HasValue ? YForTime(n.Note.EndTimeMs.Value, now) : yRaw;
                // a note that has flowed off the top (above the clip band, past the HP bar) is no longer VISIBLE,
                // but it is NOT retired yet: it stays alive and judgeable until its miss window actually elapses.
                // On slow songs the off-top point comes BEFORE MissBoundary, so retiring it here would skip the
                // Miss — instead we just hide it and let AutoMiss (or a late in-window press) judge it at the proper
                // time, then retire it once it's been judged. (disappears at the same spot as before, still scored.)
                if (!held && Mathf.Max(yRaw, yEnd) < NotesClipTop - 36f)
                {
                    n.Head.enabled = false; if (n.Body) n.Body.SetActive(false); if (n.Tail) n.Tail.enabled = false; if (n.Cap3d) n.Cap3d.SetActive(false);
                    if (n.HeadJudged) n.Done = true;   // hit late / auto-missed -> now fully retired
                    continue;
                }
                bool visible = held || Mathf.Min(yRaw, yEnd) <= NotesClipBottom + 60f;   // shown once it enters from the bottom; SpriteMask clips it to the board
                n.Head.enabled = visible;
                if (!visible) { if (n.Body) n.Body.SetActive(false); if (n.Tail) n.Tail.enabled = false; if (n.Cap3d) n.Cap3d.SetActive(false); continue; }
                float y = yRaw;   // NO clamp — notes keep flowing past the receptor (the mask hides them above the HP bar)
                int frame = ((int)(Time.time * noteAnimFps)) & 3;   // 4-frame glow cycle (= the official _0.._3 frames)
                bool stWin = showtimeMode && _showtime.Active;
                float noteScale = stWin ? showtimeNoteScale : 1f;       // notes grow a little during the auto-hit window
                float noteW = LaneW * noteScale;
                if (showtimeMode) { n.Head.color = _noteTint; if (n.Tail) n.Tail.color = _noteTint; }   // gold→red flash over the window's last 3s
                if (use3d)
                {
                    // 3D-MESH head: draw the real NOTES.MSH arrow FLAT at this note's exact 2D position (same lane + scroll
                    // Y as the sprite), textured by the beat family, additive. The 2D head sprite is hidden; the hold
                    // body/tail below stay 2D. RotZ = the per-lane arrow direction.
                    // HOLD head is forced to family 0 = the on-beat (4th) MAGENTA (洋紅), regardless of its beat position.
                    _highwayItems.Add(new Note3dHighway.Item {
                        World = SdoLayout.ToWorld(LaneLeftX[c] + LaneCx0, y, -0.5f),
                        Size = LaneW * note3dMaster * noteScale, RotZ = Note3dRot[c] + (note3dFlip180 ? 180f : 0f),
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
                    PlaceAspect(n.Head, LaneLeftX[c] + LaneCx0, y, noteW, 1f);
                }

                if (n.Note.EndTimeMs.HasValue)
                {
                    float holdW = LaneW * (_note3dMode ? note3dHoldWidth * note3dMaster : 1f) * noteScale;   // 3D skin: hold width matches the note, scaled by the master (+ showtime grow)
                    float cx = LaneLeftX[c] + 34.5f;
                    float tailY = Mathf.Max(y, yEnd);                                   // true tail edge (below the head in upscroll)
                    if (_note3dMode)
                    {
                        // OFFICIAL cap = a welded TRIANGLE at the tail end (LONG.MSH verts 0/1/2), pointing away from the
                        // judge line — real geometry, not a sprite. The 2D sprite tail stays hidden while the 3D skin is on.
                        if (n.Tail) n.Tail.enabled = false;
                        if (n.Cap3d == null && _capMeshMat != null) n.Cap3d = CreateHoldCap();
                        if (n.Cap3d != null)
                        {
                            float capBaseY = tailY + note3dCapOffset;
                            float capLen = holdW * LongCapLenRatio;
                            bool capVis = capBaseY <= NotesClipBottom && capBaseY + capLen >= NotesClipTop;
                            n.Cap3d.SetActive(capVis);
                            if (capVis)
                            {
                                n.Cap3d.transform.position = SdoLayout.ToWorld(cx, capBaseY, 0.6f);
                                n.Cap3d.transform.localScale = new Vector3(holdW, holdW, 1f);
                            }
                        }
                    }
                    else if (n.Tail)
                    {
                        n.Tail.enabled = true;
                        // 2D cap sits at the tail END (yEnd), its own sprite/aspect.
                        PlaceAspect(n.Tail, cx, yEnd, holdW, 0.5f);
                        if (n.Cap3d != null && n.Cap3d.activeSelf) n.Cap3d.SetActive(false);
                    }
                    if (n.Body)
                    {
                        float top = Mathf.Max(Mathf.Min(y, yEnd) + (_note3dMode ? note3dHoldHeadGap : 0f), NotesClipTop);
                        float bot = Mathf.Min(Mathf.Max(y, yEnd), NotesClipBottom);
                        float len = Mathf.Max(0f, bot - top), midY = (top + bot) / 2f;
                        n.Body.SetActive(len > 0.5f);
                        if (showtimeMode) { var bmr = n.Body.GetComponent<MeshRenderer>(); if (bmr && bmr.sharedMaterial) bmr.sharedMaterial.color = _noteTint; }   // long-note body gold→red flash too
                        if (len > 0.5f)
                        {
                            n.Body.transform.position = SdoLayout.ToWorld(cx, midY, 0.6f);
                            n.Body.transform.localScale = new Vector3(holdW, len, 1);
                            var m = n.Body.GetComponent<MeshFilter>().mesh; var uv = m.uv;
                            if (_note3dMode)
                            {
                                // OFFICIAL body mapping (FUN_0041a7e0): sample ONLY U 0.2243..0.7683 of LONG_0_1 (the fat
                                // outer silver rails are outside the band, never drawn) and V = 1 − z·(1/31.2) with z
                                // ANCHORED AT THE TAIL (cap weld z=0.0287 → V≈0.999) — the chevrons stay glued to the cap
                                // no matter how the body is clamped/consumed. z = design px × (22.0074 mesh units / holdW).
                                float k = LongMeshW / Mathf.Max(holdW, 1e-3f);
                                float vBot = 1f - (LongZBase + (tailY - bot) * k) * LongVPerUnit;
                                float vTop = 1f - (LongZBase + (tailY - top) * k) * LongVPerUnit;
                                uv[0].x = LongU0; uv[3].x = LongU0; uv[1].x = LongU1; uv[2].x = LongU1;
                                uv[0].y = vBot; uv[1].y = vBot; uv[2].y = vTop; uv[3].y = vTop;
                            }
                            else
                            {
                                // 2D skin: tile the body texture square along the length (拼接, not stretch)
                                float tileH = holdW * (_holdTex[c].height / (float)_holdTex[c].width);
                                float tiles = len / Mathf.Max(tileH, 1e-3f);
                                uv[0].x = 0f; uv[3].x = 0f; uv[1].x = 1f; uv[2].x = 1f;
                                uv[0].y = 0f; uv[1].y = 0f; uv[2].y = tiles; uv[3].y = tiles;
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

        private void HandleInput(double now)
        {
            int mask = 0;
            var laneKeys = laneKeyOverride ?? DefaultLaneKeys;
            for (int lane = 0; lane < Keys; lane++)
            {
                bool down = false, anyHeld = false, anyUp = false;
                foreach (var k in laneKeys[lane])
                { if (Input.GetKeyDown(k)) down = true; if (Input.GetKey(k)) anyHeld = true; if (Input.GetKeyUp(k)) anyUp = true; }
                if (anyHeld) mask |= 1 << lane;
                if (down) { PressLane(lane, now); _recDownStart[lane] = Time.time; }   // any press fires the one-shot keydown burst
                else if (_stJustEnded) ReplayShowtimeSeamPress(lane, now, anyHeld);    // ShowTime auto→manual SEAM: replay the in-window press that lost its GetKeyDown edge onto the exact note it aimed at
                if (anyUp && !anyHeld) ReleaseLane(lane, now);   // released only when no set key is still held
            }
            if (_stJustEnded) { _stJustEnded = false; for (int i = 0; i < Keys; i++) { _stPressMs[i] = -1.0; _stReleaseMs[i] = -1.0; _stPressNote[i] = null; } }   // seam carry-over is a one-frame event
            _replay.Record(now, mask);   // osu-style 打擊紀錄 (appends only when the held-key bitmask changes)
        }

        // Record dance-gate transitions (the effective _dancing && !_failed each frame). Tiny: only changes at the
        // 8-beat settle or on HP-out. Drives the result-screen BACKGROUND replay so the looped dance reproduces the
        // original performance's stop/start gaps (the DPS choreography itself is deterministic from time).
        private void RecordGate(double now)
        {
            bool g = _dancing && !_failed;
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
            foreach (var n in _notes)
            {
                if (n.Done) continue;
                if (!n.HeadJudged && now >= n.Note.StartTimeMs)
                {
                    n.HeadJudged = true; ApplyEvent(grade, n.Note.Lane);
                    _recDownStart[n.Note.Lane] = Time.time;   // auto-press: fire the keydown burst (head only, never the hold tail)
                    if (grade == Judgment.Miss) { /* flows past the receptor, then ScrollNotes removes it */ }
                    else if (n.Note.IsHold) { _holding[n.Note.Lane] = n; SpawnHit3dLong(n.Note.Lane); }   // 3D: continuous HIT_LONG for the hold
                    else n.Done = true;
                }
                if (n.HeadJudged && !n.Done && grade != Judgment.Miss && n.Note.IsHold && _holding[n.Note.Lane] == n
                    && n.Note.EndTimeMs.HasValue && now >= n.Note.EndTimeMs.Value)
                { _holding[n.Note.Lane] = null; ApplyEvent(grade, n.Note.Lane); StopHit3dLong(n.Note.Lane); n.Done = true; }
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
                if (Input.GetKeyDown(KeyCode.Space) && _showtime.TryActivate(now, ComputeShowtimeWindowMs())) OnShowtimeStart();
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
            if (!n.Note.IsHold) { n.Done = true; return; }         // tap → done
            if (j.Value == Judgment.Bad) { n.BundledFail = true; return; }   // bad hold head → AutoMiss fails the tail later (matches PressLane)
            if (held) { _holding[lane] = n; return; }              // still holding across the seam → hold continues (tail judged on the later real release / AutoMiss)
            // player already let go INSIDE the window → judge the tail at the TRUE release time (clamped ≤ seam), not a lingering auto-Perfect and not the over-lenient seam time
            double relMs = _stReleaseMs[lane] >= 0.0 ? Math.Min(_stReleaseMs[lane], now) : now;
            ApplyEvent(_engine.JudgeHoldTail(n.Note.EndTimeMs ?? n.Note.StartTimeMs, relMs) ?? Judgment.Miss, lane);
            n.Done = true;
        }

        private void PressLane(int lane, double now)
        {
            var n = NearestHittable(lane, now); if (n == null) return;
            Judgment jv;
            if (forcedJudge >= 0) jv = (Judgment)forcedJudge;                         // debug: force a grade on the hit
            else { var j = _engine.JudgeHit(n.Note.StartTimeMs, now); if (j == null) return; jv = j.Value; }
            n.HeadJudged = true; ApplyEvent(jv, lane);
            if (jv == Judgment.Miss) { /* keep flowing past the receptor; ScrollNotes removes it off the top */ }
            else if (n.Note.IsHold) { if (jv == Judgment.Bad) n.BundledFail = true; else { _holding[lane] = n; SpawnHit3dLong(lane); } }   // 3D: continuous HIT_LONG for the hold
            else n.Done = true;
        }

        private void ReleaseLane(int lane, double now)
        {
            var n = _holding[lane]; if (n == null) return;
            _holding[lane] = null;
            ApplyEvent(_engine.JudgeHoldTail(n.Note.EndTimeMs ?? n.Note.StartTimeMs, now) ?? Judgment.Miss, lane);
            StopHit3dLong(lane);   // 3D: end the looping HIT_LONG → one-shot HIT_SUO terminator
            n.Done = true;
        }

        private RuntimeNote NearestHittable(int lane, double now)
        {
            RuntimeNote best = null; double bestAbs = double.MaxValue;
            foreach (var n in _notes)
            {
                if (n.Done || n.HeadJudged || n.Note.Lane != lane) continue;
                double d = Math.Abs(n.Note.StartTimeMs - now);
                if (d < bestAbs && d <= _engine.Windows.MissBoundary) { bestAbs = d; best = n; }
            }
            return best;
        }

        private void AutoMiss(double now)
        {
            foreach (var n in _notes)
            {
                if (n.Done) continue;
                if (!n.HeadJudged && _engine.HasPassed(n.Note.StartTimeMs, now)) { n.HeadJudged = true; ApplyEvent(Judgment.Miss); if (n.Note.IsHold) ApplyEvent(Judgment.Miss); continue; }   // judged miss, but keeps flowing off the top
                if (n.BundledFail && n.Note.EndTimeMs.HasValue && _engine.HasPassed(n.Note.EndTimeMs.Value, now)) { ApplyEvent(Judgment.Miss); n.Done = true; continue; }
                if (_holding[n.Note.Lane] == n && n.Note.EndTimeMs.HasValue && now >= n.Note.EndTimeMs.Value) { _holding[n.Note.Lane] = null; ApplyEvent(Judgment.Perfect); StopHit3dLong(n.Note.Lane); n.Done = true; }
            }
        }

        private void ApplyEvent(Judgment j, int lane = -1)
        {
            _score.Apply(j); _health.Apply(j);
            if (showtimeMode) _showtime.OnJudge(j);                               // ShowTime: fill the gauge (normal) or accrue the bonus (in a window)
            UpdateEmojiOnJudge(j);                                                // combo-milestone / consecutive-miss emoji cut-ins
            _blockHadNote = true;                                                // a note was judged this block (-> not an empty block)
            if (j == Judgment.Bad || j == Judgment.Miss) _blockHadBreak = true;   // break -> NOT stopped now; the dancer is re-decided at the next 8-beat settlement
            _judgeWord.sprite = _judgeSprites[(int)j]; _judgeWordAt = Time.time;
            if (lane >= 0 && (j == Judgment.Perfect || j == Judgment.Cool))   // tap: fire immediately, may overlap
            {
                if (_hit3dMode) SpawnHit3d(lane);                              // 3D skin: real AU_HIT.EFT burst at the receptor
                else if (_burstFrames != null) SpawnBurst(lane, false);       // 2D skins: sprite flipbook burst (during a window _burstFrames IS the EFT_SHOWTIME set)
            }
            // 3D skin: the official has NO lane click-strip glow on press and NO red board flash on miss — suppress both.
            if (lane >= 0 && j != Judgment.Miss && !_note3dMode) TriggerClickFlash(lane);   // light the struck lane's click strip (any contact, not a miss)
            if (j == Judgment.Miss && !_note3dMode) TriggerMissFlash();                     // miss: flash ALL four lane strips red once
        }

        // Every 8 beats (the score-settlement cadence) re-decide whether the dancer keeps dancing — a break NEVER
        // stops it mid-block, only this boundary does. Two conditions (see the _blockHadBreak field comment):
        //   1. block had a break (Bad/Miss) -> dance only if the current combo is still > 30, else stop.
        //   2. block had NO break but DID judge notes -> dance (clean block always dances, even at low combo).
        //      No break and NO notes at all -> keep the current state (a stopped dancer does not resume on silence).
        // while() so a long frame that skips a boundary still settles. _dancing is read by the avatar each frame.
        private void UpdateDanceGate(double now)
        {
            double settleMs = 8 * (60000.0 / Math.Max(1.0, _map.Bpm));   // 8 beats = 2 bars, same as the score commit
            if (_nextDanceSettleMs <= 0) _nextDanceSettleMs = settleMs;
            while (now >= _nextDanceSettleMs)
            {
                if (_blockHadBreak) _dancing = _score.Combo > 30;   // (1) broke -> carry on only with a strong (>30) combo
                else if (_blockHadNote) _dancing = true;            // (2) clean block with notes -> dance/resume
                // else: empty block (no break, no notes) -> hold the current _dancing state
                _blockHadBreak = false;
                _blockHadNote = false;
                _nextDanceSettleMs += settleMs;
            }
        }

        private const float BurstWidth = 235f;            // hit-burst draw size for the REFERENCE skin (EFT_13, 300px native)
        private const float BurstNativeRef = 300f;        // EFT_13 native px — bursts render native-proportional to this so
                                                          // a smaller skin (EFT_2=150, EFT_14=128) draws smaller, not stretched up to BurstWidth
        // a TAP burst fires on every hit and may overlap others on the same lane (no gating). A HOLD burst loops,
        // one full round at a time (gated). Each burst gets its OWN material clone so overlapping bursts never bleed.
        private BurstFx SpawnBurst(int lane, bool isHold)
        {
            // directional skins (PET/8/9/10) ship separate frames for left-right vs up-down lanes; lanes 1(down)/2(up) use
            // the _ud set, lanes 0(left)/3(right) use _rl (_burstFrames). Non-directional skins leave _burstFramesUD null.
            var frames = (_burstFramesUD != null && (lane == 1 || lane == 2)) ? _burstFramesUD : _burstFrames;
            if (frames == null || frames.Length == 0) return null;
            var mat = _matPool.Count > 0 ? _matPool.Pop() : (_addMat != null ? new Material(_addMat) : null);  // own instance, pooled
            // brightness: the additive shader is Blend SrcAlpha One, and its _TintColor defaults to (.5,.5,.5,.5) ->
            // the .5 alpha halves the burst (too dark). Drive _TintColor by burstBright (1.0 = stock, higher = brighter).
            if (mat != null) { float t = 0.5f * burstBright; mat.SetColor("_TintColor", new Color(t, t, t, Mathf.Clamp01(t))); }
            var sr = NewSR("Burst", frames[0], 6);
            if (mat != null) sr.sharedMaterial = mat;                   // additive -> black bg becomes transparent glow
            // native-proportional: scale by THIS skin's frame size vs the reference, so every skin keeps its true relative
            // size (the old fixed BurstWidth stretched a small 150px skin up to the 300px skin's footprint -> "too big").
            float burstNativeW = frames[0] != null ? frames[0].rect.width : BurstNativeRef;   // native px (PPU-independent)
            PlaceAspect(sr, LaneLeftX[lane] + LaneCx0, judgeLineY, BurstWidth * burstSize * (burstNativeW / BurstNativeRef));
            var sr2 = NewSR("Burst+", frames[0], 6);                   // 2nd additive layer -> vivid in-game glow
            if (mat != null) sr2.sharedMaterial = mat;
            sr2.transform.SetParent(sr.transform, false);
            var fx = new BurstFx { Sr = sr, Sr2 = sr2, Mat = mat, Lane = lane, Start = Time.time, IsHold = isHold, Frames = frames };
            _fx.Add(fx);
            return fx;
        }

        private sealed class RuntimeNote
        {
            public readonly OsuHitObject Note; public readonly SpriteRenderer Head, Tail; public readonly GameObject Body;
            public GameObject Cap3d;   // 3D skin: the official LONG.MSH cap TRIANGLE (real geometry, welded at the tail end); lazily created
            public bool HeadJudged, BundledFail, Done;
            public readonly int ColorFamily;   // 3D-note beat-quantization colour (0=magenta,1=blue,2=green); used only in _note3dMode
            public RuntimeNote(OsuHitObject n, SpriteRenderer head, GameObject body, SpriteRenderer tail, int colorFamily)
            { Note = n; Head = head; Body = body; Tail = tail; ColorFamily = colorFamily; }
        }
    }
}
