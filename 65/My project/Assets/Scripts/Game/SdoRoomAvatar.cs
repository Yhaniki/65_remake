using System.IO;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Builds a default female (WOMAN) SDO avatar for the waiting room — the same skeleton + 6 body parts + standby
    /// idle the in-game dancer uses (ScreenGameplay.TryLoadAvatar), but standalone so the room can spawn one without
    /// going through the play screen. Reused for both the walkable local player (full-body, in-scene) and the isolated
    /// head-portrait avatar (parked off-stage, rendered head-only). Avatar motion/skinning is driven by SdoAvatar.
    /// </summary>
    public static class SdoRoomAvatar
    {
        // Default WOMAN costume — identical to ScreenGameplay's defaults so the lobby avatar matches the dancer.
        public static readonly string[] WomanParts =
        {
            "AVATAR/900007_WOMAN_FACE.MSH",
            "AVATAR/900017_WOMAN_HAIR.MSH",
            "AVATAR/900018_WOMAN_COAT.MSH",
            "AVATAR/900019_WOMAN_PANT.MSH",
            "AVATAR/900020_WOMAN_SHOES.MSH",
            "AVATAR/900011_WOMAN_HAND.MSH",
        };
        public const string FemaleHrc = "AVATAR/FEMALE.HRC";
        public const string IdleMot = "MOTION/WREST0056.MOT";   // LOBBY standby idle (motion cat 0) — NOT the in-game
                                                                 // arena idle WREST0072 (cat 0x15); the room holds standby
        public const string WalkMot = "MOTION/WWALK0001.MOT";   // free-walk clip (StateRoom walk category 6)

        /// <summary>
        /// Build the avatar onto <paramref name="parent"/>: load FEMALE.HRC, the 6 WOMAN parts and the idle clip, set
        /// the default (thin) body shape, arm the idle pose, and put everything on <paramref name="layer"/>. When
        /// <paramref name="portraitOpaque"/> the parts use the Sdo/PortraitOpaque shader (clean opaque head for the
        /// portrait RT); otherwise normal Unlit/Texture + two-sided hair, with the 2-material COAT/PANT skin submeshes
        /// resolved per-range (so arms/legs aren't painted with cloth). Returns the SdoAvatar, or null if the skeleton
        /// or every part failed to load.
        /// </summary>
        public static SdoAvatar Build(GameObject parent, int layer, bool portraitOpaque, string[] parts = null, string hrcRel = null,
                                      bool bindPoseNoIdle = false)
        {
            string hrcPath = SdoAvatarBuilder.ResolveAvatarFile(hrcRel ?? FemaleHrc);   // MALE.HRC for male outfits
            if (!File.Exists(hrcPath)) { Debug.LogWarning("[room-avatar] missing " + hrcPath); return null; }
            var hrc = HrcLoader.Load(File.ReadAllBytes(hrcPath));
            if (hrc == null) { Debug.LogWarning("[room-avatar] HRC parse fail"); return null; }

            // bindPoseNoIdle (商城 shop preview): show the skeleton's BIND POSE with NO motion — exactly the official
            // AvtShow (AvtShow_LoadModelByName → AvatarHelper_Create(name,0), no .mot). The shop passes the wshop/mshop
            // MANNEQUIN hrc whose bind is arms-down + STRAIGHT legs (FEMALE/MALE.HRC bind is a T-pose; the bent knees
            // came from the WREST/MREST idle frame-0). Animate=false → SdoAvatar.Pose uses hrc.LocalRest for every bone.
            var idle = bindPoseNoIdle ? null : LoadMot(IdleMot);
            var av = parent.AddComponent<SdoAvatar>();
            av.Setup(hrc, idle);
            av.SetBodyShape(SdoBodyShape.WeightFromIndex(0, false));   // default thin female (matches the dancer)
            if (bindPoseNoIdle) { av.Animate = false; }
            else { av.RestMot = idle; av.BlendSec = 0f; }   // no idle↔walk crossfade in the lobby — start walking immediately

            // Load the WOMAN body parts via the shared builder (same loop the in-game dancer + head portrait use).
            var built = SdoAvatarBuilder.LoadParts(parent, av, parts ?? WomanParts,
                portraitOpaque ? SdoAvatarBuilder.SkinStyle.Portrait : SdoAvatarBuilder.SkinStyle.Gameplay);
            if (built.Parts == 0) { Debug.LogWarning("[room-avatar] no parts loaded"); Object.Destroy(av); return null; }

            if (bindPoseNoIdle) av.PoseFrame(0f);   // skin the bind pose now (retargets T-pose-authored garments onto the mannequin)
            else av.PoseInitialIdle();              // arm the idle so the first frame isn't the bind/T-pose
            SetLayerRecursive(parent, layer);
            return av;
        }

        public static MotLoader LoadMot(string rel)
        {
            try
            {
                var path = Path.Combine(SdoExtracted.Root, rel.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(path) ? MotLoader.Load(File.ReadAllBytes(path)) : null;
            }
            catch { return null; }
        }

        public static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform c in go.transform) SetLayerRecursive(c.gameObject, layer);
        }
    }
}
