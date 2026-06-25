using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Scene effects that the native stage controller attaches to mapobj skeleton nodes every frame.
    /// These are not ordinary world placements: the original resolves a node name (e.g. Plane29) and
    /// calls Effect_SetTransformAnimated at that node's current world position.
    /// </summary>
    public readonly struct SceneAttachedEft
    {
        public readonly string Eft;
        public readonly string Bone;
        public readonly Vector3 Offset;
        public readonly Vector3 EulerDeg;
        public readonly float Scale;

        public SceneAttachedEft(string eft, string bone, Vector3 offset, Vector3 eulerDeg, float scale)
        {
            Eft = eft;
            Bone = bone;
            Offset = offset;
            EulerDeg = eulerDeg;
            Scale = scale;
        }
    }

    public static class SceneAttachedEftCatalog
    {
        private static readonly IReadOnlyList<SceneAttachedEft> Empty = new SceneAttachedEft[0];

        private static readonly Dictionary<string, Dictionary<string, SceneAttachedEft[]>> ByFolder =
            new Dictionary<string, Dictionary<string, SceneAttachedEft[]>>
            {
                ["SCN0015"] = new Dictionary<string, SceneAttachedEft[]>
                {
                    // StageScene15 update FUN_004addd0:
                    //   nodes Plane29/66/33/31 -> Effect_SetTransformAnimated(booklight, x, y - 5, z, scale 20)
                    ["15_SHU1"] = OneBooklight("Plane29"),
                    ["15_SHU2"] = OneBooklight("Plane66"),
                    ["15_SHU3"] = OneBooklight("Plane33"),
                    ["15_SHU4"] = OneBooklight("Plane31"),
                },
            };

        public static IReadOnlyList<SceneAttachedEft> ForMapobj(string folder, string mapobjBase)
        {
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(mapobjBase)) return Empty;
            if (!ByFolder.TryGetValue(folder.ToUpperInvariant(), out var byObject)) return Empty;
            return byObject.TryGetValue(mapobjBase.ToUpperInvariant(), out var entries) ? entries : Empty;
        }

        private static SceneAttachedEft[] OneBooklight(string bone)
        {
            return new[] { new SceneAttachedEft("booklight", bone, new Vector3(0f, -5f, 0f), Vector3.zero, 20f) };
        }
    }
}
