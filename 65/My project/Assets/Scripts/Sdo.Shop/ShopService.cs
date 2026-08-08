using System.Collections.Generic;

namespace Sdo.Shop
{
    /// <summary>Outcome of a purchase (mirrors ShopMessages.java result codes).</summary>
    public enum BuyResult
    {
        Ok,                // MSG_NO_ERROR
        UnknownItem,       // MSG_ERROR (item not in catalog)
        NotEnoughMoney,    // MSG_YOU_DO_NOT_HAVE_ENOUGH_COINS_POINTS_OR_BONUS_FOR_THIS_ITEM
        NoRoom,            // MSG_YOU_HAVE_NO_MORE_ROOM
        AlreadyOwned,      // remake guard: clothing you already own (and hasn't expired)
    }

    /// <summary>Outcome of a dress/equip attempt (mirrors DressItemMsg.java result codes).</summary>
    public enum EquipResult
    {
        Ok,           // NO_ERROR
        NotOwned,     // can't wear what you don't own
        Expired,      // rental lapsed
        MinLevel,     // SORRY_YOU_MUST_MEET_THE_MINIMUM_LEVEL
        WrongGender,  // CANT_EQUIP_ITEM_WITH_CURRENT_GENDER
        NotWearable,  // consumables / effects aren't a worn clothing slot
    }

    /// <summary>
    /// The 商城 transaction + 換裝 rules, ported from the server emulator's Shop.java + the _7002 dress handler, but
    /// evaluated locally for the single-player remake. Pure logic (no Unity, no I/O) so it is fully unit-tested; the
    /// UI (<c>ScreenShop</c>) and any persistence layer call these and react to the result codes.
    ///
    /// Buy order (faithful to Shop.buyItem): has-space → can-afford → spend → craft (expiry from duration) → own.
    /// Time is passed in as <c>nowUnix</c> (Unix seconds) rather than read from a clock, so purchases are deterministic
    /// and testable.
    /// </summary>
    public static class ShopService
    {
        private const long SecondsPerDay = 86400L;

        public static bool CanAfford(Wallet wallet, ItemPriceCurrency currency, int amount)
            => wallet != null && wallet.Get(currency) >= amount;

        public static bool CanAfford(Wallet wallet, ShopItem item)
            => item != null && CanAfford(wallet, item.Currency, item.Price);

        /// <summary>Expiry timestamp for a rental duration (Shop.craftItem): permanent → -1, 0 → 1 (the known
        /// odd case), N days → now + N·86400. Unknown/placeholder durations are treated as permanent.</summary>
        public static long ComputeExpire(int durationDays, long nowUnix)
        {
            switch (durationDays)
            {
                case (int)ItemDuration.Permanent: return -1;
                case (int)ItemDuration.Zero: return 1;
                case (int)ItemDuration.Seven: return nowUnix + 7 * SecondsPerDay;
                case (int)ItemDuration.Thirty: return nowUnix + 30 * SecondsPerDay;
                default: return durationDays > 0 ? nowUnix + durationDays * SecondsPerDay : -1;
            }
        }

        /// <summary>Attempt to buy <paramref name="item"/> into <paramref name="wardrobe"/> at <paramref name="nowUnix"/>.
        /// Deducts the right wallet on success and records the owned entry (consumables stack).</summary>
        public static BuyResult Buy(Wardrobe wardrobe, ShopItem item, long nowUnix)
        {
            if (wardrobe == null || item == null) return BuyResult.UnknownItem;

            bool consumable = item.IsConsumable;
            var slot = item.SlotType;

            // Already own this (non-consumable) and it hasn't lapsed → nothing to buy.
            if (!consumable)
            {
                var existing = wardrobe.GetOwned(item.Id);
                if (existing != null && !existing.IsExpired(nowUnix)) return BuyResult.AlreadyOwned;
            }

            // Slot capacity (Shop.hasSpace). Restocking an already-owned consumable doesn't consume a new slot.
            bool needsNewSlot = !(consumable && wardrobe.Owns(item.Id));
            if (needsNewSlot)
            {
                int used = wardrobe.Count(slot);
                int cap = slot == ItemSlotType.Clothes ? wardrobe.ClothSlotCount : wardrobe.ItemSlotCount;
                if (used >= cap) return BuyResult.NoRoom;
            }

            if (!CanAfford(wardrobe.Wallet, item)) return BuyResult.NotEnoughMoney;

            wardrobe.Wallet.Spend(item.Currency, item.Price);

            int addQty = item.Quantity > 0 ? item.Quantity : 1;
            var owned = wardrobe.GetOwned(item.Id);
            if (consumable && owned != null)
            {
                owned.Quantity += addQty;   // stack
            }
            else
            {
                wardrobe.AddOwned(new OwnedItem
                {
                    ItemId = item.Id,
                    Slot = slot,
                    ExpireUnix = ComputeExpire(item.DurationDays, nowUnix),
                    Quantity = consumable ? addQty : 1,
                });
            }
            return BuyResult.Ok;
        }

        /// <summary>
        /// Buy a 套装 (outfit set): one price for the set, but the player receives its COMPONENT GARMENTS as separate
        /// inventory entries — each takes its own 儲物櫃 格子, exactly like the retail client (使用者:「不是一整個進去
        /// 格子,而是各個部分分開各自一個格子」). The set row itself is never owned (it has no mesh of its own).
        ///
        /// Order mirrors <see cref="Buy"/>: already-complete → space for the pieces still missing → afford → spend →
        /// craft each piece with the SET's duration. Pieces already owned (and unexpired) are left alone: they neither
        /// cost a slot nor get re-issued, so re-buying a set the player half-owns just fills the gaps.
        /// <paramref name="components"/> comes from the catalog (AvatarItemCatalog.OutfitComponentItems) — passed in so
        /// this stays pure/unit-testable.
        /// </summary>
        public static BuyResult BuyOutfit(Wardrobe wardrobe, ShopItem outfit, IList<ShopItem> components, long nowUnix)
        {
            if (wardrobe == null || outfit == null || components == null || components.Count == 0) return BuyResult.UnknownItem;

            int need = 0;                                   // 還沒擁有(或已過期)的件數 = 要佔幾格
            foreach (var c in components)
            {
                if (c == null) continue;
                var owned = wardrobe.GetOwned(c.Id);
                if (owned == null || owned.IsExpired(nowUnix)) need++;
            }
            if (need == 0) return BuyResult.AlreadyOwned;    // 整套每一件都有了

            if (wardrobe.Count(ItemSlotType.Clothes) + need > wardrobe.ClothSlotCount) return BuyResult.NoRoom;
            if (!CanAfford(wardrobe.Wallet, outfit)) return BuyResult.NotEnoughMoney;

            wardrobe.Wallet.Spend(outfit.Currency, outfit.Price);   // 收整套的標價一次 (不是逐件加總)
            long expire = ComputeExpire(outfit.DurationDays, nowUnix);
            foreach (var c in components)
            {
                if (c == null) continue;
                var owned = wardrobe.GetOwned(c.Id);
                if (owned != null && !owned.IsExpired(nowUnix)) continue;
                wardrobe.AddOwned(new OwnedItem { ItemId = c.Id, Slot = c.SlotType, ExpireUnix = expire, Quantity = 1 });
            }
            return BuyResult.Ok;
        }

        /// <summary>Check the dress rules for <paramref name="item"/> without mutating (the _7002 check()):
        /// owned + not expired, meets min level, and gender-compatible.</summary>
        public static EquipResult CanEquip(Wardrobe wardrobe, ShopItem item, int playerLevel, ItemSex playerSex, long nowUnix)
        {
            if (wardrobe == null || item == null) return EquipResult.NotOwned;
            if (item.SlotType != ItemSlotType.Clothes || item.EquipSlot == EquipSlot.None) return EquipResult.NotWearable;

            var owned = wardrobe.GetOwned(item.Id);
            if (owned == null) return EquipResult.NotOwned;
            if (owned.IsExpired(nowUnix)) return EquipResult.Expired;
            if (item.MinLevel > playerLevel) return EquipResult.MinLevel;
            if (item.Sex != ItemSex.Both && playerSex != ItemSex.Both && item.Sex != playerSex) return EquipResult.WrongGender;
            return EquipResult.Ok;
        }

        /// <summary>Wear <paramref name="item"/> if the dress rules pass; returns the same code <see cref="CanEquip"/>
        /// would. On Ok the item occupies its <see cref="EquipSlot"/> (replacing whatever was there).</summary>
        public static EquipResult Equip(Wardrobe wardrobe, ShopItem item, int playerLevel, ItemSex playerSex, long nowUnix)
        {
            var r = CanEquip(wardrobe, item, playerLevel, playerSex, nowUnix);
            if (r == EquipResult.Ok) wardrobe.SetEquipped(item.EquipSlot, item.Id);
            return r;
        }

        /// <summary>Take off whatever is worn in <paramref name="slot"/>.</summary>
        public static void Unequip(Wardrobe wardrobe, EquipSlot slot) => wardrobe?.ClearEquipped(slot);
    }
}
