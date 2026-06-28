using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Small render-profile table for scene EFT emitters whose official balance depends on emitter layering.
    /// Keyed by effect/slot/texture, never by scene or DDS filename.
    /// </summary>
    public readonly struct SceneEftRenderTuning
    {
        public readonly float RgbMul;
        public readonly float AlphaMul;
        public readonly Vector3 ScaleMul;

        public SceneEftRenderTuning(float rgbMul, float alphaMul, float scaleMul)
            : this(rgbMul, alphaMul, new Vector3(scaleMul, scaleMul, scaleMul))
        {
        }

        public SceneEftRenderTuning(float rgbMul, float alphaMul, Vector3 scaleMul)
        {
            RgbMul = rgbMul;
            AlphaMul = alphaMul;
            ScaleMul = scaleMul;
        }
    }

    public static class SceneEftRenderCatalog
    {
        public static readonly SceneEftRenderTuning Identity = new SceneEftRenderTuning(1f, 1f, 1f);

        private readonly struct Entry
        {
            public readonly string Eft;
            public readonly int Slot;
            public readonly int TexIdx;
            public readonly SceneEftRenderTuning Tuning;

            public Entry(string eft, int slot, int texIdx, SceneEftRenderTuning tuning)
            {
                Eft = eft;
                Slot = slot;
                TexIdx = texIdx;
                Tuning = tuning;
            }
        }

        private static readonly Entry[] Entries =
        {
            // fire3.EFT visible slots (confirmed from binary + Frida capture):
            //   slot1 = billboard (0x10001), tex84, Emit=0 → never spawns; EftEffect skips Emit=0 non-carriers
            //   slot2 = world-quad (0x1),    tex84, Emit=1 → the blue upward flame streaks (RotJit.y=360°)
            //   slot4 = billboard (0x10001), tex30, Emit=1 → the broad hearth halo (AEF_4_02 circular gradient)
            // Native capture: SrcAlpha+One, no alpha-test, no zwrite, two-sided, linear filter.
            // Slot4 BaseSize=1.5 @ effScale=100 → 150 world-unit additive billboard. AEF_4_02 centre = pale
            // cyan (199,255,255); needs high alphaMul to produce visible glow. scaleMul=0.80 keeps it from
            // drowning the flame.
            new Entry("fire3", 2, 84, new SceneEftRenderTuning(1.65f, 1.30f, 1.00f)),
            new Entry("fire3", 4, 30, new SceneEftRenderTuning(0.55f, 0.70f, 0.80f)),

            // booklight.EFT: slot2 is the visible glowing orb (billboard, attach=1 to the carrier). The carrier's
            // baseSize (0.3) is multiplied into the orb's world matrix (D3D9 attach = parent_world × child_local),
            // so the faithful quad is only ~14 world units → it reads "發光太小". scaleMul ENLARGES the orb without
            // touching effScale (which would also amplify the orb's vel.y drift off the book). A bigger orb also
            // makes the fixed ~10u rise look proportionally smaller, so it sits ON the book. rgbMul un-dims it.
            // ScaleMul/RgbMul are the two knobs to nudge for "視覺看起來對"; the round shape is handled in EftEffect.
            new Entry("booklight", 2, 31, new SceneEftRenderTuning(0.30f, 1.00f, 2.50f)),
        };

        public static SceneEftRenderTuning Find(string eft, int slot, int texIdx)
        {
            if (string.IsNullOrEmpty(eft)) return Identity;
            for (int i = 0; i < Entries.Length; i++)
            {
                var e = Entries[i];
                if (e.Slot == slot &&
                    e.TexIdx == texIdx &&
                    string.Equals(e.Eft, eft, System.StringComparison.OrdinalIgnoreCase))
                    return e.Tuning;
            }
            return Identity;
        }
    }
}
