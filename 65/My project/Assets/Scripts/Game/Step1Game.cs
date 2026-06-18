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
        public int healthLevel = 0;
        public bool autoPlay = true;
        // DEBUG: force a grade on every manual hit (-1 = real timing window). F4 panel selects it.
        public int forcedJudge = -1;
        private static readonly string[] ForceJudgeLabels = { "Real", "Perfect", "Cool", "Bad", "Miss" };
        public float scrollPxPerSec = 320f;
        public float judgeLineY = 70f;        // receptor / hit line Y (design px). UPSCROLL: notes rise to it.
        public string gnPath = @"H:\65_remake\assets\sdox_offline\music\sdom1435K.gn"; // official chart
        public string oggPath = @"H:\65_remake\assets\sdox_offline\music\sdom1435.ogg"; // matching song audio
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
        private AudioSource _audio, _sfx;
        private readonly Dictionary<string, AudioClip> _seCache = new Dictionary<string, AudioClip>();
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
        private bool _showDebugUI = true;
        private Vector2 _dbgScroll;          // scroll for the tuning sliders so they never push the playtest controls off-panel
        private TextMesh _musicName, _lvText, _timeText, _info, _fpsText;
        private float _fps;
        private double _totalMs;
        private int _lastMilestone;       // last combo milestone (50/100/150…) already celebrated
        private long _shownScore, _scoreFrom, _scoreTarget;  // (8) score commits every 8 beats, then counts up + zooms
        private double _nextScoreCommitMs; private float _scoreAnimAt = -10f;
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

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (FindObjectOfType<Step1Game>() != null) return;
            new GameObject("Step1Game").AddComponent<Step1Game>();
        }

        private void Start()
        {
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
            _audio = gameObject.AddComponent<AudioSource>();
            _sfx = gameObject.AddComponent<AudioSource>();
            // Enter on the crane with no note board: hold the track hidden while the opening shot flies in, then
            // OpeningSequence() reveals it with READY. Only when there's actually a 3D crane to watch.
            if (use3dCamera && _camReady && openingIntroSec > 0f) { _introStartRt = Time.realtimeSinceStartup; SetTrackVisible(false); }
            if (observeBurstMode) { _dancing = false; _camMode = 0; SetTrackVisible(false); _introStartRt = -1f; }   // idle dancer, fixed cam, hidden track
            StartCoroutine(LoadAndPlayAudio());
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
            // EFT_HIT bursts are opaque-on-black -> additive blending so black reads as transparent glow.
            // The Particles/Additive shader's _MainTex is NOT [PerRendererData], so SpriteRenderers SHARING one
            // material all sample the last-written sprite -> bursts cross-bleed & jitter. Each burst clones its
            // OWN instance of this template (see SpawnBurst) so every burst animates independently.
            var sh = Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Sprites/Default");
            _addMat = new Material(sh);
            _hpGlowMat = new Material(sh);  // HP glow gets its own additive instance (tint driven by hpGlowBright; never shared so its _MainTex stays this glow's frame)
        }

        private bool LoadChart()
        {
            // (3) official .gn chart first
            if (!string.IsNullOrEmpty(gnPath) && File.Exists(gnPath))
            {
                _map = GnChart.Load(File.ReadAllBytes(gnPath), difficulty);
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
            var lblSong = NewSR("LblSong", SdoExtracted.Hud("GamePlay1.an"), 30); SdoLayout.PlaceTopLeft(lblSong, 11, 575);   // "歌曲名:"
            var lblAttr = NewSR("LblAttr", SdoExtracted.Hud("GamePlay2.an"), 30); SdoLayout.PlaceTopLeft(lblAttr, 204, 575);   // "LV: 时间:"
            // values sit at x per DdrGamePlay.xml, but y = the label graphics' vertical centre (575+~20/2 ≈ 585),
            // MiddleLeft-anchored so they're vertically centred with "歌曲名:" / "LV: 时间:".
            // Title from the import-time UTF-8 catalog (keyed by .gn filename); GB2312 is never
            // decoded at runtime. Fall back to _map.Title (set only on the .osu path) then "song".
            var songTitle = SongCatalog.Title(gnPath);
            if (string.IsNullOrEmpty(songTitle)) songTitle = _map.Title;
            if (string.IsNullOrEmpty(songTitle)) songTitle = "song";
            _musicName = NewText("MusicName", 80, 585, 13, Color.white); _musicName.text = songTitle;
            _lvText = NewText("MusicLev", 240, 585, 13, Color.white); _lvText.text = _map.Level.ToString();
            int tot0 = (int)Math.Round(_totalMs / 1000.0);   // initial: "--:--  [total]"
            _timeText = NewText("MusicTime", 336, 585, 13, Color.white); _timeText.text = FullWidth($"- : -    {tot0 / 60} : {tot0 % 60:00}");
            _info = NewText("Info", 610, 8, 10, Color.white);
            _fpsText = NewText("Fps", 6, 9, 11, new Color(0.5f, 1f, 0.5f, 1f));   // debug FPS (top-left)
            _readyGo = NewSR("ReadyGo", null, 50); _readyGo.enabled = false;
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
                if (!avatarDebug && avatar != null)
                    try   // never let a hand-glow hiccup abort scene/audio setup (which run AFTER TryLoadAvatar)
                    {
                        CreateHandTrail(parent.transform, avatar, "Bip01_L_Hand", "Bip01_L_Finger0", handYellow);
                        CreateHandTrail(parent.transform, avatar, "Bip01_R_Hand", "Bip01_R_Finger0", handYellow);
                    }
                    catch (System.Exception e) { Debug.LogError("[handtrail] creation failed (non-fatal): " + e); }
                CreateGroundStarRing(_avatarChest.x, _avatarChest.z, 0.6f, avatar, parent.transform);   // follows the dancer's pelvis
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
        public bool observeBurstMode = true;
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
            foreach (var mr in FindObjectsOfType<Renderer>())
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
        // fixed transforms. SCN0009's switch-case loads GUATAN x4 — positions/scales from the decompiled
        // Scene_LoadBackground (case 9); see docs/reverse-engineering/SDO_SCENE_MAPOBJ_TABLE.json.
        private struct MapobjInstance { public Vector3 Pos; public float Scale; }

        private void TryLoadMapobjs()
        {
            var guatan = new[]
            {
                new MapobjInstance { Pos = new Vector3(-45.79f, 0f,   0f), Scale = 1f },
                new MapobjInstance { Pos = new Vector3(129.21f, 0f, -83f), Scale = 1f },
                new MapobjInstance { Pos = new Vector3(-95.79f, 0f,  50f), Scale = 0.65f },
                new MapobjInstance { Pos = new Vector3(179.21f, 0f, -83f), Scale = 0.65f },
            };
            AddMapobj("SCENE/MAPOBJ/GUATAN", "GUATAN", guatan);
        }

        // Build one mapobj group: load HRC+MOT once (read-only, shared) and re-parse the MSH per instance so each
        // gets its own Mesh to CPU-skin into. Animation auto-loops the .mot (no DPS). Placed on the stage layer at
        // native SDO coords (the loaders keep verbatim coords, same as the stage mesh + avatar).
        private void AddMapobj(string relDir, string baseName, MapobjInstance[] instances)
        {
            var dir = Path.Combine(SdoExtracted.Root, relDir.Replace('/', Path.DirectorySeparatorChar));
            var mshPath = Path.Combine(dir, baseName + ".MSH");
            if (!File.Exists(mshPath)) { Debug.LogWarning("[mapobj] missing " + mshPath); return; }
            var mshBytes = File.ReadAllBytes(mshPath);
            HrcLoader hrc = LoadAsset(relDir + "/" + baseName + ".HRC", b => HrcLoader.Load(b));
            MotLoader mot = LoadAsset(relDir + "/" + baseName + ".MOT", b => MotLoader.Load(b));
            var fallbackCol = new Color(0.72f, 0.70f, 0.66f);
            int n = 0;
            foreach (var inst in instances)
            {
                var r = MshLoader.Load(mshBytes);
                if (r == null || r.Submeshes.Count == 0) { Debug.LogWarning("[mapobj] parse fail " + baseName); return; }
                var parent = new GameObject($"{baseName}_{n++}");
                parent.transform.position = inst.Pos;
                parent.transform.localScale = Vector3.one * inst.Scale;
                SdoAvatar avatar = null;
                if (hrc != null) { avatar = parent.AddComponent<SdoAvatar>(); avatar.Setup(hrc, mot); }   // null DPS -> auto-loops .mot
                foreach (var sub in r.Submeshes)
                {
                    var go = new GameObject(baseName + "_mesh");
                    go.transform.SetParent(parent.transform, false);
                    go.AddComponent<MeshFilter>().mesh = sub.Mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    // per-submesh material (cloth/skin split like the avatar): multi-range submesh -> one material per range
                    if (sub.Ranges != null && sub.Ranges.Count > 1 && sub.Mesh.subMeshCount == sub.Ranges.Count)
                    {
                        var mats = new Material[sub.Ranges.Count];
                        for (int s = 0; s < sub.Ranges.Count; s++)
                        {
                            int a = sub.Ranges[s].Attrib;
                            string nm = (sub.DdsNames != null && a >= 0 && a < sub.DdsNames.Length && !string.IsNullOrEmpty(sub.DdsNames[a])) ? sub.DdsNames[a] : sub.Dds;
                            Texture2D t = ResolveDds(dir, nm);
                            mats[s] = t != null ? new Material(Shader.Find("Unlit/Texture")) { mainTexture = t }
                                                : new Material(Shader.Find("Unlit/Color")) { color = fallbackCol };
                        }
                        mr.sharedMaterials = mats;
                    }
                    else
                    {
                        Texture2D tex = ResolveDds(dir, sub.Dds);
                        mr.sharedMaterial = tex != null ? new Material(Shader.Find("Unlit/Texture")) { mainTexture = tex }
                                                        : new Material(Shader.Find("Unlit/Color")) { color = fallbackCol };
                    }
                    if (avatar != null && sub.BindVerts != null && sub.BoneHrc != null)
                        avatar.AddPart(sub.Mesh, sub.BindVerts, sub.BoneHrc, sub.BoneWt, sub.MshInvBindByHrc);
                }
                SetLayerRecursive(parent, SceneLayer);
            }
            Debug.Log($"[mapobj] {baseName}: {instances.Length} instances, {(hrc != null ? hrc.Names.Length + " bones" : "static")}, mot={(mot != null ? "yes" : "no")}");
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
            var sceneRT = new RenderTexture(800, 600, 24) { name = "sceneRT" };
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
            if (Input.GetKeyDown(KeyCode.LeftBracket)) SetTimeScale(_timeScale * 0.5f);    // [ slower
            if (Input.GetKeyDown(KeyCode.RightBracket)) SetTimeScale(_timeScale * 2f);     // ] faster
            if (Input.GetKeyDown(KeyCode.Backslash)) { if (Time.timeScale > 0f) Time.timeScale = 0f; else SetTimeScale(_timeScale); }  // \ pause/resume
            if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadEquals)) SetTimeScale(1f);   // = reset 1×
            ApplyRingDebug();   // live floor-ring spread/brightness/spin from the F4 sliders
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
            ScrollNotes(now);
            if (!_failed) { if (autoPlay) AutoPlay(now); else { HandleInput(now); AutoMiss(now); } }
            UpdateDanceGate(now);   // dancer dance/stop decision (after judging, so this frame's misses count)
            // long note held -> continuous burst that loops ONE full animation at a time (gated). Only this
            // hold case waits for the round to finish; taps fire freely above.
            for (int lane = 0; lane < Keys; lane++)
                if (_holding[lane] != null && _burstFrames != null && _holdBurst[lane] == null) _holdBurst[lane] = SpawnBurst(lane, true);
            UpdateClickFlash();
            UpdateFx(); UpdateHud();
            if (_health != null && _health.IsFailed) _failed = true;
            if (!_ended && (_failed || now > _totalMs + 2000)) { _ended = true; } // ending/fail sfx removed (wrong clips — to re-add later)
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
            for (int lane = 0; lane < Keys; lane++)
            {
                bool down = false, anyHeld = false, anyUp = false;
                foreach (var k in LaneKeys[lane])
                { if (Input.GetKeyDown(k)) down = true; if (Input.GetKey(k)) anyHeld = true; if (Input.GetKeyUp(k)) anyUp = true; }
                if (down) { PressLane(lane, now); _recDownStart[lane] = Time.time; }   // any press fires the one-shot keydown burst
                if (anyUp && !anyHeld) ReleaseLane(lane, now);   // released only when no set key is still held
            }
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

            // --- playtest controls: PINNED at the top so they're never clipped by the (growing) slider list below.
            // (They used to live at the bottom; once enough tuning sliders accumulated they overflowed the box and
            // got clipped away — that's why auto-play / manual / Cool-Miss "disappeared".) ---
            GUILayout.Label("[F4 hide]   Playtest");
            autoPlay = GUILayout.Toggle(autoPlay, autoPlay ? " Auto-play: ON" : " Auto-play: OFF — manual");
            GUILayout.Label("Manual keys  L/D/U/R = A S W D  or  Num 4 5 8 6");
            GUILayout.Label($"Force hit grade: {(forcedJudge < 0 ? "Real (timing)" : ForceJudgeLabels[forcedJudge + 1])}");
            forcedJudge = GUILayout.Toolbar(forcedJudge + 1, ForceJudgeLabels) - 1;   // 0=Real(-1), 1..4=Perfect..Miss

            // BURST OBSERVE mode status + slow-motion control (no dance/notes/music, fixed cam).
            if (observeBurstMode)
            {
                bool paused = Time.timeScale <= 0f;
                GUILayout.Label("== OBSERVE MODE ==  cam0, no dance/notes/music");
                GUILayout.Label($"Time: {(paused ? "PAUSED" : _timeScale.ToString("0.00") + "×")}   keys [ ] = slow/fast, \\ pause, = reset");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("0.1×")) SetTimeScale(0.1f);
                if (GUILayout.Button("0.25×")) SetTimeScale(0.25f);
                if (GUILayout.Button("0.5×")) SetTimeScale(0.5f);
                if (GUILayout.Button("1×")) SetTimeScale(1f);
                if (GUILayout.Button(paused ? "▶" : "❚❚")) { if (paused) SetTimeScale(_timeScale); else Time.timeScale = 0f; }
                GUILayout.EndHorizontal();
                GUILayout.Space(4);
            }
            // fire a specific combo burst on demand (tier 0..4 = 100..500COMBO). Pinned so it's always reachable.
            GUILayout.BeginHorizontal();
            GUILayout.Label("Fire combo:", GUILayout.Width(66));
            for (int t = 0; t < 5; t++) if (GUILayout.Button(((t + 1) * 100).ToString())) SpawnComboBurst(t);
            GUILayout.EndHorizontal();
            // end-of-song result burst (FINISHED.EFT — the 結算 firework). effScale≈5 is the data-derived value
            // (bumping it just raises the spawn point); the burst RANGE comes from the up-cone velocity, not size.
            if (GUILayout.Button("Fire FINISHED (result firework)")) SpawnNamedEft("FINISHED", 5f);
            GUILayout.Space(6);

            // 體型 (fat/thin): preset buttons (faithful SDO body indices) + a fine B slider — both re-shape the dancer LIVE.
            GUILayout.Label($"Body shape (thin..fat): B={_bodyShapeB:F3}  (1.00 = standard)");
            GUILayout.BeginHorizontal();
            for (int i = 0; i < BodyShapeLabels.Length; i++)
                if (GUILayout.Button(BodyShapeLabels[i]))
                { _bodyShapeB = SdoBodyShape.WeightFromIndex(i, maleBody); if (_avatar) _avatar.SetBodyShape(_bodyShapeB); }
            GUILayout.EndHorizontal();
            float newB = GUILayout.HorizontalSlider(_bodyShapeB, 0.7f, 1.4f);   // fine override (continuous)
            if (Mathf.Abs(newB - _bodyShapeB) > 1e-4f) { _bodyShapeB = newB; if (_avatar) _avatar.SetBodyShape(_bodyShapeB); }
            GUILayout.Space(6);

            // --- visual tuning sliders (scroll so any number of them can be added without hiding the controls above) ---
            _dbgScroll = GUILayout.BeginScrollView(_dbgScroll);
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
            GUILayout.Label($"Combo blue mesh intensity: {EftEffect.MeshIntensity:F1}× (AEF_3_00 visibility; 1=raw/drowned)");
            EftEffect.MeshIntensity = GUILayout.HorizontalSlider(EftEffect.MeshIntensity, 1f, 8f);
            GUILayout.Label($"Combo blue mesh width: {EftEffect.MeshWidthMatch:F2}× (300 AEF_3_00 width vs ball; tracks ball rate)");
            EftEffect.MeshWidthMatch = GUILayout.HorizontalSlider(EftEffect.MeshWidthMatch, 0.1f, 1.2f);
            GUILayout.Label($"Combo 200 mesh count: {EftEffect.MeshMax200} (AEF_3_00 count cap; official ~5-6)");
            EftEffect.MeshMax200 = Mathf.RoundToInt(GUILayout.HorizontalSlider(EftEffect.MeshMax200, 1f, 15f));
            GUILayout.Label($"Combo 300 mesh shrink: {EftEffect.MeshShrinkEnd:F2} end (lower=shrinks smaller/faster)");
            EftEffect.MeshShrinkEnd = GUILayout.HorizontalSlider(EftEffect.MeshShrinkEnd, 0.05f, 1f);
            GUILayout.Label($"Combo 300 mesh spawn W×{EftEffect.MeshStartW:F1} / H×{EftEffect.MeshStartH:F1} (→1 by end), opacity {EftEffect.MeshAlpha:F2}");
            EftEffect.MeshStartW = GUILayout.HorizontalSlider(EftEffect.MeshStartW, 1f, 4f);
            EftEffect.MeshStartH = GUILayout.HorizontalSlider(EftEffect.MeshStartH, 1f, 8f);
            EftEffect.MeshAlpha = GUILayout.HorizontalSlider(EftEffect.MeshAlpha, 0.2f, 1f);
            GUILayout.Label($"Combo 300 mesh drop: {EftEffect.MeshDropFrac:F2} (0=on ball/keeps up, 1=at ball bottom)");
            EftEffect.MeshDropFrac = GUILayout.HorizontalSlider(EftEffect.MeshDropFrac, 0f, 1.5f);
            // combo TRAIL streaks (200/300's light flares = engine 0x20000 = a unit quad stretched by animScale.y, NOT a
            // swept band; length is the scaleY channel, so only the WIDTH is tunable here — 1× = faithful)
            GUILayout.Label($"Combo trail width: {EftEffect.TrailWidthMul:F2}×  (200/300 light streaks, 1=faithful)");
            EftEffect.TrailWidthMul = GUILayout.HorizontalSlider(EftEffect.TrailWidthMul, 0.2f, 3f);
            GUILayout.Label($"Hand-trail width: {handTrailWidth:F2}×");
            handTrailWidth = GUILayout.HorizontalSlider(handTrailWidth, 0.1f, 3f);
            GUILayout.Label($"Hand-trail time: {handTrailTime:F2}s");
            handTrailTime = GUILayout.HorizontalSlider(handTrailTime, 0.05f, 1.2f);
            foreach (var rib in _handTrails) if (rib) { rib.widthMul = handTrailWidth; rib.life = handTrailTime; }
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

        private void UpdateHpBar()
        {
            if (!_trackVisible) return;   // hidden during the opening intro; SetTrackVisible(true) re-shows it
            double hp = _health?.Health ?? HealthProcessor.MaxHealth;
            float frac = Mathf.Clamp01((float)((hp - HealthProcessor.FloorHealth) / (HealthProcessor.MaxHealth - HealthProcessor.FloorHealth)));
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
