using System;
using System.Collections.Generic;
using System.IO;
using Sdo.Shop;

namespace Sdo.Game
{
    /// <summary>
    /// Resolves a worn outfit — equipped 商城 items layered over the WOMAN defaults — into the ordered list of
    /// body-part <c>.msh</c> paths that <see cref="SdoAvatarBuilder"/> loads. This is the single seam the shop's
    /// 換裝 / try-on drives: pick items, resolve parts, rebuild the avatar. Pure logic (mesh existence is injectable),
    /// so the layering rules are unit-testable.
    /// </summary>
    public static class AvatarOutfit
    {
        /// <summary>Default WOMAN body parts keyed by the slot each occupies, so an equipped item replaces exactly one
        /// slot (the same starter costume the in-game dancer / lobby avatar use).</summary>
        public static readonly IReadOnlyDictionary<EquipSlot, string> WomanDefaults = new Dictionary<EquipSlot, string>
        {
            { EquipSlot.Face,   "AVATAR/900007_WOMAN_FACE.MSH" },
            { EquipSlot.Hair,   "AVATAR/900017_WOMAN_HAIR.MSH" },
            { EquipSlot.Top,    "AVATAR/900018_WOMAN_COAT.MSH" },
            { EquipSlot.Bottom, "AVATAR/900019_WOMAN_PANT.MSH" },
            { EquipSlot.Shoes,  "AVATAR/900020_WOMAN_SHOES.MSH" },
            { EquipSlot.Gloves, "AVATAR/900011_WOMAN_HAND.MSH" },
        };

        /// <summary>Default MAN body parts (starter male costume), keyed by slot — the male counterpart of
        /// <see cref="WomanDefaults"/> (verified present: 900001 FACE / 900002 HAIR / 900003 COAT / 900004 PANT /
        /// 900005 HAND / 900006 SHOES).</summary>
        public static readonly IReadOnlyDictionary<EquipSlot, string> ManDefaults = new Dictionary<EquipSlot, string>
        {
            { EquipSlot.Face,   "AVATAR/900001_MAN_FACE.MSH" },
            { EquipSlot.Hair,   "AVATAR/900002_MAN_HAIR.MSH" },
            { EquipSlot.Top,    "AVATAR/900003_MAN_COAT.MSH" },
            { EquipSlot.Bottom, "AVATAR/900004_MAN_PANT.MSH" },
            { EquipSlot.Shoes,  "AVATAR/900006_MAN_SHOES.MSH" },
            { EquipSlot.Gloves, "AVATAR/900005_MAN_HAND.MSH" },
        };

        public const string FemaleHrc = "AVATAR/FEMALE.HRC";
        public const string MaleHrc = "AVATAR/MALE.HRC";

        /// <summary>Skeleton for a gender — male clothing skins to MALE.HRC, female to FEMALE.HRC (never mix: a MAN
        /// mesh on the female skeleton, or vice versa, deforms wrong).</summary>
        public static string HrcFor(ItemSex sex) => sex == ItemSex.Male ? MaleHrc : FemaleHrc;

        /// <summary>Starter default parts for a gender.</summary>
        public static IReadOnlyDictionary<EquipSlot, string> DefaultsFor(ItemSex sex)
            => sex == ItemSex.Male ? ManDefaults : WomanDefaults;

        // Stable draw order (Glasses/Necklace/Wings additive — no default; Expression replaces the Face slot, see below).
        private static readonly EquipSlot[] Order =
            { EquipSlot.Face, EquipSlot.Hair, EquipSlot.Top, EquipSlot.Bottom, EquipSlot.Shoes, EquipSlot.Gloves, EquipSlot.Glasses, EquipSlot.Necklace, EquipSlot.Wings };

        /// <summary>
        /// Build the WOMAN parts list wearing <paramref name="equipped"/> over the defaults. A worn item replaces its
        /// slot's mesh (Hair→Hair, Top→Coat, …); a OnePiece replaces Top and drops the separate Bottom; Glasses is
        /// additive. Items whose mesh isn't on disk are skipped (the default stays) — <paramref name="meshExists"/>
        /// defaults to the real Root+Datas resolver.
        /// </summary>
        /// <summary>Female-default overload (back-compat for the female-only callers).</summary>
        public static List<string> ResolveParts(IEnumerable<ShopItem> equipped, Func<string, bool> meshExists = null)
            => ResolveParts(ItemSex.Female, equipped, meshExists);

        /// <summary>As above but for either gender: layers <paramref name="equipped"/> over that gender's defaults.
        /// Equipped items should already be of matching gender (male-category meshes only skin to MALE.HRC).</summary>
        public static List<string> ResolveParts(ItemSex sex, IEnumerable<ShopItem> equipped, Func<string, bool> meshExists = null)
        {
            meshExists = meshExists ?? DefaultMeshExists;
            var slots = new Dictionary<EquipSlot, string>(DefaultsFor(sex));
            if (equipped != null)
                foreach (var it in equipped)
                {
                    if (it == null) continue;
                    var rel = it.MshRelPath;
                    if (rel == null || !meshExists(rel)) continue;
                    switch (it.EquipSlot)
                    {
                        case EquipSlot.OnePiece: slots[EquipSlot.Top] = rel; slots.Remove(EquipSlot.Bottom); break;
                        case EquipSlot.Expression: slots[EquipSlot.Face] = rel; break;   // 表情 = 換臉 (取代預設臉,避免雙臉 z-fight)
                        case EquipSlot.None: break;
                        default: slots[it.EquipSlot] = rel; break;                       // Necklace 等飾品 = 各自 slot (Order 已含)
                    }
                }
            var list = new List<string>(Order.Length);
            foreach (var s in Order)
                if (slots.TryGetValue(s, out var p) && !string.IsNullOrEmpty(p)) list.Add(p);
            return list;
        }

        private static bool DefaultMeshExists(string rel) => File.Exists(SdoAvatarBuilder.ResolveAvatarFile(rel));
    }
}
