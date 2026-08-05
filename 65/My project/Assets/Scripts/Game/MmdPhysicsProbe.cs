using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Sdo.Game
{
    /// <summary>
    /// IN-GAME cloth-physics probe: measures the MMD→Magica conversion (<see cref="MmdMagicaCloth"/>) inside the REAL
    /// game runtime and writes the magica_&lt;scenario&gt;.json half of the cloth-validation contract (the pybullet
    /// reference sim writes ref_*.json from the same PMX). This exists because Magica Cloth 2 does not simulate under
    /// the Unity Test Framework (teams active + dispatch firing, yet zero substeps — cause unresolved), while it
    /// demonstrably runs in the game — so we measure where it runs.
    ///
    /// Trigger: launch with <c>-mmdprobe</c> (built player; quits when done) or create the flag file
    /// <c>&lt;repo&gt;/tools/mmd_cloth_validate/probe.request</c> before pressing Play (editor; file is consumed).
    /// Scenarios (shared contract with the reference sim): rest 4 s | turn: 1.5 s settle + head +90° yaw over 0.4 s +
    /// 2 s hold | walk: 1.5 s + whole model +Z at 1.2 m/s for 2 s + 2 s hold | spin: 1.5 s + 360° about +Y over 1 s +
    /// 2 s hold. Runs at at a forced 60 fps sim pacing (Time.captureDeltaTime), records 4 representative chains.
    /// </summary>
    public sealed class MmdPhysicsProbe : MonoBehaviour
    {
        // Derived, not hardcoded to a worktree — this was written in feat/mmd-avatar and its "H:/65_remake-mmd/..."
        // constants stopped resolving the moment that branch merged. The model comes from the game's own catalogue
        // (MmdAvatarSwap), the output goes to THIS checkout's tools/mmd_cloth_validate/ (editor) or beside the exe (build).
        private static string RequestFile => Path.Combine(MmdProbePaths.HarnessDir, "probe.request");
        private const float UnitScale = 3.0f;   // same uniform root scale MmdAvatar applies in-game (approx.)
        private const int Fps = 60;
        private const int MaxNonFiniteWarmupFrames = 120;
        private static readonly string[] Scenarios = { "rest", "turn", "walk", "spin", "dance" };
        private static readonly float[] ScenarioDurations = { 4.0f, 3.9f, 5.5f, 4.5f, 6.0f };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            string[] args = Environment.GetCommandLineArgs();
            bool cli = args.Any(a => string.Equals(a, "-mmdprobe", StringComparison.OrdinalIgnoreCase));
            bool req = File.Exists(RequestFile);
            if (!cli && !req) return;
            if (req) { try { File.Delete(RequestFile); } catch { } }
            var go = new GameObject("MmdPhysicsProbe");
            DontDestroyOnLoad(go);
            var probe = go.AddComponent<MmdPhysicsProbe>();
            probe._quitWhenDone = cli;
            probe._pmxArgument = MmdPhysicsProbeSelection.ArgValue(args, "-mmdprobe-pmx");
            probe._outArgument = MmdPhysicsProbeSelection.ArgValue(args, "-mmdprobe-out");
            SdoLog.Note("mmdprobe", $"armed (cli={cli}, request={req}, explicitPmx={!string.IsNullOrWhiteSpace(probe._pmxArgument)})");
        }

        private bool _quitWhenDone;
        private bool _failed;
        private bool _canaryAlive;
        private string _pmxArgument;
        private string _outArgument;
        private string _pmxPath;
        private string _outDir;
        private string _pmxSha256;
        private PmxLoader _pmx;
        private List<MmdPhysicsProbeSelection.ChainSpec> _chainSpecs;

        private void Start() { StartCoroutine(RunAll()); }

        private IEnumerator RunAll()
        {
            // Let the frontend boot fully settle first — every environment where the cloth was built within the first
            // frames of play froze (zero substeps, cause inside MC2 unresolved); the game's own late-built cloth works.
            yield return new WaitForSeconds(3f);

            // The legacy catalogue selects its model during scene startup too. Resolve only after that startup window;
            // an explicit -mmdprobe-pmx still takes precedence and follows the same validation path.
            if (!PrepareModel())
            {
                Finish(1);
                yield break;
            }

            // Built-in A/B canary: a minimal vanilla BoneCloth (unit scale, all defaults). If even this does not move,
            // the environment is frozen and the scenario data would be garbage — abort loudly.
            yield return VanillaCanary();
            if (!_canaryAlive)
            {
                Fail("vanilla Magica Cloth canary is frozen");
                Finish(1);
                yield break;
            }

            for (int s = 0; s < Scenarios.Length; s++)
            {
                yield return RunScenario(Scenarios[s], ScenarioDurations[s]);
                if (_failed)
                {
                    Finish(1);
                    yield break;
                }
            }

            try
            {
                WriteModelJson(Path.Combine(_outDir, "model.json"));
            }
            catch (Exception e)
            {
                Fail("cannot write model.json: " + e.Message);
                Finish(1);
                yield break;
            }
            SdoLog.Note("mmdprobe", "ALL DONE");
            Finish(0);
        }

        private bool PrepareModel()
        {
            try
            {
                string selected = string.IsNullOrWhiteSpace(_pmxArgument) ? MmdAvatarSwap.ModelPath : _pmxArgument;
                if (string.IsNullOrWhiteSpace(selected))
                    return Fail("no MMD model is installed and -mmdprobe-pmx was not supplied");
                _pmxPath = Path.GetFullPath(selected);
                if (!File.Exists(_pmxPath)) return Fail("PMX does not exist: " + _pmxPath);

                byte[] bytes = File.ReadAllBytes(_pmxPath);
                _pmx = PmxLoader.Load(bytes);
                if (_pmx == null || _pmx.Positions == null || _pmx.Positions.Length == 0 ||
                    _pmx.Bones == null || _pmx.Bones.Count == 0)
                    return Fail("PMX parse failed or produced an empty model: " + _pmxPath);
                using (var sha = SHA256.Create())
                    _pmxSha256 = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");

                _chainSpecs = MmdPhysicsProbeSelection.SelectChains(_pmx.Bones, _pmx.RigidBodies, 4);
                if (_chainSpecs.Count == 0) return Fail("PMX has no selectable dynamic physics chains");

                _outDir = Path.GetFullPath(string.IsNullOrWhiteSpace(_outArgument) ? MmdProbePaths.HarnessDir : _outArgument);
                Directory.CreateDirectory(_outDir);
                return true;
            }
            catch (Exception e)
            {
                return Fail("model/output preparation failed: " + e.Message);
            }
        }

        private bool Fail(string message)
        {
            if (!_failed) SdoLog.Note("mmdprobe", "FAIL: " + message);
            _failed = true;
            return false;
        }

        private void Finish(int exitCode)
        {
            if (_quitWhenDone) Application.Quit(exitCode);
        }

        private IEnumerator VanillaCanary()
        {
            var root = new GameObject("ProbeCanary").transform;
            root.position = new Vector3(480f, 0f, 480f);
            var b0 = new GameObject("c0").transform; b0.SetParent(root, false); b0.localPosition = Vector3.up;
            var b1 = new GameObject("c1").transform; b1.SetParent(b0, false); b1.localPosition = Vector3.right * 0.3f;
            var b2 = new GameObject("c2").transform; b2.SetParent(b1, false); b2.localPosition = Vector3.right * 0.3f;
            var go = new GameObject("CanaryCloth");
            go.transform.SetParent(root, false);
            var cloth = go.AddComponent<MagicaCloth2.MagicaCloth>();
            cloth.SerializeData.clothType = MagicaCloth2.ClothProcess.ClothType.BoneCloth;
            cloth.SerializeData.rootBones.Add(b0);
            cloth.BuildAndRun();
            int build = 0;
            while (build < 600 && !cloth.Process.IsRunning()) { build++; yield return null; }
            if (!cloth.Process.IsRunning())
            {
                SdoLog.Note("mmdprobe", $"CANARY build timeout after {build} frames");
                Object.Destroy(root.gameObject);
                yield return null;
                yield break;
            }
            // SAMPLE AT END-OF-FRAME: MC2 restores bones to the ORIGINAL pose in EarlyUpdate and writes sim results in
            // late update — sampling from a plain coroutine (Update phase) reads the restored pose and looks frozen
            // even while the render shows movement. End-of-frame is after the write (and after render).
            var eof = new WaitForEndOfFrame();
            yield return eof;
            Vector3 tip0 = b2.position;
            float t = 0f;
            while (t < 2f) { yield return eof; t += Time.deltaTime; }
            float moved = (b2.position - tip0).magnitude;
            _canaryAlive = moved > 0.05f;
            SdoLog.Note("mmdprobe", $"CANARY build={build} moved={moved:F4} over 2s -> {(_canaryAlive ? "ALIVE" : "FROZEN")}");
            Object.Destroy(root.gameObject);
            yield return null;
        }

        private sealed class Chain
        {
            public string Name;
            public string[] BoneNames;
            public int[] Bones;
            public readonly List<Vector3[]> Frames = new List<Vector3[]>();
        }

        private IEnumerator RunScenario(string scenario, float durationSec)
        {
            var pmx = _pmx;

            float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
            foreach (var p in pmx.Positions) { if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y; }
            float upm = (maxY - minY) * UnitScale / 1.6f;
            if (float.IsNaN(upm) || float.IsInfinity(upm) || upm <= 1e-5f)
            {
                Fail("model has no usable vertex height");
                yield break;
            }

            // bone hierarchy exactly like MmdAvatar.Construct (identity local rotations, uniform root scale)
            int bc = pmx.Bones.Count;
            var rootGo = new GameObject("MmdProbeRig");
            var root = rootGo.transform;
            root.position = new Vector3(500f, 0f, 500f);   // far from the game scene (visual only; physics is per-team)
            root.localScale = Vector3.one * UnitScale;
            var bone = new Transform[bc];
            var parent = new int[bc];
            for (int i = 0; i < bc; i++)
            {
                parent[i] = (pmx.Bones[i].Parent >= 0 && pmx.Bones[i].Parent < bc) ? pmx.Bones[i].Parent : -1;
                bone[i] = new GameObject("b" + i).transform;
            }
            for (int i = 0; i < bc; i++)
            {
                bone[i].SetParent(parent[i] >= 0 ? bone[parent[i]] : root, false);
                Vector3 parPos = parent[i] >= 0 ? pmx.Bones[parent[i]].Position : Vector3.zero;
                bone[i].localPosition = pmx.Bones[i].Position - parPos;
                bone[i].localRotation = Quaternion.identity;
            }
            var restPos = new Vector3[bc];
            var restRot = new Quaternion[bc];
            for (int i = 0; i < bc; i++) { restPos[i] = bone[i].localPosition; restRot[i] = bone[i].localRotation; }

            int head = MmdPhysicsProbeSelection.FindMotionBone(pmx.Bones);
            MmdMagicaCloth magica;
            try { magica = MmdMagicaCloth.Setup(rootGo, bone, parent, pmx, UnitScale); }
            catch (Exception e)
            {
                Fail("cloth setup threw: " + e.Message);
                Destroy(rootGo);
                yield break;
            }
            if (magica == null || !magica.Any || head < 0)
            {
                Fail(head < 0 ? "model has no head/motion bone" : "cloth setup produced no cloth");
                Destroy(rootGo);
                yield break;
            }
            var limbs = Limbs.Find(pmx, bone);
            var clip = ClipMeter.Build(pmx, bone, restPos);

            var cloths = rootGo.GetComponentsInChildren<MagicaCloth2.MagicaCloth>(true);
            int buildFrames = 0;
            while (buildFrames < 900)
            {
                bool all = cloths.Length > 0;
                foreach (var c in cloths) if (!c.Process.IsRunning()) { all = false; break; }
                if (all) break;
                buildFrames++;
                yield return null;
            }
            if (cloths.Length == 0 || cloths.Any(c => !c.Process.IsRunning()))
            {
                Fail($"cloth build timeout after {buildFrames} frames ({cloths.Length} cloths)");
                Destroy(rootGo);
                yield break;
            }
            for (int i = 0; i < bc; i++) { bone[i].localPosition = restPos[i]; bone[i].localRotation = restRot[i]; }
            foreach (var c in cloths) c.ResetCloth();

            // REAL-time pacing (captureDeltaTime froze MC2 everywhere): drive by accumulated Time.deltaTime and record
            // the per-frame dt series so the metrics build an exact time axis.
            var chains = CreateChains();
            if (chains.Count == 0)
            {
                Fail("representative chain selection was empty");
                Destroy(rootGo);
                yield break;
            }
            var anchor = new List<float[]>(1024);
            var dts = new List<float>(1024);
            int[] physicsSampleBones = pmx.PhysicsBones.Where(i => i >= 0 && i < bone.Length && bone[i] != null)
                                                       .OrderBy(i => i).ToArray();
            var allPhysicsPositions = new Vector3[physicsSampleBones.Length];
            float walkSpeedWorld = 1.2f * upm;
            Vector3 basePosition = root.position;
            // SAMPLE AT END-OF-FRAME: MC2 restores original bone poses in EarlyUpdate and writes sim results in late
            // update — a plain `yield return null` (Update phase) reads the restored pose (looks frozen even though the
            // render moves). End-of-frame is after MC2's write.
            var eof = new WaitForEndOfFrame();
            float t = 0f, estDt = 1f / Fps;
            int discardedWarmupFrames = 0;
            while (t < durationSec)
            {
                Drive(scenario, t + estDt, bone[head], root, basePosition, walkSpeedWorld);   // pose for the frame about to sim
                if (scenario == "dance") limbs.Pose(t + estDt, root, basePosition, upm);
                yield return eof;
                float dt = Mathf.Clamp(Time.deltaTime, 1e-4f, 0.1f);
                estDt = dt;
                Vector3 hp = bone[head].position; Quaternion hq = bone[head].rotation;
                var sampledChains = new List<Vector3[]>(chains.Count);
                foreach (var ch in chains)
                {
                    var arr = new Vector3[ch.Bones.Length];
                    for (int b = 0; b < ch.Bones.Length; b++) arr[b] = bone[ch.Bones[b]].position;
                    sampledChains.Add(arr);
                }
                for (int b = 0; b < physicsSampleBones.Length; b++)
                    allPhysicsPositions[b] = bone[physicsSampleBones[b]].position;
                if (!MmdPhysicsProbeSelection.IsFiniteSample(hp, hq, sampledChains, allPhysicsPositions))
                {
                    if (anchor.Count > 0)
                    {
                        Fail($"{scenario}: non-finite cloth sample after recording started at frame {anchor.Count}");
                        Destroy(rootGo);
                        yield break;
                    }
                    discardedWarmupFrames++;
                    if (discardedWarmupFrames == 1)
                        SdoLog.Note("mmdprobe", $"{scenario}: discarded a non-finite post-reset warmup frame");
                    if (discardedWarmupFrames > MaxNonFiniteWarmupFrames)
                    {
                        Fail($"{scenario}: cloth stayed non-finite for {discardedWarmupFrames} warmup frames");
                        Destroy(rootGo);
                        yield break;
                    }
                    continue;
                }

                t += dt; dts.Add(dt);
                clip.Sample(bone);
                anchor.Add(new[] { hp.x, hp.y, hp.z, hq.x, hq.y, hq.z, hq.w });
                for (int c = 0; c < chains.Count; c++) chains[c].Frames.Add(sampledChains[c]);
            }

            string outPath = Path.Combine(_outDir, "magica_" + scenario + ".json");
            try { WriteJson(outPath, scenario, upm, buildFrames, discardedWarmupFrames, anchor, chains, dts, clip, upm); }
            catch (Exception e)
            {
                Fail($"cannot write {Path.GetFileName(outPath)}: {e.Message}");
                Destroy(rootGo);
                yield break;
            }
            SdoLog.Note("mmdprobe", $"{scenario}: {anchor.Count}f ({t:F2}s) build={buildFrames} warmupDropped={discardedWarmupFrames} " +
                                    $"upm={upm:F2} cloths={cloths.Length} " +
                                    $"clip max={clip.MaxDepth / upm * 100f:F1}cm in {clip.HitFrames}/{clip.Frames}f -> {outPath}");

            Destroy(rootGo);
            yield return null;
        }

        private static void Drive(string scenario, float t, Transform head, Transform root, Vector3 basePos, float walkSpeedWorld)
        {
            switch (scenario)
            {
                case "turn":
                    head.localRotation = Quaternion.Euler(0f, 90f * Mathf.Clamp01((t - 1.5f) / 0.4f), 0f);
                    break;
                case "walk":
                    root.position = basePos + new Vector3(0f, 0f, walkSpeedWorld * Mathf.Clamp(t - 1.5f, 0f, 2f));
                    break;
                case "spin":
                    root.rotation = Quaternion.Euler(0f, 360f * Mathf.Clamp01(t - 1.5f), 0f);
                    break;
            }
        }

        /// <summary>
        /// The "dance" scenario: legs, spine and head swinging at the rate a real chart does, plus a bounce on the
        /// root. The other four scenarios move the whole model gently (a 0.4 s head turn, a 1.2 m/s walk) and NEVER
        /// move a limb — so they cannot produce the one failure the cloth is actually judged on in game, a leg
        /// sweeping through the skirt. Not compared against the reference sim (that would need per-bone FK on the
        /// pybullet side); its recording exists for the CLIPPING numbers and for eyeballing the swing under load.
        /// </summary>
        private sealed class Limbs
        {
            private Transform _upper, _upper2, _head;
            private Transform[] _leg = new Transform[2], _knee = new Transform[2];

            public static Limbs Find(PmxLoader pmx, Transform[] bone)
            {
                Transform B(string n) { int i = FindBone(pmx, n); return i >= 0 ? bone[i] : null; }
                var l = new Limbs { _upper = B("上半身"), _upper2 = B("上半身2"), _head = B("頭") };
                l._leg[0] = B("左足"); l._leg[1] = B("右足");
                l._knee[0] = B("左ひざ"); l._knee[1] = B("右ひざ");
                return l;
            }

            public void Pose(float t, Transform root, Vector3 basePos, float upm)
            {
                float step = t * 2f * Mathf.PI * 1.8f;                       // 1.8 steps/s ≈ a 110 BPM chart
                for (int s = 0; s < 2; s++)
                {
                    float ph = step + (s == 0 ? 0f : Mathf.PI);
                    if (_leg[s] != null) _leg[s].localRotation = Quaternion.Euler(55f * Mathf.Sin(ph), 12f * Mathf.Sin(ph * 0.5f), 0f);
                    if (_knee[s] != null) _knee[s].localRotation = Quaternion.Euler(-45f * (1f - Mathf.Cos(ph)) * 0.5f - 5f, 0f, 0f);
                }
                float sway = t * 2f * Mathf.PI * 1.2f;
                if (_upper != null) _upper.localRotation = Quaternion.Euler(8f * Mathf.Sin(sway * 0.5f), 30f * Mathf.Sin(sway), 10f * Mathf.Sin(sway * 0.5f));
                if (_upper2 != null) _upper2.localRotation = Quaternion.Euler(0f, 15f * Mathf.Sin(sway + 0.6f), 0f);
                if (_head != null) _head.localRotation = Quaternion.Euler(0f, 45f * Mathf.Sin(t * 2f * Mathf.PI * 1.5f), 0f);
                // bounce + a small travel: the cloth must survive world movement on top of the limb motion
                root.position = basePos + new Vector3(0.35f * upm * Mathf.Sin(t * 1.4f), 0.07f * upm * Mathf.Abs(Mathf.Sin(step)), 0f);
            }
        }

        /// <summary>
        /// How deep the cloth bones end up INSIDE the body, measured against the model's own kinematic rigid bodies
        /// (rebuilt here from the .pmx exactly like <see cref="MmdMagicaCloth"/> builds its colliders, so the meter
        /// does not depend on Magica's internals). There is no reference value to compare against — Bullet simply does
        /// not let a body penetrate — so this is an ABSOLUTE quality number: at rest it should be ~0, and a dance that
        /// pushes a leg through the skirt shows up as centimetres of depth in a large fraction of frames.
        /// </summary>
        private sealed class ClipMeter
        {
            private struct Shape { public Transform Bone; public Vector3 Local0, Local1; public float R; public byte Group; public ushort Mask; }
            private struct Particle { public int Bone; public byte Group; public ushort Mask; }
            private readonly List<Shape> _shapes = new List<Shape>();
            private readonly List<Particle> _cloth = new List<Particle>();

            public float MaxDepth, DepthSum;
            public int Frames, HitFrames;
            public float MeanDepth => Frames > 0 ? DepthSum / Frames : 0f;

            public static ClipMeter Build(PmxLoader pmx, Transform[] bone, Vector3[] restPos)
            {
                var m = new ClipMeter();
                if (pmx.RigidBodies == null) return m;
                foreach (var rb in pmx.RigidBodies)
                {
                    if (rb.Mode != 0 || rb.Bone < 0 || rb.Bone >= bone.Length || bone[rb.Bone] == null) continue;
                    if (pmx.PhysicsBones.Contains(rb.Bone)) continue;
                    string bn = pmx.Bones[rb.Bone].NameJp ?? "";
                    if (bn.Contains("指")) continue;
                    Vector3 off = rb.Position - pmx.Bones[rb.Bone].Position;
                    var rot = Quaternion.Euler(rb.Rotation * Mathf.Rad2Deg);
                    float r = Mathf.Max(rb.Size.x, 1e-3f);
                    Vector3 half = rb.Shape == 2 ? rot * new Vector3(0f, rb.Size.y * 0.5f, 0f) : Vector3.zero;
                    if (rb.Shape == 1) r = Mathf.Max(rb.Size.x, Mathf.Max(rb.Size.y, rb.Size.z));
                    m._shapes.Add(new Shape { Bone = bone[rb.Bone], Local0 = off - half, Local1 = off + half, R = r,
                                              Group = rb.Group, Mask = rb.Mask });
                }
                // Only pairs the AUTHOR let collide count as clipping. Without this filter a skirt panel reads as 9 cm
                // "inside the body" while standing still, because its root legitimately sits inside the big hip capsule
                // — which is exactly why the author cleared that group bit.
                foreach (var rb in pmx.RigidBodies)
                {
                    if (rb.Mode == 0 || rb.Bone < 0 || rb.Bone >= bone.Length || bone[rb.Bone] == null) continue;
                    m._cloth.Add(new Particle { Bone = rb.Bone, Group = rb.Group, Mask = rb.Mask });
                }
                return m;
            }

            /// <summary>One frame: the deepest any cloth bone sits inside any body shape (world units).</summary>
            public void Sample(Transform[] bone)
            {
                if (_shapes.Count == 0 || _cloth.Count == 0) return;
                float worst = 0f;
                for (int s = 0; s < _shapes.Count; s++)
                {
                    var sh = _shapes[s];
                    if (sh.Bone == null) continue;
                    Vector3 a = sh.Bone.TransformPoint(sh.Local0), b = sh.Bone.TransformPoint(sh.Local1);
                    float scale = sh.Bone.lossyScale.x, r = sh.R * scale;
                    Vector3 ab = b - a;
                    float abLen2 = Mathf.Max(Vector3.Dot(ab, ab), 1e-9f);
                    for (int c = 0; c < _cloth.Count; c++)
                    {
                        var part = _cloth[c];
                        if (((part.Mask >> sh.Group) & 1) == 0 || ((sh.Mask >> part.Group) & 1) == 0) continue;
                        var t = bone[part.Bone];
                        if (t == null) continue;
                        Vector3 p = t.position;
                        float u = Mathf.Clamp01(Vector3.Dot(p - a, ab) / abLen2);
                        float d = Vector3.Distance(p, a + ab * u);
                        if (r - d > worst) worst = r - d;
                    }
                }
                Frames++;
                if (worst > 1e-4f) { HitFrames++; DepthSum += worst; }
                if (worst > MaxDepth) MaxDepth = worst;
            }
        }

        private List<Chain> CreateChains()
        {
            return _chainSpecs.Select(spec => new Chain
            {
                Name = spec.Id,
                Bones = spec.Bones,
                BoneNames = spec.BoneNames,
            }).ToList();
        }

        private static int FindBone(PmxLoader pmx, string nameJp)
        {
            for (int i = 0; i < pmx.Bones.Count; i++) if (pmx.Bones[i].NameJp == nameJp) return i;
            return -1;
        }

        private void WriteModelJson(string path)
        {
            int kinematicBodies = _pmx.RigidBodies.Count(body => body.Mode == 0);
            int dynamicBodies = _pmx.RigidBodies.Count - kinematicBodies;
            string displayName = !string.IsNullOrWhiteSpace(_pmx.NameJp) ? _pmx.NameJp :
                                 (!string.IsNullOrWhiteSpace(_pmx.NameEn) ? _pmx.NameEn : Path.GetFileNameWithoutExtension(_pmxPath));
            var sb = new StringBuilder(1024);
            sb.Append("{\"schema\":1,\"pmxPath\":").Append(JsonString(_pmxPath));
            sb.Append(",\"sha256\":").Append(JsonString(_pmxSha256));
            sb.Append(",\"pmxVersion\":").Append(F(_pmx.Version));
            sb.Append(",\"name\":").Append(JsonString(displayName));
            sb.Append(",\"nameJp\":").Append(JsonString(_pmx.NameJp));
            sb.Append(",\"nameEn\":").Append(JsonString(_pmx.NameEn));
            sb.Append(",\"counts\":{");
            sb.Append("\"vertices\":").Append(_pmx.VertexCount);
            sb.Append(",\"triangles\":").Append((_pmx.Indices?.Length ?? 0) / 3);
            sb.Append(",\"materials\":").Append(_pmx.Materials?.Count ?? 0);
            sb.Append(",\"bones\":").Append(_pmx.Bones.Count);
            sb.Append(",\"rigidBodies\":").Append(_pmx.RigidBodies.Count);
            sb.Append(",\"kinematicRigidBodies\":").Append(kinematicBodies);
            sb.Append(",\"dynamicRigidBodies\":").Append(dynamicBodies);
            sb.Append(",\"physicsBones\":").Append(_pmx.PhysicsBones?.Count ?? 0);
            sb.Append(",\"selectedChains\":").Append(_chainSpecs.Count);
            sb.Append(",\"scenarios\":").Append(Scenarios.Length).Append('}');
            sb.Append(",\"chainIds\":[");
            for (int i = 0; i < _chainSpecs.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonString(_chainSpecs[i].Id));
            }
            sb.Append("]}");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static string JsonString(string value)
        {
            if (value == null) return "null";
            var sb = new StringBuilder(value.Length + 2).Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static string F(float v) => v.ToString("G9", CultureInfo.InvariantCulture);

        private static void WriteJson(string path, string scenario, float upm, int buildFrames, int discardedWarmupFrames,
                                      List<float[]> anchor, List<Chain> chains, List<float> dts, ClipMeter clip, float upmForClip)
        {
            var sb = new StringBuilder(4 << 20);
            sb.Append("{\"scenario\":").Append(JsonString(scenario)).Append(",\"fps\":60,\"unitsPerMeter\":").Append(F(upm));
            sb.Append(",\"unitScale\":").Append(F(UnitScale)).Append(",\"buildFrames\":").Append(buildFrames);
            sb.Append(",\"discardedWarmupFrames\":").Append(discardedWarmupFrames);
            // Cloth-inside-body depth, in METRES (like every other length in the contract). magica-only: Bullet does
            // not let bodies interpenetrate, so there is nothing to compare it to — it is judged against 0.
            sb.Append(",\"clip\":{\"maxDepthM\":").Append(F(clip.MaxDepth / upmForClip))
              .Append(",\"meanDepthM\":").Append(F(clip.MeanDepth / upmForClip))
              .Append(",\"hitFrames\":").Append(clip.HitFrames).Append(",\"frames\":").Append(clip.Frames).Append('}');
            sb.Append(",\"dt\":[");
            for (int f = 0; f < dts.Count; f++) { if (f > 0) sb.Append(','); sb.Append(F(dts[f])); }
            sb.Append("],\"anchor\":[");
            for (int f = 0; f < anchor.Count; f++)
            {
                if (f > 0) sb.Append(',');
                sb.Append('[');
                var a = anchor[f];
                for (int k = 0; k < 7; k++) { if (k > 0) sb.Append(','); sb.Append(F(a[k])); }
                sb.Append(']');
            }
            sb.Append("],\"chains\":{");
            for (int c = 0; c < chains.Count; c++)
            {
                var ch = chains[c];
                if (c > 0) sb.Append(',');
                sb.Append(JsonString(ch.Name)).Append(":{\"bones\":[");
                for (int b = 0; b < ch.BoneNames.Length; b++)
                { if (b > 0) sb.Append(','); sb.Append(JsonString(ch.BoneNames[b])); }
                sb.Append("],\"frames\":[");
                for (int f = 0; f < ch.Frames.Count; f++)
                {
                    if (f > 0) sb.Append(',');
                    sb.Append('[');
                    var fr = ch.Frames[f];
                    for (int b = 0; b < fr.Length; b++)
                    {
                        if (b > 0) sb.Append(',');
                        sb.Append('[').Append(F(fr[b].x)).Append(',').Append(F(fr[b].y)).Append(',').Append(F(fr[b].z)).Append(']');
                    }
                    sb.Append(']');
                }
                sb.Append("]}");
            }
            sb.Append("}}");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
