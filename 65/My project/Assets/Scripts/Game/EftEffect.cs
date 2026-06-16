using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
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

        Transform _follow; float _effScale; Camera _cam;
        Material _addMat; int _layer; float _bright;
        float _glowMul, _glowSpread;   // outer-glow halo: intensity (0=off) + how much bigger than the particle
        float _acc; Vector3 _spawnPos; int _maxTicks; int _tick;
        readonly List<P> _ps = new List<P>();
        readonly List<P> _pending = new List<P>();   // particles spawned this tick (added after the iteration)
        Func<int, Texture2D> _texResolver;
        readonly Dictionary<int, EftEmitter> _slotMap = new Dictionary<int, EftEmitter>();
        static Texture2D _glow;

        sealed class P
        {
            public EftEmitter E;
            public int life, life0, spawnDelay, startDelay;
            public Vector3 rot, pos, baseSize, vel;   // vel = per-particle velocity, randomly TILTED by posSpread (fan-out)
            public Transform tr; public Material mat;
            public Transform glowTr; public Material glowMat;   // outer-glow halo child (null when glow off)
            public bool ring, orient, started, invisible;
            public bool[] kidSpawned;   // per E.Children: has this particle sub-emitted that child yet?
        }

        public void Init(EftFile file, float effScale, Transform follow, Func<int, Texture2D> texResolver,
                         Material addMat, int layer, float bright, float glow = 0f, float glowSpread = 0.6f)
        {
            _effScale = effScale; _follow = follow; _texResolver = texResolver; _addMat = addMat; _layer = layer; _bright = bright;
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
                if (_slotMap.TryGetValue(file.RootSlots[k], out var rem)) SpawnEmitter(rem, file.RootDelays[k], true);
            _maxTicks = 0;
            foreach (var em in file.Emitters) _maxTicks = Mathf.Max(_maxTicks, em.StartDelay + em.SpawnDelay + Mathf.Abs(em.Life0) + 20);
            Flush();
        }

        // spawn EMIT particles from an emitter. 0x40000 = external geometry: the engine's quad draw skips it, but it
        // still sub-emits its children — so spawn it as an INVISIBLE carrier (no quad) so its children appear.
        void SpawnEmitter(EftEmitter em, int extraDelay, bool isRoot, Vector3 parentPos = default)
        {
            var tex = em.HasTex ? _texResolver(em.TexIdx) : null;
            int emit = Mathf.Max(1, em.Emit);
            for (int n = 0; n < emit; n++) Spawn(em, tex, extraDelay, isRoot, parentPos);
        }

        void Spawn(EftEmitter em, Texture2D tex, int extraDelay, bool isRoot, Vector3 parentPos)
        {
            Vector3 bs = em.BaseSize;
            int life0 = Mathf.Max(1, Mathf.Abs(em.Life0));
            bool isTrail = (em.Flags & 0x20000) != 0;
            var p = new P
            {
                E = em,
                life = life0, life0 = life0,
                kidSpawned = em.Children.Count > 0 ? new bool[em.Children.Count] : null,
                spawnDelay = em.SpawnDelay + Mathf.Max(0, extraDelay),
                startDelay = em.StartDelay > 0 ? UnityEngine.Random.Range(0, em.StartDelay) : 0,   // staggered (rand % delay)
                // sub-emitted particles inherit the PARENT's position (engine: child[0x17..19] += parent matrix
                // translation). Without this, trails sub-emitted by the scattered externals all spawn at the origin
                // and (with their own +Z velocity) fly off backward together = the "light flying backward" artifact.
                pos = em.Pos + parentPos,
                // per-particle velocity, TILTED by a random angle within posSpread (engine: Math_RotateAroundAxis of
                // vel by rand(±posSpreadX°, ±posSpreadZ°) at spawn). This is what fans a spray OUT into a fountain —
                // 200/300's billboards (vel up + posSpread 34/38) must scatter, not rise in lockstep. Trails are
                // ribbons anchored to their (near-stationary) parent — they must NOT fly with a free velocity.
                vel = isTrail ? Vector3.zero : TiltVel(em.Vel, em.PosSpreadX, em.PosSpreadZ),
                // rotation starts at the template's initial rotation + jitter; the update accumulates the channels
                // onto it. This is what makes aef_1_07 lie FLAT (its [0x1a]=90°) and spin randomly (Y jitter 360°).
                rot = em.InitRot + new Vector3(
                    UnityEngine.Random.Range(-1f, 1f) * em.RotJit.x,
                    UnityEngine.Random.Range(-1f, 1f) * em.RotJit.y,
                    UnityEngine.Random.Range(-1f, 1f) * em.RotJit.z),
                baseSize = bs,
                ring = em.IsRing,
                invisible = (em.Flags & 0x40000) != 0,
                // billboard (0x10000) and motion-trail (0x20000) face the camera; ring + plain world-matrix quads
                // use their world matrix (rotation from the template initRot + channels — e.g. aef_1_07's 90° = flat).
                orient = em.Orient || (em.Flags & 0x20000) != 0,
            };

            var go = new GameObject(em.IsRing ? "eft-ring" : "eft-bb");
            go.transform.SetParent(transform, false);
            go.layer = _layer;
            if (!p.invisible)
            {
                go.AddComponent<MeshFilter>().mesh = em.IsRing ? BuildRing(em) : Quad();
                var mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
                var mat = _addMat != null ? new Material(_addMat) : new Material(Shader.Find("Sprites/Default"));
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

            // billboards face the camera every frame (engine billboard branch); rings keep their world matrix
            if (_cam != null)
                foreach (var p in _ps)
                    if (p.orient && p.tr.gameObject.activeSelf)
                        p.tr.rotation = Quaternion.LookRotation(p.tr.position - _cam.transform.position, _cam.transform.up)
                                        * Quaternion.Euler(0f, 0f, p.rot.z);
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
                    if (c.TrigType != 1 && ageTicks >= fireAt) { p.kidSpawned[i] = true; SpawnEmitter(c, 0, false, p.pos); }
                }

            // per-axis scale channels (0xe/0xf/0x10) → animScale, with the 0.5-pivot remap.
            // Verified EXACT vs the live game (Frida): ring X/Z bloom 0.45→1.02→0.30, Y grows 0.31→2.02. No trim.
            Vector3 animScale = new Vector3(em.Ch[0xe].Scale(t), em.Ch[0xf].Scale(t), em.Ch[0x10].Scale(t));
            // rotation channels (0xb/0xc/0xd) ACCUMULATE (degrees)
            p.rot.x += em.Ch[0xb].RangedMin(t);
            p.rot.y += em.Ch[0xc].RangedMin(t);
            p.rot.z += em.Ch[0xd].RangedMin(t);
            // position integration: pos += speedMul · velocity · scaleVel(ch0); + gravity on Y.
            // Uses p.vel (the per-particle TILTED velocity), so a spray fans out instead of rising in lockstep.
            float scaleVel = em.Ch[0].Scale(t);
            p.pos += em.SpeedMul * p.vel * scaleVel;
            float ageLin = life0 - p.life;
            p.pos.y -= ageLin * em.GravAccel + em.GravBase;

            p.tr.localPosition = p.pos;
            p.tr.localScale = Vector3.Scale(p.baseSize, animScale);
            // rings + plain world-matrix quads: oriented by the world matrix = Euler(initRot + accumulated channels).
            // aef_1_07 keeps its template 90° X → lies flat on the ground; the ring's rotY channel spins it.
            if (!p.orient) p.tr.localRotation = Quaternion.Euler(p.rot.x, p.rot.y, p.rot.z);
            // billboards (p.orient): oriented to camera in LateUpdate

            // colour = diffuse (ch2/3/4 = R/G/B) + specular (ch6/7/5 = R/G/B), alpha = ch1. D3D adds specular to
            // diffuse; keeping both preserves the real per-particle hue (blue aef_1_07, orange aef_4_03, etc.).
            if (p.mat != null)   // invisible 0x40000 carriers have no material — they exist only to sub-emit
            {
                float r = em.Ch[2].Ranged(t) + em.Ch[6].Ranged(t);
                float g = em.Ch[3].Ranged(t) + em.Ch[7].Ranged(t);
                float b = em.Ch[4].Ranged(t) + em.Ch[5].Ranged(t);
                float a = em.Ch[1].Ranged(t);
                SetCol(p.mat, r, g, b, a);
                // outer-glow halo: same hue, intensity scaled by _glowMul (alpha drives additive brightness)
                if (p.glowMat != null) SetCol(p.glowMat, r, g, b, a * _glowMul);
            }
        }

        // Per-particle velocity tilt within the posSpread cone (engine: Math_RotateAroundAxis of vel by the
        // randomized posSpread at birth). CRITICAL: the engine randomizes posSpread as `rand%s − rand%s`, a
        // TRIANGULAR distribution biased toward 0 → most tilts are SMALL, so an up-velocity spray stays mostly
        // diagonal-UP with only a few wide ones. A uniform ±s (what we had) pushed most particles to ~horizontal,
        // which was wrong. Frida-verified: 200's tex31 (posSpread 34) born ~23° off vertical; FINISHED sparks
        // (posSpread ~100) born 32–82° up, almost none horizontal/down. posSpread units = degrees.
        static Vector3 TiltVel(Vector3 vel, int spreadX, int spreadZ)
        {
            if ((spreadX == 0 && spreadZ == 0) || vel == Vector3.zero) return vel;
            float rx = UnityEngine.Random.Range(0f, spreadX) - UnityEngine.Random.Range(0f, spreadX);   // triangular ±spreadX
            float rz = UnityEngine.Random.Range(0f, spreadZ) - UnityEngine.Random.Range(0f, spreadZ);   // triangular ±spreadZ
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
