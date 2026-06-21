using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Hand-authored UV-scroll speeds for stage props the original animates by streaming texture coordinates (the
    /// decompiled scene updates in FUN_004ad250 write tex-coord offset 0x58=U / 0x5c=V each tick). Keyed by scene
    /// FOLDER + mesh base name; speed is in UV units per second.
    ///
    /// SCN0014 underwater corals: StageScene_UpdateMultiPlacement_004b0330 scrolls V by _DAT_00589034 (0.004) every
    /// 50 ms = 0.08 / s, wrapping at 1.0 — the glow streams like a marquee. (Some render states use 2×; we apply the
    /// base rate uniformly, which reads the same.)
    /// </summary>
    public static class SceneMapobjUvScrollCatalog
    {
        private static readonly Vector2 CoralV = new Vector2(0f, -0.08f);   // V scroll, 0.08 UV/s (decompiled)

        private static readonly Dictionary<string, Dictionary<string, Vector2>> ByFolder =
            new Dictionary<string, Dictionary<string, Vector2>>
            {
                ["SCN0014"] = new Dictionary<string, Vector2>
                {
                    ["SHANHU-BAI"] = CoralV, ["SHANHU-HONG"] = CoralV, ["SHANHU-LV"] = CoralV,
                    ["SHANHUZHI-BAI"] = CoralV, ["SHANHUZHI-HONG"] = CoralV, ["SHANHUZHI-LV"] = CoralV,
                },
            };

        /// <summary>UV-scroll speed (UV/s) for a (scene folder, mesh base) pair, or Vector2.zero if it doesn't scroll.</summary>
        public static Vector2 Find(string folder, string meshBase)
        {
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(meshBase)) return Vector2.zero;
            if (!ByFolder.TryGetValue(folder.ToUpperInvariant(), out var m)) return Vector2.zero;
            return m.TryGetValue(meshBase.ToUpperInvariant(), out var v) ? v : Vector2.zero;
        }
    }
}
