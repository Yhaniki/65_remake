using System.Collections.Generic;

namespace Sdo.Shop
{
    /// <summary>
    /// One 套装 (OUTFIT / SET) decoded from the client's <c>setinfo.dat</c> — a full outfit sold as a single shop
    /// entry that, when worn, equips several individual garment components at once (top + bottom + shoes + hair …).
    /// The sellable name/price live in <c>iteminfo.dat</c> (categories 200 / 201 / 203); this record only holds the
    /// component list. A shop set row links here by <see cref="ShopItem.ModelId"/> == <see cref="SetId"/>.
    /// </summary>
    public sealed class OutfitSet
    {
        public int SetId;
        /// <summary>Up to 6 component garments (empty slots dropped). Each is a raw model id → an AVATAR/*.MSH.</summary>
        public readonly List<OutfitComponent> Components = new List<OutfitComponent>();
    }

    /// <summary>One garment inside an <see cref="OutfitSet"/>. <see cref="ModelId"/> is the 6-digit file prefix of the
    /// component mesh (e.g. 3829 → <c>003829_WOMAN_COAT.MSH</c>); the slot token is embedded in the on-disk filename,
    /// not stored here. <see cref="Flag"/> is -1 for garment sets (for gift bundles it is the granted quantity).</summary>
    public struct OutfitComponent
    {
        public int ModelId;
        public short Flag;
        public string Name;   // per-component GBK name (usually empty; display name comes from the iteminfo row)
    }
}
