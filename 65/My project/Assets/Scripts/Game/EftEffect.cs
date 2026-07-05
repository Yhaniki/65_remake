using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>A resolved 3D-mesh effect (xmesh\list.txt entry): the Unity mesh + one texture per material submesh.
    /// Built by the host (ScreenGameplay.ResolveEftMesh) from the .MSH via <see cref="SceneLoader"/>; consumed by
    /// <see cref="EftEffect"/> when an emitter has a non-zero <see cref="EftEmitter.MeshIdx"/>.</summary>
    public sealed class EftMeshData
    {
        public Mesh Mesh;
        public Texture2D[] SubTex;   // one per mesh.subMeshCount (may contain nulls → drawn untextured/white)
        // OPTIONAL .mot-driven rigid prop (SCN0008 delta_line): when these are non-null the mesh is NOT drawn as one
        // static combined mesh — instead each submesh rides its own bone via an SdoAvatar so DELTA_LINE.MOT animates it
        // (the three colour bars extend via scale.Y). All null for the combo aef03_00 mesh path, which is untouched.
        public HrcLoader Hrc;            // skeleton for the .mot
        public MotLoader Mot;            // the embedded motion (e.g. DELTA_LINE.MOT — sequential scale-Y extend)
        public Mesh[] SubmeshMeshes;     // per-submesh meshes (verts in bone-local space, NOT baked) — one per bone-follower
        public Texture2D[] SubmeshTex;   // texture per submesh mesh (aka/ao/ki)
        public int[] SubmeshBone;        // HRC bone each submesh rides (matched by colour name)
    }

    /// <summary>
    /// Faithful runtime for a parsed <see cref="EftFile"/> — replicates the SDO 3DEFT particle engine
    /// (Particle_SpawnFromEmitter / _InitFromTemplate / _Update / _Draw, decompiled in scene/030).
    /// Fixed 20ms (50Hz) simulation; per-particle 0.5-pivot scale channels, accumulating rotation, additive draw.
    /// Effect transform = this GameObject at the pelvis-on-floor with uniform scale (30 for 100COMBO); each
    /// particle is a child whose localScale = baseSize×animScale, localRotation = Euler(accumulated channels),
    /// localPosition = integrated position — which reproduces the engine's S·R·T·Owner world matrix.
    /// </summary>
    // Run AFTER SdoAvatar.LateUpdate has posed the bones / moved the anchors — same convention as HandRibbon/
    // HeadMarker/PlayingEmoji. A bone-attached effect (SCN0015 booklight) follows _follow.position in LateUpdate;
    // at the default order 0 it raced SdoAvatar (also order 0) and frequently read the PREVIOUS frame's anchor,
    // so the orb lagged a fast-moving prop by one frame ("有時候偏離書本"). Harmless for non-following uses.
    [DefaultExecutionOrder(100)]
    public sealed class EftEffect : MonoBehaviour
    {
        const float Step = 0.02f;   // 20ms fixed step
        // NOTE: the ring's tall flare is CORRECT — Frida capture of the live online client proves the combo ring's
        // scaleY genuinely grows 0.31→2.02 (world height ~60u ≈ dancer height) and it's just very faint at the end
        // (alpha fades to ~10). An earlier RingHeightMul=0.42 hack that trimmed it was WRONG and has been removed.

        // TRAIL STREAKS (emitter flag 0x20000): the engine does NOT sweep a path-ribbon — it draws the trail as a normal
        // unit quad (±0.5, local XY) transformed by the particle's own S·R·T world matrix, EXACTLY like a world-quad.
        // The Y SCALE CHANNEL (animScale.y) stretches the quad into the visible streak; it is oriented by the rotation
        // channels (rotY/Z=0 → its local +Y ≈ world +Y, a vertical flare) and is NOT billboarded (flag lacks 0x10000).
        // GROUND TRUTH (live Frida hook, eft_trail_log.txt): the rendered Y-extent (world-matrix row-Y length) ÷ animScale.y
        // is CONSTANT over the whole life (≈ baseSize.y×effScale) — i.e. the length is channel-driven, not path-length,
        // which a swept ribbon could never reproduce. The "external" (0x40000) is just an invisible anchor that sub-emits
        // and POSITIONS the streak. Verified params: the 200/300COMBO trail = vel(0,0,0.08) life50 tex3.
        public static float TrailWidthMul = 1f;   // F4 tuning: multiplies the streak quad's width (local X); 1 = faithful
        // TRAIL COLOUR OVERRIDE: the 200/300COMBO ground-streak trail (tex3=aef_1_01) decodes to a pale PINK diffuse
        // (ch2-4 ≈ 229,196,227), but the live official client renders these "往外爆發的垂直光條" as a dimmer BLUE
        // (user-confirmed against the original — the official strips are blue and not as bright). When TrailOverride is
        // on, the trail's RGB is replaced by TrailTint×TrailBright (the data's alpha curve still drives the fade);
        // turn it off to fall back to the faithful diffuse. Tunable live via F4.
        public static bool TrailOverride = false;   // OFF = faithful pink (the engine's word[0x34] diffuse IS pink — the
        public static Color TrailTint = new Color(0.32f, 0.55f, 1f);   // official "blue strips" are the tex31 fountain's
        public static float TrailBright = 0.55f;                       // blue-violet HALO, NOT the trail, so don't recolour the trail by default
        // DEBUG: spawn only this one root slot (−1 = all) so each emitter can be captured in isolation to see its true
        // on-screen colour/shape (the 200 fountain tex31 = orange core + blue-violet halo; trail tex3 = pink; etc.).
        public static int OnlyRootSlot = -1;
        // 3D-MESH ATTACH (engine particle word[6] → xmesh\list.txt): when an emitter has MeshIdx>0 the engine draws
        // that mesh at the particle's world matrix instead of a flat quad (trail path 030_scene:5527). This is the
        // 200/300COMBO slot0 blue mesh (aef03_00 / AEF_3_00.DDS) the texture-only port was missing. On by default;
        // F4-toggleable so the old quad-only behaviour can be compared.
        public static bool EnableMesh = true;
        // DEBUG: hide everything EXCEPT the 3D meshes (AEF_3_00) so the blue mesh can be seen in isolation — verifies it
        // actually spawns/renders (vs being drowned by the bright balls). Also logs each mesh's geometry once.
        public static bool DebugMeshOnly = false;
        static bool _meshDbgLogged;
        // The 200/300COMBO "burst" world-quad (flag 0x1, tex4 = aef_1_07) is an ORANGE radial-spike sprite whose emitter
        // DIFFUSE is BLUE (154,159,255). Faithful MODULATE (texture×diffuse) muddies it to brown; the official reads as
        // a BLUE radial burst. When on, world-quads use the luminance shader (Sdo/EftAdditiveLum) so the texture is a
        // shape mask and the colour comes from the diffuse tint = the blue burst common to BOTH 200 and 300.
        public static bool LumWorldQuad = false;   // reverted to faithful MODULATE (experiment: tex4 blue burst) per user
        static Shader _lumShader;
        static Shader LumShader() { if (_lumShader == null) _lumShader = Shader.Find("Sdo/EftAdditiveLum"); return _lumShader; }
        static Shader _alphaShader;
        static Shader AlphaShader() { if (_alphaShader == null) _alphaShader = Shader.Find("Sdo/EftAlpha"); return _alphaShader; }

        // SPRITE-SHEET FLIPBOOK: a flipbook whose frames are CELLS of one atlas (SCN0010 confetti ZIPIANZ: 16 cells
        // in a 4×4 grid, all sharing one texture) animates by UV SUB-RECT, not by swapping textures. The EFT cell
        // rect is (uMin,vTop,uMax,vBot) with V=0 at the TOP (D3D); Unity samples V=0 at the bottom, so the offset's
        // V is 1−vBot. Full-cell (0,0,1,1) maps to scale(1,1)/offset(0,0) = the identity (a safe no-op).
        static bool UsableUv(Vector4 r)
        {
            if (float.IsNaN(r.x) || float.IsNaN(r.y) || float.IsNaN(r.z) || float.IsNaN(r.w)) return false;
            if (r.x < -0.01f || r.y < -0.01f || r.z > 1.01f || r.w > 1.01f) return false;
            return (r.z - r.x) > 0.01f && (r.w - r.y) > 0.01f;
        }
        static void ApplyFrameUv(Material m, Vector4 r)
        {
            if (m == null) return;
            m.mainTextureScale = new Vector2(r.z - r.x, r.w - r.y);
            m.mainTextureOffset = new Vector2(r.x, 1f - r.w);
        }

        // COMBO-BURST EXPOSURE RAMP for the 200/300 fountain balls (tex30/tex31 billboards). The official burst is
        // OVEREXPOSED — a white-hot blob at spawn — then the exposure FALLS so the ball's true colour (cyan/violet) and
        // the AEF_3_00 blue mesh riding behind it emerge, before alpha fades it out. So the ball-core additive tint is
        // multiplied by BallCoreIntensity at birth and decays to 1.0 (real colour) over the first BallCoreExpoFrac of
        // life. (A CONSTANT boost kept the balls white the whole life and drowned the blue mesh — exactly the bug the
        // user hit.) Only the balls are exposure-ramped; trails/mesh/ring/glow stay at 1.0 so their real hue shows.
        public static float BallCoreIntensity = 5f;    // peak exposure at spawn (white-hot); 1 = no overexposure
        public static float BallCoreExpoFrac = 0.3f;   // fraction of life over which exposure decays back to 1 (real colour)
        // ── POWER GAUGE (氣條 ShowTime) — head white-hot + electric-ribbon flow. See docs SDO_SHOWTIME round-4. ──
        // The online engine (sdo.bin.c FUN_0098d660 @664485/664489) draws the naga00 star + ring_l sparks WHITE-HOT via
        // additive-accumulation + D3DRS_SPECULARENABLE specular-add hard-clipped at 1.0. Now that the RIBBON renders at
        // the faithful 1× (energyStripBright 0.5 compensates the Legacy shader's 2×), the blue body no longer washes, so
        // the WHITE head layers (tex100 naga00 star + tex96 ring_l sparks) can be boosted to pop the flash at the moving
        // head WITHOUT washing the ribbon body (they overlap only at the tip). Halo (tex30) stays 1× so its colour reads.
        public static float PowerHeadGlowBright = 1.5f;
        // aef_4_02 head HALO (POWER slot1) = the BIG soft glow attached at the fill head (官方那顆「大顆的、接著集氣條」).
        // It was deliberately left "soft" (ci=1 = base 0.5×) so it stayed invisible on the black RT — give it its own
        // additive boost so the big head glow actually shows. Tunable to match the official size/brightness.
        public static float PowerHaloBright = 2.5f;
        // POWER slot3 = the CROSSING electric ribbon (initRot 90,90,0 in Y/R = perpendicular to slot2, 45° in B). It now
        // rides slot4's growth correctly (carrier-scale exclusion removed) so it is a proper second ribbon that grows
        // from the head + fades via its own alpha (255→0), not the old constant full-length band. But at 1.0 the extra
        // additive blue washed the whole gauge toward WHITE/GREY (user: 藍色集氣條變灰) — additive blue+blue clips. Keep
        // it DIM so it adds a subtle crossing crackle without desaturating the band. Raise cautiously if too faint.
        public static float PowerCrossDim = 0.6f;
        // POWER slot3's faithful initRot (90,90,0) pitches its quad FLAT (normal → world −Y) = EDGE-ON to the head-on
        // gauge camera → it renders as an invisible thin line, so only slot2 (the horizontal ribbon) shows (user: 只看
        // 到一條). To actually draw the OFFICIAL's two CROSSING ribbons we face slot3 at the camera like slot2 (0,90,·)
        // but ROLL it by this angle in the screen plane = a visible diagonal bolt crossing the horizontal slot2. This is
        // a deliberate deviation from the edge-on data (which can't show in a flat 15px viewport). 0 = flat like slot2.
        public static float PowerCrossAngle = 32f;
        // POWER ribbon DENSITY = slot4 (ribbon carrier) LIFE in ticks. Ribbons re-spawn every ~16 ticks and die WITH
        // slot4 (parent-death, so they never freeze into static bands). slot4's life sets how long each ribbon GROWS
        // (scaleZ 0→full) before it dies — a longer life keeps more GROWING (animated) ribbons overlapping = more visible
        // bands (young-short + old-long, both moving). Faithful = 20 (mostly 1-2 bands); higher = denser flowing current.
        // Independent of energyStripSpeed (which sets overall crackle SPEED). F4-tunable.
        public static float PowerRibbonLife = 30f;
        // WHITE-HOT head core (RE-verified): official = OVERSIZED white additive quads (naga00 tex100 + ring_l tex96)
        // whose half-height blankets the ±9.375 gauge band its whole life, carrier-loop-overlapped + additive-clipped to
        // white. The remake's white quads are too small (~17-45 world → leave the band → only tiny flickers). This
        // multiplies the WHITE slots' size so they blanket the band = the persistent white-hot core the user wants.
        // (The GOLD aef_4_02 slot1 is the dancer body-aura material, NOT this — left faithful.)
        // Optional size nudge for the WHITE head slots (naga00/ring_l) if the core reads too small after the faithful
        // integer-position fix. 1.0 = faithful (recommended). Stability now comes from the engine-correct INT position
        // truncation (see StepParticle), NOT a pin — so leave this at 1.0 unless the core needs to be a touch bigger.
        public static float PowerWhiteSize = 1f;
        // A/B toggle: engine-accurate 2-point channel sampler for POWER_*.EFT (氣條). ON = official (halo/star stay
        // concentrated & visible; ribbon scaleY thins monotonically). OFF = full-curve (halo over-blooms to an
        // invisible wash). Combined with PowerHaloBright, ON is what makes the big head halo appear.
        public static bool PowerEngineSampler = true;
        // DIAGNOSTIC (SDO_SHOWTIME_ISO): isolate gauge layers to see which washes the blue band white. 0=all, 1=ribbon
        // only (hide head glow), 2=head glow only (hide ribbon). Set from ScreenGameplay via the env var.
        public static int PowerIsolate;
        static int _renderDbgN;   // DIAG cap for the head-glow world-position dump
        // NOTE: the engine has NO ribbon UV scroll (FUN_0098d660 @664564 sets D3DTSS_TEXTURETRANSFORMFLAGS=DISABLE;
        // rai_05 is frameCount=1 = static UV) — the official "電流流動" is purely the overlapping re-spawn crackle +
        // scaleY pinch. A UV-scroll enhancement was tried and REMOVED per user: 官方沒有的就不要加.
        // The 200/300 slot0 AEF_3_00 blue MESH that rides each ball has a DIM texture, so at 1× it's drowned by the
        // bright fountain (the user couldn't see it). Boost its additive so the blue mesh shows once the balls' spawn
        // exposure fades. trails/ring/disk untouched. F4-tunable. NOTE: now 200-ONLY (300 uses Mesh300Intensity).
        public static float MeshIntensity = 5f;

        // ── 300-ONLY AEF_3_00 controls (fully decoupled from 200 so tuning 300 never touches the 200 ground curtain). ──
        // 200 = the mesh rides a STATIONARY ground external (non-orient parent); 300 = it rides a RISING ball (orient
        // parent). The runtime tells them apart by parent.E.Orient (see P.mesh300, set at spawn). 200 keeps the legacy
        // MeshIntensity/MeshAlpha path verbatim; 300 takes the fields below.
        public static float Mesh300Intensity = 3.2f;   // 300 additive boost (user-tuned)
        // 300 OPACITY — applied to the additive RGB ENERGY, not the alpha channel. The old code multiplied the ALPHA by
        // MeshAlpha, but with the intensity boost the result `rgb×intensity×alpha` stayed > 1 across the whole 0.2–1.0
        // slider range → hard-clipped to white → "調整透明度沒變". Scaling the RGB energy makes the slider visibly fade
        // the flame (energy = Mesh300Intensity×Mesh300Alpha, so 0.2 brings a 5× boost down to ~1× = real translucency).
        public static float Mesh300Alpha = 0.6f;   // user-tuned
        // 300 flame is STRAIGHTENED at load: the AEF_3_00 mesh geometry itself curves forward (+Z lean grows with Y);
        // 200 wants that curve (it's correct), but 300 wants a perfectly vertical flame. StraightenMesh() removes the
        // per-height-level mean-Z lean while keeping the V cross-section + width/height. Toggle off to compare raw.
        public static bool Mesh300Straight = true;
        // 300 flame FRONT/BACK offset (effect-local Z, ×effScale into world). Straightening recentred the flame on the
        // ball (z≈0) so it sits ON the orb and covers it; a NEGATIVE value pushes the flame BEHIND the ball so the orb
        // reads in front of it ("稍微往後,不要擋到球"). Positive = in front. F4-tunable; only the 300 mesh uses it.
        public static float Mesh300Z = 0.07f;   // user-tuned (slightly forward)
        // The 300 AEF_3_00 mesh rides a ball but its OWN scale channels change at a different RATE than the ball (too
        // thin at spawn, balloons tall mid-life), so its width never matches the ball. Instead it's driven by the
        // PARENT BALL's width curve (same big→small rate); MeshWidthMatch scales that to the ball's absolute width.
        public static float MeshWidthMatch = 0.18f;   // user-tuned
        // 200 spawns 15 AEF_3_00 meshes (slot1 external emit 15) but the official shows only ~5-6 → cap the count.
        public static int MeshMax200 = 6;
        // 300's mesh tracks the ball width (slow shrink); the official shrinks it FASTER and SMALLER, so multiply the
        // tracked size by lerp(1 → MeshShrinkEnd) over life. Lower = shrinks smaller. (300 only; 200 uses own curve.)
        public static float MeshShrinkEnd = 0.05f;   // user-tuned
        // 300 AEF_3_00 spec: at SPAWN it's MeshStartW× wider and MeshStartH× longer (tall vertical flame), easing to the
        // shrunk end size over life. Bottom anchored to the ball's bottom, fully vertical (no tilt). MeshAlpha dims it.
        public static float MeshStartW = 2.1f;    // start width multiplier (→1 by end) — user-tuned
        public static float MeshStartH = 6.4f;     // start length/height multiplier (→1 by end) — user-tuned
        public static float MeshAlpha = 0.8f;   // 200-ONLY AEF_3_00 opacity vs the raw alpha curve (300 uses Mesh300Alpha)
        public static float MeshDropFrac = 0.22f;  // 300 mesh vertical anchor: 0 = on the ball (keeps up), 1 = at ball bottom — user-tuned

        // ── SCN0008 magic-circle CORNER GLOW BALLS (tex42 aef_1_01_01, slot4/5/6) ───────────────────────────────────
        // The 3 bright glow flares sit at the floor TRIANGLE corners (same as the delta_line colour bars: ki=TOP,
        // aka=BL, ao=BR). In the raw EFT they are ATTACH children: slot4 rides slot1 (external, initRot 90,0,0) and
        // slot5/6 ride slot2 (the KEKKAI disc, initRot 90,-60,0). The faithful attach math (parent.pos + Euler(parent.rot)
        // × localOffset, engine Particle_Update 030_scene:7388-7401 ownSRT×parentWorld) lands slot4 at TOP correctly,
        // but the disc parent's 90°X tilt rotates slot5/6's +Z(0.5) offset DOWN into the floor (y→-20 world) AND its
        // -60°Y is a SPIN PHASE that swings their azimuth off the BL/BR corners (computed: slot5→(2,-20,-144),
        // slot6→(126,-20,62) — exactly the misplaced/below-floor balls the user saw, with BL missing). The official
        // shows them FIXED on the floor at the triangle corners and PULSING, so for this persistent effect we PIN the
        // tex42 flares to the corner positions instead of reproducing the disc's tilt/spin. Corners are in effect-LOCAL
        // EFT units (the effect transform applies its own 180°Y + effScale); y = bar height (≈1.6 world / effScale40).
        // slot4=TOP (ki), slot5=BL (aka), slot6=BR (ao) — matches the delta_line bar corners 1:1.
        public static bool PinCornerGlows = true;   // OFF = faithful (disc-tilted, two below floor); ON = fixed floor corners
        public static Vector3 CornerTop = new Vector3(-0.035f, 0.04f, -3.425f);   // ki  TOP  → world ≈ (1.4, 1.6, 137)
        public static Vector3 CornerBL  = new Vector3( 3.000f, 0.04f,  1.725f);   // aka BL   → world ≈ (-120, 1.6, -69)
        public static Vector3 CornerBR  = new Vector3(-3.025f, 0.04f,  1.750f);   // ao  BR   → world ≈ (121, 1.6, -70)
        // SCN0008 DISC spin: the kekkai disc's rot channels decode to ~0 (no spin) in the EFT, but the user confirms the
        // official disc visibly ROTATES (and the MW rim runes already self-spin via their 0.52 rotY channel). So spin the
        // disc quad in its own plane (about local Z = the floor normal after the disc's 90°X tilt; rotY would tumble it).
        // Persistent + tex69 only → never touches the corner glows (tex42) or runes (tex117). Rate is an estimate (no
        // channel data for it); tune to taste. 0 = faithful/no spin.
        public static float DiscSpinDegPerTick = 0f;   // disc does NOT rotate (user-confirmed); leave 0
        public static float DiscPulseDepth = 1f;        // exponent on the disc alpha pulse: 1=faithful (decompiled ch1 128↔255 = ×2 additive); >1 deepens it (embellishment)
        public static float LastDiscAlpha;              // DIAG: the disc's current ch1 alpha (0..255), so a phase-aligned screenshot can be picked

        // TRAJECTORY DUMP (verification): when on, every particle logs its effect-relative WORLD position over its
        // full life in the SAME format as the Frida hook (eft_online_log.txt) so my sim can be diffed against the
        // ground truth particle-by-particle. effect-relative world = p.pos × effScale (the Frida effect sits at the
        // dance spot ≈ origin, so its "world" pos IS the effect-relative displacement → directly comparable).
        public static bool DumpTraj;
        static readonly Dictionary<int, int> _trajCount = new Dictionary<int, int>();   // texIdx -> #adopted
        static System.IO.StreamWriter _trajW;
        public static void ResetTrajDump() { _trajCount.Clear(); }
        // write a line to the dump file (truncates on first call). Used for headers + each traj sample, so the data
        // survives even if the PlayMode test is marked Failed by an unrelated render-error log.
        public static void DumpLog(string s)
        {
            if (_trajW == null) { _trajW = new System.IO.StreamWriter(System.IO.Path.Combine(UnityEngine.Application.persistentDataPath, "mysim-traj.log"), false) { AutoFlush = true }; }
            _trajW.WriteLine(s);
        }

        Transform _follow; float _effScale; Camera _cam;
        Material _addMat; int _layer; float _bright;
        bool _isPower;                 // cached EffectName.StartsWith("POWER") — the ShowTime 氣條 gauge effect
        float _glowMul, _glowSpread;   // outer-glow halo: intensity (0=off) + how much bigger than the particle
        float _acc; Vector3 _spawnPos; int _maxTicks; int _tick;
        int _mesh32Count;   // # of non-orient (200 ground) AEF_3_00 meshes spawned this effect — capped at MeshMax200
        readonly List<P> _ps = new List<P>();
        readonly List<P> _pending = new List<P>();   // particles spawned this tick (added after the iteration)
        bool _reclaimedAny;                          // a dead particle was reclaimed this tick → sweep _ps
        static readonly System.Predicate<P> _isDead = p => p.dead;   // cached (no per-tick delegate alloc)
        Func<int, Texture2D> _texResolver;
        Func<int, EftMeshData> _meshResolver;   // xmesh index → 3D mesh (null when the host supplies none)
        readonly Dictionary<int, EftEmitter> _slotMap = new Dictionary<int, EftEmitter>();
        EftFile _file;                  // kept for re-spawning the root tree (persistent scene effects)
        System.Collections.Generic.HashSet<int> _dbgSlots;   // DIAG: log each scene-effect emitter slot once
        // Persistent scene background effect (magic circle, snow, aurora…): never auto-destroys; when all particles
        // die it re-spawns its roots (loops), so the original's "play once in the scene ctor, runs the whole song" is
        // reproduced. Default false = the one-shot combo-burst behaviour (auto-destroys when spent). Set before Init.
        public bool Persistent;
        // LOOP (hiteft3D HOLD burst = HIT_LONG.EFT): like Persistent, this effect self-sustains — but WITHOUT the
        // scene-effect side-paths (RespawnTree / SceneEftRenderCatalog tuning). It keeps its negative-life emitters
        // (EftEmitter.Loop) re-initing forever and re-fires their trigType-3 children, so the hold glow is continuous
        // for the whole (multi-second) hold; it never auto-destroys — the host tears it down on release. The one-shot
        // head-flash emitter (positive life, Loop=false) still fires exactly once. Set before Init.
        public bool Loop;
        public string EffectName;
        // SORTING ORDER for this effect's transparent renderers (0 = engine default / distance-sorted). Combo/scene
        // effects render on the perspective stage layer where order is irrelevant, but the hiteft3D hit burst (drawn in
        // the ORTHOGRAPHIC play field at order 6) and the ShowTime board-burst (a LATE overlay pass over notes/HUD,
        // sdo.bin FUN_00402d20) both need explicit ordering. Applied to every particle's renderer(s) as they spawn.
        public int SortingOrder;
        // MOTION SCALE (1 = faithful). AU_HIT rises ~20× its own size, so at an effScale big enough to SEE the spark it
        // would shoot far off-screen. For the hiteft3D burst in the orthographic play field this damps only the
        // per-particle velocity/gravity integration (not size/rotation/colour), so the burst can be scaled up for a
        // visible footprint while its rise stays a small flick near the receptor. Combos/scene effects leave it at 1.
        public float MotionScale = 1f;
        // TICK-RATE SPEED (1 = faithful). Multiplies the accumulated dt so the ENTIRE effect (life curves, child
        // re-spawn cadence, particle motion) runs faster/slower. Unlike MotionScale (velocity only) this speeds up the
        // crackle/flicker too. Used for the ShowTime window's side EDGE4 lightning columns (user: 電流太慢, ×2).
        public float SpeedMul = 1f;
        // RGB TINT multiplied into every particle's additive colour (1,1,1 = faithful). The hiteft3D note-hit (hit.eft)
        // stores WHITE note-arrow textures; the official 3D skin renders them GOLD/yellow via a play-time diffuse tint,
        // so the host sets this to gold. Combos/scene effects leave it white (their real per-channel hue is used as-is).
        public Color Tint = Color.white;
        // FAITHFUL ALPHA (hiteft3D hit burst): route EVERY particle through Sdo/EftAlpha (1× premultiplied, linear in
        // ch1, gamma-space) instead of Legacy Particles/Additive. The legacy shader does 2×tint×tex with SrcAlpha
        // blending, so srcAlpha = sat(2×ch1/255): any ch1 ≥ 128 clips to identical max brightness — HIT.EFT's authored
        // 92ms halo ramp-in snaps full in ~18ms and the 45ms fade-out shows only its last ~23ms = the "僵硬快閃".
        // The official engine adds tex×diffuse×(ch1/255) LINEARLY (TEXTUREFACTOR, SRCALPHA|ONE) — exactly Sdo/EftAlpha.
        public bool FaithfulAlpha;
        // Explicit billboard camera: when set, particles face THIS camera instead of the auto-found perspective scene
        // camera. The ShowTime board-burst renders through a dedicated near-plane camera (eye z-1000), so its billboards
        // must orient to that camera, not the stage camera. Must be a PERSPECTIVE camera (the billboard math assumes it).
        public Camera BillboardCam;
        static Texture2D _glow;

        sealed class P
        {
            public EftEmitter E;
            public int life, life0, spawnDelay, startDelay;
            public Vector3 rot, pos, baseSize, vel;   // vel = per-particle velocity, randomly TILTED by posSpread (fan-out)
            public Vector3 liveScale;                  // baseSize × animScale this tick (cached for children to read in attach)
            public Transform tr; public Material mat;
            public Material[] meshMats;   // 3D-mesh submesh materials (non-null only for mesh particles); tinted as a group
            public EftMotMesh meshMot; public float meshMotMax;   // .mot-rigid prop (delta_line): drive its .mot ONCE over the particle's life (no auto-loop)
            public Transform glowTr; public Material glowMat;   // outer-glow halo child (null when glow off)
            public bool ring, orient, started, invisible, isTrail, isMesh;
            public bool mesh300;   // AEF_3_00 mesh riding a RISING ball (orient parent) = the 300 case → 300-only params
            public bool isBallCore;   // 200/300 burst ball billboard (tex30/31) → optional BallCoreIntensity boost in StepParticle
            public float renderRgbMul = 1f, renderAlphaMul = 1f;
            public Vector3 renderScaleMul = Vector3.one;
            // ATTACH-TO-PARENT (engine word[0x37]): when attach!=0 this particle RIDES `parent` — each frame its world
            // position = parent.pos + (parent.rot × its own local drift). 200/300 slot0 mesh locks to its parent this way.
            // lockToParent: when the parent itself MOVES (300's rising orb), suppress the own drift so the mesh stays
            // welded to that orb (one ball, one mesh); a STATIONARY parent (200's ground external) keeps the drift so the
            // mesh fans outward into the ground curtain.
            public int attach; public P parent; public bool lockToParent;
            public bool root;   // a ROOT particle (no parent) — for a Persistent scene effect it loops (never dies)
            public bool dead;   // reclaimed (GameObject destroyed, removed from _ps) — the P object lingers only so live children can still read its frozen pos/rot/liveScale
            public float azim;   // this particle's random cone azimuth (rad); children inherit it to rotate their velocity
            public float velScale;   // per-particle constant velocity-scale jitter = 1.0±word[0x1f4] (engine param_1[500])
            public Vector3 turbAxis;   // fixed-at-birth turbulence rotation axis (perp to initial vel) so kicks accumulate
            public int turbN; public float turbAng;   // DEBUG: turbulence fire count + accumulated angle
            public int bornTick;   // DEBUG: global tick when this particle was spawned (to measure spawn-time spread)
            public bool turbOff;   // one-shot velocity-turbulence (TurbMode==1) already fired → disabled
            public int dumpN;   // trajectory-dump per-texture index (0 = not adopted)
            public bool[] kidSpawned;   // per E.Children: has this particle sub-emitted that child yet?
            // TEXTURE FLIPBOOK state: this particle cycles its sprite through frameTex[] every frameHold ticks (engine
            // word[0x43]/0x45/0x42). The texture SWAP is what makes a FINISHED spark visibly pop in/out = the flicker.
            public Texture2D[] frameTex; public int frameCount, frameHold; public bool frameLoop;
            public int frame, frameCounter;
        }

        public void Init(EftFile file, float effScale, Transform follow, Func<int, Texture2D> texResolver,
                         Material addMat, int layer, float bright, float glow = 0f, float glowSpread = 0.6f,
                         Func<int, EftMeshData> meshResolver = null)
        {
            _effScale = effScale; _follow = follow; _texResolver = texResolver; _meshResolver = meshResolver; _addMat = addMat; _layer = layer; _bright = bright;
            _glowMul = glow; _glowSpread = glowSpread; _file = file;
            _isPower = EffectName != null && EffectName.StartsWith("POWER");   // ShowTime 氣條 gauge (head white-hot + ribbon flow)
            _spawnPos = transform.position;
            if (transform.localScale == Vector3.one) transform.localScale = Vector3.one * effScale;   // caller may preset a non-uniform world scale
            if (_glow == null) _glow = MakeGlow();

            foreach (var em in file.Emitters) _slotMap[em.Slot] = em;
            SpawnRoots();
            _maxTicks = 0;
            foreach (var em in file.Emitters) _maxTicks = Mathf.Max(_maxTicks, em.StartDelay + em.SpawnDelay + Mathf.Abs(em.Life0) + 20);
            if (DumpTraj)
                foreach (var em in file.Emitters)
                {
                    var c0 = em.Ch[0];
                    DumpLog($"   EMITTER slot{em.Slot} tex{em.TexIdx} flags=0x{em.Flags:X} vel=({em.Vel.x:F3},{em.Vel.y:F3},{em.Vel.z:F3})" +
                            $" posSpread=({em.PosSpreadX},{em.PosSpreadZ}) lifeJit={em.LifeJit} cone=({em.ConeInner:F2},{em.ConeMag:F2})" +
                            $" life0={em.Life0} emit={em.Emit}");
                    DumpLog($"      ch0(vel) cnt={c0.Count} min={c0.Min:F3} max={c0.Max:F3} " +
                            $"Scale@[.05,.3,.6,.9]=[{c0.Scale(.05f):F2},{c0.Scale(.3f):F2},{c0.Scale(.6f):F2},{c0.Scale(.9f):F2}] " +
                            $"Decoded@[.05,.5,.9]=[{c0.Decoded(.05f):F2},{c0.Decoded(.5f):F2},{c0.Decoded(.9f):F2}]");
                    DumpLog($"      TURB prob={em.TurbProb} mode={em.TurbMode} mask=0x{em.TurbMask:X} " +
                            $"rndA=({em.TurbRndA.x:F1},{em.TurbRndA.y:F1},{em.TurbRndA.z:F1}) rndB=({em.TurbRndB.x:F1},{em.TurbRndB.y:F1},{em.TurbRndB.z:F1})");
                }
            Flush();
        }

        // Spawn the EFT's ROOT emitters; each spawned particle then sub-emits its own children at their at-times.
        // Factored out of Init so a persistent scene effect can re-spawn its whole tree once it has fully died (loop).
        private void SpawnRoots()
        {
            for (int k = 0; k < _file.RootSlots.Length; k++)
                if (_slotMap.TryGetValue(_file.RootSlots[k], out var rem))
                {
                    if (OnlyRootSlot >= 0 && _file.RootSlots[k] != OnlyRootSlot) continue;   // DEBUG slot isolation
                    SpawnEmitter(rem, _file.RootDelays[k], true);
                }
        }

        // Persistent loop: every particle is dead, so tear their GameObjects down and start the tree over.
        private void RespawnTree()
        {
            foreach (var p in _ps)
            {
                if (p.glowTr) Destroy(p.glowTr.gameObject);
                if (p.tr) Destroy(p.tr.gameObject);
            }
            _ps.Clear();
            _tick = 0;
            SpawnRoots();
            Flush();
        }

        // spawn EMIT particles from an emitter. 0x40000 = external geometry: the engine's quad draw skips it, but it
        // still sub-emits its children — so spawn it as an INVISIBLE carrier (no quad) so its children appear.
        void SpawnEmitter(EftEmitter em, int extraDelay, bool isRoot, Vector3 parentPos = default, float parentAzim = 0f, Vector3 parentVel = default, P parentP = null)
        {
            var tex = em.HasTex ? _texResolver(em.TexIdx) : null;
            // resolve the flipbook frame textures once for this emit batch (all spawned particles share the same
            // Texture2D objects; only the per-particle CURRENT frame index differs). frame 0 == em.TexIdx == tex.
            Texture2D[] frames = null;
            if (em.HasTex && em.FrameCount > 1)
            {
                frames = new Texture2D[em.FrameCount];
                for (int i = 0; i < em.FrameCount; i++) frames[i] = _texResolver(em.FrameTex[i]);
            }
            // Emit=0 means this slot produces no particles in the original engine (FIRE3 slot1: billboard tex84,
            // Emit=0 → never spawns despite being in the trigger tree). Only carriers (0x40000) keep ≥1 phantom
            // particle as a position anchor so their children get correct world coordinates.
            bool isCarrierSlot = (em.Flags & 0x40000) != 0;
            int emit = em.Emit > 0 ? em.Emit : (isCarrierSlot ? 1 : 0);
            if (emit == 0) return;
            for (int n = 0; n < emit; n++) Spawn(em, tex, frames, extraDelay, isRoot, parentPos, parentAzim, parentVel, n, emit, parentP);
        }

        void Spawn(EftEmitter em, Texture2D tex, Texture2D[] frames, int extraDelay, bool isRoot, Vector3 parentPos, float parentAzim, Vector3 parentVel, int emitIdx, int emitTotal, P parentP = null)
        {
            // PER-PARTICLE SIZE JITTER (engine InitFromTemplate ~135379): each spark's baseSize is scaled by an
            // INDEPENDENT RandScaleJitter per axis (word[0x1f8/9/a]); a negative Y/Z word links it to X (uniform).
            // This is the "每顆初始大小不同" — 400's tex96 X-cross has (0.2,0.2,0) so each cross is ±20% and a different
            // X/Y stretch; 200/FINISHED have 0 (all same size). Without it my X-cross was uniform = "still wrong".
            float jx = RandScaleJitter(em.SizeJit.x);
            float jy = em.SizeJit.y >= 0f ? RandScaleJitter(em.SizeJit.y) : jx;
            float jz = em.SizeJit.z >= 0f ? RandScaleJitter(em.SizeJit.z) : jx;
            Vector3 bs = new Vector3(em.BaseSize.x * jx, em.BaseSize.y * jy, em.BaseSize.z * jz);
            int life0 = Mathf.Max(1, Mathf.Abs(em.Life0));
            // POWER ribbon DENSITY lever: slot4 is the invisible ribbon CARRIER — its life drives how long each ribbon
            // grows (scaleZ 0→full) and, via the parent-death kill, how long the ribbon lives before it re-spawns. The
            // ribbon re-spawns every ~16 ticks, so a longer carrier life = more GROWING (never-frozen) ribbons overlap =
            // more visible bands (young-short + old-long). Faithful = 20; higher = denser current. F4-tunable.
            if (_isPower && em.Slot == 4) life0 = Mathf.Max(1, Mathf.RoundToInt(PowerRibbonLife));
            // PER-PARTICLE LIFE JITTER (engine InitFromTemplate: life += rand%j − rand%j). Without it every particle
            // of an emitter lives the same time → flies to the SAME height; the jitter (e.g. 200 tex31 j=10 → life
            // 40-60, 400 tex96 j=20 → life 30-70) is what makes a spray reach MANY different heights.
            if (em.LifeJit > 0)
            {
                int j = Mathf.Min(em.LifeJit, life0 - 1);
                if (j > 0) life0 = Mathf.Max(1, life0 + UnityEngine.Random.Range(0, j + 1) - UnityEngine.Random.Range(0, j + 1));
            }
            bool isTrail = (em.Flags & 0x20000) != 0;
            // SPAWN SCATTER (engine Particle_RandomConeVelocity_004bebc0): take the cone axis (+Z), rotate it by random
            // ±ConeAng{X,Y,Z}/2 degrees about each axis, then scale by a random magnitude in [ConeInner,ConeMag]. The
            // ANGULAR spread is what makes it 3-D: confetti (360,360,0) → a SPHERICAL shell (pieces high AND low);
            // combo sprays (0,360,0) → a horizontal ring (Y only) — IDENTICAL to the old hard-coded ring, so combos
            // don't regress. The old code dropped ConeAngX, so confetti lost their vertical spread (one mid band).
            Vector3 coneOff = Vector3.zero;
            float myAzim = parentAzim;   // default: inherit the parent's cone azimuth
            if (em.ConeMag > 0f)
            {
                float mag = UnityEngine.Random.Range(em.ConeInner, em.ConeMag);
                float rx = UnityEngine.Random.Range(-em.ConeAngX * 0.5f, em.ConeAngX * 0.5f);
                float ry = UnityEngine.Random.Range(-em.ConeAngY * 0.5f, em.ConeAngY * 0.5f);
                float rz = UnityEngine.Random.Range(-em.ConeAngZ * 0.5f, em.ConeAngZ * 0.5f);
                coneOff = (Quaternion.Euler(rx, ry, rz) * Vector3.forward) * mag;
                myAzim = ry * Mathf.Deg2Rad;   // the Y rotation IS this particle's cone azimuth (children/velocity inherit it)
            }
            // velocity = posSpread tilt, then (for SPRAYS) ROTATE into the cone azimuth so a fountain fans OUT instead of
            // rising in lockstep. No-op for vertical fountains (rotating a +Y vector around Y does nothing). TRAILS are
            // handled separately just below (their velocity uses an independent random yaw, not the cone azimuth).
            Vector3 tiltedVel = TiltVel(em.Vel, em.PosSpreadX, em.PosSpreadZ);
            // A plain (non-attach) trail gets an INDEPENDENT random yaw so its streak faces/drifts a random way (the
            // 200-style ground streaks). But an ATTACH-MODE mesh trail (200/300 slot0, word[0x37]≠0) has ZERO
            // randomisation in the data — posSpread=cone=rotJit=initRot=0; it RIDES its parent and the spread comes
            // ENTIRELY from the parent's scatter (200 parent cone 0–0.1 on the floor; 300 parent posSpread 34/38 fanning
            // up). Forcing a random yaw here was the "亂飛" — each mesh spun off in a random direction instead of riding
            // its orb. So: random yaw only for non-attach trails; attach trails stay upright (yaw 0).
            float trailYaw = (isTrail && em.AttachMode == 0) ? UnityEngine.Random.Range(0f, 360f) : 0f;
            float velYawDeg = isTrail ? trailYaw : myAzim * Mathf.Rad2Deg;
            if (velYawDeg != 0f) tiltedVel = Quaternion.AngleAxis(velYawDeg, Vector3.up) * tiltedVel;
            // SUB-EMIT VELOCITY INHERITANCE (engine SpawnChildEffect→ApplyVelocity, flag word[0x39]): a child ADDS its
            // parent's current velocity. This is the 400 X-cross "lift" — tex96 is sub-emitted by a RISING ring
            // (vel.y=0.008), inheriting that +0.008 so it reaches the official height (slot3 0.035→0.043, slot4
            // 0.020→0.028, matching the captured birth vel 0.04/0.03) instead of clustering low (the "積在下面" bug).
            // Flag-gated, so 200's trails (off) and FINISHED's sparks (off, else the root's −0.3 would drag them down)
            // are untouched.
            if (em.InheritVel) tiltedVel += parentVel;
            // 3D-MESH ATTACH — the engine draws the word[6] mesh ONLY when flag 0x20000 is set (decompiled render
            // FUN @0x4bd...: `if (word[1] & 0x20000) { if (mesh[word6]) drawMesh; else drawStretchedQuad; }`). That same
            // bit is our `isTrail`, so mesh and stretched-quad are the two outcomes of one flag: mesh if it resolves,
            // else the trail quad. Confirmed: 200/300COMBO slot0 (mesh 32) flags=0x20001 → mesh; KIKKAI_3 (SCN0008,
            // mesh 172 delta_line) flags=0x20001 → mesh; but stagelightb/bgl (mesh 101) AND 100/400/500 (mesh 100/101)
            // have flags WITHOUT 0x20000 → the engine ignores word[6] and draws the flat tex0 BILLBOARD (the real stage
            // light beam / combo billboard). The old MeshIdx==32 || Persistent heuristic mis-drew the column drums.
            bool meshOk = EnableMesh && _meshResolver != null && isTrail;
            EftMeshData md = meshOk ? _meshResolver(em.MeshIdx) : null;
            bool isMesh = md != null && md.Mesh != null;
            // DIAG (persistent scene effects only): one line per emitter slot so the Player.log shows whether each
            // child (delta_line mesh 172, MW tex 117, the colour bars) actually spawned + resolved its mesh/texture.
            if (Persistent) { _dbgSlots ??= new System.Collections.Generic.HashSet<int>();
                if (_dbgSlots.Add(em.Slot)) {
                    bool texOK = em.HasTex && _texResolver != null && _texResolver(em.TexIdx) != null;
                    string line = $"[scene-eft-dbg] {EffectName} slot{em.Slot} root={isRoot} mesh={em.MeshIdx}(isMesh={isMesh}) tex={(em.HasTex ? em.TexIdx : -1)} texOK={texOK} isTrail={isTrail} worldQuad={(!em.IsRing && !em.Orient)} orient={em.Orient} flags=0x{em.Flags:X} invisible={((em.Flags & 0x40000) != 0)} base=({em.BaseSize.x:F1},{em.BaseSize.y:F1},{em.BaseSize.z:F1}) cone=(mag{em.ConeMag:F2},{em.ConeAngX:F0}x{em.ConeAngY:F0}) initRot=({em.InitRot.x:F0},{em.InitRot.y:F0},{em.InitRot.z:F0}) life={em.Life0} trig={em.TrigType} kids={em.Children.Count}";
                    Debug.Log(line);
                    if (EffectName != null && EffectName.StartsWith("POWER")) { try { System.IO.File.AppendAllText(@"H:/65_remake/gauge_dbg.txt", line + "\n"); } catch { } }
                } }
            // 300 = the AEF_3_00 mesh rides a RISING ball (orient parent); 200 = it rides a stationary ground external.
            // This is the single switch that keeps the two tiers' tuning fully independent (straighten + intensity +
            // opacity all branch on it), so changing 300 never touches the 200 ground curtain.
            bool mesh300 = isMesh && em.MeshIdx == 32 && parentP != null && parentP.E.Orient;
            // 200's AEF_3_00 meshes ride a NON-orient ground EXTERNAL (slot1 emit 15 → 15 meshes), but the official
            // shows only ~5-6. Cap the count for the non-orient (200) case; the excess render nothing. 300's meshes
            // ride orient BALLS (12) and are NOT capped.
            bool capMesh = false;
            if (isMesh && em.MeshIdx == 32 && (parentP == null || !parentP.E.Orient))   // 200COMBO ground cap only; not scene meshes
            {
                if (_mesh32Count >= MeshMax200) capMesh = true;
                else _mesh32Count++;
            }
            var renderTuning = Persistent ? SceneEftRenderCatalog.Find(EffectName, em.Slot, em.TexIdx) : SceneEftRenderCatalog.Identity;
            var p = new P
            {
                E = em,
                life = life0, life0 = life0,
                kidSpawned = em.Children.Count > 0 ? new bool[em.Children.Count] : null,
                spawnDelay = em.SpawnDelay + Mathf.Max(0, extraDelay),
                // UNIFORM stagger — exactly the engine (InitFromTemplate 030_scene:6458-6460: `startDelay = rand %
                // startDelay`). The X-cross spawn must be EVENLY distributed across the window (平均), not mid-peaked.
                // (An earlier triangular hack peaked it in the middle — wrong; the engine modulo is uniform.)
                startDelay = em.StartDelay > 0 ? UnityEngine.Random.Range(0, em.StartDelay) : 0,
                // sub-emitted particles inherit the PARENT's position (engine: child[0x17..19] += parent matrix
                // translation). Without this, trails sub-emitted by the scattered externals all spawn at the origin
                // and (with their own +Z velocity) fly off backward together = the "light flying backward" artifact.
                // ATTACH-MODE children (200/300 mesh trail, word[0x37]≠0) ride their parent: their `pos` is a LOCAL
                // offset and the parent's CURRENT position is added every frame in StepParticle. Non-attach children
                // bake the parent's spawn position in once (the 100/400 InheritPos behaviour), exactly as before.
                // PERSISTENT scene effects HONOUR the InheritPos flag (word 0x38): the SCN0008 delta_line (slot0,
                // InheritPos=0) must NOT add its external parent slot1's pos (0,0.5,0)×effScale40 = +20 world, which
                // floated the 3 colour lines at waist height (Frida: the engine spawns delta_line at world (0,0,0)).
                // Combos keep the always-inherit remake hack (non-Persistent), so their trails are untouched.
                pos = (em.AttachMode != 0 ? em.Pos
                        : (Persistent && !em.InheritPos) ? em.Pos
                        : em.Pos + parentPos) + coneOff,
                attach = em.AttachMode, root = isRoot,
                parent = em.AttachMode != 0 ? parentP : null,
                // a mesh riding a MOVING parent welds to it (300's rising orb → no drift); riding a STATIONARY parent it
                // keeps its drift (200's ground external → fans out). parentVel is the parent's spawn velocity.
                // Only MESH particles lock; non-mesh (billboard/world-quad) always integrate their OWN velocity on top of
                // the parent position — official engine data confirms fire3 slot2 rises at vel.y=0.05 despite attach=1.
                lockToParent = isMesh && em.AttachMode != 0 && parentVel.sqrMagnitude > 1e-8f,
                // per-particle velocity, TILTED by a random angle within posSpread (engine: Math_RotateAroundAxis of
                // vel by rand(±posSpreadX°, ±posSpreadZ°) at spawn). This is what fans a spray OUT into a fountain —
                // 200/300's billboards (vel up + posSpread 34/38) must scatter, not rise in lockstep. TRAILS use their
                // OWN template velocity too: the 200/300 trail = vel(0,0,0.08), so each trail creeps +Z and sweeps a
                // short ribbon. (An earlier hack inherited the PARENT's velocity → 200's trails, hung off STATIONARY
                // externals, got vel 0 and never streaked. The real fix was inheriting parent POSITION below, not vel.)
                vel = tiltedVel,
                azim = myAzim,   // pass this particle's cone azimuth to its sub-emitted children
                velScale = RandScaleJitter(em.ScaleJit),   // per-particle 1.0±word[0x1f4] velocity-scale spread
                bornTick = _tick,
                turbAxis = em.TurbMask != 0 ? RandPerp(tiltedVel) : Vector3.zero,   // fixed crackle axis (if turbulent)
                // rotation starts at the template's initial rotation + jitter; the update accumulates the channels
                // onto it. This is what makes aef_1_07 lie FLAT (its [0x1a]=90°) and spin randomly (Y jitter 360°).
                // POWER slot3: its faithful (90,90,0) pitch renders edge-on/invisible in the flat gauge → re-face it at
                // the camera like slot2 (0,90,·) but rolled by PowerCrossAngle → a visible diagonal CROSSING ribbon.
                rot = (_isPower && em.Slot == 3 ? new Vector3(0f, 90f, PowerCrossAngle) : em.InitRot) + new Vector3(
                    UnityEngine.Random.Range(-1f, 1f) * em.RotJit.x,
                    UnityEngine.Random.Range(-1f, 1f) * em.RotJit.y,
                    UnityEngine.Random.Range(-1f, 1f) * em.RotJit.z),
                baseSize = bs, liveScale = bs,   // liveScale refreshed each tick; default bs so children see non-zero on first spawn
                ring = em.IsRing,
                invisible = (em.Flags & 0x40000) != 0 || capMesh,   // capped excess 200 meshes render nothing
                isTrail = isTrail,
                isMesh = isMesh,
                mesh300 = mesh300,
                // billboards (0x10000) face the camera via their world matrix (rotation from the template initRot +
                // channels — e.g. aef_1_07's 90° = flat). TRAILS are world-quads oriented by their euler channels and
                // are NOT billboarded (the flag lacks 0x10000), so force orient off for them too.
                orient = em.Orient && !isTrail,
                // the 200/300 fountain balls (oriented billboard, tex31=AEF_4_03 / tex30=AEF_4_02) — the only emitters
                // BallCoreIntensity boosts (their texture core is near-white, so a brighter additive enlarges the
                // white-hot blob; the burst camera's gamma-clip is the main mechanism, this is just an extra lever).
                isBallCore = em.Orient && !isTrail && !isMesh && (em.TexIdx == 30 || em.TexIdx == 31),
                renderRgbMul = renderTuning.RgbMul,
                renderAlphaMul = renderTuning.AlphaMul,
                renderScaleMul = renderTuning.ScaleMul,
                // texture flipbook (only when this emitter has >1 frame); frame 0 is the initial texture already set
                frameTex = frames, frameCount = frames != null ? em.FrameCount : 1,
                frameHold = em.FrameHold, frameLoop = em.FrameLoop,
            };
            // Face the flare the SAME way its head drifts (trailYaw) → quad faces the way it moves, exactly like the
            // engine (head drift ∥ axZ, the quad's local +Z). Spinning about the vertical +Y stretch axis leaves axY
            // untouched, so the streak stays a vertical flare; only its azimuth (axX/axZ) varies per particle.
            // EXCEPT the SCN0008 delta_line: its emitter flags carry 0x20000 (trail) ALONGSIDE MeshIdx=172, so it is
            // mis-classified as a trail — but it is a .mot-rigid MESH whose azimuth MUST stay the emitter's InitRot.y
            // (60°/120°) so EftMotMesh's owner (= effect 180° × particle InitRot.y) lands at the captured 240°/300°.
            // The random trailYaw scattered the 3 colour bars to a new angle every spawn. Combos (non-Persistent) keep
            // the trail-yaw (their aef03_00 trail relies on it).
            if (isTrail && !(Persistent && p.isMesh)) p.rot.y = trailYaw;

            // ENGINE-FAITHFUL INT position AT SPAWN: truncate all 3 axes so every re-spawned head generation clusters at
            // the SAME spot (the fill head) = a STABLE white-hot core. (A round-6 experiment kept X float to add depth
            // variety, but the user reported it as "頭光前後跳位置" — the depth scatter reads as the core hopping
            // forward/back, which is wrong. Reverted: stability wins.) The "many stacked / random flash" look must come
            // from the OVERLAPPING GENERATIONS at different ages (size 0.17→3.6 + alpha phase) + random startDelay(0..9)
            // staggering, NOT from position scatter. ring_l sparks (tex96) EXCLUDED: they SHOULD drift OUT around core.
            if (_isPower && em.TexIdx != 96) { p.pos.x = (float)(int)p.pos.x; p.pos.y = (float)(int)p.pos.y; p.pos.z = (float)(int)p.pos.z; }

            // DIAGNOSTIC: isolate the POWER gauge layers (still SPAWN so the trigger chain 0→1→4→2/3 is intact, just
            // render invisible). 1 = ribbon only (hide halo/star/sparks 1/5/6); 2 = head glow only (hide ribbon 2/3).
            if (_isPower && PowerIsolate != 0)
            {
                bool glow = em.Slot == 1 || em.Slot == 5 || em.Slot == 6;
                bool ribbon = em.Slot == 2 || em.Slot == 3;
                if ((PowerIsolate == 1 && glow) || (PowerIsolate == 2 && ribbon)) p.invisible = true;
            }

            var go = new GameObject(p.isMesh ? "eft-mesh" : p.isTrail ? "eft-trail" : em.IsRing ? "eft-ring" : "eft-bb");
            go.transform.SetParent(transform, false);
            go.layer = _layer;
            if (p.invisible)
            {
                // 0x40000 external carrier: no geometry, exists only to inherit a position + sub-emit its trail child.
            }
            else if (p.isMesh && md.Mot != null && md.Hrc != null && md.SubmeshMeshes != null && md.SubmeshMeshes.Length > 0)
            {
                // .MOT-DRIVEN RIGID PROP (SCN0008 delta_line, xmesh 172): the 3 colour bars (aka/ao/ki). Posed by
                // EftMotMesh, which replicates the engine's EXACT FK (D3DX row-major local = R·S·T, world = local·parent;
                // verified 1:1 against a Frida capture of sdo_stand_alone) and bakes each bar's bone-local verts by its
                // bone's world matrix per frame. `go` (this particle, under the effect) supplies the OWNER (effect ×
                // particle world); each bar mesh sits at identity under go. The earlier SdoAvatar/Transform approach
                // scattered the bars (Unity column-major can't do the engine's rotate-then-scale; its quat→matrix was
                // also the transpose of D3DX) — that's why this uses a dedicated row-major component.
                var mm = go.AddComponent<EftMotMesh>();
                mm.Setup(md.Hrc, md.Mot);
                int subN = md.SubmeshMeshes.Length;
                var mats = new Material[subN];
                for (int s = 0; s < subN; s++)
                {
                    var mat = _addMat != null ? new Material(_addMat) : new Material(Shader.Find("Sprites/Default"));
                    Texture2D st = (md.SubmeshTex != null && s < md.SubmeshTex.Length) ? md.SubmeshTex[s] : null;
                    mat.mainTexture = st != null ? st : _glow;
                    mats[s] = mat;
                    var child = new GameObject("eft-mesh-sub" + s);
                    child.transform.SetParent(go.transform, false);   // IDENTITY under go — EftMotMesh writes posed verts into the mesh
                    child.layer = _layer;
                    var srcMesh = md.SubmeshMeshes[s];
                    var src = srcMesh.vertices;                         // original bone-local verts (the baker's source)
                    var bakeMesh = new Mesh { name = "delta-bake" + s };
                    bakeMesh.vertices = src; bakeMesh.uv = srcMesh.uv; bakeMesh.triangles = srcMesh.triangles; bakeMesh.RecalculateBounds();
                    child.AddComponent<MeshFilter>().mesh = bakeMesh;
                    var cmr = child.AddComponent<MeshRenderer>();
                    cmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; cmr.receiveShadows = false;
                    cmr.sharedMaterial = mat;
                    int bone = (md.SubmeshBone != null && s < md.SubmeshBone.Length) ? md.SubmeshBone[s] : -1;
                    if (bone >= 0) mm.AddBar(bone, bakeMesh, src);
                }
                p.meshMats = mats; p.mat = mats[0];
                // The sliding window comes from the engine's trigType-3 re-fire (a 2nd instance 40 ticks behind, see
                // StepParticle), NOT from a per-mesh phase. Each instance just plays its .mot ONCE over its life.
                p.meshMot = mm; p.meshMotMax = md.Mot.MaxTime;
                if (!_meshDbgLogged) { _meshDbgLogged = true; Debug.Log($"[mesh-dbg] slot{em.Slot} meshIdx={em.MeshIdx} MOT-rigid(row-major FK) subN={subN} bones=[{string.Join(",", md.SubmeshBone ?? new int[0])}] motMaxTime={md.Mot.MaxTime} effScale={_effScale}"); }
            }
            else if (p.isMesh)
            {
                // 3D-MESH (engine word[6] mesh): draw the resolved xmesh mesh at the particle's world matrix (the
                // localPosition/Rotation/Scale below reproduce the engine's S·R·T·Owner). One additive material per
                // material submesh (clone of the combo additive mat + that submesh's DDS), so aef03_00's blue
                // textures show and fade by the trail's alpha channel. NOT billboarded (trail flag lacks 0x10000).
                // 300 uses a STRAIGHTENED clone (forward-lean removed) so its flame is perfectly vertical; 200 keeps the
                // raw curved mesh (the user confirmed 200 is correct). Cached per source mesh so it's built once.
                Mesh useMesh = (p.mesh300 && Mesh300Straight) ? StraightenMesh(md.Mesh) : md.Mesh;
                go.AddComponent<MeshFilter>().mesh = useMesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
                int subN = Mathf.Max(1, useMesh.subMeshCount);
                var mats = new Material[subN];
                for (int s = 0; s < subN; s++)
                {
                    var mat = _addMat != null ? new Material(_addMat) : new Material(Shader.Find("Sprites/Default"));
                    Texture2D st = (md.SubTex != null && s < md.SubTex.Length) ? md.SubTex[s] : null;
                    // a mesh emitter with a REAL texture flipbook (EDGE4's tornado + rai_00..03 lightning) samples the
                    // EMITTER's animated texture, not the mesh's baked DDS — start submesh 0 on flipbook frame 0; the
                    // flipbook advance in StepParticle (p.mat = mats[0]) cycles it from there.
                    if (frames != null && tex != null && s == 0) st = tex;
                    mat.mainTexture = st != null ? st : _glow;
                    mats[s] = mat;
                }
                mr.sharedMaterials = mats;
                p.meshMats = mats; p.mat = mats[0];
                if (!_meshDbgLogged)
                {
                    _meshDbgLogged = true;
                    var bnd = md.Mesh.bounds;
                    var texNames = "";
                    if (md.SubTex != null) foreach (var st in md.SubTex) texNames += (st != null ? st.name + "(" + st.width + "x" + st.height + ")" : "null") + ",";
                    Debug.Log($"[mesh-dbg] slot{em.Slot} meshIdx={em.MeshIdx} subN={subN} meshBounds c={bnd.center} size={bnd.size} baseSize={em.BaseSize} effScale={_effScale} subTex=[{texNames}]");
                }
            }
            else if (p.isTrail)
            {
                // STRETCHED-QUAD STREAK (flag 0x20000) — a centred unit quad stretched by animScale.y into the streak,
                // oriented by its euler channels, NOT billboarded (see StepParticle). Double-sided Quad() + its own
                // additive material with the streak texture (tex3); coloured per-frame by SetCol like any world-quad.
                go.AddComponent<MeshFilter>().mesh = Quad();
                var mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
                var mat = FaithfulAlpha && AlphaShader() != null ? new Material(AlphaShader())
                    : _addMat != null ? new Material(_addMat) : new Material(Shader.Find("Sprites/Default"));
                mat.mainTexture = tex != null ? tex : _glow;
                mr.sharedMaterial = mat;
                p.mat = mat;
            }
            else
            {
                // BILLBOARDS use a SINGLE-SIDED quad: they always face the camera, so the back face is redundant — and
                // with an additive Cull-Off material a double-sided quad draws the SAME image twice at the SAME pixels
                // = 2× brightness → the bright sparks SATURATE to flat white and the rotating-starburst twinkle washes
                // out (the "no flicker" the double-sided texture caused). World-quads (non-orient, flat) keep both sides.
                go.AddComponent<MeshFilter>().mesh = em.IsRing ? BuildRing(em) : (em.Orient ? QuadBillboard() : Quad());
                var mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
                // world-quad burst (flag 0x1: not ring, not billboard) = the tex4 radial burst — colour it by the BLUE
                // diffuse using the texture only as a shape mask (the orange sprite would otherwise muddy to brown).
                bool worldQuad = !em.IsRing && !em.Orient;
                Material mat;
                if ((em.Blend == 1 || FaithfulAlpha) && AlphaShader() != null) mat = new Material(AlphaShader());
                // SCN0008 kekkai disc (tex69) + MW runes (tex117): textures have no alpha channel (tex.a=1 everywhere).
                // Legacy Particles/Additive does 2×tex×_TintColor → SrcAlpha = 2×1×_TintColor.a clips to 1 at all
                // ch1 values → disc permanently at max brightness regardless of the ch1 pulse = "慘白"/always-on.
                // Sdo/EftAlpha uses SrcAlpha = _TintColor.a directly (no 2×, no tex.a) → correct fade/pulse.
                else if (Persistent && em.HasTex && (em.TexIdx == 69 || em.TexIdx == 117) && AlphaShader() != null)
                    mat = new Material(AlphaShader());
                // POWER electric ribbon (slot2/3, rai_04/05/01 textures): the rai PNGs are OPAQUE (alpha=255) — a dark
                // background + bright lightning bolts. Legacy additive accumulates the DARK BG of the 20u overlapping
                // ribbons into a SOLID colour band (the "橫軸中間固定色" the user saw, obscuring the flicker). The
                // luminance shader makes the dark bg transparent (contribution ∝ luminance²), so ONLY the bright bolts
                // show additively = the official electric ribbon (band-coloured lightning on the empty channel).
                else if (_isPower && (em.Slot == 2 || em.Slot == 3) && LumShader() != null) mat = new Material(LumShader());
                else if (worldQuad && LumWorldQuad && LumShader() != null) mat = new Material(LumShader());
                else mat = _addMat != null ? new Material(_addMat) : new Material(Shader.Find("Sprites/Default"));
                mat.mainTexture = tex != null ? tex : _glow;
                mr.sharedMaterial = mat;
                p.mat = mat;

                // OUTER-GLOW HALO: a child that mirrors the particle (same mesh+texture) but scaled up and dimmed,
                // additive. It inherits the particle's transform (so billboards/rings stay oriented). Only created
                // when glow is on, so the faithful look is untouched at _glowMul=0.
                if (_glowMul > 0f)
                {
                    var ggo = new GameObject("eft-glow");
                    ggo.transform.SetParent(go.transform, false);
                    ggo.layer = _layer;
                    ggo.transform.localScale = Vector3.one * (1f + _glowSpread);
                    ggo.AddComponent<MeshFilter>().mesh = mr.GetComponent<MeshFilter>().mesh;
                    var gmr = ggo.AddComponent<MeshRenderer>();
                    gmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; gmr.receiveShadows = false;
                    var gmat = _addMat != null ? new Material(_addMat) : new Material(Shader.Find("Sprites/Default"));
                    gmat.mainTexture = tex != null ? tex : _glow;
                    gmr.sharedMaterial = gmat;
                    p.glowMat = gmat; p.glowTr = ggo.transform;
                }
            }
            // sprite-sheet flipbook: crop the material to frame 0's cell now (StepParticle advances it per frame).
            // Only set for true multi-cell flipbooks (em.FrameUv != null) — single-frame quads keep the full UV.
            if (em.FrameUv != null && em.FrameUv.Length > 0 && UsableUv(em.FrameUv[0]))
            {
                ApplyFrameUv(p.mat, em.FrameUv[0]);
                if (p.glowMat != null) ApplyFrameUv(p.glowMat, em.FrameUv[0]);
            }
            if (SortingOrder != 0)
                foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.sortingOrder = SortingOrder;
            p.tr = go.transform;
            // 2D play-field effects (hiteft3D) set SortingOrder so this burst draws OVER the board/notes like the sprite
            // burst; covers every renderer under `go` (quad/mesh/trail + glow child + mot-rigid submeshes) in one pass.
            if (SortingOrder != 0)
                foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.sortingOrder = SortingOrder;
            go.SetActive(false);
            _pending.Add(p);
        }

        void Flush() { if (_pending.Count > 0) { _ps.AddRange(_pending); _pending.Clear(); } }

        void LateUpdate()
        {
            // re-pin to the dancer's pelvis-on-floor each frame (engine re-calls SetTransformAnimated)
            transform.position = _follow != null ? _follow.position : _spawnPos;
            transform.localScale = Vector3.one * _effScale;
            if (BillboardCam != null) _cam = BillboardCam;   // dedicated burst camera overrides the auto-found stage camera
            else if (_cam == null)
            {
                // Billboard toward the camera that ACTUALLY RENDERS this effect (its cullingMask includes our layer),
                // not merely the first perspective camera found. The ShowTime gauge added a 2nd perspective camera
                // (GaugeCam, eye z-1000, layer 6); the old "first non-ortho camera" grab picked it non-deterministically,
                // so the stage combo bursts (layer 4, rendered by _sceneCam) billboarded toward the gauge eye and faced
                // straight up. Prefer the layer-matching camera; fall back to any perspective camera (legacy 2D path).
                foreach (var c in Camera.allCameras) if (!c.orthographic && (c.cullingMask & (1 << _layer)) != 0) { _cam = c; break; }
                if (_cam == null) foreach (var c in Camera.allCameras) if (!c.orthographic) { _cam = c; break; }
            }

            _acc += Time.deltaTime * SpeedMul;   // SpeedMul>1 runs the WHOLE effect faster (life curves + re-spawn + motion)
            while (_acc >= Step) { _acc -= Step; Tick(); }

            // billboards face the camera every frame (engine billboard branch); rings keep their world matrix.
            // ROLL = the accumulating rotZ spin (chD, e.g. naga00 −3.6°/tick) PLUS the per-particle random rotY phase
            // (RotJit.y, e.g. naga00 ±180°). Without the rotY phase, ALL naga00 sparks start at roll 0 and spin in
            // SYNC → the 4-point-starburst rays sweep together = one global pulse that reads as "not flickering".
            // Adding the random phase desyncs them → individual sparks twinkle out of phase = the firework's shimmer.
            if (_cam != null)
                foreach (var p in _ps)
                    if (p.orient && p.tr.gameObject.activeSelf)
                    {
                        // face the camera. For an ORTHO billboard camera all particles face flat along the camera's
                        // forward (their world position is irrelevant); for a perspective camera each faces the eye.
                        Vector3 fwd = _cam.orthographic ? _cam.transform.forward : (p.tr.position - _cam.transform.position);
                        p.tr.rotation = Quaternion.LookRotation(fwd, _cam.transform.up)
                                        * Quaternion.Euler(0f, 0f, p.rot.z + p.rot.y);
                    }
        }

        void Tick()
        {
            _tick++;
            int alive = 0;
            foreach (var p in _ps)
            {
                if (p.spawnDelay > 0) { p.spawnDelay--; if (p.tr.gameObject.activeSelf) p.tr.gameObject.SetActive(false); alive++; continue; }
                if (p.startDelay > 0) { p.startDelay--; if (p.tr.gameObject.activeSelf) p.tr.gameObject.SetActive(false); alive++; continue; }
                if (p.life <= 0)
                {
                    // LEAK FIX: a looping effect (Persistent/Loop) has an eternal root, so `alive` never hits 0 and
                    // RespawnTree never runs — dead child particles' GameObjects would pile up forever (thousands over a
                    // song → FPS 120→2). RECLAIM them here: destroy the GameObjects + drop the P from _ps. Safe because
                    // children only read parent P-FIELDS (pos/rot/liveScale/baseSize), never parent.tr — so the dead P
                    // lingers via child refs (frozen) until its children die too. One-shot effects keep the old
                    // deactivate (they're torn down wholesale at alive==0 / _maxTicks).
                    if (Persistent || Loop)
                    {
                        if (p.glowTr) Destroy(p.glowTr.gameObject);
                        if (p.tr) Destroy(p.tr.gameObject);
                        p.dead = true; _reclaimedAny = true;
                    }
                    else if (p.tr.gameObject.activeSelf) p.tr.gameObject.SetActive(false);
                    continue;
                }
                if (!p.tr.gameObject.activeSelf) p.tr.gameObject.SetActive(true);
                p.started = true; alive++;
                StepParticle(p);
            }
            Flush();
            if (_reclaimedAny) { _ps.RemoveAll(_isDead); _reclaimedAny = false; }
            if (Persistent)
            {
                // scene background effect: never destroy; loop the tree when it has fully died (continuous emitters
                // never reach alive==0 so they just keep running — e.g. the magic circle's steady glow).
                if (_tick > 2 && alive == 0 && _pending.Count == 0) RespawnTree();
            }
            else if (Loop)
            {
                // HOLD burst: the negative-life emitters keep it alive; the host destroys it on release. NO auto-destroy
                // and NO _maxTicks hard-kill (a hold can outlast _maxTicks+60 = ~1.6s → it must not self-terminate).
            }
            else
            {
                if (_tick > 2 && alive == 0 && _pending.Count == 0) Destroy(gameObject);
                if (_tick > _maxTicks + 60) Destroy(gameObject);
            }
        }

        void StepParticle(P p)
        {
            var em = p.E;
            p.life--;
            // POWER ribbon (slot2/3, attach=1) → die WITH its slot4 carrier (engine FUN_0098fc80 @666401-406). slot4
            // lives 20 ticks and DRIVES the ribbon's length via its growing scaleZ; once slot4 dies its liveScale FREEZES,
            // so any ribbon kept alive past 20 rides that frozen full length = a STATIC band (user: 「兩條直線」不會動).
            // Killing at parent-death means every ribbon dies while still GROWING → always animated, no static lines,
            // and it re-spawns every ~16 ticks = the flowing crackle. (A longer cap was tried and REVERTED — it just
            // stacked frozen bands.) slot4 is reclaimed the same tick but its P lingers (frozen life<=0) via this ref.
            if (_isPower && p.attach != 0 && p.parent != null && p.parent.life <= 0) { p.life = 0; }
            // Persistent scene effect: a ROOT particle never dies — it loops back to full life so the base stays put
            // forever (e.g. the SCN0008 kekkai disc, life 501: without this it died at 501 while its sub-particles
            // lived to ~600, so RespawnTree waited and the circle blinked out for ~2s). The trigType-3 children keep
            // re-emitting off the (now everlasting) root. A static disc loops invisibly; nothing disappears ("一直在").
            // Persistent loops EVERY root; Loop (HOLD burst) loops only the emitters the file marked negative-life
            // (EftEmitter.Loop) — so HIT_LONG's sustain slots 0/2 re-init forever while its one-shot head flash (slot1)
            // still dies once. Faithful to the engine's per-particle 0x80000 flag.
            if ((Persistent || (Loop && em.Loop)) && p.root && p.life <= 0)
            {
                p.life = p.life0;
                // re-arm one-shot children (trigType 0/2) so the whole effect RE-BLOOMS each root cycle instead of
                // dying after one life — e.g. SCN0008's MW glow runes (slot8/9, trigType 0, life 501) vanished ~10s in
                // because the looping root never re-spawned them. (trigType-3 children re-fire on their own interval.)
                if (p.kidSpawned != null) System.Array.Clear(p.kidSpawned, 0, p.kidSpawned.Length);
            }
            int life0 = p.life0;
            int ageTicks = life0 - p.life;
            float t = ageTicks / (float)life0;   // age 0..1
            // FIX1 (ShowTime gauge fidelity): official engine 2-point-samples every non-alpha channel of a flags&1==0
            // emitter (POWER slots 1/2/3/5/6). Stops the aef_4_02 halo (slot1) & naga00 star (slot5) over-blooming into
            // an invisible wash (scale key2 pivot ×5 → ×1.5). Scoped to POWER via _isPower; non-POWER (twoPt=false) is
            // unchanged. SC/RC/RMC dispatch; ch1(alpha) always stays full-curve (engine excludes it).
            bool twoPt = PowerEngineSampler && _isPower && (em.Flags & 1) == 0;
            float SC(int ch) => twoPt ? em.Ch[ch].Scale2(t) : em.Ch[ch].Scale(t);
            float RC(int ch) => twoPt ? em.Ch[ch].Ranged2(t) : em.Ch[ch].Ranged(t);
            float RMC(int ch) => twoPt ? em.Ch[ch].RangedMin2(t) : em.Ch[ch].RangedMin(t);
            // .mot frame timer — EXACT engine replication (decompiled Particle_Update 030:7611-7631):
            //   frame += speed/60 per 50Hz tick;  speed = emitter word[9] (=0 for delta_line) → defaults to 15.
            //   ⇒ frame += 15/60 = 0.25 per tick = 12.5 fps. MAX (word[8]) = 240 is never reached in the 120-tick life,
            //   so over one life frame goes 0→30 (= the .mot's 31 frames) EXACTLY ONCE, then the particle dies. NOT a
            //   loop and NOT 30fps. The sliding window 紅→紅藍→藍黃→黃 comes from the trigType-3 RE-FIRE below spawning
            //   a 2nd instance 40 ticks (=10 .mot-frames = one colour window) later — not from any per-mesh phase.
            if (p.meshMot != null) p.meshMot.FrameOverride = Mathf.Min(ageTicks * 0.25f, p.meshMotMax);

            // SUB-EMIT this particle's children at each child's at-time (engine: trigType 2 = at parent age==atTime;
            // 0 = on birth). This is what staggers 100COMBO's rings to ~age 8, and 400's billboards to the rings'
            // mid-life. naga00 EMIT 3 → each fires its 2 ring children → the 15 rings the live game shows.
            if (p.kidSpawned != null)
                for (int i = 0; i < em.Children.Count; i++)
                {
                    var c = em.Children[i];
                    // trigType 3 = REPEATING (decompiled Particle_Update 030:7714): re-emit the child every AtTime
                    // ticks while the parent lives — keeps a continuous, moving sub-effect alive (e.g. the SCN0008
                    // magic circle's 3 colour light bars: life 60, re-fired every 30 → always 2 overlap; without it
                    // they fired once and vanished). GATED on Persistent so the one-shot combo bursts (100/400/500
                    // COMBO also have trigType-3 children, validated as fire-once) keep their proven look.
                    if (c.TrigType == 3 && (Persistent || Loop))
                    {
                        // decompiled (030:7714-7724): the engine fires the child on the parent's BIRTH, then whenever the
                        // parent's DOWN-COUNTING life counter is a multiple of the child's atTime — i.e. `parentLife % atTime
                        // == 0`. parentLife = life0 - ageTicks, so the first re-fire is at ageTicks = life0 - atTime, NOT at
                        // ageTicks == atTime. For SCN0008 delta_line (parent slot1 life0=501, atTime=460) that is ageTicks=41
                        // — i.e. a 2nd instance 40 ticks (=10 .mot-frames = one colour window) after the first, which is
                        // EXACTLY what overlaps the colours into the sliding window 紅→紅藍→藍黃→黃. (The old `ageTicks %
                        // atTime` re-fired at 460 = far too late, so only one instance was ever live → no overlap.)
                        int interval = Mathf.Max(1, Mathf.RoundToInt(c.AtTime));
                        if (ageTicks == 1 || (life0 - ageTicks) % interval == 0) SpawnEmitter(c, 0, false, p.pos, p.azim, p.vel, p);
                        continue;
                    }
                    if (p.kidSpawned[i]) continue;
                    int fireAt = c.TrigType == 0 ? 0 : Mathf.RoundToInt(c.AtTime);   // trigType 2 uses at-time
                    if (c.TrigType != 1 && ageTicks >= fireAt) { p.kidSpawned[i] = true; SpawnEmitter(c, 0, false, p.pos, p.azim, p.vel, p); }
                }

            // TEXTURE FLIPBOOK ADVANCE (engine Particle_Update 030_scene:6963; texidx = word[0x47+frame]). Every
            // `frameHold` ticks step to the next frame, wrapping if loop. THIS is the FINISHED firework's flicker:
            // its sparks swap between visually distinct sprites — naga00 (bright 4-point star) ⇄ ring_l (sparse X) ⇄
            // aef_1_01 (thin streak) every 1-2 ticks → each spark's bright core pops in and out (一下看到一下看不到).
            // It is a TEXTURE swap, NOT a colour-channel or alpha oscillation (FINISHED's alphaCut word[0x22c]=0).
            if (p.frameTex != null && p.frameCount > 1 && p.mat != null)
            {
                if (p.frameHold <= p.frameCounter)
                {
                    p.frameCounter = 0;
                    int nf = p.frame + 1;
                    if (nf >= p.frameCount) nf = p.frameLoop ? 0 : p.frameCount - 1;
                    if (nf != p.frame)
                    {
                        p.frame = nf;
                        var ft = p.frameTex[nf];
                        if (ft != null) { p.mat.mainTexture = ft; if (p.glowMat != null) p.glowMat.mainTexture = ft; }
                        // sprite-sheet flipbook: step to this frame's atlas cell (confetti 4×4). The texidx may be
                        // identical every frame (ZIPIANZ) — the UV sub-rect is what actually animates the piece.
                        if (p.E.FrameUv != null && nf < p.E.FrameUv.Length && UsableUv(p.E.FrameUv[nf]))
                        {
                            ApplyFrameUv(p.mat, p.E.FrameUv[nf]);
                            if (p.glowMat != null) ApplyFrameUv(p.glowMat, p.E.FrameUv[nf]);
                        }
                    }
                }
                p.frameCounter++;
            }

            // per-axis scale channels (0xe/0xf/0x10) → animScale, with the 0.5-pivot remap.
            // Verified EXACT vs the live game (Frida): ring X/Z bloom 0.45→1.02→0.30, Y grows 0.31→2.02. No trim.
            Vector3 animScale = new Vector3(SC(0xe), SC(0xf), SC(0x10));   // 2-pt for POWER (halo/star don't over-bloom → visible)
            // WHITE-HOT head core: enlarge the WHITE head-glow slots (naga00 tex100 + ring_l tex96) so their additive
            // quads BLANKET the ±9.375 gauge band the whole life (they'd otherwise be ~17-45 world, leave the band, and
            // read as tiny flickers). This is the RE-verified "oversized additive quad" mechanism that makes the core
            // persistent white-hot. Only the white slots; the gold aef_4_02 (tex30 = body-aura material) is untouched.
            if (_isPower && em.HasTex && (em.TexIdx == 100 || em.TexIdx == 96)) animScale *= PowerWhiteSize;
            // SCN0008 corner glow flares (tex42): the raw channels animate sclH ONLY (slot4: 1.0→~2×), which stretched the
            // TOP flare into a tall oval AND (taking max) ballooned it into an over-bright burst ("一直變形 / 後面那顆太亮").
            // Keep the flare a STABLE round star at its base size — the visible "忽大忽小" twinkle comes from its alpha
            // (ch1: 0→255→0 fade-in/out), not a scale stretch. So ignore the deforming scale channels for these glows.
            if (Persistent && em.HasTex && em.TexIdx == 42) animScale = Vector3.one;
            // SCN0015 booklight orb (tex31): the channels grow X/Z to ~1.53 but leave Y at 1.0, so the camera
            // billboard flattens into a wide OVAL ("變形") — same failure mode as the tex42 flares above. Drive all
            // axes from the X channel so it stays a ROUND ball that still breathes (1→1.53→1). The orb's overall
            // size/brightness is tuned in SceneEftRenderCatalog ("booklight",2,31), not here.
            if (Persistent && EffectName == "booklight") animScale = new Vector3(animScale.x, animScale.x, animScale.x);
            // SCN0005/24 SNOW flakes (slot0 orient billboard): sclY shrinks 1.0→0.44 over life while sclX/Z stay ~1.0,
            // so the camera billboard flattens each aging flake into a wide horizontal OVAL — "有些雪是扁的" (young flakes
            // round, older ones squished). Same non-uniform-scale-on-a-billboard artifact as the tex42 glows / booklight
            // orb above. The flake's fade is driven ENTIRELY by its ch1 alpha (0→1→0), so the vertical squish serves no
            // purpose; drive all axes from the (near-constant) X channel so every flake stays ROUND from every camera
            // angle (使用者回報:每個角度看都應該是圓的). Covers both snow scenes (SCN0005 聖誕夜 + SCN0024 雪景).
            if (Persistent && EffectName == "snow") animScale = new Vector3(animScale.x, animScale.x, animScale.x);
            // AEF_3_00 blue mesh (MeshIdx 32, ONLY 200/300 — never touch 100/400/500's column meshes). Its own scale
            // channels make it too thin at spawn and stretch it too TALL mid-life (trail Y-bloom). Use a UNIFORM scale
            // (no Y-bloom): 300 rides a BALL → track the ball's width/rate × MeshWidthMatch; 200 rides the ground
            // external → its own width curve, uniform. Keeps width matched to the fountain without ballooning.
            if (p.isMesh && em.MeshIdx == 32)
            {
                if (p.parent != null && p.parent.E.Orient)
                {
                    // 300: base = track the ball width × shrink-to-small over life. Then at SPAWN make it MeshStartW×
                    // wider and MeshStartH× taller (a tall vertical flame), easing to the uniform end size by death.
                    float s = p.parent.E.Ch[0xe].Scale(t) * MeshWidthMatch * Mathf.Lerp(1f, MeshShrinkEnd, t);
                    float wMul = Mathf.Lerp(MeshStartW, 1f, t), hMul = Mathf.Lerp(MeshStartH, 1f, t);
                    animScale = new Vector3(s * wMul, s * hMul, s * wMul);
                }
                else
                    animScale = Vector3.one * em.Ch[0xe].Scale(t);   // 200: own width curve, uniform
            }
            // rotation channels (0xb/0xc/0xd) ACCUMULATE (degrees)
            p.rot.x += RMC(0xb);
            p.rot.y += RMC(0xc);
            p.rot.z += RMC(0xd);
            // SCN0008 kekkai disc (tex69): does NOT rotate (confirmed by the user + decompile: its rot channels are ~0
            // and no effect-level spin is armed). DiscSpinDegPerTick stays 0 = faithful. (Was briefly set to spin; wrong.)
            if (Persistent && DiscSpinDegPerTick != 0f && em.HasTex && em.TexIdx == 69) p.rot.z += DiscSpinDegPerTick;
            // position integration: pos += velocity · ch0Scale · velScale. EXACTLY the engine UPDATE (~136220):
            // pos += vel × param_1[500] × param_1[0x33], where param_1[0x33]=ch0 scale and param_1[500]=the per-particle
            // RandScaleJitter of word[0x1f4] (= p.velScale, a CONSTANT 1.0±jitter set at spawn). Earlier I dropped
            // word[0x1f4] entirely thinking it was a flat 0.5/0.8 multiplier that halved distance — wrong: its AVERAGE
            // is 1.0 (so distance was ~right) but its per-particle SPREAD is what makes a fountain reach MANY heights
            // (tex96 jit 0.8 → some sparks ×1.8 → the official's Y≈131 risers my flat version capped at ~91).
            // VELOCITY TURBULENCE / "crackle" (engine UPDATE @~004bd574: gate `(life & mask) && rand%100<prob`, then
            // FUN_004bd4d0 rotates the velocity by random per-axis angles clamped ±TurbClamp). This is the FINISHED
            // firework's FLICKER — its sparks (tex100/tex96/tex3) all have mask=5 prob=50 ±10°, so ~37% of frames each
            // spark's direction jumps and it darts/crackles. NOT an alpha strobe (the 0x22c alpha-cutoff is 0 here).
            // 400's root flash crackles too (±5–10° all axes). Applied BEFORE integration, exactly like the engine.
            if (!p.turbOff && em.TurbMask != 0 && em.TurbMode != -1 && (p.life & em.TurbMask) != 0
                && UnityEngine.Random.Range(0, 100) < em.TurbProb)
            {
                // ENGINE-EXACT (FUN_004bd4d0 + FUN_004bd240): rotate the velocity by the random per-axis angles in a
                // basis RECOMPUTED from the current velocity every tick (basisA = up×vel = horizontal perpendicular,
                // basisB = vel×basisA). Because basisA is recomputed each tick, the random ±ang per tick makes the
                // velocity PITCH jitter (bob up/down) at the ~18 Hz fire rate instead of drifting smoothly — that
                // per-tick bob is the FINISHED firework's brightness FLICKER (Frida: ±2–4% bright-pixel oscillation).
                float ax = TurbAxis(em.TurbRndA.x, em.TurbRndB.x, em.TurbClamp.x);
                float ay = TurbAxis(em.TurbRndA.y, em.TurbRndB.y, em.TurbClamp.y);
                p.vel = EngineTurbRotate(p.vel, ax, ay);
                p.turbN++; p.turbAng += ax;
                if (em.TurbMode == 1) p.turbOff = true;   // one-shot
            }

            // position integration: pos += vel · ch0Scale · velScale (engine Particle_Update 030_scene:7326-7335).
            // EXCEPT a mesh welded to a MOVING parent (300's orb) keeps its own pos at the spawn offset so it tracks the
            // orb EXACTLY (the orb's rise IS the motion) — otherwise its +Z drift slides it off the orb over time.
            if (!p.lockToParent)
            {
                float scaleVel = SC(0);
                p.pos += p.vel * (scaleVel * p.velScale * MotionScale);
                float ageLin = life0 - p.life;
                p.pos.y -= (ageLin * em.GravAccel + em.GravBase) * MotionScale;
                // ENGINE-FAITHFUL INTEGER POSITION (why the official head glow doesn't move): the head's per-tick step
                // (vel.y0.2×ch0≈0.034 local ⟪1) truncates to 0 so it NEVER rises, and truncating all 3 axes clusters
                // every re-spawn at the fill head = a STABLE white-hot core. (Round-6 tried keeping X float for depth
                // variety; user reported "頭光前後跳位置" = the core hopping forward/back → reverted. Stable core.)
                if (_isPower && em.TexIdx != 96) { p.pos.x = (float)(int)p.pos.x; p.pos.y = (float)(int)p.pos.y; p.pos.z = (float)(int)p.pos.z; }
            }

            if (DumpTraj && !p.invisible) DumpTrajectory(p, ageTicks, life0, t);

            // colour = diffuse (ch2/3/4 = R/G/B) + specular (ch6/7/5 = R/G/B), alpha = ch1. D3D adds specular to
            // diffuse; keeping both preserves the real per-particle hue (blue aef_1_07, orange aef_4_03, etc.).
            float r = RC(2) + RC(6);
            float g = RC(3) + RC(7);
            float b = RC(4) + RC(5);
            float a = em.Ch[1].Ranged(t);   // alpha (ch1) ALWAYS full-curve, even in 2-point mode (engine excludes ch1)
            // DIAG: dump the head-glow (slot1 halo) vs star (slot5) WORLD position/scale/alpha so we can see if the halo
            // is cone-scattered out of the thin RT frustum (camera at GaugeOrigin.y=20000, visible ±~9.4 world units).
            if (_isPower && (em.Slot == 1 || em.Slot == 5) && _renderDbgN < 60)
            {
                _renderDbgN++;
                var wp = transform.TransformPoint(p.pos);
                try { System.IO.File.AppendAllText(@"H:/65_remake/gauge_render.txt",
                    $"{EffectName} slot{em.Slot} t={t:F2} wY-20000={(wp.y - 20000f):F1} wX={wp.x:F0} animS=({animScale.x:F1},{animScale.y:F1}) worldSize~={(em.BaseSize.y * animScale.y * _effScale):F0} a={a:F0}\n"); } catch { }
            }
            // SCN0008 kekkai DISC (tex69): its ch1 alpha only pulses 128↔255 (×2), which on a thin additive line pattern
            // over the dark floor reads as "always lit" — the user sees no 變暗變亮. DEEPEN the pulse (a²/255: 128→64,
            // 255→255 = ×4 contrast) so the disc clearly dims then brightens. Tunable; disc-only + Persistent.
            if (Persistent && em.HasTex && em.TexIdx == 69) { if (DiscPulseDepth != 1f) a = Mathf.Pow(a / 255f, DiscPulseDepth) * 255f; LastDiscAlpha = a; }
            // ground-streak trail: override the pale-pink diffuse with the official's dimmer BLUE (keep the alpha fade).
            if (p.isTrail && TrailOverride)
            {
                r = TrailTint.r * TrailBright * 255f;
                g = TrailTint.g * TrailBright * 255f;
                b = TrailTint.b * TrailBright * 255f;
            }
            if (p.renderRgbMul != 1f || p.renderAlphaMul != 1f)
            {
                r *= p.renderRgbMul;
                g *= p.renderRgbMul;
                b *= p.renderRgbMul;
                a *= p.renderAlphaMul;
            }
            // 3D mesh COLOUR: the engine tints the mesh by the particle DIFFUSE (param_1[0x34] → TEXTUREFACTOR/vertex
            // colour, 030_scene:5560/5567), NOT white — so AEF_3_00's raw blue is modulated by slot0's diffuse channels
            // (ch2-4 ≈ 229,195,~240 = a pale lavender-pink), giving the "官方有調過色" adjusted blue-violet rather than
            // the raw texture. r/g/b already hold that diffuse, so DON'T override to white — let it modulate the texture.
            // (alpha = ch1 still drives the fade.)

            // ATTACH-TO-PARENT (engine word[0x37]=1, Particle_Update 030_scene:7388-7401): the world matrix is rebuilt
            // each frame as `ownSRT × parentWorldMatrix` — the trail RIDES its parent AND its own local drift is rotated
            // INTO the parent's frame. The spread/scatter therefore comes from the PARENT's rotation, not the trail:
            //   200 parent (ground external) has a RANDOM Y rotation (rotJit.y=360°) → each trail's +Z drift fans to a
            //       random azimuth = the outward GROUND CURTAIN the user wants.
            //   300 parent (rising orb) has NO rotation → the trail rides the orb up with only its straight +Z drift.
            // Parent rotation = its LOGICAL euler (initRot + jitter + channels), NOT the billboard's camera-facing.
            if (p.attach != 0 && p.parent != null)
            {
                if (p.isMesh && em.MeshIdx == 32 && p.parent.E.Orient)
                {
                    // 300 AEF_3_00: COMPLETELY VERTICAL (no tilt). Its base (pivot) is anchored ON the ball so it KEEPS
                    // UP as the ball rises (an earlier −ballHalfH drop left the shrinking flame below the risen ball =
                    // "沒跟上球"). MeshDropFrac×ballHalfH optionally drops it toward the ball's bottom (0 = on the ball).
                    float ballHalfH = 0.5f * p.parent.baseSize.y * p.parent.E.Ch[0xf].Scale(t);
                    // Mesh300Z pushes the flame fore/aft of the orb (− = behind, so the ball reads in front of it).
                    p.tr.localPosition = p.parent.pos + new Vector3(0f, -ballHalfH * MeshDropFrac, Mesh300Z);
                    p.tr.localRotation = Quaternion.identity;
                }
                else if (Persistent && PinCornerGlows && p.E.HasTex && p.E.TexIdx == 42)
                {
                    // SCN0008 corner glow balls (slot4/5/6, tex42): pin to the FIXED floor triangle corner instead of
                    // riding the disc's 90°X tilt (which sinks slot5/6 to y=-20) and -60°Y spin (which swings them off
                    // the BL/BR corners). slot4=TOP(ki), slot5=BL(aka), slot6=BR(ao) — see CornerTop/BL/BR above.
                    p.tr.localPosition = p.E.Slot == 5 ? CornerBL : p.E.Slot == 6 ? CornerBR : CornerTop;
                    // billboards face the camera in LateUpdate; identity local rotation is fine (orient overrides it).
                    p.tr.localRotation = Quaternion.identity;
                }
                else
                {
                    Quaternion prot = Quaternion.Euler(p.parent.rot);
                    // D3D9 engine: child world matrix = parent_world × child_local. Parent scale IS part of parent_world,
                    // so the child's position and scale are multiplied by the parent emitter's own baseSize×animScale.
                    // Example: STAGELIGHTB slot2 carrier has baseSize.y=5 → slot0 beam pos×5 and scale×5 → 225 world units
                    // long instead of 45. Trails and meshes are handled by their own render paths — keep old behavior there.
                    if (!p.isTrail && !p.isMesh)
                    {
                        Vector3 ps = p.parent.liveScale;
                        p.tr.localPosition = p.parent.pos + prot * Vector3.Scale(ps, p.pos);
                    }
                    else
                        p.tr.localPosition = p.parent.pos + prot * p.pos;
                    p.tr.localRotation = prot * Quaternion.Euler(p.rot.x, p.rot.y, p.rot.z);
                }
            }
            else
            {
                p.tr.localPosition = p.pos;
                // rings + plain world-matrix quads: oriented by Euler(initRot + accumulated channels). aef_1_07 keeps
                // its template 90° X → lies flat; the ring's rotY channel spins it. Billboards (p.orient) face the camera.
                if (!p.orient) p.tr.localRotation = Quaternion.Euler(p.rot.x, p.rot.y, p.rot.z);
            }
            Vector3 ownScale = Vector3.Scale(Vector3.Scale(p.baseSize, animScale), p.renderScaleMul);
            // D3D9 attach mode 1: parent scale is applied to child scale (part of the full matrix multiply) — but ONLY
            // for world-matrix quads. The engine's BILLBOARD branch (sdo.bin.c 664689-664745) rebuilds the render
            // matrix from the camera basis with the particle's OWN baseSize × anim channels and takes only the
            // TRANSLATION from the attach-composed matrix — a billboard never inherits the parent's scale. (BOOM slot0
            // rode its invisible carrier's baseSize (1,5,3) and blew into a 900px vertical sheet that swamped the whole
            // burst.) Persistent scene effects keep the old behaviour: BOOKLIGHT/FIRE3/AURORA/HONGBAO were eye-validated
            // WITH the parent multiply, so the engine-faithful rule is applied to one-shot (non-Persistent) effects only.
            // POWER slot2 AND slot3 both inherit carrier growth via the inverse-rotate hack below (line ~1120). RE
            // (ground-truth EFT parse) corrected the old assumption: slot3's initRot is (90,90,0), NOT 45°. So
            // Inverse(Euler(90,90,0))·(1.5,1.5,1.5f) = Abs(1.5f,1.5,1.5) → the growing carrier component (1.5f) lands
            // on slot3's LOCAL X = the ribbon LENGTH; height/depth stay constant. Right edge pins at the head, ribbon
            // grows LEFT like slot2, NO vertical band. The old `!(_isPower && Slot==3)` exclusion was for the PRE-hack
            // direct-multiply path (where slot3's 90° X-pitch sent carrier-Z growth onto its vertical axis = the tall
            // static strip); the inverse hack made it stale, and leaving it in stranded slot3 at a constant full 20u
            // CENTERED length → a bright line straddling the head into the un-filled zone (氣條那條貫穿線). Removing
            // it makes slot3 the proper second (crossing) ribbon. Verified: RE workflow wf_0c0ba4d2 (issue1, CONFIRMED).
            bool inheritParentScale = p.attach != 0 && p.parent != null && !p.isTrail && !p.isMesh
                                      && !(p.orient && !Persistent);
            // Engine (sdo.bin.c Particle_Update @666394-666415, attach mode 1) = childLocal · parentWorld (row-major):
            // the carrier's NON-UNIFORM scale acts AFTER the child's own rotation. The naive per-axis Scale applies it
            // in the child's PRE-rotation (mesh) frame → the ShowTime POWER strip's ANIMATING carrier Z-scale hit the
            // quad's Z-thickness (invisible) instead of its WIDTH → the ribbon slid off the gauge frustum (氣條看不見).
            // Rotate the parent scale into the child's mesh frame by the inverse of the child's rotation so it lands on
            // the width axis (right edge pins at the head, ribbon grows left = the official electric fill). Scoped to
            // the POWER_* gauge (used nowhere else) so every already-tuned attach effect (STAGELIGHTB…) is untouched.
            if (inheritParentScale && EffectName != null && EffectName.StartsWith("POWER"))
            {
                Vector3 rp = Quaternion.Inverse(Quaternion.Euler(p.rot)) * p.parent.liveScale;
                p.tr.localScale = Vector3.Scale(new Vector3(Mathf.Abs(rp.x), Mathf.Abs(rp.y), Mathf.Abs(rp.z)), ownScale);
            }
            else
                p.tr.localScale = inheritParentScale ? Vector3.Scale(p.parent.liveScale, ownScale) : ownScale;
            p.liveScale = ownScale;  // store OWN scale (without parent) so children can multiply it in next tick
            // trail streak: animScale.y already stretches local +Y into the streak; TrailWidthMul tunes its width (local X).
            if (p.isTrail && TrailWidthMul != 1f) { var s = p.tr.localScale; s.x *= TrailWidthMul; p.tr.localScale = s; }
            // billboards (p.orient): oriented to camera in LateUpdate
            if (p.meshMats != null)   // 3D mesh (AEF_3_00 blue mesh): tint every submesh as a group; alpha fades it
            {
                // Three independent paths so 200 and 300 never share a knob:
                //  • 300 (mesh300): boost = Mesh300Intensity, and OPACITY (Mesh300Alpha) scales the additive RGB ENERGY
                //    — NOT the alpha — because with the boost `rgb×intensity×alpha` stayed clipped to white across the
                //    whole opacity range (the "透明度沒變" bug). Scaling RGB makes the slider visibly fade the flame.
                //  • 200 (MeshIdx 32, non-300): the legacy MeshIntensity(rgb)+MeshAlpha(alpha) path, verbatim (correct).
                //  • other tiers' meshes (100/400/500 columns): 1×, untouched. DebugMeshOnly isolates at a fixed 5×.
                if (DebugMeshOnly)
                    for (int i = 0; i < p.meshMats.Length; i++) SetCol(p.meshMats[i], r * 5f, g * 5f, b * 5f, a);
                else if (p.mesh300)
                {
                    float e = Mesh300Intensity * Mesh300Alpha;   // opacity dims the additive energy (visible at any blend)
                    for (int i = 0; i < p.meshMats.Length; i++) SetCol(p.meshMats[i], r * e, g * e, b * e, a);
                }
                else if (em.MeshIdx == 32)   // 200 — unchanged
                    for (int i = 0; i < p.meshMats.Length; i++) SetCol(p.meshMats[i], r * MeshIntensity, g * MeshIntensity, b * MeshIntensity, a * MeshAlpha);
                else
                {
                    // SCENE .mot-rigid mesh (SCN0008 delta_line): the aka/ao/ki TEXTURES carry the colour and the
                    // emitter has NO diffuse/alpha channels, so r=g=b=a come out 0 (Ranged of an empty channel) → an
                    // additive material would be invisible (the "三色線完全沒出現" bug). Render WHITE so the textures
                    // show; only fade by the alpha channel if it actually has keyframes. Falls back to the raw
                    // r/g/b/a for any mesh that DOES author colour channels.
                    bool hasCol = em.Ch[1].Count >= 2 || em.Ch[2].Count >= 2 || em.Ch[3].Count >= 2 || em.Ch[4].Count >= 2;
                    float mr = hasCol ? r : 255f, mg = hasCol ? g : 255f, mb = hasCol ? b : 255f;
                    float ma = em.Ch[1].Count >= 2 ? a : 255f;
                    for (int i = 0; i < p.meshMats.Length; i++) SetCol(p.meshMats[i], mr, mg, mb, ma);
                }
            }
            else if (DebugMeshOnly && p.mat != null)   // isolate the mesh: hide all non-mesh particles (additive a=0)
            {
                SetCol(p.mat, 0f, 0f, 0f, 0f);
                if (p.glowMat != null) SetCol(p.glowMat, 0f, 0f, 0f, 0f);
            }
            else if (p.mat != null)   // invisible 0x40000 carriers have no material — they exist only to sub-emit
            {
                // EXPOSURE RAMP (200/300 fountain balls only): overexposed white-hot at birth → decays to the real
                // colour over the first BallCoreExpoFrac of life, so the ball's true cyan/violet and the AEF_3_00 blue
                // mesh behind it emerge as it rises, then alpha fades it. trails/ring/disk stay at real exposure (1).
                // Scene EFTs (Persistent=true) are gentle ambient glows — NOT combo bursts — so skip the ramp; their
                // orbs (tex30/31) should be soft halos, not white-hot blasts that overwhelm the flame particles above.
                float ci = p.isBallCore && !Persistent
                    ? Mathf.Lerp(BallCoreIntensity, 1f, Mathf.Clamp01(t / Mathf.Max(0.01f, BallCoreExpoFrac)))
                    : 1f;
                // POWER gauge head layers: boost the additive energy so the layers actually show on the black RT.
                //   naga00 star (tex100) + ring_l sparks (tex96) → PowerHeadGlowBright (white core clips 爆白);
                //   aef_4_02 HALO (tex30) → PowerHaloBright — the BIG soft glow at the fill head (官方那顆「大顆的」),
                //     previously left unboosted ("stays soft") so it never showed.
                if (_isPower && p.E.HasTex)
                {
                    if (p.E.TexIdx == 100 || p.E.TexIdx == 96) ci *= PowerHeadGlowBright;
                    else if (p.E.TexIdx == 30) ci *= PowerHaloBright;
                    // slot3 = the blue 45° cross-flash: dim it so the alpha→0 fade dominates and the ~3 overlapping
                    // generations read as a soft center-bright crackle instead of an additively-saturated static strip.
                    else if (p.E.Slot == 3) ci *= PowerCrossDim;
                }
                SetCol(p.mat, r * ci, g * ci, b * ci, a);
                // outer-glow halo: same hue (NOT boosted), intensity scaled by _glowMul (alpha drives additive brightness)
                if (p.glowMat != null) SetCol(p.glowMat, r, g, b, a * _glowMul);
            }
        }

        // Log one particle's effect-relative WORLD trajectory in the Frida-hook format so the sim can be diffed against
        // eft_online_log.txt. Adopt near birth, cap 64 per texture, sample every 8 ticks + last frame.
        void DumpTrajectory(P p, int ageTicks, int life0, float t)
        {
            int tex = p.E.TexIdx;
            if (p.dumpN == 0)
            {
                if (ageTicks > 3) return;
                int c = (_trajCount.TryGetValue(tex, out var v) ? v : 0);
                if (c >= 64) return;
                _trajCount[tex] = c + 1; p.dumpN = c + 1;
            }
            if (ageTicks % 8 != 0 && ageTicks != life0 - 1) return;
            Vector3 wp = p.pos * _effScale;   // effect-relative world (Frida effect sits at origin)
            var em = p.E;
            Vector3 aS = new Vector3(em.Ch[0xe].Scale(t), em.Ch[0xf].Scale(t), em.Ch[0x10].Scale(t));
            Vector3 ws = Vector3.Scale(Vector3.Scale(p.baseSize, aS), Vector3.one * _effScale);
            int alpha = Mathf.RoundToInt(Mathf.Clamp(em.Ch[1].Ranged(t), 0, 255));
            string kind = p.isTrail ? "trail" : em.IsRing ? "RING" : "bb";
            Vector3 wpos = p.tr != null ? p.tr.position : Vector3.zero;   // ACTUAL world pos (reveals effect-transform spin/orbit)
            DumpLog($"   traj {kind} tex{tex}#{p.dumpN} t={ageTicks}/{life0} pos=({wp.x:F1},{wp.y:F1},{wp.z:F1})" +
                    $" WORLD=({wpos.x:F1},{wpos.y:F1},{wpos.z:F1})" +
                    $" scale=({ws.x:F2},{ws.y:F2},{ws.z:F2}) animS=({aS.x:F2},{aS.y:F2},{aS.z:F2})" +
                    $" rotDeg=({p.rot.x:F0},{p.rot.y:F0},{p.rot.z:F0}) born={p.bornTick} a={alpha}");
        }

        // Per-particle velocity tilt within the posSpread cone (engine: Math_RotateAroundAxis of vel by the
        // randomized posSpread at birth). CRITICAL: the engine randomizes posSpread as `rand%s − rand%s`, a
        // TRIANGULAR distribution biased toward 0 → most tilts are SMALL, so an up-velocity spray stays mostly
        // diagonal-UP with only a few wide ones. A uniform ±s (what we had) pushed most particles to ~horizontal,
        // which was wrong. Frida-verified: 200's tex31 (posSpread 34) born ~23° off vertical; FINISHED sparks
        // (posSpread ~100) born 32–82° up, almost none horizontal/down. posSpread units = degrees.
        // one turbulence axis: random angle in [lo,hi], clamped to ±|clamp| when clamp≠0 (engine FUN_004beb80 + clamp).
        static float TurbAxis(float lo, float hi, float clamp)
        {
            if (lo == 0f && hi == 0f) return 0f;
            float v = UnityEngine.Random.Range(Mathf.Min(lo, hi), Mathf.Max(lo, hi));
            if (clamp != 0f) { float c = Mathf.Abs(clamp); v = Mathf.Clamp(v, -c, c); }
            return v;
        }

        // a random unit vector perpendicular to v (the fixed per-particle turbulence axis). Random azimuth around v so
        // different sparks zigzag in different planes → the ensemble shimmers in all directions.
        static Vector3 RandPerp(Vector3 v)
        {
            if (v.sqrMagnitude < 1e-10f) return Vector3.up;
            Vector3 r = new Vector3(UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f));
            Vector3 p = Vector3.Cross(v, r);
            if (p.sqrMagnitude < 1e-6f) p = Vector3.Cross(v, Vector3.up);
            if (p.sqrMagnitude < 1e-6f) p = Vector3.Cross(v, Vector3.right);
            return p.normalized;
        }

        // engine FUN_004bd4d0/FUN_004bd240: rotate vel by (ax around basisA, ay around basisB), basis RECOMPUTED from
        // vel each call (basisA = reference×vel, reference = world-up, or world-right when vel is vertical). The
        // per-tick recompute is why random ±ang makes the pitch BOB (flicker) rather than accumulate into a drift.
        static Vector3 EngineTurbRotate(Vector3 v, float ax, float ay)
        {
            if (v.sqrMagnitude < 1e-10f || (ax == 0f && ay == 0f)) return v;
            Vector3 vn = v.normalized;
            Vector3 reference = (Mathf.Abs(vn.x) < 1e-4f && Mathf.Abs(vn.z) < 1e-4f) ? Vector3.right : Vector3.up;
            Vector3 basisA = Vector3.Cross(reference, vn).normalized;
            Vector3 basisB = Vector3.Cross(vn, basisA).normalized;
            return Quaternion.AngleAxis(ay, basisB) * (Quaternion.AngleAxis(ax, basisA) * v);
        }

        // per-particle velocity-scale jitter (engine FUN_004bee20): 1.0 + tri(±w), tri = (rand15−rand15)/32768 (biased
        // toward 0); if the result goes negative (only when w>1) the engine returns its reciprocal. w=0 → exactly 1.
        static float RandScaleJitter(float w)
        {
            if (w == 0f) return 1f;
            float tri = (UnityEngine.Random.Range(0, 32768) - UnityEngine.Random.Range(0, 32768)) / 32768f;
            float s = tri * w + 1f;
            return s < 0f ? 1f / s : s;
        }

        public static bool UniformTilt = false;   // F4/experiment toggle: uniform vs triangular spread
        static Vector3 TiltVel(Vector3 vel, int spreadX, int spreadZ)
        {
            if ((spreadX == 0 && spreadZ == 0) || vel == Vector3.zero) return vel;
            float rx, rz;
            if (UniformTilt)
            {
                rx = UnityEngine.Random.Range(-spreadX, spreadX);   // uniform ±spreadX
                rz = UnityEngine.Random.Range(-spreadZ, spreadZ);
            }
            else
            {
                rx = UnityEngine.Random.Range(0f, spreadX) - UnityEngine.Random.Range(0f, spreadX);   // triangular ±spreadX
                rz = UnityEngine.Random.Range(0f, spreadZ) - UnityEngine.Random.Range(0f, spreadZ);   // triangular ±spreadZ
            }
            return Quaternion.Euler(rx, 0f, rz) * vel;
        }

        void SetCol(Material m, float r, float g, float b, float a)
        {
            // r/g/b/a here ARE the engine's exact per-frame diffuse(+specular) bytes (0..255) — Frida-verified to
            // match the live client to ±1. So map straight through (k = 1/255); _bright stays a user gain (F4),
            // default 1.0 = faithful. The glow comes from additive blend + alpha + the 15 overlapping rings, NOT a
            // fudge factor. The ring's rainbow hue-cycle (white→orange→green→magenta) is exactly these channels.
            float k = _bright / 255f;
            var c = new Color(r * k * Tint.r, g * k * Tint.g, b * k * Tint.b, Mathf.Clamp01(a / 255f));
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", c); else m.color = c;
        }

        // ---- geometry ----

        // exact decompiled ring band (FUN_004beeb0): seg quads, inner rx @ y=−rh/2, outer ry @ y=+rh/2,
        // x = r·(cos−sin), z = r·(cos+sin). GEOMETRY is Y-up (Frida yAxis = (0,+scale,0), m11>0 — NOT flipped).
        // The "upside-down glow" was the TEXTURE V: aef_3_01 is upward light spikes with bright tips at the image top
        // (= v1 in Unity); the old UV put v1 at the mesh bottom → spikes pointed DOWN. Flipped so bright tips → +h
        // (spikes radiate UP). Double-sided.
        static Mesh BuildRing(EftEmitter em)
        {
            int seg = Mathf.Max(3, em.Seg);
            float rx = em.Rx, ry = em.Ry, h = em.Rh * 0.5f;
            int vc = seg * 4;
            var verts = new Vector3[vc]; var uv = new Vector2[vc]; var col = new Color32[vc]; var tris = new int[seg * 12];
            var white = new Color32(255, 255, 255, 255);
            for (int s = 0; s < seg; s++)
            {
                float a0 = s * Mathf.PI * 2f / seg, a1 = (s + 1) * Mathf.PI * 2f / seg;
                float c0 = Mathf.Cos(a0), s0 = Mathf.Sin(a0), c1 = Mathf.Cos(a1), s1 = Mathf.Sin(a1);
                int v = s * 4;
                verts[v + 0] = new Vector3(c0 * rx - s0 * rx, -h, (s0 + c0) * rx); uv[v + 0] = new Vector2(0f, 0f);
                verts[v + 1] = new Vector3(c1 * rx - s1 * rx, -h, (s1 + c1) * rx); uv[v + 1] = new Vector2(1f, 0f);
                verts[v + 2] = new Vector3(c1 * ry - s1 * ry, +h, (s1 + c1) * ry); uv[v + 2] = new Vector2(1f, 1f);
                verts[v + 3] = new Vector3(c0 * ry - s0 * ry, +h, (s0 + c0) * ry); uv[v + 3] = new Vector2(0f, 1f);
                col[v] = col[v + 1] = col[v + 2] = col[v + 3] = white;
                int ti = s * 12;
                tris[ti + 0] = v; tris[ti + 1] = v + 2; tris[ti + 2] = v + 1;
                tris[ti + 3] = v; tris[ti + 4] = v + 3; tris[ti + 5] = v + 2;
                tris[ti + 6] = v; tris[ti + 7] = v + 1; tris[ti + 8] = v + 2;   // back faces (double-sided)
                tris[ti + 9] = v; tris[ti + 10] = v + 2; tris[ti + 11] = v + 3;
            }
            var m = new Mesh { name = "eft-ring" };
            m.vertices = verts; m.uv = uv; m.colors32 = col; m.triangles = tris; m.RecalculateBounds();
            return m;
        }

        // De-lean a curved effect mesh into a perfectly vertical one (300 AEF_3_00). The AEF_3_00 geometry curves
        // FORWARD (+Z grows with height Y) — the "彎彎的" the user reported. Removing the per-height-level MEAN Z makes
        // every level's spine sit at z=0 (no forward lean) while preserving the V cross-section thickness (center vs
        // sides) AND the width/height/UVs, so the flame keeps its silhouette and volume but rises straight up. Verified
        // against AEF03_00.MSH (15 verts, 5 height levels: sides x=±0.22 z≈lean, centre x=0 z≈lean+0.045). 200 keeps the
        // raw mesh (its curve is correct). Cached per source so the clone is built once.
        static readonly Dictionary<Mesh, Mesh> _straightCache = new Dictionary<Mesh, Mesh>();
        static Mesh StraightenMesh(Mesh src)
        {
            if (src == null) return null;
            if (_straightCache.TryGetValue(src, out var cached)) return cached;
            var verts = src.vertices;
            var nv = (Vector3[])verts.Clone();
            var done = new bool[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                if (done[i]) continue;
                float y = verts[i].y, sumZ = 0f; int cnt = 0;
                for (int j = 0; j < verts.Length; j++)
                    if (Mathf.Abs(verts[j].y - y) < 1e-3f) { sumZ += verts[j].z; cnt++; }
                float meanZ = sumZ / cnt;   // the forward lean at this height
                for (int j = 0; j < verts.Length; j++)
                    if (Mathf.Abs(verts[j].y - y) < 1e-3f) { nv[j].z = verts[j].z - meanZ; done[j] = true; }
            }
            var m = new Mesh { name = src.name + "-straight" };
            m.vertices = nv;
            m.uv = src.uv;
            if (src.colors32 != null && src.colors32.Length == nv.Length) m.colors32 = src.colors32;
            m.subMeshCount = src.subMeshCount;
            for (int s = 0; s < src.subMeshCount; s++) m.SetTriangles(src.GetTriangles(s), s);
            m.RecalculateBounds(); m.RecalculateNormals();
            _straightCache[src] = m;
            return m;
        }

        static Mesh _quad;
        static Mesh Quad()
        {
            if (_quad != null) return _quad;
            var m = new Mesh { name = "eft-quad" };
            m.vertices = new[] { new Vector3(-.5f, -.5f), new Vector3(.5f, -.5f), new Vector3(.5f, .5f), new Vector3(-.5f, .5f) };
            m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };   // double-sided
            m.RecalculateBounds();
            return _quad = m;
        }

        static Mesh _quadBB;
        // single-sided quad for camera-facing billboards (front only) → 1× additive brightness so the spinning-starburst
        // twinkle isn't washed out by a double-draw. Front faces +Z (toward the camera after LookRotation).
        static Mesh QuadBillboard()
        {
            if (_quadBB != null) return _quadBB;
            var m = new Mesh { name = "eft-quad-bb" };
            m.vertices = new[] { new Vector3(-.5f, -.5f), new Vector3(.5f, -.5f), new Vector3(.5f, .5f), new Vector3(-.5f, .5f) };
            m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };   // single-sided = the camera-facing half of Quad() (so it stays visible under Cull Back too)
            m.RecalculateBounds();
            return _quadBB = m;
        }

        // soft radial glow for NULL-texture emitters (slots 5-8) — additive puffs
        static Texture2D MakeGlow()
        {
            const int S = 64; var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
            var px = new Color32[S * S]; float c = (S - 1) / 2f;
            for (int y = 0; y < S; y++) for (int x = 0; x < S; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                float a = Mathf.Clamp01(1f - d); a *= a;
                byte v = (byte)(a * 255);
                px[y * S + x] = new Color32(255, 255, 255, v);
            }
            t.SetPixels32(px); t.Apply(); t.wrapMode = TextureWrapMode.Clamp;
            return t;
        }
    }
}
