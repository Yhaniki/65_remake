namespace Sdo.Shop
{
    /// <summary>
    /// One catalog entry in the 商城 (shop) — the authoritative name + price + classification for a single avatar
    /// item, as read from the client's <c>iteminfo.dat</c> (see <see cref="IteminfoReader"/>) or an equivalent seed
    /// (arrowgene ag_items). Mirrors the server emulator's ShopItem.java. All the fields that decide how the item is
    /// shown and sold live HERE — the official protocol never sends the catalog over the network; the client owns it.
    ///
    /// Price is two things: a raw <see cref="Price"/> number and a <see cref="Currency"/> (which wallet). The same
    /// garment often appears as several entries with different <see cref="Duration"/> (7 / 30 / permanent) at
    /// different prices — the "租7天 / 租30天 / 永久" tiers.
    /// </summary>
    public sealed class ShopItem
    {
        public int Id;                 // unique item id (= the 6-digit prefix of its AVATAR/*.MSH files)
        public string Name;            // display name (GBK in the .dat; already decoded to a C# string here)
        public int Price;              // price magnitude (currency-less); may be 0 (free) or -1 (undefined placeholder)
        public int PriceCategoryRaw;   // raw priceCategory byte → Currency
        public int ModelId;            // texture / model id
        public int Category;           // raw category magic number (encodes gender + body slot)
        public int MinLevel;           // level required to buy / wear
        public int DurationDays;       // rental days: -1 permanent / 0 / 7 / 30
        public int Quantity;           // consumable stack size; -1 = not consumable (clothes)
        public int SexRaw;             // raw sex byte → Sex
        public bool WeddingRing;       // marriage-ring item

        public ItemPriceCurrency Currency => ItemTypes.CurrencyFromRaw(PriceCategoryRaw);
        public ItemSex Sex => ItemTypes.SexFromRaw(SexRaw);
        public ItemSlotType SlotType => ItemTypes.SlotTypeFromCategory(Category);
        public EquipSlot EquipSlot => ItemTypes.EquipSlotFromCategory(Category);
        public bool IsClothes => SlotType == ItemSlotType.Clothes;
        /// <summary>會堆疊的消耗品 (道具/藥水/寵物食物/禮包) —— 買第二次是 +數量。</summary>
        public bool IsConsumable => ItemTypes.IsStackable(Category);
        /// <summary>非穿戴的 2D 商品 (道具/藥水/特效/寵物/禮包)：沒有 avatar mesh，商品格畫 ITEM2D 圖示、不能試穿。</summary>
        public bool IsProp => ItemTypes.IsProp(EquipSlot);
        public bool IsPermanent => DurationDays < 0;

        /// <summary>Gender folder token for the model files (MAN / WOMAN), from the category block.</summary>
        public string GenderFolder => ItemTypes.SexFromCategory(Category) == ItemSex.Male ? "MAN" : "WOMAN";

        /// <summary>
        /// The Extracted-relative path of this item's body-part mesh, e.g. <c>AVATAR/000673_WOMAN_GLASS.MSH</c>.
        /// The on-disk filename prefix is the 6-digit zero-padded <see cref="ModelId"/> (NOT the item Id) — validated
        /// against the extracted AVATAR folder (30,766 / 31,563 catalog items resolve). Returns null for items with no
        /// single worn mesh (outfits / consumables / effects). The caller checks the file actually exists.
        /// </summary>
        public string MshRelPath
        {
            get
            {
                string slot = ItemTypes.MshSlotSuffix(Category);
                return slot == null ? null : $"AVATAR/{ModelId:D6}_{GenderFolder}_{slot}.MSH";
            }
        }

        public override string ToString() => $"[{Id}] {Name} ({Price} {Currency})";
    }
}
