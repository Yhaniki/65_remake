using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

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
    /// <c>H:/65_remake-mmd/tools/mmd_cloth_validate/probe.request</c> before pressing Play (editor; file is consumed).
    /// Scenarios (shared contract with the reference sim): rest 4 s | turn: 1.5 s settle + head +90° yaw over 0.4 s +
    /// 2 s hold | walk: 1.5 s + whole model +Z at 1.2 m/s for 2 s + 2 s hold | spin: 1.5 s + 360° about +Y over 1 s +
    /// 2 s hold. Runs at at a forced 60 fps sim pacing (Time.captureDeltaTime), records 4 representative chains.
    /// </summary>
    public sealed class MmdPhysicsProbe : MonoBehaviour
    {
        private const string PmxDir = "H:/65_remake/assets/IkaHatunemiku2025";
        private const string OutDir = "H:/65_remake-mmd/tools/mmd_cloth_validate";
        private const string RequestFile = OutDir + "/probe.request";
        private const float UnitScale = 3.0f;   // same uniform root scale MmdAvatar applies in-game (approx.)
        private const int Fps = 60;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            bool cli = System.Environment.GetCommandLineArgs().Any(a => a == "-mmdprobe");
            bool req = File.Exists(RequestFile);
            if (!cli && !req) return;
            if (req) { try { File.Delete(RequestFile); } catch { } }
            var go = new GameObject("MmdPhysicsProbe");
            DontDestroyOnLoad(go);
            go.AddComponent<MmdPhysicsProbe>()._quitWhenDone = cli;
            SdoLog.Note("mmdprobe", $"armed (cli={cli}, request={req})");
        }

        private bool _quitWhenDone;

        private void Start() { StartCoroutine(RunAll()); }

        private IEnumerator RunAll()
        {
            // Let the frontend boot fully settle first — every environment where the cloth was built within the first
            // frames of play froze (zero substeps, cause inside MC2 unresolved); the game's own late-built cloth works.
            yield return new WaitForSeconds(3f);

            // Built-in A/B canary: a minimal vanilla BoneCloth (unit scale, all defaults). If even this does not move,
            // the environment is frozen and the scenario data would be garbage — abort loudly.
            yield return VanillaCanary();

            string[] scenarios = { "rest", "turn", "walk", "spin" };
            float[] durations = { 4.0f, 3.9f, 5.5f, 4.5f };
            for (int s = 0; s < scenarios.Length; s++)
                yield return RunScenario(scenarios[s], durations[s]);
            SdoLog.Note("mmdprobe", "ALL DONE");
            if (_quitWhenDone) Application.Quit(0);
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
            // SAMPLE AT END-OF-FRAME: MC2 restores bones to the ORIGINAL pose in EarlyUpdate and writes sim results in
            // late update — sampling from a plain coroutine (Update phase) reads the restored pose and looks frozen
            // even while the render shows movement. End-of-frame is after the write (and after render).
            var eof = new WaitForEndOfFrame();
            yield return eof;
            Vector3 tip0 = b2.position;
            float t = 0f;
            while (t < 2f) { yield return eof; t += Time.deltaTime; }
            float moved = (b2.position - tip0).magnitude;
            SdoLog.Note("mmdprobe", $"CANARY build={build} moved={moved:F4} over 2s -> {(moved > 0.05f ? "ALIVE" : "FROZEN")}");
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
            string pmxPath = FindPmx();
            if (pmxPath == null) { SdoLog.Note("mmdprobe", "FAIL: no pmx under " + PmxDir); yield break; }
            var pmx = PmxLoader.Load(File.ReadAllBytes(pmxPath));
            if (pmx == null) { SdoLog.Note("mmdprobe", "FAIL: pmx parse"); yield break; }

            float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
            foreach (var p in pmx.Positions) { if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y; }
            float upm = (maxY - minY) * UnitScale / 1.6f;

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

            int head = FindBone(pmx, "頭");
            var magica = MmdMagicaCloth.Setup(rootGo, bone, parent, pmx, UnitScale);
            if (magica == null || head < 0) { SdoLog.Note("mmdprobe", "FAIL: setup"); Destroy(rootGo); yield break; }

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
            for (int i = 0; i < bc; i++) { bone[i].localPosition = restPos[i]; bone[i].localRotation = restRot[i]; }
            foreach (var c in cloths) c.ResetCloth();

            // REAL-time pacing (captureDeltaTime froze MC2 everywhere): drive by accumulated Time.deltaTime and record
            // the per-frame dt series so the metrics build an exact time axis.
            var chains = ExtractChains(pmx);
            var anchor = new List<float[]>(1024);
            var dts = new List<float>(1024);
            float walkSpeedWorld = 1.2f * upm;
            Vector3 basePosition = root.position;
            // SAMPLE AT END-OF-FRAME: MC2 restores original bone poses in EarlyUpdate and writes sim results in late
            // update — a plain `yield return null` (Update phase) reads the restored pose (looks frozen even though the
            // render moves). End-of-frame is after MC2's write.
            var eof = new WaitForEndOfFrame();
            float t = 0f, estDt = 1f / Fps;
            while (t < durationSec)
            {
                Drive(scenario, t + estDt, bone[head], root, basePosition, walkSpeedWorld);   // pose for the frame about to sim
                yield return eof;
                float dt = Mathf.Clamp(Time.deltaTime, 1e-4f, 0.1f);
                estDt = dt; t += dt; dts.Add(dt);
                Vector3 hp = bone[head].position; Quaternion hq = bone[head].rotation;
                anchor.Add(new[] { hp.x, hp.y, hp.z, hq.x, hq.y, hq.z, hq.w });
                foreach (var ch in chains)
                {
                    var arr = new Vector3[ch.Bones.Length];
                    for (int b = 0; b < ch.Bones.Length; b++) arr[b] = bone[ch.Bones[b]].position;
                    ch.Frames.Add(arr);
                }
            }

            Directory.CreateDirectory(OutDir);
            string outPath = Path.Combine(OutDir, "magica_" + scenario + ".json");
            WriteJson(outPath, scenario, upm, buildFrames, anchor, chains, dts);
            SdoLog.Note("mmdprobe", $"{scenario}: {anchor.Count}f ({t:F2}s) build={buildFrames} upm={upm:F2} cloths={cloths.Length} -> {outPath}");

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

        // Chains = maximal runs of parent-linked physics bones, root → tip (same picks as the reference sim contract).
        private static List<Chain> ExtractChains(PmxLoader pmx)
        {
            int bc = pmx.Bones.Count;
            var dyn = new Dictionary<int, string>();
            foreach (var rb in pmx.RigidBodies)
                if (rb.Mode != 0 && rb.Bone >= 0 && rb.Bone < bc && !dyn.ContainsKey(rb.Bone)) dyn[rb.Bone] = rb.Name ?? "";
            var physSorted = dyn.Keys.OrderBy(i => i).ToList();
            var phys = new HashSet<int>(physSorted);

            var chainsAll = new List<List<int>>();
            foreach (int r in physSorted)
            {
                int p = pmx.Bones[r].Parent;
                if (p >= 0 && phys.Contains(p)) continue;
                var chain = new List<int> { r };
                for (int cur = r; ; )
                {
                    int child = -1;
                    foreach (int i in physSorted) if (pmx.Bones[i].Parent == cur) { child = i; break; }
                    if (child < 0) break;
                    chain.Add(child);
                    cur = child;
                }
                chainsAll.Add(chain);
            }

            List<int> Pick(System.Func<string, bool> rootPred) => chainsAll.FirstOrDefault(ch => rootPred(dyn[ch[0]]));
            var twin = Pick(n => n.StartsWith("RightTwicHairA"));
            var bang = Pick(n => n.StartsWith("BangHairA")) ?? Pick(n => n.Contains("Bang"));
            var tie = Pick(n => n.StartsWith("Tie"));
            var dress = chainsAll.Where(ch => dyn[ch[0]].StartsWith("Dress")).ToList();
            var skirt = dress.FirstOrDefault(ch => dyn[ch[0]].EndsWith("_5")) ?? (dress.Count > 0 ? dress[dress.Count / 2] : null);

            var outl = new List<Chain>();
            void Add(string name, List<int> ch)
            {
                if (ch == null) return;
                outl.Add(new Chain { Name = name, Bones = ch.ToArray(), BoneNames = ch.Select(i => dyn[i]).ToArray() });
            }
            Add("RightTwicHairA", twin);
            Add("BangHairA", bang);
            Add("Tie", tie);
            Add("Dress_5", skirt);
            return outl;
        }

        private static string FindPmx()
        {
            if (!Directory.Exists(PmxDir)) return null;
            string best = null;
            foreach (var f in Directory.GetFiles(PmxDir))
            {
                if (Path.GetExtension(f).ToLowerInvariant() != ".pmx") continue;
                if (f.ToUpperInvariant().Contains("-JP")) return f;
                if (best == null) best = f;
            }
            return best;
        }

        private static int FindBone(PmxLoader pmx, string nameJp)
        {
            for (int i = 0; i < pmx.Bones.Count; i++) if (pmx.Bones[i].NameJp == nameJp) return i;
            return -1;
        }

        private static string F(float v) => v.ToString("G9", CultureInfo.InvariantCulture);

        private static void WriteJson(string path, string scenario, float upm, int buildFrames,
                                      List<float[]> anchor, List<Chain> chains, List<float> dts)
        {
            var sb = new StringBuilder(4 << 20);
            sb.Append("{\"scenario\":\"").Append(scenario).Append("\",\"fps\":60,\"unitsPerMeter\":").Append(F(upm));
            sb.Append(",\"unitScale\":").Append(F(UnitScale)).Append(",\"buildFrames\":").Append(buildFrames);
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
                sb.Append('"').Append(ch.Name).Append("\":{\"bones\":[");
                for (int b = 0; b < ch.BoneNames.Length; b++)
                { if (b > 0) sb.Append(','); sb.Append('"').Append(ch.BoneNames[b]).Append('"'); }
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
