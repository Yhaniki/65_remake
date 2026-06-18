using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>A resolved 3D-mesh effect (xmesh\list.txt entry): the Unity mesh + one texture per material submesh.
    /// Built by the host (Step1Game.ResolveEftMesh) from the .MSH via <see cref="SceneLoader"/>; consumed by
    /// <see cref="EftEffect"/> when an emitter has a non-zero <see cref="EftEmitter.MeshIdx"/>.</summary>
    public sealed class EftMeshData
    {
        public Mesh Mesh;
        public Texture2D[] SubTex;   // one per mesh.subMeshCount (may contain nulls → drawn untextured/white)
    }

    /// <summary>
    /// Faithful runtime for a parsed <see cref="EftFile"/> — replicates the SDO 3DEFT particle engine
    /// (Particle_SpawnFromEmitter / _InitFromTemplate / _Update / _Draw, decompiled in scene/030).
    /// Fixed 20ms (50Hz) simulation; per-particle 0.5-pivot scale channels, accumulating rotation, additive draw.
    /// Effect transform = this GameObject at the pelvis-on-floor with uniform scale (30 for 100COMBO); each
    /// particle is a child whose localScale = baseSize×animScale, localRotation = Euler(accumulated channels),
    /// localPosition = integrated position — which reproduces the engine's S·R·T·Owner world matrix.
    /// </summary>
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

        // COMBO-BURST EXPOSURE RAMP for the 200/300 fountain balls (tex30/tex31 billboards). The official burst is
        // OVEREXPOSED — a white-hot blob at spawn — then the exposure FALLS so the ball's true colour (cyan/violet) and
        // the AEF_3_00 blue mesh riding behind it emerge, before alpha fades it out. So the ball-core additive tint is
        // multiplied by BallCoreIntensity at birth and decays to 1.0 (real colour) over the first BallCoreExpoFrac of
        // life. (A CONSTANT boost kept the balls white the whole life and drowned the blue mesh — exactly the bug the
        // user hit.) Only the balls are exposure-ramped; trails/mesh/ring/glow stay at 1.0 so their real hue shows.
        public static float BallCoreIntensity = 5f;    // peak exposure at spawn (white-hot); 1 = no overexposure
        public static float BallCoreExpoFrac = 0.3f;   // fraction of life over which exposure decays back to 1 (real colour)
        // The 200/300 slot0 AEF_3_00 blue MESH that rides each ball has a DIM texture, so at 1× it's drowned by the
        // bright fountain (the user couldn't see it). Boost its additive so the blue mesh shows once the balls' spawn
        // exposure fades. trails/ring/disk untouched. F4-tunable.
        public static float MeshIntensity = 5f;
        // The 300 AEF_3_00 mesh rides a ball but its OWN scale channels change at a different RATE than the ball (too
        // thin at spawn, balloons tall mid-life), so its width never matches the ball. Instead it's driven by the
        // PARENT BALL's width curve (same big→small rate); MeshWidthMatch scales that to the ball's absolute width.
        public static float MeshWidthMatch = 0.3f;
        // 200 spawns 15 AEF_3_00 meshes (slot1 external emit 15) but the official shows only ~5-6 → cap the count.
        public static int MeshMax200 = 6;
        // 300's mesh tracks the ball width (slow shrink); the official shrinks it FASTER and SMALLER, so multiply the
        // tracked size by lerp(1 → MeshShrinkEnd) over life. Lower = shrinks smaller. (300 only; 200 uses own curve.)
        public static float MeshShrinkEnd = 0.15f;
        // 300 AEF_3_00 spec: at SPAWN it's MeshStartW× wider and MeshStartH× longer (tall vertical flame), easing to the
        // shrunk end size over life. Bottom anchored to the ball's bottom, fully vertical (no tilt). MeshAlpha dims it.
        public static float MeshStartW = 1.5f;    // start width multiplier (→1 by end)
        public static float MeshStartH = 3f;       // start length/height multiplier (→1 by end)
        public static float MeshAlpha = 0.8f;   // AEF_3_00 opacity vs the raw alpha curve
        public static float MeshDropFrac = 0.33f;  // 300 mesh vertical anchor: 0 = on the ball (keeps up), 1 = at ball bottom

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
            if (_trajW == null) { _trajW = new System.IO.StreamWriter("H:/65_remake/mysim-traj.log", false) { AutoFlush = true }; }
            _trajW.WriteLine(s);
        }

        Transform _follow; float _effScale; Camera _cam;
        Material _addMat; int _layer; float _bright;
        float _glowMul, _glowSpread;   // outer-glow halo: intensity (0=off) + how much bigger than the particle
        float _acc; Vector3 _spawnPos; int _maxTicks; int _tick;
        int _mesh32Count;   // # of non-orient (200 ground) AEF_3_00 meshes spawned this effect — capped at MeshMax200
        readonly List<P> _ps = new List<P>();
        readonly List<P> _pending = new List<P>();   // particles spawned this tick (added after the iteration)
        Func<int, Texture2D> _texResolver;
        Func<int, EftMeshData> _meshResolver;   // xmesh index → 3D mesh (null when the host supplies none)
        readonly Dictionary<int, EftEmitter> _slotMap = new Dictionary<int, EftEmitter>();
        static Texture2D _glow;

        sealed class P
        {
            public EftEmitter E;
            public int life, life0, spawnDelay, startDelay;
            public Vector3 rot, pos, baseSize, vel;   // vel = per-particle velocity, randomly TILTED by posSpread (fan-out)
            public Transform tr; public Material mat;
            public Material[] meshMats;   // 3D-mesh submesh materials (non-null only for mesh particles); tinted as a group
            public Transform glowTr; public Material glowMat;   // outer-glow halo child (null when glow off)
            public bool ring, orient, started, invisible, isTrail, isMesh;
            public bool isBallCore;   // 200/300 burst ball billboard (tex30/31) → optional BallCoreIntensity boost in StepParticle
            // ATTACH-TO-PARENT (engine word[0x37]): when attach!=0 this particle RIDES `parent` — each frame its world
            // position = parent.pos + (parent.rot × its own local drift). 200/300 slot0 mesh locks to its parent this way.
            // lockToParent: when the parent itself MOVES (300's rising orb), suppress the own drift so the mesh stays
            // welded to that orb (one ball, one mesh); a STATIONARY parent (200's ground external) keeps the drift so the
            // mesh fans outward into the ground curtain.
            public int attach; public P parent; public bool lockToParent;
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
            _glowMul = glow; _glowSpread = glowSpread;
            _spawnPos = transform.position;
            transform.localScale = Vector3.one * effScale;
            if (_glow == null) _glow = MakeGlow();

            // FAITHFUL spawn tree (Particle_InitEmitterInstance): spawn only the ROOT emitters; each particle then
            // sub-emits ITS OWN children (the DFS tree EftFile parsed via NumTrig) at each child's at-time. The
            // other populated slots NEVER spawn unless they're in the tree (e.g. 100COMBO's ring_l X-twinkles are
            // children only in 400COMBO). Verified against the live game (Frida log: 100 = naga00 + 15 rings).
            foreach (var em in file.Emitters) _slotMap[em.Slot] = em;
            for (int k = 0; k < file.RootSlots.Length; k++)
                if (_slotMap.TryGetValue(file.RootSlots[k], out var rem))
                {
                    if (OnlyRootSlot >= 0 && file.RootSlots[k] != OnlyRootSlot) continue;   // DEBUG slot isolation
                    SpawnEmitter(rem, file.RootDelays[k], true);
                }
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
            int emit = Mathf.Max(1, em.Emit);
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
            // PER-PARTICLE LIFE JITTER (engine InitFromTemplate: life += rand%j − rand%j). Without it every particle
            // of an emitter lives the same time → flies to the SAME height; the jitter (e.g. 200 tex31 j=10 → life
            // 40-60, 400 tex96 j=20 → life 30-70) is what makes a spray reach MANY different heights.
            if (em.LifeJit > 0)
            {
                int j = Mathf.Min(em.LifeJit, life0 - 1);
                if (j > 0) life0 = Mathf.Max(1, life0 + UnityEngine.Random.Range(0, j + 1) - UnityEngine.Random.Range(0, j + 1));
            }
            bool isTrail = (em.Flags & 0x20000) != 0;
            // SPAWN SCATTER (engine: Particle_RandomConeVelocity adds a random offset to pos when word[0x1f3]≠0). A
            // horizontal annulus radius [ConeInner,ConeMag] at a random angle — so particles DON'T all start dead
            // centre. 400 tex96 = [0.1,0.4] (×30 ≈ 3-12u off-centre) = why the official X-cross fans wide; 200
            // externals = [0,0.1]. This is the "初始位置不是全部從中心噴" the user saw.
            Vector3 coneOff = Vector3.zero;
            float myAzim = parentAzim;   // default: inherit the parent's cone azimuth
            if (em.ConeMag > 0f)
            {
                float r = UnityEngine.Random.Range(em.ConeInner, em.ConeMag);
                myAzim = UnityEngine.Random.Range(0f, 2f * Mathf.PI);   // this particle's OWN random cone azimuth
                coneOff = new Vector3(r * Mathf.Cos(myAzim), 0f, r * Mathf.Sin(myAzim));
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
            // 3D-MESH ATTACH (engine particle word[6] → xmesh\list.txt): a non-null resolve makes this particle render
            // that mesh at its own world matrix (S·R·T) instead of the flat trail/quad — the 200/300COMBO slot0 blue
            // aef03_00 mesh (MeshIdx 32). The host resolves+caches it; null falls back to the prior quad behaviour.
            // SCOPE TO idx 32 ONLY: 100/400/500's emitters ALSO carry MeshIdx (100/101 = column_00/01, naga01/naga06)
            // and rendering THOSE replaced their correct billboards (naga00 star / tex96 X-cross / tex20 ring) with
            // wrong column meshes — i.e. the regression that broke 100/400/500. Only AEF_3_00 (32) is wanted for now.
            EftMeshData md = (EnableMesh && em.MeshIdx == 32 && _meshResolver != null) ? _meshResolver(em.MeshIdx) : null;
            bool isMesh = md != null && md.Mesh != null;
            // 200's AEF_3_00 meshes ride a NON-orient ground EXTERNAL (slot1 emit 15 → 15 meshes), but the official
            // shows only ~5-6. Cap the count for the non-orient (200) case; the excess render nothing. 300's meshes
            // ride orient BALLS (12) and are NOT capped.
            bool capMesh = false;
            if (isMesh && (parentP == null || !parentP.E.Orient))
            {
                if (_mesh32Count >= MeshMax200) capMesh = true;
                else _mesh32Count++;
            }
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
                pos = (em.AttachMode != 0 ? em.Pos : em.Pos + parentPos) + coneOff,
                attach = em.AttachMode,
                parent = em.AttachMode != 0 ? parentP : null,
                // a mesh riding a MOVING parent welds to it (300's rising orb → no drift); riding a STATIONARY parent it
                // keeps its drift (200's ground external → fans out). parentVel is the parent's spawn velocity.
                lockToParent = em.AttachMode != 0 && parentVel.sqrMagnitude > 1e-8f,
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
                rot = em.InitRot + new Vector3(
                    UnityEngine.Random.Range(-1f, 1f) * em.RotJit.x,
                    UnityEngine.Random.Range(-1f, 1f) * em.RotJit.y,
                    UnityEngine.Random.Range(-1f, 1f) * em.RotJit.z),
                baseSize = bs,
                ring = em.IsRing,
                invisible = (em.Flags & 0x40000) != 0 || capMesh,   // capped excess 200 meshes render nothing
                isTrail = isTrail,
                isMesh = isMesh,
                // billboards (0x10000) face the camera via their world matrix (rotation from the template initRot +
                // channels — e.g. aef_1_07's 90° = flat). TRAILS are world-quads oriented by their euler channels and
                // are NOT billboarded (the flag lacks 0x10000), so force orient off for them too.
                orient = em.Orient && !isTrail,
                // the 200/300 fountain balls (oriented billboard, tex31=AEF_4_03 / tex30=AEF_4_02) — the only emitters
                // BallCoreIntensity boosts (their texture core is near-white, so a brighter additive enlarges the
                // white-hot blob; the burst camera's gamma-clip is the main mechanism, this is just an extra lever).
                isBallCore = em.Orient && !isTrail && !isMesh && (em.TexIdx == 30 || em.TexIdx == 31),
                // texture flipbook (only when this emitter has >1 frame); frame 0 is the initial texture already set
                frameTex = frames, frameCount = frames != null ? em.FrameCount : 1,
                frameHold = em.FrameHold, frameLoop = em.FrameLoop,
            };
            // Face the flare the SAME way its head drifts (trailYaw) → quad faces the way it moves, exactly like the
            // engine (head drift ∥ axZ, the quad's local +Z). Spinning about the vertical +Y stretch axis leaves axY
            // untouched, so the streak stays a vertical flare; only its azimuth (axX/axZ) varies per particle.
            if (isTrail) p.rot.y = trailYaw;

            var go = new GameObject(p.isMesh ? "eft-mesh" : p.isTrail ? "eft-trail" : em.IsRing ? "eft-ring" : "eft-bb");
            go.transform.SetParent(transform, false);
            go.layer = _layer;
            if (p.invisible)
            {
                // 0x40000 external carrier: no geometry, exists only to inherit a position + sub-emit its trail child.
            }
            else if (p.isMesh)
            {
                // 3D-MESH (engine word[6] mesh): draw the resolved xmesh mesh at the particle's world matrix (the
                // localPosition/Rotation/Scale below reproduce the engine's S·R·T·Owner). One additive material per
                // material submesh (clone of the combo additive mat + that submesh's DDS), so aef03_00's blue
                // textures show and fade by the trail's alpha channel. NOT billboarded (trail flag lacks 0x10000).
                go.AddComponent<MeshFilter>().mesh = md.Mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
                int subN = Mathf.Max(1, md.Mesh.subMeshCount);
                var mats = new Material[subN];
                for (int s = 0; s < subN; s++)
                {
                    var mat = _addMat != null ? new Material(_addMat) : new Material(Shader.Find("Sprites/Default"));
                    Texture2D st = (md.SubTex != null && s < md.SubTex.Length) ? md.SubTex[s] : null;
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
                var mat = _addMat != null ? new Material(_addMat) : new Material(Shader.Find("Sprites/Default"));
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
                if (worldQuad && LumWorldQuad && LumShader() != null) mat = new Material(LumShader());
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
            p.tr = go.transform;
            go.SetActive(false);
            _pending.Add(p);
        }

        void Flush() { if (_pending.Count > 0) { _ps.AddRange(_pending); _pending.Clear(); } }

        void LateUpdate()
        {
            // re-pin to the dancer's pelvis-on-floor each frame (engine re-calls SetTransformAnimated)
            transform.position = _follow != null ? _follow.position : _spawnPos;
            transform.localScale = Vector3.one * _effScale;
            if (_cam == null) foreach (var c in Camera.allCameras) if (!c.orthographic) { _cam = c; break; }

            _acc += Time.deltaTime;
            while (_acc >= Step) { _acc -= Step; Tick(); }

            // billboards face the camera every frame (engine billboard branch); rings keep their world matrix.
            // ROLL = the accumulating rotZ spin (chD, e.g. naga00 −3.6°/tick) PLUS the per-particle random rotY phase
            // (RotJit.y, e.g. naga00 ±180°). Without the rotY phase, ALL naga00 sparks start at roll 0 and spin in
            // SYNC → the 4-point-starburst rays sweep together = one global pulse that reads as "not flickering".
            // Adding the random phase desyncs them → individual sparks twinkle out of phase = the firework's shimmer.
            if (_cam != null)
                foreach (var p in _ps)
                    if (p.orient && p.tr.gameObject.activeSelf)
                        p.tr.rotation = Quaternion.LookRotation(p.tr.position - _cam.transform.position, _cam.transform.up)
                                        * Quaternion.Euler(0f, 0f, p.rot.z + p.rot.y);
        }

        void Tick()
        {
            _tick++;
            int alive = 0;
            foreach (var p in _ps)
            {
                if (p.spawnDelay > 0) { p.spawnDelay--; if (p.tr.gameObject.activeSelf) p.tr.gameObject.SetActive(false); alive++; continue; }
                if (p.startDelay > 0) { p.startDelay--; if (p.tr.gameObject.activeSelf) p.tr.gameObject.SetActive(false); alive++; continue; }
                if (p.life <= 0) { if (p.tr.gameObject.activeSelf) p.tr.gameObject.SetActive(false); continue; }
                if (!p.tr.gameObject.activeSelf) p.tr.gameObject.SetActive(true);
                p.started = true; alive++;
                StepParticle(p);
            }
            Flush();
            if (_tick > 2 && alive == 0 && _pending.Count == 0) Destroy(gameObject);
            if (_tick > _maxTicks + 60) Destroy(gameObject);
        }

        void StepParticle(P p)
        {
            var em = p.E;
            p.life--;
            int life0 = p.life0;
            int ageTicks = life0 - p.life;
            float t = ageTicks / (float)life0;   // age 0..1

            // SUB-EMIT this particle's children at each child's at-time (engine: trigType 2 = at parent age==atTime;
            // 0 = on birth). This is what staggers 100COMBO's rings to ~age 8, and 400's billboards to the rings'
            // mid-life. naga00 EMIT 3 → each fires its 2 ring children → the 15 rings the live game shows.
            if (p.kidSpawned != null)
                for (int i = 0; i < em.Children.Count; i++)
                {
                    if (p.kidSpawned[i]) continue;
                    var c = em.Children[i];
                    int fireAt = c.TrigType == 0 ? 0 : Mathf.RoundToInt(c.AtTime);   // trigType 2/3 use at-time
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
                    }
                }
                p.frameCounter++;
            }

            // per-axis scale channels (0xe/0xf/0x10) → animScale, with the 0.5-pivot remap.
            // Verified EXACT vs the live game (Frida): ring X/Z bloom 0.45→1.02→0.30, Y grows 0.31→2.02. No trim.
            Vector3 animScale = new Vector3(em.Ch[0xe].Scale(t), em.Ch[0xf].Scale(t), em.Ch[0x10].Scale(t));
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
            p.rot.x += em.Ch[0xb].RangedMin(t);
            p.rot.y += em.Ch[0xc].RangedMin(t);
            p.rot.z += em.Ch[0xd].RangedMin(t);
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
                float scaleVel = em.Ch[0].Scale(t);
                p.pos += p.vel * (scaleVel * p.velScale);
                float ageLin = life0 - p.life;
                p.pos.y -= ageLin * em.GravAccel + em.GravBase;
            }

            if (DumpTraj && !p.invisible) DumpTrajectory(p, ageTicks, life0, t);

            // colour = diffuse (ch2/3/4 = R/G/B) + specular (ch6/7/5 = R/G/B), alpha = ch1. D3D adds specular to
            // diffuse; keeping both preserves the real per-particle hue (blue aef_1_07, orange aef_4_03, etc.).
            float r = em.Ch[2].Ranged(t) + em.Ch[6].Ranged(t);
            float g = em.Ch[3].Ranged(t) + em.Ch[7].Ranged(t);
            float b = em.Ch[4].Ranged(t) + em.Ch[5].Ranged(t);
            float a = em.Ch[1].Ranged(t);
            // ground-streak trail: override the pale-pink diffuse with the official's dimmer BLUE (keep the alpha fade).
            if (p.isTrail && TrailOverride)
            {
                r = TrailTint.r * TrailBright * 255f;
                g = TrailTint.g * TrailBright * 255f;
                b = TrailTint.b * TrailBright * 255f;
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
                    p.tr.localPosition = p.parent.pos + new Vector3(0f, -ballHalfH * MeshDropFrac, 0f);
                    p.tr.localRotation = Quaternion.identity;
                }
                else
                {
                    Quaternion prot = Quaternion.Euler(p.parent.rot);
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
            p.tr.localScale = Vector3.Scale(p.baseSize, animScale);
            // trail streak: animScale.y already stretches local +Y into the streak; TrailWidthMul tunes its width (local X).
            if (p.isTrail && TrailWidthMul != 1f) { var s = p.tr.localScale; s.x *= TrailWidthMul; p.tr.localScale = s; }
            // billboards (p.orient): oriented to camera in LateUpdate
            if (p.meshMats != null)   // 3D mesh (AEF_3_00 blue mesh): tint every submesh as a group; alpha fades it
            {
                // MeshIntensity boosts ONLY the AEF_3_00 blue mesh (MeshIdx 32, 200/300) so it isn't drowned by the
                // bright balls — its texture is dim. Other tiers' meshes (100/400/500 column_00/01) stay at 1× so they
                // are NOT broken. (DebugMeshOnly isolates the mesh at a fixed 5×.)
                float mci = DebugMeshOnly ? 5f : (em.MeshIdx == 32 ? MeshIntensity : 1f);
                float ma = em.MeshIdx == 32 ? MeshAlpha : 1f;   // AEF_3_00 opacity = 80% of the raw alpha curve
                for (int i = 0; i < p.meshMats.Length; i++) SetCol(p.meshMats[i], r * mci, g * mci, b * mci, a * ma);
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
                float ci = p.isBallCore
                    ? Mathf.Lerp(BallCoreIntensity, 1f, Mathf.Clamp01(t / Mathf.Max(0.01f, BallCoreExpoFrac)))
                    : 1f;
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
            DumpLog($"   traj {kind} tex{tex}#{p.dumpN} t={ageTicks}/{life0} pos=({wp.x:F1},{wp.y:F1},{wp.z:F1})" +
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
            var c = new Color(r * k, g * k, b * k, Mathf.Clamp01(a / 255f));
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
