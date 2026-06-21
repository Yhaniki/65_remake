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
    public sealed class Step1Game : MonoBehaviour
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
        public float scrollPxPerSec = 320f;
        public float judgeLineY = 70f;        // receptor / hit line Y (design px). UPSCROLL: notes rise to it.
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
        private static readonly KeyCode[][] LaneKeys =
        {
            new[] { KeyCode.A, KeyCode.Keypad4 },   // 0 Left
            new[] { KeyCode.S, KeyCode.Keypad5 },   // 1 Down
            new[] { KeyCode.W, KeyCode.Keypad8 },   // 2 Up
            new[] { KeyCode.D, KeyCode.Keypad6 },   // 3 Right
        };

        // EXACT HUD coords (DdrGamePlay.xml absolute) + EFT positions (decompiled)
        private static readonly Vector2 HpSize = new Vector2(238, 11);
        private static readonly Vector2 HpPos = new Vector2(TrackCenterX - 119f, 18); // centred on the track (0..275)
        private static readonly Vector2 HpEftSize = new Vector2(64, 32);  // real HpEft1.png size
        private static readonly Vector2 ScorePos = new Vector2(290, 18);
        private const float ScoreDigitPitch = 25f;       // 29 + alt(-4)
        private static readonly Vector2 JudgeWordCenter = new Vector2(TrackCenterX, 216);
        private const float ComboWordY = 268f;
        private const float ComboDigitY = 326f, ComboDigitStep = 42f, ComboDigitW = 48f;
        // The COMBO word and the digits must render at ONE per-pixel scale so the label and the number read as the
        // same font (native COMBO.PNG = 117×33, each digit = 67×72). Deriving the word width from the digit width
        // locks word/number to the source-art ratio; a hardcoded 100 drew the word at 0.855× vs the digits' 0.716×.
        private const float ComboWordW = ComboDigitW * 117f / 67f;   // ≈ 83.8

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
        private float _introStartRt = -1f;     // realtime the intro began; <0 = no intro (track shown immediately)
        private bool _trackVisible = true;     // false during the opening hold (board + HP bar hidden, see SetTrackVisible)

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
        public float headPortraitDist = 23f;      // cam distance from the head (zoom) — tuned
        public float headPortraitFov = 35f;
        public Vector3 headAimOffset = new Vector3(-2.1f, 4.4f, 0f);   // look-target offset from the head BONE (centre the FACE) — tuned
        public float headAvatarScale = 1.05f;     // idle avatar uniform scale — tuned
        public float headAvatarYaw = 28f;         // idle avatar Y rotation (3/4 view angle; faces the cam) — tuned
        private Camera _headCam; private RenderTexture _headRt; private SdoAvatar _headAvatar;
        private Vector3 _headModelPos = new Vector3(0f, 50f, 0f);   // head bone REST pos (model space) — cam targets this so it stays FIXED (no per-frame bob chase)
        private static readonly Vector3 HeadAvatarSpot = new Vector3(5000f, 0f, 5000f);   // isolated parking spot (off the stage)

        private readonly List<RuntimeNote> _notes = new List<RuntimeNote>();
        private readonly RuntimeNote[] _holding = new RuntimeNote[Keys];
        private readonly Sprite[][] _noteFrames = new Sprite[Keys][];
        private readonly Texture2D[] _holdTex = new Texture2D[Keys];
        private readonly Sprite[] _holdTail = new Sprite[Keys];
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
        private SpriteRenderer _hpBg, _hpTex, _hpBackFrame, _hpGlow;
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
        private Material _addMat;           // additive material template; each burst clones its own instance
        private Material _hpGlowMat;        // HP-edge glow's OWN additive instance (dedicated so its _TintColor can be driven bright, and no _MainTex cross-bleed with bursts)
        private SpriteRenderer _readyGo;   // opening READY/GO overlay (centre screen)
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
        private SpriteRenderer _lblSong, _lblAttr;   // bottom "歌曲名:" / "LV: 时间:" labels (hidden at result)
        private float _fps;
        private double _totalMs;
        private int _lastMilestone;       // last combo milestone (50/100/150…) already celebrated
        private long _shownScore, _scoreFrom, _scoreTarget;  // (8) score commits every 8 beats, then counts up + zooms
        private double _nextScoreCommitMs; private float _scoreAnimAt = -10f;

        // ---- ranking UI (head nameplate + centre rank N/M + right-side roster list) ----
        // The remake renders ONE dancer; opponents are a configurable mock roster so the rank/list read
        // like the official multiplayer screen (see RankingBoard for the pure ordering logic).
        public bool mockOpponents = true;            // seed simulated opponents (default) vs. solo (rank 1/1, list of 1)
        public bool freeMode = false;                // 自由模式: no ranking UI during play, no G幣/EXP reward; HP-out still shows GAME OVER
        public string localPlayerName = "玩家";       // local player's display name (hardcoded default, tunable)
        public int playerLevel = 1;                  // character level — scales the round-end coin/honor reward (Sdo.Ruleset.Reward)
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
        private static readonly string[] SpectatorNames = { "酷", "美麗", "悲晴吉克", "路過旅人", "小幫手" };
        private SpriteRenderer _lookerTitle;
        private Label3D[] _lookerRows;
        public float lookerTitleX = 694f, lookerTitleY = 214f, lookerX = 698f, lookerFirstY = 236f, lookerRowStep = 16f, lookerFontWorld = 18f;
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
        // the front-end's kill — the kill only destroys the Step1Game object, not the separate roots it created — so a
        // leftover dancer lingers and the real launch then doubles it (two avatars on the dance-spot).
        public static bool AutoBootSuppressed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (AutoBootSuppressed) return;
            if (FindAnyObjectByType<Step1Game>() != null) return;
            new GameObject("Step1Game").AddComponent<Step1Game>();
        }

        private void Start()
        {
            ResolveDevDefaults();
            _cam = Camera.main ?? new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            SdoLayout.SetupCamera(_cam);
            LoadArt();
            if (!LoadChart()) return;
            BuildBoard();
            if (!observeBurstMode) SpawnNotes();   // observe mode: no notes (clean stage to watch the burst)
            foreach (var n in _notes) { double t = n.Note.EndTimeMs ?? n.Note.StartTimeMs; if (t > _totalMs) _totalMs = t; }
            BuildHud();
            TryLoadAvatar();
            TryLoadScene();
            _engine = new ManiaJudgmentEngine(JudgmentWindows.FromSdoBpm(_map.Bpm));
            _score = new ScoreProcessor(_map.TotalNotes);
            _health = new HealthProcessor(healthLevel);
            RefreshRanking();   // initial roster/rank (rank 1/N) before the first score commit
            _audio = gameObject.AddComponent<AudioSource>();
            _sfx = gameObject.AddComponent<AudioSource>();
            _ambient = gameObject.AddComponent<AudioSource>();
            var ambName = AmbientSeName(SceneMapId());   // load the per-scene ambience (sea/stadium/underwater/garden) if any
            if (!string.IsNullOrEmpty(ambName)) StartCoroutine(LoadAmbientCo(ambName));
            // Enter on the crane with no note board: hold the track hidden while the opening shot flies in, then
            // OpeningSequence() reveals it with READY. Only when there's actually a 3D crane to watch.
            if (use3dCamera && _camReady && openingIntroSec > 0f) { _introStartRt = Time.realtimeSinceStartup; SetTrackVisible(false); }
            if (observeBurstMode) { _dancing = false; _camMode = 0; SetTrackVisible(false); _introStartRt = -1f; }   // idle dancer, fixed cam, hidden track
            StartCoroutine(LoadAndPlayAudio());
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
            // Arm the first play after a short random gap (the engine's timer first fires once an interval elapses, so
            // it never blasts the moment the scene opens; the READY/GO intro gets a clear beat first).
            if (_ambientClip != null) _nextAmbientAt = Time.realtimeSinceStartup + UnityEngine.Random.Range(3f, 12f);
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

        private string NoteDir => Path.Combine(SdoExtracted.Root, "NOTEIMAGE", "NOTEIMAGE_5");

        private void LoadArt()
        {
            for (int c = 0; c < Keys; c++)
            {
                string d = Dir5[c];
                var fr = new Sprite[4]; bool ok = true;
                for (int f = 0; f < 4; f++) { fr[f] = SdoExtracted.LoadImage(NoteDir, d + "holdheadactive" + f + ".png"); if (fr[f] == null) ok = false; }
                if (ok) _noteFrames[c] = fr;
                _recIdle[c] = SdoExtracted.LoadImage(NoteDir, d + "_judgeline1.png");
                // keydown burst frames: *_judgeline2..6 in order (KEYDOWN_JUDGELINE.AN); fall back to idle if missing
                var rdf = new Sprite[5];
                for (int f = 0; f < 5; f++) rdf[f] = SdoExtracted.LoadImage(NoteDir, d + "_judgeline" + (f + 2) + ".png") ?? _recIdle[c];
                _recDownFrames[c] = rdf;
                string baseLong = (d == "left" || d == "right") ? "rightleft_long" : "updown_long";
                var bodySpr = SdoExtracted.LoadImage(NoteDir, baseLong + ".png");
                if (bodySpr != null) { _holdTex[c] = bodySpr.texture; _holdTex[c].wrapMode = TextureWrapMode.Repeat; SdoExtracted.AlphaBleed(_holdTex[c]); }
                // end cap = the ORIGINAL per-lane png (rightleft_long_bottom for L/R, updown_long_bottom for U/D), but
                // copied to a UNIQUE texture per lane (never shares the cache) + de-seamed, so a lane's cap can never
                // get confused with another lane's. Each tail also gets its own material (SpawnNotes) to stop any
                // SpriteMask batch cross-bleed.
                var capSpr = SdoExtracted.LoadImage(NoteDir, baseLong + "_bottom.png");
                _holdTail[c] = capSpr != null ? SdoExtracted.CleanCapCopy(capSpr) : null;
            }
            // lane click-flash strips (notes_board_click1..4.png) live in NOTEIMAGE root, not the skin folder
            var boardDir = Path.Combine(SdoExtracted.Root, "NOTEIMAGE");
            for (int c = 0; c < Keys; c++) _clickFlashSpr[c] = SdoExtracted.LoadImage(boardDir, "notes_board_click" + (c + 1) + ".png");
            _judgeSprites[0] = SdoExtracted.Eft("PERFECT.PNG");
            _judgeSprites[1] = SdoExtracted.Eft("COOL.PNG");
            _judgeSprites[2] = SdoExtracted.Eft("BAD.PNG");
            _judgeSprites[3] = SdoExtracted.Eft("MISS.PNG");
            for (int i = 0; i < 10; i++) _comboDigitSprites[i] = SdoExtracted.Eft("0" + i + ".PNG");
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
            // Camera-only intro: hold the note board hidden while the crane flies in (measured from scene start so the
            // camera always gets its full lead, even if the audio loaded slowly), then reveal the track. The board +
            // receptors appear together with the READY text — decompiled state 3->4 (NoteBoard_Update / StartPlayback).
            if (_introStartRt >= 0f)
            {
                while (Time.realtimeSinceStartup - _introStartRt < openingIntroSec) yield return null;
                SetTrackVisible(true);
            }
            if (_readyGo != null)
            {
                float t0 = Time.realtimeSinceStartup;
                PlaySe("VOICE_0003");                                   // "ready ... go!" (~2.56s)
                _readyGo.enabled = true;
                yield return PlayFrames(_readyFrames, 1.0f, 360f);      // READY: 10 frames @ 100ms/frame (decompiled StartReadyAnim param=100)
                while (Time.realtimeSinceStartup - t0 < 2.0f) yield return null;  // HOLD on READY — wait for the voice's "go" cue
                // GO frames: 01-03 = "GO!" appearing (G->Go->GO), 04-06 = it blurs/fades out. So play the
                // appear half, HOLD the sharp full "GO!", then play the disappear half — not all 6 straight.
                int half = Mathf.Max(1, _goFrames.Length / 2);
                yield return PlayFrameRange(_goFrames, 0, half, 0.1f, 300f);        // appear
                float h0 = Time.realtimeSinceStartup; while (Time.realtimeSinceStartup - h0 < 0.5f) yield return null;  // hold "GO!"
                yield return PlayFrameRange(_goFrames, half, _goFrames.Length, 0.1f, 300f);  // disappear
                _readyGo.enabled = false;
            }
            // GO is done -> start the song and the gameplay clock together. Both use the same StartLeadSec offset on
            // their own time base (dspTime / timeAsDouble) so the audio and the chart stay aligned, as before. Runs even
            // if the READY/GO overlay was missing, so the song never fails to start.
            _songStartDspTime = AudioSettings.dspTime + StartLeadSec;
            if (_audio != null && _audio.clip != null) _audio.PlayScheduled(_songStartDspTime);
            _clockStart = Time.timeAsDouble + StartLeadSec;
        }

        private IEnumerator PlayFrames(Sprite[] frames, float dur, float widthPx)
        {
            if (frames == null || frames.Length == 0) { yield return new WaitForSecondsRealtime(dur); yield break; }
            float t = 0;
            while (t < dur)
            {
                int fi = Mathf.Clamp((int)(t / dur * frames.Length), 0, frames.Length - 1);
                _readyGo.sprite = frames[fi];
                PlaceAspect(_readyGo, 400f, 300f, widthPx, -5f);   // centre of screen, over the avatar
                t += Time.deltaTime; yield return null;
            }
        }

        // play frames[from..to) holding each for secPerFrame (decompiled 100ms/frame)
        private IEnumerator PlayFrameRange(Sprite[] frames, int from, int to, float secPerFrame, float widthPx)
        {
            if (frames == null || frames.Length == 0) yield break;
            for (int i = from; i < to && i < frames.Length; i++)
            {
                _readyGo.sprite = frames[i];
                PlaceAspect(_readyGo, 400f, 300f, widthPx, -5f);
                float t = 0; while (t < secPerFrame) { t += Time.deltaTime; yield return null; }
            }
        }

        // ---------- build ----------

        private SpriteRenderer NewSR(string name, Sprite spr, int order)
        {
            var sr = new GameObject(name).AddComponent<SpriteRenderer>();
            sr.sprite = spr; sr.sortingOrder = order; return sr;
        }

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
            for (int c = 0; c < Keys; c++)
            {
                _clickFlashStart[c] = -1f;
                if (_clickFlashSpr[c] == null) continue;
                var fsr = NewSR("ClickFlash" + c, _clickFlashSpr[c], -20);
                fsr.color = new Color(1f, 1f, 1f, 0f); fsr.enabled = false;
                SdoLayout.PlaceTopLeft(fsr, LaneLeftX[c] + 1f, ClickStripTopY, 9f);
                _clickFlashSr[c] = fsr;
            }
            BuildNoteClip();
        }

        // a SpriteMask spanning the board's play band [NotesClipTop, NotesClipBottom]; note head/tail are flagged
        // VisibleInsideMask (SpawnNotes) so they're clipped to it — never drawn over the HP bar or below the board.
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
            if (_hpBg) _hpBg.enabled = on;
            if (_hpTex) _hpTex.enabled = on;
            if (_hpBackFrame) _hpBackFrame.enabled = on;
            if (_hpGlow) _hpGlow.enabled = on;           // UpdateHpBar refines this (low HP -> off) once visible again
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
            mr.sharedMaterial = new Material(Shader.Find("Sprites/Default")) { mainTexture = _holdTex[col] };
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
                        tail.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;
                        tail.sharedMaterial = new Material(Shader.Find("Sprites/Default"));   // own material -> no mask batch cross-bleed
                    }
                }
                if (body) body.SetActive(false);   // hide body+tail too until scrolled in (else they pile at screen centre)
                if (tail) tail.enabled = false;
                _notes.Add(new RuntimeNote(h, head, body, tail));
            }
        }

        private void BuildHud()
        {
            // HP bar (WinMyHp), official textures only, XML draw order (back->front):
            // bloodBG2 bg, MyHp fill (clipped to HP%), FullHp overlay, MyHpBack frame (black-keyed,
            // so its black centre is transparent and the fill shows through), HpEft glow at the edge.
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
            _comboWord = NewSR("ComboWord", SdoExtracted.Eft("COMBO.PNG"), 40); _comboWord.enabled = false;

            // bottom song info — official label graphics + value text (DdrGamePlay.xml positions)
            _lblSong = NewSR("LblSong", SdoExtracted.Hud("GamePlay1.an"), 30); SdoLayout.PlaceTopLeft(_lblSong, 11, 575);   // "歌曲名:"
            _lblAttr = NewSR("LblAttr", SdoExtracted.Hud("GamePlay2.an"), 30); SdoLayout.PlaceTopLeft(_lblAttr, 204, 575);   // "LV: 时间:"
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
            BuildRankingUi();
            UpdateHpBar();
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
            Debug.Log($"[avatar] WOMAN: {parts} parts, skeleton={(hrc != null ? hrc.Names.Length + " bones" : "none")}, mot={(mot != null ? mot.MaxTime + 1 + " frames" : "none")}");
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

        // Resolve the auto-director shot list exactly as the decompiled selector (021_gameplay ~0x4768a9):
        // switch(mapId) gated by playerCount<=3 / 4..6 / >6, then degrade to the generic numeric lists, then 1.CDT.
        private string SelectCdtPath()
        {
            int map = SceneMapId();
            int n = Mathf.Max(1, playerCount);
            string name = null;
            if (n <= 3)
            {
                if (map >= 3 && map <= 18) name = SoloCdt[map - 3];
                if (name == null) name = (n == 1) ? "1" : "3";        // fallback (decomp push 6=1.cdt / push 1=3.cdt)
            }
            else if (n <= 6)
            {
                if (map >= 3 && map <= 18) name = GroupCdt[map - 3];
                if (name == null) name = "6";
            }
            else name = "6";
            foreach (var c in new[] { name, n == 1 ? "1" : (n <= 3 ? "3" : "6"), "1" })
            {
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
        private struct MapobjInstance { public Vector3 Pos; public float Scale; }

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

        // Build one mapobj group ONCE, then place it at every instance transform. The MSH is parsed a single time
        // and the skinned meshes are SHARED across instances: a STATIC prop (no .mot) is skinned to its bind pose
        // once and then frozen (its SdoAvatar disables itself — zero per-frame work); an ANIMATED prop is driven by
        // ONE SdoAvatar (instance 0) whose looping .mot updates the shared meshes, and the other instances simply
        // render those same meshes at their own transform. So N copies cost 1 parse + (1 or 0) skin/frame + N draws,
        // not N×everything — this is what keeps the dense scenes cheap (box ×256, deng ×72, the room/saloon prop
        // walls). Lockstep copies look identical to the original (every instance plays the same clip in phase).
        // Materials/textures are read-only, so one set per submesh is shared too. Stage layer, native SDO coords.
        private void AddMapobj(string relDir, string mshFile, string hrcFile, string motFile, MapobjInstance[] instances)
        {
            if (instances == null || instances.Length == 0) return;
            var dir = Path.Combine(SdoExtracted.Root, relDir.Replace('/', Path.DirectorySeparatorChar));
            var mshPath = Path.Combine(dir, mshFile);
            if (!File.Exists(mshPath)) { Debug.LogWarning("[mapobj] missing " + mshPath); return; }
            string baseName = Path.GetFileNameWithoutExtension(mshFile);   // GameObject-name / log label
            var r = MshLoader.Load(File.ReadAllBytes(mshPath));            // parse ONCE; every instance shares these meshes
            if (r == null || r.Submeshes.Count == 0) { Debug.LogWarning("[mapobj] parse fail " + baseName); return; }
            HrcLoader hrc = LoadAsset(relDir + "/" + hrcFile, b => HrcLoader.Load(b));
            // motFile may be null (static prop — e.g. SCN0010 house): skinned to the bind pose once, then frozen.
            MotLoader mot = string.IsNullOrEmpty(motFile) ? null : LoadAsset(relDir + "/" + motFile, b => MotLoader.Load(b));
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
            // capable (Sdo/UnlitInstanced) so a group's copies batch into instanced draws on the GPU.
            var subMats = new List<Material[]>(r.Submeshes.Count);
            foreach (var sub in r.Submeshes)
            {
                Material[] mats;
                // per-submesh material (cloth/skin split like the avatar): multi-range submesh -> one material per range
                if (sub.Ranges != null && sub.Ranges.Count > 1 && sub.Mesh.subMeshCount == sub.Ranges.Count)
                {
                    mats = new Material[sub.Ranges.Count];
                    for (int s = 0; s < sub.Ranges.Count; s++)
                    {
                        int a = sub.Ranges[s].Attrib;
                        string nm = (sub.DdsNames != null && a >= 0 && a < sub.DdsNames.Length && !string.IsNullOrEmpty(sub.DdsNames[a])) ? sub.DdsNames[a] : sub.Dds;
                        mats[s] = NewMapobjMat(ResolveDds(dir, nm), fallbackCol);
                    }
                }
                else
                {
                    mats = new[] { NewMapobjMat(ResolveDds(dir, sub.Dds), fallbackCol) };
                }
                subMats.Add(mats);
            }

            // Animated texture overlay (faithful to the original's UIPicMap frame-swap): a few static props — the FIFA
            // crowd (renqun) and spotlights (shanguang) — are textured by a per-frame DDS sequence cycled every 300 ms,
            // NOT by their MSH material. Drive the shared submesh materials through that sequence. The geometry stays
            // frozen; only the bound texture changes. Critical for SCN0013 night, whose crowd frames are renamed on
            // disk (fifanight_renqun001..009.dds) and so are unreachable by the MSH-material path (rendered white).
            var texAnim = SceneMapobjTexAnimCatalog.Find(SceneFolder(), baseName);
            if (texAnim != null)
            {
                var frames = new List<Texture2D>(texAnim.Frames.Length);
                foreach (var fn in texAnim.Frames) { var t = ResolveDds(dir, fn); if (t != null) frames.Add(t); }
                if (frames.Count > 0)
                {
                    var animMats = new List<Material>();
                    foreach (var ms in subMats) if (ms != null) foreach (var m in ms) if (m != null) animMats.Add(m);
                    // The MSH material is a placeholder (often unresolved -> NewMapobjMat tinted it the fallback beige
                    // with no texture). Reset _Color to white so the swapped frame shows true-colour, not tinted.
                    foreach (var m in animMats) m.color = Color.white;
                    // Transparent props (FIFA crowd / spotlights) are alpha-cutout sprites — the opaque mapobj shader
                    // paints their transparent regions solid (stands read empty/black). Switch those to the two-sided
                    // alpha-blended overlay so only the sprite shows. Opaque props (the sea video wall) keep their
                    // material. Same Material instances the renderers use, so this applies to the rendered mesh too.
                    if (texAnim.Transparent)
                    {
                        var overlay = Shader.Find("Sdo/UnlitOverlay");
                        if (overlay != null) foreach (var m in animMats) m.shader = overlay;
                    }
                    var holder = new GameObject(baseName + "_texanim");   // root: torn down with the play screen
                    holder.AddComponent<MapobjTexAnimator>().Init(animMats.ToArray(), frames.ToArray(), texAnim.IntervalMs);
                    Debug.Log($"[mapobj] {baseName}: texture-anim {frames.Count}/{texAnim.Frames.Length} frames @ {texAnim.IntervalMs}ms, transparent={texAnim.Transparent}");
                }
                else Debug.LogWarning($"[mapobj] {baseName}: texture-anim found no frames in {dir}");
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
                    parent.transform.localScale = Vector3.one * instances[idx].Scale;
                    var avatar = parent.AddComponent<SdoAvatar>();
                    avatar.Setup(hrc, mot);                                   // drives the bone FK from the .mot (no parts -> no skin)
                    // each submesh rides its own leaf bone (trophy: ball on Sphere01, cup on Cylinder01) so the .mot
                    // spins/animates every part; the verts stay in bone-local space (NOT baked).
                    for (int s = 0; s < r.Submeshes.Count; s++)
                    {
                        int bone = leafBones[System.Math.Min(s, leafBones.Length - 1)];
                        var follow = new GameObject($"{baseName}_follow{s}");
                        follow.transform.SetParent(parent.transform, false);
                        AddMapobjMeshChild(follow.transform, baseName + "_mesh", r.Submeshes[s].Mesh, subMats[s]);
                        avatar.AddBoneFollower(bone, follow.transform);
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
                    parent.transform.localScale = Vector3.one * instances[idx].Scale;
                    var avatar = parent.AddComponent<SdoAvatar>();
                    avatar.GpuSkinning = true;
                    avatar.Setup(hrc, mot);
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
                parent.transform.localScale = Vector3.one * instances[idx].Scale;
                if (idx == 0)
                {
                    // driver: owns the skinned meshes (+ the SdoAvatar that animates them, null DPS -> auto-loops .mot)
                    SdoAvatar avatar = hrc != null ? parent.AddComponent<SdoAvatar>() : null;
                    if (avatar != null) avatar.Setup(hrc, mot);
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

        // One GPU-instancing-capable unlit material for a mapobj submesh (Cull Back, texture × tint), so a group's
        // shared-mesh copies batch into instanced GPU draws. Falls back to the built-in Unlit shaders if the custom
        // one isn't present (then no instancing, but identical look). tex==null -> flat fallback colour.
        private static Material NewMapobjMat(Texture2D tex, Color fallbackCol)
        {
            var inst = Shader.Find("Sdo/UnlitInstanced");
            if (inst != null)
            {
                var m = new Material(inst) { enableInstancing = true };
                if (tex != null) m.mainTexture = tex; else m.color = fallbackCol;   // _MainTex defaults to white -> tint shows
                return m;
            }
            return tex != null ? new Material(Shader.Find("Unlit/Texture")) { mainTexture = tex }
                               : new Material(Shader.Find("Unlit/Color")) { color = fallbackCol };
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
                b = res.Mesh.bounds;
                // render at NATIVE SDO world coords (no lift). The .cv cameras + the avatar dance spot (_avatarChest)
                // are authored in this same space with the dancer standing on the native floor, so they line up.
                Debug.Log($"[scene] SCN0009: {res.Materials.Length} subsets, bounds c={b.center} s={b.size}");
                TryLoadMapobjs();   // stage props on the same layer (SCN0009 -> GUATAN x4)
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
            cam.nearClipPlane = 1f; cam.farClipPlane = Mathf.Max(4000f, b.size.magnitude * 4f);
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
        private static Sprite[] LoadEmojiSeq(string prefix, int count)
        {
            var arr = new List<Sprite>(count);
            for (int i = 0; i < count; i++)
            {
                var s = SdoExtracted.LoadImage(PlayingExpDir, $"{prefix}{i:D3}.PNG");
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
            _emGTH = LoadEmojiSeq("GTH", 8);     // low HP (<30% bar)
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

        // resolve a material's .dds name to a file in the avatar dir (case-insensitive), load it
        private Texture2D ResolveDds(string dir, string ddsName)
        {
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
            try { return DdsLoader.Load(File.ReadAllBytes(hit)); } catch { return null; }
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
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f), 0.1f);   // smoothed debug FPS
            if (_fpsText) _fpsText.text = "FPS " + Mathf.RoundToInt(_fps);
            if (Input.GetKeyDown(KeyCode.F4)) _showDebugUI = !_showDebugUI;        // toggle the tuning sliders
            if (Input.GetKeyDown(KeyCode.B)) SpawnComboBurst(0);   // DEBUG B: fire the 100COMBO floor ring burst on demand
            // BURST OBSERVE controls: 1-5 fire 100..500COMBO, 0 fires FINISHED; [ / ] slow/speed time, \ pause, = reset.
            if (Input.GetKeyDown(KeyCode.Alpha1)) SpawnComboBurst(0);
            if (Input.GetKeyDown(KeyCode.Alpha2)) SpawnComboBurst(1);
            if (Input.GetKeyDown(KeyCode.Alpha3)) SpawnComboBurst(2);
            if (Input.GetKeyDown(KeyCode.Alpha4)) SpawnComboBurst(3);
            if (Input.GetKeyDown(KeyCode.Alpha5)) SpawnComboBurst(4);
            if (Input.GetKeyDown(KeyCode.Alpha0)) SpawnNamedEft("FINISHED", 5f);
            if (Input.GetKeyDown(KeyCode.F5) && _started && !_ended) { _ended = true; EnterResult(); }   // DEBUG F5: cut the song short → jump to the result sequence
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
            _clock.SetAudioSeconds(now / 1000.0);
            if (_ended) { ResultTick(); UpdateFx(); return; }   // post-song: finish sequence drives avatar/camera/panel; gameplay frozen (FX still tick out)
            ScrollNotes(now);
            if (!_failed) { if (autoPlay) AutoPlay(now); else { HandleInput(now); AutoMiss(now); } }
            UpdateDanceGate(now);   // dancer dance/stop decision (after judging, so this frame's misses count)
            RecordGate(now);        // log gate transitions for the result-screen background replay
            // long note held -> continuous burst that loops ONE full animation at a time (gated). Only this
            // hold case waits for the round to finish; taps fire freely above.
            for (int lane = 0; lane < Keys; lane++)
                if (_holding[lane] != null && _burstFrames != null && _holdBurst[lane] == null) _holdBurst[lane] = SpawnBurst(lane, true);
            UpdateClickFlash();
            UpdateFx(); UpdateHud();
            if (_health != null && _health.IsFailed) _failed = true;
            if (!_ended && (_failed || now > _totalMs + 2000)) { _ended = true; EnterResult(); }
        }

        // Song finished (or HP-out): freeze gameplay, hide the note board, play the win/lose 定格 pose on the
        // winning dancer, and fire the FINISHED burst on the winner. Mirrors decompiled FinishSequenceTick phase4
        // (021_gameplay:2674) — the winner (top score) plays cat5, everyone else cat4.
        private void EnterResult()
        {
            if (_audio) _audio.Stop();                        // stop the song (natural end already silent; matters for F5 mid-song cut)
            RebuildRoster();                                  // finalize scores so the rank/winner is current
            var (rank, _) = RankingBoard.LocalRank(_roster);
            _localWon = rank <= 1;                            // rank 1 = highest score = winner
            _gameOver = _failed;                              // HP-out → GAME OVER (overrides win/lose banner)
            // STAGE 1 (win/lose pose): clear ONLY the note board (+HP/receptors) and its combo/judgment words.
            // The top score, centre rank and right-side roster STAY visible until the result panel appears.
            SetTrackVisible(false);                           // note board + HP + receptors + click strips
            // SetTrackVisible(false) also hid the ranking — but it must STAY up through the win/lose pose (final
            // standings). Re-show it here with the final order; only HideHudForPanel (result panel) hides it.
            if (_rosterName != null) { UpdateRosterList(); UpdateRankDisplay(); SetRankingVisible(true); }
            HideComboAndJudge();                              // combo number + judgment word (part of the play board)
            ClearGameplayFx();                                // tear down in-flight bursts/holds (F5 mid-song leaves a hold burst looping)
            if (_emoji != null) _emoji.Stop();                // clear any head emoji cut-in so it doesn't linger into the result
            foreach (var n in _notes)                         // also kill any note sprites still in flight
            { if (n.Head) n.Head.enabled = false; if (n.Body) n.Body.SetActive(false); if (n.Tail) n.Tail.enabled = false; }
            if (_avatar != null)                              // win/lose 定格 pose (cat5/cat4), held on its last frame
            {
                var mot = ResolveMot(_localWon ? winMot : loseMot);
                if (mot != null) _avatar.PlayOneShot(mot, true);
            }
            // FINISHED is a combo-style burst attached to the WINNER's dancer (follows _ringTr). The remake renders
            // only the local avatar, so it shows when the local player is the winner; otherwise no rendered dancer.
            if (_localWon) SpawnNamedEft("FINISHED", 5f);
            if (enableResultSfx) PlaySe(_localWon ? "SE_0014" : "SE_0015");   // win/lose jingle (off until clips verified)
            _resultPhase = ResultPhase.FinishPose; _resultPhaseStart = Time.time;
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
            HideHudForPanel();   // stage 2: now hide the score / rank / roster / song-info / nameplate
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
            _result.Show(_songTitle, diff, rows, _localWon, expGained, coinsGained, head, _gameOver, PlaySe);
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
                    r = new ResultScreen.Row { Perfect = P, Cool = C, Bad = B, Miss = M, MaxCombo = _score.MaxCombo, Accuracy = (P + C) * 100.0 / judged, Score = _score.Score };
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

        // UPSCROLL (matches the official screen): future notes are below the hit line and RISE to it.
        private float YForTime(double noteMs, double now) => judgeLineY + (float)((noteMs - now) / 1000.0) * scrollPxPerSec;

        private void ScrollNotes(double now)
        {
            foreach (var n in _notes)
            {
                if (n.Done) { n.Head.enabled = false; if (n.Body) n.Body.SetActive(false); if (n.Tail) n.Tail.enabled = false; continue; }
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
                    n.Head.enabled = false; if (n.Body) n.Body.SetActive(false); if (n.Tail) n.Tail.enabled = false;
                    if (n.HeadJudged) n.Done = true;   // hit late / auto-missed -> now fully retired
                    continue;
                }
                bool visible = held || Mathf.Min(yRaw, yEnd) <= NotesClipBottom + 60f;   // shown once it enters from the bottom; SpriteMask clips it to the board
                n.Head.enabled = visible;
                if (!visible) { if (n.Body) n.Body.SetActive(false); if (n.Tail) n.Tail.enabled = false; continue; }
                float y = yRaw;   // NO clamp — notes keep flowing past the receptor (the mask hides them above the HP bar)
                if (_noteFrames[c] != null) n.Head.sprite = _noteFrames[c][((int)(Time.time * noteAnimFps)) & 3];
                PlaceAspect(n.Head, LaneLeftX[c] + LaneCx0, y, LaneW, 1f);

                if (n.Note.EndTimeMs.HasValue)
                {
                    if (n.Tail) { n.Tail.enabled = true; PlaceAspect(n.Tail, LaneLeftX[c] + 34.5f, yEnd, LaneW, 0.5f); }
                    if (n.Body)
                    {
                        float cx = LaneLeftX[c] + 34.5f;
                        float top = Mathf.Max(Mathf.Min(y, yEnd), NotesClipTop);     // clamp body to the board
                        float bot = Mathf.Min(Mathf.Max(y, yEnd), NotesClipBottom);
                        float len = Mathf.Max(0f, bot - top), midY = (top + bot) / 2f;
                        n.Body.SetActive(len > 0.5f);
                        if (len > 0.5f)
                        {
                            n.Body.transform.position = SdoLayout.ToWorld(cx, midY, 0.6f);
                            n.Body.transform.localScale = new Vector3(LaneW, len, 1);
                            // tile the body texture along the length (拼接, not stretch)
                            float tileH = LaneW * (_holdTex[c].height / (float)_holdTex[c].width);
                            float tiles = len / Mathf.Max(tileH, 1e-3f);
                            var m = n.Body.GetComponent<MeshFilter>().mesh; var uv = m.uv; uv[2].y = tiles; uv[3].y = tiles; m.uv = uv;
                        }
                    }
                }
            }
        }

        // ---------- input / judge ----------

        private void HandleInput(double now)
        {
            int mask = 0;
            for (int lane = 0; lane < Keys; lane++)
            {
                bool down = false, anyHeld = false, anyUp = false;
                foreach (var k in LaneKeys[lane])
                { if (Input.GetKeyDown(k)) down = true; if (Input.GetKey(k)) anyHeld = true; if (Input.GetKeyUp(k)) anyUp = true; }
                if (anyHeld) mask |= 1 << lane;
                if (down) { PressLane(lane, now); _recDownStart[lane] = Time.time; }   // any press fires the one-shot keydown burst
                if (anyUp && !anyHeld) ReleaseLane(lane, now);   // released only when no set key is still held
            }
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

        private void AutoPlay(double now)
        {
            // auto-play applies the F4 "Force hit grade" if one is selected, else Perfect — so picking Cool/Bad/Miss
            // in the panel immediately drives what auto-play hits with. A Miss isn't "held"/removed: it flows off.
            Judgment grade = forcedJudge >= 0 ? (Judgment)forcedJudge : Judgment.Perfect;
            foreach (var n in _notes)
            {
                if (n.Done) continue;
                if (!n.HeadJudged && now >= n.Note.StartTimeMs)
                {
                    n.HeadJudged = true; ApplyEvent(grade, n.Note.Lane);
                    _recDownStart[n.Note.Lane] = Time.time;   // auto-press: fire the keydown burst (head only, never the hold tail)
                    if (grade == Judgment.Miss) { /* flows past the receptor, then ScrollNotes removes it */ }
                    else if (n.Note.IsHold) _holding[n.Note.Lane] = n;
                    else n.Done = true;
                }
                if (n.HeadJudged && !n.Done && grade != Judgment.Miss && n.Note.IsHold && _holding[n.Note.Lane] == n
                    && n.Note.EndTimeMs.HasValue && now >= n.Note.EndTimeMs.Value)
                { _holding[n.Note.Lane] = null; ApplyEvent(grade, n.Note.Lane); n.Done = true; }
            }
        }

        private void PressLane(int lane, double now)
        {
            var n = NearestHittable(lane, now); if (n == null) return;
            Judgment jv;
            if (forcedJudge >= 0) jv = (Judgment)forcedJudge;                         // debug: force a grade on the hit
            else { var j = _engine.JudgeHit(n.Note.StartTimeMs, now); if (j == null) return; jv = j.Value; }
            n.HeadJudged = true; ApplyEvent(jv, lane);
            if (jv == Judgment.Miss) { /* keep flowing past the receptor; ScrollNotes removes it off the top */ }
            else if (n.Note.IsHold) { if (jv == Judgment.Bad) n.BundledFail = true; else _holding[lane] = n; }
            else n.Done = true;
        }

        private void ReleaseLane(int lane, double now)
        {
            var n = _holding[lane]; if (n == null) return;
            _holding[lane] = null;
            ApplyEvent(_engine.JudgeHoldTail(n.Note.EndTimeMs ?? n.Note.StartTimeMs, now) ?? Judgment.Miss, lane); n.Done = true;
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
                if (_holding[n.Note.Lane] == n && n.Note.EndTimeMs.HasValue && now >= n.Note.EndTimeMs.Value) { _holding[n.Note.Lane] = null; ApplyEvent(Judgment.Perfect); n.Done = true; }
            }
        }

        private void ApplyEvent(Judgment j, int lane = -1)
        {
            _score.Apply(j); _health.Apply(j);
            UpdateEmojiOnJudge(j);                                                // combo-milestone / consecutive-miss emoji cut-ins
            _blockHadNote = true;                                                // a note was judged this block (-> not an empty block)
            if (j == Judgment.Bad || j == Judgment.Miss) _blockHadBreak = true;   // break -> NOT stopped now; the dancer is re-decided at the next 8-beat settlement
            _judgeWord.sprite = _judgeSprites[(int)j]; _judgeWordAt = Time.time;
            if (lane >= 0 && _burstFrames != null && (j == Judgment.Perfect || j == Judgment.Cool)) SpawnBurst(lane, false);  // tap: fire immediately, may overlap
            if (lane >= 0 && j != Judgment.Miss) TriggerClickFlash(lane);   // light the struck lane's click strip (any contact, not a miss)
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

        private const float BurstWidth = 235f;            // hit-burst draw size (covers receptor + glow, well beyond the lane)
        // a TAP burst fires on every hit and may overlap others on the same lane (no gating). A HOLD burst loops,
        // one full round at a time (gated). Each burst gets its OWN material clone so overlapping bursts never bleed.
        private BurstFx SpawnBurst(int lane, bool isHold)
        {
            var mat = _matPool.Count > 0 ? _matPool.Pop() : (_addMat != null ? new Material(_addMat) : null);  // own instance, pooled
            // brightness: the additive shader is Blend SrcAlpha One, and its _TintColor defaults to (.5,.5,.5,.5) ->
            // the .5 alpha halves the burst (too dark). Drive _TintColor by burstBright (1.0 = stock, higher = brighter).
            if (mat != null) { float t = 0.5f * burstBright; mat.SetColor("_TintColor", new Color(t, t, t, Mathf.Clamp01(t))); }
            var sr = NewSR("Burst", _burstFrames[0], 6);
            if (mat != null) sr.sharedMaterial = mat;                   // additive -> black bg becomes transparent glow
            PlaceAspect(sr, LaneLeftX[lane] + LaneCx0, judgeLineY, BurstWidth * burstSize);
            var sr2 = NewSR("Burst+", _burstFrames[0], 6);             // 2nd additive layer -> vivid in-game glow
            if (mat != null) sr2.sharedMaterial = mat;
            sr2.transform.SetParent(sr.transform, false);
            var fx = new BurstFx { Sr = sr, Sr2 = sr2, Mat = mat, Lane = lane, Start = Time.time, IsHold = isHold };
            _fx.Add(fx);
            return fx;
        }

        // in-game debug tuning sliders (F4 toggles). Board alpha applies live; burst size/brightness apply to the
        // next bursts (taps fire continuously, so the effect shows within ~0.3s).
        private void OnGUI()
        {
            if (!_showDebugUI) return;
            float h = Mathf.Min(560f, Screen.height - 16f);
            GUILayout.BeginArea(new Rect(Screen.width - 280, 8, 270, h), GUI.skin.box);

            // --- TABS: pinned header (F4-hide + tab bar) is tiny; ALL controls live inside the scroll view per tab, so
            // each group (Play / Combo / Stage) gets the full panel height instead of fighting one growing slider list. ---
            GUILayout.Label("[F4 hide]   Debug");
            _dbgTab = GUILayout.Toolbar(_dbgTab, DbgTabs);

            // slow-mo time control is mode-level (observation) — keep it reachable on every tab while observing.
            if (observeBurstMode)
            {
                bool paused = Time.timeScale <= 0f;
                GUILayout.Label("== OBSERVE ==  cam0, no dance/notes/music");
                GUILayout.Label($"Time: {(paused ? "PAUSED" : _timeScale.ToString("0.00") + "×")}   [ ] slow/fast, \\ pause, = reset");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("0.1×")) SetTimeScale(0.1f);
                if (GUILayout.Button("0.25×")) SetTimeScale(0.25f);
                if (GUILayout.Button("0.5×")) SetTimeScale(0.5f);
                if (GUILayout.Button("1×")) SetTimeScale(1f);
                if (GUILayout.Button(paused ? "▶" : "❚❚")) { if (paused) SetTimeScale(_timeScale); else Time.timeScale = 0f; }
                GUILayout.EndHorizontal();
                GUILayout.Space(4);
            }

            _dbgScroll = GUILayout.BeginScrollView(_dbgScroll);
            if (_dbgTab == 0)        // ===== PLAY: playtest + body shape =====
            {
                autoPlay = GUILayout.Toggle(autoPlay, autoPlay ? " Auto-play: ON" : " Auto-play: OFF — manual");
                GUILayout.Label("Manual keys  L/D/U/R = A S W D  or  Num 4 5 8 6");
                GUILayout.Label($"Force hit grade: {(forcedJudge < 0 ? "Real (timing)" : ForceJudgeLabels[forcedJudge + 1])}");
                forcedJudge = GUILayout.Toolbar(forcedJudge + 1, ForceJudgeLabels) - 1;   // 0=Real(-1), 1..4=Perfect..Miss
                GUILayout.Space(6);
                // 體型 (fat/thin): preset buttons (faithful SDO body indices) + a fine B slider — re-shape the dancer LIVE.
                GUILayout.Label($"Body shape (thin..fat): B={_bodyShapeB:F3}  (1.00 = standard)");
                GUILayout.BeginHorizontal();
                for (int i = 0; i < BodyShapeLabels.Length; i++)
                    if (GUILayout.Button(BodyShapeLabels[i]))
                    { _bodyShapeB = SdoBodyShape.WeightFromIndex(i, maleBody); if (_avatar) _avatar.SetBodyShape(_bodyShapeB); }
                GUILayout.EndHorizontal();
                float newB = GUILayout.HorizontalSlider(_bodyShapeB, 0.7f, 1.4f);   // fine override (continuous)
                if (Mathf.Abs(newB - _bodyShapeB) > 1e-4f) { _bodyShapeB = newB; if (_avatar) _avatar.SetBodyShape(_bodyShapeB); }
            }
            else if (_dbgTab == 1)   // ===== COMBO: fire bursts + combo/mesh/trail tuning =====
            {
                // fire a specific combo burst on demand (tier 0..4 = 100..500COMBO).
                GUILayout.BeginHorizontal();
                GUILayout.Label("Fire combo:", GUILayout.Width(66));
                for (int t = 0; t < 5; t++) if (GUILayout.Button(((t + 1) * 100).ToString())) SpawnComboBurst(t);
                GUILayout.EndHorizontal();
                if (GUILayout.Button("Fire FINISHED (result firework)")) SpawnNamedEft("FINISHED", 5f);

                // ── 300 AEF_3_00 (blue flame) — pinned FIRST so it's the easiest group to find/tune ──
                GUILayout.Space(6);
                GUILayout.Label("══ 300 AEF_3_00 藍焰 ══");
                GUILayout.Label($"300 AEF_3_00 透明度 opacity: {EftEffect.Mesh300Alpha:F2} (低=透明/淡)");
                EftEffect.Mesh300Alpha = GUILayout.HorizontalSlider(EftEffect.Mesh300Alpha, 0.05f, 1f);
                GUILayout.Label($"300 AEF_3_00 亮度 intensity: {EftEffect.Mesh300Intensity:F1}× (1=raw/drowned)");
                EftEffect.Mesh300Intensity = GUILayout.HorizontalSlider(EftEffect.Mesh300Intensity, 1f, 8f);
                GUILayout.Label($"300 AEF_3_00 前後 Z: {EftEffect.Mesh300Z:F2} (− 往後不擋球 / + 往前)");
                EftEffect.Mesh300Z = GUILayout.HorizontalSlider(EftEffect.Mesh300Z, -2f, 2f);
                EftEffect.Mesh300Straight = GUILayout.Toggle(EftEffect.Mesh300Straight, " 300 AEF_3_00 拉直 straighten (de-lean)");
                GUILayout.Label($"300 AEF_3_00 寬度 width: {EftEffect.MeshWidthMatch:F2}× (vs ball)");
                EftEffect.MeshWidthMatch = GUILayout.HorizontalSlider(EftEffect.MeshWidthMatch, 0.1f, 1.2f);
                GUILayout.Label($"300 AEF_3_00 收縮 shrink: {EftEffect.MeshShrinkEnd:F2} end (低=更小更快)");
                EftEffect.MeshShrinkEnd = GUILayout.HorizontalSlider(EftEffect.MeshShrinkEnd, 0.05f, 1f);
                GUILayout.Label($"300 AEF_3_00 出生 W×{EftEffect.MeshStartW:F1} / H×{EftEffect.MeshStartH:F1} (→1 by end)");
                EftEffect.MeshStartW = GUILayout.HorizontalSlider(EftEffect.MeshStartW, 1f, 4f);
                EftEffect.MeshStartH = GUILayout.HorizontalSlider(EftEffect.MeshStartH, 1f, 8f);
                GUILayout.Label($"300 AEF_3_00 下降錨點 drop: {EftEffect.MeshDropFrac:F2} (0=貼球/跟上, 1=球底)");
                EftEffect.MeshDropFrac = GUILayout.HorizontalSlider(EftEffect.MeshDropFrac, 0f, 1.5f);

                // ── 200 AEF_3_00 (ground curtain) ──
                GUILayout.Space(6);
                GUILayout.Label("══ 200 AEF_3_00 地面 ══");
                GUILayout.Label($"200 AEF_3_00 亮度 intensity: {EftEffect.MeshIntensity:F1}× (1=raw/drowned)");
                EftEffect.MeshIntensity = GUILayout.HorizontalSlider(EftEffect.MeshIntensity, 1f, 8f);
                GUILayout.Label($"200 AEF_3_00 透明度 opacity: {EftEffect.MeshAlpha:F2}");
                EftEffect.MeshAlpha = GUILayout.HorizontalSlider(EftEffect.MeshAlpha, 0.2f, 1f);
                GUILayout.Label($"200 AEF_3_00 數量 count: {EftEffect.MeshMax200} (official ~5-6)");
                EftEffect.MeshMax200 = Mathf.RoundToInt(GUILayout.HorizontalSlider(EftEffect.MeshMax200, 1f, 15f));

                // ── burst / glow / exposure (200+300 common) ──
                GUILayout.Space(6);
                GUILayout.Label("══ Burst / glow ══");
                GUILayout.Label($"Combo-burst size: {comboBurstSize:F2}×  (press B to test)");
                comboBurstSize = GUILayout.HorizontalSlider(comboBurstSize, 0.3f, 3f);
                GUILayout.Label($"Combo-burst brightness: {comboBurstBright:F2}× (1.0=faithful)");
                comboBurstBright = GUILayout.HorizontalSlider(comboBurstBright, 0.2f, 2.5f);
                GUILayout.Label($"Outer-glow intensity: {comboGlow:F2}× (0=off/faithful)");
                comboGlow = GUILayout.HorizontalSlider(comboGlow, 0f, 3f);
                GUILayout.Label($"Outer-glow spread: {comboGlowSpread:F2}× bigger than particle");
                comboGlowSpread = GUILayout.HorizontalSlider(comboGlowSpread, 0f, 3f);
                GUILayout.Label($"Combo spawn exposure: {EftEffect.BallCoreIntensity:F1}× (200/300 white-hot at birth; 1=off)");
                EftEffect.BallCoreIntensity = GUILayout.HorizontalSlider(EftEffect.BallCoreIntensity, 1f, 10f);
                GUILayout.Label($"Combo exposure fade: {EftEffect.BallCoreExpoFrac:F2} of life (→real colour; lower=colour sooner)");
                EftEffect.BallCoreExpoFrac = GUILayout.HorizontalSlider(EftEffect.BallCoreExpoFrac, 0.05f, 0.8f);
                // combo TRAIL streaks (200/300's light flares = engine 0x20000 = a unit quad stretched by animScale.y, NOT a
                // swept band; length is the scaleY channel, so only the WIDTH is tunable here — 1× = faithful)
                GUILayout.Label($"Combo trail width: {EftEffect.TrailWidthMul:F2}×  (200/300 light streaks, 1=faithful)");
                EftEffect.TrailWidthMul = GUILayout.HorizontalSlider(EftEffect.TrailWidthMul, 0.2f, 3f);
            }
            else if (_dbgTab == 2)    // ===== STAGE: board / hit-burst / HP / floor-ring / hand-trail =====
            {
                GUILayout.Label($"Opening intro: {openingIntroSec:F1}s {(_trackVisible ? "(shown)" : "(holding — camera only)")}");
                openingIntroSec = GUILayout.HorizontalSlider(openingIntroSec, 0f, 15f);   // board+HP+READY appear after this; tunable live during the hold
                GUILayout.Label($"Board opacity: {boardAlpha:F2}× (1=native, ~1.4=official, ~2.6=opaque)");
                boardAlpha = GUILayout.HorizontalSlider(boardAlpha, 0f, 2.6f);
                GUILayout.Label($"Board X nudge: {boardX:F0}px");
                boardX = GUILayout.HorizontalSlider(boardX, -40f, 40f);
                GUILayout.Label($"Burst size: {burstSize:F2}×");
                burstSize = GUILayout.HorizontalSlider(burstSize, 0.3f, 3f);
                GUILayout.Label($"Burst brightness: {burstBright:F2}×");
                burstBright = GUILayout.HorizontalSlider(burstBright, 0.3f, 3f);
                GUILayout.Label($"Click-flash brightness: {clickFlashBright:F2}×");
                clickFlashBright = GUILayout.HorizontalSlider(clickFlashBright, 0f, 1.5f);
                GUILayout.Label($"Keydown burst: {recKeydownStepSec*1000f:F0}ms/frame ({recKeydownStepSec*5f*1000f:F0}ms total, 5 frames)");
                recKeydownStepSec = GUILayout.HorizontalSlider(recKeydownStepSec, 0.005f, 0.1f);
                GUILayout.Label($"HP-glow brightness: {hpGlowBright:F2}× (~1=old dim, 2.5=official)");
                hpGlowBright = GUILayout.HorizontalSlider(hpGlowBright, 0.3f, 5f);
                GUILayout.Label($"HP-glow X offset: {hpGlowOffsetX:F0}px (− = left toward bar)");
                hpGlowOffsetX = GUILayout.HorizontalSlider(hpGlowOffsetX, -48f, 8f);
                GUILayout.Label($"Floor-ring spread (radius): {ringOuterRadius:F0}");
                ringOuterRadius = GUILayout.HorizontalSlider(ringOuterRadius, 4f, 60f);
                GUILayout.Label($"Floor-ring brightness: {ringBrightness:F2}× (0=off)");
                ringBrightness = GUILayout.HorizontalSlider(ringBrightness, 0f, 2f);
                GUILayout.Label($"Floor-ring spin: {ringSpinDeg:F0}°/s");
                ringSpinDeg = GUILayout.HorizontalSlider(ringSpinDeg, -120f, 120f);
                GUILayout.Label($"Hand-trail width: {handTrailWidth:F2}×");
                handTrailWidth = GUILayout.HorizontalSlider(handTrailWidth, 0.1f, 3f);
                GUILayout.Label($"Hand-trail time: {handTrailTime:F2}s");
                handTrailTime = GUILayout.HorizontalSlider(handTrailTime, 0.05f, 1.2f);
                foreach (var rib in _handTrails) if (rib) { rib.widthMul = handTrailWidth; rib.life = handTrailTime; }
            }
            else if (_dbgTab == 3)    // ===== EMOJI: head marker + rank/roster + head-emoji test =====
            {
                GUILayout.Label("══ 頭頂名牌 Head marker（螢幕空間，對官方圖微調）══");
                if (_headMarker == null) GUILayout.Label("(name marker 尚未就緒：需載入舞者 avatar)");
                else
                {
                    GUILayout.Label($"名字字級 font(px): {_headMarker.nameFontPx:F0}");
                    _headMarker.nameFontPx = GUILayout.HorizontalSlider(_headMarker.nameFontPx, 8f, 64f);
                    GUILayout.Label($"箭頭寬度 arrow(px): {_headMarker.arrowDesignW:F0}");
                    _headMarker.arrowDesignW = GUILayout.HorizontalSlider(_headMarker.arrowDesignW, 6f, 64f);
                    GUILayout.Label($"離頭距離 up(世界): {_headMarker.upWorld:F1}");
                    _headMarker.upWorld = GUILayout.HorizontalSlider(_headMarker.upWorld, 0f, 50f);
                    GUILayout.Label($"箭頭/名字間距 gap(px): {_headMarker.arrowGapPx:F0}");
                    _headMarker.arrowGapPx = GUILayout.HorizontalSlider(_headMarker.arrowGapPx, 0f, 30f);
                    GUILayout.Label($"箭頭換幀 frame: {_headMarker.frameMs:F0}ms");
                    _headMarker.frameMs = GUILayout.HorizontalSlider(_headMarker.frameMs, 50f, 600f);
                }

                GUILayout.Space(8);
                GUILayout.Label("══ 排名/清單 Rank & roster（即時微調）══");
                GUILayout.Label($"清單字級 list font(px): {rosterFontWorld:F0}");
                rosterFontWorld = GUILayout.HorizontalSlider(rosterFontWorld, 10f, 48f);
                GUILayout.Label($"清單起始 firstY: {rosterFirstY:F0}");
                rosterFirstY = GUILayout.HorizontalSlider(rosterFirstY, 40f, 300f);
                GUILayout.Label($"清單列距 rowStep: {rosterRowStep:F0}");
                rosterRowStep = GUILayout.HorizontalSlider(rosterRowStep, 12f, 44f);
                GUILayout.Label($"名次數字寬 rankW(px): {rankDigitW:F0}  間距 pitch: {rankPitch:F0}");
                rankDigitW = GUILayout.HorizontalSlider(rankDigitW, 12f, 48f);
                rankPitch = GUILayout.HorizontalSlider(rankPitch, 14f, 48f);
                GUILayout.Label($"名次中心X / Y: {rankCenterX:F0} / {rankY:F0}");
                rankCenterX = GUILayout.HorizontalSlider(rankCenterX, 280f, 520f);
                rankY = GUILayout.HorizontalSlider(rankY, 36f, 130f);
                GUILayout.Label($"旁觀標題X/Y: {lookerTitleX:F0}/{lookerTitleY:F0}  字級:{lookerFontWorld:F0}");
                lookerTitleX = GUILayout.HorizontalSlider(lookerTitleX, 560f, 780f);
                lookerTitleY = GUILayout.HorizontalSlider(lookerTitleY, 150f, 320f);
                lookerFontWorld = GUILayout.HorizontalSlider(lookerFontWorld, 8f, 32f);
                GUILayout.Label($"旁觀名 X/起始Y/列距: {lookerX:F0}/{lookerFirstY:F0}/{lookerRowStep:F0}");
                lookerX = GUILayout.HorizontalSlider(lookerX, 560f, 780f);
                lookerFirstY = GUILayout.HorizontalSlider(lookerFirstY, 160f, 380f);
                lookerRowStep = GUILayout.HorizontalSlider(lookerRowStep, 10f, 30f);
                if (GUILayout.Button("套用清單版面 / re-layout list")) RelayoutRoster();

                GUILayout.Space(8);
                GUILayout.Label("══ 表情測試 Head emoji ══");
                if (_emoji == null) GUILayout.Label("(emoji 尚未就緒：需載入舞者 avatar)");
                else
                {
                    GUILayout.Label("點擊直接觸發（取當下人物位置後凍結）：");
                    for (int i = 0; i < EmojiTestButtons.Length; i += 2)
                    {
                        GUILayout.BeginHorizontal();
                        if (GUILayout.Button(EmojiTestButtons[i].label)) ShowEmoji(EmojiTestButtons[i].kind);
                        if (i + 1 < EmojiTestButtons.Length && GUILayout.Button(EmojiTestButtons[i + 1].label)) ShowEmoji(EmojiTestButtons[i + 1].kind);
                        GUILayout.EndHorizontal();
                    }
                    if (GUILayout.Button("Stop / 清除")) _emoji.Stop();

                    GUILayout.Space(6);
                    GUILayout.Label("位置 XYZ（槽位 world 座標 + 世界偏移）— 即時套用：");
                    GUILayout.Label($"X: {_emoji.xOff:F1}");
                    _emoji.xOff = GUILayout.HorizontalSlider(_emoji.xOff, -60f, 60f);
                    GUILayout.Label($"Y: {_emoji.yOff:F1}");
                    _emoji.yOff = GUILayout.HorizontalSlider(_emoji.yOff, -20f, 80f);
                    GUILayout.Label($"Z: {_emoji.zOff:F1}");
                    _emoji.zOff = GUILayout.HorizontalSlider(_emoji.zOff, -60f, 60f);

                    GUILayout.Space(4);
                    GUILayout.Label($"大小 scale: {_emoji.worldScale:F2}");
                    _emoji.worldScale = GUILayout.HorizontalSlider(_emoji.worldScale, 0.05f, 1.5f);
                    GUILayout.Label($"每幀 frame: {_emoji.frameMs:F0}ms");
                    _emoji.frameMs = GUILayout.HorizontalSlider(_emoji.frameMs, 50f, 1000f);
                    GUILayout.Label($"跟隨平滑 follow: {_emoji.followLerp:F1} (大=瞬移, 小=慢慢移)");
                    _emoji.followLerp = GUILayout.HorizontalSlider(_emoji.followLerp, 1f, 30f);
                }
            }
            if (_dbgTab == 4)    // ===== RESULT: 結算面板微調 (名字 / 頭框 / 頭像 AvtShow) =====
            {
                freeMode = GUILayout.Toggle(freeMode, freeMode ? " 自由模式: ON（無排名/無G·經驗，死亡=GAME OVER）" : " 自由模式: OFF");
                GUILayout.Space(6);
                GUILayout.Label("══ 結算名字 & 頭框（進結算後即時套用）══");
                if (_result == null) GUILayout.Label("(尚未進結算畫面)");
                else
                {
                    GUILayout.Label($"名字 X: {_result.nickX:F0}");
                    _result.nickX = GUILayout.HorizontalSlider(_result.nickX, 60f, 320f);
                    GUILayout.Label($"名字 Y偏移: {_result.nickYOff:F0}");
                    _result.nickYOff = GUILayout.HorizontalSlider(_result.nickYOff, -10f, 30f);
                    GUILayout.Label($"名字 大小 size: {_result.nickSize:F0}");
                    _result.nickSize = GUILayout.HorizontalSlider(_result.nickSize, 10f, 36f);
                    GUILayout.Space(6);
                    GUILayout.Label($"頭框 X: {_result.headBoxX:F0}");
                    _result.headBoxX = GUILayout.HorizontalSlider(_result.headBoxX, 0f, 90f);
                    GUILayout.Label($"頭框 Y偏移: {_result.headBoxYOff:F0}");
                    _result.headBoxYOff = GUILayout.HorizontalSlider(_result.headBoxYOff, -10f, 50f);
                    GUILayout.Label($"頭框 正方形大小 size: {_result.headBoxSize:F0}");
                    _result.headBoxSize = GUILayout.HorizontalSlider(_result.headBoxSize, 20f, 96f);
                }
                GUILayout.Space(8);
                GUILayout.Label("══ 頭像 AvtShow（idle 人物）即時套用 ══");
                GUILayout.Label("相機自動跟頭骨→頭一定在框；調 yaw 角度、dist/fov 遠近、偏移對準臉");
                GUILayout.Label($"旋轉 yaw: {headAvatarYaw:F0}°（轉到面向正確）");
                headAvatarYaw = GUILayout.HorizontalSlider(headAvatarYaw, 0f, 360f);
                GUILayout.Label($"縮放 scale: {headAvatarScale:F2}");
                headAvatarScale = GUILayout.HorizontalSlider(headAvatarScale, 0.2f, 6f);
                GUILayout.Label($"相機 距離 dist: {headPortraitDist:F0}（小=放大）");
                headPortraitDist = GUILayout.HorizontalSlider(headPortraitDist, 5f, 120f);
                GUILayout.Label($"相機 FOV: {headPortraitFov:F0}");
                headPortraitFov = GUILayout.HorizontalSlider(headPortraitFov, 10f, 60f);
                GUILayout.Space(4);
                GUILayout.Label($"瞄準偏移 X: {headAimOffset.x:F1}");
                headAimOffset.x = GUILayout.HorizontalSlider(headAimOffset.x, -40f, 40f);
                GUILayout.Label($"瞄準偏移 Y: {headAimOffset.y:F1}（+往上對到臉）");
                headAimOffset.y = GUILayout.HorizontalSlider(headAimOffset.y, -40f, 40f);
                GUILayout.Label($"瞄準偏移 Z: {headAimOffset.z:F1}");
                headAimOffset.z = GUILayout.HorizontalSlider(headAimOffset.z, -40f, 40f);
            }
            if (_dbgTab == 5)    // ===== BANNER: YOU WIN/LOSE 位置 / 大小 / 動畫時間 + 預覽/播放測試 =====
            {
                GUILayout.Label("══ YOU WIN/LOSE 橫幅動畫（進結算畫面後即時套用）══");
                GUILayout.Label("只調動畫『起始位置』；結束位置與大小固定(官方)。可先按 F5 跳到結算畫面。");
                if (_result == null) GUILayout.Label("(尚未進結算畫面)");
                else
                {
                    GUILayout.Label($"起始位置 X(中心): {_result.bannerStartX:F0}");
                    _result.bannerStartX = GUILayout.HorizontalSlider(_result.bannerStartX, -100f, 900f);
                    GUILayout.Label($"起始位置 Y(中心): {_result.bannerStartY:F0}");
                    _result.bannerStartY = GUILayout.HorizontalSlider(_result.bannerStartY, -100f, 400f);
                    GUILayout.Label($"起始大小 scale: {_result.bannerStartScale:F2}");
                    _result.bannerStartScale = GUILayout.HorizontalSlider(_result.bannerStartScale, 0.5f, 6f);
                    GUILayout.Label($"動畫時間 sec: {_result.bannerAnimSec:F2}");
                    _result.bannerAnimSec = GUILayout.HorizontalSlider(_result.bannerAnimSec, 0.05f, 2f);
                    GUILayout.Space(6);
                    GUILayout.Label("預覽『起始』（定格在起始點+畫面寬，拖上面 X/Y 即時看）：");
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("預覽起始 WIN")) _result.PreviewBanner(true, true);
                    if (GUILayout.Button("預覽起始 LOSE")) _result.PreviewBanner(false, true);
                    GUILayout.EndHorizontal();
                    GUILayout.Label("預覽『結束』（定格在固定結束點/大小）：");
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("預覽結束 WIN")) _result.PreviewBanner(true, false);
                    if (GUILayout.Button("預覽結束 LOSE")) _result.PreviewBanner(false, false);
                    GUILayout.EndHorizontal();
                    GUILayout.Label("播放動畫（起始→結束）：");
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("播放 WIN")) _result.PlayBannerTest(true);
                    if (GUILayout.Button("播放 LOSE")) _result.PlayBannerTest(false);
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndScrollView();

            GUILayout.EndArea();
        }

        private const float BurstSecPerFrame = 0.03f;     // ~30ms/frame -> 12 frames ≈ 0.36s (quick in-game flash)
        private void UpdateFx()
        {
            if (_burstFrames == null) return;
            for (int i = _fx.Count - 1; i >= 0; i--)
            {
                var fx = _fx[i];
                int step = (int)((Time.time - fx.Start) / BurstSecPerFrame);
                if (step >= _burstFrames.Length)
                {
                    // HOLD: finished a round, still held -> loop (wait for the full animation before the next round).
                    // TAP (or released hold): one-shot, ends here.
                    if (fx.IsHold && _holding[fx.Lane] != null) { fx.Start = Time.time; step = 0; }
                    else { if (fx.IsHold) _holdBurst[fx.Lane] = null; DestroyBurst(fx); _fx.RemoveAt(i); continue; }
                }
                var spr = _burstFrames[step];
                fx.Sr.sprite = spr; if (fx.Sr2) fx.Sr2.sprite = spr;
            }
        }

        private void DestroyBurst(BurstFx fx) { if (fx.Sr) Destroy(fx.Sr.gameObject); if (fx.Mat) _matPool.Push(fx.Mat); }

        // Tear down every in-flight gameplay burst / hold / click-flash. Needed when the result is entered MID-song
        // (F5, or HP-out while holding): a hold burst loops forever while its lane stays "held", so without this it
        // freezes on screen behind the result panel.
        private void ClearGameplayFx()
        {
            for (int i = _fx.Count - 1; i >= 0; i--) DestroyBurst(_fx[i]);
            _fx.Clear();
            for (int lane = 0; lane < Keys; lane++)
            {
                _holdBurst[lane] = null; _holding[lane] = null;
                if (_clickFlashSr[lane]) _clickFlashSr[lane].enabled = false; _clickFlashStart[lane] = -1f;
            }
        }

        private sealed class BurstFx { public SpriteRenderer Sr, Sr2; public Material Mat; public int Lane; public float Start; public bool IsHold; }

        // ---------- lane click flash (decompiled NoteBoard_DrawClickFlash_00498bd0) ----------

        // (re)start the lane's click strip at frame 0 (full alpha). Called on a hit.
        private void TriggerClickFlash(int lane)
        {
            if (lane < 0 || lane >= Keys || _clickFlashSr[lane] == null) return;
            _clickFlashStart[lane] = Time.time;
        }

        // step the 3-frame white×alpha cycle (255→130→0). A tap plays it once then hides; a held long-note
        // re-arms each frame so the lane keeps pulsing (matches the decompile drawing it while struck/held).
        private void UpdateClickFlash()
        {
            for (int lane = 0; lane < Keys; lane++)
            {
                var sr = _clickFlashSr[lane]; if (sr == null) continue;
                bool held = _holding[lane] != null;
                if (held && _clickFlashStart[lane] < 0f) _clickFlashStart[lane] = Time.time;   // keep pulsing while held
                if (_clickFlashStart[lane] < 0f) { if (sr.enabled) sr.enabled = false; continue; }
                int step = (int)((Time.time - _clickFlashStart[lane]) / Mathf.Max(1e-4f, clickFlashStepSec));
                if (held) step %= ClickFlashAlpha.Length;                                       // loop the pulse
                else if (step >= ClickFlashAlpha.Length - 1) { _clickFlashStart[lane] = -1f; sr.enabled = false; continue; }  // tap ends on the 0-alpha frame
                float a = ClickFlashAlpha[step] * clickFlashBright;   // 0.8 = ~80% brightness
                sr.enabled = a > 0f;
                if (sr.enabled) sr.color = new Color(1f, 1f, 1f, a);
            }
        }

        // ---------- HUD update ----------

        private void UpdateHud()
        {
            // keydown receptor burst: play *_judgeline2..6 once over recKeydownStepSec/frame, then snap back to idle (1)
            for (int c = 0; c < Keys; c++)
            {
                if (_receptors[c] == null) continue;
                Sprite spr = _recIdle[c];
                if (_recDownStart[c] >= 0f && _recDownFrames[c] != null)
                {
                    int f = (int)((Time.time - _recDownStart[c]) / Mathf.Max(1e-4f, recKeydownStepSec));
                    if (f >= _recDownFrames[c].Length) _recDownStart[c] = -1f;   // burst done → idle frame 1
                    else spr = _recDownFrames[c][f];
                }
                _receptors[c].sprite = spr;
            }

            float age = Time.time - _judgeWordAt;
            if (_judgeWord.sprite != null && age < 0.5f)
            {
                _judgeWord.color = new Color(1, 1, 1, Mathf.Clamp01(1f - age / 0.5f));
                float pop = (1f + Mathf.Clamp01(1f - age * 6f) * 1.0f) * 0.8f; // 2.0->1.0 ×0.8 (decompiled)
                PlaceAspect(_judgeWord, JudgeWordCenter.x, JudgeWordCenter.y, _judgeWord.sprite.bounds.size.x, -2);
                _judgeWord.transform.localScale *= pop;
            }
            else _judgeWord.color = new Color(1, 1, 1, 0);

            UpdateComboDigits(); UpdateScoreDigits(); UpdateHpBar();

            if (_timeText)
            {
                // official format: "[left]  [total]". Left = "--:--" until the music starts (during the READY/GO
                // opening _clockStart is still in the future), then a countdown of the remaining time. Right = total.
                int tot = (int)Math.Round(_totalMs / 1000.0);
                double el = Time.timeAsDouble - _clockStart;
                string left = el < 0 ? "- : -" : $"{(int)Math.Max(0, tot - el) / 60} : {(int)Math.Max(0, tot - el) % 60:00}";
                _timeText.text = FullWidth($"{left}    {tot / 60} : {tot % 60:00}");
            }
            // combo milestone (50/100/150…) -> celebration burst + voice
            int milestone = (_score.Combo / 50) * 50;
            if (milestone >= 50 && milestone != _lastMilestone) { _lastMilestone = milestone; SpawnMilestone(milestone); }

            string camLbl = (use3dCamera && _camReady) ? (_camMode < 0 ? "  F2:AUTO" : $"  F2:CAM{_camMode}") : "";
            if (_info) _info.text = (_failed ? "FAILED " : "") + $"P{_score.PerfectCount} C{_score.CoolCount} B{_score.BadCount} M{_score.MissCount}  x{_score.Combo}{camLbl}";
        }

        private void SpawnMilestone(int combo)
        {
            string v = null;   // combo voices (decompiled): 100→0008 200→0007 300→0006 400→0005 500→0004
            switch (combo) { case 100: v = "VOICE_0008"; break; case 200: v = "VOICE_0007"; break; case 300: v = "VOICE_0006"; break; case 400: v = "VOICE_0005"; break; case 500: v = "VOICE_0004"; break; }
            if (v != null) PlaySe(v);
            // floor combo burst at every 100 (orig 100combo.eft..500combo.eft = 3DEft idx 0x13..0x17,
            // spawned at the dancer's pelvis-on-floor by Dancer_SpawnDirEffect_004a7680, uniform scale 30/20/30/30/35;
            // tiers clamp to 500. NB: the original fires these from authored chart events, not combo%100 — we
            // approximate with the milestone so the effect still plays at 100/200/...).
            if (combo % 100 == 0) SpawnComboBurst(Mathf.Clamp(combo / 100 - 1, 0, 4));
        }

        public float comboBurstSize = 1f, comboBurstBright = 1f;   // F4-tunable
        public float comboGlow = 0f, comboGlowSpread = 0.6f;        // outer-glow halo: intensity (0=off, faithful) + spread
        // decompiled per-tier uniform effect scale (DAT_0054f228, Dancer_SpawnDirEffect_004a7680): 100..500 = 30/20/30/30/35
        private static readonly float[] ComboTierScale = { 30f, 20f, 30f, 30f, 35f };
        private static readonly Dictionary<int, EftFile> _eftCache = new Dictionary<int, EftFile>();
        // Faithful: load the actual <tier>COMBO.EFT and run it through the EftEffect interpreter (real emitters,
        // EMIT counts, start-delays, per-channel curves, ring geometry, additive) — no hand-tuned sprites.
        private void SpawnComboBurst(int tier)
        {
            tier = Mathf.Clamp(tier, 0, 4);
            int effId = 0x13 + tier;
            if (!_eftCache.TryGetValue(effId, out var file))
            {
                var path = Path.Combine(SdoExtracted.Root, "3DEFT", ((tier + 1) * 100) + "COMBO.EFT");
                if (!File.Exists(path)) { Debug.LogWarning("[combo] missing " + path); return; }
                file = EftFile.Load(File.ReadAllBytes(path));
                _eftCache[effId] = file;
            }
            var go = new GameObject("ComboBurst" + ((tier + 1) * 100));
            // pelvis projected to floor (Dancer_SpawnDirEffect: Bip01_Pelvis x/z, y≈0.1); the yuanpan ground ring
            // already tracks the pelvis-on-floor, so reuse its transform as the follow anchor.
            go.transform.position = _ringTr != null ? _ringTr.position : new Vector3(_avatarChest.x, 0.6f, _avatarChest.z);
            // Render on the STAGE layer/camera so the burst shares the scene depth → it rises FROM THE GROUND and is
            // occluded by the dancer's body (a 2D composite drew it flat in front of everything). The white-hot core
            // is enlarged in this depth-correct linear-additive path via EftEffect.BallCoreIntensity (F4 slider).
            int layer = use3dCamera ? SceneLayer : 0;
            float effScale = ComboTierScale[tier] * comboBurstSize;   // .eft uniform spawn scale (× F4 size tuning)
            go.AddComponent<EftEffect>().Init(file, effScale, _ringTr, ResolveEftTex, _addMat, layer, comboBurstBright, comboGlow, comboGlowSpread, ResolveEftMesh);
            if (use3dCamera) SetLayerRecursive(go, SceneLayer);
        }

        private static readonly Dictionary<string, EftFile> _namedEftCache = new Dictionary<string, EftFile>();
        public void SpawnNamedEftForTest(string name, float baseScale) => SpawnNamedEft(name, baseScale);
        // Spawn any <name>.EFT (e.g. FINISHED = the end-of-song result burst: a tex103 root that bursts 18+18+30
        // billboards with posSpread 90-122° = a near-spherical firework. effScale≈5: base 0.25 × anim 5.26 × 5 = the
        // captured worldScale 6.57). Same interpreter/anchor as combos; baseScale × the F4 size slider.
        private void SpawnNamedEft(string name, float baseScale)
        {
            if (!_namedEftCache.TryGetValue(name, out var file))
            {
                var path = Path.Combine(SdoExtracted.Root, "3DEFT", name + ".EFT");
                if (!File.Exists(path)) { Debug.LogWarning("[eft] missing " + path); return; }
                file = EftFile.Load(File.ReadAllBytes(path));
                _namedEftCache[name] = file;
            }
            var go = new GameObject("Eft_" + name);
            go.transform.position = _ringTr != null ? _ringTr.position : new Vector3(_avatarChest.x, 0.6f, _avatarChest.z);
            int layer = use3dCamera ? SceneLayer : 0;
            float effScale = baseScale * comboBurstSize;
            go.AddComponent<EftEffect>().Init(file, effScale, _ringTr, ResolveEftTex, _addMat, layer, comboBurstBright, comboGlow, comboGlowSpread, ResolveEftMesh);
            if (use3dCamera) SetLayerRecursive(go, SceneLayer);
        }

        // EFT generic texture list (Extracted/3DEFT/GENERIC/LIST.TXT): pipe index → relative path → Texture2D.
        private static string[] _eftTexList;
        private static readonly Dictionary<int, Texture2D> _eftTexCache = new Dictionary<int, Texture2D>();
        private static Texture2D ResolveEftTex(int idx)
        {
            if (_eftTexCache.TryGetValue(idx, out var cached)) return cached;
            if (_eftTexList == null)
            {
                var list = new List<string>();
                var lp = Path.Combine(SdoExtracted.Root, "3DEFT", "GENERIC", "LIST.TXT");
                if (File.Exists(lp))
                    foreach (var raw in File.ReadAllLines(lp))
                    {
                        int bar = raw.IndexOf('|'); if (bar < 0) continue;
                        if (!int.TryParse(raw.Substring(0, bar).Trim(), out int li)) continue;
                        var toks = raw.Substring(bar + 1).Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        while (list.Count <= li) list.Add(null);
                        list[li] = (toks.Length > 0 && toks[0] != "NULL") ? toks[0] : null;
                    }
                _eftTexList = list.ToArray();
            }
            Texture2D tex = null;
            if (idx >= 0 && idx < _eftTexList.Length && _eftTexList[idx] != null)
            {
                var rel = _eftTexList[idx].Replace("generic\\", "").Replace("generic/", "").Replace('\\', '/');
                var dir = Path.Combine(SdoExtracted.Root, "3DEFT", "GENERIC", Path.GetDirectoryName(rel) ?? "");
                var name = Path.GetFileName(rel).ToUpperInvariant();
                tex = SdoExtracted.LoadTextureRaw(dir, name + ".png") ?? SdoExtracted.LoadTextureRaw(dir, name + ".BMP");
            }
            _eftTexCache[idx] = tex;
            return tex;
        }

        // EFT 3D-mesh list (Extracted/3DEFT/XMESH/LIST.TXT): the SECOND texture pipeline the texture-only port missed.
        // The engine (Effect_LoadTextureLists_004be540 first loop) increments an index per parsed line and loads
        // <path>.msh/.hrc via Avatar_LoadHrc into AvatarScene; a particle's word[6] indexes it. Here: sequential
        // line index → .MSH parsed by LoadEffectMesh (FVF-0x112 effect-mesh path) → Unity mesh + its DDS texture.
        // idx 32 = adol_x\aef03_00 = the 200/300COMBO slot0 blue mesh (textured AEF_3_00.DDS); 100/101 = column_00/01.
        private static string[] _eftMeshList;
        private static readonly Dictionary<int, EftMeshData> _eftMeshCache = new Dictionary<int, EftMeshData>();
        private static EftMeshData ResolveEftMesh(int idx)
        {
            if (_eftMeshCache.TryGetValue(idx, out var cached)) return cached;
            if (_eftMeshList == null)
            {
                var list = new List<string>();
                var lp = Path.Combine(SdoExtracted.Root, "3DEFT", "XMESH", "LIST.TXT");
                if (File.Exists(lp))
                    foreach (var raw in File.ReadAllLines(lp))
                    {
                        if (string.IsNullOrWhiteSpace(raw)) continue;   // engine: sscanf==0 on a blank line ⇒ no index bump
                        int bar = raw.IndexOf('|');
                        var body = (bar >= 0 ? raw.Substring(bar + 1) : raw).Trim();
                        var toks = body.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        string path = toks.Length > 0 ? toks[0] : null;   // path token (2nd sscanf field)
                        if (path != null && path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant() == "xmesh") path = null;
                        list.Add(path);   // sequential index = position over non-blank lines (the engine's counter)
                    }
                _eftMeshList = list.ToArray();
            }
            EftMeshData md = null;
            if (idx >= 0 && idx < _eftMeshList.Length && _eftMeshList[idx] != null)
            {
                var rel = _eftMeshList[idx].Replace("xmesh\\", "").Replace("xmesh/", "").Replace('/', '\\');
                var xdir = Path.Combine(SdoExtracted.Root, "3DEFT", "XMESH");
                // the extraction keeps meshes both under their subfolder (adol_x\AEF03_00.MSH) AND flattened in XMESH\;
                // try the listed subpath first, then the bare basename.
                var mshPath = Path.Combine(xdir, rel + ".MSH");
                if (!File.Exists(mshPath)) mshPath = Path.Combine(xdir, Path.GetFileName(rel).ToUpperInvariant() + ".MSH");
                if (File.Exists(mshPath))
                {
                    try { md = LoadEffectMesh(File.ReadAllBytes(mshPath), xdir, idx, rel); }
                    catch (Exception e) { Debug.LogWarning("[eft-mesh] load error idx " + idx + ": " + e.Message); }
                }
                if (md == null) Debug.LogWarning("[eft-mesh] missing/failed mesh idx " + idx + " (" + rel + ")");
            }
            _eftMeshCache[idx] = md;
            return md;
        }

        // Parse an SDO effect-mesh (.MSH, FVF 0x112 = pos+normal+uv, stride 32) into a Unity mesh + its texture.
        // This variant differs from SCENE.MSH (its post-vertex material block is laid out differently, so SceneLoader
        // mis-reads numMat), but the geometry header is identical: magic "Mesh00000030", submeshCount, then
        // [fvf, idxBytes, opt, indices, vertBytes, stride, verts]. We parse block 0 (aef03_00 = 15 verts / 16 tris)
        // and texture it with the mesh's own DDS — preferring the distinctively-coloured "aef_3*" material (the blue
        // the user identified) over the generic image/aef_0_* parts. Verbatim verts (D3D9 & Unity share LH).
        private static EftMeshData LoadEffectMesh(byte[] d, string xdir, int idx, string rel)
        {
            if (d == null || d.Length < 32 || System.Text.Encoding.ASCII.GetString(d, 0, 4) != "Mesh") return null;
            int p = 12;
            uint U() { uint v = (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24)); p += 4; return v; }
            float F(int o) => BitConverter.ToSingle(d, o);
            U();                                  // submeshCount (block 0 only)
            U();                                  // fvf (0x112)
            int idxBytes = (int)U(); U();         // index bytes, options
            int idxCount = idxBytes / 2;
            if (idxCount <= 0 || p + idxBytes > d.Length) return null;
            var tris = new int[idxCount];
            for (int i = 0; i < idxCount; i++) { tris[i] = (ushort)(d[p] | (d[p + 1] << 8)); p += 2; }
            int vertBytes = (int)U(); int stride = (int)U();
            if (stride < 16 || vertBytes <= 0 || p + vertBytes > d.Length) return null;
            int vcount = vertBytes / stride, uvOff = stride - 8;
            var verts = new Vector3[vcount]; var uvs = new Vector2[vcount];
            for (int i = 0; i < vcount; i++)
            {
                int b = p + i * stride;
                verts[i] = new Vector3(F(b), F(b + 4), F(b + 8));      // verbatim (D3D9 & Unity both LH)
                uvs[i] = new Vector2(F(b + uvOff), F(b + uvOff + 4));  // V not flipped (same as scene/avatar)
            }
            var mesh = new Mesh { name = "eft-mesh-" + idx };
            mesh.vertices = verts; mesh.uv = uvs; mesh.triangles = tris; mesh.RecalculateBounds();

            // pick the texture: scan the file's embedded .dds names, prefer an "aef_3*" (the coloured part), else the
            // first non-"image" material, else the first. Then resolve the file in XMESH\ (NTFS case-insensitive).
            var names = new List<string>();
            for (int o = 0; o + 4 < d.Length; o++)
                if (d[o] == '.' && d[o + 1] == 'd' && d[o + 2] == 'd' && d[o + 3] == 's')
                {
                    int s = o; while (s > 0 && d[s - 1] > 32 && d[s - 1] < 127) s--;
                    names.Add(System.Text.Encoding.ASCII.GetString(d, s, o - s + 4));
                }
            string pick = null;
            foreach (var n in names) if (n.ToLowerInvariant().Contains("aef_3")) { pick = n; break; }
            if (pick == null) foreach (var n in names) if (!n.ToLowerInvariant().Contains("image")) { pick = n; break; }
            if (pick == null && names.Count > 0) pick = names[0];
            Texture2D tex = pick != null ? LoadXmeshDds(xdir, pick) : null;
            Debug.Log($"[eft-mesh] idx {idx} '{rel}': {vcount}v/{idxCount / 3}t, dds=[{string.Join(",", names)}] picked '{pick}' tex={(tex != null)}");
            return new EftMeshData { Mesh = mesh, SubTex = new[] { tex } };
        }

        // Resolve a DDS referenced inside a .MSH: the stored name may carry leading binary/junk bytes (e.g. "LBaef_3_00.dds"),
        // so try progressively-trimmed suffixes until one names a real file under XMESH\ (case-insensitive on Windows).
        private static Texture2D LoadXmeshDds(string xdir, string rawName)
        {
            for (int s = 0; s < rawName.Length; s++)
            {
                var cand = Path.GetFileName(rawName.Substring(s));
                if (cand.Length < 5) continue;
                var fp = Path.Combine(xdir, cand);
                if (File.Exists(fp)) { try { return DdsLoader.Load(File.ReadAllBytes(fp)); } catch { return null; } }
            }
            return null;
        }

        private void UpdateScoreDigits()
        {
            // (8) commit the real score every 8 beats, then count up old->new + zoom-pop (decompiled BeginAnimate)
            double now = (Time.timeAsDouble - _clockStart) * 1000.0;
            double beatMs = 60000.0 / Math.Max(1.0, _map.Bpm);
            if (_nextScoreCommitMs <= 0) _nextScoreCommitMs = 8 * beatMs;
            if (now >= _nextScoreCommitMs)
            {
                if (_score.Score != _scoreTarget) { _scoreFrom = _shownScore; _scoreTarget = _score.Score; _scoreAnimAt = Time.time; _scoreCommitPop = true; _scoreArmed = true; }
                _nextScoreCommitMs += 8 * beatMs;
                RefreshRanking();   // re-sort + redraw the roster list and rank on the same 8-beat cadence
            }
            // decompiled CtlNumLabel (FUN_0043dac0): NOT a smooth per-frame lerp. It adds a fixed
            // step = delta/20 (0x21c = (target-cur)/0x14) only once every ~50ms (0x31<elapsed, /0x32),
            // then snaps to target at 999ms. => ~20 discrete updates/s, so 個位/十位 不會每幀都在跳(60Hz糊掉).
            double rollMs = (Time.time - _scoreAnimAt) * 1000.0;
            if (rollMs >= 999.0) _shownScore = _scoreTarget;
            else
            {
                long step = (_scoreTarget - _scoreFrom) / 20;   // 0x21c = (target - cur) / 0x14
                long ticks = (long)(rollMs / 50.0);             // one step per ~50ms (0x32) → ~20 ticks over 1s
                _shownScore = _scoreFrom + step * ticks;
            }

            string s = _shownScore.ToString("D8");
            int firstSig = s.Length - 1;               // hidezero: hide leading zeros (keep last)
            for (int k = 0; k < s.Length; k++) if (s[k] != '0') { firstSig = k; break; }
            for (int i = 0; i < _scoreDigits.Length; i++)
            {
                bool show = i >= firstSig && i < s.Length;
                bool newlyVisible = show && !_digitVisible[i];
                // pop a digit only when it FIRST appears (a higher place showing up later in the roll) or on a commit
                // (all visible digits together). NOT on every rolling char change — that would reset it forever.
                if (show && _scoreArmed && (_scoreCommitPop || newlyVisible)) _digitPopAt[i] = Time.time;
                _digitVisible[i] = show;
                var spr = show ? _scoreDigitSprites[s[i] - '0'] : null;
                _scoreDigits[i].enabled = spr != null; _scoreDigits[i].sprite = spr;
                if (spr != null) { PlaceAspect(_scoreDigits[i], ScorePos.x + i * ScoreDigitPitch + 14, ScorePos.y + 18, 29); _scoreDigits[i].transform.localScale *= DigitBounce(Time.time - _digitPopAt[i]); }
            }
            _scoreCommitPop = false;
        }

        // per-digit pop: slow grow 1.0->1.3 then slow shrink 1.3->1.0, eased, over the WHOLE count-up
        // (~1s, decompiled scale 1.0<->1.3). 必須跟數字滾動同長,否則「還沒跑完就縮小完了」: 放大在中段(0.5s)到頂,
        // 縮回 1.0 剛好落在數字停止滾動(999ms)的同一刻。
        private const float DigitPopDur = 0.999f;             // = roll length (999ms snap), keep in sync
        private static float DigitBounce(float t)
        {
            const float D = DigitPopDur;
            if (t < 0f || t >= D) return 1f;
            float u = t / D;
            float tri = u < 0.5f ? u * 2f : (1f - u) * 2f;    // 0->1->0
            return 1f + 0.3f * Mathf.SmoothStep(0f, 1f, tri);  // ease in/out = 緩慢放大/縮小
        }

        private void UpdateComboDigits()
        {
            int combo = _score.Combo;
            if (combo < 2) { foreach (var d in _comboDigits) d.enabled = false; if (_comboWord) _comboWord.enabled = false; _lastComboShown = combo; return; }
            if (combo != _lastComboShown) { _comboPopAt = Time.time; _lastComboShown = combo; }
            float pop = (1f + Mathf.Clamp01(1f - (Time.time - _comboPopAt) * 9f) * 1.0f) * 0.8f;
            string s = combo.ToString();
            float startX = TrackCenterX - (s.Length - 1) * ComboDigitStep / 2f;   // centred on the track
            for (int i = 0; i < _comboDigits.Count; i++)
            {
                var d = _comboDigits[i];
                if (i >= s.Length) { d.enabled = false; continue; }
                var spr = _comboDigitSprites[s[i] - '0'];
                d.enabled = spr != null; d.sprite = spr;
                // pop scales the WHOLE number as a single group about its centre (TrackCenterX): grow each digit's
                // offset-from-centre by `pop` too, so the inter-digit gaps expand in step with the digit size and the
                // places never collide/overlap. (Scaling each digit about its own centre left the gaps fixed -> fight.)
                if (spr != null) { float dx = TrackCenterX + (startX + i * ComboDigitStep - TrackCenterX) * pop; PlaceAspect(d, dx, ComboDigitY, ComboDigitW, -2); d.transform.localScale *= pop; }
            }
            if (_comboWord && _comboWord.sprite != null) { _comboWord.enabled = true; PlaceAspect(_comboWord, TrackCenterX, ComboWordY, ComboWordW); _comboWord.transform.localScale *= pop; }
        }

        // ==== ranking UI: head nameplate, centre rank N/M, right-side roster list ====

        private void BuildRankingUi()
        {
            _finalEst = Math.Max(20000L, (long)_map.TotalNotes * 68L);   // ≈ all-perfect ServerScore ceiling
            var arrowDir = Path.Combine(SdoExtracted.Root, "UI", "ARROW");
            _arrowFrames = new Sprite[9];
            for (int i = 0; i < 9; i++) _arrowFrames[i] = SdoExtracted.LoadImage(arrowDir, i.ToString("D3") + ".PNG");
            var gpDir = SdoExtracted.GameplayUiDir;
            _slashSprite = SdoExtracted.LoadImage(gpDir, "GAMEPLAY61.PNG");   // the "/" glyph (25×29, matches PKSCORE)
            var pkDir = Path.Combine(gpDir, "PKSCORE");
            for (int i = 0; i < _pkDigits.Length; i++) _pkDigits[i] = SdoExtracted.LoadImage(pkDir, i + ".PNG");

            // centre "N / M": two pink PKSCORE digits + the GAMEPLAY61 slash glyph between them.
            _rankCurD = NewSR("RankCur", null, 26); _rankCurD.enabled = false;
            _rankTotD = NewSR("RankTot", null, 26); _rankTotD.enabled = false;
            _rankSlash = NewSR("RankSlash", _slashSprite, 26); _rankSlash.enabled = false;

            // right-side roster list: RosterRows × (name [left] + score [right]), fixed positions on the HUD layer.
            _rosterName = new Label3D[RosterRows];
            _rosterScore = new Label3D[RosterRows];
            for (int row = 0; row < RosterRows; row++)
            {
                float y = rosterFirstY + row * rosterRowStep;
                _rosterName[row] = TextStyles.NewLabel("RosterName" + row, TextStyles.Style.ListOther, 45, rosterFontWorld, TextAnchor.MiddleLeft);
                _rosterName[row].Position = SdoLayout.ToWorld(rosterNameX, y, -3f);
                _rosterScore[row] = TextStyles.NewLabel("RosterScore" + row, TextStyles.Style.ListOther, 45, rosterFontWorld, TextAnchor.MiddleRight);
                _rosterScore[row].Position = SdoLayout.ToWorld(rosterScoreX, y, -3f);
            }

            // spectators (旁觀玩家): GAMEPLAY18 title + fake light-blue names (static; never re-sorted).
            _lookerTitle = NewSR("LookerTitle", SdoExtracted.LoadImage(gpDir, "GAMEPLAY18.PNG"), 45);
            SdoLayout.PlaceTopLeft(_lookerTitle, lookerTitleX, lookerTitleY, -3f);
            _lookerRows = new Label3D[SpectatorNames.Length];
            for (int i = 0; i < SpectatorNames.Length; i++)
            {
                _lookerRows[i] = TextStyles.NewLabel("Looker" + i, TextStyles.Style.Looker, 45, lookerFontWorld, TextAnchor.MiddleLeft);
                _lookerRows[i].Position = SdoLayout.ToWorld(lookerX, lookerFirstY + i * lookerRowStep, -3f);
                _lookerRows[i].Text = SpectatorNames[i];
            }
        }

        // re-apply the (live-tunable) roster font/positions + rank size, then redraw. Hooked to the F4 button.
        private void RelayoutRoster()
        {
            if (_rosterName == null) return;
            for (int row = 0; row < RosterRows; row++)
            {
                float y = rosterFirstY + row * rosterRowStep;
                _rosterName[row].PxSize = rosterFontWorld;
                _rosterName[row].Position = SdoLayout.ToWorld(rosterNameX, y, -3f);
                _rosterScore[row].PxSize = rosterFontWorld;
                _rosterScore[row].Position = SdoLayout.ToWorld(rosterScoreX, y, -3f);
            }
            if (_lookerTitle != null) SdoLayout.PlaceTopLeft(_lookerTitle, lookerTitleX, lookerTitleY, -3f);
            if (_lookerRows != null)
                for (int i = 0; i < _lookerRows.Length; i++)
                {
                    _lookerRows[i].PxSize = lookerFontWorld;
                    _lookerRows[i].Position = SdoLayout.ToWorld(lookerX, lookerFirstY + i * lookerRowStep, -3f);
                }
            if (_roster.Count == 0) RebuildRoster();
            UpdateRosterList();
            UpdateRankDisplay();
        }

        // the local dancer's nameplate (animated arrow + name). It is a SCREEN-SPACE label (on the HUD
        // layer, not the scene layer): HeadMarker projects the head bone through the scene cam each frame
        // and draws a fixed pixel distance above it — so it floats over the head from any angle and never
        // occludes it. Only the local player is rendered, so there is exactly one.
        private void CreateHeadMarker(SdoAvatar avatar)
        {
            int headIdx = avatar.BoneIndex("Bip01_Head");
            if (headIdx < 0) headIdx = avatar.BoneIndex("Bip01_Neck");
            Transform anchor = null;
            if (headIdx >= 0 && _avatarRoot != null)
            {
                var ag = new GameObject("HeadMarkerAnchor");
                if (use3dCamera) ag.layer = SceneLayer;
                ag.transform.SetParent(_avatarRoot, false);
                avatar.AddAnchor(headIdx, ag.transform);
                anchor = ag.transform;
            }
            var go = new GameObject("HeadMarker");   // HUD layer (default) — children draw in the main ortho cam
            var hm = go.AddComponent<HeadMarker>();
            hm.Init(_arrowFrames, localPlayerName);
            Transform a = anchor;
            hm.AnchorGetter = () => a != null ? a.position
                : ((_avatarRoot != null ? _avatarRoot.position : _danceSpot) + new Vector3(0f, 59f, 0f));
            hm.CamGetter = () => _sceneCam != null ? _sceneCam : _cam;
            _headMarker = hm;
        }

        // Build (once) a SEPARATE idle avatar (decompiled: each result row has its own AvtShow avatar playing a wait/
        // idle clip — NOT the background dancer), isolated on its own layer far from the stage, and a camera that
        // renders just its head into a RenderTexture for the local row. Returns the RT, or null if unavailable.
        private Texture BuildLocalHeadPortrait()
        {
            if (!resultHeadPortrait) return null;
            if (_headRt != null) { UpdateHeadPortraitCam(); return _headRt; }
            BuildIdleHeadAvatar();
            if (_headAvatar == null) return null;

            _headRt = new RenderTexture(192, 224, 16, RenderTextureFormat.ARGB32) { name = "HeadPortraitRT" };
            var camGo = new GameObject("HeadPortraitCam");
            _headCam = camGo.AddComponent<Camera>();
            _headCam.orthographic = false;
            _headCam.fieldOfView = headPortraitFov;
            _headCam.nearClipPlane = 0.5f; _headCam.farClipPlane = 500f;
            _headCam.cullingMask = 1 << headPortraitLayer;   // ONLY the isolated idle avatar
            _headCam.clearFlags = CameraClearFlags.SolidColor;
            _headCam.backgroundColor = new Color(0f, 0f, 0f, 0f);   // TRANSPARENT → no black box; the panel/stage shows through
            _headCam.targetTexture = _headRt;
            _headCam.depth = -10;
            UpdateHeadPortraitCam();
            return _headRt;
        }

        // The isolated idle avatar (a second skinned instance, parked far from the stage on headPortraitLayer so only
        // the head cam sees it). DanceEnabled=false → it holds the standby idle (RestMot). Simplified material setup
        // (single texture per submesh) — it's only ever seen as a small head portrait.
        private void BuildIdleHeadAvatar()
        {
            if (_headAvatar != null) return;
            var hrc = LoadAsset(skeletonHrc, b => HrcLoader.Load(b));
            if (hrc == null) return;
            var parent = new GameObject("HeadIdleAvatar");
            parent.transform.position = HeadAvatarSpot;   // far from the stage; isolated for the head cam
            var av = parent.AddComponent<SdoAvatar>();
            av.Setup(hrc, LoadAsset(danceMot, b => MotLoader.Load(b)));
            av.SetBodyShape(SdoBodyShape.WeightFromIndex(bodyShapeIndex, maleBody));
            av.RestMot = LoadAsset(restMot, b => MotLoader.Load(b));
            av.DanceEnabled = () => false;     // always hold the standby idle clip
            av.DanceTimeSec = () => -1f;
            foreach (var rel in avatarParts)
            {
                var path = Path.Combine(SdoExtracted.Root, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) continue;
                var r = MshLoader.Load(File.ReadAllBytes(path));
                if (r == null || r.Submeshes.Count == 0) continue;
                var dir = Path.GetDirectoryName(path);
                // PortraitOpaque: forces drawn pixels fully opaque (no semi-transparent hair) + cutout gaps + two-sided.
                var sh = Shader.Find("Sdo/PortraitOpaque") ?? Shader.Find("Unlit/Texture");
                int si = 0;
                foreach (var sub in r.Submeshes)
                {
                    var go = new GameObject("h_" + Path.GetFileNameWithoutExtension(rel) + "_" + si++);
                    go.transform.SetParent(parent.transform, false);
                    go.AddComponent<MeshFilter>().mesh = sub.Mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    var tex = ResolveDds(dir, sub.Dds);
                    mr.sharedMaterial = tex != null ? new Material(sh) { mainTexture = tex }
                                                    : new Material(Shader.Find("Unlit/Color")) { color = PartColor(rel) };
                    if (sub.BindVerts != null && sub.BoneHrc != null)
                        av.AddPart(sub.Mesh, sub.BindVerts, sub.BoneHrc, sub.BoneWt, sub.MshInvBindByHrc);
                }
            }
            av.PoseInitialIdle();
            SetLayerRecursive(parent, headPortraitLayer);
            _headAvatar = av;
            // cache the head bone's REST (bind) model-space position — the cam targets this (NOT the live animated bone),
            // so the camera stays FIXED and the idle head-bob plays out inside the frame instead of being chased.
            Vector3 hp = av.BoneModelPos("Bip01_Head");
            if (hp == Vector3.zero) hp = av.BoneModelPos("Bip01_Neck");
            if (hp != Vector3.zero) _headModelPos = hp;
        }

        // FIXED head cam: targets the head bone's REST position (stable; only moves when the F4 sliders change), sitting a
        // fixed distance in front (world -Z). The avatar is scaled/yawed for the 3/4 angle; its idle bob plays in-frame.
        private void UpdateHeadPortraitCam()
        {
            if (_headAvatar != null)
            {
                var t = _headAvatar.transform;
                t.position = HeadAvatarSpot;
                t.localScale = Vector3.one * Mathf.Max(0.01f, headAvatarScale);
                t.localRotation = Quaternion.Euler(0f, headAvatarYaw, 0f);
            }
            if (_headCam == null || _headAvatar == null) return;
            Vector3 restHead = _headAvatar.transform.TransformPoint(_headModelPos);   // rest head world pos (no bob)
            Vector3 target = restHead + headAimOffset;
            _headCam.fieldOfView = headPortraitFov;
            _headCam.transform.position = target + new Vector3(0f, 0f, -headPortraitDist);
            _headCam.transform.LookAt(target, Vector3.up);
        }

        // rebuild + redraw the roster (called at each 8-beat score commit and once at startup).
        private void RefreshRanking()
        {
            if (_rosterName == null || !_trackVisible) return;   // not built / hidden during the opening hold
            if (freeMode) { SetRankingVisible(false); return; }  // 自由模式: no ranking display during play
            RebuildRoster();
            UpdateRosterList();
            UpdateRankDisplay();
        }

        private void RebuildRoster()
        {
            _roster.Clear();
            _roster.Add(new PlayerEntry(localPlayerName, _score != null ? _score.Score : 0L, true));
            if (mockOpponents && !freeMode)   // 自由模式 = solo (no opponents)
            {
                double now = _clockStart >= 0 ? (Time.timeAsDouble - _clockStart) * 1000.0 : 0.0;
                double progress = _totalMs > 1.0 ? Math.Min(1.0, Math.Max(0.0, now / _totalMs)) : 0.0;
                int n = Math.Min(OpponentNames.Length, RosterRows - 1);
                for (int i = 0; i < n; i++)
                    _roster.Add(new PlayerEntry(OpponentNames[i], SimOpponentScore(i, progress), false));
            }
        }

        // deterministic mock score: skill × smoothstep(progress) × (1 ± small oscillation). The oscillation
        // lets opponents trade places over the song so the rank moves; result is clamped ≥ 0.
        private long SimOpponentScore(int i, double progress)
        {
            float skill = 0.72f + 0.11f * ((i * 7 + 3) % 5);                 // ≈ 0.72..1.16 spread
            double curve = progress * progress * (3.0 - 2.0 * progress);     // smoothstep, monotonic 0→1
            double jitter = 0.05 * Math.Sin(i * 1.7 + progress * (6.0 + i)); // ±5% lead changes
            double v = _finalEst * skill * curve * (1.0 + jitter);
            return v < 0 ? 0 : (long)v;
        }

        private void UpdateRosterList()
        {
            var order = RankingBoard.SortedIndices(_roster);
            for (int row = 0; row < RosterRows; row++)
            {
                if (row < order.Length)
                {
                    var p = _roster[order[row]];
                    var (face, edge) = TextStyles.Colors(p.IsLocal ? TextStyles.Style.ListLocal : TextStyles.Style.ListOther);
                    _rosterName[row].SetColors(face, edge); _rosterName[row].SetActive(true); _rosterName[row].Text = p.Name;
                    _rosterScore[row].SetColors(face, edge); _rosterScore[row].SetActive(true); _rosterScore[row].Text = p.Score.ToString();
                }
                else { _rosterName[row].SetActive(false); _rosterScore[row].SetActive(false); }
            }
        }

        private void UpdateRankDisplay()
        {
            var (rank, total) = RankingBoard.LocalRank(_roster);
            rank = Mathf.Clamp(rank, 0, 6);    // PKSCORE digits only go 0..6
            total = Mathf.Clamp(total, 0, 6);
            var cur = _pkDigits[rank]; var tot = _pkDigits[total];
            _rankCurD.sprite = cur; _rankCurD.enabled = cur != null;
            _rankTotD.sprite = tot; _rankTotD.enabled = tot != null;
            // N (current) — slash — M (total), spaced on the score's column pitch (M lands under the tens digit).
            if (cur != null) PlaceAspect(_rankCurD, rankCenterX - rankPitch, rankY, rankDigitW, -2f);
            _rankSlash.enabled = _rankSlash.sprite != null;
            if (_rankSlash.sprite != null) PlaceAspect(_rankSlash, rankCenterX, rankY, rankDigitW, -2f);  // GAMEPLAY61 "/"
            if (tot != null) PlaceAspect(_rankTotD, rankCenterX + rankPitch, rankY, rankDigitW, -2f);
        }

        private void SetRankingVisible(bool on)
        {
            if (freeMode) on = false;   // 自由模式: ranking (rank N/M + roster list) never shows during play
            if (_rosterName != null)
                for (int i = 0; i < RosterRows; i++)
                {
                    if (_rosterName[i] != null) _rosterName[i].SetActive(on);
                    if (_rosterScore[i] != null) _rosterScore[i].SetActive(on);
                }
            if (_rankCurD) _rankCurD.enabled = on && _rankCurD.sprite != null;
            if (_rankTotD) _rankTotD.enabled = on && _rankTotD.sprite != null;
            if (_rankSlash) _rankSlash.enabled = on;
            if (_lookerTitle) _lookerTitle.enabled = on && _lookerTitle.sprite != null;
            if (_lookerRows != null)
                for (int i = 0; i < _lookerRows.Length; i++)
                    if (_lookerRows[i] != null) _lookerRows[i].SetActive(on);
        }

        private void UpdateHpBar()
        {
            if (!_trackVisible) return;   // hidden during the opening intro; SetTrackVisible(true) re-shows it
            double hp = _health?.Health ?? HealthProcessor.MaxHealth;
            float frac = Mathf.Clamp01((float)((hp - HealthProcessor.FloorHealth) / (HealthProcessor.MaxHealth - HealthProcessor.FloorHealth)));
            ShowEmoji(_emojiState.OnHp(frac));   // low-HP emoji (GTH): <30% bar fires once, re-arms above 40%
            // official MyHp fill clipped to (HP+150)/1150 (no overlay -> uniform red, no banding).
            if (_hpTex) SdoLayout.PlaceBarFill(_hpTex, HpPos.x, HpPos.y, HpSize.x, HpSize.y, frac, -0.1f);
            if (_hpGlow && _hpGlowFrames != null && _hpGlowFrames.Length > 0)
            {
                _hpGlowT += Time.deltaTime * 24f;   // HpEft flash (6 frames) — was too slow at 12fps
                _hpGlow.sprite = _hpGlowFrames[((int)_hpGlowT) % _hpGlowFrames.Length];
                // glow is opaque-on-black -> additive. Drive its OWN material's _TintColor by hpGlowBright so it reads
                // as bright as the official (the shared _addMat's stock (.5,.5,.5,.5) tint was halving it -> too dim).
                if (_hpGlowMat != null)
                {
                    float t = 0.5f * hpGlowBright;   // 0.5 = old stock; rgb keeps brightening past 1 (additive, unclamped)
                    if (_hpGlowMat.HasProperty("_TintColor")) _hpGlowMat.SetColor("_TintColor", new Color(t, t, t, Mathf.Clamp01(t)));
                    // Scissor the glow to the bar's frame (world X) so the low-HP flash is CUT at either end, not spilled.
                    if (_hpGlowMat.HasProperty("_ClipMinX"))
                    {
                        _hpGlowMat.SetFloat("_ClipMinX", SdoLayout.WorldX(HpPos.x));
                        _hpGlowMat.SetFloat("_ClipMaxX", SdoLayout.WorldX(HpPos.x + HpSize.x));
                    }
                    _hpGlow.sharedMaterial = _hpGlowMat;
                }
                // HpEft sits at the HP fill's LEADING EDGE (decompiled HpEft.x = (HP+150)/1150 * barW + base), native
                // 64×32 (no width-squash). Clamp so the glow's right edge never juts PAST the bar's right end.
                // HpEft.png's bright/widest core sits at ~0.78 of its width; hpGlowOffsetX (default -20) lands that core
                // flush ON the fill edge (the old -16 left it ~2px right of the edge -> read as "too far right").
                float edgeX = Mathf.Min(HpPos.x + HpSize.x * frac, HpPos.x + HpSize.x);   // fill edge, capped at bar end
                float cx = edgeX + hpGlowOffsetX;
                PlaceAspect(_hpGlow, cx, HpPos.y + HpSize.y / 2f, HpEftSize.x, -0.2f);
                _hpGlow.enabled = hp > HealthProcessor.FloorHealth + 1;
            }
        }

        private sealed class RuntimeNote
        {
            public readonly OsuHitObject Note; public readonly SpriteRenderer Head, Tail; public readonly GameObject Body;
            public bool HeadJudged, BundledFail, Done;
            public RuntimeNote(OsuHitObject n, SpriteRenderer head, GameObject body, SpriteRenderer tail)
            { Note = n; Head = head; Body = body; Tail = tail; }
        }
    }
}
