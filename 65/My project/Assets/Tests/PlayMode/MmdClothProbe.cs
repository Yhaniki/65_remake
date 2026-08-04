using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using MagicaCloth2;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// Magica-Cloth-side probe of the MMD physics conversion (<see cref="MmdMagicaCloth"/>), producing the
    /// magica_&lt;scenario&gt;.json half of the cloth-validation contract (the reference sim writes ref_*.json from the
    /// same PMX rigid-body data). Four canonical scenarios, each rebuilt from the authored rest pose and driven on the
    /// kinematic anchor at a fixed 60 fps (<c>Time.captureDeltaTime</c>):
    ///   rest = 4 s settle | turn = 1.5 s settle + head +90° yaw over 0.4 s + 2 s hold |
    ///   walk = 1.5 s + whole model +Z at 1.2 m/s for 2 s + 2 s hold | spin = 1.5 s + 360° about +Y over 1 s + 2 s hold.
    /// Records world positions of 4 representative chains (twintail / bang / tie / skirt panel, identified by dynamic
    /// rigid-body name prefix) + the head anchor pose per frame, into
    /// H:/65_remake-mmd/tools/mmd_cloth_validate/magica_&lt;scenario&gt;.json.
    /// Run: -runTests -testPlatform PlayMode -testFilter Sdo.Tests.MmdClothProbe
    /// </summary>
    public class MmdClothProbe
    {
        private const string PmxDir = "H:/65_remake/assets/IkaHatunemiku2025";
        private const string OutDir = "H:/65_remake-mmd/tools/mmd_cloth_validate";
        private const float UnitScale = 3.0f;    // same uniform root scale MmdAvatar applies in-game (approx.)
        private const int Fps = 60;

        [UnityTest] public IEnumerator Probe_Rest() { yield return Run("rest", 4.0f); }
        [UnityTest] public IEnumerator Probe_Turn() { yield return Run("turn", 3.9f); }   // 1.5 + 0.4 + 2.0
        [UnityTest] public IEnumerator Probe_Walk() { yield return Run("walk", 5.5f); }   // 1.5 + 2.0 + 2.0
        [UnityTest] public IEnumerator Probe_Spin() { yield return Run("spin", 4.5f); }   // 1.5 + 1.0 + 2.0

        [TearDown]
        public void Cleanup()
        {
            Time.captureDeltaTime = 0f;
            Time.timeScale = 1f;
            var left = GameObject.Find("MmdClothProbeRoot");
            if (left != null) Object.Destroy(left);
        }

        private sealed class Chain
        {
            public string Name;                 // canonical contract key (rigid-body name prefix)
            public string[] BoneNames;          // dynamic rigid-body names, root → tip
            public int[] Bones;                 // pmx bone indices, root → tip
            public readonly List<Vector3[]> Frames = new List<Vector3[]>();
        }

        private IEnumerator Run(string scenario, float durationSec)
        {
            LogAssert.ignoreFailingMessages = true;   // front-end auto-boot noise must not fail the probe

            // ---- parse the PMX (prefer the -JP file, matching the game's ResolveMikuPmx) ----
            string pmxPath = FindPmx();
            Assert.IsNotNull(pmxPath, "no .pmx under " + PmxDir);
            var pmx = PmxLoader.Load(File.ReadAllBytes(pmxPath));
            Assert.IsNotNull(pmx, "PmxLoader.Load failed for " + pmxPath);

            float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
            foreach (var p in pmx.Positions) { if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y; }
            float modelHeightUnits = maxY - minY;              // raw PMX units (mesh height)
            float upm = modelHeightUnits * UnitScale / 1.6f;   // WORLD units per meter (model ≙ a 1.6 m girl)

            // ---- bone hierarchy EXACTLY like MmdAvatar.Construct (identity local rotations, uniform root scale) ----
            int bc = pmx.Bones.Count;
            var rootGo = new GameObject("MmdClothProbeRoot");
            var root = rootGo.transform;
            root.localScale = Vector3.one * UnitScale;
            var bone = new Transform[bc];
            var parent = new int[bc];
            for (int i = 0; i < bc; i++)
            {
                var b = pmx.Bones[i];
                parent[i] = (b.Parent >= 0 && b.Parent < bc) ? b.Parent : -1;
                bone[i] = new GameObject("b" + i).transform;
            }
            for (int i = 0; i < bc; i++)
            {
                bone[i].SetParent(parent[i] >= 0 ? bone[parent[i]] : root, false);
                Vector3 parPos = parent[i] >= 0 ? pmx.Bones[parent[i]].Position : Vector3.zero;
                bone[i].localPosition = pmx.Bones[i].Position - parPos;
                bone[i].localRotation = Quaternion.identity;
                bone[i].localScale = Vector3.one;
            }
            // authored rest-pose snapshot — restored after the async cloth build so recording starts exactly at rest
            var restPos = new Vector3[bc];
            var restRot = new Quaternion[bc];
            for (int i = 0; i < bc; i++) { restPos[i] = bone[i].localPosition; restRot[i] = bone[i].localRotation; }

            int head = FindBone(pmx, "頭");
            Assert.GreaterOrEqual(head, 0, "頭 (head) bone not found");

            // ---- the conversion under test ----
            // UTF rebuilds the PlayerLoop for [UnityTest] pumping, which stomps MC2's injected update phases (cloth
            // builds but never simulates headless). Re-inject (public + idempotent) BEFORE building.
            MagicaManager.InitCustomGameLoop();
            var magica = MmdMagicaCloth.Setup(rootGo, bone, parent, pmx, UnitScale);
            Assert.IsNotNull(magica, "MmdMagicaCloth.Setup returned null");

            Time.captureDeltaTime = 1f / Fps;   // fixed 60 fps sim pacing regardless of wall clock

            // ---- wait for the async BuildAndRun to finish (chains sag meanwhile; hard reset below) ----
            var cloths = rootGo.GetComponentsInChildren<MagicaCloth>(true);
            Assert.Greater(cloths.Length, 0, "no MagicaCloth components were built");
            int buildFrames = 0;
            while (buildFrames < 900)
            {
                bool all = true;
                foreach (var c in cloths) if (!c.Process.IsRunning()) { all = false; break; }
                if (all) break;
                buildFrames++;
                yield return null;
            }
            Assert.Less(buildFrames, 900, "MagicaCloth build did not complete (Process.IsRunning never true)");

            // back to the authored rest pose + full sim reset ⇒ t=0 of the recording is the authored rest pose
            for (int i = 0; i < bc; i++) { bone[i].localPosition = restPos[i]; bone[i].localRotation = restRot[i]; }
            foreach (var c in cloths) c.ResetCloth();

            // ---- representative chains (identified by dynamic rigid-body name prefixes; shared contract) ----
            var chains = ExtractChains(pmx);
            foreach (var need in new[] { "RightTwicHairA", "BangHairA", "Tie", "Dress_5" })
                Assert.IsTrue(chains.Any(c => c.Name == need), "representative chain missing: " + need);

            // ---- drive the anchor + record at 60 fps ----
            int frames = Mathf.RoundToInt(durationSec * Fps);
            var anchor = new List<float[]>(frames);
            float walkSpeedWorld = 1.2f * upm;   // 1.2 m/s → world units/s
            for (int f = 0; f < frames; f++)
            {
                float t = (f + 1) / (float)Fps;              // time at the END of this frame's sim step
                Drive(scenario, t, bone[head], root, walkSpeedWorld);
                yield return null;                           // MC2 simulates this frame with the new anchor pose
                Vector3 hp = bone[head].position;            // anchor + chains sampled at the same post-step instant
                Quaternion hq = bone[head].rotation;
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
            WriteJson(outPath, scenario, upm, buildFrames, anchor, chains);
            Debug.Log($"[MmdClothProbe] {scenario}: {frames} frames @60fps, buildFrames={buildFrames}, " +
                      $"upm={upm:F3} (modelHeight={modelHeightUnits:F3}u × {UnitScale}), cloths={cloths.Length} -> {outPath}");

            Object.Destroy(rootGo);
            Time.captureDeltaTime = 0f;
            yield return null;
        }

        // Scenario timeline (t = seconds since recording start). Rest pose until 1.5 s in every non-rest scenario.
        private static void Drive(string scenario, float t, Transform head, Transform root, float walkSpeedWorld)
        {
            switch (scenario)
            {
                case "turn":   // head yaw 0→+90° over 0.4 s, then hold
                    head.localRotation = Quaternion.Euler(0f, 90f * Mathf.Clamp01((t - 1.5f) / 0.4f), 0f);
                    break;
                case "walk":   // whole model forward +Z (MMD space = probe world +Z) at 1.2 m/s for 2 s, then stop
                    root.position = new Vector3(0f, 0f, walkSpeedWorld * Mathf.Clamp(t - 1.5f, 0f, 2f));
                    break;
                case "spin":   // whole model 360° about +Y over 1.0 s, then hold
                    root.rotation = Quaternion.Euler(0f, 360f * Mathf.Clamp01(t - 1.5f), 0f);
                    break;
            }
        }

        // Chains = maximal runs of parent-linked physics bones (bones with a DYNAMIC rigid body), root → tip, exactly
        // the strands MmdMagicaCloth simulates. Representative picks per the shared contract:
        //   twintail RightTwicHairA_* | bang BangHairA_* | tie Tie_* | skirt = Dress panel 5 (bodies Dress_<seg>_<panel>).
        private static List<Chain> ExtractChains(PmxLoader pmx)
        {
            int bc = pmx.Bones.Count;
            var dyn = new Dictionary<int, string>();   // bone → its dynamic rigid-body name
            foreach (var rb in pmx.RigidBodies)
                if (rb.Mode != 0 && rb.Bone >= 0 && rb.Bone < bc && !dyn.ContainsKey(rb.Bone)) dyn[rb.Bone] = rb.Name ?? "";
            var physSorted = dyn.Keys.OrderBy(i => i).ToList();
            var phys = new HashSet<int>(physSorted);

            var chainsAll = new List<List<int>>();
            foreach (int r in physSorted)
            {
                int p = pmx.Bones[r].Parent;
                if (p >= 0 && phys.Contains(p)) continue;   // not a chain root
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
            var skirt = dress.FirstOrDefault(ch => dyn[ch[0]].EndsWith("_5"))      // panel 5 (root body Dress_0_5)
                        ?? (dress.Count > 0 ? dress[dress.Count / 2] : null);      // fallback: middle panel

            var outl = new List<Chain>();
            void Add(string name, List<int> ch)
            {
                if (ch == null) return;
                outl.Add(new Chain
                {
                    Name = name,
                    Bones = ch.ToArray(),
                    BoneNames = ch.Select(i => dyn[i]).ToArray(),
                });
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

        // ---- tiny dependency-free JSON writer (contract schema + a few extra self-describing fields) ----
        private static string F(float v) => v.ToString("G9", CultureInfo.InvariantCulture);

        private static void WriteJson(string path, string scenario, float upm, int buildFrames,
                                      List<float[]> anchor, List<Chain> chains)
        {
            var sb = new StringBuilder(4 << 20);
            sb.Append("{\"scenario\":\"").Append(scenario).Append("\",\"fps\":60,\"unitsPerMeter\":").Append(F(upm));
            sb.Append(",\"unitScale\":").Append(F(UnitScale)).Append(",\"buildFrames\":").Append(buildFrames);
            sb.Append(",\"anchor\":[");
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
