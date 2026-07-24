namespace Sdo.Shop
{
    // Item taxonomy for the 商城 (shop), ported verbatim from the official item catalog as reverse-engineered by the
    // Arrowgene Dance!Online server emulator (library/models/item/*.java) and validated against the real client
    // iteminfo.dat. The numeric enum values ARE the on-disk / on-wire magic numbers — do not renumber.

    /// <summary>Which wallet a price is charged to. Raw values are the iteminfo.dat priceCategory byte
    /// (ItemPriceCategoryType.java). Note the official set skips 2 and 3.</summary>
    public enum ItemPriceCurrency
    {
        Points = 0,   // 遊戲內累積點數 (earned by playing)
        Coins = 1,    // 儲值現金幣 (cash)
        Bonus = 4,    // 紅利 / 活動獎勵幣
    }

    /// <summary>Item gender restriction (ItemSexType.java).</summary>
    public enum ItemSex
    {
        Female = 0,
        Male = 1,
        Both = 2,
    }

    /// <summary>Inventory bucket + packet slot-id base (InventorySlotType.java). A worn slot id on the wire is
    /// <c>(int)SlotType + slotNumber</c>.</summary>
    public enum ItemSlotType
    {
        Clothes = 200,
        Items = 400,
    }

    /// <summary>Where a clothing item is worn — or, for the non-worn 2D 商品 (道具/藥水/特效/寵物/禮包)，which shop
    /// shelf it sits on. Derived from the raw category magic number, collapsing the per-gender pair (e.g. male 2 /
    /// female 102 → Top). 非穿戴的那幾個 (Consumable…GiftPack) 不是「穿在哪」而是「賣在哪一頁」——沿用同一個 enum 是為了
    /// 讓商城 UI 的分頁模型統一 (Consumable/Effect 本來就在這裡)；用 <see cref="ItemTypes.IsProp"/> 區分。</summary>
    public enum EquipSlot
    {
        None = 0,
        Face,
        Hair,
        Top,
        Bottom,
        Gloves,
        Shoes,
        Glasses,
        OnePiece,
        Outfit,
        Consumable,
        Effect,
        Expression,   // 表情 (饰品店 表情頁；cat 6/106，mesh _FACE_HUAN)
        Necklace,     // 项链 (饰品店 项链頁；cat 9/109，mesh _LINGDANG)
        Wings,        // 翅膀/背飾 (饰品店 翅膀頁；cat 8/108，mesh _CHIBANG；官方 SHOP.XML CheckBox name="wing")

        // ---- 非衣服的 2D 商品 (無 avatar mesh，商品格畫 ITEM2D 圖示)。SHOP.XML 的 Property / Shoppet 視窗分頁 ----
        Potion,       // 藥水 (道具店 pill 頁；cat 22000，如 标准/纤瘦/胖胖药水 → 體型)
        Pet,          // 寵物本體 (伙伴店 Petavt 頁；cat 41000，modelId 1000xxx → PETAVATAR/*.MSH)
        PetHead,      // 寵物頭飾 (伙伴店 Petclo 頁；cat 42000，modelId 1030xxx → PETAVATAR/*_HEADEAR.*，PETHEADEAR.TXT 掛點表)
        PetClothes,   // 寵物衣服 (伙伴店 Petface 頁；cat 43000，modelId 1040xxx → PETAVATAR/*_ALL_COAT.*)
        PetProp,      // 寵物道具/食物 (伙伴店 Petpro 頁；cat 44000，modelId 1090xxx，如 饼干)
        GiftPack,     // 禮包 (礼包店；cat 14000，modelId 6000xx，如 婚庆大礼包)
    }

    /// <summary>Rental period in days (ItemDurationType.java). Permanent = -1; Zero(0) is a known one-off oddity.</summary>
    public enum ItemDuration
    {
        Permanent = -1,
        Zero = 0,
        Seven = 7,
        Thirty = 30,
    }

    /// <summary>The raw category magic numbers (ItemCategoryType.java) — same value both genders share the mapping
    /// helpers in <see cref="ItemTypes"/>.</summary>
    public static class ItemCategory
    {
        // male
        public const int HairMale = 1, TopMale = 2, BottomMale = 3, GlovesMale = 4, ShoesMale = 5,
                         FaceMale = 6, GlassesMale = 7, WingsMale = 8, NecklaceMale = 9, OnePieceMale = 50, OutfitMale = 201;
        // female
        public const int HairFemale = 101, TopFemale = 102, BottomFemale = 103, GlovesFemale = 104, ShoesFemale = 105,
                         FaceFemale = 106, GlassesFemale = 107, WingsFemale = 108, NecklaceFemale = 109, OnePieceFemale = 150, OutfitFemale = 200;
        public const int OutfitMixed = 203;   // 套装(混性別;sex byte 皆 0,性別要靠名字 男/女 判)

        // ---- 非衣服 (無 avatar mesh，商品格畫 2D 圖示)。實檔 iteminfo.dat 31,563 筆的 category 分布佐證：----
        public const int MainConsumables = 21000;   // 道具   188 modelId (100000 小喇叭…)   → 道具店 funcprop
        public const int Potions = 22000;           // 藥水    21 modelId (100031 标准药水…) → 道具店 pill
        public const int AvatarEffects = 24000;     // 人物特效 36 modelId (200000 樱花纷飞 = ITEM2D 200000_CHARBACK0_*) → 道具店 effectplayer
        public const int GiftPacks = 14000;         // 禮包    28 modelId (600003 婚庆大礼包…) → 礼包店
        public const int Pets = 41000;              // 寵物    52 modelId (1000000 鼠小弟 豆豆…) → 伙伴店 Petavt
        public const int PetHeadwear = 42000;       // 寵物頭飾 21 modelId (1030000 小小绅士…)  → 伙伴店 Petclo
        public const int PetClothes = 43000;        // 寵物衣服 24 modelId (1040000 我爱你~~…)  → 伙伴店 Petface
        public const int PetProps = 44000;          // 寵物道具 10 modelId (1090000 普通饼干…)  → 伙伴店 Petpro
    }

    /// <summary>Pure mapping helpers between the raw catalog magic numbers and the typed enums.</summary>
    public static class ItemTypes
    {
        /// <summary>Map the raw priceCategory byte to a wallet. Unknown values fall back to <see cref="ItemPriceCurrency.Points"/>.</summary>
        public static ItemPriceCurrency CurrencyFromRaw(int raw)
        {
            switch (raw)
            {
                case 0: return ItemPriceCurrency.Points;
                case 1: return ItemPriceCurrency.Coins;
                case 4: return ItemPriceCurrency.Bonus;
                default: return ItemPriceCurrency.Points;
            }
        }

        /// <summary>Map a raw sex byte to the enum. Unknown (e.g. 0xFF placeholder rows) → Both (unrestricted).</summary>
        public static ItemSex SexFromRaw(int raw)
        {
            switch (raw)
            {
                case 0: return ItemSex.Female;
                case 1: return ItemSex.Male;
                case 2: return ItemSex.Both;
                default: return ItemSex.Both;
            }
        }

        /// <summary>非穿戴的 2D 商品 (道具/藥水/特效/禮包/寵物 那幾類) 進背包 ITEMS(400) 桶；其餘穿戴品進 CLOTHES(200)。
        /// Mirrors ShopItem.getSlotType().</summary>
        public static ItemSlotType SlotTypeFromCategory(int category)
            => IsProp(EquipSlotFromCategory(category)) ? ItemSlotType.Items : ItemSlotType.Clothes;

        /// <summary>這個 slot 是「非穿戴的 2D 商品」嗎 (商品格畫 ITEM2D 圖示、不試穿、買了進背包)。</summary>
        public static bool IsProp(EquipSlot slot)
        {
            switch (slot)
            {
                case EquipSlot.Consumable:
                case EquipSlot.Potion:
                case EquipSlot.Effect:
                case EquipSlot.GiftPack:
                case EquipSlot.Pet:
                case EquipSlot.PetHead:
                case EquipSlot.PetClothes:
                case EquipSlot.PetProp:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>會堆疊的消耗品 (道具/藥水/寵物食物/禮包 —— 買第二次是 +數量，不是「已擁有」擋掉)。
        /// 寵物本體、寵物頭飾/衣服、人物特效各只擁有一份。</summary>
        public static bool IsStackable(int category)
        {
            switch (category)
            {
                case ItemCategory.MainConsumables:
                case ItemCategory.Potions:
                case ItemCategory.PetProps:
                case ItemCategory.GiftPacks:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Collapse the per-gender category magic number to a worn slot (or Consumable/Effect/None).</summary>
        public static EquipSlot EquipSlotFromCategory(int category)
        {
            switch (category)
            {
                case ItemCategory.HairMale: case ItemCategory.HairFemale: return EquipSlot.Hair;
                case ItemCategory.TopMale: case ItemCategory.TopFemale: return EquipSlot.Top;
                case ItemCategory.BottomMale: case ItemCategory.BottomFemale: return EquipSlot.Bottom;
                case ItemCategory.GlovesMale: case ItemCategory.GlovesFemale: return EquipSlot.Gloves;
                case ItemCategory.ShoesMale: case ItemCategory.ShoesFemale: return EquipSlot.Shoes;
                // 6/106 是商城「表情」頁 (mesh _FACE_HUAN)，不是底層預設臉——底層臉走 AvatarOutfit 的 enum 定值,不經 category。
                case ItemCategory.FaceMale: case ItemCategory.FaceFemale: return EquipSlot.Expression;
                case ItemCategory.NecklaceMale: case ItemCategory.NecklaceFemale: return EquipSlot.Necklace;
                case ItemCategory.WingsMale: case ItemCategory.WingsFemale: return EquipSlot.Wings;   // 8/108 = 翅膀/背飾 (mesh _CHIBANG)
                case ItemCategory.GlassesMale: case ItemCategory.GlassesFemale: return EquipSlot.Glasses;
                case ItemCategory.OnePieceMale: case ItemCategory.OnePieceFemale: return EquipSlot.OnePiece;
                case ItemCategory.OutfitMale: case ItemCategory.OutfitFemale: case ItemCategory.OutfitMixed: return EquipSlot.Outfit;
                // 非衣服的 2D 商品 (無 mesh；商品格畫 ITEM2D 圖示)
                case ItemCategory.MainConsumables: return EquipSlot.Consumable;
                case ItemCategory.Potions: return EquipSlot.Potion;
                case ItemCategory.AvatarEffects: return EquipSlot.Effect;
                case ItemCategory.GiftPacks: return EquipSlot.GiftPack;
                case ItemCategory.Pets: return EquipSlot.Pet;
                case ItemCategory.PetHeadwear: return EquipSlot.PetHead;
                case ItemCategory.PetClothes: return EquipSlot.PetClothes;
                case ItemCategory.PetProps: return EquipSlot.PetProp;
                default: return EquipSlot.None;
            }
        }

        /// <summary>The AVATAR/*.MSH filename SLOT token for a clothing category (HAIR/COAT/PANT/HAND/SHOES/FACE/
        /// GLASS/ONE), verified against the extracted asset folder. Returns null for Outfit / Consumable / Effect /
        /// unknown (no single-part .msh). Note: gloves map to HAND and tops map to COAT (the engine's own naming).</summary>
        public static string MshSlotSuffix(int category)
        {
            switch (EquipSlotFromCategory(category))
            {
                case EquipSlot.Hair: return "HAIR";
                case EquipSlot.Top: return "COAT";
                case EquipSlot.Bottom: return "PANT";
                case EquipSlot.Gloves: return "HAND";
                case EquipSlot.Shoes: return "SHOES";
                case EquipSlot.Expression: return "FACE_HUAN";   // 表情 mesh (另有少數 _EXPRESSION 命名者→無此檔會列為無模型)
                case EquipSlot.Necklace: return "LINGDANG";      // 项链 mesh
                case EquipSlot.Wings: return "CHIBANG";          // 翅膀 mesh (實測 109 件 cat8/108 全對到 NNNNNN_性別_CHIBANG.MSH)
                case EquipSlot.Glasses: return "GLASS";
                case EquipSlot.OnePiece: return "ONE";
                default: return null;
            }
        }

        /// <summary>Gender implied by the category magic number (male block 1-7,50,201 vs female 101-107,150,200).
        /// Returns Both for non-clothing categories.</summary>
        public static ItemSex SexFromCategory(int category)
        {
            switch (category)
            {
                case ItemCategory.HairMale: case ItemCategory.TopMale: case ItemCategory.BottomMale:
                case ItemCategory.GlovesMale: case ItemCategory.ShoesMale: case ItemCategory.FaceMale:
                case ItemCategory.GlassesMale: case ItemCategory.WingsMale: case ItemCategory.NecklaceMale:
                case ItemCategory.OnePieceMale: case ItemCategory.OutfitMale:
                    return ItemSex.Male;
                case ItemCategory.HairFemale: case ItemCategory.TopFemale: case ItemCategory.BottomFemale:
                case ItemCategory.GlovesFemale: case ItemCategory.ShoesFemale: case ItemCategory.FaceFemale:
                case ItemCategory.GlassesFemale: case ItemCategory.WingsFemale: case ItemCategory.NecklaceFemale:
                case ItemCategory.OnePieceFemale: case ItemCategory.OutfitFemale:
                    return ItemSex.Female;
                default:
                    return ItemSex.Both;
            }
        }

        /// <summary>Gender of a shop ROW, handling 套装 (outfit) whose sex byte is unreliable: cat 203 (mixed) carries
        /// sex=0 for both genders, so its gender comes from the 男/女 in the name; cat 200/201 already resolve via
        /// <see cref="SexFromCategory"/>. Everything else falls back to the category. Used to group/filter outfit rows.</summary>
        public static ItemSex GenderOf(int category, string name)
        {
            if (category == ItemCategory.OutfitMixed)
                return (name != null && name.IndexOf('男') >= 0) ? ItemSex.Male : ItemSex.Female;
            return SexFromCategory(category);
        }
    }
}
