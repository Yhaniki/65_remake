using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Pure, testable selection logic used by <see cref="MmdPhysicsProbe"/>.  The runtime probe used to record four
    /// rigid-body prefixes that only exist in Ika Miku; a different PMX therefore emitted a syntactically valid JSON
    /// file with an empty <c>chains</c> object.  This helper keeps the old Ika contract when all four legacy chains are
    /// present, and otherwise selects deterministic root-to-leaf paths from any PMX's dynamic-bone hierarchy.
    /// </summary>
    public static class MmdPhysicsProbeSelection
    {
        public sealed class ChainSpec
        {
            public string Id;
            public string[] BoneNames;
            public int[] Bones;
        }

        private sealed class Candidate
        {
            public List<int> Bones;
            public MmdClothPartId Part;
            public int Root => Bones[0];
            public int Tip => Bones[Bones.Count - 1];
        }

        /// <summary>Read either <c>-flag value</c> or <c>-flag=value</c>; null when absent.</summary>
        public static string ArgValue(string[] args, string flag)
        {
            if (args == null || string.IsNullOrEmpty(flag)) return null;
            string prefix = flag + "=";
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i] ?? "";
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return arg.Substring(prefix.Length);
                if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return args[i + 1];
            }
            return null;
        }

        /// <summary>Standard Japanese head first, then the English-name fallback used by translated PMX files.</summary>
        public static int FindMotionBone(IList<PmxLoader.Bone> bones)
        {
            if (bones == null) return -1;
            for (int i = 0; i < bones.Count; i++)
                if (MmdBoneMap.TryGetBip01(bones[i]?.NameJp, out string target) && target == "Bip01_Head") return i;
            for (int i = 0; i < bones.Count; i++)
                if (string.Equals(bones[i]?.NameEn, "Head", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(bones[i]?.NameJp, "Head", StringComparison.OrdinalIgnoreCase)) return i;
            for (int i = 0; i < bones.Count; i++)
                if ((bones[i]?.NameEn ?? "").IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (bones[i]?.NameJp ?? "").Contains("頭")) return i;
            return -1;
        }

        /// <summary>
        /// True only when every value that would be committed to one recording frame is finite. Magica can expose a
        /// transient NaN write on the first frame after <c>ResetCloth()</c>; the probe may wait for a finite warmup
        /// sample, but must never serialize that write or ignore a later simulation blow-up.
        /// </summary>
        public static bool IsFiniteSample(Vector3 anchorPosition, Quaternion anchorRotation,
                                          IList<Vector3[]> chainPositions,
                                          IList<Vector3> allPhysicsPositions = null)
        {
            if (!IsFinite(anchorPosition.x) || !IsFinite(anchorPosition.y) || !IsFinite(anchorPosition.z) ||
                !IsFinite(anchorRotation.x) || !IsFinite(anchorRotation.y) || !IsFinite(anchorRotation.z) ||
                !IsFinite(anchorRotation.w) || chainPositions == null || chainPositions.Count == 0) return false;
            foreach (var chain in chainPositions)
            {
                if (chain == null || chain.Length == 0) return false;
                foreach (var position in chain)
                    if (!IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z)) return false;
            }
            if (allPhysicsPositions != null)
                foreach (var position in allPhysicsPositions)
                    if (!IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z)) return false;
            return true;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>
        /// Pick at most <paramref name="maxChains"/> representative dynamic root-to-leaf paths. Forks become separate
        /// paths, so neither branch silently disappears. Selection is stable across filesystem/localisation changes:
        /// longest path first within a part, then root/tip bone index.
        /// </summary>
        public static List<ChainSpec> SelectChains(IList<PmxLoader.Bone> bones,
                                                   IList<PmxLoader.RigidBody> bodies,
                                                   int maxChains = 4)
        {
            var output = new List<ChainSpec>();
            if (bones == null || bodies == null || maxChains <= 0) return output;

            var bodyName = new Dictionary<int, string>();
            foreach (var body in bodies)
                if (body != null && body.Mode != 0 && body.Bone >= 0 && body.Bone < bones.Count && !bodyName.ContainsKey(body.Bone))
                    bodyName[body.Bone] = body.Name ?? "";
            if (bodyName.Count == 0) return output;

            var physics = new HashSet<int>(bodyName.Keys);
            var children = new Dictionary<int, List<int>>();
            foreach (int bone in physics.OrderBy(i => i))
            {
                int parent = bones[bone]?.Parent ?? -1;
                if (!physics.Contains(parent)) continue;
                if (!children.TryGetValue(parent, out var list)) children[parent] = list = new List<int>();
                list.Add(bone);
            }
            foreach (var list in children.Values) list.Sort();

            var candidates = new List<Candidate>();
            foreach (int root in physics.OrderBy(i => i))
            {
                int parent = bones[root]?.Parent ?? -1;
                if (physics.Contains(parent)) continue;
                AddLeafPaths(root, new List<int>(), children, bodyName, candidates);
            }
            if (candidates.Count == 0) return output;

            // Preserve the established Ika/Bullet comparison schema. A partial match is not enough: other models may
            // coincidentally call one body "Tie", and must still take the generic path instead of emitting 1/4 data.
            Candidate Pick(Func<string, bool> predicate) => candidates
                .Where(candidate => predicate(bodyName[candidate.Root]))
                .OrderByDescending(candidate => candidate.Bones.Count)
                .ThenBy(candidate => candidate.Root)
                .ThenBy(candidate => candidate.Tip)
                .FirstOrDefault();
            var twin = Pick(name => name.StartsWith("RightTwicHairA", StringComparison.Ordinal));
            var bang = Pick(name => name.StartsWith("BangHairA", StringComparison.Ordinal) || name.Contains("Bang"));
            var tie = Pick(name => name.StartsWith("Tie", StringComparison.Ordinal));
            var skirt = Pick(name => name.StartsWith("Dress", StringComparison.Ordinal) && name.EndsWith("_5", StringComparison.Ordinal))
                        ?? Pick(name => name.StartsWith("Dress", StringComparison.Ordinal));
            if (twin != null && bang != null && tie != null && skirt != null)
            {
                Add(output, "RightTwicHairA", twin, bodyName);
                Add(output, "BangHairA", bang, bodyName);
                Add(output, "Tie", tie, bodyName);
                Add(output, "Dress_5", skirt, bodyName);
                return output.Take(maxChains).ToList();
            }

            var ranked = candidates
                .OrderByDescending(candidate => candidate.Bones.Count)
                .ThenBy(candidate => candidate.Root)
                .ThenBy(candidate => candidate.Tip)
                .ToList();
            var selected = new List<Candidate>();
            var seen = new HashSet<Candidate>();
            foreach (var part in new[] { MmdClothPartId.Bang, MmdClothPartId.Hair, MmdClothPartId.Skirt, MmdClothPartId.Tie })
            {
                var candidate = ranked.FirstOrDefault(item => item.Part == part);
                if (candidate != null && seen.Add(candidate)) selected.Add(candidate);
                if (selected.Count >= maxChains) break;
            }
            foreach (var candidate in ranked)
            {
                if (selected.Count >= maxChains) break;
                if (seen.Add(candidate)) selected.Add(candidate);
            }
            foreach (var candidate in selected)
                Add(output, $"chain_{candidate.Root:D4}_{candidate.Tip:D4}", candidate, bodyName);
            return output;
        }

        private static void AddLeafPaths(int bone, List<int> prefix,
                                         Dictionary<int, List<int>> children,
                                         Dictionary<int, string> bodyName,
                                         List<Candidate> output)
        {
            var path = new List<int>(prefix) { bone };
            if (!children.TryGetValue(bone, out var childBones) || childBones.Count == 0)
            {
                var votes = new int[4];
                foreach (int item in path) votes[(int)MmdMagicaCloth.GroupOf(bodyName[item])]++;
                int winner = 0;
                for (int i = 1; i < votes.Length; i++) if (votes[i] > votes[winner]) winner = i;
                output.Add(new Candidate { Bones = path, Part = (MmdClothPartId)winner });
                return;
            }
            foreach (int child in childBones) AddLeafPaths(child, path, children, bodyName, output);
        }

        private static void Add(List<ChainSpec> output, string id, Candidate candidate,
                                Dictionary<int, string> bodyName)
        {
            output.Add(new ChainSpec
            {
                Id = id,
                Bones = candidate.Bones.ToArray(),
                BoneNames = candidate.Bones.Select(bone => bodyName[bone]).ToArray(),
            });
        }
    }
}
