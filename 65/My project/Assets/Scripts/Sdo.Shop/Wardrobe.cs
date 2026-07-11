using System.Collections.Generic;

namespace Sdo.Shop
{
    /// <summary>The player's three separate money balances (mirrors Character.getPoints/getCoins/getBonus in the
    /// server emulator). One <see cref="ItemPriceCurrency"/> maps to exactly one balance.</summary>
    public sealed class Wallet
    {
        public int Points;
        public int Coins;
        public int Bonus;

        public int Get(ItemPriceCurrency c)
        {
            switch (c)
            {
                case ItemPriceCurrency.Coins: return Coins;
                case ItemPriceCurrency.Bonus: return Bonus;
                default: return Points;
            }
        }

        public void Add(ItemPriceCurrency c, int amount)
        {
            switch (c)
            {
                case ItemPriceCurrency.Coins: Coins += amount; break;
                case ItemPriceCurrency.Bonus: Bonus += amount; break;
                default: Points += amount; break;
            }
        }

        public void Spend(ItemPriceCurrency c, int amount) => Add(c, -amount);
    }

    /// <summary>An owned inventory entry (trimmed InventoryItem.java): which item, when it expires, and — for
    /// consumables — how many. Clothes have Quantity 1 and an ExpireUnix of -1 (permanent) or a future timestamp.</summary>
    public sealed class OwnedItem
    {
        public int ItemId;
        public ItemSlotType Slot;
        public long ExpireUnix;   // -1 = permanent; else a Unix-seconds expiry
        public int Quantity;      // consumables stack; clothes = 1

        public bool IsExpired(long nowUnix) => ExpireUnix >= 0 && nowUnix >= ExpireUnix;
    }

    /// <summary>
    /// The player's衣櫃 + 背包 + 錢包: what they own, what they currently wear, and their money. Pure state (no Unity);
    /// mutated only through <see cref="ShopService"/> so the buy/equip rules stay in one place. Slot capacities mirror
    /// the server's per-character cloth/item slot counts (default effectively unlimited for the single-player remake).
    /// </summary>
    public sealed class Wardrobe
    {
        public readonly Wallet Wallet = new Wallet();

        public int ClothSlotCount = 200;   // character.getClothSlotCount()
        public int ItemSlotCount = 200;    // character.getItemSlotCount()

        private readonly Dictionary<int, OwnedItem> _owned = new Dictionary<int, OwnedItem>();
        private readonly Dictionary<EquipSlot, int> _equipped = new Dictionary<EquipSlot, int>();

        public IReadOnlyDictionary<int, OwnedItem> Owned => _owned;
        public IReadOnlyDictionary<EquipSlot, int> Equipped => _equipped;

        public bool Owns(int itemId) => _owned.ContainsKey(itemId);
        public OwnedItem GetOwned(int itemId) => _owned.TryGetValue(itemId, out var o) ? o : null;
        public void AddOwned(OwnedItem item) { if (item != null) _owned[item.ItemId] = item; }
        public void RemoveOwned(int itemId) { _owned.Remove(itemId); }

        /// <summary>Count of owned entries in a bucket — used for the slot-capacity check when buying.</summary>
        public int Count(ItemSlotType slot)
        {
            int n = 0;
            foreach (var kv in _owned) if (kv.Value.Slot == slot) n++;
            return n;
        }

        /// <summary>Item id worn in <paramref name="slot"/>, or 0 (NOT_EQUIPPED_SLOT_ID) if nothing.</summary>
        public int EquippedId(EquipSlot slot) => _equipped.TryGetValue(slot, out var id) ? id : 0;
        public bool IsEquipped(int itemId)
        {
            foreach (var kv in _equipped) if (kv.Value == itemId) return true;
            return false;
        }
        public void SetEquipped(EquipSlot slot, int itemId) { if (slot != EquipSlot.None) _equipped[slot] = itemId; }
        public void ClearEquipped(EquipSlot slot) { _equipped.Remove(slot); }
    }
}
